using System;
using System.Diagnostics;
using System.Runtime;
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

        private bool HasActiveUiWindows() =>
            _mainWindow != null || _quickPanel != null || _trayMenu != null || IsPhoneWindowOpen;

        private void ScheduleUiReclaim()
        {
            if (!ShouldReclaimIdle() || HasActiveUiWindows() || Dispatcher.HasShutdownStarted) return;
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
            if (HasActiveUiWindows() || !ShouldReclaimIdle() || Dispatcher.HasShutdownStarted) return;
            AppIconService.TrimForIdle();
            _trayWheel?.ReleaseIdleOsd();
#if SONICROUTE_PHONE
            AndroidAudioHost.TrimForIdle();
            if (!AndroidAudioHost.HasActiveWork() && !HasActivePhoneClipboard)
            {
                // 无活动投屏、音视频或剪贴板时压实一次 LOH 并归还空页，严禁在 Full 下裁剪整进程工作集。
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            else
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            }
#else
            // Match v1.29: one native working-set request, without a forced full GC.
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
                || HasActiveUiWindows()) return false;
            foreach (Window window in Windows)
                if (window.IsVisible) return false;
            long now = Stopwatch.GetTimestamp();
            if (_lastWorkingSetTrimAt != 0 && now - _lastWorkingSetTrimAt < Stopwatch.Frequency * WorkingSetTrimCooldownSeconds)
                return false; // Do not reschedule; another UI close can request the next pass.
            _lastWorkingSetTrimAt = now;
            bool trimmed;
            try { trimmed = NativeSetProcessWorkingSetSize(NativeCurrentProcess(), new IntPtr(-1), new IntPtr(-1)); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
            return trimmed;
#endif
        }

#if !SONICROUTE_PHONE
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr NativeCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeSetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
#endif
    }
}
