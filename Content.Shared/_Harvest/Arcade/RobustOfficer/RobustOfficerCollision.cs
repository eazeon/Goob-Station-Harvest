using System.Numerics;

namespace Content.Shared._Harvest.Arcade.RobustOfficer;

/// <summary>
/// Movement of square bodies against the room's tiles.
/// </summary>
public static class RobustOfficerCollision
{
    public const float PlayerRadius = 0.3f;

    public static bool BlocksPlayer(RobustOfficerTile tile)
    {
        return tile is RobustOfficerTile.Wall or RobustOfficerTile.Table or RobustOfficerTile.Crate
            or RobustOfficerTile.Door or RobustOfficerTile.ExitClosed;
    }

    /// <summary>
    /// Moves a square body of half size <paramref name="radius"/> one axis at a time, stopping it against blocked tiles.
    /// </summary>
    /// <returns>Whether it was stopped.</returns>
    public static bool MoveBody(ref Vector2 position, Vector2 delta, float radius, Func<int, int, bool> blocked)
    {
        var hit = false;
        if (delta.X != 0)
        {
            position.X += delta.X;
            if (Overlaps(position, radius, blocked))
            {
                hit = true;
                position.X = delta.X > 0
                    ? MathF.Floor(position.X + radius) - radius - 0.001f
                    : MathF.Floor(position.X - radius) + 1 + radius + 0.001f;
            }
        }

        if (delta.Y != 0)
        {
            position.Y += delta.Y;
            if (Overlaps(position, radius, blocked))
            {
                hit = true;
                position.Y = delta.Y > 0
                    ? MathF.Floor(position.Y + radius) - radius - 0.001f
                    : MathF.Floor(position.Y - radius) + 1 + radius + 0.001f;
            }
        }

        return hit;
    }

    public static bool Overlaps(Vector2 position, float radius, Func<int, int, bool> blocked)
    {
        var minX = (int) MathF.Floor(position.X - radius);
        var maxX = (int) MathF.Floor(position.X + radius - 0.0001f);
        var minY = (int) MathF.Floor(position.Y - radius);
        var maxY = (int) MathF.Floor(position.Y + radius - 0.0001f);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (blocked(x, y))
                    return true;
            }
        }

        return false;
    }
}
