using HarmonyLib;
using System.Threading;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

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
        var player = reward.Player;
        var chosen = new List<CardModel>();
        var complete = false;
        try
        {
            while (true)
            {
                var alternatives = CardRewardAlternative.Generate(reward);
                var selection = Selector?.GetSelectedCardReward(reward._cards, alternatives)
                                ?? throw new InvalidOperationException("Shadow card reward has no scripted selector.");
                if (selection.card is { } card)
                {
                    complete = true;
                    var more = Hook.ShouldAllowSelectingMoreCardRewards(player.RunState, player, reward);
                    var result = await CardPileCmd.Add(card, PileType.Deck, skipVisuals: true);
                    if (result.success)
                    {
                        chosen.Add(result.cardAdded);
                        reward._cards.RemoveAll(candidate => ReferenceEquals(candidate.Card, result.cardAdded));
                    }
                    if (!more)
                        break;
                }
                else if (selection.alternative is { } alternative)
                {
                    complete = alternative.AfterSelected == PostAlternateCardRewardAction.EndSelectionAndCompleteReward;
                    await alternative.OnSelect();
                    if (alternative.AfterSelected is PostAlternateCardRewardAction.EndSelectionAndCompleteReward
                        or PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward)
                        break;
                }
                else
                {
                    complete = false;
                    break;
                }
            }

            var history = (player.RunState.CurrentMapPointHistoryEntry
                           ?? throw new InvalidOperationException("Shadow card reward lacks room history."))
                .GetEntry(player.NetId).CardChoices;
            history.AddRange(chosen.Select(card => new CardChoiceHistoryEntry(card, wasPicked: true)));
            if (complete)
                history.AddRange(reward.Cards.Select(card => new CardChoiceHistoryEntry(card, wasPicked: false)));
            return complete;
        }
        finally
        {
            player.RelicObtained -= reward.OnRelicObtained;
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
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(player, shadow)
            && !ShadowIsolation.AllowLocalContext)
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMe), typeof(Creature))]
    private static void IsMeCreature(Creature? creature, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(creature?.Player, shadow)
            && !ShadowIsolation.AllowLocalContext)
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(CardModel))]
    private static void IsMineCard(CardModel? card, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(card?.Owner, shadow))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(PotionModel))]
    private static void IsMinePotion(PotionModel? potion, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(potion?.Owner, shadow))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(RelicModel))]
    private static void IsMineRelic(RelicModel? relic, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(relic?.Owner, shadow))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(EventModel))]
    private static void IsMineEvent(EventModel? eventModel, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(eventModel?.Owner, shadow))
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
