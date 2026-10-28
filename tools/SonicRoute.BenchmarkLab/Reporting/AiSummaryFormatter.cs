using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SonicRoute.BenchmarkLab.Comparison;
using SonicRoute.BenchmarkLab.Core;

namespace SonicRoute.BenchmarkLab.Reporting
{
    public sealed class AiSummaryData
    {
        public string Status { get; set; } = "PASS"; // PASS | REGRESSION | INCONCLUSIVE
        public string Baseline { get; set; } = string.Empty;
        public string Candidate { get; set; } = string.Empty;
        public int Rounds { get; set; }
        public DecisionReadiness? DecisionReadiness { get; set; }
        public string CpuModel { get; set; } = string.Empty;
        public int Cores { get; set; }
        public List<string> Regressions { get; set; } = new();
        public List<string> Improvements { get; set; } = new();
        public List<string> StableMetrics { get; set; } = new();
        public List<string> Caveats { get; set; } = new();
        public string ReportJson { get; set; } = string.Empty;
        public string ReportHtml { get; set; } = string.Empty;
    }

    public static class AiSummaryFormatter
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static (string Markdown, string Json) Generate(SuiteComparisonResult result, string outputDir)
        {
            var summaryData = new AiSummaryData
            {
                Status = !result.SourceFilesUnchanged || result.Runs.Any(r => !r.Result.Success) ? "EXECUTION_ERROR" : result.OverallVerdict switch
                {
                    OverallVerdict.Pass => "PASS",
                    OverallVerdict.RegressionDetected => "REGRESSION_DETECTED",
                    _ => "INCONCLUSIVE"
                },
                Baseline = Path.GetFileName(result.BaselineTarget) + (!string.IsNullOrEmpty(result.BaselineVersion) ? $" ({result.BaselineVersion})" : ""),
                Candidate = Path.GetFileName(result.CandidateTarget) + (!string.IsNullOrEmpty(result.CandidateVersion) ? $" ({result.CandidateVersion})" : ""),
                Rounds = result.Rounds,
                DecisionReadiness = result.DecisionReadiness,
                CpuModel = result.Environment.CpuModel,
                Cores = result.Environment.LogicalProcessorCount,
                Regressions = new List<string>(result.RegressionsSummary),
                Improvements = new List<string>(result.ImprovementsSummary),
                Caveats = new List<string>(result.Limitations),
                ReportJson = Path.Combine(outputDir, "report.json"),
                ReportHtml = Path.Combine(outputDir, "report.html")
            };

            // Aggregate stable metrics
            int stableCount = 0;
            foreach (var s in result.Scenarios)
            {
                foreach (var m in s.MetricComparisons.Where(mc => mc.Verdict == MetricVerdict.Stable))
                {
                    stableCount++;
                }
            }
            if (stableCount > 0)
            {
                summaryData.StableMetrics.Add($"{stableCount} 项常规指标（CPU时间、工作集、私有内存、句柄等）在预设噪声容限内保持平稳");
            }

            // Build Markdown
            var sb = new StringBuilder();
            string statusIcon = summaryData.Status switch
            {
                "PASS" => "🟢 PASS (本次未检出超阈值退化)",
                "REGRESSION_DETECTED" => "🔴 REGRESSION (发现退化)",
                "EXECUTION_ERROR" => "🔴 EXECUTION_ERROR (执行失败；查看逐轮证据)",
                _ => "🟡 INCONCLUSIVE (证据不足、负载未确认或波动未收敛)"
            };

            sb.AppendLine($"### SonicRoute 性能对比结论: {statusIcon}");
            sb.AppendLine();
            sb.AppendLine($"- **对比目标**: `{summaryData.Baseline}` ➔ `{summaryData.Candidate}` ({summaryData.Rounds} 轮，{(result.IsAaControl ? "A/A 对照" : result.UsesStoredBaseline ? "历史基线，非交替" : "A/B 交替")})");
            sb.AppendLine($"- **宿主环境**: {summaryData.CpuModel} ({summaryData.Cores} 逻辑核) | {result.Environment.OperatingSystem}");
            if (summaryData.DecisionReadiness is { } readiness)
            {
                sb.AppendLine($"- **推荐决策协议**: {(readiness.RecommendedProtocolMet ? "已满足（不证明因果）" : "尚未满足；不得只凭 PASS 宣称最终性能通过")}");
                foreach (string reason in readiness.Reasons) summaryData.Caveats.Add("Decision protocol: " + reason);
            }
            sb.AppendLine();

            if (summaryData.Regressions.Count > 0)
            {
                sb.AppendLine("#### ⚠️ 发现性能退化项 (需优先排查):");
                foreach (var reg in summaryData.Regressions)
                {
                    sb.AppendLine($"- **{reg}**");
                }
                sb.AppendLine();
            }

            if (summaryData.Improvements.Count > 0)
            {
                sb.AppendLine("#### ⚡ 显著改善项:");
                foreach (var imp in summaryData.Improvements)
                {
                    sb.AppendLine($"- {imp}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("#### 📊 稳定指标概要:");
            foreach (var st in summaryData.StableMetrics)
            {
                sb.AppendLine($"- {st}");
            }
            sb.AppendLine();

            if (summaryData.Caveats.Count > 0)
            {
                sb.AppendLine("#### ℹ️ 测试限制与说明:");
                foreach (var c in summaryData.Caveats)
                {
                    sb.AppendLine($"- {c}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("#### 📁 完整报告与证据:");
            sb.AppendLine($"- 结构化数据: `file:///{summaryData.ReportJson.Replace('\\', '/')}`");
            sb.AppendLine($"- 可视化图表: `file:///{summaryData.ReportHtml.Replace('\\', '/')}`");

            string markdown = sb.ToString();
            string json = JsonSerializer.Serialize(summaryData, JsonOptions);

            return (markdown, json);
        }

        public static void SaveFiles(SuiteComparisonResult result, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            var (md, json) = Generate(result, outputDir);

            string mdPath = Path.Combine(outputDir, "ai-summary.md");
            string jsonPath = Path.Combine(outputDir, "ai-summary.json");

            File.WriteAllText(mdPath, md, Encoding.UTF8);
            File.WriteAllText(jsonPath, json, Encoding.UTF8);

            result.AiSummaryMdPath = mdPath;
        }
    }
}
