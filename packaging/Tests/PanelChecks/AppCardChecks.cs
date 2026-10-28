using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

    private sealed class AppCardVolume : ISimpleAudioVolume
    {
        internal bool Muted, AllowMuteWrites;
        internal int MuteReads, MuteWrites;
        public int GetMasterVolume(out float value) { value = .42f; return 0; }
        public int GetMute(out int value) { System.Threading.Interlocked.Increment(ref MuteReads); value = Muted ? 1 : 0; return 0; }
        public int SetMasterVolume(float value, ref Guid context) => throw new InvalidOperationException("Read-only UI fixture");
        public int SetMute(int value, ref Guid context)
        {
            if (!AllowMuteWrites) throw new InvalidOperationException("Unexpected fixture mute write");
            System.Threading.Interlocked.Increment(ref MuteWrites); Muted = value != 0; return 0;
        }
    }

    private static void CheckAppListMuteReadback(MainWindow window, List<AppItem> items, string mode)
    {
        // Seed only the isolated process's existing caches; execute the actual page-load and mute-read paths.
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var saved = new Dictionary<FieldInfo, object?>();
        void Set(Type owner, string name, object? value)
        {
            var field = owner.GetField(name, flags)!;
            if (!saved.ContainsKey(field)) saved.Add(field, field.GetValue(null));
            field.SetValue(null, value);
        }
        var clock = typeof(AudioService).Assembly.GetType("SonicRoute.Core.Compat.CompatEnv")!.GetProperty("TickCount64", flags)!;
        var first = new AppCardVolume { AllowMuteWrites = true }; var second = new AppCardVolume { Muted = true };
        var capture = new AppCardVolume();
        var apps = items.Select(item => item.Info).ToList();
        var source = ((ListBox)window.FindName("AppsListBox")).ItemsSource;
        try
        {
            int appGeneration = (int)typeof(AudioService).GetField("_appsGeneration", flags)!.GetValue(null)!;
            Set(typeof(AudioService), "_appsCache", apps);
            Set(typeof(AudioService), "_appsCacheGeneration", appGeneration);
            Set(typeof(AudioService), "_appsReadTask", Task.FromResult(apps));
            Set(typeof(AudioService), "_appsReadGeneration", appGeneration);
            Set(typeof(SessionVolumeService), "_renderVolumes", new Dictionary<int, List<ISimpleAudioVolume>> {
                [items[0].ProcessId] = new() { first }, [items[1].ProcessId] = new() { second }
            });
            Set(typeof(SessionVolumeService), "_captureVolumes", new Dictionary<int, List<ISimpleAudioVolume>> {
                [items[0].ProcessId] = new() { capture }
            });
            int sessionGeneration = (int)typeof(SessionVolumeService).GetField("_cacheGeneration", flags)!.GetValue(null)!;
            Set(typeof(SessionVolumeService), "_snapshotGeneration", sessionGeneration);
            void ReadPage()
            {
                Set(typeof(AudioService), "_appsCacheAt", clock.GetValue(null));
                Set(typeof(SessionVolumeService), "_lastEnum", DateTime.UtcNow);
                Finish((Task)AppCardCall(window, "LoadAppsAsync")!);
            }
            ReadPage();
            Check(!items[0].IsMuted && items[1].IsMuted && second.MuteReads == 1
                && ReferenceEquals(source, ((ListBox)window.FindName("AppsListBox")).ItemsSource),
                mode + " page load reads the unselected app mute once through existing session cache without rebuilding rows");
            second.Muted = false; ReadPage();
            Check(!items[1].IsMuted && second.MuteReads == 2,
                mode + " manual page refresh updates unselected mute state through the same read-only path");
            var mute = (Button)window.FindName("AppsMuteButton");
            bool Muted() => ReferenceEquals(mute.Foreground, window.FindResource("Theme.Accent"));
            void ClickMute() => typeof(Button).GetMethod("OnClick", Instance)!.Invoke(mute, null);
            async Task Toggle(bool expected)
            {
                int writes = first.MuteWrites;
                Set(typeof(SessionVolumeService), "_lastEnum", DateTime.UtcNow);
                ClickMute();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (first.MuteWrites != writes + 1 || Muted() != expected)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("App mute button did not complete");
                    await Task.Delay(1);
                }
            }
            AppCardCall(window, "SetAppsVolumeUi", 42);
            Finish(Toggle(true));
            Check(first.Muted && items[0].IsMuted && Muted() && second.MuteWrites == 0,
                mode + " relocated Button.OnClick mutes only the selected playback fixture and synchronizes its list marker");
            Finish(Toggle(false));
            Check(!first.Muted && !items[0].IsMuted && !Muted() && first.MuteWrites == 2,
                mode + " relocated button unmutes through the existing application playback handler");
            AppCardCall(window, "SetAppsVolumeUi", (object?)null); ClickMute();
            Check(!mute.IsEnabled && first.MuteWrites == 2 && capture.MuteReads + capture.MuteWrites == 0,
                mode + " unavailable playback disables the moved button; app mute never reads or writes capture");
            AppCardCall(window, "SetAppsVolumeUi", 42);
        }
        finally { foreach (var pair in saved) pair.Key.SetValue(null, pair.Value); }
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
            var mute = (Button)window.FindName("AppsMuteButton");
            bool Muted() => ReferenceEquals(mute.Foreground, window.FindResource("Theme.Accent"));
            var auto = (CheckBox)window.FindName("AppsDisableAutoButton");
            var shown = (CheckBox)window.FindName("AppsShowInPanelButton");
            int clicks = 0; mute.Click += (_, _) => clicks++; auto.Click += (_, _) => clicks++; shown.Click += (_, _) => clicks++;
            AppCardCall(window, "ApplyAppsMuteVisual", true);
            AppCardCall(window, "ApplyAppsMuteVisual", false);
            AppCardCall(window, "UpdateAppsDisableAutoButton"); AppCardCall(window, "UpdateAppsShowInPanelButton");
            Check(clicks == 0 && !Muted() && auto.IsChecked == false && shown.IsChecked == true, mode + " readback changes mute button and switches without invoking Click; panel visibility has positive meaning");
            Check(ReferenceEquals(mute.Style, window.FindResource("QuickPanelToolButton"))
                && window.FindName("AppsMinus") == null && window.FindName("AppsPlus") == null
                && ((Grid)window.FindName("AppsControls")).Children.Count == 2,
                mode + " application volume reuses the quick-panel button and removes plus/minus and duplicate mute switch");
            var root = LayoutAppCards(window, 1080, 760);
            var marker = AppCardTree(list).OfType<Ellipse>().First(e => e.Name == "StateDot");
            var hiddenMarker = AppCardTree(list).OfType<Ellipse>().First(e => e.Name == "PanelHiddenDot");
            var normal = ((SolidColorBrush)window.FindResource("Theme.Success")).Color;
            var inverse = ((SolidColorBrush)window.FindResource("Theme.AccentInverse")).Color;
            Check(marker.Visibility == Visibility.Visible && ((SolidColorBrush)marker.Fill).Color == ((SolidColorBrush)window.FindResource("Theme.Success")).Color, mode + " active fixture uses existing success brush");
            Click(shown); root.UpdateLayout();
            var icon = (Grid)hiddenMarker.Parent;
            var hiddenBounds = hiddenMarker.TransformToAncestor(icon).TransformBounds(new Rect(hiddenMarker.RenderSize));
            Check(items[0].IsHiddenInPanel && shown.IsChecked == false && config.HiddenPanelApps.Contains("Steam")
                && hiddenMarker.Visibility == Visibility.Visible && ((SolidColorBrush)hiddenMarker.Fill).Color == inverse
                && hiddenBounds.Left <= 0 && hiddenBounds.Top <= 0 && ((SolidColorBrush)marker.Fill).Color == normal,
                mode + " hiding from quick panel puts an independent inverse dot at the icon top left, leaving activity dot unchanged");
            RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-hidden.png"));
            Click(shown); Click(auto); root.UpdateLayout();
            Check(!items[0].IsHiddenInPanel && auto.IsChecked == true && items[0].IsAutoSwitchDisabled
                && hiddenMarker.Visibility == Visibility.Collapsed && ((SolidColorBrush)marker.Fill).Color == normal
                && ((SolidColorBrush)items[0].DotBrush).Color == inverse,
                mode + " auto-follow exclusion keeps its original flag but cannot be confused with list mute or hidden dots");
            Click(auto);
            AppCardCall(window, "ApplyAppsMuteVisual", true); root.UpdateLayout();
            Check(Muted() && items[0].IsMuted && marker.Visibility == Visibility.Visible && ((SolidColorBrush)marker.Fill).Color == inverse
                && hiddenMarker.Visibility == Visibility.Collapsed && (string)marker.ToolTip == L10n.T("Qp.Muted"),
                mode + " selected mute readback overrides active color with inverse dot without audio writes");
            Click(shown); root.UpdateLayout();
            Check(hiddenMarker.Visibility == Visibility.Visible && marker.Visibility == Visibility.Visible
                && ((SolidColorBrush)marker.Fill).Color == inverse, mode + " muted and hidden app displays both independent markers");
            RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-muted-hidden.png"));
            AppCardCall(window, "ApplyAppsMuteVisual", false); Click(shown); root.UpdateLayout();
            var inactiveMarker = AppCardTree(list).OfType<Ellipse>().First(e => e.Name == "StateDot" && ReferenceEquals(e.DataContext, items[1]));
            items[1].IsMuted = true; root.UpdateLayout();
            Check(inactiveMarker.Visibility == Visibility.Visible && ((SolidColorBrush)inactiveMarker.Fill).Color == inverse
                && ((SolidColorBrush)marker.Fill).Color == normal, mode + " inactive muted app has inverse dot without changing another app");
            items[1].IsMuted = false; root.UpdateLayout();
            Check(inactiveMarker.Visibility == Visibility.Collapsed && marker.Visibility == Visibility.Visible
                && ((SolidColorBrush)marker.Fill).Color == normal, mode + " unmuting restores normal active dot or hides inactive dot");
            CheckAppListMuteReadback(window, items, mode);
            var renameButton = (Button)window.FindName("AppsRenameButton");
            var title = (TextBlock)window.FindName("AppsDetailTitle");
            var rename = (TextBox)window.FindName("AppsRenameBox");
            var titlePosition = title.TranslatePoint(new Point(), root);
            renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); root.UpdateLayout();
            var editPosition = rename.TranslatePoint(new Point(), root);
            Check(window.FindName("AppsRenameToggle") == null && title.Visibility == Visibility.Collapsed
                && rename.Visibility == Visibility.Visible && rename.Text == "Steam" && rename.SelectionLength == rename.Text.Length
                && Math.Abs(titlePosition.X - editPosition.X) <= 1 && Math.Abs(titlePosition.Y - editPosition.Y) <= 2
                && !config.AppNames.ContainsKey("Steam"), mode + " pencil opens the existing textbox at the title without saving an unchanged default");
            rename.Text = "自定义应用名称";
            Check(config.AppNames["Steam"] == "自定义应用名称"
                && title.Text == "自定义应用名称"
                && items[0].Label == "自定义应用名称" && rename.ActualHeight > 0,
                mode + " original rename handler updates title and existing list item without rebuilding textbox");
            root.UpdateLayout();
            RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-editing.png"));
            root = LayoutAppCards(window, 780, 620); RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-editing-narrow.png"));
            rename.Text = "";
            root.UpdateLayout();
            Check(rename.ActualWidth >= 60 && rename.Visibility == Visibility.Visible,
                mode + " clearing the name keeps a usable inline textbox until editing finishes");
            rename.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, rename, renameButton) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            root.UpdateLayout();
            Check(!config.AppNames.ContainsKey("Steam") && items[0].Label == "Steam" && title.Visibility == Visibility.Visible
                && rename.Visibility == Visibility.Collapsed && !Field<bool>(window, "_nameSavePending"),
                mode + " blur closes inline editing and blank name restores the default through the existing save path");
            renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            list.SelectedItem = items[1]; root.UpdateLayout();
            Check(rename.Visibility == Visibility.Collapsed && title.Text == items[1].Label && !config.AppNames.ContainsKey(items[1].ProcessName),
                mode + " changing application closes the old editor without copying its name to the new target");
            list.SelectedItem = items[0]; Finish((Task)AppCardCall(window, "RefreshAppsSelectionAsync")!);
            AppCardCall(window, "SetAppsVolumeUi", 42);
            AppCardCall(window, "ApplyExpMicUi", false); root.UpdateLayout();
            var routing = (Grid)window.FindName("AppsRoutingPanel");
            var outputSection = (FrameworkElement)window.FindName("AppsOutputCard");
            var inputSection = (FrameworkElement)window.FindName("AppsInputCard");
            Check(inputSection.Visibility == Visibility.Collapsed && Grid.GetColumnSpan(outputSection) == 2
                && Math.Abs(((ComboBox)window.FindName("AppsOutputCombo")).ActualWidth - routing.ActualWidth) <= 1,
                mode + " microphone off collapses input and output selector fills the shared card");
            AppCardCall(window, "ApplyExpMicUi", true); root.UpdateLayout();
            Check(inputSection.Visibility == Visibility.Visible && Grid.GetColumnSpan(outputSection) == 1,
                mode + " microphone on restores both existing selectors");
            var routeTitle = routing.Children.OfType<TextBlock>().Single();
            var routeTitleBounds = routeTitle.TransformToAncestor(routing).TransformBounds(new Rect(routeTitle.RenderSize));
            Check(routing.Parent is Border sharedCard && sharedCard.Style == window.FindResource("AppsSectionCard")
                && VisualTreeHelper.GetParent(outputSection) == routing && VisualTreeHelper.GetParent(inputSection) == routing
                && !routing.Children.OfType<Border>().Any() && Grid.GetColumnSpan(routeTitle) == 2
                && routeTitleBounds.Left >= 0 && routeTitleBounds.Right <= routing.ActualWidth + 0.5
                && routeTitleBounds.Bottom <= outputSection.TranslatePoint(new Point(), routing).Y,
                mode + " routing title, output and input share one continuous card surface without separate inner card borders");
            foreach (string locale in new[] { "zh-CN", "en-US", "zh-TW", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "ru-RU" })
            {
                SetLanguage(locale);
                foreach (int width in new[] { 780, 920, 1280 })
                {
                    root = LayoutAppCards(window, width, width == 1280 ? 860 : 620);
                    var import = (Button)window.FindName("IconImportButton");
                    var actions = (WrapPanel)import.Parent;
                    var header = (Grid)actions.Parent;
                    var titleBounds = title.TransformToAncestor(header).TransformBounds(new Rect(title.RenderSize));
                    var pencilBounds = renameButton.TransformToAncestor(header).TransformBounds(new Rect(renameButton.RenderSize));
                    var actionBounds = actions.TransformToAncestor(header).TransformBounds(new Rect(actions.RenderSize));
                    bool headerFits = title.ActualWidth >= 35 && pencilBounds.Left >= titleBounds.Right - 0.5
                        && actionBounds.Left >= pencilBounds.Right - 0.5
                        && Math.Abs(actionBounds.Top) <= 0.5 && actionBounds.Right <= header.ActualWidth + 0.5
                        && new[] { "AppsRenameButton", "IconImportButton", "IconResetButton" }.All(name => {
                            var control = (FrameworkElement)window.FindName(name);
                            var bounds = control.TransformToAncestor(header).TransformBounds(new Rect(control.RenderSize));
                            return control.ActualWidth > 0 && bounds.Right <= header.ActualWidth + 0.5;
                        });
                    var slider = (Slider)window.FindName("AppsVolumeSlider");
                    var volumeText = (TextBlock)window.FindName("AppsVolumeText");
                    var row = (DockPanel)slider.Parent;
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(row).TransformBounds(new Rect(element.RenderSize));
                    var muteBounds = Bounds(mute); var sliderBounds = Bounds(slider); var volumeBounds = Bounds(volumeText);
                    bool volumeFits = ReferenceEquals(mute.Parent, row) && DockPanel.GetDock(mute) == Dock.Left
                        && Equals(mute.Content, L10n.T("Qp.Mute")) && mute.ActualHeight == 28
                        && Math.Abs(muteBounds.Left) < .5 && sliderBounds.Left >= muteBounds.Right + 9.5
                        && slider.ActualWidth > 40 && sliderBounds.Right <= volumeBounds.Left + .5
                        && volumeBounds.Right <= row.ActualWidth + .5 && slider.ActualHeight == 32;
                    Check(headerFits && volumeFits && new[] { "AppsDisableAutoButton", "AppsShowInPanelButton" }.All(name => {
                        var control = (CheckBox)window.FindName(name);
                        var border = AppCardTree((Grid)window.FindName("AppsControls")).OfType<Border>().First(b => b.Child is Grid grid && AppCardTree(grid).Contains(control));
                        var bounds = control.TransformToAncestor(border).TransformBounds(new Rect(control.RenderSize));
                        return control.ActualWidth == 34 && bounds.Left >= 0 && bounds.Right <= border.ActualWidth + 0.5
                            && Math.Abs(bounds.Top + bounds.Height / 2 - border.ActualHeight / 2) <= 0.5
                            && Math.Abs(bounds.Right - (border.ActualWidth - 13)) <= 0.5;
                    }), mode + " " + locale + " width=" + width + " app volume mute/slider/percentage and remaining controls fit without clipping");
                }
            }
            SetLanguage("zh-CN");
            root = LayoutAppCards(window, 1080, 760); RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + ".png"));
            RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-144dpi.png"), 1.5);
            root = LayoutAppCards(window, 780, 620); RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-narrow.png"));
            SetLanguage("de-DE"); root = LayoutAppCards(window, 920, 620); RenderAppCards(root, Path.Combine(output, "app-cards-" + mode + "-de.png"));
            list.SelectedItem = null;
            Check(!((Grid)window.FindName("AppsControls")).IsEnabled && !((Grid)window.FindName("AppsRoutingPanel")).IsEnabled
                && !renameButton.IsEnabled && rename.Visibility == Visibility.Collapsed
                && !mute.IsEnabled && !Muted() && shown.IsChecked == false && ((TextBlock)window.FindName("AppsVolumeText")).Text == "—"
                && ((ComboBox)window.FindName("AppsOutputCombo")).SelectedItem == null && ((ComboBox)window.FindName("AppsInputCombo")).SelectedItem == null,
                mode + " empty selection disables controls and clears old switch/volume state");
            window.Close();
            Check(Field<bool>(window, "_isClosed") && list.ItemsSource == null && list.Items.Count == 0,
                mode + " window uses existing close cleanup and releases items");
        }
    }
}
