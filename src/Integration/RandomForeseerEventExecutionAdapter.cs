using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using SeedOracle.Api;
using SeedOracle.Forecasting;

namespace SeedOracle.Integration;

/// <summary>
/// Executes event options on a SERIALIZED SHADOW RUN for the route planner.
/// Envelope (from docs/rng-audit-event-execution.md): the shadow player's
/// NetId is rewritten so every IsMe/IsMine gate (profile writes, VFX) stays
/// closed; NonInteractiveMode mutes audio; card-select prompts are answered
/// by a scripted ICardSelector instead of the blocking modal; everything is
/// wrapped by the caller in PredictionPurityGuard.
/// </summary>
internal sealed partial class RandomForeseerAdapter
{
    /// <summary>Events whose Chosen() cannot run headless at all.</summary>
    private static readonly HashSet<string> EventExecutionDenyList = new(StringComparer.Ordinal)
    {
        "CrystalSphere",     // minigame screen + network messages (has its own layout viewer)
        "BattlewornDummy",   // EnterCombatWithoutExitingEvent: needs synchronizer + room push
        "PunchOff",
        "Amalgamator",       // option effects await frame/UI signals: sync-block deadlocks
    };

    /// <summary>Option-level denies: options that open reward screens or
    /// kill-confirmation popups; TextKey substring match.</summary>
    private static readonly (string Event, string Key)[] OptionDenyList =
    [
        ("BrainLeech", "RIP"),
        ("DenseVegetation", "FIGHT"),
        ("WhisperingHollow", "GOLD"),
        ("Trial", "REJECT"),
        ("Trial", "DOUBLE_DOWN"),
        ("WarHistorianRepy", "UNLOCK"),
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
        if (EventExecutionDenyList.Contains(entryName))
            return true;
        if (optionIndex < 0 || optionIndex >= options.Count)
            return true;
        var key = options[optionIndex].TextKey;
        foreach (var (denyEvent, denyKey) in OptionDenyList)
        {
            if (entryName.Contains(denyEvent, StringComparison.Ordinal)
                && key.Contains(denyKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        ModelId? plannedCardPick)
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
            var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = RunState.FromSerializable(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(livePlayer.NetId)
                               ?? throw new InvalidOperationException(
                                   $"shadow snapshot lacks player {livePlayer.NetId}");
            RewriteShadowNetId(shadowPlayer);

            // The proven initialization path (same as the prediction adapter):
            // BeginEvent is intentionally NOT used — it rejects canonical
            // clones whose Owner was carried over by the shallow copy.
            var shadowEvent = canonicalEvent.ToMutable();
            shadowEvent.Owner = shadowPlayer;
            var playerSlot = shadowEvent.IsShared
                ? 0
                : shadowPlayer.RunState.GetPlayerSlotIndex(shadowPlayer);
            shadowEvent.Rng = new MegaCrit.Sts2.Core.Random.Rng(
                (ulong)((long)shadowPlayer.RunState.Rng.Seed + playerSlot)
                + StringHelper.GetDeterministicHashCode(shadowEvent.Id.Entry));
            shadowEvent.CalculateVars();
            var options = (IReadOnlyList<EventOption>?)GenerateInitialEventOptionsMethod.Invoke(shadowEvent, null)
                          ?? throw new InvalidOperationException(
                              $"Event {shadowEvent.Id} returned no initial options.");
            if (optionIndex >= options.Count)
            {
                outcome.OptionOutOfRange = true;
                return outcome;
            }
            if (IsEventExecutionDenied(entryName, options, optionIndex))
            {
                outcome.DenyReason = "该选项会打开界面/进入特殊战斗，无法无头预演";
                return outcome;
            }
            SetEventStateMethod.Invoke(shadowEvent, [shadowEvent.InitialDescription, options]);
            var before = Capture(shadowPlayer);

            using (CardSelectCmd.PushSelector(new ScriptedCardSelector(plannedCardPick)))
            {
                await options[optionIndex].Chosen();
            }

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
            return outcome;
        }
        finally
        {
            NonInteractiveMode.AutoSlayerCheck = previousCheck;
        }
    }

    private static void RewriteShadowNetId(Player shadowPlayer)
    {
        // NetId is get-only; the backing field rewrite is what keeps every
        // LocalContext.IsMe/IsMine gate (profile writes, VFX) closed.
        var field = typeof(Player).GetField(
            "<NetId>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(shadowPlayer, shadowPlayer.NetId ^ 0x5EED0FCEC0FFEE00UL);
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

    internal CrystalSphereLayout PredictCrystalSphereLayout(
        Player livePlayer,
        EventModel canonical,
        int divinationCount)
    {
        try
        {
            var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = RunState.FromSerializable(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(livePlayer.NetId)
                               ?? throw new InvalidOperationException("shadow snapshot lacks player");
            RewriteShadowNetId(shadowPlayer);

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
    }

    /// <summary>
    /// Victory-drop prediction for combat-entry event branches (audit §4a):
    /// no combat is entered; basic rewards come from the cloned reward
    /// pipeline over the event's own encounter, and per-event resume effects
    /// are mirrored exactly as the option closures perform them.
    /// </summary>
    internal sealed class EventCombatDrops
    {
        public Forecast<CombatRewardDetails>? Rewards { get; set; }
        public List<string> Labels { get; } = [];
        public string? Error { get; set; }
    }

    internal EventCombatDrops PredictEventCombatVictoryDrops(
        Player livePlayer,
        EventModel canonicalEvent,
        int optionIndex)
    {
        var drops = new EventCombatDrops();
        try
        {
            var encounter = canonicalEvent.CanonicalEncounter
                            ?? throw new InvalidOperationException("event has no encounter");
            var snapshot = RunManager.Instance.ToSave(preFinishedRoom: null);
            var shadowRun = RunState.FromSerializable(snapshot);
            var shadowPlayer = shadowRun.GetPlayer(livePlayer.NetId)
                               ?? throw new InvalidOperationException("shadow snapshot lacks player");
            RewriteShadowNetId(shadowPlayer);

            var mutableEncounter = encounter.ToMutable();
            mutableEncounter.GenerateMonstersWithSlots(shadowRun);
            drops.Rewards = PredictCombatRewards(shadowPlayer, [RoomType.Monster], mutableEncounter);

            var entryName = canonicalEvent.GetType().Name;
            switch (entryName)
            {
                case "BattlewornDummy":
                    if (optionIndex == 0)
                    {
                        var pool = shadowPlayer.Character.PotionPool
                            .GetUnlockedPotions(shadowPlayer.UnlockState)
                            .Concat(ModelDb.PotionPool<SharedPotionPool>()
                                .GetUnlockedPotions(shadowPlayer.UnlockState));
                        var potion = shadowPlayer.PlayerRng.Rewards.NextItem(pool);
                        drops.Labels.Add(potion is null
                            ? "战后药水：无可用"
                            : $"战后药水：{potion!.Title.GetFormattedText()}");
                    }
                    else if (optionIndex == 1)
                    {
                        drops.Labels.Add("战后：随机强化 2 张可升级牌（牌组变化随 M4 线程生效）");
                    }
                    else if (optionIndex == 2)
                    {
                        var relic = RelicFactory.PullNextRelicFromFront(shadowPlayer);
                        drops.Labels.Add(relic is null
                            ? "战后遗物：无"
                            : $"战后遗物：{relic.Title.GetFormattedText()}");
                    }
                    break;
                case "PunchOff":
                    drops.Labels.Add("额外奖励：遗物 + 药水（接受时结算）");
                    break;
            }

            return drops;
        }
        catch (Exception exception)
        {
            drops.Error = exception.GetBaseException().Message;
            return drops;
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
