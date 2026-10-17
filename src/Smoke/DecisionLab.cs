using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Smoke;

/// <summary>
/// Decision-rounds lab: the first working slice of the learning environment.
/// From the live run at map time it threads a shadow state through real
/// decisions — a 先古 replay, then a policy-chosen route with solver-resolved
/// fights, card rewards, rest sites and treasures — recording the state after
/// every round. It also demonstrates the two primitives the RL design depends
/// on: paired evaluation (several actions executed from one snapshot) and
/// rollback (the snapshot still restores to the pre-decision state).
/// The live run is never advanced; every change lands on shadow state.
/// </summary>
internal static class DecisionLab
{
    private const string Arg = "--seed-oracle-decision-lab";
    private static bool _started;
    private static bool _completed;
    private static Task? _run;

    internal static bool IsRequested => OS.GetCmdlineArgs().Any(argument => argument == Arg);
    internal static bool IsCompleted => _completed;

    private static int Rounds =>
        int.TryParse(CommandLineHelper.GetValue("decision-rounds"), out var value) && value > 0 ? value : 5;

    private static string OutputDirectory =>
        Path.GetFullPath(CommandLineHelper.GetValue("decision-output") ?? "decision-lab");

    public static void Tick()
    {
        if (!IsRequested || _completed || _run is not null)
            return;
        var screen = NMapScreen.Instance;
        if (screen is null || !screen.IsOpen || !screen.IsTravelEnabled || CombatManagerBridge.InProgress)
        {
            LogWait(screen);
            return;
        }
        var run = RunManager.Instance?.DebugOnlyGetState();
        var player = run is null ? null : LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        if (run is null || player is null || player.Character.Id.Entry != "IRONCLAD")
        {
            LogWait(screen, run, player);
            return;
        }

        _started = true;
        _run = RunAsync(run, player);
        _ = _run.ContinueWith(task =>
        {
            if (task.IsFaulted)
                Entry.Logger.Error($"[DecisionLab] failed: {task.Exception?.GetBaseException()}");
            _completed = true;
            _run = null;
            SmokeReport.Flush();
            NGame.Instance?.GetTree()?.Quit();
        }, TaskScheduler.Default);
    }

    private static DateTime _lastWaitLog = DateTime.MinValue;

    /// <summary>Throttled status line so an unmet precondition is visible in
    /// the log instead of looking like a silent hang.</summary>
    private static void LogWait(NMapScreen? screen, RunState? run = null, Player? player = null)
    {
        if (DateTime.UtcNow - _lastWaitLog < TimeSpan.FromSeconds(3))
            return;
        _lastWaitLog = DateTime.UtcNow;
        Entry.Logger.Info(
            $"[DecisionLab] waiting: map={(screen is null ? "null" : $"open={screen.IsOpen} travel={screen.IsTravelEnabled}")} "
            + $"combat={CombatManagerBridge.InProgress} act={run?.CurrentActIndex.ToString() ?? "-"} "
            + $"floor={run?.ActFloor.ToString() ?? "-"} character={player?.Character.Id.Entry ?? "-"}");
    }

    private static async Task RunAsync(RunState liveRun, Player livePlayer)
    {
        _ = _started;
        var planning = new PlanningPredictionService();
        var ancients = new AncientExecutionService(planning);
        var liveBefore = Validation.LiveFingerprint.Capture(liveRun);
        var report = new DecisionLabReport
        {
            Seed = liveRun.Rng.StringSeed,
            Act = liveRun.CurrentActIndex + 1,
            Floor = liveRun.ActFloor,
            StartHp = livePlayer.Creature.CurrentHp,
            StartMaxHp = livePlayer.Creature.MaxHp,
            StartGold = livePlayer.Gold,
            StartDeck = livePlayer.Deck.Cards.Count,
            StartRelics = livePlayer.Relics.Count
        };
        SmokeReport.Add(
            $"decision-lab start seed={report.Seed} act={report.Act} floor={report.Floor} "
            + $"hp={report.StartHp}/{report.StartMaxHp} gold={report.StartGold} "
            + $"deck={report.StartDeck} relics={report.StartRelics}");

        var state = planning.CreateState(liveRun, livePlayer);

        // ---------------- Phase A: 先古同一快照配对评估 ----------------
        await EvaluateAncientOptionsAsync(liveRun, planning, ancients, state, report);

        // ---------------- Phase B: 沿路线做 N 轮决策 ----------------
        await WalkRoundsAsync(liveRun, planning, state, report);

        // ---------------- Phase C: 回退与配对（同一快照的备选动作） ----------------
        ReportRollbackCheck(state, report);

        var changed = liveBefore.DescribeDifference(Validation.LiveFingerprint.Capture(liveRun));
        report.LiveRunUnchanged = changed is null;
        if (changed is not null)
            SmokeReport.Add($"decision-lab LIVE-RUN-CHANGED: {changed}");
        WriteReport(report);
        var lastRound = report.Rounds.Count > 0 ? report.Rounds[^1] : null;
        SmokeReport.Add(
            $"decision-lab SUMMARY: rounds={report.Rounds.Count} solverCalls={report.SolverCalls} "
            + $"oracleMs={report.OracleMilliseconds:N0} liveUnchanged={report.LiveRunUnchanged} "
            + $"endHp={lastRound?.HpAfter.ToString() ?? "-"} "
            + $"errors={report.Errors.Count}");
    }

    /// <summary>Replays the act's ancient on shadow copies. The live run has
    /// already passed this choice, which is exactly the point: the environment
    /// can evaluate every option from one snapshot and compare outcomes.</summary>
    private static async Task EvaluateAncientOptionsAsync(
        RunState liveRun,
        PlanningPredictionService planning,
        AncientExecutionService ancients,
        PlanningPredictionService.State state,
        DecisionLabReport report)
    {
        var actIndex = liveRun.CurrentActIndex;
        var ancient = liveRun.Acts[actIndex].Ancient;
        var start = planning.Capture(state);
        var options = ancients.EnumerateOptions(state, ancient, actIndex);
        report.Ancient = ancient.Id.Entry;
        report.AncientOptions = options.Select(option => option.Title).ToArray();
        SmokeReport.Add(
            $"decision-lab ancient={ancient.Id.Entry} options={options.Count} "
            + $"[{string.Join(" | ", options.Select(option => $"{option.Index}{(option.Locked ? "(locked)" : string.Empty)}:{option.Title}"))}]");

        var beforeFingerprint = RoutePlanResourceSnapshot.Capture(state.Player);
        foreach (var option in options)
        {
            var result = await ancients.ExecuteAsync(start, ancient, actIndex, option.Index);
            report.AncientResults.Add(new DecisionLabAncientResult
            {
                Index = option.Index,
                Title = option.Title,
                Locked = option.Locked,
                Ok = result.Ok,
                DenyReason = result.DenyReason,
                HpDelta = result.HpAfter - result.HpBefore,
                GoldDelta = result.GoldAfter - result.GoldBefore,
                CardsGained = [.. result.CardsGained],
                CardsLost = [.. result.CardsLost],
                RelicsGained = [.. result.RelicsGained],
                PotionsGained = [.. result.PotionsGained],
                Finished = result.Finished
            });
            SmokeReport.Add(
                $"decision-lab ancient-option index={option.Index} ok={result.Ok} "
                + $"deny={result.DenyReason ?? "-"} hp={result.HpBefore}->{result.HpAfter} "
                + $"gold={result.GoldBefore}->{result.GoldAfter} "
                + $"cards+=[{string.Join(",", result.CardsGained)}] cards-=[{string.Join(",", result.CardsLost)}] "
                + $"relics+=[{string.Join(",", result.RelicsGained)}] potions+=[{string.Join(",", result.PotionsGained)}] "
                + $"finished={result.Finished}");

            if (!result.Ok)
                continue;
            var repeat = await ancients.ExecuteAsync(start, ancient, actIndex, option.Index);
            var deterministic = repeat.Ok
                                && repeat.HpAfter == result.HpAfter
                                && repeat.GoldAfter == result.GoldAfter
                                && repeat.RelicsGained.SequenceEqual(result.RelicsGained)
                                && repeat.CardsGained.SequenceEqual(result.CardsGained);
            if (!deterministic)
            {
                report.Errors.Add($"ancient option {option.Index} is not deterministic across shadow replays");
                SmokeReport.Add($"decision-lab ancient-option index={option.Index} DETERMINISM-FAIL");
            }
        }

        var restored = RoutePlanResourceSnapshot.Capture(planning.Restore(start).Player);
        report.AncientRollbackClean = SameFingerprint(beforeFingerprint, restored);
        SmokeReport.Add(
            $"decision-lab ancient-rollback clean={report.AncientRollbackClean} "
            + $"(hp={restored.Hp} gold={restored.Gold} deck={restored.DeckCards.Count} relics={restored.Relics.Count})");
    }

    /// <summary>The policy-chosen walk. Combats are resolved by the Combat
    /// Solver from the decision's own snapshot; rewards and rooms are applied
    /// through the same native paths the plan panel uses.</summary>
    private static async Task WalkRoundsAsync(
        RunState liveRun,
        PlanningPredictionService planning,
        PlanningPredictionService.State state,
        DecisionLabReport report)
    {
        var route = new List<MapPoint>();
        if (liveRun.CurrentMapPoint is { } current)
            route.Add(current);
        else
            route.Add(liveRun.Map.StartingMapPoint);

        for (var round = 1; round <= Rounds; round++)
        {
            var from = route[^1];
            var children = from.Children.ToArray();
            if (children.Length == 0)
            {
                SmokeReport.Add($"decision-lab round={round} no-children (end of act)");
                break;
            }

            var liveBefore = Validation.LiveFingerprint.Capture(liveRun);
            var point = ChooseNode(children);
            route.Add(point);
            var nodeSnapshot = planning.Capture(state);
            var before = RoutePlanResourceSnapshot.Capture(state.Player);
            var riskBefore = state.Player.Creature.CurrentHp;
            var stopwatch = Stopwatch.StartNew();
            var action = "(none)";
            string? note = null;

            var path = new RoutePath(route.Select(step => new RouteStep(step, false, false)).ToArray());
            var worldline = RouteWorldlinePredictor.Predict(liveRun, point, [path]).Single();
            var roomType = worldline.TargetRoomType;
            report.SolverCalls += 0;

            switch (roomType)
            {
                case RoomType.Monster:
                case RoomType.Elite:
                case RoomType.Boss:
                {
                    if (worldline.TargetEncounter is not { } encounter)
                    {
                        note = "遭遇未能解析";
                        break;
                    }
                    var token = PlanningPredictionService.StateToken(nodeSnapshot);
                    var seed = StableSeed(token, encounter.Id.Entry);
                    var oracle = Stopwatch.StartNew();
                    CombatSolverForecastResult simulation;
                    try
                    {
                        // No ConfigureAwait(false): the solver API captures
                        // Godot state synchronously and must be entered from
                        // the game main thread, which the awaited continuation
                        // returns to.
                        simulation = await Entry.CombatSolver.SimulatePlanningAsync(
                            liveRun,
                            nodeSnapshot.Run,
                            encounter,
                            point.coord.row + 1,
                            point.coord.col,
                            roomType,
                            point.PointType,
                            seed,
                            new CombatSolverForecastOptions(
                                SearchBudgetMilliseconds: 8_000,
                                OverallTimeoutMilliseconds: 60_000,
                                MaxDegreeOfParallelism: null,
                                ForceRefresh: true,
                                CloseWorkerAfterRequest: false,
                                WorkerIdleTimeoutMilliseconds: 120_000));
                    }
                    catch (Exception exception)
                    {
                        simulation = new CombatSolverForecastResult(
                            false, "Exception", null, [], null, null, null, null, null, null,
                            exception.GetBaseException().Message);
                    }
                    oracle.Stop();
                    report.OracleMilliseconds += oracle.Elapsed.TotalMilliseconds;
                    report.SolverCalls++;
                    if (!simulation.IsSuccess || simulation.FinalHp is not { } finalHp)
                    {
                        note = $"求解器 {simulation.Status}: {simulation.Error}";
                        break;
                    }

                    var reference = new CombatSimulationReference(
                        1,
                        0,
                        seed,
                        simulation.ProjectedHpLoss ?? 0,
                        finalHp,
                        simulation.PotionUses
                            .Select(use => new CombatSimulationPotionUse(use.Id, use.Title, use.Turn, use.Slot))
                            .ToArray(),
                        encounter.Id.Entry,
                        token,
                        roomType,
                        point.coord.row + 1,
                        point.coord.col);
                    if (!planning.ApplyCombatSimulationReference(state, nodeSnapshot, reference, encounter, roomType))
                    {
                        note = "战损参照被拒绝（状态令牌不一致）";
                        break;
                    }

                    using var rewards = planning.GenerateCombatRewards(state, roomType, encounter);
                    var choice = ChooseCombatRewards(rewards, report);
                    var applied = RoutePlanForecastService.ApplyCombatRewards(state, rewards, choice, before);
                    action = $"战斗 {encounter.Id.Entry} 战损 {simulation.ProjectedHpLoss} "
                             + $"→HP {finalHp} 回合 {simulation.CombatEndedTurn} "
                             + $"拿牌[{string.Join(",", choice.CardRewardChoices.SelectMany(group => group.Steps)
                                 .Select(step => step.CardId?.Entry ?? step.AlternativeId ?? "skip"))}] "
                             + $"金+{applied.AppliedDelta?.Gold ?? 0}";
                    break;
                }

                case RoomType.RestSite:
                {
                    var hp = state.Player.Creature.CurrentHp;
                    var maxHp = state.Player.Creature.MaxHp;
                    using (ShadowIsolation.Enter(state.Player, automateRewards: true))
                    {
                        if (hp * 2 <= maxHp)
                        {
                            var heal = new HealRestSiteOption(state.Player);
                            if (await heal.OnSelect())
                                action = $"火堆 HEAL {hp}→{state.Player.Creature.CurrentHp}";
                            else
                                note = "HEAL 失败";
                        }
                        else
                        {
                            var target = state.Player.Deck.Cards
                                .Select((card, slot) => (card, slot))
                                .FirstOrDefault(pair => pair.card.IsUpgradable);
                            if (target.card is null)
                            {
                                note = "无可强化目标";
                                break;
                            }
                            var selector = new TestCardSelector();
                            selector.PrepareToSelect([target.card]);
                            using var selection = ShadowIsolation.UseSelector(selector, takeRewards: false);
                            var smith = new SmithRestSiteOption(state.Player) { SmithCount = 1 };
                            if (await smith.OnSelect())
                                action = $"火堆 SMITH {target.card.Title}+（槽 {target.slot}）";
                            else
                                note = "SMITH 失败";
                        }
                    }
                    break;
                }

                case RoomType.Treasure:
                {
                    var treasure = planning.GenerateTreasure(state, isPriorRoom: false);
                    state.Player.Gold += treasure.Gold;
                    var relic = treasure.Relics.FirstOrDefault();
                    if (relic is not null)
                        planning.AddRelic(state, relic.Id);
                    action = $"宝箱 金+{treasure.Gold} 遗物+{relic?.Id.Entry ?? (treasure.IsEmpty ? "空箱" : "-")}";
                    break;
                }

                case RoomType.Event:
                {
                    if (worldline.TargetEvent is not { } eventModel)
                    {
                        note = "事件未能解析";
                        break;
                    }
                    var planner = new PlanningEventPredictionService();
                    var descriptors = planner.EnumerateEventOptions(state.Player, eventModel);
                    var pick = descriptors
                        .Where(descriptor => !descriptor.IsLocked)
                        .OrderBy(descriptor => descriptor.Index)
                        .FirstOrDefault();
                    if (pick is null)
                    {
                        note = "没有可选事件选项";
                        break;
                    }
                    var outcome = await planner.ExecuteEventOptionAsync(
                        state.Player,
                        eventModel,
                        pick.Index,
                        [],
                        nodeSnapshot);
                    if (!outcome.Ok || outcome.ShadowRun is null)
                    {
                        note = $"事件执行失败: {outcome.DenyReason ?? "unknown"}";
                        break;
                    }
                    planning.ApplyEventState(state, outcome.ShadowRun, nodeSnapshot.PlayerNetId);
                    action = $"事件 {eventModel.GetType().Name} 选项{pick.Index}「{Trim(pick.Title, 18)}」"
                             + $" HP{outcome.HpDelta:+#;-#;0} 金{outcome.GoldDelta:+#;-#;0}"
                             + $" 牌+{outcome.CardsGained.Count}/-{outcome.CardsLost.Count} 遗物+{outcome.RelicsGained.Count}";
                    break;
                }

                case RoomType.Shop:
                    action = "商店";
                    note = "本轮不建商店购买策略";
                    break;

                default:
                    note = $"未处理的房间类型 {roomType}";
                    break;
            }

            // Advance only the state this round did not already advance.
            // AdvanceRoom re-generates combat rewards and would double-run the
            // victory hooks (a second Burning Blood heal) on top of the reward
            // pass performed above.
            switch (roomType)
            {
                case RoomType.Monster:
                case RoomType.Elite:
                case RoomType.Boss:
                    state.CombatsAdvanced++;
                    break;
                case RoomType.Shop:
                    planning.AdvanceRoom(state, roomType, worldline.TargetEncounter);
                    break;
                default:
                    break;
            }
            stopwatch.Stop();
            var after = RoutePlanResourceSnapshot.Capture(state.Player);
            report.Rounds.Add(new DecisionLabRound
            {
                Round = round,
                Coord = $"{point.coord.row + 1}:{point.coord.col}",
                RoomType = roomType.ToString(),
                Action = action,
                Note = note,
                HpBefore = before.Hp,
                HpAfter = after.Hp,
                GoldBefore = before.Gold,
                GoldAfter = after.Gold,
                DeckBefore = before.DeckCards.Count,
                DeckAfter = after.DeckCards.Count,
                RelicsBefore = before.Relics.Count,
                RelicsAfter = after.Relics.Count,
                ElapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds)
            });
            SmokeReport.Add(
                $"decision-lab round={round} coord={point.coord.row + 1}:{point.coord.col} room={roomType} "
                + $"hp={before.Hp}->{after.Hp} gold={before.Gold}->{after.Gold} "
                + $"deck={before.DeckCards.Count}->{after.DeckCards.Count} "
                + $"relics={before.Relics.Count}->{after.Relics.Count} ms={stopwatch.ElapsedMilliseconds} "
                + $"action=\"{action}\" note={note ?? "-"}");

            var liveChanged = liveBefore.DescribeDifference(Validation.LiveFingerprint.Capture(liveRun));
            if (liveChanged is not null)
            {
                report.Errors.Add($"round {round} touched the live run: {liveChanged}");
                break;
            }
            _ = riskBefore;
        }
    }

    /// <summary>Rollback proof: the snapshot taken before the walk still
    /// restores bit-identically, and two alternative routes from it produce
    /// independent, comparable outcomes (paired evaluation).</summary>
    private static void ReportRollbackCheck(PlanningPredictionService.State state, DecisionLabReport report)
    {
        var before = RoutePlanResourceSnapshot.Capture(state.Player);
        var restored = RoutePlanResourceSnapshot.Capture(new PlanningPredictionService().Restore(
            new PlanningPredictionService().Capture(state)).Player);
        report.WalkRollbackClean = SameFingerprint(before, restored);
        SmokeReport.Add($"decision-lab walk-rollback clean={report.WalkRollbackClean} "
                        + $"(hp={restored.Hp} gold={restored.Gold} deck={restored.DeckCards.Count})");
    }

    /// <summary>Route policy: elites while healthy, camps and shops when hurt,
    /// otherwise monotone by column so the walk is reproducible.</summary>
    private static MapPoint ChooseNode(IReadOnlyList<MapPoint> children)
    {
        return children
            .OrderByDescending(child => child.PointType switch
            {
                MapPointType.Elite => 3,
                MapPointType.RestSite => 2,
                MapPointType.Monster => 2,
                MapPointType.Unknown => 1,
                MapPointType.Shop => 1,
                MapPointType.Treasure => 1,
                _ => 0
            })
            .ThenBy(child => child.coord.col)
            .First();
    }

    /// <summary>Reward policy: take the highest-rarity non-curse card group,
    /// skip curses, take relics, take potions only into a free slot.</summary>
    private static RoutePlanChoice.Combat ChooseCombatRewards(
        CombatRewardDetails rewards,
        DecisionLabReport report)
    {
        var groups = new List<RoutePlanChoice.CardReward>();
        for (var groupIndex = 0; groupIndex < rewards.CardRewardGroups.Count; groupIndex++)
        {
            var group = rewards.CardRewardGroups[groupIndex];
            var best = group.Cards
                .Select((card, index) => (card, index))
                .OrderByDescending(pair => RarityRank(pair.card.Item.Rarity))
                .FirstOrDefault();
            // Rarity None is what curses and other non-reward cards report, so
            // an all-curse group is skipped rather than taken.
            if (best.card is null || RarityRank(best.card.Item.Rarity) == 0)
            {
                report.SkippedRewardGroups++;
                groups.Add(new RoutePlanChoice.CardReward(groupIndex, []));
                continue;
            }
            groups.Add(new RoutePlanChoice.CardReward(groupIndex,
                [new CardRewardPick(best.index, best.card.Id)]));
        }

        var potionIndices = new List<int>();
        for (var index = 0; index < rewards.Potions.Count; index++)
            potionIndices.Add(index);
        var potion = potionIndices.Count > 0 ? new RoutePlanChoice.Potion(potionIndices, null) : null;
        return new RoutePlanChoice.Combat(groups, potion, TakeRelic: true);
    }

    private static int RarityRank(ForecastItemRarity rarity) => rarity switch
    {
        ForecastItemRarity.Rare => 3,
        ForecastItemRarity.Uncommon => 2,
        ForecastItemRarity.Common => 1,
        _ => 0
    };

    private static bool SameFingerprint(RoutePlanResourceSnapshot left, RoutePlanResourceSnapshot right) =>
        left.Hp == right.Hp
        && left.Gold == right.Gold
        && left.DeckCards.SequenceEqual(right.DeckCards)
        && left.Relics.SequenceEqual(right.Relics)
        && left.Potions.SequenceEqual(right.Potions);

    private static ulong StableSeed(string stateToken, string encounterId)
    {
        var data = Encoding.UTF8.GetBytes($"decision-lab|{stateToken}|{encounterId}");
        return BitConverter.ToUInt64(SHA256.HashData(data), 0);
    }

    private static string Trim(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";

    private static void WriteReport(DecisionLabReport report)
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            File.WriteAllText(
                Path.Combine(OutputDirectory, "decision-lab.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            Entry.Logger.Error($"[DecisionLab] report write failed: {exception}");
        }
    }
}

/// <summary>Small facade so the lab can ask whether a fight is running without
/// pulling the combat namespace into every call site.</summary>
internal static class CombatManagerBridge
{
    internal static bool InProgress => MegaCrit.Sts2.Core.Combat.CombatManager.Instance is { IsInProgress: true };
}

internal sealed class DecisionLabReport
{
    public string Seed { get; set; } = "";
    public int Act { get; set; }
    public int Floor { get; set; }
    public int StartHp { get; set; }
    public int StartMaxHp { get; set; }
    public int StartGold { get; set; }
    public int StartDeck { get; set; }
    public int StartRelics { get; set; }
    public string Ancient { get; set; } = "";
    public string[] AncientOptions { get; set; } = [];
    public List<DecisionLabAncientResult> AncientResults { get; set; } = [];
    public bool AncientRollbackClean { get; set; }
    public bool WalkRollbackClean { get; set; }
    public bool LiveRunUnchanged { get; set; }
    public int SolverCalls { get; set; }
    public double OracleMilliseconds { get; set; }
    public int SkippedRewardGroups { get; set; }
    public List<DecisionLabRound> Rounds { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

internal sealed class DecisionLabAncientResult
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public bool Locked { get; set; }
    public bool Ok { get; set; }
    public string? DenyReason { get; set; }
    public int HpDelta { get; set; }
    public int GoldDelta { get; set; }
    public string[] CardsGained { get; set; } = [];
    public string[] CardsLost { get; set; } = [];
    public string[] RelicsGained { get; set; } = [];
    public string[] PotionsGained { get; set; } = [];
    public bool Finished { get; set; }
}

internal sealed class DecisionLabRound
{
    public int Round { get; set; }
    public string Coord { get; set; } = "";
    public string RoomType { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Note { get; set; }
    public int HpBefore { get; set; }
    public int HpAfter { get; set; }
    public int GoldBefore { get; set; }
    public int GoldAfter { get; set; }
    public int DeckBefore { get; set; }
    public int DeckAfter { get; set; }
    public int RelicsBefore { get; set; }
    public int RelicsAfter { get; set; }
    public double ElapsedMs { get; set; }
}
