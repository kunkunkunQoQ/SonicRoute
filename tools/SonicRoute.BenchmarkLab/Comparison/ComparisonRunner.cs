using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.BenchmarkLab.Core;
using SonicRoute.BenchmarkLab.Scenarios;
using SonicRoute.BenchmarkLab.Storage;

namespace SonicRoute.BenchmarkLab.Comparison
{
    public sealed class BenchmarkRunner
    {
        public static async Task<Dictionary<string, ProcessMetricSummary>> RunSuiteAsync(
            string targetPath,
            IReadOnlyList<IBenchmarkScenario> scenarios,
            BenchmarkTier tier,
            int rounds,
            double? customDurationSeconds,
            Action<string>? logger,
            CancellationToken token)
        {
            var resultsByScenario = new Dictionary<string, List<ProcessMetricSummary>>();

            for (int r = 1; r <= rounds; r++)
            {
                logger?.Invoke($"--- Running Target Round {r}/{rounds} ---");

                foreach (var scenario in scenarios)
                {
                    if (token.IsCancellationRequested) break;

                    logger?.Invoke($"Executing scenario '{scenario.Name}' (Tier={tier})...");
                    var context = new BenchmarkContext
                    {
                        TargetExecutable = targetPath,
                        Tier = tier,
                        CustomDurationSeconds = customDurationSeconds,
                        CancellationToken = token,
                        LogMessage = logger
                    };

                    var scenarioResult = await scenario.RunAsync(context);
                    if (!resultsByScenario.ContainsKey(scenario.Id))
                    {
                        resultsByScenario[scenario.Id] = new List<ProcessMetricSummary>();
                    }
                    resultsByScenario[scenario.Id].Add(scenarioResult.Metrics);

                    // Brief cooldown between scenarios
                    await Task.Delay(1000, token);
                }
            }

            var averaged = new Dictionary<string, ProcessMetricSummary>();
            foreach (var kvp in resultsByScenario)
            {
                averaged[kvp.Key] = AverageSummaries(kvp.Value);
            }
            return averaged;
        }

        public static ProcessMetricSummary AverageSummaries(IReadOnlyList<ProcessMetricSummary> summaries)
        {
            if (summaries == null || summaries.Count == 0) return new ProcessMetricSummary();
            if (summaries.Count == 1) return summaries[0];

            return new ProcessMetricSummary
            {
                DurationSeconds = summaries.Average(s => s.DurationSeconds),
                SampleCount = (int)summaries.Average(s => s.SampleCount),
                TotalCpuTimeMs = summaries.Average(s => s.TotalCpuTimeMs),
                UserCpuTimeMs = summaries.Average(s => s.UserCpuTimeMs),
                KernelCpuTimeMs = summaries.Average(s => s.KernelCpuTimeMs),
                CpuPercentMachine = new MetricStatistics
                {
                    Mean = summaries.Average(s => s.CpuPercentMachine.Mean),
                    Median = summaries.Average(s => s.CpuPercentMachine.Median),
                    P95 = summaries.Average(s => s.CpuPercentMachine.P95),
                    Max = summaries.Average(s => s.CpuPercentMachine.Max)
                },
                CpuPercentPerCore = new MetricStatistics
                {
                    Mean = summaries.Average(s => s.CpuPercentPerCore.Mean),
                    Median = summaries.Average(s => s.CpuPercentPerCore.Median),
                    P95 = summaries.Average(s => s.CpuPercentPerCore.P95),
                    Max = summaries.Average(s => s.CpuPercentPerCore.Max)
                },
                WorkingSetBytes = new MetricStatistics
                {
                    Mean = summaries.Average(s => s.WorkingSetBytes.Mean),
                    Median = summaries.Average(s => s.WorkingSetBytes.Median),
                    Max = summaries.Average(s => s.WorkingSetBytes.Max)
                },
                PeakWorkingSetBytes = (long)summaries.Average(s => s.PeakWorkingSetBytes),
                PrivateBytes = new MetricStatistics
                {
                    Mean = summaries.Average(s => s.PrivateBytes.Mean),
                    Median = summaries.Average(s => s.PrivateBytes.Median),
                    Max = summaries.Average(s => s.PrivateBytes.Max),
                    Delta = summaries.Average(s => s.PrivateBytes.Delta)
                },
                ThreadCount = new MetricStatistics
                {
                    Mean = summaries.Average(s => s.ThreadCount.Mean),
                    Max = summaries.Average(s => s.ThreadCount.Max)
                },
                HandleCount = new MetricStatistics
                {
                    Mean = summaries.Average(s => s.HandleCount.Mean),
                    Max = summaries.Average(s => s.HandleCount.Max)
                },
                IoReadBytesTotal = (ulong)summaries.Average(s => (double)s.IoReadBytesTotal),
                IoWriteBytesTotal = (ulong)summaries.Average(s => (double)s.IoWriteBytesTotal),
                IoReadOpsTotal = (ulong)summaries.Average(s => (double)s.IoReadOpsTotal),
                IoWriteOpsTotal = (ulong)summaries.Average(s => (double)s.IoWriteOpsTotal),
                StartupLatencyMs = summaries.Any(s => s.StartupLatencyMs.HasValue) ? summaries.Where(s => s.StartupLatencyMs.HasValue).Average(s => s.StartupLatencyMs!.Value) : null,
                MonitorSelfCpuTimeMs = summaries.Average(s => s.MonitorSelfCpuTimeMs)
            };
        }
    }

    public sealed class ComparisonRunner
    {
        public static async Task<SuiteComparisonResult> RunComparisonAsync(
            string baselineTarget,
            string candidateTarget,
            IReadOnlyList<IBenchmarkScenario> scenarios,
            BenchmarkTier tier = BenchmarkTier.Standard,
            int rounds = 3,
            double? customDurationSeconds = null,
            double cpuThresholdPercent = 5.0,
            double memoryThresholdPercent = 10.0,
            Action<string>? logger = null,
            CancellationToken token = default)
        {
            var suiteResult = new SuiteComparisonResult
            {
                BaselineTarget = baselineTarget,
                CandidateTarget = candidateTarget,
                Rounds = rounds,
                Environment = SystemEnvironmentInfo.CaptureCurrent()
            };

            // Check if baselineTarget is a stored baseline name
            var storedBaseline = BaselineStore.Load(baselineTarget);
            Dictionary<string, ProcessMetricSummary> baselineSummaries;

            if (storedBaseline != null)
            {
                logger?.Invoke($"Loaded stored baseline '{baselineTarget}' (Saved on {storedBaseline.SavedUtc:yyyy-MM-dd HH:mm:ss} UTC).");
                suiteResult.BaselineVersion = storedBaseline.Version;

                // Environment compatibility check
                if (!suiteResult.Environment.IsCompatibleWith(storedBaseline.Environment, out string compatWarning))
                {
                    logger?.Invoke($"[WARNING] {compatWarning}");
                    suiteResult.Limitations.Add(compatWarning);
                }

                baselineSummaries = storedBaseline.Scenarios;

                // Run candidate
                logger?.Invoke($"Running candidate target: {candidateTarget} for {rounds} round(s)...");
                var candidateSummaries = await BenchmarkRunner.RunSuiteAsync(
                    candidateTarget, scenarios, tier, rounds, customDurationSeconds, logger, token);

                EvaluateComparison(suiteResult, scenarios, baselineSummaries, candidateSummaries, cpuThresholdPercent, memoryThresholdPercent, rounds);
                return suiteResult;
            }

            // Both are executables: run alternating A/B test rounds
            if (!File.Exists(baselineTarget))
            {
                throw new FileNotFoundException($"Baseline executable not found: {baselineTarget}");
            }
            if (!File.Exists(candidateTarget))
            {
                throw new FileNotFoundException($"Candidate executable not found: {candidateTarget}");
            }

            suiteResult.BaselineVersion = GetFileVersion(baselineTarget);
            suiteResult.CandidateVersion = GetFileVersion(candidateTarget);

            var baselineRounds = new Dictionary<string, List<ProcessMetricSummary>>();
            var candidateRounds = new Dictionary<string, List<ProcessMetricSummary>>();

            for (int r = 1; r <= rounds; r++)
            {
                bool baselineFirst = (r % 2 != 0);
                string firstTarget = baselineFirst ? baselineTarget : candidateTarget;
                string secondTarget = baselineFirst ? candidateTarget : baselineTarget;
                string firstName = baselineFirst ? "Baseline" : "Candidate";
                string secondName = baselineFirst ? "Candidate" : "Baseline";

                logger?.Invoke($"\n=== Round {r}/{rounds} (Order: {firstName} -> {secondName}) ===");

                // Run first target
                await RunTargetForRound(firstTarget, baselineFirst ? baselineRounds : candidateRounds, scenarios, tier, customDurationSeconds, logger, token);

                // Thermal & system cooldown
                logger?.Invoke("Cooldown 1.5s...");
                await Task.Delay(1500, token);

                // Run second target
                await RunTargetForRound(secondTarget, baselineFirst ? candidateRounds : baselineRounds, scenarios, tier, customDurationSeconds, logger, token);

                if (r < rounds)
                {
                    logger?.Invoke("Round cooldown 2.0s...");
                    await Task.Delay(2000, token);
                }
            }

            // Average across rounds
            suiteResult.BaselineRounds = baselineRounds;
            suiteResult.CandidateRounds = candidateRounds;
            var avgBaseline = baselineRounds.ToDictionary(k => k.Key, v => BenchmarkRunner.AverageSummaries(v.Value));
            var avgCandidate = candidateRounds.ToDictionary(k => k.Key, v => BenchmarkRunner.AverageSummaries(v.Value));

            EvaluateComparison(suiteResult, scenarios, avgBaseline, avgCandidate, cpuThresholdPercent, memoryThresholdPercent, rounds);
            return suiteResult;
        }

        private static async Task RunTargetForRound(
            string targetPath,
            Dictionary<string, List<ProcessMetricSummary>> collector,
            IReadOnlyList<IBenchmarkScenario> scenarios,
            BenchmarkTier tier,
            double? customDurationSeconds,
            Action<string>? logger,
            CancellationToken token)
        {
            foreach (var scenario in scenarios)
            {
                if (token.IsCancellationRequested) break;

                var context = new BenchmarkContext
                {
                    TargetExecutable = targetPath,
                    Tier = tier,
                    CustomDurationSeconds = customDurationSeconds,
                    CancellationToken = token,
                    LogMessage = logger
                };

                var res = await scenario.RunAsync(context);
                if (!collector.ContainsKey(scenario.Id)) collector[scenario.Id] = new List<ProcessMetricSummary>();
                collector[scenario.Id].Add(res.Metrics);

                await Task.Delay(800, token);
            }
        }

        private static void EvaluateComparison(
            SuiteComparisonResult suite,
            IReadOnlyList<IBenchmarkScenario> scenarios,
            Dictionary<string, ProcessMetricSummary> baselineSummaries,
            Dictionary<string, ProcessMetricSummary> candidateSummaries,
            double cpuThreshold,
            double memThreshold,
            int rounds)
        {
            bool anyRegression = false;

            foreach (var scenario in scenarios)
            {
                if (!baselineSummaries.TryGetValue(scenario.Id, out var bSummary) ||
                    !candidateSummaries.TryGetValue(scenario.Id, out var cSummary))
                {
                    continue;
                }

                var scenarioResult = RegressionEvaluator.EvaluateScenario(
                    scenario.Id,
                    scenario.Name,
                    bSummary,
                    cSummary,
                    cpuThreshold,
                    memThreshold,
                    rounds);

                suite.Scenarios.Add(scenarioResult);

                if (scenarioResult.Verdict == OverallVerdict.Inconclusive)
                {
                    suite.Limitations.Add($"[{scenario.Name}] {scenarioResult.Notes}");
                }
                else if (scenarioResult.Verdict == OverallVerdict.RegressionDetected)
                {
                    anyRegression = true;
                    foreach (var m in scenarioResult.MetricComparisons.Where(mc => mc.Verdict == MetricVerdict.Regressed))
                    {
                        suite.RegressionsSummary.Add($"[{scenario.Name}] {m.MetricName} {m.Details}");
                    }
                }

                foreach (var m in scenarioResult.MetricComparisons.Where(mc => mc.Verdict == MetricVerdict.Improved))
                {
                    suite.ImprovementsSummary.Add($"[{scenario.Name}] {m.MetricName} {m.Details}");
                }
            }

            suite.OverallVerdict = anyRegression ? OverallVerdict.RegressionDetected
                : suite.Scenarios.Any(s => s.Verdict == OverallVerdict.Inconclusive) ? OverallVerdict.Inconclusive
                : OverallVerdict.Pass;

            if (rounds < 3)
            {
                suite.Limitations.Add($"Executed {rounds} round(s); 3+ rounds recommended for production regression gating to minimize background OS noise.");
            }
        }

        private static string GetFileVersion(string path)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return vi.FileVersion ?? vi.ProductVersion ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        public static string ComputeFileSha256(string path)
        {
            try
            {
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(path);
                byte[] hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch
            {
                return "unknown_sha256";
            }
        }
    }
}
