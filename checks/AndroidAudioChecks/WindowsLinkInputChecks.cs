using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static partial class WindowInputChecks
    {
        private static void RequirePrivateDesktop()
        {
            // The launcher creates a private desktop. These windows must never cover the user's desktop.
            if (Environment.GetEnvironmentVariable("SONICROUTE_PRIVATE_INPUT_CHECK") != "1")
                throw new InvalidOperationException("Run the input checks on a private desktop.");
        }

        internal static void Run()
        {
            RequirePrivateDesktop();
            nint window = CreateWindowExW(0, "STATIC", "Shared", 0x90000000, 100, 100, 300, 200, 0, 0, 0, 0);
            nint child = CreateWindowExW(0, "STATIC", "", 0x50000000, 20, 20, 130, 95, window, 0, 0, 0);
            nint edit = CreateWindowExW(0, "EDIT", "", 0x50000000, 10, 10, 100, 60, child, 0, 0, 0);
            nint surface = CreateWindowExW(0, "STATIC", "", 0x50000000, 10, 70, 100, 24, child, 0, 0, 0);
            nint button = CreateWindowExW(0, "BUTTON", "Option", 0x50000003, 150, 20, 100, 40, window, 7, 0, 0);
            nint cover = CreateWindowExW(0, "STATIC", "Local app", 0x90000000, 100, 100, 300, 200, 0, 0, 0, 0);
            if (edit == 0 || cover == 0) throw new InvalidOperationException("Input fixture creation failed.");
            try
            {
                SetActiveWindow(cover);
                SetFocus(cover);
                SetCapture(cover);
                Pump();
                uint thread = GetWindowThreadProcessId(window, out uint pid);
                using var input = new LinkWindowInput(window, (int)pid, thread);
                nint foreground = GetForegroundWindow();
                Check(GetActiveWindow() == cover && GetFocus() == cover && foreground != window,
                    "a different overlapping window owns local input before remote control");
                bool readCursor = GetCursorPos(out var start);
                (int Ctrl, int Left) localKeys = (GetAsyncKeyState(0x11) >> 15, GetAsyncKeyState(1) >> 15);
                input.Apply(Pointer(1, 1, 50, 100));
                input.Apply(Pointer(0, 0, 240, 150));
                input.Apply(Pointer(2, 1, 240, 150));
                input.Apply(Pointer(3, 0, 50, 100));
                var pointer = Pump();
                Check(pointer.Any(m => m.Hwnd == surface && m.Id == 0x201 && m.Lp == (20 | (10 << 16)))
                    && pointer.Any(m => m.Hwnd == surface && m.Id == 0x200 && m.Wp == 1)
                    && pointer.Any(m => m.Hwnd == surface && m.Id == 0x202)
                    && pointer.Any(m => m.Hwnd == surface && m.Id == 0x20a && m.Lp == (150 | (200 << 16))),
                    "covered nested control receives client-coordinate click and drag, and screen-coordinate wheel");
                input.Apply(Pointer(1, 1, 50, 50));
                input.Apply(Pointer(2, 1, 50, 50));
                input.Apply([3, .. Encoding.UTF8.GetBytes("声")]);
                input.Apply([2, 0x25, 1]);
                input.Apply([4]);
                var keyboard = Pump();
                var value = new StringBuilder(32);
                GetWindowTextW(edit, value, value.Capacity);
                Check(value.ToString() == "声" && keyboard.Any(m => m.Hwnd == edit && m.Id == 0x100)
                    && keyboard.Any(m => m.Hwnd == edit && m.Id == 0x101),
                    "Unicode text reaches the remote edit and viewer focus loss releases its held key");
                input.Apply(Pointer(1, 1, 170, 40));
                Pump();
                Check(GetFocus() == cover && GetCapture() == cover, "pressing a remote checkbox preserves local focus and mouse capture");
                input.Apply(Pointer(2, 1, 170, 40));
                Check(Pump().Any(m => m.Hwnd == window && m.Id == 0x111 && m.Wp == 7 && m.Lp == button)
                    && SendMessageW(button, 0xf0, 0, 0) == 1, "releasing the checkbox toggles it and notifies its owner once");
                Check(GetForegroundWindow() == foreground && GetActiveWindow() == cover && GetFocus() == cover && GetCapture() == cover
                    && (!readCursor || GetCursorPos(out var end) && end.X == start.X && end.Y == start.Y)
                    && localKeys == (GetAsyncKeyState(0x11) >> 15, GetAsyncKeyState(1) >> 15)
                    && !pointer.Concat(keyboard).Any(m => m.Hwnd == cover && m.Id is >= 0x100 and <= 0x20e),
                    "local focus, physical input state and the covering window are independent of remote input");
                input.Apply(Pointer(1, 2, 50, 100));
                input.Dispose();
                input.Apply([3, (byte)'x']);
                var cleanup = Pump();
                Check(cleanup.Any(m => m.Hwnd == surface && m.Id == 0x205) && !cleanup.Any(m => m.Id == 0x102),
                    "revocation releases the remote button and rejects subsequent text");
                using var wrong = new LinkWindowInput(window, (int)pid, thread + 1);
                wrong.Apply([3, (byte)'x']);
                Check(!Pump().Any(m => m.Id == 0x102), "an obsolete window identity cannot receive input");
            }
            finally { DestroyWindow(cover); DestroyWindow(window); }
            RunMonitor();
        }

        private static byte[] Pointer(byte action, byte button, int x, int y)
        {
            byte[] packet = [1, action, button, 0, 0, 0, 0, 120, 0];
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(3), (ushort)((x * 65535 + 298) / 299));
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5), (ushort)((y * 65535 + 198) / 199));
            return packet;
        }

        private static List<Message> Pump()
        {
            List<Message> events = [];
            while (PeekMessageW(out var msg, 0, 0, 0, 1))
            {
                events.Add(msg);
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            return events;
        }

        [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Message
        { internal nint Hwnd; internal uint Id; internal nuint Wp; internal nint Lp; internal uint Time; internal Point Pt; internal uint Private; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint data);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint w);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint w, out uint pid);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern nint GetActiveWindow();
        [DllImport("user32.dll")] private static extern nint SetActiveWindow(nint w);
        [DllImport("user32.dll")] private static extern nint GetFocus();
        [DllImport("user32.dll")] private static extern nint SetFocus(nint w);
        [DllImport("user32.dll")] private static extern nint SetCapture(nint w);
        [DllImport("user32.dll")] private static extern nint GetCapture();
        [DllImport("user32.dll")] private static extern nint SendMessageW(nint w, uint msg, nuint wp, nint lp);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message m, nint w, uint min, uint max, uint flags);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message m);
        [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message m);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint w, StringBuilder text, int length);
    }
}
