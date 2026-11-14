using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using SeedOracle.Forecasting;
using SeedOracle.Validation;

namespace SeedOracle.UI;

/// <summary>
/// Blitz-mode map forecasts: the top icon strip's nodes (NBlitzTopBarMapPoint)
/// get the same predictions the normal map shows — the hovered node's
/// encounter and reward drops, plus a summary of the ancients and bosses still
/// ahead on the route. The game's OnFocus already created its legend tip set;
/// this postfix appends our panels into that set's flow container, so they
/// inherit its position and its lifetime (OnUnfocus removes the whole set).
/// </summary>
internal static class BlitzMapForecastHover
{
    private const string HoverTipScene = "res://scenes/ui/hover_tip.tscn";
    private const int DelayedRenderMilliseconds = 80;
    private static readonly List<(NBlitzTopBarMapPoint Owner, ulong Due)> Pending = [];

    [HarmonyPatch(typeof(NBlitzTopBarMapPoint), "OnFocus")]
    internal static class NBlitzTopBarMapPointOnFocusPatch
    {
        [HarmonyPostfix]
        private static void Postfix(NBlitzTopBarMapPoint __instance)
        {
            try
            {
                if (__instance._runState is not RunState run) return;
                var point = __instance.Point;
        var currentRow = run.MapLocation.coord?.row ?? 0;
        // Only unvisited nodes ahead of the current position; visited
        // ones already show the game's own history tip.
        if (point.coord.row <= currentRow) return;

        BlitzMapForecastHover.Request(__instance, run, point);
            }
            catch (Exception)
            {
                // The forecast must never cost the game its own hover tip.
            }
        }
    }

    private static void Request(NBlitzTopBarMapPoint owner, RunState run, MapPoint point)
    {
        // Adjacent nodes render immediately; further ones after a short delay
        // so scrubbing the strip does not fire a computation per icon.
        var currentRow = run.MapLocation.coord?.row ?? 0;
        var adjacent = point.coord.row <= currentRow + 1;
        if (adjacent)
        {
            Render(owner, run, point);
            return;
        }
        Pending.RemoveAll(pending => !IsUsable(pending.Owner));
        Pending.Add((owner, Time.GetTicksMsec() + (ulong)DelayedRenderMilliseconds));
        owner.GetTree().CreateTimer(DelayedRenderMilliseconds / 1000.0).Timeout += () =>
        {
            if (Pending.RemoveAll(pending => pending.Owner == owner && Time.GetTicksMsec() >= pending.Due) > 0
                && IsUsable(owner))
            {
                Render(owner, run, point);
            }
        };
    }

    private static void Render(NBlitzTopBarMapPoint owner, RunState run, MapPoint point)
    {
        if (!NHoverTipSet._activeHoverTips.TryGetValue(owner, out var tipSet)
            || !GodotObject.IsInstanceValid(tipSet)
            || tipSet.IsQueuedForDeletion())
            return;

        var forecast = PredictionPurityGuard.Execute(
            run, $"blitz-map:{point.coord}", () => Entry.MapForecasts.Predict(run, point, true));
        var tooltip = MapForecastTooltipBuilder.Build(forecast);
        if (tooltip is null) return;

        var chinese = LocManager.Instance?.Language is "zhs" or "zht";
        var container = tipSet._textHoverTipContainer;
        var width = container.GetChildren().OfType<Control>().FirstOrDefault()?.Size.X ?? 300f;

        AppendTipPanel(container, width,
            chinese ? "Seed Oracle · 预测" : "Seed Oracle · forecast",
            tooltip.Descriptions);

        var upcoming = BuildUpcoming(run, point);
        if (upcoming.Count > 0)
        {
            AppendTipPanel(container, width,
                chinese ? "Seed Oracle · 后续先古与 Boss" : "Seed Oracle · upcoming ancients & bosses",
                upcoming);
        }
    }

    /// <summary>
    /// The ancients and bosses still ahead of the hovered node: the current
    /// act's boss (unless the node is that boss) and every later act's ancient
    /// and bosses — plain act-model reads, no simulation.
    /// </summary>
    internal static List<string> BuildUpcoming(RunState run, MapPoint point)
    {
        var lines = new List<string>();
        for (var actIndex = run.CurrentActIndex; actIndex < run.Acts.Count; actIndex++)
        {
            var act = run.Acts[actIndex];
            var parts = new List<string>();
            var bossTitle = act.BossEncounter?.Title.GetFormattedText();
            var isBossNode = actIndex == run.CurrentActIndex
                             && point.PointType == MapPointType.Boss;
            if (!isBossNode && bossTitle is not null)
                parts.Add("Boss：" + bossTitle);
            if (act.SecondBossEncounter is not null
                && (!isBossNode || act.SecondBossEncounter.Title.GetFormattedText() != bossTitle))
                parts.Add("Boss2：" + act.SecondBossEncounter.Title.GetFormattedText());
            if (actIndex > run.CurrentActIndex && act.Ancient is not null)
                parts.Insert(0, "先古：" + act.Ancient.Title.GetFormattedText());
            if (parts.Count > 0)
                lines.Add($"第{actIndex + 1}幕：" + string.Join("，", parts));
        }
        return lines;
    }

    private static void AppendTipPanel(Control container, float width, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        var panel = PreloadManager.Cache.GetScene(HoverTipScene).Instantiate<Control>();
        container.AddChild(panel);
        ForecastTooltipLayout.ConfigurePanel(panel, width);
        panel.GetNode<MegaLabel>("%Title").SetTextAutoSize(title);
        panel.GetNode<MegaRichTextLabel>("%Description").Text = string.Join("\n", lines);
        panel.ResetSize();
    }

    private static bool IsUsable(NBlitzTopBarMapPoint? owner) =>
        owner is not null && GodotObject.IsInstanceValid(owner) && !owner.IsQueuedForDeletion();
}
