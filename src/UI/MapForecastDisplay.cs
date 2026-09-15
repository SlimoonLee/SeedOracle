using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using SeedOracle.Data;

namespace SeedOracle.UI;

internal static class MapForecastDisplay
{
    internal static bool Enabled =>
        RoutePlanPanel.IsPlanMode || SeedOracleData.Settings.ShowMapForecastsOutsidePlan;

    // Settings and plan-mode changes must also dismiss previews already on screen.
    internal static void Refresh()
    {
        try
        {
            RouteNodeMarkerOverlay.ClearAll();
            MapHoverTipPresentation.HideForecastTip();
            var screen = NMapScreen.Instance;
            if (screen is not null && GodotObject.IsInstanceValid(screen)
                && !screen.IsQueuedForDeletion() && screen._runState is { } run)
                RoutePlanOverlay.Refresh(screen, run);
        }
        catch (Exception exception)
        {
            Entry.Logger.Error($"Refreshing map forecast visibility failed: {exception}");
        }
    }
}
