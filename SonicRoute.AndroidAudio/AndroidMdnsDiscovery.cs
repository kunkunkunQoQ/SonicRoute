using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SonicRoute.AndroidAudio;

/// <summary>Short-lived DNS-SD queries for an explicit connect operation, independent of the shared ADB cache.</summary>
public static class AndroidMdnsDiscovery
{
    public static async Task<AndroidMdnsService?> FindAsync(string type, string? name, string? host, string? guid,
        TimeSpan duration, CancellationToken token)
    {
        AndroidMdnsService? found = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        IPAddress? preferredHost = host != null && IPAddress.TryParse(host.Trim('[', ']'), out var parsedHost) ? parsedHost : null;
        await RunQueriesAsync([type], name, preferredHost, duration, (service, source) =>
        {
            if (!service.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) return false;
            if (name != null && !AndroidWirelessConnection.MatchesPairingName(service.Name, name)) return false;
            if (host != null && (preferredHost == null || !source.Equals(preferredHost))) return false;
            if (host == null && guid != null)
            {
                string prefix = guid.StartsWith("adb-", StringComparison.OrdinalIgnoreCase) ? guid : "adb-" + guid;
                if (!service.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase) && !service.Name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (Interlocked.CompareExchange(ref found, service, null) == null)
                stop.Cancel();
            return true;
        }, stop.Token, throwOnOuterCancel: token).ConfigureAwait(false);
        return found;
    }

    internal static async Task<IReadOnlyList<AndroidMdnsService>> DiscoverAllAsync(TimeSpan duration, CancellationToken token)
    {
        var results = new Dictionary<string, AndroidMdnsService>(StringComparer.OrdinalIgnoreCase);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        int settleArmed = 0;
        await RunQueriesAsync(["_adb-tls-pairing._tcp", "_adb-tls-connect._tcp"], null, null, duration, (service, _) =>
        {
            lock (results)
                results[$"{service.Type}\0{service.Name}\0{service.Address}"] = service;
            if (Interlocked.Exchange(ref settleArmed, 1) == 0)
                stop.CancelAfter(TimeSpan.FromMilliseconds(200));
            return false;
        }, stop.Token, throwOnOuterCancel: token).ConfigureAwait(false);
        lock (results)
            return results.Values.ToList();
    }

    private static async Task RunQueriesAsync(string[] types, string? name, IPAddress? preferredHost, TimeSpan duration,
        Func<AndroidMdnsService, IPAddress, bool> onService, CancellationToken queryToken, CancellationToken throwOnOuterCancel)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(queryToken);
        lifetime.CancelAfter(duration);
        IPAddress[] addresses = SelectCandidateAddresses(preferredHost);
        if (addresses.Length == 0) return;

        byte[][] queries = types.Select(t => BuildQuery(t, name)).ToArray();
        var work = addresses.Select(a => QueryInterfaceAsync(a, queries, onService, lifetime.Token)).ToList();
        try
        {
            await Task.WhenAll(work).ConfigureAwait(false);
            throwOnOuterCancel.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!throwOnOuterCancel.IsCancellationRequested) { }
    }

    private static IPAddress[] SelectCandidateAddresses(IPAddress? preferredHost)
    {
        try
        {
            var candidates = new List<(int Rank, IPAddress Address)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                bool isVirtual = IsLikelyVirtualOrTunnel(nic);
                foreach (var uni in nic.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(uni.Address))
                        continue;
                    int rank = preferredHost != null && IsSameSubnet(uni.Address, uni.IPv4Mask, preferredHost) ? 0
                        : isVirtual ? 3
                        : nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 1
                        : 2;
                    candidates.Add((rank, uni.Address));
                }
            }
            return candidates
                .OrderBy(c => c.Rank)
                .Select(c => c.Address)
                .Distinct()
                .Take(8)
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static bool IsSameSubnet(IPAddress local, IPAddress? mask, IPAddress target)
    {
        if (mask == null || local.AddressFamily != AddressFamily.InterNetwork || target.AddressFamily != AddressFamily.InterNetwork)
            return false;
        Span<byte> a = stackalloc byte[4], m = stackalloc byte[4], t = stackalloc byte[4];
        if (!local.TryWriteBytes(a, out _) || !mask.TryWriteBytes(m, out _) || !target.TryWriteBytes(t, out _))
            return false;
        for (int i = 0; i < 4; i++)
        {
            if ((a[i] & m[i]) != (t[i] & m[i])) return false;
        }
        return true;
    }

    private static bool IsLikelyVirtualOrTunnel(NetworkInterface nic)
    {
        if ((int)nic.NetworkInterfaceType == 53 || nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return true;
        string text = nic.Name + " " + nic.Description;
        return text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WSL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Clash", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Radmin", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task QueryInterfaceAsync(IPAddress local, byte[][] queries,
        Func<AndroidMdnsService, IPAddress, bool> onService, CancellationToken token)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(local, 0)); // Legacy unicast replies; no shared port, firewall change or daemon restart.
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            byte[] buffer = new byte[4096];
            var endpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            Task<SocketReceiveFromResult>? receive = null;
            int probeDelayMs = 300;
            while (true)
            {
                foreach (byte[] query in queries)
                    await socket.SendToAsync(query, SocketFlags.None, endpoint, token).ConfigureAwait(false);
                var nextProbe = Task.Delay(TimeSpan.FromMilliseconds(probeDelayMs), token);
                probeDelayMs = Math.Min(probeDelayMs * 2, 2500);
                while (!nextProbe.IsCompleted)
                {
                    receive ??= socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token).AsTask();
                    if (await Task.WhenAny(receive, nextProbe).ConfigureAwait(false) != receive) break;
                    var packet = await receive.ConfigureAwait(false); receive = null;
                    var source = ((IPEndPoint)packet.RemoteEndPoint).Address;
                    foreach (var service in ParseResponse(buffer.AsSpan(0, packet.ReceivedBytes), source))
                    {
                        if (onService(service, source)) return;
                    }
                }
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (NetworkInformationException) { }
    }
    public static byte[] BuildQuery(string type, string? name)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (string label in ((name == null ? "" : name + ".") + type + ".local").Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Invalid DNS label.");
            stream.WriteByte((byte)bytes.Length); stream.Write(bytes);
        }
        stream.WriteByte(0); stream.WriteByte(0); stream.WriteByte(name == null ? (byte)12 : (byte)33);
        stream.WriteByte(0x80); stream.WriteByte(1);
        return stream.ToArray();
    }
    public static IReadOnlyList<AndroidMdnsService> ParseResponse(ReadOnlySpan<byte> packet, IPAddress source)
    {
        var services = new List<AndroidMdnsService>();
        if (packet.Length < 12 || (packet[2] & 0x80) == 0) return services;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) + BinaryPrimitives.ReadUInt16BigEndian(packet[8..]) + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        if (questions > 64 || records > 256) return services;
        int offset = 12;
        for (int i = 0; i < questions; ++i) { if (ReadName(packet, ref offset) == null || offset > packet.Length - 4) return services; offset += 4; }
        for (int i = 0; i < records; ++i)
        {
            string? owner = ReadName(packet, ref offset);
            if (owner == null || offset > packet.Length - 10) return services;
            int type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]), length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;
            if (length > packet.Length - offset) return services;
            if (type == 33 && length >= 7)
            {
                int port = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 4)..]);
                foreach (string serviceType in new[] { "_adb-tls-pairing._tcp", "_adb-tls-connect._tcp" })
                {
                    string suffix = "." + serviceType + ".local";
                    if (port > 0 && owner.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        services.Add(new(owner[..^suffix.Length], serviceType, $"{source}:{port}"));
                }
            }
            offset += length;
        }
        return services;
    }
    private static string? ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>(); int position = offset, resume = -1, total = 0;
        for (int hops = 0; hops < 64 && position < packet.Length; ++hops)
        {
            byte count = packet[position++];
            if (count == 0) { offset = resume >= 0 ? resume : position; return string.Join('.', labels); }
            if ((count & 0xc0) == 0xc0)
            {
                if (position >= packet.Length) return null;
                int target = ((count & 0x3f) << 8) | packet[position++];
                resume = resume < 0 ? position : resume; position = target; continue;
            }
            if (count > 63 || count > packet.Length - position || (total += count + 1) > 255) return null;
            labels.Add(Encoding.UTF8.GetString(packet.Slice(position, count))); position += count;
        }
        return null;
    }
}
