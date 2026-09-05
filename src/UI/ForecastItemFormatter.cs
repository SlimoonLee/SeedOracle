using SeedOracle.Data;
using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal static class ForecastItemFormatter
{
    private const int CardArtWidth = 72;
    private const int CardArtHeight = 54;

    public static string Format(ForecastItemDetails item) =>
        Format(item, SeedOracleData.Settings.UseCardArtThumbnails);

    internal static string Format(ForecastItemDetails item, bool useCardArtThumbnails)
    {
        if (useCardArtThumbnails
            && item.Kind == ForecastItemKind.Card
            && !string.IsNullOrWhiteSpace(item.ImagePath))
        {
            var rarityMarker = ApplyRarityColor("◆", item.Rarity);
            var image = $"[img={CardArtWidth}x{CardArtHeight}]{item.ImagePath}[/img]";
            var upgrade = item.IsUpgraded ? "[green]+[/green]" : string.Empty;
            return rarityMarker + image + upgrade;
        }

        return ApplyRarityColor(item.Name, item.Rarity);
    }

    internal static string ApplyRarityColor(string text, ForecastItemRarity rarity)
    {
        var color = rarity switch
        {
            ForecastItemRarity.Common => "#FFFFFF",
            ForecastItemRarity.Uncommon => "#64FFFF",
            ForecastItemRarity.Rare or ForecastItemRarity.Ancient => "#FFDA36",
            ForecastItemRarity.Shop => "#F6A94A",
            ForecastItemRarity.Event => "#52D273",
            ForecastItemRarity.Curse => "#E669FF",
            ForecastItemRarity.Quest => "#F46836",
            _ => null
        };
        return color is null ? text : $"[color={color}]{text}[/color]";
    }
}
