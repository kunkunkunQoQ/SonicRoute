using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task PhoneDemandAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var bundle = new AndroidRuntimeBundle(Path.Combine(output, "runtime"));
        var adb = new AdbClient(bundle);
        Check(!Directory.Exists(bundle.DirectoryPath), "unused phone services create no tool cache");
        Check((await adb.RunAsync(default, "version")).Contains("Android Debug Bridge")
            && !Directory.Exists(bundle.DirectoryPath),
            "first in-process ADB version request extracts no external executables or DLLs");
        var runtime = new VirtualDisplayRuntime(adb);
        runtime.ValidateControl();
        Check(Directory.GetFiles(bundle.DirectoryPath, "*", SearchOption.AllDirectories).Length == 1
            && File.Exists(runtime.ServerFile), "unpair control prepares only sonicroute-catalog.jar, without ADB or third-party DLLs");
        runtime.Validate();
        Check(Directory.GetFiles(bundle.DirectoryPath, "*", SearchOption.AllDirectories).Length == 1
            && !File.Exists(Path.Combine(bundle.DirectoryPath, "adb.exe")),
            "video validation reuses sonicroute-catalog.jar without ADB/FFmpeg/SDL2/scrcpy binaries");
        adb.ValidateCatalog();
        Check(Directory.GetFiles(bundle.DirectoryPath, "*", SearchOption.AllDirectories).Length == 1,
            "audio/catalogue validation keeps only the unified helper jar");

        var device = new AndroidDevice("fixture-one", "Fixture", "device", true);
        var app = new AndroidApp("cn.test.one", "One", 1001, "media", [1, 2, 3]);
        string cachePath = Path.Combine(output, "catalogue.json");
        int fetches = 0;
        var refreshGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new AndroidAppCatalogCache(cachePath, async (_, token) =>
        {
            int version = Interlocked.Increment(ref fetches);
            if (version > 1) { entered.TrySetResult(); await refreshGate.Task.WaitAsync(token); }
            return new[] { app with { Name = "Version " + version } };
        });
        var first = await cache.GetAsync(device, false, default);
        var refreshed = cache.GetAsync(device, true, default);
        await entered.Task;
        var concurrent = Enumerable.Range(0, 6).Select(_ => cache.GetAsync(device, true, default)).Append(refreshed).ToArray();
        refreshGate.SetResult();
        var results = await Task.WhenAll(concurrent);
        Check(fetches == 2 && results.All(a => ReferenceEquals(a, results[0])) && results[0][0].Name == "Version 2",
            "overlapping refreshes share one refreshed catalogue and immutable snapshot");
        string file = Directory.GetFiles(output, "catalogue.json.*.json").Single();
        var writeTime = File.GetLastWriteTimeUtc(file);
        bool stable = true;
        for (int i = 0; i < 100; i++) stable &= ReferenceEquals(await cache.GetAsync(device, false, default), results[0]);
        Check(stable && fetches == 2 && File.GetLastWriteTimeUtc(file) == writeTime,
            "warm opens cause no fetch or persistence rewrite");

        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var independent = new AndroidAppCatalogCache(Path.Combine(output, "independent.json"), async (target, token) =>
        {
            if (target.Serial == device.Serial) { started.SetResult(); await blocked.Task.WaitAsync(token); }
            return new[] { app };
        });
        using (var cancellation = new CancellationTokenSource())
        {
            var slow = independent.GetAsync(device, false, cancellation.Token);
            await started.Task;
            var other = await independent.GetAsync(device with { Serial = "fixture-two" }, false, default).WaitAsync(TimeSpan.FromSeconds(2));
            Check(other.Count == 1 && !slow.IsCompleted, "one slow phone cannot block another phone's catalogue");
            cancellation.Cancel();
            bool cancelled = false; try { await slow; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !independent.TryGet(device, out _), "cancelled catalogue work publishes no partial snapshot");
        }

        int memoryFetches = 0;
        var largeApps = Enumerable.Range(0, 40).Select(i => app with { Package = "cn.test.memory" + i, IconPng = new byte[65536] }).ToArray();
        var bounded = new AndroidAppCatalogCache(Path.Combine(output, "bounded.json"), (_, _) =>
        { memoryFetches++; return Task.FromResult<IReadOnlyList<AndroidApp>>(largeApps); });
        for (int i = 0; i < 8; i++) await bounded.GetAsync(device with { Serial = "fixture-memory-" + i }, false, default);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var snapshots = (System.Collections.IDictionary)typeof(AndroidAppCatalogCache).GetField("_snapshots", hidden)!.GetValue(bounded)!;
        long retained = (long)typeof(AndroidAppCatalogCache).GetField("_cachedBytes", hidden)!.GetValue(bounded)!;
        Check(snapshots.Count <= 4 && retained <= 8 * 1024 * 1024, "catalogue metadata and PNGs have a bounded LRU memory budget");
        Check((await bounded.GetAsync(device with { Serial = "fixture-memory-0" }, false, default)).Count == 40 && memoryFetches == 8,
            "evicted phone catalogues reopen from disk without another device query");

        string phonePath = Path.Combine(output, "phone.json");
        var store = new AndroidPhoneStore(phonePath);
        store.RememberDevice(device); store.Rename("小米测试"); store.Retain(app); store.SetSmartVolume(app.Package, false);
        await store.FlushAsync();
        var restored = new AndroidPhoneStore(phonePath);
        Check(restored.Profile.Name == "小米测试" && restored.Profile.QuickPanelApps.Count == 1 && !restored.IsSmartVolumeEnabled(device, app.Package)
            && restored.Profile.RetainedApps[0].IconPng!.SequenceEqual(app.IconPng!),
            "streamed phone preferences preserve Unicode names, icons and hidden membership");
        var settings = new AndroidAudioSettingsStore(Path.Combine(output, "audio.json"));
        settings.Remember(device, app.Package, 37, "fixture-output"); await settings.FlushAsync();
        Check(new AndroidAudioSettingsStore(settings.FilePath).Get(device, app.Package) == new AndroidAppAudioSettings(37, "fixture-output"),
            "streamed audio preferences preserve per-device volume and output");

        string atomicPath = Path.Combine(output, "atomic.txt"); File.WriteAllText(atomicPath, "before");
        var atomic = typeof(AndroidAppCatalogCache).Assembly.GetType("SonicRoute.Core.AtomicFile")!;
        bool failed = false;
        try
        {
            atomic.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                new object[] { atomicPath, (Action<Stream>)(stream => { stream.WriteByte(1); throw new IOException("fixture interruption"); }) });
        }
        catch (TargetInvocationException error) when (error.InnerException is IOException) { failed = true; }
        Check(failed && File.ReadAllText(atomicPath) == "before" && !Directory.EnumerateFiles(output)
            .Any(p => Path.GetFileName(p).StartsWith("atomic.txt.", StringComparison.Ordinal)),
            "interrupted streamed persistence retains the original file and removes its temporary file");

        foreach (int length in new[] { 2, 14, 16, 20, 1920 })
        {
            var samples = Enumerable.Range(0, length).Select(i => (float)Math.Sin(i * .17) * 1.5f).ToArray();
            samples[0] = .0001f; samples[1] = -.0001f;
            if (length >= 16) { samples[5] = float.NaN; samples[7] = float.PositiveInfinity; samples[9] = float.NegativeInfinity; }
            var pcm = new AndroidPcmSource { Volume = .35f }; pcm.Write(samples);
            float peak = 0; int nonSilent = 0;
            foreach (float sample in samples) { peak = Math.Max(peak, Math.Abs(sample)); if (Math.Abs(sample) > .0001f) nonSilent++; }
            Check(pcm.NonSilentSamplesWritten == nonSilent && (float.IsNaN(peak) ? float.IsNaN(pcm.LastPeak) : pcm.LastPeak == peak),
                "SIMD diagnostics match scalar thresholds, peak and NaN semantics for " + length + " samples");
        }
        CheckPcmWrapAndGain();
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { Passed = true, Assertions = _checks, RetainedCatalogues = snapshots.Count, EstimatedCatalogueBytes = retained,
            DeviceAccess = false, AdbCommand = "version only", Source = "phone-demand" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} phone demand assertions; no phone connected or controlled");
    }
}
