using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Validation;

internal static class PredictionPurityGuard
{
    public static T Execute<T>(RunState run, string operation, Func<T> prediction)
    {
#if DEBUG
        var before = LiveFingerprint.Capture(run);
        try
        {
            return ExecuteAndVerify(run, operation, prediction, before);
        }
        catch
        {
            var afterFailure = LiveFingerprint.Capture(run);
            var changed = before.DescribeDifference(afterFailure);
            if (changed is not null)
            {
                throw new InvalidOperationException(
                    $"Prediction '{operation}' changed live state before it failed. Changed sections: {changed}.");
            }

            throw;
        }
#else
        return prediction();
#endif
    }

#if DEBUG
    private static T ExecuteAndVerify<T>(
        RunState run,
        string operation,
        Func<T> prediction,
        LiveFingerprint before)
    {
        var result = prediction();
        var after = LiveFingerprint.Capture(run);
        var changed = before.DescribeDifference(after);
        if (changed is not null)
        {
            throw new InvalidOperationException(
                $"Prediction '{operation}' changed live state. Changed sections: {changed}.");
        }

        return result;
    }
#endif
}
