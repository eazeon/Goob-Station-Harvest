using System.Numerics;
using Content.Shared.FixedPoint;
using Content.Shared.Administration.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Sprite;

namespace Content.Goobstation.Server.AdminTools;

public sealed partial class AdminOverrideSystem : EntitySystem
{
    [Dependency] private SharedScaleVisualsSystem _scale = null!;

    private readonly Dictionary<EntityUid, AdminOverrideCache> _cache = new();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<AdminOverrideComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!_cache.TryGetValue(uid, out var cached))
                cached = new AdminOverrideCache();

            if (comp.CritThreshold != cached.CritThreshold || comp.DeadThreshold != cached.DeadThreshold)
            {
                if (TryComp<MobThresholdsComponent>(uid, out var thresholds))
                {
                    if (comp.CritThreshold >= FixedPoint2.Zero)
                        thresholds.CritThreshold = comp.CritThreshold;
                    if (comp.DeadThreshold >= FixedPoint2.Zero)
                        thresholds.DeadThreshold = comp.DeadThreshold;
                    Dirty(uid, thresholds);
                }
            }

            if (comp.EntityScale != cached.EntityScale)
            {
                if (comp.EntityScale != Vector2.Zero)
                    _scale.SetSpriteScale(uid, comp.EntityScale);
            }

            _cache[uid] = new AdminOverrideCache(
                comp.EntityScale,
                comp.CritThreshold,
                comp.DeadThreshold
            );
        }
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AdminOverrideComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnShutdown(EntityUid uid, AdminOverrideComponent component, ComponentShutdown args)
    {
        _cache.Remove(uid);
    }

    private record AdminOverrideCache(
        Vector2 EntityScale = default,
        FixedPoint2 CritThreshold = default,
        FixedPoint2 DeadThreshold = default
    );
}
