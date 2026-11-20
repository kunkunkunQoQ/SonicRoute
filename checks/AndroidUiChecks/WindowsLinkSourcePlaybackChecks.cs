using System.IO;
using System.Reflection;
using System.Text.Json;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task CheckSourcePlaybackAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        string root = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        int appMuted = 0, systemMuted = 0, starts = 0;
        bool isolate = true, canLocateSpeaker = true;
        await using var source = new WindowsLinkService(Path.Combine(root, "origin"), "Origin", port: 0, discovery: false,
            acquireLocalSilence: _ =>
            {
                if (!isolate) return null;
                Interlocked.Increment(ref appMuted);
                return new PushSpeakerLease(() => Interlocked.Decrement(ref appMuted));
            })
        {
            ProbeAudioCapture = _ => true, ProbeExclusiveCapture = () => true,
            ProbeSpeakerFallback = () => true, ProbeAppSpeaker = _ => canLocateSpeaker,
            AcquireSpeakerFallback = _ => throw new InvalidOperationException("Should not mute a speaker without consent."),
            AcquireSystemSpeakerSilence = () =>
            {
                Interlocked.Increment(ref systemMuted);
                return new PushSpeakerLease(() => Interlocked.Decrement(ref systemMuted));
            },
            CaptureAudio = async (_, write, started, stop) =>
            {
                Interlocked.Increment(ref starts);
                byte[] pcm = new byte[1920];
                for (int i = 0; i < pcm.Length; i += 2) pcm[i] = 110;
                started();
                while (!stop.IsCancellationRequested) { write(pcm); await Task.Delay(10, stop); }
            }
        };
        await using var receiver = new WindowsLinkService(Path.Combine(root, "destination"), "Destination", port: 0, discovery: false,
            audioPlayerFactory: (_, _, _) => new AirPlayFixtureOutput());
        source.PairingRequested += p => source.AcceptPairing(p.Id, true);
        receiver.PairingRequested += p => receiver.AcceptPairing(p.Id, true);
        await Task.WhenAll(source.EnableAsync(token), receiver.EnableAsync(token));
        await receiver.ConnectAsync(source.LocalAddress, token: token);
        while (!source.Peers.Any(p => p.Connected) || !receiver.Peers.Any(p => p.Connected)) await Task.Delay(10, token);
        var target = source.Peers.Single(p => p.Connected).Id;
        Check(receiver.Peers.Single(p => p.Connected).SupportsSourcePlayback, "paired endpoints negotiated source playback control");
        var legacy = JsonSerializer.Deserialize<LinkHello>("""{"Name":"Old","Port":4567,"Capabilities":129,"Trusted":true,"Commitment":""}""");
        Check(legacy is { SupportsSourcePlayback: false }, "old Hello without the extension cannot receive source playback commands");
        async Task<bool> Refused(Guid stream, bool speaker = false)
        {
            try { await receiver.SetSourcePlaybackAsync(stream, false, speaker, token); return false; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { return true; }
        }
        async Task WaitFor(Func<bool> predicate) { while (!predicate()) await Task.Delay(10, token); }
        bool Agreed(Guid id, bool only) => source.Streams.Any(s => s.Id == id && s.DestinationOnly == only)
            && receiver.Streams.Any(s => s.Id == id && s.DestinationOnly == only);
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var assembly = typeof(App).Assembly;
        var field = assembly.GetType("SonicRoute.WindowsLinkHub")!.GetField("_service", statics)!;
        var previousHub = field.GetValue(null);
        field.SetValue(null, receiver);
        var group = assembly.GetType("SonicRoute.WindowsLinkIncomingOutputs")!;
        var apply = group.GetMethod("ApplyAsync", statics)!.CreateDelegate<Func<WindowsLinkAudioSource, string[], bool, Task>>();
        var selectedOutputs = group.GetMethod("GetSelected", statics)!.CreateDelegate<Func<WindowsLinkAudioSource, HashSet<string>>>();
        string origin = WindowsLinkOutputIdentity.FormatDeviceId(receiver.Peers.Single(p => p.Connected).Id);
        try
        {

            // Only synthetic PCM enters this connection, including in the system-audio case.
            Guid app = await source.SendAudioAsync(target, 77, "Silent app", token);
            var appSource = receiver.IncomingAudioSources.Single();
            var session = appSource.Session;
            await apply(appSource, [origin, "fixture-speaker", "fixture-phone"], false);
            Check(selectedOutputs(appSource).SetEquals([origin, "fixture-speaker", "fixture-phone"]),
                "incoming output chooser reflects the origin and both independent receiver outputs");
            var selected = session.GetOutputs();
            isolate = false;
            canLocateSpeaker = false;
            try { await receiver.SetSourcePlaybackAsync(app, false, false, token); throw new Exception("Expected unavailable source."); }
            catch (IOException) { }
            Check(Agreed(app, false), "an application without an identifiable speaker cannot prompt for a mute");
            canLocateSpeaker = true;
            try { await receiver.SetSourcePlaybackAsync(app, false, false, token); throw new Exception("Missing speaker confirmation."); }
            catch (SourceSpeakerConsentRequiredException) { }
            Check(Agreed(app, false) && appMuted == 0, "only an available speaker fallback raises the typed consent response");
            isolate = true;
            await apply(appSource, ["fixture-speaker", "fixture-phone"], false);
            long samples = session.Pcm!.NonSilentSamplesWritten;
            await WaitFor(() => session.Pcm!.NonSilentSamplesWritten > samples);
            Check(Agreed(app, true) && appMuted == 1 && starts == 1 && session.GetOutputs().SequenceEqual(selected),
                "application source mutes in place; PCM continues on the original stream and selected receiver outputs");
            await apply(appSource, [origin, "fixture-speaker", "fixture-phone"], false);
            Check(Agreed(app, false) && appMuted == 0 && starts == 1, "selecting the source releases its app mute without replacing the capture");
            Guid system = await source.SendAudioAsync(target, null, "Whole source", token);
            try { await receiver.SetSourcePlaybackAsync(system, false, true, token); throw new Exception("Expected source conflict."); }
            catch (IOException) { }
            Check(systemMuted == 0, "a conflicting app capture blocks a whole-computer mute without offering speaker consent");
            await apply(appSource, [origin], false);
            await WaitFor(() => source.Streams.All(s => s.Id != app));
            Check(await Refused(system) && Agreed(system, false) && systemMuted == 0, "a whole-computer mute requires explicit speaker consent");
            var systemSource = receiver.IncomingAudioSources.Single();
            var before = selectedOutputs(systemSource);
            try { await apply(systemSource, ["fixture-phone"], false); throw new Exception("Missing whole-system consent."); }
            catch (Exception ex) when (ex.GetType().Name == "SpeakerConsent") { }
            Check(before.SetEquals(selectedOutputs(systemSource)) && before.Contains(AudioService.SystemDefaultDeviceId) && before.Contains(origin),
                "requesting whole-computer mute through the chooser preserves every prior selection until confirmed");
            await apply(systemSource, ["fixture-phone"], true);
            Check(Agreed(system, true) && systemMuted == 1, "confirmed system-source mute is reflected at both ends");
            try { await source.SendAudioAsync(target, 77, "Silent app", token); throw new Exception("Expected active system mute."); }
            catch (InvalidOperationException) { }
            Check(source.Streams.Single().Id == system, "a new shared app cannot promise local sound while system speakers are held muted");
            await receiver.SetSourcePlaybackAsync(system, true, false, token);
            Check(Agreed(system, false) && systemMuted == 0, "the source computer rejoins system playback in place");
            await receiver.SetSourcePlaybackAsync(system, false, true, token);
            await apply(systemSource, [origin], false);
            await WaitFor(() => source.Streams.Length == 0 && systemMuted == 0);
            Check(receiver.IncomingAudioSources.Length == 0, "selecting only the source restores its speakers and stops the network stream");
            system = await source.SendAudioAsync(target, null, "Whole source", token);
            await receiver.SetSourcePlaybackAsync(system, false, true, token);
            await receiver.DisconnectAsync(receiver.Peers.Single(p => p.Connected).Id);
            await WaitFor(() => systemMuted == 0 && source.Streams.Length == 0);
            Check(starts == 3, "disconnect releases whole-computer mute; only the explicitly reopened stream started a new capture");
        }
        finally { field.SetValue(null, previousHub); }
    }
}
