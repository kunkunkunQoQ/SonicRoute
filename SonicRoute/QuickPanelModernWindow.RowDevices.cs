using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.Core.Models;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;

namespace SonicRoute;

public partial class QuickPanelModernWindow
{
    // The same group, labels, device pills and selected styling serve both source types.
    private WrapPanel AddRowDeviceGroup(Panel parent, IEnumerable<AudioDeviceInfo> devices, string title,
        Thickness margin, Action<Button, AudioDeviceInfo> configure)
    {
        var label = new TextBlock { Text = L10n.T(title), FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 3) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        var wrap = new WrapPanel { Margin = margin };
        foreach (var device in devices)
        {
            var button = NewDevButton(device, false);
            configure(button, device);
            wrap.Children.Add(button);
        }
        parent.Children.Add(label); parent.Children.Add(wrap);
        return wrap;
    }

    private bool CollapseNativeDeviceRows(AppRow? except)
    {
        bool changed = false;
        foreach (var other in _rows.Values)
        {
            if (other == except || (!other.Expanded && !other.ExpandWrap.IsAnimating)) continue;
            changed = true; other.Expanded = false; ++other.DeviceBuildVersion;
            other.ExpandWrap.SetExpanded(false, false);
            UiMotion.RotateArrow(other.ExpandArrow, false, false);
            if (other.ExpandButton.IsChecked == true) other.ExpandButton.IsChecked = false;
            CacheCollapsedRow(other);
        }
        return changed;
    }
}
