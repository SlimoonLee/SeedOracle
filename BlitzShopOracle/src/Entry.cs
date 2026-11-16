using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using BlitzShopOracle.Data;
using BlitzShopOracle.UI;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace BlitzShopOracle;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public const string ModId = "BlitzShopOracle";
    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        // Seed Oracle contains the same panel. Avoid two copies if a tester
        // leaves the full mod enabled while trying this standalone build.
        if (AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => string.Equals(assembly.GetName().Name, "SeedOracle", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.Info("Seed Oracle is loaded; standalone Blitz Shop Oracle stays disabled to avoid duplicate patches.");
            return;
        }

        var harmony = new Harmony($"{ModId}.Harmony");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        if (NGame.Instance is { } host)
            BlitzShopOracleDispatcher.Ensure(host);
        Logger.Info("Blitz Shop Oracle initialized without Combat Solver or Random Foreseer.");
    }
}
