using System.Text.Json;
using System.Runtime.CompilerServices;

namespace SeedOracle.Forecasting;

// Keys follow detached display state, never only the canonical model ID.
internal static class ForecastContentKey
{
    private static readonly ConditionalWeakTable<ForecastItemDetails, string> ItemKeys = new();

    internal static string Item(ForecastItemDetails item) => ItemKeys.GetValue(item, static value => JsonSerializer.Serialize(new
    {
        Id = value.Id?.ToString(), value.Kind, value.Name, value.Rarity,
        value.IsUpgraded, value.StateKey, value.ImagePath
    }));

    internal static string Merchant(MerchantInventoryForecast inventory) => JsonSerializer.Serialize(new
    {
        Character = inventory.CharacterCards.Select(Entry),
        Colorless = inventory.ColorlessCards.Select(Entry),
        Relics = inventory.Relics.Select(Entry),
        Potions = inventory.Potions.Select(Entry),
        inventory.CardRemovalCost, inventory.FutureVisitOrdinal,
        inventory.ReferenceCondition,
        Branches = inventory.ChoiceBranches.Select(branch => new
            { branch.Condition, Inventory = Merchant(branch.Inventory) }),
        inventory.ChoicesTruncated, inventory.UnresolvedChoices
    });

    // Presentation grouping compares the stock itself; its route/choice conditions
    // are retained beside the grouped result rather than repeated in the stock key.
    internal static string MerchantStock(MerchantInventoryForecast inventory) => JsonSerializer.Serialize(new
    {
        Character = inventory.CharacterCards.Select(Entry),
        Colorless = inventory.ColorlessCards.Select(Entry),
        Relics = inventory.Relics.Select(Entry),
        Potions = inventory.Potions.Select(Entry),
        inventory.CardRemovalCost
    });

    internal static string MerchantConditions(MerchantInventoryForecast inventory) =>
        inventory.ReferenceChoicePaths.Count == 0 && inventory.ChoiceBranches.All(branch => branch.ChoicePaths.Count == 0)
            ? "" : JsonSerializer.Serialize(new
            {
                Reference = inventory.ReferenceChoicePaths,
                Branches = inventory.ChoiceBranches.Select(branch => branch.ChoicePaths)
            });

    internal static string Combat(CombatRewardDetails rewards) => JsonSerializer.Serialize(new
    {
        rewards.Gold,
        Groups = rewards.CardRewardGroups.Select(group => new
        {
            Steps = group.Steps.Select(step => new
            {
                Cards = step.Cards.Select(card => Item(card.Item)), step.Alternatives
            })
        }),
        Potions = rewards.Potions.Select(item => Item(item.Item)),
        Relics = rewards.Relics.Select(item => Item(item.Item)),
        rewards.Possibilities, rewards.PossibilitiesTruncated, rewards.HuntPreviewLimit
    });

    internal static string Treasure(TreasureRoomDetails treasure) => JsonSerializer.Serialize(new
    {
        treasure.Gold, treasure.IsEmpty, Relics = treasure.Relics.Select(item => Item(item.Item))
    });

    private static string Entry(MerchantItemForecast entry) =>
        JsonSerializer.Serialize(new { Item = Item(entry.Item), entry.Cost, entry.IsOnSale });
}
