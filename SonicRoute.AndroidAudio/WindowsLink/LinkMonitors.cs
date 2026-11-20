using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SonicRoute.WindowsLink;

internal static class LinkMonitors
{
    internal static LinkMonitor[] GetActive()
    {
        nint previous = SetThreadDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        try { return Enumerate(); }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    private static LinkMonitor[] Enumerate()
    {
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; TryGetDevice(null, index, out var adapter); index++)
            if ((adapter.Flags & 1) != 0 && (adapter.Flags & 8) == 0) // Attached to the desktop, excluding mirroring drivers.
                devices.Add(adapter.DeviceName);

        var monitors = new List<LinkMonitor>();
        EnumDisplayMonitors(0, 0, (handle, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(handle, ref info) || !devices.Contains(info.DeviceName)) return true;
            string name = info.DeviceName[(info.DeviceName.LastIndexOf('\\') + 1)..];
            int width = info.Bounds.Right - info.Bounds.Left, height = info.Bounds.Bottom - info.Bounds.Top;
            if (width <= 0 || height <= 0 || name.Length == 0) return true;

            int number = name.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.AsSpan(7), out int parsed) && parsed > 0 ? parsed : monitors.Count + 1;
            monitors.Add(new LinkMonitor(handle, name, DeviceIdentity(info.DeviceName), new(number, width, height, (info.Flags & 1) != 0)));
            return true;
        }, 0);
        return monitors.OrderByDescending(m => m.Display.IsPrimary).ThenBy(m => m.Display.Number).ToArray();
    }

    internal static bool IsCurrent(LinkMonitor monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor.Handle, ref info) && DeviceIdentity(info.DeviceName) == monitor.DeviceId;
    }

    private static string DeviceIdentity(string adapter)
    {
        // Windows can reuse a DISPLAYn name after unplugging a screen. Bind the request
        // to the device interface(s) on that output as well as the current HMONITOR.
        var paths = new List<string>();
        for (uint index = 0; TryGetDevice(adapter, index, out var device); index++)
            if ((device.Flags & 1) != 0 && !string.IsNullOrEmpty(device.DeviceId)) paths.Add(device.DeviceId);
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        string identity = adapter + "|" + string.Join("|", paths);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static bool TryGetDevice(string? adapter, uint index, out DisplayDevice device)
    {
        device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(adapter, index, ref device, 1); // EDD_GET_DEVICE_INTERFACE_NAME
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Bounds, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceString;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceKey;
    }

    private delegate bool MonitorCallback(nint monitor, nint hdc, nint rect, nint state);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint state);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice result, uint flags);
}
