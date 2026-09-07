using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SeedOracle.Api;

namespace SeedOracle.Forecasting;

/// <summary>
/// Native planning-only reward simulation. This state is deliberately
/// independent from Random Foreseer's prediction context: the shadow player,
/// its reward/shop RNGs, card-rarity odds, potion odds and relic bags are the
/// only state used to generate downstream plan results.
/// </summary>
internal sealed class PlanningPredictionService
{
    internal sealed class State
    {
        public required RunState Run { get; set; }
        public required Player Player { get; set; }
        public int MerchantVisits { get; set; }
        public int CombatsAdvanced { get; set; }
        public int TreasureRoomsAdvanced { get; set; }
        public bool LavaRockTriggered { get; set; }
        public bool WongosTicketTriggered { get; set; }
        public int HistoricalTreasureRooms { get; init; }
    }

    /// <summary>
    /// Serializable boundary for an event preview. The event executor can
    /// restore this without ever consulting the live run or a third-party
    /// prediction context.
    /// </summary>
    internal sealed record StateSnapshot(
        SerializableRun Run,
        ulong PlayerNetId,
        int MerchantVisits,
        int CombatsAdvanced,
        int TreasureRoomsAdvanced,
        bool LavaRockTriggered,
        bool WongosTicketTriggered,
        int HistoricalTreasureRooms);

    private const PredictionDependency CombatDependencies =
        PredictionDependency.Rewards
        | PredictionDependency.RelicGrabBag
        | PredictionDependency.CardRarityOdds
        | PredictionDependency.PlayerState;

    internal State CreateState(RunState liveRun, Player livePlayer)
    {
        var snapshot = CreateSerializableRun(liveRun);
        var shadowRun = RunState.FromSerializable(snapshot);
        var shadowPlayer = shadowRun.GetPlayer(livePlayer.NetId)
                           ?? throw new InvalidOperationException(
                               $"Planning shadow snapshot lacks player {livePlayer.NetId}.");
        return new State
        {
            Run = shadowRun,
            Player = shadowPlayer,
            HistoricalTreasureRooms = livePlayer.RunState.MapPointHistory
                .SelectMany(history => history)
                .Count(entry => entry.HasRoomOfType(RoomType.Treasure))
        };
    }

    internal StateSnapshot Capture(State state) => new(
        CreateSerializableRun(state.Run),
        state.Player.NetId,
        state.MerchantVisits,
        state.CombatsAdvanced,
        state.TreasureRoomsAdvanced,
        state.LavaRockTriggered,
        state.WongosTicketTriggered,
        state.HistoricalTreasureRooms);

    internal State Restore(StateSnapshot snapshot)
    {
        var run = RunState.FromSerializable(snapshot.Run);
        var player = run.GetPlayer(snapshot.PlayerNetId)
                     ?? throw new InvalidOperationException(
                         $"Planning snapshot lacks player {snapshot.PlayerNetId}.");
        return new State
        {
            Run = run,
            Player = player,
            MerchantVisits = snapshot.MerchantVisits,
            CombatsAdvanced = snapshot.CombatsAdvanced,
            TreasureRoomsAdvanced = snapshot.TreasureRoomsAdvanced,
            LavaRockTriggered = snapshot.LavaRockTriggered,
            WongosTicketTriggered = snapshot.WongosTicketTriggered,
            HistoricalTreasureRooms = snapshot.HistoricalTreasureRooms
        };
    }

    internal void ApplyEventState(State target, RunState eventRun, ulong playerNetId)
    {
        // Clone the executor graph at the hand-off boundary. Refreshing the
        // panel must never consume a cached event result's RNG or bags.
        var clonedRun = RunState.FromSerializable(CreateSerializableRun(eventRun));
        target.Run = clonedRun;
        target.Player = clonedRun.GetPlayer(playerNetId)
                        ?? throw new InvalidOperationException(
                            $"Event snapshot lacks player {playerNetId}.");
    }

    internal void AddCard(State state, ModelId cardId, bool upgraded)
    {
        var canonical = ModelDb.AllCards.FirstOrDefault(card => card.Id.Entry == cardId.Entry);
        if (canonical is null)
            return;

        var card = state.Run.CreateCard(canonical, state.Player);
        if (upgraded && !card.IsUpgraded)
            MegaCrit.Sts2.Core.Commands.CardCmd.Upgrade(card);
        state.Player.Deck.AddInternal(card, silent: true);
    }

    internal bool RemoveCard(State state, ModelId cardId, int slot = -1)
    {
        var card = slot >= 0
                   && slot < state.Player.Deck.Cards.Count
                   && state.Player.Deck.Cards[slot].Id.Entry == cardId.Entry
            ? state.Player.Deck.Cards[slot]
            : state.Player.Deck.Cards.FirstOrDefault(candidate => candidate.Id.Entry == cardId.Entry);
        if (card is null)
            return false;
        state.Player.Deck.RemoveInternal(card, silent: true);
        state.Run.RemoveCard(card);
        return true;
    }

    internal bool UpgradeCard(State state, ModelId cardId, int slot = -1)
    {
        var card = slot >= 0
                   && slot < state.Player.Deck.Cards.Count
                   && state.Player.Deck.Cards[slot].Id.Entry == cardId.Entry
            ? state.Player.Deck.Cards[slot]
            : state.Player.Deck.Cards.FirstOrDefault(candidate => candidate.Id.Entry == cardId.Entry);
        if (card is null || !card.IsUpgradable || card.IsUpgraded)
            return false;
        MegaCrit.Sts2.Core.Commands.CardCmd.Upgrade(card);
        return true;
    }

    internal bool AddPotion(State state, ModelId potionId, ModelId? discardId = null)
    {
        if (discardId is { } discard)
        {
            var existing = state.Player.PotionSlots
                .FirstOrDefault(potion => potion is not null
                    && PotionIdsMatch(potion.Id, discard));
            if (existing is not null)
                state.Player.DiscardPotionInternal(existing, silent: true);
        }

        if (!state.Player.HasOpenPotionSlots)
            return false;
        var canonical = ResolvePotion(potionId);
        if (canonical is null)
            return false;
        return state.Player.AddPotionInternal(canonical.ToMutable(), silent: true).success;
    }

    internal void SetPotionSlots(State state, IReadOnlyList<ModelId> slots)
    {
        var remaining = slots.ToList();
        foreach (var current in state.Player.Potions.ToList())
        {
            var index = remaining.FindIndex(slot => PotionIdsMatch(current.Id, slot));
            if (index >= 0)
            {
                remaining.RemoveAt(index);
                continue;
            }

            state.Player.DiscardPotionInternal(current, silent: true);
        }

        foreach (var id in remaining)
        {
            var canonical = ResolvePotion(id);
            if (canonical is null || !state.Player.HasOpenPotionSlots)
                continue;
            state.Player.AddPotionInternal(canonical.ToMutable(), silent: true);
        }
    }

    private static PotionModel? ResolvePotion(ModelId id)
    {
        var registered = ModelDb.GetByIdOrNull<PotionModel>(id);
        if (registered is not null)
            return registered;

        var potion = ModelDb.AllPotions.FirstOrDefault(candidate =>
            candidate.Id.Category.Equals(id.Category, StringComparison.OrdinalIgnoreCase)
            && candidate.Id.Entry.Equals(id.Entry, StringComparison.OrdinalIgnoreCase));
        if (potion is not null)
            return potion;

        // Some serialized saves from older builds retained only the entry or
        // used a category casing different from the current model registry.
        // Entry matching is safe here because potion entries are globally
        // unique, and prevents a valid slot from disappearing silently.
        return ModelDb.AllPotions.FirstOrDefault(candidate =>
            candidate.Id.Entry.Equals(id.Entry, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PotionIdsMatch(ModelId left, ModelId right) =>
        left.Category.Equals(right.Category, StringComparison.OrdinalIgnoreCase)
        && left.Entry.Equals(right.Entry, StringComparison.OrdinalIgnoreCase)
        || left.Entry.Equals(right.Entry, StringComparison.OrdinalIgnoreCase);

    internal static SerializableRun CreateSerializableRun(RunState source)
    {
        // RunState intentionally has no public ToSerializable method. Start
        // from the engine's save envelope, then replace every mutable section
        // with the supplied shadow state so RNG, odds, bags and inventories
        // remain on the same worldline.
        var save = RunManager.Instance.ToSave(preFinishedRoom: null);
        save.Acts = source.Acts.Select(act => act.ToSave()).ToList();
        save.CurrentActIndex = source.CurrentActIndex;
        save.EventsSeen = source.VisitedEventIds.ToList();
        save.SerializableOdds = source.Odds.ToSerializable();
        save.SerializableSharedRelicGrabBag = source.SharedRelicGrabBag.ToSerializable();
        save.Players = source.Players.Select(player => player.ToSerializable()).ToList();
        save.SerializableRng = source.Rng.ToSerializable();
        save.VisitedMapCoords = source.VisitedMapCoords.ToList();
        save.MapPointHistory = source.MapPointHistory.Select(history => history.ToList()).ToList();
        save.Ascension = source.AscensionLevel;
        save.GameMode = source.GameMode;
        save.ExtraFields = source.ExtraFields.ToSerializable();
        return save;
    }

    internal void AdvanceRoom(State state, RoomType roomType, EncounterModel? encounter)
    {
        switch (roomType)
        {
            case RoomType.Monster:
            case RoomType.Elite:
            case RoomType.Boss:
                state.CombatsAdvanced++;
                _ = GenerateCombatRewards(state, roomType, encounter);
                break;
            case RoomType.Shop:
                _ = GenerateMerchant(state);
                break;
            case RoomType.Treasure:
                _ = GenerateTreasure(state, isPriorRoom: true);
                break;
        }
    }

    internal CombatRewardDetails GenerateCombatRewards(
        State state,
        RoomType roomType,
        EncounterModel? encounter)
    {
        var player = state.Player;
        var slots = new List<RewardSlot>();
        var finalBossWithoutRewards = roomType == RoomType.Boss
                                      && player.RunState.CurrentActIndex >= player.RunState.Acts.Count - 1;

        if (!finalBossWithoutRewards && (encounter?.ShouldGiveRewards ?? true))
        {
            var shouldAddPotion = player.PlayerOdds.PotionReward.Roll(player, roomType);
            var gold = encounter is null
                ? player.PlayerRng.Rewards.NextInt(1)
                : player.PlayerRng.Rewards.NextInt(encounter.MinGoldReward, encounter.MaxGoldReward + 1);
            slots.Add(RewardSlot.GoldSlot(gold));

            if (shouldAddPotion)
            {
                var potion = PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Rewards);
                slots.Add(RewardSlot.Potion(new RewardItemDetails(ForecastItemDetails.Potion(potion))));
            }

            var cards = GenerateCards(player, roomType, isFromCombat: true);
            slots.Add(RewardSlot.Cards(cards));

            if (roomType == RoomType.Elite)
                slots.Add(RewardSlot.Relic(PullPlayerRelic(state)));
        }

        ApplyEarlyHooks(state, roomType, finalBossWithoutRewards, slots);
        ApplyLateHooks(state, roomType, slots);
        foreach (var slot in slots.Where(slot => !slot.Populated))
        {
            if (slot.Kind == RewardKind.Cards)
                slot.Items.AddRange(GenerateCards(player, slot.CardRoomType, isFromCombat: false));
            else if (slot.Kind == RewardKind.Relic)
                slot.Items.Add(PullPlayerRelic(state));
            slot.Populated = true;
        }

        return new CombatRewardDetails(
            slots.Where(slot => slot.Kind == RewardKind.Gold).Sum(slot => slot.Gold),
            slots.Where(slot => slot.Kind == RewardKind.Cards)
                .Select(slot => (IReadOnlyList<RewardItemDetails>)slot.Items.ToArray())
                .ToArray(),
            slots.Where(slot => slot.Kind == RewardKind.Potion)
                .SelectMany(slot => slot.Items)
                .ToArray(),
            slots.Where(slot => slot.Kind == RewardKind.Relic)
                .SelectMany(slot => slot.Items)
                .ToArray());
    }

    internal TreasureRoomDetails GenerateTreasure(State state, bool isPriorRoom)
    {
        state.TreasureRoomsAdvanced++;
        var relics = new List<RewardItemDetails>();
        var localReceivesTreasure = false;
        foreach (var runPlayer in state.Run.Players)
        {
            if (!Hook.ShouldGenerateTreasure(state.Run, runPlayer))
                continue;

            var rarity = RelicFactory.RollRarity(state.Run.Rng.TreasureRoomRelics);
            var relic = TryGetTutorialTreasureRelic(state, runPlayer)
                        ?? state.Run.SharedRelicGrabBag.PullFromFront(rarity, state.Run)
                        ?? RelicFactory.FallbackRelic;
            relics.Add(new RewardItemDetails(ForecastItemDetails.Relic(relic)));
            localReceivesTreasure |= ReferenceEquals(runPlayer, state.Player);
        }

        var gold = 0;
        if (localReceivesTreasure)
        {
            gold = state.Player.PlayerRng.Rewards.NextInt(42, 53);
            if (state.Player.RunState.AscensionLevel >= (int)MegaCrit.Sts2.Core.Entities.Ascension.AscensionLevel.Poverty)
                gold = (int)(gold * 0.75);
        }

        if (isPriorRoom && state.Run.Players.Count == 1 && relics.Count == 1)
        {
            var relic = ModelDb.GetById<RelicModel>(relics[0].Id);
            state.Player.RelicGrabBag.Remove(relic);
        }

        return new TreasureRoomDetails(gold, relics, relics.Count == 0);
    }

    internal MerchantInventoryForecast GenerateMerchant(State state)
    {
        state.MerchantVisits++;
        var player = state.Player;
        var shops = player.PlayerRng.Shops;
        var characterTypes = new[] { CardType.Attack, CardType.Attack, CardType.Skill, CardType.Skill, CardType.Power };
        var saleIndex = shops.NextInt(characterTypes.Length);
        var characterPool = player.Character.CardPool
            .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
            .ToArray();
        var characterCards = new List<MerchantItemForecast>();
        for (var index = 0; index < characterTypes.Length; index++)
        {
            var result = CardFactory.CreateForMerchant(player, characterPool, characterTypes[index]);
            var card = result.Card;
            var cost = Mathf.RoundToInt(BaseCardCost(card) * shops.NextFloat(0.95f, 1.05f));
            if (index == saleIndex)
                cost /= 2;
            characterCards.Add(new MerchantItemForecast(
                ForecastItemDetails.Card(card),
                ApplyMerchantPrice(state, cost),
                index == saleIndex));
        }

        var colorlessPool = ModelDb.CardPool<MegaCrit.Sts2.Core.Models.CardPools.ColorlessCardPool>()
            .GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint)
            .ToArray();
        var colorlessCards = new List<MerchantItemForecast>();
        foreach (var rarity in new[] { CardRarity.Uncommon, CardRarity.Rare })
        {
            var result = CardFactory.CreateForMerchant(player, colorlessPool, rarity);
            colorlessCards.Add(new MerchantItemForecast(
                ForecastItemDetails.Card(result.Card),
                ApplyMerchantPrice(state, Mathf.RoundToInt(BaseCardCost(result.Card) * shops.NextFloat(0.95f, 1.05f)))));
        }

        var relics = new List<MerchantItemForecast>();
        foreach (var rarity in new[]
                 {
                     RelicFactory.RollRarity(player),
                     RelicFactory.RollRarity(player),
                     RelicRarity.Shop
                 })
        {
            var relic = player.RelicGrabBag.PullFromBack(
                            rarity,
                            candidate => candidate.IsAllowedInShops,
                            player.RunState)
                        ?? RelicFactory.FallbackRelic;
            state.Run.SharedRelicGrabBag.Remove(relic);
            var cost = (int)Math.Round(relic.MerchantCost * shops.NextFloat(0.85f, 1.15f));
            relics.Add(new MerchantItemForecast(
                ForecastItemDetails.Relic(relic),
                ApplyMerchantPrice(state, cost)));
        }

        var potions = PotionFactory.CreateRandomPotionsOutOfCombat(player, 3, shops)
            .Select(potion =>
            {
                var baseCost = potion.Rarity switch
                {
                    PotionRarity.Rare => 100,
                    PotionRarity.Uncommon => 75,
                    _ => 50
                };
                var cost = Mathf.RoundToInt(baseCost * shops.NextFloat(0.95f, 1.05f));
                return new MerchantItemForecast(
                    ForecastItemDetails.Potion(potion),
                    ApplyMerchantPrice(state, cost));
            })
            .ToArray();

        var removal = new MerchantCardRemovalEntry(player);
        return new MerchantInventoryForecast(
            characterCards,
            colorlessCards,
            relics,
            potions,
            ApplyMerchantPrice(state, removal.Cost),
            state.MerchantVisits);
    }

    internal void RemoveRelic(State state, ModelId relicId)
    {
        var relic = ModelDb.AllRelics.FirstOrDefault(candidate => candidate.Id.Entry == relicId.Entry);
        if (relic is null)
            return;
        state.Player.RelicGrabBag.Remove(relic);
        state.Run.SharedRelicGrabBag.Remove(relic);
    }

    internal void AddRelic(State state, ModelId relicId)
    {
        var canonical = ModelDb.AllRelics.FirstOrDefault(relic => relic.Id.Entry == relicId.Entry);
        if (canonical is null || state.Player.Relics.Any(relic => relic.Id.Entry == relicId.Entry && !relic.IsStackable))
            return;

        state.Player.AddRelicInternal(canonical.ToMutable(), silent: true);
        if (!canonical.IsStackable)
        {
            state.Player.RelicGrabBag.Remove(canonical);
            state.Run.SharedRelicGrabBag.Remove(canonical);
        }
    }

    private IReadOnlyList<RewardItemDetails> GenerateCards(Player player, RoomType roomType, bool isFromCombat)
    {
        var flags = CardCreationFlags.IsCardReward;
        if (isFromCombat)
            flags |= CardCreationFlags.IsFromCombat;
        return CardFactory.CreateForReward(
                player,
                3,
                CardCreationOptions.ForRoom(player, roomType).WithFlags(flags))
            .Select(result => new RewardItemDetails(ForecastItemDetails.Card(result.Card)))
            .ToArray();
    }

    private RewardItemDetails PullPlayerRelic(State state)
    {
        var relic = RelicFactory.PullNextRelicFromFront(state.Player);
        return new RewardItemDetails(ForecastItemDetails.Relic(relic));
    }

    private static void ApplyEarlyHooks(
        State state,
        RoomType roomType,
        bool finalBossWithoutRewards,
        List<RewardSlot> slots)
    {
        foreach (var listener in state.Run.IterateHookListeners(null))
        {
            switch (listener)
            {
                case AmethystAubergine aubergine
                    when ReferenceEquals(aubergine.Owner, state.Player)
                         && roomType.IsCombatRoom()
                         && !finalBossWithoutRewards:
                    slots.Add(RewardSlot.GoldSlot(aubergine.DynamicVars.Gold.IntValue));
                    break;
                case BlackStar blackStar
                    when ReferenceEquals(blackStar.Owner, state.Player) && roomType == RoomType.Elite:
                    slots.Add(RewardSlot.UnpopulatedRelic());
                    break;
                case LavaRock lavaRock
                    when ReferenceEquals(lavaRock.Owner, state.Player)
                         && roomType == RoomType.Boss
                         && state.Player.RunState.CurrentActIndex == 0
                         && !lavaRock.HasTriggered
                         && !state.LavaRockTriggered:
                    for (var index = 0; index < lavaRock.DynamicVars["Relics"].IntValue; index++)
                        slots.Add(RewardSlot.UnpopulatedRelic());
                    state.LavaRockTriggered = true;
                    break;
                case PrayerWheel prayerWheel
                    when ReferenceEquals(prayerWheel.Owner, state.Player) && roomType == RoomType.Monster:
                    slots.Add(RewardSlot.UnpopulatedCards(RoomType.Monster));
                    break;
                case WhiteStar whiteStar
                    when ReferenceEquals(whiteStar.Owner, state.Player) && roomType == RoomType.Elite:
                    slots.Add(RewardSlot.UnpopulatedCards(RoomType.Boss));
                    break;
                case WongosMysteryTicket ticket
                    when ReferenceEquals(ticket.Owner, state.Player)
                         && !ticket.GaveRelic
                         && !state.WongosTicketTriggered
                         && ticket.CombatsFinished + state.CombatsAdvanced >= WongosMysteryTicket.combatsToActivate:
                    for (var index = 0; index < WongosMysteryTicket.relicCount; index++)
                        slots.Add(RewardSlot.UnpopulatedRelic());
                    state.WongosTicketTriggered = true;
                    break;
            }
        }
    }

    private static void ApplyLateHooks(State state, RoomType roomType, List<RewardSlot> slots)
    {
        foreach (var listener in state.Run.IterateHookListeners(null))
        {
            switch (listener)
            {
                case Midas:
                    foreach (var gold in slots.Where(slot => slot.Kind == RewardKind.Gold))
                        gold.Gold *= 2;
                    break;
                case Vintage when roomType == RoomType.Monster:
                    for (var index = 0; index < slots.Count; index++)
                    {
                        if (slots[index].Kind == RewardKind.Cards)
                            slots[index] = RewardSlot.UnpopulatedRelic();
                    }
                    break;
            }
        }
    }

    private static int ApplyMerchantPrice(State state, int cost)
    {
        var probe = new PriceProbe(state.Player, cost);
        return (int)Hook.ModifyMerchantPrice(state.Run, state.Player, probe, cost);
    }

    private static float BaseCardCost(CardModel card)
    {
        var cost = card.Rarity switch
        {
            CardRarity.Rare => 150,
            CardRarity.Uncommon => 75,
            _ => 50
        };
        return card.Pool is MegaCrit.Sts2.Core.Models.CardPools.ColorlessCardPool
            ? Mathf.RoundToInt(cost * 1.15f)
            : cost;
    }

    private static RelicModel? TryGetTutorialTreasureRelic(State state, Player player)
    {
        if (state.Run.Players.Count != 1
            || !ReferenceEquals(player, state.Player)
            || player.UnlockState.NumberOfRuns != 0
            || state.HistoricalTreasureRooms + state.TreasureRoomsAdvanced != 1)
            return null;

        var gorget = ModelDb.Relic<Gorget>();
        if (!state.Player.RelicGrabBag.Contains(gorget))
            return null;
        state.Player.RelicGrabBag.Remove(gorget);
        state.Run.SharedRelicGrabBag.Remove(gorget);
        return gorget;
    }

    private enum RewardKind
    {
        Gold,
        Cards,
        Potion,
        Relic
    }

    private sealed class RewardSlot
    {
        public required RewardKind Kind { get; init; }
        public int Gold { get; set; }
        public List<RewardItemDetails> Items { get; } = [];
        public RoomType CardRoomType { get; init; }
        public bool Populated { get; set; }

        public static RewardSlot GoldSlot(int amount) => new() { Kind = RewardKind.Gold, Gold = amount, Populated = true };
        public static RewardSlot Cards(IEnumerable<RewardItemDetails> items)
        {
            var slot = new RewardSlot
            {
                Kind = RewardKind.Cards,
                Populated = true
            };
            slot.Items.AddRange(items);
            return slot;
        }
        public static RewardSlot Potion(RewardItemDetails item) => new()
        {
            Kind = RewardKind.Potion,
            Populated = true,
            Items = { item }
        };
        public static RewardSlot Relic(RewardItemDetails item) => new()
        {
            Kind = RewardKind.Relic,
            Populated = true,
            Items = { item }
        };
        public static RewardSlot UnpopulatedCards(RoomType roomType) => new() { Kind = RewardKind.Cards, CardRoomType = roomType };
        public static RewardSlot UnpopulatedRelic() => new() { Kind = RewardKind.Relic };

        private RewardSlot WithItems(IEnumerable<RewardItemDetails> items)
        {
            Items.AddRange(items);
            return this;
        }
    }

    private sealed class PriceProbe(Player player, int cost) : MerchantEntry(player)
    {
        public override bool IsStocked => true;
        public override void CalcCost() => _cost = cost;
        protected override Task<(bool, int)> OnTryPurchase(MerchantInventory? inventory, bool ignoreCost) =>
            Task.FromResult((false, 0));
        protected override void ClearAfterPurchase() { }
        protected override void RestockAfterPurchase(MerchantInventory? inventory) { }
    }
}
