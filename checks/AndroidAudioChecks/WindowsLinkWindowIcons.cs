using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WindowsLinkWindowIconsAsync()
    {
        var png = WicIconLoader.LoadDefaultPhoneIconPng() ?? throw new Exception("Missing fixture icon");
        var app = new LinkMedia(Guid.NewGuid(), "Fixture PC", LinkMediaKind.Window, "Drag me", true, 1, false, null, false,
            AppKey: "fixture.exe", IconPng: png);

        static nint Icon(nint hwnd, bool big) => SendTestMessageW(hwnd, 0x7f /* WM_GETICON */, big ? 1 : 0, 0);
        static NativeVideoWindow Viewer(LinkMedia media) => LinkVideoViewer.CreateWindow(media, false, 0, 0);
        static async Task<(byte[] Small, byte[] Big)> ReadViewer(LinkMedia media)
        {
            await using var window = Viewer(media);
            nint hwnd = await window.CreateAsync(default);
            return (ReadIconPixels(Icon(hwnd, false)), ReadIconPixels(Icon(hwnd, true)));
        }

        nint small, big;
        string identity;
        await using (var window = Viewer(app))
        {
            nint hwnd = await window.CreateAsync(default);
            small = Icon(hwnd, false);
            big = Icon(hwnd, true);
            identity = ReadWindowAppUserModelId(hwnd) ?? "";
            Check((GetLong(hwnd, -20) & 1) == 0 && small != 0 && big != 0 && small != big,
                "a Windows Link viewer exposes distinct caption and taskbar icons with a visible caption icon style");
            foreach (var (handle, size) in new[] { (small, Math.Max(16, GetIconMetric(49))), (big, Math.Max(32, GetIconMetric(11))) })
            {
                nint reference = WicIconLoader.TryCreateIcon(png, size);
                try
                {
                    var actual = ReadIconPixels(handle);
                    Check(actual.Length != 0 && actual.SequenceEqual(ReadIconPixels(reference)),
                        $"the viewer's {size}px icon contains the remote application's pixels");
                }
                finally { WicIconLoader.DestroyIconSafe(ref reference); }
            }
        }
        Check(ReadIconPixels(small).Length == 0 && ReadIconPixels(big).Length == 0,
            "closing the viewer releases both owned icon handles");

        await using (var reopened = Viewer(app with { Id = Guid.NewGuid(), Name = "New window title" }))
        await using (var differentApp = Viewer(app with { AppKey = "fixture2.exe" }))
        await using (var differentPeer = Viewer(app with { PeerId = "Other PC" }))
        {
            string? otherAppId = ReadWindowAppUserModelId(await differentApp.CreateAsync(default));
            string? otherPeerId = ReadWindowAppUserModelId(await differentPeer.CreateAsync(default));
            Check(identity.StartsWith("SonicRoute.WindowsLink.") && ReadWindowAppUserModelId(await reopened.CreateAsync(default)) == identity
                && !string.IsNullOrEmpty(otherAppId) && !string.IsNullOrEmpty(otherPeerId)
                && otherAppId != identity && otherPeerId != identity,
                "taskbar identity is stable after reopening and distinguishes other remote applications and devices");
        }
        var absent = await ReadViewer(app with { IconPng = null });
        var damaged = await ReadViewer(app with { IconPng = [0, 1, 2] });
        Check(absent.Small.Length > 0 && absent.Big.Length > 0 && absent.Small.SequenceEqual(damaged.Small)
            && absent.Big.SequenceEqual(damaged.Big), "old peers and damaged image data get both Windows application fallback icons");
        var monitor = await ReadViewer(app with { AppKey = LinkRemoteApp.MonitorKeyPrefix + "Display 1" });
        Check(monitor.Small.Length > 0 && monitor.Big.Length > 0 && !monitor.Small.SequenceEqual(absent.Small)
            && !monitor.Big.SequenceEqual(absent.Big), "a shared monitor uses desktop icons distinct from application fallback icons");
        await using var mirror = new NativeVideoWindow("Android mirror", true, false, png, 0, 0, 160, 100);
        var mirrorHwnd = await mirror.CreateAsync(default);
        Check((GetLong(mirrorHwnd, -20) & 1) != 0 && Icon(mirrorHwnd, false) == 0 && Icon(mirrorHwnd, true) == 0,
            "the Android whole-device mirror keeps its icon-free caption");
        await using var androidApp = new NativeVideoWindow("Android app", false, true, png, 0, 0, 160, 100, "SonicRoute.Display.Phone.App");
        var androidHwnd = await androidApp.CreateAsync(default);
        Check((GetLong(androidHwnd, -20) & 1) == 0 && Icon(androidHwnd, false) != 0 && Icon(androidHwnd, true) != 0
            && ReadWindowAppUserModelId(androidHwnd) == "SonicRoute.Display.Phone.App",
            "an Android application window keeps its caption icon, taskbar icon and Android taskbar identity");
    }

    private static byte[] ReadIconPixels(nint icon)
    {
        if (icon == 0 || !GetIconInfo(icon, out var info)) return [];
        try
        {
            int bytes = GetBitmapBits(info.Color, 0, null);
            if (bytes <= 0) return [];
            byte[] pixels = new byte[bytes];
            return GetBitmapBits(info.Color, bytes, pixels) == bytes ? pixels : [];
        }
        finally { DeleteIconBitmap(info.Color); DeleteIconBitmap(info.Mask); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestIconInfo
    {
        public int IsIcon, XHotspot, YHotspot;
        public nint Mask, Color;
    }
    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")] private static extern int GetIconMetric(int index);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out TestIconInfo info);
    [DllImport("gdi32.dll")] private static extern int GetBitmapBits(nint bitmap, int bytes, [Out] byte[]? pixels);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool DeleteIconBitmap(nint bitmap);
}
