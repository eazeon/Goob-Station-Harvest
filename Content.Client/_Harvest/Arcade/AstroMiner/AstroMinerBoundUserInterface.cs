using Content.Shared._Harvest.Arcade.AstroMiner;
using Robust.Client.UserInterface;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;

namespace Content.Client._Harvest.Arcade.AstroMiner;

/// <summary>
/// Shows the game and plays its music and small sounds for whoever has the window open.
/// The cabinet itself plays the loud ones (exit opening, death, escape) for everyone around.
/// </summary>
public sealed class AstroMinerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private static readonly SoundSpecifier MusicSound = new SoundPathSpecifier("/Audio/_Harvest/Arcade/AstroMiner/music.ogg");
    private static readonly SoundSpecifier DigSound = new SoundPathSpecifier("/Audio/_Harvest/Arcade/AstroMiner/dig.ogg");
    private static readonly SoundSpecifier CrystalSound = new SoundPathSpecifier("/Audio/_Harvest/Arcade/AstroMiner/crystal.ogg");
    private static readonly SoundSpecifier LandSound = new SoundPathSpecifier("/Audio/_Harvest/Arcade/AstroMiner/land.ogg");
    private static readonly SoundSpecifier ExplosionSound = new SoundPathSpecifier("/Audio/_Harvest/Arcade/AstroMiner/explosion.ogg");

    private static readonly AudioParams MusicParams = AudioParams.Default.WithLoop(true);

    /// <summary>
    /// Volume of the music with the slider at full, in decibels.
    /// </summary>
    private const float MusicVolume = -10f;
    private static readonly AudioParams EffectParams = AudioParams.Default.WithVolume(-6f);

    /// <summary>
    /// Kept for the whole session, so turning the music off sticks between cabinets.
    /// </summary>
    private static bool _musicEnabled = true;

    /// <summary>
    /// The slider's music volume from 0 to 1, kept for the whole session like <see cref="_musicEnabled"/>.
    /// </summary>
    private static float _musicGain = 1f;

    private AstroMinerMenu? _menu;
    private EntityUid? _music;
    private bool _inGame;

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<AstroMinerMenu>();
        _menu.MusicEnabled = _musicEnabled;
        _menu.MusicVolume = _musicGain;
        _menu.OnMusicVolumeChanged += gain =>
        {
            _musicGain = gain;
            EntMan.System<SharedAudioSystem>().SetVolume(_music, MusicDecibels());
        };
        _menu.OnAction += action => SendMessage(new AstroMinerActionMessage(action));
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
            case AstroMinerStateMessage state:
                _menu?.UpdateState(state);
                PlayEffects(state.Events);
                break;
            case AstroMinerScreenMessage screen:
                _menu?.SetScreen(screen.Screen, screen.Started, screen.Mode, screen.FinalScore, screen.RoundPlacement, screen.AllTimePlacement);
                _inGame = screen.Screen == AstroMinerScreen.Game;
                UpdateMusic();
                break;
            case AstroMinerUserStatusMessage status:
                _menu?.SetUsability(status.IsPlayer);
                break;
            case AstroMinerHighscoresMessage scores:
                _menu?.UpdateHighscores(scores.Mode, scores.Round, scores.AllTime);
                break;
        }
    }

    private void PlayEffects(AstroMinerEvents events)
    {
        // One sound per kind at most, the louder events first.
        if (events.HasFlag(AstroMinerEvents.Explosion) && !events.HasFlag(AstroMinerEvents.MinerDied))
            Play(ExplosionSound);
        if (events.HasFlag(AstroMinerEvents.CrystalCollected) || events.HasFlag(AstroMinerEvents.Transformed))
            Play(CrystalSound);
        else if (events.HasFlag(AstroMinerEvents.Dug))
            Play(DigSound);
        if (events.HasFlag(AstroMinerEvents.Landed))
            Play(LandSound);
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
