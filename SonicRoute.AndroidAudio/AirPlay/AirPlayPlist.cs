using System.Buffers.Binary;
using System.Text;

namespace SonicRoute.AirPlay;

// The binary plist subset used by AirPlay's RTSP messages. Reject malformed offsets,
// cycles and deeply nested or excessively large graphs before allocating their contents.
internal static class AirPlayPlist
{
    internal static Dictionary<string, object?> Read(byte[] payload) =>
        new Reader(payload).ReadRoot() as Dictionary<string, object?>
        ?? throw new InvalidDataException("Expected an AirPlay property list dictionary.");

    internal static long Int(Dictionary<string, object?> data, string name, long fallback = 0) =>
        data.GetValueOrDefault(name) is long value ? value : fallback;

    private sealed class Reader
    {
        private readonly byte[] _bytes;
        private readonly int[] _offsets;
        private readonly int _table, _root, _refSize;
        private int _remaining = 4096;
        private int _byteBudget;

        internal Reader(byte[] bytes)
        {
            _bytes = bytes;
            _byteBudget = bytes.Length * 3;
            if (bytes.Length < 40 || !bytes.AsSpan(0, 8).SequenceEqual("bplist00"u8)) throw Invalid();
            var trailer = bytes.AsSpan(bytes.Length - 32);
            int offsetSize = trailer[6];
            _refSize = trailer[7];
            int count = Bound(ReadUInt(trailer.Slice(8, 8)), 2048);
            _root = Bound(ReadUInt(trailer.Slice(16, 8)), count - 1);
            _table = Bound(ReadUInt(trailer.Slice(24, 8)), bytes.Length - 32);
            if (offsetSize is < 1 or > 8 || _refSize is < 1 or > 8 || _table < 8
                || count * offsetSize > bytes.Length - 32 - _table) throw Invalid();
            _offsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                int offset = Bound(ReadUInt(bytes.AsSpan(_table + i * offsetSize, offsetSize)), _table - 1);
                if (offset < 8) throw Invalid();
                _offsets[i] = offset;
            }
        }

        internal object? ReadRoot() => ReadObject(_root, 0);

        private object? ReadObject(int id, int depth)
        {
            if (depth > 12 || --_remaining < 0) throw Invalid();
            int offset = _offsets[id];
            byte marker = Segment(ref offset, 1)[0];
            int kind = marker >> 4, width = marker & 15;
            if (marker == 0) return null;
            if (marker is 8 or 9) return marker == 9;
            if (kind == 1 && width <= 3) return unchecked((long)ReadUInt(Segment(ref offset, 1 << width)));
            if (kind == 1 && width == 4)
            {
                var extended = Segment(ref offset, 16);
                ulong high = ReadUInt(extended[..8]);
                if (high is not (0 or ulong.MaxValue)) throw Invalid();
                return unchecked((long)ReadUInt(extended[8..]));
            }
            if (kind == 2 && width == 2)
                return (double)BitConverter.Int32BitsToSingle((int)ReadUInt(Segment(ref offset, 4)));
            if (kind == 2 && width == 3 || marker == 0x33)
                return BitConverter.Int64BitsToDouble(unchecked((long)ReadUInt(Segment(ref offset, 8))));
            if (kind is not (4 or 5 or 6 or 7 or 10 or 13)) throw Invalid();
            int size = width < 15 ? width : ReadSize(ref offset);
            if (kind == 10)
            {
                if (size > _remaining || size > (_table - offset) / _refSize) throw Invalid();
                var values = new object?[size];
                for (int i = 0; i < size; ++i) values[i] = ReadObject(Reference(ref offset), depth + 1);
                return values;
            }
            if (kind == 13)
            {
                if (size > _remaining / 2 || size > (_table - offset) / (2 * _refSize)) throw Invalid();
                var result = new Dictionary<string, object?>(size, StringComparer.Ordinal);
                int valueOffset = offset + size * _refSize;
                for (int i = 0; i < size; ++i)
                {
                    if (ReadObject(Reference(ref offset), depth + 1) is not string key
                        || !result.TryAdd(key, ReadObject(Reference(ref valueOffset), depth + 1))) throw Invalid();
                }
                return result;
            }
            int byteCount = kind == 6 ? checked(size * 2) : size;
            if ((_byteBudget -= byteCount) < 0) throw Invalid();
            var data = Segment(ref offset, byteCount);
            return kind switch
            {
                4 => data.ToArray(),
                5 => Encoding.ASCII.GetString(data),
                6 => Encoding.BigEndianUnicode.GetString(data),
                _ => Encoding.UTF8.GetString(data)
            };
        }

        private int Reference(ref int offset) => Bound(ReadUInt(Segment(ref offset, _refSize)), _offsets.Length - 1);
        private int ReadSize(ref int offset)
        {
            byte tag = Segment(ref offset, 1)[0];
            if (tag is < 0x10 or > 0x13) throw Invalid();
            return Bound(ReadUInt(Segment(ref offset, 1 << (tag & 15))), _bytes.Length);
        }

        private ReadOnlySpan<byte> Segment(ref int offset, int length)
        {
            if (length > _table - offset) throw Invalid();
            var result = _bytes.AsSpan(offset, length);
            offset += length;
            return result;
        }
    }

    internal static byte[] Write(Dictionary<string, object?> data)
    {
        using var output = new MemoryStream();
        var offsets = new List<ulong>();
        output.Write("bplist00"u8);
        int root = WriteObject(output, offsets, data);
        ulong table = (ulong)output.Position;
        foreach (var offset in offsets) WriteUInt(output, offset, 4);
        output.Write(new byte[6]);
        output.WriteByte(4);
        output.WriteByte(2);
        WriteUInt(output, (ulong)offsets.Count, 8);
        WriteUInt(output, (ulong)root, 8);
        WriteUInt(output, table, 8);
        return output.ToArray();
    }

    private static int WriteObject(Stream stream, List<ulong> offsets, object? value)
    {
        if (value is Dictionary<string, object?> map)
        {
            int[] keys = map.Keys.Select(key => WriteObject(stream, offsets, key)).ToArray();
            int[] values = map.Values.Select(item => WriteObject(stream, offsets, item)).ToArray();
            int id = Mark(stream, offsets);
            WriteCount(stream, 0xd0, keys.Length);
            foreach (int item in keys) WriteUInt(stream, (ulong)item, 2);
            foreach (int item in values) WriteUInt(stream, (ulong)item, 2);
            return id;
        }
        if (value is object?[] list)
        {
            int[] items = list.Select(item => WriteObject(stream, offsets, item)).ToArray();
            int id = Mark(stream, offsets);
            WriteCount(stream, 0xa0, items.Length);
            foreach (int item in items) WriteUInt(stream, (ulong)item, 2);
            return id;
        }
        int index = Mark(stream, offsets);
        switch (value)
        {
            case null: stream.WriteByte(0); break;
            case bool flag: stream.WriteByte((byte)(flag ? 9 : 8)); break;
            case int i: WriteInteger(stream, i); break;
            case long l: WriteInteger(stream, l); break;
            case double real:
                stream.WriteByte(0x23);
                WriteUInt(stream, unchecked((ulong)BitConverter.DoubleToInt64Bits(real)), 8);
                break;
            case string s:
                bool ascii = s.All(static c => c <= 127);
                WriteCount(stream, ascii ? 0x50 : 0x60, s.Length);
                stream.Write((ascii ? Encoding.ASCII : Encoding.BigEndianUnicode).GetBytes(s));
                break;
            case byte[] bytes:
                WriteCount(stream, 0x40, bytes.Length);
                stream.Write(bytes);
                break;
            default: throw new ArgumentException("Unsupported AirPlay property type.", nameof(value));
        }
        return index;
    }

    private static int Mark(Stream stream, List<ulong> offsets)
    {
        offsets.Add((ulong)stream.Position);
        return offsets.Count - 1;
    }

    private static void WriteCount(Stream stream, int tag, int count)
    {
        stream.WriteByte((byte)(tag | Math.Min(15, count)));
        if (count >= 15) WriteInteger(stream, count);
    }

    private static void WriteInteger(Stream stream, long value)
    {
        int width = value is >= 0 and <= byte.MaxValue ? 0 : value is >= 0 and <= ushort.MaxValue ? 1
            : value is >= 0 and <= uint.MaxValue ? 2 : 3;
        stream.WriteByte((byte)(0x10 | width));
        WriteUInt(stream, unchecked((ulong)value), 1 << width);
    }

    private static void WriteUInt(Stream stream, ulong value, int size)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes[(8 - size)..]);
    }

    private static ulong ReadUInt(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        foreach (byte b in data) value = (value << 8) | b;
        return value;
    }

    private static int Bound(ulong value, int max) => max >= 0 && value <= (ulong)max ? (int)value : throw Invalid();
    private static InvalidDataException Invalid() => new("Invalid AirPlay binary property list.");
}
