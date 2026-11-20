using System.Reflection;
using System.Windows.Controls;
using System.Windows.Interop;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int WirelessPanelEventsUi(App app)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var xiaomi = new AndroidDevice("192.0.2.7:32100", "Xiaomi", "device", true, "wifi-one", "wifi-1")
            { ConnectionTarget = "@sonicroute-transport:direct:wifi-1", IdentityFingerprint = "one" };
        var second = new AndroidDevice("192.0.2.8:32110", "Second", "device", true, "wifi-two", "wifi-2")
            { ConnectionTarget = "@sonicroute-transport:direct:wifi-2", IdentityFingerprint = "two" };
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(second);
        string secondId = store.Profile.Id;
        store.RememberDevice(xiaomi);
        string xiaomiId = store.Profile.Id;
        IReadOnlyList<AndroidDevice> available = [xiaomi, second];
        int reads = 0;
        var discovery = new AndroidDeviceDiscovery(_ =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(Volatile.Read(ref available));
        });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        Action<string>? lost = null;
        Action? changed = null;
        int listeners = 0;
        CancellationToken lifetime = default;
        Task Listen(Action<string> onLost, Action onChanged, CancellationToken token)
        {
            Interlocked.Increment(ref listeners);
            lost = onLost;
            changed = onChanged;
            lifetime = token;
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        cache.GetProperty("WatchPanelWireless", statics)!.SetValue(null,
            (Func<Action<string>, Action, CancellationToken, Task>)Listen);
        using var sink = new HwndSource(new HwndSourceParameters("Wireless panel fixture") { WindowStyle = 0 });
        var sinkField = typeof(App).GetField("_activateSink", Hidden)!;
        sinkField.SetValue(app, sink);
        var main = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        var quick = new PhoneAudioWindow(false) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            main.Show();
            ((RadioButton)main.FindName("NavApps")).IsChecked = true;
            Pump((Task)typeof(MainWindow).GetMethod("InitializePhoneManagementAsync", Hidden)!.Invoke(main, null)!);
            var sidebar = (ListBox)main.FindName("PhoneDevicesList");
            bool Active(string id) => sidebar.Items.Cast<object>().Any(row =>
                Equals(row.GetType().GetProperty("Id")!.GetValue(row), id)
                && (bool)row.GetType().GetProperty("IsActive")!.GetValue(row)!);
            Until(() => lost != null && Active(xiaomiId) && Active(secondId));

            int baselineReads = Volatile.Read(ref reads);
            Volatile.Write(ref available, [second]);
            Pump(Task.Run(() => lost!(xiaomi.ConnectionTarget!)));
            Until(() => !Active(xiaomiId) && Active(secondId));
            Check(sidebar.Items.Count == 2 && reads == baselineReads, "main list alone handles a wireless loss without scanning or disconnecting the other phone");

            quick.Show();
            var combo = (ComboBox)quick.FindName("DeviceCombo");
            var badge = (TextBlock)quick.FindName("ConnectionText");
            bool QuickOnline() => badge.Text == L10n.T("Phone.ConnectedStatus");
            Until(() => combo.Items.OfType<AndroidDevice>().Count() == 2 && !QuickOnline());
            Check(listeners == 1 && combo.SelectedItem is AndroidDevice { State: "offline" },
                "second panel shares the first subscription and opens on the original offline phone");

            var replacement = xiaomi with { TransportId = "wifi-3", ConnectionTarget = "@sonicroute-transport:direct:wifi-3" };
            Volatile.Write(ref available, [replacement, second]);
            changed!();
            Until(() => Active(xiaomiId) && Active(secondId) && QuickOnline());
            lost!(xiaomi.ConnectionTarget!);
            DrainUi();
            Check(Active(xiaomiId) && QuickOnline() && Equals(combo.SelectedItem, replacement),
                "late close from an old connection cannot disconnect a new session at the same address");

            Volatile.Write(ref available, [replacement]);
            lost!(second.ConnectionTarget!);
            Until(() => !Active(secondId));
            Check(Active(xiaomiId) && QuickOnline() && sidebar.Items.Count == 2 && store.Profiles.Count == 2,
                "losing the unselected phone leaves the selected Xiaomi online and both saved profiles intact");
            main.Hide();
            Check(!lifetime.IsCancellationRequested, "visible quick panel retains the shared connection subscription");
            Volatile.Write(ref available, []);
            lost!(replacement.ConnectionTarget!);
            Until(() => !QuickOnline());
            Check(combo.Items.OfType<AndroidDevice>().Count() == 2 && store.Profile.Id == xiaomiId,
                "quick panel alone handles loss without a refresh or switching the saved phone");
            quick.Hide();
            Until(() => lifetime.IsCancellationRequested);
            Check(listeners == 1, "last hidden panel cancels the only wireless subscription");
            Console.WriteLine($"PASS {_checks} wireless panel assertions (two simulated phones, no ADB server)");
            return 0;
        }
        finally { quick.Close(); main.Close(); sinkField.SetValue(app, null); }
    }
}
