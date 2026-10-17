using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal static class RouteStateExplorer
{
    private const int MaximumVisitedStates = 8192;
    private const int MaximumRoutePaths = 512;

    public static RouteExploration Explore(
        RunState run, MapPoint target, Func<MapPoint, bool>? canTraverseIntermediate = null)
    {
        var routes = new List<RoutePath>();
        var stack = new Stack<SearchState>();
        var freeTravelUses = GetFreeTravelUses(run);
        var candidates = new Dictionary<(MapPoint Point, bool FreeTravel), Candidate[]>();
        var minimumTravelUses = new Dictionary<MapPoint, int>();

        Candidate[] Candidates(MapPoint source, bool freeTravel)
        {
            var key = (source, freeTravel);
            if (!candidates.TryGetValue(key, out var result))
                candidates[key] = result = GetCandidates(run, source, freeTravel).ToArray();
            return result;
        }

        // Reachability depends on the point and remaining travel uses, not the route prefix.
        int MinimumTravelUses(MapPoint point)
        {
            if (ReferenceEquals(point, target))
                return 0;
            if (point.coord.row >= target.coord.row || canTraverseIntermediate?.Invoke(point) == false)
                return int.MaxValue;
            if (minimumTravelUses.TryGetValue(point, out var cached))
                return cached;
            var minimum = int.MaxValue;
            foreach (var candidate in Candidates(point, freeTravel: true))
            {
                if (candidate.Point.coord.row <= point.coord.row)
                    continue;
                var needed = MinimumTravelUses(candidate.Point);
                if (needed != int.MaxValue)
                    minimum = Math.Min(minimum, needed + (candidate.UsedFreeTravel ? 1 : 0));
            }
            minimumTravelUses[point] = minimum;
            return minimum;
        }

        void Push(MapPoint point, bool usedTravel, bool travelAvailable, int remaining, SearchState? previous)
        {
            var needed = MinimumTravelUses(point);
            if (needed != int.MaxValue && needed <= remaining)
                stack.Push(new SearchState(new RouteStep(point, usedTravel, travelAvailable), previous,
                    remaining, (previous?.Length ?? 0) + 1));
        }

        void PushChildren(MapPoint source, int remaining, SearchState? previous)
        {
            var travelAvailable = remaining > 0;
            var next = Candidates(source, travelAvailable);
            for (var index = next.Length - 1; index >= 0; index--)
            {
                var candidate = next[index];
                if (candidate.Point.coord.row <= source.coord.row)
                    continue;
                var nextRemaining = candidate.UsedFreeTravel && remaining != int.MaxValue ? remaining - 1 : remaining;
                Push(candidate.Point, candidate.UsedFreeTravel, travelAvailable, nextRemaining, previous);
            }
        }

        if (run.CurrentMapPoint is { } current)
            PushChildren(current, freeTravelUses, null);
        else
            Push(run.Map.StartingMapPoint, false, false, freeTravelUses, null);

        var visitedStateCount = 0;
        while (stack.Count > 0
               && visitedStateCount < MaximumVisitedStates
               && routes.Count < MaximumRoutePaths)
        {
            var state = stack.Pop();
            visitedStateCount++;
            if (ReferenceEquals(state.Step.Point, target))
            {
                var steps = new RouteStep[state.Length];
                var cursor = state;
                for (var index = steps.Length - 1; index >= 0; index--)
                {
                    steps[index] = cursor.Step;
                    cursor = cursor.Previous!;
                }
                routes.Add(new RoutePath(steps));
                continue;
            }
            PushChildren(state.Step.Point, state.RemainingFreeTravelUses, state);
        }

        var truncated = stack.Count > 0;
        return new RouteExploration(
            routes
                .OrderBy(route => route.Length)
                .ThenBy(route => string.Join(",", route.Steps.Select(step => step.Point.coord.col)))
                .ToArray(),
            truncated);
    }

    private static IEnumerable<Candidate> GetCandidates(
        RunState run,
        MapPoint source,
        bool freeTravelAvailable)
    {
        var structural = source.Children;
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
            yield return new Candidate(point, usedFreeTravel);
        }
    }

    internal static int GetFreeTravelUses(RunState run)
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
        RouteStep Step,
        SearchState? Previous,
        int RemainingFreeTravelUses,
        int Length);

    private readonly record struct Candidate(
        MapPoint Point,
        bool UsedFreeTravel);
}
