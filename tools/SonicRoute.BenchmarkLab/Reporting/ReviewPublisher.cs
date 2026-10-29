using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonicRoute.BenchmarkLab.Reporting;

// The executing AI writes the review beside the immutable measurements. Readers
// receive one ready-to-read HTML file; no browser editing or model API is needed.
public static class ReviewPublisher
{
    public const string Schema = "BenchmarkLab.AIReview.v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public sealed class Author { public string Kind { get; set; } = "ai"; public string Model { get; set; } = ""; }
    public sealed class Finding { public string Title { get; set; } = ""; public string Detail { get; set; } = ""; public string? ScenarioId { get; set; } }
    public sealed class Review
    {
        public string Schema { get; set; } = ReviewPublisher.Schema;
        public string ReportSha256 { get; set; } = "";
        public string Status { get; set; } = "pending";
        public Author Author { get; set; } = new();
        public string GeneratedUtc { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<Finding> Findings { get; set; } = new();
        public List<string> Limitations { get; set; } = new();
        public List<string> Recommendations { get; set; } = new();
    }
    public sealed record InputFile(string Name, string SHA256);
    public sealed record Revision(int Number, string RecordedUtc, string ReviewSha256, Review Review);
    public sealed record Published(string ReportHtml, string ReportUrl, string ReviewFile, string ReviewStatus, string ReportSha256, int Revisions);
    private sealed record Inputs(JsonElement Source, JsonElement? Metadata, JsonElement? Summary, string Name, List<InputFile> Files, string Digest);

    public static Published Publish(string directory, bool finalize = false)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory)) throw new ArgumentException("Report directory does not exist: " + directory);
        RejectLinks(directory);
        var inputs = ReadInputs(directory);
        string reviewPath = Path.Combine(directory, "ai-review.json"), logPath = Path.Combine(directory, "ai-review-log.jsonl");
        Review review;
        if (!File.Exists(reviewPath))
        {
            if (finalize) throw new ArgumentException("AI review is missing. Run report --input first, then write ai-review.json.");
            review = new() { ReportSha256 = inputs.Digest };
        }
        else
        {
            RejectLinks(reviewPath);
            if (new FileInfo(reviewPath).Length > 100_000) throw new ArgumentException("ai-review.json exceeds 100 KB.");
            try { review = JsonSerializer.Deserialize<Review>(File.ReadAllText(reviewPath)) ?? throw new JsonException(); }
            catch (JsonException ex) { throw new ArgumentException("Invalid ai-review.json: " + ex.Message); }
        }
        Validate(review, inputs);
        if (finalize && review.Status != "complete") throw new ArgumentException("AI review is pending. Write the summary and set Status=complete before finalizing.");
        var history = ReadHistory(logPath, inputs);
        string reviewHash = Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(review)));
        if (review.Status == "complete" && (history.Count == 0 || history[^1].ReviewSha256 != reviewHash))
            history.Add(new(history.Count + 1, DateTime.UtcNow.ToString("O"), reviewHash, review));
        var context = new
        {
            Schema = "BenchmarkLab.ReviewContext.v1", ReportSha256 = inputs.Digest, SourceFiles = inputs.Files,
            ReviewFile = "ai-review.json", FinalizeCommand = "SonicRoute.BenchmarkLab report --input \"" + directory + "\" --finalize --json",
            Instructions = "AI: read source evidence, write ai-review.json directly, preserve ReportSha256, fill actual model/time/summary/findings/limits/next steps, set Status=complete, then finalize. Never replace rule verdicts or claim A/A/synthetic/smoke as product improvement."
        };
        var document = new
        {
            ViewerSchemaVersion = 1, ReaderMode = true, SourceName = inputs.Name, Source = inputs.Source,
            Metadata = inputs.Metadata, Summary = inputs.Summary, Review = review.Status == "complete" ? review : null,
            ReviewState = review.Status, ReportSha256 = inputs.Digest, ReviewHistory = history
        };
        string html = HtmlReporter.CreateDocument(document);
        // Check the same fixed inputs again before replacing any published artifact.
        foreach (var file in inputs.Files)
            if (Hash(File.ReadAllBytes(Path.Combine(directory, file.Name))) != file.SHA256)
                throw new ArgumentException("Measurements changed during report generation; no report was published.");
        // Publish one complete file at a time. HTML is last, so an interrupted write
        // leaves the previous reader report intact; repeating finalize is idempotent.
        WriteAtomic(Path.Combine(directory, "review-context.json"), JsonSerializer.Serialize(context, Json));
        if (!File.Exists(reviewPath)) WriteAtomic(reviewPath, JsonSerializer.Serialize(review, Json));
        if (history.Count > 0) WriteAtomic(logPath, string.Join('\n', history.Select(r => JsonSerializer.Serialize(r))) + "\n");
        WriteAtomic(Path.Combine(directory, "report-view.json"), JsonSerializer.Serialize(document, Json));
        string htmlPath = Path.Combine(directory, "report.html");
        WriteAtomic(htmlPath, html);
        return new(htmlPath, new Uri(htmlPath).AbsoluteUri, reviewPath, review.Status, inputs.Digest, history.Count);
    }

    private static Inputs ReadInputs(string directory)
    {
        var files = new List<InputFile>();
        JsonElement Read(string name)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new ArgumentException("Required report evidence is missing: " + name);
            RejectLinks(path);
            byte[] bytes = File.ReadAllBytes(path);
            files.Add(new(name, Hash(bytes)));
            int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            try { using var parsed = JsonDocument.Parse(bytes.AsMemory(offset)); return parsed.RootElement.Clone(); }
            catch (JsonException ex) { throw new ArgumentException("Invalid evidence " + name + ": " + ex.Message); }
        }
        JsonElement source; JsonElement? metadata = null, summary = null; string sourceName;
        if (File.Exists(Path.Combine(directory, "report.json")))
        {
            source = Read(sourceName = "report.json");
            if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("Scenarios", out var scenarios) || scenarios.ValueKind != JsonValueKind.Array
                || !source.TryGetProperty("Runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("report.json must contain original comparison Scenarios and Runs.");
        }
        else
        {
            source = Read(sourceName = "run_evidence.json"); metadata = Read("run_metadata.json"); summary = Read("run_summary.json");
            if (source.ValueKind != JsonValueKind.Array || metadata.Value.ValueKind != JsonValueKind.Object || summary.Value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Single-target report requires evidence array, metadata object and summary object.");
        }
        string digest = Hash(Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => f.Name + ":" + f.SHA256))));
        return new(source, metadata, summary, sourceName, files, digest);
    }
    private static void Validate(Review review, Inputs inputs)
    {
        if (review.Schema != Schema || !string.Equals(review.ReportSha256, inputs.Digest, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AI review schema/report fingerprint mismatch. Preserve original evidence; do not reuse a review from another report.");
        review.ReportSha256 = inputs.Digest;
        if (review.Status is not ("pending" or "complete")) throw new ArgumentException("Review Status must be pending or complete.");
        if (review.Status == "pending") return;
        if (review.Author?.Kind != "ai") throw new ArgumentException("Review must name its AI author.");
        Text(review.Author.Model, "Author.Model", 120); Text(review.Summary, "Summary", 4000);
        if (review.GeneratedUtc?.Length > 100 || !DateTimeOffset.TryParse(review.GeneratedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var generated) || generated.Offset != TimeSpan.Zero)
            throw new ArgumentException("GeneratedUtc must be an actual UTC timestamp.");
        if (review.Findings == null || review.Findings.Count > 12) throw new ArgumentException("Findings must contain at most 12 items.");
        var ids = inputs.Source.ValueKind == JsonValueKind.Array
            ? inputs.Source.EnumerateArray().Select(r => r.TryGetProperty("ScenarioId", out var id) ? id.GetString() : null).ToHashSet()
            : inputs.Source.GetProperty("Scenarios").EnumerateArray().Select(s => s.TryGetProperty("ScenarioId", out var id) ? id.GetString() : null).ToHashSet();
        foreach (var finding in review.Findings)
        {
            if (finding == null) throw new ArgumentException("Finding cannot be null.");
            Text(finding.Title, "Finding.Title", 180); Text(finding.Detail, "Finding.Detail", 2000);
            if (finding.ScenarioId != null && !ids.Contains(finding.ScenarioId)) throw new ArgumentException("Review references unknown scenario: " + finding.ScenarioId);
        }
        foreach (var (values, name) in new[] { (review.Limitations, "Limitations"), (review.Recommendations, "Recommendations") })
        {
            if (values == null || values.Count > 12) throw new ArgumentException(name + " must contain at most 12 items.");
            foreach (string value in values) Text(value, name, 1500);
        }
    }
    private static List<Revision> ReadHistory(string path, Inputs inputs)
    {
        var result = new List<Revision>();
        if (!File.Exists(path)) return result;
        RejectLinks(path);
        if (new FileInfo(path).Length > 16_000_000) throw new ArgumentException("Review history is too large; preserve and archive it explicitly.");
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Revision revision;
            try { revision = JsonSerializer.Deserialize<Revision>(line) ?? throw new JsonException(); }
            catch (JsonException ex) { throw new ArgumentException("Invalid review history; existing report retained: " + ex.Message); }
            if (revision.Review == null) throw new ArgumentException("Missing review in history.");
            Validate(revision.Review, inputs);
            if (revision.Number != result.Count + 1 || revision.Review.Status != "complete" || revision.ReviewSha256 != Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(revision.Review))))
                throw new ArgumentException("Review history is inconsistent; preserve it for inspection.");
            result.Add(revision);
        }
        return result;
    }
    private static void Text(string? value, string field, int max)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new ArgumentException(field + " is missing or exceeds " + max + " characters."); }
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Linked report paths are not supported: " + current);
    }
    private static void WriteAtomic(string path, string content)
    {
        RejectLinks(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
