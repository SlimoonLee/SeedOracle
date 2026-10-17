using System.Collections.Concurrent;
using System.Reflection;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Factories;

namespace SeedOracle.Forecasting;

// A normal reward is rolled before selection. Taking a card instead of skipping
// changes the deck/history, not the RNG. Forget that distinction only while the
// remaining native operations cannot observe it. An unsupported operation drops
// the proof and keeps the original, concrete selection condition.
internal static class OrdinaryCardChoiceEquivalence
{
    private static readonly ConcurrentDictionary<Type, bool> InertModels = new();
    private static readonly ConcurrentDictionary<Type, bool> MerchantModels = new();
    private static readonly HashSet<string> MerchantHooks =
    [
        "ModifyMerchantCardPool", "ModifyMerchantCardRarity", "ModifyMerchantCardCreationResults",
        "ModifyMerchantPrice", "ShouldAllowMerchantCardRemoval", "ModifyCardRewardUpgradeOdds"
    ];

    internal static bool CanForgetPick(PlanningPredictionService.State state, CombatRewardDetails rewards) =>
        rewards.CardRewardGroups.Count > 0
        && rewards.NativeCardRewards.Count == rewards.CardRewardGroups.Count
        && rewards.CardRewardGroups.All(group => group.Steps.Count == 1
            && group.Steps[0].Alternatives.Any(option => option.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase))
            && group.Steps[0].Alternatives.All(option => option.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase)
                || option.OptionId.Equals("REROLL", StringComparison.OrdinalIgnoreCase)))
        && rewards.NativeCardRewards.All(reward => reward.Cards.All(IsInertCard))
        && StateIsDeckIndependent(state);

    internal static bool CanContinue(PlanningPredictionService.State state, RoomType type,
        MapPoint point, bool target)
    {
        if (target && type == RoomType.Shop)
            return StateIsDeckIndependent(state, merchant: true);
        if (!StateIsDeckIndependent(state)) return false;
        if (target && type.IsCombatRoom()) return true;
        if (type == RoomType.Monster) return true;
        if (type != RoomType.Event || point.PointType == MapPointType.Ancient) return false;

        // Event eligibility can itself inspect the deck. Certify every queue
        // entry examined, not just the event chosen on the skip reference path.
        var rooms = state.Run.Act._rooms;
        if (rooms.events.Count == 0) return false;
        var index = rooms.eventsVisited;
        for (var examined = 0; examined < rooms.events.Count; examined++, index++)
        {
            var candidate = rooms.events[index % rooms.events.Count];
            if (candidate is not BrainLeech
                && candidate.GetType().GetMethod(nameof(EventModel.IsAllowed))?.DeclaringType != typeof(EventModel))
                return false;
            if (candidate.IsAllowed(state.Run) && !state.Run.VisitedEventIds.Contains(candidate.Id))
                return candidate is BrainLeech;
        }
        return rooms.events[index % rooms.events.Count] is BrainLeech;
    }

    private static bool StateIsDeckIndependent(PlanningPredictionService.State state, bool merchant = false)
    {
        if (!HasNativePools(state)) return false;
        return state.Run.IterateHookListeners(null).All(model =>
            ReferenceEquals(model, state.Run.MultiplayerScalingModel)
            || (merchant ? MerchantModels.GetOrAdd(model.GetType(), type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => MerchantHooks.Contains(method.Name))
                    .All(method => method.DeclaringType == typeof(AbstractModel))) : IsInertModel(model)));
    }

    internal static bool HasNativePools(PlanningPredictionService.State state)
    {
        var native = typeof(AbstractModel).Assembly;
        if (state.Run.Players.Count != 1 || state.Run.Modifiers.Count != 0
            || state.Player.Character.GetType().Assembly != native
            // Unused registered content is not a dependency. Inspect only pools
            // and bags that these native operations can actually draw from.
            || state.Player.Character.CardPool.GetUnlockedCards(state.Player.UnlockState, state.Player.RunState.CardMultiplayerConstraint)
                .Concat(ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(state.Player.UnlockState, state.Player.RunState.CardMultiplayerConstraint))
                .Any(model => model.GetType().Assembly != native)
            || PotionFactory.GetPotionOptions(state.Player).Any(model => model.GetType().Assembly != native)
            || state.Player.RelicGrabBag._deques.Values.SelectMany(models => models)
                .Concat(state.Player.RelicGrabBag._originalRelics ?? [])
                .Any(model => model.GetType().Assembly != native))
            return false;
        return true;
    }

    private static bool IsInertCard(CardModel card) => card.GetType().Assembly == typeof(AbstractModel).Assembly && IsInertModel(card)
        && (card.Enchantment is null || IsInertModel(card.Enchantment));

    private static bool IsInertModel(AbstractModel model) => model is not TheHunt
        && InertModels.GetOrAdd(model.GetType(), type =>
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => !method.IsSpecialName
                    && method.GetBaseDefinition().DeclaringType == typeof(AbstractModel)
                    && ObservesAllowedSteps(method.Name))
                .All(method => method.DeclaringType == typeof(AbstractModel)
                    // These native starter callbacks only heal the owner.
                    || method.Name == nameof(AbstractModel.AfterCombatVictory)
                        && (type == typeof(BurningBlood) || type == typeof(BlackBlood))
                    // Native extra groups and reroll availability do not inspect
                    // the deck. Actually choosing reroll is never in the proof.
                    || method.Name == nameof(AbstractModel.TryModifyRewards)
                        && (type == typeof(PrayerWheel) || type == typeof(WhiteStar))
                    || method.Name == nameof(AbstractModel.TryModifyRewardsLate) && type == typeof(Driftwood));
        });

    // Native calls reachable from reward generation/pickup, unknown-room rolls,
    // Brain Leech and merchant creation. Combat play/turn/draw callbacks are not
    // invoked by these planning steps and must not make a starter relic such as
    // Cracked Core look like a dependency on the reward selection.
    private static bool ObservesAllowedSteps(string name) =>
        new[] { "Reward", "Merchant", "Gold", "Potion", "Relic", "Event", "UnknownMapPoint",
            "AddToDeck", "AddedToDeck", "CardChangedPiles", "CombatEnd", "CombatVictory",
            "Damage", "Heal", "Hp", "Death", "Die", "Hitting" }.Any(part => name.Contains(part, StringComparison.Ordinal));
}
