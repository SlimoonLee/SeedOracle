#if DEBUG
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class ShadowDeathIsolationRegressionTests
{
    internal static bool Observing { get; private set; }
    private static readonly List<string> GlobalCalls = [];
    internal static void Record(MethodBase method)
    {
        if (Observing) GlobalCalls.Add(method.DeclaringType!.Name + "." + method.Name);
    }

    internal static void Run(RunState live)
    {
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var planning = new PlanningPredictionService();
        var before = LiveFingerprint.Capture(live);
        var globalBefore = GlobalSnapshot();
        var profileBefore = JsonSerializer.Serialize(SaveManager.Instance.Progress.ToSerializable());
        var fixture = planning.CreateState(live, player);
        foreach (var relic in fixture.Player.Relics.ToArray()) fixture.Player.RemoveRelicInternal(relic);
        foreach (var potion in fixture.Player.Potions.ToArray()) fixture.Player.DiscardPotionInternal(potion, silent: true);
        fixture.Player.Creature.SetCurrentHpInternal(1);
        fixture.Run.AppendToMapPointHistory(MegaCrit.Sts2.Core.Map.MapPointType.Shop, RoomType.Shop, null);
        var input = planning.Capture(fixture);

        GlobalCalls.Clear();
        Observing = true;
        try
        {
            // Direct, indirect, collection and after-scope calls must all
            // preserve model death without entering global run endings.
            var shadow = planning.Restore(input);
            using (ShadowIsolation.Enter(shadow.Player, automateRewards: true))
                CreatureCmd.Kill(shadow.Player.Creature).GetAwaiter().GetResult();
            Require(shadow.Player.Creature.IsDead, "shadow death was not applied to the branch");
            var indirect = planning.Restore(input);
            CreatureCmd.SetCurrentHp(indirect.Player.Creature, 0).GetAwaiter().GetResult();
            Require(indirect.Player.Creature.IsDead, "after-scope indirect death was not applied");

            var tail = planning.Restore(input);
            planning.AddRelic(tail, ModelDb.Relic<LizardTail>().Id);
            CreatureCmd.Kill(new[] { tail.Player.Creature }).GetAwaiter().GetResult();
            Require(tail.Player.Creature.CurrentHp == Math.Max(1, tail.Player.Creature.MaxHp / 2)
                && tail.Player.Relics.OfType<LizardTail>().Single().WasUsed, "Lizard Tail prevention changed");

            var fairy = planning.Restore(input);
            using (ShadowIsolation.Enter(fairy.Player))
                PotionCmd.TryToProcure(ModelDb.Potion<FairyInABottle>().ToMutable(), fairy.Player).GetAwaiter().GetResult();
            CreatureCmd.Kill(fairy.Player.Creature).GetAwaiter().GetResult();
            Require(fairy.Player.Creature.CurrentHp == Math.Max(1, (int)(fairy.Player.Creature.MaxHp * 0.3m))
                && !fairy.Player.Potions.OfType<FairyInABottle>().Any(), "Fairy prevention or consumption changed");

            var forced = planning.Restore(input);
            planning.AddRelic(forced, ModelDb.Relic<LizardTail>().Id);
            CreatureCmd.Kill(forced.Player.Creature, force: true).GetAwaiter().GetResult();
            Require(forced.Player.Creature.IsDead && !forced.Player.Relics.OfType<LizardTail>().Single().WasUsed,
                "forced death must bypass prevention");
            var mixedRejected = false;
            try { CreatureCmd.Kill(new[] { player.Creature, forced.Player.Creature }).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { mixedRejected = true; }
            Require(mixedRejected, "mixed live/shadow collection was not rejected");

            var events = new PlanningEventPredictionService();
            var lethalEvent = events.ExecuteEventOptionAsync(player, ModelDb.Event<RoomFullOfCheese>(), 1, [], input)
                .GetAwaiter().GetResult();
            Require(lethalEvent.Ok && lethalEvent.ShadowPlayer?.Creature.IsDead == true,
                "real low-HP event execution did not retain its lethal branch");

            var trialSteps = new[] { new EventPlanStep("TRIAL.pages.INITIAL.options.REJECT", []) };
            var trial = events.EnumerateEventOptions(player, ModelDb.Event<Trial>(), input, previousSteps: trialSteps);
            Require(trial.Any(option => option.TextKey.EndsWith(".DOUBLE_DOWN", StringComparison.Ordinal)
                && option.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RunEnding),
                "Trial run-ending option was not discovered safely");

            VerifyMerchantRemoval(planning, input);
        }
        finally { Observing = false; }

        Require(GlobalCalls.Count == 0, "shadow entered live boundaries: " + string.Join(", ", GlobalCalls));
        Require(!player.Creature.IsDead && globalBefore == GlobalSnapshot(), "live run/combat manager changed");
        Require(profileBefore == JsonSerializer.Serialize(SaveManager.Instance.Progress.ToSerializable()),
            "profile progress changed");
        Require(before.DescribeDifference(LiveFingerprint.Capture(live)) is null, "live run changed");
        Smoke.SmokeReport.Add("shadow-death-isolation PASS: direct indirect after_scope lethal_event=dead "
            + "lizard_tail=fairy=preserved force=bypasses_prevention mixed=rejected "
            + "global_end_calls=0 profile=unchanged managers=unchanged live=unchanged");
        Smoke.SmokeReport.Add("shadow-merchant-isolation PASS: parasol_cards=7 free_removal=shadow_only "
            + "missing_choice=blocked real_synchronizer_calls=0 original_parasol_tasks=0 map_input_calls=0 abandon_modals=0");
    }

    private static void VerifyMerchantRemoval(PlanningPredictionService planning, PlanningPredictionService.StateSnapshot input)
    {
        var shadow = planning.Restore(input);
        planning.AddRelic(shadow, ModelDb.Relic<LordsParasol>().Id);
        var room = new MerchantRoom { _runState = shadow.Run };
        shadow.Run.PushRoom(room);
        try
        {
            using var isolation = ShadowIsolation.Enter(shadow.Player, automateRewards: true);
            using var discovery = ShadowIsolation.GenerateMerchantInventory();
            var stock = MerchantInventory.CreateForNormalMerchant(shadow.Player);
            // Isolate the Parasol -> removal leak. Arbitrary relic choices
            // have separate native-merchant regressions.
            stock._relicEntries.Clear();
            stock._potionEntries.Clear();
            room.Inventories.Add(stock);
            var selected = shadow.Player.Deck.Cards.First(card => card.IsRemovable);
            var cards = shadow.Player.Deck.Cards.Count;
            var gold = shadow.Player.Gold;
            var removals = shadow.Player.ExtraFields.CardShopRemovalsUsed;
            var history = shadow.Run.CurrentMapPointHistoryEntry!.GetEntry(shadow.Player.NetId).CardsRemoved.Count;
            using (ShadowIsolation.UseSelector(new ExplicitRemovalSelector(selected)))
                shadow.Player.Relics.OfType<LordsParasol>().Single().AfterRoomEntered(room).GetAwaiter().GetResult();
            Require(shadow.Player.Deck.Cards.Count == cards + 7 - 1 && !shadow.Player.Deck.Cards.Contains(selected)
                && stock.CardEntries.All(entry => !entry.IsStocked) && stock.CardRemovalEntry!.Used
                && shadow.Player.Gold == gold && shadow.Player.ExtraFields.CardShopRemovalsUsed == removals + 1
                && shadow.Run.CurrentMapPointHistoryEntry.GetEntry(shadow.Player.NetId).CardsRemoved.Count == history + 1,
                "Parasol purchases/free removal did not apply only to the shadow model");
        }
        finally { shadow.Run.PopCurrentRoom(); }

        var unchosen = planning.Restore(input);
        planning.AddRelic(unchosen, ModelDb.Relic<LordsParasol>().Id);
        var rejected = false;
        try { planning.GenerateMerchant(unchosen); }
        catch (InvalidOperationException exception) { rejected = exception.Message.Contains("领主阳伞", StringComparison.Ordinal); }
        Require(rejected, "unspecified Parasol selections were silently guessed or dispatched to real UI");

        // Exercise the native removal wrapper without an ambient frame too.
        var delayed = planning.Restore(input);
        var removal = new MerchantCardRemovalEntry(delayed.Player);
        rejected = false;
        try { removal.OnTryPurchaseWrapper(null, ignoreCost: true, cancelable: false).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && !removal.Used, "after-scope removal reached the live synchronizer or guessed a target");
    }

    private sealed class ExplicitRemovalSelector(CardModel selected) : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) =>
            Task.FromResult<IEnumerable<CardModel>>(options.Contains(selected) ? [selected] : []);

        public MegaCrit.Sts2.Core.TestSupport.CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> alternatives) =>
            throw new InvalidOperationException("Unexpected reward selection in removal fixture.");
    }

    private static string GlobalSnapshot() => JsonSerializer.Serialize(new
    {
        RunManager.Instance.IsInProgress, RunManager.Instance.IsCleaningUp, RunManager.Instance.IsAbandoned,
        RunManager.Instance._runHistoryWasUploaded,
        Combat = new { CombatManager.Instance.IsInProgress, CombatManager.Instance.IsEnding,
            CombatManager.Instance.CurrentCombatId, Depth = CombatManager.Instance._cardOrPotionEffectDepth.Count }
    });

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Shadow isolation regression: " + message);
    }
}

[HarmonyPatch]
internal static class ShadowGlobalBoundaryProbe
{
    private static IEnumerable<MethodBase> TargetMethods() =>
    [
        AccessTools.DeclaredMethod(typeof(RunManager), nameof(RunManager.OnEnded)),
        AccessTools.DeclaredMethod(typeof(CombatManager), nameof(CombatManager.LoseCombat)),
        AccessTools.DeclaredMethod(typeof(NRun), nameof(NRun.ShowGameOverScreen)),
        AccessTools.DeclaredMethod(typeof(OneOffSynchronizer), nameof(OneOffSynchronizer.DoLocalMerchantCardRemoval)),
        AccessTools.DeclaredMethod(typeof(LordsParasol), "PurchaseEverything"),
        AccessTools.DeclaredMethod(typeof(NMapScreen), nameof(NMapScreen.SetTravelEnabled)),
        AccessTools.DeclaredMethod(typeof(NAbandonRunConfirmPopup), nameof(NAbandonRunConfirmPopup.Create))
    ];

    [HarmonyPrefix]
    private static void Prefix(MethodBase __originalMethod) => ShadowDeathIsolationRegressionTests.Record(__originalMethod);
}
#endif
