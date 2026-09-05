using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.UI;

/// <summary>
/// Persistent per-node marker for the route plan. Drawn in the same shape
/// vocabulary as the combat panel markers; planned rooms use the gold octagon
/// with the target floor number, completed rooms turn into a green check.
/// </summary>
internal sealed partial class RoutePlanNodeMarker : Control
{
    private const float MarkerSize = 132f;
    private const float Radius = 54f;
    private static readonly Color PlannedColor = new(1f, 0.84f, 0.2f);
    private static readonly Color CompletedColor = new(0.35f, 0.85f, 0.52f);

    private bool _completed;
    private int _floor;

    public RoutePlanNodeMarker()
    {
        Size = new Vector2(MarkerSize, MarkerSize);
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        ZIndex = RouteNodeMarker.OverlayZIndex;
    }

    public void Configure(bool completed, int floor, Vector2 center)
    {
        _completed = completed;
        _floor = floor;
        Position = center - Size * 0.5f;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var center = Size * 0.5f;
        var color = _completed ? CompletedColor : PlannedColor;
        var fill = color with { A = 0.16f };
        var shadow = new Color(0.02f, 0.06f, 0.08f, 0.78f);
        var polygon = Enumerable.Range(0, 8)
            .Select(index => Mathf.Pi * 0.125f + Mathf.Pi * 2f * index / 8f)
            .Select(angle => center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * Radius)
            .ToArray();
        DrawColoredPolygon(polygon, fill);
        var outline = polygon.Append(polygon[0]).ToArray();
        DrawPolyline(outline, shadow, 10f, true);
        DrawPolyline(outline, color, 5f, true);

        var font = ThemeDB.FallbackFont;
        if (font is null)
            return;

        var text = _completed ? "✓" : _floor.ToString();
        var fontSize = 34;
        var textWidth = Size.X;
        var baseline = center.Y + fontSize * 0.35f;
        DrawString(
            font,
            new Vector2(0f, baseline),
            text,
            HorizontalAlignment.Center,
            textWidth,
            fontSize,
            color with { A = 0.95f });
    }
}

/// <summary>
/// Owns the persistent plan overlay: markers on planned nodes plus a single
/// gold line along the planned chain. Deliberately independent from the hover
/// driven <see cref="RouteNodeMarkerOverlay"/> so hovering never erases it.
/// </summary>
internal static class RoutePlanOverlay
{
    private static readonly List<RoutePlanNodeMarker> ActiveMarkers = [];
    private static RoutePathLineOverlay? _activeLines;

    public static void Refresh(NMapScreen screen, RunState run)
    {
        Clear();
        var plan = RoutePlanTracker.Current;
        if (plan is null || plan.Phase != RoutePlanPhase.Active)
            return;

        foreach (var entry in plan.Entries)
        {
            if (!screen._mapPointDictionary.TryGetValue(entry.Coord, out var node))
                continue;

            var marker = new RoutePlanNodeMarker
            {
                Name = $"SeedOraclePlanMarker_{entry.Coord.row}_{entry.Coord.col}"
            };
            var floor = run.TotalFloor - run.ActFloor + entry.Coord.row + 1;
            marker.Configure(entry.IsCompleted, floor, GetVisualCenter(node, node));
            node.AddChild(marker);
            ActiveMarkers.Add(marker);
        }

        ShowLines(screen, plan);
    }

    public static void Clear()
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

    private static void ShowLines(NMapScreen screen, RoutePlan plan)
    {
        var anchors = new List<MapCoord>();
        if (screen._mapPointDictionary.ContainsKey(plan.AnchorCoord))
            anchors.Add(plan.AnchorCoord);
        anchors.AddRange(plan.Entries.Select(entry => entry.Coord));
        if (anchors.Count < 2)
            return;

        var pointsContainer = screen._points;
        var overlay = new RoutePathLineOverlay
        {
            Name = "SeedOraclePlanLines",
            Position = Vector2.Zero,
            Size = pointsContainer.Size
        };
        pointsContainer.AddChild(overlay);

        var bundles = new List<RouteLineBundle>();
        for (var index = 1; index < anchors.Count; index++)
        {
            if (!screen._mapPointDictionary.TryGetValue(anchors[index - 1], out var startNode)
                || !screen._mapPointDictionary.TryGetValue(anchors[index], out var endNode))
            {
                continue;
            }

            bundles.Add(new RouteLineBundle(
                anchors[index - 1],
                anchors[index],
                GetVisualCenter(startNode, overlay),
                GetVisualCenter(endNode, overlay),
                [PlannedLineColor]));
        }

        if (bundles.Count == 0)
        {
            overlay.QueueFree();
            return;
        }

        overlay.Configure(bundles.ToArray());
        _activeLines = overlay;
    }

    private static readonly Color PlannedLineColor = new(1f, 0.84f, 0.2f, 1f);

    private static Vector2 GetVisualCenter(NMapPoint node, CanvasItem coordinateSpace)
    {
        var visual = node.GetNodeOrNull<Control>("%IconContainer")
                     ?? node.GetNodeOrNull<Control>("%Icon")
                     ?? node;
        var globalCenter = visual.GetGlobalTransformWithCanvas() * (visual.Size * 0.5f);
        return coordinateSpace.GetGlobalTransformWithCanvas().AffineInverse() * globalCenter;
    }
}
