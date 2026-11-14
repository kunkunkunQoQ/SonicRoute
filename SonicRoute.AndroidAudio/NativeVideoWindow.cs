using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace SonicRoute.AndroidAudio;

public enum NativeWindowMouseBinding
{
    Back,
    Home,
    AppSwitch,
    ExpandNotificationPanel,
    ExpandSettingsPanel
}

/// <summary>
/// Dedicated Win32 HWND host running on its own message-loop thread (<c>GetMessageW</c> = zero idle CPU).
/// Owns mirror aspect-locked sizing, flex 8px-aligned sizing with 100ms coalesce, Win32 IME routing,
/// mouse/wheel forwarding, WIC window icon setup, and <c>WM_APP + 73</c> restore notification.
/// </summary>
public sealed class NativeVideoWindow : IAsyncDisposable
{
    public const uint RestoreMessage = 0x8000 + 73; // WM_APP + 73 (SR_RESTORE_MESSAGE)

    private const int WmDestroy = 0x0002;
    private const int WmSize = 0x0005;
    private const int WmSetFocus = 0x0007;
    private const int WmKillFocus = 0x0008;
    private const int WmPaint = 0x000F;
    private const int WmClose = 0x0010;
    private const int WmEraseBkgnd = 0x0014;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmSetIcon = 0x0080;
    private const int WmNcDestroy = 0x0082;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmImeStartComposition = 0x010D;
    private const int WmImeEndComposition = 0x010E;
    private const int WmInputDeviceChange = 0x00FE;
    private const int WmInput = 0x00FF;
    private const int WmTimer = 0x0113;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseHWheel = 0x020E;
    private const int WmSizing = 0x0214;
    private const int WmDeviceChange = 0x0219;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDpiChanged = 0x02E0;

    private const uint WsOverlappedWindow = 0x00CF_0000;
    private const uint WsClipChildren = 0x0200_0000;
    private const uint WsClipSiblings = 0x0400_0000;
    private const uint WsExDlgModalFrame = 0x0000_0001;
    private const uint CsDblClks = 0x0008;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private const int SizeMinimized = 1;
    private const int GidcRemoval = 2;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int BlackBrush = 4;
    private const int MonitorDefaultToNearest = 2;

    private static readonly nint CoalesceTimerId = 1;
    private static readonly nint GamepadTimerId = 2;
    private const uint GamepadPollIntervalMs = 16;
    private const string WindowClassName = "SonicRoute_VirtualDisplay_WindowClass";

    private static readonly WndProcDelegate SharedWndProc = StaticWndProc;
    private static readonly object ClassRegistrationLock = new();
    private static bool _classRegistered;
    private static readonly ConcurrentDictionary<nint, NativeVideoWindow> ActiveWindows = new();

    private readonly string _title;
    private readonly bool _isMirrorMode;
    private readonly bool _preferText;
    private byte[]? _iconPng;
    private readonly int _initialX;
    private readonly int _initialY;
    private readonly int _initialClientWidth;
    private readonly int _initialClientHeight;
    private readonly string _appUserModelId;
    private readonly ImeKeyRouter _imeRouter = new();
    private readonly TaskCompletionSource<nint> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly object _lifecycleGate = new();
    private bool _threadStarted;
    private bool _closeRequested;

    private nint _hwnd;
    private nint _bigIcon;
    private nint _smallIcon;
    private bool _inSizeMove;
    private bool _suppressProgrammaticSize;
    private bool _closedInvoked;

    private int _contentW;
    private int _contentH;
    private int _clientW;
    private int _clientH;

    private long _lastFlexSentTick;
    private int _lastSentFlexW;
    private int _lastSentFlexH;
    private int _pendingFlexW;
    private int _pendingFlexH;
    private bool _hasPendingFlex;
    private int _lastCommittedWinW;
    private int _lastCommittedWinH;

    private int _mouseButtonsState;
    private char _pendingHighSurrogate;

    private readonly NativeGamepadReader _gamepadReader = new();
    private readonly GamepadInputState[] _lastGamepadStates = new GamepadInputState[NativeGamepadReader.MaxSlots];
    private readonly bool[] _slotActiveNonNeutral = new bool[NativeGamepadReader.MaxSlots];
    private bool _hasFocus;
    private bool _gamepadTimerActive;
    private int _xinputConnectedMask;
    private Func<int, XInputSample>? _xinputProbeOverride;

    public NativeVideoWindow(
        string title,
        bool isMirrorMode,
        bool preferText,
        byte[]? iconPng,
        int x,
        int y,
        int initialClientWidth,
        int initialClientHeight,
        string? appUserModelId = null)
    {
        _title = string.IsNullOrWhiteSpace(title) ? "SonicRoute" : title;
        _isMirrorMode = isMirrorMode;
        _preferText = preferText;
        _iconPng = iconPng;
        _initialX = x;
        _initialY = y;
        _initialClientWidth = Math.Max(96, initialClientWidth);
        _initialClientHeight = Math.Max(64, initialClientHeight);
        _appUserModelId = string.IsNullOrWhiteSpace(appUserModelId)
            ? $"SonicRoute.VirtualDisplay.{Guid.NewGuid():N}"
            : appUserModelId.Trim();
        _contentW = _initialClientWidth;
        _contentH = _initialClientHeight;

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = $"SonicRoute Video Window ({_title})"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public nint WindowHandle => Volatile.Read(ref _hwnd);
    public string AppUserModelId => _appUserModelId;
    public int ClientWidth => Volatile.Read(ref _clientW);
    public int ClientHeight => Volatile.Read(ref _clientH);
    public int ContentWidth => Volatile.Read(ref _contentW);
    public int ContentHeight => Volatile.Read(ref _contentH);
    public bool IsMaximized => WindowHandle is var hwnd && hwnd != 0 && IsZoomed(hwnd);
    public Task Closed => _closed.Task;

    public event Action? PaintRequested;
    public event Action<int, int>? ClientSizeChanged;
    public event Action<int, int>? FlexResizeRequested;
    public event Action<int, int>? WindowSizeCommitted;
    public event Action? RestoreRequested;
    public event Action? CloseRequested;

    public event Action<byte, ulong, int, int, int, int, float, int, int>? TouchEvent;
    public event Action<int, int, int, int, float, float, int>? ScrollEvent;
    public event Action<byte, int, int, int>? KeyEvent;
    public event Action<string>? TextInput;
    public event Action<NativeWindowMouseBinding, byte>? MouseShortcut;
    public event Action<GamepadInputState>? GamepadStateChanged;

    internal bool HasGamepadFocus => Volatile.Read(ref _hasFocus);
    internal bool IsGamepadTimerActive => Volatile.Read(ref _gamepadTimerActive);
    internal Func<int, XInputSample>? XInputProbeOverride
    {
        get => Volatile.Read(ref _xinputProbeOverride);
        set => Volatile.Write(ref _xinputProbeOverride, value);
    }

    public async Task<nint> CreateAsync(CancellationToken cancellationToken)
    {
        using var reg = cancellationToken.Register(() =>
        {
            _created.TrySetCanceled(cancellationToken);
            RequestClose();
        });
        lock (_lifecycleGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_closeRequested, this);
            if (_threadStarted) throw new InvalidOperationException("The video window was already started.");
            _threadStarted = true;
            try { _thread.Start(); }
            catch { _closed.TrySetResult(); throw; }
        }
        return await _created.Task.ConfigureAwait(false);
    }

    private int _shownFlag;
    public void EnsureVisible()
    {
        nint hwnd = WindowHandle;
        if (hwnd == 0) return;
        if (Interlocked.Exchange(ref _shownFlag, 1) == 0)
            ShowWindowAsync(hwnd, SwShow);
    }

    public void UpdateContentSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        bool changed = _contentW != width || _contentH != height;
        _contentW = width;
        _contentH = height;

        if (changed && _isMirrorMode && WindowHandle is var hwnd && hwnd != 0 && !IsIconic(hwnd) && !IsZoomed(hwnd))
        {
            PostMessage(hwnd, WmUserFitContent, (nint)width, (nint)height);
        }
    }

    public void Activate()
    {
        nint hwnd = WindowHandle;
        if (hwnd == 0) return;
        EnsureVisible();
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
    }

    public void PostRestoreSignal()
    {
        nint hwnd = WindowHandle;
        if (hwnd != 0) PostMessage(hwnd, RestoreMessage, 0, 0);
    }

    private const int WmUserFitContent = 0x0400 + 1;
    private const int WmUserClose = 0x0400 + 2;

    private void ThreadMain()
    {
        try
        {
            if (Volatile.Read(ref _closeRequested)) return;
            nint hInstance = GetModuleHandle(null);
            EnsureClassRegistered(hInstance);
            if (Volatile.Read(ref _closeRequested)) return;

            uint style = WsOverlappedWindow | WsClipChildren | WsClipSiblings;
            uint exStyle = _isMirrorMode ? WsExDlgModalFrame : 0;

            var (winX, winY, winW, winH) = ComputeInitialWindowRect(style, exStyle);

            nint hwnd = CreateWindowEx(
                exStyle, WindowClassName, _title, style,
                winX, winY, winW, winH,
                0, 0, hInstance, 0);
            if (hwnd == 0)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"CreateWindowEx failed with Win32 error {err}.");
            }

            ActiveWindows[hwnd] = this;
            Volatile.Write(ref _hwnd, hwnd);
            if (Volatile.Read(ref _closeRequested)) return;

            if (GetClientRect(hwnd, out var cr))
            {
                _clientW = Math.Max(1, cr.Right - cr.Left);
                _clientH = Math.Max(1, cr.Bottom - cr.Top);
                (_lastSentFlexW, _lastSentFlexH) = FlexResizeMath.AlignAndClamp(_clientW, _clientH);
                if (WindowSizeCommitted != null)
                    CommitWindowSizeIfValid(_clientW, _clientH);
            }

            ApplyWindowAppUserModelId(hwnd, _appUserModelId);
            ApplyWindowIcon(hwnd);
            if (GetForegroundWindow() == hwnd || !AnyOtherWindowHasGamepadFocus(hwnd))
                NativeGamepadReader.RegisterWindow(hwnd);
            if (!_created.TrySetResult(hwnd)) return;

            if (GetForegroundWindow() == hwnd && !IsIconic(hwnd))
            {
                Volatile.Write(ref _hasFocus, true);
                RefreshXInputPolling(hwnd);
            }

            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(in msg);
                DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            _created.TrySetException(ex);
        }
        finally
        {
            nint hwnd = WindowHandle;
            if (hwnd != 0) DestroyWindow(hwnd);
            _closed.TrySetResult();
        }
    }

    private (int X, int Y, int W, int H) ComputeInitialWindowRect(uint style, uint exStyle)
    {
        int reqW = _initialClientWidth;
        int reqH = _initialClientHeight;
        var rect = new Rect { Left = 0, Top = 0, Right = reqW, Bottom = reqH };
        AdjustWindowRectEx(ref rect, style, false, exStyle);
        int ncW = (rect.Right - rect.Left) - reqW;
        int ncH = (rect.Bottom - rect.Top) - reqH;

        var pt = new Point { X = _initialX, Y = _initialY };
        nint monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref mi))
        {
            int maxClientW = Math.Max(240, (mi.rcWork.Right - mi.rcWork.Left) - ncW);
            int maxClientH = Math.Max(240, (mi.rcWork.Bottom - mi.rcWork.Top) - ncH);
            if (reqW > maxClientW || reqH > maxClientH)
            {
                (reqW, reqH) = MirrorAspectMath.FitAspect(reqW, reqH, maxClientW, maxClientH);
            }

            int totalW = reqW + ncW;
            int totalH = reqH + ncH;
            int x = Math.Clamp(_initialX, mi.rcWork.Left, Math.Max(mi.rcWork.Left, mi.rcWork.Right - totalW));
            int y = Math.Clamp(_initialY, mi.rcWork.Top, Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - totalH));
            return (x, y, totalW, totalH);
        }

        return (_initialX, _initialY, reqW + ncW, reqH + ncH);
    }

    private void ApplyWindowIcon(nint hwnd)
    {
        if (_isMirrorMode)
        {
            _iconPng = null;
            // Keep caption clean without generic Win32 icon, matching mirror_resize.h.
            SendMessage(hwnd, WmSetIcon, IconBig, 0);
            SendMessage(hwnd, WmSetIcon, IconSmall, 0);
            SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoZOrder | SwpNoActivate | 0x0002 | 0x0001 | SwpFrameChanged);
            return;
        }

        byte[]? png = _iconPng is { Length: > 0 } ? _iconPng : WicIconLoader.LoadDefaultPhoneIconPng();
        _iconPng = null;
        if (png == null) return;

        int bigPx = Math.Max(32, GetSystemMetrics(11 /* SM_CXICON */));
        int smallPx = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
        _bigIcon = WicIconLoader.TryCreateIcon(png, bigPx);
        _smallIcon = WicIconLoader.TryCreateIcon(png, smallPx);
        if (_bigIcon != 0) SendMessage(hwnd, WmSetIcon, IconBig, _bigIcon);
        if (_smallIcon != 0) SendMessage(hwnd, WmSetIcon, IconSmall, _smallIcon);
    }

    private static void EnsureClassRegistered(nint hInstance)
    {
        lock (ClassRegistrationLock)
        {
            if (_classRegistered) return;
            var wc = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                style = CsDblClks,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SharedWndProc),
                hInstance = hInstance,
                hCursor = LoadCursor(0, 32512 /* IDC_ARROW */),
                hbrBackground = GetStockObject(BlackBrush),
                lpszClassName = WindowClassName
            };
            ushort atom = RegisterClassEx(in wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                    throw new InvalidOperationException($"RegisterClassEx failed with Win32 error {err}.");
            }
            _classRegistered = true;
        }
    }

    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (ActiveWindows.TryGetValue(hwnd, out var window))
            return window.WndProc(hwnd, msg, wParam, lParam);
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmEraseBkgnd:
                return 1;

            case WmPaint:
                if (BeginPaint(hwnd, out var ps) != 0)
                    EndPaint(hwnd, in ps);
                PaintRequested?.Invoke();
                return 0;

            case WmGetMinMaxInfo:
                HandleGetMinMaxInfo(hwnd, lParam);
                return 0;

            case WmEnterSizeMove:
                _inSizeMove = true;
                return 0;

            case WmSizing:
                if (HandleSizing(hwnd, (int)wParam, lParam)) return 1;
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WmExitSizeMove:
                _inSizeMove = false;
                _hasPendingFlex = false;
                KillTimer(hwnd, CoalesceTimerId);
                HandleClientRectFinalized(hwnd);
                return 0;

            case WmWindowPosChanging:
                if (_isMirrorMode) HandleMirrorWindowPosChanging(hwnd, lParam);
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WmSize:
                if ((int)wParam == SizeMinimized)
                {
                    OnGamepadFocusLostOrMinimized(hwnd);
                }
                else
                {
                    int cw = lParam.ToInt32() & 0xFFFF;
                    int ch = (lParam.ToInt32() >> 16) & 0xFFFF;
                    if (cw > 0 && ch > 0)
                    {
                        _clientW = cw;
                        _clientH = ch;
                        ClientSizeChanged?.Invoke(cw, ch);
                        if (!_inSizeMove && !_suppressProgrammaticSize && !_isMirrorMode)
                            EmitFlexResizeAndCommit(cw, ch, bypassThrottle: true, commitWindowSize: true);
                    }
                    if (!HasGamepadFocus && GetForegroundWindow() == hwnd)
                    {
                        Volatile.Write(ref _hasFocus, true);
                        RefreshXInputPolling(hwnd);
                    }
                }
                return 0;

            case WmTimer:
                if (wParam == CoalesceTimerId)
                {
                    KillTimer(hwnd, CoalesceTimerId);
                    FlushPendingFlexResize();
                }
                else if (wParam == GamepadTimerId)
                {
                    RefreshXInputPolling(hwnd);
                }
                return 0;

            case WmDpiChanged:
                if (lParam != 0)
                {
                    var suggested = Marshal.PtrToStructure<Rect>(lParam);
                    SetWindowPos(hwnd, 0, suggested.Left, suggested.Top,
                        suggested.Right - suggested.Left, suggested.Bottom - suggested.Top,
                        SwpNoZOrder | SwpNoActivate);
                }
                return 0;

            case WmUserFitContent:
                FitWindowToContentAspect(hwnd, (int)wParam, (int)lParam);
                return 0;

            case RestoreMessage:
                Activate();
                RestoreRequested?.Invoke();
                return 0;

            case WmImeStartComposition:
                _imeRouter.OnStartComposition();
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WmImeEndComposition:
                _imeRouter.OnEndComposition();
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WmSetFocus:
                NativeGamepadReader.RegisterWindow(hwnd);
                if (!IsIconic(hwnd))
                {
                    Volatile.Write(ref _hasFocus, true);
                    RefreshXInputPolling(hwnd);
                }
                return 0;

            case WmKillFocus:
                _imeRouter.OnFocusLost();
                _pendingHighSurrogate = '\0';
                ReleaseMouseCaptureIfNeeded(hwnd);
                OnGamepadFocusLostOrMinimized(hwnd);
                return 0;

            case WmInput:
                if (HasGamepadFocus && !IsIconic(hwnd) &&
                    _gamepadReader.TryProcessRawInput(lParam, _xinputConnectedMask, out var hidState))
                {
                    PublishGamepadState(hidState);
                }
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WmInputDeviceChange:
                if (wParam == GidcRemoval)
                {
                    int removedSlot = _gamepadReader.RemoveHidDevice(lParam);
                    if (removedSlot >= 0) EmitNeutralForSlotIfActive(removedSlot);
                }
                if (HasGamepadFocus && !IsIconic(hwnd))
                    RefreshXInputPolling(hwnd);
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WmDeviceChange:
                if (HasGamepadFocus && !IsIconic(hwnd))
                    RefreshXInputPolling(hwnd);
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WmKeyDown:
            case WmSysKeyDown:
                HandleKeyMessage(isDown: true, isSys: msg == WmSysKeyDown, (uint)wParam.ToInt64(), (uint)lParam.ToInt64());
                return msg == WmSysKeyDown ? DefWindowProc(hwnd, msg, wParam, lParam) : 0;

            case WmKeyUp:
            case WmSysKeyUp:
                HandleKeyMessage(isDown: false, isSys: msg == WmSysKeyUp, (uint)wParam.ToInt64(), (uint)lParam.ToInt64());
                return msg == WmSysKeyUp ? DefWindowProc(hwnd, msg, wParam, lParam) : 0;

            case WmChar:
                HandleChar((char)(wParam.ToInt64() & 0xFFFF));
                return 0;

            case WmLButtonDblClk:
                HandleLeftDoubleClick(hwnd, lParam);
                return 0;

            case WmLButtonDown:
                HandleMouseButton(hwnd, isDown: true, ScrcpyControlMessageWriter.ButtonPrimary, lParam);
                return 0;
            case WmLButtonUp:
                HandleMouseButton(hwnd, isDown: false, ScrcpyControlMessageWriter.ButtonPrimary, lParam);
                return 0;
            case WmRButtonDown:
                HandleMouseButton(hwnd, isDown: true, ScrcpyControlMessageWriter.ButtonSecondary, lParam);
                return 0;
            case WmRButtonUp:
                HandleMouseButton(hwnd, isDown: false, ScrcpyControlMessageWriter.ButtonSecondary, lParam);
                return 0;
            case WmMButtonDown:
                HandleMouseButton(hwnd, isDown: true, ScrcpyControlMessageWriter.ButtonTertiary, lParam);
                return 0;
            case WmMButtonUp:
                HandleMouseButton(hwnd, isDown: false, ScrcpyControlMessageWriter.ButtonTertiary, lParam);
                return 0;
            case WmXButtonDown:
                HandleMouseButton(hwnd, isDown: true, GetXButtonFlag(wParam), lParam);
                return 1;
            case WmXButtonUp:
                HandleMouseButton(hwnd, isDown: false, GetXButtonFlag(wParam), lParam);
                return 1;

            case WmMouseMove:
                HandleMouseMove(lParam);
                return 0;

            case WmMouseWheel:
            case WmMouseHWheel:
                HandleMouseWheel(hwnd, isHorizontal: msg == WmMouseHWheel, wParam, lParam);
                return 0;

            case WmClose:
                OnGamepadFocusLostOrMinimized(hwnd);
                if (!_closedInvoked)
                {
                    _closedInvoked = true;
                    CloseRequested?.Invoke();
                }
                DestroyWindow(hwnd);
                return 0;

            case WmUserClose:
                OnGamepadFocusLostOrMinimized(hwnd);
                DestroyWindow(hwnd);
                return 0;

            case WmDestroy:
                ClearWindowAppUserModelId(hwnd);
                return DefWindowProc(hwnd, msg, wParam, lParam);

            case WmNcDestroy:
                KillTimer(hwnd, CoalesceTimerId);
                OnGamepadFocusLostOrMinimized(hwnd);
                ActiveWindows.TryRemove(hwnd, out _);
                if (ActiveWindows.IsEmpty)
                {
                    NativeGamepadReader.UnregisterIfNoWindowsRemain();
                }
                else
                {
                    foreach (var other in ActiveWindows.Values)
                    {
                        if (other.HasGamepadFocus && other.WindowHandle is var otherHwnd && otherHwnd != 0)
                        {
                            NativeGamepadReader.RegisterWindow(otherHwnd);
                            break;
                        }
                    }
                }
                _gamepadReader.Dispose();
                Volatile.Write(ref _hwnd, 0);
                WicIconLoader.DestroyIconSafe(ref _bigIcon);
                WicIconLoader.DestroyIconSafe(ref _smallIcon);
                _iconPng = null;
                PostQuitMessage(0);
                return DefWindowProc(hwnd, msg, wParam, lParam);

            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void GetNonClientAndWorkArea(nint hwnd, out int ncW, out int ncH, out Rect workArea)
    {
        ncW = 16;
        ncH = 39;
        if (GetWindowRect(hwnd, out var wr) && GetClientRect(hwnd, out var cr))
        {
            ncW = Math.Max(0, (wr.Right - wr.Left) - (cr.Right - cr.Left));
            ncH = Math.Max(0, (wr.Bottom - wr.Top) - (cr.Bottom - cr.Top));
        }

        nint mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        workArea = GetMonitorInfo(mon, ref mi)
            ? mi.rcWork
            : new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private void HandleGetMinMaxInfo(nint hwnd, nint lParam)
    {
        if (lParam == 0) return;
        GetNonClientAndWorkArea(hwnd, out int ncW, out int ncH, out var work);
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        int workW = Math.Max(320, work.Right - work.Left);
        int workH = Math.Max(240, work.Bottom - work.Top);

        if (_isMirrorMode)
        {
            var (minW, minH) = MirrorAspectMath.ComputeMinTrackSize(_contentW, _contentH, ncW, ncH);
            mmi.ptMinTrackSize.X = Math.Min(minW, workW);
            mmi.ptMinTrackSize.Y = Math.Min(minH, workH);

            var (maxCW, maxCH) = MirrorAspectMath.FitAspect(_contentW, _contentH, Math.Max(1, workW - ncW), Math.Max(1, workH - ncH));
            mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, maxCW + ncW);
            mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, maxCH + ncH);
        }
        else
        {
            mmi.ptMinTrackSize.X = FlexResizeMath.MinSide + ncW;
            mmi.ptMinTrackSize.Y = FlexResizeMath.MinSide + ncH;
        }
        Marshal.StructureToPtr(mmi, lParam, false);
    }

    private bool HandleSizing(nint hwnd, int edge, nint lParam)
    {
        if (lParam == 0) return false;
        GetNonClientAndWorkArea(hwnd, out int ncW, out int ncH, out var work);
        var rc = Marshal.PtrToStructure<Rect>(lParam);

        if (_isMirrorMode)
        {
            int maxTrackW = Math.Max(320, work.Right - work.Left);
            int maxTrackH = Math.Max(240, work.Bottom - work.Top);
            MirrorAspectMath.ApplySizingEdge(
                ref rc.Left, ref rc.Top, ref rc.Right, ref rc.Bottom,
                edge, _contentW, _contentH, ncW, ncH, maxTrackW, maxTrackH);
            Marshal.StructureToPtr(rc, lParam, false);
            PaintRequested?.Invoke();
            return true;
        }

        // Do not alter the proposed rectangle in flex mode: all eight edges/corners move freely.
        int clientW = (rc.Right - rc.Left) - ncW;
        int clientH = (rc.Bottom - rc.Top) - ncH;
        if (clientW > 0 && clientH > 0)
            ScheduleCoalescedFlexResize(hwnd, clientW, clientH);
        PaintRequested?.Invoke();
        return false;
    }

    private void HandleMirrorWindowPosChanging(nint hwnd, nint lParam)
    {
        if (lParam == 0 || _suppressProgrammaticSize || IsIconic(hwnd) || IsZoomed(hwnd)) return;
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((pos.flags & 0x0001 /* SWP_NOSIZE */) != 0 || pos.cx <= 0 || pos.cy <= 0) return;

        GetNonClientAndWorkArea(hwnd, out int ncW, out int ncH, out var work);
        int maxCW = Math.Max(1, (work.Right - work.Left) - ncW);
        int maxCH = Math.Max(1, (work.Bottom - work.Top) - ncH);
        int cw = Math.Clamp(pos.cx - ncW, 1, maxCW);
        int ch = Math.Clamp(pos.cy - ncH, 1, maxCH);
        var (fitW, fitH) = MirrorAspectMath.FitAspect(_contentW, _contentH, cw, ch);
        pos.cx = fitW + ncW;
        pos.cy = fitH + ncH;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    private void FitWindowToContentAspect(nint hwnd, int contentW, int contentH)
    {
        if (contentW <= 0 || contentH <= 0 || !GetWindowRect(hwnd, out var wr)) return;
        GetNonClientAndWorkArea(hwnd, out int ncW, out int ncH, out var work);
        int maxCW = Math.Max(1, (work.Right - work.Left) - ncW);
        int maxCH = Math.Max(1, (work.Bottom - work.Top) - ncH);
        int curCW = Math.Max(1, _clientW);
        int curCH = Math.Max(1, _clientH);
        var (fitW, fitH) = MirrorAspectMath.FitAspect(contentW, contentH, Math.Min(curCW, maxCW), Math.Min(curCH, maxCH));
        if (fitW == curCW && fitH == curCH) return;

        int cx = wr.Left + (wr.Right - wr.Left) / 2;
        int cy = wr.Top + (wr.Bottom - wr.Top) / 2;
        int newW = fitW + ncW;
        int newH = fitH + ncH;
        int x = Math.Clamp(cx - newW / 2, work.Left, Math.Max(work.Left, work.Right - newW));
        int y = Math.Clamp(cy - newH / 2, work.Top, Math.Max(work.Top, work.Bottom - newH));

        _suppressProgrammaticSize = true;
        try { SetWindowPos(hwnd, 0, x, y, newW, newH, SwpNoZOrder | SwpNoActivate); }
        finally { _suppressProgrammaticSize = false; }
    }

    private void ScheduleCoalescedFlexResize(nint hwnd, int clientW, int clientH)
    {
        var (w, h) = FlexResizeMath.AlignAndClamp(clientW, clientH);
        _pendingFlexW = w;
        _pendingFlexH = h;
        _hasPendingFlex = true;

        long now = Environment.TickCount64;
        if (now - _lastFlexSentTick >= FlexResizeMath.CoalesceIntervalMs)
        {
            KillTimer(hwnd, CoalesceTimerId);
            FlushPendingFlexResize();
        }
        else
        {
            SetTimer(hwnd, CoalesceTimerId, FlexResizeMath.CoalesceIntervalMs, 0);
        }
    }

    private void FlushPendingFlexResize()
    {
        if (!_hasPendingFlex) return;
        _hasPendingFlex = false;
        EmitFlexResizeAndCommit(_pendingFlexW, _pendingFlexH, bypassThrottle: true, commitWindowSize: false);
    }

    private void HandleClientRectFinalized(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var cr)) return;
        int cw = Math.Max(1, cr.Right - cr.Left);
        int ch = Math.Max(1, cr.Bottom - cr.Top);
        _clientW = cw;
        _clientH = ch;
        ClientSizeChanged?.Invoke(cw, ch);
        if (!_isMirrorMode) EmitFlexResizeAndCommit(cw, ch, bypassThrottle: true, commitWindowSize: true);
    }

    public void CommitInitialWindowSize()
    {
        if (_lastCommittedWinW == 0 && _lastCommittedWinH == 0)
            CommitWindowSizeIfValid(Volatile.Read(ref _clientW), Volatile.Read(ref _clientH));
    }

    public bool SyncInitialFlexSize(int frameWidth, int frameHeight)
    {
        if (_isMirrorMode || frameWidth <= 0 || frameHeight <= 0) return false;
        int cw = Volatile.Read(ref _clientW);
        int ch = Volatile.Read(ref _clientH);
        if (cw <= 0 || ch <= 0) return false;

        var (targetW, targetH) = FlexResizeMath.AlignAndClamp(cw, ch);
        double targetAspect = (double)targetW / targetH;
        double frameAspect = (double)frameWidth / frameHeight;
        double relDiff = Math.Abs(targetAspect - frameAspect) / Math.Max(targetAspect, frameAspect);
        if (relDiff <= 0.025) return false;

        _lastSentFlexW = targetW;
        _lastSentFlexH = targetH;
        _lastFlexSentTick = Environment.TickCount64;
        FlexResizeRequested?.Invoke(targetW, targetH);
        return true;
    }

    public void ResetFlexThrottle() => Volatile.Write(ref _lastFlexSentTick, 0);

    private void CommitWindowSizeIfValid(int clientW, int clientH)
    {
        if (IsMaximized) return;
        if (clientW < 96 || clientH < 64 || clientW > 16384 || clientH > 16384) return;
        if (clientW == _lastCommittedWinW && clientH == _lastCommittedWinH) return;
        _lastCommittedWinW = clientW;
        _lastCommittedWinH = clientH;
        WindowSizeCommitted?.Invoke(clientW, clientH);
    }

    private void EmitFlexResizeAndCommit(int clientW, int clientH, bool bypassThrottle, bool commitWindowSize = true)
    {
        var (w, h) = FlexResizeMath.AlignAndClamp(clientW, clientH);
        long now = Environment.TickCount64;
        if ((bypassThrottle || now - _lastFlexSentTick >= FlexResizeMath.CoalesceIntervalMs)
            && (w != _lastSentFlexW || h != _lastSentFlexH))
        {
            _lastSentFlexW = w;
            _lastSentFlexH = h;
            _lastFlexSentTick = now;
            FlexResizeRequested?.Invoke(w, h);
        }
        if (commitWindowSize)
        {
            CommitWindowSizeIfValid(clientW, clientH);
        }
    }

    internal VideoRect GetInputContentRect()
        => _isMirrorMode
            ? VideoCoordinateMapper.ComputeContentRect(_clientW, _clientH, _contentW, _contentH)
            : new VideoRect(0, 0, Math.Max(1, _clientW), Math.Max(1, _clientH));

    private void HandleLeftDoubleClick(nint hwnd, nint lParam)
    {
        int px = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int py = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var rect = GetInputContentRect();
        if (_isMirrorMode && VideoCoordinateMapper.IsOutsideContentRect(px, py, rect))
        {
            FitWindowToContentAspect(hwnd, _contentW, _contentH);
            return;
        }
        HandleMouseButton(hwnd, isDown: true, ScrcpyControlMessageWriter.ButtonPrimary, lParam);
    }

    private void HandleMouseButton(nint hwnd, bool isDown, int button, nint lParam)
    {
        if (button == 0) return;

        bool shift = IsKeyDown(0x10 /* VK_SHIFT */);
        if (!shift && button != ScrcpyControlMessageWriter.ButtonPrimary)
        {
            NativeWindowMouseBinding? binding = button switch
            {
                ScrcpyControlMessageWriter.ButtonSecondary => NativeWindowMouseBinding.Back,
                ScrcpyControlMessageWriter.ButtonTertiary => NativeWindowMouseBinding.Home,
                ScrcpyControlMessageWriter.ButtonBack => NativeWindowMouseBinding.AppSwitch,
                ScrcpyControlMessageWriter.ButtonForward => NativeWindowMouseBinding.ExpandNotificationPanel,
                _ => null
            };
            if (binding.HasValue)
            {
                byte act = isDown ? ScrcpyControlMessageWriter.KeyActionDown : ScrcpyControlMessageWriter.KeyActionUp;
                MouseShortcut?.Invoke(binding.Value, act);
                return;
            }
        }

        if (isDown)
        {
            _mouseButtonsState |= button;
            SetCapture(hwnd);
        }
        else
        {
            _mouseButtonsState &= ~button;
            if (_mouseButtonsState == 0 && GetCapture() == hwnd) ReleaseCapture();
        }

        int px = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int py = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var rect = GetInputContentRect();
        var (fx, fy) = VideoCoordinateMapper.ClientToFrame(px, py, rect, _contentW, _contentH);
        byte motionAction = isDown ? ScrcpyControlMessageWriter.MotionActionDown : ScrcpyControlMessageWriter.MotionActionUp;
        float pressure = isDown ? 1.0f : 0.0f;
        TouchEvent?.Invoke(motionAction, ScrcpyControlMessageWriter.PointerIdMouse,
            fx, fy, _contentW, _contentH, pressure, button, _mouseButtonsState);
    }

    private void HandleMouseMove(nint lParam)
    {
        if (_contentW <= 0 || _contentH <= 0) return;
        int px = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int py = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var rect = GetInputContentRect();
        var (fx, fy) = VideoCoordinateMapper.ClientToFrame(px, py, rect, _contentW, _contentH);

        bool pressed = _mouseButtonsState != 0;
        byte action = pressed ? ScrcpyControlMessageWriter.MotionActionMove : ScrcpyControlMessageWriter.MotionActionHoverMove;
        float pressure = pressed ? 1.0f : 0.0f;
        TouchEvent?.Invoke(action, ScrcpyControlMessageWriter.PointerIdMouse,
            fx, fy, _contentW, _contentH, pressure, 0, _mouseButtonsState);
    }

    private void HandleMouseWheel(nint hwnd, bool isHorizontal, nint wParam, nint lParam)
    {
        if (_contentW <= 0 || _contentH <= 0) return;
        int rawDelta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
        float steps = rawDelta / 120.0f;
        if (steps == 0f) return;

        var pt = new Point
        {
            X = unchecked((short)(lParam.ToInt64() & 0xFFFF)),
            Y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF))
        };
        ScreenToClient(hwnd, ref pt);
        var rect = GetInputContentRect();
        var (fx, fy) = VideoCoordinateMapper.ClientToFrame(pt.X, pt.Y, rect, _contentW, _contentH);
        ScrollEvent?.Invoke(fx, fy, _contentW, _contentH, isHorizontal ? steps : 0f, isHorizontal ? 0f : steps, _mouseButtonsState);
    }

    private void HandleKeyMessage(bool isDown, bool isSys, uint vk, uint flags)
    {
        uint scanCode = (flags >> 16) & 0xFF;
        bool extended = (flags & 0x0100_0000) != 0;
        int repeat = isDown && (flags & 0x4000_0000) != 0 ? 1 : 0;

        bool ctrl = IsKeyDown(0x11 /* VK_CONTROL */);
        bool shift = IsKeyDown(0x10 /* VK_SHIFT */);
        bool alt = IsKeyDown(0x12 /* VK_MENU */) || isSys;
        bool win = IsKeyDown(0x5B /* VK_LWIN */) || IsKeyDown(0x5C /* VK_RWIN */);

        if (_imeRouter.ShouldBlockKeyMessage(isDown, vk, scanCode, ctrl, win, alt))
            return;

        if (!ctrl && !alt && !win && _preferText && IsPrintableVirtualKey(vk))
            return;

        if (!AndroidKeycodeMapper.TryMapVirtualKey(vk, extended, out int androidKey))
            return;

        int meta = ReadCurrentMetaState();
        byte action = isDown ? ScrcpyControlMessageWriter.KeyActionDown : ScrcpyControlMessageWriter.KeyActionUp;
        KeyEvent?.Invoke(action, androidKey, repeat, meta);
    }

    private void HandleChar(char ch)
    {
        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch;
            return;
        }
        if (char.IsLowSurrogate(ch) && _pendingHighSurrogate != '\0')
        {
            string pair = new([_pendingHighSurrogate, ch]);
            _pendingHighSurrogate = '\0';
            TextInput?.Invoke(pair);
            return;
        }
        _pendingHighSurrogate = '\0';

        if (ch < 0x20 || ch == 0x7F) return;
        if (!_preferText && ch < 0x80) return;

        TextInput?.Invoke(ch.ToString());
    }

    private static bool IsPrintableVirtualKey(uint vk)
        => vk is (>= 0x30 and <= 0x39)
            or (>= 0x41 and <= 0x5A)
            or 0x20
            or (>= 0xBA and <= 0xC0)
            or (>= 0xDB and <= 0xDF);

    private static int ReadCurrentMetaState()
    {
        int meta = 0;
        if (IsKeyDown(0xA0)) meta |= AndroidKeycodeMapper.AMetaShiftOn | AndroidKeycodeMapper.AMetaShiftLeftOn;
        if (IsKeyDown(0xA1)) meta |= AndroidKeycodeMapper.AMetaShiftOn | AndroidKeycodeMapper.AMetaShiftRightOn;
        if ((meta & AndroidKeycodeMapper.AMetaShiftOn) == 0 && IsKeyDown(0x10)) meta |= AndroidKeycodeMapper.AMetaShiftOn;

        if (IsKeyDown(0xA2)) meta |= AndroidKeycodeMapper.AMetaCtrlOn | AndroidKeycodeMapper.AMetaCtrlLeftOn;
        if (IsKeyDown(0xA3)) meta |= AndroidKeycodeMapper.AMetaCtrlOn | AndroidKeycodeMapper.AMetaCtrlRightOn;
        if ((meta & AndroidKeycodeMapper.AMetaCtrlOn) == 0 && IsKeyDown(0x11)) meta |= AndroidKeycodeMapper.AMetaCtrlOn;

        if (IsKeyDown(0xA4)) meta |= AndroidKeycodeMapper.AMetaAltOn | AndroidKeycodeMapper.AMetaAltLeftOn;
        if (IsKeyDown(0xA5)) meta |= AndroidKeycodeMapper.AMetaAltOn | AndroidKeycodeMapper.AMetaAltRightOn;
        if ((meta & AndroidKeycodeMapper.AMetaAltOn) == 0 && IsKeyDown(0x12)) meta |= AndroidKeycodeMapper.AMetaAltOn;

        if (IsKeyDown(0x5B)) meta |= AndroidKeycodeMapper.AMetaMetaOn | AndroidKeycodeMapper.AMetaMetaLeftOn;
        if (IsKeyDown(0x5C)) meta |= AndroidKeycodeMapper.AMetaMetaOn | AndroidKeycodeMapper.AMetaMetaRightOn;

        if (IsToggled(0x14)) meta |= AndroidKeycodeMapper.AMetaCapsLockOn;
        if (IsToggled(0x90)) meta |= AndroidKeycodeMapper.AMetaNumLockOn;
        if (IsToggled(0x91)) meta |= AndroidKeycodeMapper.AMetaScrollLockOn;
        return meta;
    }

    private static bool IsKeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;
    private static bool IsToggled(int vk) => (GetKeyState(vk) & 0x0001) != 0;

    private static int GetXButtonFlag(nint wParam)
    {
        int xb = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
        return xb switch
        {
            1 => ScrcpyControlMessageWriter.ButtonBack,
            2 => ScrcpyControlMessageWriter.ButtonForward,
            _ => 0
        };
    }

    private void ReleaseMouseCaptureIfNeeded(nint hwnd)
    {
        _mouseButtonsState = 0;
        if (GetCapture() == hwnd) ReleaseCapture();
    }

    private static bool AnyOtherWindowHasGamepadFocus(nint excludeHwnd)
    {
        foreach (var pair in ActiveWindows)
        {
            if (pair.Key != excludeHwnd && pair.Value.HasGamepadFocus)
                return true;
        }
        return false;
    }

    private void RefreshXInputPolling(nint hwnd)
    {
        if (!HasGamepadFocus || IsIconic(hwnd))
        {
            StopGamepadTimerIfNeeded(hwnd);
            return;
        }

        int connectedMask = PollXInputControllers();
        if (connectedMask != 0)
            StartGamepadTimerIfNeeded(hwnd);
        else
            StopGamepadTimerIfNeeded(hwnd);
    }

    private int PollXInputControllers()
    {
        int prevMask = _xinputConnectedMask;
        int newMask = 0;
        var probe = Volatile.Read(ref _xinputProbeOverride);

        for (int slot = 0; slot < NativeGamepadReader.MaxSlots; slot++)
        {
            bool connected;
            ushort buttons;
            byte leftTrigger;
            byte rightTrigger;
            short thumbLX;
            short thumbLY;
            short thumbRX;
            short thumbRY;

            if (probe != null)
            {
                var sample = probe(slot);
                connected = sample.Connected;
                buttons = sample.Buttons;
                leftTrigger = sample.LeftTrigger;
                rightTrigger = sample.RightTrigger;
                thumbLX = sample.ThumbLX;
                thumbLY = sample.ThumbLY;
                thumbRX = sample.ThumbRX;
                thumbRY = sample.ThumbRY;
            }
            else
            {
                connected = NativeGamepadReader.TryPollXInput(
                    slot,
                    out buttons,
                    out leftTrigger,
                    out rightTrigger,
                    out thumbLX,
                    out thumbLY,
                    out thumbRX,
                    out thumbRY);
            }

            if (connected)
            {
                newMask |= 1 << slot;
                PublishGamepadState(GamepadMath.NormalizeState(
                    (byte)slot,
                    buttons,
                    leftTrigger,
                    rightTrigger,
                    thumbLX,
                    thumbLY,
                    thumbRX,
                    thumbRY));
            }
            else if ((prevMask & (1 << slot)) != 0)
            {
                EmitNeutralForSlotIfActive(slot);
            }
        }

        _xinputConnectedMask = newMask;
        return newMask;
    }

    private void PublishGamepadState(GamepadInputState normalized)
    {
        int slot = normalized.Slot;
        if ((uint)slot >= NativeGamepadReader.MaxSlots) return;

        if (!normalized.IsNeutral)
        {
            if (!_slotActiveNonNeutral[slot] || normalized != _lastGamepadStates[slot])
            {
                _lastGamepadStates[slot] = normalized;
                _slotActiveNonNeutral[slot] = true;
                GamepadStateChanged?.Invoke(normalized);
            }
            return;
        }

        if (_slotActiveNonNeutral[slot])
        {
            _lastGamepadStates[slot] = normalized;
            _slotActiveNonNeutral[slot] = false;
            GamepadStateChanged?.Invoke(normalized);
        }
    }

    private void EmitNeutralForSlotIfActive(int slot)
    {
        if ((uint)slot >= NativeGamepadReader.MaxSlots || !_slotActiveNonNeutral[slot])
            return;

        var neutral = GamepadInputState.Neutral((byte)slot);
        _lastGamepadStates[slot] = neutral;
        _slotActiveNonNeutral[slot] = false;
        GamepadStateChanged?.Invoke(neutral);
    }

    private void EmitNeutralForAllActiveSlots()
    {
        for (int slot = 0; slot < NativeGamepadReader.MaxSlots; slot++)
            EmitNeutralForSlotIfActive(slot);
    }

    private void StartGamepadTimerIfNeeded(nint hwnd)
    {
        if (hwnd == 0 || Volatile.Read(ref _gamepadTimerActive)) return;
        if (SetTimer(hwnd, GamepadTimerId, GamepadPollIntervalMs, 0) != 0)
            Volatile.Write(ref _gamepadTimerActive, true);
    }

    private void StopGamepadTimerIfNeeded(nint hwnd)
    {
        if (Volatile.Read(ref _gamepadTimerActive))
        {
            if (hwnd != 0) KillTimer(hwnd, GamepadTimerId);
            Volatile.Write(ref _gamepadTimerActive, false);
        }
    }

    private void OnGamepadFocusLostOrMinimized(nint hwnd)
    {
        Volatile.Write(ref _hasFocus, false);
        StopGamepadTimerIfNeeded(hwnd);
        _xinputConnectedMask = 0;
        EmitNeutralForAllActiveSlots();
    }

    private void RequestClose()
    {
        nint hwnd;
        lock (_lifecycleGate)
        {
            _closeRequested = true;
            hwnd = WindowHandle;
            if (!_threadStarted) _closed.TrySetResult();
        }
        if (hwnd != 0)
            PostMessage(hwnd, WmUserClose, 0, 0);
    }

    public async ValueTask DisposeAsync()
    {
        RequestClose();
        if (_thread.IsAlive && _thread != Thread.CurrentThread)
        {
            try { await _closed.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
            catch { }
        }
        _iconPng = null;
    }

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint hdc;
        public int fErase;
        public Rect rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? lpModuleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(in WndClassEx lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg lpMsg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(in Msg lpMsg);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hWnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out Rect lpRect);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref Rect lpRect, uint dwStyle, bool bMenu, uint dwExStyle);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point pt, uint dwFlags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint hInstance, int lpCursorName);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hWnd, out PaintStruct lpPaint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint hWnd, in PaintStruct lpPaint);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint hWnd, nint nIDEvent, uint uElapse, nint lpTimerFunc);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hWnd, nint uIDEvent);
    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hWnd, ref Point lpPoint);
    private static readonly Guid IidIPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey PkeyAppUserModelId = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };
    private const ushort VtEmpty = 0;
    private const ushort VtLpwstr = 31;

    private static void ApplyWindowAppUserModelId(nint hwnd, string appUserModelId)
    {
        if (hwnd == 0 || string.IsNullOrWhiteSpace(appUserModelId)) return;
        Guid iid = IidIPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, in iid, out nint storePtr) < 0 || storePtr == 0)
            return;
        using var store = new ComPtr(storePtr);
        nint strPtr = Marshal.StringToCoTaskMemUni(appUserModelId);
        try
        {
            var pv = new PropVariant { vt = VtLpwstr, ptrVal = strPtr };
            PropertyKey key = PkeyAppUserModelId;
            if (ComVtbl.Get<PropertyStoreSetValueFn>(store.Handle, 6)(store.Handle, in key, in pv) >= 0)
                ComVtbl.Get<PropertyStoreCommitFn>(store.Handle, 7)(store.Handle);
        }
        finally
        {
            Marshal.FreeCoTaskMem(strPtr);
        }
    }

    private static void ClearWindowAppUserModelId(nint hwnd)
    {
        if (hwnd == 0) return;
        Guid iid = IidIPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, in iid, out nint storePtr) < 0 || storePtr == 0)
            return;
        using var store = new ComPtr(storePtr);
        var pv = new PropVariant { vt = VtEmpty };
        PropertyKey key = PkeyAppUserModelId;
        if (ComVtbl.Get<PropertyStoreSetValueFn>(store.Handle, 6)(store.Handle, in key, in pv) >= 0)
            ComVtbl.Get<PropertyStoreCommitFn>(store.Handle, 7)(store.Handle);
    }

    public static string? TryGetWindowAppUserModelId(nint hwnd)
    {
        if (hwnd == 0) return null;
        Guid iid = IidIPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, in iid, out nint storePtr) < 0 || storePtr == 0)
            return null;
        using var store = new ComPtr(storePtr);
        PropertyKey key = PkeyAppUserModelId;
        if (ComVtbl.Get<PropertyStoreGetValueFn>(store.Handle, 5)(store.Handle, in key, out var pv) < 0)
            return null;
        try
        {
            return pv.vt == VtLpwstr && pv.ptrVal != 0 ? Marshal.PtrToStringUni(pv.ptrVal) : null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint ptrVal;
        public nint pad;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreGetValueFn(nint self, in PropertyKey key, out PropVariant pv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreSetValueFn(nint self, in PropertyKey key, in PropVariant propvar);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreCommitFn(nint self);

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid riid, out nint ppv);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pvar);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
}

internal readonly record struct XInputSample(
    bool Connected,
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short ThumbLX,
    short ThumbLY,
    short ThumbRX,
    short ThumbRY);
