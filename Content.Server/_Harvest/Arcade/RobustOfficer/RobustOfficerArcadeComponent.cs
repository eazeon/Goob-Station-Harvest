using Content.Shared._Harvest.Arcade.RobustOfficer;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.Arcade.RobustOfficer;

[RegisterComponent]
public sealed partial class RobustOfficerArcadeComponent : Component
{
    [ViewVariables]
    public EntityUid? Player;

    [ViewVariables]
    public readonly List<EntityUid> Spectators = new();

    /// <summary>
    /// The room being played, null before the first game.
    /// </summary>
    [ViewVariables]
    public RobustOfficerSimulation? Simulation;

    /// <summary>
    /// Score, lives, credits, rank and upgrades of the current game.
    /// </summary>
    [ViewVariables]
    public RobustOfficerRun? Run;

    [ViewVariables]
    public bool Started;

    [ViewVariables]
    public bool Playing;

    [ViewVariables]
    public bool GameOver;

    /// <summary>
    /// Between two rooms, at the requisitions vendor.
    /// </summary>
    [ViewVariables]
    public bool InShop;

    [ViewVariables]
    public RobustOfficerMode Mode;

    /// <summary>
    /// Index of the room being played, from 0; the depth minus one in endless mode.
    /// </summary>
    [ViewVariables]
    public int LevelIndex;

    /// <summary>
    /// Rolled on each game; room n is generated from it and n, so a retry replays the same room.
    /// </summary>
    [ViewVariables]
    public int Seed;

    [ViewVariables]
    public string LevelName = string.Empty;

    /// <summary>
    /// Dispensed when the boss of the current room is beaten, see <see cref="RobustOfficerSectorPrototype.Reward"/>.
    /// </summary>
    [ViewVariables]
    public EntProtoId? LevelReward;

    [ViewVariables]
    public float Accumulator;

    /// <summary>
    /// Seconds left before the game goes on after the officer died or left the room.
    /// </summary>
    [ViewVariables]
    public float? TransitionTimer;

    /// <summary>
    /// Held movement keys: up, down, left, right from the lowest bit.
    /// </summary>
    [ViewVariables]
    public byte MoveKeys;

    /// <summary>
    /// Held shooting keys: up, down, left, right from the lowest bit.
    /// </summary>
    [ViewVariables]
    public byte ShootKeys;

    /// <summary>
    /// A shooting key pressed and released between two ticks, so a quick tap still fires.
    /// </summary>
    [ViewVariables]
    public Vector2i? ShootTapped;

    [ViewVariables]
    public bool UseItem;

    /// <summary>
    /// The tiles changed since they were last sent.
    /// </summary>
    [ViewVariables]
    public bool TilesDirty = true;

    [ViewVariables]
    public RobustOfficerEnding Ending;

    [ViewVariables]
    public int? RoundPlacement;

    [ViewVariables]
    public int? AllTimePlacement;

    [DataField]
    public int StartingLives = 3;

    [DataField]
    public float DeathDelay = 2f;

    [DataField]
    public float ExitDelay = 0.6f;

    /// <summary>
    /// Points for each room left alive.
    /// </summary>
    [DataField]
    public int RoomBonus = 100;

    [DataField]
    public List<EntProtoId> PossibleRewards = new();

    /// <summary>
    /// All dispensed when the campaign is won, whatever prizes are left.
    /// </summary>
    [DataField]
    public List<EntProtoId> FinalRewards = new();

    [DataField]
    public int RewardMinAmount;

    [DataField]
    public int RewardMaxAmount;

    /// <summary>
    /// Prizes left in the machine, one is dispensed per boss beaten.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public int RewardAmount;

    [DataField]
    public SoundSpecifier NewGameSound = new SoundPathSpecifier("/Audio/Effects/Arcade/newgame.ogg");

    [DataField]
    public SoundSpecifier ExitOpenSound = new SoundPathSpecifier("/Audio/Effects/Arcade/player_charge.ogg");

    [DataField]
    public SoundSpecifier BossKilledSound = new SoundPathSpecifier("/Audio/Effects/Arcade/win.ogg");

    [DataField]
    public SoundSpecifier DeathSound = new SoundPathSpecifier("/Audio/Effects/Arcade/player_attack.ogg");

    [DataField]
    public SoundSpecifier NukeSound = new SoundPathSpecifier("/Audio/Effects/explosion1.ogg");

    [DataField]
    public SoundSpecifier PromotionSound = new SoundPathSpecifier("/Audio/Effects/Arcade/player_heal.ogg");

    [DataField]
    public SoundSpecifier BuySound = new SoundPathSpecifier("/Audio/Machines/machine_vend.ogg");

    [DataField]
    public SoundSpecifier GameOverSound = new SoundPathSpecifier("/Audio/Effects/Arcade/gameover.ogg");

    [DataField]
    public SoundSpecifier VictorySound = new SoundPathSpecifier("/Audio/Effects/Arcade/win.ogg");
}
