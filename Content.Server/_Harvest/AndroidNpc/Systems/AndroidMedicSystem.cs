// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Damage.Systems;
using Content.Shared.Damage.Components;
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Database;
using Content.Shared.IdentityManagement;
using Content.Shared.Popups;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.BehaviorTree;
using Content.Server._Harvest.AndroidNpc.Services;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Shared.Bed.Components;
using Content.Shared._Shitmed.Medical.Surgery;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Humanoid;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Medical.SuitSensors;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared._DV.Carrying;
using Content.Shared.Hands.EntitySystems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Harvest.AndroidNpc.Systems;

/// <summary>
/// Paramedic androids: treat injured crew on the spot with injections (bicaridine, dermaline, dylovene, dexalin),
/// give epinephrine to critical patients and carry critical patients and bodies to medbay. The injections are
/// simulated, as with a hypospray: the drug goes into the bloodstream with the hypospray's sound and popups, and
/// heals over time like any medicine.
/// </summary>
public sealed class AndroidMedicSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedBuckleSystem _buckle = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly CarryingSystem _carrying = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly ClaimedTargetsSystem _claims = default!;
    [Dependency] private readonly AndroidInventorySystem _inventory = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;
    [Dependency] private readonly AndroidBreakSystem _breaks = default!;
    [Dependency] private readonly AndroidOrderSystem _orders = default!;

    private const float ScanRange = 12f;
    /// <summary>
    /// Reach for care and pulling; a patient lying on the floor keeps the android a little further away.
    /// </summary>
    private const float CareRange = 2f;

    /// <summary>
    /// How close the paramedic must be to grab a patient; pulling fails a bit further.
    /// </summary>
    private const float PullRange = 1.2f;

    /// <summary>
    /// A patient this close to the medbay beacon has arrived and is left to the doctors.
    /// </summary>
    private const float MedbayRadius = 2.5f;

    /// <summary>
    /// Patients this close to the medbay or morgue beacon are already there and are not dragged around.
    /// </summary>
    private const float MedbayArea = 5f;

    private const string MorgueBeacon = "station-beacon-morgue";

    private static readonly TimeSpan PlacesRefresh = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Living patients with less damage than this are left alone, unless they asked for care.
    /// </summary>
    private const float InjuredDamage = 20f;

    private const float EpinephrineUnits = 10f;
    private const string Epinephrine = "Epinephrine";

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InjectTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EpinephrineCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CarryAnnounceCooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What the android injects for each kind of damage. A drug is given again only once the patient has
    /// almost none of it left in the blood, so it never overdoses anyone.
    /// </summary>
    private static readonly (string Group, string Reagent)[] Medicines =
    {
        ("Brute", "Bicaridine"), ("Burn", "Dermaline"), ("Toxin", "Dylovene"), ("Airloss", "Dexalin"),
    };

    private const float MedicineUnits = 10f;
    private const float MedicineDamage = 5f;
    private const float RedoseBelow = 1f;

    private static readonly SoundSpecifier InjectSound = new SoundPathSpecifier("/Audio/Items/hypospray.ogg");

    /// <summary>
    /// Beds this close to the medbay beacon are medbay's.
    /// </summary>
    private const float BedSearchRange = 15f;
    private static readonly TimeSpan UnreachableBedMemory = TimeSpan.FromMinutes(2);

    private enum Need
    {
        None,
        Treat,
        Carry,
        Inject
    }

    /// <summary>
    /// Answers a call for help: "heal me" makes the caller the next patient, a reported casualty sends the
    /// android to the place named (or to the speaker). Returns what the android says.
    /// </summary>
    public string Call(EntityUid medic, EntityUid speaker, bool healMe, string? place, string language)
    {
        var feedback = _prototypeManager.AndroidFeedback(language);
        if (!TryComp<AndroidMedicComponent>(medic, out var comp))
            return feedback.NotAMedic;

        if (TryComp<AndroidComponent>(medic, out var android) && (android.BreakState != AndroidBreakState.None || android.Status == AndroidStatus.OnBreak))
        {
            _breaks.EndBreak(medic);
            android.NextBreakAt = _timing.CurTime + _prototypeManager.AndroidConfig().NextBreakDelay(_random);
        }

        if (healMe)
        {
            if (!TryComp<DamageableComponent>(speaker, out var damage) || damage.TotalDamage <= 0)
                return feedback.NotHurt;

            if (!TryComp<MobStateComponent>(speaker, out _) || !CanTreat(medic, comp, speaker, damage))
                return feedback.CannotTreat;

            _orders.ClearOrder(medic);
            comp.Caller = speaker;
            comp.NextScanAt = TimeSpan.Zero;
            return feedback.AckHealMe;
        }

        // The place is named after its beacon ("Bar", "Infirmerie"), not repeated as said.
        if (place == null ||
            !new NavMapBeaconLocator(EntityManager).TryFindPlace(Transform(medic).Coordinates, place, _dialogue.Classifier, out var name, out var coords))
        {
            _orders.IssueOrder(medic, speaker, AndroidOrderType.RespondToCall, null, Transform(speaker).Coordinates, language: language);
            return feedback.AckInjured;
        }

        _orders.IssueOrder(medic, speaker, AndroidOrderType.RespondToCall, name, coords, language: language);
        return AndroidData.Format(feedback.AckInjuredAt, ("target", name));
    }

    /// <summary>
    /// The patient to look after: the current one while it still needs care, else the most urgent one around
    /// (critical, then bodies and critical patients to carry, then the injured), the caller first.
    /// </summary>
    public EntityUid? SelectPatient(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp))
            return null;

        // A caller comes before a patient who only needs patching up.
        if (comp.Patient is { } current && NeedOf(medic, comp, current) is var currentNeed && currentNeed != Need.None &&
            !(currentNeed == Need.Treat && comp.Caller is { } waiting && waiting != current && NeedOf(medic, comp, waiting) != Need.None))
        {
            return current;
        }

        // Done with a patient: look for the next one right away instead of wandering off on patrol. One still on
        // the paramedic's back is put down (on a bed if one is at hand).
        if (comp.Patient is { } done)
        {
            comp.NextScanAt = TimeSpan.Zero;
            if (IsCarrying(medic, done))
                FinishCarry(medic);
        }

        DropPatient(medic, comp);

        var now = _timing.CurTime;
        if (now < comp.NextScanAt)
            return null;

        comp.NextScanAt = now + ScanInterval;

        if (comp.Caller is { } caller && NeedOf(medic, comp, caller) == Need.None)
            comp.Caller = null;

        var origin = _transform.GetWorldPosition(medic);
        EntityUid? best = null;
        var bestScore = float.MinValue;

        // Nearby crew, plus the downed crew whose suit sensors give their position anywhere on the station: the
        // android reads the sensors itself, so there is no crew monitor to loot from it.
        var candidates = _lookup.GetEntitiesInRange<MobStateComponent>(Transform(medic).Coordinates, ScanRange).Select(e => e.Owner).ToHashSet();
        candidates.UnionWith(DownedOnSensors(Transform(medic).GridUid));

        // Whoever called for care, by voice or on the radio, wherever they are.
        if (comp.Caller is { } calling)
            candidates.Add(calling);

        foreach (var patient in candidates)
        {
            var need = NeedOf(medic, comp, patient);
            if (need == Need.None || patient != comp.Caller && !HasComp<HumanoidAppearanceComponent>(patient) || _claims.IsClaimedByOther(patient, medic))
                continue;

            var score = (patient == comp.Caller ? 100f : 0f) + (int) need * 10f - (_transform.GetWorldPosition(patient) - origin).Length() * 0.1f;
            if (score <= bestScore)
                continue;

            best = patient;
            bestScore = score;
        }

        if (best is not { } chosen || !_claims.TryClaim(medic, chosen, TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().ClaimTtlSeconds)))
            return null;

        comp.Patient = chosen;
        return chosen;
    }

    /// <summary>
    /// Critical or dead crew on the grid whose suit sensors give their coordinates, as the crew monitor lists them.
    /// </summary>
    private IEnumerable<EntityUid> DownedOnSensors(EntityUid? grid)
    {
        var sensors = EntityQueryEnumerator<SuitSensorComponent>();
        while (sensors.MoveNext(out var sensor))
        {
            if (sensor.Mode != SuitSensorMode.SensorCords || sensor.User is not { } user ||
                !TryComp<MobStateComponent>(user, out var mob) || mob.CurrentState == MobState.Alive ||
                Transform(user).GridUid != grid)
            {
                continue;
            }

            yield return user;
        }
    }

    public EntityCoordinates? PatientCoordinates(EntityUid medic)
    {
        return TryComp<AndroidMedicComponent>(medic, out var comp) && comp.Patient is { } patient && Exists(patient)
            ? Transform(patient).Coordinates
            : null;
    }

    public bool MustCarry(EntityUid medic)
    {
        return TryComp<AndroidMedicComponent>(medic, out var comp) && comp.Patient is { } patient &&
               NeedOf(medic, comp, patient) == Need.Carry;
    }

    /// <summary>
    /// Treats the patient once in reach. Running while caring, Success when the patient needs nothing more,
    /// Failure while the android must walk up to it.
    /// </summary>
    public BehaviorStatus Care(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp) || comp.Patient is not { } patient)
            return BehaviorStatus.Failure;

        var need = NeedOf(medic, comp, patient);
        if (need is Need.None or Need.Carry)
        {
            // Settled in medbay while still on the paramedic's back: put it down (on a bed if one is at hand).
            if (need == Need.None && IsCarrying(medic, patient))
                FinishCarry(medic);

            return BehaviorStatus.Success;
        }

        // Epinephrine is given on the paramedic's back; other drugs wait until the patient is put down.
        if (need == Need.Treat && IsCarrying(medic, patient))
            FinishCarry(medic);

        if ((_transform.GetWorldPosition(patient) - _transform.GetWorldPosition(medic)).Length() > CareRange)
        {
            comp.CareUntil = null;
            return BehaviorStatus.Failure;
        }

        var now = _timing.CurTime;
        if (comp.CareUntil == null)
        {
            comp.CareUntil = now + InjectTime;
            Announce(medic, comp, patient, need);
            return BehaviorStatus.Running;
        }

        if (now < comp.CareUntil)
            return BehaviorStatus.Running;

        comp.CareUntil = null;
        if (need == Need.Inject)
            Inject(medic, comp, patient);
        else
            Treat(medic, patient);

        if (NeedOf(medic, comp, patient) == Need.None && _dialogue.LinesFor(medic) is { } lines)
            _dialogue.SpeakRandom(medic, lines.Treated, ("patient", Name(patient)));

        return BehaviorStatus.Running;
    }

    /// <summary>
    /// Picks the patient up to carry it to medbay (fireman carry: no drag to snag on door frames, the patient
    /// stays lying down). Running until it is on the paramedic's back.
    /// </summary>
    public BehaviorStatus StartCarry(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp) || comp.Patient is not { } patient ||
            !TryComp<CarriableComponent>(patient, out var carriable))
        {
            return BehaviorStatus.Failure;
        }

        if (IsCarrying(medic, patient))
            return BehaviorStatus.Success;

        // Picking up needs to be closer than treating: walk up to the patient first.
        if ((_transform.GetWorldPosition(patient) - _transform.GetWorldPosition(medic)).Length() > PullRange)
            return BehaviorStatus.Failure;

        // Carrying takes both hands.
        for (var i = 0; i < carriable.FreeHandsRequired && _hands.CountFreeHands(medic) < carriable.FreeHandsRequired; i++)
            _inventory.TryFreeHand(medic);

        if (!_carrying.TryCarry(medic, (patient, carriable)))
            return BehaviorStatus.Running;

        // Said once per patient, not every time a patient drifting in and out of crit is picked up again.
        var now = _timing.CurTime;
        if (comp.CarryAnnounced is not { } last || last.Patient != patient || now - last.At > CarryAnnounceCooldown)
        {
            comp.CarryAnnounced = (patient, now);
            if (_dialogue.LinesFor(medic) is { } lines)
                _dialogue.SpeakRandom(medic, lines.Carrying, ("patient", Name(patient)));
        }

        return BehaviorStatus.Success;
    }

    /// <summary>
    /// Where to carry patients: the medbay beacon, or the medical one. Looked up again every so often.
    /// </summary>
    public EntityCoordinates? MedbayCoordinates(EntityUid medic)
    {
        RefreshPlaces(medic);
        return CompOrNull<AndroidMedicComponent>(medic)?.Medbay;
    }

    private void RefreshPlaces(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp) || _timing.CurTime < comp.PlacesRefreshAt ||
            !TryComp<AndroidComponent>(medic, out var android) || _prototypeManager.AndroidJobData(android.Job) is not { } job)
        {
            return;
        }

        comp.PlacesRefreshAt = _timing.CurTime + PlacesRefresh;
        var locator = new NavMapBeaconLocator(EntityManager);
        var origin = Transform(medic).Coordinates;

        comp.Medbay = job.WorkBeacon != null && locator.TryFindBeacon(origin, job.WorkBeacon, out var coords) ||
                      job.ResupplyBeacon != null && locator.TryFindBeacon(origin, job.ResupplyBeacon, out coords)
            ? coords
            : null;
        comp.Morgue = locator.TryFindBeacon(origin, MorgueBeacon, out var morgue) ? morgue : null;

        // A bed in a room the paramedic cannot get into (behind a door it has no access to) is no use.
        comp.MedbayReachable.Clear();
        if (comp.Medbay is { } medbay)
        {
            var blackboard = AndroidBlackboard.Create(EntityManager, medic, new BlacklistManager());
            foreach (var tile in AndroidSpots.ReachableTiles(blackboard, medic, medbay, BedSearchRange + 3f, 800))
                comp.MedbayReachable.Add((tile.EntityId, tile.Position.Floored()));
        }
    }

    /// <summary>
    /// Whether the paramedic can walk up to the bed from medbay; unknown (navmesh not built yet) counts as yes.
    /// </summary>
    private bool CanReachBed(AndroidMedicComponent comp, EntityUid bed)
    {
        if (comp.MedbayReachable.Count == 0)
            return true;

        var coords = Transform(bed).Coordinates;
        var tile = coords.Position.Floored();
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        {
            if (comp.MedbayReachable.Contains((coords.EntityId, tile + new Vector2i(dx, dy))))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Where to carry the patient: a free stasis bed at medbay, else a medical bed, else the medbay beacon.
    /// </summary>
    public EntityCoordinates? CarryCoordinates(EntityUid medic)
    {
        return TryComp<AndroidMedicComponent>(medic, out var comp) && FreeBed(medic, comp) is { } bed
            ? Transform(bed).Coordinates
            : MedbayCoordinates(medic);
    }

    public bool IsCarrying(EntityUid medic, EntityUid patient)
    {
        return TryComp<BeingCarriedComponent>(patient, out var carried) && carried.Carrier == medic;
    }

    private void PutDown(EntityUid medic, EntityUid patient)
    {
        if (IsCarrying(medic, patient))
            _carrying.DropCarried(medic, patient);
    }

    /// <summary>
    /// Puts the patient on the bed it was carried to, or lets go of it at medbay, where the doctors take over.
    /// </summary>
    public void FinishCarry(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp) || comp.Patient is not { } patient)
            return;

        PutDown(medic, patient);

        if (FreeBed(medic, comp) is { } bed)
            _buckle.TryBuckle(patient, medic, bed, popup: false);

        if (comp.Bed is { } claimed)
            _claims.ReleaseClaim(claimed);

        comp.Bed = null;
        comp.Delivered.Add(patient);
    }

    /// <summary>
    /// The medbay bed claimed for the current patient, or a newly claimed free one: stasis beds first.
    /// </summary>
    private EntityUid? FreeBed(EntityUid medic, AndroidMedicComponent comp)
    {
        if (comp.Bed is { } bed && IsFreeBed(medic, comp, bed))
            return bed;

        comp.Bed = null;
        if (MedbayCoordinates(medic) is not { } medbay)
            return null;

        EntityUid? best = null;
        var bestScore = float.MaxValue;
        foreach (var (strap, _) in _lookup.GetEntitiesInRange<StrapComponent>(medbay, BedSearchRange, LookupFlags.Static))
        {
            if (!IsBedFor(comp.Patient, strap) || !IsFreeBed(medic, comp, strap))
                continue;

            var score = (HasComp<StasisBedComponent>(strap) ? 0f : 100f) + (_transform.GetWorldPosition(strap) - _transform.ToMapCoordinates(medbay).Position).Length();
            if (score >= bestScore)
                continue;

            best = strap;
            bestScore = score;
        }

        if (best is { } chosen && _claims.TryClaim(medic, chosen, TimeSpan.FromMinutes(2)))
            comp.Bed = chosen;

        return comp.Bed;
    }

    /// <summary>
    /// The way to the bed failed with a patient in tow: that bed is skipped for a while and the next one (or the
    /// medbay beacon) is tried, still holding the patient. Failing on the way to the beacon lets go of it.
    /// </summary>
    public void CarryFailed(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp))
            return;

        if (comp.Bed is not { } bed)
        {
            DropPatient(medic, comp);
            return;
        }

        comp.UnreachableBeds[bed] = _timing.CurTime;
        _claims.ReleaseClaim(bed);
        comp.Bed = null;
    }

    private bool IsFreeBed(EntityUid medic, AndroidMedicComponent comp, EntityUid bed)
    {
        if (comp.UnreachableBeds.TryGetValue(bed, out var failedAt) && _timing.CurTime - failedAt < UnreachableBedMemory ||
            !CanReachBed(comp, bed))
        {
            return false;
        }

        return !Deleted(bed) &&
               TryComp<StrapComponent>(bed, out var strap) && strap.Enabled &&
               strap.BuckledEntities.All(b => b == comp.Patient) &&
               !_claims.IsClaimedByOther(bed, medic);
    }

    public void DropPatient(EntityUid medic, AndroidMedicComponent comp)
    {
        if (comp.Patient is { } patient)
        {
            _claims.ReleaseClaim(patient);
            PutDown(medic, patient);
        }

        if (comp.Bed is { } bed)
            _claims.ReleaseClaim(bed);

        comp.Patient = null;
        comp.Bed = null;
        comp.CareUntil = null;
    }

    private Need NeedOf(EntityUid medic, AndroidMedicComponent comp, EntityUid patient)
    {
        if (patient == medic || Deleted(patient) ||
            !TryComp<MobStateComponent>(patient, out var mob) ||
            !TryComp<DamageableComponent>(patient, out var damage) ||
            _containers.IsEntityInContainer(patient) ||
            TryComp<PullableComponent>(patient, out var pullable) && pullable.Puller is { } puller && puller != medic ||
            TryComp<BeingCarriedComponent>(patient, out var carried) && carried.Carrier != medic)
        {
            return Need.None;
        }

        if (mob.CurrentState is MobState.Critical or MobState.Dead)
            comp.ToDeliver.Add(patient);

        // A patient found critical goes to medbay even when it briefly comes out of crit on the way,
        // unless it was patched up in the meantime.
        if (comp.ToDeliver.Contains(patient) && mob.CurrentState == MobState.Alive)
        {
            if (damage.TotalDamage < InjuredDamage || Settled(medic, comp, patient))
                comp.ToDeliver.Remove(patient);
            else
                return Need.Carry;
        }

        if (mob.CurrentState == MobState.Critical &&
            (!comp.InjectedAt.TryGetValue(patient, out var injected) || _timing.CurTime - injected > EpinephrineCooldown))
        {
            return Need.Inject;
        }

        // Critical patients and bodies need doctors (oxygen, surgery, defibrillator): bring them onto a medbay bed.
        // A critical patient on its bed keeps being looked after; a body is left to the doctors.
        if (mob.CurrentState is MobState.Critical or MobState.Dead)
        {
            if (!Settled(medic, comp, patient))
                return Need.Carry;

            return mob.CurrentState == MobState.Critical && CanTreat(medic, comp, patient, damage) ? Need.Treat : Need.None;
        }

        var hurt = damage.TotalDamage >= InjuredDamage || patient == comp.Caller && damage.TotalDamage > 0;
        return hurt && CanTreat(medic, comp, patient, damage) ? Need.Treat : Need.None;
    }

    /// <summary>
    /// Whether the patient is already where it should be: lying on a bed, at the morgue, or, when no medbay bed
    /// is free, at medbay (close to the beacon while being carried, anywhere in it otherwise).
    /// </summary>
    private bool Settled(EntityUid medic, AndroidMedicComponent comp, EntityUid patient)
    {
        if (TryComp<BuckleComponent>(patient, out var buckle) && buckle.Buckled)
            return true;

        // Without a medbay beacon there is nowhere to carry patients to.
        if (MedbayCoordinates(medic) is not { } medbay)
            return true;

        // A body lying at the morgue is left there; a living patient, or one carried past the morgue, is not.
        var coords = Transform(patient).Coordinates;
        if (comp.Morgue is { } morgue && _transform.InRange(morgue, coords, MedbayArea) &&
            _mobState.IsDead(patient) && !IsCarrying(medic, patient))
        {
            return true;
        }

        if (comp.Patient == patient ? FreeBed(medic, comp) != null : AnyFreeBed(medic, comp, medbay, patient))
            return false;

        var radius = comp.Patient == patient ? MedbayRadius : MedbayArea;
        return comp.Delivered.Contains(patient) || _transform.InRange(medbay, coords, radius);
    }

    private bool AnyFreeBed(EntityUid medic, AndroidMedicComponent comp, EntityCoordinates medbay, EntityUid patient)
    {
        foreach (var (strap, _) in _lookup.GetEntitiesInRange<StrapComponent>(medbay, BedSearchRange, LookupFlags.Static))
        {
            if (IsBedFor(patient, strap) && IsFreeBed(medic, comp, strap))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A body only goes on a stasis bed, where it does not rot and the doctors see it; a critical patient on a
    /// stasis bed or a medical bed. Never an operating table.
    /// </summary>
    private bool IsBedFor(EntityUid? patient, EntityUid bed)
    {
        if (HasComp<OperatingTableComponent>(bed))
            return false;

        if (HasComp<StasisBedComponent>(bed))
            return true;

        return HasComp<HealOnBuckleComponent>(bed) && !(patient is { } p && _mobState.IsDead(p));
    }

    private bool CanTreat(EntityUid medic, AndroidMedicComponent comp, EntityUid patient, DamageableComponent damage)
    {
        return NeededMedicine(patient, damage) != null;
    }

    /// <summary>
    /// The drug for the patient's worst kind of damage that it does not already have in its blood.
    /// </summary>
    private string? NeededMedicine(EntityUid patient, DamageableComponent damage)
    {
        if (!_solutions.TryGetInjectableSolution(patient, out _, out var blood))
            return null;

        string? best = null;
        var worst = MedicineDamage;
        foreach (var (group, reagent) in Medicines)
        {
            var amount = damage.DamagePerGroup.GetValueOrDefault(group).Float();
            if (amount <= worst || blood.GetReagentQuantity(new ReagentId(reagent, null)) >= RedoseBelow)
                continue;

            best = reagent;
            worst = amount;
        }

        return best;
    }

    private void Treat(EntityUid medic, EntityUid patient)
    {
        if (TryComp<DamageableComponent>(patient, out var damage) && NeededMedicine(patient, damage) is { } reagent)
            InjectDrug(medic, patient, reagent, MedicineUnits);
    }

    private void Inject(EntityUid medic, AndroidMedicComponent comp, EntityUid patient)
    {
        comp.InjectedAt[patient] = _timing.CurTime;
        InjectDrug(medic, patient, Epinephrine, EpinephrineUnits);
    }

    /// <summary>
    /// A hypospray injection without the item: the drug goes into the patient's blood with the hypospray's
    /// sound, the "small prick" the patient feels, a popup for the people around and an admin log.
    /// </summary>
    private void InjectDrug(EntityUid medic, EntityUid patient, string reagent, float units)
    {
        if (!_solutions.TryGetInjectableSolution(patient, out var blood, out _) ||
            !_solutions.TryAddReagent(blood.Value, reagent, units, out var accepted) || accepted <= 0)
        {
            return;
        }

        _audio.PlayPvs(InjectSound, patient);
        _popup.PopupEntity(Loc.GetString("injector-component-feel-prick-message"), patient, patient);
        _popup.PopupEntity(Loc.GetString("android-medic-inject-others",
                ("user", Identity.Entity(medic, EntityManager)), ("target", Identity.Entity(patient, EntityManager))),
            patient, Filter.PvsExcept(patient), true);
        _adminLogger.Add(LogType.ForceFeed, LogImpact.Low,
            $"{ToPrettyString(medic):user} (android paramedic) injected {ToPrettyString(patient):target} with {accepted}u of {reagent}");
    }

    private void Announce(EntityUid medic, AndroidMedicComponent comp, EntityUid patient, Need need)
    {
        if (comp.Announced == (patient, need == Need.Inject) || _dialogue.LinesFor(medic) is not { } lines)
            return;

        comp.Announced = (patient, need == Need.Inject);
        _dialogue.SpeakRandom(medic, need == Need.Inject ? lines.Stabilizing : lines.Treating, ("patient", Name(patient)));
    }
}
