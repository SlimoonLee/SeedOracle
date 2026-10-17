using Godot;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using SeedOracle.Api;
using SeedOracle.Integration;
using SeedOracle.UI;
using SeedOracle.Validation;

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
        var shadowRun = RestoreRun(snapshot);
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

    internal static string StateToken(StateSnapshot snapshot)
    {
        var normalized = CloneSnapshot(snapshot.Run);
        normalized.SaveTime = 0;
        normalized.RunTime = 0;
        normalized.WinTime = 0;
        normalized.NumReloads = 0;
        normalized.MapDrawings = null;
        normalized.PreFinishedRoom = null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            normalized,
            JsonSerializationUtility.GetTypeInfo<SerializableRun>());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static string StateToken(State state) => StateToken(new PlanningPredictionService().Capture(state));

    internal bool ApplyCombatSimulationReference(
        State state,
        StateSnapshot expectedSnapshot,
        CombatSimulationReference reference,
        EncounterModel encounter,
        RoomType roomType)
    {
        if (reference.RoomType != roomType
            || !reference.EncounterId.Equals(encounter.Id.Entry, StringComparison.OrdinalIgnoreCase)
            || !StateToken(expectedSnapshot).Equals(reference.StateToken, StringComparison.Ordinal)
            || !StateToken(state).Equals(reference.StateToken, StringComparison.Ordinal)
            || reference.SampleCount is < 1 or > 10
            || reference.SelectedSampleIndex < 0
            || reference.SelectedSampleIndex >= reference.SampleCount
            || reference.ProjectedHpLoss < 0
            || reference.FinalHp is not > 0
            || reference.FinalHp > state.Player.Creature.MaxHp)
        {
            return false;
        }

        var usedSlots = new HashSet<int>();
        var usedPotions = new List<PotionModel>();
        foreach (var use in reference.PotionUses)
        {
            PotionModel? potion = null;
            if (use.Slot >= 0 && use.Slot < state.Player.PotionSlots.Count)
            {
                var slotPotion = state.Player.PotionSlots[use.Slot];
                if (slotPotion is not null
                    && slotPotion.Id.Entry.Equals(use.Id, StringComparison.OrdinalIgnoreCase))
                {
                    potion = slotPotion;
                }
            }

            if (potion is null || !usedSlots.Add(state.Player.PotionSlots.IndexOf(potion)))
                return false;
            usedPotions.Add(potion);
        }

        foreach (var potion in usedPotions)
            state.Player.DiscardPotionInternal(potion, silent: true);
        state.Player.Creature.SetCurrentHpInternal(reference.FinalHp.Value);
        return true;
    }

    internal State Restore(StateSnapshot snapshot)
    {
        var run = RestoreRun(snapshot.Run);
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
        var clonedRun = RestoreRun(CreateSerializableRun(eventRun));
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
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        CardPileCmd.Add(card, PileType.Deck, skipVisuals: true).GetAwaiter().GetResult();
    }

    internal void AddCard(State state, ForecastItemDetails item)
    {
        if (item.StateKey is null)
        {
            AddCard(state, item.Id!, item.IsUpgraded);
            return;
        }
        var save = JsonSerializer.Deserialize(item.StateKey, JsonSerializationUtility.GetTypeInfo<SerializableCard>())
                   ?? throw new InvalidOperationException("Missing purchased card state.");
        var card = CardModel.FromSerializable(save);
        state.Run.AddCard(card, state.Player);
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        CardPileCmd.Add(card, PileType.Deck, skipVisuals: true).GetAwaiter().GetResult();
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
        using var isolation = ShadowIsolation.Enter(state.Player);
        CardCmd.Upgrade(card, CardPreviewStyle.None);
        return true;
    }

    internal bool AddPotion(State state, ModelId potionId, ModelId? discardId = null)
    {
        var canonical = ResolvePotion(potionId);
        if (canonical is null)
        {
            Entry.Logger.Warn(
                $"[PlanPotion] unable to resolve requested potion {potionId.Category}.{potionId.Entry}; "
                + "the planning state was left unchanged.");
            return false;
        }

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
        var result = state.Player.AddPotionInternal(canonical.ToMutable(), silent: true);
        if (!result.success)
        {
            Entry.Logger.Warn(
                $"[PlanPotion] failed to add {canonical.Id.Category}.{canonical.Id.Entry}: "
                + $"{result.failureReason}");
        }

        return result.success;
    }

    internal static IReadOnlyList<ModelId> GetPotionSlotIds(State state) =>
        state.Player.PotionSlots
            .OfType<PotionModel>()
            .Select(potion => potion.Id)
            .ToArray();

    /// <summary>
    /// Resolves a potion ID at the model boundary. Saves and forecast records
    /// can come from different game builds, so category casing and the exact
    /// slug spelling must not decide whether a valid potion is discarded.
    /// </summary>
    internal static PotionModel? ResolvePotion(ModelId id)
    {
        try
        {
            var registered = ModelDb.GetByIdOrNull<PotionModel>(id);
            if (registered is not null)
                return registered;
        }
        catch (Exception exception)
        {
            Entry.Logger.Debug(
                $"[PlanPotion] direct lookup failed for {id.Category}.{id.Entry}: {exception.Message}");
        }

        var potion = ModelDb.AllPotions.FirstOrDefault(candidate =>
            candidate.Id.Category.Equals(id.Category, StringComparison.OrdinalIgnoreCase)
            && candidate.Id.Entry.Equals(id.Entry, StringComparison.OrdinalIgnoreCase));
        if (potion is not null)
            return potion;

        // Some serialized saves and older forecast adapters retained only the
        // entry or used a different slug spelling (for example camel case vs
        // snake case). Potion entries are globally unique, so normalized entry
        // matching is safe and keeps StrengthPotion/"肌肉药水" intact.
        var normalizedEntry = NormalizePotionKey(id.Entry);
        return ModelDb.AllPotions.FirstOrDefault(candidate =>
            NormalizePotionKey(candidate.Id.Entry) == normalizedEntry
            || NormalizePotionKey(candidate.GetType().Name) == normalizedEntry);
    }

    private static string NormalizePotionKey(string value) =>
        new(value.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    private static bool PotionIdsMatch(ModelId left, ModelId right) =>
        left.Category.Equals(right.Category, StringComparison.OrdinalIgnoreCase)
        && left.Entry.Equals(right.Entry, StringComparison.OrdinalIgnoreCase)
        || left.Entry.Equals(right.Entry, StringComparison.OrdinalIgnoreCase);

    internal static SerializableRun CreateSerializableRun(RunState source)
    {
        using var workSample = ForecastWorkProfile.Measure("snapshot.capture");
        // RunState intentionally has no public ToSerializable method. Start
        // from the engine's save envelope, then replace every mutable section
        // with the supplied shadow state so RNG, odds, bags and inventories
        // remain on the same worldline.
        SerializableRun save;
        using (ForecastWorkProfile.Measure("snapshot.header"))
            save = RunManager.Instance.ToSave(preFinishedRoom: null);
        save.Acts = source.Acts.Select(act => act.ToSave()).ToList();
        if (source.Map is not NullActMap)
            save.Acts[source.CurrentActIndex].SavedMap = SerializableActMap.FromActMap(source.Map);
        save.Modifiers = source.Modifiers.Select(modifier => modifier.ToSerializable()).ToList();
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
        return CloneSnapshot(save);
    }

    internal static RunState RestoreRun(SerializableRun snapshot)
    {
        using var workSample = ForecastWorkProfile.Measure("snapshot.restore");
        var owned = CloneSnapshot(snapshot);
        var run = RunState.FromSerializable(owned);
        ShadowIsolation.RegisterRun(run);
        // FromSerializable restores players/history, but leaves Map unset.
        // Preserve the captured topology without regenerating it or using live objects.
        if (owned.Acts[owned.CurrentActIndex].SavedMap is { } map)
            run.Map = new SavedActMap(map);
        // Native RunManager performs this after FromSerializable. In
        // particular Deadly Events restores the non-serialized elite base
        // odds here; omitting it makes elite question marks disappear after
        // the first roll. Do not invoke unreviewed third-party load effects.
        foreach (var modifier in run.Modifiers.Where(modifier => modifier.GetType().Assembly == typeof(ModifierModel).Assembly))
            modifier.OnRunLoaded(run);
        return run;
    }

    internal static SerializableRun CloneSnapshot(SerializableRun save)
    {
        using var workSample = ForecastWorkProfile.Measure("snapshot.clone");
        // FromSerializable reuses mutable history entries from its input.
        // Cross the actual save serialization boundary before handing a graph
        // to an event, otherwise even a cloned player can alter live history.
        var typeInfo = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(save, typeInfo);
        var clone = JsonSerializer.Deserialize(bytes, typeInfo)
                    ?? throw new InvalidOperationException("Unable to clone planning run snapshot.");
        // Native serialization omits false, while the native initializer is true.
        // Restore just that primitive from the source graph after decoding. The
        // serializer preserves list order; every mutable object still belongs
        // to the clone. Avoid building and re-encoding a whole JSON DOM merely
        // to fix these flags on each branch capture and restore.
        for (var actIndex = 0; actIndex < save.Acts.Count; actIndex++)
        {
            if (save.Acts[actIndex].SavedMap is not { } sourceMap)
                continue;
            var targetMap = clone.Acts[actIndex].SavedMap!;
            for (var pointIndex = 0; pointIndex < sourceMap.Points.Count; pointIndex++)
                targetMap.Points[pointIndex].CanBeModified = sourceMap.Points[pointIndex].CanBeModified;
            targetMap.BossPoint.CanBeModified = sourceMap.BossPoint.CanBeModified;
            targetMap.StartingPoint.CanBeModified = sourceMap.StartingPoint.CanBeModified;
            if (sourceMap.SecondBossPoint is { } secondBoss)
                targetMap.SecondBossPoint!.CanBeModified = secondBoss.CanBeModified;
        }
        return clone;
    }

    internal void AdvanceRoomsBeforeTarget(State state, IReadOnlyList<RoomType> resolvedRooms)
    {
        var visited = new Dictionary<RoomType, int>();
        foreach (var type in resolvedRooms.Take(resolvedRooms.Count - 1))
        {
            EncounterModel? encounter = null;
            if (type.IsCombatRoom())
            {
                var offset = visited.GetValueOrDefault(type);
                var rooms = state.Run.Act._rooms;
                encounter = type switch
                {
                    RoomType.Monster when rooms.normalEncounters.Count > 0 =>
                        rooms.normalEncounters[(rooms.normalEncountersVisited + offset) % rooms.normalEncounters.Count],
                    RoomType.Elite when rooms.eliteEncounters.Count > 0 =>
                        rooms.eliteEncounters[(rooms.eliteEncountersVisited + offset) % rooms.eliteEncounters.Count],
                    RoomType.Boss => offset > 0 ? state.Run.Act.SecondBossEncounter : state.Run.Act.BossEncounter,
                    _ => null
                };
                visited[type] = offset + 1;
            }
            AdvanceRoom(state, type, encounter);
        }
    }

    internal void AdvanceRoom(State state, RoomType roomType, EncounterModel? encounter)
    {
        switch (roomType)
        {
            case RoomType.Monster:
            case RoomType.Elite:
            case RoomType.Boss:
                state.CombatsAdvanced++;
                using (GenerateCombatRewards(state, roomType, encounter)) { }
                break;
            case RoomType.Shop:
                _ = GenerateMerchant(state);
                break;
            case RoomType.Treasure:
                _ = GenerateTreasure(state, isPriorRoom: true);
                break;
            case RoomType.RestSite:
                EnterRestSite(state);
                break;
        }
    }

    internal void EnterRestSite(State state)
    {
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        var room = new RestSiteRoom();
        state.Run.PushRoom(room);
        try { Hook.AfterRoomEntered(state.Run, room).GetAwaiter().GetResult(); }
        finally { state.Run.PopCurrentRoom(); }
    }

    internal CombatRewardDetails GenerateCombatRewards(
        State state,
        RoomType roomType,
        EncounterModel? encounter,
        CombatRoom? eventCombatRoom = null,
        int assumedHuntTriggers = 0)
    {
        var player = state.Player;
        using var isolation = ShadowIsolation.Enter(player, automateRewards: true);
        if (encounter is null)
            throw new InvalidOperationException("Combat reward prediction requires a resolved encounter.");

        // CombatRoom is the native boundary for reward generation. It carries
        // the encounter's gold range and lets RewardsSet dispatch every early
        // and late reward hook in the same order as a finished combat.
        var mutableEncounter = encounter.IsMutable ? (EncounterModel)encounter.MutableClone() : encounter.ToMutable();
        var room = eventCombatRoom ?? new CombatRoom(mutableEncounter, state.Run);
        if (assumedHuntTriggers is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(assumedHuntTriggers));
        for (var trigger = 0; trigger < assumedHuntTriggers; trigger++)
            room.AddExtraReward(player, new CardReward(CardCreationOptions.ForRoom(player, room.RoomType), 3, player));
        state.Run.AppendToMapPointHistory(eventCombatRoom is not null ? MapPointType.Ancient : room.RoomType switch
        {
            RoomType.Elite => MapPointType.Elite,
            RoomType.Boss => MapPointType.Boss,
            _ => MapPointType.Monster
        }, room.RoomType, encounter.Id);
        state.Run.PushRoom(room);
        RewardsSet? rewards = null;
        CardReward[] originalCards = [];
        try
        {
            // A normal map combat enters a new base room. Event sub-combats
            // already have their parent event and must not pay Maw Bank twice.
            // Do not dispatch combat setup callbacks requiring live combat
            // piles/powers merely to predict rewards.
            if (eventCombatRoom is null)
                foreach (var bank in state.Run.Players.SelectMany(owner => owner.Relics).OfType<MawBank>())
                    bank.AfterRoomEntered(room).GetAwaiter().GetResult();
            // Invoke native model callbacks on the shadow graph. Dispatching
            // global combat lifecycle methods would also notify other mods
            // that the real combat ended.
            foreach (var model in state.Run.IterateHookListeners(null))
            {
                model.AfterCombatEnd(room).GetAwaiter().GetResult();
                model.InvokeExecutionFinished();
            }
            foreach (var model in state.Run.IterateHookListeners(null))
            {
                model.AfterCombatVictoryEarly(room).GetAwaiter().GetResult();
                model.InvokeExecutionFinished();
            }
            foreach (var model in state.Run.IterateHookListeners(null))
            {
                model.AfterCombatVictory(room).GetAwaiter().GetResult();
                model.InvokeExecutionFinished();
            }
            if (!encounter.ShouldGiveRewards)
                return new CombatRewardDetails(0, [], [], []);
            rewards = new RewardsSet(player).WithRewardsFromRoom(room);
            originalCards = ShadowIsolation.EnumerateRewards(rewards.Rewards).OfType<CardReward>().ToArray();
            rewards.GenerateWithoutOffering().GetAwaiter().GetResult();
            Hook.BeforeCombatRewardOffered(rewards, state.Run, room).GetAwaiter().GetResult();
        }
        finally
        {
            state.Run.PopCurrentRoom();
            foreach (var card in originalCards.Concat(
                         ShadowIsolation.EnumerateRewards(rewards?.Rewards ?? []).OfType<CardReward>()).Distinct())
                player.RelicObtained -= card.OnRelicObtained;
        }

        return DescribeRewards(rewards, originalCards) with { NativeEncounter = mutableEncounter };
    }

    internal static IDisposable EnterRewardRoom(State state, EncounterModel encounter) => new RewardRoom(state, encounter);

    private sealed class RewardRoom : IDisposable
    {
        private readonly RunState run;
        internal RewardRoom(State state, EncounterModel encounter)
        {
            run = state.Run;
            run.PushRoom(new CombatRoom(encounter.IsMutable ? (EncounterModel)encounter.MutableClone() : encounter.ToMutable(), run));
        }
        public void Dispose() => run.PopCurrentRoom();
    }

    internal static CombatRewardDetails DescribeRewards(RewardsSet rewards, IReadOnlyList<CardReward>? originalCards = null)
    {
        var cardGroups = new List<CombatCardRewardGroup>();
        var potions = new List<RewardItemDetails>();
        var relics = new List<RewardItemDetails>();
        var gold = 0;
        var nativeGroups = new List<CardReward>();

        var nativeRewards = ShadowIsolation.EnumerateRewards(rewards.Rewards).ToArray();
        foreach (var reward in nativeRewards)
        {
            switch (reward)
            {
                case GoldReward goldReward:
                    gold += goldReward.Amount;
                    break;
                case CardReward cardReward:
                {
                    cardGroups.Add(new CombatCardRewardGroup([DescribeCardReward(cardReward)]));
                    nativeGroups.Add(cardReward);
                    break;
                }
                case PotionReward potionReward when potionReward.Potion is { } potion:
                    potions.Add(new RewardItemDetails(ForecastItemDetails.Potion(potion)));
                    break;
                case RelicReward relicReward when relicReward.Relic is { } relic:
                    relics.Add(new RewardItemDetails(ForecastItemDetails.Relic(relic)));
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported combat reward: {reward.GetType().Name}");
            }
        }

        return new CombatRewardDetails(gold, cardGroups, potions, relics)
        {
            NativeCardRewards = nativeGroups,
            NativeRewards = nativeRewards,
            OriginalCardRewards = originalCards ?? []
        };
    }

    internal static CardRewardStepDetails DescribeCardReward(CardReward reward) => new(
        reward.Cards.Select(card => new RewardItemDetails(ForecastItemDetails.Card(card))).ToArray(),
        CardRewardAlternative.Generate(reward).Select(alternative => new CardRewardAlternativeDetails(
            alternative.OptionId, alternative.Title.GetFormattedText(), alternative.AfterSelected)).ToArray());

    internal TreasureRoomDetails GenerateTreasure(State state, bool isPriorRoom)
    {
        state.TreasureRoomsAdvanced++;
        var relics = new List<RewardItemDetails>();
        var localReceivesTreasure = false;

        // TreasureRoom.EnterInternal notifies room entry before relic picking
        // begins, and SilverCrucible.ShouldGenerateTreasure compares against that
        // entry counter (TreasureRoomsEntered > 1). Without this dispatch every
        // predicted chest stays at the live counter and is shown as the relic's
        // still-empty first chest.
        var treasureRoom = new TreasureRoom(state.Run.CurrentActIndex);
        state.Run.PushRoom(treasureRoom);
        try
        {
            Hook.AfterRoomEntered(state.Run, treasureRoom).GetAwaiter().GetResult();

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
        finally { state.Run.PopCurrentRoom(); }
    }

    internal MerchantInventoryForecast GenerateMerchant(State state)
    {
        using var shop = new PlanningMerchantSession(state);
        return shop.Describe();
    }

    internal static MerchantInventoryForecast DescribeMerchant(MerchantInventory inventory, int ordinal)
    {
        using var pricing = ShadowIsolation.EvaluateLocalMerchantPrice();
        MerchantItemForecast Empty() => new(ForecastItemDetails.Text("—"), -1);
        MerchantItemForecast Card(MerchantCardEntry entry) => entry.CreationResult is { } result ? new(
            ForecastItemDetails.Card(result.Card), entry.Cost, entry.IsOnSale) : Empty();
        return new MerchantInventoryForecast(
            inventory.CharacterCardEntries.Select(Card).ToArray(),
            inventory.ColorlessCardEntries.Select(Card).ToArray(),
            inventory.RelicEntries.Select(entry => entry.Model is { } relic ? new MerchantItemForecast(
                ForecastItemDetails.Relic(relic), entry.Cost) : Empty()).ToArray(),
            inventory.PotionEntries.Select(entry => entry.Model is { } potion ? new MerchantItemForecast(
                ForecastItemDetails.Potion(potion), entry.Cost) : Empty()).ToArray(),
            inventory.CardRemovalEntry!.Cost,
            ordinal);
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

        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        RelicCmd.Obtain(canonical.ToMutable(), state.Player).GetAwaiter().GetResult();
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

}
