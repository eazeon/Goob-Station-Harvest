// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Shared.Radio;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Harvest.AndroidNpc;

/// <summary>
/// Validates the android YAML data: every referenced prototype exists, every pattern compiles and every job
/// has what its behavior tree needs.
/// </summary>
[TestFixture]
public sealed class AndroidNpcConfigTests
{
    [Test]
    public async Task GlobalConfig_IsLoadedWithSaneValues()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var protos = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            Assert.That(protos.HasIndex<AndroidNpcConfigPrototype>("Default"), Is.True);
            Assert.That(protos.HasIndex<AndroidOrderFeedbackPrototype>("en"), Is.True);
            Assert.That(protos.HasIndex<AndroidOrderFeedbackPrototype>("fr"), Is.True);
            Assert.That(protos.HasIndex<AndroidOrderPhrasesPrototype>("Default"), Is.True);
            Assert.That(protos.HasIndex<AndroidIntentRulesPrototype>("Default"), Is.True);

            var config = protos.AndroidConfig();
            Assert.Multiple(() =>
            {
                Assert.That(config.MaxAndroids, Is.GreaterThan(0));
                Assert.That(config.HearingRange, Is.GreaterThan(0));
                Assert.That(config.ResupplyThreshold, Is.InRange(0f, 1f));
                Assert.That(config.ClaimTtlSeconds, Is.GreaterThan(0f));
                Assert.That(config.ConversationMinConfidence, Is.LessThanOrEqualTo(config.ConversationEffectMinConfidence));
                Assert.That(config.ConversationThreatThreshold, Is.InRange(0f, 1f));
                Assert.That(protos.HasIndex<RadioChannelPrototype>(config.SecurityRadioChannel), Is.True);
                Assert.That(protos.HasIndex<RadioChannelPrototype>(config.EmergencyRadioChannel), Is.True);
            });

            var customer = protos.AndroidFeedback().Customer;
            Assert.That(config.CustomerDrinks.Concat(config.CustomerFood).All(p => protos.HasIndex(p)), Is.True);
            Assert.That(new[] { customer.BarOrder, customer.KitchenOrder, customer.BarReminder, customer.KitchenReminder, customer.Grumpy, customer.Thanks },
                Has.All.Not.Empty);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EveryJob_HasValidPrototypesLinesAndTree()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var protos = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var trees = server.System<AndroidSystem>().Trees;

            foreach (var job in Enum.GetValues<AndroidJob>())
            {
                var data = protos.AndroidJobData(job);
                Assert.That(data, Is.Not.Null, $"No androidJob for {job}");
                Assert.That(trees.ContainsKey(job), Is.True, $"No behavior tree for {job}");

                Assert.Multiple(() =>
                {
                    Assert.That(protos.HasIndex(data!.EntityPrototype), Is.True, $"{job} entity");
                    Assert.That(data.StationJob is not { } stationJob || protos.HasIndex<JobPrototype>(stationJob), Is.True, $"{job} station job");
                    Assert.That(protos.HasIndex<RadioChannelPrototype>(data.RadioChannel), Is.True, $"{job} radio channel");
                    Assert.That(data.RequiredLoadout.All(protos.HasIndex<EntityPrototype>), Is.True, $"{job} loadout");
                    Assert.That(data.Menu.All(m => protos.HasIndex(m.Item)), Is.True, $"{job} menu");
                    Assert.That(data.Menu.SelectMany(m => m.Patterns).All(Compiles), Is.True, $"{job} menu patterns");
                    foreach (var lang in new[] { "en", "fr" })
                    {
                        Assert.That(data.Lines.ContainsKey(lang), Is.True, $"{job} has no {lang} lines");
                        var lines = data.LinesFor(lang)!;
                        Assert.That(lines.Break, Is.Not.Empty, $"{job} {lang} break lines");
                        Assert.That(lines.Flee, Is.Not.Empty, $"{job} {lang} flee lines");
                        Assert.That(lines.Despawn, Is.Not.Empty, $"{job} {lang} despawn lines");
                        Assert.That(lines.Idle, Is.Not.Empty, $"{job} {lang} idle lines");
                    }
                    Assert.That(data.AddressKeywords, Is.Not.Empty, $"{job} address keywords");
                });

                if (job is AndroidJob.Cook or AndroidJob.Bartender)
                {
                    Assert.That(data!.Menu, Is.Not.Empty, $"{job} is a service job without a menu");
                    Assert.That(data.LinesFor("fr")!.Preparing, Is.Not.Empty);
                    Assert.That(data.LinesFor("fr")!.Served, Is.Not.Empty);
                }

                if (job == AndroidJob.Paramedic)
                {
                    Assert.That(data!.RequiredLoadout, Does.Not.Contain("HandheldCrewMonitor"), "No crew monitor to loot from a dead paramedic");
                    foreach (var lang in new[] { "en", "fr" })
                    {
                        var lines = data.LinesFor(lang)!;
                        Assert.That(lines.Treating.Concat(lines.Treated).Concat(lines.Stabilizing).Concat(lines.Carrying), Is.Not.Empty, $"{lang} care lines");
                    }
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ConversationPatterns_AllCompile()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var protos = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var rules = protos.Index<AndroidIntentRulesPrototype>("Default");
            var patterns = rules.Keywords.SelectMany(k => k.Patterns)
                .Concat(rules.Tone.SelectMany(t => t.Patterns))
                .Concat(rules.Languages.SelectMany(l => l.Hints))
                .Concat(protos.EnumeratePrototypes<AndroidConversationPrototype>().SelectMany(c => c.Supplies.SelectMany(s => s.Patterns)))
                .ToList();

            Assert.That(patterns, Is.Not.Empty);
            Assert.That(patterns.Where(p => !Compiles(p)), Is.Empty, "Invalid regex patterns");

            foreach (var conversation in protos.EnumeratePrototypes<AndroidConversationPrototype>())
            {
                Assert.That(conversation.Supplies.SelectMany(s => s.Items).All(protos.HasIndex<EntityPrototype>), Is.True,
                    $"{conversation.ID} supplies");
            }
        });

        await pair.CleanReturnAsync();
    }

    private static bool Compiles(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
