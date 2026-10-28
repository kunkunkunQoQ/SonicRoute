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
            CancellationToken token,
            List<ScenarioResult>? evidence = null,
            int sampleIntervalMs = 200, double? customWarmupSeconds = null)
        {
            var resultsByScenario = new Dictionary<string, List<ProcessMetricSummary>>();

            for (int r = 1; r <= rounds; r++)
            {
                logger?.Invoke($"--- Running Target Round {r}/{rounds} ---");

                foreach (var scenario in scenarios)
                {
                    token.ThrowIfCancellationRequested();

                    logger?.Invoke($"Executing scenario '{scenario.Name}' (Tier={tier})...");
                    var context = new BenchmarkContext
                    {
                        TargetExecutable = targetPath,
                        Tier = tier,
                        CustomDurationSeconds = customDurationSeconds,
                        CustomWarmupSeconds = customWarmupSeconds,
                        SampleIntervalMs = sampleIntervalMs,
                        CancellationToken = token,
                        LogMessage = logger
                    };

                    var scenarioResult = await RunScenarioAsync(scenario, context);
                    evidence?.Add(scenarioResult);
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

        internal static async Task<ScenarioResult> RunScenarioAsync(IBenchmarkScenario scenario, BenchmarkContext context)
        {
            var started = DateTime.UtcNow;
            ScenarioResult result;
            try { result = await scenario.RunAsync(context); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                result = new ScenarioResult { ScenarioId = scenario.Id, ScenarioName = scenario.Name,
                    TargetPath = context.TargetExecutable, Success = false, ErrorMessage = error.ToString() };
            }
            result.StartedUtc = started;
            result.CompletedUtc = DateTime.UtcNow;
            result.SampleIntervalMs = context.SampleIntervalMs;
            if (!result.Metrics.MeasurementValid || result.Metrics.ProcessExitedDuringMeasurement || result.Metrics.SampleCount < 3 || result.Metrics.DurationSeconds < 0.5)
            { result.Success = false; result.ErrorMessage ??= result.Metrics.MeasurementError ?? "Process exited or insufficient samples collected."; }
            if (!result.Success)
            { result.Metrics.MeasurementValid = false; result.Metrics.MeasurementError = result.ErrorMessage ?? "Scenario workload was not verified."; }
            return result;
        }

        public static ProcessMetricSummary AverageSummaries(IReadOnlyList<ProcessMetricSummary> summaries)
        {
            if (summaries == null || summaries.Count == 0) return new ProcessMetricSummary { MeasurementValid = false, MeasurementError = "No rounds." };
            if (summaries.Count == 1) return summaries[0];

            return new ProcessMetricSummary
            {
                DurationSeconds = summaries.Average(s => s.DurationSeconds),
                SamplingQuality = new SamplingQuality
                {
                    SuitableForComparison = summaries.All(s => s.SamplingQuality?.SuitableForComparison != false),
                    Warnings = summaries.SelectMany(s => s.SamplingQuality?.Warnings ?? new()).Distinct().ToList()
                },
                MeasurementValid = summaries.All(s => s.MeasurementValid && !s.ProcessExitedDuringMeasurement),
                MeasurementError = string.Join("; ", summaries.Where(s => !s.MeasurementValid || s.ProcessExitedDuringMeasurement).Select(s => s.MeasurementError ?? "Process exited.")),
                ProcessExitedDuringMeasurement = summaries.Any(s => s.ProcessExitedDuringMeasurement),
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
                    Delta = summaries.Average(s => s.PrivateBytes.Delta),
                    TrendPerSecond = summaries.Average(s => s.PrivateBytes.TrendPerSecond)
                },
                ThreadCount = new MetricStatistics
                {
                    Mean = summaries.Average(s => s.ThreadCount.Mean),
                    Max = summaries.Average(s => s.ThreadCount.Max),
                    TrendPerSecond = summaries.Average(s => s.ThreadCount.TrendPerSecond)
                },
                HandleCount = new MetricStatistics
                {
                    Mean = summaries.Average(s => s.HandleCount.Mean),
                    Max = summaries.Average(s => s.HandleCount.Max),
                    TrendPerSecond = summaries.Average(s => s.HandleCount.TrendPerSecond)
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
            CancellationToken token = default,
            int sampleIntervalMs = 200, double? customWarmupSeconds = null, double baselineMaxAgeDays = 7)
        {
            var suiteResult = new SuiteComparisonResult
            {
                BaselineTarget = baselineTarget,
                CandidateTarget = candidateTarget,
                Rounds = rounds,
                Protocol = MeasurementProtocol.Create(scenarios, tier, customDurationSeconds, sampleIntervalMs, customWarmupSeconds),
                Environment = SystemEnvironmentInfo.CaptureCurrent()
            };
            if (rounds < 1 || scenarios.Count == 0) throw new ArgumentException("At least one round and one scenario are required.");
            suiteResult.CandidateSha256 = ComputeFileSha256(candidateTarget);
            suiteResult.CandidateFiles = CaptureTargetFiles(candidateTarget);

            // Check if baselineTarget is a stored baseline name
            var storedBaseline = BaselineStore.Load(baselineTarget);
            Dictionary<string, ProcessMetricSummary> baselineSummaries;

            if (storedBaseline != null)
            {
                suiteResult.UsesStoredBaseline = true;
                suiteResult.BaselineSavedUtc = storedBaseline.SavedUtc;
                suiteResult.BaselineMaxAgeDays = baselineMaxAgeDays;
                suiteResult.BaselineSha256 = storedBaseline.ExeSha256;
                suiteResult.BaselineFiles = storedBaseline.TargetFiles;
                logger?.Invoke($"Loaded stored baseline '{baselineTarget}' (Saved on {storedBaseline.SavedUtc:yyyy-MM-dd HH:mm:ss} UTC).");
                suiteResult.BaselineVersion = storedBaseline.Version;
                suiteResult.CandidateVersion = GetFileVersion(candidateTarget);

                // Environment compatibility check
                if (!suiteResult.Environment.IsCompatibleWith(storedBaseline.Environment, out string compatWarning))
                {
                    logger?.Invoke($"[WARNING] {compatWarning}");
                    suiteResult.Limitations.Add(compatWarning);
                }

                baselineSummaries = storedBaseline.Scenarios;

                // Run candidate
                logger?.Invoke($"Running candidate target: {candidateTarget} for {rounds} round(s)...");
                var runs = new List<ScenarioResult>();
                var candidateSummaries = await BenchmarkRunner.RunSuiteAsync(
                    candidateTarget, scenarios, tier, rounds, customDurationSeconds, logger, token, runs, sampleIntervalMs, customWarmupSeconds);
                suiteResult.BaselineRounds = storedBaseline.Rounds;
                suiteResult.CandidateRounds = runs.GroupBy(r => r.ScenarioId).ToDictionary(g => g.Key, g => g.Select(r => r.Metrics).ToList());
                suiteResult.Runs = runs.Select((r, i) => new BenchmarkRunEvidence { Side = "Candidate", Round = i / scenarios.Count + 1, Order = i + 1, Result = r }).ToList();

                EvaluateComparison(suiteResult, scenarios, baselineSummaries, candidateSummaries, cpuThresholdPercent, memoryThresholdPercent, rounds);
                if (!suiteResult.Environment.IsCompatibleWith(storedBaseline.Environment, out _) || storedBaseline.SchemaVersion != 3
                    || !suiteResult.Protocol.IsCompatibleWith(storedBaseline.Protocol)
                    || !BaselinePolicy.IsFresh(storedBaseline.SavedUtc, DateTime.UtcNow, baselineMaxAgeDays))
                {
                    if (!BaselinePolicy.IsFresh(storedBaseline.SavedUtc, DateTime.UtcNow, baselineMaxAgeDays))
                        suiteResult.Limitations.Add($"Historical baseline timestamp {storedBaseline.SavedUtc:O} exceeds the {baselineMaxAgeDays} day freshness policy or is invalid/future-dated.");
                    suiteResult.Limitations.Add("Historical baseline environment/protocol/tier/duration is incompatible or lacks complete round evidence; it is readable but not a valid regression gate.");
                    suiteResult.OverallVerdict = OverallVerdict.Inconclusive;
                    foreach (var result in suiteResult.Scenarios) { result.Verdict = OverallVerdict.Inconclusive; foreach (var metric in result.MetricComparisons) metric.Verdict = MetricVerdict.Inconclusive; }
                    suiteResult.RegressionsSummary.Clear(); suiteResult.ImprovementsSummary.Clear();
                }
                FinalizeSourceIdentity(suiteResult, candidateTarget, suiteResult.CandidateFiles);
                suiteResult.DecisionReadiness = DecisionReadiness.Assess(suiteResult);
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
            suiteResult.BaselineSha256 = ComputeFileSha256(baselineTarget);
            suiteResult.BaselineFiles = CaptureTargetFiles(baselineTarget);
            suiteResult.IsAaControl = SameFiles(suiteResult.BaselineFiles, suiteResult.CandidateFiles);
            if (suiteResult.IsAaControl) suiteResult.Limitations.Add("A/A control: both executable SHA256 hashes match. Differences describe test repeatability, not a product optimization.");

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

                // Keep the same scenario's two measurements adjacent to reduce suite-length drift.
                foreach (var scenario in scenarios)
                {
                    var pair = new[] { scenario };
                    await RunTargetForRound(firstTarget, baselineFirst ? baselineRounds : candidateRounds, pair, tier, customDurationSeconds, logger, token, suiteResult.Runs, r, firstName, sampleIntervalMs, customWarmupSeconds);
                    logger?.Invoke("Pair cooldown 1.5s...");
                    await Task.Delay(1500, token);
                    await RunTargetForRound(secondTarget, baselineFirst ? candidateRounds : baselineRounds, pair, tier, customDurationSeconds, logger, token, suiteResult.Runs, r, secondName, sampleIntervalMs, customWarmupSeconds);
                }

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
            FinalizeSourceIdentity(suiteResult, baselineTarget, suiteResult.BaselineFiles);
            FinalizeSourceIdentity(suiteResult, candidateTarget, suiteResult.CandidateFiles);
            suiteResult.DecisionReadiness = DecisionReadiness.Assess(suiteResult);
            return suiteResult;
        }

        private static async Task RunTargetForRound(
            string targetPath,
            Dictionary<string, List<ProcessMetricSummary>> collector,
            IReadOnlyList<IBenchmarkScenario> scenarios,
            BenchmarkTier tier,
            double? customDurationSeconds,
            Action<string>? logger,
            CancellationToken token,
            List<BenchmarkRunEvidence> evidence, int round, string side, int sampleIntervalMs, double? customWarmupSeconds)
        {
            foreach (var scenario in scenarios)
            {
                token.ThrowIfCancellationRequested();

                var context = new BenchmarkContext
                {
                    TargetExecutable = targetPath,
                    Tier = tier,
                    CustomDurationSeconds = customDurationSeconds,
                        CustomWarmupSeconds = customWarmupSeconds,
                    SampleIntervalMs = sampleIntervalMs,
                    CancellationToken = token,
                    LogMessage = logger
                };

                var res = await BenchmarkRunner.RunScenarioAsync(scenario, context);
                evidence.Add(new BenchmarkRunEvidence { Round = round, Side = side, Order = evidence.Count + 1, Result = res });
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
                    suite.Scenarios.Add(new ScenarioComparisonResult { ScenarioId = scenario.Id, ScenarioName = scenario.Name,
                        Verdict = OverallVerdict.Inconclusive, Notes = "Scenario missing from one side; not a pass." });
                    suite.Limitations.Add($"[{scenario.Name}] Missing scenario evidence.");
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
                if (scenarioResult.MetricComparisons.Count > 0)
                {
                    RegressionEvaluator.ApplyRoundEvidence(scenarioResult,
                        suite.BaselineRounds.GetValueOrDefault(scenario.Id) ?? new(),
                        suite.CandidateRounds.GetValueOrDefault(scenario.Id) ?? new(), paired: !suite.UsesStoredBaseline);
                }

                suite.Scenarios.Add(scenarioResult);

                if (scenarioResult.Verdict == OverallVerdict.Inconclusive)
                {
                    scenarioResult.Notes = string.Join("; ", new[] { scenarioResult.Notes }.Concat(scenarioResult.MetricComparisons.Where(m => m.Verdict == MetricVerdict.Inconclusive).Select(m => m.MetricName + ": " + m.Details)).Where(s => !string.IsNullOrEmpty(s)));
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

            suite.OverallVerdict = suite.Scenarios.Count == 0 ? OverallVerdict.Inconclusive : anyRegression ? OverallVerdict.RegressionDetected
                : suite.Scenarios.Any(s => s.Verdict == OverallVerdict.Inconclusive) ? OverallVerdict.Inconclusive
                : OverallVerdict.Pass;

            if (rounds < 3)
            {
                suite.Limitations.Add($"Executed {rounds} round(s); 3+ rounds recommended for production regression gating to minimize background OS noise.");
            }
            if (suite.UsesStoredBaseline) suite.Limitations.Add("Historical rounds are independent, not paired alternating measurements; host conditions may have changed since capture.");
            if (suite.IsAaControl)
            {
                suite.ImprovementsSummary.Clear();
                if (suite.OverallVerdict == OverallVerdict.RegressionDetected)
                {
                    suite.OverallVerdict = OverallVerdict.Inconclusive;
                    suite.Limitations.Add("A/A showed a directional measurement difference. Investigate order/environment effects; identical bytes do not establish a product-version regression.");
                }
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
            catch (Exception error) { throw new IOException("Cannot bind benchmark to executable SHA256: " + path, error); }
        }

        public static Dictionary<string, string> CaptureTargetFiles(string executable)
        {
            string full = Path.GetFullPath(executable), directory = Path.GetDirectoryName(full)!;
            string stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(full));
            var files = new Dictionary<string, string> { ["executable"] = ComputeFileSha256(full) };
            foreach (string suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                if (File.Exists(stem + suffix)) files["entry" + suffix] = ComputeFileSha256(stem + suffix);
            if (File.Exists(full + ".config")) files["exe.config"] = ComputeFileSha256(full + ".config");
            // Bind SonicRoute's adjacent assemblies too, including modern multifile
            // deployments: an unchanged apphost/Core DLL pair is not the same product.
            string description = FileVersionInfo.GetVersionInfo(full).FileDescription ?? "";
            if (description.Contains("SonicRoute", StringComparison.OrdinalIgnoreCase))
                foreach (var file in Directory.EnumerateFiles(directory, "*.dll")) files[Path.GetFileName(file)] = ComputeFileSha256(file);
            return files;
        }

        private static bool SameFiles(Dictionary<string, string> a, Dictionary<string, string> b) =>
            a.Count == b.Count && a.All(entry => b.TryGetValue(entry.Key, out var hash) && hash == entry.Value);

        public static void VerifyFilesUnchanged(string executable, Dictionary<string, string> expected)
        {
            if (!SameFiles(expected, CaptureTargetFiles(executable)))
                throw new InvalidDataException("Benchmark target or its local managed payload changed while running; do not use this evidence.");
        }

        private static void FinalizeSourceIdentity(SuiteComparisonResult result, string target, Dictionary<string, string> files)
        {
            try { VerifyFilesUnchanged(target, files); }
            catch (IOException error)
            {
                result.SourceFilesUnchanged = false;
                result.OverallVerdict = OverallVerdict.Inconclusive;
                result.Limitations.Add(error.Message);
                foreach (var scenario in result.Scenarios)
                { scenario.Verdict = OverallVerdict.Inconclusive; foreach (var metric in scenario.MetricComparisons) metric.Verdict = MetricVerdict.Inconclusive; }
                result.ImprovementsSummary.Clear(); result.RegressionsSummary.Clear();
            }
        }
    }
}
