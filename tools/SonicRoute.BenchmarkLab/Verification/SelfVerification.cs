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
    private static void Check(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException(name);
        _passed++; Console.WriteLine("PASS " + name);
    }
    private static void Case(string name, Action action)
    {
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

    public static async Task<int> RunAllAsync(string? realSonicRouteExe = null)
    {
        _passed = _failed = 0;
        Console.WriteLine("BenchmarkLab 1.1: calculation, evidence, reporting and owned native-process checks");
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
        string output = Path.Combine(Path.GetTempPath(), "SonicRouteBenchmarkChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        Case("Reports preserve uncertainty", () => {
            var scenario = Compare(Summary(0), Summary(15.625)); scenario.ScenarioName = "<script>bad</script>";
            var suite = new SuiteComparisonResult { BaselineTarget = "A.exe", CandidateTarget = "B.exe", Rounds = 3,
                OverallVerdict = OverallVerdict.Inconclusive, Scenarios = new() { scenario }, Runs = new() { new() { Round = 1, Side = "Candidate", Result = new() { Success = false, ErrorMessage = "load missing", Observations = new() { ["Proof"] = false }, Timeseries = new[] { new ProcessSample { TotalCpuTimeMs = 12 } } } } } };
            HtmlReporter.SaveComparisonReport(suite, output); JsonReporter.SaveComparisonReport(suite, output); AiSummaryFormatter.SaveFiles(suite, output);
            string html = File.ReadAllText(Path.Combine(output, "report.html"));
            Check(html.Contains("INCONCLUSIVE") && !html.Contains("所有测试场景常规指标均在预设阈值以内"), "inconclusive HTML has no green all-tests-passed claim");
            Check(!html.Contains("<script>bad</script>") && html.Contains("&lt;script&gt;"), "scenario text is HTML escaped");
            var saved = JsonSerializer.Deserialize<SuiteComparisonResult>(File.ReadAllText(Path.Combine(output, "report.json")))!;
            Check(!saved.Runs[0].Result.Success && saved.Runs[0].Result.ErrorMessage == "load missing" && saved.Runs[0].Result.Timeseries.Count == 1, "full report retains failed workload, observations and raw samples");
            var (markdown, json) = AiSummaryFormatter.Generate(suite, output);
            Check(markdown.Contains("INCONCLUSIVE") && JsonDocument.Parse(json).RootElement.GetProperty("Status").GetString() == "INCONCLUSIVE", "AI summary and JSON agree on uncertainty");
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
        try
        {
            var failed = await BenchmarkRunner.RunScenarioAsync(new InvalidScenario(), new BenchmarkContext());
            Check(!failed.Success && !failed.Metrics.MeasurementValid && failed.Observations.ContainsKey("LoadConfirmed"), "runner propagates scenario failure instead of treating its metrics as valid");
            string self = Path.ChangeExtension(typeof(SelfVerification).Assembly.Location, ".exe");
            using (var monitor = ProcessMonitor.Launch(self, "__worker threads 2500", measureStartupLatency: false))
            {
                monitor.Start(); await Task.Delay(1600); var metrics = await monitor.StopAsync();
                Check(metrics.MeasurementValid && metrics.SampleCount >= 8 && metrics.PrivateBytes.Mean > 0, "owned native process produces valid real CPU/memory samples");
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
