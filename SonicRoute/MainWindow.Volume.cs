using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using SonicRoute.Core;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private CoalescedVolumeWriter? _appVolumeWriter;
        private int _overviewVolumeWriteVersion, _appsVolumeWriteVersion;
        private bool _appsMuteBusy;
        private int _appsMuteVersion;

        private void QueueAppVolume(bool overview, int pid, int percent)
        {
            _appVolumeWriter ??= new CoalescedVolumeWriter(Dispatcher, SessionVolumeService.SetVolumePercentChecked);
            var owner = new WeakReference<MainWindow>(this);
            int version = _appVolumeWriter.Enqueue(pid, percent, (target, request, applied, actual) =>
            {
                if (owner.TryGetTarget(out var window)) window.ApplyAppVolumeWrite(overview, target, request, applied, actual);
            });
            if (overview) { _overviewVolumeWriteVersion = version; ++_overviewStateRequest; }
            else { _appsVolumeWriteVersion = version; ++_appsStateRequest; }
        }

        private void ApplyAppVolumeWrite(bool overview, int pid, int version, bool applied, int actual)
        {
            if (_isClosed) return;
            if (overview)
            {
                if (_overviewApp?.ProcessId != (uint)pid || version != _overviewVolumeWriteVersion) return;
                SetVolumeUi(actual >= 0 ? actual : null);
                OverviewStatusText.Text = applied && actual >= 0
                    ? string.Format(L10n.T("Ov.VolOk"), actual) : L10n.T("Ov.VolFail");
            }
            else
            {
                if (_appsSelected?.ProcessId != (uint)pid || version != _appsVolumeWriteVersion) return;
                SetAppsVolumeUi(actual >= 0 ? actual : null);
                if (!applied || actual < 0) ShowToast(L10n.T("Ov.VolFail"));
            }
        }

        private void OverviewVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isClosed || _suppressVolume || _overviewApp == null || !_overviewVolumeReady) return;
            int percent = (int)Math.Round(e.NewValue);
            OverviewVolumeText.Text = $"{percent}%";
            QueueAppVolume(true, (int)_overviewApp.ProcessId, percent);
        }

        private void AppsVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isClosed || _suppressVolume || _appsSelected == null || !_appsVolumeReady) return;
            int percent = (int)Math.Round(e.NewValue);
            AppsVolumeText.Text = $"{percent}%";
            QueueAppVolume(false, (int)_appsSelected.ProcessId, percent);
        }

        private void AppVolumeDragCompleted(object sender, DragCompletedEventArgs e) => _appVolumeWriter?.Flush();
    }
}
