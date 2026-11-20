using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using CoreAudio = SonicRoute.Core.Interop;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private sealed record Output(string Id, string Name, bool Default, bool Muted, bool Occupied, bool ToneActive, bool SessionMuted);

    private static bool MayResume(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue) return false;
        try { using var process = Process.GetProcessById((int)processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static Output[] Snapshot(int tonePid = -1)
    {
        // Exercise the same COM activation order as a running product with its mixer open.
        var render = CoreAudio.EDataFlow.eRender;
        var outputs = AudioService.GetDevices(render);
        var enumerator = (CoreAudio.IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(typeof(CoreAudio.MMDeviceEnumeratorComObject).GUID, true)!)!;
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var role in Enum.GetValues<CoreAudio.ERole>())
            {
                if (enumerator.GetDefaultAudioEndpoint(render, role, out var item) < 0) continue;
                try { Marshal.ThrowExceptionForHR(item.GetId(out var id)); defaults.Add(id); }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        finally { Marshal.ReleaseComObject(enumerator); }

        var results = new List<Output>();
        foreach (var output in outputs)
        {
            using var lease = WasapiDeviceLease.Open(output.Id);
            var endpoint = lease.Device;
            bool occupied = false, toneActive = false, sessionMuted = false;
            var sessions = endpoint.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                bool active = session.State == AudioSessionState.AudioSessionStateActive;
                if (session.GetProcessID == tonePid && active)
                {
                    toneActive = true;
                    sessionMuted |= session.SimpleAudioVolume.Mute;
                }
                else if (session.GetProcessID != Environment.ProcessId && (active || MayResume(session.GetProcessID))) occupied = true;
            }
            results.Add(new(endpoint.ID, endpoint.FriendlyName, defaults.Contains(endpoint.ID), endpoint.AudioEndpointVolume.Mute, occupied, toneActive, sessionMuted));
        }
        return results.ToArray();
    }

    private static async Task EmitTone(string endpointId)
    {
        using var lease = WasapiDeviceLease.Open(endpointId);
        using var output = new WasapiOut(lease.Device, AudioClientShareMode.Shared, true, 30);
        var tone = new SignalGenerator(48000, 2) { Gain = 0.013, Frequency = 713, Type = SignalGeneratorType.Sin };
        output.Init(tone.ToWaveProvider());
        output.Play();
        Console.WriteLine("ready");
        await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(55));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task Until(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(50, token);
    }

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--tone", var endpoint]) { await EmitTone(endpoint); return 0; }
        if (args is ["--mirror-tone", var toneOutput]) { await MovableToneAsync(toneOutput); return 0; }
        if (args is ["--local-mirrors", var directory]) return await CheckLocalMirrorsAsync(directory);
        if (args is ["--system-lease"]) { CheckSystemLease(); return 0; }
        if (args.Length != 1) throw new ArgumentException("Supply the isolated results directory.");
        string root = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"))).FullName;
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;

        Output[] before;
        using (var endpoints = new WasapiDeviceLease.Enumerator())
        using (var initial = endpoints.TryDefault(Role.Multimedia))
        {
            before = Snapshot();
            using var reopened = endpoints.TryDefault(Role.Multimedia);
            Require(initial != null && reopened?.Device.ID == initial.Device.ID && before.Any(d => d.Id == initial.Device.ID && d.Default),
                "the product enumerator and Core can open the same speaker in either activation order");
        }
        var free = before.Where(d => !d.Default && !d.Occupied && !d.Muted)
            .OrderBy(d => new[] { "Virtual", "Sonar", "Steam Streaming", "CABLE", "Voicemeeter" }
                .Any(label => d.Name.Contains(label, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Require(free.Length >= 2, "two idle, unmuted, non-default endpoints are available to isolate this check");
        var source = free[0];
        var destination = free[1];
        foreach (var output in before.Where(d => d.Occupied || d.Default || d.Muted))
            Console.WriteLine($"Excluded {output.Name}: default={output.Default}, live sessions={output.Occupied}, muted={output.Muted}.");
        Console.WriteLine($"Source output: {source.Name}; receiver output: {destination.Name}.");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--tone");
        start.ArgumentList.Add(source.Id);
        using var tone = Process.Start(start) ?? throw new IOException("Tone fixture could not start.");
        int pid = tone.Id;
        Output Speaker() => Snapshot(pid).Single(d => d.Id == source.Id);
        var host = typeof(SonicRoute.App).Assembly.GetType("SonicRoute.AndroidAudioHost", true)!;
        const BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
        var acquire = host.GetMethod("AcquireWindowsLinkSpeakerFallback", hidden)!.CreateDelegate<Func<int, IPcProcessSilenceLease?>>();
        var available = host.GetMethod("HasWindowsLinkSpeakerFallback", hidden)!.CreateDelegate<Func<bool>>();
        var roles = host.GetMethod("ReadSilenceRoles", hidden)!.CreateDelegate<Func<int, (string?, string?)>>();
        try
        {
            Require(await tone.StandardOutput.ReadLineAsync(token) == "ready", "713 Hz tone fixture started");
            await Until(() => Speaker() is { ToneActive: true, Default: false, Muted: false, SessionMuted: false, Occupied: false }, token);
            var originalRoute = roles(pid);
            var app = new LinkRemoteApp(tone.ProcessName, "713 Hz", tone.ProcessName, pid, true, false, 0, null, null) { HasAudioSession = true };
            AndroidPcmSource? pcm = null;
            int attempts = 0;
            IPcProcessSilenceLease? AcquireForTest(int owner)
            {
                // Guard the one permitted write just before invoking the unmodified production lease.
                try
                {
                    var snapshot = Snapshot(pid);
                    var target = snapshot.Single(d => d.ToneActive);
                    Console.WriteLine($"Source guard pid={owner}/{pid}, speaker={target.Name}, default={target.Default}, other sessions={target.Occupied}, session mute={target.SessionMuted}");
                    Require(owner == pid && target is { Default: false, Occupied: false, SessionMuted: false } && target.Id == source.Id,
                        "only the test process is rendering to the selected non-default source speaker");
                    attempts++;
                    return acquire(owner);
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
            }

            bool hasSpare = false;
            int migrationAttempts = 0;
            IPcProcessSilenceLease? TryMigrate(int owner)
            {
                if (owner != pid) throw new ArgumentException("Unexpected process.");
                migrationAttempts++;
                throw new InvalidOperationException("The fixture stays on its explicitly opened endpoint.");
            }
            await using var sender = new WindowsLinkService(Path.Combine(root, "source"), "Source", 0, false,
                localCatalogProvider: (_, _) => Task.FromResult(new[] { app }), acquireLocalSilence: TryMigrate)
                { ProbeExclusiveCapture = () => hasSpare, ProbeSpeakerFallback = available, AcquireSpeakerFallback = AcquireForTest };
            await using var receiver = new WindowsLinkService(Path.Combine(root, "destination"), "Destination", 0, false,
                audioPlayerFactory: (input, _, _) => { pcm = input; return new WasapiPcmPlayer(input, destination.Id); });
            sender.Error += ex => Console.Error.WriteLine("Sender: " + ex);
            receiver.Error += ex => Console.Error.WriteLine("Receiver: " + ex);
            sender.PairingRequested += pair => sender.AcceptPairing(pair.Id, true);
            receiver.PairingRequested += pair => receiver.AcceptPairing(pair.Id, true);
            await Task.WhenAll(sender.EnableAsync(token), receiver.EnableAsync(token));
            await receiver.ConnectAsync(sender.LocalAddress, token: token);
            var peer = receiver.Peers.Single(p => p.Connected);
            var listed = (await receiver.RequestCatalogAsync(peer.Id, false, token, audioOnly: true)).Single();
            Require(listed.CanMuteSourceSpeaker && !listed.SupportsExclusivePlayback(peer.Capabilities),
                "TLS catalog distinguishes a single-speaker fallback from a dedicated output");
            bool rejected = false;
            try { await receiver.RequestPullAudioAsync(peer.Id, listed, token, destinationOnly: true); }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException) { rejected = true; }
            Require(rejected && attempts == 0 && !Speaker().Muted, "an unconfirmed request leaves the source speaker audible");

            foreach (bool disconnect in new[] { false, true })
            {
                Guid stream;
                if (!disconnect)
                    stream = await receiver.RequestPullAudioAsync(peer.Id, listed, token, destinationOnly: true, allowSpeakerMute: true);
                else
                {
                    hasSpare = true;
                    sender.RefreshExclusiveCapability(force: true);
                    string target = sender.Peers.Single(p => p.Connected).Id;
                    Guid shared = await sender.SendAudioAsync(target, pid, app.Name, token);
                    Require(!Speaker().Muted && sender.FindOutgoingAppAudio(pid) is { DestinationOnly: false },
                        "a lower-zone push leaves the source audible");
                    try { await sender.SendAudioAsync(target, pid, app.Name, token, destinationOnly: true); }
                    catch (InvalidOperationException) { }
                    Require(sender.FindOutgoingAppAudio(pid)?.Id == shared && !Speaker().Muted && attempts == 1,
                        "an undisclosed speaker mute is never applied when the app retains its output");
                    stream = await sender.SendAudioAsync(target, pid, app.Name, token, destinationOnly: true, allowSpeakerMute: true);
                    Require(migrationAttempts == 2 && sender.FindOutgoingAppAudio(pid) is { DestinationOnly: true } routed && routed.Id == stream
                        && sender.Streams.All(s => s.Id != shared), "upper-zone push replaces shared playback after the app refuses to migrate");
                }
                var current = pcm ?? throw new InvalidOperationException("The receiver did not open a WASAPI player.");
                Require(Speaker() is { Muted: true, ToneActive: true, SessionMuted: false } && roles(pid) == originalRoute,
                    "source endpoint is muted while the tone process stays on its original unmuted session");
                long startWritten = current.NonSilentSamplesWritten, startConsumed = current.SamplesConsumed;
                await Task.Delay(700, token);
                long written = current.NonSilentSamplesWritten - startWritten, consumed = current.SamplesConsumed - startConsumed;
                Require(written > 1000 && consumed > 1000 && current.LastPeak > 0.0003f && Speaker().Muted,
                    $"while source is muted, receiver gets {written} non-silent samples, consumes {consumed} samples, peak {current.LastPeak:F5}");
                if (disconnect) await receiver.DisconnectAsync(peer.Id);
                else await receiver.StopStreamAsync(stream);
                await Until(() => sender.Streams.Length == 0 && !Speaker().Muted, token);
                Require(Speaker() is { Muted: false, SessionMuted: false, ToneActive: true } && roles(pid) == originalRoute,
                    disconnect ? "disconnect restores the original speaker and route" : "returning the stream restores the original speaker and route");
            }
            var after = Snapshot(pid);
            Require(attempts == 2 && before.All(old => after.Any(d => d.Id == old.Id && d.Muted == old.Muted && d.Default == old.Default)),
                "all pre-existing endpoint mute and default states match their initial values");
        }
        finally
        {
            tone.StandardInput.Close();
            await tone.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        return 0;
    }
}
