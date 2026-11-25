using System.Diagnostics;
using System.Text.Json.Serialization;

namespace SonicRoute.Native.Product;

internal sealed record MirrorSinkStatus(string Endpoint, long SubmittedFrames, long OverflowFrames,
    long EmptyAfterStart, double? FirstSignalMs, long NonSilentPacketFrames);
internal sealed record MirrorStatus(int Pid, bool Active, bool EventDriven, long Packets, long CapturedFrames,
    long Discontinuities, double? FirstPacketMs, double? StartToFirstPacketMs, double? P99DispatchMs,
    long? DispatchAllocatedBytes, MirrorSinkStatus[] Sinks);
[JsonSerializable(typeof(MirrorStatus))]
[JsonSerializable(typeof(string[]))]
internal partial class RoutingJson : JsonSerializerContext { }

internal sealed class MirrorMeasurement
{
    internal readonly bool Enabled = Environment.GetEnvironmentVariable("SONICROUTE_CANDIDATE_METRICS") == "1";
    internal readonly long Begin = Stopwatch.GetTimestamp();
    private readonly long[]? _durations;
    private long _started, _packets, _frames, _first, _discontinuities, _allocated;
    private bool _eventDriven;
    internal MirrorMeasurement() { if (Enabled) _durations = new long[1024]; }
    internal double? Milliseconds(long tick) => tick == 0 ? null : (tick - Begin) * 1000d / Stopwatch.Frequency;
    internal void Started(bool events) { _started = Stopwatch.GetTimestamp(); _eventDriven = events; }
    internal void Record(long begin,long end,int bytes,long discontinuities,long allocated)
    {
        if (_first == 0) _first = begin;
        _frames += bytes / 4;
        _discontinuities = discontinuities;
        if (_durations != null) _durations[(int)(_packets % _durations.Length)] = end-begin;
        ++_packets;
        _allocated += allocated;
    }
    internal MirrorStatus Snapshot(int pid,MirrorSinkStatus[] sinks)
    {
        var copy = _durations?.Take((int)Math.Min(Volatile.Read(ref _packets), _durations.Length)).ToArray();
        double? p99 = null;
        if (copy is {Length:>0}) { Array.Sort(copy); p99 = copy[(int)Math.Ceiling(copy.Length*.99)-1]*1000d / Stopwatch.Frequency; }
        return new(pid,true,_eventDriven,_packets,_frames,_discontinuities,Milliseconds(_first),
            _first == 0 ? null : (_first-_started)*1000d / Stopwatch.Frequency, p99, Enabled ? _allocated : null, sinks);
    }
}
