using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.Validation;

namespace SeedOracle.UI;

internal enum ManualPreCombatItemStatus
{
    Ready,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

internal sealed partial class PreCombatForecastPanelControl : PanelContainer
{
    internal const string NodeName = "SeedOraclePreCombatForecastPanel";
    internal const int PanelZIndex = 160;
    private const float PanelTopOffset = 88f;

    private readonly Label _title;
    private readonly Label _summary;
    private readonly OptionButton _budgetSelect;
    private readonly OptionButton _parallelismSelect;
    private readonly Button _stopButton;
    private readonly Button _closeButton;
    private readonly VBoxContainer _targetList;
    private readonly List<PreCombatTargetRow> _rows = [];
    private NMapScreen? _screen;
    private List<PreCombatForecastTarget> _targets = [];
    private CancellationTokenSource? _cancellation;
    private int _batchGeneration;
    private int _activeTargetIndex = -1;
    private string? _stateToken;
    private bool _refreshAfterCurrent;

    internal event Action? PresentationChanged;

    internal int DisplayedTargetCount => _targets.Count;

    internal int ManualCalculationButtonCount => _rows.Count;

    internal int RouteBadgeCount => _rows.Count(row => row.HasRouteBadge);

    internal bool IsRunning { get; private set; }

    internal int SelectedSearchBudgetMilliseconds => _budgetSelect.GetSelectedId() switch
    {
        5_000 => 5_000,
        12_000 => 12_000,
        20_000 => 20_000,
        _ => 8_000,
    };

    internal int? SelectedParallelism
    {
        get
        {
            var selected = _parallelismSelect.GetSelectedId();
            return selected <= 0 ? null : selected;
        }
    }

    public PreCombatForecastPanelControl()
    {
        Name = NodeName;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ZIndex = PanelZIndex;
        SetAnchorsPreset(LayoutPreset.TopWide);
        OffsetLeft = 260f;
        OffsetTop = PanelTopOffset;
        OffsetRight = -350f;
        OffsetBottom = 820f;
        Visible = false;
        AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
            new Color(0.025f, 0.07f, 0.085f, 0.97f),
            new Color(0.24f, 0.43f, 0.49f, 0.98f),
            9));

        var outerMargin = new MarginContainer { MouseFilter = MouseFilterEnum.Pass };
        outerMargin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 16);
        outerMargin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 16);
        outerMargin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 12);
        outerMargin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 12);
        AddChild(outerMargin);

        var column = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        column.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 8);
        outerMargin.AddChild(column);

        var header = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        header.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 10);
        column.AddChild(header);

        _title = PreCombatPanelStyles.CreateLabel(
            "战斗威胁评估",
            22,
            StsColors.gold,
            bold: true);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_title);

        header.AddChild(PreCombatPanelStyles.CreateLabel("搜索预算", 15, StsColors.cream));
        _budgetSelect = CreateOptionButton(150f);
        _budgetSelect.AddItem("快速 · 5 秒", 5_000);
        _budgetSelect.AddItem("标准 · 8 秒", 8_000);
        _budgetSelect.AddItem("深入 · 12 秒", 12_000);
        _budgetSelect.AddItem("极高 · 20 秒", 20_000);
        _budgetSelect.Selected = 1;
        _budgetSelect.TooltipText = "每个战斗节点的搜索时间；提高预算会增加结果质量和等待时间";
        _budgetSelect.ItemSelected += _ => RefreshCachedRows();
        header.AddChild(_budgetSelect);

        header.AddChild(PreCombatPanelStyles.CreateLabel("并行度", 15, StsColors.cream));
        _parallelismSelect = CreateOptionButton(175f);
        _parallelismSelect.AddItem("沿用求解器设置", 0);
        foreach (var degree in new[] { 1, 2, 4, 8, 16 })
            _parallelismSelect.AddItem($"{degree} 线程", degree);
        _parallelismSelect.Selected = 0;
        _parallelismSelect.TooltipText = "限制单个 Combat Solver 搜索使用的并行线程数";
        _parallelismSelect.ItemSelected += _ => RefreshCachedRows();
        header.AddChild(_parallelismSelect);

        _stopButton = PreCombatPanelStyles.CreateButton("停止", 82f);
        _stopButton.TooltipText = "停止当前隔离 worker，并取消尚未开始的节点";
        _stopButton.Pressed += StopCalculation;
        _stopButton.Disabled = true;
        header.AddChild(_stopButton);

        _closeButton = PreCombatPanelStyles.CreateButton("关闭", 82f);
        _closeButton.Pressed += HideWhenIdle;
        header.AddChild(_closeButton);

        BuildWorkerToolbar(column);

        var tabs = new TabContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        column.AddChild(tabs);

        var routeTab = new VBoxContainer
        {
            Name = "确定路线",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        routeTab.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 8);
        tabs.AddChild(routeTab);

        var manualHint = PreCombatPanelStyles.CreateLabel(
            "每项右侧单独计算；相同状态与设置会恢复缓存，未点击的路线不消耗性能。",
            13,
            new Color(0.63f, 0.75f, 0.79f));
        manualHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        routeTab.AddChild(manualHint);

        _summary = PreCombatPanelStyles.CreateLabel(string.Empty, 15, StsColors.cream);
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        routeTab.AddChild(_summary);

        var separator = new HSeparator { MouseFilter = MouseFilterEnum.Ignore };
        separator.AddThemeConstantOverride("separation", 2);
        routeTab.AddChild(separator);

        var scroll = new ScrollContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        routeTab.AddChild(scroll);

        _targetList = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkBegin
        };
        _targetList.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 8);
        scroll.AddChild(_targetList);

        BuildSimulationTab(tabs);
        SetProcess(true);
    }

    internal void Configure(NMapScreen screen, bool mapChanged)
    {
        if (mapChanged && IsRunning)
        {
            _screen = screen;
            _refreshAfterCurrent = true;
            StopCalculation();
            return;
        }
        _screen = screen;
        if (IsRunning)
            return;

        _targets = CollectTargets(screen);
        _stateToken = TryCaptureStateToken(screen._runState);
        RebuildRows();
        ConfigureSimulation(screen._runState);
        UpdateReadySummary();
        RefreshWorkerStatus(force: true);
    }

    internal void ShowWithoutCalculating()
    {
        Visible = true;
        PresentationChanged?.Invoke();
    }

    internal void Collapse()
    {
        if (IsRunning)
            return;
        Visible = false;
        RouteNodeMarkerOverlay.ClearAll();
        if (!KeepWorkerAlive)
            ReleaseWorkerSafely();
        PresentationChanged?.Invoke();
    }

    internal void CancelAndHide()
    {
        StopCalculation();
        Visible = false;
        RouteNodeMarkerOverlay.ClearAll();
        if (!KeepWorkerAlive)
            ReleaseWorkerSafely();
        PresentationChanged?.Invoke();
    }

    internal static List<PreCombatForecastTarget> CollectTargets(NMapScreen screen)
    {
        if (!screen.IsTravelEnabled || CombatManager.Instance.IsInProgress)
            return [];

        var run = screen._runState;
        var candidates = new List<PreCombatForecastTarget>();
        foreach (var point in screen._mapPointDictionary.Values
                     .Where(node => node.State != MapPointState.Traveled)
                     .Select(node => node.Point)
                     .DistinctBy(point => point.coord)
                     .OrderBy(point => point.coord.row)
                     .ThenBy(point => point.coord.col))
        {
            try
            {
                var pointCandidates = PredictionPurityGuard.Execute(
                    run,
                    $"manual-precombat:{point.coord}",
                    () => BuildPointTargets(run, point));
                candidates.AddRange(pointCandidates);
            }
            catch (Exception exception)
            {
                Entry.ReportPredictionFailure(point, exception);
            }
        }

        return candidates
            .GroupBy(target => string.Join(
                '|',
                target.Point.coord,
                target.RoomType,
                target.Encounter.Id,
                target.IsSecondBoss,
                target.PlayerCurrentHpOverride?.ToString() ?? "live-hp",
                string.Join(';', (target.InterveningMapPoints ?? []).Select(static step =>
                    $"{step.MapColumn},{step.ActFloor},{step.MapPointType},{step.RoomType}"))))
            .Select(group =>
            {
                var first = group.First();
                return first with
                {
                    MinimumSteps = group.Min(target => target.MinimumSteps),
                    RouteSummaries = group
                        .SelectMany(target => target.RouteSummaries ?? [])
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    InterveningRoomSummaries = group
                        .SelectMany(target => target.InterveningRoomSummaries ?? [])
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    UsesCurrentStateAssumption = group.Any(target => target.UsesCurrentStateAssumption),
                    StateScenario = first.PlayerCurrentHpOverride is not null
                        ? first.StateScenario
                        : group.Any(target => target.UsesCurrentStateAssumption)
                            ? "沿途状态保持当前值"
                            : first.StateScenario
                };
            })
            .OrderBy(target => target.MinimumSteps)
            .ThenBy(target => target.Point.coord.row)
            .ThenBy(target => target.Point.coord.col)
            .ThenBy(target => target.Encounter.Id.Entry, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<PreCombatForecastTarget> BuildPointTargets(RunState run, MapPoint point)
    {
        var exploration = RouteStateExplorer.Explore(run, point);
        if (exploration.Paths.Count == 0)
            return [];

        var targetTotalFloor = run.TotalFloor - run.ActFloor + point.coord.row + 1;
        var results = new List<PreCombatForecastTarget>();
        foreach (var worldline in RouteWorldlinePredictor.Predict(run, point, exploration.Paths))
        {
            if (worldline.TargetEncounter is null
                || worldline.TargetRoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
            {
                continue;
            }

            var interveningRooms = worldline.ResolvedRooms
                .Take(Math.Max(0, worldline.ResolvedRooms.Count - 1))
                .ToArray();
            if (interveningRooms.Any(room => room is RoomType.Monster or RoomType.Elite or RoomType.Boss))
            {
                // This target is not the next combat on this route, so current player/combat state cannot represent it.
                continue;
            }
            if (interveningRooms.Any(room => room == RoomType.Event))
            {
                // Event options can consume Niche and other combat-relevant RNG streams. Until a concrete option
                // has been chosen, skipping the event in the worker would make the combat look more certain than it is.
                continue;
            }

            var generated = EncounterCompositionPredictor.Generate(
                run,
                targetTotalFloor,
                [worldline.TargetEncounter]).Single();
            var routeSummary = FormatRouteSummary(worldline);
            var roomSummary = FormatInterveningRooms(worldline);
            var interveningMapPoints = worldline.Path.Steps
                .Take(interveningRooms.Length)
                .Zip(interveningRooms)
                .Select(static pair => new CombatSolverMapStep(
                    pair.First.Point.coord.col,
                    pair.First.Point.coord.row + 1,
                    pair.Second,
                    pair.First.Point.PointType))
                .ToArray();
            var baseline = new PreCombatForecastTarget(
                point,
                worldline.TargetRoomType,
                generated.Details,
                ReferenceEquals(point, run.Map.SecondBossMapPoint),
                worldline.Path.Length,
                [routeSummary],
                roomSummary.Length == 0 ? [] : [roomSummary],
                interveningMapPoints,
                interveningRooms.Length > 0,
                StateScenario: interveningRooms.Length == 0
                    ? "当前战斗状态（目标为下一步）"
                    : "沿途状态保持当前值");
            results.Add(baseline);

            var restCount = interveningRooms.Count(room => room == RoomType.RestSite);
            if (restCount > 0)
            {
                var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
                if (player is not null)
                {
                    var healedHp = player.Creature.CurrentHp;
                    var healAmount = HealRestSiteOption.GetHealAmount(player);
                    for (var restIndex = 0; restIndex < restCount; restIndex++)
                    {
                        healedHp = (int)Math.Min(
                            player.Creature.MaxHp,
                            (decimal)healedHp + healAmount);
                    }
                    if (healedHp > player.Creature.CurrentHp)
                    {
                        results.Add(baseline with
                        {
                            PlayerCurrentHpOverride = healedHp,
                            StateScenario = restCount == 1
                                ? $"若可在前置篝火休息（入战 {healedHp} HP）"
                                : $"若可在 {restCount} 个前置篝火均休息（入战 {healedHp} HP）"
                        });
                    }
                }
            }
        }
        return results;
    }

    private static string FormatRouteSummary(RouteWorldline worldline)
    {
        var decisions = worldline.Route
            .Where(choice => choice.IsDecision)
            .Select(choice => $"第 {choice.Floor} 层左起第 {choice.PositionFromLeft} 个")
            .ToArray();
        return decisions.Length == 0
            ? worldline.Path.Length == 1 ? "下一步" : "唯一线路"
            : string.Join(" → ", decisions);
    }

    private static string FormatInterveningRooms(RouteWorldline worldline)
    {
        var rooms = worldline.Path.Steps
            .Take(Math.Max(0, worldline.Path.Steps.Count - 1))
            .Zip(worldline.ResolvedRooms.Take(Math.Max(0, worldline.ResolvedRooms.Count - 1)))
            .Select(pair => $"第 {pair.First.Point.coord.row + 1} 层{RoomName(pair.Second)}")
            .ToArray();
        return string.Join(" → ", rooms);
    }

    private static string RoomName(RoomType room) => room switch
    {
        RoomType.RestSite => "篝火",
        RoomType.Treasure => "宝箱",
        RoomType.Shop => "商店",
        RoomType.Event => "事件",
        _ => room.ToString(),
    };

    private void HandleRowAction(int index)
    {
        if (index < 0 || index >= _rows.Count)
            return;
        if (IsRunning)
        {
            if (_activeTargetIndex == index)
                StopCalculation();
            return;
        }

        StartCalculation(index, forceRefresh: _rows[index].HasResult);
    }

    private void StartCalculation(int index, bool forceRefresh)
    {
        if (IsRunning
            || _screen is null
            || !GodotObject.IsInstanceValid(_screen)
            || index < 0
            || index >= _targets.Count)
        {
            return;
        }
        if (!Entry.CombatSolver.SupportsPreCombatForecast)
        {
            SetSummary("未检测到支持战前 API 的 Combat Solver 版本。", StsColors.red);
            return;
        }

        var run = _screen._runState;
        var currentStateToken = TryCaptureStateToken(run);
        if (currentStateToken is null)
            return;
        if (_stateToken is not null
            && !currentStateToken.Equals(_stateToken, StringComparison.Ordinal))
        {
            _targets = CollectTargets(_screen);
            _stateToken = currentStateToken;
            RebuildRows();
            SetSummary("跑局状态已经变化，列表与缓存已按当前状态刷新；请重新选择要计算的路线。", StsColors.gold);
            return;
        }
        _stateToken = currentStateToken;

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var generation = ++_batchGeneration;
        _activeTargetIndex = index;
        IsRunning = true;
        var row = _rows[index];
        row.SetStatus(ManualPreCombatItemStatus.Running);
        ApplyRunningState();
        SetSummary(
            $"正在计算{row.RouteName}：第 {_targets[index].ActFloor} 层 {_targets[index].Encounter.Title}；其他路线不会排队。",
            StsColors.gold);
        PresentationChanged?.Invoke();

        var target = _targets[index];
        var options = BuildOptions() with
        {
            PlayerCurrentHpOverride = target.PlayerCurrentHpOverride,
            InterveningMapPoints = target.InterveningMapPoints,
            ForceRefresh = forceRefresh
        };
        _ = RunSingleAsync(
            run,
            target,
            row,
            currentStateToken,
            options,
            forceRefresh,
            generation,
            _cancellation.Token);
    }

    private async Task RunSingleAsync(
        RunState run,
        PreCombatForecastTarget target,
        PreCombatTargetRow row,
        string stateToken,
        CombatSolverForecastOptions options,
        bool forceRefresh,
        int generation,
        CancellationToken cancellationToken)
    {
        CombatSolverForecastResult? result = null;
        try
        {
            result = await PreCombatForecastCoordinator.CalculateAsync(
                run,
                target,
                stateToken,
                options,
                forceRefresh,
                cancellationToken);

            if (generation != _batchGeneration || !GodotObject.IsInstanceValid(this))
                return;
            row.SetResult(result, fromCache: false);
            RefreshHoveredTarget(target);
            if (result.IsSuccess)
            {
                SetSummary(
                    $"{row.RouteName}计算完成；结果已按当前跑局状态和搜索设置缓存。可继续选择其他路线。",
                    StsColors.cream);
            }
            else if (result.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                SetSummary($"已停止{row.RouteName}的计算；没有启动其他路线。", StsColors.gold);
            }
            else
            {
                SetSummary(
                    $"{row.RouteName}计算失败：{ShortError(result.Error ?? result.Status)}",
                    StsColors.red);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _batchGeneration && GodotObject.IsInstanceValid(this))
            {
                row.SetStatus(ManualPreCombatItemStatus.Cancelled);
                SetSummary($"已停止{row.RouteName}的计算；没有启动其他路线。", StsColors.gold);
            }
        }
        catch (Exception exception)
        {
            if (generation == _batchGeneration && GodotObject.IsInstanceValid(this))
            {
                row.SetFailure(exception.GetBaseException().Message);
                SetSummary(
                    $"{row.RouteName}计算失败：{ShortError(exception.GetBaseException().Message)}",
                    StsColors.red);
            }
            Entry.Logger.Error($"Manual pre-combat calculation failed: {exception.GetBaseException()}");
        }
        finally
        {
            if (generation == _batchGeneration && GodotObject.IsInstanceValid(this))
            {
                IsRunning = false;
                _activeTargetIndex = -1;
                _cancellation?.Dispose();
                _cancellation = null;
                ApplyRunningState();
                PresentationChanged?.Invoke();
                if (_refreshAfterCurrent && _screen is not null && GodotObject.IsInstanceValid(_screen))
                {
                    _refreshAfterCurrent = false;
                    Configure(_screen, mapChanged: true);
                }
            }
        }
    }

    private CombatSolverForecastOptions BuildOptions()
    {
        var searchBudget = SelectedSearchBudgetMilliseconds;
        var overallTimeout = searchBudget switch
        {
            <= 5_000 => 50_000,
            <= 8_000 => 60_000,
            <= 12_000 => 75_000,
            _ => 100_000,
        };
        return new CombatSolverForecastOptions(
            searchBudget,
            overallTimeout,
            SelectedParallelism,
            CancelWorkerWhenCallerCancels: true,
            CloseWorkerAfterRequest: !KeepWorkerAlive,
            WorkerIdleTimeoutMilliseconds: SelectedWorkerIdleTimeoutMilliseconds);
    }

    private void StopCalculation()
    {
        if (!IsRunning || _cancellation is null || _cancellation.IsCancellationRequested)
            return;
        _cancellation.Cancel();
        _stopButton.Disabled = true;
        if (_activeTargetIndex >= 0 && _activeTargetIndex < _rows.Count)
            _rows[_activeTargetIndex].SetStopping();
        if (IsSimulationRunning)
            SetSimulationSummary("正在停止当前模拟批次……", StsColors.gold);
        else
            SetSummary("正在停止当前后台计算……", StsColors.gold);
    }

    private void HideWhenIdle()
    {
        if (IsRunning)
            return;
        Collapse();
        PreCombatForecastPanel.RememberExpanded(false);
    }

    private void ApplyRunningState()
    {
        _stopButton.Disabled = !IsRunning;
        _closeButton.Disabled = IsRunning;
        _budgetSelect.Disabled = IsRunning;
        _parallelismSelect.Disabled = IsRunning;
        for (var index = 0; index < _rows.Count; index++)
            _rows[index].SetInteractionEnabled(!IsRunning || index == _activeTargetIndex);
        ApplyExtendedRunningState();
    }

    private void RebuildRows()
    {
        foreach (var child in _targetList.GetChildren())
            child.QueueFree();
        _rows.Clear();
        var routeStyles = new Dictionary<string, (RouteMarkerStyle Style, string Name)>(StringComparer.Ordinal);
        var nextStyleIndex = 0;
        var options = BuildOptions();
        for (var index = 0; index < _targets.Count; index++)
        {
            var target = _targets[index];
            var routeKey = BuildRouteVisualKey(target);
            if (!routeStyles.TryGetValue(routeKey, out var routeStyle))
            {
                routeStyle = (
                    RouteMarkerPlanner.GetStyle(nextStyleIndex),
                    FormatRouteName(nextStyleIndex));
                routeStyles.Add(routeKey, routeStyle);
                nextStyleIndex++;
            }

            var rowIndex = index;
            var row = new PreCombatTargetRow(
                target,
                routeStyle.Style,
                routeStyle.Name,
                () => HandleRowAction(rowIndex),
                () => ShowRouteHighlight(target, routeStyle.Style),
                RouteNodeMarkerOverlay.ClearAll);
            _rows.Add(row);
            _targetList.AddChild(row.Container);
            if (_stateToken is not null
                && PreCombatForecastCoordinator.TryGetCached(
                    _stateToken,
                    target,
                    options with
                    {
                        PlayerCurrentHpOverride = target.PlayerCurrentHpOverride,
                        InterveningMapPoints = target.InterveningMapPoints
                    },
                    out var cached))
            {
                row.SetResult(cached, fromCache: true);
            }
        }
        ApplyRunningState();
    }

    private void UpdateReadySummary()
    {
        if (!Entry.CombatSolver.SupportsPreCombatForecast)
        {
            SetSummary("Combat Solver 战前 API 不可用。", StsColors.red);
            return;
        }
        SetSummary(
            _targets.Count == 0
                ? "当前路线中没有能在不经过其他战斗或未决事件的情况下确定的下一场战斗。"
                : _rows.Count(row => row.HasResult) is var cached && cached > 0
                    ? $"已找到 {_targets.Count} 个场景，其中 {cached} 个已从相同跑局状态与搜索设置恢复。请按需点击每项右侧按钮。"
                    : $"已找到 {_targets.Count} 个可计算场景；请按需点击每项右侧按钮，未选择的路线不会消耗性能。",
            _targets.Count == 0 ? StsColors.gold : StsColors.cream);
    }

    private void RefreshCachedRows()
    {
        if (IsRunning || _screen is null || !GodotObject.IsInstanceValid(_screen))
            return;
        _stateToken = TryCaptureStateToken(_screen._runState);
        RebuildRows();
        UpdateReadySummary();
    }

    private string? TryCaptureStateToken(RunState run)
    {
        if (!Entry.CombatSolver.SupportsPreCombatForecast)
            return null;
        try
        {
            return Entry.CombatSolver.CaptureLiveStateToken(run);
        }
        catch (Exception exception)
        {
            SetSummary(
                $"无法捕获当前跑局快照：{ShortError(exception.GetBaseException().Message)}",
                StsColors.red);
            return null;
        }
    }

    private static string BuildRouteVisualKey(PreCombatForecastTarget target) => string.Join(
        '|',
        target.Point.coord,
        target.RoomType,
        target.Encounter.Id,
        string.Join(';', (target.InterveningMapPoints ?? []).Select(static step =>
            $"{step.MapColumn},{step.ActFloor},{step.MapPointType},{step.RoomType}")),
        string.Join(';', target.RouteSummaries ?? []));

    private static string FormatRouteName(int index) => index < 26
        ? $"路线 {(char)('A' + index)}"
        : $"路线 {index + 1}";

    private void ShowRouteHighlight(PreCombatForecastTarget target, RouteMarkerStyle style)
    {
        if (_screen is null
            || !GodotObject.IsInstanceValid(_screen)
            || !_screen._mapPointDictionary.TryGetValue(target.Point.coord, out var owner))
        {
            return;
        }

        var route = new List<MapPoint>();
        foreach (var step in target.InterveningMapPoints ?? [])
        {
            var coordinate = new MapCoord(step.MapColumn, step.ActFloor - 1);
            if (_screen._mapPointDictionary.TryGetValue(coordinate, out var pointNode))
                route.Add(pointNode.Point);
        }
        if (route.Count == 0 || route[^1].coord != target.Point.coord)
            route.Add(target.Point);
        RouteNodeMarkerOverlay.Show(
            owner,
            [new RouteMarkerAssignment(target.Point, style)],
            [new RouteLineAssignment(style, [route])]);
    }

    private static void ReleaseWorkerSafely()
    {
        _ = ReleaseAsync();
        return;

        static async Task ReleaseAsync()
        {
            try
            {
                await Entry.CombatSolver.StopPreCombatWorkerAsync();
            }
            catch (Exception exception)
            {
                Entry.Logger.Error($"Stopping the reusable pre-combat worker failed: {exception.GetBaseException()}");
            }
        }
    }

    private void RefreshHoveredTarget(PreCombatForecastTarget target)
    {
        if (_screen is null
            || !_screen._mapPointDictionary.TryGetValue(target.Point.coord, out var owner)
            || !NHoverTipSet._activeHoverTips.ContainsKey(owner))
        {
            return;
        }
        NHoverTipSet.Remove(owner);
        owner.Call(NMapPoint.MethodName.OnFocus);
    }

    private void SetSummary(string text, Color color)
    {
        _summary.Text = text;
        _summary.AddThemeColorOverride(ThemeConstants.Label.FontColor, color);
    }

    private static OptionButton CreateOptionButton(float width)
    {
        var option = new OptionButton
        {
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(width, 38f)
        };
        option.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        option.AddThemeFontSizeOverride("font_size", 15);
        option.AddThemeColorOverride("font_color", StsColors.cream);
        return option;
    }

    private static string ShortError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "未知错误";
        var singleLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 180 ? singleLine : singleLine[..177] + "...";
    }

    private sealed class PreCombatTargetRow
    {
        private readonly PreCombatForecastTarget _target;
        private readonly Label _state;
        private readonly Label _details;
        private readonly Button _calculateButton;

        public PreCombatTargetRow(
            PreCombatForecastTarget target,
            RouteMarkerStyle style,
            string routeName,
            Action onAction,
            Action onHover,
            Action onUnhover)
        {
            _target = target;
            RouteName = routeName;
            Container = new PanelContainer
            {
                MouseFilter = MouseFilterEnum.Stop,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0f, 122f)
            };
            Container.AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
                new Color(0.06f, 0.12f, 0.14f, 0.88f),
                style.Color with { A = 0.82f },
                6));
            Container.MouseEntered += onHover;
            Container.MouseExited += onUnhover;

            var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Pass };
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 12);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 12);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 7);
            margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 7);
            Container.AddChild(margin);

            var row = new HBoxContainer
            {
                MouseFilter = MouseFilterEnum.Pass,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            row.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 14);
            margin.AddChild(row);

            var identity = new VBoxContainer
            {
                MouseFilter = MouseFilterEnum.Pass,
                CustomMinimumSize = new Vector2(300f, 0f)
            };
            row.AddChild(identity);
            var room = target.RoomType switch
            {
                RoomType.Elite => "精英战",
                RoomType.Boss => "Boss",
                _ => "普通战",
            };
            var routeHeader = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
            routeHeader.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 7);
            identity.AddChild(routeHeader);
            routeHeader.AddChild(new RouteMarkerBadge(style));
            var routeTitles = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
            routeHeader.AddChild(routeTitles);
            routeTitles.AddChild(PreCombatPanelStyles.CreateLabel(routeName, 13, style.Color, bold: true));
            routeTitles.AddChild(PreCombatPanelStyles.CreateLabel(
                $"第 {target.ActFloor} 层 · {room} · {target.Encounter.Title}",
                17,
                target.RoomType == RoomType.Elite ? StsColors.gold : StsColors.cream,
                bold: true));
            var pointKind = target.Point.PointType == MapPointType.Unknown ? "（问号已确定）" : string.Empty;
            identity.AddChild(PreCombatPanelStyles.CreateLabel(
                $"坐标 {target.Point.coord} {pointKind} · {target.StateScenario}",
                13,
                new Color(0.63f, 0.75f, 0.79f)));

            var routeSummaries = target.RouteSummaries ?? [];
            var interveningRooms = target.InterveningRoomSummaries ?? [];
            var detailLines = new List<string>
            {
                "怪物：" + string.Join(" + ", target.Encounter.Monsters),
                "路径：" + (routeSummaries.Count == 0
                    ? "下一步"
                    : string.Join("；", routeSummaries.Take(3)))
            };
            if (routeSummaries.Count > 3)
                detailLines[^1] += $"；另 {routeSummaries.Count - 3} 条";
            if (interveningRooms.Count > 0)
                detailLines.Add("前置：" + string.Join("；", interveningRooms.Take(2)));
            _details = PreCombatPanelStyles.CreateLabel(
                string.Join('\n', detailLines),
                15,
                StsColors.cream);
            _details.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _details.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(_details);

            _state = PreCombatPanelStyles.CreateLabel("尚未计算", 15, StsColors.cream, bold: true);
            _state.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _state.CustomMinimumSize = new Vector2(330f, 0f);
            _state.HorizontalAlignment = HorizontalAlignment.Left;
            row.AddChild(_state);

            _calculateButton = PreCombatPanelStyles.CreateButton("计算", 104f);
            _calculateButton.TooltipText = "只计算这一项；结果会按完整跑局状态与当前搜索设置缓存";
            _calculateButton.Pressed += onAction;
            row.AddChild(_calculateButton);
        }

        public PanelContainer Container { get; }

        public string RouteName { get; }

        public bool HasRouteBadge => true;

        public bool HasResult { get; private set; }

        public void SetStatus(ManualPreCombatItemStatus status)
        {
            (_state.Text, var color) = status switch
            {
                ManualPreCombatItemStatus.Ready => ("尚未计算", StsColors.cream),
                ManualPreCombatItemStatus.Running => ("计算中 · 后台求解器处理中", StsColors.gold),
                ManualPreCombatItemStatus.Cancelled => ("已取消；可再次计算", new Color(0.63f, 0.75f, 0.79f)),
                ManualPreCombatItemStatus.Succeeded => (_state.Text, new Color(0.48f, 0.92f, 0.47f)),
                _ => (_state.Text, StsColors.red),
            };
            _state.AddThemeColorOverride(ThemeConstants.Label.FontColor, color);
            if (status == ManualPreCombatItemStatus.Running)
                _calculateButton.Text = "停止";
            else if (status is ManualPreCombatItemStatus.Ready or ManualPreCombatItemStatus.Cancelled)
                _calculateButton.Text = "计算";
        }

        public void SetStopping()
        {
            _state.Text = "正在停止后台计算……";
            _state.AddThemeColorOverride(ThemeConstants.Label.FontColor, StsColors.gold);
            _calculateButton.Disabled = true;
        }

        public void SetFailure(string error)
        {
            HasResult = false;
            _state.Text = $"无法计算：{ShortError(error)}";
            SetStatus(ManualPreCombatItemStatus.Failed);
            _calculateButton.Text = "重试";
        }

        public void SetInteractionEnabled(bool enabled) => _calculateButton.Disabled = !enabled;

        public void SetResult(CombatSolverForecastResult result, bool fromCache)
        {
            if (!result.IsSuccess)
            {
                HasResult = false;
                _state.Text = result.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "已取消"
                    : $"无法计算：{ShortError(result.Error ?? result.Status)}";
                SetStatus(result.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                    ? ManualPreCombatItemStatus.Cancelled
                    : ManualPreCombatItemStatus.Failed);
                _calculateButton.Text = result.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                    ? "计算"
                    : "重试";
                return;
            }

            HasResult = true;
            var potionText = result.PotionUses.Count == 0
                ? "不使用药水"
                : string.Join("；", result.PotionUses.Select(use => $"第 {use.Turn} 回合 {use.Title}"));
            var confidence = result.Confidence switch
            {
                "Complete" => "已找到结束战斗路线",
                "DeathOnly" => "仅找到死亡路线",
                _ => "预算内最佳路线",
            };
            var elapsed = result.TotalElapsedMilliseconds is { } milliseconds
                ? $" · {milliseconds / 1000d:0.0} 秒"
                : string.Empty;
            var assumption = _target.UsesCurrentStateAssumption
                ? _target.PlayerCurrentHpOverride is null
                    ? "\n条件结果：沿途房间造成的 HP、牌、遗物与药水变化尚未应用"
                    : "\n条件结果：已应用休息后 HP；其他沿途奖励、锻造及触发尚未应用"
                : string.Empty;
            var cacheText = fromCache ? "缓存结果 · " : string.Empty;
            _state.Text = $"{cacheText}预计战损 {result.ProjectedHpLoss?.ToString() ?? "?"} HP · {potionText}\n{confidence}{elapsed}{assumption}";
            SetStatus(ManualPreCombatItemStatus.Succeeded);
            _calculateButton.Text = "重新计算";
        }
    }
}

internal sealed partial class PreCombatForecastToggleButton : Button
{
    internal const string NodeName = "SeedOraclePreCombatForecastToggle";
    internal const int ToggleZIndex = 175;

    private PreCombatForecastPanelControl? _panel;

    internal bool IsExpanded { get; private set; }

    public PreCombatForecastToggleButton()
    {
        Name = NodeName;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.None;
        ZIndex = ToggleZIndex;
        AnchorLeft = 0.68f;
        AnchorRight = 0.68f;
        AnchorTop = 0f;
        AnchorBottom = 0f;
        OffsetLeft = -105f;
        OffsetRight = 105f;
        OffsetTop = 38f;
        OffsetBottom = 80f;
        CustomMinimumSize = new Vector2(210f, 42f);
        TooltipText = "打开确定路线战损与纯模拟工具；所有计算都由你手动启动";
        this.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        AddThemeFontSizeOverride("font_size", 17);
        AddThemeColorOverride("font_color", StsColors.cream);
        AddThemeColorOverride("font_hover_color", StsColors.gold);
        AddThemeColorOverride("font_pressed_color", StsColors.gold);
        AddThemeStyleboxOverride("normal", PreCombatPanelStyles.CreateButtonStyle(
            new Color(0.025f, 0.07f, 0.085f, 0.94f),
            new Color(0.24f, 0.43f, 0.49f, 0.96f)));
        AddThemeStyleboxOverride("hover", PreCombatPanelStyles.CreateButtonStyle(
            new Color(0.055f, 0.13f, 0.15f, 0.98f),
            StsColors.gold));
        AddThemeStyleboxOverride("pressed", PreCombatPanelStyles.CreateButtonStyle(
            new Color(0.015f, 0.05f, 0.065f, 0.98f),
            StsColors.gold));
        Pressed += Toggle;
        ApplyState();
    }

    internal void Bind(PreCombatForecastPanelControl panel, bool expanded)
    {
        if (!ReferenceEquals(_panel, panel))
        {
            if (_panel is not null)
                _panel.PresentationChanged -= ApplyState;
            _panel = panel;
            _panel.PresentationChanged += ApplyState;
        }
        if (!_panel.IsRunning)
            _panel.Visible = expanded;
        Disabled = !Entry.CombatSolver.SupportsPreCombatForecast;
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
        if (_panel.IsRunning)
        {
            _panel.Visible = true;
            ApplyState();
            return;
        }

        if (_panel.Visible)
        {
            _panel.Collapse();
            PreCombatForecastPanel.RememberExpanded(false);
        }
        else
        {
            RunSeedOverviewPanel.CollapseSafely();
            RoutePlanPanel.CollapseSafely();
            PreCombatForecastPanel.RememberExpanded(true);
            _panel.ShowWithoutCalculating();
        }
        ApplyState();
    }

    private void ApplyState()
    {
        IsExpanded = _panel?.Visible == true;
        Text = _panel?.IsRunning == true
            ? "威胁评估中…"
            : IsExpanded
                ? "战损与模拟  ▲"
                : "战损与模拟  ▼";
    }
}

internal static class PreCombatForecastPanel
{
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);
    private static PreCombatForecastPanelControl? _currentPanel;
    private static PreCombatForecastToggleButton? _currentToggle;
    private static bool _isExpanded;

    public static PreCombatForecastPanelControl Refresh(NMapScreen screen, bool mapChanged = false)
    {
        var panel = screen.GetNodeOrNull<PreCombatForecastPanelControl>(
            PreCombatForecastPanelControl.NodeName);
        if (panel is null || !GodotObject.IsInstanceValid(panel) || panel.IsQueuedForDeletion())
        {
            panel = new PreCombatForecastPanelControl();
            screen.AddChild(panel);
        }

        var topBar = NRun.Instance?.GlobalUi.TopBar
                     ?? throw new InvalidOperationException(
                         "Seed Oracle could not locate the active run top bar.");
        var toggle = topBar.GetNodeOrNull<PreCombatForecastToggleButton>(
            PreCombatForecastToggleButton.NodeName);
        if (toggle is null || !GodotObject.IsInstanceValid(toggle) || toggle.IsQueuedForDeletion())
        {
            toggle = new PreCombatForecastToggleButton();
            topBar.AddChild(toggle);
        }

        panel.Configure(screen, mapChanged);
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
        _currentPanel.Collapse();
        _isExpanded = false;
    }

    public static void HideToggleSafely()
    {
        try
        {
            _currentPanel?.CancelAndHide();
            _isExpanded = false;
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

    public static void RefreshSafely(NMapScreen screen, bool mapChanged = false)
    {
        try
        {
            Refresh(screen, mapChanged);
        }
        catch (Exception exception)
        {
            ReportFailure("refresh", exception);
        }
    }

    private static void ReportFailure(string operation, Exception exception)
    {
        var root = exception.GetBaseException();
        var key = $"{operation}:{root.GetType().FullName}:{root.Message}";
        if (ReportedFailures.Add(key))
            Entry.Logger.Error($"Manual pre-combat panel {operation} failed: {root}");
    }
}

internal static class PreCombatPanelStyles
{
    public static Label CreateLabel(string text, int size, Color color, bool bold = false)
    {
        var label = new Label
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None
        };
        label.ApplyLocaleFontSubstitution(
            bold ? FontType.Bold : FontType.Regular,
            ThemeConstants.Label.Font);
        label.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, size);
        label.AddThemeColorOverride(ThemeConstants.Label.FontColor, color);
        return label;
    }

    public static Button CreateButton(string text, float width)
    {
        var button = new Button
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(width, 38f)
        };
        button.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        button.AddThemeFontSizeOverride("font_size", 15);
        button.AddThemeColorOverride("font_color", StsColors.cream);
        button.AddThemeColorOverride("font_hover_color", StsColors.gold);
        button.AddThemeColorOverride("font_pressed_color", StsColors.gold);
        button.AddThemeStyleboxOverride("normal", CreateButtonStyle(
            new Color(0.06f, 0.13f, 0.15f, 0.95f),
            new Color(0.24f, 0.43f, 0.49f, 0.95f)));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle(
            new Color(0.09f, 0.18f, 0.20f, 0.98f),
            StsColors.gold));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle(
            new Color(0.03f, 0.08f, 0.10f, 0.98f),
            StsColors.gold));
        return button;
    }

    public static StyleBoxFlat CreatePanel(Color background, Color border, int radius) => new()
    {
        BgColor = background,
        BorderColor = border,
        BorderWidthLeft = 2,
        BorderWidthTop = 2,
        BorderWidthRight = 2,
        BorderWidthBottom = 2,
        CornerRadiusTopLeft = radius,
        CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius,
        CornerRadiusBottomRight = radius
    };

    public static StyleBoxFlat CreateButtonStyle(Color background, Color border)
    {
        var style = CreatePanel(background, border, 7);
        style.ContentMarginLeft = 9f;
        style.ContentMarginRight = 9f;
        style.ContentMarginTop = 4f;
        style.ContentMarginBottom = 4f;
        return style;
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
internal static class NMapScreenSetMapPreCombatPanelPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) =>
        PreCombatForecastPanel.RefreshSafely(__instance, mapChanged: true);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
internal static class NMapScreenOpenPreCombatPanelPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance) =>
        PreCombatForecastPanel.RefreshSafely(__instance);
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Close))]
internal static class NMapScreenClosePreCombatPanelPatch
{
    [HarmonyPostfix]
    private static void Postfix() => PreCombatForecastPanel.HideToggleSafely();
}
