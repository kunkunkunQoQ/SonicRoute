using System.Runtime.InteropServices;

namespace SonicRoute.Native.Product;

internal static unsafe partial class ShellNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct WindowClass
    {
        internal uint Size, Style;
        internal nint Proc;
        internal int ClassBytes, WindowBytes;
        internal nint Instance, Icon, Cursor, Background, Menu, Name, SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint W;
        internal nint L;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly bool Contains(Point p, int margin = 0) => Left < Right && Top < Bottom
            && p.X >= Left - margin && p.X < Right + margin && p.Y >= Top - margin && p.Y < Bottom + margin;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData
    {
        internal Point Point;
        internal uint Data, Flags, Time;
        internal nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceFilter
    {
        internal uint Size, Type, Reserved;
        internal Guid InterfaceClass;
        internal ushort Name;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NotifyIcon
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, CallbackMessage;
        internal nint Icon;
        internal fixed char Tip[128];
        internal uint State, StateMask;
        internal fixed char Info[256];
        internal uint Version;
        internal fixed char Title[64];
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IconId
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal Guid Guid;
    }
    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint GetModuleHandleW(string? module);
    [LibraryImport("kernel32")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("ole32")] internal static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32")] internal static partial void CoUninitialize();
    [LibraryImport("user32", SetLastError = true)] internal static partial ushort RegisterClassExW(in WindowClass wc);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)] internal static partial int UnregisterClassW(string name, nint module);
    [LibraryImport("user32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] internal static partial nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint module, nint data);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint FindWindowW(string? cls, string title);
    [LibraryImport("user32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] internal static partial uint RegisterWindowMessageW(string name);
    [LibraryImport("user32", SetLastError = true)] internal static partial int GetMessageW(out Message msg, nint window, uint min, uint max);
    [LibraryImport("user32")] internal static partial int TranslateMessage(in Message msg);
    [LibraryImport("user32")] internal static partial nint DispatchMessageW(in Message msg);
    [LibraryImport("user32")] internal static partial nint DefWindowProcW(nint window, uint msg, nuint w, nint l);
    [LibraryImport("user32")] internal static partial void PostQuitMessage(int code);
    [LibraryImport("user32", SetLastError = true)] internal static partial int PostMessageW(nint window, uint msg, nuint w, nint l);
    [LibraryImport("user32")] internal static partial int DestroyWindow(nint window);
    [LibraryImport("user32", SetLastError = true)] internal static partial nuint SetTimer(nint window, nuint id, uint ms, nint callback);
    [LibraryImport("user32")] internal static partial int KillTimer(nint window, nuint id);
    [LibraryImport("user32", SetLastError = true)] internal static partial int RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [LibraryImport("user32")] internal static partial int UnregisterHotKey(nint window, int id);
    [LibraryImport("user32", SetLastError = true)] internal static partial int AddClipboardFormatListener(nint window);
    [LibraryImport("user32")] internal static partial int RemoveClipboardFormatListener(nint window);
    [LibraryImport("wtsapi32", SetLastError = true)] internal static partial int WTSRegisterSessionNotification(nint window, uint flags);
    [LibraryImport("wtsapi32")] internal static partial int WTSUnRegisterSessionNotification(nint window);
    [LibraryImport("user32", SetLastError = true)] internal static partial nint RegisterDeviceNotificationW(nint window, in DeviceFilter filter, uint flags);
    [LibraryImport("user32")] internal static partial int UnregisterDeviceNotification(nint notification);
    [LibraryImport("user32")] internal static partial nint GetForegroundWindow();
    [LibraryImport("user32", SetLastError = true)] internal static partial nint SetWindowsHookExW(int kind, delegate* unmanaged[Stdcall]<int, nuint, nint, nint> callback, nint module, uint thread);
    [LibraryImport("user32")] internal static partial int UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32")] internal static partial nint CallNextHookEx(nint hook, int code, nuint w, nint l);
    [LibraryImport("user32")] internal static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32")] internal static partial nint LoadImageW(nint module, nint resource, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32")] internal static partial nint LoadIconW(nint module, nint id);
    [LibraryImport("user32")] internal static partial int GetSystemMetrics(int index);
    [LibraryImport("shell32")] internal static partial int Shell_NotifyIconW(uint operation, ref NotifyIcon data);
    [LibraryImport("shell32")] internal static partial int Shell_NotifyIconGetRect(in IconId id, out Rect rect);
    [LibraryImport("user32")] internal static partial nint WindowFromPoint(Point point);
    [LibraryImport("user32")] internal static partial nint GetAncestor(nint child, uint flags);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint FindWindowExW(nint parent, nint after, string cls, string? title);
    [LibraryImport("user32")] internal static partial int GetClassNameW(nint window, char* name, int length);
    [LibraryImport("user32")] internal static partial int GetWindowRect(nint window, out Rect rect);
}
