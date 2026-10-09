using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace SonicRoute
{
    /// <summary>仅在用户操作时更新；不收藏自定义色、不启动轮询、不提前写入主题。</summary>
    internal sealed class ThemeColorPickerWindow : Window
    {
        internal static readonly IReadOnlyList<Color> BasicColors = CreateBasicColors();
        private readonly Dictionary<Color, Button> _swatches = new();
        private readonly Grid _spectrum;
        private readonly Border _preview;
        private readonly Slider _lightnessSlider;
        private readonly TextBox _hex, _red, _green, _blue;
        private readonly TextBox _hueInput, _saturationInput, _lightnessInput;
        private readonly TranslateTransform _marker = new();
        private Button? _selectedSwatch;
        private bool _syncing;
        private double _hue, _saturation, _lightness;
        private Color _lightnessColor;
        internal Color SelectedColor { get; private set; }

        internal ThemeColorPickerWindow(Window owner, Color initial)
        {
            Owner = owner;
            Title = L10n.T("Th.ChooseColor");
            Width = 720;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = owner.FontFamily;
            SetResourceReference(BackgroundProperty, "Theme.SurfaceBg");
            SetResourceReference(ForegroundProperty, "Theme.TextPrimary");

            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(336) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            columns.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(columns);

            var basic = new StackPanel();
            basic.Children.Add(Label(L10n.T("Color.Basic"), new Thickness(2, 0, 0, 12)));
            var palette = new UniformGrid { Columns = 12 };
            foreach (var color in BasicColors)
            {
                var swatch = new Button
                {
                    Background = Brush(color), ToolTip = ColorValues.ToHex(color),
                    Style = (Style)FindResource("ColorSwatchButton"), FontSize = 12,
                    Foreground = Brush(color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 150
                        ? Colors.Black : Colors.White)
                };
                AutomationProperties.SetName(swatch, ColorValues.ToHex(color));
                swatch.Click += (_, _) => SelectColor(color);
                _swatches.Add(color, swatch);
                palette.Children.Add(swatch);
            }
            basic.Children.Add(palette);
            columns.Children.Add(basic);

            var editor = new StackPanel();
            Grid.SetColumn(editor, 2);
            columns.Children.Add(editor);
            editor.Children.Add(Label(L10n.T("Color.Spectrum"), new Thickness(0, 0, 0, 12)));
            var spectrumRow = new Grid { Height = 220 };
            spectrumRow.ColumnDefinitions.Add(new ColumnDefinition());
            spectrumRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(ColorValues.FromHsl(i * 60, 1, 0.5), i / 6.0));
            rainbow.Freeze();
            // 与系统选色器相同：横轴色相、纵轴饱和度，右侧独立调整明暗。
            _spectrum = new Grid { Background = rainbow, ClipToBounds = true, Cursor = System.Windows.Input.Cursors.Cross, Focusable = true };
            AutomationProperties.SetName(_spectrum, L10n.T("Color.Spectrum"));
            _spectrum.Children.Add(new Border { Background = Gradient(Colors.Transparent, Color.FromRgb(128, 128, 128), true) });
            var markerLayer = new Canvas { IsHitTestVisible = false };
            var markerRing = new Grid { Width = 14, Height = 14, RenderTransform = _marker };
            markerRing.Children.Add(new Ellipse { Stroke = System.Windows.Media.Brushes.Black, StrokeThickness = 3 });
            markerRing.Children.Add(new Ellipse { Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 1.5, Margin = new Thickness(1) });
            markerLayer.Children.Add(markerRing);
            _spectrum.Children.Add(markerLayer);
            _spectrum.MouseLeftButtonDown += (_, e) => { _spectrum.Focus(); _spectrum.CaptureMouse(); SelectSpectrum(e.GetPosition(_spectrum)); e.Handled = true; };
            _spectrum.MouseMove += (_, e) => { if (_spectrum.IsMouseCaptured) SelectSpectrum(e.GetPosition(_spectrum)); };
            _spectrum.MouseLeftButtonUp += (_, e) => { if (_spectrum.IsMouseCaptured) { SelectSpectrum(e.GetPosition(_spectrum)); _spectrum.ReleaseMouseCapture(); e.Handled = true; } };
            _spectrum.SizeChanged += (_, _) => MoveMarker();
            Closed += (_, _) => { if (_spectrum.IsMouseCaptured) _spectrum.ReleaseMouseCapture(); };
            _spectrum.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Left) _hue = Math.Max(0, _hue - 1);
                else if (e.Key == Key.Right) _hue = Math.Min(360, _hue + 1);
                else if (e.Key == Key.Up) _saturation = Math.Min(1, _saturation + 0.01);
                else if (e.Key == Key.Down) _saturation = Math.Max(0, _saturation - 0.01);
                else return;
                SelectColor(ColorValues.FromHsl(_hue, _saturation, _lightness), updateHsl: false);
                e.Handled = true;
            };
            spectrumRow.Children.Add(_spectrum);
            _lightnessSlider = new Slider { Minimum = 0, Maximum = 1, SmallChange = 0.01, LargeChange = 0.1,
                Orientation = Orientation.Vertical, IsMoveToPointEnabled = true, Margin = new Thickness(12, 0, 0, 0),
                Style = (Style)FindResource("ColorLightnessSlider") };
            AutomationProperties.SetName(_lightnessSlider, L10n.T("Color.Lightness"));
            _lightnessSlider.ValueChanged += (_, _) =>
            {
                if (_syncing) return;
                _lightness = _lightnessSlider.Value;
                SelectColor(ColorValues.FromHsl(_hue, _saturation, _lightness), updateHsl: false);
            };
            Grid.SetColumn(_lightnessSlider, 1);
            spectrumRow.Children.Add(_lightnessSlider);
            editor.Children.Add(spectrumRow);
            var hsl = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, 0, 0) };
            _hueInput = CreateChannel(hsl, L10n.T("Color.Hue"));
            _saturationInput = CreateChannel(hsl, L10n.T("Color.Saturation"));
            _lightnessInput = CreateChannel(hsl, L10n.T("Color.Lightness"));
            _hueInput.ToolTip = "0–360°";
            _saturationInput.ToolTip = _lightnessInput.ToolTip = "0–100%";
            editor.Children.Add(hsl);

            var values = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            values.ColumnDefinitions.Add(new ColumnDefinition());
            _preview = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) };
            values.Children.Add(_preview);
            _hex = CreateInput("Hex");
            _hex.FontFamily = new FontFamily("Consolas");
            Grid.SetColumn(_hex, 1);
            values.Children.Add(_hex);
            basic.Children.Add(values);

            var rgb = new UniformGrid { Columns = 3, Margin = new Thickness(0, 12, 0, 0) };
            _red = CreateChannel(rgb, L10n.T("Color.Red"));
            _green = CreateChannel(rgb, L10n.T("Color.Green"));
            _blue = CreateChannel(rgb, L10n.T("Color.Blue"));
            basic.Children.Add(rgb);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var ok = new Button { Content = L10n.T("Color.Confirm"), Width = 100, IsDefault = true, Style = (Style)owner.FindResource("GhostButton") };
            var cancel = new Button { Content = L10n.T("Auto.Cancel"), Width = 100, IsCancel = true, Margin = new Thickness(10, 0, 0, 0), Style = (Style)owner.FindResource("GhostButton") };
            ok.Click += (_, _) => { CommitFocusedInput(); DialogResult = true; };
            actions.Children.Add(ok);
            actions.Children.Add(cancel);
            Grid.SetRow(actions, 1);
            root.Children.Add(actions);
            Content = root;
            SelectColor(initial);
        }

        private static TextBlock Label(string text, Thickness margin) => new TextBlock
        { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = margin };

        private TextBox CreateInput(string name)
        {
            var box = new TextBox { Height = 34 };
            AutomationProperties.SetName(box, name);
            box.LostKeyboardFocus += (_, _) => CommitInput(box);
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitInput(box); e.Handled = true; }
            };
            return box;
        }

        private TextBox CreateChannel(Panel parent, string name)
        {
            var group = new StackPanel { Margin = new Thickness(2, 0, 6, 0) };
            group.Children.Add(Label(name, new Thickness(0, 0, 0, 4)));
            var box = CreateInput(name);
            group.Children.Add(box);
            parent.Children.Add(group);
            return box;
        }

        private void CommitFocusedInput()
        {
            if (Keyboard.FocusedElement is TextBox box && IsAncestorOf(box)) CommitInput(box);
        }

        private void CommitInput(TextBox box)
        {
            if (_syncing || Equals(box.Tag, box.Text)) return;
            if (box == _hex)
            {
                if (ColorValues.TryParseHex(box.Text, out var color)) { SelectColor(color); return; }
            }
            else if (box == _hueInput || box == _saturationInput || box == _lightnessInput)
            {
                if (double.TryParse(box.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double component)
                    && component >= 0 && component <= (box == _hueInput ? 360 : 100))
                {
                    if (box == _hueInput) _hue = component;
                    else if (box == _saturationInput) _saturation = component / 100;
                    else _lightness = component / 100;
                    SelectColor(ColorValues.FromHsl(_hue, _saturation, _lightness), updateHsl: false);
                    return;
                }
            }
            else if (byte.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte channel))
            {
                SelectColor(Color.FromRgb(box == _red ? channel : SelectedColor.R,
                    box == _green ? channel : SelectedColor.G, box == _blue ? channel : SelectedColor.B));
                return;
            }
            // 无效值恢复选择值；关闭/取消不会把半成品输入写回主题。
            SyncInputs();
        }

        private void SelectSpectrum(Point position)
        {
            if (_spectrum.ActualWidth <= 0 || _spectrum.ActualHeight <= 0) return;
            _hue = Math.Max(0, Math.Min(1, position.X / _spectrum.ActualWidth)) * 360;
            _saturation = 1 - Math.Max(0, Math.Min(1, position.Y / _spectrum.ActualHeight));
            SelectColor(ColorValues.FromHsl(_hue, _saturation, _lightness), updateHsl: false);
        }

        internal void SelectColor(Color color, bool updateHsl = true)
        {
            SelectedColor = Color.FromRgb(color.R, color.G, color.B);
            if (updateHsl) ColorValues.ToHsl(SelectedColor, ref _hue, out _saturation, out _lightness);
            SyncInputs();
            var mid = ColorValues.FromHsl(_hue, _saturation, 0.5);
            if (_lightnessSlider.Background == null || mid != _lightnessColor)
            {
                _lightnessColor = mid;
                var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                gradient.GradientStops.Add(new GradientStop(Colors.White, 0));
                gradient.GradientStops.Add(new GradientStop(mid, 0.5));
                gradient.GradientStops.Add(new GradientStop(Colors.Black, 1));
                gradient.Freeze();
                _lightnessSlider.Background = gradient;
            }
            MoveMarker();
            _swatches.TryGetValue(SelectedColor, out var swatch);
            if (ReferenceEquals(swatch, _selectedSwatch)) return;
            if (_selectedSwatch != null) { _selectedSwatch.Content = null; _selectedSwatch.BorderThickness = new Thickness(1); }
            _selectedSwatch = swatch;
            if (swatch != null) { swatch.Content = "✓"; swatch.BorderThickness = new Thickness(2); }
        }

        private void SyncInputs()
        {
            _syncing = true;
            try
            {
                _hex.Text = ColorValues.ToHex(SelectedColor);
                _red.Text = SelectedColor.R.ToString(CultureInfo.InvariantCulture);
                _green.Text = SelectedColor.G.ToString(CultureInfo.InvariantCulture);
                _blue.Text = SelectedColor.B.ToString(CultureInfo.InvariantCulture);
                _hueInput.Text = _hue.ToString("0.##", CultureInfo.InvariantCulture);
                _saturationInput.Text = (_saturation * 100).ToString("0.##", CultureInfo.InvariantCulture);
                _lightnessInput.Text = (_lightness * 100).ToString("0.##", CultureInfo.InvariantCulture);
                foreach (var box in new[] { _hex, _red, _green, _blue, _hueInput, _saturationInput, _lightnessInput }) box.Tag = box.Text;
                _lightnessSlider.Value = _lightness;
                if (_preview.Background is not SolidColorBrush old || old.Color != SelectedColor)
                    _preview.Background = Brush(SelectedColor);
            }
            finally { _syncing = false; }
        }

        private void MoveMarker()
        {
            _marker.X = _hue / 360 * _spectrum.ActualWidth - 7;
            _marker.Y = (1 - _saturation) * _spectrum.ActualHeight - 7;
        }

        private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }

        private static LinearGradientBrush Gradient(Color first, Color last, bool vertical)
        {
            var brush = new LinearGradientBrush(first, last, vertical ? 90 : 0);
            brush.Freeze();
            return brush;
        }

        private static IReadOnlyList<Color> CreateBasicColors()
        {
            var colors = new List<Color>(96);
            for (int i = 0; i < 12; i++) { byte gray = (byte)Math.Round(i * 255 / 11.0); colors.Add(Color.FromRgb(gray, gray, gray)); }
            var saturation = new[] { 0.2, 0.4, 0.65, 1.0, 1.0, 1.0, 1.0 };
            var value = new[] { 1.0, 1.0, 1.0, 1.0, 0.8, 0.6, 0.35 };
            for (int row = 0; row < saturation.Length; row++)
                for (int column = 0; column < 12; column++) colors.Add(ColorValues.FromHsv(column * 30, saturation[row], value[row]));
            return colors.AsReadOnly();
        }
    }

    internal static class ColorValues
    {
        internal static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        internal static bool TryParseHex(string text, out Color color)
        {
            string value = (text ?? "").Trim();
            if (value.StartsWith("#", StringComparison.Ordinal)) value = value.Substring(1);
            if (value.Length == 6 && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            { color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true; }
            color = default;
            return false;
        }

        internal static Color FromHsv(double hue, double saturation, double value)
        {
            double h = ((hue % 360) + 360) % 360 / 60;
            double chroma = value * saturation, x = chroma * (1 - Math.Abs(h % 2 - 1)), m = value - chroma;
            double r, g, b;
            if (h < 1) { r = chroma; g = x; b = 0; }
            else if (h < 2) { r = x; g = chroma; b = 0; }
            else if (h < 3) { r = 0; g = chroma; b = x; }
            else if (h < 4) { r = 0; g = x; b = chroma; }
            else if (h < 5) { r = x; g = 0; b = chroma; }
            else { r = chroma; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        internal static Color FromHsl(double hue, double saturation, double lightness)
        {
            double value = lightness + saturation * Math.Min(lightness, 1 - lightness);
            return FromHsv(hue, value == 0 ? 0 : 2 * (1 - lightness / value), value);
        }

        internal static void ToHsl(Color color, ref double hue, out double saturation, out double lightness)
        {
            ToHsv(color, ref hue, out double hsvSaturation, out double maximum);
            double minimum = maximum * (1 - hsvSaturation);
            lightness = (maximum + minimum) / 2;
            saturation = lightness <= 0 || lightness >= 1 ? 0 : (maximum - minimum) / (1 - Math.Abs(2 * lightness - 1));
        }

        internal static void ToHsv(Color color, ref double hue, out double saturation, out double value)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b)), delta = maximum - minimum;
            value = maximum;
            saturation = maximum == 0 ? 0 : delta / maximum;
            if (delta == 0) return; // 灰色保留用户选择的色相。
            hue = 60 * (maximum == r ? (g - b) / delta : maximum == g ? (b - r) / delta + 2 : (r - g) / delta + 4);
            if (hue < 0) hue += 360;
        }
    }
}
