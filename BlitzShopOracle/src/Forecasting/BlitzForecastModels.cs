using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace BlitzShopOracle.Forecasting;

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
    bool IsUpgraded = false,
    string? StateKey = null)
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
            card.IsUpgraded,
            card.IsMutable
                ? JsonSerializer.Serialize(card.ToSerializable(), JsonSerializationUtility.GetTypeInfo<SerializableCard>())
                : null);

    public static ForecastItemDetails Relic(RelicModel relic) =>
        new(relic.Id, relic.Title.GetFormattedText(), ForecastItemKind.Relic, RelicRarityOf(relic.Rarity));

    public static ForecastItemDetails Potion(PotionModel potion) =>
        new(potion.Id, potion.Title.GetFormattedText(), ForecastItemKind.Potion, PotionRarityOf(potion.Rarity));

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

internal sealed record BlitzYieldForecast
{
    internal static readonly BlitzYieldForecast Empty = new();
    public IReadOnlyList<IReadOnlyList<ForecastItemDetails>> CardScreens { get; init; } = [];
    public IReadOnlyList<ForecastItemDetails> Relics { get; init; } = [];
    public IReadOnlyList<ForecastItemDetails> Potions { get; init; } = [];
    public IReadOnlyList<ForecastItemDetails> GainedCards { get; init; } = [];
    public IReadOnlyList<ForecastItemDetails> UpgradedCards { get; init; } = [];
    public IReadOnlyList<BlitzRandomUpgradeVariant> RandomUpgradeVariants { get; init; } = [];
    public int MaxHpDelta { get; init; }
    public int HealedHp { get; init; }
    public int ExtraChoices { get; init; }
    public bool NeedsCardPick { get; init; }
    public IReadOnlyList<BlitzCardChoiceOutcome> TransformChoices { get; init; } = [];
    public int PickableCards { get; init; }
    public bool Resolved { get; init; }
    public bool Purchased { get; init; }
}

internal sealed record BlitzCardChoiceOutcome(ForecastItemDetails Source, ForecastItemDetails Result)
{
    public string? Origin { get; init; }
    public IReadOnlyList<int> ViaSlots { get; init; } = [];
}

internal enum BlitzRandomUpgradeChoiceKind
{
    Reward,
    Remove,
    Transform,
    Upgrade,
    Enchant
}

internal sealed record BlitzRandomUpgradeChoice(
    int SourceSlot,
    int ScreenIndex,
    ForecastItemDetails? Card,
    ForecastItemDetails? Result = null,
    BlitzRandomUpgradeChoiceKind Kind = BlitzRandomUpgradeChoiceKind.Reward);

internal sealed record BlitzRandomUpgradeVariant(
    IReadOnlyList<BlitzRandomUpgradeChoice> Choices,
    IReadOnlyList<ForecastItemDetails> UpgradedCards);


