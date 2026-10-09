using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

internal static class Program
{
    private static readonly List<object> Results = new List<object>();
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string Output = "";
    private static AppConfig Config = new AppConfig();
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };
    private static readonly Func<long>? AllocationCounter = CreateAllocationCounter();
    private static long Allocated() => AllocationCounter == null ? -1 : AllocationCounter();
    private static Func<long>? CreateAllocationCounter()
    {
        var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
        return method == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("SONICROUTE_V121_PERF_ISOLATED", "1");
        Environment.SetEnvironmentVariable("SONICROUTE_V121_READ_ONLY", "1");
        Environment.SetEnvironmentVariable("SONICROUTE_V121_TEST_NO_AUDIO_REFRESH", null);
        Output = args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(Output);
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Config = new AppConfig { Language = "en-US", ThemeMode = "dark", MicMuteOsdPersistent = false, OsdFadeInMs = 0, OsdFadeOutMs = 0 };
            Config.Hotkeys = new Dictionary<string, string>(HotkeyActions.Defaults);
            typeof(ConfigService).GetField("_cache", PrivateStatic)!.SetValue(null, Config);
            typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
            if (!ConfigService.ConfigPath.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
                !AutoRuleStore.RulesDir.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Config/rule storage isolation missing");
            EnsureTestDataPath(ConfigService.ConfigPath);
            EnsureTestDataPath(AutoRuleStore.RulesDir);
            Directory.CreateDirectory(AutoRuleStore.RulesDir);
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent(); // Copied App.OnStartup exits before normal startup tasks.
            L10n.Instance.SetLanguage("en-US");
            ThemeService.Apply("dark", "blue");
            ThemeService.ApplyBackgroundOpacity(Config.BackgroundOpacity);
            Add(new { Case = "environment", Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                LogicalProcessors = Environment.ProcessorCount, RenderTier = System.Windows.Media.RenderCapability.Tier >> 16,
                ConfigPath = ConfigService.ConfigPath, RulesDir = AutoRuleStore.RulesDir,
                ReadOnlyGuards = "System/session volume, mute, microphone, and default-device write APIs blocked by test-copy env guard.",
                Note = "Copied app; config/rules redirected under this binary directory; startup suppressed; DispatcherSynchronizationContext installed before app initialization." });
            string mode = args.Length > 1 ? args[1] : "all";
            if (mode == "storage") RunStorageRegression();
            else if (mode == "storage-mini") RunStorageMini();
            else if (mode == "indexed-save") RunIndexedSave();
            else if (mode == "atomic-conflict") RunAtomicConflictRegression();
            else if (mode == "osd-strict") RunOsdStrict();
            else if (mode == "ui-v121") V121UiCases.Run(Add, Pump, Allocated);
            else if (mode == "core-only") RunCore();
            else if (mode == "peak-ui-only") RunPeakUi();
            else if (mode == "persistence-only") RunPersistenceCheck();
            else if (mode == "supplemental") { RunSupplementalCore(); RunPersistenceCheck(); RunWindows(true); }
            else if (mode == "windows-only") RunWindows();
            else RunUi();
            Save();
            return 0;
        }
        catch (Exception ex)
        {
            Add(new { Case = "error", Error = ex.ToString() });
            Save();
            return 1;
        }
    }

    private static void RunStorageRegression()
    {
        EnsureTestDataPath(AutoRuleStore.RulesDir);
        Directory.CreateDirectory(AutoRuleStore.RulesDir);
        void ResetRules()
        {
            EnsureTestDataPath(AutoRuleStore.RulesDir);
            void Breadcrumb(string step) { Console.Error.WriteLine($"[storage-reset] {DateTime.Now:HH:mm:ss.fff} {step}"); Console.Error.Flush(); }
            Breadcrumb("before enumerate files");
            var files = Directory.GetFiles(AutoRuleStore.RulesDir, "*", SearchOption.AllDirectories);
            Breadcrumb("after enumerate files count=" + files.Length);
            foreach (string file in files) { Breadcrumb("before delete file=" + Path.GetFileName(file)); File.Delete(file); Breadcrumb("after delete file=" + Path.GetFileName(file)); }
            Breadcrumb("before enumerate directories");
            var dirs = Directory.GetDirectories(AutoRuleStore.RulesDir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).ToArray();
            Breadcrumb("after enumerate directories count=" + dirs.Length);
            foreach (string dir in dirs)
                if (Directory.Exists(dir)) { Breadcrumb("before delete dir=" + Path.GetFileName(dir)); Directory.Delete(dir, true); Breadcrumb("after delete dir=" + Path.GetFileName(dir)); }
            Breadcrumb("before invalidate");
            typeof(AutoRuleStore).GetMethod("Invalidate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
            Breadcrumb("after invalidate; before LoadAll");
            AutoRuleStore.LoadAll();
            Breadcrumb("after LoadAll");
        }
        string pathOf(string name) => Path.Combine(AutoRuleStore.RulesDir, name + ".json");
        AutoRule MakeRule(string id, string name) => new AutoRule { Id = id, Name = name, Enabled = false };

        ResetRules();
        string failId = "v121-fail-" + Guid.NewGuid().ToString("N");
        var before = MakeRule(failId, "V121 fail before");
        AutoRuleStore.Save(before);
        string oldPath = pathOf(before.Name);
        before.Name = "V121 fail blocked";
        string blockedPath = pathOf(before.Name);
        Directory.CreateDirectory(blockedPath);
        string? failure = null;
        try { AutoRuleStore.Save(before); } catch (Exception ex) { failure = ex.GetType().Name; }
        bool retainedFile = File.Exists(oldPath);
        bool retainedRecord = AutoRuleStore.LoadAll().Any(r => r.Id == failId);
        Add(new { Case = "rule-save-failure-retains-old", Failure = failure, OldFileExists = retainedFile, RuleStillLoaded = retainedRecord,
            Pass = retainedFile && retainedRecord, Note = "Intentional write failure by creating a directory at the renamed target; test-data only." });
        Directory.Delete(blockedPath, true);

        ResetRules();
        string renameId = "v121-rename-" + Guid.NewGuid().ToString("N");
        var renamed = MakeRule(renameId, "V121 rename before");
        AutoRuleStore.Save(renamed);
        string original = pathOf(renamed.Name);
        renamed.Name = "V121 rename after";
        AutoRuleStore.Save(renamed);
        bool renamePass = File.Exists(pathOf(renamed.Name)) && !File.Exists(original) && AutoRuleStore.LoadAll().Count(r => r.Id == renameId) == 1;
        Add(new { Case = "rule-rename-migrates-file", OldExists = File.Exists(original), NewExists = File.Exists(pathOf(renamed.Name)),
            UniqueLoadedRule = AutoRuleStore.LoadAll().Count(r => r.Id == renameId) == 1, Pass = renamePass });

        ResetRules();
        var sameA = MakeRule("v121-same-a-" + Guid.NewGuid().ToString("N"), "V121 same name");
        var sameB = MakeRule("v121-same-b-" + Guid.NewGuid().ToString("N"), "V121 same name");
        AutoRuleStore.Save(sameA); AutoRuleStore.Save(sameB);
        var sameRows = AutoRuleStore.LoadAll().Where(r => r.Id == sameA.Id || r.Id == sameB.Id).ToList();
        Add(new { Case = "rule-same-name-does-not-overwrite", Loaded = sameRows.Count, Files = Directory.GetFiles(AutoRuleStore.RulesDir, "*.json").Select(Path.GetFileName).ToArray(),
            Pass = sameRows.Count == 2 && Directory.GetFiles(AutoRuleStore.RulesDir, "*.json").Length == 2 });

        ResetRules();
        string legacyId = "v121-old-id-" + Guid.NewGuid().ToString("N");
        var legacy = MakeRule(legacyId, "V121 migrated legacy");
        string legacyPath = pathOf(legacyId);
        Add(new { Case = "legacy-id-migration-breadcrumb", Step = "before fixture serialization" });
        string legacyJson = JsonSerializer.Serialize(legacy); // Use isolated default options; do not reuse results JsonOptions for fixture setup.
        Add(new { Case = "legacy-id-migration-breadcrumb", Step = "after fixture serialization", JsonLength = legacyJson.Length });
        File.WriteAllText(legacyPath, legacyJson);
        Add(new { Case = "legacy-id-migration-breadcrumb", Step = "after fixture write" });
        typeof(AutoRuleStore).GetMethod("Invalidate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
        Add(new { Case = "legacy-id-migration-breadcrumb", Step = "after store invalidate" });
        bool legacyLoaded = AutoRuleStore.LoadAll().Any(r => r.Id == legacyId);
        Add(new { Case = "legacy-id-migration-breadcrumb", Step = "after store load", LegacyLoaded = legacyLoaded });
        AutoRuleStore.Save(legacy);
        Add(new { Case = "legacy-id-migration-breadcrumb", Step = "after store save" });
        Add(new { Case = "rule-legacy-id-filename-migrates", Loaded = legacyLoaded, OldIdFileExists = File.Exists(legacyPath),
            NameFileExists = File.Exists(pathOf(legacy.Name)), Pass = legacyLoaded && !File.Exists(legacyPath) && File.Exists(pathOf(legacy.Name)) });

        ResetRules();
        string deleteId = "v121-delete-" + Guid.NewGuid().ToString("N");
        AutoRuleStore.Save(MakeRule(deleteId, "V121 delete me"));
        _ = AutoRuleStore.LoadAll();
        AutoRuleStore.Delete(deleteId);
        Add(new { Case = "rule-delete-invalidates-cache", FileCount = Directory.GetFiles(AutoRuleStore.RulesDir, "*.json").Length,
            StillLoaded = AutoRuleStore.LoadAll().Any(r => r.Id == deleteId), Pass = !AutoRuleStore.LoadAll().Any(r => r.Id == deleteId) });

        ResetRules();
        string cloneId = "v121-clone-" + Guid.NewGuid().ToString("N");
        var cloneSource = new AutoRule { Id = cloneId, Name = "V121 clone", Enabled = false,
            Actions = new List<AutoRuleStep> { new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdText = "original text" } } };
        AutoRuleStore.Save(cloneSource);
        var copy = AutoRuleStore.LoadAll().Single(r => r.Id == cloneId);
        copy.Name = "mutated copy";
        copy.Actions[0].OsdText = "mutated nested copy";
        var stored = AutoRuleStore.Find(cloneId);
        bool clonePass = stored?.Name == "V121 clone" && stored.Actions.Count == 1 && stored.Actions[0].OsdText == "original text";
        Add(new { Case = "rule-load-find-deep-copy", StoredName = stored?.Name, StoredActionText = stored?.Actions.FirstOrDefault()?.OsdText, Pass = clonePass });

        ResetRules();
        string? snapshotStatus = "not-supported";
        long snapshotRevision = -1;
        var readSnapshot = typeof(AutoRuleStore).GetMethod("ReadSnapshot", BindingFlags.Public | BindingFlags.Static);
        if (readSnapshot != null)
        {
            object first = readSnapshot.Invoke(null, new object[] { -1L })!;
            snapshotRevision = Convert.ToInt64(TupleMember(first, "Revision"));
            object unchanged = readSnapshot.Invoke(null, new object[] { snapshotRevision })!;
            bool unchangedNull = TupleMember(unchanged, "Rules") == null;
            string externalId = "v121-external-" + Guid.NewGuid().ToString("N");
            var external = MakeRule(externalId, "V121 external fixture");
            File.WriteAllText(pathOf(external.Name), JsonSerializer.Serialize(external, JsonOptions));
            var watch = Stopwatch.StartNew();
            object? changed = null;
            while (watch.ElapsedMilliseconds < 2500)
            {
                Thread.Sleep(50);
                changed = readSnapshot.Invoke(null, new object[] { snapshotRevision });
                if (TupleMember(changed!, "Rules") != null && Convert.ToInt64(TupleMember(changed!, "Revision")) > snapshotRevision) break;
            }
            bool externalSeen = changed != null && TupleMember(changed, "Rules") is List<AutoRule> rules && rules.Any(r => r.Id == externalId);
            long afterRevision = changed == null ? -1 : Convert.ToInt64(TupleMember(changed, "Revision"));
            snapshotStatus = "unchanged-null=" + unchangedNull + "; external-seen=" + externalSeen + "; revision=" + snapshotRevision + "->" + afterRevision;
            Add(new { Case = "rule-snapshot-revision-and-external-change", InitialRevision = snapshotRevision, UnchangedReturnsNull = unchangedNull,
                ExternalChangeObserved = externalSeen, FinalRevision = afterRevision, Pass = unchangedNull && externalSeen && afterRevision > snapshotRevision,
                WaitMs = watch.ElapsedMilliseconds });

            // Probe the FileSystemWatcher after its directory disappears and is recreated.
            _ = readSnapshot.Invoke(null, new object[] { afterRevision });
            string rulesDirectory = AutoRuleStore.RulesDir;
            Directory.Delete(rulesDirectory, true);
            Directory.CreateDirectory(rulesDirectory);
            string recoveryId = "v121-recreated-" + Guid.NewGuid().ToString("N");
            var recovered = MakeRule(recoveryId, "V121 recreated fixture");
            File.WriteAllText(pathOf(recovered.Name), JsonSerializer.Serialize(recovered, JsonOptions));
            var recoveryWatch = Stopwatch.StartNew();
            bool recoveredSeen = false;
            long recoveryRevision = afterRevision;
            while (recoveryWatch.ElapsedMilliseconds < 2500)
            {
                Thread.Sleep(50);
                object snapshot = readSnapshot.Invoke(null, new object[] { afterRevision })!;
                recoveryRevision = Convert.ToInt64(TupleMember(snapshot, "Revision"));
                if (TupleMember(snapshot, "Rules") is List<AutoRule> recoveredRules && recoveredRules.Any(r => r.Id == recoveryId)) { recoveredSeen = true; break; }
            }
            Add(new { Case = "rule-watcher-directory-recreated", AutoDiscoveredAfterDirectoryRecreate = recoveredSeen,
                Revision = afterRevision + "->" + recoveryRevision, WaitMs = recoveryWatch.ElapsedMilliseconds,
                Pass = recoveredSeen });
        }
        else Add(new { Case = "rule-snapshot-revision-and-external-change", Status = snapshotStatus });

        Environment.SetEnvironmentVariable("SONICROUTE_V121_TEST_NO_AUDIO_REFRESH", "1");
        foreach (var field in typeof(SessionVolumeService).GetFields(PrivateStatic))
        {
            string name = field.Name.ToLowerInvariant();
            if (!(name.Contains("render") || name.Contains("capture"))) continue;
            if (field.GetValue(null) is System.Collections.IDictionary dictionary) dictionary.Clear();
        }
        bool allMutedEmpty = SessionVolumeService.AllMuted();
        Environment.SetEnvironmentVariable("SONICROUTE_V121_TEST_NO_AUDIO_REFRESH", null);
        Add(new { Case = "all-muted-empty-set", Result = allMutedEmpty, Pass = !allMutedEmpty,
            TestOnlyNoRefreshSeam = true, Note = "Refresh was no-op only in these copied sources; empty render/capture collections set through reflection." });

        var readState = typeof(SystemVolumeService).GetMethod("ReadState", BindingFlags.Public | BindingFlags.Static);
        if (readState == null) Add(new { Case = "system-read-state-merged", Status = "not-supported" });
        else
        {
            var combined = readState.Invoke(null, new object?[] { null });
            var separateVolume = SystemVolumeService.GetVolumePercent();
            var separateMuted = SystemVolumeService.IsMuted();
            object? combinedVolume = TupleMember(combined!, "Volume");
            object? combinedMuted = TupleMember(combined!, "Muted");
            Add(new { Case = "system-read-state-merged", CombinedVolume = combinedVolume, SeparateVolume = separateVolume,
                CombinedMuted = combinedMuted, SeparateMuted = separateMuted,
                ReadConsistent = Convert.ToInt32(combinedVolume) == separateVolume && Convert.ToBoolean(combinedMuted) == separateMuted,
                ReadOnly = true, Note = "Three immediate reads; small state changes by external apps can cause a transient mismatch." });
        }
    }

    private static void ResetRuleFiles()
    {
        EnsureTestDataPath(AutoRuleStore.RulesDir);
        Directory.CreateDirectory(AutoRuleStore.RulesDir);
        foreach (string file in Directory.GetFiles(AutoRuleStore.RulesDir, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (string dir in Directory.GetDirectories(AutoRuleStore.RulesDir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        AutoRuleStore.Invalidate();
    }

    private static void EnsureTestDataPath(string path)
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data")) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing test-data operation outside isolated path: " + full);
    }

    private static void RunStorageMini()
    {
        ResetRuleFiles();
        string name = "V121 atomic failure " + Guid.NewGuid().ToString("N");
        var rule = new AutoRule { Id = "v121-mini-" + Guid.NewGuid().ToString("N"), Name = name, Enabled = false };
        AutoRuleStore.Save(rule);
        string oldPath = Path.Combine(AutoRuleStore.RulesDir, name + ".json");
        rule.Name += " renamed";
        string blocked = Path.Combine(AutoRuleStore.RulesDir, rule.Name + ".json");
        Directory.CreateDirectory(blocked);
        string? failure = null;
        try { AutoRuleStore.Save(rule); } catch (Exception e) { failure = e.GetType().Name; }
        bool retained = File.Exists(oldPath) && AutoRuleStore.LoadAll().Any(r => r.Id == rule.Id);
        Add(new { Case = "atomic-save-failure-preserves-loaded-rule", Failure = failure,
            OldFileExists = File.Exists(oldPath), RuleStillLoaded = AutoRuleStore.LoadAll().Any(r => r.Id == rule.Id), Pass = retained,
            Note = "Directory at target path forces a write failure inside redirected test-data." });
        Directory.Delete(blocked, true);

        var snapshotMethod = typeof(AutoRuleStore).GetMethod("ReadSnapshot", BindingFlags.Public | BindingFlags.Static);
        if (snapshotMethod == null)
        {
            Add(new { Case = "snapshot-revision-watch", Status = "not-supported-in-baseline" });
            return;
        }
        object first = snapshotMethod.Invoke(null, new object[] { -1L })!;
        long revision = Convert.ToInt64(TupleMember(first, "Revision"));
        object same = snapshotMethod.Invoke(null, new object[] { revision })!;
        bool unchangedNull = TupleMember(same, "Rules") == null;
        var external = new AutoRule { Id = "v121-watch-" + Guid.NewGuid().ToString("N"), Name = "V121 watcher " + Guid.NewGuid().ToString("N"), Enabled = false };
        File.WriteAllText(Path.Combine(AutoRuleStore.RulesDir, external.Name + ".json"), JsonSerializer.Serialize(external, JsonOptions));
        var watch = Stopwatch.StartNew();
        bool seen = false;
        long finalRevision = revision;
        while (watch.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(40);
            object changed = snapshotMethod.Invoke(null, new object[] { revision })!;
            finalRevision = Convert.ToInt64(TupleMember(changed, "Revision"));
            if (TupleMember(changed, "Rules") is List<AutoRule> rules && rules.Any(r => r.Id == external.Id)) { seen = true; break; }
        }
        Add(new { Case = "snapshot-revision-watch", UnchangedReturnsNull = unchangedNull,
            ExternalFileDetected = seen, InitialRevision = revision, FinalRevision = finalRevision,
            WaitMs = watch.ElapsedMilliseconds, Pass = unchangedNull && seen && finalRevision > revision });
    }

    private static void RunIndexedSave()
    {
        ResetRuleFiles();
        var fixtures = Enumerable.Range(0, 100).Select(i => new AutoRule
        {
            Id = "v121-indexed-save-" + i.ToString("D3"),
            Name = "V121 indexed save " + i.ToString("D3"),
            Enabled = false,
            Actions = Enumerable.Range(0, 8).Select(_ => new AutoRuleStep
                { Action = AutoRuleAction.ShowOsd, OsdTitle = "Validation", OsdText = "Indexed save" }).ToList()
        }).ToList();
        foreach (var fixture in fixtures) AutoRuleStore.Save(fixture);
        AutoRuleStore.Invalidate();
        var loaded = AutoRuleStore.LoadAll();
        if (loaded.Count != 100) throw new InvalidOperationException("Expected 100 actual files after AutoRuleStore reload; got " + loaded.Count);
        var target = AutoRuleStore.Find(fixtures[0].Id) ?? throw new InvalidOperationException("Indexed rule not found after reload");
        Add(new { Case = "indexed-save-fixture", Files = Directory.GetFiles(AutoRuleStore.RulesDir, "*.json").Length,
            Reloaded = loaded.Count, RuleLoadedViaFind = true, StoreInvalidateAndReload = true });
        string targetPath = Path.Combine(AutoRuleStore.RulesDir, "V121 indexed save 000.json");
        try { Measure("automation-save-with-100-indexed-rules", 50, () => AutoRuleStore.Save(target)); }
        catch (IOException error)
        {
            Add(AtomicIoFacts("indexed-save-io-diagnostic", error, targetPath));
            throw;
        }
    }

    private static void RunAtomicConflictRegression()
    {
        ResetRuleFiles();
        const string ruleName = "V121 atomic sharing conflict";
        const string ruleId = "v121-atomic-sharing-conflict";
        string targetPath = Path.Combine(AutoRuleStore.RulesDir, ruleName + ".json");
        var original = new AutoRule { Id = ruleId, Name = ruleName, Enabled = false,
            Actions = new List<AutoRuleStep> { new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdText = "old-value" } } };
        AutoRuleStore.Save(original);
        AutoRuleStore.Invalidate();
        _ = AutoRuleStore.LoadAll();
        string oldDisk = File.ReadAllText(targetPath);

        var blockedUpdate = AutoRuleStore.Find(ruleId)!;
        blockedUpdate.Actions[0].OsdText = "must-not-commit";
        IOException? persistentError = null;
        FileAttributes? targetAttrsWhileBlocked;
        var failureWatch = Stopwatch.StartNew();
        using (var held = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            targetAttrsWhileBlocked = File.GetAttributes(targetPath);
            try { AutoRuleStore.Save(blockedUpdate); }
            catch (IOException error) { persistentError = error; }
        }
        failureWatch.Stop();
        string failedDisk = File.ReadAllText(targetPath);
        string? failedCachedText = AutoRuleStore.Find(ruleId)?.Actions.FirstOrDefault()?.OsdText;
        string[] failedTemps = FindAtomicTemps(targetPath);
        Add(AtomicIoFacts("atomic-persistent-sharing-lock", persistentError, targetPath,
            new { ElapsedMs = failureWatch.Elapsed.TotalMilliseconds, TargetAttributesWhileLocked = targetAttrsWhileBlocked,
                OldDiskPreserved = failedDisk == oldDisk, CachedOldValuePreserved = failedCachedText == "old-value",
                NoTemporaryFilesRemain = failedTemps.Length == 0, RemainingTempFiles = failedTemps }));

        var shortLockUpdate = AutoRuleStore.Find(ruleId)!;
        shortLockUpdate.Actions[0].OsdText = "short-lock-committed";
        using (var held = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var startRelease = new ManualResetEventSlim(false))
        {
            Exception? releaseError = null;
            double releasedAtMs = -1;
            var lockClock = Stopwatch.StartNew();
            var releaseThread = new Thread(() =>
            {
                startRelease.Wait();
                Thread.Sleep(10);
                releasedAtMs = lockClock.Elapsed.TotalMilliseconds;
                try { held.Dispose(); } catch (Exception error) { releaseError = error; }
            }) { IsBackground = true, Name = "V121 short file lock release" };
            releaseThread.Start();
            startRelease.Set();
            var saveWatch = Stopwatch.StartNew();
            IOException? shortLockError = null;
            try { AutoRuleStore.Save(shortLockUpdate); }
            catch (IOException error) { shortLockError = error; }
            saveWatch.Stop();
            if (!releaseThread.Join(1000)) throw new TimeoutException("Short sharing lock release thread did not finish.");
            string shortDisk = File.ReadAllText(targetPath);
            string? shortCachedText = AutoRuleStore.Find(ruleId)?.Actions.FirstOrDefault()?.OsdText;
            string[] shortTemps = FindAtomicTemps(targetPath);
            Add(new { Case = "atomic-short-sharing-lock", TargetPath = targetPath,
                ReleasedAtMs = releasedAtMs, SaveElapsedMs = saveWatch.Elapsed.TotalMilliseconds,
                ReleaseError = releaseError?.ToString(), ErrorHResult = shortLockError?.HResult.ToString("X8"),
                DiskContainsNewValue = shortDisk.Contains("short-lock-committed"),
                CacheContainsNewValue = shortCachedText == "short-lock-committed", NoTemporaryFilesRemain = shortTemps.Length == 0,
                RemainingTempFiles = shortTemps, Pass = shortLockError == null && releaseError == null && shortCachedText == "short-lock-committed"
                    && shortDisk.Contains("short-lock-committed") && shortTemps.Length == 0 });
        }
    }

    private static string[] FindAtomicTemps(string targetPath)
    {
        string directory = Path.GetDirectoryName(targetPath)!;
        return Directory.Exists(directory) ? Directory.GetFiles(directory, Path.GetFileName(targetPath) + ".*.tmp") : Array.Empty<string>();
    }

    private static object AtomicIoFacts(string caseName, IOException? error, string targetPath, object? extra = null)
    {
        FileAttributes? attrs = File.Exists(targetPath) ? File.GetAttributes(targetPath) : null;
        var temps = FindAtomicTemps(targetPath).Select(path => new { Path = path,
            Attributes = File.GetAttributes(path), Length = new FileInfo(path).Length }).ToArray();
        return new { Case = caseName, TargetPath = targetPath, TargetExists = File.Exists(targetPath),
            TargetAttributes = attrs, HResult = error?.HResult.ToString("X8"), Win32Code = error == null ? null : (int?)(error.HResult & 0xffff),
            Error = error?.ToString(), TempFileCount = temps.Length, TempFiles = temps, Extra = extra };
    }

    private static void WaitTask(Task task, string label, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < timeoutMs) Pump(10);
        if (!task.IsCompleted) throw new TimeoutException(label + " timed out after " + timeoutMs + "ms");
        task.GetAwaiter().GetResult();
    }

    private static object? TupleMember(object tuple, string name)
    {
        var type = tuple.GetType();
        object? value = type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(tuple)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(tuple);
        if (value != null) return value;
        string itemName = name == "Revision" || name == "Volume" ? "Item1" :
                          name == "Rules" || name == "Muted" ? "Item2" : "";
        return itemName.Length == 0 ? null : type.GetField(itemName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(tuple);
    }

    private static void RunOsdStrict()
    {
        var osdType = typeof(App).Assembly.GetType("SonicRoute.OsdService", true)!;
        var service = Activator.CreateInstance(osdType, true)!;
        var showMethod = osdType.GetMethod("ShowVolume", PrivateInstance)!;
        var notifyMethod = osdType.GetMethod("NotifyVolumeVisualChanged", PrivateInstance)!;
        var show = (Action<string, int, string, bool, string>)Delegate.CreateDelegate(typeof(Action<string, int, string, bool, string>), service, showMethod);
        var notify = (Action)Delegate.CreateDelegate(typeof(Action), service, notifyMethod);
        Config.OsdFadeInMs = 0; Config.OsdFadeOutMs = 0; Config.OsdVolumeVisualEnabled = true;
        ConfigService.Save(Config); notify();
        show("v1.21 validation", 41, "41%", false, "v121-test-target");
        Pump(350);
        var window = (Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service);
        if (window == null || !window.IsVisible) throw new InvalidOperationException("OSD test copy did not become visible");
        var initialPlacement = OsdPlacementFacts(window);
        Add(new { Case = "osd-onscreen-window-placement", Placement = initialPlacement });
        var fillScaleField = osdType.GetField("_fillScale", PrivateInstance);
        int changingCount = 0, renderEvents = 0;
        double previousScale = double.NaN;
        EventHandler renderMonitor = (_, _) =>
        {
            renderEvents++;
            object? scale = fillScaleField?.GetValue(service);
            object? x = scale?.GetType().GetProperty("ScaleX")?.GetValue(scale);
            if (x == null) return;
            double value = Convert.ToDouble(x);
            if (double.IsNaN(previousScale) || Math.Abs(value - previousScale) > 0.00001) changingCount++;
            previousScale = value;
        };

        void MeasurePhase(string caseName, bool enabled, int durationMs, bool trackScale)
        {
            Config.OsdVolumeVisualEnabled = enabled;
            ConfigService.Save(Config); notify();
            show("v1.21 validation", 41, "41%", false, "v121-test-target");
            Pump(500);
            window = (Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service);
            if (window == null) throw new InvalidOperationException("OSD window missing before timed phase");
            var placement = OsdPlacementFacts(window);
            bool inView = window.IsVisible && window.Opacity > 0 && IntersectsWorkArea(window);
            if (!inView) throw new InvalidOperationException("OSD is not visibly intersecting the primary work area before timed phase: " + JsonSerializer.Serialize(placement, JsonOptions));
            changingCount = 0; renderEvents = 0; previousScale = double.NaN;
            if (trackScale) CompositionTarget.Rendering += renderMonitor;
            int calls = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30.0) };
            timer.Tick += (_, _) => { int value = (calls * 17 + 9) % 101; calls++; show("v1.21 validation", value, value + "%", false, "v121-test-target"); };
            var process = Process.GetCurrentProcess(); process.Refresh();
            double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
            long allocationBefore = Allocated(); int gen0Before = GC.CollectionCount(0);
            var watch = Stopwatch.StartNew(); timer.Start(); Pump(durationMs); timer.Stop(); watch.Stop();
            process.Refresh();
            if (trackScale) CompositionTarget.Rendering -= renderMonitor;
            double elapsed = watch.Elapsed.TotalMilliseconds;
            double cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpuBefore;
            Add(new { Case = caseName, BarEnabled = enabled, ScaleMonitorEnabled = trackScale, RequestedInputHz = 30, InputCalls = calls,
                ActualInputHz = calls * 1000.0 / elapsed, RenderingEvents = renderEvents, RenderingHz = renderEvents * 1000.0 / elapsed,
                ScaleXChangedFrames = changingCount, ScaleXChangedFramesPerSecond = changingCount * 1000.0 / elapsed,
                WallMs = elapsed, CpuMs = cpuMs, MachineCpuPercent = 100 * cpuMs / elapsed / Environment.ProcessorCount,
                UiThreadAllocatedBytes = allocationBefore < 0 ? -1 : Allocated() - allocationBefore,
                Gen0Collections = GC.CollectionCount(0) - gen0Before, WorkingSetMiB = process.WorkingSet64 / 1048576.0,
                PrivateMiB = process.PrivateMemorySize64 / 1048576.0, RenderTier = System.Windows.Media.RenderCapability.Tier >> 16,
                Placement = placement, ScaleRateMeaning = "ScaleX logical property changes per second; not compositor-presented FPS." });
        }

        MeasurePhase("osd-30hz-bar-on-cpu", true, 7000, false);
        MeasurePhase("osd-30hz-bar-on-frame-diagnostic", true, 7000, true);
        MeasurePhase("osd-30hz-bar-off-cpu", false, 7000, false);
        Config.OsdVolumeVisualEnabled = true; ConfigService.Save(Config); notify();
        show("v1.21 validation", 77, "77%", false, "v121-test-target");
        Pump(250);
        var percent = osdType.GetField("_percent", PrivateInstance)?.GetValue(service) as TextBlock;
        object? notice = osdType.GetField("_notice", PrivateInstance)?.GetValue(service);
        object? noticeVolume = notice == null ? null : TupleMember(notice, "Volume");
        object? scaleObject = fillScaleField?.GetValue(service);
        object? scaleValue = scaleObject?.GetType().GetProperty("ScaleX")?.GetValue(scaleObject);
        double? finalScaleX = scaleValue == null ? null : Convert.ToDouble(scaleValue);
        bool finalNumeric = percent?.Text == "77%" && (noticeVolume == null || Convert.ToInt32(noticeVolume) == 77);
        bool finalScaleCorrect = finalScaleX.HasValue && Math.Abs(finalScaleX.Value - 0.77) < 0.001;
        Pump(250);
        object? ReadFlag(string name) => osdType.GetField(name, PrivateInstance)?.GetValue(service);
        bool? animating = ReadFlag("_barAnimating") as bool?;
        bool? pending = ReadFlag("_barUpdatePending") as bool?;
        bool? subscribed = ReadFlag("_barRenderingSubscribed") as bool?;
        Add(new { Case = "osd-final-value-and-animation-stop", PercentText = percent?.Text, NoticeVolume = noticeVolume, FinalScaleX = finalScaleX, ExpectedScaleX = 0.77,
            BarAnimating = animating, PendingRenderUpdate = pending, RenderingSubscribed = subscribed,
            Pass = finalNumeric && finalScaleCorrect && animating != true && pending != true && subscribed != true });
        Pump(1200);
        window = (Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service);
        Add(new { Case = "osd-hidden-idle-state", IsVisible = window?.IsVisible, BarAnimating = ReadFlag("_barAnimating"),
            PendingRenderUpdate = ReadFlag("_barUpdatePending"), RenderingSubscribed = ReadFlag("_barRenderingSubscribed"),
            Pass = window?.IsVisible != true && !Equals(ReadFlag("_barAnimating"), true) && !Equals(ReadFlag("_barUpdatePending"), true) && !Equals(ReadFlag("_barRenderingSubscribed"), true) });
        MeasureIdle("osd-hidden-idle-2500ms", 2500);
        ((IDisposable)service).Dispose();
    }
    private static void RunPeakUi()
    {
        var panel = new QuickPanelModernWindow { ShowActivated = false, ShowInTaskbar = false };
        panel.Show();
        var load = (Task)typeof(QuickPanelModernWindow).GetMethod("LoadAsync", PrivateInstance)!.Invoke(panel, null)!;
        var timeout = Stopwatch.StartNew();
        while (!load.IsCompleted && timeout.ElapsedMilliseconds < 10000) Pump(10);
        if (!load.IsCompleted) throw new TimeoutException("Peak test panel load timeout");
        load.GetAwaiter().GetResult();
        if (!(bool)typeof(QuickPanelModernWindow).GetField("_contentReady", PrivateInstance)!.GetValue(panel)!)
            throw new InvalidOperationException("Peak test panel failed to load");
        var rows = (System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_rows", PrivateInstance)!.GetValue(panel)!;
        var pids = rows.Keys.Cast<int>().ToArray();
        if (pids.Length == 0) throw new InvalidOperationException("No app rows for peak test");
        typeof(QuickPanelModernWindow).GetMethod("StopMeter", PrivateInstance)!.Invoke(panel, null);
        var publish = (Action<IReadOnlyDictionary<int, float>>)Delegate.CreateDelegate(typeof(Action<IReadOnlyDictionary<int, float>>), panel,
            typeof(QuickPanelModernWindow).GetMethod("OnPeaksUpdated", PrivateInstance)!);
        int warmCalls = 0;
        var warm = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
        warm.Tick += (_, _) => publish(PeakSnapshot(pids, ++warmCalls, pids.Length));
        warm.Start(); Pump(8000); warm.Stop(); Pump(500);
        foreach (int changing in new[] { 0, 1, pids.Length, 0 })
        {
            int calls = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (_, _) => publish(PeakSnapshot(pids, ++calls, changing));
            publish(PeakSnapshot(pids, 0, changing)); Pump(300);
            var process = Process.GetCurrentProcess(); process.Refresh();
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            long allocated = Allocated();
            var watch = Stopwatch.StartNew();
            if (changing > 0) timer.Start();
            Pump(6000); timer.Stop();
            process.Refresh();
            double wall = watch.Elapsed.TotalMilliseconds;
            double used = process.TotalProcessorTime.TotalMilliseconds - cpu;
            Add(new { Case = "synthetic-peak-ui", AppRows = pids.Length, ChangingRows = changing, ActualSnapshots = calls,
                ActualHz = calls * 1000.0 / wall, WallMs = wall, CpuMs = used,
                MachineCpuPercent = 100 * used / wall / Environment.ProcessorCount,
                UiThreadAllocatedBytes = allocated < 0 ? -1 : Allocated() - allocated,
                Note = "Native meter stopped. Synthetic snapshots exercise actual peak coalescing and ScaleTransform UI; no audio played." });
        }
        panel.Close(); Pump(300);
    }

    private static IReadOnlyDictionary<int, float> PeakSnapshot(int[] pids, int tick, int changing)
    {
        var snapshot = new Dictionary<int, float>();
        for (int i = 0; i < pids.Length; i++) snapshot[pids[i]] = i < changing ? (float)(0.45 + 0.4 * Math.Sin(tick * 0.33 + i)) : 0f;
        return snapshot;
    }

    private static void RunPersistenceCheck()
    {
        var rule = new AutoRule { Id = "perf-rename-failure-id", Name = "Perf rename before", Enabled = false };
        AutoRuleStore.Save(rule);
        var oldFile = Path.Combine(AutoRuleStore.RulesDir, rule.Name + ".json");
        if (!File.Exists(oldFile)) throw new InvalidOperationException("Original test rule was not saved");
        rule.Name = "Perf rename after";
        var target = Path.Combine(AutoRuleStore.RulesDir, rule.Name + ".json");
        Directory.CreateDirectory(target); // Deliberately block the new file path, only within isolated test-data.
        string? failure = null;
        try { AutoRuleStore.Save(rule); }
        catch (Exception e) { failure = e.GetType().Name; }
        bool exists = AutoRuleStore.LoadAll().Any(r => r.Id == rule.Id);
        Add(new { Case = "automation-rename-write-failure", Failure = failure, OldFileExists = File.Exists(oldFile),
            RuleStillOnDisk = exists, TargetBlockedByTestDirectory = Directory.Exists(target),
            Note = "Controlled failure in redirected test-data only. No user rules touched." });
        typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
    }

    private static List<AutoRule> SyntheticRules(int count)
    {
        return Enumerable.Range(0, count).Select(i => new AutoRule
        {
            Id = "perf-rule-" + i.ToString("D3"), Name = "Synthetic " + i, Enabled = false,
            Actions = Enumerable.Range(0, 8).Select(j => new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdTitle = "Test", OsdText = "Test" }).ToList()
        }).ToList();
    }

    private static void RunSupplementalCore()
    {
        var getVolume = typeof(SystemVolumeService).GetMethod("GetVolume", PrivateStatic)!;
        Action combined = () =>
        {
            var endpoint = (IAudioEndpointVolume?)getVolume.Invoke(null, new object?[] { null });
            if (endpoint == null) throw new InvalidOperationException("No default endpoint for read test");
            try
            {
                if (endpoint.GetMasterVolumeLevelScalar(out float _) < 0 || endpoint.GetMute(out int _) < 0)
                    throw new InvalidOperationException("Combined read failed");
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(endpoint); }
        };
        Measure("device-volume-and-mute-separate-recheck", 100, () => { SystemVolumeService.GetVolumePercent(); SystemVolumeService.IsMuted(); });
        Measure("device-volume-and-mute-combined-prototype", 100, combined);
        var rules = SyntheticRules(100);
        Directory.CreateDirectory(AutoRuleStore.RulesDir);
        foreach (var rule in rules)
            File.WriteAllText(Path.Combine(AutoRuleStore.RulesDir, rule.Id + ".json"), JsonSerializer.Serialize(rule, JsonOptions));
        Measure("automation-save-among-100-rules", 30, () => AutoRuleStore.Save(rules[0]));
        typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
        Add(new { Case = "supplemental-prototype-limits", Note = "Combined endpoint read is a test-only prototype via reflection; production unchanged. 100 disabled synthetic rules stored only under redirected test-data." });
    }

    private static void RunCore()
    {
        var apps = AudioService.GetApps(true);
        var outputs = AudioService.GetDevices(EDataFlow.eRender);
        var inputs = AudioService.GetDevices(EDataFlow.eCapture);
        Add(new { Case = "audio-environment", Applications = apps.Count, NamedApplications = apps.Count(a => !string.IsNullOrWhiteSpace(a.ProcessName)), ActiveApplications = apps.Count(a => a.HasActiveSession), Outputs = outputs.Count, Inputs = inputs.Count });
        Measure("apps-fresh", 20, () => AudioService.GetApps(true));
        Measure("apps-cached", 1000, () => AudioService.GetApps());
        Measure("output-devices-fresh", 20, () => { typeof(AudioService).GetField("_deviceCacheRenderAt", PrivateStatic)!.SetValue(null, 0L); AudioService.GetDevices(EDataFlow.eRender); });
        Measure("output-devices-cached", 1000, () => AudioService.GetDevices(EDataFlow.eRender));
        Measure("session-refresh", 20, () => SessionVolumeService.Refresh(true));
        if (apps.Count > 0)
        {
            int pid = (int)apps[0].ProcessId;
            SessionVolumeService.Refresh(true);
            Measure("app-volume-and-mute", 400, () => { SessionVolumeService.GetVolumePercent(pid); SessionVolumeService.IsMuted(pid); });
        }
        Measure("device-volume-and-mute", 100, () => { SystemVolumeService.GetVolumePercent(); SystemVolumeService.IsMuted(); });
        Measure("global-microphone-state", 40, () => GlobalMicMuteService.IsAnyMuted(true));
        for (int i = 0; i < 30; i++) Config.AppNames["synthetic-app-" + i] = "Synthetic application " + i;
        for (int i = 0; i < 20; i++) Config.DeviceNames["synthetic-device-" + i] = "Synthetic device " + i;
        Measure("config-json-new-options", 600, () => JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
        Measure("config-json-shared-options", 600, () => JsonSerializer.Serialize(Config, JsonOptions));
        Measure("config-save-isolated-file", 150, () => ConfigService.Save(Config));
        var rules = Enumerable.Range(0, 100).Select(i => new AutoRule { Id = Guid.NewGuid().ToString("N"), Name = "Synthetic " + i }).ToList();
        foreach (var rule in rules) rule.Actions = Enumerable.Range(0, 8).Select(i => new AutoRuleStep { Action = AutoRuleAction.ShowOsd, OsdTitle = "Test", OsdText = "Test" }).ToList();
        Measure("automation-signature-100-rules-800-steps", 100, () => JsonSerializer.Serialize(rules));
        RunMeter("meter-no-targets", new int[0], 6);
        var targets = apps.Where(a => SessionVolumeService.HasSession((int)a.ProcessId) && !SessionVolumeService.IsMuted((int)a.ProcessId))
            .Select(a => (int)a.ProcessId).Distinct().ToArray();
        if (targets.Length > 0)
        {
            RunMeter("meter-one-target", targets.Take(1).ToArray(), 6);
            RunMeter("meter-all-eligible-targets", targets, 6);
        }
    }

    private static void RunMeter(string name, int[] targets, int seconds)
    {
        int notifications = 0;
        float maxPeak = 0;
        using (var meter = new AudioMeterService())
        {
            meter.PeaksUpdated += peaks => { Interlocked.Increment(ref notifications); foreach (float value in peaks.Values) if (value > maxPeak) maxPeak = value; };
            meter.Start(targets);
            Thread.Sleep(300);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            double cpu = process.TotalProcessorTime.TotalMilliseconds;
            var watch = Stopwatch.StartNew();
            Thread.Sleep(seconds * 1000);
            process.Refresh();
            double elapsed = watch.Elapsed.TotalMilliseconds;
            double used = process.TotalProcessorTime.TotalMilliseconds - cpu;
            var stop = Stopwatch.StartNew();
            meter.Stop();
            stop.Stop();
            Add(new { Case = name, Targets = targets.Length, DurationMs = elapsed, CpuMs = used,
                MachineCpuPercent = 100 * used / elapsed / Environment.ProcessorCount, Notifications = notifications,
                MaximumPublishedPeak = maxPeak, StopMs = stop.Elapsed.TotalMilliseconds });
        }
    }

    private static void RunUi()
    {
        var osdType = typeof(App).Assembly.GetType("SonicRoute.OsdService", true)!;
        var showMethod = osdType.GetMethod("ShowVolume", PrivateInstance)!;
        var updateMethod = osdType.GetMethod("NotifyVolumeVisualChanged", PrivateInstance)!;
        var service = Activator.CreateInstance(osdType, true)!;
        var show = (Action<string, int, string, bool, string>)Delegate.CreateDelegate(typeof(Action<string, int, string, bool, string>), service, showMethod);
        var notify = (Action)Delegate.CreateDelegate(typeof(Action), service, updateMethod);
        Config.OsdVolumeVisualEnabled = false;
        double start = Clock.Elapsed.TotalMilliseconds;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> diagnostic = (_, e) =>
        {
            if ((e.Exception.StackTrace ?? "").Contains("OsdService")) Console.Error.WriteLine("OSD first-chance: " + e.Exception);
        };
        AppDomain.CurrentDomain.FirstChanceException += diagnostic;
        show("SonicRoute performance test", 50, "50%", false, "test");
        AppDomain.CurrentDomain.FirstChanceException -= diagnostic;
        double firstShowSynchronousMs = Clock.Elapsed.TotalMilliseconds - start;
        var firstVisible = ((Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service))?.IsVisible;
        Pump(300);
        var window = (Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service);
        if (window == null || !window.IsVisible)
        {
            osdType.GetMethod("EnsureWindow", PrivateInstance)!.Invoke(service, null);
            osdType.GetMethod("ApplyConfig", PrivateInstance)!.Invoke(service, new object[] { Config });
            osdType.GetMethod("ApplyNotice", PrivateInstance)!.Invoke(service, new object[] { Config, false });
            osdType.GetMethod("ApplyMetrics", PrivateInstance)!.Invoke(service, null);
            throw new InvalidOperationException("OSD was not displayed; diagnostic private calls completed; immediate visibility=" + firstVisible + "; synchronous milliseconds=" + firstShowSynchronousMs);
        }
        Add(new { Case = "osd-first-show", SynchronousMs = firstShowSynchronousMs, ImmediatelyVisible = firstVisible, WallToSettledMs = Clock.Elapsed.TotalMilliseconds - start, FixedPumpMs = 300 });
        foreach (bool enabled in new[] { false, true })
        {
            Config.OsdVolumeVisualEnabled = enabled;
            notify();
            int warmCalls = 0;
            var warmTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
            warmTimer.Tick += (_, _) => { int volume = (warmCalls++ * 4) % 101; show("SonicRoute performance test", volume, volume + "%", false, "test"); };
            warmTimer.Start(); Pump(6000); warmTimer.Stop();
        }
        Add(new { Case = "osd-warmup", WarmupMs = 12000, Note = "Both variants fed changing values before timed comparisons to reduce startup/JIT bias." });
        foreach (int hz in new[] { 30, 100 })
        {
            foreach (bool enabled in new[] { false, true, true, false })
            {
                Config.OsdVolumeVisualEnabled = enabled;
                notify();
                show("SonicRoute performance test", 50, "50%", false, "test");
                Pump(250);
                int calls = 0;
                var durations = new List<double>();
                var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1000.0 / hz) };
                timer.Tick += (_, _) => {
                    int volume = (calls++ * 4) % 101;
                    var before = Clock.Elapsed.TotalMilliseconds;
                    show("SonicRoute performance test", volume, volume + "%", false, "test");
                    durations.Add(Clock.Elapsed.TotalMilliseconds - before);
                };
                var process = Process.GetCurrentProcess();
                process.Refresh();
                double cpu = process.TotalProcessorTime.TotalMilliseconds;
                long allocation = Allocated();
                int gen0 = GC.CollectionCount(0);
                var watch = Stopwatch.StartNew();
                timer.Start();
                Pump(6000);
                timer.Stop();
                process.Refresh();
                double elapsed = watch.Elapsed.TotalMilliseconds;
                double used = process.TotalProcessorTime.TotalMilliseconds - cpu;
                Add(new { Case = enabled ? "osd-bar-on" : "osd-bar-off", RequestedHz = hz, ActualCalls = calls,
                    ActualHz = 1000.0 * calls / elapsed, WallMs = elapsed, CpuMs = used,
                    MachineCpuPercent = 100 * used / elapsed / Environment.ProcessorCount,
                    UiThreadAllocatedBytes = allocation < 0 ? -1 : Allocated() - allocation,
                    HandlerP95Ms = Percentile(durations, 0.95), Gen0Collections = GC.CollectionCount(0) - gen0,
                    WorkingSetMiB = process.WorkingSet64 / 1048576.0, PrivateMiB = process.PrivateMemorySize64 / 1048576.0 });
            }
        }
        Pump(1800);
        MeasureIdle("osd-hidden-idle", 5000);
        var hiddenWindow = (Window?)osdType.GetField("_window", PrivateInstance)!.GetValue(service);
        Add(new { Case = "osd-hidden-state", IsVisible = hiddenWindow?.IsVisible, PendingRender = osdType.GetField("_barUpdatePending", PrivateInstance)!.GetValue(service) });
        ((IDisposable)service).Dispose();
        RunWindows();
    }

    private static void RunWindows(bool supplemental = false)
    {
        MainWindow? main = null;
        var before = Clock.Elapsed.TotalMilliseconds;
        main = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        Add(new { Case = "main-window-constructor-first", WallMs = Clock.Elapsed.TotalMilliseconds - before });
        main.Show();
        Pump(1000);
        foreach (string tag in new[] { "Settings", "Automation", "Theme", "Hotkeys", "Settings", "Automation" })
        {
            var nav = typeof(MainWindow).GetMethod("Nav_Checked", PrivateInstance)!;
            var watch = Stopwatch.StartNew();
            nav.Invoke(main, new object[] { new RadioButton { Tag = tag }, new RoutedEventArgs() });
            double synchronous = watch.Elapsed.TotalMilliseconds;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Add(new { Case = "navigation", Page = tag, SynchronousMs = synchronous, ToDispatcherIdleMs = watch.Elapsed.TotalMilliseconds });
            Pump(350);
        }
        if (supplemental)
        {
            var items = (ItemsControl)main.FindName("AutoRuleList");
            foreach (int count in new[] { 0, 20, 100, 100 })
            {
                typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, SyntheticRules(count));
                long allocated = Allocated();
                var watch = Stopwatch.StartNew();
                var task = (Task)typeof(MainWindow).GetMethod("RefreshAutoRulesAsync", PrivateInstance)!.Invoke(main, null)!;
                while (!task.IsCompleted && watch.ElapsedMilliseconds < 10000) Pump(10);
                if (!task.IsCompleted) throw new TimeoutException("Synthetic automation UI timeout");
                task.GetAwaiter().GetResult();
                double completionMs = watch.Elapsed.TotalMilliseconds;
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                if (items.Items.Count != count) throw new InvalidOperationException("Synthetic rules UI did not build expected count");
                Add(new { Case = "automation-list-build", Rules = count, Steps = count * 8, CompletionMs = completionMs,
                    ToDispatcherIdleMs = watch.Elapsed.TotalMilliseconds, UiThreadAllocatedBytes = allocated < 0 ? -1 : Allocated() - allocated,
                    MaterializedRows = items.Items.Count });
            }
            typeof(AutoRuleStore).GetField("_cache", PrivateStatic)!.SetValue(null, new List<AutoRule>());
        }
        main.Close();
        Pump(500);
        var created = new List<WeakReference>();
        for (int i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            var panel = new QuickPanelModernWindow { ShowActivated = false, ShowInTaskbar = false };
            EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> diagnostic = (_, e) =>
            {
                if ((e.Exception.StackTrace ?? "").Contains("QuickPanelModernWindow")) Console.Error.WriteLine("Panel first-chance: " + e.Exception);
            };
            AppDomain.CurrentDomain.FirstChanceException += diagnostic;
            panel.Show();
            var load = (Task)typeof(QuickPanelModernWindow).GetMethod("LoadAsync", PrivateInstance)!.Invoke(panel, null)!;
            var timeout = Stopwatch.StartNew();
            while (!load.IsCompleted && timeout.ElapsedMilliseconds < 8000) Pump(10);
            if (!load.IsCompleted) throw new TimeoutException("Panel load timeout");
            load.GetAwaiter().GetResult();
            AppDomain.CurrentDomain.FirstChanceException -= diagnostic;
            bool contentReady = (bool)typeof(QuickPanelModernWindow).GetField("_contentReady", PrivateInstance)!.GetValue(panel)!;
            if (!contentReady) throw new InvalidOperationException("Panel content failed to load; exclude this run from performance comparisons");
            Add(new { Case = "quick-panel-load", Iteration = i, WallMs = watch.Elapsed.TotalMilliseconds,
                AppRows = ((System.Collections.IDictionary)typeof(QuickPanelModernWindow).GetField("_rows", PrivateInstance)!.GetValue(panel)!).Count,
                NamedApplications = AudioService.GetApps().Count(a => !string.IsNullOrWhiteSpace(a.ProcessName)),
                ContentReady = contentReady,
                IsClosed = typeof(QuickPanelModernWindow).GetField("_isClosed", PrivateInstance)!.GetValue(panel) });
            Pump(300);
            created.Add(new WeakReference(panel));
            panel.Close();
        }
        Pump(500);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Add(new { Case = "closed-panels-retention", Created = created.Count, AliveAfterForcedGc = created.Count(w => w.IsAlive),
            Note = "Last local can remain JIT-rooted; small sample is not a long-run leak test." });
        var process = Process.GetCurrentProcess();
        process.Refresh();
        double privateBefore = process.PrivateMemorySize64 / 1048576.0;
        double workingBefore = process.WorkingSet64 / 1048576.0;
        var gcWatch = Stopwatch.StartNew();
        typeof(App).GetMethod("GcNow", PrivateStatic)!.Invoke(null, null);
        double gcMs = gcWatch.Elapsed.TotalMilliseconds;
        gcWatch.Restart();
        typeof(App).GetMethod("TrimWorkingSet", PrivateStatic)!.Invoke(null, null);
        double trimMs = gcWatch.Elapsed.TotalMilliseconds;
        process.Refresh();
        Add(new { Case = "forced-gc-and-trim-after-close", GcMs = gcMs, TrimMs = trimMs,
            WorkingSetBeforeMiB = workingBefore, WorkingSetAfterMiB = process.WorkingSet64 / 1048576.0,
            PrivateBeforeMiB = privateBefore, PrivateAfterMiB = process.PrivateMemorySize64 / 1048576.0,
            Note = "Owned test process only. Already collected for retention, so this is a warmed lower-workload GC case." });
    }

    private static void MeasureIdle(string name, int milliseconds)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        double cpu = process.TotalProcessorTime.TotalMilliseconds;
        var watch = Stopwatch.StartNew();
        Pump(milliseconds);
        process.Refresh();
        double elapsed = watch.Elapsed.TotalMilliseconds;
        double used = process.TotalProcessorTime.TotalMilliseconds - cpu;
        Add(new { Case = name, WallMs = elapsed, CpuMs = used, MachineCpuPercent = 100 * used / elapsed / Environment.ProcessorCount });
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Measure(string name, int count, Action action)
    {
        action();
        var times = new List<double>(count);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        double cpu = process.TotalProcessorTime.TotalMilliseconds;
        long allocated = Allocated();
        int gen0 = GC.CollectionCount(0);
        for (int i = 0; i < count; i++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            times.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
        }
        process.Refresh();
        Add(new { Case = name, Operations = count, MeanMs = times.Average(), MedianMs = Percentile(times, 0.5),
            P95Ms = Percentile(times, 0.95), MaxMs = times.Max(), CpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu,
            AllocatedBytesPerOperation = allocated < 0 ? -1 : (Allocated() - allocated) / (double)count,
            Gen0Collections = GC.CollectionCount(0) - gen0 });
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * p) - 1)];
    }

    private static bool IntersectsWorkArea(Window window)
    {
        var work = SystemParameters.WorkArea;
        return window.Left < work.Right && window.Left + window.ActualWidth > work.Left
            && window.Top < work.Bottom && window.Top + window.ActualHeight > work.Top;
    }

    private static object OsdPlacementFacts(Window window)
    {
        var work = SystemParameters.WorkArea;
        return new { Left = window.Left, Top = window.Top, ActualWidth = window.ActualWidth, ActualHeight = window.ActualHeight,
            Opacity = window.Opacity, IsVisible = window.IsVisible, WorkAreaLeft = work.Left, WorkAreaTop = work.Top,
            WorkAreaWidth = work.Width, WorkAreaHeight = work.Height, IntersectsPrimaryWorkArea = IntersectsWorkArea(window) };
    }
    private static void Add(object row)
    {
        Results.Add(row);
        Console.WriteLine(JsonSerializer.Serialize(row));
        Save();
    }
    private static void Save() => File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(Results, JsonOptions));
}
