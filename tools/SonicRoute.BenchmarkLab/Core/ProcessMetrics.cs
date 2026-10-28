using System;
using System.Collections.Generic;
using System.Linq;

namespace SonicRoute.BenchmarkLab.Core
{
    public sealed class ProcessSample
    {
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public double ElapsedMs { get; set; }
        public double CollectionDurationMs { get; set; }

        public double TotalCpuTimeMs { get; set; }
        public double UserCpuTimeMs { get; set; }
        public double KernelCpuTimeMs { get; set; }

        public double InstantCpuPercentMachine { get; set; }
        public double InstantCpuPercentPerCore { get; set; }

        public long WorkingSetBytes { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public long PrivateBytes { get; set; }

        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }

        public ulong IoReadBytes { get; set; }
        public ulong IoWriteBytes { get; set; }
        public ulong IoReadOps { get; set; }
        public ulong IoWriteOps { get; set; }

        public long? ManagedMemoryBytes { get; set; }
    }

    public sealed class MetricStatistics
    {
        public int Count { get; set; }
        public double Mean { get; set; }
        public double Median { get; set; } // P50
        public double P95 { get; set; }
        public double P99 { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double StartValue { get; set; }
        public double EndValue { get; set; }
        public double Delta { get; set; } // End - Start
        public double TrendPerSecond { get; set; } // Rate of change per second
        public double StandardDeviation { get; set; }
        public double CoefficientOfVariation => Mean != 0 ? StandardDeviation / Math.Abs(Mean) : 0;

        public static MetricStatistics Calculate(IReadOnlyList<double> values, double durationSeconds)
        {
            if (values == null || values.Count == 0)
            {
                return new MetricStatistics();
            }

            var sorted = values.OrderBy(v => v).ToList();
            int count = sorted.Count;

            double mean = values.Average();
            double min = sorted[0];
            double max = sorted[count - 1];
            double median = Percentile(sorted, 0.50);
            double p95 = Percentile(sorted, 0.95);
            double p99 = Percentile(sorted, 0.99);

            double start = values[0];
            double end = values[count - 1];
            double delta = end - start;
            double trend = durationSeconds > 0 ? delta / durationSeconds : 0;

            double sumSq = values.Sum(v => (v - mean) * (v - mean));
            double stdDev = count > 1 ? Math.Sqrt(sumSq / (count - 1)) : 0;

            return new MetricStatistics
            {
                Count = count,
                Mean = mean,
                Median = median,
                P95 = p95,
                P99 = p99,
                Min = min,
                Max = max,
                StartValue = start,
                EndValue = end,
                Delta = delta,
                TrendPerSecond = trend,
                StandardDeviation = stdDev
            };
        }

        public static MetricStatistics CalculateTimeWeighted(IReadOnlyList<ProcessSample> samples, Func<ProcessSample, double> selector, double seconds)
        {
            var result = Calculate(samples.Select(selector).ToArray(), seconds);
            double area = 0, span = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                double delta = samples[i].ElapsedMs - samples[i - 1].ElapsedMs;
                if (!double.IsFinite(delta) || delta <= 0) continue;
                area += (selector(samples[i - 1]) + selector(samples[i])) * 0.5 * delta;
                span += delta;
            }
            if (span > 0) result.Mean = area / span;
            // Dispersion/quantiles remain sample statistics, explicitly described in the summary.
            return result;
        }

        private static double Percentile(List<double> sorted, double percentile)
        {
            if (sorted.Count == 1) return sorted[0];
            double index = percentile * (sorted.Count - 1);
            int low = (int)Math.Floor(index);
            int high = (int)Math.Ceiling(index);
            if (low == high) return sorted[low];
            double fraction = index - low;
            return sorted[low] * (1.0 - fraction) + sorted[high] * fraction;
        }
    }

    public sealed class ProcessMetricSummary
    {
        public double DurationSeconds { get; set; }
        public int SampleCount { get; set; }
        public SamplingQuality? SamplingQuality { get; set; }
        public string ResourceMeanNotes { get; set; } = "Resource means use trapezoidal time weighting; percentiles/dispersion describe collected samples, peaks may miss activity between samples.";

        // Monotonic, ground-truth cumulative CPU time
        public double TotalCpuTimeMs { get; set; }
        public double UserCpuTimeMs { get; set; }
        public double KernelCpuTimeMs { get; set; }

        public MetricStatistics CpuPercentMachine { get; set; } = new();
        public MetricStatistics CpuPercentPerCore { get; set; } = new();

        public MetricStatistics WorkingSetBytes { get; set; } = new();
        public long PeakWorkingSetBytes { get; set; }
        public MetricStatistics PrivateBytes { get; set; } = new();

        public MetricStatistics ThreadCount { get; set; } = new();
        public MetricStatistics HandleCount { get; set; } = new();

        public ulong IoReadBytesTotal { get; set; }
        public ulong IoWriteBytesTotal { get; set; }
        public ulong IoReadOpsTotal { get; set; }
        public ulong IoWriteOpsTotal { get; set; }

        // Explicit note for unmeasured/managed metric
        public string ManagedMemoryNotes { get; set; } = "Unmeasured: External process; managed heap requires in-proc profiler or CLR ETW.";

        public double? StartupLatencyMs { get; set; }
        public double? ResponseLatencyMs { get; set; }

        // Benchmark tool self-overhead calibration
        public double MonitorSelfCpuTimeMs { get; set; }
        public string MonitorCpuNotes { get; set; } = "Whole BenchmarkLab process CPU during measurement, including workload generation; not isolated sampler overhead.";
        public string StartupLatencyNotes { get; set; } = "WaitForInputIdle proxy only; does not establish a visible, rendered or data-ready window.";
        public bool MeasurementValid { get; set; } = true;
        public string? MeasurementError { get; set; }
        public bool ProcessExitedDuringMeasurement { get; set; }
        public double CpuTimePerSecondMs => DurationSeconds > 0 ? TotalCpuTimeMs / DurationSeconds : 0;

        public static ProcessMetricSummary Aggregate(IReadOnlyList<ProcessSample> samples, double durationSeconds, double monitorSelfCpuMs = 0)
        {
            if (samples == null || samples.Count == 0)
            {
                return new ProcessMetricSummary
                {
                    DurationSeconds = durationSeconds,
                    MeasurementValid = false,
                    MeasurementError = "No samples collected.",
                    MonitorSelfCpuTimeMs = monitorSelfCpuMs
                };
            }

            var first = samples[0];
            var last = samples[samples.Count - 1];

            double totalCpu = Math.Max(0, last.TotalCpuTimeMs - first.TotalCpuTimeMs);
            double userCpu = Math.Max(0, last.UserCpuTimeMs - first.UserCpuTimeMs);
            double kernelCpu = Math.Max(0, last.KernelCpuTimeMs - first.KernelCpuTimeMs);

            long peakWs = samples.Max(s => s.PeakWorkingSetBytes);
            if (peakWs == 0) peakWs = samples.Max(s => s.WorkingSetBytes);

            ulong ioReadTotal = last.IoReadBytes >= first.IoReadBytes ? last.IoReadBytes - first.IoReadBytes : 0;
            ulong ioWriteTotal = last.IoWriteBytes >= first.IoWriteBytes ? last.IoWriteBytes - first.IoWriteBytes : 0;
            ulong ioReadOps = last.IoReadOps >= first.IoReadOps ? last.IoReadOps - first.IoReadOps : 0;
            ulong ioWriteOps = last.IoWriteOps >= first.IoWriteOps ? last.IoWriteOps - first.IoWriteOps : 0;

            return new ProcessMetricSummary
            {
                DurationSeconds = durationSeconds,
                MeasurementValid = samples.Count >= 3 && durationSeconds >= 0.5,
                SampleCount = samples.Count,
                TotalCpuTimeMs = totalCpu,
                UserCpuTimeMs = userCpu,
                KernelCpuTimeMs = kernelCpu,
                // The initial endpoint has no interval. Exclude its artificial zero;
                // the overall mean uses cumulative CPU / actual wall duration below.
                CpuPercentMachine = CpuStatistics(samples, durationSeconds, totalCpu, Environment.ProcessorCount),
                CpuPercentPerCore = CpuStatistics(samples, durationSeconds, totalCpu, 1),
                WorkingSetBytes = MetricStatistics.CalculateTimeWeighted(samples, s => s.WorkingSetBytes, durationSeconds),
                PeakWorkingSetBytes = peakWs,
                PrivateBytes = MetricStatistics.CalculateTimeWeighted(samples, s => s.PrivateBytes, durationSeconds),
                ThreadCount = MetricStatistics.CalculateTimeWeighted(samples, s => s.ThreadCount, durationSeconds),
                HandleCount = MetricStatistics.CalculateTimeWeighted(samples, s => s.HandleCount, durationSeconds),
                IoReadBytesTotal = ioReadTotal,
                IoWriteBytesTotal = ioWriteTotal,
                IoReadOpsTotal = ioReadOps,
                IoWriteOpsTotal = ioWriteOps,
                MonitorSelfCpuTimeMs = monitorSelfCpuMs
            };
        }

        private static MetricStatistics CpuStatistics(IReadOnlyList<ProcessSample> samples, double seconds, double cpuMs, int cores)
        {
            var values = samples.Skip(1).Select(s => cores == 1 ? s.InstantCpuPercentPerCore : s.InstantCpuPercentMachine).ToArray();
            var result = MetricStatistics.Calculate(values, seconds);
            result.Mean = seconds > 0 ? cpuMs / (seconds * 1000) * 100 / cores : 0;
            return result;
        }
    }
}
