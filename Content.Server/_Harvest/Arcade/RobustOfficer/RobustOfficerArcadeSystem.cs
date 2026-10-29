using System.Linq;
using Content.Server.Advertise.EntitySystems;
using Content.Shared._Harvest.Arcade.RobustOfficer;
using Content.Shared.Advertise.Components;
using Content.Shared.Power;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Harvest.Arcade.RobustOfficer;

/// <summary>
/// Runs the Robust Officer arcade: one player controls the officer, anyone else with the UI open watches.
/// </summary>
public sealed class RobustOfficerArcadeSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SpeakOnUIClosedSystem _speakOnUIClosed = default!;
    [Dependency] private readonly RobustOfficerLeaderboardSystem _leaderboard = default!;

    private const byte Up = 1 << 0;
    private const byte Down = 1 << 1;
    private const byte Left = 1 << 2;
    private const byte Right = 1 << 3;

    private List<RobustOfficerSectorPrototype> _sectors = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RobustOfficerArcadeComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<RobustOfficerArcadeComponent, AfterActivatableUIOpenEvent>(OnAfterUiOpen);
        SubscribeLocalEvent<RobustOfficerArcadeComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        Subs.BuiEvents<RobustOfficerArcadeComponent>(RobustOfficerUiKey.Key, subs =>
        {
            subs.Event<BoundUIClosedEvent>(OnUiClosed);
            subs.Event<RobustOfficerActionMessage>(OnAction);
            subs.Event<RobustOfficerBuyMessage>(OnBuy);
        });

        LoadSectors();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<RobustOfficerSectorPrototype>())
            LoadSectors();
    }

    private void LoadSectors()
    {
        _sectors = _prototypes.EnumeratePrototypes<RobustOfficerSectorPrototype>()
            .OrderBy(s => s.Order)
            .ThenBy(s => s.ID)
            .ToList();
    }

    /// <summary>
    /// The shop opens after every second room.
    /// </summary>
    public static bool ShopAfter(int levelIndex) => levelIndex % 2 == 1;

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<RobustOfficerArcadeComponent>();
        while (query.MoveNext(out var uid, out var arcade))
        {
            if (!arcade.Playing || arcade.Simulation == null)
                continue;

            arcade.Accumulator += frameTime;
            var changed = false;
            var events = RobustOfficerEvents.None;
            while (arcade.Accumulator >= RobustOfficerSimulation.TickSeconds)
            {
                arcade.Accumulator -= RobustOfficerSimulation.TickSeconds;
                events |= Step((uid, arcade));
                changed = true;
            }

            if (changed)
                SendState((uid, arcade), events: events);

            if (arcade.TransitionTimer != null)
            {
                arcade.TransitionTimer -= frameTime;
                if (arcade.TransitionTimer <= 0)
                    EndTransition((uid, arcade));
            }
        }
    }

    private RobustOfficerEvents Step(Entity<RobustOfficerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        var sim = arcade.Simulation!;

        sim.Move = ToVector(arcade.MoveKeys);
        var shoot = ToVector(arcade.ShootKeys);
        sim.Shoot = shoot == Vector2i.Zero && arcade.ShootTapped is { } tapped ? tapped : shoot;
        sim.UseItem |= arcade.UseItem;
        arcade.ShootTapped = null;
        arcade.UseItem = false;

        var events = sim.Tick();

        if (events.HasFlag(RobustOfficerEvents.ExitOpened))
        {
            _audio.PlayPvs(arcade.ExitOpenSound, ent);
            arcade.TilesDirty = true;
        }

        if (events.HasFlag(RobustOfficerEvents.BossKilled))
        {
            _audio.PlayPvs(arcade.BossKilledSound, ent);
            Dispense(ent);
        }

        if (events.HasFlag(RobustOfficerEvents.NukeExploded))
            _audio.PlayPvs(arcade.NukeSound, ent);

        if (events.HasFlag(RobustOfficerEvents.PlayerDied))
        {
            _audio.PlayPvs(arcade.DeathSound, ent);
            arcade.TransitionTimer = arcade.DeathDelay;
        }
        else if (events.HasFlag(RobustOfficerEvents.Exited))
        {
            arcade.Run!.Score += arcade.RoomBonus;
            arcade.TransitionTimer = arcade.ExitDelay;
        }

        return events;
    }

    /// <summary>
    /// Goes on once the death or the exit has been shown long enough.
    /// </summary>
    private void EndTransition(Entity<RobustOfficerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        arcade.TransitionTimer = null;
        var sim = arcade.Simulation!;
        var run = arcade.Run!;

        if (sim.Dead)
        {
            if (run.Lives <= 0)
            {
                EndGame(ent, RobustOfficerEnding.Defeat);
                return;
            }

            sim.Respawn();
            SendState(ent);
            return;
        }

        if (!sim.Exited)
            return;

        var finished = arcade.LevelIndex;
        arcade.LevelIndex++;
        if (arcade.Mode == RobustOfficerMode.Standard && arcade.LevelIndex >= _sectors.Count)
        {
            Win(ent);
            return;
        }

        if (ShopAfter(finished))
        {
            OpenShop(ent);
            return;
        }

        StartLevel(ent);
    }

    private void StartLevel(Entity<RobustOfficerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        RobustOfficerRoom room;
        if (arcade.Mode == RobustOfficerMode.Endless)
        {
            var depth = arcade.LevelIndex + 1;
            room = RobustOfficerRoomGenerator.Generate(depth, unchecked(arcade.Seed + depth * 104729));
            arcade.LevelName = room.Boss is { } boss
                ? Loc.GetString("robust-officer-level-endless-boss", ("boss", BossName(boss)))
                : Loc.GetString("robust-officer-level-endless");
            arcade.LevelReward = null;
        }
        else
        {
            if (_sectors.Count == 0)
                return;

            var index = Math.Min(arcade.LevelIndex, _sectors.Count - 1);
            var sector = _sectors[index];
            // The nth boss of the campaign has n times its health.
            var bosses = _sectors.Take(index + 1).Count(s => s.Boss != null);
            room = new RobustOfficerRoom(sector.Map, sector.Seconds, sector.Boss, sector.Waves, sector.Theme,
                bossHealthMultiplier: Math.Max(1, bosses));
            arcade.LevelName = Loc.GetString(sector.Name);
            arcade.LevelReward = sector.Reward;
        }

        arcade.Simulation = new RobustOfficerSimulation(room, arcade.Run!, unchecked(arcade.Seed + arcade.LevelIndex * 7919));
        arcade.Accumulator = 0;
        arcade.TilesDirty = true;
        arcade.ShootTapped = null;
        arcade.UseItem = false;
        SendState(ent);
    }

    private string BossName(RobustOfficerBoss boss)
    {
        return Loc.GetString($"robust-officer-boss-{boss.ToString().ToLowerInvariant()}");
    }

    private void NewGame(Entity<RobustOfficerArcadeComponent> ent, RobustOfficerMode mode)
    {
        var arcade = ent.Comp;
        arcade.Mode = mode;
        arcade.Seed = _random.Next();
        arcade.Run = new RobustOfficerRun(arcade.StartingLives);
        arcade.Started = true;
        arcade.Playing = true;
        arcade.GameOver = false;
        arcade.InShop = false;
        arcade.LevelIndex = 0;
        arcade.TransitionTimer = null;
        arcade.RoundPlacement = null;
        arcade.AllTimePlacement = null;

        _audio.PlayPvs(arcade.NewGameSound, ent);
        SendScreen(ent, RobustOfficerScreen.Game);
        StartLevel(ent);
    }

    /// <summary>
    /// The campaign is over: the cabinet hands out its final prizes and the game ends with the victory screen.
    /// </summary>
    private void Win(Entity<RobustOfficerArcadeComponent> ent)
    {
        var coords = Transform(ent).Coordinates;
        foreach (var reward in ent.Comp.FinalRewards)
        {
            SpawnAtPosition(reward, coords);
        }

        EndGame(ent, RobustOfficerEnding.Victory);
    }

    private void EndGame(Entity<RobustOfficerArcadeComponent> ent, RobustOfficerEnding ending)
    {
        var arcade = ent.Comp;
        arcade.Playing = false;
        arcade.GameOver = true;
        arcade.InShop = false;
        arcade.Ending = ending;
        arcade.TransitionTimer = null;
        ClearInput(arcade);

        if (arcade.Player is { } player && arcade.Run is { } run)
        {
            var name = MetaData(player).EntityName;
            (arcade.RoundPlacement, arcade.AllTimePlacement) = _leaderboard.Register(arcade.Mode, name, run.Score, arcade.LevelIndex + 1);
        }

        _audio.PlayPvs(ending == RobustOfficerEnding.Victory ? arcade.VictorySound : arcade.GameOverSound, ent);
        SendHighscores(ent, mode: arcade.Mode);
        SendScreen(ent, RobustOfficerScreen.GameOver);
    }

    private void OpenShop(Entity<RobustOfficerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        arcade.InShop = true;
        arcade.Playing = false;
        ClearInput(arcade);
        SendScreen(ent, RobustOfficerScreen.Shop);
        SendShop(ent);
    }

    private void Dispense(Entity<RobustOfficerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        var coords = Transform(ent).Coordinates;
        if (arcade.LevelReward is { } levelReward)
            SpawnAtPosition(levelReward, coords);

        if (arcade.RewardAmount <= 0 || arcade.PossibleRewards.Count == 0)
            return;

        SpawnAtPosition(_random.Pick(arcade.PossibleRewards), coords);
        arcade.RewardAmount--;
    }

    private void OnMapInit(Entity<RobustOfficerArcadeComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.RewardAmount = _random.Next(ent.Comp.RewardMinAmount, ent.Comp.RewardMaxAmount + 1);
    }

    private void OnAfterUiOpen(Entity<RobustOfficerArcadeComponent> ent, ref AfterActivatableUIOpenEvent args)
    {
        var arcade = ent.Comp;
        if (arcade.Player == null)
            arcade.Player = args.User;
        else if (arcade.Player != args.User && !arcade.Spectators.Contains(args.User))
            arcade.Spectators.Add(args.User);

        SendUserStatus(ent, args.User);
        SendHighscores(ent, args.User);

        var screen = arcade.GameOver ? RobustOfficerScreen.GameOver
            : !arcade.Started ? RobustOfficerScreen.Title
            : arcade.InShop ? RobustOfficerScreen.Shop
            : arcade.Playing ? RobustOfficerScreen.Game
            : RobustOfficerScreen.Pause;
        SendScreen(ent, screen, args.User);
        SendState(ent, args.User);
        if (arcade.InShop)
            SendShop(ent, args.User);
    }

    private void OnUiClosed(Entity<RobustOfficerArcadeComponent> ent, ref BoundUIClosedEvent args)
    {
        var arcade = ent.Comp;
        if (arcade.Player != args.Actor)
        {
            arcade.Spectators.Remove(args.Actor);
            return;
        }

        ClearInput(arcade);
        arcade.Player = null;
        if (arcade.Playing)
        {
            arcade.Playing = false;
            SendScreen(ent, RobustOfficerScreen.Pause);
        }

        if (arcade.Spectators.Count == 0)
            return;

        arcade.Player = arcade.Spectators[0];
        arcade.Spectators.RemoveAt(0);
        SendUserStatus(ent, arcade.Player.Value);
    }

    private void OnPowerChanged(Entity<RobustOfficerArcadeComponent> ent, ref PowerChangedEvent args)
    {
        if (args.Powered)
            return;

        _ui.CloseUi(ent.Owner, RobustOfficerUiKey.Key);
        ent.Comp.Player = null;
        ent.Comp.Spectators.Clear();
        ent.Comp.Playing = false;
        ClearInput(ent.Comp);
    }

    private void OnBuy(Entity<RobustOfficerArcadeComponent> ent, ref RobustOfficerBuyMessage msg)
    {
        var arcade = ent.Comp;
        if (msg.Actor != arcade.Player || !arcade.InShop || arcade.Run is not { } run)
            return;

        if (!run.TryBuy(msg.Upgrade))
            return;

        _audio.PlayPvs(msg.Upgrade == RobustOfficerUpgrade.Promotion ? arcade.PromotionSound : arcade.BuySound, ent);
        SendShop(ent);
    }

    private void OnAction(Entity<RobustOfficerArcadeComponent> ent, ref RobustOfficerActionMessage msg)
    {
        var arcade = ent.Comp;
        if (msg.Actor != arcade.Player)
            return;

        switch (msg.Action)
        {
            case RobustOfficerAction.NewGame:
            case RobustOfficerAction.NewEndlessGame:
                if (TryComp<SpeakOnUIClosedComponent>(ent, out var speak))
                    _speakOnUIClosed.TrySetFlag((ent, speak));
                NewGame(ent, msg.Action == RobustOfficerAction.NewEndlessGame ? RobustOfficerMode.Endless : RobustOfficerMode.Standard);
                break;
            case RobustOfficerAction.StartMoveUp:
                arcade.MoveKeys |= Up;
                break;
            case RobustOfficerAction.EndMoveUp:
                arcade.MoveKeys &= unchecked((byte) ~Up);
                break;
            case RobustOfficerAction.StartMoveDown:
                arcade.MoveKeys |= Down;
                break;
            case RobustOfficerAction.EndMoveDown:
                arcade.MoveKeys &= unchecked((byte) ~Down);
                break;
            case RobustOfficerAction.StartMoveLeft:
                arcade.MoveKeys |= Left;
                break;
            case RobustOfficerAction.EndMoveLeft:
                arcade.MoveKeys &= unchecked((byte) ~Left);
                break;
            case RobustOfficerAction.StartMoveRight:
                arcade.MoveKeys |= Right;
                break;
            case RobustOfficerAction.EndMoveRight:
                arcade.MoveKeys &= unchecked((byte) ~Right);
                break;
            case RobustOfficerAction.StartShootUp:
                PressShoot(arcade, Up);
                break;
            case RobustOfficerAction.EndShootUp:
                arcade.ShootKeys &= unchecked((byte) ~Up);
                break;
            case RobustOfficerAction.StartShootDown:
                PressShoot(arcade, Down);
                break;
            case RobustOfficerAction.EndShootDown:
                arcade.ShootKeys &= unchecked((byte) ~Down);
                break;
            case RobustOfficerAction.StartShootLeft:
                PressShoot(arcade, Left);
                break;
            case RobustOfficerAction.EndShootLeft:
                arcade.ShootKeys &= unchecked((byte) ~Left);
                break;
            case RobustOfficerAction.StartShootRight:
                PressShoot(arcade, Right);
                break;
            case RobustOfficerAction.EndShootRight:
                arcade.ShootKeys &= unchecked((byte) ~Right);
                break;
            case RobustOfficerAction.UseItem:
                if (arcade.Playing)
                    arcade.UseItem = true;
                break;
            case RobustOfficerAction.Pause:
                if (!arcade.Playing)
                    break;
                arcade.Playing = false;
                ClearInput(arcade);
                SendScreen(ent, RobustOfficerScreen.Pause);
                break;
            case RobustOfficerAction.Unpause:
                if (!arcade.Started || arcade.GameOver)
                    break;
                if (arcade.InShop)
                {
                    SendScreen(ent, RobustOfficerScreen.Shop);
                    SendShop(ent);
                    break;
                }

                arcade.Playing = true;
                SendScreen(ent, RobustOfficerScreen.Game);
                break;
            case RobustOfficerAction.Quit:
                if (arcade.Started && !arcade.GameOver)
                    EndGame(ent, RobustOfficerEnding.Quit);
                break;
            case RobustOfficerAction.LeaveShop:
                if (!arcade.InShop)
                    break;
                arcade.InShop = false;
                arcade.Playing = true;
                SendScreen(ent, RobustOfficerScreen.Game);
                StartLevel(ent);
                break;
            case RobustOfficerAction.ShowHighscores:
                if (arcade.Playing)
                {
                    arcade.Playing = false;
                    ClearInput(arcade);
                }
                SendHighscores(ent, msg.Actor);
                SendScreen(ent, RobustOfficerScreen.Highscores, msg.Actor);
                break;
        }
    }

    private static void PressShoot(RobustOfficerArcadeComponent arcade, byte key)
    {
        arcade.ShootKeys |= key;
        arcade.ShootTapped = ToVector(arcade.ShootKeys);
    }

    private static Vector2i ToVector(byte keys)
    {
        var x = ((keys & Right) != 0 ? 1 : 0) - ((keys & Left) != 0 ? 1 : 0);
        var y = ((keys & Down) != 0 ? 1 : 0) - ((keys & Up) != 0 ? 1 : 0);
        return new Vector2i(x, y);
    }

    private static void ClearInput(RobustOfficerArcadeComponent arcade)
    {
        arcade.MoveKeys = 0;
        arcade.ShootKeys = 0;
        arcade.ShootTapped = null;
        arcade.UseItem = false;
    }

    private void SendState(Entity<RobustOfficerArcadeComponent> ent, EntityUid? actor = null, RobustOfficerEvents events = RobustOfficerEvents.None)
    {
        var arcade = ent.Comp;
        if (arcade.Simulation is not { } sim || arcade.Run is not { } run)
            return;

        var boss = sim.BossHealth();
        // Tiles only change when the exit opens: send them with a new room, then only when they change.
        var withTiles = actor != null || arcade.TilesDirty;
        if (actor == null)
            arcade.TilesDirty = false;
        var effects = sim.Effects.OrderBy(e => e.Key).ToList();
        var msg = new RobustOfficerStateMessage
        {
            Width = sim.Width,
            Height = sim.Height,
            Tiles = withTiles ? sim.GetTiles() : [],
            Theme = (byte) sim.Theme,
            Sprites = sim.GetSprites(),
            Events = events,
            TickSeconds = RobustOfficerSimulation.TickSeconds,
            Score = run.Score,
            Lives = run.Lives,
            Credits = run.Credits,
            Armor = sim.Armor,
            MaxArmor = run.MaxArmor,
            Rank = run.Rank,
            Stocked = run.Stocked,
            TimeLeft = sim.IsBossRoom ? -1 : (int) MathF.Ceiling(sim.TimeLeft),
            NukeTimer = sim.NukeTimer is { } nuke ? (int) MathF.Ceiling(nuke) : -1,
            BossHealth = boss?.Health ?? 0,
            BossMaxHealth = boss?.MaxHealth ?? 0,
            Boss = boss?.Boss,
            Effects = effects.Select(e => e.Key).ToArray(),
            EffectSeconds = effects.Select(e => (int) MathF.Ceiling(e.Value)).ToArray(),
            ExitOpen = sim.ExitOpen,
            Level = arcade.LevelIndex + 1,
            LevelName = arcade.LevelName,
        };
        Send(ent, msg, actor);
    }

    private void SendScreen(Entity<RobustOfficerArcadeComponent> ent, RobustOfficerScreen screen, EntityUid? actor = null)
    {
        var arcade = ent.Comp;
        var msg = new RobustOfficerScreenMessage(screen, arcade.Started)
        {
            Mode = arcade.Mode,
            Ending = arcade.Ending,
            FinalScore = arcade.Run?.Score ?? 0,
            RoundPlacement = arcade.RoundPlacement,
            AllTimePlacement = arcade.AllTimePlacement,
        };
        Send(ent, msg, actor);
    }

    private void SendShop(Entity<RobustOfficerArcadeComponent> ent, EntityUid? actor = null)
    {
        if (ent.Comp.Run is not { } run)
            return;

        Send(ent, new RobustOfficerShopMessage
        {
            Credits = run.Credits,
            Lives = run.Lives,
            Rank = run.Rank,
            Stocked = run.Stocked,
            Offers = run.Offers(),
        }, actor);
    }

    /// <summary>
    /// Sends the leaderboards of <paramref name="mode"/>, or of every mode.
    /// </summary>
    private void SendHighscores(Entity<RobustOfficerArcadeComponent> ent, EntityUid? actor = null, RobustOfficerMode? mode = null)
    {
        foreach (var m in Enum.GetValues<RobustOfficerMode>())
        {
            if (mode != null && m != mode)
                continue;
            Send(ent, new RobustOfficerHighscoresMessage(m, new(_leaderboard.Round(m)), new(_leaderboard.AllTime(m))), actor);
        }
    }

    private void SendUserStatus(Entity<RobustOfficerArcadeComponent> ent, EntityUid actor)
    {
        Send(ent, new RobustOfficerUserStatusMessage(ent.Comp.Player == actor), actor);
    }

    private void Send(EntityUid uid, BoundUserInterfaceMessage msg, EntityUid? actor)
    {
        if (actor is { } target)
            _ui.ServerSendUiMessage(uid, RobustOfficerUiKey.Key, msg, target);
        else
            _ui.ServerSendUiMessage(uid, RobustOfficerUiKey.Key, msg);
    }
}
