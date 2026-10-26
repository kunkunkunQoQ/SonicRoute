using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SonicRoute;
using SonicRoute.Core;

internal static class Program
{
    private static int _passed;
    private static string _directory = "";
    private static readonly JsonSerializerOptions OldExport = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); ++_passed; Console.WriteLine("PASS " + name); }
    private static string NewDirectory(string name) { string path = Path.Combine(_directory, name); Directory.CreateDirectory(path); return path; }
    private static ExecutionHistoryEntry Entry(string id = "run") => new()
    {
        RunId = id, ParentRunId = "parent", RuleId = "rule", RuleName = "中文 <>& \"\n 😀",
        StartedUtc = new DateTime(2026, 10, 26, 2, 3, 4, DateTimeKind.Utc), Source = "hotkey", Status = "success", Succeeded = 1, TotalSteps = 1,
        Steps = new() { new() { Number = 1, Action = 7, TargetApp = "应用.exe", Status = "success", Reason = "\r\n", ElapsedMs = 12 } }
    };
    private static async Task History()
    {
        string dir = NewDirectory("history");
        using var store = new ExecutionHistoryStore(dir, 1);
        var entry = Entry();
        await store.AppendAsync(entry);
        string file = Path.Combine(dir, "recent.jsonl");
        Check(File.ReadAllText(file) == JsonSerializer.Serialize(entry) + "\n", "history compact bytes match old reflection contract");
        Check(File.ReadAllLines(file).Length == 1, "escaped newlines stay in a single JSONL record");
        var read = await store.ReadRecentAsync();
        Check(read.Count == 1 && JsonSerializer.Serialize(read[0]) == JsonSerializer.Serialize(entry), "history Unicode/date/steps round trip");
        // Old records, unknown fields and truncated tails use the actual on-disk reader.
        File.AppendAllText(file, "{\"RunId\":\"old\",\"NewField\":42}\n{\"RunId\":\"invalid\",\"Steps\":[null]}\n{broken", new UTF8Encoding(false));
        read = await store.ReadRecentAsync();
        Check(read.Count == 2 && read[0].RunId == "old" && read[0].Steps.Count == 0, "old missing fields load; unknown fields ignored; corrupt/null-step records skipped");
        await store.AppendAsync(Entry("after-crash"));
        read = await store.ReadRecentAsync();
        Check(read.Count == 3 && read[0].RunId == "after-crash", "append isolates crashed partial tail");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await store.ReadRecentAsync(cancel.Token); throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { Check(true, "history cancellation retained"); }
        Check(await store.ClearAsync() && !File.Exists(file), "history clear uses existing files/lifetime");
        var large = Entry(); large.RuleName = new string('x', 90_000);
        for (int i = 0; i < 14; i++) { large.RunId = i.ToString(); await store.AppendAsync(large); }
        read = await store.ReadRecentAsync();
        Check(File.Exists(Path.Combine(dir, "previous.jsonl")) && read.Count > 5 && read[0].RunId == "13", "history rotation retains newest records");
        Check(Directory.GetFiles(dir, "*.jsonl").Sum(path => new FileInfo(path).Length) <= 1024 * 1024, "history capacity preserved");
        large.RuleName = new string('x', 140_000);
        await store.AppendAsync(large);
        Check(store.LastError == "record_too_large", "oversized history record refused");
        store.Dispose();
        await store.AppendAsync(Entry("disposed"));
        Check((await store.ReadRecentAsync()).Count == 0, "disposed history neither appends nor reads");
    }
    private static void Languages()
    {
        var special = LanguageFiles.Read("{\"Dup\":\"first\",\"Dup\":\"last\",\"Null\":null,\"Case\":\"UP\",\"case\":\"down\"}")!;
        Check(special["Dup"] == "last" && special["Null"] == null && special["Case"] != special["case"], "language duplicate/null/case contract retained");
        var dictionary = new Dictionary<string, string> { ["中文"] = "😀 <>&\n\"", ["Null"] = null! };
        Check(LanguageFiles.Serialize(dictionary) == JsonSerializer.Serialize(dictionary, OldExport), "language export indentation and relaxed escaping byte compatible");
        int count = 0;
        foreach (string code in new[] { "zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU" })
        {
            using var resource = typeof(L10n).Assembly.GetManifestResourceStream("SonicRoute.Resources.Lang." + code + ".json")!;
            using var reader = new StreamReader(resource);
            string json = reader.ReadToEnd();
            var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            Check(JsonSerializer.Serialize(LanguageFiles.Read(stream)) == JsonSerializer.Serialize(expected), code + " stream contract");
            L10n.Instance.SetLanguage(code);
            Check(expected.Where(pair => !pair.Key.StartsWith("Lang.", StringComparison.Ordinal)).All(pair => L10n.T(pair.Key) == pair.Value), code + " actual localization lookups");
            ++count;
        }
        string exports = NewDirectory("exports");
        Check(L10n.ExportBuiltinLanguages(exports) && Directory.GetFiles(exports).Length == count, "all builtin languages export");
        string import = Path.Combine(NewDirectory("imports"), "test.json");
        File.WriteAllText(import, "{\"Lang.Code\":\"en-US\",\"Lang.NativeName\":\"Override\",\"Apps.Output\":\"override\"}");
        var result = L10n.ImportLanguageFile(import); L10n.Instance.ApplyImportedLanguage(result.Code);
        Check(result.Ok && L10n.T("Apps.Output") == "override" && L10n.T("Undefined.Key") == "Undefined.Key", "external override and missing-key fallback");
        File.WriteAllText(import, "{\"Lang.Code\":\"en-US\",\"Lang.Custom\":\"附加\",\"Hello\":\"world\",\"Null\":null}");
        Check(!L10n.ImportLanguageFile(import).Ok, "null language value rejected on import");
        File.WriteAllText(import, "{\"Lang.Code\":\"en-US\",\"Lang.Custom\":\"附加\",\"Hello\":\"world\"}");
        result = L10n.ImportLanguageFile(import); L10n.Instance.ApplyImportedLanguage(result.Code);
        Check(result.Ok && result.Code == "en-US#" && L10n.T("Hello") == "world" && !L10n.ListCustomLanguages().Any(), "additional builtin-name collision keeps separate identity");
        File.WriteAllText(import, "{\"Lang.Code\":\"custom\",\"Lang.NativeName\":\"Before\",\"Hello\":\"world\"}");
        result = L10n.ImportLanguageFile(import); L10n.Instance.ApplyImportedLanguage(result.Code);
        Check(result.Ok && L10n.RenameCustomLanguage("custom", "之后"), "custom language rename");
        Check(LanguageFiles.Read(File.ReadAllText(Path.Combine(L10n.ExternalLangDir, "custom.json")))!["Lang.NativeName"] == "之后", "renamed file uses same readable format");
        Check(L10n.DeleteCustomLanguage("custom") && L10n.CurrentLanguage == "zh-CN", "delete current custom language falls back");
        File.WriteAllText(import, "{bad");
        Check(!L10n.ImportLanguageFile(import).Ok, "malformed language import refused");
        var restored = L10n.RestoreBuiltinLanguagesAsync().GetAwaiter().GetResult();
        Check(restored.Ok && File.Exists(Path.Combine(L10n.ExternalLangDir, "en-US.json")), "restore keeps valid additional language");
        L10n.Instance.SetLanguage("zh-CN"); string chineseName = L10n.T("App.Name");
        File.WriteAllText(Path.Combine(L10n.ExternalLangDir, "en-US.json"), "{\"Lang.NativeName\":\"Metadata\",\"App.Name\":null}");
        L10n.Instance.ApplyImportedLanguage("en-US");
        Check(L10n.T("App.Name") == chineseName, "null value in manually edited language falls back to Chinese");
        Check(L10n.T("Lang.NativeName") == "Lang.NativeName", "language metadata is excluded from translation keys");
        File.WriteAllText(Path.Combine(L10n.ExternalLangDir, "en-US.json"), "{broken");
        L10n.Instance.ApplyImportedLanguage("en-US");
        Check(L10n.T("App.Name") == LanguageFiles.Read(File.ReadAllText(Path.Combine(exports, "en-US.json")))!["App.Name"], "corrupt external language falls back to same builtin language");
    }

#if NET10_0_OR_GREATER
    private static int Performance(string kind, string mode, string fixture)
    {
        string json = File.ReadAllText(fixture);
        bool generated = mode == "generated";
        // Fixtures and other serializers must not prewarm this process before cold measurement.
        if (kind == "language")
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
            var value = generated ? LanguageFiles.Read(json)! : JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            clock.Stop(); bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            double coldMs = clock.Elapsed.TotalMilliseconds;
            string Write() => generated ? LanguageFiles.Serialize(value) : JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            for (int i = 0; i < 100; i++) _ = Write();
            long allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
            for (int i = 0; i < 2000; i++) _ = Write();
            clock.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine(JsonSerializer.Serialize(new { kind, mode, coldMs, coldBytes = bytes, writeMs = clock.Elapsed.TotalMilliseconds, writeBytes = allocated, iterations = 2000, runtime = Environment.Version.ToString() }));
        }
        else
        {
            var value = JsonSerializer.Deserialize<ExecutionHistoryEntry>(json)!;
            var contextType = typeof(ExecutionHistoryStore).Assembly.GetType("SonicRoute.Core.ExecutionHistoryJsonContext")!;
            var context = (System.Text.Json.Serialization.JsonSerializerContext)contextType.GetProperty("Default")!.GetValue(null)!;
            var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<ExecutionHistoryEntry>)context.GetTypeInfo(typeof(ExecutionHistoryEntry))!;
            byte[] Write() => generated ? JsonSerializer.SerializeToUtf8Bytes(value, info) : JsonSerializer.SerializeToUtf8Bytes(value);
            ExecutionHistoryEntry? Read() => JsonSerializer.Deserialize<ExecutionHistoryEntry>(json);
            for (int i = 0; i < 1000; i++) { _ = Write(); _ = Read(); }
            var clock = new Stopwatch();
            long allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Start();
            for (int i = 0; i < 20_000; i++) _ = Write();
            clock.Stop(); long writeBytes = GC.GetAllocatedBytesForCurrentThread() - allocated; double writeMs = clock.Elapsed.TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
            for (int i = 0; i < 20_000; i++) _ = Read();
            clock.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new { kind, mode, writeMs, writeBytes, readMs = clock.Elapsed.TotalMilliseconds, readBytes = GC.GetAllocatedBytesForCurrentThread() - allocated, iterations = 20_000, runtime = Environment.Version.ToString(), scope = "warm serializer only; actual Core generated writer, original reader retained; no file IO" }));
        }
        return 0;
    }
#endif
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--fixture") { File.WriteAllText(args[1], JsonSerializer.Serialize(Entry())); return 0; }
#if NET10_0_OR_GREATER
            if (args.Length > 0 && args[0] == "--perf") return Performance(args[1], args[2], args[3]);
#endif
            _directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-serialization-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", _directory);
            Directory.CreateDirectory(_directory);
            History().GetAwaiter().GetResult(); Languages();
            Console.WriteLine($"RESULT passed={_passed} failed=0 runtime={Environment.Version} isolated={_directory} AUDIO_WRITES=NONE");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
