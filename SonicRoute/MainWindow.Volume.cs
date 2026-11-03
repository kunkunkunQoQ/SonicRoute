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
            if (_appsSelected?.ProcessId != (uint)pid || version != _appsVolumeWriteVersion) return;
            SetAppsVolumeUi(actual >= 0 ? actual : null);
            if (!applied || actual < 0) ShowToast(L10n.T("Ov.VolFail"));
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
            if (_isClosed || _appsSelected?.ProcessId != (uint)pid || _appsMuteBusy) return;
            ++_appsMuteVersion;
            ApplyAppsMuteVisual(muted);
            AppsMuteButton.IsEnabled = _appsVolumeReady;
            if (!_appsVolumeReady && IsVisible) _ = RefreshAppsSelectionAsync();
        }

        private void AppVolumeDragCompleted(object sender, DragCompletedEventArgs e) => _appVolumeWriter?.Flush();
    }
}
