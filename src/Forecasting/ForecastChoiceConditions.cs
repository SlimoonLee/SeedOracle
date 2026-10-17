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
}

internal sealed record ForecastChoicePath(string Condition, IReadOnlyList<ForecastChoiceFact> Facts);
