using STS2RitsuLib;
using STS2RitsuLib.Data;
using STS2RitsuLib.Utils.Persistence;

namespace SeedOracle.Data;

internal static class SeedOracleData
{
    public const string SettingsKey = "settings";

    private static readonly ModDataStore Store = ModDataStore.For(Entry.ModId);
    private static bool _isRegistered;

    public static SeedOracleSettings Settings => Store.Get<SeedOracleSettings>(SettingsKey);

    public static void Register()
    {
        if (_isRegistered)
            return;

        using (RitsuLibFramework.BeginModDataRegistration(Entry.ModId))
        {
            Store.Register(
                SettingsKey,
                "settings.json",
                SaveScope.Global,
                () => new SeedOracleSettings(),
                autoCreateIfMissing: true);
        }

        _isRegistered = true;
    }
}
