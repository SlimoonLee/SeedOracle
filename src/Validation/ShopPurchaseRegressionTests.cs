#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class ShopPurchaseRegressionTests
{
    internal static void Run(PlanningPredictionService planning, RunState live, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        MerchantMechanicsRegressionTests.Run(planning, live, player);
        var timer = Stopwatch.StartNew();
        var state = planning.CreateState(live, player);
        foreach (var id in new[] { "BURNING_BLOOD", "ARCANE_SCROLL", "LANTERN", "JUZU_BRACELET", "VAJRA", "RAINBOW_RING" })
            planning.AddRelic(state, ModelDb.AllRelics.First(relic => relic.Id.Entry == id).Id);
        while (state.Player.HasOpenPotionSlots)
            Require(planning.AddPotion(state, ModelDb.AllPotions.First(potion => potion.Id.Entry == "POWER_POTION").Id, null),
                "could not fill the fixture potion belt");
        state.Player.Gold = 137;
        var stock = ScreenshotStock();
        var scope = ShopPurchaseChoices.Describe(state, stock, 14, true);
        Require(scope.CardCombinationsEquivalent && scope.AvailableCards.Count() == 6, "six affordable cards were not certified");
        var splash = scope.Offers.Single(offer => offer.Item.Id.Entry == "SPLASH");
        Require(splash.Item.Cost == 166 && splash.Availability == ShopOfferAvailability.Unaffordable,
            "166g Splash at 137g must be recorded as unaffordable");
        var fixtureReward = new CombatRewardDetails(14,
            [new([new([TextCard("突破"), TextCard("余烬"), TextCard("怨恨")], [])])],
            [new(ForecastItemDetails.Text("马萨雷斯的赠礼"))], [])
        {
            ReferenceChoicePaths = [new("raw no purchase", [ShopPurchaseChoices.Fact(scope, "skip", "不购买", true)])]
        };
        var tooltip = Tooltip(fixtureReward);
        var text = string.Join("\n", tooltip.Descriptions);
        Require(text.Contains("14层购牌：任意选择（137金内）") && text.Contains("飞溅") && text.Contains("166金")
            && text.Contains("买不起") && !text.Contains("不购买/购买") && !text.Contains("前提：14层"),
            "full screenshot-equivalent tooltip must replace purchase enumeration and explain omitted Splash");
        Require(text.Length < 360, "shop explanation remains too verbose");
        var missing = ShopPurchaseChoices.CompleteCoverage(new[] { new RewardChoiceBranch<CombatRewardDetails>("", fixtureReward)
            { ChoicePaths = fixtureReward.ReferenceChoicePaths } });
        Require(missing[0].ChoicePaths[0].Facts[0].Shop!.UncalculatedValues.Count == 0,
            "proved card combinations or unaffordable offers were incorrectly marked unevaluated");

        var input = planning.Capture(state);
        var tested = 0;
        var combinations = 0;
        foreach (var gold in new[] { 137, 166 })
        {
            var cards = stock.CharacterCards.Concat(stock.ColorlessCards).Where(card => card.Cost <= gold).ToArray();
            string? expected = null;
            for (var mask = 0; mask < (1 << cards.Length); mask++)
            {
                var selected = cards.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                if (selected.Sum(card => card.Cost) > gold) continue;
                var native = planning.Restore(input);
                native.Player.Gold = gold;
                native.Run.ActFloor = 14;
                native.Run.AppendToMapPointHistory(MapPointType.Shop, RoomType.Shop, null);
                var before = RngState(native);
                using (ShadowIsolation.Enter(native.Player, automateRewards: true))
                {
                    foreach (var card in selected)
                    {
                        var model = native.Run.CreateCard(ModelDb.GetById<CardModel>(card.Id), native.Player);
                        Require(CardPileCmd.Add(model, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck).GetAwaiter().GetResult().success,
                            "native card purchase add failed");
                        PlayerCmd.LoseGold(card.Cost, native.Player, GoldLossType.Spent).GetAwaiter().GetResult();
                    }
                }
                Require(RngState(native) == before, "ordinary card purchases advanced a run or player RNG");
                native.Run.ActFloor = 15;
                using var rewards = planning.GenerateCombatRewards(native, RoomType.Monster,
                    MerchantChoiceBranchService.ResolveEncounter(native, RoomType.Monster));
                var key = ForecastContentKey.Combat(rewards);
                expected ??= key;
                Require(key == expected, "an affordable native card combination changed the next reward");
                tested++;
                if (selected.Length > 1) combinations++;
            }
        }
        Require(combinations > 0, "multi-card native purchases were never exercised");
        state.Player.Gold = 166;
        var affordable = ShopPurchaseChoices.Describe(state, stock, 14, true);
        Require(affordable.AvailableCards.Count() == 7 && affordable.Offers.Single(offer => offer.Item.Id.Entry == "SPLASH")
                .Availability == ShopOfferAvailability.Available,
            "Splash must join the equivalent set when it becomes affordable");

        // Make the native shop offer Prayer Wheel, then verify its real extra
        // reward branch and a deliberately uncomputed, affordable purchase.
        PlanningPredictionService.StateSnapshot? shopInput = null;
        for (var seed = 0; seed < 24 && shopInput is null; seed++)
        {
            var candidate = planning.Restore(input);
            candidate.Player.Gold = 1000;
            foreach (var deque in candidate.Player.RelicGrabBag._deques.Values) deque.Clear();
            candidate.Player.RelicGrabBag._deques[RelicRarity.Rare] = [ModelDb.Relic<PrayerWheel>()];
            candidate.Player.PlayerRng.Rewards.LoadFromSerializable(new Rng((ulong)(700 + seed)).ToSerializable());
            var snapshot = planning.Capture(candidate);
            var inventory = planning.GenerateMerchant(planning.Restore(snapshot));
            if (inventory.Relics.All(item => item.Id.Entry != "PRAYER_WHEEL")) continue;
            shopInput = snapshot;
        }
        Require(shopInput is not null, "native Prayer Wheel shop fixture not found");
        var start = planning.Restore(shopInput!);
        var encounter = MerchantChoiceBranchService.ResolveEncounter(start, RoomType.Monster);
        using var baseline = planning.GenerateCombatRewards(planning.Restore(shopInput!), RoomType.Monster, encounter);
        var route = Route();
        var full = new MerchantChoiceBranchService(512, 48, 30000).PredictRewards(start.Player, route, baseline.Detach());
        Require(full.ChoiceBranches.Any(branch => branch.Outcome.CardRewardGroups.Count == 2),
            "buying Prayer Wheel produced no concrete extra-group branch");
        var fullText = string.Join("\n", Tooltip(full).Descriptions);
        var extraCards = full.ChoiceBranches.First(branch => branch.Outcome.CardRewardGroups.Count == 2)
            .Outcome.CardRewardGroups[1].Cards;
        Require(fullText.Contains(ModelDb.Relic<PrayerWheel>().Title.GetFormattedText())
            && fullText.Contains("第2组选牌") && extraCards.All(card => fullText.Contains(card.Name)),
            "the changing purchase was not paired with concrete extra cards");
        var limited = new MerchantChoiceBranchService(3, 48, 30000).PredictRewards(start.Player, route, baseline.Detach());
        var limitedText = string.Join("\n", Tooltip(limited).Descriptions);
        Require(limitedText.Contains("未计算购买") && limitedText.Contains(ModelDb.Relic<PrayerWheel>().Title.GetFormattedText()),
            "an affordable purchase lost to the budget must be named as unevaluated");
        Require(limited.ReferenceChoicePaths.SelectMany(path => path.Facts).SelectMany(fact => fact.Shop?.Offers ?? [])
            .Where(offer => offer.Item.Id.Entry == "PRAYER_WHEEL").All(offer => offer.Availability == ShopOfferAvailability.Available),
            "budget exhaustion was incorrectly reported as insufficient gold");
        var unknownScope = ShopPurchaseChoices.Describe(state, stock, 14, false);
        Require(!unknownScope.CardCombinationsEquivalent, "an unsupported continuation must not claim arbitrary purchase combinations");
        Smoke.SmokeReport.Add($"shop-purchase-summary PASS: native_card_sets={tested} multi_card_sets={combinations} "
            + $"screenshot_budget=137 splash_price=166 unaffordable=explicit affordable_splash=equivalent "
            + $"wheel_branch=concrete budget_omission=explicit unknown_continuation=restricted chars={text.Length} elapsed_ms={timer.ElapsedMilliseconds}");
        Smoke.SmokeReport.Add("shop-purchase-summary TOOLTIP " + text.Replace('\n', '|'));
        Smoke.SmokeReport.Add("shop-purchase-summary LIMITED " + limitedText.Replace('\n', '|'));
    }

    private static MerchantInventoryForecast ScreenshotStock() => new(
        [Card("POMMEL_STRIKE",49), Card("THUNDERCLAP",52), Card("TREMBLE",49), Card("BLOODLETTING",38,true), Card("STONE_ARMOR",73)],
        [Card("ULTIMATE_DEFEND",85), Card("SPLASH",166)],
        [Relic("SPARKLING_ROUGE",237), Relic("PRAYER_WHEEL",308), Relic("MEMBERSHIP_CARD",192)],
        [Potion("POTION_OF_BINDING",79), Potion("EXPLOSIVE_AMPOULE",50), Potion("POWER_POTION",50)], 100, 1);

    private static MerchantItemForecast Card(string id, int cost, bool sale = false) =>
        new(ForecastItemDetails.Card(ModelDb.AllCards.First(card => card.Id.Entry == id)), cost, sale);
    private static MerchantItemForecast Relic(string id, int cost) =>
        new(ForecastItemDetails.Relic(ModelDb.AllRelics.First(relic => relic.Id.Entry == id)), cost);
    private static MerchantItemForecast Potion(string id, int cost) =>
        new(ForecastItemDetails.Potion(ModelDb.AllPotions.First(potion => potion.Id.Entry == id)), cost);
    private static RewardItemDetails TextCard(string name) => new(new(null, name, ForecastItemKind.Card));
    private static MapForecastTooltip Tooltip(CombatRewardDetails rewards) => MapForecastTooltipBuilder.Build(new(
        new MapPoint(0,14) { PointType = MapPointType.Monster }, 2, 2, null, null, null, null, null,
        [new([], RoomType.Monster, null, null, null, true,
            Forecast<CombatRewardDetails>.Branch(rewards, PredictionDependency.Rewards, "fixture"))], false))!;
    private static RouteWorldline Route() => new(new RoutePath([
        new(new MapPoint(0,13) { PointType = MapPointType.Shop },false,false),
        new(new MapPoint(0,14) { PointType = MapPointType.Monster },false,false)]), [],
        RoomType.Monster, null, null, 1, [RoomType.Shop,RoomType.Monster], true);
    private static string RngState(PlanningPredictionService.State state) => JsonSerializer.Serialize(new
        { Player = state.Player.PlayerRng.ToSerializable(), Run = state.Run.Rng.ToSerializable() });
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Shop purchase regression: " + message);
    }
}
#endif
