using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Native.Ui;

// One common-control tool for the current hit target. User32 supplies the hover
// delay and dismiss interval; a hidden owner has no tooltip HWND or timer.
internal sealed unsafe class WindowTooltip : IDisposable
{
    private const uint AddTool = 0x0432, DeleteTool = 0x0433, RelayEvent = 0x0407;
    private readonly nint _owner;
    private nint _window, _text;
    private string? _id, _label;

    internal WindowTooltip(nint owner)
    {
        _owner = owner;
        var classes = new InitClasses { Size = 8, Flags = 8 /* ICC_TAB_CLASSES, includes tooltips */ };
        InitCommonControlsEx(ref classes);
        _window = CreateWindowExW(0x08000008 /* NOACTIVATE | TOPMOST */, "tooltips_class32", null,
            0x80000003 /* POPUP | ALWAYSTIP | NOPREFIX */, 0, 0, 0, 0, owner, 0, 0, 0);
        if (_window != 0) SendMessageW(_window, 0x0418 /* TTM_SETMAXTIPWIDTH */, 0, (nint)(400 * GetDpiForWindow(owner) / 96));
    }

    internal void Move(UiHitItem item, nuint keys, nint point)
    {
        if (_window == 0) return;
        if (item.Id != _id || item.Tooltip != _label)
        {
            Clear();
            _id = item.Id;
            _label = item.Tooltip;
            _text = Marshal.StringToHGlobalUni(_label);
            var info = GetInfo();
            SendMessageW(_window, AddTool, 0, (nint)(&info));
        }
        var msg = new Message { Hwnd = _owner, Id = 0x0200, Keys = keys, Point = point,
            Time = unchecked((uint)GetMessageTime()) };
        GetCursorPos(out var cursor);
        msg.X = cursor.X;
        msg.Y = cursor.Y;
        SendMessageW(_window, RelayEvent, 0, (nint)(&msg));
    }

    private ToolInfo GetInfo() => new() { Size = (uint)sizeof(ToolInfo),
        Flags = 0x101 /* IDISHWND | TRANSPARENT */, Owner = _owner, Id = _owner, Text = _text };

    internal void Clear()
    {
        if (_text == 0) return;
        var info = GetInfo();
        SendMessageW(_window, DeleteTool, 0, (nint)(&info));
        Marshal.FreeHGlobal(_text);
        _text = 0;
        _id = _label = null;
    }

    public void Dispose()
    {
        Clear();
        if (_window != 0) DestroyWindow(_window);
        _window = 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct InitClasses { public uint Size, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ToolInfo
    {
        public uint Size, Flags;
        public nint Owner, Id;
        public int Left, Top, Right, Bottom;
        public nint Instance, Text, Param, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public nint Hwnd;
        public uint Id;
        public nuint Keys;
        public nint Point;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X, Y; }
    [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref InitClasses classes);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, nint owner, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nuint keys, nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
}
