using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SonicRoute;

internal static partial class Program
{
    private static void CheckThemeCompatibility(MethodInfo theme, List<Window> windows, string directory)
    {
        Directory.CreateDirectory(directory);
        var window = new MainWindow(); windows.Add(window);
        var off = new CheckBox { Style = (Style)window.FindResource("AutomationSwitch"), Width = 34, IsChecked = false };
        var on = new CheckBox { Style = off.Style, Width = 34, IsChecked = true };
        var primary = new Button { Style = (Style)window.FindResource("PrimaryButton"), Content = "保存 / Save", Width = 120 };
        var selected = new ComboBoxItem { Style = (Style)window.FindResource("RoundedComboItem"), IsSelected = true, Content = "已选设备 / Selected device" };
        var check = new CheckBox { Content = "勾选 / Checked", IsChecked = true };
        var device = new CheckBox { Style = (Style)window.FindResource("SettingsDeviceCheck"), Content = "播放设备 / Playback", IsChecked = true };
        var choice = new RadioButton { Style = (Style)window.FindResource("ThemeChoice"), Tag = "light", Content = "浅色 / Light", IsChecked = true };
        var content = new StackPanel();
        content.Resources["AutomationSwitch"] = off.Style;
        foreach (var element in new FrameworkElement[] { off, on, primary, selected, check, device, choice })
        {
            element.Margin = new Thickness(8, 4, 8, 4);
            element.HorizontalAlignment = HorizontalAlignment.Left;
            content.Children.Add(element);
        }
        var root = new Border { Padding = new Thickness(12), Child = content };
        root.SetResourceReference(Border.BackgroundProperty, "Theme.WindowBg");
        // Keep probes in an Application-owned window so global resource invalidation reaches them.
        // The window remains unshown and never runs product startup or input handlers.
        window.Content = root;
        Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
        foreach (string mode in new[] { "light", "dark" })
        foreach (string accent in new[] { "#00FFFF", "#3B82F6", "#FFFFFF", "#000000" })
        {
            theme.Invoke(null, new object[] { mode, accent });
            Layout(root, 470, 390);
            string state = mode + " " + accent;
            Check(ColorOf((Brush)Application.Current.Resources["Theme.OnAccent"]) == Colors.White,
                state + " common accent foreground retains the v1.29 white contract");
            Check(ColorOf(((Ellipse)off.Template.FindName("Thumb", off)).Fill) == Colors.White
                && ColorOf(((Ellipse)on.Template.FindName("Thumb", on)).Fill) == Colors.White,
                state + " both off and on switches have white thumbs");
            Check(ColorOf(primary.Foreground) == Colors.White, state + " primary-button text remains white");
            Check(ColorOf(selected.Foreground) == Colors.White, state + " selected device text remains white");
            var mark = (System.Windows.Shapes.Path)check.Template.FindName("check", check);
            Check(mark.Visibility == Visibility.Visible && ColorOf(mark.Stroke) == Colors.White,
                state + " ordinary checked mark remains white");
            var deviceMark = (System.Windows.Shapes.Path)device.Template.FindName("Check", device);
            Check(deviceMark.Visibility == Visibility.Visible && ColorOf(deviceMark.Stroke) == Colors.White,
                state + " settings device checked mark remains white");
            var badge = (Border)choice.Template.FindName("Selected", choice);
            Check(badge.Visibility == Visibility.Visible && ColorOf(((System.Windows.Shapes.Path)badge.Child).Stroke) == Colors.White,
                state + " theme selection checked mark remains white");
            Check(ColorOf(root.Background) == (Color)ColorConverter.ConvertFromString(mode == "light" ? "#F7F8FA" : "#1E1E1E")
                && ColorOf(check.Foreground) == (Color)ColorConverter.ConvertFromString(mode == "light" ? "#1F2937" : "#F3F4F6"),
                state + " background and ordinary text stay in the chosen theme");
            if (accent == "#00FFFF") RenderAppCards(root, System.IO.Path.Combine(directory, mode + "-shared-controls.png"));
        }
        window.Close();
    }
}
