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

    internal sealed class PlanChain
    {
        public required RandomForeseerAdapter.RouteRewardState State { get; init; }
        public required Dictionary<MapCoord, PlanNodeOutcome> Outcomes { get; init; }

        /// <summary>Virtual potion slots threaded along the plan (by potion id);
        /// takes replace/evict per the recorded choice, never touching live state.</summary>
        public required List<ModelId> PotionSlots { get; init; }
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
            PotionSlots = player.Potions.Select(potion => potion.Id).ToList()
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

                    if (entry.Choice is RoutePlanChoice.CardReward { Skip: false })
                        outcome.Note = "+1卡";

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
                    ApplyRestChoice(state, entry.Choice, outcome, ref gold);
                    break;
                }
            }

            chain.Outcomes[entry.Coord] = outcome;
        }

        chain.Gold = gold;
        return chain;
    }

    private void ApplyRestChoice(
        RandomForeseerAdapter.RouteRewardState state,
        RoutePlanChoice? choice,
        PlanNodeOutcome outcome,
        ref int gold)
    {
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
                outcome.Note = "烹饪：删除一张牌（卡组变化在 M3 状态线程中生效）";
                break;
            case RoutePlanChoice.RestSite { OptionId: "SMITH" }:
                outcome.Note = "锻造：升级所选牌";
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
