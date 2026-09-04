using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.addons.mega_text;
using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal sealed partial class RunSeedOverviewControl : PanelContainer
{
    internal const string NodeName = "SeedOracleRunSeedOverview";
    internal const int OverviewZIndex = 150;
    private const float PanelTopOffset = 88f;
    private const float MinimumPanelHeight = 160f;
    private const float FixedPanelHeight = 76f;
    private const float EstimatedLineHeight = 24f;
    private const int EstimatedUnitsPerLine = 52;
    private const int SafetyLineCount = 1;

    private readonly HBoxContainer _row;
    private readonly List<ActCard> _cards = [];
    private int _estimatedBodyLineCount;

    internal int DisplayedActCount { get; private set; }

    internal int DisplayedOptionCount { get; private set; }

    internal int DisplayedReplacementCount { get; private set; }

    public RunSeedOverviewControl()
    {
        Name = NodeName;
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        ZIndex = OverviewZIndex;
        SetAnchorsPreset(LayoutPreset.TopWide);
        OffsetLeft = 180f;
        OffsetTop = PanelTopOffset;
        OffsetRight = -350f;
        OffsetBottom = PanelTopOffset + MinimumPanelHeight;
        Visible = false;
        Connect(
            CanvasItem.SignalName.VisibilityChanged,
            Callable.From(QueueRenderedContentResize));

        var outerStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.025f, 0.07f, 0.085f, 0.94f),
            BorderColor = new Color(0.24f, 0.43f, 0.49f, 0.95f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        };
        AddThemeStyleboxOverride("panel", outerStyle);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 10);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 10);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 8);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 8);
        AddChild(margin);

        _row = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _row.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 8);
        margin.AddChild(_row);
    }

    public void Configure(IReadOnlyList<ActSeedOverview> acts)
    {
        DisplayedActCount = acts.Count;
        DisplayedOptionCount = acts.Sum(act => act.AncientOptions.Count);
        DisplayedReplacementCount = acts.Sum(act => act.AncientOptionReplacements.Count);
        var bodies = acts.Select(BuildBody).ToArray();
        _estimatedBodyLineCount = bodies.Length == 0
            ? 0
            : bodies.Max(EstimateWrappedLineCount);
        ResizeForBodyLines(_estimatedBodyLineCount);
        while (_cards.Count < acts.Count)
            _cards.Add(CreateCard());

        for (var index = 0; index < _cards.Count; index++)
        {
            var visible = index < acts.Count;
            _cards[index].Container.Visible = visible;
            if (!visible)
                continue;

            var act = acts[index];
            _cards[index].Header.Text = $"第{act.ActNumber}幕｜先古：{act.Ancient}";
            _cards[index].Body.Text = bodies[index];
        }

        QueueRenderedContentResize();
    }

    private void QueueRenderedContentResize()
    {
        if (!Visible)
            return;

        Callable.From(GrowForRenderedContent).CallDeferred();
    }

    private void GrowForRenderedContent()
    {
        if (!GodotObject.IsInstanceValid(this) || IsQueuedForDeletion())
            return;

        var renderedLineCount = _cards
            .Where(card => card.Container.Visible)
            .Select(card => card.Body.GetLineCount())
            .DefaultIfEmpty(0)
            .Max();
        ResizeForBodyLines(Math.Max(_estimatedBodyLineCount, renderedLineCount));
    }

    private void ResizeForBodyLines(int bodyLineCount)
    {
        var desiredHeight = FixedPanelHeight
                            + (bodyLineCount + SafetyLineCount) * EstimatedLineHeight;
        OffsetBottom = PanelTopOffset + Math.Max(MinimumPanelHeight, desiredHeight);
    }

    private static int EstimateWrappedLineCount(string text)
    {
        var lineCount = 0;
        foreach (var line in text.Split('\n'))
        {
            var displayUnits = line.Sum(character => character <= sbyte.MaxValue ? 1 : 2);
            lineCount += Math.Max(
                1,
                (int)Math.Ceiling(displayUnits / (double)EstimatedUnitsPerLine));
        }

        return lineCount;
    }

    private ActCard CreateCard()
    {
        var card = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f
        };
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.065f, 0.13f, 0.15f, 0.72f),
            CornerRadiusTopLeft = 5,
            CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5,
            CornerRadiusBottomRight = 5
        });
        _row.AddChild(card);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 10);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 10);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 5);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 5);
        card.AddChild(margin);

        var column = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        column.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 2);
        margin.AddChild(column);

        var header = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        header.ApplyLocaleFontSubstitution(FontType.Bold, ThemeConstants.Label.Font);
        header.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 18);
        header.AddThemeColorOverride(ThemeConstants.Label.FontColor, StsColors.gold);
        column.AddChild(header);

        var body = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Top,
            ClipText = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        body.ApplyLocaleFontSubstitution(FontType.Regular, ThemeConstants.Label.Font);
        body.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, 14);
        body.AddThemeColorOverride(ThemeConstants.Label.FontColor, StsColors.cream);
        column.AddChild(body);

        return new ActCard(card, header, body);
    }

    private static string BuildBody(ActSeedOverview act)
    {
        var boss = act.SecondBoss is null
            ? act.Boss
            : $"{act.Boss} → {act.SecondBoss}";
        var optionLabel = act.AncientOptionAccuracy switch
        {
            AncientOptionAccuracy.Recorded => "已见选项",
            AncientOptionAccuracy.CurrentStateProjection => "选项（按当前状态预测）",
            _ => "选项"
        };
        var options = act.AncientOptions.Count == 0
            ? act.AncientOptionFailure ?? "暂不可用"
            : string.Join(" / ", act.AncientOptions.Select(FormatOption));
        var lines = new List<string>
        {
            $"Boss：{boss}",
            $"{optionLabel}：{options}"
        };
        if (act.AncientOptionReplacements.Count > 0)
        {
            lines.Add("条件替换（其余条件不变）：");
            lines.AddRange(act.AncientOptionReplacements.Select(FormatReplacement));
        }

        return string.Join('\n', lines);
    }

    private static string FormatReplacement(AncientOptionReplacement replacement)
    {
        var added = replacement.WhenConditionMet.Count == 0
            ? "无新选项"
            : string.Join(" / ", replacement.WhenConditionMet);
        var removed = replacement.WhenConditionNotMet.Count == 0
            ? "无"
            : string.Join(" / ", replacement.WhenConditionNotMet);
        return $"若{replacement.Condition}：{added} 替换 {removed}";
    }

    private static string FormatOption(AncientOptionSummary option)
    {
        if (option.WasChosen)
            return option.Title + "（已选）";
        if (option.IsLocked)
            return option.Title + "（锁定）";
        return option.Title;
    }

    private sealed record ActCard(
        PanelContainer Container,
        Label Header,
        Label Body);
}

internal sealed partial class RunSeedOverviewToggleButton : Button
{
    internal const string NodeName = "SeedOracleRunSeedOverviewToggle";
    internal const int ToggleZIndex = 175;

    private RunSeedOverviewControl? _panel;

    internal bool IsExpanded { get; private set; }

    public RunSeedOverviewToggleButton()
    {
        Name = NodeName;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ZIndex = ToggleZIndex;
        AnchorLeft = 0.56f;
        AnchorRight = 0.56f;
        AnchorTop = 0f;
        AnchorBottom = 0f;
        OffsetLeft = -105f;
        OffsetRight = 105f;
        OffsetTop = 38f;
        OffsetBottom = 80f;
        CustomMinimumSize = new Vector2(210f, 42f);
        TooltipText = "显示或隐藏三幕先古与 Boss 预测";

        this.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        AddThemeFontSizeOverride("font_size", 17);
        AddThemeColorOverride("font_color", StsColors.cream);
        AddThemeColorOverride("font_hover_color", StsColors.gold);
        AddThemeColorOverride("font_pressed_color", StsColors.gold);
        AddThemeStyleboxOverride(
            "normal",
            CreateStyle(new Color(0.025f, 0.07f, 0.085f, 0.94f), new Color(0.24f, 0.43f, 0.49f, 0.96f)));
        AddThemeStyleboxOverride(
            "hover",
            CreateStyle(new Color(0.055f, 0.13f, 0.15f, 0.98f), StsColors.gold));
        AddThemeStyleboxOverride(
            "pressed",
            CreateStyle(new Color(0.015f, 0.05f, 0.065f, 0.98f), StsColors.gold));
        Pressed += Toggle;
        ApplyState(expanded: false);
    }

    internal void Bind(RunSeedOverviewControl panel, bool expanded)
    {
        _panel = panel;
        _panel.Visible = expanded;
        ApplyState(expanded);
    }

    private void Toggle()
    {
        if (_panel is null
            || !GodotObject.IsInstanceValid(_panel)
            || _panel.IsQueuedForDeletion())
        {
            return;
        }

        var expanded = !_panel.Visible;
        if (expanded)
            PreCombatForecastPanel.CollapseSafely();
        _panel.Visible = expanded;
        ApplyState(expanded);
        RunSeedOverviewPanel.RememberExpanded(expanded);
    }

    private void ApplyState(bool expanded)
    {
        IsExpanded = expanded;
        Text = expanded
            ? "先古 / Boss  ▲"
            : "先古 / Boss  ▼";
    }

    private static StyleBoxFlat CreateStyle(Color background, Color border) => new()
    {
        BgColor = background,
        BorderColor = border,
        BorderWidthLeft = 2,
        BorderWidthTop = 2,
        BorderWidthRight = 2,
        BorderWidthBottom = 2,
        CornerRadiusTopLeft = 8,
        CornerRadiusTopRight = 8,
        CornerRadiusBottomLeft = 8,
        CornerRadiusBottomRight = 8,
        ContentMarginLeft = 10f,
        ContentMarginRight = 10f,
        ContentMarginTop = 4f,
        ContentMarginBottom = 4f
    };
}

internal static class RunSeedOverviewPanel
{
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);
    private static RunSeedOverviewControl? _currentPanel;
    private static RunSeedOverviewToggleButton? _currentToggle;
    private static bool _isExpanded;

    public static RunSeedOverviewControl Refresh(NMapScreen screen)
    {
        var panel = screen.GetNodeOrNull<RunSeedOverviewControl>(RunSeedOverviewControl.NodeName);
        if (panel is null || !GodotObject.IsInstanceValid(panel) || panel.IsQueuedForDeletion())
        {
            panel = new RunSeedOverviewControl();
            screen.AddChild(panel);
        }

        var topBar = NRun.Instance?.GlobalUi.TopBar
                     ?? throw new InvalidOperationException(
                         "Seed Oracle could not locate the active run top bar.");
        var toggle = topBar.GetNodeOrNull<RunSeedOverviewToggleButton>(
            RunSeedOverviewToggleButton.NodeName);
        if (toggle is null || !GodotObject.IsInstanceValid(toggle) || toggle.IsQueuedForDeletion())
        {
            toggle = new RunSeedOverviewToggleButton();
            topBar.AddChild(toggle);
        }

        panel.Configure(RunSeedOverviewPredictor.Predict(screen._runState));
        toggle.Bind(panel, _isExpanded);
        toggle.Visible = screen.IsOpen;
        _currentPanel = panel;
        _currentToggle = toggle;
        return panel;
    }

    internal static void RememberExpanded(bool expanded) => _isExpanded = expanded;

    internal static void CollapseSafely()
    {
        if (_currentPanel is null
            || !GodotObject.IsInstanceValid(_currentPanel)
            || _currentPanel.IsQueuedForDeletion())
        {
            return;
        }
        _currentPanel.Visible = false;
        _isExpanded = false;
        if (_currentToggle is not null
            && GodotObject.IsInstanceValid(_currentToggle)
            && !_currentToggle.IsQueuedForDeletion())
        {
            _currentToggle.Bind(_currentPanel, expanded: false);
        }
    }

    public static void HideToggleSafely()
    {
        try
        {
            var toggle = NRun.Instance?.GlobalUi.TopBar
                .GetNodeOrNull<RunSeedOverviewToggleButton>(
                    RunSeedOverviewToggleButton.NodeName);
            if (toggle is not null
                && GodotObject.IsInstanceValid(toggle)
                && !toggle.IsQueuedForDeletion())
            {
                toggle.Visible = false;
            }
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            var key = $"hide:{root.GetType().FullName}:{root.Message}";
            if (ReportedFailures.Add(key))
                Entry.Logger.Error($"Run seed overview toggle hide failed: {root}");
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
            var root = exception.GetBaseException();
            var key = $"{root.GetType().FullName}:{root.Message}";
            if (ReportedFailures.Add(key))
                Entry.Logger.Error($"Run seed overview failed: {root}");
        }
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
internal static class NMapScreenSetMapOverviewPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) =>
        RunSeedOverviewPanel.RefreshSafely(__instance);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
internal static class NMapScreenOpenOverviewPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) =>
        RunSeedOverviewPanel.RefreshSafely(__instance);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Close))]
internal static class NMapScreenCloseOverviewPatch
{
    [HarmonyPostfix]
    private static void Postfix() => RunSeedOverviewPanel.HideToggleSafely();
}
