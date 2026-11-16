#if DEBUG
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;
using SeedOracle.Forecasting;
using SeedOracle.Integration;
using SeedOracle.UI;

namespace SeedOracle.Validation;

/// <summary>
/// Self-checks for the multi-purchase Blitz shop forecast: the panorama
/// covers every reachable predecessor set, the set-level answers are what a
/// real sequence produces (and order-invariant), and the window renders them
/// and folds without disappearing. One shelf, one panorama, shared by all
/// checks — the whole suite runs synchronously on the game thread, so it
/// must not re-enumerate the shelf it already priced.
/// </summary>
internal static class BlitzShopPlanRegressionTests
{
    internal static void Run(RunState live)
    {
        var planning = new PlanningPredictionService();
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var state = BlitzState(planning, live, player);
        using var isolation = ShadowIsolation.Enter(state.Player);
        using var discovery = ShadowIsolation.GenerateMerchantInventory();
        var inventory = MerchantInventory.CreateForBlitzMerchant(state.Player);
        using var registration = ShadowIsolation.UseMerchantInventories([inventory]);

        var preview = new BlitzShopPreview(planning, state.Run, state.Player);
        preview.MeasureDeltas(inventory);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var panorama = BlitzShopPlanService.Compute(preview, inventory);
        var elapsed = watch.ElapsedMilliseconds;

        VerifyPanorama(planning, state, inventory, preview, panorama, elapsed);
        VerifyOrderInvariance(planning, state);
        VerifySequenceLookup(planning, state, inventory, panorama);
        VerifyDedupe(planning, state);
        VerifyRewardTransform(planning, state);
        VerifyRandomUpgradeDeckBranches(planning, state);
        BlitzReplayRegressionTests.VerifyMixedRewardStreams(planning, state);
        VerifyAllTypesParity(planning, live, state);
        VerifyWishSolver(inventory);
        VerifyPanel(state, inventory, panorama);
    }

    /// <summary>
    /// The panorama must price every available good against every predecessor
    /// set that fits the pick budget, its "buy outright" row must say exactly
    /// what a real probe purchase hands over (the RNG-positioned generator
    /// against the game's own purchase path — the parity gate for the whole
    /// preview engine), and the stepped enumeration the window drives must
    /// land on the same answers.
    /// </summary>
    private static void VerifyPanorama(
        PlanningPredictionService planning,
        PlanningPredictionService.State state,
        MerchantInventory inventory,
        BlitzShopPreview preview,
        BlitzShopPanorama panorama,
        long elapsed)
    {
        var available = Enumerable.Range(0, inventory.BlitzEntries.Count)
            .Where(slot => inventory.BlitzEntries[slot].IsStocked).ToArray();
        Require(panorama.Goods.Count == available.Length,
            $"the panorama priced {panorama.Goods.Count} goods while {available.Length} were on the shelf");
        Require(elapsed < 30000,
            $"enumerating a whole shelf took {elapsed} ms");

        foreach (var good in panorama.Goods)
        {
            foreach (var outcome in good.Outcomes)
            {
                Require(outcome.PrefixSlots.Count + 1 <= inventory.BlitzChoicesRemaining
                        + 2 * (ContainsTwoMoreChoices(inventory, outcome.PrefixSlots) ? 1 : 0),
                    $"a prefix of {outcome.PrefixSlots.Count} goods exceeded the pick budget");
                Require(!outcome.PrefixSlots.Contains(good.Slot),
                    "a good appeared in its own predecessor set");
            }

            // Parity with the game's own purchase path, direct prefix.
            var direct = good.Direct;
            Require(direct is not null, $"good {good.Slot} has no outright answer");
            var hover = BlitzMerchantProbe.Predict(planning, state, inventory.BlitzEntries[good.Slot]);
            Require(OutcomeText(direct!.Yield) == OutcomeText(hover),
                $"good {good.Slot} disagrees with the probe purchase: "
                + $"'{OutcomeText(direct.Yield)}' vs '{OutcomeText(hover)}'");

            // And with one non-empty prefix, bought for real on a clone in
            // both orders — the same parity the order-invariance check uses.
            var other = available.FirstOrDefault(slot => slot != good.Slot, -1);
            if (other >= 0 && good.ForPrefix([other]) is { } row)
            {
                var walk = planning.Restore(planning.Capture(state));
                Require(BlitzMerchantProbe.PredictInPlace(walk, inventory.BlitzEntries[other], deckPick: 0).Purchased,
                    $"predecessor {other} could not be completed for the parity walk");
                var walked = BlitzMerchantProbe.PredictInPlace(walk, inventory.BlitzEntries[good.Slot]);
                Require(MatchesNative(row.Yield, walked),
                    $"good {good.Slot} after {other} disagrees with the clone walk: "
                    + $"'{OutcomeText(row.Yield)}' vs '{OutcomeText(walked)}'");
            }
        }

        // The window prices the same work a few steps per frame; stepping must
        // land on exactly the batched answers.
        var stepped = new BlitzShopPlanService.BlitzShopEnumeration(preview, inventory);
        while (!stepped.Done) stepped.Step();
        var steppedPanorama = stepped.Finish();
        Require(steppedPanorama.Goods.Count == panorama.Goods.Count
                && steppedPanorama.Goods.Select(good => good.Slot).SequenceEqual(
                    panorama.Goods.Select(good => good.Slot)),
            "the stepped enumeration priced different goods than the batched one");
        foreach (var (batched, piecemeal) in panorama.Goods.Zip(steppedPanorama.Goods))
            Require(batched.Outcomes.Count == piecemeal.Outcomes.Count
                    && batched.Outcomes.Zip(piecemeal.Outcomes).All(pair =>
                        pair.First.PrefixSlots.SequenceEqual(pair.Second.PrefixSlots)
                        && OutcomeText(pair.First.Yield) == OutcomeText(pair.Second.Yield)),
                $"good {batched.Slot} differs between batched and stepped enumeration");

        var rows = panorama.Goods.Sum(good => good.Outcomes.Count);
        Smoke.SmokeReport.Add(
            $"blitz-plan PASS: goods={panorama.Goods.Count} rows={rows} ms={elapsed} "
            + $"picks={inventory.BlitzChoicesRemaining}");
    }

    /// <summary>
    /// The core claim the window is built on, measured rather than assumed: a
    /// good's answer depends on which goods went first, not on their order.
    /// Two clones buy the same two predecessors in opposite orders and must
    /// hand the third good the same contents.
    /// </summary>
    private static void VerifyOrderInvariance(
        PlanningPredictionService planning,
        PlanningPredictionService.State state)
    {
        var first = BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.CardRewards);
        var second = BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.Potions);
        var subject = BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.UpgradedCardReward);

        var ab = Buy(planning, state, [first, second, subject]);
        var ba = Buy(planning, state, [second, first, subject]);
        Require(ab is not null && ba is not null, "an order-invariance probe could not complete");
        Require(OutcomeText(ab!) == OutcomeText(ba!),
            $"the same predecessors in a different order changed the good: "
            + $"'{OutcomeText(ab!)}' vs '{OutcomeText(ba!)}'");

        Smoke.SmokeReport.Add(
            $"blitz-plan-order PASS: subject='{One(OutcomeText(ab!))}'");
    }

    /// <summary>
    /// The planner is a lookup, so a clicked order must resolve to exactly the
    /// rows an independent clone walk produces — including the budget walk for
    /// sequences that spend all three picks.
    /// </summary>
    private static void VerifySequenceLookup(
        PlanningPredictionService planning,
        PlanningPredictionService.State state,
        MerchantInventory inventory,
        BlitzShopPanorama panorama)
    {
        var order = Enumerable.Range(0, inventory.BlitzEntries.Count)
            .Where(slot => inventory.BlitzEntries[slot].IsStocked).Take(3).ToArray();
        Require(order.Length == 3, "the shelf could not fill a three-pick plan");

        var sequence = BlitzShopPlanService.Simulate(panorama, order);
        Require(sequence.Valid, "a three-pick order over a full shelf was rejected: " + sequence.Rejection);
        Require(sequence.Steps.Count == 3, "the planner dropped steps");
        var expectedLeft = inventory.BlitzChoicesRemaining - 3
            + 2 * order.Count(slot => inventory.BlitzEntries[slot].MerchantEntryType
                == BlitzMerchantEntryType.TwoMoreChoices);
        Require(sequence.ChoicesLeft == expectedLeft,
            $"the planner counted {sequence.ChoicesLeft} picks left instead of {expectedLeft}");

        for (var index = 0; index < order.Length; index++)
        {
            // The same row the engine computed: predecessors completed, then
            // the good itself without inventing a pick.
            var probe = planning.Restore(planning.Capture(state));
            for (var before = 0; before < index; before++)
                Require(BlitzMerchantProbe.PredictInPlace(probe, inventory.BlitzEntries[order[before]], deckPick: 0)
                        .Purchased,
                    $"predecessor {order[before]} could not be completed during the sequence walk");
            var walked = BlitzMerchantProbe.PredictInPlace(probe, inventory.BlitzEntries[order[index]]);
            Require(MatchesNative(sequence.Steps[index].Outcome.Yield, walked),
                $"step {index + 1} disagrees with the clone walk: "
                + $"'{OutcomeText(walked)}' vs '{OutcomeText(sequence.Steps[index].Outcome.Yield)}'");
        }

        // A fourth click beyond the budget must be refused — and the planner
        // refuses it in the same place the buttons read.
        var overflow = BlitzShopPlanService.Simulate(panorama, order.Append(order[0]).ToArray());
        Require(!overflow.Valid, "the planner accepted a purchase past the pick budget");
        var totals = BlitzShopPlanService.Total(sequence);
        Smoke.SmokeReport.Add(
            $"blitz-plan-seq PASS: steps={sequence.Steps.Count} left={sequence.ChoicesLeft} "
            + $"screens={totals.Screens} cards={totals.Cards} curses={totals.Curses} "
            + $"potions={totals.Potions} relics={totals.Relics}");
    }

    /// <summary>
    /// Two predecessors that push the streams by the same amounts hand a
    /// stream-reading good the same contents — the panorama may therefore
    /// answer both from one real purchase. Checked three ways: the aliased
    /// rows agree, they agree with an honest clone purchase, and the
    /// enumeration really did buy fewer rows than it exposes.
    /// </summary>
    private static void VerifyDedupe(
        PlanningPredictionService planning,
        PlanningPredictionService.State state)
    {
        // Rare and colorless card rewards both cost Shops +9, so a good priced
        // after either must see the same stream position.
        using var isolation = ShadowIsolation.Enter(state.Player);
        using var discovery = ShadowIsolation.GenerateMerchantInventory();
        var entries = new List<BlitzMerchantEntry>
        {
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.RareCardReward),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.ColorlessCardReward),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.Potions),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.Relic)
        };
        var inventory = new MerchantInventory(state.Player, [], [], [], [], null, entries);
        inventory.Setup();
        using var registration = ShadowIsolation.UseMerchantInventories([inventory]);

        var preview = new BlitzShopPreview(planning, state.Run, state.Player);
        preview.MeasureDeltas(inventory);
        var enumeration = new BlitzShopPlanService.BlitzShopEnumeration(preview, inventory);
        while (!enumeration.Done) enumeration.Step();
        var panorama = enumeration.Finish();

        var potions = panorama.GoodAt(2);
        Require(potions is not null, "the dedupe fixture lost its potions good");
        var afterRare = potions!.ForPrefix([0]);
        var afterColorless = potions.ForPrefix([1]);
        Require(afterRare is not null && afterColorless is not null,
            "the dedupe fixture lost a one-predecessor row");
        Require(OutcomeText(afterRare!.Yield) == OutcomeText(afterColorless!.Yield),
            $"two +9 predecessors gave the potions different answers: "
            + $"'{OutcomeText(afterRare.Yield)}' vs '{OutcomeText(afterColorless.Yield)}'");

        // And the shared answer is what buying the other one for real gives.
        var probe = planning.Restore(planning.Capture(state));
        Require(BlitzMerchantProbe.PredictInPlace(probe, entries[1], deckPick: 0).Purchased,
            "the colorless predecessor could not be completed");
        var walked = BlitzMerchantProbe.PredictInPlace(probe, entries[2]);
        Require(OutcomeText(walked) == OutcomeText(afterRare.Yield),
            $"the aliased row disagrees with a real clone purchase: "
            + $"'{OutcomeText(afterRare.Yield)}' vs '{OutcomeText(walked)}'");

        // The whole point: fewer shadow purchases than exposed rows.
        Require(enumeration.RowsPlanned > enumeration.RowsPriced,
            $"the enumeration exposed {enumeration.RowsPlanned} rows but bought every one ({enumeration.RowsPriced})");
        Smoke.SmokeReport.Add(
            $"blitz-plan-dedupe PASS: rows={enumeration.RowsPlanned} bought={enumeration.RowsPriced} "
            + $"potions='{One(OutcomeText(afterRare.Yield))}'");
    }

    /// <summary>
    /// Transform answers for cards earlier rewards and curses hand over: the
    /// transform row after a card-reward predecessor must cover every card
    /// that reward's own row says it offers (two independent generation paths
    /// at the same offsets), and after a curse-granting buy it must cover the
    /// curse with its path tagged.
    /// </summary>
    private static void VerifyRewardTransform(
        PlanningPredictionService planning,
        PlanningPredictionService.State state)
    {
        var entries = new List<BlitzMerchantEntry>
        {
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.CardRewards),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.CardTransform),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.TwoMoreChoices)
        };
        var inventory = new MerchantInventory(state.Player, [], [], [], [], null, entries);
        inventory.Setup();
        using var registration = ShadowIsolation.UseMerchantInventories([inventory]);

        var preview = new BlitzShopPreview(planning, state.Run, state.Player);
        preview.MeasureDeltas(inventory);
        var panorama = BlitzShopPlanService.Compute(preview, inventory);

        var rewardRow = panorama.GoodAt(0)?.Direct;
        var transformRow = panorama.GoodAt(1)?.ForPrefix([0]);
        Require(rewardRow is not null && transformRow is not null,
            "the reward-transform fixture lost a row");
        var offered = rewardRow!.Yield.CardScreens.SelectMany(screen => screen)
            .Select(item => item.Name).ToHashSet();
        var rewardChoices = transformRow!.Yield.TransformChoices
            .Where(choice => choice.Origin == "reward").ToList();
        Require(offered.SetEquals(rewardChoices.Select(choice => choice.Source.Name)),
            "the transform table's reward candidates disagree with the reward's own screens: "
            + $"missing [{string.Join(",", offered.Except(rewardChoices.Select(c => c.Source.Name)))}]");
        Require(rewardChoices.All(choice => choice.Result.Name.Length > 0
                && choice.Result.Name != choice.Source.Name),
            "a reward candidate's transform result is unnamed or did not change");

        // The curse the budget good hands over joins the deck and is
        // transformable: its row must answer for it, with the path tagged.
        var curse = panorama.GoodAt(2)?.Direct?.Yield.GainedCards.FirstOrDefault()?.Name;
        var transformAfterCurse = panorama.GoodAt(1)?.ForPrefix([2]);
        Require(curse is not null && transformAfterCurse is not null,
            "the curse-transform fixture lost a row");
        var curseChoice = transformAfterCurse!.Yield.TransformChoices
            .FirstOrDefault(choice => choice.Source.Name == curse);
        Require(curseChoice is not null,
            $"the curse {curse} never appeared as a transform candidate; row resolved={transformAfterCurse.Yield.Resolved} "
            + $"choices={transformAfterCurse.Yield.TransformChoices.Count} pickable={transformAfterCurse.Yield.PickableCards} "
            + $"origins=[{string.Join(",", transformAfterCurse.Yield.TransformChoices.Select(c => c.Origin).Distinct())}] "
            + $"lastError={BlitzShopPreview.LastTransformError}");
        Require(curseChoice!.Origin == "curse" && curseChoice.ViaSlots.SequenceEqual([2]),
            $"the curse candidate's path is {curseChoice.Origin} via [{string.Join(",", curseChoice.ViaSlots)}]");
        Require(curseChoice.Result.Name.Length > 0 && curseChoice.Result.Name != curse,
            "the curse's transform result is unnamed or did not change");

        var covered = rewardChoices.Count;
        Smoke.SmokeReport.Add(
            $"blitz-plan-rewardtransform PASS: offered={offered.Count} covered={covered} "
            + $"curse='{curse}'→'{curseChoice.Result.Name}' "
            + $"table={transformRow.Yield.TransformChoices.Count} pickable={transformRow.Yield.PickableCards}");
    }

    private static void VerifyRandomUpgradeDeckBranches(
        PlanningPredictionService planning,
        PlanningPredictionService.State state)
    {
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        var entries = new List<BlitzMerchantEntry>
        {
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.CardRewards),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.CardRemoval),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.CardTransform),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.RandomUpgrades)
        };
        var inventory = new MerchantInventory(state.Player, [], [], [], [], null, entries);
        inventory.Setup();
        using var registration = ShadowIsolation.UseMerchantInventories([inventory]);

        var preview = new BlitzShopPreview(planning, state.Run, state.Player);
        preview.MeasureDeltas(inventory);
        var panorama = BlitzShopPlanService.Compute(preview, inventory);
        var random = panorama.GoodAt(3);
        var reward = random?.ForPrefix([0]);
        var removal = random?.ForPrefix([1]);
        var transform = random?.ForPrefix([2]);
        Require(reward?.Yield.RandomUpgradeVariants.Count > 1,
            "a card reward before RandomUpgrades produced no choice branches");
        Require(removal?.Yield.RandomUpgradeVariants.Any(variant =>
                variant.Choices.Any(choice => choice.Kind == BlitzRandomUpgradeChoiceKind.Remove)) == true,
            "a card removal before RandomUpgrades did not change the deck branches");
        Require(transform?.Yield.RandomUpgradeVariants.Any(variant =>
                variant.Choices.Any(choice => choice.Kind == BlitzRandomUpgradeChoiceKind.Transform
                    && choice.Result is not null)) == true,
            "a card transform before RandomUpgrades did not carry its replacement card");

        Smoke.SmokeReport.Add(
            $"blitz-random-deck-branches PASS: reward={reward!.Yield.RandomUpgradeVariants.Count} "
            + $"remove={removal!.Yield.RandomUpgradeVariants.Count} "
            + $"transform={transform!.Yield.RandomUpgradeVariants.Count}");
    }

    /// <summary>
    /// Direct generation vs the game's real purchase path for EVERY entry
    /// type — the shelf-random parity gate can go a whole smoke without
    /// seeing a type (UpgradedRareCard's Uniform odds bug shipped exactly so).
    /// One synthetic shelf holding all twenty types, every direct row bought
    /// for real on a clone and compared verbatim.
    /// </summary>
    private static void VerifyAllTypesParity(
        PlanningPredictionService planning,
        RunState live,
        PlanningPredictionService.State state)
    {
        var types = Enum.GetValues<BlitzMerchantEntryType>().ToList();
        var entries = types.Select(type => BlitzMerchantEntry.Generate(state.Player, type)).ToList();
        var inventory = new MerchantInventory(state.Player, [], [], [], [], null, entries);
        inventory.Setup();
        using var registration = ShadowIsolation.UseMerchantInventories([inventory]);

        var preview = new BlitzShopPreview(planning, state.Run, state.Player);
        preview.MeasureDeltas(inventory);

        // Deck divergence diagnosis: the window prices deck-reading rows
        // against the preview's clone, the probe against its own — if the two
        // clones disagree, one of the state paths is unfaithful.
        string Digest(IEnumerable<CardModel> cards) => string.Join(",",
            cards.Select(card => card.Id.Entry + (card.IsUpgraded ? "+" : "")
                + (card.IsUpgradable ? "" : "!")));
        Smoke.SmokeReport.Add($"blitz-parity | deck source={Digest(state.Player.Deck.Cards)}");
        Smoke.SmokeReport.Add($"blitz-parity | deck preview={Digest(preview.DeckSnapshot)}");
        var probeDeckDigest = Digest(planning.Restore(planning.Capture(state)).Player.Deck.Cards);
        Smoke.SmokeReport.Add($"blitz-parity | deck probe={probeDeckDigest}");
        Smoke.SmokeReport.Add($"blitz-parity | deck live={Digest(live.Players[0].Deck.Cards)}");

        var failures = new List<string>();
        foreach (var slot in Enumerable.Range(0, entries.Count))
        {
            var mine = preview.Preview(inventory, slot, []);
            var probeClone = planning.Restore(planning.Capture(state));
            var shopsBefore = probeClone.Player.PlayerRng.Shops.ToSerializable().counter;
            var rewardsBefore = probeClone.Player.PlayerRng.Rewards.ToSerializable().counter;
            var probe = BlitzMerchantProbe.PredictInPlace(probeClone, entries[slot]);
            var shopsDrift = probeClone.Player.PlayerRng.Shops.ToSerializable().counter - shopsBefore;
            var rewardsDrift = probeClone.Player.PlayerRng.Rewards.ToSerializable().counter - rewardsBefore;
            var myDelta = preview.Delta(entries[slot].MerchantEntryType);
            var mineText = OutcomeText(mine);
            var probeText = OutcomeText(probe);
            var streamNote = myDelta.Shops == shopsDrift && myDelta.Rewards == rewardsDrift
                ? "stream=OK"
                : $"stream=MISMATCH mine=({myDelta.Shops},{myDelta.Rewards}) probe=({shopsDrift},{rewardsDrift})";
            Smoke.SmokeReport.Add(
                $"blitz-parity | {entries[slot].MerchantEntryType} {(mineText == probeText ? "OK" : "DIFF")} {streamNote}");
            if (mineText != probeText)
                failures.Add($"{entries[slot].MerchantEntryType}: mine='{One(mineText)}' probe='{One(probeText)}' {streamNote}");
        }
        Require(failures.Count == 0,
            "direct generation diverges from the real purchase path: " + string.Join("; ", failures));
        Smoke.SmokeReport.Add($"blitz-parity-alltypes PASS: types={types.Count}");
    }

    /// <summary>
    /// The wish solver against a hand-made table: prefix sets chosen so the
    /// three outcomes — solvable with a filler, mutually conflicting wishes,
    /// and not enough picks — are all deterministic.
    /// </summary>
    private static void VerifyWishSolver(MerchantInventory inventory)
    {
        // Slots 0/1/2 stand for goods A/B/C. A's variant after B differs from
        // its outright one, B's after A differs, C only has the outright one.
        BlitzPrefixOutcome Row(int[] prefix) => new(prefix, new BlitzYieldForecast { Resolved = true });
        var goods = new List<BlitzPlanGood>
        {
            new(0, inventory.BlitzEntries[0], [Row([]), Row([1])]),
            new(1, inventory.BlitzEntries[1], [Row([]), Row([0])]),
            new(2, inventory.BlitzEntries[2], [Row([])])
        };
        var table = new BlitzShopPanorama(inventory, 3, goods,
            [],
            [new BlitzStreamDelta(0, 0), new BlitzStreamDelta(9, 0), new BlitzStreamDelta(0, 0)]);

        // A's "after B" variant: B has to go first, as a filler.
        var solved = BlitzShopPlanService.Solve(table,
            [new BlitzShopPlanService.BlitzWish(0, [[1]])]);
        Require(solved.Solved, "a wish with a one-good prefix did not solve");
        Require(solved.Steps.Count == 2 && solved.Steps[0].Slot == 1 && solved.Steps[1].Slot == 0,
            "the solver bought " + string.Join(",", solved.Steps.Select(step => step.Slot)) + " instead of B then A");
        Require(solved.FillerSlots.SequenceEqual([1]),
            "the positioning purchase was not reported as a filler");
        Require(solved.ChoicesLeft == 1, $"two buys left {solved.ChoicesLeft} picks, not 1");
        Require(solved.TotalValidOrders >= 1 && solved.TotalSolutionSets >= 1,
            "the solver reported zero valid orders for a solvable wish");

        // A after B and B after A cannot both hold.
        var conflict = BlitzShopPlanService.Solve(table,
            [new BlitzShopPlanService.BlitzWish(0, [[1]]), new BlitzShopPlanService.BlitzWish(1, [[0]])]);
        Require(!conflict.Solved, "the solver satisfied mutually conflicting orders");
        Require(conflict.Reason == "Conflict",
            $"a conflict was reported as {conflict.Reason}");

        // The same table with a two-pick budget cannot buy the three goods the
        // wishes force (A needs B and C first).
        var tight = new BlitzShopPanorama(inventory, 2,
            [new BlitzPlanGood(0, inventory.BlitzEntries[0], [Row([]), Row([1, 2])]),
             new BlitzPlanGood(1, inventory.BlitzEntries[1], [Row([])]),
             new BlitzPlanGood(2, inventory.BlitzEntries[2], [Row([])])],
            [],
            [new BlitzStreamDelta(0, 0), new BlitzStreamDelta(0, 0), new BlitzStreamDelta(0, 0)]);
        var outOfPicks = BlitzShopPlanService.Solve(tight,
            [new BlitzShopPlanService.BlitzWish(0, [[1, 2]])]);
        Require(!outOfPicks.Solved, "the solver satisfied a wish beyond the pick budget");
        Require(outOfPicks.Reason == "OutOfPicks",
            $"a budget failure was reported as {outOfPicks.Reason}");

        var firstCard = ForecastItemDetails.Card(inventory.Player.Deck.Cards[0]);
        var secondCard = ForecastItemDetails.Card(inventory.Player.Deck.Cards.First(card =>
            card.Id != firstCard.Id));
        var chooseFirst = new BlitzRandomUpgradeChoice(1, 0, firstCard);
        var chooseSecond = new BlitzRandomUpgradeChoice(1, 0, secondCard);
        var conditional = new BlitzShopPlanService.BlitzWish(0, [[1]])
        {
            ChoicePaths = [new([1], [chooseFirst])]
        };
        var incompatible = new BlitzShopPlanService.BlitzWish(1, [[]])
        {
            ChoicePaths = [new([], [chooseSecond])]
        };
        Require(!BlitzShopPlanService.Solve(table, [conditional, incompatible]).Solved,
            "the solver accepted conflicting picks from the same reward screen");
        var compatible = BlitzShopPlanService.Solve(table, [conditional]);
        Require(compatible.Solved && compatible.Choices.Contains(chooseFirst),
            "the solver dropped the card selection required by its path");

        Smoke.SmokeReport.Add(
            $"blitz-plan-wish PASS: solved={solved.Steps.Count}steps filler=1 "
            + $"conflict={conflict.Reason} budget={outOfPicks.Reason}");
    }

    /// <summary>
    /// The window itself: the patches must be attached to the shop screen's
    /// lifecycle, both pages must render the panorama's answers, and folding
    /// must leave a visible title bar rather than an unfindable window.
    /// </summary>
    private static void VerifyPanel(
        PlanningPredictionService.State state,
        MerchantInventory inventory,
        BlitzShopPanorama panorama)
    {
        foreach (var method in new[]
                 {
                     AccessTools.Method(typeof(NMerchantInventory), nameof(NMerchantInventory.Open)),
                     AccessTools.Method(typeof(NMerchantInventory), nameof(NMerchantInventory.Close)),
                     AccessTools.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook),
                         nameof(MegaCrit.Sts2.Core.Hooks.Hook.AfterCardChangedPiles))
                 })
        {
            var patches = Harmony.GetPatchInfo(method);
            Require(patches is not null && patches.Postfixes.Any(patch =>
                    patch.PatchMethod.DeclaringType?.Name is "NMerchantInventoryOpenBlitzPlanPatch"
                        or "NMerchantInventoryCloseBlitzPlanPatch"
                        or "HookAfterCardChangedPilesBlitzPlanPatch"),
                $"the shop window patch is not attached to {method.Name}");
        }

        // The purchase audit: patches on the live purchase path, and its
        // multiset verdict logic — the tool that turns a "预测不准" report
        // into a [BlitzAudit] line in godot.log.
        foreach (var method in new[]
                 {
                     AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry),
                         nameof(MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry.OnTryPurchaseUnsynchronized)),
                     AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry),
                         nameof(MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry.InvokePurchaseCompleted))
                 })
        {
            var patches = Harmony.GetPatchInfo(method);
            Require(patches is not null && patches.Prefixes.Concat(patches.Postfixes).Any(patch =>
                    patch.PatchMethod.DeclaringType?.DeclaringType?.Name == "BlitzShopAudit"),
                $"the purchase audit patch is not attached to {method.Name}");
        }
        var (auditOk, auditDetail) = BlitzShopAudit.CompareMultiset(
            [("A", false), ("B", true)], [("B", true), ("A", false)]);
        Require(auditOk, "the audit flagged an identical multiset: " + auditDetail);
        var (auditCatch, auditCatchDetail) = BlitzShopAudit.CompareMultiset(
            [("A", false)], [("B", false)]);
        Require(!auditCatch && auditCatchDetail.Contains("B"),
            "the audit missed an unexpected card: " + auditCatchDetail);

        // The fingerprint must move when the deck moves: that is what makes
        // the window reprice deck-reading rows after a reward-screen pick.
        var before = BlitzShopPlanPanelControl.FingerprintOf(inventory);
        using (var isolation = ShadowIsolation.Enter(inventory.Player, automateRewards: true))
        {
            var extra = state.Run.CreateCard<Guilty>(inventory.Player);
            Require(MegaCrit.Sts2.Core.Commands.CardPileCmd.Add(extra, PileType.Deck)
                    .GetAwaiter().GetResult().success,
                "the fingerprint fixture could not add a card to the shadow deck");
        }
        Require(BlitzShopPlanPanelControl.FingerprintOf(inventory) != before,
            "the window fingerprint ignores deck changes, so reward picks stay stale");

        var panel = new BlitzShopPlanPanelControl();
        panel.Bind(inventory);
        panel.RenderForTest(panorama);
        // The window opens on the wish page now; the panorama is the second tab.
        panel.SwitchToPanoramaForTest();
        panel.RefreshContent();
        var panoramaLines = Labels(panel);
        Require(panoramaLines.Any(line => line.Contains("直接买") || line.Contains("buy outright")),
            "the panorama page did not render the outright answers");
        Require(panoramaLines.Any(line => line.Contains("①") || line.Contains("②")),
            "the panorama page did not render any good headings");
        if (panorama.Goods.Any(good => good.Outcomes.Any(outcome =>
                outcome.Yield.RandomUpgradeVariants.Count > 0)))
            Require(panoramaLines.Any(line => line.Contains("前置选项后自动升级")
                    || line.Contains("after prior deck choices")),
                "random-upgrade deck branches were computed but hidden by an identical base result");

        panel.SwitchToPlannerForTest();
        var plannerLines = Labels(panel);
        Require(plannerLines.Any(line => line.Contains("点击你想要的物品") || line.Contains("Click the item")),
            "the wish page did not explain itself with no wishes");
        if (panorama.Goods.Any(good => good.Entry.MerchantEntryType == BlitzMerchantEntryType.CardTransform))
            Require(plannerLines.Any(line => line.Contains("变化结果与路径") || line.Contains("transform results and paths")),
                "the transform good did not list its result paths");

        // Demanding an item chip must solve to a click plan that includes the
        // good and marks the wished step.
        var wishedSlot = panel.WishFirstChippedGoodForTest();
        Require(wishedSlot >= 0, "no good on the shelf owned a wishable item");
        var solvedLines = Labels(panel);
        Require(solvedLines.Any(line => line.Contains("次点击") || line.Contains("clicks")),
            "a solvable wish produced no click plan header");
        Require(solvedLines.Any(line => line.Contains("✓")),
            "the solved plan did not mark the wished step");
        panel.WishForTest(wishedSlot);

        var randomWishSlot = panel.WishFirstRandomUpgradeChipForTest();
        if (randomWishSlot >= 0)
        {
            var randomWishLines = Labels(panel);
            Require(randomWishLines.Any(line => line.Contains("已计算选牌路径")
                    || line.Contains("computed card-choice paths")),
                "a random-upgrade target did not list the card-choice paths that produce it");
            panel.ClearWishesForTest();
        }

        // Folding must leave a visible title bar, not an unfindable window. The
        // manager shows the panel; the fold only touches its own body.
        panel.Visible = true;
        var beforeFold = Labels(panel).Count;
        panel.ToggleExpansion();
        Require(panel.Visible && !panel.Expanded,
            "folding hid the whole window instead of leaving its title bar");
        Require(Labels(panel).Any(line => line.Contains("极速商店") || line.Contains("Blitz shop")),
            "the folded title bar lost its title");
        panel.ToggleExpansion();
        Require(panel.Visible && panel.Expanded && Labels(panel).Count == beforeFold,
            "unfolding did not restore the body");

        // The hover must answer from the panorama table (instant, no clone
        // purchases) whenever the shelf matches the panorama's fingerprint —
        // the per-hover probe is a full run round-trip and crashed the game
        // during a hover burst.
        var hoverSlot = panorama.Goods.First(good => good.Slot != 0 && good.Outcomes.Count > 0).Slot;
        Require(panel.TryDescribeRowFrom(inventory, hoverSlot, out var hoverText)
                && hoverText.Contains(OutcomeText(
                    panorama.GoodAt(hoverSlot)!.Direct!.Yield)),
            "the table hover lookup did not serve the priced row");

        var originalRng = inventory.Player.PlayerRng.ToSerializable();
        inventory.Player.PlayerRng.Shops.NextUnsignedLong();
        Require(!panel.TryDescribeRowFrom(inventory, hoverSlot, out _),
            "a changed RNG stream still served the old panorama");
        panel.RefreshContent();
        Require(!Labels(panel).Any(line => line.Contains("直接买：") || line.Contains("buy outright:")),
            "the stale panorama kept advertising purchase outcomes during recomputation");
        inventory.Player.PlayerRng.LoadFromSerializable(originalRng);
        panel.RefreshContent();

        // The item tip's rich text must carry the theme font override the
        // game's MegaRichTextLabel._Ready asserts — without it every hover
        // threw from the native bridge and the repeated throws took the
        // process down (the crash reported right after the window appeared).
        var sample = panorama.Goods
            .SelectMany(good => good.Outcomes)
            .SelectMany(outcome => outcome.Yield.CardScreens.SelectMany(screen => screen)
                .Concat(outcome.Yield.Potions).Concat(outcome.Yield.Relics)
                .Concat(outcome.Yield.GainedCards))
            .FirstOrDefault();
        if (sample is not null)
        {
            var tree = panel.GetTree();
            if (tree is not null)
            {
                tree.Root.AddChild(panel);
                try
                {
                    BlitzShopItemTip.Show(sample, panel);
                    var rich = FindRich(panel);
                    Require(rich is null
                            || rich.HasThemeFontOverride(ThemeConstants.RichTextLabel.NormalFont),
                        "the item tip's rich text lacks the theme font override the game asserts");
                    BlitzShopItemTip.Hide();
                }
                finally
                {
                    tree.Root.RemoveChild(panel);
                }
            }
        }

        // The manager reuses one panel instance between shops. A folded panel
        // must discard the previous shop's panorama when a new inventory is
        // bound, otherwise unfolding the next shop sees a stale table and
        // decides that no recompute is needed.
        var foldedPanel = new BlitzShopPlanPanelControl();
        foldedPanel.Bind(inventory);
        foldedPanel.RenderForTest(panorama);
        foldedPanel.Visible = true;
        foldedPanel.ToggleExpansion();
        var nextInventory = MerchantInventory.CreateForBlitzMerchant(state.Player);
        foldedPanel.Bind(nextInventory);
        Require(!foldedPanel.Expanded && foldedPanel.CurrentPanorama is null,
            "binding a new shop retained the folded panel's previous panorama");

        Smoke.SmokeReport.Add(
            $"blitz-plan-panel PASS: panorama_lines={panoramaLines.Count} planner_lines={plannerLines.Count} "
            + $"notes={panorama.Notes.Count}");
        foreach (var line in panoramaLines.Take(8))
            Smoke.SmokeReport.Add("blitz-plan-panel | " + line);
    }

    private static IReadOnlyList<string> Labels(BlitzShopPlanPanelControl panel)
    {
        var lines = new List<string>();
        Collect(panel, lines);
        return lines;

        static void Collect(Node node, List<string> into)
        {
            if (node is Label { Text.Length: > 0 } label) into.Add(label.Text);
            foreach (var child in node.GetChildren()) Collect(child, into);
        }
    }

    private static MegaRichTextLabel? FindRich(Node node)
    {
        if (node is MegaRichTextLabel rich) return rich;
        return node.GetChildren().Select(FindRich).FirstOrDefault(found => found is not null);
    }

    /// <summary>
    /// Buys the sequence on one clone (predecessors completed with the first
    /// candidate) and returns the last good's contents, the same convention
    /// the panorama rows use.
    /// </summary>
    private static BlitzYieldForecast? Buy(
        PlanningPredictionService planning,
        PlanningPredictionService.State state,
        IReadOnlyList<BlitzMerchantEntry> sequence)
    {
        var probe = planning.Restore(planning.Capture(state));
        for (var index = 0; index + 1 < sequence.Count; index++)
            if (!BlitzMerchantProbe.PredictInPlace(probe, sequence[index], deckPick: 0).Purchased)
                return null;
        var last = BlitzMerchantProbe.PredictInPlace(probe, sequence[^1]);
        return last.Resolved ? last : null;
    }

    private static string OutcomeText(BlitzYieldForecast yield) =>
        UI.BlitzShopPlanPanelControl.OutcomeText(yield, true);

    private static bool MatchesNative(BlitzYieldForecast forecast, BlitzYieldForecast native)
    {
        if (forecast.RandomUpgradeVariants.Count == 0)
            return OutcomeText(forecast) == OutcomeText(native);
        return forecast.RandomUpgradeVariants.Any(variant =>
            BlitzShopAudit.CompareMultiset(
                variant.UpgradedCards.Select(card => (card.Name, card.IsUpgraded)),
                native.UpgradedCards.Select(card => (card.Name, card.IsUpgraded))).Ok);
    }

    private static bool ContainsTwoMoreChoices(MerchantInventory inventory, IReadOnlyList<int> slots) =>
        slots.Any(slot => inventory.BlitzEntries[slot].MerchantEntryType == BlitzMerchantEntryType.TwoMoreChoices);

    private static PlanningPredictionService.State BlitzState(
        PlanningPredictionService planning,
        RunState live,
        Player player)
    {
        var snapshot = planning.Capture(planning.CreateState(live, player));
        snapshot.Run.GameMode = GameMode.Blitz;
        var state = planning.Restore(snapshot);
        Require(state.Run.GameMode == GameMode.Blitz, "the shadow restore dropped the Blitz game mode");
        return state;
    }

    private static string One(string text) => text.Length <= 90 ? text : text[..90];

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("blitz-shop-plan-regression: " + message);
    }
}
#endif
