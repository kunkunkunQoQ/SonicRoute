using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private sealed class RoutingFixtureApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }
    private static int _checks;
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Trace.Listeners.Add(new TextWriterTraceListener(Console.Error)); Trace.AutoFlush = true;
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", Path.Combine(output, "ui-data-" + Guid.NewGuid().ToString("N")));
            Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID", "AndroidUiChecks");
            App app = args.Contains("--panel-stream-settings") || args.Contains("--device-audio") || args.Contains("--speaker-routing") || args.Contains("--phone-pages") || args.Contains("--phone-clipboard") || args.Contains("--settings-localization") ? new RoutingFixtureApp() : new App();
            if (app is RoutingFixtureApp)
            { app.Resources = ProductResources(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
            else app.InitializeComponent();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            L10n.Instance.SetLanguage("zh-CN"); ThemeService.Apply("light", "#00CBE9");
            if (args.Contains("--panel-stream-settings")) return PanelStreamSettingsUi(output);
            if (args.Contains("--device-audio")) return DeviceAudioUi(output);
            if (args.Contains("--settings-localization")) return SettingsLocalizationChecks(output);
            if (args.Contains("--interaction-audit")) return InteractionAuditUi(output);
            if (args.Contains("--device-actions")) return DeviceActionsUi(output);
            if (args.Contains("--restore-live")) return RestoreDisplayLive(output, args[2]);
            if (args.Contains("--phone-demand")) return PhoneDemandUi(output);
            if (args.Contains("--speaker-routing")) return SpeakerRoutingUi(output);
            if (args.Contains("--phone-clipboard")) return PhoneClipboardChecks(output);
            if (args.Contains("--qr-live")) return QrPairingLive(output);
            var config = ConfigService.Load(); config.QuickPanelUseLegacyAppRows = true; config.QuickPanelCompactShowOutputName = false;
            var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(args[1]))!;
            int mediaIndex = Array.IndexOf(args, "--media-preview");
            if (mediaIndex >= 0)
            {
                using var mediaJson = JsonDocument.Parse(File.ReadAllText(args[mediaIndex + 1]));
                _mediaPreview = mediaJson.RootElement.GetProperty("State").Deserialize<AndroidMediaState>();
            }
            var selected = catalog.Apps.Single(a => a.Package == "cn.wenyu.bodian");
            var phoneStore = (AndroidPhoneStore)typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!
                .GetProperty("Phone", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var retainedCatalog = catalog.Apps.Where(a => new[] { "cn.wenyu.bodian", "com.netease.cloudmusic", "tv.danmaku.bili", "com.tencent.mm", "com.tencent.mobileqq", "com.spotify.music" }.Contains(a.Package)).ToArray();
            foreach (var item in retainedCatalog) phoneStore.Retain(item);
            if (args.Contains("--phone-pages"))
            {
                CheckPhoneDeviceManagement(catalog, phoneStore, output);
                CheckPhoneConnectionEvents(catalog);
                CheckPhoneCatalogPage(catalog, phoneStore, output);
                File.WriteAllText(Path.Combine(output, "phone-pages-summary.json"), JsonSerializer.Serialize(new
                { Assertions = _checks, RealAdb = false, ProductStarted = false,
                    Scope = "shared phone/PC rows, theme, independent catalog page, extended selection, explicit bottom refresh" },
                    new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS {_checks} focused phone-page assertions; no phone connection or product startup.");
                return 0;
            }
            var manager = (AndroidAudioManager)typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!
                .GetProperty("Sources", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            if (args.Contains("--phone-layout") || args.Contains("--phone-refinements"))
            {
                phoneStore.RememberDevice(catalog.Device);
                var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
                cache.GetMethod("SetDevices", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { new[] { catalog.Device } });
                cache.GetField("SelectedSerial", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, catalog.Device.Serial);
                return args.Contains("--phone-refinements") ? PhoneRefinementChecks(catalog, phoneStore, manager, output) : PhoneLayoutChecks(catalog, phoneStore, output);
            }
            if (args.Contains("--live-multi")) return LiveMulti(manager, catalog, output, args);
            if (args.Contains("--lifecycle")) return CompanionLifecycle(app, manager, catalog);
            if (args.Contains("--live-drop"))
            {
                var livePanel = new QuickPanelModernWindow();
                var scroll = (ScrollViewer)livePanel.FindName("AppListScroll");
                var dataObject = new DataObject(AndroidAppDrag.Format, new AndroidAppDrag(catalog.Device, selected));
                var drag = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { dataObject, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, scroll, new Point(10, 10) }, null)!;
                drag.RoutedEvent = System.Windows.DragDrop.PreviewDropEvent;
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.Changed += () =>
                {
                    var state = manager.Sources.FirstOrDefault(s => s.App.Package == selected.Package)?.Session.State;
                    if (state?.State == CaptureState.Receiving) ready.TrySetResult();
                    else if (state?.State == CaptureState.Failed) ready.TrySetException(new Exception(state.Error));
                };
                int secondsIndex = Array.IndexOf(args, "--seconds");
                int seconds = secondsIndex >= 0 ? int.Parse(args[secondsIndex + 1]) : 120;
                var clock = Stopwatch.StartNew();
                using var process = Process.GetCurrentProcess();
                process.Refresh(); long memoryStart = process.PrivateMemorySize64;
                AndroidPcmSource? pcm = null;
                AndroidAudioSession? session = null;
                long packets = 0, rebases = 0;
                Exception? failure = null;
                try
                {

                    scroll.RaiseEvent(drag);
                    Pump(ready.Task.WaitAsync(TimeSpan.FromSeconds(20)));
                    session = manager.Sources.Single(s => s.App.Package == selected.Package).Session;
                    session.SetVolume(30);
                    Check(drag.Handled && drag.Effects == DragDropEffects.Copy && session.State.App?.Package == selected.Package,
                        "real phone capture starts through the application viewport drop handler");
                    pcm = session.Pcm!;
                    Pump(Task.Delay(2500));
                    Check(session.Pcm?.NonSilentSamplesWritten > 0 && session.Pcm.SamplesConsumed > 0,
                        "UI drop reaches non-silent PCM and the in-process WASAPI renderer");
                    var row = (FrameworkElement)((ItemsControl)livePanel.FindName("AppListPanel")).Items[0];
                    var volume = Descendants<Slider>(row).Single(); volume.Value = 20;
                    Check(pcm.Volume == 0.2f && session.State.Volume == 20, "actual Android row slider controls received PCM gain");
                    var mute = Descendants<Button>(row).Single(b => b.ToolTip?.ToString()?.Contains("手机音频") == true);
                    mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(pcm.Volume == 0 && session.State.Muted, "actual Android row icon mutes received PCM");
                    mute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(pcm.Volume == 0.2f && !session.State.Muted, "actual row unmute restores its volume");
                    string? defaultId = AudioService.GetDefaultDeviceId(SonicRoute.Core.Interop.EDataFlow.eRender);
                    Check(defaultId != null, "Core still resolves the default Windows output beside the embedded player");
                    string? route = session.State.OutputId == defaultId ? null : defaultId;
                    Pump(session.SetOutputAsync(route));
                    Check(session.State.OutputId == route && session.State.State == CaptureState.Receiving, "real endpoint renderer can be replaced during reception");
                    bool rejected = false;
                    try { Pump(session.SetOutputAsync("invalid-lab-endpoint")); }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException) { rejected = true; }
                    Check(rejected && session.State.OutputId == route && AudioService.GetDevices(SonicRoute.Core.Interop.EDataFlow.eRender).Count > 0,
                        "failed Android route preserves its previous output and Core endpoint reads");
                    for (int elapsed = 0; elapsed < seconds; elapsed += 10)
                    {
                        Pump(Task.Delay(TimeSpan.FromSeconds(Math.Min(10, seconds - elapsed))));
                        if (session.State.State != CaptureState.Receiving) throw new Exception(session.State.Error ?? session.State.State.ToString());
                        Console.WriteLine($"LIVE UI {Math.Min(elapsed + 10, seconds)}s packets={session.PacketsDecoded} nonSilent={pcm.NonSilentSamplesWritten} buffered={pcm.BufferedSamples}");
                    }
                    packets = session.PacketsDecoded; rebases = session.TimestampDiscontinuities;
                    Pump(manager.StopAllAsync());
                    Check(session.State.State == CaptureState.Idle && session.Pcm == null, "UI-started real capture stops cleanly");
                }
                catch (Exception ex) { failure = ex; throw; }
                finally
                {
                    Pump(manager.StopAllAsync()); livePanel.Close(); process.Refresh();
                    string evidencePath = Path.Combine(output, "live-drop-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
                    {
                        Success = failure == null, Error = failure?.ToString(), DurationSeconds = clock.Elapsed.TotalSeconds,
                        RequestedPlaybackSeconds = seconds, catalog.Device, selected.Package, selected.Uid, PacketsDecoded = packets,
                        TimestampDiscontinuities = rebases, SamplesWritten = pcm?.SamplesWritten, NonSilentSamplesWritten = pcm?.NonSilentSamplesWritten,
                        SamplesConsumed = pcm?.SamplesConsumed, DroppedSamples = pcm?.DroppedSamples, UnderflowSamples = pcm?.UnderflowSamples,
                        MemoryStart = memoryStart, MemoryEnd = process.PrivateMemorySize64, StateAfterStop = session?.State.State.ToString(),
                        Evidence = "Full WPF application routed drop -> actual phone UID -> in-process Opus PCM -> WASAPI; actual row slider/mute plus endpoint replacement. Routed event simulated, no physical mouse input, microphone recording or second-app isolation check.",
                        Log = manager.GetLog()
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine("Evidence: " + evidencePath);
                }
                Console.WriteLine($"PASS {_checks} live UI integration assertions. Routed drop event was simulated; no physical mouse input injected.");
                return 0;
            }
            var source = AddFixture(manager, catalog.Device, selected);
            var panel = new QuickPanelModernWindow();
            var outputs = new List<SonicRoute.Core.Models.AudioDeviceInfo>
            {
                new() { Id = AudioService.SystemDefaultDeviceId, DisplayName = "系统输出" },
                new() { Id = "chat", DisplayName = "聊天" }, new() { Id = "game", DisplayName = "游戏" }, new() { Id = "speaker", DisplayName = "扬声器" }
            };
            typeof(QuickPanelModernWindow).GetField("_outputDisplay", Hidden)!.SetValue(panel, outputs);
            var list = (ItemsControl)panel.FindName("AppListPanel");
            Check(panel.Width == 372 && panel.Height == 450, "existing quick-panel geometry retained");
            Check(((ScrollViewer)panel.FindName("AppListScroll")).AllowDrop, "application viewport accepts drag data");
            Check(((FrameworkElement)panel.FindName("AndroidDropHighlight")).Visibility == Visibility.Collapsed, "drop highlight starts hidden");
            Check(list.Items.Count == 1, "one Android source appears without a fake Windows PID");
            var root = (FrameworkElement)list.Items[0];
            Check(!Descendants<TextBlock>(root).Any(t => t.Text == selected.Name), "compact row hides app name");
            Check(Descendants<Slider>(root).Single().Height == 32, "Android row reuses compact slider height");
            Check(Descendants<StackPanel>(root).Any(s => s.Visibility == Visibility.Collapsed), "routing details start collapsed");
            var expand = Descendants<ToggleButton>(root).Single(); expand.IsChecked = true;
            foreach (var host in Descendants<AnimatedExpandHost>(root)) host.FinishImmediately();
            Check(!Descendants<ComboBox>(root).Any(), "expanded Android row uses native device pills rather than a combo box");
            var pills = Descendants<Button>(root).Where(b => b.Tag is SonicRoute.Core.Models.AudioDeviceInfo).ToList();
            Check(pills.Count == 4 && pills.Select(b => b.Content?.ToString()).SequenceEqual(outputs.Select(d => d.DisplayName)), "Android row shares the native output group and device labels");
            Check(pills[0].Style == panel.FindResource("DevButtonActive") && pills.Skip(1).All(b => b.Style == panel.FindResource("DevButton")), "native device active and idle styles are reused");
            Check(pills.All(b => b.ToolTip == null && System.Windows.Automation.AutomationProperties.GetName(b).Length > 0),
                "shared device pills omit hover previews and keep accessible device names");
            var exit = Descendants<Button>(root).Single(b => b.Content?.ToString() == "退出");
            Check(exit.Style == panel.FindResource("DevButton") && VisualTreeHelper.GetParent(exit) is WrapPanel, "exit is one extra native-style pill in the device group");
            var secondApp = catalog.Apps.Single(a => a.Package == "com.netease.cloudmusic");
            var second = AddFixture(manager, catalog.Device, secondApp);
            Notify(manager);
            DrainUi();
            Check(list.Items.Count == 2, "multiple phone sources have separate application rows");
            var firstSlider = Descendants<Slider>(root).Single(); firstSlider.Value = 23;
            Check(source.Session.State.Volume == 23 && second.Session.State.Volume == 100, "each row slider affects only its own source");
            for (int value = 0; value < 100; value++) source.Session.SetVolume(value);
            source.Session.SetVolume(37); DrainUi();
            Check(firstSlider.Value == 37 && second.Session.State.Volume == 100,
                "a burst of source changes renders its latest value without changing another source");
            var secondRoot = (FrameworkElement)list.Items[1];
            Descendants<ToggleButton>(secondRoot).Single().IsChecked = true;
            Check(expand.IsChecked == false, "opening another source collapses the previous expanded row");
            expand.IsChecked = true;
            foreach (var host in Descendants<AnimatedExpandHost>(root)) host.FinishImmediately();
            var phone = new PhoneAudioWindow(false);
            Check(phone.Height == panel.Height, "phone panel defaults to the main panel height");
            Check(phone.FindName("KeepPhoneAudio") == null && phone.FindName("StopButton") == null && phone.FindName("CaptureText") == null
                && !Descendants<Button>(phone.Content as DependencyObject ?? phone).Any(b => b.Content?.ToString() == "日志"),
                "phone footer controls are removed and the app list takes the remaining height");
            var items = retainedCatalog.Select(a => new PhoneAppItem(a)).ToList();
            Check(phone.FindName("AllCategoryButton") == null
                && ((Grid)((Border)phone.Content).Child).RowDefinitions.Count == 4
                && Grid.GetRow((ListBox)phone.FindName("PhoneApps")) == Grid.GetRow((Grid)((TextBox)phone.FindName("SearchBox")).Parent) + 1,
                "phone app list follows search directly without a category row");
            typeof(PhoneAudioWindow).GetField("_apps", Hidden)!.SetValue(phone, items);
            var devices = (ComboBox)phone.FindName("DeviceCombo"); devices.ItemsSource = new[] { catalog.Device }; devices.SelectedIndex = 0;
            items = ((IEnumerable<PhoneAppItem>)typeof(PhoneAudioWindow).GetField("_apps", Hidden)!.GetValue(phone)!).ToList();
            var search = (TextBox)phone.FindName("SearchBox"); search.Text = "波点";
            var phoneList = (ListBox)phone.FindName("PhoneApps");
            Check(phoneList.Items.Count == 1 && ((PhoneAppItem)phoneList.Items[0]).App.Package == selected.Package, "search filters actual phone application names");
            search.Text = "cn.wenyu.bodian"; Check(phoneList.Items.Count == 1, "package-name search works");
            search.Text = "__missing__"; Check(phoneList.Items.Count == 0, "empty search result is not a fake application");
            search.Text = "";
            Check(items.Count(a => a.Icon != null) == retainedCatalog.Length && phoneList.Items.Count < catalog.Apps.Count,
                "quick phone panel loads only retained app metadata and icons from the saved allowlist");
            CheckPhoneCache(phone, catalog, items);
            CheckPhonePlaybackFilter(phone, items);
            var data = new DataObject(AndroidAppDrag.Format, new AndroidAppDrag(catalog.Device, selected));
            Check(data.GetData(AndroidAppDrag.Format, false) is AndroidAppDrag payload && payload.Device == catalog.Device && payload.App == selected,
                "typed drag payload preserves identity while the target chooses playback mode");
            var cancel = (QueryContinueDragEventArgs)Activator.CreateInstance(typeof(QueryContinueDragEventArgs),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { true, DragDropKeyStates.LeftMouseButton }, null)!;
            cancel.RoutedEvent = System.Windows.DragDrop.QueryContinueDragEvent;
            typeof(PhoneAudioWindow).GetMethod("Apps_QueryContinueDrag", Hidden)!.Invoke(phone, new object[] { phoneList, cancel });
            Check(cancel.Action == DragAction.Cancel && cancel.Handled, "Esc cancels drag before a capture request");
            var previewApps = items.Where(a => new[] { "cn.wenyu.bodian", "com.netease.cloudmusic", "tv.danmaku.bili", "com.tencent.mm", "com.tencent.mobileqq", "com.spotify.music" }.Contains(a.App.Package)).ToList();
            phoneList.ItemsSource = previewApps;
            CheckPhonePresentation(phone, output, dark: false);
            Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, "panel-compact-light.png"));
            CheckDropZones(panel, catalog.Device, selected, output);
            CheckFullAppRoutesAndVisibility(panel, output);
            CheckPhoneDeviceManagement(catalog, phoneStore, output);
            CheckPhoneConnectionEvents(catalog);
            var slider = Descendants<Slider>(root).Single();
            Check(((Border)slider.Template.FindName("PART_TrackFill", slider)).Width > 0, "shared volume track shows filled level");
            config.QuickPanelUseLegacyAppRows = false;
            typeof(QuickPanelModernWindow).GetMethod("RefreshAndroidRow", Hidden)!.Invoke(panel, null);
            root = (FrameworkElement)list.Items[0];
            Check(Descendants<TextBlock>(root).Any(t => t.Text == selected.Name), "full row shows Android application name");
            ThemeService.Apply("dark", "#00CBE9");
            CheckPhonePresentation(phone, output, dark: true);
            Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, "panel-full-dark.png"));
            Pump(manager.StopAllAsync());
            DrainUi();
            Check(list.Items.Count == 0, "stop all removes all Android source rows");
            phone.Close(); panel.Close();
            Check((bool)typeof(QuickPanelModernWindow).GetField("_isClosed", Hidden)!.GetValue(panel)!, "closed panel releases its UI lifetime");
            Console.WriteLine($"PASS {_checks} UI assertions; offline renders saved; no input injection or real capture in this UI check.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static AndroidAudioSource AddFixture(AndroidAudioManager manager, AndroidDevice device, AndroidApp app)
    {
        var session = new AndroidAudioSession(() => throw new InvalidOperationException("Offline UI fixture"), (_, _) => throw new InvalidOperationException("Offline UI fixture"));
        typeof(AndroidAudioSession).GetField("_state", Hidden)!.SetValue(session, new AndroidAudioState(CaptureState.Receiving, device, app));
        var source = (AndroidAudioSource)Activator.CreateInstance(typeof(AndroidAudioSource), BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { device, app, session, true }, null)!;
        var started = (TaskCompletionSource<AndroidAudioSource>)typeof(AndroidAudioSource).GetField("Started", Hidden)!.GetValue(source)!;
        started.TrySetResult(source);
        Action<AndroidAudioState> handler = _ => Notify(manager);
        typeof(AndroidAudioSource).GetField("Handler", Hidden)!.SetValue(source, handler); session.Changed += handler;
        ((List<AndroidAudioSource>)typeof(AndroidAudioManager).GetField("_sources", Hidden)!.GetValue(manager)!).Add(source);
        typeof(AndroidAudioManager).GetMethod("PublishSources", Hidden)!.Invoke(manager, null);
        return source;
    }
    private static int PhoneLayoutChecks(Catalog catalog, AndroidPhoneStore phoneStore, string output)
    {
        var phone = new PhoneAudioWindow(false);
        try
        {
            Pump((Task)typeof(PhoneAudioWindow).GetMethod("InitializeDevicesAsync", Hidden)!.Invoke(phone, null)!);
            CheckPhonePlacement();
            CheckPhonePresentation(phone, output, dark: false);
            ThemeService.Apply("dark", "#00CBE9");
            CheckPhonePresentation(phone, output, dark: true);
        }
        finally { phone.Close(); ThemeService.Apply("light", "#00CBE9"); }

        CheckPhoneDeviceManagement(catalog, phoneStore, output);
        CheckPhoneConnectionEvents(catalog); // Injected adb runner; no device process is launched.
        var summaryPath = Path.Combine(output, "phone-layout-summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(new
        {
            Mode = "phone-layout",
            Assertions = _checks,
            Checks = new[] { "phone presentation and scroll geometry", "companion and extra-panel placement geometry", "phone profile management and retained-app visibility", "mocked connection and pairing flow" },
            RealAdb = false,
            ProductStarted = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} phone layout assertions; summary: {summaryPath}; no device access.");
        return 0;
    }
    private static int PhoneRefinementChecks(Catalog catalog, AndroidPhoneStore store, AndroidAudioManager manager, string output)
    {
        var phone = new PhoneAudioWindow(false);
        var panel = new QuickPanelModernWindow();
        string originalName = store.Profile.Name;
        try
        {
            Pump((Task)typeof(PhoneAudioWindow).GetMethod("InitializeDevicesAsync", Hidden)!.Invoke(phone, null)!);
            int nameEvents = 0;
            Action nameChanged = () => ++nameEvents;
            store.DisplayNamesChanged += nameChanged;
            store.Rename("小米13");
            store.ObserveDevices(new[] { catalog.Device with { Name = "Raw scanned model" } });
            Check(nameEvents == 1 && store.GetDisplayName(catalog.Device) == "小米13", "a custom phone name survives discovery and only an actual name change raises the name event");
            store.DisplayNamesChanged -= nameChanged;
            var source = AddFixture(manager, catalog.Device, catalog.Apps.First(a => a.Package == "cn.wenyu.bodian"));
            var applicationAudio = typeof(App).Assembly.GetType("SonicRoute.ApplicationAudio")!;
            var describe = applicationAudio.GetMethod("Describe", BindingFlags.NonPublic | BindingFlags.Static)!;
            var info = (SonicRoute.Core.Models.AudioAppInfo)describe.Invoke(null, new object[] { source })!;
            Check(info.SourceLabel == "来自 小米13", "the shared audio-session model exposes the custom phone name to full UI and automation candidates");
            foreach (bool dark in new[] { false, true })
            {
                ThemeService.Apply(dark ? "dark" : "light", "#00CBE9");
                typeof(PhoneAudioWindow).GetMethod("DeviceNamesChanged", Hidden)!.Invoke(phone, null);
                CheckPhonePresentation(phone, output, dark);
                var combo = (ComboBox)phone.FindName("DeviceCombo");
                Check(Descendants<TextBlock>(combo).Any(label => label.Text == "小米13"), "selected device name resolves the saved alias in " + (dark ? "dark" : "light") + " mode");
                Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, dark ? "refined-panel-dark.png" : "refined-panel-light.png"));
                CheckDropZones(panel, catalog.Device, source.App, output);
            }
            store.Rename("书房手机");
            typeof(PhoneAudioWindow).GetMethod("DeviceNamesChanged", Hidden)!.Invoke(phone, null);
            Render((FrameworkElement)phone.Content, 235, 450, Path.Combine(output, "renamed-phone.png"));
            Check(Descendants<TextBlock>((ComboBox)phone.FindName("DeviceCombo")).Any(label => label.Text == "书房手机"), "renaming updates the selected template without rebuilding device choices or scanning ADB");
            Check(!ToolTipService.GetIsEnabled(new System.Windows.Documents.Run("hint")), "text-run hover bubbles follow the same global disabled policy");
            var path = Path.Combine(output, "phone-refinements-summary.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Mode = "phone-refinements", Assertions = _checks, RealAdb = false, ProductStarted = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} focused UI refinements; no device access: {path}");
            return 0;
        }
        finally { Pump(manager.StopAllAsync()); phone.Close(); panel.Close(); store.Rename(originalName); ThemeService.Apply("light", "#00CBE9"); }
    }
    private static void Notify(AndroidAudioManager manager) => typeof(AndroidAudioManager).GetMethod("Notify", Hidden)!.Invoke(manager, null);
    private static void CheckPhoneCache(PhoneAudioWindow phone, Catalog catalog, List<PhoneAppItem> items)
    {
        var iconApp = catalog.Apps.First(app => app.IconPng is { Length: > 0 });
        var shared = new PhoneAppItem(iconApp).Icon;
        var concurrentIcons = new ImageSource?[16];
        Parallel.For(0, concurrentIcons.Length, i => concurrentIcons[i] = new PhoneAppItem(iconApp).Icon);
        Check(shared?.IsFrozen == true && concurrentIcons.All(image => ReferenceEquals(image, shared)),
            "phone app views share one frozen PNG image across concurrent requests");
        var releasedIconKey = MakeTemporaryIconKey(iconApp);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!releasedIconKey.TryGetTarget(out _), "retired catalogue PNG keys are not retained by the shared image cache");
        var cache = typeof(PhoneAudioWindow).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        cache.GetMethod("SetDevices", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { new[] { catalog.Device } });
        cache.GetField("SelectedSerial", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, catalog.Device.Serial);
        var reopened = new PhoneAudioWindow(false);
        try
        {
            var initialize = (Task)typeof(PhoneAudioWindow).GetMethod("InitializeDevicesAsync", Hidden)!.Invoke(reopened, null)!;
            Check(initialize.IsCompletedSuccessfully && ((ListBox)reopened.FindName("PhoneApps")).Items.Count == items.Count
                && ReferenceEquals(((PhoneAppItem)((ListBox)reopened.FindName("PhoneApps")).Items[0]).Icon, items[0].Icon),
                "reopened phone panel binds retained metadata and shared frozen icons synchronously without an ADB request");
            Check(reopened.Title == "手机音频" && !Descendants<TextBlock>((DependencyObject)reopened.Content).Any(t => t.Text == "Beta")
                && System.Windows.Automation.AutomationProperties.GetName((Button)reopened.FindName("RefreshButton")) == "刷新",
                "phone title removes Beta while the icon reload button remains named Refresh");
        }
        finally { reopened.Close(); }
        var phoneList = (ListBox)phone.FindName("PhoneApps");
        var before = phoneList.ItemsSource;
        for (int i = 0; i < 100; ++i) typeof(PhoneAudioWindow).GetMethod("RetainedAppsChanged", Hidden)!.Invoke(phone, null);
        Check(ReferenceEquals(before, phoneList.ItemsSource),
            "unchanged retained notifications preserve the existing phone list and its scroll/selection containers");
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<byte[]> MakeTemporaryIconKey(AndroidApp app)
    {
        byte[] key = app.IconPng!.ToArray();
        _ = new PhoneAppItem(app with { IconPng = key });
        return new(key);
    }
    private static void CheckPhonePresentation(PhoneAudioWindow phone, string output, bool dark)
    {
        var media = (_mediaPreview ?? new AndroidMediaState("cn.wenyu.bodian", "示例曲目", "示例歌手", true, 306,
            PositionMs: 211000, DurationMs: 238000)) with { Playing = true, Actions = 306, ReceivedTimestamp = Stopwatch.GetTimestamp() };
        typeof(PhoneAudioWindow).GetMethod("ApplyMediaState", Hidden)!.Invoke(phone, new object[] { media });
        var qualityMethod = typeof(PhoneAudioWindow).GetMethod("ApplyConnectionQuality", Hidden)!;
        qualityMethod.Invoke(phone, new object[] { new AndroidConnectionQuality(25) });
        int phoneWidth = (int)Math.Round(phone.Width), phoneHeight = (int)Math.Round(phone.Height);
        Render((FrameworkElement)phone.Content, phoneWidth, phoneHeight, Path.Combine(output, dark ? "phone-dark.png" : "phone-light.png"));
        var title = (TextBlock)phone.FindName("MediaTitle");
        var previous = (Button)phone.FindName("MediaPreviousButton");
        var toggle = (Button)phone.FindName("MediaPlayPauseButton");
        var next = (Button)phone.FindName("MediaNextButton");
        var refresh = (Button)phone.FindName("RefreshButton");
        Check(title.Text == media.Title && previous.IsEnabled && toggle.IsEnabled && next.IsEnabled
            && System.Windows.Automation.AutomationProperties.GetName(toggle) == "暂停", (dark ? "dark" : "light") + " phone music bar shows received metadata and advertised transport controls");
        var expected = ((SolidColorBrush)phone.FindResource("Theme.SurfaceBg")).Color;
        Check(((SolidColorBrush)refresh.Background).Color == expected
            && ((SolidColorBrush)toggle.Background).Color == expected
            && refresh.Style == phone.FindResource("QuickPanelToolButton") && (dark ? expected.R < 80 : expected.R > 240),
            (dark ? "dark" : "light") + " refresh and media buttons reuse the theme surface and existing shared template");
        Check(((Slider)phone.FindName("MediaPositionSlider")).Maximum == media.DurationMs
            && ((Slider)phone.FindName("MediaPositionSlider")).IsEnabled
            && ((TextBlock)phone.FindName("MediaDurationText")).Text != "--:--"
            && ((System.Windows.Shapes.Path)phone.FindName("MediaPlayPausePath")).Data == AutomationIcons.Pause,
            (dark ? "dark" : "light") + " music progress uses phone duration with seeking and the shared solid pause geometry");
        var cover = (Border)phone.FindName("MediaArtworkFrame");
        Check(((Image)phone.FindName("MediaArtwork")).Source != null && title.TranslatePoint(new Point(), (UIElement)phone.Content).X
            >= cover.TranslatePoint(new Point(), (UIElement)phone.Content).X + cover.ActualWidth,
            "album artwork or app icon occupies the far left and the track text follows it");
        var grid = (Grid)((Border)phone.Content).Child;
        var card = (Border)phone.FindName("PhoneDeviceCard");
        var footer = (Grid)VisualTreeHelper.GetParent(card);
        Check(Grid.GetRow(footer) == grid.RowDefinitions.Count - 1 && card.ActualHeight > 0
            && !Descendants<TextBlock>((DependencyObject)phone.Content).Any(t => t.Text == "手机音频"),
            "device refresh card occupies the bottom row and the old visible phone title is removed");
        var deviceCombo = (ComboBox)phone.FindName("DeviceCombo");
        var connectionText = (TextBlock)phone.FindName("ConnectionText");
        Check(VisualTreeHelper.GetParent(refresh) == footer && Math.Abs(card.ActualHeight - 36) < 0.1
            && Math.Abs(refresh.ActualHeight - 36) < 0.1 && Math.Abs(refresh.ActualWidth - 36) < 0.1
            && Math.Abs(refresh.TranslatePoint(new Point(), footer).X - card.TranslatePoint(new Point(), footer).X - card.ActualWidth - 4) < 0.1
            && deviceCombo.ActualWidth >= 120 && Grid.GetRow(deviceCombo) == Grid.GetRow(connectionText)
            && Grid.GetColumn(deviceCombo) < Grid.GetColumn(connectionText)
            && (connectionText.Text is "未连接" or "已连接") && !connectionText.Text.Contains("应用"),
            "compact device row extends the name card, keeps same-line status and uses a 4px gap with a 36px refresh button");
        var deviceToggle = (ToggleButton)deviceCombo.Template.FindName("ToggleButton", deviceCombo)!;
        var deviceArrow = Descendants<System.Windows.Shapes.Path>(deviceToggle).Single();
        var namePresenter = (ContentPresenter)deviceToggle.Template.FindName("content", deviceToggle)!;
        double signalLeft = ((FrameworkElement)phone.FindName("ConnectionSignal")).TranslatePoint(new Point(), card).X;
        double statusRight = connectionText.TranslatePoint(new Point(connectionText.ActualWidth, 0), card).X;
        Check(deviceArrow.TranslatePoint(new Point(), card).X >= statusRight + 2
            && namePresenter.TranslatePoint(new Point(namePresenter.ActualWidth, 0), card).X <= signalLeft - 2,
            "device name and status keep separate space with the reused dropdown arrow at the far right");
        var controls = (FrameworkElement)phone.FindName("MediaControlsHost");
        double firstX = previous.TranslatePoint(new Point(), controls).X;
        double lastX = next.TranslatePoint(new Point(), controls).X + next.ActualWidth;
        Check(controls.HorizontalAlignment == HorizontalAlignment.Center && Math.Abs((firstX + lastX) / 2 - controls.ActualWidth / 2) < 0.1,
            "previous play and next buttons have a centered group with symmetric spacing");
        var phoneList = (ListBox)phone.FindName("PhoneApps");
        var itemContainer = (ListBoxItem)phoneList.ItemContainerGenerator.ContainerFromIndex(0);
        Check(itemContainer.ActualHeight > 0 && itemContainer.ActualHeight <= 50.1
            && refresh.TranslatePoint(new Point(refresh.ActualWidth, 0), footer).X <= footer.ActualWidth + 0.1,
            "compact application cards remain readable and the standalone refresh button fits the footer");
        var dot = (System.Windows.Shapes.Path)phone.FindName("SignalDot");
        var outer = (System.Windows.Shapes.Path)phone.FindName("SignalOuter");
        var signal = (Grid)phone.FindName("ConnectionSignal");
        Check(dot.Opacity == 1 && outer.Opacity == 1 && signal.Width == 14 && signal.Height == 12
            && ((SolidColorBrush)outer.Fill).Color == ((SolidColorBrush)phone.FindResource("Theme.Accent")).Color,
            (dark ? "dark" : "light") + " connection quality uses themed vector signal shapes rather than a text glyph");
        qualityMethod.Invoke(phone, new object[] { new AndroidConnectionQuality(450) });
        Check(dot.Opacity == 1 && outer.Opacity == 0.2, "high connection RTT weakens the signal without fabricating a disconnected device");
        qualityMethod.Invoke(phone, new object[] { new AndroidConnectionQuality(25) });
        CheckPhoneScroll(phone);
    }
    private static void CheckPhoneScroll(PhoneAudioWindow phone)
    {
        var phoneList = (ListBox)phone.FindName("PhoneApps");
        var itemContainer = (ListBoxItem)phoneList.ItemContainerGenerator.ContainerFromIndex(0);
        var viewer = Descendants<ScrollViewer>(phoneList).Single();
        var verticalBar = Descendants<ScrollBar>(viewer).Single(bar => bar.Orientation == Orientation.Vertical);
        double right = verticalBar.TranslatePoint(new Point(verticalBar.ActualWidth, 0), (UIElement)phone.Content).X;
        Check(viewer.Style == phone.FindResource("PanelEdgeScrollViewer") && right > ((FrameworkElement)phone.Content).ActualWidth - 6,
            "phone list reuses the main panel scrollbar gutter at the window edge");
        double viewportWidth = viewer.ViewportWidth, rowWidth = itemContainer.ActualWidth;
        var originalItems = phoneList.ItemsSource;
        phoneList.ItemsSource = originalItems.Cast<PhoneAppItem>().Take(1).ToList();
        var content = (FrameworkElement)phone.Content;
        int phoneWidth = (int)Math.Round(phone.Width), phoneHeight = (int)Math.Round(phone.Height);
        content.Measure(new Size(phoneWidth, phoneHeight)); content.Arrange(new Rect(0, 0, phoneWidth, phoneHeight)); content.UpdateLayout();
        var single = (ListBoxItem)phoneList.ItemContainerGenerator.ContainerFromIndex(0);
        Check(viewer.ComputedVerticalScrollBarVisibility != Visibility.Visible
            && Math.Abs(viewer.ViewportWidth - viewportWidth) < 0.25 && Math.Abs(single.ActualWidth - rowWidth) < 0.25,
            "filtering to a short list hides the scrollbar without shifting or stretching application cards");
        phoneList.ItemsSource = originalItems;
        content.Measure(new Size(phoneWidth, phoneHeight)); content.Arrange(new Rect(0, 0, phoneWidth, phoneHeight)); content.UpdateLayout();
    }
    private static void CheckPhonePlaybackFilter(PhoneAudioWindow phone, List<PhoneAppItem> items)
    {
        var apply = typeof(PhoneAudioWindow).GetMethod("ApplyMediaState", Hidden)!;
        var selected = items.First(a => a.Package == "cn.wenyu.bodian");
        var other = items.First(a => a.Package == "com.netease.cloudmusic");
        var list = (ListBox)phone.FindName("PhoneApps");
        Check(selected.Category == "media" && selected.Description == "影音 · 音乐",
            "known music app retains its media category and music subtitle when Android metadata is unset");
        apply.Invoke(phone, new object[] { new AndroidMediaState(selected.Package, "Track", "Artist", true, 306)
            { PlayingPackages = new[] { selected.Package, other.Package } } });
        Check(list.Items.Count == items.Count && list.Items.Cast<PhoneAppItem>().Take(2).All(a => a.IsPlaying)
            && list.Items.Cast<PhoneAppItem>().Take(2).Select(a => a.Package).ToHashSet().SetEquals(new[] { selected.Package, other.Package }),
            "all reported playing media sessions receive badges and sort before other apps");
        apply.Invoke(phone, new object[] { new AndroidMediaState(selected.Package, "Track", "Artist", false, 306) });
        Check(list.Items.Count == items.Count && items.All(a => !a.IsPlaying), "pause events clear playing badges while keeping the full app list");
        var search = (TextBox)phone.FindName("SearchBox");
        search.Text = "cn.wenyu.bodian";
        Check(list.Items.Count == 1 && ((PhoneAppItem)list.Items[0]).Package == selected.Package,
            "package search remains available without category filtering");
        search.Text = "";
    }
    private static void CheckDropZones(QuickPanelModernWindow panel, AndroidDevice device, AndroidApp app, string output)
    {
        var scroll = (ScrollViewer)panel.FindName("AppListScroll");
        var overlay = (Border)panel.FindName("AndroidDropHighlight");
        var computer = (Border)panel.FindName("AndroidComputerDropZone");
        var shared = (Border)panel.FindName("AndroidSharedDropZone");
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var dragging = host.GetProperty("IsDragging", BindingFlags.Static | BindingFlags.NonPublic)!;
        dragging.SetValue(null, true);
        Check(overlay.Visibility == Visibility.Visible, "lifting an Android icon reveals both targets before the pointer enters the main panel");
        var clear = typeof(QuickPanelModernWindow).GetMethod("ClearAndroidDropHighlight", Hidden)!;
        clear.Invoke(panel, null);
        Check(overlay.Visibility == Visibility.Visible, "targets remain visible while the Android drag is outside the main panel");
        dragging.SetValue(null, false);
        Check(overlay.Visibility == Visibility.Collapsed, "release or cancellation immediately hides the drag targets");
        Check(!ToolTipService.GetIsEnabled(computer) && !ToolTipService.GetIsEnabled(shared), "global hover policy disables framework hover bubbles");
        var split = typeof(QuickPanelModernWindow).GetMethod("IsSharedAndroidDrop", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Shared(double y) => (bool)split.Invoke(null, new object[] { y, 200d })!;
        Check(!Shared(0) && !Shared(99.9) && Shared(100) && Shared(199), "upper drop half requests PC-only and lower half requests shared playback, including the divider");
        var data = new DataObject(AndroidAppDrag.Format, new AndroidAppDrag(device, app));
        foreach (bool lower in new[] { false, true })
        {
            var over = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, scroll,
                    new Point(10, scroll.ActualHeight * (lower ? 0.75 : 0.25)) }, null)!;
            over.RoutedEvent = System.Windows.DragDrop.PreviewDragOverEvent; scroll.RaiseEvent(over);
            Render((FrameworkElement)panel.Content, 372, 450, Path.Combine(output, lower ? "drop-shared-light.png" : "drop-computer-light.png"));
            Check(over.Handled && over.Effects == DragDropEffects.Copy && overlay.Visibility == Visibility.Visible
                && ReferenceEquals((lower ? shared : computer).Background, panel.FindResource("Theme.Overlay")),
                (lower ? "lower" : "upper") + " target highlights through the actual WPF drag-over handler");
        }
        Check(Math.Abs(computer.ActualHeight - shared.ActualHeight) < 0.5, "drop targets divide the available app viewport into equal halves");
        DragEventArgs At(double y) => (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, scroll, new Point(10, y) }, null)!;
        var visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Border))!;
        var background = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Border.BackgroundProperty, typeof(Border))!;
        var list = (UIElement)panel.FindName("AppListPanel");
        int visibilityChanges = 0, brushChanges = 0;
        EventHandler visibilityChanged = (_, _) => visibilityChanges++;
        EventHandler brushChanged = (_, _) => brushChanges++;
        visibility.AddValueChanged(overlay, visibilityChanged);
        background.AddValueChanged(shared, brushChanged);
        double viewportHeight = scroll.ActualHeight;
        try
        {
            for (int i = 0; i < 40; i++)
            {
                var same = At(viewportHeight * 0.75);
                same.RoutedEvent = System.Windows.DragDrop.PreviewDragOverEvent; scroll.RaiseEvent(same);
                var childLeave = At(viewportHeight * 0.75);
                childLeave.RoutedEvent = System.Windows.DragDrop.DragLeaveEvent; list.RaiseEvent(childLeave);
            }
            Check(visibilityChanges == 0 && brushChanges == 0 && overlay.Visibility == Visibility.Visible,
                "repeated drag-over and internal child leave events neither hide nor repaint the unchanged target");
            for (int i = 0; i < 40; i++)
            {
                var crossing = At(viewportHeight * (i % 2 == 0 ? 0.25 : 0.75));
                crossing.RoutedEvent = System.Windows.DragDrop.PreviewDragOverEvent; scroll.RaiseEvent(crossing);
                ((FrameworkElement)panel.Content).UpdateLayout();
            }
            Check(visibilityChanges == 0 && Math.Abs(scroll.ActualHeight - viewportHeight) < 0.1,
                "crossing upper and lower targets preserves overlay visibility and viewport geometry");
            DrainUi();
            Check(((SolidColorBrush)overlay.Background).Color.A == 255 && list.Visibility == Visibility.Hidden,
                "drag overlay has an opaque theme backing and suppresses underlying rows throughout target crossings");
        }
        finally
        {
            visibility.RemoveValueChanged(overlay, visibilityChanged);
            background.RemoveValueChanged(shared, brushChanged);
        }
        var leave = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, scroll, new Point(-1, -1) }, null)!;
        leave.RoutedEvent = System.Windows.DragDrop.DragLeaveEvent; scroll.RaiseEvent(leave);
        Check(overlay.Visibility == Visibility.Collapsed, "leaving or cancelling the drag clears both targets");
        DrainUi(); Check(list.Visibility == Visibility.Visible, "ending a drop restores underlying application rows without changing layout");
        var returning = At(viewportHeight * 0.25);
        returning.RoutedEvent = System.Windows.DragDrop.PreviewDragOverEvent; scroll.RaiseEvent(returning);
        typeof(QuickPanelModernWindow).GetMethod("ClearAndroidDropHighlight", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);
        Check(overlay.Visibility == Visibility.Collapsed, "drag completion and Esc cleanup remove a target even if the pointer remains inside");
    }

    private static void CheckFullAppRoutesAndVisibility(QuickPanelModernWindow panel, string output)
    {
        var config = ConfigService.Load();
        bool previousMic = config.ExperimentalMic;
        var hiddenOut = config.HiddenOutputDevices.ToArray(); var hiddenIn = config.HiddenInputDevices.ToArray();
        var main = new MainWindow();
        try
        {
            config.ExperimentalMic = true; config.HiddenOutputDevices.Clear(); config.HiddenInputDevices.Clear();
            config.HiddenOutputDevices.Add("hidden-output"); config.HiddenInputDevices.Add("hidden-input");
            var outputs = new List<SonicRoute.Core.Models.AudioDeviceInfo> {
                new() { Id = "speaker", DisplayName = "扬声器", Flow = SonicRoute.Core.Interop.EDataFlow.eRender },
                new() { Id = "hidden-output", DisplayName = "未保留输出", Flow = SonicRoute.Core.Interop.EDataFlow.eRender } };
            var inputs = new List<SonicRoute.Core.Models.AudioDeviceInfo> {
                new() { Id = "mic", DisplayName = "麦克风", Flow = SonicRoute.Core.Interop.EDataFlow.eCapture },
                new() { Id = "hidden-input", DisplayName = "未保留输入", Flow = SonicRoute.Core.Interop.EDataFlow.eCapture } };
            typeof(MainWindow).GetField("_outputs", Hidden)!.SetValue(main, outputs);
            typeof(MainWindow).GetField("_inputs", Hidden)!.SetValue(main, inputs);
            typeof(MainWindow).GetMethod("RefreshDeviceDisplays", Hidden)!.Invoke(main, null);
            var outPills = (ItemsControl)main.FindName("AppsOutputQuickPanel");
            var inPills = (ItemsControl)main.FindName("AppsInputQuickPanel");
            var outCombo = (ComboBox)main.FindName("AppsOutputCombo");
            var inCombo = (ComboBox)main.FindName("AppsInputCombo");
            Check(outPills.Items.Count == 2 && inPills.Items.Count == 2 && outCombo.Items.Count == 3 && inCombo.Items.Count == 3,
                "full app routing shows only retained output/input pills while combos retain all devices");
            foreach (var pair in new[] { (outPills, outCombo), (inPills, inCombo) })
            {
                var pill = pair.Item1.Items.OfType<Button>().Last(); pill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(ReferenceEquals(pill.Style, main.FindResource("QuickDevButtonActive"))
                    && ((SonicRoute.Core.Models.AudioDeviceInfo)pair.Item2.SelectedItem).Id == ((SonicRoute.Core.Models.AudioDeviceInfo)pill.Tag).Id,
                    "retained device pill reuses the overview active style and existing combo selection path");
            }
            ((FrameworkElement)main.FindName("AppsPage")).Visibility = Visibility.Visible;
            ((FrameworkElement)main.FindName("AppsInputCard")).Visibility = Visibility.Visible;
            inCombo.Visibility = Visibility.Visible;
            ((FrameworkElement)main.FindName("AppsInputLabel")).Visibility = Visibility.Visible;
            var route = (FrameworkElement)main.FindName("AppsRoutingPanel"); route.IsEnabled = true;
            Render(route, 720, 260, Path.Combine(output, "app-routing-retained-light.png"));
            var controls = (Grid)main.FindName("AppsControls");
            var controlsContent = (StackPanel)controls.Parent;
            var controlCard = (Border)controlsContent.Parent;
            controls.IsEnabled = true;
            Check(controlsContent.Children.OfType<TextBlock>().Any(t => t.Text == "快捷控制")
                && ReferenceEquals(controlCard.Style, main.FindResource("AppsSectionCard")),
                "quick controls title and both switches share one reused section card");
            var details = (ScrollViewer)main.FindName("AppsDetailsScroll");
            Render(details, 720, 470, Path.Combine(output, "app-details-light.png"));
            Check(((StackPanel)details.Content).DesiredSize.Height <= 470, "compact app details fit header routing volume and quick controls in one viewport");
            ThemeService.Apply("dark", "#00CBE9");
            Render(details, 720, 470, Path.Combine(output, "app-details-dark.png"));
            Check(ReferenceEquals(controlCard.Background, main.FindResource("Theme.SurfaceBgAlpha"))
                && ((SolidColorBrush)controlCard.Background).Color.R < 80,
                "existing quick-controls card updates to the dark theme surface");
            ThemeService.Apply("light", "#00CBE9");
            CheckUnifiedApplications(main, panel);
            config.HiddenOutputDevices.Add(AudioService.SystemDefaultDeviceId); config.HiddenOutputDevices.Add("speaker");
            config.HiddenInputDevices.Add(AudioService.SystemDefaultInputDeviceId); config.HiddenInputDevices.Add("mic");
            typeof(MainWindow).GetMethod("RenderAppsRouteButtons", Hidden)!.Invoke(main, null);
            Check(outPills.Items.Count == 0 && inPills.Items.Count == 0, "hiding all retained devices also removes their shortcuts, including system defaults");

            uint own = (uint)Environment.ProcessId;
            uint other = own == 1 ? 2u : 1u;
            var apps = new List<SonicRoute.Core.Models.AudioAppInfo> {
                new() { ProcessId = own, ProcessName = "renamed-host" },
                new() { ProcessId = other, ProcessName = "sOnIcRoUtE" },
                new() { ProcessId = other + 1, ProcessName = "browser", DisplayName = "SonicRoute" } };
            Check(AudioService.IsInternalApplication(own, null) && AudioService.IsInternalApplication(other, "SonicRoute.exe")
                && !AudioService.IsInternalApplication(other + 1, "browser"), "shared app visibility excludes the host and other SonicRoute instances by identity");
            var candidates = (IList)typeof(MainWindow).GetMethod("GetAppCandidates", Hidden)!.Invoke(main, new object[] { apps })!;
            Check(candidates.Count == 1 && ((AppItem)candidates[0]!).Info.ProcessName == "browser",
                "full app overview settings and automation candidates all exclude SonicRoute without filtering other apps by display title");
            Func<IEnumerable<int>, (Dictionary<int, (int Volume, bool Muted, bool MuteKnown)>, bool?)> read = ids =>
                (ids.ToDictionary(id => id, _ => (100, false, true)), false);
            typeof(QuickPanelModernWindow).GetField("_readPanelState", Hidden)!.SetValue(panel, read);
            Pump((Task)typeof(QuickPanelModernWindow).GetMethod("BuildAppRowsAsync", Hidden)!.Invoke(panel, new object[] { apps.Take(2).ToList(), config })!);
            Check(((IDictionary)typeof(QuickPanelModernWindow).GetField("_rows", Hidden)!.GetValue(panel)!).Count == 0,
                "quick-panel native app rows also exclude every SonicRoute instance");
        }
        finally
        {
            config.ExperimentalMic = previousMic;
            config.HiddenOutputDevices.Clear(); config.HiddenOutputDevices.AddRange(hiddenOut);
            config.HiddenInputDevices.Clear(); config.HiddenInputDevices.AddRange(hiddenIn);
            main.Close();
        }
    }
    private static int CompanionLifecycle(App app, AndroidAudioManager manager, Catalog catalog)
    {
        var config = ConfigService.Load(); config.QuickPanelShowPhoneAudio = true; config.QuickPanelHeight = 450;
        AddFixture(manager, catalog.Device, catalog.Apps.Single(a => a.Package == "cn.wenyu.bodian"));
        AddFixture(manager, catalog.Device, catalog.Apps.Single(a => a.Package == "com.netease.cloudmusic"));
        QuickPanelModernWindow? Panel() => (QuickPanelModernWindow?)typeof(App).GetField("_quickPanel", Hidden)!.GetValue(app);
        PhoneAudioWindow? Phone() => (PhoneAudioWindow?)typeof(App).GetField("_phoneAudioWindow", Hidden)!.GetValue(app);
        void Toggle() => typeof(App).GetMethod("ToggleQuickPanel", Hidden)!.Invoke(app, null);
        void Click() => ((ButtonBase)Panel()!.FindName("PhoneAudioButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        try
        {
            Toggle(); Until(() => Phone()?.IsVisible == true);
            Check(Math.Abs(Phone()!.ActualHeight - Panel()!.ActualHeight) < 1 && Math.Abs(Phone()!.Top - Panel()!.Top) < 1,
                "visible companion aligns with and matches the quick panel height");
            Panel()!.Height = 600; Pump(Task.Delay(150));
            Check(Math.Abs(Phone()!.ActualHeight - 600) < 1, "companion follows a live main-panel height change");
            Panel()!.Height = 350; Pump(Task.Delay(150));
            Check(Math.Abs(Phone()!.ActualHeight - 350) < 1 && ((ListBox)Phone()!.FindName("PhoneApps")).ActualHeight >= 40,
                "minimum main-panel height keeps the phone application list usable");
            Click(); Check(Phone() == null && !config.QuickPanelShowPhoneAudio, "phone audio button toggles the companion off");
            Click(); Until(() => Phone()?.IsVisible == true);
            Check(config.QuickPanelShowPhoneAudio, "phone audio button toggles the companion on");
            Panel()!.Close();
            Check(Phone() == null && config.QuickPanelShowPhoneAudio && manager.Sources.Count == 2,
                "folding the main panel releases companion UI and preserves its preference and audio sources");
            Toggle(); Until(() => Phone()?.IsVisible == true);
            Check(manager.Sources.Count == 2, "reopened quick panel restores its enabled companion without removing sources");
            Phone()!.Close();
            Check(!config.QuickPanelShowPhoneAudio, "explicit companion close clears only the open preference");
            Panel()!.Close(); Toggle();
            Until(() => Panel() != null && (bool)typeof(QuickPanelModernWindow).GetField("_contentReady", Hidden)!.GetValue(Panel())!);
            Check(Phone() == null && !config.QuickPanelShowPhoneAudio, "manually closed companion remains closed on next quick-panel open");
            Panel()!.Close();
            Check(manager.Sources.Count == 2, "audio lifetimes are independent of repeated panel close and reopen");
            Console.WriteLine($"PASS {_checks} visible companion lifecycle assertions; source states are fixtures, no audio capture started.");
            return 0;
        }
        finally
        {
            var current = Panel();
            if (current != null)
            {
                var load = (Task?)typeof(QuickPanelModernWindow).GetField("_loadTask", Hidden)!.GetValue(current);
                Console.WriteLine($"LIFECYCLE visible={current.IsVisible} pos={current.Left},{current.Top} ready={typeof(QuickPanelModernWindow).GetField("_contentReady", Hidden)!.GetValue(current)} phone={Phone()?.IsVisible} saved={config.QuickPanelShowPhoneAudio} load={load?.Status} failure={load?.Exception}");
            }
            Panel()?.Close(); Pump(manager.StopAllAsync());
        }
    }
    private static int LiveMulti(AndroidAudioManager manager, Catalog catalog, string output, string[] args)
    {
        var panel = new QuickPanelModernWindow();
        var apps = new[] { "cn.wenyu.bodian", "com.netease.cloudmusic", "tv.danmaku.bili" }.Select(p => catalog.Apps.Single(a => a.Package == p)).ToArray();
        var started = new List<AndroidAudioSource>();
        var snapshots = new List<object>();
        int index = Array.IndexOf(args, "--seconds"); int seconds = index >= 0 ? int.Parse(args[index + 1]) : 30;
        Exception? failure = null;
        try
        {
            foreach (var app in apps)
            {
                var drag = Drop(panel, catalog.Device, app);
                Until(() =>
                {
                    var source = manager.Sources.FirstOrDefault(s => s.App.Package == app.Package);
                    if (source?.Session.State.State == CaptureState.Failed) throw new Exception(source.Session.State.Error);
                    return source?.Session.State.State == CaptureState.Receiving;
                });
                var source = manager.Sources.Single(s => s.App.Package == app.Package); source.Session.SetVolume(25); started.Add(source);
                Check(drag.Handled && drag.Effects == DragDropEffects.Copy && started.All(s => s.Session.State.State == CaptureState.Receiving),
                    "adding " + app.Name + " through the viewport preserves all previous real captures");
            }
            Check(started.Select(s => s.Session.Pcm).Distinct().Count() == 3, "three real applications own three separate PCM sources");
            var first = started[0]; var second = started[1];
            var list = (ItemsControl)panel.FindName("AppListPanel");
            Descendants<Slider>((DependencyObject)list.Items[0]).Single().Value = 17;
            var icon = Descendants<Button>((DependencyObject)list.Items[0]).Single(b => b.ToolTip?.ToString()?.Contains("手机音频") == true);
            icon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(first.Session.Pcm!.Volume == 0 && second.Session.Pcm!.Volume == 0.25f, "real multi-source row mute leaves the second stream gain unchanged");
            icon.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(first.Session.Pcm.Volume == 0.17f, "real source unmute restores only its own slider value");
            for (int elapsed = 0; elapsed < seconds; elapsed += 10)
            {
                Pump(Task.Delay(TimeSpan.FromSeconds(Math.Min(10, seconds - elapsed))));
                Check(started.All(s => s.Session.State.State == CaptureState.Receiving && s.Session.PacketsDecoded > 0 && s.Session.Pcm!.SamplesConsumed > 0),
                    "all independent real streams decode and render at " + Math.Min(elapsed + 10, seconds) + "s");
                Console.WriteLine(string.Join("; ", started.Select(s => $"{s.App.Name}: packets={s.Session.PacketsDecoded} nonSilent={s.Session.Pcm!.NonSilentSamplesWritten}")));
            }
            foreach (var source in started) snapshots.Add(new { source.App.Package, source.App.Uid, Packets = source.Session.PacketsDecoded,
                NonSilent = source.Session.Pcm!.NonSilentSamplesWritten, Consumed = source.Session.Pcm.SamplesConsumed, Log = source.Session.GetLog() });
            long before = first.Session.PacketsDecoded;
            var middleRow = (DependencyObject)list.Items[1]; Descendants<ToggleButton>(middleRow).Single().IsChecked = true;
            Descendants<Button>(middleRow).Single(b => b.Content?.ToString() == "退出").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => second.Session.State.State == CaptureState.Idle);
            Pump(Task.Delay(1500));
            Check(manager.Sources.Count == 2 && first.Session.PacketsDecoded > before && started[2].Session.State.State == CaptureState.Receiving,
                "native-style exit removes only the middle real stream while the others continue");
            Pump(manager.StopAllAsync());
            Check(manager.Sources.Count == 0 && started.All(s => s.Session.Pcm == null), "stop all releases all real source lifetimes");
            Console.WriteLine($"PASS {_checks} full WPF multi-app assertions.");
            return 0;
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            Pump(manager.StopAllAsync()); panel.Close();
            string path = Path.Combine(output, "live-multi-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Success = failure == null, Error = failure?.ToString(), Seconds = seconds,
                Streams = snapshots, FinalCount = manager.Sources.Count, Log = manager.GetLog(),
                Evidence = "Three independent per-UID server/socket/decoder/PCM/render paths through the full WPF drop handler. Concurrent audible phone playback and human listening are separate checks." }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Evidence: " + path);
        }
    }
    private static DragEventArgs Drop(QuickPanelModernWindow panel, AndroidDevice device, AndroidApp app)
    {
        var scroll = (ScrollViewer)panel.FindName("AppListScroll");
        var data = new DataObject(AndroidAppDrag.Format, new AndroidAppDrag(device, app));
        var drag = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object[] { data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, scroll, new Point(10, 10) }, null)!;
        drag.RoutedEvent = System.Windows.DragDrop.PreviewDropEvent; scroll.RaiseEvent(drag); return drag;
    }
    private static void Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI/device operation did not settle."); Pump(Task.Delay(50)); }
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL " + label); _checks++; Console.WriteLine("PASS " + label); }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void DrainUi() => Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
        if (root is FrameworkElement element) foreach (object child in LogicalTreeHelper.GetChildren(element))
            if (child is DependencyObject dep && VisualTreeHelper.GetChildrenCount(root) == 0) foreach (var item in Descendants<T>(dep)) yield return item;
    }
    private static void Render(FrameworkElement element, int width, int height, string path)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32); bitmap.Render(element);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
    }
    private sealed record Catalog(AndroidDevice Device, List<AndroidApp> Apps);
}
