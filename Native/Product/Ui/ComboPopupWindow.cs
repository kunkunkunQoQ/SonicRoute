using System;
using System.Collections.Generic;

namespace SonicRoute.Native.Ui;

internal readonly record struct ComboChoice(string Key, string Label, UiIcon? Icon = null);

/// <summary>
/// Floating scrollable dropdown popup matching WPF App.xaml RoundedComboBox + RoundedComboItem.
/// Supports full keyboard navigation, type-ahead prefix cycling, auto-widening, and close notifications.
/// </summary>
internal sealed class ComboPopupWindow : LayeredWindow
{
    private const float ItemH = 39f;
    private const float Pad = 1f;
    private const float MaxListHeight = 320f;
    private readonly LayeredWindow _owner;

    private UiThemePalette _theme;
    private readonly IReadOnlyList<ComboChoice> _choices;
    private readonly string _selectedKey;
    private readonly Action<ComboChoice> _onSelected;
    private readonly UiScrollState _scroll = new();
    private readonly UiHitMap _hit = new();
    private int _hoverIndex = -1;
    private int _focusedIndex = -1;
    private int _pressedIndex = -1;
    private string _typeAhead = "";
    private long _lastTypeTick;

    public static long LastClosedTick { get; private set; }
    public event Action? Closed;

    private ComboPopupWindow(
        UiThemePalette theme,
        IReadOnlyList<ComboChoice> choices,
        string selectedKey,
        Action<ComboChoice> onSelected,
        LayeredWindow owner)
        : base(180f, 120f, 100f, 36f)
    {
        _theme = theme;
        _choices = choices;
        _selectedKey = selectedKey;
        _onSelected = onSelected;
        TopMost = true;
        ToolWindow = true;
        DestroyOnClose = true;
        _owner = owner;
        OwnerHandle = owner.Handle;
    }

    internal static void Close(ref ComboPopupWindow? current)
    {
        var popup = current;
        current = null;
        if (popup == null || popup.IsDisposed) return;
        popup.CloseWindow();
        popup.Dispose();
    }

    public static ComboPopupWindow ShowFor(
        UiThemePalette theme,
        LayeredWindow owner,
        UiRect comboRectDip,
        IReadOnlyList<ComboChoice> choices,
        string selectedKey,
        Action<ComboChoice> onSelected)
    {
        var pop = new ComboPopupWindow(theme, choices, selectedKey, onSelected, owner);
        var pt = owner.DipToScreenPx(comboRectDip.X, comboRectDip.Bottom + 4f);
        GetMonitorWorkAreaForPoint(pt.X, pt.Y, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
        float scale = monDpi / 96f;
        float workW = MathF.Max(100f, (mr - ml - 8) / scale);

        var ctx = D2dSharedContext.Instance;
        float maxLabelW = 0f;
        for (int i = 0; i < choices.Count; i++)
        {
            float lw = ctx.MeasureText(choices[i].Label, UiMetrics.FontFamily, 13f, 400, workW, ItemH, false).Width;
            if (choices[i].Icon.HasValue) lw += 20f;
            if (lw > maxLabelW) maxLabelW = lw;
        }
        float contentH = choices.Count * ItemH;
        float viewH = Math.Clamp(contentH, 28f, MathF.Max(28f, MathF.Min(MaxListHeight, (mb - mt - 8) / scale) - Pad * 2f));
        // Outer border, both 4 DIP row margins, 10 DIP text insets, and the track gutter.
        float padW = Pad * 2 + 8f + 20f + 1f + (contentH > viewH ? UiMetrics.ScrollBarWidth + 2f : 0);
        float wDip = MathF.Min(workW, MathF.Max(comboRectDip.Width, MathF.Ceiling(maxLabelW) + padW));
        float hDip = viewH + Pad * 2f;

        pop._scroll.ContentHeight = contentH;
        pop._scroll.ViewportHeight = viewH;

        for (int i = 0; i < choices.Count; i++)
        {
            if (choices[i].Key == selectedKey)
            {
                pop._focusedIndex = i;
                pop._scroll.Offset = i * ItemH;
                pop._scroll.Clamp();
                break;
            }
        }

        int wPx = (int)MathF.Round(wDip * scale);
        int hPx = (int)MathF.Round(hDip * scale);
        int xPx = Math.Clamp(pt.X, ml + 4, Math.Max(ml + 4, mr - wPx - 4));
        int yPx = pt.Y;
        if (yPx + hPx > mb - 4)
        {
            var above = owner.DipToScreenPx(comboRectDip.X, comboRectDip.Y - 4f);
            yPx = Math.Max(mt + 4, above.Y - hPx);
        }

        pop.ResizeDip(wDip, hDip);
        pop.MovePx(xPx, yPx);
        pop.Show(activate: true);
        return pop;
    }

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        Invalidate();
    }

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        var outer = new UiRect(1f, 1f, widthDip - 2f, heightDip - 2f);
        g.FillRoundedRect(outer, 9f, _theme.SurfaceBg);
        g.DrawRoundedRect(outer, 9f, _theme.Border, 1f);

        var view = new UiRect(Pad, Pad, widthDip - Pad * 2f, heightDip - Pad * 2f);
        _scroll.ViewportHeight = view.Height;
        _scroll.ContentHeight = _choices.Count * ItemH;
        _scroll.Clamp();

        float itemW = _scroll.MaxOffset > 0.5f ? view.Width - UiMetrics.ScrollBarWidth - 2f : view.Width;
        g.PushClip(view);
        for (int i = 0; i < _choices.Count; i++)
        {
            float iy = view.Y + i * ItemH - _scroll.Offset;
            if (iy + ItemH < view.Y || iy > view.Bottom) continue;
            var r = new UiRect(view.X, iy, itemW, ItemH);
            bool sel = _choices[i].Key == _selectedKey;
            bool hot = i == _hoverIndex || i == _focusedIndex;
            var inner = r.Deflate(4f);
            if (sel || hot) g.FillRoundedRect(inner, 7f, _theme.Accent);
            UiColor ink = sel || hot ? _theme.OnAccent : _theme.TextPrimary;
            var label = inner.Deflate(10f, 0f);
            if (_choices[i].Icon is UiIcon icon)
            {
                g.DrawIcon(icon, new UiRect(label.X, label.CenterY - 7f, 14f, 14f), ink);
                label = label.Deflate(20f, 0, 0, 0);
            }
            g.DrawText(_choices[i].Label, label, ink, 13f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
        g.PopClip();

        _scroll.Draw(g, _theme, _hit, "combo.scroll", view);
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (leftDown && _scroll.DragMove(yDip)) { Invalidate(); return; }
        _hit.UpdateHover(xDip, yDip);
        int idx = HitChoiceIndex(xDip, yDip);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            if (idx >= 0) _focusedIndex = idx;
            SetCursorKind(idx >= 0 ? UiCursorKind.Hand : UiCursorKind.Arrow);
            Invalidate();
        }
    }

    protected override void OnPointerLeave()
    {
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }
        _pressedIndex = HitChoiceIndex(e.X, e.Y);
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            Invalidate();
            return;
        }
        int idx = HitChoiceIndex(e.X, e.Y);
        int pressed = _pressedIndex;
        _pressedIndex = -1;
        if (idx >= 0 && idx == pressed)
        {
            CommitIndex(idx);
        }
    }

    protected override void OnCaptureLost()
    {
        _pressedIndex = -1;
        _scroll.EndDrag();
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        _scroll.OnWheel(delta);
        Invalidate();
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B /*ESC*/) { CloseToOwner(); return true; }
        if (_choices.Count == 0) return false;

        int next = _focusedIndex;
        switch (vk)
        {
            case 0x26 /*UP*/:
                next = _focusedIndex <= 0 ? _choices.Count - 1 : _focusedIndex - 1;
                break;
            case 0x28 /*DOWN*/:
                next = _focusedIndex < 0 ? 0 : (_focusedIndex + 1) % _choices.Count;
                break;
            case 0x24 /*HOME*/:
                next = 0;
                break;
            case 0x23 /*END*/:
                next = _choices.Count - 1;
                break;
            case 0x21 /*PRIOR*/:
                next = Math.Max(0, (_focusedIndex < 0 ? 0 : _focusedIndex) - 6);
                break;
            case 0x22 /*NEXT*/:
                next = Math.Min(_choices.Count - 1, (_focusedIndex < 0 ? 0 : _focusedIndex) + 6);
                break;
            case 0x0D /*RETURN*/:
            case 0x20 /*SPACE*/:
                if (_focusedIndex >= 0 && _focusedIndex < _choices.Count) CommitIndex(_focusedIndex);
                return true;
            default:
                return false;
        }

        _focusedIndex = next;
        _hoverIndex = next;
        _scroll.EnsureVisible(next * ItemH, (next + 1) * ItemH, 4f);
        Invalidate();
        return true;
    }

    protected override void OnChar(char ch)
    {
        if (char.IsControl(ch) || _choices.Count == 0) return;
        long now = Environment.TickCount64;
        if (now - _lastTypeTick > 900) _typeAhead = "";
        _lastTypeTick = now;
        _typeAhead += char.ToUpperInvariant(ch);

        int start = _focusedIndex >= 0 ? (_focusedIndex + 1) % _choices.Count : 0;
        for (int step = 0; step < _choices.Count; step++)
        {
            int idx = (start + step) % _choices.Count;
            if (_choices[idx].Label.StartsWith(_typeAhead, StringComparison.OrdinalIgnoreCase))
            {
                _focusedIndex = idx;
                _hoverIndex = idx;
                _scroll.EnsureVisible(idx * ItemH, (idx + 1) * ItemH, 4f);
                Invalidate();
                return;
            }
        }
        // Single-char cycle fallback when prefix fails
        if (_typeAhead.Length > 1)
        {
            _typeAhead = char.ToUpperInvariant(ch).ToString();
            for (int step = 0; step < _choices.Count; step++)
            {
                int idx = (start + step) % _choices.Count;
                if (_choices[idx].Label.StartsWith(_typeAhead, StringComparison.OrdinalIgnoreCase))
                {
                    _focusedIndex = idx;
                    _hoverIndex = idx;
                    _scroll.EnsureVisible(idx * ItemH, (idx + 1) * ItemH, 4f);
                    Invalidate();
                    return;
                }
            }
        }
    }

    protected override void OnActivated(bool active)
    {
        if (!active) CloseWindow();
    }

    protected override void OnClosed()
    {
        LastClosedTick = Environment.TickCount64;
        base.OnClosed();
        Closed?.Invoke();
    }

    private void CommitIndex(int idx)
    {
        var picked = _choices[idx];
        CloseToOwner();
        _onSelected(picked);
    }

    private void CloseToOwner()
    {
        CloseWindow();
        if (_owner.IsVisible) _owner.RequestFocus();
    }

    private int HitChoiceIndex(float x, float y)
    {
        var view = new UiRect(Pad, Pad, WidthDip - Pad * 2f - (_scroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth + 2f : 0f), HeightDip - Pad * 2f);
        if (!view.Contains(x, y)) return -1;
        int idx = (int)MathF.Floor((y - view.Y + _scroll.Offset) / ItemH);
        return idx >= 0 && idx < _choices.Count ? idx : -1;
    }
}
