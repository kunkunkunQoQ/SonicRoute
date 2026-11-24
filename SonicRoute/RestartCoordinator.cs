using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Compat;

namespace SonicRoute
{
    internal static class RestartCoordinator
    {
        private const string Switch = "--sonicroute-restart";
        private static string EventName(string token) => @"Local\SonicRoute.Restart." + token;

        internal static async Task<bool> TryStartAsync(string executable,
            Func<ProcessStartInfo, Process?>? start = null, int readyTimeoutMs = 6000)
        {
            string token = Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token));
            using var exited = new ManualResetEvent(false);
            Process? child = null;
            EventHandler onExit = (_, _) => { try { exited.Set(); } catch (ObjectDisposedException) { } };
            try
            {
                string arguments = Switch + " " + AppInfo.CurrentProcessId + " " + token;
#if !NET48
                if (AppRuntimeIdentity.Current.IsPackaged)
                {
                    string appId = StoreInterop.NativePackageIdentity.GetApplicationUserModelId();
                    int childId = StoreInterop.StoreActivation.Activate(appId, arguments);
                    if (childId == AppInfo.CurrentProcessId)
                        throw new InvalidOperationException("Restart activation returned the current process.");
                    child = Process.GetProcessById(childId);
                }
                else
#endif
                {
                    child = (start ?? Process.Start)(new ProcessStartInfo
                    {
                        FileName = executable, UseShellExecute = true, Arguments = arguments
                    });
                }
                if (child == null) throw new InvalidOperationException("Restart did not return a child process.");
                child.Exited += onExit;
                child.EnableRaisingEvents = true;
                int result = await Task.Run(() => WaitHandle.WaitAny(new WaitHandle[] { ready, exited }, readyTimeoutMs));
                if (result == 0 && !child.HasExited) return true;
                throw new IOException(result == WaitHandle.WaitTimeout ? "Restart readiness timed out." : "Restart process exited before readiness.");
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("Restart.Start", ex); return false; }
            finally
            {
                if (child != null) { child.Exited -= onExit; child.Dispose(); }
            }
        }

        // Acknowledge only after resources initialize. Wait for the old process to
        // release its single-instance mutex before entering the replacement App.Run.
        internal static bool CompleteHandoff(ref string[] args)
        {
            if (args.Length == 0 || args[0] != Switch) return true;
            try
            {
                if (args.Length < 3 || !int.TryParse(args[1], out int parentId) || parentId <= 0
                    || parentId == AppInfo.CurrentProcessId || !Guid.TryParseExact(args[2], "N", out _))
                    throw new ArgumentException("Invalid restart handoff.");
                using var parent = Process.GetProcessById(parentId);
                if (!string.Equals(parent.MainModule?.FileName, AppInfo.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Restart parent belongs to a different executable.");
                using var ready = EventWaitHandle.OpenExisting(EventName(args[2]));
                args = args.Skip(3).ToArray();
                ready.Set();
                if (!parent.WaitForExit(15000)) throw new TimeoutException("Restart parent did not exit.");
                return true;
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("Restart.Handoff", ex); return false; }
        }
    }
}
