#if SONICROUTE_PHONE
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

/// <summary>Client-area owner anchoring for app display notices. No position polling.</summary>
internal class VirtualDisplayOverlayWindow : Window
{
    private readonly IVirtualDisplayClient _client;
    private readonly WinEventProc _events;
    private IntPtr _owner, _hook;
    private bool _closed, _queued, _requested;
    protected bool IsClosed => _closed;
    internal VirtualDisplayOverlayWindow(IVirtualDisplayClient client)
    {
        _client = client; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Width = 300; Height = 200;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
        _events = (_, _, hwnd, objectId, _, _, _) => { if (hwnd == _owner && objectId == 0) QueuePosition(); };
        Closed += (_, _) =>
        {
            _closed = true;
            if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
            if (System.Windows.Application.Current is App app) app.ReleaseClosedUiWindow(this);
        };
    }
    internal void SetOverlayVisible(bool visible)
    {
        if (_closed) return;
        _requested = visible;
        if (!visible) { Hide(); return; }
        if (_owner == IntPtr.Zero)
        {
            try
            {
                using var process = Process.GetProcessById(_client.ProcessId); _owner = process.MainWindowHandle;
                if (_owner == IntPtr.Zero) return;
                new WindowInteropHelper(this).Owner = _owner;
                // Owner destruction/show/hide/move/resize only, scoped to its process.
                _hook = SetWinEventHook(0x8001, 0x800B, IntPtr.Zero, _events, (uint)_client.ProcessId, 0, 0);
            }
            catch (ArgumentException) { Close(); return; }
            catch (InvalidOperationException) { Close(); return; }
        }
        Position();
    }
    private void QueuePosition()
    {
        if (_closed || _queued) return; _queued = true;
        Dispatcher.BeginInvoke(new Action(() => { _queued = false; if (!_closed) Position(); }));
    }
    private void Position()
    {
        if (!IsWindow(_owner)) { Close(); return; }
        if (!_requested || IsIconic(_owner) || !IsWindowVisible(_owner)) { if (IsVisible) Hide(); return; }
        if (!GetClientRect(_owner, out var rect)) return;
        var point = new NativePoint(); if (!ClientToScreen(_owner, ref point)) return;
        if (rect.Right <= 0 || rect.Bottom <= 0) return;
        if (!IsVisible) Show();
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, point.X, point.Y, rect.Right, rect.Bottom, 0x14);
    }
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
}
#endif
