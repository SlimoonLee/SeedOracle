using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.ValueProps;
using SeedOracle.Integration;

namespace SeedOracle.Smoke;

/// <summary>
/// Unattended headless test loop: launched with `--seed-oracle-smoke`, the
/// mod starts an official AutoSlay run (seeded, logged), instantly kills
/// every combat so runs are fast, audits event-option execution, and quits
/// when the run ends.
/// </summary>
internal static class SmokeRunner
{
    private const string Arg = "--seed-oracle-smoke";

    private static AutoSlayer? _autoSlayer;
    private static bool _combatHandled;
    private static bool _quitQueued;
    private static bool _eventAuditDone;
    private static bool _optionOutOfRange;
    private static int _startFailures;
    private static int _tickFailures;
    private static readonly System.Diagnostics.Stopwatch Uptime = System.Diagnostics.Stopwatch.StartNew();

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
            TickEventAudit();

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

    /// <summary>
    /// Smoke-only combat accelerator: every enemy takes unblockable lethal
    /// damage through the normal command pipeline, so drops, rewards, and RNG
    /// consumption match a real victory without waiting on turns.
    /// </summary>
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
                null!);
        }

        Entry.Logger.Info($"[Smoke] killed {monsters.Length} monsters");
    }

    /// <summary>
    /// P1 audit: once per smoke run, execute EVERY option of EVERY game event
    /// twice on independent shadow runs. Live state must not move (purity
    /// guard) and both executions must agree (determinism). Results land in
    /// the smoke report; failures are logged, never fatal to the run.
    /// </summary>
    private static void TickEventAudit()
    {
        if (_eventAuditDone)
            return;
        var run = RunManager.Instance?.DebugOnlyGetState();
        if (run is null)
            return;
        _eventAuditDone = true;

        if (Entry.RandomForeseer is not RandomForeseerAdapter adapter)
            return;
        var player = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(run)
                     ?? run.Players.FirstOrDefault();
        if (player is null)
            return;

        var ok = 0;
        var degraded = 0;
        var failed = 0;
        var nondeterministic = 0;
        foreach (var canonical in ModelDb.AllEvents)
        {
            if (canonical is AncientEventModel)
                continue;
            var entryName = canonical.GetType().Name;
            Entry.Logger.Info($"[Smoke] event audit {entryName}");
            SmokeReport.Add($"event-audit {entryName}:");

            for (var optionIndex = 0; optionIndex < 8; optionIndex++)
            {
                _optionOutOfRange = false;
                try
                {
                    SeedOracle.Validation.PredictionPurityGuard.Execute(
                        run,
                        $"smoke:event-audit:{entryName}:{optionIndex}",
                        () =>
                        {
                            var first = adapter.ExecuteEventOptionAsync(
                                player, canonical, optionIndex, null).GetAwaiter().GetResult();
                            if (first.OptionOutOfRange)
                            {
                                _optionOutOfRange = true;
                                return 0;
                            }

                            var second = adapter.ExecuteEventOptionAsync(
                                player, canonical, optionIndex, null).GetAwaiter().GetResult();

                            if (!first.Ok || !second.Ok)
                            {
                                SmokeReport.Add(
                                    $"  option {optionIndex} DEGRADED: {first.DenyReason ?? second.DenyReason}");
                                degraded++;
                                return 0;
                            }

                            if (first.GoldDelta != second.GoldDelta
                                || first.HpDelta != second.HpDelta
                                || !first.CardsGained.SequenceEqual(second.CardsGained)
                                || !first.RelicsGained.SequenceEqual(second.RelicsGained)
                                || !first.PotionsGained.SequenceEqual(second.PotionsGained))
                            {
                                SmokeReport.Add(
                                    $"  option {optionIndex} NONDETERMINISTIC: "
                                    + $"gold {first.GoldDelta}/{second.GoldDelta} hp {first.HpDelta}/{second.HpDelta}");
                                nondeterministic++;
                                return 0;
                            }

                            var nextKeys = string.Join("|", first.NextOptions.Select(pair => pair.Item2));
                            SmokeReport.Add(
                                $"  option {optionIndex} OK: gold {first.GoldDelta:+#;-#;0} hp {first.HpDelta:+#;-#;0} "
                                + $"cards[{string.Join(",", first.CardsGained)}] relics[{string.Join(",", first.RelicsGained)}] "
                                + $"potions[{string.Join(",", first.PotionsGained)}] next[{nextKeys}] finished={first.Finished}");
                            ok++;
                            return 0;
                        });
                }
                catch (Exception exception)
                {
                    var root = exception.GetBaseException();
                    SmokeReport.Add(
                        $"  option {optionIndex} PURITY/EXEC FAIL: {root.GetType().Name}: {root.Message}");
                    failed++;
                    Entry.Logger.Error($"[Smoke] event audit {entryName}[{optionIndex}] failed: {root}");
                }

                if (_optionOutOfRange)
                    break;
            }
        }

        SmokeReport.Add(
            $"event-audit SUMMARY: ok={ok} degraded={degraded} nondeterministic={nondeterministic} failed={failed}");
        SmokeReport.Flush();
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
