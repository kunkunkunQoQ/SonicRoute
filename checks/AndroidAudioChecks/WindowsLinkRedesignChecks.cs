using System.Net;
using System.Net.NetworkInformation;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private sealed class MockPcmPlayer : IPcmPlayer
    {
        public string? EndpointId { get; set; }
        public bool Playing { get; private set; }
        public bool Disposed { get; private set; }
        public void Play() => Playing = true;
        public void Stop() => Playing = false;
        public void Dispose() { Playing = false; Disposed = true; }
    }

    private static async Task RunWindowsLinkRedesignChecksAsync(string root, CancellationToken token)
    {
        Check(LinkDiscovery.IsVirtualIp(IPAddress.Parse("198.18.0.1"))
            && LinkDiscovery.IsVirtualIp(IPAddress.Parse("169.254.10.20"))
            && LinkDiscovery.IsVirtualIp(IPAddress.Parse("100.64.5.6"))
            && !LinkDiscovery.IsVirtualIp(IPAddress.Parse("192.168.1.25")),
            "virtual TUN/APIPA/CGNAT IP filter isolates real LAN addresses");

        var selectedLan = LinkDiscovery.SelectDisplayLanAddress(new[]
        {
            new LinkDiscovery.LanCandidate(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("255.255.0.0"), 1, "FlClash", "wintun", true, NetworkInterfaceType.Unknown),
            new LinkDiscovery.LanCandidate(IPAddress.Parse("169.254.44.55"), IPAddress.Parse("255.255.0.0"), 2, "Ethernet", "Realtek", false, NetworkInterfaceType.Ethernet),
            new LinkDiscovery.LanCandidate(IPAddress.Parse("192.168.1.25"), IPAddress.Parse("255.255.255.0"), 3, "WLAN", "Intel Wi-Fi", true, NetworkInterfaceType.Wireless80211)
        });
        Check(selectedLan != null && selectedLan.Equals(IPAddress.Parse("192.168.1.25")),
            "SelectDisplayLanAddress picks the real LAN adapter ahead of virtual TUN/APIPA adapters");

        Check(WindowsLinkOutputIdentity.TryParsePeerId(WindowsLinkOutputIdentity.FormatDeviceId(new string('b', 64)), out string parsedPeerId)
            && parsedPeerId == new string('b', 64)
            && !WindowsLinkOutputIdentity.TryParsePeerId("winlink-out:invalid", out _),
            "WindowsLinkOutputIdentity validates peer output identifiers");

        string dirA = Path.Combine(root, "redesign-a");
        string dirB = Path.Combine(root, "redesign-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            var largePng = Enumerable.Repeat((byte)0x5A, 2500).ToArray();
            var oversizedPng = Enumerable.Repeat((byte)0x7C, 12_000).ToArray();
            var catalogApps = Enumerable.Range(1, 20)
                .Select(i => new LinkRemoteApp(
                    $"app-{i}",
                    $"App {i}",
                    $"proc{i}.exe",
                    1000 + i,
                    i == 1,
                    i <= 5,
                    i <= 5 ? 0x1000 + i : 0,
                    i <= 5 ? $"Window {i}" : null,
                    i == 20 ? oversizedPng : largePng) { HasAudioSession = i == 1 ? null : i is 2 or 6 })
                .ToArray();

            MockPcmPlayer? createdPlayer = null;
            var hiddenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requestedScopes = new List<bool>();
            await using var nodeA = new WindowsLinkService(
                dirA,
                "Redesign Host A",
                0,
                discovery: false,
                localCatalogProvider: (audioOnly, _) =>
                {
                    requestedScopes.Add(audioOnly);
                    return Task.FromResult(catalogApps.Where(app => !hiddenKeys.Contains(app.Key)).ToArray());
                });
            await using var nodeB = new WindowsLinkService(
                dirB,
                "Redesign Host B",
                0,
                discovery: false,
                audioPlayerFactory: (_, endpoint, _) => createdPlayer = new MockPcmPlayer { EndpointId = endpoint });

            await nodeA.EnableAsync(token);
            await nodeB.EnableAsync(token);

            var codes = new Dictionary<string, string>();
            void Wire(WindowsLinkService node, string label) => node.PairingRequested += pairing =>
            {
                lock (codes) codes[label] = pairing.Code;
                _ = Task.Run(() => node.AcceptPairing(pairing.Id, true));
            };
            Wire(nodeA, "A");
            Wire(nodeB, "B");

            await nodeB.ConnectAsync(nodeA.LocalAddress, token: token);
            await WaitUntilAsync(() => nodeA.Peers.Any(p => p.Connected) && nodeB.Peers.Any(p => p.Connected), token);
            Check(codes.Count == 2 && codes["A"] == codes["B"] && IsEightDigits(codes["A"]),
                "8-digit comparison pairing code matches across both nodes");

            string aIdOnB = nodeB.Peers.Single(p => p.Connected).Id;
            string bIdOnA = nodeA.Peers.Single(p => p.Connected).Id;

            var fetched = await nodeB.RequestCatalogAsync(aIdOnB, includeIcons: true, token);
            Check(fetched.Length == 20
                && fetched[0].IsPlaying
                && fetched[0].IconPng?.Length == 2500
                && fetched[19].IconPng == null,
                "chunked RequestCatalogAsync transfers multi-frame catalogs and drops oversized icons");

            var fetchedAudio = await nodeB.RequestCatalogAsync(aIdOnB, token: token, audioOnly: true);
            Check(requestedScopes.SequenceEqual(new[] { false, true })
                && fetchedAudio.Select(a => a.ProcessId).SequenceEqual(new[] { 1001, 1002, 1006 })
                && fetchedAudio[1].HasAudioSession == true && !fetchedAudio[1].IsPlaying && fetchedAudio[2].HasWindow == false,
                "audio catalog forwards the requested scope and keeps inactive and background audio apps, excluding window-only apps");

            var invalidated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            nodeB.RemoteCatalogChanged += id => invalidated.TrySetResult(id);
            hiddenKeys.Add("APP-2");
            nodeA.NotifyLocalCatalogChanged();
            Check(await invalidated.Task.WaitAsync(token) == aIdOnB
                && (await nodeB.RequestCatalogAsync(aIdOnB, token: token, audioOnly: true))
                    .Select(a => a.ProcessId).SequenceEqual(new[] { 1001, 1006 }),
                "a catalog invalidation identifies the peer and the next snapshot drops newly excluded audio apps");

            bool missingRejected = false;
            try { await nodeB.RequestPullAudioAsync(aIdOnB, new LinkRemoteApp("missing-app", "Missing", "missing.exe", 999999, false, false, 0, null, null), token); }
            catch (Exception) { missingRejected = true; }
            Check(missingRejected, "RequestPullAudioAsync rejects non-existent remote process cleanly");

            bool badWindowRejected = false;
            try { await nodeB.RequestPullWindowAsync(aIdOnB, new LinkRemoteApp("bad-win", "Bad", "bad.exe", 999999, false, true, 0x7FFFFFF, "Missing", null), token: token); }
            catch (Exception) { badWindowRejected = true; }
            Check(badWindowRejected, "RequestPullWindowAsync rejects unavailable remote window cleanly");

            await using var playback = new LinkAudioPlayback(
                true,
                () => { },
                _ => { },
                (_, ep, _) => createdPlayer = new MockPcmPlayer { EndpointId = ep },
                watchDefault: _ => new NoopDisposable(),
                lifetime: token);
            playback.Push(new byte[384]);
            await WaitUntilAsync(() => playback.State.State == CaptureState.Receiving, token);
            IPhoneAudioSession session = playback;
            session.SetVolume(42);
            session.SetMute(true);
            playback.SetSmartVolume(false);
            Check(session.State.State == CaptureState.Receiving
                && session.State.Volume == 42
                && session.State.Muted
                && !session.State.SmartVolume
                && playback.Pcm?.Volume == 0f
                && createdPlayer is { Playing: true, Disposed: false },
                "LinkAudioPlayback exposes IPhoneAudioSession volume, mute, smart volume, and live state");
            session.SetMute(false);
            Check(playback.Pcm?.Volume is > 0.41f and < 0.43f, "unmuting LinkAudioPlayback restores normalized volume");
            await session.SetOutputAsync("test-endpoint");
            Check(session.State.OutputId == "test-endpoint" && createdPlayer is { EndpointId: "test-endpoint", Playing: true },
                "LinkAudioPlayback SetOutputAsync swaps underlying player cleanly");
            await playback.DisposeAsync();
            Check(session.State.State == CaptureState.Idle && createdPlayer is { Disposed: true },
                "disposing LinkAudioPlayback disposes player and returns session to idle");

            var customSettings = new LinkPeerSettings(AllowRemoteInput: false, VideoDecoder: AndroidStreamSettings.WindowsVideoDecoder,
                VideoBitRate: 24_000_000, VideoMaxFps: 60, VideoMaxHeight: 720);
            nodeA.UpdatePeerSettings(bIdOnA, customSettings);
            nodeA.PinManyApps(bIdOnA, [fetched[0], fetched[1]]);
            nodeA.SetAppSmartVolume(bIdOnA, fetched[0].Key, false);
            nodeA.UnpinApp(bIdOnA, fetched[1].Key);

            Check(!nodeA.GetPeerSettings(bIdOnA).AllowRemoteInput
                && !nodeA.IsSmartVolumeEnabled(bIdOnA, fetched[0].Key)
                && nodeA.GetPinnedApps(bIdOnA).Length == 1,
                "host saves AllowRemoteInput, video settings, pinned apps, and per-app SmartVolume");

            nodeA.UpdatePeerSettings(bIdOnA, customSettings with { AllowRemoteInput = true });
            Check(nodeA.GetPeerSettings(bIdOnA).AllowRemoteInput, "host toggles AllowRemoteInput on cleanly");
            nodeA.UpdatePeerSettings(bIdOnA, customSettings with { AllowRemoteInput = false });
            Check(!nodeA.GetPeerSettings(bIdOnA).AllowRemoteInput, "host revokes AllowRemoteInput cleanly");

            await using var restored = new WindowsLinkService(dirA, "Redesign Host A", 0, discovery: false);
            restored.RestoreSavedPeers();
            var reloadedSettings = restored.GetPeerSettings(bIdOnA);
            var reloadedPins = restored.GetPinnedApps(bIdOnA);
            Check(!reloadedSettings.AllowRemoteInput
                && reloadedSettings.VideoMaxHeight == 720
                && reloadedSettings.VideoMaxFps == 60
                && reloadedSettings.VideoBitRate == 24_000_000
                && reloadedSettings.VideoDecoder == AndroidStreamSettings.WindowsVideoDecoder
                && reloadedPins.Length == 1
                && !reloadedPins[0].SmartVolume
                && !restored.IsSmartVolumeEnabled(bIdOnA, fetched[0].Key),
                "peer settings, pinned apps, and SmartVolume persist across service restart");
        }
        finally
        {
            if (Directory.Exists(dirA)) Directory.Delete(dirA, recursive: true);
            if (Directory.Exists(dirB)) Directory.Delete(dirB, recursive: true);
        }
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
