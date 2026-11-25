using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Audio;

namespace SonicRoute.AirPlay;

// IMFTransform, IMFAttributes, IMFSample and IMFMediaBuffer slots match the Windows SDK.
// Each Com holds one owned reference. A sample supplied by this decoder remains borrowed in ProcessOutput.
internal sealed unsafe class AirPlayAudioTransform : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputStreamInfo { internal uint Flags, Size, Alignment; }
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputData { internal uint Id; internal nint Sample; internal uint Status; internal nint Events; }

    private static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid Rate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid BlockAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid UserData = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
    private static readonly Guid PayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly AirPlayFormat _format;
    private Com _mft = null!;
    private Com _input = null!, _inputBuffer = null!;
    private Com? _output, _outputBuffer;
    private bool _started;
    private nint _mta;
    private int _outputBits;
    private long _time;

    internal AirPlayAudioTransform(AirPlayFormat format, byte[] config)
    {
        _format = format;
        try
        {
            Com.Check(Win32Native.CoIncrementMTAUsage(out _mta), "CoIncrementMTAUsage");
            Com.Check(MFStartup(0x00020070, 1), "MFStartup");
            _started = true;
            bool aac = format.Codec == AirPlayFormat.Aac;
            _mft = Com.Create(aac ? new("32D186A7-218F-4C75-8876-DD77273A8999") : new("C0CD7D12-31FC-4BBC-B363-7322EE3E1879"),
                new("bf94c121-5b05-4e6f-8000-ba598961414d"));
            Com.Check(MFCreateMediaType(out nint ptr), "MFCreateMediaType");
            using var type = new Com(ptr);
            SetGuid(type, Major, Audio);
            SetGuid(type, Subtype, aac ? new("00001610-0000-0010-8000-00aa00389b71") : new("00006c61-0000-0010-8000-00aa00389b71"));
            SetValue(type, Channels, 2);
            SetValue(type, Rate, format.SampleRate);
            SetValue(type, Bits, format.Bits);
            SetValue(type, BlockAlign, 1);
            if (aac) SetValue(type, PayloadType, 0);
            Guid key = UserData;
            fixed (byte* bytes = config)
                Com.Check(((delegate* unmanaged[Stdcall]<nint,Guid*,byte*,int,int>)type.Slot(26))(type.Handle,&key,bytes,config.Length), "SetBlob");
            SetMediaType(15, type);
            ConfigureOutput();
            (_input, _inputBuffer) = NewSample(AirPlayDecoder.MaxPacket);
            Message(0x10000000);
            Message(0x10000003);
        }
        catch { Dispose(); throw; }
    }

    private static void SetGuid(Com type, Guid key, Guid value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint,Guid*,Guid*,int>)type.Slot(24))(type.Handle,&key,&value), "SetGUID");

    private static void SetValue(Com type, Guid key, int value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint,Guid*,int,int>)type.Slot(21))(type.Handle,&key,value), "SetUINT32");

    private static int GetValue(Com type, Guid key)
    {
        int value = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,Guid*,int*,int>)type.Slot(7))(type.Handle,&key,&value), "GetUINT32");
        return value;
    }

    private void SetMediaType(int slot, Com type) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,nint,uint,int>)_mft.Slot(slot))(_mft.Handle,0,type.Handle,0), "IMFTransform.SetType");

    private void ConfigureOutput()
    {
        for (uint i = 0; i < 16; i++)
        {
            nint ptr = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,int>)_mft.Slot(14))(_mft.Handle,0,i,&ptr);
            if (hr < 0) break;
            using var type = new Com(ptr);
            Guid key = Subtype, subtype = default;
            Com.Check(((delegate* unmanaged[Stdcall]<nint,Guid*,Guid*,int>)type.Slot(10))(type.Handle,&key,&subtype), "GetGUID");
            int bits = GetValue(type, Bits);
            if (GetValue(type, Channels) != 2 || GetValue(type, Rate) != _format.SampleRate
                || (subtype != Float && subtype != Pcm) || bits is not (16 or 24 or 32)) continue;
            SetMediaType(16, type);
            _outputBits = subtype == Float ? 0 : bits;
            OutputStreamInfo info = default;
            Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,OutputStreamInfo*,int>)_mft.Slot(7))(_mft.Handle,0,&info), "GetOutputStreamInfo");
            _output?.Dispose();
            _outputBuffer?.Dispose();
            _output = _outputBuffer = null;
            if ((info.Flags & 0x100) == 0) (_output, _outputBuffer) = NewSample(Math.Max(checked((int)info.Size), 4096 * 8));
            return;
        }
        throw new NotSupportedException("Windows did not provide a supported AirPlay PCM format.");
    }

    private static (Com Sample, Com Buffer) NewSample(int size)
    {
        Com.Check(MFCreateSample(out nint samplePtr), "MFCreateSample");
        var sample = new Com(samplePtr);
        Com? buffer = null;
        try
        {
            Com.Check(MFCreateMemoryBuffer(size, out nint p), "MFCreateMemoryBuffer");
            buffer = new Com(p);
            Com.Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)sample.Slot(42))(sample.Handle,p), "AddBuffer");
            return (sample, buffer);
        }
        catch { buffer?.Dispose(); sample.Dispose(); throw; }
    }

    private static void SetLength(Com buffer, int length) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint,int,int>)buffer.Slot(6))(buffer.Handle,length), "SetCurrentLength");

    private void Message(uint msg) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,nint,int>)_mft.Slot(23))(_mft.Handle,msg,0), "IMFTransform.ProcessMessage");

    internal int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        nint data = 0;
        int capacity = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,nint*,int*,int*,int>)_inputBuffer.Slot(3))(_inputBuffer.Handle,&data,&capacity,null), "Lock input");
        try { packet.CopyTo(new Span<byte>((void*)data,capacity)); }
        finally { Com.Check(_inputBuffer.Call(4), "Unlock input"); }
        SetLength(_inputBuffer, packet.Length);
        long duration = _format.Frames * 10_000_000L / _format.SampleRate;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,long,int>)_input.Slot(36))(_input.Handle,_time), "SetSampleTime");
        Com.Check(((delegate* unmanaged[Stdcall]<nint,long,int>)_input.Slot(38))(_input.Handle,duration), "SetSampleDuration");
        _time += duration;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,nint,uint,int>)_mft.Slot(24))(_mft.Handle,0,_input.Handle,0), "ProcessInput");

        int written = 0;
        for (int i = 0; i < 8; i++)
        {
            nint supplied = _output?.Handle ?? 0;
            OutputData result = new() { Sample = supplied };
            if (_outputBuffer != null)
            {
                // Some audio MFTs append or replace a buffer on the caller-supplied sample.
                // Reattach our empty buffer so the next access unit cannot contain prior PCM.
                Com.Check(_output!.Call(44), "RemoveAllBuffers");
                SetLength(_outputBuffer, 0);
                Com.Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)_output.Slot(42))
                    (_output.Handle, _outputBuffer.Handle), "AddBuffer");
            }
            uint status = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint,uint,uint,OutputData*,uint*,int>)_mft.Slot(25))(_mft.Handle,0,1,&result,&status);
            try
            {
                if (hr == unchecked((int)0xc00d6d72)) return written;
                if (hr == unchecked((int)0xc00d6d61)) { ConfigureOutput(); continue; }
                Com.Check(hr, "ProcessOutput");
                if (result.Sample == 0) return written;
                nint p = 0;
                Com.Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)Com.Slot(result.Sample,41))(result.Sample,&p), "ConvertToContiguousBuffer");
                using var outputBuffer = new Com(p);
                int length = 0;
                Com.Check(((delegate* unmanaged[Stdcall]<nint,nint*,int*,int*,int>)outputBuffer.Slot(3))(p,&data,null,&length), "Lock output");
                try { written += AirPlayDecoder.DecodePcm(new ReadOnlySpan<byte>((void*)data,length), pcm[written..], _outputBits, false); }
                finally { Com.Check(outputBuffer.Call(4), "Unlock output"); }
            }
            finally
            {
                if (result.Events != 0) Com.Release(result.Events);
                if (result.Sample != 0 && result.Sample != supplied) Com.Release(result.Sample);
            }
        }
        return written;
    }

    internal void Flush() { Message(0); _time = 0; }
    public void Dispose()
    {
        _output?.Dispose(); _outputBuffer?.Dispose();
        _input?.Dispose(); _inputBuffer?.Dispose(); _mft?.Dispose();
        if (_started) { MFShutdown(); _started = false; }
        if (_mta != 0) { Win32Native.CoDecrementMTAUsage(_mta); _mta = 0; }
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint value);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint value);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(int size, out nint value);
}

