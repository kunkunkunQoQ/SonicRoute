using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal sealed partial class LinkH264Encoder : IDisposable
{
    private static readonly Guid Encoder = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly Guid Transform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid CodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid Aspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid KeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    private readonly int _width, _height, _fps;
    private ComPtr? _encoder, _settings;
    private nint _mta;
    private bool _mfStarted;
    private bool _disposed;
    private OutputInfo _output;
    private byte[]? _header;
    private long _lastKey = -1;

    internal LinkH264Encoder(int width, int height, int fps, int bitrate)
    {
        if (width < 16 || height < 16 || (width | height) % 2 != 0 || width > 1920 || height > 1080)
            throw new ArgumentOutOfRangeException(nameof(width), "H.264 requires even dimensions from 16 to 1920 × 1080.");
        _width = width;
        _height = height;
        _fps = fps;
        try
        {
            Check(Win32Native.CoIncrementMTAUsage(out _mta));
            Check(MFStartup(0x00020070, 1));
            _mfStarted = true;
            Check(Win32Native.CoCreateInstance(in Encoder, 0, Win32Native.CLSCTX_INPROC_SERVER, in Transform, out nint native));
            _encoder = new ComPtr(native);
            _encoder.TryQueryInterface(CodecApi, out _settings);
            SetCodec(LowLatency, 11, 0xffff);

            using var output = Format(H264);
            Check(ComVtbl.Get<SetUInt>(output.Handle, 21)(output.Handle, in Bitrate, (uint)bitrate));
            Check(ComVtbl.Get<SetUInt>(output.Handle, 21)(output.Handle, in Profile, 66));
            Check(ComVtbl.Get<SetType>(native, 16)(native, 0, output.Handle, 0));
            using var input = Format(Nv12);
            Check(ComVtbl.Get<SetType>(native, 15)(native, 0, input.Handle, 0));
            SetCodec(GopSize, 19, (ulong)fps);
            SetCodec(BFrames, 19, 0);
            Check(ComVtbl.Get<ReadOutputInfo>(native, 7)(native, 0, out _output));
            Check(ComVtbl.Get<ProcessMessage>(native, 23)(native, 0x10000000, 0));
            Check(ComVtbl.Get<ProcessMessage>(native, 23)(native, 0x10000003, 0));
        }
        catch { Dispose(); throw; }
    }

    private ComPtr Format(Guid subtype)
    {
        Check(MFCreateMediaType(out nint pointer));
        var type = new ComPtr(pointer);
        try
        {
            var guid = ComVtbl.Get<SetGuid>(pointer, 24);
            var number = ComVtbl.Get<SetULong>(pointer, 22);
            Check(guid(pointer, in MajorType, in Video));
            Check(guid(pointer, in Subtype, in subtype));
            Check(number(pointer, in FrameSize, ((ulong)_width << 32) | (uint)_height));
            Check(number(pointer, in FrameRate, ((ulong)_fps << 32) | 1));
            Check(number(pointer, in Aspect, (1UL << 32) | 1));
            Check(ComVtbl.Get<SetUInt>(pointer, 21)(pointer, in Interlace, 2));
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    private void SetCodec(Guid key, ushort type, ulong value)
    {
        if (_settings == null) return;
        var variant = new Variant { Type = type, Data = value };
        ComVtbl.Get<SetCodecValue>(_settings.Handle, 9)(_settings.Handle, in key, in variant);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_encoder is { } encoder)
        {
            ComVtbl.Get<ProcessMessage>(encoder.Handle, 23)(encoder.Handle, 0, 0);
            ComVtbl.Get<ProcessMessage>(encoder.Handle, 23)(encoder.Handle, 0x10000001, 0);
        }
        _settings?.Dispose();
        _encoder?.Dispose();
        if (_mfStarted) MFShutdown();
        if (_mta != 0) Win32Native.CoDecrementMTAUsage(_mta);
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [StructLayout(LayoutKind.Sequential)]
    private struct Variant { internal ushort Type, R0, R1, R2; internal ulong Data, Padding; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputInfo { internal uint Flags, Size, Alignment; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputData { internal uint Id; internal nint Sample; internal uint Status; internal nint Events; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuid(nint self, in Guid key, in Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt(nint self, in Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetULong(nint self, in Guid key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetType(nint self, uint stream, nint type, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetCodecValue(nint self, in Guid key, in Variant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessMessage(nint self, uint message, nint data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadOutputInfo(nint self, uint stream, out OutputInfo value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessInput(nint self, uint stream, nint sample, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ProcessOutput(nint self, uint flags, uint count, ref OutputData output, out uint status);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SampleTime(nint self, long time);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddBuffer(nint self, nint buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPtr(nint self, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCurrentType(nint self, uint stream, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AllocatedBlob(nint self, in Guid key, out nint value, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockBuffer(nint self, out nint data, out uint maxLength, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UnlockBuffer(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetLength(nint self, uint length);

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint bytes, out nint buffer);
}
