using System;
using System.Collections.Generic;

namespace SonicRoute.Native.Ui;

internal enum UiMenuDotKind { None, Accent, Success, Fail }

internal sealed class UiMenuItem
{
    public string Text { get; set; } = "";
    public Action? Click { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsHeader { get; init; }
    public bool Checked { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Danger { get; init; }
    public UiIcon? Icon { get; init; }
    public UiMenuDotKind Dot { get; init; }
    public string? RightHint { get; init; }
    public string? Tooltip { get; set; }
    public List<UiMenuItem>? SubItems { get; init; }
    public Action<PopupMenuWindow>? SubmenuOpened { get; init; }
    public float MaxTextWidth { get; init; } = float.PositiveInfinity;

    public static UiMenuItem Separator() => new() { IsSeparator = true };
    public static UiMenuItem Header(string text) => new() { Text = text, IsHeader = true, Enabled = false };
}

/// <summary>
/// High-fidelity floating popup menu matching WPF App.xaml ConvenienceMenu / ConvenienceMenuItem.
/// Supports multi-level submenus without focus-loss teardown, vertical scrolling, async item updates, and full keyboard navigation.
/// </summary>
internal sealed class PopupMenuWindow : LayeredWindow
{
    internal event Action? Closed;

    private const float PadX = 5f; // 1 DIP frame + 4 DIP WPF menu padding
    private const float PadY = 5f;
    private const float ItemH = 30f;
    private const float HeaderH = 26f;
    private const float SepH = 9f;

    private UiThemePalette _theme;
    private List<UiMenuItem> _items;
    private readonly List<UiRect> _itemRects = new();
    private readonly UiScrollState _scroll = new();
    private readonly UiHitMap _hit = new();
    private readonly PopupMenuWindow? _parentMenu;
    private PopupMenuWindow? _subMenu;
    private int _hoverIndex = -1;
    private int _focusedIndex = -1;
    private int _openSubIndex = -1;
    private UiMenuItem? _pressedItem;
    private float _contentHeight;
    private bool _closingAll, _focusOnLoad;
    private readonly Func<TimeSpan, Action, IDisposable>? _schedule;
    private IDisposable? _pendingHover;
    private int _anchorX, _anchorY;
    private int? _flipEdge;
    private bool _alignBottomUp;
    private bool TrayStyle => _schedule != null;
    protected override UiHitMap InputMap => _hit;

    private PopupMenuWindow(UiThemePalette theme, List<UiMenuItem> items, nint owner, PopupMenuWindow? parentMenu,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
        : base(220f, 120f, 24f, 20f)
    {
        _theme = theme;
        _items = items;
        _parentMenu = parentMenu;
        TopMost = true;
        ToolWindow = true;
        _schedule = schedule ?? parentMenu?._schedule;
        // One foreground HWND owns keyboard navigation and outside activation for the whole chain.
        NoActivate = parentMenu != null;
        DestroyOnClose = true;
        OwnerHandle = owner;
    }

    public static PopupMenuWindow ShowAtCursor(UiThemePalette theme, List<UiMenuItem> items, nint owner, int screenXPx, int screenYPx, bool alignBottomUp = false,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        var menu = new PopupMenuWindow(theme, items, owner, null, schedule);
        menu.OpenAt(screenXPx, screenYPx, alignBottomUp);
        return menu;
    }

    public static PopupMenuWindow ShowAtRect(UiThemePalette theme, List<UiMenuItem> items, LayeredWindow owner, UiRect anchorDip)
    {
        var pt = owner.DipToScreenPx(anchorDip.X, anchorDip.Bottom + 4f);
        var menu = new PopupMenuWindow(theme, items, owner.Handle, null);
        menu.OpenAt(pt.X, pt.Y, alignBottomUp: false);
        return menu;
    }

    public void UpdateItems(List<UiMenuItem> items)
    {
        if (Handle == 0 || IsDisposed || _closingAll) return;
        ReleasePointerCapture();
        CloseSubMenu();
        _items = items;
        int focus = _focusOnLoad ? items.FindIndex(i => i.Enabled && !i.IsHeader && !i.IsSeparator) : -1;
        _focusedIndex = _hoverIndex = focus;
        _focusOnLoad = false;
        _scroll.Offset = 0;
        Place();
    }

    public void UpdateItem(UiMenuItem previous, UiMenuItem next)
    {
        if (Handle == 0 || _closingAll) return;
        int index = _items.IndexOf(previous);
        if (index < 0) return;
        _items[index] = next;
        Place();
        if (_openSubIndex == index && _subMenu != null)
        {
            if (next.SubItems is { Count: > 0 } children) _subMenu.UpdateItems(children);
            else CloseSubMenu();
        }
    }

    public void UpdateParentLabel(string text, string tooltip)
    {
        if (_parentMenu is { } parent && parent._openSubIndex >= 0 && parent._subMenu == this)
            parent.UpdateLabel(parent._items[parent._openSubIndex], text, tooltip);
    }

    public void UpdateLabel(UiMenuItem item, string text, string tooltip)
    {
        if (Handle == 0 || !_items.Contains(item)) return;
        item.Tooltip = tooltip;
        if (item.Text == text) { Invalidate(); return; }
        item.Text = text;
        Place();
    }

    private void OpenAt(int anchorXPx, int anchorYPx, bool alignBottomUp, int? flipEdgePx = null)
    {
        (_anchorX, _anchorY, _alignBottomUp, _flipEdge) = (anchorXPx, anchorYPx, alignBottomUp, flipEdgePx);
        Place();
        Show(activate: _parentMenu == null);
    }

    private void Place()
    {
        GetMonitorWorkAreaForPoint(_anchorX, _anchorY, out int ml, out int mt, out int mr, out int mb, out uint dpi);
        float scale = dpi / 96f;
        MeasureAndLayout((mr - ml) / scale, (mb - mt) / scale, out float w, out float h);
        int wp = (int)MathF.Ceiling(w * scale), hp = (int)MathF.Ceiling(h * scale);
        int x = _anchorX, y = _alignBottomUp ? _anchorY - hp : _anchorY;
        if (x + wp > mr) x = _flipEdge.GetValueOrDefault(mr) - wp;
        if (_parentMenu == null && !_alignBottomUp && y + hp > mb) y = _anchorY - hp;
        x = Math.Clamp(x, ml, Math.Max(ml, mr - wp));
        y = Math.Clamp(y, mt, Math.Max(mt, mb - hp));
        SetBoundsPx(x, y, w, h);
        if (_subMenu is { } child && _openSubIndex >= 0)
        {
            SetChildAnchor(child, _openSubIndex);
            child.Place();
        }
    }

    private void MeasureAndLayout(float maxWDip, float maxHDip, out float widthDip, out float heightDip)
    {
        var ctx = D2dSharedContext.Instance;
        float maxW = TrayStyle ? 24f : 160f;
        bool anyLeftCol = false;
        foreach (var item in _items)
        {
            if (item.Icon is not null || item.Dot != UiMenuDotKind.None)
                anyLeftCol = true;
        }

        float y = 0f;
        _itemRects.Clear();
        foreach (var item in _items)
        {
            if (item.IsSeparator)
            {
                _itemRects.Add(new UiRect(PadX, y, 0f, SepH));
                y += SepH;
                continue;
            }
            var size = ctx.MeasureText(item.Text, UiMetrics.FontFamily, item.IsHeader ? 11.5f : 12.5f,
                item.IsHeader ? 600 : 400, maxWDip, 32f, false);
            float h = TrayStyle ? size.Height + 12f : item.IsHeader ? HeaderH : ItemH;
            float textW = MathF.Min(size.Width, item.MaxTextWidth);
            float hintW = string.IsNullOrEmpty(item.RightHint)
                ? 0f
                : ctx.MeasureText(item.RightHint, UiMetrics.FontFamily, 11f, 400, 120f, 32f, false).Width + 20f;
            float subW = item.Checked || item.SubItems is { Count: > 0 } ? 22f : 0f;
            float rowW = PadX * 2f + (anyLeftCol ? 28f : 10f) + textW + hintW + subW + 20f;
            if (TrayStyle)
            {
                // WPF ItemBorder has 8 DIP horizontal padding and 10 DIP before its mark.
                float mark = item.Checked || item.SubItems is { Count: > 0 }
                    ? 10f + ctx.MeasureText(item.Checked ? "✓" : "›", UiMetrics.FontFamily, 12.5f, 400, 40f, 32f, false).Width : 0f;
                rowW = PadX * 2f + 16f + textW + mark;
            }
            if (rowW > maxW) maxW = rowW;
            _itemRects.Add(new UiRect(PadX, y, 0f, h));
            y += h;
        }

        _contentHeight = y;
        bool needsScroll = y + PadY * 2f > maxHDip;
        widthDip = Math.Clamp(MathF.Ceiling(maxW + (needsScroll ? UiMetrics.ScrollBarWidth + 4f : 0f)), TrayStyle ? 24f : 140f, MathF.Max(TrayStyle ? 24f : 140f, maxWDip));
        heightDip = Math.Clamp(y + PadY * 2f, TrayStyle ? 20f : 44f, MathF.Max(TrayStyle ? 20f : 44f, maxHDip));
        float itemW = widthDip - PadX * 2f - (needsScroll ? UiMetrics.ScrollBarWidth + 2f : 0f);
        for (int i = 0; i < _itemRects.Count; i++)
        {
            var r = _itemRects[i];
            _itemRects[i] = new UiRect(r.X, r.Y, itemW, r.Height);
        }
    }

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        Invalidate();
    }

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        var outer = new UiRect(0.5f, 0.5f, widthDip - 1f, heightDip - 1f);
        g.FillRoundedRect(outer, UiMetrics.MenuCornerRadius, _theme.SurfaceBg);
        g.DrawRoundedRect(outer, UiMetrics.MenuCornerRadius, _theme.Border, 1f);

        bool anyLeftCol = false;
        foreach (var it in _items)
        {
            if (it.Icon is not null || it.Dot != UiMenuDotKind.None) { anyLeftCol = true; break; }
        }

        var view = new UiRect(PadX, PadY, widthDip - PadX * 2f, heightDip - PadY * 2f);
        _scroll.ViewportHeight = view.Height;
        _scroll.ContentHeight = _contentHeight;
        _scroll.Clamp();

        g.PushClip(view);
        _hit.PushClip(view);
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var r = _itemRects[i].Offset(0f, view.Y - _scroll.Offset);
            if (r.Bottom < view.Y || r.Y > view.Bottom) continue;

            if (item.IsSeparator)
            {
                float sy = r.CenterY;
                g.DrawLine(r.X + 4f, sy, r.Right - 4f, sy, _theme.Border, 1f, roundCaps: false);
                continue;
            }
            if (item.IsHeader)
            {
                g.DrawText(item.Text, r.Deflate(10f, 0f), _theme.TextSecondary, 11.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                continue;
            }

            bool highlighted = i == _hoverIndex || i == _focusedIndex || i == _openSubIndex;
            if (highlighted && item.Enabled)
                g.FillRoundedRect(r, 4f, _theme.NavHover);

            if (!string.IsNullOrEmpty(item.Tooltip))
                _hit.Add("menu.tip." + i, r, UiCursorKind.Arrow, item.Tooltip);
            if (TrayStyle)
            {
                UiColor color = item.Enabled ? _theme.TextPrimary : _theme.TextPrimary.WithAlphaFraction(0.5f);
                string? mark = item.SubItems is { Count: > 0 } ? "›" : item.Checked ? "✓" : null;
                var label = r.Deflate(8f, 0f);
                if (mark != null)
                {
                    g.DrawText(mark, label, color, 12.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
                    float markWidth = D2dSharedContext.Instance.MeasureText(mark, UiMetrics.FontFamily, 12.5f, 400, 40f, 32f, false).Width;
                    label = new UiRect(label.X, label.Y, MathF.Max(1f, label.Width - markWidth - 10f), label.Height);
                }
                label = new UiRect(label.X, label.Y, MathF.Min(label.Width, item.MaxTextWidth), label.Height);
                g.DrawText(item.Text, label, color, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                continue;
            }
            float textLeft = r.X + (anyLeftCol ? 28f : 10f);
            if (item.Icon is UiIcon ic)
            {
                UiColor icCol = !item.Enabled ? _theme.TextSecondary.WithAlphaFraction(0.55f)
                    : item.Danger ? _theme.Fail
                    : highlighted ? _theme.Accent : _theme.TextSecondary;
                g.DrawIcon(ic, new UiRect(r.X + 7f, r.CenterY - 7f, 14f, 14f), icCol, 1.8f);
            }
            else if (item.Dot != UiMenuDotKind.None)
            {
                UiColor dotCol = item.Dot switch
                {
                    UiMenuDotKind.Success => _theme.Success,
                    UiMenuDotKind.Fail => _theme.Fail,
                    _ => _theme.Accent,
                };
                g.FillEllipse(r.X + 14f, r.CenterY, 4f, 4f, dotCol);
            }

            UiColor fg = !item.Enabled
                ? _theme.TextSecondary.WithAlphaFraction(0.55f)
                : item.Danger ? _theme.Fail : _theme.TextPrimary;

            float rightReserve = 10f;
            if (item.Checked)
            {
                g.DrawIcon(UiIcon.Check, new UiRect(r.Right - rightReserve - 12f, r.CenterY - 6f, 12f, 12f), fg);
                rightReserve += 20f;
            }
            if (item.SubItems is { Count: > 0 })
            {
                g.DrawIcon(UiIcon.ChevronRight, new UiRect(r.Right - 16f, r.CenterY - 5f, 10f, 10f), _theme.TextSecondary, 1.8f);
                rightReserve += 16f;
            }
            if (!string.IsNullOrEmpty(item.RightHint))
            {
                var hr = new UiRect(r.Right - rightReserve - 92f, r.Y, 92f, r.Height);
                g.DrawText(item.RightHint, hr, _theme.TextSecondary, 11f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
                rightReserve += 96f;
            }

            g.DrawText(item.Text, new UiRect(textLeft, r.Y, MathF.Max(10f, r.Right - rightReserve - textLeft), r.Height),
                fg, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
        _hit.PopClip();
        g.PopClip();

        _scroll.Draw(g, _theme, _hit, "menu.scroll", view);
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        for (var ancestor = _parentMenu; ancestor != null; ancestor = ancestor._parentMenu)
            ancestor.CancelPendingHover();
        if (leftDown && _scroll.DragMove(yDip)) { Invalidate(); return; }
        _hit.UpdateHover(xDip, yDip);
        int next = HitIndex(xDip, yDip);
        if (next != _hoverIndex)
        {
            _hoverIndex = next;
            _focusedIndex = next;
            SetCursorKind(!TrayStyle && next >= 0 && _items[next].Enabled ? UiCursorKind.Hand : UiCursorKind.Arrow);
            CancelPendingHover();
            // Give diagonal travel to the existing child a chance to cross its parent row.
            if (!leftDown && _openSubIndex >= 0 && _schedule != null && next != _openSubIndex)
                _pendingHover = _schedule(TimeSpan.FromMilliseconds(220), () =>
                {
                    _pendingHover = null;
                    if (Handle != 0 && _hoverIndex == next) HandleSubMenuHover(next);
                });
            else if (!leftDown) HandleSubMenuHover(next);
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
        CancelPendingHover();
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }
        int index = HitIndex(e.X, e.Y);
        _pressedItem = index >= 0 && _items[index].Enabled ? _items[index] : null;
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
        int idx = HitIndex(e.X, e.Y);
        var pressed = _pressedItem;
        _pressedItem = null;
        if (idx >= 0 && ReferenceEquals(_items[idx], pressed)) ActivateItem(idx);
    }

    protected override void OnCaptureLost()
    {
        _pressedItem = null;
        _scroll.EndDrag();
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        if (_scroll.MaxOffset > 0.5f)
        {
            CloseSubMenu();
            _scroll.OnWheel(delta);
            Invalidate();
        }
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        CancelPendingHover();
        if (_subMenu is { IsVisible: true } child) return child.OnKeyDown(vk, ctrl, shift, alt);
        if (vk == 0x1B /*VK_ESCAPE*/ || (vk == 0x25 /*VK_LEFT*/ && _parentMenu is not null))
        {
            if (_parentMenu is not null)
            {
                _parentMenu.CloseSubMenu();
                _parentMenu.Invalidate();
            }
            else
            {
                CloseChain();
            }
            return true;
        }
        if (vk == 0x27 /*VK_RIGHT*/ && _focusedIndex >= 0 && _focusedIndex < _items.Count)
        {
            var cur = _items[_focusedIndex];
            if (cur.Enabled && cur.SubItems is { Count: > 0 })
            {
                OpenSubMenu(_focusedIndex);
                if (_subMenu is not null && _subMenu._items.Count > 0)
                {
                    int first = _subMenu._items.FindIndex(i => i.Enabled && !i.IsHeader && !i.IsSeparator);
                    _subMenu._focusOnLoad = first < 0;
                    _subMenu._focusedIndex = first;
                    _subMenu._hoverIndex = first;
                    _subMenu.Invalidate();
                }
                return true;
            }
        }
        if (vk is 0x26 or 0x28 or 0x09)
        {
            int dir = vk == 0x28 || (vk == 0x09 && !shift) ? 1 : -1;
            int count = _items.Count;
            if (count == 0) return true;
            int idx = _focusedIndex < 0 ? (dir > 0 ? -1 : count) : _focusedIndex;
            for (int step = 0; step < count; step++)
            {
                idx = (idx + dir + count) % count;
                if (!_items[idx].IsSeparator && !_items[idx].IsHeader && _items[idx].Enabled)
                {
                    _focusedIndex = idx;
                    _hoverIndex = idx;
                    var rr = _itemRects[idx];
                    _scroll.EnsureVisible(rr.Y, rr.Bottom, 4f);
                    Invalidate();
                    break;
                }
            }
            return true;
        }
        if (vk is 0x0D or 0x20 && _focusedIndex >= 0)
        {
            ActivateItem(_focusedIndex);
            return true;
        }
        return false;
    }

    protected override void OnActivated(bool active)
    {
        if (!active && _parentMenu == null && !_closingAll) CloseChain();
    }

    protected override void OnClosed()
    {
        CloseSubMenu();
        Closed?.Invoke();
        Closed = null;
        base.OnClosed();
    }

    private PopupMenuWindow GetRootMenu()
    {
        var cur = this;
        while (cur._parentMenu is not null) cur = cur._parentMenu;
        return cur;
    }

    private void CloseChain()
    {
        if (_closingAll) return;
        _closingAll = true;
        CloseSubMenu();
        CloseWindow();
    }

    private void ActivateItem(int idx)
    {
        if (idx < 0 || idx >= _items.Count) return;
        var item = _items[idx];
        if (!item.Enabled || item.IsSeparator || item.IsHeader) return;
        if (item.SubItems is { Count: > 0 })
        {
            OpenSubMenu(idx);
            return;
        }

        var root = GetRootMenu();
        root.CloseChain();
        item.Click?.Invoke();
    }

    private void HandleSubMenuHover(int idx)
    {
        if (idx >= 0 && _items[idx].SubItems is { Count: > 0 } && _items[idx].Enabled)
        {
            if (_openSubIndex != idx) OpenSubMenu(idx);
        }
        else if (_openSubIndex != -1)
        {
            CloseSubMenu();
        }
    }

    private void OpenSubMenu(int idx)
    {
        if (_openSubIndex == idx && _subMenu is { IsVisible: true }) return;
        CloseSubMenu();
        var sub = _items[idx].SubItems;
        if (sub is null || sub.Count == 0) return;
        _openSubIndex = idx;
        var child = _subMenu = new PopupMenuWindow(_theme, sub, Handle, this);
        SetChildAnchor(child, idx);
        child.OpenAt(child._anchorX, child._anchorY, false, child._flipEdge);
        _items[idx].SubmenuOpened?.Invoke(child);
        Invalidate();
    }

    private void SetChildAnchor(PopupMenuWindow child, int idx)
    {
        var r = _itemRects[idx].Offset(0f, PadY - _scroll.Offset);
        var pt = DipToScreenPx(r.Right, r.Y);
        (child._anchorX, child._anchorY, child._flipEdge) = (pt.X, pt.Y, DipToScreenPx(r.X, r.Y).X);
    }

    private void CancelPendingHover()
    {
        _pendingHover?.Dispose();
        _pendingHover = null;
    }

    private void CloseSubMenu()
    {
        CancelPendingHover();
        _openSubIndex = -1;
        if (_subMenu is not null)
        {
            var s = _subMenu;
            _subMenu = null;
            s._closingAll = true;
            s.CloseSubMenu();
            s.CloseWindow();
        }
    }

    private int HitIndex(float x, float y)
    {
        float maxRight = WidthDip - PadX - (_scroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth + 2f : 0f);
        if (x < PadX || x > maxRight || y < PadY || y > HeightDip - PadY) return -1;
        float contentY = y - PadY + _scroll.Offset;
        for (int i = 0; i < _itemRects.Count; i++)
        {
            if (_items[i].IsSeparator || _items[i].IsHeader) continue;
            var r = _itemRects[i];
            if (contentY >= r.Y && contentY < r.Bottom) return i;
        }
        return -1;
    }


}
