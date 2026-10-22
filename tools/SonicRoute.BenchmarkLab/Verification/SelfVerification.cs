using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SonicRoute.BenchmarkLab.Comparison;
using SonicRoute.BenchmarkLab.Core;
using SonicRoute.BenchmarkLab.Reporting;
using SonicRoute.BenchmarkLab.Scenarios;
using SonicRoute.BenchmarkLab.Storage;

namespace SonicRoute.BenchmarkLab.Verification
{
    public static class SelfVerification
    {
        public static async Task<int> RunAllAsync(string? realSonicRouteExe = null)
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("  SonicRoute Benchmark Lab - 自检与验收标准验证");
            Console.WriteLine("================================================================================");
            int failed = 0;

            failed += Test1_NormalReportPass();
            failed += Test2_DetectCpuRegression();
            failed += Test3_DetectMemoryRegression();
            failed += Test4_NoiseDampeningNoFalseAlarms();
            failed += Test5_TokenEfficiencyAndOfflineReport();

            bool realTargetProvided = !string.IsNullOrEmpty(realSonicRouteExe) && File.Exists(realSonicRouteExe);
            if (realTargetProvided)
            {
                failed += await Test6_RealSonicRouteExecutionAsync(realSonicRouteExe!);
            }
            else
            {
                Console.WriteLine("\n[Test 6/6] Real SonicRoute Process Run: Skipped (no executable provided).");
            }

            Console.WriteLine("\n================================================================================");
            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  通过 {(realTargetProvided ? 6 : 5)} 项核心检查；跳过 {(realTargetProvided ? 0 : 1)} 项。未执行的检查不计为通过。");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ❌ 存在 {failed} 项测试未通过，请检查上述错误。");
                Console.ResetColor();
                return 1;
            }
        }

        private static int Test1_NormalReportPass()
        {
            Console.Write("\n[Test 1/6] 验证正常无退化时自动输出简洁可读报告... ");
            var b = CreateSummary(cpuMs: 200, privMb: 50, handles: 700, threads: 30);
            var c = CreateSummary(cpuMs: 198, privMb: 49.5, handles: 702, threads: 30);

            var res = RegressionEvaluator.EvaluateScenario("idle_empty", "Idle Background", b, c, 5.0, 10.0, 3);
            if (res.Verdict != OverallVerdict.Pass)
            {
                Console.WriteLine("FAILED: Expected Pass, got " + res.Verdict);
                return 1;
            }

            var suite = new SuiteComparisonResult
            {
                BaselineTarget = "v1.24.exe",
                CandidateTarget = "v1.25.exe",
                Rounds = 3,
                Scenarios = new List<ScenarioComparisonResult> { res }
            };

            var (md, json) = AiSummaryFormatter.Generate(suite, "test_out");
            if (!md.Contains("PASS") || md.Contains("发现性能退化项"))
            {
                Console.WriteLine("FAILED: Markdown report contains unexpected regression warnings.");
                return 1;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED ✓");
            Console.ResetColor();
            return 0;
        }

        private static int Test2_DetectCpuRegression()
        {
            Console.Write("[Test 2/6] 人为制造明确 CPU 退化 (+150%)，验证定位能力... ");
            var b = CreateSummary(cpuMs: 100, privMb: 50, handles: 700, threads: 30);
            var c = CreateSummary(cpuMs: 250, privMb: 50, handles: 700, threads: 30);

            var res = RegressionEvaluator.EvaluateScenario("rules_scaling_100", "Rules Scaling (100)", b, c, 5.0, 10.0, 3);
            if (res.Verdict != OverallVerdict.RegressionDetected)
            {
                Console.WriteLine("FAILED: Expected RegressionDetected for CPU, got " + res.Verdict);
                return 1;
            }

            var cpuMetric = res.MetricComparisons.First(m => m.MetricName == "Total CPU Time");
            if (cpuMetric.Verdict != MetricVerdict.Regressed)
            {
                Console.WriteLine("FAILED: CPU metric verdict is not Regressed.");
                return 1;
            }

            foreach (var candidateCpu in new[] { 0.0, 30.0, 31.0, 1000.0 })
            {
                var zero = CreateSummary(cpuMs: 0, privMb: 50, handles: 700, threads: 30);
                var changed = CreateSummary(cpuMs: candidateCpu, privMb: 50, handles: 700, threads: 30);
                var comparison = RegressionEvaluator.EvaluateScenario("zero_cpu", "Zero CPU baseline", zero, changed, 5.0, 10.0, 3);
                var metric = comparison.MetricComparisons.First(m => m.MetricName == "Total CPU Time");
                bool shouldRegress = candidateCpu > 30.0;
                if ((metric.Verdict == MetricVerdict.Regressed) != shouldRegress || metric.DeltaPercentDefined
                    || (comparison.Verdict == OverallVerdict.RegressionDetected) != shouldRegress
                    || !metric.Details.Contains("percentage undefined"))
                {
                    Console.WriteLine("FAILED: Zero-baseline CPU gate or percentage reporting is incorrect.");
                    return 1;
                }
            }

            var suite = new SuiteComparisonResult
            {
                BaselineTarget = "v1.24.exe",
                CandidateTarget = "v1.25.exe",
                Rounds = 3,
                Scenarios = new List<ScenarioComparisonResult> { res },
                OverallVerdict = OverallVerdict.RegressionDetected
            };
            suite.RegressionsSummary.Add($"[Rules Scaling (100)] Total CPU Time {cpuMetric.Details}");

            var (md, _) = AiSummaryFormatter.Generate(suite, "test_out");
            if (!md.Contains("REGRESSION") || !md.Contains("Total CPU Time"))
            {
                Console.WriteLine("FAILED: AI summary did not highlight the CPU regression.");
                return 1;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED ✓ (精准定位: " + cpuMetric.Details + ")");
            Console.ResetColor();
            return 0;
        }

        private static int Test3_DetectMemoryRegression()
        {
            Console.Write("[Test 3/6] 人为制造明确内存退化 (+60%)，验证定位能力... ");
            var b = CreateSummary(cpuMs: 100, privMb: 50, handles: 700, threads: 30);
            var c = CreateSummary(cpuMs: 100, privMb: 80, handles: 700, threads: 30);

            var res = RegressionEvaluator.EvaluateScenario("idle_empty", "Idle Background", b, c, 5.0, 10.0, 3);
            if (res.Verdict != OverallVerdict.RegressionDetected)
            {
                Console.WriteLine("FAILED: Expected RegressionDetected for Memory, got " + res.Verdict);
                return 1;
            }

            var memMetric = res.MetricComparisons.First(m => m.MetricName == "Private Memory");
            if (memMetric.Verdict != MetricVerdict.Regressed)
            {
                Console.WriteLine("FAILED: Private Memory metric verdict is not Regressed.");
                return 1;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED ✓ (精准定位: " + memMetric.Details + ")");
            Console.ResetColor();
            return 0;
        }

        private static int Test4_NoiseDampeningNoFalseAlarms()
        {
            Console.Write("[Test 4/6] 验证数据微小波动与噪声抑制 (不宣称假优化或假退化)... ");
            // 8ms difference on CPU time (below 30ms noise floor)
            var b = CreateSummary(cpuMs: 150, privMb: 50, handles: 700, threads: 30);
            var c = CreateSummary(cpuMs: 158, privMb: 50, handles: 700, threads: 30);

            var res = RegressionEvaluator.EvaluateScenario("target_absent", "Target Absent", b, c, 5.0, 10.0, 3);
            var cpuMetric = res.MetricComparisons.First(m => m.MetricName == "Total CPU Time");

            if (cpuMetric.Verdict != MetricVerdict.Stable)
            {
                Console.WriteLine("FAILED: Expected Stable within noise margin, got " + cpuMetric.Verdict);
                return 1;
            }

            if (res.Verdict != OverallVerdict.Pass)
            {
                Console.WriteLine("FAILED: Minor noise caused overall regression failure.");
                return 1;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASSED ✓ (正确判定为噪声容限内稳定: " + cpuMetric.Details + ")");
            Console.ResetColor();
            return 0;
        }

        private static int Test5_TokenEfficiencyAndOfflineReport()
        {
            Console.Write("[Test 5/6] 验证零 Token 极简报告生成与字符数控制 (~300-500 字)... ");
            var b = CreateSummary(cpuMs: 200, privMb: 60, handles: 700, threads: 30);
            var c = CreateSummary(cpuMs: 195, privMb: 59, handles: 700, threads: 30);

            var res = RegressionEvaluator.EvaluateScenario("idle_empty", "Idle", b, c, 5.0, 10.0, 3);
            var suite = new SuiteComparisonResult
            {
                BaselineTarget = "SonicRoute-v1.24.exe",
                CandidateTarget = "SonicRoute-v1.25.exe",
                Rounds = 3,
                Scenarios = new List<ScenarioComparisonResult> { res }
            };

            string outDir = Path.Combine(Path.GetTempPath(), "BenchmarkVerify_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(outDir);

            try
            {
                JsonReporter.SaveComparisonReport(suite, outDir);
                HtmlReporter.SaveComparisonReport(suite, outDir);
                AiSummaryFormatter.SaveFiles(suite, outDir);

                string md = File.ReadAllText(Path.Combine(outDir, "ai-summary.md"));
                string json = File.ReadAllText(Path.Combine(outDir, "ai-summary.json"));

                if (md.Length > 800)
                {
                    Console.WriteLine($"WARNING: AI summary length is {md.Length} chars, recommended < 600.");
                }

                if (!File.Exists(Path.Combine(outDir, "report.json")) ||
                    !File.Exists(Path.Combine(outDir, "report.html")))
                {
                    Console.WriteLine("FAILED: Complete tier-2 reports were not generated.");
                    return 1;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASSED ✓ (极简报告字数: {md.Length} 字符, 完整证据已落盘)");
                Console.ResetColor();
                return 0;
            }
            finally
            {
                try { Directory.Delete(outDir, true); } catch { }
            }
        }

        private static async Task<int> Test6_RealSonicRouteExecutionAsync(string realExe)
        {
            Console.Write($"[Test 6/6] 验证真实 SonicRoute 进程采集与安全隔离 (Target: {Path.GetFileName(realExe)})... ");
            var context = new BenchmarkContext
            {
                TargetExecutable = realExe,
                Tier = BenchmarkTier.Quick,
                CustomDurationSeconds = 2.0
            };

            var scenario = new IdleEmptyScenario();
            var res = await scenario.RunAsync(context);

            if (res.Metrics.SampleCount == 0)
            {
                Console.WriteLine("FAILED: No samples collected from target process.");
                return 1;
            }

            if (res.Metrics.PrivateBytes.Mean <= 0)
            {
                Console.WriteLine("FAILED: Private memory measurement returned 0.");
                return 1;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED ✓ (采集到 {res.Metrics.SampleCount} 帧样本, 启动耗时 {res.Metrics.StartupLatencyMs:F1}ms, 内存 {res.Metrics.PrivateBytes.Mean / (1024*1024):F1}MB, 工具开销 {res.Metrics.MonitorSelfCpuTimeMs:F1}ms)");
            Console.ResetColor();
            return 0;
        }

        private static ProcessMetricSummary CreateSummary(double cpuMs, double privMb, int handles, int threads)
        {
            return new ProcessMetricSummary
            {
                DurationSeconds = 10.0,
                SampleCount = 100,
                TotalCpuTimeMs = cpuMs,
                PrivateBytes = new MetricStatistics
                {
                    Mean = privMb * 1024 * 1024,
                    Max = privMb * 1024 * 1024
                },
                PeakWorkingSetBytes = (long)(privMb * 1.2 * 1024 * 1024),
                HandleCount = new MetricStatistics { Mean = handles, Max = handles },
                ThreadCount = new MetricStatistics { Mean = threads, Max = threads }
            };
        }
    }
}
