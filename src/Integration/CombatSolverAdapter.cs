using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace SeedOracle.Integration;

internal sealed class CombatSolverAdapter : ICombatSolverAdapter
{
    private const string ModId = "CombatSolver";
    private const string ApiNamespace = "CombatSolver.Api.";
    private static readonly Connection Unavailable = new(CreateStatus(null, null), null);
    private static readonly CombatSolverWorkerStatus InactiveWorker = new(false, false, null, null, null, null, false, null);
    private readonly Lazy<Connection> _connection = new(Connect);

    private sealed record Connection(IntegrationStatus Status, SolverApi? Api);

    // Mod initializers run in load order. Resolve only after the loader has
    // finished, so an early capability check cannot cache a false absence.
    private Connection Current => ModManager.State == ModManagerState.Initialized ? _connection.Value : Unavailable;
    public IntegrationStatus Status => Current.Status;
    public bool SupportsPreCombatForecast => Current.Api is not null;
    public bool SupportsPlanningSimulation => Current.Api?.SimulatePlanning is not null;

    private static Connection Connect()
    {
        var mod = ModManager.GetLoadedMods().FirstOrDefault(mod =>
            string.Equals(mod.manifest?.id, ModId, StringComparison.OrdinalIgnoreCase));
        SolverApi? api = null;
        if (mod is not null)
        {
            try
            {
                var apiType = mod.assemblies
                    .Select(assembly => assembly.GetType(ApiNamespace + "PreCombatForecastApi"))
                    .FirstOrDefault(type => type is not null);
                if (apiType?.GetField("ApiVersion")?.GetValue(null) is int version && version >= 5
                    && apiType.GetProperty("IsAvailable")?.GetValue(null) is true)
                {
                    api = new SolverApi(apiType);
                }
            }
            catch (Exception exception)
            {
                Entry.Logger.Info($"[Integration] Combat Solver API unavailable: {exception.GetBaseException().Message}");
            }
        }

        var status = CreateStatus(mod, api);
        Entry.Logger.Info($"[Integration] {status.Describe()}");
        return new Connection(status, api);
    }

    private static IntegrationStatus CreateStatus(Mod? mod, SolverApi? api) => new(
        ModId,
        mod is null ? "unavailable" : mod.manifest?.version ?? "unknown",
        mod is not null,
        new Dictionary<string, bool>
        {
            ["precombat_public_api_v2"] = api is not null,
            ["precombat_public_api_v3"] = api is not null,
            ["precombat_public_api_v4"] = api is not null,
            ["precombat_public_api_v5"] = api is not null,
            ["reusable_isolated_worker"] = api is not null,
            ["isolated_worker_audio_muted"] = api is not null,
            ["precombat_worker_memory_status"] = api is not null,
            ["hypothetical_combat_samples"] = api is not null,
            ["planning_shadow_combat_samples"] = api?.SimulatePlanning is not null
        });

    public string CaptureLiveStateToken(RunState run)
    {
        var api = Current.Api ?? throw new NotSupportedException(UnavailableReason(planning: false));
        return api.CaptureLiveStateToken(run);
    }

    public Task StopPreCombatWorkerAsync() => Current.Api?.StopWorker() ?? Task.CompletedTask;

    public CombatSolverWorkerStatus GetPreCombatWorkerStatus() => Current.Api is { } api
        ? ToWorkerStatus(Invoke(api.GetWorkerStatus)!)
        : InactiveWorker;

    public Task SetPreCombatWorkerIdleTimeoutAsync(int? idleTimeoutMilliseconds) =>
        Current.Api?.SetWorkerIdleTimeout(idleTimeoutMilliseconds) ?? Task.CompletedTask;

    public async Task<CombatSolverWorkerStatus> RestartPreCombatWorkerAsync(
        RunState run,
        int? idleTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (Current.Api is not { } api)
            return InactiveWorker;
        return ToWorkerStatus(await InvokeAsync(api.RestartWorker, run, idleTimeoutMilliseconds, cancellationToken));
    }

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
        if (Current.Api is not { } api)
            return Unsupported(planning: false);

        var request = CreateOptions(api.ForecastOptions, options);
        Set(request, nameof(options.PlayerCurrentHpOverride), options.PlayerCurrentHpOverride);
        Set(request, nameof(options.InterveningMapPoints), api.CreateMapSteps(options.InterveningMapPoints ?? []));
        Set(request, nameof(options.CancelWorkerWhenCallerCancels), options.CancelWorkerWhenCallerCancels);
        Set(request, nameof(options.ForceRefresh), options.ForceRefresh);
        var response = await InvokeAsync(api.Forecast, run, encounter, targetActFloor, targetMapColumn,
            ToRoomKind(api.RoomKind, roomType), ToMapPointKind(api.MapPointKind, mapPointType),
            isSecondBoss, request, cancellationToken);
        return ToForecastResult(encounter, options, response);
    }

    public async Task<CombatSolverForecastResult> SimulateAsync(
        RunState run,
        EncounterModel encounter,
        RoomType roomType,
        ulong sampleSeed,
        CombatSolverForecastOptions options,
        CancellationToken cancellationToken = default)
    {
        if (Current.Api is not { } api)
            return Unsupported(planning: false);

        var request = CreateOptions(api.SimulationOptions, options);
        Set(request, "SampleSeed", sampleSeed);
        var response = await InvokeAsync(api.Simulate, run, encounter, ToRoomKind(api.RoomKind, roomType),
            request, cancellationToken);
        return ToForecastResult(encounter, options, response);
    }

    public async Task<CombatSolverForecastResult> SimulatePlanningAsync(
        RunState liveRun,
        SerializableRun plannedRun,
        EncounterModel encounter,
        int targetActFloor,
        int targetMapColumn,
        RoomType roomType,
        MapPointType mapPointType,
        ulong sampleSeed,
        CombatSolverForecastOptions options,
        CancellationToken cancellationToken = default)
    {
        if (Current.Api is not { SimulatePlanning: { } simulate } api)
            return Unsupported(planning: true);

        var request = CreateOptions(api.SimulationOptions, options);
        Set(request, "SampleSeed", sampleSeed);
        var response = await InvokeAsync(simulate, liveRun, plannedRun, encounter, targetActFloor, targetMapColumn,
            ToRoomKind(api.RoomKind, roomType), ToMapPointKind(api.MapPointKind, mapPointType), request, cancellationToken);
        return ToForecastResult(encounter, options, response);
    }

    private string UnavailableReason(bool planning) => !Status.IsLoaded
        ? "Combat Solver is not loaded."
        : planning
            ? "Combat Solver planning simulation API v6 is unavailable."
            : "Combat Solver pre-combat API v5 is unavailable.";

    private CombatSolverForecastResult Unsupported(bool planning) => new(
        false, "Unsupported", null, [], null, null, null, null, null, null, UnavailableReason(planning));

    private static object CreateOptions(Type type, CombatSolverForecastOptions options)
    {
        var request = Activator.CreateInstance(type)!;
        Set(request, nameof(options.SearchBudgetMilliseconds), options.SearchBudgetMilliseconds);
        Set(request, nameof(options.OverallTimeoutMilliseconds), options.OverallTimeoutMilliseconds);
        Set(request, nameof(options.MaxDegreeOfParallelism), options.MaxDegreeOfParallelism);
        Set(request, nameof(options.CloseWorkerAfterRequest), options.CloseWorkerAfterRequest);
        Set(request, nameof(options.WorkerIdleTimeoutMilliseconds), options.WorkerIdleTimeoutMilliseconds);
        return request;
    }

    private static object ToRoomKind(Type kind, RoomType roomType) => Enum.Parse(kind, roomType switch
    {
        RoomType.Monster => "Normal",
        RoomType.Elite => "Elite",
        RoomType.Boss => "Boss",
        _ => throw new ArgumentOutOfRangeException(nameof(roomType), roomType, null)
    });

    private static object ToMapPointKind(Type kind, MapPointType pointType) => Enum.Parse(kind, pointType switch
    {
        MapPointType.Monster => "Normal",
        MapPointType.Elite => "Elite",
        MapPointType.Boss => "Boss",
        MapPointType.Unknown => "Unknown",
        MapPointType.Ancient => "Event",
        _ => throw new ArgumentOutOfRangeException(nameof(pointType), pointType, null)
    });

    private static CombatSolverForecastResult ToForecastResult(EncounterModel encounter,
        CombatSolverForecastOptions options, object response)
    {
        var result = new CombatSolverForecastResult(
            Read<bool>(response, nameof(CombatSolverForecastResult.IsSuccess)),
            Read<object>(response, nameof(CombatSolverForecastResult.Status)).ToString()!,
            Read<int?>(response, nameof(CombatSolverForecastResult.ProjectedHpLoss)),
            Read<IEnumerable>(response, nameof(CombatSolverForecastResult.PotionUses)).Cast<object>()
                .Select(use => new CombatSolverPotionUse(
                    Read<string>(use, nameof(CombatSolverPotionUse.Id)),
                    Read<string>(use, nameof(CombatSolverPotionUse.Title)),
                    Read<int>(use, nameof(CombatSolverPotionUse.Turn)),
                    Read<int>(use, nameof(CombatSolverPotionUse.Slot)))).ToArray(),
            Read<string?>(response, nameof(CombatSolverForecastResult.SearchBoundary)),
            Read<object?>(response, nameof(CombatSolverForecastResult.Confidence))?.ToString(),
            Read<int?>(response, nameof(CombatSolverForecastResult.FinalHp)),
            Read<int?>(response, nameof(CombatSolverForecastResult.CombatEndedTurn)),
            Read<double?>(response, nameof(CombatSolverForecastResult.SearchElapsedMilliseconds)),
            Read<double?>(response, nameof(CombatSolverForecastResult.TotalElapsedMilliseconds)),
            Read<string?>(response, nameof(CombatSolverForecastResult.Error)))
        {
            TimeoutMilliseconds = options.OverallTimeoutMilliseconds
        };
        if (result.TimedOut)
        {
            Entry.Logger.Info(
                $"[CombatSolver] timeout encounter={encounter.Id.Entry} budget={options.SearchBudgetMilliseconds}ms "
                + $"ceiling={options.OverallTimeoutMilliseconds}ms elapsed={result.TotalElapsedMilliseconds:0}ms");
        }
        return result;
    }

    private static CombatSolverWorkerStatus ToWorkerStatus(object status) => new(
        Read<bool>(status, nameof(CombatSolverWorkerStatus.IsRunning)),
        Read<bool>(status, nameof(CombatSolverWorkerStatus.IsBusy)),
        Read<int?>(status, nameof(CombatSolverWorkerStatus.ProcessId)),
        Read<long?>(status, nameof(CombatSolverWorkerStatus.WorkingSetBytes)),
        Read<long?>(status, nameof(CombatSolverWorkerStatus.PrivateMemoryBytes)),
        Read<long?>(status, nameof(CombatSolverWorkerStatus.PeakWorkingSetBytes)),
        Read<bool>(status, nameof(CombatSolverWorkerStatus.AudioMuted)),
        Read<int?>(status, nameof(CombatSolverWorkerStatus.IdleTimeoutMilliseconds)));

    private static PropertyInfo Property(object target, string name) => target.GetType().GetProperty(name)
        ?? throw new MissingMemberException(target.GetType().FullName, name);
    private static T Read<T>(object target, string name) => (T)Property(target, name).GetValue(target)!;
    private static void Set(object target, string name, object? value) => Property(target, name).SetValue(target, value);

    private static object? Invoke(MethodInfo method, params object?[] arguments) =>
        method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, arguments, null);

    private static async Task<object> InvokeAsync(MethodInfo method, params object?[] arguments)
    {
        var task = (Task)Invoke(method, arguments)!;
        await task;
        return method.ReturnType.GetProperty("Result")!.GetValue(task)!;
    }

    // Bind the public contract without taking a CLR reference to the optional DLL.
    private sealed class SolverApi
    {
        internal Type RoomKind { get; }
        internal Type MapPointKind { get; }
        internal Type ForecastOptions { get; }
        internal Type SimulationOptions { get; }
        internal MethodInfo Forecast { get; }
        internal MethodInfo Simulate { get; }
        internal MethodInfo? SimulatePlanning { get; }
        internal Func<RunState, string> CaptureLiveStateToken { get; }
        internal Func<Task> StopWorker { get; }
        internal MethodInfo GetWorkerStatus { get; }
        internal Func<int?, Task> SetWorkerIdleTimeout { get; }
        internal MethodInfo RestartWorker { get; }

        private readonly Type _mapStep;
        private readonly ConstructorInfo _mapStepConstructor;

        internal SolverApi(Type apiType)
        {
            Type Contract(string name) => apiType.Assembly.GetType(ApiNamespace + name, throwOnError: true)!;
            MethodInfo? Find(string name, Type result, params Type[] parameters)
            {
                var method = apiType.GetMethod(name, BindingFlags.Public | BindingFlags.Static, parameters);
                return method?.ReturnType == result ? method : null;
            }
            MethodInfo Require(string name, Type result, params Type[] parameters) =>
                Find(name, result, parameters) ?? throw new MissingMethodException(apiType.FullName, name);

            RoomKind = Contract("PreCombatRoomKind");
            MapPointKind = Contract("PreCombatMapPointKind");
            ForecastOptions = Contract("PreCombatForecastOptions");
            SimulationOptions = Contract("PreCombatSimulationOptions");
            _mapStep = Contract("PreCombatMapStep");
            _mapStepConstructor = _mapStep.GetConstructor([typeof(MapCoord), typeof(RoomType), typeof(MapPointType)])
                ?? throw new MissingMethodException(_mapStep.FullName, ".ctor");

            var result = typeof(Task<>).MakeGenericType(Contract("PreCombatForecastResult"));
            var workerStatus = Contract("PreCombatWorkerStatus");
            Forecast = Require("ForecastAsync", result, typeof(RunState), typeof(EncounterModel),
                typeof(int), typeof(int), RoomKind, MapPointKind, typeof(bool), ForecastOptions, typeof(CancellationToken));
            Simulate = Require("SimulateAsync", result, typeof(RunState), typeof(EncounterModel),
                RoomKind, SimulationOptions, typeof(CancellationToken));
            SimulatePlanning = Find("SimulatePlanningAsync", result, typeof(RunState), typeof(SerializableRun),
                typeof(EncounterModel), typeof(int), typeof(int), RoomKind, MapPointKind, SimulationOptions, typeof(CancellationToken));

            CaptureLiveStateToken = Require("CaptureLiveStateToken", typeof(string), typeof(RunState))
                .CreateDelegate<Func<RunState, string>>();
            StopWorker = Require("StopWorkerAsync", typeof(Task)).CreateDelegate<Func<Task>>();
            GetWorkerStatus = Require("GetWorkerStatus", workerStatus);
            SetWorkerIdleTimeout = Require("SetWorkerIdleTimeoutAsync", typeof(Task), typeof(int?))
                .CreateDelegate<Func<int?, Task>>();
            RestartWorker = Require("RestartWorkerAsync", typeof(Task<>).MakeGenericType(workerStatus),
                typeof(RunState), typeof(int?), typeof(CancellationToken));
        }

        internal Array CreateMapSteps(IReadOnlyList<CombatSolverMapStep> steps)
        {
            var result = Array.CreateInstance(_mapStep, steps.Count);
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                result.SetValue(_mapStepConstructor.Invoke(
                    [new MapCoord(step.MapColumn, step.ActFloor - 1), step.RoomType, step.MapPointType]), i);
            }
            return result;
        }
    }
}
