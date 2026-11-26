#if DEBUG
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.Smoke;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class RelicForecastRegressionTests
{
    private static readonly PlanningPredictionService Planning = new();

    internal static void Run(RunState live)
    {
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var before = LiveFingerprint.Capture(live);
        var adapter = new RandomForeseerAdapter();
        foreach (var (mode, players, cutoff) in new[]
                 { (GameMode.Standard, 1, 41), (GameMode.Standard, 2, 38), (GameMode.Blitz, 1, 21) })
        {
            CheckFloor(live, player, adapter, mode, players, cutoff - 1, late: false);
            CheckFloor(live, player, adapter, mode, players, cutoff, late: true);
        }
        CheckEventCombatFloor(live, player);
        CheckDriftwood(live, player, adapter);
        Require(before.DescribeDifference(LiveFingerprint.Capture(live)) is null, "live run changed");
        SmokeReport.Add("relic-forecast PASS: solo_40_41=matched multiplayer_37_38=matched blitz_20_21=matched "
                        + "elite_chest_shop=matched event_subcombat=same_floor driftwood_next_elite=matched live=unchanged");
    }

    private static void CheckFloor(RunState live, Player player,
        RandomForeseerAdapter adapter, GameMode mode, int players, int targetFloor, bool late)
    {
        var input = Fixture(live, player, mode, targetFloor - 2, players);
        var egg = ModelDb.Relic<FrozenEgg>();
        var iceCream = ModelDb.Relic<IceCream>();
        input.Player.RelicGrabBag._deques.Clear();
        input.Player.RelicGrabBag._deques[RelicRarity.Rare] = [egg, iceCream];
        input.Run.SharedRelicGrabBag._deques[RelicRarity.Rare] = [egg, iceCream];
        var raritySeed = Enumerable.Range(1, 100).First(seed =>
            RelicFactory.RollRarity(new Rng((ulong)seed)) == RelicRarity.Rare);
        input.Run.Rng.TreasureRoomRelics.LoadFromSerializable(new Rng((ulong)raritySeed).ToSerializable());
        var encounter = MerchantChoiceBranchService.ResolveEncounter(input, RoomType.Elite);
        var expected = late ? iceCream.Id : egg.Id;
        var elite = adapter.PredictCombatRewards(input.Player, [RoomType.RestSite, RoomType.Elite], encounter);
        Require(elite.Value?.Relics.First().Id == expected
                && (!late || elite.Value.Relics.All(relic => relic.Id != egg.Id)),
            $"{mode} {players}P {targetFloor}F elite eligibility");
        var chest = adapter.PredictTreasureRoom(input.Player, [RoomType.RestSite, RoomType.Treasure]);
        Require(chest.Value?.Relics.First().Id == expected, $"{mode} {players}P {targetFloor}F chest eligibility");

        if (mode != GameMode.Standard)
            return;

        var rest = new MapPoint(0, 5) { PointType = MapPointType.RestSite };
        var target = new MapPoint(0, 6) { PointType = MapPointType.Elite };
        rest.AddChildPoint(target);
        var chain = BuildPlan(input, [rest, target], [null, null]);
        Require(chain.Outcomes[target.coord].CombatRewards?.Value?.Relics.Single().Id == expected
                && chain.State.Run.TotalFloor == targetFloor, $"{targetFloor}F planned elite eligibility");

        var shopInput = Fixture(live, player, mode, targetFloor - 2, players);
        var fruit = ModelDb.Relic<DragonFruit>();
        var chemicalX = ModelDb.Relic<ChemicalX>();
        shopInput.Player.RelicGrabBag._deques[RelicRarity.Shop] = [chemicalX, fruit];
        var shop = adapter.PredictMerchantRoom(shopInput.Player, [RoomType.RestSite, RoomType.Shop]);
        var shopRelic = late ? chemicalX.Id : fruit.Id;
        Require(shop.Value?.Relics[2].Id == shopRelic, $"{targetFloor}F shop eligibility");
        target.PointType = MapPointType.Shop;
        var worldline = RouteWorldlinePredictor.Predict(shopInput.Run, target,
            [new RoutePath([new RouteStep(rest, false, false), new RouteStep(target, false, false)])]).Single();
        var choices = new MerchantChoiceBranchService(32, 64, 30000).Predict(shopInput.Player, worldline, shop.Value!);
        Require(choices.ReferenceCondition is not null && choices.Relics[2].Id == shopRelic,
            $"{targetFloor}F choice-expanded shop eligibility");
    }

    private static void CheckEventCombatFloor(RunState live, Player player)
    {
        var state = Fixture(live, player, GameMode.Standard, 39);
        var eventId = ModelDb.Event<BattlewornDummy>().Id;
        state.Run.AppendToMapPointHistory(MapPointType.Unknown, RoomType.Event, eventId);
        var encounter = MerchantChoiceBranchService.ResolveEncounter(state, RoomType.Elite);
        var eventCombat = new CombatRoom(encounter.ToMutable(), state.Run) { ParentEventId = eventId };
        using var rewards = Planning.GenerateCombatRewards(state, RoomType.Elite, encounter, eventCombat);
        Require(state.Run.TotalFloor == 40
                && state.Run.CurrentMapPointHistoryEntry!.Rooms.Select(room => room.RoomType)
                    .SequenceEqual(new[] { RoomType.Event, RoomType.Elite }), "event combat advanced a map floor");
    }

    private static void CheckDriftwood(RunState live, Player player,
        RandomForeseerAdapter adapter)
    {
        var input = Fixture(live, player, GameMode.Standard, 34);
        Planning.AddRelic(input, ModelDb.Relic<Driftwood>().Id);
        var encounters = input.Run.Act._rooms.eliteEncounters;
        input.Run.Act._rooms.eliteEncountersVisited = 0;
        var first = encounters[0];
        var second = encounters[1 % encounters.Count];
        var firstPoint = new MapPoint(0, 1) { PointType = MapPointType.Elite };
        var secondPoint = new MapPoint(0, 2) { PointType = MapPointType.Elite };
        firstPoint.AddChildPoint(secondPoint);
        for (ulong seed = 1; seed <= 16; seed++)
        {
            input.Player.PlayerRng.Rewards.LoadFromSerializable(new Rng(seed).ToSerializable());
            var snapshot = Planning.Capture(input);
            var skip = Planning.Restore(snapshot);
            var reroll = Planning.Restore(snapshot);
            var firstSkip = NativeElite(skip, first, reroll: false);
            var firstReroll = NativeElite(reroll, first, reroll: true);
            var nextSkip = NativeElite(skip, second, reroll: false);
            var nextReroll = NativeElite(reroll, second, reroll: false);
            if (nextSkip.Rarity == nextReroll.Rarity)
                continue;

            Require(firstSkip.Id == firstReroll.Id, "native reroll changed the current elite's generated relic");
            var skipChoice = new RoutePlanChoice.Combat([new(0, [new(AlternativeId: "Skip")])], null, false);
            var rerollChoice = new RoutePlanChoice.Combat(
                [new(0, [new(AlternativeId: "REROLL"), new(AlternativeId: "Skip")])], null, false);
            var skipped = BuildPlan(input, [firstPoint, secondPoint], [skipChoice, null]);
            var rerolled = BuildPlan(input, [firstPoint, secondPoint], [rerollChoice, null]);
            Require(skipped.Outcomes[firstPoint.coord].CombatRewards?.Value?.Relics.Single().Id == firstSkip.Id
                    && rerolled.Outcomes[firstPoint.coord].CombatRewards?.Value?.Relics.Single().Id == firstSkip.Id
                    && skipped.Outcomes[secondPoint.coord].CombatRewards?.Value?.Relics.Single().Id == nextSkip.Id
                    && rerolled.Outcomes[secondPoint.coord].CombatRewards?.Value?.Relics.Single().Id == nextReroll.Id,
                "planned downstream elite did not match native reroll replay");

            var worldline = RouteWorldlinePredictor.Predict(input.Run, secondPoint,
                [new RoutePath([new RouteStep(firstPoint, false, false), new RouteStep(secondPoint, false, false)])]).Single();
            var baseline = adapter.PredictCombatRewards(input.Player, worldline.ResolvedRooms, second);
            Require(baseline.Value?.Relics.Single().Id == nextSkip.Id, "no-reroll baseline diverged from native");
            var choices = new MerchantChoiceBranchService(128, 512, 30000).PredictRewards(input.Player, worldline, baseline.Value!);
            Require(choices.Relics.Single().Id == nextSkip.Id && choices.ChoiceBranches.Any(branch =>
                    branch.Outcome.Relics.Single().Id == nextReroll.Id
                    && branch.ChoicePaths.SelectMany(path => path.Facts).Any(fact => fact.Value.Contains("reroll", StringComparison.OrdinalIgnoreCase))),
                "map choice branches lost the downstream relic caused by Driftwood");
            SmokeReport.Add($"relic-forecast driftwood seed={seed} first={firstSkip.Id} skip={nextSkip.Rarity}/{nextSkip.Id} reroll={nextReroll.Rarity}/{nextReroll.Id}");
            return;
        }
        throw new InvalidOperationException("Relic forecast regression could not construct a rarity-changing reroll.");
    }

    private static RelicModel NativeElite(PlanningPredictionService.State state, EncounterModel encounter, bool reroll)
    {
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        state.Run.AppendToMapPointHistory(MapPointType.Elite, RoomType.Elite, encounter.Id);
        var room = new CombatRoom(encounter.ToMutable(), state.Run);
        state.Run.PushRoom(room);
        try
        {
            var rewards = new RewardsSet(state.Player).WithRewardsFromRoom(room);
            rewards.GenerateWithoutOffering().GetAwaiter().GetResult();
            var relic = rewards.Rewards.OfType<RelicReward>().Single().Relic
                        ?? throw new InvalidOperationException("The native elite relic wasn't populated.");
            if (reroll)
            {
                var rng = state.Player.PlayerRng.Rewards;
                var before = rng.ToSerializable().counter;
                rewards.Rewards.OfType<CardReward>().Single().Reroll();
                Require(rng.ToSerializable().counter - before == 9, "standard reroll must advance Rewards nine times");
            }
            return relic;
        }
        finally { state.Run.PopCurrentRoom(); }
    }

    private static PlanningPredictionService.State Fixture(RunState live,
        Player player, GameMode mode, int floor, int players = 1)
    {
        var snapshot = Planning.Capture(Planning.CreateState(live, player));
        snapshot.Run.CurrentActIndex = 2;
        snapshot.Run.GameMode = mode;
        snapshot.Run.VisitedMapCoords.Clear();
        snapshot.Run.MapPointHistory.Clear();
        if (players == 2)
        {
            var teammate = PlanningPredictionService.CloneSnapshot(snapshot.Run).Players[0];
            teammate.NetId = player.NetId + 1;
            snapshot.Run.Players.Add(teammate);
        }
        var state = Planning.Restore(snapshot);
        foreach (var relic in state.Player.Relics.ToArray())
            state.Player.RemoveRelicInternal(relic, silent: true);
        while (state.Run.TotalFloor < floor)
            state.Run.AppendToMapPointHistory(MapPointType.RestSite, RoomType.RestSite, null);
        return state;
    }

    private static RoutePlanForecastService.PlanChain BuildPlan(PlanningPredictionService.State state,
        MapPoint[] points, RoutePlanChoice?[] choices)
    {
        var entries = points.Select((point, index) => new RoutePlanEntry(point.coord, choices[index], null)).ToArray();
        var plan = new RoutePlan
        {
            ActIndex = state.Run.CurrentActIndex,
            AnchorCoord = new MapCoord { row = 0, col = 0 },
            Entries = entries,
            Snapshot = RoutePlanResourceSnapshot.Capture(state.Player)
        };
        var chain = new RoutePlanForecastService(Planning).BuildChain(state.Run, state.Player, entries,
            entry => points.Single(point => point.coord == entry.Coord), plan);
        Require(chain is { BlockedAtNode: null }, "relic forecast plan stopped unexpectedly");
        return chain!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Relic forecast regression: " + message);
    }
}
#endif

