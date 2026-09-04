using System.Reflection;
using CombatSolver.Api;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Integration;

internal sealed class CombatSolverAdapter : ICombatSolverAdapter
{
    public IntegrationStatus Status { get; }

    public bool SupportsPreCombatForecast { get; }

    public CombatSolverAdapter()
    {
        var assembly = typeof(global::CombatSolver.Entry).Assembly;
        SupportsPreCombatForecast = PreCombatForecastApi.ApiVersion >= 5
                                    && PreCombatForecastApi.IsAvailable;

        Status = new IntegrationStatus(
            "CombatSolver",
            GetVersion(assembly),
            true,
            new Dictionary<string, bool>
            {
                ["precombat_public_api_v2"] = SupportsPreCombatForecast,
                ["precombat_public_api_v3"] = SupportsPreCombatForecast,
                ["precombat_public_api_v4"] = SupportsPreCombatForecast,
                ["precombat_public_api_v5"] = SupportsPreCombatForecast,
                ["reusable_isolated_worker"] = SupportsPreCombatForecast,
                ["isolated_worker_audio_muted"] = SupportsPreCombatForecast,
                ["precombat_worker_memory_status"] = SupportsPreCombatForecast,
                ["hypothetical_combat_samples"] = SupportsPreCombatForecast
            });
    }

    public string CaptureLiveStateToken(RunState run) =>
        PreCombatForecastApi.CaptureLiveStateToken(run);

    public Task StopPreCombatWorkerAsync() => PreCombatForecastApi.StopWorkerAsync();

    public CombatSolverWorkerStatus GetPreCombatWorkerStatus() =>
        ToWorkerStatus(PreCombatForecastApi.GetWorkerStatus());

    public Task SetPreCombatWorkerIdleTimeoutAsync(int? idleTimeoutMilliseconds) =>
        PreCombatForecastApi.SetWorkerIdleTimeoutAsync(idleTimeoutMilliseconds);

    public async Task<CombatSolverWorkerStatus> RestartPreCombatWorkerAsync(
        RunState run,
        int? idleTimeoutMilliseconds,
        CancellationToken cancellationToken = default) =>
        ToWorkerStatus(await PreCombatForecastApi.RestartWorkerAsync(
            run,
            idleTimeoutMilliseconds,
            cancellationToken));

    public async Task<CombatSolverForecastResult> ForecastAsync(
        RunState run,
        EncounterModel encounter,
        int targetActFloor,
        int targetMapColumn,
        RoomType roomType,
        MapPointType mapPointType,
        bool isSecondBoss,
        CombatSolverForecastOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsPreCombatForecast)
        {
            return new CombatSolverForecastResult(
                false,
                PreCombatForecastStatus.Unsupported.ToString(),
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                "Combat Solver pre-combat API v5 is unavailable.");
        }

        var kind = roomType switch
        {
            RoomType.Monster => PreCombatRoomKind.Normal,
            RoomType.Elite => PreCombatRoomKind.Elite,
            RoomType.Boss => PreCombatRoomKind.Boss,
            _ => throw new ArgumentOutOfRangeException(nameof(roomType), roomType, null)
        };
        var result = await PreCombatForecastApi.ForecastAsync(
            run,
            encounter,
            targetActFloor,
            targetMapColumn,
            kind,
            mapPointType switch
            {
                MapPointType.Monster => PreCombatMapPointKind.Normal,
                MapPointType.Elite => PreCombatMapPointKind.Elite,
                MapPointType.Boss => PreCombatMapPointKind.Boss,
                MapPointType.Unknown => PreCombatMapPointKind.Unknown,
                _ => throw new ArgumentOutOfRangeException(nameof(mapPointType), mapPointType, null)
            },
            isSecondBoss,
            new PreCombatForecastOptions
            {
                SearchBudgetMilliseconds = options.SearchBudgetMilliseconds,
                OverallTimeoutMilliseconds = options.OverallTimeoutMilliseconds,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                PlayerCurrentHpOverride = options.PlayerCurrentHpOverride,
                InterveningMapPoints = (options.InterveningMapPoints ?? [])
                    .Select(static step => new PreCombatMapStep(
                        new MapCoord(step.MapColumn, step.ActFloor - 1),
                        step.RoomType,
                        step.MapPointType))
                    .ToArray(),
                CancelWorkerWhenCallerCancels = options.CancelWorkerWhenCallerCancels,
                ForceRefresh = options.ForceRefresh,
                CloseWorkerAfterRequest = options.CloseWorkerAfterRequest,
                WorkerIdleTimeoutMilliseconds = options.WorkerIdleTimeoutMilliseconds,
            },
            cancellationToken: cancellationToken);
        return ToForecastResult(result);
    }

    public async Task<CombatSolverForecastResult> SimulateAsync(
        RunState run,
        EncounterModel encounter,
        RoomType roomType,
        ulong sampleSeed,
        CombatSolverForecastOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsPreCombatForecast)
        {
            return new CombatSolverForecastResult(
                false,
                PreCombatForecastStatus.Unsupported.ToString(),
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                "Combat Solver pre-combat API v5 is unavailable.");
        }

        PreCombatForecastResult result = await PreCombatForecastApi.SimulateAsync(
            run,
            encounter,
            ToRoomKind(roomType),
            new PreCombatSimulationOptions
            {
                SearchBudgetMilliseconds = options.SearchBudgetMilliseconds,
                OverallTimeoutMilliseconds = options.OverallTimeoutMilliseconds,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                SampleSeed = sampleSeed,
                CloseWorkerAfterRequest = options.CloseWorkerAfterRequest,
                WorkerIdleTimeoutMilliseconds = options.WorkerIdleTimeoutMilliseconds,
            },
            cancellationToken);
        return ToForecastResult(result);
    }

    private static CombatSolverForecastResult ToForecastResult(PreCombatForecastResult result) => new(
            result.IsSuccess,
            result.Status.ToString(),
            result.ProjectedHpLoss,
            result.PotionUses.Select(use => new CombatSolverPotionUse(
                use.Id,
                use.Title,
                use.Turn,
                use.Slot)).ToArray(),
            result.SearchBoundary,
            result.Confidence?.ToString(),
            result.FinalHp,
            result.CombatEndedTurn,
            result.SearchElapsedMilliseconds,
            result.TotalElapsedMilliseconds,
            result.Error);

    private static PreCombatRoomKind ToRoomKind(RoomType roomType) => roomType switch
    {
        RoomType.Monster => PreCombatRoomKind.Normal,
        RoomType.Elite => PreCombatRoomKind.Elite,
        RoomType.Boss => PreCombatRoomKind.Boss,
        _ => throw new ArgumentOutOfRangeException(nameof(roomType), roomType, null)
    };

    private static CombatSolverWorkerStatus ToWorkerStatus(PreCombatWorkerStatus status) => new(
        status.IsRunning,
        status.IsBusy,
        status.ProcessId,
        status.WorkingSetBytes,
        status.PrivateMemoryBytes,
        status.PeakWorkingSetBytes,
        status.AudioMuted,
        status.IdleTimeoutMilliseconds);

    private static string GetVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString(3)
        ?? "unknown";
}
