using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private sealed class ClipboardPhone : IAndroidClipboardClient
    {
        internal readonly ConcurrentQueue<string> Sent = new();
        internal readonly TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? SendGate;
        internal Action<string>? Changed;
        internal AndroidDevice? Device;
        internal bool Stopped;
        internal int SendsStarted;
        private long _revision;
        public long Revision => Interlocked.Read(ref _revision);
        public async Task WatchAsync(AndroidDevice device, Action ready, Action<string> changed, CancellationToken token)
        {
            Device = device; Changed = changed; ready();
            try { await End.Task.WaitAsync(token); }
            finally { Stopped = true; }
        }
        public async Task SendAsync(string text, long expectedRevision, CancellationToken token)
        {
            Interlocked.Increment(ref SendsStarted);
            if (SendGate is { } gate) await gate.Task.WaitAsync(token);
            if (expectedRevision == Revision) Sent.Enqueue(text);
        }
        internal void Copy(string text) { Interlocked.Increment(ref _revision); Changed!(text); }
    }
    private sealed class ClipboardDesktop : IDesktopClipboardLink
    {
        public event Action? Changed;
        internal string Text = "copied before enabling";
        internal bool Listening;
        internal int Reads, Writes;
        internal TaskCompletionSource? ReadGate, WriteGate;
        public long Revision { get; private set; }
        public void SetListening(bool enabled) => Listening = enabled;
        internal void Copy(string text) { Text = text; Revision++; if (Listening) Changed?.Invoke(); }
        public async Task<string?> ReadAsync(CancellationToken token)
        {
            Reads++; string snapshot = Text;
            if (ReadGate is { } gate) { ReadGate = null; await gate.Task.WaitAsync(token); }
            return snapshot;
        }
        public async Task<(bool Written, long Revision)> WriteAsync(string text, long expectedRevision, CancellationToken token)
        {
            if (WriteGate is { } gate) { WriteGate = null; await gate.Task.WaitAsync(token); }
            token.ThrowIfCancellationRequested();
            if (Revision != expectedRevision) return (false, Revision);
            Writes++; Copy(text); return (true, Revision);
        }
        public void Dispose() { Listening = false; Changed = null; }
    }
    private static int PhoneClipboardChecks(string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var discoveryProperty = cache.GetProperty("Discovery", statics)!;
        var previous = discoveryProperty.GetValue(null);
        int scans = 0;
        var one = new AndroidDevice("clipboard-fixture-one", "One", "device", true, "clipboard-hardware-one");
        var two = new AndroidDevice("clipboard-fixture-two", "Two", "device", false, "clipboard-hardware-two");
        var discovery = new AndroidDeviceDiscovery(_ => { scans++; return Task.FromResult<IReadOnlyList<AndroidDevice>>([one, two]); });
        discovery.Publish([one, two]); discoveryProperty.SetValue(null, discovery);
        var clients = new List<ClipboardPhone>(); var desktop = new ClipboardDesktop();
        string file = Path.Combine(output, "settings-" + Guid.NewGuid().ToString("N"), "clipboard-settings.json");
        PhoneClipboardSync New(ClipboardDesktop target) => new(file, Dispatcher.CurrentDispatcher, target,
            () => { var client = new ClipboardPhone(); clients.Add(client); return client; });
        var sync = New(desktop);
        void Ready(AndroidDevice device) => Until(() => sync.Status(device).Contains("双向"));
        try
        {
            Pump(sync.InitializeAsync());
            Check(!sync.IsEnabled(one) && clients.Count == 0 && !desktop.Listening && desktop.Reads == 0,
                "default-off clipboard allocates no phone connection or desktop listener and reads no old clipboard");
            sync.SetEnabled(one, true); Ready(one); var a = clients[0];
            Check(sync.IsEnabled(one) && !sync.IsEnabled(two) && clients.Count == 1 && desktop.Listening && desktop.Reads == 0,
                "enabling one device starts only its event stream and does not upload earlier clipboard contents");
            sync.SetEnabled(one, true);
            Check(clients.Count == 1, "repeated enable does not create duplicate helpers");
            sync.SetEnabled(two, true); Ready(two); var b = clients[1];
            desktop.Copy("电脑复制：中文🙂\n第二行");
            Until(() => a.Sent.Count == 1 && b.Sent.Count == 1);
            Check(a.Sent.Last() == desktop.Text && b.Sent.Last() == desktop.Text && desktop.Writes == 0,
                "Unicode and multiline computer text reach each enabled phone once");
            a.Copy("手机复制：你好🙂");
            Until(() => desktop.Text == "手机复制：你好🙂" && b.Sent.Last() == desktop.Text);
            Check(a.Sent.Count == 1 && desktop.Writes == 1, "phone copy reaches computer and another enabled device without echoing to its origin");
            b.Copy(desktop.Text); DrainUi();
            Check(desktop.Writes == 1 && a.Sent.Count == 1, "identical remote notifications cannot form a clipboard feedback loop");

            a.SendGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int beforeBurst = a.SendsStarted; desktop.Copy("burst-start"); Until(() => a.SendsStarted > beforeBurst);
            for (int i = 0; i < 100; i++) desktop.Copy("burst-" + i);
            a.SendGate.SetResult(); a.SendGate = null;
            Until(() => a.Sent.Last() == "burst-99");
            Check(a.SendsStarted <= beforeBurst + 2, "a busy phone keeps only the newest pending copy instead of a growing send queue");

            var readGate = desktop.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            desktop.Copy("stale delayed read"); a.Copy("newer phone text");
            Until(() => desktop.Text == "newer phone text"); readGate.SetResult(); DrainUi();
            Check(!a.Sent.Contains("stale delayed read") && desktop.Text == "newer phone text",
                "a delayed computer clipboard read cannot overwrite a newer phone copy");

            var writeGate = desktop.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Copy("remote write one"); DrainUi(); a.Copy("remote write two"); DrainUi();
            writeGate.SetResult(); Until(() => desktop.Text == "remote write two");
            Check(desktop.Text == "remote write two", "concurrent phone updates serialize their desktop writes and finish with the newest text");
            writeGate = desktop.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Copy("in-flight remote write"); DrainUi(); desktop.Copy("newer local copy"); writeGate.SetResult();
            Until(() => a.Sent.Last() == "newer local copy");
            Check(desktop.Text == "newer local copy", "a newer computer copy wins while a remote write waits for the clipboard lock");

            a.SendGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int waiting = a.SendsStarted; desktop.Copy("stale send in flight"); Until(() => a.SendsStarted > waiting);
            a.Copy("phone copy while sending"); Until(() => desktop.Text == "phone copy while sending");
            a.SendGate.SetResult(); a.SendGate = null; DrainUi();
            Check(!a.Sent.Contains("stale send in flight"), "the captured phone revision rejects a queued write after a newer phone copy");

            sync.SetEnabled(one, false); Until(() => a.Stopped);
            a.Copy("disabled source"); DrainUi(); desktop.Copy("only second phone"); Until(() => b.Sent.Last() == "only second phone");
            Check(desktop.Text == "only second phone" && !a.Sent.Contains("only second phone") && desktop.Listening,
                "disabling one device stops only that connection and ignores queued notifications from it");
            writeGate = desktop.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int writesBeforeDisable = desktop.Writes;
            b.Copy("pending when disabled"); DrainUi();
            sync.SetEnabled(two, false); Until(() => b.Stopped); writeGate.TrySetResult(); DrainUi();
            Check(desktop.Writes == writesBeforeDisable, "disabling the last device cancels a clipboard write that was still waiting");
            int reads = desktop.Reads; desktop.Copy("off"); DrainUi(); Pump(sync.FlushAsync());
            Check(!desktop.Listening && desktop.Reads == reads, "disabling all devices removes the desktop listener and clipboard reads");
            sync.SetEnabled(two, true); Ready(two); Pump(sync.FlushAsync());
            string settings = File.ReadAllText(file);
            Check(settings.Contains("clipboard-hardware-two") && !settings.Contains("remote write") && !settings.Contains("newer local copy"),
                "the separate settings file persists only enabled devices, never copied text or clipboard history");
            Pump(sync.DisposeAsync().AsTask());
            desktop = new ClipboardDesktop(); sync = New(desktop); Pump(sync.InitializeAsync()); Ready(two);
            Check(!sync.IsEnabled(one) && sync.IsEnabled(two) && desktop.Reads == 0 && scans == 0,
                "restarting restores independent device switches from the small file without scanning cached devices or sending old text");
            var last = clients.Last(); int count = clients.Count;
            last.End.TrySetException(new IOException("fixture disconnection")); Until(() => sync.Status(two).Contains("断"));
            Pump(Task.Delay(100));
            Check(clients.Count == count && !desktop.Listening, "transport failure releases the clipboard listener and does not start a retry polling loop");
            discovery.Publish([one, two]); Ready(two);
            Check(clients.Count == count + 1, "an existing explicit device refresh retries a failed clipboard connection");
            var restored = clients.Last(); discovery.Publish(Array.Empty<AndroidDevice>()); Until(() => restored.Stopped);
            Check(!desktop.Listening && sync.IsEnabled(two), "disconnect suspends live work while retaining the saved device preference");
            discovery.Publish([one, two with { Serial = "clipboard-fixture-two-new-transport" }]); Ready(two);
            Check(clients.Last().Device?.Serial == "clipboard-fixture-two-new-transport", "hardware identity restores only the selected phone after its transport address changes");
            Check(!AndroidClipboardClient.IsSupported(new string('中', 30000)) && AndroidClipboardClient.IsSupported("中文🙂\ntext"),
                "text is bounded by UTF-8 bytes while preserving supported Unicode");
            Check(AndroidClipboardClient.IsOwnedCommand("app_process org.sonicroute.android.ClipboardBridge " + new string('a', 32), new string('a', 32))
                && !AndroidClipboardClient.IsOwnedCommand("app_process other.service " + new string('a', 32), new string('a', 32)),
                "helper cleanup accepts only this clipboard class and its exact session ownership token");
            var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
            Check(!(bool)host.GetProperty("IsAdbCreated", statics)!.GetValue(null)!, "clipboard fixtures touch no real ADB service or system clipboard");
            File.WriteAllText(Path.Combine(output, "clipboard-summary.json"), JsonSerializer.Serialize(new
                { Assertions = _checks, RealAdb = false, RealClipboard = false, Cases = "per-device settings, Unicode, echo suppression, coalescing, races, restore, disconnect and disposal" }));
            Console.WriteLine($"PASS {_checks} clipboard lifecycle and synchronization checks.");
            return 0;
        }
        finally { Pump(sync.DisposeAsync().AsTask()); discoveryProperty.SetValue(null, previous); }
    }
}
