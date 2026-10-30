// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Damage.Systems;
using Content.Shared.Damage.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server._Harvest.AndroidNpc.BehaviorTree;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Services;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Server.Atmos.Components;
using Content.Server.Radio;
using Content.Shared._EinsteinEngines.Language;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Chat;
using Content.Shared.Gravity;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Server.Medical.SuitSensors;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Medical.SuitSensors;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Speech;
using Content.Shared.Verbs;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.UnitTesting;
using Content.IntegrationTests.Pair;
using static Content.IntegrationTests.Tests._Harvest.AndroidNpc.AndroidLifecycleAndServiceTests;

namespace Content.IntegrationTests.Tests._Harvest.AndroidNpc;

/// <summary>
/// The paramedic android, and the janitor dragging mice with its hands full.
/// </summary>
[TestFixture]
[TestOf(typeof(AndroidMedicSystem))]
public sealed class AndroidParamedicTests
{
    private static void Hurt(IIntegrationInstance server, EntityUid target, float amount)
    {
        var blunt = server.ProtoMan.Index<DamageTypePrototype>("Blunt");
        server.System<DamageableSystem>().TryChangeDamage(target, new DamageSpecifier(blunt, amount), true, canMiss: false);
    }

    private static float Damage(IEntityManager sEnt, EntityUid uid) => sEnt.GetComponent<DamageableComponent>(uid).TotalDamage.Float();

    /// <summary>
    /// Dresses the mob in a jumpsuit whose suit sensor reports the given mode, as crew wear them.
    /// </summary>
    internal static void WearSensors(IIntegrationInstance server, EntityUid mob, SuitSensorMode mode = SuitSensorMode.SensorCords)
    {
        var sEnt = server.EntMan;
        var suit = sEnt.SpawnEntity("ClothingUniformJumpsuitColorGrey", sEnt.GetComponent<TransformComponent>(mob).Coordinates);
        Assert.That(server.System<InventorySystem>().TryEquip(mob, suit, "jumpsuit", silent: true, force: true), Is.True);
        server.System<SuitSensorSystem>().SetSensor((suit, null), mode, mob);
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Janitor_PullsADeadMouseWithBothHandsFull(bool withoutBag)
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid janitor = default;

        await server.WaitAssertion(() => janitor = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords));
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var hands = server.System<SharedHandsSystem>();
            if (withoutBag && server.System<InventorySystem>().TryGetSlotEntity(janitor, "back", out var bag))
                sEnt.DeleteEntity(bag);

            while (hands.CountFreeHands(janitor) > 0)
                hands.TryPickupAnyHand(janitor, sEnt.SpawnEntity("Bucket", map.GridCoords));
            Assert.That(hands.EnumerateHeld(janitor).Select(i => sEnt.GetComponent<MetaDataComponent>(i).EntityPrototype?.ID), Does.Contain("MopItem"));

            var mouse = sEnt.SpawnEntity("MobMouse", map.GridCoords.Offset(new Vector2(0.5f, 0)));
            server.System<MobStateSystem>().ChangeMobState(mouse, MobState.Dead);
            sEnt.GetComponent<AndroidInventoryComponent>(janitor).PestTarget = mouse;

            var status = new StartPullPestAction().Tick(janitor, AndroidBlackboard.Create(sEnt, janitor, new BlacklistManager()), 0f);
            Assert.That(status, Is.EqualTo(BehaviorStatus.Success));
            Assert.That(sEnt.GetComponent<PullableComponent>(mouse).Puller, Is.EqualTo(janitor), "A hand was freed to drag the mouse");
            Assert.That(hands.EnumerateHeld(janitor).Select(i => sEnt.GetComponent<MetaDataComponent>(i).EntityPrototype?.ID), Does.Contain("MopItem"),
                "The mop stays in hand");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RoundStart_FillsEveryFreeAndroidJob()
    {
        var pair = await AndroidTestPool.GetServerClient(new PoolSettings { Connected = true, DummyTicker = false, Dirty = true, Map = "Saltern" });
        var server = pair.Server;
        var sEnt = server.EntMan;

        await server.WaitRunTicks(30);
        await server.WaitAssertion(() =>
        {
            var jobs = new List<AndroidJob>();
            var query = sEnt.AllEntityQueryEnumerator<AndroidComponent>();
            while (query.MoveNext(out _, out var android))
                jobs.Add(android.Job);

            TestContext.WriteLine($"Androids: {string.Join(", ", jobs)}");
            Assert.That(jobs, Is.EquivalentTo(Enum.GetValues<AndroidJob>()), "One android per free job as soon as the round starts");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Paramedic_TreatsAnInjuredCrewMember()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid patient = default, medic = default;

        // Hurt before the paramedic arrives: its first scan finds the patient. Finding nobody, it would patrol
        // off the one-tile test grid and drift away in space.
        await server.WaitAssertion(() =>
        {
            patient = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.8f, 0)));
            Hurt(server, patient, 40);
            medic = sEnt.SpawnEntity("MobAndroidParamedic", map.GridCoords);
        });
        await server.WaitRunTicks(300);

        await server.WaitAssertion(() =>
        {
            var android = sEnt.GetComponent<AndroidComponent>(medic);
            Assert.That(Damage(sEnt, patient), Is.LessThan(40f),
                $"The paramedic used a kit on the patient; status {android.Status}, said: {string.Join(" | ", android.RecentLines)}");
            Assert.That(sEnt.GetComponent<AndroidComponent>(medic).RecentLines, Is.Not.Empty, "It talked to the patient");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Paramedic_CarriesBodiesToMedbay()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        var xforms = sEnt.System<SharedTransformSystem>();
        await BuildHall(server, map, 14);
        EntityUid body = default, medic = default;

        await server.WaitAssertion(() =>
        {
            body = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 10.5f, 0.5f));
            Hurt(server, body, 250);
            server.System<MobStateSystem>().ChangeMobState(body, MobState.Dead);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() => medic = sEnt.SpawnEntity("MobAndroidParamedic", new EntityCoordinates(map.Grid.Owner, 12.5f, 0.5f)));
        await server.WaitRunTicks(900);

        await server.WaitAssertion(() =>
        {
            Assert.That(xforms.GetWorldPosition(body).X, Is.LessThan(4f),
                $"The body was carried to medbay; medic at {xforms.GetWorldPosition(medic)}, body at {xforms.GetWorldPosition(body)}, said: {string.Join(" | ", sEnt.GetComponent<AndroidComponent>(medic).RecentLines)}");
            Assert.That(server.System<AndroidMedicSystem>().IsCarrying(medic, body), Is.False, "and left there for the doctors");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A long, three-tile-wide corridor with gravity and the medbay beacon at its start.
    /// </summary>
    private static async Task BuildHall(IIntegrationInstance server, TestMapData map, int length)
    {
        var sEnt = server.EntMan;
        await server.WaitAssertion(() =>
        {
            AndroidNpcLogicTests.EnsureSafeAtmosphere(server, map);
            var tile = new Tile(map.Tile.Tile.TypeId);
            sEnt.System<SharedMapSystem>().SetTiles(map.Grid,
                Enumerable.Range(0, length).SelectMany(x => Enumerable.Range(-1, 3).Select(y => (new Vector2i(x, y), tile))).ToList());
            var gravity = sEnt.EnsureComponent<GravityComponent>(map.Grid.Owner);
            gravity.Enabled = true;
            gravity.Inherent = true;

            // New tiles of the grid's own atmosphere are vacuum: without it the map's breathable air applies.
            sEnt.RemoveComponent<GridAtmosphereComponent>(map.Grid.Owner);
            sEnt.SpawnEntity("DefaultStationBeaconMedbay", new EntityCoordinates(map.Grid.Owner, 0.5f, 0.5f));
        });
        await server.WaitRunTicks(25);
    }

    [Test]
    public async Task Paramedic_GoesToCriticalCrewAnywhereButLeavesTheMorgueAlone()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        await BuildHall(server, map, 40);
        EntityUid medic = default, critical = default;

        await server.WaitAssertion(() =>
        {
            sEnt.SpawnEntity("DefaultStationBeaconMorgue", new EntityCoordinates(map.Grid.Owner, 20.5f, 0.5f));
            var body = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 21.5f, 0.5f));
            WearSensors(server, body);
            server.System<MobStateSystem>().ChangeMobState(body, MobState.Dead);

            critical = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 36.5f, 0.5f));
            WearSensors(server, critical);
            Hurt(server, critical, 120);
            medic = sEnt.SpawnEntity("MobAndroidParamedic", new EntityCoordinates(map.Grid.Owner, 1.5f, 0.5f));
        });
        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
            Assert.That(sEnt.GetComponent<AndroidMedicComponent>(medic).Patient, Is.EqualTo(critical),
                "A critical crew member 35 tiles away shows on the crew monitor and is attended to; the body at the morgue is left there"));

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Far away crew only show when their suit sensors give coordinates. The paramedic reads them itself: it carries
    /// no crew monitor an antagonist could take from it.
    /// </summary>
    [Test]
    public async Task Paramedic_SeesFarCrewOnlyThroughCoordinateSensors()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        await BuildHall(server, map, 40);
        EntityUid medic = default;

        await server.WaitAssertion(() => medic = sEnt.SpawnEntity("MobAndroidParamedic", new EntityCoordinates(map.Grid.Owner, 1.5f, 0.5f)));
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var inventory = sEnt.GetComponent<AndroidInventoryComponent>(medic);
            Assert.That(server.System<AndroidInventorySystem>().FindItemByPrototype(medic, inventory, "HandheldCrewMonitor"), Is.Null,
                "No crew monitor to loot");

            var critical = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 36.5f, 0.5f));
            WearSensors(server, critical, SuitSensorMode.SensorVitals);
            Hurt(server, critical, 120);
        });
        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
            Assert.That(sEnt.GetComponent<AndroidMedicComponent>(medic).Patient, Is.Null,
                "Sensors without coordinates do not say where the patient is"));

        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public async Task Paramedic_PutsBodiesOnAStasisBedOnly(bool withStasisBed)
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        await BuildHall(server, map, 16);
        EntityUid medic = default, body = default, bed = default, table = default;
        EntityUid? stasis = null;

        await server.WaitAssertion(() =>
        {
            // The operating table and the medical bed are closer to medbay than the stasis bed.
            table = sEnt.SpawnEntity("OperatingTable", new EntityCoordinates(map.Grid.Owner, 1.5f, 1.5f));
            bed = sEnt.SpawnEntity("MedicalBed", new EntityCoordinates(map.Grid.Owner, 2.5f, 1.5f));
            if (withStasisBed)
                stasis = sEnt.SpawnEntity("StasisBed", new EntityCoordinates(map.Grid.Owner, 4.5f, 1.5f));
            body = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 12.5f, 0.5f));
            WearSensors(server, body, SuitSensorMode.SensorCords);
            Hurt(server, body, 250);
            server.System<MobStateSystem>().ChangeMobState(body, MobState.Dead);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() => medic = sEnt.SpawnEntity("MobAndroidParamedic", new EntityCoordinates(map.Grid.Owner, 14.5f, 0.5f)));
        await server.WaitRunTicks(1200);

        await server.WaitAssertion(() =>
        {
            var buckledTo = sEnt.GetComponent<Content.Shared.Buckle.Components.BuckleComponent>(body).BuckledTo;
            Assert.That(buckledTo, Is.Not.EqualTo(table), "Never on the operating table");
            Assert.That(buckledTo, Is.Not.EqualTo(bed), "Never on a medical bed, where it would rot");
            Assert.That(buckledTo, Is.EqualTo(stasis),
                $"On the stasis bed if there is one, else left lying at medbay; said: {string.Join(" | ", sEnt.GetComponent<AndroidComponent>(medic).RecentLines)}");
            Assert.That(sEnt.GetComponent<TransformComponent>(body).LocalPosition.X, Is.LessThan(6f), "It was carried to medbay");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Paramedic_ComesToAFarCallerOnTheRadioAndInjectsBicaridine()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        var xforms = sEnt.System<SharedTransformSystem>();
        await BuildHall(server, map, 40);
        EntityUid medic = default, caller = default;

        await server.WaitAssertion(() =>
        {
            caller = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 36.5f, 0.5f));
            medic = sEnt.SpawnEntity("MobAndroidParamedic", new EntityCoordinates(map.Grid.Owner, 1.5f, 0.5f));
            Name(sEnt, medic, "Zephyrin");
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Hurt(server, caller, 30);
            var channel = server.ProtoMan.Index<RadioChannelPrototype>("Medical");
            var language = server.ProtoMan.Index<LanguagePrototype>("TauCetiBasic");
            var chat = new ChatMessage(ChatChannel.Radio, "Zephyrin, soigne-moi", string.Empty, sEnt.GetNetEntity(caller), null);
            var ev = new RadioReceiveEvent(caller, channel, chat, chat, language, caller);
            sEnt.EventBus.RaiseLocalEvent(medic, ref ev);
            Assert.That(sEnt.GetComponent<AndroidMedicComponent>(medic).Caller, Is.EqualTo(caller));
        });

        var reached = false;
        for (var i = 0; i < 40 && !reached; i++)
        {
            await server.WaitRunTicks(30);
            await server.WaitAssertion(() =>
                reached = (xforms.GetWorldPosition(medic) - xforms.GetWorldPosition(caller)).Length() < 2.5f);
        }

        Assert.That(reached, Is.True, "The paramedic walked the 35 tiles to the caller");
        await server.WaitRunTicks(120);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<SharedSolutionContainerSystem>().TryGetInjectableSolution(caller, out _, out var blood), Is.True);
            var brute = sEnt.GetComponent<DamageableComponent>(caller).DamagePerGroup.GetValueOrDefault("Brute").Float();
            Assert.That(blood!.GetReagentQuantity(new ReagentId("Bicaridine", null)) > 0 || brute < 30f, Is.True,
                "Bicaridine was injected and is healing the bruises; nothing is healed directly");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Paramedic_AnswersOnTheRadio()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid medic = default, speaker = default;

        await server.WaitAssertion(() =>
        {
            sEnt.SpawnEntity("DefaultStationBeaconBar", map.GridCoords);
            medic = sEnt.SpawnEntity("MobAndroidParamedic", map.GridCoords);
            speaker = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.8f, 0)));
            Name(sEnt, medic, "Zephyrin");
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.GetComponent<ActiveRadioComponent>(medic).Channels.Select(c => c.Id), Does.Contain("Medical").And.Contain("Common"));

            var channel = server.ProtoMan.Index<RadioChannelPrototype>("Common");
            var language = server.ProtoMan.Index<LanguagePrototype>("TauCetiBasic");
            var chat = new ChatMessage(ChatChannel.Radio, "Zephyrin, il y a un blessé au bar", string.Empty, sEnt.GetNetEntity(speaker), null);
            var ev = new RadioReceiveEvent(speaker, channel, chat, chat, language, speaker);
            sEnt.EventBus.RaiseLocalEvent(medic, ref ev);

            Assert.That(sEnt.GetComponent<AndroidOrderComponent>(medic).OrderType, Is.EqualTo(AndroidOrderType.RespondToCall));
        });
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
            Assert.That(sEnt.GetComponent<AndroidComponent>(medic).RecentLines.Any(l => l.StartsWith("J'arrive tout de suite")), Is.True,
                "The android answers the radio call"));

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Paramedic_PutsTheCriticalPatientOnAStasisBed()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        await BuildHall(server, map, 14);
        EntityUid medic = default, patient = default, bed = default;

        await server.WaitAssertion(() =>
        {
            bed = sEnt.SpawnEntity("StasisBed", new EntityCoordinates(map.Grid.Owner, 2.5f, 1.5f));
            patient = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 11.5f, 0.5f));
            Hurt(server, patient, 120);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() => medic = sEnt.SpawnEntity("MobAndroidParamedic", new EntityCoordinates(map.Grid.Owner, 12.5f, 0.5f)));
        await server.WaitRunTicks(1500);

        await server.WaitAssertion(() =>
        {
            var state = sEnt.GetComponent<MobStateComponent>(patient).CurrentState;
            var buckle = sEnt.GetComponent<Content.Shared.Buckle.Components.BuckleComponent>(patient);
            Assert.That(buckle.BuckledTo == bed || state == MobState.Alive, Is.True,
                $"The critical patient lies on the stasis bed, unless it recovered on the way; {state}, buckled to {buckle.BuckledTo}, said: {string.Join(" | ", sEnt.GetComponent<AndroidComponent>(medic).RecentLines)}");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Paramedic_GivesMedicineForPoisonWhenAsked()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var fr = server.ProtoMan.AndroidFeedback("fr");
        EntityUid medic = default, patient = default;

        await server.WaitAssertion(() =>
        {
            medic = sEnt.SpawnEntity("MobAndroidParamedic", map.GridCoords);
            patient = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.8f, 0)));
            Name(sEnt, medic, "Zephyrin");
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var poison = server.ProtoMan.Index<DamageTypePrototype>("Poison");
            server.System<DamageableSystem>().TryChangeDamage(patient, new DamageSpecifier(poison, 15), true, canMiss: false);
            sEnt.EventBus.RaiseLocalEvent(medic, new ListenEvent("Zephyrin, soigne-moi", patient));
            Assert.That(sEnt.GetComponent<AndroidMedicComponent>(medic).Caller, Is.EqualTo(patient), "Poison is treatable, with dylovene");
        });
        await server.WaitRunTicks(300);

        await server.WaitAssertion(() =>
        {
            // Dylovene was injected and is healing the poison over time.
            var toxin = sEnt.GetComponent<DamageableComponent>(patient).DamagePerGroup.GetValueOrDefault("Toxin").Float();
            Assert.That(toxin, Is.LessThan(15f), "The poison is going down");
            Assert.That(sEnt.GetComponent<AndroidComponent>(medic).RecentLines, Does.Contain(fr.AckHealMe));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Android_FleesAwayFromItsAttacker()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        var xforms = sEnt.System<SharedTransformSystem>();
        EntityUid android = default, attacker = default;

        await server.WaitAssertion(() =>
        {
            AndroidNpcLogicTests.EnsureSafeAtmosphere(server, map);
            var tile = new Tile(map.Tile.Tile.TypeId);
            sEnt.System<SharedMapSystem>().SetTiles(map.Grid,
                Enumerable.Range(0, 16).SelectMany(x => Enumerable.Range(-2, 5).Select(y => (new Vector2i(x, y), tile))).ToList());
            var gravity = sEnt.EnsureComponent<GravityComponent>(map.Grid.Owner);
            gravity.Enabled = true;
            gravity.Inherent = true;

            // New tiles of the grid's own atmosphere are vacuum: without it the map's breathable air applies.
            sEnt.RemoveComponent<GridAtmosphereComponent>(map.Grid.Owner);
        });
        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            attacker = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 5.5f, 0.5f));
            android = sEnt.SpawnEntity("MobAndroidCook", new EntityCoordinates(map.Grid.Owner, 6.5f, 0.5f));
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var blunt = server.ProtoMan.Index<DamageTypePrototype>("Blunt");
            server.System<DamageableSystem>().TryChangeDamage(android, new DamageSpecifier(blunt, 10), true, origin: attacker, canMiss: false);
        });
        await server.WaitRunTicks(150);

        await server.WaitAssertion(() =>
        {
            var distance = (xforms.GetWorldPosition(android) - xforms.GetWorldPosition(attacker)).Length();
            Assert.That(distance, Is.GreaterThan(5f), "The android ran away from its attacker, not towards it");
            Assert.That(xforms.GetWorldPosition(android).X, Is.GreaterThan(xforms.GetWorldPosition(attacker).X), "on the far side");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Paramedic_KeepsCarryingAPatientDriftingInAndOutOfCrit()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        await BuildHall(server, map, 30);
        var mobState = server.System<MobStateSystem>();
        EntityUid medic = default, patient = default;

        await server.WaitAssertion(() =>
        {
            patient = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 26.5f, 0.5f));
            Hurt(server, patient, 120);
        });
        await server.WaitRunTicks(30);
        await server.WaitAssertion(() => medic = sEnt.SpawnEntity("MobAndroidParamedic", new EntityCoordinates(map.Grid.Owner, 28.5f, 0.5f)));

        var pulled = false;
        for (var i = 0; i < 30 && !pulled; i++)
        {
            await server.WaitRunTicks(10);
            await server.WaitAssertion(() => pulled = server.System<AndroidMedicSystem>().IsCarrying(medic, patient));
        }

        Assert.That(pulled, Is.True, "The paramedic picked up the critical patient");

        for (var i = 0; i < 4; i++)
        {
            var state = i % 2 == 0 ? MobState.Alive : MobState.Critical;
            await server.WaitAssertion(() => mobState.ChangeMobState(patient, state));
            await server.WaitRunTicks(20);
        }

        await server.WaitAssertion(() =>
        {
            var carrying = server.ProtoMan.AndroidJobData(AndroidJob.Paramedic)!.LinesFor("fr")!.Carrying;
            var said = sEnt.GetComponent<AndroidComponent>(medic).RecentLines;
            Assert.That(server.System<AndroidMedicSystem>().IsCarrying(medic, patient), Is.True,
                $"Still carrying the patient; said: {string.Join(" | ", said)}");
            Assert.That(said.Count(carrying.Contains), Is.LessThanOrEqualTo(1), "The transfer is announced once");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Paramedic_AnswersCallsForHelp()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var fr = server.ProtoMan.AndroidFeedback("fr");
        EntityUid medic = default, janitor = default, hurt = default, fine = default;

        await server.WaitAssertion(() =>
        {
            sEnt.SpawnEntity("DefaultStationBeaconBar", map.GridCoords);
            medic = sEnt.SpawnEntity("MobAndroidParamedic", map.GridCoords);
            janitor = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords.Offset(new Vector2(-0.8f, 0)));
            hurt = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.8f, 0)));
            fine = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0, 0.8f)));
            Name(sEnt, medic, "Zephyrin");
            Name(sEnt, janitor, "Zebulon");
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Hurt(server, hurt, 10);
            sEnt.EventBus.RaiseLocalEvent(medic, new ListenEvent("Zephyrin, soigne-moi", hurt));
            Assert.That(sEnt.GetComponent<AndroidMedicComponent>(medic).Caller, Is.EqualTo(hurt), "A light injury is treated when asked");
            sEnt.EventBus.RaiseLocalEvent(janitor, new ListenEvent("Zebulon, soigne-moi", hurt));
        });
        await server.WaitRunTicks(300);

        await server.WaitAssertion(() =>
        {
            Assert.That(Damage(sEnt, hurt), Is.LessThan(10f), "The caller was treated");
            Assert.That(sEnt.GetComponent<AndroidComponent>(janitor).RecentLines, Does.Contain(fr.NotAMedic));

            sEnt.EventBus.RaiseLocalEvent(medic, new ListenEvent("Zephyrin, soigne-moi", fine));
        });
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.GetComponent<AndroidComponent>(medic).LastSaid, Is.EqualTo(fr.NotHurt));

            // Back on the grid next to the bar beacon: it patrols off the one-tile test grid meanwhile.
            sEnt.System<SharedTransformSystem>().SetCoordinates(medic, map.GridCoords);
            sEnt.EventBus.RaiseLocalEvent(medic, new ListenEvent("Zephyrin, il y a un blessé au bar", fine));

            var order = sEnt.GetComponent<AndroidOrderComponent>(medic);
            Assert.That(order.OrderType, Is.EqualTo(AndroidOrderType.RespondToCall));
            Assert.That(order.Target, Is.Not.Null, "It heads to the bar beacon");
        });
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
            Assert.That(sEnt.GetComponent<AndroidComponent>(medic).RecentLines.Any(l => l.StartsWith("J'arrive tout de suite")), Is.True));

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ContextMenu_HealMeOnlyForTheParamedic()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var loc = server.ResolveDependency<ILocalizationManager>();
        EntityUid player = default, medic = default, janitor = default;

        await server.WaitAssertion(() =>
        {
            player = sEnt.SpawnEntity("MobHuman", map.GridCoords);
            medic = sEnt.SpawnEntity("MobAndroidParamedic", map.GridCoords.Offset(new Vector2(0.8f, 0)));
            janitor = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords.Offset(new Vector2(-0.8f, 0)));
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var verbs = server.System<SharedVerbSystem>();
            var healMe = loc.GetString("android-verb-heal-me");
            Assert.That(verbs.GetLocalVerbs(medic, player, typeof(Verb)).Select(v => v.Text), Does.Contain(healMe));
            Assert.That(verbs.GetLocalVerbs(janitor, player, typeof(Verb)).Select(v => v.Text), Does.Not.Contain(healMe));
        });

        await pair.CleanReturnAsync();
    }
}
