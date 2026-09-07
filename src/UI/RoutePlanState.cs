using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

namespace SeedOracle.UI;

internal enum RoutePlanPhase
{
    Active,
    Void
}

internal enum MerchantCategory
{
    CharacterCard,
    ColorlessCard,
    Relic,
    Potion
}

internal sealed record MerchantPick(MerchantCategory Category, int Index);

/// <summary>
/// A card selected by an event plan. DeckSlot is captured from the shadow
/// state immediately before the event so duplicate copies remain distinct.
/// SelectionStep identifies the native card-selection request when an event
/// asks for more than one card or opens several requests in sequence.
/// </summary>
internal sealed record EventCardPick(
    int SelectionStep,
    ModelId CardId,
    int DeckSlot = -1,
    int SelectionOrder = 0);

internal sealed record EventPlanStep(string TextKey, IReadOnlyList<EventCardPick> CardPicks, bool? TakeRewards = null)
{
    public CombatSimulationReference? SimulationReference { get; init; }
    public RoutePlanChoice.Combat? CombatRewards { get; init; }
}

internal sealed record CombatSimulationPotionUse(
    string Id,
    string Title,
    int Turn,
    int Slot);

internal sealed record CombatSimulationReference(
    int SampleCount,
    int SelectedSampleIndex,
    ulong SampleSeed,
    int ProjectedHpLoss,
    int? FinalHp,
    IReadOnlyList<CombatSimulationPotionUse> PotionUses,
    string EncounterId,
    string StateToken,
    RoomType RoomType,
    int TargetFloor,
    int TargetColumn);

internal sealed record CardRewardPick(int CardIndex = -1, ModelId? CardId = null, string? AlternativeId = null);

/// <summary>
/// A player intent recorded on one planned room. Choices never touch live
/// state; they are applied to cloned/shadow state by the forecast pipeline.
/// </summary>
internal abstract record RoutePlanChoice
{
    private RoutePlanChoice()
    {
    }

    internal sealed record CardReward(int BundleIndex, IReadOnlyList<CardRewardPick> Steps) : RoutePlanChoice;

    /// <summary>Potion pickup from rewards: indices taken, plus the potion to
    /// discard first when the slots are full (replacement).</summary>
    internal sealed record Potion(
        IReadOnlyList<int> TakenPotions,
        ModelId? DiscardPotion) : RoutePlanChoice;

    /// <summary>
    /// Card groups, potions and relics are independent decisions.
    /// </summary>
    internal sealed record Combat(
        IReadOnlyList<CardReward> CardRewardChoices,
        Potion? PotionChoice,
        bool TakeRelic) : RoutePlanChoice
    {
        public CombatSimulationReference? SimulationReference { get; init; }

        internal CardReward ChoiceForGroup(int groupIndex) =>
            CardRewardChoices.FirstOrDefault(choice => choice.BundleIndex == groupIndex)
            ?? new CardReward(groupIndex, []);

        internal Combat WithCardRewardChoice(CardReward choice) =>
            this with
            {
                CardRewardChoices = CardRewardChoices
                    .Where(existing => existing.BundleIndex != choice.BundleIndex)
                    .Append(choice)
                    .OrderBy(existing => existing.BundleIndex)
                    .ToArray()
            };
    }

    internal sealed record Merchant(IReadOnlyList<MerchantPick> Picks, bool RemoveCard) : RoutePlanChoice;

    internal sealed record EventOption(int OptionIndex, IReadOnlyList<EventCardPick> CardPicks) : RoutePlanChoice
    {
        public CombatSimulationReference? SimulationReference { get; init; }
        public Combat CombatRewards { get; init; } = new([], null, true);
        public IReadOnlyList<EventPlanStep> PreviousSteps { get; init; } = [];
        public bool? TakeRewards { get; init; }
        public EventOption(int optionIndex)
            : this(optionIndex, [])
        {
        }
    }

    internal sealed record RestSite(string OptionId, ModelId? TargetCard) : RoutePlanChoice
    {
        /// <summary>
        /// Cook removes two cards. Smith and the other rest actions leave this
        /// unset; keeping it optional preserves the existing plan data shape.
        /// </summary>
        public ModelId? SecondTargetCard { get; init; }

        /// <summary>
        /// Zero-based deck slots captured in the state immediately before this
        /// rest site. ModelId alone cannot distinguish two copies of a card.
        /// A negative value keeps compatibility with older in-memory choices.
        /// </summary>
        public int TargetCardSlot { get; init; } = -1;
        public int SecondTargetCardSlot { get; init; } = -1;

        /// <summary>
        /// A rest-site choice is complete once its required targets are known.
        /// This is kept on the choice itself so stale UI controls cannot mutate
        /// an already committed action after the panel is rebuilt.
        /// </summary>
        public bool IsCommitted => OptionId switch
        {
            "" => false,
            "SMITH" => TargetCard is not null,
            "COOK" => TargetCard is { } first
                       && SecondTargetCard is { } second
                       && (first.Entry != second.Entry
                           || (TargetCardSlot >= 0
                               && SecondTargetCardSlot >= 0
                               && TargetCardSlot != SecondTargetCardSlot)),
            _ => true
        };
    }

    internal sealed record Relic(bool Take) : RoutePlanChoice;

    /// <summary>Normalizes legacy single-reward choices into the composite form.</summary>
    internal static Combat AsCombat(RoutePlanChoice? choice) => choice switch
    {
        Combat combat => combat,
        CardReward card => new Combat([card], null, true),
        Potion potion => new Combat([], potion, true),
        Relic relic => new Combat([], null, relic.Take),
        _ => new Combat([], null, true)
    };
}

/// <summary>
/// Semantic snapshot of the resources that route plans track. Stored as
/// display names so completed-floor diffs can be shown without model lookups.
/// </summary>
internal sealed record RoutePlanResourceSnapshot(
    int Gold,
    int Hp,
    IReadOnlyList<string> DeckCards,
    IReadOnlyList<string> Relics,
    IReadOnlyList<string> Potions)
{
    public static RoutePlanResourceSnapshot Capture(Player? player)
    {
        if (player is null)
            return new RoutePlanResourceSnapshot(0, 0, [], [], []);

        return new RoutePlanResourceSnapshot(
            player.Gold,
            player.Creature.CurrentHp,
            player.Deck.Cards
                .Select(card => $"{card.Title}{(card.IsUpgraded ? "+" : string.Empty)}")
                .ToArray(),
            player.Relics.Select(relic => relic.Title.GetFormattedText()).ToArray(),
            player.Potions.Select(potion => potion.Title.GetFormattedText()).ToArray());
    }
}

/// <summary>What actually happened in a completed planned room, from live-state diffs.</summary>
internal sealed record RoutePlanActualDelta(
    int Gold,
    int Hp,
    IReadOnlyList<string> CardsGained,
    IReadOnlyList<string> CardsLost,
    IReadOnlyList<string> RelicsGained,
    IReadOnlyList<string> RelicsLost,
    IReadOnlyList<string> PotionsGained,
    IReadOnlyList<string> PotionsLost)
{
    public bool IsEmpty =>
        Gold == 0
        && Hp == 0
        && CardsGained.Count == 0
        && CardsLost.Count == 0
        && RelicsGained.Count == 0
        && RelicsLost.Count == 0
        && PotionsGained.Count == 0
        && PotionsLost.Count == 0;

    public static RoutePlanActualDelta Diff(
        RoutePlanResourceSnapshot before,
        RoutePlanResourceSnapshot after) => new(
        after.Gold - before.Gold,
        after.Hp - before.Hp,
        MultisetMinus(after.DeckCards, before.DeckCards),
        MultisetMinus(before.DeckCards, after.DeckCards),
        MultisetMinus(after.Relics, before.Relics),
        MultisetMinus(before.Relics, after.Relics),
        MultisetMinus(after.Potions, before.Potions),
        MultisetMinus(before.Potions, after.Potions));

    private static IReadOnlyList<string> MultisetMinus(
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

internal sealed record RoutePlanEntry(
    MapCoord Coord,
    RoutePlanChoice? Choice,
    RoutePlanActualDelta? Actual)
{
    public bool IsCompleted => Actual is not null;
}

internal sealed class RoutePlan
{
    public required int ActIndex { get; init; }
    public required MapCoord AnchorCoord { get; set; }
    public required IReadOnlyList<RoutePlanEntry> Entries { get; set; }
    public required RoutePlanResourceSnapshot Snapshot { get; set; }
    public RoutePlanPhase Phase { get; set; } = RoutePlanPhase.Active;
    public string? VoidReasonKey { get; set; }

    public RoutePlanEntry? Head => Entries.FirstOrDefault(entry => !entry.IsCompleted);
}

/// <summary>
/// Owns the current route plan and reconciles it against actual map progress:
/// entering the planned head replaces that entry with actual results; entering
/// anything else voids the whole plan.
/// </summary>
internal static class RoutePlanTracker
{
    private static RoutePlan? _plan;

    public static event Action? PlanChanged;

    public static RoutePlan? Current => _plan;

    private static void RaisePlanChanged() => PlanChanged?.Invoke();

    public static void Clear()
    {
        if (_plan is null)
            return;
        _plan = null;
        RaisePlanChanged();
    }

    /// <summary>Called when the map screen receives a map; plans are per act.</summary>
    public static void OnMapSet(RunState run)
    {
        if (_plan is not null && _plan.ActIndex != run.CurrentActIndex)
            _plan = null;
    }

    /// <summary>
    /// Click handling for an untravelable map node: start/extend/truncate the
    /// plan. Returns a transient error message, or null when the plan changed.
    /// </summary>
    public static string? ToggleNode(RunState run, MapCoord coord)
    {
        var plan = _plan;
        if (plan is null || plan.Phase == RoutePlanPhase.Void || plan.Head is null)
            return StartPlan(run, coord);

        var entries = plan.Entries;
        var headIndex = -1;
        for (var index = 0; index < entries.Count; index++)
        {
            if (!entries[index].IsCompleted)
            {
                headIndex = index;
                break;
            }
        }

        if (headIndex < 0)
            return StartPlan(run, coord);

        var clickedIndex = -1;
        for (var index = headIndex; index < entries.Count; index++)
        {
            if (entries[index].Coord == coord)
            {
                clickedIndex = index;
                break;
            }
        }

        if (clickedIndex >= 0)
        {
            var remaining = entries.Take(clickedIndex).ToArray();
            if (remaining.Length == headIndex)
            {
                _plan = null;
            }
            else
            {
                plan.Entries = remaining;
            }

            RaisePlanChanged();
            return null;
        }

        var tailPoint = FindMapPoint(run, entries[entries.Count - 1].Coord);
        if (tailPoint is null || tailPoint.Children.All(child => child.coord != coord))
            return "该房间与计划末端不相连"; // The room is not connected to the plan tail.

        plan.Entries = entries.Append(new RoutePlanEntry(coord, null, null)).ToArray();
        RaisePlanChanged();
        return null;
    }

    private static string? StartPlan(RunState run, MapCoord coord)
    {
        var current = run.CurrentMapPoint;
        if (current is null)
            return "当前位置未知，无法开始规划"; // Current position is unknown.

        var reachable = current.Children.Any(child => child.coord == coord)
                        || current.Children.Any(travelable => travelable.Children.Any(
                            grandChild => grandChild.coord == coord));
        if (!reachable)
            return "该房间无法从当前位置沿后续楼层到达"; // Not reachable along future floors.

        var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        _plan = new RoutePlan
        {
            ActIndex = run.CurrentActIndex,
            AnchorCoord = current.coord,
            Entries = [new RoutePlanEntry(coord, null, null)],
            Snapshot = RoutePlanResourceSnapshot.Capture(player)
        };
        RaisePlanChanged();
        return null;
    }

    /// <summary>
    /// Reconciles plan progress against the live run. Called every time the map
    /// opens (after a room completes): matching the head completes that entry
    /// with actual results; anything else voids the plan.
    /// </summary>
    public static void Reconcile(RunState run)
    {
        var plan = _plan;
        if (plan is null || plan.Phase == RoutePlanPhase.Void)
            return;

        if (plan.ActIndex != run.CurrentActIndex)
        {
            Void(plan, "act_changed");
            return;
        }

        var current = run.CurrentMapPoint;
        if (current is null || current.coord == plan.AnchorCoord)
            return;

        while (plan.Phase == RoutePlanPhase.Active
               && current.coord != plan.AnchorCoord
               && plan.Head is { } head)
        {
            if (current.coord == head.Coord)
            {
                CompleteHead(run, plan);
                continue;
            }

            if (current.Children.Any(child => child.coord == head.Coord))
            {
                // The player entered the unplanned travelable room that leads
                // into the plan; it is a legitimate step toward the head.
                plan.AnchorCoord = current.coord;
                plan.Snapshot = RoutePlanResourceSnapshot.Capture(
                    LocalContext.GetMe(run) ?? run.Players.FirstOrDefault());
                return;
            }

            Void(plan, "deviated");
            return;
        }

        if (plan.Phase == RoutePlanPhase.Active && current.coord != plan.AnchorCoord)
            Void(plan, "deviated");
    }

    private static void CompleteHead(RunState run, RoutePlan plan)
    {
        var head = plan.Head!;
        var headIndex = -1;
        for (var index = 0; index < plan.Entries.Count; index++)
        {
            if (ReferenceEquals(plan.Entries[index], head))
            {
                headIndex = index;
                break;
            }
        }

        var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        var now = RoutePlanResourceSnapshot.Capture(player);
        var delta = RoutePlanActualDelta.Diff(plan.Snapshot, now);

        var entries = plan.Entries.ToArray();
        entries[headIndex] = entries[headIndex] with { Actual = delta };
        plan.Entries = entries;
        plan.Snapshot = now;
        plan.AnchorCoord = run.CurrentMapPoint!.coord;
        RaisePlanChanged();
    }

    private static void Void(RoutePlan plan, string reasonKey)
    {
        plan.Phase = RoutePlanPhase.Void;
        plan.VoidReasonKey = reasonKey;
        RaisePlanChanged();
    }

    public static MapPoint? FindMapPoint(RunState run, MapCoord coord)
    {
        var map = run.Map;
        if (map is null)
            return null;

        return map.GetPointsInRow(coord.row)
            .FirstOrDefault(point => point.coord == coord);
    }
}
