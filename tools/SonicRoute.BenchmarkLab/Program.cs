using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
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
            if (args.FirstOrDefault() == "__worker") return await Verification.SelfVerification.RunWorkerAsync(args.Skip(1).ToArray());

            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            string command = args[0].ToLowerInvariant();
            var remainingArgs = args.Skip(1).ToArray();
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;

            try
            {
                return command switch
                {
                    "compare" or "ab" => await ExecuteCompareAsync(remainingArgs, cancellation.Token),
                    "run" => await ExecuteRunAsync(remainingArgs, cancellation.Token),
                    "plan" => ExecutePlan(remainingArgs),
                    "report" => ExecuteReport(remainingArgs),
                    "baseline" => ExecuteBaseline(remainingArgs),
                    "scenarios" => ExecuteScenarios(remainingArgs),
                    "verify" or "selftest" => await ExecuteVerifyAsync(remainingArgs),
                    _ => ShowUnknownCommand(command)
                };
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { Console.Error.WriteLine("Canceled; owned test processes are being released."); return 130; }
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
            finally { Console.CancelKeyPress -= cancel; }
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
            Console.WriteLine(@"  plan            预览场景、轮次顺序、桌面影响和预计时长；不启动目标");
            Console.WriteLine(@"  report          --input <报告目录> [--finalize] [--json]；将 AI 写入的 ai-review.json 生成为只读网页");
            Console.WriteLine(@"  baseline        管理已保存的历史性能基线 (list, show, delete)");
            Console.WriteLine(@"  scenarios       列出所有已注册的测试场景");
            Console.WriteLine(@"  verify          计算/报告/真实自建子进程检查；--target 显式追加软件隔离检查");
            Console.WriteLine();
            Console.WriteLine(@"'compare' 核心选项:");
            Console.WriteLine(@"  --aa                       以 candidate 自身做 A/A 重复性对照");
            Console.WriteLine(@"  --mode <targeted|light|full> 功能专项 / 轻量冒烟 / 全部自动化性能场景");
            Console.WriteLine(@"  --feature <name[,name...]> 专项：idle,rules,process,foreground,delay,hotkeys,schedule,window,endurance");
            Console.WriteLine(@"  --args <string>            generic_process 的启动参数（直接传给程序）");
            Console.WriteLine(@"  --allow-desktop-interaction 显式允许窗口、前台切换或全局快捷键场景");
            Console.WriteLine(@"  --baseline <path|name>      基准路径或已保存基线名称（--aa 时省略）");
            Console.WriteLine(@"  --candidate <path>          待测候选版本可执行文件路径 (必需)");
            Console.WriteLine(@"  --scenario <id[,id...]>     选择一个或多个指定场景，按给定顺序执行");
            Console.WriteLine(@"  --suite <quick|standard|endurance> 测试套件预设 (默认: standard)");
            Console.WriteLine(@"  --module <all|automation|hotkey|ui|general> 按受影响模块过滤场景");
            Console.WriteLine(@"  --rounds <n>                测试交替轮次 (默认: 3 轮)");
            Console.WriteLine(@"  --duration <seconds>        覆盖每阶段测试时长");
            Console.WriteLine(@"  --warmup <seconds>          覆盖每次启动后的预热，写入协议；默认随模式");
            Console.WriteLine(@"  --baseline-max-age-days <n> 历史基线最大天数，默认7；过期不作门禁");
            Console.WriteLine(@"  --interval <ms>             资源采样间隔20–1000ms，默认200ms；CPU仍按端点计量");
            Console.WriteLine(@"  --threshold-cpu <percent>   CPU 退化报警阈值百分比 (默认: 5.0%)");
            Console.WriteLine(@"  --threshold-mem <percent>   内存退化报警阈值百分比 (默认: 10.0%)");
            Console.WriteLine(@"  --output <dir>              报告输出目录 (默认: ./benchmarks/<run_id>/)");
            Console.WriteLine(@"  --quiet, -q                 安静模式: 仅输出极简 AI 总结");
            Console.WriteLine(@"  --json                      以纯 JSON 格式输出 AI 报告");
            Console.WriteLine();
            Console.WriteLine(@"退出码说明:");
            Console.WriteLine(@"  0: PASS (本次未检出超阈值退化；run 仅表示测量完成)");
            Console.WriteLine(@"  1: REGRESSION (检测到性能显著退化)");
            Console.WriteLine(@"  2: INVALID_ARGUMENTS (命令行参数错误或文件不存在)");
            Console.WriteLine(@"  3: EXECUTION_ERROR (测试执行失败、崩溃或超时)");
            Console.WriteLine(@"  4: INCONCLUSIVE (重复性不足、波动或数据不可比；负载失败为3)");
            Console.WriteLine();
            Console.WriteLine(@"AI 快速调用示例:");
            Console.WriteLine(@"  SonicRoute.BenchmarkLab compare --baseline old.exe --candidate new.exe --suite quick --quiet");
        }

        private static async Task<int> ExecuteVerifyAsync(string[] args)
        {
            var options = ParseOptions(args);
            string? targetExe = options.GetValueOrDefault("target");
            if (targetExe != null && !File.Exists(targetExe)) throw new FileNotFoundException("Explicit verification target not found.", targetExe);
            string? evidenceDirectory = options.GetValueOrDefault("output");
            if (evidenceDirectory != null) CreateOutputDirectory(evidenceDirectory);
            return await SonicRoute.BenchmarkLab.Verification.SelfVerification.RunAllAsync(targetExe, evidenceDirectory, options.GetValueOrDefault("feature"));
        }

        private static int ExecuteReport(string[] args)
        {
            var options = ParseOptions(args, reportOnly: true);
            if (!options.TryGetValue("input", out var input) || string.IsNullOrWhiteSpace(input)) throw new ArgumentException("report requires --input <report directory>.");
            if (options.Keys.Any(key => key.ToLowerInvariant() is not ("input" or "finalize" or "json"))) throw new ArgumentException("report only accepts --input, --finalize and --json.");
            var result = ReviewPublisher.Publish(input, options.ContainsKey("finalize"));
            if (options.ContainsKey("json")) Console.WriteLine(JsonSerializer.Serialize(result));
            else Console.WriteLine($"Review: {result.ReviewStatus}\nReport: {result.ReportUrl}\nAI writes: {result.ReviewFile}");
            return 0;
        }

        private static int ExecutePlan(string[] args)
        {
            var options = ParseOptions(args);
            TestModes.Apply(options);
            bool compare = options.ContainsKey("candidate") || options.ContainsKey("baseline") || options.ContainsKey("aa");
            string suite = options.GetValueOrDefault("suite", "standard");
            int rounds = IntegerOption(options, "rounds", compare ? 3 : 1);
            int interval = IntegerOption(options, "interval", 200);
            if (interval < 20) throw new ArgumentException("--interval must be from 20 to 1000 milliseconds.");
            double? duration = options.ContainsKey("duration") ? NumberOption(options, "duration", 10, 0.5) : null;
            var selected = SelectScenarios(options.GetValueOrDefault("scenario"), suite, options.GetValueOrDefault("module", "all"));
            var scenarios = ConfigureScenarios(selected, suite, options, preview: true);
            bool historical = options.TryGetValue("baseline", out var baseline) && !options.ContainsKey("aa")
                && !baseline.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            double? warmup = options.ContainsKey("warmup") ? NumberOption(options, "warmup", 5) : null;
            var plan = BenchmarkPlan.Create(scenarios, ResolveTier(suite), rounds, duration, interval, compare, historical, options.ContainsKey("allow-desktop-interaction"), warmup);
            if (options.ContainsKey("json")) Console.WriteLine(JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
            else
            {
                Console.WriteLine($"BenchmarkLab 1.2 plan: {plan.Mode}, {rounds} rounds, {plan.TargetLaunchCount} measured launches");
                Console.WriteLine($"Measurement {plan.MeasurementSeconds:F1}s + warmup {plan.WarmupSeconds:F1}s + cooldown {plan.CooldownSeconds:F1}s = {plan.PlannedSecondsExcludingStartupReadinessAndWorkloadOverruns:F1}s (startup/readiness/cleanup extra)");
                foreach (var run in plan.Runs) Console.WriteLine($"{run.Order,3}. round {run.Round} {run.Side,-9} {run.ScenarioId}: warmup {run.WarmupSeconds:F1}s, measure {run.DurationSeconds:F1}s");
                foreach (string warning in plan.Warnings) Console.WriteLine("Note: " + warning);
            }
            return 0;
        }

        private static BenchmarkTier ResolveTier(string suite) => suite.ToLowerInvariant() switch
        { "quick" => BenchmarkTier.Quick, "endurance" => BenchmarkTier.Endurance, _ => BenchmarkTier.Standard };

        private static IReadOnlyList<IBenchmarkScenario> SelectScenarios(string? ids, string suite, string module)
        {
            if (!Enum.TryParse<BenchmarkModule>(module, true, out var parsed) || !Enum.IsDefined(parsed)) throw new ArgumentException("Unknown module: " + module);
            if (string.IsNullOrWhiteSpace(ids)) return ScenarioRegistry.GetScenariosForSuite(suite, parsed);
            var selected = new List<IBenchmarkScenario>();
            foreach (var id in ids.Split(',', StringSplitOptions.TrimEntries))
            {
                var scenario = ScenarioRegistry.GetById(id) ?? throw new ArgumentException("Unknown scenario ID: '" + id + "'.");
                if (selected.Any(s => s.Id == scenario.Id)) throw new ArgumentException("Duplicate scenario: " + id);
                selected.Add(scenario);
            }
            return selected;
        }

        private static async Task<int> ExecuteCompareAsync(string[] args, CancellationToken token)
        {
            var options = ParseOptions(args);
            TestModes.Apply(options);

            string? baseline = options.GetValueOrDefault("baseline");
            string? candidate = options.GetValueOrDefault("candidate");
            if (options.ContainsKey("aa")) baseline = candidate;

            if (string.IsNullOrWhiteSpace(baseline)) throw new ArgumentException("Missing required option '--baseline <path|name>'.");
            if (string.IsNullOrWhiteSpace(candidate)) throw new ArgumentException("Missing required option '--candidate <path>'.");

            string suiteName = options.GetValueOrDefault("suite", "standard");
            string? scenarioId = options.GetValueOrDefault("scenario");
            string moduleStr = options.GetValueOrDefault("module", "all");
            int rounds = IntegerOption(options, "rounds", 3);
            int interval = IntegerOption(options, "interval", 200);
            if (interval < 20) throw new ArgumentException("--interval must be from 20 to 1000 milliseconds.");
            double? duration = options.ContainsKey("duration") ? NumberOption(options, "duration", 10, 0.5) : null;
            double thresholdCpu = NumberOption(options, "threshold-cpu", 5);
            double thresholdMem = NumberOption(options, "threshold-mem", 10);
            bool quiet = options.ContainsKey("quiet") || options.ContainsKey("q");
            bool jsonOnly = options.ContainsKey("json");

            string outputDir = options.GetValueOrDefault("output", Path.Combine(Environment.CurrentDirectory, "benchmarks", $"compare_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));

            BenchmarkModule module = Enum.TryParse<BenchmarkModule>(moduleStr, true, out var m) ? m : BenchmarkModule.All;

            IReadOnlyList<IBenchmarkScenario> scenarios = SelectScenarios(scenarioId, suiteName, moduleStr);
            scenarios = ConfigureScenarios(scenarios, suiteName, options);
            if (!File.Exists(candidate)) throw new FileNotFoundException("Candidate not found.", candidate);
            CreateOutputDirectory(outputDir);

            BenchmarkTier tier = suiteName.ToLowerInvariant() switch
            {
                "quick" => BenchmarkTier.Quick,
                "endurance" => BenchmarkTier.Endurance,
                _ => BenchmarkTier.Standard
            };

            Action<string>? logger = quiet || jsonOnly ? null : msg => Console.WriteLine(msg);

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
                logger, token, interval,
                options.ContainsKey("warmup") ? NumberOption(options, "warmup", 5) : null,
                NumberOption(options, "baseline-max-age-days", 7, 0.01));

            // Generate reports
            result.ReportHtmlPath = Path.Combine(outputDir, "report.html");
            var (aiMd, aiJson) = AiSummaryFormatter.Generate(result, outputDir);
            result.AiSummaryMdPath = Path.Combine(outputDir, "ai-summary.md");
            File.WriteAllText(result.AiSummaryMdPath, aiMd, Encoding.UTF8);
            File.WriteAllText(Path.Combine(outputDir, "ai-summary.json"), aiJson, Encoding.UTF8);
            JsonReporter.SaveComparisonReport(result, outputDir);
            ReviewPublisher.Publish(outputDir);

            if (jsonOnly)
            {
                Console.WriteLine(aiJson);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(aiMd);
            }

            if (!result.SourceFilesUnchanged || result.Runs.Any(r => !r.Result.Success)) return 3;
            return result.OverallVerdict switch
            {
                OverallVerdict.Pass => 0,
                OverallVerdict.RegressionDetected => 1,
                OverallVerdict.Inconclusive => 4,
                _ => 3
            };
        }

        private static async Task<int> ExecuteRunAsync(string[] args, CancellationToken token)
        {
            var options = ParseOptions(args);
            TestModes.Apply(options);
            string? target = options.GetValueOrDefault("target");
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Missing required option '--target <path>'.");

            string suiteName = options.GetValueOrDefault("suite", "standard");
            string? scenarioId = options.GetValueOrDefault("scenario");
            string moduleStr = options.GetValueOrDefault("module", "all");
            int rounds = IntegerOption(options, "rounds", 1);
            int interval = IntegerOption(options, "interval", 200);
            if (interval < 20) throw new ArgumentException("--interval must be from 20 to 1000 milliseconds.");
            double? duration = options.ContainsKey("duration") ? NumberOption(options, "duration", 10, 0.5) : null;
            string? saveBaselineName = options.GetValueOrDefault("save-baseline");

            string outputDir = options.GetValueOrDefault("output", Path.Combine(Environment.CurrentDirectory, "benchmarks", $"run_{DateTime.UtcNow:yyyyMMdd_HHmmss}"));

            BenchmarkModule module = Enum.TryParse<BenchmarkModule>(moduleStr, true, out var m) ? m : BenchmarkModule.All;

            IReadOnlyList<IBenchmarkScenario> scenarios = SelectScenarios(scenarioId, suiteName, moduleStr);
            scenarios = ConfigureScenarios(scenarios, suiteName, options);
            if (!File.Exists(target)) throw new FileNotFoundException("Target not found.", target);
            CreateOutputDirectory(outputDir);

            BenchmarkTier tier = suiteName.ToLowerInvariant() switch
            {
                "quick" => BenchmarkTier.Quick,
                "endurance" => BenchmarkTier.Endurance,
                _ => BenchmarkTier.Standard
            };

            bool jsonOnly = options.ContainsKey("json");
            Action<string>? logger = jsonOnly || options.ContainsKey("quiet") || options.ContainsKey("q") ? null : Console.WriteLine;
            logger?.Invoke($"Running benchmark on {target} ({rounds} rounds, {scenarios.Count} scenarios)...");
            var evidence = new List<ScenarioResult>();
            var targetFiles = ComparisonRunner.CaptureTargetFiles(target);
            var startedUtc = DateTime.UtcNow;

            var summaries = await BenchmarkRunner.RunSuiteAsync(
                target,
                scenarios,
                tier,
                rounds,
                duration,
                logger,
                token, evidence, interval, options.ContainsKey("warmup") ? NumberOption(options, "warmup", 5) : null);

            string jsonPath = Path.Combine(outputDir, "run_summary.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outputDir, "run_evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            var protocol = MeasurementProtocol.Create(scenarios, tier, duration, interval, options.ContainsKey("warmup") ? NumberOption(options, "warmup", 5) : null);
            bool sourceFilesUnchanged = true;
            string? sourceError = null;
            try { ComparisonRunner.VerifyFilesUnchanged(target, targetFiles); }
            catch (IOException error) { sourceFilesUnchanged = false; sourceError = error.Message; }
            var metadata = new { SchemaVersion = 3, ToolVersion = "1.2", Protocol = protocol, TimestampUtc = startedUtc, Target = target, TargetFiles = targetFiles,
                SourceFilesUnchanged = sourceFilesUnchanged, SourceError = sourceError,
                Tier = tier.ToString(), RequestedDurationSeconds = duration, SampleIntervalMs = interval, Rounds = rounds, Environment = SystemEnvironmentInfo.CaptureCurrent() };
            File.WriteAllText(Path.Combine(outputDir, "run_metadata.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            ReviewPublisher.Publish(outputDir);
            if (!sourceFilesUnchanged || evidence.Any(r => !r.Success))
            {
                if (jsonOnly) Console.WriteLine(JsonSerializer.Serialize(new { Status = "EXECUTION_ERROR", Report = jsonPath, Evidence = "run_evidence.json" }));
                else Console.Error.WriteLine("One or more workloads or measurements failed; evidence saved, baseline not saved.");
                return 3;
            }

            if (evidence.Any(r => r.Metrics.SamplingQuality?.SuitableForComparison == false))
            {
                if (jsonOnly) Console.WriteLine(JsonSerializer.Serialize(new { Status = "INCONCLUSIVE", Report = jsonPath, Reason = "Sampling quality is unsuitable; baseline not saved." }));
                else Console.Error.WriteLine("Sampling gaps prevent a valid comparison baseline; evidence saved, baseline not saved.");
                return 4;
            }

            // Save baseline if requested
            if (!string.IsNullOrWhiteSpace(saveBaselineName))
            {
                var stored = new StoredBaseline
                {
                    SchemaVersion = 3, Protocol = protocol, Tier = tier.ToString(), RequestedDurationSeconds = duration,
                    SampleIntervalMs = interval,
                    TargetFiles = targetFiles,
                    Rounds = evidence.GroupBy(r => r.ScenarioId).ToDictionary(g => g.Key, g => g.Select(r => r.Metrics).ToList()),
                    Name = saveBaselineName,
                    TargetPath = target,
                    ExeSha256 = ComparisonRunner.ComputeFileSha256(target),
                    Version = FileVersionInfo.GetVersionInfo(target).FileVersion ?? "1.0",
                    Environment = SystemEnvironmentInfo.CaptureCurrent(),
                    Scenarios = summaries
                };
                BaselineStore.Save(saveBaselineName, stored);
                logger?.Invoke($"[Baseline Saved] Baseline '{saveBaselineName}' stored successfully in baseline store.");
            }

            if (jsonOnly) Console.WriteLine(JsonSerializer.Serialize(new { Status = "MEASURED", Report = jsonPath, Rounds = rounds }));
            else logger?.Invoke($"Summary results written to {jsonPath}");

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

        private static Dictionary<string, string> ParseOptions(string[] args, bool reportOnly = false)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new HashSet<string>(reportOnly ? new[] { "json", "finalize" } : new[] { "quiet", "q", "json", "aa", "allow-desktop-interaction" }, StringComparer.OrdinalIgnoreCase);
            var values = new HashSet<string>(reportOnly ? new[] { "input" } : new[] { "baseline", "candidate", "target", "suite", "scenario", "module", "mode", "feature", "rounds", "duration", "warmup", "baseline-max-age-days", "interval", "threshold-cpu", "threshold-mem", "output", "save-baseline", "args" }, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--") || arg.StartsWith("-"))
                {
                    string key = arg.TrimStart('-');
                    if (flags.Contains(key)) { dict[key] = "true"; continue; }
                    if (!values.Contains(key)) throw new ArgumentException("Unknown option: " + arg);
                    if (i + 1 < args.Length && (key == "args" || !args[i + 1].StartsWith("-")))
                    {
                        dict[key] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        throw new ArgumentException("Missing value for " + arg);
                    }
                }
                else throw new ArgumentException("Unexpected argument: " + arg);
            }
            return dict;
        }

        private static void CreateOutputDirectory(string path)
        {
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                throw new ArgumentException("Use a fresh output directory; existing evidence is not overwritten.");
            Directory.CreateDirectory(path);
        }

        private static int IntegerOption(Dictionary<string, string> options, string name, int fallback)
        {
            if (!options.TryGetValue(name, out var text)) return fallback;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1 || value > 1000)
                throw new ArgumentException($"--{name} must be an integer from 1 to 1000.");
            return value;
        }

        private static double NumberOption(Dictionary<string, string> options, string name, double fallback, double minimum = 0)
        {
            if (!options.TryGetValue(name, out var text)) return fallback;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < minimum || value > 86400)
                throw new ArgumentException($"--{name} must be finite and between {minimum} and 86400.");
            return value;
        }

        private static IReadOnlyList<IBenchmarkScenario> ConfigureScenarios(IReadOnlyList<IBenchmarkScenario> scenarios, string suite, Dictionary<string, string> options, bool preview = false)
        {
            if (suite.ToLowerInvariant() is not ("quick" or "standard" or "endurance" or "automation" or "optimization")) throw new ArgumentException("Unknown suite: " + suite);
            if (options.TryGetValue("module", out var module) && (!Enum.TryParse<BenchmarkModule>(module, true, out var parsed) || !Enum.IsDefined(parsed))) throw new ArgumentException("Unknown module: " + module);
            if (scenarios.Count == 0) throw new ArgumentException("No scenarios match the request.");
            if (!preview && !options.ContainsKey("allow-desktop-interaction") && scenarios.Any(BenchmarkPlan.RequiresDesktop))
                throw new ArgumentException("This suite includes UI/foreground/global-hotkey activity. Select a background-only scenario/quick suite or explicitly pass --allow-desktop-interaction.");
            return scenarios.Select(s => s.Id == "generic_process" ? new GenericProcessScenario(options.GetValueOrDefault("args", "")) : s).ToArray();
        }
    }
}
