using System.Collections.Generic;
using System.Linq;
using Content.Client._Harvest.Arcade.RobustOfficer;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Content.Server._Harvest.Arcade.RobustOfficer;
using Content.Shared._Harvest.Arcade;
using Content.Shared._Harvest.Arcade.RobustOfficer;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Harvest.Arcade;

[TestFixture]
[TestOf(typeof(RobustOfficerArcadeSystem))]
public sealed class RobustOfficerTest
{
    /// <summary>
    /// Every room of the campaign can be played, and survives random play.
    /// </summary>
    [Test]
    public async Task SectorsAreValid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var sectors = protoMan.EnumeratePrototypes<RobustOfficerSectorPrototype>().OrderBy(s => s.Order).ToList();
            Assert.That(sectors, Has.Count.EqualTo(20));
            Assert.That(sectors.Select(s => s.Order).Distinct().Count(), Is.EqualTo(sectors.Count), "Two rooms share an order.");
            Assert.That(sectors.Where(s => s.Boss != null).Select(s => s.Boss!.Value), Is.EquivalentTo(Enum.GetValues<RobustOfficerBoss>()),
                "Each boss must have its room.");

            var random = new System.Random(1);
            foreach (var sector in sectors)
            {
                var error = RobustOfficerGrid.Validate(sector.Map, sector.Boss != null);
                Assert.That(error, Is.Null, $"{sector.ID}: {error}");
                Assert.That(sector.Theme, Is.InRange(0, RobustOfficerField.Themes - 1), $"{sector.ID}: unknown theme.");
                Assert.That(sector.Boss != null || sector.Waves.Count > 0, $"{sector.ID}: no enemies.");
                if (sector.Reward is { } reward)
                    Assert.That(protoMan.HasIndex(reward), $"{sector.ID}: unknown reward {reward}.");

                var run = new RobustOfficerRun(99);
                var sim = new RobustOfficerSimulation(new RobustOfficerRoom(sector.Map, sector.Seconds, sector.Boss, sector.Waves, sector.Theme), run, 5);
                for (var i = 0; i < 1200; i++)
                {
                    if (i % 10 == 0)
                    {
                        sim.Move = new Vector2i(random.Next(3) - 1, random.Next(3) - 1);
                        sim.Shoot = new Vector2i(random.Next(3) - 1, random.Next(3) - 1);
                    }

                    sim.Tick();
                    if (sim.Dead)
                        sim.Respawn();
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
        var leaderboard = server.System<RobustOfficerLeaderboardSystem>();
        var resources = server.ResolveDependency<IResourceManager>();

        await server.WaitAssertion(() =>
        {
            var (round, allTime) = leaderboard.Register(RobustOfficerMode.Standard, "Test Officer", 1_000_000, 20);
            Assert.That(round, Is.EqualTo(1));
            Assert.That(allTime, Is.EqualTo(1));

            leaderboard.Register(RobustOfficerMode.Endless, "Endless Officer", 2_000_000, 12);
            Assert.That(leaderboard.AllTime(RobustOfficerMode.Standard).Select(e => e.Name), Does.Not.Contain("Endless Officer"));

            foreach (var (mode, name) in new[] { (RobustOfficerMode.Standard, "Test Officer"), (RobustOfficerMode.Endless, "Endless Officer") })
            {
                var path = RobustOfficerLeaderboardSystem.SavePath(mode);
                Assert.That(resources.UserData.Exists(path), $"{path} was not written");
                using var reader = resources.UserData.OpenText(path);
                Assert.That(reader.ReadToEnd(), Does.Contain(name));
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The cabinet spawns with its prizes, and the client window finds every sprite and every line of text.
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
            var arcade = entMan.SpawnAtPosition("RobustOfficerArcadeFilled", map.GridCoords);
            var comp = entMan.GetComponent<RobustOfficerArcadeComponent>(arcade);
            Assert.That(comp.RewardAmount, Is.InRange(5, 8));
            Assert.That(comp.PossibleRewards, Is.Not.Empty);
        });

        await client.WaitAssertion(() =>
        {
            var loc = client.ResolveDependency<ILocalizationManager>();
            var keys = Enum.GetValues<RobustOfficerRank>().Select(r => "robust-officer-rank-" + Id(r))
                .Concat(Enum.GetValues<RobustOfficerPickup>().Select(p => "robust-officer-pickup-" + Id(p)))
                .Concat(Enum.GetValues<RobustOfficerUpgrade>().SelectMany(u => new[] { "robust-officer-upgrade-" + Id(u), $"robust-officer-upgrade-{Id(u)}-desc" }))
                .Concat(Enum.GetValues<RobustOfficerBoss>().Select(b => "robust-officer-boss-" + b.ToString().ToLowerInvariant()));
            foreach (var key in keys)
            {
                Assert.That(loc.HasString(key), $"Missing {key}");
            }

            var menu = new RobustOfficerMenu();
            var kinds = Enum.GetValues<RobustOfficerSpriteKind>();
            menu.UpdateState(new RobustOfficerStateMessage
            {
                Width = 8,
                Height = 1,
                Tiles = Enum.GetValues<RobustOfficerTile>().Select(t => (byte) t).ToArray(),
                Sprites = kinds.Select((k, i) => new RobustOfficerSprite { Id = (ushort) (i + 1), Kind = k, X = (short) (i * 4), Y = 8 }).ToArray(),
                Stocked = RobustOfficerPickup.Flashbang,
                Effects = [RobustOfficerPickup.RapidFire],
                EffectSeconds = [5],
                Boss = RobustOfficerBoss.XenoQueen,
                TimeLeft = -1,
                NukeTimer = -1,
                Level = 1,
                LevelName = "Test",
            });
            menu.UpdateShop(new RobustOfficerShopMessage
            {
                Credits = 30,
                Offers = new RobustOfficerRun(3).Offers(),
            });
            menu.SetScreen(RobustOfficerScreen.Shop, true);
            menu.SetScreen(RobustOfficerScreen.GameOver, true, RobustOfficerMode.Endless, 100, 1, 2);
            menu.UpdateHighscores(RobustOfficerMode.Endless, [new ArcadeScoreEntry { Name = "A", Score = 1, Level = 3 }], []);
            menu.SetScreen(RobustOfficerScreen.Highscores, true, RobustOfficerMode.Endless);
            menu.SetScreen(RobustOfficerScreen.GameOver, true, RobustOfficerMode.Standard, 100, null, null, RobustOfficerEnding.Victory);
            menu.Orphan();
        });

        // Opened for real, no shop button sits on another one.
        RobustOfficerMenu shopMenu = default!;
        await client.WaitPost(() =>
        {
            shopMenu = new RobustOfficerMenu();
            shopMenu.SetUsability(true);
            shopMenu.UpdateShop(new RobustOfficerShopMessage { Credits = 30, Offers = new RobustOfficerRun(3).Offers() });
            shopMenu.SetScreen(RobustOfficerScreen.Shop, true);
            shopMenu.OpenCentered();
        });
        await client.WaitRunTicks(10);
        await client.WaitAssertion(() =>
        {
            var buttons = Descendants(shopMenu).OfType<Button>().Where(b => b.VisibleInTree && b.Size.Y > 0).ToList();
            Assert.That(buttons, Has.Count.GreaterThan(Enum.GetValues<RobustOfficerUpgrade>().Length));
            for (var i = 0; i < buttons.Count; i++)
            {
                for (var j = i + 1; j < buttons.Count; j++)
                {
                    var a = UIBox2.FromDimensions(buttons[i].GlobalPosition, buttons[i].Size);
                    var b = UIBox2.FromDimensions(buttons[j].GlobalPosition, buttons[j].Size);
                    var overlapX = MathF.Min(a.Right, b.Right) - MathF.Max(a.Left, b.Left);
                    var overlapY = MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Top, b.Top);
                    Assert.That(overlapX > 1 && overlapY > 1, Is.False, $"'{buttons[i].Text}' overlaps '{buttons[j].Text}'");
                }
            }

            shopMenu.Close();
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A game through the cabinet: two rooms, the shop and a promotion, a pause, then a death that ends it.
    /// </summary>
    [Test]
    public async Task GameFlowsThroughTheShop()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        EntityUid arcade = default;
        EntityUid player = default;
        RobustOfficerArcadeComponent comp = default!;

        void Act(RobustOfficerAction action)
        {
            entMan.EventBus.RaiseLocalEvent(arcade, new RobustOfficerActionMessage(action) { Actor = player, UiKey = RobustOfficerUiKey.Key });
        }

        // Clears the room and walks the officer out through the exit in the given direction.
        async Task LeaveRoom(RobustOfficerAction start, RobustOfficerAction end)
        {
            await server.WaitPost(() =>
            {
                comp.Simulation!.TimeLeft = 0;
                comp.Simulation.KillEnemies();
                Act(start);
            });
            var level = comp.LevelIndex;
            for (var i = 0; i < 300 && comp.LevelIndex == level; i++)
            {
                await server.WaitRunTicks(1);
            }

            await server.WaitPost(() => Act(end));
        }

        await server.WaitPost(() =>
        {
            arcade = entMan.SpawnAtPosition("RobustOfficerArcade", map.GridCoords);
            player = entMan.SpawnAtPosition("FoodDonutPlain", map.GridCoords);
            entMan.System<MetaDataSystem>().SetEntityName(player, "Flow Officer");
            comp = entMan.GetComponent<RobustOfficerArcadeComponent>(arcade);
            comp.Player = player;
            Act(RobustOfficerAction.NewGame);
        });

        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(comp.Playing);
            Assert.That(comp.Simulation, Is.Not.Null);
            Assert.That(comp.LevelName, Is.Not.Empty);
            Assert.That(comp.Run!.Lives, Is.EqualTo(comp.StartingLives));
        });

        // The first room leads straight to the second, the second to the shop.
        await LeaveRoom(RobustOfficerAction.StartMoveDown, RobustOfficerAction.EndMoveDown);
        await server.WaitAssertion(() =>
        {
            Assert.That(comp.LevelIndex, Is.EqualTo(1));
            Assert.That(comp.InShop, Is.False);
            Assert.That(comp.Run!.Score, Is.GreaterThanOrEqualTo(comp.RoomBonus));
        });

        await LeaveRoom(RobustOfficerAction.StartMoveRight, RobustOfficerAction.EndMoveRight);
        await server.WaitAssertion(() =>
        {
            Assert.That(comp.LevelIndex, Is.EqualTo(2));
            Assert.That(comp.InShop);
            Assert.That(comp.Playing, Is.False);

            comp.Run!.Credits = 50;
            entMan.EventBus.RaiseLocalEvent(arcade, new RobustOfficerBuyMessage(RobustOfficerUpgrade.Promotion) { Actor = player, UiKey = RobustOfficerUiKey.Key });
            Assert.That(comp.Run.Rank, Is.EqualTo(RobustOfficerRank.Officer));
            Assert.That(comp.Run.Credits, Is.EqualTo(10));

            Act(RobustOfficerAction.LeaveShop);
            Assert.That(comp.InShop, Is.False);
            Assert.That(comp.Playing);
            Assert.That(comp.Simulation!.Armor, Is.EqualTo(1));

            Act(RobustOfficerAction.Pause);
            Assert.That(comp.Playing, Is.False);
            Act(RobustOfficerAction.Unpause);
            Assert.That(comp.Playing);

            // Last life, no armor left: the next hit ends the game.
            comp.Run.Lives = 1;
            comp.Simulation.Armor = 0;
            comp.Simulation.AddEnemy(RobustOfficerEnemy.Zombie, comp.Simulation.PlayerPosition);
        });

        await server.WaitRunTicks(120);
        await server.WaitAssertion(() =>
        {
            Assert.That(comp.GameOver);
            Assert.That(comp.Playing, Is.False);
            Assert.That(server.System<RobustOfficerLeaderboardSystem>().Round(RobustOfficerMode.Standard).Select(e => e.Name), Does.Contain("Flow Officer"));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Clearing the last room of the campaign ends the game with the victory screen and the final prizes;
    /// a game can also be stopped from the pause menu, keeping its score.
    /// </summary>
    [Test]
    public async Task CampaignEndsWithTheFinalPrizes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var entMan = server.EntMan;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var map = await pair.CreateTestMap();
        EntityUid arcade = default;
        EntityUid player = default;
        RobustOfficerArcadeComponent comp = default!;

        void Act(RobustOfficerAction action)
        {
            entMan.EventBus.RaiseLocalEvent(arcade, new RobustOfficerActionMessage(action) { Actor = player, UiKey = RobustOfficerUiKey.Key });
        }

        int Count(EntProtoId id)
        {
            return entMan.AllEntities<MetaDataComponent>().Count(e => e.Comp.EntityPrototype?.ID == id.Id);
        }

        var finalRewards = new List<EntProtoId>();
        var before = new Dictionary<EntProtoId, int>();
        await server.WaitPost(() =>
        {
            arcade = entMan.SpawnAtPosition("RobustOfficerArcade", map.GridCoords);
            player = entMan.SpawnAtPosition("FoodDonutPlain", map.GridCoords);
            entMan.System<MetaDataSystem>().SetEntityName(player, "Victorious Officer");
            comp = entMan.GetComponent<RobustOfficerArcadeComponent>(arcade);
            comp.Player = player;
            finalRewards.AddRange(comp.FinalRewards);
            foreach (var reward in finalRewards)
            {
                before[reward] = Count(reward);
            }

            // Straight to the vault, the last room.
            Act(RobustOfficerAction.NewGame);
            comp.LevelIndex = protoMan.EnumeratePrototypes<RobustOfficerSectorPrototype>().Count() - 1;
            Act(RobustOfficerAction.Pause);
            comp.InShop = true;
            Act(RobustOfficerAction.LeaveShop);
            Assert.That(comp.Simulation!.IsBossRoom);
        });

        // The boss shows up, falls, and the officer walks out.
        await server.WaitRunTicks(60);
        await server.WaitPost(() =>
        {
            Assert.That(comp.Simulation!.BossAlive);
            Assert.That(comp.Simulation.BossHealth()!.Value.MaxHealth,
                Is.EqualTo(RobustOfficerSimulation.BossBaseHealth(RobustOfficerBoss.NukieCommander) * Enum.GetValues<RobustOfficerBoss>().Length));
            comp.Simulation.KillEnemies();
            Act(RobustOfficerAction.StartMoveDown);
        });

        for (var i = 0; i < 300 && !comp.GameOver; i++)
        {
            await server.WaitRunTicks(1);
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(comp.GameOver);
            Assert.That(comp.Ending, Is.EqualTo(RobustOfficerEnding.Victory));
            Assert.That(finalRewards, Is.Not.Empty);
            foreach (var reward in finalRewards)
            {
                Assert.That(Count(reward), Is.EqualTo(before[reward] + 1), $"{reward} was not dispensed.");
            }

            Assert.That(server.System<RobustOfficerLeaderboardSystem>().Round(RobustOfficerMode.Standard).Select(e => e.Name),
                Does.Contain("Victorious Officer"));

            // A new game stopped from the pause menu keeps its score.
            Act(RobustOfficerAction.NewEndlessGame);
            Assert.That(comp.Playing);
            comp.Run!.Score = 1234;
            Act(RobustOfficerAction.Pause);
            Act(RobustOfficerAction.Quit);
            Assert.That(comp.GameOver);
            Assert.That(comp.Ending, Is.EqualTo(RobustOfficerEnding.Quit));
            Assert.That(server.System<RobustOfficerLeaderboardSystem>().Round(RobustOfficerMode.Endless).Select(e => e.Score), Does.Contain(1234));
        });

        await pair.CleanReturnAsync();
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (var child in control.Children)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static string Id(Enum value)
    {
        return RobustOfficerField.StateName(value.ToString()).Replace('_', '-');
    }
}
