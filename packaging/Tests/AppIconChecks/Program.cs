using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;

// Links the actual production service. The only substitutes are the custom-icon/config store;
// no test reads user configuration, launches SonicRoute, or changes another application's ACL.
internal static class Program
{
    private static readonly List<string> Passed = new();
    private static WindowProc? _windowProc;
    private static IntPtr _fixtureIcon;
    private static string _fixtureMode = "";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fixture") return Fixture(args[1]);
        if (args.Length >= 2 && args[0] == "--probe-pid")
        {
            int pid = int.Parse(args[1]);
            var handle = OpenProcess(0x1000, false, pid);
            int error = Marshal.GetLastWin32Error();
            if (handle != IntPtr.Zero) CloseHandle(handle);
            var watch = Stopwatch.StartNew();
            var icon = AppIconService.GetIconForPid(pid);
            Console.WriteLine($"PID={pid}; limitedQuery={handle != IntPtr.Zero}; error={error}; icon={icon != null}; elapsedMs={watch.Elapsed.TotalMilliseconds:F2}");
            if (icon != null && args.Length == 3)
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create((BitmapSource)icon));
                using var file = File.Create(args[2]); encoder.Save(file);
            }
            return icon == null ? 1 : 0;
        }
        if (args.Length == 4 && args[0] == "--probe-session")
        {
            var icon = AppIconService.GetIconForApp(int.Parse(args[1]), "probe", args[2]);
            Console.WriteLine($"SESSION PID={args[1]}; icon={icon != null}");
            if (icon == null) return 1;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create((BitmapSource)icon));
            using var file = File.Create(args[3]); encoder.Save(file);
            return 0;
        }
        try
        {
            Check("invalid PID", AppIconService.GetIconForPid(0) == null && AppIconService.GetIconForPid(-1) == null);
            Check("exited/missing PID", AppIconService.GetIconForPid(int.MaxValue) == null);
            Check("missing file", AppIconService.GetIconForPath(Path.Combine(AppContext.BaseDirectory, "missing.exe")) == null);
            var path = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
            var image = AppIconService.GetIconForPath(path);
            Check("normal executable icon", image is BitmapSource bitmap && bitmap.IsFrozen);
            Check("successful path cache reused", ReferenceEquals(image, AppIconService.GetIconForPath(path)));
            var windowDefaults = (Lazy<byte[][]>)typeof(AppIconService).GetField("WindowDefaultIconPixels", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Check("file icons do not initialize window-only resources", !windowDefaults.IsValueCreated);
            AppIconService.Resume();
            Check("existing UI resume enables icon metadata", SonicRoute.Core.AudioService.IconMetadataEnabled);
            AppIconService.TrimForIdle();
            Check("existing idle trim disables icon metadata", !SonicRoute.Core.AudioService.IconMetadataEnabled);
            Check("UI lifecycle preserves successful icon cache", ReferenceEquals(image, AppIconService.GetIconForPath(path)));
            using (var self = Process.GetCurrentProcess())
                Check("normal PID uses path cache", ReferenceEquals(image, AppIconService.GetIconForPid(self.Id)));
            var parallel = Enumerable.Range(0, 16).Select(_ => Task.Run(() => AppIconService.GetIconForPath(path))).ToArray();
            Task.WaitAll(parallel);
            Check("concurrent requests share frozen image", parallel.All(task => ReferenceEquals(image, task.Result)));

            Check("session icon works without process access/window", ReferenceEquals(image, AppIconService.GetIconForApp(int.MaxValue, "test", path)));
            using (var self = Process.GetCurrentProcess())
                Check("missing session icon preserves PID fallback", ReferenceEquals(image, AppIconService.GetIconForApp(self.Id, "test", path + ".missing")));
            string temp = Path.Combine(Path.GetTempPath(), "SonicRoute Icon Checks " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string unicode = Path.Combine(temp, "中文 空格,路径.exe");
            try
            {
                File.Copy(path, unicode);
                Check("session path supports Unicode/spaces/commas", SamePixels(image!, AppIconService.GetIconForApp(int.MaxValue, "test", unicode)!));
            }
            finally { File.Delete(unicode); Directory.Delete(temp); }

            var custom = new byte[] { 70, 130, 210, 255 };
            var imported = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, custom, 4);
            imported.Freeze();
            CustomAppIconStore.Image = imported;
            SonicRoute.Core.AppIconFiles.Custom = "test.png";
            Check("custom import wins without PID access", ReferenceEquals(imported, AppIconService.GetIconForApp(int.MaxValue, "test")));
            Check("custom import wins over session icon", ReferenceEquals(imported, AppIconService.GetIconForApp(int.MaxValue, "test", path)));
            AppIconService.InvalidateCustom("test.png");
            CustomAppIconStore.Image = null;
            int reads = CustomAppIconStore.Reads;
            Check("custom miss preserves default fallback", AppIconService.GetIconForApp(int.MaxValue, "test") == null);
            CustomAppIconStore.Image = imported;
            Check("recent miss does not hammer storage", AppIconService.GetIconForApp(int.MaxValue, "test") == null && CustomAppIconStore.Reads == reads + 1);
            if (!args.Contains("--permission-only"))
            {
                Thread.Sleep(5100);
                Check("temporary miss recovers on next request", ReferenceEquals(imported, AppIconService.GetIconForApp(int.MaxValue, "test")));
            }
            SonicRoute.Core.AppIconFiles.Custom = null;

            foreach (var mode in new[] { "class", "message", "alpha", "generic", "hung", "none" })
            {
                using var child = StartFixture(mode);
                try
                {
                    // Prove that the old limited-query path really is denied by Windows, rather
                    // than substituting an exception or pretending this is an elevated test.
                    var denied = OpenProcess(0x1000, false, child.Id);
                    int error = Marshal.GetLastWin32Error();
                    if (denied != IntPtr.Zero) CloseHandle(denied);
                    Check(mode + ": limited process query denied", denied == IntPtr.Zero && error == 5);
                    var watch = Stopwatch.StartNew();
                    var icon = AppIconService.GetIconForPid(child.Id);
                    watch.Stop();
                    Console.WriteLine($"MEASURE {mode} {watch.Elapsed.TotalMilliseconds:F2} ms");
                    if (mode == "class" || mode == "message" || mode == "alpha")
                    {
                        if (mode == "class") Check("window-only resources initialize on fallback", windowDefaults.IsValueCreated);
                        Check(mode + ": icon recovered without process access", icon is BitmapSource source && source.IsFrozen);
                        Check(mode + ": correct icon pixels and alpha", CorrectFixturePixels(icon!, mode == "alpha" ? (byte)128 : (byte)255));
                        Check(mode + ": repeated copy keeps source icon alive", SamePixels(icon!, AppIconService.GetIconForPid(child.Id)!));
                    }
                    else Check(mode + ": safe placeholder", icon == null);
                    Check(mode + ": bounded response", watch.ElapsedMilliseconds < 1500);
                }
                finally { TerminateProcess(child.Handle, 0); child.WaitForExit(5000); }
                Check(mode + ": exited fixture no longer has an icon", AppIconService.GetIconForPid(child.Id) == null);
            }

            // The fallback must not leak GDI/USER objects or destroy borrowed class icons.
            using (var child = StartFixture("class"))
            {
                try
                {
                    for (int i = 0; i < 5; i++) AppIconService.GetIconForPid(child.Id);
                    using var self = Process.GetCurrentProcess();
                    uint gdi = GetGuiResources(self.Handle, 0), user = GetGuiResources(self.Handle, 1);
                    for (int i = 0; i < 100; i++)
                        if (AppIconService.GetIconForPid(child.Id) == null) throw new Exception("Repeated icon copy failed.");
                    Check("100 fallback copies release native resources", GetGuiResources(self.Handle, 0) == gdi && GetGuiResources(self.Handle, 1) == user);
                }
                finally { TerminateProcess(child.Handle, 0); child.WaitForExit(5000); }
            }
            Console.WriteLine($"PASS {Passed.Count} checks; runtime {RuntimeInformation.FrameworkDescription}; real Windows process ACL/window tests, not an elevated UU test.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(string name, bool result)
    {
        if (!result) throw new Exception("FAIL: " + name);
        Passed.Add(name);
        Console.WriteLine("PASS " + name);
    }

    private static bool SamePixels(ImageSource a, ImageSource b)
    {
        byte[] Read(ImageSource image)
        {
            var bitmap = (BitmapSource)image;
            if (bitmap.Format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels;
        }
        return Read(a).SequenceEqual(Read(b));
    }

    private static bool CorrectFixturePixels(ImageSource image, byte alpha)
    {
        var source = (BitmapSource)image;
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - 210) > 1 || Math.Abs(pixels[i + 1] - 110) > 1 || Math.Abs(pixels[i + 2] - 10) > 1 || pixels[i + 3] != alpha)
            {
                Console.WriteLine($"PIXEL {i / 4}: BGRA {pixels[i]},{pixels[i+1]},{pixels[i+2]},{pixels[i+3]}; expected alpha {alpha}; {source.PixelWidth}x{source.PixelHeight}");
                return false;
            }
        return pixels.Length > 0;
    }

    private static Process StartFixture(string mode)
    {
        var process = Process.Start(new ProcessStartInfo(Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe"), "--fixture " + mode)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        // Keep the handle granted by CreateProcess before the child restricts its own DACL.
        _ = process.Handle;
        var ready = process.StandardOutput.ReadLineAsync();
        if (!ready.Wait(10000) || ready.Result != "READY")
        {
            TerminateProcess(process.Handle, 1);
            process.WaitForExit(5000);
            string error = process.StandardError.ReadToEnd();
            process.Dispose();
            throw new Exception("Fixture did not start: " + error);
        }
        return process;
    }

    private static int Fixture(string mode)
    {
        _fixtureMode = mode;
        using var iconBytes = new MemoryStream();
        using (var writer = new BinaryWriter(iconBytes, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)24); writer.Write((byte)24); writer.Write((ushort)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(40 + 24 * 24 * 4 + 24 * 4); writer.Write(22);
            writer.Write(40); writer.Write(24); writer.Write(48); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(new byte[24]);
            for (int i = 0; i < 24 * 24; i++) { writer.Write((byte)210); writer.Write((byte)110); writer.Write((byte)10); writer.Write((byte)(mode == "alpha" ? 128 : 255)); }
            writer.Write(new byte[24 * 4]);
        }
        iconBytes.Position = 0;
        using var fixtureIcon = new System.Drawing.Icon(iconBytes);
        _fixtureIcon = fixtureIcon.Handle;
        _windowProc = WindowMessage;
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Name = "SonicRouteIconCheck" + mode,
            Proc = Marshal.GetFunctionPointerForDelegate(_windowProc), Icon = mode == "class" || mode == "alpha" ? _fixtureIcon : IntPtr.Zero };
        if (mode == "generic") cls.Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        if (RegisterClassEx(ref cls) == 0) throw new System.ComponentModel.Win32Exception();
        if (mode != "none" && CreateWindowEx(0, cls.Name, "SonicRoute owned icon fixture", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        // Only this disposable child is affected. It exits when the parent closes its test.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(D;;0x1000;;;WD)(A;;GA;;;WD)", 1, out var descriptor, out _))
            throw new System.ComponentModel.Win32Exception();
        try { if (!SetKernelObjectSecurity(GetCurrentProcess(), 4, descriptor)) throw new System.ComponentModel.Win32Exception(); }
        finally { LocalFree(descriptor); }
        Console.WriteLine("READY");
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref message);
        return 0;
    }

    private static IntPtr WindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == 0x007F)
        {
            if (_fixtureMode == "hung") Thread.Sleep(10000);
            if (_fixtureMode == "message") return _fixtureIcon;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Proc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, Menu;
        public string Name;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extra, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetKernelObjectSecurity(IntPtr handle, uint information, IntPtr descriptor);
}

namespace SonicRoute.Core
{
    internal static class AudioService
    {
        internal static bool IconMetadataEnabled;
        internal static void SetAppIconMetadataEnabled(bool enabled) => IconMetadataEnabled = enabled;
    }
    internal static class ConfigService { internal static object Load() => new object(); }
    internal static class AppIconFiles
    {
        internal static string? Custom;
        internal static string? GetFileName(object config, string processName) => Custom;
    }
}
namespace SonicRoute
{
    internal static class CustomAppIconStore
    {
        internal static ImageSource? Image;
        internal static int Reads;
        internal static ImageSource? Load(string fileName) { Reads++; return Image; }
    }
}
