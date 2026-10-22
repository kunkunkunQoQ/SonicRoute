using System;
using System.Collections.Generic;
using SonicRoute.BenchmarkLab.Core;

namespace SonicRoute.BenchmarkLab.Comparison
{
    public enum MetricVerdict
    {
        Stable,          // Within noise margin / no significant difference
        Improved,        // Meaningfully better
        Regressed,       // Meaningfully worse (exceeds regression threshold)
        Inconclusive,    // Insufficient data or excessive noise
        Unmeasured       // Metric not measured
    }

    public enum OverallVerdict
    {
        Pass,                // No regressions detected
        RegressionDetected,  // One or more metrics regressed beyond threshold
        Inconclusive         // Data insufficient / conflicting
    }

    public sealed class MetricComparisonResult
    {
        public string MetricName { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public double BaselineValue { get; set; }
        public double CandidateValue { get; set; }
        public double DeltaAbsolute { get; set; }
        public double DeltaPercent { get; set; }
        public bool DeltaPercentDefined { get; set; } = true;
        public double ThresholdPercent { get; set; }
        public MetricVerdict Verdict { get; set; } = MetricVerdict.Stable;
        public string Details { get; set; } = string.Empty;
    }

    public sealed class ScenarioComparisonResult
    {
        public string ScenarioId { get; set; } = string.Empty;
        public string ScenarioName { get; set; } = string.Empty;
        public ProcessMetricSummary BaselineSummary { get; set; } = new();
        public ProcessMetricSummary CandidateSummary { get; set; } = new();
        public List<MetricComparisonResult> MetricComparisons { get; set; } = new();
        public OverallVerdict Verdict { get; set; } = OverallVerdict.Pass;
        public string Notes { get; set; } = string.Empty;
    }

    public sealed class SuiteComparisonResult
    {
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string BaselineTarget { get; set; } = string.Empty;
        public string CandidateTarget { get; set; } = string.Empty;
        public string BaselineVersion { get; set; } = string.Empty;
        public string CandidateVersion { get; set; } = string.Empty;
        public int Rounds { get; set; }
        public SystemEnvironmentInfo Environment { get; set; } = SystemEnvironmentInfo.CaptureCurrent();
        public List<ScenarioComparisonResult> Scenarios { get; set; } = new();
        // Preserve variation across real alternating rounds; averages alone cannot establish repeatability.
        public Dictionary<string, List<ProcessMetricSummary>> BaselineRounds { get; set; } = new();
        public Dictionary<string, List<ProcessMetricSummary>> CandidateRounds { get; set; } = new();
        public OverallVerdict OverallVerdict { get; set; } = OverallVerdict.Pass;
        public List<string> RegressionsSummary { get; set; } = new();
        public List<string> ImprovementsSummary { get; set; } = new();
        public List<string> Limitations { get; set; } = new();
        public string ReportJsonPath { get; set; } = string.Empty;
        public string ReportHtmlPath { get; set; } = string.Empty;
        public string AiSummaryMdPath { get; set; } = string.Empty;
    }

    public static class RegressionEvaluator
    {
        // Noise floors below which small absolute differences are ignored
        private const double CpuTimeNoiseFloorMs = 30.0;
        private const double MemoryNoiseFloorBytes = 2 * 1024 * 1024; // 2 MB
        private const int HandleNoiseFloor = 8;
        private const int ThreadNoiseFloor = 2;

        public static ScenarioComparisonResult EvaluateScenario(
            string scenarioId,
            string scenarioName,
            ProcessMetricSummary baseline,
            ProcessMetricSummary candidate,
            double cpuThresholdPercent = 5.0,
            double memoryThresholdPercent = 10.0,
            int rounds = 1)
        {
            var result = new ScenarioComparisonResult
            {
                ScenarioId = scenarioId,
                ScenarioName = scenarioName,
                BaselineSummary = baseline,
                CandidateSummary = candidate
            };

            // Check for premature exit or insufficient sampling
            if (baseline.SampleCount < 3 || baseline.DurationSeconds < 0.5)
            {
                result.Verdict = OverallVerdict.Inconclusive;
                result.Notes = $"Baseline process exited prematurely or insufficient samples collected (SampleCount={baseline.SampleCount}, Duration={baseline.DurationSeconds:F1}s). Likely single-instance mutex collision or early termination.";
                return result;
            }

            if (candidate.SampleCount < 3 || candidate.DurationSeconds < 0.5)
            {
                result.Verdict = OverallVerdict.Inconclusive;
                result.Notes = $"Candidate process exited prematurely or insufficient samples collected (SampleCount={candidate.SampleCount}, Duration={candidate.DurationSeconds:F1}s). Likely single-instance mutex collision or early crash.";
                return result;
            }

            bool hasRegression = false;

            // 1. Total Cumulative CPU Time (monotonic ground truth)
            var cpuComp = CompareMetric(
                "Total CPU Time",
                "ms",
                baseline.TotalCpuTimeMs,
                candidate.TotalCpuTimeMs,
                cpuThresholdPercent,
                CpuTimeNoiseFloorMs,
                lowerIsBetter: true);
            result.MetricComparisons.Add(cpuComp);
            if (cpuComp.Verdict == MetricVerdict.Regressed) hasRegression = true;

            // 2. Private Bytes (Memory allocated to process)
            var privMemComp = CompareMetric(
                "Private Memory",
                "MB",
                baseline.PrivateBytes.Mean / (1024 * 1024),
                candidate.PrivateBytes.Mean / (1024 * 1024),
                memoryThresholdPercent,
                MemoryNoiseFloorBytes / (1024 * 1024),
                lowerIsBetter: true);
            result.MetricComparisons.Add(privMemComp);
            if (privMemComp.Verdict == MetricVerdict.Regressed) hasRegression = true;

            // 3. Peak Working Set
            var peakWsComp = CompareMetric(
                "Peak Working Set",
                "MB",
                baseline.PeakWorkingSetBytes / (1024.0 * 1024.0),
                candidate.PeakWorkingSetBytes / (1024.0 * 1024.0),
                memoryThresholdPercent,
                MemoryNoiseFloorBytes / (1024.0 * 1024.0),
                lowerIsBetter: true);
            result.MetricComparisons.Add(peakWsComp);
            if (peakWsComp.Verdict == MetricVerdict.Regressed) hasRegression = true;

            // 4. Handle Count
            var handleComp = CompareMetric(
                "Handle Count",
                "handles",
                baseline.HandleCount.Mean,
                candidate.HandleCount.Mean,
                thresholdPercent: 15.0,
                absoluteNoiseFloor: HandleNoiseFloor,
                lowerIsBetter: true);
            result.MetricComparisons.Add(handleComp);
            if (handleComp.Verdict == MetricVerdict.Regressed) hasRegression = true;

            // 5. Thread Count
            var threadComp = CompareMetric(
                "Thread Count",
                "threads",
                baseline.ThreadCount.Mean,
                candidate.ThreadCount.Mean,
                thresholdPercent: 20.0,
                absoluteNoiseFloor: ThreadNoiseFloor,
                lowerIsBetter: true);
            result.MetricComparisons.Add(threadComp);
            if (threadComp.Verdict == MetricVerdict.Regressed) hasRegression = true;

            // 6. Startup Latency (if measured)
            if (baseline.StartupLatencyMs.HasValue && candidate.StartupLatencyMs.HasValue)
            {
                var startupComp = CompareMetric(
                    "Startup Latency",
                    "ms",
                    baseline.StartupLatencyMs.Value,
                    candidate.StartupLatencyMs.Value,
                    thresholdPercent: 15.0,
                    absoluteNoiseFloor: 50.0,
                    lowerIsBetter: true);
                result.MetricComparisons.Add(startupComp);
                if (startupComp.Verdict == MetricVerdict.Regressed) hasRegression = true;
            }

            // Flag low confidence if only 1 round was run
            if (rounds <= 1)
            {
                result.Notes = "Single-round test: Results subject to system scheduling variance. Multi-round recommended for final verification.";
            }

            result.Verdict = hasRegression ? OverallVerdict.RegressionDetected : OverallVerdict.Pass;
            return result;
        }

        private static MetricComparisonResult CompareMetric(
            string name,
            string unit,
            double baselineVal,
            double candidateVal,
            double thresholdPercent,
            double absoluteNoiseFloor,
            bool lowerIsBetter = true)
        {
            double deltaAbs = candidateVal - baselineVal;
            bool percentageDefined = baselineVal > 0.0001;
            double deltaPct = percentageDefined ? (deltaAbs / baselineVal) * 100.0 : 0.0;

            MetricVerdict verdict;

            // Check if within absolute noise floor
            if (Math.Abs(deltaAbs) <= absoluteNoiseFloor)
            {
                verdict = MetricVerdict.Stable;
            }
            else if (!percentageDefined)
            {
                // A zero baseline has no meaningful relative percentage. An increase
                // above the unchanged absolute noise floor must not silently pass.
                double effectiveDelta = lowerIsBetter ? deltaAbs : -deltaAbs;
                verdict = effectiveDelta > 0 ? MetricVerdict.Regressed : MetricVerdict.Improved;
            }
            else
            {
                double effectiveDeltaPct = lowerIsBetter ? deltaPct : -deltaPct;

                if (effectiveDeltaPct > thresholdPercent)
                {
                    verdict = MetricVerdict.Regressed;
                }
                else if (effectiveDeltaPct < -thresholdPercent)
                {
                    verdict = MetricVerdict.Improved;
                }
                else
                {
                    verdict = MetricVerdict.Stable;
                }
            }

            string details = verdict switch
            {
                MetricVerdict.Regressed => $"Regression: changed by {deltaPct:+0.0;-0.0}% (threshold is +{thresholdPercent:0}%)",
                MetricVerdict.Improved => $"Improvement: changed by {deltaPct:+0.0;-0.0}%",
                MetricVerdict.Stable => $"Within noise margin ({deltaPct:+0.0;-0.0}%, delta={deltaAbs:+0.00;-0.00} {unit})",
                _ => "Uncertain"
            };
            if (!percentageDefined)
                details = $"{verdict}: baseline is zero; percentage undefined; absolute delta={deltaAbs:+0.00;-0.00} {unit} (noise floor {absoluteNoiseFloor:0.00} {unit})";

            return new MetricComparisonResult
            {
                MetricName = name,
                Unit = unit,
                BaselineValue = baselineVal,
                CandidateValue = candidateVal,
                DeltaAbsolute = deltaAbs,
                DeltaPercent = deltaPct,
                DeltaPercentDefined = percentageDefined,
                ThresholdPercent = thresholdPercent,
                Verdict = verdict,
                Details = details
            };
        }
    }
}
