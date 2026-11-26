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

    internal static ICombatSolverAdapter CombatSolver { get; } = new CombatSolverAdapter();

    public static void Initialize()
    {
        SeedOracleData.Register();
        SeedOracleSettingsUi.Register();

        // The smoke runner must start regardless of adapter health: the gate
        // has to produce a report even when Random Foreseer is broken.
        if (Smoke.SmokeRunner.IsRequested)
            Smoke.SmokeRunner.Begin();

        // Random Foreseer's selected variant can fail on its own when a patch
        // no longer matches the host game — adapter construction must not take
        // the rest of Seed Oracle down: the unavailable stub degrades
        // every RF-delegated forecast while our own predictions (encounter,
        // upcoming ancients/bosses, blitz window, audits) keep working.
        try
        {
            var randomForeseer = new RandomForeseerAdapter();
            RandomForeseer = randomForeseer;

            Logger.Info($"[Integration] {randomForeseer.Status.Describe()}");
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            Logger.Info($"[Integration] adapter construction failed: {root}");
            RandomForeseer = new RandomForeseerUnavailableAdapter();
        }

        MapForecasts = new MapForecastService(RandomForeseer);

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
