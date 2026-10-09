using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SonicRoute.Core;

namespace SonicRoute
{
    public partial class App
    {
        internal MicrophoneStateMonitor? MicMonitor { get; private set; }
        private volatile bool _backgroundStopped;
        private bool _micMuteWatchPending, _ruleProbeRunning;
        private int _micNotificationQueued, _ruleNotificationQueued;
        private DispatcherTimer? _ruleReloadTimer;

        private void StartBackgroundMonitoring()
        {
            MicMonitor = new MicrophoneStateMonitor();
            MicMonitor.Invalidated += MicStateInvalidated;
            _micMuteWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _micMuteWatchTimer.Tick += (_, _) =>
            {
                _ = UpdateMicStateAsync();
                _ = ProbeRuleDirectoryAsync();
            };
            _micMuteWatchTimer.Start();
            _ = UpdateMicStateAsync(); // 首次只建立基线；启动后立即具备变化检测能力。
        }

        private void MicStateInvalidated()
        {
            if (_backgroundStopped || Dispatcher.HasShutdownStarted
                || Interlocked.Exchange(ref _micNotificationQueued, 1) != 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _micNotificationQueued, 0);
                if (!_backgroundStopped) _ = UpdateMicStateAsync();
            }), DispatcherPriority.Background);
        }

        private async Task UpdateMicStateAsync()
        {
            if (_backgroundStopped || MicMonitor == null) return;
            if (_micMuteWatchInProgress) { _micMuteWatchPending = true; return; }
            _micMuteWatchInProgress = true;
            try
            {
                do
                {
                    _micMuteWatchPending = false;
                    bool trackInput = ConfigService.Load().MicMuteOsdTrackInputMuted;
                    long micVersion = _trayWheel?.MicStateVersion ?? 0;
                    bool muted = await MicMonitor.ReadAsync(trackInput);
                    if (_backgroundStopped || Dispatcher.HasShutdownStarted
                        || ConfigService.Load().MicMuteOsdTrackInputMuted != trackInput
                        || (_trayWheel?.MicStateVersion ?? 0) != micVersion) continue;
                    if (!_micMuteBaselineReady)
                    { _micMuteBaselineReady = true; _lastMicMutedBaseline = muted; continue; }
                    if (muted != _lastMicMutedBaseline)
                    {
                        _lastMicMutedBaseline = muted;
                        ShowMicMuteOsd(L10n.T("Ov.MuteMic"), muted);
                    }
                } while (_micMuteWatchPending && !_backgroundStopped);
            }
            catch { }
            finally { _micMuteWatchInProgress = false; }
        }

        private async Task ProbeRuleDirectoryAsync()
        {
            if (_backgroundStopped || _ruleProbeRunning) return;
            _ruleProbeRunning = true;
            try { await Task.Run(AutoRuleStore.CheckForExternalChanges); }
            catch { }
            finally { _ruleProbeRunning = false; }
        }

        private void RulesChanged()
        {
            if (_backgroundStopped || Dispatcher.HasShutdownStarted
                || Interlocked.Exchange(ref _ruleNotificationQueued, 1) != 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _ruleNotificationQueued, 0);
                if (_backgroundStopped) return;
                if (_ruleReloadTimer == null)
                {
                    _ruleReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    _ruleReloadTimer.Tick += (_, _) =>
                    {
                        _ruleReloadTimer.Stop();
                        if (_backgroundStopped) return;
                        try { ReloadHotkeys(); } catch { }
                    };
                }
                _ruleReloadTimer.Stop(); _ruleReloadTimer.Start();
            }), DispatcherPriority.Background);
        }

        private void StopBackgroundMonitoring()
        {
            if (_backgroundStopped) return;
            _backgroundStopped = true;
            _micMuteWatchTimer?.Stop();
            AutoRuleStore.Changed -= RulesChanged;
            _ruleReloadTimer?.Stop(); _ruleReloadTimer = null;
            if (MicMonitor != null)
            {
                MicMonitor.Invalidated -= MicStateInvalidated;
                MicMonitor.Dispose(); MicMonitor = null;
            }
            CurrentAppService.StopForegroundWatcher();
            AutoRuleStore.Shutdown();
        }
    }
}
