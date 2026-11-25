using System.Diagnostics;
using System.Text.Json;
using SonicRoute.Core;

namespace SonicRoute.AndroidAudio;

public sealed record AndroidVirtualDisplaySize(int Width, int Height)
{
    public bool IsValid() => Width is >= 240 and <= 8192 && Height is >= 240 and <= 8192;
}

public sealed record AndroidWindowSize(int Width, int Height)
{
    public bool IsValid() => Width is >= 96 and <= 16384 && Height is >= 64 and <= 16384;
}

public sealed record AndroidAppAudioSettings(int Volume = 100, string? OutputId = null,
    AndroidVirtualDisplaySize? DisplaySize = null, AndroidWindowSize? WindowSize = null)
{
    // Only an explicitly selected multi-output source is eligible for automatic capture.
    public string?[]? ResumeOutputs { get; init; }
}

internal sealed record AndroidSavedSourceRoute(string DeviceKey, string Package, string?[] Outputs);

/// <summary>Phone application and streaming preferences, independent of the desktop configuration.</summary>
public sealed class AndroidAudioSettingsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AndroidAppAudioSettings> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AndroidStreamSettings> _streams = new(StringComparer.Ordinal);
    private Task _save = Task.CompletedTask;
    private bool _writing;
    private long _revision;
    private bool _loadedSafely = true;
    public string FilePath { get; }

    public AndroidAudioSettingsStore(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        string backup = FilePath + ".bak";
        bool hasPrimary = File.Exists(FilePath), hasBackup = File.Exists(backup);
        if (!hasPrimary && !hasBackup) return;
        _loadedSafely = false;
        if (hasPrimary && TryLoadFile(FilePath))
        {
            _loadedSafely = true;
            return;
        }
        _apps.Clear();
        _streams.Clear();
        if (hasBackup && TryLoadFile(backup))
            _loadedSafely = true;
    }

    private bool TryLoadFile(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var saved = JsonSerializer.Deserialize(file, AndroidSettingsJsonContext.Default.SettingsFile);
            if (saved?.Version is not (1 or 2 or 3) || saved.Apps == null) return false;
            foreach (var entry in saved.Apps)
            {
                ValidateSourceKey(entry.Key[(entry.Key.LastIndexOf('\n') + 1)..]);
                if (entry.Value != null) _apps[entry.Key] = entry.Value with
                {
                    Volume = Math.Clamp(entry.Value.Volume, 0, 100),
                    DisplaySize = entry.Value.DisplaySize?.IsValid() == true ? entry.Value.DisplaySize : null,
                    WindowSize = entry.Value.WindowSize?.IsValid() == true ? entry.Value.WindowSize : null
                };
            }
            if (saved.Streams != null)
                foreach (var entry in saved.Streams)
                    if (!string.IsNullOrEmpty(entry.Key) && entry.Value != null) _streams[entry.Key] = entry.Value.Normalize();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Trace.WriteLine("Phone audio settings: " + ex.Message); return false; }
    }

    public AndroidAppAudioSettings Get(string package)
    { lock (_gate) return _apps.TryGetValue(package, out var value) ? value : new(); }

    public AndroidStreamSettings GetStreaming(AndroidDevice device)
    {
        lock (_gate) return _streams.GetValueOrDefault(AndroidDeviceIdentity.Key(device)) ?? new();
    }

    public void RememberStreaming(AndroidDevice device, AndroidStreamSettings settings)
    {
        var next = settings.Normalize();
        lock (_gate)
        {
            string key = AndroidDeviceIdentity.Key(device);
            if (GetStreaming(device) == next) return;
            _streams[key] = next;
            ++_revision;
            if (!_writing) { _writing = true; _save = Task.Run(SaveAsync); }
        }
    }

    public AndroidAppAudioSettings Get(AndroidDevice device, string package)
    {
        lock (_gate) return _apps.TryGetValue(AndroidDeviceIdentity.Key(device) + "\n" + package, out var value)
            ? value : _apps.GetValueOrDefault(package) ?? new(); // old single-phone preferences remain a migration fallback
    }

    // The same currently connected wireless transport has just supplied its missing
    // identity. Copy preferences to the verified key without overwriting existing
    // settings or turning endpoint-only selections into automatic resume intents.
    internal void PromoteEndpointPreferences(AndroidDevice previous, AndroidDevice verified)
    {
        if (!previous.IsWifi || !verified.IsWifi || verified.State != "device" || previous.State != "device"
            || previous.Serial != verified.Serial || previous.TransportId == null || previous.TransportId != verified.TransportId
            || AndroidDeviceIdentity.StableKey(previous) != null || AndroidDeviceIdentity.StableKey(verified) is not { } stable) return;
        string oldKey = AndroidDeviceIdentity.Key(previous);
        lock (_gate)
        {
            bool changed = false;
            if (_streams.TryGetValue(oldKey, out var stream) && _streams.TryAdd(stable, stream)) changed = true;
            foreach (var (key, value) in _apps.Where(e => e.Key.StartsWith(oldKey + "\n", StringComparison.Ordinal)).ToArray())
            {
                string? output = value.OutputId;
                if (AndroidOutputIdentity.TryParseDeviceId(output, out string phone, out string sink) && phone == oldKey)
                    output = AndroidOutputIdentity.FormatDeviceId(stable, sink);
                if (_apps.TryAdd(stable + key[oldKey.Length..], value with { OutputId = output, ResumeOutputs = null })) changed = true;
            }
            if (!changed) return;
            ++_revision;
            if (!_writing) { _writing = true; _save = Task.Run(SaveAsync); }
        }
    }

    // Explicitly verified rebind. Keep the original keys (and a durable recovery
    // copy), so failure in either configuration file never destroys preferences.
    internal Action? MigrateConnectionPreferences(AndroidDevice previous, AndroidDevice verified)
    {
        string oldKey = AndroidDeviceIdentity.Key(previous), newKey = AndroidDeviceIdentity.Key(verified);
        if (oldKey == newKey) return null;
        lock (_gate)
        {
            if (!_loadedSafely) throw new IOException("Phone audio preferences could not be loaded; the original file has been preserved.");
            var apps = new Dictionary<string, AndroidAppAudioSettings>(_apps, StringComparer.Ordinal);
            var streams = new Dictionary<string, AndroidStreamSettings>(_streams, StringComparer.Ordinal);
            bool changed = false;
            if (streams.TryGetValue(oldKey, out var stream) && !Equals(streams.GetValueOrDefault(newKey), stream))
            { streams[newKey] = stream; changed = true; }
            foreach (var (key, value) in _apps.Where(e => e.Key.StartsWith(oldKey + "\n", StringComparison.Ordinal)))
            {
                string nextKey = newKey + key[oldKey.Length..];
                var archived = apps.GetValueOrDefault(nextKey);
                string? output = value.OutputId;
                if (AndroidOutputIdentity.TryParseDeviceId(output, out string phone, out string sink) && phone == oldKey)
                    output = AndroidOutputIdentity.FormatDeviceId(newKey, sink);
                var migrated = value with { OutputId = output, DisplaySize = value.DisplaySize ?? archived?.DisplaySize,
                    WindowSize = value.WindowSize ?? archived?.WindowSize, ResumeOutputs = null };
                if (archived == migrated) continue;
                apps[nextKey] = migrated; changed = true;
            }
            if (!changed) return null;
            var beforeApps = new Dictionary<string, AndroidAppAudioSettings>(_apps, StringComparer.Ordinal);
            var beforeStreams = new Dictionary<string, AndroidStreamSettings>(_streams, StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string backup = FilePath + ".pre-rebind-" + Guid.NewGuid().ToString("N") + ".bak";
            AtomicFile.Write(backup, s => JsonSerializer.Serialize(s, new SettingsFile(3, _apps, _streams), AndroidSettingsJsonContext.Default.SettingsFile));
            AtomicFile.Write(FilePath, s => JsonSerializer.Serialize(s, new SettingsFile(3, apps, streams), AndroidSettingsJsonContext.Default.SettingsFile));
            foreach (var entry in apps) _apps[entry.Key] = entry.Value;
            foreach (var entry in streams) _streams[entry.Key] = entry.Value;
            ++_revision;
            return () =>
            {
                lock (_gate)
                {
                    // Restore only keys actually changed by this transaction, and
                    // do not overwrite a newer UI preference written meanwhile.
                    var restoredApps = new Dictionary<string, AndroidAppAudioSettings>(_apps, StringComparer.Ordinal);
                    var restoredStreams = new Dictionary<string, AndroidStreamSettings>(_streams, StringComparer.Ordinal);
                    foreach (var (key, value) in apps.Where(e => e.Key.StartsWith(newKey + "\n", StringComparison.Ordinal)))
                        if (beforeApps.GetValueOrDefault(key) != value && restoredApps.GetValueOrDefault(key) == value)
                        {
                            if (beforeApps.TryGetValue(key, out var original)) restoredApps[key] = original;
                            else restoredApps.Remove(key);
                        }
                    if (streams.TryGetValue(newKey, out var migrated) && beforeStreams.GetValueOrDefault(newKey) != migrated
                        && restoredStreams.GetValueOrDefault(newKey) == migrated)
                    {
                        if (beforeStreams.TryGetValue(newKey, out var original)) restoredStreams[newKey] = original;
                        else restoredStreams.Remove(newKey);
                    }
                    AtomicFile.Write(FilePath, s => JsonSerializer.Serialize(s, new SettingsFile(3, restoredApps, restoredStreams), AndroidSettingsJsonContext.Default.SettingsFile));
                    _apps.Clear(); foreach (var entry in restoredApps) _apps[entry.Key] = entry.Value;
                    _streams.Clear(); foreach (var entry in restoredStreams) _streams[entry.Key] = entry.Value;
                    ++_revision;
                }
            };
        }
    }
    public void Remember(AndroidDevice device, string package, int volume, string? outputId)
    {
        ValidateSourceKey(package);
        RememberCore(AndroidDeviceIdentity.Key(device) + "\n" + package, volume, outputId);
    }

    public void Remember(string package, int volume, string? outputId)
    {
        ValidateSourceKey(package);
        RememberCore(package, volume, outputId);
    }
    private void RememberCore(string key, int volume, string? outputId)
    {
        volume = Math.Clamp(volume, 0, 100);
        lock (_gate)
        {
            if (_apps.TryGetValue(key, out var previous) && previous.Volume == volume && previous.OutputId == outputId) return;
            _apps[key] = (previous ?? new()) with { Volume = volume, OutputId = outputId }; ++_revision;
            if (!_writing) { _writing = true; _save = Task.Run(SaveAsync); }
        }
    }

    internal event Action? SavedSourcesChanged;

    internal AndroidSavedSourceRoute[] GetSavedSources()
    {
        lock (_gate)
        {
            var routes = new List<AndroidSavedSourceRoute>();
            foreach (var (key, settings) in _apps)
            {
                int separator = key.IndexOf('\n');
                if (separator <= 0 || !key.StartsWith("device:", StringComparison.Ordinal)
                    || settings.ResumeOutputs is not { Length: > 1 and <= 128 } outputs) continue;
                routes.Add(new(key[..separator], key[(separator + 1)..], outputs.ToArray()));
            }
            return routes.ToArray();
        }
    }

    internal void RememberSourceOutputs(AndroidDevice device, string package, string?[]? outputs)
    {
        if (AndroidDeviceIdentity.StableKey(device) is not { } phone) return;
        string key = phone + "\n" + package;
        lock (_gate)
        {
            _apps.TryGetValue(key, out var previous);
            var next = outputs?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (next is not { Length: > 1 and <= 128 } || next.Any(id => AndroidOutputIdentity.IsPhoneOutput(id)
                && (!AndroidOutputIdentity.TryParseDeviceId(id, out string keyPart, out _) || !keyPart.StartsWith("device:", StringComparison.Ordinal))))
                next = null;
            if (previous?.ResumeOutputs is { } old
                ? next != null && old.SequenceEqual(next, StringComparer.OrdinalIgnoreCase) : next == null) return;
            _apps[key] = (previous ?? Get(device, package)) with { ResumeOutputs = next };
            ++_revision;
            if (!_writing) { _writing = true; _save = Task.Run(SaveAsync); }
        }
        SavedSourcesChanged?.Invoke();
    }

    internal void ForgetSourceRoutes(AndroidDevice device, Func<string, bool>? matches = null)
        => ForgetRoutes(device, matches, forgetDestination: false);

    internal void ForgetPhoneRoutes(AndroidDevice device)
        => ForgetRoutes(device, null, forgetDestination: true);

    private void ForgetRoutes(AndroidDevice device, Func<string, bool>? matches, bool forgetDestination)
    {
        if (AndroidDeviceIdentity.StableKey(device) is not { } stable) return;
        string prefix = stable + "\n";
        lock (_gate)
        {
            bool changed = false;
            foreach (var (key, settings) in _apps)
            {
                if (settings.ResumeOutputs is not { } outputs) continue;
                string?[]? keep = null;
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || matches != null && !matches(key[prefix.Length..]))
                {
                    if (!forgetDestination) continue;
                    keep = outputs.Where(id => !AndroidOutputIdentity.TryParseDeviceId(id, out string target, out _) || target != stable).ToArray();
                    if (keep.Length == outputs.Length) continue;
                }
                _apps[key] = settings with { ResumeOutputs = keep is { Length: > 1 } ? keep : null };
                changed = true;
            }
            if (!changed) return;
            ++_revision;
            if (!_writing) { _writing = true; _save = Task.Run(SaveAsync); }
        }
        SavedSourcesChanged?.Invoke();
    }

    public void RememberDisplay(AndroidDevice device, string package, AndroidVirtualDisplaySize size)
    {
        if (!size.IsValid()) throw new ArgumentOutOfRangeException(nameof(size));
        RememberLayout(device, package, size, null);
    }
    public void RememberWindow(AndroidDevice device, string package, AndroidWindowSize size)
    {
        if (!size.IsValid()) throw new ArgumentOutOfRangeException(nameof(size));
        RememberLayout(device, package, null, size);
    }
    private void RememberLayout(AndroidDevice device, string package, AndroidVirtualDisplaySize? display, AndroidWindowSize? window)
    {
        ValidateSourceKey(package);
        lock (_gate)
        {
            string key = AndroidDeviceIdentity.Key(device) + "\n" + package;
            var previous = Get(device, package);
            var next = previous with { DisplaySize = display ?? previous.DisplaySize, WindowSize = window ?? previous.WindowSize };
            if (next == previous) return;
            _apps[key] = next; ++_revision;
            if (!_writing) { _writing = true; _save = Task.Run(SaveAsync); }
        }
    }

    // A finite write coalescer, started only by a changed preference. No idle timer.
    private static void ValidateSourceKey(string package)
    {
        if (package != AndroidApp.DeviceAudioPackage) AdbClient.ValidatePackage(package);
    }

    private async Task SaveAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(200).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_loadedSafely) { _writing = false; return; }
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    AtomicFile.BackupBeforeWrite(FilePath);
                    AtomicFile.Write(FilePath, stream => JsonSerializer.Serialize(stream, new SettingsFile(3, _apps, _streams), AndroidSettingsJsonContext.Default.SettingsFile));
                    _writing = false; return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lock (_gate) _writing = false; Trace.WriteLine("Save phone audio settings: " + ex.Message); }
    }
    public Task FlushAsync() { lock (_gate) return _save; }
    internal sealed record SettingsFile(int Version, Dictionary<string, AndroidAppAudioSettings> Apps,
        Dictionary<string, AndroidStreamSettings>? Streams = null);
}
