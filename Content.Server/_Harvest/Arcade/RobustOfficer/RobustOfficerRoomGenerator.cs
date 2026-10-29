using System.Linq;
using Content.Shared._Harvest.Arcade.RobustOfficer;

namespace Content.Server._Harvest.Arcade.RobustOfficer;

/// <summary>
/// Makes the rooms of the endless mode. Every fourth room is a boss; deeper rooms have more kinds of enemies,
/// tougher ones and more of them.
/// </summary>
public static class RobustOfficerRoomGenerator
{
    public const int Width = 20;
    public const int Height = 15;
    public const int BossEvery = 4;

    /// <summary>
    /// Enemy health added per room, as a fraction of their base health.
    /// </summary>
    public const float HealthPerRoom = 0.2f;

    /// <summary>
    /// Past this room the waves stop getting bigger and faster; only the enemies' health keeps rising.
    /// </summary>
    public const int WaveGrowthEnd = 15;

    /// <summary>
    /// Enemies met from a given depth on.
    /// </summary>
    private static readonly (int Depth, RobustOfficerEnemy Enemy)[] Unlocks =
    [
        (1, RobustOfficerEnemy.Greytider),
        (1, RobustOfficerEnemy.Clown),
        (3, RobustOfficerEnemy.Zombie),
        (3, RobustOfficerEnemy.Slime),
        (6, RobustOfficerEnemy.Xeno),
        (7, RobustOfficerEnemy.Revenant),
        (10, RobustOfficerEnemy.Carp),
        (11, RobustOfficerEnemy.Nukie),
    ];

    public static RobustOfficerBoss? BossAt(int depth)
    {
        if (depth % BossEvery != 0)
            return null;
        return (RobustOfficerBoss) ((depth / BossEvery - 1) % Enum.GetValues<RobustOfficerBoss>().Length);
    }

    public static RobustOfficerRoom Generate(int depth, int seed)
    {
        depth = Math.Max(1, depth);
        var random = new Random(seed);
        var boss = BossAt(depth);
        var theme = (depth - 1) / BossEvery % 5;

        string[]? map = null;
        for (var attempt = 0; attempt < 30 && map == null; attempt++)
        {
            var candidate = Layout(random, depth, boss != null, attempt < 25);
            if (RobustOfficerGrid.Validate(candidate, boss != null) == null)
                map = candidate;
        }

        map ??= Layout(random, depth, boss != null, false);

        var health = 1f + HealthPerRoom * (depth - 1);
        var spawnRate = MathF.Min(1.8f, 1f + 0.03f * (Math.Min(depth, WaveGrowthEnd) - 1));
        var seconds = 50 + Math.Min(depth * 3, 40);
        // The nth boss met has n times its health.
        return new RobustOfficerRoom(map, seconds, boss, Waves(random, depth, seconds, boss != null), theme, health, spawnRate,
            Math.Max(1, depth / BossEvery));
    }

    private static string[] Layout(Random random, int depth, bool boss, bool obstacles)
    {
        var grid = new char[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                grid[y, x] = x == 0 || y == 0 || x == Width - 1 || y == Height - 1 ? '#' : '.';
            }
        }

        // Doors on two to four sides, the exit on another or the same side away from its door.
        var sides = new List<int> { 0, 1, 2, 3 }.OrderBy(_ => random.Next()).ToList();
        var doorSides = boss ? 4 : random.Next(2, 5);
        for (var i = 0; i < doorSides; i++)
        {
            PlaceOnSide(grid, sides[i], random, 'D', 2);
        }

        var exitSide = sides[doorSides % 4];
        if (!PlaceOnSide(grid, exitSide, random, 'E', 1))
        {
            for (var side = 0; side < 4 && !PlaceOnSide(grid, side, random, 'E', 1); side++)
            {
            }
        }

        var player = new Vector2i(Width / 2, Height / 2);
        if (boss)
        {
            foreach (var (x, y) in new[] { (5, 4), (14, 4), (5, 10), (14, 10) })
            {
                grid[y, x] = 'C';
            }
        }
        else if (obstacles)
        {
            var clusters = 2 + random.Next(Math.Min(depth, 5) + 1);
            for (var i = 0; i < clusters; i++)
            {
                var c = random.Next(3) == 0 ? 'C' : 'T';
                var w = random.Next(1, 4);
                var h = random.Next(1, 3);
                var x0 = random.Next(2, Width / 2 - w + 1);
                var y0 = random.Next(2, Height - 2 - h);
                for (var y = y0; y < y0 + h; y++)
                {
                    for (var x = x0; x < x0 + w; x++)
                    {
                        if (Math.Abs(y - player.Y) <= 2
                            && (Math.Abs(x - player.X) <= 2 || Math.Abs(Width - 1 - x - player.X) <= 2))
                            continue;
                        // Mirrored, so rooms look built rather than scattered.
                        grid[y, x] = c;
                        grid[y, Width - 1 - x] = c;
                    }
                }
            }
        }

        if (depth >= 3)
        {
            var vents = boss ? 0 : random.Next(1, 3);
            for (var i = 0; i < vents; i++)
            {
                var x = random.Next(2, Width / 2 - 1);
                var y = random.Next(2, Height - 2);
                if (grid[y, x] == '.' && (Math.Abs(x - player.X) > 3 || Math.Abs(y - player.Y) > 3))
                {
                    grid[y, x] = 'v';
                    grid[y, Width - 1 - x] = 'v';
                }
            }
        }

        grid[player.Y, player.X] = 'P';
        var rows = new string[Height];
        for (var y = 0; y < Height; y++)
        {
            var row = new char[Width];
            for (var x = 0; x < Width; x++)
            {
                row[x] = grid[y, x];
            }

            rows[y] = new string(row);
        }

        return rows;
    }

    /// <summary>
    /// Puts <paramref name="length"/> tiles of <paramref name="c"/> in the wall of a side (0 top, 1 right, 2 bottom, 3 left),
    /// on plain wall only and not touching another opening.
    /// </summary>
    private static bool PlaceOnSide(char[,] grid, int side, Random random, char c, int length)
    {
        var horizontal = side is 0 or 2;
        var span = horizontal ? Width : Height;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var start = random.Next(3, span - 3 - length);
            var free = true;
            for (var i = start - 1; i <= start + length && free; i++)
            {
                var (x, y) = Position(side, i);
                free = grid[y, x] == '#';
            }

            if (!free)
                continue;

            for (var i = start; i < start + length; i++)
            {
                var (x, y) = Position(side, i);
                grid[y, x] = c;
            }

            return true;
        }

        return false;

        static (int X, int Y) Position(int side, int i)
        {
            return side switch
            {
                0 => (i, 0),
                1 => (Width - 1, i),
                2 => (i, Height - 1),
                _ => (0, i),
            };
        }
    }

    private static List<RobustOfficerWave> Waves(Random random, int depth, int seconds, bool boss)
    {
        var pool = Unlocks.Where(u => u.Depth <= depth).ToList();
        // The newest enemies come up more often.
        var enemies = new List<RobustOfficerEnemy>();
        foreach (var (unlock, enemy) in pool)
        {
            var weight = depth - unlock < 4 ? 3 : 2;
            for (var i = 0; i < weight; i++)
            {
                enemies.Add(enemy);
            }
        }

        if (boss)
        {
            // A trickle of the weakest enemies next to the boss.
            return [new RobustOfficerWave(0, 6f, 1, pool.Take(2).Select(p => p.Enemy).ToList())];
        }

        var growth = Math.Min(depth, WaveGrowthEnd);
        var waves = new List<RobustOfficerWave>();
        for (var i = 0; i < 3; i++)
        {
            var interval = MathF.Max(0.6f, 1.8f - growth * 0.045f - i * 0.3f);
            var group = 2 + (growth + i) / 4 + (i == 2 ? 1 : 0);
            waves.Add(new RobustOfficerWave(seconds * i / 3f, interval, Math.Min(group, 7), enemies.OrderBy(_ => random.Next()).ToList()));
        }

        return waves;
    }
}
