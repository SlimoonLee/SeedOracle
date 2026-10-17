using System.Diagnostics;
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Rooms;
using SeedOracle.Integration;
using SeedOracle.UI;
using SeedOracle.Validation;

namespace SeedOracle.Forecasting;

// Branches own serialized runs. Neither rejected choices nor a budget stop may
// turn a partially executed shadow action into a downstream inventory.
internal sealed class MerchantChoiceBranchService(
    int maximumWork = 128, int maximumStates = 24, int maximumMilliseconds = 600,
    CurrentEventChoiceContext? currentEvent = null)
{
    private readonly PlanningPredictionService planning = new();
    private readonly PlanningEventPredictionService events = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int work;
    private bool truncated;
    private readonly HashSet<string> unresolved = new(StringComparer.Ordinal);
    private IReadOnlyList<Branch>? roots;
    private bool Chinese => LocManager.Instance?.Language is "zhs" or "zht";
    internal bool HasBudget => work < maximumWork && clock.ElapsedMilliseconds < maximumMilliseconds;
    private string Text(string chinese, string english) => Chinese ? chinese : english;

    private sealed record Branch(PlanningPredictionService.StateSnapshot State, string Condition)
    {
        internal IReadOnlyList<ForecastChoiceFact> Facts { get; init; } = [];
        internal IReadOnlyList<string> DeckOnlyChoices { get; init; } = [];
        internal bool PreservesDeckEquivalence { get; init; } = true;
    }
    private sealed record Pending(Branch Branch, int Step, MapCoord? DepartedPoint = null);

    private bool CanWork()
    {
        if (work >= maximumWork || clock.ElapsedMilliseconds >= maximumMilliseconds)
        {
            truncated = true;
            return false;
        }
        work++;
        return true;
    }

    internal MerchantInventoryForecast Predict(Player player, RouteWorldline route,
        MerchantInventoryForecast baseline)
    {
        var outcomes = PredictTargets(player, route,
            (state, type) => type == RoomType.Shop ? planning.GenerateMerchant(state) : null,
            ForecastContentKey.Merchant);
        if (outcomes.Count == 0) return baseline;
        var reference = outcomes[0];
        return reference.Outcome with
        {
            ReferenceCondition = reference.Condition,
            ReferenceCaseCount = reference.CaseCount,
            ReferenceChoicePaths = reference.ChoicePaths,
            ChoiceBranches = outcomes.Skip(1).Select(branch => new MerchantChoiceBranch(branch.Condition, branch.Outcome)
                { CaseCount = branch.CaseCount, ChoicePaths = branch.ChoicePaths }).ToArray(),
            ChoicesTruncated = truncated,
            UnresolvedChoices = unresolved.Order(StringComparer.Ordinal).ToArray()
        };
    }

    internal CombatRewardDetails PredictRewards(Player player, RouteWorldline route, CombatRewardDetails baseline)
    {
        var outcomes = PredictTargets(player, route, (state, type) =>
        {
            if (!type.IsCombatRoom()) return null;
            var encounter = ResolveEncounter(state, type);
            var before = planning.Capture(state);
            using var generated = planning.GenerateCombatRewards(state, type, encounter);
            return CombatRewardBranchService.AddPossibilities(planning, before, type, encounter, generated.Detach(), () => HasBudget);
        }, ForecastContentKey.Combat);
        return outcomes.Count == 0 ? baseline : outcomes[0].Outcome with
        {
            ReferenceCondition = outcomes[0].Condition,
            ReferenceChoicePaths = outcomes[0].ChoicePaths,
            ChoiceBranches = outcomes.Skip(1).ToArray()
        };
    }

    internal TreasureRoomDetails PredictTreasure(Player player, RouteWorldline route, TreasureRoomDetails baseline)
    {
        var outcomes = PredictTargets(player, route,
            (state, type) => type == RoomType.Treasure ? planning.GenerateTreasure(state, false) : null,
            ForecastContentKey.Treasure);
        return outcomes.Count == 0 ? baseline : outcomes[0].Outcome with
        {
            ReferenceCondition = outcomes[0].Condition,
            ReferenceChoicePaths = outcomes[0].ChoicePaths,
            ChoiceBranches = outcomes.Skip(1).ToArray()
        };
    }

    private IReadOnlyList<RewardChoiceBranch<T>> PredictTargets<T>(Player player, RouteWorldline route,
        Func<PlanningPredictionService.State, RoomType, T?> generate, Func<T, string> key) where T : class
    {
        using var workSample = ForecastWorkProfile.Measure("choice.expand");
        if (route.Path.Length == 0 || route.Path.Length == 1 && currentEvent is null)
            return [];
        unresolved.Clear();
        truncated = false;
        if (roots is null)
        {
            var input = planning.Capture(planning.CreateState((MegaCrit.Sts2.Core.Runs.RunState)player.RunState, player));
            roots = currentEvent is null ? [new Branch(input, "")]
                : PlanningEventPredictionService.MatchesCurrentInitialPage(planning.Restore(input), currentEvent)
                    ? ExpandEvent(input, currentEvent.Canonical, "") : [];
        }
        var results = new List<RewardChoiceBranch<T>>();
        // Complete a concrete reference route for each current-room option
        // before exploring the much larger set of future reward picks.
        foreach (var root in roots)
        {
            if (!CanWork()) break;
            var result = FollowReference(root, route, generate);
            if (result is not null) results.Add(result);
        }
        var pending = new Stack<Pending>(roots.Reverse().Select(root => new Pending(root, 0)));
        while (pending.Count > 0 && results.Count < maximumStates && CanWork())
        {
            var current = pending.Pop();
            var state = planning.Restore(current.Branch.State);
            if (current.DepartedPoint is { } departed)
                state.Run.AddVisitedMapCoord(departed);
            using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
            var point = route.Path.Steps[current.Step].Point;
            state.Run.ActFloor = point.coord.row + 1;
            var type = ResolveRoom(state, point);
            var deckOnly = current.Branch.DeckOnlyChoices.Count > 0
                && OrdinaryCardChoiceEquivalence.CanContinue(state, type, point, current.Step == route.Path.Length - 1)
                ? current.Branch.DeckOnlyChoices : [];
            var prefix = current.Branch.Condition;
            var floor = $"{point.coord.row + 1}F ";
            try
            {
                if (current.Step == route.Path.Length - 1)
                {
                    if (type == route.TargetRoomType && generate(state, type) is { } outcome)
                        results.Add(new RewardChoiceBranch<T>(prefix, outcome)
                            { ChoicePaths = [new(prefix, OutcomeFacts(current.Branch.Facts, deckOnly))] });
                    continue;
                }
                IReadOnlyList<Branch> branches;
                if (type.IsCombatRoom())
                {
                    var encounter = ResolveEncounter(state, type);
                    branches = ExpandCombat(planning.Capture(state), type, encounter, floor);
                }
                else
                {
                    var model = type == RoomType.Event
                        ? point.PointType == MapPointType.Ancient ? state.Run.Act._rooms.Ancient
                            : new EventQueueSimulation(state.Run, state.Run.IterateHookListeners(null).ToArray()).Consume()
                        : null;
                    state.Run.AppendToMapPointHistory(point.PointType, type, model?.Id);
                    branches = type switch
                    {
                        RoomType.Event when model is not null => ExpandEvent(planning.Capture(state), model, floor),
                        RoomType.Treasure => ExpandTreasure(state, floor),
                        RoomType.Shop => ExpandShop(state, floor, IsImmediatelyBeforeFixedCombat(route, current.Step)),
                        RoomType.RestSite => ExpandRest(state, floor),
                        _ => [new Branch(planning.Capture(state), floor + type)]
                    };
                }
                foreach (var branch in branches.Reverse())
                {
                    // Visiting the departed point is the only state change
                    // between this branch and the next step. Defer it until
                    // the queued branch is actually restored, instead of
                    // restoring and serializing every candidate immediately
                    // (including candidates the budget will never reach).
                    pending.Push(new Pending(new Branch(branch.State, Join(prefix, branch.Condition))
                    {
                        Facts = [.. current.Branch.Facts, .. BranchFacts(branch)],
                        DeckOnlyChoices = [.. (branch.PreservesDeckEquivalence ? deckOnly : []), .. branch.DeckOnlyChoices]
                    }, current.Step + 1, point.coord));
                }
            }
            catch (Exception exception)
            {
                unresolved.Add(floor + exception.GetBaseException().Message);
            }
        }
        truncated |= pending.Count > 0;
        return ShopPurchaseChoices.CompleteCoverage(results).GroupBy(result => key(result.Outcome))
            .Select(group =>
            {
                var conditions = group.Select(result => result.Condition).Distinct(StringComparer.Ordinal).ToArray();
                var label = string.Join(Text("；或 ", "; or "), conditions);
                var paths = group.SelectMany(result => result.ChoicePaths).DistinctBy(path =>
                    JsonSerializer.Serialize(path.Facts.Select(fact => new
                        { fact.Key, fact.Value, fact.Domain, fact.EquivalentValues, fact.Shop }))).ToArray();
                return new RewardChoiceBranch<T>(label, group.First().Outcome)
                {
                    CaseCount = paths.Length,
                    ChoicePaths = paths
                };
            }).ToArray();
    }

    private RewardChoiceBranch<T>? FollowReference<T>(Branch root, RouteWorldline route,
        Func<PlanningPredictionService.State, RoomType, T?> generate) where T : class
    {
        var state = planning.Restore(root.State);
        var condition = root.Condition;
        var facts = new List<ForecastChoiceFact>(BranchFacts(root));
        var deckOnly = new List<string>(root.DeckOnlyChoices);
        try
        {
            for (var index = 0; index < route.Path.Length; index++)
            {
                if (!CanWork()) return null;
                using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
                var point = route.Path.Steps[index].Point;
                state.Run.ActFloor = point.coord.row + 1;
                var type = ResolveRoom(state, point);
                if (deckOnly.Count > 0
                    && !OrdinaryCardChoiceEquivalence.CanContinue(state, type, point, index == route.Path.Length - 1))
                    deckOnly.Clear();
                if (index == route.Path.Length - 1)
                    return type == route.TargetRoomType && generate(state, type) is { } outcome
                        ? new RewardChoiceBranch<T>(condition, outcome)
                            { ChoicePaths = [new(condition, OutcomeFacts(facts, deckOnly))] } : null;
                var floor = $"{point.coord.row + 1}F ";
                if (type.IsCombatRoom())
                {
                    using var rewards = planning.GenerateCombatRewards(state, type, ResolveEncounter(state, type));
                    var deckOnlyReward = OrdinaryCardChoiceEquivalence.CanForgetPick(state, rewards);
                    var choice = new RoutePlanChoice.Combat(rewards.CardRewardGroups.Select((group, slot) =>
                        new RoutePlanChoice.CardReward(slot, [new CardRewardPick(AlternativeId: "Skip")])).ToArray(), null, false);
                    var applied = RoutePlanForecastService.ApplyCombatRewards(state, rewards, choice);
                    if (applied.CardRewardGroups.Any(group => !group.ChoiceComplete)) return null;
                    var choices = CombatFacts(choice, applied, floor, state.Player.Deck.Cards.Any(card => card is TheHunt));
                    facts.AddRange(choices);
                    if (deckOnlyReward)
                        deckOnly.AddRange(choices.Where(fact => fact.Kind == ForecastChoiceKind.CardReward).Select(fact => fact.Key));
                    AdvanceEncounter(state, type);
                    condition = Join(condition, floor + Text("跳过卡牌和遗物", "skip cards and relics")
                        + (state.Player.Deck.Cards.Any(card => card is TheHunt) ? Text("，狩猎未触发", ", no Hunt trigger") : ""));
                }
                else
                {
                    var model = type == RoomType.Event
                        ? point.PointType == MapPointType.Ancient ? state.Run.Act._rooms.Ancient
                            : new EventQueueSimulation(state.Run, state.Run.IterateHookListeners(null).ToArray()).Consume()
                        : null;
                    state.Run.AppendToMapPointHistory(point.PointType, type, model?.Id);
                    if (type == RoomType.Event && model is not null)
                    {
                        var branch = ExpandEvent(planning.Capture(state), model, floor, firstOnly: true).FirstOrDefault();
                        if (branch is null) return null;
                        state = planning.Restore(branch.State);
                        condition = Join(condition, branch.Condition);
                        facts.AddRange(BranchFacts(branch));
                    }
                    else if (type == RoomType.Treasure)
                    {
                        var chest = planning.GenerateTreasure(state, false);
                        PlayerCmd.GainGold(chest.Gold, state.Player).GetAwaiter().GetResult();
                        condition = Join(condition, floor + Text("跳过宝箱遗物", "skip chest relic"));
                        facts.Add(TreasureFact(state, chest, floor, "skip", Text("跳过遗物", "skip relic")));
                    }
                    else if (type == RoomType.Shop)
                    {
                        var stock = planning.GenerateMerchant(state);
                        var scope = ShopPurchaseChoices.Describe(state, stock, FloorNumber(floor),
                            IsImmediatelyBeforeFixedCombat(route, index));
                        condition = Join(condition, floor + Text("不购买", "buy nothing"));
                        facts.Add(ShopPurchaseChoices.Fact(scope, "skip", Text("不购买", "buy nothing"), Chinese));
                    }
                    else if (type == RoomType.RestSite)
                    {
                        planning.EnterRestSite(state);
                        condition = Join(condition, floor + Text("不锻造", "no smith"));
                        facts.Add(ActionFact("rest", floor, "skip", Text("不锻造", "no smith")));
                    }
                }
                state.Run.AddVisitedMapCoord(point.coord);
            }
        }
        catch (Exception exception) { unresolved.Add(exception.GetBaseException().Message); }
        return null;
    }

    private static void AdvanceEncounter(PlanningPredictionService.State state, RoomType type)
    {
        var rooms = state.Run.Act._rooms;
        if (type == RoomType.Monster) rooms.normalEncountersVisited++;
        if (type == RoomType.Elite) rooms.eliteEncountersVisited++;
        if (type == RoomType.Boss) rooms.bossEncountersVisited++;
        state.CombatsAdvanced++;
    }

    internal IReadOnlyList<(PlanningPredictionService.StateSnapshot State, string Condition)> CombatBranches(
        PlanningPredictionService.StateSnapshot input, RoomType type, EncounterModel encounter) =>
        ExpandCombat(input, type, encounter, "").Select(branch => (branch.State, branch.Condition)).ToArray();

    private static ForecastChoiceFact Opaque(string condition) =>
        new("fixed:" + condition, "", condition, condition, [condition], Closed: false);

    private static IReadOnlyList<ForecastChoiceFact> OutcomeFacts(IEnumerable<ForecastChoiceFact> facts,
        IReadOnlyList<string> deckOnly) => facts.Select(fact => deckOnly.Contains(fact.Key)
            ? fact with { EquivalentValues = fact.Domain.Where(value => value != "alt:reroll").ToArray() }
            : fact).ToArray();

    private static IReadOnlyList<ForecastChoiceFact> BranchFacts(Branch branch) => branch.Facts.Count > 0
        ? branch.Facts : string.IsNullOrEmpty(branch.Condition) ? [] : [Opaque(branch.Condition)];

    private static int FloorNumber(string floor) => int.TryParse(floor.TrimEnd(' ', 'F'), out var value) ? value : 0;

    private ForecastChoiceFact ActionFact(string kind, string floor, string value, string label) =>
        new(kind + ":" + floor, Text($"{FloorNumber(floor)}层", floor), value, label, [], Closed: false);

    private ForecastChoiceFact TreasureFact(PlanningPredictionService.State state, TreasureRoomDetails treasure,
        string floor, string value, string label) =>
        new("chest:" + floor, Text($"{FloorNumber(floor)}层宝箱", floor + "chest"), value, label,
            ["skip", .. treasure.Relics.Select(item => item.Id.ToString())],
            Closed: state.Run.Players.Count == 1 && treasure.Relics.Count == 1);

    private IReadOnlyList<ForecastChoiceFact> CombatFacts(RoutePlanChoice.Combat choice,
        CombatRewardDetails rewards, string floor, bool hasHunt)
    {
        var facts = new List<ForecastChoiceFact>();
        var number = FloorNumber(floor);
        var room = $"combat:{floor}";
        if (hasHunt)
            facts.Add(new(room + ":hunt", floor + Text("狩猎次数", "Hunt count"),
                choice.HuntTriggers.ToString(), choice.HuntTriggers.ToString(), [], Closed: false));
        if (rewards.Relics.Count > 0)
            facts.Add(new(room + ":relic", floor + Text("遗物", "relic"), choice.TakeRelic ? "take" : "skip",
                Text(choice.TakeRelic ? "领取" : "跳过", choice.TakeRelic ? "take" : "skip"), ["take", "skip"]));
        foreach (var (action, order) in choice.OrderedActions.Select((action, order) => (action, order)))
        {
            var pick = choice.ChoiceForGroup(action.GroupIndex).Steps[action.StepIndex];
            var step = rewards.CardRewardGroups[action.GroupIndex].Steps[action.StepIndex];
            var domain = step.Alternatives.Select(item => "alt:" + item.OptionId.ToLowerInvariant())
                .Concat(step.Cards.Select((card, slot) => CardKey(card, slot))).ToArray();
            var slot = pick.CardIndex >= 0 ? pick.CardIndex : Enumerable.Range(0, step.Cards.Count)
                .FirstOrDefault(index => step.Cards[index].Id == pick.CardId, -1);
            var value = pick.AlternativeId is { } alternative ? "alt:" + alternative.ToLowerInvariant()
                : slot >= 0 ? CardKey(step.Cards[slot], slot) : "unknown";
            var title = pick.AlternativeId is { } id ? step.Alternatives.First(item =>
                    item.OptionId.Equals(id, StringComparison.OrdinalIgnoreCase)).Title
                : slot >= 0 ? ForecastItemFormatter.Format(step.Cards[slot].Item, false) : "?";
            facts.Add(new($"{room}:action:{order}:group:{action.GroupIndex}:step:{action.StepIndex}",
                Text($"{number}层第{action.GroupIndex + 1}组选牌", $"{number}F group {action.GroupIndex + 1}"),
                value, title, domain, Kind: ForecastChoiceKind.CardReward, Floor: number,
                Group: action.GroupIndex + 1, Step: action.StepIndex, GroupCount: rewards.CardRewardGroups.Count));
        }
        return facts;

        static string CardKey(RewardItemDetails card, int slot) => $"card:{slot}:{ForecastContentKey.Item(card.Item)}";
    }

    private IEnumerable<ForecastChoiceFact> EventFacts(EventModel model,
        IReadOnlyList<PlanningEventPredictionService.EventOptionDescriptor> options,
        PlanningEventPredictionService.EventOptionDescriptor option, IReadOnlyList<EventCardPick> picks,
        bool take, string floor, IReadOnlyList<EventPlanStep> previous)
    {
        var page = string.Join("/", previous.Select(step => step.TextKey + ":"
            + string.Join(",", step.CardPicks.Select(pick => $"{pick.SelectionStep}:{pick.DeckSlot}:{pick.SelectionOrder}"))
            + ":" + step.TakeRewards));
        var scope = $"event:{floor}:{model.Id}:{page}";
        var number = FloorNumber(floor);
        var heading = (number > 0 ? Text($"{number}层", $"{number}F ") : "") + model.Title.GetFormattedText();
        yield return new(scope + ":option", heading, OptionKey(option), option.Title,
            options.Where(item => !item.IsLocked).Select(OptionKey).Distinct().ToArray(),
            Kind: ForecastChoiceKind.EventOption, Floor: number);
        foreach (var selection in option.CardSelections)
        {
            var selected = picks.Where(pick => pick.SelectionStep == selection.SelectionStep)
                .OrderBy(pick => pick.SelectionOrder).ToArray();
            var candidates = selected.Select(pick => selection.Candidates.First(card => card.CardId == pick.CardId
                && card.DeckSlot == pick.DeckSlot)).ToArray();
            var value = selected.Length == 0 ? "none" : string.Join(",", candidates.Select(CandidateKey));
            var labels = candidates.Select(card => card.Title
                    + (card.Upgraded && !card.Title.Contains('+') ? "+" : "")
                    + (card.Enchantment is { } enchantment ? $" ({enchantment})" : "")
                    + (selection.Candidates.Count(item => item.Title == card.Title && item.Upgraded == card.Upgraded
                        && item.Enchantment == card.Enchantment) > 1
                        ? Text($"（牌组第{card.DeckSlot + 1}张）", $" (deck slot {card.DeckSlot + 1})") : ""));
            var domain = selection.Candidates.Select(CandidateKey)
                .Concat(selection.MinSelect == 0 ? new[] { "none" } : []).ToArray();
            yield return new($"{scope}:option:{option.TextKey}:cards:{selection.SelectionStep}", Text("选牌", "card"),
                value, selected.Length == 0 ? Text("不选牌", "none") : string.Join("、", labels), domain,
                Closed: selection.MaxSelect == 1, Kind: ForecastChoiceKind.EventCard, Floor: number);
        }
        if (option.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice)
            yield return new(scope + ":reward", Text("奖励", "rewards"), take ? "take" : "skip",
                Text(take ? "领取" : "跳过", take ? "take" : "skip"), ["take", "skip"]);

        static string OptionKey(PlanningEventPredictionService.EventOptionDescriptor item) => $"{item.Index}:{item.TextKey}:{item.Title}";
        static string CandidateKey(PlanningEventPredictionService.EventCardCandidate card) =>
            $"{card.CardId}:{card.DeckSlot}:{card.Upgraded}:{card.Enchantment}";
    }

    private IReadOnlyList<Branch> ExpandCombat(PlanningPredictionService.StateSnapshot input,
        RoomType type, EncounterModel encounter, string floor)
    {
        var pending = new Queue<RoutePlanChoice.Combat>();
        var initial = planning.Restore(input);
        var huntLimit = initial.Player.Deck.Cards.Any(card => card is TheHunt)
            ? CombatRewardBranchService.MaximumHuntPreview : 0;
        if (huntLimit > 0)
            unresolved.Add("狩猎仅枚举触发 0–3 次；更多次数须指定条件 / Hunt counts above 3 require a plan");
        for (var hunt = 0; hunt <= huntLimit; hunt++)
        {
            pending.Enqueue(new RoutePlanChoice.Combat([], null, true) { HuntTriggers = hunt });
            if (type == RoomType.Elite)
                pending.Enqueue(new RoutePlanChoice.Combat([], null, false) { HuntTriggers = hunt });
        }
        var completed = new List<Branch>();
        while (pending.Count > 0 && completed.Count < maximumStates && CanWork())
        {
            var choice = pending.Dequeue();
            var state = planning.Restore(input);
            using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
            try
            {
                using var generated = planning.GenerateCombatRewards(state, type, encounter,
                    assumedHuntTriggers: choice.HuntTriggers);
                var deckOnlyReward = OrdinaryCardChoiceEquivalence.CanForgetPick(state, generated);
                var result = RoutePlanForecastService.ApplyCombatRewards(state, generated, choice);
                if (result.CardRewardGroups.Any(group => group.Steps.Any(step => step.RejectedChoice)))
                    continue;
                var open = result.CardRewardGroups.Select((group, index) => (group, index))
                    .Where(item => !item.group.ChoiceComplete).ToArray();
                if (open.Length == 0)
                {
                    AdvanceEncounter(state, type);
                    var actions = choice.OrderedActions.Select(action =>
                    {
                        var pick = choice.ChoiceForGroup(action.GroupIndex).Steps[action.StepIndex];
                        var step = result.CardRewardGroups[action.GroupIndex].Steps[action.StepIndex];
                        var title = pick.AlternativeId is { } alternative
                            ? step.Alternatives.FirstOrDefault(item => item.OptionId.Equals(alternative, StringComparison.OrdinalIgnoreCase))?.Title
                            : step.Cards.FirstOrDefault(item => item.Id == pick.CardId)?.Name;
                        return Text($"第{action.GroupIndex + 1}组选「{title}」", $"group {action.GroupIndex + 1}: {title}");
                    });
                    var relics = result.Relics.Count > 0
                        ? Text(choice.TakeRelic ? "领取遗物；" : "跳过遗物；", choice.TakeRelic ? "take relic; " : "skip relic; ") : "";
                    var hunt = huntLimit > 0 ? Text($"狩猎触发{choice.HuntTriggers}次；", $"Hunt x{choice.HuntTriggers}; ") : "";
                    var facts = CombatFacts(choice, result, floor, huntLimit > 0);
                    completed.Add(new Branch(planning.Capture(state), floor + hunt + relics + string.Join(" > ", actions))
                    {
                        Facts = facts,
                        DeckOnlyChoices = deckOnlyReward && choice.OrderedActions.Count == result.CardRewardGroups.Count
                            && choice.OrderedActions.All(action => action.StepIndex == 0
                                && choice.ChoiceForGroup(action.GroupIndex).Steps[action.StepIndex].AlternativeId is not "REROLL")
                            && (result.Relics.Count == 0 || !choice.TakeRelic)
                            ? facts.Where(fact => fact.Kind == ForecastChoiceKind.CardReward).Select(fact => fact.Key).ToArray() : [],
                        PreservesDeckEquivalence = result.Relics.Count == 0 || !choice.TakeRelic
                    });
                    continue;
                }
                // Every open group can be acted on next, retaining interleaved
                // reroll/pick order rather than taking a Cartesian product.
                foreach (var (group, index) in open)
                {
                    var step = group.Steps[^1];
                    var options = step.Alternatives.Select(alternative =>
                            new CardRewardPick(AlternativeId: alternative.OptionId))
                        .Concat(step.Cards.Select((card, slot) =>
                            new CardRewardPick(slot, card.Id, CardStateKey: card.Item.StateKey)));
                    foreach (var pick in options)
                    {
                        if (pending.Count >= maximumWork) { truncated = true; break; }
                        pending.Enqueue(choice.WithCardRewardChoice(new RoutePlanChoice.CardReward(index,
                            [.. choice.ChoiceForGroup(index).Steps, pick])));
                    }
                }
            }
            catch (Exception exception) { unresolved.Add(floor + exception.GetBaseException().Message); }
        }
        truncated |= pending.Count > 0;
        return completed;
    }

    private IReadOnlyList<Branch> ExpandTreasure(PlanningPredictionService.State state, string floor)
    {
        var treasure = planning.GenerateTreasure(state, isPriorRoom: false);
        PlayerCmd.GainGold(treasure.Gold, state.Player).GetAwaiter().GetResult();
        var before = planning.Capture(state);
        var results = new List<Branch> { new(before, floor + Text("跳过宝箱遗物", "skip chest relic"))
            { Facts = [TreasureFact(state, treasure, floor, "skip", Text("跳过遗物", "skip relic"))] } };
        if (state.Run.Players.Count != 1)
            unresolved.Add("多人宝箱投票尚未枚举 / multiplayer chest votes");
        foreach (var relic in treasure.Relics)
        {
            if (!CanWork()) break;
            var taken = planning.Restore(before);
            try
            {
                planning.AddRelic(taken, relic.Id);
                results.Add(new Branch(planning.Capture(taken), floor + Text("领取「", "take \"") + relic.Name + Text("」", "\""))
                    { Facts = [TreasureFact(state, treasure, floor, relic.Id.ToString(), Text("领取", "take ") + relic.Name)] });
            }
            catch (Exception exception) { unresolved.Add(floor + relic.Name + ": " + exception.GetBaseException().Message); }
        }
        return results;
    }

    private static bool IsImmediatelyBeforeFixedCombat(RouteWorldline route, int step) => step == route.Path.Length - 2
        && route.Path.Steps[^1].Point.PointType == MapPointType.Monster && route.TargetRoomType == RoomType.Monster;

    private IReadOnlyList<Branch> ExpandShop(PlanningPredictionService.State state, string floor, bool nextIsFixedCombat)
    {
        var beforeGeneration = planning.Capture(state);
        var stock = planning.GenerateMerchant(state);
        var choices = ShopPurchaseChoices.Describe(state, stock, FloorNumber(floor), nextIsFixedCombat);
        var before = planning.Capture(state);
        var results = new List<Branch> { new(before, floor + Text("不购买", "buy nothing"))
            { Facts = [ShopPurchaseChoices.Fact(choices, "skip", Text("不购买", "buy nothing"), Chinese)] } };
        foreach (var offer in choices.Offers.Where(offer => offer.Availability == ShopOfferAvailability.Available))
        {
            var item = offer.Item;
            if (choices.CardCombinationsEquivalent && item.Item.Kind == ForecastItemKind.Card) continue;
            if (!CanWork()) break;
            // Replay from before generation: the native inventory itself is
            // not serializable. Restoring the post-generation run and making a
            // new inventory would consume every initial stock roll twice.
            var purchase = planning.Restore(beforeGeneration);
            try
            {
                var selector = new MerchantRewardSelector(purchase, FloorNumber(floor), nextIsFixedCombat, Chinese);
                using (var shop = new PlanningMerchantSession(purchase))
                {
                    if (!shop.TryPurchase(ShopPurchaseChoices.Pick(stock, offer.Slot), selector, selector.HandleRewardsAsync)) continue;
                }
                results.Add(new Branch(planning.Capture(purchase), floor + Text("购买「", "buy \"") + item.Name + Text("」", "\""))
                    { Facts = [ShopPurchaseChoices.Fact(choices, offer.Value,
                        Text("购买", "buy ") + ForecastItemFormatter.Format(item.Item, false), Chinese), .. selector.Facts],
                        PreservesDeckEquivalence = false });
            }
            catch (Exception exception) { unresolved.Add(floor + item.Name + ": " + exception.GetBaseException().Message); }
        }
        if (!choices.CardCombinationsEquivalent)
            unresolved.Add("前置商店未枚举连续购买；每个购买分支含该次购买触发的补货与获得效果 / chained purchases are not enumerated; each purchase includes its native restock and acquisition effects");
        return results;
    }

    private IReadOnlyList<Branch> ExpandRest(PlanningPredictionService.State state, string floor)
    {
        planning.EnterRestSite(state);
        var before = planning.Capture(state);
        var results = new List<Branch> { new(before, floor + Text("不锻造", "no smith"))
            { Facts = [ActionFact("rest", floor, "skip", Text("不锻造", "no smith"))] } };
        foreach (var (card, slot) in state.Player.Deck.Cards.Select((card, slot) => (card, slot)))
        {
            if (!card.IsUpgradable) continue;
            if (!CanWork()) break;
            var upgraded = planning.Restore(before);
            if (planning.UpgradeCard(upgraded, card.Id, slot))
                results.Add(new Branch(planning.Capture(upgraded), floor + Text("锻造「", "smith \"") + card.Title + Text("」", "\""))
                    { Facts = [ActionFact("rest", floor, "smith:" + slot + ":" + card.Id,
                        Text("锻造", "smith ") + card.Title + Text($"（牌组第{slot + 1}张）", $" (deck slot {slot + 1})"))] });
        }
        unresolved.Add("休息点仅枚举牌组不变与锻造；其他动作须指定规划 / other rest actions require a plan");
        return results;
    }

    private IReadOnlyList<Branch> ExpandEvent(PlanningPredictionService.StateSnapshot input,
        EventModel model, string floor, bool firstOnly = false)
    {
        var pending = new Queue<(IReadOnlyList<EventPlanStep> Steps, string Label, IReadOnlyList<ForecastChoiceFact> Facts)>();
        pending.Enqueue(([], "", []));
        var results = new List<Branch>();
        while (pending.Count > 0 && results.Count < maximumStates && CanWork())
        {
            var (previous, label, facts) = pending.Dequeue();
            if (previous.Count >= 8) { truncated = true; continue; }
            var state = planning.Restore(input);
            var options = events.EnumerateEventOptions(state.Player, model, input, previousSteps: previous);
            if (options.Count == 0) unresolved.Add(floor + model.Id.Entry + ": no executable options");
            var executable = options.Where(option => !option.IsLocked && option.Capability.Kind is
                PlanningEventPredictionService.EventPlanningKind.Exact or PlanningEventPredictionService.EventPlanningKind.RewardChoice);
            var actions = executable.SelectMany(option => EventPicks(option.CardSelections)
                .SelectMany((picks, index) => (option.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice
                        ? new[] { true, false } : new[] { true })
                    .Select(take => (Option: option, Picks: picks, Take: take, Index: index))))
                .OrderBy(action => action.Index);
            foreach (var (option, picks, take, _) in actions)
            {
                    if (results.Count >= maximumStates) { truncated = true; return results; }
                    if (!CanWork()) return results;
                    var outcome = events.ExecuteEventOptionAsync(state.Player, model, option.Index, picks,
                        input, option, previous, take).GetAwaiter().GetResult();
                    if (!outcome.Ok || outcome.ShadowRun is null || outcome.ShadowPlayer is null)
                    {
                        unresolved.Add(floor + model.Id.Entry + ": " + (outcome.DenyReason ?? option.Title));
                        continue;
                    }
                    var selected = picks.Select(pick => option.CardSelections
                        .First(selection => selection.SelectionStep == pick.SelectionStep).Candidates
                        .First(card => card.CardId == pick.CardId && card.DeckSlot == pick.DeckSlot).Title);
                    var action = Text("选「", "choose \"") + option.Title + Text("」", "\"")
                        + (picks.Count > 0 ? Text("，选牌「", ", cards \"") + string.Join("、", selected) + Text("」", "\"") : "")
                        + (option.Capability.Kind == PlanningEventPredictionService.EventPlanningKind.RewardChoice
                            ? Text(take ? "，领取奖励" : "，跳过奖励", take ? ", take rewards" : ", skip rewards") : "");
                    var condition = Join(label, action);
                    var nextFacts = facts.Concat(EventFacts(model, options, option, picks, take, floor, previous)).ToArray();
                    if (outcome.Finished)
                    {
                        var next = planning.Restore(input);
                        planning.ApplyEventState(next, outcome.ShadowRun, outcome.ShadowPlayer.NetId);
                        if (!next.Player.Creature.IsDead)
                        {
                            results.Add(new Branch(planning.Capture(next), floor + model.Title.GetFormattedText() + ": " + condition)
                                { Facts = nextFacts });
                            if (firstOnly) return results;
                        }
                    }
                    else
                        pending.Enqueue((outcome.CompletedSteps, condition, nextFacts));
            }
        }
        truncated |= pending.Count > 0;
        return results;
    }

    private IEnumerable<IReadOnlyList<EventCardPick>> EventPicks(
        IReadOnlyList<PlanningEventPredictionService.EventCardSelectionDescriptor> selections)
    {
        IEnumerable<IReadOnlyList<EventCardPick>> choices = new[] { (IReadOnlyList<EventCardPick>)Array.Empty<EventCardPick>() };
        foreach (var selection in selections)
        {
            var partial = new List<IReadOnlyList<EventCardPick>>();
            foreach (var prefix in choices)
            foreach (var count in Enumerable.Range(selection.MinSelect, selection.MaxSelect - selection.MinSelect + 1))
            foreach (var slots in OrderedSlots(selection.Candidates.Count, count))
            {
                if (partial.Count >= maximumStates) { truncated = true; break; }
                partial.Add(prefix.Concat(slots.Select((slot, order) => new EventCardPick(selection.SelectionStep,
                    selection.Candidates[slot].CardId, selection.Candidates[slot].DeckSlot, order))).ToArray());
            }
            choices = partial;
        }
        return choices;
    }

    private IEnumerable<int[]> OrderedSlots(int total, int count, int[]? prefix = null)
    {
        prefix ??= [];
        if (prefix.Length == count) { yield return prefix; yield break; }
        for (var slot = 0; slot < total; slot++)
            if (!prefix.Contains(slot))
                foreach (var result in OrderedSlots(total, count, [.. prefix, slot]))
                    yield return result;
    }

    internal static RoomType ResolveRoom(PlanningPredictionService.State state, MapPoint point)
    {
        if (point.PointType == MapPointType.Unknown)
        {
            var blacklist = state.Run.CurrentMapPointHistoryEntry?.HasRoomOfType(RoomType.Shop) == true
                            || point.Children.Count > 0 && point.Children.All(child => child.PointType == MapPointType.Shop);
            return state.Run.Odds.UnknownMapPoint.Roll(blacklist ? [RoomType.Shop] : [], state.Run);
        }
        return point.PointType switch
        {
            MapPointType.Monster => RoomType.Monster, MapPointType.Elite => RoomType.Elite,
            MapPointType.Boss => RoomType.Boss, MapPointType.Treasure => RoomType.Treasure,
            MapPointType.Shop => RoomType.Shop, MapPointType.RestSite => RoomType.RestSite,
            MapPointType.Ancient => RoomType.Event, _ => RoomType.Unassigned
        };
    }

    internal static EncounterModel ResolveEncounter(PlanningPredictionService.State state, RoomType type)
    {
        var rooms = state.Run.Act._rooms;
        return type switch
        {
            RoomType.Monster => rooms.normalEncounters[rooms.normalEncountersVisited % rooms.normalEncounters.Count],
            RoomType.Elite => rooms.eliteEncounters[rooms.eliteEncountersVisited % rooms.eliteEncounters.Count],
            RoomType.Boss when rooms.bossEncountersVisited > 0 && state.Run.Act.SecondBossEncounter is { } second => second,
            _ => state.Run.Act.BossEncounter
        };
    }

    private static string Join(string prefix, string next) => prefix.Length == 0 ? next : prefix + " > " + next;
}
