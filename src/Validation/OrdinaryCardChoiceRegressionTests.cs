#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class OrdinaryCardChoiceRegressionTests
{
    internal static void Run(PlanningPredictionService planning, RunState live, Player player)
    {
        var timer = Stopwatch.StartNew();
        PlanningPredictionService.StateSnapshot? input = null;
        var fixture = -1;
        for (var seed = 0; seed < 32 && input is null; seed++)
        {
            var state = planning.CreateState(live, player);
            state.Player.InitializeSeed("ORDINARY_PICK_" + seed);
            ForceEvent(state, ModelDb.Event<BrainLeech>());
            var snapshot = planning.Capture(state);
            var ordinary = true;
            for (var floor = 2; floor <= 3; floor++)
            {
                state.Run.ActFloor = floor;
                using var rewards = planning.GenerateCombatRewards(state, RoomType.Monster,
                    MerchantChoiceBranchService.ResolveEncounter(state, RoomType.Monster));
                ordinary &= OrdinaryCardChoiceEquivalence.CanForgetPick(state, rewards)
                    && rewards.CardRewardGroups[0].Cards.Count == 3;
                TakeReward(state, rewards, -1);
                AdvanceCombat(state, floor);
            }
            if (ordinary) { input = snapshot; fixture = seed; }
        }
        Require(input is not null, "could not create an ordinary two-combat fixture");
        var expected = new Dictionary<int, string>();
        var checkedCases = 0;
        for (var first = -1; first < 3; first++)
        for (var second = -1; second < 3; second++)
        for (var option = 0; option < 2; option++)
        for (var target = 0; target < (option == 0 ? 5 : 3); target++)
        {
            var state = planning.Restore(input!);
            using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
            for (var floor = 2; floor <= 3; floor++)
            {
                state.Run.ActFloor = floor;
                var beforeGeneration = state.Player.PlayerRng.Rewards.ToSerializable().counter;
                using var rewards = planning.GenerateCombatRewards(state, RoomType.Monster,
                    MerchantChoiceBranchService.ResolveEncounter(state, RoomType.Monster));
                Require(state.Player.PlayerRng.Rewards.ToSerializable().counter > beforeGeneration,
                    "native reward generation did not consume Rewards RNG");
                TakeReward(state, rewards, floor == 2 ? first : second);
                AdvanceCombat(state, floor);
            }
            var eventPoint = new MapPoint(0, 3) { PointType = MapPointType.Unknown };
            state.Run.ActFloor = 4;
            Require(MerchantChoiceBranchService.ResolveRoom(state, eventPoint) == RoomType.Event, "event room roll");
            var model = new EventQueueSimulation(state.Run, state.Run.IterateHookListeners(null).ToArray()).Consume()!;
            state.Run.AppendToMapPointHistory(MapPointType.Unknown, RoomType.Event, model.Id);
            var native = model.ToMutable();
            native.BeginEvent(state.Player, new EventCombatSynchronizer(state.Run, state.Run), false).GetAwaiter().GetResult();
            using (ShadowIsolation.UseSelector(new IndexSelector(target)))
                native.CurrentOptions[option].Chosen().GetAwaiter().GetResult();
            Require(native.IsFinished, "native Brain Leech option did not complete");
            state.Run.AddVisitedMapCoord(eventPoint.coord);
            state.Run.ActFloor = 5;
            var stock = ForecastContentKey.MerchantStock(planning.GenerateMerchant(state));
            if (!expected.TryAdd(option, stock))
                Require(expected[option] == stock,
                    $"ordinary selection changed Brain Leech/shop: {first}/{second}/{option}/{target}");
            checkedCases++;
        }
        Require(expected[0] != expected[1], "Brain Leech options must retain their real stock difference");

        var start = planning.Restore(input!);
        var baseline = planning.GenerateMerchant(planning.Restore(input!));
        // Only the reference path is evaluated. Removal must follow the native
        // dependency proof, not pretend that the enumeration budget covered 4x4.
        var predicted = new MerchantChoiceBranchService(24, 1, 30000).Predict(start.Player, Route(), baseline);
        Require(predicted.ChoicesTruncated && predicted.ReferenceChoicePaths.Count == 1
            && predicted.ReferenceChoicePaths.All(path => path.Facts.Count(fact => fact.Kind == ForecastChoiceKind.CardReward
                && fact.EquivalentValues.Count == fact.Domain.Count) == 2),
            "reference-only Brain Leech/shop must preserve the proven full menu as any-choice metadata");
        var lines = new List<string>();
        MapForecastTooltipBuilder.AppendMerchantContents(lines, predicted, true);
        Require(!string.Join("\n", lines).Contains("跳过选牌")
            && lines.Any(line => line.Contains("2、3层选牌：任意选择")), "proven freedom did not reach the tooltip");
        Require(ForecastContentKey.MerchantStock(predicted) == expected[0], "bounded reference stock differs from native");

        var transform = planning.Restore(input!);
        ForceEvent(transform, ModelDb.Event<AromaOfChaos>());
        var transformed = new MerchantChoiceBranchService(24, 1, 30000).Predict(transform.Player, Route(), baseline);
        Require(transformed.ReferenceChoicePaths.Any(path => path.Facts.Count(fact => fact.Kind == ForecastChoiceKind.CardReward) == 2),
            "an intervening deck-selection event must invalidate the ordinary-pick proof");

        var reroll = planning.Restore(input!);
        planning.AddRelic(reroll, ModelDb.Relic<Driftwood>().Id);
        using (var rewards = planning.GenerateCombatRewards(reroll, RoomType.Monster,
                   MerchantChoiceBranchService.ResolveEncounter(reroll, RoomType.Monster)))
            Require(OrdinaryCardChoiceEquivalence.CanForgetPick(reroll, rewards), "ordinary picks should remain free when reroll is offered");
        var rerollPrediction = new MerchantChoiceBranchService(24, 1, 30000).Predict(reroll.Player, Route(), baseline);
        var rerollLines = new List<string>();
        MapForecastTooltipBuilder.AppendMerchantContents(rerollLines, rerollPrediction, true);
        Require(string.Join("\n", rerollLines).Contains("任意选择（不重掷）")
            && rerollPrediction.ReferenceChoicePaths.SelectMany(path => path.Facts)
                .Where(fact => fact.Kind == ForecastChoiceKind.CardReward).All(fact =>
                    fact.EquivalentValues.Count > 0 && !fact.EquivalentValues.Contains("alt:reroll")),
            "reroll must remain a distinct action, outside ordinary any-choice coverage");
        var wheel = planning.Restore(input!);
        planning.AddRelic(wheel, ModelDb.Relic<PrayerWheel>().Id);
        var wheelPrediction = new MerchantChoiceBranchService(24, 1, 30000).Predict(wheel.Player, Route(), baseline);
        var wheelFacts = wheelPrediction.ReferenceChoicePaths.SelectMany(path => path.Facts)
            .Where(fact => fact.Kind == ForecastChoiceKind.CardReward).ToArray();
        Require(wheelFacts.Length == 4 && wheelFacts.All(fact => fact.EquivalentValues.Count == fact.Domain.Count),
            "ordinary choices in both Prayer Wheel groups must be free");
        var hunt = planning.Restore(input!);
        planning.AddCard(hunt, ModelDb.Card<TheHunt>().Id, false);
        using (var rewards = planning.GenerateCombatRewards(hunt, RoomType.Monster,
                   MerchantChoiceBranchService.ResolveEncounter(hunt, RoomType.Monster)))
            Require(!OrdinaryCardChoiceEquivalence.CanForgetPick(hunt, rewards), "Hunt dependency was erased");

        var fish = planning.Restore(input!);
        planning.AddRelic(fish, ModelDb.Relic<LuckyFysh>().Id);
        var fishInput = planning.Capture(fish);
        var pickupGold = new List<int>();
        foreach (var pick in new[] { -1, 0 })
        {
            var state = planning.Restore(fishInput);
            state.Run.ActFloor = 2;
            using var rewards = planning.GenerateCombatRewards(state, RoomType.Monster,
                MerchantChoiceBranchService.ResolveEncounter(state, RoomType.Monster));
            Require(!OrdinaryCardChoiceEquivalence.CanForgetPick(state, rewards), "pickup hook was erased");
            TakeReward(state, rewards, pick);
            pickupGold.Add(state.Player.Gold);
        }
        Require(pickupGold[1] - pickupGold[0] == 15,
            "native Lucky Fysh counterexample must retain its card-pick gold effect despite equal RNG");
        Smoke.SmokeReport.Add($"ordinary-card-choice PASS: fixture=ORDINARY_PICK_{fixture} native_cases={checkedCases} "
            + $"generation_rng=advanced pick_skip_all_rng=equal shop_per_event_option=equal "
            + $"reference_only=any_choice transform=retained reroll=separate wheel_groups=any_choice hunt=retained "
            + $"pickup_hook_gold_delta=15 elapsed_ms={timer.ElapsedMilliseconds}");
        Smoke.SmokeReport.Add("ordinary-card-choice TOOLTIP " + string.Join(" | ", lines));
    }

    private static void TakeReward(PlanningPredictionService.State state, CombatRewardDetails rewards, int pick)
    {
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        foreach (var reward in rewards.NativeRewards.Where(reward => reward is not CardReward))
            if (!reward.SelectUnsynchronized().GetAwaiter().GetResult()) reward.OnSkipped();
        var before = RngState(state);
        var rewardCards = rewards.NativeCardRewards.Single();
        if (pick < 0) rewardCards.OnSkipped();
        else
        {
            // Direct native add/taken callbacks, independently of the planner's
            // reward selector/session implementation.
            var candidates = rewardCards.Cards.ToArray();
            var result = CardPileCmd.Add(candidates[pick], PileType.Deck).GetAwaiter().GetResult();
            Require(result.success, "native card pickup failed");
            Hook.AfterRewardTaken(state.Run, state.Player, rewardCards).GetAwaiter().GetResult();
            foreach (var card in candidates)
                state.Run.CurrentMapPointHistoryEntry!.GetEntry(state.Player.NetId).CardChoices
                    .Add(new CardChoiceHistoryEntry(card, ReferenceEquals(card, candidates[pick])));
        }
        Require(RngState(state) == before, "ordinary pick/skip advanced a player or run RNG stream");
    }

    private static string RngState(PlanningPredictionService.State state) => JsonSerializer.Serialize(new
        { Player = state.Player.PlayerRng.ToSerializable(), Run = state.Run.Rng.ToSerializable() });

    private static void AdvanceCombat(PlanningPredictionService.State state, int floor)
    {
        state.Run.Act._rooms.normalEncountersVisited++;
        state.CombatsAdvanced++;
        state.Run.AddVisitedMapCoord(new MapPoint(0, floor - 1).coord);
    }

    private static void ForceEvent(PlanningPredictionService.State state, EventModel model)
    {
        state.Run.Act._rooms.events.Clear();
        state.Run.Act._rooms.events.Add(model);
        state.Run.Act._rooms.eventsVisited = 0;
        state.Run.Odds.UnknownMapPoint.MonsterOdds = 0;
        state.Run.Odds.UnknownMapPoint.EliteOdds = -1;
        state.Run.Odds.UnknownMapPoint.TreasureOdds = 0;
        state.Run.Odds.UnknownMapPoint.ShopOdds = 0;
    }

    private static RouteWorldline Route()
    {
        MapPoint[] points = [new(0, 1) { PointType = MapPointType.Monster }, new(0, 2) { PointType = MapPointType.Monster },
            new(0, 3) { PointType = MapPointType.Unknown }, new(0, 4) { PointType = MapPointType.Shop }];
        for (var index = 0; index < points.Length - 1; index++) points[index].AddChildPoint(points[index + 1]);
        return new(new RoutePath(points.Select(point => new RouteStep(point, false, false)).ToArray()), [],
            RoomType.Shop, null, null, 1, [RoomType.Monster, RoomType.Monster, RoomType.Event, RoomType.Shop], true);
    }

    private sealed class IndexSelector(int index) : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) =>
            Task.FromResult(options.Skip(index).Take(maxSelect));
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives) => new() { card = options[index].Card };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Ordinary card choice regression: " + message);
    }
}
#endif
