using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using SonicRoute;

internal static partial class Program
{
    private static class NativeClipboardFixture
    {
        internal static async Task Run(string station)
        {
            static string Name(nint handle)
            {
                var name = new StringBuilder(256);
                if (!GetUserObjectInformationW(handle, 2, name, (uint)name.Capacity * 2, out _)) throw Win32Error();
                return name.ToString();
            }
            if (!station.StartsWith("SonicRouteClip-", StringComparison.Ordinal) || !Guid.TryParseExact(station[15..], "N", out _)
                || Name(GetProcessWindowStation()) != station || Name(GetThreadDesktop(GetCurrentThreadId())) != "ClipboardFixture")
                throw new InvalidOperationException("The native clipboard check requires its own verified window station and desktop.");
            Check(true, "the native fixture is isolated in a named private window station with a separate clipboard");
            using var hwnd = new HwndSource(new HwndSourceParameters("Fixture") { ParentWindow = -3, WindowStyle = 0 });
            Type native = typeof(App).Assembly.GetType("SonicRoute.DesktopClipboardLink")!;
            IDesktopClipboardLink Endpoint() => (IDesktopClipboardLink)Activator.CreateInstance(native, [hwnd, 16368])!;
            using var first = Endpoint(); using var second = Endpoint();
            int one = 0, two = 0;
            first.Changed += () => one++;
            second.Changed += () => two++;
            first.SetListening(true); second.SetListening(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = timeout.Token;
            const string local = "私有剪贴板 • native text";
            void Publish(string value, string? privacy = null)
            {
                if (!OpenClipboard(hwnd.Handle)) throw Win32Error();
                try
                {
                    if (!EmptyClipboard()) throw Win32Error();
                    if (privacy != null) Put(RegisterClipboardFormatW(privacy), [0, 0, 0, 0]);
                    Put(13, Encoding.Unicode.GetBytes(value + "\0"));
                }
                finally { CloseClipboard(); }
            }
            Publish(local);
            while (one == 0 || two == 0) await Task.Delay(5, token);
            Check(await first.ReadAsync(token) == local, "two subscribers share one native notification and read a new Unicode copy");
            first.SetListening(false);
            int previous = two;
            Publish("private", "CanUploadToCloudClipboard");
            while (two == previous) await Task.Delay(5, token);
            Check(await second.ReadAsync(token) == null, "a native CanUploadToCloudClipboard zero blocks outgoing text even after the other listener detaches");
            Publish("history private", "CanIncludeInClipboardHistory");
            Check(await second.ReadAsync(token) == null, "the native history-exclusion DWORD also protects its text");
            Publish("vault fixture", "Clipboard Viewer Ignore");
            Check(await second.ReadAsync(token) == null, "password-manager exclusion data is skipped on the native clipboard");
            long prior = second.Revision;
            const string received = "received on the private native clipboard";
            var result = await second.WriteAsync(received, prior, token);
            Check(result.Written && !((await second.WriteAsync("stale", prior, token)).Written),
                "a native remote write succeeds once and rejects the obsolete clipboard revision");
            if (!OpenClipboard(hwnd.Handle)) throw Win32Error();
            try
            {
                nint data = GetClipboardData(13);
                nint value = GlobalLock(data);
                if (value == 0) throw Win32Error();
                try { Check(Marshal.PtrToStringUni(value) == received, "a different native clipboard consumer can paste the delivered text"); }
                finally { GlobalUnlock(data); }
                Check(GetClipboardData(RegisterClipboardFormatW("SonicRoute.RemoteClipboard.v1")) != 0
                    && GetClipboardData(RegisterClipboardFormatW("CanUploadToCloudClipboard")) != 0,
                    "native writes label remote origin and suppress Windows cloud redistribution");
            }
            finally { CloseClipboard(); }
            Check(await second.ReadAsync(token) == null, "incoming native clipboard text cannot be read back into another sharing session");
        }

        private static void Put(uint format, byte[] data)
        {
            nint memory = GlobalAlloc(0x42, (nuint)data.Length);
            if (memory == 0) throw Win32Error();
            try
            {
                nint address = GlobalLock(memory);
                if (address == 0) throw Win32Error();
                try { Marshal.Copy(data, 0, address, data.Length); } finally { GlobalUnlock(memory); }
                if (SetClipboardData(format, memory) == 0) throw Win32Error();
                memory = 0;
            }
            finally { if (memory != 0) GlobalFree(memory); }
        }
        private static Exception Win32Error() => new Win32Exception(Marshal.GetLastWin32Error());
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint hwnd);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint value);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterClipboardFormatW(string format);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint handle);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
        [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint handle);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
        [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder data, uint bytes, out uint needed);
    }
}
