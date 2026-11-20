using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SonicRoute.WindowsLink;

internal sealed class LinkMonitorInput(LinkMonitor monitor, Func<LinkWindow, bool>? canControl) : ILinkInput
{
    private sealed record Target(LinkWindow Window, LinkWindowInput Input);
    private readonly object _gate = new();
    private Target? _selected, _hover;
    private bool _closed;
    private long _rateStart;
    private int _count;

    public void Apply(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty) return;
        lock (_gate)
        {
            if (_closed) return;
            if (packet[0] == 4) { ClearTargets(); return; }
            if (Stopwatch.GetElapsedTime(_rateStart) >= TimeSpan.FromSeconds(1))
            {
                _rateStart = Stopwatch.GetTimestamp();
                _count = 0;
            }
            if (++_count > 240) { ClearTargets(); return; }

            // Monitor bounds and WindowFromPhysicalPoint must use the same physical desktop coordinates.
            nint previous = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
            try
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfoW(monitor.Handle, ref info) || info.Bounds.Right <= info.Bounds.Left
                    || info.Bounds.Bottom <= info.Bounds.Top) { ClearTargets(); return; }
                if (_selected != null && !CanRoute(_selected, info.Bounds)) ClearSelection();
                if (packet[0] == 1 && packet.Length == 9)
                    RoutePointer(packet, info.Bounds);
                else if (packet[0] is 2 or 3 && _selected?.Input.HasKeyboardTarget == true)
                    _selected.Input.Apply(packet);
            }
            finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        }
    }

    private bool CanRoute(Target target, Rect monitorBounds)
    {
        var window = target.Window;
        return GetWindowThreadProcessId(window.Handle, out uint pid) == window.ThreadId && pid == window.ProcessId
            && IsWindowVisible(window.Handle) && !IsIconic(window.Handle)
            && GetWindowRect(window.Handle, out var bounds) && bounds.Left < monitorBounds.Right
            && bounds.Right > monitorBounds.Left && bounds.Top < monitorBounds.Bottom && bounds.Bottom > monitorBounds.Top
            && (DwmGetWindowAttribute(window.Handle, 14, out uint cloaked, sizeof(uint)) != 0 || cloaked == 0)
            && canControl?.Invoke(window) != false;
    }

    private void RoutePointer(ReadOnlySpan<byte> packet, Rect bounds)
    {
        byte action = packet[1], button = packet[2];
        if (action > 3 || (action is 1 or 2 && button is not (1 or 2 or 4))) return;
        int x = bounds.Left + (int)((bounds.Right - bounds.Left - 1L) * BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]) / ushort.MaxValue);
        int y = bounds.Top + (int)((bounds.Bottom - bounds.Top - 1L) * BinaryPrimitives.ReadUInt16LittleEndian(packet[5..]) / ushort.MaxValue);

        // Keep a held remote drag on its original window. Hover and wheel may visit
        // another application without redirecting text away from the remote selection.
        Target? target = _selected;
        if (target?.Input.HasPointerCapture != true)
        {
            nint hit = WindowFromPhysicalPoint(new Point(x, y));
            nint root = hit == 0 ? 0 : GetAncestor(hit, 2); // GA_ROOT
            if (target == null || target.Window.Handle != root) target = HoverTarget(root, bounds);
            if (action == 1 && target != _selected)
            {
                ClearSelection();
                _selected = target;
                if (target == _hover) _hover = null;
            }
        }
        target?.Input.ApplyAtPhysicalPoint(packet, x, y);
    }

    private Target? HoverTarget(nint root, Rect bounds)
    {
        if (_hover != null && _hover.Window.Handle == root && CanRoute(_hover, bounds)) return _hover;
        _hover?.Input.Dispose();
        _hover = null;
        if (root != 0 && LinkWindowCapture.ReadWindow(root) is { } window && canControl?.Invoke(window) != false)
            _hover = new Target(window, new LinkWindowInput(root, window.ProcessId, window.ThreadId));
        return _hover;
    }

    private void ClearSelection()
    {
        _selected?.Input.Dispose();
        _selected = null;
    }

    private void ClearTargets()
    {
        ClearSelection();
        _hover?.Input.Dispose();
        _hover = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
            ClearTargets();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal int Size; internal Rect Bounds, Work; internal uint Flags; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, int size);
}
