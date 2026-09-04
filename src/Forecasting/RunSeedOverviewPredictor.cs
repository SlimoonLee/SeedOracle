using System.Reflection;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Validation;

namespace SeedOracle.Forecasting;

internal enum AncientOptionAccuracy
{
    Recorded,
    CurrentState,
    CurrentStateProjection,
    Unavailable
}

internal sealed record AncientOptionSummary(
    string Title,
    bool IsLocked,
    bool WasChosen);

internal sealed record AncientOptionReplacement(
    string Condition,
    IReadOnlyList<string> WhenConditionMet,
    IReadOnlyList<string> WhenConditionNotMet);

internal sealed record ActSeedOverview(
    int ActNumber,
    string ActTitle,
    string Ancient,
    IReadOnlyList<AncientOptionSummary> AncientOptions,
    IReadOnlyList<AncientOptionReplacement> AncientOptionReplacements,
    AncientOptionAccuracy AncientOptionAccuracy,
    string? AncientOptionFailure,
    string Boss,
    string? SecondBoss);

internal static class RunSeedOverviewPredictor
{
    private enum ConditionalOptionRule
    {
        BasicStrike,
        SwiftCards,
        InstinctCards,
        GoopyCards,
        RemovableCards,
        NoEventPet
    }

    private sealed record ConditionalRuleDefinition(
        ConditionalOptionRule Rule,
        string Condition);

    private sealed record AncientSimulation(
        AncientEventModel Ancient,
        Player Player);

    private sealed record GeneratedAncientPrediction(
        IReadOnlyList<AncientOptionSummary> Options,
        IReadOnlyList<AncientOptionReplacement> Replacements);

    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly MethodInfo GenerateInitialEventOptionsMethod = typeof(EventModel)
        .GetMethods(AnyInstance)
        .Single(method => method.Name == "GenerateInitialOptionsWrapper"
                          && method.GetParameters().Length == 0);

    public static IReadOnlyList<ActSeedOverview> Predict(RunState run) =>
        PredictionPurityGuard.Execute(run, "run-seed-overview", () => PredictCore(run));

    private static IReadOnlyList<ActSeedOverview> PredictCore(RunState run)
    {
        var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        if (player is null)
            return [];

        var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
        var results = new List<ActSeedOverview>(run.Acts.Count);
        for (var actIndex = 0; actIndex < run.Acts.Count; actIndex++)
        {
            var act = run.Acts[actIndex];
            IReadOnlyList<AncientOptionSummary> options;
            IReadOnlyList<AncientOptionReplacement> replacements = [];
            AncientOptionAccuracy accuracy;
            string? failure = null;

            if (!act._rooms.HasAncient)
            {
                options = [];
                accuracy = AncientOptionAccuracy.Unavailable;
                failure = "本幕没有先古节点";
            }
            else
            {
                var recordedOptions = FindRecordedOptions(run, player, actIndex);
                if (recordedOptions.Count > 0)
                {
                    options = recordedOptions;
                    accuracy = AncientOptionAccuracy.Recorded;
                }
                else
                {
                    try
                    {
                        var generated = GenerateOptions(snapshot, player.NetId, actIndex);
                        options = generated.Options;
                        replacements = generated.Replacements;
                        accuracy = actIndex > run.CurrentActIndex
                            ? AncientOptionAccuracy.CurrentStateProjection
                            : AncientOptionAccuracy.CurrentState;
                        if (options.Count == 0)
                        {
                            accuracy = AncientOptionAccuracy.Unavailable;
                            failure = "当前状态下没有可选交易";
                        }
                    }
                    catch (Exception exception)
                    {
                        options = [];
                        accuracy = AncientOptionAccuracy.Unavailable;
                        failure = NormalizeText(exception.GetBaseException().Message, 72);
                    }
                }
            }

            results.Add(new ActSeedOverview(
                actIndex + 1,
                NormalizeText(act.Title.GetFormattedText()),
                act._rooms.HasAncient
                    ? NormalizeText(act.Ancient.Title.GetFormattedText())
                    : "无",
                options,
                replacements,
                accuracy,
                failure,
                NormalizeText(act.BossEncounter.Title.GetFormattedText()),
                act.SecondBossEncounter is null
                    ? null
                    : NormalizeText(act.SecondBossEncounter.Title.GetFormattedText())));
        }

        return results;
    }

    private static IReadOnlyList<AncientOptionSummary> FindRecordedOptions(
        RunState run,
        Player player,
        int actIndex)
    {
        if (actIndex >= run.MapPointHistory.Count)
            return [];

        var ancientEntry = run.MapPointHistory[actIndex]
            .FirstOrDefault(entry => entry.MapPointType == MapPointType.Ancient);
        if (ancientEntry is null)
            return [];

        try
        {
            return ancientEntry.GetEntry(player.NetId).AncientChoices
                .Select(choice => new AncientOptionSummary(
                    NormalizeText(choice.Title.GetFormattedText()),
                    IsLocked: false,
                    choice.WasChosen))
                .Where(option => !string.IsNullOrWhiteSpace(option.Title))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static GeneratedAncientPrediction GenerateOptions(
        MegaCrit.Sts2.Core.Saves.SerializableRun snapshot,
        ulong playerId,
        int actIndex)
    {
        var simulation = CreateAncientSimulation(snapshot, playerId, actIndex);
        var generated = (IReadOnlyList<EventOption>?)GenerateInitialEventOptionsMethod.Invoke(
            simulation.Ancient,
            null)
                        ?? throw new InvalidOperationException(
                            $"Ancient {simulation.Ancient.Id} returned no initial options.");
        var options = generated
            .Where(option => !option.IsProceed)
            .Select(SummarizeOption)
            .ToArray();
        var replacements = options.Length == 0
            ? []
            : GenerateConditionalReplacements(
                snapshot,
                playerId,
                actIndex,
                simulation.Ancient,
                options.Select(option => option.Title).ToArray());
        return new GeneratedAncientPrediction(options, replacements);
    }

    private static AncientSimulation CreateAncientSimulation(
        MegaCrit.Sts2.Core.Saves.SerializableRun snapshot,
        ulong playerId,
        int actIndex,
        AncientEventModel? ancientOverride = null)
    {
        var shadowRun = RunState.FromSerializable(snapshot);
        shadowRun.CurrentActIndex = actIndex;
        var shadowPlayer = shadowRun.GetPlayer(playerId)
                           ?? throw new InvalidOperationException(
                               $"Could not find player {playerId} in the prediction snapshot.");
        var source = ancientOverride ?? shadowRun.Acts[actIndex].Ancient;
        var mutable = source.IsMutable
            ? (AncientEventModel)source.ClonePreservingMutability()
            : (AncientEventModel)source.ToMutable();
        mutable.Owner = shadowPlayer;
        var playerSlot = mutable.IsShared
            ? 0
            : shadowRun.GetPlayerSlotIndex(shadowPlayer);
        mutable.Rng = new MegaCrit.Sts2.Core.Random.Rng(
            (ulong)((long)shadowRun.Rng.Seed + playerSlot)
            + StringHelper.GetDeterministicHashCode(mutable.Id.Entry));
        mutable.CalculateVars();
        return new AncientSimulation(mutable, shadowPlayer);
    }

    private static IReadOnlyList<AncientOptionReplacement> GenerateConditionalReplacements(
        MegaCrit.Sts2.Core.Saves.SerializableRun snapshot,
        ulong playerId,
        int actIndex,
        AncientEventModel ancient,
        IReadOnlyList<string> currentOptions)
    {
        var definitions = GetConditionalRules(ancient);
        if (definitions.Count == 0)
            return [];
        var owner = ancient.Owner
                    ?? throw new InvalidOperationException(
                        $"Ancient {ancient.Id} has no owner during conditional prediction.");

        var replacements = new List<AncientOptionReplacement>(definitions.Count);
        foreach (var definition in definitions)
        {
            var whenMet = GenerateConditionalTitles(
                snapshot,
                playerId,
                actIndex,
                ancient,
                definition.Rule,
                conditionMet: true);
            var whenNotMet = GenerateConditionalTitles(
                snapshot,
                playerId,
                actIndex,
                ancient,
                definition.Rule,
                conditionMet: false);
            var replayedCurrentOptions = IsConditionMet(owner, definition.Rule)
                ? whenMet
                : whenNotMet;
            if (!currentOptions.SequenceEqual(replayedCurrentOptions, StringComparer.Ordinal))
            {
                // Suppress condition text if a game update changes the original algorithm.
                // The main option prediction still comes from the game's own implementation.
                return [];
            }

            var added = MultisetDifference(whenMet, whenNotMet);
            var removed = MultisetDifference(whenNotMet, whenMet);
            if (added.Count == 0 && removed.Count == 0)
                continue;

            replacements.Add(new AncientOptionReplacement(
                definition.Condition,
                added,
                removed));
        }

        return replacements;
    }

    private static IReadOnlyList<ConditionalRuleDefinition> GetConditionalRules(
        AncientEventModel ancient) => ancient switch
    {
        Tezcatara =>
        [
            new ConditionalRuleDefinition(
                ConditionalOptionRule.BasicStrike,
                "进入时仍有基础攻击牌")
        ],
        Nonupeipe =>
        [
            new ConditionalRuleDefinition(
                ConditionalOptionRule.SwiftCards,
                "进入时至少有 4 张可附魔“迅捷”的牌")
        ],
        Tanx =>
        [
            new ConditionalRuleDefinition(
                ConditionalOptionRule.InstinctCards,
                "进入时至少有 3 张可附魔“本能”的牌")
        ],
        Pael =>
        [
            new ConditionalRuleDefinition(
                ConditionalOptionRule.GoopyCards,
                "进入时至少有 3 张可附魔“黏糊”的牌"),
            new ConditionalRuleDefinition(
                ConditionalOptionRule.RemovableCards,
                "进入时至少有 5 张可移除牌"),
            new ConditionalRuleDefinition(
                ConditionalOptionRule.NoEventPet,
                "进入时尚未拥有事件宠物")
        ],
        _ => []
    };

    private static bool IsConditionMet(Player player, ConditionalOptionRule rule) => rule switch
    {
        ConditionalOptionRule.BasicStrike => player.Deck.Cards.Any(
            card => card.Tags.Contains(CardTag.Strike) && card.Rarity == CardRarity.Basic),
        ConditionalOptionRule.SwiftCards =>
            player.Deck.Cards.Count(ModelDb.Enchantment<Swift>().CanEnchant) >= 4,
        ConditionalOptionRule.InstinctCards =>
            player.Deck.Cards.Count(ModelDb.Enchantment<Instinct>().CanEnchant) >= 3,
        ConditionalOptionRule.GoopyCards =>
            player.Deck.Cards.Count(ModelDb.Enchantment<Goopy>().CanEnchant) >= 3,
        ConditionalOptionRule.RemovableCards =>
            player.Deck.Cards.Count(card => card.IsRemovable) >= 5,
        ConditionalOptionRule.NoEventPet => !player.HasEventPet(),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
    };

    private static IReadOnlyList<string> GenerateConditionalTitles(
        MegaCrit.Sts2.Core.Saves.SerializableRun snapshot,
        ulong playerId,
        int actIndex,
        AncientEventModel ancient,
        ConditionalOptionRule rule,
        bool conditionMet)
    {
        var simulation = CreateAncientSimulation(snapshot, playerId, actIndex, ancient);
        return GenerateConditionalOptions(
                simulation.Ancient,
                simulation.Player,
                rule,
                conditionMet)
            .Where(option => !option.IsProceed)
            .Select(option => SummarizeOption(option).Title)
            .ToArray();
    }

#if DEBUG
    internal static int ValidateConditionalRuleImplementations(RunState run)
    {
        var player = LocalContext.GetMe(run)
                     ?? run.Players.FirstOrDefault()
                     ?? throw new InvalidOperationException(
                         "Conditional Ancient validation could not find a player.");
        var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
        var actIndex = Math.Clamp(run.CurrentActIndex, 0, run.Acts.Count - 1);
        AncientEventModel[] ancients =
        [
            ModelDb.AncientEvent<Tezcatara>(),
            ModelDb.AncientEvent<Nonupeipe>(),
            ModelDb.AncientEvent<Tanx>(),
            ModelDb.AncientEvent<Pael>()
        ];

        var validatedRuleCount = 0;
        foreach (var ancient in ancients)
        {
            var actualSimulation = CreateAncientSimulation(
                snapshot,
                player.NetId,
                actIndex,
                ancient);
            var actual = ((IReadOnlyList<EventOption>?)GenerateInitialEventOptionsMethod.Invoke(
                    actualSimulation.Ancient,
                    null)
                          ?? throw new InvalidOperationException(
                              $"Ancient {ancient.Id} returned no options during validation."))
                .Where(option => !option.IsProceed)
                .Select(option => SummarizeOption(option).Title)
                .ToArray();
            if (actual.Length == 0)
                continue;

            foreach (var definition in GetConditionalRules(ancient))
            {
                var whenMet = GenerateConditionalTitles(
                    snapshot,
                    player.NetId,
                    actIndex,
                    ancient,
                    definition.Rule,
                    conditionMet: true);
                var whenNotMet = GenerateConditionalTitles(
                    snapshot,
                    player.NetId,
                    actIndex,
                    ancient,
                    definition.Rule,
                    conditionMet: false);
                if (whenMet.Count != 3 || whenNotMet.Count != 3)
                {
                    throw new InvalidOperationException(
                        $"Ancient {ancient.Id} rule {definition.Rule} did not generate three options.");
                }

                var expected = IsConditionMet(actualSimulation.Player, definition.Rule)
                    ? whenMet
                    : whenNotMet;
                if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Ancient {ancient.Id} rule {definition.Rule} no longer matches the game's generator.");
                }

                validatedRuleCount++;
            }
        }

        return validatedRuleCount;
    }
#endif

    private static IReadOnlyList<EventOption> GenerateConditionalOptions(
        AncientEventModel ancient,
        Player player,
        ConditionalOptionRule rule,
        bool conditionMet)
    {
        return ancient switch
        {
            Tezcatara when rule == ConditionalOptionRule.BasicStrike =>
                GenerateTezcataraOptions(ancient, conditionMet),
            Nonupeipe when rule == ConditionalOptionRule.SwiftCards =>
                GenerateNonupeipeOptions(ancient, conditionMet),
            Tanx when rule == ConditionalOptionRule.InstinctCards =>
                GenerateTanxOptions(ancient, conditionMet),
            Pael when rule is ConditionalOptionRule.GoopyCards
                or ConditionalOptionRule.RemovableCards
                or ConditionalOptionRule.NoEventPet =>
                GeneratePaelOptions(ancient, player, rule, conditionMet),
            _ => throw new InvalidOperationException(
                $"Conditional rule {rule} does not apply to ancient {ancient.Id}.")
        };
    }

    private static IReadOnlyList<EventOption> GenerateTezcataraOptions(
        AncientEventModel ancient,
        bool hasBasicStrike)
    {
        var firstPool = GetOptionPool(ancient, "OptionPool1").ToList();
        if (hasBasicStrike)
            firstPool.Add(GetOption(ancient, "NutritiousSoupOption"));

        return
        [
            PickOption(ancient, firstPool),
            PickOption(ancient, GetOptionPool(ancient, "OptionPool2")),
            PickOption(ancient, GetOptionPool(ancient, "OptionPool3"))
        ];
    }

    private static IReadOnlyList<EventOption> GenerateNonupeipeOptions(
        AncientEventModel ancient,
        bool hasEnoughSwiftCards)
    {
        var pool = GetOptionPool(ancient, "OptionPool").ToList();
        if (hasEnoughSwiftCards)
            pool.Add(GetOption(ancient, "BeautifulBraceletEventOption"));
        pool.UnstableShuffle(ancient.Rng);
        return pool.Take(3).ToArray();
    }

    private static IReadOnlyList<EventOption> GenerateTanxOptions(
        AncientEventModel ancient,
        bool hasEnoughInstinctCards)
    {
        var pool = GetOptionPool(ancient, "BaseOptionPool").ToList();
        if (hasEnoughInstinctCards)
            pool.Add(GetOption(ancient, "TriBoomerangOption"));
        pool.UnstableShuffle(ancient.Rng);
        return pool.Take(3).ToArray();
    }

    private static IReadOnlyList<EventOption> GeneratePaelOptions(
        AncientEventModel ancient,
        Player player,
        ConditionalOptionRule overriddenRule,
        bool conditionMet)
    {
        var hasEnoughGoopyCards = overriddenRule == ConditionalOptionRule.GoopyCards
            ? conditionMet
            : player.Deck.Cards.Count(ModelDb.Enchantment<Goopy>().CanEnchant) >= 3;
        var hasEnoughRemovableCards = overriddenRule == ConditionalOptionRule.RemovableCards
            ? conditionMet
            : player.Deck.Cards.Count(card => card.IsRemovable) >= 5;
        var hasNoEventPet = overriddenRule == ConditionalOptionRule.NoEventPet
            ? conditionMet
            : !player.HasEventPet();

        var first = PickOption(ancient, GetOptionPool(ancient, "OptionPool1"));
        var secondPool = GetOptionPool(ancient, "OptionPool2").ToList();
        if (hasEnoughGoopyCards)
            secondPool.Add(GetOption(ancient, "PaelsClawOption"));
        if (hasEnoughRemovableCards)
            secondPool.Add(GetOption(ancient, "PaelsToothOption"));
        secondPool.AddRange(secondPool.ToArray());
        secondPool.Add(GetOption(ancient, "PaelsGrowthOption"));
        var second = PickOption(ancient, secondPool);

        var thirdPool = GetOptionPool(ancient, "OptionPool3").ToList();
        if (hasNoEventPet)
            thirdPool.Add(GetOption(ancient, "PaelsLegionOption"));
        var third = PickOption(ancient, thirdPool);
        return [first, second, third];
    }

    private static EventOption PickOption(
        AncientEventModel ancient,
        IEnumerable<EventOption> options)
    {
        return ancient.Rng.NextItem(options)
               ?? throw new InvalidOperationException(
                   $"Ancient {ancient.Id} tried to choose from an empty option pool.");
    }

    private static IEnumerable<EventOption> GetOptionPool(
        AncientEventModel ancient,
        string propertyName)
    {
        var value = GetAncientProperty(ancient, propertyName);
        return value as IEnumerable<EventOption>
               ?? throw new InvalidOperationException(
                   $"Ancient {ancient.Id} property {propertyName} is not an option pool.");
    }

    private static EventOption GetOption(
        AncientEventModel ancient,
        string propertyName)
    {
        return GetAncientProperty(ancient, propertyName) as EventOption
               ?? throw new InvalidOperationException(
                   $"Ancient {ancient.Id} property {propertyName} is not an option.");
    }

    private static object GetAncientProperty(
        AncientEventModel ancient,
        string propertyName)
    {
        var property = ancient.GetType().GetProperty(propertyName, AnyInstance)
                       ?? throw new MissingMemberException(
                           ancient.GetType().FullName,
                           propertyName);
        return property.GetValue(ancient)
               ?? throw new InvalidOperationException(
                   $"Ancient {ancient.Id} property {propertyName} returned null.");
    }

    private static AncientOptionSummary SummarizeOption(EventOption option)
    {
        var title = NormalizeText(option.Title.GetFormattedText());
        if (string.IsNullOrWhiteSpace(title))
            title = option.TextKey.Split('.').Last();
        return new AncientOptionSummary(title, option.IsLocked, WasChosen: false);
    }

    private static IReadOnlyList<string> MultisetDifference(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var remaining = right
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var difference = new List<string>();
        foreach (var value in left)
        {
            if (remaining.TryGetValue(value, out var count) && count > 0)
            {
                remaining[value] = count - 1;
                continue;
            }

            difference.Add(value);
        }

        return difference;
    }

    private static string NormalizeText(string text, int maximumLength = 96)
    {
        var normalized = string.Join(
            " ",
            text.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..(maximumLength - 1)] + "…";
    }
}
