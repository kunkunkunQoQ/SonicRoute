using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SonicRoute.AirPlay;

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
    private AirPlayDiscovery.Lan[] _lan = [];

    internal LinkDiscovery(string id, string name, int port, LinkCapabilities capabilities)
    {
        _id = id;
        _query = Encode(1, id, name, port, capabilities);
        _answer = Encode(2, id, name, port, capabilities);
        try
        {
            _socket.EnableBroadcast = true;
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        catch { _socket.Dispose(); throw; }
    }

    internal static IPAddress? SelectDisplayLanAddress() => SelectDisplayLanAddress(ReadLanCandidates());

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

    internal static AirPlayDiscovery.Lan[] FilterBroadcastInterfaces(AirPlayDiscovery.Lan[] interfaces)
    {
        if (interfaces.Length == 0) return interfaces;
        var filtered = interfaces.Where(lan => !IsVirtualIp(lan.Address)).ToArray();
        return filtered.Length > 0 ? filtered : interfaces;
    }

    private static LanCandidate[] ReadLanCandidates()
    {
        var list = new List<LanCandidate>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var unicast = properties.UnicastAddresses.FirstOrDefault(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork
                    && a.IPv4Mask is { } mask && !mask.Equals(IPAddress.Any));
                if (unicast == null || properties.GetIPv4Properties() is not { } ipv4) continue;
                bool hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                list.Add(new LanCandidate(unicast.Address, unicast.IPv4Mask!, ipv4.Index,
                    adapter.Name ?? "", adapter.Description ?? "", hasGateway, adapter.NetworkInterfaceType));
            }
            catch (NetworkInformationException) { }
        }
        return list.DistinctBy(c => c.Index).ToArray();
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
        AirPlayDiscovery.Lan[] lan = FilterBroadcastInterfaces(AirPlayDiscovery.ReadInterfaces());
        Volatile.Write(ref _lan, lan);
        foreach (var network in lan)
        {
            uint address = BinaryPrimitives.ReadUInt32BigEndian(network.Address.GetAddressBytes());
            uint mask = BinaryPrimitives.ReadUInt32BigEndian(network.Mask.GetAddressBytes());
            byte[] broadcast = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(broadcast, address | ~mask);
            try { await _socket.SendAsync(_query, new IPEndPoint(new IPAddress(broadcast), Port), token).ConfigureAwait(false); }
            catch (SocketException) { /* A changing adapter can invalidate this broadcast. */ }
        }
    }

    internal async Task RequestReconnectAsync(byte[] request, IPAddress address, CancellationToken token)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return;
        try { await _socket.SendAsync(request, new IPEndPoint(address, Port), token).ConfigureAwait(false); }
        catch (SocketException) { }
    }

    internal async Task ListenAsync(Action<LinkOffer> found, Action<ReadOnlyMemory<byte>, IPAddress> reconnect, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult message;
            try { message = await _socket.ReceiveAsync(token).ConfigureAwait(false); }
            catch (SocketException) when (!token.IsCancellationRequested) { continue; }
            if (!Volatile.Read(ref _lan).Any(lan => lan.Includes(message.RemoteEndPoint.Address))) continue;
            if (message.Buffer is [_, _, _, _, 1, 3, ..])
            {
                reconnect(message.Buffer, message.RemoteEndPoint.Address);
                continue;
            }
            if (!TryDecode(message.Buffer, message.RemoteEndPoint.Address, out var offer, out bool query)
                || offer!.Id == _id) continue;
            found(offer);
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
