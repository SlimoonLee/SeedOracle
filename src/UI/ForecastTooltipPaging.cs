using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.addons.mega_text;

namespace SeedOracle.UI;

// Keep every computed row available while showing one bounded native panel.
internal static class ForecastTooltipPaging
{
    private static WeakReference<NMapPoint>? owner;
    private static IReadOnlyList<string> pages = [];
    private static string title = "";
    private static int pageIndex;
    private static WeakReference<NHoverTipSet>? currentSet;
    private static float panelWidth;
    private static ulong lastWheelEvent;
    internal static int PageIndex => pageIndex;

    internal static IReadOnlyList<string> CreatePages(
        IReadOnlyList<string> descriptions, float viewportHeight, float viewportWidth = 1920f)
    {
        using var layout = new ForecastTooltipLayout(viewportWidth);
        var maximumHeight = Math.Max(200f, viewportHeight - 48f);
        var result = new List<string>();
        foreach (var description in descriptions)
        {
            var tokens = Tokenize(description);
            var boundaries = tokens.Select((token, index) => (token, index))
                .Where(item => item.token.Text == "\n").Select(item => item.index + 1)
                .Append(tokens.Count).Distinct().ToArray();
            var start = 0;
            while (start < tokens.Count)
            {
                var ends = boundaries.Where(end => end > start).ToArray();
                var end = LargestFit(0, ends.Length - 1, index => Fits(ends[index])) is var fit && fit >= 0
                    ? ends[fit] : -1;
                if (end < 0)
                {
                    // Split an individually oversized paragraph only at a page boundary.
                    end = LargestFit(start + 1, ends[0], Fits);
                    if (end <= start)
                        end = start + 1;
                    var wordEnd = Enumerable.Range(Math.Max(start + 1, end - 80), Math.Min(80, end - start))
                        .Where(index => index <= end && tokens[index - 1].BreakAfter).LastOrDefault();
                    if (wordEnd > start)
                        end = wordEnd;
                }
                result.Add(Slice(start, end));
                start = end;

                bool Fits(int stop) => layout.Measure(Slice(start, stop)) <= maximumHeight;
            }

            string Slice(int first, int stop) => tokens[first].OpenBefore
                + string.Concat(tokens.Skip(first).Take(stop - first).Select(token => token.Text))
                + tokens[stop - 1].CloseAfter;
        }
        return result;
    }

    private static int LargestFit(int first, int last, Func<int, bool> fits)
    {
        var result = -1;
        while (first <= last)
        {
            var middle = first + (last - first) / 2;
            if (fits(middle))
            {
                result = middle;
                first = middle + 1;
            }
            else
                last = middle - 1;
        }
        return result;
    }

    internal static void Show(NMapPoint target, MapForecastTooltip tooltip)
    {
        Clear();
        owner = new(target);
        title = tooltip.Title;
        var viewport = NGame.Instance?.GetViewportRect().Size ?? new Vector2(1920, 1080);
        panelWidth = ForecastTooltipLayout.WidthForViewport(viewport.X);
        pages = CreatePages(tooltip.Descriptions, viewport.Y, viewport.X);
        ShowPage(target);
    }

    internal static bool HandleInput(NMapPoint target, InputEvent input)
    {
        if (pages.Count < 2 || owner?.TryGetTarget(out var active) != true || !ReferenceEquals(active, target)
            || !NHoverTipSet._activeHoverTips.ContainsKey(target)
            || input is not InputEventMouseButton { Pressed: true, ShiftPressed: true } mouse
            || mouse.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown))
            return false;
        if (lastWheelEvent != input.GetInstanceId())
        {
            lastWheelEvent = input.GetInstanceId();
            pageIndex = Math.Clamp(pageIndex + (mouse.ButtonIndex == MouseButton.WheelDown ? 1 : -1), 0, pages.Count - 1);
            ShowPage(target);
        }
        target.GetViewport()?.SetInputAsHandled();
        return true;
    }

    internal static bool HandleMapInput(NMapScreen screen, InputEvent input) =>
        owner?.TryGetTarget(out var target) == true && ReferenceEquals(target._screen, screen) && HandleInput(target, input);

    internal static void Clear()
    {
        owner = null;
        pages = [];
        pageIndex = 0;
        currentSet = null;
        lastWheelEvent = 0;
    }

    private static void ShowPage(NMapPoint target)
    {
        if (pages.Count == 0)
            return;
        var chinese = LocManager.Instance?.Language is "zhs" or "zht";
        var pageTitle = pages.Count > 1 ? $"{title} · {pageIndex + 1}/{pages.Count}" : title;
        var navigation = pages.Count > 1
            ? chinese ? "[gold]Shift+滚轮翻页[/gold]\n" : "[gold]Shift+wheel: pages[/gold]\n"
            : "";
        NHoverTipSet? set;
        if (currentSet?.TryGetTarget(out set) == true && GodotObject.IsInstanceValid(set) && !set.IsQueuedForDeletion()
            && NHoverTipSet._activeHoverTips.GetValueOrDefault(target) == set)
        {
            var panel = set._textHoverTipContainer.GetChildren().OfType<Control>().Single();
            ForecastTooltipLayout.ConfigurePanel(panel, panelWidth);
            panel.GetNode<MegaLabel>("%Title").SetTextAutoSize(pageTitle);
            panel.GetNode<MegaRichTextLabel>("%Description").Text = navigation + pages[pageIndex];
            panel.ResetSize();
            set._textHoverTipContainer.Size = new Vector2(panelWidth, panel.Size.Y + 5f);
        }
        else
        {
            MapHoverTipPresentation.HideTip(target);
            var tip = new HoverTip(target._runState.Act.Title, navigation + pages[pageIndex])
                { Title = pageTitle, Id = $"{Entry.ModId}:paged:{target.Point.coord}:{pageIndex}" };
            set = NHoverTipSet.CreateAndShow(target, tip, HoverTip.GetHoverTipAlignment(target));
            currentSet = set is null ? null : new(set);
            if (set is not null)
            {
                var panel = set._textHoverTipContainer.GetChildren().OfType<Control>().Single();
                ForecastTooltipLayout.ConfigurePanel(panel, panelWidth);
                panel.ResetSize();
                set._textHoverTipContainer.Size = new Vector2(panelWidth, panel.Size.Y + 5f);
            }
        }
        MapHoverTipPresentation.Finalize(target, set);
    }

    private sealed record Token(string Text, string OpenBefore, string CloseAfter, bool BreakAfter);

    private static IReadOnlyList<Token> Tokenize(string text)
    {
        var tags = new List<(string Name, string Open)>();
        var result = new List<Token>();
        var opening = "";
        var closing = "";
        for (var index = 0; index < text.Length;)
        {
            string token;
            var before = opening;
            if (text[index] == '[' && text.IndexOf(']', index) is var end && end >= 0)
            {
                token = text[index..(end + 1)];
                if (token.StartsWith("[img", StringComparison.Ordinal)
                    && text.IndexOf("[/img]", end + 1, StringComparison.Ordinal) is var imageEnd && imageEnd >= 0)
                {
                    token = text[index..(imageEnd + 6)];
                }
                else if (token.StartsWith("[/", StringComparison.Ordinal))
                {
                    var name = token[2..^1];
                    var open = tags.FindLastIndex(tag => tag.Name == name);
                    if (open >= 0)
                        tags.RemoveAt(open);
                }
                else
                {
                    var name = token[1..^1].Split('=')[0];
                    if (name is "color" or "gold" or "green" or "orange" or "blue" or "aqua" or "pink"
                        or "red" or "purple" or "b" or "i" or "u")
                        tags.Add((name, token));
                }
                opening = string.Concat(tags.Select(tag => tag.Open));
                closing = string.Concat(tags.AsEnumerable().Reverse().Select(tag => $"[/{tag.Name}]"));
            }
            else
            {
                var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
                token = text.Substring(index, length);
            }
            index += token.Length;
            if (token == "\r")
                continue;
            result.Add(new Token(token, before, closing, token is " " or "\n" or "、" or "；" or "/"));
        }
        return result;
    }
}
