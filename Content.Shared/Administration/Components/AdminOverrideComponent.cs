using System.Numerics;
using Content.Shared.FixedPoint;
using Robust.Shared.GameStates;

namespace Content.Shared.Administration.Components;

/// <summary>
/// Consolidated admin override component for rapid in-game modifications.
/// Add this to an entity to expose editable fields for sprite, scale,
/// health thresholds, and dismemberability via View Variables.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(SharedAdminOverrideSystem), Other = AccessPermissions.ReadWriteExecute)]
public sealed partial class AdminOverrideComponent : Component
{
    /// <summary>
    /// Path to the RSI file (e.g. "Mobs/Animals/mouse.rsi"). Empty = no override.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public string RSIPath = string.Empty;

    /// <summary>
    /// State to use within the RSI. Empty = no override.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public string RSIState = string.Empty;

    /// <summary>
    /// Entity scale override applied via ScaleVisuals. Vector2.Zero = no override.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public Vector2 EntityScale = Vector2.Zero;

    /// <summary>
    /// Overrides the Critical damage threshold. Negative = no override.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public FixedPoint2 CritThreshold = FixedPoint2.New(-1);

    /// <summary>
    /// Overrides the Dead damage threshold. Negative = no override.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public FixedPoint2 DeadThreshold = FixedPoint2.New(-1);

    /// <summary>
    /// Whether this entity can be dismembered (lose limbs).
    /// Defaults to true.
    /// </summary>
    [DataField, AutoNetworkedField, ViewVariables(VVAccess.ReadWrite)]
    public bool Dismemberable = true;
}
