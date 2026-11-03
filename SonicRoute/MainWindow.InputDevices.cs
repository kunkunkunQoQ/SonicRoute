using System;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private Task? _inputDeviceLoad;
        private bool _inputDevicesLoaded;
        private bool _initialDeviceLoadComplete;

        private Task EnsureInputDevicesAsync()
        {
            if (_isClosed || _inputDevicesLoaded) return Task.CompletedTask;
            if (_inputDeviceLoad != null && !_inputDeviceLoad.IsCompleted) return _inputDeviceLoad;
            return _inputDeviceLoad = LoadInputDevicesCoreAsync();
        }

        private async Task LoadInputDevicesCoreAsync()
        {
            try
            {
                var inputs = await _uiLifetime.ReadAsync(() =>
                {
                    var devices = AudioService.GetDevices(EDataFlow.eCapture);
                    string? defaultId = AudioService.GetDefaultDeviceId(EDataFlow.eCapture);
                    foreach (var device in devices)
                        device.IsDefault = string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase);
                    return devices;
                });
                if (_isClosed) return;
                _inputs = inputs;
                _inputDevicesLoaded = true;
                // Initial output/input reads are joined by LoadDevicesAsync, which refreshes once.
                if (!_initialDeviceLoadComplete) return;
                ReloadDeviceDisplay();
                if (!_config.ExperimentalMic) return;
                if (AppsPage.Visibility == System.Windows.Visibility.Visible)
                    await RefreshAppsSelectionAsync();
            }
            catch (OperationCanceledException) when (_isClosed) { }
            catch { /* Leave the read retryable after a transient device/service failure. */ }
        }
    }
}
