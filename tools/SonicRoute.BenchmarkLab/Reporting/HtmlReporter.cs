using System.Reflection;
using System.Text;
using System.Text.Json;
using SonicRoute.BenchmarkLab.Comparison;

namespace SonicRoute.BenchmarkLab.Reporting;

public static class HtmlReporter
{
    // Share the browser-local viewer. The default JSON encoder escapes HTML-sensitive
    // characters, so imported names cannot close the embedded data script.
    public static string SaveComparisonReport(SuiteComparisonResult result, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string path = Path.Combine(outputDir, "report.html");
        result.ReportHtmlPath = path;
        return SaveDocument(result, path);
    }

    public static string SaveRunReport(object evidence, object metadata, object summary, string outputDir) => SaveDocument(
        new { ViewerSchemaVersion = 1, SourceName = "run_evidence.json", Source = evidence, Metadata = metadata, Summary = summary },
        Path.Combine(outputDir, "report.html"));

    private static string SaveDocument(object data, string path)
    {
        string html = ReadAsset("index.html")
            .Replace("<link rel=\"stylesheet\" href=\"style.css\">", "<style>" + ReadAsset("style.css") + "</style>")
            .Replace("<script id=\"report-data\" type=\"application/json\">null</script>",
                "<script id=\"report-data\" type=\"application/json\">" + JsonSerializer.Serialize(data) + "</script>")
            .Replace("<script src=\"data.js\"></script>", "<script>" + ReadAsset("data.js") + "</script>")
            .Replace("<script src=\"app.js\"></script>", "<script>" + ReadAsset("app.js") + "</script>");
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return path;
    }

    private static string ReadAsset(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BenchmarkViewer." + file)
            ?? throw new InvalidOperationException("Missing embedded viewer asset: " + file);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
