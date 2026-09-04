using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal static class EncounterCompositionPredictor
{
    public static IReadOnlyList<GeneratedEncounter> Generate(
        RunState run,
        int targetTotalFloor,
        IEnumerable<EncounterModel> encounters)
    {
        var generated = new List<GeneratedEncounter>();
        foreach (var source in encounters.DistinctBy(encounter => encounter.Id))
        {
            var mutable = source.ToMutable();
            var seed = (ulong)((long)run.Rng.Seed + targetTotalFloor)
                       + StringHelper.GetDeterministicHashCode(source.Id.Entry);

            // Supplying the target-floor RNG on the temporary encounter makes the original method skip
            // reading the live run's current floor. The temporary encounter is never registered with the run.
            mutable._rng = new Rng(seed);
            mutable.GenerateMonstersWithSlots(run);

            var monsters = mutable.MonstersWithSlots
                .Select(pair => (pair.Item1, pair.Item2))
                .ToArray();
            generated.Add(new GeneratedEncounter(
                source,
                mutable,
                monsters,
                new EncounterDetails(
                    source.Id,
                    source.Title.GetFormattedText(),
                    monsters.Select(pair => pair.Item1.Title.GetFormattedText()).ToArray())));
        }

        return generated;
    }
}

