#if DEBUG
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.Smoke;

namespace SeedOracle.Validation;

// Observe actual native rooms and rewards during the isolated AutoSlay run.
// Prediction is captured before map travel; actual rewards are read only after
// native combat and reward hooks. This is independent of the shadow generator.
internal static class LivePredictionAudit
{
    private sealed record Pending(MapCoord Coord, MapNodeForecast Forecast, SerializableRun Before);
    private static Pending? pending;
    private static CombatRoom? lastCombat;
    private static int rewardsMatched, rewardsDiffered, hpMatched, hpDiffered, encountersMatched, encountersDiffered, errors;
    internal static bool Enabled => SmokeRunner.IsRequested
        && Environment.GetEnvironmentVariable("SEED_ORACLE_LIVE_PREDICTION_AUDIT") == "1";

    internal static void Capture(RunState run, MapCoord coord)
    {
        if (!Enabled || run.VisitedMapCoords.Contains(coord)) return;
        var player = LocalContext.GetMe(run) ?? run.Players[0];
        if (ShadowIsolation.IsShadowPlayer(player)) return;
        pending = null;
        try
        {
            var point = run.Map.GetPoint(coord);
            if (point is null) return;
            var before = PlanningPredictionService.CreateSerializableRun(run);
            var forecast = PredictionPurityGuard.Execute(run, "audit:before-real-travel",
                () => Entry.MapForecasts.Predict(run, point, true));
            pending = new(coord, forecast, before);
        }
        catch (Exception exception)
        {
            errors++;
            SmokeReport.Add("live-prediction ERROR: capture " + exception.GetBaseException().Message);
        }
    }

    internal static void BeforeCombatKill(CombatState combat)
    {
        if (!Enabled || pending is not { } captured || combat.RunState is not RunState run
            || run.CurrentRoom is not CombatRoom room || ReferenceEquals(lastCombat, room)) return;
        lastCombat = room;
        var expected = captured.Forecast.Encounter;
        if (expected is { HasValue: true } && expected.Value!.Count == 1)
        {
            if (expected.Value[0].Id == room.Encounter.Id) encountersMatched++;
            else { encountersDiffered++; ReportDifference("encounter", expected.Value, room.Encounter.Id.ToString(), captured); }
        }
        if (captured.Forecast.MonsterHp is { HasValue: true } hp)
        {
            var actual = combat.Enemies.Select(enemy => new MonsterHpDetails(enemy.Monster!.Title.GetFormattedText(), enemy.MaxHp)).ToArray();
            if (JsonSerializer.Serialize(hp.Value) == JsonSerializer.Serialize(actual)) hpMatched++;
            else { hpDiffered++; ReportDifference("hp", hp.Value, actual, captured); }
        }
    }

    internal static void ObserveRewards(RewardsSet rewards)
    {
        if (!Enabled || pending is not { } captured || ShadowIsolation.IsShadowPlayer(rewards.Player)
            || rewards.Player.RunState.CurrentRoom is not CombatRoom) return;
        try
        {
            var predicted = captured.Forecast.RouteVariants.Select(variant => variant.CombatRewards)
                .FirstOrDefault(value => value is { HasValue: true })?.Value;
            if (predicted is null) return;
            var actual = PlanningPredictionService.DescribeRewards(rewards).Detach();
            var expected = predicted with { Possibilities = [], PossibilitiesTruncated = false, HuntPreviewLimit = null };
            if (ForecastContentKey.Combat(expected) == ForecastContentKey.Combat(actual)) rewardsMatched++;
            else { rewardsDiffered++; ReportDifference("rewards", expected, actual, captured); }
            SmokeReport.Add($"live-prediction combat: coord={captured.Coord} character={rewards.Player.Character.Id} matches={rewardsMatched} differences={rewardsDiffered}");
        }
        catch (Exception exception)
        {
            errors++;
            SmokeReport.Add("live-prediction ERROR: rewards " + exception.GetBaseException().Message);
        }
    }

    private static void ReportDifference(string kind, object? expected, object? actual, Pending captured)
    {
        File.AppendAllText("seed_oracle_live_prediction_audit.jsonl", JsonSerializer.Serialize(new
        {
            kind, captured.Coord, expected, actual,
            snapshot = JsonSerializer.Serialize(captured.Before, JsonSerializationUtility.GetTypeInfo<SerializableRun>())
        }) + "\n");
        SmokeReport.Add($"live-prediction DIFFERENCE: kind={kind} coord={captured.Coord}");
    }

    internal static void Summarize()
    {
        if (Enabled) SmokeReport.Add($"live-prediction SUMMARY: rewards_match={rewardsMatched} rewards_differ={rewardsDiffered} hp_match={hpMatched} hp_differ={hpDiffered} encounter_match={encountersMatched} encounter_differ={encountersDiffered} errors={errors}");
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.EnterMapCoord))]
internal static class LivePredictionBeforeTravelPatch
{
    [HarmonyPrefix]
    private static void Prefix(RunManager __instance, MapCoord coord)
    {
        if (LivePredictionAudit.Enabled && __instance.DebugOnlyGetState() is { } run)
            LivePredictionAudit.Capture(run, coord);
    }
}

[HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen.ShowScreen))]
internal static class LivePredictionNativeRewardsPatch
{
    [HarmonyPrefix]
    private static void Prefix(RewardsSet set) => LivePredictionAudit.ObserveRewards(set);
}
#endif
