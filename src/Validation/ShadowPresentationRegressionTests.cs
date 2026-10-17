#if DEBUG
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using SeedOracle.Forecasting;
using SeedOracle.Integration;

namespace SeedOracle.Validation;

internal static class ShadowPresentationRegressionTests
{
    internal static bool Observing { get; private set; }
    internal static int UpgradeVfxCalls { get; private set; }

    internal static void RecordUpgradeVfx() { if (Observing) UpgradeVfxCalls++; }

    internal static void Run(RunState live)
    {
        var planning = new PlanningPredictionService();
        var player = LocalContext.GetMe(live) ?? live.Players[0];
        var fingerprint = LiveFingerprint.Capture(live);
        var input = planning.Capture(planning.CreateState(live, player));
        var outer = planning.Restore(input);
        var upgraded = 0;
        var misidentified = 0;
        var preview = NRun.Instance?.GlobalUi.CardPreviewContainer;
        var initialNodes = preview?.GetChildCount() ?? 0;
        var routeSmithBranches = 0;
        Observing = true;
        UpgradeVfxCalls = 0;
        try
        {
            // ExpandRest has an outer shadow frame, then restores a different
            // state for EACH target card. NetId matches the real local player.
            using (ShadowIsolation.Enter(outer.Player))
            foreach (var (card, index) in outer.Player.Deck.Cards.Select((card, index) => (card, index)))
            {
                if (!card.IsUpgradable || card.IsUpgraded) continue;
                var branch = planning.Restore(input);
                if (LocalContext.IsMine(branch.Player.Deck.Cards[index])) misidentified++;
                Require(planning.UpgradeCard(branch, card.Id, index) && branch.Player.Deck.Cards[index].IsUpgraded,
                    "shadow smith branch did not actually upgrade its chosen card");
                upgraded++;
            }

            // The actual route expansion, rather than just its helper, must
            // reach smith choices while leaving all live preview nodes alone.
            var routeState = planning.Restore(input);
            var route = new RouteWorldline(new RoutePath([
                new(new MapPoint(0,1) { PointType = MapPointType.RestSite }, false, false),
                new(new MapPoint(0,2) { PointType = MapPointType.Shop }, false, false)]), [],
                RoomType.Shop, null, null, 1, [RoomType.RestSite, RoomType.Shop], true);
            var merchant = new MerchantChoiceBranchService(512, 48, 30000).Predict(routeState.Player, route,
                planning.GenerateMerchant(planning.Restore(input)));
            routeSmithBranches = merchant.ReferenceChoicePaths.Concat(merchant.ChoiceBranches.SelectMany(branch => branch.ChoicePaths))
                .Count(path => path.Facts.Any(fact => fact.Value.StartsWith("smith:", StringComparison.Ordinal)));
            Require(routeSmithBranches > 0, "real rest-to-shop route never reached a smith choice");

            var delayed = planning.Restore(input);
            var delayedCard = delayed.Player.Deck.Cards.First(card => card.IsUpgradable && !card.IsUpgraded);
            Action callback;
            using (ShadowIsolation.Enter(delayed.Player))
                callback = () => CardCmd.Upgrade(delayedCard);
            callback();
            Require(delayedCard.IsUpgraded && !LocalContext.IsMe(delayed.Player) && !LocalContext.IsMine(delayedCard),
                "shadow identity or native upgrade failed after the execution scope ended");

            var rawCard = delayed.Run.CreateCard(delayedCard.CanonicalInstance, delayed.Player);
            var result = CardPileCmd.Add(rawCard, PileType.Deck).GetAwaiter().GetResult();
            Require(result.success && delayed.Player.Deck.Cards.Contains(rawCard), "native shadow card add failed");
            CardCmd.PreviewCardPileAdd(result);
            CardCmd.Preview(delayed.Player.Deck.Cards);

            using (ShadowIsolation.Enter(outer.Player))
            using (ShadowIsolation.Enter(delayed.Player))
            {
                Require(!LocalContext.IsMe(outer.Player) && !LocalContext.IsMine(outer.Player.Deck.Cards[0])
                    && LocalContext.IsMe(player), "nested isolation hid the wrong player's ownership");
                using (ShadowIsolation.EvaluateLocalMerchantPrice())
                {
                    Require(LocalContext.IsMe(delayed.Player) && !LocalContext.IsMine(delayedCard),
                        "price evaluation reopened ownership for shadow card animations");
                    CardCmd.Preview(delayed.Player.Deck.Cards);
                }
                Require(!LocalContext.IsMe(delayed.Player), "price evaluation leaked its temporary ownership");
            }
        }
        finally { Observing = false; }
        var addedNodes = (preview?.GetChildCount() ?? 0) - initialNodes;
        Smoke.SmokeReport.Add($"shadow-presentation OBSERVED smith_branches={upgraded} locally_owned_shadows={misidentified} "
            + $"upgrade_vfx={UpgradeVfxCalls} preview_nodes={addedNodes} native_ui={preview is not null}");
        Require(upgraded > 0 && misidentified == 0 && UpgradeVfxCalls == 0 && addedNodes == 0,
            "rest-site prediction leaked shadow ownership or upgrade animations into the live UI");
        Require(fingerprint.DescribeDifference(LiveFingerprint.Capture(live)) is null,
            "presentation regression changed live gameplay state");
        Require(LocalContext.IsMe(player), "real local player lost its ownership");
        Smoke.SmokeReport.Add($"shadow-presentation PASS: smith_targets={upgraded} route_smith_branches={routeSmithBranches} "
            + "after_scope_upgrade=applied raw_card_add=applied previews=0 nested=isolated price=restricted live=unchanged real_ownership=preserved");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Shadow presentation regression: " + message);
    }
}

[HarmonyPatch(typeof(NCardUpgradeVfx), nameof(NCardUpgradeVfx.Create))]
internal static class ShadowUpgradeVfxProbe
{
    [HarmonyPrefix]
    private static void Prefix() => ShadowPresentationRegressionTests.RecordUpgradeVfx();
}
#endif
