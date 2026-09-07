using System.Diagnostics;
using System.Reflection;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Api;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Forecasting;

/// <summary>
/// Native event simulation used by route planning. It deliberately does not
/// read Random Foreseer's registries or reward context. Every operation starts
/// from the plan's serialized state, so event card choices and other effects
/// are carried into the next combat reward roll.
/// </summary>
internal sealed class PlanningEventPredictionService
{
    internal enum EventPlanningKind
    {
        Exact,
        RewardChoice,
        CardOrUiChoice,
        SpecialCombat,
        Minigame,
        RunEnding
    }

    internal sealed record EventPlanningCapability(EventPlanningKind Kind, string ReasonKey);

    internal enum EventCardSelectionKind
    {
        Remove,
        Upgrade,
        Transform,
        Enchant,
        Generic
    }

    internal sealed record EventCardCandidate(
        ModelId CardId,
        int DeckSlot,
        string Title,
        bool Upgraded,
        string? Enchantment);

    /// <summary>
    /// One native CardSelectCmd request. Min/Max are retained so the UI and
    /// scripted selector enforce the same cardinality as the game.
    /// </summary>
    internal sealed record EventCardSelectionDescriptor(
        int SelectionStep,
        EventCardSelectionKind Kind,
        string Label,
        int MinSelect,
        int MaxSelect,
        IReadOnlyList<EventCardCandidate> Candidates);

    internal sealed record EventOptionDescriptor(
        int Index,
        string Title,
        string Description,
        string TextKey,
        bool IsLocked,
        EventPlanningCapability Capability,
        IReadOnlyList<EventCardSelectionDescriptor> CardSelections,
        IReadOnlyList<string> HoverTips,
        string? CombatEncounterId = null)
    {
        public EventOptionDescriptor(
            int index,
            string title,
            string textKey,
            bool isLocked,
            EventPlanningCapability capability)
            : this(index, title, string.Empty, textKey, isLocked, capability, [], [])
        {
        }

        public EventOptionDescriptor(
            int index,
            string title,
            string textKey,
            bool isLocked,
            EventPlanningCapability capability,
            IReadOnlyList<EventCardSelectionDescriptor> cardSelections)
            : this(index, title, string.Empty, textKey, isLocked, capability, cardSelections, [])
        {
        }
    }

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
        public List<(string TextKey, string Option)> NextOptions { get; } = [];
        public bool Finished { get; set; }
        public bool OptionOutOfRange { get; set; }
        public RunState? ShadowRun { get; set; }
        public Player? ShadowPlayer { get; set; }
        public EventModel? ShadowEvent { get; set; }
        public IReadOnlyList<EventPlanStep> CompletedSteps { get; set; } = [];
        public string? CombatEncounterId { get; set; }
        public bool CombatShouldResumeAfterCombat { get; set; }
        public IReadOnlyList<Reward> CombatExtraRewards { get; set; } = [];
        public CombatRewardDetails? CombatRewards { get; set; }
        public PlanningPredictionService.StateSnapshot? BeforeCombatRewards { get; set; }
    }

    internal sealed record PlayerSnapshot(
        int Gold,
        int Hp,
        List<string> Deck,
        List<string> Relics,
        List<string> Potions);

    internal sealed class CrystalSphereLayout
    {
        public int DivinationCount { get; init; }
        public int Cost { get; init; }
        public List<string> Rows { get; init; } = [];
        public List<string> Legend { get; init; } = [];
        public string? Error { get; init; }
    }

    internal PlanningPredictionService.StateSnapshot CaptureCombatState(EventExecutionOutcome outcome)
    {
        if (outcome.ShadowRun is null || outcome.ShadowPlayer is null)
            throw new InvalidOperationException("事件战斗尚未生成影子状态。");
        return new PlanningPredictionService.StateSnapshot(
            PlanningPredictionService.CreateSerializableRun(outcome.ShadowRun),
            outcome.ShadowPlayer.NetId,
            0,
            0,
            0,
            false,
            false,
            0);
    }

    internal async Task<bool> ApplyCombatSimulationToEventOutcomeAsync(
        EventExecutionOutcome outcome,
        PlanningPredictionService.StateSnapshot combatState,
        CombatSimulationReference reference,
        EncounterModel encounter,
        RoutePlanChoice.Combat rewardChoice)
    {
        if (outcome.ShadowRun is null || outcome.ShadowPlayer is null)
            return false;
        var planning = new PlanningPredictionService();
        // Rewards and the event retain their owning player, so resolve on
        // this fresh execution graph instead of swapping only its RunState.
        var state = new PlanningPredictionService.State
        {
            Run = outcome.ShadowRun,
            Player = outcome.ShadowPlayer
        };
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        if (!planning.ApplyCombatSimulationReference(
                state,
                combatState,
                reference,
                encounter,
                encounter.RoomType))
        {
            return false;
        }
        outcome.BeforeCombatRewards = planning.Capture(state);
        var room = new CombatRoom(encounter.ToMutable(), state.Run)
        {
            ParentEventId = outcome.ShadowEvent!.Id,
            ShouldResumeParentEventAfterCombat = outcome.CombatShouldResumeAfterCombat
        };
        foreach (var reward in outcome.CombatExtraRewards)
            room.AddExtraReward(state.Player, reward);
        var before = RoutePlanResourceSnapshot.Capture(state.Player);
        var resolvedRewards = new CombatRewardDetails(0, [], [], []);
        void ApplyRewards(CombatRewardDetails generated)
        {
            var groupOffset = resolvedRewards.CardRewardGroups.Count;
            var potionOffset = resolvedRewards.Potions.Count;
            var choice = rewardChoice with
            {
                CardRewardChoices = rewardChoice.CardRewardChoices
                    .Where(pick => pick.BundleIndex >= groupOffset)
                    .Select(pick => pick with { BundleIndex = pick.BundleIndex - groupOffset }).ToArray(),
                PotionChoice = rewardChoice.PotionChoice is { } potions
                    ? potions with { TakenPotions = potions.TakenPotions
                        .Where(index => index >= potionOffset).Select(index => index - potionOffset).ToArray() }
                    : null
            };
            var applied = RoutePlanForecastService.ApplyCombatRewards(state, generated, choice, before);
            resolvedRewards = new CombatRewardDetails(
                resolvedRewards.Gold + applied.Gold,
                resolvedRewards.CardRewardGroups.Concat(applied.CardRewardGroups).ToArray(),
                resolvedRewards.Potions.Concat(applied.Potions).ToArray(),
                resolvedRewards.Relics.Concat(applied.Relics).ToArray()) { AppliedDelta = applied.AppliedDelta };
        }
        using (var generated = planning.GenerateCombatRewards(state, encounter.RoomType, encounter, room))
            ApplyRewards(generated);

        if (outcome.CombatShouldResumeAfterCombat)
        {
            // Resume can offer another native reward set (including cards).
            // Keep its group and potion indices continuous with combat drops.
            using var rewardHandler = ShadowIsolation.UseRewardHandler(async rewards =>
            {
                await rewards.GenerateWithoutOffering();
                using var generated = PlanningPredictionService.DescribeRewards(rewards);
                ApplyRewards(generated);
            });
            using var selector = ShadowIsolation.UseSelector(new ScriptedCardSelector([]));
            await outcome.ShadowEvent.Resume(room);
        }
        outcome.CombatRewards = resolvedRewards;
        outcome.Finished = !outcome.CombatShouldResumeAfterCombat || outcome.ShadowEvent.IsFinished;
        outcome.NextOptions.Clear();
        if (!outcome.Finished)
            foreach (var option in outcome.ShadowEvent.CurrentOptions)
                outcome.NextOptions.Add((option.TextKey, option.Title.GetFormattedText()));
        return true;
    }

    private static readonly MethodInfo GenerateInitialEventOptionsMethod = typeof(EventModel)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(method => method.Name == "GenerateInitialOptionsWrapper"
                          && method.GetParameters().Length == 0);

    private static readonly MethodInfo SetEventStateMethod = typeof(EventModel)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(method => method.Name == "SetEventState"
                          && method.GetParameters().Length == 2);

    private static readonly FieldInfo CombatSynchronizerField = typeof(EventModel)
        .GetField("_combatSynchronizer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(EventModel).FullName, "_combatSynchronizer");

    private static readonly HashSet<string> EventExecutionDenyList = new(StringComparer.Ordinal)
    {
        "CrystalSphere",
    };

    private static readonly (string Event, string Key, EventPlanningKind Kind)[] OptionDenyList =
    [
        ("DenseVegetation", "INITIAL.options.REST", EventPlanningKind.RewardChoice),
        ("Wellspring", "INITIAL.options.BOTTLE", EventPlanningKind.RewardChoice),
        ("DrowningBeacon", "INITIAL.options.BOTTLE", EventPlanningKind.RewardChoice),
        ("ColorfulPhilosophers", "INITIAL.options.", EventPlanningKind.RewardChoice),
        ("PotionCourier", "INITIAL.options.", EventPlanningKind.RewardChoice),
        ("TheLegendsWereTrue", "INITIAL.options.SLOWLY_FIND_AN_EXIT", EventPlanningKind.RewardChoice),
        ("WhisperingHollow", "GOLD", EventPlanningKind.RewardChoice),
        ("WarHistorianRepy", "UNLOCK_CHEST", EventPlanningKind.RewardChoice),
        ("Trial", "DOUBLE_DOWN", EventPlanningKind.RunEnding)
    ];

    internal bool IsEventExecutionDenied(
        string entryName,
        IReadOnlyList<EventOption> options,
        int optionIndex)
    {
        if (optionIndex < 0 || optionIndex >= options.Count)
            return true;
        return GetEventPlanningCapability(entryName, options[optionIndex]).Kind != EventPlanningKind.Exact;
    }

    internal EventPlanningCapability GetEventPlanningCapability(string entryName, EventOption option)
    {
        if (entryName.Contains("CrystalSphere", StringComparison.Ordinal))
            return new EventPlanningCapability(EventPlanningKind.Minigame, "crystal_sphere");
        // Native discovery upgrades this to Exact once the selector is reached.
        if (entryName.Contains("Amalgamator", StringComparison.Ordinal))
            return new EventPlanningCapability(EventPlanningKind.CardOrUiChoice, "awaits_ui_frames");

        foreach (var (denyEvent, denyKey, kind) in OptionDenyList)
        {
            if (entryName.Contains(denyEvent, StringComparison.Ordinal)
                && option.TextKey.Contains(denyKey, StringComparison.OrdinalIgnoreCase))
            {
                return new EventPlanningCapability(kind, kind switch
                {
                    EventPlanningKind.RewardChoice => "reward_choice",
                    EventPlanningKind.CardOrUiChoice => "card_or_ui_choice",
                    EventPlanningKind.SpecialCombat => "event_combat",
                    EventPlanningKind.RunEnding => "run_ending",
                    _ => "unsupported"
                });
            }
        }

        return new EventPlanningCapability(EventPlanningKind.Exact, "exact");
    }

    internal IReadOnlyList<EventOptionDescriptor> EnumerateEventOptions(
        Player livePlayer,
        EventModel canonical,
        PlanningPredictionService.StateSnapshot? plannedState = null,
        int plannedOptionIndex = -1,
        IReadOnlyList<EventCardPick>? plannedCardPicks = null,
        IReadOnlyList<EventPlanStep>? previousSteps = null)
    {
        try
        {
            var snapshot = plannedState?.Run ?? RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = PlanningPredictionService.RestoreRun(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(plannedState?.PlayerNetId ?? livePlayer.NetId)
                               ?? throw new InvalidOperationException("shadow snapshot lacks player");
            using var isolation = ShadowIsolation.Enter(shadowPlayer, automateRewards: true);
            InitShadowEventOn(shadowRun, shadowPlayer, canonical, out var shadowEvent);
            ReplayPreviousStepsAsync(shadowEvent, previousSteps).GetAwaiter().GetResult();
            var entryName = canonical.GetType().Name;
            var descriptors = new List<EventOptionDescriptor>();
            foreach (var (option, index) in shadowEvent.CurrentOptions.Select((option, index) => (option, index)))
            {
                // EventModel.CalculateVars populates the event's DynamicVarSet,
                // but EventOption loc strings are created afterwards. Add the
                // variables to both fields before formatting so costs and
                // amounts in the native action description are resolved.
                shadowEvent.DynamicVars.AddTo(option.Title);
                shadowEvent.DynamicVars.AddTo(option.Description);
                // CardSelectCmd exposes every native deck/grid/reward request
                // through ICardSelector. Probe the option on a fresh shadow
                // run so generated cards and event-specific filters come from
                // the game itself instead of an ever-growing event table.
                var declaredCapability = GetEventPlanningCapability(entryName, option);
                var combatEncounterId = option.IsLocked
                    ? null
                    : TryCaptureNativeEventCombat(
                        livePlayer,
                        canonical,
                        index,
                        plannedState,
                        plannedOptionIndex == index ? plannedCardPicks : null,
                        previousSteps);
                var selections = option.IsLocked
                                     || declaredCapability.Kind is EventPlanningKind.Minigame or EventPlanningKind.RunEnding
                    ? []
                    : TryCaptureNativeCardSelections(
                        livePlayer,
                        canonical,
                        index,
                        plannedState,
                        plannedOptionIndex == index ? plannedCardPicks : null,
                        previousSteps) ?? [];
                var capability = combatEncounterId is not null
                    ? new EventPlanningCapability(EventPlanningKind.SpecialCombat, "event_combat")
                    : selections.Count > 0
                    ? new EventPlanningCapability(EventPlanningKind.Exact, "card_selection")
                    : GetEventPlanningCapability(entryName, option);
                descriptors.Add(new EventOptionDescriptor(
                    index,
                    NormalizeHoverTipText(option.Title.GetFormattedText()),
                    NormalizeHoverTipText(option.Description.GetFormattedText()),
                    option.TextKey,
                    option.IsLocked,
                    capability,
                    selections,
                    BuildOptionHoverTips(option),
                    combatEncounterId));


            }

            return descriptors;
        }
        catch (Exception exception)
        {
            Entry.Logger.Error($"[PlanEvent] option enumeration failed: {exception}");
            return [];
        }
    }

    /// <summary>
    /// Event options carry native hover tips for their costs, rewards, and
    /// explanatory text. Preserve those tips as plain localized lines so the
    /// planning dropdown can expose the same information without opening the
    /// real event UI or importing Random Foreseer's prediction tips.
    /// </summary>
    private static IReadOnlyList<string> BuildOptionHoverTips(EventOption option)
    {
        var lines = new List<string>();
        foreach (var tip in option.HoverTips)
        {
            if (tip.Id.Contains("RandomForeseer", StringComparison.OrdinalIgnoreCase))
                continue;

            var line = tip switch
            {
                CardHoverTip cardTip => cardTip.Card.Title,
                { CanonicalModel: CardModel card } => card.Title,
                { CanonicalModel: RelicModel relic } => relic.Title.GetFormattedText(),
                { CanonicalModel: PotionModel potion } => potion.Title.GetFormattedText(),
                { CanonicalModel: OrbModel orb } => orb.Title.GetFormattedText(),
                HoverTip textTip => CombineHoverTipText(textTip.Title, textTip.Description),
                _ => string.Empty
            };

            line = NormalizeHoverTipText(line);
            if (!string.IsNullOrWhiteSpace(line)
                && !lines.Contains(line, StringComparer.Ordinal))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static string CombineHoverTipText(string? title, string? description)
    {
        if (string.IsNullOrWhiteSpace(title))
            return description ?? string.Empty;
        if (string.IsNullOrWhiteSpace(description)
            || string.Equals(title, description, StringComparison.Ordinal))
        {
            return title;
        }

        return $"{title}：{description}";
    }

    private static string NormalizeHoverTipText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var withoutMarkup = StripMarkupTags(text);
        var normalized = string.Join(" ", withoutMarkup
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 240 ? normalized : normalized[..237] + "…";
    }

    private static string StripMarkupTags(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        var inTag = false;
        foreach (var character in text)
        {
            if (character == '[')
            {
                inTag = true;
                continue;
            }

            if (inTag)
            {
                if (character == ']')
                    inTag = false;
                continue;
            }

            result.Append(character);
        }

        return result.ToString();
    }

    /// <summary>
    /// Runs one event option on an isolated copy with a selector that records
    /// every request made by the native CardSelectCmd implementation. The
    /// selector returns the first legal cards only to let later requests in
    /// the same option materialize (for example two consecutive reward grids).
    /// The copy is discarded, so this probe never advances the live run.
    /// </summary>
    private static IReadOnlyList<EventCardSelectionDescriptor>? TryCaptureNativeCardSelections(
        Player livePlayer,
        EventModel canonical,
        int optionIndex,
        PlanningPredictionService.StateSnapshot? plannedState,
        IReadOnlyList<EventCardPick>? plannedCardPicks,
        IReadOnlyList<EventPlanStep>? previousSteps)
    {
        RecordingCardSelector? selector = null;
        try
        {
            var snapshot = plannedState?.Run ?? RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = PlanningPredictionService.RestoreRun(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(plannedState?.PlayerNetId ?? livePlayer.NetId)
                               ?? throw new InvalidOperationException("shadow snapshot lacks player");
            using var isolation = ShadowIsolation.Enter(shadowPlayer, automateRewards: true);

            InitShadowEventOn(shadowRun, shadowPlayer, canonical, out var shadowEvent);
            ReplayPreviousStepsAsync(shadowEvent, previousSteps).GetAwaiter().GetResult();
            if (optionIndex < 0 || optionIndex >= shadowEvent.CurrentOptions.Count)
                throw new ArgumentOutOfRangeException(nameof(optionIndex));

            selector = new RecordingCardSelector(shadowPlayer, plannedCardPicks);
            using var combatCapture = ShadowIsolation.CaptureEventCombats();
            using (ShadowIsolation.UseSelector(selector))
            {
                shadowEvent.CurrentOptions[optionIndex].Chosen().GetAwaiter().GetResult();
            }

            return selector.ToDescriptors();
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            Entry.Logger.Debug(
                $"[PlanEvent] native card request probe failed for {canonical.Id.Entry}[{optionIndex}]: "
                + $"{root.GetType().Name}: {root.Message}");
            // Event effects can throw after a native request has already been
            // emitted (BrainLeech RIP is the important case: the reward
            // action may not finish in a synchronous probe). Keep the request
            // list instead of discarding it with the exception.
            return selector is { } recorded && recorded.Count > 0
                ? recorded.ToDescriptors()
                : null;
        }
    }

    internal async Task<EventExecutionOutcome> ExecuteEventOptionAsync(
        Player livePlayer,
        EventModel canonicalEvent,
        int optionIndex,
        IReadOnlyList<EventCardPick>? plannedCardPicks,
        PlanningPredictionService.StateSnapshot? plannedState = null,
        EventOptionDescriptor? plannedDescriptor = null,
        IReadOnlyList<EventPlanStep>? previousSteps = null,
        bool? takeRewards = null,
        CombatSimulationReference? simulationReference = null,
        RoutePlanChoice.Combat? combatRewards = null)
    {
        var outcome = new EventExecutionOutcome();
        var entryName = canonicalEvent.GetType().Name;
        if (EventExecutionDenyList.Contains(entryName))
        {
            outcome.DenyReason = "该事件无法无头预演";
            return outcome;
        }

        try
        {
            var snapshot = plannedState?.Run ?? RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = PlanningPredictionService.RestoreRun(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(plannedState?.PlayerNetId ?? livePlayer.NetId)
                               ?? throw new InvalidOperationException(
                                   $"shadow snapshot lacks player {livePlayer.NetId}");
            using var isolation = ShadowIsolation.Enter(shadowPlayer, automateRewards: true);

            InitShadowEventOn(shadowRun, shadowPlayer, canonicalEvent, out var shadowEvent);
            var before = Capture(shadowPlayer);
            await ReplayPreviousStepsAsync(shadowEvent, previousSteps);
            var options = shadowEvent.CurrentOptions;
            if (optionIndex < 0 || optionIndex >= options.Count)
            {
                outcome.OptionOutOfRange = true;
                return outcome;
            }

            if (options[optionIndex].IsLocked)
            {
                outcome.DenyReason = "该事件选项当前已锁定，计划状态可能已经变化";
                return outcome;
            }
            if (plannedDescriptor is not null && (plannedDescriptor.Index != optionIndex
                || plannedDescriptor.TextKey != options[optionIndex].TextKey))
            {
                outcome.DenyReason = "事件选项已变化，请重新选择后再预演。";
                return outcome;
            }

            // The descriptor was generated from the same node snapshot and
            // contains the native selector requests for this exact option.
            // Prefer it over the legacy event capability table so a newly
            // shipped event can execute as soon as its native selector is
            // discoverable.
            var capability = plannedDescriptor is not null
                ? plannedDescriptor.Capability
                : GetEventPlanningCapability(entryName, options[optionIndex]);
            var canPrepareCombat = capability.Kind == EventPlanningKind.SpecialCombat
                                   && plannedDescriptor?.CombatEncounterId is not null;
            if (capability.Kind != EventPlanningKind.Exact
                && !(capability.Kind == EventPlanningKind.RewardChoice && takeRewards is not null)
                && !canPrepareCombat)
            {
                outcome.DenyReason = capability.Kind switch
                {
                    EventPlanningKind.RewardChoice => "该选项还需要指定奖励取舍，暂不推进后续世界线",
                    EventPlanningKind.CardOrUiChoice => "该选项还需要指定卡牌或界面选择，暂不推进后续世界线",
                    EventPlanningKind.SpecialCombat => "该选项会进入特殊战斗，请先运行模拟战斗",
                    EventPlanningKind.Minigame => "该选项会进入小游戏，请改用专用布局预测",
                    EventPlanningKind.RunEnding => "该选项会终止当前跑局",
                    _ => "该选项暂时无法无头预演"
                };
                return outcome;
            }

            var selectedTextKey = options[optionIndex].TextKey;
            using var combatCapture = ShadowIsolation.CaptureEventCombats();
            using (ShadowIsolation.UseSelector(new ScriptedCardSelector(plannedCardPicks), takeRewards ?? true))
            {
                await options[optionIndex].Chosen();
            }

            var combatRequest = ShadowIsolation.CurrentEventCombatCaptures is { Count: > 0 }
                ? ShadowIsolation.CurrentEventCombatCaptures[^1]
                : null;
            outcome.CombatEncounterId = combatRequest?.Encounter.Id.Entry;
            outcome.CombatShouldResumeAfterCombat = combatRequest?.ShouldResumeAfterCombat ?? false;
            outcome.CombatExtraRewards = combatRequest?.ExtraRewards ?? [];
            outcome.Finished = combatRequest is null && shadowEvent.IsFinished;
            if (combatRequest is null)
                foreach (var option in shadowEvent.CurrentOptions)
                    outcome.NextOptions.Add((option.TextKey, option.Title.GetFormattedText()));
            outcome.Ok = true;
            outcome.ShadowRun = shadowRun;
            outcome.ShadowPlayer = shadowPlayer;
            outcome.ShadowEvent = shadowEvent;
            outcome.CompletedSteps = (previousSteps ?? [])
                .Append(new EventPlanStep(selectedTextKey, (plannedCardPicks ?? []).ToArray(), takeRewards)
                {
                    SimulationReference = simulationReference,
                    CombatRewards = combatRewards
                }).ToArray();
            if (combatRequest is not null && simulationReference is not null
                && !await ApplyCombatSimulationToEventOutcomeAsync(
                    outcome, CaptureCombatState(outcome), simulationReference, combatRequest.Encounter,
                    combatRewards ?? new RoutePlanChoice.Combat([], null, true)))
            {
                outcome.Ok = false;
                outcome.DenyReason = "模拟战斗参照已过期，请重新模拟当前规划状态";
            }
            FillDeltas(outcome, before, Capture(shadowPlayer));
            return outcome;
        }
        catch (CardSelectionRequiredException)
        {
            outcome.Ok = false;
            outcome.DenyReason = "该选项还需要指定卡牌，当前计划不会默认选择第一张牌";
            return outcome;
        }
        catch (Exception exception)
        {
            outcome.Ok = false;
            var root = exception.GetBaseException();
            outcome.DenyReason = $"无头执行失败，已降级：{root.Message}";
            Entry.Logger.Error($"[PlanEvent] exec {entryName}[{optionIndex}] failed: {exception}");
            return outcome;
        }
    }

    private static async Task ReplayPreviousStepsAsync(EventModel model, IReadOnlyList<EventPlanStep>? steps)
    {
        foreach (var step in steps ?? [])
        {
            var option = model.CurrentOptions.FirstOrDefault(candidate => candidate.TextKey == step.TextKey);
            if (model.IsFinished || option is null || option.IsLocked)
                throw new InvalidOperationException($"事件路径已失效：{step.TextKey}，请从事件起点重新选择。");
            using var selector = ShadowIsolation.UseSelector(new ScriptedCardSelector(step.CardPicks), step.TakeRewards ?? true);
            using var capture = ShadowIsolation.CaptureEventCombats();
            await option.Chosen();
            if (ShadowIsolation.CurrentEventCombatCaptures is { Count: > 0 } captures)
            {
                var request = captures[^1];
                var service = new PlanningEventPredictionService();
                var outcome = new EventExecutionOutcome
                {
                    ShadowRun = (RunState)model.Owner!.RunState,
                    ShadowPlayer = model.Owner,
                    ShadowEvent = model,
                    CombatShouldResumeAfterCombat = request.ShouldResumeAfterCombat,
                    CombatExtraRewards = request.ExtraRewards
                };
                if (step.SimulationReference is null
                    || !await service.ApplyCombatSimulationToEventOutcomeAsync(
                        outcome, service.CaptureCombatState(outcome), step.SimulationReference,
                        request.Encounter, step.CombatRewards ?? new RoutePlanChoice.Combat([], null, true)))
                    throw new InvalidOperationException("前置事件战斗尚未完成模拟或参照已失效。");
            }
        }
    }

    internal CrystalSphereLayout PredictCrystalSphereLayout(
        Player livePlayer,
        EventModel canonical,
        int divinationCount,
        PlanningPredictionService.StateSnapshot? plannedState = null)
    {
        try
        {
            var snapshot = plannedState?.Run ?? RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = PlanningPredictionService.RestoreRun(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(plannedState?.PlayerNetId ?? livePlayer.NetId)
                               ?? throw new InvalidOperationException("shadow snapshot lacks player");
            using var isolation = ShadowIsolation.Enter(shadowPlayer);

            var shadowEvent = canonical.ToMutable();
            shadowEvent.Owner = shadowPlayer;
            var slot = shadowEvent.IsShared
                ? 0
                : shadowPlayer.RunState.GetPlayerSlotIndex(shadowPlayer);
            shadowEvent.Rng = new Rng(
                (ulong)((long)shadowPlayer.RunState.Rng.Seed + slot)
                + StringHelper.GetDeterministicHashCode(shadowEvent.Id.Entry));
            shadowEvent.CalculateVars();

            var cost = -1;
            try
            {
                cost = (int)shadowEvent.DynamicVars["UncoverFutureCost"].BaseValue;
            }
            catch
            {
                // Cost is informational; old event versions may omit it.
            }

            var minigame = new MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame(
                shadowPlayer, shadowEvent.Rng, divinationCount);
            var glyphs = new string[11, 11];
            for (var x = 0; x < 11; x++)
            for (var y = 0; y < 11; y++)
                glyphs[x, y] = "·";

            var legend = new List<string>();
            foreach (var item in minigame.Items)
            {
                var (glyph, color, label) = item switch
                {
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSphereRelic
                        => ("遗", "#FFDA36", "遗物"),
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSphereCardReward
                        => ("卡", "#5CB8FF", "卡牌奖励"),
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSpherePotion
                        => ("药", "#FF61C7", "药水"),
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSphereCurse
                        => ("咒", "#E669FF", "诅咒"),
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSphereGold
                        => ("金", "#FFA629", "金币"),
                    _ => ("?", "#FFFFFF", item.GetType().Name)
                };
                legend.Add($"[color={color}]{glyph}[/color]={label}({item.Size.X}x{item.Size.Y})");
                for (var dx = 0; dx < item.Size.X; dx++)
                for (var dy = 0; dy < item.Size.Y; dy++)
                {
                    var cx = item.Position.X + dx;
                    var cy = item.Position.Y + dy;
                    if (cx is >= 0 and < 11 && cy is >= 0 and < 11)
                        glyphs[cx, cy] = $"[color={color}]{glyph}[/color]";
                }
            }

            var rows = new List<string>();
            for (var y = 0; y < 11; y++)
            {
                var row = string.Empty;
                for (var x = 0; x < 11; x++)
                    row += glyphs[x, y];
                rows.Add(row);
            }

            return new CrystalSphereLayout
            {
                DivinationCount = divinationCount,
                Cost = cost,
                Rows = rows,
                Legend = legend
            };
        }
        catch (Exception exception)
        {
            return new CrystalSphereLayout
            {
                DivinationCount = divinationCount,
                Error = exception.GetBaseException().Message
            };
        }
    }

    private static void InitShadowEventOn(
        RunState shadowRun,
        Player shadowPlayer,
        EventModel canonical,
        out EventModel shadowEvent)
    {
        _ = shadowRun;
        shadowEvent = canonical.ToMutable();
        shadowEvent.Owner = shadowPlayer;
        CombatSynchronizerField.SetValue(
            shadowEvent,
            new EventCombatSynchronizer(shadowRun, shadowRun));
        var playerSlot = shadowEvent.IsShared
            ? 0
            : shadowPlayer.RunState.GetPlayerSlotIndex(shadowPlayer);
        shadowEvent.Rng = new Rng(
            (ulong)((long)shadowPlayer.RunState.Rng.Seed + playerSlot)
            + StringHelper.GetDeterministicHashCode(shadowEvent.Id.Entry));
        shadowEvent.CalculateVars();
        _ = GenerateAndSetInitialOptions(shadowEvent, "planning");
    }

    private static string? TryCaptureNativeEventCombat(
        Player livePlayer,
        EventModel canonical,
        int optionIndex,
        PlanningPredictionService.StateSnapshot? plannedState,
        IReadOnlyList<EventCardPick>? plannedCardPicks,
        IReadOnlyList<EventPlanStep>? previousSteps)
    {
        try
        {
            var snapshot = plannedState?.Run ?? RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = PlanningPredictionService.RestoreRun(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(plannedState?.PlayerNetId ?? livePlayer.NetId)
                               ?? throw new InvalidOperationException("shadow snapshot lacks player");
            using var isolation = ShadowIsolation.Enter(shadowPlayer, automateRewards: true);
            InitShadowEventOn(shadowRun, shadowPlayer, canonical, out var shadowEvent);
            ReplayPreviousStepsAsync(shadowEvent, previousSteps).GetAwaiter().GetResult();
            if (optionIndex < 0 || optionIndex >= shadowEvent.CurrentOptions.Count)
                return null;

            using var capture = ShadowIsolation.CaptureEventCombats();
            using var selector = ShadowIsolation.UseSelector(new RecordingCardSelector(shadowPlayer, plannedCardPicks));
            shadowEvent.CurrentOptions[optionIndex].Chosen().GetAwaiter().GetResult();
            return ShadowIsolation.CurrentEventCombatCaptures is { Count: > 0 } captures
                ? captures[^1].Encounter.Id.Entry
                : null;
        }
        catch (Exception exception)
        {
            Entry.Logger.Debug(
                $"[PlanEvent] native combat probe failed for {canonical.Id.Entry}[{optionIndex}]: "
                + $"{exception.GetBaseException().GetType().Name}: {exception.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// 0.13.11 returns a newly materialized list from GenerateInitialOptions.
    /// SetEventState must receive that list, and effects must then use the
    /// CurrentOptions instance owned by the event.
    /// </summary>
    private static IReadOnlyList<EventOption> GenerateAndSetInitialOptions(
        EventModel eventModel,
        string operation)
    {
        var generated = (IReadOnlyList<EventOption>?)GenerateInitialEventOptionsMethod.Invoke(eventModel, null)
                        ?? throw new InvalidOperationException(
                            $"Event {eventModel.Id} returned no initial options.");
        SetEventStateMethod.Invoke(eventModel, [eventModel.InitialDescription, generated]);
        var current = eventModel.CurrentOptions;
        Entry.Logger.Debug(
            $"[EventInit] {operation} {eventModel.Id.Entry}: generated={generated.Count}, current={current.Count}");
        return current;
    }

    private static PlayerSnapshot Capture(Player player) => new(
        player.Gold,
        player.Creature.CurrentHp,
        player.Deck.Cards
            .Select(card => $"{card.Title}{(card.IsUpgraded ? "+" : string.Empty)}")
            .ToList(),
        player.Relics.Select(relic => relic.Title.GetFormattedText()).ToList(),
        player.Potions.Select(potion => potion.Title.GetFormattedText()).ToList());

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

    private static void AddMissing(
        List<string> target,
        IReadOnlyList<string> source,
        IReadOnlyList<string> removed)
    {
        var remaining = removed.ToList();
        foreach (var item in source)
        {
            if (!remaining.Remove(item))
                target.Add(item);
        }
    }

    private sealed class CardSelectionRequiredException : Exception
    {
    }

    private sealed class RecordingCardSelector : ICardSelector
    {
        private readonly Player owner;
        private readonly List<RecordedRequest> requests = [];
        private readonly IReadOnlyDictionary<int, IReadOnlyList<EventCardPick>> plannedSelections;
        private int selectionStep;

        internal RecordingCardSelector(Player owner, IReadOnlyList<EventCardPick>? plannedPicks)
        {
            this.owner = owner;
            plannedSelections = (plannedPicks ?? [])
                .GroupBy(pick => Math.Max(pick.SelectionStep, 0))
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<EventCardPick>)group
                        .OrderBy(pick => pick.SelectionOrder)
                        .ToArray());
        }

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options,
            int minSelect,
            int maxSelect)
        {
            var list = options.ToList();
            var step = selectionStep++;
            requests.Add(new RecordedRequest(
                IsReward: false,
                InferSelectionKind(isReward: false),
                minSelect,
                maxSelect,
                list.Select(ToCandidate).ToArray()));

            if (TryResolvePlannedCards(step, list, minSelect, maxSelect, out var planned))
                return Task.FromResult<IEnumerable<CardModel>>(planned);

            // Until a step is planned, select the first legal entries only to
            // expose subsequent requests. A later refresh re-probes with the
            // user's actual earlier picks.
            var count = minSelect <= 0
                ? 0
                : Math.Min(Math.Min(minSelect, maxSelect), list.Count);
            return Task.FromResult<IEnumerable<CardModel>>(list.Take(count).ToArray());
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            var step = selectionStep++;
            requests.Add(new RecordedRequest(
                IsReward: true,
                EventCardSelectionKind.Generic,
                1,
                1,
                options.Select(result => result.Card).Select(ToCandidate).ToArray()));

            if (plannedSelections.TryGetValue(step, out var picks)
                && picks.Count == 1)
            {
                var pick = picks[0];
                var selected = options
                    .Select(option => option.Card)
                    .FirstOrDefault(card => SameCard(card, pick));
                if (selected is not null)
                    return new CardRewardSelection { card = selected };
            }

            if (options.Count > 0)
            {
                return new CardRewardSelection { card = options[0].Card };
            }

            return alternatives.Count > 0
                ? new CardRewardSelection { alternative = alternatives[0] }
                : default;
        }

        internal IReadOnlyList<EventCardSelectionDescriptor> ToDescriptors() =>
            requests
                .Select((request, step) => new EventCardSelectionDescriptor(
                    step,
                    request.Kind,
                    SelectionLabel(request.Kind, request.IsReward),
                    request.MinSelect,
                    request.MaxSelect,
                    request.Candidates))
                .ToArray();

        internal int Count => requests.Count;

        private bool TryResolvePlannedCards(
            int step,
            IReadOnlyList<CardModel> options,
            int minSelect,
            int maxSelect,
            out IReadOnlyList<CardModel> selected)
        {
            selected = [];
            if (!plannedSelections.TryGetValue(step, out var picks)
                || picks.Count < minSelect
                || picks.Count > maxSelect)
            {
                return false;
            }

            var resolved = new List<CardModel>(picks.Count);
            foreach (var pick in picks)
            {
                var card = options.FirstOrDefault(candidate =>
                    !resolved.Contains(candidate) && SameCard(candidate, pick));
                if (card is null)
                    return false;
                resolved.Add(card);
            }

            selected = resolved;
            return true;
        }

        private bool SameCard(CardModel card, EventCardPick pick)
        {
            if (!card.Id.Entry.Equals(pick.CardId.Entry, StringComparison.OrdinalIgnoreCase))
                return false;
            if (pick.DeckSlot < 0)
                return true;
            return ReferenceEquals(card.Owner, owner)
                   && pick.DeckSlot < owner.Deck.Cards.Count
                   && ReferenceEquals(owner.Deck.Cards[pick.DeckSlot], card);
        }

        private EventCardCandidate ToCandidate(CardModel card)
        {
            var deckSlot = -1;
            try
            {
                if (ReferenceEquals(card.Owner, owner))
                {
                    for (var index = 0; index < owner.Deck.Cards.Count; index++)
                    {
                        if (ReferenceEquals(owner.Deck.Cards[index], card))
                        {
                            deckSlot = index;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Generated cards can be detached from a deck; they keep the
                // -1 marker and are identified by their model ID.
            }

            return new EventCardCandidate(
                card.Id,
                deckSlot,
                card.Title,
                card.IsUpgraded,
                card.Enchantment?.Title.GetFormattedText());
        }

        private static EventCardSelectionKind InferSelectionKind(bool isReward)
        {
            if (isReward)
                return EventCardSelectionKind.Generic;

            var frames = new StackTrace().GetFrames();
            var methods = frames is null
                ? []
                : frames
                    .Select(frame => frame.GetMethod()?.Name ?? string.Empty)
                    .ToArray();
            if (methods.Any(name => name.Contains("FromDeckForRemoval", StringComparison.Ordinal)))
                return EventCardSelectionKind.Remove;
            if (methods.Any(name => name.Contains("FromDeckForUpgrade", StringComparison.Ordinal)))
                return EventCardSelectionKind.Upgrade;
            if (methods.Any(name => name.Contains("FromDeckForTransformation", StringComparison.Ordinal)))
                return EventCardSelectionKind.Transform;
            if (methods.Any(name => name.Contains("FromDeckForEnchantment", StringComparison.Ordinal)))
                return EventCardSelectionKind.Enchant;
            return EventCardSelectionKind.Generic;
        }

        private static string SelectionLabel(EventCardSelectionKind kind, bool isReward) =>
            isReward
                ? "卡牌奖励 / Card reward"
                : kind switch
                {
                    EventCardSelectionKind.Remove => "删除 / Remove",
                    EventCardSelectionKind.Upgrade => "强化 / Upgrade",
                    EventCardSelectionKind.Transform => "转化 / Transform",
                    EventCardSelectionKind.Enchant => "附魔 / Enchant",
                    _ => "选择卡牌 / Choose card"
                };

        private sealed record RecordedRequest(
            bool IsReward,
            EventCardSelectionKind Kind,
            int MinSelect,
            int MaxSelect,
            IReadOnlyList<EventCardCandidate> Candidates);
    }

    private sealed class ScriptedCardSelector : ICardSelector
    {
        private readonly IReadOnlyDictionary<int, IReadOnlyList<EventCardPick>> selections;
        private int selectionIndex;

        public ScriptedCardSelector(IReadOnlyList<EventCardPick>? picks)
        {
            selections = (picks ?? [])
                .GroupBy(pick => Math.Max(pick.SelectionStep, 0))
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyList<EventCardPick>)group
                        .OrderBy(pick => pick.SelectionOrder).ToArray());
        }

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options,
            int minSelect,
            int maxSelect)
        {
            var list = options.ToList();
            if (!selections.TryGetValue(selectionIndex++, out var picks))
            {
                if (minSelect > 0)
                    throw new CardSelectionRequiredException();
                picks = [];
            }
            if (picks.Count < minSelect || picks.Count > maxSelect)
            {
                throw new InvalidOperationException(
                    $"事件选牌数量 {picks.Count} 不符合游戏要求 {minSelect}..{maxSelect}。");
            }

            var selected = new List<CardModel>(picks.Count);
            foreach (var pick in picks)
            {
                var card = FindCard(list, pick, selected);
                if (card is null)
                {
                    throw new InvalidOperationException(
                        $"事件选牌找不到 {pick.CardId.Entry}（牌组槽位 {pick.DeckSlot}）。");
                }

                selected.Add(card);
            }

            return Task.FromResult<IEnumerable<CardModel>>(selected);
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (!selections.TryGetValue(selectionIndex++, out var picks))
                throw new CardSelectionRequiredException();
            if (picks.Count != 1)
                throw new InvalidOperationException("卡牌奖励计划必须只选择一张牌。");
            var pick = picks[0];
            var selected = options
                .Select(option => option.Card)
                .FirstOrDefault(card => card.Id == pick.CardId
                                        || card.Id.Entry == pick.CardId.Entry);
            if (selected is null)
                throw new InvalidOperationException($"卡牌奖励找不到 {pick.CardId.Entry}。");
            return new CardRewardSelection { card = selected, alternative = null };
        }

        private static CardModel? FindCard(
            IReadOnlyList<CardModel> options,
            EventCardPick pick,
            IReadOnlyCollection<CardModel> selected)
        {
            if (pick.DeckSlot >= 0)
            {
                var slotMatch = options.FirstOrDefault(card =>
                    !selected.Contains(card)
                    && DeckIndex(card) == pick.DeckSlot
                    && card.Id.Entry == pick.CardId.Entry);
                return slotMatch;
            }

            return options.FirstOrDefault(card =>
                !selected.Contains(card) && card.Id.Entry == pick.CardId.Entry);
        }

        private static int DeckIndex(CardModel card)
        {
            var deck = card.Owner?.Deck.Cards;
            if (deck is null)
                return -1;
            for (var index = 0; index < deck.Count; index++)
            {
                if (ReferenceEquals(deck[index], card))
                    return index;
            }

            return -1;
        }
    }
}
