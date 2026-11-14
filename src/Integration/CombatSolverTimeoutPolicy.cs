namespace SeedOracle.Integration;

/// <summary>
/// Combat Solver takes a wall-clock ceiling in <c>[budget + 10s, 180s]</c> and
/// throws the whole request away when it is exceeded: no partial result, and the
/// worker session is invalidated. A slow-but-legitimate evaluation therefore
/// costs far more than the wait it saves, so every request is handed the largest
/// ceiling the API accepts. How deep the solver looks is decided by the search
/// budget, not by this value.
/// </summary>
internal static class CombatSolverTimeoutPolicy
{
    internal const int StartupAllowanceMilliseconds = 10_000;
    internal const int MaximumMilliseconds = 180_000;

    /// <summary>The smallest ceiling the API accepts for this budget.</summary>
    internal static int Floor(int searchBudgetMilliseconds) =>
        Math.Max(StartupAllowanceMilliseconds, searchBudgetMilliseconds + StartupAllowanceMilliseconds);

    /// <summary>Always the API maximum; the floor only guards against absurd budgets.</summary>
    internal static int Choose(int searchBudgetMilliseconds) =>
        Clamp(searchBudgetMilliseconds, MaximumMilliseconds);

    internal static int Clamp(int searchBudgetMilliseconds, int milliseconds) =>
        Math.Clamp(milliseconds,
            Math.Min(Floor(searchBudgetMilliseconds), MaximumMilliseconds),
            MaximumMilliseconds);

    internal static string ExplainTimeout(int limitMilliseconds, bool chinese) =>
        chinese
            ? $"超时：{limitMilliseconds / 1000} 秒上限内没有结果。"
            : $"Timed out after {limitMilliseconds / 1000}s.";
}
