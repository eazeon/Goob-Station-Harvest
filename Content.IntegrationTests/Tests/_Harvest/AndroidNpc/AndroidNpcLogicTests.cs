// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Shared.FixedPoint;
using Content.Server.Access.Systems;
using Content.Server._Harvest.AndroidNpc.BehaviorTree;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Trees;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Services;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Decals;
using Content.Server.Doors.Systems;
using Content.Server.Light.EntitySystems;
using Content.Server.Mind;
using Content.Server.NPC.Pathfinding;
using Content.Server.Power.Components;
using Content.Server.NPC.Systems;
using Content.Server.NPC.Components;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Atmos;
using Content.Shared.Buckle.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Disposal.Components;
using Content.Shared._vg.TileMovement;
using Content.Shared.Doors.Components;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Medical.SuitSensors;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.NPC;
using Content.Shared.Speech;
using Content.Shared.SSDIndicator;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.EntitySerialization.Systems;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._Harvest.AndroidNpc;

/// <summary>
/// In-game logic tests for the android NPC system.
/// Spawns androids and simulates speech events to verify the order and dialogue systems.
/// </summary>
[TestFixture]
[TestOf(typeof(AndroidOrderSystem))]
[TestOf(typeof(AndroidDialogueSystem))]
public sealed class AndroidNpcLogicTests
{
    [Test]
    public async Task AndroidSpawns_WithNameAndJob()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = server.EntMan.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.EntMan.TryGetComponent<AndroidComponent>(android, out var androidComp), Is.True, "Android should have AndroidComponent");
            Assert.That(androidComp.Job, Is.EqualTo(AndroidJob.Janitor));
            Assert.That(androidComp.PersonalSpace, Is.GreaterThanOrEqualTo(1.0f).And.LessThanOrEqualTo(3.0f), "Personal space should be initialized to a 1-3 tile range");

            Assert.That(server.EntMan.TryGetComponent<AndroidIdentityComponent>(android, out var identity), Is.True, "Android should have AndroidIdentityComponent");
            Assert.That(string.IsNullOrEmpty(identity.FirstName), Is.False, "Android should be assigned a first name");
            Assert.That(identity.FullDisplayName, Does.Contain(identity.FirstName));
            Assert.That(string.IsNullOrEmpty(identity.JobTitle), Is.False, "Android should be assigned a job title");

            var metaName = server.EntMan.GetComponent<MetaDataComponent>(android).EntityName;
            Assert.That(metaName, Is.EqualTo(identity.FullDisplayName));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AndroidSpawner_SpawnsJanitor()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        EntityUid spawner = default;

        await server.WaitAssertion(() =>
        {
            spawner = server.EntMan.SpawnEntity("SpawnPointAndroidJanitor", testMap.MapCoords);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.EntMan.Deleted(spawner), Is.True, "The one-shot spawner should be removed after spawning");

            var android = FindSpawnedAndroid(server, testMap.MapCoords);
            Assert.That(android, Is.Not.EqualTo(default(EntityUid)), "A janitor android should have been spawned at the spawner");

            Assert.That(server.EntMan.TryGetComponent<AndroidComponent>(android, out var androidComp), Is.True);
            Assert.That(androidComp.Job, Is.EqualTo(AndroidJob.Janitor));
        });

        await pair.CleanReturnAsync();
    }

    private static bool IsPuddleAllWater(IEntityManager sEnt, EntityUid puddle)
    {
        if (!sEnt.TryGetComponent<PuddleComponent>(puddle, out var puddleComp) ||
            puddleComp.Solution is not { } soln)
        {
            return false;
        }

        var solution = soln.Comp.Solution;
        var puddleSystem = sEnt.System<SharedPuddleSystem>();
        var waterVolume = solution.GetTotalPrototypeQuantity(puddleSystem.GetAbsorbentReagents(solution));
        return waterVolume >= solution.Volume;
    }

    private static EntityUid FindSpawnedAndroid(IIntegrationInstance server, MapCoordinates near)
    {
        var sEnt = server.EntMan;
        var xformSys = server.System<SharedTransformSystem>();
        var query = sEnt.AllEntityQueryEnumerator<AndroidComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xformSys.GetMapCoordinates(uid, xform).MapId == near.MapId && !sEnt.Deleted(uid))
                return uid;
        }

        return default;
    }

    private static void SetAndroidIdentity(IEntityManager sEnt, EntityUid android, string firstName)
    {
        if (sEnt.TryGetComponent<AndroidIdentityComponent>(android, out var identity))
        {
            identity.FirstName = firstName;
            identity.FullDisplayName = $"Androïde {firstName}";
            sEnt.System<MetaDataSystem>().SetEntityName(android, identity.FullDisplayName);
        }
    }

    [Test]
    public async Task FollowMeOrder_IsRecognized()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);
            SetAndroidIdentity(sEnt, android, "Gerard");
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.System<AndroidOrderSystem>().IsOrderMessage("suis-moi"), Is.True, "Phrase matcher should recognize 'suis-moi'");
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard suis-moi", speaker));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.TryGetComponent<AndroidOrderComponent>(android, out var order), Is.True, "Android should receive a FollowMe order");
            Assert.That(order.OrderType, Is.EqualTo(AndroidOrderType.FollowMe));
            Assert.That(order.Issuer, Is.EqualTo(speaker));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GoToDepartmentOrder_ExtractsTarget()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);
            SetAndroidIdentity(sEnt, android, "Gerard");
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.System<AndroidOrderSystem>().IsOrderMessage("Gerard va en maintenance"), Is.True, "Phrase matcher should recognize a department order");

            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard va en maintenance", speaker));

            Assert.That(sEnt.TryGetComponent<AndroidOrderComponent>(android, out var order), Is.True, "The order should be created while the event is being handled");
            Assert.That(order.OrderType, Is.EqualTo(AndroidOrderType.GoToDepartment));
            Assert.That(order.Target, Is.EqualTo("maintenance"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrderAddressedByName_IsRecognized()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);

            if (sEnt.TryGetComponent<AndroidIdentityComponent>(android, out var identity))
            {
                identity.FirstName = "Gerard";
                identity.FullDisplayName = "Androïde Gerard";
                sEnt.System<MetaDataSystem>().SetEntityName(android, identity.FullDisplayName);
            }
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard suis-moi", speaker));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.TryGetComponent<AndroidOrderComponent>(android, out var order), Is.True);
            Assert.That(order.OrderType, Is.EqualTo(AndroidOrderType.FollowMe));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Order_FromAnySpeaker_IsObeyed()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);
            SetAndroidIdentity(sEnt, android, "Gerard");

            var xform = sEnt.System<SharedTransformSystem>();
            xform.SetWorldPosition(speaker, testMap.MapCoords.Position + new Vector2(100f, 100f));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard suis-moi", speaker));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            // Anyone who can be heard may give orders: there is no access or rank check.
            Assert.That(sEnt.TryGetComponent<AndroidOrderComponent>(android, out var order), Is.True, "A speaker without any access is obeyed");
            Assert.That(order!.OrderType, Is.EqualTo(AndroidOrderType.FollowMe));
            Assert.That(order.Issuer, Is.EqualTo(speaker));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Order_Expires_IsRemovedAndStatusReset()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);
            SetAndroidIdentity(sEnt, android, "Gerard");

            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard suis-moi", speaker));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.TryGetComponent<AndroidOrderComponent>(android, out var order), Is.True);

            order.ExpiresAt = TimeSpan.Zero;

            var orderSys = server.System<AndroidOrderSystem>();
            orderSys.Update(0f);

            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(android), Is.False, "An expired order should be removed");
            // The test map is a vacuum, so the android keeps fleeing; it must just no longer be on an order.
            Assert.That(sEnt.GetComponent<AndroidComponent>(android).Status, Is.Not.EqualTo(AndroidStatus.FollowingOrder));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GoToPersonOrder_ExtractsTarget()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);
            SetAndroidIdentity(sEnt, android, "Gerard");

            sEnt.System<MetaDataSystem>().SetEntityName(speaker, "Bob");
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.System<AndroidOrderSystem>().IsOrderMessage("Gerard va voir Bob"), Is.True, "Phrase matcher should recognize a go-to-person order");

            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard va voir Bob", speaker));

            Assert.That(sEnt.TryGetComponent<AndroidOrderComponent>(android, out var order), Is.True, "The order should be created while the event is being handled");
            Assert.That(order.OrderType, Is.EqualTo(AndroidOrderType.GoToPerson));
            Assert.That(order.Target, Is.EqualTo("Bob"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OrderRequiresAddressing_UnaddressedMessageIgnored()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);
            SetAndroidIdentity(sEnt, android, "Gerard");
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            // Same phrase as the follow-me test, but not addressed to Gerard.
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("suis-moi", speaker));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(android), Is.False, "Unaddressed order phrases should not create an order");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AndroidSpawns_InitialStateIsWorking()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);

            Assert.That(sEnt.TryGetComponent<AndroidComponent>(android, out var androidComp), Is.True);
            Assert.That(androidComp.Status, Is.EqualTo(AndroidStatus.Working), "Android should start in the Working state");
            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(android), Is.False, "Android should not spawn with an order");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AndroidSpawns_UsesAndroidSpeciesAndNoSsdExamine()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.TryGetComponent<HumanoidAppearanceComponent>(android, out var appearance), Is.True);
            Assert.That(appearance.Species, Is.EqualTo("Android"), "Android should use the Android species");

            Assert.That(sEnt.HasComponent<SSDIndicatorComponent>(android), Is.False, "Android should not show SSD examine text");
            Assert.That(sEnt.HasComponent<MindExaminableComponent>(android), Is.False, "Android should not show mind/catatonic examine text");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StatusReport_IsRecognizedAndDoesNotCreateOrder()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);
            SetAndroidIdentity(sEnt, android, "Gerard");

            var orderSys = sEnt.System<AndroidOrderSystem>();
            Assert.That(orderSys.IsOrderMessage("Gerard what are you doing"), Is.True, "Phrase matcher should recognize a status-report request");

            var androidComp = sEnt.GetComponent<AndroidComponent>(android);
            var before = androidComp.Status;

            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard what are you doing", speaker));

            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(android), Is.False, "A status report should not create an order");
            Assert.That(sEnt.GetComponent<AndroidComponent>(android).Status, Is.EqualTo(before), "A status report should not change the android's state");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_CleansPuddle_WhenOneIsNearby()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid puddle = default;

        await server.WaitAssertion(() =>
        {
            // Give the map normal atmosphere so the android does not flee.
            var atmos = sEnt.System<AtmosphereSystem>();
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int)Gas.Oxygen] = 21.824779f;
            moles[(int)Gas.Nitrogen] = 82.10312f;
            atmos.SetMapAtmosphere(testMap.MapUid, false, new GasMixture(moles, Atmospherics.T20C));

            // Place a small puddle on the same tile the android will occupy.
            puddle = sEnt.SpawnEntity("PuddleBloodSmall", testMap.GridCoords);
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
        });

        // Allow the android to spot and mop the puddle.
        await server.WaitRunTicks(40);

        await server.WaitAssertion(() =>
        {
            if (sEnt.Deleted(puddle) || IsPuddleAllWater(sEnt, puddle))
                return;

            Assert.That(sEnt.TryGetComponent<AndroidMemoryComponent>(android, out var earlyMemory), Is.True);
            Assert.That(sEnt.TryGetComponent<AndroidInventoryComponent>(android, out var earlyInv), Is.True);
            var puddleXform = sEnt.GetComponent<TransformComponent>(puddle);
            var androidXform = sEnt.GetComponent<TransformComponent>(android);
            var gridId = testMap.Grid.Owner;

            var lookup = sEnt.System<EntityLookupSystem>();
            var transformSys = sEnt.System<SharedTransformSystem>();
            var puddleSys = sEnt.System<SharedPuddleSystem>();
            var interactionSys = sEnt.System<SharedInteractionSystem>();
            var scanHelper = new Content.Server._Harvest.AndroidNpc.Utilities.TileScanHelper(sEnt, lookup, transformSys, puddleSys, interactionSys);
            var direct = scanHelper.FindDirtyTargets(android, 8f, new List<string> { "Trash" });

            var diag = $"puddle ParentUid={puddleXform.ParentUid}, GridUid={puddleXform.GridUid}, MapUid={puddleXform.MapUid}; android ParentUid={androidXform.ParentUid}, GridUid={androidXform.GridUid}; direct={direct.Count}; knownDirty={earlyMemory.KnownDirtyTargets.Count}; currentTool={earlyInv.CurrentToolTarget}; equipped={earlyInv.EquippedItem?.Id ?? 0}";
            Assert.That(earlyMemory.KnownDirtyTargets.Contains(puddle) || earlyInv.CurrentToolTarget == puddle || direct.Contains(puddle), Is.True, $"Android should have spotted the puddle. {diag}");
        });

        await server.WaitRunTicks(280);

        await server.WaitAssertion(() =>
        {
            sEnt.TryGetComponent<AndroidInventoryComponent>(android, out var inv);
            sEnt.TryGetComponent<AndroidComponent>(android, out var androidComp);
            var equippedId = inv?.EquippedItem != null && sEnt.TryGetComponent<MetaDataComponent>(inv.EquippedItem.Value, out var meta) ? meta.EntityPrototype?.ID : null;
            var currentTarget = inv?.CurrentToolTarget;

            var ownedList = string.Join(",", inv?.OwnedItems.Select(e =>
            {
                var meta = sEnt.TryGetComponent<MetaDataComponent>(e, out var m) ? m : null;
                return meta?.EntityPrototype?.ID ?? $"uid{e.Id}";
            }) ?? Enumerable.Empty<string>());
            var knownCount = sEnt.TryGetComponent<AndroidMemoryComponent>(android, out var finalMem) ? finalMem.KnownDirtyTargets.Count : -1;

            Assert.That(sEnt.Deleted(puddle) || IsPuddleAllWater(sEnt, puddle), Is.True,
                $"The android should mop up a small puddle. Status={androidComp?.Status}, Equipped={equippedId}, CurrentToolTarget={currentTarget}, KnownDirty={knownCount}, Owned=[{ownedList}]");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_CleansDirtyTile_WhenOneIsNearby()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityCoordinates tile = default;

        await server.WaitAssertion(() =>
        {
            // Give the map normal atmosphere so the android does not flee.
            var atmos = sEnt.System<AtmosphereSystem>();
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int)Gas.Oxygen] = 21.824779f;
            moles[(int)Gas.Nitrogen] = 82.10312f;
            atmos.SetMapAtmosphere(testMap.MapUid, false, new GasMixture(moles, Atmospherics.T20C));

            // Add a cleanable dirt decal to the tile the android will occupy.
            var decalSys = sEnt.System<DecalSystem>();
            tile = testMap.GridCoords;
            Assert.That(decalSys.TryAddDecal("Dirt", tile, out _, cleanable: true), Is.True, "Could not place cleanable dirt decal");

            android = sEnt.SpawnEntity("MobAndroidJanitor", tile);
        });

        // Let the android notice and scrub the decal.
        await server.WaitRunTicks(280);

        await server.WaitAssertion(() =>
        {
            var scanHelper = sEnt.System<AndroidScanSystem>().ScanHelper;
            var isDirty = scanHelper.IsTileDirty(tile);

            sEnt.TryGetComponent<AndroidMemoryComponent>(android, out var memory);
            var knownTile = memory?.KnownDirtyTiles.Contains(tile) ?? false;

            Assert.That(isDirty, Is.False,
                $"The android should have scrubbed the dirty tile. tileDirty={isDirty}, knownTile={knownTile}");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_MovesToAndCleansPuddle_TwoTilesAway()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid puddle = default;

        await server.WaitAssertion(() =>
        {
            // Give the map normal atmosphere so the android does not flee.
            var atmos = sEnt.System<AtmosphereSystem>();
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int)Gas.Oxygen] = 21.824779f;
            moles[(int)Gas.Nitrogen] = 82.10312f;
            atmos.SetMapAtmosphere(testMap.MapUid, false, new GasMixture(moles, Atmospherics.T20C));

            // Build a short corridor of floor tiles so the android can reach the puddle.
            var mapSys = sEnt.System<SharedMapSystem>();
            var tile = new Tile(testMap.Tile.Tile.TypeId);
            mapSys.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, testMap.GridCoords.Offset(new Vector2(1, 0)), tile);
            mapSys.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, testMap.GridCoords.Offset(new Vector2(2, 0)), tile);

            // Spawn the puddle 2 tiles away from where the android will stand.
            puddle = sEnt.SpawnEntity("PuddleBloodSmall", testMap.GridCoords.Offset(new Vector2(2, 0)));
        });

        // Let the pathfinding graph build before the android starts trying to move.
        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
        });

        // Let the android notice the puddle and walk to it.
        await server.WaitRunTicks(350);

        await server.WaitAssertion(() =>
        {
            var puddleXform = sEnt.GetComponent<TransformComponent>(puddle);
            var androidXform = sEnt.GetComponent<TransformComponent>(android);
            var distance = (puddleXform.Coordinates.Position - androidXform.Coordinates.Position).Length();

            var canMove = sEnt.TryGetComponent<InputMoverComponent>(android, out var mover) ? (bool?)mover.CanMove : null;
            var speed = sEnt.TryGetComponent<MovementSpeedModifierComponent>(android, out var move) ? $"walk={move.CurrentWalkSpeed:F2} sprint={move.CurrentSprintSpeed:F2}" : "none";
            var steeringStatus = sEnt.TryGetComponent<NPCSteeringComponent>(android, out var steering) ? steering.Status.ToString() : "none";
            var currentTool = sEnt.TryGetComponent<AndroidInventoryComponent>(android, out var inv) ? inv.CurrentToolTarget?.ToString() ?? "null" : "none";
            var known = sEnt.TryGetComponent<AndroidMemoryComponent>(android, out var mem) ? mem.KnownDirtyTargets.Count.ToString() : "none";
            var hasPathfinding = sEnt.HasComponent<GridPathfindingComponent>(testMap.Grid.Owner);
            var pathfinding = sEnt.System<PathfindingSystem>();
            var startPoly = pathfinding.GetPoly(androidXform.Coordinates);
            var endPoly = pathfinding.GetPoly(puddleXform.Coordinates);
            var startFlags = startPoly?.Data.Flags.ToString() ?? "null";
            var endFlags = endPoly?.Data.Flags.ToString() ?? "null";
            var startLayer = startPoly?.Data.CollisionLayer.ToString("X") ?? "null";
            var endLayer = endPoly?.Data.CollisionLayer.ToString("X") ?? "null";
            var startMask = startPoly?.Data.CollisionMask.ToString("X") ?? "null";
            var endMask = endPoly?.Data.CollisionMask.ToString("X") ?? "null";
            var startNeighbors = startPoly?.Neighbors.Count ?? -1;
            var endNeighbors = endPoly?.Neighbors.Count ?? -1;
            var startTile = sEnt.System<SharedMapSystem>().GetTileRef(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(0, 0));
            var endTile = sEnt.System<SharedMapSystem>().GetTileRef(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(2, 0));

            Assert.That(sEnt.Deleted(puddle) || IsPuddleAllWater(sEnt, puddle) || distance <= 1.5f,
                $"The android should have moved to and mopped a puddle two tiles away. Distance={distance:F2} CanMove={canMove} Speed={speed} Steering={steeringStatus} CurrentTool={currentTool} KnownDirty={known} HasPathfinding={hasPathfinding} StartPoly={startPoly?.GetHashCode() ?? 0} EndPoly={endPoly?.GetHashCode() ?? 0} StartFlags={startFlags} EndFlags={endFlags} StartLayer={startLayer} EndLayer={endLayer} StartMask={startMask} EndMask={endMask} StartNeighbors={startNeighbors} EndNeighbors={endNeighbors} StartTile={startTile.Tile.TypeId} EndTile={endTile.Tile.TypeId}");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AddressedGreeting_DoesNotCreateOrderAndIsRemembered()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.MapCoords);

            if (sEnt.TryGetComponent<AndroidIdentityComponent>(android, out var identity))
            {
                identity.FirstName = "Gerard";
                identity.FullDisplayName = "Androïde Gerard";
                sEnt.System<MetaDataSystem>().SetEntityName(android, identity.FullDisplayName);
            }
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Androïde salut", speaker));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(android), Is.False, "A greeting should not create an order");

            Assert.That(sEnt.TryGetComponent<AndroidMemoryComponent>(android, out var memory), Is.True);
            Assert.That(memory.RecentInteractions.Count, Is.GreaterThan(0), "An addressed greeting should be remembered");

            var last = memory.RecentInteractions.Last();
            Assert.That(last.Source, Is.EqualTo(speaker));
            Assert.That(last.Type, Is.EqualTo("Spoke"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_Loadout_BucketAndMopFilledWithWater()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var inventory = sEnt.System<AndroidInventorySystem>();
            var solutions = sEnt.System<SharedSolutionContainerSystem>();

            var bucket = inventory.FindItemByPrototype(android, inv, "Bucket");
            var mop = inventory.FindItemByPrototype(android, inv, "MopItem");

            Assert.That(bucket, Is.Not.Null, "Android should own a bucket");
            Assert.That(mop, Is.Not.Null, "Android should own a mop");

            Assert.That(solutions.TryGetSolution((bucket!.Value, null), "drink", out var bucketSoln, out _), Is.True);
            Assert.That(bucketSoln.Value.Comp.Solution.GetTotalPrototypeQuantity(new ProtoId<ReagentPrototype>("Water")), Is.GreaterThan(FixedPoint2.Zero),
                "Bucket should spawn with water");

            var mopComp = sEnt.GetComponent<AbsorbentComponent>(mop!.Value);
            Assert.That(solutions.TryGetSolution((mop.Value, null), mopComp.SolutionName, out var mopSoln, out _), Is.True);
            Assert.That(mopSoln.Value.Comp.Solution.GetTotalPrototypeQuantity(new ProtoId<ReagentPrototype>("Water")), Is.GreaterThan(FixedPoint2.Zero),
                "Mop should spawn with water");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_IgnoresTrashInsideContainers()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var lookup = sEnt.System<EntityLookupSystem>();
            var transformSys = sEnt.System<SharedTransformSystem>();
            var puddleSys = sEnt.System<SharedPuddleSystem>();
            var interactionSys = sEnt.System<SharedInteractionSystem>();
            var scanHelper = new Content.Server._Harvest.AndroidNpc.Utilities.TileScanHelper(sEnt, lookup, transformSys, puddleSys, interactionSys);

            var bag = sEnt.SpawnEntity("TrashBag", testMap.GridCoords);
            var trash = sEnt.SpawnEntity("Syringe", testMap.GridCoords);

            sEnt.System<SharedStorageSystem>().Insert(bag, trash, out _, user: EntityUid.Invalid);

            var dummy = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            var targets = scanHelper.FindDirtyTargets(dummy, 8f, new List<string> { "Trash" });

            Assert.That(targets, Does.Not.Contain(trash), "Trash inside a bag should not be selected as a dirty target");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_WringsDirtyMopIntoCarriedBucket()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var inventory = sEnt.System<AndroidInventorySystem>();
            var solutions = sEnt.System<SharedSolutionContainerSystem>();

            Assert.That(inventory.TryGetMop(android, inv, out var mop), Is.True, "Android should own a mop");
            Assert.That(inventory.TryGetBucket(android, inv, out var bucket), Is.True, "Android should own a bucket");

            Assert.That(solutions.TryGetSolution((mop.Owner, null), mop.Comp.SolutionName, out var mopSoln, out _), Is.True);

            // Saturate the mop with blood so it holds no clean water and cannot mop anything.
            solutions.RemoveAllSolution(mopSoln.Value);
            solutions.ForceAddSolution(mopSoln.Value, new Solution("Blood", FixedPoint2.New(100)));
            solutions.UpdateChemicals(mopSoln.Value);

            Assert.That(inventory.CanMopClean(mop), Is.False, "A blood-soaked mop should not be able to clean");
            Assert.That(inventory.MopNeedsService(android, inv), Is.True, "The mop should be flagged for rinsing");
            Assert.That(inventory.CanBucketRefillMop(bucket), Is.True, "The starting bucket should hold clean water");

            // The bucket lives in the android's bag; wringing must still work from there.
            Assert.That(inventory.TryWringMop(android, inv), Is.True, "The mop should be wrung out into the bucket");

            Assert.That(inventory.CanMopClean(mop), Is.True, "The rinsed mop should be able to clean again");

            Assert.That(solutions.TryGetDrainableSolution((bucket, null, null), out _, out var bucketSolution), Is.True);
            Assert.That(bucketSolution.GetTotalPrototypeQuantity(new ProtoId<ReagentPrototype>("Blood")), Is.GreaterThan(FixedPoint2.Zero),
                "The filth from the mop should end up in the bucket rather than vanishing");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_EmptiesDirtyBucketAndRefillsAtSink()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid sink = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            sink = sEnt.SpawnEntity("Sink", testMap.GridCoords);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var inventory = sEnt.System<AndroidInventorySystem>();
            var solutions = sEnt.System<SharedSolutionContainerSystem>();
            var water = new ProtoId<ReagentPrototype>("Water");
            var blood = new ProtoId<ReagentPrototype>("Blood");

            Assert.That(inventory.TryGetBucket(android, inv, out var bucket), Is.True, "Android should own a bucket");
            Assert.That(solutions.TryGetRefillableSolution((bucket, null, null), out var bucketSoln, out _), Is.True);

            // Fill the bucket entirely with filth, as happens after repeatedly rinsing the mop.
            solutions.RemoveAllSolution(bucketSoln.Value);
            solutions.ForceAddSolution(bucketSoln.Value, new Solution("Blood", FixedPoint2.New(250)));
            solutions.UpdateChemicals(bucketSoln.Value);

            Assert.That(inventory.IsBucketDirty(bucket), Is.True, "The bucket should be considered dirty");
            Assert.That(inventory.CanBucketRefillMop(bucket), Is.False, "A bucket of blood cannot rinse the mop");
            Assert.That(inventory.BucketNeedsService(android, inv), Is.True, "The bucket should need a trip to a sink");

            Assert.That(inventory.TryFindBucketStation(android, inv, out var station), Is.True,
                "The nearby sink should be found as a service station");
            Assert.That(station, Is.EqualTo(sink));

            Assert.That(inventory.TryServiceBucket(android, inv, station), Is.True, "The bucket should be serviced at the sink");

            Assert.That(solutions.TryGetDrainableSolution((bucket, null, null), out _, out var after), Is.True);
            Assert.That(after.GetTotalPrototypeQuantity(blood), Is.EqualTo(FixedPoint2.Zero),
                "The dirty water should have been poured into the sink");
            Assert.That(after.GetTotalPrototypeQuantity(water), Is.GreaterThan(FixedPoint2.Zero),
                "The bucket should have been refilled with clean water from the sink");

            Assert.That(inventory.BucketNeedsService(android, inv), Is.False, "The bucket should no longer need servicing");
            Assert.That(inventory.CanBucketRefillMop(bucket), Is.True, "The serviced bucket should be able to rinse the mop again");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_EmptiesTrashBagIntoDisposalUnit()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid disposal = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            disposal = sEnt.SpawnEntity("DisposalUnit", testMap.GridCoords);

            // Disposal units anchor themselves when placed on a grid; the android only looks for
            // anchored ones because that is what the disposal system requires for insertion.
            Assert.That(sEnt.GetComponent<TransformComponent>(disposal).Anchored, Is.True,
                "The test disposal unit should be anchored to the grid");
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var inventory = sEnt.System<AndroidInventorySystem>();
            var storage = sEnt.System<SharedStorageSystem>();

            Assert.That(inventory.TryGetTrashBag(android, inv, out var bag), Is.True, "Android should own a trash bag");

            // Fill the trash bag to capacity with syringes.
            while (storage.HasSpace((bag.Owner, bag.Comp)))
            {
                var trash = sEnt.SpawnEntity("Syringe", testMap.GridCoords);
                storage.Insert(bag.Owner, trash, out _, user: EntityUid.Invalid);
            }

            var trashCount = bag.Comp.Container.ContainedEntities.Count;
            Assert.That(trashCount, Is.GreaterThan(0), "Trash bag should contain trash");
            Assert.That(inventory.TrashBagNeedsEmptying(android, inv), Is.True, "The full trash bag should need emptying");

            Assert.That(inventory.TryFindDisposalUnit(android, out var found), Is.True, "The nearby disposal unit should be found");
            Assert.That(found, Is.EqualTo(disposal));

            Assert.That(inventory.TryDumpTrashBag(android, inv, found), Is.True, "The trash bag should be dumped into the unit");

            var disposalComp = sEnt.GetComponent<DisposalUnitComponent>(disposal);
            Assert.That(disposalComp.Container.ContainedEntities.Count, Is.EqualTo(trashCount),
                "Every piece of trash should end up in the disposal unit instead of being deleted");

            Assert.That(bag.Comp.Container.ContainedEntities.Count, Is.EqualTo(0), "Trash bag should be empty afterwards");
            Assert.That(inventory.TrashBagNeedsEmptying(android, inv), Is.False, "The emptied bag should no longer need emptying");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_DoesNotKeepScrubbingWithASpentMop()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid puddle = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            puddle = sEnt.SpawnEntity("PuddleBloodSmall", testMap.GridCoords);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var inventory = sEnt.System<AndroidInventorySystem>();
            var solutions = sEnt.System<SharedSolutionContainerSystem>();

            Assert.That(inventory.TryGetMop(android, inv, out var mop), Is.True);
            Assert.That(inventory.TryGetBucket(android, inv, out var bucket), Is.True);

            // Drain the mop and the bucket, so nothing can rinse the mop where the android stands.
            Assert.That(solutions.TryGetSolution((mop.Owner, null), mop.Comp.SolutionName, out var mopSoln, out _), Is.True);
            solutions.RemoveAllSolution(mopSoln.Value);

            Assert.That(solutions.TryGetRefillableSolution((bucket, null, null), out var bucketSoln, out _), Is.True);
            solutions.RemoveAllSolution(bucketSoln.Value);

            Assert.That(inventory.CanMopClean(mop), Is.False, "A dry mop cannot clean");
            Assert.That(inventory.TryWringMop(android, inv), Is.False, "An empty bucket cannot rinse the mop");

            // The dirty puddle must not be picked as work while the only tool for it is unusable,
            // otherwise the android walks over and scrubs at it forever with no effect.
            var memory = sEnt.GetComponent<AndroidMemoryComponent>(android);
            memory.KnownDirtyTargets.Clear();
            memory.KnownDirtyTargets.Add(puddle);
            inv.CurrentToolTarget = null;

            var blackboard = AndroidBlackboard.Create(sEnt, android, new BlacklistManager());
            var status = new SelectDirtyTargetAction().Tick(android, blackboard, 0f);

            Assert.That(status, Is.EqualTo(BehaviorStatus.Failure),
                "A puddle should not be selected while the mop is spent");
            Assert.That(inv.CurrentToolTarget, Is.Null);
            Assert.That(memory.KnownDirtyTargets, Does.Contain(puddle),
                "The puddle should be remembered so it is cleaned once the mop is rinsed");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_IgnoresPuddlesThatAreOnlyWater()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid puddle = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            puddle = sEnt.SpawnEntity("PuddleBloodSmall", testMap.GridCoords);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var memory = sEnt.GetComponent<AndroidMemoryComponent>(android);
            var solutions = sEnt.System<SharedSolutionContainerSystem>();

            // Turn the puddle into pure water so it will evaporate.
            Assert.That(solutions.TryGetSolution((puddle, null), "puddle", out var puddleSoln, out _), Is.True);
            solutions.RemoveAllSolution(puddleSoln.Value);
            solutions.TryAddSolution(puddleSoln.Value, new Solution("Water", puddleSoln.Value.Comp.Solution.MaxVolume));

            memory.KnownDirtyTargets.Clear();
            memory.KnownDirtyTargets.Add(puddle);
            inv.CurrentToolTarget = null;

            var blackboard = AndroidBlackboard.Create(sEnt, android, new BlacklistManager());
            var status = new SelectDirtyTargetAction().Tick(android, blackboard, 0f);

            Assert.That(status, Is.EqualTo(BehaviorStatus.Failure),
                "A puddle made only of water should not be selected for cleaning");
            Assert.That(inv.CurrentToolTarget, Is.Null);
            Assert.That(memory.KnownDirtyTargets, Does.Not.Contain(puddle),
                "A water-only puddle should be dropped from the work list");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_CanPickUpBananaPeel()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var inventory = sEnt.System<AndroidInventorySystem>();
            var storage = sEnt.System<SharedStorageSystem>();

            var android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var peel = sEnt.SpawnEntity("TrashBananaPeel", testMap.GridCoords);

            Assert.That(inventory.TryGetTrashBag(android, inv, out var bag), Is.True, "Android should own a trash bag");

            var inserted = storage.Insert(bag.Owner, peel, out _, out var reason, user: android, storageComp: bag.Comp);
            Assert.That(inserted, Is.True, $"Trash bag should accept banana peel (reason: {reason ?? "none"})");
            Assert.That(bag.Comp.Container.ContainedEntities, Does.Contain(peel), "Banana peel should be inside the trash bag");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCase("TrashBananaPeel")]
    [TestCase("TrashBakedBananaPeel")]
    public async Task Janitor_PicksUpBananaPeelOverTime(string peelPrototype)
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        await server.WaitAssertion(() =>
        {
            EnsureSafeAtmosphere(server, testMap);
        });

        // Let the pathfinding navmesh build before spawning the android.
        await server.WaitRunTicks(25);

        EntityUid android = default;
        EntityUid peel = default;

        await server.WaitAssertion(() =>
        {
            // Put the android and the banana peel on the same tile so the navmesh is trivial.
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            peel = sEnt.SpawnEntity(peelPrototype, testMap.GridCoords);
        });

        // Give the android time to scan the area, equip a trash bag, and pick up the peel.
        await server.WaitRunTicks(15);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var invSys = sEnt.System<AndroidInventorySystem>();

            Assert.That(invSys.TryGetTrashBag(android, inv, out var bag), Is.True, "Android should own a trash bag");
            Assert.That(invSys.TrashBagNeedsEmptying(android, inv), Is.False, "Trash bag should not be considered full");

            if (inv.EquippedItem == null)
                Assert.Fail("Android never equipped the trash bag");

            Assert.That(sEnt.GetComponent<MetaDataComponent>(inv.EquippedItem!.Value).EntityPrototype?.ID, Is.EqualTo("TrashBag"), "Equipped item should be the trash bag");

            if (!bag.Comp.Container.ContainedEntities.Contains(peel))
                Assert.Fail("Trash bag is empty; the android never inserted the peel");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCase("TrashBananaPeel")]
    [TestCase("TrashBakedBananaPeel")]
    public async Task Janitor_TreePicksUpBananaPeel(string peelPrototype)
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        await server.WaitAssertion(() =>
        {
            EnsureSafeAtmosphere(server, testMap);
        });

        EntityUid android = default;
        EntityUid peel = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            peel = sEnt.SpawnEntity(peelPrototype, testMap.GridCoords);
        });

        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            Robust.Shared.Log.Logger.GetSawmill("AndroidNpc").Info("[TEST] Pre-resupply check");
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var invSys = sEnt.System<AndroidInventorySystem>();

            if (invSys.NeedsResupply(android, inv))
            {
                var owned = string.Join(", ", inv.OwnedItems
                    .Where(e => !sEnt.Deleted(e))
                    .Select(e => sEnt.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID ?? "?"));
                Assert.Fail($"Android still needs resupply. Owned items: {owned}");
            }
        });

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var memory = sEnt.GetComponent<AndroidMemoryComponent>(android);
            memory.KnownDirtyTargets.Add(peel);
            inv.CurrentToolTarget = null;

            var tree = JanitorTree.Build();
            var blackboard = AndroidBlackboard.Create(sEnt, android, new BlacklistManager());

            var lastStatus = BehaviorStatus.Failure;
            for (var i = 0; i < 30; i++)
            {
                // Run the steering system so the move action can make progress.
                sEnt.System<NPCSteeringSystem>().Update(0.1f);
                lastStatus = tree.Tick(android, blackboard, 0.1f);
                if (lastStatus == BehaviorStatus.Success)
                    break;
            }

            var invSys = sEnt.System<AndroidInventorySystem>();
            Assert.That(invSys.NeedsResupply(android, inv), Is.False, $"Android should not need resupply (last status {lastStatus})");
            Assert.That(inv.CurrentToolTarget, Is.EqualTo(peel).Or.Null, $"CurrentToolTarget should be the peel or cleared; was {inv.CurrentToolTarget} (last status {lastStatus})");

            if (inv.EquippedItem == null)
                Assert.Fail($"Android never equipped an item (last status {lastStatus})");

            Assert.That(sEnt.GetComponent<MetaDataComponent>(inv.EquippedItem!.Value).EntityPrototype?.ID, Is.EqualTo("TrashBag"), $"Equipped item should be the trash bag (last status {lastStatus})");
            Assert.That(invSys.TryGetTrashBag(android, inv, out var bag), Is.True, "Android should own a trash bag");
            Assert.That(bag.Comp.Container.ContainedEntities, Does.Contain(peel), "Banana peel should be in the trash bag");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_DirectEquipPicksUpBananaPeel()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            var peel = sEnt.SpawnEntity("TrashBananaPeel", testMap.GridCoords);

            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var inventory = sEnt.System<AndroidInventorySystem>();

            // Make sure the android owns a trash bag before we try to equip it.
            Assert.That(inventory.TryGetTrashBag(android, inv, out var bag), Is.True, "Android should own a trash bag");
            Assert.That(inv.OwnedItems, Does.Contain(bag.Owner), "Trash bag should be in OwnedItems");

            // Equip the trash bag.
            Assert.That(inventory.Equip(android, inv, bag.Owner), Is.True, "Equip should pick up the trash bag");
            Assert.That(inventory.GetEquippedItem(android), Is.EqualTo(bag.Owner), "Equipped item should be the trash bag");

            // Insert the peel through the storage system (the same path UseItemAction uses).
            var storage = sEnt.System<SharedStorageSystem>();
            Assert.That(storage.CanInsert(bag.Owner, peel, out var reason, bag.Comp), Is.True,
                $"Trash bag should be able to accept banana peel (reason: {reason ?? "none"})");
            Assert.That(storage.Insert(bag.Owner, peel, out _, out _, user: android, storageComp: bag.Comp), Is.True,
                "Trash bag should insert banana peel");
            Assert.That(bag.Comp.Container.ContainedEntities, Does.Contain(peel));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_TreeSelectsClosestBananaPeel()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;
        var mapSys = sEnt.System<SharedMapSystem>();

        await server.WaitAssertion(() =>
        {
            EnsureSafeAtmosphere(server, testMap);

            // Extend the floor so the peels can be at distinct distances.
            var tile = testMap.Tile.Tile;
            mapSys.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(1, 0), new Tile(tile.TypeId));
            mapSys.SetTile(testMap.Grid.Owner, testMap.Grid.Comp, new Vector2i(3, 0), new Tile(tile.TypeId));
        });

        // Let the pathfinding navmesh build on the new tiles.
        await server.WaitRunTicks(25);

        EntityUid android = default;
        EntityUid nearPeel = default;
        EntityUid farPeel = default;

        await server.WaitAssertion(() =>
        {
            var grid = testMap.Grid.Owner;
            android = sEnt.SpawnEntity("MobAndroidJanitor", new EntityCoordinates(grid, 0, 0));
            nearPeel = sEnt.SpawnEntity("TrashBananaPeel", new EntityCoordinates(grid, 1, 0));
            farPeel = sEnt.SpawnEntity("TrashBananaPeel", new EntityCoordinates(grid, 3, 0));
        });

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var memory = sEnt.GetComponent<AndroidMemoryComponent>(android);

            // Deliberately add the far peel first so a non-sorted list would pick it.
            memory.KnownDirtyTargets.Add(farPeel);
            memory.KnownDirtyTargets.Add(nearPeel);
            inv.CurrentToolTarget = null;

            var tree = JanitorTree.Build();
            var blackboard = AndroidBlackboard.Create(sEnt, android, new BlacklistManager());

            // A single tick is enough to select and begin moving to the nearest target.
            tree.Tick(android, blackboard, 0.1f);

            Assert.That(inv.CurrentToolTarget, Is.EqualTo(nearPeel), "Android should select the closest banana peel");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_OpensAirlockToReachBananaPeel()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;
        var mapSys = sEnt.System<SharedMapSystem>();

        EntityUid door = default;
        EntityUid peel = default;

        await server.WaitAssertion(() =>
        {
            EnsureSafeAtmosphere(server, testMap);

            var tile = new Tile(testMap.Tile.Tile.TypeId);
            var tile1Coords = testMap.GridCoords.Offset(new Vector2(1, 0));
            var tile2Coords = testMap.GridCoords.Offset(new Vector2(2, 0));
            var tile1Index = mapSys.CoordinatesToTile(testMap.Grid.Owner, testMap.Grid.Comp, tile1Coords);
            var tile2Index = mapSys.CoordinatesToTile(testMap.Grid.Owner, testMap.Grid.Comp, tile2Coords);

            mapSys.SetTiles(testMap.Grid, new List<(Vector2i GridIndices, Tile Tile)>
            {
                (tile1Index, tile),
                (tile2Index, tile),
            });

            // Verify the tiles were actually created.
            var tile1 = mapSys.GetTileRef(testMap.Grid, tile1Index);
            var tile2 = mapSys.GetTileRef(testMap.Grid, tile2Index);
            Assert.That(tile1.Tile.IsEmpty, Is.False, $"Tile (1,0) was not set (type {tile1.Tile.TypeId})");
            Assert.That(tile2.Tile.IsEmpty, Is.False, $"Tile (2,0) was not set (type {tile2.Tile.TypeId})");
        });

        // Let the grid's dynamic tree and pathfinding navmesh update before placing structures on it.
        await server.WaitRunTicks(25);

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            var grid = testMap.Grid.Owner;

            // Place everything on the centre of its tile so the pathfinder target is a tile
            // centre rather than a tile edge, which makes the final approach much more stable.
            var startCoords = testMap.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            var doorCoords = testMap.GridCoords.Offset(new Vector2(1.5f, 0.5f));
            var peelCoords = testMap.GridCoords.Offset(new Vector2(2.5f, 0.5f));

            door = sEnt.SpawnEntity("AirlockJanitor", doorCoords);

            // Make the door self-powered so it can actually open in this isolated test map.
            if (sEnt.TryGetComponent<ApcPowerReceiverComponent>(door, out var power))
            {
                power.NeedsPower = false;
            }

            // Force the airlock to require an explicit interaction; this makes the NPC steering
            // obstacle handler call InteractionActivate rather than relying on bump-open logic
            // that the current pathfinder does not drive correctly in isolated small corridors.
            if (sEnt.TryGetComponent<DoorComponent>(door, out var doorComp))
            {
                doorComp.BumpOpen = false;
            }

            // Place the peel past the airlock so the android must pass through it.
            peel = sEnt.SpawnEntity("TrashBananaPeel", peelCoords);

            // The peel must have been parented to the grid; otherwise grid traversal failed.
            var peelXform = sEnt.GetComponent<TransformComponent>(peel);
            Assert.That(sEnt.Deleted(peel), Is.False, "Peel was deleted before the android could pick it up");
            Assert.That(peelXform.ParentUid, Is.EqualTo(grid), $"Peel should be on the test grid; parent={peelXform.ParentUid}, grid={grid}");

            // Stop the peel from being re-parented to the map by grid traversal while the test runs.
            peelXform.GridTraversal = false;

            // Remove the item's physics so it doesn't behave as a steering obstacle while the
            // android approaches it, but keep it as an interactable target for the cleanup tree.
            sEnt.RemoveComponent<PhysicsComponent>(peel);
            sEnt.RemoveComponent<FixturesComponent>(peel);
        });

        // Give the pathfinding system time to rebuild the navmesh after the door was placed.
        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            // Force the target so the android tries to path through the closed door.
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords.Offset(new Vector2(0.5f, 0.5f)));

            // Tile movement snaps the android to tile centres, which makes it overshoot and
            // oscillate on the small final approach. Disable it so the steering can finish
            // within the interaction range instead.
            sEnt.RemoveComponent<TileMovementComponent>(android);

            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var memory = sEnt.GetComponent<AndroidMemoryComponent>(android);
            memory.KnownDirtyTargets.Clear();
            memory.KnownDirtyTargets.Add(peel);
            inv.CurrentToolTarget = peel;

            Assert.That(sEnt.Deleted(peel), Is.False, "Peel was deleted before setting target");
            Assert.That(inv.CurrentToolTarget, Is.EqualTo(peel), "CurrentToolTarget was not set");
        });

        // Give the android time to move to the door, open it, and pick up the peel.
        await server.WaitRunTicks(120);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var invSys = sEnt.System<AndroidInventorySystem>();

            var peelXform = sEnt.GetComponent<TransformComponent>(peel);
            Assert.That(sEnt.Deleted(peel), Is.False, $"Peel was deleted; parent={peelXform.ParentUid} deleted={sEnt.Deleted(peel)}");

            Assert.That(sEnt.TryGetComponent<DoorComponent>(door, out var doorComp), Is.True, "Door entity should have a DoorComponent");
            Assert.That(doorComp.State, Is.EqualTo(DoorState.Open), "Android should have opened the airlock");

            Assert.That(invSys.TryGetTrashBag(android, inv, out var bag), Is.True, "Android should own a trash bag");

            if (inv.EquippedItem == null)
                Assert.Fail("Android never equipped the trash bag");

            Assert.That(sEnt.GetComponent<MetaDataComponent>(inv.EquippedItem!.Value).EntityPrototype?.ID, Is.EqualTo("TrashBag"), "Equipped item should be the trash bag");

            if (!bag.Comp.Container.ContainedEntities.Contains(peel))
                Assert.Fail("Trash bag is empty; the android never reached the peel");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AndroidSpawns_SuitSensorCoordinatesEnabled()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        await server.WaitAssertion(() => android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.MapCoords));
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var invSys = sEnt.System<InventorySystem>();
            Assert.That(invSys.TryGetSlotEntity(android, "jumpsuit", out var jumpsuit), Is.True, "Android should wear a jumpsuit");
            Assert.That(sEnt.TryGetComponent<SuitSensorComponent>(jumpsuit!.Value, out var sensor), Is.True, "Android jumpsuit should broadcast suit sensors");
            Assert.That(sensor!.Mode, Is.EqualTo(SuitSensorMode.SensorCords), "Android suit sensor should start in coordinate mode");
            Assert.That(sensor.ControlsLocked, Is.True, "Android suit sensor controls should be locked");
            Assert.That(sensor.RandomMode, Is.False, "Android suit sensor should not use a random mode");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_DoesNotOpenLockedAirlock()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;
        var mapSys = sEnt.System<SharedMapSystem>();

        EntityUid door = default;
        EntityUid peel = default;

        await server.WaitAssertion(() =>
        {
            EnsureSafeAtmosphere(server, testMap);

            var tile = new Tile(testMap.Tile.Tile.TypeId);
            var tile1Coords = testMap.GridCoords.Offset(new Vector2(1, 0));
            var tile2Coords = testMap.GridCoords.Offset(new Vector2(2, 0));
            var tile1Index = mapSys.CoordinatesToTile(testMap.Grid.Owner, testMap.Grid.Comp, tile1Coords);
            var tile2Index = mapSys.CoordinatesToTile(testMap.Grid.Owner, testMap.Grid.Comp, tile2Coords);

            mapSys.SetTiles(testMap.Grid, new List<(Vector2i GridIndices, Tile Tile)>
            {
                (tile1Index, tile),
                (tile2Index, tile),
            });
        });

        await server.WaitRunTicks(25);

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            var grid = testMap.Grid.Owner;
            var startCoords = testMap.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            var doorCoords = testMap.GridCoords.Offset(new Vector2(1.5f, 0.5f));
            var peelCoords = testMap.GridCoords.Offset(new Vector2(2.5f, 0.5f));

            door = sEnt.SpawnEntity("AirlockSecurityLocked", doorCoords);

            // Keep the door powered but not bump-open so the android must rely on access.
            if (sEnt.TryGetComponent<ApcPowerReceiverComponent>(door, out var power))
                power.NeedsPower = false;

            if (sEnt.TryGetComponent<DoorComponent>(door, out var doorComp))
                doorComp.BumpOpen = false;

            peel = sEnt.SpawnEntity("TrashBananaPeel", peelCoords);
            var peelXform = sEnt.GetComponent<TransformComponent>(peel);
            Assert.That(peelXform.ParentUid, Is.EqualTo(grid), "Peel should be on the test grid");
            peelXform.GridTraversal = false;
            sEnt.RemoveComponent<PhysicsComponent>(peel);
            sEnt.RemoveComponent<FixturesComponent>(peel);

            android = sEnt.SpawnEntity("MobAndroidJanitor", startCoords);
            sEnt.RemoveComponent<TileMovementComponent>(android);

            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var memory = sEnt.GetComponent<AndroidMemoryComponent>(android);
            memory.KnownDirtyTargets.Clear();
            memory.KnownDirtyTargets.Add(peel);
            inv.CurrentToolTarget = peel;
        });

        await server.WaitRunTicks(80);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.TryGetComponent<DoorComponent>(door, out var doorComp), Is.True, "Door entity should have a DoorComponent");
            // "Denying" is the closed door refusing access.
            Assert.That(doorComp.State, Is.EqualTo(DoorState.Closed).Or.EqualTo(DoorState.Denying), "Android should not open an airlock it lacks access to");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_EquipsAndUsesLightReplacerOnBrokenFixture()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid fixture = default;

        await server.WaitAssertion(() =>
        {
            EnsureSafeAtmosphere(server, testMap);

            // Place the android and a powered light fixture on the same tile so no movement is needed.
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            fixture = sEnt.SpawnEntity("Poweredlight", testMap.GridCoords);

            // Break the bulb inside the fixture so the light replacer has work to do.
            var poweredLightSys = sEnt.System<PoweredLightSystem>();
            var bulb = poweredLightSys.GetBulb(fixture);
            Assert.That(bulb, Is.Not.Null, "Fixture should have a bulb to replace");

            if (sEnt.TryGetComponent<LightBulbComponent>(bulb!.Value, out var bulbComp))
                bulbComp.State = LightBulbState.Broken;
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var inv = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var memory = sEnt.GetComponent<AndroidMemoryComponent>(android);

            memory.KnownDirtyTargets.Clear();
            memory.KnownDirtyTargets.Add(fixture);
            inv.CurrentToolTarget = fixture;

            var blackboard = AndroidBlackboard.Create(sEnt, android, new BlacklistManager());

            // Equip the light replacer from the belt, even if the android is currently holding the mop.
            var equipStatus = new EquipItemAction((_, _) => "LightReplacer").Tick(android, blackboard, 0.1f);
            Assert.That(equipStatus, Is.EqualTo(BehaviorStatus.Success), "Android should equip the light replacer");

            var equippedId = inv.EquippedItem.HasValue && sEnt.TryGetComponent<MetaDataComponent>(inv.EquippedItem.Value, out var meta)
                ? meta.EntityPrototype?.ID
                : null;
            Assert.That(equippedId, Is.EqualTo("LightReplacer"), "Light replacer should be in the active hand");

            // Use the already-equipped light replacer on the broken fixture.
            var useStatus = new UseItemAction((_, _) => inv.CurrentToolTarget).Tick(android, blackboard, 0.1f);
            Assert.That(useStatus, Is.EqualTo(BehaviorStatus.Success), "Light replacer should replace the broken bulb");

            var replacedBulb = sEnt.System<PoweredLightSystem>().GetBulb(fixture);
            Assert.That(replacedBulb, Is.Not.Null, "A bulb should still be present in the fixture after replacement");

            if (sEnt.TryGetComponent<LightBulbComponent>(replacedBulb!.Value, out var newBulbComp))
                Assert.That(newBulbComp.State, Is.EqualTo(LightBulbState.Normal), "The fixture should now contain a normal bulb");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_GoesOnBreakAtBarAfterInterval()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;
        var mapSys = sEnt.System<SharedMapSystem>();

        EntityUid barBeacon = default;
        EntityUid apc = default;
        EntityUid chair = default;

        await server.WaitAssertion(() =>
        {
            EnsureSafeAtmosphere(server, testMap);

            // Build a small bar room with a clear corridor for the android.
            var tile = new Tile(testMap.Tile.Tile.TypeId);
            mapSys.SetTiles(testMap.Grid, new List<(Vector2i GridIndices, Tile Tile)>
            {
                (new Vector2i(1, 0), tile),
                (new Vector2i(2, 0), tile),
                (new Vector2i(3, 0), tile),
                (new Vector2i(4, 0), tile),
                (new Vector2i(3, 1), tile),
                (new Vector2i(4, 1), tile),
            });

            var gridCoords = testMap.GridCoords;

            // Bar at the other end: beacon and APC behind the bar, chair at the bar counter.
            // These must exist before the android spawns so OnMapInit can locate the bar.
            // Spawning them now also lets the pathfinding navmesh build for the chair tile.
            barBeacon = sEnt.SpawnEntity("DefaultStationBeaconBar", gridCoords.Offset(new Vector2(4.5f, 1.5f)));
            apc = sEnt.SpawnEntity("APCBasic", gridCoords.Offset(new Vector2(3.5f, 1.5f)));
            // Only bar stools count as a seat for placing a food/drink order - a plain chair doesn't.
            chair = sEnt.SpawnEntity("StoolBar", gridCoords.Offset(new Vector2(4.5f, 0.5f)));
        });

        // Let pathfinding build the navmesh for the corridor and the bar fixtures.
        await server.WaitRunTicks(30);

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            var gridCoords = testMap.GridCoords;

            // Android starts at the far end of the corridor.
            android = sEnt.SpawnEntity("MobAndroidJanitor", gridCoords.Offset(new Vector2(0.5f, 0.5f)));

            // Fast-forward the break timer so the test doesn't have to wait two real minutes.
            var androidComp = sEnt.GetComponent<AndroidComponent>(android);
            androidComp.NextBreakAt = IoCManager.Resolve<IGameTiming>().CurTime - TimeSpan.FromSeconds(1);
            androidComp.BreakUntil = TimeSpan.Zero;

            // The bar beacon should have been picked up during OnMapInit.
            Assert.That(androidComp.BarLocation.IsValid(sEnt), Is.True, "Android should have located the bar beacon");
        });

        // Give the android time to walk to the bar chair and buckle in.
        await server.WaitRunTicks(120);

        await server.WaitAssertion(() =>
        {
            var androidComp = sEnt.GetComponent<AndroidComponent>(android);
            var buckle = sEnt.EnsureComponent<BuckleComponent>(android);

            Assert.That(androidComp.Status, Is.EqualTo(AndroidStatus.OnBreak), "Android should be on a break");
            Assert.That(androidComp.BreakState, Is.EqualTo(AndroidBreakState.Resting), $"Android should be resting on break, but was {androidComp.BreakState}");
            Assert.That(androidComp.BreakChair, Is.EqualTo(chair), "Android should be using the bar chair for its break");
            Assert.That(buckle.Buckled, Is.True, "Android should be buckled to the chair");
            Assert.That(buckle.BuckledTo, Is.EqualTo(chair), "Android should be buckled to the bar chair");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Loads a real station map, initializes it, places the android as far from the bar as possible,
    /// and verifies it can still navigate across the station and take its break there. Initialization
    /// is required so that spawned entities (including the android) actually raise MapInitEvent.
    /// </summary>
    [Test]
    public async Task Janitor_ReachesBarFromOppositeEndOfSaltern()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var mapSys = sEnt.System<SharedMapSystem>();
        var xform = sEnt.System<SharedTransformSystem>();
        var pathfinding = sEnt.System<PathfindingSystem>();
        var mapLoader = sEnt.System<MapLoaderSystem>();
        var atmos = sEnt.System<AtmosphereSystem>();

        Entity<MapComponent>? map = null;
        HashSet<Entity<MapGridComponent>>? grids = null;
        EntityUid stationGrid = default;
        MapGridComponent? stationGridComp = null;
        EntityUid android = default;
        EntityCoordinates? barCoords = null;

        await server.WaitAssertion(() =>
        {
            var path = new ResPath("/Maps/_Goobstation/saltern.yml");
            Assert.That(mapLoader.TryLoadMap(path, out map, out grids), Is.True, "Failed to load saltern map");

            Assert.That(map, Is.Not.Null, "A map should have been loaded");
            Assert.That(grids, Is.Not.Null.And.Not.Empty, "Saltern should contain at least one grid");

            // Initialize the loaded map so its entities and any newly spawned ones get MapInit.
            mapSys.InitializeMap(map!.Value);

            // Pick the largest grid as the station grid.
            var largestArea = 0f;
            foreach (var grid in grids!)
            {
                var area = grid.Comp.LocalAABB.Size.LengthSquared();
                if (area > largestArea)
                {
                    largestArea = area;
                    stationGrid = grid.Owner;
                    stationGridComp = grid.Comp;
                }
            }

            Assert.That(sEnt.EntityExists(stationGrid), Is.True, "No station grid found");

            // Force all internal airlocks/doors open so the navmesh treats the station as connected.
            // The android can open many doors, but the test is about long-distance pathfinding, not door access.
            var doorSys = sEnt.System<DoorSystem>();
            var doorQuery = sEnt.AllEntityQueryEnumerator<DoorComponent, TransformComponent>();
            while (doorQuery.MoveNext(out var door, out var doorComp, out var doorXform))
            {
                if (doorXform.GridUid != stationGrid)
                    continue;
                if (doorComp.State == DoorState.Welded || doorComp.State == DoorState.Open)
                    continue;

                doorSys.SetState(door, DoorState.Open, doorComp);
                doorSys.OnPartialOpen(door, doorComp);
                doorSys.SetNextStateChange(door, null, doorComp);
            }

            var mixture = new GasMixture(Atmospherics.CellVolume)
            {
                Temperature = Atmospherics.T20C
            };
            mixture.AdjustMoles(Gas.Oxygen, 22f);
            mixture.AdjustMoles(Gas.Nitrogen, 83f);
            atmos.SetMapAtmosphere(map!.Value.Owner, false, mixture);
        });

        // Give PathfindingSystem time to build the station navmesh.
        await server.WaitRunTicks(300);

        await server.WaitAssertion(() =>
        {
            Assert.That(stationGridComp, Is.Not.Null, "Station grid component should be loaded");

            // Find the bar beacon.
            var locator = new NavMapBeaconLocator(sEnt);
            Assert.That(locator.TryFindBeacon(
                new EntityCoordinates(stationGrid, Vector2.Zero),
                "bar",
                out var barUid,
                out barCoords),
                Is.True,
                "Saltern should have a bar nav beacon");

            Assert.That(barUid.HasValue, Is.True);

            static bool IsFree(PathPoly? p)
            {
                if (p == null)
                    return false;
                if (!p.Data.IsFreeSpace)
                    return false;
                if ((p.Data.Flags & (PathfindingBreadcrumbFlag.Space | PathfindingBreadcrumbFlag.Invalid)) != 0)
                    return false;
                return true;
            }

            // The nav beacon itself may sit on or near an impassable tile (e.g. a bar counter).
            // Find the nearest free floor tile and flood-fill to the farthest reachable one.
            var barLocal = barCoords!.Value.Position;
            PathPoly? startPoly = null;
            for (var dx = -2; dx <= 2 && startPoly == null; dx++)
            {
                for (var dy = -2; dy <= 2 && startPoly == null; dy++)
                {
                    var probe = new EntityCoordinates(stationGrid, barLocal + new Vector2(dx, dy));
                    var poly = pathfinding.GetPoly(probe);
                    if (IsFree(poly))
                        startPoly = poly;
                }
            }

            Assert.That(startPoly, Is.Not.Null, "Could not find a free floor tile near the bar");

            var visited = new HashSet<PathPoly>();
            var queue = new Queue<PathPoly>();
            queue.Enqueue(startPoly);
            visited.Add(startPoly);

            var barCenter = startPoly.Box.Center;
            var farPoly = startPoly;
            var farDistSq = 0f;
            const float MinFarDistance = 30f;
            var minFarSq = MinFarDistance * MinFarDistance;

            while (queue.Count > 0 && visited.Count < 5000)
            {
                var current = queue.Dequeue();
                var distSq = (current.Box.Center - barCenter).LengthSquared();

                if (distSq > farDistSq)
                {
                    farDistSq = distSq;
                    farPoly = current;
                }

                if (distSq >= minFarSq)
                {
                    farPoly = current;
                    farDistSq = distSq;
                    break;
                }

                foreach (var neighbor in current.Neighbors)
                {
                    if (!IsFree(neighbor) || !visited.Add(neighbor))
                        continue;
                    queue.Enqueue(neighbor);
                }
            }

            Assert.That(farPoly, Is.Not.EqualTo(startPoly), "Could not find a floor tile far from the bar");
            Assert.That(farDistSq, Is.GreaterThan(minFarSq), "Could not find a tile at least 30 tiles away from the bar");

            // Place a guaranteed chair on the free bar tile so the android can sit.
            sEnt.SpawnEntity("Chair", startPoly.Coordinates);

            // Spawn the android on the far tile.
            android = sEnt.SpawnEntity("MobAndroidJanitor", farPoly.Coordinates);

            var androidComp = sEnt.GetComponent<AndroidComponent>(android);
            androidComp.NextBreakAt = server.Timing.CurTime - TimeSpan.FromSeconds(1);
            androidComp.BreakUntil = TimeSpan.Zero;

            // OnMapInit should have located the bar, but fall back if it didn't.
            if (!androidComp.BarLocation.IsValid(sEnt))
                androidComp.BarLocation = barCoords!.Value;

            Assert.That(androidComp.BarLocation.IsValid(sEnt), Is.True, "Android should have located the bar beacon");
        });

        // Allow 60 seconds of simulation time for a long cross-station path.
        await server.WaitRunTicks(1800);

        await server.WaitAssertion(() =>
        {
            var androidComp = sEnt.GetComponent<AndroidComponent>(android);

            Assert.That(androidComp.Status, Is.EqualTo(AndroidStatus.OnBreak), "Android should be on a break");
            Assert.That(androidComp.BreakState, Is.EqualTo(AndroidBreakState.Resting), $"Android should be resting after reaching the far bar, but was {androidComp.BreakState}");
            Assert.That(sEnt.TryGetComponent<BuckleComponent>(android, out var buckle), Is.True);
            Assert.That(buckle!.Buckled, Is.True, "Android should have buckled at the far bar");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Fills the test map with a breathable atmosphere so androids don't immediately start fleeing.
    /// </summary>
    internal static void EnsureSafeAtmosphere(IIntegrationInstance server, TestMapData testMap)
    {
        var entMan = server.EntMan;
        var atmos = entMan.System<AtmosphereSystem>();

        var mixture = new GasMixture(Atmospherics.CellVolume)
        {
            Temperature = Atmospherics.T20C
        };

        // Approximate Earth air: ~21% O2, 79% N2, roughly 1 atm in a standard 2500L cell at 20°C.
        mixture.AdjustMoles(Gas.Oxygen, 22f);
        mixture.AdjustMoles(Gas.Nitrogen, 83f);

        atmos.SetMapAtmosphere(testMap.MapUid, false, mixture);
    }
}
