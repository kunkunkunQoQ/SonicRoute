using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using Path = System.IO.Path;

internal static partial class Program
{
    private static object? AppCardCall(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, Instance)!.Invoke(target, args);

    private static IEnumerable<DependencyObject> AppCardTree(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in AppCardTree(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    private static FrameworkElement LayoutAppCards(MainWindow window, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        return root;
    }

    private static void RenderAppCards(FrameworkElement root, string path, double scale = 1)
    {
        // Raster DPI variants are not physical monitor/DWM tests.
        var bitmap = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void CheckAppCards(MethodInfo theme, List<Window> windows, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var language = typeof(App).Assembly.GetType("SonicRoute.L10n")!;
        var localizer = language.GetProperty("Instance")!.GetValue(null)!;
        void SetLanguage(string name) => language.GetMethod("SetLanguage")!.Invoke(localizer, new object[] { name });
        void Click(CheckBox control) => typeof(ToggleButton).GetMethod("OnClick", Instance)!.Invoke(control, null);
        foreach (string mode in new[] { "light", "dark" })
        {
            SetLanguage("zh-CN"); theme.Invoke(null, new object[] { mode, "blue" });
            var config = ConfigService.Load(); config.ExperimentalMic = true;
            config.HiddenPanelApps.Clear(); config.DisabledAutoSwitchApps.Clear();
            var window = new MainWindow(); windows.Add(window);
            ((RadioButton)window.FindName("NavApps")).IsChecked = true;
            ((Grid)window.FindName("OverviewPage")).Visibility = Visibility.Collapsed;
            ((Grid)window.FindName("AppsPage")).Visibility = Visibility.Visible;
            // Keep fixture device snapshots local; do not invoke device discovery for rendering.
            window.GetType().GetField("_inputDevicesLoaded", Instance)!.SetValue(window, true);
            var outputDevices = new List<AudioDeviceInfo> {
                new() { Id = AudioService.SystemDefaultDeviceId, DisplayName = "系统默认输出", Flow = EDataFlow.eRender },
                new() { Id = "fixture-output", DisplayName = "A very long playback device name for ellipsis verification", Flow = EDataFlow.eRender }
            };
            var inputDevices = new List<AudioDeviceInfo> {
                new() { Id = AudioService.SystemDefaultInputDeviceId, DisplayName = "系统默认输入", Flow = EDataFlow.eCapture }
            };
            window.GetType().GetField("_outputDisplay", Instance)!.SetValue(window, outputDevices);
            window.GetType().GetField("_inputDisplay", Instance)!.SetValue(window, inputDevices);
            window.GetType().GetField("_suppressDevCombo", Instance)!.SetValue(window, true);
            ((ComboBox)window.FindName("AppsOutputCombo")).ItemsSource = outputDevices;
            window.GetType().GetField("_suppressDevCombo", Instance)!.SetValue(window, false);
            AppCardCall(window, "ApplyExpMicUi", true);
            var items = new List<AppItem> {
                AppItem.From(new AudioAppInfo { ProcessId = int.MaxValue - 21, ProcessName = "Steam", DisplayName = "Steam", HasActiveSession = true }),
                AppItem.From(new AudioAppInfo { ProcessId = int.MaxValue - 22, ProcessName = "LongFixtureApplication", DisplayName = "非常长的应用名称用来验证省略和布局", HasActiveSession = false })
            };
            foreach (var item in items) item.RefreshAutoSwitchState();
            window.GetType().GetField("_appItems", Instance)!.SetValue(window, items);
            var list = (ListBox)window.FindName("AppsListBox"); list.ItemsSource = items; list.SelectedItem = items[0];
            Finish((Task)AppCardCall(window, "RefreshAppsSelectionAsync")!);
            AppCardCall(window, "SetAppsVolumeUi", 42);
            var mute = (CheckBox)window.FindName("AppsMuteButton");
            var auto = (CheckBox)window.FindName("AppsDisableAutoButton");
            var shown = (CheckBox)window.FindName("AppsShowInPanelButton");
            int clicks = 0; mute.Click += (_, _) => clicks++; auto.Click += (_, _) => clicks++; shown.Click += (_, _) => clicks++;
            AppCardCall(window, "ApplyAppsMuteVisual", true);
            AppCardCall(window, "ApplyAppsMuteVisual", false);
            AppCardCall(window, "UpdateAppsDisableAutoButton"); AppCardCall(window, "UpdateAppsShowInPanelButton");
            Check(clicks == 0 && mute.IsChecked == false && auto.IsChecked == false && shown.IsChecked == true, mode + " readback changes switches without invoking Click; panel visibility has positive meaning");
            var root = LayoutAppCards(window, 1080, 760);
            var marker = AppCardTree(list).OfType<Ellipse>().First(e => e.Name == "StateDot");
            Check(marker.Visibility == Visibility.Visible && ((SolidColorBrush)marker.Fill).Color == ((SolidColorBrush)window.FindResource("Theme.Success")).Color, mode + " active fixture uses existing success brush");
            Click(shown); root.UpdateLayout();
            Check(items[0].IsHiddenInPanel && shown.IsChecked == false && config.HiddenPanelApps.Contains("Steam")
                && ((SolidColorBrush)marker.Fill).Color == ((SolidColorBrush)window.FindResource("Theme.AccentInverse")).Color,
                mode + " hiding quick-panel app preserves inverse-color marker and saved preference");
            RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-hidden.png"));
            Click(shown); Click(auto); root.UpdateLayout();
            Check(!items[0].IsHiddenInPanel && auto.IsChecked == true && items[0].IsAutoSwitchDisabled
                && ((SolidColorBrush)marker.Fill).Color == ((SolidColorBrush)window.FindResource("Theme.AccentInverse")).Color,
                mode + " auto-follow exclusion retains its inverse-color marker");
            Click(auto);
            var renameToggle = (ToggleButton)window.FindName("AppsRenameToggle");
            renameToggle.IsChecked = true;
            root.UpdateLayout();
            var rename = (TextBox)window.FindName("AppsRenameBox");
            rename.Text = "自定义应用名称";
            Check(config.AppNames["Steam"] == "自定义应用名称"
                && ((TextBlock)window.FindName("AppsDetailTitle")).Text == "自定义应用名称"
                && items[0].Label == "自定义应用名称" && rename.ActualHeight > 0,
                mode + " original rename handler updates title and existing list item without rebuilding textbox");
            rename.Text = ""; renameToggle.IsChecked = false;
            AppCardCall(window, "FlushNameChanges", false);
            Check(!config.AppNames.ContainsKey("Steam") && items[0].Label == "Steam", mode + " blank display name restores the original name through existing save path");
            AppCardCall(window, "ApplyExpMicUi", false); root.UpdateLayout();
            Check(((Border)window.FindName("AppsInputCard")).Visibility == Visibility.Collapsed
                && Grid.GetColumnSpan((Border)window.FindName("AppsOutputCard")) == 2, mode + " microphone off collapses whole card and output fills the row");
            AppCardCall(window, "ApplyExpMicUi", true); root.UpdateLayout();
            Check(((Border)window.FindName("AppsInputCard")).Visibility == Visibility.Visible
                && Grid.GetColumnSpan((Border)window.FindName("AppsOutputCard")) == 1, mode + " microphone on restores both existing selectors");
            foreach (string locale in new[] { "zh-CN", "en-US", "zh-TW", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU" })
            {
                SetLanguage(locale);
                foreach (int width in new[] { 780, 920, 1280 })
                {
                    root = LayoutAppCards(window, width, width == 1280 ? 860 : 620);
                    Check(new[] { "AppsMuteButton", "AppsDisableAutoButton", "AppsShowInPanelButton" }.All(name => {
                        var control = (CheckBox)window.FindName(name);
                        var border = AppCardTree((Grid)window.FindName("AppsControls")).OfType<Border>().First(b => b.Child is Grid grid && AppCardTree(grid).Contains(control));
                        var bounds = control.TransformToAncestor(border).TransformBounds(new Rect(control.RenderSize));
                        return control.ActualWidth == 34 && bounds.Left >= 0 && bounds.Right <= border.ActualWidth + 0.5
                            && Math.Abs(bounds.Top + bounds.Height / 2 - border.ActualHeight / 2) <= 0.5
                            && Math.Abs(bounds.Right - (border.ActualWidth - 13)) <= 0.5;
                    }), mode + " " + locale + " width=" + width + " all switches align at the right and vertical center without clipping");
                }
            }
            SetLanguage("zh-CN");
            root = LayoutAppCards(window, 1080, 760); RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + ".png"));
            RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-144dpi.png"), 1.5);
            root = LayoutAppCards(window, 780, 620); RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-narrow.png"));
            SetLanguage("de-DE"); root = LayoutAppCards(window, 920, 620); RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-de.png"));
            list.SelectedItem = null;
            Check(!((Grid)window.FindName("AppsControls")).IsEnabled && !((Grid)window.FindName("AppsRoutingPanel")).IsEnabled
                && mute.IsChecked == false && shown.IsChecked == false && ((TextBlock)window.FindName("AppsVolumeText")).Text == "—"
                && ((ComboBox)window.FindName("AppsOutputCombo")).SelectedItem == null && ((ComboBox)window.FindName("AppsInputCombo")).SelectedItem == null,
                mode + " empty selection disables controls and clears old switch/volume state");
            window.Close();
            Check(Field<bool>(window, "_isClosed") && list.ItemsSource == null && list.Items.Count == 0,
                mode + " window uses existing close cleanup and releases items");
        }
    }
}
