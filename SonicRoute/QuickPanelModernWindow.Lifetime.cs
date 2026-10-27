using System;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using SonicRoute.Core;
using Application = System.Windows.Application;

namespace SonicRoute
{
    public partial class QuickPanelModernWindow
    {
        private readonly WindowUiLifetime _uiLifetime = new();
        private Task _volumeWriteTask = Task.CompletedTask;

        private void SchedulePendingVolumeFlush()
        {
            if (_volDebounce == null)
            {
                _volDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                _volDebounce.Tick += VolDebounce_Tick;
            }
            _volDebounce.Stop();
            _volDebounce.Start();
        }

        private void FlushPendingVolumes()
        {
            _volDebounce?.Stop();
            if (_pendingVolume.Count == 0 && _pendingOutputVolume.Count == 0) return;
            var snapshot = new List<KeyValuePair<int, int>>(_pendingVolume);
            var outputs = new List<(OutputVolumeTarget Target, int Percent, int Version)>(_pendingOutputVolume.Values);
            _pendingVolume.Clear();
            _pendingOutputVolume.Clear();
            _volumeWriteTask = QueueVolumeWrites(_volumeWriteTask, snapshot, outputs, new WeakReference<QuickPanelModernWindow>(this));
        }

        private static Task QueueVolumeWrites(Task previous, List<KeyValuePair<int, int>> snapshot,
            List<(OutputVolumeTarget Target, int Percent, int Version)> outputs, WeakReference<QuickPanelModernWindow> owner) =>
            previous.ContinueWith(completed =>
            {
                _ = completed.Exception;
                foreach (var item in snapshot) SessionVolumeService.SetVolumePercent(item.Key, item.Value);
                foreach (var item in outputs)
                {
                    if (!System.Threading.Volatile.Read(ref item.Target.Available)) continue;
                    var result = SystemVolumeService.SetVolumePercentChecked(item.Target.Id, item.Percent);
                    if (owner.TryGetTarget(out var panel) && !panel._isClosed)
                        panel._uiLifetime.Post(panel.Dispatcher, () => panel.ApplyOutputVolumeWriteReadback(item.Target, item.Version, result.Actual));
                }
            }, TaskScheduler.Default);

        private void CleanupClosedWindow()
        {
            if (_isClosed) return;
            _isClosed = true;
            ++_loadVersion; ++_systemVolumeRequest; ++_globalMuteVersion; ++_micMuteVersion;
            // Pending slider changes are committed independently of UI cancellation.
            FlushPendingVolumes();
            StopOutputVolumes();
            LocationChanged -= OutputOwnerLocationChanged;
            if (_volDebounce != null) _volDebounce.Tick -= VolDebounce_Tick;
            _volDebounce = null;
            StopMeter();
            _uiLifetime.Dispose();
            _entranceMotion.Stop();
            ClearRowVisuals();
            CurrentAppService.CurrentChanged -= OnSharedCurrentChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParamChanged;
            _suppressDevCombo = true;
            _systemReady = _meterEnabled = false;
            SystemDevCombo.ItemsSource = null;
            SystemDevCombo.Items.Clear();
            AppListPanel.Items.Clear();
            _rows.Clear(); _recentDeviceRow = _scrollingRow = null;
            _outputDisplay = new(); _inputDisplay = new();
            _systemDeviceId = _appRowsSignature = null;
            _loadTask = null;
            lock (_peakGate) _latestPeaks = null;
            if (_adjustMode)
            {
                _adjustMode = _adjustDragging = false;
                ReleaseMouseCapture();
                ((App)Application.Current).NotifyQuickPanelAdjustFinished();
            }
        }
    }
}
