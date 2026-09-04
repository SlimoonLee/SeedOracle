using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace SeedOracle.Forecasting;

internal static class MonsterHpPredictor
{
    public static IReadOnlyList<MonsterHpDetails> Predict(RunState run, GeneratedEncounter encounter)
    {
        var rng = new Rng(run.Rng.Niche.ToSerializable());
        var creatures = new List<Creature>();
        var result = new List<MonsterHpDetails>();

        foreach (var (monster, slot) in encounter.Monsters)
        {
            var creature = new Creature(monster, CombatSide.Enemy, slot);
            creatures.Add(creature);
            creature.SetUniqueMonsterHpValue(creatures, rng);
            creature.ScaleMonsterHpForMultiplayer(
                encounter.MutableEncounter,
                run.Players.Count,
                run.CurrentActIndex);
            result.Add(new MonsterHpDetails(monster.Title.GetFormattedText(), creature.MaxHp));
        }

        return result;
    }
}

