using System.Buffers.Binary;
using System.Text;

namespace SonicRoute.AirPlay;

internal sealed record AirPlayMetadata(string Title, string Artist, byte[]? Artwork);

// DMAP item names and artists are UTF-8, with a four-byte tag and big-endian length.
internal sealed class AirPlayMetadataReader
{
    private const int TextLimit = 1024;
    internal const int ArtworkLimit = 512 * 1024;
    private uint? _artworkTime;
    internal AirPlayMetadata Value { get; private set; } = new("", "", null);

    internal bool Update(string contentType, byte[] body, uint? time)
    {
        var previous = Value;
        if (contentType.Equals("application/x-dmap-tagged", StringComparison.OrdinalIgnoreCase))
        {
            string? title = null, artist = null;
            if (!ReadTags(body, 0, ref title, ref artist) || (title == null && artist == null)) return false;
            if ((title ?? "") == previous.Title && (artist ?? "") == previous.Artist) return false;
            bool artworkForTrack = (time.HasValue && _artworkTime == time)
                || (previous.Title.Length == 0 && previous.Artist.Length == 0);
            Value = new(title ?? "", artist ?? "", artworkForTrack ? previous.Artwork : null);
        }
        else if (IsArtworkType(contentType))
        {
            if (body.Length > ArtworkLimit) return false;
            if (body.Length != 0 && !(IsJpeg(body) || IsPng(body))) return false;
            _artworkTime = time;
            if (body.AsSpan().SequenceEqual(previous.Artwork)) return false;
            Value = previous with { Artwork = body.Length == 0 ? null : body };
        }
        return !ReferenceEquals(Value, previous);
    }

    private static bool IsJpeg(ReadOnlySpan<byte> body) => body.StartsWith(new byte[] { 0xff, 0xd8, 0xff });
    private static bool IsPng(ReadOnlySpan<byte> body) => body.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
    internal static bool IsArtworkType(string contentType) => contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase);

    private static bool ReadTags(ReadOnlySpan<byte> data, int depth, ref string? title, ref string? artist)
    {
        if (depth > 4) return false;
        while (!data.IsEmpty)
        {
            if (data.Length < 8) return false;
            var tag = data[..4];
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            if (length > data.Length - 8) return false;
            var field = data.Slice(8, (int)length);
            if (tag.SequenceEqual("minm"u8)) title = ReadText(field);
            else if (tag.SequenceEqual("asar"u8)) artist = ReadText(field);
            else if ((tag.SequenceEqual("mlit"u8) || tag.SequenceEqual("mdcl"u8) || tag.SequenceEqual("cmst"u8))
                && !ReadTags(field, depth + 1, ref title, ref artist)) return false;
            data = data[(8 + (int)length)..];
        }
        return true;
    }

    private static string ReadText(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, TextLimit)]);
        return string.Concat(text.Where(static c => !char.IsControl(c))).Trim();
    }
}
