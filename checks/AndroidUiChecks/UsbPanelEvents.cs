using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendUsbTestMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private static int UsbPanelEventsUi(App app)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        const int arrival = 0x8000, removal = 0x8004;
        Guid adbInterface = new("F72FE0D4-CBCB-407D-8814-9ED673D0DD6B");
        var usb = new AndroidDevice("fixture-usb", "小米", "device", false, "fixture-usb", "usb")
            { IdentityFingerprint = "fixture-phone", ConnectionTarget = "fixture-usb-session", UsbInterfacePath = @"\\?\usb#port-a" };
        var wifi = usb with { Serial = "adb-fixture-usb-ABC._adb-tls-connect._tcp", IsWifi = true,
            TransportId = "wifi", ConnectionTarget = "fixture-wifi-session", UsbInterfacePath = null };
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(usb);
        string id = store.Profile.Id;
        int reads = 0;
        int failingReads = 0;
        IReadOnlySet<string>? presentPaths = null;
        IReadOnlyList<AndroidDevice> devices = [usb];
        TaskCompletionSource<IReadOnlyList<AndroidDevice>>? pendingScan = null;
        var discovery = new AndroidDeviceDiscovery(_ =>
        {
            Interlocked.Increment(ref reads);
            if (Volatile.Read(ref failingReads) > 0 && Interlocked.Decrement(ref failingReads) >= 0)
                return Task.FromException<IReadOnlyList<AndroidDevice>>(new System.IO.IOException("fixture device enumeration failed"));
            return Interlocked.Exchange(ref pendingScan, null)?.Task ?? Task.FromResult(Volatile.Read(ref devices));
        });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        cache.GetProperty("ReadUsbInterfacePaths", statics)!.SetValue(null, (Func<IReadOnlySet<string>?>)(() => presentPaths));
        cache.GetProperty("WatchPanelWireless", statics)!.SetValue(null,
            (Func<Action<string>, Action, CancellationToken, Task>)((_, _, token) => Task.Delay(Timeout.Infinite, token)));
        cache.GetProperty("CreateWirelessConnection", statics)!.SetValue(null,
            (Func<AndroidWirelessConnection>)(() => new AndroidWirelessConnection((_, _) => Task.FromResult(""))));
        using var sink = new HwndSource(new HwndSourceParameters("SonicRoute USB notification fixture") { WindowStyle = 0 });
        var sinkField = typeof(App).GetField("_activateSink", Hidden)!;
        sinkField.SetValue(app, sink);
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        var phone = new PhoneAudioWindow(false) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            // Send only to the product's test-instance HWND. Moving the backend's
            // snapshot alone never publishes a list or invokes a refresh handler.
            void Notify(int change, Guid guid, string? path = null)
            {
                var name = System.Text.Encoding.Unicode.GetBytes((path ?? "") + '\0');
                int size = Math.Max(32, 28 + name.Length);
                var memory = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.Copy(new byte[size], 0, memory, size);
                    Marshal.WriteInt32(memory, size);
                    Marshal.WriteInt32(memory, 4, 5); // DBT_DEVTYP_DEVICEINTERFACE
                    Marshal.Copy(guid.ToByteArray(), 0, memory + 12, 16);
                    Marshal.Copy(name, 0, memory + 28, name.Length);
                    SendUsbTestMessage(sink.Handle, 0x0219, new(change), memory);
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            main.Show();
            ((RadioButton)main.FindName("NavApps")).IsChecked = true;
            Pump((Task)typeof(MainWindow).GetMethod("InitializePhoneManagementAsync", Hidden)!.Invoke(main, null)!);
            phone.Show();
            var sidebar = (ListBox)main.FindName("PhoneDevicesList");
            var combo = (ComboBox)phone.FindName("DeviceCombo");
            var badge = (TextBlock)phone.FindName("ConnectionText");
            bool MainShows(string status)
            {
                if (sidebar.Items.Count != 1 || store.Profiles.Count != 1 || store.Profile.Id != id) return false;
                var row = sidebar.Items[0];
                return Equals(row.GetType().GetProperty("Id")!.GetValue(row), id)
                    && Equals(row.GetType().GetProperty("Subtitle")!.GetValue(row), L10n.T(status));
            }
            bool QuickShows(AndroidDevice? transport) => combo.Items.OfType<AndroidDevice>().Count() == 1
                && (transport == null ? combo.SelectedItem is AndroidDevice { State: "offline" } : Equals(combo.SelectedItem, transport))
                && badge.Text == L10n.T(transport == null ? "Phone.Disconnected" : "Phone.ConnectedStatus");
            bool BothShow(string status, AndroidDevice? transport) => MainShows(status) && QuickShows(transport);

            Until(() => BothShow("Phone.UsbConnected", usb));
            int before = Volatile.Read(ref reads);
            Check(before == 1, "the first visible phone list scans and the second panel shares its USB snapshot");
            Volatile.Write(ref devices, []);
            Notify(removal, Guid.Empty);
            Pump(Task.Delay(400));
            Check(Volatile.Read(ref reads) == before && BothShow("Phone.UsbConnected", usb),
                "unrelated device notifications and changing the backend alone do not wake phone discovery");

            Notify(removal, adbInterface); Notify(removal, adbInterface);
            Until(() => BothShow("Phone.Disconnected", null));
            Pump(Task.Delay(400));
            Check(Volatile.Read(ref reads) == before + 1, "duplicate USB removal messages produce one scan and both panels show the original phone offline");

            Volatile.Write(ref devices, [usb]);
            Notify(arrival, adbInterface);
            Until(() => BothShow("Phone.UsbConnected", usb));
            Check(store.Profile.Id == id, "USB arrival restores the same phone in both panels without a manual action");

            var staleScan = new TaskCompletionSource<IReadOnlyList<AndroidDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref pendingScan, staleScan);
            before = Volatile.Read(ref reads);
            Notify(arrival, adbInterface);
            Until(() => Volatile.Read(ref reads) > before);
            Volatile.Write(ref devices, [wifi]);
            Notify(removal, adbInterface);
            Pump(Task.Delay(400));
            staleScan.SetResult([usb]);
            Until(() => BothShow("Phone.WifiConnected", wifi));
            Pump(Task.Delay(400));
            Check(BothShow("Phone.WifiConnected", wifi), "a stale USB scan cannot leave either panel on USB after unplug with a verified live Wi-Fi alias");

            phone.Hide();
            Volatile.Write(ref devices, [usb]);
            Notify(arrival, adbInterface);
            Until(() => MainShows("Phone.UsbConnected"));
            Check(store.Profiles.Single().Id == id, "the visible main phone list still receives changes after hiding the quick panel");
            phone.Show();
            Until(() => QuickShows(usb));
            main.Hide();
            Volatile.Write(ref devices, []);
            Notify(removal, adbInterface);
            Until(() => QuickShows(null));
            Check(store.Profiles.Single().Id == id, "the quick panel alone receives removal and keeps one saved phone");
            phone.Hide();
            before = Volatile.Read(ref reads);
            Notify(removal, adbInterface);
            Pump(Task.Delay(400));
            Check(Volatile.Read(ref reads) == before, "hiding both panels releases the phone discovery notification listener");

            // Another existing feature already has a read outstanding while both
            // panels are hidden. The first visible panel needs a post-arrival scan.
            var hiddenScan = new TaskCompletionSource<IReadOnlyList<AndroidDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref pendingScan, hiddenScan);
            var otherReader = discovery.ReadAsync(CancellationToken.None);
            Volatile.Write(ref devices, [usb]);
            Notify(arrival, adbInterface);
            phone.Show();
            Pump(Task.Delay(400));
            hiddenScan.SetResult([]);
            Pump(otherReader);
            Until(() => QuickShows(usb));
            Check(store.Profiles.Single().Id == id, "reopening after a hidden USB arrival supersedes an older scan shared by a different reader");

            main.Show();
            Volatile.Write(ref devices, [usb, wifi]);
            Notify(arrival, adbInterface);
            Until(() => discovery.Devices?.Count == 2 && BothShow("Phone.UsbConnected", usb));
            presentPaths = new HashSet<string>();
            Volatile.Write(ref devices, [wifi]);
            Volatile.Write(ref failingReads, 1);
            before = Volatile.Read(ref reads);
            Notify(removal, adbInterface);
            Until(() => BothShow("Phone.WifiConnected", wifi));
            Check(Volatile.Read(ref reads) == before + 1, "a failed unplug scan with no USB interface removes USB and retains the verified Wi-Fi alias in both panels");
            presentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { usb.UsbInterfacePath! };
            Volatile.Write(ref devices, [usb]);
            Volatile.Write(ref failingReads, 1);
            before = Volatile.Read(ref reads);
            Notify(arrival, adbInterface);
            Until(() => BothShow("Phone.UsbConnected", usb));
            Check(Volatile.Read(ref reads) == before + 2, "a transient scan failure with USB present gets one bounded retry and restores both panels");
            Volatile.Write(ref failingReads, 3);
            before = Volatile.Read(ref reads);
            Notify(arrival, adbInterface);
            Until(() => Volatile.Read(ref reads) >= before + 2);
            Pump(Task.Delay(500));
            Check(Volatile.Read(ref reads) == before + 2 && BothShow("Phone.UsbConnected", usb),
                "persistent enumeration errors stop after that single retry and preserve the last verified connection");

            var peer = new AndroidDevice("fixture-peer", "Second phone", "device", false, "fixture-peer", "peer")
                { IdentityFingerprint = "other-phone", ConnectionTarget = "peer-usb-session", UsbInterfacePath = @"\\?\usb#port-b" };
            store.RememberDevice(peer);
            string peerId = store.Profile.Id;
            store.RememberDevice(usb);
            bool UsbScanComplete() => cache.GetField("_usbPanelScan", statics)!.GetValue(null) == null;
            bool TwoPhonesShow(AndroidDevice selected, string status)
            {
                string? RowStatus(string profileId)
                {
                    var row = sidebar.Items.Cast<object>().FirstOrDefault(r => Equals(r.GetType().GetProperty("Id")!.GetValue(r), profileId));
                    return row?.GetType().GetProperty("Subtitle")!.GetValue(row) as string;
                }
                return sidebar.Items.Count == 2 && RowStatus(id) == L10n.T(status) && RowStatus(peerId) == L10n.T("Phone.UsbConnected")
                    && combo.Items.OfType<AndroidDevice>().Count() == 2 && Equals(combo.SelectedItem, selected)
                    && combo.Items.Contains(peer) && badge.Text == L10n.T("Phone.ConnectedStatus");
            }
            discovery.Publish([usb, peer, wifi]);
            Until(() => TwoPhonesShow(usb, "Phone.UsbConnected"));
            presentPaths = null; // Both ADB enumeration and the independent Windows probe fail.
            Volatile.Write(ref failingReads, 2);
            Notify(removal, adbInterface, usb.UsbInterfacePath!.ToUpperInvariant());
            Until(() => UsbScanComplete() && TwoPhonesShow(wifi, "Phone.WifiConnected"));
            Check(discovery.Devices!.SequenceEqual([peer, wifi]),
                "an exact Windows removal path retires only its USB generation when both enumerators fail; the other USB phone and Wi-Fi stay online");

            // Reset to the two USB connections. After the previous failover the
            // normal UI deliberately keeps Wi-Fi preferred while it is available.
            discovery.Publish([usb, peer]);
            Until(() => TwoPhonesShow(usb, "Phone.UsbConnected"));
            Volatile.Write(ref failingReads, 2);
            before = Volatile.Read(ref reads);
            Notify(removal, adbInterface, usb.UsbInterfacePath);
            Notify(arrival, adbInterface, usb.UsbInterfacePath);
            Until(() => Volatile.Read(ref reads) >= before + 2 && UsbScanComplete());
            Check(TwoPhonesShow(usb, "Phone.UsbConnected"), "a reattachment within the debounce window cancels a stale path removal");

            discovery.Publish([usb, peer, wifi]);
            Until(() => TwoPhonesShow(usb, "Phone.UsbConnected"));
            presentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { peer.UsbInterfacePath! };
            Volatile.Write(ref failingReads, 2);
            Notify(removal, adbInterface); // Generic event without a device path.
            Until(() => UsbScanComplete() && TwoPhonesShow(wifi, "Phone.WifiConnected"));
            Check(discovery.Devices!.SequenceEqual([peer, wifi]),
                "a Windows interface snapshot independently identifies the missing USB phone during an ADB enumeration failure");
            var lazyAdb = host.GetField("LazyAdb", statics)!.GetValue(null)!;
            Check(!(bool)lazyAdb.GetType().GetProperty("IsValueCreated")!.GetValue(lazyAdb)!, "USB event fixture never opens a real ADB backend");
            Console.WriteLine($"PASS {_checks} USB panel event assertions (private window messages and simulated transports)");
            return 0;
        }
        finally { phone.Close(); main.Close(); sinkField.SetValue(app, null); }
    }
}
