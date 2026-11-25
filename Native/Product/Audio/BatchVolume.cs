using System.Runtime.InteropServices;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductAudio
{
    internal (int Succeeded, int Total) BatchVolume(Flow flow, int? pid, int? level, int? delta,
        bool? mute, CancellationToken token)
    {
        using var apartment = new MtaScope();
        lock (_volumeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            List<Com> volumes = [];
            try
            {
                using var devices = new Endpoints();
                VisitAll(devices, flow, (p, state, control) =>
                {
                    if (state != 2 && (pid == null || p == pid) && p != 0 && !Core.AudioService.IsInternalApplication(p, null))
                        volumes.Add(control.Query(Endpoints.SimpleVolume));
                });
                bool? target = mute;
                if (level == null && delta == null && mute == null)
                {
                    bool allMuted = true;
                    foreach (var v in volumes)
                    {
                        token.ThrowIfCancellationRequested();
                        allMuted &= v.UInt32(6) != 0;
                    }
                    target = !allMuted;
                }
                int succeeded = 0;
                foreach (var v in volumes)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (level.HasValue || delta.HasValue)
                        {
                            float request = delta.HasValue
                                ? Math.Clamp(v.Scalar(4) + Math.Clamp(delta.Value, -100, 100) / 100f, 0, 1)
                                : Math.Clamp(level!.Value, 0, 100) / 100f;
                            if (!float.IsFinite(request)) continue;
                            Endpoints.SetVolume(v, false, request, Context);
                            if (Math.Abs(v.Scalar(4) - request) <= .0051f) succeeded++;
                        }
                        else
                        {
                            Endpoints.SetMute(v, false, target!.Value, Context);
                            if (flow == Flow.Capture || (v.UInt32(6) != 0) == target.Value) succeeded++;
                        }
                    }
                    catch (COMException) { /* A session may have closed during this operation. */ }
                }
                if (flow == Flow.Capture && pid.HasValue && target.HasValue)
                {
                    token.ThrowIfCancellationRequested();
                    InvalidateGroup(flow);
                    var current = GetGroup(pid.Value, flow).Sources;
                    if (current.Count == 0 || current.Any(v => (v.UInt32(6) != 0) != target.Value))
                        return (0, volumes.Count);
                }
                return (succeeded, volumes.Count);
            }
            finally { foreach (var v in volumes) v.Dispose(); }
        }
    }
}
