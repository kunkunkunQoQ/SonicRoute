using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SonicRoute.Core
{
    /// <summary>前台窗口对应的进程检测（GetForegroundWindow → GetWindowThreadProcessId）。</summary>
    public static class ForegroundAppService { public static int BackgroundTestPid = -1, EventTestPidReads;
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>当前前台窗口的 PID；无前台窗口返回 0。</summary>
        public static int GetForegroundProcessId() { System.Threading.Interlocked.Increment(ref EventTestPidReads); if(BackgroundTestPid >= 0) return BackgroundTestPid;
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }

        /// <summary>进程名（不含扩展名），失败返回 null。</summary>
        public static string? GetProcessNameSafe(int pid)
        {
#if NET48
            // Framework 的 ProcessName 会读取系统进程快照；普通进程先按 PID 查询路径。
            try
            {
                using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                if (!handle.IsInvalid)
                {
                    var path = new StringBuilder(1024);
                    int length = path.Capacity;
                    if (QueryFullProcessImageName(handle, 0, path, ref length))
                        return Path.GetFileNameWithoutExtension(path.ToString());
                    if (Marshal.GetLastWin32Error() == 122) // 长路径仅在需要时扩大缓冲区。
                    {
                        path.EnsureCapacity(32768);
                        length = path.Capacity;
                        if (QueryFullProcessImageName(handle, 0, path, ref length))
                            return Path.GetFileNameWithoutExtension(path.ToString());
                    }
                }
            }
            catch { /* 受保护进程等情况沿用原查询与失败语义。 */ }
#endif
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

#if NET48
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
#endif
    }
}
