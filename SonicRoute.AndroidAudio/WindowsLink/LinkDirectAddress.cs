using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SonicRoute.WindowsLink;

internal sealed record LinkDirectAddress(string Host, int Port, IPAddress? Literal)
{
    internal const int DefaultPort = 46916;
    internal string Canonical => Literal == null ? $"{Host}:{Port}" : new IPEndPoint(Literal, Port).ToString();

    internal static bool TryParse(string? input, [NotNullWhen(true)] out LinkDirectAddress? target)
    {
        target = null;
        string value = input?.Trim() ?? "";
        if (value.Length is 0 or > 320 || value.Any(char.IsWhiteSpace)) return false;
        string host = value;
        int port = DefaultPort;
        bool bracketed = value.StartsWith('[');
        int colon = value.IndexOf(':');
        if (bracketed)
        {
            int closing = value.IndexOf(']');
            if (closing <= 1) return false;
            host = value[1..closing];
            if (closing + 1 != value.Length && (value[closing + 1] != ':' || !TryPort(value[(closing + 2)..], out port))) return false;
        }
        else if (colon >= 0 && colon == value.LastIndexOf(':'))
        {
            host = value[..colon];
            if (!TryPort(value[(colon + 1)..], out port)) return false;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (bracketed && ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
            // Reject abbreviated, octal and integer IPv4 literals: they look like different destinations.
            if (ip.AddressFamily == AddressFamily.InterNetwork && host != ip.ToString()) return false;
            ip = Normalize(ip);
            if (!IsUnicast(ip)) return false;
            target = new(ip.ToString(), port, ip);
            return true;
        }
        if (bracketed || host.Contains(':') || host.Length == 0 || host.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
        bool absolute = host.EndsWith('.');
        try { host = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(absolute ? host[..^1] : host).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        if (host.Length is 0 or > 253 || host.Split('.').Any(label => label.Length is 0 or > 63
            || label[0] == '-' || label[^1] == '-' || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))) return false;
        target = new(absolute ? host + "." : host, port, null);
        return true;
    }

    private static bool TryPort(string value, out int port) => int.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private static bool IsUnicast(IPAddress ip) => ip.AddressFamily switch
    {
        AddressFamily.InterNetwork => !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast)
            && ip.GetAddressBytes()[0] < 224,
        AddressFamily.InterNetworkV6 => !ip.Equals(IPAddress.IPv6Any) && !ip.IsIPv6Multicast,
        _ => false
    };

    internal async Task<IPEndPoint[]> ResolveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Literal != null) return [new IPEndPoint(Literal, Port)];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        IPAddress[] records;
        try { records = await Dns.GetHostAddressesAsync(Host, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new SocketException((int)SocketError.TimedOut); }
        var endpoints = records.Select(Normalize).Where(IsUnicast).Distinct().Take(12).Select(ip => new IPEndPoint(ip, Port)).ToArray();
        if (endpoints.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        return endpoints;
    }

    internal static bool IsLocal(IPAddress address) => IPAddress.IsLoopback(address)
        || NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses).Any(local => Normalize(local.Address).Equals(address));
}

internal sealed record LinkResolvedAddress(LinkDirectAddress Target, IPEndPoint[] Endpoints, string? ConnectedPeer);
internal sealed record LinkLocalAddress(string Adapter, string Address);

internal sealed class LinkAmbiguousAddressException() : IOException("Several saved computers use this address. Select a saved device.");

internal sealed class LinkSelfConnectionException() : IOException("This is your own computer's Windows Link address.");
