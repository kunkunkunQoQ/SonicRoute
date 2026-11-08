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
    AndroidVirtualDisplaySize? DisplaySize = null, AndroidWindowSize? WindowSize = null);

/// <summary>Phone application and streaming preferences, independent of the desktop configuration.</summary>
public sealed class AndroidAudioSettingsStore
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, AndroidAppAudioSettings> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AndroidStreamSettings> _streams = new(StringComparer.Ordinal);
    private Task _save = Task.CompletedTask;
    private bool _writing;
    private long _revision;
    public string FilePath { get; }

    public AndroidAudioSettingsStore(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        try
        {
            if (!File.Exists(FilePath)) return;
            using var file = File.OpenRead(FilePath);
            var saved = JsonSerializer.Deserialize<SettingsFile>(file);
            if (saved?.Version is not (1 or 2 or 3) || saved.Apps == null) return;
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Trace.WriteLine("Phone audio settings: " + ex.Message); }
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
                SettingsFile snapshot; long revision;
                lock (_gate) { revision = _revision; snapshot = new(3, new(_apps, StringComparer.Ordinal), new(_streams, StringComparer.Ordinal)); }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.Write(FilePath, stream => JsonSerializer.Serialize(stream, snapshot, JsonOptions));
                lock (_gate)
                {
                    if (revision == _revision) { _writing = false; return; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lock (_gate) _writing = false; Trace.WriteLine("Save phone audio settings: " + ex.Message); }
    }
    public Task FlushAsync() { lock (_gate) return _save; }
    private sealed record SettingsFile(int Version, Dictionary<string, AndroidAppAudioSettings> Apps,
        Dictionary<string, AndroidStreamSettings>? Streams = null);
}
