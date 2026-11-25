using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SonicRoute.Native.Ui;

internal enum UiMouseButton { Left, Right, Middle, XButton1, XButton2 }

internal enum UiCursorKind { Arrow, Hand, IBeam, SizeNS, SizeWE, SizeNWSE, SizeNESW }

internal sealed class UiPointerEventArgs
{
    public float X { get; init; }
    public float Y { get; init; }
    public UiMouseButton Button { get; init; }
    public bool Ctrl { get; init; }
    public bool Shift { get; init; }
    public bool Alt { get; init; }
    public int ClickCount { get; init; } = 1;
}

internal delegate bool UiHitTestHandler(int xPx, int yPx, out int htCode);

/// <summary>
/// Layered Win32 window base supporting per-pixel premultiplied Direct2D rendering,
/// crisp DPI scaling, pointer/wheel/keyboard input, and on-demand frame timer animations (zero idle CPU).
/// </summary>
internal abstract unsafe class LayeredWindow : IDisposable
{
    private const string WindowClassName = "SonicRoute.NativeUi.LayeredWindow";
    private const uint CsDblClks = 0x0008;
    private const uint WsPopup = 0x80000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsMinimizeBox = 0x00020000;
    private const uint WsMaximizeBox = 0x00010000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExAppWindow = 0x00040000;

    private const uint WmDestroy = 0x0002;
    private const uint WmMove = 0x0003;
    private const uint WmSize = 0x0005;
    private const uint WmActivate = 0x0006;
    private const uint WmSetFocus = 0x0007;
    private const uint WmKillFocus = 0x0008;
    private const uint WmPaint = 0x000F;
    private const uint WmClose = 0x0010;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmShowWindow = 0x0018;
    private const uint WmCancelMode = 0x001F;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmSetCursor = 0x0020;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmTimer = 0x0113;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;
    private const uint WmMouseHWheel = 0x020E;
    private const uint WmCaptureChanged = 0x0215;
    private const uint WmDropFiles = 0x0233;
    private const uint WmMouseLeave = 0x02A3;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmUserInvalidate = 0x0401;
    private const int GwlExStyle = -20;

    private static readonly nint HwndTopMost = new(-1);
    private static readonly nint HwndNoTopMost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;

    private const int SwShow = 5;
    private const int SwShowNoActivate = 4;
    private const int SwHide = 0;
    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const int SwRestore = 9;

    private static readonly Dictionary<nint, LayeredWindow> Instances = new();
    private static bool _classRegistered;
    private static readonly delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> WndProcPtr = &WndProc;

    private D2dTarget? _target;
    private nint _memDc;
    private nint _dibBitmap;
    private nint _oldBitmap;
    private int _bufWidthPx;
    private int _bufHeightPx;

    private float _widthDip;
    private float _heightDip;
    private float _minWidthDip;
    private float _minHeightDip;
    private int _leftPx;
    private int _topPx;
    private uint _dpi = 96;
    private bool _visible;
    private bool _disposed;
    private bool _trackingLeave;
    private int _leftClickCount;
    private bool _rightPointerDown;
    private bool _paintPending;
    private bool _animTimerActive;
    private UiCursorKind _cursor = UiCursorKind.Arrow;

    public nint Handle { get; private set; }
    public bool IsVisible => _visible && Handle != 0 && !IsIconic(Handle);
    public bool IsMinimized { get; private set; }
    public bool IsDisposed => _disposed;
    public float WidthDip => _widthDip;
    public float HeightDip => _heightDip;
    public int LeftPx => _leftPx;
    public int TopPx => _topPx;
    public int WidthPx => Math.Max(1, (int)MathF.Round(_widthDip * DpiScale));
    public int HeightPx => Math.Max(1, (int)MathF.Round(_heightDip * DpiScale));
    public float DpiScale => _dpi / 96f;
    public uint Dpi => _dpi;
    public bool IsMaximized => Handle != 0 && IsZoomed(Handle);

    protected bool TopMost { get; set; }
    protected bool ToolWindow { get; set; }
    protected bool NoActivate { get; set; }
    protected bool ClickThrough { get; set; }
    protected bool Resizable { get; set; }
    protected bool ShowInTaskbar { get; set; }
    protected bool DestroyOnClose { get; set; }
    protected bool AcceptFileDrop { get; set; }
    protected nint OwnerHandle { get; set; }

    protected LayeredWindow(float widthDip, float heightDip, float minWidthDip = 100f, float minHeightDip = 40f)
    {
        _widthDip = widthDip;
        _heightDip = heightDip;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
    }

    protected void EnsureCreated(string title, nint iconHandle = 0)
    {
        if (Handle != 0 || _disposed) return;
        EnsureWindowClass();

        uint style = WsPopup | WsClipChildren;
        if (Resizable) style |= WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu;
        else if (ShowInTaskbar) style |= WsMinimizeBox | WsSysMenu;

        uint exStyle = WsExLayered;
        if (ToolWindow) exStyle |= WsExToolWindow;
        else if (ShowInTaskbar) exStyle |= WsExAppWindow;
        if (TopMost) exStyle |= WsExTopmost;
        if (NoActivate) exStyle |= WsExNoActivate;
        if (ClickThrough) exStyle |= WsExTransparent;

        int initW = Math.Max(1, (int)MathF.Round(_widthDip * DpiScale));
        int initH = Math.Max(1, (int)MathF.Round(_heightDip * DpiScale));

        Handle = CreateWindowExW(
            exStyle, WindowClassName, title, style,
            _leftPx, _topPx, initW, initH,
            OwnerHandle, 0, GetModuleHandleW(null), 0);
        if (Handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateWindowExW failed for {title}");

        Instances[Handle] = this;
        uint actualDpi = GetDpiForWindow(Handle);
        if (actualDpi >= 96 && actualDpi != _dpi)
        {
            _dpi = actualDpi;
            SetWindowPos(Handle, 0, 0, 0, WidthPx, HeightPx, SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }
        if (AcceptFileDrop) DragAcceptFiles(Handle, true);

        if (iconHandle != 0)
        {
            SendMessageW(Handle, 0x0080 /*WM_SETICON*/, 0, iconHandle);
            SendMessageW(Handle, 0x0080 /*WM_SETICON*/, 1, iconHandle);
        }
    }

    public void SetTitle(string title)
    {
        if (Handle != 0) SetWindowTextW(Handle, title);
    }

    public void SetIcon(nint hIcon)
    {
        if (Handle == 0 || hIcon == 0) return;
        SendMessageW(Handle, 0x0080, 0, hIcon);
        SendMessageW(Handle, 0x0080, 1, hIcon);
    }

    public void SetBoundsDip(float leftDip, float topDip, float widthDip, float heightDip)
    {
        float s = DpiScale;
        SetBoundsPx((int)MathF.Round(leftDip * s), (int)MathF.Round(topDip * s), widthDip, heightDip);
    }

    public void SetBoundsPx(int leftPx, int topPx, float widthDip, float heightDip)
    {
        _leftPx = leftPx;
        _topPx = topPx;
        _widthDip = MathF.Max(_minWidthDip, widthDip);
        _heightDip = MathF.Max(_minHeightDip, heightDip);
        if (Handle != 0)
        {
            int pw = WidthPx;
            int ph = HeightPx;
            SetWindowPos(Handle, TopMost ? HwndTopMost : 0, _leftPx, _topPx, pw, ph,
                (TopMost ? 0u : SwpNoZOrder) | SwpNoActivate);
            if (_visible) RenderNow();
        }
    }

    public void ResizeDip(float widthDip, float heightDip)
    {
        _widthDip = MathF.Max(_minWidthDip, widthDip);
        _heightDip = MathF.Max(_minHeightDip, heightDip);
        if (Handle != 0)
        {
            SetWindowPos(Handle, 0, 0, 0, WidthPx, HeightPx, SwpNoMove | SwpNoZOrder | SwpNoActivate);
            if (_visible) RenderNow();
        }
    }

    public void MovePx(int leftPx, int topPx)
    {
        _leftPx = leftPx;
        _topPx = topPx;
        if (Handle != 0)
            SetWindowPos(Handle, 0, _leftPx, _topPx, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    public void SetTopMost(bool topMost)
    {
        TopMost = topMost;
        if (Handle != 0)
            SetWindowPos(Handle, topMost ? HwndTopMost : HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void EnableFileDrop()
    {
        AcceptFileDrop = true;
        if (Handle != 0) DragAcceptFiles(Handle, true);
    }

    public void SetClickThrough(bool clickThrough)
    {
        ClickThrough = clickThrough;
        ApplyExStyleBit(WsExTransparent, clickThrough);
    }

    public void SetNoActivate(bool noActivate)
    {
        NoActivate = noActivate;
        ApplyExStyleBit(WsExNoActivate, noActivate);
    }

    private void ApplyExStyleBit(uint bit, bool enable)
    {
        if (Handle == 0) return;
        nint cur = GetWindowLongPtrW(Handle, GwlExStyle);
        nint next = enable ? (nint)((long)cur | bit) : (nint)((long)cur & ~bit);
        if (next != cur) SetWindowLongPtrW(Handle, GwlExStyle, next);
    }

    public void Show(bool activate = true)
    {
        if (_disposed) return;
        EnsureCreated(GetWindowTitle());
        _visible = true;
        RenderNow();
        ShowWindow(Handle, activate && !NoActivate ? SwShow : SwShowNoActivate);
        if (activate && !NoActivate)
        {
            SetForegroundWindow(Handle);
            SetFocus(Handle);
        }
        OnShown();
    }

    public void Hide()
    {
        if (Handle == 0) return;
        ReleasePointerCapture();
        _visible = false;
        StopAnimationTimer();
        ShowWindow(Handle, SwHide);
        OnHidden();
    }

    public void ReleasePointerCapture()
    {
        var hwnd = Handle;
        bool hadPress = _leftClickCount != 0 || _rightPointerDown;
        _leftClickCount = 0;
        _rightPointerDown = false;
        if (hwnd != 0 && GetCapture() == hwnd) ReleaseCapture();
        if (hadPress) OnCaptureLost();
    }

    public void CenterOnPrimaryWorkArea()
    {
        GetMonitorWorkAreaForWindow(Handle, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
        float s = monDpi / 96f;
        int wPx = Math.Max(1, (int)MathF.Round(_widthDip * s));
        int hPx = Math.Max(1, (int)MathF.Round(_heightDip * s));
        int x = ml + Math.Max(0, (mr - ml - wPx) / 2);
        int y = mt + Math.Max(0, (mb - mt - hPx) / 2);
        MovePx(x, y);
    }

    public void CloseWindow()
    {
        if (Handle != 0) DestroyWindow(Handle);
    }

    public void MinimizeWindow()
    {
        if (Handle != 0) ShowWindow(Handle, SwMinimize);
    }

    public void ToggleMaximize()
    {
        if (Handle == 0) return;
        ShowWindow(Handle, IsMaximized ? SwRestore : SwMaximize);
        Invalidate();
    }

    public void RequestFocus()
    {
        if (Handle != 0 && !NoActivate)
        {
            if (IsIconic(Handle)) ShowWindow(Handle, SwRestore);
            SetForegroundWindow(Handle);
            SetFocus(Handle);
        }
    }

    public void Invalidate()
    {
        if (!_visible || Handle == 0 || _paintPending) return;
        _paintPending = true;
        PostMessageW(Handle, WmUserInvalidate, 0, 0);
    }

    public void SetCursorKind(UiCursorKind cursor)
    {
        if (_cursor == cursor) return;
        _cursor = cursor;
        if (Handle != 0) ApplyCursor(cursor);
    }

    protected void StartAnimationTimer(int intervalMs = 16)
    {
        if (Handle == 0 || _animTimerActive) return;
        _animTimerActive = true;
        SetTimer(Handle, 1, (uint)Math.Max(8, intervalMs), 0);
    }

    protected void StopAnimationTimer()
    {
        if (Handle == 0 || !_animTimerActive) return;
        _animTimerActive = false;
        KillTimer(Handle, 1);
    }

    public (int X, int Y) DipToScreenPx(float xDip, float yDip)
    {
        var pt = new Point { X = (int)MathF.Round(xDip * DpiScale), Y = (int)MathF.Round(yDip * DpiScale) };
        if (Handle != 0) ClientToScreen(Handle, ref pt);
        return (pt.X, pt.Y);
    }

    public static void GetMonitorWorkAreaForPoint(int xPx, int yPx, out int left, out int top, out int right, out int bottom, out uint dpi)
    {
        var mon = MonitorFromPoint(new Point { X = xPx, Y = yPx }, 2 /*MONITOR_DEFAULTTONEAREST*/);
        GetMonitorWorkArea(mon, out left, out top, out right, out bottom, out dpi);
    }

    public static void GetMonitorWorkAreaForWindow(nint hwnd, out int left, out int top, out int right, out int bottom, out uint dpi)
    {
        var mon = hwnd != 0
            ? MonitorFromWindow(hwnd, 2)
            : MonitorFromPoint(new Point { X = 0, Y = 0 }, 1);
        GetMonitorWorkArea(mon, out left, out top, out right, out bottom, out dpi);
    }

    private static void GetMonitorBoundsForWindow(nint hwnd, out WinRect monitor, out WinRect work, out uint dpi)
    {
        var mon = hwnd != 0
            ? MonitorFromWindow(hwnd, 2)
            : MonitorFromPoint(new Point { X = 0, Y = 0 }, 1);
        var mi = new MonitorInfo { CbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (mon != 0 && GetMonitorInfoW(mon, ref mi))
        {
            monitor = mi.Monitor;
            work = mi.Work;
        }
        else
        {
            monitor = new WinRect { Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
            work = monitor;
        }
        dpi = 96;
        if (mon != 0 && GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx >= 96) dpi = dx;
    }

    private static void GetMonitorWorkArea(nint mon, out int left, out int top, out int right, out int bottom, out uint dpi)
    {
        var mi = new MonitorInfo { CbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (mon != 0 && GetMonitorInfoW(mon, ref mi))
        {
            left = mi.Work.Left;
            top = mi.Work.Top;
            right = mi.Work.Right;
            bottom = mi.Work.Bottom;
        }
        else
        {
            left = 0; top = 0; right = GetSystemMetrics(0); bottom = GetSystemMetrics(1);
        }
        dpi = 96;
        if (mon != 0 && GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx >= 96) dpi = dx;
    }

    public void RenderNow()
    {
        if (_disposed || Handle == 0) return;
        _paintPending = false;
        int wPx = WidthPx;
        int hPx = HeightPx;
        if (wPx <= 0 || hPx <= 0) return;

        EnsureBackBuffer(wPx, hPx);
        _target ??= new D2dTarget(D2dSharedContext.Instance);
        _target.BeginDraw(_memDc, wPx, hPx, _dpi);
        try
        {
            OnRender(_target, wPx / DpiScale, hPx / DpiScale);
        }
        finally
        {
            _target.EndDraw();
        }

        var size = new Size { Cx = wPx, Cy = hPx };
        var src = new Point { X = 0, Y = 0 };
        var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, 0, 0, ref size, _memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
    }

    private void EnsureBackBuffer(int wPx, int hPx)
    {
        if (_memDc != 0 && _bufWidthPx == wPx && _bufHeightPx == hPx) return;
        ReleaseBackBuffer();
        nint screenDc = GetDC(0);
        _memDc = CreateCompatibleDC(screenDc);
        var bmi = new BitmapInfoHeader
        {
            BiSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            BiWidth = wPx,
            BiHeight = -hPx,
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = 0,
        };
        _dibBitmap = CreateDIBSection(_memDc, ref bmi, 0, out _, 0, 0);
        _oldBitmap = SelectObject(_memDc, _dibBitmap);
        ReleaseDC(0, screenDc);
        _bufWidthPx = wPx;
        _bufHeightPx = hPx;
    }

    protected void ReleaseRenderResources()
    {
        StopAnimationTimer();
        _target?.Dispose();
        _target = null;
        ReleaseBackBuffer();
    }

    private void ReleaseBackBuffer()
    {
        if (_memDc != 0)
        {
            if (_oldBitmap != 0) SelectObject(_memDc, _oldBitmap);
            if (_dibBitmap != 0) DeleteObject(_dibBitmap);
            DeleteDC(_memDc);
        }
        _memDc = 0;
        _dibBitmap = 0;
        _oldBitmap = 0;
        _bufWidthPx = 0;
        _bufHeightPx = 0;
    }

    protected virtual string GetWindowTitle() => "SonicRoute";
    protected virtual void OnRender(D2dTarget g, float widthDip, float heightDip) { }
    protected virtual void OnShown() { }
    protected virtual void OnHidden() { }
    protected virtual bool OnClosing() => true;
    protected virtual void OnClosed() { }
    protected virtual void OnDpiChanged(uint newDpi) { }
    protected virtual void OnTickAnimation() { }
    protected virtual int HitTestNonClient(float xDip, float yDip) => 1 /*HTCLIENT*/;
    protected virtual void OnPointerMove(float xDip, float yDip, bool leftDown) { }
    protected virtual void OnPointerLeave() { }
    protected virtual void OnPointerDown(UiPointerEventArgs e) { }
    protected virtual void OnPointerUp(UiPointerEventArgs e) { }
    protected virtual void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal) { }
    protected virtual bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt) => false;
    protected virtual void OnChar(char ch) { }
    protected virtual void OnActivated(bool active) { }
    protected virtual void OnResized(float widthDip, float heightDip) { }
    protected virtual void OnMoved(int leftPx, int topPx) { }
    protected virtual void OnCaptureLost() { }
    protected virtual void OnWindowStateChanged(bool minimized, bool maximized) { }
    protected virtual void OnFilesDropped(string[] paths, float xDip, float yDip) { }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != 0) DestroyWindow(Handle);
        ReleaseRenderResources();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (!Instances.TryGetValue(hwnd, out var win))
            return DefWindowProcW(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case WmNcCalcSize:
                if (wParam != 0) return 0; // Borderless frameless layered window even with WS_THICKFRAME
                break;

            case WmGetMinMaxInfo:
                if (win.Resizable)
                {
                    var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    GetMonitorBoundsForWindow(hwnd, out var mon, out var work, out _);
                    mmi.MaxPosition = new Point { X = work.Left - mon.Left, Y = work.Top - mon.Top };
                    mmi.MaxSize = new Point { X = Math.Max(100, work.Right - work.Left), Y = Math.Max(100, work.Bottom - work.Top) };
                    mmi.MinTrackSize = new Point
                    {
                        X = (int)MathF.Round(win._minWidthDip * win.DpiScale),
                        Y = (int)MathF.Round(win._minHeightDip * win.DpiScale),
                    };
                    Marshal.StructureToPtr(mmi, lParam, false);
                    return 0;
                }
                break;

            case WmNcHitTest:
            {
                if (win.ClickThrough) return -1; /*HTTRANSPARENT*/
                int sx = unchecked((short)((long)lParam & 0xFFFF));
                int sy = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
                var pt = new Point { X = sx, Y = sy };
                ScreenToClient(hwnd, ref pt);
                float xDip = pt.X / win.DpiScale;
                float yDip = pt.Y / win.DpiScale;
                if (win.Resizable && !win.IsMaximized)
                {
                    const float grip = 6f;
                    bool l = xDip < grip, r = xDip >= win._widthDip - grip;
                    bool t = yDip < grip, b = yDip >= win._heightDip - grip;
                    if (t && l) return 13;
                    if (t && r) return 14;
                    if (b && l) return 16;
                    if (b && r) return 17;
                    if (l) return 10;
                    if (r) return 11;
                    if (t) return 12;
                    if (b) return 15;
                }
                return win.HitTestNonClient(xDip, yDip);
            }

            case WmSetCursor:
                if ((unchecked((int)(long)lParam) & 0xFFFF) == 1 /*HTCLIENT*/)
                {
                    ApplyCursor(win._cursor);
                    return 1;
                }
                break;

            case WmMouseActivate:
                if (win.NoActivate) return 3; /*MA_NOACTIVATE*/
                break;

            case WmEraseBkgnd:
                return 1;

            case WmPaint:
                ValidateRect(hwnd, 0);
                win.RenderNow();
                return 0;

            case WmUserInvalidate:
                if (win._visible) win.RenderNow();
                return 0;

            case WmTimer:
                if (wParam == 1) win.OnTickAnimation();
                return 0;

            case WmMove:
                if (!win.IsMinimized && !IsIconic(hwnd))
                {
                    if (GetWindowRect(hwnd, out var wr))
                    {
                        win._leftPx = wr.Left;
                        win._topPx = wr.Top;
                    }
                    else
                    {
                        win._leftPx = unchecked((short)((long)lParam & 0xFFFF));
                        win._topPx = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
                    }
                    win.OnMoved(win._leftPx, win._topPx);
                }
                return 0;

            case WmSize:
            {
                if (wParam == 1 /*SIZE_MINIMIZED*/)
                {
                    if (!win.IsMinimized)
                    {
                        win.IsMinimized = true;
                        win.OnWindowStateChanged(minimized: true, maximized: false);
                    }
                    return 0;
                }
                bool wasMin = win.IsMinimized;
                win.IsMinimized = false;
                if (GetWindowRect(hwnd, out var swr))
                {
                    win._leftPx = swr.Left;
                    win._topPx = swr.Top;
                }
                int pw = unchecked((short)((long)lParam & 0xFFFF));
                int ph = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
                if (pw > 0 && ph > 0)
                {
                    win._widthDip = pw / win.DpiScale;
                    win._heightDip = ph / win.DpiScale;
                    if (wasMin) win.OnWindowStateChanged(minimized: false, maximized: wParam == 2);
                    win.OnResized(win._widthDip, win._heightDip);
                    if (win._visible) win.RenderNow();
                }
                return 0;
            }

            case WmDpiChanged:
            {
                uint newDpi = (uint)(wParam & 0xFFFF);
                if (newDpi >= 96 && lParam != 0)
                {
                    win._dpi = newDpi;
                    var rc = Marshal.PtrToStructure<WinRect>(lParam);
                    win._leftPx = rc.Left;
                    win._topPx = rc.Top;
                    SetWindowPos(hwnd, 0, rc.Left, rc.Top, Math.Max(1, rc.Right - rc.Left), Math.Max(1, rc.Bottom - rc.Top),
                        SwpNoZOrder | SwpNoActivate);
                    win.OnDpiChanged(newDpi);
                    if (win._visible) win.RenderNow();
                }
                return 0;
            }

            case WmMouseMove:
            {
                if (!win._trackingLeave)
                {
                    win._trackingLeave = true;
                    var tme = new TrackMouseEventStruct { CbSize = (uint)Marshal.SizeOf<TrackMouseEventStruct>(), DwFlags = 2 /*TME_LEAVE*/, HwndTrack = hwnd };
                    TrackMouseEvent(ref tme);
                }
                (float x, float y) = ExtractPoint(lParam, win.DpiScale);
                win.OnPointerMove(x, y, ((uint)wParam & 0x0001) != 0);
                return 0;
            }

            case WmMouseLeave:
                win._trackingLeave = false;
                win.OnPointerLeave();
                return 0;

            case WmLButtonDown:
            case WmLButtonDblClk:
                if (win._leftClickCount != 0) win.ReleasePointerCapture();
                if (!win.NoActivate) SetFocus(hwnd);
                if (win.Handle != hwnd || !win._visible) return 0;
                SetCapture(hwnd);
                if (win.Handle != hwnd || GetCapture() != hwnd) return 0;
                win._leftClickCount = msg == WmLButtonDblClk ? 2 : 1;
                win.OnPointerDown(MakePointerArgs(wParam, lParam, win.DpiScale, UiMouseButton.Left, win._leftClickCount));
                return 0;

            case WmLButtonUp:
                if (win._leftClickCount == 0 || GetCapture() != hwnd) return 0;
                // WM_CAPTURECHANGED is synchronous. Finish our gesture before releasing
                // capture, so only an unexpected transfer cancels the view's press.
                int clicks = win._leftClickCount;
                win._leftClickCount = 0;
                ReleaseCapture();
                if (win.Handle != hwnd || !win._visible) return 0;
                win.OnPointerUp(MakePointerArgs(wParam, lParam, win.DpiScale, UiMouseButton.Left, clicks));
                return 0;

            case WmRButtonDown:
                if (!win.NoActivate) SetFocus(hwnd);
                if (win.Handle != hwnd || !win._visible) return 0;
                win._rightPointerDown = true;
                win.OnPointerDown(MakePointerArgs(wParam, lParam, win.DpiScale, UiMouseButton.Right, 1));
                return 0;

            case WmRButtonUp:
                if (!win._rightPointerDown) return 0;
                win._rightPointerDown = false;
                win.OnPointerUp(MakePointerArgs(wParam, lParam, win.DpiScale, UiMouseButton.Right, 1));
                return 0;

            case WmMButtonDown:
                if (!win.NoActivate) SetFocus(hwnd);
                win.OnPointerDown(MakePointerArgs(wParam, lParam, win.DpiScale, UiMouseButton.Middle, 1));
                return 0;

            case WmMButtonUp:
                win.OnPointerUp(MakePointerArgs(wParam, lParam, win.DpiScale, UiMouseButton.Middle, 1));
                return 0;

            case WmXButtonDown:
            {
                if (!win.NoActivate) SetFocus(hwnd);
                int xbtn = (int)(((uint)wParam >> 16) & 0xFFFF);
                var btn = xbtn == 2 ? UiMouseButton.XButton2 : UiMouseButton.XButton1;
                win.OnPointerDown(MakePointerArgs(wParam, lParam, win.DpiScale, btn, 1));
                return 1;
            }

            case WmXButtonUp:
            {
                int xbtn = (int)(((uint)wParam >> 16) & 0xFFFF);
                var btn = xbtn == 2 ? UiMouseButton.XButton2 : UiMouseButton.XButton1;
                win.OnPointerUp(MakePointerArgs(wParam, lParam, win.DpiScale, btn, 1));
                return 1;
            }

            case WmCaptureChanged:
                if (lParam != hwnd && win._leftClickCount != 0)
                {
                    win._leftClickCount = 0;
                    win._rightPointerDown = false;
                    win.OnCaptureLost();
                }
                return 0;

            case WmCancelMode:
            case WmKillFocus:
                win.ReleasePointerCapture();
                break;

            case WmDropFiles:
            {
                nint hDrop = (nint)wParam;
                try
                {
                    uint count = DragQueryFileW(hDrop, 0xFFFFFFFFu, null, 0);
                    if (count > 0 && count < 4096)
                    {
                        var list = new List<string>((int)count);
                        char[] buf = new char[1024];
                        for (uint i = 0; i < count; i++)
                        {
                            uint len = DragQueryFileW(hDrop, i, buf, (uint)buf.Length);
                            if (len > 0) list.Add(new string(buf, 0, (int)len));
                        }
                        var dropPt = default(Point);
                        DragQueryPoint(hDrop, ref dropPt);
                        if (list.Count > 0)
                            win.OnFilesDropped(list.ToArray(), dropPt.X / win.DpiScale, dropPt.Y / win.DpiScale);
                    }
                }
                finally
                {
                    DragFinish(hDrop);
                }
                return 0;
            }

            case WmMouseWheel:
            case WmMouseHWheel:
            {
                var pt = new Point { X = unchecked((short)((long)lParam & 0xFFFF)), Y = unchecked((short)(((long)lParam >> 16) & 0xFFFF)) };
                ScreenToClient(hwnd, ref pt);
                short delta = unchecked((short)((long)wParam >> 16));
                win.OnMouseWheel(pt.X / win.DpiScale, pt.Y / win.DpiScale, delta / 120f, msg == WmMouseHWheel);
                return 0;
            }

            case WmKeyDown:
            case WmSysKeyDown:
                if (win.OnKeyDown((int)wParam, (GetKeyState(0x11) & 0x8000) != 0, (GetKeyState(0x10) & 0x8000) != 0, (GetKeyState(0x12) & 0x8000) != 0))
                    return 0;
                break;

            case WmChar:
                win.OnChar((char)(wParam & 0xFFFF));
                return 0;

            case WmActivate:
            {
                bool active = ((uint)wParam & 0xFFFF) != 0;
                if (!active) win.ReleasePointerCapture();
                win.OnActivated(active);
                return 0;
            }

            case WmClose:
                if (!win.OnClosing()) return 0;
                if (win.DestroyOnClose) DestroyWindow(hwnd);
                else win.Hide();
                return 0;

            case WmDestroy:
                win.ReleasePointerCapture();
                Instances.Remove(hwnd);
                win._visible = false;
                win.ReleaseRenderResources();
                win.Handle = 0;
                win.OnClosed();
                return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static (float x, float y) ExtractPoint(nint lParam, float scale) =>
        (unchecked((short)((long)lParam & 0xFFFF)) / scale, unchecked((short)(((long)lParam >> 16) & 0xFFFF)) / scale);

    private static UiPointerEventArgs MakePointerArgs(nuint wParam, nint lParam, float scale, UiMouseButton btn, int clicks)
    {
        (float x, float y) = ExtractPoint(lParam, scale);
        uint keys = (uint)wParam;
        return new UiPointerEventArgs
        {
            X = x,
            Y = y,
            Button = btn,
            ClickCount = clicks,
            Ctrl = (keys & 0x0008) != 0,
            Shift = (keys & 0x0004) != 0,
            Alt = (GetKeyState(0x12) & 0x8000) != 0,
        };
    }

    private static void ApplyCursor(UiCursorKind kind)
    {
        int id = kind switch
        {
            UiCursorKind.Hand => 32649,
            UiCursorKind.IBeam => 32513,
            UiCursorKind.SizeNS => 32645,
            UiCursorKind.SizeWE => 32644,
            UiCursorKind.SizeNWSE => 32642,
            UiCursorKind.SizeNESW => 32643,
            _ => 32512,
        };
        SetCursor(LoadCursorW(0, new nint(id)));
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered) return;
        var wc = new WndClassExW
        {
            CbSize = (uint)Marshal.SizeOf<WndClassExW>(),
            Style = CsDblClks,
            LpfnWndProc = (nint)WndProcPtr,
            HInstance = GetModuleHandleW(null),
            HCursor = LoadCursorW(0, new nint(32512)),
            LpszClassName = Marshal.StringToHGlobalUni(WindowClassName),
        };
        try
        {
            if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW failed");
            _classRegistered = true;
        }
        finally
        {
            Marshal.FreeHGlobal(wc.LpszClassName);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint CbSize; public WinRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventStruct { public uint CbSize, DwFlags; public nint HwndTrack; public uint DwHoverTime; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize; public int BiWidth, BiHeight; public ushort BiPlanes, BiBitCount;
        public uint BiCompression, BiSizeImage; public int BiXPelsPerMeter, BiYPelsPerMeter; public uint BiClrUsed, BiClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassExW
    {
        public uint CbSize, Style; public nint LpfnWndProc; public int CbClsExtra, CbWndExtra;
        public nint HInstance, HIcon, HCursor, HbrBackground, LpszMenuName, LpszClassName, HIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WndClassExW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(nint hwnd, string text);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetTimer(nint hwnd, nuint id, uint elapse, nint proc);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll")] private static extern bool ValidateRect(nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint inst, nint name);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint hCursor);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouseEventStruct tme);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref Point pt);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point pt);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point pt, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint mon, ref MonitorInfo mi);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint mon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out WinRect rect);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int idx);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int idx, nint val);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(nint hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(nint drop, uint idx, [Out] char[]? file, uint cch);
    [DllImport("shell32.dll")] private static extern bool DragQueryPoint(nint drop, ref Point pt);
    [DllImport("shell32.dll")] private static extern void DragFinish(nint drop);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, nint pptDst, ref Size psize, nint hdcSrc, ref Point pptSrc, uint crKey, ref BlendFunction pblend, uint dwFlags);
}
