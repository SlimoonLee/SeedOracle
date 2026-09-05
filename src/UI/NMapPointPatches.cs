using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Validation;
using STS2RitsuLib;

namespace SeedOracle.UI;

[HarmonyPatch(typeof(NMapPoint), "OnFocus")]
internal static class NMapPointOnFocusPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NMapPoint __instance)
        => Render(__instance);

    internal static void Render(NMapPoint __instance)
    {
        RouteNodeMarkerOverlay.ClearAll();
        if (RoutePlanPanel.IsPlanMode)
        {
            // While planning, clicks and hover should stay quiet: the plan
            // overlay owns the map, forecast windows would only cover it.
            return;
        }
        if (__instance.State == MapPointState.Traveled || __instance._runState is not RunState run)
            return;

        try
        {
            var forecast = PredictionPurityGuard.Execute(
                run,
                $"map:{__instance.Point.coord}",
                () => Entry.MapForecasts.Predict(run, __instance.Point, __instance._screen.IsTravelEnabled));
            var combatSolver = PreCombatForecastCoordinator.Observe(
                run,
                forecast,
                __instance._screen.IsTravelEnabled);
            var content = MapForecastTooltipBuilder.Build(forecast, combatSolver);
            if (content is null)
                return;
            RouteNodeMarkerOverlay.Show(__instance, content.RouteMarkers, content.RouteLines);

            if (HoverTipHelper.AddTipToOwner(
                    __instance,
                    content.Title,
                    content.Descriptions[0]))
            {
                foreach (var description in content.Descriptions.Skip(1))
                    HoverTipHelper.AddTipToOwner(__instance, content.Title, description);
                MapHoverTipPresentation.Finalize(__instance);
                return;
            }

            var tips = content.Descriptions.Select((description, index) =>
                (IHoverTip)new HoverTip(run.Act.Title, description)
                {
                    Title = content.Title,
                    Id = $"{Entry.ModId}:{__instance.Point.coord}:{index}"
                });
            var tipSet = NHoverTipSet.CreateAndShow(
                __instance,
                tips,
                HoverTip.GetHoverTipAlignment(__instance));
            MapHoverTipPresentation.Finalize(__instance, tipSet);
        }
        catch (Exception exception)
        {
            RouteNodeMarkerOverlay.ClearAll();
            Entry.ReportPredictionFailure(__instance.Point, exception);
        }
    }
}

internal static class MapHoverTipPresentation
{
    internal const int HoverTipZIndex = 200;
    internal const float ViewportMargin = 12f;

    public static void Finalize(NMapPoint owner, NHoverTipSet? knownTipSet = null)
    {
        var tipSet = knownTipSet ?? FindActiveTipSet(owner);
        if (tipSet is null)
            return;

        var alignment = HoverTip.GetHoverTipAlignment(owner);
        Apply(tipSet, owner, alignment);
        Callable.From(() => Apply(tipSet, owner, alignment)).CallDeferred();
    }

    private static NHoverTipSet? FindActiveTipSet(Control owner)
    {
        return NHoverTipSet._activeHoverTips.TryGetValue(owner, out var tipSet)
            ? tipSet
            : null;
    }

    private static void Apply(
        NHoverTipSet tipSet,
        Control owner,
        HoverTipAlignment alignment)
    {
        if (!GodotObject.IsInstanceValid(tipSet)
            || tipSet.IsQueuedForDeletion()
            || !GodotObject.IsInstanceValid(owner)
            || owner.IsQueuedForDeletion())
        {
            return;
        }

        tipSet.ZIndex = HoverTipZIndex;
        tipSet.SetAlignment(owner, alignment);
        ClampTextPanelsToViewport(tipSet);
    }

    private static void ClampTextPanelsToViewport(NHoverTipSet tipSet)
    {
        var game = NGame.Instance;
        var container = tipSet._textHoverTipContainer;
        if (game is null
            || container is null
            || !GodotObject.IsInstanceValid(container))
        {
            return;
        }

        var bounds = GetVisiblePanelBounds(container);
        if (!bounds.HasArea())
            return;

        var offset = CalculateViewportClampOffset(bounds, game.GetViewportRect().Size);
        if (offset.LengthSquared() > Mathf.Epsilon)
            container.GlobalPosition += offset;
    }

    private static Rect2 GetVisiblePanelBounds(Control container)
    {
        var hasPanel = false;
        var bounds = default(Rect2);
        foreach (var panel in container.GetChildren().OfType<Control>())
        {
            if (!panel.Visible)
                continue;

            var panelRect = panel.GetGlobalRect();
            if (!panelRect.HasArea())
                continue;

            bounds = hasPanel ? bounds.Merge(panelRect) : panelRect;
            hasPanel = true;
        }

        return hasPanel ? bounds : container.GetGlobalRect();
    }

    internal static Vector2 CalculateViewportClampOffset(Rect2 bounds, Vector2 viewportSize)
    {
        var targetX = ClampLeadingEdge(bounds.Position.X, bounds.Size.X, viewportSize.X);
        var targetY = ClampLeadingEdge(bounds.Position.Y, bounds.Size.Y, viewportSize.Y);
        return new Vector2(targetX, targetY) - bounds.Position;
    }

    private static float ClampLeadingEdge(float position, float size, float viewportExtent)
    {
        var maximum = viewportExtent - ViewportMargin - size;
        return maximum < ViewportMargin
            ? ViewportMargin
            : Mathf.Clamp(position, ViewportMargin, maximum);
    }
}

[HarmonyPatch(typeof(NMapScreen), "CleanUp")]
internal static class NMapScreenCleanUpPatch
{
    [HarmonyPrefix]
    private static void Prefix() => RouteNodeMarkerOverlay.ClearAll();
}
