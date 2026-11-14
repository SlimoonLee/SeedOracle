using Term = SeedOracle.UI.ForecastChoiceConditionFormatter.Term;

namespace SeedOracle.UI;

internal static class ForecastConditionCompression
{
    /// <summary>
    /// A single outcome group already means "every enumerated upstream choice
    /// reaches this same result". Spelling the Cartesian product out costs a
    /// dozen tooltip lines and tells the player nothing they can act on, so past
    /// this length the enumeration is replaced by one counted sentence.
    /// </summary>
    internal const int MaximumEnumeratedLength = 72;

    internal static string? Summarize(IReadOnlyList<Term> terms, int caseCount, bool chinese)
    {
        if (caseCount < 2 || terms.Count == 0)
            return null;
        if (ForecastChoiceConditionFormatter.Format(terms, chinese).Length <= MaximumEnumeratedLength)
            return null;
        return chinese
            ? $"（{caseCount} 种前期选法结果相同）"
            : $" ({caseCount} upstream combinations, same result)";
    }
}
