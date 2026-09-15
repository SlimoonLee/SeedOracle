namespace SeedOracle.Data;

internal sealed class SeedOracleSettings
{
    public static SeedOracleSettings Default { get; } = new();

    public bool UseCardArtThumbnails { get; set; }

    public bool ShowMapForecastsOutsidePlan { get; set; } = true;
}
