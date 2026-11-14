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

    /// <summary>Distinct upstream combinations that reached this exact stock.</summary>
    public int CaseCount { get; init; } = 1;
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
            var route = string.IsNullOrEmpty(routeCondition) ? null
                : new ForecastChoiceFact("route", "", routeCondition, routeCondition, [],
                    Closed: false, Kind: ForecastChoiceKind.Route);
            if (paths.Count == 0)
                yield return new(Condition(condition), stock, routeCount * weight) { Facts = route is null ? null : [route] };
            else
                foreach (var path in paths)
                    yield return new(Condition(path.Condition), stock, routeCount)
                    {
                        Facts = route is null ? path.Facts : [route, .. path.Facts]
                    };
        }

        string Condition(string? choice) => string.IsNullOrEmpty(routeCondition) ? choice ?? ""
            : string.IsNullOrEmpty(choice) ? routeCondition : $"{routeCondition}（{choice}）";
    }

    internal static IReadOnlyList<MerchantStockGroup> Group(IEnumerable<MerchantForecastCase> cases)
    {
        var keys = new Dictionary<MerchantInventoryForecast, string>(ReferenceEqualityComparer.Instance);
        var groups = cases.GroupBy(item => Key(item.Inventory), StringComparer.Ordinal)
            .Select(group => new MerchantStockGroup(group.First().Inventory,
                group.Select(item => item.Condition).Where(condition => condition.Length > 0)
                    .Distinct(StringComparer.Ordinal).ToArray(),
                group.Sum(item => (long)item.Weight))
            {
                Terms = ForecastChoiceConditionFormatter.Reduce(group.Select(item => item.Facts
                    ?? (item.Condition.Length == 0 ? [] : new ForecastChoiceFact[]
                        { new("legacy", "", item.Condition, item.Condition, [], Closed: false) }))),
                CaseCount = group.Count()
            })
            .OrderByDescending(group => group.Weight).ToArray();
        // One precondition repeated in every block is noise; a route label answers
        // "how did I get here", not "why is this stock different". Only the choices
        // that actually moved the RNG belong in the condition.
        var terms = groups.SelectMany(group => group.Terms).ToArray();
        ForecastChoiceConditionFormatter.UnifySharedCells(terms);
        ForecastChoiceConditionFormatter.DropIncidentalRoutes(terms);
        return groups;

        string Key(MerchantInventoryForecast inventory)
        {
            if (!keys.TryGetValue(inventory, out var key))
                keys[inventory] = key = ForecastContentKey.MerchantStock(inventory);
            return key;
        }
    }
}
