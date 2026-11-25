using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SonicRoute
{
    public partial class App
    {
        private DispatcherTimer? _uiReclaimTimer;
        private bool _uiResourcesDirty = true;
        private bool _startupWarmupComplete;
#if SONICROUTE_IDLE_WORKING_SET_TRIM
        private bool _trimRequestedOnClose;
        private long _lastWorkingSetTrimAt;
        private static int _pendingAudioWrites;
#endif
#if SONICROUTE_PHONE
        private int _retiredPhoneReclaimQueued;
        private bool _phoneResourcesRetired;

        internal void RequestRetiredPhoneReclaim()
        {
            if (_exiting || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _retiredPhoneReclaimQueued, 1) != 0) return;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    Interlocked.Exchange(ref _retiredPhoneReclaimQueued, 0);
                    if (_exiting) return;
                    _phoneResourcesRetired = true;
                    _uiResourcesDirty = true;
                    ScheduleUiReclaim();
                }));
            }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _retiredPhoneReclaimQueued, 0); }
        }
#endif

        private void CancelUiReclaim() => _uiReclaimTimer?.Stop();

        private void ResumeUiIcons()
        {
            CancelUiReclaim();
            _uiResourcesDirty = true;
            AppIconService.Resume();
        }

        internal void ReleaseClosedUiWindow(Window window)
        {
            // The tray application must not keep WPF's first closed window as MainWindow.
            if (ReferenceEquals(MainWindow, window)) MainWindow = null;
            window.DataContext = null;
            _uiResourcesDirty = true;
#if SONICROUTE_IDLE_WORKING_SET_TRIM
            _trimRequestedOnClose = true;
#endif
            ScheduleUiReclaim();
        }

        private bool HasActiveUiWindows() =>
            (_mainWindow?.IsVisible == true && _mainWindow.WindowState != WindowState.Minimized)
            || _quickPanel != null || _trayMenu != null || IsPhoneWindowOpen;

        private void ScheduleUiReclaim()
        {
            if (!ShouldReclaimIdle() || HasActiveUiWindows() || Dispatcher.HasShutdownStarted) return;
            if (_uiReclaimTimer == null)
            {
                _uiReclaimTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher);
                _uiReclaimTimer.Tick += ReclaimUiWhenIdle;
            }
            // Keep a briefly minimized window warm. Closing it switches the same timer
            // to the existing shorter delay; duplicate idle requests cannot postpone it.
            var delay = _mainWindow == null ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromSeconds(30);
            if (_uiReclaimTimer.IsEnabled && _uiReclaimTimer.Interval == delay) return;
            _uiReclaimTimer.Interval = delay;
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

        private bool ShouldReclaimIdle() => _uiResourcesDirty && _startupWarmupComplete && !_exiting;

        private void ReclaimUiWhenIdle(object? sender, EventArgs e)
        {
            // One delayed dispatcher turn, never a periodic cleanup loop.
            _uiReclaimTimer?.Stop();
            if (HasActiveUiWindows() || !ShouldReclaimIdle() || Dispatcher.HasShutdownStarted) return;
            if (_mainWindow != null && !_mainWindow.CanReleaseIdleUiResources) return;
            _mainWindow?.ReleaseIdleUiResources();
            AppIconService.TrimForIdle();
            _trayWheel?.ReleaseIdleOsd();
#if SONICROUTE_PHONE
            AndroidAudioHost.TrimForIdle();
            if (_phoneResourcesRetired)
            {
                _phoneResourcesRetired = false;
                AndroidAudioHost.ReleaseRetiredConnectionsIfIdle();
            }
#endif
#if SONICROUTE_IDLE_WORKING_SET_TRIM
            if (_trimRequestedOnClose)
            {
                _trimRequestedOnClose = false;
                TryTrimClosedWorkingSet();
            }
#endif
            _uiResourcesDirty = false;
        }

        // A committed write can outlive the window. Never retain its owner or delay the write.
        [Conditional("SONICROUTE_IDLE_WORKING_SET_TRIM")]
        internal static void ProtectAudioWriteFromTrim(Task write)
        {
#if SONICROUTE_IDLE_WORKING_SET_TRIM
            if (write.IsCompleted) return;
            Interlocked.Increment(ref _pendingAudioWrites);
            _ = write.ContinueWith(static _ => { Interlocked.Decrement(ref _pendingAudioWrites); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
#endif
        }

#if SONICROUTE_IDLE_WORKING_SET_TRIM
        private void TryTrimClosedWorkingSet()
        {
            // Even a minimized main window still owns its UI. Check again after OSD teardown.
            if (_exiting || Dispatcher.HasShutdownStarted || _mainWindow != null || HasActiveUiWindows()
                || LocalAppOutputMirrors.HasActiveOrChangingRoutes
                || AppPlaybackOutputs.HasPendingChanges || Volatile.Read(ref _pendingAudioWrites) != 0
                || !_hotkeyOutputPlanning.IsCompleted || _hotkeyOutputs.Count != 0 || _volumeHotkeyGate.CurrentCount == 0
                || AutoRuleService.RunningRuleIds().Length != 0) return;
            foreach (Window window in Windows)
                if (window.IsVisible) return;

            long now = Stopwatch.GetTimestamp();
            if (_lastWorkingSetTrimAt != 0 && now - _lastWorkingSetTrimAt < 30L * Stopwatch.Frequency) return;
            _lastWorkingSetTrimAt = now;
            try { NativeSetProcessWorkingSetSize(NativeCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr NativeCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeSetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
#endif
    }
}
