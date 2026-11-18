using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task MdnsDiscoveryChecksAsync(bool lan)
    {
        const string type = "_adb-tls-connect._tcp";
        const string name = "adb-saved-ABC123";
        const string target = "phone.local";
        const string browseName = type + ".local";
        const string instance = name + "." + browseName;
        const ushort port = 41183;
        var browse = AndroidMdnsDiscovery.BuildQuery(type, null);
        Check(browse.AsSpan(browse.Length - 4).SequenceEqual(new byte[] { 0, 12, 0, 1 }),
            "an ephemeral socket uses standard IN class for the mDNS connection PTR query");
        const string dottedName = "adb-fixture.1-ABC123";
        Check(AndroidMdnsDiscovery.BuildQuery(type, dottedName)[12] == dottedName.Length,
            "a DNS-SD instance containing a dot remains one label in the SRV query");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var relay = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        relay.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        AndroidMdnsService? discovered = null;
        async Task ReplyAsync()
        {
            byte[] buffer = new byte[512];
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var question = await server.ReceiveFromAsync(buffer, SocketFlags.None, remote, deadline.Token);
            Check(buffer.AsSpan(0, question.ReceivedBytes).SequenceEqual(browse), "the native socket sends the connection browse query");
            await relay.SendToAsync(DnsReply((browseName, 12, DnsName(instance))), SocketFlags.None, question.RemoteEndPoint, deadline.Token);
            question = await server.ReceiveFromAsync(buffer, SocketFlags.None, remote, deadline.Token);
            Check(buffer.AsSpan(0, question.ReceivedBytes).SequenceEqual(AndroidMdnsDiscovery.BuildQuery(type, name)),
                "a PTR-only reply is followed by a query for its SRV record");
            await relay.SendToAsync(DnsReply((instance, 33, DnsServiceTarget(target, port)),
                ("other.local", 1, [192, 0, 2, 99]), (target, 1, [192, 0, 2, 23])),
                SocketFlags.None, question.RemoteEndPoint, deadline.Token);
        }
        var reply = ReplyAsync();
        await Task.WhenAll(reply, AndroidMdnsDiscovery.QueryServicesAsync(client, (IPEndPoint)server.LocalEndPoint!,
            [browse], [type], (service, _) => { discovered = service; return true; }, deadline.Token));
        Check(discovered == new AndroidMdnsService(name, type, "192.0.2.23:41183"),
            "the result uses the SRV target's host record and port across two relayed UDP replies");

        var ipv6 = AndroidMdnsDiscovery.ParseResponse(DnsReply((instance, 33, DnsServiceTarget(target, port)),
            (target, 28, IPAddress.Parse("fe80::23").GetAddressBytes())), IPAddress.Parse("fe80::1%9"));
        Check(ipv6.Single().Address == "[fe80::23%9]:41183", "a link-local IPv6 result retains its adapter scope and address brackets");

        using var expired = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        expired.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string oldAddress = expired.LocalEndPoint!.ToString()!;
        string newAddress = listener.LocalEndpoint.ToString()!;
        var savedPhone = new AndroidDevice(oldAddress, "Saved phone", "offline", true, "fixture-phone");
        var oldCandidate = new AndroidSavedConnection(savedPhone.Name, savedPhone, oldAddress);
        var newCandidate = oldCandidate with { Address = newAddress };
        var available = await AndroidWirelessConnection.VerifySavedConnectionsAsync([oldCandidate, newCandidate], deadline.Token);
        Check(available.SequenceEqual([newCandidate]), "saved discovery discards a stale port in favor of the reachable port");
        available = await AndroidWirelessConnection.VerifySavedConnectionsAsync([oldCandidate], deadline.Token);
        Check(available.Count == 0, "a stale announcement alone cannot appear as an available phone");

        using var movedListener = new TcpListener(IPAddress.Parse("127.0.0.2"), 0);
        movedListener.Start();
        string reusedAddress = newAddress;
        string movedAddress = movedListener.LocalEndpoint.ToString()!;
        var attempts = new List<string>();
        var staleTransport = savedPhone with { State = "device", TransportId = "stale" };
        IReadOnlyList<AndroidDevice> transports = [staleTransport];
        var wireless = new AndroidWirelessConnection((token, args) =>
        {
            token.ThrowIfCancellationRequested();
            if (args[0] != "connect") throw new InvalidOperationException("Unexpected command: " + args[0]);
            string endpoint = args[1];
            attempts.Add(endpoint);
            transports = endpoint == movedAddress
                ? [staleTransport, savedPhone with { Serial = movedAddress, State = "device" }]
                : [staleTransport, new(endpoint, "Other phone", "device", true, "other-phone")];
            return Task.FromResult("connected to " + endpoint);
        }, NoConnectionDelay);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        Func<CancellationToken, Task<IReadOnlyList<AndroidDevice>>> read = _ => Task.FromResult(transports);
        typeof(AndroidWirelessConnection).GetField("_devices", fields)!.SetValue(wireless, read);
        Func<CancellationToken, Task<IReadOnlyList<AndroidMdnsService>>> discover = _ =>
            Task.FromResult<IReadOnlyList<AndroidMdnsService>>([
                new("adb-fixture-phone-OLD123", type, oldAddress),
                new("adb-other-phone-ABC123", type, "192.0.2.31:41009"),
                new("adb-fixture-phone-WRONG", type, reusedAddress),
                new("adb-fixture-phone-NEW123", type, movedAddress)]);
        typeof(AndroidWirelessConnection).GetField("_browseConnections", fields)!.SetValue(wireless, discover);
        var reconnected = await wireless.ReconnectAsync(savedPhone, deadline.Token);
        Check(reconnected.Device.Serial == movedAddress && reconnected.Device.HardwareId == savedPhone.HardwareId
            && attempts.SequenceEqual([reusedAddress, movedAddress]) && !reconnected.Devices.Contains(staleTransport),
            "reconnect filters both an expired port and its stale ready transport before adopting the correct new IP");
        attempts.Clear();
        var reused = await wireless.ReconnectAsync(savedPhone, deadline.Token);
        Check(reused.Device.Serial == movedAddress && attempts.Count == 0,
            "a verified live connection takes precedence over the saved port and does not open another ADB connection");

        if (lan)
        {
            var services = await AndroidMdnsDiscovery.DiscoverConnectionsAsync(default);
            Console.WriteLine($"LAN announcements={services.Count} identities={services.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
        }
        Console.WriteLine($"PASS {_checks} targeted mDNS checks");
    }

    private static byte[] DnsReply(params (string Owner, ushort Type, byte[] Data)[] records)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, (byte)records.Length, 0, 0, 0, 0 });
        Span<byte> header = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 10);
        foreach (var (owner, type, data) in records)
        {
            stream.Write(DnsName(owner));
            BinaryPrimitives.WriteUInt16BigEndian(header, type);
            BinaryPrimitives.WriteUInt16BigEndian(header[8..], (ushort)data.Length);
            stream.Write(header);
            stream.Write(data);
        }
        return stream.ToArray();
    }

    private static byte[] DnsName(string name)
    {
        using var stream = new MemoryStream();
        foreach (string label in name.Split('.'))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }
        stream.WriteByte(0);
        return stream.ToArray();
    }

    private static byte[] DnsServiceTarget(string host, ushort port)
    {
        byte[] target = DnsName(host);
        byte[] data = new byte[6 + target.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), port);
        target.CopyTo(data, 6);
        return data;
    }
}
