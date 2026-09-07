using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.UI;

internal sealed partial class RoutePlanPanelControl
{
    private sealed record PlannedCombatSample(
        int Index,
        ulong Seed,
        CombatSolverForecastResult Result);

    private void AddNormalCombatSimulationControls(
        VBoxContainer choiceBox,
        RunState run,
        RoutePlanEntry entry,
        RouteVariantForecast variant,
        RoutePlanForecastService.PlanChain? chain,
        bool chinese,
        Action<CombatSimulationReference> acceptReference)
    {
        if (variant.Encounter is null || chain is null
            || !chain.StatesBefore.TryGetValue(entry.Coord, out var stateBefore))
            return;

        var encounter = run.Act.AllEncounters.FirstOrDefault(candidate =>
            candidate.Id == variant.Encounter.Id);
        if (encounter is null)
        {
            choiceBox.AddChild(PreCombatPanelStyles.CreateLabel(
                chinese ? "无法解析该规划战斗的原生遭遇。" : "The planned combat encounter is unavailable.",
                14, StsColors.red));
            return;
        }

        AddCombatSimulationControls(
            choiceBox,
            run,
            entry,
            stateBefore,
            encounter,
            variant.RoomType,
            RoutePlanTracker.FindMapPoint(run, entry.Coord)?.PointType ?? MapPointType.Unknown,
            chinese,
            acceptReference);
    }

    private void AddCombatSimulationControls(
        VBoxContainer choiceBox,
        RunState liveRun,
        RoutePlanEntry entry,
        PlanningPredictionService.StateSnapshot stateBefore,
        EncounterModel encounter,
        RoomType roomType,
        MapPointType mapPointType,
        bool chinese,
        Action<CombatSimulationReference> acceptReference,
        string? initialMessage = null)
    {
        var section = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        section.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 3);
        choiceBox.AddChild(section);

        var stateToken = PlanningPredictionService.StateToken(stateBefore);
        var title = PreCombatPanelStyles.CreateLabel(
            chinese ? "模拟战斗（规划状态）" : "Combat simulation (planning state)",
            16, StsColors.gold, bold: true);
        section.AddChild(title);

        var status = PreCombatPanelStyles.CreateLabel(
            initialMessage ?? (chinese
                ? "尚未模拟"
                : "No samples yet"),
            14, new Color(0.68f, 0.78f, 0.8f));
        status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        section.AddChild(status);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 5);
        section.AddChild(row);
        var countSelect = new OptionButton { MouseFilter = MouseFilterEnum.Stop, FocusMode = FocusModeEnum.None };
        countSelect.AddThemeFontSizeOverride("font_size", 16);
        countSelect.ApplyLocaleFontSubstitution(FontType.Regular, "font");
        for (var count = 1; count <= 10; count++)
            countSelect.AddItem(chinese ? $"{count} 次" : $"{count} sample{(count == 1 ? "" : "s")}");
        countSelect.Select(2);
        row.AddChild(countSelect);

        var startButton = PreCombatPanelStyles.CreateButton(
            chinese ? "开始模拟" : "Run simulation", 135);
        row.AddChild(startButton);

        var resultsBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        resultsBox.AddThemeConstantOverride(ThemeConstants.BoxContainer.Separation, 2);
        section.AddChild(resultsBox);

        var generation = _planRevision;
        var samples = new List<PlannedCombatSample>();
        var running = false;
        var cancellation = new CancellationTokenSource();
        section.TreeExiting += () => cancellation.Cancel();
        if (!Entry.CombatSolver.SupportsPlanningSimulation)
        {
            status.Text = chinese ? "CombatSolver 未提供规划状态模拟 API。" : "CombatSolver planning API is unavailable.";
            startButton.Disabled = true;
            return;
        }
        var savedReference = entry.Choice switch
        {
            RoutePlanChoice.Combat combat => combat.SimulationReference,
            RoutePlanChoice.EventOption eventChoice => eventChoice.SimulationReference,
            _ => null
        };
        if (savedReference is { } saved)
            status.Text = chinese
                ? $"已选参照 #{saved.SelectedSampleIndex + 1}/{saved.SampleCount}，战后 HP {saved.FinalHp}"
                : $"Reference #{saved.SelectedSampleIndex + 1}/{saved.SampleCount}, final HP {saved.FinalHp}";

        void RenderResults()
        {
            foreach (var child in resultsBox.GetChildren())
                child.QueueFree();
            foreach (var sample in samples)
            {
                var result = sample.Result;
                var text = result.IsSuccess
                    ? (chinese
                        ? $"#{sample.Index + 1} 战损 {result.ProjectedHpLoss ?? 0}，最终 HP {result.FinalHp?.ToString() ?? "?"}，药水 {result.PotionUses.Count}，回合 {result.CombatEndedTurn?.ToString() ?? "?"}，{result.Confidence ?? "未知"}"
                        : $"#{sample.Index + 1} loss {result.ProjectedHpLoss ?? 0}, final HP {result.FinalHp?.ToString() ?? "?"}, potions {result.PotionUses.Count}, turn {result.CombatEndedTurn?.ToString() ?? "?"}, {result.Confidence ?? "unknown"}")
                    : $"#{sample.Index + 1} {result.Status}: {result.Error ?? "unknown error"}";
                var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
                var label = PreCombatPanelStyles.CreateLabel(text, 14, result.IsSuccess ? StsColors.cream : StsColors.red);
                label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                line.AddChild(label);
                if (result.IsSuccess && result.Confidence == "Complete" && result.CombatEndedTurn is not null
                    && result.ProjectedHpLoss is { } loss && result.FinalHp is > 0)
                {
                    var finalHp = result.FinalHp.Value;
                    var choose = PreCombatPanelStyles.CreateButton(
                        chinese ? "作为规划参照" : "Use as plan reference", 145);
                    choose.Disabled = running;
                    var selected = sample;
                    choose.Pressed += () =>
                    {
                        if (_planRevision != generation)
                            return;
                        var currentEntry = RoutePlanTracker.Current?.Entries
                            .FirstOrDefault(candidate => candidate.Coord == entry.Coord);
                        if (currentEntry is null || !currentEntry.ChoiceEquals(entry))
                        {
                            status.Text = chinese ? "计划已变化，请重新模拟。" : "The plan changed; run the simulation again.";
                            return;
                        }
                        acceptReference(new CombatSimulationReference(
                            samples.Count,
                            selected.Index,
                            selected.Seed,
                            loss,
                            finalHp,
                            result.PotionUses.Select(use => new CombatSimulationPotionUse(
                                use.Id, use.Title, use.Turn, use.Slot)).ToArray(),
                            encounter.Id.Entry,
                            stateToken,
                            roomType,
                            entry.Coord.row + 1,
                            entry.Coord.col));
                    };
                    line.AddChild(choose);
                }
                resultsBox.AddChild(line);
            }
        }

        void FinishSimulation(string? error = null)
        {
            running = false;
            if (cancellation.IsCancellationRequested || _planRevision != generation)
                return;
            startButton.Disabled = false;
            countSelect.Disabled = false;
            RenderResults();
            status.Text = error is not null
                ? (chinese ? "模拟失败：" : "Simulation failed: ") + error
                : chinese
                    ? $"已完成 {samples.Count} 次；选择一个成功结果写入规划。"
                    : $"Completed {samples.Count} samples; choose a successful result to record.";
        }

        async Task ReceiveSampleAsync(Task<CombatSolverForecastResult> pending, int index, ulong seed, int count)
        {
            try
            {
                var result = await pending.ConfigureAwait(false);
                SeedOracleDispatcher.Post(() =>
                {
                    if (cancellation.IsCancellationRequested || _planRevision != generation)
                        return;
                    samples.Add(new PlannedCombatSample(index, seed, result));
                    RenderResults();
                    status.Text = chinese ? $"正在模拟 {samples.Count}/{count}…" : $"Simulating {samples.Count}/{count}…";
                    if (samples.Count < count)
                        StartNextSample(count);
                    else
                        FinishSimulation();
                });
            }
            catch (Exception exception)
            {
                SeedOracleDispatcher.Post(() =>
                {
                    if (cancellation.IsCancellationRequested)
                        return;
                    Entry.Logger.Error($"Planning combat simulation failed: {exception}");
                    FinishSimulation(exception.GetBaseException().Message);
                });
            }
        }

        void StartNextSample(int count)
        {
            if (cancellation.IsCancellationRequested || _planRevision != generation)
                return;
            try
            {
                var index = samples.Count;
                var seed = StableCombatSeed(stateToken, encounter.Id.Entry, index);
                // The API captures Godot state synchronously. Every request,
                // and all result rendering, starts on the dispatcher thread.
                var pending = Entry.CombatSolver.SimulatePlanningAsync(
                    liveRun,
                    stateBefore.Run,
                    encounter,
                    entry.Coord.row + 1,
                    entry.Coord.col,
                    roomType,
                    mapPointType,
                    seed,
                    new CombatSolverForecastOptions(
                        SearchBudgetMilliseconds: 5_000,
                        OverallTimeoutMilliseconds: 50_000,
                        MaxDegreeOfParallelism: null,
                        ForceRefresh: true,
                        CloseWorkerAfterRequest: false,
                        WorkerIdleTimeoutMilliseconds: 120_000), cancellation.Token);
                _ = ReceiveSampleAsync(pending, index, seed, count);
            }
            catch (Exception exception)
            {
                Entry.Logger.Error($"Planning combat simulation failed: {exception}");
                FinishSimulation(exception.GetBaseException().Message);
            }
        }

        void StartSimulation()
        {
            if (running)
                return;
            if (_planRevision != generation)
            {
                status.Text = chinese ? "计划已变化，请刷新后重新模拟。" : "The plan changed; refresh and simulate again.";
                return;
            }
            running = true;
            samples.Clear();
            RenderResults();
            startButton.Disabled = true;
            countSelect.Disabled = true;
            var count = countSelect.Selected + 1;
            status.Text = chinese ? $"正在模拟 0/{count}…" : $"Simulating 0/{count}…";
            StartNextSample(count);
        }

        startButton.Pressed += StartSimulation;
        RenderResults();
    }

    private static ulong StableCombatSeed(string stateToken, string encounterId, int sampleIndex)
    {
        var data = Encoding.UTF8.GetBytes($"{stateToken}|{encounterId}|{sampleIndex}");
        var hash = SHA256.HashData(data);
        return BitConverter.ToUInt64(hash, 0);
    }
}

internal static class RoutePlanEntryExtensions
{
    internal static bool ChoiceEquals(this RoutePlanEntry left, RoutePlanEntry right) =>
        left.Coord == right.Coord && Equals(left.Choice, right.Choice);
}
