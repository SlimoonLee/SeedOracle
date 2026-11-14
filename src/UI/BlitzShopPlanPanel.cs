using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using SeedOracle.Data;
using SeedOracle.Forecasting;

namespace SeedOracle.UI;

/// <summary>
/// The Blitz shop window: one place that answers the whole "six goods, three
/// picks" question. The panorama page lists every good against every set of
/// earlier purchases (identical results merged); the planner page turns the
/// player's clicked buy order into each step's exact contents. Both are views
/// over one <see cref="BlitzShopPanorama"/> computed on a background task from
/// the live shelf, so they always start from where the visit actually is.
/// </summary>
internal sealed partial class BlitzShopPlanPanelControl : PanelContainer
{
    internal const string NodeName = "SeedOracleBlitzShopPlan";

    private const int MaxAlternativeLines = 8;

    private static readonly string[] Glyphs = ["①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧"];

    private readonly Label _title;
    private readonly Label _status;
    private readonly VBoxContainer _body;
    private readonly VBoxContainer _list;
    private readonly Button _panoramaTab;
    private readonly Button _plannerTab;
    private readonly Button _collapse;

    private MerchantInventory? _inventory;
    private BlitzShopPanorama? _panorama;
    private string? _fingerprint;
    private BlitzShopPlanService.BlitzShopEnumeration? _enumeration;
    private PlanningPredictionService? _buildService;
    private bool _buildPending;
    private bool _stepping;
    private bool _recomputeArmed;
    private bool _plannerMode = true;
    private bool _expanded = true;
    /// <summary>Slot → the item the player asked for. Survives recomputes;
    /// entries whose item no longer exists on the shelf drop out with a note.</summary>
    private readonly Dictionary<int, BlitzWishKey> _wishes = [];

    /// <summary>The shelf this window is bound to, for label lookups.</summary>
    internal MerchantInventory? BoundInventory => _inventory;

    /// <summary>The panorama currently on screen, for the purchase audit.</summary>
    internal BlitzShopPanorama? CurrentPanorama => _panorama;

    /// <summary>
    /// The fingerprint the current panorama was priced against, or null while
    /// no panorama is ready. Hover lookups compare it against the live shelf
    /// to decide between an instant table read and a probe.
    /// </summary>
    internal string? CurrentFingerprint => _fingerprint;

    /// <summary>
    /// Whether an enumeration is building or stepping right now. While true
    /// the hover must NOT fall back to probes: shop-entry bursts of hovers
    /// would otherwise fire one full run round-trip per hover.
    /// </summary>
    internal bool IsPricing => _buildPending || _stepping;

    /// <summary>
    /// The hover answer from this panorama for a good bought after exactly the
    /// Used slots. False when the shelf moved past the fingerprint (pending
    /// recompute), the row is missing, or the good needs the player's pick.
    /// </summary>
    internal bool TryDescribeRowFrom(MerchantInventory inventory, int index, out string text)
    {
        text = string.Empty;
        if (_panorama is not { } panorama
            || !ReferenceEquals(_inventory, inventory)
            || CurrentFingerprint != FingerprintOf(inventory))
            return false;

        var entries = inventory.BlitzEntries;
        var prefix = Enumerable.Range(0, entries.Count)
            .Where(slot => slot != index && entries[slot].Status == BlitzMerchantEntry.BlitzEntryStatus.Used)
            .ToArray();
        var good = panorama.GoodAt(index);
        var row = good?.ForPrefix(prefix);
        if (row is null || !row.Yield.Resolved)
            return false;

        var chinese = Chinese;
        text = BlitzYieldText.Render(row.Yield, chinese);
        var directText = OutcomeText(row.Yield, chinese);
        var alternatives = good!.Outcomes
            .Where(outcome => outcome.Yield.Resolved && outcome.Yield.Purchased
                && outcome.PrefixSlots.Count > 0
                && OutcomeText(outcome.Yield, chinese) != directText)
            .GroupBy(outcome => OutcomeText(outcome.Yield, chinese))
            .Take(3);
        foreach (var group in alternatives)
        {
            var example = string.Join(chinese ? "＋" : "+",
                group.First().PrefixSlots.Select(Glyph));
            text += "\n→ " + (chinese
                ? $"先买 {example} 后：{group.Key}"
                : $"after {example}: {group.Key}");
        }
        return text.Length > 0;
    }

    public BlitzShopPlanPanelControl()
    {
        Name = NodeName;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ZIndex = 160;
        SetAnchorsPreset(LayoutPreset.RightWide);
        OffsetLeft = -600f;
        OffsetTop = 64f;
        OffsetRight = -16f;
        OffsetBottom = -36f;
        Visible = false;
        AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
            new Color(0.025f, 0.07f, 0.085f, 0.96f), new Color(0.24f, 0.43f, 0.49f, 0.96f), 8));

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 12);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 12);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 8);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 10);
        AddChild(margin);

        var column = new VBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        column.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 6);
        margin.AddChild(column);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 8);
        column.AddChild(header);
        _title = PreCombatPanelStyles.CreateLabel(
            Chinese ? "极速商店 · 求购与全景" : "Blitz shop · wishes & panorama",
            18, StsColors.gold, bold: true);
        _title.ClipText = true;
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_title);
        // The tabs are absolutely anchored to the panel's top-right corner:
        // an explicit rect cannot drift from its hit area the way a
        // container-computed child did (the user-visible symptom was a
        // clickable strip below each drawn button). They live on a plain
        // Control overlay because the PanelContainer would otherwise refit
        // them into its content area.
        var tabOverlay = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore
        };
        tabOverlay.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(tabOverlay);
        _plannerTab = HeaderTabButton(Chinese ? "求购" : "Wishes", -352, -240);
        _plannerTab.Pressed += () => SwitchMode(true);
        tabOverlay.AddChild(_plannerTab);
        _panoramaTab = HeaderTabButton(Chinese ? "全景" : "Panorama", -232, -120);
        _panoramaTab.Pressed += () => SwitchMode(false);
        tabOverlay.AddChild(_panoramaTab);
        _collapse = HeaderTabButton(Chinese ? "收起" : "Hide", -112, -8);
        _collapse.Pressed += ToggleExpansion;
        tabOverlay.AddChild(_collapse);

        // Everything below the header hides when the window folds, so the
        // folded state is a title bar that stays put — the window can never
        // be lost the way a fully hidden one can.
        _body = new VBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _body.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 6);
        column.AddChild(_body);

        _status = PreCombatPanelStyles.CreateLabel(string.Empty, 14, StsColors.cream);
        _body.AddChild(_status);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _body.AddChild(scroll);
        _list = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _list.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 8);
        scroll.AddChild(_list);
        // Item hovers live on this panel so they layer above it and die with it.
        BlitzShopItemTip.BindParent(this);
        VisibilityChanged += BlitzShopItemTip.Hide;
    }

    /// <summary>
    /// A wish: the chip the player asked for, plus — for transform results —
    /// the sources that produce it. The result of a transform depends on the
    /// source's pool, so "变成 X" is only guaranteed through a listed source;
    /// the solver then has to order the shop so that source exists to pick.
    /// </summary>
    internal sealed record BlitzWishKey(string ChipKey, IReadOnlyList<string> TransformSources);

    /// <summary>Whether the body is currently unfolded.</summary>
    internal bool Expanded => _expanded;

    /// <summary>The collapse button: folds to a title bar, remembers the choice.</summary>
    internal void ToggleExpansion() => ApplyExpansion(!_expanded, remember: true);

    /// <summary>
    /// The manager's path on (re)bind: apply the remembered state without
    /// re-remembering it.
    /// </summary>
    internal void ApplyExpansion(bool expanded) => ApplyExpansion(expanded, remember: false);

    private void ApplyExpansion(bool expanded, bool remember)
    {
        if (_expanded == expanded && _body.Visible == expanded) return;
        _expanded = expanded;
        _body.Visible = expanded;
        if (expanded)
        {
            SetAnchor(Side.Bottom, 1f);
            OffsetBottom = -36f;
        }
        else
        {
            SetAnchor(Side.Bottom, 0f);
            OffsetBottom = OffsetTop + 64f;
        }
        if (remember) BlitzShopPlanPanel.RememberExpanded(expanded);
        // Unfolding a window that was folded before its first enumeration
        // still owes the player their data.
        if (expanded && _panorama is null && !_buildPending && !_stepping) RequestCompute();
    }

    /// <summary>A header tab pinned to the panel's top-right corner: the
    /// anchors fix the rect, so the hit area and the drawn button coincide by
    /// construction. `right`/`left` are offsets from the panel's right edge.</summary>
    private static Button HeaderTabButton(string text, float left, float right)
    {
        var button = PreCombatPanelStyles.CreateButton(text, 0f);
        button.AnchorLeft = 1f;
        button.AnchorRight = 1f;
        button.AnchorTop = 0f;
        button.AnchorBottom = 0f;
        button.OffsetLeft = left;
        button.OffsetRight = right;
        button.OffsetTop = 10f;
        button.OffsetBottom = 52f;
        button.ToggleMode = true;
        return button;
    }

    internal static bool Chinese => LocManager.Instance?.Language is "zhs" or "zht";

    /// <summary>
    /// Points the window at the shelf that is on screen and starts listening
    /// for the purchases that would change every answer.
    /// </summary>
    internal void Bind(MerchantInventory inventory)
    {
        if (ReferenceEquals(_inventory, inventory)) return;
        Unbind();
        _inventory = inventory;
        foreach (var entry in inventory.BlitzEntries)
        {
            entry.EntryUpdated += OnShelfChanged;
            entry.PurchaseCompleted += OnPurchased;
        }
    }

    internal void Unbind()
    {
        if (_inventory is not { } inventory) return;
        foreach (var entry in inventory.BlitzEntries)
        {
            entry.EntryUpdated -= OnShelfChanged;
            entry.PurchaseCompleted -= OnPurchased;
        }
        _inventory = null;
    }

    private void OnShelfChanged() => RefreshFromShelf();

    private void OnPurchased(PurchaseStatus _, MerchantEntry _1) => RefreshFromShelf();

    /// <summary>
    /// Entry events fire while a real purchase is being applied; the recompute
    /// decision happens on the game thread, never inside the event.
    /// </summary>
    private void RefreshFromShelf() =>
        SeedOracleDispatcher.Post(() =>
        {
            if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion()) return;
            // Statuses changed, so the buttons and tags refresh from the live
            // shelf against the panorama still on screen; the heavy recompute
            // coalesces into one delayed restart.
            RefreshContent();
            if (_inventory is { } inventory && FingerprintOf(inventory) != _fingerprint)
                ScheduleRecompute(0.6f);
        });

    /// <summary>
    /// Kicks off the panorama enumeration unless the shelf is unchanged. This
    /// only marks the work pending — the snapshot build (two full run
    /// serializations) and the enumeration itself run inside the pump's frame
    /// slices, so opening a shop never pays them synchronously. Everything
    /// stays on the main thread: a completed shadow purchase can reach the
    /// game's own UI side effects, and a background task froze the game.
    /// The previous panorama stays on screen until the replacement lands.
    /// </summary>
    internal void RequestCompute()
    {
        if (_inventory is not { } inventory) return;
        var fingerprint = FingerprintOf(inventory);
        if (_panorama is not null && fingerprint == _fingerprint)
        {
            RefreshContent();
            return;
        }

        _fingerprint = fingerprint;
        _enumeration = null;
        _buildService = new PlanningPredictionService();
        _buildPending = true;
        _stepping = false;
        _status.Text = _panorama is null
            ? Chinese ? "枚举各购买组合中……" : "enumerating purchase combinations…"
            : Chinese ? "购买已登记，重算中……" : "purchase registered, recomputing…";
        Callable.From(Pump).CallDeferred();
    }

    internal static string FingerprintOf(MerchantInventory inventory)
    {
        // The deck digest is what catches reward-screen picks: taking a card
        // changes no merchant entry and no pick counter, but every
        // deck-reading row (transforms, random upgrades, enchant pickables)
        // prices against it.
        var deck = inventory.Player.Deck.Cards;
        return inventory.BlitzChoicesRemaining + "|"
            + string.Join("|", inventory.BlitzEntries.Select(entry =>
                entry.MerchantEntryType + ":" + entry.IsStocked))
            + "|deck:" + deck.Count + ":"
            + string.Join(",", deck.Select(card =>
                card.Id.Entry + (card.IsUpgraded ? "+" : "")));
    }

    /// <summary>
    /// Shelf events arrive in bursts while reward screens from a purchase are
    /// still settling. One coalesced restart a beat later re-reads the shelf
    /// once instead of racing the UI; the fingerprint check inside makes the
    /// timer free when nothing actually changed.
    /// </summary>
    internal void ScheduleRecompute(float delaySeconds)
    {
        if (_recomputeArmed) return;
        var tree = GetTree();
        if (tree is null)
        {
            RequestCompute();
            return;
        }
        _recomputeArmed = true;
        tree.CreateTimer(delaySeconds).Timeout += () =>
        {
            _recomputeArmed = false;
            if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion()) return;
            RequestCompute();
        };
    }

    /// <summary>
    /// One frame's slice: first the snapshot build (one atomic chunk — the
    /// serializations cannot be split), then the prefix rows a few
    /// milliseconds at a time. Reschedules itself while work is left; a newer
    /// request or a freed window silently ends the chain.
    /// </summary>
    private void Pump()
    {
        if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion()) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        if (_buildPending)
        {
            if (_inventory is not { } inventory || _buildService is null)
            {
                _buildPending = false;
                _buildService = null;
                return;
            }
            try
            {
                // One read-only shadow graph for pools and hooks; the offsets
                // and every row's contents come from RNG-positioned factory
                // calls. Nothing here executes a purchase.
                var preview = new BlitzShopPreview(_buildService,
                    (RunState)inventory.Player.RunState, inventory.Player);
                preview.MeasureDeltas(inventory);
                _enumeration = new BlitzShopPlanService.BlitzShopEnumeration(preview, inventory);
            }
            catch (Exception error)
            {
                _status.Text = Chinese ? "预测不可用" : "forecast unavailable";
                Entry.Logger.Info("[BlitzPlan] compute failed: " + error.GetBaseException().Message);
            }
            _buildPending = false;
            _buildService = null;
            _stepping = true;
        }

        var enumeration = _enumeration;
        if (!_stepping || enumeration is null) return;
        while (!enumeration.Done && watch.ElapsedMilliseconds < 6) enumeration.Step();
        if (!enumeration.Done)
        {
            _status.Text = (Chinese ? "枚举中…… 剩 " : "enumerating… ")
                + $"{enumeration.Remaining}";
            Callable.From(Pump).CallDeferred();
            return;
        }

        // A newer request replaced the work while this chain was stepping: its
        // own chain owns the shelf, this one just stops.
        if (!ReferenceEquals(_enumeration, enumeration)) return;
        _enumeration = null;
        _stepping = false;
        _panorama = enumeration.Finish();
        RefreshContent();
    }

    private void SwitchMode(bool planner)
    {
        _plannerMode = planner;
        RefreshContent();
    }

    /// <summary>Renders a computed panorama directly, for the self-test.</summary>
    internal void RenderForTest(BlitzShopPanorama panorama)
    {
        _panorama = panorama;
        if (_inventory is not null) _fingerprint = FingerprintOf(_inventory);
        RefreshContent();
    }

    /// <summary>Flips to the wish page, for the self-test.</summary>
    internal void SwitchToPlannerForTest() => SwitchMode(true);

    /// <summary>Flips to the panorama page, for the self-test.</summary>
    internal void SwitchToPanoramaForTest() => SwitchMode(false);

    /// <summary>Wishes a good's first item chip, for the self-test.</summary>
    internal void WishForTest(int slot)
    {
        if (_panorama?.GoodAt(slot) is not { } good) return;
        var chip = ChipsOf(good).FirstOrDefault();
        if (chip is not null) ToggleWish(slot, chip);
    }

    /// <summary>
    /// Wishes the first chip of the first good that has any, for the
    /// self-test — deck-pick goods own no items until the player chooses.
    /// </summary>
    internal int WishFirstChippedGoodForTest()
    {
        if (_panorama is null) return -1;
        foreach (var good in _panorama.Goods)
        {
            var chip = ChipsOf(good).FirstOrDefault();
            if (chip is null) continue;
            ToggleWish(good.Slot, chip);
            return good.Slot;
        }
        return -1;
    }

    /// <summary>Rebuilds the visible page from the latest panorama and order.</summary>
    internal void RefreshContent()
    {
        // The chips this rebuild frees may be under the mouse; their exit
        // events die with them, which used to leave the item tip stuck on
        // screen until the next hover.
        BlitzShopItemTip.Hide();
        foreach (var child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }

        _title.Text = Chinese ? "极速商店 · 全景与规划" : "Blitz shop · panorama & planner";
        _collapse.Text = _expanded
            ? Chinese ? "收起" : "Hide"
            : Chinese ? "展开" : "Show";
        _panoramaTab.Text = Chinese ? "全景" : "Panorama";
        _plannerTab.Text = Chinese ? "求购" : "Wishes";

        if (_panorama is not { } panorama)
        {
            _panoramaTab.ButtonPressed = false;
            _plannerTab.ButtonPressed = false;
            return;
        }

        _panoramaTab.ButtonPressed = !_plannerMode;
        _plannerTab.ButtonPressed = _plannerMode;
        var chinese = Chinese;
        _status.Text = (chinese ? "可带走 " : "take ")
                       + panorama.ChoicesRemaining
                       + (chinese
                           ? " 件；①–⑥＝货架从左到右；每件内容由“之前买了哪几件”决定，与先后无关"
                           : " goods; ①–⑥ = shelf order; each good depends on which set went first, not their order");

        if (_plannerMode)
            RenderWishes(panorama, chinese);
        else
            RenderPanorama(panorama, chinese);
        RenderNotes(panorama, chinese);
    }

    // ---- Panorama page ----

    private void RenderPanorama(BlitzShopPanorama panorama, bool chinese)
    {
        var inventory = panorama.Inventory;
        for (var slot = 0; slot < inventory.BlitzEntries.Count; slot++)
        {
            var entry = inventory.BlitzEntries[slot];
            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
                new Color(0.06f, 0.12f, 0.14f, 0.85f), new Color(0.16f, 0.28f, 0.33f, 0.9f), 5));
            _list.AddChild(card);
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 8);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 8);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 5);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 5);
            card.AddChild(margin);
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 2);
            margin.AddChild(column);

            var heading = Glyph(slot) + " " + BlitzShopPlanText.ShortName(entry.MerchantEntryType, chinese);
            if (entry.IsRare) heading += chinese ? "（稀有）" : " (rare)";
            if (entry.IsCursed) heading += chinese ? "（诅咒）" : " (cursed)";
            if (!entry.IsStocked) heading += chinese ? "（已带走）" : " (taken)";
            column.AddChild(PreCombatPanelStyles.CreateLabel(heading, 15, StsColors.gold, bold: true));

            var good = panorama.GoodAt(slot);
            if (good is null || good.Outcomes.Count == 0)
            {
                column.AddChild(BodyLabel(
                    entry.IsStocked
                        ? chinese ? "这一件暂不可推算" : "not predictable right now"
                        : chinese ? "已被带走" : "already taken"));
                continue;
            }

            var direct = good.Direct!;
            var directText = OutcomeText(direct.Yield, chinese);
            column.AddChild(BodyLabel((chinese ? "直接买：" : "buy outright: ") + directText));

            // The remaining prefixes, grouped by what they actually hand over:
            // identical results share one line, dead combinations are named as
            // dead, and an avalanche of distinct results folds into a count.
            var unresolvedKey = "\0unresolved";
            var groups = good.Outcomes.Skip(1)
                .GroupBy(outcome => outcome.Yield.Resolved
                    ? OutcomeText(outcome.Yield, chinese)
                    : unresolvedKey)
                .ToList();
            var changed = groups
                .Where(group => group.Key != unresolvedKey && group.Key != directText)
                .OrderBy(group => group.First().PrefixSlots.Count)
                .ToList();
            var unresolved = groups.FirstOrDefault(group => group.Key == unresolvedKey);

            var shown = 0;
            foreach (var group in changed)
            {
                if (shown >= MaxAlternativeLines) break;
                column.AddChild(BodyLabel(PrefixLabel(group.Select(o => o.PrefixSlots).ToList(), chinese)
                    + OutcomeText(group.First().Yield, chinese)));
                shown++;
            }
            if (changed.Count > shown)
                column.AddChild(BodyLabel(chinese
                    ? $"……另有 {changed.Count - shown} 种不同结果，用“规划”页组合查看"
                    : $"...{changed.Count - shown} more distinct results; combine them in the planner"));
            if (changed.Count == 0 && unresolved is null)
                column.AddChild(BodyLabel(chinese
                    ? "先买哪几件都不会改变这一项"
                    : "no earlier purchase changes this one"));
            if (unresolved is not null)
                column.AddChild(BodyLabel(chinese
                    ? "另有 " + PrefixExamples(unresolved.Select(o => o.PrefixSlots).ToList(), chinese)
                    + " 等组合无法完成购买，无法推算"
                    : "combinations like " + PrefixExamples(unresolved.Select(o => o.PrefixSlots).ToList(), chinese)
                    + " cannot complete, so no answer"));
        }
    }

    /// <summary>"先买 X 后：" — one line for one group of prefixes sharing a result.</summary>
    private string PrefixLabel(IReadOnlyList<IReadOnlyList<int>> prefixes, bool chinese)
    {
        var examples = PrefixExamples(prefixes, chinese);
        if (prefixes.Count == 1)
            return chinese ? "先买 " + examples + " 后：" : "after " + examples + ": ";
        if (prefixes.Count == 2)
        {
            var second = DescribePrefix(prefixes[1], chinese);
            return chinese
                ? "先买 " + examples + " 或 " + second + " 后："
                : "after " + examples + " or " + second + ": ";
        }
        return chinese
            ? "先买 " + examples + $" 等 {prefixes.Count} 种组合 后："
            : "after " + examples + $" and {prefixes.Count - 1} more: ";
    }

    /// <summary>The first prefix of a group, e.g. "②＋④".</summary>
    private string PrefixExamples(IReadOnlyList<IReadOnlyList<int>> prefixes, bool chinese) =>
        DescribePrefix(prefixes[0], chinese);

    /// <summary>
    /// A prefix as shelf glyphs only ("③＋⑥") — the row headers ①–⑥ are on
    /// screen next to each good, so names would only make lines wrap.
    /// </summary>
    private string DescribePrefix(IReadOnlyList<int> prefix, bool chinese) =>
        prefix.Count == 0
            ? chinese ? "直接" : "outright"
            : string.Join(chinese ? "＋" : "+", prefix.Select(Glyph));

    internal static string OutcomeText(BlitzYieldForecast yield, bool chinese)
    {
        if (!yield.Resolved) return chinese ? "暂不可预测" : "no forecast";
        if (!yield.Purchased && !yield.NeedsCardPick)
            return chinese ? "（无法完成购买）" : "(cannot be purchased)";
        var text = BlitzYieldText.Render(yield, chinese);
        return text.Length == 0
            ? chinese ? "（无内容变化）" : "(nothing)"
            : BlitzShopYield.Compact(text);
    }

    // ---- Wish page (demand-oriented, Crystal-Ball cells) ----

    /// <summary>
    /// One obtainable thing, as a clickable chip: the exact card, potion,
    /// relic or curse a good can hand over under some earlier-purchase set.
    /// The chip, not a "3-pick" sentence, is what the player targets — hover
    /// shows the item's own card, exactly like the Crystal Ball predictor's
    /// cells. A transform result chip carries the sources that produce it:
    /// the result depends on the source's pool (character / curse / colorless
    /// read different pools at the same draw), so a wish must keep them.
    /// </summary>
    private sealed record Chip(string Key, ForecastItemDetails Detail,
        IReadOnlyList<string> TransformSources)
    {
        public string Label => Detail.Name + (Detail.IsUpgraded && !Detail.Name.EndsWith('+') ? "+" : "");

        public static Chip Of(ForecastItemDetails detail, IEnumerable<string>? transformSources = null) =>
            new(detail.Kind + "|" + detail.Name + "|" + detail.IsUpgraded, detail,
                (transformSources ?? []).ToList());
    }

    /// <summary>
    /// Every item any order of this visit can hand over through this good,
    /// deduped. Cards from reward screens, potions, relics, curses, and the
    /// results a transform can turn a picked card into — the union across all
    /// predecessor sets.
    /// </summary>
    private static List<Chip> ChipsOf(BlitzPlanGood good)
    {
        var chips = new Dictionary<string, (Chip Chip, List<string> Sources)>();
        void Add(ForecastItemDetails item, string? transformSource = null)
        {
            var probe = Chip.Of(item);
            if (!chips.TryGetValue(probe.Key, out var entry))
            {
                entry = (probe, []);
                chips[probe.Key] = entry;
            }
            if (transformSource is not null && !entry.Sources.Contains(transformSource))
                entry.Sources.Add(transformSource);
        }
        foreach (var outcome in good.Outcomes)
        {
            foreach (var screen in outcome.Yield.CardScreens)
                foreach (var item in screen) Add(item);
            foreach (var item in outcome.Yield.GainedCards) Add(item);
            foreach (var item in outcome.Yield.UpgradedCards) Add(item);
            foreach (var item in outcome.Yield.Potions) Add(item);
            foreach (var item in outcome.Yield.Relics) Add(item);
            foreach (var choice in outcome.Yield.TransformChoices)
                Add(choice.Result, SourceLabel(choice));
        }
        return chips.Values.Select(entry => entry.Chip with
        {
            TransformSources = entry.Sources
        }).ToList();
    }

    /// <summary>The source label as the paths display writes it.</summary>
    private static string SourceLabel(BlitzCardChoiceOutcome choice) =>
        choice.Source.Name + (choice.Source.IsUpgraded && !choice.Source.Name.EndsWith('+') ? "+" : "");

    private static bool ContainsItem(BlitzYieldForecast yield, Chip chip) =>
        chip.TransformSources.Count > 0
            ? yield.TransformChoices.Any(choice =>
                SameItem(choice.Result, chip.Detail)
                && chip.TransformSources.Contains(SourceLabel(choice)))
            : chip.Detail.Kind switch
            {
                ForecastItemKind.Potion => yield.Potions.Any(item => SameItem(item, chip.Detail)),
                ForecastItemKind.Relic => yield.Relics.Any(item => SameItem(item, chip.Detail)),
                _ => yield.CardScreens.SelectMany(screen => screen)
                    .Concat(yield.GainedCards)
                    .Concat(yield.UpgradedCards)
                    .Any(item => SameItem(item, chip.Detail))
            };

    private static bool SameItem(ForecastItemDetails left, ForecastItemDetails right) =>
        left.Kind == right.Kind && left.Name == right.Name && left.IsUpgraded == right.IsUpgraded;

    private void RenderWishes(BlitzShopPanorama panorama, bool chinese)
    {
        _list.AddChild(BodyLabel(chinese
            ? "点击你想要的物品（悬浮可看详情），窗口自动解算“按什么顺序点哪些货物”；①–⑥＝货架从左到右。"
            : "Click the item you want (hover for details); the click order solves itself. ①–⑥ = shelf left to right."));
        foreach (var good in panorama.Goods)
        {
            var cell = new PanelContainer();
            cell.AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
                new Color(0.06f, 0.12f, 0.14f, 0.85f), new Color(0.16f, 0.28f, 0.33f, 0.9f), 5));
            _list.AddChild(cell);
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 8);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 8);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 5);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 5);
            cell.AddChild(margin);
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            column.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 4);
            margin.AddChild(column);

            var entry = good.Entry;
            var heading = Glyph(good.Slot) + " " + BlitzShopPlanText.ShortName(entry.MerchantEntryType, chinese);
            if (entry.IsRare) heading += chinese ? "（稀）" : " (rare)";
            if (entry.IsCursed) heading += chinese ? "（咒）" : " (cursed)";
            column.AddChild(PreCombatPanelStyles.CreateLabel(heading, 15, StsColors.gold, bold: true));

            var chips = ChipsOf(good);
            if (chips.Count == 0)
            {
                column.AddChild(BodyLabel(chinese ? "（内容随你的选择变化，见悬浮或全景页）" : "(contents follow your pick; hover or see panorama)"));
                continue;
            }

            var flow = new HFlowContainer();
            flow.AddThemeConstantOverride(ThemeConstants.FlowContainer.HSeparation, 6);
            flow.AddThemeConstantOverride(ThemeConstants.FlowContainer.VSeparation, 6);
            column.AddChild(flow);
            foreach (var chip in chips)
                flow.AddChild(MakeChip(chip, good.Slot, clickable: true));

            if (good.Entry.MerchantEntryType == BlitzMerchantEntryType.CardTransform)
                RenderTransformPaths(good, column, chinese);
        }

        RenderWishSolution(panorama, chinese);
    }

    /// <summary>
    /// Every transform result this visit can produce and every way to reach
    /// it: which source card, from the library, an earlier reward screen or an
    /// earlier curse, with the shelf slot that provides the source.
    /// </summary>
    private void RenderTransformPaths(BlitzPlanGood good, VBoxContainer column, bool chinese)
    {
        var groups = good.Outcomes
            .SelectMany(outcome => outcome.Yield.TransformChoices)
            .GroupBy(choice => choice.Result.Name + (choice.Result.IsUpgraded && !choice.Result.Name.EndsWith('+') ? "+" : ""))
            .OrderByDescending(group => group.Count())
            .ToList();
        if (groups.Count == 0) return;

        column.AddChild(BodyLabel(chinese ? "变化结果与路径：" : "transform results and paths:"));
        // The same question every reader hits: why so few results after all
        // those purchases? Transform draws from its own stream, which nothing
        // in this shop advances, and every candidate reads the same draw —
        // only the self-exclusion and a different pool shift the answer.
        column.AddChild(BodyLabel(chinese
            ? "（先买什么都不改变结果：变化走独立的 Transformations 流，所有候选共用同一次抽取；只有“抽中自己被排除”的相邻错位、以及不同池子（无色/诅咒）会给出别的结果）"
            : "(Buying first changes nothing: transform reads its own stream once for every candidate; only self-exclusion offsets and different pools give other results)"));
        var shown = 0;
        foreach (var group in groups)
        {
            if (shown >= MaxAlternativeLines) break;
            var paths = group.Select(choice => PathLabel(choice, chinese)).Distinct().ToList();
            var extra = Math.Max(0, paths.Count - 3);
            column.AddChild(BodyLabel(
                "· " + group.Key + (chinese ? " ← " : " <- ")
                + string.Join(chinese ? "／" : " / ", paths.Take(3))
                + (extra > 0
                    ? chinese ? $" 等 {paths.Count} 条路径" : $" (+{extra} more paths)"
                    : "")));
            shown++;
        }
        if (groups.Count > shown)
            column.AddChild(BodyLabel(chinese
                ? $"……另有 {groups.Count - shown} 种结果未列出"
                : $"...{groups.Count - shown} more results not listed"));
    }

    private static string PathLabel(BlitzCardChoiceOutcome choice, bool chinese)
    {
        var source = choice.Source.Name
            + (choice.Source.IsUpgraded && !choice.Source.Name.EndsWith('+') ? "+" : "");
        var via = string.Join(string.Empty, choice.ViaSlots.Select(Glyph));
        return choice.Origin switch
        {
            "reward" => source + (chinese ? $"（{via} 奖励牌）" : $" ({via} reward)"),
            "curse" => source + (chinese ? $"（{via} 诅咒）" : $" ({via} curse)"),
            _ => source + (chinese ? "（牌库）" : " (deck)")
        };
    }

    /// <summary>
    /// One item chip: a single Button drawing its own stylebox (the map
    /// panels' proven control — one rect for visuals, hover and clicks, no
    /// container/child layering to drift), rarity-coloured, hover showing the
    /// item's own information card next to the chip, click targeting the wish.
    /// </summary>
    private Button MakeChip(Chip chip, int slot, bool clickable)
    {
        var selected = _wishes.TryGetValue(slot, out var wished) && wished.ChipKey == chip.Key;
        var button = PreCombatPanelStyles.CreateButton((selected ? "★ " : "") + chip.Label, 0f);
        button.ToggleMode = clickable;
        button.ButtonPressed = selected;
        // Rarity colour over the shared button styles.
        button.AddThemeColorOverride("font_color", ColorOf(chip.Detail.Rarity));
        button.AddThemeColorOverride("font_pressed_color", ColorOf(chip.Detail.Rarity));
        button.AddThemeColorOverride("font_hover_color", ColorOf(chip.Detail.Rarity));
        button.AddThemeStyleboxOverride("normal", PreCombatPanelStyles.CreateButtonStyle(
            selected ? new Color(0.14f, 0.19f, 0.1f, 0.98f) : new Color(0.07f, 0.13f, 0.15f, 0.95f),
            selected ? StsColors.gold : new Color(0.2f, 0.34f, 0.4f, 0.95f)));
        button.AddThemeStyleboxOverride("hover", PreCombatPanelStyles.CreateButtonStyle(
            new Color(0.1f, 0.17f, 0.19f, 0.98f), StsColors.gold));
        button.AddThemeStyleboxOverride("pressed", PreCombatPanelStyles.CreateButtonStyle(
            new Color(0.14f, 0.19f, 0.1f, 0.98f), StsColors.gold));

        button.MouseEntered += () => BlitzShopItemTip.Show(chip.Detail, button);
        button.MouseExited += BlitzShopItemTip.Hide;
        if (clickable)
        {
            var slotCopy = slot;
            var chipCopy = chip;
            button.Pressed += () => ToggleWish(slotCopy, chipCopy);
        }
        return button;
    }

    private static Color ColorOf(ForecastItemRarity rarity) => rarity switch
    {
        ForecastItemRarity.Uncommon => new Color("64FFFF"),
        ForecastItemRarity.Rare or ForecastItemRarity.Ancient => new Color("FFDA36"),
        ForecastItemRarity.Shop => new Color("F6A94A"),
        ForecastItemRarity.Event => new Color("52D273"),
        ForecastItemRarity.Curse => new Color("E669FF"),
        ForecastItemRarity.Quest => new Color("F46836"),
        _ => StsColors.cream
    };

    private void ToggleWish(int slot, Chip chip)
    {
        if (_wishes.TryGetValue(slot, out var current) && current.ChipKey == chip.Key)
            _wishes.Remove(slot);
        else
            _wishes[slot] = new BlitzWishKey(chip.Key, chip.TransformSources);
        RefreshContent();
    }

    private void RenderWishSolution(BlitzShopPanorama panorama, bool chinese)
    {
        _list.AddChild(new HSeparator());
        if (_wishes.Count > 0)
        {
            var clear = PreCombatPanelStyles.CreateButton(chinese ? "清空愿望" : "Clear wishes", 130f);
            clear.Pressed += () =>
            {
                _wishes.Clear();
                RefreshContent();
            };
            _list.AddChild(clear);
        }

        var wishes = new List<BlitzShopPlanService.BlitzWish>();
        foreach (var (slot, wish) in _wishes)
        {
            var good = panorama.GoodAt(slot);
            if (good is null) continue;
            var chip = ChipsOf(good).FirstOrDefault(candidate => candidate.Key == wish.ChipKey);
            if (chip is null) continue;
            var allowed = good.Outcomes
                .Where(outcome => ContainsItem(outcome.Yield, chip))
                .Select(outcome => outcome.PrefixSlots).ToList();
            if (allowed.Count > 0) wishes.Add(new BlitzShopPlanService.BlitzWish(slot, allowed));
        }

        if (wishes.Count == 0)
        {
            if (_wishes.Count > 0)
                _list.AddChild(BodyLabel(chinese
                    ? "所选物品在当前货架上已不存在（可能已被买走），请重新选择。"
                    : "A chosen item no longer exists on this shelf (it may be taken); pick again."));
            return;
        }

        var solution = BlitzShopPlanService.Solve(panorama, wishes);
        if (!solution.Solved)
        {
            var reason = solution.Reason switch
            {
                "OutOfPicks" => chinese
                    ? "无解：想要的物品需要的购买次数超过剩余可带走次数。"
                    : "No order: the wishes need more purchases than picks remain.",
                "Unavailable" => chinese
                    ? "无解：有货物已被带走。"
                    : "No order: one of the goods is already taken.",
                _ => chinese
                    ? "无解：这些愿望要求的先后顺序互相冲突。"
                    : "No order: the wishes demand conflicting orders."
            };
            _list.AddChild(BodyLabel(reason));
            return;
        }

        // The Crystal-Ball plan line: a header with the click count, then the
        // route as glyphs, then one row of item chips per step.
        _list.AddChild(PreCombatPanelStyles.CreateLabel(
            (chinese ? "计划：" : "plan: ") + solution.Steps.Count
            + (chinese ? " 次点击" : " clicks"), 15, StsColors.gold, bold: true));
        _list.AddChild(BodyLabel(string.Join(" → ", solution.Steps.Select(step => Glyph(step.Slot)))));
        for (var index = 0; index < solution.Steps.Count; index++)
        {
            var step = solution.Steps[index];
            var prefix = solution.Steps.Take(index).Select(previous => previous.Slot).ToArray();
            var where = prefix.Length == 0
                ? chinese ? "直接买" : "buy outright"
                : chinese
                    ? "在 " + DescribePrefix(prefix, chinese) + " 后"
                    : "after " + DescribePrefix(prefix, chinese);
            var mark = _wishes.ContainsKey(step.Slot) ? "  ✓" : chinese ? "　（垫位）" : "  (filler)";
            // A wished transform result only comes from certain source cards
            // (the pool differs per source); the plan must name the one to pick.
            var pick = _wishes.TryGetValue(step.Slot, out var wished)
                       && step.Outcome.Yield.TransformChoices.Count > 0
                ? step.Outcome.Yield.TransformChoices
                    .Where(choice => wished.TransformSources.Contains(SourceLabel(choice)))
                    .Select(SourceLabel)
                    .FirstOrDefault()
                : null;
            var pickNote = pick is null
                ? ""
                : chinese ? $"（选 {pick} 去变化）" : $" (pick {pick} to transform)";
            var stepRow = new HBoxContainer();
            stepRow.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 6);
            _list.AddChild(stepRow);
            stepRow.AddChild(BodyLabel($"{index + 1}. {Glyph(step.Slot)} {where}{pickNote}{mark}"));
            var flow = new HFlowContainer
            {
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            flow.AddThemeConstantOverride(ThemeConstants.FlowContainer.HSeparation, 4);
            stepRow.AddChild(flow);
            foreach (var chip in ChipsOfYield(step.Outcome.Yield))
                flow.AddChild(MakeChip(chip, step.Slot, clickable: false));
        }
        _list.AddChild(BodyLabel(chinese
            ? $"剩余可带走 {solution.ChoicesLeft} 件"
            : $"{solution.ChoicesLeft} picks left"));
    }

    /// <summary>The items one step hands over, as hoverable chips.</summary>
    private static List<Chip> ChipsOfYield(BlitzYieldForecast yield) => yield.CardScreens
        .SelectMany(screen => screen)
        .Concat(yield.GainedCards)
        .Concat(yield.UpgradedCards)
        .Concat(yield.Potions)
        .Concat(yield.Relics)
        .GroupBy(item => item.Kind + "|" + item.Name + "|" + item.IsUpgraded)
        .Select(group => Chip.Of(group.First()))
        .ToList();

    // ---- Shared bits ----

    private void RenderNotes(BlitzShopPanorama panorama, bool chinese)
    {
        if (panorama.Notes.Count == 0) return;
        var lines = new List<string>();
        foreach (var note in panorama.Notes)
        {
            lines.Add(note switch
            {
                "DeckPickCompletion" => chinese
                    ? "· 需要选牌的前序货物按“自动选第一张候选”补全：选哪张不改变随机数推进，只改变你自己的结果"
                    : "· Deck-pick predecessors are completed with the first candidate: the pick's identity moves no stream",
                "RandomUpgradesOrder" => chinese
                    ? "· “自动升级”会洗整副牌库：若前面买过卡牌奖励并实际领牌，它的两张升级可能与此不同"
                    : "· Random upgrades shuffle the deck: taking earlier card rewards can change its two upgrades",
                "RewardTransform" => chinese
                    ? "· 变化表含前序卡牌奖励的候选牌：先从奖励里拿它，再送去变化即可得到所列结果"
                    : "· The transform table includes cards earlier rewards offered: take one from the reward, then transform it",
                "TwoMoreChoices" => chinese
                    ? "· “多加2次选择”本身花一次机会再返还两次：先买它可以让后面多带两件"
                    : "· Two more choices spends one pick and returns two: buying it early extends the visit",
                _ => note
            });
        }
        _list.AddChild(BodyLabel(string.Join("\n", lines)));
    }

    private static Label BodyLabel(string text)
    {
        var label = PreCombatPanelStyles.CreateLabel(text, 13, StsColors.cream);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    internal static string Glyph(int slot) => slot >= 0 && slot < Glyphs.Length ? Glyphs[slot] : slot.ToString();
}

/// <summary>Short, stable labels for the twenty good types, shared by both pages.</summary>
internal static class BlitzShopPlanText
{
    internal static string ShortName(BlitzMerchantEntryType type, bool chinese) => chinese
        ? type switch
        {
            BlitzMerchantEntryType.CardUpgrade => "升级一张牌",
            BlitzMerchantEntryType.UpgradedCardReward => "升级卡牌奖励",
            BlitzMerchantEntryType.RandomUpgrades => "自动升级×2",
            BlitzMerchantEntryType.CardTransform => "变化",
            BlitzMerchantEntryType.CardRemoval => "删牌",
            BlitzMerchantEntryType.CardRewards => "卡牌奖励×2",
            BlitzMerchantEntryType.ColorlessCardReward => "无色卡牌",
            BlitzMerchantEntryType.Potions => "药水×2",
            BlitzMerchantEntryType.CardEnchant => "附魔",
            BlitzMerchantEntryType.EnchantedCardReward => "附魔卡牌奖励",
            BlitzMerchantEntryType.PrismaticCardReward => "棱镜卡牌",
            BlitzMerchantEntryType.RareCardReward => "稀有卡牌",
            BlitzMerchantEntryType.UpgradedRareCard => "升级稀有卡",
            BlitzMerchantEntryType.Relic => "遗物",
            BlitzMerchantEntryType.Glam => "格拉姆附魔",
            BlitzMerchantEntryType.TwoMoreChoices => "多加2次选择",
            BlitzMerchantEntryType.CardRewardOfEachRarity => "各稀有度卡牌",
            BlitzMerchantEntryType.UpgradedRareCardReward => "升级稀有卡奖励",
            BlitzMerchantEntryType.TwoRelics => "遗物×2",
            BlitzMerchantEntryType.FullHealAndMaxHp => "满血+最大生命",
            _ => type.ToString()
        }
        : type switch
        {
            BlitzMerchantEntryType.CardUpgrade => "upgrade a card",
            BlitzMerchantEntryType.UpgradedCardReward => "upgraded reward",
            BlitzMerchantEntryType.RandomUpgrades => "random upgrades",
            BlitzMerchantEntryType.CardTransform => "transform",
            BlitzMerchantEntryType.CardRemoval => "remove a card",
            BlitzMerchantEntryType.CardRewards => "card rewards ×2",
            BlitzMerchantEntryType.ColorlessCardReward => "colorless cards",
            BlitzMerchantEntryType.Potions => "potions ×2",
            BlitzMerchantEntryType.CardEnchant => "enchant",
            BlitzMerchantEntryType.EnchantedCardReward => "enchanted reward",
            BlitzMerchantEntryType.PrismaticCardReward => "prismatic cards",
            BlitzMerchantEntryType.RareCardReward => "rare cards",
            BlitzMerchantEntryType.UpgradedRareCard => "upgraded rare card",
            BlitzMerchantEntryType.Relic => "relic",
            BlitzMerchantEntryType.Glam => "glam enchant",
            BlitzMerchantEntryType.TwoMoreChoices => "two more choices",
            BlitzMerchantEntryType.CardRewardOfEachRarity => "card of each rarity",
            BlitzMerchantEntryType.UpgradedRareCardReward => "upgraded rare reward",
            BlitzMerchantEntryType.TwoRelics => "relics ×2",
            BlitzMerchantEntryType.FullHealAndMaxHp => "full heal + max HP",
            _ => type.ToString()
        };
}

internal static class BlitzShopPlanPanel
{
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);
    private static BlitzShopPlanPanelControl? _currentPanel;
    private static NMerchantInventory? _currentNode;
    private static bool _isExpanded = true;

    private static bool IsUsable(Node? node) =>
        node is not null && GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion();

    private static void DetachStaleReferences()
    {
        if (IsUsable(_currentPanel)) _currentPanel!.Unbind();
        _currentPanel = null;
        _currentNode = null;
    }

    internal static void RememberExpanded(bool expanded) => _isExpanded = expanded;

    /// <summary>
    /// Called when a shop inventory opens: attaches the window to Blitz shelves
    /// and hides it everywhere else. While a Blitz shelf is open the window is
    /// always on screen — folded down to its title bar at worst — so it cannot
    /// be lost; no external toggle button is involved.
    /// </summary>
    internal static void RefreshSafely(NMerchantInventory? screen)
    {
        try
        {
            if ((_currentPanel is not null && !IsUsable(_currentPanel))
                || (_currentNode is not null && !IsUsable(_currentNode)))
            {
                DetachStaleReferences();
            }

            if (screen is not null) _currentNode = screen;
            if (!SeedOracleData.Settings.ShowBlitzShopPanel)
            {
                HideSafely();
                return;
            }

            var node = _currentNode;
            var inventory = node?.Inventory;
            if (node is null || inventory is null || !inventory.IsBlitzShop
                || inventory.Player.RunState is not RunState run || run.GameMode != GameMode.Blitz)
            {
                HideSafely();
                return;
            }

            var panel = node.GetNodeOrNull<BlitzShopPlanPanelControl>(BlitzShopPlanPanelControl.NodeName);
            if (!IsUsable(panel)) panel = null;
            if (panel is null)
            {
                panel = new BlitzShopPlanPanelControl();
                node.AddChild(panel);
            }

            _currentPanel = panel;
            panel.Bind(inventory);
            panel.Visible = true;
            panel.ApplyExpansion(_isExpanded);
            // The shop scene's rect is not guaranteed to match the viewport;
            // clamp the window inside it once layout has settled.
            Callable.From(() => ClampToViewport(panel)).CallDeferred();
            // A beat of delay keeps the shop's own open animation off the same
            // frame as the snapshot build; the fingerprint check makes the
            // timer free when the shelf did not change.
            if (_isExpanded) panel.ScheduleRecompute(0.2f);
        }
        catch (Exception exception)
        {
            ReportFailure("refresh", exception);
        }
    }

    /// <summary>Re-checks the shop the window is already bound to (after a purchase).</summary>
    internal static void RefreshCurrentSafely()
    {
        if (_currentNode is null && _currentPanel is null) return;
        RefreshSafely(null);
    }

    /// <summary>
    /// The hover answer from the panorama table: instant, with no clone
    /// purchases. Valid only while the shelf matches the fingerprint the
    /// panorama was priced against; the caller falls back to the probe
    /// otherwise (shop entry, pending recompute, folded window). The probe is
    /// a full run round-trip per hover and the prime suspect for the second
    /// shop's native crash.
    /// </summary>
    internal static bool TryDescribeRow(MerchantInventory inventory, int index, out string text)
    {
        text = string.Empty;
        if (!IsUsable(_currentPanel) || !ReferenceEquals(_currentPanel!.BoundInventory, inventory))
            return false;
        return _currentPanel.TryDescribeRowFrom(inventory, index, out text);
    }

    /// <summary>Whether the window's enumeration is still in flight; while it
    /// is, hovers wait for the table instead of probing.</summary>
    internal static bool PanoramaPending =>
        IsUsable(_currentPanel) && _currentPanel!.IsPricing;

    /// <summary>
    /// The yield the window is currently promising for a good bought after
    /// exactly this predecessor set — the purchase audit's reference. Null
    /// when no window or no such row is on screen.
    /// </summary>
    internal static BlitzYieldForecast? CurrentRow(int slot, IReadOnlyCollection<int> prefix)
    {
        if (!IsUsable(_currentPanel) || _currentPanel!.CurrentPanorama is not { } panorama)
            return null;
        return panorama.GoodAt(slot)?.ForPrefix(prefix)?.Yield;
    }

    /// <summary>
    /// Whether a shop window is on screen right now — the cheap gate that
    /// keeps the global card-changed hook from posting during combats.
    /// </summary>
    internal static bool HasOpenWindow => IsUsable(_currentPanel) && _currentPanel!.Visible;

    /// <summary>
    /// Called from the game's card-changed hook on every pile move anywhere;
    /// only an open shop window cares, and the fingerprint check inside makes
    /// the refresh free when the deck the window prices against did not move.
    /// </summary>
    internal static void OnDeckMaybeChanged()
    {
        if (!HasOpenWindow) return;
        SeedOracleDispatcher.Post(RefreshCurrentSafely);
    }

    /// <summary>
    /// Hides the window without detaching, for closed shops and turned-off
    /// settings. The remembered expansion survives: hiding because this shop
    /// is not a Blitz shelf is not the player saying "fold it next time".
    /// </summary>
    internal static void HideSafely()
    {
        try
        {
            if (IsUsable(_currentPanel)) _currentPanel!.Visible = false;
        }
        catch (Exception exception)
        {
            ReportFailure("hide", exception);
        }
    }

    internal static void ApplySettingSafely()
    {
        if (!SeedOracleData.Settings.ShowBlitzShopPanel) HideSafely();
        else RefreshSafely(null);
    }

    /// <summary>
    /// Pulls the window back inside the visible viewport: the shop inventory's
    /// own rect can extend past the screen, and an anchored child follows it
    /// off-screen (the first cut-off screenshots).
    /// </summary>
    private static void ClampToViewport(BlitzShopPlanPanelControl panel)
    {
        try
        {
            if (!IsUsable(panel)) return;
            var visible = panel.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
            if (visible == Vector2.Zero) return;
            var rect = panel.GetGlobalRect();
            var overflowRight = rect.End.X - (visible.X - 6f);
            if (overflowRight > 0) panel.OffsetRight -= overflowRight;
            var overflowBottom = rect.End.Y - (visible.Y - 6f);
            if (overflowBottom > 0) panel.OffsetBottom -= overflowBottom;
        }
        catch (Exception exception)
        {
            ReportFailure("clamp", exception);
        }
    }

    private static void ReportFailure(string operation, Exception exception)
    {
        var root = exception.GetBaseException();
        var key = $"{operation}:{root.GetType().FullName}:{root.Message}";
        if (ReportedFailures.Add(key))
            Entry.Logger.Error($"Blitz shop plan window {operation} failed: {root}");
    }
}

/// <summary>
/// The item information card a chip hover shows, in the Crystal-Ball
/// predictor's style: a small panel next to the mouse with the item's cost,
/// name, type and full description — the game's own rendering paths, never a
/// re-implementation of the text.
/// </summary>
internal static class BlitzShopItemTip
{
    private static PanelContainer? _panel;
    private static Control? _host;
    private static CanvasLayer? _layer;

    /// <summary>
    /// The tip lives on its own canvas layer, parented to the window: a layer
    /// is a plain Node, so no container ever stretches it and it draws above
    /// the whole shop. Parenting it to the PanelContainer itself re-laid-out
    /// the window on every hover, flickering between enter/exit until the
    /// process died.
    /// </summary>
    internal static void BindParent(Control host)
    {
        _host = host;
        if (_layer is null || !GodotObject.IsInstanceValid(_layer))
        {
            _layer = new CanvasLayer { Layer = 95 };
            host.AddChild(_layer);
        }
    }

    internal static void Show(ForecastItemDetails item, Control anchor)
    {
        try
        {
            if (_host is null || !GodotObject.IsInstanceValid(_host)
                || _layer is null || !GodotObject.IsInstanceValid(_layer))
                return;
            var content = Build(item, _host);
            if (content is null) return;
            Hide();
            var panel = new PanelContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            panel.AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
                new Color(0.02f, 0.05f, 0.06f, 0.98f), new Color(0.3f, 0.5f, 0.55f, 0.95f), 8));
            var margin = new MarginContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 10);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 10);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 8);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 8);
            panel.AddChild(margin);
            margin.AddChild(content);

            // Beside the chip, in the chip's own engine geometry — never the
            // mouse, whose coordinate space under stretched viewports is a
            // separate question. Right of the chip when it fits, left when
            // not, clamped into the viewport.
            var viewport = _host.GetViewport().GetVisibleRect().Size;
            var chip = anchor.GetGlobalRect();
            var right = chip.End.X + 12f;
            var at = right + 440f <= viewport.X - 8f
                ? new Vector2(right, chip.Position.Y)
                : new Vector2(MathF.Max(8f, chip.Position.X - 452f), chip.Position.Y);
            panel.Position = new Vector2(
                Mathf.Clamp(at.X, 8f, MathF.Max(8f, viewport.X - 440f)),
                Mathf.Clamp(at.Y, 8f, MathF.Max(8f, viewport.Y - 320f)));
            _layer.AddChild(panel);
            _panel = panel;
        }
        catch (Exception)
        {
            // A tooltip must never take the window down with it.
        }
    }

    internal static void Hide()
    {
        var panel = _panel;
        _panel = null;
        if (panel is null || !GodotObject.IsInstanceValid(panel)) return;
        panel.GetParent()?.RemoveChild(panel);
        panel.QueueFree();
    }

    private static Control? Build(ForecastItemDetails item, Control themeSource)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 5);
        switch (item.Kind)
        {
            case ForecastItemKind.Card when item.Id is { } id:
            {
                var card = ModelDb.GetById<CardModel>(id);
                if (card is null) return null;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 8);
                var cost = new Label
                {
                    Text = card.EnergyCost?.Canonical.ToString() ?? "?",
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                cost.AddThemeColorOverride(ThemeConstants.Label.FontColor, Colors.White);
                cost.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 18);
                cost.CustomMinimumSize = new Vector2(26f, 26f);
                var costBox = new StyleBoxFlat { BgColor = new Color(0.082f, 0.094f, 0.149f) };
                costBox.SetCornerRadiusAll(13);
                cost.AddThemeStyleboxOverride("normal", costBox);
                row.AddChild(cost);
                var name = new Label
                {
                    Text = item.Name + (item.IsUpgraded && !item.Name.EndsWith('+') ? "+" : "")
                };
                name.AddThemeColorOverride(ThemeConstants.Label.FontColor,
                    item.IsUpgraded ? StsColors.green : StsColors.cream);
                name.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 15);
                row.AddChild(name);
                column.AddChild(row);

                var type = new Label { Text = card.Type.ToString() + (item.IsUpgraded ? "  ⬆" : "") };
                type.AddThemeColorOverride(ThemeConstants.Label.FontColor, StsColors.lightGray);
                type.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 11);
                column.AddChild(type);

                var description = DescribeCard(card, item.IsUpgraded);
                if (!string.IsNullOrEmpty(description))
                    column.AddChild(MegaLabel(description, themeSource));
                return column;
            }
            case ForecastItemKind.Potion when item.Id is { } id:
            {
                var potion = ModelDb.GetById<PotionModel>(id);
                if (potion is null) return null;
                AddTitle(column, item.Name, StsColors.aqua);
                var text = potion.DynamicDescription?.GetFormattedText();
                if (!string.IsNullOrEmpty(text) && text.Length > 2)
                    column.AddChild(MegaLabel(text, themeSource));
                return column;
            }
            case ForecastItemKind.Relic when item.Id is { } id:
            {
                var relic = ModelDb.GetById<RelicModel>(id);
                if (relic is null) return null;
                AddTitle(column, item.Name, StsColors.gold);
                var rarity = new Label { Text = relic.Rarity.ToString() };
                rarity.AddThemeColorOverride(ThemeConstants.Label.FontColor, StsColors.orange);
                rarity.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 11);
                column.AddChild(rarity);
                var text = relic.DynamicDescription?.GetFormattedText();
                if (!string.IsNullOrEmpty(text)) column.AddChild(MegaLabel(text, themeSource));
                return column;
            }
            default:
                AddTitle(column, item.Name, StsColors.cream);
                return column;
        }
    }

    private static string? DescribeCard(CardModel card, bool upgraded)
    {
        try
        {
            var mutable = card.ToMutable();
            if (upgraded && mutable.IsUpgradable)
            {
                CardCmd.Upgrade(mutable, CardPreviewStyle.None);
                return mutable.GetDescriptionForUpgradePreview();
            }
            return mutable.GetDescriptionForPile(PileType.Deck, null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void AddTitle(VBoxContainer column, string text, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride(ThemeConstants.Label.FontColor, color);
        label.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 15);
        column.AddChild(label);
    }

    /// <summary>
    /// The game's rich-text label with the theme font override its own _Ready
    /// asserts — without it every hover threw "no theme font override" from
    /// the native bridge, and the repeated throws took the process down.
    /// </summary>
    private static Control MegaLabel(string text, Control themeSource)
    {
        var label = new MegaRichTextLabel
        {
            Text = text,
            CustomMinimumSize = new Vector2(400f, 0f)
        };
        label.BbcodeEnabled = true;
        label.FitContent = true;
        label.ScrollActive = false;
        label.AutoSizeEnabled = false;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        label.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.NormalFontSize, 12);
        var themeFont = themeSource.GetThemeFont(ThemeConstants.Label.Font, "Label");
        if (themeFont is null)
        {
            // No theme context to borrow a font from: a plain Label cannot
            // trip the mega-text assert, so degrade instead of crashing.
            var fallback = new Label
            {
                Text = text,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(400f, 0f)
            };
            fallback.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 12);
            return fallback;
        }
        label.AddThemeFontOverride(ThemeConstants.RichTextLabel.NormalFont, themeFont);
        return label;
    }
}

// The shop screen opens its inventory panel here; the window follows the same
// lifecycle so it only ever exists while the shelf is on screen.
[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Open))]
internal static class NMerchantInventoryOpenBlitzPlanPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMerchantInventory __instance) =>
        BlitzShopPlanPanel.RefreshSafely(__instance);
}

[HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Close))]
internal static class NMerchantInventoryCloseBlitzPlanPatch
{
    [HarmonyPostfix]
    private static void Postfix() => BlitzShopPlanPanel.HideSafely();
}

// Reward-screen picks never touch a merchant entry: without this hook the
// window would keep pricing deck-reading rows against the deck as it stood
// at shop entry, drifting further with every card the player takes.
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardChangedPiles))]
internal static class HookAfterCardChangedPilesBlitzPlanPatch
{
    [HarmonyPostfix]
    private static void Postfix() => BlitzShopPlanPanel.OnDeckMaybeChanged();
}
