using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal sealed record CurrentEventChoiceContext(
    EventModel Canonical, IReadOnlyList<string> OptionKeys, int RngCounter)
{
    internal static CurrentEventChoiceContext? Capture(RunState run)
    {
        if (run.CurrentRoom is not EventRoom { IsPreFinished: false } room)
            return null;
        try { return FromEvent(room.LocalMutableEvent); }
        catch (InvalidOperationException) { return null; }
    }

    internal static CurrentEventChoiceContext? FromEvent(EventModel model) =>
        !model.IsFinished && model.Description?.LocEntryKey == model.InitialDescription.LocEntryKey
        && model.CurrentOptions.Count > 0 && model.CurrentOptions.All(option => !option.WasChosen)
            ? new(model.CanonicalInstance, model.CurrentOptions.Select(option => option.TextKey).ToArray(),
                model.Rng.ToSerializable().counter)
            : null;
}
