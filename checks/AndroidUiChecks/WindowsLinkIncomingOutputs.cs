using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private sealed class IncomingPcmPlayer(AndroidPcmSource pcm, string? endpoint) : IPcmPlayer
    {
        internal readonly AndroidPcmSource Pcm = pcm;
        internal readonly string? Endpoint = endpoint;
        internal bool Playing;
        internal int Disposals;
        public void Play()
        {
            if (Endpoint == "broken") throw new IOException("Fixture output failed to open.");
            Playing = true;
        }
        public void Stop() => Playing = false;
        public void Dispose() { Playing = false; Interlocked.Increment(ref Disposals); }
        internal float[] ReadFrame(int samples)
        {
            byte[] buffer = new byte[samples * sizeof(float)];
            Pcm.Read(buffer, 0, buffer.Length);
            return MemoryMarshal.Cast<byte, float>(buffer).ToArray();
        }
    }

    private static void CheckIncomingWindowsAudioOutputs(QuickPanelModernWindow panel, Type hub)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        string state = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        var players = new ConcurrentQueue<IncomingPcmPlayer>();
        var thirdPlayers = new ConcurrentQueue<IncomingPcmPlayer>();
        Action<ReadOnlyMemory<byte>>? emit = null;
        int captureStarts = 0, sourceMuted = 0;
        var sender = new WindowsLinkService(Path.Combine(state, "transmitter"), "Fixture source", 0, discovery: false)
        {
            ProbeAudioCapture = _ => true,
            ProbeExclusiveCapture = () => false, ProbeSpeakerFallback = () => false,
            AcquireSystemSpeakerSilence = () =>
            {
                Interlocked.Increment(ref sourceMuted);
                return new PushSpeakerLease(() => Interlocked.Decrement(ref sourceMuted));
            },
            CaptureAudio = (_, write, started, stop) =>
            {
                Interlocked.Increment(ref captureStarts);
                Volatile.Write(ref emit, write);
                started();
                return Task.Delay(Timeout.Infinite, stop);
            }
        };
        var receiver = new WindowsLinkService(Path.Combine(state, "mixer"), "Fixture mixer", 0, discovery: false,
            audioPlayerFactory: (pcm, endpoint, _) =>
            {
                var player = new IncomingPcmPlayer(pcm, endpoint);
                players.Enqueue(player);
                return player;
            }) { ProbeAudioCapture = _ => false };
        var third = new WindowsLinkService(Path.Combine(state, "third"), "Third PC", 0, discovery: false,
            audioPlayerFactory: (pcm, endpoint, _) =>
            {
                var player = new IncomingPcmPlayer(pcm, endpoint);
                thirdPlayers.Enqueue(player);
                return player;
            }) { ProbeAudioCapture = _ => false };
        sender.PairingRequested += p => sender.AcceptPairing(p.Id, true);
        receiver.PairingRequested += p => receiver.AcceptPairing(p.Id, true);
        third.PairingRequested += p => third.AcceptPairing(p.Id, true);
        var active = hub.GetField("_service", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = active.GetValue(null);
        try
        {
            Pump(Task.WhenAll(sender.EnableAsync(token), receiver.EnableAsync(token), third.EnableAsync(token)));
            Pump(sender.ConnectAsync(receiver.LocalAddress, token: token));
            Pump(third.ConnectAsync(receiver.LocalAddress, token: token));
            Pump(third.ConnectAsync(sender.LocalAddress, token: token));
            Until(() => new[] { receiver, sender, third }.All(node => node.Peers.Count(p => p.Connected) == 2));
            var mixerId = sender.Peers.Single(p => p.Name == "Fixture mixer").Id;
            var origin = receiver.Peers.Single(p => p.Name == "Fixture source");
            var target = receiver.Peers.Single(p => p.Name == "Third PC");
            string originOutput = WindowsLinkOutputIdentity.FormatDeviceId(origin.Id);
            string relayOutput = WindowsLinkOutputIdentity.FormatDeviceId(target.Id);
            active.SetValue(null, receiver);

            var panelType = typeof(QuickPanelModernWindow);
            panelType.GetField("_outputDisplay", Hidden)!.SetValue(panel, new List<AudioDeviceInfo>
            {
                new() { Id = AudioService.SystemDefaultDeviceId, DisplayName = "System", Flow = EDataFlow.eRender },
                new() { Id = "headphones", DisplayName = "Headphones", Flow = EDataFlow.eRender }
            });
            var rows = (IDictionary)panelType.GetField("_androidRows", Hidden)!.GetValue(panel)!;
            void Refresh() => panelType.GetMethod("RefreshAndroidRow", Hidden)!.Invoke(panel, [true]);
            foreach (var kind in new[] { LinkMediaKind.AppAudio, LinkMediaKind.SystemAudio })
            {
                players.Clear();
                thirdPlayers.Clear();
                var sending = sender.SendAudioAsync(mixerId, kind == LinkMediaKind.AppAudio ? 77 : null,
                    "Fixture audio", token, appKey: kind == LinkMediaKind.AppAudio ? "fixture-app" : LinkRemoteApp.SystemAudioKey);
                Pump(sending);
                Guid id = sending.Result;
                Until(() => receiver.IncomingAudioSources.Any(s => s.StreamId == id && s.Session.State.State == CaptureState.Receiving));
                var source = receiver.IncomingAudioSources.Single(s => s.StreamId == id);
                var session = source.Session;
                session.SetSmartVolume(false);
                session.SetVolume(25);
                Refresh();
                object row = rows[source.Id]!;
                var type = row.GetType();
                ((ToggleButton)type.GetField("Expand", Hidden)!.GetValue(row)!).IsChecked = true;
                var root = (FrameworkElement)type.GetField("Root", Hidden)!.GetValue(row)!;
                Button Output(string device) => Descendants<Button>(root).Single(b => b.Tag is AudioDeviceInfo d && d.Id == device);
                void Toggle(string device)
                {
                    InvokeOutputButton(Output(device), control: true);
                    Pump((Task)type.GetField("PlaybackChange", Hidden)!.GetValue(row)!);
                }
                bool Selected(string device) => Output(device).Style == panel.FindResource("DevButtonActive");
                Toggle("headphones");
                Check(session.GetOutputs().SequenceEqual([null, "headphones"]) && Selected(AudioService.SystemDefaultDeviceId)
                    && Selected("headphones"), $"{kind}: Ctrl on the incoming mixer row selects both System and Headphones");
                var main = players.Single(p => p.Endpoint == null);
                var mirror = players.Single(p => p.Endpoint == "headphones");
                short[] samples = [16384, -8192, 4096, -2048];
                byte[] frame = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
                float[] expected = samples.Select(s => s / 32768f * .25f).ToArray();
                void SendFrame() => Volatile.Read(ref emit)!(frame);
                SendFrame();
                Until(() => main.Pcm.BufferedSamples >= samples.Length && mirror.Pcm.BufferedSamples >= samples.Length);
                Check(main.ReadFrame(samples.Length).SequenceEqual(expected) && mirror.ReadFrame(samples.Length).SequenceEqual(expected)
                    && main.Pcm.SamplesWritten == samples.Length && mirror.Pcm.SamplesWritten == samples.Length,
                    $"{kind}: one TLS audio packet reaches both players once, at the selected row volume");

                session.SetMute(true);
                SendFrame();
                Until(() => mirror.Pcm.BufferedSamples >= samples.Length);
                Check(main.ReadFrame(samples.Length).All(s => s == 0) && mirror.ReadFrame(samples.Length).All(s => s == 0),
                    $"{kind}: muting silences the same incoming packet on both outputs");
                session.SetMute(false);
                bool rejected = false;
                try { Pump(session.SetOutputsAsync(["broken", "staged"])); }
                catch (IOException) { rejected = true; }
                Check(rejected && session.GetOutputs().SequenceEqual([null, "headphones"]) && main.Playing && mirror.Playing
                    && players.Single(p => p.Endpoint == "staged").Disposals == 1 && players.Single(p => p.Endpoint == "broken").Disposals == 1,
                    $"{kind}: a failed primary switch releases the staged players and preserves both previous outputs");
                SendFrame();
                Until(() => mirror.Pcm.BufferedSamples >= samples.Length);
                Check(main.ReadFrame(samples.Length).SequenceEqual(expected) && mirror.ReadFrame(samples.Length).SequenceEqual(expected),
                    $"{kind}: both original players continue receiving unmuted samples after rollback");

                Check(receiver.CanRelayTo(source, target) && !receiver.CanRelayTo(source, origin)
                    && !receiver.CanRelayTo(source, target with { SupportsAudioRelay = false }),
                    $"{kind}: only the updated third PC is eligible for forwarding");
                bool refused = false;
                try { Pump(session.SetOutputsAsync([null, originOutput])); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused && session.GetOutputs().SequenceEqual([null, "headphones"]),
                    $"{kind}: a direct attempt to feed a stream back to its origin preserves the existing outputs");
                Toggle(relayOutput);
                Until(() => third.IncomingAudioSources.Any(s => s.Session.State.State == CaptureState.Receiving));
                var forwarded = third.IncomingAudioSources.Single();
                forwarded.Session.SetSmartVolume(false);
                var thirdPlayer = thirdPlayers.Single();
                SendFrame();
                Until(() => thirdPlayer.Pcm.BufferedSamples >= samples.Length);
                Check(main.ReadFrame(samples.Length).SequenceEqual(expected) && mirror.ReadFrame(samples.Length).SequenceEqual(expected)
                    && thirdPlayer.ReadFrame(samples.Length).SequenceEqual(expected) && thirdPlayer.Pcm.SamplesWritten == samples.Length
                    && Selected(originOutput) && Selected(AudioService.SystemDefaultDeviceId) && Selected(relayOutput)
                    && sender.Streams.Single(s => s.Id == id).DestinationOnly == false && captureStarts == (kind == LinkMediaKind.AppAudio ? 1 : 2),
                    $"{kind}: source remains enabled while one capture feeds local outputs and the third PC at the same gain");
                refused = false;
                try { Pump(forwarded.Session.SetOutputsAsync([null, originOutput])); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused && forwarded.IsRelayed && third.GetIncomingMedia(forwarded.StreamId) is { IsRelayed: true } relay
                    && relay.Kind == kind && !third.CanRelayTo(forwarded, third.Peers.Single(p => p.Id == origin.Id))
                    && sender.IncomingAudioSources.Length == 0 && forwarded.Session.GetOutputs().SequenceEqual([null]),
                    $"{kind}: the third PC refuses a second hop across its own live connection to the origin");
                Toggle(relayOutput);
                Until(() => third.IncomingAudioSources.Length == 0 && thirdPlayer.Disposals == 1);
                Check(Selected(originOutput) && Selected(AudioService.SystemDefaultDeviceId) && !Selected(relayOutput)
                    && session.GetOutputs().SequenceEqual([null, "headphones"]) && sender.Streams.Any(s => s.Id == id),
                    $"{kind}: removing the relay keeps the source and both selected local outputs");

                Toggle("headphones");
                long stoppedAt = mirror.Pcm.SamplesWritten;
                SendFrame();
                Until(() => main.Pcm.BufferedSamples >= samples.Length);
                Check(session.GetOutputs().SequenceEqual([null]) && Selected(AudioService.SystemDefaultDeviceId) && !Selected("headphones")
                    && mirror.Disposals == 1 && mirror.Pcm.SamplesWritten == stoppedAt && main.ReadFrame(samples.Length).SequenceEqual(expected),
                    $"{kind}: removing Headphones detaches that player while System continues receiving audio");
                Pump(session.SetOutputsAsync([relayOutput]));
                Until(() => third.IncomingAudioSources.Length == 1);
                var last = third.IncomingAudioSources.Single();
                last.Session.SetSmartVolume(false);
                int defaultsOpened = players.Count(p => p.Endpoint == null);
                if (kind == LinkMediaKind.AppAudio)
                {
                    Pump(session.SetOutputsAsync([relayOutput, "headphones"]));
                    Pump(third.StopStreamAsync(last.StreamId));
                    Until(() => session.GetOutputs().SequenceEqual(["headphones"]));
                    SendFrame();
                    var promoted = players.Last(p => p.Playing && p.Endpoint == "headphones");
                    Until(() => promoted.Pcm.BufferedSamples >= samples.Length);
                    Check(players.Count(p => p.Endpoint == null) == defaultsOpened && promoted.ReadFrame(samples.Length).SequenceEqual(expected)
                        && sender.Streams.Any(s => s.Id == id), "closing the primary relay promotes the selected headphones and preserves the upstream");
                    Pump(sender.StopStreamAsync(id));
                }
                else
                {
                    Pump(receiver.SetSourcePlaybackAsync(id, false, true, token));
                    Check(sourceMuted == 1 && session.GetOutputs().SequenceEqual([relayOutput]), "the third PC is the only selected output");
                    Pump(third.DisconnectAsync(mixerId));
                    Until(() => session.GetOutputs().SequenceEqual([null]) && players.Count(p => p.Endpoint == null) == defaultsOpened + 1);
                    Check(sender.Streams.Any(s => s.Id == id) && sourceMuted == 1,
                        "losing the sole relay restores the previously selected receiver output while preserving source isolation");
                    Pump(sender.StopStreamAsync(id));
                }
                Until(() => session.State.State == CaptureState.Idle && players.All(p => p.Disposals != 0));
                Refresh();
                Check(!rows.Contains(source.Id) && players.All(p => p.Disposals == 1 && !p.Playing),
                    $"{kind}: closing the incoming stream removes its mixer row and releases every opened player once");
            }
        }
        finally
        {
            active.SetValue(null, original);
            Pump(Task.WhenAll(sender.DisposeAsync().AsTask(), receiver.DisposeAsync().AsTask(), third.DisposeAsync().AsTask()));
        }
    }
}
