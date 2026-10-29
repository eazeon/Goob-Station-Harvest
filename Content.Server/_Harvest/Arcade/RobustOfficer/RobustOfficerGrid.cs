using System.Linq;
using Content.Shared._Harvest.Arcade.RobustOfficer;

namespace Content.Server._Harvest.Arcade.RobustOfficer;

/// <summary>
/// Reads room maps and answers what can walk or fly where. Also checks that a room is playable.
/// </summary>
public static class RobustOfficerGrid
{
    public const int Unreachable = int.MaxValue;

    private static readonly Vector2i[] Neighbors =
    [
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    ];

    public static RobustOfficerTile[] Parse(IReadOnlyList<string> map, out int width, out int height, out Vector2i player)
    {
        height = map.Count;
        width = height == 0 ? 0 : map[0].Length;
        player = new Vector2i(width / 2, height / 2);
        var tiles = new RobustOfficerTile[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = x < map[y].Length ? map[y][x] : '#';
                tiles[y * width + x] = c switch
                {
                    '.' => RobustOfficerTile.Floor,
                    'P' => RobustOfficerTile.Floor,
                    'T' => RobustOfficerTile.Table,
                    'C' => RobustOfficerTile.Crate,
                    'v' => RobustOfficerTile.Vent,
                    'D' => RobustOfficerTile.Door,
                    'E' => RobustOfficerTile.ExitClosed,
                    _ => RobustOfficerTile.Wall,
                };
                if (c == 'P')
                    player = new Vector2i(x, y);
            }
        }

        return tiles;
    }

    public static bool BlocksPlayer(RobustOfficerTile tile) => RobustOfficerCollision.BlocksPlayer(tile);

    public static bool BlocksEnemy(RobustOfficerTile tile)
    {
        return tile is RobustOfficerTile.Wall or RobustOfficerTile.Table or RobustOfficerTile.Crate
            or RobustOfficerTile.ExitClosed or RobustOfficerTile.ExitOpen;
    }

    /// <summary>
    /// Revenants float over furniture.
    /// </summary>
    public static bool BlocksGhost(RobustOfficerTile tile)
    {
        return tile is RobustOfficerTile.Wall or RobustOfficerTile.ExitClosed or RobustOfficerTile.ExitOpen;
    }

    /// <summary>
    /// Bullets fly over tables and vents.
    /// </summary>
    public static bool BlocksBullet(RobustOfficerTile tile)
    {
        return tile is RobustOfficerTile.Wall or RobustOfficerTile.Crate or RobustOfficerTile.Door
            or RobustOfficerTile.ExitClosed or RobustOfficerTile.ExitOpen;
    }

    public static bool IsSpawn(RobustOfficerTile tile)
    {
        return tile is RobustOfficerTile.Door or RobustOfficerTile.Vent;
    }

    /// <summary>
    /// Steps from every tile to <paramref name="target"/>, moving like a king on a chessboard
    /// but never cutting a blocked corner. <see cref="Unreachable"/> where it cannot be reached.
    /// </summary>
    public static void DistanceField(int[] field, int width, int height, Vector2i target, Func<int, int, bool> passable)
    {
        Array.Fill(field, Unreachable);
        if (target.X < 0 || target.Y < 0 || target.X >= width || target.Y >= height)
            return;

        var queue = new Queue<Vector2i>();
        field[target.Y * width + target.X] = 0;
        queue.Enqueue(target);
        while (queue.TryDequeue(out var pos))
        {
            var next = field[pos.Y * width + pos.X] + 1;
            foreach (var dir in Neighbors)
            {
                var n = pos + dir;
                if (n.X < 0 || n.Y < 0 || n.X >= width || n.Y >= height || field[n.Y * width + n.X] != Unreachable)
                    continue;
                if (!passable(n.X, n.Y))
                    continue;
                if (dir.X != 0 && dir.Y != 0 && (!passable(pos.X + dir.X, pos.Y) || !passable(pos.X, pos.Y + dir.Y)))
                    continue;

                field[n.Y * width + n.X] = next;
                queue.Enqueue(n);
            }
        }
    }

    /// <summary>
    /// Where a boss appears: the floor tile closest to the top middle of the room with free tiles all around it.
    /// </summary>
    public static Vector2i? BossSpawn(RobustOfficerTile[] tiles, int width, int height)
    {
        Vector2i? best = null;
        var bestDistance = float.MaxValue;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var free = true;
                for (var dy = -1; dy <= 1 && free; dy++)
                {
                    for (var dx = -1; dx <= 1 && free; dx++)
                    {
                        free = !BlocksEnemy(tiles[(y + dy) * width + x + dx]) && tiles[(y + dy) * width + x + dx] != RobustOfficerTile.Door;
                    }
                }

                if (!free)
                    continue;

                var distance = MathF.Abs(x + 0.5f - width / 2f) + MathF.Abs(y - 3.5f);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Vector2i(x, y);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Why a room cannot be played, or null when it can: closed by walls, doors and exits, one player,
    /// an exit the player can reach and spawns from which enemies reach the player.
    /// </summary>
    public static string? Validate(IReadOnlyList<string> map, bool boss)
    {
        if (map.Count < 5 || map[0].Length < 5)
            return "too small";
        if (map.Any(row => row.Length != map[0].Length))
            return "rows have different lengths";

        var text = string.Concat(map);
        if (text.Count(c => c == 'P') != 1)
            return "needs exactly one player";
        if (text.Any(c => !".#TCvDEP".Contains(c)))
            return "unknown tile";

        var tiles = Parse(map, out var width, out var height, out var player);
        var exits = 0;
        var spawns = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var tile = tiles[y * width + x];
                var border = x == 0 || y == 0 || x == width - 1 || y == height - 1;
                if (border && tile is not (RobustOfficerTile.Wall or RobustOfficerTile.Door or RobustOfficerTile.ExitClosed))
                    return $"open edge at {x},{y}";
                if (!border && tile is (RobustOfficerTile.Door or RobustOfficerTile.ExitClosed))
                    return $"door or exit inside the room at {x},{y}";
                if (tile == RobustOfficerTile.ExitClosed)
                    exits++;
                if (IsSpawn(tile))
                    spawns++;
            }
        }

        if (exits == 0)
            return "needs an exit";
        if (spawns < 2)
            return "needs at least two doors or vents";

        // The player walks to the exit once it opens.
        var field = new int[width * height];
        DistanceField(field, width, height, player, (x, y) =>
        {
            var tile = tiles[y * width + x];
            return tile == RobustOfficerTile.ExitClosed || !BlocksPlayer(tile);
        });
        var exitReached = false;
        for (var i = 0; i < tiles.Length; i++)
        {
            if (tiles[i] == RobustOfficerTile.ExitClosed && field[i] != Unreachable)
                exitReached = true;
        }

        if (!exitReached)
            return "the exit cannot be reached";

        DistanceField(field, width, height, player, (x, y) => !BlocksEnemy(tiles[y * width + x]));
        for (var i = 0; i < tiles.Length; i++)
        {
            if (IsSpawn(tiles[i]) && field[i] == Unreachable)
                return $"enemies from {i % width},{i / width} cannot reach the player";
        }

        if (boss && BossSpawn(tiles, width, height) == null)
            return "no room for the boss";

        return null;
    }
}
