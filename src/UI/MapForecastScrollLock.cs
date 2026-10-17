using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace SeedOracle.UI;

internal static class MapForecastScrollLock
{
    internal static bool ShiftHeld => MapForecastDisplay.Enabled
        && (Input.IsKeyPressed(Key.Shift) || Input.IsPhysicalKeyPressed(Key.Shift));

    internal static void Freeze(NMapScreen screen)
    {
        screen._isDragging = false;
        screen._targetDragPos = screen._mapContainer.Position;
    }

    internal static bool HandleScroll(NMapScreen screen, InputEvent input)
    {
        if (!MapForecastDisplay.Enabled || input is not InputEventWithModifiers { ShiftPressed: true })
            return false;
        Freeze(screen);
        ForecastTooltipPaging.HandleMapInput(screen, input);
        return true;
    }
}

[HarmonyPatch(typeof(NMapScreen), "ProcessScrollEvent")]
internal static class NMapScreenForecastScrollPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMapScreen __instance, InputEvent inputEvent) =>
        !MapForecastScrollLock.HandleScroll(__instance, inputEvent);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._Input))]
internal static class NMapScreenForecastModifierInputPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMapScreen __instance, InputEvent inputEvent)
    {
        if (!__instance.IsOpen)
            return true;
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown }
            && MapForecastScrollLock.HandleScroll(__instance, inputEvent))
        {
            __instance.GetViewport()?.SetInputAsHandled();
            return false;
        }
        if (MapForecastScrollLock.ShiftHeld)
            MapForecastScrollLock.Freeze(__instance);
        return true;
    }
}

[HarmonyPatch(typeof(NMapScreen), "CanScroll")]
internal static class NMapScreenForecastCanScrollPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref bool __result)
    {
        if (MapForecastScrollLock.ShiftHeld)
            __result = false;
    }
}

[HarmonyPatch(typeof(NMapScreen), "UpdateScrollPosition")]
internal static class NMapScreenForecastScrollPositionPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NMapScreen __instance)
    {
        if (!MapForecastScrollLock.ShiftHeld)
            return true;
        MapForecastScrollLock.Freeze(__instance);
        return false;
    }
}
