using Godot;

namespace SeedOracle.UI;

internal sealed partial class RouteMarkerBadge : Control
{
    private const float BadgeSize = 38f;
    private const float Radius = 13f;
    private readonly RouteMarkerStyle _style;

    public RouteMarkerBadge(RouteMarkerStyle style)
    {
        _style = style;
        CustomMinimumSize = new Vector2(BadgeSize, BadgeSize);
        Size = CustomMinimumSize;
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        TooltipText = $"{style.ChineseColorName}{style.ChineseName}";
    }

    public override void _Draw()
    {
        var center = Size * 0.5f;
        var fill = _style.Color with { A = 0.24f };
        var shadow = new Color(0.01f, 0.035f, 0.045f, 0.9f);
        if (_style.Shape == RouteMarkerShape.Circle)
        {
            DrawCircle(center, Radius, fill);
            DrawArc(center, Radius, 0f, Mathf.Pi * 2f, 32, shadow, 5f, true);
            DrawArc(center, Radius, 0f, Mathf.Pi * 2f, 32, _style.Color, 2.5f, true);
            return;
        }

        var polygon = _style.Shape switch
        {
            RouteMarkerShape.Triangle => RegularPolygon(center, Radius, 3, -Mathf.Pi * 0.5f),
            RouteMarkerShape.Square => RegularPolygon(center, Radius, 4, -Mathf.Pi * 0.25f),
            RouteMarkerShape.Diamond => RegularPolygon(center, Radius, 4, -Mathf.Pi * 0.5f),
            RouteMarkerShape.Pentagon => RegularPolygon(center, Radius, 5, -Mathf.Pi * 0.5f),
            RouteMarkerShape.Hexagon => RegularPolygon(center, Radius, 6, 0f),
            RouteMarkerShape.Star => StarPolygon(center, Radius, Radius * 0.48f),
            _ => RegularPolygon(center, Radius, 8, Mathf.Pi * 0.125f)
        };
        DrawColoredPolygon(polygon, fill);
        var outline = polygon.Append(polygon[0]).ToArray();
        DrawPolyline(outline, shadow, 5f, true);
        DrawPolyline(outline, _style.Color, 2.5f, true);
    }

    private static Vector2[] RegularPolygon(Vector2 center, float radius, int sides, float rotation) =>
        Enumerable.Range(0, sides)
            .Select(index => rotation + Mathf.Pi * 2f * index / sides)
            .Select(angle => center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius)
            .ToArray();

    private static Vector2[] StarPolygon(Vector2 center, float outerRadius, float innerRadius) =>
        Enumerable.Range(0, 10)
            .Select(index => -Mathf.Pi * 0.5f + Mathf.Pi * index / 5f)
            .Select((angle, index) => center
                                      + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle))
                                      * (index % 2 == 0 ? outerRadius : innerRadius))
            .ToArray();
}
