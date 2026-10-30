// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using System.Text;
using Content.IntegrationTests.Pair;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.Radio;
using Content.Shared._EinsteinEngines.Language;
using Content.Shared.Bed.Components;
using Content.Shared.Buckle.Components;
using Content.Shared.Chat;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.NPC;
using Content.Shared.Radio;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.UnitTesting;
using static Content.IntegrationTests.Tests._Harvest.AndroidNpc.AndroidLifecycleAndServiceTests;

namespace Content.IntegrationTests.Tests._Harvest.AndroidNpc;

/// <summary>
/// Androids in a real round on Saltern: closed doors with their access, atmosphere, power, and the androids
/// spawned by the lifecycle at round start. Slow (a few game minutes each).
/// </summary>
[TestFixture]
public sealed class AndroidRealRoundTests
{
    private static PoolSettings RealRound => new() { Connected = true, DummyTicker = false, Dirty = true, Map = "Saltern" };

    /// <summary>
    /// The android of the given job spawned by the lifecycle.
    /// </summary>
    internal static EntityUid FindAndroid(IEntityManager sEnt, AndroidJob job)
    {
        var query = sEnt.AllEntityQueryEnumerator<AndroidComponent>();
        while (query.MoveNext(out var uid, out var android))
        {
            if (android.Job == job)
                return uid;
        }

        Assert.Fail($"No {job} android in the round");
        return default;
    }

    /// <summary>
    /// The free floor tile nearest to the beacon of the given prototype.
    /// </summary>
    internal static EntityCoordinates FreeTileNear(IEntityManager sEnt, string beaconPrototype)
    {
        var pathfinding = sEnt.System<PathfindingSystem>();
        var query = sEnt.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out _, out var meta, out var xform))
        {
            if (meta.EntityPrototype?.ID != beaconPrototype || xform.GridUid is not { } grid)
                continue;

            var origin = xform.Coordinates.Position;
            for (var r = 0; r <= 3; r++)
            for (var dx = -r; dx <= r; dx++)
            for (var dy = -r; dy <= r; dy++)
            {
                if (pathfinding.GetPoly(new EntityCoordinates(grid, origin + new Vector2(dx, dy))) is { } poly && poly.Data.IsFreeSpace &&
                    (poly.Data.Flags & (PathfindingBreadcrumbFlag.Space | PathfindingBreadcrumbFlag.Invalid)) == 0)
                {
                    var tile = poly.Box.Center.Floored();
                    return new EntityCoordinates(grid, new Vector2(tile.X + 0.5f, tile.Y + 0.5f));
                }
            }
        }

        Assert.Fail($"No free floor near {beaconPrototype}");
        return default;
    }

    private static void Hurt(IIntegrationInstance server, EntityUid target, float amount)
    {
        var blunt = server.ProtoMan.Index<DamageTypePrototype>("Blunt");
        server.System<DamageableSystem>().TryChangeDamage(target, new DamageSpecifier(blunt, amount), true, canMiss: false);
    }

    /// <summary>
    /// Runs the round for up to <paramref name="seconds"/> of game time, sampling every five seconds until
    /// <paramref name="done"/> holds; the samples go to the test output.
    /// </summary>
    private static async Task<bool> RunUntil(TestPair pair, int seconds, System.Func<string> sample, System.Func<bool> done)
    {
        var server = pair.Server;
        var step = (int) (server.ResolveDependency<IGameTiming>().TickRate * 5);
        var log = new StringBuilder();
        var finished = false;

        for (var t = 5; t <= seconds && !finished; t += 5)
        {
            await server.WaitRunTicks(step);
            await server.WaitAssertion(() =>
            {
                log.AppendLine($"{t,3}s {sample()}");
                finished = done();
            });
        }

        TestContext.WriteLine(log.ToString());
        return finished;
    }

    /// <summary>
    /// Critical crew anywhere on the station show on the paramedic's crew monitor: it walks there through the
    /// doors it has access to, gives epinephrine, and drags them onto a medbay bed.
    /// </summary>
    [Test]
    [TestCase("DefaultStationBeaconArrivals")]
    [TestCase("DefaultStationBeaconChapel")]
    [TestCase("DefaultStationBeaconLibrary")]
    public async Task Paramedic_RescuesCriticalCrewAcrossTheStation(string place)
    {
        var pair = await AndroidTestPool.GetServerClient(RealRound);
        var server = pair.Server;
        var sEnt = server.EntMan;
        var xforms = sEnt.System<SharedTransformSystem>();
        var medicSystem = server.System<Content.Server._Harvest.AndroidNpc.Systems.AndroidMedicSystem>();
        EntityUid medic = default, patient = default;
        Vector2 medbay = default;

        // Let the round start and the navmesh build.
        await server.WaitRunTicks(300);
        await server.WaitAssertion(() =>
        {
            medic = FindAndroid(sEnt, AndroidJob.Paramedic);
            medbay = xforms.ToMapCoordinates(FreeTileNear(sEnt, "DefaultStationBeaconMedbay")).Position;
            patient = sEnt.SpawnEntity("MobHuman", FreeTileNear(sEnt, place));
            AndroidParamedicTests.WearSensors(server, patient);
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            Hurt(server, patient, 110);
            Assert.That(sEnt.GetComponent<MobStateComponent>(patient).CurrentState, Is.EqualTo(MobState.Critical));
            TestContext.WriteLine($"Patient near {place}, {(xforms.GetWorldPosition(patient) - xforms.GetWorldPosition(medic)).Length():0} m from the paramedic {sEnt.GetComponent<MetaDataComponent>(medic).EntityName}");
        });

        bool reached = false, carried = false, injected = false;
        var delivered = await RunUntil(pair, 300,
            () =>
            {
                var distance = (xforms.GetWorldPosition(patient) - xforms.GetWorldPosition(medic)).Length();
                reached |= distance < 2f;
                carried |= medicSystem.IsCarrying(medic, patient);
                injected |= sEnt.GetComponent<AndroidMedicComponent>(medic).InjectedAt.ContainsKey(patient);
                var steering = sEnt.TryGetComponent<NPCSteeringComponent>(medic, out var s) ? s.Status.ToString() : "-";
                var medicComp = sEnt.GetComponent<AndroidMedicComponent>(medic);
                var nearby = string.Join(",", sEnt.System<EntityLookupSystem>().GetEntitiesInRange(medic, 1.5f)
                    .Where(e => sEnt.HasComponent<Content.Shared.Doors.Components.DoorComponent>(e) || sEnt.HasComponent<Content.Shared.Climbing.Components.ClimbableComponent>(e) ||
                                sEnt.HasComponent<MobStateComponent>(e) && e != medic && e != patient)
                    .Select(e => $"{sEnt.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID}" +
                                 (sEnt.TryGetComponent<Content.Shared.Doors.Components.DoorComponent>(e, out var door) ? $"({door.State})" : "")));
                var medicPos = xforms.GetWorldPosition(medic);
                return $"at=({medicPos.X:0.0},{medicPos.Y:0.0}) near=[{nearby}] dist={distance,5:0.0} toMedbay={(xforms.GetWorldPosition(patient) - medbay).Length(),5:0.0} steering={steering} " +
                       $"carrying={medicSystem.IsCarrying(medic, patient)} target={(medicComp.Patient == patient ? "patient" : medicComp.Patient?.ToString() ?? "none")} " +
                       $"status={sEnt.GetComponent<AndroidComponent>(medic).Status} injected={injected} " +
                       $"state={sEnt.GetComponent<MobStateComponent>(patient).CurrentState} said={sEnt.GetComponent<AndroidComponent>(medic).LastSaid}";
            },
            () => sEnt.GetComponent<BuckleComponent>(patient).BuckledTo is { } bed &&
                  (sEnt.HasComponent<StasisBedComponent>(bed) || sEnt.HasComponent<HealOnBuckleComponent>(bed)));

        Assert.Multiple(() =>
        {
            Assert.That(reached, Is.True, "The paramedic reached the patient");
            Assert.That(injected, Is.True, "gave epinephrine");
            Assert.That(carried, Is.True, "carried them");
            Assert.That(delivered, Is.True, "and buckled them onto a medbay bed");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A body far away is brought back to medbay too, and let go there for the doctors.
    /// </summary>
    [Test]
    public async Task Paramedic_BringsABodyBackToMedbay()
    {
        var pair = await AndroidTestPool.GetServerClient(RealRound);
        var server = pair.Server;
        var sEnt = server.EntMan;
        var xforms = sEnt.System<SharedTransformSystem>();
        var medicSystem = server.System<Content.Server._Harvest.AndroidNpc.Systems.AndroidMedicSystem>();
        EntityUid medic = default, body = default;
        Vector2 medbay = default;

        await server.WaitRunTicks(300);
        await server.WaitAssertion(() =>
        {
            medic = FindAndroid(sEnt, AndroidJob.Paramedic);
            medbay = xforms.ToMapCoordinates(FreeTileNear(sEnt, "DefaultStationBeaconMedbay")).Position;
            body = sEnt.SpawnEntity("MobHuman", FreeTileNear(sEnt, "DefaultStationBeaconChapel"));
            AndroidParamedicTests.WearSensors(server, body);
            Hurt(server, body, 250);
            server.System<MobStateSystem>().ChangeMobState(body, MobState.Dead);
        });

        var delivered = await RunUntil(pair, 300,
            () => $"medic->body={(xforms.GetWorldPosition(body) - xforms.GetWorldPosition(medic)).Length(),5:0.0} body->medbay={(xforms.GetWorldPosition(body) - medbay).Length(),5:0.0} " +
                  $"carried={medicSystem.IsCarrying(medic, body)}",
            () => (xforms.GetWorldPosition(body) - medbay).Length() < 8f && !medicSystem.IsCarrying(medic, body));

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.True, "The body was brought to medbay and let go");
            Assert.That((xforms.GetWorldPosition(body) - medbay).Length(), Is.LessThan(8f), "The body lies in medbay");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Injured crew far away (not critical, so not flagged on the monitor) call the paramedic on the radio:
    /// it comes over and treats them with injections.
    /// </summary>
    [Test]
    public async Task Paramedic_AnswersARadioCallFromAcrossTheStation()
    {
        var pair = await AndroidTestPool.GetServerClient(RealRound);
        var server = pair.Server;
        var sEnt = server.EntMan;
        var xforms = sEnt.System<SharedTransformSystem>();
        var medicSystem = server.System<Content.Server._Harvest.AndroidNpc.Systems.AndroidMedicSystem>();
        EntityUid medic = default, caller = default;

        await server.WaitRunTicks(300);
        await server.WaitAssertion(() =>
        {
            medic = FindAndroid(sEnt, AndroidJob.Paramedic);
            Name(sEnt, medic, "Zephyrin");
            caller = sEnt.SpawnEntity("MobHuman", FreeTileNear(sEnt, "DefaultStationBeaconLibrary"));
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            Hurt(server, caller, 40);
            var channel = server.ProtoMan.Index<RadioChannelPrototype>("Common");
            var language = server.ProtoMan.Index<LanguagePrototype>("TauCetiBasic");
            var chat = new ChatMessage(ChatChannel.Radio, "Zephyrin, soigne-moi", string.Empty, sEnt.GetNetEntity(caller), null);
            var ev = new RadioReceiveEvent(caller, channel, chat, chat, language, caller);
            sEnt.EventBus.RaiseLocalEvent(medic, ref ev);
        });

        var reached = false;
        var healed = await RunUntil(pair, 240,
            () =>
            {
                var distance = (xforms.GetWorldPosition(caller) - xforms.GetWorldPosition(medic)).Length();
                reached |= distance < 2.5f;
                var steering = sEnt.TryGetComponent<NPCSteeringComponent>(medic, out var s) ? s.Status.ToString() : "-";
                var medicComp = sEnt.GetComponent<AndroidMedicComponent>(medic);
                return $"dist={distance,5:0.0} steering={steering} target={(medicComp.Patient == caller ? "caller" : medicComp.Patient?.ToString() ?? "none")} " +
                       $"status={sEnt.GetComponent<AndroidComponent>(medic).Status} damage={sEnt.GetComponent<DamageableComponent>(caller).TotalDamage} " +
                       $"said={sEnt.GetComponent<AndroidComponent>(medic).LastSaid}";
            },
            () => reached && sEnt.GetComponent<DamageableComponent>(caller).TotalDamage < 30);

        Assert.That(healed, Is.True, "The paramedic came over and treated the caller");
        await pair.CleanReturnAsync();
    }
}
