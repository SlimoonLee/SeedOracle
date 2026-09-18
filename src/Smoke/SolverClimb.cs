using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms;
using MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Smoke;

/// <summary>
/// Solver-guided climb harness for the card-value study. AutoSlay drives a
/// seeded run with its official random handlers for every non-combat decision
/// (random routes, random card takes); the outcome of every upcoming fight is
/// decided by the Combat Solver pre-combat forecast of that exact fight from
/// the live state at map time, the player is healed to full after every
/// combat, and the run stops after the first act's boss. One JSON report per
/// run lands in --climb-output.
/// </summary>
internal static class SolverClimb
{
    private const string Arg = "--seed-oracle-solver-climb";
    private static readonly object Gate = new();
    private static readonly List<ClimbFightRow> Fights = [];
    private static RunState? _routeRun;
    private static System.Random? _routeRandom;
    private static RunState? _mapKeyRun;
    private static int _mapKeyAct;
    private static int _mapKeyVisited;
    private static MapCoord _mapKeyCoord;
    private static NMapPoint? _chosenPoint;
    private static TaskCompletionSource<CombatSolverForecastResult>? _pendingForecast;
    private static bool _combatActive;
    private static bool _combatRecordedByClimb;
    private static bool _bossCombatEnded;
    private static bool _bossReached;
    private static bool _finished;
    private static int _skippedUnknownNodes;
    private static int _unrecordedCombats;
    private static int _potionsDiscarded;
    private static DateTime _startedUtc = DateTime.UtcNow;

    internal static bool IsRequested => OS.GetCmdlineArgs().Any(argument => argument == Arg);
    private static string OutputDirectory =>
        Path.GetFullPath(CommandLineHelper.GetValue("climb-output") ?? "solver-climb");

    /// <summary>Main-thread tick from <see cref="SmokeRunner"/>: full-heals the
    /// player after every combat and stops the run after the act-1 boss.</summary>
    public static void TickCombatEdges()
    {
        if (_finished)
            return;
        var inProgress = CombatManager.Instance is { IsInProgress: true };
        if (inProgress)
        {
            if (_combatActive)
                return;
            _combatActive = true;
            _combatRecordedByClimb = false;
            return;
        }

        if (!_combatActive)
            return;
        _combatActive = false;
        ResetTurnNudge();
        var endedRun = RunManager.Instance?.DebugOnlyGetState();
        var endedPlayer = endedRun is null
            ? null
            : LocalContext.GetMe(endedRun) ?? endedRun.Players.FirstOrDefault();
        if (!_combatRecordedByClimb)
            Interlocked.Increment(ref _unrecordedCombats);
        if (endedPlayer is not null)
            endedPlayer.Creature.SetCurrentHpInternal(endedPlayer.Creature.MaxHp);
    }

    private static void MarkBossCombatEnded()
    {
        _bossCombatEnded = true;
        _bossReached = true;
    }

    private static int _nudgedTurn;

    internal static void ResetTurnNudge() => _nudgedTurn = 0;

    /// <summary>Called each tick while a fight is still live: tanky powers
    /// (Hardened Shell caps per-turn HP loss, Slippery consumed one stack per
    /// hit) can outlast the first kill pass, and nobody plays the player's
    /// turns in this harness. Refill HP and end the turn so combat time keeps
    /// moving until the kill passes finish the job.</summary>
    internal static void NudgeTurnIfStalled(MegaCrit.Sts2.Core.Combat.ICombatState state)
    {
        var run = RunManager.Instance?.DebugOnlyGetState();
        var player = run is null
            ? null
            : LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        if (player?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } combatState)
            return;
        if (combatState.TurnNumber <= _nudgedTurn)
            return;
        _nudgedTurn = combatState.TurnNumber;
        player.Creature.SetCurrentHpInternal(player.Creature.MaxHp);
        MegaCrit.Sts2.Core.Commands.PlayerCmd.EndTurn(player, canBackOut: false);
        Entry.Logger.Info(
            $"[Smoke] solver climb: ended turn {combatState.TurnNumber} to advance a tanky fight");
    }

    private static void FinishFromCombat(RunState run)
    {
        if (_finished || !_bossCombatEnded)
            return;
        _finished = true;
        SeedOracleDispatcher.Post(() =>
        {
            WriteReport(run, final: true);
            SmokeReport.Add(BuildSummaryLine());
            SmokeReport.Flush();
            NGame.Instance?.GetTree()?.Quit(0);
        });
    }

    private static string BuildSummaryLine()
    {
        lock (Gate)
        {
            var parts = Fights
                .GroupBy(row => row.RoomType)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var losses = group.Select(row => row.Loss ?? 0).ToArray();
                    return $"{group.Key}={group.Count()}/avg{Math.Round(losses.Average(), 1)}";
                });
            return "solver-climb SUMMARY: "
                + $"fights={Fights.Count} "
                + string.Join(' ', parts)
                + $" totalLoss={Fights.Sum(row => row.Loss ?? 0)} "
                + $"deaths={Fights.Count(row => row.Death)} "
                + $"unknownSkipped={_skippedUnknownNodes} unrecordedCombats={_unrecordedCombats} "
                + $"boss={(_bossReached ? "reached" : "missing")}";
        }
    }

    // ------------------------------------------------------------------
    // Character pin: AutoSlay picks the character randomly; the study needs
    // the Ironclad starter deck, so redirect its pick.
    // ------------------------------------------------------------------

    [HarmonyPatch(typeof(NCharacterSelectButton), nameof(NCharacterSelectButton.Select))]
    internal static class SolverClimbCharacterPinPatch
    {
        private static bool _redirecting;

        [HarmonyPrefix]
        private static bool Prefix(NCharacterSelectButton __instance)
        {
            if (!SolverClimb.IsRequested || !AutoSlayer.IsActive || _redirecting)
                return true;
            if (__instance.Character?.Id.Entry.Equals("IRONCLAD", StringComparison.OrdinalIgnoreCase) == true)
                return true;

            var ironclad = UiHelper.FindAll<NCharacterSelectButton>(__instance.GetParent())
                .FirstOrDefault(button => button.Character?.Id.Entry.Equals("IRONCLAD", StringComparison.OrdinalIgnoreCase) == true
                                          && !button.IsLocked);
            if (ironclad is null)
            {
                Entry.Logger.Info("[SolverClimb] no unlocked Ironclad button; keeping AutoSlay's random pick");
                return true;
            }

            _redirecting = true;
            try
            {
                ironclad.Select();
            }
            finally
            {
                _redirecting = false;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Random route: replace AutoSlay's deterministic first-child pick with a
    // seeded uniform pick among the reachable children. The decision is
    // memoized per map position because AutoSlay polls SelectNextRoom while
    // waiting for the node to become clickable.
    // ------------------------------------------------------------------

    [HarmonyPatch(typeof(MapScreenHandler), "SelectNextRoom")]
    internal static class SolverClimbRandomRoutePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(NMapScreen mapScreen, ref NMapPoint? __result)
        {
            if (!SolverClimb.IsRequested || !AutoSlayer.IsActive)
                return true;
            __result = SelectRandomNextRoom(mapScreen);
            return false;
        }
    }

    private static NMapPoint? SelectRandomNextRoom(NMapScreen mapScreen)
    {
        var run = RunManager.Instance?.DebugOnlyGetState();
        if (run?.Map is null)
            return null;

        var lastCoord = run.VisitedMapCoords.Count > 0
            ? run.VisitedMapCoords[run.VisitedMapCoords.Count - 1]
            : run.Map.StartingMapPoint.coord;
        if (ReferenceEquals(_mapKeyRun, run)
            && _mapKeyAct == run.CurrentActIndex
            && _mapKeyVisited == run.VisitedMapCoords.Count
            && _mapKeyCoord == lastCoord
            && _chosenPoint is { })
            return _chosenPoint;

        _mapKeyRun = run;
        _mapKeyAct = run.CurrentActIndex;
        _mapKeyVisited = run.VisitedMapCoords.Count;
        _mapKeyCoord = lastCoord;
        _chosenPoint = null;
        if (!ReferenceEquals(_routeRun, run))
        {
            _routeRun = run;
            _routeRandom = CreateRouteRandom(run);
        }

        var points = UiHelper.FindAll<NMapPoint>(mapScreen);
        IReadOnlyList<MapPoint> candidates;
        if (run.VisitedMapCoords.Count == 0)
        {
            candidates = points.Select(node => node.Point)
                .Where(point => point.coord.row == 0)
                .ToArray();
        }
        else
        {
            var source = points.FirstOrDefault(node => node.Point.coord.Equals(lastCoord))?.Point;
            if (source is null)
                return null;
            candidates = source.Children.ToArray();
        }

        if (candidates.Count == 0)
            return null;
        var picked = candidates[_routeRandom!.Next(candidates.Count)];
        var chosen = points.FirstOrDefault(node => node.Point.coord.Equals(picked.coord));
        _chosenPoint = chosen;
        if (chosen is null)
            return null;

        PrepareForChosen(run, chosen.Point);
        return chosen;
    }

    private static System.Random CreateRouteRandom(RunState run)
    {
        var data = Encoding.UTF8.GetBytes("seed-oracle-solver-climb|route|" + run.Rng.StringSeed);
        var hash = SHA256.HashData(data);
        return new System.Random(BitConverter.ToInt32(hash, 0));
    }

    /// <summary>Runs once per map decision: strip potions so solver losses stay
    /// attributable to deck and relics, then forecast the exact next fight.</summary>
    private static void PrepareForChosen(RunState run, MapPoint point)
    {
        var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        if (player is null)
            return;
        var heldPotions = player.Potions.Count();
        foreach (var potion in player.Potions.ToArray())
            player.DiscardPotionInternal(potion, silent: true);
        Interlocked.Exchange(ref _potionsDiscarded, heldPotions);
        _startedUtc = _startedUtc == default ? DateTime.UtcNow : _startedUtc;

        if (point.PointType is not (MapPointType.Monster or MapPointType.Elite or MapPointType.Boss))
        {
            if (point.PointType == MapPointType.Unknown)
                Interlocked.Increment(ref _skippedUnknownNodes);
            return;
        }

        var path = new RoutePath([new RouteStep(point, false, false)]);
        var worldline = RouteWorldlinePredictor.Predict(run, point, [path]).Single();
        if (worldline.TargetEncounter is not { } encounter
            || worldline.TargetRoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
            return;

        var maxHp = player.Creature.MaxHp;
        var pending = new TaskCompletionSource<CombatSolverForecastResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingForecast = pending;
        SeedOracleDispatcher.Post(() =>
        {
            _ = DispatchForecastAsync(run, encounter, worldline.TargetRoomType, point, maxHp, pending);
        });
    }

    private static async Task DispatchForecastAsync(
        RunState run,
        EncounterModel encounter,
        RoomType roomType,
        MapPoint point,
        int maxHp,
        TaskCompletionSource<CombatSolverForecastResult> pending)
    {
        CombatSolverForecastResult result;
        try
        {
            var isSecondBoss = run.Map.SecondBossMapPoint is { } second
                               && point.coord.Equals(second.coord);
            result = await Entry.CombatSolver.ForecastAsync(
                run,
                encounter,
                point.coord.row + 1,
                point.coord.col,
                roomType,
                point.PointType,
                isSecondBoss,
                new CombatSolverForecastOptions(
                    SearchBudgetMilliseconds: 8_000,
                    OverallTimeoutMilliseconds: 60_000,
                    MaxDegreeOfParallelism: null,
                    PlayerCurrentHpOverride: maxHp,
                    ForceRefresh: true,
                    CloseWorkerAfterRequest: false,
                    WorkerIdleTimeoutMilliseconds: 120_000)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = new CombatSolverForecastResult(
                false, "Exception", null, [], null, null, null, null, null, null,
                exception.GetBaseException().Message);
        }
        pending.TrySetResult(result);
    }

    // ------------------------------------------------------------------
    // Combat room: skip AutoSlay's cheat-buff handler entirely. The fight is
    // resolved by the harness killer while this task records the forecast.
    // ------------------------------------------------------------------

    [HarmonyPatch(typeof(CombatRoomHandler), nameof(CombatRoomHandler.HandleAsync))]
    internal static class SolverClimbCombatPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(MegaCrit.Sts2.Core.Random.Rng random, CancellationToken ct, ref Task __result)
        {
            if (!SolverClimb.IsRequested || !AutoSlayer.IsActive)
                return true;
            __result = ClimbCombatAsync(ct);
            return false;
        }
    }

    private static async Task ClimbCombatAsync(CancellationToken ct)
    {
        await WaitHelper.Until(
            () => CombatManager.Instance is { IsInProgress: true },
            ct, TimeSpan.FromMinutes(2), "solver climb: combat did not start").ConfigureAwait(false);

        var run = RunManager.Instance.DebugOnlyGetState()
                  ?? throw new InvalidOperationException("solver climb: run state missing at combat");
        var player = LocalContext.GetMe(run) ?? run.Players.First();
        var encounterId = CombatManager.Instance.DebugOnlyGetState()?.Encounter?.Id.Entry ?? "?";
        var act = run.CurrentActIndex;
        var floor = run.ActFloor;
        var coord = run.CurrentMapCoord ?? default;
        var roomType = run.CurrentRoom?.RoomType ?? RoomType.Unassigned;
        var hpEntry = player.Creature.CurrentHp;
        var maxHp = player.Creature.MaxHp;
        _combatRecordedByClimb = true;
        if (act == 0 && roomType == RoomType.Boss)
            MarkBossCombatEnded();

        CombatSolverForecastResult? forecast = null;
        var pending = Interlocked.Exchange(ref _pendingForecast, null);
        if (pending is not null)
        {
            AutoSlayer.CurrentWatchdog?.Reset("solver climb: waiting for combat solver forecast");
            var completed = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(70), ct))
                .ConfigureAwait(false);
            forecast = completed == pending.Task
                ? pending.Task.Result
                : new CombatSolverForecastResult(
                    false, "TimedOut", null, [], null, null, null, null, null, null,
                    "harness stopped waiting for the solver");
        }

        var row = new ClimbFightRow
        {
            Act = act + 1,
            Floor = floor,
            Coord = coord.row + 1 + ":" + coord.col,
            RoomType = roomType.ToString(),
            EncounterId = encounterId,
            DeckSize = player.Deck.Cards.Count,
            Deck = player.Deck.Cards
                .Select(card => card.Id.Entry + (card.IsUpgraded ? "+" : string.Empty))
                .ToArray(),
            Relics = player.Relics.Select(relic => relic.Id.Entry).ToArray(),
            Gold = player.Gold,
            HpEntry = hpEntry,
            MaxHp = maxHp,
            PotionsDiscarded = _potionsDiscarded,
            Loss = forecast?.ProjectedHpLoss,
            FinalHp = forecast?.FinalHp,
            EndTurn = forecast?.CombatEndedTurn,
            Confidence = forecast?.Confidence,
            SolverStatus = forecast?.Status ?? "no-forecast",
            SolverError = forecast?.Error,
            SearchMs = forecast?.SearchElapsedMilliseconds,
            TotalMs = forecast?.TotalElapsedMilliseconds,
            Death = (forecast?.FinalHp ?? 1) <= 0
        };
        lock (Gate)
        {
            Fights.Add(row);
            WriteReport(run, final: false);
        }
        SmokeReport.Add(
            $"climb fight act={row.Act} floor={row.Floor} coord={row.Coord} room={row.RoomType} "
            + $"encounter={row.EncounterId} deck={row.DeckSize} gold={row.Gold} "
            + $"hpEntry={row.HpEntry}/{row.MaxHp} loss={row.Loss?.ToString() ?? "-"} "
            + $"finalHp={row.FinalHp?.ToString() ?? "-"} turn={row.EndTurn?.ToString() ?? "-"} "
            + $"confidence={row.Confidence ?? "-"} status={row.SolverStatus} "
            + $"error={row.SolverError ?? "-"}");

        AutoSlayer.CurrentWatchdog?.Reset("solver climb: waiting for combat end");
        await WaitHelper.Until(
            () => CombatManager.Instance is null || !CombatManager.Instance.IsInProgress,
            ct, TimeSpan.FromMinutes(3), "solver climb: combat did not end").ConfigureAwait(false);
        if (act == 0 && roomType == RoomType.Boss)
            FinishFromCombat(run);
    }

    private static void WriteReport(RunState run, bool final)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            List<ClimbFightRow> snapshot;
            lock (Gate)
            {
                snapshot = [.. Fights];
            }
            var payload = new ClimbReport
            {
                Seed = run.Rng.StringSeed,
                Character = run.Players.FirstOrDefault()?.Character.Id.Entry ?? "?",
                StartedAtUtc = _startedUtc,
                Final = final,
                Fights = snapshot
            };
            var path = Path.Combine(OutputDirectory, "climb.json");
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            Entry.Logger.Error($"[SolverClimb] report write failed: {exception}");
        }
    }
}

internal sealed class ClimbFightRow
{
    public int Act { get; set; }
    public int Floor { get; set; }
    public string Coord { get; set; } = "";
    public string RoomType { get; set; } = "";
    public string EncounterId { get; set; } = "";
    public int DeckSize { get; set; }
    public string[] Deck { get; set; } = [];
    public string[] Relics { get; set; } = [];
    public int Gold { get; set; }
    public int HpEntry { get; set; }
    public int MaxHp { get; set; }
    public int PotionsDiscarded { get; set; }
    public int? Loss { get; set; }
    public int? FinalHp { get; set; }
    public int? EndTurn { get; set; }
    public string? Confidence { get; set; }
    public string SolverStatus { get; set; } = "";
    public string? SolverError { get; set; }
    public double? SearchMs { get; set; }
    public double? TotalMs { get; set; }
    public bool Death { get; set; }
}

internal sealed class ClimbReport
{
    public string Seed { get; set; } = "";
    public string Character { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public bool Final { get; set; }
    public List<ClimbFightRow> Fights { get; set; } = [];
}
