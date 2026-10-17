using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Forecasting;

/// <summary>Outcome of executing one ancient option on a shadow copy of a
/// planning state. After is the resulting snapshot; callers thread it into the
/// next decision the same way the event pipeline does.</summary>
internal sealed record AncientOptionResult(
    bool Ok,
    string? DenyReason,
    int OptionIndex,
    string OptionTitle,
    string TextKey,
    bool Locked,
    bool Finished,
    int HpBefore,
    int HpAfter,
    int GoldBefore,
    int GoldAfter,
    IReadOnlyList<string> CardsGained,
    IReadOnlyList<string> CardsLost,
    IReadOnlyList<string> RelicsGained,
    IReadOnlyList<string> PotionsGained,
    IReadOnlyList<string> NextOptions,
    PlanningPredictionService.StateSnapshot? After);

internal sealed record AncientOptionDescriptor(
    int Index,
    string Title,
    string Description,
    string TextKey,
    bool Locked);

/// <summary>
/// Executes 先古之民 (ancient) options on a shadow run. Ancients are ordinary
/// <see cref="EventModel"/> instances, so this reuses the game's own
/// <c>BeginEvent</c> entry point — owner, per-event RNG, variable calculation
/// and initial option generation all follow the live flow — under the same
/// isolation fences the regular event pipeline uses. Nothing here touches the
/// live run.
/// </summary>
internal sealed class AncientExecutionService(PlanningPredictionService planning)
{
    /// <summary>Options the ancient would offer for this state, without executing anything.</summary>
    public IReadOnlyList<AncientOptionDescriptor> EnumerateOptions(
        PlanningPredictionService.State state,
        AncientEventModel canonical,
        int actIndex)
    {
        var snapshot = planning.Capture(state);
        return RunIsolated(snapshot, canonical, actIndex, (shadowPlayer, shadowEvent) =>
            shadowEvent.CurrentOptions
                .Where(option => !option.IsProceed)
                .Select((option, index) => new AncientOptionDescriptor(
                    index,
                    option.Title.GetFormattedText(),
                    option.Description.GetFormattedText(),
                    option.TextKey,
                    option.IsLocked))
                .ToArray());
    }

    /// <summary>
    /// Executes one option on a shadow copy. The starting snapshot is passed in
    /// so a caller can evaluate several options from the *same* state (paired
    /// rollouts) instead of chaining them.
    /// </summary>
    public Task<AncientOptionResult> ExecuteAsync(
        PlanningPredictionService.StateSnapshot start,
        AncientEventModel canonical,
        int actIndex,
        int optionIndex,
        IReadOnlyList<EventCardPick>? cardPicks = null) =>
        RunIsolatedAsync(start, canonical, actIndex, optionIndex, cardPicks);

    private async Task<AncientOptionResult> RunIsolatedAsync(
        PlanningPredictionService.StateSnapshot start,
        AncientEventModel canonical,
        int actIndex,
        int optionIndex,
        IReadOnlyList<EventCardPick>? cardPicks)
    {
        var shadowRun = PlanningPredictionService.RestoreRun(start.Run);
        shadowRun.CurrentActIndex = actIndex;
        var shadowPlayer = RequirePlayer(shadowRun, start.PlayerNetId);
        var before = RoutePlanResourceSnapshot.Capture(shadowPlayer);
        using var isolation = ShadowIsolation.Enter(shadowPlayer, automateRewards: true);

        TestCardSelector? selector = null;
        IDisposable? selectorScope = null;
        try
        {
            using var combatCapture = ShadowIsolation.CaptureEventCombats();
            var shadowEvent = BeginAncient(shadowRun, shadowPlayer, canonical, out var skipReason);
            if (shadowEvent is null)
                return Denied(optionIndex, skipReason ?? "先古无法在影子中初始化", before);

            var options = shadowEvent.CurrentOptions;
            if (optionIndex < 0 || optionIndex >= options.Count)
                return Denied(optionIndex, "选项下标越界", before);
            var option = options[optionIndex];
            if (option.IsLocked)
                return Denied(optionIndex, "该选项在当前状态下锁定", before, option);

            if (cardPicks is { Count: > 0 })
            {
                selector = new TestCardSelector();
                selector.PrepareToSelect(cardPicks
                    .Select(pick => pick.DeckSlot >= 0 && pick.DeckSlot < shadowPlayer.Deck.Cards.Count
                        ? shadowPlayer.Deck.Cards[pick.DeckSlot]
                        : shadowPlayer.Deck.Cards.FirstOrDefault(card => card.Id == pick.CardId))
                    .Where(card => card is not null)
                    .Select(card => card!));
                selectorScope = ShadowIsolation.UseSelector(selector, takeRewards: false);
            }

            await option.Chosen();
            selectorScope?.Dispose();
            selectorScope = null;

            var after = RoutePlanResourceSnapshot.Capture(shadowPlayer);
            var afterSnapshot = SnapshotOf(start, shadowRun, shadowPlayer);
            return new AncientOptionResult(
                true,
                null,
                optionIndex,
                option.Title.GetFormattedText(),
                option.TextKey,
                false,
                !shadowEvent.IsFinished ? false : true,
                before.Hp,
                after.Hp,
                before.Gold,
                after.Gold,
                Difference(after.DeckCards, before.DeckCards),
                Difference(before.DeckCards, after.DeckCards),
                Difference(after.Relics, before.Relics),
                Difference(after.Potions, before.Potions),
                shadowEvent.IsFinished
                    ? []
                    : shadowEvent.CurrentOptions.Select(next => next.Title.GetFormattedText()).ToArray(),
                afterSnapshot);
        }
        catch (Exception exception)
        {
            return Denied(optionIndex, exception.GetBaseException().Message, before);
        }
        finally
        {
            selectorScope?.Dispose();
            _ = selector;
        }
    }

    private T RunIsolated<T>(
        PlanningPredictionService.StateSnapshot start,
        AncientEventModel canonical,
        int actIndex,
        Func<Player, EventModel, T> action)
    {
        var shadowRun = PlanningPredictionService.RestoreRun(start.Run);
        shadowRun.CurrentActIndex = actIndex;
        var shadowPlayer = RequirePlayer(shadowRun, start.PlayerNetId);
        using var isolation = ShadowIsolation.Enter(shadowPlayer, automateRewards: true);
        var shadowEvent = BeginAncient(shadowRun, shadowPlayer, canonical, out _)
                          ?? throw new InvalidOperationException("先古无法在影子中初始化");
        return action(shadowPlayer, shadowEvent);
    }

    private static Player RequirePlayer(RunState shadowRun, ulong netId) =>
        shadowRun.GetPlayer(netId)
        ?? throw new InvalidOperationException($"影子快照缺少玩家 {netId}");

    /// <summary>The game's own event entry: owner, RNG, vars and options.</summary>
    private static EventModel? BeginAncient(
        RunState shadowRun,
        Player shadowPlayer,
        AncientEventModel canonical,
        out string? skipReason)
    {
        skipReason = null;
        try
        {
            var ancient = canonical.IsMutable
                ? (AncientEventModel)canonical.ClonePreservingMutability()
                : (AncientEventModel)canonical.ToMutable();
            ancient.Owner = null;
            var synchronizer = new EventCombatSynchronizer(shadowRun, shadowRun);
            ancient.BeginEvent(shadowPlayer, synchronizer, isPreFinished: false)
                .GetAwaiter().GetResult();
            return ancient;
        }
        catch (Exception exception)
        {
            skipReason = exception.GetBaseException().Message;
            return null;
        }
    }

    private static AncientOptionResult Denied(
        int optionIndex,
        string reason,
        RoutePlanResourceSnapshot before,
        EventOption? option = null) =>
        new(false, reason, optionIndex, option?.Title.GetFormattedText() ?? "?", option?.TextKey ?? "?",
            option?.IsLocked ?? false, false, before.Hp, before.Hp, before.Gold, before.Gold,
            [], [], [], [], [], null);

    /// <summary>Threading counters live on the snapshot, not the run, so an
    /// ancient execution carries the caller's counters forward unchanged.</summary>
    private static PlanningPredictionService.StateSnapshot SnapshotOf(
        PlanningPredictionService.StateSnapshot start,
        RunState shadowRun,
        Player shadowPlayer) =>
        new(PlanningPredictionService.CreateSerializableRun(shadowRun),
            shadowPlayer.NetId,
            start.MerchantVisits,
            start.CombatsAdvanced,
            start.TreasureRoomsAdvanced,
            start.LavaRockTriggered,
            start.WongosTicketTriggered,
            start.HistoricalTreasureRooms);

    private static IReadOnlyList<string> Difference(
        IReadOnlyList<string> source,
        IReadOnlyList<string> removed)
    {
        var remaining = removed.ToList();
        var result = new List<string>();
        foreach (var item in source)
        {
            if (!remaining.Remove(item))
                result.Add(item);
        }

        return result;
    }
}
