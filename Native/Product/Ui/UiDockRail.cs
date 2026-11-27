using System;
using System.Diagnostics;

namespace SonicRoute.Native.Ui;

internal readonly record struct UiItemIcon(
    NativeAppIconService.IconBitmap? Bitmap, UiIcon? Glyph = null, string? Initial = null, float GlyphInset = 0.19f)
{
    internal void Render(D2dTarget g, UiThemePalette t, UiRect rect)
    {
        if (Bitmap is { } b)
            g.DrawBgraBitmap(b.BgraPixels, b.BgraPixels, b.Width, b.Height, rect);
        else if (Glyph is { } glyph)
            g.DrawIcon(glyph, rect.Deflate(rect.Width * GlyphInset), t.TextSecondary, 1.6f);
        else
            g.DrawText(Initial ?? "?", rect, t.TextPrimary, rect.Height * 0.48f, 600, UiTextAlign.Center, UiVertAlign.Center);
    }
}

internal sealed class UiDockRail(string itemPrefix, Action invalidate)
{
    internal const float CardSize = 40f;
    internal const float Width = 39f;
    internal const float PivotInset = 12f;
    internal const float IconInset = 5f;
    private const float RowHeight = 51f;
    private const float MinRowHeight = 44f;
    private const float CornerRadius = 11f;
    private const float TileAngle = -12f * MathF.PI / 180f;
    private const float Slide = 10f;
    private const float IconSide = 23f;
    private const float TitleInset = 40f;
    private const float TitlePadding = 14f;
    private const float MinTitleWidth = 23f;
    private const float MaxTitleWidth = 204f;
    private const float TitleFont = 16.5f;
    private const float DeviceTitleFont = 14.5f;
    private const float StatusFont = 11f;

    internal readonly record struct Item(string Id, string Label, UiItemIcon Icon,
        bool Primary, bool Selected, bool Offline, string? Status = null);

    private readonly UiScrollState _scroll = new();
    private float _rowHeight = RowHeight;
    private UiRect _listRect;
    private UiHitMap? _hit;
    private bool _allowExpansion;
    private string? _hoverId;
    private string? _liftedId;
    private Item _liftedItem;
    private UiPoint _liftedCenter;
    private float _liftedSize;
    private float _liftedWidth;
    private float _expansion;
    private float _from;
    private long _animationStarted;

    internal bool NeedsAnimationTick => _animationStarted != 0;
    internal UiRect OverlayBounds { get; private set; }
    internal string? OverlayFocusId { get; private set; }

    internal bool OwnsItem(string? id) => id?.StartsWith(itemPrefix, StringComparison.Ordinal) == true;

    internal bool HandlesWheel(UiHitMap? hit, float x, float y) =>
        OwnsItem(hit?.HitTest(x, y)?.Id) || (_scroll.MaxOffset > 0f && _listRect.Contains(x, y));

    internal bool Scroll(float delta)
    {
        float before = _scroll.Offset;
        _scroll.OnWheel(delta);
        if (_scroll.Offset == before) return false;
        ResetHover();
        return true;
    }

    internal void ResetScroll() => _scroll.Offset = 0f;

    internal void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect area, int count,
        Func<int, Item> getItem, bool allowExpansion = true)
    {
        _hit = hit;
        _allowExpansion = allowExpansion;
        OverlayBounds = default;
        OverlayFocusId = null;

        float rowHeight = Math.Clamp(MathF.Floor(area.Height / Math.Max(1, count)), MinRowHeight, RowHeight);
        if (rowHeight != _rowHeight)
        {
            _scroll.Offset *= rowHeight / _rowHeight;
            _rowHeight = rowHeight;
        }
        float visibleRows = MathF.Min(count, MathF.Floor(area.Height / rowHeight));
        _listRect = new UiRect(area.X, area.Y, Width, visibleRows * rowHeight);
        _scroll.ViewportHeight = _listRect.Height;
        _scroll.ContentHeight = count * rowHeight;
        _scroll.Clamp();

        int first = (int)MathF.Floor(_scroll.Offset / rowHeight);
        int next = Math.Min(count, (int)MathF.Ceiling((_scroll.Offset + _listRect.Height) / rowHeight));
        float size = CardSize + rowHeight - RowHeight;
        string? raisedId = allowExpansion ? _liftedId ?? (hit.ShowFocus ? hit.FocusId : null) : null;

        // Clip the buried side even when the foreground navigation is translucent.
        g.PushClip(_listRect);
        hit.PushClip(_listRect);
        for (int i = first; i < next; i++)
        {
            var item = getItem(i);
            float y = _listRect.Y + i * rowHeight - _scroll.Offset;
            var center = new UiPoint(_listRect.X + PivotInset, y + rowHeight * 0.5f);
            var compact = GetShape(center, size, 0f, size);
            hit.AddRotated(item.Id, compact);
            hit.Describe(item.Id, item.Status == null ? item.Label : item.Label + " · " + item.Status, UiAccessibleRole.ListItem, item.Selected);
            if (item.Id == raisedId)
            {
                const float overlayPadding = 4f;
                float titleFont = item.Status == null ? TitleFont : DeviceTitleFont;
                float titleWidth = g.Shared.MeasureText(item.Label, UiMetrics.FontFamily, titleFont, 600,
                    MaxTitleWidth, size, wrap: false).Width;
                if (item.Status is { } status)
                    titleWidth = MathF.Max(titleWidth, g.Shared.MeasureText(status, UiMetrics.FontFamily, StatusFont, 400,
                        MaxTitleWidth, size, wrap: false).Width);
                float expandedLeft = center.X + Slide - size * 0.5f;
                float availableTitleWidth = area.Right - expandedLeft - TitleInset - TitlePadding - overlayPadding;
                float maxTitleWidth = Math.Clamp(availableTitleWidth, MinTitleWidth, MaxTitleWidth);
                _liftedWidth = TitleInset + TitlePadding + Math.Clamp(MathF.Ceiling(titleWidth), MinTitleWidth, maxTitleWidth);
                float width = MathF.Min(area.Right, expandedLeft + _liftedWidth + overlayPadding) - _listRect.X;
                OverlayBounds = UiHitMap.Intersect(new UiRect(_listRect.X, y, width, rowHeight), _listRect with { Width = width });
                OverlayFocusId = item.Id;
                _liftedItem = item;
                _liftedSize = size;
                _liftedCenter = center;
            }
            else DrawCard(g, t, item, compact, size, 0f, size);
        }
        hit.PopClip();
        g.PopClip();
        if (_scroll.MaxOffset > 0f) hit.RegisterScroll(_scroll, _listRect);
        if (OverlayBounds.IsEmpty && _liftedId != null) ResetHover();
    }

    internal void Hide()
    {
        ResetHover();
        _allowExpansion = false;
        _listRect = default;
        OverlayBounds = default;
        OverlayFocusId = null;
    }

    internal void RenderOverlay(D2dTarget g, UiThemePalette t)
    {
        var bounds = OverlayBounds;
        if (bounds.IsEmpty || OverlayFocusId is not { } id) return;
        g.PushClip(bounds);
        float expansion = id == _liftedId ? _expansion : 1f;
        var shape = GetShape(_liftedCenter, _liftedSize, expansion, _liftedWidth);
        DrawCard(g, t, _liftedItem, shape, _liftedSize, expansion, _liftedWidth);
        if (_hit is { } hit)
        {
            hit.SetRotatedOverlay(id, shape, bounds);
            if (hit.ShowFocus && hit.FocusId == id)
            {
                var ring = shape.Rect.Deflate(2f);
                g.DrawRoundedRect(ring, shape.CornerRadius - 2f, UiColor.White, 2f);
                g.DrawRoundedRect(ring, shape.CornerRadius - 2f, UiColor.Black, 1f);
            }
        }
        g.PopClip();
    }

    private static UiRotatedRect GetShape(UiPoint center, float size, float expansion, float maxWidth)
    {
        float remaining = 1f - expansion;
        float stretch = Math.Clamp((expansion - 0.24f) / 0.76f, 0f, 1f);
        var pivot = center with { X = center.X + Slide * expansion };
        return new UiRotatedRect(new UiRect(pivot.X - size * 0.5f, pivot.Y - size * 0.5f,
            size + (maxWidth - size) * stretch * stretch, size), pivot, TileAngle * remaining * remaining, CornerRadius);
    }

    private static void DrawCard(D2dTarget g, UiThemePalette t, Item item, UiRotatedRect shape,
        float size, float expansion, float maxWidth)
    {
        var rect = shape.Rect;
        if (shape.Radians != 0f) g.PushTransform(shape.Transform);
        DrawPlate(g, t, rect, item.Selected, item.Primary);
        if (shape.Radians != 0f) g.PopTransform();

        float iconX = shape.Pivot.X + IconInset * (1f - expansion);
        float iconSide = MathF.Min(IconSide, size - 12f);
        item.Icon.Render(g, t, new UiRect(iconX - iconSide * 0.5f, rect.CenterY - iconSide * 0.5f, iconSide, iconSide));
        if (item.Status != null && !item.Offline)
            g.FillEllipse(iconX + 8f, rect.CenterY + 8f, 2.75f, 2.75f, t.Success);

        if (rect.Width <= TitleInset + TitlePadding) return;
        float opacity = Math.Clamp((expansion - 0.43f) / 0.48f, 0f, 1f);
        var textClip = rect.Deflate(TitleInset, 1f, TitlePadding, 1f);
        g.PushClip(textClip);
        var textRect = textClip with { Width = maxWidth - TitleInset - TitlePadding };
        var titleInk = (item.Offline ? t.TextSecondary : t.TextPrimary).WithAlphaFraction(opacity);
        if (item.Status is { } status)
        {
            g.DrawText(item.Label, new UiRect(textRect.X, rect.CenterY - 16f, textRect.Width, 18f), titleInk, DeviceTitleFont, 600);
            g.DrawText(status, new UiRect(textRect.X, rect.CenterY + 2f, textRect.Width, 15f),
                t.TextSecondary.WithAlphaFraction(opacity), StatusFont);
        }
        else g.DrawText(item.Label, textRect, titleInk, TitleFont, 600);
        g.PopClip();
    }

    internal static void DrawPlate(D2dTarget g, UiThemePalette t, UiRect rect, bool selected, bool primary, bool hot = false)
    {
        var surface = selected ? t.NavSelected : hot ? t.NavHover : UiColor.Blend(t.SurfaceBg, t.SurfaceAlt, 0.45);
        var edge = primary ? UiColor.Blend(t.Border, t.Accent, 0.50) : t.Border;
        g.FillRoundedRect(rect.Offset(0f, 1.8f), CornerRadius, UiColor.Black.WithAlpha(t.IsDark ? (byte)40 : (byte)15));
        g.FillRoundedRect(rect, CornerRadius, surface);
        g.DrawRoundedRect(rect, CornerRadius, edge, 1f);
    }

    internal bool UpdateHover(string? target)
    {
        string? next = _allowExpansion && OwnsItem(target) ? target : null;
        if (next == _hoverId) return false;
        bool wasOpening = _hoverId == _liftedId;
        _hoverId = next;
        bool allowAnimation = LayeredWindow.ClientAreaAnimationsEnabled();
        if (!allowAnimation || _liftedId == null)
        {
            Lift(next, animate: allowAnimation && next != OverlayFocusId);
            return true;
        }
        bool opening = next == _liftedId;
        if (!opening && _expansion == 0f)
        {
            Lift(next, animate: true);
            return true;
        }
        if (opening == wasOpening && NeedsAnimationTick) return false;
        _from = _expansion;
        _animationStarted = Stopwatch.GetTimestamp();
        return false;
    }

    internal void TickAnimation()
    {
        if (!NeedsAnimationTick) return;
        bool opening = _liftedId == _hoverId;
        float duration = opening ? 155f : 105f;
        float t = Math.Clamp((float)Stopwatch.GetElapsedTime(_animationStarted).TotalMilliseconds / duration, 0f, 1f);
        float eased = 1f - (1f - t) * (1f - t) * (1f - t);
        _expansion = _from + ((opening ? 1f : 0f) - _from) * eased;
        if (t < 1f) return;
        _animationStarted = 0;
        if (opening) return;
        Lift(_hoverId, animate: true);
        invalidate();
    }

    private void Lift(string? id, bool animate)
    {
        _liftedId = id;
        OverlayFocusId = null;
        _expansion = _from = id == null || animate ? 0f : 1f;
        _animationStarted = id != null && animate ? Stopwatch.GetTimestamp() : 0;
        _liftedItem = default;
        OverlayBounds = default;
        _hit?.ClearOverlay();
    }

    internal bool ResetHover()
    {
        if (_liftedId == null) return false;
        _hoverId = null;
        Lift(null, animate: false);
        return true;
    }
}
