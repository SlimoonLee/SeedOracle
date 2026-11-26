#if DEBUG
using SeedOracle.Integration;

namespace SeedOracle.Validation;

/// <summary>
/// The timeout ceiling and the progress wording are pure policy: check them
/// without a worker so a regression shows up in the normal smoke run.
/// </summary>
internal static class CombatSolverProgressRegressionTests
{
    internal static void Run()
    {
        // The API maximum, not a budget-derived guess: a timeout drops the whole
        // result and the worker session, so waiting is always the cheaper side.
        var defaults = CombatSolverTimeoutPolicy.Choose(5_000);
        Require(defaults == CombatSolverTimeoutPolicy.MaximumMilliseconds,
            "every request must use the API maximum, got " + defaults);
        Require(CombatSolverTimeoutPolicy.Choose(20_000) == CombatSolverTimeoutPolicy.MaximumMilliseconds,
            "the largest selectable budget must still use the maximum");
        Require(CombatSolverTimeoutPolicy.Clamp(5_000, 400_000) == 180_000,
            "the API maximum must win over an oversized request");
        Require(CombatSolverTimeoutPolicy.Clamp(5_000, 1_000) == 15_000,
            "the API floor must be budget + 10s");
        Require(CombatSolverTimeoutPolicy.Clamp(175_000, 1_000) == 180_000,
            "an unrepresentable floor must fall back to the maximum instead of throwing");

        var progress = new CombatSolverProgress(CombatSolverPhase.Searching, TimeSpan.FromSeconds(2.4), 180_000,
            1_536L * 1024 * 1024, 4242, 8_000);
        var text = progress.DescribeCompact(true);
        Require(text.Contains("2.4/8") && text.Contains("搜索中") && text.Contains("1.5 GB"),
            "compact progress must show elapsed against the search budget, the phase and resident memory: " + text);
        Require(!text.Contains("180"),
            "the wall-clock ceiling is a safety net, not a progress total: " + text);
        var starting = new CombatSolverProgress(CombatSolverPhase.StartingWorker, TimeSpan.FromSeconds(1), 180_000,
            null, null, 8_000).DescribeCompact(false);
        Require(starting.Contains("starting") && !starting.Contains("/") && !starting.Contains("MB"),
            "a cold start must show plain elapsed time and invent no memory numbers: " + starting);

        var explained = CombatSolverTimeoutPolicy.ExplainTimeout(180_000, true);
        Require(explained.Contains("180"), "a timeout must name the ceiling it hit: " + explained);
        Require(!explained.Contains("下次"), "a maximum-ceiling timeout must not promise a longer retry");

        Smoke.SmokeReport.Add(
            $"combat-solver-progress PASS: ceiling={defaults} clamp_max=180000 clamp_floor=15000 compact={text}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Combat solver progress regression: " + message);
    }
}
#endif
