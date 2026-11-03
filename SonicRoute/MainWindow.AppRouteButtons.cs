using System;
using System.Linq;
using ComboBox = System.Windows.Controls.ComboBox;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private void RenderAppsRouteButtons()
        {
            // Reuse the shared retained devices, names, wrapping and active style.
            RenderDeviceQuickButtons(AppsOutputQuickPanel,
                PanelDevices.WithSystemDefault(DisplayDevices(VisibleOutputs), EDataFlow.eRender, _config),
                device => SelectAppsRouteDevice(AppsOutputCombo, device), (AppsOutputCombo.SelectedItem as AudioDeviceInfo)?.Id);
            RenderDeviceQuickButtons(AppsInputQuickPanel,
                _config.ExperimentalMic ? PanelDevices.WithSystemDefault(DisplayDevices(VisibleInputs), EDataFlow.eCapture, _config)
                    : Enumerable.Empty<AudioDeviceInfo>(),
                device => SelectAppsRouteDevice(AppsInputCombo, device), (AppsInputCombo.SelectedItem as AudioDeviceInfo)?.Id);
        }

        private static void SelectAppsRouteDevice(ComboBox combo, AudioDeviceInfo device)
        {
            // The existing combo selection path applies the route and handles errors.
            combo.SelectedItem = combo.Items.OfType<AudioDeviceInfo>().FirstOrDefault(d =>
                string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase));
        }

        private void HighlightAppsRouteButtons()
        {
            // SelectionChanged can run while InitializeComponent is still creating the cards.
            if (AppsOutputQuickPanel != null)
                HighlightDeviceQuickActive(AppsOutputQuickPanel, (AppsOutputCombo.SelectedItem as AudioDeviceInfo)?.Id);
            if (AppsInputQuickPanel != null)
                HighlightDeviceQuickActive(AppsInputQuickPanel, (AppsInputCombo.SelectedItem as AudioDeviceInfo)?.Id);
        }
    }
}
