using System.Linq;
using System.Numerics;
using Content.Shared._Harvest.Arcade.RobustOfficer;

namespace Content.Server._Harvest.Arcade.RobustOfficer;

/// <summary>
/// One room of Robust Officer: the officer holds it against waves of enemies or a boss, then walks out
/// through the exit. Pure game logic, no entities; positions are in tiles from the top left corner.
/// </summary>
public sealed partial class RobustOfficerSimulation
{
    /// <summary>
    /// The server's tick rate, so every server tick sends exactly one new state.
    /// </summary>
    public const int TicksPerSecond = 30;
    public const float TickSeconds = 1f / TicksPerSecond;

    public const int MaxEnemies = 48;
    public const float PlayerRadius = RobustOfficerCollision.PlayerRadius;
    public const float RespawnInvulnerability = 2f;
    public const float HitInvulnerability = 1.2f;
    public const float PickupLifetime = 10f;
    public const float EffectSeconds = 12f;
    public const float BossDelay = 1.5f;
    public const float NukeSeconds = 90f;
    public const float TurretSeconds = 25f;
    public const float SecBorgSeconds = 15f;
    public const float ArmorRegenSeconds = 15f;
    public const float PickupChance = 0.12f;

    /// <summary>
    /// How close the officer has to get to pick something up.
    /// </summary>
    public const float PickupRange = 0.9f;
    public const float CreditChance = 0.22f;

    private const ushort PlayerId = 1;
    private const ushort BarrierIdBase = 60000;

    private static readonly (RobustOfficerPickup Pickup, int Weight)[] PowerUps =
    [
        (RobustOfficerPickup.Stimulants, 3),
        (RobustOfficerPickup.RapidFire, 3),
        (RobustOfficerPickup.Buckshot, 3),
        (RobustOfficerPickup.ArmorPlate, 2),
        (RobustOfficerPickup.Donut, 1),
        (RobustOfficerPickup.Flashbang, 2),
        (RobustOfficerPickup.Lube, 2),
        (RobustOfficerPickup.HoloBarrier, 2),
        (RobustOfficerPickup.SecBorg, 2),
    ];

    public readonly int Width;
    public readonly int Height;
    private readonly RobustOfficerTile[] _tiles;
    private readonly float[] _barrier;
    private readonly int[] _flow;
    private readonly RobustOfficerRun _run;
    private readonly RobustOfficerRoom _room;
    private readonly Random _random;
    private readonly Vector2 _playerStart;
    private readonly List<Vector2i> _spawns = new();

    private readonly List<Enemy> _enemies = new();

    /// <summary>
    /// Enemies spawned during the tick, added once nothing iterates <see cref="_enemies"/> anymore.
    /// </summary>
    private readonly List<Enemy> _newEnemies = new();
    private readonly List<Bullet> _bullets = new();
    private readonly List<Pickup> _pickups = new();
    private readonly List<Hazard> _hazards = new();
    private readonly List<Ally> _allies = new();
    private readonly List<Poof> _poofs = new();
    private readonly Dictionary<RobustOfficerPickup, float> _effects = new();

    private ushort _nextId = PlayerId;
    private float _elapsed;
    private float _spawnTimer;
    private float _bossTimer = BossDelay;
    private bool _bossSpawned;
    private float _invulnerable;
    private float _fireCooldown;
    private float _armorRegen;

    /// <summary>
    /// After a death the officer comes back without armor for the rest of the room.
    /// </summary>
    private bool _armorLost;
    private Vector2 _facing = new(0, 1);
    private RobustOfficerEvents _events;

    /// <summary>
    /// Held movement, each axis from -1 to 1, y down.
    /// </summary>
    public Vector2i Move;

    /// <summary>
    /// Held shooting direction, each axis from -1 to 1, y down.
    /// </summary>
    public Vector2i Shoot;

    /// <summary>
    /// Set to use the stocked item on the next tick.
    /// </summary>
    public bool UseItem;

    public Vector2 PlayerPosition;
    public int Armor;
    public bool Dead;
    public bool Exited;
    public bool ExitOpen;

    /// <summary>
    /// Seconds before the enemies stop coming; unused in a boss room.
    /// </summary>
    public float TimeLeft;

    /// <summary>
    /// Seconds before the nuke goes off, in the nuclear operative commander's room.
    /// </summary>
    public float? NukeTimer;

    public RobustOfficerSimulation(RobustOfficerRoom room, RobustOfficerRun run, int seed = 0)
    {
        _room = room;
        _run = run;
        _random = new Random(seed);
        _tiles = RobustOfficerGrid.Parse(room.Map, out Width, out Height, out var player);
        _barrier = new float[_tiles.Length];
        _flow = new int[_tiles.Length];
        _playerStart = new Vector2(player.X + 0.5f, player.Y + 0.5f);
        PlayerPosition = _playerStart;
        TimeLeft = room.Seconds;
        Armor = run.MaxArmor;
        if (room.Boss == RobustOfficerBoss.NukieCommander)
            NukeTimer = NukeSeconds;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (RobustOfficerGrid.IsSpawn(this[x, y]))
                    _spawns.Add(new Vector2i(x, y));
            }
        }

        _spawnTimer = 1f;
        DeployTurret();
    }

    public RobustOfficerTile this[int x, int y] =>
        x < 0 || y < 0 || x >= Width || y >= Height ? RobustOfficerTile.Wall : _tiles[y * Width + x];

    public bool IsBossRoom => _room.Boss != null;

    public int Theme => _room.Theme;

    public int EnemyCount => _enemies.Count;

    public IEnumerable<RobustOfficerEnemy> Enemies => _enemies.Where(e => e.Boss == null).Select(e => e.Type);

    public int PickupCount => _pickups.Count;

    public bool BossAlive => _enemies.Any(e => e.Boss != null);

    public IReadOnlyDictionary<RobustOfficerPickup, float> Effects => _effects;

    public byte[] GetTiles()
    {
        return _tiles.Select(t => (byte) t).ToArray();
    }

    public RobustOfficerEvents Tick()
    {
        _events = RobustOfficerEvents.None;
        const float dt = TickSeconds;
        AgePoofs(dt);
        if (Dead || Exited)
            return _events;

        _elapsed += dt;
        UpdateTimers(dt);
        MovePlayer(dt);
        if (ExitOpen && this[(int) MathF.Floor(PlayerPosition.X), (int) MathF.Floor(PlayerPosition.Y)] == RobustOfficerTile.ExitOpen)
        {
            Exited = true;
            _events |= RobustOfficerEvents.Exited;
            return _events;
        }

        if (UseItem)
        {
            UseItem = false;
            if (_run.Stocked is { } item)
            {
                _run.Stocked = null;
                Apply(item);
                _events |= RobustOfficerEvents.ItemUsed;
            }
        }

        Fire();
        RobustOfficerGrid.DistanceField(_flow, Width, Height, Tile(PlayerPosition), (x, y) => !BlocksEnemyAt(x, y));
        SpawnEnemies(dt);
        UpdateEnemies(dt);
        UpdateBullets(dt);
        UpdateAllies(dt);
        UpdatePickups(dt);
        UpdateHazards(dt);

        _enemies.RemoveAll(e => e.Dead);
        _bullets.RemoveAll(b => b.Dead);
        _pickups.RemoveAll(p => p.Dead);
        _hazards.RemoveAll(h => h.Dead);
        _enemies.AddRange(_newEnemies);
        _newEnemies.Clear();

        CheckCleared();

        if (NukeTimer != null && !ExitOpen && !Dead)
        {
            NukeTimer -= dt;
            if (NukeTimer <= 0)
            {
                _events |= RobustOfficerEvents.NukeExploded;
                Die();
            }
        }

        return _events;
    }

    /// <summary>
    /// Brings the officer back after a death: the room is cleared of what was chasing them, the timer keeps going.
    /// </summary>
    public void Respawn()
    {
        Dead = false;
        UseItem = false;
        PlayerPosition = _playerStart;
        _invulnerable = RespawnInvulnerability;
        Armor = 0;
        _armorLost = true;
        _effects.Clear();
        _bullets.Clear();
        _hazards.Clear();
        _allies.Clear();
        _enemies.RemoveAll(e => e.Boss == null);
        foreach (var boss in _enemies)
        {
            boss.Position = BossSpawnPosition();
            boss.State = 0;
            boss.StateTimer = 0;
            boss.Stun = 1f;
        }

        if (NukeTimer != null)
            NukeTimer = NukeSeconds;
        DeployTurret();
    }

    /// <summary>
    /// Puts an enemy in the room at <paramref name="position"/>, in tiles.
    /// </summary>
    public void AddEnemy(RobustOfficerEnemy type, Vector2 position)
    {
        SpawnEnemy(type, position);
        _enemies.AddRange(_newEnemies);
        _newEnemies.Clear();
    }

    /// <summary>
    /// Drops a pickup at <paramref name="position"/>, in tiles.
    /// </summary>
    public void AddPickup(RobustOfficerPickup pickup, Vector2 position)
    {
        DropAt(pickup, position);
    }

    /// <summary>
    /// Kills every enemy in the room, the boss included, without drops.
    /// </summary>
    public void KillEnemies()
    {
        foreach (var enemy in _enemies.OrderBy(e => e.Boss == null))
        {
            Kill(enemy, false);
        }

        _enemies.RemoveAll(e => e.Dead);
        _newEnemies.Clear();
    }

    private void UpdateTimers(float dt)
    {
        _invulnerable = MathF.Max(0, _invulnerable - dt);
        _fireCooldown = MathF.Max(0, _fireCooldown - dt);
        foreach (var effect in _effects.Keys.ToList())
        {
            _effects[effect] -= dt;
            if (_effects[effect] <= 0)
                _effects.Remove(effect);
        }

        for (var i = 0; i < _barrier.Length; i++)
        {
            if (_barrier[i] > 0)
                _barrier[i] = MathF.Max(0, _barrier[i] - dt);
        }

        if (_run.RegeneratesArmor && !_armorLost && Armor < _run.MaxArmor)
        {
            _armorRegen += dt;
            if (_armorRegen >= ArmorRegenSeconds)
            {
                _armorRegen = 0;
                Armor++;
            }
        }
        else
        {
            _armorRegen = 0;
        }
    }

    /// <summary>
    /// Tiles per second the officer walks, stimulants included.
    /// </summary>
    public float PlayerSpeed => _run.Speed * (_effects.ContainsKey(RobustOfficerPickup.Stimulants) ? 1.5f : 1f);

    private void MovePlayer(float dt)
    {
        var moved = Vector2.Zero;
        if (Move != Vector2i.Zero)
        {
            moved = Vector2.Normalize(new Vector2(Move.X, Move.Y)) * PlayerSpeed * dt;
            MoveBody(ref PlayerPosition, moved, PlayerRadius, BlocksPlayerAt);
        }

        if (Shoot != Vector2i.Zero)
            _facing = Vector2.Normalize(new Vector2(Shoot.X, Shoot.Y));
        else if (moved.LengthSquared() > 0.000001f)
            _facing = Vector2.Normalize(moved);
    }

    private void Fire()
    {
        if (Shoot == Vector2i.Zero || _fireCooldown > 0)
            return;

        var weapon = _run.Weapon;
        _fireCooldown = _run.Cooldown * (_effects.ContainsKey(RobustOfficerPickup.RapidFire) ? 0.45f : 1f);
        var dir = Vector2.Normalize(new Vector2(Shoot.X, Shoot.Y));
        var angles = new List<float>();
        for (var i = 0; i < weapon.Pellets; i++)
        {
            angles.Add(weapon.Pellets == 1 ? 0 : -weapon.SpreadDegrees / 2 + weapon.SpreadDegrees * i / (weapon.Pellets - 1));
        }

        if (_effects.ContainsKey(RobustOfficerPickup.Buckshot))
        {
            var edge = weapon.SpreadDegrees / 2 + 12f;
            angles.Add(-edge);
            angles.Add(edge);
        }

        foreach (var angle in angles)
        {
            var velocity = Rotate(dir, angle) * weapon.Speed;
            _bullets.Add(new Bullet
            {
                Id = NextId(),
                Kind = weapon.Projectile,
                Position = PlayerPosition + dir * 0.3f,
                Velocity = velocity,
                Life = weapon.Range / weapon.Speed,
                Damage = _run.BulletDamage,
                Pierce = weapon.Pierce,
            });
        }

        _events |= RobustOfficerEvents.Shot;
    }

    private void SpawnEnemies(float dt)
    {
        if (IsBossRoom)
        {
            if (!_bossSpawned)
            {
                _bossTimer -= dt;
                if (_bossTimer <= 0)
                    SpawnBoss(_room.Boss!.Value);
                return;
            }

            if (!BossAlive)
                return;
        }
        else
        {
            TimeLeft = MathF.Max(0, TimeLeft - dt);
            if (TimeLeft <= 0)
                return;
        }

        var wave = _room.Waves.LastOrDefault(w => w.From <= _elapsed);
        if (wave == null || wave.Enemies.Count == 0)
            return;

        _spawnTimer -= dt * _room.SpawnRate;
        if (_spawnTimer > 0)
            return;

        _spawnTimer += MathF.Max(0.2f, wave.Interval);
        for (var i = 0; i < wave.Group; i++)
        {
            SpawnEnemy(wave.Enemies[_random.Next(wave.Enemies.Count)]);
        }
    }

    /// <summary>
    /// Sends an enemy through a door or vent away from the officer, or at <paramref name="at"/>.
    /// </summary>
    private Enemy? SpawnEnemy(RobustOfficerEnemy type, Vector2? at = null)
    {
        if (_enemies.Count(e => !e.Dead) + _newEnemies.Count >= MaxEnemies)
            return null;

        Vector2 position;
        if (at is { } given)
        {
            position = given;
        }
        else
        {
            if (_spawns.Count == 0)
                return null;

            var far = _spawns.Where(s => (Center(s) - PlayerPosition).Length() > 3.5f).ToList();
            var spawn = far.Count > 0 ? far[_random.Next(far.Count)] : _spawns[_random.Next(_spawns.Count)];
            position = Center(spawn);
        }

        var stats = EnemyStats.Of(type);
        var health = Math.Max(1, (int) MathF.Round(stats.Health * _room.HealthMultiplier, MidpointRounding.AwayFromZero));
        var enemy = new Enemy
        {
            Id = NextId(),
            Type = type,
            Kind = stats.Kind,
            Position = position,
            Health = health,
            MaxHealth = health,
            Speed = stats.Speed,
            Radius = stats.Radius,
            Value = stats.Value,
            Timer1 = (float) _random.NextDouble(),
            Side = _random.Next(2) == 0 ? -1 : 1,
        };
        _newEnemies.Add(enemy);
        return enemy;
    }

    private void UpdateEnemies(float dt)
    {
        foreach (var enemy in _enemies)
        {
            if (enemy.Dead)
                continue;

            enemy.Hurt = MathF.Max(0, enemy.Hurt - dt);
            enemy.SlipImmunity = MathF.Max(0, enemy.SlipImmunity - dt);
            if (enemy.SlideTime > 0)
            {
                enemy.SlideTime -= dt;
                MoveBody(ref enemy.Position, enemy.Slide * dt, enemy.Radius, BlocksEnemyAt);
            }

            if (enemy.Stun > 0)
            {
                enemy.Stun -= dt;
                continue;
            }

            if (enemy.Boss is { } boss)
                UpdateBoss(enemy, boss, dt);
            else
                UpdateEnemy(enemy, dt);

            if (enemy.Boss == null && enemy.SlipImmunity <= 0 && OnLube(enemy.Position))
            {
                enemy.Stun = 1.5f;
                enemy.SlipImmunity = 3f;
                enemy.Slide = enemy.LastMove * 4f;
                enemy.SlideTime = 0.3f;
            }

            if (!enemy.Phasing && enemy.Stun <= 0
                && (enemy.Position - PlayerPosition).Length() < enemy.Radius + PlayerRadius - 0.05f)
            {
                HitPlayer();
            }
        }

        Separate();
    }

    private void UpdateEnemy(Enemy enemy, float dt)
    {
        enemy.Timer1 += dt;
        enemy.Telegraph = false;
        switch (enemy.Type)
        {
            case RobustOfficerEnemy.Clown:
            {
                var dir = ChaseDirection(enemy);
                var wobble = new Vector2(-dir.Y, dir.X) * MathF.Sin(_elapsed * 6f + enemy.Id) * 0.8f;
                Walk(enemy, Vector2.Normalize(dir + wobble) * enemy.Speed, dt);
                break;
            }
            case RobustOfficerEnemy.Revenant:
            {
                // Intangible for a while every four seconds.
                enemy.Phasing = enemy.Timer1 % 4f > 2.8f;
                var dir = PlayerPosition - enemy.Position;
                if (dir != Vector2.Zero)
                    dir = Vector2.Normalize(dir);
                enemy.LastMove = dir;
                MoveBody(ref enemy.Position, dir * enemy.Speed * dt, enemy.Radius, BlocksGhostAt);
                break;
            }
            case RobustOfficerEnemy.Carp:
                UpdateCarp(enemy, dt);
                break;
            case RobustOfficerEnemy.Nukie:
                KeepDistance(enemy, 3.5f, 6f, dt);
                if (enemy.Timer1 >= 1.8f)
                {
                    enemy.Timer1 = 0;
                    ShootAtPlayer(enemy, RobustOfficerSpriteKind.EnemyBullet, 6f, 0f);
                }

                break;
            default:
                Walk(enemy, ChaseDirection(enemy) * enemy.Speed, dt);
                break;
        }
    }

    /// <summary>
    /// Carps drift towards the officer, stop, then dash in a straight line.
    /// </summary>
    private void UpdateCarp(Enemy enemy, float dt)
    {
        enemy.StateTimer += dt;
        switch (enemy.State)
        {
            case 0:
                Walk(enemy, ChaseDirection(enemy) * enemy.Speed * 0.5f, dt);
                if (enemy.StateTimer >= 2f)
                    NextState(enemy, 1);
                break;
            case 1:
                enemy.Telegraph = true;
                if (enemy.StateTimer >= 0.5f)
                {
                    var dir = PlayerPosition - enemy.Position;
                    enemy.Dash = dir == Vector2.Zero ? new Vector2(0, 1) : Vector2.Normalize(dir);
                    NextState(enemy, 2);
                }

                break;
            default:
                enemy.LastMove = enemy.Dash;
                if (MoveBody(ref enemy.Position, enemy.Dash * 16f * dt, enemy.Radius, BlocksEnemyAt) || enemy.StateTimer >= 0.45f)
                    NextState(enemy, 0);
                break;
        }
    }

    private static void NextState(Enemy enemy, int state)
    {
        enemy.State = state;
        enemy.StateTimer = 0;
        enemy.Telegraph = false;
    }

    /// <summary>
    /// Stays between <paramref name="min"/> and <paramref name="max"/> tiles from the officer, circling around them.
    /// </summary>
    private void KeepDistance(Enemy enemy, float min, float max, float dt)
    {
        var toPlayer = PlayerPosition - enemy.Position;
        var distance = toPlayer.Length();
        Vector2 dir;
        if (distance > max)
            dir = ChaseDirection(enemy);
        else if (distance < min && distance > 0)
            dir = -toPlayer / distance;
        else
            dir = distance > 0 ? new Vector2(-toPlayer.Y, toPlayer.X) / distance * enemy.Side : Vector2.Zero;

        if (Walk(enemy, dir * enemy.Speed, dt))
            enemy.Side = -enemy.Side;
    }

    /// <summary>
    /// Follows the distance field towards the officer, or goes straight at them when close or cut off.
    /// </summary>
    private Vector2 ChaseDirection(Enemy enemy)
    {
        var toPlayer = PlayerPosition - enemy.Position;
        var tile = Tile(enemy.Position);
        var index = tile.Y * Width + tile.X;
        if (toPlayer.Length() < 1.2f || index < 0 || index >= _flow.Length || _flow[index] == RobustOfficerGrid.Unreachable)
            return toPlayer == Vector2.Zero ? Vector2.Zero : Vector2.Normalize(toPlayer);

        // Fewest steps first, then the tile closest to the officer, so enemies walk straight rather than zigzag.
        var best = _flow[index];
        var bestDistance = float.MaxValue;
        Vector2i? next = null;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                var x = tile.X + dx;
                var y = tile.Y + dy;
                if (x < 0 || y < 0 || x >= Width || y >= Height)
                    continue;
                if (dx != 0 && dy != 0 && (BlocksEnemyAt(tile.X + dx, tile.Y) || BlocksEnemyAt(tile.X, tile.Y + dy)))
                    continue;

                var flow = _flow[y * Width + x];
                if (flow > best || flow >= _flow[index])
                    continue;

                var distance = (Center(new Vector2i(x, y)) - PlayerPosition).LengthSquared();
                if (flow == best && distance >= bestDistance)
                    continue;

                best = flow;
                bestDistance = distance;
                next = new Vector2i(x, y);
            }
        }

        var target = next is { } n ? Center(n) : PlayerPosition;
        var dir = target - enemy.Position;
        return dir == Vector2.Zero ? Vector2.Zero : Vector2.Normalize(dir);
    }

    /// <returns>Whether it bumped into something.</returns>
    private bool Walk(Enemy enemy, Vector2 velocity, float dt)
    {
        if (velocity != Vector2.Zero)
            enemy.LastMove = Vector2.Normalize(velocity);
        return MoveBody(ref enemy.Position, velocity * dt, enemy.Radius, BlocksEnemyAt);
    }

    /// <summary>
    /// Pushes overlapping enemies apart so a horde does not stack on one tile.
    /// </summary>
    private void Separate()
    {
        for (var i = 0; i < _enemies.Count; i++)
        {
            var a = _enemies[i];
            if (a.Dead)
                continue;

            for (var j = i + 1; j < _enemies.Count; j++)
            {
                var b = _enemies[j];
                if (b.Dead)
                    continue;

                var delta = b.Position - a.Position;
                var min = (a.Radius + b.Radius) * 0.9f;
                var distance = delta.Length();
                if (distance >= min)
                    continue;

                var push = distance > 0.001f ? delta / distance : new Vector2(1, 0);
                var amount = (min - distance) / 2f;
                // Bosses do not get pushed around.
                if (a.Boss == null)
                    MoveBody(ref a.Position, -push * amount * (b.Boss != null ? 2 : 1), a.Radius, a.Type == RobustOfficerEnemy.Revenant ? BlocksGhostAt : BlocksEnemyAt);
                if (b.Boss == null)
                    MoveBody(ref b.Position, push * amount * (a.Boss != null ? 2 : 1), b.Radius, b.Type == RobustOfficerEnemy.Revenant ? BlocksGhostAt : BlocksEnemyAt);
            }
        }
    }

    private void ShootAtPlayer(Enemy enemy, RobustOfficerSpriteKind kind, float speed, float angle)
    {
        var dir = PlayerPosition - enemy.Position;
        if (dir == Vector2.Zero)
            return;

        dir = Rotate(Vector2.Normalize(dir), angle);
        _bullets.Add(new Bullet
        {
            Id = NextId(),
            Kind = kind,
            Position = enemy.Position + dir * enemy.Radius,
            Velocity = dir * speed,
            Life = 4f,
            Hostile = true,
        });
        _events |= RobustOfficerEvents.EnemyShot;
    }

    private void UpdateBullets(float dt)
    {
        foreach (var bullet in _bullets)
        {
            bullet.Life -= dt;
            if (bullet.Life <= 0)
            {
                bullet.Dead = true;
                continue;
            }

            // Two half steps, so fast bullets do not skip over an enemy.
            for (var step = 0; step < 2 && !bullet.Dead; step++)
            {
                bullet.Position += bullet.Velocity * dt / 2;
                var tile = Tile(bullet.Position);
                if (RobustOfficerGrid.BlocksBullet(this[tile.X, tile.Y]) || bullet.Hostile && BarrierAt(tile.X, tile.Y))
                {
                    bullet.Dead = true;
                    break;
                }

                if (bullet.Hostile)
                {
                    if ((bullet.Position - PlayerPosition).Length() < PlayerRadius + 0.1f)
                    {
                        bullet.Dead = true;
                        HitPlayer();
                    }

                    continue;
                }

                foreach (var enemy in _enemies)
                {
                    if (enemy.Dead || enemy.Phasing || (bullet.Position - enemy.Position).Length() >= enemy.Radius + 0.12f)
                        continue;
                    if (bullet.Pierce && !(bullet.Hit ??= new HashSet<ushort>()).Add(enemy.Id))
                        continue;

                    Damage(enemy, bullet.Damage);
                    if (!bullet.Pierce)
                    {
                        bullet.Dead = true;
                        break;
                    }
                }
            }
        }
    }

    private void Damage(Enemy enemy, int damage)
    {
        enemy.Health -= damage;
        enemy.Hurt = 0.12f;
        _events |= RobustOfficerEvents.EnemyHit;
        if (enemy.Health <= 0)
            Kill(enemy);
    }

    private void Kill(Enemy enemy, bool drops = true)
    {
        if (enemy.Dead)
            return;

        enemy.Dead = true;
        _run.Score += enemy.Value;
        _events |= RobustOfficerEvents.EnemyKilled;
        AddPoof(enemy.Position);

        if (enemy.Boss != null)
        {
            _events |= RobustOfficerEvents.BossKilled;
            foreach (var other in _enemies)
            {
                if (other != enemy)
                    Kill(other, false);
            }

            _newEnemies.Clear();

            _bullets.RemoveAll(b => b.Hostile);
            for (var i = 0; i < 6; i++)
            {
                var offset = Rotate(new Vector2(0, 0.9f), i * 60f);
                DropAt(RobustOfficerPickup.CreditBundle, enemy.Position + offset);
            }

            return;
        }

        if (enemy.Type == RobustOfficerEnemy.Slime)
        {
            for (var i = 0; i < 2; i++)
            {
                var small = SpawnEnemy(RobustOfficerEnemy.SlimeSmall, enemy.Position);
                if (small != null)
                    MoveBody(ref small.Position, new Vector2(i == 0 ? -0.3f : 0.3f, 0), small.Radius, BlocksEnemyAt);
            }
        }

        if (!drops)
            return;

        var roll = _random.NextDouble();
        if (roll < PickupChance)
            DropAt(RollPowerUp(), enemy.Position);
        else if (roll < PickupChance + CreditChance)
            DropAt(_random.NextDouble() < 0.1 ? RobustOfficerPickup.CreditBundle : RobustOfficerPickup.Credit, enemy.Position);
    }

    private RobustOfficerPickup RollPowerUp()
    {
        var total = PowerUps.Sum(p => p.Weight);
        var roll = _random.Next(total);
        foreach (var (pickup, weight) in PowerUps)
        {
            roll -= weight;
            if (roll < 0)
                return pickup;
        }

        return PowerUps[0].Pickup;
    }

    private void DropAt(RobustOfficerPickup pickup, Vector2 position)
    {
        var tile = Tile(position);
        if (RobustOfficerGrid.BlocksPlayer(this[tile.X, tile.Y]))
            position = Center(Tile(PlayerPosition));
        _pickups.Add(new Pickup { Id = NextId(), Type = pickup, Position = position, Life = PickupLifetime });
    }

    private void HitPlayer()
    {
        if (Dead || _invulnerable > 0)
            return;

        if (Armor > 0)
        {
            Armor--;
            _invulnerable = HitInvulnerability;
            _events |= RobustOfficerEvents.ArmorHit;
            return;
        }

        Die();
    }

    private void Die()
    {
        if (Dead)
            return;

        Dead = true;
        _run.Lives--;
        _effects.Clear();
        _events |= RobustOfficerEvents.PlayerDied;
        AddPoof(PlayerPosition);
    }

    private void UpdatePickups(float dt)
    {
        foreach (var pickup in _pickups)
        {
            var isCredit = pickup.Type is RobustOfficerPickup.Credit or RobustOfficerPickup.CreditBundle;
            // Once the room is safe, the credits fly to the officer.
            if (ExitOpen && isCredit)
            {
                var dir = PlayerPosition - pickup.Position;
                if (dir.Length() > 0.1f)
                    pickup.Position += Vector2.Normalize(dir) * MathF.Min(dir.Length(), 8f * dt);
            }
            else
            {
                pickup.Life -= dt;
                if (pickup.Life <= 0)
                {
                    pickup.Dead = true;
                    continue;
                }
            }

            if ((pickup.Position - PlayerPosition).Length() < PickupRange)
            {
                pickup.Dead = true;
                Collect(pickup.Type);
            }
        }
    }

    private void Collect(RobustOfficerPickup pickup)
    {
        switch (pickup)
        {
            case RobustOfficerPickup.Credit:
                _run.Credits++;
                _run.Score += 5;
                _events |= RobustOfficerEvents.Credit;
                return;
            case RobustOfficerPickup.CreditBundle:
                _run.Credits += 5;
                _run.Score += 25;
                _events |= RobustOfficerEvents.Credit;
                return;
        }

        _events |= RobustOfficerEvents.Pickup;
        _run.Score += 10;
        if (IsStockable(pickup) && _run.Stocked == null)
        {
            _run.Stocked = pickup;
            return;
        }

        Apply(pickup);
    }

    public static bool IsStockable(RobustOfficerPickup pickup)
    {
        return pickup is RobustOfficerPickup.Flashbang or RobustOfficerPickup.Lube
            or RobustOfficerPickup.HoloBarrier or RobustOfficerPickup.SecBorg;
    }

    private void Apply(RobustOfficerPickup pickup)
    {
        switch (pickup)
        {
            case RobustOfficerPickup.Stimulants:
            case RobustOfficerPickup.RapidFire:
            case RobustOfficerPickup.Buckshot:
                _effects[pickup] = EffectSeconds;
                break;
            case RobustOfficerPickup.ArmorPlate:
                Armor = Math.Min(Armor + 1, _run.MaxArmor + 1);
                break;
            case RobustOfficerPickup.Donut:
                _run.Lives = Math.Min(_run.Lives + 1, RobustOfficerRun.MaxLives);
                break;
            case RobustOfficerPickup.Flashbang:
                foreach (var enemy in _enemies)
                {
                    enemy.Stun = MathF.Max(enemy.Stun, enemy.Boss != null ? 1.5f : 4f);
                    enemy.Telegraph = false;
                }

                _bullets.RemoveAll(b => b.Hostile);
                break;
            case RobustOfficerPickup.Lube:
                for (var i = 0; i < 8; i++)
                {
                    var position = PlayerPosition + Rotate(new Vector2(0, 1.6f), i * 45f);
                    var tile = Tile(position);
                    if (!RobustOfficerGrid.BlocksEnemy(this[tile.X, tile.Y]))
                        _hazards.Add(new Hazard { Id = NextId(), Kind = RobustOfficerSpriteKind.LubePuddle, Position = position, Life = 10f });
                }

                break;
            case RobustOfficerPickup.HoloBarrier:
                PlaceBarrier();
                break;
            case RobustOfficerPickup.SecBorg:
                _allies.Add(new Ally
                {
                    Id = NextId(),
                    Kind = RobustOfficerSpriteKind.SecBorg,
                    Position = PlayerPosition,
                    Life = SecBorgSeconds,
                    Range = 7f,
                    Cooldown = 0.45f,
                });
                break;
        }
    }

    /// <summary>
    /// A wall of five holographic tiles two tiles in front of the officer, which stops enemies and their shots.
    /// </summary>
    private void PlaceBarrier()
    {
        var dir = MathF.Abs(_facing.X) > MathF.Abs(_facing.Y)
            ? new Vector2i(MathF.Sign(_facing.X), 0)
            : new Vector2i(0, _facing.Y < 0 ? -1 : 1);
        var center = Tile(PlayerPosition) + dir * 2;
        var across = new Vector2i(dir.Y, dir.X);
        for (var i = -2; i <= 2; i++)
        {
            var tile = center + across * i;
            if (this[tile.X, tile.Y] is not (RobustOfficerTile.Floor or RobustOfficerTile.Vent))
                continue;

            _barrier[tile.Y * Width + tile.X] = 10f;
            foreach (var enemy in _enemies)
            {
                if (Tile(enemy.Position) == tile)
                    enemy.Stun = MathF.Max(enemy.Stun, 0.5f);
            }
        }
    }

    private void DeployTurret()
    {
        if (!_run.HasTurret)
            return;

        _allies.Add(new Ally
        {
            Id = NextId(),
            Kind = RobustOfficerSpriteKind.Turret,
            Position = _playerStart,
            Life = TurretSeconds,
            Range = 6f,
            Cooldown = 0.6f,
        });
    }

    private void UpdateAllies(float dt)
    {
        foreach (var ally in _allies)
        {
            ally.Life -= dt;
            ally.Timer -= dt;
            if (ally.Kind == RobustOfficerSpriteKind.SecBorg)
            {
                // Hovers next to the officer.
                var target = PlayerPosition + new Vector2(-0.8f, -0.6f);
                var dir = target - ally.Position;
                if (dir.Length() > 0.1f)
                    ally.Position += Vector2.Normalize(dir) * MathF.Min(dir.Length(), 6f * dt);
            }

            if (ally.Timer > 0)
                continue;

            Enemy? closest = null;
            var best = ally.Range;
            foreach (var enemy in _enemies)
            {
                var distance = (enemy.Position - ally.Position).Length();
                if (!enemy.Dead && !enemy.Phasing && distance < best)
                {
                    best = distance;
                    closest = enemy;
                }
            }

            if (closest == null)
                continue;

            ally.Timer = ally.Cooldown;
            var aim = Vector2.Normalize(closest.Position - ally.Position);
            _bullets.Add(new Bullet
            {
                Id = NextId(),
                Kind = RobustOfficerSpriteKind.BoltDisabler,
                Position = ally.Position,
                Velocity = aim * 11f,
                Life = ally.Range / 11f + 0.2f,
                Damage = 1,
            });
        }

        _allies.RemoveAll(a => a.Life <= 0);
    }

    private void UpdateHazards(float dt)
    {
        foreach (var hazard in _hazards)
        {
            hazard.Life -= dt;
            if (hazard.Kind == RobustOfficerSpriteKind.Rift && hazard.Life <= 0)
            {
                for (var i = 0; i < 3; i++)
                {
                    SpawnEnemy(RobustOfficerEnemy.Carp, hazard.Position + Rotate(new Vector2(0, 0.4f), i * 120f));
                }
            }

            if (hazard.Life <= 0)
            {
                hazard.Dead = true;
                continue;
            }

            if (hazard.Kind == RobustOfficerSpriteKind.Puddle && (hazard.Position - PlayerPosition).Length() < 0.45f + PlayerRadius)
                HitPlayer();
        }
    }

    private bool OnLube(Vector2 position)
    {
        foreach (var hazard in _hazards)
        {
            if (hazard.Kind == RobustOfficerSpriteKind.LubePuddle && !hazard.Dead && (hazard.Position - position).Length() < 0.55f)
                return true;
        }

        return false;
    }

    private void CheckCleared()
    {
        if (ExitOpen || Dead)
            return;

        var cleared = IsBossRoom
            ? _bossSpawned && !BossAlive
            : TimeLeft <= 0 && _enemies.Count == 0;
        if (!cleared)
            return;

        ExitOpen = true;
        _events |= RobustOfficerEvents.ExitOpened;
        _bullets.RemoveAll(b => b.Hostile);
        _hazards.RemoveAll(h => h.Kind != RobustOfficerSpriteKind.LubePuddle);
        for (var i = 0; i < _tiles.Length; i++)
        {
            if (_tiles[i] == RobustOfficerTile.ExitClosed)
                _tiles[i] = RobustOfficerTile.ExitOpen;
        }
    }

    private void AddPoof(Vector2 position)
    {
        _poofs.Add(new Poof { Id = NextId(), Position = position, Life = 0.3f });
    }

    private void AgePoofs(float dt)
    {
        foreach (var poof in _poofs)
        {
            poof.Life -= dt;
        }

        _poofs.RemoveAll(p => p.Life <= 0);
    }

    private static bool MoveBody(ref Vector2 position, Vector2 delta, float radius, Func<int, int, bool> blocked)
    {
        return RobustOfficerCollision.MoveBody(ref position, delta, radius, blocked);
    }

    private static bool Overlaps(Vector2 position, float radius, Func<int, int, bool> blocked)
    {
        return RobustOfficerCollision.Overlaps(position, radius, blocked);
    }

    private bool BlocksPlayerAt(int x, int y) => RobustOfficerGrid.BlocksPlayer(this[x, y]);

    private bool BlocksEnemyAt(int x, int y) => RobustOfficerGrid.BlocksEnemy(this[x, y]) || BarrierAt(x, y);

    private bool BlocksGhostAt(int x, int y) => RobustOfficerGrid.BlocksGhost(this[x, y]);

    private bool BarrierAt(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height && _barrier[y * Width + x] > 0;

    private static Vector2i Tile(Vector2 position) => new((int) MathF.Floor(position.X), (int) MathF.Floor(position.Y));

    private static Vector2 Center(Vector2i tile) => new(tile.X + 0.5f, tile.Y + 0.5f);

    private static Vector2 Rotate(Vector2 v, float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    private ushort NextId()
    {
        _nextId++;
        if (_nextId <= PlayerId || _nextId >= BarrierIdBase)
            _nextId = PlayerId + 1;
        return _nextId;
    }

    public RobustOfficerSprite[] GetSprites()
    {
        var sprites = new List<RobustOfficerSprite>();

        for (var i = 0; i < _barrier.Length; i++)
        {
            if (_barrier[i] > 0)
                sprites.Add(Sprite((ushort) (BarrierIdBase + i), RobustOfficerSpriteKind.Barrier, Center(new Vector2i(i % Width, i / Width)),
                    _barrier[i] < 2f ? RobustOfficerSpriteFlags.Blink : RobustOfficerSpriteFlags.None));
        }

        foreach (var hazard in _hazards)
        {
            sprites.Add(Sprite(hazard.Id, hazard.Kind, hazard.Position, hazard.Life < 1.5f && hazard.Kind != RobustOfficerSpriteKind.Rift ? RobustOfficerSpriteFlags.Blink : RobustOfficerSpriteFlags.None));
        }

        foreach (var pickup in _pickups)
        {
            sprites.Add(Sprite(pickup.Id, (RobustOfficerSpriteKind) ((int) RobustOfficerSpriteKind.PickupCredit + (int) pickup.Type), pickup.Position,
                pickup.Life < 3f && !ExitOpen ? RobustOfficerSpriteFlags.Blink : RobustOfficerSpriteFlags.None));
        }

        foreach (var ally in _allies)
        {
            sprites.Add(Sprite(ally.Id, ally.Kind, ally.Position, ally.Life < 3f ? RobustOfficerSpriteFlags.Blink : RobustOfficerSpriteFlags.None));
        }

        foreach (var enemy in _enemies)
        {
            var flags = RobustOfficerSpriteFlags.None;
            if (enemy.Hurt > 0)
                flags |= RobustOfficerSpriteFlags.Hurt;
            if (enemy.Phasing)
                flags |= RobustOfficerSpriteFlags.Phasing;
            if (enemy.Stun > 0)
                flags |= RobustOfficerSpriteFlags.Stunned;
            if (enemy.Telegraph)
                flags |= RobustOfficerSpriteFlags.Telegraph;
            sprites.Add(Sprite(enemy.Id, enemy.Kind, enemy.Position, flags, DirectionOf(enemy.LastMove)));
        }

        if (!Dead)
        {
            var kind = (RobustOfficerSpriteKind) ((int) RobustOfficerSpriteKind.PlayerCadet + (int) _run.Rank);
            sprites.Add(Sprite(PlayerId, kind, PlayerPosition,
                _invulnerable > 0 ? RobustOfficerSpriteFlags.Blink : RobustOfficerSpriteFlags.None, DirectionOf(_facing)));
        }

        foreach (var bullet in _bullets)
        {
            sprites.Add(Sprite(bullet.Id, bullet.Kind, bullet.Position, RobustOfficerSpriteFlags.None, DirectionOf(bullet.Velocity)));
        }

        foreach (var poof in _poofs)
        {
            sprites.Add(Sprite(poof.Id, RobustOfficerSpriteKind.Poof, poof.Position));
        }

        return sprites.ToArray();
    }

    /// <summary>
    /// 0 south, 1 north, 2 east, 3 west.
    /// </summary>
    private static byte DirectionOf(Vector2 dir)
    {
        if (MathF.Abs(dir.X) > MathF.Abs(dir.Y))
            return (byte) (dir.X > 0 ? 2 : 3);
        return (byte) (dir.Y < 0 ? 1 : 0);
    }

    private static RobustOfficerSprite Sprite(ushort id, RobustOfficerSpriteKind kind, Vector2 position,
        RobustOfficerSpriteFlags flags = RobustOfficerSpriteFlags.None, byte direction = 0)
    {
        return new RobustOfficerSprite
        {
            Id = id,
            Kind = kind,
            X = (short) MathF.Round(position.X * 16f),
            Y = (short) MathF.Round(position.Y * 16f),
            Direction = direction,
            Flags = flags,
        };
    }

    public (int Health, int MaxHealth, RobustOfficerBoss Boss)? BossHealth()
    {
        foreach (var enemy in _enemies)
        {
            if (enemy.Boss is { } boss)
                return (Math.Max(0, enemy.Health), enemy.MaxHealth, boss);
        }

        return null;
    }

    private sealed class Enemy
    {
        public ushort Id;
        public RobustOfficerEnemy Type;
        public RobustOfficerBoss? Boss;
        public RobustOfficerSpriteKind Kind;
        public Vector2 Position;
        public Vector2 LastMove = new(0, 1);
        public int Health;
        public int MaxHealth;
        public int Value;
        public float Speed;
        public float Radius;
        public float Hurt;
        public float Stun;
        public bool Phasing;
        public bool Telegraph;
        public bool Dead;

        /// <summary>
        /// Which way it circles the officer, -1 or 1.
        /// </summary>
        public int Side;

        public int State;
        public float StateTimer;
        public float Timer1;
        public float Timer2;
        public float Timer3;
        public int Burst;
        public Vector2 Dash;
        public Vector2 Wander;

        public Vector2 Slide;
        public float SlideTime;
        public float SlipImmunity;
    }

    private sealed class Bullet
    {
        public ushort Id;
        public RobustOfficerSpriteKind Kind;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Life;
        public int Damage;
        public bool Hostile;
        public bool Pierce;
        public bool Dead;

        /// <summary>
        /// Enemies a piercing bullet already went through.
        /// </summary>
        public HashSet<ushort>? Hit;
    }

    private sealed class Pickup
    {
        public ushort Id;
        public RobustOfficerPickup Type;
        public Vector2 Position;
        public float Life;
        public bool Dead;
    }

    private sealed class Hazard
    {
        public ushort Id;
        public RobustOfficerSpriteKind Kind;
        public Vector2 Position;
        public float Life;
        public bool Dead;
    }

    private sealed class Ally
    {
        public ushort Id;
        public RobustOfficerSpriteKind Kind;
        public Vector2 Position;
        public float Life;
        public float Range;
        public float Cooldown;
        public float Timer;
    }

    private sealed class Poof
    {
        public ushort Id;
        public Vector2 Position;
        public float Life;
    }

    private readonly record struct EnemyStats(RobustOfficerSpriteKind Kind, int Health, float Speed, float Radius, int Value)
    {
        public static EnemyStats Of(RobustOfficerEnemy type)
        {
            return type switch
            {
                RobustOfficerEnemy.Clown => new(RobustOfficerSpriteKind.Clown, 1, 3.0f, 0.3f, 15),
                RobustOfficerEnemy.Zombie => new(RobustOfficerSpriteKind.Zombie, 3, 1.4f, 0.34f, 25),
                RobustOfficerEnemy.Slime => new(RobustOfficerSpriteKind.Slime, 2, 1.9f, 0.36f, 15),
                RobustOfficerEnemy.SlimeSmall => new(RobustOfficerSpriteKind.SlimeSmall, 1, 2.8f, 0.25f, 5),
                RobustOfficerEnemy.Xeno => new(RobustOfficerSpriteKind.Xeno, 1, 3.4f, 0.32f, 15),
                RobustOfficerEnemy.Revenant => new(RobustOfficerSpriteKind.Revenant, 2, 1.9f, 0.34f, 30),
                RobustOfficerEnemy.Carp => new(RobustOfficerSpriteKind.Carp, 2, 4.8f, 0.32f, 25),
                RobustOfficerEnemy.Nukie => new(RobustOfficerSpriteKind.Nukie, 3, 2.0f, 0.32f, 40),
                _ => new(RobustOfficerSpriteKind.Greytider, 1, 2.2f, 0.32f, 10),
            };
        }
    }
}
