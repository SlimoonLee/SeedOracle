#if DEBUG
using System.Text.Json;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

internal static class MerchantMechanicsRegressionTests
{
    private readonly record struct Counters(int Rewards, int Shops, int Niche)
    {
        internal static Counters Read(PlanningPredictionService.State state) => new(
            state.Player.PlayerRng.Rewards.ToSerializable().counter,
            state.Player.PlayerRng.Shops.ToSerializable().counter,
            state.Run.Rng.Niche.ToSerializable().counter);
        public static Counters operator -(Counters after, Counters before) =>
            new(after.Rewards - before.Rewards, after.Shops - before.Shops, after.Niche - before.Niche);
    }

    internal static void Run(PlanningPredictionService planning, RunState live, Player player)
    {
        var fixture = planning.CreateState(live, player);
        foreach (var relic in fixture.Player.Relics.ToArray()) fixture.Player.RemoveRelicInternal(relic);
        foreach (var potion in fixture.Player.Potions.ToArray()) fixture.Player.DiscardPotionInternal(potion, silent: true);
        fixture.Player.InitializeSeed("NATIVE_MERCHANT_MECHANICS");
        fixture.Player.Gold = 10000;
        fixture.Run.ActFloor = 14;
        fixture.Run.AppendToMapPointHistory(MapPointType.Shop, RoomType.Shop, null);
        fixture.Player.RelicGrabBag._deques[RelicRarity.Shop] = [ModelDb.Relic<MembershipCard>()];
        var input = planning.Capture(fixture);
        var ordinary = planning.Restore(input);
        using (var shop = new PlanningMerchantSession(ordinary))
        {
            var before = FullRng(ordinary);
            var entry = shop.Inventory.CharacterCardEntries[0];
            var cost = entry.Cost;
            var gold = ordinary.Player.Gold;
            Require(shop.TryPurchase(new(MerchantCategory.CharacterCard, 0)), "ordinary purchase rejected");
            Require(FullRng(ordinary) == before && ordinary.Player.Gold == gold - cost && !entry.IsStocked,
                "ordinary native purchase must clear stock without consuming RNG");
        }
        var ordinaryCombinations = VerifyOrdinaryCombinations(planning, input);

        var courier = planning.Restore(input);
        planning.AddRelic(courier, ModelDb.Relic<TheCourier>().Id);
        var courierInput = planning.Capture(courier);
        var cases = new[]
        {
            (Pick: new MerchantPick(MerchantCategory.CharacterCard, 0), Delta: new Counters(2,2,0)),
            (Pick: new MerchantPick(MerchantCategory.ColorlessCard, 0), Delta: new Counters(1,2,0)),
            (Pick: new MerchantPick(MerchantCategory.Relic, 2), Delta: new Counters(1,1,0)),
            (Pick: new MerchantPick(MerchantCategory.Potion, 0), Delta: new Counters(0,3,0))
        };
        foreach (var (pick, delta) in cases)
        {
            var state = planning.Restore(courierInput);
            using var shop = new PlanningMerchantSession(state);
            var before = Counters.Read(state);
            Require(shop.TryPurchase(pick) && shop.Entry(pick)!.IsStocked, "Courier did not restock " + pick.Category);
            var actual = Counters.Read(state) - before;
            Require(actual == delta, $"Courier {pick.Category}: expected {delta}, got {actual}");
            Smoke.SmokeReport.Add($"merchant-mechanisms RNG courier_{pick.Category}={JsonSerializer.Serialize(actual)}");
        }

        var membership = planning.Restore(input);
        using (var shop = new PlanningMerchantSession(membership))
        {
            var original = shop.Inventory.CharacterCardEntries[0].Cost;
            Require(shop.TryPurchase(new(MerchantCategory.Relic, 2)), "Membership Card purchase rejected");
            Require(shop.Inventory.CharacterCardEntries[0].Cost == original / 2, "remaining stock did not receive native discount");
            var gold = membership.Player.Gold;
            Require(shop.TryPurchase(new(MerchantCategory.CharacterCard, 0)) && membership.Player.Gold == gold - original / 2,
                "later purchase charged stale pre-membership price");
        }

        var bank = planning.Restore(input);
        planning.AddRelic(bank, ModelDb.Relic<MawBank>().Id);
        var bankGold = bank.Player.Gold;
        using (var shop = new PlanningMerchantSession(bank))
        {
            Require(bank.Player.Gold == bankGold + 12, "Maw Bank enter-shop gold was omitted from the purchase budget");
            var rng = FullRng(bank);
            Require(shop.TryPurchase(new(MerchantCategory.CharacterCard, 0)), "Maw Bank fixture purchase rejected");
            Require(bank.Player.Relics.OfType<MawBank>().Single().HasItemBeenBought && rng == FullRng(bank),
                "native purchase callback must disable Maw Bank without advancing RNG");
        }

        var ticket = planning.Restore(input);
        planning.AddRelic(ticket, ModelDb.Relic<MealTicket>().Id);
        ticket.Player.Creature.SetCurrentHpInternal(1);
        using (var shop = new PlanningMerchantSession(ticket))
            Require(ticket.Player.Creature.CurrentHp == 16, "Meal Ticket enter-shop healing was omitted");

        var orrery = planning.Restore(input);
        orrery.Player.RelicGrabBag._deques[RelicRarity.Shop] = [ModelDb.Relic<Orrery>()];
        var orreryInput = planning.Capture(orrery);
        using (var shop = new PlanningMerchantSession(orrery))
        {
            var before = Counters.Read(orrery);
            var selector = new MerchantRewardSelector(orrery, 14, true, true);
            Require(shop.TryPurchase(new(MerchantCategory.Relic, 2), selector, selector.HandleRewardsAsync), "Orrery purchase rejected");
            Require(Counters.Read(orrery) - before == new Counters(45,0,0) && selector.Facts.Count == 5
                && selector.Facts.All(fact => fact.EquivalentValues.Count == 4),
                "Orrery must generate five complete native rewards and prove ordinary pick/skip freedom separately");
        }
        var astrolabe = planning.Restore(input);
        using (ShadowIsolation.Enter(astrolabe.Player, automateRewards: true))
        using (ShadowIsolation.UseSelector(new ExplicitSelector(deckTargets: 3)))
        {
            var before = Counters.Read(astrolabe);
            var allBefore = astrolabe.Player.PlayerRng.ToSerializable();
            RelicCmd.Obtain<Astrolabe>(astrolabe.Player).GetAwaiter().GetResult();
            Require(Counters.Read(astrolabe) - before == new Counters(0,0,3)
                && JsonSerializer.Serialize(allBefore) == JsonSerializer.Serialize(astrolabe.Player.PlayerRng.ToSerializable()),
                "Astrolabe must use Niche, not Rewards or the player's transformation RNG");
        }

        // Independent oracle: call the native entry wrapper directly, outside
        // PlanningMerchantSession, and compare complete downstream outcomes.
        var expected = new Dictionary<string, string>();
        var previewState = planning.Restore(courierInput);
        var stock = planning.GenerateMerchant(previewState);
        var offers = ShopPurchaseChoices.Describe(previewState, stock, 14, true);
        foreach (var offer in offers.AvailableCards)
        {
            var native = planning.Restore(courierInput);
            using (ShadowIsolation.Enter(native.Player, automateRewards: true))
            using (ShadowIsolation.GenerateMerchantInventory())
            using (ShadowIsolation.ExecuteMerchantPurchase())
            {
                native.Run.PushRoom(new MerchantRoom());
                try
                {
                    var inventory = MerchantInventory.CreateForNormalMerchant(native.Player);
                    var entry = inventory.CardEntries.ElementAt(offer.Slot);
                    Require(entry.OnTryPurchaseWrapper(inventory).GetAwaiter().GetResult(), "direct native purchase rejected");
                }
                finally { native.Run.PopCurrentRoom(); }
            }
            native.Run.ActFloor = 15;
            using var rewards = planning.GenerateCombatRewards(native, RoomType.Monster,
                MerchantChoiceBranchService.ResolveEncounter(native, RoomType.Monster));
            expected[offer.Value] = ForecastContentKey.Combat(rewards);
        }
        var start = planning.Restore(courierInput);
        using var baseline = planning.GenerateCombatRewards(planning.Restore(courierInput), RoomType.Monster,
            MerchantChoiceBranchService.ResolveEncounter(start, RoomType.Monster));
        var forecast = new MerchantChoiceBranchService(512, 48, 30000).PredictRewards(start.Player, Route(), baseline.Detach());
        foreach (var (value, key) in expected)
            Require(forecast.ChoiceBranches.Select(branch => (branch.Outcome, branch.ChoicePaths))
                .Prepend((forecast, forecast.ReferenceChoicePaths)).Any(result => ForecastContentKey.Combat(result.Item1) == key
                    && result.Item2.SelectMany(path => path.Facts).Any(fact => fact.Value == value)),
                "non-plan Courier branch differs from direct native purchase: " + value);

        var starStart = planning.Restore(orreryInput);
        var starForecast = new MerchantChoiceBranchService(512, 48, 30000).PredictRewards(starStart.Player, Route(), baseline.Detach());
        Require(starForecast.ChoiceBranches.Any(branch => branch.ChoicePaths.Any(path => path.Facts.Any(fact =>
            fact.Value.Contains("ORRERY", StringComparison.Ordinal)) && path.Facts.Count(fact => fact.Key.StartsWith("shop-pickup:")) == 5)),
            "non-plan Orrery acquisition did not advance all five rewards");
        var starBranch = starForecast.ChoiceBranches.First(branch => branch.ChoicePaths.Any(path =>
            path.Facts.Any(fact => fact.Value.Contains("ORRERY", StringComparison.Ordinal))));
        var starCondition = ForecastChoiceConditionFormatter.Format(ForecastChoiceConditionFormatter.Reduce(
            starBranch.ChoicePaths.Select(path => path.Facts)), true);
        Require(starCondition.Contains("购买所得5组选牌：任意选择") && !starCondition.Contains("第1组"),
            "equivalent Orrery choices must be summarized together");
        Smoke.SmokeReport.Add("merchant-mechanisms ORRERY " + starCondition);
        VerifyPlan(planning, courierInput);
        Smoke.SmokeReport.Add($"merchant-mechanisms PASS ordinary_purchase_rng=0 native_ordinary_sets={ordinaryCombinations} courier_categories=4 "
            + $"native_downstream_cards={expected.Count} orrery_rewards=45 astrolabe_niche=3 membership=dynamic maw_bank=disabled repeated_slot_plan=matched");
    }

    private static int VerifyOrdinaryCombinations(PlanningPredictionService planning, PlanningPredictionService.StateSnapshot input)
    {
        var count = 0;
        foreach (var budget in new[] { 137, 166 })
        {
            string? expected = null;
            for (var mask = 0; mask < 128; mask++)
            {
                var state = planning.Restore(input);
                state.Player.Gold = budget;
                using (var shop = new PlanningMerchantSession(state))
                {
                    var cards = shop.Inventory.CardEntries.ToArray();
                    if (cards.Where((_, index) => (mask & (1 << index)) != 0).Sum(entry => entry.Cost) > budget) continue;
                    var before = FullRng(state);
                    for (var index = 0; index < cards.Length; index++)
                        if ((mask & (1 << index)) != 0)
                            Require(shop.TryPurchase(index < 5 ? new(MerchantCategory.CharacterCard, index)
                                : new(MerchantCategory.ColorlessCard, index - 5)), "native ordinary subset purchase rejected");
                    Require(FullRng(state) == before, "native ordinary subset advanced RNG");
                }
                state.Run.ActFloor = 15;
                using var rewards = planning.GenerateCombatRewards(state, RoomType.Monster,
                    MerchantChoiceBranchService.ResolveEncounter(state, RoomType.Monster));
                var actual = ForecastContentKey.Combat(rewards);
                expected ??= actual;
                Require(actual == expected, "native ordinary purchase subset changed downstream drops");
                count++;
            }
        }
        Require(count > 2, "no affordable native ordinary purchase subsets exercised");
        return count;
    }

    private static void VerifyPlan(PlanningPredictionService planning, PlanningPredictionService.StateSnapshot input)
    {
        var native = planning.Restore(input);
        using (var shop = new PlanningMerchantSession(native))
        {
            Require(shop.TryPurchase(new(MerchantCategory.CharacterCard, 0))
                && shop.TryPurchase(new(MerchantCategory.CharacterCard, 0)), "native repeated slot purchase failed");
        }
        var state = planning.Restore(input);
        var point = new MapPoint(0,13) { PointType = MapPointType.Shop };
        var entry = new RoutePlanEntry(point.coord, new RoutePlanChoice.Merchant(
            [new(MerchantCategory.CharacterCard, 0), new(MerchantCategory.CharacterCard, 0)], false), null);
        var plan = new RoutePlan { ActIndex = state.Run.CurrentActIndex, AnchorCoord = point.coord,
            Entries = [entry], Snapshot = RoutePlanResourceSnapshot.Capture(state.Player) };
        var chain = new RoutePlanForecastService(planning).BuildChain(state.Run, state.Player, [entry], _ => point, plan);
        Require(chain is not null && chain.BlockedAtNode is null && chain.Gold == native.Player.Gold
            && FullRng(chain.State) == FullRng(native) && chain.Outcomes[point.coord].MerchantPurchases.Count == 2
            && chain.Outcomes[point.coord].MerchantAfterPurchases!.CharacterCards[0].Id is not null,
            "planned repeated Courier slot did not match native state/restock");
    }

    private sealed class ExplicitSelector(int deckTargets = 0) : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) =>
            Task.FromResult(options.Take(deckTargets));
        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives) => new()
            { alternative = alternatives.First(option => option.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase)) };
    }

    private static RouteWorldline Route() => new(new RoutePath([
        new(new MapPoint(0,13) { PointType = MapPointType.Shop },false,false),
        new(new MapPoint(0,14) { PointType = MapPointType.Monster },false,false)]), [],
        RoomType.Monster, null, null, 1, [RoomType.Shop,RoomType.Monster], true);
    private static string FullRng(PlanningPredictionService.State state) => JsonSerializer.Serialize(new
        { Player = state.Player.PlayerRng.ToSerializable(), Run = state.Run.Rng.ToSerializable() });
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Merchant mechanisms regression: " + message);
    }
}
#endif
