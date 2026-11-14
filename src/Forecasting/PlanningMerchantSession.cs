using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
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
    private readonly IDisposable? inventoryScope;
    private readonly MerchantRoom room;
    private readonly PlanningPredictionService service = new();
    private IReadOnlyList<BlitzYieldForecast>? blitzYields;
    private bool disposed;
    internal MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory Inventory { get; }

    internal PlanningMerchantSession(PlanningPredictionService.State state)
    {
        this.state = state;
        isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        discovery = ShadowIsolation.GenerateMerchantInventory();
        room = new MerchantRoom();
        state.Run.PushRoom(room);
        try
        {
            // v0.113.0: MerchantRoom no longer owns inventories; the live
            // MerchantSynchronizer does. Build the same per-player stock on the
            // shadow run and expose it through ShadowIsolation instead.
            var inventories = new List<MerchantInventory>();
            foreach (var player in state.Run.Players)
            {
                using var owner = ShadowIsolation.Enter(player, automateRewards: true);
                // Blitz owns a different stock builder: five weighted entries
                // plus one cursed entry, rolled from the same PlayerRng.Shops
                // stream. Using the standard builder there would both misreport
                // the stock and desync every later prediction.
                inventories.Add(player.RunState.GameMode == GameMode.Blitz
                    ? MerchantInventory.CreateForBlitzMerchant(player)
                    : MerchantInventory.CreateForNormalMerchant(player));
            }
            inventoryScope = ShadowIsolation.UseMerchantInventories(inventories);
            Inventory = inventories[state.Run.GetPlayerSlotIndex(state.Player)];
            state.MerchantVisits++;
            Hook.AfterRoomEntered(state.Run, room).GetAwaiter().GetResult();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal MerchantInventoryForecast Describe()
    {
        // The Blitz shelf is priced in nothing but picks, so the only question a
        // hover can answer is "what would this one hand over". Probes buy the
        // entry on restored copies; they are cached because the answer is fixed
        // for as long as the shelf is.
        var yields = Inventory.IsBlitzShop
            ? blitzYields ??= BlitzMerchantProbe.Probe(service, state, Inventory.BlitzEntries)
            : null;
        return PlanningPredictionService.DescribeMerchant(Inventory, state.MerchantVisits, yields);
    }


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
        return entry.OnTryPurchaseUnsynchronized(Inventory).GetAwaiter().GetResult();
    }

    internal bool TryRemove(CardModel card)
    {
        var entry = Inventory.CardRemovalEntry;
        if (entry is null || !entry.IsStocked || !entry.EnoughGold || !card.IsRemovable
            || !Hook.ShouldAllowMerchantCardRemoval(state.Run, state.Player)) return false;
        if (card.Pile?.Type != PileType.Deck || !ReferenceEquals(card.Owner, state.Player)) return false;

        return ShadowMerchantRemovalPatch.RemoveAsync(entry, card, entry.Cost).GetAwaiter().GetResult();
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
        // v0.113.0's MerchantRoom.Exit only mutates the LIVE MerchantSynchronizer,
        // so a shadow session must not call it.
        try
        {
            state.Run.PopCurrentRoom();
        }
        finally
        {
            inventoryScope?.Dispose();
            discovery.Dispose();
            isolation.Dispose();
        }
    }
}
