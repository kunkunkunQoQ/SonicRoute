using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal sealed partial class LinkVideoEncoder
{
    internal byte[]? Encode(ReadOnlySpan<byte> bgra, int stride, long ptsUs, out bool key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int row = _width * 4;
        if (stride < row || bgra.Length < (long)stride * (_height - 1) + row)
            throw new ArgumentException("Invalid BGRA frame stride or length.", nameof(bgra));

        nint encoder = _encoder!.Handle;
        if (_lastKey < 0 || ptsUs - _lastKey >= 1_000_000)
        {
            SetCodec(KeyFrame, 19, 1);
            _lastKey = ptsUs;
        }
        using var sample = CreateInput(bgra, stride, ptsUs);
        var input = ComVtbl.Get<ProcessInput>(encoder, 24);
        int hr = input(encoder, 0, sample.Handle, 0);
        byte[]? data = null;
        if (hr == unchecked((int)0xc00d36b5))
        {
            data = ReadOutput();
            hr = input(encoder, 0, sample.Handle, 0);
        }
        Check(hr);
        data ??= ReadOutput();
        key = false;
        if (data == null) return null;
        ToAnnexB(data);
        ulong types = NalTypes(data);
        // HEVC BLA, IDR and CRA pictures are random-access points.
        key = _hevc ? (types & (0x3ful << 16)) != 0 : (types & (1ul << 5)) != 0;
        ulong parameters = _hevc ? 7ul << 32 : (1ul << 7) | (1ul << 8);
        if (key && (types & parameters) != parameters)
        {
            _header ??= ReadHeader();
            if ((NalTypes(_header) & parameters) != parameters)
                throw new InvalidDataException("The window encoder did not provide the required parameter sets.");
            byte[] joined = new byte[_header.Length + data.Length];
            _header.CopyTo(joined, 0);
            data.CopyTo(joined, _header.Length);
            return joined;
        }
        return data;
    }

    private unsafe ComPtr CreateInput(ReadOnlySpan<byte> bgra, int stride, long ptsUs)
    {
        int bytes = _width * _height * 3 / 2;
        Check(MFCreateMemoryBuffer((uint)bytes, out nint pointer));
        using var buffer = new ComPtr(pointer);
        Check(ComVtbl.Get<LockBuffer>(pointer, 3)(pointer, out nint memory, out _, out _));
        try { ConvertNv12(bgra, stride, new Span<byte>((void*)memory, bytes)); }
        finally { ComVtbl.Get<UnlockBuffer>(pointer, 4)(pointer); }
        Check(ComVtbl.Get<SetLength>(pointer, 6)(pointer, (uint)bytes));
        return Sample(pointer, ptsUs);
    }

    private ComPtr Sample(nint buffer, long ptsUs = -1)
    {
        Check(MFCreateSample(out nint pointer));
        var sample = new ComPtr(pointer);
        try
        {
            Check(ComVtbl.Get<AddBuffer>(pointer, 42)(pointer, buffer));
            if (ptsUs >= 0)
            {
                Check(ComVtbl.Get<SampleTime>(pointer, 36)(pointer, ptsUs * 10));
                Check(ComVtbl.Get<SampleTime>(pointer, 38)(pointer, 10_000_000 / _fps));
            }
            return sample;
        }
        catch { sample.Dispose(); throw; }
    }

    private byte[]? ReadOutput()
    {
        nint encoder = _encoder!.Handle;
        ComPtr? owner = null;
        if ((_output.Flags & 0x100) == 0)
        {
            Check(MFCreateMemoryBuffer(Math.Max(_output.Size, 64 * 1024), out nint pointer));
            using var buffer = new ComPtr(pointer);
            owner = Sample(buffer.Handle);
        }
        var output = new OutputData { Sample = owner?.Handle ?? 0 };
        try
        {
            int hr = ComVtbl.Get<ProcessOutput>(encoder, 25)(encoder, 0, 1, ref output, out _);
            if (hr == unchecked((int)0xc00d6d72)) return null;
            Check(hr);
            if (output.Sample == 0) return null;
            Check(ComVtbl.Get<GetPtr>(output.Sample, 41)(output.Sample, out nint pointer));
            using var buffer = new ComPtr(pointer);
            Check(ComVtbl.Get<LockBuffer>(pointer, 3)(pointer, out nint data, out _, out uint size));
            try
            {
                if (size == 0) return null;
                if (size > LinkConnection.MaxVideoFrame)
                    throw new IOException("The compressed Windows Link frame exceeds the maximum supported size.");
                byte[] bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { ComVtbl.Get<UnlockBuffer>(pointer, 4)(pointer); }
        }
        finally
        {
            if (output.Sample != owner?.Handle) ComVtbl.SafeRelease(ref output.Sample);
            ComVtbl.SafeRelease(ref output.Events);
            owner?.Dispose();
        }
    }

    private byte[] ReadHeader()
    {
        nint encoder = _encoder!.Handle;
        Check(ComVtbl.Get<GetCurrentType>(encoder, 18)(encoder, 0, out nint output));
        using var type = new ComPtr(output);
        Check(ComVtbl.Get<AllocatedBlob>(output, 16)(output, in SequenceHeader, out nint data, out uint length));
        try
        {
            if (length is < 4 or > 65536) throw new InvalidDataException("Invalid video sequence header.");
            byte[] header = new byte[length];
            Marshal.Copy(data, header, 0, header.Length);
            if (header[0] != 1) { ToAnnexB(header); return header; }
            if (_hevc) return DecodeHevcHeader(header);
            if (header.Length < 7) throw new InvalidDataException("Invalid AVC configuration.");
            using var result = new MemoryStream();
            int offset = 6;
            ReadParameterSets(header, ref offset, header[5] & 31, result);
            if (offset >= header.Length) throw new InvalidDataException("Missing AVC picture parameter sets.");
            ReadParameterSets(header, ref offset, header[offset++], result);
            return result.ToArray();
        }
        finally { Marshal.FreeCoTaskMem(data); }
    }

    private static byte[] DecodeHevcHeader(byte[] header)
    {
        if (header.Length < 23) throw new InvalidDataException("Invalid HEVC decoder configuration.");
        using var result = new MemoryStream();
        int offset = 23;
        for (int i = 0; i < header[22]; i++)
        {
            if (offset + 3 > header.Length) throw new InvalidDataException("Missing HEVC parameter array.");
            int count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(offset + 1));
            offset += 3;
            ReadParameterSets(header, ref offset, count, result);
        }
        return result.ToArray();
    }

    private static void ReadParameterSets(byte[] config, ref int offset, int count, Stream output)
    {
        for (int i = 0; i < count; i++)
        {
            if (offset + 2 > config.Length) throw new InvalidDataException("Invalid video parameter set.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(config.AsSpan(offset));
            offset += 2;
            if (length < 1 || offset + length > config.Length) throw new InvalidDataException("Invalid video parameter set.");
            output.Write([0, 0, 0, 1]);
            output.Write(config.AsSpan(offset, length));
            offset += length;
        }
    }

    private static void ToAnnexB(byte[] data)
    {
        if (Prefix(data, 0) != 0) return;
        int offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 4) throw new InvalidDataException("Invalid video NAL unit length.");
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            if (length <= 0 || length > data.Length - offset - 4)
                throw new InvalidDataException("Invalid video NAL unit length.");
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), 1);
            offset += 4 + length;
        }
    }

    private ulong NalTypes(byte[] data)
    {
        ulong types = 0;
        for (int i = 0; i < data.Length - 3; i++)
        {
            int skip = Prefix(data, i);
            if (skip == 0 || i + skip >= data.Length) continue;
            i += skip;
            types |= 1ul << (_hevc ? (data[i] >> 1) & 63 : data[i] & 31);
        }
        return types;
    }

    private static int Prefix(byte[] data, int offset)
    {
        if (offset + 3 > data.Length || data[offset] != 0 || data[offset + 1] != 0) return 0;
        if (data[offset + 2] == 1) return 3;
        return offset + 4 <= data.Length && data[offset + 2] == 0 && data[offset + 3] == 1 ? 4 : 0;
    }

    private void ConvertNv12(ReadOnlySpan<byte> bgra, int stride, Span<byte> nv12)
    {
        int chroma = _width * _height;
        for (int y = 0; y < _height; y += 2)
        {
            var top = bgra.Slice(y * stride, _width * 4);
            var bottom = bgra.Slice((y + 1) * stride, _width * 4);
            for (int x = 0; x < _width; x += 2)
            {
                int p = x * 4;
                nv12[y * _width + x] = Luma(top[p], top[p + 1], top[p + 2]);
                nv12[y * _width + x + 1] = Luma(top[p + 4], top[p + 5], top[p + 6]);
                nv12[(y + 1) * _width + x] = Luma(bottom[p], bottom[p + 1], bottom[p + 2]);
                nv12[(y + 1) * _width + x + 1] = Luma(bottom[p + 4], bottom[p + 5], bottom[p + 6]);
                int b = (top[p] + top[p + 4] + bottom[p] + bottom[p + 4]) / 4;
                int g = (top[p + 1] + top[p + 5] + bottom[p + 1] + bottom[p + 5]) / 4;
                int r = (top[p + 2] + top[p + 6] + bottom[p + 2] + bottom[p + 6]) / 4;
                int uv = chroma + (y / 2) * _width + x;
                nv12[uv] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                nv12[uv + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        }
    }

    private static byte Luma(int b, int g, int r) => (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
}
