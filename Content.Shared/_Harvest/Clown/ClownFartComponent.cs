using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Harvest.Clown;

/// <summary>
/// A fart from this entity (normal or super) injects a little laughter into everyone around it.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class ClownFartComponent : Component
{
    [DataField]
    public ProtoId<ReagentPrototype> Reagent = "Laughter";

    [DataField]
    public FixedPoint2 Amount = 5;

    [DataField]
    public float Range = 2f;

    [DataField]
    public TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextLaughter;
}
