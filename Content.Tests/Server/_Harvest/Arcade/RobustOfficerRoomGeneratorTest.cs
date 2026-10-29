using System;
using System.Linq;
using Content.Server._Harvest.Arcade.RobustOfficer;
using Content.Shared._Harvest.Arcade.RobustOfficer;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Server._Harvest.Arcade;

[TestFixture]
public sealed class RobustOfficerRoomGeneratorTest
{
    [Test]
    public void EveryFourthRoomIsABoss()
    {
        Assert.That(RobustOfficerRoomGenerator.BossAt(1), Is.Null);
        Assert.That(RobustOfficerRoomGenerator.BossAt(4), Is.EqualTo(RobustOfficerBoss.AssistantKing));
        Assert.That(RobustOfficerRoomGenerator.BossAt(8), Is.EqualTo(RobustOfficerBoss.PatientZero));
        Assert.That(RobustOfficerRoomGenerator.BossAt(20), Is.EqualTo(RobustOfficerBoss.NukieCommander));
        Assert.That(RobustOfficerRoomGenerator.BossAt(24), Is.EqualTo(RobustOfficerBoss.AssistantKing));

        // The nth boss met has n times its health.
        Assert.That(RobustOfficerRoomGenerator.Generate(4, 1).BossHealthMultiplier, Is.EqualTo(1));
        Assert.That(RobustOfficerRoomGenerator.Generate(12, 1).BossHealthMultiplier, Is.EqualTo(3));
    }

    [Test]
    public void GeneratedRoomsArePlayable()
    {
        for (var depth = 1; depth <= 60; depth++)
        {
            for (var seed = 0; seed < 5; seed++)
            {
                var room = RobustOfficerRoomGenerator.Generate(depth, seed * 7919 + depth);
                var error = RobustOfficerGrid.Validate(room.Map, room.Boss != null);
                Assert.That(error, Is.Null, $"depth {depth}, seed {seed}: {error}\n{string.Join('\n', room.Map)}");
                Assert.That(room.Boss != null || room.Waves.Count > 0, $"depth {depth}: no enemies");
            }
        }
    }

    [Test]
    public void HealthKeepsRisingWavesStopGrowing()
    {
        Assert.That(RobustOfficerRoomGenerator.Generate(1, 1).HealthMultiplier, Is.EqualTo(1f).Within(0.001f));
        Assert.That(RobustOfficerRoomGenerator.Generate(20, 1).HealthMultiplier, Is.EqualTo(4.8f).Within(0.001f));

        var at15 = RobustOfficerRoomGenerator.Generate(15, 1);
        var at35 = RobustOfficerRoomGenerator.Generate(35, 1);
        Assert.That(at35.SpawnRate, Is.EqualTo(at15.SpawnRate));
        Assert.That(at35.Waves.Select(w => (w.Interval, w.Group)), Is.EqualTo(at15.Waves.Select(w => (w.Interval, w.Group))));
    }

    [Test]
    public void SameSeedSameRoom()
    {
        var a = RobustOfficerRoomGenerator.Generate(7, 42);
        var b = RobustOfficerRoomGenerator.Generate(7, 42);
        Assert.That(a.Map, Is.EqualTo(b.Map));
        Assert.That(a.HealthMultiplier, Is.GreaterThan(RobustOfficerRoomGenerator.Generate(1, 42).HealthMultiplier));
    }

    /// <summary>
    /// Random play through the first rooms, bosses included, never throws and ends somewhere sensible.
    /// </summary>
    [Test]
    public void RandomPlay()
    {
        var random = new Random(3);
        for (var depth = 1; depth <= 20; depth++)
        {
            var run = new RobustOfficerRun(99) { Rank = (RobustOfficerRank) (depth / 5), Stocked = RobustOfficerPickup.Lube };
            var sim = new RobustOfficerSimulation(RobustOfficerRoomGenerator.Generate(depth, depth), run, depth);
            for (var i = 0; i < 1200; i++)
            {
                if (i % 10 == 0)
                {
                    sim.Move = new Vector2i(random.Next(3) - 1, random.Next(3) - 1);
                    sim.Shoot = new Vector2i(random.Next(3) - 1, random.Next(3) - 1);
                    sim.UseItem = random.Next(20) == 0;
                }

                sim.Tick();
                if (sim.Dead)
                    sim.Respawn();

                Assert.That(sim.PlayerPosition.X, Is.InRange(0f, sim.Width));
                Assert.That(sim.PlayerPosition.Y, Is.InRange(0f, sim.Height));
                Assert.That(sim.EnemyCount, Is.LessThanOrEqualTo(RobustOfficerSimulation.MaxEnemies));
            }

            Assert.That(sim.GetSprites(), Is.Not.Empty);
        }
    }
}
