using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void IPAssert(bool condition, string name)
    {
        Add(new { Case = "input-picker-check", Name = name, Passed = condition });
        if (!condition) throw new InvalidOperationException(name);
    }

    private static IEnumerable<T> IPChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in IPChildren<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void IPCall(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, PrivateInstance)!.Invoke(target, args);

    private static void IPClick(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static void IPImage(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(Output, name))) png.Save(stream);
    }

    private static void RunInputPickerCheck()
    {
        Config = new AppConfig { Language = "zh-CN", ThemeMode = "light", Accent = "#05DBFA",
            ExperimentalUnlocked = true, ExperimentalMode = true, ExperimentalMic = true,
            CollapseDeviceSections = false, ShowAppPeakMeter = false, MicMuteOsdPersistent = false,
            AutoStart = false, AutoStartStore = false, Hotkeys = new Dictionary<string, string>() };
        typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigService.ConfigPath)!);
        IPAssert(ConfigService.ConfigPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase), "isolated-config");
        L10n.Instance.SetLanguage("zh-CN");
        ThemeService.Apply("light", Config.Accent);
        string toastLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data/toasts.log");
        File.WriteAllText(toastLog, "");
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000 };
        main.Show();
        Pump(700);
        ((FrameworkElement)main.FindName("OverviewPage")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)main.FindName("SettingsPage")).Visibility = Visibility.Visible;
        IPCall(main, "LoadSettings");
        Pump(350);
        IPAssert(!File.Exists(toastLog) || File.ReadAllText(toastLog).Length == 0, "first-settings-no-experimental-toast");
        IPAssert(Config.ExperimentalMode && Config.ExperimentalMic, "settings-initial-values-preserved");

        var reference = new TextBox { Style = (Style)Application.Current.FindResource("ModernTextBox") };
        reference.ApplyTemplate();
        var settingsInputs = IPChildren<TextBox>(main).ToList();
        IPAssert(settingsInputs.Count > 0 && settingsInputs.All(x => ReferenceEquals(x.Template, reference.Template)), "settings-share-modern-input-template");
        var names = (ItemsControl)main.FindName("OutputNameList");
        var nameBox = IPChildren<TextBox>((DependencyObject)names.Items[0]).First();
        nameBox.Text = "测试设备名称";
        Pump(400);
        IPAssert(Config.DeviceNames[(string)nameBox.Tag] == "测试设备名称" && File.Exists(ConfigService.ConfigPath), "device-name-save");
        nameBox.Clear();
        Pump(400);
        IPAssert(!Config.DeviceNames.ContainsKey((string)nameBox.Tag), "empty-device-name-restores-default");
        nameBox.BringIntoView();
        Pump(100);
        IPChildren<ScrollViewer>((DependencyObject)main.FindName("SettingsPage")).First().ScrollToVerticalOffset(
            IPChildren<ScrollViewer>((DependencyObject)main.FindName("SettingsPage")).First().VerticalOffset + 220);
        Pump(100);
        IPImage(main, "settings-modern-inputs.png");

        ((FrameworkElement)main.FindName("SettingsPage")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)main.FindName("ThemePage")).Visibility = Visibility.Visible;
        IPCall(main, "LoadTheme");
        Pump(150);
        var hex = (TextBox)main.FindName("ThemeColorHex");
        IPAssert(ReferenceEquals(hex.Template, reference.Template), "theme-hex-modern-template");
        hex.Text = "#AB12EF";
        IPCall(main, "CommitThemeHex");
        Pump(250);
        IPAssert(Config.Accent == "#AB12EF", "theme-hex-commit-and-save");
        hex.Text = "#AB CD1";
        IPCall(main, "CommitThemeHex");
        IPAssert(Config.Accent == "#AB12EF" && hex.Text == "#AB12EF", "invalid-hex-restores-color");
        var more = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("OsdMoreToggle");
        more.IsChecked = true;
        Pump(220);
        IPAssert(((Button)main.FindName("OsdResetButtonTheme")).Content.ToString() == "还原位置", "short-reset-text-without-symbol");
        ((Button)main.FindName("OsdResetButtonTheme")).BringIntoView();
        Pump(100);
        IPChildren<ScrollViewer>((DependencyObject)main.FindName("ThemePage")).First().ScrollToVerticalOffset(
            IPChildren<ScrollViewer>((DependencyObject)main.FindName("ThemePage")).First().VerticalOffset + 360);
        Pump(100);
        IPImage(main, "theme-osd-short-reset.png");

        var search = (TextBox)main.FindName("AutoSearchBox");
        search.ApplyTemplate();
        IPAssert(ReferenceEquals(search.Template, reference.Template) && InputAppearance.GetHasSearchIcon(search), "search-shares-template-and-icon");
        search.Text = "中文规则";
        search.SelectAll();
        search.SelectedText = "English rule";
        IPAssert(search.Text == "English rule" && search.CanUndo, "text-selection-replacement-and-undo-available");
        search.Undo();
        IPAssert(search.Text == "中文规则", "undo-keeps-original-text");
        search.Redo();
        IPAssert(search.Text == "English rule", "redo");

        var osdStep = new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdTitle = "原主标题", OsdText = "原副标题" };
        var scriptStep = new AutoRuleStep { Action = AutoRuleAction.RunPowerShell, ProgramPaths = new List<string> { "C:\\test.ps1" } };
        var fields = new StackPanel();
        foreach (var step in new[] { osdStep, scriptStep })
            fields.Children.Add((UIElement)typeof(MainWindow).GetMethod("BuildAutomationStepParameters", PrivateInstance)!.Invoke(main, new object[] { step })!);
        var multi = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = "第一行\n第二行" };
        var readOnly = new TextBox { IsReadOnly = true, Text = "只读路径" };
        fields.Children.Add(multi); fields.Children.Add(readOnly);
        var fieldWindow = new Window { Owner = main, Content = fields, Width = 700, Height = 600, Left = -5000, Top = -5000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, IsHitTestVisible = false };
        fieldWindow.Show(); Pump(100);
        var dynamicInputs = IPChildren<TextBox>(fields).ToList();
        IPAssert(dynamicInputs.All(x => ReferenceEquals(x.Template, reference.Template)), "dynamic-automation-inputs-share-template");
        var osdInputs = dynamicInputs.Where(x => ReferenceEquals(x.Tag, osdStep)).ToList();
        osdInputs.First(x => x.Text == "原主标题").Text = "新主标题";
        osdInputs.First(x => x.Text == "原副标题").Text = "新副标题";
        IPAssert(osdStep.OsdTitle == "新主标题" && osdStep.OsdText == "新副标题", "automation-osd-fields-write-back");
        var delay = osdInputs.First(x => x.Width == 80);
        delay.Text = "250";
        IPAssert(osdStep.DelayMs == 250, "automation-delay-write-back");
        delay.Text = "99999";
        IPAssert(osdStep.DelayMs == 60000 && delay.Text == "60000", "automation-delay-clamp");
        var path = dynamicInputs.First(x => x.Text == "C:\\test.ps1");
        path.Text = "C:\\中文脚本.ps1";
        IPAssert(scriptStep.ProgramPaths[0] == path.Text && path.AllowDrop, "automation-path-write-back-and-drop-enabled");
        IPAssert(multi.LineCount >= 2 && multi.Template.FindName("PART_ContentHost", multi) is ScrollViewer, "multiline-native-content-host");
        var valueProvider = (IValueProvider)new TextBoxAutomationPeer(readOnly).GetPattern(PatternInterface.Value);
        bool blocked = false;
        try { valueProvider.SetValue("覆盖"); } catch (InvalidOperationException) { blocked = true; }
        IPAssert(valueProvider.IsReadOnly && blocked && readOnly.Text == "只读路径", "readonly-input-rejects-edit");
        fieldWindow.Close();

        var pickerType = typeof(MainWindow).Assembly.GetType("SonicRoute.ThemeColorPickerWindow")!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Func<Window> create = () =>
        {
            var window = (Window)Activator.CreateInstance(pickerType, flags, null, new object[] { main, Color.FromRgb(128, 255, 255) }, null)!;
            window.IsHitTestVisible = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -5000; window.Top = -5000;
            return window;
        };
        var picker = create();
        picker.ShowActivated = false;
        picker.Show();
        Pump(180);
        IPAssert(IPChildren<Button>(picker).Count(x => x.Style == (Style)Application.Current.FindResource("ColorSwatchButton")) == 96, "96-basic-colors");
        IPAssert(IPChildren<TextBox>(picker).All(x => ReferenceEquals(x.Template, reference.Template)), "picker-inputs-share-template");
        IPAssert(!IPChildren<TextBlock>(picker).Any(x => x.Text.Contains("自定义颜色") || x.Text.Contains("添加到")), "no-custom-color-collection-controls");
        var pickerHex = (TextBox)pickerType.GetField("_hex", flags)!.GetValue(picker)!;
        pickerHex.Text = "#123456";
        IPCall(picker, "CommitInput", pickerHex);
        IPAssert((Color)pickerType.GetProperty("SelectedColor", flags)!.GetValue(picker)! == Color.FromRgb(0x12, 0x34, 0x56), "picker-hex-selection");
        var red = (TextBox)pickerType.GetField("_red", flags)!.GetValue(picker)!;
        red.Text = "255";
        IPCall(picker, "CommitInput", red);
        IPAssert(pickerHex.Text == "#FF3456", "rgb-byte-input");
        red.Text = "256";
        IPCall(picker, "CommitInput", red);
        IPAssert(red.Text == "255" && pickerHex.Text == "#FF3456", "invalid-rgb-restored");
        var lightness = (Slider)pickerType.GetField("_lightnessSlider", flags)!.GetValue(picker)!;
        lightness.Value = 0.5;
        var spectrum = (Grid)pickerType.GetField("_spectrum", flags)!.GetValue(picker)!;
        IPAssert(spectrum.Background is LinearGradientBrush rainbow && rainbow.GradientStops.Count == 7, "full-rainbow-spectrum-retained");
        IPCall(picker, "SelectSpectrum", new Point(spectrum.ActualWidth / 3, 0));
        IPAssert(pickerHex.Text == "#00FF00", "spectrum-selects-green");
        IPCall(picker, "SelectSpectrum", new Point(spectrum.ActualWidth * 2 / 3, 0));
        IPAssert(pickerHex.Text == "#0000FF", "spectrum-selects-blue");
        lightness.Value = 1;
        IPAssert(pickerHex.Text == "#FFFFFF", "lightness-white-endpoint");
        lightness.Value = 0;
        IPAssert(pickerHex.Text == "#000000", "lightness-black-endpoint");
        lightness.Value = 0.5;
        IPAssert(pickerHex.Text == "#0000FF", "lightness-preserves-hue-and-saturation");
        var hueInput = (TextBox)pickerType.GetField("_hueInput", flags)!.GetValue(picker)!;
        hueInput.Text = "180";
        IPCall(picker, "CommitInput", hueInput);
        IPAssert(pickerHex.Text == "#00FFFF", "hsl-input-updates-selection");
        hueInput.Text = "361";
        IPCall(picker, "CommitInput", hueInput);
        IPAssert(hueInput.Text == "180" && pickerHex.Text == "#00FFFF", "invalid-hsl-restores-selection");
        IPImage(picker, "color-picker-light.png");
        ThemeService.Apply("dark", Config.Accent);
        Pump(100);
        IPImage(picker, "color-picker-dark.png");
        picker.Close();
        IPAssert(Config.Accent == "#AB12EF", "unconfirmed-picker-never-writes-theme");

        var confirm = create();
        var selected = Color.FromRgb(0xAA, 0x22, 0xBB);
        IPCall(confirm, "SelectColor", selected, true);
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => IPClick(IPChildren<Button>(confirm).First(x => Equals(x.Content, L10n.T("Color.Confirm"))))), DispatcherPriority.ApplicationIdle);
        bool? accepted = confirm.ShowDialog();
        IPAssert(accepted == true && (Color)pickerType.GetProperty("SelectedColor", flags)!.GetValue(confirm)! == selected, "picker-confirm-result");
        var cancel = create();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => IPClick(IPChildren<Button>(cancel).First(x => Equals(x.Content, L10n.T("Auto.Cancel"))))), DispatcherPriority.ApplicationIdle);
        IPAssert(cancel.ShowDialog() != true && Config.Accent == "#AB12EF", "picker-cancel-does-not-apply");

        var mathType = typeof(MainWindow).Assembly.GetType("SonicRoute.ColorValues")!;
        var toHsv = mathType.GetMethod("ToHsv", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fromHsv = mathType.GetMethod("FromHsv", BindingFlags.NonPublic | BindingFlags.Static)!;
        int roundTrips = 0;
        for (int r = 0; r < 256; r += 17)
            for (int g = 0; g < 256; g += 17)
                for (int b = 0; b < 256; b += 17)
                {
                    var color = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    var args = new object[] { color, 0.0, 0.0, 0.0 };
                    toHsv.Invoke(null, args);
                    if (!Equals(color, fromHsv.Invoke(null, new[] { args[1], args[2], args[3] }))) throw new Exception("HSV round trip");
                    roundTrips++;
                }
        IPAssert(roundTrips == 4096, "4096-rgb-hsv-round-trips");
        var toHsl = mathType.GetMethod("ToHsl", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fromHsl = mathType.GetMethod("FromHsl", BindingFlags.NonPublic | BindingFlags.Static)!;
        int hslRoundTrips = 0;
        for (int r = 0; r < 256; r += 17)
            for (int g = 0; g < 256; g += 17)
                for (int b = 0; b < 256; b += 17)
                {
                    var color = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    var args = new object[] { color, 0.0, 0.0, 0.0 };
                    toHsl.Invoke(null, args);
                    if (!Equals(color, fromHsl.Invoke(null, new[] { args[1], args[2], args[3] }))) throw new Exception("HSL round trip");
                    hslRoundTrips++;
                }
        IPAssert(hslRoundTrips == 4096, "4096-rgb-hsl-round-trips");
        main.Close();
        Pump(100);
        Add(new { Case = "input-picker-summary", Passed = true, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ScreenshotDirectory = Output, Note = "Isolated UI only; no real rules/audio/autostart/clipboard writes. IME not simulated." });
    }
}
