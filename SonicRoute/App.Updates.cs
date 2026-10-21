using System;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Updates;

namespace SonicRoute
{
    public partial class App
    {
        internal UpdateService Updates { get; } = new();

        private void StartUpdates(Task warmup)
        {
            Updates.Changed += OnUpdateChanged;
            _ = Updates.StartAsync(warmup);
            if (UpdateBootstrap.Pending is not InstallPlan plan) return;
            // One bounded handshake after startup, not a persistent watchdog or periodic timer.
            _ = ConfirmUpdatedStartupAsync(warmup, plan);
        }

        private async Task ConfirmUpdatedStartupAsync(Task warmup, InstallPlan plan)
        {
            try
            {
                await warmup.ConfigureAwait(false);
                using var started = EventWaitHandle.OpenExisting(plan.EventName("started"));
                using var stop = EventWaitHandle.OpenExisting(plan.EventName("stop"));
                using var finished = EventWaitHandle.OpenExisting(plan.EventName("finished"));
                // Queue behind the normal startup UI work. Readiness means initialized services and a live dispatcher.
                await Dispatcher.InvokeAsync(() => started.Set(), System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task.ConfigureAwait(false);
                int signal = await Task.Run(() => WaitHandle.WaitAny(new WaitHandle[] { finished, stop }, 90000)).ConfigureAwait(false);
                if (signal == 1) await Dispatcher.InvokeAsync(Quit).Task.ConfigureAwait(false);
                else Updates.ReadOutcome();
            }
            catch { /* Worker preserves backups unless it receives readiness. */ }
        }

        private void OnUpdateChanged(string? notes)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_exiting) return;
                // No toast, auto-opened window, focus change or full-screen detection loop.
                if (_trayIcon != null) _trayIcon.Text = ("SonicRoute " + DisplayVersion +
                    (Updates.ShouldNotify ? " → " + Updates.Release!.Tag : ""));
            }));
        }

        internal void ShowUpdateSettings()
        {
            ShowMainWindow();
            _mainWindow?.OpenUpdateSettings();
        }

        internal async Task InstallUpdateAsync()
        {
            if (await Updates.InstallAsync()) Quit();
        }
    }
}
