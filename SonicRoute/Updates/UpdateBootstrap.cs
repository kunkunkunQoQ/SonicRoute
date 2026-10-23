using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SonicRoute.Updates
{
    // This path runs before App construction: no audio services, configuration migration or tray.
    internal static class UpdateBootstrap
    {
        internal static InstallPlan? Pending { get; private set; }
        internal static string ExecutablePath
        {
            get
            {
#if NET48
                // Share the existing process identity cache; keep the original query as
                // a recovery path if the initial cached lookup could not obtain a path.
                var cached = SonicRoute.Core.Compat.AppInfo.ExecutablePath;
                if (!string.IsNullOrEmpty(cached)) return cached;
#endif
                using var process = Process.GetCurrentProcess();
                return process.MainModule!.FileName!;
            }
        }
        internal static string Architecture
        {
            get
            {
#if NET48
                return "x64";
#else
                return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                    System.Runtime.InteropServices.Architecture.X64 => "x64",
                    _ => throw new PlatformNotSupportedException("Unsupported update architecture.")
                };
#endif
            }
        }
        internal static string Quote(string value)
        {
            // These arguments are absolute file paths (quotes are not valid Windows filename characters).
            if (value.IndexOf('"') >= 0 || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid update argument.");
            return "\"" + value + new string('\\', value.Length - value.TrimEnd('\\').Length) + "\"";
        }

        internal static bool BeforeStartup(string[] args, string dataDirectory, string edition, bool packaged, string mutexName, Action<string> alert)
        {
            bool worker = args.Length == 2 && (args[0] == "--update-worker" || args[0] == "--update-recover");
            bool updated = args.Length == 2 && args[0] == "--update-start";
            bool internalCommand = Array.Exists(args, a => a.StartsWith("--update-", StringComparison.Ordinal));
            if (packaged)
            {
                if (internalCommand) alert("Upd.Store");
                return !internalCommand;
            }
            if (worker)
            {
                RunWorker(args[1], args[0] == "--update-recover", dataDirectory, edition, mutexName);
                return false;
            }
            if (internalCommand && !updated) return false;
            using var gate = new Mutex(false, UpdateInstaller.GateName(edition));
            bool available = TryAcquire(gate, 0);
            if (available) gate.ReleaseMutex();
            string marker = Path.Combine(Path.GetDirectoryName(ExecutablePath)!, UpdateInstaller.MarkerName);
            if (updated)
            {
                if (!Guid.TryParseExact(args[1], "N", out _) || available || !File.Exists(marker) || File.ReadAllText(marker) != args[1]) return false;
                var p = UpdateInstaller.ReadPlan(Path.Combine(dataDirectory, "Updates", "transactions", args[1], "plan.json"));
                UpdateInstaller.ValidatePlan(p, edition, Architecture);
                if (!p.TargetExe.Equals(ExecutablePath, StringComparison.OrdinalIgnoreCase) || p.Status != "starting") return false;
                Pending = p;
                return true;
            }
            if (!available) { alert("Upd.InProgress"); return false; }
            if (File.Exists(marker))
            {
                string id = File.ReadAllText(marker);
                if (!Guid.TryParseExact(id, "N", out _)) { alert("Upd.RecoveryRequired"); return false; }
                string path = Path.Combine(dataDirectory, "Updates", "transactions", id, "plan.json");
                var p = UpdateInstaller.ReadPlan(path);
                UpdateInstaller.ValidatePlan(p, edition, Architecture);
                if (!p.TargetExe.Equals(ExecutablePath, StringComparison.OrdinalIgnoreCase)) { alert("Upd.RecoveryRequired"); return false; }
                Start(p.Runner, "--update-recover " + Quote(path), p.DataDirectory);
                return false;
            }
            return true;
        }

        internal static Process Start(string executable, string arguments, string dataDirectory)
        {
            var info = new ProcessStartInfo(executable, arguments) { UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable)!, CreateNoWindow = true };
            info.EnvironmentVariables["SONICROUTE_DATA_DIRECTORY"] = dataDirectory;
            return Process.Start(info) ?? throw new IOException("Could not start update process.");
        }

        internal static bool TryAcquire(Mutex mutex, int milliseconds)
        {
            try { return mutex.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { return true; }
        }

        private static void RunWorker(string path, bool recover, string dataDirectory, string edition, string instanceMutex)
        {
            InstallPlan? plan = null;
            bool instanceOwned = false;
            bool gateOwned = false;
            using var gate = new Mutex(false, UpdateInstaller.GateName(edition));
            Mutex instance = new Mutex(false, instanceMutex);
            Process? child = null;
            EventWaitHandle? finished = null;
            bool validated = false;
            bool restart = false;
            try
            {
                plan = UpdateInstaller.ReadPlan(path);
                UpdateInstaller.ValidatePlan(plan, edition, Architecture);
                if (!Path.GetFullPath(path).Equals(plan.PlanPath, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFullPath(dataDirectory).Equals(plan.DataDirectory, StringComparison.OrdinalIgnoreCase)
                    || !ExecutablePath.Equals(plan.Runner, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Untrusted worker plan.");
                validated = true;
                if ((!recover && plan.Status != "prepared") || (recover &&
                    (!File.Exists(Path.Combine(plan.TargetDirectory, UpdateInstaller.MarkerName)) ||
                    File.ReadAllText(Path.Combine(plan.TargetDirectory, UpdateInstaller.MarkerName)) != plan.Id)))
                    throw new InvalidDataException("Update transaction is not pending.");
                gateOwned = TryAcquire(gate, 0);
                if (!gateOwned) { validated = false; return; }
                if (!recover)
                {
                    using var parent = Process.GetProcessById(plan.ParentId);
                    if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks
                        || !parent.MainModule!.FileName!.Equals(plan.TargetExe, StringComparison.OrdinalIgnoreCase)) throw new IOException("Update parent changed.");
                    using (var ready = EventWaitHandle.OpenExisting(plan.EventName("ready"))) ready.Set();
                    if (!parent.WaitForExit(30000)) throw new IOException("The running application did not exit.");
                }
                instanceOwned = TryAcquire(instance, 10000);
                if (!instanceOwned) throw new IOException("Another application instance is still running.");
                if (recover)
                {
                    if (plan.Status == "committed") UpdateInstaller.Commit(plan);
                    else UpdateInstaller.Rollback(plan);
                    restart = true;
                    return;
                }
                UpdateInstaller.Apply(plan);
                using var acknowledged = new EventWaitHandle(false, EventResetMode.ManualReset, plan.EventName("started"));
                using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, plan.EventName("stop"));
                finished = new EventWaitHandle(false, EventResetMode.ManualReset, plan.EventName("finished"));
                instance.ReleaseMutex(); instanceOwned = false;
                // App's existing single-instance contract uses createdNew, not only ownership.
                // Closing the last handle is required before launching the updated application.
                instance.Dispose();
                child = Start(plan.TargetExe, "--update-start " + plan.Id, plan.DataDirectory);
                using var exited = new EventWaitHandle(false, EventResetMode.ManualReset);
                exited.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(child.Handle, false);
                bool started = WaitHandle.WaitAny(new WaitHandle[] { acknowledged, exited }, 45000) == 0;
                if (!started || child.WaitForExit(5000))
                {
                    plan.Error = "Updated process did not complete the startup confirmation.";
                    stop.Set();
                    if (!child.WaitForExit(10000)) throw new IOException("New process did not stop; backups retained for recovery.");
                    instance = new Mutex(false, instanceMutex);
                    instanceOwned = TryAcquire(instance, 10000);
                    if (!instanceOwned) throw new IOException("Cannot roll back while an instance is running.");
                    UpdateInstaller.Rollback(plan);
                    restart = true;
                }
                else UpdateInstaller.Commit(plan);
            }
            catch (Exception error)
            {
                if (validated && plan != null)
                {
                    try
                    {
                        plan.Error = error.GetType().Name + ": " + error.Message;
                        if (plan.Status == "prepared") plan.Status = "aborted";
                        if (plan.Status == "starting" && (child == null || child.HasExited))
                        {
                            instance.Dispose(); instance = new Mutex(false, instanceMutex);
                            instanceOwned = TryAcquire(instance, 10000);
                            if (instanceOwned) UpdateInstaller.Rollback(plan);
                        }
                        // Never restore over a process that may still be using the installed files.
                        if (plan.Status == "installing" && instanceOwned) UpdateInstaller.Rollback(plan);
                        UpdateInstaller.WritePlan(plan);
                        restart = plan.Status == "rolledBack";
                    }
                    catch { /* Leave the journal and backups intact for explicit/startup recovery. */ }
                }
            }
            finally
            {
                if (validated && plan != null) UpdateInstaller.WriteOutcome(plan);
                finished?.Set();
                finished?.Dispose();
                child?.Dispose();
                if (instanceOwned) instance.ReleaseMutex();
                instance.Dispose();
                if (gateOwned) gate.ReleaseMutex();
                // Reuse this single cleanup path for recovery and rollback. The next ordinary
                // startup must see the outcome and closed mutex handles before it starts.
                if (restart && plan != null) Start(plan.TargetExe, "--main", plan.DataDirectory).Dispose();
            }
        }
    }
}
