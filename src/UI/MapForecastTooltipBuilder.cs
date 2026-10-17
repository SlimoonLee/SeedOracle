using System.Text.Json;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal sealed record MapForecastTooltip(
    string Title,
    IReadOnlyList<string> Descriptions,
    IReadOnlyList<RouteMarkerAssignment> RouteMarkers,
    IReadOnlyList<RouteLineAssignment> RouteLines);

internal static class MapForecastTooltipBuilder
{
    private const int MaximumDisplayedRoutesPerGroup = 3;
    private const int MaximumBossPanels = 3;

    /// <summary>
    /// Narrows a map forecast to the worldline represented by the current
    /// route plan. Planning mode has an explicit route choice, so showing the
    /// all-route breakdown here would describe branches the player did not
    /// select. The target node itself is omitted from RouteVariantForecast.Route
    /// by the forecasting service; only planned rooms before it are matched.
    /// </summary>
    public static MapNodeForecast NarrowToPlan(
        MapNodeForecast forecast,
        RoutePlan? plan,
        MapCoord targetCoord)
    {
        var variant = MatchPlanVariant(forecast.RouteVariants, plan, targetCoord)
                      ?? forecast.RouteVariants.FirstOrDefault();
        if (variant is null)
            return forecast;

        var eventForecast = variant.Event is null
            ? null
            : Forecast<IReadOnlyList<EventDetails>>.CurrentWorldline(
                [variant.Event],
                forecast.Events?.Dependencies
                    ?? PredictionDependency.EventState | PredictionDependency.PlayerState,
                forecast.Events?.Reason);
        var encounterForecast = variant.Encounter is null
            ? null
            : Forecast<IReadOnlyList<EncounterDetails>>.CurrentWorldline(
                [variant.Encounter],
                forecast.Encounter?.Dependencies
                    ?? PredictionDependency.EncounterQueue | PredictionDependency.RunSeed | PredictionDependency.Floor,
                forecast.Encounter?.Reason);
        var unknownForecast = forecast.Point.PointType == MapPointType.Unknown
            ? Forecast<RoomType>.CurrentWorldline(
                variant.RoomType,
                forecast.UnknownRoom?.Dependencies
                    ?? PredictionDependency.UnknownMapPoint | PredictionDependency.EventState,
                forecast.UnknownRoom?.Reason)
            : null;

        return forecast with
        {
            UnknownRoom = MapForecastService.ApplyConditions(unknownForecast,
                forecast.RoutesTruncated || variant.HasUnmodeledStateDependency
                || forecast.UnknownRoom?.Accuracy == ForecastAccuracy.BranchDependent),
            Events = MapForecastService.ApplyConditions(eventForecast,
                forecast.RoutesTruncated || variant.HasUnmodeledStateDependency
                || forecast.Events?.Accuracy == ForecastAccuracy.BranchDependent),
            Encounter = MapForecastService.ApplyConditions(encounterForecast,
                forecast.RoutesTruncated || forecast.Encounter?.Accuracy == ForecastAccuracy.BranchDependent),
            Merchant = variant.Merchant,
            MonsterHp = variant.Encounter is null ? null : forecast.MonsterHp,
            RouteVariants = [variant]
        };
    }

    private static RouteVariantForecast? MatchPlanVariant(
        IReadOnlyList<RouteVariantForecast> variants,
        RoutePlan? plan,
        MapCoord targetCoord)
    {
        if (variants.Count == 0)
            return null;

        var entries = plan?.Phase == RoutePlanPhase.Active
            ? plan.Entries
            : [];
        var headIndex = 0;
        while (headIndex < entries.Count && entries[headIndex].IsCompleted)
            headIndex++;

        // If the hovered node is planned, match the rooms before that entry.
        // For a node outside the plan, require the whole active plan prefix;
        // if no route contains it, the caller deliberately falls back to the
        // first variant while still keeping a single planning tooltip.
        var targetIndex = -1;
        for (var index = headIndex; index < entries.Count; index++)
        {
            if (entries[index].Coord == targetCoord)
            {
                targetIndex = index;
                break;
            }
        }

        var targetChoice = targetIndex >= 0 ? entries[targetIndex].Choice : null;
        var choiceVariants = variants
            .Where(variant => MatchesPlanChoice(variant, targetChoice))
            .ToArray();
        if (choiceVariants.Length == 0)
            choiceVariants = variants.ToArray();

        var targetIsPlanned = targetIndex >= 0;
        var requiredEnd = targetIsPlanned ? targetIndex : entries.Count;
        if (requiredEnd <= headIndex)
            return choiceVariants[0];

        foreach (var variant in choiceVariants)
        {
            var coords = variant.Route
                .Select(choice => choice.Point.coord)
                .ToArray();
            var plannedIndex = headIndex;
            var consuming = false;
            var matched = true;
            foreach (var coord in coords)
            {
                if (plannedIndex < requiredEnd && coord == entries[plannedIndex].Coord)
                {
                    consuming = true;
                    plannedIndex++;
                }
                else if (!targetIsPlanned && consuming && plannedIndex >= requiredEnd)
                {
                    // The active plan is a prefix of this route. Once that
                    // prefix is consumed, arbitrary travelable rooms may lead
                    // to a hovered node that has not been added to the plan.
                    continue;
                }
                else if (!consuming)
                {
                    continue;
                }
                else
                {
                    matched = false;
                    break;
                }
            }

            if (matched && plannedIndex == requiredEnd)
                return variant;
        }

        return null;
    }

    internal static bool MatchesPlanChoice(
        RouteVariantForecast variant,
        RoutePlanChoice? choice) => choice switch
    {
        RoutePlanChoice.EventOption { OptionIndex: >= 0 } => variant.Event is not null,
        RoutePlanChoice.Merchant => variant.Merchant is not null,
        RoutePlanChoice.RestSite { OptionId: { Length: > 0 } } => variant.RoomType == RoomType.RestSite,
        RoutePlanChoice.CardReward => variant.CombatRewards is not null || variant.Encounter is not null,
        RoutePlanChoice.Potion => variant.CombatRewards is not null || variant.Merchant is not null,
        RoutePlanChoice.Relic => variant.CombatRewards is not null || variant.Treasure is not null,
        RoutePlanChoice.Combat => variant.CombatRewards is not null || variant.Encounter is not null,
        _ => true
    };

    public static MapForecastTooltip? Build(
        MapNodeForecast forecast,
        PreCombatForecastDisplay? combatSolver = null)
    {
        var chinese = LocManager.Instance?.Language is "zhs" or "zht";
        var markerPlan = RouteMarkerPlanner.Build(forecast);
        var showRouteBreakdown = ShouldShowRouteBreakdown(forecast, markerPlan);
        List<List<string>> panels;

        if (showRouteBreakdown)
        {
            panels = forecast.Point.PointType == MapPointType.Boss
                ? BuildBossRouteBreakdownPanels(forecast, markerPlan, chinese)
                : BuildRouteBreakdownPanels(forecast, markerPlan, chinese);
            if (forecast.MonsterHp is { } routeHp)
                AppendHp(panels[^1], routeHp, chinese);
        }
        else
        {
            var lines = new List<string>();
            if (forecast.UnknownRoom is { } unknown)
                AppendUnknown(lines, unknown, chinese);
            if (forecast.Events is { } events)
                AppendEvents(lines, events, chinese);
            if (forecast.Encounter is { } encounter)
                AppendEncounter(lines, encounter, chinese);
            if (forecast.MonsterHp is { } hp)
                AppendHp(lines, hp, chinese);
            if (forecast.Merchant is { } merchant)
                AppendMerchant(lines, merchant, chinese);
            if (forecast.RouteVariants.FirstOrDefault() is { } representative)
                AppendRewards(lines, representative, chinese);
            panels = [lines];
        }

        if (combatSolver is not null)
        {
            if (panels.Count == 0)
                panels.Add([]);
            AppendCombatSolver(panels[^1], combatSolver, chinese);
        }

        if (panels.Count == 0)
            panels.Add([]);
        AppendPredictionConditions(panels[^1], forecast, chinese);
        panels.RemoveAll(panel => panel.Count == 0);
        if (panels.Count == 0)
            return null;

        if (forecast.MaximumSteps > 1)
        {
            var distance = forecast.MinimumSteps == forecast.MaximumSteps
                ? forecast.MinimumSteps.ToString()
                : $"{forecast.MinimumSteps}–{forecast.MaximumSteps}";
            panels[0].Insert(0, chinese ? $"距离：{distance} 层" : $"Distance: {distance} floors");
        }

        return new MapForecastTooltip(
            chinese ? "Seed Oracle · 节点前瞻" : "Seed Oracle · Node Forecast",
            panels.Select(panel => string.Join("\n", panel)).ToArray(),
            showRouteBreakdown ? markerPlan.Assignments : [],
            showRouteBreakdown ? markerPlan.Lines : []);
    }

    private static bool ShouldShowRouteBreakdown(
        MapNodeForecast forecast,
        RouteMarkerPlan markerPlan)
    {
        if (forecast.RouteVariants.Count <= 1)
            return false;

        return markerPlan.Groups.Count > 1;
    }

    private static void AppendPredictionConditions(List<string> lines, MapNodeForecast forecast, bool chinese)
    {
        if (forecast.RoutesTruncated)
            lines.Add(chinese
                ? $"[gold]路线截断：已推演{forecast.RouteVariants.Count}条。[/gold]"
                : $"[gold]Routes truncated: {forecast.RouteVariants.Count} evaluated.[/gold]");
        if (forecast.DetailsTruncated)
            lines.Add(chinese
                ? $"[gold]掉落/选项已算{forecast.DetailRoutesComputed}/{forecast.RouteVariants.Count}条路线。[/gold]"
                : $"[gold]Contents evaluated for {forecast.DetailRoutesComputed}/{forecast.RouteVariants.Count} routes.[/gold]");
    }

    private static List<List<string>> BuildRouteBreakdownPanels(
        MapNodeForecast forecast,
        RouteMarkerPlan markerPlan,
        bool chinese)
    {
        var panels = new List<List<string>>();
        var groups = markerPlan.Groups;
        var shops = groups.Where(group => group.Representative.RoomType == RoomType.Shop
            && group.Representative.Merchant is { HasValue: true }).ToArray();

        if (shops.Length > 0)
        {
            var lines = new List<string>();
            var accuracy = shops.Max(group => group.Representative.Merchant!.Accuracy);
            lines.Add($"{(chinese ? "商店库存" : "Merchant stock")}  {Badge(accuracy, chinese)}");
            var showOrdinal = shops.Select(group => group.Representative.Merchant!.Value!.FutureVisitOrdinal)
                .Distinct().Count() > 1;
            AppendMerchantSummary(lines, shops.SelectMany(group =>
            {
                var inventory = group.Representative.Merchant!.Value!;
                var condition = BuildRouteLabel(group, chinese);
                if (showOrdinal || inventory.FutureVisitOrdinal > 1)
                    condition += chinese ? $"（第{inventory.FutureVisitOrdinal}次进店）"
                        : $" (visit {inventory.FutureVisitOrdinal})";
                return MerchantForecastSummary.Cases(inventory, condition, CompleteRouteCount(group));
            }), chinese, "");
            panels.Add(lines);
        }

        foreach (var group in groups)
        {
            if (shops.Contains(group))
                continue;
            var lines = new List<string>();
            var routes = group.Variants
                .Select(variant => FormatRoute(variant.Route, chinese))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var completeRouteCount = group.Variants
                .Select(variant => string.Join(
                    ">",
                    variant.Route.Select(choice =>
                        $"{choice.Point.coord.row},{choice.Point.coord.col},{choice.UsedFreeTravel}")))
                .Distinct(StringComparer.Ordinal)
                .Count();
            var routeLabel = FormatRouteLabel(group, routes, completeRouteCount, chinese);
            AppendRouteOutcome(lines, group.Representative, routeLabel, chinese);
            panels.Add(lines);
        }

        if (panels.Count == 0)
            panels.Add([]);
        return panels;
    }

    private static List<List<string>> BuildBossRouteBreakdownPanels(
        MapNodeForecast forecast,
        RouteMarkerPlan markerPlan,
        bool chinese)
    {
        var groups = markerPlan.Groups;
        var displayedGroups = groups
            .ToArray();
        var encounters = displayedGroups
            .Select(group => group.Representative.Encounter)
            .Where(encounter => encounter is not null)
            .DistinctBy(encounter => encounter!.Id)
            .ToArray();
        var hasSharedEncounter = encounters.Length == 1
                                 && displayedGroups.All(group =>
                                     group.Representative.Encounter?.Id == encounters[0]!.Id);
        var panelCount = Math.Min(MaximumBossPanels, Math.Max(1, displayedGroups.Length));
        var groupsPerPanel = Math.Max(
            1,
            (int)Math.Ceiling(displayedGroups.Length / (double)panelCount));
        var panels = displayedGroups
            .Chunk(groupsPerPanel)
            .Select(chunk =>
            {
                var lines = new List<string>();
                foreach (var group in chunk)
                {
                    var routeLabel = BuildRouteLabel(group, chinese);
                    AppendCompactBossOutcome(
                        lines,
                        group.Representative,
                        routeLabel,
                        chinese,
                        includeEncounter: !hasSharedEncounter);
                }
                return lines;
            })
            .ToList();

        if (panels.Count == 0)
            panels.Add([]);

        if (hasSharedEncounter)
        {
            var encounter = encounters[0]!;
            panels[0].Insert(0, chinese
                ? $"怪物：{string.Join(" + ", encounter.Monsters)}"
                : $"Monsters: {string.Join(" + ", encounter.Monsters)}");
            panels[0].Insert(0, chinese
                ? $"Boss：{encounter.Title}"
                : $"Boss: {encounter.Title}");
        }

        panels[0].Insert(0, chinese
            ? "[gold]判定：从当前位置连续沿同色线抵达 Boss；共线色带表示结果尚未分开。[/gold]"
            : "[gold]Rule: follow one color continuously from the current node to the Boss; shared lanes mean the outcome has not diverged yet.[/gold]");
        panels[0].Insert(0, chinese
            ? "按完整路线颜色（Boss 结果合并分栏）："
            : "By complete route color (compact Boss layout):");

        return panels;
    }

    private static string BuildRouteLabel(RouteOutcomeGroup group, bool chinese)
    {
        var routes = group.Variants
            .Select(variant => FormatRoute(variant.Route, chinese))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return FormatRouteLabel(group, routes, CompleteRouteCount(group), chinese);
    }

    private static int CompleteRouteCount(RouteOutcomeGroup group) => group.Variants
            .Select(variant => string.Join(
                ">",
                variant.Route.Select(choice =>
                    $"{choice.Point.coord.row},{choice.Point.coord.col},{choice.UsedFreeTravel}")))
            .Distinct(StringComparer.Ordinal)
            .Count();

    private static void AppendCompactBossOutcome(
        List<string> lines,
        RouteVariantForecast variant,
        string routeLabel,
        bool chinese,
        bool includeEncounter)
    {
        var encounterSuffix = !includeEncounter || variant.Encounter is null
            ? string.Empty
            : $" · {variant.Encounter.Title}";

        if (variant.CombatRewards is not { HasValue: true } rewardForecast)
        {
            lines.Add(chinese
                ? $"{routeLabel}{encounterSuffix}：战后掉落暂不可安全预测"
                : $"{routeLabel}{encounterSuffix}: no safe combat-reward forecast");
            return;
        }

        var rewards = rewardForecast.Value!;
        var parts = new List<string>
        {
            chinese
                ? $"基础金币 [blue]{rewards.Gold}[/blue]"
                : $"[blue]{rewards.Gold}[/blue] base gold"
        };
        for (var index = 0; index < rewards.CardRewards.Count; index++)
        {
            var heading = index == 0
                ? chinese ? "卡牌" : "cards"
                : chinese ? $"额外卡牌 {index}" : $"extra cards {index}";
            parts.Add($"{heading} {FormatItems(rewards.CardRewards[index].Select(item => item.Item))}");
        }
        parts.Add(rewards.Potions.Count == 0
            ? chinese ? "药水 无" : "no potion"
            : $"{(chinese ? "药水" : "potion")} {FormatItems(rewards.Potions.Select(item => item.Item))}");
        if (rewards.Relics.Count > 0)
        {
            parts.Add($"{(chinese ? "遗物" : "relic")} "
                      + FormatItems(rewards.Relics.Select(item => item.Item)));
        }

        lines.Add($"{routeLabel}{encounterSuffix}{(chinese ? "：" : ":")}");
        var choices = RewardForecastSummary.Group(rewards, rewards.ReferenceCondition,
            rewards.ReferenceChoicePaths, rewards.ChoiceBranches, ForecastContentKey.Combat);
        var conditions = new ForecastConditionDisplay(choices.SelectMany(group => group.Terms), chinese);
        conditions.AppendPrerequisites(lines, chinese, "  ");
        AppendOutcomeCondition(lines, conditions.Format(choices[0].Terms), chinese, "  ");
        lines.Add("  " + string.Join(chinese ? "；" : "; ", parts)
                  + "  " + Badge(rewardForecast.Accuracy, chinese));
        AppendRewardAlternatives(lines, rewards, chinese, "  ");
        AppendCombatPossibilities(lines, rewards, chinese, "  ");
        AppendRewardChanges(lines, choices, conditions, chinese, "  ",
            (before, after) => ForecastChoiceDiffFormatter.Combat(before, after, chinese));
    }

    private static string FormatRouteLabel(
        RouteOutcomeGroup group,
        IReadOnlyList<string> routes,
        int completeRouteCount,
        bool chinese)
    {
        if (group.UsesMarker && group.Style is { } markerStyle)
        {
            var text = chinese
                ? $"{markerStyle.Glyph} {markerStyle.ChineseName}节点路线"
                : $"{markerStyle.Glyph} {markerStyle.EnglishName} marker route";
            return $"[{markerStyle.RichTextTag}]{text}[/{markerStyle.RichTextTag}]";
        }

        if (group.UsesLine && group.Style is { } lineStyle)
        {
            var text = chinese
                ? $"{lineStyle.ChineseColorName}线" + (completeRouteCount > 1 ? $"（{completeRouteCount}条）" : "")
                : $"{lineStyle.EnglishColorName} line" + (completeRouteCount > 1 ? $" ({completeRouteCount} routes)" : "");
            return $"[{lineStyle.RichTextTag}]{text}[/{lineStyle.RichTextTag}]";
        }

        var shownRoutes = string.Join(
            chinese ? "；" : "; ",
            routes.Take(MaximumDisplayedRoutesPerGroup));
        if (routes.Count > MaximumDisplayedRoutesPerGroup)
        {
            shownRoutes += chinese
                ? $"；另 {routes.Count - MaximumDisplayedRoutesPerGroup} 条"
                : $"; plus {routes.Count - MaximumDisplayedRoutesPerGroup} more";
        }
        return chinese
            ? $"[gold]路线 {shownRoutes}[/gold]"
            : $"[gold]Route {shownRoutes}[/gold]";
    }

    private static void AppendRouteOutcome(
        List<string> lines,
        RouteVariantForecast variant,
        string routeLabel,
        bool chinese)
    {
        if (variant.Event is { } eventDetails)
        {
            lines.Add(chinese
                ? $"{routeLabel}：事件 · {eventDetails.Title}"
                : $"{routeLabel}: Event · {eventDetails.Title}");
            if (variant.EventContents is { } eventContents)
                AppendEventContents(lines, eventContents, chinese, "  ");
            return;
        }

        if (variant.Encounter is { } encounter)
        {
            lines.Add(chinese
                ? $"{routeLabel}：{RoomName(variant.RoomType, true)} · {encounter.Title}"
                : $"{routeLabel}: {RoomName(variant.RoomType, false)} · {encounter.Title}");
            lines.Add(chinese
                ? $"  怪物：{string.Join(" + ", encounter.Monsters)}"
                : $"  Monsters: {string.Join(" + ", encounter.Monsters)}");
            AppendRewards(lines, variant, chinese, "  ");
            return;
        }

        if (variant.RoomType == RoomType.Shop)
        {
            if (variant.Merchant is not { HasValue: true } merchant)
            {
                lines.Add(chinese
                    ? $"{routeLabel}：商店库存暂不可安全预测"
                    : $"{routeLabel}: no safe merchant forecast");
                return;
            }

            var ordinal = merchant.Value!.FutureVisitOrdinal;
            lines.Add(chinese
                ? $"{routeLabel}：从当前起第 {ordinal} 次商店"
                : $"{routeLabel}: merchant visit {ordinal} from now");
            AppendMerchantContents(lines, merchant.Value, chinese, "  ");
            return;
        }

        lines.Add($"{routeLabel}{(chinese ? "：" : ": ")}{RoomName(variant.RoomType, chinese)}");
        AppendRewards(lines, variant, chinese, "  ");
    }

    private static string FormatRoute(IReadOnlyList<RouteChoice> route, bool chinese)
    {
        var choices = route.Where(choice => choice.IsDecision).ToArray();
        if (choices.Length == 0)
            choices = route.ToArray();
        if (choices.Length == 0)
            return chinese ? "直达" : "direct";

        return string.Join(
            " → ",
            choices.Select(choice =>
            {
                var flight = choice.UsedFreeTravel
                    ? chinese ? "（飞行）" : " (flight)"
                    : string.Empty;
                return chinese
                    ? $"第{choice.Floor}层左起第{choice.PositionFromLeft}个{flight}"
                    : $"floor {choice.Floor}, node {choice.PositionFromLeft} from left{flight}";
            }));
    }

    private static void AppendUnknown(List<string> lines, Forecast<RoomType> forecast, bool chinese)
    {
        if (forecast.HasValue)
        {
            var room = RoomName(forecast.Value, chinese);
            lines.Add($"? → {room}  {Badge(forecast.Accuracy, chinese)}");
        }
    }

    private static void AppendEvents(
        List<string> lines,
        Forecast<IReadOnlyList<EventDetails>> forecast,
        bool chinese)
    {
        if (!forecast.HasValue)
            return;

        var events = forecast.Value!;
        if (events.Count == 1)
        {
            lines.Add(chinese
                ? $"事件：{events[0].Title}  {Badge(forecast.Accuracy, true)}"
                : $"Event: {events[0].Title}  {Badge(forecast.Accuracy, false)}");
            return;
        }

        lines.Add(chinese
            ? $"可能事件：{Badge(forecast.Accuracy, true)}"
            : $"Possible events: {Badge(forecast.Accuracy, false)}");
        foreach (var eventDetails in events)
            lines.Add($"• {eventDetails.Title}");
    }

    private static void AppendEncounter(
        List<string> lines,
        Forecast<IReadOnlyList<EncounterDetails>> forecast,
        bool chinese)
    {
        if (!forecast.HasValue)
        {
            lines.Add(chinese ? "遭遇：暂不可安全预测" : "Encounter: no safe forecast available");
            return;
        }

        var encounters = forecast.Value!;
        if (encounters.Count == 1)
        {
            var encounter = encounters[0];
            lines.Add(chinese
                ? $"遭遇：{encounter.Title}  {Badge(forecast.Accuracy, true)}"
                : $"Encounter: {encounter.Title}  {Badge(forecast.Accuracy, false)}");
            lines.Add(chinese
                ? $"怪物：{string.Join(" + ", encounter.Monsters)}"
                : $"Monsters: {string.Join(" + ", encounter.Monsters)}");
            return;
        }

        lines.Add(chinese
            ? $"可能遭遇：{Badge(forecast.Accuracy, true)}"
            : $"Possible encounters: {Badge(forecast.Accuracy, false)}");
        foreach (var encounter in encounters)
            lines.Add($"• {encounter.Title}: {string.Join(" + ", encounter.Monsters)}");
    }

    private static void AppendHp(
        List<string> lines,
        Forecast<IReadOnlyList<MonsterHpDetails>> forecast,
        bool chinese)
    {
        if (!forecast.HasValue)
        {
            return;
        }

        var hp = string.Join(" / ", forecast.Value!.Select(monster => $"{monster.Monster} {monster.Hp}"));
        lines.Add(chinese
            ? $"HP：{hp}  {Badge(forecast.Accuracy, true)}"
            : $"HP: {hp}  {Badge(forecast.Accuracy, false)}");
    }

    private static void AppendCombatSolver(
        List<string> lines,
        PreCombatForecastDisplay forecast,
        bool chinese)
    {
        switch (forecast.Status)
        {
            case PreCombatDisplayStatus.Deferred:
                return;
            case PreCombatDisplayStatus.NotCalculated:
                return;
            case PreCombatDisplayStatus.Pending:
                lines.Add(chinese
                    ? "[gold]战损计算中…[/gold]"
                    : "[gold]Calculating HP loss…[/gold]");
                return;
            case PreCombatDisplayStatus.Failed:
                lines.Add(chinese
                    ? $"[orange]Combat Solver：本次无法计算（{ShortError(forecast.Error)}）。[/orange]"
                    : $"[orange]Combat Solver: unavailable for this request ({ShortError(forecast.Error)}).[/orange]");
                return;
            case PreCombatDisplayStatus.Succeeded:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var loss = forecast.ProjectedHpLoss?.ToString() ?? "?";
        lines.Add(chinese
            ? $"Combat Solver：预计战损 [blue]{loss}[/blue] HP"
            : $"Combat Solver: projected loss [blue]{loss}[/blue] HP");
        var potionUses = forecast.PotionUses ?? [];
        lines.Add(potionUses.Count == 0
            ? chinese ? "  药水：不使用" : "  Potions: none used"
            : chinese
                ? "  药水：" + string.Join("；", potionUses.Select(use => $"第 {use.Turn} 回合 {use.Title}"))
                : "  Potions: " + string.Join("; ", potionUses.Select(use => $"turn {use.Turn} {use.Title}")));
        var confidence = forecast.Confidence switch
        {
            "Complete" => chinese ? "已找到结束战斗路线" : "combat-ending route found",
            "DeathOnly" => chinese ? "仅找到死亡路线" : "death routes only",
            _ => chinese ? "短时限内的最佳路线" : "best route within the short limit"
        };
        var boundary = string.IsNullOrWhiteSpace(forecast.SearchBoundary)
            ? string.Empty
            : chinese
                ? $"；边界 {forecast.SearchBoundary}"
                : $"; boundary {forecast.SearchBoundary}";
        lines.Add($"  {confidence}{boundary}");
        lines.Add(chinese
            ? "[gold]战损按求解器打法。[/gold]"
            : "[gold]HP loss follows the solver's plays.[/gold]");
    }

    private static string ShortError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "unknown error";
        var singleLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 96 ? singleLine : singleLine[..93] + "...";
    }

    private static void AppendMerchant(
        List<string> lines,
        Forecast<MerchantInventoryForecast> forecast,
        bool chinese)
    {
        if (!forecast.HasValue)
        {
            return;
        }

        var merchant = forecast.Value!;
        var conditional = merchant.FutureVisitOrdinal > 1;
        lines.Add(chinese
            ? conditional
                ? $"若为从当前起第 {merchant.FutureVisitOrdinal} 次进店  {Badge(forecast.Accuracy, true)}"
                : $"商店库存  {Badge(forecast.Accuracy, true)}"
            : conditional
                ? $"If this is merchant visit {merchant.FutureVisitOrdinal} from now  {Badge(forecast.Accuracy, false)}"
                : $"Merchant stock  {Badge(forecast.Accuracy, false)}");
        AppendMerchantContents(lines, merchant, chinese);
    }

    internal static void AppendMerchantContents(
        List<string> lines,
        MerchantInventoryForecast merchant,
        bool chinese,
        string prefix = "")
    {
        AppendMerchantSummary(lines, MerchantForecastSummary.Cases(merchant), chinese, prefix);
    }

    internal static void AppendMerchantSummary(List<string> lines,
        IEnumerable<MerchantForecastCase> cases, bool chinese, string prefix = "")
    {
        var groups = MerchantForecastSummary.Group(cases);
        if (groups.Count == 0)
            return;
        var conditions = new ForecastConditionDisplay(groups.SelectMany(group => group.Terms), chinese);
        conditions.AppendPrerequisites(lines, chinese, prefix);
        var baseline = groups[0];
        var baseCondition = conditions.Format(baseline.Terms);
        if (groups.Count > 1 || baseCondition.Length > 0)
            lines.Add(prefix + (chinese ? "基础库存" : "Base stock")
                + (baseCondition.Length > 0 ? (chinese ? "：" : ": ") + baseCondition : (chinese ? "：" : ":")));
        AppendMerchantStock(lines, baseline.Inventory, chinese, prefix);
        foreach (var group in groups.Skip(1))
        {
            var changes = ForecastChoiceDiffFormatter.MerchantCompact(baseline.Inventory, group.Inventory, chinese);
            if (changes.Count == 0) continue;
            lines.Add(prefix + conditions.Format(group.Terms) + (chinese ? "（相对基础）：" : " (vs. base):"));
            lines.AddRange(changes.Select(change => prefix + "  " + change));
        }
    }

    private static void AppendMerchantStock(List<string> lines, MerchantInventoryForecast merchant,
        bool chinese, string prefix)
    {
        AppendItems(lines, prefix + (chinese ? "角色卡" : "Character cards"), merchant.CharacterCards, chinese);
        AppendItems(lines, prefix + (chinese ? "无色卡" : "Colorless cards"), merchant.ColorlessCards, chinese);
        AppendItems(lines, prefix + (chinese ? "遗物" : "Relics"), merchant.Relics, chinese);
        AppendItems(lines, prefix + (chinese ? "药水" : "Potions"), merchant.Potions, chinese);
        lines.Add(chinese
            ? $"{prefix}删牌：{merchant.CardRemovalCost}金"
            : $"{prefix}Card removal: {merchant.CardRemovalCost}g");
    }

    internal static void AppendRewards(
        List<string> lines,
        RouteVariantForecast variant,
        bool chinese,
        string prefix = "")
    {
        if (variant.EventContents is { } eventContents)
            AppendEventContents(lines, eventContents, chinese, prefix);
        if (variant.CombatRewards is { } combat)
            AppendCombatRewards(lines, combat, chinese, prefix);
        if (variant.Treasure is { } treasure)
            AppendTreasure(lines, treasure, chinese, prefix);
    }

    internal static void AppendEventContents(
        List<string> lines,
        Forecast<EventContentDetails> forecast,
        bool chinese,
        string prefix)
    {
        if (!forecast.HasValue)
        {
            lines.Add(chinese
                ? $"{prefix}事件内随机内容：暂不可安全预测"
                : $"{prefix}Random event contents: no safe forecast");
            return;
        }

        var details = forecast.Value!;
        var options = details.Options.Where(option => option.InitialItems.Count > 0
            || option.Sets.Any(set => set.Items.Count > 0)).ToArray();
        if (options.Length == 0)
            return;
        lines.Add(chinese
            ? $"{prefix}事件选项内容  {Badge(forecast.Accuracy, true)}"
            : $"{prefix}Event option contents  {Badge(forecast.Accuracy, false)}");
        foreach (var option in options)
        {
            lines.Add(chinese
                ? $"{prefix}  选项「{option.Option}」："
                : $"{prefix}  Option \"{option.Option}\":");
            if (option.InitialItems.Count > 0)
            {
                lines.Add(chinese
                    ? $"{prefix}    初始选项提示：{FormatItems(option.InitialItems)}"
                    : $"{prefix}    Initial option hints: {FormatItems(option.InitialItems)}");
            }
            var transformGroups = option.Sets.Where(set => set.SourceCard is not null)
                .ToLookup(TransformResultKey);
            var displayedTransforms = new HashSet<(int Position, EventPredictionKind Kind, string Result)>();
            var multipleTransforms = transformGroups.Select(group => group.Key.Position).Distinct().Skip(1).Any();
            for (var index = 0; index < option.Sets.Count; index++)
            {
                var set = option.Sets[index];
                if (set.Items.Count == 0)
                    continue;
                if (set.SourceCard is not null)
                {
                    var key = TransformResultKey(set);
                    if (!displayedTransforms.Add(key))
                        continue;
                    var group = transformGroups[key];
                    var sourceNames = group.Select(candidate => ForecastItemFormatter.Format(candidate.SourceCard!, false))
                        .Distinct(StringComparer.Ordinal).Take(2);
                    var sources = string.Join(chinese ? "、" : ", ", sourceNames);
                    var results = string.Join(" / ", set.Items.Select(item => ForecastItemFormatter.Format(item, false)));
                    var position = multipleTransforms
                        ? chinese ? $"第 {set.Position + 1} 次：" : $"Transform {set.Position + 1}: "
                        : "";
                    var count = group.Count();
                    lines.Add(chinese
                        ? $"{prefix}    {position}{sources}等{count}张牌变化为{results}"
                        : $"{prefix}    {position}{sources} ({count} {(count == 1 ? "card" : "cards")}) → {results}");
                }
                else if (set.Kind == EventPredictionKind.SequencePreview)
                {
                    foreach (var group in set.Items.Select((item, step) => (Item: item, Step: step + 1))
                                 .GroupBy(item => ForecastContentKey.Item(item.Item)))
                        lines.Add(chinese
                            ? $"{prefix}    若继续重抽第 {string.Join("/", group.Select(item => item.Step))} 次：{ForecastItemFormatter.Format(group.First().Item, false)}"
                            : $"{prefix}    Rerolls {string.Join("/", group.Select(item => item.Step))}: {ForecastItemFormatter.Format(group.First().Item, false)}");
                }
                else
                {
                    var label = (set.Kind, chinese) switch
                    {
                        (EventPredictionKind.ChoiceCandidates, true) => "目标候选",
                        (EventPredictionKind.ChoiceCandidates, false) => "Target candidates",
                        (EventPredictionKind.CardRewardCandidates, true) => "待选卡包候选",
                        (EventPredictionKind.CardRewardCandidates, false) => "Card reward candidates",
                        (EventPredictionKind.CombatVictoryConditional, true) => "战胜后",
                        (EventPredictionKind.CombatVictoryConditional, false) => "On victory",
                        (_, true) => "部分结果",
                        _ => "Partial result"
                    };
                    lines.Add($"{prefix}    {label} {index + 1}: {FormatItems(set.Items)}");
                }
            }
            if (option.Sets.Any(set => set.Kind == EventPredictionKind.ChoiceCandidates && set.Position > 0))
                lines.Add(chinese
                    ? $"{prefix}    按选牌顺序；同一张来源牌只能选一次。"
                    : $"{prefix}    In selection order; each source slot can be chosen once.");
        }
    }

    private static (int Position, EventPredictionKind Kind, string Result) TransformResultKey(
        EventPredictionSetDetails set) =>
        (set.Position, set.Kind, JsonSerializer.Serialize(set.Items.Select(ForecastContentKey.Item)));

    private static void AppendCombatRewards(
        List<string> lines,
        Forecast<CombatRewardDetails> forecast,
        bool chinese,
        string prefix)
    {
        if (!forecast.HasValue)
        {
            lines.Add(chinese
                ? $"{prefix}战后掉落：暂不可安全预测"
                : $"{prefix}Combat rewards: no safe forecast");
            return;
        }

        var rewards = forecast.Value!;
        var choices = RewardForecastSummary.Group(rewards, rewards.ReferenceCondition,
            rewards.ReferenceChoicePaths, rewards.ChoiceBranches, ForecastContentKey.Combat);
        var conditions = new ForecastConditionDisplay(choices.SelectMany(group => group.Terms), chinese);
        conditions.AppendPrerequisites(lines, chinese, prefix);
        AppendOutcomeCondition(lines, conditions.Format(choices[0].Terms), chinese, prefix);
        lines.Add(chinese
            ? $"{prefix}胜利掉落：[blue]{rewards.Gold}[/blue]金币  {Badge(forecast.Accuracy, true)}"
            : $"{prefix}Base rewards on victory: [blue]{rewards.Gold}[/blue] gold  {Badge(forecast.Accuracy, false)}");
        for (var index = 0; index < rewards.CardRewards.Count; index++)
        {
            var heading = index == 0
                ? chinese ? "卡牌" : "Cards"
                : chinese ? $"额外卡牌 {index}" : $"Extra cards {index}";
            AppendRewardItems(lines, prefix + "  " + heading, rewards.CardRewards[index], chinese);
        }
        if (rewards.Potions.Count > 0)
            AppendRewardItems(lines, prefix + "  " + (chinese ? "药水" : "Potion"), rewards.Potions, chinese);
        else
            lines.Add(chinese ? $"{prefix}  药水：无" : $"{prefix}  Potion: none");
        if (rewards.Relics.Count > 0)
            AppendRewardItems(lines, prefix + "  " + (chinese ? "遗物" : "Relic"), rewards.Relics, chinese);
        AppendRewardAlternatives(lines, rewards, chinese, prefix + "  ");
        AppendCombatPossibilities(lines, rewards, chinese, prefix + "  ");
        AppendRewardChanges(lines, choices, conditions, chinese, prefix,
            (before, after) => ForecastChoiceDiffFormatter.Combat(before, after, chinese));
    }

    private static void AppendCombatPossibilities(
        List<string> lines, CombatRewardDetails rewards, bool chinese, string prefix)
    {
        foreach (var results in rewards.Possibilities.OrderBy(branch => branch.HuntTriggers)
                     .ThenBy(branch => branch.RerollOrder.Count)
                     .GroupBy(branch => JsonSerializer.Serialize(branch.Groups.Select(group =>
                         group.Cards.Select(card => ForecastContentKey.Item(card.Item))))))
        {
            var conditions = results.Select(branch =>
            {
                var hunt = branch.HuntTriggers > 0
                    ? (chinese ? $"狩猎触发 {branch.HuntTriggers} 次；" : $"The Hunt x{branch.HuntTriggers}; ")
                    : rewards.HuntPreviewLimit is not null ? (chinese ? "狩猎未触发；" : "No Hunt trigger; ") : "";
                var order = branch.RerollOrder.Count == 0
                    ? (chinese ? "不重掷" : "no reroll")
                    : (chinese ? "重掷组 " : "reroll groups ")
                      + string.Join(" > ", branch.RerollOrder.Select(group => group + 1));
                return hunt + order;
            });
            var groups = results.First().Groups;
            var changedLines = new List<string>();
            for (var index = 0; index < groups.Count; index++)
            {
                if (index < rewards.CardRewardGroups.Count && groups[index].Cards.Select(card => ForecastContentKey.Item(card.Item))
                    .SequenceEqual(rewards.CardRewardGroups[index].Cards.Select(card => ForecastContentKey.Item(card.Item))))
                    continue;
                changedLines.Add($"{prefix}  {(chinese ? $"第{index + 1}组" : $"Group {index + 1}")}："
                    + string.Join(" / ", groups[index].Cards.Select(card => ForecastItemFormatter.Format(card.Item, false))));
            }
            if (changedLines.Count == 0) continue;
            lines.Add($"{prefix}{(chinese ? "领取前，" : "Before taking rewards, ")}{string.Join(chinese ? "；或 " : "; or ", conditions)}：");
            lines.AddRange(changedLines);
        }
        if (rewards.PossibilitiesTruncated)
            lines.Add(chinese
                ? $"{prefix}已算{rewards.Possibilities.Count}种重掷结果，组合未穷尽。"
                : $"{prefix}{rewards.Possibilities.Count} reroll results computed; combinations incomplete.");
        if (rewards.HuntPreviewLimit is { } huntLimit)
        {
            var evaluated = rewards.Possibilities.Select(result => result.HuntTriggers).Append(0).Distinct().Order().ToArray();
            var range = evaluated.SequenceEqual(Enumerable.Range(0, huntLimit + 1))
                ? $"0–{huntLimit}" : string.Join(chinese ? "、" : ", ", evaluated);
            lines.Add(chinese ? $"{prefix}狩猎已算斩杀{range}次。" : $"{prefix}Hunt kills evaluated: {range}.");
        }
    }

    private static void AppendRewardAlternatives(List<string> lines, CombatRewardDetails rewards,
        bool chinese, string prefix)
    {
        var groups = rewards.CardRewardGroups.Select((group, index) => (index, Alternatives: (group.Steps.FirstOrDefault()?.Alternatives ?? [])
                .Where(option => !option.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase)
                    && !option.OptionId.Equals("REROLL", StringComparison.OrdinalIgnoreCase)).ToArray()))
            .Where(group => group.Alternatives.Length > 0);
        foreach (var grouped in groups.GroupBy(group => string.Join("|", group.Alternatives.Select(option => option.OptionId))))
        {
            var slots = grouped.Select(group => group.index + 1).ToArray();
            var heading = chinese ? $"第{string.Join("、", slots)}组另可选：" : $"Groups {string.Join(", ", slots)} also offer: ";
            lines.Add(prefix + heading + string.Join(chinese ? "、" : ", ", grouped.First().Alternatives.Select(option => option.Title)));
        }
    }

    private static void AppendTreasure(
        List<string> lines,
        Forecast<TreasureRoomDetails> forecast,
        bool chinese,
        string prefix)
    {
        if (!forecast.HasValue)
        {
            lines.Add(chinese
                ? $"{prefix}宝箱内容：暂不可安全预测"
                : $"{prefix}Treasure contents: no safe forecast");
            return;
        }

        var treasure = forecast.Value!;
        var choices = RewardForecastSummary.Group(treasure, treasure.ReferenceCondition,
            treasure.ReferenceChoicePaths, treasure.ChoiceBranches, ForecastContentKey.Treasure);
        var conditions = new ForecastConditionDisplay(choices.SelectMany(group => group.Terms), chinese);
        conditions.AppendPrerequisites(lines, chinese, prefix);
        AppendOutcomeCondition(lines, conditions.Format(choices[0].Terms), chinese, prefix);
        if (treasure.IsEmpty)
        {
            lines.Add(chinese
                ? $"{prefix}宝箱：空  {Badge(forecast.Accuracy, true)}"
                : $"{prefix}Treasure: empty  {Badge(forecast.Accuracy, false)}");
            AppendRewardChanges(lines, choices, conditions, chinese, prefix,
                (before, after) => ForecastChoiceDiffFormatter.Treasure(before, after, chinese));
            return;
        }

        lines.Add(chinese
            ? $"{prefix}宝箱内容  {Badge(forecast.Accuracy, true)}"
            : $"{prefix}Treasure contents  {Badge(forecast.Accuracy, false)}");
        AppendRewardItems(
            lines,
            prefix + "  " + (chinese ? "遗物" : "Relics"),
            treasure.Relics,
            chinese);
        lines.Add(chinese
            ? $"{prefix}  金币：[blue]{treasure.Gold}[/blue]"
            : $"{prefix}  Gold: [blue]{treasure.Gold}[/blue]");
        AppendRewardChanges(lines, choices, conditions, chinese, prefix,
            (before, after) => ForecastChoiceDiffFormatter.Treasure(before, after, chinese));
    }

    private static void AppendOutcomeCondition(List<string> lines, string condition, bool chinese, string prefix)
    {
        if (!string.IsNullOrWhiteSpace(condition))
            lines.Add(prefix + (chinese ? "条件：" : "Conditions: ") + condition);
    }

    private static void AppendRewardChanges<T>(List<string> lines, IReadOnlyList<RewardOutcomeGroup<T>> groups,
        ForecastConditionDisplay conditions, bool chinese, string prefix,
        Func<T, T, IReadOnlyList<string>> diff)
    {
        foreach (var branch in groups.Skip(1))
        {
            var changes = diff(groups[0].Outcome, branch.Outcome);
            if (changes.Count == 0) continue;
            lines.Add(prefix + conditions.Format(branch.Terms) + (chinese ? "，变化：" : ", changes:"));
            lines.AddRange(changes.Select(change => prefix + "  " + change));
        }
    }

    private static void AppendRewardItems(
        List<string> lines,
        string heading,
        IEnumerable<RewardItemDetails> items,
        bool chinese)
    {
        lines.Add($"{heading}{(chinese ? "：" : ": ")}{FormatItems(items.Select(item => item.Item))}");
    }

    private static void AppendItems(
        List<string> lines,
        string heading,
        IEnumerable<MerchantItemForecast> items,
        bool chinese)
    {
        var stock = items.ToArray();
        if (stock.Length == 0)
            return;
        var text = stock.Select((item, index) =>
        {
            var sale = item.IsOnSale ? (chinese ? "（半价）" : " (sale)") : string.Empty;
            return $"{ForecastChoiceDiffFormatter.Slot(index)}{ForecastItemFormatter.Format(item.Item)} [blue]{item.Cost}[/blue]{sale}";
        });
        lines.Add($"{heading}{(chinese ? "：" : ": ")}{string.Join(chinese ? "、" : ", ", text)}");
    }

    private static string FormatItems(IEnumerable<ForecastItemDetails> items) =>
        string.Join(" / ", items.Select(ForecastItemFormatter.Format));

    private static string Badge(ForecastAccuracy accuracy, bool chinese) => accuracy switch
    {
        ForecastAccuracy.Exact => chinese ? "[green]✓ 确定[/green]" : "[green]✓ exact[/green]",
        ForecastAccuracy.ExactForCurrentWorldline => chinese
            ? "[gold]✓ 当前世界线[/gold]"
            : "[gold]✓ current worldline[/gold]",
        ForecastAccuracy.BranchDependent => chinese
            ? "[gold]~ 条件预测[/gold]"
            : "[gold]~ conditional[/gold]",
        ForecastAccuracy.Approximate => chinese ? "[orange]≈ 近似[/orange]" : "[orange]≈ approximate[/orange]",
        _ => chinese ? "? 暂不支持" : "? unsupported"
    };

    internal static string RoomName(RoomType roomType, bool chinese) => (roomType, chinese) switch
    {
        (RoomType.Monster, true) => "普通战",
        (RoomType.Elite, true) => "精英战",
        (RoomType.Boss, true) => "Boss",
        (RoomType.Treasure, true) => "宝箱",
        (RoomType.Shop, true) => "商店",
        (RoomType.Event, true) => "事件",
        (RoomType.RestSite, true) => "休息点",
        (RoomType.Monster, false) => "Monster",
        (RoomType.Elite, false) => "Elite",
        (RoomType.Boss, false) => "Boss",
        (RoomType.Treasure, false) => "Treasure",
        (RoomType.Shop, false) => "Merchant",
        (RoomType.Event, false) => "Event",
        (RoomType.RestSite, false) => "Rest Site",
        _ => roomType.ToString()
    };
}
