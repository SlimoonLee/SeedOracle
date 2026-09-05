namespace SeedOracle.Smoke;

/// <summary>Collects smoke-test results and flushes them to a file the test
/// driver can parse after the headless run exits.</summary>
internal static class SmokeReport
{
    private static readonly List<string> Lines = [];

    public static void Add(string line)
    {
        lock (Lines)
        {
            Lines.Add(line);
        }

        Entry.Logger.Info("[Report] " + line);
    }

    public static void Flush()
    {
        try
        {
            lock (Lines)
            {
                File.WriteAllLines("seed_oracle_smoke_report.txt", Lines);
            }
        }
        catch (Exception exception)
        {
            Entry.Logger.Error($"[Report] flush failed: {exception}");
        }
    }
}
