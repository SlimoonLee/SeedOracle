using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal static class RouteStateExplorer
{
    private const int MaximumVisitedStates = 8192;
    private const int MaximumRoutePaths = 512;

    public static RouteExploration Explore(RunState run, MapPoint target)
    {
        var routes = new List<RoutePath>();
        var queue = new Queue<SearchState>();
        var freeTravelUses = GetFreeTravelUses(run);

        if (run.CurrentMapPoint is { } current)
        {
            foreach (var transition in GetTransitions(run, current, freeTravelUses))
            {
                queue.Enqueue(new SearchState(
                    transition.Point,
                    [new RouteStep(
                        transition.Point,
                        transition.UsedFreeTravel,
                        transition.FreeTravelWasAvailable)],
                    transition.RemainingFreeTravelUses));
            }
        }
        else
        {
            queue.Enqueue(new SearchState(
                run.Map.StartingMapPoint,
                [new RouteStep(run.Map.StartingMapPoint, false, false)],
                freeTravelUses));
        }

        var visitedStateCount = 0;
        while (queue.Count > 0
               && visitedStateCount < MaximumVisitedStates
               && routes.Count < MaximumRoutePaths)
        {
            var state = queue.Dequeue();
            visitedStateCount++;
            if (ReferenceEquals(state.Point, target))
            {
                routes.Add(new RoutePath(state.Steps));
                continue;
            }

            foreach (var transition in GetTransitions(run, state.Point, state.RemainingFreeTravelUses))
            {
                var steps = new RouteStep[state.Steps.Count + 1];
                for (var index = 0; index < state.Steps.Count; index++)
                    steps[index] = state.Steps[index];
                steps[^1] = new RouteStep(
                    transition.Point,
                    transition.UsedFreeTravel,
                    transition.FreeTravelWasAvailable);
                queue.Enqueue(new SearchState(
                    transition.Point,
                    steps,
                    transition.RemainingFreeTravelUses));
            }
        }

        var truncated = queue.Count > 0;
        return new RouteExploration(
            routes
                .OrderBy(route => route.Length)
                .ThenBy(route => string.Join(",", route.Steps.Select(step => step.Point.coord.col)))
                .ToArray(),
            truncated);
    }

    private static IEnumerable<Transition> GetTransitions(
        RunState run,
        MapPoint source,
        int remainingFreeTravelUses)
    {
        var structural = source.Children;
        var freeTravelAvailable = remainingFreeTravelUses > 0;
        IEnumerable<MapPoint> candidates = structural;
        if (freeTravelAvailable)
        {
            candidates = candidates.Concat(run.Map.GetPointsInRow(source.coord.row + 1));
        }

        foreach (var point in candidates
                     .Distinct()
                     .OrderBy(point => point.coord.col))
        {
            var usedFreeTravel = !structural.Contains(point);
            var remaining = usedFreeTravel && remainingFreeTravelUses != int.MaxValue
                ? remainingFreeTravelUses - 1
                : remainingFreeTravelUses;
            yield return new Transition(point, usedFreeTravel, freeTravelAvailable, remaining);
        }
    }

    private static int GetFreeTravelUses(RunState run)
    {
        if (run.Modifiers.OfType<Flight>().Any())
            return int.MaxValue;

        return run.Players
            .SelectMany(player => player.Relics)
            .OfType<WingedBoots>()
            .Where(boots => !boots.IsUsedUp)
            .Select(boots => Math.Max(0, 3 - boots.TimesUsed))
            .DefaultIfEmpty(0)
            .Max();
    }

    private sealed record SearchState(
        MapPoint Point,
        IReadOnlyList<RouteStep> Steps,
        int RemainingFreeTravelUses);

    private sealed record Transition(
        MapPoint Point,
        bool UsedFreeTravel,
        bool FreeTravelWasAvailable,
        int RemainingFreeTravelUses);
}
