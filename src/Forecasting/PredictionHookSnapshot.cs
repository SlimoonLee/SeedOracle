using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal static class PredictionHookSnapshot
{
    public static IReadOnlyList<AbstractModel> CloneListeners(RunState run) => run
        .IterateHookListeners(null)
        .Select(listener => (AbstractModel)listener.ClonePreservingMutability())
        .ToArray();
}
