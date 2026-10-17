using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using SeedOracle.Integration;
using SeedOracle.Validation;

namespace SeedOracle.Forecasting;

internal static class CombatRewardBranchService
{
    internal const int MaximumPossibilities = 64;
    internal const int MaximumHuntPreview = 3;
    private static readonly FieldInfo? AfterGeneratedField = typeof(CardReward).GetField("AfterGenerated", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly AsyncLocal<Func<bool>?> PreviewBudget = new();

    internal static IDisposable LimitPreviewWork(Func<bool> canContinue)
    {
        var previous = PreviewBudget.Value;
        PreviewBudget.Value = canContinue;
        return new BudgetScope(previous);
    }

    private sealed class BudgetScope(Func<bool>? previous) : IDisposable
    {
        public void Dispose() => PreviewBudget.Value = previous;
    }

    private sealed record RewardTemplate(CardCreationOptions Options, CardCreationSource Source,
        bool CanReroll, bool CanSkip, IReadOnlyList<string> Cards);
    private sealed record GeneratedSeed(PlanningPredictionService.StateSnapshot State, IReadOnlyList<RewardTemplate> Rewards);

    internal static CombatRewardDetails AddPossibilities(
        PlanningPredictionService planning,
        PlanningPredictionService.StateSnapshot before,
        RoomType roomType,
        EncounterModel encounter,
        CombatRewardDetails baseline,
        Func<bool>? canContinue = null)
    {
        using var workSample = ForecastWorkProfile.Measure("reward.possibilities");
        var hasHunt = before.Run.Players.First(p => p.NetId == before.PlayerNetId)
            .Deck.Any(card => card.Id == ModelDb.Card<TheHunt>().Id);
        var hasReroll = baseline.CardRewardGroups.Any(group => group.Steps[0].Alternatives
            .Any(alternative => alternative.OptionId.Equals("REROLL", StringComparison.OrdinalIgnoreCase)));
        if (!hasHunt && !hasReroll)
            return baseline;

        var pending = new Queue<(int Hunt, int[] Order)>();
        for (var hunt = 0; hunt <= (hasHunt ? MaximumHuntPreview : 0); hunt++)
            pending.Enqueue((hunt, []));
        var results = new List<CombatRewardPossibility>();
        var seeds = new Dictionary<int, GeneratedSeed>();
        while (pending.Count > 0 && results.Count < MaximumPossibilities)
        {
            if (canContinue?.Invoke() == false || PreviewBudget.Value?.Invoke() == false) break;
            var (hunt, order) = pending.Dequeue();
            var seed = seeds.GetValueOrDefault(hunt);
            var state = planning.Restore(seed?.State ?? before);
            using var scope = ShadowIsolation.Enter(state.Player, automateRewards: true);
            using var rewards = seed is null
                ? planning.GenerateCombatRewards(state, roomType, encounter, assumedHuntTriggers: hunt)
                : RestoreRewards(state, seed);
            if (order.Length == 0 && seed is null && CanRestoreRewards(rewards))
                seeds[hunt] = new(planning.Capture(state), rewards.NativeCardRewards.Select(reward =>
                    new RewardTemplate(reward.RerollOptions, reward.Options.Source, reward.CanReroll, reward.CanSkip,
                        reward.Cards.Select(card => JsonSerializer.Serialize(card.ToSerializable(),
                            JsonSerializationUtility.GetTypeInfo<SerializableCard>())).ToArray())).ToArray());
            using var rewardRoom = PlanningPredictionService.EnterRewardRoom(state, encounter);
            foreach (var group in order)
                rewards.NativeCardRewards[group].Reroll();
            if (hunt > 0 || order.Length > 0)
                results.Add(new CombatRewardPossibility(hunt, order,
                    rewards.NativeCardRewards.Select(card => new CombatCardRewardGroup(
                        [PlanningPredictionService.DescribeCardReward(card)])).ToArray()));
            for (var group = 0; group < rewards.NativeCardRewards.Count; group++)
                if (rewards.NativeCardRewards[group].CanReroll)
                    pending.Enqueue((hunt, [.. order, group]));
        }
        return baseline with
        {
            Possibilities = results,
            PossibilitiesTruncated = pending.Count > 0,
            HuntPreviewLimit = hasHunt ? MaximumHuntPreview : null
        };
    }

    private static bool CanRestoreRewards(CombatRewardDetails rewards) => AfterGeneratedField is not null
        && rewards.NativeCardRewards.All(reward => reward.GetType() == typeof(CardReward)
            && reward.RerollOptions.RngOverride is null && reward.RerollOptions.CardPoolFilter is null
            && AfterGeneratedField.GetValue(reward) is null);

    private static CombatRewardDetails RestoreRewards(PlanningPredictionService.State state, GeneratedSeed seed)
    {
        using var workSample = ForecastWorkProfile.Measure("reward.seed-reuse");
        // A reroll starts after reward generation. Restore that exact RNG/odds,
        // hook state and history once per order, retaining the actual cards
        // and reroll options. Do not re-run victory and reward-generation hooks
        // for every permutation. Custom generators/delegates use the full path.
        var rewards = seed.Rewards.Select(template =>
        {
            var options = template.Options;
            var reroll = new CardCreationOptions(options.CardPools, options.Source, options.RarityOdds).WithFlags(options.Flags);
            return new CardReward(template.Cards.Select(json => state.Run.LoadCard(
                    JsonSerializer.Deserialize(json, JsonSerializationUtility.GetTypeInfo<SerializableCard>())!, state.Player)),
                template.Source, state.Player, reroll) { CanReroll = template.CanReroll, CanSkip = template.CanSkip };
        }).ToArray();
        return new CombatRewardDetails(0, [], [], []) { NativeCardRewards = rewards };
    }
}
