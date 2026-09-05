#if DEBUG
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;
using SeedOracle.Forecasting;
using SeedOracle.UI;

namespace SeedOracle.Validation;

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
internal static class DebugForecastSmokeTest
{
    private const string EnvironmentVariable = "SEED_ORACLE_SELF_TEST";
    private static int _hasRun;

    [HarmonyPostfix]
    private static void Postfix(NMapScreen __instance, ActMap map)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariable),
                "1",
                StringComparison.Ordinal)
            || Interlocked.Exchange(ref _hasRun, 1) != 0)
        {
            return;
        }

        if (__instance._runState is not RunState run)
            throw new InvalidOperationException("Seed Oracle self-test could not obtain the live RunState.");

        var overflowBounds = new Godot.Rect2(-240f, 850f, 1440f, 360f);
        var clampOffset = MapHoverTipPresentation.CalculateViewportClampOffset(
            overflowBounds,
            new Godot.Vector2(1920f, 1080f));
        var clampedBounds = new Godot.Rect2(
            overflowBounds.Position + clampOffset,
            overflowBounds.Size);
        if (clampedBounds.Position.X < MapHoverTipPresentation.ViewportMargin
            || clampedBounds.Position.Y < MapHoverTipPresentation.ViewportMargin
            || clampedBounds.End.X > 1920f - MapHoverTipPresentation.ViewportMargin
            || clampedBounds.End.Y > 1080f - MapHoverTipPresentation.ViewportMargin)
        {
            throw new InvalidOperationException(
                $"Seed Oracle hover-tip viewport clamp failed: {clampedBounds}.");
        }

        ValidateForecastItemFormatting();

        var points = map.GetAllMapPoints()
            .Append(map.StartingMapPoint)
            .Append(map.BossMapPoint)
            .Concat(map.SecondBossMapPoint is null ? [] : [map.SecondBossMapPoint])
            .Distinct()
            .ToArray();

        var representativePoints = points
            .GroupBy(point => point.PointType)
            .Select(group => group.OrderBy(point => point.coord.row).First())
            .ToArray();
        var tooltipCount = 0;
        var routeVariantCount = 0;
        var routeMarkerCount = 0;
        var routeLineCount = 0;
        foreach (var point in representativePoints)
        {
            var forecast = PredictionPurityGuard.Execute(
                run,
                $"self-test:map:{point.PointType}:{point.coord}",
                () => Entry.MapForecasts.Predict(run, point, isTravelEnabled: false));
            routeVariantCount += forecast.RouteVariants.Count;
            var conflictingRoute = forecast.RouteVariants
                .GroupBy(variant => string.Join(
                    ">",
                    variant.Route.Select(choice =>
                        $"{choice.Point.coord.row},{choice.Point.coord.col},{choice.UsedFreeTravel}")))
                .FirstOrDefault(group => group
                    .Select(RouteMarkerPlanner.OutcomeKey)
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any());
            if (conflictingRoute is not null)
            {
                throw new InvalidOperationException(
                    "Seed Oracle produced two outcomes for the same complete coordinate route.");
            }
            if (MapForecastTooltipBuilder.Build(forecast) is not { } tooltip)
                continue;
            if (tooltip.Descriptions.Any(description =>
                    description.Contains("[yellow]", StringComparison.Ordinal)))
                throw new InvalidOperationException("Seed Oracle emitted an unsupported [yellow] rich-text tag.");
            routeMarkerCount += tooltip.RouteMarkers.Count;
            routeLineCount += tooltip.RouteLines.Count;
            tooltipCount++;
        }

        var markerSmokePoints = points
            .Where(point => __instance._mapPointDictionary.ContainsKey(point.coord))
            .Take(4)
            .ToArray();
        if (markerSmokePoints.Length == 4)
        {
            var syntheticVariants = new RouteVariantForecast[]
            {
                new(
                    [new RouteChoice(markerSmokePoints[0], 1, 1, true, false)],
                    RoomType.Monster,
                    null,
                    null,
                    null,
                    false),
                new(
                    [new RouteChoice(markerSmokePoints[1], 1, 2, true, false)],
                    RoomType.Event,
                    null,
                    null,
                    null,
                    false)
            };
            var syntheticForecast = new MapNodeForecast(
                markerSmokePoints[3],
                2,
                2,
                null,
                null,
                null,
                null,
                null,
                syntheticVariants,
                false);
            var syntheticTooltip = MapForecastTooltipBuilder.Build(syntheticForecast)
                                   ?? throw new InvalidOperationException(
                                       "Seed Oracle all-line route self-test produced no tooltip.");
            if (syntheticTooltip.RouteMarkers.Count != 0
                || syntheticTooltip.RouteLines.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Seed Oracle all-line planner produced {syntheticTooltip.RouteMarkers.Count} markers and "
                    + $"{syntheticTooltip.RouteLines.Count} lines, expected 0 and 2.");
            }

            var markerOwner = __instance._mapPointDictionary[markerSmokePoints[3].coord];
            RouteNodeMarkerOverlay.Show(
                markerOwner,
                syntheticTooltip.RouteMarkers,
                syntheticTooltip.RouteLines);
            var initialLineOverlays = __instance._points.GetChildren()
                .OfType<RoutePathLineOverlay>()
                .Where(overlay => !overlay.IsQueuedForDeletion())
                .Count();
            if (initialLineOverlays != 1)
            {
                throw new InvalidOperationException(
                    $"Seed Oracle all-line self-test attached {initialLineOverlays} overlays, expected 1.");
            }
            RouteNodeMarkerOverlay.ClearAll();

            var sharedRouteVariants = new RouteVariantForecast[]
            {
                new(
                    [
                        new RouteChoice(markerSmokePoints[0], 1, 1, true, false),
                        new RouteChoice(markerSmokePoints[2], 2, 1, true, false)
                    ],
                    RoomType.Monster,
                    null,
                    null,
                    null,
                    false),
                new(
                    [
                        new RouteChoice(markerSmokePoints[1], 1, 2, true, false),
                        new RouteChoice(markerSmokePoints[2], 2, 1, true, false)
                    ],
                    RoomType.Event,
                    null,
                    null,
                    null,
                    false)
            };
            var sharedRouteForecast = syntheticForecast with
            {
                RouteVariants = sharedRouteVariants
            };
            var sharedRouteTooltip = MapForecastTooltipBuilder.Build(sharedRouteForecast)
                                     ?? throw new InvalidOperationException(
                                         "Seed Oracle route-line self-test produced no tooltip.");
            if (sharedRouteTooltip.RouteMarkers.Count != 0
                || sharedRouteTooltip.RouteLines.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Seed Oracle route-line planner produced {sharedRouteTooltip.RouteMarkers.Count} markers and "
                    + $"{sharedRouteTooltip.RouteLines.Count} lines, expected 0 and 2.");
            }
            RouteNodeMarkerOverlay.Show(
                markerOwner,
                sharedRouteTooltip.RouteMarkers,
                sharedRouteTooltip.RouteLines);
            var attachedLineOverlays = __instance._points.GetChildren()
                .OfType<RoutePathLineOverlay>()
                .Where(overlay => !overlay.IsQueuedForDeletion())
                .ToArray();
            if (attachedLineOverlays.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Seed Oracle route-line self-test attached {attachedLineOverlays.Length} overlays, expected 1.");
            }
            if (attachedLineOverlays[0].BundleCount == 0
                || attachedLineOverlays[0].MaximumLaneCount != 2)
            {
                throw new InvalidOperationException(
                    $"Seed Oracle shared-edge self-test produced {attachedLineOverlays[0].BundleCount} bundles "
                    + $"with at most {attachedLineOverlays[0].MaximumLaneCount} lanes, expected a two-lane edge.");
            }
            RouteNodeMarkerOverlay.ClearAll();
        }

        if (RoutePathLineOverlay.OverlayZIndex != 60
            || RouteNodeMarker.OverlayZIndex != RoutePathLineOverlay.OverlayZIndex
            || RoutePathLineOverlay.OverlayZIndex >= MapHoverTipPresentation.HoverTipZIndex)
        {
            throw new InvalidOperationException(
                $"Seed Oracle overlay layers are invalid: route={RoutePathLineOverlay.OverlayZIndex}, "
                + $"marker={RouteNodeMarker.OverlayZIndex}, hover={MapHoverTipPresentation.HoverTipZIndex}.");
        }

        var midrunRouteBundleCount = 0;
        if (run.CurrentMapPoint is { } currentPoint)
        {
            var routedTarget = points
                .Where(point => point.coord.row >= currentPoint.coord.row + 2)
                .Where(point => __instance._mapPointDictionary.ContainsKey(point.coord))
                .Select(point => new
                {
                    Point = point,
                    Exploration = RouteStateExplorer.Explore(run, point)
                })
                .Where(candidate => candidate.Exploration.Paths.Count > 1)
                .OrderBy(candidate => candidate.Point.coord.row)
                .ThenBy(candidate => candidate.Point.coord.col)
                .FirstOrDefault();
            if (routedTarget is not null)
            {
                var routedForecast = PredictionPurityGuard.Execute(
                    run,
                    $"self-test:midrun-route:{routedTarget.Point.coord}",
                    () => Entry.MapForecasts.Predict(
                        run,
                        routedTarget.Point,
                        isTravelEnabled: false));
                var routedTooltip = MapForecastTooltipBuilder.Build(routedForecast);
                if (routedTooltip is { RouteLines.Count: > 0 })
                {
                    var routedOwner = __instance._mapPointDictionary[routedTarget.Point.coord];
                    RouteNodeMarkerOverlay.Show(
                        routedOwner,
                        routedTooltip.RouteMarkers,
                        routedTooltip.RouteLines);
                    var routedOverlay = __instance._points.GetChildren()
                        .OfType<RoutePathLineOverlay>()
                        .SingleOrDefault(overlay => !overlay.IsQueuedForDeletion());
                    if (routedOverlay is null
                        || routedOverlay.BundleCount == 0
                        || !routedOverlay.Touches(currentPoint.coord)
                        || !routedOverlay.Touches(routedTarget.Point.coord))
                    {
                        throw new InvalidOperationException(
                            "Seed Oracle mid-run route self-test did not connect the current node to the hovered target.");
                    }

                    midrunRouteBundleCount = routedOverlay.BundleCount;
                    RouteNodeMarkerOverlay.ClearAll();
                }
            }
        }

        var bossRouteBundleCount = 0;
        var bossPanelCount = 0;
        var bossPaths = RouteStateExplorer.Explore(run, map.BossMapPoint)
            .Paths
            .Take(2)
            .ToArray();
        if (bossPaths.Length > 0)
        {
            var bossVariants = Enumerable.Range(0, 2)
                .Select(index =>
                {
                    var path = bossPaths[index % bossPaths.Length];
                    var route = path.Steps
                        .Take(Math.Max(0, path.Steps.Count - 1))
                        .Select(step => new RouteChoice(
                            step.Point,
                            step.Point.coord.row + 1,
                            1,
                            true,
                            step.UsedFreeTravel))
                        .ToArray();
                    return new RouteVariantForecast(
                        route,
                        index == 0 ? RoomType.Monster : RoomType.Event,
                        null,
                        null,
                        null,
                        false);
                })
                .ToArray();
            var bossRouteForecast = new MapNodeForecast(
                map.BossMapPoint,
                bossPaths.Min(path => path.Steps.Count),
                bossPaths.Max(path => path.Steps.Count),
                null,
                null,
                null,
                null,
                null,
                bossVariants,
                false);
            var bossRouteTooltip = MapForecastTooltipBuilder.Build(bossRouteForecast)
                                   ?? throw new InvalidOperationException(
                                       "Seed Oracle boss-route self-test produced no tooltip.");
            if (bossRouteTooltip.Descriptions.Count > 3)
            {
                throw new InvalidOperationException(
                    $"Seed Oracle boss-route self-test produced {bossRouteTooltip.Descriptions.Count} panels, expected at most 3.");
            }

            var bossOwner = __instance._mapPointDictionary[map.BossMapPoint.coord];
            RouteNodeMarkerOverlay.Show(
                bossOwner,
                bossRouteTooltip.RouteMarkers,
                bossRouteTooltip.RouteLines);
            var bossOverlay = __instance._points.GetChildren()
                .OfType<RoutePathLineOverlay>()
                .SingleOrDefault(overlay => !overlay.IsQueuedForDeletion());
            if (bossOverlay is null
                || bossOverlay.BundleCount == 0
                || !bossOverlay.Touches(map.BossMapPoint.coord))
            {
                throw new InvalidOperationException(
                    "Seed Oracle boss-route self-test did not draw a route edge connected to the Boss node.");
            }
            if (bossOverlay.ZIndex >= MapHoverTipPresentation.HoverTipZIndex)
            {
                throw new InvalidOperationException(
                    $"Seed Oracle route overlay z-index {bossOverlay.ZIndex} must remain below hover tips at {MapHoverTipPresentation.HoverTipZIndex}.");
            }
            bossRouteBundleCount = bossOverlay.BundleCount;
            bossPanelCount = bossRouteTooltip.Descriptions.Count;
            RouteNodeMarkerOverlay.ClearAll();
        }

        var seedOverview = RunSeedOverviewPredictor.Predict(run);
        if (seedOverview.Count != run.Acts.Count
            || seedOverview.Any(act => string.IsNullOrWhiteSpace(act.Ancient)
                                       || string.IsNullOrWhiteSpace(act.Boss))
            || seedOverview.All(act => act.AncientOptions.Count == 0))
        {
            throw new InvalidOperationException(
                "Seed Oracle run overview did not produce every Ancient, Boss, and any Ancient options.");
        }
        var validatedAncientRuleCount =
            RunSeedOverviewPredictor.ValidateConditionalRuleImplementations(run);
        if (validatedAncientRuleCount != 6)
        {
            throw new InvalidOperationException(
                $"Seed Oracle validated {validatedAncientRuleCount} conditional Ancient rules, expected 6.");
        }

        var seedOverviewPanel = RunSeedOverviewPanel.Refresh(__instance);
        var topBar = NRun.Instance?.GlobalUi.TopBar
                     ?? throw new InvalidOperationException(
                         "Seed Oracle self-test could not locate the active run top bar.");
        var seedOverviewToggle = topBar.GetNodeOrNull<RunSeedOverviewToggleButton>(
            RunSeedOverviewToggleButton.NodeName);
        if (seedOverviewPanel.DisplayedActCount != seedOverview.Count
            || seedOverviewPanel.DisplayedOptionCount
            != seedOverview.Sum(act => act.AncientOptions.Count)
            || seedOverviewPanel.DisplayedReplacementCount
            != seedOverview.Sum(act => act.AncientOptionReplacements.Count)
            || seedOverviewToggle is null
            || !ReferenceEquals(seedOverviewToggle.GetParent(), topBar)
            || seedOverviewPanel.ZIndex <= RoutePathLineOverlay.OverlayZIndex
            || seedOverviewPanel.ZIndex >= seedOverviewToggle.ZIndex
            || seedOverviewToggle.ZIndex >= MapHoverTipPresentation.HoverTipZIndex)
        {
            throw new InvalidOperationException(
                $"Seed Oracle run overview panel failed: acts={seedOverviewPanel.DisplayedActCount}, "
                + $"options={seedOverviewPanel.DisplayedOptionCount}, "
                + $"replacements={seedOverviewPanel.DisplayedReplacementCount}, "
                + $"panel_z={seedOverviewPanel.ZIndex}, toggle_z={seedOverviewToggle?.ZIndex}.");
        }

        var wasExpanded = seedOverviewToggle.IsExpanded;
        seedOverviewToggle.EmitSignal(Godot.BaseButton.SignalName.Pressed);
        if (seedOverviewToggle.IsExpanded == wasExpanded
            || seedOverviewPanel.Visible != seedOverviewToggle.IsExpanded)
        {
            throw new InvalidOperationException(
                "Seed Oracle run overview toggle did not react to its Pressed signal.");
        }
        seedOverviewToggle.EmitSignal(Godot.BaseButton.SignalName.Pressed);
        if (seedOverviewToggle.IsExpanded != wasExpanded
            || seedOverviewPanel.Visible != wasExpanded)
        {
            throw new InvalidOperationException(
                "Seed Oracle run overview toggle did not restore its initial state.");
        }

        var preCombatTargets = PreCombatForecastPanelControl.CollectTargets(__instance);
        var preCombatPanel = PreCombatForecastPanel.Refresh(__instance);
        var preCombatToggle = topBar.GetNodeOrNull<PreCombatForecastToggleButton>(
            PreCombatForecastToggleButton.NodeName);
        if (preCombatToggle is null
            || !ReferenceEquals(preCombatToggle.GetParent(), topBar)
            || preCombatPanel.DisplayedTargetCount != preCombatTargets.Count
            || preCombatPanel.ManualCalculationButtonCount != preCombatTargets.Count
            || preCombatPanel.RouteBadgeCount != preCombatTargets.Count
            || preCombatPanel.SimulationSampleOptionCount != 11
            || preCombatPanel.SimulationTargetOptionCount != 6
            || !preCombatPanel.HasSimulationDisclaimer
            || preCombatPanel.WorkerRetentionOptionCount != 5
            || !preCombatPanel.HasExpectedWorkerRetentionOptions
            || !preCombatPanel.KeepsWorkerByDefault
            || !preCombatPanel.HasLocalizedSimulationEncounterLabels
            || !preCombatPanel.HasWorkerLifecycleControls
            || preCombatPanel.IsRunning
            || preCombatPanel.ZIndex <= RoutePathLineOverlay.OverlayZIndex
            || preCombatPanel.ZIndex >= preCombatToggle.ZIndex
            || preCombatToggle.ZIndex >= MapHoverTipPresentation.HoverTipZIndex
            || preCombatToggle.AnchorLeft <= seedOverviewToggle.AnchorLeft
            || preCombatPanel.SelectedSearchBudgetMilliseconds != 8_000)
        {
            throw new InvalidOperationException(
                $"Seed Oracle manual pre-combat panel failed: targets={preCombatPanel.DisplayedTargetCount}, "
                + $"expected_targets={preCombatTargets.Count}, running={preCombatPanel.IsRunning}, "
                + $"simulation_counts={preCombatPanel.SimulationSampleOptionCount}, "
                + $"simulation_targets={preCombatPanel.SimulationTargetOptionCount}, "
                + $"worker_policies={preCombatPanel.WorkerRetentionOptionCount}, "
                + $"localized_encounters={preCombatPanel.HasLocalizedSimulationEncounterLabels}, "
                + $"panel_z={preCombatPanel.ZIndex}, toggle_z={preCombatToggle?.ZIndex}.");
        }

        var preCombatWasExpanded = preCombatToggle.IsExpanded;
        preCombatToggle.EmitSignal(Godot.BaseButton.SignalName.Pressed);
        if (preCombatToggle.IsExpanded == preCombatWasExpanded
            || preCombatPanel.Visible != preCombatToggle.IsExpanded
            || preCombatPanel.IsRunning)
        {
            throw new InvalidOperationException(
                "Seed Oracle pre-combat toggle did not open or close the manual-only panel without starting work.");
        }
        preCombatToggle.EmitSignal(Godot.BaseButton.SignalName.Pressed);
        if (preCombatToggle.IsExpanded != preCombatWasExpanded
            || preCombatPanel.Visible != preCombatWasExpanded
            || preCombatPanel.IsRunning)
        {
            throw new InvalidOperationException(
                "Seed Oracle pre-combat toggle did not restore its initial idle state.");
        }

        var player = LocalContext.GetMe(run)
                     ?? throw new InvalidOperationException("Seed Oracle self-test could not obtain the local player.");
        var merchant = PredictionPurityGuard.Execute(
            run,
            "self-test:merchant",
            () => Entry.RandomForeseer.PredictInitialMerchant(player));
        if (!merchant.HasValue
            || merchant.Value!.CharacterCards.Count != 5
            || merchant.Value.ColorlessCards.Count != 2
            || merchant.Value.Relics.Count != 3
            || merchant.Value.Potions.Count != 3)
        {
            throw new InvalidOperationException(
                $"Seed Oracle merchant self-test failed: {merchant.Reason ?? "unexpected inventory shape"}.");
        }
        var merchantCards = merchant.Value.CharacterCards
            .Concat(merchant.Value.ColorlessCards)
            .Select(entry => entry.Item)
            .ToArray();
        if (merchantCards.Any(item =>
                item.Kind != ForecastItemKind.Card
                || string.IsNullOrWhiteSpace(item.ImagePath)
                || !Godot.ResourceLoader.Exists(item.ImagePath))
            || merchant.Value.Relics.Any(item => item.Item.Kind != ForecastItemKind.Relic)
            || merchant.Value.Potions.Any(item => item.Item.Kind != ForecastItemKind.Potion)
            || !merchant.Value.ColorlessCards.Any(item => item.Item.Rarity == ForecastItemRarity.Uncommon)
            || !merchant.Value.ColorlessCards.Any(item => item.Item.Rarity == ForecastItemRarity.Rare))
        {
            throw new InvalidOperationException(
                "Seed Oracle merchant items did not retain their type, rarity, and card-art metadata.");
        }

        var secondMerchant = PredictionPurityGuard.Execute(
            run,
            "self-test:merchant-second-visit",
            () => Entry.RandomForeseer.PredictMerchant(player, 2));
        if (!secondMerchant.HasValue
            || secondMerchant.Value!.FutureVisitOrdinal != 2
            || secondMerchant.Value.CharacterCards.Count != 5
            || secondMerchant.Value.ColorlessCards.Count != 2
            || secondMerchant.Value.Relics.Count != 3
            || secondMerchant.Value.Potions.Count != 3)
        {
            throw new InvalidOperationException(
                $"Seed Oracle second-merchant self-test failed: {secondMerchant.Reason ?? "unexpected inventory shape"}.");
        }

        var eventContent = PredictionPurityGuard.Execute(
            run,
            "self-test:event-random-contents",
            () => Entry.RandomForeseer.PredictEventContents(
                player,
                [RoomType.Monster, RoomType.Event],
                ModelDb.Event<RoomFullOfCheese>()));
        var eventRandomItemCount = eventContent is { HasValue: true }
            ? eventContent.Value!.Options
                .SelectMany(option => option.Sets)
                .Sum(set => set.Items.Count)
            : 0;
        if (eventRandomItemCount < 8)
        {
            throw new InvalidOperationException(
                $"Seed Oracle event-content self-test failed: {eventContent?.Reason ?? "no predicted cards"}.");
        }

        var eventTransformContent = PredictionPurityGuard.Execute(
            run,
            "self-test:event-local-rng",
            () => Entry.RandomForeseer.PredictEventContents(
                player,
                [RoomType.Event],
                ModelDb.Event<AromaOfChaos>()));
        var eventTransformItemCount = eventTransformContent is { HasValue: true }
            ? eventTransformContent.Value!.Options
                .SelectMany(option => option.Sets)
                .Sum(set => set.Items.Count)
            : 0;
        if (eventTransformItemCount == 0)
        {
            throw new InvalidOperationException(
                $"Seed Oracle event-local-RNG self-test failed: {eventTransformContent?.Reason ?? "no transform results"}.");
        }

        var eventPotionContent = PredictionPurityGuard.Execute(
            run,
            "self-test:event-detached-potion-metadata",
            () => Entry.RandomForeseer.PredictEventContents(
                player,
                [RoomType.Event],
                ModelDb.Event<TheLegendsWereTrue>()));
        var eventPotionItems = eventPotionContent is { HasValue: true }
            ? eventPotionContent.Value!.Options
                .SelectMany(option => option.Sets)
                .SelectMany(set => set.Items)
                .Count(item => item.Kind == ForecastItemKind.Potion
                               && item.Rarity != ForecastItemRarity.None)
            : 0;
        if (eventPotionItems == 0)
        {
            throw new InvalidOperationException(
                $"Seed Oracle detached event-item metadata self-test failed: "
                + $"{eventPotionContent?.Reason ?? "predicted potion lost its rarity"}.");
        }

        var slipperyBridgeContent = PredictionPurityGuard.Execute(
            run,
            "self-test:event-native-initial-content",
            () => Entry.RandomForeseer.PredictEventContents(
                player,
                [RoomType.Event],
                ModelDb.Event<SlipperyBridge>()));
        var initialBridgeOption = slipperyBridgeContent is { HasValue: true }
            ? slipperyBridgeContent.Value!.Options.FirstOrDefault(option =>
                option.TextKey == "SLIPPERY_BRIDGE.pages.INITIAL.options.OVERCOME")
            : null;
        if (initialBridgeOption is null || initialBridgeOption.InitialItems.Count != 1)
        {
            throw new InvalidOperationException(
                $"Seed Oracle native event-content self-test failed: "
                + $"{slipperyBridgeContent?.Reason ?? "initial no-damage removal card was omitted"}.");
        }

        var unknownPoint = points.FirstOrDefault(point => point.PointType == MapPointType.Unknown);
        if (unknownPoint is not null)
        {
            var unknown = PredictionPurityGuard.Execute(
                run,
                "self-test:unknown-room",
                () => UnknownMapPointPredictor.PredictNext(run, unknownPoint));
            if (!unknown.HasValue)
                throw new InvalidOperationException($"Seed Oracle unknown-room self-test failed: {unknown.Reason}.");
        }

        var combatPoint = points
            .Where(point => point.PointType is MapPointType.Monster or MapPointType.Elite or MapPointType.Boss)
            .Where(point => RouteStateExplorer.Explore(run, point).Paths.Count > 0)
            .OrderBy(point => point.coord.row)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Seed Oracle self-test found no reachable combat point.");

        var hpCount = PredictionPurityGuard.Execute(run, "self-test:monster-hp", () =>
        {
            var paths = RouteStateExplorer.Explore(run, combatPoint).Paths;
            var encounter = RouteWorldlinePredictor.Predict(run, combatPoint, paths)
                .Select(worldline => worldline.TargetEncounter)
                .FirstOrDefault(candidate => candidate is not null)
                ?? throw new InvalidOperationException("Encounter self-test found no route encounter.");
            var targetTotalFloor = run.TotalFloor - run.ActFloor + combatPoint.coord.row + 1;
            var generated = EncounterCompositionPredictor.Generate(run, targetTotalFloor, [encounter]);
            if (generated.Count == 0)
                throw new InvalidOperationException("Encounter composition self-test generated no encounters.");
            return MonsterHpPredictor.Predict(run, generated[0]).Count;
        });
        if (hpCount == 0)
            throw new InvalidOperationException("Monster HP self-test produced no monsters.");

        var combatRewardForecast = PredictionPurityGuard.Execute(
            run,
            "self-test:combat-rewards",
            () => Entry.MapForecasts.Predict(run, combatPoint, isTravelEnabled: true));
        var combatReward = combatRewardForecast.RouteVariants
            .Select(variant => variant.CombatRewards)
            .FirstOrDefault(candidate => candidate is { HasValue: true });
        if (combatReward is null
            || combatReward.Value!.CardRewards.Count == 0)
        {
            var reason = combatRewardForecast.RouteVariants
                .Select(variant => variant.CombatRewards?.Reason)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            throw new InvalidOperationException(
                $"Combat reward self-test failed: {reason ?? "no populated reward forecast"}.");
        }

        var treasurePoint = points
            .Where(point => point.PointType == MapPointType.Treasure)
            .Where(point => RouteStateExplorer.Explore(run, point).Paths.Count > 0)
            .OrderBy(point => point.coord.row)
            .FirstOrDefault();
        Forecast<TreasureRoomDetails>? treasureReward = null;
        if (treasurePoint is not null)
        {
            var treasureForecast = PredictionPurityGuard.Execute(
                run,
                "self-test:treasure-rewards",
                () => Entry.MapForecasts.Predict(run, treasurePoint, isTravelEnabled: true));
            treasureReward = treasureForecast.RouteVariants
                .Select(variant => variant.Treasure)
                .FirstOrDefault(candidate => candidate is { HasValue: true });
            if (treasureReward is null)
            {
                var reason = treasureForecast.RouteVariants
                    .Select(variant => variant.Treasure?.Reason)
                    .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
                throw new InvalidOperationException(
                    $"Treasure reward self-test failed: {reason ?? "no populated treasure forecast"}.");
            }
        }

        Entry.Logger.Info(
            $"[SelfTest] PASS map_types={representativePoints.Length} tooltips={tooltipCount} "
            + $"merchant_items={merchant.Value!.CharacterCards.Count + merchant.Value.ColorlessCards.Count + merchant.Value.Relics.Count + merchant.Value.Potions.Count} "
            + $"merchant_second_visit=passed route_variants={routeVariantCount} "
            + $"route_markers={routeMarkerCount} route_lines={routeLineCount} "
            + $"midrun_route_bundles={midrunRouteBundleCount} "
            + $"boss_route_bundles={bossRouteBundleCount} boss_panels={bossPanelCount} "
            + $"seed_overview_acts={seedOverview.Count} ancient_options={seedOverview.Sum(act => act.AncientOptions.Count)} "
            + $"ancient_replacements={seedOverview.Sum(act => act.AncientOptionReplacements.Count)} "
            + $"manual_precombat_targets={preCombatTargets.Count} "
            + $"ancient_rules={validatedAncientRuleCount} "
            + "tooltip_clamp=passed "
            + $"event_random_items={eventRandomItemCount} event_local_rng_items={eventTransformItemCount} "
            + $"event_potion_items={eventPotionItems} "
            + $"unknown={(unknownPoint is null ? "not_present" : "passed")} hp_monsters={hpCount} "
            + $"combat_reward_cards={combatReward.Value!.CardRewards.Count} "
            + $"treasure={(treasurePoint is null ? "not_present" : $"relics={treasureReward!.Value!.Relics.Count}")}");
    }

    private static void ValidateForecastItemFormatting()
    {
        var common = new ForecastItemDetails(
            ModelId.none,
            "Common",
            ForecastItemKind.Card,
            ForecastItemRarity.Common,
            "res://common.tres");
        var uncommon = common with { Name = "Uncommon", Rarity = ForecastItemRarity.Uncommon };
        var rare = common with
        {
            Name = "Rare+",
            Rarity = ForecastItemRarity.Rare,
            ImagePath = "res://rare.tres",
            IsUpgraded = true
        };

        if (ForecastItemFormatter.Format(common, useCardArtThumbnails: false)
                != "[color=#FFFFFF]Common[/color]"
            || ForecastItemFormatter.Format(uncommon, useCardArtThumbnails: false)
                != "[color=#64FFFF]Uncommon[/color]"
            || ForecastItemFormatter.Format(rare, useCardArtThumbnails: false)
                != "[color=#FFDA36]Rare+[/color]")
        {
            throw new InvalidOperationException("Seed Oracle rarity-color formatting self-test failed.");
        }

        var thumbnail = ForecastItemFormatter.Format(rare, useCardArtThumbnails: true);
        if (!thumbnail.Contains("[color=#FFDA36]◆[/color]", StringComparison.Ordinal)
            || !thumbnail.Contains("[img=72x54]res://rare.tres[/img]", StringComparison.Ordinal)
            || !thumbnail.EndsWith("[green]+[/green]", StringComparison.Ordinal)
            || thumbnail.Contains("Rare", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Seed Oracle card-art thumbnail formatting self-test failed.");
        }
    }
}
#endif
