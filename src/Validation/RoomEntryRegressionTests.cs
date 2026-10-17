#if DEBUG
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.Smoke;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class RoomEntryRegressionTests
{
    internal static void Run(RunState live)
    {
        var original = LiveFingerprint.Capture(live);
        var planning = new PlanningPredictionService();
        var source = planning.CreateState(live, LocalContext.GetMe(live) ?? live.Players[0]);
        planning.AddRelic(source, ModelDb.Relic<MawBank>().Id);
        planning.AddRelic(source, ModelDb.Relic<EternalFeather>().Id);
        source.Player.Gold = 100;
        source.Player.Creature.SetCurrentHpInternal(source.Player.Creature.MaxHp - 30);
        var snapshot = planning.Capture(source);
        var nativeRest = planning.Restore(snapshot);
        using (ShadowIsolation.Enter(nativeRest.Player))
        {
            var room = new RestSiteRoom();
            nativeRest.Run.PushRoom(room);
            try { Hook.AfterRoomEntered(nativeRest.Run, room).GetAwaiter().GetResult(); }
            finally { nativeRest.Run.PopCurrentRoom(); }
        }
        var predictedRest = planning.Restore(snapshot);
        planning.AdvanceRoomsBeforeTarget(predictedRest, [RoomType.RestSite, RoomType.Shop]);

        var nativeCombat = planning.Restore(snapshot);
        var encounter = MerchantChoiceBranchService.ResolveEncounter(nativeCombat, RoomType.Monster);
        using (ShadowIsolation.Enter(nativeCombat.Player))
        {
            var room = new CombatRoom(encounter.ToMutable(), nativeCombat.Run);
            nativeCombat.Run.PushRoom(room);
            try { nativeCombat.Player.Relics.OfType<MawBank>().Single().AfterRoomEntered(room).GetAwaiter().GetResult(); }
            finally { nativeCombat.Run.PopCurrentRoom(); }
        }
        var predictedCombat = planning.Restore(snapshot);
        using var rewards = planning.GenerateCombatRewards(predictedCombat, RoomType.Monster, encounter);

        var nativeChest = planning.Restore(snapshot);
        using (ShadowIsolation.Enter(nativeChest.Player))
        {
            var room = new TreasureRoom(nativeChest.Run.CurrentActIndex);
            nativeChest.Run.PushRoom(room);
            try { Hook.AfterRoomEntered(nativeChest.Run, room).GetAwaiter().GetResult(); }
            finally { nativeChest.Run.PopCurrentRoom(); }
        }
        var predictedChest = planning.Restore(snapshot);
        using (ShadowIsolation.Enter(predictedChest.Player)) planning.GenerateTreasure(predictedChest, false);
        SmokeReport.Add($"room-entry OBSERVED: rest_gold={predictedRest.Player.Gold}/{nativeRest.Player.Gold} rest_hp={predictedRest.Player.Creature.CurrentHp}/{nativeRest.Player.Creature.CurrentHp} combat_gold={predictedCombat.Player.Gold}/{nativeCombat.Player.Gold} chest_gold={predictedChest.Player.Gold}/{nativeChest.Player.Gold}");
        Require(predictedRest.Player.Gold == nativeRest.Player.Gold
                && predictedRest.Player.Creature.CurrentHp == nativeRest.Player.Creature.CurrentHp,
            "rest-room entry must preserve native Maw Bank income and Eternal Feather healing");
        Require(predictedCombat.Player.Gold == nativeCombat.Player.Gold, "combat entry omitted native Maw Bank income");
        Require(predictedChest.Player.Gold == nativeChest.Player.Gold, "chest entry must run with the chest as the base room");
        var points = new[]
        {
            new MapPoint(0, 1) { PointType = MapPointType.RestSite },
            new MapPoint(0, 2) { PointType = MapPointType.Treasure },
            new MapPoint(0, 3) { PointType = MapPointType.Shop }
        };
        var entries = points.Select(point => new RoutePlanEntry(point.coord,
            point.PointType == MapPointType.Treasure ? new RoutePlanChoice.Relic(false) : null, null)).ToArray();
        var input = planning.Restore(snapshot);
        var plan = new RoutePlan { ActIndex = input.Run.CurrentActIndex, AnchorCoord = input.Run.CurrentMapCoord ?? input.Run.Map.StartingMapPoint.coord,
            Entries = entries, Snapshot = RoutePlanResourceSnapshot.Capture(input.Player) };
        var chain = new RoutePlanForecastService(planning).BuildChain(input.Run, input.Player, entries,
            entry => points.Single(point => point.coord == entry.Coord), plan);
        var chestDrop = chain?.Outcomes[points[1].coord].Treasure?.Value?.Gold ?? -1;
        Require(chain is { BlockedAtNode: null } && chain.Gold == 136 + chestDrop
                && chain.State.Player.Gold == chain.Gold && chain.Outcomes[points[0].coord].GoldDelta == 12
                && chain.Outcomes[points[1].coord].GoldDelta == 12 + chestDrop
                && chain.Outcomes[points[2].coord].MerchantGoldAfterPurchases == chain.Gold,
            "automatic income must reach the planned ledger and downstream shop budget without being overwritten");
        var modified = planning.Restore(snapshot);
        var deadly = ModelDb.Modifier<DeadlyEvents>().ToMutable();
        modified.Run.AddModifierDebug(deadly);
        deadly.OnRunLoaded(modified.Run);
        modified.Run.Odds.UnknownMapPoint.MonsterOdds = 0;
        modified.Run.Odds.UnknownMapPoint.EliteOdds = 1;
        modified.Run.Odds.UnknownMapPoint.TreasureOdds = 0;
        modified.Run.Odds.UnknownMapPoint.ShopOdds = 0;
        var loaded = planning.Restore(planning.Capture(modified));
        var nativeRoll = modified.Run.Odds.UnknownMapPoint.Roll([], modified.Run);
        var loadedRoll = loaded.Run.Odds.UnknownMapPoint.Roll([], loaded.Run);
        Require(nativeRoll == loadedRoll && modified.Run.Odds.UnknownMapPoint.EliteOdds == loaded.Run.Odds.UnknownMapPoint.EliteOdds,
            "modifier load callback must restore elite base odds before unknown-room rolls");
        Require(original.DescribeDifference(LiveFingerprint.Capture(live)) is null, "room-entry checks changed the real run");
        SmokeReport.Add("room-entry PASS: rest_income=matched automatic_rest_heal=matched combat_income=matched chest_income=matched ledger_and_shop_budget=matched deadly_events_base_odds=matched live=unchanged");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Room entry regression: " + message);
    }
}
#endif
