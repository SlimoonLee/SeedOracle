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

    // One precondition repeated in every block is noise. A cell that every term
    // carrying it agrees on is a precondition of the whole forecast, so give it to
    // the terms that were built without it and let Common() print it once.
    internal static void UnifySharedCells(IReadOnlyList<Term> terms)
    {
        if (terms.Count < 2)
            return;
        var shared = terms.SelectMany(term => term.Cells)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(group => group.Select(pair => ValueKey(pair.Value)).Distinct(StringComparer.Ordinal).Count() == 1)
            .Select(group => (Key: group.Key, Cell: group.First().Value))
            // Only unconditional statements ("any pick here gives the same
            // result") may be extended to the terms that did not enumerate them.
            // A concrete option must never leak into a group that never evaluated it.
            .Where(item => IsChoiceFreedom(item.Cell))
            .ToArray();
        foreach (var (key, cell) in shared)
        {
            var carriers = terms.Count(term => term.Cells.ContainsKey(key));
            if (carriers < 2 || carriers == terms.Count)
                continue;
            foreach (var term in terms.Where(term => !term.Cells.ContainsKey(key)))
                term.Cells[key] = cell;
        }
    }

    // A route label answers "how did I get here", not "why is this outcome
    // different". Keep it only when nothing else in the term explains the
    // difference, or when the term already covers every route considered.
    internal static void DropIncidentalRoutes(IReadOnlyList<Term> terms)
    {
        if (terms.Count == 0)
            return;
        var common = Common(terms);
        var routeCount = terms.SelectMany(term => term.Cells.Values)
            .Where(cell => cell.Fact.Kind == ForecastChoiceKind.Route)
            .SelectMany(cell => cell.Values.Keys).Distinct(StringComparer.Ordinal).Count();
        // The same non-route condition leading to different outcomes on disjoint
        // routes means the route itself is the cause. Never hide it there.
        var entries = terms.Select(term => (Term: term, Key: WithoutRoutes(term),
            Routes: Routes(term))).ToArray();
        var causal = entries.Where(entry => entries.Any(other => !ReferenceEquals(entry.Term, other.Term)
                && other.Key == entry.Key && !entry.Routes.Overlaps(other.Routes)))
            .Select(entry => entry.Term).ToHashSet();
        foreach (var term in terms)
        {
            var routeKeys = term.Cells.Where(pair => pair.Value.Fact.Kind == ForecastChoiceKind.Route)
                .Select(pair => pair.Key).ToArray();
            if (routeKeys.Length == 0 || causal.Contains(term))
                continue;
            var explained = term.Cells.Any(pair => pair.Value.Fact.Kind != ForecastChoiceKind.Route
                && !common.ContainsKey(pair.Key));
            foreach (var key in routeKeys)
                if (explained || routeCount > 1 && term.Cells[key].Values.Count >= routeCount)
                    term.Cells.Remove(key);
        }
    }

    private static string WithoutRoutes(Term term) => JsonSerializer.Serialize(term.Cells
        .Where(pair => pair.Value.Fact.Kind != ForecastChoiceKind.Route)
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new { pair.Key, Values = ValueKey(pair.Value) }));

    private static HashSet<string> Routes(Term term) => term.Cells.Values
        .Where(cell => cell.Fact.Kind == ForecastChoiceKind.Route)
        .SelectMany(cell => cell.Values.Keys).ToHashSet(StringComparer.Ordinal);

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
            // CauseLabel is the position-free name of the choice ("脑蛭"), so a
            // condition never claims a floor that only exists on one of the
            // routes being compared.
            var heading = cell.Fact.CauseLabel ?? cell.Fact.Heading;
            return heading.Length == 0 ? values : heading + (chinese ? "：" : ": ") + values;
        }));
        return string.Join(chinese ? "；" : "; ", parts);
    }

    private static string Column(ForecastChoiceFact fact) => JsonSerializer.Serialize(new
        { Key = fact.CauseKey ?? fact.Key, fact.Closed, Domain = fact.Domain.Distinct().Order(StringComparer.Ordinal), Shop = fact.Shop?.Key });

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
