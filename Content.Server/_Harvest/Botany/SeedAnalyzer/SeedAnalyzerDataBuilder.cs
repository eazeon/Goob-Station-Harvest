using System.Linq;
using Content.Shared.FixedPoint;
using Content.Server.Botany;
using Content.Server.Botany.Components;
using Content.Shared._Harvest.Botany.SeedAnalyzer;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.Botany.SeedAnalyzer;

public sealed class SeedAnalyzerDataBuilder(IPrototypeManager prototypes)
{
    public SeedAnalyzerData Build(SeedComponent component, SeedData seed)
    {
        var reagents = seed.Chemicals.Select(entry =>
        {
            var (reagentId, quantity) = entry;
            var currentAmount = quantity.Min;
            if (quantity.PotencyDivisor > 0 && seed.Potency > 0)
                currentAmount += seed.Potency / quantity.PotencyDivisor;
            currentAmount = FixedPoint2.Clamp(currentAmount, quantity.Min, quantity.Max);

            return new SeedAnalyzerReagentData
            {
                ReagentId = reagentId,
                MinimumAmount = quantity.Min.Float(),
                MaximumAmount = quantity.Max.Float(),
                CurrentAmount = currentAmount.Float()
            };
        }).ToList();

        var consumedGases = seed.ConsumeGasses.Select(entry => new SeedAnalyzerGasData
        {
            GasId = entry.Key.ToString(),
            Amount = entry.Value
        }).ToList();

        var exudedGasCount = seed.ExudeGasses.Count;
        var producedGases = seed.ExudeGasses.Select(entry => new SeedAnalyzerGasData
        {
            GasId = entry.Key.ToString(),
            Amount = CalculateExudedGas(seed.Potency, entry.Value, exudedGasCount)
        }).ToList();

        var products = seed.ProductPrototypes.Select(product => new SeedAnalyzerProductData
        {
            PrototypeId = product.Id
        }).ToList();

        return new SeedAnalyzerData
        {
            DisplayName = seed.DisplayName,
            Yield = seed.Yield,
            Potency = seed.Potency,
            Endurance = seed.Endurance,
            Maturation = seed.Maturation,
            Production = seed.Production,
            Lifespan = seed.Lifespan,
            WaterConsumption = seed.WaterConsumption,
            NutrientConsumption = seed.NutrientConsumption,
            MinimumTemperature = seed.IdealHeat - seed.HeatTolerance,
            MaximumTemperature = seed.IdealHeat + seed.HeatTolerance,
            MinimumPressure = seed.LowPressureTolerance,
            MaximumPressure = seed.HighPressureTolerance,
            Viable = seed.Viable,
            Seedless = seed.Seedless,
            Ligneous = seed.Ligneous,
            ToxinTolerance = seed.ToxinsTolerance,
            PestTolerance = seed.PestTolerance,
            WeedTolerance = seed.WeedTolerance,
            HarvestType = seed.HarvestRepeat switch
            {
                HarvestType.Repeat => SeedAnalyzerHarvestType.Repeat,
                HarvestType.SelfHarvest => SeedAnalyzerHarvestType.SelfHarvest,
                _ => SeedAnalyzerHarvestType.Single
            },
            Reagents = reagents,
            ConsumedGases = consumedGases,
            ProducedGases = producedGases,
            Products = products,
            Baseline = FindBaseline(component, seed)
        };
    }

    private SeedAnalyzerBaselineData? FindBaseline(SeedComponent component, SeedData seed)
    {
        SeedPrototype? prototype = null;
        if (component.SeedId != null)
            prototypes.TryIndex(component.SeedId, out prototype);

        prototype ??= prototypes.EnumeratePrototypes<SeedPrototype>()
            .FirstOrDefault(candidate => candidate.Name == seed.Name);

        if (prototype == null)
            return null;

        return new SeedAnalyzerBaselineData
        {
            Yield = prototype.Yield,
            Potency = prototype.Potency,
            Endurance = prototype.Endurance,
            Maturation = prototype.Maturation,
            Production = prototype.Production,
            Lifespan = prototype.Lifespan,
            WaterConsumption = prototype.WaterConsumption,
            NutrientConsumption = prototype.NutrientConsumption,
            MinimumTemperature = prototype.IdealHeat - prototype.HeatTolerance,
            MaximumTemperature = prototype.IdealHeat + prototype.HeatTolerance,
            MinimumPressure = prototype.LowPressureTolerance,
            MaximumPressure = prototype.HighPressureTolerance,
            Viable = prototype.Viable,
            Seedless = prototype.Seedless,
            Ligneous = prototype.Ligneous,
            ToxinTolerance = prototype.ToxinsTolerance,
            PestTolerance = prototype.PestTolerance,
            WeedTolerance = prototype.WeedTolerance,
            HarvestType = prototype.HarvestRepeat switch
            {
                HarvestType.Repeat => SeedAnalyzerHarvestType.Repeat,
                HarvestType.SelfHarvest => SeedAnalyzerHarvestType.SelfHarvest,
                _ => SeedAnalyzerHarvestType.Single
            }
        };
    }

    private static float CalculateExudedGas(float potency, float amount, int gasCount)
    {
        if (gasCount == 0)
            return 0f;

        return MathF.Max(1f, MathF.Round(amount * MathF.Round(potency) / gasCount));
    }
}
