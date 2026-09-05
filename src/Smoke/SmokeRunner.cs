using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.ValueProps;

namespace SeedOracle.Smoke;

/// <summary>
/// Unattended headless test loop: launched with `--seed-oracle-smoke`, the
/// mod starts an official AutoSlay run (seeded, logged), instantly kills
/// every combat so runs are fast, and quits when the run ends. Smoke checks
/// (map forecasts, plan chain, event-execution audits) hook in from the
/// existing map-open patches while the run progresses.
/// </summary>
internal static class SmokeRunner
{
    private const string Arg = "--seed-oracle-smoke";

    private static AutoSlayer? _autoSlayer;
    private static bool _combatHandled;
    private static bool _quitQueued;
    private static readonly System.Diagnostics.Stopwatch Uptime = System.Diagnostics.Stopwatch.StartNew();
    private static int _startFailures;
    private static int _tickFailures;

    public static bool IsRequested => OS.GetCmdlineArgs().Any(argument => argument == Arg);

    public static void Begin()
    {
        Entry.Logger.Info("[Smoke] headless smoke mode requested");
    }

    /// <summary>Called every frame from SeedOracleDispatcher; drives the run.</summary>
    public static void Tick()
    {
        if (!IsRequested)
            return;

        try
        {
            if (_autoSlayer is null)
            {
                // Replicates NGame's official autoslay branch — including its
                // timing: the official flow starts the run only AFTER the
                // main menu is up and saves are initialized.
                if (NGame.Instance is null)
                    return;
                var menu = NGame.Instance.GetTree()?.Root?
                    .GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
                if (menu is null || !menu.IsVisibleInTree())
                    return;
                var seed = CommandLineHelper.GetValue("seed") ?? "SEEDORACLE";
                var logFile = CommandLineHelper.GetValue("log-file")
                              ?? "seed_oracle_autoslay.log";
                _autoSlayer = new AutoSlayer();
                try
                {
                    _autoSlayer.Start(seed, logFile);
                    Entry.Logger.Info($"[Smoke] autoslay started with seed {seed}");
                }
                catch (Exception startFailure)
                {
                    // Log-file contention resolves once the other writer lets
                    // go; retry for a while before giving up.
                    _autoSlayer = null;
                    _startFailures++;
                    if (_startFailures == 1)
                        Entry.Logger.Error($"[Smoke] autoslay start retrying: {startFailure.Message}");
                    if (_startFailures > 600)
                    {
                        Entry.Logger.Error("[Smoke] autoslay start failed 600 frames; quitting");
                        SmokeReport.Flush();
                        NGame.Instance?.GetTree()?.Quit();
                    }
                }

                return;
            }

            TickCombatKill();

            var runOver = !AutoSlayer.IsActive;
            if (!_quitQueued && (runOver || Uptime.Elapsed > TimeSpan.FromMinutes(20)))
            {
                _quitQueued = true;
                Entry.Logger.Info($"[Smoke] autoslay finished (elapsed {Uptime.Elapsed:hh\\:mm\\:ss}); quitting");
                SmokeReport.Flush();
                NGame.Instance?.GetTree()?.Quit();
            }
        }
        catch (Exception exception)
        {
            Entry.Logger.Error($"[Smoke] tick failed: {exception.Message}");
            if (++_tickFailures > 50)
            {
                SmokeReport.Flush();
                NGame.Instance?.GetTree()?.Quit();
            }
        }
    }

    private static void TickCombatKill()
    {
        var combat = CombatManager.Instance;
        if (combat is null || !combat.IsInProgress)
        {
            _combatHandled = false;
            return;
        }

        if (_combatHandled)
            return;
        _combatHandled = true;

        var state = combat.DebugOnlyGetState()
                    ?? throw new InvalidOperationException("combat without state");
        var monsters = state.Enemies
            .Where(creature => !creature.IsDead)
            .ToArray();
        foreach (var monster in monsters)
        {
            _ = CreatureCmd.Damage(
                new ThrowingPlayerChoiceContext(),
                monster,
                99999m,
                ValueProp.Unblockable | ValueProp.Move,
                null);
        }

        Entry.Logger.Info($"[Smoke] killed {monsters.Length} monsters");
    }
}

/// <summary>
/// In smoke mode the menu never offers "abandon run": the harness always
/// plays a fresh run and the player's real in-progress run must not be
/// touched even if save-state says one exists.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "RefreshButtons")]
internal static class SmokeMainMenuPatch
{
    [HarmonyPostfix]
    private static void Postfix(NMainMenu __instance)
    {
        if (!SmokeRunner.IsRequested)
            return;
        __instance.GetNodeOrNull<Control>("MainMenuTextButtons/AbandonRunButton")?.Hide();
        __instance.GetNodeOrNull<Control>("MainMenuTextButtons/ContinueButton")?.Hide();
    }
}
