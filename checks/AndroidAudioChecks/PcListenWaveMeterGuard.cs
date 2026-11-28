using NAudio.CoreAudioApi;

internal static partial class Program
{
    private sealed class KnownWaveMeterGuard
    {
        internal const int SegmentMs = 480;
        internal static readonly double[] Amplitudes = [.012, 0, .018, 0, .009, 0];
        private const float Quiet = 1e-9f;
        private sealed class Meter
        {
            internal readonly float[] Maximum = new float[6];
            internal readonly float[] Minimum = Enumerable.Repeat(1f, 6).ToArray();
            internal readonly int[] Count = new int[6];
            internal readonly List<(int Time, float Peak)> Envelope = [];
            internal bool Matched;
        }
        private readonly Dictionary<(string Session, uint Pid, bool Endpoint), Meter> baseline = new();
        private readonly Meter own = new();
        private readonly Queue<(long Time, float Peak)> delayedOwn = new();
        internal bool Ready { get; private set; }
        internal int MatchedReturns => baseline.Values.Count(x => x.Matched);

        internal void Before(AudioSessionControl session, bool target, float peak)
        {
            if (!float.IsFinite(peak) || peak != 0) throw new InvalidOperationException("An external render session was audible before the test WAV.");
            baseline.TryAdd((session.GetSessionInstanceIdentifier, session.GetProcessID, target), new());
        }

        private static bool Near(float peak, float expected) => expected <= Quiet ? peak <= Quiet :
            Math.Abs(peak - expected) <= Math.Max(2f / 65536f, expected * .004f);

        private static void Record(Meter meter, int i, float peak)
        {
            meter.Minimum[i] = Math.Min(meter.Minimum[i], peak);
            meter.Maximum[i] = Math.Max(meter.Maximum[i], peak);
            meter.Count[i]++;
        }

        internal RenderActivity Check(MMDeviceEnumerator devices, string endpoint, long elapsedMs)
        {
            float ownPeak = 0;
            var others = new List<(Meter? Data, float Peak)>();
            var result = GetRenderActivity(devices, endpoint, (session, target, peak) =>
            {
                if (!float.IsFinite(peak) || peak < 0) throw new InvalidOperationException("A render-session meter was invalid.");
                if (session.GetProcessID == Environment.ProcessId && target) ownPeak = Math.Max(ownPeak, peak);
                else
                {
                    baseline.TryGetValue((session.GetSessionInstanceIdentifier, session.GetProcessID, target), out var meter);
                    if (meter == null && peak != 0) throw new InvalidOperationException("A new external session became audible.");
                    others.Add((meter, peak));
                }
            });
            delayedOwn.Enqueue((elapsedMs, ownPeak));
            while (delayedOwn.Count != 0 && elapsedMs - delayedOwn.Peek().Time > 100) delayedOwn.Dequeue();
            int cycle = Amplitudes.Length * SegmentMs;
            int phase = (int)(elapsedMs % cycle);
            if (!Ready)
            {
                if (elapsedMs < 2 * cycle)
                {
                    if (elapsedMs >= cycle)
                        foreach (var (meter, peak) in others)
                            meter?.Envelope.Add((phase, peak));
                    int segment = phase / SegmentMs;
                    if (phase % SegmentMs is >= 190 and <= 330)
                    {
                        Record(own, segment, ownPeak);
                        foreach (var (meter, peak) in others)
                            if (meter != null) Record(meter, segment, peak);
                    }
                    return result;
                }
                for (int i = 0; i < Amplitudes.Length; i++)
                    if (own.Count[i] < 3 || !Near(own.Minimum[i], (float)Amplitudes[i]) || !Near(own.Maximum[i], (float)Amplitudes[i]))
                        throw new InvalidOperationException("The fixture's three tones and silence intervals could not be independently verified.");
                foreach (var meter in baseline.Values)
                {
                    if (meter.Maximum.All(x => x <= Quiet)) continue;
                    for (int i = 0; i < Amplitudes.Length; i++)
                        if (meter.Count[i] < 3 || !Near(meter.Minimum[i], own.Minimum[i]) || !Near(meter.Maximum[i], own.Maximum[i]))
                            throw new InvalidOperationException(FormattableString.Invariant(
                                $"An external meter diverged during calibration at segment {i + 1}: own {own.Minimum[i]:G4}..{own.Maximum[i]:G4}, observed {meter.Minimum[i]:G4}..{meter.Maximum[i]:G4}, reads {meter.Count[i]}."));
                    meter.Matched = true;
                }
                if (MatchedReturns > 2) throw new InvalidOperationException("More than two external returns followed the fixture.");
                Ready = true;
            }
            foreach (var (meter, peak) in others)
            {
                if (meter?.Matched != true)
                {
                    if (peak != 0) throw new InvalidOperationException("An unverified external session became audible.");
                }
                else if (!delayedOwn.Any(frame => Near(peak, frame.Peak)))
                {
                    // Drivers can delay or filter a tone's rising/falling edges. Learn the edge
                    // from this exact instance and second complete wave cycle, including wraparound.
                    bool steady = phase % SegmentMs is >= 190 and <= 330;
                    float liveMin = delayedOwn.Min(s => s.Peak), liveMax = delayedOwn.Max(s => s.Peak);
                    if (!steady && (peak >= liveMin || Near(peak, liveMin)) && (peak <= liveMax || Near(peak, liveMax))) continue;
                    var history = meter.Envelope.Where(x => Math.Min(Math.Abs(x.Time - phase), cycle - Math.Abs(x.Time - phase)) <= 55);
                    float lo = float.PositiveInfinity, hi = -1;
                    foreach (var point in history) { lo = Math.Min(lo, point.Peak); hi = Math.Max(hi, point.Peak); }
                    if (steady || hi < 0 || (peak < lo && !Near(peak, lo)) || (peak > hi && !Near(peak, hi)))
                        throw new InvalidOperationException(FormattableString.Invariant(
                            $"A calibrated return diverged at {elapsedMs}ms: own {ownPeak:G4}, observed {peak:G4}."));
                }
            }
            return result;
        }
    }
}
