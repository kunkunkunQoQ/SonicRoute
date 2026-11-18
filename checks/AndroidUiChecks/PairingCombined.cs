using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int PairingCombinedUi(string output)
    {
        using var log = new StreamWriter(Path.Combine(output, "console.log")) { AutoFlush = true };
        Console.SetOut(log); Console.SetError(log);
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var discovery = new AndroidDeviceDiscovery(_ => throw new Exception("Unexpected device scan"));
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        discovery.Publish([]);
        var clock = new QrDeadlineClock();
        var scans = new ConcurrentQueue<(string Name, TaskCompletionSource<string> Result)>();
        var names = new ConcurrentQueue<string>();
        string currentName = "";
        int generated = 0, activeScans = 0, maxScans = 0, pairCalls = 0, encodingsOnUi = 0, retirementFailure = 0;
        bool ready = false, failNextPair = false;
        TaskCompletionSource? retirement = null;
        var target = new AndroidDevice("adb-combined-fixture._adb-tls-connect._tcp", "Fixture", "device", true);
        var wireless = new AndroidWirelessConnection(async (token, args) =>
        {
            token.ThrowIfCancellationRequested();
            if (args[0] == "mdns")
            {
                int active = Interlocked.Increment(ref activeScans);
                maxScans = Math.Max(maxScans, active);
                var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                scans.Enqueue((currentName, pending));
                try { return await pending.Task.WaitAsync(token); }
                finally
                {
                    if (retirement is { } wait) await wait.Task;
                    Interlocked.Decrement(ref activeScans);
                    if (Interlocked.Exchange(ref retirementFailure, 0) != 0) throw new IOException("Synthetic late cancellation error");
                }
            }
            if (args[0] == "pair")
            {
                Interlocked.Increment(ref pairCalls);
                if (failNextPair) { failNextPair = false; throw new IOException("Synthetic pairing failure"); }
                ready = true; return "Successfully paired [guid=adb-combined-fixture]";
            }
            if (args[0] == "devices") return "List of devices attached\n" + (ready ? target.Serial + " device model:Fixture\n" : "");
            throw new Exception("Unexpected wireless fixture command: " + args[0]);
        }, timeProvider: clock);
        Func<AndroidQrPairing> create = () =>
        {
            if (System.Windows.Application.Current.Dispatcher.CheckAccess()) Interlocked.Increment(ref encodingsOnUi);
            var qr = AndroidQrPairing.Create();
            currentName = qr.ServiceName; names.Enqueue(currentName); Interlocked.Increment(ref generated);
            return qr;
        };
        Func<AndroidDevice, CancellationToken, Task<AndroidEncoderCatalog>> encoders = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new AndroidEncoderCatalog([], []));
        };
        using var page = (PhoneDevicePage)Activator.CreateInstance(typeof(PhoneDevicePage), Hidden, null, [wireless, create, encoders], null)!;
        object? Call(string method, params object?[] args) => typeof(PhoneDevicePage).GetMethod(method, Hidden)!.Invoke(page, args);
        T Find<T>(string name) => (T)page.FindName(name);
        Task? Watch() => (Task?)typeof(PhoneDevicePage).GetField("_qrTask", Hidden)!.GetValue(page);
        var image = Find<Image>("PairQrImage");
        var pair = Find<Button>("PairButton");
        var address = Find<TextBox>("PairAddressBox");
        var code = Find<TextBox>("PairCodeBox");
        var window = new Window { Width = 760, Height = 610, Content = page, ShowInTaskbar = false, ShowActivated = false };
        window.SetResourceReference(Window.BackgroundProperty, "Theme.AppBg");
        void AwaitQr() => Until(() => image.Source != null && Volatile.Read(ref activeScans) == 1);
        void StopAndWait() { Call("Suspend"); Until(() => Watch() == null); }
        void ScanLatest()
        {
            while (scans.TryDequeue(out var scan))
                if (!scan.Result.Task.IsCompleted)
                    scan.Result.TrySetResult($"{scan.Name} _adb-tls-pairing._tcp 192.0.2.5:37000");
        }
        try
        {
            Render(page, 720, 560, Path.Combine(output, "inactive.png"));
            Check(generated == 0 && activeScans == 0, "an inactive/unshown connection page performs no QR work");
            window.Show(); Call("Activate"); Call("ShowConnection"); AwaitQr();
            Check(image.Source is BitmapImage { IsFrozen: true }, "opening the connection page automatically shows a frozen QR image");
            Check(pair.IsEnabled && code.IsEnabled && address.IsEnabled && Find<Button>("ConnectButton").IsEnabled
                && !(bool)typeof(PhoneDevicePage).GetField("_busy", Hidden)!.GetValue(page)!,
                "waiting for QR does not block either manual connection method");
            var left = Find<FrameworkElement>("QrPairCard"); var right = Find<FrameworkElement>("CodePairCard");
            Check(ReferenceEquals(left.Parent, right.Parent) && ((Grid)left.Parent).Parent == Find<Border>("PairingCard"),
                "QR and pairing code share one card");
            foreach (int width in new[] { 440, 720 })
            {
                window.Width = width + 16; window.UpdateLayout(); DrainUi();
                Render(page, (int)Math.Ceiling(page.ActualWidth), (int)Math.Ceiling(page.ActualHeight), Path.Combine(output, $"pairing-light-{width}.png"));
                var l = left.TranslatePoint(new Point(), page); var r = right.TranslatePoint(new Point(), page);
                Check(r.X > l.X && Math.Abs(r.Y - l.Y) < 1 && r.X >= l.X + left.ActualWidth
                    && address.ActualWidth > 160 && Find<Button>("QrButton").ActualWidth == 166,
                    $"QR remains on the left and editable code fields fit on the right at {width} DIPs");
            }
            var originalImage = image.Source; int originalCount = generated;
            address.Text = "192.0.2.5:37000"; code.Text = "123456";
            foreach (string locale in new[] { "en-US", "de-DE", "es-ES", "fr-FR", "ja-JP", "ko-KR", "ru-RU", "zh-TW", "zh-CN" })
            {
                L10n.Instance.SetLanguage(locale); Call("ProfileChanged"); DrainUi();
                Check(!L10n.T("Phone.QrRefreshHint").StartsWith("Phone.") && !L10n.T("Phone.QrRetryHint").StartsWith("Phone."), locale + " has QR refresh/retry text");
            }
            Check(ReferenceEquals(originalImage, image.Source) && generated == originalCount,
                "language and profile refreshes reuse the existing QR instead of re-encoding it");
            ThemeService.Apply("dark", "#00CBE9");
            Render(page, (int)Math.Ceiling(page.ActualWidth), (int)Math.Ceiling(page.ActualHeight), Path.Combine(output, "pairing-dark.png"));
            Check(Find<Button>("QrButton").IsEnabled && image.Source != null, "dark theme retains a scannable white QR area");
            clock.ExpireQr(); Until(() => generated == originalCount + 1 && !ReferenceEquals(originalImage, image.Source)); AwaitQr();
            Check(Volatile.Read(ref pairCalls) == 0 && maxScans == 1 && clock.QrTimers == 1,
                "two-minute expiry automatically rotates QR credentials with a single scanner/deadline");
            var beforeClicks = image.Source; int countBeforeClicks = generated;
            retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            for (int i = 0; i < 15; i++) Find<Button>("QrButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(Task.Delay(80));
            Check(generated == countBeforeClicks && image.Source == null && activeScans == 1,
                "rapid QR clicks coalesce while the previous scanner is retiring");
            retirement.TrySetResult(); retirement = null; AwaitQr();
            Check(generated == countBeforeClicks + 1 && !ReferenceEquals(beforeClicks, image.Source) && maxScans == 1,
                "click refresh starts only one fresh QR after cleanup");
            Check(address.Text == "192.0.2.5:37000" && code.Text == "123456", "QR refresh preserves both manual input fields");
            code.Text = "123"; pair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); AwaitQr();
            Check(pairCalls == 0 && code.Text == "" && address.Text == "192.0.2.5:37000" && pair.IsEnabled,
                "invalid manual pairing resumes QR and clears the entered secret without sending a pairing request");
            Find<TextBox>("ConnectAddressBox").Text = "invalid";
            Find<Button>("ConnectButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); AwaitQr();
            Check(pairCalls == 0 && pair.IsEnabled, "a failed direct connection restores automatic QR waiting");
            retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            retirementFailure = 1;
            code.Text = "123456"; pair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(Task.Delay(80));
            Check(!pair.IsEnabled && pairCalls == 0 && activeScans == 1, "manual pairing waits for QR transport retirement");
            retirement.TrySetResult(); retirement = null;
            Until(() => Find<FrameworkElement>("DeviceCard").Visibility == Visibility.Visible && pair.IsEnabled && Watch() == null);
            Check(pairCalls == 1 && image.Source == null && Find<TextBlock>("OperationStatus").Text == L10n.T("Phone.ConnectedStatus"),
                "manual pairing succeeds even when the canceled QR transport reports a late I/O error");
            Call("ShowConnection"); AwaitQr();
            window.Hide(); Until(() => Watch() == null);
            int hiddenCount = generated; Pump(Task.Delay(120));
            Check(activeScans == 0 && generated == hiddenCount && clock.QrTimers == 0, "hiding the window releases discovery and expiry work");
            window.Show(); AwaitQr();
            Check(generated == hiddenCount + 1, "showing the connection page again automatically restores QR");
            Call("ShowDevice"); Until(() => Watch() == null);
            Check(image.Source == null && activeScans == 0, "selecting a phone device stops QR work");
            Call("ShowConnection"); AwaitQr(); StopAndWait();
            Check(image.Source == null && activeScans == 0, "suspending phone management cancels QR discovery");
            Call("Activate"); AwaitQr();
            ready = false; ScanLatest();
            Until(() => Find<FrameworkElement>("DeviceCard").Visibility == Visibility.Visible && Watch() == null);
            Check(pairCalls == 2 && image.Source == null && activeScans == 0,
                "a QR scan automatically pairs, connects and opens the device page");
            Call("ShowConnection"); AwaitQr();
            failNextPair = true; ScanLatest();
            Until(() => (bool)typeof(PhoneDevicePage).GetField("_qrRetry", Hidden)!.GetValue(page)!);
            int errorCount = generated; Pump(Task.Delay(100));
            Check(generated == errorCount && image.Source == null && Find<TextBlock>("QrStatus").Text == L10n.T("Phone.QrRetryHint"),
                "failed QR pairing uses a quiet delayed retry instead of immediately looping");
            Find<Button>("QrButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); AwaitQr();
            Check(generated == errorCount + 1 && Find<TextBlock>("QrStatus").Text == L10n.T("Phone.QrRefreshHint"),
                "clicking QR bypasses retry delay and restores the normal hint");
            Find<RadioButton>("AirPlayConnectionChoice").IsChecked = true;
            Until(() => Watch() == null);
            var airPlayHost = typeof(App).Assembly.GetType("SonicRoute.AirPlayHost")!;
            Check(Find<Border>("AirPlayCard").IsVisible && !Find<Border>("PairingCard").IsVisible
                && !Find<TextBlock>("OperationStatus").IsVisible
                && airPlayHost.GetField("_receiver", statics)!.GetValue(null) == null && activeScans == 0,
                "AirPlay tab shows only its own status, stops QR and waits for an explicit start");
            var start = (Button)Find<AirPlayReceiverView>("AirPlayConnectionView").FindName("AirPlayToggle");
            Check(start.IsEnabled && Equals(start.Content, L10n.T("Phone.AirPlayStart")),
                "AirPlay is ready to start without an Android phone");
            window.Width = 456; window.UpdateLayout(); DrainUi();
            var buttonRight = start.TranslatePoint(new Point(start.ActualWidth, 0), page).X;
            Check(start.ActualWidth > 40 && buttonRight < page.ActualWidth, "AirPlay start stays visible at a narrow window width");
            Render(page, (int)Math.Ceiling(page.ActualWidth), 260, Path.Combine(output, "airplay-connection.png"));
            Find<RadioButton>("AndroidConnectionChoice").IsChecked = true;
            AwaitQr();
            Check(!Find<Border>("AirPlayCard").IsVisible && Find<Border>("DirectConnectCard").IsVisible,
                "returning to Android restores both pairing methods and direct connection");
            page.Dispose(); Until(() => Watch() == null);
            Check(activeScans == 0 && clock.QrTimers == 0 && names.Distinct().Count() == names.Count,
                "disposal retires all deadlines/scans and every QR rotation uses a fresh identity");
            Check(encodingsOnUi == 0, "every QR encoding runs off the UI thread");
            var lazyAdb = host.GetField("LazyAdb", statics)!.GetValue(null)!;
            Check(!(bool)lazyAdb.GetType().GetProperty("IsValueCreated")!.GetValue(lazyAdb)!, "fixture never creates a real ADB backend");
            File.WriteAllText(Path.Combine(output, "pairing-combined.json"), JsonSerializer.Serialize(new
            { Success = true, Assertions = _checks, QrGenerations = generated, MaxConcurrentScanners = maxScans,
                VirtualExpiry = true, RealPhone = false, UserKeys = false, SharedAdb = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} focused combined-pairing checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { retirement?.TrySetResult(); page.Dispose(); window.Close(); }
    }

    private sealed class QrDeadlineClock : TimeProvider
    {
        private readonly List<Deadline> _deadlines = [];
        internal int QrTimers { get { lock (_deadlines) return _deadlines.Count(t => t.IsQr); } }
        internal void ExpireQr()
        {
            Deadline[] timers; lock (_deadlines) timers = _deadlines.Where(t => t.IsQr).ToArray();
            if (timers.Length != 1) throw new Exception("Expected exactly one QR deadline");
            foreach (var timer in timers) timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Deadline(this, callback, state); timer.Change(dueTime, period); return timer; }
        private sealed class Deadline(QrDeadlineClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal bool IsQr;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._deadlines)
                {
                    if (_disposed) return false;
                    IsQr = dueTime == TimeSpan.FromMinutes(2);
                    if (!owner._deadlines.Contains(this)) owner._deadlines.Add(this);
                    return true;
                }
            }
            internal void Fire() { lock (owner._deadlines) if (_disposed) return; callback(state); }
            public void Dispose() { lock (owner._deadlines) { _disposed = true; owner._deadlines.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
