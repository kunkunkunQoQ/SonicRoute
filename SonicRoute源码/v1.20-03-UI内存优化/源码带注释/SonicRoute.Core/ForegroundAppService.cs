using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SonicRoute.Core
{
    /// <summary>前台窗口对应的进程检测（GetForegroundWindow → GetWindowThreadProcessId）。</summary>
    public static class ForegroundAppService
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>当前前台窗口的 PID；无前台窗口返回 0。</summary>
        public static int GetForegroundProcessId()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }

        /// <summary>进程名（不含扩展名），失败返回 null。</summary>
        public static string? GetProcessNameSafe(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.ProcessName;
            }
            catch
            {
                return null;
            }
        }
    }
}
