#if DEBUG
using System.Diagnostics;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class ExtendedForecastRegressionTests
{
    internal static void Run(RunState live)
    {
        if (Environment.GetEnvironmentVariable("SEED_ORACLE_SELF_TEST") != "1") return;
        var original = LiveFingerprint.Capture(live);
        var planning = new PlanningPredictionService();
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var source = planning.CreateState(live, player);
        var snapshot = planning.Capture(source);
        var noWing = planning.Restore(snapshot);
        var withWing = planning.Restore(snapshot);
        planning.AddRelic(withWing, ModelDb.Relic<PaelsWing>().Id);
        var encounter = MerchantChoiceBranchService.ResolveEncounter(source, RoomType.Monster);
        using var a = planning.GenerateCombatRewards(noWing, RoomType.Monster, encounter);
        using var b = planning.GenerateCombatRewards(withWing, RoomType.Monster, encounter);
        var alternativeChanges = ForecastChoiceDiffFormatter.Combat(a, b, true);
        var extra = b.CardRewardGroups[0].Steps[0].Alternatives.Single(alternative =>
            alternative.OptionId.Equals("SACRIFICE", StringComparison.OrdinalIgnoreCase));
        var choices = a.Detach() with
        {
            ReferenceCondition = "未领取帕尔之翼",
            ChoiceBranches = [new("领取帕尔之翼", b.Detach())]
        };
        var text = new List<string>();
        MapForecastTooltipBuilder.AppendRewards(text, new([], RoomType.Monster, null, null, null, true,
            Forecast<CombatRewardDetails>.Branch(choices, PredictionDependency.Rewards, "fixture")), true);
        var partial = a.Detach() with { PossibilitiesTruncated = true };
        var mergedCoverage = RewardForecastSummary.Group(a.Detach(), "完整结果", [],
            [new("预算截断", partial)], ForecastContentKey.Combat).Count;
        Smoke.SmokeReport.Add($"extended-findings alternatives_native={extra.Title} difference_lines={alternativeChanges.Count} tooltip_contains_alternative={text.Any(line => line.Contains(extra.Title))} complete_vs_partial_groups={mergedCoverage}");
        Require(alternativeChanges.Any(line => line.Contains(extra.Title)) && text.Any(line => line.Contains(extra.Title)),
            "native alternative-only changes must remain visible in reward previews and branch differences");
        Require(mergedCoverage == 2, "complete and incomplete preview coverage must not merge");
        var missingPreview = a.Detach() with
        {
            Possibilities = [new(0, [0], a.CardRewardGroups)], PossibilitiesTruncated = false
        };
        Require(!ForecastChoiceDiffFormatter.Combat(missingPreview, partial, true)
                .Any(line => line.Contains("→ 无")),
            "uncomputed rerolls must not be reported as lost cards");
        var newlyEvaluated = ForecastChoiceDiffFormatter.Combat(partial, missingPreview, true);
        Require(newlyEvaluated.Any(line => line.Contains("新增已算")) && !newlyEvaluated.Any(line => line.Contains("无 →")),
            "newly evaluated combinations must show concrete cards without inventing an empty earlier result");
        RoomEntryRegressionTests.Run(live);
        var lampState = planning.Restore(snapshot);
        planning.AddRelic(lampState, ModelDb.Relic<Driftwood>().Id);
        planning.AddRelic(lampState, ModelDb.Relic<LavaLamp>().Id);
        lampState.Player.Relics.OfType<LavaLamp>().Single().TookDamageThisCombat = false;
        var lampBefore = planning.Capture(lampState);
        using (var lampBase = planning.GenerateCombatRewards(lampState, RoomType.Monster, encounter))
        {
            var preview = CombatRewardBranchService.AddPossibilities(planning, lampBefore, RoomType.Monster, encounter, lampBase.Detach());
            var replay = planning.Restore(lampBefore);
            using var native = planning.GenerateCombatRewards(replay, RoomType.Monster, encounter);
            using (PlanningPredictionService.EnterRewardRoom(replay, encounter)) native.NativeCardRewards[0].Reroll();
            var expectedCards = native.NativeCardRewards[0].Cards.Select(ForecastItemDetails.Card).Select(ForecastContentKey.Item);
            Require(preview.Possibilities.Single(branch => branch.RerollOrder.SequenceEqual(new[] { 0 })).Groups[0].Cards
                    .Select(card => ForecastContentKey.Item(card.Item)).SequenceEqual(expectedCards),
                "Lava Lamp reroll must preserve native combat-room hook context");
            var planned = planning.Restore(lampBefore);
            using var plannedBase = planning.GenerateCombatRewards(planned, RoomType.Monster, encounter);
            var result = RoutePlanForecastService.ApplyCombatRewards(planned, plannedBase,
                new RoutePlanChoice.Combat([new(0, [new(AlternativeId: "REROLL")])], null, false));
            Require(result.CardRewardGroups[0].Cards.Select(card => ForecastContentKey.Item(card.Item)).SequenceEqual(expectedCards),
                "planned reroll must retain the same native room-dependent upgrades");
        }
        Smoke.SmokeReport.Add("extended-reward-context PASS: lava_lamp_reroll=native_matched planned_reroll=native_matched");
        using (ShadowIsolation.Enter(source.Player))
        {
            var card = source.Run.CreateCard(ModelDb.Card<DefendIronclad>(), source.Player);
            CardCmd.Enchant<Goopy>(card, 3);
            var item = ForecastItemDetails.Card(card);
            var caption = card.Enchantment!.Title.GetFormattedText();
            Require(ForecastItemFormatter.Format(item, false).Contains(caption)
                    && ForecastItemFormatter.Format(item, true).Contains(caption),
                "thumbnail mode must retain native enchantment identity and amount");
            var itemKey = ForecastContentKey.Item(item);
            Require(itemKey != ForecastContentKey.Item(item with { IsUpgraded = true }),
                "display-key cache must distinguish immutable record copies");
        }

        var stress = planning.Restore(snapshot);
        planning.AddRelic(stress, ModelDb.Relic<Driftwood>().Id);
        planning.AddRelic(stress, ModelDb.Relic<PrayerWheel>().Id);
        planning.AddCard(stress, ModelDb.Card<TheHunt>().Id, false);
        var previewBefore = planning.Capture(stress);
        var previewState = planning.Restore(previewBefore);
        using (var baseline = planning.GenerateCombatRewards(previewState, RoomType.Monster, encounter))
        {
            using var noBudget = CombatRewardBranchService.LimitPreviewWork(() => false);
            var limited = CombatRewardBranchService.AddPossibilities(planning, previewBefore, RoomType.Monster,
                encounter, baseline.Detach());
            Require(limited.Possibilities.Count == 0 && limited.PossibilitiesTruncated,
                "exhausted outer budget must stop inner preview work and preserve incomplete coverage");
            var limitedText = new List<string>();
            MapForecastTooltipBuilder.AppendRewards(limitedText, new([], RoomType.Monster, null, null, null, true,
                Forecast<CombatRewardDetails>.Branch(limited, PredictionDependency.Rewards, "fixture")), true);
            Require(!limitedText.Any(line => line.Contains("0–3")),
                "budget-limited Hunt preview must not claim uncomputed trigger counts");
        }
        var map = new RestCombatMap();
        stress.Run.Map = map;
        stress.Run.ClearVisitedMapCoordsDebug();
        stress.Run.AddVisitedMapCoord(map.StartingMapPoint.coord);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        ForecastWorkProfile.Start();
        var forecast = new MapForecastService(Entry.RandomForeseer).Predict(stress.Run, map.Target, true);
        var milliseconds = timer.Elapsed.TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        foreach (var line in ForecastWorkProfile.Stop("multi-reward-hover")) Smoke.SmokeReport.Add(line);
        Require(forecast.RouteVariants.Any(variant => variant.CombatRewards is { HasValue: true }),
            "stress hover must produce actual rewards");
        timer.Restart();
        allocated = GC.GetAllocatedBytesForCurrentThread();
        var tooltip = MapForecastTooltipBuilder.Build(forecast)!;
        var tooltipMs = timer.Elapsed.TotalMilliseconds;
        var tooltipBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        timer.Restart();
        var pages = ForecastTooltipPaging.CreatePages(tooltip.Descriptions, 720);
        Require(pages.Count < 30, "budget omissions must not expand a reward tooltip to hundreds of pages");
        Smoke.SmokeReport.Add($"extended-layout sample: tooltip_ms={tooltipMs:F2} tooltip_bytes={tooltipBytes} pages={pages.Count} paging_ms={timer.Elapsed.TotalMilliseconds:F2} chars={tooltip.Descriptions.Sum(text => text.Length)}");
        Require(original.DescribeDifference(LiveFingerprint.Capture(live)) is null,
            "extended regression changed the real run");
        Smoke.SmokeReport.Add($"extended-hover sample: rest_sites=2 driftwood=1 wheel=1 hunt=1 ms={milliseconds:F2} bytes={bytes} live=unchanged");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Extended regression: " + message);
    }

    private sealed class RestCombatMap : ActMap
    {
        protected override MapPoint?[,] Grid { get; } = new MapPoint[1, 4];
        public override MapPoint StartingMapPoint { get; } = new(0, 0) { PointType = MapPointType.Ancient };
        public override MapPoint BossMapPoint { get; } = new(0, 4) { PointType = MapPointType.Boss };
        internal MapPoint Target => Grid[0, 3]!;

        internal RestCombatMap()
        {
            MapPoint previous = StartingMapPoint;
            for (var row = 1; row <= 3; row++)
            {
                var point = new MapPoint(0, row) { PointType = row < 3 ? MapPointType.RestSite : MapPointType.Monster };
                Grid[0, row] = point;
                previous.AddChildPoint(point);
                previous = point;
            }
            previous.AddChildPoint(BossMapPoint);
        }
    }
}
#endif
