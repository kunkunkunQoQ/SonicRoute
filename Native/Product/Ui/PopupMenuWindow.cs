using System;
using System.Collections.Generic;

namespace SonicRoute.Native.Ui;

internal enum UiMenuDotKind { None, Accent, Success, Fail }

internal sealed class UiMenuItem
{
    public string Text { get; init; } = "";
    public Action? Click { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsHeader { get; init; }
    public bool Checked { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Danger { get; init; }
    public UiIcon? Icon { get; init; }
    public UiMenuDotKind Dot { get; init; }
    public string? RightHint { get; init; }
    public List<UiMenuItem>? SubItems { get; init; }

    public static UiMenuItem Separator() => new() { IsSeparator = true };
    public static UiMenuItem Header(string text) => new() { Text = text, IsHeader = true, Enabled = false };
}

/// <summary>
/// High-fidelity floating popup menu matching WPF App.xaml ConvenienceMenu / ConvenienceMenuItem.
/// </summary>
internal sealed class PopupMenuWindow : LayeredWindow
{
    private const float PadX = 6f;
    private const float PadY = 6f;
    private const float ItemH = 30f;
    private const float HeaderH = 26f;
    private const float SepH = 9f;

    private readonly UiThemePalette _theme;
    private readonly List<UiMenuItem> _items;
    private readonly List<UiRect> _itemRects = new();
    private int _hoverIndex = -1;
    private int _focusedIndex = -1;
    private PopupMenuWindow? _subMenu;
    private int _openSubIndex = -1;
    private bool _closedByClick;
    private readonly Action? _onRootClose;

    private PopupMenuWindow(UiThemePalette theme, List<UiMenuItem> items, nint owner, Action? onRootClose)
        : base(220f, 120f, 140f, 40f)
    {
        _theme = theme;
        _items = items;
        _onRootClose = onRootClose;
        TopMost = true;
        ToolWindow = true;
        NoActivate = false;
        OwnerHandle = owner;
    }

    public static PopupMenuWindow ShowAtCursor(UiThemePalette theme, List<UiMenuItem> items, nint owner, int screenXPx, int screenYPx, bool alignBottomUp = false)
    {
        var menu = new PopupMenuWindow(theme, items, owner, null);
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

    private void OpenAt(int anchorXPx, int anchorYPx, bool alignBottomUp)
    {
        MeasureAndLayout(out float wDip, out float hDip);
        GetMonitorWorkAreaForPoint(anchorXPx, anchorYPx, out int ml, out int mt, out int mr, out int mb, out uint monDpi);

        float scale = monDpi / 96f;
        int wPx = (int)MathF.Round(wDip * scale);
        int hPx = (int)MathF.Round(hDip * scale);
        int xPx = anchorXPx;
        int yPx = alignBottomUp ? anchorYPx - hPx : anchorYPx;

        if (xPx + wPx > mr - 6) xPx = Math.Max(ml + 6, mr - wPx - 6);
        if (xPx < ml + 6) xPx = ml + 6;
        if (yPx + hPx > mb - 6) yPx = Math.Max(mt + 6, mb - hPx - 6);
        if (yPx < mt + 6) yPx = mt + 6;

        ResizeDip(wDip, hDip);
        MovePx(xPx, yPx);
        Show(activate: true);
    }

    private void MeasureAndLayout(out float widthDip, out float heightDip)
    {
        var ctx = D2dSharedContext.Instance;
        float maxW = 160f;
        bool anyLeftCol = false;
        foreach (var item in _items)
        {
            if (item.Checked || item.Icon is not null || item.Dot != UiMenuDotKind.None)
                anyLeftCol = true;
        }

        float y = PadY;
        _itemRects.Clear();
        foreach (var item in _items)
        {
            if (item.IsSeparator)
            {
                _itemRects.Add(new UiRect(PadX, y, 0f, SepH));
                y += SepH;
                continue;
            }
            float h = item.IsHeader ? HeaderH : ItemH;
            float textW = ctx.MeasureText(item.Text, UiMetrics.FontFamily, item.IsHeader ? 11.5f : 12.5f,
                item.IsHeader ? 600 : 400, 360f, 32f, false).Width;
            float hintW = string.IsNullOrEmpty(item.RightHint)
                ? 0f
                : ctx.MeasureText(item.RightHint, UiMetrics.FontFamily, 11f, 400, 120f, 32f, false).Width + 16f;
            float subW = item.SubItems is { Count: > 0 } ? 20f : 0f;
            float rowW = PadX * 2f + (anyLeftCol ? 24f : 10f) + textW + hintW + subW + 16f;
            if (rowW > maxW) maxW = rowW;
            _itemRects.Add(new UiRect(PadX, y, 0f, h));
            y += h;
        }

        widthDip = Math.Clamp(MathF.Ceiling(maxW), 168f, 380f);
        heightDip = Math.Clamp(y + PadY, 44f, 640f);
        for (int i = 0; i < _itemRects.Count; i++)
        {
            var r = _itemRects[i];
            _itemRects[i] = new UiRect(r.X, r.Y, widthDip - PadX * 2f, r.Height);
        }
    }

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        var outer = new UiRect(1f, 1f, widthDip - 2f, heightDip - 2f);
        g.FillRoundedRect(outer, UiMetrics.MenuCornerRadius, _theme.SurfaceBg);
        g.DrawRoundedRect(outer, UiMetrics.MenuCornerRadius, _theme.Border, 1f);

        bool anyLeftCol = false;
        foreach (var it in _items)
        {
            if (it.Checked || it.Icon is not null || it.Dot != UiMenuDotKind.None) { anyLeftCol = true; break; }
        }

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var r = _itemRects[i];
            if (item.IsSeparator)
            {
                float sy = r.CenterY;
                g.DrawLine(r.X + 8f, sy, r.Right - 8f, sy, _theme.Border, 1f, roundCaps: false);
                continue;
            }
            if (item.IsHeader)
            {
                g.DrawText(item.Text, r.Deflate(10f, 0f), _theme.TextSecondary, 11.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                continue;
            }

            bool highlighted = i == _hoverIndex || i == _focusedIndex || i == _openSubIndex;
            if (highlighted && item.Enabled)
                g.FillRoundedRect(r, 6f, _theme.NavHover);

            float textLeft = r.X + (anyLeftCol ? 28f : 10f);
            if (item.Checked)
            {
                g.DrawIcon(UiIcon.Check, new UiRect(r.X + 8f, r.CenterY - 6f, 12f, 12f), _theme.Accent, 2f);
            }
            else if (item.Icon is UiIcon ic)
            {
                UiColor icCol = item.Danger ? _theme.Fail : highlighted ? _theme.Accent : _theme.TextSecondary;
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
                : item.Danger
                    ? _theme.Fail
                    : _theme.TextPrimary;

            float rightReserve = 10f;
            if (item.SubItems is { Count: > 0 })
            {
                g.DrawIcon(UiIcon.ChevronRight, new UiRect(r.Right - 16f, r.CenterY - 5f, 10f, 10f), _theme.TextSecondary, 1.8f);
                rightReserve += 14f;
            }
            if (!string.IsNullOrEmpty(item.RightHint))
            {
                var hr = new UiRect(r.Right - rightReserve - 90f, r.Y, 90f, r.Height);
                g.DrawText(item.RightHint, hr, _theme.TextSecondary, 11f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
                rightReserve += 72f;
            }

            g.DrawText(item.Text, new UiRect(textLeft, r.Y, MathF.Max(10f, r.Right - rightReserve - textLeft), r.Height),
                fg, 12.5f, item.Checked ? 600 : 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        int next = HitIndex(xDip, yDip);
        if (next != _hoverIndex)
        {
            _hoverIndex = next;
            _focusedIndex = next;
            SetCursorKind(next >= 0 && _items[next].Enabled ? UiCursorKind.Hand : UiCursorKind.Arrow);
            HandleSubMenuHover(next);
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

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        int idx = HitIndex(e.X, e.Y);
        if (idx >= 0) ActivateItem(idx);
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            CloseSubMenu();
            CloseWindow();
            return true;
        }
        if (vk == 0x26 /*UP*/ || vk == 0x28 /*DOWN*/)
        {
            int dir = vk == 0x28 ? 1 : -1;
            int count = _items.Count;
            if (count == 0) return true;
            int idx = _focusedIndex;
            for (int step = 0; step < count; step++)
            {
                idx = (idx + dir + count) % count;
                if (!_items[idx].IsSeparator && !_items[idx].IsHeader && _items[idx].Enabled)
                {
                    _focusedIndex = idx;
                    _hoverIndex = idx;
                    Invalidate();
                    break;
                }
            }
            return true;
        }
        if (vk == 0x0D /*ENTER*/ && _focusedIndex >= 0)
        {
            ActivateItem(_focusedIndex);
            return true;
        }
        return false;
    }

    protected override void OnActivated(bool active)
    {
        if (!active && !_closedByClick)
        {
            if (_subMenu is { IsVisible: true }) return;
            CloseSubMenu();
            CloseWindow();
            _onRootClose?.Invoke();
        }
    }

    protected override void OnClosed()
    {
        CloseSubMenu();
        base.OnClosed();
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

        _closedByClick = true;
        CloseSubMenu();
        CloseWindow();
        _onRootClose?.Invoke();
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
        CloseSubMenu();
        var sub = _items[idx].SubItems;
        if (sub is null || sub.Count == 0) return;
        _openSubIndex = idx;
        var r = _itemRects[idx];
        var pt = DipToScreenPx(r.Right - 4f, r.Y - 4f);
        _subMenu = new PopupMenuWindow(_theme, sub, Handle, () => CloseWindow());
        _subMenu.OpenAt(pt.X, pt.Y, alignBottomUp: false);
    }

    private void CloseSubMenu()
    {
        _openSubIndex = -1;
        if (_subMenu is not null)
        {
            var s = _subMenu;
            _subMenu = null;
            s.CloseWindow();
            s.Dispose();
        }
    }

    private int HitIndex(float x, float y)
    {
        for (int i = 0; i < _itemRects.Count; i++)
        {
            if (_items[i].IsSeparator || _items[i].IsHeader) continue;
            if (_itemRects[i].Contains(x, y)) return i;
        }
        return -1;
    }
}
