using System.Buffers;
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
    private readonly Dictionary<string, Dictionary<string, string>> _nameIndexes = new(StringComparer.Ordinal);
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
    /// <summary>Clears cached in-memory snapshots and PNG bytes while keeping disk caches and lightweight name lookups.</summary>
    public void TrimForIdle()
    {
        lock (_snapshotsGate)
        {
            _snapshots.Clear();
            _recent.Clear();
            _cachedBytes = 0;
        }
    }
    /// <summary>Reads cached app name from in-memory snapshots, lightweight name indexes, or saved catalog files without invoking ADB or decoding PNGs.</summary>
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
            foreach (var index in _nameIndexes.Values)
            {
                if (index.TryGetValue(package, out var cachedName) && !string.IsNullOrWhiteSpace(cachedName))
                    return cachedName;
            }
        }
        if (knownDevices == null) return null;
        foreach (var device in knownDevices)
        {
            string key = AndroidDeviceIdentity.Key(device);
            lock (_snapshotsGate)
            {
                if (_snapshots.ContainsKey(key) || _nameIndexes.ContainsKey(key))
                    continue;
            }
            var savedNames = ReadSavedNames(device);
            if (savedNames == null) continue;
            lock (_snapshotsGate)
            {
                if (!_nameIndexes.ContainsKey(key))
                    _nameIndexes[key] = savedNames;
            }
            if (savedNames.TryGetValue(package, out var found) && !string.IsNullOrWhiteSpace(found))
                return found;
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
        var nameIndex = BuildNameIndex(snapshot.Apps);
        lock (_snapshotsGate)
        {
            snapshot = snapshot with { Bytes = snapshot.Apps.Sum(a => (long)(a.IconPng?.Length ?? 0)
                + 2L * (a.Package.Length + a.Name.Length) + 128) };
            if (_snapshots.TryGetValue(snapshot.Serial, out var previous)) _cachedBytes -= previous.Bytes;
            _snapshots[snapshot.Serial] = snapshot;
            _nameIndexes[snapshot.Serial] = nameIndex;
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
    private static Dictionary<string, string> BuildNameIndex(IReadOnlyList<AndroidApp> apps)
    {
        var names = new Dictionary<string, string>(apps.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            if (!string.IsNullOrWhiteSpace(app.Package) && !string.IsNullOrWhiteSpace(app.Name))
                names[app.Package] = app.Name;
        }
        return names;
    }
    private void Touch(string key)
    {
        var node = _recent.Find(key);
        if (node == _recent.Last && node != null) return;
        if (node != null) { _recent.Remove(node); _recent.AddLast(node); }
        else _recent.AddLast(key);
    }
    private string DeviceFile(string key) => FilePath + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json";
    private Dictionary<string, string>? ReadSavedNames(AndroidDevice device)
    {
        try
        {
            string key = AndroidDeviceIdentity.Key(device), path = DeviceFile(key);
            if (!File.Exists(path)) path = FilePath; // lazy compatibility with the old one-phone cache
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            if (file.Length > 16 * 1024 * 1024) return null;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                int buffered = 0;
                bool isFinalBlock = false;
                while (buffered < 3 && !isFinalBlock)
                {
                    int read = file.Read(buffer, buffered, buffer.Length - buffered);
                    if (read == 0) isFinalBlock = true;
                    else buffered += read;
                }
                if (buffered >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                {
                    buffered -= 3;
                    if (buffered > 0)
                        Buffer.BlockCopy(buffer, 3, buffer, 0, buffered);
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), isFinalBlock, new JsonReaderState());
                if (!NextToken(file, ref buffer, ref buffered, ref isFinalBlock, ref reader))
                    return null;
                if (reader.TokenType == JsonTokenType.Null)
                    return null;
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("Expected saved app catalog object.");

                int version = 0;
                string? serial = null;
                bool hasApps = false;
                var packages = new HashSet<string>(StringComparer.Ordinal);
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                while (NextToken(file, ref buffer, ref buffered, ref isFinalBlock, ref reader))
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                        break;
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("Expected saved app catalog property.");

                    bool isVersion = reader.ValueTextEquals("Version"u8);
                    bool isSerial = !isVersion && reader.ValueTextEquals("Serial"u8);
                    bool isApps = !isVersion && !isSerial && reader.ValueTextEquals("Apps"u8);

                    if (!NextToken(file, ref buffer, ref buffered, ref isFinalBlock, ref reader))
                        throw new JsonException("Unexpected end of saved app catalog.");

                    if (isVersion)
                    {
                        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out version))
                            throw new JsonException("Invalid catalog Version.");
                    }
                    else if (isSerial)
                    {
                        serial = reader.TokenType == JsonTokenType.Null ? null
                            : reader.TokenType == JsonTokenType.String ? reader.GetString()
                            : throw new JsonException("Invalid catalog Serial.");
                    }
                    else if (isApps)
                    {
                        if (reader.TokenType == JsonTokenType.Null)
                        {
                            hasApps = false;
                            continue;
                        }
                        if (reader.TokenType != JsonTokenType.StartArray)
                            throw new JsonException("Invalid catalog Apps array.");
                        hasApps = true;
                        int appCount = 0;
                        while (NextToken(file, ref buffer, ref buffered, ref isFinalBlock, ref reader))
                        {
                            if (reader.TokenType == JsonTokenType.EndArray)
                                break;
                            if (++appCount > 1000)
                                throw new ArgumentException("Phone app cache is too large.");
                            if (reader.TokenType != JsonTokenType.StartObject)
                                throw new ArgumentException("Invalid cached app.");

                            string? pkg = null;
                            string? name = null;
                            int uid = 0;
                            while (NextToken(file, ref buffer, ref buffered, ref isFinalBlock, ref reader))
                            {
                                if (reader.TokenType == JsonTokenType.EndObject)
                                    break;
                                if (reader.TokenType != JsonTokenType.PropertyName)
                                    throw new JsonException("Expected cached app property.");

                                bool isPkg = reader.ValueTextEquals("Package"u8);
                                bool isName = !isPkg && reader.ValueTextEquals("Name"u8);
                                bool isUid = !isPkg && !isName && reader.ValueTextEquals("Uid"u8);

                                if (!NextToken(file, ref buffer, ref buffered, ref isFinalBlock, ref reader))
                                    throw new JsonException("Unexpected end of cached app.");

                                if (isPkg)
                                    pkg = reader.TokenType == JsonTokenType.Null ? null
                                        : reader.TokenType == JsonTokenType.String ? reader.GetString()
                                        : throw new JsonException("Invalid cached app Package.");
                                else if (isName)
                                    name = reader.TokenType == JsonTokenType.Null ? null
                                        : reader.TokenType == JsonTokenType.String ? reader.GetString()
                                        : throw new JsonException("Invalid cached app Name.");
                                else if (isUid)
                                {
                                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out uid))
                                        throw new JsonException("Invalid cached app Uid.");
                                }
                                else
                                {
                                    // Skips IconPng base64 string without allocating/decoding byte[] and ignores other fields.
                                    SkipValue(file, ref buffer, ref buffered, ref isFinalBlock, ref reader);
                                }
                            }

                            if (pkg == null || !packages.Add(pkg))
                                throw new ArgumentException("Invalid cached app.");
                            AdbClient.ValidatePackage(pkg);
                            if (uid <= 0 || name == null || name.Length > 1024)
                                throw new ArgumentException("Invalid cached app metadata.");
                            if (!string.IsNullOrWhiteSpace(name))
                                names[pkg] = name;
                        }
                    }
                    else
                    {
                        SkipValue(file, ref buffer, ref buffered, ref isFinalBlock, ref reader);
                    }
                }

                if (!hasApps || !(version == 2 && string.Equals(serial, key, StringComparison.Ordinal)
                    || version == 1 && !string.IsNullOrEmpty(serial)
                    && AndroidDeviceIdentity.Matches(device, new(serial, "", "device", serial.Contains(':') || serial.Contains("_adb-tls")))))
                    return null;

                return names;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Trace.WriteLine("Read phone app cache: " + ex.Message); return null; }
    }
    private static bool NextToken(
        FileStream file,
        ref byte[] buffer,
        ref int buffered,
        ref bool isFinalBlock,
        ref Utf8JsonReader reader)
    {
        while (!reader.Read())
        {
            if (isFinalBlock)
                return false;

            int consumed = (int)reader.BytesConsumed;
            int remaining = buffered - consumed;
            if (remaining > 0 && consumed > 0)
                Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
            buffered = remaining;

            if (buffered == buffer.Length)
            {
                if (buffer.Length >= 16 * 1024 * 1024)
                    throw new JsonException("Phone app cache token exceeds maximum size.");
                int nextSize = Math.Min(16 * 1024 * 1024, buffer.Length * 2);
                byte[] grown = ArrayPool<byte>.Shared.Rent(nextSize);
                if (buffered > 0)
                    Buffer.BlockCopy(buffer, 0, grown, 0, buffered);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = grown;
            }

            int read = file.Read(buffer, buffered, buffer.Length - buffered);
            if (read == 0)
                isFinalBlock = true;
            else
                buffered += read;

            JsonReaderState state = reader.CurrentState;
            reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), isFinalBlock, state);
        }
        return true;
    }
    private static void SkipValue(
        FileStream file,
        ref byte[] buffer,
        ref int buffered,
        ref bool isFinalBlock,
        ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
            return;
        int depth = 1;
        while (depth > 0)
        {
            if (!NextToken(file, ref buffer, ref buffered, ref isFinalBlock, ref reader))
                throw new JsonException("Unexpected end of phone app cache.");
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                depth++;
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                depth--;
        }
    }
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
