using System;
using System.Collections.Generic;

namespace SonicRoute.Native.Ui;

internal readonly record struct UiHitItem(
    string Id,
    UiRect Bounds,
    UiCursorKind Cursor = UiCursorKind.Hand,
    string? Tooltip = null);

/// <summary>
/// Per-frame hit-test registry populated during rendering in Z-order (last item added wins on overlap).
/// </summary>
internal sealed class UiHitMap
{
    private readonly List<UiHitItem> _items = new(64);
    public UiHitItem? Hovered { get; private set; }
    public string? ActiveId { get; set; }

    public void Clear() => _items.Clear();

    public void Add(string id, UiRect bounds, UiCursorKind cursor = UiCursorKind.Hand, string? tooltip = null) =>
        _items.Add(new UiHitItem(id, bounds, cursor, tooltip));

    public UiHitItem? HitTest(float x, float y)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].Bounds.Contains(x, y))
                return _items[i];
        }
        return null;
    }

    public bool UpdateHover(float x, float y)
    {
        var hit = HitTest(x, y);
        bool changed = hit?.Id != Hovered?.Id;
        Hovered = hit;
        return changed;
    }

    public void ClearHover() => Hovered = null;

    public bool IsHover(string id) => Hovered?.Id == id;
    public bool IsActive(string id) => ActiveId == id;
    public bool IsHoverOrActive(string id) => Hovered?.Id == id || ActiveId == id;
}

/// <summary>
/// Reusable scroll container state + WPF-matching ScrollControls.xaml thumb rendering & dragging.
/// </summary>
internal sealed class UiScrollState
{
    public float Offset { get; set; }
    public float ContentHeight { get; set; }
    public float ViewportHeight { get; set; }
    public UiRect TrackRect { get; set; }
    public bool Dragging { get; private set; }
    private float _dragStartY;
    private float _dragStartOffset;

    public float MaxOffset => MathF.Max(0f, ContentHeight - ViewportHeight);

    public void Clamp() => Offset = Math.Clamp(Offset, 0f, MaxOffset);

    public void OnWheel(float deltaSteps)
    {
        Offset -= deltaSteps * 48f;
        Clamp();
    }

    public void EnsureVisible(float itemTop, float itemBottom, float padding = 8f)
    {
        if (ViewportHeight <= 0f) return;
        if (itemTop < Offset + padding) Offset = itemTop - padding;
        else if (itemBottom > Offset + ViewportHeight - padding) Offset = itemBottom - ViewportHeight + padding;
        Clamp();
    }

    public UiRect GetThumbRect()
    {
        if (MaxOffset <= 0.5f || TrackRect.IsEmpty || ContentHeight <= 0f) return default;
        float ratio = Math.Clamp(ViewportHeight / ContentHeight, 0.08f, 1f);
        float thumbH = MathF.Max(26f, TrackRect.Height * ratio);
        float travel = MathF.Max(1f, TrackRect.Height - thumbH);
        float thumbY = TrackRect.Y + travel * (Offset / MaxOffset);
        return new UiRect(TrackRect.X, thumbY, TrackRect.Width, thumbH);
    }

    public bool BeginDrag(float yDip)
    {
        var thumb = GetThumbRect();
        if (thumb.IsEmpty) return false;
        if (yDip >= thumb.Y && yDip <= thumb.Bottom)
        {
            Dragging = true;
            _dragStartY = yDip;
            _dragStartOffset = Offset;
            return true;
        }
        // Page jump on track click
        float targetRatio = Math.Clamp((yDip - TrackRect.Y - thumb.Height * 0.5f) / MathF.Max(1f, TrackRect.Height - thumb.Height), 0f, 1f);
        Offset = targetRatio * MaxOffset;
        Clamp();
        Dragging = true;
        _dragStartY = yDip;
        _dragStartOffset = Offset;
        return true;
    }

    public bool DragMove(float yDip)
    {
        if (!Dragging) return false;
        var thumb = GetThumbRect();
        float travel = MathF.Max(1f, TrackRect.Height - thumb.Height);
        float dy = yDip - _dragStartY;
        Offset = _dragStartOffset + (dy / travel) * MaxOffset;
        Clamp();
        return true;
    }

    public void EndDrag() => Dragging = false;

    public void Draw(D2dTarget g, UiThemePalette t, UiHitMap hit, string hitId, UiRect viewportRect)
    {
        ViewportHeight = viewportRect.Height;
        Clamp();
        if (MaxOffset <= 0.5f)
        {
            TrackRect = default;
            return;
        }

        TrackRect = new UiRect(viewportRect.Right - UiMetrics.ScrollBarWidth, viewportRect.Y + 2f, UiMetrics.ScrollBarWidth, MathF.Max(10f, viewportRect.Height - 4f));
        hit.Add(hitId, TrackRect, UiCursorKind.Arrow);
        var thumb = GetThumbRect();
        if (thumb.IsEmpty) return;

        bool hot = Dragging || hit.IsHover(hitId);
        float w = hot ? UiMetrics.ScrollThumbHoverWidth : UiMetrics.ScrollThumbWidth;
        UiColor col = Dragging
            ? t.Accent
            : hot
                ? t.TextPrimary.WithAlphaFraction(0.90f)
                : t.TextSecondary.WithAlphaFraction(0.65f);
        float cx = TrackRect.X + (TrackRect.Width - w) * 0.5f;
        g.FillRoundedRect(new UiRect(cx, thumb.Y, w, thumb.Height), UiMetrics.ScrollThumbRadius, col);
    }
}

/// <summary>
/// High-fidelity WPF-matching UI primitives (Cards, Buttons, ToggleSwitch, Sliders, Inputs, ComboBoxes).
/// </summary>
internal static class UiPrimitives
{
    public static void DrawCard(D2dTarget g, UiThemePalette t, UiRect rect, float radius = UiMetrics.CardCornerRadius, bool alt = false, bool highlightBorder = false)
    {
        g.FillRoundedRect(rect, radius, alt ? t.SurfaceAlt : t.SurfaceBgAlpha);
        g.DrawRoundedRect(rect, radius, highlightBorder ? t.Accent : t.Border, 1f);
    }

    public static void DrawAccentButton(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, string text,
        bool enabled = true, float fontSize = 12.5f, float radius = UiMetrics.ButtonCornerRadius, UiIcon? icon = null, string? tooltip = null)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand, tooltip);
        bool hot = enabled && hit.IsHover(id);
        bool down = enabled && hit.IsActive(id);
        UiColor bg = !enabled ? t.AccentDisabled : down ? t.AccentPressed : hot ? t.AccentHover : t.Accent;
        g.FillRoundedRect(rect, radius, bg);
        if (icon is UiIcon ic)
        {
            float iconSize = MathF.Min(15f, rect.Height - 10f);
            float padLeft = 10f;
            if (string.IsNullOrEmpty(text))
            {
                g.DrawIcon(ic, new UiRect(rect.CenterX - iconSize * 0.5f, rect.CenterY - iconSize * 0.5f, iconSize, iconSize), t.OnAccent, 1.8f);
                return;
            }
            g.DrawIcon(ic, new UiRect(rect.X + padLeft, rect.CenterY - iconSize * 0.5f, iconSize, iconSize), t.OnAccent, 1.8f);
            g.DrawText(text, new UiRect(rect.X + padLeft + iconSize + 6f, rect.Y, rect.Width - padLeft - iconSize - 14f, rect.Height),
                t.OnAccent, fontSize, 600, UiTextAlign.Center, UiVertAlign.Center);
        }
        else
        {
            g.DrawText(text, rect.Deflate(8f, 0f), t.OnAccent, fontSize, 600, UiTextAlign.Center, UiVertAlign.Center);
        }
    }

    public static void DrawSecondaryButton(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, string text,
        bool enabled = true, float fontSize = 12.5f, float radius = UiMetrics.ButtonCornerRadius,
        UiIcon? icon = null, bool active = false, bool danger = false, string? tooltip = null)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand, tooltip);
        bool hot = enabled && hit.IsHover(id);
        UiColor bg = active ? t.NavSelected : hot ? t.SurfaceAlt : t.SurfaceBg;
        UiColor border = active ? t.Accent : (hot && danger) ? t.Fail : hot ? t.Accent : t.Border;
        UiColor fg = !enabled ? t.TextSecondary.WithAlphaFraction(0.5f) : danger ? t.Fail : active ? t.Accent : t.TextPrimary;
        g.FillRoundedRect(rect, radius, bg);
        g.DrawRoundedRect(rect, radius, border, 1f);
        if (icon is UiIcon ic)
        {
            float iconSize = MathF.Min(15f, rect.Height - 10f);
            if (string.IsNullOrEmpty(text))
            {
                g.DrawIcon(ic, new UiRect(rect.CenterX - iconSize * 0.5f, rect.CenterY - iconSize * 0.5f, iconSize, iconSize), fg, 1.8f);
                return;
            }
            float pad = 10f;
            g.DrawIcon(ic, new UiRect(rect.X + pad, rect.CenterY - iconSize * 0.5f, iconSize, iconSize), fg, 1.8f);
            g.DrawText(text, new UiRect(rect.X + pad + iconSize + 6f, rect.Y, rect.Width - pad - iconSize - 14f, rect.Height),
                fg, fontSize, active ? 600 : 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
        else
        {
            g.DrawText(text, rect.Deflate(8f, 0f), fg, fontSize, active ? 600 : 400, UiTextAlign.Center, UiVertAlign.Center);
        }
    }

    public static void DrawGhostIconButton(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, UiIcon icon,
        float iconSize = 15f, UiColor? iconColor = null, bool active = false, bool enabled = true, string? tooltip = null, float radius = 6f)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand, tooltip);
        bool hot = enabled && hit.IsHover(id);
        if (active) g.FillRoundedRect(rect, radius, t.NavSelected);
        else if (hot) g.FillRoundedRect(rect, radius, t.NavHover);

        UiColor fg = iconColor ?? (active ? t.Accent : t.TextSecondary);
        if (!enabled) fg = fg.WithAlphaFraction(0.45f);
        var box = new UiRect(rect.CenterX - iconSize * 0.5f, rect.CenterY - iconSize * 0.5f, iconSize, iconSize);
        g.DrawIcon(icon, box, fg, 1.8f);
    }

    /// <summary>Verbatim ToggleControls.xaml AutomationSwitch (34x20 track, 16px thumb).</summary>
    public static void DrawSwitch(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, bool isChecked, bool enabled = true)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        bool hot = enabled && hit.IsHover(id);
        float w = 34f, h = 20f;
        float x = rect.CenterX - w * 0.5f;
        float y = rect.CenterY - h * 0.5f;
        var track = new UiRect(x, y, w, h);
        UiColor trackCol = isChecked ? t.Accent : t.SliderBg;
        if (hot) trackCol = trackCol.WithAlphaFraction(0.85f);
        if (!enabled) trackCol = trackCol.WithAlphaFraction(0.45f);
        g.FillRoundedRect(track, 10f, trackCol);

        float cx = isChecked ? track.Right - 2f - 8f : track.X + 2f + 8f;
        UiColor thumbCol = enabled ? t.OnAccent : t.OnAccent.WithAlphaFraction(0.65f);
        g.FillEllipse(cx, track.CenterY, 8f, 8f, thumbCol);
    }

    public static void DrawCheckBox(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, bool isChecked, string? label, float fontSize = 12.5f, bool enabled = true)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        bool hot = enabled && hit.IsHover(id);
        var box = new UiRect(rect.X, rect.CenterY - 8f, 16f, 16f);
        if (isChecked)
        {
            g.FillRoundedRect(box, 4f, t.Accent);
            g.DrawLine(box.X + 3.5f, box.Y + 8.2f, box.X + 6.8f, box.Y + 11.4f, t.OnAccent, 1.8f);
            g.DrawLine(box.X + 6.8f, box.Y + 11.4f, box.X + 12.4f, box.Y + 4.8f, t.OnAccent, 1.8f);
        }
        else
        {
            g.FillRoundedRect(box, 4f, t.SurfaceBg);
            g.DrawRoundedRect(box, 4f, hot ? t.Accent : t.Border, 1f);
        }
        if (!string.IsNullOrEmpty(label))
        {
            var textRect = new UiRect(box.Right + 8f, rect.Y, MathF.Max(0f, rect.Width - 24f), rect.Height);
            g.DrawText(label, textRect, enabled ? t.TextPrimary : t.TextSecondary, fontSize, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
    }

    public static void DrawRadioButton(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, bool selected, string label, float fontSize = 12.5f, bool enabled = true)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        bool hot = enabled && hit.IsHover(id);
        float cy = rect.CenterY;
        float cx = rect.X + 8f;
        g.FillEllipse(cx, cy, 7.5f, 7.5f, t.SurfaceBg);
        g.DrawEllipse(cx, cy, 7.5f, 7.5f, selected || hot ? t.Accent : t.Border, 1.3f);
        if (selected) g.FillEllipse(cx, cy, 4f, 4f, t.Accent);

        g.DrawText(label, new UiRect(rect.X + 23f, rect.Y, MathF.Max(0f, rect.Width - 23f), rect.Height),
            enabled ? t.TextPrimary : t.TextSecondary, fontSize, 400, UiTextAlign.Leading, UiVertAlign.Center);
    }

    /// <summary>
    /// Renders WPF App.xaml Slider / RowSlider (with optional live audio peak overlay and thumb).
    /// </summary>
    public static void DrawVolumeSlider(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect,
        float value01, float peak01, bool muted, bool showPeak,
        float trackHeight = 6f, float thumbDiameter = 14f, bool enabled = true)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        bool hot = enabled && hit.IsHoverOrActive(id);
        float pad = thumbDiameter * 0.5f;
        float barX = rect.X + pad;
        float barW = MathF.Max(4f, rect.Width - thumbDiameter);
        float barY = rect.CenterY - trackHeight * 0.5f;
        float r = trackHeight * 0.5f;

        var trackRect = new UiRect(barX, barY, barW, trackHeight);
        g.FillRoundedRect(trackRect, r, t.SliderBg);

        float clampedVal = Math.Clamp(value01, 0f, 1f);
        float fillW = barW * clampedVal;
        if (fillW > 0.5f)
        {
            UiColor fillCol = muted ? t.AccentInverse.WithAlphaFraction(0.55f) : t.Accent;
            g.FillRoundedRect(new UiRect(barX, barY, fillW, trackHeight), r, fillCol);
        }

        if (showPeak && peak01 > 0.01f && !muted && fillW > 0.5f)
        {
            float peakW = fillW * Math.Clamp(peak01, 0f, 1f);
            g.FillRoundedRect(new UiRect(barX, barY, peakW, trackHeight), r, t.AccentPeak);
        }

        float thumbX = barX + fillW;
        float rad = (hot ? thumbDiameter + 2f : thumbDiameter) * 0.5f;
        UiColor thumbCol = muted ? t.AccentInverse : t.Accent;
        if (!enabled) thumbCol = t.AccentDisabled;
        g.FillEllipse(thumbX, rect.CenterY, rad, rad, thumbCol);
    }

    /// <summary>Verbatim SonicRoute/PanelDevices.ShortName device pill truncation (head before '(', max 12 graphemes).</summary>
    public static string ShortDeviceName(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return "";
        int paren = full.IndexOf('(');
        string head = paren > 0 ? full[..paren].Trim() : full.Trim();
        if (head.Length <= 12) return head;
        int[] elements = System.Globalization.StringInfo.ParseCombiningCharacters(head);
        return elements.Length <= 12 ? head : head[..elements[11]] + "…";
    }

    public static int SliderValueFromX(UiRect rect, float mouseX, float thumbDiameter = 14f)
    {
        float pad = thumbDiameter * 0.5f;
        float barW = MathF.Max(1f, rect.Width - thumbDiameter);
        float t = Math.Clamp((mouseX - (rect.X + pad)) / barW, 0f, 1f);
        return (int)MathF.Round(t * 100f);
    }

    public static void DrawComboBox(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, string text, bool open = false, float fontSize = 12.5f, bool enabled = true)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        bool hot = enabled && hit.IsHover(id);
        g.FillRoundedRect(rect, UiMetrics.ComboCornerRadius, t.SurfaceBg);
        g.DrawRoundedRect(rect, UiMetrics.ComboCornerRadius, open || hot ? t.Accent : t.Border, 1f);
        g.DrawText(text, new UiRect(rect.X + 10f, rect.Y, MathF.Max(0f, rect.Width - 32f), rect.Height),
            enabled ? t.TextPrimary : t.TextSecondary, fontSize, 400, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(rect.Right - 22f, rect.CenterY - 5.5f, 11f, 11f), t.TextSecondary, 1.8f, open ? 180f : 0f);
    }

    public static void DrawTextBox(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, string value, string placeholder = "",
        bool focused = false, bool mono = false, float fontSize = 12.5f)
    {
        hit.Add(id, rect, UiCursorKind.IBeam);
        bool hot = hit.IsHover(id);
        g.FillRoundedRect(rect, 6f, t.SurfaceBg);
        g.DrawRoundedRect(rect, 6f, focused || hot ? t.Accent : t.Border, focused ? 1.5f : 1f);
        bool empty = string.IsNullOrEmpty(value);
        string shown = empty ? placeholder : value;
        if (!string.IsNullOrEmpty(shown))
        {
            g.DrawText(shown, rect.Deflate(9f, 2f), empty ? t.TextSecondary : t.TextPrimary,
                fontSize, 400, UiTextAlign.Leading, UiVertAlign.Center,
                wrap: false, ellipsis: true,
                fontFamily: mono ? UiMetrics.MonoFontFamily : UiMetrics.FontFamily);
        }
    }

    /// <summary>Verbatim SettingsControls.xaml SettingsSectionHeader (32px col w/ 20x20 Accent icon + 14.5 SemiBold).</summary>
    public static void DrawSettingsSectionHeader(D2dTarget g, UiThemePalette t, UiRect rect, UiIcon icon, string title)
    {
        g.DrawIcon(icon, new UiRect(rect.X, rect.CenterY - 10f, 20f, 20f), t.Accent, 1.8f);
        g.DrawText(title, new UiRect(rect.X + 32f, rect.Y, MathF.Max(0f, rect.Width - 32f), rect.Height),
            t.TextPrimary, 14.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
    }

    /// <summary>Verbatim SettingsControls.xaml SettingsSwitchRow (border bottom, 30px icon, label, 34px AutomationSwitch).</summary>
    public static void DrawSettingsSwitchRow(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, UiIcon icon, string title, bool isChecked, bool drawBottomBorder = true, bool enabled = true)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        if (enabled && hit.IsHover(id))
            g.FillRoundedRect(rect.Deflate(0f, 1f), 6f, t.NavHover.WithAlphaFraction(0.55f));
        UiColor iconCol = enabled ? t.TextSecondary : t.TextSecondary.WithAlphaFraction(0.5f);
        UiColor textCol = enabled ? t.TextPrimary : t.TextSecondary;
        g.DrawIcon(icon, new UiRect(rect.X + 2f, rect.CenterY - 8.5f, 17f, 17f), iconCol, 1.8f);
        g.DrawText(title, new UiRect(rect.X + 32f, rect.Y, MathF.Max(0f, rect.Width - 82f), rect.Height),
            textCol, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
        DrawSwitch(g, t, hit, id + ":sw", new UiRect(rect.Right - 36f, rect.CenterY - 12f, 34f, 24f), isChecked, enabled);
        if (drawBottomBorder)
            g.DrawLine(rect.X + 2f, rect.Bottom, rect.Right - 2f, rect.Bottom, t.Border, 1f, roundCaps: false);
    }

    public static void DrawBadge(D2dTarget g, UiThemePalette t, UiRect rect, string text, UiColor bg, UiColor fg, UiColor? border = null, float fontSize = 10.5f)
    {
        g.FillRoundedRect(rect, rect.Height * 0.5f, bg);
        if (border is UiColor b) g.DrawRoundedRect(rect, rect.Height * 0.5f, b, 1f);
        g.DrawText(text, rect.Deflate(6f, 0f), fg, fontSize, 600, UiTextAlign.Center, UiVertAlign.Center);
    }
}
