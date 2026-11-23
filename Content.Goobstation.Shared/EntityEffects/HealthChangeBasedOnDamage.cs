// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Shared.EntityEffects;

[UsedImplicitly]
public sealed partial class HealthChangeBasedOnDamage : EntityEffectBase<HealthChangeBasedOnDamage>
{
    [DataField]
    public float MaximumDamage = -50f;

    [DataField]
    public Dictionary<string, float> Damage = new();

    public override string? EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
        => null;
}

public sealed partial class HealthChangeBasedOnDamageSystem : EntityEffectSystem<DamageableComponent, HealthChangeBasedOnDamage>
{
    [Dependency] private DamageableSystem _damageable = default!;

    protected override void Effect(Entity<DamageableComponent> entity, ref EntityEffectEvent<HealthChangeBasedOnDamage> args)
    {
        var totalHeal = 0f;
        var dspec = new DamageSpecifier();
        var maxHeal = MathF.Abs(args.Effect.MaximumDamage);

        foreach (var (type, amount) in args.Effect.Damage)
        {
            if (totalHeal >= maxHeal)
                break;

            var existing = entity.Comp.Damage.DamageDict.GetValueOrDefault(type).Float();
            if (existing > 0)
            {
                var desiredHeal = MathF.Abs(amount);
                var remaining = maxHeal - totalHeal;
                var actualHeal = MathF.Min(existing, MathF.Min(desiredHeal, remaining));

                if (actualHeal > 0)
                {
                    dspec.DamageDict[type] = -actualHeal;
                    totalHeal += actualHeal;
                }
            }
        }

        if (dspec.DamageDict.Count > 0)
        {
            _damageable.TryChangeDamage(
                entity.Owner,
                dspec,
                ignoreResistances: true,
                interruptsDoAfters: false);
        }
    }
}
