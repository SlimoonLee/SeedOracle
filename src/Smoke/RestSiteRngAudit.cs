using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.Smoke;

/// <summary>
/// Explicit, isolated-process audit of native rest actions and the next boss's
/// opening. Unlike SimulatePlanningAsync this never replaces a combat RNG.
/// </summary>
internal static class RestSiteRngAudit
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };
    private static bool _started;
    private static bool _openingReady;
    private static bool _finished;
    private static RunState? _replayRun;
    private static object? _beforeOpening;

    internal static bool IsRequested => CommandLineHelper.HasArg("seed-oracle-rest-rng-audit");
    private static string OutputDirectory => Path.GetFullPath(CommandLineHelper.GetValue("rest-rng-output")
        ?? "rest-rng-audit");

    internal static bool TickOpeningReplay()
    {
        if (!IsRequested || CommandLineHelper.GetValue("rest-rng-snapshot") is not { } snapshotPath)
            return false;
        if (_finished)
            return true;
        if (!_started)
        {
            var menu = NGame.Instance?.GetTree()?.Root?
                .GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
            if (menu is null || !menu.IsVisibleInTree())
                return true;
            _started = true;
            _ = RestoreAndEnterBossAsync(snapshotPath);
        }
        if (_openingReady && _replayRun?.Players[0].PlayerCombatState is { Phase: PlayerTurnPhase.Play } playerCombat
            && CombatManager.Instance.DebugOnlyGetState() is { } combat)
        {
            try
            {
                if (playerCombat.TurnNumber != 1 || combat.Encounter?.RoomType != RoomType.Boss)
                    throw new InvalidOperationException("Audit did not stop at the boss's first playable turn.");
                var player = _replayRun.Players[0];
                Write("opening.json", new
                {
                    Before = _beforeOpening,
                    Rng = CaptureRng(_replayRun),
                    Encounter = combat.Encounter.Id.Entry,
                    Act = _replayRun.CurrentActIndex + 1,
                    _replayRun.ActFloor,
                    _replayRun.TotalFloor,
                    Coord = _replayRun.CurrentMapCoord,
                    Hp = player.Creature.CurrentHp,
                    Hand = Cards(playerCombat.Hand.Cards, player),
                    Draw = Cards(playerCombat.DrawPile.Cards, player),
                    Enemies = combat.Enemies.Select(creature => new
                    {
                        Id = creature.Monster!.Id.Entry,
                        creature.CurrentHp,
                        creature.MaxHp,
                        creature.SlotName,
                        Move = creature.Monster.NextMove.Id,
                        Rng = creature.Monster.Rng.ToSerializable()
                    }).ToArray()
                });
                Finish("rest-rng-opening PASS: native boss first turn; original RNG preserved; no solver or played cards");
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
        return true;
    }

    private static async Task RestoreAndEnterBossAsync(string snapshotPath)
    {
        try
        {
            var save = JsonSerializer.Deserialize(File.ReadAllText(snapshotPath),
                JsonSerializationUtility.GetTypeInfo<SerializableRun>())
                ?? throw new InvalidDataException("Empty camp snapshot.");
            var run = RunState.FromSerializable(save);
            await RunManager.Instance.SetUpSavedSingleplayer(run, save);
            await PreloadManager.LoadRunAssets(run.Players.Select(player => player.Character));
            await PreloadManager.LoadActAssets(run.Act);
            RunManager.Instance.Launch();
            NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.GenerateMap();
            if (run.CurrentActIndex != 0 || run.CurrentMapPoint?.PointType != MegaCrit.Sts2.Core.Map.MapPointType.RestSite
                || !run.CurrentMapPoint.Children.Contains(run.Map.BossMapPoint))
                throw new InvalidOperationException("Snapshot is not at the first act's last campfire.");
            var sourceRng = save.SerializableRng.Rngs;
            if (sourceRng.Any(pair => pair.Value != run.Rng.GetRng(pair.Key).ToSerializable()))
                throw new InvalidOperationException("Restoring the camp snapshot changed run RNG.");
            _beforeOpening = new { Rng = CaptureRng(run), Hp = run.Players[0].Creature.CurrentHp };
            _replayRun = run;
            // This is the normal map-travel entry, including room and combat hooks.
            await RunManager.Instance.EnterMapCoord(run.Map.BossMapPoint.coord);
            _openingReady = true;
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    internal static async Task CaptureBranchesAsync(RunState run, RestSiteRoom room)
    {
        try
        {
            var planning = new PlanningPredictionService();
            var liveBefore = Validation.LiveFingerprint.Capture(run);
            var baseline = planning.CreateState(run, run.Players[0]);
            // An injured fixture ensures resting has a measurable HP effect.
            baseline.Player.Creature.SetCurrentHpInternal(Math.Max(1, baseline.Player.Creature.MaxHp / 2));
            var snapshot = planning.Capture(baseline);
            var beforeRng = CaptureRng(baseline.Run);
            var smithCount = room.Options.OfType<SmithRestSiteOption>().Single().SmithCount;
            var candidates = baseline.Player.Deck.Cards.Select((card, slot) => (card, slot))
                .Where(pair => pair.card.IsUpgradable).ToArray();
            if (candidates.Length < smithCount)
                throw new InvalidOperationException("No legal smith target in the camp fixture.");
            var targets = candidates.Take(smithCount).Select(pair => pair.slot).ToArray();
            var branches = new List<object>();
            foreach (var choice in new[] { "HEAL", "SMITH" })
            {
                var state = planning.Restore(snapshot);
                using (ShadowIsolation.Enter(state.Player, automateRewards: true))
                {
                    var selector = new TestCardSelector();
                    selector.PrepareToSelect(targets.Select(slot => state.Player.Deck.Cards[slot]));
                    using var selected = ShadowIsolation.UseSelector(selector, takeRewards: false);
                    RestSiteOption option = choice == "HEAL"
                        ? new HealRestSiteOption(state.Player)
                        : new SmithRestSiteOption(state.Player) { SmithCount = smithCount };
                    if (!await option.OnSelect())
                        throw new InvalidOperationException($"Native {choice} option failed.");
                    state.Run.CurrentMapPointHistoryEntry!.GetEntry(state.Player.NetId).RestSiteChoices.Add(choice);
                }
                var path = Path.Combine(OutputDirectory, choice + ".save.json");
                Directory.CreateDirectory(OutputDirectory);
                File.WriteAllText(path, JsonSerializer.Serialize(planning.Capture(state).Run,
                    JsonSerializationUtility.GetTypeInfo<SerializableRun>()));
                branches.Add(new
                {
                    Choice = choice, Snapshot = path, Hp = state.Player.Creature.CurrentHp,
                    Rng = CaptureRng(state.Run), Deck = Cards(state.Player.Deck.Cards, state.Player)
                });
            }
            if (liveBefore.DescribeDifference(Validation.LiveFingerprint.Capture(run)) is { } changed)
                throw new InvalidOperationException($"Camp audit modified source run: {changed}");
            Write("branches.json", new
            {
                Seed = run.Rng.StringSeed, Character = baseline.Player.Character.Id.Entry,
                Act = run.CurrentActIndex + 1, run.ActFloor, run.TotalFloor,
                Camp = run.CurrentMapCoord, Boss = run.Map.BossMapPoint.coord,
                Encounter = run.Act.BossEncounter.Id.Entry,
                Relics = baseline.Player.Relics.Select(relic => relic.Id.Entry).ToArray(),
                BeforeHp = baseline.Player.Creature.CurrentHp, BeforeRng = beforeRng,
                SmithSlots = targets, HealRewards = "explicitly skipped", Branches = branches,
                SourceRunUnchanged = true
            });
            Finish("rest-rng-branches PASS: same first-act last-camp snapshot; native HEAL/SMITH; source unchanged");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private static object CaptureRng(RunState run) => new
    {
        Run = run.Rng.ToSerializable().Rngs.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
        Players = run.Players.Select(player => new
        {
            player.NetId,
            Rngs = player.PlayerRng.ToSerializable().Rngs.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value)
        }).ToArray()
    };

    private static object[] Cards(IEnumerable<CardModel> cards, Player player) => cards.Select(card => (object)new
    {
        Id = card.Id.Entry,
        card.CurrentUpgradeLevel,
        Slot = player.Deck.Cards.ToList().IndexOf(card.DeckVersion ?? card)
    }).ToArray();

    private static void Write(string file, object value)
    {
        Directory.CreateDirectory(OutputDirectory);
        File.WriteAllText(Path.Combine(OutputDirectory, file), JsonSerializer.Serialize(value, JsonOptions));
    }

    private static void Finish(string message)
    {
        _finished = true;
        SmokeReport.Add(message);
        SmokeReport.Flush();
        NGame.Instance?.GetTree().Quit();
    }

    private static void Fail(Exception exception)
    {
        _finished = true;
        Entry.Logger.Error($"[RestRngAudit] {exception}");
        SmokeReport.Add($"rest-rng FAIL: {exception.GetBaseException().Message}");
        SmokeReport.Flush();
        NGame.Instance?.GetTree().Quit(1);
    }
}

[HarmonyPatch(typeof(RestSiteRoomHandler), nameof(RestSiteRoomHandler.HandleAsync))]
internal static class RestSiteRngAuditCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref Task __result)
    {
        if (!SmokeRunner.IsRequested || !RestSiteRngAudit.IsRequested
            || RunManager.Instance.DebugOnlyGetState() is not { CurrentActIndex: 0, CurrentRoom: RestSiteRoom room } run
            || run.CurrentMapPoint?.Children.Contains(run.Map.BossMapPoint) != true)
            return true;
        __result = RestSiteRngAudit.CaptureBranchesAsync(run, room);
        return false;
    }
}
