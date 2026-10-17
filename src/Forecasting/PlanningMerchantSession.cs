using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Forecasting;

// A shop is generated once and keeps its native entries until exit. In
// particular, Courier restocks use the remaining inventory as their blacklist,
// and Membership Card changes the price of later purchases in this session.
internal sealed class PlanningMerchantSession : IDisposable
{
    private readonly PlanningPredictionService.State state;
    private readonly IDisposable isolation;
    private readonly IDisposable discovery;
    private readonly MerchantRoom room;
    private bool disposed;
    internal MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory Inventory { get; }

    internal PlanningMerchantSession(PlanningPredictionService.State state)
    {
        this.state = state;
        isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        discovery = ShadowIsolation.GenerateMerchantInventory();
        room = new MerchantRoom { _runState = state.Run };
        state.Run.PushRoom(room);
        try
        {
            // Native MerchantRoom creates every player's inventory before its
            // enter-room hooks. Shared relic bags make that order observable.
            foreach (var player in state.Run.Players)
            {
                using var owner = ShadowIsolation.Enter(player, automateRewards: true);
                room.Inventories.Add(MerchantInventory.CreateForNormalMerchant(player));
            }
            Inventory = room.Inventories[state.Run.GetPlayerSlotIndex(state.Player)];
            state.MerchantVisits++;
            Hook.AfterRoomEntered(state.Run, room).GetAwaiter().GetResult();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal MerchantInventoryForecast Describe() => PlanningPredictionService.DescribeMerchant(Inventory, state.MerchantVisits);

    internal MerchantEntry? Entry(MerchantPick pick) => pick.Category switch
    {
        MerchantCategory.CharacterCard => Inventory.CharacterCardEntries.ElementAtOrDefault(pick.Index),
        MerchantCategory.ColorlessCard => Inventory.ColorlessCardEntries.ElementAtOrDefault(pick.Index),
        MerchantCategory.Relic => Inventory.RelicEntries.ElementAtOrDefault(pick.Index),
        MerchantCategory.Potion => Inventory.PotionEntries.ElementAtOrDefault(pick.Index),
        _ => null
    };

    internal bool TryPurchase(MerchantPick pick, ICardSelector? selector = null,
        Func<RewardsSet, Task>? rewardHandler = null)
    {
        var entry = Entry(pick);
        if (entry is null) return false;
        using var purchase = ShadowIsolation.ExecuteMerchantPurchase();
        using var selections = ShadowIsolation.UseSelector(selector ?? new RequiredMerchantSelector());
        using var rewards = ShadowIsolation.UseRewardHandler(rewardHandler ?? (_ =>
            throw new InvalidOperationException("购买附带奖励还需要指定领取选择，不能自动代选。 / Purchase rewards require explicit selections.")));
        return entry.OnTryPurchaseWrapper(Inventory).GetAwaiter().GetResult();
    }

    internal bool TryRemove(CardModel card)
    {
        var entry = Inventory.CardRemovalEntry;
        if (entry is null || !entry.IsStocked || !entry.EnoughGold || !card.IsRemovable
            || !Hook.ShouldAllowMerchantCardRemoval(state.Run, state.Player)) return false;
        // OneOffSynchronizer normally selects a live player's target. The
        // planner already has that target; keep its native command/hook order
        // without sending a message or opening the live removal screen.
        var cost = entry.Cost;
        PlayerCmd.LoseGold(cost, state.Player, GoldLossType.Spent).GetAwaiter().GetResult();
        CardPileCmd.RemoveFromDeck(card).GetAwaiter().GetResult();
        state.Player.ExtraFields.CardShopRemovalsUsed++;
        entry.SetUsed();
        Hook.AfterItemPurchased(state.Run, state.Player, entry, cost).GetAwaiter().GetResult();
        entry.InvokePurchaseCompleted(entry);
        return true;
    }

    private sealed class RequiredMerchantSelector : ICardSelector
    {
        public Task<IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>> GetSelectedCards(
            IEnumerable<MegaCrit.Sts2.Core.Models.CardModel> options, int minSelect, int maxSelect) =>
            throw new InvalidOperationException("购买效果还需要指定牌组目标，不能自动代选。 / Purchase needs an explicit deck selection.");

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> alternatives) =>
            throw new InvalidOperationException("购买效果还需要选择新增卡牌奖励，不能自动代选。 / Purchase needs an explicit card reward selection.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { room.Exit(state.Run).GetAwaiter().GetResult(); }
        finally
        {
            state.Run.PopCurrentRoom();
            discovery.Dispose();
            isolation.Dispose();
        }
    }
}
