#if DEBUG
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using SeedOracle.Data;
using SeedOracle.Forecasting;
using SeedOracle.Api;
using SeedOracle.Integration;
using SeedOracle.UI;
using MegaCrit.Sts2.addons.mega_text;

namespace SeedOracle.Validation;

internal static class ForecastExperienceRegressionTests
{
    internal static void Run(RunState live)
    {
        if (System.Environment.GetEnvironmentVariable("SEED_ORACLE_SELF_TEST") != "1")
            return;
        var liveBefore = LiveFingerprint.Capture(live);
        ValidateHoverRequests();
        var planning = new PlanningPredictionService();
        var state = planning.CreateState(live, LocalContext.GetMe(live) ?? live.Players[0]);
        planning.AddRelic(state, ModelDb.Relic<WingedBoots>().Id);
        state.Player.Relics.OfType<WingedBoots>().Single().TimesUsed = 0;
        var map = new DenseMap();
        state.Run.Map = map;
        state.Run.ClearVisitedMapCoordsDebug();
        state.Run.AddVisitedMapCoord(map.StartingMapPoint.coord);
        var timer = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var adapter = new CountingAdapter(Entry.RandomForeseer);
        var service = new MapForecastService(adapter);
        ForecastWorkProfile.Start();
        var forecast = service.Predict(state.Run, map.Target, true);
        var elapsed = timer.ElapsedMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        foreach (var line in ForecastWorkProfile.Stop("cold-hover")) Smoke.SmokeReport.Add(line);
        timer.Restart();
        var tooltip = MapForecastTooltipBuilder.Build(forecast)!;
        var tooltipMs = timer.ElapsedMilliseconds;
        Require(adapter.Merchants == 1, "identical baseline room sequences must generate one native inventory");
        Require(bytes < 200_000_000, "bounded hovering must not allocate nearly a GB");
        Require(forecast.RoutesTruncated && forecast.RouteVariants.All(variant => variant.Merchant is null
            || variant.Merchant.Accuracy != ForecastAccuracy.ExactForCurrentWorldline), "truncated route variants must not display current-worldline certainty");
        timer.Restart();
        Require(ReferenceEquals(forecast, service.Predict(state.Run, map.Target, true)), "unchanged hover must reuse the detached forecast");
        var cachedMs = timer.ElapsedMilliseconds;
        ForecastWorkProfile.Start();
        for (var repeat = 0; repeat < 16; repeat++) service.Predict(state.Run, map.Target, true);
        foreach (var line in ForecastWorkProfile.Stop("warm-hover-16")) Smoke.SmokeReport.Add(line);
        var cacheProbe = new MapForecastService(adapter);
        var first = cacheProbe.Predict(state.Run, map.Target, true);
        foreach (var point in map.GetAllMapPoints().Where(point => !ReferenceEquals(point, map.Target)).Take(12))
            cacheProbe.Predict(state.Run, point, true);
        var retained = ReferenceEquals(first, cacheProbe.Predict(state.Run, map.Target, true));
        Require(retained, "visiting twelve other nodes must not discard the entire hover cache");
        Smoke.SmokeReport.Add($"work-profile cache-window first_retained_after_12_other_nodes={retained}");
        // The cache probe has its own native generations; later checks assert
        // changes relative to the service's current adapter total.
        var merchantsBeforeChange = adapter.Merchants;
        var snapshot = planning.Capture(state);
        ValidateSnapshotCopy(snapshot.Run);
        var paths = RouteStateExplorer.Explore(state.Run, map.Target).Paths.Take(4).ToArray();
        var fixedPredictions = RouteWorldlinePredictor.Predict(state.Run, map.Target, paths);
        var nativePredictions = RouteWorldlinePredictor.Predict(state.Run, map.Target, paths, forceShadow: true);
        Require(fixedPredictions.Zip(nativePredictions).All(pair => pair.First.TargetRoomType == pair.Second.TargetRoomType
                && pair.First.ResolvedRooms.SequenceEqual(pair.Second.ResolvedRooms)
                && pair.First.FutureMerchantVisitOrdinal == pair.Second.FutureMerchantVisitOrdinal),
            "fixed-room shortcut must agree with shadow traversal");
        planning.AddCard(state, state.Player.Deck.Cards[0].Id, false);
        Require(!ReferenceEquals(forecast, service.Predict(state.Run, map.Target, true)), "card pickup must invalidate hover cache");
        state.Player.Relics.OfType<WingedBoots>().Single().TimesUsed = 1;
        Require(adapter.Merchants == merchantsBeforeChange + 1, "state changes must regenerate baseline inventories");
        var afterPickup = service.Predict(state.Run, map.Target, true);
        Require(adapter.Merchants == merchantsBeforeChange + 2 && !ReferenceEquals(afterPickup, forecast), "travel usage must invalidate hover cache");
        var restored = planning.Restore(snapshot);
        var expected = Entry.RandomForeseer.PredictMerchantRoom(restored.Player,
            [RoomType.RestSite, RoomType.RestSite, RoomType.RestSite, RoomType.RestSite, RoomType.RestSite, RoomType.Shop]);
        Require(expected.HasValue && forecast.RouteVariants.Where(variant => variant.Merchant is { HasValue: true })
            .Any(variant => ForecastContentKey.Merchant(variant.Merchant!.Value! with
                { ReferenceCondition = null, ChoiceBranches = [], ChoicesTruncated = false, UnresolvedChoices = [] })
                == ForecastContentKey.Merchant(expected.Value!)), "shared baseline must match the original native inventory");
        ValidateOverview(state.Run);
        var paging = ValidateTextAndPaging(live);
        Require(liveBefore.DescribeDifference(LiveFingerprint.Capture(live)) is null, "experience audit changed live state");
        Smoke.SmokeReport.Add($"experience-regression PASS: routes={forecast.RouteVariants.Count} predict_ms={elapsed} predict_bytes={bytes} cached_ms={cachedMs} baseline_generations=1 tooltip_ms={tooltipMs} pages_checked={paging} native=matched cache_pick_and_boots=invalidated live=unchanged");
    }

    private static void ValidateSnapshotCopy(SerializableRun original)
    {
        var source = PlanningPredictionService.CloneSnapshot(original);
        var sourceMap = source.Acts[source.CurrentActIndex].SavedMap!;
        for (var index = 0; index < sourceMap.Points.Count; index++)
            sourceMap.Points[index].CanBeModified = index % 2 == 0;
        sourceMap.StartingPoint.CanBeModified = false;
        sourceMap.BossPoint.CanBeModified = false;
        sourceMap.SecondBossPoint = new SerializableMapPoint
            { Coord = sourceMap.BossPoint.Coord, PointType = MapPointType.Boss, CanBeModified = false };
        var typeInfo = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        var expected = ReferenceSnapshotCopy(source);
        var copy = PlanningPredictionService.CloneSnapshot(source);
        Require(JsonSerializer.SerializeToUtf8Bytes(expected, typeInfo)
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(copy, typeInfo)),
            "fast snapshot copy must preserve the complete native serialized state");
        var targetMap = copy.Acts[copy.CurrentActIndex].SavedMap!;
        Require(sourceMap.Points.Zip(targetMap.Points).All(pair =>
                pair.First.CanBeModified == pair.Second.CanBeModified && !ReferenceEquals(pair.First, pair.Second))
            && !targetMap.StartingPoint.CanBeModified && !targetMap.BossPoint.CanBeModified
            && !targetMap.SecondBossPoint!.CanBeModified,
            "snapshot copying must preserve true and false flags on regular, start and both boss points");
        foreach (var (sourcePoint, targetPoint) in sourceMap.Points.Zip(targetMap.Points))
            if (sourcePoint.ChildCoords is { } children)
            {
                targetPoint.ChildCoords!.Clear();
                Require(children.Count > 0, "snapshot child lists must remain independent");
            }
        copy.Players[0].Deck.Clear();
        copy.MapPointHistory.Clear();
        Require(JsonSerializer.SerializeToUtf8Bytes(source, typeInfo)
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, typeInfo)),
            "changing a cloned deck, map or history must not mutate the snapshot");

        // Fixed work makes the comparison independent of how many branches fit
        // into a hover's time budget. Keep the old implementation as a DEBUG
        // test oracle, never as a second production serialization path.
        const int iterations = 32;
        static (double Milliseconds, long Bytes) Measure(Func<SerializableRun> clone)
        {
            clone();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (var repeat = 0; repeat < iterations; repeat++) clone();
            return (timer.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        var oldWork = Measure(() => ReferenceSnapshotCopy(source));
        var newWork = Measure(() => PlanningPredictionService.CloneSnapshot(source));
        Smoke.SmokeReport.Add($"work-profile snapshot-copy copies={iterations} old_ms={oldWork.Milliseconds:F2} new_ms={newWork.Milliseconds:F2} old_bytes={oldWork.Bytes} new_bytes={newWork.Bytes}");
        Require(newWork.Bytes < oldWork.Bytes * 0.85,
            "snapshot copying must eliminate the whole-run JSON DOM allocation");
        Smoke.SmokeReport.Add("snapshot-copy PASS: payload=matched map_flags=matched graphs=independent");
    }

    private static void ValidateHoverRequests()
    {
        var pending = new StableHoverRequest<object>(80);
        var owners = Enumerable.Range(0, 20).Select(_ => new object()).ToArray();
        var computations = 0;
        for (var index = 0; index < owners.Length; index++)
        {
            pending.Request(owners[index], index * 16);
            if (pending.TakeReady(index * 16 + 15) is not null) computations++;
        }
        Require(computations == 0 && pending.TakeReady(383) is null
                && ReferenceEquals(pending.TakeReady(384), owners[^1]) && pending.TakeReady(400) is null,
            "rapidly crossing twenty uncached distant nodes must compute only the final stable hover once");
        pending.Request(owners[0], 500);
        pending.Cancel(owners[1]);
        Require(ReferenceEquals(pending.Owner, owners[0]), "leaving an old node must not cancel its replacement");
        pending.Cancel(owners[0]);
        Require(pending.TakeReady(600) is null, "leaving a pending node must cancel its computation");
        pending.Request(owners[0], 700);
        pending.Cancel();
        Require(pending.TakeReady(800) is null, "closing the map must cancel pending computation");
        Smoke.SmokeReport.Add("hover-requests PASS: crossed_nodes=20 stable_predictions=1 canceled_predictions=0 tasks=0 timers=0");
    }

    private static SerializableRun ReferenceSnapshotCopy(SerializableRun source)
    {
        var typeInfo = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        var root = JsonSerializer.SerializeToNode(source, typeInfo)!.AsObject();
        foreach (var act in root["acts"]!.AsArray().OfType<JsonObject>())
        {
            if (act["saved_map"] is not JsonObject map) continue;
            foreach (var point in map["points"]!.AsArray().OfType<JsonObject>()
                         .Concat(new[] { map["boss"], map["start"], map["second_boss"] }.OfType<JsonObject>()))
                if (!point.ContainsKey("can_modify")) point["can_modify"] = false;
        }
        return JsonSerializer.Deserialize(root, typeInfo)!;
    }

    private static void ValidateOverview(RunState run)
    {
        var panel = new RunSeedOverviewControl();
        var container = NGame.Instance?.HoverTipsContainer ?? throw new InvalidOperationException("Hover container unavailable");
        container.AddChild(panel);
        try
        {
            for (var index = 0; index < 20; index++)
                panel.BindRun(run);
            Require(panel.PreparationCount == 0, "hidden ancient overview must not recompute");
            panel.ShowPrepared();
            panel.Visible = false;
            panel.BindRun(run);
            panel.ShowPrepared();
            Require(panel.PreparationCount == 1, "unchanged ancient overview must reuse results");
        }
        finally { panel.Free(); }
    }

    private static int ValidateTextAndPaging(RunState live)
    {
        var item = new ForecastItemDetails(null, "基础", ForecastItemKind.Card);
        static CombatCardRewardGroup Group(ForecastItemDetails card) => new([new([new(card)], [])]);
        var rewards = new CombatRewardDetails(10, [Group(item)], [], [])
        {
            Possibilities = Enumerable.Range(0, 40).Select(index => new CombatRewardPossibility(index / 10,
                [0], [Group(item with { Name = $"已算结果{index:D2}" })])).ToArray(),
            HuntPreviewLimit = 3,
            PossibilitiesTruncated = true
        };
        var lines = new List<string>();
        var rewardForecast = Forecast<CombatRewardDetails>.Branch(rewards, PredictionDependency.Rewards, "fixture");
        MapForecastTooltipBuilder.AppendRewards(lines,
            new([], RoomType.Monster, null, null, null, true, rewardForecast), true);
        var text = string.Join("\n", lines);
        Require(Enumerable.Range(0, 40).All(index => text.Contains($"已算结果{index:D2}"))
                && text.Contains("狩猎触发 3 次") && text.Contains("已算40种"),
            "all computed rerolls and Hunt branches must remain readable, with the actual limit shown");
        var empty = new List<string>();
        MapForecastTooltipBuilder.AppendEventContents(empty,
            Forecast<EventContentDetails>.Exact(new([new("空选项", "empty", [], [])])), true, "");
        Require(empty.Count == 0, "empty event predictions must not emit headings");
        var node = new MapNodeForecast(new MapPoint(0, 1) { PointType = MapPointType.Monster }, 1, 1,
            null, null, null, null, null, [new([], RoomType.Monster, null, null, null, true, rewardForecast)], false);
        var plain = MapForecastTooltipBuilder.Build(node)!;
        var deferred = MapForecastTooltipBuilder.Build(node, new(PreCombatDisplayStatus.Deferred))!;
        Require(plain.Descriptions.SequenceEqual(deferred.Descriptions), "uncomputed combat estimates must not add boilerplate");
        var sourceCard = ForecastItemDetails.Card((LocalContext.GetMe(live) ?? live.Players[0]).Deck.Cards[0]);
        var large = rewards with
        {
            Possibilities = Enumerable.Range(0, 64).Select(index => new CombatRewardPossibility(index / 16, [0, 1],
                Enumerable.Range(0, 3).Select(_ => new CombatCardRewardGroup([new(
                    Enumerable.Range(0, 3).Select(slot => new RewardItemDetails(sourceCard with
                        { Name = $"结果{index}-{slot}" })).ToArray(), [])])).ToArray())).ToArray()
        };
        var shared = new RouteVariantForecast([], RoomType.Monster, null, null, null, true,
            Forecast<CombatRewardDetails>.Branch(large, PredictionDependency.Rewards, "fixture"));
        var keyBytes = GC.GetAllocatedBytesForCurrentThread();
        var keyTimer = Stopwatch.StartNew();
        var grouped = RouteMarkerPlanner.Build(node with { RouteVariants = Enumerable.Repeat(shared, 512).ToArray() });
        keyBytes = GC.GetAllocatedBytesForCurrentThread() - keyBytes;
        Require(grouped.Groups.Count == 1 && keyBytes < 10_000_000,
            "shared reward state must not be serialized hundreds of times while grouping routes");
        Smoke.SmokeReport.Add($"experience-grouping PASS: routes=512 possibilities=64 groups=3 cards=3 elapsed_ms={keyTimer.ElapsedMilliseconds} allocated_bytes={keyBytes}");
        const string shortText = "遭遇：一些噬尸蛞蝓  [green]✓ 确定[/green]\n怪物：噬尸蛞蝓 + 噬尸蛞蝓\nHP：噬尸蛞蝓 29 / 噬尸蛞蝓 27\n胜利掉落：15金币  [gold]~ 条件预测[/gold]\n卡牌：双重打击 / 狱火 / 愤怒\n药水：异鱼之油";
        ResourceSaver.Save(PreloadManager.Cache.GetScene("res://scenes/ui/hover_tip.tscn"), "user://oracle-hover-layout.tscn");
        var shortPages = ForecastTooltipPaging.CreatePages([shortText], 720);
        Require(shortPages.Count == 1 && shortPages[0] == shortText,
            $"ordinary forecast paragraphs must not gain hard line breaks: pages={shortPages.Count}");
        var shortPanel = PreloadManager.Cache.GetScene("res://scenes/ui/hover_tip.tscn").Instantiate<Control>();
        NGame.Instance!.HoverTipsContainer!.AddChild(shortPanel);
        var renderedLines = 0;
        try
        {
            ForecastTooltipLayout.ConfigurePanel(shortPanel, ForecastTooltipLayout.PreferredWidth);
            shortPanel.GetNode<MegaLabel>("%Title").SetTextAutoSize("Seed Oracle · 节点前瞻");
            var body = shortPanel.GetNode<MegaRichTextLabel>("%Description");
            body.Text = shortText;
            shortPanel.ResetSize();
            renderedLines = body.GetLineCount();
            Require(renderedLines <= 8 && shortPanel.Size.X <= ForecastTooltipLayout.PreferredWidth,
                $"compact native panel must fit its width: lines={renderedLines} size={shortPanel.Size} body={body.Size}");
        }
        finally { shortPanel.Free(); }
        foreach (var (name, example) in new[]
        {
            ("combat", ForecastConditionRegressionTests.CombatExample),
            ("merchant", ForecastConditionRegressionTests.MerchantExample)
        })
        {
            Require(example is not null, "full screenshot-equivalent tooltip fixture was not built");
            var actualPages = ForecastTooltipPaging.CreatePages(example!.Descriptions, 1080);
            Require(actualPages.Count == 1, $"{name} screenshot-equivalent tooltip still needs {actualPages.Count} pages");
            using var layout = new ForecastTooltipLayout(1920);
            var measured = layout.Measure(actualPages[0]);
            Require(measured < 1032, $"{name} compact tooltip exceeds the native viewport: {measured}");
            Smoke.SmokeReport.Add($"condition-tooltip-layout PASS: kind={name} pages={actualPages.Count} native_height={measured} chars={actualPages[0].Length}");
        }
        var layoutTimer = Stopwatch.StartNew();
        var totalPages = 0;
        foreach (var height in new[] { 720f, 1080f })
        foreach (var description in new[] { text, "[gold]" + new string('字', 1100) + "[/gold]",
                     string.Join("\n", Enumerable.Range(0, 80).Select(index => $"[color=#64FFFF]分支{index:D2}[/color]：卡牌A → 卡牌B")),
                     string.Join("\n", Enumerable.Range(0, 12).Select(_ =>
                         string.Join(" / ", Enumerable.Repeat(ForecastItemFormatter.Format(sourceCard, true), 6)))) })
        {
            var pages = ForecastTooltipPaging.CreatePages([description], height);
            Require(pages.Count > 1 && VisibleText(string.Concat(pages)) == VisibleText(description),
                "pagination must preserve every computed character and rich-text value");
            foreach (var page in pages)
            {
                var panel = PreloadManager.Cache.GetScene("res://scenes/ui/hover_tip.tscn").Instantiate<Control>();
                var container = NGame.Instance?.HoverTipsContainer ?? throw new InvalidOperationException("Hover container unavailable");
                container.AddChild(panel);
                try
                {
                    var body = panel.GetNode<MegaRichTextLabel>("%Description");
                    ForecastTooltipLayout.ConfigurePanel(panel, ForecastTooltipLayout.PreferredWidth);
                    panel.GetNode<MegaLabel>("%Title").SetTextAutoSize("Seed Oracle · 节点前瞻 · 1/99");
                    body.Text = "Shift+滚轮翻页\n" + page;
                    panel.ResetSize();
                    var measured = Math.Max(panel.GetCombinedMinimumSize().Y, body.GetContentHeight() + 90f);
                    Require(measured < height - 24f,
                        $"native tooltip is still taller than viewport: {measured}/{height} body={body.Size} font={body.GetThemeFontSize("normal_font_size")} content={body.GetContentHeight()} min={panel.GetCombinedMinimumSize()} chars={page.Length}");
                }
                finally { panel.Free(); }
            }
            totalPages += pages.Count;
        }
        Smoke.SmokeReport.Add($"tooltip-layout PASS: short_logical_lines={shortPages[0].Split('\n').Length} short_rendered_lines={renderedLines} native_pages={totalPages} width={ForecastTooltipLayout.PreferredWidth} elapsed_ms={layoutTimer.ElapsedMilliseconds}");
        var owner = NMapScreen.Instance!._mapPointDictionary.Values.First(node => node.State != MapPointState.Traveled);
        ForecastTooltipPaging.Show(owner, plain);
        var previousSet = MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet._activeHoverTips[owner];
        var wheel = new InputEventMouseButton
            { ButtonIndex = MouseButton.WheelDown, Pressed = true, ShiftPressed = true };
        ValidateScrollLock(owner._screen, owner, wheel);
        Require(ReferenceEquals(previousSet, MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet._activeHoverTips[owner]),
            "paging must reuse the native tooltip set");
        MapHoverTipPresentation.HideTip(owner);
        ForecastTooltipPaging.Clear();
        return totalPages;

        static string VisibleText(string value) => Regex.Replace(Regex.Replace(value, @"\[[^\]]*\]", ""), @"\s", "");
    }

    private static void ValidateScrollLock(NMapScreen screen, NMapPoint owner, InputEventMouseButton wheel)
    {
        var enabled = SeedOracleData.Settings.ShowMapForecastsOutsidePlan;
        var disabled = screen._isInputDisabled;
        var tween = screen._actAnimTween;
        var position = screen._mapContainer.Position;
        var target = screen._targetDragPos;
        var dragging = screen._isDragging;
        try
        {
            SeedOracleData.Settings.ShowMapForecastsOutsidePlan = true;
            screen._isInputDisabled = false;
            screen._actAnimTween = null;
            screen._targetDragPos = position;
            using var ordinary = new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true };
            screen.ProcessScrollEvent(ordinary);
            Require(screen._targetDragPos != position, "ordinary wheel must still scroll the native map");
            screen.ProcessScrollEvent(wheel);
            Require(screen._targetDragPos == position && screen._mapContainer.Position == position,
                "Shift wheel must cancel pending native map scrolling");
            Require(ForecastTooltipPaging.PageIndex == 1 && ForecastTooltipPaging.HandleInput(owner, wheel)
                && ForecastTooltipPaging.PageIndex == 1, "map/node dispatch must advance each wheel event exactly once");
            using var shift = new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, Pressed = true, ShiftPressed = true };
            Input.ParseInputEvent(shift);
            Input.FlushBufferedEvents();
            Require(MapForecastScrollLock.ShiftHeld,
                $"synthetic Shift must reach input state: logical={Input.IsKeyPressed(Key.Shift)} physical={Input.IsPhysicalKeyPressed(Key.Shift)}");
            screen._targetDragPos = position + new Vector2(0, 100);
            screen._isDragging = true;
            screen.UpdateScrollPosition(0.05);
            Require(!screen.CanScroll() && !screen._isDragging && screen._targetDragPos == position
                && screen._mapContainer.Position == position, "holding Shift must freeze map drag and interpolation");
            using var release = new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, Pressed = false };
            Input.ParseInputEvent(release);
            Input.FlushBufferedEvents();
            screen.ProcessScrollEvent(ordinary);
            Require(screen.CanScroll() && screen._targetDragPos != position, "releasing Shift must restore native scrolling");
            using var up = new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, ShiftPressed = true };
            screen._Input(up);
            Require(ForecastTooltipPaging.PageIndex == 0 && screen._targetDragPos == position,
                "map-wide Shift wheel entry must page while locking the map");
            Smoke.SmokeReport.Add("map-scroll-lock PASS: native_wheel=scroll Shift_wheel=frozen dispatch=once held_Shift=drag_and_inertia_frozen release=restored map_input=paged");
        }
        finally
        {
            using var release = new InputEventKey { Keycode = Key.Shift, PhysicalKeycode = Key.Shift, Pressed = false };
            Input.ParseInputEvent(release);
            Input.FlushBufferedEvents();
            SeedOracleData.Settings.ShowMapForecastsOutsidePlan = enabled;
            screen._isInputDisabled = disabled;
            screen._actAnimTween = tween;
            screen._mapContainer.Position = position;
            screen._targetDragPos = target;
            screen._isDragging = dragging;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Experience regression: " + message);
    }

    private sealed class CountingAdapter(IRandomForeseerAdapter inner) : IRandomForeseerAdapter
    {
        internal int Merchants { get; private set; }
        public IntegrationStatus Status => inner.Status;
        public Forecast<MerchantInventoryForecast> PredictInitialMerchant(Player player) => inner.PredictInitialMerchant(player);
        public Forecast<MerchantInventoryForecast> PredictMerchant(Player player, int ordinal) => inner.PredictMerchant(player, ordinal);
        public Forecast<MerchantInventoryForecast> PredictMerchantRoom(Player player, IReadOnlyList<RoomType> rooms)
        {
            Merchants++;
            return inner.PredictMerchantRoom(player, rooms);
        }
        public Forecast<CombatRewardDetails> PredictCombatRewards(Player player, IReadOnlyList<RoomType> rooms, EncounterModel encounter) => inner.PredictCombatRewards(player, rooms, encounter);
        public Forecast<TreasureRoomDetails> PredictTreasureRoom(Player player, IReadOnlyList<RoomType> rooms) => inner.PredictTreasureRoom(player, rooms);
        public Forecast<EventContentDetails>? PredictEventContents(Player player, IReadOnlyList<RoomType> rooms, EventModel model) => inner.PredictEventContents(player, rooms, model);
    }

    private sealed class DenseMap : ActMap
    {
        protected override MapPoint?[,] Grid { get; } = new MapPoint[4, 7];
        public override MapPoint StartingMapPoint { get; } = new(0, 0) { PointType = MapPointType.Ancient };
        public override MapPoint BossMapPoint { get; } = new(1, 7) { PointType = MapPointType.Boss };
        internal MapPoint Target => Grid[0, 6]!;

        internal DenseMap()
        {
            for (var row = 1; row < 7; row++)
            for (var column = 0; column < 4; column++)
                Grid[column, row] = new MapPoint(column, row)
                    { PointType = row == 6 ? MapPointType.Shop : MapPointType.RestSite };
            foreach (var point in GetPointsInRow(1))
                StartingMapPoint.AddChildPoint(point);
            foreach (var point in GetAllMapPoints())
                foreach (var child in point.coord.row == 6 ? new[] { BossMapPoint } : GetPointsInRow(point.coord.row + 1))
                    point.AddChildPoint(child);
        }
    }
}
#endif
