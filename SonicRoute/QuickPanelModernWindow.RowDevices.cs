using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;

namespace SonicRoute;

public partial class QuickPanelModernWindow
{
    private IEnumerable<AudioDeviceInfo> RowPlaybackDevices(AudioAppInfo app, AppConfig config) =>
        ApplicationAudio.OutputsForApp(app, _outputDisplay).Where(device => !config.HiddenOutputDevices.Contains(device.Id));

    private void AddRowDeviceGroup(Panel parent, IEnumerable<AudioDeviceInfo> devices, EDataFlow flow,
        Action<Button, AudioDeviceInfo> configure, Button? trailingAction = null)
    {
        bool playback = flow == EDataFlow.eRender;
        var label = new TextBlock { FontSize = 11,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 3) };
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(playback ? "[Qp.Output]" : "[Qp.Input]") { Source = L10n.Instance });
        label.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");

        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, playback ? 8 : 4) };
        foreach (var device in devices)
        {
            var button = PanelDevices.CreateShortcut(device);
            button.ToolTipOpening += (_, e) => e.Handled = true;
            button.SetResourceReference(StyleProperty, "DevButton");
            configure(button, device);
            wrap.Children.Add(button);
        }
        if (trailingAction != null) wrap.Children.Add(trailingAction);
        parent.Children.Add(label);
        parent.Children.Add(wrap);
    }

    private void SetRowDeviceSelection(Button button, string id, string selected)
    {
        string style = string.Equals(id, selected, StringComparison.OrdinalIgnoreCase) ? "DevButtonActive" : "DevButton";
        if (!ReferenceEquals(button.Style, FindResource(style))) button.SetResourceReference(StyleProperty, style);
    }

    private bool CollapseNativeDeviceRows(AppRow? except)
    {
        bool changed = false;
        foreach (var other in _rows.Values)
        {
            if (other == except || (!other.Expanded && other.ExpandWrap?.IsAnimating != true)) continue;
            changed = true; other.Expanded = false; ++other.DeviceBuildVersion;
            if (other.ExpandWrap == null) continue;
            other.ExpandWrap.SetExpanded(false, false);
            UiMotion.RotateArrow(other.ExpandArrow, false, false);
            if (other.ExpandButton.IsChecked == true) other.ExpandButton.IsChecked = false;
            CacheCollapsedRow(other);
        }
        return changed;
    }
}
