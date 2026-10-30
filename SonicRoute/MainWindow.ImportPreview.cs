using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private async Task<bool> ConfirmConfigImportAsync(byte[] prepared)
        {
            var preview = await _uiLifetime.ReadAsync(() =>
            {
                var config = ConfigService.ReadPreparedConfig(prepared);
                if (config == null) return (Config: (AppConfig?)null, MissingDevices: 0, MissingApps: 0);
                var devices = new HashSet<string>(AudioService.GetDevices(EDataFlow.eRender)
                    .Concat(AudioService.GetDevices(EDataFlow.eCapture)).Select(device => device.Id), StringComparer.OrdinalIgnoreCase);
                var deviceChoices = (config.DeviceNames?.Keys ?? Enumerable.Empty<string>())
                    .Concat(config.HiddenOutputDevices ?? new()).Concat(config.HiddenInputDevices ?? new())
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                int missingDevices = deviceChoices.Count(id => !AudioService.IsSystemDefault(id) && !devices.Contains(id));
                var apps = new HashSet<string>(AudioService.GetApps().Select(app => app.ProcessName ?? ""), StringComparer.OrdinalIgnoreCase);
                var appChoices = (config.AppNames?.Keys ?? Enumerable.Empty<string>())
                    .Concat(config.AppIcons?.Keys ?? Enumerable.Empty<string>()).Concat(config.HiddenPanelApps ?? new())
                    .Concat(config.DisabledAutoSwitchApps ?? new());
                int missingApps = appChoices.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(name => !apps.Contains(name));
                return (Config: config, MissingDevices: missingDevices, MissingApps: missingApps);
            });
            if (_isClosed || preview.Config == null) return false;
            string message = string.Format(L10n.T("Exp.ImportPreview"), preview.Config.Hotkeys?.Count ?? 0,
                preview.Config.DeviceNames?.Count ?? 0, preview.Config.AppNames?.Count ?? 0, preview.MissingDevices, preview.MissingApps);
            return System.Windows.MessageBox.Show(this, message, L10n.T("Exp.ImportConfig"), MessageBoxButton.OKCancel,
                MessageBoxImage.Information) == MessageBoxResult.OK;
        }
    }
}
