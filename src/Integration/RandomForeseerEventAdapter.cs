using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.Integration;

internal sealed partial class RandomForeseerAdapter
{
    private const PredictionDependency EventContentDependencies =
        PredictionDependency.EventState
        | PredictionDependency.Rewards
        | PredictionDependency.RelicGrabBag
        | PredictionDependency.CardRarityOdds
        | PredictionDependency.Niche
        | PredictionDependency.Transformations
        | PredictionDependency.PlayerState;

    private static readonly Lazy<EventBridge> EventPredictionBridge = new(CreateEventBridge);
    private static readonly MethodInfo GenerateInitialEventOptionsMethod = typeof(EventModel)
        .GetMethods(AnyInstance)
        .Single(method => method.Name == "GenerateInitialOptionsWrapper"
                          && method.GetParameters().Length == 0);
    private static readonly MethodInfo SetEventStateMethod = typeof(EventModel)
        .GetMethods(AnyInstance)
        .Single(method => method.Name == "SetEventState"
                          && method.GetParameters().Length == 2);
    private static readonly FieldInfo? EventOptionHoverTipsField = typeof(EventOption)
        .GetField("<HoverTips>k__BackingField", AnyInstance);

    public Forecast<EventContentDetails>? PredictEventContents(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        EventModel eventModel)
    {
        if (resolvedRooms.Count == 0 || resolvedRooms[^1] != RoomType.Event)
            throw new ArgumentException("The resolved route must end in an event room.", nameof(resolvedRooms));

        var bridge = EventPredictionBridge.Value;
        if (!SupportsRouteRewards || !bridge.IsAvailable)
            return null;

        try
        {
            var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = RunState.FromSerializable(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(player.NetId)
                               ?? throw new InvalidOperationException(
                                   $"Could not find player {player.NetId} in the prediction snapshot.");

            var routeState = CreateRouteRewardState(shadowPlayer);
            AdvanceRoomsBeforeTarget(routeState, resolvedRooms);
            ApplyRouteStateToShadowPlayer(routeState, shadowRun);

            var details = PredictInitialEventContents(bridge, shadowPlayer, eventModel);
            if (details.Options.Count == 0)
                return null;

            return resolvedRooms.Count == 1
                ? Forecast<EventContentDetails>.CurrentWorldline(
                    details,
                    EventContentDependencies,
                    "Uses a serialized shadow run plus Random Foreseer's event option predictors; live RNG is never advanced.")
                : Forecast<EventContentDetails>.Branch(
                    details,
                    EventContentDependencies,
                    "Advances cloned reward state through the selected route. Earlier event choices, reward picks, purchases, rest actions, and other player-state changes remain conditional.");
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            return Forecast<EventContentDetails>.Unsupported(
                $"Random Foreseer adapter rejected the future event state: {root.Message}",
                EventContentDependencies);
        }
    }

    private void ApplyRouteStateToShadowPlayer(RouteRewardState state, RunState shadowRun)
    {
        var playerSave = state.Player.ToSerializable();
        playerSave.Rng.Rngs[PlayerRngType.Rewards] = state.Rewards.ToSerializable();
        playerSave.Rng.Rngs[PlayerRngType.Shops] = state.Shops.ToSerializable();
        playerSave.RelicGrabBag = state.PlayerRelicGrabBag.ToSerializable();

        var cardRarityOdds = _contextType?.GetProperty("CardRarityOdds", AnyInstance)?.GetValue(state.Context)
                             ?? throw new MissingMemberException(_contextType?.FullName, "CardRarityOdds");
        playerSave.Odds.CardRarityOddsValue = (float)(cardRarityOdds.GetType()
            .GetProperty("CurrentValue", AnyInstance)?.GetValue(cardRarityOdds)
            ?? throw new MissingMemberException(cardRarityOdds.GetType().FullName, "CurrentValue"));
        playerSave.Odds.PotionRewardOddsValue = state.PotionRewardOdds.CurrentValue;

        var simulatedDeck = _contextType?.GetProperty("Deck", AnyInstance)?.GetValue(state.Context)
                            ?? throw new MissingMemberException(_contextType?.FullName, "Deck");
        var predictedCards = (IEnumerable?)(simulatedDeck.GetType()
            .GetProperty("Cards", AnyInstance)?.GetValue(simulatedDeck))
                             ?? throw new MissingMemberException(simulatedDeck.GetType().FullName, "Cards");
        playerSave.Deck = predictedCards.Cast<object>()
            .Select(predicted => (CardModel?)(predicted.GetType()
                .GetProperty("Preview", AnyInstance)?.GetValue(predicted)))
            .OfType<CardModel>()
            .Select(card => card.ToSerializable())
            .ToList();

        state.Player.SyncWithSerializedPlayer(playerSave);
        shadowRun.SharedRelicGrabBag.LoadFromSerializable(state.SharedRelicGrabBag.ToSerializable());

        var sharedRng = _contextType?.GetProperty("SharedRng", AnyInstance)?.GetValue(state.Context)
                        ?? throw new MissingMemberException(_contextType?.FullName, "SharedRng");
        var niche = (MegaCrit.Sts2.Core.Random.Rng?)(sharedRng.GetType()
            .GetProperty("Niche", AnyInstance)?.GetValue(sharedRng))
                    ?? throw new MissingMemberException(sharedRng.GetType().FullName, "Niche");
        shadowRun.Rng.Niche.LoadFromSerializable(niche.ToSerializable());
    }

    private static EventContentDetails PredictInitialEventContents(
        EventBridge bridge,
        Player shadowPlayer,
        EventModel source)
    {
        var mutable = source.IsMutable
            ? (EventModel)source.ClonePreservingMutability()
            : source.ToMutable();
        mutable.Owner = shadowPlayer;
        var playerSlot = mutable.IsShared ? 0 : shadowPlayer.RunState.GetPlayerSlotIndex(shadowPlayer);
        mutable.Rng = new MegaCrit.Sts2.Core.Random.Rng(
            (ulong)((long)shadowPlayer.RunState.Rng.Seed + playerSlot)
            + StringHelper.GetDeterministicHashCode(mutable.Id.Entry));
        mutable.CalculateVars();
        var options = (IReadOnlyList<EventOption>?)GenerateInitialEventOptionsMethod.Invoke(mutable, null)
                      ?? throw new InvalidOperationException(
                          $"Event {mutable.Id} returned no initial options.");
        SetEventStateMethod.Invoke(mutable, [mutable.InitialDescription, options]);

        var predictions = new List<EventOptionPredictionDetails>();
        foreach (var option in options.Where(candidate => !candidate.IsLocked))
        {
            var initialItems = BuildInitialOptionItems(bridge, mutable, option);
            var sets = bridge.Supports(mutable.GetType())
                       && bridge.TryPredict(mutable, option, out var tips)
                ? BuildPredictionSets(tips)
                : [];
            if (initialItems.Count == 0 && sets.Count == 0)
                continue;
            var optionTitle = NormalizeText(option.Title.GetFormattedText());
            if (string.IsNullOrWhiteSpace(optionTitle))
                optionTitle = option.TextKey.Split('.').Last();
            predictions.Add(new EventOptionPredictionDetails(
                optionTitle,
                option.TextKey,
                initialItems,
                sets));
        }

        return new EventContentDetails(predictions);
    }

    private static IReadOnlyList<ForecastItemDetails> BuildInitialOptionItems(
        EventBridge bridge,
        EventModel eventModel,
        EventOption option)
    {
        var items = new List<ForecastItemDetails>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (bridge.GetRelicForOption(eventModel, option) is { } optionRelic)
            AddItem(ForecastItemDetails.Relic(optionRelic));

        var nativeTips = EventOptionHoverTipsField?.GetValue(option) as IEnumerable<IHoverTip>
                         ?? option.HoverTips.Where(static tip => !IsRandomForeseerPredictionTip(tip));
        foreach (var tip in nativeTips)
        {
            switch (tip)
            {
                case CardHoverTip cardTip:
                    AddItem(ForecastItemDetails.Card(cardTip.Card));
                    break;
                case { CanonicalModel: CardModel card }:
                    AddItem(ForecastItemDetails.Card(card));
                    break;
                case { CanonicalModel: RelicModel relic }:
                    AddItem(ForecastItemDetails.Relic(relic));
                    break;
                case { CanonicalModel: PotionModel potion }:
                    AddItem(ForecastItemDetails.Potion(potion));
                    break;
                case { CanonicalModel: OrbModel orb }:
                    AddItem(ForecastItemDetails.Orb(orb));
                    break;
            }
        }

        return items;

        void AddItem(ForecastItemDetails item)
        {
            var normalized = NormalizeText(item.Name);
            var key = $"{item.Kind}:{item.Id}:{normalized}";
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(key))
                items.Add(item with { Name = normalized });
        }
    }

    private static bool IsRandomForeseerPredictionTip(IHoverTip tip) =>
        tip.Id.StartsWith("RandomForeseer:Prediction", StringComparison.Ordinal)
        || tip.GetType().Assembly == typeof(global::RandomForeseer.RandomForeseerCode.Entry).Assembly;

    private static IReadOnlyList<EventPredictionSetDetails> BuildPredictionSets(
        IReadOnlyList<IHoverTip> tips)
    {
        var sets = new List<EventPredictionSetDetails>();
        var looseItems = new List<ForecastItemDetails>();

        foreach (var tip in tips)
        {
            var bundleCards = tip.GetType().GetProperty("Cards", AnyInstance)?.GetValue(tip)
                              as IEnumerable<CardModel>;
            if (bundleCards is not null)
            {
                FlushLooseItems();
                var items = bundleCards.Select(ForecastItemDetails.Card).ToArray();
                if (items.Length > 0)
                    sets.Add(new EventPredictionSetDetails(items));
                continue;
            }

            switch (tip)
            {
                case CardHoverTip cardTip:
                    looseItems.Add(ForecastItemDetails.Card(cardTip.Card));
                    break;
                case { CanonicalModel: CardModel card }:
                    looseItems.Add(ForecastItemDetails.Card(card));
                    break;
                case { CanonicalModel: RelicModel relic }:
                    looseItems.Add(ForecastItemDetails.Relic(relic));
                    break;
                case { CanonicalModel: PotionModel potion }:
                    looseItems.Add(ForecastItemDetails.Potion(potion));
                    break;
                case { CanonicalModel: OrbModel orb }:
                    looseItems.Add(ForecastItemDetails.Orb(orb));
                    break;
                case HoverTip textTip:
                {
                    if (TryResolveDetachedModelTip(textTip) is { } modelItem)
                    {
                        looseItems.Add(modelItem);
                        break;
                    }
                    var text = NormalizeText(textTip.Title ?? textTip.Description);
                    if (!string.IsNullOrWhiteSpace(text))
                        looseItems.Add(ForecastItemDetails.Text(text));
                    break;
                }
            }
        }

        FlushLooseItems();
        return sets;

        void FlushLooseItems()
        {
            if (looseItems.Count == 0)
                return;
            sets.Add(new EventPredictionSetDetails(looseItems.ToArray()));
            looseItems.Clear();
        }
    }

    private static ForecastItemDetails? TryResolveDetachedModelTip(HoverTip tip)
    {
        if (!tip.Id.StartsWith("RandomForeseer:Prediction:", StringComparison.Ordinal))
            return null;

        if (ExtractLocEntry(tip.Id, "relics") is { } relicEntry)
        {
            var relic = ModelDb.AllRelics.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.Entry, relicEntry, StringComparison.Ordinal));
            if (relic is not null)
                return ForecastItemDetails.Relic(relic);
        }

        if (ExtractLocEntry(tip.Id, "potions") is { } potionEntry)
        {
            var potion = ModelDb.AllPotions.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.Entry, potionEntry, StringComparison.Ordinal));
            if (potion is not null)
                return ForecastItemDetails.Potion(potion);
        }

        return null;
    }

    private static string? ExtractLocEntry(string id, string table)
    {
        var marker = $"Title={table}.";
        var start = id.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = id.IndexOf(".title", start, StringComparison.Ordinal);
        return end <= start ? null : id[start..end];
    }

    private static string NormalizeText(string text)
    {
        var normalized = string.Join(" ", text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 160 ? normalized : normalized[..157] + "…";
    }

    private static EventBridge CreateEventBridge()
    {
        var assembly = typeof(global::RandomForeseer.RandomForeseerCode.Entry).Assembly;
        var predictionType = assembly.GetType(
            "RandomForeseer.RandomForeseerCode.OutOfCombat.EventOptionPrediction");
        var registry = predictionType?.GetField("Registry", AnyStatic)?.GetValue(null);
        var registryType = registry?.GetType();
        var predictors = registryType?.GetField("_predictors", AnyInstance)?.GetValue(registry) as IDictionary;
        var tryPredict = registryType?.GetMethods(AnyInstance)
            .SingleOrDefault(method => method.Name == "TryPredict" && method.GetParameters().Length == 3);
        var getRelicForOption = predictionType?.GetMethod("GetRelicForOption", AnyStatic);
        return new EventBridge(registry, predictors, tryPredict, getRelicForOption);
    }

    private sealed class EventBridge(
        object? registry,
        IDictionary? predictors,
        MethodInfo? tryPredict,
        MethodInfo? getRelicForOption)
    {
        public bool IsAvailable => registry is not null && predictors is not null && tryPredict is not null;

        public bool Supports(Type eventType) => predictors?.Contains(eventType) == true;

        public bool TryPredict(
            EventModel eventModel,
            EventOption option,
            out IReadOnlyList<IHoverTip> tips)
        {
            if (!IsAvailable)
            {
                tips = [];
                return false;
            }

            object?[] arguments = [eventModel, option, null];
            var predicted = (bool)(tryPredict!.Invoke(registry, arguments) ?? false);
            tips = arguments[2] switch
            {
                IReadOnlyList<IHoverTip> list => list,
                IEnumerable<IHoverTip> enumerable => enumerable.ToArray(),
                _ => []
            };
            return predicted;
        }

        public RelicModel? GetRelicForOption(EventModel eventModel, EventOption option) =>
            getRelicForOption?.Invoke(null, [eventModel, option]) as RelicModel
            ?? option.Relic;
    }
}
