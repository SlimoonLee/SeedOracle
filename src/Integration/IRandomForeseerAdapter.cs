using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.Integration;

internal interface IRandomForeseerAdapter
{
    IntegrationStatus Status { get; }

    Forecast<MerchantInventoryForecast> PredictInitialMerchant(Player player);

    Forecast<MerchantInventoryForecast> PredictMerchant(Player player, int futureVisitOrdinal);

    Forecast<CombatRewardDetails> PredictCombatRewards(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        EncounterModel encounter);

    Forecast<TreasureRoomDetails> PredictTreasureRoom(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms);

    Forecast<EventContentDetails>? PredictEventContents(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        EventModel eventModel);
}
