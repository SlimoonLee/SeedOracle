using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal static class ForecastChoiceDiffFormatter
{
    internal static string Slot(int index) => index < 10 ? "①②③④⑤⑥⑦⑧⑨⑩"[index].ToString() : (index + 1).ToString();

    // The base inventory already contains the previous item and price. Each
    // alternative gives a complete list of replacements, with only the delta.
    internal static IReadOnlyList<string> MerchantCompact(MerchantInventoryForecast before, MerchantInventoryForecast after, bool chinese)
    {
        var lines = new List<string>();
        Add(chinese ? "角色卡" : "Character", before.CharacterCards, after.CharacterCards);
        Add(chinese ? "无色卡" : "Colorless", before.ColorlessCards, after.ColorlessCards);
        Add(chinese ? "遗物" : "Relics", before.Relics, after.Relics);
        Add(chinese ? "药水" : "Potions", before.Potions, after.Potions);
        if (before.CardRemovalCost != after.CardRemovalCost)
            lines.Add((chinese ? "删牌：" : "Removal: ") + after.CardRemovalCost + (chinese ? "金" : "g")
                + Delta(after.CardRemovalCost - before.CardRemovalCost, chinese));
        return lines;

        void Add(string heading, IReadOnlyList<MerchantItemForecast> previous, IReadOnlyList<MerchantItemForecast> next)
        {
            var slots = new List<string>();
            for (var index = 0; index < Math.Max(previous.Count, next.Count); index++)
            {
                var a = index < previous.Count ? previous[index] : null;
                var b = index < next.Count ? next[index] : null;
                if (a is not null && b is not null && a.Cost == b.Cost && a.IsOnSale == b.IsOnSale
                    && ForecastContentKey.Item(a.Item) == ForecastContentKey.Item(b.Item))
                    continue;
                var value = Slot(index) + (b is null ? chinese ? "无" : "none"
                    : Name(b.Item) + " " + b.Cost + (chinese ? "金" : "g"));
                var annotations = new List<string>();
                if (b?.IsOnSale == true) annotations.Add(chinese ? "半价" : "sale");
                if (a is not null && b is not null && a.Cost != b.Cost)
                    annotations.Add((b.Cost - a.Cost).ToString("+0;-0;0"));
                if (annotations.Count > 0) value += "（" + string.Join(chinese ? "，" : ", ", annotations) + "）";
                slots.Add(value);
            }
            if (slots.Count > 0) lines.Add(heading + (chinese ? "：" : ": ") + string.Join(chinese ? "、" : "; ", slots));
        }
    }

    internal static IReadOnlyList<string> Merchant(MerchantInventoryForecast before, MerchantInventoryForecast after, bool chinese)
    {
        var lines = new List<string>();
        MerchantSlots(lines, chinese ? "角色卡" : "Character card", before.CharacterCards, after.CharacterCards, chinese);
        MerchantSlots(lines, chinese ? "无色卡" : "Colorless card", before.ColorlessCards, after.ColorlessCards, chinese);
        MerchantSlots(lines, chinese ? "遗物" : "Relic", before.Relics, after.Relics, chinese);
        MerchantSlots(lines, chinese ? "药水" : "Potion", before.Potions, after.Potions, chinese);
        if (before.CardRemovalCost != after.CardRemovalCost)
            lines.Add($"{(chinese ? "删牌价格" : "Removal price")}: {before.CardRemovalCost} → {after.CardRemovalCost}"
                + Delta(after.CardRemovalCost - before.CardRemovalCost, chinese));
        return lines;
    }

    internal static IReadOnlyList<string> Combat(CombatRewardDetails before, CombatRewardDetails after, bool chinese)
    {
        var lines = new List<string>();
        if (before.Gold != after.Gold)
            lines.Add($"{(chinese ? "金币" : "Gold")}: {before.Gold} → {after.Gold}");
        Groups(lines, before.CardRewardGroups, after.CardRewardGroups, chinese);
        Slots(lines, chinese ? "药水" : "Potion", before.Potions, after.Potions, chinese, (item, _) => Name(item.Item));
        Slots(lines, chinese ? "遗物" : "Relic", before.Relics, after.Relics, chinese, (item, _) => Name(item.Item));
        var previous = before.Possibilities.ToDictionary(PossibilityKey);
        var next = after.Possibilities.ToDictionary(PossibilityKey);
        foreach (var key in previous.Keys.Union(next.Keys))
        {
            previous.TryGetValue(key, out var a);
            next.TryGetValue(key, out var b);
            // A budget stop leaves this combination unknown. It does not
            // remove the reward cards. Showing every omitted preview as
            // "card -> none" both invented a result and produced huge text.
            if (b is null) continue;
            var branch = b;
            var label = (branch.HuntTriggers > 0
                ? chinese ? $"狩猎斩杀{branch.HuntTriggers}次，" : $"Hunt x{branch.HuntTriggers}, " : "")
                + (branch.RerollOrder.Count > 0
                    ? (chinese ? "领取前重掷组 " : "reroll before taking rewards, groups ")
                      + string.Join(" > ", branch.RerollOrder.Select(group => group + 1))
                    : chinese ? "不重掷" : "no reroll");
            if (a is null)
            {
                var heading = before.PossibilitiesTruncated
                    ? chinese ? "（新增已算）" : " (newly evaluated)"
                    : chinese ? "（新增组合）" : " (new combination)";
                lines.Add(label + heading + (chinese ? "：" : ": ") + string.Join(chinese ? "；" : "; ",
                    b.Groups.Select((group, index) => (chinese ? $"第{index + 1}组 " : $"Group {index + 1}: ")
                        + string.Join(" / ", group.Cards.Select(card => Name(card.Item))))));
                continue;
            }
            var changes = new List<string>();
            Groups(changes, a?.Groups ?? [], b?.Groups ?? [], chinese);
            lines.AddRange(changes.Select(change => label + " / " + change));
        }
        if (before.HuntPreviewLimit is not null && after.HuntPreviewLimit is null)
            lines.Add(chinese ? "不再含狩猎额外选牌" : "The Hunt no longer adds card rewards");
        if (before.PossibilitiesTruncated != after.PossibilitiesTruncated)
            lines.Add(chinese ? $"重掷组合：已算{after.Possibilities.Count}种，{(after.PossibilitiesTruncated ? "未穷尽" : "范围内已列全")}"
                : $"Rerolls: {after.Possibilities.Count} evaluated, {(after.PossibilitiesTruncated ? "incomplete" : "complete within range")}");
        return lines;
    }

    internal static IReadOnlyList<string> Treasure(TreasureRoomDetails before, TreasureRoomDetails after, bool chinese)
    {
        var lines = new List<string>();
        if (before.IsEmpty != after.IsEmpty)
            lines.Add(chinese ? after.IsEmpty ? "宝箱变为空箱" : "宝箱不再为空箱"
                : after.IsEmpty ? "Chest becomes empty" : "Chest is no longer empty");
        if (before.Gold != after.Gold)
            lines.Add($"{(chinese ? "金币" : "Gold")}: {before.Gold} → {after.Gold}");
        Slots(lines, chinese ? "遗物" : "Relic", before.Relics, after.Relics, chinese, (item, _) => Name(item.Item));
        return lines;
    }

    private static string PossibilityKey(CombatRewardPossibility branch) =>
        branch.HuntTriggers + ":" + string.Join(",", branch.RerollOrder);

    private static void Groups(List<string> lines, IReadOnlyList<CombatCardRewardGroup> before,
        IReadOnlyList<CombatCardRewardGroup> after, bool chinese)
    {
        for (var group = 0; group < Math.Max(before.Count, after.Count); group++)
        {
            Slots(lines, chinese ? $"第{group + 1}组选牌" : $"Card group {group + 1}",
                group < before.Count ? before[group].Cards : [], group < after.Count ? after[group].Cards : [],
                chinese, (item, _) => Name(item.Item));
            var a = group < before.Count ? before[group].Steps.FirstOrDefault()?.Alternatives ?? [] : [];
            var b = group < after.Count ? after[group].Steps.FirstOrDefault()?.Alternatives ?? [] : [];
            var added = b.Where(option => !a.Any(previous => previous.OptionId == option.OptionId
                && previous.AfterSelected == option.AfterSelected && previous.Title == option.Title)).ToArray();
            var removed = a.Where(option => !b.Any(next => next.OptionId == option.OptionId
                && next.AfterSelected == option.AfterSelected && next.Title == option.Title)).ToArray();
            if (added.Length > 0 || removed.Length > 0)
            {
                var changes = new List<string>();
                if (added.Length > 0) changes.Add((chinese ? "新增" : "adds ") + string.Join(chinese ? "、" : ", ", added.Select(option => option.Title)));
                if (removed.Length > 0) changes.Add((chinese ? "不再提供" : "removes ") + string.Join(chinese ? "、" : ", ", removed.Select(option => option.Title)));
                lines.Add((chinese ? $"第{group + 1}组选牌：" : $"Card group {group + 1}: ") + string.Join(chinese ? "；" : "; ", changes));
            }
        }
    }

    private static void Slots<T>(List<string> lines, string label, IReadOnlyList<T> before, IReadOnlyList<T> after,
        bool chinese, Func<T, bool, string> format)
    {
        for (var slot = 0; slot < Math.Max(before.Count, after.Count); slot++)
        {
            var a = slot < before.Count ? format(before[slot], chinese) : chinese ? "无" : "none";
            var b = slot < after.Count ? format(after[slot], chinese) : chinese ? "无" : "none";
            if (a != b)
                lines.Add($"{label}{(chinese ? $"第{slot + 1}格" : $" slot {slot + 1}")}: {a} → {b}");
        }
    }

    private static string Stock(MerchantItemForecast item, bool chinese) =>
        Name(item.Item) + " " + Price(item, chinese);

    private static string Price(MerchantItemForecast item, bool chinese) =>
        $"({item.Cost}{(chinese ? "金" : "g")}{(item.IsOnSale ? chinese ? "，半价" : ", sale" : "")})";

    private static void MerchantSlots(List<string> lines, string label,
        IReadOnlyList<MerchantItemForecast> before, IReadOnlyList<MerchantItemForecast> after, bool chinese)
    {
        for (var slot = 0; slot < Math.Max(before.Count, after.Count); slot++)
        {
            var a = slot < before.Count ? before[slot] : null;
            var b = slot < after.Count ? after[slot] : null;
            var sameItem = a is not null && b is not null
                && ForecastContentKey.Item(a.Item) == ForecastContentKey.Item(b.Item);
            if (sameItem && a!.Cost == b!.Cost && a.IsOnSale == b.IsOnSale)
                continue;
            var change = (a is null ? chinese ? "无" : "none" : Stock(a, chinese))
                + " → " + (b is null ? chinese ? "无" : "none" : sameItem ? Price(b, chinese) : Stock(b, chinese));
            if (a is not null && b is not null && a.Cost != b.Cost)
                change += Delta(b.Cost - a.Cost, chinese);
            lines.Add($"{label}{(chinese ? $"第{slot + 1}格" : $" slot {slot + 1}")}: {change}");
        }
    }

    private static string Delta(int value, bool chinese) =>
        $" ({value:+0;-0;0}{(chinese ? "金" : "g")})";

    private static string Name(ForecastItemDetails item) => ForecastItemFormatter.Format(item, false);
}
