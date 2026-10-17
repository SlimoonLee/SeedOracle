#if DEBUG
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SeedOracle.Api;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class CausalChoiceRegressionTests
{
    internal static void Run(RunState live)
    {
        var planning = new PlanningPredictionService();
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var fingerprint = LiveFingerprint.Capture(live);
        var profile = SaveManager.ToJson(SaveManager.Instance.Progress.ToSerializable());
        var eventService = new PlanningEventPredictionService();
        var checkedOptions = 0;
        var merchantChanges = 0;
        var rewardChanges = 0;
        var treasureChanges = 0;
        for (var fixture = 0; fixture < 32 && (merchantChanges == 0 || rewardChanges == 0 || treasureChanges == 0); fixture++)
        {
            var seed = "NEOW_DIFF_" + fixture;
            var initial = planning.Capture(planning.CreateState(live, player));
            initial.Run.SerializableRng = new RunRngSet(seed).ToSerializable();
            var start = planning.Restore(initial);
            start.Player.InitializeSeed(seed);
            initial = planning.Capture(start);
            using var isolation = ShadowIsolation.Enter(start.Player, automateRewards: true);
            var model = ModelDb.Event<Neow>().ToMutable();
            model.BeginEvent(start.Player, new EventCombatSynchronizer(start.Run, start.Run), false).GetAwaiter().GetResult();
            var context = CurrentEventChoiceContext.FromEvent(model)!;
            Require(context is not null, "initial Neow was not recognized");
            var input = planning.Capture(start);
            var descriptors = eventService.EnumerateEventOptions(start.Player, context!.Canonical, input);
            if (descriptors.Count != 3 || descriptors.Any(option => option.CardSelections.Count > 0
                    || option.Capability.Kind != PlanningEventPredictionService.EventPlanningKind.Exact))
                continue;
            Require(PlanningEventPredictionService.MatchesCurrentInitialPage(planning.Restore(input), context), "active page mismatch");
            var shopRoute = Route(RoomType.Shop, MapPointType.Shop);
            var combatRoute = Route(RoomType.Elite, MapPointType.Elite);
            var treasureRoute = Route(RoomType.Treasure, MapPointType.Treasure);
            var shop = new MerchantChoiceBranchService(512, 32, 30000, context).Predict(start.Player, shopRoute,
                planning.GenerateMerchant(planning.Restore(input)));
            var encounter = MerchantChoiceBranchService.ResolveEncounter(start, RoomType.Elite);
            using var baseRewards = planning.GenerateCombatRewards(planning.Restore(input), RoomType.Elite, encounter);
            var rewards = new MerchantChoiceBranchService(512, 32, 30000, context).PredictRewards(start.Player, combatRoute, baseRewards.Detach());
            var chest = new MerchantChoiceBranchService(512, 32, 30000, context).PredictTreasure(start.Player, treasureRoute,
                planning.GenerateTreasure(planning.Restore(input), false));
            foreach (var option in descriptors)
            {
                // Execute the real Neow lifecycle from its pre-entry snapshot,
                // independently of the planning event executor.
                var native = planning.Restore(initial);
                using var nativeIsolation = ShadowIsolation.Enter(native.Player, automateRewards: true);
                var nativeEvent = ModelDb.Event<Neow>().ToMutable();
                nativeEvent.BeginEvent(native.Player, new EventCombatSynchronizer(native.Run, native.Run), false).GetAwaiter().GetResult();
                Require(nativeEvent.CurrentOptions[option.Index].TextKey == option.TextKey, "native option changed");
                nativeEvent.CurrentOptions[option.Index].Chosen().GetAwaiter().GetResult();
                Require(nativeEvent.IsFinished && CurrentEventChoiceContext.FromEvent(nativeEvent) is null,
                    "completed Neow must not be replayed as a current choice");
                var after = planning.Capture(native);
                var merchantState = planning.Restore(after);
                merchantState.Run.ActFloor = 2;
                var nativeShop = planning.GenerateMerchant(merchantState);
                Require(MerchantOutcomes(shop).Any(outcome => outcome.Condition.Contains(option.Title)
                        && ForecastContentKey.Merchant(outcome.Inventory) == ForecastContentKey.Merchant(nativeShop)),
                    "Neow option did not reach native merchant: " + option.Title);
                var rewardState = planning.Restore(after);
                rewardState.Run.ActFloor = 2;
                using var nativeRewards = planning.GenerateCombatRewards(rewardState, RoomType.Elite, encounter);
                Require(rewards.ChoiceBranches.Prepend(new(rewards.ReferenceCondition!, rewards with
                        { ReferenceCondition = null, ChoiceBranches = [] })).Any(outcome => outcome.Condition.Contains(option.Title)
                        && ForecastContentKey.Combat(outcome.Outcome) == ForecastContentKey.Combat(nativeRewards)),
                    "Neow option did not reach native combat rewards: " + option.Title);
                var treasureState = planning.Restore(after);
                treasureState.Run.ActFloor = 2;
                var nativeChest = planning.GenerateTreasure(treasureState, false);
                Require(chest.ChoiceBranches.Prepend(new(chest.ReferenceCondition!, chest with
                        { ReferenceCondition = null, ChoiceBranches = [] })).Any(outcome => outcome.Condition.Contains(option.Title)
                        && ForecastContentKey.Treasure(outcome.Outcome) == ForecastContentKey.Treasure(nativeChest)),
                    "Neow option did not reach native treasure: " + option.Title);
                checkedOptions++;
            }
            var lines = new List<string>();
            MapForecastTooltipBuilder.AppendMerchantContents(lines, shop, true);
            var differentShops = shop.ChoiceBranches.Count(branch => ForecastChoiceDiffFormatter.Merchant(shop, branch.Inventory, true).Count > 0);
            Require(differentShops == 0 || lines.Any(line => line.Contains("相对基础")), "merchant tooltip omitted concrete replacement");
            Require(!lines.Any(line => line.Contains("未覆盖") || line.Contains("重算") || line.Contains("未穷尽")), "merchant boilerplate survived");
            if (merchantChanges == 0 && differentShops > 0)
            {
                Smoke.SmokeReport.Add("causal-choice EXAMPLE seed=" + seed + " " + string.Join(" | ", lines));
                ValidateThroughCombat(planning, initial, start, context, descriptors);
            }
            merchantChanges += differentShops;
            rewardChanges += rewards.ChoiceBranches.Count(branch => ForecastChoiceDiffFormatter.Combat(rewards, branch.Outcome, true).Count > 0);
            treasureChanges += chest.ChoiceBranches.Count(branch => ForecastChoiceDiffFormatter.Treasure(chest, branch.Outcome, true).Count > 0);
            Require(PlanningPredictionService.StateToken(start) == PlanningPredictionService.StateToken(input), "Neow branch mutated input");
        }
        Require(checkedOptions >= 3 && merchantChanges > 0 && rewardChanges > 0 && treasureChanges > 0,
            $"missing concrete fixtures: options={checkedOptions}, shops={merchantChanges}, rewards={rewardChanges}, treasure={treasureChanges}");
        ValidateDiffs(player.Deck.Cards[0]);
        ForecastConditionRegressionTests.Run(planning, live, player);
        ShopPurchaseRegressionTests.Run(planning, live, player);
        Require(fingerprint.DescribeDifference(LiveFingerprint.Capture(live)) is null, "Neow fixtures changed the live run");
        Require(SaveManager.ToJson(SaveManager.Instance.Progress.ToSerializable()) == profile,
            "Neow fixtures changed profile progress");
        Smoke.SmokeReport.Add($"causal-choice-regression PASS: native_neow_options={checkedOptions} shop_changes={merchantChanges} "
            + $"reward_changes={rewardChanges} treasure_changes={treasureChanges} default_budget_combat_to_shop=passed empty_hints=suppressed live_and_profile=unchanged");
    }

    private static void ValidateThroughCombat(PlanningPredictionService planning,
        PlanningPredictionService.StateSnapshot beforeNeow, PlanningPredictionService.State current,
        CurrentEventChoiceContext context, IReadOnlyList<PlanningEventPredictionService.EventOptionDescriptor> options)
    {
        var combat = new MapPoint(0, 1) { PointType = MapPointType.Monster };
        var shopPoint = new MapPoint(0, 2) { PointType = MapPointType.Shop };
        combat.AddChildPoint(shopPoint);
        var route = new RouteWorldline(new RoutePath([new(combat, false, false), new(shopPoint, false, false)]), [],
            RoomType.Shop, null, null, 1, [RoomType.Monster, RoomType.Shop], true);
        var baseline = planning.GenerateMerchant(planning.Restore(planning.Capture(current)));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var predicted = new MerchantChoiceBranchService(currentEvent: context).Predict(current.Player, route, baseline);
        var elapsed = clock.ElapsedMilliseconds;
        foreach (var option in options)
        {
            var native = planning.Restore(beforeNeow);
            using var isolation = ShadowIsolation.Enter(native.Player, automateRewards: true);
            var model = ModelDb.Event<Neow>().ToMutable();
            model.BeginEvent(native.Player, new EventCombatSynchronizer(native.Run, native.Run), false).GetAwaiter().GetResult();
            model.CurrentOptions[option.Index].Chosen().GetAwaiter().GetResult();
            native.Run.ActFloor = 2;
            using (var rewards = planning.GenerateCombatRewards(native, RoomType.Monster,
                       MerchantChoiceBranchService.ResolveEncounter(native, RoomType.Monster)))
            {
                var skip = new RoutePlanChoice.Combat(rewards.CardRewardGroups.Select((_, group) =>
                    new RoutePlanChoice.CardReward(group, [new CardRewardPick(AlternativeId: "Skip")])).ToArray(), null, false);
                RoutePlanForecastService.ApplyCombatRewards(native, rewards, skip);
            }
            native.Run.Act._rooms.normalEncountersVisited++;
            native.Run.AddVisitedMapCoord(combat.coord);
            native.Run.ActFloor = 3;
            var expected = planning.GenerateMerchant(native);
            Require(MerchantOutcomes(predicted).Any(outcome => outcome.Condition.Contains(option.Title)
                    && ForecastContentKey.Merchant(outcome.Inventory) == ForecastContentKey.Merchant(expected)),
                "normal budget lost current Neow option after combat: " + option.Title);
        }
        Smoke.SmokeReport.Add($"causal-choice default-budget PASS: neow_options=3 prior_combats=1 elapsed_ms={elapsed}");
    }

    private static IEnumerable<MerchantChoiceBranch> MerchantOutcomes(MerchantInventoryForecast stock) =>
        stock.ChoiceBranches.Prepend(new(stock.ReferenceCondition!, stock with
            { ReferenceCondition = null, ChoiceBranches = [], UnresolvedChoices = [], ChoicesTruncated = false }));

    private static void ValidateDiffs(CardModel card)
    {
        var item = new MerchantItemForecast(ForecastItemDetails.Card(card), 100);
        var stock = new MerchantInventoryForecast([item], [], [], [], 75, 1);
        var changed = stock with { ReferenceCondition = "涅奥：选甲", ChoiceBranches =
            [new("涅奥：选乙", stock with { CharacterCards = [item with { Cost = 50, IsOnSale = true }] }),
             new("同库存", stock)] };
        var lines = new List<string>();
        MapForecastTooltipBuilder.AppendMerchantContents(lines, changed, true);
        Require(string.Join("\n", lines).Contains("100")
            && lines.Any(line => line.Contains("①") && line.Contains("50金") && line.Contains("-50") && line.Contains("半价")), "price/sale diff missing");
        Require(lines.Count(line => line.StartsWith("角色卡：")) == 1
            && lines.Any(line => line.Contains("涅奥：选甲") && line.Contains("同库存")),
            "identical inventory must share one full stock and keep its choice conditions");
        var upgraded = stock with { CharacterCards = [item with { Item = item.Item with { IsUpgraded = true } }] };
        Require(ForecastChoiceDiffFormatter.Merchant(stock, upgraded, true).Count > 0, "upgrade-only change was hidden");
        var cheaper = stock with { CharacterCards = [item with { Cost = 50, IsOnSale = true }], CardRemovalCost = 50 };
        var cases = new MerchantForecastCase[]
        {
            new("路线甲：选一", stock, 1), new("路线乙：选二", cheaper, 3),
            new("路线丙：选三", cheaper with { FutureVisitOrdinal = 2 }, 2), new("路线丁：升级", upgraded, 1)
        };
        var grouped = MerchantForecastSummary.Group(cases);
        Require(grouped.Count == 3 && grouped[0].Weight == 5 && grouped[0].Conditions.Count == 2
            && grouped[0].Inventory.CharacterCards[0].Cost == 50,
            "modal stock must combine equal inventories across routes without merging upgrades");
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantSummary(lines, cases, true);
        Require(lines.Any(line => line.Contains("路线乙：选二") && line.Contains("路线丙：选三"))
            && lines.Count(line => line.StartsWith("角色卡：")) == 1
            && lines.Any(line => line.Contains("+50")) && lines.Any(line => line.Contains("+25金")),
            "merged shop must use modal stock once and show signed price/removal changes");
        var variantA = new RouteVariantForecast([new(new MapPoint(0, 0), 1, 1, true, false)], RoomType.Shop, null, null,
            Forecast<MerchantInventoryForecast>.Exact(stock), false);
        var variantB = variantA with { Route = [new(new MapPoint(1, 0), 1, 2, true, false)],
            Merchant = Forecast<MerchantInventoryForecast>.Exact(cheaper) };
        var variantC = variantB with { Route = [new(new MapPoint(2, 0), 1, 3, true, false)] };
        var node = new MapNodeForecast(new MapPoint(0, 1) { PointType = MapPointType.Shop }, 1, 1,
            null, null, null, null, null, [variantA, variantB, variantC], false);
        var tooltip = MapForecastTooltipBuilder.Build(node)!;
        Require(tooltip.Descriptions.Count == 1
            && tooltip.Descriptions[0].Split('\n').Count(line => line.StartsWith("角色卡：")) == 1
            && tooltip.Descriptions[0].Split('\n').Single(line => line.StartsWith("角色卡：")).Contains("50"),
            "route breakdown must combine shop forecasts into one base-plus-diffs panel");
        Smoke.SmokeReport.Add("merchant-summary PASS: cases=4 stocks=3 modal_weight=5 full_stock=once route_groups=combined price_and_removal=signed upgrades=distinct");
    }

    private static RouteWorldline Route(RoomType room, MapPointType pointType)
    {
        var point = new MapPoint(0, 1) { PointType = pointType };
        return new(new RoutePath([new RouteStep(point, false, false)]), [], room, null, null, 1, [room], true);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Causal choice regression: " + message);
    }
}
#endif
