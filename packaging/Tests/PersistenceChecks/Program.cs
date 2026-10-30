using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static readonly JsonSerializerOptions ReflectionJson = new() { WriteIndented = true };
    private static int _checks;
    private static JsonSerializerOptions ProductJson => (JsonSerializerOptions)typeof(ConfigService)
        .GetField("SaveOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label); _checks++;
    }

    private static void Same<T>(T value, string label)
    {
        string old = JsonSerializer.Serialize(value, ReflectionJson);
        string actual = JsonSerializer.Serialize(value, ProductJson);
        Check(old == actual, label + " write bytes");
        string oldRead = JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(old), ReflectionJson);
        string newRead = JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(old, ProductJson), ReflectionJson);
        Check(oldRead == newRead, label + " read contract");
    }

    private static AppConfig Config()
    {
        var config = new AppConfig();
        // Exercise every persisted setting, including future properties added to the model.
        foreach (var property in typeof(AppConfig).GetProperties())
        {
            Type type = property.PropertyType;
            if (type == typeof(string)) property.SetValue(config, "中文 空格 & < > \\" + property.Name);
            else if (type == typeof(bool)) property.SetValue(config, !(bool)property.GetValue(config)!);
            else if (type == typeof(int) || type == typeof(int?)) property.SetValue(config, -17);
            else if (type == typeof(double)) property.SetValue(config, 0.75d);
            else if (type == typeof(DateTime)) property.SetValue(config, new DateTime(2026, 10, 23, 1, 2, 3, DateTimeKind.Utc));
            else if (type == typeof(List<string>)) property.SetValue(config, new List<string> { "中文.exe", "value & < >" });
            else if (type == typeof(Dictionary<string, string>)) property.SetValue(config, new Dictionary<string, string> { ["中文"] = "空 格 & <>" });
            else throw new InvalidOperationException("Add fixture for new config property: " + property.Name);
        }
        config.AppIcons.Clear(); // Real API only persists validated managed icon filenames.
        return config;
    }

    private static AutoRule Rule() => new()
    {
        Id = "persistence-check", Name = "规则 中文 & 空格", Enabled = true, Trigger = AutoRuleTrigger.Schedule,
        Hotkey = "Ctrl+Alt+F24", TriggerApp = "application.exe", ScheduleMode = 2,
        ScheduleTime = "12:34", ScheduleWeekdays = new() { 1, 3, 6 }, LastRunKey = "2026-10-23 12:34",
        TargetRuleId = "other-rule", ProgramPath = "C:\\旧版 程序.exe",
        Actions = Enum.GetValues(typeof(AutoRuleAction)).Cast<AutoRuleAction>().Select(action => new AutoRuleStep
        {
            Action = action, TargetRuleId = "another-rule", TargetApp = "中文 应用", TargetDeviceId = "{device-id}",
            Volume = 42, VolumeDelta = -7, Muted = false, DelayMs = 60000, StopOnFailure = true,
            OsdTitle = "中文 & < >", OsdText = "line1\nline2", ProgramPaths = new() { "C:\\旧版 程序.exe" },
            LaunchItems = new() { new() { Path = "C:\\空 格\\程序.exe", OpenWith = "C:\\运行.exe", OpenWithName = "运行" } },
            LaunchMode = 1
        }).ToList()
    };

    private static string Outcome<T>(string json, JsonSerializerOptions? options)
    {
        try { return JsonSerializer.Serialize(JsonSerializer.Deserialize<T>(json, options), ReflectionJson); }
        catch (JsonException) { return "JsonException"; }
    }

    private sealed class CallbackWatcher : FileSystemWatcher
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                object gate = typeof(AutoRuleStore).GetField("_lock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                // A pending callback must be able to acquire the store lock while disposal waits.
                var callback = System.Threading.Tasks.Task.Run(() => { lock (gate) { } });
                Check(callback.Wait(5000), "pending watcher callback completes during disposal");
            }
            base.Dispose(disposing);
        }
    }

    private static void ShutdownCallbacks()
    {
        foreach (string field in new[] { "_watcher", "_directoryWatcher" })
            typeof(AutoRuleStore).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new CallbackWatcher());
        AutoRuleStore.Shutdown();
    }

    private static void Checks()
    {
        Check(new AppConfig().ExperimentalMic, "microphone option defaults on for new config");
        Check(new AppConfig().PanelChangeSystemDefault, "quick panel defaults to changing system playback");
        Check(JsonSerializer.Deserialize<AppConfig>("{}", ProductJson)!.PanelChangeSystemDefault, "missing quick panel system-default setting defaults on");
        Check(!JsonSerializer.Deserialize<AppConfig>("{\"PanelChangeSystemDefault\":false}", ProductJson)!.PanelChangeSystemDefault, "saved quick panel opt-out remains off");
        Check(JsonSerializer.Deserialize<AppConfig>("{\"PanelChangeSystemDefault\":true}", ProductJson)!.PanelChangeSystemDefault, "saved quick panel opt-in remains on");
        Check(!new AppConfig().TrayWheelEverywhere, "tray wheel area defaults to own icon");
        Check(!JsonSerializer.Deserialize<AppConfig>("{}", ProductJson)!.TrayWheelEverywhere, "missing tray wheel area defaults to own icon");
        Check(JsonSerializer.Deserialize<AppConfig>("{\"TrayWheelEverywhere\":true}", ProductJson)!.TrayWheelEverywhere, "saved whole-tray choice remains enabled");
        Check(!JsonSerializer.Deserialize<AppConfig>("{\"TrayWheelEverywhere\":false}", ProductJson)!.TrayWheelEverywhere, "saved own-icon choice remains disabled");
        Check(JsonSerializer.Deserialize<AppConfig>("{}", ProductJson)!.ExperimentalMic, "missing microphone field defaults on");
        Check(!JsonSerializer.Deserialize<AppConfig>("{\"ExperimentalMic\":false}", ProductJson)!.ExperimentalMic, "explicit saved microphone opt-out remains off");
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")), "isolated data root required");
#if NET10_0_OR_GREATER
        Check(ProductJson.TypeInfoResolver!.GetType().Name == "PersistenceJsonContext", "NET10 uses generated contracts");
        Check(ProductJson.GetTypeInfo(typeof(AppConfig)) is System.Text.Json.Serialization.Metadata.JsonTypeInfo<AppConfig> { SerializeHandler: not null }, "config write fast path exists");
        Check(ProductJson.GetTypeInfo(typeof(AutoRule)) is System.Text.Json.Serialization.Metadata.JsonTypeInfo<AutoRule> { SerializeHandler: not null }, "rule write fast path exists");
        Check(ReferenceEquals(ProductJson, typeof(AutoRuleStore).GetField("SaveOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)), "config and rules share options/type metadata");
#else
        Check(typeof(ConfigService).Assembly.GetType("SonicRoute.Core.PersistenceJsonContext") == null, "older runtime excludes generated context");
#endif
        Same(new AppConfig(), "default config"); Same(Config(), "all config properties");
        foreach (string oldStyle in new[] { "classic", "modern" })
        {
            string oldJson = JsonSerializer.Serialize(new { QuickPanelStyle = oldStyle, QuickPanelHeight = 610,
                QuickPanelPosMode = "custom", QuickPanelCustomX = 120, VolumeStep = 7,
                HiddenPanelApps = new[] { "HiddenApp" }, DeviceNames = new Dictionary<string, string> { ["device"] = "Custom" } });
            var retained = JsonSerializer.Deserialize<AppConfig>(oldJson, ProductJson)!;
            Check(retained.QuickPanelHeight == 610 && retained.QuickPanelPosMode == "custom"
                && retained.QuickPanelCustomX == 120 && retained.VolumeStep == 7
                && retained.HiddenPanelApps.Single() == "HiddenApp" && retained.DeviceNames["device"] == "Custom",
                "obsolete panel preference preserves other settings: " + oldStyle);
            Check(ConfigService.TrySave(retained) && !File.ReadAllText(ConfigService.ConfigPath).Contains("\"QuickPanelStyle\""),
                "normal save omits obsolete panel preference: " + oldStyle);
        }
        Same(new AppConfig { AppIcons = new(StringComparer.OrdinalIgnoreCase) { ["App.EXE"] = new string('a', 64) + ".png" } }, "custom icon mapping");
        Same(new AutoRule { Id = "default-id" }, "default rule"); Same(Rule(), "all 37 actions/nested launch items");
        Same(new List<AutoRule> { Rule(), new() { Id = "legacy", Action = AutoRuleAction.LaunchProgram, ProgramPath = "C:\\old.exe" } }, "legacy rule array");
        Same(new AppConfig { DeviceNames = null!, HiddenOutputDevices = null!, Language = null! }, "explicit null config");
        Same(new AutoRule { Id = "null-id", Actions = null!, ScheduleWeekdays = null! }, "explicit null rule");
        Same(new AutoRule { Id = "nested-null", Actions = new() { new() { LaunchItems = null!, ProgramPaths = null! } } }, "null nested collections");
        foreach (string json in new[] { "{}", "null", "{\"UnknownFutureField\":{\"x\":[1,2]},\"VolumeStep\":8}",
            "{\"VolumeStep\":1,\"VolumeStep\":7}", "{\"volumestep\":19}", "{\"LastUpdateCheckUtc\":\"invalid\"}",
            "{\"LastUpdateCheckUtc\":\"2026-10-23T01:02:03+08:00\"}", "{\"HiddenPanelApps\":null}",
            "{\"VolumeStep\":\"7\"}", "{\"VolumeStep\":2147483648}", "{\"AutoCheckUpdates\":1}", "{", "[]" })
            Check(Outcome<AppConfig>(json, null) == Outcome<AppConfig>(json, ProductJson), "config input " + json);
        foreach (string json in new[] { "{}", "null", "{\"Id\":\"fixed\",\"Action\":999,\"Trigger\":999}",
            "{\"Id\":\"fixed\",\"Actions\":[{\"Action\":8,\"ProgramPaths\":[\"C:\\\\旧版.exe\"]}]}",
            "{\"Id\":\"fixed\",\"Actions\":null}", "{\"Id\":\"fixed\",\"Trigger\":\"AppStart\"}", "[]", "{" })
        {
            // Missing Id generates a new GUID in both serializers; use a stable Id for comparison.
            string input = json == "{}" ? "{\"Id\":\"fixed\"}" : json;
            Check(Outcome<AutoRule>(input, null) == Outcome<AutoRule>(input, ProductJson), "rule input " + json);
        }
        var config = Config();
        Check(ConfigService.TrySave(config), "real atomic config save");
        Check(File.ReadAllText(ConfigService.ConfigPath) == JsonSerializer.Serialize(config, ReflectionJson), "saved config byte compatible");
        Check(ConfigService.CaptureExportSnapshot() == JsonSerializer.Serialize(config, ReflectionJson), "export snapshot byte compatible");
        string backup = Path.Combine(AppDataRoot.DirectoryPath, "备份 空格.json");
        Check(ConfigService.ExportTo(backup), "real config export");
        Check(ConfigService.PrepareImport(backup) != null, "real config import validation");
        var rule = Rule(); AutoRuleStore.Save(rule);
        Check(File.ReadAllText(Directory.GetFiles(AutoRuleStore.RulesDir, "*.json").Single()) == JsonSerializer.Serialize(rule, ReflectionJson), "saved rule byte compatible");
        AutoRuleStore.Invalidate();
        var snapshot = AutoRuleStore.ReadSnapshot();
        var loadedRule = snapshot.Rules!.Single();
        Check(loadedRule.Id == rule.Id && loadedRule.Actions.Select(step => step.Action)
            .SequenceEqual(rule.Actions.Select(step => step.Action)), "actual store reload retains all actions in order");
        var claimed = AutoRuleStore.ClaimScheduledRun(rule, "2026-10-24 12:34");
        Check(claimed?.LastRunKey == "2026-10-24 12:34", "scheduled claim persists");
        Check(AutoRuleStore.ClaimScheduledRun(rule, "2026-10-24 12:34") == null, "scheduled duplicate rejected");
        AutoRuleStore.Shutdown();
        ShutdownCallbacks();
    }

    private static void Legacy()
    {
        Check(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")), "isolated migration root required");
        Directory.CreateDirectory(AppDataRoot.DirectoryPath);
        File.WriteAllText(ConfigService.ConfigPath, "{\"VolumeStep\":9,\"AutoRules\":[{\"Id\":\"legacy\",\"Name\":\"旧规则\",\"Action\":8,\"ProgramPath\":\"C:\\\\旧版.exe\"}]}");
        Check(ConfigService.Load().VolumeStep == 9, "legacy config loads");
        var rule = AutoRuleStore.ReadSnapshot().Rules!.Single();
        Check(rule.CloneSteps().Single().EffectiveLaunchItems().Single().Path == "C:\\旧版.exe", "legacy embedded rule/launch migration");
        Check(File.Exists(Path.Combine(AutoRuleStore.RulesDir, "旧规则.json")), "legacy migration writes named rule");
        AutoRuleStore.Shutdown();
    }

#if NET10_0_OR_GREATER
    private static void Perf(string mode, string fixtures)
    {
        // Separate fixture generation from the process under measurement: otherwise
        // creating input JSON would prewarm only the reflection serializer.
        string configJson = File.ReadAllText(Path.Combine(fixtures, "config.json"));
        string ruleJson = File.ReadAllText(Path.Combine(fixtures, "rule.json"));
        JsonSerializerOptions options;
        var clock = Stopwatch.StartNew(); long allocated = GC.GetAllocatedBytesForCurrentThread();
        options = mode == "generated" ? ProductJson : new JsonSerializerOptions { WriteIndented = true };
        _ = JsonSerializer.Deserialize<AppConfig>(configJson, options);
        double configColdMs = clock.Elapsed.TotalMilliseconds;
        AutoRule rule = null!;
        for (int i = 0; i < 100; i++) rule = JsonSerializer.Deserialize<AutoRule>(ruleJson, options)!;
        double coldMs = clock.Elapsed.TotalMilliseconds; long coldBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        // Warm both operations before alternating independent read/write measurements.
        for (int i = 0; i < 100; i++) { _ = JsonSerializer.Deserialize<AutoRule>(ruleJson, options); _ = JsonSerializer.Serialize(rule, options); }
        allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
        for (int i = 0; i < 2000; i++) _ = JsonSerializer.Serialize(rule, options);
        double writeMs = clock.Elapsed.TotalMilliseconds; long writeBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        allocated = GC.GetAllocatedBytesForCurrentThread(); clock.Restart();
        for (int i = 0; i < 2000; i++) _ = JsonSerializer.Deserialize<AutoRule>(ruleJson, options);
        double readMs = clock.Elapsed.TotalMilliseconds; long readBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine(JsonSerializer.Serialize(new { mode, configColdMs, coldMs, coldBytes, writeMs, writeBytes, readMs, readBytes, runtime = Environment.Version.ToString() }));
    }
#endif

    private static int Main(string[] args)
    {
        try
        {
#if NET10_0_OR_GREATER
            if (args.Length > 0 && args[0] == "fixtures")
            {
                Directory.CreateDirectory(args[1]);
                File.WriteAllText(Path.Combine(args[1], "config.json"), JsonSerializer.Serialize(Config(), ReflectionJson));
                var rule = Rule(); rule.Actions = rule.Actions.Take(1).ToList();
                File.WriteAllText(Path.Combine(args[1], "rule.json"), JsonSerializer.Serialize(rule, ReflectionJson));
                return 0;
            }
            if (args.Length > 0 && args[0] == "perf") { Perf(args[1], args[2]); return 0; }
#endif
            if (args.Length > 0 && args[0] == "abcde") Abcde();
            else if (args.Length > 0 && args[0] == "shutdown") ShutdownCallbacks();
            else if (args.Length > 0 && args[0] == "legacy") Legacy(); else Checks();
            Console.WriteLine("PASS total=" + _checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
