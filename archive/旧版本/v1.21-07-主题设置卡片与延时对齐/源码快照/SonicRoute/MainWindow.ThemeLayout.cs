using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private string _themeHexDisplayText = "";

        private void UpdateThemeColorPreview(Color color)
        {
            _themeHexDisplayText = RgbToHex(color.R, color.G, color.B);
            RgbHex.Text = _themeHexDisplayText;
            if (RgbPreview.Background is SolidColorBrush current && current.Color == color) return;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            RgbPreview.Background = brush;
        }

        private void ThemeAdvancedToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (ThemeAdvancedPanel == null) return;
            AnimatePanelExpand(ThemeAdvancedPanel, ThemeAdvancedToggle.IsChecked == true);
        }

        private static bool TryParseThemeHex(string text, out Color color)
        {
            string hex = (text ?? "").Trim();
            if (hex.Length == 6) hex = "#" + hex;
            if (hex.Length == 7 && hex[0] == '#'
                && byte.TryParse(hex.Substring(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte r)
                && byte.TryParse(hex.Substring(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte g)
                && byte.TryParse(hex.Substring(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b))
            {
                color = Color.FromRgb(r, g, b);
                return true;
            }
            color = default;
            return false;
        }

        private void ApplyCustomThemeColor(Color color)
        {
            bool previous = _suppressSettings;
            _suppressSettings = true;
            try
            {
                SyncRgbUi(RgbToHex(color.R, color.G, color.B));
                AccentCustom.IsChecked = true;
            }
            finally { _suppressSettings = previous; }
            if (!previous) Theme_Changed(AccentCustom, new RoutedEventArgs());
        }

        private void CommitThemeHex()
        {
            if (_suppressSettings || !IsLoaded) return;
            // 仅聚焦后离开不改变预设色；只有用户实际编辑了文本才提交。
            if (string.Equals(RgbHex.Text.Trim(), _themeHexDisplayText, StringComparison.OrdinalIgnoreCase)) return;
            if (TryParseThemeHex(RgbHex.Text, out var color))
            {
                string hex = RgbToHex(color.R, color.G, color.B);
                if (AccentCustom.IsChecked == true && string.Equals(_config.Accent, hex, StringComparison.OrdinalIgnoreCase))
                    RgbHex.Text = hex;
                else ApplyCustomThemeColor(color);
            }
            else
            {
                SyncRgbUi(_config.Accent ?? "blue");
                ShowToast(L10n.T("Th.InvalidHex"));
            }
        }

        private void ThemeHex_LostFocus(object sender, RoutedEventArgs e) => CommitThemeHex();

        private void ThemeHex_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitThemeHex(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SyncRgbUi(_config.Accent ?? "blue"); e.Handled = true; }
        }

        private void ThemeChooseColor_Click(object sender, RoutedEventArgs e)
        {
            var menu = ConvenienceMenus.Create();
            menu.PlacementTarget = ThemeChooseColor;
            var picker = ConvenienceMenus.Item(L10n.T("Th.ChooseColor"));
            picker.Click += (_, _) =>
            {
                using var dialog = new System.Windows.Forms.ColorDialog
                {
                    FullOpen = true,
                    Color = System.Drawing.Color.FromArgb((int)RSlider.Value, (int)GSlider.Value, (int)BSlider.Value)
                };
                // 指定当前窗口为系统对话框的所有者，关闭后焦点返回主题页。
                var owner = new ColorDialogOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    ApplyCustomThemeColor(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
            };
            var advanced = ConvenienceMenus.Item(L10n.T("Th.AdvancedColors"));
            advanced.IsCheckable = true;
            advanced.IsChecked = ThemeAdvancedToggle.IsChecked == true;
            advanced.Click += (_, _) => ThemeAdvancedToggle.IsChecked = advanced.IsChecked;
            menu.Items.Add(picker);
            menu.Items.Add(advanced);
            ThemeChooseColor.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private sealed class ColorDialogOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; }
            public ColorDialogOwner(IntPtr handle) => Handle = handle;
        }
    }
}
