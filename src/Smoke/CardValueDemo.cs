using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.Smoke;

/// <summary>
/// One-shot first-act route report used to validate the card-value dataset
/// boundary. It evaluates every reachable route to every first-act elite with
/// the normal Combat Solver defaults, without making a gameplay choice.
/// </summary>
internal static class CardValueDemo
{
    private const string Arg = "--seed-oracle-card-value-demo";
    private static bool _started;
    private static bool _completed;
    private static Task? _run;

    public static bool IsRequested => OS.GetCmdlineArgs().Any(argument => argument == Arg);
    public static bool IsCompleted => _completed;

    public static void Tick()
    {
        if (!IsRequested || _completed || _run is not null)
            return;
        if (NMapScreen.Instance is not { IsOpen: true, IsTravelEnabled: true })
            return;

        var run = RunManager.Instance?.DebugOnlyGetState();
        if (run is null || run.CurrentActIndex != 0 || run.Players.FirstOrDefault()?.Character.Id.Entry != "IRONCLAD")
        {
            if (_started)
                return;
            return;
        }

        _started = true;
        _run = EvaluateAsync(run);
        _ = _run.ContinueWith(task =>
        {
            if (task.IsFaulted)
                Entry.Logger.Error($"[CardValueDemo] failed: {task.Exception?.GetBaseException()}");
            _completed = true;
            _run = null;
            SmokeReport.Flush();
            NGame.Instance?.GetTree()?.Quit();
        }, TaskScheduler.Default);
    }

    private static async Task EvaluateAsync(RunState run)
    {
        var elites = run.Map.GetAllMapPoints()
            .Where(point => point.PointType == MapPointType.Elite)
            .OrderBy(point => point.coord.row)
            .ThenBy(point => point.coord.col)
            .ToArray();
        SmokeReport.Add($"card-value-demo seed={run.Rng.StringSeed} character={run.Players.First().Character.Id.Entry} act=1 elites={elites.Length}");

        var routeCount = 0;
        var successCount = 0;
        var solverRequestCount = 0;
        var combatRngProbes = new HashSet<ulong>();
        var forecastCache = new Dictionary<string, CombatSolverForecastResult>(StringComparer.Ordinal);
        foreach (var target in elites)
        {
            var exploration = RouteStateExplorer.Explore(run, target);
            var worldlines = RouteWorldlinePredictor.Predict(run, target, exploration.Paths);
            SmokeReport.Add($"elite target={target.coord} routes={exploration.Paths.Count} truncated={exploration.WasTruncated}");
            for (var index = 0; index < exploration.Paths.Count; index++)
            {
                var path = exploration.Paths[index];
                var worldline = worldlines[index];
                routeCount++;
                var firstCombatIndex = Array.FindIndex(
                    worldline.ResolvedRooms.ToArray(), static room => room is RoomType.Monster or RoomType.Elite or RoomType.Boss);
                if (firstCombatIndex < 0)
                {
                    SmokeReport.Add($"route elite_target={target.coord} index={index + 1} path={FormatPath(path)} status=no-combat-before-elite");
                    continue;
                }

                var firstCombatPath = new RoutePath(path.Steps.Take(firstCombatIndex + 1).ToArray());
                var firstCombatPoint = firstCombatPath.Steps[^1].Point;
                var firstCombatWorldline = RouteWorldlinePredictor.Predict(
                        run,
                        firstCombatPoint,
                        [firstCombatPath])
                    .Single();
                if (firstCombatWorldline.TargetEncounter is not { } encounter)
                {
                    SmokeReport.Add($"route elite_target={target.coord} index={index + 1} path={FormatPath(path)} status=no-encounter");
                    continue;
                }

                var intervening = firstCombatPath.Steps
                    .Take(firstCombatPath.Steps.Count - 1)
                    .Select((step, roomIndex) => new CombatSolverMapStep(
                        step.Point.coord.col,
                        step.Point.coord.row + 1,
                        firstCombatWorldline.ResolvedRooms[roomIndex],
                        step.Point.PointType))
                    .ToArray();
                // Keep the engine's private combat-RNG probe in the report for diagnostics,
                // but do not use it as the learning/sample boundary. The card-value model
                // is intentionally limited to fair, route-visible sequence information.
                var firstEnemyCombatId = (uint)run.Players.Count;
                var combatRngProbe = unchecked((ulong)((long)run.Rng.Seed
                    + firstCombatPoint.coord.col
                    + firstCombatPoint.coord.row
                    + run.CurrentActIndex
                    + firstEnemyCombatId));
                combatRngProbes.Add(combatRngProbe);
                var normalOrdinal = firstCombatWorldline.ResolvedRooms
                    .Take(firstCombatIndex)
                    .Count(room => room == RoomType.Monster);
                var eliteOrdinal = firstCombatWorldline.ResolvedRooms
                    .Take(firstCombatIndex)
                    .Count(room => room == RoomType.Elite);
                var bossOrdinal = firstCombatWorldline.ResolvedRooms
                    .Take(firstCombatIndex)
                    .Count(room => room == RoomType.Boss);
                var eventOrdinal = firstCombatWorldline.ResolvedRooms
                    .Take(firstCombatIndex)
                    .Count(room => room == RoomType.Event);
                var shopOrdinal = firstCombatWorldline.ResolvedRooms
                    .Take(firstCombatIndex)
                    .Count(room => room == RoomType.Shop);
                var cacheKey = string.Join('|',
                    "fair-sequence-v1",
                    firstCombatWorldline.TargetRoomType,
                    normalOrdinal,
                    eliteOrdinal,
                    bossOrdinal,
                    eventOrdinal,
                    shopOrdinal,
                    string.Join(';', intervening.Select(step =>
                        $"{step.MapColumn},{step.ActFloor},{step.RoomType},{step.MapPointType}")));
                if (!forecastCache.TryGetValue(cacheKey, out var result))
                {
                    solverRequestCount++;
                    try
                    {
                        result = await Entry.CombatSolver.ForecastAsync(
                            run,
                            encounter,
                            firstCombatPoint.coord.row + 1,
                            firstCombatPoint.coord.col,
                            firstCombatWorldline.TargetRoomType,
                            firstCombatPoint.PointType,
                            false,
                            new CombatSolverForecastOptions(
                                SearchBudgetMilliseconds: 8_000,
                                OverallTimeoutMilliseconds: 60_000,
                                MaxDegreeOfParallelism: null,
                                ForceRefresh: true,
                                CloseWorkerAfterRequest: false,
                                InterveningMapPoints: intervening,
                                WorkerIdleTimeoutMilliseconds: 120_000));
                    }
                    catch (Exception exception)
                    {
                        result = new CombatSolverForecastResult(
                            false, "Exception", null, [], null, null, null, null, null, null,
                            exception.GetBaseException().Message);
                    }
                    forecastCache[cacheKey] = result;
                }

                if (result.IsSuccess)
                    successCount++;
                SmokeReport.Add(
                    $"route elite_target={target.coord} index={index + 1} first_combat={firstCombatPoint.coord} "
                    + $"path={FormatPath(path)} "
                    + $"encounter={encounter.Id.Entry} status={result.Status} loss={result.ProjectedHpLoss?.ToString() ?? "?"} "
                    + $"final_hp={result.FinalHp?.ToString() ?? "?"} potions={result.PotionUses.Count} "
                    + $"combat_rng_probe={combatRngProbe} "
                    + $"confidence={result.Confidence ?? "?"} elapsed_ms={result.TotalElapsedMilliseconds?.ToString("0") ?? "?"} "
                    + $"error={result.Error ?? "-"}");
            }
        }

        SmokeReport.Add($"card-value-demo SUMMARY: routes={routeCount} succeeded={successCount} "
            + $"solver_requests={solverRequestCount} distinct_combat_rng_probes={combatRngProbes.Count}");
    }

    private static string FormatPath(RoutePath path) =>
        string.Join(">", path.Steps.Select(step => $"{step.Point.coord.row + 1}:{step.Point.coord.col}"));
}
