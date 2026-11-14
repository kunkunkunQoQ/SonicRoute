using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// Dynamic loader and RawInput HID + XInput reader for Win32 gamepad input.
/// Kept completely passive until a window has focus (RawInput without <c>RIDEV_INPUTSINK</c>;
/// XInput timer gated strictly on focus and at least one connected controller).
/// </summary>
internal sealed class NativeGamepadReader : IDisposable
{
    public const int MaxSlots = 4;

    private const ushort HidUsagePageGenericDesktop = 0x01;
    private const ushort HidUsagePageSimulation = 0x02;
    private const ushort HidUsagePageButton = 0x09;
    private const ushort HidUsageJoystick = 0x04;
    private const ushort HidUsageGamepad = 0x05;
    private const ushort HidUsageX = 0x30;
    private const ushort HidUsageY = 0x31;
    private const ushort HidUsageZ = 0x32;
    private const ushort HidUsageRx = 0x33;
    private const ushort HidUsageRy = 0x34;
    private const ushort HidUsageRz = 0x35;
    private const ushort HidUsageHatSwitch = 0x39;
    private const ushort HidUsageAccelerator = 0xC4;
    private const ushort HidUsageBrake = 0xC5;

    private const uint RidiPreparsedData = 0x20000005;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeHid = 2;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevDevNotify = 0x00002000;
    private const int HidPInput = 0;
    private const int HidPStatusSuccess = 0x00110000;

    private static readonly Lazy<XInputGetStateDelegate?> XInputGetStateProc = new(LoadXInputProc);
    private static readonly Lazy<HidPApis?> HidApis = new(LoadHidApis);

    private readonly Dictionary<nint, HidDeviceEntry> _hidDevices = new();
    private readonly ushort[] _usageScratch = new ushort[64];
    private nint _rawInputBuffer;
    private int _rawInputCapacity;
    private bool _disposed;

    public static bool TryPollXInput(
        int slot,
        out ushort buttons,
        out byte leftTrigger,
        out byte rightTrigger,
        out short thumbLX,
        out short thumbLY,
        out short thumbRX,
        out short thumbRY)
    {
        var fn = XInputGetStateProc.Value;
        if (fn != null && (uint)slot < MaxSlots && fn((uint)slot, out var state) == 0)
        {
            buttons = state.Gamepad.wButtons;
            leftTrigger = state.Gamepad.bLeftTrigger;
            rightTrigger = state.Gamepad.bRightTrigger;
            thumbLX = state.Gamepad.sThumbLX;
            thumbLY = state.Gamepad.sThumbLY;
            thumbRX = state.Gamepad.sThumbRX;
            thumbRY = state.Gamepad.sThumbRY;
            return true;
        }

        buttons = 0;
        leftTrigger = 0;
        rightTrigger = 0;
        thumbLX = 0;
        thumbLY = 0;
        thumbRX = 0;
        thumbRY = 0;
        return false;
    }

    public static bool RegisterWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        RawInputDevice[] devices =
        [
            new RawInputDevice
            {
                usUsagePage = HidUsagePageGenericDesktop,
                usUsage = HidUsageGamepad,
                dwFlags = RidevDevNotify,
                hwndTarget = hwnd
            },
            new RawInputDevice
            {
                usUsagePage = HidUsagePageGenericDesktop,
                usUsage = HidUsageJoystick,
                dwFlags = RidevDevNotify,
                hwndTarget = hwnd
            }
        ];
        return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public static void UnregisterIfNoWindowsRemain()
    {
        RawInputDevice[] devices =
        [
            new RawInputDevice
            {
                usUsagePage = HidUsagePageGenericDesktop,
                usUsage = HidUsageGamepad,
                dwFlags = RidevRemove,
                hwndTarget = 0
            },
            new RawInputDevice
            {
                usUsagePage = HidUsagePageGenericDesktop,
                usUsage = HidUsageJoystick,
                dwFlags = RidevRemove,
                hwndTarget = 0
            }
        ];
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public int RemoveHidDevice(nint hDevice)
    {
        if (_hidDevices.Remove(hDevice, out var entry))
        {
            int slot = entry.AssignedSlot;
            entry.Dispose();
            return slot;
        }
        return -1;
    }

    public bool TryProcessRawInput(nint rawInputHandle, int xinputConnectedMask, out GamepadInputState state)
    {
        state = default;
        if (_disposed || rawInputHandle == 0 || HidApis.Value is not { } hid) return false;

        int headerSize = Marshal.SizeOf<RawInputHeader>();
        uint byteSize = 0;
        if (GetRawInputData(rawInputHandle, RidInput, 0, ref byteSize, (uint)headerSize) != 0 || byteSize == 0 || byteSize > 65536)
            return false;

        EnsureRawBuffer((int)byteSize);
        uint read = byteSize;
        if (GetRawInputData(rawInputHandle, RidInput, _rawInputBuffer, ref read, (uint)headerSize) == uint.MaxValue || read < headerSize + 8)
            return false;

        var header = Marshal.PtrToStructure<RawInputHeader>(_rawInputBuffer);
        if (header.dwType != RimTypeHid || header.hDevice == 0)
            return false;

        if (!_hidDevices.TryGetValue(header.hDevice, out var dev))
        {
            dev = InspectAndCacheHidDevice(header.hDevice, hid, xinputConnectedMask);
            _hidDevices[header.hDevice] = dev;
        }

        if (dev.IsIgnored || dev.PreparsedPtr == 0 || dev.Axes.Count == 0 && dev.MaxButtonUsage == 0)
            return false;

        int dwSizeHid = Marshal.ReadInt32(_rawInputBuffer, headerSize);
        int dwCount = Marshal.ReadInt32(_rawInputBuffer, headerSize + 4);
        if (dwSizeHid <= 0 || dwCount <= 0)
            return false;

        int reportsOffset = headerSize + 8;
        if (reportsOffset + (long)dwSizeHid * dwCount > read)
            return false;

        bool parsedAny = false;
        nint lastReportPtr = _rawInputBuffer + reportsOffset + (nint)(dwCount - 1) * dwSizeHid;
        if (TryDecodeHidReport(dev, hid, lastReportPtr, (uint)dwSizeHid, out state))
            parsedAny = true;

        return parsedAny;
    }

    private bool TryDecodeHidReport(
        HidDeviceEntry dev, HidPApis hid, nint reportPtr, uint reportLength, out GamepadInputState state)
    {
        state = default;
        uint usageLen = (uint)_usageScratch.Length;
        int btnStatus = hid.GetUsages(
            HidPInput, HidUsagePageButton, 0,
            _usageScratch, ref usageLen, dev.PreparsedPtr, reportPtr, reportLength);
        if (btnStatus != HidPStatusSuccess && btnStatus != 0 /* tolerant if report has 0 pressed */)
        {
            if ((uint)btnStatus == 0xC0110004u /* HIDP_STATUS_USAGE_NOT_FOUND */)
                usageLen = 0;
            else if (btnStatus < 0)
                return false;
        }

        short lx = 0, ly = 0, rx = 0, ry = 0;
        byte lt = 0, rt = 0;
        ushort hatMask = 0;

        foreach (var axis in dev.Axes)
        {
            if (hid.GetUsageValue(HidPInput, axis.UsagePage, 0, axis.Usage, out uint rawVal, dev.PreparsedPtr, reportPtr, reportLength) != HidPStatusSuccess)
                continue;

            switch (axis.Role)
            {
                case HidAxisRole.LeftX:
                    lx = GamepadMath.NormalizeHidSignedAxis(rawVal, axis.LogicalMin, axis.LogicalMax, axis.BitSize, invertY: false);
                    break;
                case HidAxisRole.LeftY:
                    ly = GamepadMath.NormalizeHidSignedAxis(rawVal, axis.LogicalMin, axis.LogicalMax, axis.BitSize, invertY: true);
                    break;
                case HidAxisRole.RightX:
                    rx = GamepadMath.NormalizeHidSignedAxis(rawVal, axis.LogicalMin, axis.LogicalMax, axis.BitSize, invertY: false);
                    break;
                case HidAxisRole.RightY:
                    ry = GamepadMath.NormalizeHidSignedAxis(rawVal, axis.LogicalMin, axis.LogicalMax, axis.BitSize, invertY: true);
                    break;
                case HidAxisRole.LeftTrigger:
                    lt = GamepadMath.NormalizeHidTrigger(rawVal, axis.LogicalMin, axis.LogicalMax, axis.BitSize);
                    break;
                case HidAxisRole.RightTrigger:
                    rt = GamepadMath.NormalizeHidTrigger(rawVal, axis.LogicalMin, axis.LogicalMax, axis.BitSize);
                    break;
                case HidAxisRole.HatSwitch:
                    int signedHat = axis.LogicalMin < 0
                        ? GamepadMath.SignExtendRawValue(rawVal, axis.LogicalMin, axis.BitSize)
                        : unchecked((int)rawVal);
                    hatMask |= GamepadMath.DecodeHidHatSwitch(signedHat, axis.LogicalMin, axis.LogicalMax);
                    break;
            }
        }

        ReadOnlySpan<ushort> pressed = _usageScratch.AsSpan(0, (int)Math.Min(usageLen, (uint)_usageScratch.Length));
        state = GamepadMath.NormalizeHidInput(
            (byte)dev.AssignedSlot, dev.Layout, pressed, hatMask, lt, rt, lx, ly, rx, ry);
        return true;
    }

    private HidDeviceEntry InspectAndCacheHidDevice(nint hDevice, HidPApis hid, int xinputConnectedMask)
    {
        string? name = TryGetRawDeviceName(hDevice);
        if (!string.IsNullOrEmpty(name) && name.Contains("IG_", StringComparison.OrdinalIgnoreCase))
            return HidDeviceEntry.Ignored();

        var devInfo = new RidDeviceInfo { cbSize = (uint)Marshal.SizeOf<RidDeviceInfo>() };
        uint infoSize = devInfo.cbSize;
        if (GetRawInputDeviceInfo(hDevice, RidiDeviceInfo, ref devInfo, ref infoSize) == uint.MaxValue
            || devInfo.dwType != RimTypeHid
            || devInfo.usUsagePage != HidUsagePageGenericDesktop
            || devInfo.usUsage is not (HidUsageJoystick or HidUsageGamepad))
        {
            return HidDeviceEntry.Ignored();
        }

        uint preparsedBytes = 0;
        if (GetRawInputDeviceInfoBytes(hDevice, RidiPreparsedData, 0, ref preparsedBytes) != 0 || preparsedBytes == 0 || preparsedBytes > 1024 * 1024)
            return HidDeviceEntry.Ignored();

        nint preparsedPtr = Marshal.AllocHGlobal((int)preparsedBytes);
        try
        {
            uint readBytes = preparsedBytes;
            if (GetRawInputDeviceInfoBytes(hDevice, RidiPreparsedData, preparsedPtr, ref readBytes) == uint.MaxValue)
            {
                Marshal.FreeHGlobal(preparsedPtr);
                return HidDeviceEntry.Ignored();
            }

            if (hid.GetCaps(preparsedPtr, out var caps) != HidPStatusSuccess)
            {
                Marshal.FreeHGlobal(preparsedPtr);
                return HidDeviceEntry.Ignored();
            }

            ushort maxBtnUsage = 0;
            if (caps.NumberInputButtonCaps > 0)
            {
                ushort btnCount = caps.NumberInputButtonCaps;
                var btnCaps = new HidPButtonCaps[btnCount];
                if (hid.GetButtonCaps(HidPInput, btnCaps, ref btnCount, preparsedPtr) == HidPStatusSuccess)
                {
                    for (int i = 0; i < btnCount; i++)
                    {
                        if (btnCaps[i].UsagePage != HidUsagePageButton) continue;
                        ushort high = btnCaps[i].IsRange != 0 ? btnCaps[i].UsageMax : btnCaps[i].UsageMin;
                        if (high > maxBtnUsage) maxBtnUsage = high;
                    }
                }
            }

            var rawValues = new List<HidPValueCaps>();
            if (caps.NumberInputValueCaps > 0)
            {
                ushort valCount = caps.NumberInputValueCaps;
                var valCaps = new HidPValueCaps[valCount];
                if (hid.GetValueCaps(HidPInput, valCaps, ref valCount, preparsedPtr) == HidPStatusSuccess)
                {
                    for (int i = 0; i < valCount; i++)
                    {
                        if (valCaps[i].IsRange != 0) continue;
                        rawValues.Add(valCaps[i]);
                    }
                }
            }

            bool hasRz = rawValues.Exists(v => v.UsagePage == HidUsagePageGenericDesktop && v.Usage == HidUsageRz);
            bool hasRx = rawValues.Exists(v => v.UsagePage == HidUsagePageGenericDesktop && v.Usage == HidUsageRx);
            bool hasRy = rawValues.Exists(v => v.UsagePage == HidUsagePageGenericDesktop && v.Usage == HidUsageRy);
            bool hasSimTriggers = rawValues.Exists(v => v.UsagePage == HidUsagePageSimulation && v.Usage is HidUsageBrake or HidUsageAccelerator);
            bool hasAnalogTriggers = hasSimTriggers || (hasRz && hasRx && hasRy);

            var layout = GamepadMath.ClassifyHidLayout(
                (ushort)devInfo.dwVendorId, (ushort)devInfo.dwProductId, maxBtnUsage, hasRz, hasAnalogTriggers);

            var axes = new List<HidAxisCap>(8);
            foreach (var vc in rawValues)
            {
                HidAxisRole? role = vc.UsagePage switch
                {
                    HidUsagePageGenericDesktop => vc.Usage switch
                    {
                        HidUsageX => HidAxisRole.LeftX,
                        HidUsageY => HidAxisRole.LeftY,
                        HidUsageZ => hasRz ? HidAxisRole.RightX : null,
                        HidUsageRz => HidAxisRole.RightY,
                        HidUsageRx => hasRz ? HidAxisRole.LeftTrigger : HidAxisRole.RightX,
                        HidUsageRy => hasRz ? HidAxisRole.RightTrigger : HidAxisRole.RightY,
                        HidUsageHatSwitch => HidAxisRole.HatSwitch,
                        _ => null
                    },
                    HidUsagePageSimulation => vc.Usage switch
                    {
                        HidUsageBrake => HidAxisRole.LeftTrigger,
                        HidUsageAccelerator => HidAxisRole.RightTrigger,
                        _ => null
                    },
                    _ => null
                };

                if (role.HasValue)
                {
                    axes.Add(new HidAxisCap(
                        vc.UsagePage,
                        vc.Usage,
                        role.Value,
                        vc.LogicalMin,
                        vc.LogicalMax,
                        (byte)Math.Clamp((int)vc.BitSize, 1, 32)));
                }
            }

            int slot = AllocateHidSlot(xinputConnectedMask);
            var allocatedPtr = preparsedPtr;
            preparsedPtr = 0; // ownership transferred to HidDeviceEntry
            return new HidDeviceEntry(false, slot, layout, allocatedPtr, maxBtnUsage, axes);
        }
        finally
        {
            if (preparsedPtr != 0) Marshal.FreeHGlobal(preparsedPtr);
        }
    }

    private int AllocateHidSlot(int xinputConnectedMask)
    {
        Span<bool> used = stackalloc bool[MaxSlots];
        for (int i = 0; i < MaxSlots; i++)
        {
            if ((xinputConnectedMask & (1 << i)) != 0) used[i] = true;
        }
        foreach (var entry in _hidDevices.Values)
        {
            if (!entry.IsIgnored && (uint)entry.AssignedSlot < MaxSlots)
                used[entry.AssignedSlot] = true;
        }
        for (int i = 0; i < MaxSlots; i++)
        {
            if (!used[i]) return i;
        }
        return _hidDevices.Count & 3;
    }

    private static string? TryGetRawDeviceName(nint hDevice)
    {
        uint chars = 0;
        if (GetRawInputDeviceInfoString(hDevice, RidiDeviceName, 0, ref chars) != 0 || chars == 0 || chars > 4096)
            return null;
        nint mem = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            uint len = chars;
            if (GetRawInputDeviceInfoString(hDevice, RidiDeviceName, mem, ref len) == uint.MaxValue)
                return null;
            return Marshal.PtrToStringUni(mem);
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    private void EnsureRawBuffer(int size)
    {
        if (_rawInputCapacity >= size && _rawInputBuffer != 0) return;
        if (_rawInputBuffer != 0) Marshal.FreeHGlobal(_rawInputBuffer);
        _rawInputCapacity = Math.Max(size, 4096);
        _rawInputBuffer = Marshal.AllocHGlobal(_rawInputCapacity);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _hidDevices.Values)
            entry.Dispose();
        _hidDevices.Clear();
        if (_rawInputBuffer != 0)
        {
            Marshal.FreeHGlobal(_rawInputBuffer);
            _rawInputBuffer = 0;
            _rawInputCapacity = 0;
        }
    }

    private static XInputGetStateDelegate? LoadXInputProc()
    {
        foreach (string dll in new[] { "xinput1_4.dll", "xinput9_1_0.dll", "xinput1_3.dll" })
        {
            if (!NativeLibrary.TryLoad(dll, out nint handle)) continue;
            if (NativeLibrary.TryGetExport(handle, "XInputGetState", out nint proc))
                return Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(proc);
        }
        return null;
    }

    private static HidPApis? LoadHidApis()
    {
        if (!NativeLibrary.TryLoad("hid.dll", out nint handle)) return null;
        if (!NativeLibrary.TryGetExport(handle, "HidP_GetCaps", out nint getCaps)
            || !NativeLibrary.TryGetExport(handle, "HidP_GetButtonCaps", out nint getBtnCaps)
            || !NativeLibrary.TryGetExport(handle, "HidP_GetValueCaps", out nint getValCaps)
            || !NativeLibrary.TryGetExport(handle, "HidP_GetUsages", out nint getUsages)
            || !NativeLibrary.TryGetExport(handle, "HidP_GetUsageValue", out nint getUsageValue))
        {
            return null;
        }

        return new HidPApis(
            Marshal.GetDelegateForFunctionPointer<HidPGetCapsFn>(getCaps),
            Marshal.GetDelegateForFunctionPointer<HidPGetButtonCapsFn>(getBtnCaps),
            Marshal.GetDelegateForFunctionPointer<HidPGetValueCapsFn>(getValCaps),
            Marshal.GetDelegateForFunctionPointer<HidPGetUsagesFn>(getUsages),
            Marshal.GetDelegateForFunctionPointer<HidPGetUsageValueFn>(getUsageValue));
    }

    private readonly record struct HidAxisCap(
        ushort UsagePage,
        ushort Usage,
        HidAxisRole Role,
        int LogicalMin,
        int LogicalMax,
        byte BitSize);

    private sealed class HidDeviceEntry(
        bool isIgnored,
        int assignedSlot,
        HidGamepadLayoutKind layout,
        nint preparsedPtr,
        ushort maxButtonUsage,
        List<HidAxisCap> axes) : IDisposable
    {
        public bool IsIgnored { get; } = isIgnored;
        public int AssignedSlot { get; } = assignedSlot;
        public HidGamepadLayoutKind Layout { get; } = layout;
        public nint PreparsedPtr { get; private set; } = preparsedPtr;
        public ushort MaxButtonUsage { get; } = maxButtonUsage;
        public List<HidAxisCap> Axes { get; } = axes;

        public static HidDeviceEntry Ignored() => new(true, -1, HidGamepadLayoutKind.GenericDirectInput, 0, 0, []);

        public void Dispose()
        {
            if (PreparsedPtr != 0)
            {
                Marshal.FreeHGlobal(PreparsedPtr);
                PreparsedPtr = 0;
            }
        }
    }

    private sealed record HidPApis(
        HidPGetCapsFn GetCaps,
        HidPGetButtonCapsFn GetButtonCaps,
        HidPGetValueCapsFn GetValueCaps,
        HidPGetUsagesFn GetUsages,
        HidPGetUsageValueFn GetUsageValue);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct RidDeviceInfo
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public uint dwVendorId;
        [FieldOffset(12)] public uint dwProductId;
        [FieldOffset(16)] public uint dwVersionNumber;
        [FieldOffset(20)] public ushort usUsagePage;
        [FieldOffset(22)] public ushort usUsage;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidPCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(46)] public ushort NumberInputButtonCaps;
        [FieldOffset(48)] public ushort NumberInputValueCaps;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HidPButtonCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HidPValueCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort Usage;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint XInputGetStateDelegate(uint dwUserIndex, out XInputState pState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HidPGetCapsFn(nint preparsedData, out HidPCaps capabilities);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HidPGetButtonCapsFn(int reportType, [Out] HidPButtonCaps[] buttonCaps, ref ushort length, nint preparsedData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HidPGetValueCapsFn(int reportType, [Out] HidPValueCaps[] valueCaps, ref ushort length, nint preparsedData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HidPGetUsagesFn(
        int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength,
        nint preparsedData, nint report, uint reportLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HidPGetUsageValueFn(
        int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue,
        nint preparsedData, nint report, uint reportLength);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(nint hDevice, uint uiCommand, ref RidDeviceInfo pData, ref uint pcbSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfoBytes(nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfoString(nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);
}
