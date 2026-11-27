using System;
using System.Collections.Generic;

namespace SonicRoute.Native.Ui;

internal readonly record struct UiHitItem(
    string Id,
    UiRect Bounds,
    UiCursorKind Cursor = UiCursorKind.Hand,
    string? Tooltip = null);

internal readonly record struct UiRotatedRect(UiRect Rect, UiPoint Pivot, float Radians, float CornerRadius)
{
    public D2dMatrix3x2 Transform => D2dMatrix3x2.RotateAt(Radians, Pivot.X, Pivot.Y);

    public UiRect Bounds
    {
        get
        {
            var m = Transform;
            float halfWidth = (MathF.Abs(m.M11) * Rect.Width + MathF.Abs(m.M21) * Rect.Height) * 0.5f;
            float halfHeight = (MathF.Abs(m.M12) * Rect.Width + MathF.Abs(m.M22) * Rect.Height) * 0.5f;
            float centerX = Rect.CenterX * m.M11 + Rect.CenterY * m.M21 + m.Dx;
            float centerY = Rect.CenterX * m.M12 + Rect.CenterY * m.M22 + m.Dy;
            return new UiRect(centerX - halfWidth, centerY - halfHeight, halfWidth * 2f, halfHeight * 2f);
        }
    }

    public bool Contains(float x, float y)
    {
        (float sin, float cos) = MathF.SinCos(Radians);
        float dx = x - Pivot.X, dy = y - Pivot.Y;
        float localX = Pivot.X + dx * cos + dy * sin;
        float localY = Pivot.Y - dx * sin + dy * cos;
        if (!Rect.Contains(localX, localY)) return false;
        float cornerX = localX - Math.Clamp(localX, Rect.X + CornerRadius, Rect.Right - CornerRadius);
        float cornerY = localY - Math.Clamp(localY, Rect.Y + CornerRadius, Rect.Bottom - CornerRadius);
        return cornerX * cornerX + cornerY * cornerY <= CornerRadius * CornerRadius;
    }
}

/// <summary>
/// Per-frame hit-test registry populated during rendering in Z-order (last item added wins on overlap).
/// </summary>
internal sealed partial class UiHitMap
{
    private readonly List<(UiHitItem Item, UiRect Visible, int ShapeIndex)> _items = new(64);
    private List<UiRotatedRect>? _rotatedItems;
    private readonly Stack<UiRect> _clips = new();
    private readonly List<(UiScrollState State, UiRect View)> _scrollViews = new();
    private bool _hasTransform;
    private float _txScale = 1f;
    private float _txOffX;
    private float _txOffY;
    private UiScrollState? _keyboardScroll;
    private (UiHitItem Item, UiRotatedRect Shape)? _overlay;
    public UiHitItem? Hovered { get; private set; }
    public string? ActiveId { get; set; }
    public string? FocusId { get; private set; }
    public bool ShowFocus { get; private set; }

    public void FocusPointer(float x, float y)
    {
        _keyboardScroll = null;
        FocusId = HitTest(x, y)?.Id;
        ShowFocus = false;
    }

    public void HideFocus() => ShowFocus = false;

    private bool IsTabStop(int index)
    {
        var (item, visible, _) = _items[index];
        return item.Cursor is UiCursorKind.Hand or UiCursorKind.IBeam &&
            HitTest(visible.CenterX, visible.CenterY)?.Id == item.Id;
    }

    private List<string> TabOrder(UiRect? within = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();
        for (int i = 0; i < _items.Count; i++)
        {
            var (item, rect, _) = _items[i];
            if (within is UiRect region && !region.Contains(rect.CenterX, rect.CenterY)) continue;
            if (IsTabStop(i) && seen.Add(item.Id)) order.Add(item.Id);
        }
        return order;
    }

    public bool MoveFocus(bool reverse, Action render)
    {
        var order = TabOrder();
        if (order.Count == 0) return false;
        int index = FocusId == null ? -1 : order.IndexOf(FocusId);
        if (index < 0 && reverse) index = 0;
        string next = order[(index + order.Count + (reverse ? -1 : 1)) % order.Count];
        if (TryGetFocus(out _, out var current))
        {
            for (int i = 0; i < _scrollViews.Count; i++)
            {
                var (state, view) = _scrollViews[i];
                if (!view.Contains(current.CenterX, current.CenterY)) continue;
                if (reverse ? state.Offset <= 0 : state.Offset >= state.MaxOffset) continue;
                var old = TabOrder(view);
                if (old.Count == 0 || (reverse ? old[0] : old[^1]) != FocusId) continue;
                state.Offset += (reverse ? -1 : 1) * MathF.Max(1f, view.Height - 24f);
                state.Clamp();
                render();
                var revealed = TabOrder(view);
                if (reverse) revealed.Reverse();
                next = revealed.Find(id => !old.Contains(id)) ?? next;
                break;
            }
        }
        FocusId = next;
        ShowFocus = true;
        return true;
    }

    public bool TryGetFocus(out UiHitItem target, out UiRect visible)
    {
        for (int i = 0; FocusId != null && i < _items.Count; i++)
        {
            if (_items[i].Item.Id == FocusId && IsTabStop(i))
            {
                (target, visible, _) = _items[i];
                return true;
            }
        }
        target = default;
        visible = default;
        return false;
    }

    public void Clear()
    {
        _accessibleItems?.Clear();
        _rangeSetters?.Clear();
        _overlay = null;
        _items.Clear();
        _rotatedItems?.Clear();
        _clips.Clear();
        _scrollViews.Clear();
        _hasTransform = false;
        _txScale = 1f;
        _txOffX = 0f;
        _txOffY = 0f;
    }

    public void PushTransform(float scale, float offsetX, float offsetY)
    {
        _hasTransform = true;
        _txScale = scale;
        _txOffX = offsetX;
        _txOffY = offsetY;
    }

    public void PopTransform()
    {
        _hasTransform = false;
        _txScale = 1f;
        _txOffX = 0f;
        _txOffY = 0f;
    }

    internal float TransformScale => _hasTransform ? _txScale : 1f;

    internal UiRect MapRect(UiRect bounds) => _hasTransform
        ? new UiRect(bounds.X * _txScale + _txOffX, bounds.Y * _txScale + _txOffY, bounds.Width * _txScale, bounds.Height * _txScale)
        : bounds;

    public void RegisterScroll(UiScrollState state, UiRect view)
    {
        if (_clips.TryPeek(out var parent)) view = Intersect(view, parent);
        if (!view.IsEmpty) _scrollViews.Add((state, view));
    }

    public bool ScrollFocused(int key)
    {
        if (!ShowFocus || key is not (33 or 34 or 35 or 36)) return false;
        bool haveTarget = TryGetFocus(out _, out var bounds);
        // Inner lists draw their tracks before the outer page does.
        foreach (var (state, view) in _scrollViews)
        {
            if (haveTarget ? !view.Contains(bounds.CenterX, bounds.CenterY) : state != _keyboardScroll) continue;
            float before = state.Offset;
            state.Offset = key switch { 33 => before - view.Height, 34 => before + view.Height,
                35 => state.MaxOffset, _ => 0f };
            state.Clamp();
            if (state.Offset == before) continue;
            _keyboardScroll = state;
            return true;
        }
        return false;
    }

    public void PushClip(UiRect clip) =>
        _clips.Push(_clips.TryPeek(out var parent) ? Intersect(clip, parent) : clip);

    public void PopClip() => _clips.Pop();

    internal UiRect VisiblePart(UiRect bounds)
    {
        var mapped = MapRect(bounds);
        return _clips.TryPeek(out var clip) ? Intersect(mapped, clip) : mapped;
    }

    internal static UiRect Intersect(UiRect a, UiRect b)
    {
        float x = MathF.Max(a.X, b.X);
        float y = MathF.Max(a.Y, b.Y);
        return new UiRect(x, y, MathF.Min(a.Right, b.Right) - x, MathF.Min(a.Bottom, b.Bottom) - y);
    }

    public void Add(string id, UiRect bounds, UiCursorKind cursor = UiCursorKind.Hand, string? tooltip = null)
    {
        var mapped = MapRect(bounds);
        var visible = _clips.TryPeek(out var clip) ? Intersect(mapped, clip) : mapped;
        if (!visible.IsEmpty)
        {
            _items.Add((new UiHitItem(id, mapped, cursor, tooltip), visible, -1));
            if (_accessibleItems != null && cursor is UiCursorKind.Hand or UiCursorKind.IBeam &&
                !string.IsNullOrWhiteSpace(tooltip) && !_accessibleItems.ContainsKey(id))
                RecordAccessible(id, visible, tooltip, cursor == UiCursorKind.IBeam ? UiAccessibleRole.TextBox : UiAccessibleRole.Button, null, true);
        }
    }

    // Rotated controls are registered in window coordinates.
    public void AddRotated(string id, UiRotatedRect shape)
    {
        var bounds = shape.Bounds;
        var visible = _clips.TryPeek(out var clip) ? Intersect(bounds, clip) : bounds;
        if (visible.IsEmpty) return;
        var shapes = _rotatedItems ??= new(12);
        _items.Add((new UiHitItem(id, bounds), visible, shapes.Count));
        shapes.Add(shape);
    }

    public void SetRotatedOverlay(string id, UiRotatedRect shape, UiRect clip) =>
        _overlay = (new UiHitItem(id, Intersect(shape.Bounds, clip)), shape);

    public void ClearOverlay() => _overlay = null;

    public bool IsOverlayAt(float x, float y) =>
        _overlay is { } overlay && overlay.Item.Bounds.Contains(x, y) && overlay.Shape.Contains(x, y);

    public UiHitItem? HitTest(float x, float y)
    {
        if (IsOverlayAt(x, y)) return _overlay?.Item;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var (item, visible, index) = _items[i];
            if (item.Id == _overlay?.Item.Id || !visible.Contains(x, y)) continue;
            if (index < 0 || _rotatedItems![index].Contains(x, y)) return item;
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
    public bool IsActive(string id) => ActiveId == id && Hovered?.Id == id;
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
        float ratio = Math.Clamp(ViewportHeight / ContentHeight, 0f, 1f);
        float thumbH = MathF.Min(TrackRect.Height, MathF.Max(28f, TrackRect.Height * ratio));
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
        // WPF's track buttons issue PageUp/PageDown; only the thumb starts a drag.
        int direction = yDip < thumb.Y ? -1 : 1;
        Offset += direction * ViewportHeight;
        Clamp();
        LayeredWindow.BeginScrollRepeat(this, direction);
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
        hit.RegisterScroll(this, viewportRect);
        hit.Add(hitId, TrackRect, UiCursorKind.Arrow);
        var thumb = GetThumbRect();
        if (thumb.IsEmpty) return;

        bool hot = Dragging || hit.IsHover(hitId);
        float w = hot ? UiMetrics.ScrollThumbHoverWidth : UiMetrics.ScrollThumbWidth;
        UiColor col = t.HighContrastScrollInk is UiColor systemInk
            ? (Dragging ? t.HighContrastScrollHighlight : systemInk)
            : Dragging
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
    internal static float MeasureWrappedTextHeight(string text, float size, float width) =>
        MathF.Ceiling(D2dSharedContext.Instance.MeasureText(text, UiMetrics.FontFamily, size, 400, MathF.Max(1f, width), 2048f, true).Height);

    internal static float MeasureSettingsSwitchHeight(string title, string hint, float width)
    {
        float textWidth = width - 84f;
        float textHeight = MeasureWrappedTextHeight(title, 12.5f, textWidth);
        if (hint.Length != 0) textHeight += 3f + MeasureWrappedTextHeight(hint, 11.5f, textWidth);
        return 21f + MathF.Max(20f, textHeight);
    }

    internal static float DrawSettingsSwitchWithHint(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        string id, UiRect rect, UiIcon icon, string title, string hint, bool isChecked, bool drawBottomBorder,
        Action onClick, Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        register(id, rect, onClick, UiCursorKind.Hand, string.IsNullOrEmpty(hint) ? null : hint);
        float textW = MathF.Max(1f, rect.Width - 84f);
        float titleH = MeasureWrappedTextHeight(title, 12.5f, textW);
        float hintH = hint.Length == 0 ? 0f : MeasureWrappedTextHeight(hint, 11.5f, textW);
        float textY = rect.CenterY - (titleH + (hintH == 0f ? 0f : 3f + hintH) + 1f) * .5f;
        g.DrawIcon(icon, new UiRect(rect.X + 2f, rect.CenterY - 8.5f, 17f, 17f), t.TextSecondary, 1.8f);
        g.DrawText(title, new UiRect(rect.X + 32f, textY, textW, titleH), t.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
        if (hintH > 0)
            g.DrawText(hint, new UiRect(rect.X + 32f, textY + titleH + 3f, textW, hintH), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
        DrawSwitch(g, t, hit, id, new UiRect(rect.Right - 36f, rect.CenterY - 12f, 34f, 24f), isChecked);
        hit.DescribeControl(id, rect, title, UiAccessibleRole.CheckBox, isChecked, true);
        if (drawBottomBorder)
            g.DrawLine(rect.X, rect.Bottom - .5f, rect.Right, rect.Bottom - .5f, t.Border, 1f, roundCaps: false);
        return rect.Bottom;
    }

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
        hit.DescribeControl(id, rect, string.IsNullOrEmpty(text) ? tooltip : text, UiAccessibleRole.Button, null, enabled);
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
        hit.DescribeControl(id, rect, string.IsNullOrEmpty(text) ? tooltip : text, UiAccessibleRole.Button, active ? true : null, enabled);
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
        hit.DescribeControl(id, rect, tooltip, UiAccessibleRole.Button, active ? true : null, enabled);
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
        UiColor thumbCol = t.OnAccent.WithAlphaFraction(!enabled ? 0.45f : hot ? 0.85f : 1f);
        g.FillEllipse(cx, track.CenterY, 8f, 8f, thumbCol);
    }

    public static void DrawCheckBox(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, bool isChecked, string? label, float fontSize = 12.5f, bool enabled = true)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        hit.DescribeControl(id, rect, label, UiAccessibleRole.CheckBox, isChecked, enabled);
        bool hot = enabled && hit.IsHover(id);
        if (hot) g.FillRoundedRect(rect, 7f, t.NavHover);
        float opacity = enabled ? 1f : 0.45f;
        var box = new UiRect(rect.X + 8f, rect.CenterY - 9f, 18f, 18f);
        if (isChecked)
        {
            g.FillRoundedRect(box, 5f, t.Accent.WithAlphaFraction(opacity));
            var ink = t.OnAccent.WithAlphaFraction(opacity);
            g.DrawLine(box.CenterX - 3.5f, box.CenterY, box.CenterX - 0.5f, box.CenterY + 3f, ink, 2f);
            g.DrawLine(box.CenterX - 0.5f, box.CenterY + 3f, box.CenterX + 3.5f, box.CenterY - 2f, ink, 2f);
        }
        else
        {
            g.DrawRoundedRect(box.Deflate(0.8f), 5f, (hot ? t.Accent : t.Border).WithAlphaFraction(opacity), 1.6f);
        }
        if (!string.IsNullOrEmpty(label))
        {
            g.DrawText(label, rect.Deflate(35f, 0f, 8f, 0f), t.TextPrimary.WithAlphaFraction(opacity), fontSize, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
    }

    public static void DrawRadioButton(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, bool selected, string label, float fontSize = 12.5f, bool enabled = true)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        hit.DescribeControl(id, rect, label, UiAccessibleRole.RadioButton, selected, enabled);
        bool hot = enabled && hit.IsHover(id);
        float cy = rect.CenterY, cx = rect.X + 16.5f;
        float opacity = enabled ? 1f : 0.45f;
        if (hot) g.FillRoundedRect(rect, 7f, t.NavHover);
        if (selected) g.FillEllipse(cx, cy, 8.5f, 8.5f, t.Accent.WithAlphaFraction(opacity));
        else g.DrawEllipse(cx, cy, 7.7f, 7.7f, (hot ? t.Accent : t.Border).WithAlphaFraction(opacity), 1.6f);
        g.DrawText(label, rect.Deflate(34f, 0f, 8f, 0f), t.TextPrimary.WithAlphaFraction(opacity), fontSize, 400, UiTextAlign.Leading, UiVertAlign.Center);
    }

    internal const float MinimumVisiblePeak = 0.01f;

    internal static bool UpdateVolumePeak(ref float peak, float measured)
    {
        float before = peak;
        if (!float.IsFinite(measured)) measured = 0f;
        peak = MathF.Max(measured, before * 0.72f);
        if (peak < 0.005f) peak = 0f;
        return HasVolumePeakChanged(before, peak);
    }

    internal static bool HasVolumePeakChanged(float drawn, float peak) =>
        (drawn > MinimumVisiblePeak) != (peak > MinimumVisiblePeak) || MathF.Abs(peak - drawn) > 0.008f;

    public static void DrawVolumeSlider(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect,
        float value01, float peak01, bool muted, bool showPeak, bool enabled = true, bool compact = false, bool dimUnavailable = false) =>
        DrawSlider(g, t, hit, id, rect, value01, peak01, muted, showPeak, enabled, outlinedThumb: !compact, dimUnavailable);

    public static void DrawValueSlider(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect,
        float value01, bool enabled = true) =>
        DrawSlider(g, t, hit, id, rect, value01, 0f, muted: false, showPeak: false, enabled, outlinedThumb: true);

    private static void DrawSlider(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect,
        float value01, float peak01, bool muted, bool showPeak, bool enabled, bool outlinedThumb, bool dimUnavailable = false)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand);
        bool hot = enabled && hit.IsHoverOrActive(id);
        var track = SliderTrack(rect, outlinedThumb);
        float r = track.Height * 0.5f;
        g.FillRoundedRect(track, r, t.SliderBg);

        float clampedVal = Math.Clamp(value01, 0f, 1f);
        float fillW = track.Width * clampedVal;
        if (fillW > 0.5f)
        {
            UiColor fillCol = dimUnavailable ? t.AccentDisabled : muted ? t.AccentInverse.WithAlphaFraction(0.55f) : t.Accent;
            g.FillRoundedRect(track with { Width = fillW }, r, fillCol);
        }

        if (showPeak && !dimUnavailable && peak01 > MinimumVisiblePeak && !muted && fillW > 0.5f)
        {
            float peakW = fillW * Math.Clamp(peak01, 0f, 1f);
            g.FillRoundedRect(track with { Width = peakW }, r, t.AccentPeak);
        }

        float thumbX = track.X + fillW;
        UiColor thumbCol = dimUnavailable ? t.AccentDisabled : muted ? t.AccentInverse : t.Accent;
        if (outlinedThumb)
        {
            // App.xaml uses a 28 DIP hit thumb, a 17 DIP ellipse and a 2 DIP surface stroke.
            g.FillEllipse(thumbX, rect.CenterY + 1f, 10f, 10f, new UiColor(0, 0, 0, 22));
            g.FillEllipse(thumbX, rect.CenterY + 1f, 9f, 9f, new UiColor(0, 0, 0, 32));
            g.FillEllipse(thumbX, rect.CenterY, 8.5f, 8.5f, t.SurfaceBg);
            if (enabled && hot) thumbCol = hit.ActiveId == id ? t.AccentPressed : t.AccentHover;
            g.FillEllipse(thumbX, rect.CenterY, 6.5f, 6.5f, thumbCol);
            return;
        }
        float rad = hot ? 8f : 7f;
        if (!enabled) thumbCol = t.AccentDisabled;
        g.FillEllipse(thumbX, rect.CenterY, rad, rad, thumbCol);
    }

    /// <summary>Verbatim SonicRoute/PanelDevices.ShortName device pill truncation (head before '(', max 12 graphemes).</summary>
    public static string ShortDeviceName(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return "";
        int paren = full.IndexOf('(');
        string head = paren > 0 ? full[..paren].Trim() : full;
        if (head.Length <= 12) return head;
        int[] elements = System.Globalization.StringInfo.ParseCombiningCharacters(head);
        return elements.Length <= 12 ? head : head[..elements[11]] + "…";
    }

    private static UiRect SliderTrack(UiRect rect, bool outlinedThumb)
    {
        float inset = outlinedThumb ? 14f : 7f;
        return new UiRect(rect.X + inset, rect.CenterY - 2f, MathF.Max(1f, rect.Width - inset * 2f), 4f);
    }

    public static int VolumeSliderValueFromX(UiRect rect, float mouseX, bool compact = false) =>
        SliderValueFromX(rect, mouseX, outlinedThumb: !compact);

    public static int ValueSliderValueFromX(UiRect rect, float mouseX) => SliderValueFromX(rect, mouseX, outlinedThumb: true);

    private static int SliderValueFromX(UiRect rect, float mouseX, bool outlinedThumb)
    {
        var track = SliderTrack(rect, outlinedThumb);
        float t = Math.Clamp((mouseX - track.X) / track.Width, 0f, 1f);
        return (int)MathF.Round(t * 100f);
    }

    public static void DrawComboBox(D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, string text,
        bool open = false, float fontSize = 12.5f, bool enabled = true, bool showTooltip = true,
        ComboAppIcon? appIcon = null, Action? onAppIconLoaded = null, string? tooltip = null)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand, showTooltip ? tooltip ?? text : null);
        hit.DescribeControl(id, rect, text, UiAccessibleRole.ComboBox, null, enabled);
        bool hot = enabled && hit.IsHover(id);
        float opacity = enabled ? 1f : 0.5f;
        g.FillRoundedRect(rect, UiMetrics.ComboCornerRadius, t.SurfaceBg.WithAlphaFraction(opacity));
        UiColor edge = open || hot || (hit.ShowFocus && hit.FocusId == id) ? t.Accent : t.Border;
        g.DrawRoundedRect(rect, UiMetrics.ComboCornerRadius, edge.WithAlphaFraction(opacity), 1f);
        UiColor ink = t.TextPrimary.WithAlphaFraction(opacity);
        var label = new UiRect(rect.X + 10f, rect.Y, MathF.Max(0f, rect.Width - 32f), rect.Height);
        if (appIcon is { } icon)
        {
            var iconRect = new UiRect(label.X, label.CenterY - 8f, 16f, 16f);
            if (!hit.VisiblePart(iconRect).IsEmpty) icon.Draw(g, iconRect, ink, onAppIconLoaded, opacity);
            label = label.Deflate(22f, 0, 0, 0);
        }
        g.DrawText(text, label, ink, fontSize, 400, UiTextAlign.Leading, UiVertAlign.Center);
        float arrowRight = rect.Right - 12f;
        float arrowTop = rect.CenterY - 2f;
        var arrowColor = t.TextSecondary.WithAlphaFraction(opacity);
        g.DrawLine(arrowRight - 8f, arrowTop, arrowRight - 4f, arrowTop + 4f, arrowColor, 1.5f);
        g.DrawLine(arrowRight - 4f, arrowTop + 4f, arrowRight, arrowTop, arrowColor, 1.5f);
    }

    public static void DrawTextBox(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect, string value, string placeholder = "",
        bool focused = false, bool mono = false, float fontSize = 13f, bool search = false, bool readOnlyMultiline = false,
        bool enabled = true, bool centered = false)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.IBeam);
        hit.DescribeControl(id, rect, placeholder, UiAccessibleRole.TextBox, null, enabled);
        bool hot = enabled && hit.IsHover(id);
        g.FillRoundedRect(rect, 8f, readOnlyMultiline ? t.SurfaceAlt : t.SurfaceBg);
        g.DrawRoundedRect(rect, 8f, focused || hot ? t.Accent : t.Border, 1f);
        if (search) g.DrawIcon(UiIcon.Search, new UiRect(rect.X + 10, rect.CenterY - 8, 16, 16), t.TextSecondary);
        var textBox = rect.Deflate(search ? 36f : 11f, 2f, 11f, 2f);
        if (focused && g.Editor is { } editor)
        {
            editor.SetLayout(
                hit.MapRect(rect),
                hit.MapRect(textBox),
                hit.VisiblePart(rect),
                Math.Clamp(fontSize * hit.TransformScale, 9f, 24f),
                mono,
                placeholder);
            return;
        }
        bool empty = string.IsNullOrEmpty(value);
        string shown = empty ? placeholder : value;
        if (!string.IsNullOrEmpty(shown))
        {
            var ink = empty ? t.TextSecondary : t.TextPrimary;
            g.DrawText(shown, textBox, enabled ? ink : ink.WithAlphaFraction(0.5f),
                fontSize, 400, centered ? UiTextAlign.Center : UiTextAlign.Leading, readOnlyMultiline ? UiVertAlign.Top : UiVertAlign.Center,
                wrap: readOnlyMultiline, ellipsis: !readOnlyMultiline,
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
    public static void DrawBadge(D2dTarget g, UiThemePalette t, UiRect rect, string text, UiColor bg, UiColor fg, UiColor? border = null, float fontSize = 10.5f)
    {
        g.FillRoundedRect(rect, rect.Height * 0.5f, bg);
        if (border is UiColor b) g.DrawRoundedRect(rect, rect.Height * 0.5f, b, 1f);
        g.DrawText(text, rect.Deflate(6f, 0f), fg, fontSize, 600, UiTextAlign.Center, UiVertAlign.Center);
    }
}
