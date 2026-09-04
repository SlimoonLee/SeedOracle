using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Integration;

internal interface ICombatSolverAdapter
{
    IntegrationStatus Status { get; }

    bool SupportsPreCombatForecast { get; }

    string CaptureLiveStateToken(RunState run);

    Task<CombatSolverForecastResult> ForecastAsync(
        RunState run,
        EncounterModel encounter,
        int targetActFloor,
        int targetMapColumn,
        RoomType roomType,
        MapPointType mapPointType,
        bool isSecondBoss,
        CombatSolverForecastOptions options,
        CancellationToken cancellationToken = default);

    Task<CombatSolverForecastResult> SimulateAsync(
        RunState run,
        EncounterModel encounter,
        RoomType roomType,
        ulong sampleSeed,
        CombatSolverForecastOptions options,
        CancellationToken cancellationToken = default);

    CombatSolverWorkerStatus GetPreCombatWorkerStatus();

    Task SetPreCombatWorkerIdleTimeoutAsync(int? idleTimeoutMilliseconds);

    Task<CombatSolverWorkerStatus> RestartPreCombatWorkerAsync(
        RunState run,
        int? idleTimeoutMilliseconds,
        CancellationToken cancellationToken = default);

    Task StopPreCombatWorkerAsync();
}

internal sealed record CombatSolverForecastOptions(
    int SearchBudgetMilliseconds,
    int OverallTimeoutMilliseconds,
    int? MaxDegreeOfParallelism,
    int? PlayerCurrentHpOverride = null,
    bool CancelWorkerWhenCallerCancels = true,
    bool ForceRefresh = false,
    IReadOnlyList<CombatSolverMapStep>? InterveningMapPoints = null,
    bool CloseWorkerAfterRequest = false,
    int? WorkerIdleTimeoutMilliseconds = 120_000);

internal sealed record CombatSolverWorkerStatus(
    bool IsRunning,
    bool IsBusy,
    int? ProcessId,
    long? WorkingSetBytes,
    long? PrivateMemoryBytes,
    long? PeakWorkingSetBytes,
    bool AudioMuted,
    int? IdleTimeoutMilliseconds);

internal sealed record CombatSolverMapStep(
    int MapColumn,
    int ActFloor,
    RoomType RoomType,
    MapPointType MapPointType);

internal sealed record CombatSolverPotionUse(
    string Id,
    string Title,
    int Turn,
    int Slot);

internal sealed record CombatSolverForecastResult(
    bool IsSuccess,
    string Status,
    int? ProjectedHpLoss,
    IReadOnlyList<CombatSolverPotionUse> PotionUses,
    string? SearchBoundary,
    string? Confidence,
    int? FinalHp,
    int? CombatEndedTurn,
    double? SearchElapsedMilliseconds,
    double? TotalElapsedMilliseconds,
    string? Error);
