using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute
{
    public partial class MainWindow
    {
        private string _themeHexDisplayText = "";
        private Color _themeColor;

        // 保留旧配置中预设名的颜色，界面只提供主题色编辑。
        private static Color ResolveThemeColor(string accent) => TryParseThemeHex(accent, out var color)
            ? color : accent switch
            {
                "green" => Color.FromRgb(0x22, 0xC5, 0x5E),
                "purple" => Color.FromRgb(0xEC, 0x48, 0x99),
                _ => Color.FromRgb(0x2F, 0x80, 0xED)
            };

        private void SyncThemeColorUi(string accent) => UpdateThemeColorPreview(ResolveThemeColor(accent));

        private static string ThemeColorToHex(Color color) => ColorValues.ToHex(color);

        private void UpdateThemeColorPreview(Color color)
        {
            _themeColor = color;
            _themeHexDisplayText = ThemeColorToHex(color);
            ThemeColorHex.Text = _themeHexDisplayText;
            if (ThemeColorPreview.Background is SolidColorBrush current && current.Color == color) return;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            ThemeColorPreview.Background = brush;
        }

        private static bool TryParseThemeHex(string text, out Color color) => ColorValues.TryParseHex(text, out color);

        private void ApplyCustomThemeColor(Color color)
        {
            UpdateThemeColorPreview(color);
            string hex = ThemeColorToHex(color);
            if (_suppressSettings || string.Equals(_config.Accent, hex, StringComparison.OrdinalIgnoreCase)) return;
            _config.Accent = hex;
            ScheduleThemeSave();
            ApplyThemePreview();
        }

        private void CommitThemeHex()
        {
            if (_suppressSettings || !IsLoaded) return;
            // 仅聚焦后离开不写回配置；只有用户实际编辑了文本才提交。
            if (string.Equals(ThemeColorHex.Text.Trim(), _themeHexDisplayText, StringComparison.OrdinalIgnoreCase)) return;
            if (TryParseThemeHex(ThemeColorHex.Text, out var color))
            {
                if (color == _themeColor)
                    UpdateThemeColorPreview(color);
                else ApplyCustomThemeColor(color);
            }
            else
            {
                SyncThemeColorUi(_config.Accent ?? "blue");
                ShowToast(L10n.T("Th.InvalidHex"));
            }
        }

        private void ThemeHex_LostFocus(object sender, RoutedEventArgs e) => CommitThemeHex();

        private void ThemeHex_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitThemeHex(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SyncThemeColorUi(_config.Accent ?? "blue"); e.Handled = true; }
        }

        private void ThemeChooseColor_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ThemeColorPickerWindow(this, _themeColor);
            if (dialog.ShowDialog() == true) ApplyCustomThemeColor(dialog.SelectedColor);
        }
    }
}
