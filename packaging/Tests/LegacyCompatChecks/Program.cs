using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
using SonicRoute.Core.Models;

internal static class Program
{
    const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    static int Count;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Count++; Console.WriteLine("PASS " + message); }
    static Type Bootstrap => Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SonicRoute.exe")).GetType("SonicRoute.Updates.UpdateBootstrap", true)!;
    static Func<string> PathGetter() => (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), Bootstrap.GetProperty("ExecutablePath", StaticPrivate)!.GetGetMethod(true)!);
    static void Checks()
    {
        var path = PathGetter(); string expected;
        using (var process = Process.GetCurrentProcess()) expected = process.MainModule.FileName;
        Check(path() == expected, "actual path equals original native query");
        Check(path() == AppInfo.ExecutablePath, "actual path equals existing identity cache");
        Check(expected.Contains("中文 空格"), "running from unicode and spaced directory");
        string cwd = Environment.CurrentDirectory;
        try { Environment.CurrentDirectory = Path.GetTempPath(); Check(path() == expected, "independent of current working directory"); }
        finally { Environment.CurrentDirectory = cwd; }
        System.Threading.Tasks.Parallel.For(0, 16, _ => { for (int i = 0; i < 50; i++) if (path() != expected) throw new Exception("Path changed"); });
        Check(true, "concurrent repeated path reads remain stable");
        var field = typeof(AppInfo).GetField("_executablePath", StaticPrivate)!; var old = field.GetValue(null);
        try { field.SetValue(null, null); Check(path() == expected, "failed initial cache retains original lookup fallback"); }
        finally { field.SetValue(null, old); }
        string data = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY") ?? throw new Exception("Missing isolated data");
        string edition = "LegacyProbe_" + Guid.NewGuid().ToString("N"), mutex = "Local\\" + edition;
        var before = (Func<string[], string, string, bool, string, Action<string>, bool>)Delegate.CreateDelegate(
            typeof(Func<string[], string, string, bool, string, Action<string>, bool>), Bootstrap.GetMethod("BeforeStartup", StaticPrivate)!);
        var alerts = new List<string>();
        Check(before(Array.Empty<string>(), data, edition, false, mutex, alerts.Add) && alerts.Count == 0, "actual ordinary bootstrap accepts isolated current executable");
        Check(before(Array.Empty<string>(), data, edition, true, mutex, alerts.Add), "packaged normal startup bypass remains intact");
        Check(!before(new[] { "--update-worker", "missing-plan" }, data, edition, true, mutex, alerts.Add) && alerts.Last() == "Upd.Store", "packaged worker remains blocked");
        Check(!before(new[] { "--update-bogus" }, data, edition, false, mutex, alerts.Add), "unknown internal update argument remains blocked");
        Check(!before(new[] { "--update-start", "invalid-guid" }, data, edition, false, mutex, alerts.Add), "invalid startup handshake remains blocked");
        Console.WriteLine("PASS total=" + Count);
    }
    static void PathPerf()
    {
        var path = PathGetter(); string expected = path(); // Include cold identity setup separately from warm repeated reads.
        for (int i = 0; i < 10; i++) _ = path();
        long bytes = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) if (path() != expected) throw new Exception("Path changed");
        clock.Stop(); long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "path", iterations = 1000, wallMs = clock.Elapsed.TotalMilliseconds, allocatedBytes = allocated }));
    }
    static void JsonPerf(string directory, bool readOnly)
    {
        string configJson = File.ReadAllText(Path.Combine(directory, "config.json"));
        string ruleJson = File.ReadAllText(Path.Combine(directory, "rule.json"));
        var configRead = typeof(ConfigService).GetProperty("ReadOptions", StaticPrivate)!;
        var configSave = typeof(ConfigService).GetField("SaveOptions", StaticPrivate)!;
        var ruleRead = typeof(AutoRuleStore).GetProperty("ReadOptions", StaticPrivate)!;
        var ruleSave = typeof(AutoRuleStore).GetField("SaveOptions", StaticPrivate)!;
        long bytes = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        var cr = (JsonSerializerOptions?)configRead.GetValue(null); var cs = (JsonSerializerOptions)configSave.GetValue(null)!;
        var rr = (JsonSerializerOptions?)ruleRead.GetValue(null); var rs = (JsonSerializerOptions)ruleSave.GetValue(null)!;
        var config = JsonSerializer.Deserialize<AppConfig>(configJson, cr)!; if (!readOnly) _ = JsonSerializer.Serialize(config, cs);
        AutoRule rule = null!;
        for (int i = 0; i < 100; i++) { rule = JsonSerializer.Deserialize<AutoRule>(ruleJson, rr)!; if (!readOnly) _ = JsonSerializer.Serialize(rule, rs); }
        double coldMs = clock.Elapsed.TotalMilliseconds; long coldBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        for (int i = 0; i < 100; i++) { _ = JsonSerializer.Deserialize<AutoRule>(ruleJson, rr); if (!readOnly) _ = JsonSerializer.Serialize(rule, rs); }
        bytes = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
        for (int i = 0; i < 1000; i++) { _ = JsonSerializer.Deserialize<AutoRule>(ruleJson, rr); if (!readOnly) _ = JsonSerializer.Serialize(rule, rs); }
        clock.Stop(); long warmBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Console.WriteLine(JsonSerializer.Serialize(new { mode = readOnly ? "json-read" : "json", sharedConfig = ReferenceEquals(cr, cs), sharedRule = ReferenceEquals(rr, rs), coldMs, coldBytes, warmMs = clock.Elapsed.TotalMilliseconds, warmBytes }));
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] == "checks") Checks();
            else if (args[0] == "path") PathPerf();
            else JsonPerf(args[1], args[0] == "json-read");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
