using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.Core;

internal static partial class Program
{
    private static void ClearConfigurationCache()
    {
        ConfigService.FlushPendingSaves();
        foreach (string name in new[] { "_cache", "_publishedSnapshot", "<LoadWarning>k__BackingField" })
            typeof(ConfigService).GetField(name, StaticHidden)!.SetValue(null, null);
        typeof(ConfigService).GetField("_savesBlocked", StaticHidden)!.SetValue(null, false);
    }

    private static void CheckConfiguration()
    {
        string path = ConfigService.ConfigPath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Check(path.Contains("isolated-data"), "configuration tests are isolated from user settings");
        var fields = typeof(AppConfig).GetProperties().Where(p => typeof(IDictionary).IsAssignableFrom(p.PropertyType)
            || typeof(IList).IsAssignableFrom(p.PropertyType)).ToArray();
        File.WriteAllText(path, "{" + string.Join(",", fields.Select(p => "\"" + p.Name + "\":null")) + "}");
        var cfg = ConfigService.Load();
        Check(fields.Length == 10 && fields.All(p => p.GetValue(cfg) != null), "all ten explicit-null collection fields normalize during actual config loading");
        cfg.Hotkeys["action"] = "Ctrl+F24"; cfg.DeviceNames["device"] = "first";
        Check(ConfigService.TrySave(cfg), "normalized configuration saves successfully");
        string first = File.ReadAllText(path);
        cfg.DeviceNames["device"] = "second";
        Check(ConfigService.TrySave(cfg) && File.ReadAllText(ConfigService.BackupPath) == first,
            "saving retains the complete previous configuration in an atomic .bak");
        string second = File.ReadAllText(path);
        File.Delete(ConfigService.BackupPath); Directory.CreateDirectory(ConfigService.BackupPath);
        cfg.DeviceNames["device"] = "third";
        Check(!ConfigService.TrySave(cfg) && File.ReadAllText(path) == second,
            "a backup failure prevents overwriting the current configuration");
        Directory.Delete(ConfigService.BackupPath);
        Check(ConfigService.TrySave(cfg), "saving recovers when the backup destination becomes available");
        File.WriteAllText(path, "{broken-json"); ClearConfigurationCache(); cfg = ConfigService.Load();
        var notice = ConfigService.LoadWarning ?? throw new Exception("Expected configuration recovery notice");
        Check(notice.UsedBackup && !notice.SavesBlocked && cfg.DeviceNames["device"] == "second",
            "corrupt primary configuration restores the last valid backup");
        Check(File.Exists(notice.PreservedPath) && File.ReadAllText(notice.PreservedPath!) == "{broken-json" && !File.Exists(path),
            "corrupt original bytes are preserved under a distinct name before defaults can be saved");
        Check(ConfigService.TrySave(cfg) && File.Exists(ConfigService.BackupPath), "saving recovered settings keeps the backup available");
        File.Delete(ConfigService.BackupPath); File.WriteAllText(path, "null"); ClearConfigurationCache();
        cfg = ConfigService.Load(); notice = ConfigService.LoadWarning!;
        Check(notice != null && !notice.UsedBackup && File.ReadAllText(notice.PreservedPath!) == "null" && cfg.Hotkeys.Count == 0,
            "a JSON null root is quarantined and default settings are explicitly reported");
        Check(ConfigService.TrySave(cfg), "safe defaults save only after the damaged original is preserved");
        string lockedOriginal = File.ReadAllText(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ClearConfigurationCache(); cfg = ConfigService.Load();
            Check(ConfigService.LoadWarning?.SavesBlocked == true && !ConfigService.TrySave(cfg),
                "an unreadable original blocks writes instead of silently losing settings");
        }
        Check(File.ReadAllText(path) == lockedOriginal, "failed preservation leaves original bytes unchanged");
        ClearConfigurationCache(); cfg = ConfigService.Load();
        File.WriteAllText(path, "{bad-again"); ClearConfigurationCache();
        int before = Directory.GetFiles(Path.GetDirectoryName(path)!, "config.json.corrupt-*").Length;
        var loads = Enumerable.Range(0, 6).Select(_ => Task.Run(ConfigService.Load)).ToArray();
        Task.WaitAll(loads);
        Check(loads.All(t => ReferenceEquals(t.Result, loads[0].Result))
            && Directory.GetFiles(Path.GetDirectoryName(path)!, "config.json.corrupt-*").Length == before + 1,
            "concurrent first loads publish one cache and preserve a corrupt file exactly once");
        cfg = ConfigService.Load(); Check(ConfigService.TrySave(cfg), "recovered configuration remains writable after concurrent loading");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "import.json"), "{\"Hotkeys\":null,\"DeviceNames\":{\"test\":null}}");
        byte[] prepared = ConfigService.PrepareImport(Path.Combine(Path.GetDirectoryName(path)!, "import.json"))!;
        var imported = ConfigService.ReadPreparedConfig(prepared)!;
        Check(imported.Hotkeys != null && imported.DeviceNames["test"] == "", "import preview normalizes null collections and null mapping values");
        Check(ConfigService.ApplyImport(prepared) && ConfigService.Load().Hotkeys != null,
            "import commit and reload retain null-safe settings and back up the previous configuration");
        cfg = ConfigService.Load(); cfg.DeviceNames["queued"] = "queued-save"; ConfigService.QueueSave(cfg); ConfigService.FlushPendingSaves();
        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        Check(saved.RootElement.GetProperty("DeviceNames").GetProperty("queued").GetString() == "queued-save"
            && File.Exists(ConfigService.BackupPath), "coalesced background saves also retain a backup");
    }

    private static void CheckIcons()
    {
        var method = typeof(AppIconService).GetMethod("DecodePng", StaticHidden)!;
        object? Decode(byte[]? bytes) => method.Invoke(null, new object?[] { bytes });
        byte[] broken = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13 };
        Check(Decode(broken) == null && Decode(null) == null && Decode(Array.Empty<byte>()) == null,
            "malformed, empty and absent phone PNG payloads return placeholders without throwing");
        string logged = File.ReadAllText(ExceptionDiagnostics.LogPath);
        for (int i = 0; i < 50; i++) CheckNoThrowDecode(method, broken);
        Check(File.ReadAllText(ExceptionDiagnostics.LogPath) == logged && logged.Contains("Icon.Decode"),
            "the failed immutable payload is logged once and does not cache an exception or repeatedly decode");
        var bitmap = new WriteableBitmap(16, 16, 96, 96, PixelFormats.Bgra32, null);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] valid = stream.ToArray(); var good = Decode(valid);
        Check(good is BitmapImage { IsFrozen: true } && ReferenceEquals(good, Decode(valid)), "a valid replacement icon still freezes and reuses its cached image");
        var itemType = typeof(App).Assembly.GetType("SonicRoute.PhoneAppItem");
        if (itemType != null)
        {
            var androidApp = typeof(App).Assembly.GetReferencedAssemblies().Single(a => a.Name == "SonicRoute.AndroidAudio");
            var appType = Assembly.Load(androidApp).GetType("SonicRoute.AndroidAudio.AndroidApp")!;
            var constructor = appType.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
            var parameters = constructor.GetParameters();
            var arguments = parameters.Select(p => p.HasDefaultValue ? p.DefaultValue : p.ParameterType == typeof(string) ? (object)"fixture" : p.ParameterType == typeof(int) ? 1001 : null).ToArray();
            arguments[Array.FindIndex(parameters, p => p.Name!.Equals("IconPng", StringComparison.OrdinalIgnoreCase))] = broken;
            var item = Activator.CreateInstance(itemType, constructor.Invoke(arguments))!;
            Check(itemType.GetProperty("Icon")!.GetValue(item) == null && itemType.GetProperty("Icon")!.GetValue(item) == null,
                "the phone app item and its media fallback can read a malformed icon repeatedly without a UI exception");
        }
    }

    private static void CheckNoThrowDecode(MethodInfo method, byte[] bytes)
    { if (method.Invoke(null, new object[] { bytes }) != null) throw new Exception("Bad icon unexpectedly decoded"); }

    private static void CheckMouseBindings()
    {
        using var service = new HotkeyService();
        var map = new Dictionary<string, string> { ["fixture-first"] = "Ctrl+Alt+XButton1", ["fixture-second"] = "alt+ctrl+xbutton1",
            ["fixture-wheel-first"] = "Ctrl+WheelUp", ["fixture-wheel-second"] = "Ctrl+WheelUp", ["fixture-invalid"] = "NoSuchModifier+XButton2" };
        service.Reload(map);
        var hook = (MouseHotkeyHook)typeof(HotkeyService).GetField("_mouseHook", Hidden)!.GetValue(service)!;
        var bindings = (ICollection)typeof(MouseHotkeyHook).GetField("_bindings", Hidden)!.GetValue(hook)!;
        Check(bindings.Count == 2, "equivalent modifier orders and duplicate wheel shortcuts compile to two distinct actions");
        Check(service.RegistrationStatus["fixture-second"] == "" && service.RegistrationStatus["fixture-wheel-second"] == ""
            && service.RegistrationStatus["fixture-invalid"] == "", "losing and invalid mouse bindings report actual registration failure");
        Check(service.RegistrationStatus["fixture-first"] == (hook.IsInstalled ? map["fixture-first"] : ""),
            "mouse registration success reflects both arbitration and actual hook installation");
        map.Remove("fixture-first"); service.Reload(map);
        Check(service.RegistrationStatus["fixture-second"] == (hook.IsInstalled ? map["fixture-second"] : ""),
            "removing the winner promotes the previously conflicting mouse binding");
        service.Reload(new Dictionary<string, string>());
        Check(!hook.IsInstalled && bindings.Count == 0 && service.RegistrationStatus.Count == 0,
            "clearing mouse bindings removes the native hook and all registration state");
    }
}
