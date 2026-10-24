using System;
using System.Collections.Generic;
using System.Linq;
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
        private UIElement BuildSettingsDeviceLabel(AudioDeviceInfo device)
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
            var heading = new TextBlock { Text = SettingsDeviceTitle(device, title), Tag = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
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

        private string SettingsDeviceTitle(AudioDeviceInfo device, string original) =>
            _config.DeviceNames.TryGetValue(device.Id, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : original;

        private void UpdateSettingsDeviceTitle(UIElement label, AudioDeviceInfo device)
        {
            var heading = (TextBlock)((StackPanel)((Grid)label).Children[1]).Children[0];
            string title = SettingsDeviceTitle(device, (string)heading.Tag);
            if (heading.Text != title) heading.Text = title;
        }

        // Reuse the existing name-save completion; never rebuild editors just to show an alias.
        private void RefreshSettingsDeviceTitles()
        {
            if (SettingsPage == null) return;
            foreach (var list in new[] { OutputFilterList, InputFilterList })
                foreach (CheckBox check in list.Items) UpdateSettingsDeviceTitle((UIElement)check.Content, (AudioDeviceInfo)check.Tag);
            foreach (var list in new[] { OutputNameList, InputNameList })
                foreach (Border row in list.Items) UpdateSettingsDeviceTitle(((Grid)row.Child).Children[0], (AudioDeviceInfo)row.Tag);
        }

        private CheckBox MakeFilterCheckBox(AudioDeviceInfo device, List<string> hidden, RoutedEventHandler handler)
        {
            var check = new CheckBox
            {
                Content = BuildSettingsDeviceLabel(device), IsChecked = !hidden.Contains(device.Id),
                Tag = device, Style = (Style)FindResource("SettingsDeviceCheck")
            };
            AutomationProperties.SetName(check, device.DisplayName ?? device.DisplayLabel);
            check.Checked += handler;
            check.Unchecked += handler;
            return check;
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
            var row = new Border { Child = layout, Tag = device, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            row.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceBgAlpha");
            row.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            return row;
        }

        private static IEnumerable<AudioDeviceInfo> SettingsDevices(IEnumerable<AudioDeviceInfo> devices, EDataFlow flow)
        {
            bool output = flow == EDataFlow.eRender;
            yield return new AudioDeviceInfo { Id = output ? AudioService.SystemDefaultDeviceId : AudioService.SystemDefaultInputDeviceId,
                DisplayName = L10n.T(output ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn"), Flow = flow };
            foreach (var device in devices) yield return device;
        }

        // Only runs at existing invalidation points, never on a timer or every expand click.
        private static Dictionary<string, FrameworkElement> SettingsRows(ItemsControl list, List<AudioDeviceInfo> devices)
        {
            var ids = new HashSet<string>(devices.Select(device => device.Id), StringComparer.Ordinal);
            // Remove obsolete rows first, so deleting a preceding device does not detach a surviving editor.
            for (int i = list.Items.Count - 1; i >= 0; i--)
                if (list.Items[i] is FrameworkElement { Tag: AudioDeviceInfo device } && !ids.Contains(device.Id)) list.Items.RemoveAt(i);
            return list.Items.OfType<FrameworkElement>().ToDictionary(row => ((AudioDeviceInfo)row.Tag).Id, StringComparer.Ordinal);
        }

        private static bool SameSettingsDeviceLabel(AudioDeviceInfo previous, AudioDeviceInfo current) =>
            previous.DisplayName == current.DisplayName && previous.Flow == current.Flow && previous.IsDefault == current.IsDefault;

        private static void PlaceSettingsRow(ItemsControl list, FrameworkElement row, int index)
        {
            if (index < list.Items.Count && ReferenceEquals(list.Items[index], row)) return;
            int oldIndex = list.Items.IndexOf(row);
            if (oldIndex >= 0) list.Items.RemoveAt(oldIndex);
            if (index == list.Items.Count) list.Items.Add(row);
            else list.Items.Insert(index, row);
        }

        private void SyncDeviceFilter(ItemsControl list, IEnumerable<AudioDeviceInfo> devices, List<string> hidden, RoutedEventHandler handler)
        {
            if (list.Items.Count == 0)
            {
                foreach (var device in devices) list.Items.Add(MakeFilterCheckBox(device, hidden, handler));
                return;
            }
            var targets = devices.ToList();
            if (targets.Count == 0) { if (list.Items.Count != 0) list.Items.Clear(); return; }
            var previous = SettingsRows(list, targets);
            int index = 0;
            foreach (var device in targets)
            {
                var check = previous.TryGetValue(device.Id, out var old) ? (CheckBox)old : MakeFilterCheckBox(device, hidden, handler);
                if (!SameSettingsDeviceLabel((AudioDeviceInfo)check.Tag, device)) check.Content = BuildSettingsDeviceLabel(device);
                else UpdateSettingsDeviceTitle((UIElement)check.Content, device);
                check.Tag = device;
                check.IsChecked = !hidden.Contains(device.Id);
                AutomationProperties.SetName(check, device.DisplayName ?? device.DisplayLabel);
                PlaceSettingsRow(list, check, index++);
            }
        }

        private void SyncDeviceNames(ItemsControl list, IEnumerable<AudioDeviceInfo> devices)
        {
            if (list.Items.Count == 0)
            {
                foreach (var device in devices) list.Items.Add(MakeNameRow(device));
                return;
            }
            var targets = devices.ToList();
            if (targets.Count == 0) { if (list.Items.Count != 0) list.Items.Clear(); return; }
            var previous = SettingsRows(list, targets);
            int index = 0;
            foreach (var device in targets)
            {
                var row = previous.TryGetValue(device.Id, out var old) ? (Border)old : (Border)MakeNameRow(device);
                var layout = (Grid)row.Child;
                if (!SameSettingsDeviceLabel((AudioDeviceInfo)row.Tag, device))
                {
                    layout.Children.RemoveAt(0);
                    layout.Children.Insert(0, BuildSettingsDeviceLabel(device));
                }
                else UpdateSettingsDeviceTitle(layout.Children[0], device);
                row.Tag = device;
                var box = (TextBox)layout.Children[1];
                string value = _config.DeviceNames.TryGetValue(device.Id, out var name) ? name ?? "" : "";
                if (!box.IsKeyboardFocusWithin && !string.Equals(box.Text.Trim(), value.Trim(), StringComparison.Ordinal)) box.Text = value;
                AutomationProperties.SetName(box, device.DisplayName ?? device.DisplayLabel);
                PlaceSettingsRow(list, row, index++);
            }
        }

        // 内层仅作显示的开关也会冒泡 Checked/Unchecked，不能视为用户改变外层设置。
        private bool IsSettingsChange(object sender, RoutedEventArgs e) =>
            IsLoaded && !_suppressSettings && ReferenceEquals(sender, e.OriginalSource);
    }
}
