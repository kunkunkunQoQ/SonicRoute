using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int ConnectionConsistencyUi(string output)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        int reads = 0;
        TaskCompletionSource<IReadOnlyList<AndroidDevice>>? pending = null;
        IReadOnlyList<AndroidDevice> next = [];
        var discovery = new AndroidDeviceDiscovery(_ => { ++reads; return pending?.Task ?? Task.FromResult(next); });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var stale = new AndroidDevice("192.0.2.1:37000", "Fixture", "offline", true, "same-phone", "old");
        var live = stale with { Serial = "192.0.2.1:38000", State = "device", TransportId = "new" };
        var unrelated = stale with { State = "device", HardwareId = "another-phone", TransportId = "unrelated" };
        var app = new AndroidApp("cn.consistency.music", "Music", 1001, "media");
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(stale); store.Rename("Connection fixture"); store.Retain(app);
        discovery.Publish([stale]);
        var phone = new PhoneAudioWindow(false) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var main = new MainWindow();
        bool viewsClosed = false;
        object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);
        Task<AndroidDevice> Resolve(AndroidDevice requested) => (Task<AndroidDevice>)host.GetMethod("ResolveOperationDeviceAsync", statics)!
            .Invoke(null, [requested, AndroidErrorCode.PhoneOffline])!;
        try
        {
            Pump((Task)Call(main, "InitializePhoneManagementAsync")!);
            Call(main, "SelectPhoneManagement");
            phone.Show(); Pump((Task)Call(phone, "InitializeDevicesAsync")!);
            var combo = (ComboBox)phone.FindName("DeviceCombo");
            var badge = (TextBlock)phone.FindName("ConnectionText");
            var sidebar = (ListBox)main.FindName("PhoneDevicesList");
            var details = (PhoneDevicePage)((Border)main.FindName("PhoneDetailsHost")).Child;
            Check(badge.Text == L10n.T("Phone.Disconnected") && combo.SelectedItem is AndroidDevice { State: "offline" },
                "saved offline profile remains visible and correctly offline");
            var signal = (FrameworkElement)phone.FindName("ConnectionSignal");
            var usbIcon = (FrameworkElement)phone.FindName("ConnectionUsbIcon");
            Check(signal.Visibility == Visibility.Collapsed && usbIcon.Visibility == Visibility.Collapsed
                && !combo.SelectedItem.ToString()!.Contains("Wi-Fi") && ((TextBlock)details.FindName("DeviceDescription")).Text == L10n.T("Phone.Disconnected"),
                "an offline Wi-Fi profile exposes no transport icon or old wireless label");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var failedRefresh = (Task<bool>)Call(phone, "RefreshDevicesCoreAsync", false)!;
            discovery.Publish([live]); DrainUi();
            pending.SetException(new IOException("fixture scan failed after connection notification"));
            Pump(failedRefresh); pending = null; DrainUi();
            Check(!failedRefresh.Result && badge.Text == L10n.T("Phone.ConnectedStatus")
                && Equals(combo.SelectedItem, live) && badge.ToolTip?.ToString()?.Contains("fixture scan failed") == true,
                "connection received during a failing refresh updates the selection and keeps operation errors separate");
            Check(sidebar.Items.Cast<object>().Any(i => (bool)i.GetType().GetProperty("IsActive")!.GetValue(i)!),
                "main sidebar and quick device footer agree after wireless endpoint change");
            Check(signal.Visibility == Visibility.Visible && usbIcon.Visibility == Visibility.Collapsed,
                "the online Wi-Fi phone shows only its wireless status icon");
            var connectedUsb = live with { Serial = "same-phone", IsWifi = false, TransportId = "usb" };
            store.RememberDevice(connectedUsb);
            discovery.Publish([connectedUsb]); DrainUi();
            Check(signal.Visibility == Visibility.Collapsed && usbIcon.Visibility == Visibility.Visible,
                "the online USB phone shows only its USB status icon");
            discovery.Publish([]); DrainUi();
            Check(signal.Visibility == Visibility.Collapsed && usbIcon.Visibility == Visibility.Collapsed
                && combo.SelectedItem is AndroidDevice { IsWifi: false, State: "offline" } && !combo.SelectedItem.ToString()!.Contains("USB"),
                "an offline USB profile exposes no transport icon or old USB label");
            store.RememberDevice(live);
            var operation = (TextBlock)details.FindName("OperationStatus");
            var blockedUsb = new AndroidDevice("SAME-PHONE", "USB", "usb_unavailable", false);
            string usbHint = L10n.T("Phone.Err.UsbInterfaceUnavailable");
            discovery.Publish([blockedUsb, live]); DrainUi();
            Check(operation.Text.Contains(usbHint) && badge.ToolTip?.ToString()?.Contains(usbHint) == true
                && badge.Text == L10n.T("Phone.ConnectedStatus") && combo.Items.OfType<AndroidDevice>().Count() == 1,
                "both panels explain inaccessible USB while Wi-Fi stays online as one saved device");
            discovery.Publish([live with { HardwareId = null, State = "offline" }, unrelated, live]); DrainUi();
            Check(!operation.Text.Contains(usbHint) && badge.ToolTip?.ToString()?.Contains(usbHint) != true
                && combo.Items.OfType<AndroidDevice>().Count(d => d.Serial == live.Serial) == 1 && Equals(combo.SelectedItem, live),
                "an offline alias cannot displace or duplicate the matching live device");
            int before = reads;
            var resolved = Resolve(stale); Pump(resolved);
            Check(resolved.Result == live && reads == before, "old drag selects the verified live port over a different phone using its old address, without scanning");
            resolved = Resolve(live with { State = "offline" }); Pump(resolved);
            Check(resolved.Result == live && reads == before, "an offline record with the same endpoint resolves to the shared online snapshot");
            discovery.Invalidate(); next = [live];
            resolved = Resolve(stale); Pump(resolved);
            Check(resolved.Result == live && reads == before + 1, "an invalidated snapshot gets exactly one on-demand scan");

            // Closing a view must not cause a subsequent operation to use its old record.
            phone.Close(); main.Close(); viewsClosed = true;
            var sources = (AndroidAudioManager)host.GetProperty("Sources", statics)!.GetValue(null)!;
            AndroidDevice? audioDevice = null;
            typeof(AndroidAudioManager).GetField("_factory", Hidden)!.SetValue(sources,
                (Func<AndroidAudioSession>)(() => new AndroidAudioSession(() => new ConnectionAudioFixture(d => audioDevice = d),
                    (_, _) => throw new InvalidOperationException("unexpected renderer"))));
            try { Pump((Task)host.GetMethod("AddAudioAsync", statics)!.Invoke(null, [stale, app, true, false])!); }
            catch (IOException) { }
            Check(audioDevice == live, "audio start receives the current online device instead of the offline drag object");
            var displays = (AndroidVirtualDisplayManager)host.GetProperty("Displays", statics)!.GetValue(null)!;
            var display = new ConnectionDisplayFixture();
            typeof(AndroidVirtualDisplayManager).GetField("_factory", Hidden)!.SetValue(displays, (Func<IVirtualDisplayClient>)(() => display));
            var opened = (Task<IVirtualDisplayClient>)host.GetMethod("OpenDisplayAsync", statics)!.Invoke(null, [stale, app, 0, 0])!;
            Pump(opened);
            Check(ReferenceEquals(opened.Result, display) && display.Device == live && display.Device.State == "device",
                "virtual screen startup receives the same live transport and passes the offline guard");
            Pump(displays.DisposeAsync().AsTask()); Pump(sources.DisposeAsync().AsTask());
            bool rejected = false;
            var unknown = new AndroidDevice("192.0.2.99:37000", "Unknown", "offline", true);
            try { Pump(Resolve(unknown)); }
            catch (IOException e) { rejected = AndroidError.TryGetCode(e, out var code) && code == AndroidErrorCode.PhoneOffline; }
            Check(rejected && reads == before + 2, "an unverified missing endpoint cannot silently select a different wireless device");
            var healthy = new AndroidDevice("healthy-peer", "Other phone", "device", false, "healthy-peer", "peer-transport");
            store.RememberDevice(healthy); store.RememberDevice(live); next = [healthy, live]; discovery.Publish(next);
            CheckMediaFaultIsolation(host, cache, discovery, live, healthy);
            Console.WriteLine($"PASS {_checks} connection consistency assertions (no real ADB/audio/video)");
            return 0;
        }
        finally { if (!viewsClosed) { phone.Close(); main.Close(); } }
    }

    private static void CheckMediaFaultIsolation(Type host, Type cache, AndroidDeviceDiscovery discovery, AndroidDevice live, AndroidDevice peer)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var adb = (AdbClient)host.GetProperty("Adb", statics)!.GetValue(null)!;
        var transport = typeof(AdbClient).GetField("_resolvedTransport", Hidden)!;
        var previous = transport.GetValue(adb);
        var shell = new CleanupShell();
        int attempts = 0;
        transport.SetValue(adb, new CleanupTransport(shell, path =>
        {
            if (!path.Contains("/sonicroute_media_", StringComparison.Ordinal)) return Task.CompletedTask;
            Interlocked.Increment(ref attempts);
            throw new IOException("fixture media service failed to start");
        }));
        var getOnline = cache.GetMethod("GetOnlineDevice", statics)!;
        bool peerDropped = false;
        void ObservePeer() { if (getOnline.Invoke(null, [peer]) == null) peerDropped = true; }
        discovery.Changed += ObservePeer;
        var quick = new PhoneAudioWindow { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var task = typeof(PhoneAudioWindow).GetField("_mediaTask", Hidden)!;
        try
        {
            quick.Show();
            Until(() => Volatile.Read(ref attempts) == 2 && ((Task)task.GetValue(quick)!).IsCompleted);
            Check(!peerDropped && Equals(getOnline.Invoke(null, [peer]), peer) && Equals(getOnline.Invoke(null, [live]), live),
                "a failing media helper leaves both phones online throughout its single delayed retry");
            discovery.Publish([peer, live]); DrainUi();
            Pump(Task.Delay(1250));
            Check(Volatile.Read(ref attempts) == 2 && ((TextBlock)quick.FindName("MediaSubtitle")).Text == L10n.T("Phone.MediaControlUnavailable"),
                "repeated notifications for the same live transport leave the failed helper stopped and its error visible");
            var reconnected = live with { TransportId = "reconnected" };
            discovery.Publish([peer, reconnected]);
            Until(() => Volatile.Read(ref attempts) == 3);
            quick.Hide(); Pump((Task)task.GetValue(quick)!);
            Check(!peerDropped && Volatile.Read(ref attempts) == 3,
                "a new transport permits media recovery and hiding the panel cancels its pending retry");
            quick.Show();
            Until(() => Volatile.Read(ref attempts) == 4);
            Check(Equals(getOnline.Invoke(null, [peer]), peer), "reopening the panel gives its selected phone a fresh attempt without disturbing its peer");
        }
        finally
        {
            quick.Close(); Pump((Task)task.GetValue(quick)!);
            discovery.Changed -= ObservePeer;
            transport.SetValue(adb, previous);
            Pump(shell.DisposeAsync().AsTask());
        }
    }

    private sealed class ConnectionAudioFixture(Action<AndroidDevice> capture) : IAudioTransport
    {
        public Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
        { capture(device); throw new IOException("intentional fixture stop before audio"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static int ManagedConnectionUi(string output)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        var archived = new AndroidDevice("192.0.2.2:40921", "Xiaomi fixture", "device", true, "fixture-stable", "old");
        var endpoint = new AndroidDevice("192.0.2.2:40083", "Xiaomi fixture", "device", true, null, "live");
        var live = endpoint with { HardwareId = archived.HardwareId, IdentityFingerprint = "same-phone-proof",
            ConnectionTarget = "fixture-live" };
        var usb = live with { Serial = "fixture-usb", IsWifi = false, TransportId = "usb", ConnectionTarget = "fixture-usb-live" };
        var retained = new AndroidApp("cn.fixture.archived", "Old app", 1002, "media");
        var app = new AndroidApp("cn.fixture.active", "Current app", 1001, "media");
        store.RememberDevice(archived); store.Retain(retained); store.RemoveDevice(store.Profile.Id);
        store.RememberDevice(endpoint); store.Rename("小米"); store.Retain(app);
        string profileId = store.Profile.Id;
        IReadOnlyList<AndroidDevice> available = [live, usb];
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult(available));
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        discovery.Publish([live, usb]);
        var phone = new PhoneAudioWindow(false) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var main = new MainWindow();
        object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);
        try
        {
            Pump((Task)Call(main, "InitializePhoneManagementAsync")!); Call(main, "SelectPhoneManagement");
            phone.Show(); Pump((Task)Call(phone, "InitializeDevicesAsync")!);
            var combo = (ComboBox)phone.FindName("DeviceCombo"); var badge = (TextBlock)phone.FindName("ConnectionText");
            var sidebar = (ListBox)main.FindName("PhoneDevicesList");
            bool SidebarConnected() => sidebar.Items.Cast<object>().Any(i => (bool)i.GetType().GetProperty("IsActive")!.GetValue(i)!);
            Check(badge.Text == L10n.T("Phone.Disconnected") && !SidebarConnected() && store.Profiles.Count == 1,
                "historic tombstone conflict is consistently offline and neither USB/Wi-Fi creates a duplicate profile");
            var wireless = new AndroidWirelessConnection((_, _) => throw new IOException("unexpected fixture ADB command"));
            Pump((Task)cache.GetMethod("RefreshSelectedDeviceAsync", statics)!.Invoke(null, [CancellationToken.None, wireless])!); DrainUi();
            Check(store.Profile.Id == profileId && store.Profile.RetainedApps.Count == 2 && !store.IsRemoved(live),
                "explicit reconnect preserves the active profile, merges retained apps and clears its historical removal");
            Check(combo.Items.OfType<AndroidDevice>().Count() == 1 && Equals(combo.SelectedItem, live)
                && badge.Text == L10n.T("Phone.ConnectedStatus"),
                $"quick panel selects the repaired live transport (choices={combo.Items.OfType<AndroidDevice>().Count()}, badge={badge.Text}, selected={combo.SelectedItem})");
            Check(SidebarConnected(), "main sidebar reflects explicit admission for the existing profile");
            available = [usb];
            Pump((Task)cache.GetMethod("RefreshSelectedDeviceAsync", statics)!.Invoke(null, [CancellationToken.None, wireless])!); DrainUi();
            Check(store.Profile.Id == profileId && Equals(combo.SelectedItem, usb) && SidebarConnected(),
                "refreshing the saved Wi-Fi profile uses its verified USB connection when Wi-Fi is absent");
            available = [live];
            Pump((Task)cache.GetMethod("RefreshSelectedDeviceAsync", statics)!.Invoke(null, [CancellationToken.None, wireless])!); DrainUi();
            Check(store.Profile.Id == profileId && Equals(combo.SelectedItem, live) && SidebarConnected(),
                "the same profile follows its verified Wi-Fi connection when USB is unplugged");
            var sources = (AndroidAudioManager)host.GetProperty("Sources", statics)!.GetValue(null)!;
            AndroidDevice? audioTarget = null;
            typeof(AndroidAudioManager).GetField("_factory", Hidden)!.SetValue(sources,
                (Func<AndroidAudioSession>)(() => new AndroidAudioSession(() => new ConnectionAudioFixture(d => audioTarget = d),
                    (_, _) => throw new InvalidOperationException("unexpected renderer"))));
            try { Pump((Task)host.GetMethod("AddAudioAsync", statics)!.Invoke(null, [live, app, true, false])!); }
            catch (IOException) { }
            Check(audioTarget?.CommandTarget == live.CommandTarget, "audio startup reaches the freshly admitted transport rather than failing with removed-device OSD");
            var displays = (AndroidVirtualDisplayManager)host.GetProperty("Displays", statics)!.GetValue(null)!;
            var display = new ConnectionDisplayFixture();
            typeof(AndroidVirtualDisplayManager).GetField("_factory", Hidden)!.SetValue(displays, (Func<IVirtualDisplayClient>)(() => display));
            Pump((Task)host.GetMethod("OpenDisplayAsync", statics)!.Invoke(null, [live, app, 0, 0])!);
            Check(display.Device?.CommandTarget == live.CommandTarget && !display.Completion.IsCompleted,
                "virtual screen startup uses the same admitted transport and remains active");
            var manager = (AndroidConnectionManager)cache.GetProperty("Manager", statics)!.GetValue(null)!;
            var operation = manager.OperationToken(live);
            Pump((Task)host.GetMethod("RemovePhoneAsync", statics)!.Invoke(null, [profileId, live])!); DrainUi();
            Check(display.Completion.IsCompleted && operation.IsCancellationRequested && !SidebarConnected()
                && store.Profiles.All(p => p.Id != profileId), "active removal closes the virtual screen, revokes operations and updates both panels");
            bool rejected = false;
            try { Pump((Task)host.GetMethod("OpenDisplayAsync", statics)!.Invoke(null, [live, app, 0, 0])!); }
            catch (IOException ex) { rejected = AndroidError.TryGetCode(ex, out var code) && code == AndroidErrorCode.PhoneRemovedAddFirst; }
            Check(rejected, "a stale drag cannot open a new virtual screen after removal");
            Pump(displays.DisposeAsync().AsTask()); Pump(sources.DisposeAsync().AsTask());
            Console.WriteLine($"PASS {_checks} managed connection UI assertions (mock transport, no device access)"); return 0;
        }
        finally { phone.Close(); main.Close(); }
    }

    private sealed class ConnectionDisplayFixture : IVirtualDisplayClient
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AndroidDevice? Device { get; private set; }
        public Task Completion => _completion.Task;
        public Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token)
        { Device = device; return Task.CompletedTask; }
        public void Activate() { }
        public ValueTask DisposeAsync() { _completion.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
