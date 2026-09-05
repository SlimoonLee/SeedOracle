using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SeedOracle.Api;
using SeedOracle.Integration;

namespace SeedOracle.Forecasting;

internal sealed class MapForecastService(IRandomForeseerAdapter randomForeseer)
{
    public MapNodeForecast Predict(RunState run, MapPoint point, bool isTravelEnabled)
    {
        var exploration = RouteStateExplorer.Explore(run, point);
        var minimumSteps = exploration.Paths.Count == 0
            ? 0
            : exploration.Paths.Min(path => path.Length);
        var maximumSteps = exploration.Paths.Count == 0
            ? 0
            : exploration.Paths.Max(path => path.Length);
        var immediate = exploration.Paths.Count > 0
                        && exploration.Paths.All(path => path.Length == 1);

        if (exploration.Paths.Count == 0)
        {
            return new MapNodeForecast(
                point,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                [],
                exploration.WasTruncated);
        }

        var worldlines = RouteWorldlinePredictor.Predict(run, point, exploration.Paths);
        var targetTotalFloor = run.TotalFloor - run.ActFloor + point.coord.row + 1;
        var generatedEncounters = worldlines
            .Where(worldline => worldline.TargetEncounter is not null)
            .Select(worldline => worldline.TargetEncounter!)
            .DistinctBy(encounter => encounter.Id)
            .ToDictionary(
                encounter => encounter.Id,
                encounter => EncounterCompositionPredictor.Generate(run, targetTotalFloor, [encounter]).Single());

        var player = LocalContext.GetMe(run) ?? run.Players.FirstOrDefault();
        var merchantForecasts = new Dictionary<int, Forecast<MerchantInventoryForecast>>();
        foreach (var ordinal in worldlines
                     .Where(worldline => worldline.TargetRoomType == RoomType.Shop)
                     .Select(worldline => worldline.FutureMerchantVisitOrdinal)
                     .Distinct())
        {
            merchantForecasts[ordinal] = player is null
                ? Forecast<MerchantInventoryForecast>.Unsupported(
                    "No local player is available for merchant prediction.",
                    PredictionDependency.PlayerState)
                : randomForeseer.PredictMerchant(player, ordinal);
        }

        var combatRewardForecasts = new Dictionary<string, Forecast<CombatRewardDetails>>(StringComparer.Ordinal);
        var treasureForecasts = new Dictionary<string, Forecast<TreasureRoomDetails>>(StringComparer.Ordinal);
        var eventContentForecasts = new Dictionary<string, Forecast<EventContentDetails>?>(StringComparer.Ordinal);
        foreach (var worldline in worldlines)
        {
            if (worldline.TargetEncounter is { } targetEncounter)
            {
                var key = RewardRouteKey(worldline, targetEncounter.Id);
                if (!combatRewardForecasts.ContainsKey(key))
                {
                    var prediction = player is null
                        ? Forecast<CombatRewardDetails>.Unsupported(
                            "No local player is available for combat-reward prediction.",
                            PredictionDependency.PlayerState)
                        : randomForeseer.PredictCombatRewards(
                            player,
                            worldline.ResolvedRooms,
                            targetEncounter);
                    combatRewardForecasts[key] = DowngradeForUnavailableTravel(
                        prediction,
                        isTravelEnabled);
                }
            }

            if (worldline.TargetRoomType == RoomType.Treasure)
            {
                var key = RewardRouteKey(worldline);
                if (!treasureForecasts.ContainsKey(key))
                {
                    var prediction = player is null
                        ? Forecast<TreasureRoomDetails>.Unsupported(
                            "No local player is available for treasure-room prediction.",
                            PredictionDependency.PlayerState)
                        : randomForeseer.PredictTreasureRoom(player, worldline.ResolvedRooms);
                    treasureForecasts[key] = DowngradeForUnavailableTravel(
                        prediction,
                        isTravelEnabled);
                }
            }

            if (worldline.TargetEvent is { } targetEvent)
            {
                var key = RewardRouteKey(worldline, targetEvent.Id);
                if (!eventContentForecasts.ContainsKey(key))
                {
                    var prediction = player is null
                        ? Forecast<EventContentDetails>.Unsupported(
                            "No local player is available for event-content prediction.",
                            PredictionDependency.PlayerState | PredictionDependency.EventState)
                        : randomForeseer.PredictEventContents(
                            player,
                            worldline.ResolvedRooms,
                            targetEvent);
                    eventContentForecasts[key] = prediction is null
                        ? null
                        : DowngradeForUnavailableTravel(prediction, isTravelEnabled);
                }
            }
        }

        var routeVariants = worldlines.Select(worldline =>
        {
            var eventDetails = worldline.TargetEvent is null
                ? null
                : new EventDetails(
                    worldline.TargetEvent.Id,
                    worldline.TargetEvent.Title.GetFormattedText());
            var encounterDetails = worldline.TargetEncounter is null
                ? null
                : generatedEncounters[worldline.TargetEncounter.Id].Details;
            var merchant = worldline.TargetRoomType == RoomType.Shop
                ? merchantForecasts[worldline.FutureMerchantVisitOrdinal]
                : null;
            var combatRewards = worldline.TargetEncounter is { } rewardEncounter
                ? combatRewardForecasts[RewardRouteKey(worldline, rewardEncounter.Id)]
                : null;
            var treasure = worldline.TargetRoomType == RoomType.Treasure
                ? treasureForecasts[RewardRouteKey(worldline)]
                : null;
            var eventContents = worldline.TargetEvent is { } contentEvent
                ? eventContentForecasts[RewardRouteKey(worldline, contentEvent.Id)]
                : null;
            return new RouteVariantForecast(
                worldline.Route,
                worldline.TargetRoomType,
                eventDetails,
                encounterDetails,
                merchant,
                worldline.HasUnmodeledStateDependency || !isTravelEnabled,
                combatRewards,
                treasure,
                eventContents);
        }).ToArray();

        var unknownRoom = BuildUnknownForecast(point, routeVariants, immediate);
        var events = BuildEventForecast(routeVariants, immediate);
        var encounter = BuildEncounterForecast(point, worldlines, routeVariants);
        var monsterHp = BuildMonsterHpForecast(
            run,
            routeVariants,
            generatedEncounters,
            immediate,
            isTravelEnabled);
        var merchant = BuildMerchantForecast(point, routeVariants, immediate, isTravelEnabled);

        return new MapNodeForecast(
            point,
            minimumSteps,
            maximumSteps,
            unknownRoom,
            events,
            encounter,
            monsterHp,
            merchant,
            routeVariants,
            exploration.WasTruncated);
    }

    private static string RewardRouteKey(RouteWorldline worldline, ModelId? encounterId = null) =>
        string.Join(
            ",",
            worldline.ResolvedRooms.Select(roomType => ((int)roomType).ToString()))
        + "|"
        + (encounterId?.ToString() ?? string.Empty);

    private static Forecast<T> DowngradeForUnavailableTravel<T>(
        Forecast<T> forecast,
        bool isTravelEnabled)
    {
        if (isTravelEnabled
            || !forecast.HasValue
            || forecast.Accuracy == ForecastAccuracy.BranchDependent)
        {
            return forecast;
        }

        return Forecast<T>.Branch(
            forecast.Value!,
            forecast.Dependencies,
            "The map is being viewed before travel is available; intervening state remains conditional.");
    }

    private static Forecast<RoomType>? BuildUnknownForecast(
        MapPoint point,
        IReadOnlyList<RouteVariantForecast> variants,
        bool immediate)
    {
        if (point.PointType != MapPointType.Unknown)
            return null;

        const PredictionDependency dependencies =
            PredictionDependency.UnknownMapPoint | PredictionDependency.EventState;
        var roomTypes = variants
            .Select(variant => variant.RoomType)
            .Distinct()
            .ToArray();
        if (roomTypes.Length == 1)
        {
            return Forecast<RoomType>.CurrentWorldline(
                roomTypes[0],
                dependencies,
                immediate
                    ? "Uses the next cloned unknown-room roll."
                    : "All currently feasible routes resolve this node to the same room type.");
        }

        return Forecast<RoomType>.BranchWithoutValue(
            dependencies,
            "The cloned unknown-room RNG and odds are advanced once for each earlier question-mark node on the selected route.");
    }

    private static Forecast<IReadOnlyList<EventDetails>>? BuildEventForecast(
        IReadOnlyList<RouteVariantForecast> variants,
        bool immediate)
    {
        var eventDetails = variants
            .Where(variant => variant.Event is not null)
            .Select(variant => variant.Event!)
            .DistinctBy(details => details.Id)
            .OrderBy(details => details.Id.Entry, StringComparer.Ordinal)
            .ToArray();
        if (eventDetails.Length == 0)
            return null;

        const PredictionDependency dependencies =
            PredictionDependency.EventState | PredictionDependency.PlayerState;
        var allRoutesAreThisEvent = variants.All(variant => variant.Event?.Id == eventDetails[0].Id);
        if (eventDetails.Length == 1 && allRoutesAreThisEvent)
        {
            return Forecast<IReadOnlyList<EventDetails>>.CurrentWorldline(
                eventDetails,
                dependencies,
                immediate
                    ? "Uses the current generated event queue and hook snapshot."
                    : "All currently feasible routes select the same event while the relevant player state remains unchanged.");
        }

        return Forecast<IReadOnlyList<EventDetails>>.Branch(
            eventDetails,
            dependencies,
            "The selected route changes how many earlier events are consumed.");
    }

    private static Forecast<IReadOnlyList<EncounterDetails>>? BuildEncounterForecast(
        MapPoint point,
        IReadOnlyList<RouteWorldline> worldlines,
        IReadOnlyList<RouteVariantForecast> variants)
    {
        var encounters = variants
            .Where(variant => variant.Encounter is not null)
            .Select(variant => variant.Encounter!)
            .DistinctBy(details => details.Id)
            .OrderBy(details => details.Id.Entry, StringComparer.Ordinal)
            .ToArray();
        if (encounters.Length == 0)
            return null;

        var dependencies = PredictionDependency.EncounterQueue
                           | PredictionDependency.RunSeed
                           | PredictionDependency.Floor;
        if (worldlines.Any(worldline => worldline.Path.Steps
                .Take(worldline.Path.Steps.Count - 1)
                .Any(step => step.Point.PointType == MapPointType.Unknown)))
        {
            dependencies |= PredictionDependency.UnknownMapPoint;
        }

        var allRoutesAreCombat = variants.All(variant => variant.Encounter is not null);
        if (encounters.Length == 1 && allRoutesAreCombat)
        {
            return point.PointType == MapPointType.Unknown
                ? Forecast<IReadOnlyList<EncounterDetails>>.CurrentWorldline(
                    encounters,
                    dependencies,
                    "All currently feasible route simulations resolve to this encounter.")
                : Forecast<IReadOnlyList<EncounterDetails>>.Exact(encounters, dependencies);
        }

        return Forecast<IReadOnlyList<EncounterDetails>>.Branch(
            encounters,
            dependencies,
            "Different feasible routes consume different encounter-queue entries or resolve the question mark differently.");
    }

    private static Forecast<IReadOnlyList<MonsterHpDetails>>? BuildMonsterHpForecast(
        RunState run,
        IReadOnlyList<RouteVariantForecast> variants,
        IReadOnlyDictionary<ModelId, GeneratedEncounter> generatedEncounters,
        bool immediate,
        bool isTravelEnabled)
    {
        var combatVariants = variants
            .Where(variant => variant.Encounter is not null)
            .ToArray();
        if (combatVariants.Length == 0)
            return null;

        if (immediate
            && isTravelEnabled
            && combatVariants.Length == variants.Count
            && combatVariants.Select(variant => variant.Encounter!.Id).Distinct().Count() == 1)
        {
            var encounter = generatedEncounters[combatVariants[0].Encounter!.Id];
            return Forecast<IReadOnlyList<MonsterHpDetails>>.CurrentWorldline(
                MonsterHpPredictor.Predict(run, encounter),
                PredictionDependency.Niche | PredictionDependency.PlayerState,
                "No room remains between the current state and this combat.");
        }

        return Forecast<IReadOnlyList<MonsterHpDetails>>.BranchWithoutValue(
            PredictionDependency.Niche | PredictionDependency.PlayerState,
            "A preceding room or unresolved route can consume Niche RNG before this combat.");
    }

    private static Forecast<MerchantInventoryForecast>? BuildMerchantForecast(
        MapPoint point,
        IReadOnlyList<RouteVariantForecast> variants,
        bool immediate,
        bool isTravelEnabled)
    {
        var isShopPoint = point.PointType == MapPointType.Shop;
        if (!isShopPoint && point.PointType != MapPointType.Unknown)
            return null;

        var shops = variants
            .Where(variant => variant.Merchant is not null)
            .Select(variant => variant.Merchant!)
            .ToArray();
        if (shops.Length == 0)
        {
            // An unknown point carries inventory only when some feasible route
            // resolves it to a shop; otherwise the room-type label is enough.
            return isShopPoint
                ? Forecast<MerchantInventoryForecast>.Unsupported(
                    "No merchant route could be simulated.",
                    PredictionDependency.Shops)
                : null;
        }

        var ordinals = shops
            .Where(forecast => forecast.HasValue)
            .Select(forecast => forecast.Value!.FutureVisitOrdinal)
            .Distinct()
            .ToArray();
        if (ordinals.Length == 1)
        {
            var forecast = shops.First(candidate =>
                candidate.HasValue && candidate.Value!.FutureVisitOrdinal == ordinals[0]);
            if (isShopPoint && immediate && isTravelEnabled && ordinals[0] == 1)
                return forecast;

            return Forecast<MerchantInventoryForecast>.Branch(
                forecast.Value!,
                isShopPoint
                    ? forecast.Dependencies
                    : forecast.Dependencies | PredictionDependency.UnknownMapPoint,
                isShopPoint
                    ? "Conditional inventory from the current prediction state; earlier rewards, purchases, restocks, or removals can change it."
                    : "Conditional on the unknown room resolving to a shop; earlier rewards, purchases, restocks, or removals can change it.");
        }

        if (ordinals.Length > 1)
        {
            return Forecast<MerchantInventoryForecast>.BranchWithoutValue(
                PredictionDependency.Shops
                | PredictionDependency.Rewards
                | PredictionDependency.RelicGrabBag
                | PredictionDependency.CardRarityOdds
                | PredictionDependency.PlayerState,
                "Different feasible routes reach this as a different future merchant visit.");
        }

        return shops[0];
    }
}
