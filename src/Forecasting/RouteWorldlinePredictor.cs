using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal static class RouteWorldlinePredictor
{
    public static IReadOnlyList<RouteWorldline> Predict(
        RunState run,
        MapPoint target,
        IReadOnlyList<RoutePath> paths)
    {
        var results = new List<RouteWorldline>(paths.Count);
        foreach (var path in paths)
            results.Add(PredictPath(run, target, path));
        return results;
    }

    private static RouteWorldline PredictPath(RunState run, MapPoint target, RoutePath path)
    {
        var listeners = PredictionHookSnapshot.CloneListeners(run);
        var unknowns = new UnknownMapPointSimulation(run, listeners);
        var events = new EventQueueSimulation(run, listeners);
        var normalEncountersBefore = 0;
        var eliteEncountersBefore = 0;
        var bossEncountersBefore = 0;
        var shopsBefore = 0;
        var resolvedRooms = new List<RoomType>(path.Steps.Count);
        var previousRoomWasShop = run.CurrentMapPointHistoryEntry?.HasRoomOfType(RoomType.Shop) == true;
        var hasUnmodeledStateDependency = false;

        for (var index = 0; index < path.Steps.Count; index++)
        {
            var point = path.Steps[index].Point;
            var roomType = point.PointType == MapPointType.Unknown
                ? unknowns.Roll(point, previousRoomWasShop)
                : ToRoomType(point.PointType);
            resolvedRooms.Add(roomType);
            var isTarget = index == path.Steps.Count - 1;

            if (isTarget)
            {
                var targetEvent = roomType == RoomType.Event
                    ? point.PointType == MapPointType.Ancient
                        ? run.Act._rooms.Ancient
                        : events.Peek()
                    : null;
                var targetEncounter = roomType is RoomType.Monster or RoomType.Elite or RoomType.Boss
                    ? SelectEncounter(
                        run,
                        target,
                        roomType,
                        normalEncountersBefore,
                        eliteEncountersBefore,
                        bossEncountersBefore)
                    : null;

                return new RouteWorldline(
                    path,
                    BuildRouteChoices(run, path),
                    roomType,
                    targetEvent,
                    targetEncounter,
                    roomType == RoomType.Shop ? shopsBefore + 1 : 0,
                    resolvedRooms.ToArray(),
                    hasUnmodeledStateDependency);
            }

            switch (roomType)
            {
                case RoomType.Monster:
                    normalEncountersBefore++;
                    break;
                case RoomType.Elite:
                    eliteEncountersBefore++;
                    break;
                case RoomType.Boss:
                    bossEncountersBefore++;
                    break;
                case RoomType.Shop:
                    shopsBefore++;
                    break;
                case RoomType.Event when point.PointType != MapPointType.Ancient:
                    _ = events.Consume();
                    break;
            }

            if (roomType is not RoomType.Map and not RoomType.Unassigned)
                hasUnmodeledStateDependency = true;
            previousRoomWasShop = roomType == RoomType.Shop;
        }

        throw new InvalidOperationException($"Route did not reach target {target}.");
    }

    private static EncounterModel? SelectEncounter(
        RunState run,
        MapPoint target,
        RoomType roomType,
        int normalEncountersBefore,
        int eliteEncountersBefore,
        int bossEncountersBefore)
    {
        var rooms = run.Act._rooms;
        return roomType switch
        {
            RoomType.Monster when rooms.normalEncounters.Count > 0 =>
                rooms.normalEncounters[(rooms.normalEncountersVisited + normalEncountersBefore)
                                       % rooms.normalEncounters.Count],
            RoomType.Elite when rooms.eliteEncounters.Count > 0 =>
                rooms.eliteEncounters[(rooms.eliteEncountersVisited + eliteEncountersBefore)
                                      % rooms.eliteEncounters.Count],
            RoomType.Boss when ReferenceEquals(target, run.Map.SecondBossMapPoint) =>
                run.Act.SecondBossEncounter,
            RoomType.Boss when rooms.bossEncountersVisited + bossEncountersBefore > 0
                                   && run.Act.SecondBossEncounter is not null =>
                run.Act.SecondBossEncounter,
            RoomType.Boss => run.Act.BossEncounter,
            _ => null
        };
    }

    private static IReadOnlyList<RouteChoice> BuildRouteChoices(RunState run, RoutePath path)
    {
        if (path.Steps.Count <= 1)
            return [];

        var choices = new List<RouteChoice>(path.Steps.Count - 1);
        MapPoint? previous = run.CurrentMapPoint;
        for (var index = 0; index < path.Steps.Count - 1; index++)
        {
            var step = path.Steps[index];
            var structuralChoiceCount = previous?.Children.Count ?? 1;
            var choiceCount = step.FreeTravelWasAvailable
                ? Math.Max(
                    structuralChoiceCount,
                    run.Map.GetPointsInRow(step.Point.coord.row).Count())
                : structuralChoiceCount;
            var pointsInRow = run.Map.GetPointsInRow(step.Point.coord.row)
                .OrderBy(point => point.coord.col)
                .ToArray();
            var position = Array.FindIndex(pointsInRow, point => ReferenceEquals(point, step.Point)) + 1;
            if (position <= 0)
                position = 1;

            choices.Add(new RouteChoice(
                step.Point,
                step.Point.coord.row + 1,
                position,
                choiceCount > 1,
                step.UsedFreeTravel));
            previous = step.Point;
        }

        return choices;
    }

    private static RoomType ToRoomType(MapPointType pointType) => pointType switch
    {
        MapPointType.Monster => RoomType.Monster,
        MapPointType.Elite => RoomType.Elite,
        MapPointType.Boss => RoomType.Boss,
        MapPointType.Treasure => RoomType.Treasure,
        MapPointType.Shop => RoomType.Shop,
        MapPointType.Ancient => RoomType.Event,
        MapPointType.RestSite => RoomType.RestSite,
        _ => RoomType.Unassigned
    };
}
