using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.Integration;

internal sealed partial class RandomForeseerAdapter
{
    private const PredictionDependency CombatRewardDependencies =
        PredictionDependency.Rewards
        | PredictionDependency.RelicGrabBag
        | PredictionDependency.CardRarityOdds
        | PredictionDependency.PlayerState
        | PredictionDependency.Shops;

    private const PredictionDependency TreasureDependencies =
        PredictionDependency.TreasureRelics
        | PredictionDependency.Rewards
        | PredictionDependency.RelicGrabBag
        | PredictionDependency.PlayerState
        | PredictionDependency.Shops;

    private const PredictionDependency MerchantDependencies =
        PredictionDependency.Shops
        | PredictionDependency.TreasureRelics
        | PredictionDependency.Rewards
        | PredictionDependency.RelicGrabBag
        | PredictionDependency.CardRarityOdds
        | PredictionDependency.PlayerState;

    public Forecast<CombatRewardDetails> PredictCombatRewards(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        EncounterModel encounter)
    {
        if (resolvedRooms.Count == 0 || !resolvedRooms[^1].IsCombatRoom())
            throw new ArgumentException("The resolved route must end in a combat room.", nameof(resolvedRooms));

        try
        {
            var planning = new PlanningPredictionService();
            var state = planning.CreateState((RunState)player.RunState, player);
            using var isolation = ShadowIsolation.Enter(state.Player);
            planning.AdvanceRoomsBeforeTarget(state, resolvedRooms);
            var before = planning.Capture(state);
            using var generated = planning.GenerateCombatRewards(state, resolvedRooms[^1], encounter);
            var details = CombatRewardBranchService.AddPossibilities(
                planning, before, resolvedRooms[^1], encounter, generated.Detach());
            return Forecast<CombatRewardDetails>.Branch(
                details,
                CombatRewardDependencies,
                "Base rewards assume victory without escapes, combat-added rewards or changes to reward state; intervening choices remain conditional.");
        }
        catch (Exception exception)
        {
            return Forecast<CombatRewardDetails>.Unsupported(
                $"Native combat-reward prediction failed: {exception.GetBaseException().Message}",
                CombatRewardDependencies);
        }
    }

    public Forecast<MerchantInventoryForecast> PredictMerchantRoom(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms)
    {
        if (resolvedRooms.Count == 0 || resolvedRooms[^1] != RoomType.Shop)
            throw new ArgumentException("The resolved route must end in a shop room.", nameof(resolvedRooms));

        try
        {
            // Earlier rooms can consume relics and cross a floor cutoff.
            // Replay each room before generating this shop on the shadow run.
            var planning = new PlanningPredictionService();
            var state = planning.CreateState((RunState)player.RunState, player);
            using var isolation = ShadowIsolation.Enter(state.Player);
            planning.AdvanceRoomsBeforeTarget(state, resolvedRooms);
            var details = planning.GenerateMerchant(state);
            return resolvedRooms.Count == 1 && player.RunState.Players.Count == 1
                ? Forecast<MerchantInventoryForecast>.CurrentWorldline(
                    details,
                    MerchantDependencies,
                    "Advances the native route on a cloned run; the next shop generates from the current worldline.")
                : Forecast<MerchantInventoryForecast>.Branch(
                    details,
                    MerchantDependencies,
                    "Advances native combat rewards, shops and treasure on a cloned route; intervening choices remain conditional.");
        }
        catch (Exception exception)
        {
            return Forecast<MerchantInventoryForecast>.Unsupported(
                $"Native merchant prediction failed: {exception.GetBaseException().Message}",
                MerchantDependencies);
        }
    }

    public Forecast<TreasureRoomDetails> PredictTreasureRoom(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        IReadOnlyList<MapPoint>? resolvedPoints = null)
    {
        if (resolvedRooms.Count == 0 || resolvedRooms[^1] != RoomType.Treasure)
            throw new ArgumentException("The resolved route must end in a treasure room.", nameof(resolvedRooms));

        try
        {
            var planning = new PlanningPredictionService();
            var state = planning.CreateState((RunState)player.RunState, player);
            using var isolation = ShadowIsolation.Enter(state.Player);
            planning.AdvanceRoomsBeforeTarget(state, resolvedRooms, resolvedPoints);
            var details = planning.GenerateTreasure(state, isPriorRoom: false,
                resolvedPoints is { Count: > 0 } points ? points[^1] : null);
            return resolvedRooms.Count == 1 && player.RunState.Players.Count == 1
                ? Forecast<TreasureRoomDetails>.CurrentWorldline(
                    details,
                    TreasureDependencies,
                    "Uses cloned treasure RNG, relic bags, reward RNG and native treasure hooks.")
                : Forecast<TreasureRoomDetails>.Branch(
                    details,
                    TreasureDependencies,
                    "Advances native combat rewards, shops and treasure on a cloned route; skipped relics and multiplayer voting remain conditional.");
        }
        catch (Exception exception)
        {
            return Forecast<TreasureRoomDetails>.Unsupported(
                $"Native treasure-room prediction failed: {exception.GetBaseException().Message}",
                TreasureDependencies);
        }
    }
}
