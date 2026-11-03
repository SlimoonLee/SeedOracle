using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.ValueProps;

namespace SeedOracle.Integration;

// Kill's native win-condition check reads a cloned run, then ends the GLOBAL
// RunManager and CombatManager. Intercept before that method (including its
// music/UI work and other mods' OnEnded postfixes) can ever be reached.
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Kill),
    new[] { typeof(IReadOnlyCollection<Creature>), typeof(bool) })]
internal static class ShadowCreatureDeathPatch
{
    [HarmonyPrefix]
    private static bool Prefix(IReadOnlyCollection<Creature> creatures, bool force, ref Task __result)
    {
        if (!creatures.Any(creature => ShadowIsolation.IsShadowPlayer(creature.Player)))
            return true;

        // Native combat simulation has its own isolated engine. Do not let an
        // unexpected mixed/live collection reach any global combat commands.
        if (creatures.Any(creature => !ShadowIsolation.IsShadowPlayer(creature.Player)
                                      || creature.CombatState is not null))
            throw new InvalidOperationException("影子事件的死亡目标不是独立的非战斗玩家，已停止预演。");

        __result = KillAsync(creatures, force);
        return false;
    }

    private static async Task KillAsync(IReadOnlyCollection<Creature> creatures, bool force)
    {
        foreach (var creature in creatures.ToArray())
        {
            var player = creature.Player!;
            var run = player.RunState;
            using var isolation = ShadowIsolation.Enter(player, automateRewards: true);
            // Preserve native protection against out-of-combat multiplayer
            // deaths, and native before/after/prevent-death hook ordering.
            if (run.Players.Count > 1 && !force)
            {
                await CreatureCmd.Heal(creature, 1m, playAnim: false);
                continue;
            }
            for (var recursion = 0; ; recursion++)
            {
                var hp = creature.CurrentHp;
                if (hp > 0)
                {
                    creature.LoseHpInternal(hp, ValueProp.Unblockable | ValueProp.Unpowered);
                    await Hook.AfterCurrentHpChanged(run, null, creature, -hp);
                }
                await Hook.BeforeDeath(run, null, creature);
                AbstractModel? preventer = null;
                if (force || creature.MaxHp <= 0 || Hook.ShouldDie(run, null, creature, out preventer))
                {
                    creature.InvokeDiedEvent();
                    await Hook.AfterDeath(run, null, creature, wasRemovalPrevented: false, 0f);
                    foreach (var power in creature.RemoveAllPowersAfterDeath())
                        await power.AfterRemoved(creature);
                    player.DeactivateHooks();
                    break;
                }
                if (recursion >= 10)
                    throw new InvalidOperationException("影子事件的防死效果未能恢复生命，已停止预演。");
                await Hook.AfterDeath(run, null, creature, wasRemovalPrevented: true, 0f);
                await Hook.AfterPreventingDeath(run, null, preventer!, creature);
                if (!creature.IsDead) break;
            }
        }
    }
}

// Fairy in a Bottle uses this wrapper during death prevention even outside
// combat. Its normal wrapper registers an effect on the global CombatManager.
[HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
internal static class ShadowPotionUsePatch
{
    private static readonly System.Reflection.MethodInfo OnUseMethod = AccessTools.DeclaredMethod(typeof(PotionModel), "OnUse");

    [HarmonyPrefix]
    private static bool Prefix(PotionModel __instance, PlayerChoiceContext choiceContext,
        Creature? target, ref Task __result)
    {
        if (!ShadowIsolation.IsShadowPlayer(__instance.Owner)) return true;
        if (__instance.Owner.Creature.CombatState is not null)
            throw new InvalidOperationException("影子药水使用须由隔离的战斗引擎执行。");
        __result = UseAsync(__instance, choiceContext, target);
        return false;
    }

    private static async Task UseAsync(PotionModel potion, PlayerChoiceContext context, Creature? target)
    {
        var player = potion.Owner;
        using var isolation = ShadowIsolation.Enter(player, automateRewards: true);
        potion.RemoveBeforeUse();
        context.PushModel(potion);
        try
        {
            await Hook.BeforePotionUsed(player.RunState, null, potion, target);
            await (Task)OnUseMethod.Invoke(potion, [context, target])!;
            if (player.Creature.IsDead) return;
            potion.InvokeExecutionFinished();
            await Hook.AfterPotionUsed(player.RunState, null, potion, target);
            player.RunState.CurrentMapPointHistoryEntry?.GetEntry(player.NetId).PotionUsed.Add(potion.Id);
        }
        finally { context.PopModel(potion); }
    }
}

// Trial.DoubleDown is a UI-only abandon action. Even capability discovery
// must not open its real modal; registered shadow identity also covers calls
// after the active forecast scope has been disposed.
[HarmonyPatch(typeof(Trial), "DoubleDown")]
internal static class ShadowTrialAbandonPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Trial __instance, ref Task __result)
    {
        if (!ShadowIsolation.IsShadowPlayer(__instance.Owner)) return true;
        __result = Task.FromException(new InvalidOperationException("该选项会放弃跑局，预演不打开确认界面。"));
        return false;
    }
}
