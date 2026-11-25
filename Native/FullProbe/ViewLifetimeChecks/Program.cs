using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    internal static void Require(bool value, string name)
    {
        if (!value) throw new InvalidDataException(name);
    }

    private sealed class FixtureApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
        protected override void OnExit(ExitEventArgs e) { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", root);
        Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID", Guid.NewGuid().ToString());
        var app = new FixtureApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = ProductResources();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        L10n.Instance.SetLanguage("zh-CN");
        ThemeService.Apply("light", "#00CBE9");
        var work = Dispatcher.CurrentDispatcher.InvokeAsync(() => TestAsync(root)).Task.Unwrap();
        var wait = new DispatcherFrame();
        _ = work.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => wait.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(wait);
        try { work.GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }

    private static async Task TestAsync(string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var assembly = typeof(App).Assembly;
        var cache = assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var host = assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        var phone = new AndroidDevice("probe-phone", "Fixture", "device", false, "stable-probe");
        var discovery = new AndroidDeviceDiscovery(_ => throw new InvalidOperationException("Unexpected device scan"));
        discovery.Publish([phone]);
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        store.RememberDevice(phone);
        var jobs = (AndroidFileTransfers)host.GetField("FileTransfers", statics)!.GetValue(null)!;
        string id = store.Profile.Id;

        (Window Window, PhoneDevicePage Page) OpenView()
        {
            Func<AndroidDevice, CancellationToken, Task<AndroidEncoderCatalog>> encoders =
                (_, _) => Task.FromResult(new AndroidEncoderCatalog([], []));
            var page = (PhoneDevicePage)Activator.CreateInstance(typeof(PhoneDevicePage), instance,
                null, [null, null, encoders], null)!;
            // Lifetime is the actual PhoneDevicePage Activate/Dispose path used by main navigation.
            typeof(PhoneDevicePage).GetMethod("Activate", instance)!.Invoke(page, null);
            typeof(PhoneDevicePage).GetMethod("ShowDevice", instance)!.Invoke(page, null);
            var window = new Window { Content = page, ShowInTaskbar = false, ShowActivated = false,
                Left = -30000, Top = -30000, Width = 400, Height = 300, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Closed += (_, _) => page.Dispose();
            window.Show();
            return (window, page);
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var adb = new AdbClient(root, new AdbSocketClient((IPEndPoint)listener.LocalEndpoint));
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] payload = Encoding.UTF8.GetBytes("confirmed file / 已确认");
        var peer = FileTransferProbe.ServeAsync(listener, accepted, send, payload, deadline.Token);

        var first = OpenView();
        var started = host.GetMethod("StartFileTransfer", statics)!.Invoke(null, [adb, phone, new[] { "hello.txt" }, root, "/sdcard/Download"]);
        Require(Equals(started, true), "Host admitted the verified device");
        await accepted.Task.WaitAsync(deadline.Token);
        first.Window.Close();
        Require(jobs.Get(id)?.Phase == AndroidFilePhase.Running, "Closing the owning management HWND interrupted work");
        var next = OpenView();
        var progress = (FrameworkElement)next.Page.FindName("FileProgressRow");
        Require(progress.Visibility == Visibility.Visible, "Reopened page must show active file");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(string profile) { if (profile == id && jobs.Get(id)?.Phase != AndroidFilePhase.Running) done.TrySetResult(); }
        jobs.Changed += Changed;
        try
        {
            send.SetResult();
            await peer;
            await done.Task.WaitAsync(deadline.Token);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var status = (TextBlock)next.Page.FindName("FileStatus");
            var state = jobs.Get(id);
            Require(state?.Phase == AndroidFilePhase.Saved && status.Text.Contains("hello", StringComparison.OrdinalIgnoreCase), "Reattached view did not report completion: " + status.Text);
            Require((await File.ReadAllBytesAsync(state!.Text, deadline.Token)).SequenceEqual(payload), "Transferred bytes");
            Console.WriteLine("PASS WPF Full: start via AndroidAudioHost, close manager HWND, reopen actual device page with in-flight progress, finish and verify bytes.");
        }
        finally { jobs.Changed -= Changed; next.Window.Close(); await jobs.CloseAsync(); }
    }
}

