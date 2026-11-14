using System.Text.Json;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using SonicRoute.AndroidAudio;
using CaptureState = SonicRoute.AndroidAudio.CaptureState;

internal static partial class Program
{
    private static async Task DefaultOutputAsync(string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        int notifications = 0;
        var callback = new DefaultOutputNotifications.DeviceCallback(() => notifications++);
        callback.OnDefaultDeviceChanged(DataFlow.Capture, Role.Multimedia, "input");
        callback.OnDefaultDeviceChanged(DataFlow.Render, Role.Communications, "chat");
        callback.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "console");
        Check(notifications == 0, "default renderer ignores input and unrelated device roles");
        callback.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "first");
        callback.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "FIRST");
        Check(notifications == 1, "matching render notifications suppress duplicate endpoint IDs");
        callback.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, null!);
        callback.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "second");
        Check(notifications == 3, "missing and returning default endpoints both invalidate the renderer");
        using (DefaultOutputNotifications.Subscribe(() => { }))
        using (DefaultOutputNotifications.Subscribe(() => { })) { }
        Check(true, "native shared notification registration and unregistration complete without playback");

        foreach (var app in new[] { new AndroidApp("cn.test.music", "Music", 1001, "media"), AndroidApp.DeviceAudio })
        {
            var phone = new AndroidDevice("default-fixture", "Test", "device", true);
            var watch = new DefaultOutputWatchFixture();
            var created = Channel.CreateUnbounded<DefaultOutputPlayer>();
            string defaultEndpoint = "default-A";
            int captures = 0, factories = 0;
            var failingFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = new AndroidAudioSession(
                () => { captures++; return new FakeTransport(BuildWire(), () => { }); }, (_, id) =>
                {
                    Interlocked.Increment(ref factories);
                    string resolved = id ?? Volatile.Read(ref defaultEndpoint);
                    if (resolved == "unavailable") { failingFactory.TrySetResult(); throw new IOException("endpoint unavailable"); }
                    if (resolved == "default-D") { blockedFactory.TrySetResult(); releaseFactory.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult(); }
                    var player = new DefaultOutputPlayer(resolved);
                    created.Writer.TryWrite(player);
                    return player;
                }, watch.Subscribe);
            try
            {
                await session.StartAsync(phone, app, token: deadline.Token);
                var first = await NextPlayer();
                var pcm = session.Pcm;
                string? sourceId = session.State.SourceId;
                session.SetVolume(37); session.SetMute(true); session.SetSmartVolume(false);
                Check(first.Endpoint == "default-A" && watch.Active && captures == 1,
                    $"{app.Package}: an active system-output source subscribes once");
                Volatile.Write(ref defaultEndpoint, "default-B"); watch.Signal();
                var second = await NextPlayer(); await first.Disposed.Task.WaitAsync(deadline.Token);
                Check(second.Endpoint == "default-B" && session.State.OutputId == null && ReferenceEquals(pcm, session.Pcm)
                    && session.State.SourceId == sourceId && captures == 1 && session.State.Volume == 37
                    && session.State.Muted && !session.State.SmartVolume,
                    $"{app.Package}: default change swaps only playback and retains capture, PCM, controls and system-output preference");

                await session.SetOutputAsync("pinned-speaker");
                var pinned = await NextPlayer();
                int before = factories;
                Volatile.Write(ref defaultEndpoint, "default-C"); watch.Signal();
                Check(pinned.Endpoint == "pinned-speaker" && !watch.Active && factories == before,
                    $"{app.Package}: an explicit endpoint stays pinned and releases its notification subscription");
                await session.SetOutputAsync(null);
                var returned = await NextPlayer();
                Check(returned.Endpoint == "default-C" && watch.Active && session.State.OutputId == null && captures == 1,
                    $"{app.Package}: returning to system output follows the latest endpoint without restarting capture");

                Volatile.Write(ref defaultEndpoint, "unavailable"); watch.Signal();
                await failingFactory.Task.WaitAsync(deadline.Token);
                await session.SetOutputAsync(null); // joins the serialized attempted refresh
                Check(!returned.Disposed.Task.IsCompleted && session.State.State == CaptureState.Receiving && captures == 1,
                    $"{app.Package}: a temporarily unavailable endpoint preserves the existing renderer and capture");
                Volatile.Write(ref defaultEndpoint, "default-D"); watch.Signal();
                await blockedFactory.Task.WaitAsync(deadline.Token);
                Volatile.Write(ref defaultEndpoint, "default-E"); watch.Signal();
                releaseFactory.TrySetResult();
                var intermediate = await NextPlayer();
                var latest = await NextPlayer();
                await intermediate.Disposed.Task.WaitAsync(deadline.Token);
                Check(intermediate.Endpoint == "default-D" && latest.Endpoint == "default-E" && captures == 1,
                    $"{app.Package}: a change arriving during renderer creation is drained to the newest default");
                await session.StopAsync();
                before = factories; watch.Signal();
                Check(!watch.Active && latest.Disposed.Task.IsCompleted && session.Pcm == null && factories == before,
                    $"{app.Package}: stopping playback releases the observer and subsequent changes do no work");
            }
            finally { releaseFactory.TrySetResult(); }

            async Task<DefaultOutputPlayer> NextPlayer()
            {
                var player = await created.Reader.ReadAsync(deadline.Token);
                await player.Played.Task.WaitAsync(deadline.Token);
                return player;
            }
        }
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "default-output-result.json"), JsonSerializer.Serialize(new
        {
            Success = true, Assertions = _checks, RealPhoneAccess = false, ChangedWindowsDefault = false,
            RealAudioPlayback = false, NativeNotificationRegistrationOnly = true
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} default-output assertions; no phone or Windows output changes.");
    }

    private sealed class DefaultOutputWatchFixture
    {
        private Action? _callback;
        internal bool Active => Volatile.Read(ref _callback) != null;
        internal IDisposable Subscribe(Action callback)
        {
            Volatile.Write(ref _callback, callback);
            return new DefaultOutputWatchLease(() => Volatile.Write(ref _callback, null));
        }
        internal void Signal() => Volatile.Read(ref _callback)?.Invoke();
    }
    private sealed class DefaultOutputWatchLease(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class DefaultOutputPlayer(string endpoint) : IPcmPlayer
    {
        internal string Endpoint { get; } = endpoint;
        internal TaskCompletionSource Played { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Play() => Played.TrySetResult();
        public void Stop() { }
        public void Dispose() => Disposed.TrySetResult();
    }
}
