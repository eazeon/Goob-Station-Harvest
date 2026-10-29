using System.Linq;
using Content.Client._Harvest.Arcade.AstroMiner;
using Content.Server._Harvest.Arcade.AstroMiner;
using Content.Shared._Harvest.Arcade;
using Content.Shared._Harvest.Arcade.AstroMiner;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Harvest.Arcade;

[TestFixture]
[TestOf(typeof(AstroMinerArcadeSystem))]
public sealed class AstroMinerTest
{
    private static readonly Vector2i[] Moves = [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)];

    /// <summary>
    /// Every cave is a closed rectangle with one miner, one exit and enough crystals, and survives random play.
    /// </summary>
    [Test]
    public async Task LevelsAreValid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var levels = protoMan.EnumeratePrototypes<AstroMinerLevelPrototype>().ToList();
            Assert.That(levels, Is.Not.Empty);
            Assert.That(levels.Select(l => l.Order).Distinct().Count(), Is.EqualTo(levels.Count), "Two caves share an order.");

            var random = new System.Random(1);
            foreach (var level in levels)
            {
                var width = level.Map[0].Length;
                Assert.That(level.Map.All(row => row.Length == width), $"{level.ID}: rows have different lengths.");
                Assert.That(level.Map[0].All(c => c == '#') && level.Map[^1].All(c => c == '#')
                            && level.Map.All(row => row[0] == '#' && row[^1] == '#'), $"{level.ID}: the hull must enclose the cave.");

                var text = string.Concat(level.Map);
                Assert.That(text.Count(c => c == 'P'), Is.EqualTo(1), $"{level.ID}: needs one miner.");
                Assert.That(text.Count(c => c == 'E'), Is.GreaterThanOrEqualTo(1), $"{level.ID}: needs an exit.");
                Assert.That(text.Count(c => c == '*'), Is.GreaterThanOrEqualTo(level.CrystalsNeeded), $"{level.ID}: not enough crystals.");

                var reach = AstroMinerReachability.Analyze(level.Map);
                Assert.That(reach.ExitReachable, $"{level.ID}: the exit cannot be reached.");
                Assert.That(reach.Crystals, Is.GreaterThanOrEqualTo(level.CrystalsNeeded), $"{level.ID}: only {reach.Crystals} crystals can be reached.");

                var sim = new AstroMinerSimulation(level.Map, level.Oxygen, level.CrystalsNeeded, level.CrystalValue, level.BonusCrystalValue);
                for (var i = 0; i < 600; i++)
                {
                    sim.Input = random.Next(5) == 0 ? null : Moves[random.Next(Moves.Length)];
                    sim.Tick();
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LeaderboardIsSaved()
    {
        // The scores registered here stay in the server's leaderboard.
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var leaderboard = server.System<AstroMinerLeaderboardSystem>();
        var resources = server.ResolveDependency<IResourceManager>();

        await server.WaitAssertion(() =>
        {
            var (round, allTime) = leaderboard.Register(AstroMinerMode.Standard, "Test Miner", 1_000_000, 7);
            Assert.That(round, Is.EqualTo(1));
            Assert.That(allTime, Is.EqualTo(1));
            Assert.That(leaderboard.Register(AstroMinerMode.Standard, "Nobody", 0, 1), Is.EqualTo(((int?) null, (int?) null)));

            // Each mode has its own boards and file.
            leaderboard.Register(AstroMinerMode.Endless, "Deep Miner", 2_000_000, 12);
            Assert.That(leaderboard.AllTime(AstroMinerMode.Standard).Select(e => e.Name), Does.Not.Contain("Deep Miner"));
            Assert.That(leaderboard.AllTime(AstroMinerMode.Endless).Select(e => e.Name), Does.Not.Contain("Test Miner"));

            foreach (var (mode, name) in new[] { (AstroMinerMode.Standard, "Test Miner"), (AstroMinerMode.Endless, "Deep Miner") })
            {
                var path = AstroMinerLeaderboardSystem.SavePath(mode);
                Assert.That(resources.UserData.Exists(path), $"{path} was not written");
                using var reader = resources.UserData.OpenText(path);
                Assert.That(reader.ReadToEnd(), Does.Contain(name));
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The random arcade spawner gives the cabinet its own rotation, so mapped cabinets keep facing the right way.
    /// </summary>
    [Test]
    public async Task RandomArcadeKeepsRotation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var mapSystem = server.System<SharedMapSystem>();
        var arcades = new[] { "BlockGameArcade", "SpaceVillainArcadeFilled", "AstroMinerArcadeFilled", "RobustOfficerArcadeFilled" };

        await server.WaitAssertion(() =>
        {
            var map = mapSystem.CreateMap(out var mapId, runMapInit: false);
            var spawner = entMan.SpawnEntity("RandomArcade", new MapCoordinates(0.5f, 0.5f, mapId));
            entMan.System<SharedTransformSystem>().SetLocalRotation(spawner, Angle.FromDegrees(90));
            mapSystem.InitializeMap(map);

            var spawned = entMan.AllEntities<TransformComponent>()
                .Where(e => e.Comp.MapID == mapId && arcades.Contains(entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID))
                .ToList();
            Assert.That(spawned, Has.Count.EqualTo(1));
            Assert.That(spawned[0].Comp.LocalRotation.Degrees, Is.EqualTo(90).Within(0.01));

            entMan.DeleteEntity(map);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The cabinet spawns with its prizes and the client window finds every sprite.
    /// </summary>
    [Test]
    public async Task ArcadeSpawnsAndMenuDraws()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var arcade = entMan.SpawnAtPosition("AstroMinerArcadeFilled", map.GridCoords);
            var comp = entMan.GetComponent<AstroMinerArcadeComponent>(arcade);
            Assert.That(comp.RewardAmount, Is.InRange(5, 8));
            Assert.That(comp.PossibleRewards, Is.Not.Empty);
        });

        await client.WaitAssertion(() =>
        {
            var menu = new AstroMinerMenu();
            menu.UpdateState(new AstroMinerStateMessage
            {
                Width = 2,
                Height = 1,
                Cells = [(byte) AstroMinerTile.Miner, (byte) AstroMinerTile.Crystal],
                Level = 1,
                LevelName = "Test",
            });
            menu.SetScreen(AstroMinerScreen.GameOver, true, AstroMinerMode.Endless, 100, 1, 2);
            menu.UpdateHighscores(AstroMinerMode.Endless, [new ArcadeScoreEntry { Name = "A", Score = 1, Level = 3 }], []);
            menu.SetScreen(AstroMinerScreen.Highscores, true, AstroMinerMode.Endless);
            menu.Orphan();
        });

        await pair.CleanReturnAsync();
    }
}
