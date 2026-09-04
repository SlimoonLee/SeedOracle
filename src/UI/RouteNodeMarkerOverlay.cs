using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace SeedOracle.UI;

internal sealed partial class RouteNodeMarker : Control
{
    internal const int OverlayZIndex = 60;
    private const float MarkerSize = 132f;
    private const float Radius = 54f;
    private RouteMarkerStyle? _style;

    public RouteNodeMarker()
    {
        Size = new Vector2(MarkerSize, MarkerSize);
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        ZIndex = OverlayZIndex;
    }

    public void Configure(RouteMarkerStyle style, Vector2 center)
    {
        _style = style;
        Position = center - Size * 0.5f;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_style is null)
            return;

        var center = Size * 0.5f;
        var fill = _style.Color with { A = 0.13f };
        var shadow = new Color(0.02f, 0.06f, 0.08f, 0.78f);
        if (_style.Shape == RouteMarkerShape.Circle)
        {
            DrawCircle(center, Radius, fill);
            DrawArc(center, Radius, 0f, Mathf.Pi * 2f, 64, shadow, 10f, true);
            DrawArc(center, Radius, 0f, Mathf.Pi * 2f, 64, _style.Color, 5f, true);
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
        DrawPolyline(outline, shadow, 10f, true);
        DrawPolyline(outline, _style.Color, 5f, true);
    }

    private static Vector2[] RegularPolygon(
        Vector2 center,
        float radius,
        int sides,
        float rotation) => Enumerable.Range(0, sides)
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

internal sealed record RouteLineBundle(
    MapCoord StartCoord,
    MapCoord EndCoord,
    Vector2 Start,
    Vector2 End,
    IReadOnlyList<Color> Colors);

internal sealed partial class RoutePathLineOverlay : Control
{
    internal const int OverlayZIndex = 60;
    private const float LaneWidth = 5.5f;
    private const float LaneSpacing = 7.5f;
    private const float OutlinePadding = 5f;

    private IReadOnlyList<RouteLineBundle> _bundles = [];

    internal int BundleCount => _bundles.Count;

    internal int MaximumLaneCount => _bundles.Count == 0
        ? 0
        : _bundles.Max(bundle => bundle.Colors.Count);

    internal bool Touches(MapCoord coord) => _bundles.Any(bundle =>
        bundle.StartCoord == coord || bundle.EndCoord == coord);

    public RoutePathLineOverlay()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        ZIndex = OverlayZIndex;
    }

    public void Configure(IReadOnlyList<RouteLineBundle> bundles)
    {
        _bundles = bundles;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var shadow = new Color(0.02f, 0.06f, 0.08f, 0.8f);
        foreach (var bundle in _bundles)
        {
            var bundleWidth = LaneWidth + LaneSpacing * (bundle.Colors.Count - 1);
            DrawLine(bundle.Start, bundle.End, shadow, bundleWidth + OutlinePadding, true);
        }

        foreach (var bundle in _bundles)
        {
            var delta = bundle.End - bundle.Start;
            if (delta.LengthSquared() <= Mathf.Epsilon)
                continue;

            var direction = delta.Normalized();
            var normal = new Vector2(-direction.Y, direction.X);
            for (var index = 0; index < bundle.Colors.Count; index++)
            {
                var lane = index - (bundle.Colors.Count - 1) * 0.5f;
                var offset = normal * (lane * LaneSpacing);
                var start = bundle.Start + offset;
                var end = bundle.End + offset;
                var color = bundle.Colors[index] with { A = 1f };

                DrawLine(start, end, color, LaneWidth, true);
                DrawCircle(start, LaneWidth * 0.5f, color);
                DrawCircle(end, LaneWidth * 0.5f, color);

                // Every lane meets at the map-node center. This keeps a color visually
                // continuous when the number of shared lanes changes at a junction.
                if (!start.IsEqualApprox(bundle.Start))
                    DrawLine(bundle.Start, start, color, LaneWidth, true);
                if (!end.IsEqualApprox(bundle.End))
                    DrawLine(end, bundle.End, color, LaneWidth, true);
            }
        }
    }
}

internal static class RouteNodeMarkerOverlay
{
    private static readonly List<RouteNodeMarker> ActiveMarkers = [];
    private static RoutePathLineOverlay? _activeLines;

    public static void Show(
        NMapPoint owner,
        IReadOnlyList<RouteMarkerAssignment> assignments,
        IReadOnlyList<RouteLineAssignment> lineAssignments)
    {
        ClearAll();
        foreach (var assignment in assignments)
        {
            if (!owner._screen._mapPointDictionary.TryGetValue(assignment.Point.coord, out var node))
                continue;

            var marker = new RouteNodeMarker
            {
                Name = $"SeedOracleRouteMarker_{assignment.Style.Shape}"
            };
            marker.Configure(assignment.Style, GetVisualCenter(node, node));
            node.AddChild(marker);
            ActiveMarkers.Add(marker);
        }

        ShowLines(owner, lineAssignments);
    }

    private static void ShowLines(
        NMapPoint owner,
        IReadOnlyList<RouteLineAssignment> assignments)
    {
        if (assignments.Count == 0)
            return;

        var pointsContainer = owner._screen._points;
        var overlay = new RoutePathLineOverlay
        {
            Name = "SeedOracleRouteLines",
            Position = Vector2.Zero,
            Size = pointsContainer.Size
        };
        pointsContainer.AddChild(overlay);

        var edges = new Dictionary<MapEdge, RouteLineBundleBuilder>();
        var seen = new HashSet<(RouteMarkerShape Style, MapCoord Start, MapCoord End)>();
        foreach (var assignment in assignments)
        {
            foreach (var route in assignment.Routes)
            {
                var points = new List<MapPoint>(route.Count + 1);
                if (owner._runState.CurrentMapPoint is { } current)
                    points.Add(current);
                foreach (var point in route)
                {
                    if (points.Count == 0 || points[^1].coord != point.coord)
                        points.Add(point);
                }

                for (var index = 1; index < points.Count; index++)
                {
                    var startPoint = points[index - 1];
                    var endPoint = points[index];
                    var edge = MapEdge.Create(startPoint.coord, endPoint.coord);
                    var styleKey = (assignment.Style.Shape, edge.Start, edge.End);
                    if (!seen.Add(styleKey)
                        || !TryResolveNode(owner, edge.Start, out var startNode)
                        || !TryResolveNode(owner, edge.End, out var endNode))
                    {
                        continue;
                    }

                    if (!edges.TryGetValue(edge, out var builder))
                    {
                        builder = new RouteLineBundleBuilder(
                            edge.Start,
                            edge.End,
                            GetVisualCenter(startNode, overlay),
                            GetVisualCenter(endNode, overlay));
                        edges.Add(edge, builder);
                    }
                    builder.Add(assignment.Style.Color);
                }
            }
        }

        if (edges.Count == 0)
        {
            overlay.QueueFree();
            return;
        }

        overlay.Configure(edges
            .OrderBy(pair => pair.Key.Start)
            .ThenBy(pair => pair.Key.End)
            .Select(pair => pair.Value.Build())
            .ToArray());
        _activeLines = overlay;
    }

    public static void ClearAll()
    {
        foreach (var marker in ActiveMarkers)
        {
            if (GodotObject.IsInstanceValid(marker) && !marker.IsQueuedForDeletion())
                marker.QueueFree();
        }
        ActiveMarkers.Clear();
        if (_activeLines is not null
            && GodotObject.IsInstanceValid(_activeLines)
            && !_activeLines.IsQueuedForDeletion())
        {
            _activeLines.QueueFree();
        }
        _activeLines = null;
    }

    private static Vector2 GetVisualCenter(NMapPoint node, CanvasItem coordinateSpace)
    {
        var visual = node.GetNodeOrNull<Control>("%IconContainer")
                     ?? node.GetNodeOrNull<Control>("%Icon")
                     ?? node;
        var globalCenter = visual.GetGlobalTransformWithCanvas() * (visual.Size * 0.5f);
        return coordinateSpace.GetGlobalTransformWithCanvas().AffineInverse() * globalCenter;
    }

    private static bool TryResolveNode(
        NMapPoint owner,
        MapCoord coord,
        out NMapPoint node)
    {
        if (owner.Point.coord == coord)
        {
            node = owner;
            return true;
        }

        return owner._screen._mapPointDictionary.TryGetValue(coord, out node!);
    }

    private readonly record struct MapEdge(MapCoord Start, MapCoord End)
    {
        public static MapEdge Create(MapCoord first, MapCoord second) =>
            first.CompareTo(second) <= 0
                ? new MapEdge(first, second)
                : new MapEdge(second, first);
    }

    private sealed class RouteLineBundleBuilder(
        MapCoord startCoord,
        MapCoord endCoord,
        Vector2 start,
        Vector2 end)
    {
        private readonly List<Color> _colors = [];

        public void Add(Color color) => _colors.Add(color);

        public RouteLineBundle Build() => new(
            startCoord,
            endCoord,
            start,
            end,
            _colors.ToArray());
    }
}
