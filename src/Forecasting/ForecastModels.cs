using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
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

internal enum ForecastItemKind
{
    Text,
    Card,
    Relic,
    Potion,
    Orb
}

internal enum ForecastItemRarity
{
    None,
    Common,
    Uncommon,
    Rare,
    Ancient,
    Shop,
    Event,
    Curse,
    Quest
}

internal sealed record ForecastItemDetails(
    ModelId? Id,
    string Name,
    ForecastItemKind Kind,
    ForecastItemRarity Rarity = ForecastItemRarity.None,
    string? ImagePath = null,
    bool IsUpgraded = false)
{
    public static ForecastItemDetails Text(string text) =>
        new(null, text, ForecastItemKind.Text);

    public static ForecastItemDetails Card(CardModel card) =>
        new(
            card.Id,
            card.Title,
            ForecastItemKind.Card,
            CardRarityOf(card.Rarity),
            card.PortraitPath,
            card.IsUpgraded);

    public static ForecastItemDetails Relic(RelicModel relic) =>
        new(
            relic.Id,
            relic.Title.GetFormattedText(),
            ForecastItemKind.Relic,
            RelicRarityOf(relic.Rarity));

    public static ForecastItemDetails Potion(PotionModel potion) =>
        new(
            potion.Id,
            potion.Title.GetFormattedText(),
            ForecastItemKind.Potion,
            PotionRarityOf(potion.Rarity));

    public static ForecastItemDetails Orb(OrbModel orb) =>
        new(orb.Id, orb.Title.GetFormattedText(), ForecastItemKind.Orb);

    private static ForecastItemRarity CardRarityOf(CardRarity rarity) => rarity switch
    {
        CardRarity.Basic or CardRarity.Common => ForecastItemRarity.Common,
        CardRarity.Uncommon => ForecastItemRarity.Uncommon,
        CardRarity.Rare => ForecastItemRarity.Rare,
        CardRarity.Ancient => ForecastItemRarity.Ancient,
        CardRarity.Event => ForecastItemRarity.Event,
        CardRarity.Curse => ForecastItemRarity.Curse,
        CardRarity.Quest => ForecastItemRarity.Quest,
        _ => ForecastItemRarity.None
    };

    private static ForecastItemRarity RelicRarityOf(RelicRarity rarity) => rarity switch
    {
        RelicRarity.Starter or RelicRarity.Common => ForecastItemRarity.Common,
        RelicRarity.Uncommon => ForecastItemRarity.Uncommon,
        RelicRarity.Rare => ForecastItemRarity.Rare,
        RelicRarity.Ancient => ForecastItemRarity.Ancient,
        RelicRarity.Shop => ForecastItemRarity.Shop,
        RelicRarity.Event => ForecastItemRarity.Event,
        _ => ForecastItemRarity.None
    };

    private static ForecastItemRarity PotionRarityOf(PotionRarity rarity) => rarity switch
    {
        PotionRarity.Common => ForecastItemRarity.Common,
        PotionRarity.Uncommon => ForecastItemRarity.Uncommon,
        PotionRarity.Rare => ForecastItemRarity.Rare,
        PotionRarity.Event => ForecastItemRarity.Event,
        _ => ForecastItemRarity.None
    };
}

internal sealed record EventPredictionSetDetails(IReadOnlyList<ForecastItemDetails> Items);

internal sealed record EventOptionPredictionDetails(
    string Option,
    string TextKey,
    IReadOnlyList<ForecastItemDetails> InitialItems,
    IReadOnlyList<EventPredictionSetDetails> Sets);

internal sealed record EventContentDetails(
    IReadOnlyList<EventOptionPredictionDetails> Options);

internal sealed record MonsterHpDetails(string Monster, int Hp);

internal sealed record MerchantItemForecast(
    ForecastItemDetails Item,
    int Cost,
    bool IsOnSale = false)
{
    public ModelId Id => Item.Id!;

    public string Name => Item.Name;
}

internal sealed record MerchantInventoryForecast(
    IReadOnlyList<MerchantItemForecast> CharacterCards,
    IReadOnlyList<MerchantItemForecast> ColorlessCards,
    IReadOnlyList<MerchantItemForecast> Relics,
    IReadOnlyList<MerchantItemForecast> Potions,
    int CardRemovalCost,
    int FutureVisitOrdinal);

internal sealed record RewardItemDetails(ForecastItemDetails Item)
{
    public ModelId Id => Item.Id!;

    public string Name => Item.Name;
}

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
