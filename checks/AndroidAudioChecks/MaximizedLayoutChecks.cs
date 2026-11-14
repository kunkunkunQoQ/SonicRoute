using System.Reflection;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckMaximizedLayoutAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var settings = new AndroidAudioSettingsStore(Path.Combine(outputDirectory, "settings.json"));
        var phone = new AndroidDevice("layout-phone", "Layout", "device", false);
        const string package = "cn.test.layout";
        await using var window = new NativeVideoWindow("Hidden maximum-size regression", false, true, null, 0, 0, 480, 720);
        window.WindowSizeCommitted += (w, h) => settings.RememberWindow(phone, package, new(w, h));
        int flexUpdates = 0;
        window.FlexResizeRequested += (_, _) => flexUpdates++;
        nint hwnd = await window.CreateAsync(CancellationToken.None);
        window.CommitInitialWindowSize();
        await using var client = new InProcessVirtualDisplayClient(new AdbClient(Path.Combine(outputDirectory, "unused-runtime")));
        Type type = typeof(InProcessVirtualDisplayClient);
        void Set(string field, object value) => type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, value);
        Set("_settings", settings); Set("_device", phone); Set("_package", package); Set("_window", window);
        MethodInfo remember = type.GetMethod("RememberDisplaySize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        remember.Invoke(client, [480, 720]);
        var normal = settings.Get(phone, package);
        nint style = LayoutGetWindowLongPtr(hwnd, -16);
        // Set the real Win32 maximize state without WS_VISIBLE or desktop input.
        LayoutSetWindowLongPtr(hwnd, -16, style | 0x01000000);
        Check(window.IsMaximized, "hidden native window exposes its Win32 maximized state");
        LayoutSendMessage(hwnd, 0x0005, 2, (nint)((900 << 16) | 1600));
        remember.Invoke(client, [1600, 904]);
        var maximized = settings.Get(phone, package);
        Check(maximized.WindowSize == normal.WindowSize && maximized.DisplaySize == normal.DisplaySize && flexUpdates > 0,
            "maximizing still resizes the remote screen but saves neither window nor display size");
        LayoutSetWindowLongPtr(hwnd, -16, style);
        LayoutSendMessage(hwnd, 0x0005, 0, (nint)((744 << 16) | 504));
        remember.Invoke(client, [504, 744]);
        Check(settings.Get(phone, package) is { WindowSize: { Width: 504, Height: 744 }, DisplaySize: { Width: 504, Height: 744 } },
            "restoring and resizing the window saves its normal layout again");
        LayoutSetWindowLongPtr(hwnd, -16, style | 0x01000000);
        LayoutSendMessage(hwnd, 0x0005, 2, (nint)((900 << 16) | 1600));
        remember.Invoke(client, [1600, 904]);
        await client.DisposeAsync();
        remember.Invoke(client, [1600, 904]); // A late frame after close cannot overwrite the saved layout.
        await settings.FlushAsync();
        var reloaded = new AndroidAudioSettingsStore(settings.FilePath).Get(phone, package);
        Check(reloaded.WindowSize is { Width: 504, Height: 744 } && reloaded.DisplaySize is { Width: 504, Height: 744 }
            && window.Closed.IsCompleted, "closing while maximized keeps the last normal layout on disk");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint LayoutGetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint LayoutSetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint LayoutSendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
