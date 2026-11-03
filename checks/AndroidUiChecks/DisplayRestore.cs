using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int RestoreDisplayLive(string output, string serial)
    {
        Pump(RestoreDisplayLiveAsync(output, serial)); return 0;
    }
    private static async Task RestoreDisplayLiveAsync(string output, string serial)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var adb = (AdbClient)host.GetProperty("Adb", statics)!.GetValue(null)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var token = timeout.Token;
        var device = (await adb.DevicesAsync(token, deduplicate: false)).Single(d => d.Serial == serial && d.State == "device");
        Check(device.State == "device" && !string.IsNullOrWhiteSpace(device.HardwareId),
            "real restore test targets the explicitly selected online device with a resolved hardware identity");
        var app = (await adb.AppsAsync(device, token, ["com.ss.android.ugc.aweme"])).Single();
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(device);
        var displays = (AndroidVirtualDisplayManager)host.GetProperty("Displays", statics)!.GetValue(null)!;
        var sources = (AndroidAudioManager)host.GetProperty("Sources", statics)!.GetValue(null)!;
        var settings = host.GetField("LazySettings", statics)!.GetValue(null)!;
        var prefs = (AndroidAudioSettingsStore)settings.GetType().GetProperty("Value")!.GetValue(settings)!;
        var records = new List<object>();
        IVirtualDisplayClient? client = null; Task? watch = null; object? presentation = null;
        async Task OpenAsync()
        {
            client = await (Task<IVirtualDisplayClient>)host.GetMethod("OpenDisplayAsync", statics)!.Invoke(null, [device, app, 50, 50])!;
            var type = typeof(App).Assembly.GetType("SonicRoute.VirtualDisplayPresentation")!;
            presentation = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [client, new AndroidAppDrag(device, app)], null)!;
            watch = (Task)type.GetMethod("WatchAsync", Hidden)!.Invoke(presentation, null)!;
            await WaitUntilAsync(() => client.State == VirtualDisplayState.Active && sources.Sources.Any(s => s.Session.State.State == CaptureState.Receiving),
                TimeSpan.FromSeconds(25));
        }
        try
        {
            await OpenAsync();
            using var process = Process.GetProcessById(client!.ProcessId);
            var closingUi = new MainWindow(); System.Windows.Application.Current.MainWindow = closingUi;
            closingUi.Close();
            var currentApp = System.Windows.Application.Current;
            typeof(App).GetField("_startupWarmupComplete", Hidden)!.SetValue(currentApp, true);
            typeof(App).GetMethod("ReclaimUiWhenIdle", Hidden)!.Invoke(currentApp, [null, EventArgs.Empty]);
            await Task.Delay(500, token); process.Refresh();
            Check(currentApp.MainWindow == null && !process.HasExited && displays.Count == 1
                && sources.Sources.Single().Session.State.State == CaptureState.Receiving
                && ReferenceEquals(adb, host.GetProperty("Adb", statics)!.GetValue(null)),
                "closing and reclaiming the UI releases its root while preserving ADB, the virtual screen and audio");
            await WaitUntilAsync(() => { process.Refresh(); return process.MainWindowHandle != IntPtr.Zero; }, TimeSpan.FromSeconds(6));
            var window = process.MainWindowHandle;
            GetWindowRect(window, out var outer); GetClientRect(window, out var inner);
            SetWindowPos(window, IntPtr.Zero, 50, 50, 820 + outer.Right - outer.Left - inner.Right,
                560 + outer.Bottom - outer.Top - inner.Bottom, 0x14);
            await WaitUntilAsync(() => prefs.Get(device, app.Package).DisplaySize is { Width: > 1200, Height: > 700 } size
                && Math.Abs((double)size.Width / size.Height - 820d / 560) < .04, TimeSpan.FromSeconds(10));
            var resized = prefs.Get(device, app.Package).DisplaySize!;
            Check(resized.Width > resized.Height, "real framebuffer follows a freely resized landscape window");
            Check(prefs.Get(device, app.Package).WindowSize == new AndroidWindowSize(820, 560),
                "exact desktop client dimensions are saved separately from Android resolution");
            for (int cycle = 0; cycle < 2; cycle++)
            {
                await adb.RunAsync(token, "-s", device.Serial, "shell", "am", "start", "--display", "0", "-a", "android.intent.action.MAIN",
                    "-c", "android.intent.category.LAUNCHER", "-n", "com.ss.android.ugc.aweme/.splash.SplashActivity");
                await WaitUntilAsync(() => client.State == VirtualDisplayState.OnPhone && sources.Sources.Count == 0,
                    TimeSpan.FromSeconds(15));
                var status = (Window?)presentation!.GetType().GetField("_status", Hidden)!.GetValue(presentation);
                Check(status?.IsVisible == true, "phone ownership releases temporary audio and shows the restore action");
                var restore = (Button)status!.GetType().GetField("_restore", Hidden)!.GetValue(status)!;
                restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await WaitUntilAsync(() => client.State == VirtualDisplayState.Active && sources.Sources.Any(s => s.Session.State.State == CaptureState.Receiving),
                    TimeSpan.FromSeconds(25));
                await Task.Delay(5000, token);
                var audio = sources.Sources.Single(); process.Refresh();
                Check(!process.HasExited && client.State == VirtualDisplayState.Active && audio.Session.State.State == CaptureState.Receiving
                    && audio.Session.PacketsDecoded > 0 && presentation.GetType().GetField("_status", Hidden)!.GetValue(presentation) == null,
                    "restore cycle " + (cycle + 1) + " retains the native window, decoded audio and closes only the notice");
                records.Add(new { Cycle = cycle + 1, client.ProcessId, client.State, audio.Session.PacketsDecoded,
                    NonSilentSamples = audio.Session.Pcm?.NonSilentSamplesWritten, Size = prefs.Get(device, app.Package).DisplaySize });
            }
            await displays.CloseDeviceAsync(device); await watch!;
            await prefs.FlushAsync();
            var reloaded = new AndroidAudioSettingsStore(prefs.FilePath);
            Check(reloaded.Get(device, app.Package).DisplaySize == resized && sources.Sources.Count == 0,
                "closing saves the accepted resolution and releases display-owned audio");
            await OpenAsync();
            await WaitUntilAsync(() => client!.State == VirtualDisplayState.Active, TimeSpan.FromSeconds(10));
            Check(prefs.Get(device, app.Package).DisplaySize == resized,
                "reopening restores the same real Android framebuffer without resetting to phone portrait");
            using var reopened = Process.GetProcessById(client!.ProcessId);
            await WaitUntilAsync(() => { reopened.Refresh(); return reopened.MainWindowHandle != IntPtr.Zero; }, TimeSpan.FromSeconds(6));
            GetClientRect(reopened.MainWindowHandle, out var restoredWindow);
            Check(restoredWindow.Right == 820 && restoredWindow.Bottom == 560,
                "reopening restores the actual 820x560 desktop window as well as its ratio");
            records.Add(new { Reopened = true, Size = resized, WindowSize = new AndroidWindowSize(restoredWindow.Right, restoredWindow.Bottom), client!.ProcessId });
        }
        finally
        {
            await displays.CloseDeviceAsync(device); if (watch != null) await watch;
            await sources.DisposeAsync(); await displays.DisposeAsync(); await prefs.FlushAsync(); await store.FlushAsync();
        }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { Passed = true, Assertions = _checks, Device = device, App = app.Package, Records = records,
            Scope = "Real Xiaomi13 Douyin manual restore, WPF notice, audio decoding and real framebuffer persistence; Wave not operated",
            AudibleOutputJudged = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} real display restore assertions");
    }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayRestoreRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out DisplayRestoreRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out DisplayRestoreRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
