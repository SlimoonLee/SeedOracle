using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
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
internal sealed class RoutePlanForecastService(RandomForeseerAdapter randomForeseer)
{
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

    internal sealed class PlanChain
    {
        public required RandomForeseerAdapter.RouteRewardState State { get; init; }
        public required Dictionary<MapCoord, PlanNodeOutcome> Outcomes { get; init; }

        /// <summary>Virtual potion slots threaded along the plan (by potion id);
        /// takes replace/evict per the recorded choice, never touching live state.</summary>
        public required List<ModelId> PotionSlots { get; init; }

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
        RoutePlan plan)
    {
        var headIndex = 0;
        while (headIndex < allEntries.Count && allEntries[headIndex].IsCompleted)
            headIndex++;
        _ = run;
        _ = plan;

        var state = randomForeseer.CreateRouteRewardStateForPlan(player);
        var potionMax = player.MaxPotionCount;
        var chain = new PlanChain
        {
            State = state,
            Outcomes = new Dictionary<MapCoord, PlanNodeOutcome>(),
            PotionSlots = player.Potions.Select(potion => potion.Id).ToList(),
            Deck = player.Deck.Cards
                .Select(card => new ProjectedCard(card.Id, card.Title, card.IsUpgraded))
                .ToList()
        };
        var gold = player.Gold;

        for (var index = headIndex; index < allEntries.Count; index++)
        {
            var entry = allEntries[index];
            var point = findPoint(entry);
            if (point is null)
                return chain;

            var roomType = RoutePlanPanelControl.RoomFromPointType(point.PointType);
            var outcome = new PlanNodeOutcome();

            switch (roomType)
            {
                case RoomType.Monster:
                case RoomType.Elite:
                case RoomType.Boss:
                {
                    var rewards = randomForeseer.GenerateCombatRewardsForPlan(state, roomType);
                    outcome.CombatRewards = Forecast<CombatRewardDetails>.Branch(
                        rewards,
                        PredictionDependency.Rewards
                        | PredictionDependency.CardRarityOdds
                        | PredictionDependency.RelicGrabBag
                        | PredictionDependency.PlayerState,
                        "按计划推进到该战斗的胜利掉落；实际拿取与用药后应重算。");
                    gold += rewards.Gold;
                    outcome.GoldDelta += rewards.Gold;

                    if (entry.Choice is not RoutePlanChoice.Relic { Take: false })
                    {
                        foreach (var relic in rewards.Relics)
                            RemoveRelicFromBags(state, relic.Id);
                    }

                    if (entry.Choice is RoutePlanChoice.CardReward { Skip: false } cardPick)
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
                                    chain.Deck.Add(new ProjectedCard(card.Id, card.Name, false));
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
                        entry.Choice as RoutePlanChoice.Potion,
                        outcome);
                    break;
                }
                case RoomType.Shop:
                {
                    var inventory = randomForeseer.PredictMerchantVisitForPlan(state);
                    outcome.Merchant = Forecast<MerchantInventoryForecast>.Branch(
                        inventory,
                        PredictionDependency.Shops
                        | PredictionDependency.Rewards
                        | PredictionDependency.RelicGrabBag
                        | PredictionDependency.CardRarityOdds,
                        "按计划推进到该次进店的库存；实际购买、补货与删牌后应重算。");
                    if (entry.Choice is RoutePlanChoice.Merchant merchantChoice)
                    {
                        foreach (var pick in merchantChoice.Picks)
                            gold -= CostOf(inventory, pick);
                        if (merchantChoice.RemoveCard)
                            gold -= inventory.CardRemovalCost;

                        var potionPicks = merchantChoice.Picks
                            .Where(pick => pick.Category == MerchantCategory.Potion)
                            .ToList();
                        if (potionPicks.Count > 0)
                        {
                            var chosenPotions = potionPicks
                                .Select(pick => new RewardItemDetails(
                                    IndexOr(inventory.Potions, pick.Index).Item))
                                .ToList();
                            ApplyPotionTakes(
                                chain,
                                potionMax,
                                chosenPotions,
                                new RoutePlanChoice.Potion(Enumerable.Range(0, chosenPotions.Count).ToList(), null),
                                outcome);
                        }
                    }
                    break;
                }
                case RoomType.Treasure:
                {
                    var treasure = randomForeseer.GenerateTreasureRoomForPlan(state);
                    outcome.Treasure = Forecast<TreasureRoomDetails>.Branch(
                        treasure,
                        PredictionDependency.Rewards | PredictionDependency.RelicGrabBag,
                        "按计划推进到该宝箱的内容；实际拾取后应重算。");
                    gold += treasure.Gold;
                    outcome.GoldDelta += treasure.Gold;
                    if (entry.Choice is not RoutePlanChoice.Relic { Take: false })
                    {
                        foreach (var relic in treasure.Relics)
                            RemoveRelicFromBags(state, relic.Id);
                    }
                    break;
                }
                case RoomType.RestSite:
                {
                    ApplyRestChoice(chain, potionMax, entry.Choice, outcome, ref gold);
                    break;
                }
            }

            chain.Outcomes[entry.Coord] = outcome;
        }

        chain.Gold = gold;
        return chain;
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
                var rarity = RelicFactory.RollRarity(state.Rewards);
                var relic = state.PlayerRelicGrabBag.PullFromFront(rarity, state.Player.RunState);
                if (relic is not null)
                {
                    outcome.PulledRelic = relic;
                    outcome.Note = "挖掘遗物：" + relic.Title.GetFormattedText();
                }
                break;
            }
            case RoutePlanChoice.RestSite { OptionId: "COOK" }:
                if (choice is RoutePlanChoice.RestSite { TargetCard: { } cookId })
                {
                    var removed = chain.Deck.RemoveAll(card => card.Id == cookId);
                    outcome.Note = removed > 0
                        ? "烹饪：目标牌已从计划牌组移除"
                        : "烹饪：目标牌不在计划牌组中";
                }
                else
                {
                    outcome.Note = "烹饪：未选目标牌";
                }
                break;
            case RoutePlanChoice.RestSite { OptionId: "SMITH" }:
                if (choice is RoutePlanChoice.RestSite { TargetCard: { } smithId })
                {
                    var cardIndex = chain.Deck.FindIndex(card => card.Id == smithId);
                    if (cardIndex >= 0 && !chain.Deck[cardIndex].Upgraded)
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
                    && chain.PotionSlots.Remove(discard))
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

    private void RemoveRelicFromBags(
        RandomForeseerAdapter.RouteRewardState state,
        ModelId relicId)
    {
        var relic = ModelDb.AllRelics.FirstOrDefault(candidate =>
            candidate.Id.Entry == relicId.Entry);
        if (relic is not null)
            randomForeseer.RemoveRelicForPlan(state, relic);
    }

    private static int CostOf(MerchantInventoryForecast merchant, MerchantPick pick) => pick.Category switch
    {
        MerchantCategory.CharacterCard => IndexOr(merchant.CharacterCards, pick.Index).Cost,
        MerchantCategory.ColorlessCard => IndexOr(merchant.ColorlessCards, pick.Index).Cost,
        MerchantCategory.Relic => IndexOr(merchant.Relics, pick.Index).Cost,
        _ => IndexOr(merchant.Potions, pick.Index).Cost
    };

    private static MerchantItemForecast IndexOr(IReadOnlyList<MerchantItemForecast> items, int index) =>
        index >= 0 && index < items.Count
            ? items[index]
            : new MerchantItemForecast(ForecastItemDetails.Text("?"), 0);
}
