// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server._Harvest.AndroidNpc.BehaviorTree;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Trees;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Services;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared._Trauma.Hands;
using Content.Shared.Clothing;
using Content.Shared.CombatMode;
using Content.Shared.Examine;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.Mind.Components;
using Content.Shared.NPC;
using Content.Shared.SSDIndicator;
using Content.Shared.Standing;
using Content.Shared.Strip.Components;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared._Shitmed.Body.Part;
using Content.Shared._Shitmed.Cybernetics;
using Content.Shared._Shitmed.Medical.Surgery.Pain.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Harvest.AndroidNpc.Systems;

/// <summary>
/// Main orchestrator that ticks behavior trees for all android NPCs.
/// </summary>
public sealed class AndroidSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedHumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ClaimedTargetsSystem _claims = default!;
    [Dependency] private readonly AndroidInventorySystem _inventorySystem = default!;
    [Dependency] private readonly SharedBodySystem _bodySystem = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;

    private readonly IBlacklistManager _blacklist = new BlacklistManager();

    private ISawmill _sawmill = default!;
    private readonly Dictionary<AndroidJob, IBehaviorNode> _trees = new();
    private readonly HashSet<string> _usedNames = new();
    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// A behavior tree tick slower than this is logged, so stalls can be traced to a branch.
    /// </summary>
    private const double SlowTreeTickMs = 10;

    /// <summary>
    /// All currently loaded behavior trees, keyed by job.
    /// </summary>
    public IReadOnlyDictionary<AndroidJob, IBehaviorNode> Trees => _trees;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("AndroidNpc");
        BuildTrees();

        SubscribeLocalEvent<AndroidComponent, MapInitEvent>(OnMapInit, after: [typeof(LoadoutSystem)]);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<AndroidComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<AndroidComponent, DisarmedEvent>(OnDisarmed, before: [typeof(PredictedHandsSystem)]);
        SubscribeLocalEvent<AndroidComponent, DropHandItemsEvent>(OnDropHandItems, before: [typeof(PredictedHandsSystem)]);
    }

    /// <summary>
    /// A shove still costs an android stamina, but never takes what it holds.
    /// </summary>
    private void OnDisarmed(Entity<AndroidComponent> ent, ref DisarmedEvent args)
    {
        args = args with { DisarmProbability = 0f };
    }

    /// <summary>
    /// Stunned, critical or dead, an android keeps its items in hand: they cannot be looted and go away with its body.
    /// </summary>
    private void OnDropHandItems(Entity<AndroidComponent> ent, ref DropHandItemsEvent args)
    {
        args.Handled = true;
    }

    private void BuildTrees()
    {
        foreach (var job in Enum.GetValues<AndroidJob>())
            _trees[job] = AndroidTrees.Build(job);
    }

    private void OnExamined(EntityUid uid, AndroidComponent component, ExaminedEvent args)
    {
        if (TryComp<AndroidIdentityComponent>(uid, out var identity) && identity.FirstName.Length > 0)
            args.PushMarkup(AndroidData.Format(_prototypeManager.AndroidFeedback().ExamineHint, ("name", identity.FirstName)));
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _usedNames.Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<AndroidComponent, AndroidIdentityComponent>();

        while (query.MoveNext(out var uid, out var android, out var identity))
        {
            // Fleeing and leaving take priority. Otherwise, an active order means the android is on a command,
            // and without either it should be doing its normal job.
            if (android.Status is not (AndroidStatus.Fleeing or AndroidStatus.Leaving))
            {
                if (_entityManager.HasComponent<AndroidOrderComponent>(uid))
                    android.Status = AndroidStatus.FollowingOrder;
                else if (android.Status == AndroidStatus.FollowingOrder)
                    android.Status = AndroidStatus.Working;
            }

            var now = _timing.CurTime;
            if (now < android.NextTreeTickAt)
                continue;

            var treeFrameTime = android.LastTreeTickAt == TimeSpan.Zero ? frameTime : (float) (now - android.LastTreeTickAt).TotalSeconds;
            android.LastTreeTickAt = now;
            android.NextTreeTickAt = now + TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().TreeTickIntervalSeconds);

            if (!_trees.TryGetValue(android.Job, out var tree))
            {
                _sawmill.Warning($"No behavior tree for job {android.Job}");
                continue;
            }

            _blacklist.Cleanup(android, _timing.CurTime);

            var blackboard = AndroidBlackboard.Create(_entityManager, uid, _blacklist);
            if (android.Trace != null)
            {
                android.Trace.Clear();
                blackboard.Trace = android.Trace;
            }

            var started = _stopwatch.Elapsed;
            tree.Tick(uid, blackboard, treeFrameTime);
            var ms = (_stopwatch.Elapsed - started).TotalMilliseconds;

            if (ms > SlowTreeTickMs)
            {
                var trace = android.Trace is { Count: > 0 } lines ? $"; trace: {string.Join(" / ", lines.Select(l => l.Trim()))}" : string.Empty;
                _sawmill.Warning($"[{identity.FirstName}] Slow behavior tree tick: {ms:F1} ms ({android.Status}, break {android.BreakState}){trace}");
            }
        }
    }

    private void OnMapInit(EntityUid uid, AndroidComponent component, MapInitEvent args)
    {
        // No strip menu, so nothing can be taken from an android, alive or dead. Inherited from the species prototype.
        RemComp<StrippableComponent>(uid);

        if (!_entityManager.TryGetComponent<AndroidIdentityComponent>(uid, out var identity))
            return;

        if (!_entityManager.TryGetComponent<AndroidComponent>(uid, out var android))
            return;

        // Androids are synthetic and sexless; this stops the humanoid layer system
        // from looking for a sex-specific sprite prototype like MobAndroidBodyMale.
        if (_entityManager.TryGetComponent<HumanoidAppearanceComponent>(uid, out var humanoid))
            _humanoid.SetSex(uid, Sex.Unsexed, true, humanoid);

        AssignName(uid, identity, android);

        // Each android gets a unique personal space so they don't crowd people/objects uniformly.
        component.PersonalSpace = _random.Next(10, 31) / 10f;
        component.Status = AndroidStatus.Working;

        // Spread tree evaluations of androids spawned together over different ticks.
        component.NextTreeTickAt = _timing.CurTime + TimeSpan.FromSeconds(_random.NextFloat() * _prototypeManager.AndroidConfig().TreeTickIntervalSeconds);

        var config = _prototypeManager.AndroidConfig();
        component.NextBreakAt = _timing.CurTime + config.NextBreakDelay(_random);

        if (_entityManager.TryGetComponent<TransformComponent>(uid, out var xform))
        {
            var locator = new NavMapBeaconLocator(_entityManager);
            if (locator.TryFindBeacon(xform.Coordinates, "bar", out var barCoords))
                component.BarLocation = barCoords.Value;
        }

        // First idle chatter happens a couple of minutes after spawning so they don't all speak at once.
        component.NextChatterTime = _timing.CurTime + TimeSpan.FromSeconds(_random.Next(120, 301));

        if (_entityManager.TryGetComponent<AndroidInventoryComponent>(uid, out var inventory))
        {
            _inventorySystem.InitializeLoadout(uid, inventory);
        }

        CybernetizeBody(uid, android);

        if (_entityManager.TryGetComponent<AndroidMemoryComponent>(uid, out var memory))
        {
            SetRandomPatrolNudge(uid, memory);
        }

        _inventorySystem.UpdatePdaAndId(uid, identity);

        // Androids are NPCs, not SSD players, so remove the examine text.
        _entityManager.RemoveComponent<SSDIndicatorComponent>(uid);
        _entityManager.RemoveComponent<MindExaminableComponent>(uid);

        // Allow the shared NPC steering system to process this entity.
        _entityManager.EnsureComponent<ActiveNPCComponent>(uid);

        _sawmill.Info($"[{identity.FirstName}] Android initialized");
    }

    private void SetRandomPatrolNudge(EntityUid uid, AndroidMemoryComponent memory)
    {
        var job = _inventorySystem.GetJobFor(uid);
        var routeCount = job?.PatrolRoute.Count ?? 0;
        memory.PatrolIndex = routeCount > 0 ? _random.Next(routeCount) : 0;

        var dir = _random.Next(4);
        memory.PatrolDirection = dir switch
        {
            0 => new Vector2(1, 0),
            1 => new Vector2(-1, 0),
            2 => new Vector2(0, 1),
            _ => new Vector2(0, -1)
        };
    }

    private void AssignName(EntityUid uid, AndroidIdentityComponent identity, AndroidComponent android)
    {
        var firstName = PickFirstName();
        var config = _prototypeManager.AndroidConfig();

        if (string.IsNullOrEmpty(firstName))
            firstName = config.DefaultFirstName;

        var prefix = config.NamePrefix;
        var jobTitle = GetJobTitle(android.Job);

        identity.FirstName = firstName;
        identity.JobTitle = jobTitle;
        identity.FullDisplayName = $"{prefix} {firstName}";
        identity.LastName = string.Empty;

        if (_entityManager.TryGetComponent<MetaDataComponent>(uid, out var metaData))
        {
            _entityManager.System<MetaDataSystem>().SetEntityName(uid, identity.FullDisplayName, metaData);
        }
    }


    private string GetJobTitle(AndroidJob job)
    {
        return _prototypeManager.AndroidJobData(job)?.JobTitle ?? job.ToString();
    }

    private string PickFirstName()
    {
        var nameLists = _prototypeManager.EnumeratePrototypes<AndroidNamePoolPrototype>().ToList();
        var allNames = new List<string>();

        foreach (var list in nameLists)
        {
            foreach (var name in list.Names)
            {
                if (!_usedNames.Contains(name))
                    allNames.Add(name);
            }
        }

        if (allNames.Count == 0)
        {
            _sawmill.Warning("Android name pool exhausted; reusing names");
            _usedNames.Clear();

            foreach (var list in nameLists)
            {
                allNames.AddRange(list.Names);
            }
        }

        if (allNames.Count == 0)
            return string.Empty;

        var selected = _random.Pick(allNames);
        _usedNames.Add(selected);
        return selected;
    }

    /// <summary>
    ///     Converts a large portion of the android's body to cybernetic parts and organs,
    ///     averaging around 70% cybernetic composition. The head is always cybernetic.
    /// </summary>
    private void CybernetizeBody(EntityUid uid, AndroidComponent android)
    {
        if (!_entityManager.TryGetComponent<HumanoidAppearanceComponent>(uid, out var humanoid))
            return;

        // Random synthetic skin tone: low saturation, medium value so it looks robotic
        // but still passes the Android species' Hues coloration clamp.
        var skinColor = Color.FromHsv(new Vector4(
            _random.NextFloat(),
            _random.NextFloat() * 0.25f,
            0.4f + _random.NextFloat() * 0.4f,
            1f));
        _humanoid.SetSkinColor(uid, skinColor, true, true, humanoid);

        humanoid.EyeColor = Color.FromHsv(new Vector4(
            _random.NextFloat(),
            0.9f,
            1.0f,
            1f));
        Dirty(uid, humanoid);

        var humanSprites = _prototypeManager.Index<HumanoidSpeciesBaseSpritesPrototype>("MobHumanSprites").Sprites;

        foreach (var (partId, part) in _bodySystem.GetBodyChildren(uid))
        {
            var layer = part.ToHumanoidLayers();
            if (layer == null)
                continue;

            var isHead = part.PartType == BodyPartType.Head;
            var cyberize = isHead || _random.Prob(0.70f);

            if (cyberize)
                ApplyCyberneticPart(uid, partId, part, layer.Value, humanoid, humanSprites);
            else
                ApplyHumanPart(uid, partId, part, layer.Value, humanoid, humanSprites, skinColor);

            // Organs are replaced regardless of whether the surrounding part was
            // randomized as cybernetic, so every android gets cybernetic eyes,
            // heart, lungs and liver.
            ReplaceOrgansWithCybernetic(partId, part);
        }
    }

    private void ApplyCyberneticPart(EntityUid bodyId, EntityUid partId, BodyPartComponent part, HumanoidVisualLayers layer, HumanoidAppearanceComponent humanoid, Dictionary<HumanoidVisualLayers, string> humanSprites)
    {
        var cyberId = GetCyberneticLayerId(layer);
        if (string.IsNullOrEmpty(cyberId))
            return;

        part.BaseLayerId = cyberId;
        Dirty(partId, part);

        var appearance = _entityManager.EnsureComponent<BodyPartAppearanceComponent>(partId);
        appearance.ID = cyberId;
        appearance.Type = layer;
        appearance.Color = null;
        Dirty(partId, appearance);

        _humanoid.SetBaseLayerId(bodyId, layer, cyberId, true, humanoid);

        EnsureComp<CyberneticsComponent>(partId);

        if (_entityManager.TryGetComponent<WoundableComponent>(partId, out var woundable))
        {
            woundable.DamageContainerID = "Silicon";
            woundable.CanBleed = false;
            Dirty(partId, woundable);
        }

        if (_entityManager.TryGetComponent<DamageableComponent>(partId, out var damageable))
        {
            _damageable.ChangeDamageContainer(partId, "Silicon", damageable);
        }

        part.PartComposition = BodyPartComposition.Inorganic;
        Dirty(partId, part);

        if (_entityManager.TryGetComponent<NerveComponent>(partId, out var nerve))
        {
            nerve.PainMultiplier = 1.5f;
            Dirty(partId, nerve);
        }
    }

    private void ApplyHumanPart(EntityUid bodyId, EntityUid partId, BodyPartComponent part, HumanoidVisualLayers layer, HumanoidAppearanceComponent humanoid, Dictionary<HumanoidVisualLayers, string> humanSprites, Color skinColor)
    {
        if (!humanSprites.TryGetValue(layer, out var humanId))
            return;

        part.BaseLayerId = humanId;
        Dirty(partId, part);

        var appearance = _entityManager.EnsureComponent<BodyPartAppearanceComponent>(partId);
        appearance.ID = humanId;
        appearance.Type = layer;
        appearance.Color = skinColor;
        Dirty(partId, appearance);

        _humanoid.SetBaseLayerId(bodyId, layer, humanId, true, humanoid);
        _humanoid.SetBaseLayerColor(bodyId, layer, skinColor, true, humanoid);

        if (layer == HumanoidVisualLayers.Head && humanSprites.TryGetValue(HumanoidVisualLayers.Eyes, out var eyesId))
        {
            _humanoid.SetBaseLayerId(bodyId, HumanoidVisualLayers.Eyes, eyesId, true, humanoid);
            _humanoid.SetBaseLayerColor(bodyId, HumanoidVisualLayers.Eyes, humanoid.EyeColor, true, humanoid);
        }
    }

    private string? GetCyberneticLayerId(HumanoidVisualLayers layer)
    {
        return layer switch
        {
            HumanoidVisualLayers.Head => "MobCyberneticBishopHead",
            HumanoidVisualLayers.Chest => "MobCyberneticBishopChest",
            HumanoidVisualLayers.Groin => "MobCyberneticBishopGroin",
            HumanoidVisualLayers.LArm => "MobCyberneticBishopLArm",
            HumanoidVisualLayers.RArm => "MobCyberneticBishopRArm",
            HumanoidVisualLayers.LHand => "MobCyberneticBishopLHand",
            HumanoidVisualLayers.RHand => "MobCyberneticBishopRHand",
            HumanoidVisualLayers.LLeg => "MobCyberneticBishopLLeg",
            HumanoidVisualLayers.RLeg => "MobCyberneticBishopRLeg",
            HumanoidVisualLayers.LFoot => "MobCyberneticBishopLFoot",
            HumanoidVisualLayers.RFoot => "MobCyberneticBishopRFoot",
            _ => null
        };
    }

    private void ReplaceOrgansWithCybernetic(EntityUid partId, BodyPartComponent part)
    {
        var replacements = new Dictionary<string, string>
        {
            { "eyes", "BasicCyberneticEyes" },
            { "heart", "BasicCyberneticHeart" },
            { "lungs", "BasicCyberneticLungs" },
            { "liver", "BasicCyberneticLiver" }
        };

        foreach (var (organId, organ) in _bodySystem.GetPartOrgans(partId, part).ToList())
        {
            if (!replacements.TryGetValue(organ.SlotId, out var cyberProto))
                continue;

            var slotId = organ.SlotId;
            if (!_bodySystem.TryRemoveOrgan(organId, organ))
            {
                _sawmill.Warning($"Failed to remove organic {slotId} organ from android part {partId}");
                continue;
            }

            QueueDel(organId);

            var newOrgan = Spawn(cyberProto, Transform(partId).Coordinates);
            if (!_bodySystem.InsertOrgan(partId, newOrgan, slotId, part))
            {
                _sawmill.Warning($"Failed to insert cybernetic {slotId} organ into android part {partId}");
                QueueDel(newOrgan);
            }
        }
    }
}
