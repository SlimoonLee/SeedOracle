using MegaCrit.Sts2.Core.Localization;
using SeedOracle.Data;
using SeedOracle.UI;
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

    private static readonly IModSettingsValueBinding<bool> ShowMapForecastsOutsidePlan =
        ModSettingsBindings.WithDefault(
            ModSettingsBindings.Global<SeedOracleSettings, bool>(
                Entry.ModId,
                SeedOracleData.SettingsKey,
                static settings => settings.ShowMapForecastsOutsidePlan,
                static (settings, value) =>
                {
                    settings.ShowMapForecastsOutsidePlan = value;
                    SeedOracleDispatcher.Post(MapForecastDisplay.Refresh);
                }),
            () => SeedOracleSettings.Default.ShowMapForecastsOutsidePlan);

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
                    "调整地图预测与卡牌的显示方式，设置会自动保存。",
                    "Customize map forecasts and card presentation. Changes are saved automatically."))
                .AddToggle(
                    "show_map_forecasts_outside_plan",
                    Text("非计划模式：路线与预测浮窗", "Routes and forecast tooltips outside planning"),
                    ShowMapForecastsOutsidePlan,
                    Text(
                        "关闭后，普通模式隐藏种子先知的路线、标记和地图预测浮窗。打开计划面板后仍显示规划信息。",
                        "Hide Seed Oracle's routes, markers, and map forecast tooltips outside planning. Opening the plan panel still shows planning information."))
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
