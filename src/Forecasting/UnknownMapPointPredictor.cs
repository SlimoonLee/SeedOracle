using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;

namespace SeedOracle.Forecasting;

internal static class UnknownMapPointPredictor
{
    public static Forecast<RoomType> PredictNext(RunState run, MapPoint point)
    {
        var dependencies = PredictionDependency.UnknownMapPoint | PredictionDependency.EventState;
        var previousUnknowns = run.MapPointHistory
            .SelectMany(entries => entries)
            .Count(entry => entry.MapPointType == MapPointType.Unknown);
        var simulation = new UnknownMapPointSimulation(run, PredictionHookSnapshot.CloneListeners(run));
        var result = simulation.Roll(
            point,
            run.CurrentMapPointHistoryEntry?.HasRoomOfType(RoomType.Shop) == true);

        if (run.UnlockState.NumberOfRuns == 0 && previousUnknowns <= 2)
            return Forecast<RoomType>.Exact(result, dependencies);

        return Forecast<RoomType>.CurrentWorldline(
            result,
            dependencies,
            "Uses a cloned UnknownMapPoint RNG, odds state, and hook-listener snapshot.");
    }
}
