using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SonicRoute.BenchmarkLab.Comparison;
using SonicRoute.BenchmarkLab.Core;
using SonicRoute.BenchmarkLab.Reporting;
using SonicRoute.BenchmarkLab.Scenarios;
using SonicRoute.BenchmarkLab.Storage;

namespace SonicRoute.BenchmarkLab
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            string command = args[0].ToLowerInvariant();
            var remainingArgs = args.Skip(1).ToArray();

            try
            {
                return command switch
                {
                    "compare" or "ab" => await ExecuteCompareAsync(remainingArgs),
                    "run" => await ExecuteRunAsync(remainingArgs),
                    "baseline" => ExecuteBaseline(remainingArgs),
                    "scenarios" => ExecuteScenarios(remainingArgs),
                    "verify" or "selftest" => await ExecuteVerifyAsync(remainingArgs),
                    _ => ShowUnknownCommand(command)
                };
            }
            catch (ArgumentException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"[Argument Error] {ex.Message}");
                Console.ResetColor();
                return 2;
            }
            catch (FileNotFoundException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"[File Not Found] {ex.Message}");
                Console.ResetColor();
                return 2;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"[Execution Error] {ex.Message}\n{ex.StackTrace}");
                Console.ResetColor();
                return 3;
            }
        }

        private static bool IsHelp(string arg) =>
            arg is "--help" or "-h" or "help" or "/?";

        private static int ShowUnknownCommand(string command)
        {
            Console.Error.WriteLine($"Unknown command: '{command}'. Use 'help' or '--help' for usage.");
            return 2;
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"================================================================================");
            Console.WriteLine(@"  SonicRoute Benchmark Lab - Windows 软件性能基准与自动化版本对比工具");
            Console.WriteLine(@"================================================================================");
            Console.WriteLine(@"用法: SonicRoute.BenchmarkLab <command> [options]");
            Console.WriteLine();
            Console.WriteLine(@"命令列表:");
            Console.WriteLine(@"  compare, ab     执行两个版本或与历史基线的 A/B 性能对比 (交替多轮测试)");
            Console.WriteLine(@"  run             运行指定目标程序的性能基准测试 (可保存为基线)");
            Console.WriteLine(@"  baseline        管理已保存的历史性能基线 (list, show, delete, save)");
            Console.WriteLine(@"  scenarios       列出所有已注册的测试场景");
            Console.WriteLine(@"  verify          运行内置自检与核心验收标准验证 (6 项验收检查)");
            Console.WriteLine();
            Console.WriteLine(@"'compare' 核心选项:");
            Console.WriteLine(@"  --baseline <path|name>      基准版本路径，或已保存基线名称 (必需)");
            Console.WriteLine(@"  --candidate <path>          待测候选版本可执行文件路径 (必需)");
            Console.WriteLine(@"  --scenario <id>             运行单个指定场景 (如 idle_empty)");
            Console.WriteLine(@"  --suite <quick|standard|endurance> 测试套件预设 (默认: standard)");
            Console.WriteLine(@"  --module <all|automation|hotkey|ui|general> 按受影响模块过滤场景");
            Console.WriteLine(@"  --rounds <n>                测试交替轮次 (默认: 3 轮)");
            Console.WriteLine(@"  --duration <seconds>        覆盖每阶段测试时长");
            Console.WriteLine(@"  --threshold-cpu <percent>   CPU 退化报警阈值百分比 (默认: 5.0%)");
            Console.WriteLine(@"  --threshold-mem <percent>   内存退化报警阈值百分比 (默认: 10.0%)");
            Console.WriteLine(@"  --output <dir>              报告输出目录 (默认: ./benchmarks/<run_id>/)");
            Console.WriteLine(@"  --quiet, -q                 安静模式: 仅输出极简 AI 总结");
            Console.WriteLine(@"  --json                      以纯 JSON 格式输出 AI 报告");
            Console.WriteLine();
            Console.WriteLine(@"退出码说明:");
            Console.WriteLine(@"  0: PASS (性能基准通过，未超预设退化阈值)");
            Console.WriteLine(@"  1: REGRESSION (检测到性能显著退化)");
            Console.WriteLine(@"  2: INVALID_ARGUMENTS (命令行参数错误或文件不存在)");
            Console.WriteLine(@"  3: EXECUTION_ERROR (测试执行失败、崩溃或超时)");
            Console.WriteLine(@"  4: INCONCLUSIVE (样本不足、负载未成功或数据不可比)");
            Console.WriteLine();
            Console.WriteLine(@"AI 快速调用示例:");
            Console.WriteLine(@"  SonicRoute.BenchmarkLab compare --baseline old.exe --candidate new.exe --suite quick --quiet");
        }

        private static async Task<int> ExecuteVerifyAsync(string[] args)
        {
            var options = ParseOptions(args);
            string? targetExe = options.GetValueOrDefault("target");

            if (string.IsNullOrEmpty(targetExe))
            {
                // Auto-detect built SonicRoute if available in standard paths
                string candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\..\SonicRoute\bin\Release\net8.0-windows\win-x64\SonicRoute.exe");
                if (File.Exists(candidate)) targetExe = Path.GetFullPath(candidate);
            }

            return await SonicRoute.BenchmarkLab.Verification.SelfVerification.RunAllAsync(targetExe);
        }

        private static async Task<int> ExecuteCompareAsync(string[] args)
        {
            var options = ParseOptions(args);

            string? baseline = options.GetValueOrDefault("baseline");
            string? candidate = options.GetValueOrDefault("candidate");

            if (string.IsNullOrWhiteSpace(baseline)) throw new ArgumentException("Missing required option '--baseline <path|name>'.");
            if (string.IsNullOrWhiteSpace(candidate)) throw new ArgumentException("Missing required option '--candidate <path>'.");

            string suiteName = options.GetValueOrDefault("suite", "standard");
            string? scenarioId = options.GetValueOrDefault("scenario");
            string moduleStr = options.GetValueOrDefault("module", "all");
            int rounds = int.TryParse(options.GetValueOrDefault("rounds", "3"), out int r) ? Math.Max(1, r) : 3;
            double? duration = double.TryParse(options.GetValueOrDefault("duration"), out double d) ? d : null;
            double thresholdCpu = double.TryParse(options.GetValueOrDefault("threshold-cpu", "5.0"), out double tc) ? tc : 5.0;
            double thresholdMem = double.TryParse(options.GetValueOrDefault("threshold-mem", "10.0"), out double tm) ? tm : 10.0;
            bool quiet = options.ContainsKey("quiet") || options.ContainsKey("q");
            bool jsonOnly = options.ContainsKey("json");

            string outputDir = options.GetValueOrDefault("output", Path.Combine(Environment.CurrentDirectory, "benchmarks", $"compare_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
            Directory.CreateDirectory(outputDir);

            BenchmarkModule module = Enum.TryParse<BenchmarkModule>(moduleStr, true, out var m) ? m : BenchmarkModule.All;

            IReadOnlyList<IBenchmarkScenario> scenarios;
            if (!string.IsNullOrWhiteSpace(scenarioId))
            {
                var single = ScenarioRegistry.GetById(scenarioId) ?? throw new ArgumentException($"Unknown scenario ID: '{scenarioId}'.");
                scenarios = new List<IBenchmarkScenario> { single };
            }
            else
            {
                scenarios = ScenarioRegistry.GetScenariosForSuite(suiteName, module);
            }

            BenchmarkTier tier = suiteName.ToLowerInvariant() switch
            {
                "quick" => BenchmarkTier.Quick,
                "endurance" => BenchmarkTier.Endurance,
                _ => BenchmarkTier.Standard
            };

            Action<string>? logger = quiet ? null : msg => Console.WriteLine(msg);

            if (!quiet && !jsonOnly)
            {
                Console.WriteLine($"Starting A/B comparison: {baseline} vs {candidate}");
                Console.WriteLine($"Suite={suiteName}, Rounds={rounds}, Scenarios={scenarios.Count}, Tier={tier}");
            }

            var result = await ComparisonRunner.RunComparisonAsync(
                baseline,
                candidate,
                scenarios,
                tier,
                rounds,
                duration,
                thresholdCpu,
                thresholdMem,
                logger);

            // Generate reports
            JsonReporter.SaveComparisonReport(result, outputDir);
            HtmlReporter.SaveComparisonReport(result, outputDir);
            AiSummaryFormatter.SaveFiles(result, outputDir);

            // AI Output
            var (aiMd, aiJson) = AiSummaryFormatter.Generate(result, outputDir);

            if (jsonOnly)
            {
                Console.WriteLine(aiJson);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(aiMd);
            }

            return result.OverallVerdict switch
            {
                OverallVerdict.Pass => 0,
                OverallVerdict.RegressionDetected => 1,
                OverallVerdict.Inconclusive => 4,
                _ => 3
            };
        }

        private static async Task<int> ExecuteRunAsync(string[] args)
        {
            var options = ParseOptions(args);
            string? target = options.GetValueOrDefault("target");
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Missing required option '--target <path>'.");

            string suiteName = options.GetValueOrDefault("suite", "standard");
            string? scenarioId = options.GetValueOrDefault("scenario");
            string moduleStr = options.GetValueOrDefault("module", "all");
            int rounds = int.TryParse(options.GetValueOrDefault("rounds", "1"), out int r) ? Math.Max(1, r) : 1;
            double? duration = double.TryParse(options.GetValueOrDefault("duration"), out double d) ? d : null;
            string? saveBaselineName = options.GetValueOrDefault("save-baseline");

            string outputDir = options.GetValueOrDefault("output", Path.Combine(Environment.CurrentDirectory, "benchmarks", $"run_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));
            Directory.CreateDirectory(outputDir);

            BenchmarkModule module = Enum.TryParse<BenchmarkModule>(moduleStr, true, out var m) ? m : BenchmarkModule.All;

            IReadOnlyList<IBenchmarkScenario> scenarios;
            if (!string.IsNullOrWhiteSpace(scenarioId))
            {
                var single = ScenarioRegistry.GetById(scenarioId) ?? throw new ArgumentException($"Unknown scenario ID: '{scenarioId}'.");
                scenarios = new List<IBenchmarkScenario> { single };
            }
            else
            {
                scenarios = ScenarioRegistry.GetScenariosForSuite(suiteName, module);
            }

            BenchmarkTier tier = suiteName.ToLowerInvariant() switch
            {
                "quick" => BenchmarkTier.Quick,
                "endurance" => BenchmarkTier.Endurance,
                _ => BenchmarkTier.Standard
            };

            Console.WriteLine($"Running benchmark on {target} ({rounds} rounds, {scenarios.Count} scenarios)...");

            var summaries = await BenchmarkRunner.RunSuiteAsync(
                target,
                scenarios,
                tier,
                rounds,
                duration,
                Console.WriteLine,
                default);

            // Save baseline if requested
            if (!string.IsNullOrWhiteSpace(saveBaselineName))
            {
                var stored = new StoredBaseline
                {
                    Name = saveBaselineName,
                    TargetPath = target,
                    ExeSha256 = ComparisonRunner.ComputeFileSha256(target),
                    Version = FileVersionInfo.GetVersionInfo(target).FileVersion ?? "1.0",
                    Environment = SystemEnvironmentInfo.CaptureCurrent(),
                    Scenarios = summaries
                };
                BaselineStore.Save(saveBaselineName, stored);
                Console.WriteLine($"[Baseline Saved] Baseline '{saveBaselineName}' stored successfully in baseline store.");
            }

            string jsonPath = Path.Combine(outputDir, "run_summary.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Summary results written to {jsonPath}");

            return 0;
        }

        private static int ExecuteBaseline(string[] args)
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                Console.WriteLine("baseline commands: list, show <name>, delete <name>");
                return 0;
            }

            string sub = args[0].ToLowerInvariant();
            if (sub == "list")
            {
                var baselines = BaselineStore.ListAll();
                Console.WriteLine($"Found {baselines.Count} stored baseline(s):");
                foreach (var b in baselines)
                {
                    Console.WriteLine($"  - '{b.Name}' (Target: {Path.GetFileName(b.TargetPath)} v{b.Version}, Saved: {b.SavedUtc:yyyy-MM-dd HH:mm:ss} UTC, Scenarios: {b.Scenarios.Count})");
                }
                return 0;
            }
            else if (sub == "show" && args.Length > 1)
            {
                var b = BaselineStore.Load(args[1]);
                if (b == null)
                {
                    Console.Error.WriteLine($"Baseline '{args[1]}' not found.");
                    return 2;
                }
                Console.WriteLine(JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            else if (sub == "delete" && args.Length > 1)
            {
                bool deleted = BaselineStore.Delete(args[1]);
                Console.WriteLine(deleted ? $"Deleted baseline '{args[1]}'." : $"Baseline '{args[1]}' not found.");
                return 0;
            }

            Console.Error.WriteLine("Unknown baseline subcommand. Use 'list', 'show <name>', or 'delete <name>'.");
            return 2;
        }

        private static int ExecuteScenarios(string[] args)
        {
            var list = ScenarioRegistry.GetAll();
            Console.WriteLine($"Registered benchmark scenarios ({list.Count} total):");
            Console.WriteLine();
            Console.WriteLine($"{"ID",-26} {"Module",-12} {"Standard(s)",-12} {"Name"}");
            Console.WriteLine(new string('-', 80));
            foreach (var s in list)
            {
                Console.WriteLine($"{s.Id,-26} {s.Module,-12} {s.GetDurationSeconds(BenchmarkTier.Standard),-12:F1} {s.Name}");
                Console.WriteLine($"  └ Description: {s.Description}");
            }
            return 0;
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--") || arg.StartsWith("-"))
                {
                    string key = arg.TrimStart('-');
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        dict[key] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        dict[key] = "true";
                    }
                }
            }
            return dict;
        }
    }
}
