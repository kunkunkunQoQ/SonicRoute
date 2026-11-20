using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WaitForTrustChangeAsync(WindowsLinkService service, string peerId,
        Func<LinkFullTrustState, bool, bool> expected, CancellationToken token)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(string changedPeer)
        {
            if (changedPeer == peerId && expected(service.GetFullTrustState(peerId), service.IsFullyTrusted(peerId)))
                signal.TrySetResult();
        }
        service.FullTrustChanged += Changed;
        try { await signal.Task.WaitAsync(token); }
        finally { service.FullTrustChanged -= Changed; }
    }

    private static async Task WindowsLinkTrustChecksAsync(string? package)
    {
        string root = Directory.CreateTempSubdirectory("SonicRoute-dual-trust-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var token = timeout.Token;
        WindowsLinkService Node(string name) => new(Path.Combine(root, name), name, port: 0, discovery: false)
            { ProbeAudioCapture = _ => false };
        try
        {
            string received = Directory.CreateDirectory(Path.Combine(root, "inbox")).FullName;
            string source = package == null ? Path.Combine(root, "document.txt") : Path.GetFullPath(package);
            if (package == null) await File.WriteAllTextAsync(source, "SonicRoute mutual trust transfer", token);
            string idA, idB;
            await using (var a = Node("A"))
            await using (var b = Node("B"))
            {
                a.PairingRequested += p => a.AcceptPairing(p.Id, true);
                b.PairingRequested += p => b.AcceptPairing(p.Id, true);
                await Task.WhenAll(a.EnableAsync(token), b.EnableAsync(token));
                await a.ConnectAsync(b.LocalAddress, token: token);
                await WaitUntilAsync(() => a.Peers.Any(p => p.Connected) && b.Peers.Any(p => p.Connected), token);
                idA = b.Peers.Single().Id;
                idB = a.Peers.Single().Id;
                b.SetReceiveDirectory(received);
                bool BothTrusted() => a.IsFullyTrusted(idB) && b.IsFullyTrusted(idA);
                async Task<(string A, string B)> BeginBoth()
                {
                    await Task.WhenAll(a.BeginFullTrustAsync(idB, token), b.BeginFullTrustAsync(idA, token));
                    await WaitUntilAsync(() => a.GetFullTrustState(idB).PeerCodeReady && b.GetFullTrustState(idA).PeerCodeReady, token);
                    return (a.GetFullTrustState(idB).LocalCode!, b.GetFullTrustState(idA).LocalCode!);
                }

                Check(!a.IsFullyTrusted(idB) && !b.IsFullyTrusted(idA), "normal pairing does not grant full trust");
                var generated = WaitForTrustChangeAsync(a, idB, (s, _) => s.LocalCode != null, token);
                var invited = WaitForTrustChangeAsync(b, idA, (s, _) => s.PeerCodeReady, token);
                await a.BeginFullTrustAsync(idB, token);
                await Task.WhenAll(generated, invited);
                Check(true, "generating a code notifies both the local settings page and the invited peer");
                Check(b.GetFullTrustState(idA).LocalCode == null, "a remote request cannot generate a local code");
                var codes = await BeginBoth();
                string wrong = (codes.B[0] == '0' ? "1" : "0") + codes.B[1..];
                for (int i = 0; i < 3; i++) Check(!await a.SubmitFullTrustCodeAsync(idB, wrong, token), "wrong code stays unverified");
                await WaitUntilAsync(() => a.GetFullTrustState(idB).LocalCode == null && b.GetFullTrustState(idA).LocalCode == null, token);
                Check(!a.IsFullyTrusted(idB) && !b.IsFullyTrusted(idA), "three failed entries discard both challenges");

                codes = await BeginBoth();
                var submitted = WaitForTrustChangeAsync(a, idB, (s, active) => s.Submitted && !active, token);
                var verified = WaitForTrustChangeAsync(b, idA, (s, active) => s.PeerVerified && !active, token);
                Check(await a.SubmitFullTrustCodeAsync(idB, codes.B, token), "the correct peer code verifies this side");
                await Task.WhenAll(submitted, verified);
                Check(true, "one successful entry publishes the matching waiting status on each device");
                Check(!a.IsFullyTrusted(idB) && !b.IsFullyTrusted(idA), "one correct local entry grants neither side access");
                await a.DisconnectAsync(idB);
                await WaitUntilAsync(() => !a.Peers.Single().Connected && !b.Peers.Single().Connected, token);
                await a.ConnectAsync(b.LocalAddress, idB, token);
                await WaitUntilAsync(() => a.Peers.Single().Connected && b.Peers.Single().Connected, token);
                Check(a.GetFullTrustState(idB).LocalCode == null && b.GetFullTrustState(idA).LocalCode == null
                    && !await b.SubmitFullTrustCodeAsync(idA, codes.A, token), "an unfinished exchange is discarded on disconnect");

                codes = await BeginBoth();
                var activeA = WaitForTrustChangeAsync(a, idB, (s, active) => s.Enabled && active, token);
                var activeB = WaitForTrustChangeAsync(b, idA, (s, active) => s.Enabled && active, token);
                var accepted = await Task.WhenAll(a.SubmitFullTrustCodeAsync(idB, codes.B, token), b.SubmitFullTrustCodeAsync(idA, codes.A, token));
                await Task.WhenAll(activeA, activeB);
                Check(accepted.All(x => x), "simultaneous correct entries establish full trust on both PCs");
                Check(true, "both devices publish effective full trust after the last confirmation without polling");
                int prompts = 0;
                b.FileOffered += _ => Interlocked.Increment(ref prompts);
                await a.SendFileAsync(idB, source, token);
                using (var original = File.OpenRead(source))
                using (var saved = File.OpenRead(Path.Combine(received, Path.GetFileName(source))))
                {
                    var expected = System.Security.Cryptography.SHA256.HashData(original);
                    var actual = System.Security.Cryptography.SHA256.HashData(saved);
                    Check(prompts == 0 && actual.SequenceEqual(expected),
                        $"full trust saved {saved.Length:N0} bytes in the configured folder without a prompt, SHA-256 {Convert.ToHexString(actual)}");
                }
                Check(!b.GetPeerSettings(idA).AllowRemoteInput, "full trust preserves the separately configured remote-input preference");
                var revokedA = WaitForTrustChangeAsync(a, idB, (s, active) => !s.Enabled && !active, token);
                var revokedB = WaitForTrustChangeAsync(b, idA, (s, active) => !s.Enabled && !active, token);
                await b.RevokeFullTrustAsync(idA, token);
                await Task.WhenAll(revokedA, revokedB);
                Check(true, "an online revocation immediately notifies both devices without refreshing the catalog");
                codes = await BeginBoth();
                await Task.WhenAll(a.SubmitFullTrustCodeAsync(idB, codes.B, token), b.SubmitFullTrustCodeAsync(idA, codes.A, token));
                await WaitUntilAsync(BothTrusted, token);
                await a.DisconnectAsync(idB);
                await WaitUntilAsync(() => !a.Peers.Single().Connected && !b.Peers.Single().Connected, token);
            }

            // Use B's existing certificate and a hello without the FullTrust capability.
            // Its immutable identity is still paired, but this session cannot confirm the durable grant.
            await using (var current = Node("A"))
            using (var legacyIdentity = LinkSecurity.LoadIdentity(Path.Combine(root, "B")))
            using (var legacyClient = new TcpClient())
            {
                await current.EnableAsync(token);
                await legacyClient.ConnectAsync(IPEndPoint.Parse(current.LocalAddress), token);
                int fixturePort = ((IPEndPoint)legacyClient.Client.LocalEndPoint!).Port;
                var legacy = await LinkSecurity.AuthenticateAsync(legacyClient, false, legacyIdentity, idA, "B", fixturePort,
                    LinkCapabilities.FileTransfer, id => id == idA, (_, _) => false, (_, _, _, _) => Task.FromResult(false), false, token);
                using (legacy.Stream)
                {
                    await WaitUntilAsync(() => current.Peers.Any(p => p.Id == idB && p.Connected), token);
                    var state = current.GetFullTrustState(idB);
                    Check(state.Enabled && !state.Supported && !current.IsFullyTrusted(idB),
                        "the same paired identity on an older version suspends automatic permissions and preserves its grant");
                    await current.DisconnectAsync(idB);
                }
            }

            await using var againA = Node("A");
            await using var againB = Node("B");
            await Task.WhenAll(againA.EnableAsync(token), againB.EnableAsync(token));
            await againA.ConnectAsync(againB.LocalAddress, idB, token);
            await WaitUntilAsync(() => againA.IsFullyTrusted(idB) && againB.IsFullyTrusted(idA), token);
            Check(true, "both persisted grants resume over a fresh authenticated connection after restart");
            await againA.DisconnectAsync(idB);
            await WaitUntilAsync(() => !againA.Peers.Single().Connected && !againB.Peers.Single().Connected, token);
            await againB.RevokeFullTrustAsync(idA, token);
            await againA.ConnectAsync(againB.LocalAddress, idB, token);
            await WaitUntilAsync(() => againA.Peers.Single().Connected && againB.Peers.Single().Connected && !againA.GetFullTrustState(idB).Enabled, token);
            Check(!againA.IsFullyTrusted(idB) && !againB.IsFullyTrusted(idA), "offline revocation clears the other grant upon reconnect");
            var offers = Channel.CreateUnbounded<LinkFileOffer>();
            againB.FileOffered += o => offers.Writer.TryWrite(o);
            var send = againA.SendFileAsync(idB, source, token);
            var offer = await offers.Reader.ReadAsync(token);
            Check(!send.IsCompleted && Directory.GetFiles(received).Length == 1, "ordinary file confirmation returns after revocation");
            againB.RejectFile(offer.Id);
            try { await send; } catch (IOException) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
