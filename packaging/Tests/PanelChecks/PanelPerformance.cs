using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckPanelPerformance(MethodInfo theme, List<Window> windows)
    {
        theme.Invoke(null, new object[] { "light", "blue" });
        var panel = new QuickPanelModernWindow(); windows.Add(panel);
        void Set(object target, string name, object? value) => target.GetType().GetField(name, Instance)!.SetValue(target, value);
        object? Invoke(string name, params object?[] values) => panel.GetType().GetMethod(name, Instance)!.Invoke(panel, values);
        var writes = new ConcurrentQueue<(string? Id, int Percent)>();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Func<string?, int, (bool, int)> write = (id, value) =>
        {
            writes.Enqueue((id, value));
            if (value == 1) { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); }
            return (true, value);
        };
        Set(panel, "_writeEndpointVolume", write); Set(panel, "_systemDeviceId", "performance-device-a");
        Set(panel, "_systemReady", true);
        var slider = (Slider)panel.FindName("VolumeSlider");
        try
        {
            slider.Value = 1;
            Check(entered.Wait(4000), "system slider first input starts immediately");
            for (int i = 2; i <= 100; i++)
            {
                slider.Value = i;
                if (i % 10 == 0 && i < 100) Invoke("VolDebounce_Tick", null, EventArgs.Empty);
            }
            Check(writes.Count == 1 && Field<(string?, int, int, int)?>(panel, "_pendingSystemVolume")!.Value.Item2 == 100,
                "99 following changes coalesce to the latest value");
            Invoke("VolDebounce_Tick", null, EventArgs.Empty);
            release.Set(); Finish(Field<Task>(panel, "_volumeWriteTask")); DrainUi();
            Check(writes.Select(w => w.Percent).SequenceEqual(new[] { 1, 100 }) && slider.Value == 100,
                "serialized writes preserve final value and reject old readback");
            Invoke("VolDebounce_Tick", null, EventArgs.Empty);
            Check(!Field<DispatcherTimer>(panel, "_volDebounce").IsEnabled, "volume timer stops after pending work settles");
            Console.WriteLine("SYSTEM_WRITE_METRIC inputs=100 writes=2 firstImmediate=true finalConfirmed=true");

            Invoke("CreateOutputPopupContent");
            var devices = new List<AudioDeviceInfo> { new() { Id = "performance-device-a", DisplayName = "A" } };
            Invoke("BuildOutputVolumeRows", devices, devices[0].Id);
            var extra = Field<IDictionary>(panel, "_outputVolumeRows")[devices[0].Id]!;
            Invoke("ApplyOutputVolumeState", extra, 100, false);
            var extraSlider = Field<Slider>(extra, "Slider");
            extraSlider.Value = 52;
            Check(Field<IDictionary>(panel, "_pendingOutputVolume").Count == 1, "extra slider queues its own pending value");
            slider.Value = 61; // immediate main value supersedes unflushed extra value
            Finish(Field<Task>(panel, "_volumeWriteTask")); DrainUi();
            Check(!writes.Any(w => w.Percent == 52) && !Field<bool>(extra, "Pending") && extraSlider.Value == 61,
                "main input supersedes older extra input on the same device");
            slider.Value = 62; // now throttled
            extraSlider.Value = 63;
            Check(Field<(string?, int, int, int)?>(panel, "_pendingSystemVolume") == null, "extra input supersedes older pending main input");
            Invoke("FlushPendingVolumes"); Finish(Field<Task>(panel, "_volumeWriteTask")); DrainUi();
            Check(!writes.Any(w => w.Percent == 62) && slider.Value == 63, "shared queue confirms latest extra value to main slider");

            Set(panel, "_systemVolumeThrottled", true);
            slider.Value = 64;
            Layout((FrameworkElement)panel.Content, 372, 450); slider.ApplyTemplate();
            var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
            track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
            Finish(Field<Task>(panel, "_volumeWriteTask")); DrainUi();
            Check(writes.Last().Percent == 64 && Field<(string?, int, int, int)?>(panel, "_pendingSystemVolume") == null,
                "drag completion commits final pending value");
            slider.Value = 65; slider.Value = 66;
            panel.Close(); Finish(Field<Task>(panel, "_volumeWriteTask"));
            Check(writes.Last().Percent == 66 && Field<DispatcherTimer?>(panel, "_volDebounce") == null,
                "close commits final value independently of canceled UI");
        }
        finally { release.Set(); panel.Close(); }

        var viewport = new QuickPanelModernWindow(); windows.Add(viewport);
        var rows = Field<IDictionary>(viewport, "_rows");
        var items = (ItemsControl)viewport.FindName("AppListPanel");
        var peaks = new Dictionary<int, float>();
        for (int i = 0; i < 20; i++)
        {
            var row = CreateRowNameProbe(viewport, true, false);
            Set(row, "App", new AudioAppInfo { ProcessId = (uint)(9000 + i), ProcessName = "ViewportProbe" });
            var volume = Field<Slider>(row, "Slider"); volume.Height = 32; volume.IsEnabled = true;
            Set(row, "PeakVisual", new Border()); Set(row, "PeakTransform", new ScaleTransform());
            Set(row, "ExpandWrap", new AnimatedExpandHost()); Set(row, "ExpandPanel", new StackPanel());
            var root = new StackPanel { Height = 60 }; root.Children.Add(volume);
            Set(row, "RootElement", root); items.Items.Add(root); rows.Add(9000 + i, row); peaks.Add(9000 + i, .75f);
        }
        Set(viewport, "_meterEnabled", true); Set(viewport, "_displayPeaks", peaks);
        void ViewportRead() => viewport.GetType().GetMethod("RefreshMeterViewport", Instance)!.Invoke(viewport, null);
        Layout((FrameworkElement)viewport.Content, 372, 450); DrainUi(); ViewportRead();
        int visible = Field<IList>(viewport, "_visibleMeterRows").Count;
        Check(visible > 0 && visible < rows.Count && Field<float>(rows[9019]!, "PeakLevel") == 0f,
            "offscreen rows receive no peak visual update");
        Check(Field<float>(rows[9000]!, "PeakLevel") == .75f, "visible rows consume latest peaks");
        var scroll = (ScrollViewer)viewport.FindName("AppListScroll"); scroll.ScrollToEnd();
        Layout((FrameworkElement)viewport.Content, 372, 450); DrainUi(); ViewportRead();
        Check(Field<float>(rows[9019]!, "PeakLevel") == .75f, "scrolling into view restores current peak immediately");
        peaks[9000] = .25f; peaks[9019] = .25f; ViewportRead();
        Check(Field<float>(rows[9000]!, "PeakLevel") == .75f && Field<float>(rows[9019]!, "PeakLevel") == .25f
            && Field<object?>(viewport, "_meter") == null, "viewport changes do not rebuild or start audio sampling");
        Console.WriteLine($"VIEWPORT_METRIC total=20 visible={visible} offscreenVisualUpdates=0");
        viewport.Close();

        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        AppItem FailIcon() => throw new InvalidOperationException("Canceled batch was enumerated");
        IEnumerable<AppItem> Unavailable() { yield return FailIcon(); }
        AppItem.LoadIconsAsync(Unavailable(), canceled.Token);
        Check(true, "canceled icon batch returns before enumerating or extracting");
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(App).Assembly.Location)!, "SonicRoute.exe");
        var info = new AudioAppInfo { ProcessId = int.MaxValue - 400, ProcessName = "CanceledIconProbe", IconPath = path };
        var item = AppItem.From(info);
        using var cancellation = new CancellationTokenSource();
        var loadIcon = typeof(AppItem).GetMethod("LoadIcon", Instance, null, new[] { typeof(CancellationToken) }, null)!;
        // Block the UI dispatcher until extraction has queued its result, then cancel before delivery.
        Task.Run(() => loadIcon.Invoke(item, new object[] { cancellation.Token })).GetAwaiter().GetResult();
        Check(Field<int>(item, "_iconLoading") == 1, "icon result queued while UI dispatcher is held");
        cancellation.Cancel(); DrainUi();
        Check(item.Icon == null && Field<int>(item, "_iconLoading") == 0, "queued icon result ignores canceled panel and clears in-flight flag");
        var other = AppItem.From(info); other.LoadIcon();
        Check(other.Icon != null, "canceling a panel does not cancel the shared icon cache or another owner");
    }
}
