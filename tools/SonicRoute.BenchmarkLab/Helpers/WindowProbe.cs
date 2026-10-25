using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SonicRoute.BenchmarkLab.Helpers;

internal static class WindowProbe
{
    private delegate bool EnumCallback(IntPtr window, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr unused);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    public static IntPtr FindMainWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            // Exclude hidden tray/OSD/message windows. This is an external visible
            // native-window proxy, never a first-rendered-frame or data-ready claim.
            if (owner == processId && IsWindowVisible(window) && GetWindowRect(window, out var rect)
                && rect.Right - rect.Left >= 500 && rect.Bottom - rect.Top >= 300)
            { found = window; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static async Task<bool> WaitForMainWindowAsync(Process process, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            if (FindMainWindow(process.Id) != IntPtr.Zero) return true;
            await Task.Delay(50, token);
        }
        return false;
    }
}
