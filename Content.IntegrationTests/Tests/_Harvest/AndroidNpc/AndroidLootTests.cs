// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Shared.CombatMode;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Standing;
using Content.Shared.Strip.Components;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Harvest.AndroidNpc;

/// <summary>
/// Nothing can be looted from an android, alive or dead: its gear goes away with its body.
/// </summary>
[TestFixture]
public sealed class AndroidLootTests
{
    private static async Task<(EntityUid Android, EntityUid Item)> SpawnHolding(TestPair pair, TestMapData map)
    {
        var sEnt = pair.Server.EntMan;
        EntityUid android = default, item = default;
        await pair.Server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidCook", map.GridCoords);
            item = sEnt.SpawnEntity("Crowbar", map.GridCoords);
            var hands = sEnt.System<SharedHandsSystem>();
            foreach (var held in hands.EnumerateHeld(android).ToList())
                sEnt.DeleteEntity(held);
            Assert.That(hands.TryPickupAnyHand(android, item), Is.True);
        });
        return (android, item);
    }

    [Test]
    public async Task Android_CannotBeStripped()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var map = await pair.CreateTestMap();
        var (android, _) = await SpawnHolding(pair, map);

        await pair.Server.WaitAssertion(() =>
            Assert.That(pair.Server.EntMan.HasComponent<StrippableComponent>(android), Is.False));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Android_KeepsItsItemWhenShoved()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var map = await pair.CreateTestMap();
        var sEnt = pair.Server.EntMan;
        var (android, item) = await SpawnHolding(pair, map);

        await pair.Server.WaitAssertion(() =>
        {
            var shover = sEnt.SpawnEntity("MobHuman", map.GridCoords);
            var ev = new DisarmedEvent(android, shover, 1f) { DisarmProbability = 1f };
            sEnt.EventBus.RaiseLocalEvent(android, ref ev);
            Assert.That(ev.WasDisarmed, Is.False);
            Assert.That(sEnt.System<SharedHandsSystem>().IsHolding(android, item), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Android_KeepsItsItemWhenKnockedDown()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var map = await pair.CreateTestMap();
        var sEnt = pair.Server.EntMan;
        var (android, item) = await SpawnHolding(pair, map);

        await pair.Server.WaitAssertion(() =>
        {
            var ev = new DropHandItemsEvent();
            sEnt.EventBus.RaiseLocalEvent(android, ref ev);
            Assert.That(sEnt.System<SharedHandsSystem>().IsHolding(android, item), Is.True);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCase(MobState.Critical)]
    [TestCase(MobState.Dead)]
    public async Task Android_DownedKeepsItsItemWhichGoesWithTheBody(MobState state)
    {
        var pair = await AndroidTestPool.GetServerClient();
        var map = await pair.CreateTestMap();
        var sEnt = pair.Server.EntMan;
        var (android, item) = await SpawnHolding(pair, map);

        await pair.Server.WaitAssertion(() => pair.Server.System<MobStateSystem>().ChangeMobState(android, state));
        await pair.Server.WaitRunTicks(5);

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(sEnt.System<SharedHandsSystem>().IsHolding(android, item), Is.True, "Nothing falls on the floor");
            sEnt.DeleteEntity(android);
            Assert.That(sEnt.EntityExists(item), Is.False, "The item goes away with the body");
        });
        await pair.CleanReturnAsync();
    }
}
