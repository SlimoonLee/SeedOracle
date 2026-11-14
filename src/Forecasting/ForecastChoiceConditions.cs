namespace SeedOracle.Forecasting;

internal enum ForecastChoiceKind { Other, CardReward, EventOption, EventCard, Route, ShopPurchase }

// Domain describes the actual menu, not just the alternatives the budget happened
// to evaluate. A partial enumeration must never become an unrestricted choice.
internal sealed record ForecastChoiceFact(string Key, string Heading, string Value, string ValueLabel,
    IReadOnlyList<string> Domain, bool Closed = true, ForecastChoiceKind Kind = ForecastChoiceKind.Other,
    int Floor = 0, int Group = 1, int Step = 0, int GroupCount = 1)
{
    // Values independently proved equivalent along this continuation. Keep the
    // real menu (including reroll) so ordinary freedom cannot hide extra actions.
    public IReadOnlyList<string> EquivalentValues { get; init; } = [];
    public ForecastShopChoiceScope? Shop { get; init; }

    // Position-independent identity of the choice itself. Two routes that visit
    // the same event on different floors differ in Key (floor and pages are part
    // of the scope) but not in cause: the option picked is what moved the RNG.
    // Column() folds on CauseKey so those terms can merge across routes, and
    // CauseLabel is what the tooltip shows instead of "4层脑蛭".
    public string? CauseKey { get; init; }
    public string? CauseLabel { get; init; }
}

internal sealed record ForecastChoicePath(string Condition, IReadOnlyList<ForecastChoiceFact> Facts);
