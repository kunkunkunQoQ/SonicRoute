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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(duration);
        IPAddress[] addresses;
        try { addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct().Take(8).ToArray(); }
        catch (NetworkInformationException) { return null; }
        var work = addresses.Select(a => QueryAsync(a, type, name, host, guid, lifetime.Token)).ToList();
        try
        {
            while (work.Count > 0)
            {
                var completed = await Task.WhenAny(work).ConfigureAwait(false); work.Remove(completed);
                if (await completed.ConfigureAwait(false) is { } found) return found;
            }
            token.ThrowIfCancellationRequested(); return null;
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(work).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
    private static async Task<AndroidMdnsService?> QueryAsync(IPAddress local, string type, string? name, string? host, string? guid, CancellationToken token)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(local, 0)); // Legacy unicast replies; no shared port, firewall change or daemon restart.
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            byte[] query = BuildQuery(type, name), buffer = new byte[4096];
            var endpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            Task<SocketReceiveFromResult>? receive = null;
            while (true)
            {
                await socket.SendToAsync(query, SocketFlags.None, endpoint, token).ConfigureAwait(false);
                var nextProbe = Task.Delay(TimeSpan.FromSeconds(3), token);
                while (!nextProbe.IsCompleted)
                {
                    receive ??= socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token).AsTask();
                    if (await Task.WhenAny(receive, nextProbe).ConfigureAwait(false) != receive) break;
                    var packet = await receive.ConfigureAwait(false); receive = null;
                    var source = ((IPEndPoint)packet.RemoteEndPoint).Address;
                    foreach (var service in ParseResponse(buffer.AsSpan(0, packet.ReceivedBytes), source))
                    {
                        if (!service.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
                        if (name != null && !AndroidWirelessConnection.MatchesPairingName(service.Name, name)) continue;
                        if (host != null && (!IPAddress.TryParse(host.Trim('[', ']'), out var expected) || !source.Equals(expected))) continue;
                        if (host == null && guid != null)
                        {
                            string prefix = guid.StartsWith("adb-", StringComparison.OrdinalIgnoreCase) ? guid : "adb-" + guid;
                            if (!service.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase) && !service.Name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        return service;
                    }
                }
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }
        catch (NetworkInformationException) { return null; }
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
