// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Content.Server._Harvest.AdminSquad;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server.NPC.Components;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Climbing.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Log;
using Content.Server.NPC.Pathfinding;
using Content.Shared.NPC;
using Robust.Shared.Utility;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Behavior tree action that moves the android toward a target coordinate.
/// Wraps the existing NPC steering and pathfinding systems.
/// </summary>
public sealed class MoveToAction : IBehaviorNode
{
    private readonly Func<EntityUid, AndroidBlackboard, EntityCoordinates?> _getTarget;
    private readonly float _range;
    private readonly Func<EntityUid, AndroidBlackboard, float>? _getRange;
    private readonly Action<EntityUid, AndroidBlackboard>? _onFailure;

    // Climbing tables is a last resort. We first try a path without climbing and only allow it
    // after the pathfinder reports NoPath. The tree is shared by every android of a job, so this state is
    // kept per android and loaded at the start of each tick.
    // Closest a move has to get to the tile standing in for its target.
    private const float MinResolvedRange = 0.4f;

    private sealed class MoveState
    {
        public EntityCoordinates? LastResolvedTarget;
        public bool ClimbAllowed;

        // Progress watchdog: the closest the android got to the current target, and when.
        public EntityCoordinates? WatchedTarget;
        public float BestDistance;
        public TimeSpan BestAt;
    }

    /// <summary>
    /// A move that has not brought the android half a tile closer for this long has failed: it is walking in
    /// circles around something it cannot reach (an item under a machine, a customer behind a counter).
    /// </summary>
    private static readonly TimeSpan StuckTime = TimeSpan.FromSeconds(8);

    private const float DoorAheadRange = 1.3f;

    /// <summary>
    /// How long a tile given up on (NoPath) stays refused: see <see cref="AndroidComponent.UnreachableTiles"/>.
    /// </summary>
    private static readonly TimeSpan UnreachableMemory = TimeSpan.FromSeconds(60);

    private readonly Dictionary<EntityUid, MoveState> _states = new();
    private MoveState _state = new();

    /// <summary>
    /// Optional override for how long the action keeps re-requesting a path after NoPath.
    /// </summary>
    [ViewVariables]
    public float? NoPathRetryWindow { get; set; }

    /// <summary>
    /// Whether a target given up on (NoPath) is refused for <see cref="UnreachableMemory"/>: for routine moves that
    /// pick the same unreachable spot again and again (patrol beacons, messes in locked rooms). Off for urgent moves
    /// (patients, orders, fleeing), where one failed search must not stop the android from trying again.
    /// </summary>
    public bool RemembersUnreachable { get; init; }

    /// <summary>
    /// Creates a move action with a target provider and arrival range.
    /// </summary>
    /// <param name="getTarget">Function that returns the target coordinates each tick.</param>
    /// <param name="range">How close the android must get to the target to succeed.</param>
    /// <param name="onFailure">Optional callback invoked when the move fails so the caller can clear stale state.</param>
    public MoveToAction(Func<EntityUid, AndroidBlackboard, EntityCoordinates?> getTarget, float range = 0.2f, Action<EntityUid, AndroidBlackboard>? onFailure = null)
    {
        _getTarget = getTarget;
        _range = range;
        _onFailure = onFailure;
    }

    /// <summary>
    /// Creates a move action with a target provider and dynamic arrival range.
    /// </summary>
    /// <param name="getTarget">Function that returns the target coordinates each tick.</param>
    /// <param name="getRange">Function that returns the desired arrival range each tick.</param>
    /// <param name="onFailure">Optional callback invoked when the move fails so the caller can clear stale state.</param>
    public MoveToAction(Func<EntityUid, AndroidBlackboard, EntityCoordinates?> getTarget, Func<EntityUid, AndroidBlackboard, float> getRange, Action<EntityUid, AndroidBlackboard>? onFailure = null)
    {
        _getTarget = getTarget;
        _range = 0.2f;
        _getRange = getRange;
        _onFailure = onFailure;
    }

    private const float DirectMoveFallbackRange = 5f;
    private const int ReachabilitySearchLimit = 512;
    private const float CacheTargetMoveThreshold = 0.75f;

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        _state = StateFor(owner, blackboard);

        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        var rawTarget = _getTarget(owner, blackboard);
        if (rawTarget == null || !rawTarget.Value.IsValid(blackboard.EntityManager))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return BehaviorStatus.Failure;

        var range = _getRange?.Invoke(owner, blackboard) ?? _range;
        var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);
        var sawmill = blackboard.Log;

        // Fast path: if already close enough to the raw target, no pathfinding work is needed.
        var rawMap = blackboard.Transform.ToMapCoordinates(rawTarget.Value);
        if (rawMap != MapCoordinates.Nullspace)
        {
            var rawDistance = (rawMap.Position - ownerPos).Length();
            if (rawDistance <= range)
            {
                if (blackboard.EntityManager.TryGetComponent<NPCSteeringComponent>(owner, out var steer))
                    blackboard.Steering.Unregister(owner, steer);

                sawmill.Debug($"[MoveTo] already in range of raw target (distance {rawDistance:F2})");
                _state.ClimbAllowed = false;
                ResetNoPathCounters(owner, blackboard);
                return BehaviorStatus.Success;
            }
        }

        // Resolve to a walkable tile, with caching to avoid recomputing the same BFS every tick.
        var target = ResolveWalkableTarget(owner, rawTarget.Value, ownerXform, blackboard);
        if (target == null || !target.Value.IsValid(blackboard.EntityManager))
        {
            sawmill.Debug($"[MoveTo] no walkable tile near raw target {rawTarget.Value} ({rawMap.Position}) for {owner}");
            FailAndCleanup(owner, blackboard);
            return BehaviorStatus.Failure;
        }

        var resolvedTarget = target.Value;
        if (RemembersUnreachable && IsUnreachable(owner, resolvedTarget, blackboard))
        {
            sawmill.Debug($"[MoveTo] {resolvedTarget} was unreachable a moment ago; failing at once");
            FailAndCleanup(owner, blackboard);
            return BehaviorStatus.Failure;
        }

        var steering = blackboard.EntityManager.GetComponentOrNull<NPCSteeringComponent>(owner);

        // A new target resets the "try climbing" fallback so we always attempt to go around first.
        if (resolvedTarget != _state.LastResolvedTarget)
        {
            _state.LastResolvedTarget = resolvedTarget;
            _state.ClimbAllowed = false;
            ResetNoPathCounters(owner, blackboard);
            sawmill.Debug($"[MoveTo] new resolved target {resolvedTarget}; climbing fallback reset");
        }

        sawmill.Debug($"[MoveTo] rawTarget={rawTarget.Value} resolvedTarget={resolvedTarget} range={range:F2}");

        // If we are already in range of the resolved tile, succeed immediately.
        var resolvedMap = blackboard.Transform.ToMapCoordinates(resolvedTarget);
        if (resolvedMap != MapCoordinates.Nullspace)
        {
            // The tile stands in for the real target (money on a table): get as much closer to it as the tile is
            // farther, so arriving there also means being in range of the target.
            if (rawMap != MapCoordinates.Nullspace && rawMap.MapId == resolvedMap.MapId)
                range = Math.Max(MinResolvedRange, range - (rawMap.Position - resolvedMap.Position).Length());

            var resolvedDistance = (resolvedMap.Position - ownerPos).Length();
            if (resolvedDistance <= range)
            {
                if (steering != null)
                    blackboard.Steering.Unregister(owner, steering);

                sawmill.Debug($"[MoveTo] in range of resolved target (distance {resolvedDistance:F2})");
                _state.ClimbAllowed = false;
                ResetNoPathCounters(owner, blackboard);
                return BehaviorStatus.Success;
            }
        }

        // If we are not yet steering to the right coordinates, register. The status still describes the previous
        // target (InRange of the patient just picked up, say) until steering updates: never trust it this tick.
        if (steering == null ||
            !blackboard.EntityManager.EntityExists(steering.Coordinates.EntityId) ||
            ShouldReRegister(steering.Coordinates, resolvedTarget))
        {
            var previous = steering != null && blackboard.EntityManager.EntityExists(steering.Coordinates.EntityId)
                ? blackboard.Transform.ToMapCoordinates(steering.Coordinates)
                : MapCoordinates.Nullspace;
            steering = blackboard.Steering.Register(owner, resolvedTarget, steering);

            // Same place written relative to another entity (an item on a counter) is not a new target.
            var next = blackboard.Transform.ToMapCoordinates(resolvedTarget);
            if (previous.MapId != next.MapId || (previous.Position - next.Position).Length() > 1f)
                steering.Status = SteeringStatus.Moving;
        }

        // Keep the steering target up to date for moving targets without cancelling pathfinding every tick.
        if (steering != null)
        {
            if (!steering.Coordinates.Equals(resolvedTarget))
                steering.Coordinates = resolvedTarget;

            // Androids can open normal doors and airlocks they have access to.
            steering.Flags |= PathFlags.Interact | PathFlags.Access;
            if (blackboard.System<AdminSquadSystem>().IsOrdered(owner))
                steering.Flags |= PathFlags.Smashing; // Harvest - under admin orders, break what blocks the way

            // Climbing tables and railings is only used as a last resort when no walkable
            // path is found. The first attempt is always made without the climbing flag.
            if (_state.ClimbAllowed && CanClimb(owner, blackboard))
            {
                steering.Flags |= PathFlags.Climbing;
            }
            else
            {
                steering.Flags &= ~PathFlags.Climbing;
            }
        }

        if (steering == null)
            return BehaviorStatus.Failure;

        steering.Range = range;

        TryOpenDoorsOnPath(owner, blackboard, steering);

        // Building the diagnostics string every tick is wasteful when debug logging is off.
        var diagnostics = sawmill.IsLogLevelEnabled(LogLevel.Debug)
            ? GetMovementDiagnostics(owner, blackboard, steering)
            : (distance: 0f, diag: string.Empty);
        sawmill.Debug($"[MoveTo] steering.Status={steering.Status} target={steering.Coordinates} distance={diagnostics.distance:F2} pathCount={steering.CurrentPath.Count} directMove={steering.DirectMove} flags={steering.Flags}");

        if (!IsPathAccessible(owner, steering, blackboard))
        {
            sawmill.Debug($"[MoveTo] path contains an inaccessible door for {owner}");
            blackboard.Steering.Unregister(owner, steering);
            FailAndCleanup(owner, blackboard);
            return BehaviorStatus.Failure;
        }

        switch (steering.Status)
        {
            case SteeringStatus.InRange:
                sawmill.Debug($"[MoveTo] in range of {steering.Coordinates} (distance {diagnostics.distance:F2})");
                ResetNoPathCounters(owner, blackboard);
                return BehaviorStatus.Success;

            case SteeringStatus.NoPath:
                sawmill.Debug($"[MoveTo] NoPath to {steering.Coordinates} for {owner}; {diagnostics.diag}");
                return HandleNoPath(owner, blackboard, steering, resolvedTarget);

            case SteeringStatus.Moving:
                sawmill.Debug($"[MoveTo] Moving toward {steering.Coordinates} (distance {diagnostics.distance:F2})");
                UpdatePathProgress(owner, blackboard, ownerPos);
                if (IsStuck(blackboard, steering, resolvedTarget, ownerPos))
                {
                    sawmill.Debug($"[MoveTo] no progress towards {resolvedTarget} for {StuckTime.TotalSeconds} s; giving up");
                    _state.WatchedTarget = null;
                    blackboard.Steering.Unregister(owner, steering);
                    FailAndCleanup(owner, blackboard);
                    return BehaviorStatus.Failure;
                }

                return BehaviorStatus.Running;

            default:
                sawmill.Debug($"[MoveTo] Unknown steering status {steering.Status} for {owner}; {diagnostics.diag}");
                blackboard.Steering.Unregister(owner, steering);
                FailAndCleanup(owner, blackboard);
                return BehaviorStatus.Failure;
        }
    }

    private void FailAndCleanup(EntityUid owner, AndroidBlackboard blackboard)
    {
        _onFailure?.Invoke(owner, blackboard);
    }

    private void ReRegisterSteering(EntityUid owner, AndroidBlackboard blackboard, NPCSteeringComponent oldSteering, EntityCoordinates resolvedTarget)
    {
        blackboard.Steering.Unregister(owner, oldSteering);
        var steering = blackboard.Steering.Register(owner, resolvedTarget);
        if (steering == null)
            return;

        // Androids can open normal doors and airlocks they have access to.
        steering.Flags |= PathFlags.Interact | PathFlags.Access;
        if (blackboard.System<AdminSquadSystem>().IsOrdered(owner))
            steering.Flags |= PathFlags.Smashing; // Harvest - under admin orders, break what blocks the way

        // Climbing is only allowed as a last resort; set it here so the next path request uses it.
        if (_state.ClimbAllowed && CanClimb(owner, blackboard))
        {
            steering.Flags |= PathFlags.Climbing;
        }
        else
        {
            steering.Flags &= ~PathFlags.Climbing;
        }
    }

    private BehaviorStatus HandleNoPath(EntityUid owner, AndroidBlackboard blackboard, NPCSteeringComponent steering, EntityCoordinates resolvedTarget)
    {
        var sawmill = blackboard.Log;
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
        {
            blackboard.Steering.Unregister(owner, steering);
            FailAndCleanup(owner, blackboard);
            return BehaviorStatus.Failure;
        }

        // If a path request is still in flight, wait for it to complete. Re-registering now would
        // cancel the in-progress request and make it even harder for the navmesh to catch up.
        if (steering.Pathfind)
        {
            sawmill.Debug($"[MoveTo] NoPath but path request still in flight for {resolvedTarget}; waiting");
            return BehaviorStatus.Running;
        }

        var now = blackboard.Timing.CurTime;
        var config = blackboard.Config;
        var retrySeconds = NoPathRetryWindow ?? (config.NoPathRetryWindowSeconds);
        var window = TimeSpan.FromSeconds(retrySeconds);
        var reregisterInterval = TimeSpan.FromSeconds(Math.Max(0.5f, retrySeconds / 3f));

        if (inventory.ConsecutiveNoPathCount == 0)
        {
            inventory.NoPathStartTime = now;
            inventory.NoPathLastReregisterTime = now;
            inventory.ConsecutiveNoPathCount = 1;

            // First NoPath: allow climbing as a last resort and re-request the path.
            if (!_state.ClimbAllowed)
            {
                _state.ClimbAllowed = true;
                sawmill.Debug($"[MoveTo] first NoPath to {resolvedTarget}; enabling climbing as last resort");
            }

            ReRegisterSteering(owner, blackboard, steering, resolvedTarget);
            return BehaviorStatus.Running;
        }

        inventory.ConsecutiveNoPathCount++;

        if (now - inventory.NoPathStartTime < window)
        {
            if (now - inventory.NoPathLastReregisterTime >= reregisterInterval)
            {
                sawmill.Debug($"[MoveTo] NoPath retry #{inventory.ConsecutiveNoPathCount}, re-registering for {resolvedTarget}");
                inventory.NoPathLastReregisterTime = now;
                ReRegisterSteering(owner, blackboard, steering, resolvedTarget);
            }
            else
            {
                sawmill.Debug($"[MoveTo] NoPath retry #{inventory.ConsecutiveNoPathCount}, waiting");
            }
            return BehaviorStatus.Running;
        }

        sawmill.Info($"[MoveTo] {blackboard.EntityManager.ToPrettyString(owner)}: NoPath persisted for {window.TotalSeconds:F1}s after {inventory.ConsecutiveNoPathCount} ticks, giving up on {resolvedTarget}");
        ResetNoPathCounters(owner, blackboard);
        if (RemembersUnreachable)
            RememberUnreachable(owner, resolvedTarget, blackboard);
        blackboard.Steering.Unregister(owner, steering);
        FailAndCleanup(owner, blackboard);
        _state.ClimbAllowed = false;
        return BehaviorStatus.Failure;
    }

    private static bool IsUnreachable(EntityUid owner, EntityCoordinates target, AndroidBlackboard blackboard)
    {
        return blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) &&
               android.UnreachableTiles.TryGetValue((target.EntityId, target.Position.Floored()), out var since) &&
               blackboard.Timing.CurTime - since < UnreachableMemory;
    }

    private static void RememberUnreachable(EntityUid owner, EntityCoordinates target, AndroidBlackboard blackboard)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return;

        var now = blackboard.Timing.CurTime;
        foreach (var expired in android.UnreachableTiles.Where(t => now - t.Value >= UnreachableMemory).Select(t => t.Key).ToList())
            android.UnreachableTiles.Remove(expired);

        android.UnreachableTiles[(target.EntityId, target.Position.Floored())] = now;
    }

    private static void ResetNoPathCounters(EntityUid owner, AndroidBlackboard blackboard)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return;

        inventory.ConsecutiveNoPathCount = 0;
        inventory.NoPathStartTime = TimeSpan.Zero;
        inventory.NoPathLastReregisterTime = TimeSpan.Zero;
    }

    private bool IsStuck(AndroidBlackboard blackboard, NPCSteeringComponent steering, EntityCoordinates target, Vector2 ownerPos)
    {
        var now = blackboard.Timing.CurTime;
        var targetPos = blackboard.Transform.ToMapCoordinates(target).Position;
        var distance = (targetPos - ownerPos).Length();

        // A new target, a target that moved away (someone being followed), or real progress restarts the clock,
        // and so does waiting for a path: a long one across the station takes several ticks to compute, during
        // which the android stands still without being stuck.
        if (steering.Pathfind ||
            _state.WatchedTarget is not { } watched ||
            (blackboard.Transform.ToMapCoordinates(watched).Position - targetPos).Length() > 1f ||
            distance < _state.BestDistance - 0.5f)
        {
            _state.WatchedTarget = target;
            _state.BestDistance = distance;
            _state.BestAt = now;
            return false;
        }

        return now - _state.BestAt > StuckTime;
    }

    private static void UpdatePathProgress(EntityUid owner, AndroidBlackboard blackboard, Vector2 ownerPos)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return;

        if ((ownerPos - inventory.LastPathProgressPosition).Length() > 0.1f)
        {
            inventory.LastPathProgressPosition = ownerPos;
            inventory.LastPathProgressTime = blackboard.Timing.CurTime;
        }
    }


    /// <summary>
    /// Converts the target to the android's current grid. Pathfinding needs grid-relative
    /// coordinates; if the target is parented to the map or to a different grid, the same world
    /// position is re-expressed relative to the android's grid.
    /// </summary>
    private static EntityCoordinates? ToGridCoordinates(EntityCoordinates target, TransformComponent ownerXform, SharedTransformSystem transform)
    {
        if (ownerXform.GridUid is not { Valid: true } gridUid)
            return null;

        var targetMap = transform.ToMapCoordinates(target);
        if (targetMap == MapCoordinates.Nullspace)
            return null;

        return transform.ToCoordinates(new Entity<TransformComponent?>(gridUid, null), targetMap);
    }

    /// <summary>
    /// Snaps the raw target to the nearest tile the android can actually occupy. If the raw tile
    /// is inside a table, machine or wall, this finds an adjacent free tile. If the navmesh for the
    /// area is not ready yet, it returns the converted grid coordinate unchanged.
    /// A short-lived cache is used to avoid recomputing the same tile each tick.
    /// </summary>
    private static EntityCoordinates? ResolveWalkableTarget(EntityUid owner, EntityCoordinates rawTarget, TransformComponent ownerXform, AndroidBlackboard blackboard, float searchRadius = 2f)
    {
        if (ownerXform.GridUid is not { Valid: true } gridUid)
            return null;

        var now = blackboard.Timing.CurTime;
        var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);
        var pathfinding = blackboard.EntityManager.System<PathfindingSystem>();
        var physics = blackboard.EntityManager.System<SharedPhysicsSystem>();
        var transform = blackboard.Transform;

        // Check the generic resolved-target cache before doing any work.
        if (blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
        {
            const float cacheMaxAgeSeconds = 0.5f;
            const float cacheOwnerMoveThreshold = 0.5f;

            // A target that only moved a little (a fleeing mouse, a walking issuer) reuses the cached tile.
            if (inventory.ResolvedTargetRaw is { } cachedRaw &&
                inventory.ResolvedTargetCoords is { } cachedResolved &&
                cachedRaw.EntityId == rawTarget.EntityId &&
                (cachedRaw.Position - rawTarget.Position).LengthSquared() <= CacheTargetMoveThreshold * CacheTargetMoveThreshold &&
                (now - inventory.ResolvedTargetTime).TotalSeconds <= cacheMaxAgeSeconds &&
                (ownerPos - inventory.ResolvedTargetOwnerPos).Length() <= cacheOwnerMoveThreshold &&
                cachedResolved.IsValid(blackboard.EntityManager))
            {
                blackboard.Log.Debug($"[MoveTo] using cached walkable target {cachedResolved} for {rawTarget}");
                return cachedResolved;
            }
        }

        var gridTarget = ToGridCoordinates(rawTarget, ownerXform, transform) ?? rawTarget;

        // If we cannot determine a grid-relative target there is nothing to resolve.
        if (!gridTarget.IsValid(blackboard.EntityManager))
            return null;

        var ownerCollision = physics.GetHardCollision(owner);

        // The navmesh might not be built yet; in that case GetPoly returns null for every point.
        // Check the android's own tile. If it is also null, fall back to the converted target and
        // let the pathfinder decide later rather than permanently blacklisting the target.
        var ownerGrid = transform.ToCoordinates(new Entity<TransformComponent?>(gridUid, null), transform.GetMapCoordinates(ownerXform));
        var ownerPoly = pathfinding.GetPoly(ownerGrid);

        var exactPoly = pathfinding.GetPoly(gridTarget);

        EntityCoordinates? result = null;

        // If the exact tile is walkable and connected to where the android is, use it.
        if (IsWalkable(exactPoly, ownerCollision) && IsReachable(ownerPoly, exactPoly))
        {
            result = gridTarget;
        }
        else if (ownerPoly == null)
        {
            result = gridTarget;
        }
        else
        {
            // Search outward in rings for the nearest free tile.
            for (var r = 1; r <= (int) searchRadius; r++)
            {
                for (var x = -r; x <= r; x++)
                {
                    for (var y = -r; y <= r; y++)
                    {
                        if (Math.Abs(x) != r && Math.Abs(y) != r)
                            continue;

                        var candidate = gridTarget.Offset(new Vector2(x, y));

                        if (!candidate.IsValid(blackboard.EntityManager))
                            continue;

                        var poly = pathfinding.GetPoly(candidate);
                        if (IsWalkable(poly, ownerCollision) && IsReachable(ownerPoly, poly))
                        {
                            result = candidate;
                            goto Found;
                        }
                    }
                }
            }
        }

    Found:

        if (result is { } resolved && inventory != null)
        {
            inventory.ResolvedTargetRaw = rawTarget;
            inventory.ResolvedTargetCoords = resolved;
            inventory.ResolvedTargetOwnerPos = ownerPos;
            inventory.ResolvedTargetTime = now;
        }

        return result;
    }

    /// <summary>
    /// Returns true if the given polygon is a tile the android can actually stand on.
    /// This mirrors the pathfinder's own collision check: a tile is blocked if the android's
    /// collision layer/mask overlaps with any fixture occupying that point.
    /// </summary>
    private static bool IsWalkable(PathPoly? poly, (int Layer, int Mask) ownerCollision)
    {
        if (poly == null)
            return false;

        // Space or an invalid polygon is not walkable.
        if ((poly.Data.Flags & (PathfindingBreadcrumbFlag.Space | PathfindingBreadcrumbFlag.Invalid)) != 0)
            return false;

        // If the android's hard fixtures would overlap any fixture on the tile, the tile is blocked.
        if ((ownerCollision.Layer & poly.Data.CollisionMask) != 0)
            return false;

        if ((ownerCollision.Mask & poly.Data.CollisionLayer) != 0)
            return false;

        return true;
    }

    /// <summary>
    /// Checks that the target polygon is reachable from the starting polygon through the navmesh.
    /// This stops the android from selecting a tile that is on the other side of a wall because it
    /// happens to be the nearest free cell.
    /// </summary>
    /// <summary>
    /// Whether two navmesh polygons are connected. The search is bounded: past <see cref="ReachabilitySearchLimit"/>
    /// polygons it gives up and assumes reachable, leaving the final word to the pathfinder. An unbounded search
    /// walks the whole station for every unreachable target, which stalled the server.
    /// </summary>
    private static bool IsReachable(PathPoly? from, PathPoly? to)
    {
        if (to == null)
            return false;

        // If the navmesh for either cell is not ready yet, assume reachable and let the pathfinder
        // validate the move later.
        if (from == null)
            return true;

        if (from == to)
            return true;

        var frontier = new Queue<PathPoly>();
        var visited = new HashSet<PathPoly>();
        frontier.Enqueue(from);

        while (frontier.TryDequeue(out var current))
        {
            if (current == to)
                return true;

            if (!visited.Add(current))
                continue;

            if (visited.Count > ReachabilitySearchLimit)
                return true;

            foreach (var neighbor in current.Neighbors)
            {
                if (neighbor == to)
                    return true;

                if (!visited.Contains(neighbor))
                    frontier.Enqueue(neighbor);
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether every door node on the current path can actually be opened by this android.
    /// Bolted doors, blast doors with no interaction, and doors lacking the required access are treated
    /// as impassable so the android stops ramming them and the tree can fall back.
    /// </summary>
    /// <summary>
    /// Climbing over tables is a last resort, and impossible while dragging someone.
    /// </summary>
    private static bool CanClimb(EntityUid owner, AndroidBlackboard blackboard)
    {
        return blackboard.EntityManager.TryGetComponent<ClimbingComponent>(owner, out var climbing) && climbing.CanClimb &&
               !(blackboard.EntityManager.TryGetComponent<PullerComponent>(owner, out var puller) && puller.Pulling != null);
    }

    private static bool IsPathAccessible(EntityUid owner, NPCSteeringComponent steering, AndroidBlackboard blackboard)
    {
        var access = blackboard.EntityManager.System<AccessReaderSystem>();
        var doors = blackboard.EntityManager.System<SharedDoorSystem>();
        var mapSys = blackboard.EntityManager.System<SharedMapSystem>();

        foreach (var poly in steering.CurrentPath)
        {
            if ((poly.Data.Flags & PathfindingBreadcrumbFlag.Door) == 0)
                continue;

            var doorEnt = FindDoorInPoly(poly, blackboard, mapSys);
            if (!doorEnt.HasValue)
                continue;

            var door = doorEnt.Value;

            if (!blackboard.EntityManager.TryGetComponent<DoorComponent>(door, out var doorComp))
                continue;

            // If the door is already open we do not care.
            if (doorComp.State is DoorState.Open or DoorState.Opening)
                continue;

            // Blast doors and similar non-interactive shutters cannot be opened at all.
            if (!doorComp.BumpOpen && !doorComp.ClickOpen)
                return false;

            // Bolted doors cannot be opened by anyone.
            if (doors.IsBolted(door))
                return false;

            // Doors with an access reader must accept one of the android's access tags.
            if (blackboard.EntityManager.HasComponent<AccessReaderComponent>(door) &&
                !access.IsAllowed(owner, door))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Opens closed doors the android is about to walk through, like a player clicking them: doors on the next
    /// steps of the path, and a door right ahead of it (a body being dragged can keep it from bumping the door open).
    /// A door that merely stands next to the android (seated at a bar next to an airlock) is left alone.
    /// </summary>
    private static void TryOpenDoorsOnPath(EntityUid owner, AndroidBlackboard blackboard, NPCSteeringComponent steering)
    {
        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return;

        var doors = blackboard.EntityManager.System<SharedDoorSystem>();
        var access = blackboard.EntityManager.System<AccessReaderSystem>();
        var mapSys = blackboard.EntityManager.System<SharedMapSystem>();
        var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);

        var candidates = steering.CurrentPath.Take(3)
            .Select(node => FindDoorInPoly(node, blackboard, mapSys))
            .OfType<EntityUid>()
            .ToHashSet();

        var waypoint = steering.CurrentPath.TryPeek(out var next) ? next.Coordinates : steering.Coordinates;
        var heading = blackboard.Transform.ToMapCoordinates(waypoint).Position - ownerPos;
        if (heading.LengthSquared() > 0.01f)
        {
            foreach (var (door, _) in blackboard.EntityManager.System<EntityLookupSystem>()
                         .GetEntitiesInRange<DoorComponent>(ownerXform.Coordinates, DoorAheadRange, LookupFlags.Static))
            {
                var toDoor = blackboard.Transform.GetWorldPosition(door) - ownerPos;
                if (Vector2.Dot(Vector2.Normalize(toDoor), Vector2.Normalize(heading)) > 0.5f)
                    candidates.Add(door);
            }
        }

        foreach (var door in candidates)
        {
            if (!blackboard.EntityManager.TryGetComponent<DoorComponent>(door, out var doorComp) ||
                doorComp.State is DoorState.Open or DoorState.Opening ||
                !doorComp.BumpOpen && !doorComp.ClickOpen ||
                doors.IsBolted(door) ||
                (blackboard.Transform.GetWorldPosition(door) - ownerPos).Length() > 1.5f ||
                blackboard.EntityManager.HasComponent<AccessReaderComponent>(door) && !access.IsAllowed(owner, door))
            {
                continue;
            }

            doors.TryOpen(door, doorComp, owner);
        }
    }

    private static EntityUid? FindDoorInPoly(PathPoly poly, AndroidBlackboard blackboard, SharedMapSystem mapSys)
    {
        if (!blackboard.EntityManager.TryGetComponent<MapGridComponent>(poly.GraphUid, out var grid))
            return null;

        foreach (var ent in mapSys.GetLocalAnchoredEntities(poly.GraphUid, grid, poly.Box))
        {
            if (blackboard.EntityManager.HasComponent<DoorComponent>(ent))
                return ent;
        }

        return null;
    }

    private static (float distance, string diag) GetMovementDiagnostics(EntityUid owner, AndroidBlackboard blackboard, NPCSteeringComponent steering)
    {
        var distance = 0f;
        var diag = new System.Text.StringBuilder();

        if (blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
        {
            var targetMap = blackboard.Transform.ToMapCoordinates(steering.Coordinates);
            if (targetMap != MapCoordinates.Nullspace)
            {
                var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);
                distance = (targetMap.Position - ownerPos).Length();
            }
        }

        if (blackboard.EntityManager.TryGetComponent<InputMoverComponent>(owner, out var mover))
            diag.Append($"CanMove={mover.CanMove} ");
        else
            diag.Append("NoInputMover ");

        if (blackboard.EntityManager.TryGetComponent<PhysicsComponent>(owner, out var physics))
            diag.Append($"BodyType={physics.BodyType} BodyStatus={physics.BodyStatus} ");

        if (blackboard.EntityManager.TryGetComponent<MobStateComponent>(owner, out var mobState))
            diag.Append($"MobState={mobState.CurrentState} ");

        if (blackboard.EntityManager.TryGetComponent<MovementSpeedModifierComponent>(owner, out var speed))
            diag.Append($"WalkSpeed={speed.CurrentWalkSpeed:F2} SprintSpeed={speed.CurrentSprintSpeed:F2}");

        return (distance, diag.ToString());
    }

    /// <summary>
    /// Returns true if the target has moved enough from the currently registered steering
    /// coordinates that we should clear the old path and request a new one.
    /// </summary>
    private static bool ShouldReRegister(EntityCoordinates current, EntityCoordinates target)
    {
        if (!current.EntityId.IsValid() || current.EntityId != target.EntityId)
            return true;

        var delta = current.Position - target.Position;
        return delta.LengthSquared() > 1.0f * 1.0f;
    }

    private MoveState StateFor(EntityUid owner, AndroidBlackboard blackboard)
    {
        if (_states.TryGetValue(owner, out var state))
            return state;

        if (_states.Count > 64)
        {
            foreach (var gone in _states.Keys.Where(blackboard.EntityManager.Deleted).ToList())
                _states.Remove(gone);
        }

        return _states[owner] = new MoveState();
    }
}
