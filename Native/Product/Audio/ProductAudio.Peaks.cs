using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductAudio
{
    internal static readonly Guid SessionPeakMeter = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    internal Dictionary<int, float> ReadPeaks(int[] processes)
    {
        using var apt = new MtaScope();
        using var devices = new Endpoints();
        var result = new Dictionary<int, float>(processes.Length);
        VisitAll(devices, Flow.Render, (processId, state, control) =>
        {
            int pid = (int)processId;
            if (state != 1 || !processes.Contains(pid)) return;
            try
            {
                using var meter = control.Query(SessionPeakMeter);
                result[pid] = Math.Max(result.GetValueOrDefault(pid), Math.Clamp(meter.Scalar(3), 0f, 1f));
            }
            catch (COMException) { }
        });
        return result;
    }
}
