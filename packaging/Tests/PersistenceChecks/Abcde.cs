using System.Reflection;
using System.Text.Json;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void Abcde()
    {
        string? isolated = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY");
        Check(!string.IsNullOrWhiteSpace(isolated) && Path.GetFullPath(isolated) == Path.GetDirectoryName(ConfigService.ConfigPath), "persistence fixture isolated before service initialization");
        Directory.CreateDirectory(isolated!);
        var gate = typeof(ConfigService).GetField("_writeLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var config = new AppConfig();
        int completed = 0; bool? outcome = null;
        Action<bool> saved = ok => { Interlocked.Increment(ref completed); outcome = ok; };
        ConfigService.SaveCompleted += saved;
        try
        {
            lock (gate)
            {
                for (int i = 0; i < 200; i++) { config.AppNames["fixture"] = i.ToString(); ConfigService.QueueSave(config); }
                config.AppNames["fixture"] = "mutated after capture";
            }
            ConfigService.FlushPendingSaves();
            AppConfig Disk() => JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigService.ConfigPath))!;
            Check(Disk().AppNames["fixture"] == "199", "queued saves keep the last independent collection snapshot");
            Check(completed <= 2 && outcome == true && ConfigService.PendingSaves.IsCompleted, "burst coalesces saves and leaves no worker pending");
            lock (gate)
            {
                ConfigService.QueueSave(new AppConfig { FixedAppName = "old pending" });
                Check(ConfigService.TrySave(new AppConfig { FixedAppName = "new synchronous" }), "critical synchronous save succeeds");
            }
            ConfigService.FlushPendingSaves();
            Check(Disk().FixedAppName == "new synchronous", "older pending save cannot overwrite a critical save");
            string source = Path.Combine(isolated!, "incoming.json");
            File.WriteAllText(source, "{\"FixedAppName\":\"imported\",\"FutureOption\":17}");
            byte[] before = File.ReadAllBytes(ConfigService.ConfigPath);
            var prepared = ConfigService.PrepareImport(source) ?? throw new InvalidOperationException("Import fixture rejected");
            Check(ConfigService.ReadPreparedConfig(prepared)?.FixedAppName == "imported"
                && File.ReadAllBytes(ConfigService.ConfigPath).SequenceEqual(before), "preparing and canceling an import leaves persisted configuration unchanged");
            lock (gate)
            {
                ConfigService.QueueSave(new AppConfig { FixedAppName = "obsolete before import" });
                Check(ConfigService.ApplyImport(prepared), "prepared import commits");
            }
            ConfigService.FlushPendingSaves();
            using (var doc = JsonDocument.Parse(File.ReadAllText(ConfigService.ConfigPath)))
                Check(doc.RootElement.GetProperty("FixedAppName").GetString() == "imported" && doc.RootElement.GetProperty("FutureOption").GetInt32() == 17,
                    "import fences pending saves and preserves unknown fields");
            byte[] importedBytes = File.ReadAllBytes(ConfigService.ConfigPath);
            using (var locked = new FileStream(ConfigService.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                ConfigService.QueueSave(new AppConfig { FixedAppName = "blocked save" });
                ConfigService.FlushPendingSaves();
                Check(outcome == false, "failed asynchronous save reports failure");
            }
            Check(File.ReadAllBytes(ConfigService.ConfigPath).SequenceEqual(importedBytes), "failed atomic save preserves all existing bytes");
            ConfigService.QueueSave(new AppConfig { FixedAppName = "recovered save" }); ConfigService.FlushPendingSaves();
            Check(outcome == true && Disk().FixedAppName == "recovered save", "later edits recover persistence after file contention");
            var actions = AutoActionDisplay.Groups.SelectMany(group => group.Actions).ToArray();
            Check(actions.Length == 39 && actions.Distinct().Count() == 39 && actions.All(action => Enum.IsDefined(typeof(AutoRuleAction), action)), "display metadata covers all 39 stable action IDs exactly once");
            var oldHistory = JsonSerializer.Deserialize<ExecutionHistoryEntry>("{\"Steps\":[{\"Number\":1,\"Action\":0,\"Status\":\"success\"}]}")!;
            Check(oldHistory.Steps[0].TargetDeviceId == null && oldHistory.Steps[0].TotalTargets == null, "old execution history loads without optional target details");
            var rule = new AutoRule { Id = "metadata-test", Name = "metadata test" };
            AutoRuleStore.Save(rule);
            var first = AutoRuleStore.ReadDisplaySnapshot();
            Check(first.Fingerprints!.ContainsKey(rule.Id) && AutoRuleStore.ReadDisplaySnapshot(first.Revision).Rules == null, "unchanged rule revision skips list and fingerprint generation");
            string fingerprint = first.Fingerprints[rule.Id];
            rule.Name = "changed metadata"; AutoRuleStore.Save(rule);
            var second = AutoRuleStore.ReadDisplaySnapshot(first.Revision);
            Check(second.Fingerprints![rule.Id] != fingerprint, "rule edit invalidates cached display fingerprint");
            AutoRuleStore.Shutdown();
        }
        finally { ConfigService.FlushPendingSaves(); ConfigService.SaveCompleted -= saved; }
    }
}
