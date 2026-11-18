using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task AirPlayLoopbackAsync(bool preferHevc)
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        System.Diagnostics.Trace.AutoFlush = true;
        var created = new ConcurrentQueue<(string? Endpoint, SimulatedOutput Player)>();
        Action? defaultChanged = null;
        int detached = 0;
        await using var receiver = new AirPlayReceiver("SonicRoute Simulation", loopback: true,
            player: (pcm, endpoint) =>
            {
                var player = new SimulatedOutput(pcm);
                created.Enqueue((endpoint, player));
                return player;
            }, watchDefault: action =>
            {
                defaultChanged = action;
                return new AirPlayWatch(() => Interlocked.Increment(ref detached));
            }, preferHevc: preferHevc);
        int port = receiver.Port;
        byte[] mdns = AirPlayDiscovery.WriteResponse(new AirPlayIdentity("SonicRoute Simulation"), IPAddress.Loopback, port, 120);
        Console.WriteLine(JsonSerializer.Serialize(new { Port = port, Mdns = Convert.ToBase64String(mdns) }));
        while (await Console.In.ReadLineAsync() is { } command && command != "stop")
        {
            var source = receiver.Source;
            if (command == "controls" && source != null)
            {
                source.SetVolume(37);
                source.SetMute(true);
                await source.SetOutputAsync("simulated-endpoint");
            }
            if (command == "system" && source != null) await source.SetOutputAsync(null);
            if (command == "default") defaultChanged?.Invoke();
            if (command == "disconnect" && source != null) receiver.Disconnect(source.Id);
            var outputs = created.ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Stage = receiver.Status.Stage.ToString(), receiver.Status.Sender, receiver.Status.Mirroring,
                receiver.MirrorFrames, receiver.Status.ErrorKey, receiver.Status.MirrorAudioUnavailable,
                source?.Id, Volume = source?.State.Volume, Muted = source?.State.Muted,
                Endpoint = source?.State.OutputId, SourceSamples = source?.Pcm?.SamplesWritten,
                NonSilent = source?.Pcm?.NonSilentSamplesWritten, Pending = source?.Pcm?.BufferedSamples,
                Outputs = outputs.Length, Disposed = outputs.Count(o => o.Player.Disposed), Detached = detached
            }));
        }
        await receiver.DisposeAsync();
        bool remains = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(l => l.Address.Equals(IPAddress.Loopback) && l.Port == port);
        Console.WriteLine(JsonSerializer.Serialize(new { Closed = receiver.Completion.IsCompletedSuccessfully && !remains }));
    }

    private sealed class AirPlayWatch(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    // Pulls from the actual PCM queue exactly as the player would. No device is opened.
    private sealed class SimulatedOutput(AndroidPcmSource pcm) : IPcmPlayer
    {
        private CancellationTokenSource? _stop;
        private Task? _consume;
        internal bool Disposed { get; private set; }
        public void Play()
        {
            if (_stop != null) return;
            _stop = new();
            var token = _stop.Token;
            _consume = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                byte[] bytes = new byte[480 * 2 * sizeof(float)];
                while (await timer.WaitForNextTickAsync(token)) pcm.Read(bytes, 0, bytes.Length);
            });
        }
        public void Stop()
        {
            if (_stop == null) return;
            _stop.Cancel();
            try { _consume?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _stop.Dispose(); _stop = null; _consume = null;
        }
        public void Dispose() { Stop(); Disposed = true; }
    }
}
