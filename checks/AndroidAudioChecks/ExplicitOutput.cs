using System.Text.Json;
using MMDeviceEnumerator = NAudio.CoreAudioApi.MMDeviceEnumerator;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckPhoneRouteSwitchLiveAsync(string runtime, string profile, string address, string package,
        string? additionalWindowsOutput)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = timeout.Token;
        await using var adb = new AdbClient(runtime);
        var saved = new AndroidPhoneStore(profile).Profile.LastDevice ?? throw new InvalidOperationException("No saved phone");
        var device = (await new AndroidWirelessConnection(adb).ReconnectAsync(saved, address, token)).Device;
        int uid = await adb.ResolveUidAsync(device, package, token);
        var speaker = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        await using var session = new AndroidAudioSession(adb);
        using var enumerator = additionalWindowsOutput == null ? null : new MMDeviceEnumerator();
        using var local = enumerator?.GetDevice(additionalWindowsOutput!);
        var multi = (IMultiOutputAudioSession)session;
        await multi.SetOutputsAsync([AndroidAudioSession.SourcePhoneOutputId]);
        await session.StartAsync(device, new AndroidApp(package, package, uid, "media"), true, token);
        for (int i = 0; i < 2; i++)
        {
            await multi.SetOutputsAsync([speaker]);
            var capture = session.Pcm;
            if (local != null && i == 1)
            {
                await multi.SetOutputsAsync([speaker, local.ID]);
                Check(ReferenceEquals(capture, session.Pcm) && multi.GetOutputs().Contains(local.ID),
                    "adding the Windows speaker retains the running phone capture");
            }
            var first = session.PhoneOutputDiagnostics ?? throw new IOException(session.GetLog());
            float peak = 0;
            for (int step = 0; step < 24; step++)
            {
                await Task.Delay(250, token);
                if (i == 1 && local != null) peak = Math.Max(peak, local.AudioMeterInformation.MasterPeakValue);
            }
            var last = session.PhoneOutputDiagnostics ?? throw new IOException(session.GetLog());
            long bytes = last.Bytes - first.Bytes;
            Console.WriteLine($"Speaker pass {i + 1}: bytes={bytes} nonSilentBytes={last.NonSilentBytes - first.NonSilentBytes} dropped={session.Pcm?.DroppedSamples}");
            Check(last.RoutedType == 2 && bytes > 48000 * 4 * 5 && session.Pcm?.DroppedSamples == 0 && !session.UsesSourcePhonePlayback,
                "explicit Xiaomi speaker receives six seconds of captured application PCM");
            if (i == 1 && local != null)
            {
                Check(peak > 0 && last.NonSilentBytes > first.NonSilentBytes, $"both the Windows endpoint (peak {peak:F3}) and phone received music");
                await multi.SetOutputsAsync([speaker]);
                Check(ReferenceEquals(capture, session.Pcm) && multi.GetOutputs().SequenceEqual([speaker]),
                    "removing the Windows speaker retains phone playback");
            }
            await multi.SetOutputsAsync([AndroidAudioSession.SourcePhoneOutputId]);
            long decoded = session.PacketsDecoded;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            Check(session.UsesSourcePhonePlayback && session.PhoneOutputDiagnostics == null && session.PacketsDecoded > decoded
                && multi.GetOutputs().SequenceEqual([AndroidAudioSession.SourcePhoneOutputId]),
                "returning to the native phone output retires the PCM renderer and keeps the source connected");
        }
    }

    private static async Task ExplicitOutputLiveAsync(string runtime, string output, bool allowSilence = false)
    {
        Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var adb = new AdbClient(Path.GetFullPath(runtime));
        var device = (await adb.DevicesAsync(timeout.Token)).First(d => d.State == "device");
        var outputs = await adb.OutputsAsync(device, timeout.Token);
        var bluetooth = outputs.First(s => s.Kind == AndroidOutputSinkKind.Bluetooth && s.AndroidDeviceType != 7);
        string package = "cn.wenyu.bodian";
        if (!allowSilence) await ResumeTestMusicAsync(adb, device, package, timeout.Token);
        int uid = await adb.ResolveUidAsync(device, package, timeout.Token);
        var app = new AndroidApp(package, "波点音乐", uid, "media");
        string speaker = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        string bt = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), bluetooth.Key);
        string before = await adb.RunAsync(timeout.Token, "forward", "--list");
        var phases = new List<object>();
        await using (var session = new AndroidAudioSession(adb))
        {
            double switchMs = 0;
            session.SetVolume(30); session.SetSmartVolume(true);
            await session.StartAsync(device, app, keepPhoneAudio: true, timeout.Token);
            Console.WriteLine($"DIAG capture-before-switch nonSilent={session.Pcm!.NonSilentSamplesWritten}");
            string identity = session.State.SourceId!;
            await TimedSwitchAsync(speaker);
            await CheckPhonePhaseAsync("speaker", 2);
            var source = session.Pcm;
            await TimedSwitchAsync(bt);
            await CheckPhonePhaseAsync("bluetooth", bluetooth.AndroidDeviceType);
            Check(ReferenceEquals(source, session.Pcm), "live speaker/Bluetooth switch preserves capture, decoder and PCM queue");
            session.SetMute(true);
            await Task.Delay(350, timeout.Token);
            var muted = session.PhoneOutputDiagnostics!.Value;
            await Task.Delay(500, timeout.Token);
            var afterMute = session.PhoneOutputDiagnostics!.Value;
            Check(afterMute.Bytes > muted.Bytes && afterMute.NonSilentBytes == muted.NonSilentBytes
                && session.State.State == CaptureState.Receiving && session.State.Muted,
                "live phone mute sends only silent post-gain PCM while capture and main-panel control remain active");
            session.SetMute(false); session.SetVolume(20);
            await TimedSwitchAsync(speaker);
            await CheckPhonePhaseAsync("speaker-after-unmute", 2);
            Check(session.State.Volume == 20 && session.State.SmartVolume && session.State.SourceId == identity,
                "live phone routing preserves source identity, volume and SmartVolume");
            await TimedSwitchAsync(null);
            Check(session.State.State == CaptureState.Receiving && session.PhoneOutputDiagnostics == null
                && session.State.SourceId == identity, "live return to PC keeps the same controlled phone session");
            await session.StopAsync();
            Check(session.State.State == CaptureState.Idle && session.Pcm == null,
                "explicit exit releases capture and returns control to the phone");

            async Task CheckPhonePhaseAsync(string label, int expectedType)
            {
                Console.WriteLine($"LATENCY session-{label} commandMs={switchMs:F1}");
                var pcm = session.Pcm!;
                var prior = session.PhoneOutputDiagnostics!.Value;
                var deadline = DateTime.UtcNow.AddSeconds(3);
                var current = prior;
                do
                {
                    await Task.Delay(100, timeout.Token);
                    current = session.PhoneOutputDiagnostics!.Value;
                } while (current.NonSilentBytes <= prior.NonSilentBytes && DateTime.UtcNow < deadline && !allowSilence);
                Console.WriteLine($"DIAG {label}: type={current.RoutedType}/{expectedType}; bytes={prior.Bytes}->{current.Bytes}; nonSilent={prior.NonSilentBytes}->{current.NonSilentBytes}; decoded={pcm.NonSilentSamplesWritten}; state={session.State.State}; error={session.State.Error}");
                bool nonSilent = current.NonSilentBytes > prior.NonSilentBytes && pcm.NonSilentSamplesWritten > 0;
                Check(current.RoutedType == expectedType && current.Bytes > prior.Bytes && (allowSilence || nonSilent)
                    && session.State.State == CaptureState.Receiving,
                    "live " + label + " verifies actual hardware and " + (allowSilence ? "PCM delivery (silence allowed)" : "real non-silent music"));
                phases.Add(new { Phase = label, ActualHardwareType = current.RoutedType,
                    SwitchCommandMs = switchMs, BytesSent = current.Bytes, NonSilentBytesSent = current.NonSilentBytes, DecodedNonSilentSamples = pcm.NonSilentSamplesWritten });
            }
            async Task TimedSwitchAsync(string? id)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try { await session.SetOutputAsync(id); }
                catch
                {
                    var player = typeof(AndroidAudioSession).GetField("_player", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session);
                    var connection = player?.GetType().GetField("_output", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(player);
                    if (connection != null)
                        foreach (string property in new[] { "LastRouteReport", "SwitchSetup" })
                            Console.WriteLine("FAILED-DIAG " + property + ": " + connection.GetType().GetProperty(property,
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(connection));
                    Console.WriteLine(session.GetLog());
                    throw;
                }
                switchMs = clock.Elapsed.TotalMilliseconds;
                Console.WriteLine(session.GetLog());
            }
        }
        string after = await adb.RunAsync(timeout.Token, "forward", "--list");
        Check(before.Trim() == after.Trim(), "live verification removes only its forwards and preserves pre-existing connections");
        await File.WriteAllTextAsync(Path.Combine(output, "explicit-output-live.json"), JsonSerializer.Serialize(new
        { Passed = true, Assertions = _checks, RealPhoneAccess = true, Phases = phases,
            SilenceAllowed = allowSilence,
            Scope = "Actual WavePoint Opus capture, gained PCM relay, verified native output, mute, return to PC and owned cleanup. Human listening remains manual." },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task ResumeTestMusicAsync(AdbClient adb, AndroidDevice device, string package, CancellationToken token)
    {
        var first = new TaskCompletionSource<AndroidMediaState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AndroidMediaSessionClient(adb);
        using var watchLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watch = client.WatchAsync(device, state =>
        {
            first.TrySetResult(state);
            if (state.Package == package && state.Playing) playing.TrySetResult();
        }, watchLifetime.Token);
        try
        {
            var snapshot = await first.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (snapshot.Package != package) throw new InvalidOperationException("请先在波点音乐播放歌曲，再执行声音检查。");
            if (!snapshot.Playing) await client.SendAsync("toggle", token);
            await playing.Task.WaitAsync(TimeSpan.FromSeconds(8), token);
            Console.WriteLine("PRECONDITION WavePoint MediaSession is playing.");
        }
        finally
        {
            watchLifetime.Cancel();
            try { await watch; } catch (OperationCanceledException) { }
        }
    }

    private static async Task ExplicitOutputAsync(string output)
    {
        var device = new AndroidDevice("output-fixture", "Test", "device", true);
        var app = new AndroidApp("cn.test.music", "Music", 1001, "media");
        string speaker = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        string bluetooth = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "bt:test");
        var modes = new List<bool>();
        int disposed = 0, players = 0;
        await using var session = new AndroidAudioSession(
            () => new FakeTransport(BuildWire(), () => disposed++, modes.Add), (_, _) => { players++; return new FakePlayer(); });
        await session.StartAsync(device, app, keepPhoneAudio: true);
        string id = session.State.SourceId!;
        session.SetVolume(37); session.SetSmartVolume(true);
        await session.SetOutputAsync(speaker);
        Check(session.State.State == CaptureState.Receiving && session.State.SourceId == id
            && session.State.OutputId == speaker && modes.SequenceEqual(new[] { true, false }),
            "selecting a phone output keeps the source controlled and disables the uncontrolled native duplicate");
        Check(session.State.Volume == 37 && session.Pcm!.SmartVolume && session.Pcm.Volume == 0.37f,
            "volume and SmartVolume survive capture-mode reconfiguration");
        var pcm = session.Pcm;
        await session.SetOutputAsync(bluetooth);
        Check(ReferenceEquals(pcm, session.Pcm) && modes.Count == 2 && session.State.OutputId == bluetooth,
            "speaker to Bluetooth changes only the renderer and keeps one capture and decoder");
        session.SetMute(true);
        Check(session.State.Muted && session.Pcm!.Volume == 0 && session.State.State == CaptureState.Receiving,
            "mute still affects the post-gain phone renderer without retiring its session");
        session.SetMute(false);
        bool foreignRejected = false;
        try { await session.SetOutputAsync(AndroidOutputIdentity.FormatDeviceId("another-phone", "speaker")); }
        catch (InvalidOperationException) { foreignRejected = true; }
        Check(foreignRejected && session.State.OutputId == bluetooth && modes.Count == 2,
            "foreign phone routes fail before changing source or output");
        await session.SetOutputAsync(null);
        Check(session.State.State == CaptureState.Receiving && session.State.OutputId == null
            && modes.SequenceEqual(new[] { true, false, true }) && session.State.Volume == 37,
            "returning to a PC endpoint restores the original simultaneous-play preference and retains control");
        await session.StopAsync();
        Check(disposed == 3 && session.Pcm == null && session.State.State == CaptureState.Idle,
            "explicit stop releases each owned capture exactly once");
        await session.SetOutputAsync(speaker);
        await session.StartAsync(device, app, keepPhoneAudio: true);
        Check(modes.Last() == false && session.State.OutputId == speaker && session.State.State == CaptureState.Receiving,
            "a persisted phone output starts without a stray copy on the phone default output");
        int before = players;
        await session.SetOutputAsync(speaker);
        Check(players == before && modes.Count == 4, "reselecting the current endpoint creates no additional work");
        await session.StopAsync();
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "explicit-output.json"), JsonSerializer.Serialize(new
        { Passed = true, Assertions = _checks, RealPhoneAccess = false,
            Scope = "External session identity, post-gain controls, duplication changes, foreign route rejection and explicit stop." },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
