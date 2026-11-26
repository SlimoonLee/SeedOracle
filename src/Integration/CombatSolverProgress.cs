using System.Diagnostics;

namespace SeedOracle.Integration;

internal enum CombatSolverPhase
{
    /// <summary>The isolated worker process is not up yet; a request pays a cold start.</summary>
    StartingWorker,

    /// <summary>The worker is alive but not searching: snapshot restore or result marshalling.</summary>
    Preparing,

    /// <summary>The worker holds the request and is spending its search budget.</summary>
    Searching,
}

/// <summary>
/// One observation of an in-flight Combat Solver request. The public API exposes
/// no progress callback, so the phase is inferred from the worker process state;
/// elapsed time and memory are measured here and reported as they change.
/// </summary>
internal readonly record struct CombatSolverProgress(
    CombatSolverPhase Phase,
    TimeSpan Elapsed,
    int LimitMilliseconds,
    long? WorkingSetBytes,
    int? ProcessId,
    int SearchBudgetMilliseconds = 0)
{
    /// <summary>
    /// Elapsed time against the search budget while searching, and on its own while the
    /// worker is still starting. The wall-clock ceiling is a safety net, not the expected
    /// duration, so showing it as a denominator reads like a countdown to a timeout.
    /// </summary>
    private string Amount(bool chinese)
    {
        var elapsed = Elapsed.TotalSeconds;
        if (Phase == CombatSolverPhase.Searching && SearchBudgetMilliseconds > 0)
        {
            var budget = Math.Max(1, SearchBudgetMilliseconds) / 1000d;
            return chinese ? $"{elapsed:0.0}/{budget:0} 秒" : $"{elapsed:0.0}/{budget:0}s";
        }
        return chinese ? $"{elapsed:0.0} 秒" : $"{elapsed:0.0}s";
    }

    internal string Describe(bool chinese)
    {
        var phase = Phase switch
        {
            CombatSolverPhase.StartingWorker => chinese ? "启动求解进程" : "starting worker",
            CombatSolverPhase.Preparing => chinese ? "载入跑局快照" : "loading snapshot",
            _ => chinese ? "搜索出牌路线" : "searching"
        };
        var memory = WorkingSetBytes is { } resident && resident > 0
            ? chinese
                ? $" · 内存 {FormatBytes(resident)}"
                : $" · {FormatBytes(resident)} resident"
            : string.Empty;
        return chinese
            ? $"{Amount(true)} · {phase}{memory}"
            : $"{Amount(false)} · {phase}{memory}";
    }

    internal string DescribeCompact(bool chinese)
    {
        var phase = Phase switch
        {
            CombatSolverPhase.StartingWorker => chinese ? "启动中" : "starting",
            CombatSolverPhase.Preparing => chinese ? "准备中" : "loading",
            _ => chinese ? "搜索中" : "searching"
        };
        var memory = WorkingSetBytes is { } resident && resident >= 512L * 1024 * 1024
            ? $" · {FormatBytes(resident)}"
            : string.Empty;
        return chinese
            ? $"计算中 {Amount(true)} · {phase}{memory}"
            : $"Running {Amount(false)} · {phase}{memory}";
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
        : $"{bytes / (1024d * 1024):0} MB";
}

/// <summary>
/// Polls the worker while a request is in flight. Cheap enough to tick twice a
/// second; every call degrades to "preparing" if the status probe fails.
/// </summary>
internal sealed class CombatSolverProgressMonitor
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    internal CombatSolverProgressMonitor(int limitMilliseconds, int searchBudgetMilliseconds = 0)
    {
        LimitMilliseconds = Math.Max(1, limitMilliseconds);
        SearchBudgetMilliseconds = Math.Max(0, searchBudgetMilliseconds);
    }

    internal int LimitMilliseconds { get; }

    /// <summary>The search budget of the request in flight, used as the progress total.</summary>
    internal int SearchBudgetMilliseconds { get; }

    internal TimeSpan Elapsed => _clock.Elapsed;

    internal CombatSolverProgress Snapshot()
    {
        CombatSolverWorkerStatus? status = null;
        try
        {
            status = Entry.CombatSolver.GetPreCombatWorkerStatus();
        }
        catch
        {
            // A worker probe must never break the request or the frame loop.
        }

        var phase = status switch
        {
            null => CombatSolverPhase.Preparing,
            { IsRunning: false } => CombatSolverPhase.StartingWorker,
            { IsBusy: true } => CombatSolverPhase.Searching,
            _ => CombatSolverPhase.Preparing,
        };
        return new CombatSolverProgress(phase, _clock.Elapsed, LimitMilliseconds,
            status?.WorkingSetBytes, status?.ProcessId, SearchBudgetMilliseconds);
    }
}
