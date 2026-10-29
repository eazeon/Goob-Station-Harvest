using Robust.Shared.Serialization;

namespace Content.Shared._Harvest.Arcade;

/// <summary>
/// One line of a Harvest arcade leaderboard, also the format of the saved leaderboard files.
/// </summary>
[Serializable, NetSerializable]
public sealed class ArcadeScoreEntry
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }

    /// <summary>
    /// How far the player got: the cave, depth or room reached.
    /// </summary>
    public int Level { get; set; }
}
