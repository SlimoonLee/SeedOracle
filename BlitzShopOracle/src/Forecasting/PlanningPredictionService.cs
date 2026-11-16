using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using BlitzShopOracle.Integration;

namespace BlitzShopOracle.Forecasting;

/// <summary>
/// The small snapshot boundary needed by the Blitz shop predictor. It clones
/// only the native run graph and keeps all shop, reward, transform and relic
/// streams on the copied player. No combat or third-party prediction service is
/// involved.
/// </summary>
internal sealed class PlanningPredictionService
{
    internal sealed class State
    {
        public required RunState Run { get; set; }
        public required Player Player { get; set; }
    }

    internal sealed record StateSnapshot(SerializableRun Run, ulong PlayerNetId);

    internal State CreateState(RunState liveRun, Player livePlayer)
    {
        var shadowRun = RestoreRun(CreateSerializableRun(liveRun));
        var shadowPlayer = shadowRun.GetPlayer(livePlayer.NetId)
            ?? throw new InvalidOperationException($"Planning shadow snapshot lacks player {livePlayer.NetId}.");
        return new State { Run = shadowRun, Player = shadowPlayer };
    }

    internal StateSnapshot Capture(State state) =>
        new(CreateSerializableRun(state.Run), state.Player.NetId);

    internal State Restore(StateSnapshot snapshot)
    {
        var run = RestoreRun(snapshot.Run);
        var player = run.GetPlayer(snapshot.PlayerNetId)
            ?? throw new InvalidOperationException($"Planning snapshot lacks player {snapshot.PlayerNetId}.");
        return new State { Run = run, Player = player };
    }

    internal static SerializableRun CreateSerializableRun(RunState source)
    {
        // RunState has no public ToSerializable method. Start from the engine's
        // save envelope, then replace every mutable section with this run.
        var save = RunManager.Instance.ToSave(preFinishedRoom: null);
        save.Acts = source.Acts.Select(act => act.ToSave()).ToList();
        if (source.Map is not NullActMap)
            save.Acts[source.CurrentActIndex].SavedMap = SerializableActMap.FromActMap(source.Map);
        save.Modifiers = source.Modifiers.Select(modifier => modifier.ToSerializable()).ToList();
        save.CurrentActIndex = source.CurrentActIndex;
        save.EventsSeen = source.VisitedEventIds.ToList();
        save.SerializableOdds = source.Odds.ToSerializable();
        save.SerializableSharedRelicGrabBag = source.SharedRelicGrabBag.ToSerializable();
        save.Players = source.Players.Select(player => player.ToSerializable()).ToList();
        save.SerializableRng = source.Rng.ToSerializable();
        save.VisitedMapCoords = source.VisitedMapCoords.ToList();
        save.MapPointHistory = source.MapPointHistory.Select(history => history.ToList()).ToList();
        save.Ascension = source.AscensionLevel;
        save.GameMode = source.GameMode;
        save.ExtraFields = source.ExtraFields.ToSerializable();
        return CloneSnapshot(save);
    }

    internal static RunState RestoreRun(SerializableRun snapshot)
    {
        var owned = CloneSnapshot(snapshot);
        var run = RunState.FromSerializable(owned);
        ShadowIsolation.RegisterRun(run);
        if (owned.Acts[owned.CurrentActIndex].SavedMap is { } map)
            run.Map = new SavedActMap(map);
        foreach (var modifier in run.Modifiers.Where(modifier =>
                     modifier.GetType().Assembly == typeof(ModifierModel).Assembly))
            modifier.OnRunLoaded(run);
        return run;
    }

    internal static SerializableRun CloneSnapshot(SerializableRun save)
    {
        var typeInfo = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(save, typeInfo);
        var clone = JsonSerializer.Deserialize(bytes, typeInfo)
            ?? throw new InvalidOperationException("Unable to clone planning run snapshot.");
        for (var actIndex = 0; actIndex < save.Acts.Count; actIndex++)
        {
            if (save.Acts[actIndex].SavedMap is not { } sourceMap)
                continue;
            var targetMap = clone.Acts[actIndex].SavedMap!;
            for (var pointIndex = 0; pointIndex < sourceMap.Points.Count; pointIndex++)
                targetMap.Points[pointIndex].CanBeModified = sourceMap.Points[pointIndex].CanBeModified;
            targetMap.BossPoint.CanBeModified = sourceMap.BossPoint.CanBeModified;
            targetMap.StartingPoint.CanBeModified = sourceMap.StartingPoint.CanBeModified;
            if (sourceMap.SecondBossPoint is { } secondBoss)
                targetMap.SecondBossPoint!.CanBeModified = secondBoss.CanBeModified;
        }
        return clone;
    }
}



