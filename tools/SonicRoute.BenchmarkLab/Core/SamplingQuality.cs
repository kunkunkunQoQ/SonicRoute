namespace SonicRoute.BenchmarkLab.Core;

public sealed class SamplingQuality
{
    public int RequestedIntervalMs { get; set; }
    public MetricStatistics IntervalsMs { get; set; } = new();
    public int DelayedIntervalCount { get; set; }
    public double DelayedTimeFraction { get; set; }
    public List<string> Warnings { get; set; } = new();
    public bool SuitableForComparison { get; set; } = true;

    public static SamplingQuality Calculate(IReadOnlyList<ProcessSample> samples, int requestedIntervalMs)
    {
        var result = new SamplingQuality { RequestedIntervalMs = requestedIntervalMs };
        var intervals = samples.Zip(samples.Skip(1), (a, b) => b.ElapsedMs - a.ElapsedMs).ToArray();
        if (intervals.Length < 2 || intervals.Any(d => !double.IsFinite(d) || d <= 0))
        {
            result.SuitableForComparison = false;
            result.Warnings.Add("Insufficient or non-monotonic sample timestamps.");
            return result;
        }
        double total = intervals.Sum();
        result.IntervalsMs = MetricStatistics.Calculate(intervals, total / 1000);
        // A final short interval is expected when Stop cancels the sampling wait.
        double delayedThreshold = Math.Max(requestedIntervalMs * 3.0, 100);
        var delayed = intervals.Where(d => d > delayedThreshold).ToArray();
        result.DelayedIntervalCount = delayed.Length;
        result.DelayedTimeFraction = delayed.Sum() / total;
        if (delayed.Length > 0)
            result.Warnings.Add($"{delayed.Length} sample gaps exceed {delayedThreshold:F0}ms; sampled peaks may be missed. Endpoint CPU remains cumulative.");
        if (result.DelayedTimeFraction > 0.20)
        {
            result.SuitableForComparison = false;
            result.Warnings.Add("Delayed gaps cover over 20% of the window; resource samples are unsuitable for a comparison gate.");
        }
        return result;
    }
}
