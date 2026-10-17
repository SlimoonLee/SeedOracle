#if DEBUG
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SeedOracle.Api;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class RewardChoiceRegressionTests
{
    internal static void Run(RunState live)
    {
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var fingerprint = LiveFingerprint.Capture(live);
        var profile = SaveManager.ToJson(SaveManager.Instance.Progress.ToSerializable());
        var planning = new PlanningPredictionService();
        var checkedOrders = 0;
        var previewCases = 0;
        for (var scenario = 0; scenario < 3; scenario++)
        {
            var state = planning.CreateState(live, player);
            planning.AddRelic(state, ModelDb.Relic<Driftwood>().Id);
            planning.AddRelic(state, scenario == 1 ? ModelDb.Relic<WhiteStar>().Id : ModelDb.Relic<PrayerWheel>().Id);
            if (scenario == 2) planning.AddCard(state, ModelDb.Card<TheHunt>().Id, false);
            state.Player.PlayerRng.Rewards.LoadFromSerializable(new Rng((ulong)(713 + scenario * 311)).ToSerializable());
            var type = scenario == 1 ? RoomType.Elite : RoomType.Monster;
            var encounter = MerchantChoiceBranchService.ResolveEncounter(state, type);
            var snapshot = planning.Capture(state);
            var hunt = scenario == 2 ? 1 : 0;
            var groupCount = scenario == 2 ? 3 : 2;
            foreach (var order in RerollOrders(groupCount))
            {
                var native = planning.Restore(snapshot);
                var actual = planning.Restore(snapshot);
                using var nativeScope = ShadowIsolation.Enter(native.Player, automateRewards: true);
                using var expectedRewards = planning.GenerateCombatRewards(native, type, encounter,
                    assumedHuntTriggers: hunt);
                Require(expectedRewards.NativeCardRewards.Count == groupCount, "native group count");
                var choice = new RoutePlanChoice.Combat([], null, true) { HuntTriggers = hunt };
                foreach (var group in order)
                {
                    expectedRewards.NativeCardRewards[group].Reroll();
                    choice = choice.WithCardRewardChoice(new RoutePlanChoice.CardReward(group,
                        [new CardRewardPick(AlternativeId: "REROLL")]));
                }
                var expectedCards = expectedRewards.NativeCardRewards.Select(card =>
                    PlanningPredictionService.DescribeCardReward(card).Cards).ToArray();
                using var generated = planning.GenerateCombatRewards(actual, type, encounter,
                    assumedHuntTriggers: hunt);
                var result = RoutePlanForecastService.ApplyCombatRewards(actual, generated, choice);
                Require(JsonSerializer.Serialize(result.CardRewardGroups.Select(group => group.Cards))
                        == JsonSerializer.Serialize(expectedCards), "reroll order changed candidate identity or state");
                Require(actual.Player.PlayerRng.Rewards.ToSerializable().counter
                        == native.Player.PlayerRng.Rewards.ToSerializable().counter, "reroll RNG continuation");
                var expectedShop = planning.GenerateMerchant(native);
                var actualShop = planning.GenerateMerchant(actual);
                Require(ForecastContentKey.Merchant(expectedShop) == ForecastContentKey.Merchant(actualShop),
                    "reroll order did not propagate into native downstream stock/prices");
                checkedOrders++;
            }

            var previewState = planning.Restore(snapshot);
            using var baseline = planning.GenerateCombatRewards(previewState, type, encounter);
            var untouched = PlanningPredictionService.StateToken(previewState);
            var preview = CombatRewardBranchService.AddPossibilities(planning, snapshot, type, encounter, baseline.Detach());
            foreach (var possibility in preview.Possibilities)
            {
                var replay = planning.Restore(snapshot);
                using var replayScope = ShadowIsolation.Enter(replay.Player, automateRewards: true);
                using var nativePreview = planning.GenerateCombatRewards(replay, type, encounter,
                    assumedHuntTriggers: possibility.HuntTriggers);
                foreach (var group in possibility.RerollOrder) nativePreview.NativeCardRewards[group].Reroll();
                Require(JsonSerializer.Serialize(possibility.Groups) == JsonSerializer.Serialize(
                        nativePreview.NativeCardRewards.Select(reward => new CombatCardRewardGroup(
                            [PlanningPredictionService.DescribeCardReward(reward)])).ToArray()),
                    "cached post-generation reroll state must match independent native generation and replay");
                previewCases++;
            }
            Require(preview.Possibilities.Any(branch => branch.RerollOrder.SequenceEqual(new[] { 0, 1 }))
                    && preview.Possibilities.Any(branch => branch.RerollOrder.SequenceEqual(new[] { 1, 0 })),
                "ordinary preview must include both reward-group orders");
            Require(untouched == PlanningPredictionService.StateToken(previewState), "preview consumed its input state");
            var lines = new List<string>();
            MapForecastTooltipBuilder.AppendRewards(lines, new RouteVariantForecast([], type, null, null, null,
                true, Forecast<CombatRewardDetails>.Branch(preview, PredictionDependency.Rewards, "fixture")), true);
            Require(lines.Any(line => line.Contains("重掷组 1 > 2"))
                    && lines.Any(line => line.Contains("重掷组 2 > 1")), "ordinary tooltip lost reroll branches");
            if (scenario == 2)
                Require(preview.HuntPreviewLimit == 3 && preview.Possibilities.Any(branch => branch.HuntTriggers > 0)
                        && lines.Any(line => line.Contains("狩猎触发 1 次")), "Hunt conditional coverage");
            ValidateInterleavedPicks(planning, snapshot, type, encounter, hunt);
        }

        ValidatePickup(planning, live, player);
        ValidateEventShop(planning, live, player);
        ValidateCombatShop(planning, live, player);
        Require(fingerprint.DescribeDifference(LiveFingerprint.Capture(live)) is null, "live run changed");
        Require(SaveManager.ToJson(SaveManager.Instance.Progress.ToSerializable()) == profile, "profile discovery changed");
        Smoke.SmokeReport.Add($"choice-branch-regression PASS: native_reroll_orders={checkedOrders} "
            + $"preview_native_cases={previewCases} interleaved_picks=3 pickup=2 event_shop=passed combat_shop=passed empty_hints=suppressed live_and_profile=unchanged");
    }

    private static void ValidateInterleavedPicks(PlanningPredictionService planning,
        PlanningPredictionService.StateSnapshot snapshot, RoomType type, EncounterModel encounter, int hunt)
    {
        var probe = planning.Restore(snapshot);
        using var native = planning.GenerateCombatRewards(probe, type, encounter, assumedHuntTriggers: hunt);
        using var scope = ShadowIsolation.Enter(probe.Player, automateRewards: true);
        native.NativeCardRewards[1].Reroll();
        native.NativeCardRewards[0].Reroll();
        var card1 = ForecastItemDetails.Card(native.NativeCardRewards[1].Cards.First());
        var card0 = ForecastItemDetails.Card(native.NativeCardRewards[0].Cards.Last());
        var choice = new RoutePlanChoice.Combat([], null, true) { HuntTriggers = hunt };
        choice = choice.WithCardRewardChoice(new RoutePlanChoice.CardReward(1, [new(AlternativeId: "REROLL")]));
        choice = choice.WithCardRewardChoice(new RoutePlanChoice.CardReward(0, [new(AlternativeId: "REROLL")]));
        choice = choice.WithCardRewardChoice(new RoutePlanChoice.CardReward(1,
            [new(AlternativeId: "REROLL"), new(0, card1.Id, CardStateKey: card1.StateKey)]));
        choice = choice.WithCardRewardChoice(new RoutePlanChoice.CardReward(0,
            [new(AlternativeId: "REROLL"), new(2, card0.Id, CardStateKey: card0.StateKey)]));
        Require(choice.OrderedActions.Select(action => action.GroupIndex).SequenceEqual(new[] { 1, 0, 1, 0 }),
            "editing a later pick reordered earlier rerolls");
        var actual = planning.Restore(snapshot);
        var before = actual.Player.Deck.Cards.Count;
        using var generated = planning.GenerateCombatRewards(actual, type, encounter, assumedHuntTriggers: hunt);
        var result = RoutePlanForecastService.ApplyCombatRewards(actual, generated, choice);
        Require(result.AppliedDelta?.Cards == 2 && result.CardRewardGroups.Take(2).All(group => group.ChoiceComplete),
            "interleaved picks failed to complete both groups");
        Require(actual.Player.Deck.Cards.Skip(before).Select(card => card.Id).SequenceEqual(new[] { card1.Id, card0.Id }),
            "card obtain order differs from recorded actions");
        Require(result.CardRewardGroups.SelectMany(group => group.Steps).All(step => !step.RejectedChoice),
            "valid interleaved action was rejected");
    }

    private static void ValidatePickup(PlanningPredictionService planning, RunState run, Player player)
    {
        foreach (var relic in new RelicModel[] { ModelDb.Relic<OldCoin>(), ModelDb.Relic<Whetstone>() })
        {
            var actual = planning.CreateState(run, player);
            var expected = planning.Restore(planning.Capture(actual));
            var gold = actual.Player.Gold;
            planning.AddRelic(actual, relic.Id);
            using (ShadowIsolation.Enter(expected.Player, automateRewards: true))
                RelicCmd.Obtain(relic.ToMutable(), expected.Player).GetAwaiter().GetResult();
            Require(PlanningPredictionService.StateToken(actual) == PlanningPredictionService.StateToken(expected),
                "native pickup lifecycle/RNG mismatch: " + relic.Id.Entry);
            if (relic is OldCoin) Require(actual.Player.Gold == gold + 300, "Old Coin pickup gold was lost");
        }
    }

    private static void ValidateEventShop(PlanningPredictionService planning, RunState live, Player player)
    {
        var state = planning.CreateState(live, player);
        state.Run.Act._rooms.events.Clear();
        state.Run.Act._rooms.events.Add(ModelDb.Event<AromaOfChaos>());
        state.Run.Act._rooms.eventsVisited = 0;
        state.Run.Odds.UnknownMapPoint.MonsterOdds = 0;
        state.Run.Odds.UnknownMapPoint.EliteOdds = -1;
        state.Run.Odds.UnknownMapPoint.TreasureOdds = 0;
        state.Run.Odds.UnknownMapPoint.ShopOdds = 0;
        var input = planning.Capture(state);
        var eventPoint = new MapPoint(0, 1) { PointType = MapPointType.Unknown };
        var shopPoint = new MapPoint(0, 2) { PointType = MapPointType.Shop };
        eventPoint.AddChildPoint(shopPoint);
        var route = Route([eventPoint, shopPoint], [RoomType.Event, RoomType.Shop]);
        var baseline = planning.GenerateMerchant(planning.Restore(input));
        var service = new MerchantChoiceBranchService(512, 48, 30000);
        var result = service.Predict(state.Player, route, baseline);
        Require(result.ChoiceBranches.Select(branch => branch.Condition).Prepend(result.ReferenceCondition ?? "")
            .Any(condition => condition.Contains(ModelDb.Event<AromaOfChaos>().Title.GetFormattedText())),
            "event choice state never reached the shop");
        var expected = planning.Restore(input);
        using (ShadowIsolation.Enter(expected.Player, automateRewards: true))
        {
            expected.Run.ActFloor = 2;
            Require(MerchantChoiceBranchService.ResolveRoom(expected, eventPoint) == RoomType.Event, "fixture event roll");
            var model = new EventQueueSimulation(expected.Run, expected.Run.IterateHookListeners(null).ToArray()).Consume()!;
            expected.Run.AppendToMapPointHistory(eventPoint.PointType, RoomType.Event, model.Id);
            var eventInput = planning.Capture(expected);
            var eventService = new PlanningEventPredictionService();
            var option = eventService.EnumerateEventOptions(expected.Player, model, eventInput)[0];
            var selection = option.CardSelections[0];
            var card = selection.Candidates[0];
            var outcome = eventService.ExecuteEventOptionAsync(expected.Player, model, option.Index,
                [new EventCardPick(selection.SelectionStep, card.CardId, card.DeckSlot)], eventInput, option,
                takeRewards: true).GetAwaiter().GetResult();
            Require(outcome.Ok && outcome.Finished, "native event fixture did not finish");
            planning.ApplyEventState(expected, outcome.ShadowRun!, outcome.ShadowPlayer!.NetId);
            expected.Run.AddVisitedMapCoord(eventPoint.coord);
            expected.Run.ActFloor = 3;
            var nativeShop = planning.GenerateMerchant(expected);
            Require(result.ChoiceBranches.Select(branch => branch.Inventory).Prepend(result with
                    { ReferenceCondition = null, ChoiceBranches = [], UnresolvedChoices = [], ChoicesTruncated = false })
                    .Any(inventory => ForecastContentKey.Merchant(inventory)
                    == ForecastContentKey.Merchant(nativeShop)), "event downstream stock/prices differ from native selected option");
        }
        Require(PlanningPredictionService.StateToken(state) == PlanningPredictionService.StateToken(input), "event branch mutated input");
        var limited = new MerchantChoiceBranchService(1, 1, 30000).Predict(state.Player, route, baseline);
        var lines = new List<string>();
        MapForecastTooltipBuilder.AppendMerchantContents(lines, limited, true);
        Require(!lines.Any(line => line.Contains("未覆盖") || line.Contains("尚未穷尽") || line.Contains("改为")),
            "empty branch exploration must not emit warning-only hints");
    }

    private static void ValidateCombatShop(PlanningPredictionService planning, RunState live, Player player)
    {
        var state = planning.CreateState(live, player);
        planning.AddRelic(state, ModelDb.Relic<Driftwood>().Id);
        var input = planning.Capture(state);
        var combat = new MapPoint(0, 1) { PointType = MapPointType.Monster };
        var shop = new MapPoint(0, 2) { PointType = MapPointType.Shop };
        combat.AddChildPoint(shop);
        var result = new MerchantChoiceBranchService(512, 32, 30000).Predict(state.Player,
            Route([combat, shop], [RoomType.Monster, RoomType.Shop]), planning.GenerateMerchant(planning.Restore(input)));
        Require(result.ChoiceBranches.Count > 0, "combat choices produced no merchant state");
        Require(result.ChoiceBranches.Any(branch => branch.Condition.Contains("重掷", StringComparison.OrdinalIgnoreCase)
                || branch.Condition.Contains("Reroll", StringComparison.OrdinalIgnoreCase)),
            "reroll state never reached downstream merchant branches");
        var expected = planning.Restore(input);
        expected.Run.ActFloor = 2;
        var encounter = MerchantChoiceBranchService.ResolveEncounter(expected, RoomType.Monster);
        using (var generated = planning.GenerateCombatRewards(expected, RoomType.Monster, encounter))
        {
            var choice = new RoutePlanChoice.Combat([], null, true).WithCardRewardChoice(
                new RoutePlanChoice.CardReward(0, [new(AlternativeId: "REROLL"), new(AlternativeId: "Skip")]));
            RoutePlanForecastService.ApplyCombatRewards(expected, generated, choice);
        }
        expected.Run.Act._rooms.normalEncountersVisited++;
        expected.Run.AddVisitedMapCoord(combat.coord);
        expected.Run.ActFloor = 3;
        var expectedShop = planning.GenerateMerchant(expected);
        Require(result.ChoiceBranches.Any(branch => ForecastContentKey.Merchant(branch.Inventory)
                == ForecastContentKey.Merchant(expectedShop)), "reroll downstream stock/prices mismatch");
    }

    private static RouteWorldline Route(IReadOnlyList<MapPoint> points, IReadOnlyList<RoomType> types) => new(
        new RoutePath(points.Select(point => new RouteStep(point, false, false)).ToArray()), [],
        types[^1], null, null, 1, types, true);

    private static IEnumerable<int[]> RerollOrders(int count, int[]? prefix = null)
    {
        prefix ??= [];
        yield return prefix;
        for (var group = 0; group < count; group++)
            if (!prefix.Contains(group))
                foreach (var result in RerollOrders(count, [.. prefix, group]))
                    yield return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Choice branch regression: " + message);
    }
}
#endif
