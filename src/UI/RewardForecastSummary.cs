using SeedOracle.Forecasting;
using Term = SeedOracle.UI.ForecastChoiceConditionFormatter.Term;

namespace SeedOracle.UI;

internal sealed record RewardOutcomeGroup<T>(T Outcome, IReadOnlyList<Term> Terms);

internal static class RewardForecastSummary
{
    internal static IReadOnlyList<RewardOutcomeGroup<T>> Group<T>(T baseline, string? reference,
        IReadOnlyList<ForecastChoicePath> paths, IReadOnlyList<RewardChoiceBranch<T>> branches, Func<T, string> key)
        where T : class => branches.Prepend(new(reference ?? "", baseline) { ChoicePaths = paths })
        .GroupBy(branch => key(branch.Outcome), StringComparer.Ordinal)
        .Select(group => new RewardOutcomeGroup<T>(group.First().Outcome,
            ForecastChoiceConditionFormatter.Reduce(group.SelectMany(branch =>
                ForecastChoiceConditionFormatter.Paths(branch.Condition, branch.ChoicePaths)))))
        .ToArray();
}
