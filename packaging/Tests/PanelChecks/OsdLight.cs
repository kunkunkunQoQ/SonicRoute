using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static object? OsdCall(object osd, string method, params object?[] arguments) =>
        osd.GetType().GetMethod(method, Instance)!.Invoke(osd, arguments);

    private static void CheckOsdLightLive(string output, bool baseline)
    {
        var config = ConfigService.Load();
        config.ShowAppPeakMeter = false;
        config.QuickPanelShowOutputName = config.QuickPanelCompactShowOutputName = false;
        config.QuickPanelShowAllOutputVolumes = false;
        config.OsdVolumeVisualEnabled = true;
        config.OsdFadeInMs = 60;
        config.OsdFadeOutMs = 80;
        config.MicMuteOsdPersistent = true;
        var theme = typeof(App).Assembly.GetType("SonicRoute.ThemeService")!.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(string) }, null)!;
        theme.Invoke(null, new object[] { "dark", "cyan" });
        var osd = Activator.CreateInstance(typeof(App).Assembly.GetType("SonicRoute.OsdService")!, true)!;
        var process = Process.GetCurrentProcess();
        try
        {
            OsdCall(osd, "ShowVolume", "OSD fixture", 25, "25%", false, "fixture");
            Finish(Task.Delay(100));
            var scale = Field<ScaleTransform>(osd, "_fillScale");
            var descriptor = DependencyPropertyDescriptor.FromProperty(ScaleTransform.ScaleXProperty, typeof(ScaleTransform));
            var changes = new List<double>();
            var clock = Stopwatch.StartNew();
            EventHandler changed = (_, _) => changes.Add(clock.Elapsed.TotalMilliseconds);
            descriptor.AddValueChanged(scale, changed);
            process.Refresh();
            var cpu = process.TotalProcessorTime;
#if NET48
            long allocated = 0;
#else
            long allocated = GC.GetAllocatedBytesForCurrentThread();
#endif
            var inputTime = TimeSpan.Zero;
            for (int burst = 0; burst < 8; burst++)
            {
                var input = Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) OsdCall(osd, "ShowVolume", "OSD fixture", (i + burst * 9) % 101, "volume", false, "fixture");
                input.Stop(); inputTime += input.Elapsed;
                Finish(Task.Delay(120));
            }
            process.Refresh();
#if !NET48
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
#endif
            var metric = new
            {
                Baseline = baseline, Runtime = Environment.Version.ToString(), Inputs = 800,
                InputWallMs = inputTime.TotalMilliseconds, ProcessCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                UiThreadAllocatedBytes = allocated, AnimatedValueChanges = changes.Count,
                ChangeIntervalsMs = changes.Zip(changes.Skip(1), (a, b) => b - a).ToArray(),
                Scope = "Synthetic OSD input on a private desktop; no endpoint writes, no whole-product CPU or GPU claim."
            };
            descriptor.RemoveValueChanged(scale, changed);
            File.WriteAllText(Path.Combine(output, "osd-metric.json"), JsonSerializer.Serialize(metric, new JsonSerializerOptions { WriteIndented = true }));
            Check(Field<TextBlock>(osd, "_percent").Text == ((99 + 7 * 9) % 101) + "%", "OSD renders the final value of a repeated input burst");
            if (!baseline) CheckOsdLightBoundaries(osd);
        }
        finally { ((IDisposable)osd).Dispose(); process.Dispose(); }

        foreach (bool compact in new[] { false, true }) CheckVirtualizedRowsLive(config, output, compact, baseline);
        if (!baseline) CheckIdleCacheTransitions();
        File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new
        {
            Success = true, Baseline = baseline, Runtime = Environment.Version.ToString(), Assertions = _checks,
            ProductStartup = false, PrivateDesktop = true, AudioWrites = "fixtures only", PhoneAccess = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CheckOsdLightBoundaries(object osd)
    {
        OsdCall(osd, "ShowVolume", "First", 41, "41%", false, "first");
        Check(Field<TextBlock>(osd, "_percent").Text == "41%", "first OSD response remains immediate");
        for (int i = 42; i <= 84; i++) OsdCall(osd, "ShowVolume", "First", i, "volume", false, "first");
        var pending = Field<DispatcherOperation>(osd, "_noticeOperation");
        Check(Field<bool>(osd, "_noticePending") && pending.Status == DispatcherOperationStatus.Pending
            && Field<TextBlock>(osd, "_percent").Text == "41%", "same-target burst reuses one pending UI presentation");
        OsdCall(osd, "ShowVolume", "Other", 12, "12%", false, "other");
        Check(!Field<bool>(osd, "_noticePending") && pending.Status == DispatcherOperationStatus.Aborted
            && Field<TextBlock>(osd, "_percent").Text == "12%", "target changes cancel the stale queued value and display immediately");
        OsdCall(osd, "ShowVolume", "Other", 19, "19%", false, "other");
        DrainUi();
        Check(Field<TextBlock>(osd, "_percent").Text == "19%" && Field<object?>(osd, "_noticeOperation") == null,
            "one render turn presents the last value and releases the pending operation");

        var readField = osd.GetType().GetField("_readMicState", Instance)!;
        readField.SetValue(osd, (Func<bool, Task<(bool Muted, bool Known)>>)(_ => Task.FromResult((false, true))));
        OsdCall(osd, "ShowMic", "Mic", true);
        OsdCall(osd, "Show", "Temporary", "test");
        OsdCall(osd, "Hide");
        Finish((Task)OsdCall(osd, "RestoreMicAsync")!);
        Check(!Field<bool>(osd, "_micPersistent") && Field<DispatcherTimer>(osd, "_idleReleaseTimer").IsEnabled,
            "confirmed unmute after a temporary notice schedules hidden-window release");
        OsdCall(osd, "IdleReleaseTimer_Tick", null, EventArgs.Empty);
        Check(Field<Window?>(osd, "_window") == null, "idle release closes the native OSD window and drops its visual references");
        Check(Application.Current.MainWindow == null, "a first OSD window is not retained as the application's closed main window");

        readField.SetValue(osd, (Func<bool, Task<(bool Muted, bool Known)>>)(_ => Task.FromResult((false, false))));
        OsdCall(osd, "ShowMic", "Mic", true);
        OsdCall(osd, "Show", "Temporary", "test");
        OsdCall(osd, "Hide");
        Finish((Task)OsdCall(osd, "RestoreMicAsync")!);
        Check(Field<bool>(osd, "_micPersistent") && Field<bool>(osd, "_showingMic") && Field<Window>(osd, "_window").IsVisible,
            "an unknown microphone read preserves the previously confirmed mute banner");

        var delayed = new TaskCompletionSource<(bool Muted, bool Known)>(TaskCreationOptions.RunContinuationsAsynchronously);
        readField.SetValue(osd, (Func<bool, Task<(bool Muted, bool Known)>>)(_ => delayed.Task));
        OsdCall(osd, "Show", "Temporary", "test"); OsdCall(osd, "Hide");
        var restore = (Task)OsdCall(osd, "RestoreMicAsync")!;
        OsdCall(osd, "ShowVolume", "Newer", 64, "64%", false, "newer");
        delayed.SetResult((false, true)); Finish(restore);
        Check(Field<TextBlock>(osd, "_title").Text == "Newer", "a late microphone result cannot replace a newer notification");

        var previousMode = RenderOptions.ProcessRenderMode;
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            int duration = (int)osd.GetType().GetMethod("FadeDuration", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { 400 })!;
            Check(duration <= 80, "software rendering bounds fade duration without adding a timer");
            OsdCall(osd, "ShowVolume", "Software", 20, "20%", false, "software");
            OsdCall(osd, "ShowVolume", "Software", 80, "80%", false, "software"); DrainUi();
            Check(!Field<bool>(osd, "_barAnimating") && Field<ScaleTransform>(osd, "_fillScale").ScaleX == .8,
                "software rendering displays the latest volume without a bar animation loop");
        }
        finally { RenderOptions.ProcessRenderMode = previousMode; }
        OsdCall(osd, "ShowVolume", "Software", 53, "53%", false, "software");
        ((IDisposable)osd).Dispose(); DrainUi();
        Check(Field<object?>(osd, "_noticeOperation") == null && Field<Window?>(osd, "_window") == null,
            "dispose cancels pending notices, animation handlers and the OSD window");
    }

    private static void CheckVirtualizedRowsLive(AppConfig config, string output, bool compact, bool baseline)
    {
        config.QuickPanelUseLegacyAppRows = compact;
        var panel = new QuickPanelModernWindow();
        var writes = new List<(int Pid, int Volume)>();
        panel.GetType().GetField("_readPanelState", Instance)!.SetValue(panel,
            (Func<IEnumerable<int>, (Dictionary<int, (int Volume, bool Muted, bool MuteKnown)> Apps, bool? AllMuted)>)(ids =>
                (ids.ToDictionary(id => id, _ => (50, false, true)), false)));
        panel.GetType().GetField("_writeAppVolume", Instance)!.SetValue(panel, (Func<int, int, bool>)((pid, volume) =>
        { lock (writes) writes.Add((pid, volume)); return true; }));
        var endpoints = panel.GetType().GetField("_readRowEndpoints", Instance);
        endpoints?.SetValue(panel, (Func<int, bool, (string Output, string Input)>)((_, _) =>
            (AudioService.SystemDefaultDeviceId, AudioService.SystemDefaultInputDeviceId)));
        var content = (FrameworkElement)panel.Content;
        panel.Content = null;
        var host = new Window { Width = 372, Height = 450, ShowInTaskbar = false, ShowActivated = false, Content = content };
        var apps = Enumerable.Range(0, 200).Select(i => new AudioAppInfo
        { ProcessId = (uint)(int.MaxValue - 4000 - i), ProcessName = "RowFixture" + i, DisplayName = "Application " + i }).ToList();
        var build = panel.GetType().GetMethod("BuildAppRowsAsync", Instance)!;
        var watch = Stopwatch.StartNew();
        Finish((Task)build.Invoke(panel, new object[] { apps, config })!);
        host.Show(); host.UpdateLayout(); DrainUi();
        var rows = Field<IDictionary>(panel, "_rows");
        int Realized() => rows.Values.Cast<object>().Count(row => Field<FrameworkElement?>(row, "RootElement") != null);
        int realized = Realized(); watch.Stop();
        Check(rows.Count == 200 && realized > 0, "large application list keeps all business rows and presents a viewport");
        if (!baseline) Check(realized < 60, "large application list creates controls only around the viewport");
        var scroll = (ScrollViewer)panel.FindName("AppListScroll");
        if (!baseline && panel.FindName("AndroidDropHighlight") is Border overlay)
        {
            overlay.Visibility = Visibility.Visible; host.UpdateLayout(); DrainUi();
            Check(scroll.AllowDrop && scroll.IsVisible && ((ListBoxItem)((ListBox)panel.FindName("AppListPanel")).ItemContainerGenerator.ContainerFromIndex(0)).Visibility == Visibility.Hidden,
                "phone drag overlay hides application rows while retaining its live drop surface");
            overlay.Visibility = Visibility.Collapsed; host.UpdateLayout(); DrainUi();
        }
        var first = rows[(int)apps[0].ProcessId]!;
        var last = rows[(int)apps[199].ProcessId]!;
        var firstLifetime = first.GetType().GetField("VisualLifetime", Instance)?.GetValue(first) as CancellationTokenSource;
        var firstToken = firstLifetime?.Token;
        if (!baseline)
        {
            Field<Slider>(first, "Slider").Value = 73;
            OsdCall(panel, "FlushPendingVolumes"); Finish(Field<Task>(panel, "_volumeWriteTask")); DrainUi();
            Check(writes.Any(w => w.Pid == (int)apps[0].ProcessId && w.Volume == 73), "a realized slider commits its final value through the existing write queue");
            OsdCall(panel, "ApplyAppMuteReadback", (int)apps[199].ProcessId, true);
        }
        scroll.ScrollToEnd(); host.UpdateLayout(); DrainUi();
        OutputWait(() => Field<FrameworkElement?>(last, "RootElement") != null, "scrolling realizes the last application row");
        int scrolled = Realized();
        if (!baseline)
        {
            OutputWait(() => Field<FrameworkElement?>(first, "RootElement") == null, "offscreen controls are released after container recycling");
            Check(firstToken?.IsCancellationRequested == true && Field<Slider?>(first, "Slider") == null,
                "offscreen icon work is canceled and the old slider reference is dropped");
            Check(Field<bool>(last, "IsMuted") && Field<Border>(last, "MuteDot").Visibility == Visibility.Visible,
                "offscreen mute updates are restored when their row is realized");
        }
        scroll.ScrollToTop(); host.UpdateLayout(); DrainUi();
        OutputWait(() => Field<FrameworkElement?>(first, "RootElement") != null, "scrolling back realizes the original application");
        if (!baseline)
        {
            Check(Field<Slider>(first, "Slider").Value == 73 && Field<Dictionary<int, int>>(panel, "_pendingVolume").Count == 0,
                "recycled rows restore volume without writing it back to audio");
            var expanded = first.GetType().GetField("Expanded", Instance)!;
            expanded.SetValue(first, true);
            scroll.ScrollToEnd(); host.UpdateLayout(); DrainUi();
            scroll.ScrollToTop(); host.UpdateLayout(); DrainUi();
            OutputWait(() => Field<FrameworkElement?>(first, "RootElement") != null, "expanded row can be realized again");
            OutputWait(() => Field<StackPanel>(first, "ExpandPanel").Visibility == Visibility.Visible,
                "recycled expanded row restores its device controls");
            Check(Field<bool>(first, "Expanded"), "expanded state belongs to the application rather than a recycled container");
            Finish((Task)build.Invoke(panel, new object[] { apps.Take(4).ToList(), config })!); host.UpdateLayout(); DrainUi();
            Check(rows.Count == 4 && Realized() == 4, "shrinking a large list restores the small-list path without stale containers");
        }
        var image = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(Path.Combine(output, compact ? "rows-compact.png" : "rows-standard.png"))) encoder.Save(stream);
        File.WriteAllText(Path.Combine(output, compact ? "rows-compact.json" : "rows-standard.json"), JsonSerializer.Serialize(new
        { Baseline = baseline, Compact = compact, Rows = 200, InitiallyRealized = realized, ScrolledRealized = scrolled, BuildAndLayoutMs = watch.Elapsed.TotalMilliseconds }));
        panel.Close(); host.Close(); DrainUi();
        Check(rows.Count == 0, "closing the panel clears all application models and visual ownership");
    }

    private static void CheckIdleCacheTransitions()
    {
        var type = typeof(AppIconService);
        var cache = type.GetField("ExternalPngIcons", BindingFlags.Static | BindingFlags.NonPublic)!;
        type.GetMethod("Resume", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        type.GetMethod("TrimForIdle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        var first = cache.GetValue(null);
        type.GetMethod("TrimForIdle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        Check(ReferenceEquals(first, cache.GetValue(null)), "repeated idle requests reuse the already-retired icon cache");
        type.GetMethod("Resume", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        type.GetMethod("TrimForIdle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        Check(!ReferenceEquals(first, cache.GetValue(null)), "a new UI session can retire its decoded icon cache once again");
    }
}
