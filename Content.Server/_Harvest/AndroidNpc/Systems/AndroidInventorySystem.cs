// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Shared.Access.Systems;
using Content.Shared.Containers;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Chemistry;
using System.Diagnostics.CodeAnalysis;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Disposal.Components;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Fluids.EntitySystems;
using Content.Shared.Item;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Components;
using Content.Shared.PDA;
using Content.Shared.Roles;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Harvest.AndroidNpc.Systems;

/// <summary>
/// Manages tool equipping and loadout checks for androids.
/// </summary>
public sealed class AndroidInventorySystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedIdCardSystem _idCard = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly SharedAbsorbentSystem _absorbent = default!;
    [Dependency] private readonly SharedPuddleSystem _puddle = default!;
    [Dependency] private readonly DrainSystem _drain = default!;
    [Dependency] private readonly DumpableSystem _dumpable = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private static readonly TimeSpan MissingToolRecheck = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How far the android will look for a sink or disposal unit to service its tools at.
    /// This has to cover a decent chunk of the station because the android walks there.
    /// </summary>
    private const float ServiceSearchRadius = 64f;
    private const float ServiceSearchRadiusSquared = ServiceSearchRadius * ServiceSearchRadius;

    /// <summary>
    /// Below this much clean water the mop stops being able to make progress and is wrung out.
    /// </summary>
    private static readonly FixedPoint2 MopLowWater = FixedPoint2.New(20);

    /// <summary>
    /// The bucket is taken to a sink once its clean water drops below this fraction of capacity.
    /// </summary>
    private const float BucketLowWaterFraction = 0.25f;

    private static readonly ProtoId<ReagentPrototype> WaterReagent = "Water";

    /// <summary>
    /// Checks whether the android is missing any required loadout items.
    /// </summary>
    public bool HasMissingLoadout(EntityUid owner, AndroidInventoryComponent inventory)
    {
        var job = GetJobFor(owner);
        if (job == null)
            return false;

        var requiredCounts = new Dictionary<string, int>();
        foreach (var proto in job.RequiredLoadout)
        {
            requiredCounts[proto] = requiredCounts.GetValueOrDefault(proto) + 1;
        }

        foreach (var (proto, required) in requiredCounts)
        {
            if (CountItemsOfPrototype(owner, inventory, proto) < required)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the android is missing loadout items and has to visit its supply closet.
    /// Tool maintenance (wringing the mop, dumping the bucket, emptying the trash bag) is handled
    /// separately by the servicing API below, because that work happens at sinks and disposal units
    /// rather than at the resupply beacon.
    /// </summary>
    public bool NeedsResupply(EntityUid owner, AndroidInventoryComponent inventory)
    {
        if (GetJobFor(owner) is not { RequiredLoadout.Count: > 0 } job)
            return false;

        var missing = 0;
        foreach (var (proto, required) in job.RequiredLoadout.GroupBy(p => p).Select(g => (g.Key, g.Count())))
        {
            var owned = CountItemsOfPrototype(owner, inventory, proto);
            if (owned == 0)
                return true;

            missing += Math.Max(0, required - owned);
        }

        return missing > 0 && missing >= job.RequiredLoadout.Count * _prototypeManager.AndroidConfig().ResupplyThreshold;
    }

    /// <summary>
    /// Performs whatever tool maintenance can be done without travelling, i.e. wringing the mop out
    /// into the carried bucket. Anything that needs a sink or a disposal unit is driven by the
    /// behavior tree instead.
    /// </summary>
    public void ResupplyTools(EntityUid owner, AndroidInventoryComponent inventory)
    {
        TryWringMop(owner, inventory);
    }

    #region Mop

    /// <summary>
    /// Gets the android's mop, if it owns one.
    /// </summary>
    public bool TryGetMop(EntityUid owner, AndroidInventoryComponent inventory, out Entity<AbsorbentComponent> mop)
    {
        mop = default;

        foreach (var item in inventory.OwnedItems)
        {
            if (_entityManager.Deleted(item))
                continue;

            if (_entityManager.TryGetComponent<AbsorbentComponent>(item, out var absorbent))
            {
                mop = (item, absorbent);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the mop still holds enough absorbent reagent to make progress on a puddle.
    /// A mop with no water left does nothing at all when used, so this has to be checked before
    /// trying to clean, otherwise the android gets stuck scrubbing with a dirty mop.
    /// </summary>
    public bool CanMopClean(Entity<AbsorbentComponent> mop)
    {
        return GetAbsorbentVolume(mop.Owner, mop.Comp.SolutionName) > FixedPoint2.Zero;
    }

    /// <summary>
    /// Whether the mop is out of clean water (or close to it) and should be wrung out.
    /// </summary>
    public bool MopNeedsService(EntityUid owner, AndroidInventoryComponent inventory)
    {
        if (!TryGetMop(owner, inventory, out var mop))
            return false;

        return GetAbsorbentVolume(mop.Owner, mop.Comp.SolutionName) <= MopLowWater;
    }

    /// <summary>
    /// Wrings the mop out into the android's own bucket using the normal mopping interaction, which
    /// pushes the absorbed filth into the bucket and pulls clean water back into the mop.
    /// The bucket does not have to be held; it works from inside the android's bag.
    /// </summary>
    public bool TryWringMop(EntityUid owner, AndroidInventoryComponent inventory)
    {
        if (!TryGetMop(owner, inventory, out var mop))
            return false;

        if (!TryGetBucket(owner, inventory, out var bucket) || !CanBucketRefillMop(bucket))
            return false;

        var before = GetAbsorbentVolume(mop.Owner, mop.Comp.SolutionName);
        _absorbent.Mop(mop, owner, bucket);

        return GetAbsorbentVolume(mop.Owner, mop.Comp.SolutionName) > before;
    }

    #endregion

    #region Bucket

    /// <summary>
    /// Gets the android's mop bucket, i.e. an owned container the mop can be rinsed in.
    /// </summary>
    public bool TryGetBucket(EntityUid owner, AndroidInventoryComponent inventory, out EntityUid bucket)
    {
        bucket = default;

        foreach (var item in inventory.OwnedItems)
        {
            if (_entityManager.Deleted(item))
                continue;

            // The mop itself is refillable-adjacent; never rinse it into itself.
            if (_entityManager.HasComponent<AbsorbentComponent>(item))
                continue;

            if (!_entityManager.HasComponent<RefillableSolutionComponent>(item) ||
                !_entityManager.HasComponent<DrainableSolutionComponent>(item))
            {
                continue;
            }

            bucket = item;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the bucket still holds clean water the mop can pull from.
    /// </summary>
    public bool CanBucketRefillMop(EntityUid bucket)
    {
        if (!_solutions.TryGetRefillableSolution((bucket, null, null), out var soln, out _))
            return false;

        return GetAbsorbentVolume(soln.Value) > FixedPoint2.Zero;
    }

    /// <summary>
    /// Whether the bucket holds anything other than clean water and should be dumped out.
    /// </summary>
    public bool IsBucketDirty(EntityUid bucket)
    {
        if (!_solutions.TryGetDrainableSolution((bucket, null, null), out var soln, out var solution))
            return false;

        return solution.Volume - GetAbsorbentVolume(soln.Value) > FixedPoint2.Zero;
    }

    /// <summary>
    /// Whether the bucket has to be taken to a sink. Rinsing the mop always draws clean water out of
    /// the bucket before pushing filth back in, so the only thing that matters is how much clean
    /// water is left; a bucket brimming with water is fine even though it has no free volume.
    /// </summary>
    public bool BucketNeedsService(EntityUid owner, AndroidInventoryComponent inventory)
    {
        if (!TryGetBucket(owner, inventory, out var bucket))
            return false;

        if (!_solutions.TryGetRefillableSolution((bucket, null, null), out var soln, out var solution))
            return false;

        return GetAbsorbentVolume(soln.Value) < solution.MaxVolume * BucketLowWaterFraction;
    }

    /// <summary>
    /// Empties the bucket's dirty water into the station's plumbing and refills it with clean water.
    /// </summary>
    public bool TryServiceBucket(EntityUid owner, AndroidInventoryComponent inventory, EntityUid station)
    {
        if (!TryGetBucket(owner, inventory, out var bucket))
            return false;

        var serviced = false;

        if (IsBucketDirty(bucket) &&
            _entityManager.TryGetComponent<DrainComponent>(station, out var drain))
        {
            _drain.Empty((station, drain), owner, bucket);
            serviced = true;
        }

        if (TryFillBucket(bucket, station))
            serviced = true;

        return serviced;
    }

    private bool TryFillBucket(EntityUid bucket, EntityUid source)
    {
        if (!_solutions.TryGetRefillableSolution((bucket, null, null), out var bucketSoln, out var bucketSolution))
            return false;

        if (bucketSolution.AvailableVolume <= FixedPoint2.Zero)
            return false;

        if (!_solutions.TryGetDrainableSolution((source, null, null), out var sourceSoln, out var sourceSolution))
            return false;

        var reagents = _puddle.GetAbsorbentReagents(sourceSolution);
        var available = sourceSolution.GetTotalPrototypeQuantity(reagents);
        var toTake = FixedPoint2.Min(available, bucketSolution.AvailableVolume);

        if (toTake <= FixedPoint2.Zero)
            return false;

        var water = sourceSolution.SplitSolutionWithOnly(toTake, reagents);
        _solutions.UpdateChemicals(sourceSoln.Value);
        _solutions.TryAddSolution(bucketSoln.Value, water);

        return water.Volume > FixedPoint2.Zero;
    }

    /// <summary>
    /// Finds the nearest place the bucket can be serviced. A sink is ideal since it both drains and
    /// supplies water, but a bare drain or a bare water source is accepted when that is all the
    /// bucket currently needs.
    /// </summary>
    public bool TryFindBucketStation(EntityUid owner, AndroidInventoryComponent inventory, out EntityUid station)
    {
        station = default;

        if (!TryGetBucket(owner, inventory, out var bucket))
            return false;

        if (!_entityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return false;

        var needsDrain = IsBucketDirty(bucket);
        var ownerPos = _transform.GetWorldPosition(ownerXform);
        var ownerMap = ownerXform.MapUid;
        var ownerGrid = ownerXform.GridUid;

        var bestScore = -1;
        var bestDist = float.PositiveInfinity;

        var query = _entityManager.EntityQueryEnumerator<SolutionContainerManagerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var manager, out var xform))
        {
            if (uid == bucket || !IsReachableFixture(uid, xform, ownerMap, ownerGrid))
                continue;

            var canDrain = _entityManager.HasComponent<DrainComponent>(uid) && HasDrainSpace(uid);
            var canFill = HasCleanWater((uid, manager));

            // A dirty bucket must be dumped first, so a station that cannot drain is useless.
            if (needsDrain && !canDrain)
                continue;

            if (!needsDrain && !canFill)
                continue;

            var score = (canDrain ? 1 : 0) + (canFill ? 1 : 0);
            var distSquared = (_transform.GetWorldPosition(xform) - ownerPos).LengthSquared();

            if (distSquared > ServiceSearchRadiusSquared)
                continue;

            if (score < bestScore || (score == bestScore && distSquared >= bestDist))
                continue;

            bestScore = score;
            bestDist = distSquared;
            station = uid;
        }

        return bestScore > 0;
    }

    private bool HasDrainSpace(EntityUid uid)
    {
        // A clogged drain cannot take any more, so do not walk to it.
        return !_solutions.TryGetSolution((uid, null), DrainComponent.SolutionName, out _, out var solution) ||
               solution.AvailableVolume > FixedPoint2.Zero;
    }

    private bool HasCleanWater(Entity<SolutionContainerManagerComponent> ent)
    {
        if (!_solutions.TryGetDrainableSolution((ent.Owner, null, ent.Comp), out var soln, out _))
            return false;

        return GetAbsorbentVolume(soln.Value) > FixedPoint2.Zero;
    }

    #endregion

    #region Trash

    /// <summary>
    /// Gets the android's trash bag, if it owns one.
    /// </summary>
    public bool TryGetTrashBag(EntityUid owner, AndroidInventoryComponent inventory, out Entity<StorageComponent> bag)
    {
        bag = default;

        foreach (var item in inventory.OwnedItems)
        {
            if (_entityManager.Deleted(item))
                continue;

            if (!_entityManager.HasComponent<DumpableComponent>(item) ||
                !_entityManager.TryGetComponent<StorageComponent>(item, out var storage))
            {
                continue;
            }

            bag = (item, storage);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the trash bag is out of room and has to be emptied into a disposal unit.
    /// </summary>
    public bool TrashBagNeedsEmptying(EntityUid owner, AndroidInventoryComponent inventory)
    {
        if (!TryGetTrashBag(owner, inventory, out var bag))
            return false;

        if (bag.Comp.Container.ContainedEntities.Count == 0)
            return false;

        return !_storage.HasSpace((bag.Owner, bag.Comp));
    }

    /// <summary>
    /// Tips the trash bag into a disposal unit using the normal dumping interaction.
    /// </summary>
    public bool TryDumpTrashBag(EntityUid owner, AndroidInventoryComponent inventory, EntityUid disposal)
    {
        if (!TryGetTrashBag(owner, inventory, out var bag))
            return false;

        if (bag.Comp.Container.ContainedEntities.Count == 0)
            return false;

        if (!_entityManager.HasComponent<DisposalUnitComponent>(disposal))
            return false;

        _dumpable.DumpContents(bag.Owner, disposal, owner);

        return bag.Comp.Container.ContainedEntities.Count == 0;
    }

    /// <summary>
    /// Finds the nearest usable disposal unit on the android's grid.
    /// </summary>
    public bool TryFindDisposalUnit(EntityUid owner, out EntityUid disposal)
    {
        disposal = default;

        if (!_entityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return false;

        var ownerPos = _transform.GetWorldPosition(ownerXform);
        var ownerMap = ownerXform.MapUid;
        var ownerGrid = ownerXform.GridUid;
        var bestDist = float.PositiveInfinity;
        var found = false;

        var query = _entityManager.EntityQueryEnumerator<DisposalUnitComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var unitXform))
        {
            if (!IsReachableFixture(uid, unitXform, ownerMap, ownerGrid) || !unitXform.Anchored)
                continue;

            var distSquared = (_transform.GetWorldPosition(unitXform) - ownerPos).LengthSquared();
            if (distSquared > ServiceSearchRadiusSquared || distSquared >= bestDist)
                continue;

            bestDist = distSquared;
            disposal = uid;
            found = true;
        }

        return found;
    }

    #endregion

    /// <summary>
    /// Filters out entities that are held, stored, on another map/grid, or otherwise not a piece of
    /// station furniture the android could walk up to. The android's own grid is used rather than
    /// the parent so this works while the android is standing on a lattice but its grid is the floor.
    /// </summary>
    private bool IsReachableFixture(EntityUid uid, TransformComponent xform, EntityUid? ownerMap, EntityUid? ownerGrid)
    {
        if (_entityManager.Deleted(uid) ||
            _entityManager.HasComponent<MobStateComponent>(uid) ||
            _entityManager.HasComponent<ItemComponent>(uid))
        {
            return false;
        }

        if (ownerMap == null || xform.MapUid != ownerMap)
            return false;

        // Must be a fixture sitting directly on the same grid the android can path on.
        if (xform.GridUid is not { Valid: true } gridUid || xform.ParentUid != gridUid)
            return false;

        return ownerGrid == null || xform.GridUid == ownerGrid;
    }

    private FixedPoint2 GetAbsorbentVolume(EntityUid uid, string solutionName)
    {
        if (!_solutions.TryGetSolution((uid, null), solutionName, out var soln, out _))
            return FixedPoint2.Zero;

        return GetAbsorbentVolume(soln.Value);
    }

    private FixedPoint2 GetAbsorbentVolume(Entity<SolutionComponent> soln)
    {
        var solution = soln.Comp.Solution;

        return solution.GetTotalPrototypeQuantity(_puddle.GetAbsorbentReagents(solution));
    }

    /// <summary>
    /// Tries to find an owned item with the specified prototype. If it is not already registered,
    /// performs a full inventory rescan so gear that was filled into containers after the android
    /// spawned (e.g. the janitor belt) can still be found.
    /// </summary>
    public EntityUid? FindAndRegisterOwnedTool(EntityUid owner, AndroidInventoryComponent inventory, string prototype)
    {
        var item = FindItemByPrototype(owner, inventory, prototype);
        if (item.HasValue)
            return item;

        // A full rescan walks every container of the android; when the tool is missing, do not repeat it each tick.
        var now = _timing.CurTime;
        if (inventory.MissingToolRecheckAt.TryGetValue(prototype, out var recheck) && now < recheck)
            return null;

        inventory.MissingToolRecheckAt[prototype] = now + MissingToolRecheck;

        foreach (var candidate in CollectAllItems(owner))
        {
            if (_entityManager.Deleted(candidate) ||
                !_entityManager.TryGetComponent<MetaDataComponent>(candidate, out var meta) ||
                meta.EntityPrototype?.ID != prototype)
            {
                continue;
            }

            if (!inventory.OwnedItems.Contains(candidate))
                inventory.OwnedItems.Add(candidate);

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Tries to find an owned item with the specified prototype.
    /// </summary>
    public EntityUid? FindItemByPrototype(EntityUid owner, AndroidInventoryComponent inventory, string prototype)
    {
        foreach (var item in inventory.OwnedItems)
        {
            if (_entityManager.Deleted(item))
                continue;

            if (_entityManager.TryGetComponent<MetaDataComponent>(item, out var meta) &&
                meta.EntityPrototype?.ID == prototype)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Equips the specified item in the active hand, retrieving it from the android's own backpack
    /// or belt if needed. Items on the floor are only picked up from normal human interaction range,
    /// and the android will not drop a held tool to the floor unless it really cannot stow it.
    /// </summary>
    public bool Equip(EntityUid owner, AndroidInventoryComponent inventory, EntityUid item)
    {
        if (_entityManager.Deleted(item))
            return false;

        // Already being held? Just make it the active hand.
        if (_entityManager.TryGetComponent<HandsComponent>(owner, out var handsComp) &&
            _hands.IsHolding((owner, handsComp), item, out var hand))
        {
            if (!_hands.TrySetActiveHand((owner, handsComp), hand))
                return false;

            inventory.EquippedItem = item;
            return true;
        }

        var wasContained = false;

        // If the item is inside a container (e.g., backpack or hand), remove it first.
        if (_entityManager.TryGetComponent<TransformComponent>(item, out var itemXform) &&
            _entityManager.TryGetComponent<MetaDataComponent>(item, out var itemMeta) &&
            _containers.TryGetContainingContainer((item, itemXform, itemMeta), out var container))
        {
            if (!IsAndroidContainer(owner, container))
                return false;

            if (!_entityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
                return false;

            _containers.Remove(
                (item, itemXform, itemMeta),
                container,
                reparent: true,
                force: true,
                destination: ownerXform.Coordinates);

            wasContained = true;
        }

        // Item is on the floor. Make sure it is within normal interaction range before picking it up
        // so androids cannot magically retrieve dropped tools from across the room.
        if (!wasContained)
        {
            if (!_interaction.InRangeUnobstructed(owner, item, SharedInteractionSystem.InteractionRange))
                return false;
        }

        // Try a normal pickup first. If there is no free hand, try to stow the current active tool
        // in the android's backpack or belt so it does not get dropped on the floor.
        if (_hands.TryPickupAnyHand(owner, item, handsComp: handsComp))
        {
            _hands.TrySelect(owner, item);
            inventory.EquippedItem = item;
            return true;
        }

        if (handsComp != null && TryStowHeldItem(owner, handsComp, item))
        {
            if (_hands.TryPickupAnyHand(owner, item, handsComp: handsComp))
            {
                _hands.TrySelect(owner, item);
                inventory.EquippedItem = item;
                return true;
            }
        }

        // Last resort: force a drop if the backpack is full or there is nowhere to stow.
        if (!_hands.TryForcePickupAnyHand(owner, item, handsComp: handsComp))
            return false;

        _hands.TrySelect(owner, item);
        inventory.EquippedItem = item;
        return true;
    }

    private bool IsAndroidContainer(EntityUid owner, BaseContainer container)
    {
        if (container.Owner == owner)
            return true;

        // Backpacks and belts are children of the android (container owner -> owner).
        if (_entityManager.TryGetComponent<TransformComponent>(container.Owner, out var cXform) &&
            cXform.ParentUid == owner)
        {
            return true;
        }

        return false;
    }

    private bool TryStowHeldItem(EntityUid owner, HandsComponent hands, EntityUid except)
    {
        var activeItem = _hands.GetActiveItem((owner, hands));
        if (activeItem.HasValue && activeItem.Value != except && TryStowItem(owner, activeItem.Value, hands))
            return true;

        foreach (var handId in hands.Hands.Keys)
        {
            var held = _hands.GetHeldItem((owner, hands), handId);
            if (held == except || !held.HasValue)
                continue;

            if (TryStowItem(owner, held.Value, hands))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Makes sure a hand is free, e.g. to pull a body: puts a held item in the bag, keeping the job's weapon
    /// (the mop) in hand when possible, and drops one only when the bag is full.
    /// </summary>
    public bool TryFreeHand(EntityUid owner)
    {
        if (!_entityManager.TryGetComponent<HandsComponent>(owner, out var hands))
            return false;

        if (_hands.TryGetEmptyHand((owner, hands), out _))
            return true;

        var weapon = GetJobFor(owner)?.RetaliationWeapon;
        var held = hands.Hands.Keys
            .Select(hand => (Hand: hand, Item: _hands.GetHeldItem((owner, hands), hand)))
            .Where(h => h.Item is { } item && !_entityManager.HasComponent<VirtualItemComponent>(item))
            .OrderBy(h => _entityManager.GetComponent<MetaDataComponent>(h.Item!.Value).EntityPrototype?.ID == weapon?.Id)
            .ToList();

        foreach (var (_, item) in held)
        {
            if (TryStowItem(owner, item!.Value, hands))
                return true;
        }

        return held.Count > 0 && _hands.TryDrop((owner, hands), held[0].Hand);
    }

    /// <summary>
    /// Puts away whatever the android holds besides <paramref name="keep"/>, so a two-handed weapon can be wielded.
    /// Items that do not fit in the bag are dropped.
    /// </summary>
    public void FreeOtherHands(EntityUid owner, EntityUid keep)
    {
        if (!_entityManager.TryGetComponent<HandsComponent>(owner, out var hands))
            return;

        foreach (var handId in hands.Hands.Keys.ToList())
        {
            if (_hands.GetHeldItem((owner, hands), handId) is not { } held || held == keep)
                continue;

            if (!TryStowItem(owner, held, hands))
                _hands.TryDrop((owner, hands), handId);
        }
    }

    /// <summary>
    /// Picks the item up and puts it in the android's bag or belt, keeping it in hand if they are full.
    /// </summary>
    public bool TryPocket(EntityUid owner, EntityUid item)
    {
        if (!_entityManager.TryGetComponent<HandsComponent>(owner, out var hands) ||
            !_interaction.InRangeUnobstructed(owner, item, SharedInteractionSystem.InteractionRange) ||
            !_hands.TryPickupAnyHand(owner, item, handsComp: hands))
        {
            return false;
        }

        TryStowItem(owner, item, hands);
        return true;
    }

    private bool TryStowItem(EntityUid owner, EntityUid item, HandsComponent hands)
    {
        foreach (var slot in new[] { "back", "belt", "pocket1", "pocket2" })
        {
            if (!_inventory.TryGetSlotEntity(owner, slot, out var slotItem))
                continue;

            if (!_entityManager.TryGetComponent<StorageComponent>(slotItem, out var storage))
                continue;

            if (_hands.TryDropIntoContainer((owner, hands), item, storage.Container))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the android is currently holding an item that can be used as a melee weapon.
    /// </summary>
    public bool TryGetWeaponInHand(EntityUid owner, out EntityUid weapon)
    {
        weapon = default;

        if (!_hands.TryGetActiveItem(owner, out var held) || !_entityManager.EntityExists(held.Value))
            return false;

        if (!_entityManager.TryGetComponent<MeleeWeaponComponent>(held.Value, out var melee))
            return false;

        if (melee.MustBeEquippedToUse)
            return false;

        weapon = held.Value;
        return true;
    }

    /// <summary>
    /// Returns the currently equipped item, if any.
    /// </summary>
    public EntityUid? GetEquippedItem(EntityUid owner)
    {
        if (!_hands.TryGetActiveItem(owner, out var item))
            return null;

        return item;
    }

    /// <summary>
    /// Spawns and collects all required loadout items for the android, preferring the backpack.
    /// </summary>
    public void InitializeLoadout(EntityUid owner, AndroidInventoryComponent inventory)
    {
        RestockLoadout(owner, inventory);
    }

    /// <summary>
    /// Ensures the android owns the exact quantity of every prototype listed in the job's required loadout.
    /// Items are first collected from the android's inventory and containers; missing items are spawned
    /// and placed inside the backpack if one is available.
    /// </summary>
    public void RestockLoadout(EntityUid owner, AndroidInventoryComponent inventory)
    {
        var job = GetJobFor(owner);
        if (job == null)
            return;

        if (!_entityManager.TryGetComponent<TransformComponent>(owner, out var xform))
            return;

        var requiredCounts = new Dictionary<string, int>();
        foreach (var proto in job.RequiredLoadout)
        {
            requiredCounts[proto] = requiredCounts.GetValueOrDefault(proto) + 1;
        }

        // Find every item the android already owns, whether in hands, equipment, or nested storage.
        var collected = CollectAllItems(owner);
        var collectedByProto = new Dictionary<string, List<EntityUid>>();

        inventory.OwnedItems.Clear();

        foreach (var item in collected)
        {
            if (_entityManager.Deleted(item) ||
                !_entityManager.TryGetComponent<MetaDataComponent>(item, out var meta) ||
                meta.EntityPrototype?.ID is not { } protoId)
            {
                continue;
            }

            if (!requiredCounts.ContainsKey(protoId))
                continue;

            if (!collectedByProto.TryGetValue(protoId, out var list))
            {
                list = new List<EntityUid>();
                collectedByProto[protoId] = list;
            }

            list.Add(item);
        }

        foreach (var (proto, items) in collectedByProto)
        {
            var needed = requiredCounts.GetValueOrDefault(proto);
            for (var i = 0; i < Math.Min(items.Count, needed); i++)
            {
                inventory.OwnedItems.Add(items[i]);
                PrepareTool(items[i]);
            }
        }

        // Register every piece of gear the android is carrying so optional tools like soap, spray,
        // flashlight and light replacer can be found and equipped later, even if they are not part
        // of the required loadout.
        foreach (var item in collected)
        {
            if (!inventory.OwnedItems.Contains(item))
                inventory.OwnedItems.Add(item);
        }

        // Find the backpack so missing items can be stashed there instead of dropped at the feet.
        EntityUid? satchel = null;
        if (_inventory.TryGetSlotEntity(owner, "back", out var backUid) &&
            _entityManager.HasComponent<StorageComponent>(backUid))
        {
            satchel = backUid;
        }

        foreach (var (proto, required) in requiredCounts)
        {
            var existing = CountItemsOfPrototype(owner, inventory, proto);
            for (var i = existing; i < required; i++)
            {
                var item = _entityManager.SpawnEntity(proto, xform.Coordinates);
                inventory.OwnedItems.Add(item);
                PrepareTool(item);

                // The mop should be held; everything else goes in the bag.
                if (proto == "MopItem" && _hands.TryPickupAnyHand(owner, item))
                {
                    _hands.TrySelect(owner, item);
                    continue;
                }

                if (satchel.HasValue)
                {
                    _storage.Insert(satchel.Value, item, out _, user: owner, playSound: false);
                }
                else
                {
                    _hands.TryPickupAnyHand(owner, item);
                }
            }
        }
    }

    /// <summary>
    /// Updates the android's PDA and ID card to match its assigned identity.
    /// </summary>
    public void UpdatePdaAndId(EntityUid owner, AndroidIdentityComponent identity)
    {
        if (!_inventory.TryGetSlotEntity(owner, "id", out var pdaUid) ||
            !_entityManager.TryGetComponent<PdaComponent>(pdaUid, out var pda))
        {
            return;
        }

        if (pda.ContainedId is not { } idCardUid ||
            !_entityManager.TryGetComponent<Content.Shared.Access.Components.IdCardComponent>(idCardUid, out var idCard))
        {
            return;
        }

        var jobProtoId = GetJobPrototypeFor(owner, identity.JobTitle);
        var jobProto = _prototypeManager.Index<JobPrototype>(jobProtoId);

        _idCard.TryChangeFullName(idCardUid, identity.FullDisplayName, idCard);
        _idCard.TryChangeJobTitle(idCardUid, identity.JobTitle, idCard);
        _idCard.TryChangeJobDepartment(idCardUid, jobProto, idCard);

        idCard.JobPrototype = jobProto.ID;
        idCard.JobIcon = jobProto.Icon;
        Dirty(idCardUid, idCard);
    }

    private void PrepareTool(EntityUid item)
    {
        // Fill the janitor's mop with water so it can mop properly.
        if (_entityManager.TryGetComponent<AbsorbentComponent>(item, out var absorbent) &&
            _solutions.TryGetSolution((item, null), absorbent.SolutionName, out var soln, out _))
        {
            FillWithWater(soln.Value);
            return;
        }

        // Fill the janitor's bucket with water.
        if (_solutions.TryGetRefillableSolution((item, null, null), out var bucketSoln, out _))
            FillWithWater(bucketSoln.Value);
    }

    private void FillWithWater(Entity<SolutionComponent> soln)
    {
        // Only pre-fill fresh tools that are completely empty; refills are handled by tool servicing.
        if (soln.Comp.Solution.Volume != FixedPoint2.Zero)
            return;

        var max = soln.Comp.Solution.MaxVolume;
        if (max <= FixedPoint2.Zero)
            return;

        var water = new Solution(WaterReagent, max);
        _solutions.ForceAddSolution(soln, water);
    }

    private int CountItemsOfPrototype(EntityUid owner, AndroidInventoryComponent inventory, string prototype)
    {
        var count = 0;

        foreach (var item in inventory.OwnedItems)
        {
            if (_entityManager.Deleted(item))
                continue;

            if (_entityManager.TryGetComponent<MetaDataComponent>(item, out var meta) &&
                meta.EntityPrototype?.ID == prototype)
            {
                count++;
            }
        }

        // Placed wet floor signs are still owned by the android.
        if (prototype == "WetFloorSign")
        {
            foreach (var item in inventory.PlacedSigns)
            {
                if (!_entityManager.Deleted(item) &&
                    _entityManager.TryGetComponent<MetaDataComponent>(item, out var meta) &&
                    meta.EntityPrototype?.ID == prototype)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Hands one carried item matching the given prototypes to the recipient, preferring an item that is not
    /// the active tool. If the recipient is out of reach or has no free hand, the item is dropped at the android's feet.
    /// </summary>
    public bool TryGiveItem(
        EntityUid owner,
        AndroidInventoryComponent inventory,
        ICollection<string> prototypes,
        EntityUid recipient,
        float handOverRange,
        [NotNullWhen(true)] out EntityUid? given)
    {
        given = null;
        EntityUid? equipped = null;

        foreach (var item in CollectAllItems(owner))
        {
            if (_entityManager.Deleted(item) ||
                !_entityManager.TryGetComponent<MetaDataComponent>(item, out var meta) ||
                meta.EntityPrototype is not { } proto ||
                !prototypes.Contains(proto.ID))
            {
                continue;
            }

            if (item == inventory.EquippedItem)
            {
                equipped ??= item;
                continue;
            }

            given = item;
            break;
        }

        given ??= equipped;

        if (given is not { } gift || !_entityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
        {
            given = null;
            return false;
        }

        if (_entityManager.TryGetComponent<TransformComponent>(gift, out var giftXform) &&
            _entityManager.TryGetComponent<MetaDataComponent>(gift, out var giftMeta) &&
            _containers.TryGetContainingContainer((gift, giftXform, giftMeta), out var container))
        {
            _containers.Remove((gift, giftXform, giftMeta), container, reparent: true, force: true, destination: ownerXform.Coordinates);
        }

        inventory.OwnedItems.Remove(gift);
        if (inventory.EquippedItem == gift)
            inventory.EquippedItem = null;

        if (_interaction.InRangeUnobstructed(owner, recipient, handOverRange))
            _hands.TryPickupAnyHand(recipient, gift);

        return true;
    }

    private List<EntityUid> CollectAllItems(EntityUid root)
    {
        var items = new List<EntityUid>();
        var queue = new Queue<EntityUid>();
        var seen = new HashSet<EntityUid>();

        queue.Enqueue(root);
        seen.Add(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!_entityManager.TryGetComponent<ContainerManagerComponent>(current, out var containerManager))
                continue;

            foreach (var container in _containers.GetAllContainers(current, containerManager))
            {
                foreach (var entity in container.ContainedEntities)
                {
                    if (!seen.Add(entity))
                        continue;

                    items.Add(entity);
                    queue.Enqueue(entity);
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Finds the <see cref="AndroidJobPrototype"/> matching the android's job.
    /// </summary>
    public AndroidJobPrototype? GetJobFor(EntityUid owner)
    {
        if (!_entityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return null;

        foreach (var proto in _prototypeManager.EnumeratePrototypes<AndroidJobPrototype>())
        {
            if (proto.Job == android.Job)
                return proto;
        }

        return null;
    }

    private string GetJobPrototypeFor(EntityUid owner, string jobTitle)
    {
        // Try to find a matching SS14 job prototype from the title.
        foreach (var proto in _prototypeManager.EnumeratePrototypes<JobPrototype>())
        {
            if (proto.Name == jobTitle)
                return proto.ID;
        }

        return "Janitor";
    }

    /// <summary>
    /// Returns true if the android has an active do-after for a tool it owns or is holding
    /// (e.g., scrubbing a floor with a mop or soap). If found, the tool that started the do-after
    /// is returned so the caller can make sure the android keeps that item in hand.
    /// </summary>
    public bool IsToolDoAfterRunning(EntityUid owner, AndroidInventoryComponent inventory, [NotNullWhen(true)] out EntityUid? runningTool)
    {
        runningTool = null;

        if (!_entityManager.TryGetComponent<DoAfterComponent>(owner, out var doAfterComp))
            return false;

        foreach (var doAfter in doAfterComp.DoAfters.Values)
        {
            if (doAfter.CancelledTime != null || doAfter.Completed)
                continue;

            if (doAfter.Args.Used is not { Valid: true } used)
                continue;

            if (_entityManager.Deleted(used))
                continue;

            if (!inventory.OwnedItems.Contains(used) && used != inventory.EquippedItem)
                continue;

            runningTool = used;
            return true;
        }

        return false;
    }
}
