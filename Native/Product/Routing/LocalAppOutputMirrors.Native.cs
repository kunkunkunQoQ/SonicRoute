using System.Diagnostics;
using SonicRoute.Native.Audio;
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Product;

namespace SonicRoute;

internal readonly record struct MirrorGainState(int VolumePercent, bool Muted);

internal static partial class LocalAppOutputMirrors
{
    private static readonly object GainGate = new();
    private static readonly Dictionary<int, Dictionary<string, MirrorGainState>> MirrorGains = new();
    private static readonly MirrorGainState DefaultGain = new(100, false);

    internal static MirrorStatus Status(int pid) => Routes.TryGetValue(pid, out var route)
        ? route.Snapshot() : new(pid,false,false,0,0,0,null,null,null,null,[]);

    internal static MirrorGainState GetSinkGain(int pid, string sinkId)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(sinkId)) return DefaultGain;
        lock (GainGate)
            return MirrorGains.TryGetValue(pid, out var gains) && gains.TryGetValue(sinkId, out var state)
                ? state : DefaultGain;
    }

    internal static MirrorGainState SetSinkGain(int pid, string sinkId, int? volumePercent = null, bool? muted = null, bool toggleMute = false)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(sinkId)) return DefaultGain;
        lock (GainGate)
        {
            if (!MirrorGains.TryGetValue(pid, out var gains))
            {
                gains = new(Ids);
                MirrorGains.Add(pid, gains);
            }
            var previous = gains.GetValueOrDefault(sinkId, DefaultGain);
            var next = new MirrorGainState(
                Math.Clamp(volumePercent ?? previous.VolumePercent, 0, 100),
                toggleMute ? !previous.Muted : (muted ?? previous.Muted));
            gains[sinkId] = next;
            if (Routes.TryGetValue(pid, out var route))
                foreach (var sink in route.Sinks)
                    if (Ids.Equals(sink.Id, sinkId)) ApplyGainToSink(sink, next);
            return next;
        }
    }

    private static void ApplyGainToSink(Sink sink, MirrorGainState state)
    {
        if (sink.DesktopPlayer is { } desktop)
            desktop.SetGain(state.VolumePercent, state.Muted);
#if SONICROUTE_PHONE
        else if (sink.Pcm is { } pcm)
            pcm.Volume = state.Muted ? 0f : state.VolumePercent / 100f;
#endif
    }

    private sealed partial class Route
    {
        private readonly AutoResetEvent _renderWake = new(false);
        private readonly MirrorMeasurement _measurement = new();
        internal MirrorStatus Snapshot() => _measurement.Snapshot(ProcessId, Sinks.Select(s => s.DesktopPlayer?.Status()).OfType<MirrorSinkStatus>().ToArray());

        private Sink[] PublishSinks(Sink[] next, bool register)
        {
            // Keep gain edits and publication of the players in the same critical section.
            lock (GainGate)
            {
                if (MirrorGains.TryGetValue(ProcessId, out var gains))
                    foreach (var sink in next)
                        if (gains.TryGetValue(sink.Id, out var gain)) ApplyGainToSink(sink, gain);
                var previous = Interlocked.Exchange(ref _sinks, next);
                if (register) Routes[ProcessId] = this;
                return previous;
            }
        }

        partial void OnRouteStopped()
        {
            lock (GainGate) MirrorGains.Remove(ProcessId);
        }

        private Task CaptureNativeAsync(Action started) => Task.Factory.StartNew(() =>
        {
            using var mta = new MtaScope();
            using var capture = new ProcessCapture((uint)ProcessId, new Guid(ProcessCaptureIdentity.SessionId), _stop.Token, renderWake: _renderWake);
            void Deliver(ReadOnlyMemory<byte> pcm)
            {
                if (!_measurement.Enabled) { Push(pcm); return; }
                long before = Stopwatch.GetTimestamp();
                long allocations = GC.GetAllocatedBytesForCurrentThread();
                Push(pcm);
                _measurement.Record(before,Stopwatch.GetTimestamp(),pcm.Length,capture.Discontinuities,
                    GC.GetAllocatedBytesForCurrentThread()-allocations);
            }
            Action<ReadOnlyMemory<byte>> dispatch = Deliver;
            capture.Start();
            _measurement.Started(capture.EventDriven);
            started();
            while (capture.WaitForPacket(Timeout.Infinite))
            {
                capture.DrainBytes(dispatch);
                foreach (var sink in Sinks) sink.DesktopPlayer?.Drain();
            }
        }, _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
