using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task RunWindowsLinkDiscoveryChecksAsync(string root)
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = safety.Token;
        string dirA = Path.Combine(root, "discovery-a");
        string dirB = Path.Combine(root, "discovery-b");
        var changedIp = IPAddress.Parse("127.0.0.2");
        IPEndPoint udpA, udpB, movedUdp;
        using (var socketA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var socketB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var movedSocket = new UdpClient(new IPEndPoint(changedIp, 0)))
        {
            udpA = (IPEndPoint)socketA.Client.LocalEndPoint!;
            udpB = (IPEndPoint)socketB.Client.LocalEndPoint!;
            movedUdp = (IPEndPoint)movedSocket.Client.LocalEndPoint!;
        }

        var ttl = TimeSpan.FromSeconds(2.5);
        await using var a = new WindowsLinkService(dirA, "Discovery A", port: 0,
            offerLifetime: ttl, discoveryBind: udpA, discoveryTarget: udpB);
        await using var b = new WindowsLinkService(dirB, "Discovery B", port: 0,
            offerLifetime: ttl, discoveryBind: udpB, discoveryTarget: udpA);
        using (LinkSecurity.LoadIdentity(dirB)) { }

        int promptsA = 0, promptsB = 0;
        a.PairingRequested += request => { Interlocked.Increment(ref promptsA); a.AcceptPairing(request.Id, true); };
        b.PairingRequested += request => { Interlocked.Increment(ref promptsB); b.AcceptPairing(request.Id, true); };
        await a.EnableAsync(token);
        using var visiblePage = a.ObserveDiscovery();
        long started = Stopwatch.GetTimestamp();
        Task search = a.SearchAsync(token);
        using (var pageClose = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var second = a.SearchAsync(pageClose.Token);
            pageClose.Cancel();
            bool canceled = false;
            try { await second; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { canceled = true; }
            Check(canceled && !search.IsCompleted, "closing a search view cancels its wait while another view shares the discovery window");
        }

        await b.EnableAsync(token);
        await WaitUntilAsync(() => a.Peers is [{ State: LinkPeerState.Discovered, Online: true, Trusted: false }]
            && b.Peers is [{ State: LinkPeerState.Discovered, Online: true, Trusted: false }], token);
        await search;
        Check(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)
            && a.Peers[0].State == LinkPeerState.Discovered && b.Peers[0].State == LinkPeerState.Discovered,
            "directed UDP between isolated nodes discovers each peer and the search waits for responses before reporting results");

        string bId = a.Peers[0].Id;
        string aId = b.Peers[0].Id;
        await a.ConnectAsync(a.Peers[0].Address!, bId, token);
        await WaitUntilAsync(() => a.Peers[0].State == LinkPeerState.Connected && b.Peers[0].State == LinkPeerState.Connected, token);
        Check(promptsA == 1 && promptsB == 1, "selecting a discovered peer requires both pairing confirmations");

        string authenticatedAddress = a.Peers[0].Address!;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var run = typeof(WindowsLinkService).GetField("_run", hidden)!.GetValue(a);
        var found = typeof(WindowsLinkService).GetMethod("Found", hidden)!;
        var alternate = new LinkOffer(bId, "Discovery B", new IPEndPoint(changedIp, IPEndPoint.Parse(authenticatedAddress).Port),
            LinkCapabilities.RemoteBrowse);
        found.Invoke(a, [run, alternate]);
        found.Invoke(a, [run, alternate]);
        Check(a.Peers is [{ Connected: true, Trusted: true }] && a.Peers[0].Address == authenticatedAddress,
            "multiple advertisements for the same certificate preserve a single row and the live authenticated endpoint");

        a.Remove(bId);
        await WaitUntilAsync(() => a.Peers is [{ State: LinkPeerState.Discovered, Trusted: false, Connected: false }]
            && b.Peers is [{ State: LinkPeerState.DisconnectedByUser }], token);
        Check(a.Peers[0].Id == bId && promptsA == 1 && promptsB == 1,
            "removing a connected device rediscovers the same certificate over UDP without trusting or automatically dialing it");
        await a.ConnectAsync(a.Peers[0].Address!, bId, token);
        await WaitUntilAsync(() => a.Peers[0].Connected && b.Peers[0].Connected, token);
        Check(promptsA == 2 && promptsB == 2, "reconnecting an explicitly removed device requires mutual pairing again");

        await b.DisconnectAsync(aId);
        await WaitUntilAsync(() => a.Peers[0].State == LinkPeerState.DisconnectedByUser
            && b.Peers[0].State == LinkPeerState.DisconnectedByUser, token);
        await a.SearchAsync(token);
        Check(a.Peers[0].State == LinkPeerState.DisconnectedByUser && !b.Peers[0].Connected,
            "manual disconnect suppresses trusted auto reconnect even after a fresh discovery window");

        await b.DisableAsync();
        await using var moved = new WindowsLinkService(dirB, "Discovery B", port: 0,
            offerLifetime: ttl, discoveryBind: movedUdp, discoveryTarget: udpA);
        moved.PairingRequested += request => { Interlocked.Increment(ref promptsB); moved.AcceptPairing(request.Id, false); };
        await moved.EnableAsync(token);
        await a.SearchAsync(token);
        await a.ReconnectPeerAsync(bId, token);
        await WaitUntilAsync(() => a.Peers[0].Connected && moved.Peers[0].Connected, token);
        Check(IPEndPoint.Parse(a.Peers[0].Address!).Address.Equals(changedIp) && promptsA == 2 && promptsB == 2,
            "manual resume uses the newly advertised IP after the old TCP endpoint disappears and reuses certificate trust");

        await moved.DisableAsync();
        await WaitUntilAsync(() => !a.Peers[0].Connected, token);
        await moved.EnableAsync(token);
        a.OnNetworkResume();
        await WaitUntilAsync(() => a.Peers[0].Connected && moved.Peers[0].Connected, token);
        Check(promptsA == 2 && promptsB == 2, "trusted nodes recover automatically after a service and network-resume transition");

        await moved.DisableAsync();
        await WaitUntilAsync(() => a.Peers is [{ Trusted: true, Online: false, Connected: false }], token);
        Check(a.Peers[0].Id == bId, "advertisements age out while the trusted peer remains saved");

        string temporaryId = new('D', 64);
        int expiryEvents = 0;
        a.Changed += () =>
        {
            if (!a.Peers.Any(peer => peer.Id == temporaryId)) Interlocked.Increment(ref expiryEvents);
        };
        found.Invoke(a, [run, new LinkOffer(temporaryId, "Temporary fixture", udpB, LinkCapabilities.RemoteBrowse)]);
        Check(a.Peers.Any(peer => peer.Id == temporaryId), "unpaired announcement appears without a TCP connection");
        await WaitUntilAsync(() => Volatile.Read(ref expiryEvents) != 0 && a.Peers.All(peer => peer.Id != temporaryId), token);
        Check(a.Peers.All(peer => peer.Id != temporaryId), "expired unpaired entries are pruned and generate a UI update");

        Task closing = a.SearchAsync(token);
        await a.DisableAsync();
        bool stopped = false;
        try { await closing; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { stopped = true; }
        Check(stopped && !a.IsEnabled, "stopping the service cancels discovery and an active search with an unavailable partner");

        await RunWindowsLinkMultiWindowAndResilienceChecksAsync(root, token);
    }

    private static async Task RunWindowsLinkMultiWindowAndResilienceChecksAsync(string root, CancellationToken token)
    {
        string dirA = Path.Combine(root, "multi-a");
        string dirB = Path.Combine(root, "multi-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            int catalogCalls = 0;
            var windows = new[]
            {
                new LinkRemoteApp("sameproc", "Same Proc", "sameproc", 4321, false, true, 0x101, "Editor Window 1", null)
                    { HasAudioSession = false, InstanceId = "w:101:10e1:1:10" },
                new LinkRemoteApp("sameproc", "Same Proc", "sameproc", 4321, false, true, 0x102, "Editor Window 2", null)
                    { HasAudioSession = false, InstanceId = "w:102:10e1:2:10" }
            };

            IPEndPoint udpA, udpB;
            using (var socketA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            using (var socketB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                udpA = (IPEndPoint)socketA.Client.LocalEndPoint!;
                udpB = (IPEndPoint)socketB.Client.LocalEndPoint!;
            }

            await using var a = new WindowsLinkService(dirA, "Multi Host A", port: 0,
                offerLifetime: TimeSpan.FromSeconds(20), discoveryBind: udpA, discoveryTarget: udpB)
            {
                ScopedCatalogProvider = async (scope, _, ct) =>
                {
                    if (scope == LinkCatalogScope.Windows)
                    {
                        Interlocked.Increment(ref catalogCalls);
                        await Task.Delay(50, ct);
                        return windows;
                    }
                    return [];
                }
            };
            await using var b = new WindowsLinkService(dirB, "Multi Host B", port: 0,
                offerLifetime: TimeSpan.FromSeconds(20), discoveryBind: udpB, discoveryTarget: udpA);

            int promptsA = 0, promptsB = 0;
            a.PairingRequested += p => { Interlocked.Increment(ref promptsA); a.AcceptPairing(p.Id, true); };
            b.PairingRequested += p => { Interlocked.Increment(ref promptsB); b.AcceptPairing(p.Id, true); };

            await a.EnableAsync(token);
            await b.EnableAsync(token);
            await WaitUntilAsync(() => a.Peers.Any(p => p.Online) && b.Peers.Any(p => p.Online), token);

            string bId = a.Peers.Single().Id;
            string aId = b.Peers.Single().Id;
            await b.ConnectAsync(b.Peers.Single().Address!, aId, token);
            await WaitUntilAsync(() => a.Peers.Single().Connected && b.Peers.Single().Connected, token);

            var fetches = await Task.WhenAll(
                b.RequestCatalogAsync(aId, includeIcons: false, token, windowsOnly: true),
                b.RequestCatalogAsync(aId, includeIcons: false, token, windowsOnly: true));
            var appWindows = fetches[0].Where(app => !app.IsMonitor).ToArray();
            Check(Volatile.Read(ref catalogCalls) == 1 && fetches[0].SequenceEqual(fetches[1])
                && appWindows.Select(w => w.WindowHandle).SequenceEqual([0x101L, 0x102L])
                && appWindows.Select(w => w.WindowTitle).SequenceEqual(["Editor Window 1", "Editor Window 2"])
                && appWindows.Select(w => w.InstanceId).Distinct().Count() == 2,
                "same-process multiple windows enumerate independently and concurrent catalog reads coalesce");

            var displays = fetches[0].Where(app => app.IsMonitor).ToArray();
            var available = b.Peers.Single().Capabilities.HasFlag(LinkCapabilities.MonitorCapture) ? LinkMonitors.GetActive() : [];
            Check(displays.Select(app => app.InstanceId).SequenceEqual(available.Select(m => m.InstanceId))
                && !(await b.RequestCatalogAsync(aId, token: token)).Any(app => app.IsMonitor)
                && !(await b.RequestCatalogAsync(aId, token: token, audioOnly: true)).Any(app => app.IsMonitor),
                "the windows-only catalog lists active displays and legacy combined and audio catalogs exclude them");
            if (displays.FirstOrDefault() is { } display)
            {
                bool rejected = false;
                try { await b.RequestPullWindowAsync(aId, display with { InstanceId = "m:stale" }, token: token); }
                catch (IOException ex) when (ex.Message.Contains("display", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
                Check(rejected && !a.Streams.Any(), "a stale monitor identity cannot start capture on a different screen");
            }

            // Same-IP restart while long TTL offer is still cached recovers automatically without waiting for TTL expiry.
            await b.DisableAsync();
            await WaitUntilAsync(() => a.Peers.Single() is { Connected: false }, token);
            await using var b2 = new WindowsLinkService(dirB, "Multi Host B", port: 0,
                offerLifetime: TimeSpan.FromSeconds(20), discoveryBind: udpB, discoveryTarget: udpA);
            b2.PairingRequested += p =>
            {
                Interlocked.Increment(ref promptsB);
                _ = Task.Run(async () =>
                {
                    // A person checking the pairing code may take longer than a TCP connect attempt.
                    await Task.Delay(3300, token);
                    b2.AcceptPairing(p.Id, true);
                });
            };
            await b2.EnableAsync(token);
            await WaitUntilAsync(() => a.Peers.Single().Connected && b2.Peers.Single().Connected, token);
            Check(promptsA == 1 && promptsB == 1,
                "same-IP peer restart triggers automatic reconnect before offer TTL expiry without re-pairing");

            // Removing trust on A makes background auto-reconnect on B fail with PairingRejected without prompting A,
            // while manual ReconnectPeerAsync still allows mutual re-pairing.
            await b2.DisableAsync();
            await WaitUntilAsync(() => !a.Peers.Single().Connected, token);
            a.Remove(bId);
            await b2.EnableAsync(token);
            await WaitUntilAsync(() => b2.Peers.Single() is { State: LinkPeerState.Failed, Failure: LinkConnectFailure.PairingRejected }, token);
            await Task.Delay(150, token);
            Check(promptsA == 1 && b2.Peers.Single().Failure == LinkConnectFailure.PairingRejected,
                "remote trust removal stops background retries with PairingRejected and never prompts the removing peer");

            await b2.ReconnectPeerAsync(aId, token);
            await WaitUntilAsync(() => a.Peers.Single().Connected && b2.Peers.Single().Connected, token);
            Check(promptsA == 2 && promptsB == 2,
                "manual reconnect allows time to verify the code after the TCP connection has opened");
        }
        finally
        {
            if (Directory.Exists(dirA)) Directory.Delete(dirA, recursive: true);
            if (Directory.Exists(dirB)) Directory.Delete(dirB, recursive: true);
        }
    }
}
