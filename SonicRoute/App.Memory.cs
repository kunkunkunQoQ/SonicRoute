using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SonicRoute
{
    public partial class App
    {
        private DispatcherTimer? _uiReclaimTimer;
        private bool _uiWorkingSetDirty = true;
        private bool _startupWarmupComplete;
#if SONICROUTE_PHONE
        internal const bool IdleWorkingSetTrimEnabled = false;
#else
        internal const bool IdleWorkingSetTrimEnabled = true;
        private bool _uiWorkingSetTrimRequested;
        private long _lastWorkingSetTrimAt;
        private const int WorkingSetTrimCooldownSeconds = 30;
#endif

        private void CancelUiReclaim() => _uiReclaimTimer?.Stop();

        internal void ReleaseClosedUiWindow(Window window)
        {
            // The tray application must not keep WPF's first closed window as MainWindow.
            if (ReferenceEquals(MainWindow, window)) MainWindow = null;
            window.DataContext = null;
            _uiWorkingSetDirty = true;
#if !SONICROUTE_PHONE
            // Trim only after an actual UI close, not automatically during startup.
            _uiWorkingSetTrimRequested = true;
#endif
            ScheduleUiReclaim();
        }

        private void ScheduleUiReclaim()
        {
            if (!ShouldReclaimIdle() || _mainWindow != null || _quickPanel != null || _trayMenu != null || Dispatcher.HasShutdownStarted) return;
            if (_uiReclaimTimer == null)
            {
                _uiReclaimTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(1500) };
                _uiReclaimTimer.Tick += ReclaimUiWhenIdle;
            }
            _uiReclaimTimer.Stop();
            _uiReclaimTimer.Start();
        }

        private async Task ReclaimAfterWarmupAsync(Task warmup)
        {
            try { await warmup.ConfigureAwait(false); }
            catch { /* A failed warmup must not strand the one-shot idle request. */ }
            try { await Dispatcher.InvokeAsync(() => { _startupWarmupComplete = true; ScheduleUiReclaim(); }).Task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }

        private bool ShouldReclaimIdle() => _uiWorkingSetDirty && _startupWarmupComplete && !_exiting;

        private void ReclaimUiWhenIdle(object? sender, EventArgs e)
        {
            // One delayed dispatcher turn, never a periodic cleanup loop.
            _uiReclaimTimer?.Stop();
            if (_mainWindow != null || _quickPanel != null || _trayMenu != null || !ShouldReclaimIdle()
                || Dispatcher.HasShutdownStarted) return;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
#if !SONICROUTE_PHONE
            TryTrimIdleWorkingSet();
            _uiWorkingSetTrimRequested = false;
#endif
            _uiWorkingSetDirty = false;
        }

        private bool TryTrimIdleWorkingSet()
        {
#if SONICROUTE_PHONE
            // Full may still be carrying live audio/virtual-display/native transport pages.
            return false;
#else
            if (!_uiWorkingSetTrimRequested || !_startupWarmupComplete || _exiting || Dispatcher.HasShutdownStarted
                || _mainWindow != null || _quickPanel != null || _trayMenu != null) return false;
            foreach (Window window in Windows)
                if (window.IsVisible) return false;
            long now = Stopwatch.GetTimestamp();
            if (_lastWorkingSetTrimAt != 0 && now - _lastWorkingSetTrimAt < Stopwatch.Frequency * WorkingSetTrimCooldownSeconds)
                return false; // Do not reschedule; another UI close can request the next pass.
            _lastWorkingSetTrimAt = now;
            bool trimmed;
            try { trimmed = NativeEmptyWorkingSet(NativeCurrentProcess()); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
            return trimmed;
#endif
        }

#if !SONICROUTE_PHONE
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr NativeCurrentProcess();

        [DllImport("psapi.dll", EntryPoint = "EmptyWorkingSet", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeEmptyWorkingSet(IntPtr process);
#endif
    }
}
