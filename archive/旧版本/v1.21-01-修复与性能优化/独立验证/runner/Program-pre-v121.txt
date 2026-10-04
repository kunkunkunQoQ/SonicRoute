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
        try
        {
            Config = new AppConfig { Language = "en-US", ThemeMode = "dark", MicMuteOsdPersistent = false };
            Config.Hotkeys = new Dictionary<string, string>(HotkeyActions.Defaults);
            typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
            typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
            if (!ConfigService.ConfigPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Storage isolation missing");
            Environment.SetEnvironmentVariable("SONICROUTE_PERF_ISOLATED", "1");
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
            if (mode == "all" || mode == "core-only") RunCore();
            if (mode == "peak-ui-only") RunPeakUi();
            else if (mode == "persistence-only") RunPersistenceCheck();
            else if (mode == "supplemental") { RunSupplementalCore(); RunPersistenceCheck(); RunWindows(true); }
            else if (mode == "windows-only") RunWindows();
            else if (mode != "core-only") RunUi();
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
