using System.Reflection;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.Integration;

internal sealed partial class RandomForeseerAdapter : IRandomForeseerAdapter
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly Type? _contextType;
    private readonly ConstructorInfo? _contextConstructor;
    private readonly PropertyInfo? _contextRngProperty;
    private readonly PropertyInfo? _contextRelicGrabBagProperty;
    private readonly PropertyInfo? _contextPotionRewardOddsProperty;
    private readonly MethodInfo? _predictCardMethod;
    private readonly MethodInfo? _predictCardsMethod;
    private readonly MethodInfo? _fastForwardCombatEndMethod;
    private readonly MethodInfo? _modifyMerchantPriceMethod;
    private readonly FieldInfo? _characterEntriesField;
    private readonly FieldInfo? _colorlessEntriesField;
    private readonly PropertyInfo? _creationResultProperty;
    private readonly PropertyInfo? _isOnSaleProperty;
    private readonly FieldInfo? _rawCostField;

    public IntegrationStatus Status { get; }

    private bool SupportsInitialMerchant =>
        _contextConstructor is not null
        && _contextRngProperty is not null
        && _contextRelicGrabBagProperty is not null
        && _predictCardMethod is not null
        && _modifyMerchantPriceMethod is not null
        && _characterEntriesField is not null
        && _colorlessEntriesField is not null
        && _creationResultProperty is not null
        && _isOnSaleProperty is not null
        && _rawCostField is not null;

    private bool SupportsRouteRewards =>
        SupportsInitialMerchant
        && _contextPotionRewardOddsProperty is not null
        && _predictCardsMethod is not null
        && _fastForwardCombatEndMethod is not null;

    public RandomForeseerAdapter()
    {
        var assembly = typeof(global::RandomForeseer.RandomForeseerCode.Entry).Assembly;
        _contextType = assembly.GetType(
            "RandomForeseer.RandomForeseerCode.OutOfCombat.RunPredictionContext");
        var merchantPredictionType = assembly.GetType(
            "RandomForeseer.RandomForeseerCode.OutOfCombat.MerchantRestockPrediction");
        var hookMirrorsType = assembly.GetType(
            "RandomForeseer.RandomForeseerCode.OutOfCombat.Mirrors.HookMirrors");
        var cardRewardPredictionType = assembly.GetType(
            "RandomForeseer.RandomForeseerCode.OutOfCombat.CardRewardPrediction");
        var combatEndEffectPredictionType = assembly.GetType(
            "RandomForeseer.RandomForeseerCode.OutOfCombat.CombatEndEffectPrediction");

        _contextConstructor = _contextType?.GetConstructor(
            AnyInstance,
            binder: null,
            [typeof(Player)],
            modifiers: null);
        _contextRngProperty = _contextType?.GetProperty("Rng", AnyInstance);
        _contextRelicGrabBagProperty = _contextType?.GetProperty("RelicGrabBag", AnyInstance);
        _contextPotionRewardOddsProperty = _contextType?.GetProperty("PotionRewardOdds", AnyInstance);
        _predictCardMethod = merchantPredictionType?
            .GetMethods(AnyStatic)
            .SingleOrDefault(method => method.Name == "PredictCard" && method.GetParameters().Length == 3);
        _modifyMerchantPriceMethod = hookMirrorsType?
            .GetMethods(AnyStatic)
            .SingleOrDefault(method => method.Name == "ModifyMerchantPrice" && method.GetParameters().Length == 4);
        _predictCardsMethod = cardRewardPredictionType?
            .GetMethods(AnyStatic)
            .SingleOrDefault(method => method.Name == "PredictCards"
                                       && method.GetParameters().Length == 5
                                       && method.GetParameters()[0].ParameterType == _contextType);
        _fastForwardCombatEndMethod = combatEndEffectPredictionType?
            .GetMethods(AnyStatic)
            .SingleOrDefault(method => method.Name == "FastForwardMonsterRoomCombatEndHooks"
                                       && method.GetParameters().Length == 1
                                       && method.GetParameters()[0].ParameterType == _contextType);

        _characterEntriesField = typeof(MerchantInventory).GetField("_characterCardEntries", AnyInstance);
        _colorlessEntriesField = typeof(MerchantInventory).GetField("_colorlessCardEntries", AnyInstance);
        _creationResultProperty = typeof(MerchantCardEntry).GetProperty(nameof(MerchantCardEntry.CreationResult), AnyInstance);
        _isOnSaleProperty = typeof(MerchantCardEntry).GetProperty(nameof(MerchantCardEntry.IsOnSale), AnyInstance);
        _rawCostField = typeof(MerchantEntry).GetField("_cost", AnyInstance);

        Status = new IntegrationStatus(
            "RandomForeseer",
            GetVersion(assembly),
            true,
            new Dictionary<string, bool>
            {
                ["prediction_context"] = _contextConstructor is not null,
                ["merchant_card_bridge"] = _predictCardMethod is not null,
                ["merchant_price_hook_mirror"] = _modifyMerchantPriceMethod is not null,
                ["initial_merchant_public_api"] = false,
                ["initial_merchant_adapter"] = SupportsInitialMerchant,
                ["combat_reward_adapter"] = SupportsRouteRewards,
                ["treasure_room_adapter"] = SupportsRouteRewards,
                ["event_content_adapter"] = SupportsRouteRewards && EventPredictionBridge.Value.IsAvailable
            });
    }

    public Forecast<MerchantInventoryForecast> PredictInitialMerchant(Player player)
        => PredictMerchant(player, 1);

    public Forecast<MerchantInventoryForecast> PredictMerchant(Player player, int futureVisitOrdinal)
    {
        const PredictionDependency dependencies =
            PredictionDependency.Shops
            | PredictionDependency.Rewards
            | PredictionDependency.RelicGrabBag
            | PredictionDependency.CardRarityOdds
            | PredictionDependency.PlayerState;

        if (futureVisitOrdinal < 1)
            throw new ArgumentOutOfRangeException(nameof(futureVisitOrdinal));

        if (!SupportsInitialMerchant)
        {
            return Forecast<MerchantInventoryForecast>.Unsupported(
                "Random Foreseer does not expose the internal signatures required by this adapter version.",
                dependencies);
        }

        try
        {
            var context = _contextConstructor!.Invoke([player]);
            var rngSet = _contextRngProperty!.GetValue(context)
                         ?? throw new MissingMemberException(_contextType!.FullName, "Rng");
            var rewards = GetRng(rngSet, "Rewards");
            var shops = GetRng(rngSet, "Shops");
            var relicGrabBag = (MegaCrit.Sts2.Core.Runs.RelicGrabBag?)_contextRelicGrabBagProperty!.GetValue(context)
                               ?? throw new MissingMemberException(_contextType!.FullName, "RelicGrabBag");
            var sharedRelicGrabBag = MegaCrit.Sts2.Core.Runs.RelicGrabBag.FromSerializable(
                player.RunState.SharedRelicGrabBag.ToSerializable());

            MerchantInventoryForecast? forecast = null;
            for (var visit = 1; visit <= futureVisitOrdinal; visit++)
            {
                forecast = PredictMerchantVisit(
                    context,
                    player,
                    rewards,
                    shops,
                    relicGrabBag,
                    sharedRelicGrabBag,
                    visit);
            }

            return futureVisitOrdinal == 1
                ? Forecast<MerchantInventoryForecast>.CurrentWorldline(
                    forecast!,
                    dependencies,
                    "Uses Random Foreseer's cloned prediction context and hook mirrors.")
                : Forecast<MerchantInventoryForecast>.Branch(
                    forecast!,
                    dependencies,
                    "Assumes earlier merchant visits only generate their initial stock; purchases, restocks, removals, and unrelated reward RNG consumers are unchanged.");
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            return Forecast<MerchantInventoryForecast>.Unsupported(
                $"Random Foreseer adapter rejected the current merchant state: {root.Message}",
                dependencies);
        }
    }

    private MerchantInventoryForecast PredictMerchantVisit(
        object context,
        Player player,
        Rng rewards,
        Rng shops,
        MegaCrit.Sts2.Core.Runs.RelicGrabBag relicGrabBag,
        MegaCrit.Sts2.Core.Runs.RelicGrabBag sharedRelicGrabBag,
        int visit)
    {
        var inventory = new MerchantInventory(player);
        var characterCards = PredictCharacterCards(context, inventory, player, shops);
        var colorlessCards = PredictColorlessCards(context, inventory, player, shops);
        var relics = PredictRelics(player, rewards, shops, relicGrabBag, sharedRelicGrabBag);
        var potions = PredictPotions(player, shops);
        var removalCost = PredictRemovalCost(player);
        return new MerchantInventoryForecast(
            characterCards,
            colorlessCards,
            relics,
            potions,
            removalCost,
            visit);
    }

    private IReadOnlyList<MerchantItemForecast> PredictCharacterCards(
        object context,
        MerchantInventory inventory,
        Player player,
        Rng shops)
    {
        CardType[] cardTypes =
        [
            CardType.Attack,
            CardType.Attack,
            CardType.Skill,
            CardType.Skill,
            CardType.Power
        ];
        var saleIndex = shops.NextInt(cardTypes.Length);
        var pool = player.Character.CardPool
            .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
            .ToArray();
        var entries = GetEntryList(_characterEntriesField!, inventory);
        var result = new List<MerchantItemForecast>();

        for (var index = 0; index < cardTypes.Length; index++)
        {
            var entry = new MerchantCardEntry(player, inventory, pool, cardTypes[index]);
            entries.Add(entry);
            result.Add(PredictCard(context, inventory, entry, shops, index == saleIndex));
        }

        return result;
    }

    private IReadOnlyList<MerchantItemForecast> PredictColorlessCards(
        object context,
        MerchantInventory inventory,
        Player player,
        Rng shops)
    {
        CardRarity[] rarities = [CardRarity.Uncommon, CardRarity.Rare];
        var pool = ModelDb.CardPool<ColorlessCardPool>()
            .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
            .ToArray();
        var entries = GetEntryList(_colorlessEntriesField!, inventory);
        var result = new List<MerchantItemForecast>();

        foreach (var rarity in rarities)
        {
            var entry = new MerchantCardEntry(player, inventory, pool, rarity);
            entries.Add(entry);
            result.Add(PredictCard(context, inventory, entry, shops, isOnSale: false));
        }

        return result;
    }

    private MerchantItemForecast PredictCard(
        object context,
        MerchantInventory inventory,
        MerchantCardEntry entry,
        Rng shops,
        bool isOnSale)
    {
        var shopsBefore = shops.ToSerializable();
        var tips = (IEnumerable<IHoverTip>?)_predictCardMethod!.Invoke(null, [context, entry, inventory])
                   ?? throw new InvalidOperationException("Random Foreseer returned no merchant card prediction.");
        var materializedTips = tips.ToArray();
        var card = materializedTips.OfType<CardHoverTip>().SingleOrDefault()?.Card
                   ?? throw new InvalidOperationException("Random Foreseer returned no predicted merchant card.");

        _creationResultProperty!.SetValue(entry, new CardCreationResult(card));

        int cost;
        if (isOnSale)
        {
            // Vanilla SetOnSale recalculates the card cost, consuming a second Shops roll.
            _isOnSaleProperty!.SetValue(entry, true);
            cost = Mathf.RoundToInt(BaseCardCost(card) * shops.NextFloat(0.95f, 1.05f)) / 2;
            cost = ApplyPrice(player: card.Owner!, entry, cost);
        }
        else if (!TryReadCost(materializedTips, out cost))
        {
            var costRng = new Rng(shopsBefore);
            _ = costRng.NextInt(1);
            cost = Mathf.RoundToInt(BaseCardCost(card) * costRng.NextFloat(0.95f, 1.05f));
            cost = ApplyPrice(player: card.Owner!, entry, cost);
        }

        return new MerchantItemForecast(card.Id, card.Title, cost, isOnSale);
    }

    private IReadOnlyList<MerchantItemForecast> PredictRelics(
        Player player,
        Rng rewards,
        Rng shops,
        MegaCrit.Sts2.Core.Runs.RelicGrabBag relicGrabBag,
        MegaCrit.Sts2.Core.Runs.RelicGrabBag sharedRelicGrabBag)
    {
        RelicRarity[] rarities =
        [
            RelicFactory.RollRarity(rewards),
            RelicFactory.RollRarity(rewards),
            RelicRarity.Shop
        ];
        var priceProbe = new ForecastMerchantEntry(player);
        var result = new List<MerchantItemForecast>();

        foreach (var rarity in rarities)
        {
            var relic = relicGrabBag.PullFromBack(
                            rarity,
                            candidate => candidate.IsAllowedInShops,
                            player.RunState)
                        ?? RelicFactory.FallbackRelic;
            sharedRelicGrabBag.Remove(relic);
            var cost = (int)Math.Round(relic.MerchantCost * shops.NextFloat(0.85f, 1.15f));
            cost = ApplyPrice(player, priceProbe, cost);
            result.Add(new MerchantItemForecast(relic.Id, relic.Title.GetFormattedText(), cost));
        }

        return result;
    }

    private IReadOnlyList<MerchantItemForecast> PredictPotions(Player player, Rng shops)
    {
        var priceProbe = new ForecastMerchantEntry(player);
        var potions = PotionFactory.CreateRandomPotionsOutOfCombat(player, 3, shops).ToArray();
        var result = new List<MerchantItemForecast>();
        foreach (var potion in potions)
        {
            var baseCost = potion.Rarity switch
            {
                PotionRarity.Rare => 100,
                PotionRarity.Uncommon => 75,
                _ => 50
            };
            var cost = Mathf.RoundToInt(baseCost * shops.NextFloat(0.95f, 1.05f));
            cost = ApplyPrice(player, priceProbe, cost);
            result.Add(new MerchantItemForecast(potion.Id, potion.Title.GetFormattedText(), cost));
        }

        return result;
    }

    private int PredictRemovalCost(Player player)
    {
        var entry = new MerchantCardRemovalEntry(player);
        var rawCost = (int)(_rawCostField!.GetValue(entry)
                            ?? throw new InvalidOperationException("Could not read the predicted removal cost."));
        return ApplyPrice(player, entry, rawCost);
    }

    private int ApplyPrice(Player player, MerchantEntry entry, int originalCost)
    {
        var value = _modifyMerchantPriceMethod!.Invoke(
            null,
            [player.RunState, player, entry, (decimal)originalCost]);
        return (int)(decimal)(value ?? originalCost);
    }

    private static List<MerchantCardEntry> GetEntryList(FieldInfo field, MerchantInventory inventory) =>
        (List<MerchantCardEntry>?)field.GetValue(inventory)
        ?? throw new MissingFieldException(typeof(MerchantInventory).FullName, field.Name);

    private static Rng GetRng(object rngSet, string name) =>
        (Rng?)rngSet.GetType().GetProperty(name, AnyInstance)?.GetValue(rngSet)
        ?? throw new MissingMemberException(rngSet.GetType().FullName, name);

    private static float BaseCardCost(CardModel card)
    {
        var cost = card.Rarity switch
        {
            CardRarity.Rare => 150,
            CardRarity.Uncommon => 75,
            _ => 50
        };
        return card.Pool is ColorlessCardPool ? Mathf.RoundToInt(cost * 1.15f) : cost;
    }

    private static bool TryReadCost(IEnumerable<IHoverTip> tips, out int cost)
    {
        foreach (var tip in tips.OfType<HoverTip>())
        {
            var match = BlueNumberRegex().Match(tip.Description);
            if (match.Success && int.TryParse(match.Groups[1].Value, out cost))
                return true;
        }

        cost = 0;
        return false;
    }

    private static string GetVersion(Assembly assembly)
    {
        var metadataVersion = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RandomForeseerModVersion")
            ?.Value;
        return metadataVersion
               ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString(3)
               ?? "unknown";
    }

    [GeneratedRegex(@"\[blue\]\s*(-?\d+)\s*\[/blue\]", RegexOptions.CultureInvariant)]
    private static partial Regex BlueNumberRegex();

    private sealed class ForecastMerchantEntry(Player player) : MerchantEntry(player)
    {
        public override bool IsStocked => true;

        public override void CalcCost()
        {
        }

        protected override Task<(bool, int)> OnTryPurchase(MerchantInventory? inventory, bool ignoreCost) =>
            Task.FromResult((false, 0));

        protected override void ClearAfterPurchase()
        {
        }

        protected override void RestockAfterPurchase(MerchantInventory? inventory)
        {
        }
    }
}
