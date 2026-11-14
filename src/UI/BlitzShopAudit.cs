using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.UI;

/// <summary>
/// Production audit for the Blitz shop window: every completed purchase logs
/// what the window promised against what actually landed. Reward screens stay
/// unverified (their contents only exist once the player picks); deck effects
/// — curses, granted cards, upgrades — are deterministic and checked exactly,
/// so a "预测不准" report can be read straight out of godot.log under
/// [BlitzAudit] instead of reconstructed by hand.
/// </summary>
internal static class BlitzShopAudit
{
    private static Dictionary<(string Id, bool Upgraded), int>? _deckBefore;
    private static MerchantInventory? _visit;
    private static readonly List<int> Purchased = [];

    /// <summary>Captured before the async purchase body runs; cheap, and
    /// overwritten by the next attempt if this one is declined.</summary>
    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseUnsynchronized))]
    internal static class OnTryPurchasePrefix
    {
        [HarmonyPrefix]
        private static void Prefix(MerchantEntry __instance)
        {
            try
            {
                if (__instance is not BlitzMerchantEntry entry
                    || ShadowIsolation.IsShadowPlayer(entry.Player)
                    || entry.Player.RunState is not RunState run
                    || run.GameMode != GameMode.Blitz)
                    return;
                _deckBefore = BlitzMerchantProbe.CountDeck(entry.Player);
            }
            catch (Exception)
            {
                // The audit never stands between the player and the shop.
            }
        }
    }

    /// <summary>
    /// Fires on the game thread after a successful purchase: cursed buys and
    /// direct deck grants are already in the deck, so the diff against the
    /// pre-purchase snapshot is the purchase's real deck outcome.
    /// </summary>
    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted))]
    internal static class PurchaseCompletedPostfix
    {
        [HarmonyPostfix]
        private static void Postfix(MerchantEntry __instance)
        {
            try
            {
                Audit(__instance as BlitzMerchantEntry);
            }
            catch (Exception error)
            {
                Entry.Logger.Info("[BlitzAudit] audit failed: " + error.GetBaseException().Message);
            }
        }
    }

    private static void Audit(BlitzMerchantEntry? entry)
    {
        if (entry is null
            || ShadowIsolation.IsShadowPlayer(entry.Player)
            || BlitzShopYield.InventoryOf(entry) is not { } inventory
            || !inventory.IsBlitzShop)
            return;

        if (!ReferenceEquals(_visit, inventory))
        {
            _visit = inventory;
            Purchased.Clear();
        }
        var slot = SlotOf(inventory, entry);
        if (slot < 0) return;
        var prefix = Purchased.ToArray();
        if (!Purchased.Contains(slot)) Purchased.Add(slot);

        var preview = BlitzShopPlanPanel.CurrentRow(slot, prefix);
        var deckVerdict = "no-row";
        if (preview is not null && _deckBefore is not null)
        {
            deckVerdict = preview.Purchased && !preview.NeedsCardPick
                ? Compare(preview, entry.Player, _deckBefore)
                : "pick-dependent";
        }
        _deckBefore = null;

        Entry.Logger.Info($"[BlitzAudit] slot={slot} {entry.MerchantEntryType} "
            + $"prefix=[{string.Join(",", prefix)}] deck={deckVerdict}");
    }

    private static int SlotOf(MerchantInventory inventory, BlitzMerchantEntry entry)
    {
        for (var candidate = 0; candidate < inventory.BlitzEntries.Count; candidate++)
            if (ReferenceEquals(inventory.BlitzEntries[candidate], entry))
                return candidate;
        return -1;
    }

    /// <summary>
    /// Deterministic deck effects of the row against the real deck delta.
    /// </summary>
    private static string Compare(BlitzYieldForecast preview, Player player,
        Dictionary<(string Id, bool Upgraded), int> before)
    {
        var after = BlitzMerchantProbe.CountDeck(player);
        var (gained, upgraded) = BlitzMerchantProbe.DiffDeck(player, before, after);
        var actual = gained.Concat(upgraded)
            .Select(item => (item.Name, item.IsUpgraded)).ToList();
        // RewardsSet.Offer awaits the player's picks, so by the time
        // PurchaseCompleted fires the diff contains whatever the player took
        // from the offered screens. Those are the row's own offerings, not
        // surprises; only cards outside the deterministic gains AND the
        // screens are engine mismatches.
        var deterministic = preview.GainedCards.Concat(preview.UpgradedCards)
            .Select(item => (item.Name, item.IsUpgraded)).ToList();
        var screens = preview.CardScreens.SelectMany(screen => screen)
            .Select(item => (item.Name, item.IsUpgraded)).ToHashSet();
        var (ok, detail) = ClassifyDiff(deterministic, screens, actual);
        return ok
            ? detail.Length == 0 ? "OK" : $"OK (+{detail} picked)"
            : $"MISMATCH {detail}";
    }

    /// <summary>
    /// The pure verdict core: every deterministic gain must appear in the
    /// diff, and every extra card (the player's screen picks) must be one of
    /// the offered screen cards. Returns the picked count or the offenders.
    /// </summary>
    internal static (bool Ok, string Detail) ClassifyDiff(
        IReadOnlyList<(string Name, bool Upgraded)> deterministic,
        IReadOnlyCollection<(string Name, bool Upgraded)> screens,
        IReadOnlyList<(string Name, bool Upgraded)> actual)
    {
        var remaining = new List<(string Name, bool Upgraded)>(actual);
        foreach (var item in deterministic)
        {
            var index = remaining.FindIndex(candidate => candidate == item);
            if (index < 0)
                return (false, "missing " + item.Name + (item.Upgraded ? "+" : ""));
            remaining.RemoveAt(index);
        }
        var outside = remaining.Where(item => !screens.Contains(item)).ToList();
        return outside.Count == 0
            ? (true, remaining.Count.ToString())
            : (false, "unexpected "
                + string.Join(",", outside.Select(item => item.Name + (item.Upgraded ? "+" : ""))));
    }

    /// <summary>Multiset comparison over (name, upgraded); the pure core the
    /// self-test exercises so the audit's verdicts are themselves checked.</summary>
    internal static (bool Ok, string Detail) CompareMultiset(
        IEnumerable<(string Name, bool Upgraded)> expected,
        IEnumerable<(string Name, bool Upgraded)> actual)
    {
        var counts = new Dictionary<(string, bool), int>();
        foreach (var item in expected)
        {
            counts.TryGetValue(item, out var count);
            counts[item] = count + 1;
        }
        foreach (var item in actual)
        {
            if (!counts.TryGetValue(item, out var count) || count == 0)
                return (false, $"unexpected '{item.Item1}{(item.Item2 ? "+" : "")}'");
            counts[item] = count - 1;
        }
        var missing = counts.Where(pair => pair.Value > 0)
            .Select(pair => $"{pair.Key.Item1}{(pair.Key.Item2 ? "+" : "")}×{pair.Value}")
            .ToList();
        return missing.Count == 0
            ? (true, "")
            : (false, "missing " + string.Join(",", missing));
    }
}
