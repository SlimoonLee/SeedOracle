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
    private const int MaximumDisplayedOutcomeGroups = 8;
    private const int MaximumDisplayedRoutesPerGroup = 3;
    private const int MaximumBossPanels = 3;

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

    private static List<List<string>> BuildRouteBreakdownPanels(
        MapNodeForecast forecast,
        RouteMarkerPlan markerPlan,
        bool chinese)
    {
        var panels = new List<List<string>>();
        var groups = markerPlan.Groups;

        foreach (var group in groups.Take(MaximumDisplayedOutcomeGroups))
        {
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
        panels[0].Insert(0, chinese
            ? "[gold]判定：从当前位置连续沿同色线抵达目标；共线段表示结果尚未分开。[/gold]"
            : "[gold]Rule: follow one color continuously from the current node to the target; shared segments mean the outcome has not diverged yet.[/gold]");
        panels[0].Insert(0, chinese ? "按完整路线颜色：" : "By complete route color:");

        if (groups.Count > MaximumDisplayedOutcomeGroups)
        {
            panels[^1].Add(chinese
                ? $"另有 {groups.Count - MaximumDisplayedOutcomeGroups} 组不同结果未展开。"
                : $"{groups.Count - MaximumDisplayedOutcomeGroups} additional outcome groups are collapsed.");
        }

        if (forecast.RoutesTruncated)
        {
            panels[^1].Add(chinese
                ? "[gold]可行路线超过枚举上限，以上仅为已计算部分。[/gold]"
                : "[gold]The route count exceeded the enumeration limit; only computed routes are shown.[/gold]");
        }

        if (forecast.RouteVariants.Any(variant => variant.HasUnmodeledStateDependency))
        {
            panels[^1].Add(chinese
                ? "[gold]条件预测：沿途状态按当前值。[/gold]"
                : "[gold]Conditional: intervening state remains as currently modeled.[/gold]");
        }

        return panels;
    }

    private static List<List<string>> BuildBossRouteBreakdownPanels(
        MapNodeForecast forecast,
        RouteMarkerPlan markerPlan,
        bool chinese)
    {
        var groups = markerPlan.Groups;
        var displayedGroups = groups
            .Take(MaximumDisplayedOutcomeGroups)
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

        AppendRouteBreakdownFooter(panels, forecast, groups.Count, chinese);
        return panels;
    }

    private static string BuildRouteLabel(RouteOutcomeGroup group, bool chinese)
    {
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
        return FormatRouteLabel(group, routes, completeRouteCount, chinese);
    }

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
                ? $"金币 [blue]{rewards.Gold}[/blue]"
                : $"[blue]{rewards.Gold}[/blue] gold"
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
        lines.Add("  " + string.Join(chinese ? "；" : "; ", parts)
                  + "  " + Badge(rewardForecast.Accuracy, chinese));
    }

    private static void AppendRouteBreakdownFooter(
        List<List<string>> panels,
        MapNodeForecast forecast,
        int groupCount,
        bool chinese)
    {
        if (groupCount > MaximumDisplayedOutcomeGroups)
        {
            panels[^1].Add(chinese
                ? $"另有 {groupCount - MaximumDisplayedOutcomeGroups} 组不同结果未展开。"
                : $"{groupCount - MaximumDisplayedOutcomeGroups} additional outcome groups are collapsed.");
        }

        if (forecast.RoutesTruncated)
        {
            panels[^1].Add(chinese
                ? "[gold]可行路线超过枚举上限，以上仅为已计算部分。[/gold]"
                : "[gold]The route count exceeded the enumeration limit; only computed routes are shown.[/gold]");
        }

        if (forecast.RouteVariants.Any(variant => variant.HasUnmodeledStateDependency))
        {
            panels[^1].Add(chinese
                ? "[gold]条件预测：沿途状态按当前值。[/gold]"
                : "[gold]Conditional: intervening state remains as currently modeled.[/gold]");
        }
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
                ? $"{lineStyle.ChineseColorName}线路（{completeRouteCount} 条完整路线）"
                : $"{lineStyle.EnglishColorName} line ({completeRouteCount} complete {(completeRouteCount == 1 ? "route" : "routes")})";
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
        else
        {
            lines.Add(chinese
                ? $"? → 见路线条件  {Badge(forecast.Accuracy, true)}"
                : $"? → see route conditions  {Badge(forecast.Accuracy, false)}");
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
            lines.Add(chinese
                ? $"HP：未确定  {Badge(forecast.Accuracy, true)}"
                : $"HP: unresolved  {Badge(forecast.Accuracy, false)}");
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
                lines.Add(chinese
                    ? "[gold]Combat Solver：该节点含多种遭遇结果；请在地图上方战损面板按路线查看。[/gold]"
                    : "[gold]Combat Solver: this node has multiple encounter outcomes; inspect them by route in the forecast panel.[/gold]");
                return;
            case PreCombatDisplayStatus.NotCalculated:
                lines.Add(chinese
                    ? "[gold]Combat Solver：点击地图上方「战损计算」开始。[/gold]"
                    : "[gold]Combat Solver: use Damage Forecast at the top of the map to calculate.[/gold]");
                return;
            case PreCombatDisplayStatus.Pending:
                lines.Add(chinese
                    ? "[gold]Combat Solver：已由手动面板启动；隔离进程不会推进当前跑局 RNG。[/gold]"
                    : "[gold]Combat Solver: started from the manual panel; the isolated worker does not advance live RNG.[/gold]");
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
            ? "[gold]基于当前牌组、遗物和药水；返回结果前已复核主跑局状态未变。[/gold]"
            : "[gold]Uses the current deck, relics, and potions; the live run is revalidated before return.[/gold]");
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
            lines.Add(chinese
                ? $"商店库存：见路线条件  {Badge(forecast.Accuracy, true)}"
                : $"Merchant stock: see route conditions  {Badge(forecast.Accuracy, false)}");
            return;
        }

        var merchant = forecast.Value!;
        var conditional = forecast.Accuracy == ForecastAccuracy.BranchDependent
                          || merchant.FutureVisitOrdinal > 1;
        lines.Add(chinese
            ? conditional
                ? $"若为从当前起第 {merchant.FutureVisitOrdinal} 次进店  {Badge(forecast.Accuracy, true)}"
                : $"商店库存  {Badge(forecast.Accuracy, true)}"
            : conditional
                ? $"If this is merchant visit {merchant.FutureVisitOrdinal} from now  {Badge(forecast.Accuracy, false)}"
                : $"Merchant stock  {Badge(forecast.Accuracy, false)}");
        AppendMerchantContents(lines, merchant, chinese);
        if (conditional)
        {
            lines.Add(chinese
                ? "[gold]条件：沿途奖励、购买、补货和删牌未改变相关状态。[/gold]"
                : "[gold]Condition: intervening rewards, purchases, restocks, and removals do not change the relevant state.[/gold]");
        }
    }

    private static void AppendMerchantContents(
        List<string> lines,
        MerchantInventoryForecast merchant,
        bool chinese,
        string prefix = "")
    {
        AppendItems(lines, prefix + (chinese ? "角色卡" : "Character cards"), merchant.CharacterCards, chinese);
        AppendItems(lines, prefix + (chinese ? "无色卡" : "Colorless cards"), merchant.ColorlessCards, chinese);
        AppendItems(lines, prefix + (chinese ? "遗物" : "Relics"), merchant.Relics, chinese);
        AppendItems(lines, prefix + (chinese ? "药水" : "Potions"), merchant.Potions, chinese);
        lines.Add(chinese
            ? $"{prefix}删牌：{merchant.CardRemovalCost}"
            : $"{prefix}Card removal: {merchant.CardRemovalCost}");
    }

    private static void AppendRewards(
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

    private static void AppendEventContents(
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
        lines.Add(chinese
            ? $"{prefix}事件选项内容  {Badge(forecast.Accuracy, true)}"
            : $"{prefix}Event option contents  {Badge(forecast.Accuracy, false)}");
        foreach (var option in details.Options)
        {
            if (option.InitialItems.Count > 0 && option.Sets.Count == 0)
            {
                lines.Add(chinese
                    ? $"{prefix}  选项「{option.Option}」入场可见：{FormatItems(option.InitialItems)}"
                    : $"{prefix}  Option \"{option.Option}\" shown on entry: {FormatItems(option.InitialItems)}");
                continue;
            }

            if (option.InitialItems.Count == 0 && option.Sets.Count == 1)
            {
                lines.Add(chinese
                    ? $"{prefix}  选项「{option.Option}」结果：{FormatItems(option.Sets[0].Items)}"
                    : $"{prefix}  Option \"{option.Option}\" result: {FormatItems(option.Sets[0].Items)}");
                continue;
            }

            lines.Add(chinese
                ? $"{prefix}  选项「{option.Option}」："
                : $"{prefix}  Option \"{option.Option}\":");
            if (option.InitialItems.Count > 0)
            {
                lines.Add(chinese
                    ? $"{prefix}    入场可见：{FormatItems(option.InitialItems)}"
                    : $"{prefix}    Shown on entry: {FormatItems(option.InitialItems)}");
            }
            for (var index = 0; index < option.Sets.Count; index++)
            {
                lines.Add(chinese
                    ? $"{prefix}    结果第 {index + 1} 组：{FormatItems(option.Sets[index].Items)}"
                    : $"{prefix}    Result set {index + 1}: {FormatItems(option.Sets[index].Items)}");
            }
        }
    }

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
        lines.Add(chinese
            ? $"{prefix}战后掉落：金币 [blue]{rewards.Gold}[/blue]  {Badge(forecast.Accuracy, true)}"
            : $"{prefix}Combat rewards: [blue]{rewards.Gold}[/blue] gold  {Badge(forecast.Accuracy, false)}");
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
        if (treasure.IsEmpty)
        {
            lines.Add(chinese
                ? $"{prefix}宝箱：空  {Badge(forecast.Accuracy, true)}"
                : $"{prefix}Treasure: empty  {Badge(forecast.Accuracy, false)}");
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
        var text = items.Select(item =>
        {
            var sale = item.IsOnSale ? (chinese ? " [半价]" : " [sale]") : string.Empty;
            return $"{ForecastItemFormatter.Format(item.Item)} [blue]{item.Cost}[/blue]{sale}";
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

    private static string RoomName(RoomType roomType, bool chinese) => (roomType, chinese) switch
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
