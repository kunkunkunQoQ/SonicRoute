using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using static NAudio.MediaFoundation.MediaFoundationAttributes;

namespace SonicRoute.AirPlay;

internal readonly record struct AirPlayFormat(int Codec, int SampleRate, int Bits, int Frames)
{
    internal const int Pcm = 1, Alac = 2, Aac = 4;
    internal const long OutputFormats = (1L << 11) | (1L << 15) | (15L << 18);
    internal const long AacFormats = 3L << 22;

    internal static AirPlayFormat Parse(Dictionary<string, object?> stream, bool legacy = false)
    {
        long format = AirPlayPlist.Int(stream, "audioFormat");
        long ct = AirPlayPlist.Int(stream, "ct");
        var (codec, rate, bits, frames) = format switch
        {
            0 when legacy && ct == Pcm => (Pcm, 44100, 16, 352),
            0 when legacy && ct == Alac => (Alac, 44100, 16, 352),
            0 when legacy && ct == Aac => (Aac, 44100, 16, 1024),
            1L << 11 => (Pcm, 44100, 16, 352),
            1L << 15 => (Pcm, 48000, 16, 352),
            1L << 18 => (Alac, 44100, 16, 352),
            1L << 19 => (Alac, 44100, 24, 352),
            1L << 20 => (Alac, 48000, 16, 352),
            1L << 21 => (Alac, 48000, 24, 352),
            1L << 22 => (Aac, 44100, 16, 1024),
            1L << 23 => (Aac, 48000, 16, 1024),
            _ => throw new NotSupportedException("Unsupported AirPlay audio format.")
        };
        long spf = AirPlayPlist.Int(stream, "spf", frames);
        if ((ct != 0 && ct != codec) || spf is < 1 or > 4096) throw new NotSupportedException("Unsupported AirPlay frame format.");
        return new(codec, rate, bits, (int)spf);
    }
}

/// <summary>Decodes AirPlay packets with Windows Media Foundation and keeps one pair of reusable samples.</summary>
internal sealed class AirPlayDecoder : IDisposable
{
    private static readonly Guid AlacDecoder = new("C0CD7D12-31FC-4BBC-B363-7322EE3E1879");
    private static readonly Guid AacDecoder = new("32D186A7-218F-4C75-8876-DD77273A8999");
    private static readonly Guid AlacType = new("00006c61-0000-0010-8000-00aa00389b71");
    private static readonly Guid AacType = new("00001610-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatType = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmType = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid AacPayloadType = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    internal const int MaxPacket = 24 * 1024;
    private readonly AirPlayFormat _format;
    private readonly MFT_OUTPUT_DATA_BUFFER[] _result = new MFT_OUTPUT_DATA_BUFFER[1];
    private IMFTransform? _transform;
    private IMFSample? _input, _output;
    private IMFMediaBuffer? _inputBuffer, _outputBuffer;
    private bool _started;
    private int _outputBits;
    private long _time;

    internal AirPlayDecoder(AirPlayFormat format)
    {
        _format = format;
        if (format.Codec == AirPlayFormat.Pcm) return;
        try
        {
            Marshal.ThrowExceptionForHR(MFStartup(0x00020070, 1));
            _started = true;
            bool aac = format.Codec == AirPlayFormat.Aac;
            _transform = (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(aac ? AacDecoder : AlacDecoder, true)!)!;
            var inputType = MediaFoundationApi.CreateMediaType();
            try
            {
                inputType.SetGUID(MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Audio);
                inputType.SetGUID(MF_MT_SUBTYPE, aac ? AacType : AlacType);
                inputType.SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2);
                inputType.SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, format.SampleRate);
                inputType.SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, format.Bits);
                inputType.SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 1);
                if (aac) inputType.SetUINT32(AacPayloadType, 0);
                byte[] config = aac ? AacConfig(format.SampleRate) : AlacConfig(format);
                inputType.SetBlob(MF_MT_USER_DATA, config, config.Length);
                _transform.SetInputType(0, inputType, 0);
            }
            finally { Release(inputType); }
            ConfigureOutput();
            _input = MediaFoundationApi.CreateSample();
            _inputBuffer = MediaFoundationApi.CreateMemoryBuffer(MaxPacket);
            _input.AddBuffer(_inputBuffer);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        }
        catch { Dispose(); throw; }
    }

    private void ConfigureOutput()
    {
        for (int i = 0; i < 16; ++i)
        {
            IMFMediaType type;
            try { _transform!.GetOutputAvailableType(0, i, out type); }
            catch (COMException) { break; }
            try
            {
                type.GetGUID(MF_MT_SUBTYPE, out Guid subtype);
                type.GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, out int channels);
                type.GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, out int bits);
                type.GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, out int rate);
                if (channels != 2 || rate != _format.SampleRate || (subtype != FloatType && subtype != PcmType)
                    || bits is not (16 or 24 or 32)) continue;
                _transform.SetOutputType(0, type, 0);
                _outputBits = subtype == FloatType ? 0 : bits;
                _transform.GetOutputStreamInfo(0, out var info);
                Release(_output); _output = null;
                Release(_outputBuffer); _outputBuffer = null;
                if (((int)info.dwFlags & 0x100) == 0) // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
                {
                    _output = MediaFoundationApi.CreateSample();
                    _outputBuffer = MediaFoundationApi.CreateMemoryBuffer(Math.Max(info.cbSize, 4096 * 2 * sizeof(float)));
                    _output.AddBuffer(_outputBuffer);
                }
                return;
            }
            finally { Release(type); }
        }
        throw new NotSupportedException("Windows did not provide a supported AirPlay PCM format.");
    }

    internal unsafe int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        if (packet.Length is 0 or > MaxPacket) return 0;
        if (_transform == null) return DecodePcm(packet, pcm, 16, bigEndian: true);
        _inputBuffer!.Lock(out nint pointer, out int capacity, out _);
        try { packet.CopyTo(new Span<byte>((void*)pointer, capacity)); }
        finally { _inputBuffer.Unlock(); }
        _inputBuffer.SetCurrentLength(packet.Length);
        long duration = _format.Frames * 10_000_000L / _format.SampleRate;
        _input!.SetSampleTime(_time);
        _input.SetSampleDuration(duration);
        _time += duration;
        _transform.ProcessInput(0, _input, 0);
        int written = 0;
        // Drain all synchronous output from this packet before reusing the input sample.
        for (int i = 0; i < 8; ++i)
        {
            var supplied = _output;
            _result[0] = new MFT_OUTPUT_DATA_BUFFER { pSample = supplied! };
            _outputBuffer?.SetCurrentLength(0);
            int error = _transform.ProcessOutput(0, 1, _result, out _);
            var sample = _result[0].pSample;
            try
            {
                if (error == unchecked((int)0xC00D6D72)) return written; // MF_E_TRANSFORM_NEED_MORE_INPUT
                if (error == unchecked((int)0xC00D6D61))
                {
                    ConfigureOutput();
                    continue;
                }
                Marshal.ThrowExceptionForHR(error);
                if (sample == null) return written;
                sample.ConvertToContiguousBuffer(out var buffer);
                try
                {
                    buffer.Lock(out nint data, out _, out int length);
                    try { written += DecodePcm(new ReadOnlySpan<byte>((void*)data, length), pcm[written..], _outputBits, false); }
                    finally { buffer.Unlock(); }
                }
                finally { Release(buffer); }
            }
            finally
            {
                Release(_result[0].pEvents);
                if (sample != null && !ReferenceEquals(sample, supplied)) Release(sample);
                _result[0] = default;
            }
        }
        return written;
    }

    private static int DecodePcm(ReadOnlySpan<byte> input, Span<float> output, int bits, bool bigEndian)
    {
        int width = bits == 0 ? 4 : bits / 8;
        int samples = Math.Min(input.Length / width, output.Length) & ~1;
        if (bits == 0)
        {
            MemoryMarshal.Cast<byte, float>(input[..(samples * 4)]).CopyTo(output);
            return samples;
        }
        for (int i = 0; i < samples; ++i)
        {
            var bytes = input[(i * width)..];
            output[i] = (bits, bigEndian) switch
            {
                (16, true) => BinaryPrimitives.ReadInt16BigEndian(bytes) / 32768f,
                (16, false) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                (24, _) => (bytes[0] << 8 | bytes[1] << 16 | bytes[2] << 24) / 2147483648f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f
            };
        }
        return samples;
    }

    internal void Flush()
    {
        _transform?.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, 0);
        _time = 0;
    }

    private static byte[] AacConfig(int rate)
    {
        // HEAACWAVEINFO extension followed by the two-byte stereo AAC-LC AudioSpecificConfig.
        return [0, 0, 0xfe, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            rate == 44100 ? (byte)0x12 : (byte)0x11, rate == 44100 ? (byte)0x10 : (byte)0x90];
    }

    private static byte[] AlacConfig(AirPlayFormat format)
    {
        // The Windows decoder consumes ALACSpecificConfig directly, without the enclosing atom.
        byte[] bytes = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(bytes, format.Frames);
        bytes[5] = (byte)format.Bits;
        bytes[6] = 40; bytes[7] = 10; bytes[8] = 14; bytes[9] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 255);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), format.SampleRate);
        return bytes;
    }

    private static void Release(object? value) { if (value != null) Marshal.ReleaseComObject(value); }
    public void Dispose()
    {
        Release(_input); _input = null;
        Release(_output); _output = null;
        Release(_inputBuffer); _inputBuffer = null;
        Release(_outputBuffer); _outputBuffer = null;
        Release(_transform); _transform = null;
        if (_started) { _started = false; _ = MFShutdown(); }
    }

    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
}
