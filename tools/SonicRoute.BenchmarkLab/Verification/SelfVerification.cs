using System.Diagnostics;
using System.Text.Json;
using SonicRoute.BenchmarkLab.Comparison;
using SonicRoute.BenchmarkLab.Core;
using SonicRoute.BenchmarkLab.Reporting;
using SonicRoute.BenchmarkLab.Scenarios;
using SonicRoute.BenchmarkLab.Storage;

namespace SonicRoute.BenchmarkLab.Verification;

public static class SelfVerification
{
    private static int _passed, _failed;
    private static HashSet<string>? _selectedFeatures;
    public static readonly string[] Features = { "cpu", "statistics", "sampling", "protocol", "reports", "sandbox", "cleanup", "lifecycle" };
    private static bool Selected(string feature) => _selectedFeatures == null || _selectedFeatures.Contains(feature);
    private static void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException(name);
        _passed++; Console.WriteLine("PASS " + name);
    }
    private static void Case(string name, Action action)
    {
        string feature = name switch
        {
            "CPU window normalization" or "Low CPU and zero baseline" => "cpu",
            "Real signals and round noise" or "Paired and Welch intervals" => "statistics",
            "Invalid measurement" or "Weighted CPU endpoints" or "Time weighted means and gap quality" => "sampling",
            "Reports preserve uncertainty" or "AI report publishing" => "reports",
            "Stable rule IDs and real JSONL" => "sandbox",
            "Owned temporary cleanup" => "cleanup",
            _ => "protocol"
        };
        if (!Selected(feature)) return;
        try { action(); } catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static ProcessMetricSummary Summary(double cpu = 100, double duration = 10, double memoryMb = 50) => new()
    {
        DurationSeconds = duration, SampleCount = 100, TotalCpuTimeMs = cpu,
        PrivateBytes = new() { Count = 100, Mean = memoryMb * 1024 * 1024, Max = memoryMb * 1024 * 1024 },
        PeakWorkingSetBytes = (long)(memoryMb * 1.2 * 1024 * 1024),
        ThreadCount = new() { Count = 100, Mean = 12, Max = 12 }, HandleCount = new() { Count = 100, Mean = 100, Max = 100 }
    };
    private static ScenarioComparisonResult Compare(ProcessMetricSummary a, ProcessMetricSummary b) => RegressionEvaluator.EvaluateScenario("fixture", "Fixture", a, b, 5, 10, 3);
    private static MetricComparisonResult Cpu(ScenarioComparisonResult result) => result.MetricComparisons.Single(m => m.MetricName == "Total CPU Time");

    public static async Task<int> RunAllAsync(string? realSonicRouteExe = null, string? evidenceDirectory = null, string? features = null)
    {
        _selectedFeatures = features == null ? null : features.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_selectedFeatures?.Any(f => !Features.Contains(f, StringComparer.OrdinalIgnoreCase)) == true)
            throw new ArgumentException("Unknown verify feature. Supported: " + string.Join(", ", Features));
        _passed = _failed = 0;
        Console.WriteLine("BenchmarkLab 1.2: protocol, sampling quality, statistics, reports and owned native-process checks");
        Case("CPU window normalization", () => {
            var result = Compare(Summary(100, 10), Summary(200, 20));
            Check(Cpu(result).DeltaAbsolute == 0 && Cpu(result).Verdict == MetricVerdict.Stable, "unequal windows normalize CPU rates without changing raw totals");
            Check(result.CandidateSummary.TotalCpuTimeMs == 200, "raw CPU remains auditable");
        });
        Case("Low CPU and zero baseline", () => {
            var result = Compare(Summary(0), Summary(15.625));
            Check(Cpu(result).Verdict == MetricVerdict.Inconclusive && result.Verdict == OverallVerdict.Inconclusive, "sub-30ms change is inconclusive rather than a false stable pass");
            Check(!Cpu(result).DeltaPercentDefined && Cpu(result).Details.Contains("percentage undefined"), "zero baseline has no invented percentage");
            Check(Cpu(Compare(Summary(0), Summary(31))).Verdict == MetricVerdict.Regressed, "existing 30ms absolute gate is not raised to hide increases");
        });
        Case("Real signals and round noise", () => {
            var result = Compare(Summary(100), Summary(250));
            RegressionEvaluator.ApplyRoundEvidence(result, new[] { Summary(98), Summary(100), Summary(102) }, new[] { Summary(248), Summary(250), Summary(252) });
            Check(result.Verdict == OverallVerdict.RegressionDetected, "repeatable CPU regression remains detected with paired rounds");
            var memory = Compare(Summary(), Summary(memoryMb: 80));
            Check(memory.MetricComparisons.Single(m => m.MetricName == "Private Memory").Verdict == MetricVerdict.Regressed, "large memory regression remains detected");
            var jitter = Compare(Summary(166.67), Summary(233.33));
            RegressionEvaluator.ApplyRoundEvidence(jitter, new[] { Summary(100), Summary(300), Summary(100) }, new[] { Summary(300), Summary(100), Summary(300) });
            Check(Cpu(jitter).Verdict == MetricVerdict.Inconclusive, "contradictory paired rounds cannot be judged from the mean alone");
            var single = Compare(Summary(), Summary());
            RegressionEvaluator.ApplyRoundEvidence(single, new[] { Summary() }, new[] { Summary() });
            Check(single.Verdict == OverallVerdict.Inconclusive, "single round cannot establish repeatability");
            var boundary = Compare(Summary(), Summary(memoryMb: 56));
            RegressionEvaluator.ApplyRoundEvidence(boundary, new[] { Summary(), Summary(), Summary() }, new[] { Summary(memoryMb: 55), Summary(memoryMb: 56), Summary(memoryMb: 57) });
            Check(boundary.MetricComparisons.Single(m => m.MetricName == "Private Memory").Verdict == MetricVerdict.Inconclusive, "confidence interval crossing the original gate does not establish regression");
        });
        Case("Invalid measurement", () => {
            var bad = Summary(); bad.MeasurementValid = false; bad.MeasurementError = "Read denied";
            var result = Compare(bad, Summary());
            Check(result.Verdict == OverallVerdict.Inconclusive && result.MetricComparisons.Count == 0 && result.Notes.Contains("Read denied"), "read failure never becomes zero resource use or a pass");
            bad = Summary(); bad.ProcessExitedDuringMeasurement = true;
            Check(Compare(bad, Summary()).Verdict == OverallVerdict.Inconclusive, "early process exit invalidates measurement");
        });
        Case("Weighted CPU endpoints", () => {
            var samples = new[] { new ProcessSample { ElapsedMs = 0, TotalCpuTimeMs = 0 }, new ProcessSample { ElapsedMs = 100, TotalCpuTimeMs = 0 }, new ProcessSample { ElapsedMs = 1000, TotalCpuTimeMs = 100, InstantCpuPercentPerCore = 100.0 / 900 * 100 } };
            var result = ProcessMetricSummary.Aggregate(samples, 1);
            Check(Math.Abs(result.CpuPercentPerCore.Mean - 10) < 0.0001, "overall CPU mean uses actual endpoints instead of unweighted sample percentages");
        });
        using var scratch = new OwnedTestDirectory("Verify");
        string output = evidenceDirectory ?? scratch.DirectoryPath;
        Directory.CreateDirectory(output);
        Case("Reports preserve uncertainty", () => {
            var scenario = Compare(Summary(0), Summary(15.625)); scenario.ScenarioName = "<script>bad</script>";
            var suite = new SuiteComparisonResult { BaselineTarget = "A.exe", CandidateTarget = "B.exe", Rounds = 3,
                DecisionReadiness = new() { RecommendedProtocolMet = false, Reasons = new() { "Smoke only" } },
                OverallVerdict = OverallVerdict.Inconclusive, Scenarios = new() { scenario }, Runs = new() { new() { Round = 1, Side = "Candidate", Result = new() { Success = false, ErrorMessage = "load missing", Observations = new() { ["Proof"] = false }, Timeseries = new[] { new ProcessSample { TotalCpuTimeMs = 12 } } } } } };
            HtmlReporter.SaveComparisonReport(suite, output); JsonReporter.SaveComparisonReport(suite, output); AiSummaryFormatter.SaveFiles(suite, output);
            string html = File.ReadAllText(Path.Combine(output, "report.html"));
            Check(html.Contains("report-data") && !html.Contains("src=\"app.js\"") && !html.Contains("href=\"style.css\""), "standalone HTML embeds both viewer and report without external assets");
            Check(!html.Contains("<script>bad</script>") && html.Contains("\\u003Cscript\\u003E"), "scenario text cannot escape JSON data into executable HTML");
            var saved = JsonSerializer.Deserialize<SuiteComparisonResult>(File.ReadAllText(Path.Combine(output, "report.json")))!;
            Check(!saved.Runs[0].Result.Success && saved.Runs[0].Result.ErrorMessage == "load missing" && saved.Runs[0].Result.Timeseries.Count == 1, "full report retains failed workload, observations and raw samples");
            var (markdown, json) = AiSummaryFormatter.Generate(suite, output);
            Check(markdown.Contains("EXECUTION_ERROR") && JsonDocument.Parse(json).RootElement.GetProperty("Status").GetString() == "EXECUTION_ERROR", "failed workload is visibly an execution error in AI markdown and JSON");
            Check(markdown.Contains("尚未满足") && !JsonDocument.Parse(json).RootElement.GetProperty("DecisionReadiness").GetProperty("RecommendedProtocolMet").GetBoolean(), "AI summary cannot lose the distinction between a verdict and final decision readiness");
            suite.Runs.Clear();
            Check(JsonDocument.Parse(AiSummaryFormatter.Generate(suite, output).Json).RootElement.GetProperty("Status").GetString() == "INCONCLUSIVE", "non-failing uncertain measurement remains inconclusive");
        });
        Case("AI report publishing", () => {
            string directory = Path.Combine(output, "review-workflow"); Directory.CreateDirectory(directory);
            var suite = new SuiteComparisonResult { BaselineTarget = "A.exe", CandidateTarget = "B.exe", Rounds = 3,
                OverallVerdict = OverallVerdict.Inconclusive, Scenarios = new() { Compare(Summary(0), Summary(15)) } };
            JsonReporter.SaveComparisonReport(suite, directory);
            string sourcePath = Path.Combine(directory, "report.json"), original = File.ReadAllText(sourcePath);
            byte[] originalBytes = File.ReadAllBytes(sourcePath);
            string reviewPath = Path.Combine(directory, "ai-review.json"), logPath = Path.Combine(directory, "ai-review-log.jsonl");
            var pending = ReviewPublisher.Publish(directory);
            Check(pending.ReviewStatus == "pending" && File.Exists(Path.Combine(directory, "review-context.json")) && !File.Exists(logPath), "report prepares editable AI review and context without inventing a summary");
            string pendingHtml = File.ReadAllText(pending.ReportHtml);
            bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
            Check(Reject(() => ReviewPublisher.Publish(directory, true)) && pendingHtml == File.ReadAllText(pending.ReportHtml), "finalize rejects pending review while preserving the published report");
            var review = JsonSerializer.Deserialize<ReviewPublisher.Review>(File.ReadAllText(reviewPath))!;
            review.Status = "complete"; review.Author.Model = "QA fixture model"; review.GeneratedUtc = DateTime.UtcNow.ToString("O");
            review.Summary = "QA fixture: evidence is inconclusive; not a product measurement. <script>window.PWNED=1</script>";
            review.Findings.Add(new() { Title = "Evidence", Detail = "No product gain established.", ScenarioId = "fixture" });
            review.Limitations.Add("Synthetic verification fixture.");
            review.ReportSha256 = review.ReportSha256.ToUpperInvariant();
            void SaveReview() => File.WriteAllText(reviewPath, JsonSerializer.Serialize(review));
            SaveReview(); var complete = ReviewPublisher.Publish(directory, true);
            Check(complete.ReviewStatus == "complete" && complete.ReportUrl.StartsWith("file:") && complete.Revisions == 1 && File.ReadAllText(sourcePath) == original, "AI directly writes the review and finalize embeds it without changing measurements");
            string html = File.ReadAllText(complete.ReportHtml);
            Check(!html.Contains("<script>window.PWNED") && html.Contains("\\u003Cscript\\u003E") && !html.Contains("ai-paste") && !html.Contains("<dialog"), "reader report safely embeds AI text and has no manual summary workflow");
            Check(ReviewPublisher.Publish(directory, true).Revisions == 1 && File.ReadAllLines(logPath).Length == 1, "repeated finalize does not append duplicate AI log entries");
            review.Summary = "QA revision two: still inconclusive."; SaveReview();
            Check(ReviewPublisher.Publish(directory, true).Revisions == 2 && File.ReadAllLines(logPath)[0].Contains("QA fixture:"), "changed AI summary retains its previous log revision");
            string completeHtml = File.ReadAllText(complete.ReportHtml), log = File.ReadAllText(logPath);
            string digest = review.ReportSha256; review.ReportSha256 = new string('0', 64); SaveReview();
            Check(Reject(() => ReviewPublisher.Publish(directory, true)) && File.ReadAllText(complete.ReportHtml) == completeHtml && File.ReadAllText(logPath) == log, "wrong-report review cannot replace report or logs");
            review.ReportSha256 = digest; review.Findings[0].ScenarioId = "missing"; SaveReview();
            Check(Reject(() => ReviewPublisher.Publish(directory, true)), "AI cannot cite a scenario absent from measurements");
            review.Findings[0].ScenarioId = "fixture"; review.Author.Model = ""; SaveReview();
            Check(Reject(() => ReviewPublisher.Publish(directory, true)), "complete AI review requires actual author provenance");
            review.Author.Model = "QA fixture model"; review.GeneratedUtc = "not a date"; SaveReview();
            Check(Reject(() => ReviewPublisher.Publish(directory, true)), "complete AI review requires a valid UTC timestamp");
            review.GeneratedUtc = DateTime.UtcNow.ToString("O"); SaveReview(); File.AppendAllText(sourcePath, "\n");
            Check(Reject(() => ReviewPublisher.Publish(directory, true)) && completeHtml == File.ReadAllText(complete.ReportHtml), "even a raw evidence byte change invalidates the old review");
            File.WriteAllBytes(sourcePath, originalBytes);
            File.AppendAllText(logPath, "{broken\n");
            Check(Reject(() => ReviewPublisher.Publish(directory, true)) && completeHtml == File.ReadAllText(complete.ReportHtml), "invalid AI log history is retained and blocks publication");
            File.WriteAllText(logPath, log);
            string runDirectory = Path.Combine(directory, "single-target"); Directory.CreateDirectory(runDirectory);
            File.WriteAllText(Path.Combine(runDirectory, "run_evidence.json"), JsonSerializer.Serialize(new[] { new ScenarioResult { ScenarioId = "fixture", Success = true, Metrics = Summary() } }));
            File.WriteAllText(Path.Combine(runDirectory, "run_metadata.json"), "{\"Target\":\"one.exe\"}");
            File.WriteAllText(Path.Combine(runDirectory, "run_summary.json"), JsonSerializer.Serialize(new Dictionary<string, ProcessMetricSummary> { ["fixture"] = Summary() }));
            var runPending = ReviewPublisher.Publish(runDirectory);
            string runReviewPath = Path.Combine(runDirectory, "ai-review.json");
            review.ReportSha256 = runPending.ReportSha256; File.WriteAllText(runReviewPath, JsonSerializer.Serialize(review));
            Check(ReviewPublisher.Publish(runDirectory, true).ReviewStatus == "complete", "single-target AI review binds evidence, metadata and summary together");
            File.WriteAllText(Path.Combine(runDirectory, "run_summary.json"), "{}");
            Check(Reject(() => ReviewPublisher.Publish(runDirectory, true)), "changing single-target summary invalidates its AI review");
        });
        Case("Stable rule IDs and real JSONL", () => {
            using var sandbox = new SonicRouteSandbox(); sandbox.PopulateRules(4, "example.exe", triggerType: 0);
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(sandbox.SandboxPath, "config.json")));
            Check(config.RootElement.GetProperty("TrayWheelPaused").GetBoolean(), "sandbox disables tray-wheel audio input instead of relying on user inactivity");
            var first = Directory.GetFiles(Path.Combine(sandbox.SandboxPath, "Automation")).Order().Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.Clone()).ToArray();
            sandbox.PopulateRules(4, "example.exe", triggerType: 0, hotkey: "Ctrl+Alt+Shift+F10");
            var second = Directory.GetFiles(Path.Combine(sandbox.SandboxPath, "Automation")).Order().Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.Clone()).ToArray();
            Check(first.Select(r => r.GetProperty("Id").GetString()).SequenceEqual(second.Select(r => r.GetProperty("Id").GetString())), "saving rules retains identities instead of delete/recreate churn");
            Check(second.Select(r => r.GetProperty("Hotkey").GetString()).Distinct().Count() == 4, "controlled hotkey workload avoids self-conflicting identical bindings");
            Check(second.All(r => r.GetProperty("TriggerApp").GetString() == "example"), "rule process selectors match extension-free product semantics");
            string history = Path.Combine(sandbox.SandboxPath, "ExecutionHistory", "Lite"); Directory.CreateDirectory(history);
            File.WriteAllText(Path.Combine(history, "recent.jsonl"), JsonSerializer.Serialize(new { RuleId = second[0].GetProperty("Id").GetString(), ElapsedMs = 2500 }) + "\n{incomplete");
            Check(sandbox.ReadHistory().Length == 1, "history reader counts only complete matching JSONL entries");
            sandbox.PopulateRules(2, triggerType: 4);
            Check(Directory.GetFiles(Path.Combine(sandbox.SandboxPath, "Automation"), "*.json").Length == 2, "shrinking workload removes only owned excess rules");
        });
        Case("Historical baseline compatibility", () => {
            var environment = SystemEnvironmentInfo.CaptureCurrent(); var changed = SystemEnvironmentInfo.CaptureCurrent(); changed.OsVersion += ".different";
            Check(!environment.IsCompatibleWith(changed, out _), "OS version mismatch invalidates historical compatibility");
            var baseline = new StoredBaseline { SchemaVersion = 2, Tier = "Quick", RequestedDurationSeconds = 2, Rounds = new() { ["fixture"] = new() { Summary(), Summary(), Summary() } } };
            BaselineStore.Save("fixture", baseline, output);
            Check(BaselineStore.Load("fixture", output)?.Rounds["fixture"].Count == 3, "baseline stores complete round protocol evidence");
        });
        Case("Managed target identity", () => {
            string executable = Path.Combine(output, "sidecar.exe");
            File.Copy(Path.ChangeExtension(typeof(SelfVerification).Assembly.Location, ".exe"), executable);
            string managed = Path.ChangeExtension(executable, ".dll");
            File.WriteAllText(managed, "payload A");
            var identity = ComparisonRunner.CaptureTargetFiles(executable);
            Check(identity.ContainsKey("entry.dll"), "apphost identity includes its local managed payload");
            File.WriteAllText(managed, "payload B");
            bool rejected = false; try { ComparisonRunner.VerifyFilesUnchanged(executable, identity); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "changing a managed payload invalidates unchanged apphost bytes");
        });
        Case("Time weighted means and gap quality", () => {
            var samples = new[] { new ProcessSample { ElapsedMs = 0, PrivateBytes = 0 }, new ProcessSample { ElapsedMs = 100, PrivateBytes = 100 }, new ProcessSample { ElapsedMs = 1000, PrivateBytes = 100 } };
            var summary = ProcessMetricSummary.Aggregate(samples, 1);
            Check(Math.Abs(summary.PrivateBytes.Mean - 95) < 0.000001, "irregular resource samples are weighted by elapsed time rather than sample count");
            var quality = SamplingQuality.Calculate(samples, 200);
            Check(!quality.SuitableForComparison && quality.DelayedIntervalCount == 1 && Math.Abs(quality.DelayedTimeFraction - 0.9) < 0.000001, "large sample gaps expose their covered time and invalidate gating");
            var bad = Summary(); bad.SamplingQuality = quality;
            Check(Compare(bad, Summary()).Verdict == OverallVerdict.Inconclusive, "bad sampling quality cannot pass a comparison");
            Check(!SamplingQuality.Calculate(new[] { new ProcessSample(), new ProcessSample(), new ProcessSample() }, 200).SuitableForComparison, "duplicate timestamps cannot masquerade as independent samples");
            Check(SamplingQuality.Calculate(new[] { new ProcessSample(), new ProcessSample { ElapsedMs = 200 }, new ProcessSample { ElapsedMs = 400 }, new ProcessSample { ElapsedMs = 401 } }, 200).SuitableForComparison, "short final stop interval is not a delayed gap");
        });
        Case("Owned temporary cleanup", () => {
            string owned;
            using (var directory = new OwnedTestDirectory("Fixture"))
            {
                owned = directory.DirectoryPath;
                File.WriteAllText(Path.Combine(owned, "temporary.txt"), "owned fixture");
            }
            Check(!Directory.Exists(owned), "temporary material is removed after its owned lifetime");
            Check(Directory.Exists(output), "cleaning one test directory preserves report evidence and sibling material");
            bool rejected = false;
            try { using var invalid = new OwnedTestDirectory("Fixture", name: "../escape"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "cleanup directory traversal is rejected before creating its destination");
        });
        Case("Versioned scenario protocol", () => {
            var generic = new IBenchmarkScenario[] { new GenericProcessScenario("--alpha") };
            var a = MeasurementProtocol.Create(generic, BenchmarkTier.Quick, 2, 200);
            var b = MeasurementProtocol.Create(new IBenchmarkScenario[] { new GenericProcessScenario("--beta") }, BenchmarkTier.Quick, 2, 200);
            Check(!a.IsCompatibleWith(b), "different generic arguments cannot reuse a historical baseline");
            Check(!a.IsCompatibleWith(null), "old baselines without measurement protocol remain viewable but not comparable");
            b = MeasurementProtocol.Create(generic, BenchmarkTier.Quick, 2, 200);
            Check(a.IsCompatibleWith(b), "identical duration, warmup, argument and sampler semantics are comparable");
            b.Revision = "older";
            Check(!a.IsCompatibleWith(b), "different resource aggregation semantics invalidate historical gating");
        });
        Case("Paired and Welch intervals", () => {
            var paired = RoundStatistics.Calculate(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 }, true);
            Check(paired.Lower95 == 3 && paired.Upper95 == 3 && paired.DegreesOfFreedom == 2, "constant paired differences have zero standard error");
            var independent = RoundStatistics.Calculate(new double[] { 1, 2, 3 }, new double[] { 2, 4, 6, 8 }, false);
            Check(Math.Abs(independent.StandardError - Math.Sqrt(2)) < 0.000001 && Math.Abs(independent.DegreesOfFreedom - 216.0 / 53) < 0.000001, "unequal historical sample sizes use Welch-Satterthwaite degrees of freedom");
            Check(RoundStatistics.Critical95(2.9) == 4.303 && RoundStatistics.Critical95(100) == 2.043, "fractional/large degrees of freedom use conservative table bounds");
        });
        Case("Preview schedule", () => {
            var selection = new IBenchmarkScenario[] { new IdleEmptyScenario(), new TargetAbsentScenario() };
            var plan = BenchmarkPlan.Create(selection, BenchmarkTier.Quick, 2, 10, 200, true, false, false);
            Check(plan.Runs.Select(r => r.Side).SequenceEqual(new[] { "Baseline", "Candidate", "Baseline", "Candidate", "Candidate", "Baseline", "Candidate", "Baseline" }), "A/B pairs stay adjacent within each scenario and alternate across rounds");
            Check(plan.TargetLaunchCount == 8 && plan.MeasurementSeconds == 80 && Math.Abs(plan.CooldownSeconds - 14.4) < 0.000001, "preview accounts for all target windows and configured cooldowns");
            var desktop = BenchmarkPlan.Create(new IBenchmarkScenario[] { new WindowTrayTransitionsScenario() }, BenchmarkTier.Standard, 1, 1, 200, false, false, false);
            Check(desktop.DesktopInteractionRequired && !desktop.DesktopInteractionAllowed, "preview exposes desktop impact without starting or allowing it");
        });
        Case("Three performance modes", () => {
            var light = new Dictionary<string, string> { ["mode"] = "light" }; TestModes.Apply(light);
            Check(light["rounds"] == "1" && light["scenario"].Split(',').Length == 3 && light["duration"] == "3", "light mode is explicitly a short three-scenario smoke test");
            var targeted = new Dictionary<string, string> { ["mode"] = "targeted", ["feature"] = "hotkeys,schedule" }; TestModes.Apply(targeted);
            Check(targeted["scenario"] == "rule_save_hotkeys,schedule_rule_churn" && targeted["rounds"] == "5" && targeted["duration"] == "30" && targeted["warmup"] == "5", "targeted mode selects requested features with five rounds, thirty-second windows and five-second warmup");
            var full = new Dictionary<string, string> { ["mode"] = "full" }; TestModes.Apply(full);
            Check(full["scenario"].Split(',').Length == 12 && !full["scenario"].Contains("generic_process"), "full mode covers all twelve isolated scenarios, excluding unrestricted generic targets");
            bool rejected = false; try { TestModes.Apply(new() { ["mode"] = "targeted", ["feature"] = "not-a-feature" }); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "unknown targeted features are rejected rather than silently expanding the suite");
            rejected = false; try { TestModes.Apply(new() { ["mode"] = "full", ["rounds"] = "1" }); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "full mode does not silently degrade to a single smoke round");
            Check(full["rounds"] == "7" && full["duration"] == "60" && full["warmup"] == "5", "full mode predeclares seven long-window rounds");
        });
        Case("Baseline capture policy", () => {
            var now = new DateTime(2026,10,28,10,0,0,DateTimeKind.Utc);
            Check(BaselinePolicy.IsFresh(now.AddDays(-6),now,7) && !BaselinePolicy.IsFresh(now.AddDays(-8),now,7), "historical freshness respects the declared seven-day bound");
            Check(!BaselinePolicy.IsFresh(default,now,7) && !BaselinePolicy.IsFresh(now.AddHours(1),now,7), "unset and implausible future baseline dates are rejected");
            bool rejected = false;
            BaselineStore.Save("immutable-fixture",new StoredBaseline(),output);
            try { BaselineStore.Save("immutable-fixture",new StoredBaseline(),output); } catch(ArgumentException) { rejected = true; }
            Check(rejected, "refreshing a baseline cannot overwrite an existing capture");
            var a=MeasurementProtocol.Create(new IBenchmarkScenario[]{new GenericProcessScenario()},BenchmarkTier.Standard,30,200,5);
            var b=MeasurementProtocol.Create(new IBenchmarkScenario[]{new GenericProcessScenario()},BenchmarkTier.Standard,30,200,2);
            Check(a.Scenarios["generic_process"].WarmupSeconds==5 && !a.IsCompatibleWith(b), "configured warmup is bound to historical comparability");
            Check(!DecisionReadiness.Assess(new SuiteComparisonResult()).RecommendedProtocolMet, "missing protocol and rounds cannot meet the final decision protocol");
        });
        try
        {
            if (Selected("protocol"))
            {
            var failed = await BenchmarkRunner.RunScenarioAsync(new InvalidScenario(), new BenchmarkContext());
            Check(!failed.Success && !failed.Metrics.MeasurementValid && failed.Observations.ContainsKey("LoadConfirmed"), "runner propagates scenario failure instead of treating its metrics as valid");
            }
            if (Selected("lifecycle"))
            {
            string self = Path.ChangeExtension(typeof(SelfVerification).Assembly.Location, ".exe");
            using (var monitor = ProcessMonitor.Launch(self, "__worker threads 2500", measureStartupLatency: false))
            {
                monitor.Start(); await Task.Delay(1600); var metrics = await monitor.StopAsync();
                Check(metrics.MeasurementValid && metrics.SampleCount >= 7 && metrics.PrivateBytes.Mean > 0 && metrics.SamplingQuality != null, "owned native process produces valid CPU/memory samples and quality evidence at the default 200ms interval");
                Check(metrics.ThreadCount.Max - metrics.ThreadCount.Min >= 4, "native thread changes are observed after Process.Refresh");
                using var reference = Process.GetProcessById(monitor.ProcessId);
                using (var attached = ProcessMonitor.Attach(monitor.ProcessId))
                {
                    bool rejected = false; try { attached.Kill(); } catch (InvalidOperationException) { rejected = true; }
                    Check(rejected && !reference.HasExited, "attached unrelated processes cannot be killed by monitor API");
                }
                monitor.Dispose();
                Check(reference.WaitForExit(1500), "disposing monitor joins sampling then terminates owned process");
            }
            using (var monitor = ProcessMonitor.Launch(self, "__worker idle 150", measureStartupLatency: false))
            {
                monitor.Start(); await Task.Delay(450); var metrics = await monitor.StopAsync();
                Check(!metrics.MeasurementValid && metrics.ProcessExitedDuringMeasurement, "real early-exit target is not a successful benchmark");
            }
            }
        }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL native checks: " + error); }
        if (!string.IsNullOrWhiteSpace(realSonicRouteExe))
        {
            try
            {
                var result = await BenchmarkRunner.RunScenarioAsync(new IdleEmptyScenario(), new BenchmarkContext { TargetExecutable = realSonicRouteExe, Tier = BenchmarkTier.Quick, CustomDurationSeconds = 0.8 });
                Check(result.Success && result.Observations.ContainsKey("SandboxEvidence"), "explicit real SonicRoute target confirms isolated readiness and measurement");
            }
            catch (Exception error) { _failed++; Console.WriteLine("FAIL explicit target: " + error); }
        }
        else Console.WriteLine("NOT TESTED actual SonicRoute (supply --target explicitly; no auto-discovered user application is started)");
        Console.WriteLine($"RESULT passed={_passed} failed={_failed} reportFixture={output}");
        return _failed == 0 ? 0 : 1;
    }

    public static async Task<int> RunWorkerAsync(string[] args)
    {
        string mode = args.ElementAtOrDefault(0) ?? "idle";
        int duration = int.TryParse(args.ElementAtOrDefault(1), out int parsed) ? Math.Clamp(parsed, 1, 60000) : 2000;
        if (mode == "threads")
        {
            await Task.Delay(350);
            using var done = new ManualResetEventSlim();
            var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() => done.Wait()) { IsBackground = true }).ToArray();
            foreach (var thread in threads) thread.Start();
            await Task.Delay(650); done.Set(); foreach (var thread in threads) thread.Join();
            await Task.Delay(Math.Max(1, duration - 1000));
        }
        else if (mode == "cpu")
        {
            var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < duration) Thread.SpinWait(1000);
        }
        else await Task.Delay(duration);
        return 0;
    }

    private sealed class InvalidScenario : BaseScenario
    {
        public override string Id => "invalid_fixture";
        public override string Name => "Invalid workload";
        public override string Description => "Fixture";
        public override BenchmarkModule Module => BenchmarkModule.General;
        public override Task<ScenarioResult> RunAsync(BenchmarkContext context) => Task.FromResult(new ScenarioResult { Success = false, ErrorMessage = "Load not observed", Metrics = Summary(), Observations = new() { ["LoadConfirmed"] = false } });
    }
}
