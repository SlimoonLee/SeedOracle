#if DEBUG
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.Validation;

internal static class BlitzReplayRegressionTests
{
    internal static void RunSavedRun()
    {
        var path = Environment.GetEnvironmentVariable("SEED_ORACLE_BLITZ_REPLAY_SAVE");
        if (string.IsNullOrEmpty(path)) return;
        var save = JsonSerializer.Deserialize(File.ReadAllText(path),
            JsonSerializationUtility.GetTypeInfo<SerializableRun>())
            ?? throw new InvalidOperationException("Missing Blitz replay snapshot.");
        var run = PlanningPredictionService.RestoreRun(save);
        var player = run.Players[0];
        var planning = new PlanningPredictionService();
        using var isolation = ShadowIsolation.Enter(player, automateRewards: true);
        using var discovery = ShadowIsolation.GenerateMerchantInventory();
        var inventory = MerchantInventory.CreateForBlitzMerchant(player);
        using var registration = ShadowIsolation.UseMerchantInventories([inventory]);
        var state = planning.CreateState(run, player);
        var snapshot = planning.Capture(state);
        var preview = new BlitzShopPreview(planning, run, player);
        var liveRngBefore = JsonSerializer.Serialize(player.PlayerRng.ToSerializable());
        preview.MeasureDeltas(inventory);
        Smoke.SmokeReport.Add("blitz-saved-replay | shelf=" + string.Join(",", inventory.BlitzEntries
            .Select((entry, slot) => $"{slot}:{entry.MerchantEntryType}")));
        var failures = new List<string>();
        var checkedRows = 0;
        for (var slot = 0; slot < inventory.BlitzEntries.Count; slot++)
        {
            if (BlitzShopPlanService.NeedsDeckPick(inventory.BlitzEntries[slot])
                || inventory.BlitzEntries[slot].MerchantEntryType == BlitzMerchantEntryType.RandomUpgrades)
                continue;
            var others = Enumerable.Range(0, inventory.BlitzEntries.Count).Where(index => index != slot).ToArray();
            var prefixes = new List<int[]> { Array.Empty<int>() };
            prefixes.AddRange(others.Select(index => new[] { index }));
            prefixes.AddRange(others.SelectMany(first => others.Where(second => first < second)
                .Select(second => new[] { first, second })));
            foreach (var prefix in prefixes)
            {
                var probe = planning.Restore(snapshot);
                var completed = true;
                foreach (var before in prefix)
                    completed &= BlitzMerchantProbe.PredictInPlace(probe, inventory.BlitzEntries[before], 0).Purchased;
                if (!completed) continue;
                var expected = BlitzMerchantProbe.PredictInPlace(probe, inventory.BlitzEntries[slot]);
                var actual = preview.Preview(inventory, slot, prefix);
                var expectedText = UI.BlitzShopPlanPanelControl.OutcomeText(expected, true);
                var actualText = UI.BlitzShopPlanPanelControl.OutcomeText(actual, true);
                checkedRows++;
                if (actualText == expectedText) continue;
                var failure = $"slot={slot} prefix=[{string.Join(",", prefix)}] "
                    + $"preview='{actualText}' native='{expectedText}'";
                failures.Add(failure);
                Smoke.SmokeReport.Add("blitz-saved-replay DIFF: " + failure);
            }
        }
        Smoke.SmokeReport.Add($"blitz-saved-replay {(failures.Count == 0 ? "PASS" : "FAIL")}: "
            + $"checked={checkedRows} differences={failures.Count}");
        if (failures.Count > 0)
            throw new InvalidOperationException("Saved Blitz replay diverged. " + failures[0]);
        VerifyMixedRewardStreams(planning, state, inventory, preview);
        Require(JsonSerializer.Serialize(player.PlayerRng.ToSerializable()) == liveRngBefore,
            "forecast generation changed its source RNG streams");
    }

    internal static void VerifyMixedRewardStreams(
        PlanningPredictionService planning,
        PlanningPredictionService.State state,
        MerchantInventory? inventory = null,
        BlitzShopPreview? preview = null)
    {
        if (state.Run.CurrentMapPointHistoryEntry is null)
        {
            state = planning.Restore(planning.Capture(state));
            state.Run.AppendToMapPointHistory(MegaCrit.Sts2.Core.Map.MapPointType.Shop,
                MegaCrit.Sts2.Core.Rooms.RoomType.Shop, null);
        }
        using var scope = ShadowIsolation.Enter(state.Player, automateRewards: true);
        inventory ??= new MerchantInventory(state.Player, [], [], [], [], null,
        [
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.UpgradedCardReward),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.CardRewardOfEachRarity),
            BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.RandomUpgrades)
        ]);
        if (preview is null)
        {
            inventory.Setup();
            preview = new BlitzShopPreview(planning, state.Run, state.Player);
            preview.MeasureDeltas(inventory);
        }
        var slots = inventory.BlitzEntries.Select((entry, slot) => (entry.MerchantEntryType, Slot: slot)).ToArray();
        var first = slots.Single(item => item.MerchantEntryType == BlitzMerchantEntryType.UpgradedCardReward).Slot;
        var second = slots.Single(item => item.MerchantEntryType == BlitzMerchantEntryType.CardRewardOfEachRarity).Slot;
        var target = slots.Single(item => item.MerchantEntryType == BlitzMerchantEntryType.RandomUpgrades).Slot;
        var snapshot = planning.Capture(state);
        var verified = 0;
        var offeringPaths = 0;
        foreach (var prefix in new[] { new[] { first, second }, new[] { second, first } })
        {
            var forecast = preview.Preview(inventory, target, prefix);
            Require(!forecast.RandomUpgradePathsTruncated, "the four-screen replay was truncated");
            Require(forecast.RandomUpgradeVariants.Count == 256,
                $"four reward screens produced {forecast.RandomUpgradeVariants.Count} paths instead of 256");
            Require(forecast.UpgradedCards.Count == 0,
                "a conditional upgrade row also advertised an unconditional result");
            foreach (var variant in forecast.RandomUpgradeVariants)
            {
                var probe = planning.Restore(snapshot);
                foreach (var before in prefix)
                    PurchaseWithChoices(probe, inventory.BlitzEntries[before], before, variant.Choices);
                var actual = BlitzMerchantProbe.PredictInPlace(probe, inventory.BlitzEntries[target]);
                Require(actual.Purchased, "the random upgrade purchase failed during native replay");
                Require(Signature(actual.UpgradedCards) == Signature(variant.UpgradedCards),
                    $"upgrade path [{string.Join(",", prefix)}] promised {Signature(variant.UpgradedCards)} "
                    + $"but native replay gave {Signature(actual.UpgradedCards)}");
                verified++;
                if (variant.UpgradedCards.Any(card => card.Id?.Entry == "OFFERING")) offeringPaths++;
            }
        }
        Smoke.SmokeReport.Add($"blitz-mixed-stream-paths PASS: native-replayed={verified} "
            + $"offering-paths={offeringPaths} purchase-orders=2");
    }

    private static void PurchaseWithChoices(
        PlanningPredictionService.State state,
        BlitzMerchantEntry source,
        int sourceSlot,
        IReadOnlyList<BlitzRandomUpgradeChoice> choices)
    {
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        using var discovery = ShadowIsolation.GenerateMerchantInventory();
        var entry = (BlitzMerchantEntry)AccessTools.Method(typeof(BlitzMerchantProbe), "Replica")
            .Invoke(null, [source, state.Player])!;
        var inventory = new MerchantInventory(state.Player, [], [], [], [], null, [entry]);
        inventory.Setup();
        using var registration = ShadowIsolation.UseMerchantInventories([inventory]);
        using var rewards = ShadowIsolation.UseRewardHandler(async set =>
        {
            await set.GenerateWithoutOffering();
            var screen = 0;
            foreach (var reward in ShadowIsolation.EnumerateRewards(set.Rewards).OfType<CardReward>())
            {
                var screenIndex = screen++;
                var choice = choices.Single(candidate => candidate.SourceSlot == sourceSlot
                    && candidate.ScreenIndex == screenIndex);
                if (choice.Card is null)
                {
                    reward.OnSkipped();
                    continue;
                }
                var card = reward.Cards.FirstOrDefault(candidate =>
                {
                    var effective = Hook.ModifyCardBeingAddedToDeck(state.Run, state.Run.CloneCard(candidate), out _);
                    return effective.Id.Entry == choice.Card.Id?.Entry
                           && effective.IsUpgraded == choice.Card.IsUpgraded;
                });
                Require(card is not null,
                    $"path asks for unavailable {choice.Card.Name} at slot={sourceSlot} screen={screen}; "
                    + $"native offers {string.Join("/", reward.Cards.Select(option => option.Title))}");
                var session = new ShadowIsolation.CardRewardSession(reward);
                await session.ApplyAsync(new CardRewardSelection { card = card });
                await session.CompleteRewardAsync();
            }
        });
        using var purchase = ShadowIsolation.ExecuteMerchantPurchase();
        Require(entry.OnTryPurchaseUnsynchronized(inventory).GetAwaiter().GetResult(),
            $"native predecessor {sourceSlot} failed");
    }

    private static string Signature(IEnumerable<ForecastItemDetails> cards) =>
        string.Join("/", cards.Select(card => card.Id?.Entry + (card.IsUpgraded ? "+" : ""))
            .Order(StringComparer.Ordinal));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("blitz-replay-regression: " + message);
    }
}
#endif
