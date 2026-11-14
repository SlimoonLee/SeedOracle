#if DEBUG
using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;
using SeedOracle.Forecasting;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class ForecastConditionRegressionTests
{
    internal static MapForecastTooltip? CombatExample { get; private set; }
    internal static MapForecastTooltip? MerchantExample { get; private set; }

    internal static void Run(PlanningPredictionService planning, RunState live, Player player)
    {
        var timer = Stopwatch.StartNew();
        var card = new ForecastItemDetails(null, "库存牌", ForecastItemKind.Card);
        var stock = new MerchantInventoryForecast([new(card, 100)], [], [], [], 75, 1);
        string[] menu = ["alt:skip", "a", "b", "c"];
        string[] names = ["跳过", "双重打击", "狱火", "愤怒"];
        var paths = menu.Select((value, index) => new ForecastChoicePath("2F raw choice " + index,
            [Pick(2, value, names[index], menu)])).ToArray();
        var single = stock with { ReferenceChoicePaths = paths };
        var lines = new List<string>();
        MapForecastTooltipBuilder.AppendMerchantContents(lines, single, true);
        Require(!lines.Any(line => line.StartsWith("对应") || line.StartsWith("条件") || line.Contains("双重打击"))
            && lines.Any(line => line.Contains("2层选牌：任意选择")),
            "full equal-result card menu must explicitly say any choice instead of enumerating treatments");
        var reward = new CombatRewardDetails(8,
            [new CombatCardRewardGroup([new([new(card with { Name = "全身撞击" }),
                new(card with { Name = "挑衅" }), new(card with { Name = "与我一战" })], [])])], [], [])
        { ReferenceCondition = "2F 跳过卡牌和遗物；或 2F 第1组选「跳过」；或 2F 第1组选「双重打击」；或 2F 第1组选「狱火」；或 2F 第1组选「愤怒」",
            ReferenceChoicePaths = paths };
        var rewardForecast = Forecast<CombatRewardDetails>.Branch(reward, PredictionDependency.Rewards, "fixture");
        CombatExample = MapForecastTooltipBuilder.Build(new(new MapPoint(0, 2) { PointType = MapPointType.Monster }, 2, 2,
            null, null, null, null, null, [new([], RoomType.Monster, null, null, null, true, rewardForecast)], false))!;
        var rewardText = string.Join("\n", CombatExample.Descriptions);
        Require(!rewardText.Contains("参照选择") && !rewardText.Contains("双重打击") && !rewardText.Contains("2F")
            && rewardText.Contains("全身撞击") && rewardText.Contains("8"),
            "full combat tooltip must simplify the reference menu while retaining its actual rewards");
        var chest = new TreasureRoomDetails(20, [new(card with { Name = "宝箱遗物" })], false)
            { ReferenceCondition = reward.ReferenceCondition, ReferenceChoicePaths = paths };
        lines.Clear();
        MapForecastTooltipBuilder.AppendRewards(lines, new([], RoomType.Treasure, null, null, null, true,
            Treasure: Forecast<TreasureRoomDetails>.Exact(chest)), true);
        Require(!string.Join("\n", lines).Contains("双重打击") && lines.Any(line => line.Contains("宝箱遗物")),
            "treasure tooltip must use the same complete reference-domain reduction");
        var partialReward = reward with { ReferenceChoicePaths = paths.Take(2).ToArray() };
        lines.Clear();
        MapForecastTooltipBuilder.AppendRewards(lines, new([], RoomType.Monster, null, null, null, true,
            Forecast<CombatRewardDetails>.Exact(partialReward)), true);
        // The reward is the same whichever of the enumerated paths you take, so the
        // path list is not part of the answer. Restricted coverage still shows up as
        // the "条件预测" badge instead of a wall of prerequisites.
        Require(!lines.Any(line => line.Contains("前提") || line.Contains("跳过/双重打击")),
            "a reward identical along every enumerated path must not name those paths");

        var partial = stock with { ReferenceChoicePaths = paths.Take(2).ToArray(), ChoicesTruncated = true };
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantContents(lines, partial, true);
        Require(lines.Any(line => line.Contains("跳过/双重打击")) && !lines.Any(line => line.Contains("任意")),
            "partially evaluated menu must retain the actual subset");
        // A stock with nothing conditional on it is just a list: no "Base stock:"
        // label with an empty condition in front of it.
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantContents(lines, stock, true);
        Require(!lines.Any(line => line.StartsWith("基础库存")) && lines.Any(line => line.StartsWith("角色卡")),
            "a single stock with no conditions must not print an empty base header");

        string[] binary = ["0", "1"];
        var diagonal = new[] { new[] { Fact("a", "0", binary), Fact("b", "0", binary) },
            new[] { Fact("a", "1", binary), Fact("b", "1", binary) } };
        Require(ForecastChoiceConditionFormatter.Reduce(diagonal).Count == 2,
            "correlated alternatives must not become an unrestricted Cartesian product");
        var cartesian = binary.SelectMany(a => binary.Select(b =>
            (IReadOnlyList<ForecastChoiceFact>)new[] { Fact("a", a, binary), Fact("b", b, binary) }));
        var reduced = ForecastChoiceConditionFormatter.Reduce(cartesian);
        Require(reduced.Count == 1 && reduced[0].Cells.Count == 0, "complete equal-result product must collapse");
        var duplicateTargets = ForecastChoiceConditionFormatter.Reduce(new[]
        {
            new[] { new ForecastChoiceFact("target", "选牌", "slot0", "打击", ["slot0", "slot1"]) },
            new[] { new ForecastChoiceFact("target", "选牌", "slot1", "打击", ["slot0", "slot1"]) }
        });
        Require(duplicateTargets.Count == 1 && duplicateTargets[0].Cells.Count == 0,
            "equal names must keep separate source slots until all targets are covered");
        var openDomain = ForecastChoiceConditionFormatter.Reduce(new[]
        {
            new[] { new ForecastChoiceFact("shop", "2层", "skip", "不购买", [], Closed: false) },
            new[] { new ForecastChoiceFact("shop", "2层", "buy", "购买卡牌A", [], Closed: false) }
        });
        Require(openDomain.Count == 1 && openDomain[0].Cells.Count == 1
            && ForecastChoiceConditionFormatter.Format(openDomain, true).Contains("不购买/购买卡牌A"),
            "limited shop/rest actions must combine their labels without claiming all actions");
        var domainA = stock with { ReferenceCondition = "相同表面标签", ReferenceChoicePaths = [paths[0]] };
        var domainB = domainA with { ReferenceChoicePaths = [new("相同表面标签",
            [Pick(2, "alt:skip", "跳过", ["alt:skip"])])] };
        var variants = new[] { new RouteVariantForecast([], RoomType.Shop, null, null, Forecast<MerchantInventoryForecast>.Exact(domainA), false),
            new RouteVariantForecast([], RoomType.Shop, null, null, Forecast<MerchantInventoryForecast>.Exact(domainB), false) };
        var node = new MapNodeForecast(new MapPoint(0, 1) { PointType = MapPointType.Shop }, 1, 1,
            null, null, null, null, null, variants, false);
        Require(RouteMarkerPlanner.Build(node).Groups.Count == 2,
            "route grouping must retain differing actual choice domains behind equal display labels");

        var six = Enumerable.Range(0, 6).Select(index => "slot" + index).ToArray();
        var three = six.Take(3).ToArray();
        var optionMenu = new[] { "share", "tear" };
        ForecastChoiceFact Option(string value) => new("brain:option", "4层脑蛭", value,
            value == "share" ? "分享知识" : "把它扯下来", optionMenu, Kind: ForecastChoiceKind.EventOption);
        ForecastChoiceFact Target(string option, string value, string[] domain) =>
            new("brain:" + option + ":target", "选牌", value, "来源牌" + value, domain, Kind: ForecastChoiceKind.EventCard);
        var routeBlue = new ForecastChoiceFact("route", "", "blue", "青色线路", [], Closed: false, Kind: ForecastChoiceKind.Route);
        var routeGreen = routeBlue with { Value = "green", ValueLabel = "绿色线路" };
        var prefix = new[] { Pick(2, "alt:skip", "跳过", menu) with { EquivalentValues = menu },
            Pick(3, "alt:skip", "跳过", menu) with { EquivalentValues = menu } };
        var baseline = stock with { CharacterCards = [new(card with { Name = "A" }, 52), new(card with { Name = "B" }, 72), new(card with { Name = "C" }, 72)] };
        var changedA = baseline with { CharacterCards = [baseline.CharacterCards[0], new(card with { Name = "D" }, 48), new(card with { Name = "E" }, 48)], CardRemovalCost = 100 };
        var changedB = changedA with { CharacterCards = [new(card with { Name = "F" }, 78), changedA.CharacterCards[1], changedA.CharacterCards[2]], CardRemovalCost = 125 };
        var cases = six.Select(value => new MerchantForecastCase("raw share " + value, baseline, 1)
            { Facts = [.. prefix, routeBlue, Option("share"), Target("share", value, six)] })
            .Concat(three.Select(value => new MerchantForecastCase("raw tear " + value, changedA, 1)
                { Facts = [.. prefix, routeBlue, Option("tear"), Target("tear", value, three)] }))
            .Append(new("raw green", changedB, 1) { Facts = [.. prefix, routeGreen] }).ToArray();
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantSummary(lines, cases, true);
        var text = string.Join("\n", lines);
        Require(lines.Count(line => line.Contains("2、3层选牌：任意选择")) == 1
            && !text.Contains("跳过选牌") && !text.Contains("来源牌"),
            "proven equivalent prior-floor choices must explicitly say any choice once");
        Require(text.Contains("脑蛭：分享知识") && text.Contains("脑蛭：把它扯下来")
            && lines.Count(line => line.Contains("相对基础")) == 2
            && lines.Count(line => line.Contains("②D 48金") && line.Contains("③E 48金")) == 2,
            "each alternative must give one complete compact change list");
        Require(!text.Contains("→") && !text.Contains("，变化：")
            && !text.Contains("青色线路") && text.Contains("绿色线路")
            && lines.Count(line => line.Contains("2、3层选牌：任意选择")) == 1,
            "route order must not label a stock the event option already explains");
        Require(text.Length < 650, "screenshot-equivalent condition chains remain too verbose");
        Require(!text.Contains("种前期选法结果相同"),
            "groups with different stock must keep their distinguishing conditions");
        Smoke.SmokeReport.Add("condition-summary EXAMPLE " + string.Join(" | ", lines));
        // Same shop reached in two different orders (monster→event vs event→monster).
        // The order is not the cause: the event option is. The tooltip must name the
        // option once instead of printing one colour-labelled block per route.
        ForecastChoiceFact OrderOption(int floor, string value, string label) =>
            new($"event:{floor}:BrainLeech:page:option", $"{floor}层脑蛭", value, label, optionMenu,
                Kind: ForecastChoiceKind.EventOption, Floor: floor)
            { CauseKey = "event:BrainLeech:option", CauseLabel = "脑蛭" };
        var orderCases = new[]
        {
            new MerchantForecastCase("raw", baseline, 1) { Facts = [.. prefix, routeBlue, OrderOption(3, "share", "分享知识")] },
            new MerchantForecastCase("raw", changedA, 1) { Facts = [.. prefix, routeBlue, OrderOption(3, "tear", "把它扯下来")] },
            new MerchantForecastCase("raw", baseline, 1) { Facts = [.. prefix, routeGreen, OrderOption(2, "share", "分享知识")] },
            new MerchantForecastCase("raw", changedA, 1) { Facts = [.. prefix, routeGreen, OrderOption(2, "tear", "把它扯下来")] }
        };
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantSummary(lines, orderCases, true);
        var causeText = string.Join("\n", lines);
        Require(causeText.Contains("基础库存：脑蛭：分享知识")
            && causeText.Contains("脑蛭：把它扯下来（相对基础）：")
            && !causeText.Contains("青色线路") && !causeText.Contains("绿色线路")
            && !causeText.Contains("3层脑蛭") && !causeText.Contains("2层脑蛭")
            && lines.Count(line => line.Contains("相对基础")) == 1,
            "the event option, not the route order, must be the only discriminator: " + causeText);
        Smoke.SmokeReport.Add("condition-summary CAUSE " + causeText.Replace('\n', '|'));
        // Same option, same floors, different stocks on disjoint routes: here the
        // route itself is the cause and must stay visible.
        var conflictCases = new[]
        {
            new MerchantForecastCase("raw", baseline, 1) { Facts = [.. prefix, routeBlue, OrderOption(3, "share", "分享知识")] },
            new MerchantForecastCase("raw", changedA, 1) { Facts = [.. prefix, routeGreen, OrderOption(2, "share", "分享知识")] },
            new MerchantForecastCase("raw", changedB, 1) { Facts = [.. prefix, routeBlue, OrderOption(3, "tear", "把它扯下来")] }
        };
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantSummary(lines, conflictCases, true);
        var conflictText = string.Join("\n", lines);
        Require(conflictText.Contains("青色线路") && conflictText.Contains("绿色线路"),
            "a condition that yields different stocks on disjoint routes must keep its route: " + conflictText);
        // One stock reachable through many correlated upstream picks: the
        // product is unreadable and useless, so it collapses to a count.
        var wide = Enumerable.Range(0, 9).Select(index => "w" + index).ToArray();
        var correlated = Enumerable.Range(0, 6).Select(index => new MerchantForecastCase("raw " + index, baseline, 1)
        {
            Facts =
            [
                new ForecastChoiceFact("wide:a", "3层选牌", "w" + index, "牌" + index, wide),
                new ForecastChoiceFact("wide:b", "4层选牌", "w" + index, "牌" + index, wide)
            ]
        }).ToArray();
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantSummary(lines, correlated, true);
        var pooled = string.Join("\n", lines);
        Require(pooled.Contains("基础库存（6 种前期选法结果相同）") && !pooled.Contains("；或")
            && lines.Count(line => line.Contains("角色卡")) == 1,
            "one pooled stock must not enumerate every upstream combination: " + pooled);
        Smoke.SmokeReport.Add("condition-summary POOLED " + pooled.Replace('\n', '|'));
        // The screenshot case: one stock reachable from every route and every
        // upstream pick. Route labels and "prerequisite" lines are the same noise
        // as the enumeration and must disappear with it.
        var pickMenu = Enumerable.Range(0, 8).Select(index => "n" + index).ToArray();
        var wideMenu = Enumerable.Range(0, 8).Select(index => "m" + index).ToArray();
        ForecastChoiceFact Pick2(string value, string label) =>
            new("combat:2", "2层第1组选牌", value, label, pickMenu, Kind: ForecastChoiceKind.CardReward, Floor: 2);
        MerchantForecastCase RoutedCase(ForecastChoiceFact route, ForecastChoiceFact prior, int pick) => new("raw", baseline, 1)
        {
            Facts =
            [
                route,
                prior,
                new ForecastChoiceFact("neow", "涅奥", "m" + pick, "选项" + pick, wideMenu),
                new ForecastChoiceFact("f3", "3层第1组选牌", "m" + pick, "牌" + pick, wideMenu)
            ]
        };
        var routePool = Enumerable.Range(0, 5)
            .SelectMany(index => new[]
            {
                RoutedCase(routeBlue, Pick2("skip", "跳过"), index),
                RoutedCase(routeGreen, Pick2("take", "拿牌"), index)
            }).ToArray();
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantSummary(lines, routePool, true);
        var routePooled = string.Join("\n", lines);
        Require(routePooled.Contains("基础库存（10 种前期选法结果相同）")
            && !routePooled.Contains("青色线路") && !routePooled.Contains("绿色线路")
            && !routePooled.Contains("前提") && !routePooled.Contains("或")
            && !routePooled.Contains("2层第1组选牌"),
            "route labels and prerequisites must collapse with the enumeration: " + routePooled);
        Smoke.SmokeReport.Add("condition-summary ROUTED " + routePooled.Replace('\n', '|'));
        var screenshotStock = baseline with
        {
            CharacterCards = [Store("头槌", 52), Store("劫掠", 72), Store("战鼓", 72), Store("破灭", 24, true), Store("燃烧", 75)],
            ColorlessCards = [Store("花样百出", 90), Store("狼揍", 179)],
            Relics = [Store("摆动球", 201), Store("意外光滑的石头", 176), Store("燃烧木棍", 197)],
            Potions = [Store("攻击药水", 51), Store("鲜血药水", 52), Store("能量药水", 51)], CardRemovalCost = 100
        };
        var screenshotTear = screenshotStock with
        {
            CharacterCards = [screenshotStock.CharacterCards[0], Store("完美打击", 48), Store("破灭", 48), Store("战鼓", 37, true), screenshotStock.CharacterCards[4]],
            Relics = [screenshotStock.Relics[0], Store("坚固钳子", 276), screenshotStock.Relics[2]]
        };
        var screenshotGreen = screenshotTear with
        {
            CharacterCards = [Store("与我一战", 78), screenshotTear.CharacterCards[1], screenshotTear.CharacterCards[2], Store("耸肩无视", 24, true), screenshotTear.CharacterCards[4]],
            Relics = [screenshotStock.Relics[0], Store("闪亮回红", 226), screenshotStock.Relics[2]],
            ReferenceChoicePaths = [new("", [])]
        };
        var screenshotBlue = screenshotStock with
        {
            ReferenceChoicePaths = six.Select(value => new ForecastChoicePath("raw share " + value,
                [.. prefix, Option("share"), Target("share", value, six)])).ToArray(),
            ChoiceBranches = [new("raw tear", screenshotTear)
            { ChoicePaths = three.Select(value => new ForecastChoicePath("raw tear " + value,
                [.. prefix, Option("tear"), Target("tear", value, three)])).ToArray() }]
        };
        var shopVariants = new[]
        {
            new RouteVariantForecast([new(new MapPoint(0, 1), 2, 1, true, false)], RoomType.Shop, null, null,
                Forecast<MerchantInventoryForecast>.Branch(screenshotBlue, PredictionDependency.Shops, "fixture"), true),
            new RouteVariantForecast([new(new MapPoint(1, 1), 2, 2, true, false)], RoomType.Shop, null, null,
                Forecast<MerchantInventoryForecast>.Branch(screenshotGreen, PredictionDependency.Shops, "fixture"), true)
        };
        MerchantExample = MapForecastTooltipBuilder.Build(new(new MapPoint(0, 4) { PointType = MapPointType.Shop }, 4, 4,
            null, null, null, null, null, shopVariants, false))!;
        var fullShopText = string.Join("\n", MerchantExample.Descriptions);
        Require(fullShopText.Split("2、3层选牌：任意选择").Length == 2 && !fullShopText.Contains("跳过选牌")
            && fullShopText.Split("相对基础").Length == 3 && !fullShopText.Contains("第2格")
            && fullShopText.Contains("②完美打击 48金") && fullShopText.Contains("①头槌"),
            "complete map tooltip must render screenshot-equivalent stock and branches with matching numbered slots");
        Smoke.SmokeReport.Add("condition-tooltip COMBAT " + rewardText.Replace('\n', '|'));
        Smoke.SmokeReport.Add("condition-tooltip MERCHANT " + fullShopText.Replace('\n', '|'));

        var state = planning.CreateState(live, player);
        state.Run.Act._rooms.events.Clear();
        state.Run.Act._rooms.events.Add(ModelDb.Event<BrainLeech>());
        state.Run.Act._rooms.eventsVisited = 0;
        state.Run.Odds.UnknownMapPoint.MonsterOdds = 0;
        state.Run.Odds.UnknownMapPoint.EliteOdds = -1;
        state.Run.Odds.UnknownMapPoint.TreasureOdds = 0;
        state.Run.Odds.UnknownMapPoint.ShopOdds = 0;
        var eventPoint = new MapPoint(0, 1) { PointType = MapPointType.Unknown };
        var shopPoint = new MapPoint(0, 2) { PointType = MapPointType.Shop };
        eventPoint.AddChildPoint(shopPoint);
        var route = new RouteWorldline(new RoutePath([new(eventPoint, false, false), new(shopPoint, false, false)]), [],
            RoomType.Shop, null, null, 1, [RoomType.Event, RoomType.Shop], true);
        var predicted = new MerchantChoiceBranchService(2048, 96, 30000).Predict(state.Player, route,
            planning.GenerateMerchant(planning.Restore(planning.Capture(state))));
        var actualPaths = predicted.ReferenceChoicePaths.Concat(predicted.ChoiceBranches.SelectMany(branch => branch.ChoicePaths)).ToArray();
        Require(actualPaths.Any(path => path.Facts.Any(fact => fact.Kind == ForecastChoiceKind.EventCard && fact.Closed)),
            "native event candidate domain never reached the displayed merchant conditions");
        var duplicateLabels = actualPaths.GroupBy(path => path.Condition).Any(group => group.Count() > 1);
        lines.Clear();
        MapForecastTooltipBuilder.AppendMerchantContents(lines, predicted, true);
        var nativeText = string.Join("\n", lines);
        var rawLength = actualPaths.Sum(path => path.Condition.Length);
        // Stock names vary by character. Its unrelated length must not decide
        // whether equal event targets were combined into fewer conditions.
        var compactTerms = MerchantForecastSummary.Group(MerchantForecastSummary.Cases(predicted))
            .Sum(group => group.Terms.Count);
        Require(compactTerms < actualPaths.Length, "native event targets did not compact");
        var combatA = new MapPoint(0, 1) { PointType = MapPointType.Monster };
        var combatB = new MapPoint(0, 2) { PointType = MapPointType.Monster };
        var combatRoute = new RouteWorldline(new RoutePath([new(combatA, false, false), new(combatB, false, false)]), [],
            RoomType.Monster, null, null, 0, [RoomType.Monster, RoomType.Monster], true);
        var encounter = MerchantChoiceBranchService.ResolveEncounter(state, RoomType.Monster);
        using var initialRewards = planning.GenerateCombatRewards(planning.Restore(planning.Capture(state)), RoomType.Monster, encounter);
        var predictedRewards = new MerchantChoiceBranchService(512, 48, 30000).PredictRewards(state.Player, combatRoute, initialRewards.Detach());
        Require(predictedRewards.ReferenceChoicePaths.Count > 0,
            "native combat-to-combat adapter must retain structured reference paths, including unconditional ones");
        // Node audit: one-species encounters state the name once, and a uniform
        // lineup does not repeat that name in front of every HP value.
        MapForecastTooltip BuildEncounter(EncounterDetails details, IReadOnlyList<MonsterHpDetails> hp) =>
            MapForecastTooltipBuilder.Build(new MapNodeForecast(
                new MapPoint(0, 1) { PointType = MapPointType.Monster }, 1, 1, null, null,
                Forecast<IReadOnlyList<EncounterDetails>>.Exact([details]),
                hp.Count == 0 ? null : Forecast<IReadOnlyList<MonsterHpDetails>>.Exact(hp),
                null, [new([], RoomType.Monster, null, details, null, true)], false))!;
        var singleLineup = new EncounterDetails(encounter.Id, "花园幽灵鳗",
            ["花园幽灵鳗", "花园幽灵鳗", "花园幽灵鳗", "花园幽灵鳗"]);
        var encounterText = string.Join("\n", BuildEncounter(singleLineup,
            [new("花园幽灵鳗", 29), new("花园幽灵鳗", 27), new("花园幽灵鳗", 28)]).Descriptions);
        Require(encounterText.Contains("遭遇：花园幽灵鳗 ×4") && !encounterText.Contains("怪物：")
            && encounterText.Contains("HP：29 / 27 / 28"),
            "single-species encounters must not repeat names: " + encounterText);
        var mixedText = string.Join("\n", BuildEncounter(
                new EncounterDetails(encounter.Id, "一些噬尸蛞蝓", ["噬尸蛞蝓", "酸液史莱姆"]),
                [new("噬尸蛞蝓", 29), new("酸液史莱姆", 27)]).Descriptions);
        Require(mixedText.Contains("怪物：噬尸蛞蝓 + 酸液史莱姆")
            && mixedText.Contains("HP：噬尸蛞蝓 29 / 酸液史莱姆 27"),
            "mixed lineups must keep the monster names: " + mixedText);
        Smoke.SmokeReport.Add("node-audit ENCOUNTER " + encounterText.Replace('\n', '|'));
        var eventDetails = new EventDetails(encounter.Id, "脑蛭");
        var unknownEvent = MapForecastTooltipBuilder.Build(new MapNodeForecast(
            new MapPoint(0, 2) { PointType = MapPointType.Unknown }, 2, 2,
            Forecast<RoomType>.Exact(RoomType.Event),
            Forecast<IReadOnlyList<EventDetails>>.Exact([eventDetails]),
            null, null, null, [new([], RoomType.Event, eventDetails, null, null, true)], false))!;
        var unknownText = string.Join("\n", unknownEvent.Descriptions);
        Require(!unknownText.Contains("? →") && unknownText.Contains("事件：脑蛭"),
            "a resolved question mark must not restate its room type: " + unknownText);
        Smoke.SmokeReport.Add("node-audit UNKNOWN " + unknownText.Replace('\n', '|'));
        OrdinaryCardChoiceRegressionTests.Run(planning, live, player);
        Smoke.SmokeReport.Add($"condition-summary PASS: full_menu=any_choice partial_menu=retained correlation=preserved event_targets=collapsed per_case=complete route_prerequisites=once native_paths={actualPaths.Length} native_reward_paths={predictedRewards.ReferenceChoicePaths.Count} duplicate_source_labels={duplicateLabels} fixture_chars={text.Length} native_raw_chars={rawLength} native_display_chars={nativeText.Length} elapsed_ms={timer.ElapsedMilliseconds}");

        static MerchantItemForecast Store(string name, int cost, bool sale = false) =>
            new(new ForecastItemDetails(null, name, ForecastItemKind.Card), cost, sale);
    }

    private static ForecastChoiceFact Pick(int floor, string value, string label, IReadOnlyList<string> domain) =>
        new("combat:" + floor, $"{floor}层第1组选牌", value, label, domain,
            Kind: ForecastChoiceKind.CardReward, Floor: floor);

    private static ForecastChoiceFact Fact(string key, string value, IReadOnlyList<string> domain) =>
        new(key, key, value, value, domain);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Condition summary regression: " + message);
    }
}
#endif
