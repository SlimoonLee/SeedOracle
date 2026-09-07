using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using SeedOracle.Forecasting;
using SeedOracle.Validation;

namespace SeedOracle.UI;

internal sealed partial class RoutePlanPanelControl : PanelContainer
{
    internal const string NodeName = "SeedOracleRoutePlanPanel";
    internal const int PanelZIndex = 160;

    private readonly Label _status;
    private readonly Label _ledger;
    private readonly VBoxContainer _planList;
    private readonly ScrollContainer _scroll;
    private NMapScreen? _screen;
    private RoutePlanForecastService? _planForecasts;
    private readonly PlanningEventPredictionService _planEventPredictions = new();
    private RunState? _lastRun;
    private RoutePlanForecastService.PlanChain? _lastChain;
    private string? _stateToken;
    private long _planRevision;

    public RoutePlanPanelControl()
    {
        Name = NodeName;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ZIndex = PanelZIndex;
        SetAnchorsPreset(LayoutPreset.LeftWide);
        OffsetLeft = 12f;
        OffsetTop = 96f;
        OffsetRight = 400f;
        OffsetBottom = -96f;
        Visible = false;
        AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
            new Color(0.025f, 0.07f, 0.085f, 0.97f),
            new Color(0.75f, 0.62f, 0.22f, 0.95f),
            9));

        var outerMargin = new MarginContainer { MouseFilter = MouseFilterEnum.Pass };
        outerMargin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 12);
        outerMargin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 12);
        outerMargin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 10);
        outerMargin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 10);
        AddChild(outerMargin);

        var column = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        column.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 7);
        outerMargin.AddChild(column);

        var header = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        header.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 8);
        column.AddChild(header);
        header.AddChild(PreCombatPanelStyles.CreateLabel("全知规划", 24, StsColors.gold, bold: true));

        var spacer = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        header.AddChild(spacer);

        var clearButton = PreCombatPanelStyles.CreateButton("清除", 64f);
        clearButton.TooltipText = "清除整个计划";
        clearButton.Pressed += () =>
        {
            RoutePlanTracker.Clear();
            RefreshPlan();
        };
        header.AddChild(clearButton);

        var closeButton = PreCombatPanelStyles.CreateButton("×", 34f);
        closeButton.TooltipText = "关闭面板（计划保留）";
        closeButton.Pressed += Collapse;
        header.AddChild(closeButton);

        _status = PreCombatPanelStyles.CreateLabel(string.Empty, 18, StsColors.cream);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddChild(_status);

        var scroll = new ScrollContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        column.AddChild(scroll);
        _scroll = scroll;

        _planList = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _planList.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 6);
        scroll.AddChild(_planList);

        _ledger = PreCombatPanelStyles.CreateLabel(string.Empty, 17, StsColors.cream);
        _ledger.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _ledger.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddChild(_ledger);
    }

    /// <summary>
    /// Pins the plan list to its end for a few frames: rebuilding rows and
    /// RichTextLabel text parsing grow the content over several frames, so a
    /// single deferred scroll lands short.
    /// </summary>
    public void ScrollToBottom()
    {
        var tree = _scroll.GetTree();
        if (tree is null)
            return;

        var frames = 3;
        void OnFrame()
        {
            if (!GodotObject.IsInstanceValid(_scroll))
            {
                tree.ProcessFrame -= OnFrame;
                return;
            }

            _scroll.ScrollVertical = (int)_scroll.GetVScrollBar().MaxValue;
            frames--;
            if (frames <= 0)
                tree.ProcessFrame -= OnFrame;
        }

        tree.ProcessFrame += OnFrame;
    }

    private static bool Chinese => LocManager.Instance?.Language is "zhs" or "zht";

    public void Collapse()
    {
        Visible = false;
        var screen = _screen;
        if (screen is not null
            && GodotObject.IsInstanceValid(screen)
            && screen._runState is { } run)
        {
            RoutePlanOverlay.Refresh(screen, run);
        }
    }

    public void Configure(NMapScreen screen)
    {
        _screen = screen;
    }

    /// <summary>Shows a transient hint/error line above the plan rows.</summary>
    public void ShowHint(string? message, bool error)
    {
        _status.Text = message ?? HintForCurrentPlan();
        _status.AddThemeColorOverride(
            ThemeConstants.Label.FontColor,
            error ? new Color(1f, 0.42f, 0.36f) : StsColors.cream);
    }

    private string HintForCurrentPlan()
    {
        var plan = RoutePlanTracker.Current;
        return plan switch
        {
            null => Chinese
                ? "规划模式：点击房间按顺序纳入规划（不会实际进入）。点击计划末端房间可截断；关闭面板后恢复正常移动。"
                : "Plan mode: click rooms in order to plan them (you will not enter them). Click the last planned room to truncate. Close the panel to travel normally.",
            { Phase: RoutePlanPhase.Void } => VoidText(plan),
            _ => Chinese
                ? "点击房间继续追加；点击计划末端房间可截断。关闭面板后点击可进入房间才会实际前进。"
                : "Click rooms to append; click the last planned room to truncate. Close the panel to travel again."
        };
    }

    private static string VoidText(RoutePlan plan) => plan.VoidReasonKey switch
    {
        "act_changed" => "计划已作废：进入了新的一幕。",
        _ => "计划已作废：实际进入的楼层与计划不符。"
    };

    public void RefreshPlan()
    {
        _planRevision++;
        var screen = _screen;
        if (screen is null || !GodotObject.IsInstanceValid(screen))
            return;
        var run = screen._runState;
        if (run is null)
            return;

        var chinese = Chinese;
        var plan = RoutePlanTracker.Current;
        ShowHint(null, error: false);

        foreach (var child in _planList.GetChildren())
            child.QueueFree();

        var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        if (plan is null)
        {
            _eventOutcomes.Clear();
            _ledger.Text = FormatCurrentResources(player, chinese);
            return;
        }

        PruneEventOutcomes(plan);

        _lastRun = run;
        _lastChain = null;
        if (plan.Phase == RoutePlanPhase.Active && player is not null)
        {
            _planForecasts ??= new RoutePlanForecastService(new PlanningPredictionService());
            try
            {
                _lastChain = PredictionPurityGuard.Execute(
                    run,
                    "plan-chain",
                    () => _planForecasts.BuildChain(
                        run,
                        player,
                        plan.Entries,
                        entry => RoutePlanTracker.FindMapPoint(run, entry.Coord),
                        plan,
                        PlannedEventOutcomes()));
            }
            catch (Exception exception)
            {
                Entry.Logger.Error($"Plan chain threading failed: {exception}");
            }
        }

        if (plan.Phase == RoutePlanPhase.Void)
        {
            _planList.AddChild(PreCombatPanelStyles.CreateLabel(
                VoidText(plan),
                14,
                new Color(1f, 0.42f, 0.36f),
                bold: true));
        }

        var completed = plan.Entries.Where(entry => entry.IsCompleted).ToArray();
        if (completed.Length > 0)
        {
            _planList.AddChild(PreCombatPanelStyles.CreateLabel(
                chinese ? "已完成（实际所得）" : "Completed (actual)",
                18,
                new Color(0.55f, 0.85f, 0.62f),
                bold: true));
            foreach (var entry in completed)
                _planList.AddChild(BuildCompletedRow(run, entry, chinese));
        }

        var remaining = plan.Entries.Where(entry => !entry.IsCompleted).ToArray();
        if (remaining.Length > 0 && plan.Phase == RoutePlanPhase.Active)
        {
            _planList.AddChild(PreCombatPanelStyles.CreateLabel(
                chinese ? "计划（点击地图追加）" : "Planned",
                18,
                StsColors.gold,
                bold: true));
            foreach (var entry in remaining)
                _planList.AddChild(BuildPlannedRow(run, plan, entry, chinese, _lastChain));
        }

        _ledger.Text = BuildLedger(run, player, plan, chinese, _lastChain);
    }

    private PanelContainer BuildCompletedRow(RunState run, RoutePlanEntry entry, bool chinese)
    {
        var row = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
            new Color(0.03f, 0.08f, 0.06f, 0.95f),
            new Color(0.35f, 0.85f, 0.52f, 0.6f),
            6));
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        box.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 3);
        row.AddChild(box);

        var floor = run.TotalFloor - run.ActFloor + entry.Coord.row + 1;
        var roomName = RoutePlanTracker.FindMapPoint(run, entry.Coord) is { } point
            ? DescribeRoom(point, chinese)
            : "?";
        box.AddChild(PreCombatPanelStyles.CreateLabel(
            $"✓ {(chinese ? $"第{floor}层" : $"Floor {floor}")} · {roomName}",
            20,
            new Color(0.55f, 0.85f, 0.62f),
            bold: true));
        box.AddChild(PreCombatPanelStyles.CreateLabel(
            FormatActual(entry.Actual!, chinese),
            17,
            StsColors.cream));
        return row;
    }

    private PanelContainer BuildPlannedRow(
        RunState run,
        RoutePlan plan,
        RoutePlanEntry entry,
        bool chinese,
        RoutePlanForecastService.PlanChain? chain)
    {
        var row = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
            new Color(0.04f, 0.06f, 0.07f, 0.95f),
            new Color(1f, 0.84f, 0.2f, 0.65f),
            6));
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        box.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 3);
        row.AddChild(box);

        var floor = run.TotalFloor - run.ActFloor + entry.Coord.row + 1;
        var point = RoutePlanTracker.FindMapPoint(run, entry.Coord);
        var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        var header = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        header.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 6);
        box.AddChild(header);
        header.AddChild(new RouteMarkerBadge(RouteMarkerPlanner.GetStyle(7)));
        header.AddChild(PreCombatPanelStyles.CreateLabel(
            $"{(chinese ? $"第{floor}层" : $"Floor {floor}")} · {DescribeRoom(point, chinese)}",
            20,
            StsColors.cream,
            bold: true));

        RouteVariantForecast? variant = null;
        MapNodeForecast? nodeForecast = null;
        string? deltaOverride = null;
        if (point is not null)
        {
            nodeForecast = PredictionPurityGuard.Execute(
                run,
                $"plan:{point.coord}",
                () => Entry.MapForecasts.Predict(run, point, isTravelEnabled: false));
            variant = MatchVariant(nodeForecast.RouteVariants, plan.Entries, entry);
            if (chain is not null && chain.Outcomes.TryGetValue(entry.Coord, out var outcome))
            {
                // The threaded plan state is authoritative for rewards, shop
                // stock, and treasure: choices made upstream already changed it.
                if (variant is not null) variant = variant with
                {
                    Merchant = outcome.Merchant ?? variant.Merchant,
                    CombatRewards = outcome.CombatRewards ?? variant.CombatRewards,
                    Treasure = outcome.Treasure ?? variant.Treasure
                };
                if (outcome.Note is not null)
                    deltaOverride = outcome.Note;
            }
            if (deltaOverride is null
                && chain?.BlockedAtEvent is { } blocked
                && IsAfterPlanEntry(plan, blocked, entry.Coord))
            {
                deltaOverride = chinese
                    ? "前置事件尚未完成；完成事件的后续选项后才会刷新这里"
                    : "A previous event is unfinished; this node refreshes after its follow-up choices.";
            }
        }

        var content = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            MouseFilter = MouseFilterEnum.Ignore,
            ScrollActive = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        content.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.NormalFontSize, 19);
        var themeFont = GetThemeFont(ThemeConstants.Label.Font, "Label");
        if (themeFont is not null)
            content.AddThemeFontOverride(ThemeConstants.RichTextLabel.NormalFont, themeFont);
        content.Text = variant is null
            ? chinese ? "该路线暂时无法预测。" : "No forecast for this route yet."
            : ConvertGameTags(string.Join("\n", FormatVariantContent(point, variant, chinese)));
        box.AddChild(content);

        var deltaLabel = PreCombatPanelStyles.CreateLabel(
            deltaOverride
            ?? (variant is null ? string.Empty : EstimatePlanDelta(entry, variant, player, chinese).Text),
            17,
            new Color(0.15f, 0.82f, 1f));
        box.AddChild(deltaLabel);

        if (variant is not null)
            BuildChoiceControls(box, run, plan, entry, variant, player, chinese, deltaLabel, _lastChain);
        return row;
    }

    private void BuildChoiceControls(
        VBoxContainer box,
        RunState run,
        RoutePlan plan,
        RoutePlanEntry entry,
        RouteVariantForecast variant,
        Player? player,
        bool chinese,
        Label deltaLabel,
        RoutePlanForecastService.PlanChain? chain)
    {
        var choiceBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        choiceBox.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 2);
        var planningPlayer = ResolvePlanningPlayer(chain, entry, player);
        box.AddChild(choiceBox);
        if (chain is not null && planningPlayer is null)
        {
            choiceBox.AddChild(PreCombatPanelStyles.CreateLabel(
                chinese ? "等待前置事件完成后生成该节点的规划状态。"
                    : "This node's planning state is pending an earlier event.",
                15, new Color(0.68f, 0.78f, 0.8f)));
            return;
        }

        void Update(RoutePlanChoice choice)
        {
            var current = plan.Entries.FirstOrDefault(item =>
                               !item.IsCompleted && item.Coord == entry.Coord)
                           ?? entry;

            // Signals from controls that were visible before a refresh can
            // arrive after a rest action was committed. Treat the recorded
            // choice as authoritative; only the explicit empty choice from
            // "Change rest choice" may clear it.
            if (current.Choice is RoutePlanChoice.RestSite currentRest
                && currentRest.IsCommitted
                && choice is RoutePlanChoice.RestSite nextRest
                && nextRest.OptionId.Length > 0)
            {
                return;
            }

            InvalidateEventOutcomesFrom(plan, current);
            var updated = current with { Choice = choice };
            plan.Entries = plan.Entries
                .Select(item => !item.IsCompleted && item.Coord == entry.Coord ? updated : item)
                .ToArray();
            deltaLabel.Text = EstimatePlanDelta(updated, variant, player, chinese).Text;
            // Rebuild every row after a choice change. The upstream choice can
            // change downstream gold, merchant stock, card filters, and event
            // locks, so refreshing only the ledger leaves stale controls on
            // screen.
            RefreshPlan();
        }

        if (variant.Encounter is not null)
        {
            var combatChoice = RoutePlanChoice.AsCombat(entry.Choice);
            RoutePlanChoice.Combat CurrentCombat() => RoutePlanChoice.AsCombat(
                plan.Entries.FirstOrDefault(item => !item.IsCompleted && item.Coord == entry.Coord)?.Choice);
            AddNormalCombatSimulationControls(choiceBox, run, entry, variant, chain, chinese,
                reference => Update(CurrentCombat() with { SimulationReference = reference }));
            if (variant.CombatRewards is { HasValue: true } combat)
            {
                var rewardPlayer = chain?.Outcomes.GetValueOrDefault(entry.Coord)?.BeforeCombatRewards is { } rewardState
                    ? new PlanningPredictionService().Restore(rewardState).Player : planningPlayer;
                AddCombatRewardControls(choiceBox, combat.Value!, combatChoice, CurrentCombat, Update, rewardPlayer, chinese);
            }
        }
        else if (variant.RoomType == RoomType.Shop && variant.Merchant is { HasValue: true } merchant)
        {
            List<MerchantPick> picks = entry.Choice is RoutePlanChoice.Merchant existing
                ? existing.Picks.ToList()
                : new List<MerchantPick>();

            void AddPick(MerchantPick pick, string label, int cost)
            {
                var button = new CheckButton
                {
                    Text = $"{label}  {cost}",
                    ButtonPressed = picks.Any(existing =>
                        existing.Category == pick.Category && existing.Index == pick.Index),
                    MouseFilter = MouseFilterEnum.Stop,
                    FocusMode = FocusModeEnum.None
                };
                button.AddThemeFontSizeOverride("font_size", 16);
                button.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                button.Toggled += on =>
                {
                    if (on)
                        picks.Add(pick);
                    else
                        picks.RemoveAll(other =>
                            other.Category == pick.Category && other.Index == pick.Index);
                    var removeCard = plan.Entries.FirstOrDefault(item =>
                                      !item.IsCompleted && item.Coord == entry.Coord)?.Choice
                                  is RoutePlanChoice.Merchant { RemoveCard: true };
                    Update(new RoutePlanChoice.Merchant(picks.ToArray(), removeCard));
                };
                choiceBox.AddChild(button);
            }

            var cards = merchant.Value!.CharacterCards;
            for (var index = 0; index < cards.Count; index++)
                AddPick(new MerchantPick(MerchantCategory.CharacterCard, index), cards[index].Name, cards[index].Cost);
            var colorless = merchant.Value.ColorlessCards;
            for (var index = 0; index < colorless.Count; index++)
                AddPick(new MerchantPick(MerchantCategory.ColorlessCard, index), colorless[index].Name, colorless[index].Cost);
            var relics = merchant.Value.Relics;
            for (var index = 0; index < relics.Count; index++)
                AddPick(new MerchantPick(MerchantCategory.Relic, index), relics[index].Name, relics[index].Cost);
            var potions = merchant.Value.Potions;
            for (var index = 0; index < potions.Count; index++)
                AddPick(new MerchantPick(MerchantCategory.Potion, index), potions[index].Name, potions[index].Cost);

            var removal = new CheckButton
            {
                Text = chinese ? $"删牌（{merchant.Value.CardRemovalCost}金）" : $"Remove card ({merchant.Value.CardRemovalCost}g)",
                ButtonPressed = entry.Choice is RoutePlanChoice.Merchant { RemoveCard: true },
                MouseFilter = MouseFilterEnum.Stop,
                FocusMode = FocusModeEnum.None
            };
            removal.AddThemeFontSizeOverride("font_size", 16);
            removal.ApplyLocaleFontSubstitution(FontType.Regular, "font");
            removal.Toggled += on =>
                Update(new RoutePlanChoice.Merchant(picks.ToArray(), on));
            choiceBox.AddChild(removal);
        }
        else if (variant.Event is not null)
        {
            var eventName = variant.Event.Id.Entry;
            var isCrystalSphere = eventName.Contains("CrystalSphere", StringComparison.Ordinal);

            if (isCrystalSphere && _planForecasts is not null)
            {
                // Crystal sphere: shadow-construct the minigame and show the
                // full 11x11 layout — the real event stays fully player-driven.
                var canonical = ResolveCanonicalEvent(eventName);
                if (canonical is not null && player is not null)
                {
                    var layoutBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
                    choiceBox.AddChild(layoutBox);
                    var eventChoice = entry.Choice as RoutePlanChoice.EventOption;
                    var mode = eventChoice?.OptionIndex is 1 ? 6 : 3;
                    var stateBefore = _lastChain?.StatesBefore.TryGetValue(entry.Coord, out var capturedState) == true
                        ? capturedState
                        : null;
                    int LayoutCost(int count) =>
                        Math.Max(_planEventPredictions
                            .PredictCrystalSphereLayout(player, canonical, count, stateBefore).Cost, 0);

                    void RenderLayout()
                    {
                        foreach (var child in layoutBox.GetChildren())
                            child.QueueFree();
                        var layout = _planEventPredictions
                            .PredictCrystalSphereLayout(player, canonical, mode, stateBefore);
                        var text = layout.Error is not null
                            ? $"[color=#FF6B5E]布局预测失败：{layout.Error}[/color]"
                            : string.Join("\n", layout.Rows)
                              + "\n" + string.Join("  ", layout.Legend)
                              + (layout.Cost >= 0 && mode == 6
                                  ? $"  [color=#FFA629]6次价格：{layout.Cost}金[/color]"
                                  : string.Empty);
                        var grid = new RichTextLabel
                        {
                            BbcodeEnabled = true,
                            FitContent = true,
                            MouseFilter = MouseFilterEnum.Ignore,
                            ScrollActive = false,
                            SizeFlagsHorizontal = SizeFlags.ExpandFill
                        };
                        grid.AddThemeFontSizeOverride(ThemeConstants.RichTextLabel.NormalFontSize, 15);
                        var gridFont = GetThemeFont(ThemeConstants.Label.Font, "Label");
                        if (gridFont is not null)
                            grid.AddThemeFontOverride(ThemeConstants.RichTextLabel.NormalFont, gridFont);
                        grid.Text = text;
                        layoutBox.AddChild(grid);
                    }

                    RenderLayout();

                    var modeSelect = new OptionButton
                    {
                        MouseFilter = MouseFilterEnum.Stop,
                        FocusMode = FocusModeEnum.None
                    };
                    modeSelect.AddThemeFontSizeOverride("font_size", 17);
                    modeSelect.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                    modeSelect.AddItem(chinese ? "占卜 3 次（免费）" : "Divine 3 times (free)");
                    var cost6 = LayoutCost(6);
                    modeSelect.AddItem(chinese ? $"占卜 6 次（{cost6} 金）" : $"Divine 6 times ({cost6} gold)");
                    modeSelect.Select(eventChoice?.OptionIndex is 1 ? 1 : 0);
                    modeSelect.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
                    {
                        mode = index == 1 ? 6 : 3;
                        Update(new RoutePlanChoice.EventOption((int)index));
                        RenderLayout();
                    });
                    choiceBox.AddChild(modeSelect);
                }
            }
            else
            {
                // Use the complete native option list. Event contents shown by
                // the ordinary map tooltip are explanatory only; plan choices
                // must be generated from the plan's own shadow state.
                var canonical = ResolveCanonicalEvent(variant.Event.Id.Entry);
                if (canonical is not null
                    && player is not null)
                {
                    var stateBefore = _lastChain?.StatesBefore.TryGetValue(entry.Coord, out var capturedState) == true
                        ? capturedState
                        : null;
                    var eventChoice = entry.Choice as RoutePlanChoice.EventOption;
                    void UpdateEventChoice(RoutePlanChoice next)
                    {
                        Update(next is RoutePlanChoice.EventOption nextEvent
                            ? nextEvent with { PreviousSteps = eventChoice?.PreviousSteps ?? [] }
                            : next);
                    }
                    var choices = _planEventPredictions.EnumerateEventOptions(
                        player,
                        canonical,
                        stateBefore,
                        eventChoice?.OptionIndex ?? -1,
                        eventChoice?.CardPicks,
                        eventChoice?.PreviousSteps);
                    if (eventChoice is { PreviousSteps.Count: > 0 })
                    {
                        choiceBox.AddChild(PreCombatPanelStyles.CreateLabel(
                            chinese ? $"事件第 {eventChoice.PreviousSteps.Count + 1} 步"
                                : $"Event step {eventChoice.PreviousSteps.Count + 1}",
                            16, StsColors.cream));
                        var restart = PreCombatPanelStyles.CreateButton(chinese ? "从事件起点重选" : "Restart event plan", 160);
                        restart.Pressed += () => Update(new RoutePlanChoice.EventOption(-1));
                        choiceBox.AddChild(restart);
                    }
                    var select = new OptionButton
                    {
                        MouseFilter = MouseFilterEnum.Stop,
                        FocusMode = FocusModeEnum.None
                    };
                    select.AddThemeFontSizeOverride("font_size", 17);
                    select.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                    select.AddItem(chinese ? "事件选项：未选" : "Event: not chosen");
                    for (var optionIndex = 0; optionIndex < choices.Count; optionIndex++)
                    {
                        var option = choices[optionIndex];
                        var itemIndex = optionIndex + 1;
                        select.AddItem(PlanningPrefix(option.Capability.Kind) + option.Title);
                        var hoverText = FormatEventOptionHoverTips(option, chinese);
                        if (hoverText.Length > 0)
                            select.SetItemTooltip(itemIndex, hoverText);
                        if (option.IsLocked)
                            select.SetItemDisabled(itemIndex, true);
                    }
                    var selected = eventChoice is null
                        ? -1
                        : choices.ToList().FindIndex(option => option.Index == eventChoice.OptionIndex);
                    select.Select(selected < 0 ? 0 : selected + 1);
                    choiceBox.AddChild(select);

                    if (selected >= 0)
                    {
                        if (choices[selected].Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice)
                        {
                            var rewardPolicy = new OptionButton { MouseFilter = MouseFilterEnum.Stop, FocusMode = FocusModeEnum.None };
                            rewardPolicy.AddThemeFontSizeOverride("font_size", 16);
                            rewardPolicy.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                            rewardPolicy.AddItem(chinese ? "事件奖励：未选" : "Event rewards: choose…");
                            rewardPolicy.AddItem(chinese ? "领取奖励（药水满槽则放弃）" : "Take rewards (skip potions when full)");
                            rewardPolicy.AddItem(chinese ? "跳过全部可跳过奖励" : "Skip optional rewards");
                            rewardPolicy.Select(eventChoice?.TakeRewards is { } take ? (take ? 1 : 2) : 0);
                            rewardPolicy.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
                                UpdateEventChoice(new RoutePlanChoice.EventOption(choices[selected].Index, eventChoice?.CardPicks ?? [])
                                {
                                    TakeRewards = index == 0 ? null : index == 1
                                }));
                            choiceBox.AddChild(rewardPolicy);
                        }
                        var hoverText = FormatEventOptionHoverTips(choices[selected], chinese);
                        if (hoverText.Length > 0)
                        {
                            var details = PreCombatPanelStyles.CreateLabel(
                                hoverText,
                                14,
                                new Color(0.68f, 0.78f, 0.8f));
                            details.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                            details.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                            choiceBox.AddChild(details);
                        }

                        AddEventCardSelectionControls(
                            choiceBox,
                            entry,
                            choices[selected],
                            planningPlayer,
                            chinese,
                            UpdateEventChoice);
                    }

                    _ = AddEventExecutionControls(
                        choiceBox,
                        variant.Event.Id.Entry,
                        entry,
                        player,
                        chinese,
                        _lastChain,
                        choices);
                    if (selected >= 0 && eventChoice?.SimulationReference is { } eventReference
                        && _eventOutcomes.TryGetValue(entry.Coord, out var combatOutcome)
                        && combatOutcome.Ok && combatOutcome.CombatRewards is { } eventRewards
                        && stateBefore is not null)
                    {
                        var rewardPlayer = combatOutcome.BeforeCombatRewards is { } rewardState
                            ? new PlanningPredictionService().Restore(rewardState).Player : planningPlayer;
                        RoutePlanChoice.EventOption CurrentEventChoice() =>
                            plan.Entries.First(item => !item.IsCompleted && item.Coord == entry.Coord).Choice
                                as RoutePlanChoice.EventOption ?? eventChoice;
                        var rewardStatus = PreCombatPanelStyles.CreateLabel(string.Empty, 14, StsColors.cream);
                        choiceBox.AddChild(rewardStatus);
                        AddCombatRewardControls(choiceBox, eventRewards, eventChoice.CombatRewards,
                            () => CurrentEventChoice().CombatRewards,
                            rewards => _ = CommitEventCombatSimulationAsync(
                                entry, player, canonical, choices[selected],
                                CurrentEventChoice() with { CombatRewards = rewards }, stateBefore,
                                eventReference, _planRevision, rewardStatus, chinese), rewardPlayer, chinese);
                    }
                    select.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
                    {
                        var listIndex = (int)index - 1;
                        if (listIndex < 0)
                        {
                            UpdateEventChoice(new RoutePlanChoice.EventOption(-1));
                            return;
                        }
                        if (listIndex >= choices.Count)
                            return;
                        var selectedOption = choices[listIndex];
                        UpdateEventChoice(new RoutePlanChoice.EventOption(selectedOption.Index));
                    });
                    if (_eventOutcomes.TryGetValue(entry.Coord, out var completedStep)
                        && completedStep.Ok && !completedStep.Finished && completedStep.NextOptions.Count > 0)
                    {
                        var proceed = PreCombatPanelStyles.CreateButton(chinese ? "选择后续事件选项" : "Choose next event step", 190);
                        proceed.Pressed += () => Update(new RoutePlanChoice.EventOption(-1)
                        {
                            PreviousSteps = completedStep.CompletedSteps
                        });
                        choiceBox.AddChild(proceed);
                    }
                }
            }
        }
        else if (variant.RoomType == RoomType.Treasure && variant.Treasure is { HasValue: true } treasure)
        {
            AddTakeToggle(
                choiceBox,
                chinese ? "拾取宝箱遗物" : "Take chest relic",
                entry.Choice is RoutePlanChoice.Relic { Take: false } ? false : true,
                take => Update(new RoutePlanChoice.Relic(take)));
        }
        else if (variant.RoomType == RoomType.RestSite)
        {
            List<RestSiteOption> options = [];
            try
            {
                if (player is not null)
                {
                    options = PredictionPurityGuard.Execute(
                        run,
                        $"plan-rest-options:{entry.Coord.row}_{entry.Coord.col}",
                        () => RestSiteOption.Generate(planningPlayer ?? player));
                }
            }
            catch (Exception exception)
            {
                Entry.Logger.Error($"Rest option enumeration failed: {exception}");
            }

            var restChoice = entry.Choice as RoutePlanChoice.RestSite;
            var select = new OptionButton
            {
                MouseFilter = MouseFilterEnum.Stop,
                FocusMode = FocusModeEnum.None
            };
            select.AddThemeFontSizeOverride("font_size", 17);
            select.ApplyLocaleFontSubstitution(FontType.Regular, "font");
            select.AddItem(chinese ? "休息点：跳过" : "Rest site: skip");
            for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
            {
                var option = options[optionIndex];
                select.AddItem(option.Title.GetFormattedText());
                if (!option.IsEnabled)
                    select.SetItemDisabled(optionIndex + 1, true);
            }
            var selectedRestOption = restChoice is null
                ? -1
                : options.FindIndex(option => option.OptionId == restChoice.OptionId);
            select.Select(selectedRestOption < 0 ? 0 : selectedRestOption + 1);
            select.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
            {
                if (index <= 0)
                {
                    Update(new RoutePlanChoice.RestSite(string.Empty, null));
                    return;
                }
                var optionIndex = (int)index - 1;
                if (optionIndex >= options.Count)
                    return;
                Update(new RoutePlanChoice.RestSite(options[optionIndex].OptionId, null));
                var optionId = options[optionIndex].OptionId;
                if (optionId is "SMITH" or "COOK" or "CLONE" or "MEND")
                    RefreshPlan();
            });
            choiceBox.AddChild(select);

            var restOptionId = restChoice?.OptionId ?? string.Empty;
            // Only Smith and Cook select cards. Clone, Dig, Lift, Kindle,
            // Hatch, Heal and multiplayer Mend resolve their own target/effect
            // when the option is chosen and must never expose a fake card
            // picker in the planning panel.
            var targetAction = restOptionId is "SMITH" or "COOK";
            var targetSelected = restChoice?.IsCommitted == true;
            var actionCommitted = restChoice?.IsCommitted == true;
            if (actionCommitted)
            {
                // A rest room has one action. Once an action (and, where
                // required, its target card) is recorded, lock the action
                // selector so a panel refresh cannot look like a second forge
                // or cook. Reset is explicit and starts a new plan choice.
                select.Disabled = true;
                var reset = PreCombatPanelStyles.CreateButton(
                    chinese ? "更改休息选择" : "Change rest choice",
                    180);
                reset.TooltipText = chinese
                    ? "每个休息点只能执行一次；点击后清除当前动作并重新选择"
                    : "Each rest site has one action; clear it before choosing another.";
                reset.Pressed += () =>
                {
                    Update(new RoutePlanChoice.RestSite(string.Empty, null));
                    RefreshPlan();
                };
                choiceBox.AddChild(reset);
            }

            if (targetAction && restChoice is not null && planningPlayer is not null)
            {
                // Use the state immediately before this node. The final chain
                // state has already applied this node (and later nodes), which
                // used to hide the selected smith/cook target after a refresh.
                var deck = planningPlayer.Deck.Cards
                    .Select((card, slot) => (
                        Id: (ModelId)card.Id,
                        Title: card.Title,
                        Upgraded: card.IsUpgraded,
                        IsUpgradable: card.IsUpgradable,
                        IsRemovable: card.IsRemovable,
                        Slot: slot))
                    .ToList();

                var candidates = restOptionId switch
                {
                    // Eternal cards are neither removable nor upgradable;
                    // forging excludes already-upgraded cards (IsUpgradable
                    // alone governs infinite-upgrade cards like Searing Blow).
                    "SMITH" => deck.Where(card => card.IsUpgradable && !card.Upgraded).ToList(),
                    "COOK" => deck.Where(card => card.IsRemovable).ToList(),
                    _ => deck.ToList()
                };

                void AddTargetPicker(
                    int slot,
                    ModelId? current,
                    int currentSlot,
                    ModelId? other,
                    int otherSlot)
                {
                    var filtered = candidates
                        .Where(card => otherSlot >= 0
                            ? card.Slot != otherSlot
                            : other is not { } otherId || card.Id.Entry != otherId.Entry)
                        .ToList();
                    if (current is { } currentId
                        && !filtered.Any(card => currentSlot >= 0
                            ? card.Slot == currentSlot
                            : card.Id.Entry == currentId.Entry))
                    {
                        // Preserve a stale target in the display so the user
                        // can see why the action is locked and explicitly reset
                        // it instead of silently replacing the card.
                        var deckIndex = currentSlot >= 0
                            ? deck.FindIndex(card => card.Slot == currentSlot)
                            : deck.FindIndex(card => card.Id.Entry == currentId.Entry);
                        if (deckIndex >= 0)
                        {
                            filtered.Insert(0, deck[deckIndex]);
                        }
                        else
                        {
                            var model = ModelDb.AllCards.FirstOrDefault(card =>
                                card.Id.Entry == currentId.Entry);
                                filtered.Insert(0, (
                                Id: currentId,
                                Title: model?.Title ?? currentId.Entry,
                                Upgraded: model?.IsUpgraded ?? false,
                                IsUpgradable: model?.IsUpgradable ?? false,
                                IsRemovable: model?.IsRemovable ?? false,
                                Slot: currentSlot));
                        }
                    }

                    var target = new OptionButton
                    {
                        MouseFilter = MouseFilterEnum.Stop,
                        FocusMode = FocusModeEnum.None
                    };
                    target.AddThemeFontSizeOverride("font_size", 16);
                    target.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                    target.AddItem(slot == 0 && restOptionId == "COOK"
                        ? (chinese ? "烹饪第 1 张牌…" : "Cook card 1…")
                        : slot == 1
                            ? (chinese ? "烹饪第 2 张牌…" : "Cook card 2…")
                            : (chinese ? "选择目标牌…" : "Pick a card…"));
                    foreach (var card in filtered)
                        target.AddItem($"{card.Title}{(card.Upgraded ? "+" : string.Empty)}"
                                       + (current is { } selected
                                           && (currentSlot >= 0
                                               ? card.Slot == currentSlot
                                               : card.Id.Entry == selected.Entry)
                                            ? (chinese ? "（已选）" : " (selected)")
                                            : string.Empty));
                    var selectedIndex = current is not { } selectedCurrent
                        ? 0
                        : (currentSlot >= 0
                            ? filtered.FindIndex(card => card.Slot == currentSlot)
                            : filtered.FindIndex(card => card.Id.Entry == selectedCurrent.Entry)) + 1;
                    target.Select(selectedIndex < 0 ? 0 : selectedIndex);
                    target.Disabled = targetSelected;
                    target.TooltipText = targetSelected
                        ? (chinese
                            ? "休息动作已确定；如需更改，请先点击“更改休息选择”"
                            : "Rest action locked; use Change rest choice to choose again.")
                        : string.Empty;
                    if (!targetSelected)
                    {
                        target.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index2 =>
                        {
                            if (index2 <= 0 || (int)index2 - 1 >= filtered.Count)
                                return;
                            var selectedCard = filtered[(int)index2 - 1];
                            var selected = selectedCard.Id;
                            if (restOptionId == "COOK")
                            {
                                var liveRestChoice = plan.Entries
                                    .FirstOrDefault(item =>
                                        !item.IsCompleted && item.Coord == entry.Coord)
                                    ?.Choice as RoutePlanChoice.RestSite
                                    ?? restChoice;
                                var next = slot == 0
                                    ? new RoutePlanChoice.RestSite(restOptionId, selected)
                                    {
                                        SecondTargetCard = liveRestChoice.SecondTargetCard,
                                        TargetCardSlot = selectedCard.Slot,
                                        SecondTargetCardSlot = liveRestChoice.SecondTargetCardSlot
                                    }
                                    : new RoutePlanChoice.RestSite(restOptionId, liveRestChoice.TargetCard)
                                    {
                                        SecondTargetCard = selected,
                                        TargetCardSlot = liveRestChoice.TargetCardSlot,
                                        SecondTargetCardSlot = selectedCard.Slot
                                    };
                                Update(next);
                                RefreshPlan();
                            }
                            else
                            {
                                Update(new RoutePlanChoice.RestSite(restOptionId, selected)
                                {
                                    TargetCardSlot = selectedCard.Slot
                                });
                                RefreshPlan();
                            }
                        });
                    }
                    choiceBox.AddChild(target);
                }

                AddTargetPicker(
                    0,
                    restChoice.TargetCard,
                    restChoice.TargetCardSlot,
                    restChoice.SecondTargetCard,
                    restChoice.SecondTargetCardSlot);
                if (restOptionId == "COOK")
                    AddTargetPicker(
                        1,
                        restChoice.SecondTargetCard,
                        restChoice.SecondTargetCardSlot,
                        restChoice.TargetCard,
                        restChoice.TargetCardSlot);
            }
        }
    }

    private sealed record PlanningPotion(ModelId Id, string Name);

    private static bool SameModelId(ModelId left, ModelId right) =>
        left.Category.Equals(right.Category, StringComparison.OrdinalIgnoreCase)
        && left.Entry.Equals(right.Entry, StringComparison.OrdinalIgnoreCase);

    private static bool SamePotionEntry(ModelId left, ModelId right) =>
        left.Entry.Equals(right.Entry, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<PlanningPotion> ResolvePlanningPotions(Player? planningPlayer)
    {
        // The node's restored shadow player is the only source of truth. A
        // second ID ledger can drift when an upstream reward is discarded,
        // replaced, or fails model resolution.
        return (planningPlayer?.Potions ?? [])
            .Select(potion => new PlanningPotion(potion.Id, potion.Title.GetFormattedText()))
            .ToArray();
    }

    private static string FormatEventOptionHoverTips(
        PlanningEventPredictionService.EventOptionDescriptor option,
        bool chinese)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(option.Description))
            lines.Add(option.Description);
        lines.AddRange(option.HoverTips.Where(tip => !lines.Contains(tip, StringComparer.Ordinal)));
        if (lines.Count == 0)
            return string.Empty;

        var heading = chinese ? "事件说明：" : "Event details: ";
        return heading + string.Join("\n", lines);
    }

    private static Player? ResolvePlanningPlayer(
        RoutePlanForecastService.PlanChain? chain,
        RoutePlanEntry entry,
        Player? fallback)
    {
        if (chain is null)
            return fallback;
        if (!chain.StatesBefore.TryGetValue(entry.Coord, out var snapshot))
            return null;

        try
        {
            var run = PlanningPredictionService.RestoreRun(snapshot.Run);
            return run.GetPlayer(snapshot.PlayerNetId);
        }
        catch (Exception exception)
        {
            Entry.Logger.Debug($"Planning state restore failed for {entry.Coord}: {exception.Message}");
            return null;
        }
    }

    private readonly Dictionary<MapCoord, PlanningEventPredictionService.EventExecutionOutcome> _eventOutcomes = new();

    private IReadOnlyDictionary<MapCoord, RoutePlanForecastService.PlannedEventOutcome> PlannedEventOutcomes()
    {
        return _eventOutcomes
            .Where(pair => pair.Value.Ok
                           && pair.Value.ShadowRun is not null
                           && pair.Value.ShadowPlayer is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => new RoutePlanForecastService.PlannedEventOutcome(
                    pair.Value.ShadowRun!,
                    pair.Value.ShadowPlayer!.NetId,
                    pair.Value.Finished));
    }

    private void InvalidateEventOutcomesFrom(RoutePlan plan, RoutePlanEntry changed)
    {
        var invalidate = false;
        foreach (var item in plan.Entries)
        {
            if (!invalidate && !item.IsCompleted && item.Coord == changed.Coord)
                invalidate = true;
            if (invalidate)
                _eventOutcomes.Remove(item.Coord);
        }
    }

    private void PruneEventOutcomes(RoutePlan plan)
    {
        var plannedCoords = plan.Phase == RoutePlanPhase.Active
            ? plan.Entries
                .Where(entry => !entry.IsCompleted)
                .Select(entry => entry.Coord)
                .ToHashSet()
            : [];
        foreach (var coord in _eventOutcomes.Keys.ToArray())
        {
            if (!plannedCoords.Contains(coord))
                _eventOutcomes.Remove(coord);
        }
    }

    private static string PlanningPrefix(PlanningEventPredictionService.EventPlanningKind kind) => kind switch
    {
        PlanningEventPredictionService.EventPlanningKind.Exact => "✓ ",
        PlanningEventPredictionService.EventPlanningKind.RewardChoice => "◇ ",
        PlanningEventPredictionService.EventPlanningKind.CardOrUiChoice => "◆ ",
        PlanningEventPredictionService.EventPlanningKind.SpecialCombat => "⚔ ",
        PlanningEventPredictionService.EventPlanningKind.Minigame => "▦ ",
        PlanningEventPredictionService.EventPlanningKind.RunEnding => "✕ ",
        _ => string.Empty
    };

    private static EventModel? ResolveCanonicalEvent(string entry)
    {
        return ModelDb.AllEvents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id.Entry, entry, StringComparison.Ordinal));
    }

    private static bool IsCardSelectionComplete(
        PlanningEventPredictionService.EventOptionDescriptor option,
        IReadOnlyList<EventCardPick> picks)
    {
        foreach (var selection in option.CardSelections)
        {
            var selected = picks
                .Where(pick => pick.SelectionStep == selection.SelectionStep)
                .ToArray();
            if (selected.Length < selection.MinSelect || selected.Length > selection.MaxSelect)
                return false;

            if (selected.Any(pick => !selection.Candidates.Any(candidate =>
                    candidate.DeckSlot == pick.DeckSlot
                    && candidate.CardId.Entry == pick.CardId.Entry)))
            {
                return false;
            }

            if (selected.Select(pick => pick.DeckSlot).Where(slot => slot >= 0).Distinct().Count()
                != selected.Count(pick => pick.DeckSlot >= 0))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddEventCardSelectionControls(
        VBoxContainer choiceBox,
        RoutePlanEntry entry,
        PlanningEventPredictionService.EventOptionDescriptor option,
        Player? planningPlayer,
        bool chinese,
        Action<RoutePlanChoice> update)
    {
        if (option.CardSelections.Count == 0)
            return;

        var currentChoice = RoutePlanTracker.Current?.Entries
            .FirstOrDefault(candidate => candidate.Coord == entry.Coord)?.Choice
            as RoutePlanChoice.EventOption;
        var currentPicks = currentChoice?.CardPicks.ToList() ?? [];

        foreach (var selection in option.CardSelections)
        {
            var header = PreCombatPanelStyles.CreateLabel(
                $"{selection.Label} ({selection.MinSelect}..{selection.MaxSelect})",
                16,
                new Color(0.55f, 0.8f, 0.95f),
                bold: true);
            choiceBox.AddChild(header);

            var selectedForStep = currentPicks
                .Where(pick => pick.SelectionStep == selection.SelectionStep)
                .OrderBy(pick => pick.SelectionOrder)
                .ToList();
            if (selection.Candidates.Count == 0)
            {
                choiceBox.AddChild(PreCombatPanelStyles.CreateLabel(
                    chinese
                        ? "当前规划状态没有符合条件的卡牌，事件选项将保持锁定。"
                        : "No eligible cards exist in the current planning state; this event option stays locked.",
                    15,
                    new Color(1f, 0.48f, 0.4f)));
            }
            var pickerCount = Math.Max(selection.MaxSelect, 1);
            for (var pickerIndex = 0; pickerIndex < pickerCount; pickerIndex++)
            {
                var selectionOrder = pickerIndex;
                var selectedPick = pickerIndex < selectedForStep.Count
                    ? selectedForStep[pickerIndex]
                    : null;
                var selectedCandidate = selectedPick is null
                    ? null
                    : selection.Candidates.FirstOrDefault(candidate =>
                        candidate.DeckSlot == selectedPick.DeckSlot
                        && candidate.CardId.Entry == selectedPick.CardId.Entry);

                var candidates = selection.Candidates
                    .Where(candidate => selectedForStep
                        .Where((_, index) => index != pickerIndex)
                        .All(pick => pick.DeckSlot < 0
                            || candidate.DeckSlot != pick.DeckSlot))
                    .ToList();
                if (selectedPick is not null && selectedCandidate is null)
                {
                    // Keep a stale pick visible after an upstream plan change;
                    // the execution button remains disabled until it is fixed.
                    var staleModel = ModelDb.AllCards.FirstOrDefault(model =>
                        model.Id.Entry == selectedPick.CardId.Entry);
                    candidates.Insert(0, new PlanningEventPredictionService.EventCardCandidate(
                        selectedPick.CardId,
                        selectedPick.DeckSlot,
                        staleModel?.Title ?? selectedPick.CardId.Entry,
                        staleModel?.IsUpgraded ?? false,
                        staleModel?.Enchantment?.Title.GetFormattedText()));
                }

                var picker = new OptionButton
                {
                    MouseFilter = MouseFilterEnum.Stop,
                    FocusMode = FocusModeEnum.None
                };
                picker.AddThemeFontSizeOverride("font_size", 16);
                picker.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                picker.AddItem(pickerIndex == 0 && selection.MaxSelect == 1
                    ? (chinese ? "选择目标牌…" : "Pick a card…")
                    : (chinese ? $"第 {pickerIndex + 1} 张…" : $"Card {pickerIndex + 1}…"));
                foreach (var candidate in candidates)
                {
                    var label = candidate.Title + (candidate.Upgraded ? "+" : string.Empty);
                    if (candidate.Enchantment is { Length: > 0 } enchantment)
                        label += $" [{enchantment}]";
                    if (selectedCandidate is not null
                        && candidate.DeckSlot == selectedCandidate.DeckSlot
                        && candidate.CardId.Entry == selectedCandidate.CardId.Entry)
                    {
                        label += chinese ? "（已选）" : " (selected)";
                    }

                    picker.AddItem(label);
                }

                var selectedIndex = selectedCandidate is null
                    ? 0
                    : candidates.FindIndex(candidate =>
                          candidate.DeckSlot == selectedCandidate.DeckSlot
                          && candidate.CardId.Entry == selectedCandidate.CardId.Entry) + 1;
                picker.Select(selectedIndex < 0 ? 0 : selectedIndex);
                picker.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
                {
                    var next = currentPicks
                        .Where(pick => pick.SelectionStep != selection.SelectionStep)
                        .ToList();
                    next.AddRange(selectedForStep
                        .Where((_, indexInStep) => indexInStep != selectionOrder)
                        .Select((pick, indexInStep) => pick with
                        {
                            SelectionOrder = indexInStep >= selectionOrder
                                ? indexInStep + 1
                                : indexInStep
                        }));
                    if (index > 0 && (int)index - 1 < candidates.Count)
                    {
                        var candidate = candidates[(int)index - 1];
                        next.Add(new EventCardPick(
                            selection.SelectionStep,
                            candidate.CardId,
                            candidate.DeckSlot,
                            selectionOrder));
                    }

                    update(new RoutePlanChoice.EventOption(option.Index, next
                        .OrderBy(pick => pick.SelectionStep)
                        .ToArray()));
                });
                choiceBox.AddChild(picker);
            }
        }
    }

    /// <summary>
    /// "执行预演" button + outcome label for whitelisted events: the option
    /// really executes on a shadow run (worker task), yielding exact deltas
    /// and the revealed follow-up options. Live state is never touched.
    /// </summary>
    private Action<int>? AddEventExecutionControls(
        VBoxContainer choiceBox,
        string eventName,
        RoutePlanEntry entry,
        Player? player,
        bool chinese,
        RoutePlanForecastService.PlanChain? chain,
        IReadOnlyList<PlanningEventPredictionService.EventOptionDescriptor> options)
    {
        if (ResolveCanonicalEvent(eventName) is not { } canonical || player is null)
        {
            return null;
        }

        var capabilityLabel = PreCombatPanelStyles.CreateLabel(
            string.Empty,
            15,
            new Color(0.55f, 0.8f, 0.95f));
        capabilityLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        capabilityLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var outcomeLabel = PreCombatPanelStyles.CreateLabel(
            DescribeEventOutcome(entry.Coord, chinese),
            15,
            StsColors.cream);
        outcomeLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        outcomeLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var executeButton = PreCombatPanelStyles.CreateButton(
            chinese ? "执行预演" : "Simulate choice", 160);

        void ApplyCapability(int optionIndex)
        {
            var option = options.FirstOrDefault(candidate => candidate.Index == optionIndex);
            outcomeLabel.Text = DescribeEventOutcome(entry.Coord, chinese);
            if (option is null)
            {
                capabilityLabel.Text = chinese
                    ? "图例：✓精确　◇奖励待选　◆卡牌/交互待选　⚔特殊战斗　✕终止跑局"
                    : "Legend: ✓ exact  ◇ reward choice  ◆ card/UI choice  ⚔ special combat  ✕ run ends";
                executeButton.Text = chinese ? "先选择事件选项" : "Choose an option";
                executeButton.Disabled = true;
                return;
            }

            var kind = option.Capability.Kind;
            var liveEventChoice = RoutePlanTracker.Current?.Entries
                .FirstOrDefault(candidate => candidate.Coord == entry.Coord)?.Choice
                as RoutePlanChoice.EventOption;
            var cardSelectionComplete = IsCardSelectionComplete(
                option,
                liveEventChoice?.CardPicks ?? []);
            capabilityLabel.Text = kind switch
            {
                PlanningEventPredictionService.EventPlanningKind.Exact => chinese
                    ? option.CardSelections.Count > 0
                        ? (cardSelectionComplete
                            ? "✓ 卡牌选择已完成，可在隔离影子跑局中精确执行。"
                            : "◆ 请先完成事件中的卡牌选择，再推进后续世界线。")
                        : "✓ 可在隔离影子跑局中精确执行；结果可计入资源台账。"
                    : option.CardSelections.Count > 0
                        ? (cardSelectionComplete
                            ? "✓ Card selections complete; the event can run exactly on a shadow run."
                            : "◆ Complete the event card selections before advancing the worldline.")
                        : "✓ Can execute exactly on an isolated shadow run; result can enter the resource ledger.",
                PlanningEventPredictionService.EventPlanningKind.RewardChoice => chinese
                    ? "◇ 随机奖励内容可预知，但还需要指定拿取/跳过；未指定前不应推进后续世界线。"
                    : "◇ Reward contents are predictable, but take/skip decisions are still required before advancing the worldline.",
                PlanningEventPredictionService.EventPlanningKind.CardOrUiChoice => chinese
                    ? "◆ 还包含卡牌或界面内选择；当前只展示候选内容，不把默认第一项伪装成计划。"
                    : "◆ Contains another card or UI choice; candidates are shown without silently assuming the first one.",
                PlanningEventPredictionService.EventPlanningKind.SpecialCombat => chinese
                    ? "⚔ 事件战斗"
                    : "⚔ Event combat",
                PlanningEventPredictionService.EventPlanningKind.Minigame => chinese
                    ? "▦ 小游戏使用专用布局预测。"
                    : "▦ This minigame uses its dedicated layout forecast.",
                PlanningEventPredictionService.EventPlanningKind.RunEnding => chinese
                    ? "✕ 该选择会结束当前跑局，计划中的后续房间不再成立。"
                    : "✕ This choice ends the run, so later planned rooms are unreachable.",
                _ => string.Empty
            };

            executeButton.Text = kind switch
            {
                PlanningEventPredictionService.EventPlanningKind.Exact => chinese ? "执行精确预演" : "Run exact preview",
                PlanningEventPredictionService.EventPlanningKind.SpecialCombat => chinese ? "模拟战斗" : "Simulate combat",
                PlanningEventPredictionService.EventPlanningKind.RewardChoice => liveEventChoice?.TakeRewards is not null
                    ? (chinese ? "执行奖励预演" : "Preview rewards")
                    : (chinese ? "等待奖励取舍" : "Reward choice required"),
                PlanningEventPredictionService.EventPlanningKind.CardOrUiChoice => chinese ? "等待附加选择" : "Extra choice required",
                PlanningEventPredictionService.EventPlanningKind.RunEnding => chinese ? "此选择终止跑局" : "This ends the run",
                _ => chinese ? "使用专用预测" : "Use dedicated forecast"
            };
            executeButton.Disabled = kind is not (
                PlanningEventPredictionService.EventPlanningKind.Exact
                or PlanningEventPredictionService.EventPlanningKind.SpecialCombat
                or PlanningEventPredictionService.EventPlanningKind.RewardChoice)
                || (kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice
                    && liveEventChoice?.TakeRewards is null)
                || (kind == PlanningEventPredictionService.EventPlanningKind.Exact
                    && !cardSelectionComplete);
            executeButton.TooltipText = capabilityLabel.Text;
        }

        executeButton.Pressed += () =>
        {
            // Read the choice fresh: the dropdown updates plan.Entries after
            // this control was built, so the captured record is stale.
            var currentEntry = RoutePlanTracker.Current?.Entries
                .FirstOrDefault(candidate => candidate.Coord == entry.Coord);
            var choice = currentEntry?.Choice as RoutePlanChoice.EventOption;
            if (choice is not { OptionIndex: >= 0 })
            {
                outcomeLabel.Text = chinese ? "先选择一个事件选项。" : "Choose an option first.";
                return;
            }

            var requestedOptionIndex = choice.OptionIndex;
            var descriptor = options.FirstOrDefault(option => option.Index == requestedOptionIndex);
            if (descriptor is null)
            {
                outcomeLabel.Text = chinese ? "选项已经失效，请重新选择。" : "The option is stale; choose it again.";
                return;
            }

            if (descriptor.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.SpecialCombat)
            {
                outcomeLabel.Text = chinese ? "准备事件战斗模拟…" : "Preparing event combat simulation…";
                executeButton.Disabled = true;
                var revision = _planRevision;
                var combatChain = _lastChain;
                var combatPlannedState = combatChain is not null
                                   && combatChain.StatesBefore.TryGetValue(entry.Coord, out var combatStateBefore)
                    ? combatStateBefore
                    : null;
                _ = PrepareEventCombatSimulationAsync(
                    choiceBox,
                    executeButton,
                    outcomeLabel,
                    entry,
                    player,
                    canonical,
                    descriptor,
                    choice,
                    combatPlannedState,
                    chinese,
                    revision);
                return;
            }

            if (descriptor.Capability.Kind != PlanningEventPredictionService.EventPlanningKind.Exact
                && !(descriptor.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice
                    && choice.TakeRewards is not null))
            {
                ApplyCapability(requestedOptionIndex);
                return;
            }

            outcomeLabel.Text = chinese ? "执行中…" : "Simulating…";
            executeButton.Disabled = true;
            var executionRevision = _planRevision;
            var activeChain = _lastChain;
            var plannedState = activeChain is not null
                               && activeChain.StatesBefore.TryGetValue(entry.Coord, out var stateBefore)
                ? stateBefore
                : null;
            _ = Task.Run(async () =>
            {
                var outcome = await _planEventPredictions.ExecuteEventOptionAsync(
                     player,
                     canonical,
                     requestedOptionIndex,
                     choice.CardPicks,
                     plannedState,
                     descriptor,
                     choice.PreviousSteps,
                     choice.TakeRewards);
                SeedOracleDispatcher.Post(() =>
                {
                    if (executionRevision != _planRevision)
                        return;
                    var liveChoice = RoutePlanTracker.Current?.Entries
                        .FirstOrDefault(candidate => candidate.Coord == entry.Coord)?.Choice
                        as RoutePlanChoice.EventOption;
                    if (liveChoice?.OptionIndex != requestedOptionIndex)
                        return;
                    if (outcome.Ok)
                    {
                        _eventOutcomes[entry.Coord] = outcome;
                    }

                    ApplyCapability(requestedOptionIndex);
                    outcomeLabel.Text = DescribeOutcomeText(outcome, chinese);
                    RefreshPlan();
                });
            });
        };
        choiceBox.AddChild(capabilityLabel);
        choiceBox.AddChild(executeButton);
        choiceBox.AddChild(outcomeLabel);
        var initialChoice = RoutePlanTracker.Current?.Entries
            .FirstOrDefault(candidate => candidate.Coord == entry.Coord)?.Choice
            as RoutePlanChoice.EventOption;
        ApplyCapability(initialChoice?.OptionIndex ?? -1);
        return ApplyCapability;
    }

    private async Task PrepareEventCombatSimulationAsync(
        VBoxContainer choiceBox,
        Button executeButton,
        Label outcomeLabel,
        RoutePlanEntry entry,
        Player livePlayer,
        EventModel canonical,
        PlanningEventPredictionService.EventOptionDescriptor descriptor,
        RoutePlanChoice.EventOption selectedChoice,
        PlanningPredictionService.StateSnapshot? plannedState,
        bool chinese,
        long revision)
    {
        if (plannedState is null)
        {
            outcomeLabel.Text = chinese ? "事件规划状态尚未生成。" : "The event planning state is unavailable.";
            executeButton.Disabled = false;
            return;
        }

        try
        {
            var outcome = await _planEventPredictions.ExecuteEventOptionAsync(
                livePlayer,
                canonical,
                selectedChoice.OptionIndex,
                selectedChoice.CardPicks,
                plannedState,
                descriptor,
                selectedChoice.PreviousSteps,
                selectedChoice.TakeRewards);
            SeedOracleDispatcher.Post(() =>
                ApplyPreparedEventCombat(
                    choiceBox,
                    executeButton,
                    outcomeLabel,
                    entry,
                    livePlayer,
                    canonical,
                    descriptor,
                    selectedChoice,
                    plannedState,
                    chinese,
                    revision,
                    outcome));
        }
        catch (Exception exception)
        {
            SeedOracleDispatcher.Post(() =>
            {
                if (GodotObject.IsInstanceValid(outcomeLabel))
                {
                    outcomeLabel.Text = (chinese ? "事件战斗准备失败：" : "Event combat preparation failed: ")
                                        + exception.GetBaseException().Message;
                    executeButton.Disabled = false;
                }
            });
            Entry.Logger.Error($"Event combat simulation preparation failed: {exception}");
        }
    }

    private void ApplyPreparedEventCombat(
        VBoxContainer choiceBox,
        Button executeButton,
        Label outcomeLabel,
        RoutePlanEntry entry,
        Player livePlayer,
        EventModel canonical,
        PlanningEventPredictionService.EventOptionDescriptor descriptor,
        RoutePlanChoice.EventOption selectedChoice,
        PlanningPredictionService.StateSnapshot plannedState,
        bool chinese,
        long revision,
        PlanningEventPredictionService.EventExecutionOutcome outcome)
    {
        if (!GodotObject.IsInstanceValid(outcomeLabel) || revision != _planRevision)
            return;
        if (!outcome.Ok || outcome.ShadowRun is null || outcome.ShadowPlayer is null)
        {
            outcomeLabel.Text = outcome.DenyReason ?? (chinese ? "事件战斗准备失败。" : "Could not prepare event combat.");
            executeButton.Disabled = false;
            return;
        }

        var encounterId = outcome.CombatEncounterId ?? descriptor.CombatEncounterId;
        var encounter = encounterId is null
            ? null
            : ModelDb.All.OfType<EncounterModel>().FirstOrDefault(candidate =>
                candidate.Id.Entry.Equals(encounterId, StringComparison.OrdinalIgnoreCase));
        if (encounter is null)
        {
            outcomeLabel.Text = chinese ? "事件没有可识别的原生战斗遭遇。" : "The event did not expose a native combat encounter.";
            executeButton.Disabled = false;
            return;
        }

        var combatState = _planEventPredictions.CaptureCombatState(outcome);
        outcomeLabel.Text = chinese
            ? $"已捕获事件战斗：{encounter.Title.GetFormattedText()}；请选择模拟次数。"
            : $"Captured event combat: {encounter.Title.GetFormattedText()}; choose sample count.";
        executeButton.Text = chinese ? "事件战斗已准备" : "Event combat prepared";

        AddCombatSimulationControls(
            choiceBox,
            _lastRun ?? (RunState)livePlayer.RunState,
            entry,
            combatState,
            encounter,
            encounter.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss
                ? encounter.RoomType
                : RoomType.Monster,
            RoutePlanTracker.FindMapPoint(_lastRun ?? (RunState)livePlayer.RunState, entry.Coord)?.PointType
                ?? MapPointType.Unknown,
            chinese,
            reference => _ = CommitEventCombatSimulationAsync(
                entry,
                livePlayer,
                canonical,
                descriptor,
                selectedChoice,
                plannedState,
                reference,
                revision,
                outcomeLabel,
                chinese));
    }

    private async Task CommitEventCombatSimulationAsync(
        RoutePlanEntry entry,
        Player livePlayer,
        EventModel canonical,
        PlanningEventPredictionService.EventOptionDescriptor descriptor,
        RoutePlanChoice.EventOption selectedChoice,
        PlanningPredictionService.StateSnapshot plannedState,
        CombatSimulationReference reference,
        long revision,
        Label outcomeLabel,
        bool chinese)
    {
        if (revision != _planRevision)
            return;
        if (reference.TargetFloor != entry.Coord.row + 1 || reference.TargetColumn != entry.Coord.col)
            return;
        try
        {
            var outcome = await _planEventPredictions.ExecuteEventOptionAsync(
                livePlayer,
                canonical,
                selectedChoice.OptionIndex,
                selectedChoice.CardPicks,
                plannedState,
                descriptor,
                selectedChoice.PreviousSteps,
                selectedChoice.TakeRewards,
                reference,
                selectedChoice.CombatRewards);
            SeedOracleDispatcher.Post(() =>
                ApplyEventCombatReference(
                    entry,
                    selectedChoice,
                    reference,
                    revision,
                    outcomeLabel,
                    chinese,
                    outcome));
        }
        catch (Exception exception)
        {
            SeedOracleDispatcher.Post(() =>
            {
                if (GodotObject.IsInstanceValid(outcomeLabel))
                    outcomeLabel.Text = (chinese ? "模拟战斗参照失败：" : "Could not record combat reference: ")
                                        + exception.GetBaseException().Message;
            });
            Entry.Logger.Error($"Event combat simulation commit failed: {exception}");
        }
    }

    private void ApplyEventCombatReference(
        RoutePlanEntry entry,
        RoutePlanChoice.EventOption selectedChoice,
        CombatSimulationReference reference,
        long revision,
        Label outcomeLabel,
        bool chinese,
        PlanningEventPredictionService.EventExecutionOutcome outcome)
    {
        if (!GodotObject.IsInstanceValid(outcomeLabel) || revision != _planRevision)
            return;
        if (!outcome.Ok)
        {
            outcomeLabel.Text = outcome.DenyReason ?? (chinese ? "模拟战斗参照已失效。" : "The combat reference is stale.");
            return;
        }

        var plan = RoutePlanTracker.Current;
        var current = plan?.Entries.FirstOrDefault(candidate =>
            !candidate.IsCompleted && candidate.Coord == entry.Coord);
        if (plan is null || current is null)
            return;
        InvalidateEventOutcomesFrom(plan, current);
        plan.Entries = plan.Entries
            .Select(item => !item.IsCompleted && item.Coord == entry.Coord
                ? item with
                {
                    Choice = selectedChoice with
                    {
                        SimulationReference = reference,
                        CombatRewards = selectedChoice.CombatRewards
                    }
                }
                : item)
            .ToArray();
        _eventOutcomes[entry.Coord] = outcome;
        outcomeLabel.Text = DescribeOutcomeText(outcome, chinese);
        RefreshPlan();
    }

    private string DescribeEventOutcome(MapCoord coord, bool chinese)
    {
        return _eventOutcomes.TryGetValue(coord, out var outcome)
            ? DescribeOutcomeText(outcome, chinese)
            : string.Empty;
    }

    private static string DescribeOutcomeText(
        PlanningEventPredictionService.EventExecutionOutcome outcome,
        bool chinese)
    {
        if (!outcome.Ok)
        {
            return outcome.DenyReason ?? (chinese ? "执行失败。" : "Execution failed.");
        }

        var parts = new List<string>();
        if (outcome.GoldDelta != 0)
            parts.Add($"{(chinese ? "金币" : "gold")} {outcome.GoldDelta:+#;-#;0}");
        if (outcome.HpDelta != 0)
            parts.Add($"HP {outcome.HpDelta:+#;-#;0}");
        if (outcome.CardsGained.Count > 0)
            parts.Add($"{(chinese ? "获得卡" : "+cards")} {string.Join("、", outcome.CardsGained)}");
        if (outcome.CardsLost.Count > 0)
            parts.Add($"{(chinese ? "失去卡" : "-cards")} {string.Join("、", outcome.CardsLost)}");
        if (outcome.RelicsGained.Count > 0)
            parts.Add($"{(chinese ? "获得遗物" : "+relics")} {string.Join("、", outcome.RelicsGained)}");
        if (outcome.PotionsGained.Count > 0)
            parts.Add($"{(chinese ? "获得药水" : "+potions")} {string.Join("、", outcome.PotionsGained)}");
        var text = parts.Count == 0
            ? (chinese ? "无直接收益。" : "No direct gains.")
            : string.Join("  ", parts);
        if (outcome.NextOptions.Count > 0)
        {
            text += chinese
                ? $"　后续选项：{string.Join(" / ", outcome.NextOptions.Select(pair => pair.Item2))}"
                : $"　Follow-up: {string.Join(" / ", outcome.NextOptions.Select(pair => pair.Item2))}";
        }

        return text;
    }

    private static void AddTakeToggle(
        VBoxContainer box,
        string label,
        bool initial,
        Action<bool> onChanged)
    {
        var button = new CheckButton
        {
            Text = label,
            ButtonPressed = initial,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None
        };
        button.AddThemeFontSizeOverride("font_size", 17);
        button.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        button.Toggled += on => onChanged(on);
        box.AddChild(button);
    }

    private void RefreshLedgerOnly()
    {
        var screen = _screen;
        if (screen is null || !GodotObject.IsInstanceValid(screen) || screen._runState is not { } run)
            return;
        var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        var plan = RoutePlanTracker.Current;
        if (plan is not null
            && plan.Phase == RoutePlanPhase.Active
            && player is not null
            && _planForecasts is not null)
        {
            try
            {
                _lastChain = PredictionPurityGuard.Execute(
                    run,
                    "plan-chain",
                    () => _planForecasts.BuildChain(
                        run,
                        player,
                        plan.Entries,
                        entry => RoutePlanTracker.FindMapPoint(run, entry.Coord),
                        plan,
                        PlannedEventOutcomes()));
            }
            catch (Exception exception)
            {
                Entry.Logger.Error($"Plan chain threading failed: {exception}");
            }
        }

        _ledger.Text = plan is null
            ? FormatCurrentResources(player, Chinese)
            : BuildLedger(run, player, plan, Chinese, _lastChain);
    }

    private static RouteVariantForecast? MatchVariant(
        IReadOnlyList<RouteVariantForecast> variants,
        IReadOnlyList<RoutePlanEntry> entries,
        RoutePlanEntry target)
    {
        var headIndex = 0;
        while (headIndex < entries.Count && entries[headIndex].IsCompleted)
            headIndex++;
        var targetIndex = -1;
        for (var index = headIndex; index < entries.Count; index++)
        {
            if (ReferenceEquals(entries[index], target))
            {
                targetIndex = index;
                break;
            }
        }

        if (targetIndex < 0)
            return null;

        // variant.Route lists the rooms BEFORE the target; the target itself is
        // implied by the Predict call. The planned rooms between the head and
        // the target must appear in order, with extras allowed only before the
        // plan head (the travelable step that leads into the plan).
        foreach (var variant in variants)
        {
            if (!MapForecastTooltipBuilder.MatchesPlanChoice(variant, target.Choice))
                continue;

            var coords = variant.Route
                .Select(choice => choice.Point.coord)
                .ToArray();
            var plannedIndex = headIndex;
            var consuming = false;
            var matched = true;
            for (var coordIndex = 0; coordIndex < coords.Length; coordIndex++)
            {
                if (plannedIndex < targetIndex && coords[coordIndex] == entries[plannedIndex].Coord)
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

            if (matched && plannedIndex == targetIndex)
                return variant;
        }

        return null;
    }

    private static bool IsAfterPlanEntry(
        RoutePlan plan,
        MapCoord earlier,
        MapCoord target)
    {
        var earlierIndex = plan.Entries
            .Select((entry, index) => (entry.Coord, index))
            .FirstOrDefault(pair => pair.Coord == earlier)
            .index;
        var targetIndex = plan.Entries
            .Select((entry, index) => (entry.Coord, index))
            .FirstOrDefault(pair => pair.Coord == target)
            .index;
        return targetIndex > earlierIndex;
    }

    private static string DescribeRoom(MapPoint? point, bool chinese)
    {
        if (point is null)
            return "?";
        return point.PointType switch
        {
            MapPointType.Unknown => chinese ? "？房间" : "? room",
            _ => MapForecastTooltipBuilder.RoomName(RoomFromPointType(point.PointType), chinese)
        };
    }

    internal static RoomType RoomFromPointType(MapPointType pointType) => pointType switch
    {
        MapPointType.Monster => RoomType.Monster,
        MapPointType.Elite => RoomType.Elite,
        MapPointType.Boss => RoomType.Boss,
        MapPointType.Shop => RoomType.Shop,
        MapPointType.Treasure => RoomType.Treasure,
        MapPointType.RestSite => RoomType.RestSite,
        _ => RoomType.Unassigned
    };

    /// <summary>
    /// The game's custom rich tags ([gold]/[blue]/...) only exist in the
    /// tip renderer; a standard RichTextLabel needs them as plain colors.
    /// </summary>
    private static string ConvertGameTags(string text)
    {
        foreach (var (tag, hex) in GameTagColors)
        {
            text = text.Replace($"[{tag}]", $"[color=#{hex}]")
                       .Replace($"[/{tag}]", "[/color]");
        }

        return text;
    }

    private static readonly (string Tag, string Hex)[] GameTagColors =
    [
        ("gold", "FFD633"),
        ("green", "40E661"),
        ("blue", "5CB8FF"),
        ("orange", "FFA629"),
        ("aqua", "26D1FF"),
        ("pink", "FF61C7"),
        ("purple", "B873FF"),
        ("red", "FF4D47")
    ];

    private static IReadOnlyList<string> FormatVariantContent(
        MapPoint? point,
        RouteVariantForecast variant,
        bool chinese)
    {
        var lines = new List<string>();
        var prefix = point is { PointType: MapPointType.Unknown } ? "? → " : string.Empty;

        if (variant.Event is { } eventDetails)
        {
            lines.Add($"[gold]{prefix}{(chinese ? "事件" : "Event")} · {eventDetails.Title}[/gold]");
            lines.Add(chinese
                ? "事件选项与后续世界线按当前规划状态生成"
                : "Event options and downstream state use the current plan snapshot");
            return lines;
        }

        if (variant.Encounter is { } encounter)
        {
            lines.Add($"[gold]{prefix}{MapForecastTooltipBuilder.RoomName(variant.RoomType, chinese)}"
                      + $" · {encounter.Title}[/gold]");
            lines.Add(chinese
                ? $"怪物：{string.Join(" + ", encounter.Monsters)}"
                : $"Monsters: {string.Join(" + ", encounter.Monsters)}");
            MapForecastTooltipBuilder.AppendRewards(lines, variant, chinese, " ");
            return lines;
        }

        if (variant.RoomType == RoomType.Shop)
        {
            if (variant.Merchant is { HasValue: true } merchant)
            {
                lines.Add($"[gold]{prefix}{(chinese
                    ? $"从当前起第 {merchant.Value!.FutureVisitOrdinal} 次商店"
                    : $"Merchant visit {merchant.Value!.FutureVisitOrdinal} from now")}[/gold]");
                MapForecastTooltipBuilder.AppendMerchantContents(lines, merchant.Value!, chinese, " ");
            }
            else
            {
                lines.Add(chinese ? "商店库存暂不可安全预测" : "No safe merchant forecast");
            }
            return lines;
        }

        lines.Add($"[gold]{prefix}{MapForecastTooltipBuilder.RoomName(variant.RoomType, chinese)}[/gold]");
        MapForecastTooltipBuilder.AppendRewards(lines, variant, chinese, " ");
        return lines;
    }

    private static (int Gold, int Cards, int Relics, int Potions, int Hp, string Text) EstimatePlanDelta(
        RoutePlanEntry entry,
        RouteVariantForecast variant,
        Player? player,
        bool chinese)
    {
        var choice = entry.Choice;
        var gold = 0;
        var cards = 0;
        var relics = 0;
        var potions = 0;
        var hp = 0;
        var notes = new List<string>();

        if (variant.Encounter is not null)
        {
            var combatChoice = RoutePlanChoice.AsCombat(choice);
            if (variant.CombatRewards is { HasValue: true } combat)
            {
                gold += combat.Value!.Gold;
                potions += combatChoice.PotionChoice is { } takenPotions
                    ? takenPotions.TakenPotions.Count
                    : Math.Min(combat.Value.Potions.Count, Math.Max(0, (player?.MaxPotionCount ?? 3) - (player?.Potions.Count() ?? 0)));
                if (combatChoice.TakeRelic)
                    relics += combat.Value.Relics.Count;
            }

            if (variant.CombatRewards?.Value?.AppliedDelta is { } applied)
            {
                gold = applied.Gold;
                cards = applied.Cards;
                relics = applied.Relics;
                potions = applied.Potions;
                hp = applied.Hp;
            }
        }
        else if (variant.RoomType == RoomType.Shop && variant.Merchant is { HasValue: true } merchant)
        {
            if (choice is RoutePlanChoice.Merchant merchantChoice)
            {
                foreach (var pick in merchantChoice.Picks)
                    gold -= CostOf(merchant.Value!, pick);
                if (merchantChoice.RemoveCard)
                {
                    gold -= merchant.Value!.CardRemovalCost;
                    cards -= 1;
                }
            }
        }
        else if (variant.RoomType == RoomType.Treasure && variant.Treasure is { HasValue: true } treasure)
        {
            gold += treasure.Value!.Gold;
            if (choice is not RoutePlanChoice.Relic { Take: false })
                relics += treasure.Value.Relics.Count;
        }
        else if (variant.RoomType == RoomType.RestSite && choice is RoutePlanChoice.RestSite rest)
        {
            if (rest.OptionId == "HEAL")
                hp = player is null ? 0 : (int)HealRestSiteOption.GetHealAmount(player);
            else if (rest.OptionId == "LIFT")
                notes.Add("最大生命+");
            else if (rest.OptionId == "COOK")
            {
                cards = rest.TargetCard is not null && rest.SecondTargetCard is not null
                    ? -2
                    : 0;
                notes.Add(rest.TargetCard is { } first && rest.SecondTargetCard is { } second
                    ? $"烹饪：{first.Entry}、{second.Entry}，最大生命+5"
                    : (chinese ? "烹饪：需要两张目标牌" : "cook: two target cards required"));
            }
            else if (rest.OptionId is "SMITH" or "CLONE" or "MEND" or "HATCH" or "KINDLE")
                notes.Add(rest.TargetCard is { } target
                    ? $"{(chinese ? "目标：" : "target: ")}{target.Entry}"
                    : (chinese ? "未选目标牌" : "no target card"));
            else if (rest.OptionId == "DIG")
                notes.Add(chinese ? "挖遗物" : "dig relic");
        }

        if (gold != 0)
            notes.Add($"{(gold > 0 ? "+" : string.Empty)}{gold}{(chinese ? "金" : "g")}");
        if (cards != 0)
            notes.Add($"{(cards > 0 ? "+" : string.Empty)}{cards}{(chinese ? "卡" : "card")}");
        if (relics != 0)
            notes.Add($"{(relics > 0 ? "+" : string.Empty)}{relics}{(chinese ? "遗物" : "relic")}");
        if (potions != 0)
            notes.Add($"{(potions > 0 ? "+" : string.Empty)}{potions}{(chinese ? "药水" : "potion")}");
        if (hp != 0)
            notes.Add($"HP {(hp > 0 ? "+" : string.Empty)}{hp}");
        if (choice is RoutePlanChoice.EventOption)
            notes.Add(chinese ? "已记选项" : "option noted");

        return (gold, cards, relics, potions, hp, notes.Count == 0
            ? (chinese ? "本层：无资源变动" : "No expected change")
            : (chinese ? "本层预计：" : "Expected: ") + string.Join("  ", notes));
    }

    private static int CostOf(MerchantInventoryForecast merchant, MerchantPick pick) => pick.Category switch
    {
        MerchantCategory.CharacterCard => IndexOr(merchant.CharacterCards, pick.Index).Cost,
        MerchantCategory.ColorlessCard => IndexOr(merchant.ColorlessCards, pick.Index).Cost,
        MerchantCategory.Relic => IndexOr(merchant.Relics, pick.Index).Cost,
        _ => IndexOr(merchant.Potions, pick.Index).Cost
    };

    private static MerchantItemForecast IndexOr(IReadOnlyList<MerchantItemForecast> items, int index) =>
        index >= 0 && index < items.Count ? items[index] : new MerchantItemForecast(ForecastItemDetails.Text("?"), 0);

    private string BuildLedger(
        RunState run,
        Player? player,
        RoutePlan plan,
        bool chinese,
        RoutePlanForecastService.PlanChain? chain)
    {
        if (player is null)
            return string.Empty;
        var potionMax = chain?.State.Player.MaxPotionCount ?? player.MaxPotionCount;

        var gold = chain?.Gold ?? player.Gold;
        var cards = chain?.Deck.Count ?? player.Deck.Cards.Count;
        var relics = chain?.State.Player.Relics.Count ?? player.Relics.Count;
        var potions = chain?.PotionSlots.Count ?? player.Potions.Count();
        var hp = chain?.State.Player.Creature.CurrentHp ?? player.Creature.CurrentHp;

        if (plan.Phase == RoutePlanPhase.Active)
        {
            foreach (var entry in plan.Entries.Where(item => !item.IsCompleted))
            {
                if (RoutePlanTracker.FindMapPoint(run, entry.Coord) is not { } point)
                    continue;
                var nodeForecast = PredictionPurityGuard.Execute(
                    run,
                    $"plan-ledger:{point.coord}",
                    () => Entry.MapForecasts.Predict(run, point, isTravelEnabled: false));
                var variant = MatchVariant(nodeForecast.RouteVariants, plan.Entries, entry);
                if (variant is null)
                    continue;
                if (chain is not null && chain.Outcomes.TryGetValue(entry.Coord, out var outcome))
                {
                    variant = variant with
                    {
                        Merchant = outcome.Merchant ?? variant.Merchant,
                        CombatRewards = outcome.CombatRewards ?? variant.CombatRewards,
                        Treasure = outcome.Treasure ?? variant.Treasure
                    };
                }
                var estimate = EstimatePlanDelta(entry, variant, player, chinese);
                if (chain is null && _eventOutcomes.TryGetValue(entry.Coord, out var eventOutcome))
                {
                    gold += eventOutcome.GoldDelta;
                    hp += eventOutcome.HpDelta;
                }
                if (chain is null)
                    gold += estimate.Gold;
                if (chain is null) cards += estimate.Cards;
                if (chain is null)
                    relics += estimate.Relics;
                if (chain is null) potions += estimate.Potions;
                if (chain is null) hp += estimate.Hp;
            }
        }

        var arrow = chinese ? "→" : "->";
        var blockedNote = chain?.BlockedAtEvent is not null
            ? (chinese
                ? "\n⚠ 计划在未完成事件处暂停；后续节点将在事件全部选完后重算"
                : "\n⚠ Plan paused at an unfinished event; later nodes refresh after all event choices are set.")
            : string.Empty;
        return chinese
            ? $"当前 金币{player.Gold} 卡{player.Deck.Cards.Count} 遗物{player.Relics.Count}"
              + $" 药水{player.Potions.Count()}/{player.MaxPotionCount} HP{player.Creature.CurrentHp}"
              + $"\n预计到计划终点 金币{gold} 卡{cards} 遗物{relics} 药水{Math.Min(potions, potionMax)}{(potions > potionMax ? "（超出槽位的药水将放弃）" : string.Empty)} HP{hp}（战损未计入）"
              + blockedNote
            : $"Now  gold {player.Gold}, deck {player.Deck.Cards.Count}, relics {player.Relics.Count}"
              + $", potions {player.Potions.Count()}, HP {player.Creature.CurrentHp}"
              + $"\nAt plan end {arrow} gold {gold}, deck {cards}, relics {relics}, potions {potions}, HP {hp}"
              + " (combat losses not included)"
              + blockedNote;
    }

    private static string FormatCurrentResources(Player? player, bool chinese)
    {
        if (player is null)
            return string.Empty;
        return chinese
            ? $"当前 金币{player.Gold} 卡{player.Deck.Cards.Count} 遗物{player.Relics.Count}"
              + $" 药水{player.Potions.Count()}/{player.MaxPotionCount} HP{player.Creature.CurrentHp}"
            : $"Now  gold {player.Gold}, deck {player.Deck.Cards.Count}, relics {player.Relics.Count}"
              + $", potions {player.Potions.Count()}, HP {player.Creature.CurrentHp}";
    }

    private static string FormatActual(RoutePlanActualDelta delta, bool chinese)
    {
        var parts = new List<string>();
        if (delta.Gold != 0)
            parts.Add($"{(chinese ? "金币" : "gold")} {delta.Gold:+#;-#;0}");
        if (delta.CardsGained.Count > 0)
            parts.Add($"{(chinese ? "获得卡" : "+cards")} {string.Join("、", delta.CardsGained)}");
        if (delta.CardsLost.Count > 0)
            parts.Add($"{(chinese ? "失去卡" : "-cards")} {string.Join("、", delta.CardsLost)}");
        if (delta.RelicsGained.Count > 0)
            parts.Add($"{(chinese ? "获得遗物" : "+relics")} {string.Join("、", delta.RelicsGained)}");
        if (delta.RelicsLost.Count > 0)
            parts.Add($"{(chinese ? "失去遗物" : "-relics")} {string.Join("、", delta.RelicsLost)}");
        if (delta.PotionsGained.Count > 0)
            parts.Add($"{(chinese ? "获得药水" : "+potions")} {string.Join("、", delta.PotionsGained)}");
        if (delta.PotionsLost.Count > 0)
            parts.Add($"{(chinese ? "失去药水" : "-potions")} {string.Join("、", delta.PotionsLost)}");
        if (delta.Hp != 0)
            parts.Add($"HP {delta.Hp:+#;-#;0}");
        return parts.Count == 0
            ? (chinese ? "实际：无变化" : "Actual: no change")
            : (chinese ? "实际：" : "Actual: ") + string.Join("  ", parts);
    }
}

internal sealed partial class RoutePlanToggleButton : Button
{
    internal const string NodeName = "SeedOracleRoutePlanToggle";
    internal const int ToggleZIndex = 175;

    private RoutePlanPanelControl? _panel;

    public RoutePlanToggleButton()
    {
        Name = NodeName;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ZIndex = ToggleZIndex;
        AnchorLeft = 1f;
        AnchorRight = 1f;
        AnchorTop = 0.69f;
        AnchorBottom = 0.69f;
        OffsetLeft = -330f;
        OffsetRight = -16f;
        OffsetTop = -24f;
        OffsetBottom = 24f;
        CustomMinimumSize = new Vector2(210f, 42f);
        TooltipText = "开启全知规划：点击尚不可进入的房间预选路线，并在左侧计划表中安排选择";
        this.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        AddThemeFontSizeOverride("font_size", 17);
        AddThemeColorOverride("font_color", StsColors.cream);
        AddThemeColorOverride("font_hover_color", StsColors.gold);
        AddThemeColorOverride("font_pressed_color", StsColors.gold);
        AddThemeStyleboxOverride("normal", PreCombatPanelStyles.CreateButtonStyle(
            new Color(0.025f, 0.07f, 0.085f, 0.94f),
            new Color(0.75f, 0.62f, 0.22f, 0.96f)));
        AddThemeStyleboxOverride("hover", PreCombatPanelStyles.CreateButtonStyle(
            new Color(0.055f, 0.13f, 0.15f, 0.98f),
            StsColors.gold));
        AddThemeStyleboxOverride("pressed", PreCombatPanelStyles.CreateButtonStyle(
            new Color(0.015f, 0.05f, 0.065f, 0.98f),
            StsColors.gold));
        Pressed += Toggle;
        ApplyState();
    }

    internal void Bind(RoutePlanPanelControl panel)
    {
        _panel = panel;
        ApplyState();
    }

    private void Toggle()
    {
        if (_panel is null
            || !GodotObject.IsInstanceValid(_panel)
            || _panel.IsQueuedForDeletion())
        {
            return;
        }

        if (_panel.Visible)
        {
            _panel.Collapse();
        }
        else
        {
            RunSeedOverviewPanel.CollapseSafely();
            PreCombatForecastPanel.CollapseSafely();
            _panel.Visible = true;
            _panel.RefreshPlan();
        }

        ApplyState();
    }

    internal void ApplyState()
    {
        Text = _panel is { Visible: true } ? "全知规划模式  ▲" : "全知规划模式  ▼";
    }
}

internal static class RoutePlanPanel
{
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);
    private static RoutePlanPanelControl? _currentPanel;
    private static RoutePlanToggleButton? _currentToggle;

    internal static bool IsPlanMode => _currentPanel is { Visible: true };

    public static RoutePlanPanelControl Refresh(NMapScreen screen)
    {
        var panel = screen.GetNodeOrNull<RoutePlanPanelControl>(RoutePlanPanelControl.NodeName);
        var created = false;
        if (panel is null || !GodotObject.IsInstanceValid(panel) || panel.IsQueuedForDeletion())
        {
            panel = new RoutePlanPanelControl();
            screen.AddChild(panel);
            created = true;
        }

        var toggle = screen.GetNodeOrNull<RoutePlanToggleButton>(RoutePlanToggleButton.NodeName);
        if (toggle is null || !GodotObject.IsInstanceValid(toggle) || toggle.IsQueuedForDeletion())
        {
            toggle = new RoutePlanToggleButton();
            screen.AddChild(toggle);
            created = true;
        }

        if (created)
            Entry.Logger.Info("[PlanPanel] plan UI created");
        panel.Configure(screen);
        toggle.Bind(panel);
        toggle.Visible = screen.IsOpen;
        if (panel.Visible)
            panel.RefreshPlan();
        if (screen._runState is { } run)
            RoutePlanOverlay.Refresh(screen, run);
        _currentPanel = panel;
        _currentToggle = toggle;
        return panel;
    }

    internal static void CollapseSafely()
    {
        if (_currentPanel is null
            || !GodotObject.IsInstanceValid(_currentPanel)
            || _currentPanel.IsQueuedForDeletion())
        {
            return;
        }

        _currentPanel.Collapse();
        _currentToggle?.ApplyState();
    }

    public static void HideToggleSafely()
    {
        try
        {
            if (_currentPanel is not null
                && GodotObject.IsInstanceValid(_currentPanel)
                && !_currentPanel.IsQueuedForDeletion())
            {
                _currentPanel.Collapse();
            }

            if (_currentToggle is not null
                && GodotObject.IsInstanceValid(_currentToggle)
                && !_currentToggle.IsQueuedForDeletion())
            {
                _currentToggle.Visible = false;
            }
        }
        catch (Exception exception)
        {
            ReportFailure("hide", exception);
        }
    }

    public static void RefreshSafely(NMapScreen screen)
    {
        try
        {
            Refresh(screen);
        }
        catch (Exception exception)
        {
            ReportFailure("refresh", exception);
        }
    }

    internal static void HandleMapPointClick(NMapPoint point)
    {
        try
        {
            if (!IsPlanMode || _currentPanel is null)
                return;
            Entry.Logger.Info(
                $"[PlanClick] coord={point.Point.coord} state={point.State} screenOpen={point._screen?.IsOpen}");
            if (point.State is not (MapPointState.Untravelable or MapPointState.Travelable))
                return;
            if (point._runState is not RunState run || point._screen is not { } screen)
                return;
            if (screen.Drawings.GetLocalDrawingMode() != DrawingMode.None)
                return;

            var error = RoutePlanTracker.ToggleNode(run, point.Point.coord);
            if (error is not null)
                Entry.Logger.Info($"[PlanClick] rejected: {error}");
            _currentPanel.ShowHint(error, error is not null);
            _currentPanel.RefreshPlan();
            _currentPanel.ScrollToBottom();
            RoutePlanOverlay.Refresh(screen, run);
            MapHoverTipPresentation.HideTip(point);
        }
        catch (Exception exception)
        {
            ReportFailure("plan-click", exception);
        }
    }

    private static void ReportFailure(string operation, Exception exception)
    {
        var root = exception.GetBaseException();
        var key = $"{operation}:{root.GetType().FullName}:{root.Message}";
        if (ReportedFailures.Add(key))
            Entry.Logger.Error($"Route plan panel {operation} failed: {root}");
    }
}

/// <summary>
/// While plan mode is open, left clicks on map rooms are planning gestures,
/// never travel: the press and release are swallowed at the raw _GuiInput
/// level and the release is routed to the plan handler. NMapPoint.OnRelease is
/// intentionally not used: it depends on the game's enable/press bookkeeping
/// that is torn down by Disable(), while hover proves map nodes still receive
/// GUI events.
/// </summary>
[HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl._GuiInput))]
internal static class NMapPointPlanClickPatch
{
    [HarmonyPrefix]
    private static bool Prefix(NClickableControl __instance, InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left })
            return true;
        if (__instance is not NMapPoint point)
            return true;
        if (point.State is not (MapPointState.Untravelable or MapPointState.Travelable))
            return true;
        if (!RoutePlanPanel.IsPlanMode)
            return true;

        var screen = point._screen;
        if (screen is null || screen.Drawings.GetLocalDrawingMode() != DrawingMode.None)
            return true;

        if (inputEvent.IsPressed())
            return false;

        RoutePlanPanel.HandleMapPointClick(point);
        return false;
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
internal static class NMapScreenSetMapRoutePlanPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance)
    {
        if (__instance._runState is { } run)
            RoutePlanTracker.OnMapSet(run);
        RoutePlanPanel.RefreshSafely(__instance);
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
internal static class NMapScreenOpenRoutePlanPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance)
    {
        if (__instance._runState is { } run)
            RoutePlanTracker.Reconcile(run);
        RoutePlanPanel.RefreshSafely(__instance);
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Close))]
internal static class NMapScreenCloseRoutePlanPatch
{
    [HarmonyPostfix]
    private static void Postfix() => RoutePlanPanel.HideToggleSafely();
}
