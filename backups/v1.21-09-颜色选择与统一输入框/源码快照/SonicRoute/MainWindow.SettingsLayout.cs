using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private static UIElement BuildSettingsDeviceLabel(AudioDeviceInfo device)
        {
            string full = device.DisplayName ?? "";
            string title = full;
            string detail = "";
            int split = full.IndexOf(" (", StringComparison.Ordinal);
            if (split > 0 && full.EndsWith(")", StringComparison.Ordinal))
            {
                title = full.Substring(0, split);
                detail = full.Substring(split + 2, full.Length - split - 3);
            }
            if (string.IsNullOrWhiteSpace(title)) title = device.DisplayLabel;
            var icon = device.Flow == EDataFlow.eCapture ? AutomationIcons.Microphone
                : device.Id == AudioService.SystemDefaultDeviceId || full.IndexOf("HDMI", StringComparison.OrdinalIgnoreCase) >= 0
                    ? AutomationIcons.Monitor
                    : full.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 || full.Contains("耳机") || full.Contains("耳機")
                        ? AutomationIcons.Headphones : AutomationIcons.Speaker;
            var layout = new Grid { ToolTip = full };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.Children.Add(AutomationIcons.Create(icon, 19));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var heading = new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextPrimary");
            text.Children.Add(heading);
            if (detail.Length > 0)
            {
                var hint = new TextBlock { Text = detail, FontSize = 10, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextSecondary");
                text.Children.Add(hint);
            }
            Grid.SetColumn(text, 1);
            layout.Children.Add(text);
            return layout;
        }

        private void AddFilterCheckBox(ItemsControl list, AudioDeviceInfo device, List<string> hidden, RoutedEventHandler handler)
        {
            var check = new CheckBox
            {
                Content = BuildSettingsDeviceLabel(device), IsChecked = !hidden.Contains(device.Id),
                Tag = device, Style = (Style)FindResource("SettingsDeviceCheck")
            };
            AutomationProperties.SetName(check, device.DisplayName ?? device.DisplayLabel);
            check.Checked += handler;
            check.Unchecked += handler;
            list.Items.Add(check);
        }

        private UIElement MakeNameRow(AudioDeviceInfo device)
        {
            var box = new TextBox
            {
                Text = _config.DeviceNames.TryGetValue(device.Id, out var name) ? name ?? "" : "", Tag = device.Id,
                FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center, MinWidth = 80,
                Margin = new Thickness(16, 0, 0, 0)
            };
            AutomationProperties.SetName(box, device.DisplayName ?? device.DisplayLabel);
            box.TextChanged += DeviceName_TextChanged;
            box.LostKeyboardFocus += NameEdit_LostKeyboardFocus;
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
            layout.Children.Add(BuildSettingsDeviceLabel(device));
            Grid.SetColumn(box, 1);
            layout.Children.Add(box);
            var row = new Border { Child = layout, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            row.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBgAlpha");
            row.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            return row;
        }

        // 内层仅作显示的开关也会冒泡 Checked/Unchecked，不能视为用户改变外层设置。
        private bool IsSettingsChange(object sender, RoutedEventArgs e) =>
            IsLoaded && !_suppressSettings && ReferenceEquals(sender, e.OriginalSource);
    }
}
