using System;
using System.Collections.Generic;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal readonly record struct OutputConnectionItem(
    string DeviceId,
    string PrimaryName,
    string SecondaryName,
    string FullTooltip,
    UiIcon Icon,
    UiColor AccentColor,
    bool Connected,
    bool IsNative,
    bool CanDisconnect,
    bool IsPending);

/// <summary>
/// Vertical scrollable output connection menu anchored to the App Node '+' button.
/// Keeps open across repeated connect/disconnect clicks and refreshes groups in-place without jumping.
/// </summary>
internal sealed class OutputConnectionPopupWindow : LayeredWindow
{
    private const float PopupWidthDip = 304f;
    private const float MaxPopupHeightDip = 368f;
    private const float MinPopupHeightDip = 96f;
    private const float PadX = 6f;
    private const float PadY = 6f;
    private const float SectionHeaderH = 26f;
    private const float RowH = 44f;
    private const float EmptyRowH = 28f;
    private const float SectionGap = 6f;

    private readonly record struct LayoutEntry(bool IsHeader, bool IsEmpty, string Text, OutputConnectionItem? Item, UiRect Rect);

    private readonly LayeredWindow _owner;
    private readonly Action<string> _onConnect;
    private readonly Action<string> _onDisconnect;
    private readonly UiScrollState _scroll = new();
    private readonly UiHitMap _hit = new();
    private readonly List<LayoutEntry> _entries = new(24);

    private UiThemePalette _theme;
    private IReadOnlyList<OutputConnectionItem> _connectable = [];
    private IReadOnlyList<OutputConnectionItem> _disconnectable = [];
    private float _contentHeight;
    private int _hoverEntry = -1;
    private int _focusedEntry = -1;
    private bool _keyboardNavigating;
    private UiPoint? _pointer;
    private int _activationKey;
    private string? _pressedDeviceId;
    private bool _pressedConnect;
    private int _fixedLeftPx;
    private int _fixedTopPx;
    private float _fixedHeightDip;
    private bool _placed;

    internal event Action? Closed;
    protected override UiHitMap InputMap => _hit;

    private OutputConnectionPopupWindow(
        UiThemePalette theme,
        LayeredWindow owner,
        Action<string> onConnect,
        Action<string> onDisconnect)
        : base(PopupWidthDip, 180f, 220f, MinPopupHeightDip)
    {
        _theme = theme;
        _owner = owner;
        _onConnect = onConnect;
        _onDisconnect = onDisconnect;
        TopMost = true;
        ToolWindow = true;
        DestroyOnClose = true;
        OwnerHandle = owner.Handle;
    }

    internal static OutputConnectionPopupWindow ShowFor(
        UiThemePalette theme,
        LayeredWindow owner,
        UiRect anchorDip,
        IReadOnlyList<OutputConnectionItem> connectable,
        IReadOnlyList<OutputConnectionItem> disconnectable,
        Action<string> onConnect,
        Action<string> onDisconnect)
    {
        var popup = new OutputConnectionPopupWindow(theme, owner, onConnect, onDisconnect);
        popup.SetItemsCore(connectable, disconnectable, initialAnchor: anchorDip);
        popup.Show(activate: true);
        return popup;
    }

    internal void RefreshItems(
        IReadOnlyList<OutputConnectionItem> connectable,
        IReadOnlyList<OutputConnectionItem> disconnectable)
    {
        if (IsDisposed || Handle == 0) return;
        if (ItemsEqual(_connectable, connectable) && ItemsEqual(_disconnectable, disconnectable)) return;
        SetItemsCore(connectable, disconnectable, initialAnchor: null);
        Invalidate();
    }

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        if (IsVisible) Invalidate();
    }

    private void SetItemsCore(
        IReadOnlyList<OutputConnectionItem> connectable,
        IReadOnlyList<OutputConnectionItem> disconnectable,
        UiRect? initialAnchor)
    {
        int previousFocus = _focusedEntry;
        string? focusedId = IsClickableEntry(previousFocus) ? _entries[previousFocus].Item?.DeviceId : null;
        _connectable = connectable;
        _disconnectable = disconnectable;

        RebuildEntries();

        if (!_placed && initialAnchor is { } anchor)
        {
            var topLeft = _owner.DipToScreenPx(anchor.X, anchor.Y);
            var bottomRight = _owner.DipToScreenPx(anchor.Right, anchor.Bottom);
            var center = _owner.DipToScreenPx(anchor.CenterX, anchor.CenterY);
            GetMonitorWorkAreaForPoint(center.X, center.Y, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
            float scale = monDpi / 96f;

            float desiredH = Math.Clamp(_contentHeight + PadY * 2f, MinPopupHeightDip, MaxPopupHeightDip);
            int widthPx = (int)MathF.Ceiling(PopupWidthDip * scale);
            int heightPx = (int)MathF.Ceiling(desiredH * scale);
            int gapPx = (int)MathF.Round(6f * scale);

            int xPx = center.X - widthPx / 2;
            xPx = Math.Clamp(xPx, ml + 4, Math.Max(ml + 4, mr - widthPx - 4));

            int below = mb - bottomRight.Y - gapPx - 4;
            int above = topLeft.Y - gapPx - mt - 4;
            bool openAbove = heightPx > below && above > below;
            int maxAvailPx = Math.Max((int)MathF.Ceiling(MinPopupHeightDip * scale), openAbove ? above : below);
            if (heightPx > maxAvailPx)
            {
                heightPx = maxAvailPx;
                desiredH = heightPx / scale;
            }
            int yPx = openAbove ? topLeft.Y - gapPx - heightPx : bottomRight.Y + gapPx;
            yPx = Math.Clamp(yPx, mt + 4, Math.Max(mt + 4, mb - heightPx - 4));

            _fixedLeftPx = xPx;
            _fixedTopPx = yPx;
            _fixedHeightDip = desiredH;
            _placed = true;
            SetBoundsPx(_fixedLeftPx, _fixedTopPx, PopupWidthDip, _fixedHeightDip);
        }

        // Preserve exact window bounds and clamp scroll offset smoothly on live refresh so the popup never jumps.
        _scroll.ViewportHeight = MathF.Max(20f, _fixedHeightDip - PadY * 2f);
        _scroll.ContentHeight = _contentHeight;
        _scroll.Clamp();
        _focusedEntry = -1;
        if (focusedId != null)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!IsClickableEntry(i) || !string.Equals(focusedId, _entries[i].Item?.DeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                _focusedEntry = i;
                if (_keyboardNavigating && i != previousFocus)
                {
                    var rect = _entries[i].Rect;
                    _scroll.EnsureVisible(rect.Y, rect.Bottom, 6f);
                }
                break;
            }
        }
        _hoverEntry = !_keyboardNavigating && _pointer is { } pointer ? HitEntryIndex(pointer.X, pointer.Y) : -1;
    }

    private static bool ItemsEqual(IReadOnlyList<OutputConnectionItem> left, IReadOnlyList<OutputConnectionItem> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (left[i] != right[i]) return false;
        return true;
    }

    private void RebuildEntries()
    {
        _entries.Clear();
        float y = 0f;
        float rowW = PopupWidthDip - PadX * 2f;

        // Group 1: Connect to output
        _entries.Add(new LayoutEntry(true, false, L10n.T("Apps.Canvas.ConnectToOutput"), null, new UiRect(PadX, y, rowW, SectionHeaderH)));
        y += SectionHeaderH;
        if (_connectable.Count == 0)
        {
            _entries.Add(new LayoutEntry(false, true, L10n.T("Apps.Canvas.NoConnectableOutputs"), null, new UiRect(PadX, y, rowW, EmptyRowH)));
            y += EmptyRowH;
        }
        else
        {
            for (int i = 0; i < _connectable.Count; i++)
            {
                _entries.Add(new LayoutEntry(false, false, "", _connectable[i], new UiRect(PadX, y, rowW, RowH)));
                y += RowH;
            }
        }

        y += SectionGap;

        // Group 2: Disconnect output
        _entries.Add(new LayoutEntry(true, false, L10n.T("Apps.Canvas.DisconnectOutput"), null, new UiRect(PadX, y, rowW, SectionHeaderH)));
        y += SectionHeaderH;
        if (_disconnectable.Count == 0)
        {
            _entries.Add(new LayoutEntry(false, true, L10n.T("Apps.Canvas.NoDisconnectableOutputs"), null, new UiRect(PadX, y, rowW, EmptyRowH)));
            y += EmptyRowH;
        }
        else
        {
            for (int i = 0; i < _disconnectable.Count; i++)
            {
                _entries.Add(new LayoutEntry(false, false, "", _disconnectable[i], new UiRect(PadX, y, rowW, RowH)));
                y += RowH;
            }
        }

        _contentHeight = y + 2f;
    }

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        var outer = new UiRect(0.5f, 0.5f, widthDip - 1f, heightDip - 1f);
        g.FillRoundedRect(outer, 10f, _theme.SurfaceBg);
        g.DrawRoundedRect(outer, 10f, _theme.Border, 1f);

        var view = new UiRect(PadX, PadY, widthDip - PadX * 2f, heightDip - PadY * 2f);
        _scroll.ViewportHeight = view.Height;
        _scroll.ContentHeight = _contentHeight;
        _scroll.Clamp();
        float availW = _scroll.MaxOffset > 0.5f ? view.Width - UiMetrics.ScrollBarWidth - 2f : view.Width;

        g.PushClip(view);
        _hit.PushClip(view);

        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var r = new UiRect(view.X, view.Y + entry.Rect.Y - _scroll.Offset, availW, entry.Rect.Height);
            if (r.Bottom < view.Y || r.Y > view.Bottom) continue;

            if (entry.IsHeader)
            {
                if (i > 0)
                    g.DrawLine(r.X + 4f, r.Y - 2.5f, r.Right - 4f, r.Y - 2.5f, _theme.Border, 1f, roundCaps: false);
                g.DrawText(entry.Text, r.Deflate(8f, 0f), _theme.TextSecondary, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                continue;
            }

            if (entry.IsEmpty || entry.Item is not { } item)
            {
                g.DrawText(entry.Text, r.Deflate(10f, 0f), _theme.TextSecondary.WithAlphaFraction(0.75f), 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                continue;
            }

            bool interactive = !item.Connected || item.CanDisconnect;
            bool hot = interactive && (i == _hoverEntry || i == _focusedEntry);
            var rowBgRect = r.Deflate(2f, 1.5f);

            string hitId = (item.Connected ? "outpop.disc:" : "outpop.conn:") + item.DeviceId;
            _hit.Add(hitId, rowBgRect, interactive ? UiCursorKind.Hand : UiCursorKind.Arrow, item.FullTooltip);

            if (hot)
                g.FillRoundedRect(rowBgRect, 7f, item.Connected ? _theme.Fail.WithAlphaFraction(_theme.IsDark ? 0.16f : 0.10f) : _theme.NavHover);

            // Device icon tile
            var iconTile = new UiRect(rowBgRect.X + 7f, rowBgRect.CenterY - 14f, 28f, 28f);
            g.FillRoundedRect(iconTile, 7f, item.Connected ? item.AccentColor.WithAlphaFraction(_theme.IsDark ? 0.20f : 0.14f) : _theme.SurfaceAlt);
            UiColor iconColor = item.Connected ? item.AccentColor : _theme.TextSecondary;
            g.DrawIcon(item.Icon, iconTile.Deflate(6f), iconColor, 1.7f);

            // Right-side badge / action icon
            float rightReserve = 30f;
            if (item.Connected)
            {
                string tag = item.IsPending
                    ? L10n.T("Apps.Canvas.SwitchingShort")
                    : item.IsNative ? L10n.T("Apps.Canvas.NativeBadge") : L10n.T("Apps.Canvas.ForwardBadge");
                float tagW = MathF.Ceiling(D2dSharedContext.Instance.MeasureText(tag, UiMetrics.FontFamily, 10f, 600, 110f, 18f, false).Width) + 16f;
                var badgeR = new UiRect(rowBgRect.Right - 26f - tagW, rowBgRect.CenterY - 9f, tagW, 18f);
                UiPrimitives.DrawBadge(g, _theme, badgeR, tag,
                    item.IsNative ? _theme.NavSelected : _theme.SurfaceAlt,
                    item.IsNative ? _theme.Accent : _theme.TextSecondary, fontSize: 10f);
                rightReserve = rowBgRect.Right - badgeR.X + 6f;

                var actIcon = new UiRect(rowBgRect.Right - 21f, rowBgRect.CenterY - 6f, 12f, 12f);
                g.DrawIcon(UiIcon.Close, actIcon, hot ? _theme.Fail : _theme.TextSecondary.WithAlphaFraction(0.75f), 1.8f);
            }
            else
            {
                var actIcon = new UiRect(rowBgRect.Right - 22f, rowBgRect.CenterY - 6.5f, 13f, 13f);
                g.DrawIcon(UiIcon.Plus, actIcon, hot ? _theme.Accent : _theme.TextSecondary.WithAlphaFraction(0.75f), 1.8f);
            }

            float textX = iconTile.Right + 9f;
            float textW = MathF.Max(24f, rowBgRect.Right - rightReserve - textX);
            if (!string.IsNullOrEmpty(item.SecondaryName))
            {
                g.DrawText(item.PrimaryName, new UiRect(textX, rowBgRect.Y + 4f, textW, 18f),
                    _theme.TextPrimary, 12.5f, item.Connected ? 600 : 400, UiTextAlign.Leading, UiVertAlign.Center);
                g.DrawText(item.SecondaryName, new UiRect(textX, rowBgRect.Y + 22f, textW, 15f),
                    _theme.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }
            else
            {
                g.DrawText(item.PrimaryName, new UiRect(textX, rowBgRect.Y, textW, rowBgRect.Height),
                    _theme.TextPrimary, 12.5f, item.Connected ? 600 : 400, UiTextAlign.Leading, UiVertAlign.Center);
            }
        }

        _hit.PopClip();
        g.PopClip();
        _scroll.Draw(g, _theme, _hit, "outpop.scroll", view);
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        _pointer = new(xDip, yDip);
        if (leftDown && _scroll.DragMove(yDip)) { Invalidate(); return; }
        if (_keyboardNavigating)
        {
            _keyboardNavigating = false;
            _focusedEntry = -1;
            Invalidate();
        }
        _hit.UpdateHover(xDip, yDip);
        int idx = HitEntryIndex(xDip, yDip);
        if (idx != _hoverEntry)
        {
            _hoverEntry = idx;
            SetCursorKind(idx >= 0 ? UiCursorKind.Hand : UiCursorKind.Arrow);
            Invalidate();
        }
    }

    protected override void OnPointerLeave()
    {
        _pointer = null;
        if (_hoverEntry != -1)
        {
            _hoverEntry = -1;
            Invalidate();
        }
    }

    protected override void OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        _pointer = new(e.X, e.Y);
        _keyboardNavigating = false;
        _focusedEntry = -1;
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }
        int idx = HitEntryIndex(e.X, e.Y);
        if (idx >= 0 && _entries[idx].Item is { } item)
        {
            _pressedDeviceId = item.DeviceId;
            _pressedConnect = !item.Connected;
        }
        else
        {
            _pressedDeviceId = null;
        }
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
        string? pressedId = _pressedDeviceId;
        bool pressedConn = _pressedConnect;
        _pressedDeviceId = null;

        int idx = HitEntryIndex(e.X, e.Y);
        if (idx >= 0 && _entries[idx].Item is { } item && pressedId != null
            && string.Equals(item.DeviceId, pressedId, StringComparison.OrdinalIgnoreCase)
            && (!item.Connected == pressedConn))
        {
            TriggerItem(item);
        }
    }

    protected override void OnCaptureLost()
    {
        _pressedDeviceId = null;
        _scroll.EndDrag();
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        if (_scroll.MaxOffset > 0.5f)
        {
            _pointer = new(xDip, yDip);
            _scroll.OnWheel(delta);
            _hoverEntry = HitEntryIndex(xDip, yDip);
            Invalidate();
        }
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            CloseToOwner();
            return true;
        }
        if (vk is 0x26 or 0x28 or 0x09)
        {
            int dir = vk == 0x28 || (vk == 0x09 && !shift) ? 1 : -1;
            int count = _entries.Count;
            if (count == 0) return true;
            int idx = _focusedEntry < 0 ? (dir > 0 ? -1 : count) : _focusedEntry;
            for (int step = 0; step < count; step++)
            {
                idx = (idx + dir + count) % count;
                if (IsClickableEntry(idx))
                {
                    _keyboardNavigating = true;
                    _focusedEntry = idx;
                    _hoverEntry = -1;
                    var rr = _entries[idx].Rect;
                    _scroll.EnsureVisible(rr.Y, rr.Bottom, 6f);
                    Invalidate();
                    break;
                }
            }
            return true;
        }
        if (vk is 0x0D or 0x20)
        {
            if (_activationKey == 0 && IsClickableEntry(_focusedEntry) && _entries[_focusedEntry].Item is { } item)
            {
                _activationKey = vk;
                TriggerItem(item);
            }
            return true;
        }
        return false;
    }

    protected override bool OnKeyUp(int vk)
    {
        if (vk != _activationKey) return false;
        _activationKey = 0;
        return true;
    }

    protected override void OnActivated(bool active)
    {
        if (!active) CloseWindow();
    }

    protected override void OnClosed()
    {
        base.OnClosed();
        var cb = Closed;
        Closed = null;
        cb?.Invoke();
    }

    private void TriggerItem(OutputConnectionItem item)
    {
        // Keep popup open! Callback updates optimistic state & calls RefreshItems in-place.
        if (!item.Connected) _onConnect(item.DeviceId);
        else if (item.CanDisconnect) _onDisconnect(item.DeviceId);
    }

    private void CloseToOwner()
    {
        CloseWindow();
        if (_owner.IsVisible) _owner.RequestFocus();
    }

    private bool IsClickableEntry(int index) =>
        index >= 0 && index < _entries.Count && !_entries[index].IsHeader && !_entries[index].IsEmpty
        && _entries[index].Item is { } it && (!it.Connected || it.CanDisconnect);

    private int HitEntryIndex(float x, float y)
    {
        float maxRight = WidthDip - PadX - (_scroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth + 2f : 0f);
        if (x < PadX || x > maxRight || y < PadY || y > HeightDip - PadY) return -1;
        float contentY = y - PadY + _scroll.Offset;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!IsClickableEntry(i)) continue;
            var r = _entries[i].Rect;
            if (contentY >= r.Y && contentY < r.Bottom) return i;
        }
        return -1;
    }
}
