using BlitzShopOracle.Forecasting;

namespace BlitzShopOracle.UI;

/// <summary>
/// Renders one good's predicted contents. Shared by the map tooltip and the
/// in-shop hover so both say exactly the same thing.
/// </summary>
internal static class BlitzYieldText
{
    internal static string Render(BlitzYieldForecast yield, bool chinese) =>
        string.Join("\n", Lines(yield, chinese));

    internal static IReadOnlyList<string> Lines(BlitzYieldForecast yield, bool chinese)
    {
        if (!yield.Resolved)
            return [chinese ? "→ 暂不可预测" : "-> no forecast"];

        var lines = new List<string>();
        void Line(string text, params string[] names)
        {
            if (names.Length == 0) return;
            lines.Add("→ " + (chinese ? text : string.Empty) + string.Join(chinese ? "、" : ", ", names));
        }

        Line(chinese ? "牌库获得：" : "deck: ",
            yield.GainedCards.Select(item => item.Name).ToArray());
        Line(chinese ? "遗物：" : "relics: ",
            yield.Relics.Select(item => item.Name).ToArray());
        Line(chinese ? "药水：" : "potions: ",
            yield.Potions.Select(item => item.Name).ToArray());
        for (var index = 0; index < yield.CardScreens.Count; index++)
        {
            var screen = yield.CardScreens[index];
            var heading = yield.CardScreens.Count == 1
                ? chinese ? "三选一：" : "3-card pick: "
                : chinese ? $"三选一 {index + 1}/{yield.CardScreens.Count}：" : $"3-card pick {index + 1}: ";
            lines.Add("→ " + heading + string.Join(" / ", screen.Select(item => item.Name)));
        }
        Line(chinese ? "升级：" : "upgraded: ",
            yield.UpgradedCards
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => item.Name)
                .ToArray());
        if (yield.RandomUpgradeVariants.Any(variant => variant.Choices.Count > 0))
            lines.Add(chinese
                ? "→ 升级结果取决于前置选牌，请按求购路径操作"
                : "-> upgrades depend on the preceding card choices shown in the wish path");
        if (yield.RandomUpgradePathsTruncated)
            lines.Add(chinese
                ? "→ 路径较多，当前仅显示已计算的部分"
                : "-> more paths exist beyond the current search limit");

        var growth = new List<string>();
        if (yield.MaxHpDelta != 0)
            growth.Add(chinese
                ? $"最大生命{(yield.MaxHpDelta > 0 ? "+" : string.Empty)}{yield.MaxHpDelta}"
                : $"max HP {(yield.MaxHpDelta > 0 ? "+" : string.Empty)}{yield.MaxHpDelta}");
        if (yield.HealedHp > 0)
            growth.Add(chinese ? $"回复 {yield.HealedHp} 生命" : $"heal {yield.HealedHp}");
        if (growth.Count > 0)
            lines.Add("→ " + string.Join(chinese ? "，" : ", ", growth));

        if (yield.ExtraChoices != 0)
        {
            lines.Add("→ " + (chinese
                ? $"可带走 {(yield.ExtraChoices > 0 ? "+" : string.Empty)}{yield.ExtraChoices} 件"
                : $"picks {(yield.ExtraChoices > 0 ? "+" : string.Empty)}{yield.ExtraChoices}"));
        }

        // A transform is a deck pick with a random answer, so the answer is
        // listed per outcome. When every eligible card lands on the same card
        // — which is what one draw from one stream position usually gives —
        // saying so is more useful than naming two of them.
        var transformGroups = yield.TransformChoices
            .GroupBy(choice => choice.Result.Name, StringComparer.Ordinal)
            .ToList();
        if (transformGroups.Count == 1)
        {
            var only = yield.TransformChoices[0].Result.Name;
            lines.Add("→ " + (chinese
                ? $"{yield.TransformChoices.Count} 张可选牌都变化为 {only}"
                : $"all {yield.TransformChoices.Count} eligible cards become {only}"));
        }
        else
        {
            foreach (var group in transformGroups)
            {
                var sources = string.Join(chinese ? "、" : ", ",
                    group.Select(choice => choice.Source.Name).Distinct(StringComparer.Ordinal).Take(2));
                var results = string.Join(" / ",
                    group.Select(choice => choice.Result.Name).Distinct(StringComparer.Ordinal));
                var count = group.Count();
                lines.Add("→ " + (chinese
                    ? count == 1
                        ? $"{sources} 变化为 {results}"
                        : $"{sources}等 {count} 张牌变化为 {results}"
                    : count == 1
                        ? $"{sources} -> {results}"
                        : $"{sources} ({count} cards) -> {results}"));
            }
        }

        if (yield.NeedsCardPick && yield.TransformChoices.Count == 0)
        {
            lines.Add("→ " + (chinese
                ? $"需要你选一张牌（{yield.PickableCards} 张可选），结果随选择改变"
                : $"needs your card pick ({yield.PickableCards} eligible); result follows the pick"));
        }

        return lines;
    }
}
