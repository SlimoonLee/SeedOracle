#if DEBUG
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SeedOracle.Api;
using SeedOracle.Data;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class PredictionAccuracyRegressionTests
{
    internal static void Run(RunState live)
    {
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var before = LiveFingerprint.Capture(live);
        var profileBefore = SaveManager.ToJson(SaveManager.Instance.Progress.ToSerializable());
        var planning = new PlanningPredictionService();
        var scenarios = new RelicModel[][]
        {
            [],
            [ModelDb.Relic<MoltenEgg>(), ModelDb.Relic<ToxicEgg>(), ModelDb.Relic<FrozenEgg>()],
            [ModelDb.Relic<MembershipCard>()],
            [ModelDb.Relic<MembershipCard>(), ModelDb.Relic<TheCourier>()]
        };
        var inventories = 0;
        for (var scenario = 0; scenario < scenarios.Length; scenario++)
        {
            var input = planning.CreateState(live, player);
            foreach (var relic in scenarios[scenario])
                planning.AddRelic(input, relic.Id);
            input.Player.PlayerRng.Shops.LoadFromSerializable(new Rng((ulong)(101 + scenario * 137)).ToSerializable());
            input.Player.PlayerRng.Rewards.LoadFromSerializable(new Rng((ulong)(211 + scenario * 193)).ToSerializable());
            var snapshot = planning.Capture(input);
            var predicted = planning.Restore(snapshot);
            var native = planning.Restore(snapshot);
            for (var visit = 1; visit <= 3; visit++)
            {
                var actual = planning.GenerateMerchant(predicted);
                MerchantInventoryForecast expected;
                using (ShadowIsolation.Enter(native.Player))
                using (ShadowIsolation.GenerateMerchantInventory())
                {
                    Require(!LocalContext.IsMe(native.Player), "local UI ownership must stay closed outside pricing");
                    native.Run.PushRoom(new MerchantRoom());
                    try
                    {
                        var inventory = MerchantInventory.CreateForNormalMerchant(native.Player);
                        using var pricing = ShadowIsolation.EvaluateLocalMerchantPrice();
                        Require(LocalContext.IsMe(native.Player), "local price ownership must be restored");
                        expected = new MerchantInventoryForecast(
                            inventory.CharacterCardEntries.Select(Card).ToArray(),
                            inventory.ColorlessCardEntries.Select(Card).ToArray(),
                            inventory.RelicEntries.Select(e => new MerchantItemForecast(ForecastItemDetails.Relic(e.Model!), e.Cost)).ToArray(),
                            inventory.PotionEntries.Select(e => new MerchantItemForecast(ForecastItemDetails.Potion(e.Model!), e.Cost)).ToArray(),
                            inventory.CardRemovalEntry!.Cost, visit);
                    }
                    finally { native.Run.PopCurrentRoom(); }
                    Require(!LocalContext.IsMe(native.Player), "price ownership scope must unwind");
                }
                native.MerchantVisits = visit;
                Require(ForecastContentKey.Merchant(actual) == ForecastContentKey.Merchant(expected),
                    $"native inventory mismatch scenario={scenario} visit={visit}");
                Require(PlanningPredictionService.StateToken(predicted) == PlanningPredictionService.StateToken(native),
                    $"native RNG/bag/state mismatch scenario={scenario} visit={visit}");
                Require(actual.CharacterCards.Count(c => c.IsOnSale) == 1, "merchant must have exactly one sale");
                Require(actual.CharacterCards.Select(c => c.Id).Distinct().Count() == 5, "merchant card blacklist must exclude duplicates");
                if (scenario == 1)
                    Require(actual.CharacterCards.Concat(actual.ColorlessCards).All(c => c.Item.IsUpgraded), "egg merchant hooks must upgrade stock");
                if (scenario >= 2)
                {
                    var noDiscount = planning.Restore(snapshot);
                    foreach (var relic in scenarios[scenario])
                    {
                        var owned = noDiscount.Player.Relics.First(r => r.Id == relic.Id);
                        noDiscount.Player.RemoveRelicInternal(owned, silent: true);
                    }
                    var baseStock = planning.GenerateMerchant(noDiscount);
                    var scale = scenario == 2 ? 0.5m : 0.4m;
                    if (visit == 1)
                    {
                        Require(actual.CharacterCards.Select(c => c.Cost).SequenceEqual(baseStock.CharacterCards.Select(c => (int)(c.Cost * scale))),
                            "membership/courier discount must match the native price hook");
                        Require(actual.CardRemovalCost == (int)(baseStock.CardRemovalCost * scale), "removal discount must not be lost or doubled");
                    }
                }
                inventories++;
            }
        }

        ValidateEventFloors(live, planning);
        ValidatePresentation(live);
        ValidateTransformTargets(live);
        ValidateTransformGrouping();
        var combat = live.Act._rooms.normalEncounters.First();
        var rewards = Entry.RandomForeseer.PredictCombatRewards(player, [RoomType.Monster], combat);
        Require(rewards is { HasValue: true, Accuracy: ForecastAccuracy.BranchDependent }, "base rewards must carry combat conditions");
        Require(before.DescribeDifference(LiveFingerprint.Capture(live)) is null, "accuracy regression changed the live run");
        Require(profileBefore == SaveManager.ToJson(SaveManager.Instance.Progress.ToSerializable()), "merchant prediction changed profile discovery");
        Smoke.SmokeReport.Add($"accuracy-regression PASS: native_merchants={inventories} event_floor_cases=2 grouping=passed truncation=passed event_targets=passed transform_summary=15_to_3 rewards=conditional live_and_profile=unchanged");
    }

    private static MerchantItemForecast Card(MerchantCardEntry entry) => new(
        ForecastItemDetails.Card(entry.CreationResult!.Card), entry.Cost, entry.IsOnSale);

    private static void ValidateEventFloors(RunState live, PlanningPredictionService planning)
    {
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        foreach (var (floor, gate) in new (int, EventModel)[]
                 { (5, ModelDb.Event<PunchOff>()), (6, ModelDb.Event<SlipperyBridge>()) })
        {
            var state = planning.CreateState(live, player);
            while (state.Run.TotalFloor < floor)
                state.Run.AppendToMapPointHistory(MapPointType.RestSite, RoomType.RestSite, null);
            Require(state.Run.TotalFloor == floor, "event floor fixture must start at the threshold boundary");
            var rooms = state.Run.Act._rooms;
            rooms.events.Clear();
            rooms.events.Add(gate);
            rooms.events.Add(ModelDb.Event<DoorsOfLightAndDark>());
            rooms.eventsVisited = 0;
            foreach (var type in state.Run.Odds.UnknownMapPoint._nonEventOdds.Keys.ToArray())
                state.Run.Odds.UnknownMapPoint._nonEventOdds[type] = -1;
            var rest = new MapPoint(0, floor) { PointType = MapPointType.RestSite };
            var target = new MapPoint(0, floor + 1) { PointType = MapPointType.Unknown };
            var native = planning.Restore(planning.Capture(state));
            native.Run.AppendToMapPointHistory(MapPointType.RestSite, RoomType.RestSite, null);
            native.Run.ActFloor = target.coord.row + 1;
            var expected = native.Run.Act.PullNextEvent(native.Run);
            Require(expected.Id == gate.Id, "native event fixture must admit the gated event");
            var result = RouteWorldlinePredictor.Predict(state.Run, target,
                [new RoutePath([new RouteStep(rest, false, false), new RouteStep(target, false, false)])]).Single();
            Require(result.TargetEvent?.Id == expected.Id, $"event floor mismatch for {gate.Id}");
        }
    }

    private static void ValidatePresentation(RunState live)
    {
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var item = ForecastItemDetails.Card(player.Deck.Cards[0]);
        var shop = new MerchantInventoryForecast([new MerchantItemForecast(item, 100)], [], [], [], 75, 1);
        var point = new MapPoint(0, 2) { PointType = MapPointType.Shop };
        var a = new RouteVariantForecast([], RoomType.Shop, null, null,
            Forecast<MerchantInventoryForecast>.Branch(shop, PredictionDependency.Shops, "fixture"), true);
        var b = a with { Merchant = Forecast<MerchantInventoryForecast>.Branch(
            shop with { CharacterCards = [new MerchantItemForecast(item, 101)] }, PredictionDependency.Shops, "fixture") };
        var forecast = new MapNodeForecast(point, 2, 2, null, null, null, null, a.Merchant, [a, b], false);
        Require(RouteMarkerPlanner.Build(forecast).Groups.Count == 2, "different stock at the same visit ordinal must get different route groups");
        b = a with { Merchant = Forecast<MerchantInventoryForecast>.Branch(
            shop with { CharacterCards = [new MerchantItemForecast(item with { IsUpgraded = !item.IsUpgraded }, 100)] }, PredictionDependency.Shops, "fixture") };
        Require(RouteMarkerPlanner.Build(forecast with { RouteVariants = [a, b] }).Groups.Count == 2, "upgrade state must be part of the route key");
        b = a with { Merchant = Forecast<MerchantInventoryForecast>.Branch(
            shop with { CharacterCards = [new MerchantItemForecast(item with { StateKey = "different-enchantment" }, 100)] }, PredictionDependency.Shops, "fixture") };
        Require(RouteMarkerPlanner.Build(forecast with { RouteVariants = [a, b] }).Groups.Count == 2, "enchantment state must be part of the route key");
        var tooltip = MapForecastTooltipBuilder.Build(forecast with { RouteVariants = [a], RoutesTruncated = true })!;
        var text = string.Join("\n", tooltip.Descriptions);
        Require(text.Contains("截断") || text.Contains("truncated"), "single-result tooltip must show truncation");
        Require(!text.Contains("选择后请重算") && !text.Contains("recalculate after choosing"),
            "tooltip must not replace uncomputed branches with boilerplate");
        Require(MapForecastService.ApplyConditions(Forecast<int>.Exact(1), true)!.Accuracy == ForecastAccuracy.BranchDependent,
            "truncated enumeration must not retain Exact accuracy");
        Require(MapForecastTooltipBuilder.Build(forecast with
            { Merchant = null, RouteVariants = [], RoutesTruncated = true }) is not null,
            "truncation must remain visible even when no route result was generated");
    }

    private static void ValidateTransformTargets(RunState live)
    {
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var forecast = Entry.RandomForeseer.PredictEventContents(player, [RoomType.Event], ModelDb.Event<AromaOfChaos>());
        Require(forecast is { HasValue: true, Accuracy: ForecastAccuracy.BranchDependent }, "transform candidates must be conditional");
        var sets = forecast!.Value!.Options.SelectMany(o => o.Sets).Where(s => s.SourceCard is not null).ToArray();
        var eligible = player.Deck.Cards.Count(c => c.Type != MegaCrit.Sts2.Core.Entities.Cards.CardType.Quest && c.IsTransformable);
        Require(sets.Length == eligible && sets.All(s => s.SourceDeckIndex >= 0 && s.Kind == EventPredictionKind.ChoiceCandidates),
            "transform preview must retain every source slot, including duplicate source IDs");
        var lines = new List<string>();
        MapForecastTooltipBuilder.AppendEventContents(lines, forecast, true, "");
        Require(lines.Any(line => line.Contains("张牌变化为"))
                && !lines.Any(line => line.Contains("若选牌组第") || line.Contains("未推演完整事件终局")),
            "event tooltip must display concrete target mapping without boilerplate");
    }

    private static void ValidateTransformGrouping()
    {
        var strike = new ForecastItemDetails(null, "打击", ForecastItemKind.Card, ImagePath: "fixture.png");
        var defend = strike with { Name = "防御" };
        var thirdSource = strike with { Name = "第三种来源" };
        var dull = strike with { Name = "暗淡蓝点" };
        var clumsy = strike with { Name = "笨拙" };
        var result = strike with { Name = "XX" };
        var sets = Enumerable.Range(0, 15).Select(index => new EventPredictionSetDetails(
            [result with { Name = index < 12 ? "XX" : index < 14 ? "YY" : "ZZ" }],
            EventPredictionKind.ChoiceCandidates,
            index < 5 ? strike : index < 10 ? defend : index < 12 ? thirdSource : index < 14 ? dull : clumsy,
            index)).ToArray();
        var option = new EventOptionPredictionDetails("变牌", "fixture", [], sets);
        var useCardArt = SeedOracleData.Settings.UseCardArtThumbnails;
        try
        {
            foreach (var thumbnails in new[] { false, true })
            {
                SeedOracleData.Settings.UseCardArtThumbnails = thumbnails;
                var lines = Render([option]);
                Require(lines.Skip(2).SequenceEqual(new[]
                {
                    "    打击、防御等12张牌变化为XX",
                    "    暗淡蓝点等2张牌变化为YY",
                    "    笨拙等1张牌变化为ZZ"
                }), "transform summary must compress 15 slots to 3 text rows with exact counts and at most two source names");
                Require(option.Sets.Count == 15, "display grouping must retain all prediction slots");
                lines = Render([option], chinese: false);
                Require(lines.Count == 5 && lines[2].Contains("(12 cards)") && lines[4].Contains("(1 card)")
                        && !lines.Any(line => line.Contains("[img=")), "English transform summary must stay compact in either thumbnail mode");
            }
        }
        finally { SeedOracleData.Settings.UseCardArtThumbnails = useCardArt; }

        var separate = option with { Sets =
        [
            new([result], EventPredictionKind.ChoiceCandidates, strike, 0),
            new([result with { IsUpgraded = true }], EventPredictionKind.ChoiceCandidates, defend, 1),
            new([result with { StateKey = "enchantment" }], EventPredictionKind.ChoiceCandidates, dull, 2),
            new([result], EventPredictionKind.ChoiceCandidates, strike, 0, Position: 1),
            new([result, dull], EventPredictionKind.ChoiceCandidates, clumsy, 3),
            new([result], EventPredictionKind.SequencePreview),
            new([dull], EventPredictionKind.CardRewardCandidates)
        ] };
        var separateLines = Render([separate, option with { Sets = [sets[0]] }]);
        Require(separateLines.Count(line => line.Contains("张牌变化为")) == 6
                && separateLines.Any(line => line.Contains("第 2 次："))
                && separateLines.Any(line => line.Contains("若继续重抽第 1 次"))
                && separateLines.Any(line => line.Contains("待选卡包候选")),
            "transform grouping must separate options, positions, upgrades, enchantments and full result lists, retaining other preview kinds");

        static List<string> Render(IReadOnlyList<EventOptionPredictionDetails> options, bool chinese = true)
        {
            var lines = new List<string>();
            MapForecastTooltipBuilder.AppendEventContents(lines,
                Forecast<EventContentDetails>.Branch(new(options), PredictionDependency.EventState, "fixture"), chinese, "");
            return lines;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Accuracy regression: " + message);
    }
}
#endif
