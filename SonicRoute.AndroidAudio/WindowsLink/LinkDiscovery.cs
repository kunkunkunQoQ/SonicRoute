using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SonicRoute.WindowsLink;

internal sealed class LinkDiscovery : IDisposable
{
    private const int Port = 46917;
    private const int HeaderSize = 42;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] VirtualAdapterTokens =
    [
        "wintun", "tun", "tap", "clash", "mihomo", "flclash",
        "wireguard", "tailscale", "zerotier", "vethernet", "hyper-v",
        "vmware", "virtualbox", "vbox", "wsl", "loopback"
    ];

    internal sealed record LanCandidate(
        IPAddress Address,
        IPAddress Mask,
        int Index,
        string Name,
        string Description,
        bool HasGateway,
        NetworkInterfaceType InterfaceType);

    private readonly UdpClient _socket = new(AddressFamily.InterNetwork);
    private readonly byte[] _query;
    private readonly byte[] _answer;
    private readonly string _id;
    private readonly IPEndPoint? _directedTarget;
    private NetworkSnapshot _network = new([], []);

    private sealed record NetworkSnapshot((uint Address, uint Mask, int Priority)[] Subnets, IPEndPoint[] Broadcasts);

    internal LinkDiscovery(string id, string name, int port, LinkCapabilities capabilities,
        IPEndPoint? bind = null, IPEndPoint? directedTarget = null)
    {
        _id = id;
        _directedTarget = directedTarget;
        _query = Encode(1, id, name, port, capabilities);
        _answer = Encode(2, id, name, port, capabilities);
        try
        {
            _socket.EnableBroadcast = true;
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(bind ?? new IPEndPoint(IPAddress.Any, Port));
            try { _socket.Client.IOControl((IOControlCode)(-1744830452), [0, 0, 0, 0], null); }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException) { }
        }
        catch { _socket.Dispose(); throw; }
    }

    internal static IPAddress? SelectDisplayLanAddress()
    {
        var candidates = ReadLanCandidates(out var ipv6);
        return SelectDisplayLanAddress(candidates) ?? ipv6;
    }

    internal static IPAddress? SelectDisplayLanAddress(IReadOnlyList<LanCandidate> candidates)
    {
        if (candidates.Count == 0) return null;
        var preferred = candidates.Where(c => !IsVirtualIp(c.Address) && !IsVirtualAdapterName(c.Name, c.Description)).ToArray();
        if (preferred.Length == 0)
            preferred = candidates.Where(c => !IsVirtualIp(c.Address)).ToArray();
        if (preferred.Length == 0)
            preferred = candidates.ToArray();
        return preferred
            .OrderByDescending(ScoreCandidate)
            .ThenBy(c => c.Index)
            .Select(c => c.Address)
            .FirstOrDefault();
    }

    internal IPAddress? RefreshInterfaces()
    {
        IPAddress? ipv6 = null;
        LanCandidate[] candidates = _directedTarget == null ? ReadLanCandidates(out ipv6) : [];
        var ordered = candidates.OrderByDescending(ScoreCandidate).ToArray();
        var subnets = ordered
            .Select(candidate => (Address: ToAddressNumber(candidate.Address), Mask: ToAddressNumber(candidate.Mask),
                Priority: ScoreCandidate(candidate) + 1)).ToArray();
        IPEndPoint[] broadcasts;
        if (_directedTarget is { } target)
        {
            broadcasts = [target];
        }
        else
        {
            var broadcastCandidates = ordered
                .Select(c => (Address: ToAddressNumber(c.Address), Mask: ToAddressNumber(c.Mask), Virtual: IsVirtualIp(c.Address)))
                .Where(s => s.Mask < uint.MaxValue - 1)
                .ToArray();
            var preferred = broadcastCandidates.Where(s => !s.Virtual).ToArray();
            var active = preferred.Length > 0 ? preferred : broadcastCandidates;
            broadcasts = active
                .Select(subnet => subnet.Address | ~subnet.Mask).Distinct()
                .Select(address => new IPEndPoint(FromAddressNumber(address), Port)).ToArray();
        }
        Volatile.Write(ref _network, new NetworkSnapshot(subnets, broadcasts));
        return _directedTarget == null ? SelectDisplayLanAddress(candidates) ?? ipv6 : IPAddress.Loopback;
    }

    internal int RankAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return 0;
        if (_directedTarget != null) return IPAddress.IsLoopback(address) ? 1 : 0;
        uint value = ToAddressNumber(address);
        foreach (var subnet in Volatile.Read(ref _network).Subnets)
            if (((subnet.Address ^ value) & subnet.Mask) == 0) return subnet.Priority;
        return 0;
    }

    private static uint ToAddressNumber(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress FromAddressNumber(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes);
    }

    private static LanCandidate[] ReadLanCandidates(out IPAddress? ipv6)
    {
        ipv6 = null;
        var list = new List<LanCandidate>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                ipv6 ??= properties.UnicastAddresses.Select(entry => entry.Address).FirstOrDefault(ip =>
                    ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast
                    && !ip.IsIPv6SiteLocal && !ip.IsIPv4MappedToIPv6 && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.IPv6Any));
                if (!adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
                if (properties.GetIPv4Properties() is not { } ipv4) continue;
                bool hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                        || unicast.IPv4Mask is not { } mask || mask.Equals(IPAddress.Any)) continue;
                    list.Add(new LanCandidate(unicast.Address, mask, ipv4.Index,
                        adapter.Name ?? "", adapter.Description ?? "", hasGateway, adapter.NetworkInterfaceType));
                }
            }
            catch (NetworkInformationException) { }
        }
        return list.DistinctBy(c => (c.Address, c.Mask)).OrderByDescending(ScoreCandidate).Take(16).ToArray();
    }

    private static int ScoreCandidate(LanCandidate candidate)
    {
        int score = 0;
        if (!IsVirtualIp(candidate.Address)) score += 1000;
        if (!IsVirtualAdapterName(candidate.Name, candidate.Description)) score += 1000;
        if (candidate.HasGateway) score += 500;
        byte[] bytes = candidate.Address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            if (bytes[0] == 192 && bytes[1] == 168) score += 200;
            else if (bytes[0] == 10) score += 150;
            else if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) score += 100;
        }
        if (candidate.InterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.GigabitEthernet)
            score += 50;
        return score;
    }

    internal static bool IsVirtualIp(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        // 198.18.0.0/15 (benchmarking / Clash/Mihomo/FlClash fake-ip TUN), 169.254.0.0/16 (APIPA), 100.64.0.0/10 (CGNAT/Tailscale)
        return (bytes[0] == 198 && (bytes[1] & 0xFE) == 18)
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
    }

    private static bool IsVirtualAdapterName(string name, string description)
    {
        foreach (string token in VirtualAdapterTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)
                || description.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal async Task DiscoverAsync(CancellationToken token)
    {
        foreach (var target in Volatile.Read(ref _network).Broadcasts)
        {
            try { await _socket.SendAsync(_query, target, token).ConfigureAwait(false); }
            catch (SocketException) { /* A changing adapter can invalidate this broadcast. */ }
        }
    }

    internal async Task RequestReconnectAsync(byte[] request, IPAddress address, CancellationToken token)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return;
        try { await _socket.SendAsync(request, _directedTarget ?? new IPEndPoint(address, Port), token).ConfigureAwait(false); }
        catch (SocketException) { }
    }

    internal async Task ListenAsync(Action<LinkOffer, bool> found, Action<ReadOnlyMemory<byte>, IPAddress> reconnect, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult message;
            try { message = await _socket.ReceiveAsync(token).ConfigureAwait(false); }
            catch (SocketException) when (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                continue;
            }
            if (RankAddress(message.RemoteEndPoint.Address) == 0) continue;
            if (message.Buffer is [_, _, _, _, 1, 3, ..])
            {
                reconnect(message.Buffer, message.RemoteEndPoint.Address);
                continue;
            }
            if (!TryDecode(message.Buffer, message.RemoteEndPoint.Address, out var offer, out bool query)
                || offer!.Id == _id) continue;
            found(offer, query);
            if (query)
            {
                try { await _socket.SendAsync(_answer, message.RemoteEndPoint, token).ConfigureAwait(false); }
                catch (SocketException) { }
            }
        }
    }

    private static byte[] Encode(byte type, string id, string name, int port, LinkCapabilities capabilities)
    {
        byte[] label = Encoding.UTF8.GetBytes(name);
        byte[] data = new byte[HeaderSize + label.Length];
        "SRWL"u8.CopyTo(data);
        data[4] = 1;
        data[5] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)port);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)capabilities);
        Convert.FromHexString(id).CopyTo(data, 10);
        label.CopyTo(data, HeaderSize);
        return data;
    }

    private static bool TryDecode(ReadOnlySpan<byte> data, IPAddress address, out LinkOffer? offer, out bool query)
    {
        offer = null;
        query = false;
        if (data.Length is <= HeaderSize or > HeaderSize + 256 || !data[..4].SequenceEqual("SRWL"u8)
            || data[4] != 1 || data[5] is not (1 or 2)) return false;
        int port = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (port == 0) return false;
        string name;
        try { name = Utf8.GetString(data[HeaderSize..]); }
        catch (DecoderFallbackException) { return false; }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl)) return false;
        var capabilities = (LinkCapabilities)BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        offer = new LinkOffer(Convert.ToHexString(data.Slice(10, 32)), name, new IPEndPoint(address, port), capabilities);
        query = data[5] == 1;
        return true;
    }

    public void Dispose() => _socket.Dispose();
}
