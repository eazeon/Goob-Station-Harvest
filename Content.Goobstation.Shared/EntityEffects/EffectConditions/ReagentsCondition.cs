// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.FixedPoint;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.EntityConditions;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Shared.EntityEffects.EffectConditions;

public sealed partial class ReagentsCondition : EntityConditionBase<ReagentsCondition>
{
    [DataField]
    public float Min = 0f;

    public override string EntityConditionGuidebookText(IPrototypeManager prototype)
    {
        return Loc.GetString("reagent-effect-condition-guidebook-reagents", ("min", Min));
    }
}

public sealed partial class ReagentsConditionSystem : EntityConditionSystem<SolutionContainerManagerComponent, ReagentsCondition>
{
    [Dependency] private SharedSolutionContainerSystem _solution = default!;

    protected override void Condition(Entity<SolutionContainerManagerComponent> entity, ref EntityConditionEvent<ReagentsCondition> args)
    {
        var totalVolume = FixedPoint2.Zero;

        foreach (var (_, solution) in _solution.EnumerateSolutions(entity.AsNullable()))
        {
            totalVolume += solution.Comp.Solution.Volume;
        }

        args.Result = totalVolume >= args.Condition.Min;
    }
}
