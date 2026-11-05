using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckCatalogCacheAsync(string directory, AndroidDevice device, AndroidApp app)
    {
        string path = Path.Combine(directory, "phone-app-catalog.json");
        int fetches = 0;
        var cache = new AndroidAppCatalogCache(path, async (_, token) =>
        {
            Interlocked.Increment(ref fetches); await Task.Yield(); token.ThrowIfCancellationRequested();
            return new[] { app with { Name = "version-" + fetches } };
        });
        Check(!File.Exists(path) && fetches == 0, "catalogue cache construction has no disk or device work");
        var reads = await Task.WhenAll(Enumerable.Range(0,8).Select(_ => cache.GetAsync(device, false, default)));
        string deviceFile = Directory.GetFiles(directory, "phone-app-catalog.json.*.json").Single();
        Check(fetches == 1 && reads.All(a => ReferenceEquals(a, reads[0])) && File.Exists(deviceFile),
            "concurrent catalogue opens share a single fetch and immutable in-memory snapshot");
        var restored = new AndroidAppCatalogCache(path, (_, _) => throw new Exception("Disk cache should satisfy reopen"));
        var disk = await restored.GetAsync(device, false, default);
        Check(disk[0].Name == "version-1" && disk[0].IconPng!.SequenceEqual(app.IconPng!),
            "application restart restores catalogue metadata and PNG bytes without a device request");
        var refreshed = await cache.GetAsync(device, true, default);
        Check(fetches == 2 && refreshed[0].Name == "version-2" && !ReferenceEquals(refreshed, reads[0]),
            "explicit Refresh replaces the saved catalogue while ordinary reopen reuses it");
        int otherFetches = 0;
        var other = new AndroidAppCatalogCache(path, (_, _) => { ++otherFetches; return Task.FromResult<IReadOnlyList<AndroidApp>>(new[] { app }); });
        await other.GetAsync(device with { Serial = "different-phone" }, false, default);
        Check(otherFetches == 1, "catalogue saved for another phone is never shown as this device's catalogue");
        string before = File.ReadAllText(deviceFile);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledCache = new AndroidAppCatalogCache(path, async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return new[] { app }; });
        using var cancellation = new CancellationTokenSource();
        var pending = cancelledCache.GetAsync(device, true, cancellation.Token);
        await entered.Task; cancellation.Cancel();
        bool cancelled = false; try { await pending; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && File.ReadAllText(deviceFile) == before && !cancelledCache.TryGet(device, out _),
            "cancelling an in-flight catalogue fetch preserves the saved catalogue and publishes no partial data");
        var invalid = new AndroidAppCatalogCache(path, (_, _) => Task.FromResult<IReadOnlyList<AndroidApp>>(new[] { app, app }));
        bool rejected = false; try { await invalid.GetAsync(device, true, default); } catch (ArgumentException) { rejected = true; }
        Check(rejected && File.ReadAllText(deviceFile) == before, "invalid refreshed catalogue cannot overwrite the saved file");
        File.WriteAllText(deviceFile, "{broken-json");
        int recovered = 0;
        var corrupt = new AndroidAppCatalogCache(path, (_, _) => { ++recovered; return Task.FromResult<IReadOnlyList<AndroidApp>>(new[] { app }); });
        await corrupt.GetAsync(device, false, default);
        Check(recovered == 1 && (await corrupt.GetAsync(device, false, default)).Count == 1,
            "corrupt cache recovers on demand once and then reuses the replacement snapshot");
    }
}
