using System.Runtime.InteropServices;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal static class HotkeyBinding
{
    internal static void Draw(D2dTarget g, UiThemePalette theme, UiHitMap hit, string id,
        UiRect rect, string text, string? tooltip = null)
    {
        hit.Add(id, rect, UiCursorKind.Hand, tooltip);
        float opacity = hit.IsActive(id) ? .7f : 1f;
        g.FillRoundedRect(rect, 8f, (hit.IsHover(id) ? theme.NavHover : theme.SurfaceBg).WithAlphaFraction(opacity));
        g.DrawRoundedRect(rect, 8f, theme.Border.WithAlphaFraction(opacity), 1f);
        g.DrawText(text, rect.Deflate(8f, 0), theme.Accent.WithAlphaFraction(opacity), 12.5f, 600,
            UiTextAlign.Center, UiVertAlign.Center);
    }

    internal static string? MouseButton(UiMouseButton button) => button switch
    {
        UiMouseButton.Middle => "MButton",
        UiMouseButton.XButton1 => "XButton1",
        UiMouseButton.XButton2 => "XButton2",
        _ => null
    };

    internal static string Wheel(float delta) => Format(delta > 0 ? "WheelUp" : "WheelDown",
        GetKeyState(0x11) < 0, GetKeyState(0x12) < 0, GetKeyState(0x10) < 0);

    internal static string? Keyboard(int vk, bool ctrl, bool alt, bool shift) =>
        VirtualKeyToName(vk) is string key ? Format(key, ctrl, alt, shift) : null;

    internal static string Format(string key, bool ctrl, bool alt, bool shift) =>
        (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") +
        (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0 ? "Win+" : "") + key;

    private static string? VirtualKeyToName(int vk)
    {
        if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x1B)
            return null;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        if (vk is >= 0x60 and <= 0x69) return "NumPad" + (vk - 0x60);
        return vk switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x08 => "Back",
            0x90 => "NumLock",
            0x91 => "Scroll",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x2C => "PrintScreen",
            0xDF => "Oem8",
            0xBB => "OemPlus",
            0xBD => "OemMinus",
            0xBC => "OemComma",
            0xBE => "OemPeriod",
            0xBF => "OemQuestion",
            0xDB => "OemOpenBrackets",
            0xDD => "OemCloseBrackets",
            0xDC => "OemPipes",
            0xBA => "OemSemicolon",
            0xDE => "OemQuotes",
            0xC0 => "OemTilde",
            _ => null,
        };
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
}
