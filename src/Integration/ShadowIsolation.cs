using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;

namespace SeedOracle.Integration;

/// <summary>
/// Shared guard for native shadow-run execution. Event effects frequently
/// branch on local-player ownership; closing those branches keeps shadow
/// objects from writing profile/UI state while preserving their real NetId.
/// </summary>
internal static class ShadowIsolation
{
    internal static Player? ActiveShadowPlayer { get; private set; }

    internal static void Enter(Player shadowPlayer) => ActiveShadowPlayer = shadowPlayer;

    internal static void Exit() => ActiveShadowPlayer = null;
}

/// <summary>
/// Closes local-player gates for shadow objects while an isolated prediction
/// is in flight. Live players and models remain unaffected.
/// </summary>
[HarmonyPatch(typeof(LocalContext))]
internal static class ShadowIsolationPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMe), typeof(Player))]
    private static void IsMePlayer(Player? player, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(player, shadow))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMe), typeof(Creature))]
    private static void IsMeCreature(Creature? creature, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(creature?.Player, shadow))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(CardModel))]
    private static void IsMineCard(CardModel? card, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(card?.Owner, shadow))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(PotionModel))]
    private static void IsMinePotion(PotionModel? potion, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(potion?.Owner, shadow))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(RelicModel))]
    private static void IsMineRelic(RelicModel? relic, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(relic?.Owner, shadow))
        {
            __result = false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(LocalContext.IsMine), typeof(EventModel))]
    private static void IsMineEvent(EventModel? eventModel, ref bool __result)
    {
        if (__result
            && ShadowIsolation.ActiveShadowPlayer is { } shadow
            && ReferenceEquals(eventModel?.Owner, shadow))
        {
            __result = false;
        }
    }
}
