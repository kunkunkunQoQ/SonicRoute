using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static int InteractionAuditUi(string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ =>
        {
            scans++;
            return Task.FromResult<IReadOnlyList<AndroidDevice>>(Array.Empty<AndroidDevice>());
        });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        var device = new AndroidDevice("offline-fixture", "Fixture", "device", true, "interaction-audit", "fixture");
        discovery.Publish([device]); store.RememberDevice(device);
        var music = new AndroidApp("cn.audit.music", "Music", 1001, "media");
        store.RetainMany([music, music with { Package = "cn.audit.video", Name = "Video" }]);
        var bluetooth = new AndroidOutputSink("bt:8:fixture", AndroidOutputSinkKind.Bluetooth, 4, 8, "Headphones");
        store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker, bluetooth]);
        var phone = new PhoneAudioWindow(false)
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        object? Call(string name, params object?[] values) => typeof(PhoneAudioWindow).GetMethod(name, Hidden)!.Invoke(phone, values);
        var outputs = host.GetMethod("GetOutputDevices", statics)!.CreateDelegate<Func<IReadOnlyList<AudioDeviceInfo>>>();
        var reads = new List<CancellationTokenSource>();
        try
        {
            phone.Show(); DrainUi();
            Pump((Task)Call("InitializeDevicesAsync")!); DrainUi();
            var dragController = typeof(App).Assembly.GetType("SonicRoute.AppIconDragController")!;
            var panels = (Window[])dragController.GetMethod("CapturePanelWindows", statics)!.Invoke(null, null)!;
            var pointType = dragController.GetNestedType("ScreenPoint", BindingFlags.NonPublic)!;
            object point = Activator.CreateInstance(pointType)!;
            var screen = phone.PointToScreen(new Point(10, 10));
            pointType.GetField("X", Hidden)!.SetValue(point, (int)screen.X);
            pointType.GetField("Y", Hidden)!.SetValue(point, (int)screen.Y);
            var inside = dragController.GetMethods(statics).Single(m => m.Name == "InsidePanel" && m.GetParameters().Length == 2);
            Check(panels.Contains(phone) && (bool)inside.Invoke(null, [point, panels])!,
                "drag feedback captures current panels once and reads live native bounds");
            phone.Hide(); DrainUi();
            Check(!(bool)inside.Invoke(null, [point, panels])!, "cached drag references stop matching a panel as soon as it hides");
            phone.Show(); DrainUi();
            Call("EnsurePcDropZones", true);
            var zones = (Grid)phone.FindName("PcDropZonesGrid");
            var originalCells = zones.Children.Cast<UIElement>().ToArray();
            Check(zones.RowDefinitions.Count == 2, "cached drag zones include speaker and connected Bluetooth");
            var drag = typeof(PhoneAudioWindow).GetMethod("PhoneApp_DragOver", Hidden)!.CreateDelegate<Action<object, DragEventArgs>>(phone);
            var data = new System.Windows.DataObject("SonicRoute.PcApp.v1", new AudioAppInfo
                { ProcessId = 42424, ProcessName = "fixture", DisplayName = "Fixture" });
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), Hidden, null,
                [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move, phone, new Point(80, 180)], null)!;
            args.RoutedEvent = System.Windows.DragDrop.PreviewDragOverEvent;
            drag(phone, args);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) drag(phone, args);
            long dragBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(originalCells.SequenceEqual(zones.Children.Cast<UIElement>()) && dragBytes < 512 * 1024,
                "1000 unchanged hover callbacks reuse cells with bounded allocation");
            Call("EnsurePcDropZones", true);
            Check(originalCells.SequenceEqual(zones.Children.Cast<UIElement>()), "refreshing unchanged outputs keeps existing themed cells");
            store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]); DrainUi();
            Check(zones.RowDefinitions.Count == 1, "Bluetooth disconnect invalidates the active drag snapshot");
            store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker, bluetooth]); DrainUi();
            Check(zones.RowDefinitions.Count == 2, "Bluetooth reconnect rebuilds the drag snapshot");

            var initialOutputs = outputs();
            Check(ReferenceEquals(initialOutputs, outputs()), "unchanged output reads reuse their cached snapshot");
            store.Rename("Renamed phone"); DrainUi();
            Check(!ReferenceEquals(initialOutputs, outputs()) && outputs().All(d => d.DisplayName!.Contains("Renamed phone")),
                "phone rename invalidates cached output labels");
            discovery.Publish([device with { State = "offline" }]); DrainUi();
            Check(outputs().Count == 0, "offline discovery immediately removes cached phone outputs");
            discovery.Publish([device]); store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker, bluetooth]); DrainUi();
            Check(outputs().Count == 2, "online discovery restores current outputs without a scan");

            var apply = typeof(PhoneAudioWindow).GetMethod("ApplyMediaState", Hidden)!.CreateDelegate<Action<AndroidMediaState?>>(phone);
            var state = new AndroidMediaState(music.Package, "Track", "Artist", true, 822, DurationMs: 180000)
                { PlayingPackages = [music.Package] };
            apply(state); var rows = ((ListBox)phone.FindName("PhoneApps")).ItemsSource;
            for (int i = 0; i < 100; i++) apply(state with { PositionMs = i * 10 });
            Check(ReferenceEquals(rows, ((ListBox)phone.FindName("PhoneApps")).ItemsSource)
                && ((TextBlock)phone.FindName("MediaTitle")).Text == "Track",
                "position-only updates preserve rows and track metadata");
            var progress = (Slider)phone.FindName("MediaPositionSlider");
            apply(state with { PositionMs = 90000, ReceivedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() });
            Check(Math.Abs(progress.Value - 90000) < 1500, "a large position jump immediately corrects the visible progress clock");
            apply(state with { PositionMs = 45000, Playing = false });
            Check(Math.Abs(progress.Value - 45000) < 1, "paused progress stops animation at the reported position");
            apply(state);
            store.Retain(music with { Package = "cn.audit.other", Name = "Other" }); DrainUi();
            Check(((ListBox)phone.FindName("PhoneApps")).Items.OfType<PhoneAppItem>().Single(i => i.Package == music.Package).IsPlaying,
                "changing the retained list reapplies playing flags even with the same media snapshot");

            var queue = typeof(PhoneAudioWindow).GetMethod("QueueMediaState", Hidden)!
                .CreateDelegate<Action<AndroidMediaState, CancellationTokenSource>>(phone);
            var readField = typeof(PhoneAudioWindow).GetField("_mediaRead", Hidden)!;
            var firstRead = new CancellationTokenSource(); reads.Add(firstRead); readField.SetValue(phone, firstRead);
            int backgroundPosts = 0;
            DispatcherHookEventHandler posted = (_, e) => { if (e.Operation.Priority == DispatcherPriority.Background) backgroundPosts++; };
            phone.Dispatcher.Hooks.OperationPosted += posted;
            try { for (int i = 0; i < 1000; i++) queue(state with { Title = "Burst " + i }, firstRead); }
            finally { phone.Dispatcher.Hooks.OperationPosted -= posted; }
            Check(backgroundPosts == 1, "a thousand media callbacks queue only one background UI update");
            DrainUi();
            Check(((TextBlock)phone.FindName("MediaTitle")).Text == "Burst 999", "coalesced media keeps the latest track");
            var secondRead = new CancellationTokenSource(); reads.Add(secondRead);
            firstRead.Cancel(); readField.SetValue(phone, secondRead);
            queue(state with { Title = "Old subscription" }, firstRead);
            queue(state with { Title = "New subscription" }, secondRead); DrainUi();
            Check(((TextBlock)phone.FindName("MediaTitle")).Text == "New subscription", "old subscriptions cannot overwrite current media");
            queue(state with { Title = "Hidden update" }, secondRead); phone.Hide(); DrainUi();
            Check(secondRead.IsCancellationRequested && ((TextBlock)phone.FindName("MediaTitle")).Text == "New subscription",
                "hiding cancels media and drops an already queued snapshot");
            readField.SetValue(phone, null);
            Check(scans == 0 && !(bool)host.GetProperty("IsAdbCreated", statics)!.GetValue(null)!
                && !(bool)host.GetProperty("IsPcToPhoneCreated", statics)!.GetValue(null)!,
                "offline drag and media checks start no ADB, discovery or audio stream");
            Pump(store.FlushAsync());
            File.WriteAllText(Path.Combine(output, "interaction-ui.json"), JsonSerializer.Serialize(new
                { Passed = true, Assertions = _checks, DragAllocatedBytes = dragBytes, BackgroundPosts = backgroundPosts,
                    Scans = scans, RealDeviceAccess = false, Scope = "Offscreen drag/cache and media queue lifecycle; no real OLE input or phone" },
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { phone.Close(); foreach (var read in reads) read.Dispose(); }
    }
}
