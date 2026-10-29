using Robust.Shared.Serialization;

namespace Content.Shared._Harvest.Arcade.RobustOfficer;

[Serializable, NetSerializable]
public enum RobustOfficerUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum RobustOfficerTile : byte
{
    Floor,
    Wall,
    /// <summary>
    /// Blocks walking but not bullets.
    /// </summary>
    Table,
    Crate,
    /// <summary>
    /// Floor that enemies can crawl out of.
    /// </summary>
    Vent,
    /// <summary>
    /// Airlock on the edge of the room that enemies come through.
    /// </summary>
    Door,
    ExitClosed,
    ExitOpen,
}

[Serializable, NetSerializable]
public enum RobustOfficerEnemy : byte
{
    Greytider,
    Clown,
    Zombie,
    Slime,
    SlimeSmall,
    Xeno,
    Revenant,
    Carp,
    Nukie,
}

[Serializable, NetSerializable]
public enum RobustOfficerBoss : byte
{
    AssistantKing,
    PatientZero,
    XenoQueen,
    SpaceDragon,
    NukieCommander,
}

[Serializable, NetSerializable]
public enum RobustOfficerRank : byte
{
    Cadet,
    Officer,
    Warden,
    HeadOfSecurity,
}

[Serializable, NetSerializable]
public enum RobustOfficerPickup : byte
{
    Credit,
    CreditBundle,
    Stimulants,
    RapidFire,
    Buckshot,
    ArmorPlate,
    Donut,
    Flashbang,
    Lube,
    HoloBarrier,
    SecBorg,
}

/// <summary>
/// What the requisitions vendor sells between rooms.
/// </summary>
[Serializable, NetSerializable]
public enum RobustOfficerUpgrade : byte
{
    Promotion,
    FireRate,
    Damage,
    Boots,
    Donut,
    Flashbang,
}

/// <summary>
/// Everything drawn on top of the tiles. The sprite state is the name in snake case.
/// </summary>
[Serializable, NetSerializable]
public enum RobustOfficerSpriteKind : byte
{
    PlayerCadet,
    PlayerOfficer,
    PlayerWarden,
    PlayerHeadOfSecurity,

    Greytider,
    Clown,
    Zombie,
    Slime,
    SlimeSmall,
    Xeno,
    Revenant,
    Carp,
    Nukie,

    AssistantKing,
    PatientZero,
    XenoQueen,
    SpaceDragon,
    NukieCommander,

    BoltDisabler,
    Bullet,
    Pellet,
    Laser,
    EnemyBullet,
    Toolbox,
    Acid,

    PickupCredit,
    PickupCreditBundle,
    PickupStimulants,
    PickupRapidFire,
    PickupBuckshot,
    PickupArmorPlate,
    PickupDonut,
    PickupFlashbang,
    PickupLube,
    PickupHoloBarrier,
    PickupSecBorg,

    Turret,
    SecBorg,
    Puddle,
    LubePuddle,
    Barrier,
    Rift,
    Poof,
}

[Flags, Serializable, NetSerializable]
public enum RobustOfficerSpriteFlags : byte
{
    None = 0,
    /// <summary>
    /// Just took a hit.
    /// </summary>
    Hurt = 1 << 0,
    /// <summary>
    /// Invulnerable player or pickup about to vanish.
    /// </summary>
    Blink = 1 << 1,
    Phasing = 1 << 2,
    Stunned = 1 << 3,
    /// <summary>
    /// About to charge or dash.
    /// </summary>
    Telegraph = 1 << 4,
}

[Flags, Serializable, NetSerializable]
public enum RobustOfficerEvents : uint
{
    None = 0,
    Shot = 1 << 0,
    EnemyHit = 1 << 1,
    EnemyKilled = 1 << 2,
    ArmorHit = 1 << 3,
    PlayerDied = 1 << 4,
    Pickup = 1 << 5,
    Credit = 1 << 6,
    ExitOpened = 1 << 7,
    Exited = 1 << 8,
    BossSpawned = 1 << 9,
    BossKilled = 1 << 10,
    ItemUsed = 1 << 11,
    EnemyShot = 1 << 12,
    NukeExploded = 1 << 13,
}

[Serializable, NetSerializable]
public enum RobustOfficerMode : byte
{
    Standard,
    Endless,
}

[Serializable, NetSerializable]
public enum RobustOfficerAction : byte
{
    NewGame,
    NewEndlessGame,
    StartMoveUp,
    EndMoveUp,
    StartMoveDown,
    EndMoveDown,
    StartMoveLeft,
    EndMoveLeft,
    StartMoveRight,
    EndMoveRight,
    StartShootUp,
    EndShootUp,
    StartShootDown,
    EndShootDown,
    StartShootLeft,
    EndShootLeft,
    StartShootRight,
    EndShootRight,
    UseItem,
    Pause,
    Unpause,
    ShowHighscores,
    LeaveShop,

    /// <summary>
    /// Ends the game now, keeping its score.
    /// </summary>
    Quit,
}

/// <summary>
/// How a game ended.
/// </summary>
[Serializable, NetSerializable]
public enum RobustOfficerEnding : byte
{
    Defeat,
    Quit,

    /// <summary>
    /// The last room of the campaign was cleared.
    /// </summary>
    Victory,
}

[Serializable, NetSerializable]
public enum RobustOfficerScreen : byte
{
    Title,
    Game,
    Pause,
    Shop,
    GameOver,
    Highscores,
}

[Serializable, NetSerializable]
public sealed class RobustOfficerActionMessage(RobustOfficerAction action) : BoundUserInterfaceMessage
{
    public readonly RobustOfficerAction Action = action;
}

[Serializable, NetSerializable]
public sealed class RobustOfficerBuyMessage(RobustOfficerUpgrade upgrade) : BoundUserInterfaceMessage
{
    public readonly RobustOfficerUpgrade Upgrade = upgrade;
}

/// <summary>
/// One thing to draw. Positions are in sixteenths of a tile, from the top left corner of the room.
/// </summary>
[Serializable, NetSerializable]
public struct RobustOfficerSprite
{
    /// <summary>
    /// Stays the same while the object lives, so the client can slide it from its previous position.
    /// </summary>
    public ushort Id;
    public RobustOfficerSpriteKind Kind;
    public short X;
    public short Y;

    /// <summary>
    /// 0 south, 1 north, 2 east, 3 west; only used by directional sprites.
    /// </summary>
    public byte Direction;
    public RobustOfficerSpriteFlags Flags;
}

[Serializable, NetSerializable]
public sealed class RobustOfficerStateMessage : BoundUserInterfaceMessage
{
    public int Width;
    public int Height;

    /// <summary>
    /// Empty when the tiles did not change since the last state.
    /// </summary>
    public byte[] Tiles = [];

    /// <summary>
    /// Floor style of the room, one per act.
    /// </summary>
    public byte Theme;
    public RobustOfficerSprite[] Sprites = [];
    public RobustOfficerEvents Events;

    /// <summary>
    /// Seconds between two ticks, how long a slide lasts.
    /// </summary>
    public float TickSeconds;

    public int Score;
    public int Lives;
    public int Credits;
    public int Armor;
    public int MaxArmor;
    public RobustOfficerRank Rank;
    public RobustOfficerPickup? Stocked;

    /// <summary>
    /// Seconds before the enemies stop coming, -1 in a boss room.
    /// </summary>
    public int TimeLeft;

    /// <summary>
    /// Seconds before the nuke goes off, -1 without one.
    /// </summary>
    public int NukeTimer;
    public int BossHealth;
    public int BossMaxHealth;
    public RobustOfficerBoss? Boss;

    /// <summary>
    /// Timed power-ups running, their seconds left in <see cref="EffectSeconds"/>.
    /// </summary>
    public RobustOfficerPickup[] Effects = [];
    public int[] EffectSeconds = [];
    public bool ExitOpen;
    public int Level;
    public string LevelName = string.Empty;
}

[Serializable, NetSerializable]
public sealed class RobustOfficerScreenMessage(RobustOfficerScreen screen, bool started) : BoundUserInterfaceMessage
{
    public readonly RobustOfficerScreen Screen = screen;
    public readonly bool Started = started;
    public RobustOfficerMode Mode;
    public RobustOfficerEnding Ending;
    public int FinalScore;
    public int? RoundPlacement;
    public int? AllTimePlacement;
}

[Serializable, NetSerializable]
public sealed class RobustOfficerShopOffer
{
    public RobustOfficerUpgrade Upgrade;

    /// <summary>
    /// Levels already bought, or the rank reached for a promotion.
    /// </summary>
    public int Level;
    public int MaxLevel;

    /// <summary>
    /// Null when it cannot be bought anymore.
    /// </summary>
    public int? Price;
}

[Serializable, NetSerializable]
public sealed class RobustOfficerShopMessage : BoundUserInterfaceMessage
{
    public int Credits;
    public int Lives;
    public RobustOfficerRank Rank;
    public RobustOfficerPickup? Stocked;
    public List<RobustOfficerShopOffer> Offers = new();
}

[Serializable, NetSerializable]
public sealed class RobustOfficerUserStatusMessage(bool isPlayer) : BoundUserInterfaceMessage
{
    public readonly bool IsPlayer = isPlayer;
}

[Serializable, NetSerializable]
public sealed class RobustOfficerHighscoresMessage(RobustOfficerMode mode, List<ArcadeScoreEntry> round, List<ArcadeScoreEntry> allTime) : BoundUserInterfaceMessage
{
    public readonly RobustOfficerMode Mode = mode;
    public readonly List<ArcadeScoreEntry> Round = round;
    public readonly List<ArcadeScoreEntry> AllTime = allTime;
}
