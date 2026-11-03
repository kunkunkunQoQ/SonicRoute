using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

internal static partial class Program
{
    private static int PhoneDemandUi(string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        bool Created(string name) => (bool)host.GetProperty(name, statics)!.GetValue(null)!;
        bool LazyCreated(string field)
        {
            var value = host.GetField(field, statics)!.GetValue(null)!;
            return (bool)value.GetType().GetProperty("IsValueCreated")!.GetValue(value)!;
        }
        object? Call(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, arguments);
        void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var discoveryProperty = cache.GetProperty("Discovery", statics)!;
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ => { scans++; return Task.FromResult<IReadOnlyList<AndroidDevice>>(Array.Empty<AndroidDevice>()); });
        discoveryProperty.SetValue(null, discovery);
        Check(!Created("IsCreated") && !LazyCreated("LazyPhone") && !LazyCreated("LazyAdb"),
            "normal app initialization leaves phone audio, profiles and ADB uncreated");
        var config = ConfigService.Load(); config.QuickPanelShowPhoneAudio = false;
        var quick = new QuickPanelModernWindow();
        var phoneToggle = (System.Windows.Controls.Primitives.ToggleButton)quick.FindName("PhoneAudioButton");
        var outputToggle = (System.Windows.Controls.Primitives.ToggleButton)quick.FindName("OutputVolumesToggle");
        Check(ReferenceEquals(phoneToggle.Style, outputToggle.Style)
            && phoneToggle.Width == outputToggle.Width && phoneToggle.Height == outputToggle.Height
            && phoneToggle.Content is System.Windows.Shapes.Path && phoneToggle.ToolTip == null,
            "phone entry shares companion-toggle dimensions and style, uses an icon and has no hover tooltip");
        Check(!Created("IsCreated") && !LazyCreated("LazySettings") && !LazyCreated("LazyAdb") && scans == 0,
            "computer quick panel creates no phone audio manager, preferences, tools or device scan");
        quick.Close();

        var main = new MainWindow();
        try
        {
            Pump((Task)Call(main, "InitializePhoneManagementAsync")!);
            Check(LazyCreated("LazyPhone") && !LazyCreated("LazyAdb") && !Created("IsCreated") && scans == 0,
                "full Apps sidebar loads saved names asynchronously without starting ADB or audio");
            Pump((Task)Call(main, "RefreshPhoneDevicesAsync")!);
            Check(scans == 0 && !LazyCreated("LazyAdb"), "refreshing computer applications does not scan an unopened phone page");
            var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
            var device = new AndroidDevice("offline-fixture", "Fixture", "device", true, HardwareId: "fixture-hardware", TransportId: "fixture-transport");
            var app = new AndroidApp("cn.test.music", "Music", 1001, "media");
            store.RememberDevice(device); store.Retain(app);
            discovery.Publish(new[] { device });
            Call(main, "SelectPhoneManagement");
            var page = (PhoneDevicePage)((Border)main.FindName("PhoneDetailsHost")).Child;
            Check(scans == 0 && ((ItemsControl)page.FindName("RetainedApps")).Items.Count == 1,
                "selecting a cached phone binds only saved apps without fetching a full catalogue");
            var choices = new[] { new PhoneAppChoice(new PhoneAppItem(app), true),
                new PhoneAppChoice(new PhoneAppItem(app with { Package = "cn.test.video", Name = "Video", Uid = 1002 }), false) };
            Set(page, "_catalog", choices.Select(a => a.Item).ToArray()); Set(page, "_catalogChoices", choices);
            ((FrameworkElement)page.FindName("CatalogPanel")).Visibility = Visibility.Visible;
            Call(page, "ApplyCatalogFilter");
            var catalogList = (ListBox)page.FindName("CatalogApps"); var bound = catalogList.ItemsSource;
            store.Retain(choices[1].Item.App);
            Check(choices[1].Retained && ReferenceEquals(bound, catalogList.ItemsSource)
                && ReferenceEquals(catalogList.Items[1], choices[1]),
                "pin changes update existing catalogue choices without resetting rows or scroll");
            Call(page, "Suspend");
            var oldName = ((TextBlock)page.FindName("DeviceName")).Text;
            store.Rename("隐藏时改名");
            Check(((TextBlock)page.FindName("DeviceName")).Text == oldName && catalogList.ItemsSource == null
                && ((IEnumerable)typeof(PhoneDevicePage).GetField("_catalog", Hidden)!.GetValue(page)!).Cast<object>().Count() == 0,
                "suspended phone pages stop profile updates and release full catalogue view models");
            Call(page, "Activate");
            Check(((TextBlock)page.FindName("DeviceName")).Text == "隐藏时改名" && scans == 0,
                "reactivation applies cached profile changes without rescanning the phone");

            var phone = new PhoneAudioWindow(false) { Left = -10000, Top = -10000, Width = 235, Height = 450,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                phone.Show(); Pump((Task)Call(phone, "InitializeDevicesAsync")!);
                var apps = (ListBox)phone.FindName("PhoneApps"); var savedRows = apps.ItemsSource;
                var media = new AndroidMediaState(app.Package, "Track", "Artist", true, 512, DurationMs: 120000);
                Call(phone, "ApplyMediaState", media);
                savedRows = apps.ItemsSource;
                for (int i = 0; i < 20; i++) Call(phone, "ApplyFilter");
                Check(ReferenceEquals(savedRows, apps.ItemsSource), "unchanged filtering keeps phone rows, selection and scroll intact");
                using var mediaLifetime = new CancellationTokenSource();
                var mediaTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Set(phone, "_mediaRead", mediaLifetime); Set(phone, "_mediaTask", mediaTask.Task);
                Set(phone, "_mediaDevice", device); Set(phone, "_enableMedia", true);
                for (int i = 0; i < 10; i++) Call(phone, "DevicesChanged");
                Check(!mediaLifetime.IsCancellationRequested && ReferenceEquals(typeof(PhoneAudioWindow).GetField("_mediaTask", Hidden)!.GetValue(phone), mediaTask.Task),
                    "repeated same-device notifications reuse the live media subscription");
                var cover = new System.Windows.Media.DrawingImage(new System.Windows.Media.GeometryDrawing(
                    System.Windows.Media.Brushes.Blue, null, new System.Windows.Media.RectangleGeometry(new Rect(0,0,96,96))));
                ((Image)phone.FindName("MediaArtwork")).Source = cover;
                Set(phone, "_artworkBytes", new byte[1024 * 1024]);
                Call(phone, "QueueMediaState", media, mediaLifetime);
                phone.Hide();
                Check(mediaLifetime.IsCancellationRequested, "hiding the phone panel cancels its media and latency lifetime");
                Check(typeof(PhoneAudioWindow).GetField("_artworkBytes", Hidden)!.GetValue(phone) == null
                    && typeof(PhoneAudioWindow).GetField("_mediaState", Hidden)!.GetValue(phone) == null
                    && typeof(PhoneAudioWindow).GetField("_pendingMediaState", Hidden)!.GetValue(phone) == null
                    && ((Image)phone.FindName("MediaArtwork")).Source == null
                    && ((Image)phone.FindName("MediaAppIcon")).Source == null && ReferenceEquals(savedRows, apps.ItemsSource),
                    "hiding releases cover bytes/images and media snapshots while retaining cached app rows");
                mediaTask.TrySetResult(); Set(phone, "_enableMedia", false); Set(phone, "_mediaRead", null!);
                var uiLifetime = typeof(PhoneAudioWindow).GetField("_mediaUiLifetime", Hidden)!.GetValue(phone)!;
                bool queuedRan = false;
                var pending = (System.Windows.Threading.DispatcherOperation)uiLifetime.GetType().GetMethod("Post", Hidden)!.Invoke(
                    uiLifetime, [phone.Dispatcher, (Action)(() => queuedRan = true), System.Windows.Threading.DispatcherPriority.Background])!;
                phone.Close(); DrainUi();
                Check(!queuedRan && pending.Status == System.Windows.Threading.DispatcherOperationStatus.Aborted,
                    "closing the phone panel aborts queued media UI work without stopping audio/video providers");
            }
            finally { phone.Close(); }

            var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slowDiscovery = new AndroidDeviceDiscovery(async token =>
            {
                scanStarted.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { if (token.IsCancellationRequested) cancelled.SetResult(); }
                return Array.Empty<AndroidDevice>();
            });
            discoveryProperty.SetValue(null, slowDiscovery);
            var pendingPhone = new PhoneAudioWindow(false) { Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                pendingPhone.Show(); var refresh = (Task)Call(pendingPhone, "RefreshDevicesAsync")!;
                Pump(scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(2))); pendingPhone.Hide();
                Pump(Task.WhenAll(refresh, cancelled.Task).WaitAsync(TimeSpan.FromSeconds(2)));
                Check(slowDiscovery.Devices == null, "hidden panel cancels the last scan caller without publishing stale devices");
            }
            finally { pendingPhone.Close(); discoveryProperty.SetValue(null, discovery); }
            Pump(store.FlushAsync());
            Check(!Created("IsCreated") && !Created("AreDisplaysCreated") && !LazyCreated("LazyAdb") && !LazyCreated("LazySettings"),
                "UI demand checks never create phone audio/video workers, preferences or ADB");
            File.WriteAllText(Path.Combine(output, "phone-demand-ui.json"), JsonSerializer.Serialize(new
            { Passed = true, Assertions = _checks, DeviceAccess = false, ProductStarted = false,
                Scope = "Offscreen controls, fixture discovery and synthetic media lifetime; no real phone or playback" }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} phone demand UI assertions; no phone access");
            return 0;
        }
        finally { main.Close(); }
    }
}
