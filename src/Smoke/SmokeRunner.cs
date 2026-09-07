using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.ValueProps;
using SeedOracle.Forecasting;
using SeedOracle.UI;
using SeedOracle.Integration;
using SeedOracle.Validation;

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
    private static PlanningSimulationAuditSession? _planningSimulationAudit;
    private static TaskCompletionSource? _planningSimulationGate;
    private static bool _planningSimulationReported;
    private static LiveFingerprint? _planningSimulationLiveBefore;
    private static int _startFailures;
    private static int _tickFailures;
    private static int _mapNavigationBypass;
    private static readonly System.Diagnostics.Stopwatch Uptime = System.Diagnostics.Stopwatch.StartNew();

    public static bool IsRequested => OS.GetCmdlineArgs().Any(argument => argument == Arg);
    private static bool PlanningAuditOnly => CommandLineHelper.HasArg("seed-oracle-planning-audit");

    internal static bool ShouldHoldAutoSlay =>
        IsRequested
        && !_planningSimulationReported;

    internal static Task WaitForPlanningSimulationAsync(CancellationToken cancellationToken)
    {
        _planningSimulationGate ??= new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _planningSimulationGate.Task.WaitAsync(cancellationToken);
    }

    internal static void AllowOneMapNavigation() =>
        Interlocked.Exchange(ref _mapNavigationBypass, 1);

    internal static bool ConsumeMapNavigationBypass() =>
        Interlocked.Exchange(ref _mapNavigationBypass, 0) == 1;

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
            TickPlanningSimulationAudit();
            if (PlanningAuditOnly && _eventAuditDone
                && (!Entry.CombatSolver.SupportsPlanningSimulation || _planningSimulationReported))
            {
                SmokeReport.Flush();
                NGame.Instance?.GetTree()?.Quit();
                return;
            }

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
        if (_eventAuditDone || _planningSimulationGate is null
            || NMapScreen.Instance is not { IsOpen: true, IsTravelEnabled: true })
            return;
        var run = RunManager.Instance?.DebugOnlyGetState();
        if (run is null || run.CurrentMapPointHistoryEntry is null
                        || CombatManager.Instance.IsInProgress)
            return;
        _eventAuditDone = true;

        var player = MegaCrit.Sts2.Core.Context.LocalContext.GetMe(run)
                     ?? run.Players.FirstOrDefault();
        if (player is null)
            return;

        AuditPlanningPotionRoundTrip(run, player);
        PredictionPurityGuard.Execute(run, "smoke:native-combat-reward-groups", () =>
        {
            AuditNativeCombatRewardGroups(run, player);
            return 0;
        });
        if (Entry.CombatSolver.SupportsPlanningSimulation)
        {
            _planningSimulationLiveBefore = LiveFingerprint.Capture(run);
            try
            {
                _planningSimulationAudit = StartPlanningSimulationAudit(run, player);
            }
            catch (Exception exception)
            {
                // Keep planning-audit mode observable when route setup fails.
                _planningSimulationAudit = PlanningSimulationAuditSession.Failed(exception);
            }
        }
        else
        {
            _planningSimulationReported = true;
            _planningSimulationGate.TrySetResult();
        }

        var ok = 0;
        var degraded = 0;
        var failed = 0;
        var nondeterministic = 0;
        var planner = new PlanningEventPredictionService();
        foreach (var canonical in ModelDb.AllEvents)
        {
            if (canonical is AncientEventModel)
                continue;
            var entryName = canonical.GetType().Name;
            Entry.Logger.Info($"[Smoke] event audit {entryName}");
            SmokeReport.Add($"event-audit {entryName}:");

            IReadOnlyList<PlanningEventPredictionService.EventOptionDescriptor> options;
            try
            {
                options = PredictionPurityGuard.Execute(run, $"smoke:enumerate:{entryName}",
                    () => planner.EnumerateEventOptions(player, canonical));
            }
            catch (Exception exception)
            {
                var root = exception.GetBaseException();
                SmokeReport.Add($"  ENUMERATION FAIL: {root.GetType().Name}: {root.Message}");
                failed++;
                continue;
            }

            if (options.Count == 0)
            {
                SmokeReport.Add("  ENUMERATION EMPTY");
                degraded++;
            }
            foreach (var descriptor in options)
            {
                var optionIndex = descriptor.Index;
                SmokeReport.Add($"  option {optionIndex}: {descriptor.TextKey}; "
                    + $"kind={descriptor.Capability.Kind}; locked={descriptor.IsLocked}; "
                    + $"requests={string.Join(',', descriptor.CardSelections.Select(s => $"{s.MinSelect}..{s.MaxSelect}/{s.Candidates.Count}"))}");
                var picks = descriptor.CardSelections
                    .SelectMany(selection => selection.Candidates
                        .Take(selection.MinSelect)
                        .Select((candidate, order) => new EventCardPick(
                            selection.SelectionStep,
                            candidate.CardId,
                            candidate.DeckSlot,
                            order)))
                    .ToArray();
                try
                {
                    SeedOracle.Validation.PredictionPurityGuard.Execute(
                        run,
                        $"smoke:event-audit:{entryName}:{optionIndex}",
                        () =>
                        {
                            var first = planner.ExecuteEventOptionAsync(
                                player,
                                canonical,
                                optionIndex,
                                picks,
                                plannedDescriptor: descriptor,
                                takeRewards: descriptor.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice ? true : null).GetAwaiter().GetResult();
                            if (first.OptionOutOfRange)
                            {
                                return 0;
                            }

                            var second = planner.ExecuteEventOptionAsync(
                                player,
                                canonical,
                                optionIndex,
                                picks,
                                plannedDescriptor: descriptor,
                                takeRewards: descriptor.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice ? true : null).GetAwaiter().GetResult();

                            if (!first.Ok || !second.Ok)
                            {
                                SmokeReport.Add(
                                    $"  option {optionIndex} DEGRADED: {first.DenyReason ?? second.DenyReason}");
                                if (!descriptor.IsLocked && descriptor.Capability.Kind is
                                    PlanningEventPredictionService.EventPlanningKind.Exact or PlanningEventPredictionService.EventPlanningKind.RewardChoice)
                                    failed++;
                                else
                                    degraded++;
                                return 0;
                            }

                            var stateDifference = first.ShadowRun is not null && second.ShadowRun is not null
                                ? LiveFingerprint.Capture(first.ShadowRun).DescribeDifference(LiveFingerprint.Capture(second.ShadowRun))
                                : "missing shadow state";
                            if (stateDifference is not null || first.Finished != second.Finished
                                || !first.NextOptions.SequenceEqual(second.NextOptions)
                                || first.GoldDelta != second.GoldDelta
                                || first.HpDelta != second.HpDelta
                                || !first.CardsGained.SequenceEqual(second.CardsGained)
                                || !first.RelicsGained.SequenceEqual(second.RelicsGained)
                                || !first.PotionsGained.SequenceEqual(second.PotionsGained))
                            {
                                SmokeReport.Add(
                                    $"  option {optionIndex} NONDETERMINISTIC: "
                                    + $"gold {first.GoldDelta}/{second.GoldDelta} hp {first.HpDelta}/{second.HpDelta}; state={stateDifference}");
                                nondeterministic++;
                                return 0;
                            }

                            var nextKeys = string.Join("|", first.NextOptions.Select(pair => pair.Item2));
                            SmokeReport.Add(
                                $"  option {optionIndex} OK: gold {first.GoldDelta:+#;-#;0} hp {first.HpDelta:+#;-#;0} "
                                + $"cards[{string.Join(",", first.CardsGained)}] relics[{string.Join(",", first.RelicsGained)}] "
                                + $"potions[{string.Join(",", first.PotionsGained)}] next[{nextKeys}] finished={first.Finished}");
                            ok++;
                            if (descriptor.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice)
                            {
                                var skipped = planner.ExecuteEventOptionAsync(player, canonical, optionIndex, picks,
                                    plannedDescriptor: descriptor, takeRewards: false).GetAwaiter().GetResult();
                                if (!skipped.Ok)
                                    throw new InvalidOperationException($"reward skip failed: {skipped.DenyReason}");
                                SmokeReport.Add($"  option {optionIndex} REWARD-SKIP PASS");
                            }
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

            }
            SmokeReport.Flush();
        }

        AuditEventContinuation(run, player, planner);
        SmokeReport.Add(
            $"event-audit SUMMARY: ok={ok} degraded={degraded} nondeterministic={nondeterministic} failed={failed}");
        SmokeReport.Flush();
    }

    private static void TickPlanningSimulationAudit()
    {
        if (_planningSimulationAudit is null || _planningSimulationReported)
            return;

        if (_planningSimulationAudit.Failure is { } setupFailure)
        {
            _planningSimulationReported = true;
            ReportPlanningSimulationFailure(setupFailure);
            return;
        }

        var pending = _planningSimulationAudit.Pending;
        if (pending is null || !pending.IsCompleted)
            return;

        try
        {
            _planningSimulationAudit.Results.Add(pending.GetAwaiter().GetResult());
            if (_planningSimulationAudit.Results.Count < PlanningSimulationAuditSession.SampleCount)
            {
                // SimulatePlanningAsync captures Godot state synchronously at
                // entry. Start each next request from a dispatcher frame so
                // its main-thread requirement remains true after an await.
                StartPlanningSimulationSample(
                    _planningSimulationAudit,
                    _planningSimulationAudit.Results.Count);
                return;
            }

            _planningSimulationReported = true;
            var failures = _planningSimulationAudit.Results.Count(result => !result.IsSuccess);
            var successes = _planningSimulationAudit.Results.Count - failures;
            var liveDifference = _planningSimulationLiveBefore?.DescribeDifference(
                LiveFingerprint.Capture(RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("live run ended")));
            if (liveDifference is not null || failures > 0)
                throw new InvalidOperationException(
                    $"planning simulation audit failed: successes={successes}, failures={failures}, "
                    + $"live={liveDifference ?? "unchanged"}; "
                    + string.Join(" | ", _planningSimulationAudit.Results.Select((result, index) =>
                        $"#{index + 1}:{result.Status}:{result.Error ?? "-"}")));
            var planning = new PlanningPredictionService();
            foreach (var (result, index) in _planningSimulationAudit.Results.Select((result, index) => (result, index)))
            {
                if (result.Confidence != "Complete" || result.CombatEndedTurn is null
                    || result.ProjectedHpLoss is not { } hpLoss || result.FinalHp is not > 0)
                    throw new InvalidOperationException($"sample {index + 1} did not finish a victorious combat: {result.Confidence}");
                var snapshot = _planningSimulationAudit.PlannedState;
                var state = planning.Restore(snapshot);
                var reference = new CombatSimulationReference(
                    PlanningSimulationAuditSession.SampleCount, index, 0x504C_414E_0000_0000UL + (uint)index,
                    hpLoss, result.FinalHp,
                    result.PotionUses.Select(use => new CombatSimulationPotionUse(use.Id, use.Title, use.Turn, use.Slot)).ToArray(),
                    _planningSimulationAudit.Encounter.Id.Entry, PlanningPredictionService.StateToken(snapshot),
                    _planningSimulationAudit.RoomType, _planningSimulationAudit.TargetActFloor, _planningSimulationAudit.TargetMapColumn);
                if (!planning.ApplyCombatSimulationReference(state, snapshot, reference, _planningSimulationAudit.Encounter, _planningSimulationAudit.RoomType))
                    throw new InvalidOperationException($"sample {index + 1} could not be written back to its planning state");
                var restored = planning.Restore(planning.Capture(state));
                if (restored.Player.Creature.CurrentHp != result.FinalHp
                    || restored.Player.Potions.Count() != planning.Restore(snapshot).Player.Potions.Count() - result.PotionUses.Count)
                    throw new InvalidOperationException($"sample {index + 1} lost its HP/potion reference on the next planning snapshot");
                if (planning.ApplyCombatSimulationReference(planning.Restore(snapshot), snapshot,
                        reference with { StateToken = "stale" }, _planningSimulationAudit.Encounter, _planningSimulationAudit.RoomType))
                    throw new InvalidOperationException("a stale combat reference was accepted");
                if (_planningSimulationAudit.Event is { } eventCase)
                {
                    var service = new PlanningEventPredictionService();
                    var first = service.ExecuteEventOptionAsync(
                        _planningSimulationAudit.LiveRun.Players[0], eventCase.Event, eventCase.Option.Index,
                        eventCase.Picks, eventCase.BeforeEvent, eventCase.Option, eventCase.PreviousSteps,
                        simulationReference: reference, combatRewards: new RoutePlanChoice.Combat([], null, true)).GetAwaiter().GetResult();
                    var second = service.ExecuteEventOptionAsync(
                        _planningSimulationAudit.LiveRun.Players[0], eventCase.Event, eventCase.Option.Index,
                        eventCase.Picks, eventCase.BeforeEvent, eventCase.Option, eventCase.PreviousSteps,
                        simulationReference: reference, combatRewards: new RoutePlanChoice.Combat([], null, true)).GetAwaiter().GetResult();
                    if (!first.Ok || !second.Ok || first.CombatRewards is null || first.ShadowRun is null || second.ShadowRun is null)
                        throw new InvalidOperationException($"event combat reference failed: {first.DenyReason ?? second.DenyReason}");
                    var difference = LiveFingerprint.Capture(first.ShadowRun).DescribeDifference(LiveFingerprint.Capture(second.ShadowRun));
                    if (difference is not null)
                        throw new InvalidOperationException($"event combat reward/continuation replay diverged: {difference}");
                    SmokeReport.Add($"planning-event-combat PASS: {eventCase.Event.Id.Entry}; sample={index + 1}; "
                        + $"previous-pages={eventCase.PreviousSteps.Count}; resumed={first.CombatShouldResumeAfterCombat}; "
                        + $"finished={first.Finished}; rewards={first.CombatRewards.CardRewardGroups.Count} card groups, "
                        + $"{first.CombatRewards.Potions.Count} potions, {first.CombatRewards.Relics.Count} relics; state-replay=equal");
                }
            }
            if (_planningSimulationLiveBefore?.DescribeDifference(LiveFingerprint.Capture(_planningSimulationAudit.LiveRun)) is { } writebackDifference)
                throw new InvalidOperationException($"reference writeback changed live state: {writebackDifference}");
            SmokeReport.Add(
                $"planning-combat-simulation PASS: samples={_planningSimulationAudit.Results.Count}; "
                + $"case={CommandLineHelper.GetValue("simulation-case") ?? "normal"}; encounter={_planningSimulationAudit.Encounter.Id.Entry}; "
                + $"statuses={string.Join(',', _planningSimulationAudit.Results.Select(result => result.Status))}; "
                + $"planned-hp={_planningSimulationAudit.PlannedHp}; "
                + $"final-hp={string.Join(',', _planningSimulationAudit.Results.Select(result => result.FinalHp?.ToString() ?? "?"))}; "
                + "reference-roundtrip=PASS; stale-reference-rejected; live-unchanged");
            _planningSimulationGate?.TrySetResult();
        }
        catch (Exception exception)
        {
            _planningSimulationReported = true;
            ReportPlanningSimulationFailure(exception);
        }
        SmokeReport.Flush();
    }

    private static void ReportPlanningSimulationFailure(Exception exception)
    {
        var root = exception.GetBaseException();
        SmokeReport.Add($"planning-combat-simulation FAIL: {root.GetType().Name}: {root.Message}");
        Entry.Logger.Error($"[Smoke] planning combat simulation audit failed: {root}");
        _planningSimulationGate?.TrySetResult();
        SmokeReport.Flush();
    }

    private sealed class PlanningSimulationAuditSession
    {
        internal const int SampleCount = 3;

        internal required RunState LiveRun { get; init; }
        internal required PlanningPredictionService.StateSnapshot PlannedState { get; init; }
        internal SerializableRun PlannedRun => PlannedState.Run;
        internal required EncounterModel Encounter { get; init; }
        internal required int TargetActFloor { get; init; }
        internal required int TargetMapColumn { get; init; }
        internal required RoomType RoomType { get; init; }
        internal required MapPointType MapPointType { get; init; }
        internal required int PlannedHp { get; init; }
        internal EventCombatAuditCase? Event { get; init; }
        internal Task<CombatSolverForecastResult>? Pending { get; set; }
        internal List<CombatSolverForecastResult> Results { get; } = [];
        internal Exception? Failure { get; init; }

        internal static PlanningSimulationAuditSession Failed(Exception exception) => new()
        {
            Failure = exception,
            LiveRun = null!,
            PlannedState = null!,
            Encounter = null!,
            TargetActFloor = 0,
            TargetMapColumn = 0,
            RoomType = RoomType.Monster,
            MapPointType = MapPointType.Monster,
            PlannedHp = 0,
        };
    }

    private sealed record EventCombatAuditCase(
        EventModel Event,
        PlanningEventPredictionService.EventOptionDescriptor Option,
        IReadOnlyList<EventCardPick> Picks,
        IReadOnlyList<EventPlanStep> PreviousSteps,
        PlanningPredictionService.StateSnapshot BeforeEvent);

    private static PlanningSimulationAuditSession StartPlanningSimulationAudit(
        RunState run,
        Player player)
    {
        var simulationCase = CommandLineHelper.GetValue("simulation-case") ?? "normal";
        var pointType = simulationCase == "normal" ? MapPointType.Monster : MapPointType.Unknown;
        var candidates = run.Map.GetAllMapPoints()
            .Where(candidate => candidate.PointType == pointType)
            .Where(candidate => RouteStateExplorer.Explore(run, candidate).Paths.Count > 0)
            .OrderBy(candidate => candidate.coord.row).ToArray();
        var route = candidates.Select(point => new
            {
                Point = point,
                Encounter = RouteWorldlinePredictor.Predict(run, point, RouteStateExplorer.Explore(run, point).Paths)
                    .Select(worldline => worldline.TargetEncounter)
                    .FirstOrDefault(encounter => encounter?.RoomType == RoomType.Monster)
            }).FirstOrDefault(candidate => simulationCase == "event" || candidate.Encounter is not null)
            ?? throw new InvalidOperationException("planning simulation audit found no reachable target combat");
        var point = route.Point;
        var encounter = route.Encounter;

        var planning = new PlanningPredictionService();
        var plannedState = planning.CreateState(run, player);
        var plannedHp = Math.Max(1, plannedState.Player.Creature.CurrentHp - 2);
        plannedState.Player.Creature.SetCurrentHpInternal(plannedHp);
        var card = ModelDb.AllCards.FirstOrDefault(candidate => candidate.Id.Entry.Length > 0);
        if (card is not null)
            planning.AddCard(plannedState, card.Id, upgraded: false);
        var potion = ModelDb.AllPotions.FirstOrDefault();
        if (potion is not null && plannedState.Player.HasOpenPotionSlots)
            planning.AddPotion(plannedState, potion.Id);
        var snapshot = planning.Capture(plannedState);
        EventCombatAuditCase? eventCase = null;
        if (simulationCase == "event")
        {
            var eventName = CommandLineHelper.GetValue("simulation-event") ?? "DenseVegetation";
            var canonical = ModelDb.AllEvents.Single(model => model.GetType().Name == eventName);
            var service = new PlanningEventPredictionService();
            var paths = new Queue<IReadOnlyList<EventPlanStep>>();
            paths.Enqueue([]);
            for (var visited = 0; paths.TryDequeue(out var path) && visited < 32 && eventCase is null; visited++)
            {
                foreach (var descriptor in service.EnumerateEventOptions(player, canonical, snapshot, previousSteps: path)
                    .Where(option => !option.IsLocked))
                {
                    var picks = descriptor.CardSelections.SelectMany(selection => selection.Candidates.Take(selection.MinSelect)
                        .Select((card, order) => new EventCardPick(selection.SelectionStep, card.CardId, card.DeckSlot, order))).ToArray();
                    if (descriptor.Capability.Kind is not (PlanningEventPredictionService.EventPlanningKind.Exact
                        or PlanningEventPredictionService.EventPlanningKind.RewardChoice
                        or PlanningEventPredictionService.EventPlanningKind.SpecialCombat))
                        continue;
                    var outcome = service.ExecuteEventOptionAsync(player, canonical, descriptor.Index, picks, snapshot,
                        descriptor, path, takeRewards: true).GetAwaiter().GetResult();
                    if (!outcome.Ok)
                        throw new InvalidOperationException($"event combat preparation failed: {outcome.DenyReason}");
                    if (outcome.CombatEncounterId is { } encounterId)
                    {
                        eventCase = new EventCombatAuditCase(canonical, descriptor, picks, path, snapshot);
                        encounter = ModelDb.All.OfType<EncounterModel>().Single(model => model.Id.Entry == encounterId);
                        snapshot = service.CaptureCombatState(outcome);
                        break;
                    }
                    if (!outcome.Finished && path.Count < 7)
                        paths.Enqueue(outcome.CompletedSteps);
                }
            }
            if (eventCase is null)
                throw new InvalidOperationException($"no native combat found within the bounded event audit for {eventName}");
        }
        if (encounter is null)
            throw new InvalidOperationException("planning simulation audit has no encounter");
        var session = new PlanningSimulationAuditSession
        {
            LiveRun = run,
            PlannedState = snapshot,
            Encounter = encounter,
            TargetActFloor = point.coord.row + 1,
            TargetMapColumn = point.coord.col,
            RoomType = encounter.RoomType,
            MapPointType = pointType,
            PlannedHp = planning.Restore(snapshot).Player.Creature.CurrentHp,
            Event = eventCase,
        };
        StartPlanningSimulationSample(session, 0);
        return session;
    }

    private static void StartPlanningSimulationSample(
        PlanningSimulationAuditSession session,
        int sampleIndex)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("planning simulation samples must start on the game main thread");

        session.Pending = Entry.CombatSolver.SimulatePlanningAsync(
            session.LiveRun,
            session.PlannedRun,
            session.Encounter,
            session.TargetActFloor,
            session.TargetMapColumn,
            session.RoomType,
            session.MapPointType,
            0x504C_414E_0000_0000UL + (uint)sampleIndex,
            new CombatSolverForecastOptions(
                SearchBudgetMilliseconds: 2_000,
                OverallTimeoutMilliseconds: 30_000,
                MaxDegreeOfParallelism: null,
                ForceRefresh: true,
                CloseWorkerAfterRequest: false,
                WorkerIdleTimeoutMilliseconds: 120_000));
    }

    private static void AuditPlanningPotionRoundTrip(RunState run, Player player)
    {
        var planning = new PlanningPredictionService();
        var state = planning.CreateState(run, player);
        foreach (var potion in state.Player.Potions.ToArray())
            state.Player.DiscardPotionInternal(potion, silent: true);

        var explosive = ModelDb.AllPotions.FirstOrDefault(potion =>
            potion.GetType().Name.Equals("ExplosiveAmpoule", StringComparison.Ordinal))
                        ?? ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.FirePotion>();
        var strength = ModelDb.AllPotions.FirstOrDefault(potion =>
            potion.GetType().Name.Equals("StrengthPotion", StringComparison.Ordinal));
        if (explosive is null || strength is null)
            throw new InvalidOperationException("planning potion audit could not resolve base-game potion models");

        using var isolation = ShadowIsolation.Enter(state.Player);
        if (!planning.AddPotion(state, explosive.Id))
            throw new InvalidOperationException("planning potion audit could not fill the first slot");
        var beforePickup = planning.Capture(state);
        var chain = new RoutePlanForecastService.PlanChain
        {
            State = state, Deck = [], Outcomes = [], StatesBefore = []
        };
        var service = new RoutePlanForecastService(planning);
        var reward = new RewardItemDetails(ForecastItemDetails.Potion(strength));
        var potionMax = state.Player.MaxPotionCount;
        service.ApplyPotionTakes(chain, state.Player.MaxPotionCount, [reward],
            new RoutePlanChoice.Potion([], null), new());
        if (chain.PotionSlots.Count != 1)
            throw new InvalidOperationException("explicit potion skip was ignored");
        service.ApplyPotionTakes(chain, state.Player.MaxPotionCount, [reward], null, new());

        var restored = planning.Restore(planning.Capture(state));
        var ids = PlanningPredictionService.GetPotionSlotIds(restored);
        if (ids.Count != 2
            || !ids[0].Entry.Equals(explosive.Id.Entry, StringComparison.OrdinalIgnoreCase)
            || !ids[1].Entry.Equals(strength.Id.Entry, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "planning potion audit lost or reordered a potion across capture/restore: "
                + string.Join(",", ids.Select(id => id.Entry)));
        }

        SmokeReport.Add(
            $"planning-potion-roundtrip PASS: {ids[0].Entry},{ids[1].Entry}");
        if (PlanningPredictionService.GetPotionSlotIds(planning.Restore(beforePickup)).Count != 1)
            throw new InvalidOperationException("a later pickup mutated an earlier node snapshot");
        while (chain.PotionSlots.Count < potionMax)
            service.ApplyPotionTakes(chain, potionMax, [reward], null, new());
        var fullPotionCount = chain.PotionSlots.Count;
        service.ApplyPotionTakes(chain, potionMax, [reward], null, new());
        if (chain.PotionSlots.Count != fullPotionCount)
            throw new InvalidOperationException("a full potion belt accepted another potion");
        service.ApplyPotionTakes(chain, state.Player.MaxPotionCount, [reward],
            new RoutePlanChoice.Potion([0, 0], explosive.Id), new());
        if (chain.PotionSlots.Count != potionMax
            || chain.PotionSlots.Any(id => id.Entry.Equals(explosive.Id.Entry, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("potion replacement did not retain two native strength potions");
        if (planning.AddPotion(state, new ModelId("POTION", "INVALID_PLANNING_AUDIT"), strength.Id)
            || chain.PotionSlots.Count != potionMax)
            throw new InvalidOperationException("an invalid potion replacement changed the belt");
        SmokeReport.Add("planning-potion-chain PASS: default pickup, explicit skip, full belt, replacement, earlier snapshot isolation");
    }

    private static void AuditNativeCombatRewardGroups(RunState run, Player player)
    {
        var point = run.Map.GetAllMapPoints()
            .Where(candidate => candidate.PointType == MapPointType.Monster)
            .Where(candidate => RouteStateExplorer.Explore(run, candidate).Paths.Count > 0)
            .OrderBy(candidate => candidate.coord.row)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("native reward audit found no reachable combat point");
        var roomType = RoomType.Monster;
        var encounter = RouteWorldlinePredictor.Predict(
                run,
                point,
                RouteStateExplorer.Explore(run, point).Paths)
            .Select(worldline => worldline.TargetEncounter)
            .FirstOrDefault(candidate => candidate?.RoomType == roomType)
            ?? throw new InvalidOperationException("native reward audit found no route encounter");

        var planning = new PlanningPredictionService();
        var prayerState = planning.CreateState(run, player);
        planning.AddRelic(prayerState, ModelDb.Relic<PrayerWheel>().Id);
        var prayerSnapshot = planning.Capture(prayerState);
        RoutePlanChoice.Combat prayerChoice;
        using (ShadowIsolation.Enter(prayerState.Player))
        {
            using var rewards = planning.GenerateCombatRewards(prayerState, roomType, encounter);
            if (rewards.CardRewardGroups.Count != 2
                || rewards.CardRewardGroups.Any(group => group.Cards.Count != 3))
            {
                throw new InvalidOperationException(
                    $"Prayer Wheel native groups expected 2x3, got {rewards.CardRewardGroups.Count} "
                    + $"[{string.Join(',', rewards.CardRewardGroups.Select(group => group.Cards.Count))}]");
            }
            var firstCard = rewards.CardRewardGroups[0].Cards[0].Id;
            var secondCard = rewards.CardRewardGroups[1].Cards[2].Id;
            prayerChoice = new RoutePlanChoice.Combat([
                new RoutePlanChoice.CardReward(0, [new CardRewardPick(0, firstCard)]),
                new RoutePlanChoice.CardReward(1, [new CardRewardPick(2, secondCard)])
            ], null, true);
            var deckBefore = prayerState.Player.Deck.Cards.Count;
            var selected = RoutePlanForecastService.ApplyCombatRewards(prayerState, rewards, prayerChoice);
            if (selected.AppliedDelta?.Cards != 2
                || !prayerState.Player.Deck.Cards.Skip(deckBefore).Select(card => card.Id)
                    .SequenceEqual(new[] { firstCard, secondCard })
                || prayerState.Run.CurrentMapPointHistoryEntry!.GetEntry(player.NetId).CardChoices
                    .Count(choice => choice.wasPicked) != 2)
                throw new InvalidOperationException("independent native reward groups did not each add their selected card");
        }
        foreach (var invalidPick in new CardRewardPick?[]
                 { null, new(0, new ModelId("CARD", "INVALID_AUDIT_CARD")), new(4) })
        {
            var state = planning.Restore(prayerSnapshot);
            using var rewards = planning.GenerateCombatRewards(state, roomType, encounter);
            var firstStep = invalidPick is null
                ? new CardRewardPick(AlternativeId: rewards.CardRewardGroups[0].Steps[0].Alternatives
                    .Single(option => option.AfterSelected == PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward).OptionId)
                : invalidPick;
            var choice = prayerChoice.WithCardRewardChoice(new RoutePlanChoice.CardReward(0, [firstStep]));
            var selected = RoutePlanForecastService.ApplyCombatRewards(state, rewards, choice);
            if (selected.AppliedDelta?.Cards != 1
                || selected.CardRewardGroups[1].Steps[0].SelectedIndex != 2
                || selected.CardRewardGroups[0].Steps[0].RejectedChoice != (invalidPick is not null))
                throw new InvalidOperationException("skipping or rejecting one reward group changed another group's choice");
        }

        var whiteStarState = planning.CreateState(run, player);
        planning.AddRelic(whiteStarState, ModelDb.Relic<WhiteStar>().Id);
        var elitePoint = run.Map.GetAllMapPoints()
            .Where(candidate => candidate.PointType == MapPointType.Elite)
            .Where(candidate => RouteStateExplorer.Explore(run, candidate).Paths.Count > 0)
            .OrderBy(candidate => candidate.coord.row)
            .FirstOrDefault();
        var eliteEncounter = elitePoint is null
            ? null
            : RouteWorldlinePredictor.Predict(
                run,
                elitePoint,
                RouteStateExplorer.Explore(run, elitePoint).Paths)
            .Select(worldline => worldline.TargetEncounter)
            .FirstOrDefault(candidate => candidate?.RoomType == RoomType.Elite);
        if (eliteEncounter is null)
            throw new InvalidOperationException("native reward audit found no elite encounter");
        using (ShadowIsolation.Enter(whiteStarState.Player))
        {
            using var rewards = planning.GenerateCombatRewards(whiteStarState, RoomType.Elite, eliteEncounter);
            if (rewards.CardRewardGroups.Count != 2
                || rewards.CardRewardGroups[1].Cards.Count != 3
                || rewards.CardRewardGroups[1].Cards.Any(card => card.Item.Rarity != ForecastItemRarity.Rare))
                throw new InvalidOperationException("White Star native extra boss-rarity group was not preserved");
        }

        var candyState = planning.CreateState(run, player);
        planning.AddRelic(candyState, ModelDb.Relic<LastingCandy>().Id);
        planning.AddRelic(candyState, ModelDb.Relic<PrayerWheel>().Id);
        using (ShadowIsolation.Enter(candyState.Player))
        {
            using var first = planning.GenerateCombatRewards(candyState, roomType, encounter);
            using var second = planning.GenerateCombatRewards(candyState, roomType, encounter);
            if (!first.CardRewardGroups.Select(group => group.Cards.Count).SequenceEqual(new[] { 3, 3 })
                || !second.CardRewardGroups.Select(group => group.Cards.Count).SequenceEqual(new[] { 4, 3 })
                || candyState.Player.Relics.OfType<LastingCandy>().Single().CombatRewardsSeen != 2)
            {
                throw new InvalidOperationException(
                    $"Lasting Candy native candidate progression was wrong: "
                    + $"{first.CardRewardGroups.FirstOrDefault()?.Cards.Count}/{second.CardRewardGroups.FirstOrDefault()?.Cards.Count}");
            }
        }

        var paelState = planning.CreateState(run, player);
        planning.AddRelic(paelState, ModelDb.Relic<PaelsWing>().Id);
        using (ShadowIsolation.Enter(paelState.Player))
        {
            using var rewards = planning.GenerateCombatRewards(paelState, roomType, encounter);
            if (!rewards.CardRewardGroups.Any(group => group.Steps[0].Alternatives
                    .Any(option => option.OptionId == PaelsWing.sacrificeAlternativeKey)))
            {
                throw new InvalidOperationException("Pael's Wing native sacrifice alternative was not exposed");
            }

            var pael = paelState.Player.Relics.OfType<PaelsWing>().Single();
            var beforeRelicCount = paelState.Player.Relics.Count;
            var sacrifice = new RoutePlanChoice.CardReward(0,
                [new CardRewardPick(AlternativeId: PaelsWing.sacrificeAlternativeKey)]);
            RoutePlanForecastService.ApplyCombatRewards(paelState, rewards,
                new RoutePlanChoice.Combat([sacrifice], null, TakeRelic: true));
            using var nextRewards = planning.GenerateCombatRewards(paelState, roomType, encounter);
            RoutePlanForecastService.ApplyCombatRewards(paelState, nextRewards,
                new RoutePlanChoice.Combat([sacrifice], null, TakeRelic: true));
            if (pael.RewardsSacrificed != 2 || paelState.Player.Relics.Count <= beforeRelicCount)
                throw new InvalidOperationException("Pael's Wing native sacrifice did not advance its threshold reward");
        }

        var doubleSacrificeState = planning.Restore(prayerSnapshot);
        planning.AddRelic(doubleSacrificeState, ModelDb.Relic<PaelsWing>().Id);
        var beforeSacrifice = planning.Capture(doubleSacrificeState);
        var sacrificeChoice = new RoutePlanChoice.Combat(Enumerable.Range(0, 2)
            .Select(group => new RoutePlanChoice.CardReward(group,
                [new CardRewardPick(AlternativeId: PaelsWing.sacrificeAlternativeKey)])).ToArray(), null, true);
        using (var rewards = planning.GenerateCombatRewards(doubleSacrificeState, roomType, encounter))
        {
            var result = RoutePlanForecastService.ApplyCombatRewards(doubleSacrificeState, rewards, sacrificeChoice);
            if (result.AppliedDelta is not { Cards: 0, Relics: 1 }
                || doubleSacrificeState.Player.Relics.OfType<PaelsWing>().Single().RewardsSacrificed != 2)
                throw new InvalidOperationException("sacrificing two groups in one combat did not grant one relic");
        }
        var restoredSacrifice = planning.Restore(planning.Capture(doubleSacrificeState));
        var replayedSacrifice = planning.Restore(beforeSacrifice);
        using (var rewards = planning.GenerateCombatRewards(replayedSacrifice, roomType, encounter))
            RoutePlanForecastService.ApplyCombatRewards(replayedSacrifice, rewards, sacrificeChoice);
        if (planning.Restore(beforeSacrifice).Player.Relics.OfType<PaelsWing>().Single().RewardsSacrificed != 0
            || restoredSacrifice.Player.Relics.OfType<PaelsWing>().Single().RewardsSacrificed != 2
            || !restoredSacrifice.Player.Relics.Select(relic => relic.Id)
                .SequenceEqual(replayedSacrifice.Player.Relics.Select(relic => relic.Id))
            || restoredSacrifice.Player.PlayerRng.Rewards.ToSerializable().counter
                != replayedSacrifice.Player.PlayerRng.Rewards.ToSerializable().counter)
            throw new InvalidOperationException("sacrifice replay or snapshot changed relics, counters or reward RNG");
        using (var next = planning.GenerateCombatRewards(restoredSacrifice, roomType, encounter))
        using (var repeated = planning.GenerateCombatRewards(replayedSacrifice, roomType, encounter))
        {
            if (RewardSignature(next) != RewardSignature(repeated))
                throw new InvalidOperationException("sacrifice replay changed downstream combat rewards");
        }

        var rerollState = planning.Restore(prayerSnapshot);
        planning.AddRelic(rerollState, ModelDb.Relic<Driftwood>().Id);
        var rerollSnapshot = planning.Capture(rerollState);
        RoutePlanChoice.Combat rerollChoice;
        ModelId rerolledCard;
        using (var rewards = planning.GenerateCombatRewards(rerollState, roomType, encounter))
        {
            var alternative = rewards.CardRewardGroups[0].Steps[0].Alternatives.Single(option =>
                option.AfterSelected == PostAlternateCardRewardAction.DoNothing);
            rerollChoice = new RoutePlanChoice.Combat([
                new RoutePlanChoice.CardReward(0, [new CardRewardPick(AlternativeId: alternative.OptionId)]),
                new RoutePlanChoice.CardReward(1, [new CardRewardPick(0, rewards.CardRewardGroups[1].Cards[0].Id)])
            ], null, true);
            var preview = RoutePlanForecastService.ApplyCombatRewards(rerollState, rewards, rerollChoice);
            if (preview.CardRewardGroups[0].Steps.Count != 2 || preview.AppliedDelta?.Cards != 1)
                throw new InvalidOperationException("reroll did not expose a new choice within its original group");
            rerolledCard = preview.CardRewardGroups[0].Steps[1].Cards[1].Id;
            rerollChoice = rerollChoice.WithCardRewardChoice(new RoutePlanChoice.CardReward(0,
                [rerollChoice.ChoiceForGroup(0).Steps[0], new CardRewardPick(1, rerolledCard)]));
        }
        var rerollReplay = planning.Restore(rerollSnapshot);
        using (var rewards = planning.GenerateCombatRewards(rerollReplay, roomType, encounter))
        {
            var result = RoutePlanForecastService.ApplyCombatRewards(rerollReplay, rewards, rerollChoice);
            if (result.AppliedDelta?.Cards != 2
                || result.CardRewardGroups[0].Steps.Count != 2
                || result.CardRewardGroups[0].Steps[1].SelectedIndex != 1
                || result.CardRewardGroups[0].Steps[1].Alternatives.Any(option =>
                    option.AfterSelected == PostAlternateCardRewardAction.DoNothing)
                || !rerollReplay.Player.Deck.Cards.Any(card => card.Id == rerolledCard))
                throw new InvalidOperationException("reroll-then-pick failed or reroll remained available after use");
        }

        var mapForecast = Entry.RandomForeseer.PredictCombatRewards(prayerState.Player, [roomType], encounter);
        var nativeState = planning.CreateState(prayerState.Run, prayerState.Player);
        using (var expected = planning.GenerateCombatRewards(nativeState, roomType, encounter))
        {
            if (!mapForecast.HasValue || RewardSignature(mapForecast.Value!) != RewardSignature(expected))
                throw new InvalidOperationException("map and planner combat reward entry points disagree");
        }
        var nativeTreasureState = planning.CreateState(candyState.Run, candyState.Player);
        planning.AdvanceRoomsBeforeTarget(nativeTreasureState, [roomType, RoomType.Treasure]);
        var expectedTreasure = planning.GenerateTreasure(nativeTreasureState, isPriorRoom: false);
        var treasureForecast = Entry.RandomForeseer.PredictTreasureRoom(candyState.Player, [roomType, RoomType.Treasure]);
        if (!treasureForecast.HasValue || treasureForecast.Value!.Gold != expectedTreasure.Gold
            || !treasureForecast.Value.Relics.Select(relic => relic.Id)
                .SequenceEqual(expectedTreasure.Relics.Select(relic => relic.Id)))
            throw new InvalidOperationException("map treasure preview did not use native preceding combat state");

        SmokeReport.Add("native-combat-reward-groups PASS: independent picks/skip, stale choices, White Star rarity, Candy+Wheel [4,3], two sacrifices, reroll-then-pick, snapshot/RNG replay, map/planner parity");

        static string RewardSignature(CombatRewardDetails rewards) =>
            $"{rewards.Gold}:" + string.Join("|", rewards.CardRewardGroups.Select(group =>
                string.Join(',', group.Cards.Select(card => $"{card.Id}:{card.Item.IsUpgraded}"))))
            + ":" + string.Join(',', rewards.Potions.Concat(rewards.Relics).Select(item => item.Id));
    }

    private static void AuditEventContinuation(RunState run, Player player, PlanningEventPredictionService planner)
    {
        foreach (var name in new[] { "Trial", "TinkerTime", "BrainLeech" })
        {
            var canonical = ModelDb.AllEvents.First(model => model.GetType().Name == name);
            var pending = new Queue<IReadOnlyList<EventPlanStep>>();
            pending.Enqueue([]);
            var completed = 0;
            var visited = 0;
            while (pending.TryDequeue(out var path) && visited++ < 32)
            {
                var options = PredictionPurityGuard.Execute(run, $"smoke:path-enumerate:{name}",
                    () => planner.EnumerateEventOptions(player, canonical, previousSteps: path));
                foreach (var option in options.Where(option => !option.IsLocked
                    && option.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.Exact))
                {
                    var picks = option.CardSelections.SelectMany(selection => selection.Candidates
                        .TakeLast(selection.MinSelect).Select((card, order) =>
                            new EventCardPick(selection.SelectionStep, card.CardId, card.DeckSlot, order))).ToArray();
                    // Re-probe later grids using the actual earlier card picks.
                    var resolved = planner.EnumerateEventOptions(player, canonical,
                        plannedOptionIndex: option.Index, plannedCardPicks: picks, previousSteps: path)
                        .First(descriptor => descriptor.TextKey == option.TextKey);
                    var result = PredictionPurityGuard.Execute(run, $"smoke:path-execute:{name}",
                        () => planner.ExecuteEventOptionAsync(player, canonical, option.Index, picks,
                            plannedDescriptor: resolved, previousSteps: path).GetAwaiter().GetResult());
                    if (name == "BrainLeech")
                    {
                        var missing = planner.ExecuteEventOptionAsync(player, canonical, option.Index, [],
                            plannedDescriptor: resolved).GetAwaiter().GetResult();
                        var stale = planner.ExecuteEventOptionAsync(player, canonical, option.Index, picks,
                            plannedDescriptor: resolved with { TextKey = "INVALID_AUDIT_KEY" }).GetAwaiter().GetResult();
                        if (missing.Ok || stale.Ok)
                            throw new InvalidOperationException("missing or stale event choices were accepted");
                    }
                    if (!result.Ok)
                        throw new InvalidOperationException($"event continuation {name}/{option.TextKey}: {result.DenyReason}");
                    if (result.Finished)
                        completed++;
                    else if (path.Count < 7)
                        pending.Enqueue(result.CompletedSteps);
                    else
                        throw new InvalidOperationException($"event continuation {name} exceeded eight steps");
                }
            }
            if (completed == 0 || pending.Count > 0)
                throw new InvalidOperationException($"event continuation {name} did not reach completion");
            SmokeReport.Add($"event-continuation PASS: {name}; terminal paths={completed}; pages={visited}");
        }
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

/// <summary>
/// The planning smoke audit must keep the live run at a stable map until
/// CombatSolver has revalidated its token. This patch only participates in the
/// explicit smoke command-line mode and releases when the audit has
/// recorded either a pass or a concrete failure.
/// </summary>
[HarmonyPatch(typeof(MapScreenHandler), nameof(MapScreenHandler.HandleAsync))]
internal static class SmokeAutoSlayPlanningGatePatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        MapScreenHandler __instance,
        MegaCrit.Sts2.Core.Random.Rng random,
        CancellationToken ct,
        ref Task __result)
    {
        if (SmokeRunner.ConsumeMapNavigationBypass())
            return true;
        if (!SmokeRunner.ShouldHoldAutoSlay)
            return true;

        __result = ResumeNavigationAsync(__instance, random, ct);
        return false;
    }

    private static async Task ResumeNavigationAsync(
        MapScreenHandler handler, MegaCrit.Sts2.Core.Random.Rng random, CancellationToken cancellationToken)
    {
        await SmokeRunner.WaitForPlanningSimulationAsync(cancellationToken).ConfigureAwait(false);
        var resumed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        SeedOracleDispatcher.Post(() =>
        {
            try
            {
                // The completed audit releases the prefix; perform the map
                // navigation we held so full-run smoke can continue normally.
                SmokeRunner.AllowOneMapNavigation();
                resumed.TrySetResult(handler.HandleAsync(random, cancellationToken));
            }
            catch (Exception exception)
            {
                resumed.TrySetException(exception);
            }
        });
        await (await resumed.Task.ConfigureAwait(false)).ConfigureAwait(false);
    }
}
