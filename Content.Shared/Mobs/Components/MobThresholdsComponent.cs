// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Alert;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Mobs.Components;

[RegisterComponent, NetworkedComponent]
[Access(typeof(MobThresholdSystem), Other = AccessPermissions.ReadWriteExecute)]
public sealed partial class MobThresholdsComponent : Component
{
    [DataField("thresholds", required: true)]
    public SortedDictionary<FixedPoint2, MobState> Thresholds = new();

    /// <summary>
    /// VV proxy for the Critical threshold.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public FixedPoint2 CritThreshold
    {
        get => GetThreshold(MobState.Critical);
        set => SetThreshold(MobState.Critical, value);
    }

    /// <summary>
    /// VV proxy for the Dead (max health) threshold.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public FixedPoint2 DeadThreshold
    {
        get => GetThreshold(MobState.Dead);
        set => SetThreshold(MobState.Dead, value);
    }

    private FixedPoint2 GetThreshold(MobState state)
    {
        foreach (var (threshold, mobState) in Thresholds)
        {
            if (mobState == state)
                return threshold;
        }
        return FixedPoint2.Zero;
    }

    private void SetThreshold(MobState state, FixedPoint2 value)
    {
        var toRemove = new List<FixedPoint2>();
        foreach (var (threshold, mobState) in Thresholds)
        {
            if (mobState == state)
                toRemove.Add(threshold);
        }
        foreach (var key in toRemove)
            Thresholds.Remove(key);
        Thresholds[value] = state;
    }

    [DataField("triggersAlerts")]
    public bool TriggersAlerts = true;

    [DataField("currentThresholdState")]
    public MobState CurrentThresholdState;

    /// <summary>
    /// The health alert that should be displayed for player controlled entities.
    /// Used for alternate health alerts (silicons, for example)
    /// </summary>
    [DataField("stateAlertDict")]
    public Dictionary<MobState, ProtoId<AlertPrototype>> StateAlertDict = new()
    {
        {MobState.Alive, "HumanHealth"},
        {MobState.Critical, "HumanCrit"},
        {MobState.Dead, "HumanDead"},
    };

    [DataField]
    public ProtoId<AlertCategoryPrototype> HealthAlertCategory = "Health";

    /// <summary>
    /// Whether or not this entity should display damage overlays (robots don't feel pain, black out etc.)
    /// </summary>
    [DataField("showOverlays")]
    public bool ShowOverlays = true;

    /// <summary>
    /// Whether or not this entity can be revived out of a dead state.
    /// </summary>
    [DataField("allowRevives")]
    public bool AllowRevives;
}

[Serializable, NetSerializable]
public sealed class MobThresholdsComponentState : ComponentState
{
    public Dictionary<FixedPoint2, MobState> UnsortedThresholds;

    public bool TriggersAlerts;

    public MobState CurrentThresholdState;

    public Dictionary<MobState, ProtoId<AlertPrototype>> StateAlertDict;

    public bool ShowOverlays;

    public bool AllowRevives;

    public MobThresholdsComponentState(Dictionary<FixedPoint2, MobState> unsortedThresholds,
        bool triggersAlerts,
        MobState currentThresholdState,
        Dictionary<MobState,
        ProtoId<AlertPrototype>> stateAlertDict,
        bool showOverlays,
        bool allowRevives)
    {
        UnsortedThresholds = unsortedThresholds;
        TriggersAlerts = triggersAlerts;
        CurrentThresholdState = currentThresholdState;
        StateAlertDict = stateAlertDict;
        ShowOverlays = showOverlays;
        AllowRevives = allowRevives;
    }
}
