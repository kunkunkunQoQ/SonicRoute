using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SonicRoute.WindowsLink;

internal sealed class LinkWindowInput(nint window, int processId) : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _gate = new();
    private readonly HashSet<byte> _keys = new();
    private readonly HashSet<byte> _buttons = new();
    private long _rateStart;
    private int _rate;
    private bool _closed;

    internal void Apply(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) return;
        lock (_gate)
        {
            if (_closed) return;
            if (packet[0] == 4 || !TargetIsForeground())
            {
                ReleasePressed();
                return;
            }
            if (Stopwatch.GetElapsedTime(_rateStart) >= TimeSpan.FromSeconds(1))
            {
                _rateStart = Stopwatch.GetTimestamp();
                _rate = 0;
            }
            if (++_rate > 240) { ReleasePressed(); return; }
            switch (packet[0])
            {
                case 1 when packet.Length == 9:
                    Mouse(packet);
                    break;
                case 2 when packet.Length == 3 && packet[2] <= 1 && IsAppKey(packet[1]):
                    Keyboard(packet[1], packet[2] == 1);
                    break;
                case 3 when packet.Length is >= 2 and <= 9:
                    Text(packet[1..]);
                    break;
            }
        }
    }

    private bool TargetIsForeground() => GetForegroundWindow() == window
        && GetWindowThreadProcessId(window, out uint actualProcess) != 0 && actualProcess == processId;

    private void Mouse(ReadOnlySpan<byte> packet)
    {
        byte action = packet[1], button = packet[2];
        if (action > 3 || (action is 1 or 2 && button is not (1 or 2 or 4))) return;
        if (DwmGetWindowAttribute(window, 9, out Rect bounds, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(window, out bounds)) return;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return;
        var location = new Point
        {
            X = bounds.Left + (int)((bounds.Right - bounds.Left - 1L) * BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]) / 65535L),
            Y = bounds.Top + (int)((bounds.Bottom - bounds.Top - 1L) * BinaryPrimitives.ReadUInt16LittleEndian(packet[5..]) / 65535L)
        };
        if (GetAncestor(WindowFromPoint(location), 2) != window) return;
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        if (width < 2 || height < 2) return;
        int x = (int)(Math.Clamp(location.X - left, 0, width - 1) * 65535L / (width - 1));
        int y = (int)(Math.Clamp(location.Y - top, 0, height - 1) * 65535L / (height - 1));
        NativeInput move = Pointer(0xC001, x, y);
        if (action == 0) Send([move]);
        else if (action == 3)
        {
            int delta = Math.Clamp((int)BinaryPrimitives.ReadInt16LittleEndian(packet[7..]), -1200, 1200);
            if (delta != 0) Send([move, Pointer(0x0800, data: unchecked((uint)delta))]);
        }
        else if (action == 1)
        {
            if (_buttons.Contains(button)) return;
            if (Send([move, Pointer(ButtonFlag(button, down: true))]) == 2) _buttons.Add(button);
        }
        else if (_buttons.Remove(button)) Send([move, Pointer(ButtonFlag(button, down: false))]);
    }

    private static uint ButtonFlag(byte button, bool down) => (button, down) switch
    {
        (1, true) => 0x0002, (1, false) => 0x0004,
        (2, true) => 0x0008, (2, false) => 0x0010,
        (4, true) => 0x0020, _ => 0x0040
    };

    private static bool IsAppKey(byte key) => key is 0x08 or 0x09 or 0x0D or 0x10 or 0x11 or 0x1B
        or >= 0x21 and <= 0x28 or 0x2D or 0x2E or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A;

    private void Keyboard(byte key, bool down)
    {
        if (down)
        {
            if (Send([Key(key)]) == 1) _keys.Add(key);
        }
        else if (_keys.Remove(key)) Send([Key(key, flags: 2)]);
    }

    private static void Text(ReadOnlySpan<byte> utf8)
    {
        string text;
        try { text = Utf8.GetString(utf8); }
        catch (DecoderFallbackException) { return; }
        if (text.Any(char.IsControl)) return;
        var inputs = new NativeInput[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = Key(0, text[i], 4);
            inputs[i * 2 + 1] = Key(0, text[i], 6);
        }
        Send(inputs);
    }

    private void ReleasePressed()
    {
        if (_keys.Count == 0 && _buttons.Count == 0) return;
        Send(_keys.Select(key => Key(key, flags: 2)).Concat(_buttons.Select(button => Pointer(ButtonFlag(button, false)))).ToArray());
        _keys.Clear();
        _buttons.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            ReleasePressed();
        }
    }

    private static uint Send(NativeInput[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());

    private static NativeInput Pointer(uint flags, int x = 0, int y = 0, uint data = 0)
        => new() { Value = new() { Mouse = new() { X = x, Y = y, Data = data, Flags = flags } } };

    private static NativeInput Key(ushort key, ushort scan = 0, uint flags = 0)
        => new() { Type = 1, Value = new() { Key = new() { Virtual = key, Scan = scan, Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { internal uint Type; internal InputValue Value; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputValue
    {
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyInput Key;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { internal ushort Virtual, Scan; internal uint Flags, Time; internal nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect rect, int size);
}
