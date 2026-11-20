using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static partial class WindowInputChecks
    {
        private const uint SnapshotMessage = 0x8001, StopMessage = 0x8002, ToneMessage = 0x8003;
        private sealed record FixtureWindows(long First, long Second, long Restricted);
        private sealed record InputSnapshot(string First, string Second, string Restricted, int DragDown,
            int DragMove, int DragUp, int WheelSecond, int KeyDown, int KeyUp);

        // The test driver and the three target windows need distinct PIDs so normal sharing checks apply.
        internal static void MonitorFixture(string desktopName, bool renderVideo)
        {
            RequirePrivateDesktop();
            if (!renderVideo && desktopName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The monitor input fixture requires an isolated desktop.");
            nint desktop = OpenDesktopW(desktopName, 0, false, 0x1ff);
            if (desktop == 0 || !SetThreadDesktop(desktop))
                throw new InvalidOperationException("Could not attach the monitor input fixture to its driver's desktop.");
            nint dpi = SetThreadDpiAwarenessContext(-4);
            Rect coverBounds = default;
            if (renderVideo)
            {
                // The foreground can be a tray popup (or another test can have a private desktop).
                // Keep the source at the bottom of z-order under an ordinary window or the shell.
                var covers = LinkWindowCapture.GetWindows().Select(window => window.Handle)
                    .Prepend(GetForegroundWindow()).Append(GetDesktopWindow());
                if (!covers.Any(hwnd => GetWindowRect(hwnd, out coverBounds)
                    && coverBounds.Right - coverBounds.Left >= 420 && coverBounds.Bottom - coverBounds.Top >= 160))
                    throw new InvalidOperationException("The compositor desktop is unavailable.");
            }
            nint CreateRoot(string title, int x)
            {
                if (!renderVideo) return CreateWindowExW(0, "#32770", title, 0x90000000, x, 20, 180, 110, 0, 0, 0, 0);
                // Inactive desktops have no DWM window surface. Only these test HWNDs need composition.
                // Place them beneath the interior of an existing window, at the bottom of z-order without activation.
                int left = coverBounds.Left + (coverBounds.Right - coverBounds.Left - 404) / 2 + (x == 20 ? 0 : 204);
                int top = coverBounds.Top + (coverBounds.Bottom - coverBounds.Top - 144) / 2;
                nint hwnd = CreateWindowExW(0x08000000, "#32770", title, 0x80000000, left, top, 200, 144, 0, 0, 0, 0);
                SetWindowPos(hwnd, 1, left, top, 200, 144, 0x0050);
                return hwnd;
            }
            nint AddChild(nint root, string cls, int y, uint extra) =>
                CreateWindowExW(0, cls, "", 0x50000000 | extra, 12, y, 140, 32, root, 0, 0, 0);
            nint first = CreateRoot("Monitor fixture A", 20), second = CreateRoot("Monitor fixture B", 210),
                restricted = renderVideo ? 0 : CreateRoot("Excluded monitor fixture", 400);
            try
            {
                nint firstEdit = AddChild(first, "EDIT", 8, 0), secondEdit = AddChild(second, "EDIT", 8, 0),
                    restrictedEdit = renderVideo ? 0 : AddChild(restricted, "EDIT", 8, 0);
                nint firstSurface = AddChild(first, "STATIC", 58, 0x100), secondSurface = AddChild(second, "STATIC", 58, 0x100);
                if (firstEdit == 0 || secondEdit == 0 || !renderVideo && restrictedEdit == 0 || firstSurface == 0 || secondSurface == 0)
                    throw new InvalidOperationException("Monitor input fixture window creation failed.");
                if (renderVideo)
                {
                    SetTimer(first, 1, 67, 0);
                }
                Console.WriteLine(JsonSerializer.Serialize(new FixtureWindows(first, second, restricted)));

                string Read(nint hwnd)
                {
                    var text = new StringBuilder(64);
                    GetWindowTextW(hwnd, text, text.Capacity);
                    return text.ToString();
                }

                int down = 0, move = 0, up = 0, wheel = 0, keyDown = 0, keyUp = 0;
                while (GetMessageW(out var message, 0, 0, 0) > 0)
                {
                    if (message.Hwnd == first && message.Id == StopMessage) break;
                    if (message.Hwnd == first && message.Id == 0x113 && renderVideo)
                    {
                        uint color = (uint)(0x303030 + (Environment.TickCount64 / 100 % 64) * 0x010101);
                        nint brush = CreateSolidBrush(color);
                        var rect = new Rect { Right = 200, Bottom = 144 };
                        foreach (var target in new[] { first, second })
                        {
                            nint dc = GetDC(target);
                            FillRect(dc, ref rect, brush);
                            ReleaseDC(target, dc);
                        }
                        GdiFlush();
                        DeleteObject(brush);
                        continue;
                    }
                    if (message.Hwnd == first && message.Id == ToneMessage)
                    {
                        _ = PlayWindowFixtureToneAsync();
                        continue;
                    }
                    if (message.Hwnd == first && message.Id == SnapshotMessage)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new InputSnapshot(Read(firstEdit), Read(secondEdit),
                            Read(restrictedEdit), down, move, up, wheel, keyDown, keyUp)));
                        continue;
                    }
                    if (message.Hwnd == firstSurface && message.Id == 0x204) down++;
                    if (message.Hwnd == firstSurface && message.Id == 0x200 && (message.Wp & 2) != 0) move++;
                    if (message.Hwnd == firstSurface && message.Id == 0x205) up++;
                    if (message.Hwnd == secondSurface && message.Id == 0x20a) wheel++;
                    if (message.Id == 0x100 && message.Wp == 0x11) keyDown++;
                    if (message.Id == 0x101 && message.Wp == 0x11) keyUp++;
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
            finally
            {
                DestroyWindow(restricted);
                DestroyWindow(second);
                DestroyWindow(first);
                if (dpi != 0) SetThreadDpiAwarenessContext(dpi);
            }
        }

        private static Process StartMonitorFixture(bool renderVideo = false)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--windows-link-monitor-input-fixture");
            var desktopName = new StringBuilder(256);
            if (!GetUserObjectInformationW(GetThreadDesktop(GetCurrentThreadId()), 2, desktopName,
                    desktopName.Capacity * sizeof(char), out _) || desktopName.Length == 0)
                throw new InvalidOperationException("Could not identify the private test desktop.");
            start.ArgumentList.Add(renderVideo ? "Default" : desktopName.ToString());
            if (renderVideo) start.ArgumentList.Add("--render-video");
            return Process.Start(start) ?? throw new InvalidOperationException("Could not launch the window fixture.");
        }

        private static void RunMonitor()
        {
            using var fixture = StartMonitorFixture();
            T Read<T>() where T : class =>
                JsonSerializer.Deserialize<T>(fixture.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8))
                    .GetAwaiter().GetResult() ?? throw new EndOfStreamException("Monitor input fixture exited."))!;
            nint local = 0, first = 0;
            nint dpi = SetThreadDpiAwarenessContext(-4);
            try
            {
                var windows = Read<FixtureWindows>();
                first = (nint)windows.First;
                var monitor = LinkMonitors.GetActive().Single(m => m.Display.IsPrimary);
                if (LinkWindowCapture.ReadWindow(first)?.ProcessId != fixture.Id)
                    throw new InvalidOperationException("The native monitor fixture is not visible on the test desktop.");
                InputSnapshot Snapshot()
                {
                    if (!PostMessageW(first, SnapshotMessage, 0, 0)) throw new InvalidOperationException("Input fixture unavailable.");
                    return Read<InputSnapshot>();
                }
                byte[] At(byte action, byte button, int x, int y)
                {
                    byte[] packet = [1, action, button, 0, 0, 0, 0, 120, 0];
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(3), (ushort)Math.Round(x * 65535d / (monitor.Display.Width - 1)));
                    BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), (ushort)Math.Round(y * 65535d / (monitor.Display.Height - 1)));
                    return packet;
                }

                local = CreateWindowExW(0, "STATIC", "Local operator", 0x90000000, 20, 150, 180, 90, 0, 0, 0, 0);
                SetActiveWindow(local);
                SetFocus(local);
                SetCapture(local);
                Pump();
                nint foreground = GetForegroundWindow();
                bool readCursor = GetCursorPos(out var cursor);
                (int Ctrl, int Right) keys = (GetAsyncKeyState(0x11) >> 15, GetAsyncKeyState(2) >> 15);
                bool allow = true;
                using var input = new LinkMonitorInput(monitor, window => allow && window.Handle != (nint)windows.Restricted);
                void Click(int x, int y)
                {
                    input.Apply(At(1, 1, x, y));
                    input.Apply(At(2, 1, x, y));
                }

                Click(60, 45);
                input.Apply([3, .. Encoding.UTF8.GetBytes("声")]);
                input.Apply(At(3, 0, 260, 90));
                input.Apply([3, .. Encoding.UTF8.GetBytes("音")]);
                var typed = Snapshot();
                Check(typed.First == "声音" && typed.Second == "" && typed.WheelSecond == 1,
                    "monitor input types in the remotely clicked edit while scrolling a different window");

                input.Apply(At(1, 2, 60, 90));
                input.Apply(At(0, 0, 260, 90));
                input.Apply(At(2, 2, 260, 90));
                var dragged = Snapshot();
                Check(dragged.DragDown == 1 && dragged.DragMove > 0 && dragged.DragUp == 1,
                    $"a monitor drag ending over another window stays with the original pressed control (down {dragged.DragDown}, move {dragged.DragMove}, up {dragged.DragUp})");

                Click(250, 45);
                input.Apply([3, (byte)'B']);
                Click(450, 45);
                input.Apply([3, (byte)'X']);
                Click(60, 45);
                Click(4, 4);
                input.Apply([3, (byte)'X']);
                var excluded = Snapshot();
                Check(excluded.First == typed.First && excluded.Second == "B" && excluded.Restricted == "",
                    "an excluded window or empty desktop click clears the previous monitor keyboard target");

                input.Apply(At(1, 2, 60, 90));
                input.Apply([2, 0x11, 1]);
                allow = false;
                input.Dispose();
                input.Apply([3, (byte)'X']);
                input.Apply(At(1, 2, 60, 90));
                var revoked = Snapshot();
                Check(revoked.DragDown == 2 && revoked.DragUp == 2 && revoked.KeyDown == 1 && revoked.KeyUp == 1
                    && revoked.First == excluded.First && revoked.Second == excluded.Second && revoked.Restricted == "",
                    "revoking monitor control releases its held button and modifier and rejects later packets");
                Check(local != 0 && GetActiveWindow() == local && GetFocus() == local && GetCapture() == local
                    && GetForegroundWindow() == foreground
                    && (!readCursor || GetCursorPos(out var end) && end.X == cursor.X && end.Y == cursor.Y)
                    && keys == (GetAsyncKeyState(0x11) >> 15, GetAsyncKeyState(2) >> 15),
                    "monitor input preserves local foreground, focus, capture, physical cursor and key state");
            }
            finally
            {
                if (local != 0) DestroyWindow(local);
                if (first != 0) PostMessageW(first, StopMessage, 0, 0);
                if (!fixture.WaitForExit(3000)) fixture.Kill();
                if (dpi != 0) SetThreadDpiAwarenessContext(dpi);
            }
        }

        [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);
        [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nuint wparam, nint lparam);
        [DllImport("user32.dll")] private static extern int GetMessageW(out Message msg, nint window, uint min, uint max);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder name, int length, out int required);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint OpenDesktopW(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(nint desktop);
        [DllImport("user32.dll")] private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
        [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    }
}
