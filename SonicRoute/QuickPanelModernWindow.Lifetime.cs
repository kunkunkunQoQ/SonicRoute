using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
        private (string? Id, int Percent, int Version, int Sequence)? _pendingSystemVolume;
        private readonly ConcurrentDictionary<string, int> _latestEndpointWrites = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, int> _latestAppWrites = new();
        private int _appWriteSequence;
        private int _endpointWriteSequence;
        private int _systemVolumeWriteVersion;
        private bool _systemVolumeThrottled;
        private Func<string?, int, (bool Applied, int Actual)> _writeEndpointVolume = SystemVolumeService.SetVolumePercentChecked;
        private Func<int, int, bool> _writeAppVolume = SessionVolumeService.SetVolumePercent;

        private int RegisterEndpointWrite(string? id)
        {
            int sequence = ++_endpointWriteSequence;
            _latestEndpointWrites[id ?? AudioService.SystemDefaultDeviceId] = sequence;
            return sequence;
        }

        private void SchedulePendingVolumeFlush(bool restart = true)
        {
            if (_volDebounce == null)
            {
                _volDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                _volDebounce.Tick += VolDebounce_Tick;
            }
            if (restart && !_systemVolumeThrottled) _volDebounce.Stop();
            if (!_volDebounce.IsEnabled) _volDebounce.Start();
        }

        private void QueueSystemVolume(int percent)
        {
            ++_systemVolumeRequest;
            if (_systemDeviceId != null && _outputVolumeRows.TryGetValue(_systemDeviceId, out var row))
            {
                _pendingOutputVolume.Remove(_systemDeviceId);
                row.Pending = false;
                ++row.Target.Version;
            }
            _pendingSystemVolume = (_systemDeviceId, percent, ++_systemVolumeWriteVersion, RegisterEndpointWrite(_systemDeviceId));
            if (_systemVolumeThrottled) return;
            FlushPendingVolumes(); // First response is immediate; subsequent changes share the bounded timer.
            _systemVolumeThrottled = true;
            SchedulePendingVolumeFlush(false);
        }

        private void SystemVolumeDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            FlushPendingVolumes();
            _systemVolumeThrottled = false;
        }

        private void ApplySystemVolumeWriteReadback(string? id, int version, int actual)
        {
            if (_isClosed || version != _systemVolumeWriteVersion || !string.Equals(id, _systemDeviceId, StringComparison.OrdinalIgnoreCase)) return;
            if (actual >= 0) ApplySystemVolumeReadback(id, actual, _systemMutedForOsd);
            else _ = RefreshSystemVolumeAsync();
        }

        private void FlushPendingVolumes()
        {
            _volDebounce?.Stop();
            if (_pendingVolume.Count == 0 && _pendingOutputVolume.Count == 0 && !_pendingSystemVolume.HasValue) return;
            var system = _pendingSystemVolume;
            _pendingSystemVolume = null;
            var snapshot = new List<(int Pid, int Percent, int Sequence)>();
            foreach (var item in _pendingVolume)
            {
                if (!_latestAppWrites.TryGetValue(item.Key, out int sequence))
                    _latestAppWrites[item.Key] = sequence = ++_appWriteSequence;
                snapshot.Add((item.Key, item.Value, sequence));
            }
            var outputs = new List<(OutputVolumeTarget Target, int Percent, int Version, int Sequence)>(_pendingOutputVolume.Values);
            _pendingVolume.Clear();
            _pendingOutputVolume.Clear();
            _volumeWriteTask = QueueVolumeWrites(_volumeWriteTask, snapshot, outputs, system, _latestEndpointWrites, _latestAppWrites, _writeAppVolume, _writeEndpointVolume, new WeakReference<QuickPanelModernWindow>(this));
        }

        private static Task QueueVolumeWrites(Task previous, List<(int Pid, int Percent, int Sequence)> snapshot,
            List<(OutputVolumeTarget Target, int Percent, int Version, int Sequence)> outputs,
            (string? Id, int Percent, int Version, int Sequence)? system, ConcurrentDictionary<string, int> latest,
            ConcurrentDictionary<int, int> latestApps,
            Func<int, int, bool> writeApp,
            Func<string?, int, (bool Applied, int Actual)> writeEndpoint,
            WeakReference<QuickPanelModernWindow> owner) =>
            previous.ContinueWith(completed =>
            {
                _ = completed.Exception;
                foreach (var item in snapshot)
                    if (latestApps.TryGetValue(item.Pid, out int appSequence) && appSequence == item.Sequence)
                        writeApp(item.Pid, item.Percent);
                foreach (var item in outputs)
                {
                    if (!System.Threading.Volatile.Read(ref item.Target.Available)
                        || !latest.TryGetValue(item.Target.Id, out int sequence) || sequence != item.Sequence) continue;
                    var result = writeEndpoint(item.Target.Id, item.Percent);
                    if (owner.TryGetTarget(out var panel) && !panel._isClosed)
                        panel._uiLifetime.Post(panel.Dispatcher, () => panel.ApplyOutputVolumeWriteReadback(item.Target, item.Version, result.Actual));
                }
                if (system.HasValue && latest.TryGetValue(system.Value.Id ?? AudioService.SystemDefaultDeviceId, out int current)
                    && current == system.Value.Sequence)
                {
                    var item = system.Value;
                    var result = writeEndpoint(item.Id, item.Percent);
                    if (owner.TryGetTarget(out var panel) && !panel._isClosed)
                        panel._uiLifetime.Post(panel.Dispatcher, () => panel.ApplySystemVolumeWriteReadback(item.Id, item.Version, result.Actual));
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
            _appRowsLayoutKey = null;
            _loadTask = null;
            _globalMuteLoadState = null;
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
