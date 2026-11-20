using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WindowsLinkAddressChecksAsync()
    {
        string root = Directory.CreateTempSubdirectory("sonicroute-link-address-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var token = deadline.Token;
        try
        {
            Check(LinkDirectAddress.TryParse("  STUDIO-PC  ", out var pc) && pc.Canonical == "studio-pc:46916"
                && LinkDirectAddress.TryParse("node.mesh.example:8443", out var mesh) && mesh.Port == 8443
                && LinkDirectAddress.TryParse("2001:db8::1", out var ipv6) && ipv6.Port == 46916
                && LinkDirectAddress.TryParse("[2001:db8::1]:8443", out ipv6) && ipv6.Canonical == "[2001:db8::1]:8443"
                && LinkDirectAddress.TryParse("100.64.15.4", out _), "hostnames, VPN IPv4 and bracketed or bare IPv6 use the requested or default port");
            Check(new[] { "", "https://host", "host:0", "[::1]:65536", "[host]:5", "[::1]junk", "a..b", "addr/path",
                "127.1", "999.2.0.1", "0.0.0.0", "::", "224.0.0.1", "ff02::1", "user@host", "host:12x", "foo bar" }
                .All(value => !LinkDirectAddress.TryParse(value, out _)), "invalid targets fail without opening a socket");

            string dirA = Path.Combine(root, "sender"), dirB = Path.Combine(root, "peer");
            await using var a = new WindowsLinkService(dirA, "Caller", port: 0, discovery: false);
            await using var b = new WindowsLinkService(dirB, "Peer", port: 0, discovery: false);
            int confirms = 0;
            a.PairingRequested += pair => { Interlocked.Increment(ref confirms); a.AcceptPairing(pair.Id, true); };
            b.PairingRequested += pair => { Interlocked.Increment(ref confirms); b.AcceptPairing(pair.Id, true); };
            await Task.WhenAll(a.EnableAsync(token), b.EnableAsync(token));
            var destination = IPEndPoint.Parse(b.LocalAddress);
            Check(b.GetLocalAddresses() is [var localOnly] && localOnly.Address == b.LocalAddress,
                "a single reachable local address can be copied directly without an adapter chooser");
            bool self = false;
            try { await a.ResolveDirectAddressAsync($"localhost:{IPEndPoint.Parse(a.LocalAddress).Port}", token); }
            catch (LinkSelfConnectionException) { self = true; }
            Check(self && a.Peers.Length == 0, "the service catches its own port and host before pairing");

            using var mapped = new TcpListener(IPAddress.Loopback, 0);
            mapped.Start();
            string address = $"localhost:{((IPEndPoint)mapped.LocalEndpoint).Port}";
            var forward = RelayOnceAsync(mapped, destination, token);
            await a.ConnectAsync(address, token: token);
            await WaitUntilAsync(() => a.Peers.Any(p => p.Connected) && b.Peers.Any(p => p.Connected), token);
            var peer = a.Peers.Single(p => p.Connected);
            string state = Path.Combine(dirA, "trusted.json");
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(state, token))![0]!;
            Check(confirms == 2 && (string?)saved["DirectAddress"] == address
                && IPEndPoint.Parse(peer.Address!).Port == ((IPEndPoint)mapped.LocalEndpoint).Port,
                "mutual pairing through DNS stores the typed host and the actual mapped port");
            Check((await a.ResolveDirectAddressAsync(address, token)).ConnectedPeer == peer.Id,
                "pasting an already-connected address identifies the live peer");
            await a.ConnectAsync(destination.ToString(), token: token);
            var persisted = JsonNode.Parse(await File.ReadAllTextAsync(state, token))![0]!;
            Check(a.Peers.Single().Connected && !forward.IsCompleted && (string?)persisted["DirectAddress"] == address,
                "connecting through another port for the same identity preserves the active connection and its original DNS route");
            await a.DisconnectAsync(peer.Id);
            await forward;
            await a.DisableAsync();

            saved["Address"] = "127.0.0.1:1";
            await File.WriteAllTextAsync(state, saved.Parent!.ToJsonString(), token);
            await using var restored = new WindowsLinkService(dirA, "Caller", port: 0, discovery: false);
            var reconnect = RelayOnceAsync(mapped, destination, token);
            await restored.ReconnectPeerAsync(peer.Id, token);
            Check(confirms == 2 && restored.Peers.Any(p => p.Connected && p.Trusted && p.Id == peer.Id),
                "restarting re-resolves the saved DNS name and reaches the same certificate behind the mapped port");
            await restored.DisconnectAsync(peer.Id);
            await reconnect;
            await b.DisableAsync();
            await using var wrong = new WindowsLinkService(Path.Combine(root, "other"), "Different device", port: 0, discovery: false);
            await wrong.EnableAsync(token);
            int wrongPrompts = 0;
            wrong.PairingRequested += _ => Interlocked.Increment(ref wrongPrompts);
            var wrongForward = RelayOnceAsync(mapped, IPEndPoint.Parse(wrong.LocalAddress), token);
            bool rejected = false;
            try { await restored.ConnectAsync(address, token: token); }
            catch (LinkConnectionFailureException ex) { rejected = ex.Reason == LinkConnectFailure.IdentityMismatch; }
            await wrongForward;
            Check(rejected && wrongPrompts == 0 && restored.Peers.All(p => p.Id == peer.Id && !p.Connected) && wrong.Peers.Length == 0,
                "a DNS name now leading to another certificate cannot silently pair with or replace the saved peer");

            if (Socket.OSSupportsIPv6)
            {
                using var directed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                await using var v6 = new WindowsLinkService(Path.Combine(root, "ipv6"), "IPv6 receiver", port: 0,
                    discoveryBind: new IPEndPoint(IPAddress.Loopback, 0), discoveryTarget: (IPEndPoint)directed.Client.LocalEndPoint!);
                v6.PairingRequested += pair => v6.AcceptPairing(pair.Id, true);
                restored.PairingRequested += pair => restored.AcceptPairing(pair.Id, true);
                await v6.EnableAsync(token);
                var choices = v6.GetLocalAddresses();
                Check(choices.Length >= 1 && choices[0].Address == v6.LocalAddress
                    && choices.Select(choice => choice.Address).Distinct().Count() == choices.Length
                    && choices.All(choice => IPEndPoint.Parse(choice.Address).Port == IPEndPoint.Parse(v6.LocalAddress).Port),
                    "the on-demand adapter menu lists distinct local addresses with the actual listen port");
                await restored.ConnectAsync($"[::1]:{IPEndPoint.Parse(v6.LocalAddress).Port}", token: token);
                Check(restored.Peers.Any(p => p.Connected && IPEndPoint.Parse(p.Address!).Address.Equals(IPAddress.IPv6Loopback)),
                    "the production dual-mode listener accepts an IPv6 TCP/TLS session");
            }
            string ambiguousDir = Directory.CreateDirectory(Path.Combine(root, "ambiguous")).FullName;
            var second = saved.DeepClone();
            second["Id"] = new string('F', 64);
            await File.WriteAllTextAsync(Path.Combine(ambiguousDir, "trusted.json"),
                new JsonArray(saved.DeepClone(), second).ToJsonString(), token);
            await using var ambiguous = new WindowsLinkService(ambiguousDir, "Offline fixture", port: 0, discovery: false);
            ambiguous.RestoreSavedPeers();
            bool conflict = false;
            try { await ambiguous.ResolveDirectAddressAsync(address, token); }
            catch (LinkAmbiguousAddressException) { conflict = true; }
            Check(conflict && !ambiguous.IsEnabled && ambiguous.Peers.Length == 2,
                "shared saved addresses request an explicit device selection before starting the service");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task RelayOnceAsync(TcpListener listener, IPEndPoint target, CancellationToken token)
    {
        using var incoming = await listener.AcceptTcpClientAsync(token);
        using var outgoing = new TcpClient(target.AddressFamily);
        await outgoing.ConnectAsync(target, token);
        var a = incoming.GetStream();
        var b = outgoing.GetStream();
        var forward = a.CopyToAsync(b, token);
        var reverse = b.CopyToAsync(a, token);
        await Task.WhenAny(forward, reverse);
        incoming.Close();
        outgoing.Close();
        try { await Task.WhenAll(forward, reverse); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}
