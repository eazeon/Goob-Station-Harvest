using System.Linq;
using System.Numerics;
using Content.Shared._Harvest.Arcade.RobustOfficer;

namespace Content.Server._Harvest.Arcade.RobustOfficer;

/// <summary>
/// The five bosses, one at the end of each act.
/// </summary>
public sealed partial class RobustOfficerSimulation
{
    public static int BossBaseHealth(RobustOfficerBoss boss)
    {
        return boss switch
        {
            RobustOfficerBoss.PatientZero => 70,
            RobustOfficerBoss.XenoQueen => 100,
            RobustOfficerBoss.SpaceDragon => 130,
            RobustOfficerBoss.NukieCommander => 160,
            _ => 45,
        };
    }

    private void SpawnBoss(RobustOfficerBoss boss)
    {
        _bossSpawned = true;
        var health = (int) MathF.Ceiling(BossBaseHealth(boss) * _room.BossHealthMultiplier);
        _newEnemies.Add(new Enemy
        {
            Id = NextId(),
            Boss = boss,
            Kind = (RobustOfficerSpriteKind) ((int) RobustOfficerSpriteKind.AssistantKing + (int) boss),
            Position = BossSpawnPosition(),
            Health = health,
            MaxHealth = health,
            Value = 250 * ((int) boss + 1),
            Radius = boss == RobustOfficerBoss.SpaceDragon ? 0.8f : 0.7f,
            Speed = boss switch
            {
                RobustOfficerBoss.PatientZero => 1.2f,
                RobustOfficerBoss.XenoQueen => 1.0f,
                RobustOfficerBoss.SpaceDragon => 1.5f,
                RobustOfficerBoss.NukieCommander => 2.2f,
                _ => 1.4f,
            },
            Side = 1,
        });
        _events |= RobustOfficerEvents.BossSpawned;
    }

    private Vector2 BossSpawnPosition()
    {
        return RobustOfficerGrid.BossSpawn(_tiles, Width, Height) is { } tile ? Center(tile) : _playerStart;
    }

    private void UpdateBoss(Enemy boss, RobustOfficerBoss type, float dt)
    {
        boss.Timer1 += dt;
        boss.Timer2 += dt;
        boss.StateTimer += dt;
        var enraged = boss.Health * 2 < boss.MaxHealth;
        switch (type)
        {
            case RobustOfficerBoss.AssistantKing:
                // Throws toolboxes and calls the greytide for help.
                Walk(boss, ChaseDirection(boss) * boss.Speed, dt);
                if (boss.Timer1 >= (enraged ? 1.8f : 2.4f))
                {
                    boss.Timer1 = 0;
                    var count = enraged ? 5 : 3;
                    for (var i = 0; i < count; i++)
                    {
                        ShootAtPlayer(boss, RobustOfficerSpriteKind.Toolbox, 5f, (i - (count - 1) / 2f) * 15f);
                    }
                }

                if (boss.Timer2 >= 8f)
                {
                    boss.Timer2 = 0;
                    SpawnEnemy(RobustOfficerEnemy.Greytider);
                    SpawnEnemy(RobustOfficerEnemy.Greytider);
                }

                break;

            case RobustOfficerBoss.PatientZero:
                // Shambles around leaving infected puddles, then charges.
                switch (boss.State)
                {
                    case 0:
                        Walk(boss, ChaseDirection(boss) * boss.Speed, dt);
                        if (boss.Timer2 >= (enraged ? 0.7f : 1f))
                        {
                            boss.Timer2 = 0;
                            if (_hazards.Count(h => h.Kind == RobustOfficerSpriteKind.Puddle) < 14)
                                _hazards.Add(new Hazard { Id = NextId(), Kind = RobustOfficerSpriteKind.Puddle, Position = boss.Position, Life = 7f });
                        }

                        if (boss.StateTimer >= 5f)
                            NextState(boss, 1);
                        break;
                    case 1:
                        boss.Telegraph = true;
                        if (boss.StateTimer >= 0.7f)
                        {
                            boss.Dash = AimAtPlayer(boss);
                            NextState(boss, 2);
                        }

                        break;
                    default:
                        boss.LastMove = boss.Dash;
                        if (MoveBody(ref boss.Position, boss.Dash * 6.5f * dt, boss.Radius, BlocksEnemyAt) || boss.StateTimer >= 1f)
                            NextState(boss, 0);
                        break;
                }

                break;

            case RobustOfficerBoss.XenoQueen:
                // Wanders, spits acid in a fan and lays drones.
                if (boss.Wander == Vector2.Zero || (boss.Wander - boss.Position).Length() < 0.5f || boss.StateTimer >= 3f)
                {
                    boss.StateTimer = 0;
                    boss.Wander = RandomFloor(boss.Radius) ?? boss.Position;
                }

                var toWander = boss.Wander - boss.Position;
                if (toWander.Length() > 0.1f)
                    Walk(boss, Vector2.Normalize(toWander) * boss.Speed, dt);

                if (boss.Timer1 >= (enraged ? 1.6f : 2.2f))
                {
                    boss.Timer1 = 0;
                    for (var i = -2; i <= 2; i++)
                    {
                        ShootAtPlayer(boss, RobustOfficerSpriteKind.Acid, 4.5f, i * 20f);
                    }
                }

                if (boss.Timer2 >= 5f)
                {
                    boss.Timer2 = 0;
                    if (_enemies.Count(e => e.Type == RobustOfficerEnemy.Xeno && e.Boss == null && !e.Dead) < 6)
                        SpawnEnemy(RobustOfficerEnemy.Xeno, boss.Position + new Vector2(0, boss.Radius));
                }

                break;

            case RobustOfficerBoss.SpaceDragon:
                // Dashes across the room and opens rifts full of carps.
                switch (boss.State)
                {
                    case 0:
                        Walk(boss, ChaseDirection(boss) * boss.Speed, dt);
                        if (boss.StateTimer >= (enraged ? 1.1f : 1.6f))
                            NextState(boss, 1);
                        break;
                    case 1:
                        boss.Telegraph = true;
                        if (boss.StateTimer >= 0.9f)
                        {
                            boss.Dash = AimAtPlayer(boss);
                            NextState(boss, 2);
                        }

                        break;
                    default:
                        boss.LastMove = boss.Dash;
                        if (MoveBody(ref boss.Position, boss.Dash * 9f * dt, boss.Radius, BlocksEnemyAt))
                        {
                            // Dazed after hitting a wall.
                            boss.Stun = 0.8f;
                            NextState(boss, 0);
                        }
                        else if (boss.StateTimer >= 1.4f)
                        {
                            NextState(boss, 0);
                        }

                        break;
                }

                if (boss.Timer2 >= 7f)
                {
                    boss.Timer2 = 0;
                    if (RandomFloor(0.4f, 4f) is { } rift)
                        _hazards.Add(new Hazard { Id = NextId(), Kind = RobustOfficerSpriteKind.Rift, Position = rift, Life = 1.2f });
                }

                break;

            case RobustOfficerBoss.NukieCommander:
                // Keeps its distance, fires bursts and calls in operatives while the nuke ticks.
                KeepDistance(boss, 4f, 6f, dt);
                if (boss.Burst > 0)
                {
                    boss.Timer3 += dt;
                    if (boss.Timer3 >= 0.12f)
                    {
                        boss.Timer3 = 0;
                        boss.Burst--;
                        ShootAtPlayer(boss, RobustOfficerSpriteKind.EnemyBullet, 7f, (float) (_random.NextDouble() * 10 - 5));
                    }
                }
                else if (boss.Timer1 >= (enraged ? 1.1f : 1.4f))
                {
                    boss.Timer1 = 0;
                    boss.Burst = enraged ? 4 : 3;
                    boss.Timer3 = 0.12f;
                }

                if (boss.Timer2 >= 10f)
                {
                    boss.Timer2 = 0;
                    var nukies = _enemies.Count(e => e.Type == RobustOfficerEnemy.Nukie && e.Boss == null && !e.Dead);
                    for (var i = nukies; i < Math.Min(nukies + 2, 4); i++)
                    {
                        SpawnEnemy(RobustOfficerEnemy.Nukie);
                    }
                }

                break;
        }
    }

    private Vector2 AimAtPlayer(Enemy enemy)
    {
        var dir = PlayerPosition - enemy.Position;
        return dir == Vector2.Zero ? new Vector2(0, 1) : Vector2.Normalize(dir);
    }

    /// <summary>
    /// The center of a random floor tile with room for a body of <paramref name="radius"/>,
    /// at least <paramref name="awayFromPlayer"/> tiles from the officer.
    /// </summary>
    private Vector2? RandomFloor(float radius, float awayFromPlayer = 0f)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var center = new Vector2(_random.Next(1, Width - 1) + 0.5f, _random.Next(1, Height - 1) + 0.5f);
            if (Overlaps(center, radius, BlocksEnemyAt) || (center - PlayerPosition).Length() < awayFromPlayer)
                continue;
            if (this[(int) center.X, (int) center.Y] == RobustOfficerTile.Door)
                continue;
            return center;
        }

        return null;
    }
}
