using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SonicRoute.WindowsLink;

internal sealed class LinkWindowInput(nint window, int processId, uint threadId) : ILinkInput
{
    private const uint WmKeyDown = 0x100, WmKeyUp = 0x101, WmChar = 0x102;
    private const uint WmMouseMove = 0x200, WmLeftDown = 0x201, WmRightDown = 0x204, WmMiddleDown = 0x207, WmWheel = 0x20A;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _gate = new();
    private readonly Dictionary<byte, nint> _keys = new();
    private nint _pointerTarget, _lastClicked;
    private int _buttons;
    private Point _lastPoint;
    private int _selectionAnchor;
    private bool _selectingText;
    private int _pressedButtonStyle = -1;
    private long _rateStart;
    private int _rate;
    private bool _closed;
    private (nint Target, byte Button, Point Point, long At) _lastDown;

    // Monitor routing owns this instance and inspects its click state between packets.
    internal bool HasPointerCapture => _buttons != 0;
    internal bool HasKeyboardTarget => _lastClicked != 0 && Owns(_lastClicked);

    public void Apply(ReadOnlySpan<byte> packet) => Apply(packet, null);

    internal void ApplyAtPhysicalPoint(ReadOnlySpan<byte> packet, int x, int y) =>
        Apply(packet, new Point { X = x, Y = y });

    private void Apply(ReadOnlySpan<byte> packet, Point? physicalPoint)
    {
        if (packet.IsEmpty) return;
        lock (_gate)
        {
            if (_closed) return;
            if (packet[0] == 4 || !IsSharedWindow() || !IsWindowVisible(window) || IsIconic(window) || !IsWindowEnabled(window))
            {
                ReleasePressed();
                return;
            }
            if (Stopwatch.GetElapsedTime(_rateStart) >= TimeSpan.FromSeconds(1))
            {
                _rateStart = Stopwatch.GetTimestamp();
                _rate = 0;
            }
            if (++_rate > 240) { ReleasePressed(); return; }
            switch (packet[0])
            {
                case 1 when packet.Length == 9:
                    Mouse(packet, physicalPoint);
                    break;
                case 2 when packet.Length == 3 && packet[2] <= 1 && IsAppKey(packet[1]):
                    Keyboard(packet[1], packet[2] == 1);
                    break;
                case 3 when packet.Length is >= 2 and <= 9:
                    Text(packet[1..]);
                    break;
            }
        }
    }

    private bool IsSharedWindow() => threadId != 0
        && GetWindowThreadProcessId(window, out uint actual) == threadId && actual == processId;

    private bool Owns(nint target) => target != 0 && IsSharedWindow()
        && (target == window || IsChild(window, target) && GetWindowThreadProcessId(target, out uint pid) != 0 && pid == processId);

    private bool Post(nint target, uint message, nuint parameter, nint data) =>
        Owns(target) && PostMessageW(target, message, parameter, data);

    private nint HitTest(Point screen)
    {
        nint target = window;
        for (int depth = 0; depth < 32; depth++)
        {
            Point client = screen;
            if (!ScreenToClient(target, ref client)) return 0;
            // Search only this window's client area. WindowFromPoint would pick the covering app.
            nint child = ChildWindowFromPointEx(target, client, 7); // skip hidden, disabled and transparent children
            if (child == 0 || child == target) return child;
            if (!Owns(child)) return 0;
            target = child;
        }
        return 0;
    }

    private nint KeyboardTarget()
    {
        // Keep text with the remotely clicked control even when the local user focuses a different one.
        if (_lastClicked != 0) return Owns(_lastClicked) ? _lastClicked : 0;
        GuiThreadInfo info = new() { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(threadId, ref info) && Owns(info.Focus) ? info.Focus : window;
    }

    private void Mouse(ReadOnlySpan<byte> packet, Point? physicalPoint)
    {
        byte action = packet[1], button = packet[2];
        if (action > 3 || (action is 1 or 2 && button is not (1 or 2 or 4))) return;
        // A new click on the monitor must acquire its own text target, even on the same top-level window.
        if (physicalPoint.HasValue && action == 1 && _buttons == 0) _lastClicked = 0;
        // DWM reports physical pixels, but the receiving window processes messages in its own DPI space.
        nint previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(window));
        try
        {
            Point screen;
            bool physical = true;
            if (physicalPoint is { } supplied) screen = supplied;
            else if (!TryWindowPoint(packet, out screen, out physical)) return;
            if (physical && !PhysicalToLogicalPointForPerMonitorDPI(window, ref screen))
            {
                if (_buttons == 0 || physicalPoint is not { } outside
                    || !TryMapCapturedPoint(outside, out screen)) return;
            }
            RouteMouse(packet, action, button, screen);
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    // Windows can reject point conversion outside the target HWND. During a monitor drag,
    // extrapolate its own window transform so the original control still gets move and up.
    private bool TryMapCapturedPoint(Point point, out Point logical)
    {
        logical = default;
        if (!GetWindowRect(window, out var target)) return false;
        nint previous = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
        if (previous == 0) return false;
        try
        {
            if (!GetWindowRect(window, out var physical)
                || physical.Right <= physical.Left || physical.Bottom <= physical.Top) return false;
            logical.X = target.Left + (int)Math.Round((point.X - physical.Left) * (double)(target.Right - target.Left) / (physical.Right - physical.Left));
            logical.Y = target.Top + (int)Math.Round((point.Y - physical.Top) * (double)(target.Bottom - target.Top) / (physical.Bottom - physical.Top));
            return true;
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private bool TryWindowPoint(ReadOnlySpan<byte> packet, out Point screen, out bool physical)
    {
        screen = default;
        physical = DwmGetWindowAttribute(window, 9, out Rect bounds, Marshal.SizeOf<Rect>()) == 0
            && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
        if (!physical && !GetWindowRect(window, out bounds)) return false;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        screen = new Point
        {
            X = bounds.Left + (int)((bounds.Right - bounds.Left - 1L) * BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]) / 65535L),
            Y = bounds.Top + (int)((bounds.Bottom - bounds.Top - 1L) * BinaryPrimitives.ReadUInt16LittleEndian(packet[5..]) / 65535L)
        };
        return true;
    }

    private void RouteMouse(ReadOnlySpan<byte> packet, byte action, byte button, Point screen)
    {
        nint target = _buttons == 0 ? HitTest(screen) : _pointerTarget;
        Point client = screen;
        if (!Owns(target) || !ScreenToClient(target, ref client))
        {
            if (_buttons != 0) ReleaseButtons();
            return;
        }
        _lastPoint = client;
        // An EDIT handles WM_LBUTTONDOWN by activating its top-level window and taking
        // real mouse capture. Use its selection API while the viewer holds this drag.
        if (button == 1 && action == 1 && _buttons == 0)
        {
            var className = new StringBuilder(16);
            GetClassNameW(target, className, className.Capacity);
            string control = className.ToString();
            if (control.Equals("Edit", StringComparison.OrdinalIgnoreCase))
            {
                if (TryCharacterAt(target, client, out _selectionAnchor))
                {
                    _selectingText = true;
                    _buttons = 1;
                    _pointerTarget = _lastClicked = target;
                    Post(target, 0x00b1, (nuint)_selectionAnchor, _selectionAnchor); // EM_SETSEL
                }
                return;
            }
            if (control.Equals("Button", StringComparison.OrdinalIgnoreCase))
            {
                int style = GetWindowLongW(target, -16) & 15; // BS_TYPEMASK
                if (style is 0 or 1 or 2 or 3 or 5 or 6)
                {
                    _pressedButtonStyle = style;
                    _buttons = 1;
                    _pointerTarget = _lastClicked = target;
                    return;
                }
            }
        }
        if (_pressedButtonStyle >= 0 && action != 3)
        {
            if (action == 2 && button == 1)
            {
                int style = _pressedButtonStyle;
                ReleaseButtons();
                if (HitTest(screen) == target) ClickButton(target, style);
            }
            return;
        }
        if (_selectingText && action != 3)
        {
            if ((action == 0 || action == 2 && button == 1) && TryCharacterAt(target, client, out int index))
                Post(target, 0x00b1, (nuint)_selectionAnchor, index);
            if (action == 2 && button == 1) ReleaseButtons();
            return;
        }
        if (action == 0)
            Post(target, WmMouseMove, MouseFlags(_buttons), Pack(client));
        else if (action == 3)
        {
            int delta = Math.Clamp((int)BinaryPrimitives.ReadInt16LittleEndian(packet[7..]), -1200, 1200);
            if (delta != 0) Post(target, WmWheel, MouseFlags(_buttons) | ((nuint)(ushort)delta << 16), Pack(screen));
        }
        else if (action == 1 && (_buttons & button) == 0)
        {
            uint message = ButtonDown(button);
            long now = Stopwatch.GetTimestamp();
            bool doubleClick = _lastDown.Target == target && _lastDown.Button == button
                && Stopwatch.GetElapsedTime(_lastDown.At, now).TotalMilliseconds <= GetDoubleClickTime()
                && Math.Abs(client.X - _lastDown.Point.X) <= GetSystemMetrics(36) / 2
                && Math.Abs(client.Y - _lastDown.Point.Y) <= GetSystemMetrics(37) / 2
                && (GetClassLongW(target, -26) & 8) != 0; // CS_DBLCLKS
            if (Post(target, doubleClick ? message + 2 : message, MouseFlags(_buttons | button), Pack(client)))
            {
                _lastDown = doubleClick ? default : (target, button, client, now);
                _buttons |= button;
                _pointerTarget = _lastClicked = target;
            }
        }
        else if (action == 2 && (_buttons & button) != 0)
            ButtonUp(button);
    }

    private nuint MouseFlags(int buttons) => (nuint)((buttons & 3) | ((buttons & 4) << 2)
        | (_keys.ContainsKey(0x10) ? 4 : 0) | (_keys.ContainsKey(0x11) ? 8 : 0));

    private static nint Pack(Point pt) => unchecked((int)((uint)(ushort)pt.X | ((uint)(ushort)pt.Y << 16)));
    private static uint ButtonDown(byte button) => button switch { 1 => WmLeftDown, 2 => WmRightDown, _ => WmMiddleDown };

    private void ButtonUp(byte button)
    {
        _buttons &= ~button;
        Post(_pointerTarget, ButtonDown(button) + 1, MouseFlags(_buttons), Pack(_lastPoint));
        if (_buttons == 0) _pointerTarget = 0;
    }

    private static bool IsAppKey(byte key) => key is 0x08 or 0x09 or 0x0D or 0x10 or 0x11 or 0x1B
        or >= 0x21 and <= 0x28 or 0x2D or 0x2E or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A;

    private void Keyboard(byte key, bool down)
    {
        bool held = _keys.TryGetValue(key, out nint target);
        if (!down && !held) return;
        if (!held) target = KeyboardTarget();
        uint scan = MapVirtualKeyW(key, 4); // MAPVK_VK_TO_VSC_EX
        uint flags = 1 | ((scan & 0xff) << 16);
        if ((scan >> 8) == 0xe0) flags |= 1u << 24;
        if (held) flags |= 1u << 30;
        if (!down) flags |= 1u << 31;
        bool posted = Post(target, down ? WmKeyDown : WmKeyUp, key, unchecked((nint)(int)flags));
        if (!down) _keys.Remove(key);
        else if (posted) _keys[key] = target;
    }

    private void Text(ReadOnlySpan<byte> utf8)
    {
        string text;
        try { text = Utf8.GetString(utf8); }
        catch (DecoderFallbackException) { return; }
        if (text.Any(char.IsControl)) return;
        nint target = KeyboardTarget();
        foreach (char ch in text) Post(target, WmChar, ch, 1);
    }

    private void ReleaseButtons()
    {
        if (_selectingText || _pressedButtonStyle >= 0)
        {
            _selectingText = false;
            _pressedButtonStyle = -1;
            _pointerTarget = 0;
            _buttons = 0;
            return;
        }
        for (byte button = 1; button <= 4; button <<= 1)
            if ((_buttons & button) != 0) ButtonUp(button);
    }

    private void ClickButton(nint target, int style)
    {
        // BM_CLICK simulates a physical press and can take focus and mouse capture.
        // Complete ordinary push/check buttons through their control state and BN_CLICKED notification.
        nint parent = GetParent(target);
        if (!Owns(target) || !Owns(parent)) return;
        if (style is 3 or 6)
        {
            if (SendMessageTimeoutW(target, 0x00f0, 0, 0, 0x23, 50, out nuint current) == 0) return; // BM_GETCHECK
            nuint next = style == 6 ? (current + 1) % 3 : current == 0 ? 1u : 0u;
            if (!Owns(target) || SendMessageTimeoutW(target, 0x00f1, next, 0, 0x23, 50, out _) == 0) return; // BM_SETCHECK
        }
        if (GetParent(target) == parent) Post(parent, 0x0111, (ushort)GetDlgCtrlID(target), target); // WM_COMMAND / BN_CLICKED
    }

    private bool TryCharacterAt(nint target, Point client, out int index)
    {
        index = 0;
        if (!Owns(target) || SendMessageTimeoutW(target, 0x00d7, 0, Pack(client), 0x23, 50, out var result) == 0
            || unchecked((int)result) == -1) return false; // EM_CHARFROMPOS
        index = (ushort)result;
        return true;
    }

    private void ReleasePressed()
    {
        ReleaseButtons();
        foreach (byte key in _keys.Keys.ToArray()) Keyboard(key, false);
        _lastDown = default;
        _lastClicked = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            ReleasePressed();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Rect CaretRect;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool PhysicalToLogicalPointForPerMonitorDPI(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint parent, Point client, uint skip);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nuint wp, nint lp, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mode);
    [DllImport("user32.dll")] private static extern uint GetClassLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect rect, int size);
}
