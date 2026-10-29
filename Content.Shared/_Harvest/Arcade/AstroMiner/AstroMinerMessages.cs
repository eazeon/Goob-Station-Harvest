using Robust.Shared.Serialization;

namespace Content.Shared._Harvest.Arcade.AstroMiner;

[Serializable, NetSerializable]
public enum AstroMinerUiKey : byte
{
    Key,
}

/// <summary>
/// What a cell of the field holds. Sent to the client as one byte per cell.
/// </summary>
[Serializable, NetSerializable]
public enum AstroMinerTile : byte
{
    Empty,
    Dirt,
    /// <summary>Destructible rock wall, round enough for boulders to roll off.</summary>
    Wall,
    /// <summary>Indestructible station hull.</summary>
    Hull,
    Boulder,
    Crystal,
    ExitClosed,
    ExitOpen,
    Miner,
    Carp,
    Explosion,
    /// <summary>A falling boulder that lands on it comes out below as a crystal, when the cell below is empty.</summary>
    Foundry,
    /// <summary>Spreads through empty cells and dirt; turns into crystals once boxed in, into boulders once too big.</summary>
    Blob,
}

/// <summary>
/// What happened during a tick, so the client can play sounds.
/// </summary>
[Flags, Serializable, NetSerializable]
public enum AstroMinerEvents : ushort
{
    None = 0,
    CrystalCollected = 1 << 0,
    Explosion = 1 << 1,
    ExitOpened = 1 << 2,
    Escaped = 1 << 3,
    MinerDied = 1 << 4,
    Dug = 1 << 5,
    /// <summary>A falling boulder or crystal hit something and stopped.</summary>
    Landed = 1 << 6,
    /// <summary>A foundry turned a boulder into a crystal, or a boxed in blob turned into crystals.</summary>
    Transformed = 1 << 7,
}

/// <summary>
/// Standard plays the hand-made caves in order; endless generates ever deeper caves. Each has its own leaderboards.
/// </summary>
[Serializable, NetSerializable]
public enum AstroMinerMode : byte
{
    Standard,
    Endless,
}

[Serializable, NetSerializable]
public enum AstroMinerAction : byte
{
    /// <summary>Starts a game in <see cref="AstroMinerMode.Standard"/>.</summary>
    NewGame,
    /// <summary>Starts a game in <see cref="AstroMinerMode.Endless"/>.</summary>
    NewEndlessGame,
    StartUp,
    EndUp,
    StartDown,
    EndDown,
    StartLeft,
    EndLeft,
    StartRight,
    EndRight,
    StartGrab,
    EndGrab,
    Pause,
    Unpause,
    ShowHighscores,
}

[Serializable, NetSerializable]
public enum AstroMinerScreen : byte
{
    Title,
    Game,
    Pause,
    GameOver,
    Highscores,
}

[Serializable, NetSerializable]
public sealed class AstroMinerActionMessage(AstroMinerAction action) : BoundUserInterfaceMessage
{
    public readonly AstroMinerAction Action = action;
}

/// <summary>
/// The whole field and the counters shown above it.
/// </summary>
[Serializable, NetSerializable]
public sealed class AstroMinerStateMessage : BoundUserInterfaceMessage
{
    public int Width;
    public int Height;
    public byte[] Cells = [];

    /// <summary>
    /// Per cell, 1 + the direction (up, right, down, left) its content moved in during the last tick, 0 if it did not move.
    /// The client slides moved objects from their previous cell.
    /// </summary>
    public byte[] Moves = [];

    public Vector2i Miner;

    /// <summary>
    /// Draw the miner with its alternative sprite for this game.
    /// </summary>
    public bool AltMiner;
    public AstroMinerEvents Events;

    /// <summary>
    /// Seconds between two ticks, how long a slide lasts.
    /// </summary>
    public float TickSeconds;
    public int Score;
    public int Lives;
    public int Crystals;
    public int CrystalsNeeded;
    public int Oxygen;
    public int Level;
    public string LevelName = string.Empty;
}

[Serializable, NetSerializable]
public sealed class AstroMinerScreenMessage(AstroMinerScreen screen, bool started) : BoundUserInterfaceMessage
{
    public readonly AstroMinerScreen Screen = screen;
    public readonly bool Started = started;
    public AstroMinerMode Mode;
    public int FinalScore;
    public int? RoundPlacement;
    public int? AllTimePlacement;
}

[Serializable, NetSerializable]
public sealed class AstroMinerUserStatusMessage(bool isPlayer) : BoundUserInterfaceMessage
{
    public readonly bool IsPlayer = isPlayer;
}

[Serializable, NetSerializable]
public sealed class AstroMinerHighscoresMessage(AstroMinerMode mode, List<ArcadeScoreEntry> round, List<ArcadeScoreEntry> allTime) : BoundUserInterfaceMessage
{
    public readonly AstroMinerMode Mode = mode;
    public readonly List<ArcadeScoreEntry> Round = round;
    public readonly List<ArcadeScoreEntry> AllTime = allTime;
}
