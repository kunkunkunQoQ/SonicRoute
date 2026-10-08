using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static class Program
{
    private static readonly List<object> Results = new List<object>();
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string Output = "";
    private static AppConfig Config = new AppConfig();
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };
    private static readonly Func<long>? AllocationCounter = CreateAllocationCounter();
    private static long Allocated() => AllocationCounter == null ? -1 : AllocationCounter();

    private sealed class ResourceSample
    {
        public int Index { get; set; }
        public double ElapsedSeconds { get; set; }
        public double CpuOneCorePercent { get; set; }
        public double CpuMachinePercent { get; set; }
        public double SystemCpuPercent { get; set; }
        public double PrivateMiB { get; set; }
        public double WorkingSetMiB { get; set; }
        public double ManagedHeapMiB { get; set; }
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public long LayoutUpdatedCount { get; set; }
        public long SizeChangedCount { get; set; }
        public long HexTextChangedCount { get; set; }
        public bool ThemePreviewTimerEnabled { get; set; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);
    private static Func<long>? CreateAllocationCounter()
    {
        var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
        return method == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Output = args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(Output);
        Environment.SetEnvironmentVariable("SONICROUTE_PERF_ISOLATED", "1");
        try
        {
            Config = new AppConfig { Language = "en-US", ThemeMode = "dark", MicMuteOsdPersistent = false };
            Config.Hotkeys = new Dictionary<string, string>(HotkeyActions.Defaults);
            typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
            typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
            if (!ConfigService.ConfigPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Storage isolation missing");
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent(); // Copied App.OnStartup explicitly suppresses queued startup in this test process.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            L10n.Instance.SetLanguage("en-US");
            ThemeService.Apply("dark", "blue");
            ThemeService.ApplyBackgroundOpacity(Config.BackgroundOpacity);
            Add(new { Case = "environment", Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                LogicalProcessors = Environment.ProcessorCount, RenderTier = System.Windows.Media.RenderCapability.Tier >> 16,
                ConfigPath = ConfigService.ConfigPath, RulesDir = AutoRuleStore.RulesDir,
                Note = "Production source copy; config and rule directory paths redirected, App.OnStartup suppressed. No rules, routes, or volume writes." });
            string mode = args.Length > 1 ? args[1] : "all";
            if (mode == "resource")
            {
                string page = args.Length > 2 ? args[2] : "theme";
                int ruleCount = args.Length > 3 ? int.Parse(args[3]) : 0;
                int warmupSeconds = args.Length > 4 ? int.Parse(args[4]) : 45;
                int sampleSeconds = args.Length > 5 ? int.Parse(args[5]) : 120;
                int activityHz = args.Length > 6 ? int.Parse(args[6]) : 30;
                string themeHide = args.Length > 7 ? args[7] : "none";
                RunResourceSample(page, ruleCount, warmupSeconds, sampleSeconds, activityHz, themeHide);
            }
            else if (mode == "theme-check") RunThemeCheck();
            else if (mode == "theme-light-check") RunThemeCheck(args.Length > 2 ? args[2] : "zh-CN", true);
            else if (mode == "settings-check") RunSettingsCheck();
            else if (mode == "delay-check") RunDelayAlignmentCheck();
            else if (mode == "all") { RunCore(); RunUi(); }
            else if (mode == "core-only") RunCore();
            else if (mode == "peak-ui-only") RunPeakUi();
            else if (mode == "persistence-only") RunPersistenceCheck();
            else if (mode == "supplemental") { RunSupplementalCore(); RunPersistenceCheck(); RunWindows(true); }
            else if (mode == "windows-only") RunWindows();
            else RunUi();
            Save();
            return 0;
        }
        catch (Exception ex)
        {
            Add(new { Case = "error", Error = ex.ToString() });
            Save();
            return 1;
        }
    }

    private static void RunResourceSample(string page, int ruleCount, int warmupSeconds, int sampleSeconds, int activityHz, string themeHide = "none")
    {
        if (page != "theme" && page != "settings" && page != "automation" && page != "active-osd") throw new ArgumentException("Page must be theme, settings, automation, or active-osd.");
        if (page != "theme" && themeHide != "none") throw new ArgumentException("Theme diagnostics apply only to the theme page.");
        if (themeHide != "none" && themeHide != "preview" && themeHide != "advanced" && themeHide != "radio" && themeHide != "osd")
            throw new ArgumentException("Theme hide case must be none, preview, advanced, radio, or osd.");
        if (page == "active-osd" && ruleCount != 0) throw new ArgumentException("The active OSD scenario requires zero rules.");
        if (page == "active-osd" && (activityHz < 1 || activityHz > 60)) throw new ArgumentOutOfRangeException(nameof(activityHz));
        if (ruleCount < 0 || warmupSeconds < 0 || sampleSeconds < 10) throw new ArgumentOutOfRangeException("Invalid resource sample parameters.");

        Config = new AppConfig
        {
            Language = "en-US",
            ThemeMode = "dark",
            Accent = "blue",
            BackgroundOpacity = 85,
            QuickPanelStyle = "modern",
            ShowAppPeakMeter = false,
            MicMuteOsdPersistent = false
        };
        Config.Hotkeys = new Dictionary<string, string>();
        typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
        var rules = Enumerable.Range(0, ruleCount).Select(i => new AutoRule
        {
            Id = "resource-fixture-" + i.ToString("D3"),
            Name = "Resource fixture " + i.ToString("D3"),
            Enabled = false,
            Actions = new List<AutoRuleStep> { new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdTitle = "Fixture", OsdText = "Disabled test rule" } }
        }).ToList();
        typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, rules);
        if (!ConfigService.ConfigPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
            !AutoRuleStore.RulesDir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Storage isolation missing before resource sample.");

        L10n.Instance.SetLanguage("en-US");
        ThemeService.Apply("dark", "blue");
        ThemeService.ApplyBackgroundOpacity(Config.BackgroundOpacity);

        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Width = 920, Height = 620, WindowStartupLocation = WindowStartupLocation.Manual };
        var windowAndPointer = ShowWindowAwayFromPointer(main);
        Pump(1000);
        string navName = page switch
        {
            "theme" => "NavTheme",
            "settings" => "NavSettings",
            _ => "NavAutomation"
        };
        ((RadioButton)main.FindName(navName)).IsChecked = true;
        if (page == "theme" && themeHide != "none") ApplyThemeVisibilityDiagnostic(main, themeHide);
        if (page == "automation" || page == "active-osd")
        {
            var refresh = (Task)typeof(MainWindow).GetMethod("RefreshAutoRulesAsync", PrivateInstance)!.Invoke(main, null)!;
            var timeout = Stopwatch.StartNew();
            while (!refresh.IsCompleted && timeout.ElapsedMilliseconds < 10000) Pump(10);
            if (!refresh.IsCompleted) throw new TimeoutException("Automation page did not settle before resource sampling.");
            refresh.GetAwaiter().GetResult();
            var list = (ItemsControl)main.FindName("AutoRuleList");
            if (list.Items.Count != ruleCount) throw new InvalidOperationException("Automation fixture count mismatch.");
        }
        Pump(1500);
        System.Windows.Input.Keyboard.ClearFocus();
        Pump(250);
        object? activeOsd = null;
        MethodInfo? showVolumeOsd = null;
        DispatcherTimer? activityTimer = null;
        int activityUpdates = 0;
        int activityUpdatesDuringSample = 0;
        double activitySampleElapsedSeconds = 0;
        if (page == "active-osd")
        {
            Config.OsdVolumeVisualEnabled = true;
            var osdType = typeof(MainWindow).Assembly.GetType("SonicRoute.OsdService", throwOnError: true)!;
            activeOsd = Activator.CreateInstance(osdType, nonPublic: true)!;
            showVolumeOsd = osdType.GetMethod("ShowVolume", BindingFlags.Instance | BindingFlags.NonPublic)!;
            activityTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1000.0 / activityHz) };
            activityTimer.Tick += (_, _) =>
            {
                activityUpdates++;
                int volume = (activityUpdates * 4) % 101;
                showVolumeOsd.Invoke(activeOsd, new object[] { "Synthetic benchmark", volume, volume + "%", false, "resource-perf" });
            };
            activityTimer.Start();
        }
        Pump(warmupSeconds * 1000);
        System.Windows.Input.Keyboard.ClearFocus();
        Pump(250);

        long layoutUpdatedCount = 0;
        long sizeChangedCount = 0;
        long hexTextChangedCount = 0;
        long themePreviewTimerEnabledSeconds = 0;
        EventHandler layoutUpdatedHandler = (_, _) => layoutUpdatedCount++;
        SizeChangedEventHandler sizeChangedHandler = (_, _) => sizeChangedCount++;
        main.LayoutUpdated += layoutUpdatedHandler;
        main.SizeChanged += sizeChangedHandler;
        var themePageElement = page == "theme" ? (FrameworkElement)main.FindName("ThemePage") : null;
        themePageElement?.AddHandler(FrameworkElement.SizeChangedEvent, sizeChangedHandler);
        var hexEditor = page == "theme" ? main.FindName("RgbHex") as TextBox : null;
        TextChangedEventHandler? hexChangedHandler = null;
        if (hexEditor != null)
        {
            hexChangedHandler = (_, _) => hexTextChangedCount++;
            hexEditor.TextChanged += hexChangedHandler;
        }
        var themePreviewTimer = page == "theme"
            ? typeof(MainWindow).GetField("_themePreviewTimer", PrivateInstance)?.GetValue(main) as DispatcherTimer
            : null;

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var rows = new List<ResourceSample>(sampleSeconds);
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        double previousElapsed = 0;
        double previousCpuMs = process.TotalProcessorTime.TotalMilliseconds;
        GetSystemTimes(out var previousIdle, out var previousKernel, out var previousUser);
        int activityUpdatesBeforeSample = activityUpdates;
        var activitySampleClock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            process.Refresh();
            double elapsed = clock.Elapsed.TotalSeconds;
            double cpuMs = process.TotalProcessorTime.TotalMilliseconds;
            double wallMs = Math.Max(1, (elapsed - previousElapsed) * 1000.0);
            double oneCorePercent = 100.0 * (cpuMs - previousCpuMs) / wallMs;
            double systemCpuPercent = SystemCpuPercent(previousIdle, previousKernel, previousUser,
                out previousIdle, out previousKernel, out previousUser);
            rows.Add(new ResourceSample
            {
                Index = rows.Count + 1,
                ElapsedSeconds = elapsed,
                CpuOneCorePercent = oneCorePercent,
                CpuMachinePercent = oneCorePercent / Environment.ProcessorCount,
                SystemCpuPercent = systemCpuPercent,
                PrivateMiB = process.PrivateMemorySize64 / 1048576.0,
                WorkingSetMiB = process.WorkingSet64 / 1048576.0,
                ManagedHeapMiB = GC.GetTotalMemory(false) / 1048576.0,
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                LayoutUpdatedCount = layoutUpdatedCount,
                SizeChangedCount = sizeChangedCount,
                HexTextChangedCount = hexTextChangedCount,
                ThemePreviewTimerEnabled = themePreviewTimer?.IsEnabled == true
            });
            if (themePreviewTimer?.IsEnabled == true) themePreviewTimerEnabledSeconds++;
            previousElapsed = elapsed;
            previousCpuMs = cpuMs;
            if (rows.Count >= sampleSeconds)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        activitySampleClock.Stop();
        activitySampleElapsedSeconds = activitySampleClock.Elapsed.TotalSeconds;
        activityUpdatesDuringSample = activityUpdates - activityUpdatesBeforeSample;
        activityTimer?.Stop();
        (activeOsd as IDisposable)?.Dispose();
        main.LayoutUpdated -= layoutUpdatedHandler;
        main.SizeChanged -= sizeChangedHandler;
        themePageElement?.RemoveHandler(FrameworkElement.SizeChangedEvent, sizeChangedHandler);
        if (hexEditor != null && hexChangedHandler != null) hexEditor.TextChanged -= hexChangedHandler;

        string scenario = page + "-" + ruleCount.ToString("D3");
        File.WriteAllText(Path.Combine(Output, "samples-" + scenario + ".json"), JsonSerializer.Serialize(rows, JsonOptions));
        var machineCpu = rows.Select(x => x.CpuMachinePercent).ToList();
        var oneCoreCpu = rows.Select(x => x.CpuOneCorePercent).ToList();
        var privateBytes = rows.Select(x => x.PrivateMiB).ToList();
        var workingSet = rows.Select(x => x.WorkingSetMiB).ToList();
        var managedHeap = rows.Select(x => x.ManagedHeapMiB).ToList();
        var systemCpu = rows.Where(x => x.SystemCpuPercent >= 0).Select(x => x.SystemCpuPercent).ToList();
        Add(new
        {
            Case = "resource-sample",
            Scenario = scenario,
            Page = page,
            DisabledRules = ruleCount,
            Activity = page == "active-osd" ? "synthetic ShowVolume updates, OsdVolumeVisualEnabled=true; no audio APIs" : "none",
            ActivityUpdates = activityUpdates,
            ActivityUpdatesDuringSample = activityUpdatesDuringSample,
            ActivitySampleSeconds = activitySampleElapsedSeconds,
            ActivityActualHzDuringSample = activitySampleElapsedSeconds > 0 ? activityUpdatesDuringSample / activitySampleElapsedSeconds : 0,
            ActivityRequestedHz = page == "active-osd" ? activityHz : 0,
            WarmupSeconds = warmupSeconds,
            RequestedSampleSeconds = sampleSeconds,
            ThemeHideCase = page == "theme" ? themeHide : "none",
            WindowWidth = main.ActualWidth,
            WindowHeight = main.ActualHeight,
            WindowLeft = main.Left,
            WindowTop = main.Top,
            PointerOutsideWindow = windowAndPointer.PointerOutside,
            PointerRepositionedWindow = windowAndPointer.Repositioned,
            Samples = rows.Count,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            LogicalProcessors = Environment.ProcessorCount,
            MachineCpuMeanPercent = machineCpu.Average(),
            MachineCpuMinPercent = machineCpu.Min(),
            MachineCpuMaxPercent = machineCpu.Max(),
            MachineCpuP95Percent = Percentile(machineCpu, 0.95),
            SystemCpuMeanPercent = systemCpu.Count == 0 ? -1 : systemCpu.Average(),
            SystemCpuMinPercent = systemCpu.Count == 0 ? -1 : systemCpu.Min(),
            SystemCpuMaxPercent = systemCpu.Count == 0 ? -1 : systemCpu.Max(),
            SystemCpuP95Percent = systemCpu.Count == 0 ? -1 : Percentile(systemCpu, 0.95),
            OneCoreCpuMeanPercent = oneCoreCpu.Average(),
            OneCoreCpuMinPercent = oneCoreCpu.Min(),
            OneCoreCpuMaxPercent = oneCoreCpu.Max(),
            PrivateMeanMiB = privateBytes.Average(),
            PrivateMinMiB = privateBytes.Min(),
            PrivateMaxMiB = privateBytes.Max(),
            PrivateStartMiB = privateBytes.First(),
            PrivateEndMiB = privateBytes.Last(),
            WorkingSetMeanMiB = workingSet.Average(),
            WorkingSetMinMiB = workingSet.Min(),
            WorkingSetMaxMiB = workingSet.Max(),
            WorkingSetStartMiB = workingSet.First(),
            WorkingSetEndMiB = workingSet.Last(),
            ManagedHeapMeanMiB = managedHeap.Average(),
            ManagedHeapMinMiB = managedHeap.Min(),
            ManagedHeapMaxMiB = managedHeap.Max(),
            ManagedHeapStartMiB = managedHeap.First(),
            ManagedHeapEndMiB = managedHeap.Last(),
            LayoutUpdatedCount = layoutUpdatedCount,
            SizeChangedCount = sizeChangedCount,
            HexTextChangedCount = hexTextChangedCount,
            ThemePreviewTimerEnabledSeconds = themePreviewTimerEnabledSeconds,
            Note = "One process, isolated config/rules, startup suppressed, no real rules/audio writes. Managed heap sampled in-process with GC.GetTotalMemory(false). Keyboard focus was cleared before warmup and before sample; 250ms UI settle followed each clear."
        });
        main.Close();
        Pump(500);
    }

    private static void RunThemeCheck(string language = "en-US", bool lightScreenshot = false)
    {
        Config = new AppConfig { Language = language, ThemeMode = lightScreenshot ? "light" : "dark", Accent = "blue", BackgroundOpacity = 85, ShowAppPeakMeter = false };
        typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
        typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
        if (!ConfigService.ConfigPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
            !AutoRuleStore.RulesDir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
            !L10n.ExternalLangDir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Theme-check storage isolation missing.");

        L10n.Instance.SetLanguage(language);
        ThemeService.Apply(Config.ThemeMode, Config.Accent);
        ThemeService.ApplyBackgroundOpacity(Config.BackgroundOpacity);
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        main.Show();
        Pump(500);
        ((RadioButton)main.FindName("NavTheme")).IsChecked = true;
        Pump(500);

        bool Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Theme check failed: " + label);
            return condition;
        }

        var themeSystem = (RadioButton)main.FindName("ThemeSystem");
        var themeLight = (RadioButton)main.FindName("ThemeLight");
        var themeDark = (RadioButton)main.FindName("ThemeDark");
        var accentBlue = (RadioButton)main.FindName("AccentBlue");
        var accentGreen = (RadioButton)main.FindName("AccentGreen");
        var accentPurple = (RadioButton)main.FindName("AccentPurple");
        var accentCustom = (RadioButton)main.FindName("AccentCustom");
        var hex = (TextBox)main.FindName("RgbHex");
        var advanced = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("ThemeAdvancedToggle");
        var advancedPanel = (FrameworkElement)main.FindName("ThemeAdvancedPanel");
        var opacity = (Slider)main.FindName("OpacitySlider");

        themeSystem.IsChecked = true;
        bool systemMode = Check(Config.ThemeMode == "system", "system theme mode");
        themeLight.IsChecked = true;
        bool lightMode = Check(Config.ThemeMode == "light", "light theme mode");
        themeDark.IsChecked = true;
        bool darkMode = Check(Config.ThemeMode == "dark", "dark theme mode");
        accentGreen.IsChecked = true;
        bool greenAccent = Check(Config.Accent == "green", "green accent");
        accentPurple.IsChecked = true;
        bool purpleAccent = Check(Config.Accent == "purple", "purple accent");
        accentBlue.IsChecked = true;
        bool blueAccent = Check(Config.Accent == "blue", "blue accent");

        hex.Text = "#2F80ED";
        string accentBeforeFocus = Config.Accent;
        string displayBeforeFocus = typeof(MainWindow).GetField("_themeHexDisplayText", PrivateInstance)?.GetValue(main)?.ToString() ?? "(field missing)";
        string mainFields = string.Join(",", typeof(MainWindow).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Select(x => x.Name));
        typeof(MainWindow).GetMethod("ThemeHex_LostFocus", PrivateInstance)!.Invoke(main,
            new object[] { hex, new RoutedEventArgs(TextBox.LostFocusEvent, hex) });
        bool focusOnlyPreservesPreset = Check(Config.Accent == "blue", "focusing and leaving an unchanged Hex field preserves the preset accent (before=" + accentBeforeFocus + ", display=" + displayBeforeFocus + ", after=" + Config.Accent + ", text=" + hex.Text + ", fields=" + mainFields + ")");

        var commit = typeof(MainWindow).GetMethod("CommitThemeHex", PrivateInstance);
        if (commit == null) throw new InvalidOperationException("Hex commit handler is missing.");
        hex.Text = "12AB34";
        commit.Invoke(main, null);
        bool validWithoutPrefix = Check(Config.Accent == "#12AB34" && hex.Text == "#12AB34" && accentCustom.IsChecked == true,
            "valid Hex without prefix applies custom color");
        hex.Text = "#C01020";
        commit.Invoke(main, null);
        bool validWithPrefix = Check(Config.Accent == "#C01020" && hex.Text == "#C01020", "valid Hex with prefix applies custom color");
        string lastValidAccent = Config.Accent;
        hex.Text = "GGHHII";
        commit.Invoke(main, null);
        bool invalidHexRejected = Check(Config.Accent == lastValidAccent && hex.Text == lastValidAccent,
            "invalid Hex leaves the active color intact and restores the editor");
        hex.Text = "#AB CD1";
        commit.Invoke(main, null);
        bool embeddedWhitespaceHexRejected = Check(Config.Accent == lastValidAccent && hex.Text == lastValidAccent,
            "Hex with embedded whitespace is rejected");

        advanced.IsChecked = true;
        Pump(400);
        bool advancedOpens = Check(advancedPanel.Visibility == Visibility.Visible, "RGB advanced panel opens");
        var red = (Slider)main.FindName("RSlider");
        var green = (Slider)main.FindName("GSlider");
        var blue = (Slider)main.FindName("BSlider");
        red.Value = 26;
        green.Value = 88;
        blue.Value = 166;
        Pump(350);
        string sliderAccent = $"#{(int)Math.Round(red.Value):X2}{(int)Math.Round(green.Value):X2}{(int)Math.Round(blue.Value):X2}";
        bool rgbSlidersApply = Check(Config.Accent == sliderAccent && hex.Text == sliderAccent, "RGB sliders update custom color and Hex field");
        advanced.IsChecked = false;
        Pump(400);
        bool advancedCloses = Check(advancedPanel.Visibility == Visibility.Collapsed, "RGB advanced panel closes");

        opacity.Value = 82;
        Pump(250);
        bool opacityResponds = Check(Config.BackgroundOpacity == 82, "opacity slider updates isolated configuration");
        opacity.Value = 85;
        if (lightScreenshot) themeLight.IsChecked = true; else themeDark.IsChecked = true;
        accentBlue.IsChecked = true;
        Pump(400);

        advanced.IsChecked = !lightScreenshot;
        Pump(450);
        Pump(2800);
        main.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(main);
        int width = Math.Max(1, (int)Math.Ceiling(main.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(main.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(main);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string screenshotPath = Path.Combine(Output, "theme-page.png");
        using (var stream = File.Create(screenshotPath)) encoder.Save(stream);

        var themePage = (Grid)main.FindName("ThemePage");
        var themeScroll = themePage.Children.OfType<ScrollViewer>().Single();
        var themeContent = (StackPanel)themeScroll.Content;
        var savedBackground = themeContent.Background;
        themeContent.Background = main.Background;
        int fullPageWidth = Math.Max(1, (int)Math.Ceiling(themeContent.ActualWidth * dpi.DpiScaleX));
        int fullPageHeight = Math.Max(1, (int)Math.Ceiling(themeContent.ActualHeight * dpi.DpiScaleY));
        var fullPageBitmap = new RenderTargetBitmap(fullPageWidth, fullPageHeight, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            System.Windows.Media.PixelFormats.Pbgra32);
        fullPageBitmap.Render(themeContent);
        themeContent.Background = savedBackground;
        var fullPageEncoder = new PngBitmapEncoder();
        fullPageEncoder.Frames.Add(BitmapFrame.Create(fullPageBitmap));
        string fullPageScreenshotPath = Path.Combine(Output, "theme-page-full.png");
        using (var stream = File.Create(fullPageScreenshotPath)) fullPageEncoder.Save(stream);

        Add(new
        {
            Case = "theme-controls",
            Language = language,
            ThemeAtScreenshot = lightScreenshot ? "light" : "dark",
            RgbAdvancedExpanded = advanced.IsChecked == true,
            SystemMode = systemMode,
            LightMode = lightMode,
            DarkMode = darkMode,
            GreenAccent = greenAccent,
            PurpleAccent = purpleAccent,
            BlueAccent = blueAccent,
            FocusOnlyPreservesPreset = focusOnlyPreservesPreset,
            ValidHexWithoutPrefix = validWithoutPrefix,
            ValidHexWithPrefix = validWithPrefix,
            InvalidHexRejected = invalidHexRejected,
            EmbeddedWhitespaceHexRejected = embeddedWhitespaceHexRejected,
            AdvancedOpens = advancedOpens,
            RgbSlidersApply = rgbSlidersApply,
            AdvancedCloses = advancedCloses,
            OpacityResponds = opacityResponds,
            Screenshot = screenshotPath,
            FullPageScreenshot = fullPageScreenshotPath,
            FullPageLogicalHeight = themeContent.ActualHeight,
            Note = "Theme page exercised in an isolated source-copy test process. The system color dialog and audio controls were not opened or changed."
        });
        main.Close();
        Pump(500);
    }

    private static void RunSettingsCheck()
    {
        Config = new AppConfig
        {
            Language = "zh-CN",
            ThemeMode = "light",
            Accent = "blue",
            BackgroundOpacity = 85,
            StartMinimized = true,
            CollapseDeviceSections = true,
            ExperimentalMic = true,
            ShowAppPeakMeter = false
        };
        Config.Hotkeys = new Dictionary<string, string>(HotkeyActions.Defaults);
        typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
        typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
        if (!ConfigService.ConfigPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
            !AutoRuleStore.RulesDir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
            !L10n.ExternalLangDir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Settings-check storage isolation missing.");

        L10n.Instance.SetLanguage("zh-CN");
        ThemeService.Apply("light", "blue");
        ThemeService.ApplyBackgroundOpacity(Config.BackgroundOpacity);
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Width = 780, Height = 760 };
        main.Show();
        Pump(1200);
        ((RadioButton)main.FindName("NavSettings")).IsChecked = true;
        Pump(1400);

        bool Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Settings check failed: " + label);
            return condition;
        }
        void SetExpanded(ToggleButton toggle, bool expanded)
        {
            toggle.IsChecked = expanded;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
        }

        var keepToggle = (ToggleButton)main.FindName("KeepDevicesMoreToggle");
        var namesToggle = (ToggleButton)main.FindName("DeviceNamesMoreToggle");
        var keepBody = (FrameworkElement)main.FindName("KeepDevicesBody");
        var namesBody = (FrameworkElement)main.FindName("DeviceNamesBody");
        var moreOptionsStyle = (Style)main.FindResource("MoreOptionsToggle");
        bool sharedMoreOptionsStyle = new[] { keepToggle, namesToggle, (ToggleButton)main.FindName("SettingsMoreToggle") }
            .All(toggle => ReferenceEquals(toggle.Style, moreOptionsStyle));
        bool moreOptionsUsesThemeAccent = ReferenceEquals(((ToggleButton)main.FindName("SettingsMoreToggle")).Foreground, main.FindResource("Theme.Accent"));
        Check(sharedMoreOptionsStyle, "device and settings disclosures use the shared MoreOptionsToggle template");
        Check(moreOptionsUsesThemeAccent, "MoreOptionsToggle foreground resolves to the light theme accent");
        bool deviceSectionCollapseDefault = Config.CollapseDeviceSections &&
            keepBody.Visibility == Visibility.Collapsed && namesBody.Visibility == Visibility.Collapsed;
        SetExpanded(keepToggle, true);
        SetExpanded(namesToggle, true);
        Pump(500);
        bool deviceSectionsOpen = Check(keepBody.Visibility == Visibility.Visible && namesBody.Visibility == Visibility.Visible,
            "collapsed device sections expand through their new toggles");
        SetExpanded(keepToggle, false);
        SetExpanded(namesToggle, false);
        Pump(420);
        bool deviceSectionsClose = Check(keepBody.Visibility == Visibility.Collapsed && namesBody.Visibility == Visibility.Collapsed,
            "both device sections close through their toggles");
        SetExpanded(keepToggle, true);
        SetExpanded(namesToggle, true);
        Pump(500);

        var outputList = (ItemsControl)main.FindName("OutputFilterList");
        var inputList = (ItemsControl)main.FindName("InputFilterList");
        var outputBoxes = outputList.Items.OfType<CheckBox>().ToList();
        var inputBoxes = inputList.Items.OfType<CheckBox>().ToList();
        if (outputBoxes.Count == 0 || inputBoxes.Count == 0)
            throw new InvalidOperationException("Settings check expected both isolated output and input device fixtures.");
        var selectAllOutput = (Button)main.FindName("SelectAllOutputButton");
        selectAllOutput.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, selectAllOutput));
        bool outputSelectAllOff = Check(outputBoxes.All(x => x.IsChecked == false) && Config.HiddenOutputDevices.Count == outputBoxes.Count,
            "output Select All button hides all devices in isolated config");
        selectAllOutput.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, selectAllOutput));
        bool outputSelectAllOn = Check(outputBoxes.All(x => x.IsChecked == true) && Config.HiddenOutputDevices.Count == 0,
            "output Select All button restores every device");
        var oneOutput = outputBoxes[0];
        oneOutput.IsChecked = false;
        string oneOutputId = ((AudioDeviceInfo)oneOutput.Tag).Id;
        bool outputSingleOff = Check(Config.HiddenOutputDevices.SequenceEqual(new[] { oneOutputId }),
            "single output checkbox writes only its ID to isolated config");
        oneOutput.IsChecked = true;
        bool outputSingleRestored = Check(Config.HiddenOutputDevices.Count == 0,
            "single output checkbox restores isolated selection");

        var selectAllInput = (Button)main.FindName("SelectAllInputButton");
        selectAllInput.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, selectAllInput));
        bool inputSelectAllOff = Check(inputBoxes.All(x => x.IsChecked == false) && Config.HiddenInputDevices.Count == inputBoxes.Count,
            "input Select All button hides all devices in isolated config");
        selectAllInput.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, selectAllInput));
        bool inputSelectAllOn = Check(inputBoxes.All(x => x.IsChecked == true) && Config.HiddenInputDevices.Count == 0,
            "input Select All button restores every device");
        var oneInput = inputBoxes[0];
        oneInput.IsChecked = false;
        string oneInputId = ((AudioDeviceInfo)oneInput.Tag).Id;
        bool inputSingleOff = Check(Config.HiddenInputDevices.SequenceEqual(new[] { oneInputId }),
            "single input checkbox writes only its ID to isolated config");
        oneInput.IsChecked = true;
        bool inputSingleRestored = Check(Config.HiddenInputDevices.Count == 0,
            "single input checkbox restores isolated selection");

        var outputNames = (ItemsControl)main.FindName("OutputNameList");
        var nameBox = FindVisualDescendants<TextBox>(outputNames).FirstOrDefault(x => x.Tag is string);
        if (nameBox == null) throw new InvalidOperationException("Settings check could not locate the system-default output name editor.");
        string nameId = (string)nameBox.Tag;
        nameBox.Text = "隔离测试名称";
        typeof(MainWindow).GetMethod("FlushNameChanges", PrivateInstance)!.Invoke(main, new object[] { false });
        bool nameSaved = Check(Config.DeviceNames.TryGetValue(nameId, out var configuredName) && configuredName == "隔离测试名称",
            "device name edit saves only to isolated config");
        var nameRow = ItemsControl.ContainerFromElement(outputNames, nameBox) as DependencyObject ?? outputNames;
        var clearButton = FindVisualDescendants<Button>(nameRow).FirstOrDefault(x => (x.Content as string) == "×");
        if (clearButton == null) throw new InvalidOperationException("Settings check could not locate the name clear button.");
        clearButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, clearButton));
        typeof(MainWindow).GetMethod("FlushNameChanges", PrivateInstance)!.Invoke(main, new object[] { false });
        bool nameCleared = Check(!Config.DeviceNames.ContainsKey(nameId) && string.IsNullOrEmpty(nameBox.Text),
            "device name clear button removes the isolated override");

        var recent = (RadioButton)main.FindName("DefaultAppRecent");
        var last = (RadioButton)main.FindName("DefaultAppLast");
        var fixedMode = (RadioButton)main.FindName("DefaultAppFixed");
        var fixedCombo = (ComboBox)main.FindName("FixedAppCombo");
        recent.IsChecked = true;
        bool recentEnabled = Check(Config.DefaultAppMode == "recent" && !fixedCombo.IsEnabled,
            "recent default-app option disables fixed-app dropdown");
        last.IsChecked = true;
        bool lastEnabled = Check(Config.DefaultAppMode == "last" && !fixedCombo.IsEnabled,
            "last-used default-app option disables fixed-app dropdown");
        fixedMode.IsChecked = true;
        bool fixedEnabled = Check(Config.DefaultAppMode == "fixed" && fixedCombo.IsEnabled,
            "fixed default-app option enables fixed-app dropdown");
        recent.IsChecked = true;

        var startMinimized = (CheckBox)main.FindName("SettingsStartMinimized");
        bool initialMinimized = startMinimized.IsChecked == true;
        var startMinimizedPeer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(startMinimized);
        var toggleProvider = (System.Windows.Automation.Provider.IToggleProvider)startMinimizedPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)!;
        toggleProvider.Toggle();
        bool startMinimizedClicked = Check(Config.StartMinimized == !initialMinimized && startMinimized.IsChecked == !initialMinimized,
            "StartMinimized full-row CheckBox automation click updates isolated config");
        toggleProvider.Toggle();
        bool startMinimizedRestored = Check(Config.StartMinimized == initialMinimized && startMinimized.IsChecked == initialMinimized,
            "StartMinimized toggle restores its original isolated value");

        var settingsMoreToggle = (ToggleButton)main.FindName("SettingsMoreToggle");
        SetExpanded(settingsMoreToggle, true);
        Pump(350);

        main.UpdateLayout();
        var settingsGrid = (Grid)main.FindName("SettingsPage");
        var settingsScroll = settingsGrid.Children.OfType<ScrollViewer>().Single();
        bool noHorizontalOverflow = Check(settingsScroll.ExtentWidth <= settingsScroll.ViewportWidth + 1.0,
            "Settings page has no horizontal overflow at 780 DIP (extent=" + settingsScroll.ExtentWidth + ", viewport=" + settingsScroll.ViewportWidth + ")");
        var doNotClickNames = new[] { "SettingsAutoStart", "CleanAutoStartBtn" };
        var untouchedDoNotClick = doNotClickNames.Select(name =>
        {
            var element = (FrameworkElement)main.FindName(name);
            return new { Name = name, Present = element != null, Visibility = element?.Visibility.ToString(), Width = element?.ActualWidth, Height = element?.ActualHeight };
        }).ToArray();
        var settingsMorePanel = (FrameworkElement)main.FindName("SettingsMorePanel");
        bool cleanAutoStartLayoutVisible = Check(settingsMorePanel.Visibility == Visibility.Visible &&
            ((FrameworkElement)main.FindName("CleanAutoStartBtn")).ActualWidth > 0,
            "clean-autostart button layout is visible after opening the parent disclosure; button itself remains untouched");

        settingsScroll.ScrollToTop();
        System.Windows.Input.Keyboard.ClearFocus();
        Pump(350);
        main.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(main);
        var fullPageContent = (StackPanel)settingsScroll.Content;
        var savedBackground = fullPageContent.Background;
        fullPageContent.Background = main.Background;
        int fullWidth = Math.Max(1, (int)Math.Ceiling(fullPageContent.ActualWidth * dpi.DpiScaleX));
        int fullHeight = Math.Max(1, (int)Math.Ceiling(fullPageContent.ActualHeight * dpi.DpiScaleY));
        var fullBitmap = new RenderTargetBitmap(fullWidth, fullHeight, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        fullBitmap.Render(fullPageContent);
        fullPageContent.Background = savedBackground;
        var fullEncoder = new PngBitmapEncoder();
        fullEncoder.Frames.Add(BitmapFrame.Create(fullBitmap));
        string fullScreenshot = Path.Combine(Output, "settings-page-full.png");
        using (var stream = File.Create(fullScreenshot)) fullEncoder.Save(stream);

        var viewportBitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(main.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(main.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        viewportBitmap.Render(main);
        var viewportEncoder = new PngBitmapEncoder();
        viewportEncoder.Frames.Add(BitmapFrame.Create(viewportBitmap));
        string viewportScreenshot = Path.Combine(Output, "settings-page.png");
        using (var stream = File.Create(viewportScreenshot)) viewportEncoder.Save(stream);

        Add(new
        {
            Case = "settings-controls",
            Language = "zh-CN",
            Theme = "light",
            WindowWidth = main.ActualWidth,
            WindowHeight = main.ActualHeight,
            ResourceDictionaryLoaded = main.FindResource("SettingsDeviceCheck") is Style,
            KeepDeviceCount = outputBoxes.Count,
            InputDeviceCount = inputBoxes.Count,
            DeviceSectionCollapsedDefault = deviceSectionCollapseDefault,
            DeviceSectionsOpen = deviceSectionsOpen,
            DeviceSectionsClose = deviceSectionsClose,
            SharedMoreOptionsStyle = sharedMoreOptionsStyle,
            MoreOptionsUsesThemeAccent = moreOptionsUsesThemeAccent,
            OutputSelectAllOff = outputSelectAllOff,
            OutputSelectAllOn = outputSelectAllOn,
            OutputSingleOff = outputSingleOff,
            OutputSingleRestored = outputSingleRestored,
            InputSelectAllOff = inputSelectAllOff,
            InputSelectAllOn = inputSelectAllOn,
            InputSingleOff = inputSingleOff,
            InputSingleRestored = inputSingleRestored,
            NameSaved = nameSaved,
            NameCleared = nameCleared,
            RecentModeDropdownDisabled = recentEnabled,
            LastModeDropdownDisabled = lastEnabled,
            FixedModeDropdownEnabled = fixedEnabled,
            StartMinimizedClicked = startMinimizedClicked,
            StartMinimizedRestored = startMinimizedRestored,
            CleanAutoStartLayoutVisible = cleanAutoStartLayoutVisible,
            StartMinimizedRowHitTestVisible = startMinimized.IsHitTestVisible,
            NoHorizontalOverflowAt780Dip = noHorizontalOverflow,
            SettingsExtentWidth = settingsScroll.ExtentWidth,
            SettingsViewportWidth = settingsScroll.ViewportWidth,
            UntouchedRegistryControls = untouchedDoNotClick,
            Screenshot = viewportScreenshot,
            FullPageScreenshot = fullScreenshot,
            FullPageLogicalHeight = fullPageContent.ActualHeight,
            Note = "Isolated source-copy runtime check. The AutoStart/CleanAutoStart controls were inspected only and never clicked; other system, panel, and audio effect settings were inspected only. ExperimentalMic=true only in isolated test Config to expose input device cards. No real volume or registry changes."
        });
        main.Close();
        Pump(500);
    }

    private static void RunDelayAlignmentCheck()
    {
        Config = new AppConfig { Language = "zh-CN", ThemeMode = "light", Accent = "blue", ShowAppPeakMeter = false };
        Config.Hotkeys = new Dictionary<string, string>();
        typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
        typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
        if (!ConfigService.ConfigPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
            !AutoRuleStore.RulesDir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Delay-check storage isolation missing.");

        L10n.Instance.SetLanguage("zh-CN");
        ThemeService.Apply("light", "blue");
        ThemeService.ApplyBackgroundOpacity(Config.BackgroundOpacity);
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Width = 920, Height = 620, WindowStartupLocation = WindowStartupLocation.Manual };
        var windowAndPointer = ShowWindowAwayFromPointer(main);
        Pump(500);
        ((RadioButton)main.FindName("NavAutomation")).IsChecked = true;
        Pump(300);
        typeof(MainWindow).GetMethod("SetAutomationEditorVisible", PrivateInstance)!.Invoke(main, new object[] { true });

        var step = new AutoRuleStep { Action = AutoRuleAction.LaunchProgram, DelayMs = 125, LaunchMode = 0 };
        var row = (UIElement)typeof(MainWindow).GetMethod("BuildAutoStepRow", PrivateInstance)!.Invoke(main, new object[] { step })!;
        var host = (ItemsControl)main.FindName("AutoStepsHost");
        host.Items.Clear();
        host.Items.Add(row);
        Pump(500);
        System.Windows.Input.Keyboard.ClearFocus();
        Pump(100);

        string delayLabel = L10n.T("Auto.Delay");
        var delay = FindVisualDescendants<TextBox>(row)
            .SingleOrDefault(x => System.Windows.Automation.AutomationProperties.GetName(x) == delayLabel)
            ?? throw new InvalidOperationException("Delay-check could not locate the delay TextBox.");
        var launchMode = FindVisualDescendants<ComboBox>(row)
            .SingleOrDefault(x => ReferenceEquals(x.Tag, step) && x.Items.Count == 2)
            ?? throw new InvalidOperationException("Delay-check could not locate the launch-mode ComboBox.");
        var delayOrigin = delay.TransformToAncestor(main).Transform(new Point(0, 0));
        var launchOrigin = launchMode.TransformToAncestor(main).Transform(new Point(0, 0));
        double delta = Math.Abs(delayOrigin.X - launchOrigin.X);
        bool xAligned = delta <= 0.5;
        bool textLeftAligned = delay.TextAlignment == TextAlignment.Left;
        bool launchInputLeftAligned = launchMode.HorizontalAlignment == HorizontalAlignment.Left;
        if (!xAligned || !textLeftAligned || !launchInputLeftAligned)
            throw new InvalidOperationException($"Delay alignment check failed: delayX={delayOrigin.X:F2}, launchModeX={launchOrigin.X:F2}, delta={delta:F2}, text={delay.TextAlignment}, launchHorizontal={launchMode.HorizontalAlignment}.");

        Add(new
        {
            Case = "delay-alignment",
            Language = "zh-CN",
            Theme = "light",
            WindowWidth = main.ActualWidth,
            WindowHeight = main.ActualHeight,
            WindowLeft = main.Left,
            WindowTop = main.Top,
            PointerOutsideWindow = windowAndPointer.PointerOutside,
            DelayText = delay.Text,
            DelayTextAlignment = delay.TextAlignment.ToString(),
            LaunchModeHorizontalAlignment = launchMode.HorizontalAlignment.ToString(),
            DelayLeftDip = delayOrigin.X,
            LaunchModeLeftDip = launchOrigin.X,
            LeftEdgeDeltaDip = delta,
            LeftEdgesMatch = xAligned,
            StorageIsolated = true,
            Note = "Runtime coordinates measured from the actual automation editor step row. Synthetic LaunchProgram step was rendered only; it was not saved, executed, or launched."
        });
        main.Close();
        Pump(300);
    }

    private static (bool PointerOutside, bool Repositioned) ShowWindowAwayFromPointer(MainWindow main)
    {
        main.Left = 20;
        main.Top = 20;
        main.Show();
        Pump(500);
        if (!GetCursorPos(out var point)) return (true, false);
        bool repositioned = false;
        var dpi = VisualTreeHelper.GetDpi(main);
        double maxLeft = Math.Max(0, SystemParameters.PrimaryScreenWidth - main.ActualWidth - 20);
        double maxTop = Math.Max(0, SystemParameters.PrimaryScreenHeight - main.ActualHeight - 20);
        double screenWidthPx = SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX;
        double screenHeightPx = SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY;
        var candidates = new[]
        {
            (Left: point.X < screenWidthPx / 2 ? maxLeft : 20, Top: point.Y < screenHeightPx / 2 ? maxTop : 20),
            (Left: 20d, Top: 20d),
            (Left: maxLeft, Top: 20d),
            (Left: 20d, Top: maxTop),
            (Left: maxLeft, Top: maxTop)
        };
        foreach (var candidate in candidates)
        {
            var origin = main.PointToScreen(new Point(0, 0));
            double widthPx = main.ActualWidth * dpi.DpiScaleX;
            double heightPx = main.ActualHeight * dpi.DpiScaleY;
            if (point.X < origin.X || point.X >= origin.X + widthPx || point.Y < origin.Y || point.Y >= origin.Y + heightPx)
                return (true, repositioned);
            main.Left = candidate.Left;
            main.Top = candidate.Top;
            repositioned = true;
            Pump(120);
            if (!GetCursorPos(out point)) return (true, repositioned);
        }
        var finalOrigin = main.PointToScreen(new Point(0, 0));
        double finalWidthPx = main.ActualWidth * dpi.DpiScaleX;
        double finalHeightPx = main.ActualHeight * dpi.DpiScaleY;
        bool outside = point.X < finalOrigin.X || point.X >= finalOrigin.X + finalWidthPx ||
            point.Y < finalOrigin.Y || point.Y >= finalOrigin.Y + finalHeightPx;
        return (outside, repositioned);
    }

    private static void ApplyThemeVisibilityDiagnostic(MainWindow main, string hide)
    {
        var page = (FrameworkElement)main.FindName("ThemePage");
        if (hide == "preview")
        {
            var template = (DataTemplate)main.FindResource("ThemePreview");
            var preview = FindVisualDescendants<ContentControl>(page).FirstOrDefault(x => ReferenceEquals(x.ContentTemplate, template));
            if (preview == null) throw new InvalidOperationException("Could not locate the ThemePreview ContentControl.");
            preview.Visibility = Visibility.Collapsed;
            return;
        }
        if (hide == "advanced")
        {
            var panel = (FrameworkElement)main.FindName("ThemeAdvancedPanel");
            DependencyObject? current = panel;
            while (current != null && current.GetType().Name != "AnimatedExpandHost") current = ParentOf(current);
            (current as UIElement ?? panel).Visibility = Visibility.Collapsed;
            return;
        }
        if (hide == "radio")
        {
            var radio = (FrameworkElement)main.FindName("ThemeSystem");
            var parent = FindAncestor<WrapPanel>(radio);
            if (parent == null) throw new InvalidOperationException("Could not locate the theme radio group.");
            parent.Visibility = Visibility.Collapsed;
            return;
        }
        if (hide == "osd")
        {
            var scroll = ((Grid)page).Children.OfType<ScrollViewer>().Single();
            if (scroll.Content is not StackPanel content) throw new InvalidOperationException("Theme page content is missing.");
            var target = (DependencyObject)main.FindName("OsdAdjustBtnTheme");
            var card = content.Children.OfType<FrameworkElement>().FirstOrDefault(child => IsDescendantOf(target, child));
            if (card == null) throw new InvalidOperationException("Could not locate the OSD appearance card.");
            card.Visibility = Visibility.Collapsed;
        }
    }

    private static DependencyObject? ParentOf(DependencyObject value)
    {
        try { return VisualTreeHelper.GetParent(value) ?? LogicalTreeHelper.GetParent(value); }
        catch { return LogicalTreeHelper.GetParent(value); }
    }

    private static T? FindAncestor<T>(DependencyObject value) where T : DependencyObject
    {
        for (var current = ParentOf(value); current != null; current = ParentOf(current))
            if (current is T match) return match;
        return null;
    }

    private static bool IsDescendantOf(DependencyObject value, DependencyObject ancestor)
    {
        for (var current = value; current != null; current = ParentOf(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count;
        try { count = VisualTreeHelper.GetChildrenCount(root); }
        catch { yield break; }
        for (int i = 0; i < count; i++)
        {
            DependencyObject child;
            try { child = VisualTreeHelper.GetChild(root, i); }
            catch { continue; }
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static double SystemCpuPercent(NativeFileTime previousIdle, NativeFileTime previousKernel, NativeFileTime previousUser,
        out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user)
    {
        if (!GetSystemTimes(out idle, out kernel, out user)) return -1;
        ulong idleDelta = ToUInt64(idle) - ToUInt64(previousIdle);
        ulong totalDelta = (ToUInt64(kernel) - ToUInt64(previousKernel)) + (ToUInt64(user) - ToUInt64(previousUser));
        return totalDelta == 0 ? -1 : 100.0 * (totalDelta - idleDelta) / totalDelta;
    }

    private static ulong ToUInt64(NativeFileTime value) => ((ulong)value.High << 32) | value.Low;

    private static void RunPeakUi()
    {
        var panel = new QuickPanelModernWindow { ShowActivated = false, ShowInTaskbar = false };
        panel.Show();
        var load = (Task)typeof(QuickPanelModernWindow).GetMethod("LoadAsync", PrivateInstance)!.Invoke(panel, null)!;
        var timeout = Stopwatch.StartNew();
        while (!load.IsCompleted && timeout.ElapsedMilliseconds < 10000) Pump(10);
        if (!load.IsCompleted) throw new TimeoutException("Peak test panel load timeout");
        load.GetAwaiter().GetResult();
        if (!(bool)typeof(QuickPanelModernWindow).GetField("_contentReady", PrivateInstance)!.GetValue(panel)!)
            throw new InvalidOperationException("Peak test panel failed to load");
        var rows = (System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_rows", PrivateInstance)!.GetValue(panel)!;
        var pids = rows.Keys.Cast<int>().ToArray();
        if (pids.Length == 0) throw new InvalidOperationException("No app rows for peak test");
        typeof(QuickPanelModernWindow).GetMethod("StopMeter", PrivateInstance)!.Invoke(panel, null);
        var publish = (Action<IReadOnlyDictionary<int, float>>)Delegate.CreateDelegate(typeof(Action<IReadOnlyDictionary<int, float>>), panel,
            typeof(QuickPanelModernWindow).GetMethod("OnPeaksUpdated", PrivateInstance)!);
        int warmCalls = 0;
        var warm = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
        warm.Tick += (_, _) => publish(PeakSnapshot(pids, ++warmCalls, pids.Length));
        warm.Start(); Pump(8000); warm.Stop(); Pump(500);
        foreach (int changing in new[] { 0, 1, pids.Length, 0 })
        {
            int calls = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (_, _) => publish(PeakSnapshot(pids, ++calls, changing));
            publish(PeakSnapshot(pids, 0, changing)); Pump(300);
            var process = Process.GetCurrentProcess(); process.Refresh();
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            long allocated = Allocated();
            var watch = Stopwatch.StartNew();
            if (changing > 0) timer.Start();
            Pump(6000); timer.Stop();
            process.Refresh();
            double wall = watch.Elapsed.TotalMilliseconds;
            double used = process.TotalProcessorTime.TotalMilliseconds - cpu;
            Add(new { Case = "synthetic-peak-ui", AppRows = pids.Length, ChangingRows = changing, ActualSnapshots = calls,
                ActualHz = calls * 1000.0 / wall, WallMs = wall, CpuMs = used,
                MachineCpuPercent = 100 * used / wall / Environment.ProcessorCount,
                UiThreadAllocatedBytes = allocated < 0 ? -1 : Allocated() - allocated,
                Note = "Native meter stopped. Synthetic snapshots exercise actual peak coalescing and ScaleTransform UI; no audio played." });
        }
        panel.Close(); Pump(300);
    }

    private static IReadOnlyDictionary<int, float> PeakSnapshot(int[] pids, int tick, int changing)
    {
        var snapshot = new Dictionary<int, float>();
        for (int i = 0; i < pids.Length; i++) snapshot[pids[i]] = i < changing ? (float)(0.45 + 0.4 * Math.Sin(tick * 0.33 + i)) : 0f;
        return snapshot;
    }

    private static void RunPersistenceCheck()
    {
        var rule = new AutoRule { Id = "perf-rename-failure-id", Name = "Perf rename before", Enabled = false };
        AutoRuleStore.Save(rule);
        var oldFile = Path.Combine(AutoRuleStore.RulesDir, rule.Name + ".json");
        if (!File.Exists(oldFile)) throw new InvalidOperationException("Original test rule was not saved");
        rule.Name = "Perf rename after";
        var target = Path.Combine(AutoRuleStore.RulesDir, rule.Name + ".json");
        Directory.CreateDirectory(target); // Deliberately block the new file path, only within isolated test-data.
        string? failure = null;
        try { AutoRuleStore.Save(rule); }
        catch (Exception e) { failure = e.GetType().Name; }
        bool exists = AutoRuleStore.LoadAll().Any(r => r.Id == rule.Id);
        Add(new { Case = "automation-rename-write-failure", Failure = failure, OldFileExists = File.Exists(oldFile),
            RuleStillOnDisk = exists, TargetBlockedByTestDirectory = Directory.Exists(target),
            Note = "Controlled failure in redirected test-data only. No user rules touched." });
        typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
    }

    private static List<AutoRule> SyntheticRules(int count)
    {
        return Enumerable.Range(0, count).Select(i => new AutoRule
        {
            Id = "perf-rule-" + i.ToString("D3"), Name = "Synthetic " + i, Enabled = false,
            Actions = Enumerable.Range(0, 8).Select(j => new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdTitle = "Test", OsdText = "Test" }).ToList()
        }).ToList();
    }

    private static void RunSupplementalCore()
    {
        var getVolume = typeof(SystemVolumeService).GetMethod("GetVolume", PrivateStatic)!;
        Action combined = () =>
        {
            var endpoint = (IAudioEndpointVolume?)getVolume.Invoke(null, new object?[] { null });
            if (endpoint == null) throw new InvalidOperationException("No default endpoint for read test");
            try
            {
                if (endpoint.GetMasterVolumeLevelScalar(out float _) < 0 || endpoint.GetMute(out int _) < 0)
                    throw new InvalidOperationException("Combined read failed");
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(endpoint); }
        };
        Measure("device-volume-and-mute-separate-recheck", 100, () => { SystemVolumeService.GetVolumePercent(); SystemVolumeService.IsMuted(); });
        Measure("device-volume-and-mute-combined-prototype", 100, combined);
        var rules = SyntheticRules(100);
        Directory.CreateDirectory(AutoRuleStore.RulesDir);
        foreach (var rule in rules)
            File.WriteAllText(Path.Combine(AutoRuleStore.RulesDir, rule.Id + ".json"), JsonSerializer.Serialize(rule, JsonOptions));
        Measure("automation-save-among-100-rules", 30, () => AutoRuleStore.Save(rules[0]));
        typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
        Add(new { Case = "supplemental-prototype-limits", Note = "Combined endpoint read is a test-only prototype via reflection; production unchanged. 100 disabled synthetic rules stored only under redirected test-data." });
    }

    private static void RunCore()
    {
        var apps = AudioService.GetApps(true);
        var outputs = AudioService.GetDevices(EDataFlow.eRender);
        var inputs = AudioService.GetDevices(EDataFlow.eCapture);
        Add(new { Case = "audio-environment", Applications = apps.Count, NamedApplications = apps.Count(a => !string.IsNullOrWhiteSpace(a.ProcessName)), ActiveApplications = apps.Count(a => a.HasActiveSession), Outputs = outputs.Count, Inputs = inputs.Count });
        Measure("apps-fresh", 20, () => AudioService.GetApps(true));
        Measure("apps-cached", 1000, () => AudioService.GetApps());
        Measure("output-devices-fresh", 20, () => { typeof(AudioService).GetField("_deviceCacheRenderAt", PrivateStatic)!.SetValue(null, 0L); AudioService.GetDevices(EDataFlow.eRender); });
        Measure("output-devices-cached", 1000, () => AudioService.GetDevices(EDataFlow.eRender));
        Measure("session-refresh", 20, () => SessionVolumeService.Refresh(true));
        if (apps.Count > 0)
        {
            int pid = (int)apps[0].ProcessId;
            SessionVolumeService.Refresh(true);
            Measure("app-volume-and-mute", 400, () => { SessionVolumeService.GetVolumePercent(pid); SessionVolumeService.IsMuted(pid); });
        }
        Measure("device-volume-and-mute", 100, () => { SystemVolumeService.GetVolumePercent(); SystemVolumeService.IsMuted(); });
        Measure("global-microphone-state", 40, () => GlobalMicMuteService.IsAnyMuted(true));
        for (int i = 0; i < 30; i++) Config.AppNames["synthetic-app-" + i] = "Synthetic application " + i;
        for (int i = 0; i < 20; i++) Config.DeviceNames["synthetic-device-" + i] = "Synthetic device " + i;
        Measure("config-json-new-options", 600, () => JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        Measure("config-json-shared-options", 600, () => JsonSerializer.Serialize(Config, JsonOptions));
        Measure("config-save-isolated-file", 150, () => ConfigService.Save(Config));
        var rules = Enumerable.Range(0, 100).Select(i => new AutoRule { Id = Guid.NewGuid().ToString("N"), Name = "Synthetic " + i }).ToList();
        foreach (var rule in rules) rule.Actions = Enumerable.Range(0, 8).Select(i => new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdTitle = "Test", OsdText = "Test" }).ToList();
        Measure("automation-signature-100-rules-800-steps", 100, () => JsonSerializer.Serialize(rules));
        RunMeter("meter-no-targets", new int[0], 6);
        var targets = apps.Where(a => SessionVolumeService.HasSession((int)a.ProcessId) && !SessionVolumeService.IsMuted((int)a.ProcessId))
            .Select(a => (int)a.ProcessId).Distinct().ToArray();
        if (targets.Length > 0)
        {
            RunMeter("meter-one-target", targets.Take(1).ToArray(), 6);
            RunMeter("meter-all-eligible-targets", targets, 6);
        }
    }

    private static void RunMeter(string name, int[] targets, int seconds)
    {
        int notifications = 0;
        float maxPeak = 0;
        using (var meter = new AudioMeterService())
        {
            meter.PeaksUpdated += peaks => { Interlocked.Increment(ref notifications); foreach (float value in peaks.Values) if (value > maxPeak) maxPeak = value; };
            meter.Start(targets);
            Thread.Sleep(300);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            var watch = Stopwatch.StartNew();
            Thread.Sleep(seconds * 1000);
            process.Refresh();
            double elapsed = watch.Elapsed.TotalMilliseconds;
            double used = process.TotalProcessorTime.TotalMilliseconds - cpu;
            var stop = Stopwatch.StartNew();
            meter.Stop();
            stop.Stop();
            Add(new { Case = name, Targets = targets.Length, DurationMs = elapsed, CpuMs = used,
                MachineCpuPercent = 100 * used / elapsed / Environment.ProcessorCount, Notifications = notifications,
                MaximumPublishedPeak = maxPeak, StopMs = stop.Elapsed.TotalMilliseconds });
        }
    }

    private static void RunUi()
    {
        var osdType = typeof(App).Assembly.GetType("SonicRoute.OsdService", true)!;
        var showMethod = osdType.GetMethod("ShowVolume", PrivateInstance)!;
        var updateMethod = osdType.GetMethod("NotifyVolumeVisualChanged", PrivateInstance)!;
        var service = Activator.CreateInstance(osdType, true)!;
        var show = (Action<string, int, string, bool, string>)Delegate.CreateDelegate(typeof(Action<string, int, string, bool, string>), service, showMethod);
        var notify = (Action)Delegate.CreateDelegate(typeof(Action), service, updateMethod);
        Config.OsdVolumeVisualEnabled = false;
        double start = Clock.Elapsed.TotalMilliseconds;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> diagnostic = (_, e) =>
        {
            if ((e.Exception.StackTrace ?? "").Contains("OsdService")) Console.Error.WriteLine("OSD first-chance: " + e.Exception);
        };
        AppDomain.CurrentDomain.FirstChanceException += diagnostic;
        show("SonicRoute performance test", 50, "50%", false, "test");
        AppDomain.CurrentDomain.FirstChanceException -= diagnostic;
        double firstShowSynchronousMs = Clock.Elapsed.TotalMilliseconds - start;
        var firstVisible = ((Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service))?.IsVisible;
        Pump(300);
        var window = (Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service);
        if (window == null || !window.IsVisible)
        {
            osdType.GetMethod("EnsureWindow", PrivateInstance)!.Invoke(service, null);
            osdType.GetMethod("ApplyConfig", PrivateInstance)!.Invoke(service, new object[] { Config });
            osdType.GetMethod("ApplyNotice", PrivateInstance)!.Invoke(service, new object[] { Config, false });
            osdType.GetMethod("ApplyMetrics", PrivateInstance)!.Invoke(service, null);
            throw new InvalidOperationException("OSD was not displayed; diagnostic private calls completed; immediate visibility=" + firstVisible + "; synchronous milliseconds=" + firstShowSynchronousMs);
        }
        Add(new { Case = "osd-first-show", SynchronousMs = firstShowSynchronousMs, ImmediatelyVisible = firstVisible, WallToSettledMs = Clock.Elapsed.TotalMilliseconds - start, FixedPumpMs = 300 });
        foreach (bool enabled in new[] { false, true })
        {
            Config.OsdVolumeVisualEnabled = enabled;
            notify();
            int warmCalls = 0;
            var warmTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            warmTimer.Tick += (_, _) => { int volume = (warmCalls++ * 4) % 101; show("SonicRoute performance test", volume, volume + "%", false, "test"); };
            warmTimer.Start(); Pump(6000); warmTimer.Stop();
        }
        Add(new { Case = "osd-warmup", WarmupMs = 12000, Note = "Both variants fed changing values before timed comparisons to reduce startup/JIT bias." });
        foreach (int hz in new[] { 30, 100 })
        {
            foreach (bool enabled in new[] { false, true, true, false })
            {
                Config.OsdVolumeVisualEnabled = enabled;
                notify();
                show("SonicRoute performance test", 50, "50%", false, "test");
                Pump(250);
                int calls = 0;
                var durations = new List<double>();
                var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1000.0 / hz) };
                timer.Tick += (_, _) => {
                    int volume = (calls++ * 4) % 101;
                    var before = Clock.Elapsed.TotalMilliseconds;
                    show("SonicRoute performance test", volume, volume + "%", false, "test");
                    durations.Add(Clock.Elapsed.TotalMilliseconds - before);
                };
                var process = Process.GetCurrentProcess();
                process.Refresh();
                double cpu = process.TotalProcessorTime.TotalMilliseconds;
                long allocation = Allocated();
                int gen0 = GC.CollectionCount(0);
                var watch = Stopwatch.StartNew();
                timer.Start();
                Pump(6000);
                timer.Stop();
                process.Refresh();
                double elapsed = watch.Elapsed.TotalMilliseconds;
                double used = process.TotalProcessorTime.TotalMilliseconds - cpu;
                Add(new { Case = enabled ? "osd-bar-on" : "osd-bar-off", RequestedHz = hz, ActualCalls = calls,
                    ActualHz = 1000.0 * calls / elapsed, WallMs = elapsed, CpuMs = used,
                    MachineCpuPercent = 100 * used / elapsed / Environment.ProcessorCount,
                    UiThreadAllocatedBytes = allocation < 0 ? -1 : Allocated() - allocation,
                    HandlerP95Ms = Percentile(durations, 0.95), Gen0Collections = GC.CollectionCount(0) - gen0,
                    WorkingSetMiB = process.WorkingSet64 / 1048576.0, PrivateMiB = process.PrivateMemorySize64 / 1048576.0 });
            }
        }
        Pump(1800);
        MeasureIdle("osd-hidden-idle", 5000);
        var hiddenWindow = (Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service);
        Add(new { Case = "osd-hidden-state", IsVisible = hiddenWindow?.IsVisible, PendingRender = osdType.GetField("_barUpdatePending", PrivateInstance)!.GetValue(service) });
        ((IDisposable)service).Dispose();
        RunWindows();
    }

    private static void RunWindows(bool supplemental = false)
    {
        MainWindow? main = null;
        var before = Clock.Elapsed.TotalMilliseconds;
        main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        Add(new { Case = "main-window-constructor-first", WallMs = Clock.Elapsed.TotalMilliseconds - before });
        main.Show();
        Pump(1000);
        foreach (string tag in new[] { "Settings", "Automation", "Theme", "Hotkeys", "Settings", "Automation" })
        {
            var nav = typeof(MainWindow).GetMethod("Nav_Checked", PrivateInstance)!;
            var watch = Stopwatch.StartNew();
            nav.Invoke(main, new object[] { new RadioButton { Tag = tag }, new RoutedEventArgs() });
            double synchronous = watch.Elapsed.TotalMilliseconds;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Add(new { Case = "navigation", Page = tag, SynchronousMs = synchronous, ToDispatcherIdleMs = watch.Elapsed.TotalMilliseconds });
            Pump(350);
        }
        if (supplemental)
        {
            var items = (ItemsControl)main.FindName("AutoRuleList");
            foreach (int count in new[] { 0, 20, 100, 100 })
            {
                typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, SyntheticRules(count));
                long allocated = Allocated();
                var watch = Stopwatch.StartNew();
                var task = (Task)typeof(MainWindow).GetMethod("RefreshAutoRulesAsync", PrivateInstance)!.Invoke(main, null)!;
                while (!task.IsCompleted && watch.ElapsedMilliseconds < 10000) Pump(10);
                if (!task.IsCompleted) throw new TimeoutException("Synthetic automation UI timeout");
                task.GetAwaiter().GetResult();
                double completionMs = watch.Elapsed.TotalMilliseconds;
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                if (items.Items.Count != count) throw new InvalidOperationException("Synthetic rules UI did not build expected count");
                Add(new { Case = "automation-list-build", Rules = count, Steps = count * 8, CompletionMs = completionMs,
                    ToDispatcherIdleMs = watch.Elapsed.TotalMilliseconds, UiThreadAllocatedBytes = allocated < 0 ? -1 : Allocated() - allocated,
                    MaterializedRows = items.Items.Count });
            }
            typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
        }
        main.Close();
        Pump(500);
        var created = new List<WeakReference>();
        for (int i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            var panel = new QuickPanelModernWindow { ShowActivated = false, ShowInTaskbar = false };
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> diagnostic = (_, e) =>
            {
                if ((e.Exception.StackTrace ?? "").Contains("QuickPanelModernWindow")) Console.Error.WriteLine("Panel first-chance: " + e.Exception);
            };
            AppDomain.CurrentDomain.FirstChanceException += diagnostic;
            panel.Show();
            var load = (Task)typeof(QuickPanelModernWindow).GetMethod("LoadAsync", PrivateInstance)!.Invoke(panel, null)!;
            var timeout = Stopwatch.StartNew();
            while (!load.IsCompleted && timeout.ElapsedMilliseconds < 8000) Pump(10);
            if (!load.IsCompleted) throw new TimeoutException("Panel load timeout");
            load.GetAwaiter().GetResult();
            AppDomain.CurrentDomain.FirstChanceException -= diagnostic;
            bool contentReady = (bool)typeof(QuickPanelModernWindow).GetField("_contentReady", PrivateInstance)!.GetValue(panel)!;
            if (!contentReady) throw new InvalidOperationException("Panel content failed to load; exclude this run from performance comparisons");
            Add(new { Case = "quick-panel-load", Iteration = i, WallMs = watch.Elapsed.TotalMilliseconds,
                AppRows = ((System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_rows", PrivateInstance)!.GetValue(panel)!).Count,
                NamedApplications = AudioService.GetApps().Count(a => !string.IsNullOrWhiteSpace(a.ProcessName)),
                ContentReady = contentReady,
                IsClosed = typeof(QuickPanelModernWindow).GetField("_isClosed", PrivateInstance)!.GetValue(panel) });
            Pump(300);
            created.Add(new WeakReference(panel));
            panel.Close();
        }
        Pump(500);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Add(new { Case = "closed-panels-retention", Created = created.Count, AliveAfterForcedGc = created.Count(w => w.IsAlive),
            Note = "Last local can remain JIT-rooted; small sample is not a long-run leak test." });
        var process = Process.GetCurrentProcess();
        process.Refresh();
        double privateBefore = process.PrivateMemorySize64 / 1048576.0;
        double workingBefore = process.WorkingSet64 / 1048576.0;
        var gcWatch = Stopwatch.StartNew();
        typeof(App).GetMethod("GcNow", PrivateStatic)!.Invoke(null, null);
        double gcMs = gcWatch.Elapsed.TotalMilliseconds;
        gcWatch.Restart();
        typeof(App).GetMethod("TrimWorkingSet", PrivateStatic)!.Invoke(null, null);
        double trimMs = gcWatch.Elapsed.TotalMilliseconds;
        process.Refresh();
        Add(new { Case = "forced-gc-and-trim-after-close", GcMs = gcMs, TrimMs = trimMs,
            WorkingSetBeforeMiB = workingBefore, WorkingSetAfterMiB = process.WorkingSet64 / 1048576.0,
            PrivateBeforeMiB = privateBefore, PrivateAfterMiB = process.PrivateMemorySize64 / 1048576.0,
            Note = "Owned test process only. Already collected for retention, so this is a warmed lower-workload GC case." });
    }

    private static void MeasureIdle(string name, int milliseconds)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        double cpu = process.TotalProcessorTime.TotalMilliseconds;
        var watch = Stopwatch.StartNew();
        Pump(milliseconds);
        process.Refresh();
        double elapsed = watch.Elapsed.TotalMilliseconds;
        double used = process.TotalProcessorTime.TotalMilliseconds - cpu;
        Add(new { Case = name, WallMs = elapsed, CpuMs = used, MachineCpuPercent = 100 * used / elapsed / Environment.ProcessorCount });
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Measure(string name, int count, Action action)
    {
        action();
        var times = new List<double>(count);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        double cpu = process.TotalProcessorTime.TotalMilliseconds;
        long allocated = Allocated();
        int gen0 = GC.CollectionCount(0);
        for (int i = 0; i < count; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            times.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }
        process.Refresh();
        Add(new { Case = name, Operations = count, MeanMs = times.Average(), MedianMs = Percentile(times, 0.5),
            P95Ms = Percentile(times, 0.95), MaxMs = times.Max(), CpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu,
            AllocatedBytesPerOperation = allocated < 0 ? -1 : (Allocated() - allocated) / (double)count,
            Gen0Collections = GC.CollectionCount(0) - gen0 });
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * p) - 1)];
    }
    private static void Add(object row)
    {
        Results.Add(row);
        Console.WriteLine(JsonSerializer.Serialize(row));
        Save();
    }
    private static void Save() => File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(Results, JsonOptions));
}
