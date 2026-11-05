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
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "output-startup.json"), JsonSerializer.Serialize(new
        {
            Assertions = _checks, RealPhoneAccess = false,
            Scope = "Gated phone startup overlap, failed capture joins and disposes a late renderer, ordinary PC startup stays ordered."
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
    private sealed class StartupPlayer(Action played, Action disposed) : IPcmPlayer
    {
        public void Play() => played();
        public void Stop() { }
        public void Dispose() => disposed();
    }
}
