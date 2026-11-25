using System.Diagnostics;
using SonicRoute.Native.Audio;
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Product;

namespace SonicRoute;

internal static partial class LocalAppOutputMirrors
{
    internal static MirrorStatus Status(int pid) => Routes.TryGetValue(pid, out var route)
        ? route.Snapshot() : new(pid,false,false,0,0,0,null,null,null,null,[]);

    private sealed partial class Route
    {
        private readonly AutoResetEvent _renderWake = new(false);
        private readonly MirrorMeasurement _measurement = new();
        internal MirrorStatus Snapshot() => _measurement.Snapshot(ProcessId, Sinks.Select(s => s.DesktopPlayer.Status()).ToArray());

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
                foreach (var sink in Sinks) sink.DesktopPlayer.Drain();
            }
        }, _stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
