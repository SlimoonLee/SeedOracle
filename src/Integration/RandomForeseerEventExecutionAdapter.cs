using MegaCrit.Sts2.Core.Commands;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.Integration;

/// <summary>
/// Executes event options on a SERIALIZED SHADOW RUN for the route planner.
/// Envelope (from docs/rng-audit-event-execution.md): the shadow player's
/// IsMe/IsMine gates are closed by ShadowIsolationPatch while its original
/// NetId remains available for history lookups; NonInteractiveMode mutes
/// audio; card-select prompts are answered by a scripted ICardSelector
/// instead of the blocking modal; everything is wrapped by the caller in
/// PredictionPurityGuard.
/// </summary>
internal sealed partial class RandomForeseerAdapter
{
    internal enum EventPlanningKind
    {
        Exact,
        RewardChoice,
        CardOrUiChoice,
        SpecialCombat,
        Minigame,
        RunEnding
    }

    internal sealed record EventPlanningCapability(EventPlanningKind Kind, string ReasonKey);

    internal sealed record EventOptionDescriptor(
        int Index,
        string Title,
        string TextKey,
        bool IsLocked,
        EventPlanningCapability Capability);

    /// <summary>Events whose Chosen() cannot run headless at all.</summary>
    private static readonly HashSet<string> EventExecutionDenyList = new(StringComparer.Ordinal)
    {
        "CrystalSphere",     // minigame screen + network messages (has its own layout viewer)
        "Amalgamator",       // option effects await frame/UI signals: sync-block deadlocks
    };

    /// <summary>Option-level denies: options that open reward/card-selection
    /// screens or kill-confirmation popups; TextKey substring match.</summary>
    private static readonly (string Event, string Key, EventPlanningKind Kind)[] OptionDenyList =
    [
        // These options enter RewardsCmd.OfferCustom or a native card grid.
        // The serialized shadow has no multiplayer reward state or UI modal;
        // keep the boundary explicit instead of letting the native path throw.
        ("DenseVegetation", "INITIAL.options.REST", EventPlanningKind.RewardChoice),
        ("Wellspring", "INITIAL.options.BOTTLE", EventPlanningKind.RewardChoice),
        ("DrowningBeacon", "INITIAL.options.BOTTLE", EventPlanningKind.RewardChoice),
        ("ColorfulPhilosophers", "INITIAL.options.", EventPlanningKind.RewardChoice),
        ("PotionCourier", "INITIAL.options.", EventPlanningKind.RewardChoice),
        ("BrainLeech", "INITIAL.options.SHARE_KNOWLEDGE", EventPlanningKind.RewardChoice),
        ("TheLegendsWereTrue", "INITIAL.options.SLOWLY_FIND_AN_EXIT", EventPlanningKind.RewardChoice),
        ("WhisperingHollow", "GOLD", EventPlanningKind.RewardChoice),
        ("WarHistorianRepy", "UNLOCK_CHEST", EventPlanningKind.RewardChoice),

        // Native card grids or other UI-mediated choices need another plan
        // decision before their result can be threaded downstream.
        ("RoomFullOfCheese", "INITIAL.options.GORGE", EventPlanningKind.CardOrUiChoice),
        ("BrainLeech", "RIP", EventPlanningKind.CardOrUiChoice),

        ("Trial", "DOUBLE_DOWN", EventPlanningKind.RunEnding),
    ];

    internal sealed class EventExecutionOutcome
    {
        public bool Ok { get; set; }
        public string? DenyReason { get; set; }
        public int GoldDelta { get; set; }
        public int HpDelta { get; set; }
        public List<string> CardsGained { get; } = [];
        public List<string> CardsLost { get; } = [];
        public List<string> RelicsGained { get; } = [];
        public List<string> PotionsGained { get; } = [];
        /// <summary>Secondary options revealed by executing this choice.</summary>
        public List<(string TextKey, string Option)> NextOptions { get; } = [];
        public bool Finished { get; set; }
        public bool OptionOutOfRange { get; set; }
        public RunState? ShadowRun { get; set; }
        public Player? ShadowPlayer { get; set; }
        public EventModel? ShadowEvent { get; set; }
    }

    internal sealed record PlayerSnapshot(
        int Gold,
        int Hp,
        List<string> Deck,
        List<string> Relics,
        List<string> Potions);

    internal bool IsEventExecutionDenied(string entryName, IReadOnlyList<EventOption> options, int optionIndex)
    {
        if (optionIndex < 0 || optionIndex >= options.Count)
            return true;
        return GetEventPlanningCapability(entryName, options[optionIndex]).Kind != EventPlanningKind.Exact;
    }

    internal EventPlanningCapability GetEventPlanningCapability(string entryName, EventOption option)
    {
        if (entryName.Contains("CrystalSphere", StringComparison.Ordinal))
            return new EventPlanningCapability(EventPlanningKind.Minigame, "crystal_sphere");
        if (entryName.Contains("Amalgamator", StringComparison.Ordinal))
            return new EventPlanningCapability(EventPlanningKind.CardOrUiChoice, "awaits_ui_frames");

        foreach (var (denyEvent, denyKey, kind) in OptionDenyList)
        {
            if (entryName.Contains(denyEvent, StringComparison.Ordinal)
                && option.TextKey.Contains(denyKey, StringComparison.OrdinalIgnoreCase))
            {
                return new EventPlanningCapability(kind, kind switch
                {
                    EventPlanningKind.RewardChoice => "reward_choice",
                    EventPlanningKind.CardOrUiChoice => "card_or_ui_choice",
                    EventPlanningKind.SpecialCombat => "event_combat",
                    EventPlanningKind.RunEnding => "run_ending",
                    _ => "unsupported"
                });
            }
        }

        return new EventPlanningCapability(EventPlanningKind.Exact, "exact");
    }

    /// <summary>
    /// Fire-and-forget execution of one option on a fresh shadow run. Never
    /// touches the live run; completion is delivered through the returned
    /// task (post it back to the main thread via SeedOracleDispatcher).
    /// </summary>
    internal async Task<EventExecutionOutcome> ExecuteEventOptionAsync(
        Player livePlayer,
        EventModel canonicalEvent,
        int optionIndex,
        ModelId? plannedCardPick,
        IReadOnlyList<RoutePlanForecastService.ProjectedCard>? projectedDeck = null,
        SerializableRun? plannedRunSnapshot = null,
        ulong? plannedPlayerNetId = null)
    {
        var outcome = new EventExecutionOutcome();
        var entryName = canonicalEvent.GetType().Name;
        if (EventExecutionDenyList.Contains(entryName))
        {
            outcome.DenyReason = "该事件无法无头预演";
            return outcome;
        }

        var previousCheck = NonInteractiveMode.AutoSlayerCheck;
        NonInteractiveMode.AutoSlayerCheck = () => true;
        try
        {
            var snapshot = plannedRunSnapshot ?? RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = PlanningPredictionService.RestoreRun(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(plannedPlayerNetId ?? livePlayer.NetId)
                               ?? throw new InvalidOperationException(
                                   $"shadow snapshot lacks player {livePlayer.NetId}");
            EnterShadowIsolation(shadowPlayer);

            if (projectedDeck is { Count: > 0 })
            {
                // Thread the plan's projected deck into the shadow player so
                // the event's own card lists (removal pools, upgrade pools,
                // selection grids) reflect planned pickups, removals, and
                // upgrades instead of the live deck.
                var playerSave = shadowPlayer.ToSerializable();
                playerSave.Deck = projectedDeck
                    .Select(projected =>
                    {
                        var model = ModelDb.AllCards.FirstOrDefault(candidate =>
                            candidate.Id.Entry == projected.Id.Entry);
                        if (model is null)
                            return null;
                        var card = model.ToMutable();
                        if (projected.Upgraded && !card.IsUpgraded)
                            CardCmd.Upgrade(card);
                        return (CardModel?)card;
                    })
                    .OfType<CardModel>()
                    .Select(card => card.ToSerializable())
                    .ToList();
                shadowPlayer.SyncWithSerializedPlayer(playerSave);
            }

            // RF event hooks can consult their per-run prediction state while
            // initial options are generated. Keep the context alive through
            // the option execution as well as initialization.
            var routeState = _contextConstructor!.Invoke([shadowPlayer]);
            InitShadowEventOn(shadowRun, shadowPlayer, canonicalEvent, out var shadowEvent);
            GC.KeepAlive(routeState);

            var options = shadowEvent.CurrentOptions;
            if (optionIndex >= options.Count)
            {
                outcome.OptionOutOfRange = true;
                return outcome;
            }
            var capability = GetEventPlanningCapability(entryName, options[optionIndex]);
            if (capability.Kind != EventPlanningKind.Exact)
            {
                outcome.DenyReason = capability.Kind switch
                {
                    EventPlanningKind.RewardChoice => "该选项还需要指定奖励取舍，暂不推进后续世界线",
                    EventPlanningKind.CardOrUiChoice => "该选项还需要指定卡牌或界面选择，暂不推进后续世界线",
                    EventPlanningKind.SpecialCombat => "该选项会进入特殊战斗，请改用胜利掉落/战损估算",
                    EventPlanningKind.Minigame => "该选项会进入小游戏，请改用专用布局预测",
                    EventPlanningKind.RunEnding => "该选项会终止当前跑局",
                    _ => "该选项暂时无法无头预演"
                };
                return outcome;
            }
            var before = Capture(shadowPlayer);

            using (CardSelectCmd.PushSelector(new ScriptedCardSelector(plannedCardPick)))
            {
                await options[optionIndex].Chosen();
            }
            GC.KeepAlive(routeState);

            var after = Capture(shadowPlayer);
            FillDeltas(outcome, before, after);
            outcome.Finished = shadowEvent.IsFinished;
            foreach (var option in shadowEvent.CurrentOptions)
                outcome.NextOptions.Add((option.TextKey, option.Title.GetFormattedText()));
            outcome.Ok = true;
            outcome.ShadowRun = shadowRun;
            outcome.ShadowPlayer = shadowPlayer;
            outcome.ShadowEvent = shadowEvent;
            return outcome;
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            outcome.DenyReason = $"无头执行失败，已降级：{root.Message}";
            Entry.Logger.Error($"[Smoke] exec {entryName}[{optionIndex}] failed: {exception}");
            return outcome;
        }
        finally
        {
            NonInteractiveMode.AutoSlayerCheck = previousCheck;
            ExitShadowIsolation();
        }
    }

    /// <summary>
    /// While a shadow execution/layout is in flight, every
    /// LocalContext.IsMe/IsMine gate reports false for the SHADOW objects:
    /// profile writes, VFX, and other local-player branches stay closed. The
    /// shadow player keeps its ORIGINAL NetId, so run-history lookups inside
    /// effect code still resolve (they index PlayerStats by NetId).
    /// </summary>
    // Kept as a compatibility alias for the RF smoke/audit path. Planning
    // predictions use ShadowIsolation directly and do not depend on RF.
    internal static Player? ActiveShadowPlayer
    {
        get => ShadowIsolation.ActiveShadowPlayer;
        private set
        {
            if (value is null)
                ShadowIsolation.Exit();
            else
                ShadowIsolation.Enter(value);
        }
    }

    private static void EnterShadowIsolation(Player shadowPlayer)
    {
        ActiveShadowPlayer = shadowPlayer;
    }

    private static void ExitShadowIsolation()
    {
        ActiveShadowPlayer = null;
    }
    private static PlayerSnapshot Capture(Player player)
    {
        return new PlayerSnapshot(
            player.Gold,
            player.Creature.CurrentHp,
            player.Deck.Cards
                .Select(card => $"{card.Title}{(card.IsUpgraded ? "+" : string.Empty)}")
                .ToList(),
            player.Relics.Select(relic => relic.Title.GetFormattedText()).ToList(),
            player.Potions.Select(potion => potion.Title.GetFormattedText()).ToList());
    }

    private static void FillDeltas(
        EventExecutionOutcome outcome,
        PlayerSnapshot before,
        PlayerSnapshot after)
    {
        outcome.GoldDelta = after.Gold - before.Gold;
        outcome.HpDelta = after.Hp - before.Hp;
        AddMissing(outcome.CardsGained, after.Deck, before.Deck);
        AddMissing(outcome.CardsLost, before.Deck, after.Deck);
        AddMissing(outcome.RelicsGained, after.Relics, before.Relics);
        AddMissing(outcome.PotionsGained, after.Potions, before.Potions);
    }

    private static void AddMissing(List<string> target, IReadOnlyList<string> source, IReadOnlyList<string> removed)
    {
        var remaining = removed.ToList();
        foreach (var item in source)
        {
            if (!remaining.Remove(item))
                target.Add(item);
        }
    }

    /// <summary>
    /// Crystal sphere "omniscient layout": the minigame constructor generates
    /// the entire 11x11 grid and all 15 items from the event-local RNG alone,
    /// so a shadow construction predicts every cell without opening the
    /// screen or consuming any live randomness.
    /// </summary>
    internal sealed class CrystalSphereLayout
    {
        public int DivinationCount { get; init; }
        public int Cost { get; init; }
        public List<string> Rows { get; init; } = [];
        public List<string> Legend { get; init; } = [];
        public string? Error { get; init; }
    }

    /// <summary>
    /// Enumerates an event's initial options (index + localized title) through
    /// the same shadow initialization the executor uses — for events outside
    /// Random Foreseer's registry whose options are deterministic.
    /// </summary>
    internal IReadOnlyList<EventOptionDescriptor> EnumerateEventOptions(
        Player livePlayer,
        EventModel canonical)
    {
        try
        {
            _ = InitializeShadowEvent(livePlayer, canonical, out var shadowEvent);
            return shadowEvent.CurrentOptions
                .Select((option, index) => new EventOptionDescriptor(
                    index,
                    option.Title.GetFormattedText(),
                    option.TextKey,
                    option.IsLocked,
                    GetEventPlanningCapability(canonical.GetType().Name, option)))
                .ToList();
        }
        catch (Exception exception)
        {
            Entry.Logger.Error($"[Smoke] option enumeration failed: {exception.Message}");
            return [];
        }
        finally
        {
            ExitShadowIsolation();
        }
    }

    /// <summary>
    /// Shared shadow-event initialization: snapshot, shadow player with IsMe
    /// isolation (real NetId preserved for history lookups), mutable clone,
    /// deterministic event RNG, CalculateVars, initial options.
    /// </summary>
    private RunState InitializeShadowEvent(
        Player livePlayer,
        EventModel canonical,
        out EventModel shadowEvent)
    {
        var (shadowRun, shadowPlayer) = PrepareShadowRun(livePlayer);
        ActiveShadowPlayer = shadowPlayer;
        var routeState = _contextConstructor!.Invoke([shadowPlayer]);
        InitShadowEventOn(shadowRun, shadowPlayer, canonical, out shadowEvent);
        GC.KeepAlive(routeState);
        return shadowRun;
    }

    private static (RunState ShadowRun, Player ShadowPlayer) PrepareShadowRun(Player livePlayer)
    {
        ActiveShadowPlayer = null;
        var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
        var shadowRun = PlanningPredictionService.RestoreRun(snapshot);
        var shadowPlayer = shadowRun.GetPlayer(livePlayer.NetId)
                           ?? throw new InvalidOperationException("shadow snapshot lacks player");
        return (shadowRun, shadowPlayer);
    }

    private void InitShadowEventOn(
        RunState shadowRun,
        Player shadowPlayer,
        EventModel canonical,
        out EventModel shadowEvent)
    {
        _ = shadowRun;

        shadowEvent = canonical.ToMutable();
        shadowEvent.Owner = shadowPlayer;
        var playerSlot = shadowEvent.IsShared
            ? 0
            : shadowPlayer.RunState.GetPlayerSlotIndex(shadowPlayer);
        shadowEvent.Rng = new MegaCrit.Sts2.Core.Random.Rng(
            (ulong)((long)shadowPlayer.RunState.Rng.Seed + playerSlot)
            + StringHelper.GetDeterministicHashCode(shadowEvent.Id.Entry));
        shadowEvent.CalculateVars();
        _ = GenerateAndSetInitialOptions(shadowEvent, "shadow-execution");
    }

    internal CrystalSphereLayout PredictCrystalSphereLayout(
        Player livePlayer,
        EventModel canonical,
        int divinationCount)
    {
        try
        {
            var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = PlanningPredictionService.RestoreRun(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(livePlayer.NetId)
                               ?? throw new InvalidOperationException("shadow snapshot lacks player");
            EnterShadowIsolation(shadowPlayer);

            var shadowEvent = canonical.ToMutable();
            shadowEvent.Owner = shadowPlayer;
            var slot = shadowEvent.IsShared
                ? 0
                : shadowPlayer.RunState.GetPlayerSlotIndex(shadowPlayer);
            shadowEvent.Rng = new MegaCrit.Sts2.Core.Random.Rng(
                (ulong)((long)shadowPlayer.RunState.Rng.Seed + slot)
                + StringHelper.GetDeterministicHashCode(shadowEvent.Id.Entry));
            shadowEvent.CalculateVars();

            var cost = -1;
            try
            {
                var entry = shadowEvent.DynamicVars["UncoverFutureCost"];
                cost = (int)entry.BaseValue;
            }
            catch
            {
                // cost display is best-effort only
            }

            var minigame = new MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame(
                shadowPlayer, shadowEvent.Rng, divinationCount);

            var glyphs = new string[11, 11];
            for (var x = 0; x < 11; x++)
            {
                for (var y = 0; y < 11; y++)
                {
                    glyphs[x, y] = "·";
                }
            }

            var legend = new List<string>();
            foreach (var item in minigame.Items)
            {
                var (glyph, color, label) = item switch
                {
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSphereRelic
                        => ("遗", "#FFDA36", "遗物"),
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSphereCardReward
                        => ("卡", "#5CB8FF", "卡牌奖励"),
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSpherePotion
                        => ("药", "#FF61C7", "药水"),
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSphereCurse
                        => ("咒", "#E669FF", "诅咒"),
                    MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems.CrystalSphereGold
                        => ("金", "#FFA629", "金币"),
                    _ => ("?", "#FFFFFF", item.GetType().Name)
                };
                legend.Add($"[color={color}]{glyph}[/color]={label}({item.Size.X}x{item.Size.Y})");
                var position = item.Position;
                for (var dx = 0; dx < item.Size.X; dx++)
                {
                    for (var dy = 0; dy < item.Size.Y; dy++)
                    {
                        var cx = position.X + dx;
                        var cy = position.Y + dy;
                        if (cx is >= 0 and < 11 && cy is >= 0 and < 11)
                        {
                            glyphs[cx, cy] = $"[color={color}]{glyph}[/color]";
                        }
                    }
                }
            }

            var rows = new List<string>();
            for (var y = 0; y < 11; y++)
            {
                var row = string.Empty;
                for (var x = 0; x < 11; x++)
                {
                    row += glyphs[x, y];
                }

                rows.Add(row);
            }

            return new CrystalSphereLayout
            {
                DivinationCount = divinationCount,
                Cost = cost,
                Rows = rows,
                Legend = legend
            };
        }
        catch (Exception exception)
        {
            var root = exception.GetBaseException();
            return new CrystalSphereLayout
            {
                DivinationCount = divinationCount,
                Error = root.Message
            };
        }
        finally
        {
            ExitShadowIsolation();
        }
    }

    /// <summary>
    /// Answers card-select prompts without opening the blocking modal: the
    /// planned card wins; otherwise the first allowed card (options never
    /// reach the game's screen pipeline at all).
    /// </summary>
    private sealed class ScriptedCardSelector(ModelId? plannedCard) : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options,
            int minSelect,
            int maxSelect)
        {
            var list = options.ToList();
            if (list.Count == 0)
                return Task.FromResult((IEnumerable<CardModel>)Array.Empty<CardModel>());

            if (plannedCard is { } want)
            {
                var exact = list.FirstOrDefault(card => card.Id == want)
                            ?? list.FirstOrDefault(card => card.Id.Entry == want.Entry);
                if (exact is not null)
                    return Task.FromResult((IEnumerable<CardModel>)new[] { exact });
            }

            var count = Math.Min(Math.Max(minSelect, 1), list.Count);
            return Task.FromResult((IEnumerable<CardModel>)list.Take(count));
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0)
                return default;

            CardModel? pick = null;
            if (plannedCard is { } want)
            {
                pick = options.FirstOrDefault(option => option.Card.Id == want)?.Card
                       ?? options.FirstOrDefault(option => option.Card.Id.Entry == want.Entry)?.Card;
            }

            return new CardRewardSelection
            {
                card = pick ?? options[0].Card,
                alternative = null
            };
        }
    }
}
