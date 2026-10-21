using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core;

namespace SonicRoute.Updates
{
    internal enum UpdateState { Idle, Checking, Latest, Available, MissingPackage, Downloading, Ready, Preparing, Failed, Cancelled }

    // One application-owned operation. UI windows only observe events; closing one never leaks a subscription.
    // No recurring timer, watcher, scan or resident HTTP connection while idle.
    internal sealed class UpdateService : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource? _download;
        private CancellationTokenSource? _automatic;
        private int _disposed;
        private DateTime _lastManual = DateTime.MinValue;
        private string? _payload;
        private int _operation;
        private readonly Func<HttpMessageHandler>? _handlerFactory;
        internal UpdateService(Func<HttpMessageHandler>? handlerFactory = null) { _handlerFactory = handlerFactory; }
        internal event Action<string?>? Changed;
        internal UpdateState State { get; private set; }
        internal UpdateRelease? Release { get; private set; }
        internal long Bytes { get; private set; }
        internal double BytesPerSecond { get; private set; }
        internal string Error { get; private set; } = "";
        internal string LastOutcome { get; private set; } = "";
        internal bool NotificationsDismissed { get; private set; }
        internal bool IsPackaged => AppRuntimeIdentity.Current.IsPackaged;
        internal bool Busy => Volatile.Read(ref _operation) != 0;
        internal bool ShouldNotify
        {
            get
            {
                if (Release == null || NotificationsDismissed) return false;
                var version = UpdateProtocol.ParseVersion(Release.Tag);
                return version > UpdateProtocol.ParseVersion(App.DisplayVersion) &&
                    version != UpdateProtocol.ParseVersion(ConfigService.Load().IgnoredUpdateVersion);
            }
        }

        internal async Task StartAsync(Task warmup)
        {
            if (IsPackaged || _lifetime.IsCancellationRequested) return;
            // The update-start handshake reads its outcome after the worker finishes.
            if (UpdateBootstrap.Pending == null) ReadOutcome();
            // Not due: do not allocate cancellation or delay work just to return after warmup.
            if (!AutomaticCheckDue()) return;
            using var automatic = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _automatic = automatic;
            try
            {
                await warmup.ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(15), automatic.Token).ConfigureAwait(false);
                if (!AutomaticCheckDue()) return;
                await CheckAsync(false, automatic.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { /* Startup checks must never escape into the audio application's startup. */ }
            finally { _automatic = null; }
        }

        internal static bool AutomaticDue(DateTime last, DateTime now) => last > now || now - last >= TimeSpan.FromHours(24);

        private static bool AutomaticCheckDue()
        {
            var config = ConfigService.Load();
            return config.AutoCheckUpdates && AutomaticDue(config.LastUpdateCheckUtc, DateTime.UtcNow);
        }

        internal async Task CheckAsync(bool manual, CancellationToken automaticToken = default)
        {
            if (IsPackaged || Busy || _lifetime.IsCancellationRequested) return;
            if (manual && DateTime.UtcNow - _lastManual < TimeSpan.FromSeconds(15)) return;
            if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return;
            if (manual) { _lastManual = DateTime.UtcNow; NotificationsDismissed = false; }
            State = UpdateState.Checking; Error = ""; Notify();
            string? notes = null;
            try
            {
                var config = ConfigService.Load();
                config.LastUpdateCheckUtc = DateTime.UtcNow;
                if (manual) config.IgnoredUpdateVersion = "";
                ConfigService.Save(config);
                using var transport = new UpdateTransport(_handlerFactory?.Invoke());
                var release = await transport.CheckAsync(AppRuntimeIdentity.Current.CommandEdition, UpdateBootstrap.Architecture,
                    App.DisplayVersion, manual ? _lifetime.Token : automaticToken).ConfigureAwait(false);
                bool newer = UpdateProtocol.ParseVersion(release.Tag) > UpdateProtocol.ParseVersion(App.DisplayVersion);
                notes = newer ? release.Notes : "";
                // Application state retains only install metadata. Notes belong to the open UI;
                // its existing WindowUiLifetime/ReleaseDeferredPages release all owning references.
                Release = new UpdateRelease { Tag = release.Tag, Url = release.Url, Package = release.Package, Asset = release.Asset };
                _payload = null;
                State = !newer ? UpdateState.Latest
                    : Release.Package == null ? UpdateState.MissingPackage : UpdateState.Available;
                if (State == UpdateState.Available && Release.Package != null)
                {
                    string path = Path.Combine(AppDataRoot.DirectoryPath, "Updates", "downloads", Release.Package.Sha256.ToLowerInvariant() + ".package");
                    if (File.Exists(path))
                    {
                        try { UpdateProtocol.RejectReparsePoints(path); UpdateProtocol.Verify(path, Release.Package.Sha256, Release.Package.Size); _payload = path; State = UpdateState.Ready; }
                        catch { /* Invalid caches are re-downloaded on demand. */ }
                    }
                }
            }
            catch (OperationCanceledException) when (!manual && automaticToken.IsCancellationRequested) { State = UpdateState.Idle; }
            catch (Exception e) { State = UpdateState.Failed; Error = e.GetType().Name + ": " + e.Message; }
            finally { Volatile.Write(ref _operation, 0); Notify(notes); }
        }

        internal void Dismiss(bool ignoreVersion)
        {
            NotificationsDismissed = true;
            if (ignoreVersion && Release != null)
            {
                var config = ConfigService.Load(); config.IgnoredUpdateVersion = Release.Tag; ConfigService.Save(config);
            }
            Notify();
        }

        internal async Task DownloadAsync()
        {
            if (IsPackaged || Busy || Release?.Package == null || _lifetime.IsCancellationRequested) return;
            var release = Release;
            if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return;
            State = UpdateState.Downloading; Bytes = 0; Error = ""; Notify();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _download = cancel;
            try
            {
                string cache = Path.Combine(AppDataRoot.DirectoryPath, "Updates", "downloads");
                UpdateProtocol.RejectReparsePoints(cache);
                Directory.CreateDirectory(cache);
                // Content-addressed, one stable filename per verified official package. No remote path names used.
                string path = Path.Combine(cache, release.Package.Sha256.ToLowerInvariant() + ".package");
                await Task.Run(() => Cleanup(cache, path), cancel.Token).ConfigureAwait(false);
                using var transport = new UpdateTransport(_handlerFactory?.Invoke());
                await transport.DownloadAsync(release, path, (bytes, speed) =>
                    { Bytes = bytes; BytesPerSecond = speed; Notify(); }, cancel.Token).ConfigureAwait(false);
                _payload = path; State = UpdateState.Ready;
            }
            catch (Exception e)
            {
                State = cancel.IsCancellationRequested ? UpdateState.Cancelled : UpdateState.Failed;
                Error = e.GetType().Name + ": " + e.Message;
            }
            finally { _download = null; Volatile.Write(ref _operation, 0); Notify(); }
        }

        internal void CancelDownload() { try { _download?.Cancel(); } catch (ObjectDisposedException) { } }

        internal void AutomaticSettingChanged(bool enabled)
        { if (!enabled) { try { _automatic?.Cancel(); } catch (ObjectDisposedException) { } } }

        // Start and positively acknowledge the local helper before asking the main application to exit.
        internal async Task<bool> InstallAsync()
        {
            if (IsPackaged || Busy || State != UpdateState.Ready || Release == null || _payload == null) return false;
            if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) return false;
            State = UpdateState.Preparing; Error = ""; Notify();
            try
            {
                var plan = await Task.Run(() => UpdateInstaller.Prepare(Release, _payload, UpdateBootstrap.ExecutablePath,
                    AppDataRoot.DirectoryPath), _lifetime.Token).ConfigureAwait(false);
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, plan.EventName("ready"));
                using var process = UpdateBootstrap.Start(plan.Runner, "--update-worker " + UpdateBootstrap.Quote(plan.PlanPath), plan.DataDirectory);
                if (!await Task.Run(() => ready.WaitOne(10000)).ConfigureAwait(false)) throw new IOException("Update helper did not become ready.");
                return true;
            }
            catch (Exception e) { State = UpdateState.Failed; Error = e.GetType().Name + ": " + e.Message; return false; }
            finally { Volatile.Write(ref _operation, 0); Notify(); }
        }

        internal void ReadOutcome()
        {
            try
            {
                string path = Path.Combine(AppDataRoot.DirectoryPath, "Updates", "last-result.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 16384) return;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.GetProperty("TargetExe").GetString()!.Equals(UpdateBootstrap.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
                string status = doc.RootElement.GetProperty("Status").GetString() ?? "";
                LastOutcome = status == "committed" ? "Upd.InstallSucceeded" : status == "rolledBack" ? "Upd.RolledBack" : status == "aborted" ? "Upd.Failed" : "Upd.RecoveryPending";
                Notify();
            }
            catch { }
        }

        private static void Cleanup(string downloads, string keep)
        {
            // Only on a user-requested download; bounded to this updater-owned directory, never user data.
            foreach (var file in new DirectoryInfo(downloads).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(2).Take(128))
            {
                if (file.FullName == keep || DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromDays(7)) continue;
                try { UpdateProtocol.RejectReparsePoints(file.FullName); file.Delete(); } catch { }
            }
            string transactions = Path.Combine(Path.GetDirectoryName(downloads)!, "transactions");
            if (!Directory.Exists(transactions)) return;
            foreach (string directory in Directory.GetDirectories(transactions).Take(64))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromDays(7)) continue;
                    var plan = UpdateInstaller.ReadPlan(Path.Combine(directory, "plan.json"));
                    UpdateInstaller.ValidatePlan(plan, plan.Package.Edition, plan.Package.Architecture);
                    if (File.Exists(Path.Combine(plan.TargetDirectory, UpdateInstaller.MarkerName))) continue;
                    if (plan.Status == "committed" || plan.Status == "rolledBack" || plan.Status == "aborted")
                    {
                        UpdateInstaller.SafeDeleteTree(plan.WorkDirectory);
                        UpdateInstaller.SafeDeleteTree(directory);
                    }
                }
                catch { }
            }
        }

        private void Notify(string? notes = null) { if (!_lifetime.IsCancellationRequested) Changed?.Invoke(notes); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel(); CancelDownload(); Changed = null; _lifetime.Dispose();
        }
    }
}
