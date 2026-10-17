using System.Text.Json;
using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal static class ForecastChoiceConditionFormatter
{
    internal static IEnumerable<IReadOnlyList<ForecastChoiceFact>> Paths(
        string? condition, IReadOnlyList<ForecastChoicePath> paths) => paths.Count > 0
        ? paths.Select(path => path.Facts)
        : [string.IsNullOrEmpty(condition) ? [] : new ForecastChoiceFact[]
            { new("legacy", "", condition, condition, [], Closed: false) }];

    internal sealed record Cell(ForecastChoiceFact Fact, IReadOnlyDictionary<string, string> Values);
    internal sealed class Term(Dictionary<string, Cell> cells)
    {
        internal Dictionary<string, Cell> Cells { get; } = cells;
    }

    internal static IReadOnlyList<Term> Reduce(IEnumerable<IReadOnlyList<ForecastChoiceFact>> paths)
    {
        var terms = paths.Select(path => new Term(path.GroupBy(Column).ToDictionary(group => group.Key,
            group => new Cell(group.First(), group.SelectMany(fact => fact.EquivalentValues.Count == 0
                    ? new[] { KeyValuePair.Create(fact.Value, fact.ValueLabel) }
                    : fact.EquivalentValues.Select(value => KeyValuePair.Create(value,
                        value == fact.Value ? fact.ValueLabel : "")))
                .DistinctBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.Ordinal)), StringComparer.Ordinal))).ToList();
        foreach (var term in terms)
            Normalize(term);
        terms = terms.DistinctBy(term => Signature(term)).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var key in terms.SelectMany(term => term.Cells.Keys).Distinct().ToArray())
            {
                var groups = terms.Where(term => term.Cells.ContainsKey(key))
                    .GroupBy(term => Signature(term, key)).Where(group => group.Count() > 1).ToArray();
                foreach (var group in groups)
                {
                    var first = group.First();
                    var values = group.SelectMany(term => term.Cells[key].Values)
                        .DistinctBy(value => value.Key).ToDictionary(value => value.Key, value => value.Value,
                            StringComparer.Ordinal);
                    var cells = new Dictionary<string, Cell>(first.Cells, StringComparer.Ordinal)
                        { [key] = new(first.Cells[key].Fact, values) };
                    foreach (var term in group)
                        terms.Remove(term);
                    var merged = new Term(cells);
                    Normalize(merged);
                    terms.Add(merged);
                    changed = true;
                }
            }
            terms = terms.DistinctBy(term => Signature(term)).ToList();
        }
        return terms;
    }

    internal static IReadOnlyDictionary<string, Cell> Common(IEnumerable<Term> source)
    {
        var terms = source.ToArray();
        if (terms.Length == 0)
            return new Dictionary<string, Cell>();
        return terms[0].Cells.Where(pair => terms.All(term => term.Cells.TryGetValue(pair.Key, out var value)
                && ValueKey(value) == ValueKey(pair.Value)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal static string Format(IEnumerable<Term> source, bool chinese,
        IReadOnlyDictionary<string, Cell>? omit = null)
    {
        var terms = source.ToArray();
        if (terms.Length == 0)
            return "";
        var common = Common(terms);
        var shared = FormatCells(common.Where(pair => omit?.ContainsKey(pair.Key) != true).Select(pair => pair.Value), chinese);
        var alternatives = terms.Select(term => FormatCells(term.Cells
                .Where(pair => !common.ContainsKey(pair.Key) && omit?.ContainsKey(pair.Key) != true)
                .Select(pair => pair.Value), chinese))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (alternatives.Any(value => value.Length == 0))
            return shared;
        var choices = string.Join(chinese ? "；或 " : "; or ", alternatives);
        return shared.Length == 0 ? choices : choices.Length == 0 ? shared : $"{shared}（{choices}）";
    }

    internal static string FormatCells(IEnumerable<Cell> source, bool chinese)
    {
        var cells = source.ToList();
        var parts = new List<string>();
        foreach (var purchase in cells.Where(IsEquivalentShopCards))
        {
            var shop = purchase.Fact.Shop!;
            var title = chinese ? $"{shop.Floor}层购牌" : $"{shop.Floor}F cards";
            parts.Add(title + (shop.CardCombinationsEquivalent
                ? chinese ? $"：任意选择（{shop.Gold}金内）" : $": any affordable combination ({shop.Gold}g)"
                : chinese ? "：不买或单件购买，结果相同" : ": no purchase or one card, same result"));
            var cards = shop.AvailableCards.Select(offer => offer.Value).Prepend("skip").ToHashSet(StringComparer.Ordinal);
            var other = shop.Offers.Where(offer => purchase.Values.ContainsKey(offer.Value) && !cards.Contains(offer.Value))
                .Select(offer => ForecastItemFormatter.Format(offer.Item.Item, false)).ToArray();
            if (other.Length > 0)
                parts.Add((chinese ? "或单独购买" : "or buy one: ") + string.Join(chinese ? "、" : ", ", other));
        }
        cells.RemoveAll(IsEquivalentShopCards);
        foreach (var freedom in cells.Where(IsAnyCardChoice)
                     .GroupBy(cell => cell.Values.Count == cell.Fact.Domain.Distinct().Count()))
        {
            var ordinary = freedom.Where(cell => cell.Fact.GroupCount == 1 && cell.Fact.Step == 0).ToArray();
            var qualifier = freedom.Key ? "" : chinese ? "（不重掷）" : " (no reroll)";
            if (ordinary.Length > 0)
            {
                parts.Add(string.Join(chinese ? "、" : ",", ordinary.Select(cell => cell.Fact.Floor).Distinct().Order())
                    + (chinese ? "层选牌：任意选择" : "F cards: any choice") + qualifier);
            }
            var purchaseRewards = freedom.Except(ordinary).Where(cell => cell.Fact.Key.StartsWith("shop-pickup:", StringComparison.Ordinal))
                .GroupBy(cell => cell.Fact.Floor)
                .Where(group => group.All(cell => cell.Fact.GroupCount == group.Count())
                    && group.Select(cell => cell.Fact.Group).Distinct().Count() == group.Count()).ToArray();
            foreach (var group in purchaseRewards)
                parts.Add(chinese ? $"{group.Key}层购买所得{group.Count()}组选牌：任意选择{qualifier}"
                    : $"{group.Key}F purchase: any choice in {group.Count()} card rewards{qualifier}");
            parts.AddRange(freedom.Except(ordinary).Except(purchaseRewards.SelectMany(group => group)).Select(cell => cell.Fact.Heading
                + (chinese ? "：任意选择" : ": any choice") + qualifier));
        }
        cells.RemoveAll(IsAnyCardChoice);
        var skips = cells.Where(cell => cell.Fact.Kind == ForecastChoiceKind.CardReward
            && cell.Fact.GroupCount == 1 && cell.Fact.Step == 0 && cell.Values.Count == 1
            && cell.Values.ContainsKey("alt:skip")).ToArray();
        if (skips.Length > 1)
        {
            parts.Add(string.Join(chinese ? "、" : ",", skips.Select(cell => cell.Fact.Floor).Distinct().Order())
                + (chinese ? "层跳过选牌" : "F skip cards"));
            cells.RemoveAll(cell => skips.Contains(cell));
        }
        parts.AddRange(cells.Select(cell =>
        {
            var values = string.Join("/", cell.Values.Values.Distinct(StringComparer.Ordinal));
            return cell.Fact.Heading.Length == 0 ? values : cell.Fact.Heading + (chinese ? "：" : ": ") + values;
        }));
        return string.Join(chinese ? "；" : "; ", parts);
    }

    private static string Column(ForecastChoiceFact fact) => JsonSerializer.Serialize(new
        { fact.Key, fact.Closed, Domain = fact.Domain.Distinct().Order(StringComparer.Ordinal), Shop = fact.Shop?.Key });

    private static string ValueKey(Cell cell) => JsonSerializer.Serialize(cell.Values.Keys.Order(StringComparer.Ordinal));

    private static string Signature(Term term, string? omit = null) => JsonSerializer.Serialize(term.Cells
        .Where(pair => pair.Key != omit).OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new { pair.Key, Values = ValueKey(pair.Value) }));

    private static void Normalize(Term term)
    {
        foreach (var (key, cell) in term.Cells.ToArray())
            if (cell.Fact.Kind != ForecastChoiceKind.CardReward && cell.Fact.Closed && cell.Fact.Domain.Count > 0
                && cell.Fact.Domain.ToHashSet(StringComparer.Ordinal).SetEquals(cell.Values.Keys))
                term.Cells.Remove(key);
    }

    internal static bool IsAnyCardChoice(Cell cell) => cell.Fact.Kind == ForecastChoiceKind.CardReward
        && cell.Fact.Closed && cell.Fact.Domain.Count > 0
        && (cell.Fact.Domain.ToHashSet(StringComparer.Ordinal).SetEquals(cell.Values.Keys)
            || cell.Fact.Domain.Contains("alt:reroll")
                && cell.Fact.Domain.Where(value => value != "alt:reroll").ToHashSet(StringComparer.Ordinal).SetEquals(cell.Values.Keys));

    internal static bool IsChoiceFreedom(Cell cell) => IsAnyCardChoice(cell) || IsEquivalentShopCards(cell);

    private static bool IsEquivalentShopCards(Cell cell) => cell.Fact.Shop is { } shop
        && cell.Values.ContainsKey("skip") && shop.AvailableCards.Any()
        && shop.AvailableCards.All(offer => cell.Values.ContainsKey(offer.Value));
}
