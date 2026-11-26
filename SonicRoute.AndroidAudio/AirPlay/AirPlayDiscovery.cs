using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SonicRoute.AirPlay;

internal sealed class AirPlayIdentity(string name, byte[]? screenKey = null, bool hevc = false, bool requireCode = false, string? id = null)
{
    // A single DNS-SD label also has to fit the 12 hex digits and '@' in the RAOP instance name.
    private const int NameByteLimit = 50;
    private const long AudioFeatures = (1L << 9) | (1L << 15) | (1L << 17) | (7L << 18) | (1L << 30) | (1L << 38)
        | (1L << 40) | (1L << 41) | (1L << 47) | (1L << 48);
    private const long ScreenFeatures = (1L << 7) | (1L << 14) | (1L << 27);
    internal const string AirPlayService = "_airplay._tcp.local";
    internal const string RaopService = "_raop._tcp.local";
    internal const string Version = "366.0";
    internal string Name { get; } = NormalizeName(name) is { Length: > 0 } normalized ? normalized : "SonicRoute";
    internal string Id { get; } = id ?? CreateId();
    private string DisplayId => new Guid("00000000-0000-0000-0000-" + Id).ToString();
    internal string DeviceId => string.Join(':', Enumerable.Range(0, 6).Select(i => Id.Substring(i * 2, 2)));
    internal string Host => $"sonicroute-{Id.ToLowerInvariant()}.local";
    private long Features => AudioFeatures | (screenKey == null ? 0 : ScreenFeatures | (hevc ? 1L << 42 : 0));
    private string FeatureText => $"0x{Features & uint.MaxValue:X},0x{Features >> 32:X}";
    private string PasswordFlag => requireCode ? "pw=true" : "pw=false";

    internal static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var result = new StringBuilder(NameByteLimit);
        int bytes = 0;
        foreach (Rune rune in name.AsSpan().Trim().EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            bytes += rune.Utf8SequenceLength;
            if (bytes > NameByteLimit) break;
            result.Append(rune.ToString());
        }
        return result.ToString().Trim();
    }

    internal static string CreateId()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(6);
        bytes[0] = (byte)((bytes[0] | 2) & ~1);
        return Convert.ToHexString(bytes);
    }

    internal string[] AirPlayTxt => [$"deviceid={DeviceId}", $"features={FeatureText}", "flags=0x4", PasswordFlag,
        "acl=0", "model=SonicRoute", $"srcvers={Version}", "vv=2", "protovers=1.1",
        .. (screenKey == null ? Array.Empty<string>() : new[] { $"pk={Convert.ToHexStringLower(screenKey)}", $"pi={DisplayId}" })];
    internal string[] RaopTxt => [$"ft={FeatureText}", requireCode ? "sf=0x8c" : "sf=0x4", $"vs={Version}", "am=SonicRoute",
        "cn=0,1,2,3", "md=0,1", "ch=2", "sr=44100", "ss=16", "tp=UDP,TCP", screenKey == null ? "et=4" : "et=3,4,5",
        PasswordFlag, "txtvers=1", "vn=3"];

    internal byte[] Info()
    {
        var info = new Dictionary<string, object?>
        {
            ["name"] = Name, ["manufacturer"] = "SonicRoute", ["model"] = "SonicRoute", ["deviceID"] = DeviceId,
            ["macAddress"] = DeviceId, ["features"] = Features, ["statusFlags"] = 4, ["protocolVersion"] = "1.1",
            ["sourceVersion"] = Version, ["keepAliveLowPower"] = true, ["initialVolume"] = 0.0,
            ["txtAirPlay"] = AirPlayDiscovery.Txt(AirPlayTxt), ["txtRAOP"] = AirPlayDiscovery.Txt(RaopTxt),
            ["audioFormats"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = 96, ["audioInputFormats"] = 0, ["audioOutputFormats"] = AirPlayFormat.OutputFormats | AirPlayFormat.AacFormats | AirPlayFormat.EldFormats },
                new Dictionary<string, object?> { ["type"] = 103, ["audioInputFormats"] = 0, ["audioOutputFormats"] = AirPlayFormat.AacFormats | AirPlayFormat.OutputFormats }
            }
        };
        if (screenKey != null)
        {
            info["pk"] = screenKey;
            info["pi"] = DisplayId;
            info["displays"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["uuid"] = DisplayId, ["width"] = 1920, ["height"] = 1080,
                    ["widthPixels"] = 1920, ["heightPixels"] = 1080, ["widthPhysical"] = 0, ["heightPhysical"] = 0,
                    ["rotation"] = false, ["maxFPS"] = 60, ["refreshRate"] = 1.0 / 60,
                    ["overscanned"] = false, ["features"] = 14
                }
            };
        }
        return AirPlayPlist.Write(info);
    }
}

internal sealed class AirPlayDiscovery : IAsyncDisposable
{
    private const string Browse = "_services._dns-sd._udp.local";
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint Multicast = new(Group, 5353);
    internal sealed record Lan(IPAddress Address, IPAddress Mask, int Index)
    {
        internal bool Includes(IPAddress other) => other.AddressFamily == AddressFamily.InterNetwork
            && ((BinaryPrimitives.ReadUInt32BigEndian(Address.GetAddressBytes())
            ^ BinaryPrimitives.ReadUInt32BigEndian(other.GetAddressBytes()))
            & BinaryPrimitives.ReadUInt32BigEndian(Mask.GetAddressBytes())) == 0;
    }

    internal static Lan[] ReadInterfaces()
    {
        var interfaces = new List<Lan>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var address = properties.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                    && a.IPv4Mask is { } mask && !mask.Equals(IPAddress.Any));
                if (address != null && properties.GetIPv4Properties() is { } ipv4)
                    interfaces.Add(new Lan(address.Address, address.IPv4Mask!, ipv4.Index));
            }
            catch (NetworkInformationException ex)
            {
                // An adapter can disappear between enumerating it and reading its IP configuration.
                System.Diagnostics.Trace.WriteLine($"AirPlay skipped an adapter ({ex.NativeErrorCode}).");
            }
        }
        return interfaces.DistinctBy(l => l.Index).ToArray();
    }

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listen;
    private readonly (Lan Lan, byte[] Reply, byte[] Goodbye)[] _interfaces;
    private readonly HashSet<string> _names;

    internal AirPlayDiscovery(AirPlayIdentity identity, int port, Lan[] interfaces)
    {
        _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Browse, AirPlayIdentity.AirPlayService, AirPlayIdentity.RaopService, identity.Host,
            $"{identity.Name}.{AirPlayIdentity.AirPlayService}", $"{identity.Id}@{identity.Name}.{AirPlayIdentity.RaopService}"
        };
        try
        {
            _socket.ExclusiveAddressUse = false;
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            _socket.Ttl = 255;
            _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            _socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
            var announced = new List<(Lan Lan, byte[] Reply, byte[] Goodbye)>(interfaces.Length);
            SocketException? lastError = null;
            foreach (var lan in interfaces)
            {
                try
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, lan.Address));
                    var reply = WriteResponse(identity, lan.Address, port, 120);
                    Send(reply, lan, Multicast);
                    announced.Add((lan, reply, WriteResponse(identity, lan.Address, port, 0)));
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                    System.Diagnostics.Trace.WriteLine($"AirPlay skipped an adapter ({ex.NativeErrorCode}).");
                }
            }
            if (announced.Count == 0) throw lastError ?? new SocketException((int)SocketError.NetworkUnreachable);
            _interfaces = announced.ToArray();
            _listen = ListenAsync(_stop.Token);
        }
        catch { _socket.Dispose(); _stop.Dispose(); throw; }
    }

    private void Send(byte[] packet, Lan lan, IPEndPoint to)
    {
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, lan.Address.GetAddressBytes());
        _socket.SendTo(packet, to);
    }

    private async Task ListenAsync(CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var read = await _socket.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, remote, token).ConfigureAwait(false);
                var sender = (IPEndPoint)read.RemoteEndPoint;
                var active = _interfaces.FirstOrDefault(it => it.Lan.Index == read.PacketInformation.Interface && it.Lan.Includes(sender.Address));
                if (active.Lan == null || !TryMatchQuery(buffer.AsSpan(0, read.ReceivedBytes), _names, out bool unicast)) continue;
                // Apple senders use normal mDNS queries from port 5353. QU replies stay unicast.
                if (sender.Port != 5353) continue;
                // One disconnected adapter must not stop replies on the other interfaces.
                try { Send(active.Reply, active.Lan, unicast ? sender : Multicast); }
                catch (SocketException) { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset) { }
            catch (SocketException) { return; } // Adapter may have disappeared; re-enable to refresh the network.
        }
    }

    internal static bool TryMatchQuery(ReadOnlySpan<byte> data, HashSet<string> names, out bool unicast)
    {
        unicast = false;
        if (data.Length < 12 || (data[2] & 0xf8) != 0) return false;
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        if (count is < 1 or > 32) return false;
        int position = 12;
        bool matched = false, multicast = false;
        for (int i = 0; i < count; ++i)
        {
            string? name = ReadName(data, ref position);
            if (name == null || position > data.Length - 4) return false;
            int type = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            int mode = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            position += 4;
            if ((mode & 0x7fff) != 1 || type is not (1 or 12 or 16 or 33 or 255) || !names.Contains(name)) continue;
            matched = true;
            multicast |= (mode & 0x8000) == 0;
        }
        unicast = matched && !multicast;
        return matched;
    }

    private static string? ReadName(ReadOnlySpan<byte> data, ref int position)
    {
        int index = position, end = -1;
        var name = new StringBuilder();
        for (int labels = 0; labels < 40 && index < data.Length; ++labels)
        {
            int size = data[index++];
            if (size == 0)
            {
                position = end < 0 ? index : end;
                return name.ToString();
            }
            if ((size & 0xc0) == 0xc0 && index < data.Length)
            {
                int target = ((size & 63) << 8) | data[index++];
                if (target >= index - 2) return null;
                if (end < 0) end = index;
                index = target;
                continue;
            }
            if (size > 63 || size > data.Length - index || name.Length + size > 253) return null;
            if (name.Length != 0) name.Append('.');
            name.Append(Encoding.UTF8.GetString(data.Slice(index, size)));
            index += size;
        }
        return null;
    }

    internal static byte[] WriteResponse(AirPlayIdentity identity, IPAddress address, int port, uint ttl)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 9, 0, 0, 0, 0 });
        byte[] browse = Labels(Browse), host = Labels(identity.Host);
        WriteRecord(output, browse, 12, ttl, Labels(AirPlayIdentity.AirPlayService));
        WriteRecord(output, browse, 12, ttl, Labels(AirPlayIdentity.RaopService));
        foreach (var (service, instance, txt) in new[]
        {
            (AirPlayIdentity.AirPlayService, identity.Name, identity.AirPlayTxt),
            (AirPlayIdentity.RaopService, $"{identity.Id}@{identity.Name}", identity.RaopTxt)
        })
        {
            byte[] serviceName = Labels(service);
            byte[] name = [.. Txt([instance]), .. serviceName];
            WriteRecord(output, serviceName, 12, ttl, name);
            byte[] srv = [0, 0, 0, 0, (byte)(port >> 8), (byte)port, .. host];
            WriteRecord(output, name, 33, ttl, srv);
            WriteRecord(output, name, 16, ttl, Txt(txt));
        }
        WriteRecord(output, host, 1, ttl, address.GetAddressBytes());
        return output.ToArray();
    }

    private static void WriteRecord(Stream to, byte[] name, ushort type, uint ttl, byte[] payload)
    {
        Span<byte> header = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(header, type);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], type == 12 ? (ushort)1 : (ushort)0x8001);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], ttl);
        BinaryPrimitives.WriteUInt16BigEndian(header[8..], (ushort)payload.Length);
        to.Write(name); to.Write(header); to.Write(payload);
    }

    private static byte[] Labels(string name) => [.. Txt(name.Split('.')), 0];
    internal static byte[] Txt(string[] values)
    {
        using var output = new MemoryStream();
        foreach (string text in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            output.WriteByte(checked((byte)bytes.Length));
            output.Write(bytes);
        }
        return output.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _listen.ConfigureAwait(false); }
        finally
        {
            foreach (var item in _interfaces)
                try { Send(item.Goodbye, item.Lan, Multicast); } catch (SocketException) { }
            _socket.Dispose();
            _stop.Dispose();
        }
    }
}
