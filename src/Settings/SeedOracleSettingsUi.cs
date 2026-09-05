using MegaCrit.Sts2.Core.Localization;
using SeedOracle.Data;
using STS2RitsuLib;
using STS2RitsuLib.Settings;

namespace SeedOracle.Settings;

internal static class SeedOracleSettingsUi
{
    private static readonly IModSettingsValueBinding<bool> UseCardArtThumbnails =
        ModSettingsBindings.WithDefault(
            ModSettingsBindings.Global<SeedOracleSettings, bool>(
                Entry.ModId,
                SeedOracleData.SettingsKey,
                static settings => settings.UseCardArtThumbnails,
                static (settings, value) => settings.UseCardArtThumbnails = value),
            () => SeedOracleSettings.Default.UseCardArtThumbnails);

    public static void Register()
    {
        RitsuLibFramework.RegisterModSettings(Entry.ModId, page => page
            .WithModDisplayName(Text("Seed Oracle / 种子先知", "Seed Oracle"))
            .WithTitle(Text("种子先知设置", "Seed Oracle Settings"))
            .WithDescription(Text(
                "调整地图节点预测的显示方式。",
                "Choose how map-node forecasts are displayed."))
            .AddSection("forecast_display", section => section
                .WithTitle(Text("预测显示", "Forecast display"))
                .WithDescription(Text(
                    "稀有度颜色始终生效；卡图模式只替换卡名，不改变预测与缓存。",
                    "Rarity colors are always used. Card art only replaces card names and does not change prediction or caching."))
                .AddToggle(
                    "use_card_art_thumbnails",
                    Text("使用卡图缩略图", "Use card-art thumbnails"),
                    UseCardArtThumbnails,
                    Text(
                        "在奖励、商店和事件预测中用缩略卡图代替卡名。稀有度由卡图前的彩色菱形表示，升级牌保留“+”。",
                        "Replace card names with small card-art images in reward, shop, and event forecasts. A colored diamond shows rarity, and upgraded cards keep their + marker."))));
    }

    private static ModSettingsText Text(string chinese, string english) =>
        ModSettingsText.Dynamic(() =>
            LocManager.Instance?.Language is "zhs" or "zht" ? chinese : english);
}
