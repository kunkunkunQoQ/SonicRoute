using System.Buffers;
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
/// Zero-FFmpeg H.264 Annex-B low-latency video decoder built on Windows Media Foundation
/// (<c>CLSID_CMSH264DecoderMFT</c> with <c>MF_LOW_LATENCY = 1</c> / <c>CODECAPI_AVLowLatencyMode = 1</c>).
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

    private const uint MftOutputStreamProvidesSamples = 0x100;
    private const uint MftOutputStreamCanProvideSamples = 0x200;
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

    private readonly object _gate = new();
    private bool _mfStarted;
    private bool _coUninitNeeded;
    private bool _streamingStarted;
    private bool _disposed;

    private ComPtr _mft = new();
    private ComPtr _dxgiManager = new();
    private ComPtr _reusableSoftwareSample = new();
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

    public MediaFoundationH264Decoder(nint d3d11Device, int initialWidth = 0, int initialHeight = 0)
    {
        int coHr = Win32Native.CoInitializeEx(0, Win32Native.COINIT_MULTITHREADED);
        _coUninitNeeded = coHr is 0 or 1;
        if (coHr < 0 && coHr != Win32Native.RPC_E_CHANGED_MODE)
            ComVtbl.ThrowIfFailed(coHr, "CoInitializeEx");

        try
        {
            int mfHr = MFStartup(MfVersion, MfStartupNoSocket);
            ComVtbl.ThrowIfFailed(mfHr, "MFStartup");
            _mfStarted = true;

            Guid clsid = ClsidMsH264DecoderMft;
            Guid iidMft = IidImfTransform;
            int hr = Win32Native.CoCreateInstance(in clsid, 0, Win32Native.CLSCTX_INPROC_SERVER, in iidMft, out nint mftPtr);
            if (hr == Win32Native.REGDB_E_CLASSNOTREG || hr < 0 || mftPtr == 0)
            {
                throw AndroidError.WithCode(
                    new InvalidOperationException(
                        $"系统未安装 H.264 Media Foundation 解码器组件 (0x{hr:X8})。若使用 Windows N/KN 版本，请安装“媒体功能包 / Media Feature Pack”。"),
                    AndroidErrorCode.DisplayOpenFailed);
            }
            _mft = new ComPtr(mftPtr);

            ConfigureAttributesAndD3D(d3d11Device);
            ConfigureInputType(initialWidth, initialHeight);
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

    public bool IsHardwareAccelerated { get; private set; }
    public int Width => _cropWidth > 0 ? _cropWidth : _codedWidth;
    public int Height => _cropHeight > 0 ? _cropHeight : _codedHeight;

    private void ConfigureAttributesAndD3D(nint d3d11Device)
    {
        if (ComVtbl.Get<GetAttributesFn>(_mft.Handle, 8)(_mft.Handle, out nint attrPtr) < 0 || attrPtr == 0)
            return;
        using var attrs = new ComPtr(attrPtr);

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

    private void ConfigureInputType(int width, int height)
    {
        int hr = MFCreateMediaType(out nint mtPtr);
        ComVtbl.ThrowIfFailed(hr, "MFCreateMediaType(Input)");
        using var mt = new ComPtr(mtPtr);

        Guid majorKey = MfMtMajorType, majorVal = MfMediaTypeVideo;
        Guid subKey = MfMtSubtype, subVal = MfVideoFormatH264;
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
                throw new InvalidOperationException($"CMSH264DecoderMFT did not expose MFVideoFormat_NV12 output type (0x{hr:X8}).");

            using var outMt = new ComPtr(outMtPtr);
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
            using var cur = new ComPtr(curPtr);
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
            byte[] areaBytes = new byte[16];
            var pin = GCHandle.Alloc(areaBytes, GCHandleType.Pinned);
            try
            {
                if (ComVtbl.Get<AttrGetBlobFn>(cur.Handle, 15)(cur.Handle, in cropKey, pin.AddrOfPinnedObject(), 16, out uint cb) >= 0 && cb >= 16)
                {
                    short ox = BitConverter.ToInt16(areaBytes, 2);
                    short oy = BitConverter.ToInt16(areaBytes, 6);
                    int cw = BitConverter.ToInt32(areaBytes, 8);
                    int ch = BitConverter.ToInt32(areaBytes, 12);
                    if (cw > 0 && ch > 0)
                    {
                        _cropX = Math.Max(0, (int)ox);
                        _cropY = Math.Max(0, (int)oy);
                        _cropWidth = cw;
                        _cropHeight = ch;
                    }
                }
            }
            finally
            {
                pin.Free();
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
        DecodedSoftwareFrameHandler? onSoftwareFrame)
    {
        if (annexBData.IsEmpty) return 0;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            using var sample = CreateInputSample(annexBData, ptsUs);
            int hr = ComVtbl.Get<ProcessInputFn>(_mft.Handle, 24)(_mft.Handle, 0, sample.Handle, 0);
            int frames = 0;

            if (hr == MfENotAccepting)
            {
                frames += DrainOutputLocked(onHardwareFrame, onSoftwareFrame);
                hr = ComVtbl.Get<ProcessInputFn>(_mft.Handle, 24)(_mft.Handle, 0, sample.Handle, 0);
            }
            ComVtbl.ThrowIfFailed(hr, "IMFTransform.ProcessInput");

            frames += DrainOutputLocked(onHardwareFrame, onSoftwareFrame);
            return frames;
        }
    }

    public int Drain(DecodedHardwareFrameHandler? onHardwareFrame, DecodedSoftwareFrameHandler? onSoftwareFrame)
    {
        lock (_gate)
        {
            if (_disposed || _mft.IsNull) return 0;
            ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, MftMessageNotifyEndOfStream, 0);
            ComVtbl.Get<ProcessMessageFn>(_mft.Handle, 23)(_mft.Handle, 1 /* MFT_MESSAGE_COMMAND_DRAIN */, 0);
            return DrainOutputLocked(onHardwareFrame, onSoftwareFrame);
        }
    }

    private ComPtr CreateInputSample(ReadOnlySpan<byte> data, long ptsUs)
    {
        ComVtbl.ThrowIfFailed(MFCreateMemoryBuffer((uint)data.Length, out nint bufPtr), "MFCreateMemoryBuffer");
        using var buf = new ComPtr(bufPtr);

        ComVtbl.ThrowIfFailed(ComVtbl.Get<BufferLockFn>(buf.Handle, 3)(buf.Handle, out nint dstPtr, out _, out _), "IMFMediaBuffer.Lock");
        try
        {
            byte[] temp = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                data.CopyTo(temp);
                Marshal.Copy(temp, 0, dstPtr, data.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(temp);
            }
        }
        finally
        {
            ComVtbl.Get<BufferUnlockFn>(buf.Handle, 4)(buf.Handle);
        }

        ComVtbl.ThrowIfFailed(ComVtbl.Get<BufferSetLenFn>(buf.Handle, 6)(buf.Handle, (uint)data.Length), "SetCurrentLength");
        ComVtbl.ThrowIfFailed(MFCreateSample(out nint samplePtr), "MFCreateSample");
        var sample = new ComPtr(samplePtr);
        try
        {
            ComVtbl.ThrowIfFailed(ComVtbl.Get<SampleAddBufferFn>(sample.Handle, 42)(sample.Handle, buf.Handle), "AddBuffer");
            ComVtbl.Get<SampleSetTimeFn>(sample.Handle, 36)(sample.Handle, ptsUs * 10);
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private int DrainOutputLocked(
        DecodedHardwareFrameHandler? onHardwareFrame,
        DecodedSoftwareFrameHandler? onSoftwareFrame)
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
                if (bPtr != 0)
                {
                    ComVtbl.Get<BufferSetLenFn>(bPtr, 6)(bPtr, 0);
                    Marshal.Release(bPtr);
                }
            }

            var outputBuf = new MftOutputDataBuffer
            {
                dwStreamID = 0,
                pSample = providedSample,
                dwStatus = 0,
                pEvents = 0
            };

            int hr = ComVtbl.Get<ProcessOutputFn>(_mft.Handle, 25)(_mft.Handle, 0, 1, ref outputBuf, out uint status);
            if (outputBuf.pEvents != 0) Marshal.Release(outputBuf.pEvents);

            if (hr == MfETransformNeedMoreInput)
            {
                if (_mftProvidesOutputSamples && outputBuf.pSample != 0) Marshal.Release(outputBuf.pSample);
                break;
            }

            if (hr == MfETransformStreamChange || (outputBuf.dwStatus & 0x100) != 0 || (status & 0x100) != 0)
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
                try
                {
                    if (_codedWidth <= 0 || _codedHeight <= 0) RefreshOutputDimensions();
                    EmitSample(outSamplePtr, onHardwareFrame, onSoftwareFrame);
                    count++;
                }
                finally
                {
                    if (_mftProvidesOutputSamples) Marshal.Release(outSamplePtr);
                }
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
                using (new ComPtr(curBuf))
                {
                    if (ComVtbl.Get<BufferGetMaxLenFn>(curBuf, 7)(curBuf, out uint maxLen) >= 0 && maxLen >= needed)
                        return;
                }
            }
            _reusableSoftwareSample.Dispose();
        }

        ComVtbl.ThrowIfFailed(MFCreateSample(out nint sPtr), "MFCreateSample(Out)");
        var sample = new ComPtr(sPtr);
        ComVtbl.ThrowIfFailed(MFCreateMemoryBuffer(needed, out nint bPtr), "MFCreateMemoryBuffer(Out)");
        using (var buf = new ComPtr(bPtr))
            ComVtbl.ThrowIfFailed(ComVtbl.Get<SampleAddBufferFn>(sample.Handle, 42)(sample.Handle, buf.Handle), "AddBuffer(Out)");
        _reusableSoftwareSample = sample;
    }

    private void EmitSample(
        nint samplePtr,
        DecodedHardwareFrameHandler? onHardwareFrame,
        DecodedSoftwareFrameHandler? onSoftwareFrame)
    {
        if (ComVtbl.Get<SampleGetBufferByIndexFn>(samplePtr, 40)(samplePtr, 0, out nint bufPtr) < 0 || bufPtr == 0)
            return;
        using var mediaBuf = new ComPtr(bufPtr);

        if (onHardwareFrame != null && mediaBuf.TryQueryInterface(in IidImfDxgiBuffer, out var dxgiBuf) && dxgiBuf != null)
        {
            using (dxgiBuf)
            {
                Guid texIid = IidId3d11Texture2D;
                if (ComVtbl.Get<DxgiBufGetResourceFn>(dxgiBuf.Handle, 3)(dxgiBuf.Handle, in texIid, out nint texPtr) >= 0 && texPtr != 0)
                {
                    using var tex = new ComPtr(texPtr);
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
        }

        if (onSoftwareFrame == null) return;

        if (mediaBuf.TryQueryInterface(in IidImf2DBuffer, out var buf2d) && buf2d != null)
        {
            using (buf2d)
            {
                if (ComVtbl.Get<Lock2DFn>(buf2d.Handle, 3)(buf2d.Handle, out nint scan0, out int pitch) >= 0)
                {
                    try
                    {
                        int absPitch = Math.Abs(pitch);
                        int h = Math.Max(2, _codedHeight);
                        int uvH = (h + 1) / 2;
                        int total = absPitch * (h + uvH);
                        if (_swCopyBuffer.Length < total) _swCopyBuffer = new byte[total];
                        if (pitch > 0)
                        {
                            Marshal.Copy(scan0, _swCopyBuffer, 0, total);
                        }
                        else
                        {
                            for (int row = 0; row < h + uvH; row++)
                                Marshal.Copy(scan0 + row * pitch, _swCopyBuffer, row * absPitch, absPitch);
                        }
                        onSoftwareFrame(
                            _swCopyBuffer.AsSpan(0, total),
                            absPitch, h, _cropX, _cropY,
                            _cropWidth > 0 ? _cropWidth : _codedWidth,
                            _cropHeight > 0 ? _cropHeight : _codedHeight,
                            _colorMatrix);
                        return;
                    }
                    finally
                    {
                        ComVtbl.Get<Unlock2DFn>(buf2d.Handle, 4)(buf2d.Handle);
                    }
                }
            }
        }

        if (ComVtbl.Get<BufferLockFn>(mediaBuf.Handle, 3)(mediaBuf.Handle, out nint ptr, out _, out uint curLen) >= 0)
        {
            try
            {
                if (curLen > 0)
                {
                    if (_swCopyBuffer.Length < (int)curLen) _swCopyBuffer = new byte[(int)curLen];
                    Marshal.Copy(ptr, _swCopyBuffer, 0, (int)curLen);
                    int stride = _stride > 0 ? _stride : Math.Max(1, _codedWidth);
                    int codedH = Math.Max(2, _codedHeight > 0 ? _codedHeight : (int)curLen * 2 / (3 * stride));
                    onSoftwareFrame(
                        _swCopyBuffer.AsSpan(0, (int)curLen),
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
            if (_mfStarted) { try { MFShutdown(); } catch { } _mfStarted = false; }
            if (_coUninitNeeded) { try { Win32Native.CoUninitialize(); } catch { } _coUninitNeeded = false; }
        }
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint dwFlags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint ppMFType);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint ppIMFSample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint cbMaxLength, out nint ppBuffer);
    [DllImport("mfplat.dll")] private static extern int MFCreateDXGIDeviceManager(out uint resetToken, out nint ppDeviceManager);

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
