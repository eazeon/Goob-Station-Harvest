using System.Numerics;
using System.Text;
using Content.Shared._Harvest.Arcade;
using Content.Shared._Harvest.Arcade.AstroMiner;
using Content.Shared.Input;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Harvest.Arcade.AstroMiner;

public sealed class AstroMinerMenu : DefaultWindow
{
    private readonly AstroMinerField _field;
    private readonly Label _levelLabel;
    private readonly Label _crystalsLabel;
    private readonly Label _oxygenLabel;
    private readonly Label _scoreLabel;
    private readonly Label _livesLabel;
    private readonly Button _pauseButton;
    private readonly Button _musicButton;
    private readonly Slider _musicVolume;

    private readonly PanelContainer _overlay;
    private readonly Label _overlayTitle;
    private readonly Label _overlayText;
    private readonly Button _resumeButton;
    private readonly Button _newGameButton;
    private readonly Button _newEndlessGameButton;
    private readonly BoxContainer _scoresModeBox;
    private readonly Button _standardScoresButton;
    private readonly Button _endlessScoresButton;
    private readonly Button _scoresButton;
    private readonly Button _backButton;
    private readonly BoxContainer _scoresBox;
    private readonly Label _roundScoresLabel;
    private readonly Label _allTimeScoresLabel;

    private bool _isPlayer;
    private bool _started;
    private AstroMinerScreen _screen = AstroMinerScreen.Title;
    private AstroMinerScreen _screenBeforeScores = AstroMinerScreen.Title;
    private (AstroMinerMode Mode, int FinalScore, int? RoundPlacement, int? AllTimePlacement) _lastScreenInfo;
    private AstroMinerMode _scoresMode = AstroMinerMode.Standard;
    private readonly Dictionary<AstroMinerMode, (List<ArcadeScoreEntry> Round, List<ArcadeScoreEntry> AllTime)> _scores = new();

    public event Action<AstroMinerAction>? OnAction;
    public event Action<bool>? OnMusicToggled;

    /// <summary>
    /// Raised with the music volume picked on the slider, from 0 to 1.
    /// </summary>
    public event Action<float>? OnMusicVolumeChanged;

    public AstroMinerMenu()
    {
        Title = Loc.GetString("astrominer-menu-title");
        MinSize = SetSize = new Vector2(700, 500);
        CanKeyboardFocus = true;

        var root = new BoxContainer { Orientation = LayoutOrientation.Vertical };

        var header = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 16 };
        _levelLabel = new Label { HorizontalExpand = true, ClipText = true };
        _crystalsLabel = new Label();
        _oxygenLabel = new Label();
        _scoreLabel = new Label();
        _livesLabel = new Label();
        header.AddChild(_levelLabel);
        header.AddChild(_crystalsLabel);
        header.AddChild(_oxygenLabel);
        header.AddChild(_scoreLabel);
        header.AddChild(_livesLabel);
        root.AddChild(header);

        var stack = new Control { VerticalExpand = true, HorizontalExpand = true, Margin = new Thickness(0, 4) };
        _field = new AstroMinerField { VerticalExpand = true, HorizontalExpand = true };
        stack.AddChild(_field);

        var overlayBox = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 8,
            Margin = new Thickness(16),
        };
        _overlayTitle = new Label { Align = Label.AlignMode.Center, StyleClasses = { "LabelHeading" } };
        _overlayText = new Label { Align = Label.AlignMode.Center };
        overlayBox.AddChild(_overlayTitle);
        overlayBox.AddChild(_overlayText);

        // Which mode's leaderboards are shown.
        _scoresModeBox = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 4, HorizontalAlignment = HAlignment.Center };
        var modeGroup = new ButtonGroup();
        _standardScoresButton = new Button { Text = Loc.GetString("astrominer-menu-mode-standard"), Group = modeGroup, Pressed = true };
        _endlessScoresButton = new Button { Text = Loc.GetString("astrominer-menu-mode-endless"), Group = modeGroup };
        _standardScoresButton.OnPressed += _ => ShowScores(AstroMinerMode.Standard);
        _endlessScoresButton.OnPressed += _ => ShowScores(AstroMinerMode.Endless);
        _scoresModeBox.AddChild(_standardScoresButton);
        _scoresModeBox.AddChild(_endlessScoresButton);
        overlayBox.AddChild(_scoresModeBox);

        _scoresBox = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 24 };
        _roundScoresLabel = new Label();
        _allTimeScoresLabel = new Label();
        _scoresBox.AddChild(_roundScoresLabel);
        _scoresBox.AddChild(_allTimeScoresLabel);
        overlayBox.AddChild(_scoresBox);

        _resumeButton = MakeButton("astrominer-menu-button-resume", AstroMinerAction.Unpause);
        _newGameButton = MakeButton("astrominer-menu-button-new-standard", AstroMinerAction.NewGame);
        _newEndlessGameButton = MakeButton("astrominer-menu-button-new-endless", AstroMinerAction.NewEndlessGame);
        _scoresButton = MakeButton("astrominer-menu-button-scores", AstroMinerAction.ShowHighscores);
        _backButton = new Button { Text = Loc.GetString("astrominer-menu-button-back") };
        _backButton.OnPressed += _ => SetScreen(_screenBeforeScores, _started, _lastScreenInfo.Mode,
            _lastScreenInfo.FinalScore, _lastScreenInfo.RoundPlacement, _lastScreenInfo.AllTimePlacement);
        overlayBox.AddChild(_resumeButton);
        overlayBox.AddChild(_newGameButton);
        overlayBox.AddChild(_newEndlessGameButton);
        overlayBox.AddChild(_scoresButton);
        overlayBox.AddChild(_backButton);

        _overlay = new PanelContainer
        {
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Center,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = new Color(10, 12, 20, 230),
                BorderColor = new Color(80, 200, 220),
                BorderThickness = new Thickness(2),
            },
        };
        _overlay.AddChild(overlayBox);
        stack.AddChild(_overlay);
        root.AddChild(stack);

        var footer = new BoxContainer { Orientation = LayoutOrientation.Horizontal };
        // Clipped so the music controls always fit; the whole text is in the tooltip.
        footer.AddChild(new Label
        {
            Text = Loc.GetString("astrominer-menu-help"),
            ToolTip = Loc.GetString("astrominer-menu-help"),
            MouseFilter = MouseFilterMode.Stop,
            HorizontalExpand = true,
            ClipText = true,
            FontColorOverride = Color.Gray,
        });
        _musicButton = new Button { ToggleMode = true, Pressed = true, Text = Loc.GetString("astrominer-menu-button-music") };
        _musicButton.OnToggled += args => OnMusicToggled?.Invoke(args.Pressed);
        footer.AddChild(_musicButton);
        _musicVolume = new Slider
        {
            MinValue = 0f,
            MaxValue = 100f,
            Value = 100f,
            Rounded = true,
            MinWidth = 100f,
            VerticalAlignment = VAlignment.Center,
            ToolTip = Loc.GetString("astrominer-menu-music-volume"),
            Margin = new Thickness(4, 0, 8, 0),
        };
        _musicVolume.OnValueChanged += range => OnMusicVolumeChanged?.Invoke(range.Value / 100f);
        footer.AddChild(_musicVolume);
        _pauseButton = MakeButton("astrominer-menu-button-pause", AstroMinerAction.Pause);
        footer.AddChild(_pauseButton);
        root.AddChild(footer);

        Contents.AddChild(root);
        SetScreen(AstroMinerScreen.Title, false);
    }

    private Button MakeButton(string text, AstroMinerAction action)
    {
        var button = new Button { Text = Loc.GetString(text) };
        button.OnPressed += _ => OnAction?.Invoke(action);
        return button;
    }

    /// <summary>
    /// The music volume, from 0 to 1.
    /// </summary>
    public float MusicVolume
    {
        get => _musicVolume.Value / 100f;
        set => _musicVolume.SetValueWithoutEvent(value * 100f);
    }

    public bool MusicEnabled
    {
        get => _musicButton.Pressed;
        set => _musicButton.Pressed = value;
    }

    public void SetUsability(bool isPlayer)
    {
        _isPlayer = isPlayer;
        UpdateButtons();
    }

    public void UpdateState(AstroMinerStateMessage state)
    {
        _field.SetState(state);
        _levelLabel.Text = Loc.GetString("astrominer-menu-level", ("level", state.Level), ("name", state.LevelName));
        _crystalsLabel.Text = Loc.GetString("astrominer-menu-crystals", ("crystals", state.Crystals), ("needed", state.CrystalsNeeded));
        _oxygenLabel.Text = Loc.GetString("astrominer-menu-oxygen", ("oxygen", state.Oxygen));
        _oxygenLabel.FontColorOverride = state.Oxygen <= 15 ? Color.OrangeRed : null;
        _scoreLabel.Text = Loc.GetString("astrominer-menu-score", ("score", state.Score));
        _livesLabel.Text = Loc.GetString("astrominer-menu-lives", ("lives", state.Lives));
    }

    public void SetScreen(AstroMinerScreen screen, bool started, AstroMinerMode mode = AstroMinerMode.Standard,
        int finalScore = 0, int? roundPlacement = null, int? allTimePlacement = null)
    {
        _started = started;
        if (started)
            ShowScores(mode);
        if (screen != AstroMinerScreen.Highscores)
            _lastScreenInfo = (mode, finalScore, roundPlacement, allTimePlacement);
        if (screen == AstroMinerScreen.Highscores && _screen != AstroMinerScreen.Highscores)
            _screenBeforeScores = _screen == AstroMinerScreen.Game ? AstroMinerScreen.Pause : _screen;
        _screen = screen;

        _overlay.Visible = screen != AstroMinerScreen.Game;
        _scoresBox.Visible = screen == AstroMinerScreen.Highscores;
        _scoresModeBox.Visible = _scoresBox.Visible;

        switch (screen)
        {
            case AstroMinerScreen.Title:
                _overlayTitle.Text = Loc.GetString("astrominer-menu-title");
                _overlayText.Text = Loc.GetString("astrominer-menu-intro");
                break;
            case AstroMinerScreen.Pause:
                _overlayTitle.Text = Loc.GetString("astrominer-menu-paused");
                _overlayText.Text = string.Empty;
                break;
            case AstroMinerScreen.GameOver:
                _overlayTitle.Text = Loc.GetString("astrominer-menu-game-over");
                var text = new StringBuilder(Loc.GetString(mode == AstroMinerMode.Endless ? "astrominer-menu-mode-endless" : "astrominer-menu-mode-standard"));
                text.Append('\n').Append(Loc.GetString("astrominer-menu-final-score", ("score", finalScore)));
                if (roundPlacement != null)
                    text.Append('\n').Append(Loc.GetString("astrominer-menu-round-placement", ("place", roundPlacement)));
                if (allTimePlacement != null)
                    text.Append('\n').Append(Loc.GetString("astrominer-menu-all-time-placement", ("place", allTimePlacement)));
                _overlayText.Text = text.ToString();
                break;
            case AstroMinerScreen.Highscores:
                _overlayTitle.Text = Loc.GetString("astrominer-menu-scores");
                _overlayText.Text = string.Empty;
                break;
            case AstroMinerScreen.Game:
                GrabKeyboardFocus();
                break;
        }

        UpdateButtons();
    }

    public void UpdateHighscores(AstroMinerMode mode, List<ArcadeScoreEntry> round, List<ArcadeScoreEntry> allTime)
    {
        _scores[mode] = (round, allTime);
        if (mode == _scoresMode)
            ShowScores(mode);
    }

    private void ShowScores(AstroMinerMode mode)
    {
        _scoresMode = mode;
        _standardScoresButton.Pressed = mode == AstroMinerMode.Standard;
        _endlessScoresButton.Pressed = mode == AstroMinerMode.Endless;

        var (round, allTime) = _scores.GetValueOrDefault(mode, ([], []));
        var entryLoc = mode == AstroMinerMode.Endless ? "astrominer-menu-score-entry-endless" : "astrominer-menu-score-entry";
        _roundScoresLabel.Text = FormatScores("astrominer-menu-round-scores", entryLoc, round);
        _allTimeScoresLabel.Text = FormatScores("astrominer-menu-all-time-scores", entryLoc, allTime);
    }

    private static string FormatScores(string title, string entryLoc, List<ArcadeScoreEntry> entries)
    {
        var text = new StringBuilder(Loc.GetString(title));
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            text.Append('\n').Append(Loc.GetString(entryLoc,
                ("place", i + 1), ("name", entry.Name), ("score", entry.Score), ("level", entry.Level)));
        }

        if (entries.Count == 0)
            text.Append('\n').Append(Loc.GetString("astrominer-menu-no-scores"));
        return text.ToString();
    }

    private void UpdateButtons()
    {
        _pauseButton.Disabled = !_isPlayer || _screen != AstroMinerScreen.Game;
        _resumeButton.Visible = _screen == AstroMinerScreen.Pause;
        _newGameButton.Visible = _screen is AstroMinerScreen.Title or AstroMinerScreen.Pause or AstroMinerScreen.GameOver;
        _newEndlessGameButton.Visible = _newGameButton.Visible;
        _scoresButton.Visible = _newGameButton.Visible;
        _backButton.Visible = _screen == AstroMinerScreen.Highscores;
        _resumeButton.Disabled = _newGameButton.Disabled = _newEndlessGameButton.Disabled = _scoresButton.Disabled = !_isPlayer;
    }

    protected override void KeyboardFocusExited()
    {
        if (!IsOpen || _screen != AstroMinerScreen.Game || !_isPlayer)
            return;
        OnAction?.Invoke(AstroMinerAction.Pause);
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (!_isPlayer || args.Handled)
            return;

        if (args.Function == ContentKeyFunctions.ArcadeUp)
            OnAction?.Invoke(AstroMinerAction.StartUp);
        else if (args.Function == ContentKeyFunctions.ArcadeDown)
            OnAction?.Invoke(AstroMinerAction.StartDown);
        else if (args.Function == ContentKeyFunctions.ArcadeLeft)
            OnAction?.Invoke(AstroMinerAction.StartLeft);
        else if (args.Function == ContentKeyFunctions.ArcadeRight)
            OnAction?.Invoke(AstroMinerAction.StartRight);
        else if (args.Function == ContentKeyFunctions.Arcade1)
            OnAction?.Invoke(AstroMinerAction.StartGrab);
        else
            return;

        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (!_isPlayer || args.Handled)
            return;

        if (args.Function == ContentKeyFunctions.ArcadeUp)
            OnAction?.Invoke(AstroMinerAction.EndUp);
        else if (args.Function == ContentKeyFunctions.ArcadeDown)
            OnAction?.Invoke(AstroMinerAction.EndDown);
        else if (args.Function == ContentKeyFunctions.ArcadeLeft)
            OnAction?.Invoke(AstroMinerAction.EndLeft);
        else if (args.Function == ContentKeyFunctions.ArcadeRight)
            OnAction?.Invoke(AstroMinerAction.EndRight);
        else if (args.Function == ContentKeyFunctions.Arcade1)
            OnAction?.Invoke(AstroMinerAction.EndGrab);
    }
}
