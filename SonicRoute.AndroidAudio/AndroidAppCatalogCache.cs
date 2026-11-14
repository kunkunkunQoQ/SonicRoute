using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using SonicRoute.Core;

namespace SonicRoute.AndroidAudio;

/// <summary>Per-phone catalogues, read only on demand. Refresh is explicit; no timer or discovery.</summary>
public sealed class AndroidAppCatalogCache(string filePath,
    Func<AndroidDevice, CancellationToken, Task<IReadOnlyList<AndroidApp>>> fetch)
{
    private readonly object _snapshotsGate = new();
    private readonly Dictionary<string, SemaphoreSlim> _loads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recent = new();
    private long _cachedBytes;
    private const long CacheBudget = 8 * 1024 * 1024;
    private sealed record Snapshot(string Serial, IReadOnlyList<AndroidApp> Apps, long Bytes = 0);
    private sealed record Saved(int Version, string Serial, AndroidApp[] Apps);
    public string FilePath { get; } = Path.GetFullPath(filePath);
    public bool TryGet(AndroidDevice device, out IReadOnlyList<AndroidApp> apps)
    {
        lock (_snapshotsGate)
        {
            bool found = _snapshots.TryGetValue(AndroidDeviceIdentity.Key(device), out var current);
            if (found) Touch(current!.Serial);
            apps = current?.Apps ?? Array.Empty<AndroidApp>(); return found;
        }
    }
    /// <summary>Reads cached app name from in-memory snapshots or saved catalog files without invoking ADB.</summary>
    public string? TryFindCachedName(string package, IEnumerable<AndroidDevice>? knownDevices = null)
    {
        if (string.IsNullOrWhiteSpace(package)) return null;
        lock (_snapshotsGate)
        {
            foreach (var snapshot in _snapshots.Values)
            {
                foreach (var app in snapshot.Apps)
                    if (string.Equals(app.Package, package, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(app.Name))
                        return app.Name;
            }
        }
        if (knownDevices == null) return null;
        foreach (var device in knownDevices)
        {
            if (TryGet(device, out _)) continue;
            var saved = ReadSaved(device);
            if (saved == null) continue;
            foreach (var app in saved)
                if (string.Equals(app.Package, package, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(app.Name))
                    return app.Name;
        }
        return null;
    }
    public async Task<IReadOnlyList<AndroidApp>> GetAsync(AndroidDevice device, bool refresh, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!refresh && TryGet(device, out var cached)) return cached;
        string key = AndroidDeviceIdentity.Key(device);
        SemaphoreSlim load; Snapshot? observed;
        lock (_snapshotsGate)
        {
            if (!_loads.TryGetValue(key, out load!)) _loads[key] = load = new(1);
            _snapshots.TryGetValue(key, out observed);
        }
        await load.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_snapshotsGate)
                if (_snapshots.TryGetValue(key, out var current) && (!refresh || !ReferenceEquals(current, observed)))
                { Touch(key); return current.Apps; } // concurrent refreshes of one phone share the result
            if (!refresh)
            {
                var saved = await Task.Run(() => ReadSaved(device), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (saved != null) { Publish(new(AndroidDeviceIdentity.Key(device), saved)); return saved; }
            }
            var result = await fetch(device, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var apps = result.ToArray(); Validate(apps);
            var snapshot = new Snapshot(key, Array.AsReadOnly(apps));
            await Task.Run(() => WriteSaved(snapshot), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Publish(snapshot);
            return snapshot.Apps;
        }
        finally { load.Release(); }
    }
    private void Publish(Snapshot snapshot)
    {
        lock (_snapshotsGate)
        {
            snapshot = snapshot with { Bytes = snapshot.Apps.Sum(a => (long)(a.IconPng?.Length ?? 0)
                + 2L * (a.Package.Length + a.Name.Length) + 128) };
            if (_snapshots.TryGetValue(snapshot.Serial, out var previous)) _cachedBytes -= previous.Bytes;
            _snapshots[snapshot.Serial] = snapshot;
            _cachedBytes += snapshot.Bytes; Touch(snapshot.Serial);
            // Keep the most recent catalogue even if it alone exceeds the budget.
            // Evicted phones retain their disk cache and are not queried again on reopen.
            while (_snapshots.Count > 1 && (_snapshots.Count > 4 || _cachedBytes > CacheBudget))
            {
                string oldest = _recent.First!.Value; _recent.RemoveFirst();
                _cachedBytes -= _snapshots[oldest].Bytes; _snapshots.Remove(oldest);
            }
        }
    }
    private void Touch(string key)
    {
        var node = _recent.Find(key);
        if (node == _recent.Last && node != null) return;
        if (node != null) { _recent.Remove(node); _recent.AddLast(node); }
        else _recent.AddLast(key);
    }
    private string DeviceFile(string key) => FilePath + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json";
    private IReadOnlyList<AndroidApp>? ReadSaved(AndroidDevice device)
    {
        try
        {
            string key = AndroidDeviceIdentity.Key(device), path = DeviceFile(key);
            if (!File.Exists(path)) path = FilePath; // lazy compatibility with the old one-phone cache
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            using var file = File.OpenRead(path);
            var saved = JsonSerializer.Deserialize<Saved>(file);
            if (saved?.Apps == null || !(saved.Version == 2 && saved.Serial == key || saved.Version == 1
                && AndroidDeviceIdentity.Matches(device, new(saved.Serial, "", "device", saved.Serial.Contains(':') || saved.Serial.Contains("_adb-tls"))))) return null;
            Validate(saved.Apps);
            return Array.AsReadOnly(saved.Apps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Trace.WriteLine("Read phone app cache: " + ex.Message); return null; }
    }
    private void WriteSaved(Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.Write(DeviceFile(snapshot.Serial), stream => JsonSerializer.Serialize(stream,
                new Saved(2, snapshot.Serial, snapshot.Apps.ToArray())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.WriteLine("Write phone app cache: " + ex.Message); }
    }
    private static void Validate(IReadOnlyList<AndroidApp> apps)
    {
        if (apps.Count > 1000) throw new ArgumentException("Phone app cache is too large.");
        var packages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            if (app == null || !packages.Add(app.Package)) throw new ArgumentException("Invalid cached app.");
            AdbClient.ValidatePackage(app.Package);
            if (app.Uid <= 0 || app.Name == null || app.Name.Length > 1024 || app.IconPng?.Length > 131072)
                throw new ArgumentException("Invalid cached app metadata.");
        }
    }
}
