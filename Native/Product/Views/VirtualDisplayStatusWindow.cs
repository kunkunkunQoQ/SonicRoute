#if SONICROUTE_PHONE
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class VirtualDisplayStatusWindow : LayeredWindow
{
    private readonly IVirtualDisplayClient _client;
    private UiThemePalette _theme;
    private readonly IMainThread _dispatcher;
    private readonly UiHitMap _hit = new();
    private readonly WinEventProc _locationChanged;
    private nint _hook;
    private bool _positionQueued, _restoring, _closed;
    private VirtualDisplayState _state;

    internal VirtualDisplayStatusWindow(IVirtualDisplayClient client, AppConfig config, IMainThread dispatcher)
        : base(300, 200, 1, 1)
    {
        _client = client;
        _theme = UiThemePalette.Resolve(config);
        _dispatcher = dispatcher;
        OwnerHandle = client.WindowHandle;
        ToolWindow = true;
        DestroyOnClose = true;
        _locationChanged = OnOwnerEvent;
    }

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        Invalidate();
    }

    protected override UiHitMap InputMap => _hit;

    internal void UpdateState(VirtualDisplayState state)
    {
        _state = state;
        _restoring = state == VirtualDisplayState.Recovering;
        if (_hook == 0 && OwnerHandle != 0)
        {
            uint tid = GetWindowThreadProcessId(OwnerHandle, out uint pid);
            // Only the target video window's thread, while this status is being shown.
            if (pid != 0 && tid != 0) _hook = SetWinEventHook(0x8001, 0x800B, 0, _locationChanged, pid, tid, 0);
        }
        PositionOverVideo();
        Invalidate();
    }

    private void OnOwnerEvent(nint hook, uint ev, nint hwnd, int obj, int child, uint tid, uint time)
    {
        if (hwnd != OwnerHandle || obj != 0 || child != 0 || _positionQueued || IsDisposed) return;
        _positionQueued = true;
        if (!_dispatcher.TryPost(() =>
        {
            _positionQueued = false;
            if (!_closed && !IsDisposed) PositionOverVideo();
        })) _positionQueued = false;
    }

    private void PositionOverVideo()
    {
        if (OwnerHandle == 0 || !IsWindow(OwnerHandle)) { Dispose(); return; }
        if (IsIconic(OwnerHandle) || ViewNative.IsWindowVisible(OwnerHandle) == 0)
        {
            if (IsVisible) Hide();
            return;
        }
        if (ViewNative.GetClientRect(OwnerHandle, out var rect) == 0 || rect.Right <= 0 || rect.Bottom <= 0) return;
        var origin = new ViewNative.Point();
        if (ViewNative.ClientToScreen(OwnerHandle, ref origin) == 0) return;
        float scale = Math.Max(96u, GetDpiForWindow(OwnerHandle)) / 96f;
        if (LeftPx != origin.X || TopPx != origin.Y || WidthPx != rect.Right || HeightPx != rect.Bottom || !IsVisible)
            SetBoundsPx(origin.X, origin.Y, rect.Right / scale, rect.Bottom / scale);
        if (!IsVisible) Show(activate: false);
    }

    protected override void OnRender(D2dTarget g, float w, float h)
    {
        _hit.Clear();
        g.FillRoundedRect(new UiRect(0, 0, w, h), 0, _theme.SurfaceBg);
        bool phone = _state == VirtualDisplayState.OnPhone, recovering = _state == VirtualDisplayState.Recovering;
        string title = L10n.T(phone ? "Phone.DisplayUsingOnPhone" : recovering ? "Phone.DisplayRecovering" : "Phone.DisplayScreenPaused");
        string detail = L10n.T(phone ? "Phone.DisplayUsingOnPhoneDetail" : recovering ? "Phone.DisplayPleaseWait" : "Phone.DisplayCanReopenHere");
        float inner = Math.Max(1, w - 48);
        float titleH = D2dSharedContext.Instance.MeasureText(title, UiMetrics.FontFamily, 18, 600, inner, 160, true).Height;
        float detailH = D2dSharedContext.Instance.MeasureText(detail, UiMetrics.FontFamily, 13, 400, inner, 200, true).Height;
        float y = Math.Max(0, (h - titleH - detailH - 64) / 2);
        g.DrawText(title, new UiRect(24, y, inner, titleH), _theme.TextPrimary, 18, 600, UiTextAlign.Center, wrap: true);
        y += titleH + 10;
        g.DrawText(detail, new UiRect(24, y, inner, detailH), _theme.TextSecondary, 13, 400, UiTextAlign.Center, wrap: true);
        string restore = L10n.T(phone ? "Phone.DisplaySwitchToPc" : "Phone.DisplayRestoreScreen");
        float width = Math.Min(inner, 38 + D2dSharedContext.Instance.MeasureText(restore, UiMetrics.FontFamily, 13, 400, inner, 36, false).Width);
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "display.restore", new UiRect((w - width) / 2, y + detailH + 18, width, 36), restore, enabled: !_restoring);
    }

    protected override void OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button == UiMouseButton.Left) _hit.ActiveId = _hit.HitTest(e.X, e.Y)?.Id;
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (_hit.UpdateHover(xDip, yDip)) { SetCursorKind(_hit.Hovered?.Cursor ?? UiCursorKind.Arrow); Invalidate(); }
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        string? pressed = _hit.ActiveId;
        _hit.ActiveId = null;
        if (_hit.HitTest(e.X, e.Y) is { } item && item.Id == pressed) ActivateFocused(item, e);
    }

    protected override void OnCaptureLost() { _hit.ActiveId = null; Invalidate(); }

    protected override bool ActivateFocused(UiHitItem item, UiPointerEventArgs e)
    {
        if (_restoring || item.Id != "display.restore") return false;
        _restoring = true;
        Invalidate();
        _client.Restore();
        return true;
    }

    public override void Dispose()
    {
        if (_hook != 0) { UnhookWinEvent(_hook); _hook = 0; }
        base.Dispose();
    }

    protected override void OnClosed()
    {
        _closed = true;
        if (_hook != 0) { UnhookWinEvent(_hook); _hook = 0; }
        base.OnClosed();
    }

    private delegate void WinEventProc(nint hook, uint ev, nint hwnd, int obj, int child, uint tid, uint time);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint low, uint high, nint module, WinEventProc callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
#endif
