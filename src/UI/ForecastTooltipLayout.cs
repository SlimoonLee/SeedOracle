using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.addons.mega_text;

namespace SeedOracle.UI;

internal sealed class ForecastTooltipLayout : IDisposable
{
    internal const float PreferredWidth = 520f;
    private readonly Control panel;
    private readonly float width;

    internal ForecastTooltipLayout(float viewportWidth)
    {
        width = WidthForViewport(viewportWidth);
        panel = PreloadManager.Cache.GetScene("res://scenes/ui/hover_tip.tscn").Instantiate<Control>();
        (NGame.Instance?.HoverTipsContainer ?? throw new InvalidOperationException("Hover container unavailable"))
            .AddChild(panel);
        panel.Visible = false;
        ConfigurePanel(panel, width);
        panel.GetNode<MegaLabel>("%Title").SetTextAutoSize("Seed Oracle · 节点前瞻 · 1/99");
    }

    internal static float WidthForViewport(float viewportWidth) => Math.Clamp(viewportWidth - 48f, 240f, PreferredWidth);

    internal static void ConfigurePanel(Control control, float width)
    {
        var body = control.GetNode<MegaRichTextLabel>("%Description");
        body.AutoSizeEnabled = false;
        // Keep the native font size stable across measurements and displayed pages.
        if (!control.HasMeta("oracle_body_font_size"))
            control.SetMeta("oracle_body_font_size", body.GetThemeFontSize("normal_font_size"));
        body.SetFontSize(control.GetMeta("oracle_body_font_size").AsInt32());
        control.CustomMinimumSize = new Vector2(width, 0);
        control.Size = new Vector2(width, control.Size.Y);
        var inset = 0;
        for (var ancestor = body.GetParent(); ancestor is Control; ancestor = ancestor.GetParent())
        {
            if (ancestor is MarginContainer margin)
                inset += margin.GetThemeConstant("margin_left") + margin.GetThemeConstant("margin_right");
            if (ReferenceEquals(ancestor, control))
                break;
        }
        var title = control.GetNode<MegaLabel>("%Title");
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var iconWidth = control.GetNode<TextureRect>("%Icon").GetCombinedMinimumSize().X;
        title.Size = new Vector2(Math.Max(1f, width - inset - iconWidth - 5f), title.Size.Y);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.FitContent = true;
        body.CustomMinimumSize = new Vector2(width - inset, 0);
        body.Size = new Vector2(width - inset, body.Size.Y);
    }

    internal float Measure(string text)
    {
        var body = panel.GetNode<MegaRichTextLabel>("%Description");
        ConfigurePanel(panel, width);
        body.Text = "Shift+滚轮翻页\n" + text;
        panel.ResetSize();
        return Math.Max(panel.GetCombinedMinimumSize().Y, body.GetContentHeight() + 100f);
    }

    public void Dispose() => panel.Free();
}
