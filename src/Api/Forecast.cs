namespace SeedOracle.Api;

public enum ForecastAccuracy
{
    Exact,
    ExactForCurrentWorldline,
    BranchDependent,
    Approximate,
    Unsupported
}

[Flags]
public enum PredictionDependency : ulong
{
    None = 0,
    EncounterQueue = 1UL << 0,
    RunSeed = 1UL << 1,
    Floor = 1UL << 2,
    UnknownMapPoint = 1UL << 3,
    Shops = 1UL << 4,
    Rewards = 1UL << 5,
    Transformations = 1UL << 6,
    Niche = 1UL << 7,
    Shuffle = 1UL << 8,
    MonsterAi = 1UL << 9,
    TreasureRelics = 1UL << 10,
    RelicGrabBag = 1UL << 11,
    CardRarityOdds = 1UL << 12,
    PlayerState = 1UL << 13,
    EventState = 1UL << 14
}

public sealed record Forecast<T>
{
    private Forecast(
        T? value,
        bool hasValue,
        ForecastAccuracy accuracy,
        PredictionDependency dependencies,
        string? reason)
    {
        Value = value;
        HasValue = hasValue;
        Accuracy = accuracy;
        Dependencies = dependencies;
        Reason = reason;
    }

    public T? Value { get; }

    public bool HasValue { get; }

    public ForecastAccuracy Accuracy { get; }

    public PredictionDependency Dependencies { get; }

    public string? Reason { get; }

    public static Forecast<T> Exact(T value, PredictionDependency dependencies = PredictionDependency.None) =>
        new(value, true, ForecastAccuracy.Exact, dependencies, null);

    public static Forecast<T> CurrentWorldline(
        T value,
        PredictionDependency dependencies,
        string? reason = null) =>
        new(value, true, ForecastAccuracy.ExactForCurrentWorldline, dependencies, reason);

    public static Forecast<T> Branch(
        T? value,
        PredictionDependency dependencies,
        string reason) =>
        new(value, true, ForecastAccuracy.BranchDependent, dependencies, reason);

    public static Forecast<T> BranchWithoutValue(
        PredictionDependency dependencies,
        string reason) =>
        new(default, false, ForecastAccuracy.BranchDependent, dependencies, reason);

    public static Forecast<T> Unsupported(string reason, PredictionDependency dependencies = PredictionDependency.None) =>
        new(default, false, ForecastAccuracy.Unsupported, dependencies, reason);
}
