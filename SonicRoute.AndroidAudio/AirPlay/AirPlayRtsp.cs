using System.Globalization;
using System.Text;

namespace SonicRoute.AirPlay;

internal sealed class AirPlayRtsp(Stream stream) : IDisposable
{
    internal const string Plist = "application/x-apple-binary-plist";
    internal const string Octets = "application/octet-stream";
    internal const string Pairing = "application/pairing+tlv8";
    private const int MaxHeader = 8192, MaxBody = 128 * 1024, MaxArtworkBody = 4 * 1024 * 1024;
    private readonly byte[] _input = new byte[MaxHeader];
    private int _start, _end;
    private AirPlayControlCipher? _cipher;

    internal sealed record Request(string Method, string Path, string Protocol, Dictionary<string, string> Headers, byte[] Body, bool BodySkipped)
    {
        internal string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    internal void Secure(byte[] key)
    {
        if (_cipher != null || _start != _end) throw new InvalidDataException("Unexpected data after pairing.");
        _cipher = new AirPlayControlCipher(key);
    }

    internal async Task<Request?> ReadAsync(CancellationToken token, bool allowArtwork = false)
    {
        int endOfHeader;
        while ((endOfHeader = _input.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8)) < 0)
        {
            int pending = _end - _start;
            if (pending > MaxHeader - AirPlayControlCipher.MaxFrame) throw new InvalidDataException("AirPlay header exceeds limit.");
            _input.AsSpan(_start, pending).CopyTo(_input);
            _start = 0; _end = pending;
            int length = await FillAsync(_input.AsMemory(_end), token).ConfigureAwait(false);
            if (length == 0)
            {
                if (pending == 0) return null;
                throw new EndOfStreamException();
            }
            _end += length;
        }
        string[] lines = Encoding.ASCII.GetString(_input, _start, endOfHeader).Split("\r\n");
        _start += endOfHeader + 4;
        var first = lines[0].Split(' ');
        if (first is not [_, _, "RTSP/1.0" or "HTTP/1.1"] || first[0].Length > 32)
            throw new InvalidDataException("Invalid AirPlay request line.");
        string path = first[1];
        if (!path.StartsWith('/') && Uri.TryCreate(path, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;
        int query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.AsSpan(1))
        {
            int delimiter = line.IndexOf(':');
            if (delimiter <= 0 || !headers.TryAdd(line[..delimiter].Trim(), line[(delimiter + 1)..].Trim()))
                throw new InvalidDataException("Invalid AirPlay header.");
        }
        if (headers.ContainsKey("Transfer-Encoding")) throw new InvalidDataException("Unexpected transfer encoding.");
        int contentLength = 0;
        bool isArtwork = allowArtwork && first[0] == "SET_PARAMETER" && headers.TryGetValue("Content-Type", out var kind)
            && AirPlayMetadataReader.IsArtworkType(kind.Split(';', 2)[0].Trim());
        int bodyLimit = isArtwork ? MaxArtworkBody : MaxBody;
        if (headers.TryGetValue("Content-Length", out string? value)
            && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength > bodyLimit))
            throw new InvalidDataException("AirPlay body exceeds limit.");
        // Drain large covers with the existing read buffer so a cover cannot interrupt playback.
        bool skipArtwork = isArtwork && contentLength > AirPlayMetadataReader.ArtworkLimit;
        byte[] content = contentLength == 0 || skipArtwork ? [] : new byte[contentLength];
        int copied = 0;
        while (copied < contentLength)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await FillAsync(_input, token).ConfigureAwait(false);
                if (_end == 0) throw new EndOfStreamException();
            }
            int count = Math.Min(contentLength - copied, _end - _start);
            if (!skipArtwork) _input.AsSpan(_start, count).CopyTo(content.AsSpan(copied));
            _start += count;
            copied += count;
        }
        return new(first[0], path, first[2], headers, content, skipArtwork);
    }

    private ValueTask<int> FillAsync(Memory<byte> target, CancellationToken token) =>
        _cipher == null ? stream.ReadAsync(target, token) : _cipher.ReadAsync(stream, target, token);

    internal async Task RespondAsync(Request request, CancellationToken token, int code = 200,
        byte[]? body = null, string? contentType = null, string? extra = null)
    {
        string reason = code switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
            415 => "Unsupported Media Type", 453 => "Not Enough Bandwidth", 455 => "Method Not Valid in This State",
            470 => "Client Authentication Failure", _ => "Not Implemented"
        };
        using var response = new MemoryStream();
        var header = new StringBuilder(FormattableString.Invariant($"{request.Protocol} {code} {reason}\r\nServer: AirTunes/366.0\r\n"));
        if (int.TryParse(request.Header("CSeq"), NumberStyles.None, CultureInfo.InvariantCulture, out int sequence))
            header.Append(FormattableString.Invariant($"CSeq: {sequence}\r\n"));
        if (contentType != null) header.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (extra != null) header.Append(extra);
        header.Append(FormattableString.Invariant($"Content-Length: {body?.Length ?? 0}\r\n\r\n"));
        response.Write(Encoding.ASCII.GetBytes(header.ToString()));
        if (body != null) response.Write(body);
        var data = response.GetBuffer().AsMemory(0, (int)response.Length);
        if (_cipher != null) await _cipher.WriteAsync(stream, data, token).ConfigureAwait(false);
        else await stream.WriteAsync(data, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cipher?.Dispose();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_input);
    }
}
