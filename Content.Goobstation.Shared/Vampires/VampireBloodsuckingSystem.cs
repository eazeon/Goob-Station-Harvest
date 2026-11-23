// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Shared.Vampires;

public abstract partial class SharedVampireBloodsuckingSystem : EntitySystem
{
    [Dependency] protected SharedBloodstreamSystem _bloodstream = default!;
    [Dependency] protected IngestionSystem _ingestion = default!;
    [Dependency] protected SharedHandsSystem _hands = default!;
    [Dependency] protected SharedPopupSystem _popup = default!;
    [Dependency] protected SharedDoAfterSystem _doAfter = default!;
    [Dependency] protected SharedSolutionContainerSystem _solution = default!;
    [Dependency] protected SharedAudioSystem _audio = default!;
    [Dependency] protected SharedMindSystem _mind = default!;
    [Dependency] protected MobStateSystem _mobState = default!;
    [Dependency] protected HungerSystem _hunger = default!;
    // NOTE: EntityQuery dependencies removed - using EntityManager.TryComp directly to avoid registration issues

    private static readonly EntProtoId BiteEffect = "WeaponArcBite";
    private static readonly SoundSpecifier BiteSound = new SoundPathSpecifier("/Audio/Effects/bite.ogg");

    protected void OnMeleeHit(Entity<VampireBloodsuckingComponent> ent, ref MeleeHitEvent args)
    {
        if (args.HitEntities.Count == 0)
            return;

        var target = args.HitEntities.First();

        // Target must be alive, have blood, and we must meet the requirements.
        // VampireDrainableComponent is optional — tracks drain limits for player mobs.
        if (!_mobState.IsAlive(target) || !HasComp<BloodstreamComponent>(target) || !CanBloodSuck(ent.Owner))
            return;

        var attemptEv = new BloodsuckingAttemptEvent();
        RaiseLocalEvent(target, ref attemptEv);
        if (attemptEv.Cancelled)
        {
            _popup.PopupClient("This target cannot be drained!", ent.Owner, PopupType.MediumCaution);
            return;
        }

        if (!_ingestion.HasMouthAvailable(target, ent.Owner))
            return;

        BloodSuck(ent, target);

        // Cancel the normal hit interaction,
        // we don't want to continue the behavior.
        args.Handled = true;
    }

    protected void OnBloodSuckDoAfter(Entity<VampireBloodsuckingComponent> ent, ref BloodSuckDoAfterEvent args)
    {
        if (args.Cancelled || args.Target is not { } target)
            return;

        var user = ent.Owner;
        _hunger.ModifyHunger(user, ent.Comp.HungerRestoration);

        if (!TryComp<BloodstreamComponent>(target, out var bloodstream))
            return;

        if (!_solution.ResolveSolution(target, bloodstream.BloodSolutionName, ref bloodstream.BloodSolution, out var sol) || sol.Volume <= 0)
            return;

        // VampireDrainableComponent is optional — player mobs have it for drain-limit tracking.
        TryComp<VampireDrainableComponent>(target, out var drainable);

        // If we have already reached our limit on this target, don't go further.
        if (drainable != null && drainable.BloodGathered >= drainable.MaxBlood)
        {
            _popup.PopupClient("You have drained most of their life force, you will get no more usable blood from them", user, user, PopupType.MediumCaution);
            return;
        }

        var bloodToRemove = FixedPoint2.Min(ent.Comp.BloodToRemove, sol.Volume);
        var bloodInt = (int) bloodToRemove;

        _bloodstream.TryModifyBloodLevel((target, bloodstream), -bloodToRemove);
        _bloodstream.TryModifyBleedAmount((target, bloodstream), bloodstream.MaxBleedAmount * 0.6f);

        if (drainable != null)
        {
            drainable.BloodGathered += bloodInt;
            Dirty(target, drainable);
        }

        // Notify anyone, for example Vampires to update their blood pools
        var ev = new BloodsuckingSuccessEvent(bloodInt);
        RaiseLocalEvent(user, ref ev);

        _popup.PopupClient("You drain the life force out of them...", user, user, PopupType.MediumCaution);
        _popup.PopupEntity("You feel like your life force has been drained...", user, target, PopupType.MediumCaution);

        ent.Comp.ConsumedVictims.Add(target);
        Dirty(ent);
    }

    #region  Helper
    /// <summary>
    /// Starts the blood sucking process via DoAfter.
    /// </summary>
    protected void BloodSuck(Entity<VampireBloodsuckingComponent> ent, EntityUid target)
    {
        PredictedSpawnAtPosition(BiteEffect, Transform(target).Coordinates);
        _audio.PlayPredicted(BiteSound, target, ent.Owner);

        _popup.PopupClient("You start draining them...", ent.Owner, ent.Owner, PopupType.Medium);

        var doAfterArgs = new DoAfterArgs(
            EntityManager,
            user: ent.Owner,
            delay: ent.Comp.BloodsuckingDelay,
            @event: new BloodSuckDoAfterEvent(),
            eventTarget: ent.Owner,
            target: target
        )
        {
            BlockDuplicate = true,
            BreakOnHandChange = true,
            BreakOnMove = true,
        };

        if (!_doAfter.TryStartDoAfter(doAfterArgs))
        {
            _popup.PopupClient("The blood sucking process has failed!", ent.Owner, ent.Owner, PopupType.SmallCaution);
            Dirty(ent);
        }
    }

    /// <summary>
    /// Checks whether an entity can do a blood sucking sequence.
    /// </summary>
    /// <returns></returns>
    protected bool CanBloodSuck(EntityUid user)
    {
        // Our current selected hand must be empty for this to work.
        if (!_hands.ActiveHandIsEmpty(user))
            return false;

        // We must be targeting our target's head first.
        // Note: This will disallow a normal head targeting interaction, but it's fine if your active hand is not empty.
        if (!TryComp<TargetingComponent>(user, out var targeting) || targeting.Target != TargetBodyPart.Head)
            return false;

        return true;
    }
    #endregion
}
