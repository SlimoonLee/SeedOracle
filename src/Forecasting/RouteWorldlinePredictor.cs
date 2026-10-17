using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SeedOracle.Validation;

namespace SeedOracle.Forecasting;

internal static class RouteWorldlinePredictor
{
    public static IReadOnlyList<RouteWorldline> Predict(
        RunState run,
        MapPoint target,
        IReadOnlyList<RoutePath> paths,
        bool firstCombatOnly = false,
        Func<bool>? canContinue = null,
        bool forceShadow = false)
    {
        using var workSample = ForecastWorkProfile.Measure("worldlines");
        var results = new List<RouteWorldline>(paths.Count);
        if (paths.Count == 0)
            return results;
        SerializableRun? snapshot = null;
        foreach (var path in paths)
        {
            if (results.Count > 0 && canContinue?.Invoke() == false)
                break;
            var needsShadow = forceShadow || path.Steps.Any(step => step.Point.PointType is MapPointType.Unknown or MapPointType.Ancient);
            if (needsShadow)
                snapshot ??= PlanningPredictionService.CreateSerializableRun(run);
            var prediction = needsShadow
                ? PredictPath(run, target, path, snapshot!, firstCombatOnly)
                : PredictFixedPath(run, target, path, firstCombatOnly);
            if (prediction is { } result)
                results.Add(result);
        }
        return results;
    }

    private static RouteWorldline? PredictFixedPath(RunState run, MapPoint target, RoutePath path, bool firstCombatOnly)
    {
        var rooms = path.Steps.Select(step => ToRoomType(step.Point.PointType)).ToArray();
        var prior = rooms.SkipLast(1).ToArray();
        if (firstCombatOnly && prior.Any(room => room.IsCombatRoom() || room == RoomType.Event))
            return null;
        var type = rooms[^1];
        return new RouteWorldline(path, BuildRouteChoices(run, path), type, null,
            type.IsCombatRoom() ? SelectEncounter(run, target, type,
                prior.Count(room => room == RoomType.Monster), prior.Count(room => room == RoomType.Elite),
                prior.Count(room => room == RoomType.Boss)) : null,
            type == RoomType.Shop ? prior.Count(room => room == RoomType.Shop) + 1 : 0,
            rooms, prior.Any(room => room is not (RoomType.Map or RoomType.Unassigned)));
    }

    private static RouteWorldline? PredictPath(
        RunState run, MapPoint target, RoutePath path, SerializableRun snapshot, bool firstCombatOnly)
    {
        var shadow = PlanningPredictionService.RestoreRun(snapshot);
        var listeners = shadow.IterateHookListeners(null).ToArray();
        var unknowns = new UnknownMapPointSimulation(shadow, listeners);
        var events = new EventQueueSimulation(shadow, listeners);
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
            shadow.ActFloor = point.coord.row + 1;
            var roomType = point.PointType == MapPointType.Unknown
                ? unknowns.Roll(point, previousRoomWasShop)
                : ToRoomType(point.PointType);
            resolvedRooms.Add(roomType);
            var isTarget = index == path.Steps.Count - 1;
            if (!isTarget && firstCombatOnly
                && roomType is RoomType.Monster or RoomType.Elite or RoomType.Boss or RoomType.Event)
                return null;

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

            ModelId? roomId = null;
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
                    roomId = events.Consume()?.Id;
                    break;
            }

            // Native event selection sees prior history, before the target is appended.
            shadow.AppendToMapPointHistory(point.PointType, roomType, roomId);
            shadow.AddVisitedMapCoord(point.coord);

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
