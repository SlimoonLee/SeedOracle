using System.Reflection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Integration;

namespace SeedOracle.Forecasting;

/// <summary>
/// Blitz goods sell actions, and almost all of them roll their contents at the
/// moment of purchase, out of <c>PlayerRng.Shops</c>. Buying the entry on a
/// restored copy of the run therefore shows exactly what it hands over without
/// reimplementing a single draw.
///
/// The entry itself is copied rather than regenerated: its constructor also
/// rolls (the enchantment for one good, the character for the prismatic one), so
/// generating a fresh one would both pick different contents and move the stream
/// the goods are about to read.
///
/// The one thing a clone cannot answer is a deck selection: upgrading, removing,
/// transforming or enchanting a card needs the player's target. Those probes stop
/// at the selection and report what the purchase is waiting for.
/// </summary>
internal static class BlitzMerchantProbe
{
    private static readonly FieldInfo PlayerField = typeof(MerchantEntry)
        .GetField("_player", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ShallowCopy = typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // A copy still points at the live inventory's handlers, which would decrement
    // the real pick counter (and refresh the real shop) when the probe buys.
    private static readonly FieldInfo[] EventFields = new[] { "PurchaseCompleted", "PurchaseFailed", "EntryUpdated" }
        .Select(name => typeof(MerchantEntry).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic))
        .OfType<FieldInfo>()
        .ToArray();

    internal static IReadOnlyList<BlitzYieldForecast> Probe(
        PlanningPredictionService service,
        PlanningPredictionService.State state,
        IReadOnlyList<BlitzMerchantEntry> shelf)
    {
        if (shelf.Count == 0) return [];

        var snapshot = service.Capture(state);
        var results = new BlitzYieldForecast[shelf.Count];
        for (var index = 0; index < shelf.Count; index++)
            results[index] = Predict(service, snapshot, shelf[index]);
        return results;
    }

    internal static BlitzYieldForecast Predict(
        PlanningPredictionService service,
        PlanningPredictionService.State state,
        BlitzMerchantEntry entry,
        int? deckPick = null) => Predict(service, service.Capture(state), entry, deckPick);

    private static BlitzYieldForecast Predict(
        PlanningPredictionService service,
        PlanningPredictionService.StateSnapshot snapshot,
        BlitzMerchantEntry entry,
        int? deckPick = null)
    {
        PlanningPredictionService.State probe;
        try { probe = service.Restore(snapshot); }
        catch (Exception) { return BlitzYieldForecast.Empty; }
        return PredictInPlace(probe, entry, deckPick);
    }

    /// <summary>
    /// Buys the entry on a state the caller already owns. The purchase mutates
    /// that clone (deck, hp, choices), so it must not be shared with anything
    /// else — which is exactly what a one-shot hover clone is.
    /// </summary>
    /// <param name="deckPick">
    /// Answers a deck selection with that candidate index instead of declining
    /// it. Besides checking the transform table against a real purchase, this is
    /// how a multi-buy forecast completes a predecessor that waits for a pick:
    /// the pick's identity never moves a stream, only the completion does.
    /// </param>
    internal static BlitzYieldForecast PredictInPlace(
        PlanningPredictionService.State probe,
        BlitzMerchantEntry entry,
        int? deckPick = null)
    {
        var player = probe.Player;
        var deckBefore = CountDeck(player);
        var hpBefore = player.Creature.CurrentHp;
        var maxHpBefore = player.Creature.MaxHp;
        // A deck selection may read from the same stream the purchase would, so
        // the transformation position is pinned before the probe buys anything.
        var transformStream = entry.MerchantEntryType == BlitzMerchantEntryType.CardTransform
            ? player.PlayerRng.Transformations.ToSerializable()
            : null;

        try
        {
            using var isolation = ShadowIsolation.Enter(player, automateRewards: true);
            using var discovery = ShadowIsolation.GenerateMerchantInventory();
            var replica = Replica(entry, player);
            var inventory = new MerchantInventory(player, [], [], [], [], null, [replica]);
            inventory.Setup();
            using var registration = ShadowIsolation.UseMerchantInventories([inventory]);

            var choicesBefore = inventory.BlitzChoicesRemaining;
            var selector = new ProbeSelector(deckPick);
            var offered = new List<Reward>();
            using var selections = ShadowIsolation.UseSelector(selector);
            using var handler = ShadowIsolation.UseRewardHandler(async set =>
            {
                // Populate rolls the cards, relics and potions. Offering them
                // would need the player's picks, which is exactly what the probe
                // is not allowed to invent.
                await set.GenerateWithoutOffering();
                offered.AddRange(ShadowIsolation.EnumerateRewards(set.Rewards));
            });
            using var purchase = ShadowIsolation.ExecuteMerchantPurchase();
            bool taken;
            try { taken = replica.OnTryPurchaseUnsynchronized(inventory).GetAwaiter().GetResult(); }
            catch (Exception) { return BlitzYieldForecast.Empty; }

            var deckAfter = CountDeck(player);
            var (gained, upgraded) = DiffDeck(player, deckBefore, deckAfter);
            return new BlitzYieldForecast
            {
                CardScreens = CardScreens(offered),
                Relics = offered.OfType<RelicReward>().Select(reward => reward.Relic)
                    .OfType<RelicModel>().Select(ForecastItemDetails.Relic).ToArray(),
                Potions = offered.OfType<PotionReward>().Select(reward => reward.Potion)
                    .OfType<PotionModel>().Select(ForecastItemDetails.Potion).ToArray(),
                GainedCards = gained,
                UpgradedCards = upgraded,
                MaxHpDelta = player.Creature.MaxHp - maxHpBefore,
                HealedHp = player.Creature.CurrentHp - hpBefore,
                // Every successful purchase also spends one pick, which is the
                // price of the good rather than something it grants.
                ExtraChoices = inventory.BlitzChoicesRemaining - choicesBefore + (taken ? 1 : 0),
                TransformChoices = transformStream is null
                    ? []
                    : TransformChoices(selector.Candidates, transformStream),
                NeedsCardPick = selector.Asked,
                PickableCards = selector.Pickable,
                Resolved = true,
                Purchased = taken
            };
        }
        catch (Exception)
        {
            return BlitzYieldForecast.Empty;
        }
    }

    /// <summary>
    /// Answers "what does each selectable card turn into" without buying one.
    /// The game transforms through <c>CardFactory</c> and every candidate reads
    /// the stream from the same position, so a clone per candidate is exactly
    /// what that pick would do — the only per-card difference is that a card is
    /// never replaced by itself.
    /// </summary>
    private static IReadOnlyList<BlitzCardChoiceOutcome> TransformChoices(
        IReadOnlyList<CardModel> candidates, SerializableRng stream)
    {
        if (candidates.Count == 0) return [];
        var pristine = new Rng(stream);
        var outcomes = new List<BlitzCardChoiceOutcome>(candidates.Count);
        foreach (var card in candidates)
        {
            if (!card.IsTransformable) continue;
            try
            {
                var result = CardFactory.CreateRandomCardForTransform(
                    card, isInCombat: false, new Rng(pristine.ToSerializable()));
                outcomes.Add(new BlitzCardChoiceOutcome(
                    ForecastItemDetails.Card(card), ForecastItemDetails.Card(result)));
            }
            catch (Exception)
            {
                // A pool that cannot offer a replacement is the game's problem,
                // not a reason to drop the rest of the forecast.
            }
        }
        return outcomes;
    }

    /// <summary>
    /// Prices <paramref name="second"/> after <paramref name="first"/> was
    /// taken. The shop lets the player choose, and every purchase moves the
    /// streams the goods read, so the pair — not the single good — is what has
    /// an exact answer once an order is assumed.
    /// </summary>
    internal static BlitzYieldForecast PredictAfter(
        PlanningPredictionService.State probe,
        BlitzMerchantEntry first,
        BlitzMerchantEntry second)
    {
        var taken = PredictInPlace(probe, first);
        // A predecessor that stopped at a deck pick never spent its stream, so
        // its effect on the next good cannot be stated without inventing a pick.
        if (!taken.Resolved || taken.NeedsCardPick) return BlitzYieldForecast.Empty;
        return PredictInPlace(probe, second);
    }

    private static BlitzMerchantEntry Replica(BlitzMerchantEntry source, Player player)
    {
        var clone = (BlitzMerchantEntry)ShallowCopy.Invoke(source, null)!;
        PlayerField.SetValue(clone, player);
        foreach (var field in EventFields)
            field.SetValue(clone, null);
        return clone;
    }

    private static IReadOnlyList<IReadOnlyList<ForecastItemDetails>> CardScreens(IEnumerable<Reward> rewards) =>
        rewards.OfType<CardReward>()
            .Select(reward => (IReadOnlyList<ForecastItemDetails>)reward.Cards
                .Select(ForecastItemDetails.Card).ToArray())
            .Where(screen => screen.Count > 0)
            .ToArray();

    private static Dictionary<(string Id, bool Upgraded), int> CountDeck(Player player)
    {
        var counts = new Dictionary<(string, bool), int>();
        foreach (var card in player.Deck.Cards)
        {
            var key = (card.Id.Entry, card.IsUpgraded);
            counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
        }
        return counts;
    }

    /// <summary>
    /// Separates "a card joined the deck" from "an existing card was upgraded".
    /// <c>CardCmd.Upgrade</c> may swap the instance, so the two cases are told
    /// apart by counting (id, upgraded) instead of comparing references.
    /// </summary>
    private static (IReadOnlyList<ForecastItemDetails> Gained, IReadOnlyList<ForecastItemDetails> Upgraded) DiffDeck(
        Player player,
        Dictionary<(string Id, bool Upgraded), int> before,
        Dictionary<(string Id, bool Upgraded), int> after)
    {
        var gained = new List<ForecastItemDetails>();
        var upgraded = new List<ForecastItemDetails>();
        var remaining = new Dictionary<(string, bool), int>(after);

        foreach (var ((id, wasUpgraded), count) in before)
        {
            remaining.TryGetValue((id, wasUpgraded), out var now);
            if (now >= count) continue;
            var missing = count - now;
            remaining[(id, wasUpgraded)] = count;
            // A base copy that disappeared while a second upgraded copy appeared
            // is the upgrade itself; anything beyond that was granted as-is.
            if (wasUpgraded) continue;
            var beforeUpgraded = before.TryGetValue((id, true), out var upgradedBefore) ? upgradedBefore : 0;
            remaining.TryGetValue((id, true), out var nowUpgraded);
            var upgrades = Math.Min(missing, Math.Max(0, nowUpgraded - beforeUpgraded));
            remaining[(id, true)] = nowUpgraded - upgrades;
            for (var index = 0; index < upgrades; index++)
                upgraded.Add(DescribeDeckCard(player, id, upgraded: true));
        }

        foreach (var ((id, isUpgraded), count) in remaining)
        {
            var was = before.TryGetValue((id, isUpgraded), out var existed) ? existed : 0;
            for (var index = was; index < count; index++)
                gained.Add(DescribeDeckCard(player, id, isUpgraded));
        }

        return (gained, upgraded);
    }

    private static ForecastItemDetails DescribeDeckCard(Player player, string id, bool upgraded)
    {
        var card = player.Deck.Cards.FirstOrDefault(candidate =>
            candidate.Id.Entry == id && candidate.IsUpgraded == upgraded);
        return card is null ? ForecastItemDetails.Text(id) : ForecastItemDetails.Card(card);
    }

    /// <summary>
    /// Answers deck selections with nothing. Entries treat an empty answer as a
    /// cancelled purchase, which leaves the streams untouched so the probe can
    /// report "this needs your pick" without inventing one.
    /// </summary>
    private sealed class ProbeSelector(int? pick = null) : ICardSelector
    {
        public bool Asked { get; private set; }
        public int Pickable { get; private set; }

        /// <summary>
        /// The cards the game itself offered. Kept so a transform can answer for
        /// every one of them instead of stopping at the prompt.
        /// </summary>
        public IReadOnlyList<CardModel> Candidates { get; private set; } = [];

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            Asked = true;
            var candidates = options.ToList();
            Candidates = candidates;
            Pickable = candidates.Count;
            return Task.FromResult<IEnumerable<CardModel>>(
                pick is { } index && index >= 0 && index < candidates.Count
                    ? [candidates[index]]
                    : []);
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            Asked = true;
            Pickable = options.Count;
            return default;
        }
    }
}
