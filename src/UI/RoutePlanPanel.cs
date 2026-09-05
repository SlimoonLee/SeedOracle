using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
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
    private NMapScreen? _screen;
    private string? _stateToken;

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
            _ledger.Text = FormatCurrentResources(player, chinese);
            return;
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
                _planList.AddChild(BuildPlannedRow(run, plan, entry, chinese));
        }

        _ledger.Text = BuildLedger(run, player, plan, chinese);
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

    private PanelContainer BuildPlannedRow(RunState run, RoutePlan plan, RoutePlanEntry entry, bool chinese)
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
        if (point is not null)
        {
            nodeForecast = PredictionPurityGuard.Execute(
                run,
                $"plan:{point.coord}",
                () => Entry.MapForecasts.Predict(run, point, isTravelEnabled: false));
            variant = MatchVariant(nodeForecast.RouteVariants, plan.Entries, entry);
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
            variant is null ? string.Empty : EstimatePlanDelta(entry, variant, player, chinese).Text,
            17,
            new Color(0.15f, 0.82f, 1f));
        box.AddChild(deltaLabel);

        if (variant is not null)
            BuildChoiceControls(box, plan, entry, variant, player, chinese, deltaLabel);
        return row;
    }

    private void BuildChoiceControls(
        VBoxContainer box,
        RoutePlan plan,
        RoutePlanEntry entry,
        RouteVariantForecast variant,
        Player? player,
        bool chinese,
        Label deltaLabel)
    {
        var choiceBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        choiceBox.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 2);
        box.AddChild(choiceBox);

        void Update(RoutePlanChoice choice)
        {
            var updated = entry with { Choice = choice };
            plan.Entries = plan.Entries
                .Select(item => item == entry ? updated : item)
                .ToArray();
            deltaLabel.Text = EstimatePlanDelta(updated, variant, player, chinese).Text;
            RefreshLedgerOnly();
        }

        if (variant.Encounter is not null)
        {
            if (variant.CombatRewards is { HasValue: true } combat)
            {
                var bundles = combat.Value!.CardRewards;
                if (bundles.Count > 0)
                {
                    var options = new List<(int Bundle, int Card, string Name)>();
                    for (var bundleIndex = 0; bundleIndex < bundles.Count; bundleIndex++)
                    {
                        foreach (var card in bundles[bundleIndex])
                            options.Add((bundleIndex, options.Count, card.Name));
                    }

                    var select = new OptionButton
                    {
                        MouseFilter = MouseFilterEnum.Stop,
                        FocusMode = FocusModeEnum.None
                    };
                    select.AddThemeFontSizeOverride("font_size", 17);
                    select.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                    select.AddItem(chinese ? "卡牌奖励：跳过" : "Card reward: skip");
                    foreach (var option in options)
                        select.AddItem(chinese ? $"拿 {option.Name}" : $"Take {option.Name}");
                    var choice = entry.Choice as RoutePlanChoice.CardReward;
                    select.Select(choice is { Skip: false }
                        ? 1 + options.FindIndex(option =>
                              option.Bundle == choice.BundleIndex && option.Card == choice.CardIndex)
                        : 0);
                    select.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
                    {
                        if (index <= 0)
                            Update(new RoutePlanChoice.CardReward(0, 0, Skip: true));
                        else
                            Update(new RoutePlanChoice.CardReward(
                                options[(int)index - 1].Bundle,
                                options[(int)index - 1].Card,
                                Skip: false));
                    });
                    choiceBox.AddChild(select);
                }

                if (combat.Value!.Relics.Count > 0)
                {
                    AddTakeToggle(
                        choiceBox,
                        chinese ? "拾取精英遗物" : "Take elite relic",
                        entry.Choice is RoutePlanChoice.Relic { Take: false } ? false : true,
                        take => Update(new RoutePlanChoice.Relic(take)));
                }
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
                    var removeCard = entry.Choice is RoutePlanChoice.Merchant { RemoveCard: true };
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
        else if (variant.Event is not null && variant.EventContents is { HasValue: true } eventContents)
        {
            var options = eventContents.Value!.Options;
            var select = new OptionButton
            {
                MouseFilter = MouseFilterEnum.Stop,
                FocusMode = FocusModeEnum.None
            };
            select.AddThemeFontSizeOverride("font_size", 17);
            select.ApplyLocaleFontSubstitution(FontType.Regular, "font");
            select.AddItem(chinese ? "事件选项：未选" : "Event: not chosen");
            foreach (var option in options)
                select.AddItem(option.Option);
            var eventChoice = entry.Choice as RoutePlanChoice.EventOption;
            select.Select(eventChoice is null ? 0 : eventChoice.OptionIndex + 1);
            select.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
                Update(new RoutePlanChoice.EventOption((int)index - 1)));
            choiceBox.AddChild(select);
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
            var select = new OptionButton
            {
                MouseFilter = MouseFilterEnum.Stop,
                FocusMode = FocusModeEnum.None
            };
            select.AddThemeFontSizeOverride("font_size", 17);
            select.ApplyLocaleFontSubstitution(FontType.Regular, "font");
            select.AddItem(chinese ? "休息点：跳过" : "Rest site: skip");
            select.AddItem(chinese ? "休息回血" : "Rest (heal)");
            select.Select(entry.Choice is RoutePlanChoice.Rest { Heal: true } ? 1 : 0);
            select.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
                Update(new RoutePlanChoice.Rest(Heal: index == 1)));
            choiceBox.AddChild(select);
        }
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
        _ledger.Text = BuildLedger(run, player, RoutePlanTracker.Current!, Chinese);
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
            if (variant.EventContents is { } eventContents)
                MapForecastTooltipBuilder.AppendEventContents(lines, eventContents, chinese, " ");
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
            if (variant.CombatRewards is { HasValue: true } combat)
            {
                gold += combat.Value!.Gold;
                potions += combat.Value.Potions.Count;
                if (choice is not RoutePlanChoice.Relic { Take: false })
                    relics += combat.Value.Relics.Count;
            }

            if (choice is RoutePlanChoice.CardReward { Skip: false })
                cards += 1;
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
        else if (variant.RoomType == RoomType.RestSite && choice is RoutePlanChoice.Rest { Heal: true })
        {
            hp = player is null ? 0 : (int)HealRestSiteOption.GetHealAmount(player);
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

    private string BuildLedger(RunState run, Player? player, RoutePlan plan, bool chinese)
    {
        if (player is null)
            return string.Empty;

        var gold = player.Gold;
        var cards = player.Deck.Cards.Count;
        var relics = player.Relics.Count;
        var potions = player.Potions.Count();
        var hp = player.Creature.CurrentHp;

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
                var estimate = EstimatePlanDelta(entry, variant, player, chinese);
                gold += estimate.Gold;
                cards += estimate.Cards;
                relics += estimate.Relics;
                potions += estimate.Potions;
                hp += estimate.Hp;
            }
        }

        var arrow = chinese ? "→" : "->";
        return chinese
            ? $"当前 金币{player.Gold} 卡{player.Deck.Cards.Count} 遗物{player.Relics.Count}"
              + $" 药水{player.Potions.Count()} HP{player.Creature.CurrentHp}"
              + $"\n预计到计划终点 金币{gold} 卡{cards} 遗物{relics} 药水{potions} HP{hp}（战损未计入）"
            : $"Now  gold {player.Gold}, deck {player.Deck.Cards.Count}, relics {player.Relics.Count}"
              + $", potions {player.Potions.Count()}, HP {player.Creature.CurrentHp}"
              + $"\nAt plan end {arrow} gold {gold}, deck {cards}, relics {relics}, potions {potions}, HP {hp}"
              + " (combat losses not included)";
    }

    private static string FormatCurrentResources(Player? player, bool chinese)
    {
        if (player is null)
            return string.Empty;
        return chinese
            ? $"当前 金币{player.Gold} 卡{player.Deck.Cards.Count} 遗物{player.Relics.Count}"
              + $" 药水{player.Potions.Count()} HP{player.Creature.CurrentHp}"
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
