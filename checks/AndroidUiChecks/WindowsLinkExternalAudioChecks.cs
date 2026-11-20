using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task CheckExternalPhoneOutputsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = deadline.Token;
        string root = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        var outputs = new ConcurrentQueue<IncomingPcmPlayer>();
        int rejected = 0;
        WindowsLinkService Node(string name)
        {
            var node = new WindowsLinkService(Path.Combine(root, name), name, 0, discovery: false,
                audioPlayerFactory: (pcm, endpoint, _) =>
                {
                    if (Volatile.Read(ref rejected) != 0) throw new IOException("Fixture playback is unavailable.");
                    var player = new IncomingPcmPlayer(pcm, endpoint);
                    outputs.Enqueue(player);
                    return player;
                }) { ProbeAudioCapture = _ => false };
            node.PairingRequested += request => node.AcceptPairing(request.Id, true);
            return node;
        }
        async Task WaitFor(Func<bool> test)
        {
            while (!test()) await Task.Delay(10, token);
        }
        await using var bridge = Node("PhoneBridge");
        await using var kun = Node("KUN");
        await using var other = Node("Third");
        await Task.WhenAll(bridge.EnableAsync(token), kun.EnableAsync(token), other.EnableAsync(token));
        await kun.ConnectAsync(bridge.LocalAddress, token: token);
        await other.ConnectAsync(bridge.LocalAddress, token: token);
        await other.ConnectAsync(kun.LocalAddress, token: token);
        await WaitFor(() => new[] { bridge, kun, other }.All(n => n.Peers.Count(p => p.Connected) == 2));
        var target = bridge.Peers.Single(p => p.Name == "KUN");
        var third = bridge.Peers.Single(p => p.Name == "Third");
        Check(!bridge.CanSendAudio && WindowsLinkService.CanReceiveExternalAudio(target)
            && !WindowsLinkService.CanReceiveExternalAudio(target with { SupportsAudioRelay = false }),
            "phone forwarding works on a receive-only PC and requires an updated TLS peer");
        int closed = 0, otherClosed = 0;
        var phone = new LinkExternalAudioDescriptor("example.music", "Phone music");
        await using var a = await bridge.OpenExternalAudioOutputAsync(phone, target.Id, _ => Interlocked.Increment(ref closed), token);
        await using var b = await bridge.OpenExternalAudioOutputAsync(phone, third.Id, _ => Interlocked.Increment(ref otherClosed), token);
        var atKun = kun.IncomingAudioSources.Single();
        var atThird = other.IncomingAudioSources.Single();
        Check(atKun.IsRelayed && atKun.AppKey == phone.AppKey && atKun.Name == phone.Name
            && atKun.StreamId == a.StreamId && atKun.Session.State.State == CaptureState.Receiving
            && atThird.Session.State.State == CaptureState.Receiving, "both app outputs acknowledge live playback before becoming selectable");
        atKun.Session.SetSmartVolume(false);
        atThird.Session.SetSmartVolume(false);
        var kunPlayer = outputs.Single(p => p.Pcm == atKun.Session.Pcm);
        var thirdPlayer = outputs.Single(p => p.Pcm == atThird.Session.Pcm);
        short[] signed = [-32768, 8192, 16384, -4096];
        byte[] packet = MemoryMarshal.AsBytes(signed.AsSpan()).ToArray();
        float[] expected = signed.Select(s => s / 32768f).ToArray();
        a.Push(packet);
        b.Push(packet);
        await WaitFor(() => outputs.All(p => p.Pcm.BufferedSamples >= expected.Length));
        Check(kunPlayer.ReadFrame(expected.Length).SequenceEqual(expected) && thirdPlayer.ReadFrame(expected.Length).SequenceEqual(expected)
            && kunPlayer.Pcm.SamplesWritten == expected.Length && thirdPlayer.Pcm.SamplesWritten == expected.Length,
            "the same phone PCM16 frame arrives once at KUN and the other PC with exact signed samples");
        bool refused = false;
        try { await atKun.Session.SetOutputsAsync([null, WindowsLinkOutputIdentity.FormatDeviceId(third.Id)]); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && !kun.CanRelayTo(atKun, kun.Peers.Single(p => p.Id == third.Id))
            && other.IncomingAudioSources.Length == 1, "a phone-originated stream cannot loop through another PC");
        await kun.StopStreamAsync(a.StreamId);
        await WaitFor(() => Volatile.Read(ref closed) == 1 && !bridge.Streams.Any(s => s.Id == a.StreamId));
        a.Push(packet);
        a.Dispose();
        b.Push(packet);
        await WaitFor(() => thirdPlayer.Pcm.BufferedSamples == expected.Length);
        Check(closed == 1 && otherClosed == 0 && thirdPlayer.ReadFrame(expected.Length).SequenceEqual(expected)
            && kunPlayer.Pcm.SamplesWritten == expected.Length, "closing KUN notifies once and leaves the other receiver playing");
        await b.DisposeAsync();
        await WaitFor(() => other.IncomingAudioSources.Length == 0);
        Check(otherClosed == 0, "locally removing a phone output retires the destination without raising a disconnect event");
        var system = new LinkExternalAudioDescriptor(LinkRemoteApp.SystemAudioKey, "All phone sounds", SystemAudio: true);
        await using var whole = await bridge.OpenExternalAudioOutputAsync(system, target.Id, _ => Interlocked.Increment(ref closed), token);
        var mix = kun.IncomingAudioSources.Single();
        mix.Session.SetSmartVolume(false);
        var mixPlayer = outputs.Single(p => p.Pcm == mix.Session.Pcm);
        whole.Push(packet);
        await WaitFor(() => mixPlayer.Pcm.BufferedSamples == expected.Length);
        Check(kun.GetIncomingMedia(whole.StreamId) is { Kind: LinkMediaKind.SystemAudio, IsRelayed: true }
            && mixPlayer.ReadFrame(expected.Length).SequenceEqual(expected), "the complete phone mix uses the same PCM channel and is identified as system audio");
        await kun.DisconnectAsync(kun.Peers.Single(p => p.Name == "PhoneBridge").Id);
        await WaitFor(() => closed == 2 && bridge.Streams.Length == 0 && kun.IncomingAudioSources.Length == 0);
        Check(mixPlayer.Disposals == 1, "disconnecting KUN retires the phone mix and releases its receive player");
        Volatile.Write(ref rejected, 1);
        refused = false;
        try { await using var failed = await bridge.OpenExternalAudioOutputAsync(phone, third.Id, _ => Interlocked.Increment(ref otherClosed), token); }
        catch (IOException) { refused = true; }
        await WaitFor(() => bridge.Streams.Length == 0 && other.IncomingAudioSources.Length == 0);
        Check(refused && otherClosed == 0, "a receiver that cannot open playback rejects the route before it can be selected");
    }
}
