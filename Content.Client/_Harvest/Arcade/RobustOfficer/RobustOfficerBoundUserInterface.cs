using Content.Shared._Harvest.Arcade.RobustOfficer;
using Robust.Client.UserInterface;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client._Harvest.Arcade.RobustOfficer;

/// <summary>
/// Shows the game and plays its music and small sounds for whoever has the window open.
/// The cabinet itself plays the loud ones (exit opening, death, boss beaten, the nuke) for everyone around.
/// </summary>
public sealed class RobustOfficerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [Dependency] private readonly IGameTiming _timing = default!;

    private const string AudioPath = "/Audio/_Harvest/Arcade/RobustOfficer/";
    private static readonly SoundSpecifier MusicSound = new SoundPathSpecifier(AudioPath + "music.ogg");
    private static readonly SoundSpecifier ShootSound = new SoundPathSpecifier(AudioPath + "shoot.ogg");
    private static readonly SoundSpecifier HitSound = new SoundPathSpecifier(AudioPath + "hit.ogg");
    private static readonly SoundSpecifier KillSound = new SoundPathSpecifier(AudioPath + "kill.ogg");
    private static readonly SoundSpecifier PickupSound = new SoundPathSpecifier(AudioPath + "pickup.ogg");
    private static readonly SoundSpecifier CreditSound = new SoundPathSpecifier(AudioPath + "credit.ogg");
    private static readonly SoundSpecifier ArmorSound = new SoundPathSpecifier(AudioPath + "armor.ogg");
    private static readonly SoundSpecifier ItemSound = new SoundPathSpecifier(AudioPath + "item.ogg");
    private static readonly SoundSpecifier EnemyShotSound = new SoundPathSpecifier(AudioPath + "enemy_shot.ogg");

    private static readonly AudioParams MusicParams = AudioParams.Default.WithLoop(true);

    /// <summary>
    /// Volume of the music with the slider at full, in decibels.
    /// </summary>
    private const float MusicVolume = -10f;
    private static readonly AudioParams EffectParams = AudioParams.Default.WithVolume(-8f);

    /// <summary>
    /// Kept for the whole session, so turning the music off sticks between cabinets.
    /// </summary>
    private static bool _musicEnabled = true;

    /// <summary>
    /// The slider's music volume from 0 to 1, kept for the whole session like <see cref="_musicEnabled"/>.
    /// </summary>
    private static float _musicGain = 1f;

    /// <summary>
    /// Shots come up to 20 times a second; this keeps them from turning into a buzz.
    /// </summary>
    private static readonly TimeSpan ShotSoundInterval = TimeSpan.FromSeconds(0.09);

    private RobustOfficerMenu? _menu;
    private EntityUid? _music;
    private bool _inGame;
    private TimeSpan _lastShotSound;

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<RobustOfficerMenu>();
        _menu.MusicEnabled = _musicEnabled;
        _menu.MusicVolume = _musicGain;
        _menu.OnMusicVolumeChanged += gain =>
        {
            _musicGain = gain;
            EntMan.System<SharedAudioSystem>().SetVolume(_music, MusicDecibels());
        };
        _menu.OnAction += action => SendMessage(new RobustOfficerActionMessage(action));
        _menu.OnBuy += upgrade => SendMessage(new RobustOfficerBuyMessage(upgrade));
        _menu.OnMusicToggled += enabled =>
        {
            _musicEnabled = enabled;
            UpdateMusic();
        };
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        switch (message)
        {
            case RobustOfficerStateMessage state:
                _menu?.UpdateState(state);
                PlayEffects(state.Events);
                break;
            case RobustOfficerScreenMessage screen:
                _menu?.SetScreen(screen.Screen, screen.Started, screen.Mode, screen.FinalScore, screen.RoundPlacement, screen.AllTimePlacement, screen.Ending);
                _inGame = screen.Screen == RobustOfficerScreen.Game;
                UpdateMusic();
                break;
            case RobustOfficerShopMessage shop:
                _menu?.UpdateShop(shop);
                break;
            case RobustOfficerUserStatusMessage status:
                _menu?.SetUsability(status.IsPlayer);
                break;
            case RobustOfficerHighscoresMessage scores:
                _menu?.UpdateHighscores(scores.Mode, scores.Round, scores.AllTime);
                break;
        }
    }

    private void PlayEffects(RobustOfficerEvents events)
    {
        // One sound per kind at most.
        if (events.HasFlag(RobustOfficerEvents.ItemUsed))
            Play(ItemSound);
        if (events.HasFlag(RobustOfficerEvents.ArmorHit))
            Play(ArmorSound);
        if (events.HasFlag(RobustOfficerEvents.EnemyKilled))
            Play(KillSound);
        else if (events.HasFlag(RobustOfficerEvents.EnemyHit))
            Play(HitSound);
        if (events.HasFlag(RobustOfficerEvents.Pickup))
            Play(PickupSound);
        else if (events.HasFlag(RobustOfficerEvents.Credit))
            Play(CreditSound);
        if (events.HasFlag(RobustOfficerEvents.EnemyShot))
            Play(EnemyShotSound);

        if (events.HasFlag(RobustOfficerEvents.Shot) && _timing.RealTime - _lastShotSound >= ShotSoundInterval)
        {
            _lastShotSound = _timing.RealTime;
            Play(ShootSound);
        }
    }

    private void Play(SoundSpecifier sound)
    {
        EntMan.System<SharedAudioSystem>().PlayGlobal(sound, Filter.Local(), false, EffectParams);
    }

    /// <summary>
    /// The slider scales the music's gain; at zero it is simply inaudible.
    /// </summary>
    private static float MusicDecibels()
    {
        return MusicVolume + SharedAudioSystem.GainToVolume(MathF.Max(_musicGain, 0.001f));
    }

    private void UpdateMusic()
    {
        var audio = EntMan.System<SharedAudioSystem>();
        var wanted = _inGame && _musicEnabled;
        if (wanted && _music == null)
            _music = audio.PlayGlobal(MusicSound, Filter.Local(), false, MusicParams.WithVolume(MusicDecibels()))?.Entity;
        else if (!wanted && _music != null)
            _music = audio.Stop(_music);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        _inGame = false;
        UpdateMusic();
    }
}
