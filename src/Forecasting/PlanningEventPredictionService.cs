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
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Random;
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
    private static int _brainLeechEnumerationLogged;

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
        IReadOnlyList<string> HoverTips)
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

    internal sealed class EventCombatDrops
    {
        public Forecast<CombatRewardDetails>? Rewards { get; set; }
        public List<string> Labels { get; } = [];
        public string? Error { get; set; }
    }

    private static readonly MethodInfo GenerateInitialEventOptionsMethod = typeof(EventModel)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(method => method.Name == "GenerateInitialOptionsWrapper"
                          && method.GetParameters().Length == 0);

    private static readonly MethodInfo SetEventStateMethod = typeof(EventModel)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(method => method.Name == "SetEventState"
                          && method.GetParameters().Length == 2);

    private static readonly HashSet<string> EventExecutionDenyList = new(StringComparer.Ordinal)
    {
        "CrystalSphere",
        "BattlewornDummy",
        "PunchOff",
        "Amalgamator"
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
        ("RoomFullOfCheese", "INITIAL.options.GORGE", EventPlanningKind.CardOrUiChoice),
        ("BrainLeech", "RIP", EventPlanningKind.CardOrUiChoice),
        ("DenseVegetation", "FIGHT", EventPlanningKind.SpecialCombat),
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
        if (entryName.Contains("BattlewornDummy", StringComparison.Ordinal)
            || entryName.Contains("PunchOff", StringComparison.Ordinal))
        {
            return new EventPlanningCapability(EventPlanningKind.SpecialCombat, "event_combat");
        }
        if (entryName.Contains("Amalgamator", StringComparison.Ordinal))
            return new EventPlanningCapability(EventPlanningKind.CardOrUiChoice, "awaits_ui_frames");

        // These options open a native card-selection screen, but the planner
        // can provide the same card list and replay the selected deck slots on
        // an isolated shadow run. They are therefore exact once the UI has a
        // complete card pick recorded.
        if (HasSupportedCardSelection(entryName, option.TextKey))
            return new EventPlanningCapability(EventPlanningKind.Exact, "card_selection");

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
        PlanningPredictionService.StateSnapshot? plannedState = null)
    {
        try
        {
            _ = InitializeShadowEvent(livePlayer, canonical, plannedState, out var shadowEvent);
            var shadowPlayer = shadowEvent.Owner
                               ?? throw new InvalidOperationException("shadow event has no owner");
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
                var selections = BuildCardSelections(
                    entryName,
                    option.TextKey,
                    shadowEvent,
                    shadowPlayer);
                var capability = selections.Count > 0
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
                    BuildOptionHoverTips(option)));

                if (IsGeneratedCardSelection(entryName, option.TextKey)
                    && Interlocked.Exchange(ref _brainLeechEnumerationLogged, 1) == 0)
                {
                    Entry.Logger.Info(
                        $"[PlanEvent] BrainLeech card choice recognized: key={option.TextKey}, "
                        + $"candidates={selections.FirstOrDefault()?.Candidates.Count ?? 0}, "
                        + $"capability={capability.Kind}");
                }
            }

            return descriptors;
        }
        catch (Exception exception)
        {
            Entry.Logger.Error($"[PlanEvent] option enumeration failed: {exception}");
            return [];
        }
        finally
        {
            ShadowIsolation.Exit();
        }
    }

    private static bool HasSupportedCardSelection(string entryName, string textKey) =>
        IsGeneratedCardSelection(entryName, textKey)
        || BuildCardSelectionRule(entryName, textKey) is not null;

    private static bool IsGeneratedCardSelection(string entryName, string textKey) =>
        textKey.Contains("SHARE_KNOWLEDGE", StringComparison.OrdinalIgnoreCase)
        && (entryName.Contains("BrainLeech", StringComparison.OrdinalIgnoreCase)
            || textKey.Contains("BRAIN_LEECH", StringComparison.OrdinalIgnoreCase));

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

    private enum CardSelectionRuleKind
    {
        Remove,
        Upgrade,
        Transform,
        Enchant,
        GenericTransform
    }

    private sealed record CardSelectionRule(
        CardSelectionRuleKind Kind,
        int MinSelect,
        int MaxSelect,
        string? EnchantmentEntry,
        int EnchantmentAmount,
        CardType? TypeFilter,
        bool BasicOnly);

    /// <summary>
    /// Maps native event actions to the card-selection request they make. The
    /// game keeps these filters in private event methods, so this small table
    /// mirrors the shipped event source and is intentionally keyed by the
    /// stable event/option text keys rather than localized text.
    /// </summary>
    private static CardSelectionRule? BuildCardSelectionRule(string entryName, string textKey)
    {
        var key = textKey.ToUpperInvariant();
        if (entryName.Equals("AromaOfChaos", StringComparison.Ordinal))
            return key.Contains("LET_GO")
                ? TransformRule(1)
                : key.Contains("MAINTAIN_CONTROL") ? UpgradeRule(1) : null;
        if (entryName.Equals("DoorsOfLightAndDark", StringComparison.Ordinal))
            return key.Contains("DARK") ? RemoveRule(1) : null;
        if (entryName.Equals("FieldOfManSizedHoles", StringComparison.Ordinal))
            return key.Contains("RESIST")
                ? RemoveRule(2)
                : key.Contains("ENTER_YOUR_HOLE") ? EnchantRule("PerfectFit", 1, 1) : null;
        if (entryName.Equals("GraveOfTheForgotten", StringComparison.Ordinal))
            return key.Contains("CONFRONT") ? EnchantRule("SoulsPower", 1, 1) : null;
        if (entryName.Equals("LuminousChoir", StringComparison.Ordinal))
            return key.Contains("REACH_INTO_THE_FLESH") ? RemoveRule(2) : null;
        if (entryName.Equals("MorphicGrove", StringComparison.Ordinal))
            return key.Contains("GROUP") ? TransformRule(2) : null;
        if (entryName.Equals("SapphireSeed", StringComparison.Ordinal))
            return key.Contains("EAT")
                ? UpgradeRule(1)
                : key.Contains("PLANT") ? EnchantRule("Sown", 1, 1) : null;
        if (entryName.Equals("SelfHelpBook", StringComparison.Ordinal))
        {
            if (key.Contains("READ_THE_BACK"))
                return EnchantRule("Sharp", 1, 1, CardType.Attack, 2);
            if (key.Contains("READ_PASSAGE"))
                return EnchantRule("Nimble", 1, 1, CardType.Skill, 2);
            if (key.Contains("READ_ENTIRE_BOOK"))
                return EnchantRule("Swift", 1, 1, CardType.Power, 2);
        }
        if (entryName.Equals("SpiralingWhirlpool", StringComparison.Ordinal))
            return key.Contains("OBSERVE") ? EnchantRule("Spiral", 1, 1) : null;
        if (entryName.Equals("SpiritGrafter", StringComparison.Ordinal))
            return key.Contains("REJECTION") ? UpgradeRule(1) : null;
        if (entryName.Equals("StoneOfAllTime", StringComparison.Ordinal))
            return key.Contains("PUSH") ? EnchantRule("Vigorous", 1, 1, null, 8) : null;
        if (entryName.Equals("Symbiote", StringComparison.Ordinal))
            return key.Contains("APPROACH")
                ? EnchantRule("Corrupted", 1, 1)
                : key.Contains("KILL_WITH_FIRE") ? TransformRule(2) : null;
        if (entryName.Equals("WaterloggedScriptorium", StringComparison.Ordinal))
        {
            if (key.Contains("TENTACLE_QUILL"))
                return EnchantRule("Steady", 1, 1);
            if (key.Contains("PRICKLY_SPONGE"))
                return EnchantRule("Steady", 2, 2);
        }
        if (entryName.Equals("Wellspring", StringComparison.Ordinal))
            return key.Contains("BATHE") ? RemoveRule(1) : null;
        if (entryName.Equals("WhisperingHollow", StringComparison.Ordinal))
            return key.Contains("HUG") ? TransformRule(1) : null;
        if (entryName.Equals("WoodCarvings", StringComparison.Ordinal))
        {
            if (key.Contains("SNAKE"))
                return EnchantRule("Slither", 1, 1);
            if (key.Contains("BIRD") || key.Contains("TORUS"))
                return new CardSelectionRule(CardSelectionRuleKind.GenericTransform, 1, 1, null, 0, null, true);
        }
        if (entryName.Equals("ZenWeaver", StringComparison.Ordinal))
        {
            if (key.Contains("EMOTIONAL_AWARENESS"))
                return RemoveRule(1);
            if (key.Contains("ARACHNID_ACUPUNCTURE"))
                return RemoveRule(2);
        }
        if (entryName.Equals("Trial", StringComparison.Ordinal))
        {
            if (key.Contains("MERCHANT") && key.Contains("INNOCENT"))
                return UpgradeRule(2);
            if (key.Contains("NONDESCRIPT") && key.Contains("INNOCENT"))
                return TransformRule(2);
        }
        if (entryName.Equals("EndlessConveyor", StringComparison.Ordinal))
            return key.Contains("JELLY_LIVER") ? TransformRule(1) : null;

        return null;

        static CardSelectionRule RemoveRule(int count) =>
            new(CardSelectionRuleKind.Remove, count, count, null, 0, null, false);
        static CardSelectionRule UpgradeRule(int count) =>
            new(CardSelectionRuleKind.Upgrade, count, count, null, 0, null, false);
        static CardSelectionRule TransformRule(int count) =>
            new(CardSelectionRuleKind.Transform, count, count, null, 0, null, false);
        static CardSelectionRule EnchantRule(
            string entry,
            int minSelect,
            int maxSelect,
            CardType? type = null,
            int amount = 1) =>
            new(CardSelectionRuleKind.Enchant, minSelect, maxSelect, entry, amount, type, false);
    }

    private static IReadOnlyList<EventCardSelectionDescriptor> BuildCardSelections(
        string entryName,
        string textKey,
        EventModel shadowEvent,
        Player shadowPlayer)
    {
        if (IsGeneratedCardSelection(entryName, textKey))
            return BuildGeneratedCardSelection(shadowEvent, shadowPlayer);

        var rule = BuildCardSelectionRule(entryName, textKey);
        if (rule is null)
            return [];

        EnchantmentModel? enchantment = null;
        if (rule.EnchantmentEntry is { } enchantmentEntry)
        {
            enchantment = ModelDb.DebugEnchantments.FirstOrDefault(model =>
                model.Id.Entry.Equals(enchantmentEntry, StringComparison.OrdinalIgnoreCase));
            if (enchantment is null)
                return [];
        }

        var candidates = shadowPlayer.Deck.Cards
            .Select((card, slot) => (Card: card, Slot: slot))
            .Where(pair =>
            {
                var card = pair.Card;
                if (rule.TypeFilter is { } type && card.Type != type)
                    return false;
                if (rule.BasicOnly && card.Rarity != CardRarity.Basic)
                    return false;
                return rule.Kind switch
                {
                    CardSelectionRuleKind.Remove => card.IsRemovable,
                    CardSelectionRuleKind.Upgrade => card.IsUpgradable,
                    CardSelectionRuleKind.Transform => card.IsTransformable,
                    CardSelectionRuleKind.GenericTransform => card.IsTransformable,
                    CardSelectionRuleKind.Enchant => enchantment?.CanEnchant(card) == true,
                    _ => false
                };
            })
            .Select(pair => new EventCardCandidate(
                pair.Card.Id,
                pair.Slot,
                pair.Card.Title,
                pair.Card.IsUpgraded,
                pair.Card.Enchantment?.Title.GetFormattedText()))
            .ToArray();

        var enchantmentAmount = rule.EnchantmentAmount;
        if (entryName.Equals("StoneOfAllTime", StringComparison.Ordinal)
            && rule.EnchantmentEntry is "Vigorous")
        {
            try
            {
                enchantmentAmount = shadowEvent.DynamicVars["PushVigorousAmount"].IntValue;
            }
            catch
            {
                // Keep the canonical fallback for older event builds.
            }
        }

        var kind = rule.Kind switch
        {
            CardSelectionRuleKind.Remove => EventCardSelectionKind.Remove,
            CardSelectionRuleKind.Upgrade => EventCardSelectionKind.Upgrade,
            CardSelectionRuleKind.Transform or CardSelectionRuleKind.GenericTransform => EventCardSelectionKind.Transform,
            CardSelectionRuleKind.Enchant => EventCardSelectionKind.Enchant,
            _ => EventCardSelectionKind.Generic
        };
        var label = kind switch
        {
            EventCardSelectionKind.Remove => "删除 / Remove",
            EventCardSelectionKind.Upgrade => "强化 / Upgrade",
            EventCardSelectionKind.Transform => "转化 / Transform",
            EventCardSelectionKind.Enchant => $"附魔 {enchantment?.Title.GetFormattedText()}"
                                               + (enchantmentAmount > 1 ? $" x{enchantmentAmount}" : string.Empty)
                                               + " / Enchant",
            _ => "选择卡牌 / Choose card"
        };
        return [new EventCardSelectionDescriptor(
            0,
            kind,
            label,
            rule.MinSelect,
            rule.MaxSelect,
             candidates)];
    }

    /// <summary>
    /// Brain Leech creates five fresh character cards, then asks the player to
    /// add exactly one to the deck. Generate them from the same shadow state
    /// and native reward options used by the event so the card list and RNG
    /// remain aligned with execution.
    /// </summary>
    private static IReadOnlyList<EventCardSelectionDescriptor> BuildGeneratedCardSelection(
        EventModel shadowEvent,
        Player shadowPlayer)
    {
        var count = shadowEvent.DynamicVars["FromCardChoiceCount"].IntValue;
        var cards = CardFactory.CreateForReward(
                shadowPlayer,
                count,
                CardCreationOptions.ForNonCombatWithDefaultOdds(
                    new[] { shadowPlayer.Character.CardPool }))
            .ToList();
        var candidates = cards
            .Select(result => new EventCardCandidate(
                result.Card.Id,
                -1,
                result.Card.Title,
                result.Card.IsUpgraded,
                result.Card.Enchantment?.Title.GetFormattedText()))
            .ToArray();

        return [new EventCardSelectionDescriptor(
            0,
            EventCardSelectionKind.Generic,
            "加入牌组 / Add to deck",
            1,
            1,
            candidates)];
    }

    internal async Task<EventExecutionOutcome> ExecuteEventOptionAsync(
        Player livePlayer,
        EventModel canonicalEvent,
        int optionIndex,
        IReadOnlyList<EventCardPick>? plannedCardPicks,
        PlanningPredictionService.StateSnapshot? plannedState = null)
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
            var snapshot = plannedState?.Run ?? RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = RunState.FromSerializable(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(plannedState?.PlayerNetId ?? livePlayer.NetId)
                               ?? throw new InvalidOperationException(
                                   $"shadow snapshot lacks player {livePlayer.NetId}");
            ShadowIsolation.Enter(shadowPlayer);

            InitShadowEventOn(shadowRun, shadowPlayer, canonicalEvent, out var shadowEvent);
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

            var capability = GetEventPlanningCapability(entryName, options[optionIndex]);
            if (capability.Kind != EventPlanningKind.Exact)
            {
                outcome.DenyReason = capability.Kind switch
                {
                    EventPlanningKind.RewardChoice => "该选项还需要指定奖励取舍，暂不推进后续世界线",
                    EventPlanningKind.CardOrUiChoice => "该选项还需要指定卡牌或界面选择，暂不推进后续世界线",
                    EventPlanningKind.SpecialCombat => "该选项会进入特殊战斗，请改用胜利掉落/战损估算",
                    EventPlanningKind.Minigame => "该选项会进入小游戏，请改用专用布局预测",
                    EventPlanningKind.RunEnding => "该选项会终止当前跑局",
                    _ => "该选项暂时无法无头预演"
                };
                return outcome;
            }

            var before = Capture(shadowPlayer);
            using (CardSelectCmd.PushSelector(new ScriptedCardSelector(plannedCardPicks)))
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
        catch (CardSelectionRequiredException)
        {
            outcome.DenyReason = "该选项还需要指定卡牌，当前计划不会默认选择第一张牌";
            return outcome;
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            outcome.DenyReason = $"无头执行失败，已降级：{root.Message}";
            Entry.Logger.Error($"[PlanEvent] exec {entryName}[{optionIndex}] failed: {exception}");
            return outcome;
        }
        finally
        {
            NonInteractiveMode.AutoSlayerCheck = previousCheck;
            ShadowIsolation.Exit();
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
            var shadowRun = RunState.FromSerializable(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(plannedState?.PlayerNetId ?? livePlayer.NetId)
                               ?? throw new InvalidOperationException("shadow snapshot lacks player");
            ShadowIsolation.Enter(shadowPlayer);

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
        finally
        {
            ShadowIsolation.Exit();
        }
    }

    internal EventCombatDrops PredictEventCombatVictoryDrops(
        Player livePlayer,
        EventModel canonicalEvent,
        int optionIndex,
        PlanningPredictionService.StateSnapshot? plannedState = null)
    {
        var drops = new EventCombatDrops();
        try
        {
            var encounter = canonicalEvent.CanonicalEncounter
                            ?? throw new InvalidOperationException("event has no encounter");
            var snapshot = plannedState?.Run ?? RunManager.Instance.ToSave(preFinishedRoom: null);
            var planning = new PlanningPredictionService();
            var state = planning.Restore(new PlanningPredictionService.StateSnapshot(
                snapshot,
                plannedState?.PlayerNetId ?? livePlayer.NetId,
                plannedState?.MerchantVisits ?? 0,
                plannedState?.CombatsAdvanced ?? 0,
                plannedState?.TreasureRoomsAdvanced ?? 0,
                plannedState?.LavaRockTriggered ?? false,
                plannedState?.WongosTicketTriggered ?? false,
                plannedState?.HistoricalTreasureRooms ?? 0));
            ShadowIsolation.Enter(state.Player);

            var mutableEncounter = encounter.ToMutable();
            mutableEncounter.GenerateMonstersWithSlots(state.Run);
            state.CombatsAdvanced++;
            var rewards = planning.GenerateCombatRewards(state, RoomType.Monster, mutableEncounter);
            drops.Rewards = Forecast<CombatRewardDetails>.CurrentWorldline(
                rewards,
                PredictionDependency.Rewards
                | PredictionDependency.RelicGrabBag
                | PredictionDependency.CardRarityOdds
                | PredictionDependency.PlayerState,
                "Native event-combat victory reward simulation.");

            var entryName = canonicalEvent.GetType().Name;
            switch (entryName)
            {
                case "BattlewornDummy":
                    if (optionIndex == 0)
                    {
                        var pool = state.Player.Character.PotionPool
                            .GetUnlockedPotions(state.Player.UnlockState)
                            .Concat(ModelDb.PotionPool<SharedPotionPool>()
                                .GetUnlockedPotions(state.Player.UnlockState));
                        var potion = state.Player.PlayerRng.Rewards.NextItem(pool);
                        drops.Labels.Add(potion is null
                            ? "战后药水：无可用"
                            : $"战后药水：{potion.Title.GetFormattedText()}");
                    }
                    else if (optionIndex == 1)
                    {
                        drops.Labels.Add("战后：随机强化 2 张可升级牌（牌组变化需在事件选项确定后接入）");
                    }
                    else if (optionIndex == 2)
                    {
                        var relic = RelicFactory.PullNextRelicFromFront(state.Player);
                        drops.Labels.Add(relic is null
                            ? "战后遗物：无"
                            : $"战后遗物：{relic.Title.GetFormattedText()}");
                    }
                    break;
                case "PunchOff":
                    drops.Labels.Add("额外奖励：遗物 + 药水（接受时结算）");
                    break;
            }

            return drops;
        }
        catch (Exception exception)
        {
            drops.Error = exception.GetBaseException().Message;
            return drops;
        }
        finally
        {
            ShadowIsolation.Exit();
        }
    }

    private RunState InitializeShadowEvent(
        Player livePlayer,
        EventModel canonical,
        PlanningPredictionService.StateSnapshot? plannedState,
        out EventModel shadowEvent)
    {
        var snapshot = plannedState?.Run ?? RunManager.Instance.ToSave(preFinishedRoom: null);
        var shadowRun = RunState.FromSerializable(snapshot);
        var shadowPlayer = shadowRun.GetPlayer(plannedState?.PlayerNetId ?? livePlayer.NetId)
                           ?? throw new InvalidOperationException("shadow snapshot lacks player");
        ShadowIsolation.Enter(shadowPlayer);
        InitShadowEventOn(shadowRun, shadowPlayer, canonical, out shadowEvent);
        return shadowRun;
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
        var playerSlot = shadowEvent.IsShared
            ? 0
            : shadowPlayer.RunState.GetPlayerSlotIndex(shadowPlayer);
        shadowEvent.Rng = new Rng(
            (ulong)((long)shadowPlayer.RunState.Rng.Seed + playerSlot)
            + StringHelper.GetDeterministicHashCode(shadowEvent.Id.Entry));
        shadowEvent.CalculateVars();
        _ = GenerateAndSetInitialOptions(shadowEvent, "planning");
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

    private sealed class ScriptedCardSelector : ICardSelector
    {
        private readonly IReadOnlyList<IReadOnlyList<EventCardPick>> selections;
        private int selectionIndex;

        public ScriptedCardSelector(IReadOnlyList<EventCardPick>? picks)
        {
            selections = (picks ?? [])
                .GroupBy(pick => Math.Max(pick.SelectionStep, 0))
                .OrderBy(group => group.Key)
                .Select(group => (IReadOnlyList<EventCardPick>)group
                    .OrderBy(pick => pick.SelectionOrder)
                    .ToArray())
                .ToArray();
        }

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options,
            int minSelect,
            int maxSelect)
        {
            var list = options.ToList();
            if (list.Count == 0)
                return Task.FromResult((IEnumerable<CardModel>)Array.Empty<CardModel>());

            if (selectionIndex >= selections.Count)
                throw new CardSelectionRequiredException();

            var picks = selections[selectionIndex++];
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
            if (options.Count == 0)
                return default;

            if (selectionIndex >= selections.Count)
                throw new CardSelectionRequiredException();

            var picks = selections[selectionIndex++];
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
                if (slotMatch is not null)
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
