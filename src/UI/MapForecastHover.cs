using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.UI;

// One pending request, serviced by the map's existing frame callback. Passing
// time in makes cancellation and rapid mouse movement testable without timers
// or background work touching native game models.
internal sealed class StableHoverRequest<T>(int delayMilliseconds) where T : class
{
    internal T? Owner { get; private set; }
    private long readyAt;

    internal void Request(T owner, long now)
    {
        Owner = owner;
        readyAt = now + delayMilliseconds;
    }

    internal void Cancel(T? owner = null)
    {
        if (owner is null || ReferenceEquals(owner, Owner)) Owner = null;
    }

    internal T? TakeReady(long now)
    {
        if (now < readyAt) return null;
        var owner = Owner;
        Owner = null;
        return owner;
    }
}

internal static class MapForecastHover
{
    private static readonly StableHoverRequest<NMapPoint> Pending = new(80);

    internal static void Cancel(NMapPoint? owner = null) => Pending.Cancel(owner);

    internal static void Request(NMapPoint owner)
    {
        Pending.Cancel();
        RouteNodeMarkerOverlay.ClearAll();
        ForecastTooltipPaging.Clear();
        if (!MapForecastDisplay.Enabled || owner.State == MapPointState.Traveled
            || owner._runState is not RunState run) return;
        try
        {
            // Adjacent nodes are cheap and remain immediate. A previously
            // computed distant node also appears immediately, after the same
            // full state validation used by normal prediction.
            if (owner.Point.coord.row <= (run.CurrentMapPoint?.coord.row ?? -1) + 1)
                NMapPointOnFocusPatch.Render(owner);
            else if (Entry.MapForecasts.TryGetCached(run, owner.Point, owner._screen.IsTravelEnabled, out var cached))
                NMapPointOnFocusPatch.Render(owner, cached);
            else
                Pending.Request(owner, System.Environment.TickCount64);
        }
        catch (Exception exception) { Entry.ReportPredictionFailure(owner.Point, exception); }
    }

    internal static void Process(NMapScreen screen)
    {
        if (Pending.Owner is not { } owner) return;
        if (!GodotObject.IsInstanceValid(owner) || owner.IsQueuedForDeletion()
            || !owner.IsFocused || !owner.IsVisibleInTree() || !MapForecastDisplay.Enabled)
        {
            Pending.Cancel();
            return;
        }
        if (!ReferenceEquals(owner._screen, screen)) return;
        if (!screen.IsOpen)
        {
            Pending.Cancel();
            return;
        }
        if (screen._isDragging)
        {
            Pending.Request(owner, System.Environment.TickCount64);
            return;
        }
        if (Pending.TakeReady(System.Environment.TickCount64) is not null)
            NMapPointOnFocusPatch.Render(owner);
    }
}

[HarmonyPatch(typeof(NMapPoint), "OnUnfocus")]
internal static class NMapPointCancelForecastPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapPoint __instance) => MapForecastHover.Cancel(__instance);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._Process))]
internal static class NMapScreenPendingForecastPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) => MapForecastHover.Process(__instance);
}
