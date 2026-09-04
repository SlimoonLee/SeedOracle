using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using SeedOracle.Integration;

namespace SeedOracle.UI;

internal sealed partial class PreCombatForecastPanelControl
{
    private enum SimulationTargetKind
    {
        RandomWeak = 1,
        RandomRegular = 2,
        RandomElite = 3,
        SpecificNormal = 4,
        SpecificElite = 5,
        CurrentBoss = 6,
    }

    private enum WorkerRetentionPolicy
    {
        TwoMinutes = 0,
        TenMinutes = 1,
        ThirtyMinutes = 2,
        Indefinite = 3,
        AutoClose = 4,
    }

    private sealed record SimulationEncounterChoice(EncounterModel Encounter, string Label);

    private sealed record SimulationSampleOutcome(
        int Number,
        string PoolLabel,
        EncounterModel Encounter,
        ulong SampleSeed,
        CombatSolverForecastResult Result);

    private static WorkerRetentionPolicy _workerRetentionPreference = WorkerRetentionPolicy.TwoMinutes;

    private Label _workerStatusLabel = null!;
    private OptionButton _workerRetentionSelect = null!;
    private Button _closeWorkerButton = null!;
    private Button _restartWorkerButton = null!;
    private Label _simulationDisclaimer = null!;
    private Label _simulationSummary = null!;
    private OptionButton _simulationTargetSelect = null!;
    private OptionButton _simulationEncounterSelect = null!;
    private OptionButton _simulationCountSelect = null!;
    private Button _simulationStartButton = null!;
    private VBoxContainer _simulationResultList = null!;
    private readonly List<SimulationEncounterChoice> _simulationEncounterChoices = [];
    private string? _simulationResultStateToken;
    private double _workerStatusRefreshElapsed;
    private bool _workerLifecycleOperation;

    private WorkerRetentionPolicy SelectedWorkerRetentionPolicy =>
        Enum.IsDefined(typeof(WorkerRetentionPolicy), (int)_workerRetentionSelect.GetSelectedId())
            ? (WorkerRetentionPolicy)_workerRetentionSelect.GetSelectedId()
            : WorkerRetentionPolicy.TwoMinutes;

    private bool KeepWorkerAlive => SelectedWorkerRetentionPolicy != WorkerRetentionPolicy.AutoClose;

    private int? SelectedWorkerIdleTimeoutMilliseconds => SelectedWorkerRetentionPolicy switch
    {
        WorkerRetentionPolicy.TwoMinutes => 120_000,
        WorkerRetentionPolicy.TenMinutes => 600_000,
        WorkerRetentionPolicy.ThirtyMinutes => 1_800_000,
        WorkerRetentionPolicy.Indefinite => null,
        WorkerRetentionPolicy.AutoClose => 120_000,
        _ => 120_000,
    };

    private bool IsSimulationRunning { get; set; }

    internal int SimulationSampleOptionCount => _simulationCountSelect.ItemCount;

    internal int SimulationTargetOptionCount => _simulationTargetSelect.ItemCount;

    internal int WorkerRetentionOptionCount => _workerRetentionSelect.ItemCount;

    internal bool KeepsWorkerByDefault => KeepWorkerAlive;

    internal bool HasExpectedWorkerRetentionOptions =>
        _workerRetentionSelect.ItemCount == 5
        && _workerRetentionSelect.GetItemText(0) == "保活 2 分钟"
        && _workerRetentionSelect.GetItemText(1) == "保活 10 分钟"
        && _workerRetentionSelect.GetItemText(2) == "保活 30 分钟"
        && _workerRetentionSelect.GetItemText(3) == "一直维持"
        && _workerRetentionSelect.GetItemText(4) == "自动关闭";

    internal bool HasLocalizedSimulationEncounterLabels =>
        _screen is not null
        && GodotObject.IsInstanceValid(_screen)
        && _screen._runState.Act.AllEncounters.Any()
        && _screen._runState.Act.AllEncounters.All(static encounter =>
            IsResolvedEncounterTitle(LocalizedEncounterTitle(encounter)));

    internal bool HasWorkerLifecycleControls =>
        _closeWorkerButton.Text == "关闭后台"
        && _restartWorkerButton.Text == "重启 / 预热"
        && _workerStatusLabel.Text.StartsWith("后台", StringComparison.Ordinal);

    internal bool HasSimulationDisclaimer =>
        _simulationDisclaimer.Text.Contains("怪物生命", StringComparison.Ordinal)
        && _simulationDisclaimer.Text.Contains("开局洗牌", StringComparison.Ordinal)
        && _simulationDisclaimer.Text.Contains("仅供参考", StringComparison.Ordinal);

    public override void _Process(double delta)
    {
        if (!Visible)
            return;
        _workerStatusRefreshElapsed += delta;
        if (_workerStatusRefreshElapsed >= 0.75d)
            RefreshWorkerStatus(force: true);
    }

    private void BuildWorkerToolbar(VBoxContainer parent)
    {
        var toolbar = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        toolbar.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 9);
        parent.AddChild(toolbar);

        _workerStatusLabel = PreCombatPanelStyles.CreateLabel(
            "后台：未启动",
            14,
            new Color(0.63f, 0.75f, 0.79f));
        _workerStatusLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _workerStatusLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _workerStatusLabel.TooltipText = "工作集是当前驻留在物理内存中的部分；私有内存是该 worker 独占的提交内存。隔离音频始终静音。";
        toolbar.AddChild(_workerStatusLabel);

        toolbar.AddChild(PreCombatPanelStyles.CreateLabel("任务结束", 14, StsColors.cream));
        _workerRetentionSelect = CreateOptionButton(178f);
        _workerRetentionSelect.AddItem("保活 2 分钟", (int)WorkerRetentionPolicy.TwoMinutes);
        _workerRetentionSelect.AddItem("保活 10 分钟", (int)WorkerRetentionPolicy.TenMinutes);
        _workerRetentionSelect.AddItem("保活 30 分钟", (int)WorkerRetentionPolicy.ThirtyMinutes);
        _workerRetentionSelect.AddItem("一直维持", (int)WorkerRetentionPolicy.Indefinite);
        _workerRetentionSelect.AddItem("自动关闭", (int)WorkerRetentionPolicy.AutoClose);
        _workerRetentionSelect.Selected = (int)_workerRetentionPreference;
        _workerRetentionSelect.TooltipText = "保活可省去下一次约十余秒的游戏进程冷启动；可在空闲 2、10、30 分钟后关闭、一直维持，或在单项计算/整个模拟批次结束后自动释放内存。";
        _workerRetentionSelect.ItemSelected += _ => OnWorkerRetentionChanged();
        toolbar.AddChild(_workerRetentionSelect);

        _closeWorkerButton = PreCombatPanelStyles.CreateButton("关闭后台", 108f);
        _closeWorkerButton.TooltipText = "立即关闭空闲的隔离 Combat Solver worker";
        _closeWorkerButton.Pressed += () => _ = CloseWorkerAsync(manual: true);
        toolbar.AddChild(_closeWorkerButton);

        _restartWorkerButton = PreCombatPanelStyles.CreateButton("重启 / 预热", 126f);
        _restartWorkerButton.TooltipText = "关闭旧 worker 并立即启动新进程；你选择目标时它可在后台完成游戏初始化";
        _restartWorkerButton.Pressed += () => _ = RestartWorkerAsync();
        toolbar.AddChild(_restartWorkerButton);
    }

    private void BuildSimulationTab(TabContainer tabs)
    {
        var page = new VBoxContainer
        {
            Name = "咱俩碰一碰",
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        page.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 9);
        tabs.AddChild(page);

        _simulationDisclaimer = PreCombatPanelStyles.CreateLabel(
            "纯模拟：远处战斗的随机数尚未确定。每个样本会假定怪物组与怪物生命、开局洗牌、怪物行动和其他战斗随机数；使用当前牌组、遗物、药水与生命，只用于估计威胁，仅供参考。",
            14,
            StsColors.gold,
            bold: true);
        _simulationDisclaimer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        page.AddChild(_simulationDisclaimer);

        var controls = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        controls.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 9);
        page.AddChild(controls);

        controls.AddChild(PreCombatPanelStyles.CreateLabel("对象", 15, StsColors.cream));
        _simulationTargetSelect = CreateOptionButton(188f);
        _simulationTargetSelect.AddItem("随机弱怪池", (int)SimulationTargetKind.RandomWeak);
        _simulationTargetSelect.AddItem("随机强怪池", (int)SimulationTargetKind.RandomRegular);
        _simulationTargetSelect.AddItem("随机本幕精英", (int)SimulationTargetKind.RandomElite);
        _simulationTargetSelect.AddItem("指定普通怪组", (int)SimulationTargetKind.SpecificNormal);
        _simulationTargetSelect.AddItem("指定精英怪组", (int)SimulationTargetKind.SpecificElite);
        _simulationTargetSelect.AddItem("本幕 Boss", (int)SimulationTargetKind.CurrentBoss);
        _simulationTargetSelect.ItemSelected += _ => RefreshSimulationEncounterChoices();
        controls.AddChild(_simulationTargetSelect);

        _simulationEncounterSelect = CreateOptionButton(310f);
        _simulationEncounterSelect.TooltipText = "指定怪组时选择当前幕原生遭遇；随机池会为每个样本独立抽取。";
        controls.AddChild(_simulationEncounterSelect);

        controls.AddChild(PreCombatPanelStyles.CreateLabel("次数", 15, StsColors.cream));
        _simulationCountSelect = CreateOptionButton(118f);
        for (var count = 0; count <= 10; count++)
            _simulationCountSelect.AddItem(count == 0 ? "0 次" : $"{count} 次", count);
        _simulationCountSelect.Selected = 0;
        _simulationCountSelect.TooltipText = "一次手动任务最多运行 10 个独立假设样本；0 次用于清空结果，不会启动 worker。";
        controls.AddChild(_simulationCountSelect);

        _simulationStartButton = PreCombatPanelStyles.CreateButton("开始模拟", 108f);
        _simulationStartButton.Pressed += StartSimulation;
        controls.AddChild(_simulationStartButton);

        _simulationSummary = PreCombatPanelStyles.CreateLabel(
            "选择 0～10 次后手动开始；模拟不会读取或推进真实战斗随机数。",
            15,
            StsColors.cream);
        _simulationSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        page.AddChild(_simulationSummary);

        var separator = new HSeparator { MouseFilter = MouseFilterEnum.Ignore };
        separator.AddThemeConstantOverride("separation", 2);
        page.AddChild(separator);

        var scroll = new ScrollContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        page.AddChild(scroll);

        _simulationResultList = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkBegin
        };
        _simulationResultList.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 7);
        scroll.AddChild(_simulationResultList);
    }

    private void ConfigureSimulation(RunState run)
    {
        RefreshSimulationEncounterChoices();
        if (_simulationResultStateToken is not null
            && _stateToken is not null
            && !_simulationResultStateToken.Equals(_stateToken, StringComparison.Ordinal))
        {
            ClearSimulationResults();
            _simulationResultStateToken = null;
            SetSimulationSummary("跑局状态已经变化，旧模拟结果已清除。请选择样本后重新运行。", StsColors.gold);
        }
    }

    private void RefreshSimulationEncounterChoices()
    {
        if (_screen is null || !GodotObject.IsInstanceValid(_screen))
            return;

        string? previousId = _simulationEncounterChoices.Count > 0
                             && _simulationEncounterSelect.Selected >= 0
                             && _simulationEncounterSelect.Selected < _simulationEncounterChoices.Count
            ? _simulationEncounterChoices[_simulationEncounterSelect.Selected].Encounter.Id.Entry
            : null;
        _simulationEncounterSelect.Clear();
        _simulationEncounterChoices.Clear();

        RunState run = _screen._runState;
        SimulationTargetKind kind = SelectedSimulationTargetKind;
        IEnumerable<SimulationEncounterChoice> choices = kind switch
        {
            SimulationTargetKind.SpecificNormal => run.Act.AllWeakEncounters
                .Select(encounter => new SimulationEncounterChoice(
                    encounter,
                    $"[弱] {LocalizedEncounterTitle(encounter)}"))
                .Concat(run.Act.AllRegularEncounters.Select(encounter =>
                    new SimulationEncounterChoice(encounter, $"[强] {LocalizedEncounterTitle(encounter)}"))),
            SimulationTargetKind.SpecificElite => run.Act.AllEliteEncounters
                .Select(encounter => new SimulationEncounterChoice(encounter, LocalizedEncounterTitle(encounter))),
            SimulationTargetKind.CurrentBoss =>
            [new SimulationEncounterChoice(run.Act.BossEncounter, LocalizedEncounterTitle(run.Act.BossEncounter))],
            _ => [],
        };
        _simulationEncounterChoices.AddRange(choices
            .OrderBy(static choice => choice.Label, StringComparer.CurrentCulture));

        if (_simulationEncounterChoices.Count == 0)
        {
            _simulationEncounterSelect.AddItem(kind switch
            {
                SimulationTargetKind.RandomWeak => "每个样本从弱怪池抽取",
                SimulationTargetKind.RandomRegular => "每个样本从强怪池抽取",
                SimulationTargetKind.RandomElite => "每个样本从精英池抽取",
                _ => "当前幕没有可用遭遇",
            });
            _simulationEncounterSelect.Disabled = true;
            return;
        }

        var selected = 0;
        for (var index = 0; index < _simulationEncounterChoices.Count; index++)
        {
            SimulationEncounterChoice choice = _simulationEncounterChoices[index];
            _simulationEncounterSelect.AddItem(choice.Label, index);
            if (choice.Encounter.Id.Entry.Equals(previousId, StringComparison.Ordinal))
                selected = index;
        }
        _simulationEncounterSelect.Selected = selected;
        _simulationEncounterSelect.Disabled = kind == SimulationTargetKind.CurrentBoss || IsRunning;
    }

    private SimulationTargetKind SelectedSimulationTargetKind =>
        Enum.IsDefined(typeof(SimulationTargetKind), (int)_simulationTargetSelect.GetSelectedId())
            ? (SimulationTargetKind)_simulationTargetSelect.GetSelectedId()
            : SimulationTargetKind.RandomWeak;

    private void StartSimulation()
    {
        if (IsRunning
            || _screen is null
            || !GodotObject.IsInstanceValid(_screen)
            || !Entry.CombatSolver.SupportsPreCombatForecast)
        {
            return;
        }

        int sampleCount = (int)_simulationCountSelect.GetSelectedId();
        if (sampleCount == 0)
        {
            ClearSimulationResults();
            _simulationResultStateToken = null;
            SetSimulationSummary("已清空模拟结果；0 次不会启动后台 worker。", StsColors.cream);
            return;
        }

        RunState run = _screen._runState;
        string stateToken;
        try
        {
            stateToken = Entry.CombatSolver.CaptureLiveStateToken(run);
        }
        catch (Exception exception)
        {
            SetSimulationSummary(
                $"无法捕获当前跑局快照：{ShortError(exception.GetBaseException().Message)}",
                StsColors.red);
            return;
        }

        ulong batchSeed = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
        IReadOnlyList<(EncounterModel Encounter, string PoolLabel, ulong Seed)> samples;
        try
        {
            samples = BuildSimulationSamples(run, sampleCount, batchSeed);
        }
        catch (InvalidOperationException exception)
        {
            SetSimulationSummary(exception.Message, StsColors.red);
            return;
        }

        ClearSimulationResults();
        _simulationResultStateToken = stateToken;
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        int generation = ++_batchGeneration;
        _activeTargetIndex = -1;
        IsSimulationRunning = true;
        IsRunning = true;
        ApplyRunningState();
        SetSimulationSummary(
            $"正在运行 0/{sampleCount}：样本会串行执行并在批次内复用同一个静音 worker。",
            StsColors.gold);
        PresentationChanged?.Invoke();
        _ = RunSimulationAsync(run, stateToken, samples, generation, _cancellation.Token);
    }

    private IReadOnlyList<(EncounterModel Encounter, string PoolLabel, ulong Seed)> BuildSimulationSamples(
        RunState run,
        int count,
        ulong batchSeed)
    {
        SimulationTargetKind kind = SelectedSimulationTargetKind;
        EncounterModel[] pool = kind switch
        {
            SimulationTargetKind.RandomWeak => run.Act.AllWeakEncounters.ToArray(),
            SimulationTargetKind.RandomRegular => run.Act.AllRegularEncounters.ToArray(),
            SimulationTargetKind.RandomElite => run.Act.AllEliteEncounters.ToArray(),
            SimulationTargetKind.SpecificNormal or SimulationTargetKind.SpecificElite
                => ResolveSelectedSimulationEncounter(),
            SimulationTargetKind.CurrentBoss => [run.Act.BossEncounter],
            _ => [],
        };
        if (pool.Length == 0)
            throw new InvalidOperationException("当前幕没有符合该选项的遭遇。");

        string poolLabel = kind switch
        {
            SimulationTargetKind.RandomWeak => "随机弱怪池",
            SimulationTargetKind.RandomRegular => "随机强怪池",
            SimulationTargetKind.RandomElite => "随机本幕精英",
            SimulationTargetKind.SpecificNormal => "指定普通怪组",
            SimulationTargetKind.SpecificElite => "指定精英怪组",
            SimulationTargetKind.CurrentBoss => "本幕 Boss",
            _ => "模拟",
        };
        var random = new System.Random(unchecked((int)(batchSeed ^ (batchSeed >> 32))));
        var samples = new List<(EncounterModel, string, ulong)>(count);
        ulong seedState = batchSeed;
        for (var index = 0; index < count; index++)
        {
            EncounterModel encounter = pool.Length == 1 ? pool[0] : pool[random.Next(pool.Length)];
            samples.Add((encounter, poolLabel, NextSampleSeed(ref seedState)));
        }
        return samples;
    }

    private EncounterModel[] ResolveSelectedSimulationEncounter()
    {
        int index = _simulationEncounterSelect.Selected;
        return index >= 0 && index < _simulationEncounterChoices.Count
            ? [_simulationEncounterChoices[index].Encounter]
            : [];
    }

    private async Task RunSimulationAsync(
        RunState run,
        string stateToken,
        IReadOnlyList<(EncounterModel Encounter, string PoolLabel, ulong Seed)> samples,
        int generation,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<SimulationSampleOutcome>(samples.Count);
        try
        {
            CombatSolverForecastOptions options = BuildOptions() with
            {
                ForceRefresh = true,
                CloseWorkerAfterRequest = false,
            };
            for (var index = 0; index < samples.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string liveStateToken = Entry.CombatSolver.CaptureLiveStateToken(run);
                if (!liveStateToken.Equals(stateToken, StringComparison.Ordinal))
                {
                    SetSimulationSummary(
                        $"跑局状态已经变化；已停止批次并保留前 {outcomes.Count} 个样本。",
                        StsColors.gold);
                    return;
                }
                var sample = samples[index];
                SetSimulationSummary(
                    $"正在运行 {index + 1}/{samples.Count}：{sample.PoolLabel} · {LocalizedEncounterTitle(sample.Encounter)}",
                    StsColors.gold);
                CombatSolverForecastResult result = await Entry.CombatSolver.SimulateAsync(
                    run,
                    sample.Encounter,
                    sample.Encounter.RoomType,
                    sample.Seed,
                    options,
                    cancellationToken);
                if (generation != _batchGeneration || !GodotObject.IsInstanceValid(this))
                    return;

                var outcome = new SimulationSampleOutcome(
                    index + 1,
                    sample.PoolLabel,
                    sample.Encounter,
                    sample.Seed,
                    result);
                outcomes.Add(outcome);
                AddSimulationResultRow(outcome);
                UpdateSimulationAggregate(outcomes, samples.Count, finished: false);
                if (result.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    SetSimulationSummary($"已停止模拟；保留已完成的 {index} 个样本。", StsColors.gold);
                    return;
                }
                if (result.Status.Equals("LiveStateChanged", StringComparison.OrdinalIgnoreCase))
                {
                    SetSimulationSummary(
                        $"跑局状态已经变化；已停止批次并保留前 {index} 个样本。",
                        StsColors.gold);
                    return;
                }
            }

            if (generation == _batchGeneration && GodotObject.IsInstanceValid(this))
                UpdateSimulationAggregate(outcomes, samples.Count, finished: true);
        }
        catch (OperationCanceledException)
        {
            if (generation == _batchGeneration && GodotObject.IsInstanceValid(this))
                SetSimulationSummary($"已停止模拟；保留已完成的 {outcomes.Count} 个样本。", StsColors.gold);
        }
        catch (Exception exception)
        {
            if (generation == _batchGeneration && GodotObject.IsInstanceValid(this))
            {
                SetSimulationSummary(
                    $"模拟失败：{ShortError(exception.GetBaseException().Message)}",
                    StsColors.red);
            }
            Entry.Logger.Error($"Hypothetical pre-combat simulation failed: {exception.GetBaseException()}");
        }
        finally
        {
            if (!KeepWorkerAlive)
                await CloseWorkerAsync(manual: false);
            if (generation == _batchGeneration && GodotObject.IsInstanceValid(this))
            {
                IsSimulationRunning = false;
                IsRunning = false;
                _cancellation?.Dispose();
                _cancellation = null;
                ApplyRunningState();
                RefreshWorkerStatus(force: true);
                PresentationChanged?.Invoke();
                if (_refreshAfterCurrent && _screen is not null && GodotObject.IsInstanceValid(_screen))
                {
                    _refreshAfterCurrent = false;
                    Configure(_screen, mapChanged: true);
                }
            }
        }
    }

    private void AddSimulationResultRow(SimulationSampleOutcome outcome)
    {
        var panel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        panel.AddThemeStyleboxOverride("panel", PreCombatPanelStyles.CreatePanel(
            new Color(0.035f, 0.095f, 0.11f, 0.94f),
            new Color(0.18f, 0.36f, 0.41f, 0.96f),
            7));
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Pass };
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginLeft, 11);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginRight, 11);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginTop, 7);
        margin.AddThemeConstantOverride(ThemeConstants.MarginContainer.MarginBottom, 7);
        panel.AddChild(margin);

        var row = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 12);
        margin.AddChild(row);

        var identity = PreCombatPanelStyles.CreateLabel(
            $"样本 {outcome.Number} · {outcome.PoolLabel}\n{LocalizedEncounterTitle(outcome.Encounter)}",
            15,
            StsColors.cream,
            bold: true);
        identity.CustomMinimumSize = new Vector2(365f, 48f);
        identity.TooltipText = $"假设样本种子：{outcome.SampleSeed}";
        row.AddChild(identity);

        var result = PreCombatPanelStyles.CreateLabel(
            FormatSimulationResult(outcome.Result),
            15,
            outcome.Result.IsSuccess ? new Color(0.48f, 0.92f, 0.47f) : StsColors.red);
        result.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        result.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(result);
        _simulationResultList.AddChild(panel);
    }

    private static string FormatSimulationResult(CombatSolverForecastResult result)
    {
        if (!result.IsSuccess)
            return $"未完成：{ShortError(result.Error ?? result.Status)}";
        string potionText = result.PotionUses.Count == 0
            ? "不用药"
            : $"用药 {result.PotionUses.Count}";
        string confidence = result.Confidence switch
        {
            "Complete" => "找到结束战斗路线",
            "DeathOnly" => "仅找到死亡路线",
            _ => "预算内最佳路线",
        };
        string turn = result.CombatEndedTurn is { } combatTurn ? $" · T{combatTurn}" : string.Empty;
        return $"预计战损 {result.ProjectedHpLoss?.ToString() ?? "?"} HP · 终局 {result.FinalHp?.ToString() ?? "?"} HP · {potionText}{turn}\n{confidence}";
    }

    private void UpdateSimulationAggregate(
        IReadOnlyList<SimulationSampleOutcome> outcomes,
        int requested,
        bool finished)
    {
        SimulationSampleOutcome[] succeeded = outcomes.Where(static outcome => outcome.Result.IsSuccess).ToArray();
        if (succeeded.Length == 0)
        {
            SimulationSampleOutcome? latest = outcomes.LastOrDefault();
            SetSimulationSummary(
                latest is null
                    ? "尚无完成样本。"
                    : $"0/{requested} 个样本成功：{ShortError(latest.Result.Error ?? latest.Result.Status)}",
                latest is null ? StsColors.cream : StsColors.red);
            return;
        }

        int[] losses = succeeded
            .Where(static outcome => outcome.Result.ProjectedHpLoss.HasValue)
            .Select(static outcome => outcome.Result.ProjectedHpLoss!.Value)
            .ToArray();
        int deathOnly = succeeded.Count(static outcome =>
            outcome.Result.Confidence?.Equals("DeathOnly", StringComparison.OrdinalIgnoreCase) == true);
        string range = losses.Length == 0
            ? "战损未知"
            : $"战损 最低 {losses.Min()} / 平均 {losses.Average():0.0} / 最高 {losses.Max()} HP";
        string prefix = finished ? "模拟完成" : "模拟进行中";
        string deathText = deathOnly > 0 ? $" · 仅找到死亡路线 {deathOnly}" : string.Empty;
        SetSimulationSummary(
            $"{prefix}：成功 {succeeded.Length}/{requested} · {range}{deathText}。这些是假设 RNG 下的求解结果，仅供威胁比较。",
            finished ? StsColors.cream : StsColors.gold);
    }

    private void ClearSimulationResults()
    {
        foreach (Node child in _simulationResultList.GetChildren())
            child.QueueFree();
    }

    private void SetSimulationSummary(string text, Color color)
    {
        _simulationSummary.Text = text;
        _simulationSummary.AddThemeColorOverride(ThemeConstants.Label.FontColor, color);
    }

    private void OnWorkerRetentionChanged()
    {
        _workerRetentionPreference = SelectedWorkerRetentionPolicy;
        if (!KeepWorkerAlive && !IsRunning)
        {
            _ = CloseWorkerAsync(manual: false);
            return;
        }
        if (!IsRunning)
            _ = ConfigureWorkerIdleTimeoutAsync();
    }

    private async Task ConfigureWorkerIdleTimeoutAsync()
    {
        if (_workerLifecycleOperation || !Entry.CombatSolver.SupportsPreCombatForecast)
            return;
        _workerLifecycleOperation = true;
        ApplyRunningState();
        _workerStatusLabel.Text = "后台：正在更新保活时间……";
        try
        {
            await Entry.CombatSolver.SetPreCombatWorkerIdleTimeoutAsync(SelectedWorkerIdleTimeoutMilliseconds);
        }
        catch (Exception exception)
        {
            _workerStatusLabel.Text = $"后台保活设置失败：{ShortError(exception.GetBaseException().Message)}";
            Entry.Logger.Error($"Updating the reusable pre-combat worker idle timeout failed: {exception.GetBaseException()}");
        }
        finally
        {
            _workerLifecycleOperation = false;
            ApplyRunningState();
            RefreshWorkerStatus(force: true);
        }
    }

    private async Task CloseWorkerAsync(bool manual)
    {
        if (_workerLifecycleOperation
            || (manual && IsRunning)
            || !Entry.CombatSolver.SupportsPreCombatForecast)
            return;
        _workerLifecycleOperation = true;
        ApplyRunningState();
        _workerStatusLabel.Text = "后台：正在关闭……";
        try
        {
            await Entry.CombatSolver.StopPreCombatWorkerAsync();
            if (manual)
                _workerStatusLabel.Text = "后台：已手动关闭";
        }
        catch (Exception exception)
        {
            _workerStatusLabel.Text = $"后台关闭失败：{ShortError(exception.GetBaseException().Message)}";
            Entry.Logger.Error($"Stopping the reusable pre-combat worker failed: {exception.GetBaseException()}");
        }
        finally
        {
            _workerLifecycleOperation = false;
            ApplyRunningState();
            RefreshWorkerStatus(force: true);
        }
    }

    private async Task RestartWorkerAsync()
    {
        if (_workerLifecycleOperation
            || IsRunning
            || _screen is null
            || !GodotObject.IsInstanceValid(_screen)
            || !Entry.CombatSolver.SupportsPreCombatForecast)
        {
            return;
        }
        _workerLifecycleOperation = true;
        ApplyRunningState();
        _workerStatusLabel.Text = "后台：正在重启并预热……";
        try
        {
            CombatSolverWorkerStatus status = await Entry.CombatSolver.RestartPreCombatWorkerAsync(
                _screen._runState,
                SelectedWorkerIdleTimeoutMilliseconds);
            RenderWorkerStatus(status);
        }
        catch (Exception exception)
        {
            _workerStatusLabel.Text = $"后台重启失败：{ShortError(exception.GetBaseException().Message)}";
            Entry.Logger.Error($"Restarting the reusable pre-combat worker failed: {exception.GetBaseException()}");
        }
        finally
        {
            _workerLifecycleOperation = false;
            ApplyRunningState();
        }
    }

    private void ApplyExtendedRunningState()
    {
        bool controlsLocked = IsRunning || _workerLifecycleOperation;
        _workerRetentionSelect.Disabled = controlsLocked;
        _closeWorkerButton.Disabled = controlsLocked;
        _restartWorkerButton.Disabled = controlsLocked;
        _simulationTargetSelect.Disabled = controlsLocked;
        _simulationCountSelect.Disabled = controlsLocked;
        _simulationStartButton.Disabled = controlsLocked;
        _simulationEncounterSelect.Disabled = controlsLocked
                                              || _simulationEncounterChoices.Count == 0
                                              || SelectedSimulationTargetKind == SimulationTargetKind.CurrentBoss;
        if (_workerLifecycleOperation)
            _closeButton.Disabled = true;
    }

    private void RefreshWorkerStatus(bool force)
    {
        if (!force && _workerStatusRefreshElapsed < 0.75d)
            return;
        _workerStatusRefreshElapsed = 0d;
        if (_workerLifecycleOperation)
            return;
        if (!Entry.CombatSolver.SupportsPreCombatForecast)
        {
            _workerStatusLabel.Text = "后台：需要本地 Combat Solver API v5";
            _workerStatusLabel.AddThemeColorOverride(ThemeConstants.Label.FontColor, StsColors.red);
            return;
        }
        try
        {
            RenderWorkerStatus(Entry.CombatSolver.GetPreCombatWorkerStatus());
        }
        catch (Exception exception)
        {
            _workerStatusLabel.Text = $"后台状态不可用：{ShortError(exception.GetBaseException().Message)}";
            _workerStatusLabel.AddThemeColorOverride(ThemeConstants.Label.FontColor, StsColors.red);
        }
    }

    private void RenderWorkerStatus(CombatSolverWorkerStatus status)
    {
        if (!status.IsRunning)
        {
            _workerStatusLabel.Text = status.IsBusy ? "后台：正在切换进程……" : "后台：未启动 · 0 MB";
            _workerStatusLabel.AddThemeColorOverride(
                ThemeConstants.Label.FontColor,
                status.IsBusy ? StsColors.gold : new Color(0.63f, 0.75f, 0.79f));
            return;
        }

        string activity = status.IsBusy ? "计算中" : "待命";
        string audio = status.AudioMuted ? "音频静音" : "音频状态未知";
        string retention = status.IdleTimeoutMilliseconds is { } milliseconds
            ? $"保活 {milliseconds / 60_000} 分钟"
            : "一直维持";
        _workerStatusLabel.Text =
            $"后台 PID {status.ProcessId} · {activity} · 工作集 {FormatBytes(status.WorkingSetBytes)} · 私有 {FormatBytes(status.PrivateMemoryBytes)} · {retention} · {audio}";
        _workerStatusLabel.AddThemeColorOverride(
            ThemeConstants.Label.FontColor,
            status.IsBusy ? StsColors.gold : new Color(0.48f, 0.92f, 0.47f));
    }

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
            return "?";
        const double gibibyte = 1024d * 1024d * 1024d;
        return bytes.Value >= gibibyte
            ? $"{bytes.Value / gibibyte:0.00} GB"
             : $"{bytes.Value / (1024d * 1024d):0} MB";
    }

    private static string LocalizedEncounterTitle(EncounterModel encounter)
    {
        string title = encounter.Title.GetFormattedText();
        return string.IsNullOrWhiteSpace(title) ? encounter.Id.Entry : title;
    }

    private static bool IsResolvedEncounterTitle(string title) =>
        !string.IsNullOrWhiteSpace(title)
        && !title.StartsWith("LocString ", StringComparison.Ordinal)
        && !title.EndsWith(".title", StringComparison.OrdinalIgnoreCase);

    private static ulong NextSampleSeed(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
