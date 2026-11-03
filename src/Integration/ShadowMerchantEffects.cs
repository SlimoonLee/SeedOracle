using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.TestSupport;

namespace SeedOracle.Integration;

internal sealed class RequiredShadowMerchantSelector(string source = "商店") : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) =>
        throw new InvalidOperationException(source + "的删牌或商品获得效果尚未指定选牌目标，暂不推进后续预测。");

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives) =>
        throw new InvalidOperationException(source + "获得的额外卡牌奖励尚未指定选择，暂不推进后续预测。");
}

// The native entry calls OneOffSynchronizer.DoLocalMerchantCardRemoval, which
// selects and mutates the REAL local player regardless of the entry's owner.
// Route shadow entries around that boundary, including free Parasol removals.
[HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper))]
internal static class ShadowMerchantRemovalPatch
{
    [HarmonyPrefix]
    private static bool Prefix(MerchantCardRemovalEntry __instance, bool ignoreCost, bool cancelable,
        ref Task<bool> __result)
    {
        if (!ShadowIsolation.IsShadowPlayer(__instance._player)) return true;
        __result = SelectAndRemoveAsync(__instance, ignoreCost, cancelable);
        return false;
    }

    private static async Task<bool> SelectAndRemoveAsync(MerchantCardRemovalEntry entry, bool ignoreCost, bool cancelable)
    {
        var selector = ShadowIsolation.Selector ?? new RequiredShadowMerchantSelector();
        using var isolation = ShadowIsolation.Enter(entry._player, automateRewards: true);
        using var selections = ShadowIsolation.UseSelector(selector);
        if (!entry.IsStocked) return false;
        if (!ignoreCost && !entry.EnoughGold)
        {
            entry.InvokePurchaseFailed(PurchaseStatus.FailureGold);
            return false;
        }
        var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1)
        {
            Cancelable = cancelable,
            RequireManualConfirmation = true
        };
        var card = (await CardSelectCmd.FromDeckForRemoval(entry._player, prefs)).FirstOrDefault();
        return card is not null && await RemoveAsync(entry, card, ignoreCost ? 0 : entry.Cost);
    }

    internal static async Task<bool> RemoveAsync(MerchantCardRemovalEntry entry, CardModel card, int cost)
    {
        var player = entry._player;
        if (!ShadowIsolation.IsShadowPlayer(player) || !ReferenceEquals(card.Owner, player)
            || card.Pile?.Type != PileType.Deck || !card.IsRemovable || !entry.IsStocked)
            return false;
        await PlayerCmd.LoseGold(cost, player, GoldLossType.Spent);
        // This model command preserves native history and BeforeCardRemoved
        // hooks. It has no synchronizer; only its optional preview is omitted.
        await CardPileCmd.RemoveFromDeck(card, showPreview: false);
        player.ExtraFields.CardShopRemovalsUsed++;
        entry.SetUsed();
        await Hook.AfterItemPurchased(player.RunState, player, entry, cost);
        entry.InvokePurchaseCompleted(entry);
        return true;
    }
}

// LordsParasol starts an unawaited task, changes live map/deck input, and calls
// the real merchant synchronizer. Await its model purchases on the clone, with
// explicit selections and no scene or network operations. Missing selections
// propagate to the caller as an unsupported branch instead of opening real UI.
[HarmonyPatch(typeof(LordsParasol), nameof(LordsParasol.AfterRoomEntered))]
internal static class ShadowLordsParasolPatch
{
    [HarmonyPrefix]
    private static bool Prefix(LordsParasol __instance, AbstractRoom room, ref Task __result)
    {
        if (!ShadowIsolation.IsShadowPlayer(__instance.Owner)) return true;
        __result = room is MerchantRoom shop
            ? PurchaseAsync(__instance.Owner, shop)
            : Task.CompletedTask;
        return false;
    }

    private static async Task PurchaseAsync(Player player, MerchantRoom room)
    {
        // Match native multiplayer ownership: each client purchases only its
        // local inventory, even though all players' hooks are enumerated.
        var inventory = room.GetLocalInventory();
        if (!ReferenceEquals(inventory.Player, player)) return;
        var selector = ShadowIsolation.Selector ?? new RequiredShadowMerchantSelector("领主阳伞");
        using var isolation = ShadowIsolation.Enter(player, automateRewards: true);
        using var selections = ShadowIsolation.UseSelector(selector);
        using var discovery = ShadowIsolation.GenerateMerchantInventory();
        using var purchase = ShadowIsolation.ExecuteMerchantPurchase();
        foreach (var entry in inventory.CharacterCardEntries.Concat(inventory.ColorlessCardEntries))
            if (entry.IsStocked) await entry.OnTryPurchaseWrapper(inventory, ignoreCost: true);
        foreach (var entry in inventory.RelicEntries)
            await entry.OnTryPurchaseWrapper(inventory, ignoreCost: true);
        foreach (var entry in inventory.PotionEntries)
            await entry.OnTryPurchaseWrapper(inventory, ignoreCost: true);
        if (inventory.CardRemovalEntry is { } removal)
            await removal.OnTryPurchaseWrapper(inventory, ignoreCost: true, cancelable: false);
    }
}
