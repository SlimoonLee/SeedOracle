using SeedOracle.Forecasting;
using Cell = SeedOracle.UI.ForecastChoiceConditionFormatter.Cell;
using Term = SeedOracle.UI.ForecastChoiceConditionFormatter.Term;

namespace SeedOracle.UI;

// Factor prerequisites at the scope where they actually apply: all results, or
// one route. Different routes need not have the same prior reward decisions.
internal sealed class ForecastConditionDisplay
{
    private readonly IReadOnlyDictionary<string, Cell> common;
    private readonly bool chinese;
    private readonly IReadOnlyList<ForecastShopChoiceScope> shops;
    private readonly Dictionary<string, (string Label, IReadOnlyDictionary<string, Cell> Conditions)> routes = new();

    internal ForecastConditionDisplay(IEnumerable<Term> source, bool chinese)
    {
        this.chinese = chinese;
        var terms = source.ToArray();
        shops = terms.SelectMany(term => term.Cells.Values).Select(cell => cell.Fact.Shop)
            .OfType<ForecastShopChoiceScope>().DistinctBy(shop => shop.Key).ToArray();
        common = ForecastChoiceConditionFormatter.Common(terms);
        foreach (var route in terms.SelectMany(term => term.Cells.Values
                     .Where(cell => cell.Fact.Kind == ForecastChoiceKind.Route)
                     .SelectMany(cell => cell.Values.Select(value => (value.Key, value.Value, Term: term))))
                     .GroupBy(item => item.Key, StringComparer.Ordinal))
        {
            var scoped = ForecastChoiceConditionFormatter.Common(route.Select(item => item.Term))
                .Where(pair => !common.ContainsKey(pair.Key) && pair.Value.Fact.Kind != ForecastChoiceKind.Route)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (scoped.Count > 0)
                routes[route.Key] = (route.First().Value, scoped);
        }
    }

    internal void AppendPrerequisites(List<string> lines, bool chinese, string prefix)
    {
        AppendScope("", common.Values);
        foreach (var route in routes.Values)
            AppendScope(route.Label, route.Conditions.Values);
        foreach (var shop in shops)
        {
            var unavailable = shop.Offers.Where(offer => offer.Availability == ShopOfferAvailability.Unaffordable).ToArray();
            if (unavailable.Length > 0)
                lines.Add(prefix + (chinese ? $"{shop.Floor}层预算{shop.Gold}金；买不起：" : $"{shop.Floor}F budget {shop.Gold}g; unaffordable: ")
                    + string.Join(chinese ? "、" : ", ", unavailable.GroupBy(offer => offer.Item.Item.Kind).SelectMany(group =>
                        group.Count() > 1 && group.Key == ForecastItemKind.Relic
                            ? new[] { chinese ? $"{group.Count()}件遗物（最低{group.Min(offer => offer.Item.Cost)}金）"
                                : $"{group.Count()} relics (from {group.Min(offer => offer.Item.Cost)}g)" }
                            : group.Select(offer => ForecastItemFormatter.Format(offer.Item.Item, false) + offer.Item.Cost + (chinese ? "金" : "g")))));
            if (shop.Offers.Any(offer => offer.Availability == ShopOfferAvailability.PotionSlotsFull))
                lines.Add(prefix + (chinese ? $"{shop.Floor}层药水栏满，购药需先腾位。" : $"{shop.Floor}F potion slots full; free a slot before buying."));
            var pending = shop.Offers.Where(offer => shop.UncalculatedValues.Contains(offer.Value)).ToArray();
            if (pending.Length > 0)
                lines.Add(prefix + (chinese ? $"{shop.Floor}层未计算购买：" : $"{shop.Floor}F purchases not evaluated: ")
                    + string.Join(chinese ? "、" : ", ", pending.Select(offer => ForecastItemFormatter.Format(offer.Item.Item, false))));
        }

        void AppendScope(string scope, IEnumerable<Cell> source)
        {
            var cells = source.ToArray();
            var freedom = ForecastChoiceConditionFormatter.FormatCells(cells.Where(ForecastChoiceConditionFormatter.IsChoiceFreedom), chinese);
            if (freedom.Length > 0)
                lines.Add(prefix + (scope.Length == 0 ? "" : scope + (chinese ? "：" : ": ")) + freedom);
            var required = ForecastChoiceConditionFormatter.FormatCells(cells.Where(cell => !ForecastChoiceConditionFormatter.IsChoiceFreedom(cell)), chinese);
            if (required.Length > 0)
                lines.Add(prefix + (scope.Length == 0 ? chinese ? "前提：" : "Requires: "
                    : scope + (chinese ? "前提：" : " requires: ")) + required);
        }
    }

    internal string Format(IEnumerable<Term> source)
    {
        var terms = source.Select(term =>
        {
            var cells = new Dictionary<string, Cell>(term.Cells, StringComparer.Ordinal);
            foreach (var key in common.Keys)
                cells.Remove(key);
            var routeKeys = term.Cells.Values.Where(cell => cell.Fact.Kind == ForecastChoiceKind.Route)
                .SelectMany(cell => cell.Values.Keys).ToArray();
            if (routeKeys.Length > 0 && routeKeys.All(routes.ContainsKey))
                foreach (var pair in routes[routeKeys[0]].Conditions)
                    if (routeKeys.All(route => routes[route].Conditions.TryGetValue(pair.Key, out var other)
                        && pair.Value.Values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(other.Values.Keys)))
                        cells.Remove(pair.Key);
            return new Term(cells);
        });
        return ForecastChoiceConditionFormatter.Format(terms, chinese);
    }
}
