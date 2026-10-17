using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal sealed record MerchantForecastCase(string Condition, MerchantInventoryForecast Inventory, int Weight)
{
    public IReadOnlyList<ForecastChoiceFact>? Facts { get; init; }
}

internal sealed record MerchantStockGroup(
    MerchantInventoryForecast Inventory, IReadOnlyList<string> Conditions, long Weight)
{
    public IReadOnlyList<ForecastChoiceConditionFormatter.Term> Terms { get; init; } = [];
}

internal static class MerchantForecastSummary
{
    internal static IEnumerable<MerchantForecastCase> Cases(
        MerchantInventoryForecast inventory, string routeCondition = "", int routeCount = 1)
    {
        foreach (var item in Expand(inventory.ReferenceCondition, inventory.ReferenceChoicePaths,
                     inventory, inventory.ReferenceCaseCount))
            yield return item;
        foreach (var branch in inventory.ChoiceBranches)
            foreach (var item in Expand(branch.Condition, branch.ChoicePaths, branch.Inventory, branch.CaseCount))
                yield return item;

        IEnumerable<MerchantForecastCase> Expand(string? condition, IReadOnlyList<ForecastChoicePath> paths,
            MerchantInventoryForecast stock, int weight)
        {
            if (paths.Count == 0)
                yield return new(Condition(condition), stock, routeCount * weight);
            else
                foreach (var path in paths)
                    yield return new(Condition(path.Condition), stock, routeCount)
                    {
                        Facts = string.IsNullOrEmpty(routeCondition) ? path.Facts :
                            [new("route", "", routeCondition, routeCondition, [], Closed: false, Kind: ForecastChoiceKind.Route),
                                .. path.Facts]
                    };
        }

        string Condition(string? choice) => string.IsNullOrEmpty(routeCondition) ? choice ?? ""
            : string.IsNullOrEmpty(choice) ? routeCondition : $"{routeCondition}（{choice}）";
    }

    internal static IReadOnlyList<MerchantStockGroup> Group(IEnumerable<MerchantForecastCase> cases)
    {
        var keys = new Dictionary<MerchantInventoryForecast, string>(ReferenceEqualityComparer.Instance);
        return cases.GroupBy(item => Key(item.Inventory), StringComparer.Ordinal)
            .Select(group => new MerchantStockGroup(group.First().Inventory,
                group.Select(item => item.Condition).Where(condition => condition.Length > 0)
                    .Distinct(StringComparer.Ordinal).ToArray(),
                group.Sum(item => (long)item.Weight))
            {
                Terms = ForecastChoiceConditionFormatter.Reduce(group.Select(item => item.Facts
                    ?? (item.Condition.Length == 0 ? [] : new ForecastChoiceFact[]
                        { new("legacy", "", item.Condition, item.Condition, [], Closed: false) })))
            })
            .OrderByDescending(group => group.Weight).ToArray();

        string Key(MerchantInventoryForecast inventory)
        {
            if (!keys.TryGetValue(inventory, out var key))
                keys[inventory] = key = ForecastContentKey.MerchantStock(inventory);
            return key;
        }
    }
}
