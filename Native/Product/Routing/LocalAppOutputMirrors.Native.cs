using System.Diagnostics;
using SonicRoute.Native.Audio;
using SonicRoute.AndroidAudio;
using SonicRoute.Core.Models;
using SonicRoute.Native.Product;

namespace SonicRoute;

internal static partial class LocalAppOutputMirrors
{
    private static readonly Dictionary<int, Dictionary<string, MirrorGainState>> LiveMirrorGains = new();

    internal static MirrorStatus Status(int pid) => Routes.TryGetValue(pid, out var route)
        ? route.Snapshot() : new(pid,false,false,0,0,0,null,null,null,null,[]);

    internal static bool HasSink(int pid, string sinkId) =>
        Routes.TryGetValue(pid, out var route) && route.GetSink(sinkId) != null;

    internal static MirrorGainState GetSinkGain(int pid, string sinkId)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(sinkId)) return MirrorGainState.Default;
        lock (CaptureForwardGainPreferences.Gate)
        {
            if (!Routes.TryGetValue(pid, out var route) || route.GetSink(sinkId) is not { } sink) return MirrorGainState.Default;
            return ReadGain(route, sink);
        }
    }

    internal static MirrorGainState SetSinkGain(int pid, string sinkId, int? volumePercent = null, bool? muted = null,
        bool toggleMute = false, int? maximumVolumePercent = null, bool persist = true)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(sinkId)) return MirrorGainState.Default;
        lock (CaptureForwardGainPreferences.Gate)
        {
            if (!Routes.TryGetValue(pid, out var route) || route.GetSink(sinkId) is not { } current) return MirrorGainState.Default;
            var next = ReadGain(route, current).WithChange(volumePercent, muted, toggleMute, maximumVolumePercent);
            if (persist && route.GainAppKey is { } appKey && current.GainOutputId is { } stableOutputId)
            {
                CaptureForwardGainPreferences.Save(appKey, stableOutputId, next);
                foreach (var entry in Routes)
                {
                    var active = entry.Value;
                    if (!Ids.Equals(active.GainAppKey, appKey)) continue;
                    foreach (var sink in active.Sinks)
                    {
                        if (!Ids.Equals(sink.GainOutputId, stableOutputId)) continue;
                        if (LiveMirrorGains.TryGetValue(active.ProcessId, out var gains))
                        {
                            gains.Remove(sink.Id);
                            if (gains.Count == 0) LiveMirrorGains.Remove(active.ProcessId);
                        }
                        ApplyGainToSink(sink, next);
                    }
                }
            }
            else
            {
                if (!LiveMirrorGains.TryGetValue(pid, out var gains)) LiveMirrorGains.Add(pid, gains = new(Ids));
                gains[current.Id] = next;
                ApplyGainToSink(current, next);
            }
            return next;
        }
    }

    private static MirrorGainState ReadGain(Route route, Sink sink) =>
        LiveMirrorGains.TryGetValue(route.ProcessId, out var gains) && gains.TryGetValue(sink.Id, out var state)
            ? state : CaptureForwardGainPreferences.Read(route.GainAppKey, sink.GainOutputId);

    private static void ApplyGainToSink(Sink sink, MirrorGainState state)
    {
        if (sink.DesktopPlayer is { } desktop)
            desktop.SetGain(state.VolumePercent, state.Muted);
#if SONICROUTE_PHONE
        else if (sink.Pcm is { } pcm)
            pcm.MirrorGain = state.Muted ? 0f : state.VolumePercent / 100f;
#endif
    }

    private sealed partial class Route
    {
        private readonly AutoResetEvent _renderWake = new(false);
        private readonly MirrorMeasurement _measurement = new();
        internal string? GainAppKey { get; private set; }

        internal Sink? GetSink(string id)
        {
            string normalized = CaptureForwardGainPreferences.NormalizeOutputId(id);
            foreach (var sink in Sinks)
                if (Ids.Equals(sink.Id, normalized)) return sink;
            return null;
        }

        internal MirrorStatus Snapshot() => _measurement.Snapshot(ProcessId, Sinks.Select(s => s.DesktopPlayer?.Status()).OfType<MirrorSinkStatus>().ToArray());

        private Sink[] PublishSinks(Sink[] next, bool register)
        {
            if (register) GainAppKey = PlaybackAppIdentity.Read(ProcessId)?.Key;
            // Keep gain edits and publication of the players in the same critical section.
            lock (CaptureForwardGainPreferences.Gate)
            {
                foreach (var sink in next) ApplyGainToSink(sink, ReadGain(this, sink));
                var previous = Interlocked.Exchange(ref _sinks, next);
                if (register) Routes[ProcessId] = this;
                return previous;
            }
        }

        partial void OnRouteStopped()
        {
            lock (CaptureForwardGainPreferences.Gate) LiveMirrorGains.Remove(ProcessId);
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
