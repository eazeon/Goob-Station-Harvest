using Robust.Shared.Prototypes;

namespace Content.Shared._Harvest.Arcade.RobustOfficer;

/// <summary>
/// One room of Robust Officer. Rooms are played in the order of <see cref="Order"/>.
/// </summary>
/// <remarks>
/// Map legend: <c>#</c> wall, <c>.</c> floor, <c>T</c> table, <c>C</c> crate, <c>v</c> vent,
/// <c>D</c> door enemies come through, <c>E</c> exit, <c>P</c> player.
/// </remarks>
[Prototype]
public sealed partial class RobustOfficerSectorPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public int Order;

    /// <summary>
    /// Floor style, one per act.
    /// </summary>
    [DataField]
    public int Theme;

    /// <summary>
    /// Seconds the room has to be held before the enemies stop coming. Unused in a boss room.
    /// </summary>
    [DataField]
    public int Seconds = 60;

    /// <summary>
    /// Fought in this room instead of holding it for <see cref="Seconds"/>.
    /// </summary>
    [DataField]
    public RobustOfficerBoss? Boss;

    /// <summary>
    /// Dispensed every time the boss of this room is beaten, on top of the cabinet's prizes.
    /// </summary>
    [DataField]
    public EntProtoId? Reward;

    [DataField]
    public List<RobustOfficerWave> Waves = new();

    [DataField(required: true)]
    public List<string> Map = new();
}

/// <summary>
/// Enemies sent from <see cref="From"/> seconds into the room until the next wave starts.
/// </summary>
[DataDefinition]
public sealed partial class RobustOfficerWave
{
    [DataField]
    public float From;

    /// <summary>
    /// Seconds between two groups.
    /// </summary>
    [DataField]
    public float Interval = 2f;

    /// <summary>
    /// Enemies per group.
    /// </summary>
    [DataField]
    public int Group = 1;

    /// <summary>
    /// Picked at random for each enemy; repeat one to make it more likely.
    /// </summary>
    [DataField(required: true)]
    public List<RobustOfficerEnemy> Enemies = new();

    public RobustOfficerWave()
    {
    }

    public RobustOfficerWave(float from, float interval, int group, List<RobustOfficerEnemy> enemies)
    {
        From = from;
        Interval = interval;
        Group = group;
        Enemies = enemies;
    }
}
