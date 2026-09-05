using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using SeedOracle.Data;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.Settings;
using SeedOracle.UI;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace SeedOracle;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public const string ModId = "SeedOracle";

    private static readonly object FailureLock = new();
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.Ordinal);

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    internal static MapForecastService MapForecasts { get; private set; } = null!;

    internal static IRandomForeseerAdapter RandomForeseer { get; private set; } = null!;

    internal static ICombatSolverAdapter CombatSolver { get; private set; } = null!;

    public static void Initialize()
    {
        SeedOracleData.Register();
        SeedOracleSettingsUi.Register();

        var randomForeseer = new RandomForeseerAdapter();
        var combatSolver = new CombatSolverAdapter();
        RandomForeseer = randomForeseer;
        CombatSolver = combatSolver;
        MapForecasts = new MapForecastService(randomForeseer);

        if (Smoke.SmokeRunner.IsRequested)
            Smoke.SmokeRunner.Begin();

        Logger.Info($"[Integration] {randomForeseer.Status.Describe()}");
        Logger.Info($"[Integration] {combatSolver.Status.Describe()}");
        if (!combatSolver.SupportsPreCombatForecast)
        {
            Logger.Info(
                "[Integration] Combat Solver pre-combat API v3 is unavailable; combat damage estimates remain disabled.");
        }

        var assembly = Assembly.GetExecutingAssembly();
        new Harmony($"{ModId}.Harmony").PatchAll(assembly);
        if (NGame.Instance is { } host)
            SeedOracleDispatcher.Ensure(host);
        Logger.Info("Seed Oracle initialized.");
    }

    internal static void ReportPredictionFailure(MapPoint point, Exception exception)
    {
        var root = exception.GetBaseException();
        var key = $"{point.PointType}:{point.coord}:{root.GetType().FullName}:{root.Message}";
        lock (FailureLock)
        {
            if (!ReportedFailures.Add(key))
                return;
        }

        Logger.Error($"Map prediction failed for {point.PointType} {point.coord}: {root}");
    }
}
