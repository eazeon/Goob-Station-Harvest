using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._Harvest.Arcade.RobustOfficer;
using Content.Shared._Harvest.Arcade.RobustOfficer;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Server._Harvest.Arcade;

[TestFixture]
public sealed class RobustOfficerSimulationTest
{
    private static readonly string[] Map =
    [
        "#####D######",
        "#..........#",
        "#..........#",
        "D....P.....#",
        "#..........#",
        "#####E######",
    ];

    private static readonly Vector2 Start = new(5.5f, 3.5f);

    /// <summary>
    /// A big empty room for the bosses, the officer at the bottom.
    /// </summary>
    private static readonly string[] BossMap = Enumerable.Range(0, 15).Select(y => y switch
    {
        0 => "#########DD#########",
        14 => "##########E#########",
        12 => "#.........P........#",
        _ => "#..................#",
    }).ToArray();

    private static RobustOfficerSimulation Create(RobustOfficerRun run, int seconds = 0, RobustOfficerBoss? boss = null, float bossHealth = 1f,
        float health = 1f)
    {
        return new RobustOfficerSimulation(new RobustOfficerRoom(boss == null ? Map : BossMap, seconds, boss, new List<RobustOfficerWave>(),
            healthMultiplier: health, bossHealthMultiplier: bossHealth), run);
    }

    private const int Second = RobustOfficerSimulation.TicksPerSecond;

    private static RobustOfficerEvents Ticks(RobustOfficerSimulation sim, int count)
    {
        var events = RobustOfficerEvents.None;
        for (var i = 0; i < count; i++)
        {
            events |= sim.Tick();
        }

        return events;
    }

    [Test]
    public void PlayerStopsAtWalls()
    {
        var sim = Create(new RobustOfficerRun(3));
        sim.Move = new Vector2i(-1, 0);
        Ticks(sim, 100);
        Assert.That(sim.PlayerPosition.X, Is.EqualTo(1 + RobustOfficerSimulation.PlayerRadius).Within(0.01f));

        // Doors on the edge stop the officer too.
        sim.Move = new Vector2i(0, -1);
        Ticks(sim, 100);
        Assert.That(sim.PlayerPosition.Y, Is.EqualTo(1 + RobustOfficerSimulation.PlayerRadius).Within(0.01f));
    }

    [Test]
    public void ExitOpensOnceTheRoomIsHeld()
    {
        var sim = Create(new RobustOfficerRun(3), seconds: 1);
        Ticks(sim, Second / 2);
        Assert.That(sim.ExitOpen, Is.False);

        var events = Ticks(sim, Second);
        Assert.That(events.HasFlag(RobustOfficerEvents.ExitOpened));
        Assert.That(sim[5, 5], Is.EqualTo(RobustOfficerTile.ExitOpen));

        sim.Move = new Vector2i(0, 1);
        events = Ticks(sim, 2 * Second);
        Assert.That(events.HasFlag(RobustOfficerEvents.Exited));
        Assert.That(sim.Exited);
    }

    [Test]
    public void EnemiesKeepTheExitClosedUntilShot()
    {
        var run = new RobustOfficerRun(3);
        var sim = Create(run);
        sim.AddEnemy(RobustOfficerEnemy.Greytider, new Vector2(9.5f, 3.5f));
        Ticks(sim, 2);
        Assert.That(sim.ExitOpen, Is.False);

        sim.Shoot = new Vector2i(1, 0);
        var events = Ticks(sim, Second);
        Assert.That(events.HasFlag(RobustOfficerEvents.Shot));
        Assert.That(events.HasFlag(RobustOfficerEvents.EnemyKilled));
        Assert.That(sim.EnemyCount, Is.Zero);
        Assert.That(sim.ExitOpen);
        Assert.That(sim.Dead, Is.False);
        Assert.That(run.Score, Is.GreaterThanOrEqualTo(10));
    }

    [Test]
    public void ArmorAbsorbsAHitThenTheOfficerDies()
    {
        var run = new RobustOfficerRun(3) { Rank = RobustOfficerRank.Officer };
        var sim = Create(run, seconds: 60);
        Assert.That(sim.Armor, Is.EqualTo(1));

        sim.AddEnemy(RobustOfficerEnemy.Zombie, Start + new Vector2(0.4f, 0));
        var events = Ticks(sim, 2);
        Assert.That(events.HasFlag(RobustOfficerEvents.ArmorHit));
        Assert.That(sim.Armor, Is.Zero);
        Assert.That(run.Lives, Is.EqualTo(3));
        Assert.That(sim.Dead, Is.False);

        // Invulnerable for a moment, then the zombie gets through.
        events = Ticks(sim, 2 * Second);
        Assert.That(events.HasFlag(RobustOfficerEvents.PlayerDied));
        Assert.That(sim.Dead);
        Assert.That(run.Lives, Is.EqualTo(2));
    }

    [Test]
    public void RespawnClearsTheRoom()
    {
        var run = new RobustOfficerRun(3);
        var sim = Create(run, seconds: 60);
        sim.AddEnemy(RobustOfficerEnemy.Greytider, Start + new Vector2(0.3f, 0));
        sim.Move = new Vector2i(1, 0);
        Ticks(sim, 3);
        Assert.That(sim.Dead);

        sim.Respawn();
        Assert.That(sim.Dead, Is.False);
        Assert.That(sim.EnemyCount, Is.Zero);
        Assert.That(sim.PlayerPosition, Is.EqualTo(Start));
        Assert.That(sim.GetSprites().Single(s => s.Kind == RobustOfficerSpriteKind.PlayerCadet).Flags,
            Is.EqualTo(RobustOfficerSpriteFlags.Blink));
    }

    [Test]
    public void RespawnBringsNoArmorBack()
    {
        var run = new RobustOfficerRun(3) { Rank = RobustOfficerRank.HeadOfSecurity };
        var sim = Create(run, seconds: 60);
        Assert.That(sim.Armor, Is.EqualTo(3));

        sim.AddEnemy(RobustOfficerEnemy.Zombie, Start);
        for (var i = 0; i < 20 * Second && !sim.Dead; i++)
        {
            sim.Tick();
        }

        Assert.That(sim.Dead);
        sim.Respawn();
        Assert.That(sim.Armor, Is.Zero);

        // Not even the head of security's shield until the next room.
        Ticks(sim, (int) (RobustOfficerSimulation.ArmorRegenSeconds + 1) * Second);
        Assert.That(sim.Armor, Is.Zero);
    }

    [Test]
    public void PickupsAreCollectedFromAFewStepsAway()
    {
        var run = new RobustOfficerRun(3);
        var sim = Create(run, seconds: 60);
        sim.AddPickup(RobustOfficerPickup.Credit, Start + new Vector2(RobustOfficerSimulation.PickupRange - 0.05f, 0));
        sim.Tick();
        Assert.That(run.Credits, Is.EqualTo(1));
    }

    /// <summary>
    /// Enemy health is rounded to the nearest: a greytider still dies in one hit at 1.4x, takes two at 1.6x.
    /// </summary>
    [TestCase(1.4f, true)]
    [TestCase(1.6f, false)]
    public void EnemyHealthIsRoundedToTheNearest(float health, bool oneHit)
    {
        var sim = Create(new RobustOfficerRun(3), seconds: 60, health: health);
        sim.AddEnemy(RobustOfficerEnemy.Greytider, new Vector2(9.5f, 3.5f));
        sim.Shoot = new Vector2i(1, 0);
        var events = RobustOfficerEvents.None;
        for (var i = 0; i < 2 * Second && !events.HasFlag(RobustOfficerEvents.EnemyHit); i++)
        {
            events = sim.Tick();
        }

        Assert.That(events.HasFlag(RobustOfficerEvents.EnemyHit));
        Assert.That(events.HasFlag(RobustOfficerEvents.EnemyKilled), Is.EqualTo(oneHit));
    }

    [Test]
    public void LaterBossesHaveMoreHealth()
    {
        // The room's enemy health multiplier leaves the boss alone.
        var sim = Create(new RobustOfficerRun(3), boss: RobustOfficerBoss.XenoQueen, bossHealth: 3, health: 5);
        Ticks(sim, (int) ((RobustOfficerSimulation.BossDelay + 0.5f) * Second));
        Assert.That(sim.BossHealth()?.MaxHealth, Is.EqualTo(RobustOfficerSimulation.BossBaseHealth(RobustOfficerBoss.XenoQueen) * 3));
    }

    [Test]
    public void SlimesSplitWhenKilled()
    {
        var sim = Create(new RobustOfficerRun(3), seconds: 60);
        sim.AddEnemy(RobustOfficerEnemy.Slime, new Vector2(9.5f, 3.5f));
        sim.Shoot = new Vector2i(1, 0);
        for (var i = 0; i < 200 && sim.Enemies.Contains(RobustOfficerEnemy.Slime); i++)
        {
            sim.Tick();
        }

        Assert.That(sim.Enemies.Count(e => e == RobustOfficerEnemy.SlimeSmall), Is.EqualTo(2));
    }

    [Test]
    public void BossRoomOpensOnceTheBossIsBeaten()
    {
        var sim = Create(new RobustOfficerRun(3), boss: RobustOfficerBoss.AssistantKing);
        Ticks(sim, Second / 2);
        Assert.That(sim.BossAlive, Is.False);
        Assert.That(sim.ExitOpen, Is.False);

        var events = Ticks(sim, (int) (RobustOfficerSimulation.BossDelay * Second));
        Assert.That(events.HasFlag(RobustOfficerEvents.BossSpawned));
        Assert.That(sim.BossHealth()?.MaxHealth, Is.EqualTo(RobustOfficerSimulation.BossBaseHealth(RobustOfficerBoss.AssistantKing)));

        sim.KillEnemies();
        Ticks(sim, 1);
        Assert.That(sim.BossAlive, Is.False);
        Assert.That(sim.ExitOpen);
    }

    [Test]
    public void TheNukeRoomHasATimer()
    {
        var sim = Create(new RobustOfficerRun(3), boss: RobustOfficerBoss.NukieCommander);
        Assert.That(sim.NukeTimer, Is.EqualTo(RobustOfficerSimulation.NukeSeconds));
        Ticks(sim, Second);
        Assert.That(sim.NukeTimer, Is.LessThan(RobustOfficerSimulation.NukeSeconds));
    }

    [Test]
    public void PickupsAreStockedOrApplied()
    {
        var run = new RobustOfficerRun(3);
        var sim = Create(run, seconds: 60);

        sim.AddPickup(RobustOfficerPickup.Flashbang, Start);
        Assert.That(Ticks(sim, 1).HasFlag(RobustOfficerEvents.Pickup));
        Assert.That(run.Stocked, Is.EqualTo(RobustOfficerPickup.Flashbang));

        // The slot is taken, so this one is used right away.
        sim.AddPickup(RobustOfficerPickup.SecBorg, Start);
        Ticks(sim, 1);
        Assert.That(run.Stocked, Is.EqualTo(RobustOfficerPickup.Flashbang));
        Assert.That(sim.GetSprites().Any(s => s.Kind == RobustOfficerSpriteKind.SecBorg));

        sim.AddPickup(RobustOfficerPickup.Credit, Start);
        sim.AddPickup(RobustOfficerPickup.CreditBundle, Start);
        sim.AddPickup(RobustOfficerPickup.Donut, Start);
        sim.AddPickup(RobustOfficerPickup.RapidFire, Start);
        Ticks(sim, 1);
        Assert.That(run.Credits, Is.EqualTo(6));
        Assert.That(run.Lives, Is.EqualTo(4));
        Assert.That(sim.Effects.ContainsKey(RobustOfficerPickup.RapidFire));

        sim.AddEnemy(RobustOfficerEnemy.Greytider, new Vector2(9.5f, 1.5f));
        sim.UseItem = true;
        Assert.That(Ticks(sim, 1).HasFlag(RobustOfficerEvents.ItemUsed));
        Assert.That(run.Stocked, Is.Null);
        Assert.That(sim.GetSprites().Single(s => s.Kind == RobustOfficerSpriteKind.Greytider).Flags.HasFlag(RobustOfficerSpriteFlags.Stunned));
    }

    [Test]
    public void HoloBarrierBlocksEnemies()
    {
        var run = new RobustOfficerRun(3) { Stocked = RobustOfficerPickup.HoloBarrier };
        var sim = Create(run, seconds: 60);
        sim.Shoot = new Vector2i(1, 0);
        sim.Tick();
        sim.Shoot = Vector2i.Zero;
        sim.UseItem = true;
        sim.Tick();
        // Five tiles across, minus the one that would be in the wall.
        Assert.That(sim.GetSprites().Count(s => s.Kind == RobustOfficerSpriteKind.Barrier), Is.EqualTo(4));

        // A zombie on the far side cannot walk through it.
        sim.AddEnemy(RobustOfficerEnemy.Zombie, new Vector2(9.5f, 3.5f));
        Ticks(sim, 2 * Second);
        var zombie = sim.GetSprites().Single(s => s.Kind == RobustOfficerSpriteKind.Zombie);
        Assert.That(zombie.X / 16f, Is.GreaterThan(8f));
    }

    [Test]
    public void WardensGetATurret()
    {
        var sim = Create(new RobustOfficerRun(3) { Rank = RobustOfficerRank.Warden }, seconds: 60);
        Assert.That(sim.GetSprites().Any(s => s.Kind == RobustOfficerSpriteKind.Turret));
        Assert.That(sim.GetSprites().Any(s => s.Kind == RobustOfficerSpriteKind.PlayerWarden));
    }

    [Test]
    public void ShopSellsUpgrades()
    {
        var run = new RobustOfficerRun(3) { Credits = 48 };
        Assert.That(run.TryBuy(RobustOfficerUpgrade.Promotion));
        Assert.That(run.Rank, Is.EqualTo(RobustOfficerRank.Officer));
        Assert.That(run.Weapon, Is.EqualTo(RobustOfficerWeapon.Pistol));
        Assert.That(run.MaxArmor, Is.EqualTo(1));
        Assert.That(run.Credits, Is.EqualTo(8));

        // Not enough credits for the next promotion.
        Assert.That(run.TryBuy(RobustOfficerUpgrade.Promotion), Is.False);
        Assert.That(run.TryBuy(RobustOfficerUpgrade.Flashbang));
        Assert.That(run.Stocked, Is.EqualTo(RobustOfficerPickup.Flashbang));
        Assert.That(run.Offer(RobustOfficerUpgrade.Flashbang).Price, Is.Null);

        run.Credits = 1000;
        while (run.TryBuy(RobustOfficerUpgrade.Promotion))
        {
        }

        Assert.That(run.Rank, Is.EqualTo(RobustOfficerRank.HeadOfSecurity));
        Assert.That(run.Offer(RobustOfficerUpgrade.Promotion).Price, Is.Null);

        var cooldown = run.Cooldown;
        Assert.That(run.TryBuy(RobustOfficerUpgrade.FireRate));
        Assert.That(run.Cooldown, Is.LessThan(cooldown));
        Assert.That(run.TryBuy(RobustOfficerUpgrade.Damage));
        Assert.That(run.BulletDamage, Is.EqualTo(RobustOfficerWeapon.EnergyGun.Damage + 1));

        // Each donut costs twice the last one.
        var donut = run.Offer(RobustOfficerUpgrade.Donut).Price!.Value;
        Assert.That(run.TryBuy(RobustOfficerUpgrade.Donut));
        Assert.That(run.Offer(RobustOfficerUpgrade.Donut).Price, Is.EqualTo(donut * 2));
        Assert.That(run.TryBuy(RobustOfficerUpgrade.Donut));
        Assert.That(run.Offer(RobustOfficerUpgrade.Donut).Price, Is.EqualTo(donut * 4));

        run.Credits = 1_000_000;
        while (run.TryBuy(RobustOfficerUpgrade.Donut))
        {
        }

        Assert.That(run.Lives, Is.EqualTo(RobustOfficerRun.MaxLives));
    }

    [Test]
    public void ShopOpensEveryOtherRoom()
    {
        Assert.That(Enumerable.Range(0, 8).Where(RobustOfficerArcadeSystem.ShopAfter), Is.EqualTo(new[] { 1, 3, 5, 7 }));
    }
}
