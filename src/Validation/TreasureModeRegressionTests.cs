#if DEBUG
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.Validation;

/// <summary>
/// v0.113.0 treasure-room modes: the SpoilsMap quest bonus and the Blitz chest
/// both change the chest without changing the relic bag contract, so they are
/// verified against the same native shadow the live forecast uses.
/// </summary>
internal static class TreasureModeRegressionTests
{
    internal static void Run(RunState live)
    {
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var before = LiveFingerprint.Capture(live);
        var planning = new PlanningPredictionService();
        var treasure = new MapPoint(0, 1) { PointType = MapPointType.Treasure };
        var unknown = new MapPoint(0, 1) { PointType = MapPointType.Unknown };

        // Standard chest: one rarity roll and one relic. This is the control the
        // other cases are measured against.
        var plain = planning.CreateState(live, player);
        var plainRolls = TreasureRolls(plain);
        var plainChest = planning.GenerateTreasure(plain, isPriorRoom: false, treasure);

        if (!plainChest.IsEmpty)
        {
            Require(TreasureRolls(plain) - plainRolls == 1,
                "a standard chest must roll the treasure rarity once");
            Require(plainChest.Relics.Count == 1,
                "a standard chest must offer exactly one relic");
            VerifySpoilsBonus(planning, live, player, treasure, unknown, plainChest.Gold);
            VerifyBlitzChest(planning, live, player, treasure);
            VerifyBlitzMerchant(planning, live, player);
        }
        else
        {
            Smoke.SmokeReport.Add(
                "treasure-mode SKIP: the live run's next chest is suppressed (Silver Crucible first chest)");
        }

        Require(before.DescribeDifference(LiveFingerprint.Capture(live)) is null,
            "treasure-mode fixtures changed the live run");
    }

    private static void VerifySpoilsBonus(
        PlanningPredictionService planning,
        RunState live,
        Player player,
        MapPoint treasure,
        MapPoint unknown,
        int plainGold)
    {
        // Quest chest: the map is in the deck, pinned to this act, and the point
        // is a real Treasure node. Native pays 42-52 gold and then the map's 600.
        var quest = planning.CreateState(live, player);
        AddSpoilsMap(quest, quest.Run.CurrentActIndex);
        var questChest = planning.GenerateTreasure(quest, isPriorRoom: false, treasure);
        Require(questChest.Gold - plainGold == 600,
            $"the SpoilsMap chest paid {questChest.Gold} instead of {plainGold} + 600");
        Require(!quest.Player.Deck.Cards.OfType<SpoilsMap>().Any(),
            "a completed SpoilsMap must leave the deck");

        // The same card must not pay twice: the chest paid, the card is gone.
        var secondChest = planning.GenerateTreasure(quest, isPriorRoom: false, treasure);
        Require(secondChest.Gold <= 52,
            $"a later chest re-paid the SpoilsMap bonus ({secondChest.Gold} gold)");

        // A chest rolled by an unknown point carries no quest mark, so the map
        // stays in the deck and pays nothing.
        var viaUnknown = planning.CreateState(live, player);
        AddSpoilsMap(viaUnknown, viaUnknown.Run.CurrentActIndex);
        var unknownChest = planning.GenerateTreasure(viaUnknown, isPriorRoom: false, unknown);
        Require(unknownChest.Gold <= 52,
            $"an unknown-point chest paid the SpoilsMap bonus ({unknownChest.Gold} gold)");
        Require(viaUnknown.Player.Deck.Cards.OfType<SpoilsMap>().Any(),
            "an unknown-point chest must keep the map in the deck");

        // A map pinned to another act never marks this act's chest.
        var wrongAct = planning.CreateState(live, player);
        AddSpoilsMap(wrongAct, wrongAct.Run.CurrentActIndex + 2);
        var wrongActChest = planning.GenerateTreasure(wrongAct, isPriorRoom: false, treasure);
        Require(wrongActChest.Gold <= 52,
            $"a map pinned to another act paid the bonus ({wrongActChest.Gold} gold)");
        Require(wrongAct.Player.Deck.Cards.OfType<SpoilsMap>().Any(),
            "a map pinned to another act must stay in the deck");

        Smoke.SmokeReport.Add(
            $"treasure-spoils PASS: base={plainGold} quest={questChest.Gold} "
            + $"repeat={secondChest.Gold} unknown_point={unknownChest.Gold} other_act={wrongActChest.Gold} "
            + "card_consumed=once");
    }

    private static void VerifyBlitzChest(
        PlanningPredictionService planning,
        RunState live,
        Player player,
        MapPoint treasure)
    {
        var blitz = BlitzState(planning, live, player);
        var rolls = TreasureRolls(blitz);
        var rewardRolls = blitz.Player.PlayerRng.Rewards.ToSerializable().counter;
        var chest = planning.GenerateTreasure(blitz, isPriorRoom: false, treasure);
        var rolled = TreasureRolls(blitz) - rolls;

        // Singleplayer Blitz adds one extra shared relic and one extra rarity
        // roll. The whole gold payout is skipped, which also means the Rewards
        // stream must not advance at all.
        Require(rolled == 2, $"a singleplayer Blitz chest rolled {rolled} rarities instead of 2");
        Require(chest.Relics.Count == 2,
            $"a singleplayer Blitz chest offered {chest.Relics.Count} relics instead of 2");
        Require(chest.Gold == 0, $"a Blitz chest paid {chest.Gold} gold instead of 0");
        Require(blitz.Player.PlayerRng.Rewards.ToSerializable().counter == rewardRolls,
            "a Blitz chest advanced the Rewards stream");
        Require(chest.Relics.Select(relic => relic.Id).Distinct().Count() == 2,
            "a singleplayer Blitz chest offered the same relic twice");

        Smoke.SmokeReport.Add(
            $"treasure-blitz PASS: relics={chest.Relics.Count} rarity_rolls={rolled} "
            + $"gold={chest.Gold} reward_rolls=0");
    }

    private static void VerifyBlitzMerchant(
        PlanningPredictionService planning,
        RunState live,
        Player player)
    {
        var blitz = BlitzState(planning, live, player);
        using var isolation = ShadowIsolation.Enter(blitz.Player);
        // The shelf is a function of Shops *before* generation, so the snapshot
        // has to be taken first: rolling once moves the stream and the next shop
        // is a different shop.
        var before = planning.Capture(blitz);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stock = planning.GenerateMerchant(blitz);
        var probeMs = watch.ElapsedMilliseconds;

        // Five weighted picks plus one cursed pick, all generated with the shop.
        Require(stock.BlitzEntries.Count == 6,
            $"a Blitz shop offered {stock.BlitzEntries.Count} goods instead of 6");
        Require(stock.BlitzChoices == 3,
            $"a Blitz shop allowed {stock.BlitzChoices} picks instead of 3");
        Require(stock.BlitzEntries.All(entry => entry.Title.Length > 0),
            "a Blitz good rendered without a title");
        Require(stock.BlitzEntries.All(entry => entry.IsAvailable),
            "a freshly generated Blitz shop already lost a good");
        Require(stock.BlitzEntries.Any(entry => entry.IsCursed),
            "the cursed Blitz pick is missing");
        Require(stock.CharacterCards.Count == 0 && stock.Relics.Count == 0 && stock.Potions.Count == 0,
            "a Blitz shop must not expose standard stock");

        // The same pre-generation stream must produce the same shelf twice, and
        // the tooltip must show it instead of the old "not modeled" placeholder.
        var firstRestore = planning.Restore(before);
        Require(firstRestore.Run.GameMode == GameMode.Blitz,
            "a Blitz snapshot lost its game mode when restored");
        var again = planning.GenerateMerchant(firstRestore);
        var third = planning.GenerateMerchant(planning.Restore(before));
        Require(ForecastContentKey.Merchant(stock) == ForecastContentKey.Merchant(again),
            "a Blitz shelf is not reproducible from the stream it was rolled on");
        Require(ForecastContentKey.Merchant(again) == ForecastContentKey.Merchant(third),
            "two restores of one snapshot disagree on the Blitz shelf");

        var lines = new List<string>();
        UI.MapForecastTooltipBuilder.AppendMerchantContents(lines, stock, true);
        Require(lines.Any(line => line.Contains("可带走")), "the Blitz tooltip lost the pick allowance");
        Require(lines.Any(line => line.Contains(stock.BlitzEntries[0].Title)),
            "the Blitz tooltip did not list the goods");
        Require(stock.BlitzEntries.All(entry => entry.Yield.Resolved),
            "a Blitz good could not be priced: "
            + string.Join(",", stock.BlitzEntries.Where(entry => !entry.Yield.Resolved).Select(entry => entry.Title)));
        foreach (var entry in stock.BlitzEntries)
        {
            var yield = entry.Yield;
            var said = yield.CardScreens.Count > 0 || yield.Relics.Count > 0 || yield.Potions.Count > 0
                       || yield.GainedCards.Count > 0 || yield.UpgradedCards.Count > 0
                       || yield.MaxHpDelta != 0 || yield.HealedHp != 0 || yield.ExtraChoices != 0
                       || yield.NeedsCardPick;
            Require(said, $"a Blitz good predicted nothing at all: {entry.Title}");
        }
        Require(lines.Any(line => line.Contains("→")), "the Blitz tooltip lost the yield lines");

        // A card-reward shelf must name the exact cards the game would show, and
        // a deck-selection good must say so instead of inventing a target.
        var screens = stock.BlitzEntries.SelectMany(entry => entry.Yield.CardScreens).ToArray();
        Require(screens.All(screen => screen.Count == 3),
            "a Blitz card reward offered a screen that is not three cards");
        Require(screens.SelectMany(screen => screen).All(card => card.Name.Length > 0),
            "a predicted card reward screen has an unnamed card");
        var picks = stock.BlitzEntries.Count(entry => entry.Yield.NeedsCardPick);
        Require(stock.BlitzEntries.All(entry => entry.Yield.NeedsCardPick || entry.Yield.PickableCards == 0),
            "a Blitz good reported eligible cards without asking for a pick");

        foreach (var line in lines)
            Smoke.SmokeReport.Add("blitz-tooltip | " + line);

        // The in-shop hover reads the shelf that is actually on screen, so the
        // probe must work from a live inventory rather than from a planned one.
        var hoverLines = HoverShopLines(planning, live, player);
        Require(hoverLines.Count == 6, $"the in-shop hover described {hoverLines.Count} goods instead of 6");
        Require(hoverLines.All(line => line.Contains("→")),
            "an in-shop hover line carried no prediction");
        foreach (var line in hoverLines)
            Smoke.SmokeReport.Add("blitz-hover | " + line.Replace("\n", " / "));

        // A transform good asks for a deck pick, but the outcome of every pickable
        // card is deterministic, so it must be listed rather than left unknown.
        VerifyBlitzTransform(planning, live, player);

        // Which good advances which stream decides whether the order matters.
        VerifyBlitzStreams(planning, live, player);
        VerifyBlitzOrder(planning, live, player);

        Smoke.SmokeReport.Add(
            $"blitz-yield PASS: screens={screens.Length} "
            + $"relics={stock.BlitzEntries.Sum(entry => entry.Yield.Relics.Count)} "
            + $"potions={stock.BlitzEntries.Sum(entry => entry.Yield.Potions.Count)} "
            + $"gained={stock.BlitzEntries.Sum(entry => entry.Yield.GainedCards.Count)} "
            + $"upgraded={stock.BlitzEntries.Sum(entry => entry.Yield.UpgradedCards.Count)} "
            + $"decks={picks} probe_ms={probeMs}");

        Smoke.SmokeReport.Add(
            $"blitz-merchant PASS: goods={stock.BlitzEntries.Count} picks={stock.BlitzChoices} "
            + $"cursed={stock.BlitzEntries.Count(entry => entry.IsCursed)} "
            + $"rare={stock.BlitzEntries.Count(entry => entry.IsRare)} lines={lines.Count}");
    }

    private static IReadOnlyList<string> HoverShopLines(
        PlanningPredictionService planning,
        RunState live,
        Player player)
    {
        var state = BlitzState(planning, live, player);
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        using var discovery = ShadowIsolation.GenerateMerchantInventory();
        var inventory = MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory
            .CreateForBlitzMerchant(state.Player);
        using var registration = ShadowIsolation.UseMerchantInventories([inventory]);

        // The prediction only reaches the player if both patches are really on
        // the game's methods; calling Describe directly would hide a missing one.
        var hoverGetter = AccessTools.PropertyGetter(
            typeof(BlitzMerchantEntry), nameof(BlitzMerchantEntry.HoverTips));
        var hoverPatches = Harmony.GetPatchInfo(hoverGetter);
        Require(hoverPatches is not null && hoverPatches.Postfixes.Any(
                patch => patch.PatchMethod.DeclaringType == typeof(UI.BlitzMerchantHoverTipPatch)),
            "the in-shop hover patch is not attached to BlitzMerchantEntry.HoverTips");
        var fillSlot = AccessTools.Method(typeof(NBlitzMerchantSlot), nameof(NBlitzMerchantSlot.FillSlot));
        var slotPatches = Harmony.GetPatchInfo(fillSlot);
        Require(slotPatches is not null && slotPatches.Postfixes.Any(
                patch => patch.PatchMethod.DeclaringType == typeof(UI.BlitzSlotBindingPatch)),
            "the in-shop slot binding patch is not attached to NBlitzMerchantSlot.FillSlot");

        // A probe copy must fall through untouched: the guard is what keeps the
        // game's own tooltip exactly as it was.
        var sample = inventory.BlitzEntries[0];
        var nativeText = sample.Description.GetFormattedText();
        var shown = sample.HoverTips.ToList();
        Require(shown.Count > 0 && shown[0] is HoverTip untouched && untouched.Description == nativeText,
            "hovering a shadow good rewrote the game's own tooltip");

        var lines = new List<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // The hover answers from the panorama table; register the window for
        // this fixture the way RefreshSafely does in-game.
        var panorama = BlitzShopPlanService.Compute(planning, state.Run, state.Player, inventory);
        var panel = new UI.BlitzShopPlanPanelControl();
        panel.Bind(inventory);
        panel.RenderForTest(panorama);
        UI.BlitzShopPlanPanel.UseForTest(panel);
        for (var index = 0; index < inventory.BlitzEntries.Count; index++)
        {
            var first = UI.BlitzShopYield.Describe(inventory, index);
            var second = UI.BlitzShopYield.Describe(inventory, index);
            Require(first == second, "the in-shop hover changed its answer without a purchase");
            Require(first.Length > 0,
                $"the in-shop hover said nothing about {inventory.BlitzEntries[index].MerchantEntryType}");
            lines.Add(first);

            // The prediction has to join the game's own tip, keeping its title
            // and its text rather than replacing them.
            var entry = inventory.BlitzEntries[index];
            var nativeTip = new HoverTip(entry.Title, entry.Description);
            var composed = UI.BlitzShopYield.Compose(new IHoverTip[] { nativeTip }, entry.Title, first);
            Require(composed.Count == 1, "the in-shop hover added a second tooltip box");
            var tip = (HoverTip)composed[0];
            Require(tip.Title == nativeTip.Title, "the in-shop hover replaced the game's title");
            Require(tip.Description.EndsWith(first, StringComparison.Ordinal),
                "the in-shop hover did not append its prediction");
            Require(tip.Description.Length > first.Length,
                "the in-shop hover dropped the game's own description");
        }
        UI.BlitzShopPlanPanel.ClearForTest(panel);
        // Every hover prices each "what if something else went first" branch
        // from the panorama table, so the cost has to stay inside a hover.
        var elapsed = watch.ElapsedMilliseconds;
        Require(elapsed < 6000, $"pricing the whole shelf took {elapsed} ms");
        Require(lines.Any(line => line.Contains("先买") || line.Contains("不会改变")),
            "the in-shop hover never said what the order does to it");
        Smoke.SmokeReport.Add(
            $"blitz-hover-ms PASS: goods={lines.Count} total_ms={elapsed} "
            + $"per_good_ms={elapsed / Math.Max(1, lines.Count)}");
        return lines;
    }

    private static void VerifyBlitzTransform(
        PlanningPredictionService planning,
        RunState live,
        Player player)
    {
        var state = BlitzState(planning, live, player);
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        var entry = BlitzMerchantEntry.Generate(state.Player, BlitzMerchantEntryType.CardTransform);

        // Declining the pick is what the shop hover does, and it still has to
        // answer for every card the game itself would offer.
        var declined = BlitzMerchantProbe.Predict(planning, state, entry);
        Require(declined.Resolved, "a Blitz transform good could not be priced");
        Require(declined.NeedsCardPick, "a Blitz transform good did not ask for a card");
        Require(declined.TransformChoices.Count == declined.PickableCards,
            $"a Blitz transform answered {declined.TransformChoices.Count} of "
            + $"{declined.PickableCards} pickable cards");
        Require(declined.TransformChoices.All(choice => choice.Result.Name.Length > 0),
            "a Blitz transform listed a result without a name");
        Require(declined.TransformChoices.All(choice => choice.Source.Name != choice.Result.Name),
            "a Blitz transform turned a card into itself");
        var distinct = declined.TransformChoices
            .Select(choice => choice.Result.Name).Distinct(StringComparer.Ordinal).Count();
        Require(declined.TransformChoices.Count <= 2 || distinct < declined.TransformChoices.Count,
            "a Blitz transform drew a fresh card for every candidate instead of one stream position");

        // Buying one of them has to land on exactly the card the table promised.
        var promised = declined.TransformChoices[0].Result.Name;
        var bought = BlitzMerchantProbe.Predict(planning, state, entry, deckPick: 0);
        var landed = bought.GainedCards.Select(card => card.Name).FirstOrDefault();
        Require(landed == promised,
            $"a Blitz transform promised {promised} for the first card but gave "
            + $"{landed ?? "nothing"}");

        // The last candidate too: a source that sits before the drawn slot is
        // the case where the table could be off by one.
        var last = declined.TransformChoices.Count - 1;
        var promisedLast = declined.TransformChoices[last].Result.Name;
        var boughtLast = BlitzMerchantProbe.Predict(planning, state, entry, deckPick: last);
        var landedLast = boughtLast.GainedCards.Select(card => card.Name).FirstOrDefault();
        Require(landedLast == promisedLast,
            $"a Blitz transform promised {promisedLast} for the last card but gave "
            + $"{landedLast ?? "nothing"}");

        var lines = UI.BlitzYieldText.Lines(declined, true);
        Require(lines.Any(line => line.Contains("变化为")),
            "the Blitz transform tooltip did not name any outcome");
        Require(lines.All(line => !line.Contains("结果随选择改变")),
            "the Blitz transform tooltip still says the result is unknown");

        Smoke.SmokeReport.Add(
            $"blitz-transform PASS: cards={declined.TransformChoices.Count} "
            + $"results={distinct} picked={promised} lines={lines.Count}");
        foreach (var line in lines)
            Smoke.SmokeReport.Add("blitz-transform | " + line);
    }

    /// <summary>
    /// Every good sells an action, so what the next one gives depends on which
    /// streams the earlier purchases moved. This measures that instead of
    /// assuming it: one shadow purchase per entry type, counters either side.
    /// </summary>
    private static void VerifyBlitzStreams(
        PlanningPredictionService planning,
        RunState live,
        Player player)
    {
        var baseState = BlitzState(planning, live, player);
        var readers = new List<string>();
        var writers = new List<string>();
        foreach (var type in Enum.GetValues<BlitzMerchantEntryType>())
        {
            var probe = planning.Restore(planning.Capture(baseState));
            using var isolation = ShadowIsolation.Enter(probe.Player, automateRewards: true);
            var entry = BlitzMerchantEntry.Generate(probe.Player, type);
            var before = StreamCounters.Capture(probe);
            var yield = BlitzMerchantProbe.PredictInPlace(probe, entry);
            var after = StreamCounters.Capture(probe);
            var delta = after.Minus(before);
            if (!yield.Resolved)
            {
                Smoke.SmokeReport.Add($"blitz-stream | {type} unresolved");
                continue;
            }
            var moved = delta.Describe();
            Smoke.SmokeReport.Add(
                $"blitz-stream | {type} {moved} pick={(yield.NeedsCardPick ? yield.PickableCards : 0)}");
            if (delta.MovesShops || delta.MovesTransformations) writers.Add(type.ToString());
            if (type is BlitzMerchantEntryType.CardRewards or BlitzMerchantEntryType.UpgradedCardReward
                or BlitzMerchantEntryType.RareCardReward or BlitzMerchantEntryType.UpgradedRareCard
                or BlitzMerchantEntryType.ColorlessCardReward or BlitzMerchantEntryType.PrismaticCardReward
                or BlitzMerchantEntryType.EnchantedCardReward or BlitzMerchantEntryType.CardRewardOfEachRarity
                or BlitzMerchantEntryType.Potions or BlitzMerchantEntryType.CardTransform)
                readers.Add(type.ToString());
        }

        Require(writers.Count > 0, "no Blitz good moved a stream, so the order could not matter");
        Smoke.SmokeReport.Add(
            $"blitz-streams PASS: writers={writers.Count} readers={readers.Count} "
            + $"types={Enum.GetValues<BlitzMerchantEntryType>().Length}");
    }

    /// <summary>
    /// Proves the order really changes the goods, and that a good which reads no
    /// stream keeps its answer no matter what was taken first.
    /// </summary>
    private static void VerifyBlitzOrder(
        PlanningPredictionService planning,
        RunState live,
        Player player)
    {
        var state = BlitzState(planning, live, player);
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);

        BlitzMerchantEntry Make(BlitzMerchantEntryType type) =>
            BlitzMerchantEntry.Generate(state.Player, type);

        var cards = Make(BlitzMerchantEntryType.CardRewards);
        var potions = Make(BlitzMerchantEntryType.Potions);
        var more = Make(BlitzMerchantEntryType.TwoMoreChoices);
        var heal = Make(BlitzMerchantEntryType.FullHealAndMaxHp);

        var drink = UI.BlitzYieldText.Render(BlitzMerchantProbe.Predict(planning, state, potions), true);
        var drinkAfterCards = UI.BlitzYieldText.Render(
            BlitzMerchantProbe.PredictAfter(
                planning.Restore(planning.Capture(state)), cards, potions), true);
        var drinkAfterHeal = UI.BlitzYieldText.Render(
            BlitzMerchantProbe.PredictAfter(
                planning.Restore(planning.Capture(state)), heal, potions), true);
        Require(drinkAfterCards != drink || drinkAfterHeal != drink,
            "nothing taken first changed the potions, so the order could not matter");

        // Even the goods that look static roll something: the "two more picks"
        // good also hands over a curse, and that curse comes out of Shops.
        var moreAlone = UI.BlitzYieldText.Render(BlitzMerchantProbe.Predict(planning, state, more), true);
        var moreAfterCards = UI.BlitzYieldText.Render(
            BlitzMerchantProbe.PredictAfter(
                planning.Restore(planning.Capture(state)), cards, more), true);
        Require(moreAlone.Contains("可带走"), "the two-more-picks good lost its pick allowance");
        Require(moreAfterCards != moreAlone,
            $"a static-looking good kept its curse after a card reward: {One(moreAlone)}");

        // The heal good looks fixed but rolls its curse from Shops, which is
        // exactly the kind of "static" entry an order-blind forecast gets wrong.
        var healAlone = UI.BlitzYieldText.Render(BlitzMerchantProbe.Predict(planning, state, heal), true);
        var healAfterCards = UI.BlitzYieldText.Render(
            BlitzMerchantProbe.PredictAfter(
                planning.Restore(planning.Capture(state)), cards, heal), true);
        Require(healAfterCards != healAlone,
            "the heal good's curse did not move after a card reward");

        Smoke.SmokeReport.Add(
            $"blitz-order PASS: potions='{One(drink)}' afterCards='{One(drinkAfterCards)}' "
            + $"afterHeal='{One(drinkAfterHeal)}' picksAlone='{One(moreAlone)}' "
            + $"picksAfterCards='{One(moreAfterCards)}' "
            + $"curseAlone='{One(healAlone)}' curseAfterCards='{One(healAfterCards)}'");
    }

    private static string One(string text) => text.Replace("\n", " / ");

    /// <summary>
    /// A digest of everything a purchase can move: the four player streams, the
    /// shared relic bag and the odds state.
    /// </summary>
    private readonly record struct StreamCounters(
        int Shops, int Rewards, int Transformations, int NonCombat, string Bag, string Odds)
    {
        internal static StreamCounters Capture(PlanningPredictionService.State state)
        {
            var rng = state.Player.PlayerRng;
            return new StreamCounters(
                rng.Shops.ToSerializable().counter,
                rng.Rewards.ToSerializable().counter,
                rng.Transformations.ToSerializable().counter,
                rng.NonCombatGeneration.ToSerializable().counter,
                Digest(state.Run.SharedRelicGrabBag.ToSerializable()),
                Digest(state.Run.Odds.ToSerializable()));
        }

        private static string Digest(object value)
        {
            try { return System.Text.Json.JsonSerializer.Serialize(value); }
            catch (Exception) { return "?"; }
        }

        internal bool MovesShops => Shops != 0 || Rewards != 0;

        internal bool MovesTransformations => Transformations != 0;

        internal StreamCounters Minus(StreamCounters before) => new(
            Shops - before.Shops,
            Rewards - before.Rewards,
            Transformations - before.Transformations,
            NonCombat - before.NonCombat,
            Bag == before.Bag ? "same" : "moved",
            Odds == before.Odds ? "same" : "moved");

        internal string Describe() =>
            $"shops={Shops:+0;-0;0} rewards={Rewards:+0;-0;0} "
            + $"transform={Transformations:+0;-0;0} noncombat={NonCombat:+0;-0;0} "
            + $"bag={(Bag == "same" ? "same" : "moved")} odds={(Odds == "same" ? "same" : "moved")}";
    }

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

    private static void AddSpoilsMap(PlanningPredictionService.State state, int actIndex)
    {
        using var isolation = ShadowIsolation.Enter(state.Player, automateRewards: true);
        var card = state.Run.CreateCard<SpoilsMap>(state.Player);
        // AfterCreated pins the map to act index 1; re-pin it so the fixture can
        // exercise the current act of whatever run the self-test landed on.
        card.SpoilsActIndex = actIndex;
        Require(CardPileCmd.Add(card, PileType.Deck).GetAwaiter().GetResult().success,
            "the SpoilsMap fixture could not enter the shadow deck");
    }

    private static int TreasureRolls(PlanningPredictionService.State state) =>
        state.Run.Rng.TreasureRoomRelics.ToSerializable().counter;

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("treasure-mode-regression: " + message);
    }
}
#endif
