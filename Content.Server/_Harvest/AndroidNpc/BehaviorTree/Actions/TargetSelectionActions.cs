// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.Footprints;
using Content.Shared.FixedPoint;
using Content.Goobstation.Shared.FloorCleaner;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Shared.Fluids.Components;
using Content.Shared.Fluids;
using Content.Shared.Item;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map.Components;
using Robust.Shared.Map;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Selects the nearest known dirty target and stores it in the inventory component.
/// </summary>
public sealed class SelectDirtyTargetAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        // Only consider work the android currently has a working tool for, otherwise it walks up to a
        // puddle with a spent mop, fails, and immediately reselects the same target.
        var canMop = inventorySystem.TryGetMop(owner, inventory, out var mop) && inventorySystem.CanMopClean(mop);
        var canBagTrash = !inventorySystem.TrashBagNeedsEmptying(owner, inventory);
        var canLightReplace = inventorySystem.FindAndRegisterOwnedTool(owner, inventory, "LightReplacer").HasValue;
        var canScrub = inventory.OwnedItems.Any(e =>
            !blackboard.EntityManager.Deleted(e) &&
            (blackboard.EntityManager.HasComponent<FloorCleanerComponent>(e) ||
             blackboard.EntityManager.HasComponent<SprayComponent>(e)));

        blackboard.Log.Debug($"[SelectDirtyTarget] canMop={canMop} canBagTrash={canBagTrash} canLightReplace={canLightReplace} canScrub={canScrub} KnownDirtyTargets={memory.KnownDirtyTargets.Count}");

        // A tool do-after is in progress for the current target; keep it until the do-after finishes.
        // This prevents EquipItemAction from switching tools and cancelling the do-after.
        if (inventory.CurrentToolTarget is { } currentTarget &&
            !blackboard.EntityManager.Deleted(currentTarget) &&
            inventorySystem.IsToolDoAfterRunning(owner, inventory, out _))
        {
            blackboard.Log.Debug($"[SelectDirtyTarget] keeping target {currentTarget} while tool do-after is running");
            return BehaviorStatus.Success;
        }

        // Already have a valid floor target.
        if (inventory.CurrentToolTarget.HasValue &&
            !blackboard.EntityManager.Deleted(inventory.CurrentToolTarget.Value) &&
            IsValidFloorTarget(inventory.CurrentToolTarget.Value, blackboard) &&
            IsStillDirtyTarget(inventory.CurrentToolTarget.Value, blackboard) &&
            HasWorkingToolFor(inventory.CurrentToolTarget.Value, blackboard, canMop, canBagTrash, canLightReplace, canScrub))
        {
            blackboard.Log.Debug($"[SelectDirtyTarget] Already have target {inventory.CurrentToolTarget.Value}");
            return BehaviorStatus.Success;
        }

        if (inventory.CurrentToolTarget is { } stale)
        {
            var why = blackboard.EntityManager.Deleted(stale)
                ? "deleted"
                : !IsValidFloorTarget(stale, blackboard)
                    ? $"invalidfloor parent={blackboard.EntityManager.GetComponent<TransformComponent>(stale).ParentUid} grid={blackboard.EntityManager.GetComponent<TransformComponent>(stale).GridUid}"
                    : !IsStillDirtyTarget(stale, blackboard)
                        ? "clean"
                        : "notool";
            blackboard.Log.Debug($"[SelectDirtyTarget] Clearing stale target {stale} because {why}");
            blackboard.Claims.ReleaseClaim(stale);
        }

        inventory.CurrentToolTarget = null;

        if (!canMop && !canBagTrash && !canLightReplace && !canScrub)
            return BehaviorStatus.Failure;

        // Snapshot the list so we can pick a candidate without dropping dirty work just because
        // the current tool is spent. We do prune targets that are no longer valid floor targets
        // (deleted, picked up, or already clean puddles) so the list stays honest.
        var candidates = new List<EntityUid>(memory.KnownDirtyTargets);

        // Prefer targets that are farther ahead in the android's current patrol direction. This
        // nudges janitors away from repeatedly orbiting the same few local tiles and makes them
        // cross into other areas of the station.
        if (blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
        {
            var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);
            var patrolDir = memory.PatrolDirection;
            const float DirectionNudge = 12f;

            candidates.Sort((a, b) =>
            {
                var posA = blackboard.EntityManager.TryGetComponent<TransformComponent>(a, out var xformA)
                    ? blackboard.Transform.GetWorldPosition(xformA)
                    : Vector2.Zero;
                var posB = blackboard.EntityManager.TryGetComponent<TransformComponent>(b, out var xformB)
                    ? blackboard.Transform.GetWorldPosition(xformB)
                    : Vector2.Zero;

                var distA = (posA - ownerPos).Length();
                var distB = (posB - ownerPos).Length();

                if (patrolDir != Vector2.Zero)
                {
                    var dirA = distA > 0 ? Vector2.Normalize(posA - ownerPos) : Vector2.Zero;
                    var dirB = distB > 0 ? Vector2.Normalize(posB - ownerPos) : Vector2.Zero;
                    distA -= Vector2.Dot(dirA, patrolDir) * DirectionNudge;
                    distB -= Vector2.Dot(dirB, patrolDir) * DirectionNudge;
                }

                return distA.CompareTo(distB);
            });
        }

        var toRemove = new List<EntityUid>();
        var selected = (EntityUid?)null;

        foreach (var candidate in candidates)
        {
            if (blackboard.EntityManager.Deleted(candidate))
            {
                toRemove.Add(candidate);
                continue;
            }

            if (!IsValidFloorTarget(candidate, blackboard) || !IsStillDirtyTarget(candidate, blackboard))
            {
                toRemove.Add(candidate);
                continue;
            }

            // Broken lights are handled by their own dedicated branch so they can be prioritised
            // above puddles and trash.
            if (blackboard.EntityManager.HasComponent<PoweredLightComponent>(candidate))
                continue;

            // The right tool is not available right now; keep the target for later so the android
            // can still see the mess and go service its tools instead of saying "all clear".
            if (!HasWorkingToolFor(candidate, blackboard, canMop, canBagTrash, canLightReplace, canScrub))
                continue;

            if (blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) &&
                blackboard.Blacklist.IsBlacklisted(android, candidate, GetBlacklistThreshold(blackboard)))
                continue;

            if (blackboard.Claims.IsClaimedByOther(candidate, owner))
                continue;

            blackboard.Claims.TryClaim(owner, candidate, TimeSpan.FromSeconds(blackboard.Config.ClaimTtlSeconds));

            selected = candidate;
            inventory.CurrentToolTarget = candidate;
            inventory.ToolUseStarted = false;
            inventory.LastTargetWasLiquid = blackboard.EntityManager.HasComponent<PuddleComponent>(candidate);
            blackboard.Log.Debug($"[SelectDirtyTarget] Selected target {candidate} (liquid: {inventory.LastTargetWasLiquid})");

            break;
        }

        foreach (var removed in toRemove)
            memory.KnownDirtyTargets.Remove(removed);

        if (selected.HasValue)
            return BehaviorStatus.Success;

        inventory.CurrentToolTarget = null;
        // If we found a candidate but could not claim/select one, give the whole work branch a short
        // cooldown so the android does not churn through the same list every tick.
        blackboard.SetBranchCooldown("Work");
        return BehaviorStatus.Failure;
    }

    /// <summary>
    /// Whether the android has a tool that can currently deal with this kind of mess.
    /// </summary>
    internal static bool HasWorkingToolFor(EntityUid target, AndroidBlackboard blackboard, bool canMop, bool canBagTrash, bool canLightReplace, bool canScrub)
    {
        if (blackboard.EntityManager.HasComponent<PuddleComponent>(target))
            return canMop;

        if (blackboard.EntityManager.HasComponent<PoweredLightComponent>(target))
            return canLightReplace;

        if (blackboard.EntityManager.HasComponent<ItemComponent>(target))
            return canBagTrash;

        // Footprints and other floor grime are scrubbed with soap or spray.
        return canScrub;
    }

    internal static bool IsValidFloorTarget(EntityUid target, AndroidBlackboard blackboard)
    {
        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(target, out var xform))
            return false;

        // Light fixtures are wall-mounted, not loose floor items.
        if (blackboard.EntityManager.HasComponent<PoweredLightComponent>(target))
            return true;

        // The target must be directly parented to a grid (not held by a mob or inside a container).
        if (!xform.ParentUid.IsValid() || !blackboard.EntityManager.HasComponent<MapGridComponent>(xform.ParentUid))
            return false;

        // If GridUid has already been initialized, make sure it agrees with the parent.
        if (xform.GridUid is { Valid: true } && xform.GridUid != xform.ParentUid)
            return false;

        // Dead pests (e.g. mice) are valid trash, but living mobs are not floor garbage.
        if (blackboard.EntityManager.TryGetComponent<MobStateComponent>(target, out var mobState) &&
            mobState.CurrentState != MobState.Dead)
        {
            return false;
        }

        // Puddles are not items; trash must be a loose item; footprints are also valid floor targets.
        if (!blackboard.EntityManager.HasComponent<PuddleComponent>(target) &&
            !blackboard.EntityManager.HasComponent<ItemComponent>(target) &&
            !blackboard.EntityManager.HasComponent<FootprintComponent>(target))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puddles that have already been mopped into pure water will evaporate; trash is dirty by
    /// definition while it is still on the floor; light fixtures are dirty until the bulb is normal.
    /// </summary>
    internal static bool IsStillDirtyTarget(EntityUid target, AndroidBlackboard blackboard)
    {
        if (blackboard.EntityManager.TryGetComponent<PoweredLightComponent>(target, out var light))
        {
            var bulb = blackboard.EntityManager.System<SharedPoweredLightSystem>().GetBulb(target, light);
            if (!bulb.HasValue)
                return true;

            if (!blackboard.EntityManager.TryGetComponent<LightBulbComponent>(bulb.Value, out var bulbComp))
                return true;

            return bulbComp.State != LightBulbState.Normal;
        }

        if (!blackboard.EntityManager.TryGetComponent<PuddleComponent>(target, out var puddle))
            return true;

        if (puddle.Solution is not { } soln)
            return true;

        var solution = soln.Comp.Solution;
        var puddleSystem = blackboard.EntityManager.System<SharedPuddleSystem>();
        var waterVolume = solution.GetTotalPrototypeQuantity(puddleSystem.GetAbsorbentReagents(solution));
        return waterVolume < solution.Volume;
    }

    internal static int GetBlacklistThreshold(AndroidBlackboard blackboard)
    {
        foreach (var proto in blackboard.Prototype.EnumeratePrototypes<AndroidNpcConfigPrototype>())
            return proto.BlacklistThreshold;

        return 3;
    }
}

/// <summary>
/// Selects the nearest known dirty tile (cleanable decal) and stores it in the android's memory.
/// </summary>
public sealed class SelectDirtyTileAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        // A tool do-after is in progress for the currently selected tile. Do not change the target
        // or reset tool state, otherwise EquipItemAction may switch tools and cancel the do-after.
        if (memory.CurrentDirtyTile is { } currentTile &&
            currentTile.IsValid(blackboard.EntityManager) &&
            blackboard.EntityManager.System<AndroidInventorySystem>().IsToolDoAfterRunning(owner, inventory, out _))
        {
            blackboard.Log.Debug($"[SelectDirtyTile] keeping tile {currentTile.Position} while tool do-after is running");
            return BehaviorStatus.Success;
        }

        var scanHelper = blackboard.EntityManager.System<AndroidScanSystem>().ScanHelper;

        // Keep walking to the tile already chosen while it is still dirty; re-sorting every known tile each
        // tick is expensive on stations with many cleanable decals.
        if (memory.CurrentDirtyTile is { } chosen && chosen.IsValid(blackboard.EntityManager) && scanHelper.IsTileDirty(chosen))
            return BehaviorStatus.Success;

        memory.CurrentDirtyTile = null;

        if (memory.KnownDirtyTiles.Count == 0)
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return BehaviorStatus.Failure;

        var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);

        var candidates = new List<EntityCoordinates>(memory.KnownDirtyTiles);
        var patrolDir = memory.PatrolDirection;
        const float DirectionNudge = 12f;

        candidates.Sort((a, b) =>
        {
            var posA = blackboard.Transform.ToMapCoordinates(a, false).Position;
            var posB = blackboard.Transform.ToMapCoordinates(b, false).Position;

            var distA = (posA - ownerPos).Length();
            var distB = (posB - ownerPos).Length();

            if (patrolDir != Vector2.Zero)
            {
                var dirA = distA > 0 ? Vector2.Normalize(posA - ownerPos) : Vector2.Zero;
                var dirB = distB > 0 ? Vector2.Normalize(posB - ownerPos) : Vector2.Zero;
                distA -= Vector2.Dot(dirA, patrolDir) * DirectionNudge;
                distB -= Vector2.Dot(dirB, patrolDir) * DirectionNudge;
            }

            return distA.CompareTo(distB);
        });

        var toRemove = new List<EntityCoordinates>();
        EntityCoordinates? selected = null;

        foreach (var candidate in candidates)
        {
            if (!candidate.IsValid(blackboard.EntityManager))
            {
                toRemove.Add(candidate);
                continue;
            }

            if (!scanHelper.IsTileDirty(candidate))
            {
                toRemove.Add(candidate);
                continue;
            }

            selected = candidate;
            memory.CurrentDirtyTile = candidate;
            inventory.ToolUseStarted = false;
            blackboard.Log.Debug($"[SelectDirtyTile] Selected tile {candidate.Position} on {candidate.EntityId}");
            break;
        }

        foreach (var removed in toRemove)
            memory.KnownDirtyTiles.Remove(removed);

        if (!selected.HasValue)
        {
            blackboard.Log.Debug("[SelectDirtyTile] No dirty tile remained after validation");
            return BehaviorStatus.Failure;
        }

        return BehaviorStatus.Success;
    }
}

/// <summary>
/// Selects a known broken light fixture and stores it in the android's inventory as the current tool
/// target. This gives light repair its own priority branch so it runs before generic puddle/trash
/// cleaning.
/// </summary>
public sealed class SelectBrokenLightAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var sawmill = blackboard.Log;

        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();
        var canLightReplace = inventorySystem.FindAndRegisterOwnedTool(owner, inventory, "LightReplacer").HasValue;

        // Already working on a broken light.
        if (inventory.CurrentToolTarget is { } current &&
            !blackboard.EntityManager.Deleted(current) &&
            blackboard.EntityManager.HasComponent<PoweredLightComponent>(current) &&
            SelectDirtyTargetAction.IsValidFloorTarget(current, blackboard) &&
            SelectDirtyTargetAction.IsStillDirtyTarget(current, blackboard) &&
            canLightReplace)
        {
            sawmill.Debug($"[SelectBrokenLight] continuing {current}");
            return BehaviorStatus.Success;
        }

        // If the current target is some other kind of dirty work, leave it for the other branch.
        if (inventory.CurrentToolTarget is { } other &&
            !blackboard.EntityManager.Deleted(other) &&
            !blackboard.EntityManager.HasComponent<PoweredLightComponent>(other))
        {
            return BehaviorStatus.Failure;
        }

        // Clear a stale broken-light target.
        if (inventory.CurrentToolTarget is { } stale)
        {
            blackboard.Claims.ReleaseClaim(stale);
            inventory.CurrentToolTarget = null;
        }

        if (!canLightReplace)
            return BehaviorStatus.Failure;

        var candidates = new List<EntityUid>(memory.KnownDirtyTargets);

        // Quick reject if nothing here is a light.
        if (!candidates.Any(e => !blackboard.EntityManager.Deleted(e) &&
                                 blackboard.EntityManager.HasComponent<PoweredLightComponent>(e)))
        {
            return BehaviorStatus.Failure;
        }

        // Prefer lights that are farther ahead in the current patrol direction so the android
        // spreads out rather than circling the same area.
        if (blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
        {
            var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);
            var patrolDir = memory.PatrolDirection;
            const float DirectionNudge = 12f;

            candidates.Sort((a, b) =>
            {
                var posA = blackboard.EntityManager.TryGetComponent<TransformComponent>(a, out var xformA)
                    ? blackboard.Transform.GetWorldPosition(xformA)
                    : Vector2.Zero;
                var posB = blackboard.EntityManager.TryGetComponent<TransformComponent>(b, out var xformB)
                    ? blackboard.Transform.GetWorldPosition(xformB)
                    : Vector2.Zero;

                var distA = (posA - ownerPos).Length();
                var distB = (posB - ownerPos).Length();

                if (patrolDir != Vector2.Zero)
                {
                    var dirA = distA > 0 ? Vector2.Normalize(posA - ownerPos) : Vector2.Zero;
                    var dirB = distB > 0 ? Vector2.Normalize(posB - ownerPos) : Vector2.Zero;
                    distA -= Vector2.Dot(dirA, patrolDir) * DirectionNudge;
                    distB -= Vector2.Dot(dirB, patrolDir) * DirectionNudge;
                }

                return distA.CompareTo(distB);
            });
        }

        var toRemove = new List<EntityUid>();
        EntityUid? selected = null;

        foreach (var candidate in candidates)
        {
            if (blackboard.EntityManager.Deleted(candidate))
            {
                toRemove.Add(candidate);
                continue;
            }

            if (!blackboard.EntityManager.HasComponent<PoweredLightComponent>(candidate))
                continue;

            if (!SelectDirtyTargetAction.IsValidFloorTarget(candidate, blackboard) ||
                !SelectDirtyTargetAction.IsStillDirtyTarget(candidate, blackboard))
            {
                toRemove.Add(candidate);
                continue;
            }

            if (blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) &&
                blackboard.Blacklist.IsBlacklisted(android, candidate, SelectDirtyTargetAction.GetBlacklistThreshold(blackboard)))
                continue;

            if (blackboard.Claims.IsClaimedByOther(candidate, owner))
                continue;

            blackboard.Claims.TryClaim(owner, candidate, TimeSpan.FromSeconds(blackboard.Config.ClaimTtlSeconds));

            selected = candidate;
            inventory.CurrentToolTarget = candidate;
            inventory.ToolUseStarted = false;
            sawmill.Debug($"[SelectBrokenLight] selected {candidate}");
            break;
        }

        foreach (var removed in toRemove)
            memory.KnownDirtyTargets.Remove(removed);

        if (selected.HasValue)
            return BehaviorStatus.Success;

        inventory.CurrentToolTarget = null;
        return BehaviorStatus.Failure;
    }
}
