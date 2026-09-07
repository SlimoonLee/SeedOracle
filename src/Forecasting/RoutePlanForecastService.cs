using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Rooms;
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
        public RelicModel? PulledRelic { get; set; }
        public int GoldDelta { get; set; }
        public string? Note { get; set; }
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

        /// <summary>Virtual potion slots threaded along the plan (by potion id);
        /// takes replace/evict per the recorded choice, never touching live state.</summary>
        public required List<ModelId> PotionSlots { get; init; }

        /// <summary>
        /// Potion slots immediately before each planned node. The UI uses this
        /// explicit virtual ledger instead of falling back to the live player
        /// when a serialized shadow player cannot be restored for display.
        /// </summary>
        public required Dictionary<MapCoord, IReadOnlyList<ModelId>> PotionSlotsBefore { get; init; }

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
            PotionSlotsBefore = new Dictionary<MapCoord, IReadOnlyList<ModelId>>(),
            PotionSlots = state.Player.Potions.Select(potion => potion.Id).ToList(),
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
                chain.StatesBefore[entry.Coord] = planning.Capture(state);
                chain.PotionSlotsBefore[entry.Coord] = chain.PotionSlots.ToArray();

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
                    ShadowIsolation.Enter(state.Player);
                    chain.Deck.Clear();
                    chain.Deck.AddRange(state.Player.Deck.Cards.Select(card =>
                        new ProjectedCard(card.Id, card.Title, card.IsUpgraded)));
                    chain.PotionSlots.Clear();
                    chain.PotionSlots.AddRange(state.Player.Potions.Select(potion => potion.Id));
                    potionMax = state.Player.MaxPotionCount;
                    gold = state.Player.Gold;
                    outcome.Note = eventOutcome.Finished
                        ? "事件预演结果已接入后续世界线"
                        : "事件预演已接入；仍有后续选项待确定";
                }
                else if (eventOutcomes is not null
                         && entry.Choice is RoutePlanChoice.EventOption { OptionIndex: >= 0 })
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

            // Resolve the plan-matching worldline: it carries the real
            // encounter (gold ranges, monster slots) and resolved rooms.
                EncounterModel? nodeEncounter = null;
                if (roomType is RoomType.Monster or RoomType.Elite or RoomType.Boss)
                {
                    var exploration = RouteStateExplorer.Explore(run, point);
                    var worldlines = RouteWorldlinePredictor.Predict(run, point, exploration.Paths);
                    nodeEncounter = MatchWorldlineEncounter(worldlines, allEntries, headIndex, index);
                }

                switch (roomType)
                {
                case RoomType.Monster:
                case RoomType.Elite:
                case RoomType.Boss:
                {
                    state.CombatsAdvanced++;
                    var rewards = planning.GenerateCombatRewards(state, roomType, nodeEncounter);
                    var combatChoice = RoutePlanChoice.AsCombat(entry.Choice);
                    outcome.CombatRewards = Forecast<CombatRewardDetails>.Branch(
                        rewards,
                        PredictionDependency.Rewards
                        | PredictionDependency.CardRarityOdds
                        | PredictionDependency.RelicGrabBag
                        | PredictionDependency.PlayerState,
                        "按计划推进到该战斗的胜利掉落；实际拿取与用药后应重算。");
                    gold += rewards.Gold;
                    outcome.GoldDelta += rewards.Gold;

                    if (combatChoice.TakeRelic)
                    {
                        foreach (var relic in rewards.Relics)
                            planning.AddRelic(state, relic.Id);
                    }

                    if (combatChoice.CardRewardChoice is { Skip: false } cardPick)
                    {
                        outcome.Note = "+1卡";
                        // Thread the picked card into the projected deck so
                        // later smith/cook targets and event decks see it.
                        var flat = 0;
                        var done = false;
                        foreach (var bundle in rewards.CardRewards)
                        {
                            foreach (var card in bundle)
                            {
                                if (done)
                                    break;
                                if (flat == cardPick.CardIndex)
                                {
                                    chain.Deck.Add(new ProjectedCard(card.Id, card.Name, card.Item.IsUpgraded));
                                    planning.AddCard(state, card.Id, card.Item.IsUpgraded);
                                    done = true;
                                }

                                flat++;
                            }

                            if (done)
                                break;
                        }
                    }

                    ApplyPotionTakes(
                        chain,
                        potionMax,
                        rewards.Potions,
                        combatChoice.PotionChoice,
                        outcome);
                    planning.SetPotionSlots(state, chain.PotionSlots);
                    break;
                }
                case RoomType.Shop:
                {
                    var inventory = planning.GenerateMerchant(state);
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
                        var purchasedEntries = new HashSet<MerchantPick>();
                        foreach (var pick in merchantChoice.Picks)
                        {
                            var item = IndexOf(inventory, pick);
                            var itemId = item.Item.Id;
                            if (!purchasedEntries.Add(pick)
                                || itemId is null
                                || item.Cost < 0
                                || gold < item.Cost)
                            {
                                rejectedPurchases++;
                                continue;
                            }

                            if (pick.Category == MerchantCategory.Potion)
                            {
                                // MerchantPotionEntry tries to procure the potion before
                                // charging gold. With no replacement choice on the shop
                                // screen, a full belt therefore rejects the purchase.
                                if (chain.PotionSlots.Count >= potionMax)
                                {
                                    rejectedPurchases++;
                                    continue;
                                }

                                gold -= item.Cost;
                                chain.PotionSlots.Add(itemId);
                                planning.SetPotionSlots(state, chain.PotionSlots);
                                outcome.Note = (outcome.Note is null ? "" : outcome.Note + "；") + "+1药水";
                                continue;
                            }

                            gold -= item.Cost;
                            ApplyMerchantPurchase(chain, state, pick, item);
                        }
                        if (merchantChoice.RemoveCard)
                        {
                            if (gold >= inventory.CardRemovalCost)
                            {
                                gold -= inventory.CardRemovalCost;
                                var removed = chain.Deck.FirstOrDefault();
                                if (removed is not null)
                                {
                                    chain.Deck.Remove(removed);
                                    planning.RemoveCard(state, removed.Id);
                                }
                                else
                                {
                                    rejectedPurchases++;
                                }
                            }
                            else
                            {
                                rejectedPurchases++;
                            }
                        }

                        if (rejectedPurchases > 0)
                        {
                            outcome.Note = (outcome.Note is null ? "" : outcome.Note + "；")
                                           + $"商店计划有 {rejectedPurchases} 项因金币、库存或药水槽条件不足未执行";
                        }

                    }
                    break;
                }
                case RoomType.Treasure:
                {
                    var treasure = planning.GenerateTreasure(state, isPriorRoom: false);
                    outcome.Treasure = Forecast<TreasureRoomDetails>.Branch(
                        treasure,
                        PredictionDependency.Rewards | PredictionDependency.RelicGrabBag,
                        "按计划推进到该宝箱的内容；实际拾取后应重算。");
                    gold += treasure.Gold;
                    outcome.GoldDelta += treasure.Gold;
                    if (entry.Choice is not RoutePlanChoice.Relic { Take: false })
                    {
                        foreach (var relic in treasure.Relics)
                            planning.AddRelic(state, relic.Id);
                    }
                    break;
                }
                case RoomType.RestSite:
                {
                    ApplyRestChoice(chain, potionMax, entry.Choice, outcome, ref gold);
                    break;
                }
                }

                // Keep native hooks and any later event snapshot in sync with the
                // virtual ledger after this node's recorded purchases/rewards.
                state.Player.Gold = gold;

                chain.Outcomes[entry.Coord] = outcome;
            }

            chain.Gold = gold;
            return chain;
        }
        finally
        {
            ShadowIsolation.Exit();
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
    private static void ApplyPotionTakes(
        PlanChain chain,
        int max,
        IReadOnlyList<RewardItemDetails> potions,
        RoutePlanChoice.Potion? choice,
        PlanNodeOutcome outcome)
    {
        if (potions.Count == 0)
            return;

        var taken = choice?.TakenPotions ?? Enumerable.Range(0, potions.Count).ToList();
        var discardUsed = false;
        var gained = 0;
        var dropped = 0;
        foreach (var index in taken)
        {
            if (index < 0 || index >= potions.Count)
                continue;

            if (chain.PotionSlots.Count >= max)
            {
                if (choice?.DiscardPotion is { } discard
                    && !discardUsed
                    && RemovePotionSlot(chain.PotionSlots, discard))
                {
                    discardUsed = true;
                }
                else
                {
                    dropped++;
                    continue;
                }
            }

            chain.PotionSlots.Add(potions[index].Id);
            gained++;
        }

        if (gained > 0)
            outcome.Note = (outcome.Note is null ? "" : outcome.Note + "；") + $"+{gained}药水";
        if (dropped > 0)
            outcome.Note = (outcome.Note is null ? "" : outcome.Note + "；")
                           + $"药水槽已满，放弃 {dropped} 瓶";
    }

    private static bool RemovePotionSlot(List<ModelId> slots, ModelId discard)
    {
        var index = slots.FindIndex(slot => slot.Entry.Equals(
            discard.Entry,
            StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        slots.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Picks the worldline whose route passes through the planned rooms
    /// (head..index-1) in order — extras allowed only before the plan head —
    /// and yields the target's real encounter model.
    /// </summary>
    private static EncounterModel? MatchWorldlineEncounter(
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
                return line.TargetEncounter;
        }

        return null;
    }

    private void ApplyMerchantPurchase(
        PlanChain chain,
        PlanningPredictionService.State state,
        MerchantPick pick,
        MerchantItemForecast item)
    {
        switch (pick.Category)
        {
            case MerchantCategory.CharacterCard:
            case MerchantCategory.ColorlessCard:
                if (item.Item.Id is { } cardId)
                {
                    planning.AddCard(state, cardId, item.Item.IsUpgraded);
                    chain.Deck.Add(new ProjectedCard(cardId, item.Item.Name, item.Item.IsUpgraded));
                }
                break;
            case MerchantCategory.Relic:
                if (item.Item.Id is { } relicId)
                    planning.AddRelic(state, relicId);
                break;
            case MerchantCategory.Potion:
                break;
        }
    }

    private static int CostOf(MerchantInventoryForecast merchant, MerchantPick pick) => pick.Category switch
    {
        MerchantCategory.CharacterCard => IndexOr(merchant.CharacterCards, pick.Index).Cost,
        MerchantCategory.ColorlessCard => IndexOr(merchant.ColorlessCards, pick.Index).Cost,
        MerchantCategory.Relic => IndexOr(merchant.Relics, pick.Index).Cost,
        _ => IndexOr(merchant.Potions, pick.Index).Cost
    };

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
