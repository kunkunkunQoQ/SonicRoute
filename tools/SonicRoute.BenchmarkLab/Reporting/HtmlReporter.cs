using System;
using System.IO;
using System.Linq;
using System.Text;
using SonicRoute.BenchmarkLab.Comparison;

namespace SonicRoute.BenchmarkLab.Reporting
{
    public static class HtmlReporter
    {
        public static string SaveComparisonReport(SuiteComparisonResult result, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            string htmlPath = Path.Combine(outputDir, "report.html");

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine("  <title>SonicRoute Benchmark Lab - 性能对比报告</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine("    :root { --bg: #f8fafc; --card: #ffffff; --text: #1e293b; --subtext: #64748b; --border: #e2e8f0; --accent: #2563eb; --pass: #16a34a; --fail: #dc2626; --warn: #d97706; }");
            sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: var(--bg); color: var(--text); margin: 0; padding: 24px; }");
            sb.AppendLine("    .container { max-width: 1100px; margin: 0 auto; }");
            sb.AppendLine("    .header { margin-bottom: 24px; }");
            sb.AppendLine("    h1 { margin: 0 0 8px 0; font-size: 24px; font-weight: 700; }");
            sb.AppendLine("    .sub { color: var(--subtext); font-size: 14px; }");
            sb.AppendLine("    .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; margin-bottom: 24px; }");
            sb.AppendLine("    .card { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 16px; box-shadow: 0 1px 3px rgba(0,0,0,0.05); }");
            sb.AppendLine("    .card-title { font-size: 12px; font-weight: 600; text-transform: uppercase; color: var(--subtext); margin-bottom: 6px; }");
            sb.AppendLine("    .card-val { font-size: 20px; font-weight: 700; }");
            sb.AppendLine("    .badge { display: inline-block; padding: 4px 10px; border-radius: 9999px; font-size: 12px; font-weight: 600; }");
            sb.AppendLine("    .badge-pass { background: #dcfce7; color: #15803d; }");
            sb.AppendLine("    .badge-fail { background: #fee2e2; color: #b91c1c; }");
            sb.AppendLine("    .badge-warn { background: #fef3c7; color: #b45309; }");
            sb.AppendLine("    .badge-neutral { background: #f1f5f9; color: #475569; }");
            sb.AppendLine("    .section { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 20px; margin-bottom: 24px; box-shadow: 0 1px 3px rgba(0,0,0,0.05); }");
            sb.AppendLine("    .section-title { font-size: 16px; font-weight: 700; margin-top: 0; margin-bottom: 16px; border-bottom: 1px solid var(--border); padding-bottom: 8px; }");
            sb.AppendLine("    table { width: 100%; border-collapse: collapse; font-size: 13px; text-align: left; }");
            sb.AppendLine("    th { background: #f8fafc; color: var(--subtext); font-weight: 600; padding: 10px 12px; border-bottom: 1px solid var(--border); }");
            sb.AppendLine("    td { padding: 10px 12px; border-bottom: 1px solid var(--border); }");
            sb.AppendLine("    tr:last-child td { border-bottom: none; }");
            sb.AppendLine("    .alert { padding: 12px 16px; border-radius: 6px; margin-bottom: 16px; font-size: 13px; }");
            sb.AppendLine("    .alert-danger { background: #fef2f2; border-left: 4px solid var(--fail); color: #991b1b; }");
            sb.AppendLine("    .alert-success { background: #f0fdf4; border-left: 4px solid var(--pass); color: #166534; }");
            sb.AppendLine("    .alert-info { background: #f8fafc; border-left: 4px solid var(--accent); color: #1e3a8a; }");
            sb.AppendLine("    .chart-container { margin: 16px 0; }");
            sb.AppendLine("    .bar-row { display: flex; align-items: center; margin-bottom: 8px; font-size: 12px; }");
            sb.AppendLine("    .bar-label { width: 140px; text-overflow: ellipsis; overflow: hidden; white-space: nowrap; font-weight: 500; }");
            sb.AppendLine("    .bar-track { flex: 1; height: 18px; background: #e2e8f0; border-radius: 4px; overflow: hidden; position: relative; margin: 0 8px; }");
            sb.AppendLine("    .bar-fill { height: 100%; transition: width 0.3s; }");
            sb.AppendLine("    .bar-val { width: 90px; text-align: right; font-weight: 600; }");
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("<div class=\"container\">");

            // Header
            sb.AppendLine("  <div class=\"header\">");
            sb.AppendLine("    <h1>SonicRoute Benchmark Lab - 性能对比报告</h1>");
            sb.AppendLine($"    <div class=\"sub\">生成时间: {result.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC | 运行轮次: {result.Rounds} 轮交替</div>");
            sb.AppendLine("  </div>");

            // KPI Cards
            string verdictBadge = result.OverallVerdict == OverallVerdict.Pass
                ? "<span class=\"badge badge-pass\">PASS (未超阈值)</span>"
                : "<span class=\"badge badge-fail\">REGRESSION (检测到性能退化)</span>";

            sb.AppendLine("  <div class=\"cards\">");
            sb.AppendLine($"    <div class=\"card\"><div class=\"card-title\">综合判定</div><div class=\"card-val\">{verdictBadge}</div></div>");
            sb.AppendLine($"    <div class=\"card\"><div class=\"card-title\">基线版本</div><div class=\"card-val\" style=\"font-size:16px;\">{Path.GetFileName(result.BaselineTarget)}</div><div class=\"sub\">{result.BaselineVersion}</div></div>");
            sb.AppendLine($"    <div class=\"card\"><div class=\"card-title\">待测版本</div><div class=\"card-val\" style=\"font-size:16px;\">{Path.GetFileName(result.CandidateTarget)}</div><div class=\"sub\">{result.CandidateVersion}</div></div>");
            sb.AppendLine($"    <div class=\"card\"><div class=\"card-title\">测试环境</div><div class=\"card-val\" style=\"font-size:14px;\">{result.Environment.CpuModel}</div><div class=\"sub\">{result.Environment.LogicalProcessorCount} 逻辑核 / {result.Environment.OperatingSystem}</div></div>");
            sb.AppendLine("  </div>");

            // Summary Findings
            if (result.RegressionsSummary.Count > 0)
            {
                sb.AppendLine("  <div class=\"alert alert-danger\">");
                sb.AppendLine("    <strong>⚠️ 检测到以下性能退化项目:</strong><br>");
                foreach (var reg in result.RegressionsSummary)
                {
                    sb.AppendLine($"    • {reg}<br>");
                }
                sb.AppendLine("  </div>");
            }
            else
            {
                sb.AppendLine("  <div class=\"alert alert-success\">");
                sb.AppendLine("    <strong>🟢 所有测试场景常规指标均在预设阈值以内，未发现性能退化。</strong>");
                sb.AppendLine("  </div>");
            }

            if (result.ImprovementsSummary.Count > 0)
            {
                sb.AppendLine("  <div class=\"alert alert-info\">");
                sb.AppendLine("    <strong>⚡ 检测到以下显著性能改善:</strong><br>");
                foreach (var imp in result.ImprovementsSummary)
                {
                    sb.AppendLine($"    • {imp}<br>");
                }
                sb.AppendLine("  </div>");
            }

            // Scenarios Table
            foreach (var scenario in result.Scenarios)
            {
                sb.AppendLine("  <div class=\"section\">");
                string sBadge = scenario.Verdict == OverallVerdict.Pass
                    ? "<span class=\"badge badge-pass\">PASS</span>"
                    : "<span class=\"badge badge-fail\">REGRESSION</span>";

                sb.AppendLine($"    <div class=\"section-title\" style=\"display:flex; justify-content:space-between; align-items:center;\"><span>场景: {scenario.ScenarioName} ({scenario.ScenarioId})</span>{sBadge}</div>");

                sb.AppendLine("    <table>");
                sb.AppendLine("      <thead><tr><th>指标名称</th><th>基线均值</th><th>待测均值</th><th>绝对差值</th><th>变化幅度</th><th>阈值</th><th>判定</th></tr></thead>");
                sb.AppendLine("      <tbody>");

                foreach (var m in scenario.MetricComparisons)
                {
                    string mBadge = m.Verdict switch
                    {
                        MetricVerdict.Regressed => "<span class=\"badge badge-fail\">退化</span>",
                        MetricVerdict.Improved => "<span class=\"badge badge-pass\">改善</span>",
                        _ => "<span class=\"badge badge-neutral\">稳定</span>"
                    };

                    sb.AppendLine($"        <tr>");
                    sb.AppendLine($"          <td><strong>{m.MetricName}</strong></td>");
                    sb.AppendLine($"          <td>{m.BaselineValue:F2} {m.Unit}</td>");
                    sb.AppendLine($"          <td>{m.CandidateValue:F2} {m.Unit}</td>");
                    sb.AppendLine($"          <td>{m.DeltaAbsolute:+0.00;-0.00} {m.Unit}</td>");
                    sb.AppendLine($"          <td><strong>{(m.DeltaPercentDefined ? m.DeltaPercent.ToString("+0.0;-0.0") + "%" : "N/A (zero baseline)")}</strong></td>");
                    sb.AppendLine($"          <td>+{m.ThresholdPercent:0}%</td>");
                    sb.AppendLine($"          <td>{mBadge}</td>");
                    sb.AppendLine($"        </tr>");
                }

                sb.AppendLine("      </tbody>");
                sb.AppendLine("    </table>");
                sb.AppendLine("  </div>");
            }

            // Footer
            sb.AppendLine("  <div class=\"sub\" style=\"text-align:center; padding: 20px;\">SonicRoute Benchmark Lab • Automated Zero-Token Performance Report</div>");
            sb.AppendLine("</div>");
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            result.ReportHtmlPath = htmlPath;
            File.WriteAllText(htmlPath, sb.ToString(), Encoding.UTF8);
            return htmlPath;
        }
    }
}
