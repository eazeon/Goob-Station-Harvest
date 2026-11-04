// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.Server.Atmos.Components;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server.Power.Components;
using Content.Shared.Gravity;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Systems;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Harvest.AndroidNpc;

/// <summary>
/// Androids never slip: what lies on the floor must not stop them in a doorway, even dragging someone.
/// </summary>
[TestFixture]
public sealed class AndroidObstacleTests
{
    [Test]
    [TestCase("TrashBananaPeel", false)]
    [TestCase("TrashBananaPeel", true)]
    [TestCase("Soap", false)]
    [TestCase("Soap", true)]
    [TestCase("DrinkGlass", true)]
    public async Task Android_CrossesADoorWithAnItemInFrontOfIt(string item, bool dragging)
    {
        var pair = await AndroidTestPool.GetServerClient();
        var map = await pair.CreateTestMap();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var grid = map.Grid.Owner;
        EntityUid android = default;

        // Two rooms joined by a door.
        await server.WaitAssertion(() =>
        {
            AndroidNpcLogicTests.EnsureSafeAtmosphere(server, map);
            var tile = new Tile(map.Tile.Tile.TypeId);
            sEnt.System<SharedMapSystem>().SetTiles(map.Grid,
                Enumerable.Range(-1, 15).SelectMany(x => Enumerable.Range(-1, 7).Select(y => (new Vector2i(x, y), tile))).ToList());
            var gravity = sEnt.EnsureComponent<GravityComponent>(grid);
            gravity.Enabled = true;
            gravity.Inherent = true;
            sEnt.RemoveComponent<GridAtmosphereComponent>(grid);

            for (var x = -1; x <= 13; x++)
            {
                sEnt.SpawnEntity("WallSolid", new EntityCoordinates(grid, x + 0.5f, 5.5f));
                sEnt.SpawnEntity("WallSolid", new EntityCoordinates(grid, x + 0.5f, -0.5f));
            }

            for (var y = 0; y < 5; y++)
            {
                sEnt.SpawnEntity("WallSolid", new EntityCoordinates(grid, -0.5f, y + 0.5f));
                sEnt.SpawnEntity("WallSolid", new EntityCoordinates(grid, 13.5f, y + 0.5f));
                if (y != 2)
                    sEnt.SpawnEntity("WallSolid", new EntityCoordinates(grid, 6.5f, y + 0.5f));
            }

            var door = sEnt.SpawnEntity("AirlockKitchenLocked", new EntityCoordinates(grid, 6.5f, 2.5f));
            sEnt.GetComponent<ApcPowerReceiverComponent>(door).NeedsPower = false;
            sEnt.SpawnEntity(item, new EntityCoordinates(grid, 5.5f, 2.5f));
        });
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidCook", new EntityCoordinates(grid, 1.5f, 0.5f));
            if (dragging)
            {
                var body = sEnt.SpawnEntity("MobHuman", new EntityCoordinates(grid, 1.5f, 1.3f));
                server.System<MobStateSystem>().ChangeMobState(body, MobState.Dead);
                Assert.That(server.System<PullingSystem>().TryStartPull(android, body), Is.True);
            }

            sEnt.System<AndroidOrderSystem>().IssueOrder(android, android, AndroidOrderType.CleanArea, null,
                new EntityCoordinates(grid, 12.5f, 4.5f));
        });

        var position = Vector2.Zero;
        for (var ticks = 0; ticks < 400 && position.X < 11f; ticks += 5)
        {
            await server.WaitRunTicks(5);
            await server.WaitAssertion(() => position = sEnt.GetComponent<TransformComponent>(android).LocalPosition);
        }

        Assert.That(position.X, Is.GreaterThanOrEqualTo(11f), $"Stopped at {position}");
        await pair.CleanReturnAsync();
    }
}
