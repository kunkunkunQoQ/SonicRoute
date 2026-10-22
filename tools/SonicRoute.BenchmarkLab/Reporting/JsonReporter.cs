using System;
using System.IO;
using System.Text;
using System.Text.Json;
using SonicRoute.BenchmarkLab.Comparison;

namespace SonicRoute.BenchmarkLab.Reporting
{
    public static class JsonReporter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static string SaveComparisonReport(SuiteComparisonResult result, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            string reportPath = Path.Combine(outputDir, "report.json");

            result.ReportJsonPath = reportPath;
            string json = JsonSerializer.Serialize(result, JsonOptions);
            File.WriteAllText(reportPath, json, Encoding.UTF8);

            return reportPath;
        }
    }
}
