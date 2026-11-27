using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SonicRoute.AndroidAudio;

// Deliberately not a record: default formatting must never display the invitation.
internal sealed class SrapInvite(byte[] id, byte[] secret, string fingerprint, IPEndPoint? endpoint) : IDisposable
{
    private const string Prefix = "sonicroute://pair?";
    internal byte[] Id { get; } = id;
    internal byte[] Secret { get; } = secret;
    internal string Fingerprint { get; } = fingerprint;
    internal IPEndPoint? Endpoint { get; } = endpoint;
    internal static bool ContainsInvitation(string text) => text.Contains(Prefix, StringComparison.OrdinalIgnoreCase);

    internal static SrapInvite Parse(string text)
    {
        var raw = text.Trim();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw.Length > 1024 || !raw.StartsWith(Prefix, StringComparison.Ordinal)) throw Invalid();
        foreach (string field in raw[Prefix.Length..].Split('&'))
        {
            int equal = field.IndexOf('=');
            if (equal <= 0 || equal == field.Length - 1) throw Invalid();
            string key = field[..equal];
            string value = field[(equal + 1)..];
            if (key is not ("v" or "id" or "s" or "fp" or "h" or "p") || !fields.TryAdd(key, value)) throw Invalid();
        }
        if (fields.GetValueOrDefault("v") != "1" || !IsFingerprint(fields.GetValueOrDefault("fp"))) throw Invalid();
        IPEndPoint? endpoint = null;
        if (fields.TryGetValue("h", out string? host) | fields.TryGetValue("p", out string? port))
        {
            if (host == null || port == null || !TryAddress(host, out var address) || !TryPort(port, out int number)) throw Invalid();
            endpoint = new(address!, number);
        }
        byte[] invite = Decode(fields.GetValueOrDefault("id"), 16, 16);
        byte[] secret = Decode(fields.GetValueOrDefault("s"), 16, 32);
        return new(invite, secret, fields["fp"].ToUpperInvariant(), endpoint);
    }

    private static FormatException Invalid() => new("Invalid SonicRoute invitation.");

    private static bool TryAddress(string host, out IPAddress? address)
    {
        address = null;
        // h is an IP literal. For IPv6, accept brackets and %25 followed by a numeric
        // client-local interface index. A raw Android interface name is not portable.
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.Contains(':'))
        {
            int zone = host.IndexOf('%');
            var literal = zone < 0 ? host : host[..zone];
            if (literal.Length > 45 || literal.Any(c => !Uri.IsHexDigit(c) && c is not (':' or '.'))) return false;
            if (zone >= 0)
            {
                if (!host.AsSpan(zone).StartsWith("%25", StringComparison.Ordinal)
                    || !uint.TryParse(host.AsSpan(zone + 3), System.Globalization.NumberStyles.None, null, out uint index) || index == 0)
                    return false;
                host = host[..zone] + "%" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!IPAddress.TryParse(host, out address) || address.AddressFamily != AddressFamily.InterNetworkV6
                || address.IsIPv4MappedToIPv6 || address.IsIPv6Multicast) return false;
            return !address.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(address)
                && (!address.IsIPv6LinkLocal || address.ScopeId != 0);
        }
        if (!IPAddress.TryParse(host, out address) || address.AddressFamily != AddressFamily.InterNetwork
            || address.ToString() != host || IPAddress.IsLoopback(address)) return false;
        byte first = address.GetAddressBytes()[0];
        return first is > 0 and < 224;
    }

    private static bool TryPort(string value, out int port) => int.TryParse(value, System.Globalization.NumberStyles.None, null, out port)
        && port is > 0 and <= 65535 && value == port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Decode(string? text, int min, int max)
    {
        if (text == null || text.Length > (max * 4 + 2) / 3 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw Invalid();
        byte[] bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) & ~3, '='));
        if (bytes.Length >= min && bytes.Length <= max && Encode(bytes) == text) return bytes;
        CryptographicOperations.ZeroMemory(bytes);
        throw Invalid();
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(Secret);
}
