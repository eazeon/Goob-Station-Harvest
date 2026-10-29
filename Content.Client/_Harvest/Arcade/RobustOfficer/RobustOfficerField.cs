using System.Numerics;
using Content.Shared._Harvest.Arcade.RobustOfficer;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._Harvest.Arcade.RobustOfficer;

/// <summary>
/// Draws the Robust Officer room, sliding everything from its previous position between ticks.
/// </summary>
public sealed class RobustOfficerField : Control
{
    private static readonly ResPath RsiPath = new("/Textures/_Harvest/Arcade/robustofficer.rsi");
    private static readonly ResPath BigRsiPath = new("/Textures/_Harvest/Arcade/robustofficer_big.rsi");
    private static readonly RsiDirection[] Directions = [RsiDirection.South, RsiDirection.North, RsiDirection.East, RsiDirection.West];

    private const int SpriteSize = 16;
    public const int Themes = 5;

    private readonly Dictionary<RobustOfficerSpriteKind, (Texture[][] Frames, float Delay, bool Big)> _sprites = new();
    private readonly Texture[] _floors = new Texture[Themes];
    private readonly Dictionary<RobustOfficerTile, Texture> _tiles = new();
    private readonly Dictionary<RobustOfficerPickup, Texture> _icons = new();

    private int _width;
    private int _height;
    private byte[] _tileBytes = [];
    private int _theme;
    private RobustOfficerSprite[] _drawn = [];
    private readonly Dictionary<ushort, Vector2> _previous = new();
    private float _sinceUpdate;
    private float _time;

    /// <summary>
    /// Smoothed seconds between two states as they actually arrive, how long a slide lasts.
    /// </summary>
    private float _interval = 1f / 30f;

    private int _bossHealth;
    private int _bossMaxHealth;
    private int _nukeTimer = -1;

    public RobustOfficerField()
    {
        RectClipContent = true;
        MinSize = new Vector2(320, 240);

        var sprites = IoCManager.Resolve<IEntityManager>().System<SpriteSystem>();
        foreach (var kind in Enum.GetValues<RobustOfficerSpriteKind>())
        {
            var big = kind is >= RobustOfficerSpriteKind.AssistantKing and <= RobustOfficerSpriteKind.NukieCommander;
            var state = sprites.GetState(new SpriteSpecifier.Rsi(big ? BigRsiPath : RsiPath, StateName(kind.ToString())));
            var frames = new Texture[4][];
            for (var dir = 0; dir < 4; dir++)
            {
                frames[dir] = state.RsiDirections == RsiDirectionType.Dir4 ? state.GetFrames(Directions[dir]) : state.GetFrames(RsiDirection.South);
            }

            _sprites[kind] = (frames, frames[0].Length > 1 ? state.GetDelay(0) : 0f, big);
        }

        for (var theme = 0; theme < Themes; theme++)
        {
            _floors[theme] = Texture(sprites, $"floor_{theme}");
        }

        foreach (var tile in Enum.GetValues<RobustOfficerTile>())
        {
            if (tile != RobustOfficerTile.Floor)
                _tiles[tile] = Texture(sprites, StateName(tile.ToString()));
        }

        foreach (var pickup in Enum.GetValues<RobustOfficerPickup>())
        {
            _icons[pickup] = Texture(sprites, "pickup_" + StateName(pickup.ToString()));
        }
    }

    private static Texture Texture(SpriteSystem sprites, string state)
    {
        return sprites.GetState(new SpriteSpecifier.Rsi(RsiPath, state)).Frame0;
    }

    /// <summary>
    /// PlayerHeadOfSecurity becomes player_head_of_security.
    /// </summary>
    public static string StateName(string name)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    public Texture Icon(RobustOfficerPickup pickup) => _icons[pickup];

    public void SetState(RobustOfficerStateMessage state)
    {
        // Positions when this message arrived, so everything slides from where it was drawn.
        _previous.Clear();
        var progress = SlideProgress;
        foreach (var sprite in _drawn)
        {
            _previous[sprite.Id] = Position(sprite, progress);
        }

        // States come once per server tick, but not at a perfectly even pace: slide over the measured interval.
        if (_sinceUpdate > 0)
            _interval = Math.Clamp(_interval + (_sinceUpdate - _interval) * 0.2f, 0.015f, 0.15f);

        _width = state.Width;
        _height = state.Height;
        if (state.Tiles.Length > 0)
            _tileBytes = state.Tiles;
        _theme = Math.Clamp((int) state.Theme, 0, Themes - 1);
        _drawn = state.Sprites;

        _bossHealth = state.BossHealth;
        _bossMaxHealth = state.BossMaxHealth;
        _nukeTimer = state.NukeTimer;
        _sinceUpdate = 0;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        _time += args.DeltaSeconds;
        _sinceUpdate += args.DeltaSeconds;
    }

    private float SlideProgress => Math.Clamp(_sinceUpdate / _interval, 0f, 1f);

    private Vector2 Position(RobustOfficerSprite sprite, float progress)
    {
        var current = new Vector2(sprite.X, sprite.Y) / 16f;
        if (!_previous.TryGetValue(sprite.Id, out var previous) || (previous - current).Length() > 2f)
            return current;
        return previous + (current - previous) * progress;
    }

    /// <summary>
    /// The largest multiple of the sprite size that fits, or the exact fit when even twice the sprite size does not.
    /// </summary>
    private float TileSize()
    {
        if (_width == 0 || _height == 0)
            return SpriteSize;

        var fit = MathF.Min(PixelWidth / (float) _width, PixelHeight / (float) _height);
        return fit >= SpriteSize * 2 ? MathF.Floor(fit / SpriteSize) * SpriteSize : MathF.Max(1f, MathF.Floor(fit));
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        handle.DrawRect(new UIBox2(0, 0, PixelWidth, PixelHeight), Color.Black);
        if (_width == 0 || _height == 0 || _tileBytes.Length < _width * _height)
            return;

        var size = TileSize();
        var origin = new Vector2(MathF.Floor((PixelWidth - size * _width) / 2f), MathF.Floor((PixelHeight - size * _height) / 2f));

        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var box = UIBox2.FromDimensions(origin + new Vector2(x, y) * size, new Vector2(size, size));
                var tile = (RobustOfficerTile) _tileBytes[y * _width + x];
                if (tile != RobustOfficerTile.Wall)
                    handle.DrawTextureRect(_floors[_theme], box);
                if (_tiles.TryGetValue(tile, out var texture))
                    handle.DrawTextureRect(texture, box);
            }
        }

        var progress = SlideProgress;
        foreach (var sprite in _drawn)
        {
            if (!_sprites.TryGetValue(sprite.Kind, out var data))
                continue;
            if (sprite.Flags.HasFlag(RobustOfficerSpriteFlags.Blink) && (int) (_time * 10) % 2 == 0)
                continue;

            var frames = data.Frames[Math.Min((int) sprite.Direction, 3)];
            var texture = data.Delay > 0 ? frames[(int) (_time / data.Delay) % frames.Length] : frames[0];
            var drawSize = data.Big ? size * 2 : size;
            var center = origin + Position(sprite, progress) * size;
            var box = UIBox2.FromDimensions(Round(center - new Vector2(drawSize, drawSize) / 2f), new Vector2(drawSize, drawSize));

            var color = Color.White;
            if (sprite.Flags.HasFlag(RobustOfficerSpriteFlags.Hurt))
                color = new Color(255, 120, 120);
            else if (sprite.Flags.HasFlag(RobustOfficerSpriteFlags.Stunned))
                color = new Color(150, 170, 255);
            else if (sprite.Flags.HasFlag(RobustOfficerSpriteFlags.Telegraph) && (int) (_time * 12) % 2 == 0)
                color = new Color(255, 190, 90);
            if (sprite.Flags.HasFlag(RobustOfficerSpriteFlags.Phasing))
                color = color.WithAlpha(0.35f);

            handle.DrawTextureRect(texture, box, color);
        }

        // Boss health along the top of the room.
        if (_bossMaxHealth > 0)
        {
            var width = size * _width * 0.6f;
            var bar = UIBox2.FromDimensions(origin + new Vector2((size * _width - width) / 2f, size * 0.3f), new Vector2(width, size * 0.35f));
            handle.DrawRect(bar, new Color(20, 0, 0, 200));
            handle.DrawRect(new UIBox2(bar.Left, bar.Top, bar.Left + bar.Width * _bossHealth / _bossMaxHealth, bar.Bottom), new Color(220, 40, 40));
            handle.DrawRect(bar, Color.White, false);
        }

        // The nuke's countdown flashes in the last seconds.
        if (_nukeTimer >= 0 && _nukeTimer <= 10 && (int) (_time * 4) % 2 == 0)
            handle.DrawRect(new UIBox2(0, 0, PixelWidth, PixelHeight), new Color(255, 0, 0, 40));
    }

    private static Vector2 Round(Vector2 v) => new(MathF.Round(v.X), MathF.Round(v.Y));
}
