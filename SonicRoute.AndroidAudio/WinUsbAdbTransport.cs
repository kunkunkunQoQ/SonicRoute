using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SonicRoute.AndroidAudio;

internal sealed record WinUsbDeviceDescriptor(string DevicePath, string? InstanceId);

internal sealed class WinUsbAdbLink : IAdbPacketLink
{
    public static readonly Guid AndroidAdbInterfaceGuid = new("F72FE0D4-CBCB-407D-8814-9ED673D0DD6B");

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;

    private const uint PolicyAutoClearStall = 0x02;
    private const uint PolicyAllowPartialReads = 0x05;
    private const byte UsbDeviceDescriptorType = 0x01;
    private const byte UsbStringDescriptorType = 0x03;
    private const int UsbdPipeTypeBulk = 2;
    private const int TransferStageSize = 256 * 1024;

    private readonly SafeFileHandle _deviceHandle;
    private readonly IntPtr _winUsbHandle;
    private readonly byte _bulkInPipeId;
    private readonly byte _bulkOutPipeId;
    private readonly int _maxPacketSize;

    private readonly SemaphoreSlim _rxOpLock = new(1, 1);
    private readonly SemaphoreSlim _txOpLock = new(1, 1);
    private readonly byte[] _rxStage;
    private readonly byte[] _txStage;
    private GCHandle _rxPin;
    private GCHandle _txPin;
    private IntPtr _rxOverlapped;
    private IntPtr _txOverlapped;
    private readonly ManualResetEvent _rxEvent = new(false);
    private readonly ManualResetEvent _txEvent = new(false);
    private int _disposed;

    private WinUsbAdbLink(
        string devicePath,
        string? serialNumber,
        SafeFileHandle deviceHandle,
        IntPtr winUsbHandle,
        byte bulkInPipeId,
        byte bulkOutPipeId,
        int maxPacketSize)
    {
        DevicePath = devicePath;
        SerialNumber = serialNumber;
        _deviceHandle = deviceHandle;
        _winUsbHandle = winUsbHandle;
        _bulkInPipeId = bulkInPipeId;
        _bulkOutPipeId = bulkOutPipeId;
        _maxPacketSize = maxPacketSize;

        _rxStage = new byte[TransferStageSize];
        _txStage = new byte[TransferStageSize];
        _rxPin = GCHandle.Alloc(_rxStage, GCHandleType.Pinned);
        _txPin = GCHandle.Alloc(_txStage, GCHandleType.Pinned);
        _rxOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        _txOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
    }

    public string DevicePath { get; }
    public string? SerialNumber { get; }
    public int MaxPacketSize => _maxPacketSize;
    public bool SupportsTlsUpgrade => false;
    public string? PeerCertificateSha256 => null;

    public static IReadOnlyList<WinUsbDeviceDescriptor> EnumeratePresentDevices()
    {
        Guid interfaceGuid = AndroidAdbInterfaceGuid;
        IntPtr devInfoSet = SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            return [];

        var list = new List<WinUsbDeviceDescriptor>();
        try
        {
            uint index = 0;
            while (true)
            {
                var ifaceData = new SpDeviceInterfaceData { cbSize = (uint)(IntPtr.Size == 8 ? 32 : 28) };
                if (!SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref interfaceGuid, index, ref ifaceData))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) break;
                    break;
                }
                index++;

                var devInfoData = new SpDevinfoData { cbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                _ = SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, IntPtr.Zero, 0, out uint requiredSize, ref devInfoData);
                if (requiredSize == 0 && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    continue;
                if (requiredSize < 8 || requiredSize > 32768)
                    continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    Marshal.WriteInt32(detailBuf, 0, IntPtr.Size == 8 ? 8 : 6);
                    devInfoData = new SpDevinfoData { cbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                    if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, detailBuf, requiredSize, out _, ref devInfoData))
                        continue;
                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    string? instanceId = TryReadDeviceInstanceId(devInfoSet, ref devInfoData);
                    list.Add(new WinUsbDeviceDescriptor(path, instanceId));
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
        return list;
    }

    public static WinUsbAdbLink? TryOpen(string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);
        SafeFileHandle? fileHandle = CreateFileW(
            devicePath,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (fileHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            fileHandle.Dispose();
            if (err is ErrorFileNotFound or ErrorAccessDenied or 32 /* ERROR_SHARING_VIOLATION */)
                return null;
            throw new IOException($"Failed to open Android WinUSB interface '{devicePath}' (Win32 {err}).", new Win32Exception(err));
        }

        IntPtr winUsbHandle = IntPtr.Zero;
        try
        {
            if (!WinUsb_Initialize(fileHandle, out winUsbHandle))
            {
                int err = Marshal.GetLastWin32Error();
                if (err is ErrorAccessDenied or 31 /* ERROR_GEN_FAILURE */ or ErrorFileNotFound)
                    return null;
                throw new IOException($"WinUsb_Initialize failed for '{devicePath}' (Win32 {err}).", new Win32Exception(err));
            }

            if (!WinUsb_QueryInterfaceSettings(winUsbHandle, 0, out var ifaceDesc))
                return null;
            if (ifaceDesc.bInterfaceClass != 0xFF || ifaceDesc.bInterfaceSubClass != 0x42 || ifaceDesc.bInterfaceProtocol != 0x01)
                return null;

            byte bulkIn = 0;
            byte bulkOut = 0;
            int maxPacket = 512;
            for (byte i = 0; i < ifaceDesc.bNumEndpoints; i++)
            {
                if (!WinUsb_QueryPipe(winUsbHandle, 0, i, out var pipe)) continue;
                if (pipe.PipeType != UsbdPipeTypeBulk) continue;
                if ((pipe.PipeId & 0x80) != 0)
                {
                    bulkIn = pipe.PipeId;
                    if (pipe.MaximumPacketSize > 0) maxPacket = pipe.MaximumPacketSize;
                }
                else
                {
                    bulkOut = pipe.PipeId;
                }
            }

            if (bulkIn == 0 || bulkOut == 0)
                return null;

            ConfigurePipe(winUsbHandle, bulkIn, allowPartialReads: true);
            ConfigurePipe(winUsbHandle, bulkOut, allowPartialReads: false);

            string? usbSerial = TryReadUsbSerialNumber(winUsbHandle);
            var link = new WinUsbAdbLink(devicePath, usbSerial, fileHandle, winUsbHandle, bulkIn, bulkOut, maxPacket);
            fileHandle = null;
            winUsbHandle = IntPtr.Zero;
            return link;
        }
        finally
        {
            if (winUsbHandle != IntPtr.Zero) WinUsb_Free(winUsbHandle);
            fileHandle?.Dispose();
        }
    }

    public async ValueTask<(AdbPacketHeader Header, int PayloadLength)> ReadPacketAsync(byte[] payloadBuffer, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        byte[] headerBuf = new byte[AdbPacketHeader.WireSize];
        await ReadExactUsbAsync(headerBuf, headerBuf.Length, allowZeroPacketSkip: true, token).ConfigureAwait(false);
        if (!AdbPacketHeader.TryParse(headerBuf, out var header))
            throw new InvalidDataException($"Invalid ADB USB packet header magic (cmd=0x{header.Command:X8}).");

        if (header.DataLength > (uint)payloadBuffer.Length)
            throw new InvalidDataException($"ADB USB packet payload {header.DataLength} bytes exceeds buffer {payloadBuffer.Length} bytes.");

        int len = (int)header.DataLength;
        if (len > 0)
            await ReadExactUsbAsync(payloadBuffer, len, allowZeroPacketSkip: false, token).ConfigureAwait(false);
        return (header, len);
    }

    public async ValueTask WritePacketAsync(AdbPacketHeader header, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        byte[] headerBuf = new byte[AdbPacketHeader.WireSize];
        header.WriteTo(headerBuf);
        await WriteExactUsbAsync(headerBuf, token).ConfigureAwait(false);
        if (!payload.IsEmpty)
            await WriteExactUsbAsync(payload, token).ConfigureAwait(false);
    }

    public Task UpgradeToTlsAsync(
        System.Security.Cryptography.X509Certificates.X509Certificate2 clientCertificate,
        Func<System.Security.Cryptography.X509Certificates.X509Certificate2?, bool>? serverCertificateValidator,
        string? targetHost,
        CancellationToken token) =>
        throw new NotSupportedException("ADB over WinUSB does not use STLS transport upgrade.");

    private async Task ReadExactUsbAsync(byte[] destination, int totalBytes, bool allowZeroPacketSkip, CancellationToken token)
    {
        await _rxOpLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int offset = 0;
            int zlpCount = 0;
            while (offset < totalBytes)
            {
                int chunk = Math.Min(totalBytes - offset, _rxStage.Length);
                int read = await TransferPipeAsync(
                    isRead: true,
                    _bulkInPipeId,
                    _rxPin.AddrOfPinnedObject(),
                    chunk,
                    _rxOverlapped,
                    _rxEvent,
                    token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (allowZeroPacketSkip && offset == 0 && ++zlpCount <= 4)
                        continue;
                    throw new EndOfStreamException("ADB WinUSB bulk IN endpoint returned unexpected zero-length packet.");
                }
                zlpCount = 0;
                Buffer.BlockCopy(_rxStage, 0, destination, offset, read);
                offset += read;
            }
        }
        finally
        {
            _rxOpLock.Release();
        }
    }

    private async Task WriteExactUsbAsync(ReadOnlyMemory<byte> source, CancellationToken token)
    {
        await _txOpLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int offset = 0;
            while (offset < source.Length)
            {
                int chunk = Math.Min(source.Length - offset, _txStage.Length);
                source.Slice(offset, chunk).Span.CopyTo(_txStage);
                int written = await TransferPipeAsync(
                    isRead: false,
                    _bulkOutPipeId,
                    _txPin.AddrOfPinnedObject(),
                    chunk,
                    _txOverlapped,
                    _txEvent,
                    token).ConfigureAwait(false);
                if (written <= 0)
                    throw new EndOfStreamException("ADB WinUSB bulk OUT endpoint failed to transfer data.");
                offset += written;
            }
        }
        finally
        {
            _txOpLock.Release();
        }
    }

    private async Task<int> TransferPipeAsync(
        bool isRead,
        byte pipeId,
        IntPtr bufferPtr,
        int length,
        IntPtr overlappedPtr,
        ManualResetEvent evt,
        CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        token.ThrowIfCancellationRequested();

        evt.Reset();
        var overlapped = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };
        Marshal.StructureToPtr(overlapped, overlappedPtr, fDeleteOld: false);

        bool completedSync = isRead
            ? WinUsb_ReadPipe(_winUsbHandle, pipeId, bufferPtr, (uint)length, IntPtr.Zero, overlappedPtr)
            : WinUsb_WritePipe(_winUsbHandle, pipeId, bufferPtr, (uint)length, IntPtr.Zero, overlappedPtr);

        if (completedSync)
        {
            if (!WinUsb_GetOverlappedResult(_winUsbHandle, overlappedPtr, out uint bytesImmediate, bWait: false))
                throw CreatePipeException(Marshal.GetLastWin32Error(), token);
            return (int)bytesImmediate;
        }

        int initialErr = Marshal.GetLastWin32Error();
        if (initialErr != ErrorIoPending)
            throw CreatePipeException(initialErr, token);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisteredWaitHandle? waitReg = ThreadPool.RegisterWaitForSingleObject(
            evt,
            static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            tcs,
            Timeout.Infinite,
            executeOnlyOnce: true);

        using var cancelReg = token.Register(
            static state =>
            {
                var (handle, ov) = ((SafeFileHandle, IntPtr))state!;
                try { CancelIoEx(handle, ov); } catch { }
            },
            (_deviceHandle, overlappedPtr));

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            waitReg?.Unregister(null);
        }

        // Always block on bWait: true so kernel completes any CancelIoEx before we touch the pinned buffer again.
        if (!WinUsb_GetOverlappedResult(_winUsbHandle, overlappedPtr, out uint bytesAsync, bWait: true))
            throw CreatePipeException(Marshal.GetLastWin32Error(), token);

        return (int)bytesAsync;
    }

    private static Exception CreatePipeException(int win32Error, CancellationToken token)
    {
        if (win32Error == ErrorOperationAborted && token.IsCancellationRequested)
            return new OperationCanceledException(token);
        return new IOException($"ADB WinUSB I/O error (Win32 {win32Error}).", new Win32Exception(win32Error));
    }

    private static void ConfigurePipe(IntPtr winUsbHandle, byte pipeId, bool allowPartialReads)
    {
        byte one = 1;
        _ = WinUsb_SetPipePolicy(winUsbHandle, pipeId, PolicyAutoClearStall, 1, ref one);
        if (allowPartialReads)
            _ = WinUsb_SetPipePolicy(winUsbHandle, pipeId, PolicyAllowPartialReads, 1, ref one);
    }

    private static string? TryReadUsbSerialNumber(IntPtr winUsbHandle)
    {
        byte[] devDesc = new byte[18];
        if (!WinUsb_GetDescriptor(winUsbHandle, UsbDeviceDescriptorType, 0, 0, devDesc, (uint)devDesc.Length, out uint len) || len < 18)
            return null;
        byte serialIndex = devDesc[16];
        if (serialIndex == 0) return null;

        byte[] strDesc = new byte[256];
        if (!WinUsb_GetDescriptor(winUsbHandle, UsbStringDescriptorType, serialIndex, 0x0409, strDesc, (uint)strDesc.Length, out uint strLen) || strLen < 4)
            return null;
        int actual = Math.Min((int)strLen, strDesc[0]);
        if (actual <= 2 || strDesc[1] != UsbStringDescriptorType) return null;
        string serial = Encoding.Unicode.GetString(strDesc, 2, actual - 2).Trim('\0').Trim();
        return serial.Length > 0 ? serial : null;
    }

    private static string? TryReadDeviceInstanceId(IntPtr devInfoSet, ref SpDevinfoData devInfoData)
    {
        char[] buf = ArrayPool<char>.Shared.Rent(512);
        try
        {
            if (SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, buf, buf.Length, out uint req))
            {
                int len = Math.Max(0, (int)req - 1);
                return new string(buf, 0, len).TrimEnd('\0');
            }
            return null;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buf);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { WinUsb_AbortPipe(_winUsbHandle, _bulkInPipeId); } catch { }
        try { WinUsb_AbortPipe(_winUsbHandle, _bulkOutPipeId); } catch { }
        try { CancelIoEx(_deviceHandle, IntPtr.Zero); } catch { }

        await _rxOpLock.WaitAsync().ConfigureAwait(false);
        await _txOpLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_winUsbHandle != IntPtr.Zero)
                WinUsb_Free(_winUsbHandle);
            _deviceHandle.Dispose();
            if (_rxPin.IsAllocated) _rxPin.Free();
            if (_txPin.IsAllocated) _txPin.Free();
            if (_rxOverlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_rxOverlapped); _rxOverlapped = IntPtr.Zero; }
            if (_txOverlapped != IntPtr.Zero) { Marshal.FreeHGlobal(_txOverlapped); _txOverlapped = IntPtr.Zero; }
            _rxEvent.Dispose();
            _txEvent.Dispose();
        }
        finally
        {
            _rxOpLock.Release();
            _txOpLock.Release();
            _rxOpLock.Dispose();
            _txOpLock.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbInterfaceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        [Out] char[] deviceInstanceId,
        int deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_QueryInterfaceSettings(IntPtr interfaceHandle, byte alternateInterfaceNumber, out UsbInterfaceDescriptor usbAltInterfaceDescriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_QueryPipe(IntPtr interfaceHandle, byte alternateInterfaceNumber, byte pipeIndex, out WinUsbPipeInformation pipeInformation);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref byte value);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_GetDescriptor(
        IntPtr interfaceHandle,
        byte descriptorType,
        byte index,
        ushort languageId,
        [Out] byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_ReadPipe(
        IntPtr interfaceHandle,
        byte pipeId,
        IntPtr buffer,
        uint bufferLength,
        IntPtr lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_WritePipe(
        IntPtr interfaceHandle,
        byte pipeId,
        IntPtr buffer,
        uint bufferLength,
        IntPtr lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_GetOverlappedResult(
        IntPtr interfaceHandle,
        IntPtr overlapped,
        out uint lpNumberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool bWait);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_AbortPipe(IntPtr interfaceHandle, byte pipeId);
}

/// <summary>
/// Event-driven Win32 DBT_DEVICEINTERFACE notification window monitoring Android USB arrival/removal with zero polling.
/// </summary>
internal sealed class WinUsbDeviceWatcher : IDisposable
{
    private const int WmDestroy = 0x0002;
    private const int WmDeviceChange = 0x0219;
    private const int WmClose = 0x0010;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const uint DbtDevTypDeviceInterface = 5;
    private const uint DeviceNotifyWindowHandle = 0x00000000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Action _onChanged;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private IntPtr _hwnd;
    private IntPtr _notifyHandle;
    private WndProcDelegate? _wndProcDelegate;
    private int _disposed;

    public WinUsbDeviceWatcher(Action onChanged)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "SonicRoute.WinUsbWatcher" };
        _thread.Start();
        _ready.Wait();
    }

    private void MessageLoop()
    {
        try
        {
            string className = "SonicRouteAdbUsbWatcher_" + Environment.ProcessId + "_" + Environment.CurrentManagedThreadId;
            _wndProcDelegate = WndProc;
            IntPtr hInstance = GetModuleHandleW(null);
            var wndClass = new WndClassExW
            {
                cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = hInstance,
                lpszClassName = className
            };
            ushort atom = RegisterClassExW(ref wndClass);
            if (atom == 0) return;
            try
            {
                _hwnd = CreateWindowExW(0, className, "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (_hwnd != IntPtr.Zero)
                {
                    var filter = new DevBroadcastDeviceInterface
                    {
                        dbcc_size = (uint)Marshal.SizeOf<DevBroadcastDeviceInterface>(),
                        dbcc_devicetype = DbtDevTypDeviceInterface,
                        dbcc_classguid = WinUsbAdbLink.AndroidAdbInterfaceGuid
                    };
                    IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<DevBroadcastDeviceInterface>());
                    try
                    {
                        Marshal.StructureToPtr(filter, ptr, false);
                        _notifyHandle = RegisterDeviceNotificationW(_hwnd, ptr, DeviceNotifyWindowHandle);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }
            finally
            {
                _ready.Set();
            }

            if (_hwnd == IntPtr.Zero) return;
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch
        {
            _ready.Set();
        }
        finally
        {
            if (_notifyHandle != IntPtr.Zero)
            {
                UnregisterDeviceNotification(_notifyHandle);
                _notifyHandle = IntPtr.Zero;
            }
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmDeviceChange)
        {
            int evt = unchecked((int)wParam.ToInt64());
            if (evt is DbtDeviceArrival or DbtDeviceRemoveComplete)
            {
                try { _onChanged(); } catch { }
            }
            return new IntPtr(1);
        }
        if (msg == WmClose)
        {
            DestroyWindow(hWnd);
            return IntPtr.Zero;
        }
        if (msg == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
            _ = PostMessageW(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevBroadcastDeviceInterface
    {
        public uint dbcc_size;
        public uint dbcc_devicetype;
        public uint dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassExW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
