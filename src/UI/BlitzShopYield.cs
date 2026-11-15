using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.UI;

/// <summary>
/// Answers "what does this good actually hand over" for the icons inside a Blitz
/// shop. The shelf lives on the live run there, so the probe starts from the
/// current stream position: what it reports is exactly what buying next gives,
/// including after some goods were already taken.
/// </summary>
internal static class BlitzShopYield
{
    // Which inventory a good was put on screen from. Recording it while the slot
    // binds means the hover talks about the shelf the player is looking at, even
    // if the run's inventory lookup changes shape between versions.
    private static readonly ConditionalWeakTable<BlitzMerchantEntry, Binding> Bindings = new();

    private sealed class Binding
    {
        internal MerchantInventory? Inventory;
    }

    internal static bool Chinese => LocManager.Instance?.Language is "zhs" or "zht";

    /// <summary>
    /// Remembers the inventory a slot was filled from. Called for every good the
    /// shop puts on screen, before any hover can happen.
    /// </summary>
    internal static void Bind(BlitzMerchantEntry entry, Node slot)
    {
        try
        {
            var inventory = OwnerInventory(slot);
            if (inventory is null) return;
            Bindings.GetValue(entry, static _ => new Binding()).Inventory = inventory;
        }
        catch (Exception)
        {
            // Binding is a shortcut, never a requirement.
        }
    }

    private static MerchantInventory? OwnerInventory(Node? node)
    {
        for (var depth = 0; node is not null && depth < 8; depth++)
        {
            if (node is NMerchantInventory panel && panel.Inventory is { } inventory) return inventory;
            node = node.GetParent();
        }
        return null;
    }

    internal static bool TryDescribe(BlitzMerchantEntry entry, out string text)
    {
        text = string.Empty;
        MerchantInventory? inventory = null;
        var reason = "no reason recorded";
        try
        {
            var player = entry.Player;
            if (player is null)
            {
                reason = "the entry lost its player";
            }
            else if (ShadowIsolation.IsShadowPlayer(player))
            {
                // Expected on every probe copy: the prediction must never talk
                // about itself and turn one box into two.
                return false;
            }
            else if (player.RunState is not RunState run)
            {
                reason = "the player is not in a run";
            }
            else if (run.GameMode != GameMode.Blitz)
            {
                reason = "the run is " + run.GameMode;
            }
            else if ((inventory = Resolve(player, entry)) is null)
            {
                reason = "no inventory owns this good";
            }
            else if (!inventory.IsBlitzShop)
            {
                reason = "the inventory is not a blitz shelf";
            }
            else
            {
                var index = IndexOf(inventory, entry);
                if (index < 0)
                {
                    reason = "the good is not on the shelf";
                }
                else
                {
                    text = Describe(inventory, index);
                    if (text.Length > 0) return true;
                    reason = "the shelf described nothing";
                }
            }
        }
        catch (Exception error)
        {
            // GetInventoryFor indexes a list, so an unexpected empty inventory
            // arrives here. The type is enough to recognise it.
            reason = "the lookup threw " + error.GetBaseException().GetType().Name;
        }

        ReportOnce(entry, reason);
        return false;
    }

    private static MerchantInventory? Resolve(Player player, BlitzMerchantEntry entry) =>
        InventoryOf(entry) is { } bound && ReferenceEquals(bound.Player, player) ? bound : Fallback(player);

    /// <summary>The shelf a good was put on screen from; the audit uses this
    /// to find a purchased entry's slot without touching hover state.</summary>
    internal static MerchantInventory? InventoryOf(BlitzMerchantEntry entry)
    {
        if (Bindings.TryGetValue(entry, out var binding)
            && binding.Inventory is { } bound)
        {
            return bound;
        }
        return Fallback(entry.Player);
    }

    private static MerchantInventory? Fallback(Player? player)
    {
        // The shop panel itself reads its inventory from here, so it is the same
        // object the slots were filled from.
        return player is null
            ? null
            : RunManager.Instance?.MerchantSynchronizer?.GetInventoryFor(player);
    }

    private static int IndexOf(MerchantInventory inventory, BlitzMerchantEntry entry)
    {
        for (var slot = 0; slot < inventory.BlitzEntries.Count; slot++)
            if (ReferenceEquals(inventory.BlitzEntries[slot], entry)) return slot;
        return -1;
    }

    /// <summary>
    /// One line per good type explains why the hover stayed silent, so a "still
    /// nothing" report can be read out of the log instead of guessed at.
    /// </summary>
    private static void ReportOnce(BlitzMerchantEntry entry, string reason)
    {
        var key = entry.MerchantEntryType + ":" + reason;
        lock (Reported)
        {
            if (!Reported.Add(key)) return;
        }
        Entry.Logger.Info($"[BlitzHover] no prediction for {entry.MerchantEntryType}: {reason}");
    }

    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    internal static string Describe(MerchantInventory inventory, int index)
    {
        // The panorama table answers instantly and without clone purchases —
        // its rows are verbatim-identical to the probe by the all-type parity
        // fixture. The probe (a full run round-trip per hover, with hook
        // mirrors running inside a shadow purchase) stays only as the fallback
        // for shop entry, pending recomputes and closed windows: repeated
        // probes are the prime suspect for the second-shop native crash.
        if (BlitzShopPlanPanel.TryDescribeRow(inventory, index, out var rowText))
            return rowText;
        // While the panorama is being priced (shop entry, post-purchase
        // recompute), DO NOT probe: shop-entry hover bursts fire one full run
        // round-trip per hover — the native crash pattern. The table lands
        // within a second.
        if (BlitzShopPlanPanel.PanoramaPending)
            return Chinese ? "→ 预测生成中……" : "-> pricing…";
        // A current panorama without this exact row means the combination was
        // outside the priced budget (e.g. hovering a disabled good). Honest
        // answer, no probing.
        return Chinese ? "→ 该组合暂无预测" : "-> no forecast for this combination";
    }

    /// <summary>
    /// Folds a multi-line prediction into one line so order and plan lists stay
    /// short. Shared with the shop panorama window.
    /// </summary>
    internal static string Compact(string text) =>
        string.Join(Chinese ? "；" : "; ",
            text.Split('\n').Select(line => line.TrimStart('→', ' ').TrimEnd()));

    /// <summary>
    /// Merges the prediction into the game's own tip instead of adding a second
    /// box: the player sees one tooltip with the same title, the native text and
    /// the numbers underneath.
    /// </summary>
    internal static IReadOnlyList<IHoverTip> Compose(
        IEnumerable<IHoverTip> tips, LocString title, string text)
    {
        var list = tips.ToList();
        if (list.Count == 0 || list[0] is not HoverTip first) return list;
        list[0] = new HoverTip(title, first.Description + "\n" + text, first.Icon);
        return list;
    }
}

// The slot builds its tooltip from BlitzMerchantEntry.HoverTips, so the
// prediction joins the same box as the game's own title and description.
[HarmonyPatch(typeof(BlitzMerchantEntry), nameof(BlitzMerchantEntry.HoverTips), MethodType.Getter)]
internal static class BlitzMerchantHoverTipPatch
{
    [HarmonyPostfix]
    private static void Postfix(BlitzMerchantEntry __instance, ref IEnumerable<IHoverTip> __result)
    {
        try
        {
            if (!BlitzShopYield.TryDescribe(__instance, out var text)) return;
            __result = BlitzShopYield.Compose(__result, __instance.Title, text);
        }
        catch (Exception)
        {
            // A prediction must never cost the player the game's own tooltip.
        }
    }
}

// FillSlot is where a good lands on the shelf, and the slot already sits inside
// the shop panel, so this is the one moment the mapping is certain.
[HarmonyPatch(typeof(NBlitzMerchantSlot), nameof(NBlitzMerchantSlot.FillSlot))]
internal static class BlitzSlotBindingPatch
{
    [HarmonyPostfix]
    private static void Postfix(NBlitzMerchantSlot __instance, BlitzMerchantEntry entry)
    {
        BlitzShopYield.Bind(entry, __instance);
    }
}
