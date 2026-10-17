using System.Text.Json;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using SeedOracle.Data;
using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal static class ForecastItemFormatter
{
    private const int CardArtWidth = 72;
    private const int CardArtHeight = 54;
    private static readonly ConditionalWeakTable<ForecastItemDetails, CardCaption> CardCaptions = new();

    private sealed class CardCaption(ForecastItemDetails item)
    {
        private readonly SerializableCard? card = JsonSerializer.Deserialize(item.StateKey!, JsonSerializationUtility.GetTypeInfo<SerializableCard>());
        private string? language;
        private string? caption;
        internal string Get()
        {
            var currentLanguage = LocManager.Instance?.Language;
            if (caption is null || currentLanguage != language)
            {
                caption = card?.Enchantment is { } enchantment
                    ? $" ({EnchantmentModel.FromSerializable(enchantment).Title.GetFormattedText()} {enchantment.Amount})" : "";
                language = currentLanguage;
            }
            return caption;
        }
    }

    public static string Format(ForecastItemDetails item) =>
        Format(item, SeedOracleData.Settings.UseCardArtThumbnails);

    internal static string Format(ForecastItemDetails item, bool useCardArtThumbnails)
    {
        var caption = item.Kind == ForecastItemKind.Card && item.StateKey?.StartsWith('{') == true
            ? CardCaptions.GetValue(item, static card => new(card)).Get() : "";
        if (useCardArtThumbnails
            && item.Kind == ForecastItemKind.Card
            && !string.IsNullOrWhiteSpace(item.ImagePath))
        {
            var rarityMarker = ApplyRarityColor("◆", item.Rarity);
            var image = $"[img={CardArtWidth}x{CardArtHeight}]{item.ImagePath}[/img]";
            var upgrade = item.IsUpgraded ? "[green]+[/green]" : string.Empty;
            return rarityMarker + image + upgrade + caption;
        }

        var name = item.IsUpgraded && !item.Name.Contains('+') ? item.Name + "+" : item.Name;
        return ApplyRarityColor(name, item.Rarity) + caption;
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
