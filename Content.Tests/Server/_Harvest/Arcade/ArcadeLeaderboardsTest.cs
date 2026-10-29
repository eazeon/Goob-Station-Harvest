using System.Linq;
using Content.Server._Harvest.Arcade;
using Content.Server._Harvest.Arcade.AstroMiner;
using Content.Server._Harvest.Arcade.RobustOfficer;
using Content.Shared._Harvest.Arcade.AstroMiner;
using Content.Shared._Harvest.Arcade.RobustOfficer;
using NUnit.Framework;
using Robust.Shared.ContentPack;
using Robust.Shared.Log;

namespace Content.Tests.Server._Harvest.Arcade;

[TestFixture]
public sealed class ArcadeLeaderboardsTest
{
    private static readonly ISawmill Sawmill = new LogManager().GetSawmill("arcade");

    /// <summary>
    /// AstroMiner still reads the files it wrote before the leaderboards were shared,
    /// and each game keeps its own files.
    /// </summary>
    [Test]
    public void GamesKeepSeparateFiles()
    {
        var data = new VirtualWritableDirProvider();
        var astroPath = AstroMinerLeaderboardSystem.SavePath(AstroMinerMode.Standard);
        using (var writer = data.OpenWriteText(astroPath))
        {
            writer.Write("[{\"Name\":\"Old Miner\",\"Score\":500,\"Level\":3}]");
        }

        var astro = new ArcadeLeaderboards<AstroMinerMode>(data, Sawmill, AstroMinerLeaderboardSystem.SavePath);
        Assert.That(astro.AllTime(AstroMinerMode.Standard).Single().Name, Is.EqualTo("Old Miner"));
        Assert.That(astro.AllTime(AstroMinerMode.Endless), Is.Empty);

        var officer = new ArcadeLeaderboards<RobustOfficerMode>(data, Sawmill, RobustOfficerLeaderboardSystem.SavePath);
        Assert.That(officer.AllTime(RobustOfficerMode.Standard), Is.Empty);
        Assert.That(officer.Register(RobustOfficerMode.Standard, "Officer", 900, 5), Is.EqualTo(((int?) 1, (int?) 1)));
        Assert.That(officer.Register(RobustOfficerMode.Endless, "Nobody", 0, 1), Is.EqualTo(((int?) null, (int?) null)));

        using (var reader = data.OpenText(astroPath))
        {
            Assert.That(reader.ReadToEnd(), Does.Not.Contain("Officer"));
        }

        // Reloading finds the scores again; the round lists start empty.
        var reloaded = new ArcadeLeaderboards<RobustOfficerMode>(data, Sawmill, RobustOfficerLeaderboardSystem.SavePath);
        Assert.That(reloaded.AllTime(RobustOfficerMode.Standard).Single().Score, Is.EqualTo(900));
        Assert.That(reloaded.Round(RobustOfficerMode.Standard), Is.Empty);
        Assert.That(reloaded.AllTime(RobustOfficerMode.Endless), Is.Empty);
    }

    [Test]
    public void OnlyTheBestScoresAreKept()
    {
        var boards = new ArcadeLeaderboards<RobustOfficerMode>(new VirtualWritableDirProvider(), Sawmill, RobustOfficerLeaderboardSystem.SavePath);
        for (var i = 1; i <= 15; i++)
        {
            boards.Register(RobustOfficerMode.Standard, $"P{i}", i * 10, 1);
        }

        var scores = boards.AllTime(RobustOfficerMode.Standard).Select(e => e.Score).ToList();
        Assert.That(scores, Has.Count.EqualTo(ArcadeLeaderboards<RobustOfficerMode>.MaxEntries));
        Assert.That(scores, Is.Ordered.Descending);
        Assert.That(scores[0], Is.EqualTo(150));
        Assert.That(boards.Register(RobustOfficerMode.Standard, "Low", 5, 1), Is.EqualTo(((int?) null, (int?) null)));

        boards.ClearRound();
        Assert.That(boards.Round(RobustOfficerMode.Standard), Is.Empty);
        Assert.That(boards.AllTime(RobustOfficerMode.Standard), Is.Not.Empty);
    }
}
