using System.Numerics;
using System.Text;
using Content.Shared._Harvest.Arcade;
using Content.Shared._Harvest.Arcade.RobustOfficer;
using Content.Shared.Input;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Input;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client._Harvest.Arcade.RobustOfficer;

public sealed class RobustOfficerMenu : DefaultWindow
{
    private readonly RobustOfficerField _field;
    private readonly Label _levelLabel;
    private readonly Label _timerLabel;
    private readonly Label _scoreLabel;
    private readonly Label _creditsLabel;
    private readonly Label _livesLabel;
    private readonly Label _rankLabel;
    private readonly Label _armorLabel;
    private readonly TextureRect _itemIcon;
    private readonly Label _itemLabel;
    private readonly Label _effectsLabel;
    private readonly Button _pauseButton;
    private readonly Button _musicButton;
    private readonly Slider _musicVolume;

    private readonly PanelContainer _overlay;
    private readonly Label _overlayTitle;
    private readonly Label _overlayText;
    private readonly Button _resumeButton;
    private readonly Button _quitButton;
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

    private readonly BoxContainer _shopBox;
    private readonly Label _shopStatusLabel;
    private readonly BoxContainer _shopRows;
    private readonly Button _leaveShopButton;

    private bool _isPlayer;
    private bool _started;
    private RobustOfficerScreen _screen = RobustOfficerScreen.Title;
    private RobustOfficerScreen _screenBeforeScores = RobustOfficerScreen.Title;
    private (RobustOfficerMode Mode, int FinalScore, int? RoundPlacement, int? AllTimePlacement, RobustOfficerEnding Ending) _lastScreenInfo;
    private RobustOfficerMode _scoresMode = RobustOfficerMode.Standard;
    private readonly Dictionary<RobustOfficerMode, (List<ArcadeScoreEntry> Round, List<ArcadeScoreEntry> AllTime)> _scores = new();
    private RobustOfficerShopMessage? _shop;

    public event Action<RobustOfficerAction>? OnAction;
    public event Action<RobustOfficerUpgrade>? OnBuy;
    public event Action<bool>? OnMusicToggled;

    /// <summary>
    /// Raised with the music volume picked on the slider, from 0 to 1.
    /// </summary>
    public event Action<float>? OnMusicVolumeChanged;

    public RobustOfficerMenu()
    {
        Title = Loc.GetString("robust-officer-menu-title");
        MinSize = SetSize = new Vector2(800, 640);
        CanKeyboardFocus = true;

        var root = new BoxContainer { Orientation = LayoutOrientation.Vertical };

        var header = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 16 };
        _levelLabel = new Label { HorizontalExpand = true, ClipText = true };
        _timerLabel = new Label();
        _scoreLabel = new Label();
        _creditsLabel = new Label();
        _livesLabel = new Label();
        header.AddChild(_levelLabel);
        header.AddChild(_timerLabel);
        header.AddChild(_scoreLabel);
        header.AddChild(_creditsLabel);
        header.AddChild(_livesLabel);
        root.AddChild(header);

        var status = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 16 };
        _rankLabel = new Label { FontColorOverride = new Color(220, 60, 60) };
        _armorLabel = new Label();
        var item = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 4 };
        _itemIcon = new TextureRect { Stretch = TextureRect.StretchMode.Scale, SetSize = new Vector2(16, 16), VerticalAlignment = VAlignment.Center };
        _itemLabel = new Label();
        item.AddChild(_itemIcon);
        item.AddChild(_itemLabel);
        _effectsLabel = new Label { HorizontalExpand = true, ClipText = true, FontColorOverride = new Color(120, 200, 255) };
        status.AddChild(_rankLabel);
        status.AddChild(_armorLabel);
        status.AddChild(item);
        status.AddChild(_effectsLabel);
        root.AddChild(status);

        var stack = new Control { VerticalExpand = true, HorizontalExpand = true, Margin = new Thickness(0, 4) };
        _field = new RobustOfficerField { VerticalExpand = true, HorizontalExpand = true };
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
        _standardScoresButton = new Button { Text = Loc.GetString("robust-officer-menu-mode-standard"), Group = modeGroup, Pressed = true };
        _endlessScoresButton = new Button { Text = Loc.GetString("robust-officer-menu-mode-endless"), Group = modeGroup };
        _standardScoresButton.OnPressed += _ => ShowScores(RobustOfficerMode.Standard);
        _endlessScoresButton.OnPressed += _ => ShowScores(RobustOfficerMode.Endless);
        _scoresModeBox.AddChild(_standardScoresButton);
        _scoresModeBox.AddChild(_endlessScoresButton);
        overlayBox.AddChild(_scoresModeBox);

        _scoresBox = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 24 };
        _roundScoresLabel = new Label();
        _allTimeScoresLabel = new Label();
        _scoresBox.AddChild(_roundScoresLabel);
        _scoresBox.AddChild(_allTimeScoresLabel);
        overlayBox.AddChild(_scoresBox);

        _shopBox = new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 6 };
        _shopStatusLabel = new Label { Align = Label.AlignMode.Center };
        // Rows rather than a grid: the grid measured its rows shorter than it laid them out,
        // so the last offer ended up under the leave button.
        _shopRows = new BoxContainer { Orientation = LayoutOrientation.Vertical, SeparationOverride = 4 };
        _leaveShopButton = MakeButton("robust-officer-menu-button-leave-shop", RobustOfficerAction.LeaveShop);
        _shopBox.AddChild(_shopStatusLabel);
        _shopBox.AddChild(_shopRows);
        _shopBox.AddChild(_leaveShopButton);
        overlayBox.AddChild(_shopBox);

        _resumeButton = MakeButton("robust-officer-menu-button-resume", RobustOfficerAction.Unpause);
        _newGameButton = MakeButton("robust-officer-menu-button-new-standard", RobustOfficerAction.NewGame);
        _newEndlessGameButton = MakeButton("robust-officer-menu-button-new-endless", RobustOfficerAction.NewEndlessGame);
        _scoresButton = MakeButton("robust-officer-menu-button-scores", RobustOfficerAction.ShowHighscores);
        _quitButton = MakeButton("robust-officer-menu-button-quit", RobustOfficerAction.Quit);
        _backButton = new Button { Text = Loc.GetString("robust-officer-menu-button-back") };
        _backButton.OnPressed += _ => SetScreen(_screenBeforeScores, _started, _lastScreenInfo.Mode,
            _lastScreenInfo.FinalScore, _lastScreenInfo.RoundPlacement, _lastScreenInfo.AllTimePlacement, _lastScreenInfo.Ending);
        overlayBox.AddChild(_resumeButton);
        overlayBox.AddChild(_quitButton);
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
                BackgroundColor = new Color(12, 10, 18, 235),
                BorderColor = new Color(220, 50, 50),
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
            Text = Loc.GetString("robust-officer-menu-help"),
            ToolTip = Loc.GetString("robust-officer-menu-help"),
            MouseFilter = MouseFilterMode.Stop,
            HorizontalExpand = true,
            ClipText = true,
            FontColorOverride = Color.Gray,
        });
        _musicButton = new Button { ToggleMode = true, Pressed = true, Text = Loc.GetString("robust-officer-menu-button-music") };
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
            ToolTip = Loc.GetString("robust-officer-menu-music-volume"),
            Margin = new Thickness(4, 0, 8, 0),
        };
        _musicVolume.OnValueChanged += range => OnMusicVolumeChanged?.Invoke(range.Value / 100f);
        footer.AddChild(_musicVolume);
        _pauseButton = MakeButton("robust-officer-menu-button-pause", RobustOfficerAction.Pause);
        footer.AddChild(_pauseButton);
        root.AddChild(footer);

        Contents.AddChild(root);
        SetScreen(RobustOfficerScreen.Title, false);
    }

    private Button MakeButton(string text, RobustOfficerAction action)
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
        if (_shop != null)
            UpdateShop(_shop);
    }

    public static string RankName(RobustOfficerRank rank)
    {
        return Loc.GetString($"robust-officer-rank-{RobustOfficerField.StateName(rank.ToString()).Replace('_', '-')}");
    }

    public static string PickupName(RobustOfficerPickup pickup)
    {
        return Loc.GetString($"robust-officer-pickup-{RobustOfficerField.StateName(pickup.ToString()).Replace('_', '-')}");
    }

    public void UpdateState(RobustOfficerStateMessage state)
    {
        _field.SetState(state);
        _levelLabel.Text = Loc.GetString("robust-officer-menu-level", ("level", state.Level), ("name", state.LevelName));
        if (state.NukeTimer >= 0 && !state.ExitOpen)
        {
            _timerLabel.Text = Loc.GetString("robust-officer-menu-nuke", ("seconds", state.NukeTimer));
            _timerLabel.FontColorOverride = Color.OrangeRed;
        }
        else if (state.ExitOpen)
        {
            _timerLabel.Text = Loc.GetString("robust-officer-menu-exit-open");
            _timerLabel.FontColorOverride = Color.LightGreen;
        }
        else if (state.TimeLeft >= 0)
        {
            _timerLabel.Text = Loc.GetString("robust-officer-menu-time", ("seconds", state.TimeLeft));
            _timerLabel.FontColorOverride = null;
        }
        else
        {
            _timerLabel.Text = state.Boss is { } boss
                ? Loc.GetString($"robust-officer-boss-{RobustOfficerField.StateName(boss.ToString()).Replace("_", "")}")
                : string.Empty;
            _timerLabel.FontColorOverride = Color.OrangeRed;
        }

        _scoreLabel.Text = Loc.GetString("robust-officer-menu-score", ("score", state.Score));
        _creditsLabel.Text = Loc.GetString("robust-officer-menu-credits", ("credits", state.Credits));
        _livesLabel.Text = Loc.GetString("robust-officer-menu-lives", ("lives", state.Lives));
        _rankLabel.Text = RankName(state.Rank);
        _armorLabel.Text = Loc.GetString("robust-officer-menu-armor", ("armor", state.Armor), ("max", state.MaxArmor));

        _itemIcon.Texture = state.Stocked is { } stocked ? _field.Icon(stocked) : null;
        _itemLabel.Text = state.Stocked is { } item
            ? Loc.GetString("robust-officer-menu-item", ("item", PickupName(item)))
            : Loc.GetString("robust-officer-menu-no-item");

        var effects = new StringBuilder();
        for (var i = 0; i < state.Effects.Length && i < state.EffectSeconds.Length; i++)
        {
            if (effects.Length > 0)
                effects.Append("  ");
            effects.Append(Loc.GetString("robust-officer-menu-effect", ("effect", PickupName(state.Effects[i])), ("seconds", state.EffectSeconds[i])));
        }

        _effectsLabel.Text = effects.ToString();
    }

    public void UpdateShop(RobustOfficerShopMessage shop)
    {
        _shop = shop;
        _shopStatusLabel.Text = Loc.GetString("robust-officer-shop-status",
            ("credits", shop.Credits), ("rank", RankName(shop.Rank)), ("lives", shop.Lives));

        _shopRows.RemoveAllChildren();
        foreach (var offer in shop.Offers)
        {
            var id = RobustOfficerField.StateName(offer.Upgrade.ToString()).Replace('_', '-');
            var name = offer.Upgrade != RobustOfficerUpgrade.Promotion
                ? Loc.GetString($"robust-officer-upgrade-{id}")
                : offer.Price != null
                    ? Loc.GetString("robust-officer-upgrade-promotion", ("rank", RankName((RobustOfficerRank) (offer.Level + 1))))
                    : Loc.GetString("robust-officer-upgrade-promotion-max");
            var row = new BoxContainer { Orientation = LayoutOrientation.Horizontal, SeparationOverride = 12 };
            row.AddChild(new Label
            {
                Text = name,
                ToolTip = Loc.GetString($"robust-officer-upgrade-{id}-desc"),
                MouseFilter = MouseFilterMode.Stop,
                HorizontalExpand = true,
                MinWidth = 240,
                VerticalAlignment = VAlignment.Center,
            });

            var level = offer.Upgrade is RobustOfficerUpgrade.FireRate or RobustOfficerUpgrade.Damage or RobustOfficerUpgrade.Boots
                ? Loc.GetString("robust-officer-shop-level", ("level", offer.Level), ("max", offer.MaxLevel))
                : string.Empty;
            row.AddChild(new Label { Text = level, FontColorOverride = Color.Gray, MinWidth = 32, VerticalAlignment = VAlignment.Center });

            var button = new Button
            {
                Text = offer.Price is { } price
                    ? Loc.GetString("robust-officer-shop-buy", ("price", price))
                    : Loc.GetString("robust-officer-shop-unavailable"),
                Disabled = !_isPlayer || offer.Price == null || offer.Price > shop.Credits,
                MinWidth = 150,
            };
            var upgrade = offer.Upgrade;
            button.OnPressed += _ => OnBuy?.Invoke(upgrade);
            row.AddChild(button);
            _shopRows.AddChild(row);
        }
    }

    public void SetScreen(RobustOfficerScreen screen, bool started, RobustOfficerMode mode = RobustOfficerMode.Standard,
        int finalScore = 0, int? roundPlacement = null, int? allTimePlacement = null, RobustOfficerEnding ending = RobustOfficerEnding.Defeat)
    {
        _started = started;
        if (started)
            ShowScores(mode);
        if (screen != RobustOfficerScreen.Highscores)
            _lastScreenInfo = (mode, finalScore, roundPlacement, allTimePlacement, ending);
        if (screen == RobustOfficerScreen.Highscores && _screen != RobustOfficerScreen.Highscores)
            _screenBeforeScores = _screen == RobustOfficerScreen.Game ? RobustOfficerScreen.Pause : _screen;
        _screen = screen;

        _overlay.Visible = screen != RobustOfficerScreen.Game;
        _scoresBox.Visible = screen == RobustOfficerScreen.Highscores;
        _scoresModeBox.Visible = _scoresBox.Visible;
        _shopBox.Visible = screen == RobustOfficerScreen.Shop;

        switch (screen)
        {
            case RobustOfficerScreen.Title:
                _overlayTitle.Text = Loc.GetString("robust-officer-menu-title");
                _overlayText.Text = Loc.GetString("robust-officer-menu-intro");
                break;
            case RobustOfficerScreen.Pause:
                _overlayTitle.Text = Loc.GetString("robust-officer-menu-paused");
                _overlayText.Text = string.Empty;
                break;
            case RobustOfficerScreen.Shop:
                _overlayTitle.Text = Loc.GetString("robust-officer-shop-title");
                _overlayText.Text = Loc.GetString("robust-officer-shop-intro");
                break;
            case RobustOfficerScreen.GameOver:
                _overlayTitle.Text = Loc.GetString(ending switch
                {
                    RobustOfficerEnding.Victory => "robust-officer-menu-victory",
                    RobustOfficerEnding.Quit => "robust-officer-menu-quit",
                    _ => "robust-officer-menu-game-over",
                });
                var text = new StringBuilder(Loc.GetString(mode == RobustOfficerMode.Endless ? "robust-officer-menu-mode-endless" : "robust-officer-menu-mode-standard"));
                if (ending == RobustOfficerEnding.Victory)
                    text.Append('\n').Append(Loc.GetString("robust-officer-menu-victory-text"));
                text.Append('\n').Append(Loc.GetString("robust-officer-menu-final-score", ("score", finalScore)));
                if (roundPlacement != null)
                    text.Append('\n').Append(Loc.GetString("robust-officer-menu-round-placement", ("place", roundPlacement)));
                if (allTimePlacement != null)
                    text.Append('\n').Append(Loc.GetString("robust-officer-menu-all-time-placement", ("place", allTimePlacement)));
                _overlayText.Text = text.ToString();
                break;
            case RobustOfficerScreen.Highscores:
                _overlayTitle.Text = Loc.GetString("robust-officer-menu-scores");
                _overlayText.Text = string.Empty;
                break;
            case RobustOfficerScreen.Game:
                GrabKeyboardFocus();
                break;
        }

        UpdateButtons();
    }

    public void UpdateHighscores(RobustOfficerMode mode, List<ArcadeScoreEntry> round, List<ArcadeScoreEntry> allTime)
    {
        _scores[mode] = (round, allTime);
        if (mode == _scoresMode)
            ShowScores(mode);
    }

    private void ShowScores(RobustOfficerMode mode)
    {
        _scoresMode = mode;
        _standardScoresButton.Pressed = mode == RobustOfficerMode.Standard;
        _endlessScoresButton.Pressed = mode == RobustOfficerMode.Endless;

        var (round, allTime) = _scores.GetValueOrDefault(mode, ([], []));
        _roundScoresLabel.Text = FormatScores("robust-officer-menu-round-scores", round);
        _allTimeScoresLabel.Text = FormatScores("robust-officer-menu-all-time-scores", allTime);
    }

    private static string FormatScores(string title, List<ArcadeScoreEntry> entries)
    {
        var text = new StringBuilder(Loc.GetString(title));
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            text.Append('\n').Append(Loc.GetString("robust-officer-menu-score-entry",
                ("place", i + 1), ("name", entry.Name), ("score", entry.Score), ("level", entry.Level)));
        }

        if (entries.Count == 0)
            text.Append('\n').Append(Loc.GetString("robust-officer-menu-no-scores"));
        return text.ToString();
    }

    private void UpdateButtons()
    {
        _pauseButton.Disabled = !_isPlayer || _screen != RobustOfficerScreen.Game;
        _resumeButton.Visible = _screen == RobustOfficerScreen.Pause;
        _quitButton.Visible = _resumeButton.Visible;
        _newGameButton.Visible = _screen is RobustOfficerScreen.Title or RobustOfficerScreen.Pause or RobustOfficerScreen.GameOver;
        _newEndlessGameButton.Visible = _newGameButton.Visible;
        _scoresButton.Visible = _newGameButton.Visible;
        _backButton.Visible = _screen == RobustOfficerScreen.Highscores;
        _resumeButton.Disabled = _quitButton.Disabled = _newGameButton.Disabled = _newEndlessGameButton.Disabled = _scoresButton.Disabled
            = _leaveShopButton.Disabled = !_isPlayer;
    }

    protected override void KeyboardFocusExited()
    {
        if (!IsOpen || _screen != RobustOfficerScreen.Game || !_isPlayer)
            return;
        OnAction?.Invoke(RobustOfficerAction.Pause);
    }

    /// <summary>
    /// The move keybinds (WASD by default) move, the arcade arrows shoot, the arcade button uses the stocked item.
    /// </summary>
    private static RobustOfficerAction? KeyAction(BoundKeyFunction function, bool down)
    {
        if (function == EngineKeyFunctions.MoveUp)
            return down ? RobustOfficerAction.StartMoveUp : RobustOfficerAction.EndMoveUp;
        if (function == EngineKeyFunctions.MoveDown)
            return down ? RobustOfficerAction.StartMoveDown : RobustOfficerAction.EndMoveDown;
        if (function == EngineKeyFunctions.MoveLeft)
            return down ? RobustOfficerAction.StartMoveLeft : RobustOfficerAction.EndMoveLeft;
        if (function == EngineKeyFunctions.MoveRight)
            return down ? RobustOfficerAction.StartMoveRight : RobustOfficerAction.EndMoveRight;
        if (function == ContentKeyFunctions.ArcadeUp)
            return down ? RobustOfficerAction.StartShootUp : RobustOfficerAction.EndShootUp;
        if (function == ContentKeyFunctions.ArcadeDown)
            return down ? RobustOfficerAction.StartShootDown : RobustOfficerAction.EndShootDown;
        if (function == ContentKeyFunctions.ArcadeLeft)
            return down ? RobustOfficerAction.StartShootLeft : RobustOfficerAction.EndShootLeft;
        if (function == ContentKeyFunctions.ArcadeRight)
            return down ? RobustOfficerAction.StartShootRight : RobustOfficerAction.EndShootRight;
        if (function == ContentKeyFunctions.Arcade1 && down)
            return RobustOfficerAction.UseItem;
        return null;
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (!_isPlayer || args.Handled || KeyAction(args.Function, true) is not { } action)
            return;

        OnAction?.Invoke(action);
        args.Handle();
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        base.KeyBindUp(args);
        if (!_isPlayer || args.Handled || KeyAction(args.Function, false) is not { } action)
            return;

        OnAction?.Invoke(action);
    }
}
