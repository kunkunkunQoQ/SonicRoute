using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SonicRoute.Core;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;

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

        private async void IconImport_Click(object sender, RoutedEventArgs e) => await ImportAppIconAsync();

        private void IconImport_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = DroppedAppIconPath(e) == null ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        }

        private async void IconImport_Drop(object sender, System.Windows.DragEventArgs e)
        {
            string? path = DroppedAppIconPath(e);
            e.Effects = path == null ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
            if (path != null) await ImportAppIconAsync(path);
        }

        private string? DroppedAppIconPath(System.Windows.DragEventArgs e)
        {
            if (_appIconImportBusy || _configTransferBusy || _isClosed || AppsListBox.SelectedItem is not AppItem item
                || string.IsNullOrWhiteSpace(item.ProcessName) || (e.AllowedEffects & DragDropEffects.Copy) == 0) return null;
            try
            {
                if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1) return null;
                string extension = Path.GetExtension(paths[0]);
                return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ? paths[0] : null;
            }
            catch { return null; }
        }

        private async Task ImportAppIconAsync(string? sourcePath = null)
        {
            if (_appIconImportBusy || _configTransferBusy || _isClosed || AppsListBox.SelectedItem is not AppItem item) return;
            string processName = item.ProcessName;
            if (string.IsNullOrWhiteSpace(processName)) return;
            if (_config.AppIcons.Count >= AppIconFiles.MaxIcons && !_config.AppIcons.ContainsKey(processName))
            {
                ShowToast(L10n.T("AppIcons.Limit"));
                return;
            }
            if (sourcePath == null)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = L10n.T("AppIcons.Import"), Filter = "PNG / JPEG / ICO|*.png;*.jpg;*.jpeg;*.ico",
                    CheckFileExists = true, Multiselect = false
                };
                if (dialog.ShowDialog(this) != true) return;
                sourcePath = dialog.FileName;
            }
            _appIconImportBusy = true;
            UpdateIconSettingsState();
            try
            {
                byte[] bytes;
                try { bytes = await Task.Run(() => CustomAppIconStore.Prepare(sourcePath)); }
                catch { if (!_isClosed) ShowToast(L10n.T("AppIcons.Invalid")); return; }
                if (_isClosed) return;
                string fileName;
                bool changed = false;
                try { fileName = await Task.Run(() => AppIconFiles.SavePngIfChanged(bytes, out changed)); }
                catch { if (!_isClosed) ShowToast(L10n.T("AppIcons.SaveFailed")); return; }
                if (_isClosed) return;
                string? previous = AppIconFiles.GetFileName(_config, processName);
                if (!ConfigService.TrySetAppIcon(processName, fileName))
                {
                    ShowToast(L10n.T("AppIcons.SaveFailed"));
                    return;
                }
                if (changed) AppIconService.InvalidateCustom(fileName);
                if (changed || previous != fileName) ((App)System.Windows.Application.Current).NotifyAppIconChanged(processName);
                ShowToast(L10n.T("AppIcons.Imported"));
                if (changed || previous != fileName) await Task.Run(() => AppIconFiles.CleanupUnused(ConfigService.Load()));
            }
            finally
            {
                _appIconImportBusy = false;
                if (!_isClosed) UpdateIconSettingsState();
            }
        }

        private async void IconReset_Click(object sender, RoutedEventArgs e)
        {
            if (_appIconImportBusy || _configTransferBusy || AppsListBox.SelectedItem is not AppItem item) return;
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
