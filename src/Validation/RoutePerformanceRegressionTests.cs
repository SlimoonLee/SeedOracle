#if DEBUG
using System.Diagnostics;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class RoutePerformanceRegressionTests
{
    internal static void Run(RunState live)
    {
        var before = LiveFingerprint.Capture(live);
        var planning = new PlanningPredictionService();
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var state = planning.CreateState(live, player);
        planning.AddRelic(state, ModelDb.Relic<WingedBoots>().Id);
        var boots = state.Player.Relics.OfType<WingedBoots>().Single();
        boots.TimesUsed = 0;
        var cases = ValidateSmallMap(state.Run, boots);
        state = planning.CreateState(live, player);
        planning.AddRelic(state, ModelDb.Relic<WingedBoots>().Id);
        state.Player.Relics.OfType<WingedBoots>().Single().TimesUsed = 0;
        ValidateDeferredRewardRefresh(state, planning);
        var points = state.Run.Map.GetAllMapPoints().Append(state.Run.Map.BossMapPoint)
            .Where(point => point.coord.row > (state.Run.CurrentMapPoint?.coord.row ?? -2))
            .OrderBy(point => point.coord.row).ThenBy(point => point.coord.col).ToArray();
        var timer = Stopwatch.StartNew();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var paths = 0;
        var truncated = 0;
        foreach (var point in points)
        {
            var exploration = RouteStateExplorer.Explore(state.Run, point);
            paths += exploration.Paths.Count;
            if (exploration.WasTruncated)
                truncated++;
        }
        var explorationMs = timer.ElapsedMilliseconds;
        var explorationBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Require(explorationBytes < 64_000_000, "full-map exploration must not allocate hundreds of MB with three travel uses");
        timer.Restart();
        bytes = GC.GetAllocatedBytesForCurrentThread();
        var targets = 0;
        foreach (var point in points.Where(point => point.PointType is MapPointType.Monster
                     or MapPointType.Elite or MapPointType.Boss or MapPointType.Unknown))
            targets += PreCombatForecastPanelControl.BuildPointTargets(state.Run, point).Count;
        Require(before.DescribeDifference(LiveFingerprint.Capture(live)) is null, "route performance checks changed the live run");
        Smoke.SmokeReport.Add($"route-performance PASS: exact_cases={cases} boots=3 points={points.Length} paths={paths} truncated={truncated} explore_ms={explorationMs} explore_bytes={explorationBytes} targets={targets} collect_ms={timer.ElapsedMilliseconds} collect_bytes={GC.GetAllocatedBytesForCurrentThread() - bytes} live=unchanged");
    }

    private static int ValidateSmallMap(RunState run, WingedBoots boots)
    {
        var map = new FixtureMap();
        run.Map = map;
        run.ClearVisitedMapCoordsDebug();
        run.AddVisitedMapCoord(map.StartingMapPoint.coord);
        var points = map.GetAllMapPoints().Append(map.BossMapPoint).ToArray();
        var cases = 0;
        for (var uses = 0; uses <= 4; uses++)
        {
            boots.TimesUsed = 3 - Math.Min(uses, 3);
            run.Modifiers = uses == 4 ? [ModelDb.Modifier<Flight>().ToMutable()] : [];
            foreach (var target in points)
            {
                var expected = ReferencePaths(run, target);
                var actual = RouteStateExplorer.Explore(run, target);
                Require(!actual.WasTruncated && actual.Paths.Select(PathKey).ToHashSet().SetEquals(expected.Select(PathKey)),
                    $"route mismatch with travel uses={uses} target={target.coord}");
                cases++;
            }
        }
        run.Modifiers = [];
        boots.TimesUsed = 0;
        map.GetPoint(0, 1)!.PointType = MapPointType.Monster;
        map.GetPoint(1, 1)!.PointType = MapPointType.Shop;
        map.GetPoint(2, 1)!.PointType = MapPointType.Unknown;
        map.GetPoint(1, 2)!.PointType = MapPointType.Ancient;
        foreach (var target in points.Where(point => point.PointType is MapPointType.Monster or MapPointType.Boss))
        {
            var expected = RouteWorldlinePredictor.Predict(run, target, ReferencePaths(run, target))
                .Where(worldline => !worldline.ResolvedRooms.SkipLast(1).Any(room =>
                    room is MegaCrit.Sts2.Core.Rooms.RoomType.Monster or MegaCrit.Sts2.Core.Rooms.RoomType.Elite
                        or MegaCrit.Sts2.Core.Rooms.RoomType.Boss or MegaCrit.Sts2.Core.Rooms.RoomType.Event))
                .Select(worldline => PathKey(worldline.Path)).ToHashSet();
            var paths = RouteStateExplorer.Explore(run, target, static point =>
                point.PointType is not (MapPointType.Monster or MapPointType.Elite or MapPointType.Boss or MapPointType.Ancient));
            var actual = RouteWorldlinePredictor.Predict(run, target, paths.Paths, firstCombatOnly: true);
            Require(actual.Select(worldline => PathKey(worldline.Path)).ToHashSet().SetEquals(expected),
                "next-combat pruning must retain safe room and unknown-room branches");
            cases++;
        }
        var dense = new FixtureMap(rows: 10);
        run.Map = dense;
        run.ClearVisitedMapCoordsDebug();
        run.AddVisitedMapCoord(dense.StartingMapPoint.coord);
        run.Modifiers = [ModelDb.Modifier<Flight>().ToMutable()];
        var limited = RouteStateExplorer.Explore(run, dense.BossMapPoint);
        Require(limited.Paths.Count == 512 && limited.WasTruncated,
            "dense routes must reach the target within the search budget and report omitted paths");
        return cases;
    }

    private static void ValidateDeferredRewardRefresh(
        PlanningPredictionService.State state, PlanningPredictionService planning)
    {
        var screen = new NMapScreen { _runState = state.Run, IsTravelEnabled = true };
        var panel = new PreCombatForecastPanelControl();
        screen.AddChild(panel);
        try
        {
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < 20; index++)
                panel.Configure(screen, mapChanged: false);
            Require(panel.TargetCollectionCount == 0, "hidden map refresh must not collect candidates");
            var hiddenMs = timer.ElapsedMilliseconds;
            panel.ShowWithoutCalculating();
            Require(panel.TargetCollectionCount == 1 && !panel.IsRunning, "explicit opening must prepare candidates without starting a worker");
            panel.Collapse();
            planning.AddCard(state, state.Player.Deck.Cards[0].Id, upgraded: false);
            panel.Configure(screen, mapChanged: false);
            Require(panel.TargetCollectionCount == 1, "receiving a card must not rebuild a collapsed panel");
            panel.ShowWithoutCalculating();
            Require(panel.TargetCollectionCount == 2, "opening after receiving a card must replace stale candidates");
            panel.Collapse();
            panel.Configure(screen, mapChanged: false);
            panel.ShowWithoutCalculating();
            Require(panel.TargetCollectionCount == 2, "unchanged reopening must reuse candidate preparation");
            panel.Collapse();
            state.Player.Relics.OfType<WingedBoots>().Single().TimesUsed = 1;
            panel.Configure(screen, mapChanged: false);
            Require(panel.TargetCollectionCount == 2, "using travel must not rebuild a collapsed panel");
            panel.ShowWithoutCalculating();
            Require(panel.TargetCollectionCount == 3, "changed travel uses must invalidate cached preparation");
            panel.Collapse();
            state.Player.Relics.OfType<WingedBoots>().Single().TimesUsed = 0;
            Smoke.SmokeReport.Add($"route-reward-refresh PASS: hidden_refreshes=20 hidden_ms={hiddenMs} card_pick=deferred travel_use=deferred changed_open=refreshed unchanged_open=cached worker=idle");
        }
        finally
        {
            panel.Free();
            screen.Free();
        }
    }

    private static List<RoutePath> ReferencePaths(RunState run, MapPoint target)
    {
        var result = new List<RoutePath>();
        var uses = RouteStateExplorer.GetFreeTravelUses(run);
        Visit(run.CurrentMapPoint!, uses, []);
        return result;

        void Visit(MapPoint source, int remaining, IReadOnlyList<RouteStep> prefix)
        {
            if (ReferenceEquals(source, target))
            {
                result.Add(new RoutePath(prefix));
                return;
            }
            if (source.coord.row >= target.coord.row)
                return;
            var available = remaining > 0;
            var next = source.Children.Concat(available ? run.Map.GetPointsInRow(source.coord.row + 1) : []).Distinct();
            foreach (var point in next)
            {
                var used = !source.Children.Contains(point);
                var nextRemaining = used && remaining != int.MaxValue ? remaining - 1 : remaining;
                Visit(point, nextRemaining, [.. prefix, new RouteStep(point, used, available)]);
            }
        }
    }

    private static string PathKey(RoutePath path) => string.Join(";", path.Steps.Select(step =>
        $"{step.Point.coord}:{step.UsedFreeTravel}:{step.FreeTravelWasAvailable}"));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Route performance regression: " + message);
    }

    private sealed class FixtureMap : ActMap
    {
        protected override MapPoint?[,] Grid { get; }
        public override MapPoint StartingMapPoint { get; } = new(0, 0) { PointType = MapPointType.Ancient };
        public override MapPoint BossMapPoint { get; }

        internal FixtureMap(int rows = 4)
        {
            Grid = new MapPoint[3, rows];
            BossMapPoint = new MapPoint(1, rows) { PointType = MapPointType.Boss };
            for (var row = 1; row < rows; row++)
            for (var column = 0; column < 3; column++)
                Grid[column, row] = new MapPoint(column, row)
                    { PointType = row == rows - 1 ? MapPointType.Monster : MapPointType.RestSite };
            StartingMapPoint.AddChildPoint(Grid[0, 1]!);
            foreach (var point in GetAllMapPoints())
            {
                if (point.coord.row == rows - 1)
                    point.AddChildPoint(BossMapPoint);
                else
                {
                    point.AddChildPoint(Grid[point.coord.col, point.coord.row + 1]!);
                    if (point.coord.col == 0)
                        point.AddChildPoint(Grid[1, point.coord.row + 1]!);
                }
            }
        }
    }
}
#endif
