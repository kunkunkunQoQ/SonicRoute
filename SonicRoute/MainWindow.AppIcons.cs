using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SonicRoute.Core;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private bool _appIconImportBusy;

        private void UpdateIconSettingsState()
        {
            if (IconImportButton == null || IconResetButton == null) return;
            string? name = _appsSelected?.ProcessName;
            bool selected = !string.IsNullOrWhiteSpace(name);
            IconImportButton.IsEnabled = !_appIconImportBusy && selected;
            IconResetButton.IsEnabled = !_appIconImportBusy && selected && AppIconFiles.GetFileName(_config, name!) != null;
        }

        private async void IconImport_Click(object sender, RoutedEventArgs e)
        {
            if (_appIconImportBusy || AppsListBox.SelectedItem is not AppItem item) return;
            string processName = item.ProcessName;
            if (string.IsNullOrWhiteSpace(processName)) return;
            if (_config.AppIcons.Count >= AppIconFiles.MaxIcons && !_config.AppIcons.ContainsKey(processName))
            {
                ShowToast(L10n.T("AppIcons.Limit"));
                return;
            }
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = L10n.T("AppIcons.Import"), Filter = "PNG / JPEG / ICO|*.png;*.jpg;*.jpeg;*.ico",
                CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            _appIconImportBusy = true;
            UpdateIconSettingsState();
            try
            {
                byte[] bytes;
                try { bytes = await Task.Run(() => CustomAppIconStore.Prepare(dialog.FileName)); }
                catch { if (!_isClosed) ShowToast(L10n.T("AppIcons.Invalid")); return; }
                if (_isClosed) return;
                string fileName;
                try { fileName = await Task.Run(() => AppIconFiles.SavePng(bytes)); }
                catch { if (!_isClosed) ShowToast(L10n.T("AppIcons.SaveFailed")); return; }
                if (_isClosed) return;
                if (!ConfigService.TrySetAppIcon(processName, fileName))
                {
                    ShowToast(L10n.T("AppIcons.SaveFailed"));
                    return;
                }
                AppIconService.InvalidateCustom(fileName);
                ((App)System.Windows.Application.Current).NotifyAppIconChanged(processName);
                ShowToast(L10n.T("AppIcons.Imported"));
                await Task.Run(() => AppIconFiles.CleanupUnused(ConfigService.Load()));
            }
            finally
            {
                _appIconImportBusy = false;
                if (!_isClosed) UpdateIconSettingsState();
            }
        }

        private async void IconReset_Click(object sender, RoutedEventArgs e)
        {
            if (_appIconImportBusy || AppsListBox.SelectedItem is not AppItem item) return;
            if (!ConfigService.TrySetAppIcon(item.ProcessName, null))
            {
                ShowToast(L10n.T("AppIcons.SaveFailed"));
                return;
            }
            ((App)System.Windows.Application.Current).NotifyAppIconChanged(item.ProcessName);
            UpdateIconSettingsState();
            ShowToast(L10n.T("AppIcons.ResetDone"));
            _appIconImportBusy = true;
            UpdateIconSettingsState();
            try { await Task.Run(() => AppIconFiles.CleanupUnused(ConfigService.Load())); }
            finally { _appIconImportBusy = false; if (!_isClosed) UpdateIconSettingsState(); }
        }

        internal void RefreshAppIcons(string processName)
        {
            if (_isClosed) return;
            var items = _appItems.Concat(_candidateItems);
            foreach (var combo in new[] { OverviewAppCombo, FixedAppCombo, AutoTriggerAppCombo }.Concat(_autoStepAppCombos))
                if (combo != null) items = items.Concat(combo.Items.OfType<AppItem>());
            foreach (var item in items.Distinct().Where(item => string.Equals(item.ProcessName, processName, StringComparison.OrdinalIgnoreCase)))
                item.ReloadIcon();
            UpdateIconSettingsState();
        }
    }
}
