using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

public delegate void DecodedHardwareFrameHandler(
    nint texture2D,
    uint arraySlice,
    int codedWidth,
    int codedHeight,
    int cropX,
    int cropY,
    int cropWidth,
    int cropHeight,
    VideoColorMatrix colorMatrix);

public delegate void DecodedSoftwareFrameHandler(
    ReadOnlySpan<byte> nv12Buffer,
    int stride,
    int codedHeight,
    int cropX,
    int cropY,
    int cropWidth,
    int cropHeight,
    VideoColorMatrix colorMatrix);

/// <summary>
/// Low-latency Annex B decoder built on the Windows H.264 and optional HEVC Media Foundation transforms.
/// Outputs D3D11 hardware <c>DXGI_FORMAT_NV12</c> texture slices via <c>IMFDXGIDeviceManager</c> when bound to
/// a D3D11 device, or falls back transparently to CPU NV12 planes when running headless / software-only.
/// </summary>
public sealed class MediaFoundationH264Decoder : IDisposable
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupNoSocket = 0x1;

    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfENotAccepting = unchecked((int)0xC00D36B5);
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);

    internal const int MaxPooledInputSamples = 4;
    internal const int MaxPooledInputBufferBytes = 512 * 1024;
    internal const int MaxTotalPooledInputBytes = 2 * 1024 * 1024;

    private const uint MftOutputStreamProvidesSamples = 0x100;
    private const uint MftOutputStreamCanProvideSamples = 0x200;
    private const uint MftOutputDataBufferFormatChange = 0x100;
    private const uint MftOutputDataBufferNoSample = 0x300;
    private const uint MftMessageCommandFlush = 0x0;
    private const uint MftMessageSetD3DManager = 0x2;
    private const uint MftMessageNotifyBeginStreaming = 0x10000000;
    private const uint MftMessageNotifyEndOfStream = 0x10000002;
    private const uint MftMessageNotifyStartOfStream = 0x10000003;

    private static readonly Guid ClsidMsH264DecoderMft = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
    private static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IidImfDxgiBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
    private static readonly Guid IidImf2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
    private static readonly Guid IidId3d11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatHevc = new("43564548-0000-0010-8000-00aa00389b71");
    private static readonly Guid MftCategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtMinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid MfMtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MfMtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"); // same as CODECAPI_AVLowLatencyMode
    private static readonly Guid MfSaD3D11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    private static readonly Guid MfSampleExtensionDiscontinuity = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid MfSampleExtensionFrameCorruption = new("b4dd4a8c-0beb-44c4-8b75-b02b913b04f0");

    private static int _activeInstanceCount;

    private readonly object _gate = new();
    private bool _mfStarted;
    private nint _mtaCookie;
    private bool _counted;
    private bool _streamingStarted;
    private bool _disposed;

    private ComPtr _mft = new();
    private ComPtr _dxgiManager = new();
    private ComPtr _reusableSoftwareSample = new();
    private readonly PooledInputSample?[] _inputSamplePool = new PooledInputSample?[MaxPooledInputSamples];
    private int _inputPoolTotalBytes;
    private bool _mftProvidesOutputSamples;
    private uint _reusableOutputBufSize;

    private int _codedWidth;
    private int _codedHeight;
    private int _cropX;
    private int _cropY;
    private int _cropWidth;
    private int _cropHeight;
    private int _stride;
    private VideoColorMatrix _colorMatrix = VideoColorMatrix.Bt601Limited;
    private byte[] _swCopyBuffer = Array.Empty<byte>();

    internal bool SkipCorruptFrames { get; init; }
    public long CorruptFramesSkipped { get; private set; }
    internal Action? CorruptFrameSkipped;

    internal int InputSamplePoolTotalBytes { get { lock (_gate) return _inputPoolTotalBytes; } }
    internal int InputSamplePoolSlotCount
    {
        get
        {
            lock (_gate)
            {
                int count = 0;
                for (int i = 0; i < MaxPooledInputSamples; i++)
                {
                    if (_inputSamplePool[i] is not null) count++;
                }
                return count;
            }
        }
    }
    internal long InputSamplePoolHits { get; private set; }
    internal long InputSamplePoolBusySkips { get; private set; }
    internal long InputSampleTransientAllocations { get; private set; }

    internal bool TryGetInputSamplePoolSlotForTests(
        int index,
        out nint sample,
        out nint buffer,
        out int capacity,
        out bool hasDiscontinuityAttr)
    {
        lock (_gate)
        {
            if ((uint)index < MaxPooledInputSamples && _inputSamplePool[index] is { } slot)
            {
                sample = slot.Sample;
                buffer = slot.Buffer;
                capacity = slot.Capacity;
                hasDiscontinuityAttr = slot.HasDiscontinuityAttr;
                return true;
            }
        }
        sample = 0;
        buffer = 0;
        capacity = 0;
        hasDiscontinuityAttr = false;
        return false;
    }

    private sealed class PooledInputSample
    {
        public nint Sample;
        public nint Buffer;
        public int Capacity;
        public bool HasDiscontinuityAttr;
    }

    /// <summary>Zero-allocation stack-only COM pointer guard for hot-path temporary interfaces.</summary>
    private ref struct ComLease
    {
        private nint _ptr;

        public ComLease(nint ptr) => _ptr = ptr;

        public nint Handle => _ptr;
        public bool IsNull => _ptr == 0;

        public nint Detach()
        {
            nint ptr = _ptr;
            _ptr = 0;
            return ptr;
        }

        public bool TryQueryInterface(in Guid iid, out nint result)
        {
            result = 0;
            if (_ptr == 0) return false;
            if (ComVtbl.QueryInterface(_ptr, in iid, out nint next) >= 0 && next != 0)
            {
                result = next;
                return true;
            }
            return false;
        }

        public void Dispose() => ComVtbl.SafeRelease(ref _ptr);
    }

    public MediaFoundationH264Decoder(nint d3d11Device, int initialWidth = 0, int initialHeight = 0, string? decoder = null,
        uint codec = ScrcpyVideoStreamHeader.CodecH264)
    {
        bool hevc = codec == ScrcpyVideoStreamHeader.CodecH265;
        if (hevc ? decoder is not (null or AndroidStreamSettings.WindowsHevcDecoder)
            : codec != ScrcpyVideoStreamHeader.CodecH264 || decoder is not (null or AndroidStreamSettings.WindowsVideoDecoder or AndroidStreamSettings.SoftwareVideoDecoder))
            throw new ArgumentException("Unsupported video decoder or format.", nameof(decoder));
        Interlocked.Increment(ref _activeInstanceCount);
        _counted = true;

        try
        {
            int coHr = Win32Native.CoIncrementMTAUsage(out _mtaCookie);
            ComVtbl.ThrowIfFailed(coHr, "CoIncrementMTAUsage");

            int mfHr = MFStartup(MfVersion, MfStartupNoSocket);
            ComVtbl.ThrowIfFailed(mfHr, "MFStartup");
            _mfStarted = true;

            if (hevc) _mft = OpenHevcTransform();
            else
            {
                int hr = Win32Native.CoCreateInstance(in ClsidMsH264DecoderMft, 0, Win32Native.CLSCTX_INPROC_SERVER, in IidImfTransform, out nint mftPtr);
                if (hr < 0 || mftPtr == 0)
                    throw AndroidError.WithCode(new InvalidOperationException($"H.264 Media Foundation decoder unavailable (0x{hr:X8})."), AndroidErrorCode.DisplayOpenFailed);
                _mft = new ComPtr(mftPtr);
            }

            // No DXGI manager in software mode, even when the renderer owns a GPU device.
            // Automatic and explicit Windows acceleration retain the existing DXVA fallback.
            ConfigureAttributesAndD3D(decoder == AndroidStreamSettings.SoftwareVideoDecoder ? 0 : d3d11Device);
            ConfigureInputType(initialWidth, initialHeight, hevc ? MfVideoFormatHevc : MfVideoFormatH264);
            ConfigureOutputType();

            ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, MftMessageNotifyBeginStreaming, 0);
            ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, MftMessageNotifyStartOfStream, 0);
            _streamingStarted = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static readonly Lazy<bool> HevcAvailable = new(() =>
    {
        try
        {
            using var probe = new MediaFoundationH264Decoder(0, 640, 480, codec: ScrcpyVideoStreamHeader.CodecH265);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or DllNotFoundException)
        { return false; }
    });

    public static bool SupportsHevc => HevcAvailable.Value;

    private static ComPtr OpenHevcTransform()
    {
        // Ask Media Foundation for a synchronous transform. Windows can supply HEVC through
        // a media extension as well as a system codec. The decode loop uses synchronous MFT calls.
        var input = new MftType { Major = MfMediaTypeVideo, Subtype = MfVideoFormatHevc };
        const uint flags = 0x01 | 0x10 | 0x40; // SYNCMFT, LOCALMFT, SORTANDFILTER
        int hr = MFTEnumEx(MftCategoryVideoDecoder, flags, in input, 0, out nint list, out uint count);
        ComVtbl.ThrowIfFailed(hr, "MFTEnumEx(HEVC)");
        try
        {
            for (int i = 0; i < count; i++)
            {
                nint activate = Marshal.ReadIntPtr(list, i * nint.Size);
                if (ComVtbl.Get<ActivateObjectFn>(activate, 33)(activate, in IidImfTransform, out nint transform) >= 0 && transform != 0)
                    return new ComPtr(transform);
            }
        }
        finally
        {
            for (int i = 0; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(list, i * nint.Size));
            Marshal.FreeCoTaskMem(list);
        }
        throw new NotSupportedException("A compatible Windows HEVC Media Foundation decoder is unavailable.");
    }

    public static int ActiveInstanceCount => Volatile.Read(ref _activeInstanceCount);
    public bool IsHardwareAccelerated { get; private set; }
    public int Width => _cropWidth > 0 ? _cropWidth : _codedWidth;
    public int Height => _cropHeight > 0 ? _cropHeight : _codedHeight;

    private void ConfigureAttributesAndD3D(nint d3d11Device)
    {
        if (ComVtbl.Get<GetAttributesFn>(_mft.Handle, 8)(_mft.Handle, out nint attrPtr) < 0 || attrPtr == 0)
            return;
        using var attrs = new ComLease(attrPtr);

        Guid lowLatency = MfLowLatency;
        ComVtbl.Get<AttrSetUint32Fn>(attrs.Handle, 21)(attrs.Handle, in lowLatency, 1);

        if (d3d11Device == 0) return;

        Guid d3d11Aware = MfSaD3D11Aware;
        if (ComVtbl.Get<AttrGetUint32Fn>(attrs.Handle, 7)(attrs.Handle, in d3d11Aware, out uint aware) < 0 || aware == 0)
            return;

        if (MFCreateDXGIDeviceManager(out uint resetToken, out nint mgrPtr) >= 0 && mgrPtr != 0)
        {
            var mgr = new ComPtr(mgrPtr);
            int resetHr = ComVtbl.Get<DxgiMgrResetDeviceFn>(mgr.Handle, 7)(mgr.Handle, d3d11Device, resetToken);
            if (resetHr >= 0)
            {
                int msgHr = ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, MftMessageSetD3DManager, mgr.Handle);
                if (msgHr >= 0)
                {
                    _dxgiManager = mgr;
                    IsHardwareAccelerated = true;
                    return;
                }
            }
            mgr.Dispose();
        }
    }

    private void ConfigureInputType(int width, int height, Guid format)
    {
        int hr = MFCreateMediaType(out nint mtPtr);
        ComVtbl.ThrowIfFailed(hr, "MFCreateMediaType(Input)");
        using var mt = new ComLease(mtPtr);

        Guid majorKey = MfMtMajorType, majorVal = MfMediaTypeVideo;
        Guid subKey = MfMtSubtype, subVal = format;
        Guid interlaceKey = MfMtInterlaceMode;

        ComVtbl.ThrowIfFailed(ComVtbl.Get<AttrSetGuidFn>(mt.Handle, 24)(mt.Handle, in majorKey, in majorVal), "SetInputMajor");
        ComVtbl.ThrowIfFailed(ComVtbl.Get<AttrSetGuidFn>(mt.Handle, 24)(mt.Handle, in subKey, in subVal), "SetInputSubtype");
        ComVtbl.ThrowIfFailed(ComVtbl.Get<AttrSetUint32Fn>(mt.Handle, 21)(mt.Handle, in interlaceKey, 2 /* Progressive */), "SetInterlace");

        if (width > 0 && height > 0)
        {
            Guid frameSizeKey = MfMtFrameSize;
            ulong packed = ((ulong)(uint)width << 32) | (uint)height;
            ComVtbl.Get<AttrSetUint64Fn>(mt.Handle, 22)(mt.Handle, in frameSizeKey, packed);
        }

        hr = ComVtbl.Get<SetInputTypeFn>(_mft.Handle, 15)(_mft.Handle, 0, mt.Handle, 0);
        ComVtbl.ThrowIfFailed(hr, "IMFTransform.SetInputType");
    }

    private void ConfigureOutputType()
    {
        for (uint index = 0; ; index++)
        {
            int hr = ComVtbl.Get<GetOutputAvailableTypeFn>(_mft.Handle, 14)(_mft.Handle, 0, index, out nint outMtPtr);
            if (hr == MfENoMoreTypes || hr < 0)
                throw new InvalidOperationException($"The video decoder did not expose MFVideoFormat_NV12 output type (0x{hr:X8}).");

            using var outMt = new ComLease(outMtPtr);
            Guid subKey = MfMtSubtype;
            if (ComVtbl.Get<AttrGetGuidFn>(outMt.Handle, 10)(outMt.Handle, in subKey, out Guid subtype) >= 0
                && subtype == MfVideoFormatNv12)
            {
                int setHr = ComVtbl.Get<SetOutputTypeFn>(_mft.Handle, 16)(_mft.Handle, 0, outMt.Handle, 0);
                ComVtbl.ThrowIfFailed(setHr, "IMFTransform.SetOutputType");
                break;
            }
        }

        RefreshOutputDimensions();
    }

    private void RefreshOutputDimensions()
    {
        if (ComVtbl.Get<GetOutputCurrentTypeFn>(_mft.Handle, 18)(_mft.Handle, 0, out nint curPtr) >= 0 && curPtr != 0)
        {
            using var cur = new ComLease(curPtr);
            Guid sizeKey = MfMtFrameSize;
            if (ComVtbl.Get<AttrGetUint64Fn>(cur.Handle, 8)(cur.Handle, in sizeKey, out ulong packed) >= 0)
            {
                _codedWidth = (int)(packed >> 32);
                _codedHeight = (int)(packed & 0xFFFF_FFFF);
            }

            _cropX = 0;
            _cropY = 0;
            _cropWidth = _codedWidth;
            _cropHeight = _codedHeight;

            Guid cropKey = MfMtMinimumDisplayAperture;
            Span<byte> areaBytes = stackalloc byte[16];
            unsafe
            {
                fixed (byte* pArea = areaBytes)
                {
                    if (ComVtbl.Get<AttrGetBlobFn>(cur.Handle, 15)(cur.Handle, in cropKey, (nint)pArea, 16, out uint cb) >= 0 && cb >= 16)
                    {
                        short ox = BinaryPrimitives.ReadInt16LittleEndian(areaBytes.Slice(2, 2));
                        short oy = BinaryPrimitives.ReadInt16LittleEndian(areaBytes.Slice(6, 2));
                        int cw = BinaryPrimitives.ReadInt32LittleEndian(areaBytes.Slice(8, 4));
                        int ch = BinaryPrimitives.ReadInt32LittleEndian(areaBytes.Slice(12, 4));
                        if (cw > 0 && ch > 0)
                        {
                            _cropX = Math.Max(0, (int)ox);
                            _cropY = Math.Max(0, (int)oy);
                            _cropWidth = cw;
                            _cropHeight = ch;
                        }
                    }
                }
            }

            Guid strideKey = MfMtDefaultStride;
            if (ComVtbl.Get<AttrGetUint32Fn>(cur.Handle, 7)(cur.Handle, in strideKey, out uint rawStride) >= 0)
                _stride = Math.Abs((int)rawStride);
            else
                _stride = _codedWidth;

            Guid yuvKey = MfMtYuvMatrix;
            _colorMatrix = VideoColorMatrix.Bt601Limited;
            if (ComVtbl.Get<AttrGetUint32Fn>(cur.Handle, 7)(cur.Handle, in yuvKey, out uint matrix) >= 0 && matrix == 1)
                _colorMatrix = VideoColorMatrix.Bt709Limited;
        }

        if (ComVtbl.Get<GetOutputStreamInfoFn>(_mft.Handle, 7)(_mft.Handle, 0, out var info) >= 0)
        {
            _mftProvidesOutputSamples = (info.dwFlags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
            if (!_mftProvidesOutputSamples && info.cbSize > 0)
                _reusableOutputBufSize = info.cbSize;
        }
    }

    public int DecodePacket(
        ReadOnlySpan<byte> annexBData,
        long ptsUs,
        DecodedHardwareFrameHandler? onHardwareFrame,
        DecodedSoftwareFrameHandler? onSoftwareFrame,
        bool discontinuity = false) =>
        DecodePacket(ReadOnlySpan<byte>.Empty, annexBData, ptsUs, onHardwareFrame, onSoftwareFrame, discontinuity);

    public int DecodePacket(
        ReadOnlySpan<byte> prefixData,
        ReadOnlySpan<byte> annexBData,
        long ptsUs,
        DecodedHardwareFrameHandler? onHardwareFrame,
        DecodedSoftwareFrameHandler? onSoftwareFrame,
        bool discontinuity = false)
    {
        if (prefixData.IsEmpty && annexBData.IsEmpty) return 0;

        int corruptSkipped = 0;
        int frames = 0;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (discontinuity)
                {
                    var processMessage = ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23);
                    ComVtbl.ThrowIfFailed(processMessage(_mft.Handle, MftMessageCommandFlush, 0), "Flush video decoder");
                    ReleaseInputSamplePoolLocked();
                    ComVtbl.ThrowIfFailed(processMessage(_mft.Handle, MftMessageNotifyStartOfStream, 0), "Restart video decoder");
                }

                nint samplePtr = AcquireAndFillInputSampleLocked(
                    prefixData, annexBData, ptsUs, discontinuity, out PooledInputSample? pooledSlot);
                using var transientSample = new ComLease(pooledSlot is null ? samplePtr : 0);

                int hr = ComVtbl.Get<ProcessInputFn>(_mft.Handle, 24)(_mft.Handle, 0, samplePtr, 0);
                if (hr == MfENotAccepting)
                {
                    frames += DrainOutputLocked(onHardwareFrame, onSoftwareFrame, ref corruptSkipped);
                    hr = ComVtbl.Get<ProcessInputFn>(_mft.Handle, 24)(_mft.Handle, 0, samplePtr, 0);
                }
                ComVtbl.ThrowIfFailed(hr, "IMFTransform.ProcessInput");

                frames += DrainOutputLocked(onHardwareFrame, onSoftwareFrame, ref corruptSkipped);
            }
        }
        finally
        {
            NotifyCorruptFramesSkipped(corruptSkipped);
        }

        return frames;
    }

    public int Drain(DecodedHardwareFrameHandler? onHardwareFrame, DecodedSoftwareFrameHandler? onSoftwareFrame)
    {
        int corruptSkipped = 0;
        try
        {
            lock (_gate)
            {
                if (_disposed || _mft.IsNull) return 0;
                ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, MftMessageNotifyEndOfStream, 0);
                ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, 1 /* MFT_MESSAGE_COMMAND_DRAIN */, 0);
                return DrainOutputLocked(onHardwareFrame, onSoftwareFrame, ref corruptSkipped);
            }
        }
        finally
        {
            NotifyCorruptFramesSkipped(corruptSkipped);
        }
    }

    private void NotifyCorruptFramesSkipped(int corruptSkipped)
    {
        if (corruptSkipped <= 0) return;
        var handler = CorruptFrameSkipped;
        if (handler is null) return;
        for (int i = 0; i < corruptSkipped; i++)
            handler();
    }

    private static uint GetComRefCount(nint comPtr)
    {
        ComVtbl.Get<ComAddRefFn>(comPtr, 1)(comPtr);
        return ComVtbl.Get<ComReleaseFn>(comPtr, 2)(comPtr);
    }

    private static int ComputePooledBufferCapacity(int totalBytes, int availableBudget)
    {
        int limit = Math.Min(MaxPooledInputBufferBytes, availableBudget);
        uint rounded = BitOperations.RoundUpToPowerOf2((uint)Math.Max(totalBytes, 4096));
        int cap = rounded > (uint)limit ? limit : (int)rounded;
        return Math.Max(totalBytes, cap);
    }

    private static void CreateSampleAndBuffer(int capacity, out nint samplePtr, out nint bufferPtr)
    {
        ComVtbl.ThrowIfFailed(MFCreateMemoryBuffer((uint)capacity, out nint bPtr), "MFCreateMemoryBuffer");
        using var buf = new ComLease(bPtr);
        ComVtbl.ThrowIfFailed(MFCreateSample(out nint sPtr), "MFCreateSample");
        using var sample = new ComLease(sPtr);
        ComVtbl.ThrowIfFailed(ComVtbl.Get<SampleAddBufferFn>(sample.Handle, 42)(sample.Handle, buf.Handle), "AddBuffer");
        samplePtr = sample.Detach();
        bufferPtr = buf.Detach();
    }

    private static void PopulateInputSample(
        nint samplePtr,
        nint bufferPtr,
        ReadOnlySpan<byte> prefixData,
        ReadOnlySpan<byte> annexBData,
        int totalBytes,
        long ptsUs,
        bool discontinuity,
        PooledInputSample? slot)
    {
        ComVtbl.ThrowIfFailed(ComVtbl.Get<BufferLockFn>(bufferPtr, 3)(bufferPtr, out nint dstPtr, out uint maxLen, out _), "IMFMediaBuffer.Lock");
        try
        {
            if (maxLen < (uint)totalBytes)
                throw new InvalidOperationException($"Media Foundation input buffer capacity {maxLen} is smaller than {totalBytes} bytes.");
            unsafe
            {
                var dst = new Span<byte>((void*)dstPtr, totalBytes);
                if (!prefixData.IsEmpty)
                {
                    prefixData.CopyTo(dst);
                    annexBData.CopyTo(dst.Slice(prefixData.Length));
                }
                else
                {
                    annexBData.CopyTo(dst);
                }
            }
        }
        finally
        {
            ComVtbl.Get<BufferUnlockFn>(bufferPtr, 4)(bufferPtr);
        }

        ComVtbl.ThrowIfFailed(ComVtbl.Get<BufferSetLenFn>(bufferPtr, 6)(bufferPtr, (uint)totalBytes), "SetCurrentLength");
        ComVtbl.Get<SampleSetTimeFn>(samplePtr, 36)(samplePtr, ptsUs * 10);

        if (discontinuity)
        {
            ComVtbl.ThrowIfFailed(
                ComVtbl.Get<AttrSetUint32Fn>(samplePtr, 21)(samplePtr, in MfSampleExtensionDiscontinuity, 1),
                "Mark video discontinuity");
            if (slot is not null) slot.HasDiscontinuityAttr = true;
        }
        else if (slot is { HasDiscontinuityAttr: true })
        {
            ComVtbl.ThrowIfFailed(
                ComVtbl.Get<AttrDeleteItemFn>(samplePtr, 19)(samplePtr, in MfSampleExtensionDiscontinuity),
                "Clear video discontinuity");
            slot.HasDiscontinuityAttr = false;
        }
    }

    private nint AcquireAndFillInputSampleLocked(
        ReadOnlySpan<byte> prefixData,
        ReadOnlySpan<byte> annexBData,
        long ptsUs,
        bool discontinuity,
        out PooledInputSample? pooledSlot)
    {
        int totalBytes = checked(prefixData.Length + annexBData.Length);
        pooledSlot = null;

        if (totalBytes <= MaxPooledInputBufferBytes)
        {
            PooledInputSample? reusable = null;
            int emptyIndex = -1;

            for (int i = 0; i < MaxPooledInputSamples; i++)
            {
                var slot = _inputSamplePool[i];
                if (slot is null)
                {
                    if (emptyIndex < 0) emptyIndex = i;
                    continue;
                }

                // Check IMFSample refcount (1) BEFORE IMFMediaBuffer refcount (2): once Sample refcount is 1
                // under _gate, MFT no longer holds Sample and cannot acquire a fresh Buffer reference via it.
                if (GetComRefCount(slot.Sample) != 1 || GetComRefCount(slot.Buffer) != 2)
                {
                    InputSamplePoolBusySkips++;
                    continue;
                }

                if (slot.Capacity >= totalBytes)
                {
                    reusable = slot;
                    break;
                }

                reusable ??= slot;
            }

            if (reusable is not null && reusable.Capacity >= totalBytes)
            {
                InputSamplePoolHits++;
                pooledSlot = reusable;
                PopulateInputSample(reusable.Sample, reusable.Buffer, prefixData, annexBData, totalBytes, ptsUs, discontinuity, reusable);
                return reusable.Sample;
            }

            if (reusable is not null)
            {
                int otherBytes = _inputPoolTotalBytes - reusable.Capacity;
                int availableBudget = MaxTotalPooledInputBytes - otherBytes;
                if (totalBytes <= availableBudget)
                {
                    int targetCap = ComputePooledBufferCapacity(totalBytes, availableBudget);
                    CreateSampleAndBuffer(targetCap, out nint newSample, out nint newBuffer);
                    _inputPoolTotalBytes = otherBytes + targetCap;
                    ComVtbl.SafeRelease(ref reusable.Sample);
                    ComVtbl.SafeRelease(ref reusable.Buffer);
                    reusable.Sample = newSample;
                    reusable.Buffer = newBuffer;
                    reusable.Capacity = targetCap;
                    reusable.HasDiscontinuityAttr = false;

                    pooledSlot = reusable;
                    PopulateInputSample(newSample, newBuffer, prefixData, annexBData, totalBytes, ptsUs, discontinuity, reusable);
                    return newSample;
                }
            }
            else if (emptyIndex >= 0)
            {
                int availableBudget = MaxTotalPooledInputBytes - _inputPoolTotalBytes;
                if (totalBytes <= availableBudget)
                {
                    int targetCap = ComputePooledBufferCapacity(totalBytes, availableBudget);
                    CreateSampleAndBuffer(targetCap, out nint newSample, out nint newBuffer);
                    var newSlot = new PooledInputSample
                    {
                        Sample = newSample,
                        Buffer = newBuffer,
                        Capacity = targetCap,
                        HasDiscontinuityAttr = false
                    };
                    _inputSamplePool[emptyIndex] = newSlot;
                    _inputPoolTotalBytes += targetCap;

                    pooledSlot = newSlot;
                    PopulateInputSample(newSample, newBuffer, prefixData, annexBData, totalBytes, ptsUs, discontinuity, newSlot);
                    return newSample;
                }
            }
        }

        InputSampleTransientAllocations++;
        CreateSampleAndBuffer(totalBytes, out nint transientSamplePtr, out nint transientBufferPtr);
        using var sampleOwner = new ComLease(transientSamplePtr);
        using var bufferOwner = new ComLease(transientBufferPtr);
        PopulateInputSample(sampleOwner.Handle, bufferOwner.Handle, prefixData, annexBData, totalBytes, ptsUs, discontinuity, slot: null);
        // bufferOwner releases our extra buffer reference; sampleOwner retains the sole ref until freed after ProcessInput.
        return sampleOwner.Detach();
    }

    private void ReleaseInputSamplePoolLocked()
    {
        for (int i = 0; i < MaxPooledInputSamples; i++)
        {
            var slot = _inputSamplePool[i];
            if (slot is null) continue;
            _inputSamplePool[i] = null;
            ComVtbl.SafeRelease(ref slot.Sample);
            ComVtbl.SafeRelease(ref slot.Buffer);
        }
        _inputPoolTotalBytes = 0;
    }

    private int DrainOutputLocked(
        DecodedHardwareFrameHandler? onHardwareFrame,
        DecodedSoftwareFrameHandler? onSoftwareFrame,
        ref int corruptSkipped)
    {
        int count = 0;
        while (true)
        {
            nint providedSample = 0;
            if (!_mftProvidesOutputSamples)
            {
                EnsureSoftwareOutputSample();
                providedSample = _reusableSoftwareSample.Handle;
                ComVtbl.Get<SampleGetBufferByIndexFn>(providedSample, 40)(providedSample, 0, out nint bPtr);
                using var b = new ComLease(bPtr);
                if (!b.IsNull)
                    ComVtbl.Get<BufferSetLenFn>(b.Handle, 6)(b.Handle, 0);
            }

            var outputBuf = new MftOutputDataBuffer
            {
                dwStreamID = 0,
                pSample = providedSample,
                dwStatus = 0,
                pEvents = 0
            };

            int hr = ComVtbl.Get<ProcessOutputFn>(_mft.Handle, 25)(_mft.Handle, 0, 1, ref outputBuf, out _);
            if (outputBuf.pEvents != 0) Marshal.Release(outputBuf.pEvents);

            if (hr == MfETransformNeedMoreInput || (hr >= 0 && outputBuf.dwStatus == MftOutputDataBufferNoSample))
            {
                if (_mftProvidesOutputSamples && outputBuf.pSample != 0) Marshal.Release(outputBuf.pSample);
                break;
            }

            if (hr == MfETransformStreamChange || outputBuf.dwStatus == MftOutputDataBufferFormatChange)
            {
                if (_mftProvidesOutputSamples && outputBuf.pSample != 0) Marshal.Release(outputBuf.pSample);
                ConfigureOutputType();
                continue;
            }

            if (hr < 0)
            {
                if (_mftProvidesOutputSamples && outputBuf.pSample != 0) Marshal.Release(outputBuf.pSample);
                ComVtbl.ThrowIfFailed(hr, "IMFTransform.ProcessOutput");
            }

            nint outSamplePtr = outputBuf.pSample;
            if (outSamplePtr != 0)
            {
                using var ownedOutSample = new ComLease(_mftProvidesOutputSamples ? outSamplePtr : 0);
                if (SkipCorruptFrames
                    && ComVtbl.Get<AttrGetUint32Fn>(outSamplePtr, 7)(outSamplePtr, in MfSampleExtensionFrameCorruption, out uint corrupt) >= 0
                    && corrupt == 1)
                {
                    CorruptFramesSkipped++;
                    corruptSkipped++;
                    // Caller-supplied software samples can be reused for the next picture.
                    if (outSamplePtr == providedSample)
                        ComVtbl.Get<AttrDeleteItemFn>(outSamplePtr, 19)(outSamplePtr, in MfSampleExtensionFrameCorruption);
                    continue;
                }

                if (_codedWidth <= 0 || _codedHeight <= 0) RefreshOutputDimensions();
                EmitSample(outSamplePtr, onHardwareFrame, onSoftwareFrame);
                count++;
            }
        }
        return count;
    }

    private void EnsureSoftwareOutputSample()
    {
        uint needed = Math.Max(_reusableOutputBufSize, (uint)Math.Max(1, _codedWidth * _codedHeight * 3 / 2));
        if (!_reusableSoftwareSample.IsNull)
        {
            if (ComVtbl.Get<SampleGetBufferByIndexFn>(_reusableSoftwareSample.Handle, 40)(
                    _reusableSoftwareSample.Handle, 0, out nint curBuf) >= 0 && curBuf != 0)
            {
                using (new ComLease(curBuf))
                {
                    if (ComVtbl.Get<BufferGetMaxLenFn>(curBuf, 7)(curBuf, out uint maxLen) >= 0 && maxLen >= needed)
                        return;
                }
            }
            _reusableSoftwareSample.Dispose();
        }

        ComVtbl.ThrowIfFailed(MFCreateSample(out nint sPtr), "MFCreateSample(Out)");
        var sample = new ComPtr(sPtr);
        try
        {
            ComVtbl.ThrowIfFailed(MFCreateMemoryBuffer(needed, out nint bPtr), "MFCreateMemoryBuffer(Out)");
            using (var buf = new ComLease(bPtr))
                ComVtbl.ThrowIfFailed(ComVtbl.Get<SampleAddBufferFn>(sample.Handle, 42)(sample.Handle, buf.Handle), "AddBuffer(Out)");
            _reusableSoftwareSample = sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private void EmitSample(
        nint samplePtr,
        DecodedHardwareFrameHandler? onHardwareFrame,
        DecodedSoftwareFrameHandler? onSoftwareFrame)
    {
        if (ComVtbl.Get<SampleGetBufferByIndexFn>(samplePtr, 40)(samplePtr, 0, out nint bufPtr) < 0 || bufPtr == 0)
            return;
        using var mediaBuf = new ComLease(bufPtr);

        if (onHardwareFrame != null && mediaBuf.TryQueryInterface(in IidImfDxgiBuffer, out nint dxgiBufPtr))
        {
            using var dxgiBuf = new ComLease(dxgiBufPtr);
            Guid texIid = IidId3d11Texture2D;
            if (ComVtbl.Get<DxgiBufGetResourceFn>(dxgiBuf.Handle, 3)(dxgiBuf.Handle, in texIid, out nint texPtr) >= 0 && texPtr != 0)
            {
                using var tex = new ComLease(texPtr);
                ComVtbl.Get<DxgiBufGetSubresourceFn>(dxgiBuf.Handle, 4)(dxgiBuf.Handle, out uint subresource);
                ComVtbl.Get<D3D11TexGetDescFn>(tex.Handle, 10)(tex.Handle, out var desc);
                int codedW = (int)desc.Width;
                int codedH = (int)desc.Height;
                int cropW = _cropWidth > 0 ? Math.Min(_cropWidth, codedW) : codedW;
                int cropH = _cropHeight > 0 ? Math.Min(_cropHeight, codedH) : codedH;
                onHardwareFrame(tex.Handle, subresource, codedW, codedH, _cropX, _cropY, cropW, cropH, _colorMatrix);
                return;
            }
        }

        if (onSoftwareFrame == null) return;

        if (mediaBuf.TryQueryInterface(in IidImf2DBuffer, out nint buf2dPtr))
        {
            using var buf2d = new ComLease(buf2dPtr);
            if (ComVtbl.Get<Lock2DFn>(buf2d.Handle, 3)(buf2d.Handle, out nint scan0, out int pitch) >= 0)
            {
                try
                {
                    int absPitch = Math.Abs(pitch);
                    int h = Math.Max(2, _codedHeight);
                    int uvH = (h + 1) / 2;
                    int total = absPitch * (h + uvH);
                    if (scan0 != 0 && absPitch > 0 && total > 0)
                    {
                        if (pitch > 0)
                        {
                            ReadOnlySpan<byte> directSpan;
                            unsafe
                            {
                                directSpan = new ReadOnlySpan<byte>((void*)scan0, total);
                            }
                            onSoftwareFrame(
                                directSpan,
                                absPitch, h, _cropX, _cropY,
                                _cropWidth > 0 ? _cropWidth : _codedWidth,
                                _cropHeight > 0 ? _cropHeight : _codedHeight,
                                _colorMatrix);
                        }
                        else
                        {
                            if (_swCopyBuffer.Length < total) _swCopyBuffer = new byte[total];
                            for (int row = 0; row < h + uvH; row++)
                                Marshal.Copy(scan0 + row * pitch, _swCopyBuffer, row * absPitch, absPitch);
                            onSoftwareFrame(
                                _swCopyBuffer.AsSpan(0, total),
                                absPitch, h, _cropX, _cropY,
                                _cropWidth > 0 ? _cropWidth : _codedWidth,
                                _cropHeight > 0 ? _cropHeight : _codedHeight,
                                _colorMatrix);
                        }
                    }
                    return;
                }
                finally
                {
                    ComVtbl.Get<Unlock2DFn>(buf2d.Handle, 4)(buf2d.Handle);
                }
            }
        }

        if (ComVtbl.Get<BufferLockFn>(mediaBuf.Handle, 3)(mediaBuf.Handle, out nint ptr, out _, out uint curLen) >= 0)
        {
            try
            {
                if (ptr != 0 && curLen > 0)
                {
                    int len = checked((int)curLen);
                    int stride = _stride > 0 ? _stride : Math.Max(1, _codedWidth);
                    int codedH = Math.Max(2, _codedHeight > 0 ? _codedHeight : len * 2 / (3 * stride));
                    ReadOnlySpan<byte> directSpan;
                    unsafe
                    {
                        directSpan = new ReadOnlySpan<byte>((void*)ptr, len);
                    }
                    onSoftwareFrame(
                        directSpan,
                        stride, codedH, _cropX, _cropY,
                        _cropWidth > 0 ? _cropWidth : _codedWidth,
                        _cropHeight > 0 ? _cropHeight : codedH,
                        _colorMatrix);
                }
            }
            finally
            {
                ComVtbl.Get<BufferUnlockFn>(mediaBuf.Handle, 4)(mediaBuf.Handle);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_mft.IsNull && _streamingStarted)
            {
                try
                {
                    ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, MftMessageCommandFlush, 0);
                    ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, MftMessageNotifyEndOfStream, 0);
                }
                catch { }
            }
            _reusableSoftwareSample.Dispose();
            _dxgiManager.Dispose();
            _mft.Dispose();
            ReleaseInputSamplePoolLocked();
            _swCopyBuffer = Array.Empty<byte>();
            if (_mfStarted) { try { MFShutdown(); } catch { } _mfStarted = false; }

            int remaining = _counted ? Interlocked.Decrement(ref _activeInstanceCount) : Volatile.Read(ref _activeInstanceCount);
            _counted = false;
            if (remaining == 0)
            {
                try { Win32Native.CoFreeUnusedLibrariesEx(0, 0); } catch { }
            }

            nint cookie = Interlocked.Exchange(ref _mtaCookie, 0);
            if (cookie != 0)
            {
                try { Win32Native.CoDecrementMTAUsage(cookie); } catch { }
            }
        }
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint dwFlags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint ppMFType);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint ppIMFSample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out nint ppBuffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateDXGIDeviceManager(out uint resetToken, out nint ppDeviceManager);
    [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags, in MftType input, nint output, out nint activate, out uint count);

    [StructLayout(LayoutKind.Sequential)]
    private struct MftType { public Guid Major, Subtype; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputStreamInfo
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint dwStreamID;
        public nint pSample;
        public uint dwStatus;
        public nint pEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11TexDescMinimal
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ComAddRefFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ComReleaseFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ActivateObjectFn(nint self, in Guid iid, out nint ptr);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputStreamInfoFn(nint self, uint streamId, out MftOutputStreamInfo info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAttributesFn(nint self, out nint ppAttributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputAvailableTypeFn(nint self, uint streamId, uint typeIndex, out nint ppType);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInputTypeFn(nint self, uint streamId, nint pType, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetOutputTypeFn(nint self, uint streamId, nint pType, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputCurrentTypeFn(nint self, uint streamId, out nint ppType);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessMessageFn(nint self, uint message, nint param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessInputFn(nint self, uint streamId, nint pSample, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessOutputFn(nint self, uint flags, uint outputBufferCount, ref MftOutputDataBuffer outputSamples, out uint status);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttrGetUint32Fn(nint self, in Guid key, out uint val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttrGetUint64Fn(nint self, in Guid key, out ulong val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttrGetGuidFn(nint self, in Guid key, out Guid val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttrGetBlobFn(nint self, in Guid key, nint buf, uint cbSize, out uint cbLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttrDeleteItemFn(nint self, in Guid key);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttrSetUint32Fn(nint self, in Guid key, uint val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttrSetUint64Fn(nint self, in Guid key, ulong val);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AttrSetGuidFn(nint self, in Guid key, in Guid val);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DxgiMgrResetDeviceFn(nint self, nint pDevice, uint resetToken);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DxgiBufGetResourceFn(nint self, in Guid riid, out nint ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DxgiBufGetSubresourceFn(nint self, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int D3D11TexGetDescFn(nint self, out D3D11TexDescMinimal desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SampleSetTimeFn(nint self, long hnsSampleTime);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SampleGetBufferByIndexFn(nint self, uint index, out nint ppBuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SampleAddBufferFn(nint self, nint pBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BufferLockFn(nint self, out nint ppBuffer, out uint maxLen, out uint curLen);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BufferUnlockFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BufferSetLenFn(nint self, uint curLen);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BufferGetMaxLenFn(nint self, out uint maxLen);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Lock2DFn(nint self, out nint ppScanline0, out int pPitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Unlock2DFn(nint self);
}
