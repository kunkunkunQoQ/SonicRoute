using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SonicRoute.Packaging
{
    public static class LocalEditionNative
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess
        {
            public int Pid;
            public System.Runtime.InteropServices.ComTypes.FILETIME Started;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint session, uint fileCount, IntPtr files,
            uint processCount, [In] UniqueProcess[] processes, uint serviceCount, IntPtr services);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmShutdown(uint session, uint flags, IntPtr status);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint session);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string windowClass, string title);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        public static bool IsReady(Process process, string title)
        {
            if (process.HasExited) return false;
            IntPtr hwnd = FindWindow(null, title);
            uint pid;
            IntPtr ignored;
            return hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out pid) != 0 && pid == process.Id
                && SendMessageTimeout(hwnd, 0, IntPtr.Zero, IntPtr.Zero, 2, 250, out ignored) != IntPtr.Zero;
        }

        public static void RequestExit(Process process)
        {
            if (process.HasExited) return;
            long created = process.StartTime.ToFileTimeUtc();
            var identity = new UniqueProcess { Pid = process.Id };
            identity.Started.dwLowDateTime = unchecked((int)created);
            identity.Started.dwHighDateTime = (int)(created >> 32);
            uint session;
            Check(RmStartSession(out session, 0, new StringBuilder(33)));
            try
            {
                // Register this process only. Other executables with the same filename are unrelated.
                Check(RmRegisterResources(session, 0, IntPtr.Zero, 1, new[] { identity }, 0, IntPtr.Zero));
                int result = RmShutdown(session, 0, IntPtr.Zero);
                if (!process.WaitForExit(10000))
                    throw new InvalidOperationException("The running copy did not exit normally (Restart Manager " + result + ").");
            }
            finally { RmEndSession(session); }
        }

        private static void Check(int error) { if (error != 0) throw new Win32Exception(error); }
    }
}
