using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SonicRoute.AndroidAudio;

/// <summary>Short-lived DNS-SD queries for an explicit connect operation, independent of the shared ADB cache.</summary>
public static class AndroidMdnsDiscovery
{
    private const string PairingType = "_adb-tls-pairing._tcp";
    private const string ConnectType = AndroidWirelessConnection.ConnectType;
    private static readonly string[] AdbTypes = [PairingType, ConnectType];
    private static readonly IPAddress MulticastV4 = IPAddress.Parse("224.0.0.251");
    private static readonly byte[] MulticastV6 = IPAddress.Parse("ff02::fb").GetAddressBytes();

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
            if (guid == null && host != null && (preferredHost == null || !source.Equals(preferredHost))) return false;
            if (guid != null)
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

    internal static Task<IReadOnlyList<AndroidMdnsService>> DiscoverConnectionsAsync(CancellationToken token) =>
        CollectAsync([ConnectType], TimeSpan.FromMilliseconds(1800), settleOnFirst: false, token);

    internal static Task<IReadOnlyList<AndroidMdnsService>> DiscoverAllAsync(TimeSpan duration, CancellationToken token) =>
        CollectAsync(AdbTypes, duration, settleOnFirst: true, token);

    // A known offline phone can announce a new TLS port after reboot. Listen for
    // unsolicited advertisements while recovery is pending, without sending probes.
    internal static Task WatchConnectionsAsync(Func<AndroidMdnsService, Task> onService, CancellationToken token)
    {
        var addresses = SelectCandidateAddresses(null);
        return Task.WhenAll(WatchFamilyAsync(addresses, ipv6: false, onService, token),
            WatchFamilyAsync(addresses, ipv6: true, onService, token));
    }

    private static async Task WatchFamilyAsync(IPAddress[] addresses, bool ipv6, Func<AndroidMdnsService, Task> onService, CancellationToken token)
    {
        var family = ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        var any = ipv6 ? IPAddress.IPv6Any : IPAddress.Any;
        var locals = addresses.Where(a => a.AddressFamily == family).ToArray();
        if (locals.Length == 0) return;
        try
        {
            using var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(any, 5353));
            bool joined = false;
            foreach (var local in locals)
            {
                try
                {
                    if (ipv6)
                        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(new IPAddress(MulticastV6), local.ScopeId));
                    else socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MulticastV4, local));
                    joined = true;
                }
                catch (SocketException) { }
            }
            if (!joined) return;
            byte[] buffer = new byte[4096];
            var remote = new IPEndPoint(any, 0);
            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult packet;
                try { packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, token).ConfigureAwait(false); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize) { continue; }
                var peer = (IPEndPoint)packet.RemoteEndPoint;
                if (peer.Port != 5353 || buffer.AsSpan(0, packet.ReceivedBytes).IndexOf("_adb-tls-connect"u8) < 0) continue;
                foreach (var service in ParseResponse(buffer.AsSpan(0, packet.ReceivedBytes), peer.Address))
                    if (service.Type == ConnectType) await onService(service).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) { }
    }

    private static async Task<IReadOnlyList<AndroidMdnsService>> CollectAsync(string[] types, TimeSpan duration, bool settleOnFirst,
        CancellationToken token)
    {
        var results = new Dictionary<string, AndroidMdnsService>(StringComparer.OrdinalIgnoreCase);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        int settleArmed = 0;
        await RunQueriesAsync(types, null, null, duration, (service, _) =>
        {
            lock (results)
                results[$"{service.Type}\0{service.Name}\0{service.Address}"] = service;
            if (settleOnFirst && Interlocked.Exchange(ref settleArmed, 1) == 0)
                stop.CancelAfter(TimeSpan.FromMilliseconds(200));
            return false;
        }, stop.Token, throwOnOuterCancel: token).ConfigureAwait(false);
        lock (results)
            return results.Values.OrderBy(s => s.Address.StartsWith('[')).ToArray();
    }

    private static async Task RunQueriesAsync(string[] types, string? name, IPAddress? preferredHost, TimeSpan duration,
        Func<AndroidMdnsService, IPAddress, bool> onService, CancellationToken queryToken, CancellationToken throwOnOuterCancel)
    {
        throwOnOuterCancel.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(queryToken);
        lifetime.CancelAfter(duration);
        IPAddress[] addresses = SelectCandidateAddresses(preferredHost);
        if (addresses.Length == 0) return;

        byte[][] queries = types.Select(t => BuildQuery(t, name)).ToArray();
        var work = addresses.Select(a => QueryInterfaceAsync(a, queries, types, onService, lifetime.Token)).ToList();
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
                if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                bool isVirtual = IsLikelyVirtualOrTunnel(nic);
                foreach (var uni in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = uni.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork && !address.IsIPv6LinkLocal) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    if (address.IsIPv6LinkLocal && address.ScopeId == 0)
                    {
                        int index = nic.GetIPProperties().GetIPv6Properties()?.Index ?? 0;
                        if (index == 0) continue;
                        address = new IPAddress(address.GetAddressBytes(), index);
                    }
                    int rank = preferredHost != null && (address.AddressFamily == AddressFamily.InterNetwork
                        ? IsSameSubnet(address, uni.IPv4Mask, preferredHost)
                        : preferredHost.AddressFamily == AddressFamily.InterNetworkV6 && preferredHost.ScopeId == address.ScopeId) ? 0
                        : isVirtual ? 3
                        : nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 1
                        : 2;
                    candidates.Add((rank, address));
                }
            }
            return candidates
                .OrderBy(c => c.Rank)
                .ThenBy(c => c.Address.AddressFamily != AddressFamily.InterNetwork)
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

    private static async Task QueryInterfaceAsync(IPAddress local, byte[][] queries, string[] types,
        Func<AndroidMdnsService, IPAddress, bool> onService, CancellationToken token)
    {
        try
        {
            using var socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(local, 0)); // A one-shot query receives a unicast reply on this ephemeral port.
            bool ipv6 = local.AddressFamily == AddressFamily.InterNetworkV6;
            if (ipv6)
            {
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, (int)local.ScopeId);
                socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 255);
            }
            else
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            }
            var endpoint = new IPEndPoint(ipv6 ? new IPAddress(MulticastV6, local.ScopeId) : MulticastV4, 5353);
            await QueryServicesAsync(socket, endpoint, queries, types, onService, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (NetworkInformationException) { }
    }

    internal static async Task QueryServicesAsync(Socket socket, IPEndPoint endpoint, byte[][] queries, string[] types,
        Func<AndroidMdnsService, IPAddress, bool> onService, CancellationToken token)
    {
        bool ipv6 = socket.AddressFamily == AddressFamily.InterNetworkV6;
        long scope = ipv6 ? ((IPEndPoint)socket.LocalEndPoint!).Address.ScopeId : 0;
        byte[] buffer = new byte[4096];
        var remoteTemplate = new IPEndPoint(ipv6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        var requestedInstances = new HashSet<ServiceInstance>();
        Task<SocketReceiveFromResult>? receive = null;
        int probeDelayMs = 1000;
        while (true)
        {
            foreach (byte[] query in queries)
                await socket.SendToAsync(query, SocketFlags.None, endpoint, token).ConfigureAwait(false);
            var nextProbe = Task.Delay(TimeSpan.FromMilliseconds(probeDelayMs), token);
            probeDelayMs = Math.Min(probeDelayMs * 2, 2500);
            while (!nextProbe.IsCompleted)
            {
                receive ??= socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteTemplate, token).AsTask();
                if (await Task.WhenAny(receive, nextProbe).ConfigureAwait(false) != receive) break;
                var packet = await receive.ConfigureAwait(false); receive = null;
                var source = (IPEndPoint)packet.RemoteEndPoint;
                // Local mDNS relays can return the one-shot reply from a different UDP port.
                var response = ReadResponse(buffer.AsSpan(0, packet.ReceivedBytes));
                if (response == null) continue;
                foreach (var record in response.Services)
                {
                    if (!types.Contains(record.Instance.Type, StringComparer.OrdinalIgnoreCase)) continue;
                    var address = response.GetAddress(record.Target, source.Address, scope);
                    if (onService(record.ToService(address), address)) return;
                }
                foreach (var instance in response.Instances)
                {
                    if (requestedInstances.Count >= 32 || !types.Contains(instance.Type, StringComparer.OrdinalIgnoreCase)
                        || response.Services.Any(s => s.Instance == instance) || !requestedInstances.Add(instance)) continue;
                    await socket.SendToAsync(BuildQuery(instance.Type, instance.Name), SocketFlags.None, endpoint, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
        }
    }
    public static byte[] BuildQuery(string type, string? name)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        var labels = type.Split('.').Append("local");
        if (name != null) labels = labels.Prepend(name);
        foreach (string label in labels)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Invalid DNS label.");
            stream.WriteByte((byte)bytes.Length); stream.Write(bytes);
        }
        stream.WriteByte(0); stream.WriteByte(0); stream.WriteByte(name == null ? (byte)12 : (byte)33);
        // RFC 6762 legacy one-shot queries use class IN; the non-5353 source port requests a unicast response.
        stream.WriteByte(0); stream.WriteByte(1);
        return stream.ToArray();
    }
    public static IReadOnlyList<AndroidMdnsService> ParseResponse(ReadOnlySpan<byte> packet, IPAddress source)
    {
        var response = ReadResponse(packet);
        if (response == null) return [];
        long scope = source.AddressFamily == AddressFamily.InterNetworkV6 ? source.ScopeId : 0;
        return response.Services.Select(s => s.ToService(response.GetAddress(s.Target, source, scope))).ToArray();
    }

    private readonly record struct ServiceInstance(string Name, string Type);
    private readonly record struct ServiceRecord(ServiceInstance Instance, string Target, int Port)
    {
        internal AndroidMdnsService ToService(IPAddress address) => new(Instance.Name, Instance.Type, new IPEndPoint(address, Port).ToString());
    }

    private sealed class Response
    {
        internal readonly List<ServiceInstance> Instances = [];
        internal readonly List<ServiceRecord> Services = [];
        internal readonly List<(string Host, IPAddress Address)> Addresses = [];

        internal IPAddress GetAddress(string target, IPAddress source, long scope)
        {
            IPAddress? advertised = null;
            foreach (var (host, address) in Addresses)
            {
                if (!host.Equals(target, StringComparison.OrdinalIgnoreCase) || address.AddressFamily != source.AddressFamily) continue;
                var candidate = address.IsIPv6LinkLocal && scope != 0 ? new IPAddress(address.GetAddressBytes(), scope) : address;
                if (candidate.Equals(source)) return source;
                advertised ??= candidate;
            }
            return advertised ?? source; // Android also answers from the service host when no address record is attached.
        }
    }

    private static Response? ReadResponse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || (packet[2] & 0x80) == 0 || (packet[3] & 0x0f) != 0) return null;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) + BinaryPrimitives.ReadUInt16BigEndian(packet[8..]) + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        if (questions > 64 || records > 256) return null;
        int offset = 12;
        for (int i = 0; i < questions; ++i) { if (ReadName(packet, ref offset) == null || offset > packet.Length - 4) return null; offset += 4; }
        var response = new Response();
        for (int i = 0; i < records; ++i)
        {
            string? owner = ReadName(packet, ref offset);
            if (owner == null || offset > packet.Length - 10) return null;
            int type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            int recordClass = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]) & 0x7fff;
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
            offset += 10;
            if (length > packet.Length - offset) return null;
            int end = offset + length;
            if (recordClass == 1 && ttl != 0)
            {
                if (type == 12 && IsBrowseName(owner))
                {
                    int next = offset;
                    if (ReadName(packet, ref next) is { } instance && next == end
                        && TryReadInstance(instance) is { } ptr && owner.Equals(ptr.Type + ".local", StringComparison.OrdinalIgnoreCase))
                        response.Instances.Add(ptr);
                }
                else if (type == 33 && length >= 7 && TryReadInstance(owner) is { } service)
                {
                    int port = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 4)..]);
                    int next = offset + 6;
                    if (port > 0 && ReadName(packet, ref next) is { Length: > 0 } target && next == end)
                        response.Services.Add(new(service, target, port));
                }
                else if ((type == 1 && length == 4) || (type == 28 && length == 16))
                {
                    var address = new IPAddress(packet.Slice(offset, length));
                    if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6Multicast
                        && (type != 1 || packet[offset] < 224)) response.Addresses.Add((owner, address));
                }
            }
            offset = end;
        }
        return response;
    }

    private static bool IsBrowseName(string owner) => AdbTypes.Any(type => owner.Equals(type + ".local", StringComparison.OrdinalIgnoreCase));

    private static ServiceInstance? TryReadInstance(string owner)
    {
        foreach (string type in AdbTypes)
        {
            string suffix = "." + type + ".local";
            if (!owner.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string name = owner[..^suffix.Length];
            if (Encoding.UTF8.GetByteCount(name) is > 0 and <= 63) return new(name, type);
        }
        return null;
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
