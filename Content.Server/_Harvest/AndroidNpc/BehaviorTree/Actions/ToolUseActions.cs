// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Goobstation.Common.Footprints;
using Content.Shared.FixedPoint;
using Content.Goobstation.Shared.FloorCleaner;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.DoAfter;
using Content.Shared.Fluids.Components;
using Content.Shared.Fluids.EntitySystems;
using Content.Shared.Fluids;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Storage;
using Content.Shared.Timing;
using Content.Shared.Trigger.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map.Components;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Equips the first item matching the requested prototype.
/// </summary>
public sealed class EquipItemAction : IBehaviorNode
{
    private readonly Func<EntityUid, AndroidBlackboard, string?> _getPrototype;

    /// <summary>
    /// Creates an action that equips an item of the prototype returned by <paramref name="getPrototype"/>.
    /// </summary>
    public EquipItemAction(Func<EntityUid, AndroidBlackboard, string?> getPrototype)
    {
        _getPrototype = getPrototype;
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var sawmill = blackboard.Log;

        if (blackboard.EntityManager.Deleted(owner))
        {
            sawmill.Warning("[EquipItem] owner deleted");
            return BehaviorStatus.Failure;
        }

        var proto = _getPrototype(owner, blackboard);

        sawmill.Debug($"[EquipItem] requested prototype: {proto ?? "(null)"}");

        if (string.IsNullOrEmpty(proto))
        {
            sawmill.Warning("[EquipItem] no prototype");
            return BehaviorStatus.Failure;
        }

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
        {
            sawmill.Warning("[EquipItem] no inventory");
            return BehaviorStatus.Failure;
        }

        var invSys = blackboard.EntityManager.System<AndroidInventorySystem>();

        // Already equipped?
        var equipped = invSys.GetEquippedItem(owner);

        if (equipped.HasValue &&
            blackboard.EntityManager.TryGetComponent<MetaDataComponent>(equipped.Value, out var meta) &&
            meta.EntityPrototype?.ID == proto)
        {
            sawmill.Debug($"[EquipItem] already equipped {equipped.Value} ({proto})");
            inventory.EquippedItem = equipped.Value;
            return BehaviorStatus.Success;
        }

        // If a tool do-after is already running, keep the tool that started it. Switching items would
        // cancel the do-after and produce overlapping/stuttering progress bars.
        if (invSys.IsToolDoAfterRunning(owner, inventory, out var runningTool))
        {
            sawmill.Debug($"[EquipItem] tool do-after is running for {runningTool.Value}; preserving it");

            if (equipped == runningTool)
            {
                sawmill.Debug($"[EquipItem] requested {proto} but keeping running tool {runningTool.Value}");
                return BehaviorStatus.Success;
            }

            // The active hand has the wrong item; make the running tool active again so the do-after
            // is not interrupted by BreakOnHandChange/BreakOnDropItem checks.
            inventory.PreviousEquippedItem = inventory.EquippedItem;
            if (invSys.Equip(owner, inventory, runningTool.Value))
            {
                sawmill.Debug($"[EquipItem] re-equipped running tool {runningTool.Value}");
                return BehaviorStatus.Success;
            }

            sawmill.Warning($"[EquipItem] failed to re-equip running tool {runningTool.Value}; retrying");
            return BehaviorStatus.Running;
        }

        var item = invSys.FindAndRegisterOwnedTool(owner, inventory, proto);

        if (!item.HasValue)
        {
            sawmill.Warning($"[EquipItem] could not find {proto} in {inventory.OwnedItems.Count} owned items");
            return BehaviorStatus.Failure;
        }

        sawmill.Debug($"[EquipItem] found {item.Value} for {proto}; trying to equip");

        // Track the previously equipped item before Equip() overwrites the active hand.
        inventory.PreviousEquippedItem = inventory.EquippedItem;

        if (!blackboard.EntityManager.System<AndroidInventorySystem>().Equip(owner, inventory, item.Value))
        {
            sawmill.Warning($"[EquipItem] Equip() failed for {item.Value}");
            return BehaviorStatus.Failure;
        }

        inventory.EquippedItem = item.Value;
        sawmill.Debug($"[EquipItem] equipped {item.Value} ({proto})");
        return BehaviorStatus.Success;
    }
}

/// <summary>
/// Uses the currently equipped item on the active target.
/// </summary>
public sealed class UseItemAction : IBehaviorNode
{
    /// <summary>
    /// Returns the current target entity to use the tool on.
    /// </summary>
    private readonly Func<EntityUid, AndroidBlackboard, EntityUid?> _getTarget;

    /// <summary>
    /// Creates a use-item action with the supplied target provider.
    /// </summary>
    public UseItemAction(Func<EntityUid, AndroidBlackboard, EntityUid?> getTarget)
    {
        _getTarget = getTarget;
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var sawmill = blackboard.Log;

        // PRE-VALIDATION
        if (blackboard.EntityManager.Deleted(owner))
        {
            sawmill.Warning("[UseItem] owner deleted");
            return BehaviorStatus.Failure;
        }

        var target = _getTarget(owner, blackboard);

        if (!target.HasValue || blackboard.EntityManager.Deleted(target.Value))
        {
            sawmill.Debug($"[UseItem] target missing: {target?.Id ?? 0}");
            return BehaviorStatus.Failure;
        }

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
        {
            sawmill.Warning("[UseItem] no inventory");
            return BehaviorStatus.Failure;
        }

        var invSys = blackboard.EntityManager.System<AndroidInventorySystem>();
        var equipped = inventory.EquippedItem;

        sawmill.Debug($"[UseItem] equipped={equipped.ToString()} target={target.ToString()}");

        if (!equipped.HasValue || blackboard.EntityManager.Deleted(equipped.Value))
        {
            sawmill.Warning("[UseItem] no equipped item");
            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return BehaviorStatus.Failure;
        }

        // Wait for use delay if any.
        if (blackboard.EntityManager.TryGetComponent<UseDelayComponent>(equipped.Value, out var useDelay)
            && blackboard.EntityManager.System<UseDelaySystem>().IsDelayed((equipped.Value, useDelay)))
        {
            return BehaviorStatus.Running;
        }

        // Safety: refuse to interact with targets that are no longer loose on the floor.
        if (!IsValidFloorTarget(target.Value, blackboard))
        {
            var tx = blackboard.EntityManager.GetComponent<TransformComponent>(target.Value);
            sawmill.Debug($"[UseItem] target {target.Value} is not on the floor; parent={tx.ParentUid} grid={tx.GridUid}");
            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return BehaviorStatus.Failure;
        }

        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        if (!InInteractionRange(owner, target.Value, blackboard))
        {
            var ownerPos = blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ox)
                ? blackboard.Transform.GetWorldPosition(ox)
                : Vector2.Zero;
            var targetPos = blackboard.EntityManager.TryGetComponent<TransformComponent>(target.Value, out var tx)
                ? blackboard.Transform.GetWorldPosition(tx)
                : Vector2.Zero;
            sawmill.Debug($"[UseItem] target {target.Value} is not in interaction range; distance={(targetPos - ownerPos).Length():F2} owner={ownerPos} target={targetPos}");
            RecordFailure(owner, blackboard, target.Value);
            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return BehaviorStatus.Failure;
        }

        // EXECUTE
        var isFootprint = blackboard.EntityManager.HasComponent<FootprintComponent>(target.Value);

        if (isFootprint)
        {
            // Footprints and floor grime should be scrubbed, not mopped. A mop that also has
            // FloorCleanerComponent can clean them through the after-interact path.
            if (blackboard.EntityManager.HasComponent<FloorCleanerComponent>(equipped.Value))
            {
                if (invSys.IsToolDoAfterRunning(owner, inventory, out _))
                {
                    sawmill.Debug($"[UseItem] waiting for scrub do-after on {target.Value}");
                    return BehaviorStatus.Running;
                }

                var targetXform = blackboard.EntityManager.GetComponent<TransformComponent>(target.Value);
                var ev = new AfterInteractEvent(owner, equipped.Value, null, targetXform.Coordinates, true);
                blackboard.EntityManager.EventBus.RaiseLocalEvent(equipped.Value, ev, false);
                sawmill.Debug($"[UseItem] scrubbed {target.Value} with {equipped.Value}; handled={ev.Handled}");

                if (ev.Handled && invSys.IsToolDoAfterRunning(owner, inventory, out _))
                {
                    sawmill.Debug($"[UseItem] scrub do-after started on {target.Value}; running");
                    return BehaviorStatus.Running;
                }

                blackboard.Claims.ReleaseClaim(target.Value);
                inventory.CurrentToolTarget = null;
                return ev.Handled ? BehaviorStatus.Success : BehaviorStatus.Failure;
            }

            if (blackboard.EntityManager.TryGetComponent<SprayComponent>(equipped.Value, out var spray))
            {
                var spraySystem = blackboard.EntityManager.System<SharedSpraySystem>();
                var targetXform = blackboard.EntityManager.GetComponent<TransformComponent>(target.Value);
                spraySystem.Spray((equipped.Value, spray), new MapCoordinates(targetXform.WorldPosition, targetXform.MapID), owner);
                sawmill.Debug($"[UseItem] sprayed {target.Value} with {equipped.Value}");
                blackboard.Claims.ReleaseClaim(target.Value);
                inventory.CurrentToolTarget = null;
                return BehaviorStatus.Success;
            }

            sawmill.Warning($"[UseItem] wrong tool for footprint {target.Value}; equipped={equipped.Value}");
            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return BehaviorStatus.Failure;
        }

        if (blackboard.EntityManager.TryGetComponent<AbsorbentComponent>(equipped.Value, out var absorbent))
        {
            // A mop with no clean water left silently does nothing when used, so bail out now and let
            // the servicing branches of the tree rinse it instead of scrubbing forever.
            if (!inventorySystem.CanMopClean((equipped.Value, absorbent)))
            {
                sawmill.Debug($"[UseItem] mop {equipped.Value} has no clean water left; needs rinsing");
                blackboard.Claims.ReleaseClaim(target.Value);
                inventory.CurrentToolTarget = null;
                return BehaviorStatus.Failure;
            }

            var dirtBefore = GetDirtyVolume(target.Value, blackboard);

            sawmill.Debug($"[UseItem] mopping target {target.Value} with {equipped.Value}");
            blackboard.EntityManager.System<SharedAbsorbentSystem>().Mop((equipped.Value, absorbent), owner, target.Value);

            // Puddle deleted or fully cleaned (now just water -> will evaporate).
            if (blackboard.EntityManager.Deleted(target.Value))
            {
                sawmill.Debug($"[UseItem] target {target.Value} deleted after mopping");
                blackboard.Claims.ReleaseClaim(target.Value);
                inventory.CurrentToolTarget = null;
                return BehaviorStatus.Success;
            }

            if (blackboard.EntityManager.HasComponent<PuddleComponent>(target.Value))
            {
                var dirtAfter = GetDirtyVolume(target.Value, blackboard);

                if (dirtAfter <= FixedPoint2.Zero)
                {
                    sawmill.Debug($"[UseItem] puddle {target.Value} is now just water; success");
                    blackboard.Claims.ReleaseClaim(target.Value);
                    inventory.CurrentToolTarget = null;
                    return BehaviorStatus.Success;
                }

                // No progress at all this tick. Something is preventing the mop from working, so stop
                // rather than repeat the same no-op forever.
                if (dirtAfter >= dirtBefore)
                {
                    sawmill.Debug($"[UseItem] mopping {target.Value} made no progress ({dirtBefore} -> {dirtAfter})");
                    blackboard.Claims.ReleaseClaim(target.Value);
                    inventory.CurrentToolTarget = null;
                    return BehaviorStatus.Failure;
                }

                sawmill.Debug($"[UseItem] target {target.Value} still has dirty puddle; running");
                return BehaviorStatus.Running;
            }

            // A mop was equipped for something that is not a puddle (should not happen after footprint handling).
            sawmill.Warning($"[UseItem] mop used on non-puddle {target.Value}");
            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return BehaviorStatus.Failure;
        }
        else if (blackboard.EntityManager.TryGetComponent<SprayComponent>(equipped.Value, out var spray))
        {
            var spraySystem = blackboard.EntityManager.System<SharedSpraySystem>();
            var targetXform = blackboard.EntityManager.GetComponent<TransformComponent>(target.Value);
            spraySystem.Spray((equipped.Value, spray), new MapCoordinates(targetXform.WorldPosition, targetXform.MapID), owner);
            sawmill.Debug($"[UseItem] sprayed {target.Value} with {equipped.Value}");
            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return BehaviorStatus.Success;
        }
        else if (blackboard.EntityManager.HasComponent<FloorCleanerComponent>(equipped.Value))
        {
            // Wait for an existing scrub do-after to finish before deciding what to do next.
            if (invSys.IsToolDoAfterRunning(owner, inventory, out _))
            {
                sawmill.Debug($"[UseItem] waiting for scrub do-after on {target.Value}");
                return BehaviorStatus.Running;
            }

            var targetXform = blackboard.EntityManager.GetComponent<TransformComponent>(target.Value);
            var ev = new AfterInteractEvent(owner, equipped.Value, null, targetXform.Coordinates, true);
            blackboard.EntityManager.EventBus.RaiseLocalEvent(equipped.Value, ev, false);
            sawmill.Debug($"[UseItem] scrubbed {target.Value} with soap {equipped.Value}; handled={ev.Handled}");

            if (ev.Handled && invSys.IsToolDoAfterRunning(owner, inventory, out _))
            {
                sawmill.Debug($"[UseItem] scrub do-after started on {target.Value}; running");
                return BehaviorStatus.Running;
            }

            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return ev.Handled ? BehaviorStatus.Success : BehaviorStatus.Failure;
        }
        else if (blackboard.EntityManager.TryGetComponent<StorageComponent>(equipped.Value, out var storage))
        {
            var storageSystem = blackboard.EntityManager.System<SharedStorageSystem>();

            // The bag is out of room; it has to be emptied into a disposal unit before this can continue.
            if (!storageSystem.HasSpace((equipped.Value, storage)))
            {
                sawmill.Debug($"[UseItem] trash bag {equipped.Value} is full; needs emptying");
                blackboard.Claims.ReleaseClaim(target.Value);
                inventory.CurrentToolTarget = null;
                return BehaviorStatus.Failure;
            }

            // Put trash into the equipped trash bag/box.
            if (!storageSystem.CanInsert(equipped.Value, target.Value, out var reason, storage))
            {
                sawmill.Warning($"[UseItem] trash bag {equipped.Value} cannot accept {target.Value}: {reason ?? "unknown"}");
                RecordFailure(owner, blackboard, target.Value);
                blackboard.Claims.ReleaseClaim(target.Value);
                inventory.CurrentToolTarget = null;
                return BehaviorStatus.Failure;
            }

            if (!storageSystem.Insert(equipped.Value, target.Value, out _, out _, user: owner, storageComp: storage))
            {
                sawmill.Warning($"[UseItem] trash bag {equipped.Value} refused to insert {target.Value}");
                RecordFailure(owner, blackboard, target.Value);
                blackboard.Claims.ReleaseClaim(target.Value);
                inventory.CurrentToolTarget = null;
                return BehaviorStatus.Failure;
            }

            sawmill.Debug($"[UseItem] inserted {target.Value} into {equipped.Value}");
        }
        else if (blackboard.EntityManager.TryGetComponent<LightReplacerComponent>(equipped.Value, out _))
        {
            // Light fixtures are wall-mounted, not floor items, so skip the floor-only check.
            var lightReplacer = blackboard.EntityManager.System<Content.Server.Light.EntitySystems.LightReplacerSystem>();

            sawmill.Debug($"[UseItem] replacing bulb in {target.Value} with {equipped.Value}");

            if (blackboard.EntityManager.TryGetComponent<PoweredLightComponent>(target.Value, out var fixture) &&
                blackboard.EntityManager.System<SharedPoweredLightSystem>().GetBulb(target.Value, fixture) is { } fixtureBulb &&
                blackboard.EntityManager.TryGetComponent<LightBulbComponent>(fixtureBulb, out var fixtureBulbComp))
            {
                sawmill.Debug($"[UseItem] fixture bulb {fixtureBulb} state: {fixtureBulbComp.State}");

                // Another source already fixed the light, or our own tree already replaced it.
                // Treat the target as resolved rather than failing and looping on it.
                if (fixtureBulbComp.State == LightBulbState.Normal)
                {
                    sawmill.Debug($"[UseItem] fixture {target.Value} already has a normal bulb; treating as resolved");
                    blackboard.Claims.ReleaseClaim(target.Value);
                    return BehaviorStatus.Success;
                }
            }

            if (lightReplacer.TryReplaceBulb(equipped.Value, target.Value, owner))
            {
                sawmill.Debug($"[UseItem] replaced bulb in {target.Value}");
                blackboard.Claims.ReleaseClaim(target.Value);
                // Do not clear CurrentToolTarget here; SelectDirtyTarget will drop fixed fixtures once
                // IsStillDirtyTarget reports they no longer need a bulb.
                return BehaviorStatus.Success;
            }

            if (blackboard.EntityManager.TryGetComponent<LightReplacerComponent>(equipped.Value, out var replacer) &&
                blackboard.EntityManager.TryGetComponent<PoweredLightComponent>(target.Value, out var lightFixture))
            {
                sawmill.Warning($"[UseItem] could not replace bulb in {target.Value} (fixture type: {lightFixture.BulbType}, bulbs in replacer: {replacer.InsertedBulbs.ContainedEntities.Count})");
            }
            else
                sawmill.Warning($"[UseItem] could not replace bulb in {target.Value} (no light replacer or fixture component)");

            RecordFailure(owner, blackboard, target.Value);
            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return BehaviorStatus.Failure;
        }
        else
        {
            // The wrong tool is equipped for this target; do not treat the target as the problem.
            blackboard.Claims.ReleaseClaim(target.Value);
            inventory.CurrentToolTarget = null;
            return BehaviorStatus.Failure;
        }

        // Non-puddle floor trash was picked up successfully.
        var equippedProto = blackboard.EntityManager.TryGetComponent<MetaDataComponent>(equipped.Value, out var meta)
            ? meta.EntityPrototype?.ID ?? "?"
            : "?";
        sawmill.Debug($"[UseItem] target {target.Value} handled with {equipped.Value} ({equippedProto}); success");
        blackboard.Claims.ReleaseClaim(target.Value);
        // Leave CurrentToolTarget set; SelectDirtyTarget will detect the item is no longer on the
        // floor via IsValidFloorTarget and pick a new target on the next tick.
        return BehaviorStatus.Success;
    }

    /// <summary>
    /// How much of the puddle is something other than an absorbent reagent, i.e. how much of it still
    /// needs mopping up. Water left behind simply evaporates.
    /// </summary>
    private static FixedPoint2 GetDirtyVolume(EntityUid target, AndroidBlackboard blackboard)
    {
        if (!blackboard.EntityManager.TryGetComponent<PuddleComponent>(target, out var puddle) ||
            puddle.Solution is not { } soln)
        {
            return FixedPoint2.Zero;
        }

        var solution = soln.Comp.Solution;
        var puddleSystem = blackboard.EntityManager.System<SharedPuddleSystem>();

        return solution.Volume - solution.GetTotalPrototypeQuantity(puddleSystem.GetAbsorbentReagents(solution));
    }

    private static bool IsValidFloorTarget(EntityUid target, AndroidBlackboard blackboard)
    {
        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(target, out var xform))
            return false;

        // Light fixtures are wall-mounted, not floor items, so the normal floor-parent checks do not apply.
        if (blackboard.EntityManager.HasComponent<PoweredLightComponent>(target))
            return true;

        // A loose floor item must be parented directly to a grid.
        if (!xform.ParentUid.IsValid() || !blackboard.EntityManager.HasComponent<MapGridComponent>(xform.ParentUid))
            return false;

        if (xform.GridUid is { Valid: true } && xform.GridUid != xform.ParentUid)
            return false;

        if (blackboard.EntityManager.HasComponent<MobStateComponent>(target))
            return false;

        // Trash should be a loose item; puddles are not items.
        if (!blackboard.EntityManager.HasComponent<PuddleComponent>(target) &&
            !blackboard.EntityManager.HasComponent<ItemComponent>(target))
        {
            return false;
        }

        return true;
    }

    private static bool InInteractionRange(EntityUid owner, EntityUid target, AndroidBlackboard blackboard)
    {
        var interaction = blackboard.EntityManager.System<SharedInteractionSystem>();
        return interaction.InRangeUnobstructed(owner, target, SharedInteractionSystem.InteractionRange);
    }

    private static void RecordFailure(EntityUid owner, AndroidBlackboard blackboard, EntityUid target)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return;

        var config = blackboard.Config;
        var threshold = config.BlacklistThreshold;
        var ttl = TimeSpan.FromSeconds(config.BlacklistTtlSeconds);

        blackboard.Blacklist.RecordFailure(android, target, threshold, ttl, blackboard.Timing.CurTime);

        // Give the whole work branch a short cooldown after a failed use so the android does not
        // immediately reselect the same target and loop on it.
        blackboard.SetBranchCooldown("Work");
    }

}

/// <summary>
/// Uses the currently equipped scrubbing/spraying tool on the active dirty tile (cleanable decal).
/// </summary>
public sealed class UseItemOnTileAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var sawmill = blackboard.Log;

        if (blackboard.EntityManager.Deleted(owner))
        {
            sawmill.Warning("[UseItemOnTile] owner deleted");
            return BehaviorStatus.Failure;
        }

        if (!blackboard.EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory))
        {
            sawmill.Warning("[UseItemOnTile] no memory");
            return BehaviorStatus.Failure;
        }

        if (!memory.CurrentDirtyTile.HasValue)
        {
            sawmill.Debug("[UseItemOnTile] no current dirty tile");
            return BehaviorStatus.Failure;
        }

        var tile = memory.CurrentDirtyTile.Value;

        if (!tile.IsValid(blackboard.EntityManager))
        {
            sawmill.Debug("[UseItemOnTile] tile coordinates are no longer valid");
            memory.CurrentDirtyTile = null;
            return BehaviorStatus.Failure;
        }

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
        {
            sawmill.Warning("[UseItemOnTile] no inventory");
            return BehaviorStatus.Failure;
        }

        var invSys = blackboard.EntityManager.System<AndroidInventorySystem>();
        var equipped = inventory.EquippedItem;

        sawmill.Debug($"[UseItemOnTile] equipped={equipped.ToString()} tile={tile.Position}");

        if (!equipped.HasValue || blackboard.EntityManager.Deleted(equipped.Value))
        {
            sawmill.Warning("[UseItemOnTile] no equipped item");
            memory.CurrentDirtyTile = null;
            return BehaviorStatus.Failure;
        }

        // Wait for use delay if any.
        if (blackboard.EntityManager.TryGetComponent<UseDelayComponent>(equipped.Value, out var useDelay)
            && blackboard.EntityManager.System<UseDelaySystem>().IsDelayed((equipped.Value, useDelay)))
        {
            return BehaviorStatus.Running;
        }

        var interaction = blackboard.EntityManager.System<SharedInteractionSystem>();

        if (!interaction.InRangeUnobstructed(owner, tile, SharedInteractionSystem.InteractionRange))
        {
            sawmill.Debug($"[UseItemOnTile] tile {tile.Position} is not in interaction range");
            RecordFailure(owner, blackboard, memory, tile);
            return BehaviorStatus.Failure;
        }

        // Prefer the dedicated floor-cleaner (mop/soap) because it removes decals and footprints.
        if (blackboard.EntityManager.TryGetComponent<FloorCleanerComponent>(equipped.Value, out _))
        {
            if (invSys.IsToolDoAfterRunning(owner, inventory, out _))
            {
                sawmill.Debug("[UseItemOnTile] waiting for tool do-after");
                return BehaviorStatus.Running;
            }

            var ev = new AfterInteractEvent(owner, equipped.Value, null, tile, true);
            blackboard.EntityManager.EventBus.RaiseLocalEvent(equipped.Value, ev, false);
            sawmill.Debug($"[UseItemOnTile] scrubbed tile {tile.Position}; handled={ev.Handled}");

            if (ev.Handled && invSys.IsToolDoAfterRunning(owner, inventory, out _))
            {
                sawmill.Debug("[UseItemOnTile] tool do-after started; running");
                return BehaviorStatus.Running;
            }

            // Tile was either handled or not; move on either way.
            memory.CurrentDirtyTile = null;
            memory.KnownDirtyTiles.Remove(tile);
            return ev.Handled ? BehaviorStatus.Success : BehaviorStatus.Failure;
        }

        // Spray cleaners also remove decals via tile reactions.
        if (blackboard.EntityManager.TryGetComponent<SprayComponent>(equipped.Value, out var spray))
        {
            var spraySystem = blackboard.EntityManager.System<SharedSpraySystem>();
            var mapCoords = blackboard.Transform.ToMapCoordinates(tile);
            spraySystem.Spray(new Entity<SprayComponent>(equipped.Value, spray), mapCoords, owner);
            sawmill.Debug($"[UseItemOnTile] sprayed tile {tile.Position} with {equipped.Value}");

            memory.CurrentDirtyTile = null;
            memory.KnownDirtyTiles.Remove(tile);
            return BehaviorStatus.Success;
        }

        // Mops can only interact with puddles, not decals.
        sawmill.Warning($"[UseItemOnTile] equipped item {equipped.Value} cannot scrub tiles");
        memory.CurrentDirtyTile = null;
        return BehaviorStatus.Failure;
    }

    private static void RecordFailure(EntityUid owner, AndroidBlackboard blackboard, AndroidMemoryComponent memory, EntityCoordinates tile)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return;

        var config = blackboard.Config;
        var threshold = config.BlacklistThreshold;
        var ttl = TimeSpan.FromSeconds(config.BlacklistTtlSeconds);

        // No entity to blacklist; just drop the tile for now so the android tries another one.
        memory.KnownDirtyTiles.Remove(tile);
        memory.CurrentDirtyTile = null;

        blackboard.SetBranchCooldown("Work");
    }

}

/// <summary>
/// Activates a cleaner grenade that the android is holding and drops it at its feet so it
/// foams the center of a large puddle cluster.
/// </summary>
public sealed class UseCleanadeAction : IBehaviorNode
{
    /// <summary>
    /// How long the android should stay away from a primed cleanade so the foam can dissipate.
    /// The cleanade smoke duration is 5s; a small margin is added for the foam to finish cleaning.
    /// </summary>
    private static readonly TimeSpan CleanadeFleeDuration = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Minimum distance the android should keep from the primed cleanade.
    /// </summary>
    private const float CleanadeFleeDistance = 6f;

    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var equipped = inventory.EquippedItem;
        if (!equipped.HasValue || blackboard.EntityManager.Deleted(equipped.Value))
        {
            blackboard.SetBranchCooldown("Cleanade");
            return BehaviorStatus.Failure;
        }

        if (!blackboard.EntityManager.TryGetComponent<MetaDataComponent>(equipped.Value, out var meta) ||
            meta.EntityPrototype?.ID != "CleanerGrenade")
        {
            blackboard.SetBranchCooldown("Cleanade");
            return BehaviorStatus.Failure;
        }

        // Already primed; just record a safe fallback point and succeed.
        if (blackboard.EntityManager.HasComponent<ActiveTimerTriggerComponent>(equipped.Value))
        {
            RecordFleePoint(owner, blackboard, inventory);
            return BehaviorStatus.Success;
        }

        var interaction = blackboard.EntityManager.System<SharedInteractionSystem>();
        var hands = blackboard.EntityManager.System<SharedHandsSystem>();

        // Prime the grenade while it is in hand.
        if (!interaction.UseInHandInteraction(owner, equipped.Value))
        {
            blackboard.SetBranchCooldown("Cleanade");
            return BehaviorStatus.Failure;
        }

        // Leave it at the android's current position so it cleans the area around it.
        hands.TryDrop(owner, targetDropLocation: null, checkActionBlocker: false, doDropInteraction: false);

        RecordFleePoint(owner, blackboard, inventory);
        return BehaviorStatus.Success;
    }

    private static void RecordFleePoint(EntityUid owner, AndroidBlackboard blackboard, AndroidInventoryComponent inventory)
    {
        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return;

        var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);
        var gridUid = ownerXform.GridUid ?? ownerXform.ParentUid;
        if (!blackboard.EntityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            return;

        var random = blackboard.Random;
        var angle = random.NextFloat() * MathF.PI * 2f;
        var fleePos = ownerPos + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * CleanadeFleeDistance;
        var mapSys = blackboard.EntityManager.System<SharedMapSystem>();
        var tilePos = mapSys.WorldToTile(gridUid, grid, fleePos);
        fleePos = mapSys.TileCenterToVector(gridUid, grid, tilePos);

        inventory.CleanadeFleeTarget = new EntityCoordinates(gridUid, fleePos);
        inventory.CleanadeFleeUntil = blackboard.Timing.CurTime + CleanadeFleeDuration;
    }
}
