using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;
using SeedOracle.Integration;
using SeedOracle.UI;
using SeedOracle.Validation;

namespace SeedOracle.Forecasting;

/// <summary>
/// Threads ONE cloned, choice-modified reward state along the planned route.
/// Each planned room's rewards/shop inventory/treasure are predicted from the
/// plan state after every earlier room and user choice, so a relic taken at
/// node k disappears from downstream shops, elites, and chests.
/// </summary>
internal sealed class RoutePlanForecastService(PlanningPredictionService planning)
{
    internal PlanningPredictionService.StateSnapshot Capture(PlanningPredictionService.State state) =>
        planning.Capture(state);

    internal sealed class PlanNodeOutcome
    {
        public Forecast<CombatRewardDetails>? CombatRewards { get; set; }
        public Forecast<TreasureRoomDetails>? Treasure { get; set; }
        public Forecast<MerchantInventoryForecast>? Merchant { get; set; }
        public MerchantInventoryForecast? MerchantAfterPurchases { get; set; }
        public List<(int PlanIndex, MerchantItemForecast Item)> MerchantPurchases { get; } = [];
        public int? MerchantGoldAfterPurchases { get; set; }
        public RelicModel? PulledRelic { get; set; }
        public int GoldDelta { get; set; }
        public string? Note { get; set; }
        public PlanningPredictionService.StateSnapshot? BeforeCombatRewards { get; set; }
    }

    internal sealed record ProjectedCard(ModelId Id, string Title, bool Upgraded);

    /// <summary>Event result that can be joined back into the native plan
    /// state. The adapter that executes events may still live in the
    /// integration layer, but the route predictor only consumes this neutral
    /// state boundary.</summary>
    internal sealed record PlannedEventOutcome(
        RunState ShadowRun,
        ulong PlayerNetId,
        bool Finished);

    internal sealed class PlanChain
    {
        public required PlanningPredictionService.State State { get; init; }
        public required Dictionary<MapCoord, PlanNodeOutcome> Outcomes { get; init; }
        public required Dictionary<MapCoord, PlanningPredictionService.StateSnapshot> StatesBefore { get; init; }
        public MapCoord? BlockedAtEvent { get; set; }
        public MapCoord? BlockedAtNode { get; set; }

        /// <summary>
        /// Potion slots threaded along the plan. The shadow player's native
        /// slots are the only mutable source of truth; this property is a
        /// snapshot view for callers that need the current count.
        /// </summary>
        public IReadOnlyList<ModelId> PotionSlots =>
            PlanningPredictionService.GetPotionSlotIds(State);

        /// <summary>Projected deck threaded along the plan: planned card
        /// pickups add, forge upgrades, cook removals — downstream choices
        /// (smith/cook pickers, later event decks) see this, not the live deck.</summary>
        public required List<ProjectedCard> Deck { get; init; }
        public int Gold { get; set; }
    }

    /// <summary>Builds the threaded chain for every non-completed plan entry.</summary>
    public PlanChain? BuildChain(
        RunState run,
        Player player,
        IReadOnlyList<RoutePlanEntry> allEntries,
        Func<RoutePlanEntry, MapPoint?> findPoint,
        RoutePlan plan,
        IReadOnlyDictionary<MapCoord, PlannedEventOutcome>? eventOutcomes = null)
    {
        var headIndex = 0;
        while (headIndex < allEntries.Count && allEntries[headIndex].IsCompleted)
            headIndex++;
        _ = run;
        _ = plan;

        var state = planning.CreateState(run, player);
        var potionMax = state.Player.MaxPotionCount;
        var chain = new PlanChain
        {
            State = state,
            Outcomes = new Dictionary<MapCoord, PlanNodeOutcome>(),
            StatesBefore = new Dictionary<MapCoord, PlanningPredictionService.StateSnapshot>(),
            Deck = state.Player.Deck.Cards
                .Select(card => new ProjectedCard(card.Id, card.Title, card.IsUpgraded))
                .ToList()
        };
        var gold = state.Player.Gold;
        ShadowIsolation.Enter(state.Player);
        try
        {
            for (var index = headIndex; index < allEntries.Count; index++)
            {
                var entry = allEntries[index];
                var point = findPoint(entry);
                if (point is null)
                    return chain;

                var roomType = RoutePlanPanelControl.RoomFromPointType(point.PointType);
                var outcome = new PlanNodeOutcome();
                state.Run.ActFloor = point.coord.row + 1;
                chain.StatesBefore[entry.Coord] = planning.Capture(state);

            // An event preview is executed against the state immediately
            // before this node. Join its serialized result here so all later
            // native reward rolls see cards, relics, potions, RNG and flags
            // changed by that event.
                if (eventOutcomes?.TryGetValue(entry.Coord, out var eventOutcome) == true)
                {
                    if (!eventOutcome.Finished)
                    {
                        outcome.Note = "事件仍有后续选项，完成事件后才能继续计算后续路线";
                        chain.Outcomes[entry.Coord] = outcome;
                        chain.BlockedAtEvent = entry.Coord;
                        break;
                    }

                    planning.ApplyEventState(state, eventOutcome.ShadowRun, eventOutcome.PlayerNetId);
                    ShadowIsolation.ReplaceCurrent(state.Player);
                    chain.Deck.Clear();
                    chain.Deck.AddRange(state.Player.Deck.Cards.Select(card =>
                        new ProjectedCard(card.Id, card.Title, card.IsUpgraded)));
                    potionMax = state.Player.MaxPotionCount;
                    gold = state.Player.Gold;
                    outcome.Note = eventOutcome.Finished
                        ? "事件预演结果已接入后续世界线"
                        : "事件预演已接入；仍有后续选项待确定";
                }
                else if (eventOutcomes is not null
                         && entry.Choice is RoutePlanChoice.EventOption pendingEvent
                         && (pendingEvent.OptionIndex >= 0 || pendingEvent.PreviousSteps.Count > 0))
                {
                    // A selected event has not changed the threaded state until
                    // its exact shadow preview succeeds. Do not show later rooms
                    // as if this choice had no effect.
                    outcome.Note = "事件选项已选择但尚未完成预演，完成后才能计算后续路线";
                    chain.Outcomes[entry.Coord] = outcome;
                    chain.BlockedAtEvent = entry.Coord;
                    break;
                }

                potionMax = state.Player.MaxPotionCount;

            // Resolve against the choice-modified graph, including any event
            // effects on room odds and encounter queues.
                EncounterModel? nodeEncounter = null;
                if (roomType is RoomType.Monster or RoomType.Elite or RoomType.Boss
                    || point.PointType == MapPointType.Unknown)
                {
                    roomType = MerchantChoiceBranchService.ResolveRoom(state, point);
                    if (roomType.IsCombatRoom())
                        nodeEncounter = MerchantChoiceBranchService.ResolveEncounter(state, roomType);
                }

                try
                {
                switch (roomType)
                {
                case RoomType.Monster:
                case RoomType.Elite:
                case RoomType.Boss:
                {
                    var before = RoutePlanResourceSnapshot.Capture(state.Player);
                    var combatChoice = RoutePlanChoice.AsCombat(entry.Choice);
                    if (combatChoice.SimulationReference is { } simulationReference
                        && (nodeEncounter is null
                            || simulationReference.TargetFloor != entry.Coord.row + 1
                            || simulationReference.TargetColumn != entry.Coord.col
                            || !planning.ApplyCombatSimulationReference(
                            state,
                            chain.StatesBefore[entry.Coord],
                            simulationReference,
                            nodeEncounter,
                            roomType)))
                    {
                        outcome.Note = "模拟战斗参照已过期，请重新模拟当前规划状态";
                        chain.Outcomes[entry.Coord] = outcome;
                        chain.BlockedAtNode = entry.Coord;
                        break;
                    }
                    state.CombatsAdvanced++;
                    outcome.BeforeCombatRewards = planning.Capture(state);
                    using var generated = planning.GenerateCombatRewards(state, roomType, nodeEncounter,
                        assumedHuntTriggers: combatChoice.HuntTriggers);
                    var rewards = ApplyCombatRewards(state, generated, combatChoice, before);
                    var rooms = state.Run.Act._rooms;
                    if (roomType == RoomType.Monster) rooms.normalEncountersVisited++;
                    if (roomType == RoomType.Elite) rooms.eliteEncountersVisited++;
                    if (roomType == RoomType.Boss) rooms.bossEncountersVisited++;
                    outcome.CombatRewards = Forecast<CombatRewardDetails>.Branch(
                        rewards,
                        PredictionDependency.Rewards
                        | PredictionDependency.CardRarityOdds
                        | PredictionDependency.RelicGrabBag
                        | PredictionDependency.PlayerState,
                        "按计划推进到该战斗的胜利掉落；实际拿取与用药后应重算。");
                    gold = state.Player.Gold;
                    outcome.GoldDelta = gold - before.Gold;
                    chain.Deck.Clear();
                    chain.Deck.AddRange(state.Player.Deck.Cards.Select(card =>
                        new ProjectedCard(card.Id, card.Title, card.IsUpgraded)));
                    if (rewards.CardRewardGroups.Any(group => group.Steps.Any(step => step.RejectedChoice)))
                        outcome.Note = "卡牌奖励已变化，失效的选择未执行，请重新选择";
                    break;
                }
                case RoomType.Shop:
                {
                    var goldBeforeShop = state.Player.Gold;
                    state.Run.AppendToMapPointHistory(MapPointType.Shop, RoomType.Shop, null);
                    using var shop = new PlanningMerchantSession(state);
                    gold = state.Player.Gold;
                    var inventory = shop.Describe();
                    outcome.Merchant = Forecast<MerchantInventoryForecast>.Branch(
                        inventory,
                        PredictionDependency.Shops
                        | PredictionDependency.Rewards
                        | PredictionDependency.RelicGrabBag
                        | PredictionDependency.CardRarityOdds,
                        "按计划推进到该次进店的库存；实际购买、补货与删牌后应重算。");
                    if (entry.Choice is RoutePlanChoice.Merchant merchantChoice)
                    {
                        var rejectedPurchases = 0;
                        foreach (var (pick, planIndex) in merchantChoice.Picks.Select((pick, index) => (pick, index)))
                        {
                            var item = IndexOf(shop.Describe(), pick);
                            try
                            {
                                if (shop.TryPurchase(pick)) outcome.MerchantPurchases.Add((planIndex, item));
                                else rejectedPurchases++;
                            }
                            finally { gold = state.Player.Gold; }
                        }
                        if (merchantChoice.RemoveCard)
                        {
                            var removed = state.Player.Deck.Cards.FirstOrDefault(card => card.IsRemovable);
                            if (removed is null || !shop.TryRemove(removed)) rejectedPurchases++;
                            gold = state.Player.Gold;
                        }

                        if (rejectedPurchases > 0)
                        {
                            outcome.Note = (outcome.Note is null ? "" : outcome.Note + "；")
                                           + $"商店计划有 {rejectedPurchases} 项因金币、库存或药水槽条件不足未执行";
                        }
                    }
                    outcome.MerchantAfterPurchases = shop.Describe();
                    outcome.MerchantGoldAfterPurchases = gold;
                    outcome.GoldDelta = gold - goldBeforeShop;
                    chain.Deck.Clear();
                    chain.Deck.AddRange(state.Player.Deck.Cards.Select(card =>
                        new ProjectedCard(card.Id, card.Title, card.IsUpgraded)));
                    break;
                }
                case RoomType.Treasure:
                {
                    var goldBefore = gold;
                    var treasure = planning.GenerateTreasure(state, isPriorRoom: false);
                    outcome.Treasure = Forecast<TreasureRoomDetails>.Branch(
                        treasure,
                        PredictionDependency.Rewards | PredictionDependency.RelicGrabBag,
                        "按计划推进到该宝箱的内容；实际拾取后应重算。");
                    // Include native entry income (Maw Bank) before adding
                    // the separately generated treasure reward.
                    gold = state.Player.Gold + treasure.Gold;
                    state.Player.Gold = gold;
                    outcome.GoldDelta += treasure.Gold;
                    if (entry.Choice is not RoutePlanChoice.Relic { Take: false })
                    {
                        foreach (var relic in treasure.Relics)
                            planning.AddRelic(state, relic.Id);
                        gold = state.Player.Gold;
                    }
                    outcome.GoldDelta = gold - goldBefore;
                    break;
                }
                case RoomType.RestSite:
                {
                    var goldBeforeRest = state.Player.Gold;
                    planning.EnterRestSite(state);
                    gold = state.Player.Gold;
                    ApplyRestChoice(chain, potionMax, entry.Choice, outcome, ref gold);
                    outcome.GoldDelta = gold - goldBeforeRest;
                    break;
                }
                }
                }
                catch (Exception exception)
                {
                    outcome.Note = "领取或选择尚未完成，后续路线已暂停：" + exception.GetBaseException().Message;
                    chain.BlockedAtNode = entry.Coord;
                }

                // Keep native hooks and any later event snapshot in sync with the
                // virtual ledger after this node's recorded purchases/rewards.
                state.Player.Gold = gold;
                state.Run.AddVisitedMapCoord(point.coord);

                chain.Outcomes[entry.Coord] = outcome;
                if (chain.BlockedAtNode is not null)
                    break;
            }

            chain.Gold = gold;
            return chain;
        }
        finally
        {
            ShadowIsolation.Exit();
        }
    }

    internal static CombatRewardDetails ApplyCombatRewards(
        PlanningPredictionService.State state,
        CombatRewardDetails rewards,
        RoutePlanChoice.Combat combatChoice,
        RoutePlanResourceSnapshot? before = null)
    {
        before ??= RoutePlanResourceSnapshot.Capture(state.Player);
        using var scope = ShadowIsolation.Enter(state.Player, automateRewards: true);
        using var rewardRoom = rewards.NativeEncounter is { } encounter && state.Run.CurrentRoom is not CombatRoom
            ? PlanningPredictionService.EnterRewardRoom(state, encounter) : null;
        var cards = rewards.NativeCardRewards;
        var sessions = cards.Select(card => new ShadowIsolation.CardRewardSession(card)).ToArray();
        var steps = cards.Select(_ => new List<CardRewardStepDetails>()).ToArray();
        var rejected = new bool[cards.Count];
        var potionIndex = 0;
        var discardUsed = false;
        foreach (var card in rewards.NativeCardRewards)
        {
            card.Player.RelicObtained -= card.OnRelicObtained;
            card.Player.RelicObtained += card.OnRelicObtained;
        }
        foreach (var reward in rewards.NativeRewards)
        {
            if (reward is CardReward)
                continue;
            var take = reward is not RelicReward || combatChoice.TakeRelic;
            if (reward is PotionReward)
            {
                take = combatChoice.PotionChoice?.TakenPotions.Contains(potionIndex++) ?? true;
                if (take && !state.Player.HasOpenPotionSlots && !discardUsed
                    && combatChoice.PotionChoice?.DiscardPotion is { } discardId)
                {
                    var discard = state.Player.Potions.FirstOrDefault(potion => potion.Id == discardId);
                    if (discard is not null)
                    {
                        state.Player.DiscardPotionInternal(discard, silent: true);
                        discardUsed = true;
                    }
                }
            }
            using var noImplicitPicks = ShadowIsolation.UseSelector(new PlannedCardRewardSelector([]));
            if (!take || !reward.SelectUnsynchronized().GetAwaiter().GetResult())
                reward.OnSkipped();
        }
        foreach (var action in combatChoice.OrderedActions)
        {
            var group = action.GroupIndex;
            if (group < 0 || group >= cards.Count)
                continue;
            var picks = combatChoice.ChoiceForGroup(group).Steps;
            if (rejected[group] || sessions[group].Finished)
                continue;
            if (action.StepIndex != steps[group].Count || action.StepIndex >= picks.Count)
            {
                rejected[group] = true;
                continue;
            }
            var selector = new PlannedCardRewardSelector([picks[action.StepIndex]]);
            var selection = selector.GetSelectedCardReward(cards[group]._cards,
                CardRewardAlternative.Generate(cards[group]));
            steps[group].Add(selector.Steps[0]);
            rejected[group] = selector.Steps[0].RejectedChoice;
            using var selectorScope = ShadowIsolation.UseSelector(new PlannedCardRewardSelector([]));
            sessions[group].ApplyAsync(selection).GetAwaiter().GetResult();
            if (sessions[group].Finished)
                sessions[group].CompleteRewardAsync().GetAwaiter().GetResult();
        }
        var groups = new List<CombatCardRewardGroup>();
        for (var group = 0; group < cards.Count; group++)
        {
            var complete = sessions[group].Finished && !rejected[group];
            if (!sessions[group].Finished)
            {
                steps[group].Add(PlanningPredictionService.DescribeCardReward(cards[group]) with
                    { RejectedChoice = rejected[group] });
                sessions[group].ApplyAsync(default).GetAwaiter().GetResult();
                sessions[group].CompleteRewardAsync().GetAwaiter().GetResult();
            }
            groups.Add(new CombatCardRewardGroup(steps[group]) { ChoiceComplete = complete });
        }
        return rewards.Detach() with
        {
            CardRewardGroups = groups,
            AppliedDelta = new RewardResourceDelta(
                state.Player.Gold - before.Gold,
                state.Player.Deck.Cards.Count - before.DeckCards.Count,
                state.Player.Relics.Count - before.Relics.Count,
                state.Player.Potions.Count() - before.Potions.Count,
                state.Player.Creature.CurrentHp - before.Hp)
        };
    }

    private sealed class PlannedCardRewardSelector(IReadOnlyList<CardRewardPick> picks) : ICardSelector
    {
        public List<CardRewardStepDetails> Steps { get; } = [];
        private bool rejected;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options,
            int minSelect,
            int maxSelect) =>
            throw new InvalidOperationException("奖励还需要选择牌组目标，当前计划无法自动确定该选择。");

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            var pick = !rejected && Steps.Count < picks.Count ? picks[Steps.Count] : null;
            var selectedIndex = -1;
            CardRewardSelection selection = default;
            if (pick?.AlternativeId is { } alternativeId)
            {
                var index = alternatives.ToList().FindIndex(item => item.OptionId == alternativeId);
                if (index >= 0)
                {
                    selectedIndex = options.Count + index;
                    selection.alternative = alternatives[index];
                }
            }
            else if (pick is { CardIndex: >= 0 } && pick.CardIndex < options.Count)
            {
                var card = options[pick.CardIndex].Card;
                if ((pick.CardId is null || card.Id == pick.CardId)
                    && (pick.CardStateKey is null || pick.CardStateKey == ForecastItemDetails.Card(card).StateKey))
                {
                    selectedIndex = pick.CardIndex;
                    selection.card = card;
                }
            }
            rejected |= pick is not null && selectedIndex < 0;
            Steps.Add(new CardRewardStepDetails(
                options.Select(option => new RewardItemDetails(ForecastItemDetails.Card(option.Card))).ToArray(),
                alternatives.Select(alternative => new CardRewardAlternativeDetails(
                    alternative.OptionId, alternative.Title.GetFormattedText(), alternative.AfterSelected)).ToArray(),
                selectedIndex, rejected));
            return selection;
        }
    }

    private void ApplyRestChoice(
        PlanChain chain,
        int potionMax,
        RoutePlanChoice? choice,
        PlanNodeOutcome outcome,
        ref int gold)
    {
        var state = chain.State;
        switch (choice)
        {
            case RoutePlanChoice.RestSite { OptionId: "DIG" }:
            {
                var rarity = RelicFactory.RollRarity(state.Player.PlayerRng.Rewards);
                var relic = state.Player.RelicGrabBag.PullFromFront(rarity, state.Player.RunState);
                if (relic is not null)
                {
                    outcome.PulledRelic = relic;
                    planning.AddRelic(state, relic.Id);
                    gold = state.Player.Gold;
                    outcome.Note = "挖掘遗物：" + relic.Title.GetFormattedText();
                }
                break;
            }
            case RoutePlanChoice.RestSite { OptionId: "COOK" }:
                if (choice is RoutePlanChoice.RestSite cookChoice
                    && cookChoice.TargetCard is { } firstCookId
                    && cookChoice.SecondTargetCard is { } secondCookId
                    && (firstCookId.Entry != secondCookId.Entry
                        || (cookChoice.TargetCardSlot >= 0
                            && cookChoice.SecondTargetCardSlot >= 0
                            && cookChoice.TargetCardSlot != cookChoice.SecondTargetCardSlot)))
                {
                    var removed = 0;
                    var targets = new[]
                    {
                        (Id: firstCookId, Slot: cookChoice.TargetCardSlot),
                        (Id: secondCookId, Slot: cookChoice.SecondTargetCardSlot)
                    }
                    .OrderByDescending(target => target.Slot >= 0 ? target.Slot : int.MinValue);
                    foreach (var target in targets)
                    {
                        var projectedIndex = target.Slot >= 0
                                             && target.Slot < chain.Deck.Count
                                             && chain.Deck[target.Slot].Id.Entry == target.Id.Entry
                            ? target.Slot
                            : chain.Deck.FindIndex(card => card.Id.Entry == target.Id.Entry);
                        if (projectedIndex >= 0
                            && planning.RemoveCard(state, target.Id, target.Slot))
                        {
                            chain.Deck.RemoveAt(projectedIndex);
                            removed++;
                        }
                    }

                    if (removed == 2)
                    {
                        state.Player.Creature.SetMaxHpInternal(state.Player.Creature.MaxHp + 5);
                        state.Player.Creature.SetCurrentHpInternal(state.Player.Creature.CurrentHp + 5);
                        outcome.Note = "烹饪：已移除 2 张目标牌，最大生命 +5";
                    }
                    else
                    {
                        outcome.Note = $"烹饪：只找到 {removed}/2 张目标牌，未完整应用";
                    }
                }
                else
                {
                    outcome.Note = "烹饪：需要选择两张不同槽位的目标牌";
                }
                break;
            case RoutePlanChoice.RestSite { OptionId: "SMITH" }:
                if (choice is RoutePlanChoice.RestSite smithChoice
                    && smithChoice.TargetCard is { } smithId)
                {
                    // ModelId identifies the card model, not a particular copy
                    // in the deck. Match by entry so a choice survives a
                    // serialized shadow-run refresh.
                    var smithSlot = smithChoice.TargetCardSlot;
                    var cardIndex = smithSlot >= 0
                                     && smithSlot < chain.Deck.Count
                                     && chain.Deck[smithSlot].Id.Entry == smithId.Entry
                        ? smithSlot
                        : chain.Deck.FindIndex(card => card.Id.Entry == smithId.Entry);
                    if (cardIndex >= 0
                        && !chain.Deck[cardIndex].Upgraded
                        && planning.UpgradeCard(state, smithId, smithSlot))
                    {
                        chain.Deck[cardIndex] = chain.Deck[cardIndex] with { Upgraded = true };
                        outcome.Note = "锻造：目标牌已升级（计划牌组）";
                    }
                    else
                    {
                        outcome.Note = "锻造：目标牌不可升级或已升级";
                    }
                }
                else
                {
                    outcome.Note = "锻造：未选目标牌";
                }
                break;
            case RoutePlanChoice.RestSite { OptionId: "LIFT" }:
                outcome.Note = "举重：提升最大生命";
                break;
        }
    }

    /// <summary>
    /// Applies potion pickups against the virtual slot list: explicit takes,
    /// or take-everything by default; a recorded discard frees one slot once.
    /// Over-capacity potions are dropped with a note — never invented space.
    /// </summary>
    internal void ApplyPotionTakes(
        PlanChain chain,
        int max,
        IReadOnlyList<RewardItemDetails> potions,
        RoutePlanChoice.Potion? choice,
        PlanNodeOutcome outcome)
    {
        if (potions.Count == 0)
            return;

        var state = chain.State;
        var taken = choice?.TakenPotions ?? Enumerable.Range(0, potions.Count).ToList();
        var discardUsed = false;
        var gained = 0;
        var dropped = 0;
        foreach (var index in taken.Distinct())
        {
            if (index < 0 || index >= potions.Count)
                continue;

            var potionId = potions[index].Id;
            var discard = PlanningPredictionService.GetPotionSlotIds(state).Count >= max
                          && !discardUsed
                          && choice?.DiscardPotion is { } discardId
                ? discardId
                : (ModelId?)null;
            var added = planning.AddPotion(state, potionId, discard);
            if (added)
            {
                if (discard is not null)
                    discardUsed = true;
                gained++;
                Entry.Logger.Debug(
                    $"[PlanPotion] applied {potionId.Category}.{potionId.Entry}; "
                    + $"slots={string.Join(',', PlanningPredictionService.GetPotionSlotIds(state).Select(id => id.Entry))}");
            }
            else
                dropped++;

        }

        if (gained > 0)
            outcome.Note = (outcome.Note is null ? "" : outcome.Note + "；") + $"+{gained}药水";
        if (dropped > 0)
            outcome.Note = (outcome.Note is null ? "" : outcome.Note + "；")
                           + $"药水槽已满，放弃 {dropped} 瓶";
    }

    /// <summary>
    /// Picks the worldline whose route passes through the planned rooms
    /// (head..index-1) in order — extras allowed only before the plan head —
    /// and yields the target's real encounter model.
    /// </summary>
    private static RouteWorldline? MatchWorldline(
        IReadOnlyList<RouteWorldline> worldlines,
        IReadOnlyList<RoutePlanEntry> entries,
        int headIndex,
        int index)
    {
        foreach (var line in worldlines)
        {
            var coords = line.Route
                .Select(choice => choice.Point.coord)
                .ToArray();
            var plannedIndex = headIndex;
            var consuming = false;
            var matched = true;
            for (var coordIndex = 0; coordIndex < coords.Length; coordIndex++)
            {
                if (plannedIndex < index && coords[coordIndex] == entries[plannedIndex].Coord)
                {
                    consuming = true;
                    plannedIndex++;
                }
                else if (!consuming)
                {
                    continue;
                }
                else
                {
                    matched = false;
                    break;
                }
            }

            if (matched && plannedIndex == index)
                return line;
        }

        return null;
    }

    private static MerchantItemForecast IndexOf(MerchantInventoryForecast merchant, MerchantPick pick) => pick.Category switch
    {
        MerchantCategory.CharacterCard => IndexOr(merchant.CharacterCards, pick.Index),
        MerchantCategory.ColorlessCard => IndexOr(merchant.ColorlessCards, pick.Index),
        MerchantCategory.Relic => IndexOr(merchant.Relics, pick.Index),
        _ => IndexOr(merchant.Potions, pick.Index)
    };

    private static MerchantItemForecast IndexOr(IReadOnlyList<MerchantItemForecast> items, int index) =>
        index >= 0 && index < items.Count
            ? items[index]
            : new MerchantItemForecast(ForecastItemDetails.Text("?"), 0);
}
