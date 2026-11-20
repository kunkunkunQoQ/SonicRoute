using System.Security.Authentication;
using System.Net;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WindowsLinkChecksAsync()
    {
        string scratch = Environment.GetEnvironmentVariable("SONICROUTE_WINDOWS_LINK_CHECK_DIRECTORY") ?? Path.GetTempPath();
        string root = Path.GetFullPath(Path.Combine(scratch, "sonicroute-windows-link-" + Guid.NewGuid().ToString("N")));
        try
        {
            await RunWindowsLinkChecksAsync(root);
            await RunWindowsLinkDiscoveryChecksAsync(root);
            Console.WriteLine($"PASS {_checks} Windows Link checks");
        }
        finally
        {
            foreach (string node in new[] { "node-a", "node-b", "discovery-a", "discovery-b" })
            {
                string directory = Path.Combine(root, node);
                if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                File.Delete(Path.Combine(directory, "identity.dpapi"));
                File.Delete(Path.Combine(directory, "trusted.json"));
                Directory.Delete(directory);
            }
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    private static async Task RunWindowsLinkChecksAsync(string root)
    {
        string stateA = Path.Combine(root, "node-a");
        string stateB = Path.Combine(root, "node-b");
        Directory.CreateDirectory(stateA);
        Directory.CreateDirectory(stateB);

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool captureB = false;
        bool mutedSpare = true;
        bool spareProbeFailed = false;
        int probesB = 0;
        await using var a = new WindowsLinkService(stateA, "Node A", port: 0, discovery: false)
            { ProbeAudioCapture = _ => true };
        await using var b = new WindowsLinkService(stateB, "Node B", port: 0, discovery: false,
            localCatalogProvider: (_, _) => Task.FromResult(new[] { new LinkRemoteApp("music", "Music", "music", 82, true, false, 0, null, null) }),
            acquireLocalSilence: _ => null)
        {
            ProbeAudioCapture = _ => { Interlocked.Increment(ref probesB); return captureB; },
            ProbeExclusiveCapture = () => spareProbeFailed
                ? throw new InvalidOperationException("The secondary output was removed during enumeration.") : mutedSpare,
            ProbeSpeakerFallback = () => true,
            AcquireSpeakerFallback = _ => throw new InvalidOperationException("This catalog check must never change speaker mute.")
        };

        var rejectA = new TaskCompletionSource<LinkPairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectB = new TaskCompletionSource<LinkPairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<LinkPairing> onPairA = p => rejectA.TrySetResult(p);
        Action<LinkPairing> onPairB = p => rejectB.TrySetResult(p);
        a.PairingRequested += p => onPairA(p);
        b.PairingRequested += p => onPairB(p);

        await a.EnableAsync(safety.Token);
        await b.EnableAsync(safety.Token);
        Check(a.LocalAddress.StartsWith("127.0.0.1:", StringComparison.Ordinal)
            && b.LocalAddress.StartsWith("127.0.0.1:", StringComparison.Ordinal)
            && a.LocalAddress != b.LocalAddress
            && a.Peers.Length == 0 && b.Peers.Length == 0,
            "loopback nodes bind isolated ephemeral addresses without saved peers");

        // 1. Declining pairing on either side leaves neither peer saved or connected.
        Task rejectedConnect = a.ConnectAsync(b.LocalAddress, token: safety.Token);
        LinkPairing rejectReqA = await rejectA.Task.WaitAsync(safety.Token);
        LinkPairing rejectReqB = await rejectB.Task.WaitAsync(safety.Token);
        Check(IsEightDigits(rejectReqA.Code) && rejectReqA.Code == rejectReqB.Code
            && rejectReqA.Name == "Node B" && rejectReqB.Name == "Node A",
            "rejected pairing still presents matching 8-digit comparison codes to both nodes");
        a.AcceptPairing(rejectReqA.Id, true);
        b.AcceptPairing(rejectReqB.Id, false);
        bool declined = false;
        try { await rejectedConnect.WaitAsync(safety.Token); }
        catch (Exception ex) when (ex is AuthenticationException or IOException) { declined = true; }
        Check(declined && a.Peers.Length == 0 && b.Peers.Length == 0,
            "declining pairing fails authentication and saves trust on neither node");
        await Task.Delay(25, safety.Token);

        // 2. Mutual pairing: matching code, saved only after both nodes confirm.
        var acceptA = new TaskCompletionSource<LinkPairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptB = new TaskCompletionSource<LinkPairing>(TaskCreationOptions.RunContinuationsAsynchronously);
        onPairA = p => acceptA.TrySetResult(p);
        onPairB = p => acceptB.TrySetResult(p);

        Task pairConnect = Task.WhenAll(a.ConnectAsync(b.LocalAddress, token: safety.Token),
            b.ConnectAsync(a.LocalAddress, token: safety.Token));
        LinkPairing pairReqA = await acceptA.Task.WaitAsync(safety.Token);
        LinkPairing pairReqB = await acceptB.Task.WaitAsync(safety.Token);
        Check(IsEightDigits(pairReqA.Code) && pairReqA.Code == pairReqB.Code,
            "mutual pairing displays an identical 8-digit comparison code on both nodes");

        a.AcceptPairing(pairReqA.Id, true);
        await Task.Delay(100, safety.Token);
        Check(!pairConnect.IsCompleted && a.Peers.Length == 0 && b.Peers.Length == 0,
            "confirming on only one node neither completes pairing nor persists trust");

        b.AcceptPairing(pairReqB.Id, true);
        await pairConnect.WaitAsync(safety.Token);
        await WaitUntilAsync(() => a.Peers.Length == 1 && b.Peers.Length == 1
            && a.Peers[0].Connected && b.Peers[0].Connected, safety.Token);
        LinkPeer peerB = a.Peers[0];
        LinkPeer peerA = b.Peers[0];
        const LinkCapabilities send = LinkCapabilities.ProcessAudio | LinkCapabilities.SystemAudio;
        Check((peerA.Capabilities & send) == send && (peerB.Capabilities & (send | LinkCapabilities.DestinationOnly)) == 0
            && peerB.Capabilities.HasFlag(LinkCapabilities.AudioReceive) && probesB == 1,
            "a receive-only peer remains an audio destination and advertises no unavailable drag modes");
        Check(peerB.Trusted && peerB.Connected && peerB.Name == "Node B"
            && peerA.Trusted && peerA.Connected && peerA.Name == "Node A"
            && peerA.Id != peerB.Id && peerA.Id.Length == 64 && peerB.Id.Length == 64,
            "confirming on both nodes saves pinned peer trust and establishes TLS connection");

        // 3. Trust reuse across DisableAsync / EnableAsync without PairingRequested.
        int unexpectedPairings = 0;
        onPairA = p => { Interlocked.Increment(ref unexpectedPairings); a.AcceptPairing(p.Id, false); };
        onPairB = p => { Interlocked.Increment(ref unexpectedPairings); b.AcceptPairing(p.Id, false); };

        await a.DisableAsync();
        await b.DisableAsync();
        captureB = true;
        Check(a.Peers.Length == 1 && !a.Peers[0].Connected && a.Peers[0].Trusted
            && b.Peers.Length == 1 && !b.Peers[0].Connected && b.Peers[0].Trusted,
            "disabling drops active links while retaining trusted peers");

        await a.EnableAsync(safety.Token);
        await b.EnableAsync(safety.Token);
        await a.ConnectAsync(b.LocalAddress, peerB.Id, safety.Token);
        await WaitUntilAsync(() => a.Peers.Length == 1 && b.Peers.Length == 1
            && a.Peers[0].Connected && b.Peers[0].Connected, safety.Token);
        Check(Volatile.Read(ref unexpectedPairings) == 0
            && a.Peers[0].Trusted && a.Peers[0].Connected
            && b.Peers[0].Trusted && b.Peers[0].Connected,
            "trusted peers reconnect over mutual TLS without re-prompting for pairing");
        Check(probesB == 2 && (a.Peers[0].Capabilities & (send | LinkCapabilities.DestinationOnly)) == (send | LinkCapabilities.DestinationOnly),
            "reconnecting replaces saved receive-only flags with the newly detected sending and exclusive playback modes");
        mutedSpare = false;
        b.RefreshExclusiveCapability(force: true);
        var onlyShared = (await a.RequestCatalogAsync(peerB.Id, false, safety.Token, audioOnly: true, refresh: true)).Single();
        Check(!onlyShared.SupportsExclusivePlayback(a.Peers[0].Capabilities) && !b.CanSendExclusively,
            "the live audio catalog overrides previously advertised exclusive support when the spare output becomes unavailable");
        Check(onlyShared.CanMuteSourceSpeaker && b.CanUseSpeakerFallback,
            "the live catalog reports shared-speaker muting separately from application isolation");
        spareProbeFailed = true;
        b.RefreshExclusiveCapability(force: true);
        Check(b.CanUseSpeakerFallback, "a failed spare-output probe still permits playback through the source speaker");
        spareProbeFailed = false;
        bool requiresOptIn = false;
        try { await a.RequestPullAudioAsync(peerB.Id, onlyShared, safety.Token, destinationOnly: true); }
        catch (NotSupportedException) { requiresOptIn = true; }
        Check(requiresOptIn && a.Streams.Length == 0 && b.Streams.Length == 0,
            "destination-only pulls without explicit opt-in cannot mute a shared speaker");
        mutedSpare = true;
        b.RefreshExclusiveCapability(force: true);
        var exclusive = (await a.RequestCatalogAsync(peerB.Id, false, safety.Token, audioOnly: true, refresh: true)).Single();
        Check(exclusive.SupportsExclusivePlayback(LinkCapabilities.None) && exclusive.CanMuteSourceSpeaker,
            "a spare output and an explicitly permitted speaker fallback remain available together");

        // 4. Simultaneous bidirectional connect converges to one connection without duplicates.
        await a.DisableAsync();
        await b.DisableAsync();
        await a.EnableAsync(safety.Token);
        await b.EnableAsync(safety.Token);
        await Task.WhenAll(
            a.ConnectAsync(b.LocalAddress, peerB.Id, safety.Token),
            b.ConnectAsync(a.LocalAddress, peerA.Id, safety.Token));
        await WaitUntilAsync(() => a.Peers.Length == 1 && b.Peers.Length == 1
            && a.Peers[0].Connected && b.Peers[0].Connected, safety.Token);
        await Task.Delay(100, safety.Token);
        Check(Volatile.Read(ref unexpectedPairings) == 0
            && a.Peers.Length == 1 && a.Peers[0].Connected && a.Peers[0].Id == peerB.Id
            && b.Peers.Length == 1 && b.Peers[0].Connected && b.Peers[0].Id == peerA.Id,
            "simultaneous bidirectional dial yields one connected peer per node without duplicates");

        // 5. Explicit disconnect closes both sides and does not auto-reconnect.
        await a.DisconnectAsync(peerB.Id);
        await WaitUntilAsync(() => a.Peers.Length == 1 && b.Peers.Length == 1
            && !a.Peers[0].Connected && !b.Peers[0].Connected, safety.Token);
        await Task.Delay(150, safety.Token);
        Check(!a.Peers[0].Connected && a.Peers[0].Trusted
            && !b.Peers[0].Connected && b.Peers[0].Trusted,
            "explicit disconnect closes both sides without auto-reconnecting or clearing trust");

        await b.ReconnectPeerAsync(peerA.Id, safety.Token);
        await WaitUntilAsync(() => a.Peers[0].Connected && b.Peers[0].Connected, safety.Token);
        await b.DisconnectAsync(peerA.Id);
        await WaitUntilAsync(() => !a.Peers[0].Connected && !b.Peers[0].Connected, safety.Token);
        await a.ReconnectPeerAsync(peerB.Id, safety.Token);
        await WaitUntilAsync(() => a.Peers[0].Connected && b.Peers[0].Connected, safety.Token);
        Check(Volatile.Read(ref unexpectedPairings) == 0,
            "either PC can manually reconnect a paired peer after the other PC disconnects");

        using var identity = LinkSecurity.LoadIdentity(stateA);
        int aPort = IPEndPoint.Parse(a.LocalAddress).Port;
        byte[] reconnect = LinkSecurity.CreateReconnectRequest(identity, peerB.Id, aPort);
        bool IsTrusted(string id) => id == peerA.Id;
        Check(LinkSecurity.TryReadReconnectRequest(reconnect, peerB.Id, IPAddress.Loopback, IsTrusted, out var rejoin)
            && rejoin?.Id == peerA.Id && rejoin.Endpoint.Port == aPort
            && !LinkSecurity.TryReadReconnectRequest(reconnect, peerA.Id, IPAddress.Loopback, IsTrusted, out _)
            && !LinkSecurity.TryReadReconnectRequest(reconnect, peerB.Id, IPAddress.Loopback, _ => false, out _),
            "reverse reconnect accepts a signed request only for its intended paired recipient");
        reconnect[6] ^= 1;
        Check(!LinkSecurity.TryReadReconnectRequest(reconnect, peerB.Id, IPAddress.Loopback, IsTrusted, out _),
            "reverse reconnect rejects a modified TCP port");
        reconnect[6] ^= 1;

        // Deliver the datagram to the receive callback without sharing the live app's discovery port.
        var privateInstance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var reverseHandler = typeof(WindowsLinkService).GetMethod("HandleReconnectRequest", privateInstance)!;
        object? activeRun = typeof(WindowsLinkService).GetField("_run", privateInstance)!.GetValue(b);
        void DeliverReverseRequest() => reverseHandler.Invoke(b,
            [activeRun, peerB.Id, new ReadOnlyMemory<byte>(reconnect), IPAddress.Loopback]);
        await a.DisconnectAsync(peerB.Id);
        await WaitUntilAsync(() => !a.Peers[0].Connected && !b.Peers[0].Connected, safety.Token);
        DeliverReverseRequest();
        await WaitUntilAsync(() => a.Peers[0].Connected && b.Peers[0].Connected, safety.Token);
        Check(Volatile.Read(ref unexpectedPairings) == 0,
            "a trusted reverse request makes the recipient reconnect after an explicit disconnect");

        // 6. Rename persists alias and Remove clears trust and drops active link.
        a.Rename(peerB.Id, "Studio B");
        Check(a.Peers[0].Alias == "Studio B" && a.Peers[0].DisplayName == "Studio B",
            "rename updates peer alias and display name");
        await a.DisableAsync();
        await b.DisableAsync();
        await using (var restored = new WindowsLinkService(stateA, "Node A", port: 0, discovery: false))
        {
            restored.RestoreSavedPeers();
            Check(!restored.IsEnabled && restored.LocalAddress.Length == 0
                && restored.Peers is [{ Trusted: true, Connected: false, DisplayName: "Studio B" }],
                "a new service restores the renamed device from disk before enabling the listener");
        }
        await a.EnableAsync(safety.Token);
        await b.EnableAsync(safety.Token);
        Check(a.Peers[0].Alias == "Studio B" && a.Peers[0].DisplayName == "Studio B",
            "renamed peer alias persists across restart");

        await a.ConnectAsync(b.LocalAddress, peerB.Id, safety.Token);
        await WaitUntilAsync(() => a.Peers[0].Connected && b.Peers[0].Connected, safety.Token);
        a.Remove(peerB.Id);
        await WaitUntilAsync(() => a.Peers.Length == 0 && b.Peers.Length == 1 && !b.Peers[0].Connected, safety.Token);
        Check(a.Peers.Length == 0 && !b.Peers[0].Connected,
            "remove deletes saved peer locally and tears down the active link");
        b.Remove(peerA.Id);
        Check(b.Peers.Length == 0, "removing peer on the other node clears its saved entry");
        await RunWindowsLinkRedesignChecksAsync(root, safety.Token);
    }

    private static bool IsEightDigits(string code) => code.Length == 8 && code.All(char.IsAsciiDigit);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(15, deadline.Token);
    }
}
