using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Forecasting;

namespace SeedOracle.Integration;

/// <summary>
/// Executes event options on a SERIALIZED SHADOW RUN for the route planner.
/// Envelope (from docs/rng-audit-event-execution.md): the shadow player's
/// NetId is rewritten so every IsMe/IsMine gate (profile writes, VFX) stays
/// closed; NonInteractiveMode mutes audio; card-select prompts are answered
/// by a scripted ICardSelector instead of the blocking modal; everything is
/// wrapped by the caller in PredictionPurityGuard.
/// </summary>
internal sealed partial class RandomForeseerAdapter
{
    /// <summary>Events whose Chosen() cannot run headless at all.</summary>
    private static readonly HashSet<string> EventExecutionDenyList = new(StringComparer.Ordinal)
    {
        "CrystalSphere",     // minigame screen + network messages (has its own layout viewer)
        "BattlewornDummy",   // EnterCombatWithoutExitingEvent: needs synchronizer + room push
        "PunchOff",
        "DenseVegetation",   // only its REST→FIGHT page, denied at option level below too
        "Amalgamator",       // option effects await frame/UI signals: sync-block deadlocks
    };

    /// <summary>Option-level denies: options that open reward screens or
    /// kill-confirmation popups; TextKey substring match.</summary>
    private static readonly (string Event, string Key)[] OptionDenyList =
    [
        ("BrainLeech", "RIP"),
        ("WhisperingHollow", "GOLD"),
        ("Trial", "REJECT"),
        ("Trial", "DOUBLE_DOWN"),
        ("WarHistorianRepy", "UNLOCK"),
    ];

    internal sealed class EventExecutionOutcome
    {
        public bool Ok { get; set; }
        public string? DenyReason { get; set; }
        public int GoldDelta { get; set; }
        public int HpDelta { get; set; }
        public List<string> CardsGained { get; } = [];
        public List<string> CardsLost { get; } = [];
        public List<string> RelicsGained { get; } = [];
        public List<string> PotionsGained { get; } = [];
        /// <summary>Secondary options revealed by executing this choice.</summary>
        public List<(string TextKey, string Option)> NextOptions { get; } = [];
        public bool Finished { get; set; }
        public bool OptionOutOfRange { get; set; }
        public RunState? ShadowRun { get; set; }
        public Player? ShadowPlayer { get; set; }
        public EventModel? ShadowEvent { get; set; }
    }

    internal sealed record PlayerSnapshot(
        int Gold,
        int Hp,
        List<string> Deck,
        List<string> Relics,
        List<string> Potions);

    internal bool IsEventExecutionDenied(string entryName, IReadOnlyList<EventOption> options, int optionIndex)
    {
        if (EventExecutionDenyList.Contains(entryName))
            return true;
        if (optionIndex < 0 || optionIndex >= options.Count)
            return true;
        var key = options[optionIndex].TextKey;
        foreach (var (denyEvent, denyKey) in OptionDenyList)
        {
            if (entryName.Contains(denyEvent, StringComparison.Ordinal)
                && key.Contains(denyKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fire-and-forget execution of one option on a fresh shadow run. Never
    /// touches the live run; completion is delivered through the returned
    /// task (post it back to the main thread via SeedOracleDispatcher).
    /// </summary>
    internal async Task<EventExecutionOutcome> ExecuteEventOptionAsync(
        Player livePlayer,
        EventModel canonicalEvent,
        int optionIndex,
        ModelId? plannedCardPick)
    {
        var outcome = new EventExecutionOutcome();
        var entryName = canonicalEvent.GetType().Name;
        if (EventExecutionDenyList.Contains(entryName))
        {
            outcome.DenyReason = "该事件无法无头预演";
            return outcome;
        }

        var previousCheck = NonInteractiveMode.AutoSlayerCheck;
        NonInteractiveMode.AutoSlayerCheck = () => true;
        try
        {
            var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = RunState.FromSerializable(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(livePlayer.NetId)
                               ?? throw new InvalidOperationException(
                                   $"shadow snapshot lacks player {livePlayer.NetId}");
            RewriteShadowNetId(shadowPlayer);

            // The proven initialization path (same as the prediction adapter):
            // BeginEvent is intentionally NOT used — it rejects canonical
            // clones whose Owner was carried over by the shallow copy.
            var shadowEvent = canonicalEvent.ToMutable();
            shadowEvent.Owner = shadowPlayer;
            var playerSlot = shadowEvent.IsShared
                ? 0
                : shadowPlayer.RunState.GetPlayerSlotIndex(shadowPlayer);
            shadowEvent.Rng = new MegaCrit.Sts2.Core.Random.Rng(
                (ulong)((long)shadowPlayer.RunState.Rng.Seed + playerSlot)
                + StringHelper.GetDeterministicHashCode(shadowEvent.Id.Entry));
            shadowEvent.CalculateVars();
            var options = (IReadOnlyList<EventOption>?)GenerateInitialEventOptionsMethod.Invoke(shadowEvent, null)
                          ?? throw new InvalidOperationException(
                              $"Event {shadowEvent.Id} returned no initial options.");
            if (optionIndex >= options.Count)
            {
                outcome.OptionOutOfRange = true;
                return outcome;
            }
            if (IsEventExecutionDenied(entryName, options, optionIndex))
            {
                outcome.DenyReason = "该选项会打开界面/进入特殊战斗，无法无头预演";
                return outcome;
            }
            SetEventStateMethod.Invoke(shadowEvent, [shadowEvent.InitialDescription, options]);
            var before = Capture(shadowPlayer);

            using (CardSelectCmd.PushSelector(new ScriptedCardSelector(plannedCardPick)))
            {
                await options[optionIndex].Chosen();
            }

            var after = Capture(shadowPlayer);
            FillDeltas(outcome, before, after);
            outcome.Finished = shadowEvent.IsFinished;
            foreach (var option in shadowEvent.CurrentOptions)
                outcome.NextOptions.Add((option.TextKey, option.Title.GetFormattedText()));
            outcome.Ok = true;
            outcome.ShadowRun = shadowRun;
            outcome.ShadowPlayer = shadowPlayer;
            outcome.ShadowEvent = shadowEvent;
            return outcome;
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            outcome.DenyReason = $"无头执行失败，已降级：{root.Message}";
            return outcome;
        }
        finally
        {
            NonInteractiveMode.AutoSlayerCheck = previousCheck;
        }
    }

    private static void RewriteShadowNetId(Player shadowPlayer)
    {
        // NetId is get-only; the backing field rewrite is what keeps every
        // LocalContext.IsMe/IsMine gate (profile writes, VFX) closed.
        var field = typeof(Player).GetField(
            "<NetId>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(shadowPlayer, shadowPlayer.NetId ^ 0x5EED0FCEC0FFEE00UL);
    }

    private static PlayerSnapshot Capture(Player player)
    {
        return new PlayerSnapshot(
            player.Gold,
            player.Creature.CurrentHp,
            player.Deck.Cards
                .Select(card => $"{card.Title}{(card.IsUpgraded ? "+" : string.Empty)}")
                .ToList(),
            player.Relics.Select(relic => relic.Title.GetFormattedText()).ToList(),
            player.Potions.Select(potion => potion.Title.GetFormattedText()).ToList());
    }

    private static void FillDeltas(
        EventExecutionOutcome outcome,
        PlayerSnapshot before,
        PlayerSnapshot after)
    {
        outcome.GoldDelta = after.Gold - before.Gold;
        outcome.HpDelta = after.Hp - before.Hp;
        AddMissing(outcome.CardsGained, after.Deck, before.Deck);
        AddMissing(outcome.CardsLost, before.Deck, after.Deck);
        AddMissing(outcome.RelicsGained, after.Relics, before.Relics);
        AddMissing(outcome.PotionsGained, after.Potions, before.Potions);
    }

    private static void AddMissing(List<string> target, IReadOnlyList<string> source, IReadOnlyList<string> removed)
    {
        var remaining = removed.ToList();
        foreach (var item in source)
        {
            if (!remaining.Remove(item))
                target.Add(item);
        }
    }

    /// <summary>
    /// Answers card-select prompts without opening the blocking modal: the
    /// planned card wins; otherwise the first allowed card (options never
    /// reach the game's screen pipeline at all).
    /// </summary>
    private sealed class ScriptedCardSelector(ModelId? plannedCard) : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options,
            int minSelect,
            int maxSelect)
        {
            var list = options.ToList();
            if (list.Count == 0)
                return Task.FromResult((IEnumerable<CardModel>)Array.Empty<CardModel>());

            if (plannedCard is { } want)
            {
                var exact = list.FirstOrDefault(card => card.Id == want)
                            ?? list.FirstOrDefault(card => card.Id.Entry == want.Entry);
                if (exact is not null)
                    return Task.FromResult((IEnumerable<CardModel>)new[] { exact });
            }

            var count = Math.Min(Math.Max(minSelect, 1), list.Count);
            return Task.FromResult((IEnumerable<CardModel>)list.Take(count));
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0)
                return default;

            CardModel? pick = null;
            if (plannedCard is { } want)
            {
                pick = options.FirstOrDefault(option => option.Card.Id == want)?.Card
                       ?? options.FirstOrDefault(option => option.Card.Id.Entry == want.Entry)?.Card;
            }

            return new CardRewardSelection
            {
                card = pick ?? options[0].Card,
                alternative = null
            };
        }
    }
}
