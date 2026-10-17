using HarmonyLib;
using System.Threading;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;

namespace SeedOracle.Integration;

/// <summary>
/// Shared guard for native shadow-run execution. Event effects frequently
/// branch on local-player ownership; closing those branches keeps shadow
/// objects from writing profile/UI state while preserving their real NetId.
/// </summary>
internal static class ShadowIsolation
{
    private sealed record Frame(Player Player, bool AllowLocalContext, bool AutomateRewards,
        ICardSelector? Selector = null, bool TakeRewards = true,
        Func<RewardsSet, Task>? RewardHandler = null);
    private static readonly AsyncLocal<List<Frame>?> Frames = new();
    private static readonly AsyncLocal<List<ShadowEventCombatRequest>?> EventCombatCaptures = new();
    private static readonly AsyncLocal<bool> MerchantGeneration = new();
    private static readonly AsyncLocal<bool> LocalPriceEvaluation = new();
    private static readonly AsyncLocal<bool> MerchantPurchase = new();
    private sealed class ShadowPlayerIdentity { }
    private static readonly ConditionalWeakTable<Player, ShadowPlayerIdentity> ShadowPlayers = new();

    internal static void RegisterRun(RunState run)
    {
        foreach (var player in run.Players)
            ShadowPlayers.GetValue(player, static _ => new ShadowPlayerIdentity());
    }

    // A nested branch or a callback can outlive its active execution frame.
    // Shadow ownership therefore belongs to object identity, not just the top
    // frame or the NetId (which intentionally matches the real local player).
    internal static bool IsShadowPlayer(Player? player)
    {
        if (player is null) return false;
        if (ShadowPlayers.TryGetValue(player, out _)) return true;
        if (Frames.Value is not { } frames) return false;
        // LocalContext is queried on hot card/UI paths. Avoid a captured
        // predicate (and its closure allocation) on every ownership check.
        for (var index = frames.Count - 1; index >= 0; index--)
            if (ReferenceEquals(frames[index].Player, player)) return true;
        return false;
    }

    private static bool AllowsLocalContext(Player? player) => AllowLocalContext
        && ReferenceEquals(player, ActiveShadowPlayer);

    internal static bool SuppressLocalPlayer(Player? player) => IsShadowPlayer(player) && !AllowsLocalContext(player);

    internal static bool SuppressMerchantDiscovery => MerchantGeneration.Value;
    internal static bool IsEvaluatingLocalPrice => LocalPriceEvaluation.Value;
    internal static bool IsExecutingMerchantPurchase => MerchantPurchase.Value && ActiveShadowPlayer is not null;

    internal static IDisposable ExecuteMerchantPurchase()
    {
        if (ActiveShadowPlayer is null)
            throw new InvalidOperationException("Merchant purchase requires a shadow player.");
        var previous = MerchantPurchase.Value;
        MerchantPurchase.Value = true;
        return new CallbackScope(() => MerchantPurchase.Value = previous);
    }

    internal static IDisposable GenerateMerchantInventory()
    {
        if (ActiveShadowPlayer is null)
            throw new InvalidOperationException("Merchant generation requires a shadow player.");
        var previous = MerchantGeneration.Value;
        MerchantGeneration.Value = true;
        return new CallbackScope(() => MerchantGeneration.Value = previous);
    }

    internal static IDisposable EvaluateLocalMerchantPrice()
    {
        if (ActiveShadowPlayer is null)
            throw new InvalidOperationException("Merchant pricing requires a shadow player.");
        var previous = LocalPriceEvaluation.Value;
        LocalPriceEvaluation.Value = true;
        return new CallbackScope(() => LocalPriceEvaluation.Value = previous);
    }

    private sealed class CallbackScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    internal sealed record ShadowEventCombatRequest(
        EncounterModel Encounter,
        IReadOnlyList<Reward> ExtraRewards,
        bool ShouldResumeAfterCombat);

    internal static IReadOnlyList<ShadowEventCombatRequest>? CurrentEventCombatCaptures =>
        EventCombatCaptures.Value;

    internal static IDisposable CaptureEventCombats()
    {
        var previous = EventCombatCaptures.Value;
        EventCombatCaptures.Value = [];
        return new EventCombatCaptureScope(previous);
    }

    private sealed class EventCombatCaptureScope(List<ShadowEventCombatRequest>? previous) : IDisposable
    {
        public void Dispose() => EventCombatCaptures.Value = previous;
    }

    internal static void RecordEventCombat(
        EncounterModel encounter,
        IReadOnlyList<Reward> extraRewards,
        bool shouldResumeAfterCombat)
    {
        EventCombatCaptures.Value?.Add(new ShadowEventCombatRequest(
            encounter,
            extraRewards,
            shouldResumeAfterCombat));
    }

    internal static Player? ActiveShadowPlayer => Current?.Player;
    internal static bool AllowLocalContext => Current?.AllowLocalContext == true;
    internal static bool AutomateRewards => Current?.AutomateRewards == true;
    internal static ICardSelector? Selector => Current?.Selector;

    private sealed class Scope(List<Frame>? previous) : IDisposable
    {
        public void Dispose() => Frames.Value = previous;
    }

    private static Frame? Current => Frames.Value is { Count: > 0 } frames
        ? frames[^1]
        : null;

    internal static IDisposable Enter(
        Player shadowPlayer,
        bool allowLocalContext = false,
        bool automateRewards = false)
    {
        var previous = Frames.Value;
        var frames = previous is { } current
            ? new List<Frame>(current)
            : [];
        frames.Add(new Frame(shadowPlayer, allowLocalContext, automateRewards));
        Frames.Value = frames;
        return new Scope(previous);
    }

    internal static IDisposable UseSelector(ICardSelector selector, bool takeRewards = true)
    {
        var previous = Frames.Value
                       ?? throw new InvalidOperationException("A selector requires a shadow scope.");
        var frames = new List<Frame>(previous);
        frames[^1] = frames[^1] with { Selector = selector, TakeRewards = takeRewards };
        Frames.Value = frames;
        return new Scope(previous);
    }

    internal static IDisposable UseRewardHandler(Func<RewardsSet, Task> handler)
    {
        var previous = Frames.Value
                       ?? throw new InvalidOperationException("A reward handler requires a shadow scope.");
        var frames = new List<Frame>(previous);
        frames[^1] = frames[^1] with { RewardHandler = handler };
        Frames.Value = frames;
        return new Scope(previous);
    }

    internal static void Exit()
    {
        if (Frames.Value is not { Count: > 0 } current)
            return;

        var frames = new List<Frame>(current);
        frames.RemoveAt(frames.Count - 1);
        Frames.Value = frames.Count == 0 ? null : frames;
    }

    internal static void ReplaceCurrent(
        Player shadowPlayer,
        bool allowLocalContext = false,
        bool automateRewards = false)
    {
        if (Frames.Value is not { Count: > 0 } current)
        {
            Enter(shadowPlayer, allowLocalContext, automateRewards);
            return;
        }

        var frames = new List<Frame>(current);
        frames[^1] = new Frame(shadowPlayer, allowLocalContext, automateRewards);
        Frames.Value = frames;
    }

    /// <summary>
    /// RewardsSet normally hands control to the live multiplayer synchronizer.
    /// A cloned player is not part of that collection, so shadow event rewards
    /// must be consumed locally and synchronously. Card rewards still use the
    /// active ICardSelector, which keeps the planner's selected card explicit.
    /// </summary>
    internal static async Task AutomateRewardsAsync(RewardsSet rewards)
    {
        if (rewards.Player.Creature.IsDead)
            return;
        if (Current?.RewardHandler is { } handler)
        {
            await handler(rewards);
            return;
        }
        await rewards.GenerateWithoutOffering();
        if (Current?.TakeRewards == false && rewards.DisallowSkipping)
            throw new InvalidOperationException("此事件的奖励不允许跳过，请改为领取奖励。");
        foreach (var reward in EnumerateRewards(rewards.Rewards).ToArray())
        {
            // SelectUnsynchronized preserves AfterRewardTaken and linked
            // reward semantics. Only CardReward's UI/sync boundary is replaced.
            if (Current?.TakeRewards == false || !await reward.SelectUnsynchronized())
                reward.OnSkipped();
        }
    }

    internal static IEnumerable<Reward> EnumerateRewards(IEnumerable<Reward> rewards)
    {
        foreach (var reward in rewards)
        {
            if (reward is LinkedRewardSet linked)
            {
                foreach (var child in EnumerateRewards(linked.Rewards))
                    yield return child;
            }
            else
                yield return reward;
        }
    }

    internal static async Task<bool> SelectCardRewardAsync(CardReward reward)
    {
        var session = new CardRewardSession(reward);
        try
        {
            while (!session.Finished)
            {
                var alternatives = CardRewardAlternative.Generate(reward);
                var selection = Selector?.GetSelectedCardReward(reward._cards, alternatives)
                                ?? throw new InvalidOperationException("Shadow card reward has no scripted selector.");
                await session.ApplyAsync(selection);
            }
            session.CommitHistory();
            return session.Complete;
        }
        finally
        {
            reward.Player.RelicObtained -= reward.OnRelicObtained;
        }
    }

    // A reward screen can be closed after rerolling and another group opened.
    // Keep each native selection session alive across those interleaved actions.
    internal sealed class CardRewardSession(CardReward reward)
    {
        private readonly List<CardModel> chosen = [];
        private bool committed;
        public bool Finished { get; private set; }
        public bool Complete { get; private set; }

        public async Task ApplyAsync(CardRewardSelection selection)
        {
            if (Finished)
                throw new InvalidOperationException("Card reward selection already ended.");
            var player = reward.Player;
            if (selection.card is { } card)
            {
                Complete = true;
                var more = Hook.ShouldAllowSelectingMoreCardRewards(player.RunState, player, reward);
                var result = await CardPileCmd.Add(card, PileType.Deck, skipVisuals: true);
                if (result.success)
                {
                    chosen.Add(result.cardAdded);
                    reward._cards.RemoveAll(candidate => ReferenceEquals(candidate.Card, result.cardAdded));
                }
                Finished = !more;
            }
            else if (selection.alternative is { } alternative)
            {
                Complete = alternative.AfterSelected == PostAlternateCardRewardAction.EndSelectionAndCompleteReward;
                await alternative.OnSelect();
                Finished = alternative.AfterSelected is PostAlternateCardRewardAction.EndSelectionAndCompleteReward
                    or PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward;
            }
            else
            {
                Complete = false;
                Finished = true;
            }
        }

        public void CommitHistory()
        {
            if (committed)
                return;
            committed = true;
            var history = (reward.Player.RunState.CurrentMapPointHistoryEntry
                           ?? throw new InvalidOperationException("Shadow card reward lacks room history."))
                .GetEntry(reward.Player.NetId).CardChoices;
            history.AddRange(chosen.Select(card => new CardChoiceHistoryEntry(card, wasPicked: true)));
            if (Complete)
                history.AddRange(reward.Cards.Select(card => new CardChoiceHistoryEntry(card, wasPicked: false)));
        }

        internal async Task CompleteRewardAsync()
        {
            CommitHistory();
            if (Complete)
            {
                await Hook.AfterRewardTaken(reward.Player.RunState, reward.Player, reward);
                reward.SuccessfullySelected = true;
            }
            else
                reward.OnSkipped();
            if (reward.ParentRewardSet is { } parent)
            {
                parent.RemoveReward(reward);
                await (Task<bool>)AccessTools.Method(typeof(LinkedRewardSet), "OnSelect").Invoke(parent, null)!;
            }
            reward.Player.RelicObtained -= reward.OnRelicObtained;
        }
    }
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.Selector), MethodType.Getter)]
internal static class ShadowCardSelectorPatch
{
    [HarmonyPostfix]
    private static void Postfix(ref ICardSelector? __result)
    {
        if (ShadowIsolation.Selector is { } selector)
            __result = selector;
    }
}

[HarmonyPatch(typeof(EventCombatSynchronizer), nameof(EventCombatSynchronizer.ReadyToEnterCombat))]
internal static class ShadowEventCombatCapturePatch
{
    [HarmonyPrefix]
    private static bool Prefix(
        EncounterModel canonicalEncounter,
        Player player,
        IReadOnlyList<Reward> extraRewards,
        bool shouldResumeAfterCombat)
    {
        if (ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(player, shadow)
            && ShadowIsolation.CurrentEventCombatCaptures is not null)
        {
            ShadowIsolation.RecordEventCombat(
                canonicalEncounter,
                extraRewards,
                shouldResumeAfterCombat);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(NonInteractiveMode), nameof(NonInteractiveMode.IsActive), MethodType.Getter)]
internal static class ShadowNonInteractivePatch
{
    [HarmonyPostfix]
    private static void Postfix(ref bool __result) =>
        __result |= ShadowIsolation.ActiveShadowPlayer is not null;
}

[HarmonyPatch(typeof(PotionCmd), nameof(PotionCmd.TryToProcure),
    new[] { typeof(PotionModel), typeof(Player), typeof(int) })]
internal static class ShadowPotionProcurePatch
{
    [HarmonyPrefix]
    private static bool Prefix(PotionModel potion, Player player, int slotIndex,
        ref Task<PotionProcureResult> __result)
    {
        if (!ReferenceEquals(player, ShadowIsolation.ActiveShadowPlayer))
            return true;
        __result = Procure(potion, player, slotIndex);
        return false;
    }

    private static async Task<PotionProcureResult> Procure(PotionModel potion, Player player, int slotIndex)
    {
        if (!Hook.ShouldProcurePotion(player.RunState, player.Creature.CombatState, potion, player))
            return new PotionProcureResult
            {
                potion = potion, success = false, failureReason = PotionProcureFailureReason.NotAllowed
            };
        var result = player.AddPotionInternal(potion, slotIndex, silent: true);
        if (result.success)
        {
            player.RunState.CurrentMapPointHistoryEntry?.GetEntry(player.NetId).PotionChoices
                .Add(new ModelChoiceHistoryEntry(potion.Id, wasPicked: true));
            await Hook.AfterPotionProcured(player.RunState, player.Creature.CombatState, potion);
        }
        return result;
    }
}

[HarmonyPatch(typeof(CardReward), "OnSelect")]
internal static class ShadowCardRewardPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CardReward __instance, ref Task<bool> __result)
    {
        if (!ShadowIsolation.AutomateRewards
            || !ReferenceEquals(__instance.Player, ShadowIsolation.ActiveShadowPlayer))
            return true;
        __result = ShadowIsolation.SelectCardRewardAsync(__instance);
        return false;
    }
}

// The native Amalgamator actions await an unconditional animation delay.
// Keep their model commands and selector filters, while omitting scene/audio
// work in the shadow copy. EventOption.Chosen still records the native choice.
[HarmonyPatch]
internal static class ShadowAmalgamatorPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        new[] { "CombineStrikes", "CombineDefends" }.Select(name =>
            AccessTools.DeclaredMethod(typeof(MegaCrit.Sts2.Core.Models.Events.Amalgamator), name));

    [HarmonyPrefix]
    private static bool Prefix(MegaCrit.Sts2.Core.Models.Events.Amalgamator __instance,
        System.Reflection.MethodBase __originalMethod, ref Task __result)
    {
        if (!ReferenceEquals(__instance.Owner, ShadowIsolation.ActiveShadowPlayer))
            return true;
        __result = Combine(__instance, __originalMethod.Name == "CombineStrikes" ? CardTag.Strike : CardTag.Defend);
        return false;
    }

    private static async Task Combine(MegaCrit.Sts2.Core.Models.Events.Amalgamator model, CardTag tag)
    {
        var player = model.Owner ?? throw new InvalidOperationException("Shadow event lacks an owner.");
        var cards = await CardSelectCmd.FromDeckForRemoval(player,
            new MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs(
                MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs.RemoveSelectionPrompt, 2),
            card => card.Tags.Contains(tag) && card.Rarity == CardRarity.Basic && card.IsRemovable);
        await CardPileCmd.RemoveFromDeck(cards.ToList());
        var canonical = tag == CardTag.Strike
            ? (CardModel)ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.UltimateStrike>()
            : ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.UltimateDefend>();
        await CardPileCmd.Add(player.RunState.CreateCard(canonical, player), PileType.Deck, skipVisuals: true);
        model.SetEventFinished(new MegaCrit.Sts2.Core.Localization.LocString(
            "events", "AMALGAMATOR.pages.COMBINE_STRIKES.description"));
    }
}

/// <summary>
/// Closes local-player gates for shadow objects while an isolated prediction
/// is in flight. Live players and models remain unaffected.
/// </summary>
[HarmonyPatch(typeof(LocalContext))]
internal static class ShadowIsolationPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMe), typeof(Player))]
    private static void IsMePlayer(Player? player, ref bool __result)
    {
        // Only price hooks reopen local ownership, keeping event/UI gates closed.
        if (ShadowIsolation.IsEvaluatingLocalPrice
            && ReferenceEquals(player, ShadowIsolation.ActiveShadowPlayer))
        {
            __result = true;
            return;
        }
        if (__result && ShadowIsolation.SuppressLocalPlayer(player))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMe), typeof(Creature))]
    private static void IsMeCreature(Creature? creature, ref bool __result)
    {
        if (__result && ShadowIsolation.SuppressLocalPlayer(creature?.Player))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(CardModel))]
    private static void IsMineCard(CardModel? card, ref bool __result)
    {
        if (__result && ShadowIsolation.IsShadowPlayer(card?.Owner))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(PotionModel))]
    private static void IsMinePotion(PotionModel? potion, ref bool __result)
    {
        if (__result && ShadowIsolation.IsShadowPlayer(potion?.Owner))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(RelicModel))]
    private static void IsMineRelic(RelicModel? relic, ref bool __result)
    {
        if (__result && ShadowIsolation.IsShadowPlayer(relic?.Owner))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(EventModel))]
    private static void IsMineEvent(EventModel? eventModel, ref bool __result)
    {
        if (__result && ShadowIsolation.IsShadowPlayer(eventModel?.Owner))
        {
            __result = false;
        }
    }
}

/// <summary>Routes shadow RewardsSet.Offer calls into the local reward runner.</summary>
[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.Offer))]
internal static class ShadowRewardsSetPatch
{
    [HarmonyPrefix]
    private static bool Prefix(RewardsSet __instance, ref Task __result)
    {
        if (!ShadowIsolation.AutomateRewards
            || ShadowIsolation.ActiveShadowPlayer is not { } shadow
            || !ReferenceEquals(__instance.Player, shadow))
        {
            return true;
        }

        __result = ShadowIsolation.AutomateRewardsAsync(__instance);
        return false;
    }
}

// Merchant constructors mark stock as discovered before any purchase.
// The native generator is used on a shadow graph, without profile discovery.
[HarmonyPatch(typeof(SaveManager))]
internal static class ShadowMerchantDiscoveryPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        new[] { nameof(SaveManager.MarkCardAsSeen), nameof(SaveManager.MarkRelicAsSeen),
                nameof(SaveManager.MarkPotionAsSeen) }
            .Select(name => AccessTools.DeclaredMethod(typeof(SaveManager), name));

    [HarmonyPrefix]
    private static bool Prefix() => !ShadowIsolation.SuppressMerchantDiscovery;
}

// Only the Cost getter needs local ownership for Membership Card. Reopening
// ownership around a whole purchase would also reopen native UI/profile gates.
[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.Cost), MethodType.Getter)]
internal static class ShadowMerchantPricePatch
{
    [HarmonyPrefix]
    private static void Prefix(MerchantEntry __instance, ref IDisposable? __state)
    {
        if (ReferenceEquals(__instance._player, ShadowIsolation.ActiveShadowPlayer))
            __state = ShadowIsolation.EvaluateLocalMerchantPrice();
    }

    [HarmonyFinalizer]
    private static void Finalizer(IDisposable? __state) => __state?.Dispose();
}

// Native merchant entries explicitly broadcast after the item has been added.
// Keep their entire purchase/restock/hook lifecycle, suppressing only these
// network messages inside an isolated merchant purchase.
[HarmonyPatch(typeof(RewardSynchronizer))]
internal static class ShadowMerchantSyncPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        new[] { nameof(RewardSynchronizer.SyncLocalGoldLost), nameof(RewardSynchronizer.SyncLocalObtainedCard),
                nameof(RewardSynchronizer.SyncLocalObtainedRelic), nameof(RewardSynchronizer.SyncLocalObtainedPotion) }
            .Select(name => AccessTools.DeclaredMethod(typeof(RewardSynchronizer), name));

    [HarmonyPrefix]
    private static bool Prefix() => !ShadowIsolation.IsExecutingMerchantPurchase;
}
