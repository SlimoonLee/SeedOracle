using System.Diagnostics;

namespace SeedOracle.Validation;

// Opt-in measurements used by the isolated regression runner. Release scopes
// are empty and inline away; gameplay does not retain diagnostic histories.
internal static class ForecastWorkProfile
{
#if DEBUG
    private sealed class Sample { internal int Calls; internal long Ticks; internal long Bytes; }
    [ThreadStatic] private static Dictionary<string, Sample>? samples;
    internal static void Start() => samples = new(StringComparer.Ordinal);
    internal static IReadOnlyList<string> Stop(string scenario)
    {
        var result = samples;
        samples = null;
        return result?.OrderByDescending(pair => pair.Value.Ticks).Select(pair =>
            $"work-profile {scenario} stage={pair.Key} calls={pair.Value.Calls} ms={pair.Value.Ticks * 1000.0 / Stopwatch.Frequency:F2} bytes={pair.Value.Bytes}")
            .ToArray() ?? [];
    }
#endif

    internal static Scope Measure(string name) => new(name);
    internal readonly struct Scope : IDisposable
    {
#if DEBUG
        private readonly Sample? sample;
        private readonly long start;
        private readonly long allocated;
#endif
        internal Scope(string name)
        {
#if DEBUG
            if (samples is { } current)
            {
                if (!current.TryGetValue(name, out sample)) current[name] = sample = new Sample();
                start = Stopwatch.GetTimestamp();
                allocated = GC.GetAllocatedBytesForCurrentThread();
            }
#endif
        }
        public void Dispose()
        {
#if DEBUG
            if (sample is null) return;
            sample.Calls++;
            sample.Ticks += Stopwatch.GetTimestamp() - start;
            sample.Bytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
#endif
        }
    }
}
