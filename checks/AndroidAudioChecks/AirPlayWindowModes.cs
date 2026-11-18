using System.Runtime.InteropServices;
using System.Threading.Channels;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task AirPlayWindowModesAsync()
    {
        const uint keyDown = 0x100;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await using var window = new NativeVideoWindow("AirPlay window fixture", true, false, null,
            80, 100, 640, 360, acceptsGamepad: false, windowModes: true);
        nint handle = await window.CreateAsync(timeout.Token);
        window.EnsureVisible();
        var events = Channel.CreateUnbounded<(bool Topmost, bool Fullscreen)>();
        window.WindowModeChanged += (topmost, fullscreen) => events.Writer.TryWrite((topmost, fullscreen));
        var initial = Bounds(handle);

        window.SetTopmost(true);
        Check(await Next() == (true, false) && (GetLong(handle, -20) & 8) != 0, "topmost changes the actual window z-order style");
        window.SetFullscreen(true);
        Check(await Next() == (true, true) && (GetLong(handle, -16) & 0x00cf0000) == 0
            && (GetLong(handle, -20) & 1) == 0 && FillsMonitor(handle),
            $"full screen covers its monitor with the caption and frame removed (style {GetLong(handle, -16):x}, window {Bounds(handle)})");
        PostMessage(handle, keyDown, 0x1b, 0);
        Check(await Next() == (true, false) && Bounds(handle).Equals(initial), "Escape restores the original normal window bounds");
        window.SetTopmost(false);
        Check(await Next() == (false, false) && (GetLong(handle, -20) & 8) == 0, "unpin clears the topmost style");

        ShowWindowAsync(handle, 3); // SW_MAXIMIZE
        while (!window.IsMaximized) await Task.Delay(15, timeout.Token);
        PostMessage(handle, keyDown, 0x7a, 0);
        Check(await Next() == (false, true) && FillsMonitor(handle), "F11 enters full screen from a maximized window");
        PostMessage(handle, keyDown, 0x7a, 0);
        Check(await Next() == (false, false) && window.IsMaximized, "F11 leaves the prior maximized placement intact");
        await window.DisposeAsync();
        Check(window.Closed.IsCompletedSuccessfully && window.WindowHandle == 0, "closing the mirror releases its native window");

        ValueTask<(bool, bool)> Next() => events.Reader.ReadAsync(timeout.Token);
    }

    private static bool FillsMonitor(nint window)
    {
        var monitor = new AirPlayMonitor { Size = Marshal.SizeOf<AirPlayMonitor>() };
        return GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor) && Bounds(window).Equals(monitor.Bounds);
    }

    private static AirPlayRect Bounds(nint window) => GetWindowRect(window, out var rect) ? rect : throw new InvalidOperationException();
    [StructLayout(LayoutKind.Sequential)] private readonly record struct AirPlayRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct AirPlayMonitor { internal int Size; internal AirPlayRect Bounds, Work; internal int Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetLong(nint window, int attribute);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, int flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref AirPlayMonitor info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out AirPlayRect rect);
}
