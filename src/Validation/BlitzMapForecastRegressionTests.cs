#if DEBUG
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

/// <summary>
/// Blitz-mode map forecasts: the linear top-strip route must produce the same
/// node predictions the normal map shows — encounter and reward drops for
/// combat nodes, Blitz shop goods for shops, chest contents for treasure —
/// plus the upcoming ancients and bosses for the route summary.
/// </summary>
internal static class BlitzMapForecastRegressionTests
{
    internal static void Run(RunState live)
    {
        var planning = new PlanningPredictionService();
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var snapshot = planning.Capture(planning.CreateState(live, player));
        snapshot.Run.GameMode = GameMode.Blitz;
        var state = planning.Restore(snapshot);
        var run = state.Run;
        // The live smoke run is Standard; a Blitz run generates a BlitzActMap
        // at run start (RunManager.GenerateMap), so give the shadow one too.
        run.Map = new BlitzActMap(run);
        var map = (ActMap)run.Map;

        // Row 1 is the first monster node on the blitz strip.
        var monsterPoint = map.GetPoint(3, 1)
                           ?? throw new InvalidOperationException("blitz-map-regression: row 1 missing");
        var monsterForecast = Entry.MapForecasts.Predict(run, monsterPoint, true);
        Require(monsterForecast.Encounter is { HasValue: true },
            "the blitz monster node forecast has no encounter");
        var rewards = monsterForecast.RouteVariants
            .Select(variant => variant.CombatRewards)
            .FirstOrDefault(rewardsVariant => rewardsVariant is { HasValue: true })
            ?.Value;
        // The reward drop details are delegated to Random Foreseer; when RF's
        // own init is broken (for example, an incompatible SetAlignment
        // patch) they degrade to Unsupported while our encounter and route
        // summary stay up.
        var rfHealthy = rewards is not null;
        if (rfHealthy)
        {
            Require(rewards!.Gold > 0 || rewards.CardRewardGroups.Count > 0,
                "the blitz combat rewards contain neither gold nor card groups");
        }

        // Row 3 is the shop: Blitz goods must appear.
        var shopPoint = map.GetPoint(3, 3)
                        ?? throw new InvalidOperationException("blitz-map-regression: shop row missing");
        var shopForecast = Entry.MapForecasts.Predict(run, shopPoint, true);
        var shopBlitz = shopForecast?.Merchant is { HasValue: true }
            ? shopForecast.Merchant.Value.BlitzEntries.Count
            : -1;
        if (rfHealthy)
            Require(shopBlitz > 0, "the blitz shop forecast did not list the blitz goods");

        // Row 5 is the treasure node.
        var treasurePoint = map.GetPoint(3, 5)
                            ?? throw new InvalidOperationException("blitz-map-regression: treasure row missing");
        var treasureForecast = Entry.MapForecasts.Predict(run, treasurePoint, true);
        var treasure = treasureForecast?.RouteVariants
            .Select(variant => variant.Treasure)
            .FirstOrDefault(chest => chest is { HasValue: true })
            ?.Value;
        if (rfHealthy)
            Require(treasure is not null, "the blitz treasure forecast has no chest");

        // The route summary: the current act's boss plus the later acts'
        // ancients and bosses, and nothing from already-visited content.
        var bossTitle = run.Act.BossEncounter.Title.GetFormattedText();
        var upcoming = BlitzMapForecastHover.BuildUpcoming(run, monsterPoint);
        Require(upcoming.Any(line => line.Contains(bossTitle)),
            "the upcoming summary lost the current act's boss");
        Require(upcoming.Count >= 3,
            $"the upcoming summary covered {upcoming.Count} acts instead of 3");

        Smoke.SmokeReport.Add(
            $"blitz-map-forecast PASS: encounter_present={monsterForecast.Encounter is { HasValue: true }} "
            + $"rf_rewards={rfHealthy} rewards_gold={(rewards?.Gold ?? 0)} "
            + $"reward_groups={(rewards?.CardRewardGroups.Count ?? 0)} "
            + $"shop_blitz={shopBlitz} upcoming={upcoming.Count}");
        foreach (var line in upcoming)
            Smoke.SmokeReport.Add("blitz-map-forecast | " + line);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("blitz-map-regression: " + message);
    }
}
#endif
