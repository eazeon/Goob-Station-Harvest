using Content.Shared._Harvest.Arcade;
using Content.Shared._Harvest.Arcade.RobustOfficer;
using Content.Shared.GameTicking;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._Harvest.Arcade.RobustOfficer;

/// <summary>
/// Robust Officer high scores, separate for each game mode and from the other arcades: one list for the current round
/// and one kept between rounds in the server's data folder.
/// </summary>
public sealed class RobustOfficerLeaderboardSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resources = default!;

    private ArcadeLeaderboards<RobustOfficerMode> _boards = default!;

    public override void Initialize()
    {
        base.Initialize();
        _boards = new ArcadeLeaderboards<RobustOfficerMode>(_resources.UserData, Logger.GetSawmill("robustofficer"), SavePath);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _boards.ClearRound());
    }

    public static ResPath SavePath(RobustOfficerMode mode)
    {
        return new ResPath($"/robustofficer_highscores_{mode.ToString().ToLowerInvariant()}.json");
    }

    public List<ArcadeScoreEntry> Round(RobustOfficerMode mode) => _boards.Round(mode);

    public List<ArcadeScoreEntry> AllTime(RobustOfficerMode mode) => _boards.AllTime(mode);

    /// <summary>
    /// Adds a score to both lists of its mode.
    /// </summary>
    /// <returns>The 1-based placement in each list, null when the score did not make it.</returns>
    public (int? Round, int? AllTime) Register(RobustOfficerMode mode, string name, int score, int level)
    {
        return _boards.Register(mode, name, score, level);
    }
}
