using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using SeedOracle.UI;

namespace SeedOracle.Forecasting;

internal enum ShopOfferAvailability { Available, Unaffordable, PotionSlotsFull }

internal sealed record ForecastShopOffer(string Value, int Slot, MerchantItemForecast Item,
    ShopOfferAvailability Availability);

internal sealed record ForecastShopChoiceScope(int Floor, int Gold, IReadOnlyList<ForecastShopOffer> Offers,
    bool CardCombinationsEquivalent)
{
    public IReadOnlyList<string> UncalculatedValues { get; init; } = [];
    public IEnumerable<ForecastShopOffer> AvailableCards => Offers.Where(offer =>
        offer.Availability == ShopOfferAvailability.Available && offer.Item.Item.Kind == ForecastItemKind.Card);
    internal string Key => JsonSerializer.Serialize(new { Floor, Gold, Offers, CardCombinationsEquivalent });
}

internal static class ShopPurchaseChoices
{
    private static readonly ConcurrentDictionary<Type, bool> IndependentModels = new();
    private static readonly string[] RelevantHooks =
    [
        "Reward", "CombatEnd", "CombatVictory", "AddToDeck", "AddedToDeck", "CardChangedPiles",
        "ItemPurchased", "Merchant", "Gold", "Heal", "Hp", "Death", "Die"
    ];

    internal static ForecastShopChoiceScope Describe(PlanningPredictionService.State state,
        MerchantInventoryForecast stock, int floor, bool immediatelyBeforeFixedCombat)
    {
        var offers = stock.CharacterCards.Concat(stock.ColorlessCards).Concat(stock.Relics).Concat(stock.Potions)
            .Select((item, slot) => new ForecastShopOffer("buy:" + slot + ":" + ForecastContentKey.Item(item.Item),
                slot, item, state.Player.Gold < item.Cost ? ShopOfferAvailability.Unaffordable
                    : item.Item.Kind == ForecastItemKind.Potion && !state.Player.HasOpenPotionSlots
                        ? ShopOfferAvailability.PotionSlotsFull : ShopOfferAvailability.Available)).ToArray();
        // A card purchase without pickup/price/restock callbacks changes only
        // gold, deck and purchase history. No supported native reward operation
        // below observes those differences, so any affordable subset is covered
        // without enumerating all purchase orders or consuming more RNG.
        var free = CanIgnoreAddedCards(state, immediatelyBeforeFixedCombat,
            offers.Where(offer => offer.Availability == ShopOfferAvailability.Available
                && offer.Item.Item.Kind == ForecastItemKind.Card).Select(offer => offer.Item.Item));
        return new(floor, state.Player.Gold, offers, free);
    }

    internal static bool CanIgnoreAddedCards(PlanningPredictionService.State state, bool nextIsFixedCombat,
        IEnumerable<ForecastItemDetails> cards) => nextIsFixedCombat && OrdinaryCardChoiceEquivalence.HasNativePools(state)
            && state.Run.IterateHookListeners(null).All(model =>
                ReferenceEquals(model, state.Run.MultiplayerScalingModel) || IsIndependent(model))
            && cards.All(IsIndependentCard);

    internal static MerchantPick Pick(MerchantInventoryForecast stock, int slot)
    {
        if (slot < stock.CharacterCards.Count) return new(MerchantCategory.CharacterCard, slot);
        slot -= stock.CharacterCards.Count;
        if (slot < stock.ColorlessCards.Count) return new(MerchantCategory.ColorlessCard, slot);
        slot -= stock.ColorlessCards.Count;
        if (slot < stock.Relics.Count) return new(MerchantCategory.Relic, slot);
        return new(MerchantCategory.Potion, slot - stock.Relics.Count);
    }

    internal static ForecastChoiceFact Fact(ForecastShopChoiceScope scope, string value, string label, bool chinese)
    {
        var domain = new[] { "skip" }.Concat(scope.Offers.Where(offer => offer.Availability == ShopOfferAvailability.Available)
            .Select(offer => offer.Value)).ToArray();
        var cards = scope.AvailableCards.Select(offer => offer.Value).Prepend("skip").ToArray();
        return new("shop:" + scope.Floor, chinese ? $"{scope.Floor}层商店" : $"{scope.Floor}F shop",
            value, label, domain, Closed: false, Kind: ForecastChoiceKind.ShopPurchase, Floor: scope.Floor)
        {
            Shop = scope,
            EquivalentValues = scope.CardCombinationsEquivalent && cards.Contains(value) ? cards : []
        };
    }

    // Coverage is derived from complete target results, not merely a successful
    // intermediate purchase. Budget-limited or rejected actions remain visible.
    internal static IReadOnlyList<RewardChoiceBranch<T>> CompleteCoverage<T>(IReadOnlyList<RewardChoiceBranch<T>> results)
    {
        var covered = results.SelectMany(result => result.ChoicePaths).SelectMany(path => path.Facts)
            .Where(fact => fact.Shop is not null).GroupBy(fact => fact.Shop!.Key)
            .ToDictionary(group => group.Key, group => group.SelectMany(fact => fact.EquivalentValues.Count > 0
                ? fact.EquivalentValues : new[] { fact.Value }).ToHashSet(StringComparer.Ordinal));
        return results.Select(result => result with
        {
            ChoicePaths = result.ChoicePaths.Select(path => path with
            {
                Facts = path.Facts.Select(fact => fact.Shop is { } shop ? fact with
                {
                    Shop = shop with { UncalculatedValues = shop.Offers.Where(offer =>
                            offer.Availability == ShopOfferAvailability.Available && !covered[shop.Key].Contains(offer.Value))
                        .Select(offer => offer.Value).ToArray() }
                } : fact).ToArray()
            }).ToArray()
        }).ToArray();
    }

    private static bool IsIndependentCard(ForecastItemDetails item)
    {
        if (item.Id is null) return false;
        var card = ModelDb.GetById<CardModel>(item.Id);
        if (card.GetType().Assembly != typeof(CardModel).Assembly || !IsIndependent(card)) return false;
        if (item.StateKey is null) return true;
        var save = JsonSerializer.Deserialize(item.StateKey, JsonSerializationUtility.GetTypeInfo<SerializableCard>());
        return save?.Enchantment is null;
    }

    private static bool IsIndependent(AbstractModel model) => model is not TheHunt
        && IndependentModels.GetOrAdd(model.GetType(), type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName && method.GetBaseDefinition().DeclaringType == typeof(AbstractModel)
                && RelevantHooks.Any(part => method.Name.Contains(part, StringComparison.Ordinal)))
            .All(method => method.DeclaringType == typeof(AbstractModel)
                || method.Name == nameof(AbstractModel.AfterCombatVictory)
                    && (type == typeof(BurningBlood) || type == typeof(BlackBlood))
                || method.Name == nameof(AbstractModel.AfterCombatEnd) && type == typeof(RainbowRing)
                || method.Name == nameof(AbstractModel.ModifyMerchantPrice) && type == typeof(MembershipCard)
                || method.Name == nameof(AbstractModel.TryModifyRewards)
                    && (type == typeof(PrayerWheel) || type == typeof(WhiteStar))
                || method.Name == nameof(AbstractModel.TryModifyRewardsLate) && type == typeof(Driftwood)));
}
