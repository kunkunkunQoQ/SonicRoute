using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task OutputStartupAsync(string output)
    {
        var device = new AndroidDevice("startup-fixture", "Test", "device", true);
        var app = new AndroidApp("cn.test.music", "Music", 1001, "media");
        string speaker = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await GatedStartupAsync(phone: true, failCapture: false);
        await GatedStartupAsync(phone: true, failCapture: true);
        await GatedStartupAsync(phone: false, failCapture: false);
        await PhoneSwitchStartupAsync(output, cancelWhileWarming: false);
        await PhoneSwitchStartupAsync(output, cancelWhileWarming: true);
        await VerifyBeforeStoppingAsync(output);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "output-startup.json"), JsonSerializer.Serialize(new
        {
            Assertions = _checks, RealPhoneAccess = false,
            Scope = "Gated phone startup overlap, failed capture joins and disposes a late renderer, ordinary PC startup stays ordered, native output switching overlaps teardown and preserves capture on a rejected target."
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task GatedStartupAsync(bool phone, bool failCapture)
        {
            var capture = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            var captureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int factories = 0, played = 0, disposedPlayers = 0, disposedTransports = 0;
            var transport = new StartupTransport(capture.Task, () => captureEntered.TrySetResult(), () => disposedTransports++);
            await using var session = new AndroidAudioSession(() => transport, (_, _) =>
            {
                Interlocked.Increment(ref factories);
                factoryEntered.TrySetResult();
                releaseFactory.Task.GetAwaiter().GetResult();
                return new StartupPlayer(() => played++, () => disposedPlayers++);
            });
            if (phone) await session.SetOutputAsync(speaker);
            Task starting = session.StartAsync(device, app, token: deadline.Token);
            try
            {
                await captureEntered.Task.WaitAsync(deadline.Token);
                if (phone)
                {
                    await factoryEntered.Task.WaitAsync(deadline.Token);
                    Check(!capture.Task.IsCompleted && !starting.IsCompleted && played == 0,
                        "phone playback prepares while capture is pending and cannot play early");
                }
                else Check(factories == 0, "ordinary PC startup does not activate its renderer before capture connects");
                if (failCapture)
                {
                    capture.SetException(new IOException("gated capture failure"));
                    try { await Task.Delay(Timeout.Infinite, transport.RunToken).WaitAsync(deadline.Token); }
                    catch (OperationCanceledException) when (transport.RunToken.IsCancellationRequested) { }
                    Check(!starting.IsCompleted && disposedPlayers == 0,
                        "failed capture cancels its run but waits for the pending renderer owner");
                    releaseFactory.TrySetResult();
                    bool rejected = false;
                    try { await starting.WaitAsync(deadline.Token); }
                    catch (IOException) { rejected = true; }
                    Check(rejected && disposedPlayers == 1 && disposedTransports == 1 && session.Pcm == null
                        && session.State.State == CaptureState.Failed && played == 0,
                        "late renderer is disposed exactly once on failure without playing or orphaning its transport");
                }
                else
                {
                    capture.SetResult(new HoldingStream(BuildWire()));
                    await factoryEntered.Task.WaitAsync(deadline.Token);
                    Check(!starting.IsCompleted && played == 0, "startup requires both capture and renderer readiness");
                    releaseFactory.TrySetResult();
                    await starting.WaitAsync(deadline.Token);
                    Check(session.State.State == CaptureState.Receiving && played == 1,
                        "joined startup decodes and starts its renderer once");
                    await session.StopAsync();
                    Check(disposedPlayers == 1 && disposedTransports == 1 && session.Pcm == null,
                        "normal stop releases the joined renderer and capture exactly once");
                }
            }
            finally
            {
                releaseFactory.TrySetResult();
                capture.TrySetCanceled();
                try { await starting; } catch { }
            }
        }
    }

    private static async Task PhoneSwitchStartupAsync(string output, bool cancelWhileWarming)
    {
        string runtime = await FakeAudioOutputRuntimeAsync(output);
        await using var server = new FakeAdbServer();
        var adb = new AdbClient(runtime, new AdbSocketClient(server.EndPoint));
        var device = new AndroidDevice("native-switch-fixture", "Test", "device", true);
        var app = new AndroidApp("cn.test.music", "Music", 1001, "media");
        string speaker = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        string bluetooth = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "bt:aa");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modes = new List<bool>();
        int captureStarts = 0;
        await using var session = new AndroidAudioSession(() => new SwitchingTransport(modes.Add, captureStarts++ == 0 ? async () =>
        {
            retiring.TrySetResult();
            await releaseOld.Task;
        } : null), (pcm, id) => id == speaker ? new AndroidPhonePcmPlayer(adb, device, pcm, "speaker", deadline.Token) : new FakePlayer());
        await session.StartAsync(device, app, keepPhoneAudio: true, token: deadline.Token);
        Task switching = session.SetOutputAsync(speaker);
        Task? stopping = null;
        try
        {
            await retiring.Task.WaitAsync(deadline.Token);
            byte[] primed = await server.WaitForLocalAbstractBytesAsync(3840, TimeSpan.FromSeconds(3));
            Check(primed.All(b => b == 0) && captureStarts == 1 && !switching.IsCompleted,
                "speaker opens and primes while the original duplicate capture is still releasing");
            if (cancelWhileWarming) stopping = session.StopAsync();
        }
        finally { releaseOld.TrySetResult(); }
        if (stopping != null)
        {
            bool cancelled = false;
            try { await switching.WaitAsync(deadline.Token); } catch (OperationCanceledException) { cancelled = true; }
            await stopping.WaitAsync(deadline.Token);
            Check(cancelled && captureStarts == 1 && session.State.State == CaptureState.Idle && session.Pcm == null,
                "stopping during the overlap cancels the new renderer without starting another capture");
            return;
        }
        await switching.WaitAsync(deadline.Token);
        var pcm = session.Pcm;
        Check(modes.SequenceEqual([true, false]) && session.State.OutputId == speaker
            && session.State.State == CaptureState.Receiving && session.PhoneOutputDiagnostics?.RoutedType == 2,
            "route handoff captures exclusively and verifies the selected speaker");
        await session.SetOutputAsync(bluetooth).WaitAsync(deadline.Token);
        Check(session.State.OutputId == bluetooth && session.PhoneOutputDiagnostics?.RoutedType == 8
            && ReferenceEquals(pcm, session.Pcm) && captureStarts == 2,
            "the following Bluetooth switch reuses the same capture and phone renderer");
    }

    private static async Task VerifyBeforeStoppingAsync(string output)
    {
        string runtime = await FakeAudioOutputRuntimeAsync(output);
        await using var server = new FakeAdbServer();
        var adb = new AdbClient(runtime, new AdbSocketClient(server.EndPoint));
        var device = new AndroidDevice("native-switch-fixture", "Test", "device", true);
        var app = new AndroidApp("cn.test.music", "Music", 1001, "media");
        string unavailable = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "bt:unavailable");
        string speaker = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), "speaker");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        int plays = 0, stops = 0, disposed = 0;
        await using var session = new AndroidAudioSession(() => new FakeTransport(BuildWire(), () => { }), (pcm, id) =>
        {
            if (id == null) return new StartupPlayer(() => plays++, () => disposed++, () => stops++);
            return new AndroidPhonePcmPlayer(adb, device, pcm, id == speaker ? "speaker" : "bt:unavailable", deadline.Token);
        });
        await session.StartAsync(device, app, keepPhoneAudio: false, token: deadline.Token);
        var pcm = session.Pcm;
        bool rejected = false;
        try { await session.SetOutputAsync(unavailable).WaitAsync(deadline.Token); }
        catch (IOException) { rejected = true; }
        Check(rejected && plays == 1 && stops == 0 && disposed == 0 && session.State.OutputId == null && ReferenceEquals(pcm, session.Pcm),
            "an unverified phone output never stops the current Windows renderer");
        await session.SetOutputAsync(speaker).WaitAsync(deadline.Token);
        Check(session.State.OutputId == speaker && plays == 1 && stops == 1 && disposed == 1 && ReferenceEquals(pcm, session.Pcm),
            "an already exclusive capture hands off only after the new speaker acknowledges routing");
    }

    private static async Task<string> FakeAudioOutputRuntimeAsync(string output)
    {
        string runtime = Path.Combine(output, "switch-runtime");
        Directory.CreateDirectory(runtime);
        await File.WriteAllTextAsync(Path.Combine(runtime, "sonicroute-catalog.jar"), "FAKE-JAR-CONTENT");
        return runtime;
    }

    private sealed class SwitchingTransport(Action<bool> entered, Func<Task>? retire) : IAudioTransport
    {
        private HoldingStream? _stream;
        public Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
        {
            entered(keepPhoneAudio);
            _stream = new HoldingStream(BuildWire());
            return Task.FromResult<Stream>(_stream);
        }
        public async ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            if (retire != null) await retire();
        }
    }

    private sealed class StartupTransport(Task<Stream> connection, Action entered, Action disposed) : IAudioTransport
    {
        private Stream? _stream;
        public CancellationToken RunToken { get; private set; }
        public async Task<Stream> ConnectAsync(AndroidDevice device, AndroidApp app, bool keepPhoneAudio, CancellationToken token)
        {
            RunToken = token;
            entered();
            return _stream = await connection.WaitAsync(token);
        }
        public ValueTask DisposeAsync() { _stream?.Dispose(); disposed(); return ValueTask.CompletedTask; }
    }
    private sealed class StartupPlayer(Action played, Action disposed, Action? stopped = null) : IPcmPlayer
    {
        public void Play() => played();
        public void Stop() => stopped?.Invoke();
        public void Dispose() => disposed();
    }
}
