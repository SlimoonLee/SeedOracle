using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.UI;

internal enum PreCombatDisplayStatus
{
    Deferred,
    NotCalculated,
    Pending,
    Succeeded,
    Failed,
}

internal sealed record PreCombatForecastDisplay(
    PreCombatDisplayStatus Status,
    int? ProjectedHpLoss = null,
    IReadOnlyList<CombatSolverPotionUse>? PotionUses = null,
    string? SearchBoundary = null,
    string? Confidence = null,
    string? Error = null);

internal sealed record PreCombatForecastTarget(
    MapPoint Point,
    RoomType RoomType,
    EncounterDetails Encounter,
    bool IsSecondBoss,
    int MinimumSteps = 1,
    IReadOnlyList<string>? RouteSummaries = null,
    IReadOnlyList<string>? InterveningRoomSummaries = null,
    IReadOnlyList<CombatSolverMapStep>? InterveningMapPoints = null,
    bool UsesCurrentStateAssumption = false,
    int? PlayerCurrentHpOverride = null,
    string StateScenario = "当前战斗状态")
{
    public int ActFloor => Point.coord.row + 1;
}

internal static class PreCombatForecastCoordinator
{
    private const int MaximumCachedResults = 32;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, CombatSolverForecastResult> Completed =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CombatSolverForecastResult> LatestCompletedByTarget =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task<CombatSolverForecastResult>> Active =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Hover rendering is deliberately read-only. A worker is started only by the visible manual forecast panel.
    /// </summary>
    public static PreCombatForecastDisplay? Observe(
        RunState run,
        MapNodeForecast forecast,
        bool isTravelEnabled)
    {
        if (!Entry.CombatSolver.SupportsPreCombatForecast)
            return null;

        var combatVariants = GetCombatVariants(forecast);
        if (combatVariants.Length == 0)
            return null;
        if (!TryCreateKnownTarget(run, forecast, isTravelEnabled, out var target))
            return new PreCombatForecastDisplay(PreCombatDisplayStatus.Deferred);

        try
        {
            var stateToken = Entry.CombatSolver.CaptureLiveStateToken(run);
            var key = BuildTargetKey(stateToken, target);
            lock (Sync)
            {
                if (LatestCompletedByTarget.TryGetValue(key, out var completed))
                    return ToDisplay(completed);
                if (Active.Keys.Any(activeKey => activeKey.StartsWith(key + '|', StringComparison.Ordinal)))
                    return new PreCombatForecastDisplay(PreCombatDisplayStatus.Pending);
            }
        }
        catch
        {
            // The manual panel reports capture failures in full. Hovering must never start work or flood the map UI.
        }

        return new PreCombatForecastDisplay(PreCombatDisplayStatus.NotCalculated);
    }

    public static bool TryGetCached(
        string stateToken,
        PreCombatForecastTarget target,
        CombatSolverForecastOptions options,
        out CombatSolverForecastResult result)
    {
        var key = BuildRequestKey(stateToken, target, options);
        lock (Sync)
            return Completed.TryGetValue(key, out result!);
    }

    public static bool TryCreateImmediateTarget(
        RunState run,
        MapNodeForecast forecast,
        bool isTravelEnabled,
        out PreCombatForecastTarget target)
    {
        target = null!;
        var combatVariants = GetCombatVariants(forecast);
        var identities = combatVariants
            .Select(variant => (variant.RoomType, variant.Encounter!.Id))
            .Distinct()
            .ToArray();
        var isImmediate = isTravelEnabled
                          && forecast.MinimumSteps == 1
                          && forecast.MaximumSteps == 1
                          && forecast.RouteVariants.Count == combatVariants.Length
                          && identities.Length == 1;
        if (!isImmediate)
            return false;

        var selected = combatVariants[0];
        target = new PreCombatForecastTarget(
            forecast.Point,
            selected.RoomType,
            selected.Encounter!,
            run.Map.SecondBossMapPoint is { } secondBoss
            && secondBoss.coord == forecast.Point.coord);
        return true;
    }

    private static bool TryCreateKnownTarget(
        RunState run,
        MapNodeForecast forecast,
        bool isTravelEnabled,
        out PreCombatForecastTarget target)
    {
        if (TryCreateImmediateTarget(run, forecast, isTravelEnabled, out target))
            return true;

        target = null!;
        if (!isTravelEnabled)
            return false;
        var combatVariants = GetCombatVariants(forecast);
        var identities = combatVariants
            .Select(variant => (variant.RoomType, variant.Encounter!.Id))
            .Distinct()
            .ToArray();
        if (combatVariants.Length == 0
            || combatVariants.Length != forecast.RouteVariants.Count
            || identities.Length != 1)
        {
            return false;
        }

        var selected = combatVariants[0];
        target = new PreCombatForecastTarget(
            forecast.Point,
            selected.RoomType,
            selected.Encounter!,
            run.Map.SecondBossMapPoint is { } secondBoss
            && secondBoss.coord == forecast.Point.coord,
            forecast.MinimumSteps,
            UsesCurrentStateAssumption: forecast.MinimumSteps > 1);
        return true;
    }

    public static async Task<CombatSolverForecastResult> CalculateAsync(
        RunState run,
        PreCombatForecastTarget target,
        string expectedStateToken,
        CombatSolverForecastOptions options,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (CombatManager.Instance.IsInProgress)
            return Failure("Unsupported", "战斗进行中不能创建战前预测。");

        string stateToken;
        try
        {
            stateToken = Entry.CombatSolver.CaptureLiveStateToken(run);
        }
        catch (Exception exception)
        {
            return Failure("Failed", exception.GetBaseException().Message);
        }

        if (!stateToken.Equals(expectedStateToken, StringComparison.Ordinal))
            return Failure("LiveStateChanged", "跑局状态已变化；已停止本批计算，请按当前地图重新计算。");

        var effectiveOptions = options with { ForceRefresh = forceRefresh };
        var targetKey = BuildTargetKey(stateToken, target);
        var key = BuildRequestKey(stateToken, target, effectiveOptions);
        Task<CombatSolverForecastResult> task;
        lock (Sync)
        {
            if (!forceRefresh && Completed.TryGetValue(key, out var completed))
                return completed;
            if (!Active.TryGetValue(key, out task!))
            {
                var encounter = ModelDb.GetById<EncounterModel>(target.Encounter.Id);
                task = Entry.CombatSolver.ForecastAsync(
                    run,
                    encounter,
                    target.ActFloor,
                    target.Point.coord.col,
                    target.RoomType,
                    target.Point.PointType,
                    target.IsSecondBoss,
                    effectiveOptions,
                    cancellationToken);
                Active.Add(key, task);
            }
        }

        CombatSolverForecastResult result;
        try
        {
            result = await task;
        }
        catch (Exception exception)
        {
            result = Failure("Failed", exception.GetBaseException().Message);
        }

        lock (Sync)
        {
            Active.Remove(key);
            if (result.IsSuccess)
            {
                if (Completed.Count >= MaximumCachedResults)
                {
                    Completed.Clear();
                    LatestCompletedByTarget.Clear();
                }
                Completed[key] = result;
                LatestCompletedByTarget[targetKey] = result;
            }
        }
        return result;
    }

    private static RouteVariantForecast[] GetCombatVariants(MapNodeForecast forecast) =>
        forecast.RouteVariants
            .Where(variant => variant.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss
                              && variant.Encounter is not null)
            .ToArray();

    private static string BuildTargetKey(string stateToken, PreCombatForecastTarget target) => string.Join(
        '|',
        stateToken,
        target.Encounter.Id,
        target.Point.coord,
        target.RoomType,
        target.Point.PointType,
        target.IsSecondBoss,
        target.PlayerCurrentHpOverride?.ToString() ?? "live-hp",
        string.Join(';', (target.InterveningMapPoints ?? []).Select(static step =>
            $"{step.MapColumn},{step.ActFloor},{step.MapPointType},{step.RoomType}")));

    private static string BuildRequestKey(
        string stateToken,
        PreCombatForecastTarget target,
        CombatSolverForecastOptions options) => string.Join(
        '|',
        BuildTargetKey(stateToken, target),
        options.SearchBudgetMilliseconds,
        options.OverallTimeoutMilliseconds,
        options.MaxDegreeOfParallelism?.ToString() ?? "configured");

    private static CombatSolverForecastResult Failure(string status, string error) => new(
        IsSuccess: false,
        Status: status,
        ProjectedHpLoss: null,
        PotionUses: [],
        SearchBoundary: null,
        Confidence: null,
        FinalHp: null,
        CombatEndedTurn: null,
        SearchElapsedMilliseconds: null,
        TotalElapsedMilliseconds: null,
        Error: error);

    private static PreCombatForecastDisplay ToDisplay(CombatSolverForecastResult result) =>
        result.IsSuccess
            ? new PreCombatForecastDisplay(
                PreCombatDisplayStatus.Succeeded,
                result.ProjectedHpLoss,
                result.PotionUses,
                result.SearchBoundary,
                result.Confidence)
            : new PreCombatForecastDisplay(
                PreCombatDisplayStatus.Failed,
                Error: result.Error ?? result.Status);
}
