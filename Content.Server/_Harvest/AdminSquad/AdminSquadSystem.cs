// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server.Chat.Systems;
using Content.Server.Destructible;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._Harvest.AdminSquad;
using Content.Shared.Administration;
using Content.Shared.Chat;
using Content.Shared.FixedPoint;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Database;
using Content.Shared.Doors.Components;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Harvest.AdminSquad;

/// <summary>
/// Lets admins order groups of mobs around, RTS style: move somewhere, attack-move, hold a position, attack a mob or a
/// structure, speak through them or release them. HTN NPCs swap their root task for <c>AdminSquadCompound</c>, which
/// reads the keys below; mobs without AI get an HTN for the time of the order; androids follow the <c>AdminSquad</c>
/// branch of their behavior tree. A unit that stops getting closer to its goal attacks the door or obstacle in its way.
/// </summary>
public sealed class AdminSquadSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly AndroidDialogueSystem _androidDialogue = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly HTNSystem _htn = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    public const string TargetKey = "AdminSquadTarget";
    public const string RangeKey = "AdminSquadRange";
    public const string ForceMoveKey = "AdminSquadForceMove";
    public const string AttackTargetKey = "AdminSquadAttackTarget";
    public const string AttackCoordinatesKey = "AdminSquadAttackCoordinates";
    public const string AttackRangeKey = "AdminSquadAttackRange";

    private static readonly ProtoId<HTNCompoundPrototype> Compound = "AdminSquadCompound";
    private static readonly ProtoId<DamageTypePrototype> StructuralDamage = "Structural";

    /// <summary>
    /// Structural damage added to each hit a unit lands on the structure it attacks: armored doors shrug off a
    /// bite or a punch entirely, this brings an airlock (500) down in about ten hits.
    /// </summary>
    private static readonly FixedPoint2 StructuralBonus = 50;

    /// <summary>
    /// How close to its destination a unit must get.
    /// </summary>
    public const float ArrivalRange = 0.6f;

    /// <summary>
    /// How close to its attack target a unit walks before hitting it: a door seen from the next tile is 1 m away.
    /// </summary>
    public const float AttackRange = 1.3f;

    /// <summary>
    /// Tiles looked at around the destination when spreading a squad out.
    /// </summary>
    private const int FormationSearchLimit = 400;

    /// <summary>
    /// A unit that has not moved this far for <see cref="StuckTime"/> is stuck and attacks what blocks it.
    /// </summary>
    private const float ProgressDistance = 0.5f;

    private static readonly TimeSpan StuckTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How far from a stuck unit an obstacle is looked for.
    /// </summary>
    private const float ObstacleRange = 1.6f;

    private const int MaxMessageLength = 256;

    private const float CheckInterval = 0.5f;
    private float _accumulator;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<AdminSquadOrderRequestEvent>(OnOrderRequest);
        SubscribeNetworkEvent<AdminSquadSpeakRequestEvent>(OnSpeakRequest);
        SubscribeLocalEvent<AdminSquadOrderedComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<MeleeHitEvent>(OnMeleeHit);
    }

    private void OnMeleeHit(MeleeHitEvent args)
    {
        if (!TryComp<AdminSquadOrderedComponent>(args.User, out var ordered)
            || ordered.AttackTarget is not { } target
            || HasComp<MobStateComponent>(target)
            || !args.HitEntities.Contains(target))
            return;

        args.BonusDamage += new DamageSpecifier(_proto.Index(StructuralDamage), StructuralBonus);
    }

    private void OnOrderRequest(AdminSquadOrderRequestEvent msg, EntitySessionEventArgs args)
    {
        if (!_admin.HasAdminFlag(args.SenderSession, AdminFlags.Fun))
            return;

        var units = GetEntities(msg.Units);
        var target = TryGetEntity(msg.TargetEntity, out var targetUid) ? targetUid : null;
        var results = Order(units, msg.Order, GetCoordinates(msg.Target), target);

        if (results.Count > 0)
        {
            _adminLog.Add(LogType.AdminCommands, LogImpact.Low,
                $"{args.SenderSession:player} gave the squad order {msg.Order} to {results.Count} units at {msg.Target}{(target != null ? $" against {ToPrettyString(target.Value)}" : "")}");
        }

        RaiseNetworkEvent(new AdminSquadOrderResultEvent(msg.Order,
                results.Select(r => GetNetEntity(r.Unit)).ToList(),
                results.Select(r => GetNetCoordinates(r.Destination)).ToList()),
            args.SenderSession.Channel);
    }

    private void OnSpeakRequest(AdminSquadSpeakRequestEvent msg, EntitySessionEventArgs args)
    {
        if (!_admin.HasAdminFlag(args.SenderSession, AdminFlags.Fun))
            return;

        var message = msg.Message.Trim();
        if (message.Length == 0)
            return;

        if (message.Length > MaxMessageLength)
            message = message[..MaxMessageLength];

        var speakers = 0;
        foreach (var uid in GetEntities(msg.Units))
        {
            if (HasComp<ActorComponent>(uid))
                continue;

            Speak(uid, message, msg.Speech);
            speakers++;
        }

        if (speakers > 0)
        {
            _adminLog.Add(LogType.AdminCommands, LogImpact.Low,
                $"{args.SenderSession:player} made {speakers} squad units {msg.Speech}: {message}");
        }
    }

    private List<EntityUid> GetEntities(List<NetEntity> nets)
    {
        var units = new List<EntityUid>();
        foreach (var net in nets)
        {
            if (TryGetEntity(net, out var uid))
                units.Add(uid.Value);
        }

        return units;
    }

    private void OnPlayerAttached(Entity<AdminSquadOrderedComponent> ent, ref PlayerAttachedEvent args)
    {
        Release(ent);
    }

    /// <summary>
    /// Makes a unit say, whisper or emote a line.
    /// </summary>
    public void Speak(EntityUid uid, string message, AdminSquadSpeech speech)
    {
        // Android speech goes through its dialogue queue, which also remembers what it said.
        if (speech == AdminSquadSpeech.Say && HasComp<AndroidComponent>(uid))
        {
            _androidDialogue.Speak(uid, message);
            return;
        }

        var type = speech switch
        {
            AdminSquadSpeech.Whisper => InGameICChatType.Whisper,
            AdminSquadSpeech.Emote => InGameICChatType.Emote,
            _ => InGameICChatType.Speak,
        };

        _chat.TrySendInGameICMessage(uid, message, type, hideChat: false, ignoreActionBlocker: true);
    }

    /// <summary>
    /// Gives an order to every unit that can take it.
    /// </summary>
    /// <param name="target">Destination of a move order, ignored by the others.</param>
    /// <param name="targetEntity">What an attack order attacks.</param>
    /// <returns>The units that took the order with their own destination.</returns>
    public List<(EntityUid Unit, EntityCoordinates Destination)> Order(IEnumerable<EntityUid> units,
        AdminSquadOrder order,
        EntityCoordinates target,
        EntityUid? targetEntity = null)
    {
        var results = new List<(EntityUid, EntityCoordinates)>();
        var distinct = units.Distinct().ToList();

        switch (order)
        {
            case AdminSquadOrder.Release:
                foreach (var uid in distinct)
                {
                    if (Release(uid))
                        results.Add((uid, Transform(uid).Coordinates));
                }

                break;

            case AdminSquadOrder.Hold:
                foreach (var uid in distinct)
                {
                    if (!CanCommand(uid))
                        continue;

                    var coords = Transform(uid).Coordinates;
                    Apply(uid, order, coords);
                    results.Add((uid, coords));
                }

                break;

            case AdminSquadOrder.Attack:
                if (targetEntity is not { } victim || !CanAttack(victim, false))
                    break;

                var victimCoords = Transform(victim).Coordinates;
                var victimMap = Transform(victim).MapID;
                foreach (var uid in distinct)
                {
                    if (uid == victim || !CanCommand(uid) || Transform(uid).MapID != victimMap)
                        continue;

                    var ordered = Apply(uid, order, victimCoords);
                    SetAttackTarget(uid, ordered, victim, false);
                    results.Add((uid, victimCoords));
                }

                break;

            case AdminSquadOrder.Move:
            case AdminSquadOrder.AttackMove:
                if (!target.IsValid(EntityManager))
                    break;

                var mapTarget = _transform.ToMapCoordinates(target);
                var squad = distinct
                    .Where(uid => CanCommand(uid) && Transform(uid).MapID == mapTarget.MapId)
                    .ToList();

                // The closest units get the spots closest to the destination.
                squad.Sort((a, b) => Distance(a, mapTarget).CompareTo(Distance(b, mapTarget)));

                var spots = FormationSpots(mapTarget, squad.Count);
                for (var i = 0; i < squad.Count; i++)
                {
                    Apply(squad[i], order, spots[i]);
                    results.Add((squad[i], spots[i]));
                }

                break;
        }

        return results;
    }

    /// <summary>
    /// Gives the unit back its own behavior.
    /// </summary>
    public bool Release(EntityUid uid)
    {
        if (!TryComp<AdminSquadOrderedComponent>(uid, out var ordered))
            return false;

        _steering.Unregister(uid);

        if (TryComp<CombatModeComponent>(uid, out var combat))
            _combat.SetInCombatMode(uid, false, combat);

        if (ordered.AddedHtn)
        {
            RemComp<HTNComponent>(uid);
        }
        else if (ordered.PreviousRootTask != null && TryComp<HTNComponent>(uid, out var htn))
        {
            htn.RootTask = ordered.PreviousRootTask;

            var blackboard = htn.Blackboard;
            blackboard.Remove<EntityCoordinates>(TargetKey);
            blackboard.Remove<float>(RangeKey);
            blackboard.Remove<bool>(ForceMoveKey);
            blackboard.Remove<EntityUid>(AttackTargetKey);
            blackboard.Remove<EntityCoordinates>(AttackCoordinatesKey);
            blackboard.Remove<float>(AttackRangeKey);

            if (ordered.PreviousNavSmash is { } smash)
                blackboard.SetValue(NPCBlackboard.NavSmash, smash);
            else
                blackboard.Remove<bool>(NPCBlackboard.NavSmash);

            ResetPlan((uid, htn));
        }

        RemComp(uid, ordered);
        return true;
    }

    /// <summary>
    /// Any living mob nobody plays can take orders.
    /// </summary>
    public bool CanCommand(EntityUid uid)
    {
        return !HasComp<ActorComponent>(uid)
               && HasComp<MobStateComponent>(uid)
               && !_mobState.IsDead(uid);
    }

    /// <summary>
    /// A living mob, or a structure that can be destroyed. An obstacle that is an open door is not in the way.
    /// </summary>
    private bool CanAttack(EntityUid target, bool obstacle)
    {
        if (TerminatingOrDeleted(target))
            return false;

        if (TryComp<MobStateComponent>(target, out var mobState))
            return _mobState.IsAlive(target, mobState);

        if (obstacle && TryComp<DoorComponent>(target, out var door) && door.State == DoorState.Open)
            return false;

        return HasComp<DestructibleComponent>(target);
    }

    private AdminSquadOrderedComponent Apply(EntityUid uid, AdminSquadOrder order, EntityCoordinates destination)
    {
        var android = HasComp<AndroidComponent>(uid);

        if (!TryComp<AdminSquadOrderedComponent>(uid, out var ordered))
        {
            ordered = AddComp<AdminSquadOrderedComponent>(uid);

            if (!android)
            {
                if (TryComp<HTNComponent>(uid, out var own))
                {
                    ordered.PreviousRootTask = own.RootTask;
                    if (own.Blackboard.TryGetValue<bool>(NPCBlackboard.NavSmash, out var smash, EntityManager))
                        ordered.PreviousNavSmash = smash;
                }
                else
                {
                    AddComp(uid, new HTNComponent { RootTask = new HTNCompoundTask { Task = Compound } });
                    ordered.AddedHtn = true;
                }
            }
        }

        ordered.Order = order;
        ordered.Destination = destination;
        ordered.AttackTarget = null;
        ordered.AttackingObstacle = false;
        ordered.ResumeAttack = null;
        ResetProgress(uid, ordered);

        // Stop whatever the unit was walking to.
        _steering.Unregister(uid);

        if (android || !TryComp<HTNComponent>(uid, out var htn))
            return ordered;

        htn.RootTask = new HTNCompoundTask { Task = Compound };

        var blackboard = htn.Blackboard;
        blackboard.SetValue(TargetKey, destination);
        blackboard.SetValue(RangeKey, ArrivalRange);
        blackboard.SetValue(AttackRangeKey, AttackRange);
        blackboard.SetValue(NPCBlackboard.NavSmash, true);
        blackboard.Remove<EntityUid>(AttackTargetKey);
        blackboard.Remove<EntityCoordinates>(AttackCoordinatesKey);

        if (order == AdminSquadOrder.Move)
            blackboard.SetValue(ForceMoveKey, true);
        else
            blackboard.Remove<bool>(ForceMoveKey);

        ResetPlan((uid, htn));
        _npc.WakeNPC(uid, htn);
        return ordered;
    }

    private void SetAttackTarget(EntityUid uid, AdminSquadOrderedComponent ordered, EntityUid? target, bool obstacle)
    {
        ordered.AttackTarget = target;
        ordered.AttackingObstacle = target != null && obstacle;
        ResetProgress(uid, ordered);
        _steering.Unregister(uid);

        if (!TryComp<HTNComponent>(uid, out var htn) || HasComp<AndroidComponent>(uid))
            return;

        if (target != null)
        {
            htn.Blackboard.SetValue(AttackTargetKey, target.Value);
            htn.Blackboard.SetValue(AttackCoordinatesKey, new EntityCoordinates(target.Value, Vector2.Zero));
        }
        else
        {
            htn.Blackboard.Remove<EntityUid>(AttackTargetKey);
            htn.Blackboard.Remove<EntityCoordinates>(AttackCoordinatesKey);
        }

        ResetPlan((uid, htn));
    }

    private void ResetProgress(EntityUid uid, AdminSquadOrderedComponent ordered)
    {
        ordered.ProgressPosition = _transform.GetMapCoordinates(uid);
        ordered.ProgressTime = _timing.CurTime;
    }

    /// <summary>
    /// Drops the current plan so the new root task is planned right away.
    /// </summary>
    private void ResetPlan(Entity<HTNComponent> ent)
    {
        if (!ent.Comp.Enabled)
            return;

        _htn.SetHTNEnabled(ent, false);
        _htn.SetHTNEnabled(ent, true);
    }

    private float Distance(EntityUid uid, MapCoordinates target)
    {
        return (_transform.GetWorldPosition(uid) - target.Position).Length();
    }

    #region Androids

    public bool IsOrdered(EntityUid uid)
    {
        return HasComp<AdminSquadOrderedComponent>(uid);
    }

    public EntityUid? GetAttackTarget(EntityUid uid)
    {
        return CompOrNull<AdminSquadOrderedComponent>(uid)?.AttackTarget;
    }

    /// <summary>
    /// Where an ordered android should walk, or null once it is there.
    /// </summary>
    public EntityCoordinates? GetMoveGoal(EntityUid uid)
    {
        if (!TryComp<AdminSquadOrderedComponent>(uid, out var ordered)
            || ordered.Order == AdminSquadOrder.Attack
            || _transform.InRange(Transform(uid).Coordinates, ordered.Destination, ArrivalRange))
            return null;

        return ordered.Destination;
    }

    /// <summary>
    /// Hits the attack target with what the unit holds, or bare-handed, when the weapon is ready.
    /// </summary>
    public void TryMeleeAttack(EntityUid uid)
    {
        if (GetAttackTarget(uid) is not { } target
            || !_melee.TryGetWeapon(uid, out var weaponUid, out var weapon)
            || weapon.NextAttack > _timing.CurTime)
            return;

        if (TryComp<CombatModeComponent>(uid, out var combat))
            _combat.SetInCombatMode(uid, true, combat);

        _melee.AttemptLightAttack(uid, weaponUid, weapon, target);
    }

    #endregion

    /// <summary>
    /// One destination per unit: the free tiles reachable from the target tile without crossing walls,
    /// closest first, so a squad spreads out instead of fighting over one tile.
    /// </summary>
    public List<EntityCoordinates> FormationSpots(MapCoordinates target, int count)
    {
        var spots = new List<EntityCoordinates>(count);
        if (count == 0)
            return spots;

        var targetCoords = _transform.ToCoordinates(target);
        if (!_map.TryFindGridAt(target, out var gridUid, out var grid))
        {
            for (var i = 0; i < count; i++)
                spots.Add(targetCoords);

            return spots;
        }

        var start = _map.TileIndicesFor(gridUid, grid, target);
        var visited = new HashSet<Vector2i> { start };
        var queue = new Queue<Vector2i>();
        queue.Enqueue(start);

        while (queue.Count > 0 && spots.Count < count && visited.Count < FormationSearchLimit)
        {
            var tile = queue.Dequeue();
            var free = IsFree(gridUid, grid, tile);

            if (free)
                spots.Add(_map.GridTileToLocal(gridUid, grid, tile));
            else if (tile != start)
                continue;

            foreach (var dir in new[] { Vector2i.Up, Vector2i.Down, Vector2i.Left, Vector2i.Right })
            {
                var next = tile + dir;
                if (visited.Add(next))
                    queue.Enqueue(next);
            }
        }

        if (spots.Count == 0)
            spots.Add(targetCoords);

        // Not enough room: units share the spots.
        for (var i = spots.Count; i < count; i++)
            spots.Add(spots[i % spots.Count]);

        return spots;
    }

    private bool IsFree(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        return _map.TryGetTileRef(gridUid, grid, tile, out var tileRef)
               && !_turf.IsSpace(tileRef)
               && !_turf.IsTileBlocked(gridUid, tile, CollisionGroup.Impassable, grid);
    }

    /// <summary>
    /// The closest thing that can be broken between the unit and its goal: closed doors first, then any hard
    /// destructible structure the unit collides with (window, grille, wall, door assembly...).
    /// </summary>
    public EntityUid? FindObstacle(EntityUid uid, MapCoordinates goal)
    {
        var position = _transform.GetWorldPosition(uid);
        var toGoal = goal.Position - position;
        if (toGoal.LengthSquared() < 0.01f)
            return null;

        toGoal = Vector2.Normalize(toGoal);
        var unitMask = TryComp<PhysicsComponent>(uid, out var unitBody)
            ? unitBody.CollisionMask
            : (int) CollisionGroup.Impassable;

        EntityUid? best = null;
        var bestScore = float.MinValue;

        foreach (var ent in _lookup.GetEntitiesInRange(uid, ObstacleRange, LookupFlags.Static))
        {
            var isDoor = TryComp<DoorComponent>(ent, out var door);
            if (isDoor && door!.State == DoorState.Open)
                continue;

            if (!isDoor && (!HasComp<DestructibleComponent>(ent)
                            || !TryComp<PhysicsComponent>(ent, out var body)
                            || !body.Hard
                            || !body.CanCollide
                            || (body.CollisionLayer & unitMask) == 0))
                continue;

            var offset = _transform.GetWorldPosition(ent) - position;
            var distance = offset.Length();
            var facing = distance < 0.01f ? 1f : Vector2.Dot(offset / distance, toGoal);

            // Only what stands roughly between the unit and its goal.
            if (facing < 0.3f)
                continue;

            var score = facing - distance + (isDoor ? 1f : 0f);
            if (score <= bestScore)
                continue;

            best = ent;
            bestScore = score;
        }

        return best;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _accumulator += frameTime;
        if (_accumulator < CheckInterval)
            return;

        _accumulator = 0f;

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<AdminSquadOrderedComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var ordered, out var xform))
        {
            if (ordered.AttackTarget is { } target && !CanAttack(target, ordered.AttackingObstacle))
                AttackDone(uid, ordered, xform);

            // The melee and gun operators drop their target key when they stop: give it back while the attack goes on.
            if (ordered.AttackTarget is { } current
                && !HasComp<AndroidComponent>(uid)
                && TryComp<HTNComponent>(uid, out var attacker)
                && !attacker.Blackboard.TryGetValue<EntityUid>(AttackTargetKey, out _, EntityManager))
            {
                attacker.Blackboard.SetValue(AttackTargetKey, current);
                attacker.Blackboard.SetValue(AttackCoordinatesKey, new EntityCoordinates(current, Vector2.Zero));
            }

            // A move order turns into holding the destination once there.
            if (ordered.Order == AdminSquadOrder.Move
                && ordered.AttackTarget == null
                && _transform.InRange(xform.Coordinates, ordered.Destination, ArrivalRange + 0.2f))
            {
                ordered.Order = AdminSquadOrder.Hold;
                if (TryComp<HTNComponent>(uid, out var htn))
                    htn.Blackboard.Remove<bool>(ForceMoveKey);
            }

            CheckStuck(uid, ordered, xform, now);
        }
    }

    private void AttackDone(EntityUid uid, AdminSquadOrderedComponent ordered, TransformComponent xform)
    {
        if (ordered.AttackingObstacle)
        {
            // The way is clear: back to the attack target, or to the order.
            var resume = ordered.ResumeAttack;
            ordered.ResumeAttack = null;
            SetAttackTarget(uid, ordered, resume != null && CanAttack(resume.Value, false) ? resume : null, false);
            return;
        }

        // The target is down: hold where the unit stands.
        ordered.Order = AdminSquadOrder.Hold;
        ordered.Destination = xform.Coordinates;
        if (TryComp<HTNComponent>(uid, out var htn))
        {
            htn.Blackboard.SetValue(TargetKey, ordered.Destination);
            htn.Blackboard.Remove<bool>(ForceMoveKey);
        }

        SetAttackTarget(uid, ordered, null, false);

        if (TryComp<CombatModeComponent>(uid, out var combat))
            _combat.SetInCombatMode(uid, false, combat);
    }

    /// <summary>
    /// A unit that has not moved for a while without reaching its goal attacks what is in its way.
    /// </summary>
    private void CheckStuck(EntityUid uid, AdminSquadOrderedComponent ordered, TransformComponent xform, TimeSpan now)
    {
        var position = _transform.GetMapCoordinates(uid, xform);
        MapCoordinates goal;
        float range;

        if (ordered.AttackTarget is { } target)
        {
            // Already hitting the obstacle or the target.
            if (ordered.AttackingObstacle)
            {
                ResetProgress(uid, ordered);
                return;
            }

            goal = _transform.GetMapCoordinates(target);
            range = AttackRange + 0.3f;
        }
        else if (ordered.Order is AdminSquadOrder.Move or AdminSquadOrder.AttackMove)
        {
            goal = _transform.ToMapCoordinates(ordered.Destination);
            range = ArrivalRange + 0.2f;
        }
        else
        {
            ResetProgress(uid, ordered);
            return;
        }

        // Fighting on the way is not being stuck.
        var fighting = ordered.Order == AdminSquadOrder.AttackMove
                       && ordered.AttackTarget == null
                       && TryComp<CombatModeComponent>(uid, out var combat)
                       && combat.IsInCombatMode;

        if (fighting
            || goal.MapId != position.MapId
            || (goal.Position - position.Position).Length() <= range
            || position.MapId != ordered.ProgressPosition.MapId
            || (position.Position - ordered.ProgressPosition.Position).Length() >= ProgressDistance)
        {
            ResetProgress(uid, ordered);
            return;
        }

        if (now - ordered.ProgressTime < StuckTime)
            return;

        if (FindObstacle(uid, goal) is not { } obstacle)
        {
            ResetProgress(uid, ordered);
            return;
        }

        ordered.ResumeAttack = ordered.AttackTarget;
        SetAttackTarget(uid, ordered, obstacle, true);
    }
}
