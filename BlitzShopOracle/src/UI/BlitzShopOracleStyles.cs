using Godot;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace BlitzShopOracle.UI;

internal static class ThemeConstants
{
    internal static class Label
    {
        internal const string Font = "font";
        internal const string FontSize = "font_size";
        internal const string FontColor = "font_color";
    }

    internal static class RichTextLabel
    {
        internal const string NormalFont = "normal_font";
        internal const string NormalFontSize = "normal_font_size";
    }

    internal static class MarginContainer
    {
        internal const string MarginLeft = "margin_left";
        internal const string MarginRight = "margin_right";
        internal const string MarginTop = "margin_top";
        internal const string MarginBottom = "margin_bottom";
    }

    internal static class BoxContainer
    {
        internal const string Separation = "separation";
    }

    internal static class FlowContainer
    {
        internal const string HSeparation = "h_separation";
        internal const string VSeparation = "v_separation";
    }
}
internal static class StsColors
{
    internal static readonly Color gold = new("F2C14E");
    internal static readonly Color cream = new("F3E7C2");
    internal static readonly Color green = new("83D36B");
    internal static readonly Color lightGray = new("B7C8CB");
    internal static readonly Color aqua = new("67D8E8");
    internal static readonly Color orange = new("F6A94A");
    internal static readonly Color red = new("F06A62");
}

internal static class PreCombatPanelStyles
{
    internal static Label CreateLabel(string text, int size, Color color, bool bold = false)
    {
        var label = new Label
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None
        };
        label.ApplyLocaleFontSubstitution(bold ? FontType.Bold : FontType.Regular, ThemeConstants.Label.Font);
        label.AddThemeFontSizeOverride(ThemeConstants.Label.FontSize, size);
        label.AddThemeColorOverride(ThemeConstants.Label.FontColor, color);
        return label;
    }

    internal static Button CreateButton(string text, float width)
    {
        var button = new Button
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(width, 38f)
        };
        button.ApplyLocaleFontSubstitution(FontType.Bold, "font");
        button.AddThemeFontSizeOverride("font_size", 15);
        button.AddThemeColorOverride("font_color", StsColors.cream);
        button.AddThemeColorOverride("font_hover_color", StsColors.gold);
        button.AddThemeColorOverride("font_pressed_color", StsColors.gold);
        button.AddThemeStyleboxOverride("normal", CreateButtonStyle(
            new Color(0.06f, 0.13f, 0.15f, 0.95f), new Color(0.24f, 0.43f, 0.49f, 0.95f)));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle(
            new Color(0.09f, 0.18f, 0.20f, 0.98f), StsColors.gold));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle(
            new Color(0.03f, 0.08f, 0.10f, 0.98f), StsColors.gold));
        return button;
    }

    internal static StyleBoxFlat CreatePanel(Color background, Color border, int radius) => new()
    {
        BgColor = background,
        BorderColor = border,
        BorderWidthLeft = 2,
        BorderWidthTop = 2,
        BorderWidthRight = 2,
        BorderWidthBottom = 2,
        CornerRadiusTopLeft = radius,
        CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius,
        CornerRadiusBottomRight = radius
    };

    internal static StyleBoxFlat CreateButtonStyle(Color background, Color border)
    {
        var style = CreatePanel(background, border, 7);
        style.ContentMarginLeft = 9f;
        style.ContentMarginRight = 9f;
        style.ContentMarginTop = 4f;
        style.ContentMarginBottom = 4f;
        return style;
    }
}




