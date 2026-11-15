using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.Integration;

internal interface IRandomForeseerAdapter
{
    IntegrationStatus Status { get; }

    /// <summary>
    /// Whether RF's internal-signature legacy path (initial/ordinal merchant)
    /// is usable. RF loaders that fall back to an old inner variant degrade
    /// this to false; the smoke gate then skips the legacy assertions.
    /// </summary>
    bool SupportsLegacyInternalSignatures => false;

    /// <summary>Whether the RF event-content bridge is usable for map-hover
    /// event contents.</summary>
    bool SupportsEventContentBridge => false;


    Forecast<MerchantInventoryForecast> PredictInitialMerchant(Player player);

    Forecast<MerchantInventoryForecast> PredictMerchant(Player player, int futureVisitOrdinal);

    Forecast<MerchantInventoryForecast> PredictMerchantRoom(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms);

    Forecast<CombatRewardDetails> PredictCombatRewards(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        EncounterModel encounter);

    Forecast<TreasureRoomDetails> PredictTreasureRoom(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        IReadOnlyList<MapPoint>? resolvedPoints = null);

    Forecast<EventContentDetails>? PredictEventContents(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        EventModel eventModel);
}
