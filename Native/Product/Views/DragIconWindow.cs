#if SONICROUTE_PHONE
using System;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class DragIconWindow : LayeredWindow
{
    private readonly NativeAppIconService.IconBitmap? _icon;
    private readonly UiIcon? _fallbackIcon;
    private readonly string _initial;
    private readonly UiColor _text;

    internal DragIconWindow(NativeAppIconService.IconBitmap? icon, string label, UiColor text, int x, int y, UiIcon? fallbackIcon = null)
        : base(36f, 36f, 36f, 36f)
    {
        _icon = icon;
        _fallbackIcon = fallbackIcon;
        _initial = string.IsNullOrWhiteSpace(label) ? "♪" : System.Globalization.StringInfo.GetNextTextElement(label);
        _text = text;
        TopMost = ToolWindow = NoActivate = ClickThrough = true;
        MovePx(x + 14, y + 16);
        EnsureCreated("");
        Show(activate: false);
    }

    internal void Follow(int x, int y)
    {
        if (LeftPx != x + 14 || TopPx != y + 16) MovePx(x + 14, y + 16);
    }

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        if (_icon is { Width: > 0, Height: > 0 } icon)
        {
            float scale = MathF.Min((widthDip - 6f) / icon.Width, (heightDip - 6f) / icon.Height);
            float w = icon.Width * scale, h = icon.Height * scale;
            g.DrawBgraBitmap("drag.icon", icon.BgraPixels, icon.Width, icon.Height,
                new UiRect((widthDip - w) / 2f, (heightDip - h) / 2f, w, h));
        }
        else if (_fallbackIcon is { } glyph)
        {
            g.DrawIcon(glyph, new UiRect(3f, 3f, widthDip - 6f, heightDip - 6f), _text, 2f);
        }
        else
        {
            g.DrawText(_initial, new UiRect(3f, 3f, widthDip - 6f, heightDip - 6f), _text, 22f,
                hAlign: UiTextAlign.Center, vAlign: UiVertAlign.Center);
        }
    }
}
#endif
