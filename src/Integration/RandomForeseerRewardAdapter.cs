using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
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

    public Forecast<CombatRewardDetails> PredictCombatRewards(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms,
        EncounterModel encounter)
    {
        if (resolvedRooms.Count == 0 || !resolvedRooms[^1].IsCombatRoom())
            throw new ArgumentException("The resolved route must end in a combat room.", nameof(resolvedRooms));

        if (!SupportsRouteRewards)
        {
            return Forecast<CombatRewardDetails>.Unsupported(
                "Random Foreseer does not expose the cloned reward-prediction signatures required by this adapter version.",
                CombatRewardDependencies);
        }

        try
        {
            var state = CreateRouteRewardState(player);
            if (state.HasUnsupportedRewardHook)
            {
                return Forecast<CombatRewardDetails>.Unsupported(
                    "An installed model or mod has an unmirrored combat-reward hook.",
                    CombatRewardDependencies);
            }

            AdvanceRoomsBeforeTarget(state, resolvedRooms);
            AdvanceCombatEnd(state);
            var details = GenerateCombatRewards(state, resolvedRooms[^1], encounter);
            return resolvedRooms.Count == 1
                ? Forecast<CombatRewardDetails>.CurrentWorldline(
                    details,
                    CombatRewardDependencies,
                    "Uses Random Foreseer's cloned Rewards state, card odds, relic bag, and hook mirrors.")
                : Forecast<CombatRewardDetails>.Branch(
                    details,
                    CombatRewardDependencies,
                    "Advances cloned combat, merchant, treasure-gold, card-odds, and relic-bag state along this route; intervening choices remain conditional.");
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            return Forecast<CombatRewardDetails>.Unsupported(
                $"Random Foreseer adapter rejected the combat-reward state: {root.Message}",
                CombatRewardDependencies);
        }
    }

    public Forecast<TreasureRoomDetails> PredictTreasureRoom(
        Player player,
        IReadOnlyList<RoomType> resolvedRooms)
    {
        if (resolvedRooms.Count == 0 || resolvedRooms[^1] != RoomType.Treasure)
            throw new ArgumentException("The resolved route must end in a treasure room.", nameof(resolvedRooms));

        if (!SupportsRouteRewards)
        {
            return Forecast<TreasureRoomDetails>.Unsupported(
                "Random Foreseer does not expose the cloned route-prediction signatures required by this adapter version.",
                TreasureDependencies);
        }

        try
        {
            var state = CreateRouteRewardState(player);
            if (state.HasUnsupportedRewardHook || state.HasUnsupportedTreasureHook)
            {
                return Forecast<TreasureRoomDetails>.Unsupported(
                    "An installed model or mod has an unmirrored reward or treasure-generation hook.",
                    TreasureDependencies);
            }

            AdvanceRoomsBeforeTarget(state, resolvedRooms);
            var details = GenerateTreasureRoom(state, isPriorRoom: false);
            return resolvedRooms.Count == 1 && player.RunState.Players.Count == 1
                ? Forecast<TreasureRoomDetails>.CurrentWorldline(
                    details,
                    TreasureDependencies,
                    "Uses cloned TreasureRoomRelics RNG, shared relic bag, Rewards RNG, and current treasure hooks.")
                : Forecast<TreasureRoomDetails>.Branch(
                    details,
                    TreasureDependencies,
                    "Advances cloned room rewards, shops, treasure RNG, and relic bags along this route; skipped relics and multiplayer voting remain conditional.");
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            return Forecast<TreasureRoomDetails>.Unsupported(
                $"Random Foreseer adapter rejected the treasure-room state: {root.Message}",
                TreasureDependencies);
        }
    }

    internal RouteRewardState CreateRouteRewardStateForPlan(Player player) => CreateRouteRewardState(player);

    /// <summary>Advances the cloned state through ONE planned room (rewards
    /// RNG consumed exactly as the game would at that room's end).</summary>
    internal void AdvanceSingleRoomForPlan(RouteRewardState state, RoomType roomType)
    {
        switch (roomType)
        {
            case RoomType.Monster:
            case RoomType.Elite:
            case RoomType.Boss:
                AdvanceCombatEnd(state);
                _ = GenerateCombatRewards(state, roomType, encounter: null);
                break;
            case RoomType.Shop:
                state.MerchantVisits++;
                _ = PredictMerchantVisit(
                    state.Context,
                    state.Player,
                    state.Rewards,
                    state.Shops,
                    state.PlayerRelicGrabBag,
                    state.SharedRelicGrabBag,
                    state.MerchantVisits);
                break;
            case RoomType.Treasure:
                _ = GenerateTreasureRoom(state, isPriorRoom: true);
                break;
        }
    }

    internal CombatRewardDetails GenerateCombatRewardsForPlan(RouteRewardState state, RoomType roomType) =>
        GenerateCombatRewards(state, roomType, encounter: null);

    internal TreasureRoomDetails GenerateTreasureRoomForPlan(RouteRewardState state) =>
        GenerateTreasureRoom(state, isPriorRoom: false);

    internal MerchantInventoryForecast PredictMerchantVisitForPlan(RouteRewardState state)
    {
        state.MerchantVisits++;
        return PredictMerchantVisit(
            state.Context,
            state.Player,
            state.Rewards,
            state.Shops,
            state.PlayerRelicGrabBag,
            state.SharedRelicGrabBag,
            state.MerchantVisits);
    }

    /// <summary>Removes a taken relic from both cloned bags so downstream
    /// shops, elites, and chests stop offering it.</summary>
    internal void RemoveRelicForPlan(RouteRewardState state, RelicModel relic)
    {
        state.PlayerRelicGrabBag.Remove(relic);
        state.SharedRelicGrabBag.Remove(relic);
    }

    private RouteRewardState CreateRouteRewardState(Player player)
    {
        var context = _contextConstructor!.Invoke([player]);
        var rngSet = _contextRngProperty!.GetValue(context)
                     ?? throw new MissingMemberException(_contextType!.FullName, "Rng");
        var rewards = GetRng(rngSet, "Rewards");
        var shops = GetRng(rngSet, "Shops");
        var relicGrabBag = (RelicGrabBag?)_contextRelicGrabBagProperty!.GetValue(context)
                           ?? throw new MissingMemberException(_contextType!.FullName, "RelicGrabBag");
        var potionRewardOdds = (PotionRewardOdds?)_contextPotionRewardOddsProperty!.GetValue(context)
                               ?? throw new MissingMemberException(_contextType!.FullName, "PotionRewardOdds");
        var listeners = player.RunState.IterateHookListeners(null).ToArray();
        return new RouteRewardState(
            player,
            context,
            rewards,
            shops,
            relicGrabBag,
            RelicGrabBag.FromSerializable(player.RunState.SharedRelicGrabBag.ToSerializable()),
            new Rng(player.RunState.Rng.TreasureRoomRelics.ToSerializable()),
            potionRewardOdds,
            listeners,
            HasUnsupportedOverride(listeners, nameof(AbstractModel.TryModifyRewards), RewardHookTypes),
            HasUnsupportedOverride(listeners, nameof(AbstractModel.TryModifyRewardsLate), RewardHookTypes),
            HasUnsupportedOverride(listeners, nameof(AbstractModel.ShouldGenerateTreasure), TreasureHookTypes));
    }

    internal void AdvanceRoomsBeforeTargetForPlan(RouteRewardState state, IReadOnlyList<RoomType> resolvedRooms) => AdvanceRoomsBeforeTarget(state, resolvedRooms);

    private void AdvanceRoomsBeforeTarget(RouteRewardState state, IReadOnlyList<RoomType> resolvedRooms)
    {
        for (var index = 0; index < resolvedRooms.Count - 1; index++)
        {
            var roomType = resolvedRooms[index];
            switch (roomType)
            {
                case RoomType.Monster:
                case RoomType.Elite:
                case RoomType.Boss:
                    AdvanceCombatEnd(state);
                    _ = GenerateCombatRewards(state, roomType, encounter: null);
                    break;
                case RoomType.Shop:
                    state.MerchantVisits++;
                    _ = PredictMerchantVisit(
                        state.Context,
                        state.Player,
                        state.Rewards,
                        state.Shops,
                        state.PlayerRelicGrabBag,
                        state.SharedRelicGrabBag,
                        state.MerchantVisits);
                    break;
                case RoomType.Treasure:
                    _ = GenerateTreasureRoom(state, isPriorRoom: true);
                    break;
            }
        }
    }

    private void AdvanceCombatEnd(RouteRewardState state)
    {
        _fastForwardCombatEndMethod!.Invoke(null, [state.Context]);
        state.CombatsAdvanced++;
    }

    private CombatRewardDetails GenerateCombatRewards(
        RouteRewardState state,
        RoomType roomType,
        EncounterModel? encounter)
    {
        var slots = new List<PredictedRewardSlot>();
        var finalBossWithoutRewards = roomType == RoomType.Boss
                                      && state.Player.RunState.CurrentActIndex
                                      >= state.Player.RunState.Acts.Count - 1;
        if (!finalBossWithoutRewards && (encounter?.ShouldGiveRewards ?? true))
        {
            var shouldAddPotion = state.PotionRewardOdds.Roll(state.Player, roomType);
            var gold = encounter is null
                ? state.Rewards.NextInt(1)
                : state.Rewards.NextInt(encounter.MinGoldReward, encounter.MaxGoldReward + 1);
            slots.Add(PredictedRewardSlot.GoldReward(gold));

            if (shouldAddPotion)
            {
                var potion = PotionFactory.CreateRandomPotionOutOfCombat(state.Player, state.Rewards);
                slots.Add(PredictedRewardSlot.PotionReward(ToDetails(potion)));
            }

            var cards = PredictCardReward(state, roomType, isFromCombat: true);
            slots.Add(PredictedRewardSlot.CardReward(cards, populated: true));

            if (roomType == RoomType.Elite)
                slots.Add(PredictedRewardSlot.RelicReward(PullPlayerRelic(state)));
        }

        ApplyEarlyRewardHooks(state, roomType, finalBossWithoutRewards, slots);
        ApplyLateRewardHooks(state, roomType, slots);
        PopulateAddedRewards(state, slots);

        return new CombatRewardDetails(
            slots.Where(slot => slot.Kind == PredictedRewardKind.Gold).Sum(slot => slot.Gold),
            slots.Where(slot => slot.Kind == PredictedRewardKind.Cards)
                .Select(slot => (IReadOnlyList<RewardItemDetails>)slot.Items.ToArray())
                .ToArray(),
            slots.Where(slot => slot.Kind == PredictedRewardKind.Potion)
                .SelectMany(slot => slot.Items)
                .ToArray(),
            slots.Where(slot => slot.Kind == PredictedRewardKind.Relic)
                .SelectMany(slot => slot.Items)
                .ToArray());
    }

    private void ApplyEarlyRewardHooks(
        RouteRewardState state,
        RoomType roomType,
        bool finalBossWithoutRewards,
        List<PredictedRewardSlot> slots)
    {
        foreach (var listener in state.Listeners)
        {
            switch (listener)
            {
                case AmethystAubergine aubergine
                    when ReferenceEquals(aubergine.Owner, state.Player) && !finalBossWithoutRewards:
                    slots.Add(PredictedRewardSlot.GoldReward(aubergine.DynamicVars.Gold.IntValue));
                    break;
                case BlackStar blackStar
                    when ReferenceEquals(blackStar.Owner, state.Player) && roomType == RoomType.Elite:
                    slots.Add(PredictedRewardSlot.UnpopulatedRelicReward());
                    break;
                case LavaRock lavaRock
                    when ReferenceEquals(lavaRock.Owner, state.Player)
                         && roomType == RoomType.Boss
                         && state.Player.RunState.CurrentActIndex == 0
                         && !lavaRock.HasTriggered
                         && !state.LavaRockTriggered:
                    for (var index = 0; index < lavaRock.DynamicVars["Relics"].IntValue; index++)
                        slots.Add(PredictedRewardSlot.UnpopulatedRelicReward());
                    state.LavaRockTriggered = true;
                    break;
                case PrayerWheel prayerWheel
                    when ReferenceEquals(prayerWheel.Owner, state.Player) && roomType == RoomType.Monster:
                    slots.Add(PredictedRewardSlot.UnpopulatedCardReward(RoomType.Monster));
                    break;
                case WhiteStar whiteStar
                    when ReferenceEquals(whiteStar.Owner, state.Player) && roomType == RoomType.Elite:
                    slots.Add(PredictedRewardSlot.UnpopulatedCardReward(RoomType.Boss));
                    break;
                case WongosMysteryTicket ticket
                    when ReferenceEquals(ticket.Owner, state.Player)
                         && !ticket.GaveRelic
                         && !state.WongosTicketTriggered
                         && ticket.CombatsFinished + state.CombatsAdvanced >= WongosMysteryTicket.combatsToActivate:
                    for (var index = 0; index < WongosMysteryTicket.relicCount; index++)
                        slots.Add(PredictedRewardSlot.UnpopulatedRelicReward());
                    state.WongosTicketTriggered = true;
                    break;
            }
        }
    }

    private static void ApplyLateRewardHooks(
        RouteRewardState state,
        RoomType roomType,
        List<PredictedRewardSlot> slots)
    {
        foreach (var listener in state.Listeners)
        {
            switch (listener)
            {
                case Midas:
                    foreach (var gold in slots.Where(slot => slot.Kind == PredictedRewardKind.Gold))
                        gold.Gold *= 2;
                    break;
                case Vintage when roomType == RoomType.Monster:
                    for (var index = 0; index < slots.Count; index++)
                    {
                        if (slots[index].Kind == PredictedRewardKind.Cards)
                            slots[index] = PredictedRewardSlot.UnpopulatedRelicReward();
                    }
                    break;
            }
        }
    }

    private void PopulateAddedRewards(RouteRewardState state, IEnumerable<PredictedRewardSlot> slots)
    {
        foreach (var slot in slots.Where(slot => !slot.IsPopulated))
        {
            switch (slot.Kind)
            {
                case PredictedRewardKind.Cards:
                    slot.Items.AddRange(PredictCardReward(state, slot.CardRoomType, isFromCombat: false));
                    break;
                case PredictedRewardKind.Relic:
                    slot.Items.Add(PullPlayerRelic(state));
                    break;
            }
            slot.IsPopulated = true;
        }
    }

    private IReadOnlyList<RewardItemDetails> PredictCardReward(
        RouteRewardState state,
        RoomType roomType,
        bool isFromCombat)
    {
        var flags = CardCreationFlags.IsCardReward;
        if (isFromCombat)
            flags |= CardCreationFlags.IsFromCombat;
        var options = CardCreationOptions.ForRoom(state.Player, roomType).WithFlags(flags);
        var value = _predictCardsMethod!.Invoke(null, [state.Context, 3, options, null, null]);
        var cards = value as IEnumerable<CardModel>
                    ?? throw new InvalidOperationException("Random Foreseer returned no predicted combat cards.");
        return cards.Select(ToDetails).ToArray();
    }

    private static RewardItemDetails PullPlayerRelic(RouteRewardState state)
    {
        var rarity = RelicFactory.RollRarity(state.Rewards);
        var relic = state.PlayerRelicGrabBag.PullFromFront(rarity, state.Player.RunState)
                    ?? RelicFactory.FallbackRelic;
        state.SharedRelicGrabBag.Remove(relic);
        return ToDetails(relic);
    }

    private static TreasureRoomDetails GenerateTreasureRoom(RouteRewardState state, bool isPriorRoom)
    {
        state.TreasureRoomsAdvanced++;
        var relics = new List<RewardItemDetails>();
        var localReceivesTreasure = false;
        foreach (var runPlayer in state.Player.RunState.Players)
        {
            if (!ShouldGenerateTreasure(state, runPlayer))
                continue;

            var rarity = RelicFactory.RollRarity(state.TreasureRoomRelics);
            var relic = TryGetTutorialTreasureRelic(state, runPlayer)
                        ?? state.SharedRelicGrabBag.PullFromFront(rarity, state.Player.RunState)
                        ?? RelicFactory.FallbackRelic;
            relics.Add(ToDetails(relic));
            localReceivesTreasure |= ReferenceEquals(runPlayer, state.Player);
        }

        var gold = 0;
        if (localReceivesTreasure)
        {
            gold = state.Rewards.NextInt(42, 53);
            if (state.Player.RunState.AscensionLevel >= (int)AscensionLevel.Poverty)
                gold = (int)(gold * 0.75);
        }

        if (isPriorRoom
            && state.Player.RunState.Players.Count == 1
            && relics.Count == 1)
        {
            // A single-player chest has one deterministic offered relic. Continuing the
            // route assumes it was taken, so later elite/shop pulls use the same bag state.
            state.PlayerRelicGrabBag.Remove(ModelDb.GetById<RelicModel>(relics[0].Id));
        }

        return new TreasureRoomDetails(gold, relics, relics.Count == 0);
    }

    private static bool ShouldGenerateTreasure(RouteRewardState state, Player player)
    {
        foreach (var listener in state.Listeners)
        {
            if (listener is SilverCrucible crucible
                && ReferenceEquals(crucible.Owner, player)
                && crucible.TreasureRoomsEntered + state.TreasureRoomsAdvanced <= 1)
            {
                return false;
            }
        }
        return true;
    }

    private static RelicModel? TryGetTutorialTreasureRelic(RouteRewardState state, Player player)
    {
        if (state.Player.RunState.Players.Count != 1
            || !ReferenceEquals(player, state.Player)
            || player.UnlockState.NumberOfRuns != 0
            || state.HistoricalTreasureRooms + state.TreasureRoomsAdvanced != 1)
        {
            return null;
        }

        var gorget = ModelDb.Relic<Gorget>();
        if (!state.PlayerRelicGrabBag.Contains(gorget))
            return null;
        state.PlayerRelicGrabBag.Remove(gorget);
        state.SharedRelicGrabBag.Remove(gorget);
        return gorget;
    }

    private static bool HasUnsupportedOverride(
        IEnumerable<AbstractModel> listeners,
        string methodName,
        IReadOnlySet<Type> supportedTypes) => listeners.Any(listener =>
    {
        var method = listener.GetType().GetMethod(methodName, AnyInstance);
        return method?.DeclaringType != typeof(AbstractModel)
               && !supportedTypes.Contains(listener.GetType());
    });

    private static RewardItemDetails ToDetails(CardModel card) =>
        new(ForecastItemDetails.Card(card));

    private static RewardItemDetails ToDetails(PotionModel potion) =>
        new(ForecastItemDetails.Potion(potion));

    private static RewardItemDetails ToDetails(RelicModel relic) =>
        new(ForecastItemDetails.Relic(relic));

    private static readonly HashSet<Type> RewardHookTypes =
    [
        typeof(AmethystAubergine),
        typeof(BlackStar),
        typeof(Driftwood),
        typeof(LavaRock),
        typeof(PrayerWheel),
        typeof(WhiteStar),
        typeof(WongosMysteryTicket),
        typeof(Midas),
        typeof(Vintage)
    ];

    private static readonly HashSet<Type> TreasureHookTypes = [typeof(SilverCrucible)];

    internal sealed class RouteRewardState(
        Player player,
        object context,
        Rng rewards,
        Rng shops,
        RelicGrabBag playerRelicGrabBag,
        RelicGrabBag sharedRelicGrabBag,
        Rng treasureRoomRelics,
        PotionRewardOdds potionRewardOdds,
        IReadOnlyList<AbstractModel> listeners,
        bool unsupportedEarlyRewardHook,
        bool unsupportedLateRewardHook,
        bool unsupportedTreasureHook)
    {
        public Player Player { get; } = player;
        public object Context { get; } = context;
        public Rng Rewards { get; } = rewards;
        public Rng Shops { get; } = shops;
        public RelicGrabBag PlayerRelicGrabBag { get; } = playerRelicGrabBag;
        public RelicGrabBag SharedRelicGrabBag { get; } = sharedRelicGrabBag;
        public Rng TreasureRoomRelics { get; } = treasureRoomRelics;
        public PotionRewardOdds PotionRewardOdds { get; } = potionRewardOdds;
        public IReadOnlyList<AbstractModel> Listeners { get; } = listeners;
        public bool HasUnsupportedRewardHook { get; } = unsupportedEarlyRewardHook || unsupportedLateRewardHook;
        public bool HasUnsupportedTreasureHook { get; } = unsupportedTreasureHook;
        public int HistoricalTreasureRooms { get; } = player.RunState.MapPointHistory
            .SelectMany(history => history)
            .Count(entry => entry.HasRoomOfType(RoomType.Treasure));
        public int MerchantVisits { get; set; }
        public int CombatsAdvanced { get; set; }
        public int TreasureRoomsAdvanced { get; set; }
        public bool LavaRockTriggered { get; set; }
        public bool WongosTicketTriggered { get; set; }
    }

    private enum PredictedRewardKind
    {
        Gold,
        Cards,
        Potion,
        Relic
    }

    private sealed class PredictedRewardSlot
    {
        public required PredictedRewardKind Kind { get; init; }
        public int Gold { get; set; }
        public List<RewardItemDetails> Items { get; } = [];
        public RoomType CardRoomType { get; init; }
        public bool IsPopulated { get; set; }

        public static PredictedRewardSlot GoldReward(int amount) => new()
        {
            Kind = PredictedRewardKind.Gold,
            Gold = amount,
            IsPopulated = true
        };

        public static PredictedRewardSlot CardReward(
            IEnumerable<RewardItemDetails> cards,
            bool populated)
        {
            var slot = new PredictedRewardSlot
            {
                Kind = PredictedRewardKind.Cards,
                IsPopulated = populated
            };
            slot.Items.AddRange(cards);
            return slot;
        }

        public static PredictedRewardSlot UnpopulatedCardReward(RoomType roomType) => new()
        {
            Kind = PredictedRewardKind.Cards,
            CardRoomType = roomType
        };

        public static PredictedRewardSlot PotionReward(RewardItemDetails potion) => new()
        {
            Kind = PredictedRewardKind.Potion,
            IsPopulated = true,
            Items = { potion }
        };

        public static PredictedRewardSlot RelicReward(RewardItemDetails relic) => new()
        {
            Kind = PredictedRewardKind.Relic,
            IsPopulated = true,
            Items = { relic }
        };

        public static PredictedRewardSlot UnpopulatedRelicReward() => new()
        {
            Kind = PredictedRewardKind.Relic
        };
    }
}
