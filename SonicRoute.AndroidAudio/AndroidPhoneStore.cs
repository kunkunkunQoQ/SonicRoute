using System.Diagnostics;
using System.Text.Json;
using SonicRoute.Core;

namespace SonicRoute.AndroidAudio;

public sealed record AndroidPhoneProfile(string Name, AndroidDevice? LastDevice, IReadOnlyList<AndroidApp> RetainedApps)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<string> DisabledSmartVolumeApps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AndroidApp> QuickPanelApps { get; init; } = RetainedApps;
    public IReadOnlyList<AndroidOutputSink> OutputSinks { get; init; } = [AndroidOutputSink.DefaultSpeaker];
}

/// <summary>Independent phone libraries and a persisted selection; no discovery or idle work.</summary>
public sealed class AndroidPhoneStore
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record State(IReadOnlyList<AndroidPhoneProfile> Profiles, string SelectedId,
        IReadOnlyList<AndroidDevice>? RemovedDevices = null);
    private State _state;
    private Task _save = Task.CompletedTask;
    private bool _writing;
    private long _revision;
    public string FilePath { get; }
    public event Action? Changed;
    public event Action? DisplayNamesChanged;
    public event Action? OutputsChanged;
    public IReadOnlyList<AndroidPhoneProfile> Profiles { get { lock (_gate) return _state.Profiles; } }
    public AndroidPhoneProfile Profile { get { lock (_gate) return _state.Profiles.First(p => p.Id == _state.SelectedId); } }

    public AndroidPhoneStore(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        var draft = new AndroidPhoneProfile("我的手机", null, Array.Empty<AndroidApp>());
        _state = new(Array.AsReadOnly(new[] { draft }), draft.Id);
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 32 * 1024 * 1024) return;
            using var file = File.OpenRead(FilePath);
            using var json = JsonDocument.Parse(file);
            int version = json.RootElement.GetProperty("Version").GetInt32();
            AndroidPhoneProfile[] profiles; string? selected;
            IReadOnlyList<AndroidDevice>? removed = null;
            if (version == 1)
            {
                var saved = json.RootElement.Deserialize<LegacyFile>();
                if (saved?.Apps == null) return;
                profiles = [LoadProfile(new(Guid.NewGuid().ToString("N"), saved.Name, saved.Device, saved.Apps, saved.DisabledSmartVolumePackages))];
                selected = profiles[0].Id;
            }
            else if (version == 2)
            {
                var saved = json.RootElement.Deserialize<PhoneFile>();
                if (saved?.Phones == null || saved.Phones.Length is < 1 or > 32) return;
                profiles = saved.Phones.Select(LoadProfile).DistinctBy(p => p.Id).ToArray();
                selected = saved.SelectedId;
                removed = Array.AsReadOnly((saved.RemovedDevices ?? []).Where(d => d != null && !string.IsNullOrWhiteSpace(d.Serial)
                    && d.Serial.Length <= 512 && (d.HardwareId?.Length ?? 0) <= 128).DistinctBy(d => d.Serial).ToArray());
            }
            else return;
            _state = new(Array.AsReadOnly(profiles), profiles.FirstOrDefault(p => p.Id == selected)?.Id ?? profiles[0].Id, removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { Trace.WriteLine("Phone library: " + ex.Message); }
    }
    private static AndroidPhoneProfile LoadProfile(SavedPhone saved)
    {
        if (saved == null || string.IsNullOrEmpty(saved.Id) || saved.Id.Length > 128 || saved.Apps == null) throw new ArgumentException("Invalid phone library.");
        var apps = saved.Apps.Take(1000).Where(a => a != null).DistinctBy(a => a.Package).ToArray();
        foreach (var app in apps) ValidateApp(app);
        var packages = apps.Select(a => a.Package).ToHashSet(StringComparer.Ordinal);
        return WithQuickPanelApps(new(CleanName(saved.Name), saved.Device, Array.AsReadOnly(apps))
        {
            Id = saved.Id,
            DisabledSmartVolumeApps = Array.AsReadOnly((saved.DisabledSmartVolumePackages ?? []).Take(1000).Where(packages.Contains).Distinct(StringComparer.Ordinal).ToArray())
        });
    }
    private static string CleanName(string? value) => string.IsNullOrWhiteSpace(value) ? "我的手机" : value.Trim()[..Math.Min(value.Trim().Length, 64)];
    private static void ValidateApp(AndroidApp app)
    {
        AdbClient.ValidatePackage(app.Package);
        if (app.IconPng?.Length > 131072 || app.Name == null || app.Name.Length > 1024 || app.Uid <= 0) throw new ArgumentException("Invalid phone application.");
    }
    public AndroidPhoneProfile? FindProfile(AndroidDevice device)
    { lock (_gate) return _state.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.Matches(p.LastDevice, device)); }
    public string GetDisplayName(AndroidDevice device) => FindProfile(device)?.Name ?? device.Name;
    public bool IsSmartVolumeEnabled(AndroidDevice? device, string package)
    {
        lock (_gate)
        {
            var profile = device != null
                ? _state.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.Matches(p.LastDevice, device))
                : _state.Profiles.FirstOrDefault(p => p.Id == _state.SelectedId);
            return profile == null || !profile.DisabledSmartVolumeApps.Contains(package, StringComparer.Ordinal);
        }
    }
    public bool SelectProfile(string id)
    {
        bool found = false;
        Change(state => { found = state.Profiles.Any(p => p.Id == id); return found && state.SelectedId != id ? state with { SelectedId = id } : state; });
        return found;
    }
    public void Rename(string name) => ChangeProfile(null, p => p with { Name = CleanName(name) });
    private static bool IsRemoved(State state, AndroidDevice device) => state.RemovedDevices?.Any(d => AndroidDeviceIdentity.Matches(d, device)) == true;
    public bool IsRemoved(AndroidDevice device) { lock (_gate) return IsRemoved(_state, device); }
    // Only an explicit connection/add can restore a removed device. Discovery never does.
    public void RememberDevice(AndroidDevice device) => Change(state => Upsert(state with
    {
        RemovedDevices = state.RemovedDevices?.Any(d => AndroidDeviceIdentity.Matches(d, device)) == true
            ? Array.AsReadOnly(state.RemovedDevices.Where(d => !AndroidDeviceIdentity.Matches(d, device)).ToArray()) : state.RemovedDevices
    }, [device], select: true));
    public void ObserveDevices(IReadOnlyList<AndroidDevice> devices) => Change(state => Upsert(state,
        devices.Where(d => d.State == "device" && !IsRemoved(state, d)), select: false));
    public bool RemoveDevice(string profileId, IReadOnlyList<AndroidDevice>? aliases = null)
    {
        bool removed = false;
        Change(state =>
        {
            var profile = state.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile?.LastDevice is not { } device) return state;
            var known = (aliases ?? []).Where(d => AndroidDeviceIdentity.Matches(d, device)).Append(device)
                .Select(d => d with { HardwareId = device.HardwareId ?? d.HardwareId, Name = "", State = "offline", TransportId = null });
            var ignored = Array.AsReadOnly((state.RemovedDevices ?? []).Concat(known).DistinctBy(d => d.Serial).ToArray());
            var remaining = state.Profiles.Where(p => p.Id != profileId).ToArray();
            if (remaining.Length == 0) remaining = [new("我的手机", null, Array.Empty<AndroidApp>())];
            removed = true;
            return state with { Profiles = Array.AsReadOnly(remaining),
                SelectedId = state.SelectedId == profileId ? remaining[0].Id : state.SelectedId, RemovedDevices = ignored };
        });
        return removed;
    }
    private static State Upsert(State state, IEnumerable<AndroidDevice> devices, bool select)
    {
        var profiles = state.Profiles.ToList(); string selected = state.SelectedId; bool changed = false;
        var observed = devices.ToArray();
        // Upgrade old endpoint-only records before deduplicating the live transports.
        // Otherwise an mDNS entry arriving before the matching IP entry could create a second library.
        for (int i = 0; i < profiles.Count; ++i)
        {
            var alias = observed.FirstOrDefault(d => d.Serial == profiles[i].LastDevice?.Serial && d.HardwareId != null);
            if (alias != null && profiles[i].LastDevice != alias) { profiles[i] = profiles[i] with { LastDevice = alias }; changed = true; }
        }
        foreach (var device in AndroidDeviceIdentity.Distinct(observed))
        {
            int index = profiles.FindIndex(p => AndroidDeviceIdentity.Matches(p.LastDevice, device));
            if (index < 0) index = profiles.FindIndex(p => p.LastDevice == null);
            AndroidPhoneProfile next;
            if (index < 0)
            {
                if (profiles.Count >= 32) throw new InvalidOperationException("最多可保存 32 台手机。");
                next = new("我的手机", device, Array.Empty<AndroidApp>());
                profiles.Add(next); changed = true;
            }
            else
            {
                next = profiles[index] with { LastDevice = device };
                if (next != profiles[index]) { profiles[index] = next; changed = true; }
            }
            if (select && selected != next.Id) { selected = next.Id; changed = true; }
        }
        foreach (var group in profiles.Where(p => p.LastDevice != null).GroupBy(p => AndroidDeviceIdentity.Key(p.LastDevice!), StringComparer.Ordinal).Where(g => g.Count() > 1).ToArray())
        {
            var primary = group.FirstOrDefault(p => p.Id == selected) ?? group.First();
            var apps = group.OrderByDescending(p => p.Id == primary.Id).SelectMany(p => p.RetainedApps).DistinctBy(a => a.Package).ToArray();
            var disabledSmart = group.SelectMany(p => p.DisabledSmartVolumeApps).Distinct(StringComparer.Ordinal).ToArray();
            var merged = WithQuickPanelApps(primary with { Name = primary.Name == "我的手机" ? group.FirstOrDefault(p => p.Name != "我的手机")?.Name ?? primary.Name : primary.Name,
                RetainedApps = Array.AsReadOnly(apps), DisabledSmartVolumeApps = Array.AsReadOnly(disabledSmart) });
            profiles[profiles.FindIndex(p => p.Id == primary.Id)] = merged;
            profiles.RemoveAll(p => p.Id != primary.Id && group.Any(member => member.Id == p.Id));
            changed = true;
        }
        return changed ? state with { Profiles = Array.AsReadOnly(profiles.ToArray()), SelectedId = selected } : state;
    }
    public void Retain(AndroidApp app) => RetainMany([app]);
    public void RetainMany(IReadOnlyList<AndroidApp> apps)
    {
        foreach (var app in apps) ValidateApp(app);
        if (apps.Count == 0) return;
        ChangeProfile(null, p =>
        {
            if (apps.Count == 1) return p.RetainedApps.Any(a => a.Package == apps[0].Package) ? p
                : p with { RetainedApps = Array.AsReadOnly(p.RetainedApps.Append(apps[0]).ToArray()) };
            var packages = p.RetainedApps.Select(a => a.Package).ToHashSet(StringComparer.Ordinal);
            var added = apps.Where(app => packages.Add(app.Package)).ToArray();
            return added.Length == 0 ? p : p with { RetainedApps = Array.AsReadOnly(p.RetainedApps.Concat(added).ToArray()) };
        });
    }
    public void Remove(string package) => ChangeProfile(null, p => !p.RetainedApps.Any(a => a.Package == package) ? p
        : p with { RetainedApps = Array.AsReadOnly(p.RetainedApps.Where(a => a.Package != package).ToArray()),
            DisabledSmartVolumeApps = p.DisabledSmartVolumeApps.Contains(package)
                ? Array.AsReadOnly(p.DisabledSmartVolumeApps.Where(a => a != package).ToArray()) : p.DisabledSmartVolumeApps });
    public void SetSmartVolume(string package, bool enabled)
    {
        AdbClient.ValidatePackage(package);
        ChangeProfile(null, p => !p.RetainedApps.Any(a => a.Package == package) || enabled == !p.DisabledSmartVolumeApps.Contains(package) ? p
            : p with { DisabledSmartVolumeApps = Array.AsReadOnly(enabled
                ? p.DisabledSmartVolumeApps.Where(a => a != package).ToArray() : p.DisabledSmartVolumeApps.Append(package).ToArray()) });
    }
    private static AndroidPhoneProfile WithQuickPanelApps(AndroidPhoneProfile profile) =>
        profile with { QuickPanelApps = profile.RetainedApps };
    public void UpdateMetadata(IReadOnlyList<AndroidApp> apps) => UpdateMetadataCore(null, apps);
    public void UpdateMetadata(AndroidDevice device, IReadOnlyList<AndroidApp> apps)
    {
        string? id = FindProfile(device)?.Id;
        if (id != null) UpdateMetadataCore(id, apps);
    }
    public bool UpdateOutputs(AndroidDevice device, IReadOnlyList<AndroidOutputSink>? outputs)
    {
        var normalized = NormalizeOutputs(outputs);
        bool updated = false;
        lock (_gate)
        {
            int index = _state.Profiles.ToList().FindIndex(p => AndroidDeviceIdentity.Matches(p.LastDevice, device));
            if (index < 0) return false;
            var previous = _state.Profiles[index];
            if (previous.OutputSinks.SequenceEqual(normalized)) return false;
            var profiles = _state.Profiles.ToArray();
            profiles[index] = previous with { OutputSinks = normalized };
            _state = _state with { Profiles = Array.AsReadOnly(profiles) };
            updated = true;
        }
        if (updated)
        {
            OutputsChanged?.Invoke();
            Changed?.Invoke();
        }
        return updated;
    }
    private static IReadOnlyList<AndroidOutputSink> NormalizeOutputs(IReadOnlyList<AndroidOutputSink>? outputs) =>
        AndroidOutputSink.NormalizeList(outputs);
    private void UpdateMetadataCore(string? id, IReadOnlyList<AndroidApp> apps)
    {
        foreach (var app in apps) ValidateApp(app);
        var current = apps.ToDictionary(a => a.Package, StringComparer.Ordinal);
        ChangeProfile(id, p =>
        {
            var updated = p.RetainedApps.Select(a => current.GetValueOrDefault(a.Package, a)).ToArray();
            bool same = updated.Zip(p.RetainedApps).All(pair => pair.First.Package == pair.Second.Package
                && pair.First.Name == pair.Second.Name && pair.First.Uid == pair.Second.Uid
                && pair.First.Category == pair.Second.Category && pair.First.PlatformCategory == pair.Second.PlatformCategory
                && (pair.First.IconPng ?? []).AsSpan().SequenceEqual(pair.Second.IconPng ?? []));
            return same ? p : p with { RetainedApps = Array.AsReadOnly(updated) };
        });
    }
    private void ChangeProfile(string? id, Func<AndroidPhoneProfile, AndroidPhoneProfile> change) => Change(state =>
    {
        int index = state.Profiles.ToList().FindIndex(p => p.Id == (id ?? state.SelectedId));
        if (index < 0) return state;
        var previous = state.Profiles[index]; var next = change(previous);
        if (next == previous) return state;
        if (!ReferenceEquals(next.RetainedApps, previous.RetainedApps))
            next = WithQuickPanelApps(next);
        var profiles = state.Profiles.ToArray(); profiles[index] = next;
        return state with { Profiles = Array.AsReadOnly(profiles) };
    });
    private void Change(Func<State, State> change)
    {
        bool namesChanged;
        bool outputsChanged;
        lock (_gate)
        {
            var next = change(_state); if (next == _state) return;
            namesChanged = !ReferenceEquals(next.Profiles, _state.Profiles)
                && (next.Profiles.Count != _state.Profiles.Count
                    || next.Profiles.Any(p => _state.Profiles.FirstOrDefault(previous => previous.Id == p.Id)?.Name != p.Name));
            outputsChanged = namesChanged || (!ReferenceEquals(next.Profiles, _state.Profiles)
                && next.Profiles.Any(p =>
                {
                    var prev = _state.Profiles.FirstOrDefault(previous => previous.Id == p.Id);
                    return prev == null || (prev.LastDevice == null) != (p.LastDevice == null)
                        || !prev.OutputSinks.SequenceEqual(p.OutputSinks);
                }));
            _state = next; ++_revision;
            if (!_writing) { _writing = true; _save = Task.Run(SaveAsync); }
        }
        Changed?.Invoke();
        if (namesChanged) DisplayNamesChanged?.Invoke();
        if (outputsChanged) OutputsChanged?.Invoke();
    }
    private async Task SaveAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(150).ConfigureAwait(false);
                State state; long revision; lock (_gate) { state = _state; revision = _revision; }
                var saved = new PhoneFile(2, state.SelectedId, state.Profiles.Select(p => new SavedPhone(p.Id, p.Name, p.LastDevice,
                    p.RetainedApps.ToArray(), p.DisabledSmartVolumeApps.ToArray())).ToArray(), state.RemovedDevices?.ToArray());
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.Write(FilePath, stream => JsonSerializer.Serialize(stream, saved, JsonOptions));
                lock (_gate) if (revision == _revision) { _writing = false; return; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lock (_gate) _writing = false; Trace.WriteLine("Save phone library: " + ex.Message); }
    }
    public Task FlushAsync() { lock (_gate) return _save; }
    private sealed record LegacyFile(int Version, string Name, AndroidDevice? Device, AndroidApp[] Apps, string[]? DisabledSmartVolumePackages = null);
    private sealed record SavedPhone(string Id, string Name, AndroidDevice? Device, AndroidApp[] Apps, string[]? DisabledSmartVolumePackages = null);
    private sealed record PhoneFile(int Version, string SelectedId, SavedPhone[] Phones, AndroidDevice[]? RemovedDevices = null);
}
