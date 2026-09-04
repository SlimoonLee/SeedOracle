using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using SeedOracle.Api;

namespace SeedOracle.Forecasting;

internal sealed record RouteStep(
    MapPoint Point,
    bool UsedFreeTravel,
    bool FreeTravelWasAvailable);

internal sealed record RoutePath(IReadOnlyList<RouteStep> Steps)
{
    public int Length => Steps.Count;
}

internal sealed record RouteExploration(
    IReadOnlyList<RoutePath> Paths,
    bool WasTruncated);

internal sealed record RouteChoice(
    MapPoint Point,
    int Floor,
    int PositionFromLeft,
    bool IsDecision,
    bool UsedFreeTravel);

internal sealed record EncounterDetails(
    ModelId Id,
    string Title,
    IReadOnlyList<string> Monsters);

internal sealed record EventDetails(ModelId Id, string Title);

internal sealed record EventPredictionSetDetails(IReadOnlyList<string> Items);

internal sealed record EventOptionPredictionDetails(
    string Option,
    string TextKey,
    IReadOnlyList<string> InitialItems,
    IReadOnlyList<EventPredictionSetDetails> Sets);

internal sealed record EventContentDetails(
    IReadOnlyList<EventOptionPredictionDetails> Options);

internal sealed record MonsterHpDetails(string Monster, int Hp);

internal sealed record MerchantItemForecast(
    ModelId Id,
    string Name,
    int Cost,
    bool IsOnSale = false);

internal sealed record MerchantInventoryForecast(
    IReadOnlyList<MerchantItemForecast> CharacterCards,
    IReadOnlyList<MerchantItemForecast> ColorlessCards,
    IReadOnlyList<MerchantItemForecast> Relics,
    IReadOnlyList<MerchantItemForecast> Potions,
    int CardRemovalCost,
    int FutureVisitOrdinal);

internal sealed record RewardItemDetails(ModelId Id, string Name);

internal sealed record CombatRewardDetails(
    int Gold,
    IReadOnlyList<IReadOnlyList<RewardItemDetails>> CardRewards,
    IReadOnlyList<RewardItemDetails> Potions,
    IReadOnlyList<RewardItemDetails> Relics);

internal sealed record TreasureRoomDetails(
    int Gold,
    IReadOnlyList<RewardItemDetails> Relics,
    bool IsEmpty);

internal sealed record RouteVariantForecast(
    IReadOnlyList<RouteChoice> Route,
    RoomType RoomType,
    EventDetails? Event,
    EncounterDetails? Encounter,
    Forecast<MerchantInventoryForecast>? Merchant,
    bool HasUnmodeledStateDependency,
    Forecast<CombatRewardDetails>? CombatRewards = null,
    Forecast<TreasureRoomDetails>? Treasure = null,
    Forecast<EventContentDetails>? EventContents = null);

internal sealed record RouteWorldline(
    RoutePath Path,
    IReadOnlyList<RouteChoice> Route,
    RoomType TargetRoomType,
    EventModel? TargetEvent,
    EncounterModel? TargetEncounter,
    int FutureMerchantVisitOrdinal,
    IReadOnlyList<RoomType> ResolvedRooms,
    bool HasUnmodeledStateDependency);

internal sealed record MapNodeForecast(
    MapPoint Point,
    int MinimumSteps,
    int MaximumSteps,
    Forecast<RoomType>? UnknownRoom,
    Forecast<IReadOnlyList<EventDetails>>? Events,
    Forecast<IReadOnlyList<EncounterDetails>>? Encounter,
    Forecast<IReadOnlyList<MonsterHpDetails>>? MonsterHp,
    Forecast<MerchantInventoryForecast>? Merchant,
    IReadOnlyList<RouteVariantForecast> RouteVariants,
    bool RoutesTruncated);

internal sealed record GeneratedEncounter(
    EncounterModel Source,
    EncounterModel MutableEncounter,
    IReadOnlyList<(MonsterModel Monster, string? Slot)> Monsters,
    EncounterDetails Details);
