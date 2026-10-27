using System.Collections;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.Core.Interop;

internal static partial class Program
{
    private sealed class ProbeApp : App
    {
        // Application queues startup even when a test only pumps DispatcherFrame.
        // Suppress product startup/exit as well as avoiding App.Run.
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;
    private static void Check(bool value, string text)
    { if (!value) throw new Exception(text); _checks++; Console.WriteLine("PASS " + text); }
    private static void Call(object value, string method) => value.GetType().GetMethod(method, Instance)!.Invoke(value, null);
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Instance)!.GetValue(value)!;
    private static ResourceDictionary ProductResources()
    {
        // Reuse the product resource declarations without constructing its Application BAML root.
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProductApp.xaml")!;
        var document = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = document.Root!.Element(presentation + "Application.Resources")!.Elements().Single();
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        dictionary.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:SonicRoute;assembly=SonicRoute");
        foreach (var element in dictionary.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:SonicRoute"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:SonicRoute;assembly=SonicRoute");
        foreach (var source in dictionary.Descendants(presentation + "ResourceDictionary").Attributes("Source"))
            source.Value = "/SonicRoute;component/" + source.Value;
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
    }
    private static void Finish(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void CheckRows(QuickPanelModernWindow panel, string mode)
    {
        var config = ConfigService.Load();
        config.HiddenPanelApps = new List<string> { "HIDDENTARGET" };
        var apps = new List<AudioAppInfo>
        {
            new() { ProcessId = int.MaxValue - 10, ProcessName = "HiddenTarget", DisplayName = "Hidden" },
            new() { ProcessId = int.MaxValue - 11, ProcessName = "PanelMissingA", DisplayName = "A" },
            new() { ProcessId = int.MaxValue - 12, ProcessName = "PanelMissingB", DisplayName = "B" }
        };
        void Build() => Finish((Task)panel.GetType().GetMethod("BuildAppRowsAsync", Instance)!.Invoke(panel, new object[] { apps, config })!);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> trace = (_, e) => faults.Enqueue(e.Exception.GetType().Name + ": " + e.Exception.Message);
        AppDomain.CurrentDomain.FirstChanceException += trace;
        try { Build(); }
        catch { Console.WriteLine("DIAGNOSTIC " + string.Join(" | ", faults.Take(8))); throw; }
        finally { AppDomain.CurrentDomain.FirstChanceException -= trace; }
        var rows = Field<IDictionary>(panel, "_rows");
        if (rows.Count != 2) Console.WriteLine("DIAGNOSTIC " + string.Join(" | ", faults.Take(8)));
        Check(rows.Count == 2 && !rows.Contains(int.MaxValue - 10), mode + " case-insensitive hidden filtering retains visible rows (count=" + rows.Count + ")");
        object first = rows[int.MaxValue - 11]!;
        Build();
        Check(ReferenceEquals(first, rows[int.MaxValue - 11]), mode + " unchanged list reuses existing controls");
        var batch = panel.GetType().GetMethod("ApplyAllRowsMutedVisual", Instance)!;
        foreach (bool muted in new[] { true, false })
        {
            batch.Invoke(panel, new object[] { muted });
            Check(rows.Values.Cast<object>().All(row => Field<bool>(row, "IsMuted") == muted
                && Field<Border>(row, "MuteDot").Visibility == (muted ? Visibility.Visible : Visibility.Collapsed)),
                mode + " batch mute updates every row visual: " + muted);
            Check(!Field<bool>(panel, "_updatingRowVolumes"), mode + " batch mute restores existing update guard: " + muted);
        }
        Check(Field<Dictionary<int, int>>(panel, "_pendingVolume").Count == 0, mode + " readback and visual batches queue no volume writes");
        config.HiddenPanelApps = null!;
        Build();
        Check(rows.Count == 3, mode + " older null hidden list is treated as empty");
        var muteReadback = panel.GetType().GetMethod("ApplyAppMuteReadback", Instance)!;
        muteReadback.Invoke(panel, new object[] { int.MaxValue - 11, true });
        Check(Field<bool>(rows[int.MaxValue - 11]!, "IsMuted") && !Field<bool>(rows[int.MaxValue - 12]!, "IsMuted"),
            mode + " single-app mute readback changes only its target row");
        muteReadback.Invoke(panel, new object[] { int.MaxValue - 11, false });
        Check(!Field<bool>(rows[int.MaxValue - 11]!, "IsMuted") && Field<Dictionary<int, int>>(panel, "_pendingVolume").Count == 0,
            mode + " single-app unmute readback queues no audio writes");
    }

    private static void CheckDevices(QuickPanelModernWindow panel, string mode)
    {
        var config = ConfigService.Load();
        config.DeviceNames["panel-test-custom"] = "自定义 Output";
        var raw = new List<AudioDeviceInfo>
        {
            new() { Id = "panel-test-original", DisplayName = "Original", Flow = EDataFlow.eRender },
            new() { Id = "panel-test-custom", DisplayName = "Raw", Flow = EDataFlow.eRender }
        };
        var display = (List<AudioDeviceInfo>)panel.GetType().GetMethod("DisplayDevices", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { raw, config })!;
        var perApp = PanelDevices.WithSystemDefault(display, EDataFlow.eRender, config);
        var bind = panel.GetType().GetMethod("SetSystemDevices", Instance)!;
        var combo = (ComboBox)panel.FindName("SystemDevCombo");
        int selections = 0;
        bool guarded = true;
        combo.SelectionChanged += (_, _) => { selections++; guarded &= Field<bool>(panel, "_suppressDevCombo"); };
        bind.Invoke(panel, new object[] { display, "panel-test-custom" });
        Check(ReferenceEquals(combo.ItemsSource, display) && ReferenceEquals(perApp[2], display[1])
            && display[1].DisplayName == "自定义 Output" && raw[1].DisplayName == "Raw",
            mode + " top selector and application devices reuse one custom-name projection");
        Check(ReferenceEquals(combo.SelectedItem, display[1]) && display[1].IsDefault && !display[0].IsDefault,
            mode + " retained device default and selection match");
        Check(selections > 0 && guarded && !Field<bool>(panel, "_suppressDevCombo"),
            mode + " programmatic device selection suppresses audio actions and releases guard");
        bind.Invoke(panel, new object[] { new List<AudioDeviceInfo>(), "missing-device" });
        Check(combo.Items.Count == 0 && combo.SelectedItem == null && Field<string>(panel, "_systemDeviceId") == "missing-device",
            mode + " replacing devices removes stale selection and retains missing-default fallback");
        bind.Invoke(panel, new object?[] { display, null });
        Check(combo.SelectedItem == null && Field<string?>(panel, "_systemDeviceId") == null,
            mode + " missing default does not select an unrelated endpoint");
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--output-popup-live"))
        {
            AttachOutputTestDesktop();
            Directory.CreateDirectory(args.Last());
            var liveLog = new StreamWriter(Path.Combine(args.Last(), "console.log")) { AutoFlush = true };
            Console.SetOut(liveLog); Console.SetError(liveLog);
        }
        var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config.json"), "{\"QuickPanelStyle\":\"classic\",\"QuickPanelHeight\":610,\"ShowAppPeakMeter\":false,\"ExperimentalMic\":false}");
        var windows = new List<Window>();
        App? app = null;
        try
        {
            // No App.Run/Startup or Window.Show: do not register hotkeys, run automation,
            // start audio discovery, or write actual endpoint state in this BAML/lifetime check.
            app = new ProbeApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (args.Contains("--tray-hit-only"))
            {
                CheckNativeTrayHit();
                Console.WriteLine($"RESULT passed={_checks} runtime={Environment.Version} ACTUAL_TRAY_ICON=true INPUT_INJECTED=false AUDIO_WRITES=NONE");
                return 0;
            }
            app.Resources = ProductResources();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var assembly = typeof(App).Assembly;
            Check(assembly.GetType("SonicRoute.QuickPanelWindow") == null && assembly.GetType("SonicRoute.IQuickPanel") == null,
                "removed panel and two-panel abstraction absent from actual product assembly");
            using (var resources = new ResourceReader(assembly.GetManifestResourceStream("SonicRoute.g.resources")!))
                Check(!resources.Cast<DictionaryEntry>().Any(entry => ((string)entry.Key).Equals("quickpanelwindow.baml", StringComparison.OrdinalIgnoreCase)),
                    "removed panel BAML absent from executable resources");
            Check(typeof(AppConfig).GetProperty("QuickPanelStyle") == null && ConfigService.Load().QuickPanelHeight == 610,
                "obsolete classic preference ignored without losing current panel settings");
            Check(typeof(App).GetProperty("QuickPanelInstance")!.PropertyType == typeof(QuickPanelModernWindow),
                "application exposes only the remaining quick panel");
            var theme = assembly.GetType("SonicRoute.ThemeService")!.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(string) }, null)!;
            if (args.Contains("--output-popup-live")) { CheckLiveOutputPopup(theme, windows, args.Last(), args.Contains("--output-followup-live"), args.Contains("--output-optimization-live")); Console.WriteLine($"RESULT passed={_checks} PRIVATE_DESKTOP=true AUDIO_READS_ONLY=true INPUT_INJECTED=false"); return 0; }
            if (args.Contains("--output-volumes")) { CheckOutputVolumes(theme, windows, args.Last()); Console.WriteLine($"RESULT passed={_checks} OUTPUT_VOLUME_UI_ONLY=true AUDIO_WRITES=NONE"); return 0; }
            if (args.Contains("--output-followup")) { CheckOutputFollowup(theme, windows, args.Last()); Console.WriteLine($"RESULT passed={_checks} OUTPUT_FOLLOWUP_UI_ONLY=true AUDIO_WRITES=NONE"); return 0; }
            if (args.Contains("--app-cards")) { CheckAppCards(theme, windows, args.Last()); Console.WriteLine($"RESULT passed={_checks} APP_CARDS_UI_ONLY=true AUDIO_WRITES=NONE"); return 0; }
            if (args.Contains("--hotkeys-only")) { CheckDeviceHotkeysAndTraySettings(theme, windows); Console.WriteLine($"RESULT passed={_checks} HOTKEY_TRAY_SETTINGS_UI_ONLY=true AUDIO_WRITES=NONE"); return 0; }
            if (args.Contains("--measure-sections")) { MeasureSections(windows, args); return 0; }
            if (args.Contains("--render-scrollbars")) { RenderScrollbars(theme, args.Last()); return 0; }
            if (args.Contains("--app-scroll-layout")) { CheckAppScrollLayout(theme, windows, args.Last()); Console.WriteLine($"RESULT passed={_checks} APP_SCROLL_LAYOUT_ONLY=true WINDOWS_SHOWN=false AUDIO_WRITES=NONE"); return 0; }
            if (args.Contains("--output-optimization")) { CheckOutputOptimization(theme, windows); Console.WriteLine($"RESULT passed={_checks} OUTPUT_OPTIMIZATION_ONLY=true WINDOWS_SHOWN=false AUDIO_WRITES=NONE"); return 0; }
            if (args.Contains("--overview-mute")) { CheckOverviewMute(theme, windows, args.Last()); CheckDeviceHotkeysAndTraySettings(theme, windows); Console.WriteLine($"RESULT passed={_checks} OVERVIEW_MUTE_ONLY=true WINDOWS_SHOWN=false AUDIO_WRITES=FAKES_ONLY"); return 0; }
            foreach (string mode in new[] { "light", "dark" })
            {
                theme.Invoke(null, new object[] { mode, "blue" });
                CheckScrollControls(mode);
                var window = new MainWindow(); windows.Add(window);
                Call(window, "EnsureSettingsPage");
                Call(window, "EnsureThemePage");
                Call(window, "LoadTheme");
                Check(Field<Grid>(window, "SettingsPage") != null && Field<Grid>(window, "ThemePage") != null,
                    mode + " settings and theme BAML/deferred references initialize");
                Check(Field<CheckBox>(window, "SettingsPanelChangeSysDef") != null
                    && Field<Slider>(window, "PanelHeightSlider").Value == 610
                    && Field<CheckBox>(window, "AppPeakMeterCheck") != null,
                    mode + " retained panel options and height initialize");
                Check(window.GetType().GetField("QuickPanelStyleCombo", Instance) == null,
                    mode + " removed style selector has no retained control field");
                CheckSettingsSections(window, mode, directory);
                window.Close();
                Check(Field<Grid?>(window, "SettingsPage") == null && Field<Grid?>(window, "ThemePage") == null,
                    mode + " close releases both deferred pages");
                var panel = new QuickPanelModernWindow(); windows.Add(panel);
                panel.SetMeterEnabled(false);
                Check(panel.Height >= 350 && panel.Height <= 610, mode + " remaining panel initializes from old config");
                CheckDevices(panel, mode);
                CheckRows(panel, mode);
                var panelScroll = (ScrollViewer)panel.FindName("AppListScroll");
                panelScroll.ApplyTemplate();
                var panelBar = (System.Windows.Controls.Primitives.ScrollBar)panelScroll.Template.FindName("PART_VerticalScrollBar", panelScroll);
                Check(ReferenceEquals(panelBar.Style, app.FindResource(typeof(System.Windows.Controls.Primitives.ScrollBar))),
                    mode + " actual quick panel scrollbar inherits shared style");
                panel.Close();
                Check(Field<bool>(panel, "_isClosed"), mode + " remaining panel closes through existing cleanup");
                Check(Field<object?>(panel, "_meter") == null, mode + " closed panel has no meter worker");
                Check(((ComboBox)panel.FindName("SystemDevCombo")).ItemsSource == null,
                    mode + " close releases the reused device list");
            }
            Console.WriteLine($"RESULT passed={_checks} runtime={Environment.Version} ACTUAL_PRODUCT_BAML=true APP_STARTUP=false WINDOWS_SHOWN=false AUDIO_WRITES=0");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (var window in windows) try { window.Close(); } catch { }
            AutoRuleStore.Shutdown();
            // Do not call App.Shutdown: its product OnExit path belongs to normal App startup.
        }
    }
}
