using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using SonicRoute.Core;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private CoalescedVolumeWriter? _appVolumeWriter;
        private int _appsVolumeWriteVersion;
        private bool _appsMuteBusy;
        private int _appsMuteVersion;

        private void QueueAppVolume(int pid, int percent)
        {
            _appVolumeWriter ??= new CoalescedVolumeWriter(Dispatcher, SessionVolumeService.SetVolumePercentChecked);
            var owner = new WeakReference<MainWindow>(this);
            int version = _appVolumeWriter.Enqueue(pid, percent, (target, request, applied, actual) =>
            {
                if (owner.TryGetTarget(out var window)) window.ApplyAppVolumeWrite(target, request, applied, actual);
            });
            _appsVolumeWriteVersion = version; ++_appsStateRequest;
        }

        private void ApplyAppVolumeWrite(int pid, int version, bool applied, int actual)
        {
            if (_isClosed) return;
            if (applied && actual >= 0)
                (System.Windows.Application.Current as App)?.NotifyAppVolumeChanged(pid, actual, caller: this);
            if (_appsSelected?.ProcessId != (uint)pid || version != _appsVolumeWriteVersion) return;
            SetAppsVolumeUi(actual >= 0 ? actual : null);
            if (!applied || actual < 0) ShowToast(L10n.T("Ov.VolFail"));
        }

        internal void ApplyAppVolumeReadback(int pid, int actual, bool? muted = null)
        {
            if (_isClosed || !IsVisible) return;
            if (muted.HasValue)
            {
                foreach (var item in _appItems)
                    if (!item.Info.IsExternal && item.ProcessId == (uint)pid) item.IsMuted = muted.Value;
            }
            if (_appsSelected == null || _appsSelected.IsExternal || _appsSelected.ProcessId != (uint)pid) return;
            if (actual >= 0 && _appVolumeWriter?.HasPending(pid) != true && !AppsVolumeSlider.IsMouseCaptureWithin)
            {
                ++_appsVolumeWriteVersion;
                SetAppsVolumeUi(actual);
            }
            if (muted.HasValue && !_appsMuteBusy)
            {
                ++_appsMuteVersion;
                ApplyAppsMuteVisual(muted.Value);
                AppsMuteButton.IsEnabled = _appsVolumeReady;
            }
        }

        private void AppsVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isClosed || _suppressVolume || _appsSelected == null || !_appsVolumeReady) return;
            int percent = (int)Math.Round(e.NewValue);
            AppsVolumeText.Text = $"{percent}%";
            if (_appsSelected.IsExternal) { ApplicationAudio.SetVolumePercent(_appsSelected, percent); return; }
            QueueAppVolume((int)_appsSelected.ProcessId, percent);
        }

        internal void ApplyAppMuteReadback(int pid, bool muted)
        {
            if (_isClosed || !IsVisible) return;
            foreach (var item in _appItems)
                if (!item.Info.IsExternal && item.ProcessId == (uint)pid) item.IsMuted = muted;
            if (_appsSelected == null || _appsSelected.IsExternal || _appsSelected.ProcessId != (uint)pid || _appsMuteBusy) return;
            ++_appsMuteVersion;
            ApplyAppsMuteVisual(muted);
            AppsMuteButton.IsEnabled = _appsVolumeReady;
            if (!_appsVolumeReady) _ = RefreshAppsSelectionAsync();
        }

        internal void ApplyGlobalAppMuteReadback(bool muted)
        {
            if (_isClosed || !IsVisible) return;
            foreach (var item in _appItems)
                if (!item.Info.IsExternal) item.IsMuted = muted;
            if (_appsSelected != null && !_appsSelected.IsExternal && !_appsMuteBusy)
            {
                ++_appsMuteVersion;
                ApplyAppsMuteVisual(muted);
                AppsMuteButton.IsEnabled = _appsVolumeReady;
            }
        }

        internal void RefreshAppRouteReadback(int pid)
        {
            if (_isClosed || !IsVisible || !AppsPage.IsVisible) return;
            if (_appsSelected is { IsExternal: false } selected && selected.ProcessId == (uint)pid)
                _ = RefreshAppsSelectionAsync();
        }

        private void AppVolumeDragCompleted(object sender, DragCompletedEventArgs e) => _appVolumeWriter?.Flush();
    }
}
