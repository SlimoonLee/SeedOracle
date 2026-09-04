using Godot;
using MegaCrit.Sts2.Core.Map;
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
        var rawGroups = forecast.RouteVariants
            .GroupBy(OutcomeKey)
            .OrderBy(group => group.First().RoomType)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, Variants: (IReadOnlyList<RouteVariantForecast>)group.ToArray()))
            .ToArray();

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

    public static string OutcomeKey(RouteVariantForecast variant)
    {
        var merchantOrdinal = variant.Merchant is { HasValue: true }
            ? variant.Merchant.Value!.FutureVisitOrdinal
            : 0;
        return string.Join(
            "|",
            variant.RoomType,
            variant.Event?.Id.ToString() ?? string.Empty,
            variant.Encounter?.Id.ToString() ?? string.Empty,
            merchantOrdinal,
            EventContentKey(variant.EventContents),
            CombatRewardKey(variant.CombatRewards),
            TreasureKey(variant.Treasure));
    }

    private static string EventContentKey(Forecast<EventContentDetails>? forecast)
    {
        if (forecast is not { HasValue: true })
            return forecast is null ? string.Empty : $"event-content:{forecast.Accuracy}";

        return "event-content:"
               + string.Join(
                   ";",
                   forecast.Value!.Options.Select(option =>
                       option.TextKey + "=" + string.Join(
                           "/",
                           option.Sets.Select(set => string.Join(",", set.Items)))));
    }

    private static string CombatRewardKey(Forecast<CombatRewardDetails>? forecast)
    {
        if (forecast is not { HasValue: true })
            return forecast is null ? string.Empty : $"combat:{forecast.Accuracy}";

        var value = forecast.Value!;
        var cards = string.Join(
            ";",
            value.CardRewards.Select(bundle => string.Join(",", bundle.Select(item => item.Id))));
        var potions = string.Join(",", value.Potions.Select(item => item.Id));
        var relics = string.Join(",", value.Relics.Select(item => item.Id));
        return $"combat:{value.Gold}:{cards}:{potions}:{relics}";
    }

    private static string TreasureKey(Forecast<TreasureRoomDetails>? forecast)
    {
        if (forecast is not { HasValue: true })
            return forecast is null ? string.Empty : $"treasure:{forecast.Accuracy}";

        var value = forecast.Value!;
        return $"treasure:{value.Gold}:{value.IsEmpty}:{string.Join(",", value.Relics.Select(item => item.Id))}";
    }

}
