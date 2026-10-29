using Content.Shared._Harvest.Arcade;
using Content.Shared._Harvest.Arcade.AstroMiner;
using Content.Shared.GameTicking;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._Harvest.Arcade.AstroMiner;

/// <summary>
/// AstroMiner high scores, separate for each game mode: one list for the current round
/// and one kept between rounds in the server's data folder.
/// </summary>
public sealed class AstroMinerLeaderboardSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resources = default!;

    private ArcadeLeaderboards<AstroMinerMode> _boards = default!;

    public override void Initialize()
    {
        base.Initialize();
        _boards = new ArcadeLeaderboards<AstroMinerMode>(_resources.UserData, Logger.GetSawmill("astrominer"), SavePath);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _boards.ClearRound());
    }

    /// <summary>
    /// The standard mode keeps the file it had before the endless mode existed.
    /// </summary>
    public static ResPath SavePath(AstroMinerMode mode)
    {
        return mode == AstroMinerMode.Standard
            ? new ResPath("/astrominer_highscores.json")
            : new ResPath($"/astrominer_highscores_{mode.ToString().ToLowerInvariant()}.json");
    }

    public List<ArcadeScoreEntry> Round(AstroMinerMode mode) => _boards.Round(mode);

    public List<ArcadeScoreEntry> AllTime(AstroMinerMode mode) => _boards.AllTime(mode);

    /// <summary>
    /// Adds a score to both lists of its mode.
    /// </summary>
    /// <returns>The 1-based placement in each list, null when the score did not make it.</returns>
    public (int? Round, int? AllTime) Register(AstroMinerMode mode, string name, int score, int level)
    {
        return _boards.Register(mode, name, score, level);
    }
}
