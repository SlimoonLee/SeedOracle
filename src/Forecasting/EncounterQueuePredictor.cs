using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using SeedOracle.Api;

namespace SeedOracle.Forecasting;

internal static class EncounterQueuePredictor
{
    public static Forecast<IReadOnlyList<EncounterModel>> Predict(
        RoomType roomType,
        IReadOnlyList<RouteWorldline> worldlines)
    {
        var matchingRoutes = worldlines
            .Where(worldline => worldline.TargetRoomType == roomType)
            .ToArray();
        if (matchingRoutes.Length == 0)
        {
            return Forecast<IReadOnlyList<EncounterModel>>.Unsupported(
                "No feasible route resolves the target as this combat type.",
                PredictionDependency.EncounterQueue);
        }

        var candidates = matchingRoutes
            .Select(worldline => worldline.TargetEncounter)
            .Where(encounter => encounter is not null)
            .Select(encounter => encounter!)
            .DistinctBy(encounter => encounter.Id)
            .OrderBy(encounter => encounter.Id.Entry, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return Forecast<IReadOnlyList<EncounterModel>>.Unsupported(
                "The generated encounter queue is empty.",
                PredictionDependency.EncounterQueue);
        }

        if (candidates.Length == 1 && matchingRoutes.Length == worldlines.Count)
            return Forecast<IReadOnlyList<EncounterModel>>.Exact(candidates, PredictionDependency.EncounterQueue);

        return Forecast<IReadOnlyList<EncounterModel>>.Branch(
            candidates,
            PredictionDependency.EncounterQueue | PredictionDependency.UnknownMapPoint,
            "Different feasible routes consume different queue entries or resolve the target to a different room type.");
    }
}
