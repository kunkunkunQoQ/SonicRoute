using System.Diagnostics;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    // One harness runs unchanged against the preserved before and after product assemblies.
    // All windows stay offscreen; no App.Run, real OLE drag, ADB, playback or global input.
    private static int InteractionAudit(string directory, string label)
    {
        string output = Path.GetFullPath(directory); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID", "InteractionAudit");
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ =>
        {
            scans++;
            return Task.FromResult<IReadOnlyList<AndroidDevice>>(Array.Empty<AndroidDevice>());
        });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        long bootstrapAllocated = GC.GetAllocatedBytesForCurrentThread();
        var bootstrapClock = Stopwatch.StartNew();
        var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        L10n.Instance.SetLanguage("zh-CN"); ThemeService.Apply("light", "#00CBE9");
        bootstrapClock.Stop();
        var bootstrap = new { WallMs = bootstrapClock.Elapsed.TotalMilliseconds,
            AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - bootstrapAllocated };
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        bool Created(string name) => (bool)host.GetProperty(name, statics)!.GetValue(null)!;
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        var device = new AndroidDevice("offline-fixture", "Fixture", "device", true, "audit-hardware", "audit-transport");
        discovery.Publish([device]);
        store.RememberDevice(device);
        store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker,
            new AndroidOutputSink("bt:8:audit", AndroidOutputSinkKind.Bluetooth, 4, 8, "Fixture headphones")]);
        byte[] icon = MakeIcon();
        var phoneApps = Enumerable.Range(0, 30).Select(i => new AndroidApp("cn.audit.app" + i,
            "App " + i, 2000 + i, "media", icon)).ToArray();
        store.RetainMany(phoneApps); store.FlushAsync().GetAwaiter().GetResult();

        var firstClock = Stopwatch.StartNew(); long firstAllocated = GC.GetAllocatedBytesForCurrentThread();
        var first = CreateAuditPhone(device); first.Close(); DrainAuditUi();
        firstClock.Stop();
        var firstPanel = new { WallMs = firstClock.Elapsed.TotalMilliseconds,
            AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - firstAllocated };
        var metrics = new List<Metric>();
        var weakWindows = new List<WeakReference>();
        metrics.Add(Measure("phone-open-bind-close", 20,
            "20 offscreen window constructions with 30 cached apps, theme, device binding and layout; media/ADB disabled", () =>
        {
            int count = 0;
            for (int i = 0; i < 20; i++)
            {
                var panel = CreateAuditPhone(device);
                count += ((ListBox)panel.FindName("PhoneApps")).Items.Count;
                weakWindows.Add(new WeakReference(panel));
                panel.Close();
            }
            DrainAuditUi();
            return count;
        }));

        foreach (var saved in phoneApps.Skip(6)) store.SetQuickPanelVisible(saved.Package, false);
        store.FlushAsync().GetAwaiter().GetResult();
        metrics.Add(Measure("phone-open-six-apps", 20,
            "20 offscreen constructions with six cached visible apps; 30 saved, remainder hidden", () =>
        {
            int count = 0;
            for (int i = 0; i < 20; i++)
            {
                var panel = CreateAuditPhone(device);
                count += ((ListBox)panel.FindName("PhoneApps")).Items.Count;
                panel.Close();
            }
            DrainAuditUi();
            return count;
        }));
        foreach (var saved in phoneApps.Skip(6)) store.SetQuickPanelVisible(saved.Package, true);
        store.FlushAsync().GetAwaiter().GetResult();

        var phone = CreateAuditPhone(device);
        phone.Show(); DrainAuditUi();
        var root = (FrameworkElement)phone.Content;
        void Layout() { root.Measure(new Size(235, 450)); root.Arrange(new Rect(0, 0, 235, 450)); root.UpdateLayout(); }
        var drag = typeof(PhoneAudioWindow).GetMethod("PhoneApp_DragOver", Hidden)!
            .CreateDelegate<Action<object, DragEventArgs>>(phone);
        var data = new System.Windows.DataObject("SonicRoute.PcApp.v1", new AudioAppInfo
            { ProcessId = 42424, ProcessName = "fixture", DisplayName = "Fixture" });
        DragEventArgs DragAt(Point point)
        {
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), Hidden, null,
                [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move, phone, point], null)!;
            args.RoutedEvent = System.Windows.DragDrop.PreviewDragOverEvent;
            return args;
        }
        drag(phone, DragAt(new Point(80, 180))); Layout(); DrainAuditUi();
        var overlay = (Border)phone.FindName("PcDropHighlight");
        var top = DragAt(overlay.TranslatePoint(new Point(40, overlay.ActualHeight * .25), phone));
        var bottom = DragAt(overlay.TranslatePoint(new Point(40, overlay.ActualHeight * .75), phone));
        var activeZone = typeof(PhoneAudioWindow).GetField("_pcDropTargetIndex", Hidden)!;
        drag(phone, top);
        if (!Equals(activeZone.GetValue(phone), 0)) throw new InvalidOperationException("Top drag point was not in zone zero.");
        drag(phone, bottom);
        if (!Equals(activeZone.GetValue(phone), 1)) throw new InvalidOperationException("Bottom drag point was not in zone one.");
        drag(phone, top); Layout();
        var dragController = typeof(App).Assembly.GetType("SonicRoute.AppIconDragController")!;
        var screenPoint = dragController.GetNestedType("ScreenPoint", BindingFlags.NonPublic)!;
        var pointX = Expression.Parameter(typeof(int), "x");
        var pointY = Expression.Parameter(typeof(int), "y");
        var pointValue = Expression.MemberInit(Expression.New(screenPoint),
            Expression.Bind(screenPoint.GetField("X", Hidden)!, pointX),
            Expression.Bind(screenPoint.GetField("Y", Hidden)!, pointY));
        var hitMethods = dragController.GetMethods(statics).Where(m => m.Name == "InsidePanel").ToArray();
        var cachedHit = hitMethods.FirstOrDefault(m => m.GetParameters().Length == 2);
        Expression hitCall;
        if (cachedHit != null)
        {
            var panels = (Window[])dragController.GetMethod("CapturePanelWindows", statics)!.Invoke(null, null)!;
            hitCall = Expression.Call(cachedHit, pointValue, Expression.Constant(panels));
        }
        else hitCall = Expression.Call(hitMethods.Single(m => m.GetParameters().Length == 1), pointValue);
        var hitTest = Expression.Lambda<Func<int, int, bool>>(hitCall, pointX, pointY).Compile();
        metrics.Add(Measure("drag-feedback-panel-hit-test", 5000,
            "5000 native bounds hit tests outside all panels; baseline reads Application.Windows per callback, new version captures panel references once per gesture; cursor/preview/OLE excluded", () =>
        {
            int inside = 0;
            for (int i = 0; i < 5000; i++) if (hitTest(20000, 20000)) inside++;
            if (inside != 0) throw new InvalidOperationException("Outside feedback point was inside a panel.");
            return inside;
        }));
        metrics.Add(Measure("pc-drag-same-zone", 5000,
            "5000 synthetic DragOver callbacks in one zone; reuse event/payload, no OLE or mouse input", () =>
        {
            for (int i = 0; i < 5000; i++) drag(phone, top);
            return ((Grid)phone.FindName("PcDropZonesGrid")).RowDefinitions.Count;
        }));
        metrics.Add(Measure("pc-drag-cross-zone-layout", 600,
            "600 alternating drop zones plus layout; same cached phone and two outputs", () =>
        {
            for (int i = 0; i < 600; i++) { drag(phone, (i & 1) == 0 ? top : bottom); Layout(); }
            return ((Grid)phone.FindName("PcDropZonesGrid")).RowDefinitions.Count;
        }));

        var applyMedia = typeof(PhoneAudioWindow).GetMethod("ApplyMediaState", Hidden)!
            .CreateDelegate<Action<AndroidMediaState?>>(phone);
        var state = new AndroidMediaState(phoneApps[0].Package, "Track", "Artist", true, 822,
            PositionMs: 12000, DurationMs: 180000) { PlayingPackages = [phoneApps[0].Package] };
        applyMedia(state); Layout();
        metrics.Add(Measure("media-ui-identical", 2000,
            "2000 identical media UI snapshots with 30 app rows; artwork and output references reused", () =>
        {
            for (int i = 0; i < 2000; i++) applyMedia(state);
            return ((ListBox)phone.FindName("PhoneApps")).Items.Count;
        }));
        var states = Enumerable.Range(0, 2000).Select(i => state with { PositionMs = i * 10 }).ToArray();
        metrics.Add(Measure("media-ui-position-only", states.Length,
            "2000 preallocated position-only media snapshots; same track, playing packages and artwork", () =>
        {
            foreach (var update in states) applyMedia(update);
            return ((ListBox)phone.FindName("PhoneApps")).Items.Count;
        }));
        var outputs = host.GetMethod("GetOutputDevices", statics)!.CreateDelegate<Func<IReadOnlyList<AudioDeviceInfo>>>();
        metrics.Add(Measure("phone-output-snapshot", 5000,
            "5000 unified phone-output reads with unchanged cached profile/discovery; no Windows device enumeration", () =>
        {
            int total = 0;
            for (int i = 0; i < 5000; i++) total += outputs().Count;
            return total;
        }));

        phone.Show(); DrainAuditUi();
        using var mediaRead = new CancellationTokenSource();
        typeof(PhoneAudioWindow).GetField("_mediaRead", Hidden)!.SetValue(phone, mediaRead);
        var queueMethod = typeof(PhoneAudioWindow).GetMethod("QueueMediaState", Hidden);
        var queue = queueMethod?.CreateDelegate<Action<AndroidMediaState, CancellationTokenSource>>(phone);
        var burst = Enumerable.Range(0, 1000).Select(i => state with { Title = "Burst " + i }).ToArray();
        metrics.Add(Measure("media-burst-dispatch", burst.Length,
            "1000 updates received before dispatcher renders; baseline uses the original inline BeginInvoke callback, new version its extracted queue", () =>
        {
            foreach (var update in burst)
            {
                if (queue != null) queue(update, mediaRead);
                else phone.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!mediaRead.IsCancellationRequested) applyMedia(update);
                }));
            }
            DrainAuditUi();
            if (((TextBlock)phone.FindName("MediaTitle")).Text != "Burst 999")
                throw new InvalidOperationException("Media burst lost its latest snapshot.");
            return burst.Length;
        }));
        phone.Close(); DrainAuditUi();

        var lifecycle = new List<WeakReference>();
        int windowsBefore = app.Windows.Count;
        long memoryBefore = SettledAuditMemory();
        for (int i = 0; i < 15; i++) lifecycle.Add(AuditShowHideClose(device));
        DrainAuditUi(); long memoryAfter = SettledAuditMemory();
        int windowsAfter = app.Windows.Count, retainedWindows = lifecycle.Count(w => w.IsAlive);
        int retainedConstructed = weakWindows.Count(w => w.IsAlive);
        var report = new
        {
            Created = DateTimeOffset.Now, Label = label, Framework = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
            ProductHash = Hash(typeof(App).Assembly.Location), BackendHash = Hash(typeof(AndroidAudioManager).Assembly.Location),
            HarnessHash = Hash(typeof(Program).Assembly.Location), Bootstrap = bootstrap, FirstPanel = firstPanel,
            Metrics = metrics,
            Lifecycle = new { Repetitions = 15, RetainedWindows = retainedWindows, WindowsBefore = windowsBefore,
                WindowsAfter = windowsAfter, RetainedConstructed = retainedConstructed,
                ManagedBytesBefore = memoryBefore, ManagedBytesAfter = memoryAfter,
                MainWindowCleared = app.MainWindow == null,
                MainWindowType = app.MainWindow?.GetType().FullName,
                RemainingWindows = app.Windows.Cast<Window>().Select(w => new { Type = w.GetType().FullName, w.IsVisible }).ToArray() },
            Scans = scans, AdbCreated = Created("IsAdbCreated"), AudioCreated = Created("IsCreated"),
            DisplaysCreated = Created("AreDisplaysCreated"), PcToPhoneCreated = Created("IsPcToPhoneCreated"),
            Scope = "Five-round warmed offline component measurements with the same harness. Offscreen fake devices and 30 retained apps. No real phone, audio, global input or product App.Run. Cold first-panel/managed-memory observations are not whole-product benchmarks."
        };
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var metric in metrics)
        {
            var wall = metric.Trials.Select(t => t.WallMs).Order().ToArray();
            var alloc = metric.Trials.Select(t => t.AllocatedBytes).Order().ToArray();
            Console.WriteLine($"PERF {metric.Name} operations={metric.Operations} medianMs={wall[wall.Length / 2]:F3} allocatedBytes={alloc[alloc.Length / 2]} checksums={string.Join(',', metric.Trials.Select(t => t.Checksum))}");
        }
        Console.WriteLine($"LIFECYCLE retained={retainedWindows}/{lifecycle.Count} constructed={retainedConstructed}/{weakWindows.Count} windows={windowsBefore}->{windowsAfter} scans={scans}");
        if (scans != 0 || Created("IsAdbCreated") || Created("IsCreated") || Created("AreDisplaysCreated") || Created("IsPcToPhoneCreated"))
            throw new InvalidOperationException("Offline audit unexpectedly started a phone worker.");
        return 0;
    }

    private static PhoneAudioWindow CreateAuditPhone(AndroidDevice device)
    {
        var phone = new PhoneAudioWindow(false)
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        typeof(PhoneAudioWindow).GetMethod("BindDevices", Hidden)!.Invoke(phone, [new[] { device }, null]);
        typeof(PhoneAudioWindow).GetMethod("RetainedAppsChanged", Hidden)!.Invoke(phone, null);
        var root = (FrameworkElement)phone.Content;
        root.Measure(new Size(235, 450)); root.Arrange(new Rect(0, 0, 235, 450)); root.UpdateLayout();
        return phone;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AuditShowHideClose(AndroidDevice device)
    {
        var phone = CreateAuditPhone(device);
        phone.Show(); DrainAuditUi(); phone.Hide(); phone.Close();
        return new WeakReference(phone);
    }
    private static void DrainAuditUi()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static long SettledAuditMemory()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
}
