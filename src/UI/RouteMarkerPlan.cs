using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal enum RouteMarkerShape
{
    Triangle,
    Square,
    Diamond,
    Circle,
    Pentagon,
    Hexagon,
    Star,
    Octagon
}

internal sealed record RouteMarkerStyle(
    RouteMarkerShape Shape,
    string Glyph,
    string ChineseName,
    string EnglishName,
    string ChineseColorName,
    string EnglishColorName,
    string RichTextTag,
    Color Color);

internal sealed record RouteMarkerAssignment(
    MapPoint Point,
    RouteMarkerStyle Style);

internal sealed record RouteLineAssignment(
    RouteMarkerStyle Style,
    IReadOnlyList<IReadOnlyList<MapPoint>> Routes);

internal sealed record RouteOutcomeGroup(
    string Key,
    IReadOnlyList<RouteVariantForecast> Variants,
    RouteMarkerStyle? Style,
    IReadOnlyList<MapPoint> MarkerPoints)
{
    public RouteVariantForecast Representative => Variants[0];

    public bool UsesMarker => Style is not null && MarkerPoints.Count > 0;

    public bool UsesLine => Style is not null && MarkerPoints.Count == 0;
}

internal sealed record RouteMarkerPlan(
    IReadOnlyList<RouteOutcomeGroup> Groups,
    IReadOnlyList<RouteMarkerAssignment> Assignments,
    IReadOnlyList<RouteLineAssignment> Lines);

internal static class RouteMarkerPlanner
{
    private static readonly RouteMarkerStyle[] Styles =
    [
        new(RouteMarkerShape.Triangle, "△", "三角形", "triangle", "绿色", "green", "green", new Color(0.25f, 0.9f, 0.38f)),
        new(RouteMarkerShape.Square, "□", "正方形", "square", "青色", "aqua", "aqua", new Color(0.15f, 0.82f, 1f)),
        new(RouteMarkerShape.Diamond, "◇", "菱形", "diamond", "粉色", "pink", "pink", new Color(1f, 0.38f, 0.78f)),
        new(RouteMarkerShape.Circle, "○", "圆形", "circle", "橙色", "orange", "orange", new Color(1f, 0.65f, 0.16f)),
        new(RouteMarkerShape.Pentagon, "⬠", "五边形", "pentagon", "紫色", "purple", "purple", new Color(0.72f, 0.45f, 1f)),
        new(RouteMarkerShape.Hexagon, "⬡", "六边形", "hexagon", "红色", "red", "red", new Color(1f, 0.3f, 0.28f)),
        new(RouteMarkerShape.Star, "☆", "星形", "star", "蓝色", "blue", "blue", new Color(0.3f, 0.52f, 1f)),
        new(RouteMarkerShape.Octagon, "八", "八边形", "octagon", "金色", "gold", "gold", new Color(1f, 0.84f, 0.2f))
    ];

    public static RouteMarkerStyle GetStyle(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        return Styles[index % Styles.Length];
    }

    public static RouteMarkerPlan Build(MapNodeForecast forecast)
    {
        var keys = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var references = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var rawGroups = forecast.RouteVariants
            .GroupBy(variant => new OutcomeReferences(variant.RoomType, variant.Event?.Id, variant.Encounter?.Id,
                ReferenceId(variant.Merchant), ReferenceId(variant.EventContents),
                ReferenceId(variant.CombatRewards), ReferenceId(variant.Treasure)))
            .Select(group => (Key: OutcomeKey(group.First(), keys), Variants: group.ToArray()))
            .GroupBy(group => group.Key)
            .OrderBy(group => group.First().Variants[0].RoomType)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, Variants: (IReadOnlyList<RouteVariantForecast>)group.SelectMany(part => part.Variants).ToArray()))
            .ToArray();

        int ReferenceId(object? forecastValue)
        {
            if (forecastValue is null)
                return 0;
            if (!references.TryGetValue(forecastValue, out var id))
                references[forecastValue] = id = references.Count + 1;
            return id;
        }

        var groups = new List<RouteOutcomeGroup>(rawGroups.Length);
        var lines = new List<RouteLineAssignment>();
        for (var index = 0; index < rawGroups.Length; index++)
        {
            var raw = rawGroups[index];
            var style = index < Styles.Length ? Styles[index] : null;
            groups.Add(new RouteOutcomeGroup(raw.Key, raw.Variants, style, []));
            if (style is null)
                continue;

            var routes = raw.Variants
                .Select(variant => (IReadOnlyList<MapPoint>)variant.Route
                    .Select(choice => choice.Point)
                    .Append(forecast.Point)
                    .Where((point, routeIndex) => routeIndex == 0
                                                  || !ReferenceEquals(
                                                      point,
                                                      variant.Route[routeIndex - 1].Point))
                    .ToArray())
                .ToArray();
            lines.Add(new RouteLineAssignment(style, routes));
        }

        return new RouteMarkerPlan(groups, [], lines);
    }

    public static string OutcomeKey(RouteVariantForecast variant) => OutcomeKey(variant, null);

    private readonly record struct OutcomeReferences(
        MegaCrit.Sts2.Core.Rooms.RoomType Room, ModelId? Event, ModelId? Encounter,
        int Merchant, int Contents, int Rewards, int Treasure);

    private static string OutcomeKey(RouteVariantForecast variant, Dictionary<object, string>? keys)
    {
        var merchantKey = Cached(variant.Merchant, merchant => merchant is { HasValue: true }
            ? $"{merchant.Accuracy}:{ForecastContentKey.Merchant(merchant.Value!)}:{ForecastContentKey.MerchantConditions(merchant.Value!)}"
            : merchant is null ? string.Empty : $"merchant:{merchant.Accuracy}");
        return string.Join(
            "|",
            variant.RoomType,
            variant.Event?.Id.ToString() ?? string.Empty,
            variant.Encounter?.Id.ToString() ?? string.Empty,
            merchantKey,
            Cached(variant.EventContents, EventContentKey),
            Cached(variant.CombatRewards, CombatRewardKey),
            Cached(variant.Treasure, TreasureKey));

        string Cached<T>(Forecast<T>? forecast, Func<Forecast<T>?, string> build)
        {
            if (forecast is null)
                return "";
            if (keys?.TryGetValue(forecast, out var key) == true)
                return key;
            key = build(forecast);
            if (keys is not null)
                keys[forecast] = key;
            return key;
        }
    }

    private static string EventContentKey(Forecast<EventContentDetails>? forecast)
    {
        if (forecast is not { HasValue: true })
            return forecast is null ? string.Empty : $"event-content:{forecast.Accuracy}";

        return $"event-content:{forecast.Accuracy}:{forecast.Value!.UncoveredOptionCount}:"
               + string.Join(
                   ";",
                   forecast.Value!.Options.Select(option =>
                       option.TextKey + ":" + option.HasPredictedEffects + "=initial:"
                       + string.Join(",", option.InitialItems.Select(ItemKey))
                       + ":predicted:"
                       + string.Join(
                           "/",
                           option.Sets.Select(set => $"{set.Kind}:{set.Position}:{set.SourceDeckIndex}:"
                               + (set.SourceCard is null ? string.Empty : ItemKey(set.SourceCard))
                               + "=" + string.Join(",", set.Items.Select(ItemKey))))));
    }

    private static string ItemKey(ForecastItemDetails item) =>
        ForecastContentKey.Item(item);

    private static string CombatRewardKey(Forecast<CombatRewardDetails>? forecast)
    {
        if (forecast is not { HasValue: true })
            return forecast is null ? string.Empty : $"combat:{forecast.Accuracy}";

        var value = forecast.Value!;
        var cards = string.Join(
            ";",
            value.CardRewardGroups.Select(group => string.Join("/", group.Steps.Select(step =>
                string.Join(",", step.Cards.Select(item => ItemKey(item.Item)))
                + ":" + string.Join(",", step.Alternatives.Select(choice => $"{choice.OptionId}:{choice.AfterSelected}"))
                + $":{step.SelectedIndex}:{step.RejectedChoice}"))));
        var potions = string.Join(",", value.Potions.Select(item => ItemKey(item.Item)));
        var relics = string.Join(",", value.Relics.Select(item => ItemKey(item.Item)));
        var branches = System.Text.Json.JsonSerializer.Serialize(new
            { value.Possibilities, value.PossibilitiesTruncated, value.HuntPreviewLimit,
                value.ReferenceCondition, value.ReferenceChoicePaths, Branches = value.ChoiceBranches.Select(branch => new
                    { branch.Condition, branch.ChoicePaths, Outcome = ForecastContentKey.Combat(branch.Outcome) }) });
        return $"combat:{forecast.Accuracy}:{value.Gold}:{cards}:{potions}:{relics}:{branches}";
    }

    private static string TreasureKey(Forecast<TreasureRoomDetails>? forecast)
    {
        if (forecast is not { HasValue: true })
            return forecast is null ? string.Empty : $"treasure:{forecast.Accuracy}";

        var value = forecast.Value!;
        return $"treasure:{forecast.Accuracy}:{ForecastContentKey.Treasure(value)}:"
               + System.Text.Json.JsonSerializer.Serialize(new { value.ReferenceCondition, value.ReferenceChoicePaths,
                   Branches = value.ChoiceBranches.Select(branch => new
                       { branch.Condition, branch.ChoicePaths, Outcome = ForecastContentKey.Treasure(branch.Outcome) }) });
    }

}
