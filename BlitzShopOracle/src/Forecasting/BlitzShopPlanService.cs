using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace BlitzShopOracle.Forecasting;

/// <summary>
/// One good's answer after an ordered sequence of purchases. Stream offsets
/// can coincide across orders, while the cards offered and selected by those
/// purchases can differ. Keep that order through the deck-choice branches.
/// </summary>
internal sealed record BlitzPrefixOutcome(IReadOnlyList<int> PrefixSlots, BlitzYieldForecast Yield)
{
    /// <summary>
    /// False when a predecessor could not be completed on the shadow clone (for
    /// example random upgrades with nothing upgradable left). The row is then a
    /// dead end the window reports instead of guessing.
    /// </summary>
    public bool Completed => Yield.Purchased;
}

internal sealed record BlitzPlanGood(int Slot, BlitzMerchantEntry Entry, IReadOnlyList<BlitzPrefixOutcome> Outcomes)
{
    /// <summary>The answer with nothing bought before, i.e. the empty prefix.</summary>
    public BlitzPrefixOutcome? Direct => Outcomes.Count > 0 ? Outcomes[0] : null;

    internal BlitzPrefixOutcome? ForPrefix(IReadOnlyCollection<int> prefix) => Outcomes.FirstOrDefault(outcome =>
        outcome.PrefixSlots.SequenceEqual(prefix));
}

/// <summary>How far one shelf good moves the two streams the goods read.</summary>
internal sealed record BlitzStreamDelta(int Shops, int Rewards);

/// <summary>
/// The whole shelf at once, priced against reachable purchase sequences.
/// The planner window is a pure view over this — clicking
/// goods in a buy order just looks up each step's prefix row.
/// </summary>
internal sealed record BlitzShopPanorama(
    MerchantInventory Inventory,
    int ChoicesRemaining,
    IReadOnlyList<BlitzPlanGood> Goods,
    IReadOnlyList<string> Notes,
    IReadOnlyList<BlitzStreamDelta> StreamDeltas)
{
    /// <summary>
    /// Changes whenever a purchase would change an answer: remaining picks or
    /// any entry's availability. The window compares this before recomputing.
    /// </summary>
    public string Fingerprint => ChoicesRemaining + "|"
        + string.Join("|", Inventory.BlitzEntries.Select(entry => entry.MerchantEntryType + ":" + entry.IsStocked));

    internal BlitzPlanGood? GoodAt(int slot) => Goods.FirstOrDefault(good => good.Slot == slot);
}

/// <summary>One clicked buy order resolved against the panorama.</summary>
internal sealed record BlitzPlanSequence(
    IReadOnlyList<BlitzPlanStep> Steps,
    int ChoicesLeft,
    bool Valid,
    string? Rejection);

internal sealed record BlitzPlanStep(int Slot, BlitzPrefixOutcome Outcome);

internal static class BlitzShopPlanService
{
    /// <summary>
    /// Enumerates reachable ordered prefixes for each good. Each row uses
    /// RNG-positioned generation. The offsets come from the preview's own
    /// measured deltas, the contents from the game's factories.
    /// </summary>
    internal static BlitzShopPanorama Compute(
        PlanningPredictionService service,
        RunState liveRun,
        Player livePlayer,
        MerchantInventory inventory)
    {
        var preview = new BlitzShopPreview(service, liveRun, livePlayer);
        preview.MeasureDeltas(inventory);
        return Compute(preview, inventory);
    }

    /// <summary>
    /// The same enumeration from a preview the caller already built. Runs
    /// every step to completion in one call — the self-test path; the window
    /// itself steps the same work a few milliseconds per frame.
    /// </summary>
    internal static BlitzShopPanorama Compute(BlitzShopPreview preview, MerchantInventory inventory)
    {
        var enumeration = new BlitzShopEnumeration(preview, inventory);
        while (!enumeration.Done) enumeration.Step();
        return enumeration.Finish();
    }

    private static bool ContainsTwoMoreChoices(MerchantInventory inventory, IReadOnlyCollection<int> slots) =>
        slots.Any(slot => inventory.BlitzEntries[slot].MerchantEntryType == BlitzMerchantEntryType.TwoMoreChoices);

    /// <summary>
    /// The enumeration as resumable work: one step prices one (good, prefix)
    /// pair — an RNG-positioned factory generation, never a purchase, so the
    /// only thing that can touch the game is read-only model lookups.
    /// </summary>
    internal sealed class BlitzShopEnumeration
    {
        private readonly BlitzShopPreview _preview;
        private readonly MerchantInventory _inventory;
        private readonly int _remaining;
        private readonly Queue<(int Slot, int[] Prefix)> _pending = new();
        private readonly Dictionary<int, List<BlitzPrefixOutcome>> _outcomes = [];
        private readonly List<(int Slot, int[] Prefix, IReadOnlyList<int> RepPrefix)> _aliases = [];

        /// <summary>Rows that actually generate. The dedupe map keeps the rest exact without paying for them.</summary>
        internal int RowsPriced { get; private set; }

        /// <summary>Every (good, prefix) row the panorama will expose, priced or aliased.</summary>
        internal int RowsPlanned { get; private set; }

        internal BlitzShopEnumeration(BlitzShopPreview preview, MerchantInventory inventory)
        {
            _preview = preview;
            _inventory = inventory;
            _remaining = inventory.BlitzChoicesRemaining;

            var available = new List<int>();
            for (var slot = 0; slot < inventory.BlitzEntries.Count; slot++)
                if (inventory.BlitzEntries[slot].IsStocked)
                    available.Add(slot);
            // A disabled shelf (or a zero budget) has nothing left to price.
            if (_remaining <= 0 || available.Count == 0) return;

            var rows = new List<(int Slot, int[] Prefix)>();
            foreach (var slot in available)
            {
                var others = available.Where(candidate => candidate != slot).ToArray();
                for (var mask = 0; mask < 1 << others.Length; mask++)
                {
                    var prefix = others.Where((_, bit) => (mask & (1 << bit)) != 0).ToArray();
                    foreach (var order in Permutations(prefix))
                        if (FitsBudget(inventory, order, _remaining))
                            rows.Add((slot, order.ToArray()));
                }
                _outcomes[slot] = [];
            }
            RowsPlanned = rows.Count;

            // Short prefixes first: the outright row and the one-predecessor
            // rows are what the first clicks read, so they land earliest.
            // Two predecessor sets that push the streams by the same amounts
            // hand a stream-reading good the same contents (the
            // order-invariance doc), so for the goods where that is airtight
            // only one representative per signature is generated.
            var decided = new Dictionary<int, List<((int Shops, int Rewards) Signature, int[] Prefix)>>();
            foreach (var slot in available) decided[slot] = [];
            foreach (var row in rows.OrderBy(row => row.Prefix.Length).ThenBy(row => row.Slot))
            {
                if (DedupeSafe(inventory, row.Slot))
                {
                    var signature = Signature(inventory, row.Prefix, preview);
                    var match = decided[row.Slot]
                        .FirstOrDefault(entry => entry.Signature == signature);
                    if (match.Prefix is not null)
                    {
                        _aliases.Add((row.Slot, row.Prefix, match.Prefix));
                        continue;
                    }
                    decided[row.Slot].Add((signature, row.Prefix));
                }
                _pending.Enqueue(row);
            }
        }

        private static (int Shops, int Rewards) Signature(
            MerchantInventory inventory, IReadOnlyList<int> prefix, BlitzShopPreview preview)
        {
            var shops = 0;
            var rewards = 0;
            foreach (var slot in prefix)
            {
                var delta = preview.Delta(inventory.BlitzEntries[slot].MerchantEntryType);
                shops += delta.Shops;
                rewards += delta.Rewards;
            }
            return (shops, rewards);
        }

        /// <summary>
        /// Dedupe is sound only where the good reads streams and nothing else
        /// the predecessors can change: not the relic goods (they read the
        /// shared bag, whose per-rarity buckets depend on which predecessor
        /// rolled which rarity), not the deck pickers (their answer lists the
        /// deck), and never while random upgrades are around, because those
        /// outcomes depend on earlier deck choices.
        /// </summary>
        private static bool DedupeSafe(MerchantInventory inventory, int slot)
        {
            var type = inventory.BlitzEntries[slot].MerchantEntryType;
            if (type is BlitzMerchantEntryType.Relic or BlitzMerchantEntryType.TwoRelics
                or BlitzMerchantEntryType.RandomUpgrades)
                return false;
            return !NeedsDeckPick(inventory.BlitzEntries[slot])
                   && !inventory.BlitzEntries.Any(entry => entry.IsStocked
                       && entry.MerchantEntryType == BlitzMerchantEntryType.RandomUpgrades);
        }

        private static bool FitsBudget(MerchantInventory inventory, IReadOnlyList<int> prefix, int choices)
        {
            foreach (var slot in prefix)
            {
                if (choices <= 0) return false;
                choices--;
                if (inventory.BlitzEntries[slot].MerchantEntryType == BlitzMerchantEntryType.TwoMoreChoices)
                    choices += 2;
            }
            return choices > 0;
        }

        internal bool Done => _pending.Count == 0;

        /// <summary>Prefix pairs left to price, for progress display.</summary>
        internal int Remaining => _pending.Count;

        internal void Step()
        {
            if (Done) return;
            var (slot, prefix) = _pending.Dequeue();
            _outcomes[slot].Add(new BlitzPrefixOutcome(prefix,
                _preview.Preview(_inventory, slot, prefix)));
            RowsPriced++;
        }

        internal BlitzShopPanorama Finish()
        {
            // Aliased rows borrow their representative's answer: same stream
            // signature, so same contents. They keep their own prefix so the
            // planner's lookups stay per-combination.
            foreach (var (slot, prefix, repPrefix) in _aliases)
            {
                var rep = _outcomes[slot].FirstOrDefault(outcome =>
                    outcome.PrefixSlots.SequenceEqual(repPrefix));
                _outcomes[slot].Add(new BlitzPrefixOutcome(prefix,
                    rep?.Yield ?? BlitzYieldForecast.Empty));
            }

            var goods = _outcomes
                .OrderBy(pair => pair.Key)
                .Select(pair => new BlitzPlanGood(
                    pair.Key, _inventory.BlitzEntries[pair.Key], pair.Value))
                .ToList();
            var streamDeltas = Enumerable.Range(0, _inventory.BlitzEntries.Count)
                .Select(slot => new BlitzStreamDelta(
                    _preview.Delta(_inventory.BlitzEntries[slot].MerchantEntryType).Shops,
                    _preview.Delta(_inventory.BlitzEntries[slot].MerchantEntryType).Rewards))
                .ToList();
            return new BlitzShopPanorama(_inventory, _remaining, goods, Notes(_inventory, goods), streamDeltas);
        }
    }

    private static IReadOnlyList<string> Notes(MerchantInventory inventory, IReadOnlyList<BlitzPlanGood> goods)
    {
        var notes = new List<string>();
        if (inventory.BlitzEntries.Any(entry => entry.MerchantEntryType == BlitzMerchantEntryType.RandomUpgrades
                && entry.IsStocked))
            notes.Add("RandomUpgradesOrder");
        // More transform candidates than the deck can hold means earlier buys
        // offered reward cards the table now answers for too.
        if (goods.Any(good => good.Entry.MerchantEntryType == BlitzMerchantEntryType.CardTransform
                && good.Outcomes.Any(outcome => outcome.Yield.TransformChoices.Count
                    > outcome.Yield.PickableCards)))
            notes.Add("RewardTransform");
        if (inventory.BlitzEntries.Any(entry => entry.IsStocked && NeedsDeckPick(entry)))
            notes.Add("DeckPickCompletion");
        if (inventory.BlitzEntries.Any(entry => entry.MerchantEntryType == BlitzMerchantEntryType.TwoMoreChoices
                && entry.IsStocked))
            notes.Add("TwoMoreChoices");
        return notes;
    }

    /// <summary>
    /// Whether the good's own purchase waits for a deck selection. Kept in sync
    /// with the entry types whose <c>OnTrySelect</c> asks the selector first.
    /// </summary>
    internal static bool NeedsDeckPick(BlitzMerchantEntry entry) => entry.MerchantEntryType is BlitzMerchantEntryType.CardUpgrade
        or BlitzMerchantEntryType.CardRemoval
        or BlitzMerchantEntryType.CardTransform
        or BlitzMerchantEntryType.CardEnchant
        or BlitzMerchantEntryType.Glam;

    /// <summary>
    /// One demanded outcome: the good, plus every predecessor set that hands
    /// over exactly that variant. The window derives the sets by matching the
    /// variant's text against the panorama rows.
    /// </summary>
    internal sealed record BlitzWish(int Slot, IReadOnlyList<IReadOnlyList<int>> AllowedPrefixes)
    {
        internal IReadOnlyList<BlitzWishPath>? ChoicePaths { get; init; }
    }

    internal sealed record BlitzWishPath(
        IReadOnlyList<int> PrefixSlots,
        IReadOnlyList<BlitzRandomUpgradeChoice> Choices);

    /// <summary>
    /// A solved buy order for a wish list, or the reason none exists. Because a
    /// good's contents depend only on the SET bought before it (never their
    /// order), every distinct predecessor set is one essentially-different
    /// solution and each set admits every valid order of itself — the counts
    /// report how many of each exist.
    /// </summary>
    internal sealed record BlitzWishSolution(
        bool Solved,
        IReadOnlyList<BlitzPlanStep> Steps,
        int ChoicesLeft,
        IReadOnlyList<int> FillerSlots,
        string? Reason,
        int TotalValidOrders,
        int TotalSolutionSets)
    {
        public static readonly BlitzWishSolution None = new(false, [], 0, [], null, 0, 0);
        internal IReadOnlyList<BlitzRandomUpgradeChoice> Choices { get; init; } = [];
    }

    /// <summary>
    /// Finds the buy order that hands every wished good its wished variant —
    /// the demand-oriented counterpart of the planner. Pure lookup over the
    /// panorama: for each candidate buy set (wished goods plus the fewest
    /// fillers needed to push the streams) and each order of it, a good's
    /// prefix at its position decides its variant, so the search never touches
    /// a clone. Fewest fillers first, then the lexicographically smallest
    /// order, keeps the answer deterministic.
    /// </summary>
    internal static BlitzWishSolution Solve(BlitzShopPanorama panorama, IReadOnlyList<BlitzWish> wishes)
    {
        var bySlot = new Dictionary<int, IReadOnlyList<IReadOnlyList<int>>>();
        foreach (var wish in wishes)
        {
            if (panorama.GoodAt(wish.Slot) is null)
                return BlitzWishSolution.None with { Reason = "Unavailable:" + wish.Slot };
            bySlot[wish.Slot] = wish.AllowedPrefixes;
        }
        if (bySlot.Count == 0)
            return BlitzWishSolution.None with { Reason = "NoWishes" };

        var extras = panorama.Goods.Select(good => good.Slot)
            .Except(bySlot.Keys).OrderBy(slot => slot).ToList();
        // "Two more choices" is the only purchase that grows the budget; the
        // set-level check is the existence of a valid order (buy it early).
        var maxPurchases = panorama.ChoicesRemaining
            + 2 * (panorama.Goods.Any(good => good.Entry.MerchantEntryType == BlitzMerchantEntryType.TwoMoreChoices)
                ? 1 : 0);

        BlitzWishSolution? first = null;
        var validOrders = 0;
        var solutionSets = new HashSet<string>();
        for (var fillerCount = 0; fillerCount <= extras.Count; fillerCount++)
        {
            foreach (var fillers in Combinations(extras, fillerCount))
            {
                var buy = bySlot.Keys.Concat(fillers).OrderBy(slot => slot).ToList();
                if (buy.Count > maxPurchases) continue;
                foreach (var order in Permutations(buy))
                {
                    var prefix = new List<int>();
                    var ok = true;
                    foreach (var slot in order)
                    {
                        if (bySlot.TryGetValue(slot, out var allowed) && !ContainsPrefix(allowed, prefix))
                        {
                            ok = false;
                            break;
                        }
                        prefix.Add(slot);
                    }
                    if (!ok) continue;
                    // The same budget walk the buttons read — the final word on
                    // playability, TMC timing included.
                    var sequence = Simulate(panorama, order);
                    if (!sequence.Valid) continue;
                    var choicePaths = wishes.Where(wish => wish.ChoicePaths is not null)
                        .Select(wish => wish.ChoicePaths!
                            .Where(path => path.PrefixSlots.SequenceEqual(order.TakeWhile(slot => slot != wish.Slot)))
                            .ToArray())
                        .ToArray();
                    var selectedChoices = CompatibleChoices(choicePaths, 0, []);
                    if (selectedChoices is null) continue;
                    validOrders++;
                    solutionSets.Add(string.Join(",", fillers));
                    first ??= new BlitzWishSolution(true,
                        ResolveSelectedVariants(sequence.Steps, selectedChoices), sequence.ChoicesLeft,
                        fillers, null, 0, 0) { Choices = selectedChoices };
                }
            }
        }

        if (first is { } solved)
            return solved with { TotalValidOrders = validOrders, TotalSolutionSets = solutionSets.Count };

        return BlitzWishSolution.None with
        {
            Reason = InsufficientBudget(panorama, bySlot, maxPurchases) ? "OutOfPicks" : "Conflict"
        };
    }

    /// <summary>
    /// A lower bound on the buys the wishes force: the wished goods plus the
    /// smallest allowed prefix of each. Below the budget the failure is
    /// positioning, not picks.
    /// </summary>
    private static bool InsufficientBudget(
        BlitzShopPanorama panorama,
        Dictionary<int, IReadOnlyList<IReadOnlyList<int>>> bySlot,
        int maxPurchases)
    {
        var needed = new HashSet<int>(bySlot.Keys);
        foreach (var (_, allowed) in bySlot)
            needed.UnionWith(allowed.OrderBy(prefix => prefix.Count).First());
        return needed.Count > maxPurchases;
    }

    private static bool ContainsPrefix(IReadOnlyList<IReadOnlyList<int>> allowed, IReadOnlyList<int> prefix) =>
        allowed.Any(candidate => candidate.SequenceEqual(prefix));

    private static IReadOnlyList<BlitzRandomUpgradeChoice>? CompatibleChoices(
        IReadOnlyList<BlitzWishPath[]> paths,
        int index,
        IReadOnlyList<BlitzRandomUpgradeChoice> selected)
    {
        if (index == paths.Count) return selected;
        foreach (var path in paths[index])
        {
            if (path.Choices.Any(choice => selected.Any(existing =>
                    existing.SourceSlot == choice.SourceSlot && existing.ScreenIndex == choice.ScreenIndex
                    && !SameChoice(existing, choice)))) continue;
            var result = CompatibleChoices(paths, index + 1,
                selected.Concat(path.Choices).Distinct().ToArray());
            if (result is not null) return result;
        }
        return null;
    }

    private static bool SameChoice(BlitzRandomUpgradeChoice left, BlitzRandomUpgradeChoice right) =>
        left.Kind == right.Kind
        && left.Card?.Id?.Entry == right.Card?.Id?.Entry && left.Card?.IsUpgraded == right.Card?.IsUpgraded
        && left.Card?.StateKey == right.Card?.StateKey
        && left.Result?.Id?.Entry == right.Result?.Id?.Entry && left.Result?.IsUpgraded == right.Result?.IsUpgraded
        && left.Result?.StateKey == right.Result?.StateKey;

    private static IReadOnlyList<BlitzPlanStep> ResolveSelectedVariants(
        IReadOnlyList<BlitzPlanStep> steps,
        IReadOnlyList<BlitzRandomUpgradeChoice> choices) => steps.Select(step =>
    {
        var variant = step.Outcome.Yield.RandomUpgradeVariants.FirstOrDefault(candidate =>
            candidate.Choices.All(choice => choices.Any(existing =>
                existing.SourceSlot == choice.SourceSlot && existing.ScreenIndex == choice.ScreenIndex
                && SameChoice(existing, choice))));
        return variant is null ? step : step with
        {
            Outcome = step.Outcome with
            {
                Yield = step.Outcome.Yield with
                {
                    UpgradedCards = variant.UpgradedCards,
                    RandomUpgradeVariants = []
                }
            }
        };
    }).ToArray();

    private static IEnumerable<IReadOnlyList<int>> Combinations(IReadOnlyList<int> items, int count)
    {
        if (count == 0)
        {
            yield return [];
            yield break;
        }
        for (var index = 0; index <= items.Count - count; index++)
            foreach (var tail in Combinations(items.Skip(index + 1).ToList(), count - 1))
                yield return items.Skip(index).Take(1).Concat(tail).ToList();
    }

    private static IEnumerable<IReadOnlyList<int>> Permutations(IReadOnlyList<int> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }
        for (var index = 0; index < items.Count; index++)
        {
            var rest = items.Where((_, position) => position != index).ToList();
            foreach (var tail in Permutations(rest))
                yield return items.Skip(index).Take(1).Concat(tail).ToList();
        }
    }

    /// <summary>
    /// Resolves a clicked buy order against the panorama: each step's answer is
    /// the row for "this good after exactly the goods clicked before it". This
    /// is bookkeeping only — no clone is touched, so replanning is instant.
    /// </summary>
    internal static BlitzPlanSequence Simulate(BlitzShopPanorama panorama, IReadOnlyList<int> order)
    {
        var steps = new List<BlitzPlanStep>();
        var choices = panorama.ChoicesRemaining;
        var bought = new List<int>();
        foreach (var slot in order)
        {
            var good = panorama.GoodAt(slot);
            if (good is null)
                return new BlitzPlanSequence(steps, choices, false, "Unavailable:" + slot);
            if (choices <= 0)
                return new BlitzPlanSequence(steps, choices, false, "NoChoicesLeft");
            // The exact prefix row, never a stand-in: silently substituting the
            // outright answer for a combination that was never priced would
            // promise the player the wrong contents.
            var outcome = good.ForPrefix(bought);
            if (outcome is null)
                return new BlitzPlanSequence(steps, choices, false, "NotPriced:" + slot);
            steps.Add(new BlitzPlanStep(slot, outcome));
            bought.Add(slot);
            choices--;
            if (good.Entry.MerchantEntryType == BlitzMerchantEntryType.TwoMoreChoices)
                choices += 2;
        }
        return new BlitzPlanSequence(steps, choices, true, null);
    }

    /// <summary>
    /// What one clicked order adds up to, as counters the window can render:
    /// card-reward screens waiting for a pick, cards that join the deck on
    /// their own (curses counted separately), upgrades, potions, relics, max
    /// HP, healing and the picks left over.
    /// </summary>
    internal sealed record PlanTotals(
        int Screens, int Cards, int Curses, int Upgraded, int Potions, int Relics,
        int MaxHp, int Healed, int PicksLeft);

    internal static PlanTotals Total(BlitzPlanSequence sequence)
    {
        var screens = 0;
        var cards = 0;
        var curses = 0;
        var upgraded = 0;
        var potions = 0;
        var relics = 0;
        var maxHp = 0;
        var healed = 0;
        foreach (var step in sequence.Steps)
        {
            var yield = step.Outcome.Yield;
            screens += yield.CardScreens.Count;
            cards += yield.GainedCards.Count;
            curses += yield.GainedCards.Count(card => card.Rarity == ForecastItemRarity.Curse);
            upgraded += yield.UpgradedCards.Count;
            potions += yield.Potions.Count;
            relics += yield.Relics.Count;
            maxHp += yield.MaxHpDelta;
            healed += yield.HealedHp;
        }
        return new PlanTotals(screens, cards, curses, upgraded, potions, relics, maxHp, healed,
            sequence.ChoicesLeft);
    }
}
