using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SonicRoute.Core;

namespace SonicRoute.AndroidAudio;

public sealed record AndroidPhoneProfile(string Name, AndroidDevice? LastDevice, IReadOnlyList<AndroidApp> RetainedApps)
{
    public string ReceiveFolder { get; init; } = AndroidFileTransfer.Downloads;
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<string> DisabledSmartVolumeApps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AndroidApp> QuickPanelApps { get; init; } = RetainedApps;
    public IReadOnlyList<AndroidOutputSink> OutputSinks { get; init; } = [AndroidOutputSink.DefaultSpeaker];
    public bool ManuallyDisconnected { get; init; }
    public string? LastWirelessAddress { get; init; }

    internal static string? WirelessAddress(string? serial) =>
        serial != null && IPEndPoint.TryParse(serial, out var endpoint) && endpoint.Port > 0 ? endpoint.ToString() : null;

    internal AndroidPhoneProfile WithDevice(AndroidDevice device, string? wirelessAddress = null) => this with
    {
        LastDevice = device,
        LastWirelessAddress = (device.IsWifi ? WirelessAddress(device.Serial) ?? WirelessAddress(wirelessAddress) : null)
            ?? LastWirelessAddress ?? (LastDevice is { IsWifi: true } prior ? WirelessAddress(prior.Serial) : null)
    };

    // An address is only a hint. Keep the selected phone's identity for the
    // handshake, including when an old USB-only record must rely on mDNS.
    internal AndroidDevice? WirelessReconnectTarget(bool discoverFromUsb)
    {
        if (LastDevice is not { } saved || saved.IsWifi) return LastDevice;
        if ((!discoverFromUsb && LastWirelessAddress == null) || AndroidDeviceIdentity.StableKey(saved) == null) return null;
        return saved with { IsWifi = true, Serial = LastWirelessAddress ?? "",
            HardwareId = string.IsNullOrWhiteSpace(saved.HardwareId) ? saved.Serial : saved.HardwareId,
            State = "offline", TransportId = null, ConnectionTarget = null };
    }
}

internal sealed record AndroidSavedConnection(string Name, AndroidDevice Device, string Address);

/// <summary>Independent phone libraries and a persisted selection; no discovery or idle work.</summary>
public sealed class AndroidPhoneStore
{
    private readonly object _gate = new();
    private readonly Func<string>? _defaultName;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record State(IReadOnlyList<AndroidPhoneProfile> Profiles, string SelectedId,
        IReadOnlyList<AndroidDevice>? RemovedDevices = null, IReadOnlyList<AndroidPhoneProfile>? RemovedProfiles = null);
    private State _state;
    private Task _save = Task.CompletedTask;
    private bool _writing;
    private long _revision;
    private long _managementRevision;
    private bool _loadedSafely = true;
    private bool _legacyFormat;
    internal long ManagementRevision { get { lock (_gate) return _managementRevision; } }
    internal bool HasUnresolvedRemovals { get { lock (_gate) return _state.RemovedDevices?.Any(d => AndroidDeviceIdentity.StableKey(d) == null) == true; } }
    public string FilePath { get; }
    public event Action? Changed;
    public event Action? DisplayNamesChanged;
    public event Action? OutputsChanged;
    public IReadOnlyList<AndroidPhoneProfile> Profiles { get { lock (_gate) return _state.Profiles; } }
    public AndroidPhoneProfile Profile { get { lock (_gate) return _state.Profiles.First(p => p.Id == _state.SelectedId); } }

    public AndroidPhoneStore(string filePath) : this(filePath, null) { }

    public AndroidPhoneStore(string filePath, Func<string>? defaultName)
    {
        FilePath = Path.GetFullPath(filePath);
        _defaultName = defaultName;
        var draft = new AndroidPhoneProfile("", null, Array.Empty<AndroidApp>());
        _state = new(Array.AsReadOnly(new[] { draft }), draft.Id);
        try
        {
            if (!File.Exists(FilePath)) return;
            _loadedSafely = false;
            if (new FileInfo(FilePath).Length > 32 * 1024 * 1024) return;
            using var file = File.OpenRead(FilePath);
            using var json = JsonDocument.Parse(file);
            int version = json.RootElement.GetProperty("Version").GetInt32();
            AndroidPhoneProfile[] profiles; string? selected;
            IReadOnlyList<AndroidDevice>? removed = null;
            IReadOnlyList<AndroidPhoneProfile>? removedProfiles = null;
            if (version == 1)
            {
                var saved = json.RootElement.Deserialize<LegacyFile>();
                if (saved?.Apps == null) return;
                profiles = [LoadProfile(new(Guid.NewGuid().ToString("N"), saved.Name, saved.Device, saved.Apps, saved.DisabledSmartVolumePackages))];
                selected = profiles[0].Id;
            }
            else if (version is 2 or 3)
            {
                var saved = json.RootElement.Deserialize<PhoneFile>();
                if (saved?.Phones == null || saved.Phones.Length is < 1 or > 32) return;
                profiles = saved.Phones.Select(LoadProfile).DistinctBy(p => p.Id).ToArray();
                selected = saved.SelectedId;
                removed = Array.AsReadOnly((saved.RemovedDevices ?? []).Where(d => d != null && !string.IsNullOrWhiteSpace(d.Serial)
                    && d.Serial.Length <= 512 && (d.HardwareId?.Length ?? 0) <= 128)
                    .DistinctBy(d => (AndroidDeviceIdentity.Key(d), d.Serial)).ToArray());
                if (saved.RemovedProfiles is { Length: > 0 } archived)
                    removedProfiles = LoadRemovedProfiles(archived);
            }
            else return;
            _state = new(Array.AsReadOnly(profiles), profiles.FirstOrDefault(p => p.Id == selected)?.Id ?? profiles[0].Id, removed, removedProfiles);
            _loadedSafely = true;
            _legacyFormat = version < 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { Trace.WriteLine("Phone library: " + ex.Message); }
    }

    private static IReadOnlyList<AndroidPhoneProfile> LoadRemovedProfiles(SavedPhone[] saved)
    {
        var profiles = new Dictionary<string, AndroidPhoneProfile>(StringComparer.Ordinal);
        foreach (var item in saved)
        {
            if (item?.Device is not { } device || string.IsNullOrWhiteSpace(device.Serial) || device.Serial.Length > 512
                || (device.HardwareId?.Length ?? 0) > 128) continue;
            try { var profile = LoadProfile(item); profiles[profile.Id] = profile; }
            catch (ArgumentException ex) { Trace.WriteLine("Removed phone preferences: " + ex.Message); }
        }
        return Array.AsReadOnly(profiles.Values.ToArray());
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
            ManuallyDisconnected = saved.ManuallyDisconnected,
            LastWirelessAddress = AndroidPhoneProfile.WirelessAddress(saved.LastWirelessAddress)
                ?? (saved.Device is { IsWifi: true } wifi ? AndroidPhoneProfile.WirelessAddress(wifi.Serial) : null),
            ReceiveFolder = AndroidFileTransfer.IsSharedPath(saved.ReceiveFolder) ? saved.ReceiveFolder! : AndroidFileTransfer.Downloads,
            DisabledSmartVolumeApps = Array.AsReadOnly((saved.DisabledSmartVolumePackages ?? []).Take(1000).Where(packages.Contains).Distinct(StringComparer.Ordinal).ToArray())
        });
    }
    private static string CleanName(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim()[..Math.Min(value.Trim().Length, 64)];
    private static void ValidateApp(AndroidApp app)
    {
        AdbClient.ValidatePackage(app.Package);
        if (app.IconPng?.Length > 131072 || app.Name == null || app.Name.Length > 1024 || app.Uid <= 0) throw new ArgumentException("Invalid phone application.");
    }
    public AndroidPhoneProfile? FindProfile(AndroidDevice device)
    { lock (_gate) return _state.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, device)); }
    public string GetProfileDisplayName(AndroidPhoneProfile profile) => profile.Name.Length != 0
        ? profile.Name : _defaultName?.Invoke() ?? profile.LastDevice?.Name ?? "Android";
    public string GetDisplayName(AndroidDevice device) => FindProfile(device) is { } profile ? GetProfileDisplayName(profile) : device.Name;
    internal IReadOnlyList<AndroidSavedConnection> FindWirelessConnectionCandidates(IReadOnlyList<AndroidMdnsService> services,
        IReadOnlyList<AndroidDevice> connectedDevices)
    {
        if (services.Count == 0 && connectedDevices.Count == 0) return [];
        State state;
        lock (_gate) state = _state;

        var profiles = state.Profiles.Concat(state.RemovedProfiles ?? [])
            .Where(p => p.LastDevice is { } device && !string.IsNullOrWhiteSpace(device.Serial)).ToArray();
        var names = profiles.ToLookup(p => AndroidDeviceIdentity.Key(p.LastDevice!), StringComparer.OrdinalIgnoreCase);
        var wireless = profiles.Select(p => p.LastDevice!).Concat(state.RemovedDevices ?? []).Where(d => d.IsWifi).ToArray();
        var savedByIdentity = wireless.ToLookup(AndroidDeviceIdentity.Key, StringComparer.OrdinalIgnoreCase);
        var savedBySerial = wireless.ToLookup(d => d.Serial.TrimEnd('.'), StringComparer.OrdinalIgnoreCase);
        var found = new List<AndroidSavedConnection>();
        var seen = new HashSet<(string Identity, string Address)>();
        void AddCandidate(AndroidDevice device, string address)
        {
            if (!IPEndPoint.TryParse(address, out var endpoint) || endpoint.Port == 0) return;
            address = endpoint.ToString();
            string identity = AndroidDeviceIdentity.Key(device);
            if (!seen.Add((identity, address))) return;
            string name = names[identity].FirstOrDefault()?.Name ?? (string.IsNullOrWhiteSpace(device.Name) ? address : device.Name);
            found.Add(new(name, device, address));
        }

        // A ready ADB transport can supply the current address even when mDNS is silent or stale.
        foreach (var connected in connectedDevices)
        {
            if (!connected.IsWifi || connected.State != "device" || AndroidDeviceIdentity.StableKey(connected) is not { } key) continue;
            if (savedByIdentity[key].FirstOrDefault() is { } saved) AddCandidate(saved, connected.Serial);
        }
        foreach (var service in services)
        {
            if (AndroidDeviceIdentity.WirelessServiceIdentity(service) is not { } advertised) continue;
            var device = savedBySerial[advertised.Serial].FirstOrDefault()
                ?? savedBySerial[advertised.Serial + ".local"].FirstOrDefault()
                ?? savedByIdentity[AndroidDeviceIdentity.Key(advertised)].FirstOrDefault();
            if (device != null) AddCandidate(device, service.Address);
        }
        return found;
    }

    public bool IsSmartVolumeEnabled(AndroidDevice? device, string package)
    {
        lock (_gate)
        {
            var profile = device != null
                ? _state.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, device))
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
    internal bool RememberRecoveredAddress(string id, string address, Func<bool> stillCurrent)
    {
        bool accepted = false;
        ChangeProfile(id, profile =>
        {
            if (profile.ManuallyDisconnected || !stillCurrent()) return profile;
            accepted = true;
            return profile.LastWirelessAddress == address ? profile : profile with { LastWirelessAddress = address };
        });
        return accepted;
    }
    private static bool IsRemoved(State state, AndroidDevice device) =>
        state.RemovedDevices?.Any(d => AndroidDeviceIdentity.MatchesSavedDevice(d, device)) == true;
    public bool IsRemoved(AndroidDevice device) { lock (_gate) return IsRemoved(_state, device); }
    internal bool HasConflictingIdentity(AndroidDevice device)
    {
        lock (_gate) return _state.Profiles.Concat(_state.RemovedProfiles ?? []).Any(p => p.LastDevice is { } saved
            && AndroidDeviceIdentity.Key(saved) == AndroidDeviceIdentity.Key(device)
            && (AndroidDeviceIdentity.ConflictingEvidence(saved, device)
                || saved.IdentityFingerprint != null && device.IdentityFingerprint == null));
    }
    // Only an explicit connection/add can restore a removed device. Discovery never does.
    public void RememberDevice(AndroidDevice device) => BindExplicit(device, null, null);

    // Admission is a single durable library transaction. The active profile wins over an
    // archived one; old preferences are copied before committing, never moved/deleted.
    internal bool BindExplicit(AndroidDevice device, string? expectedProfileId, long? expectedRevision,
        Func<AndroidDevice, AndroidDevice, Action?>? migrate = null, AndroidDevice? verifiedAlias = null,
        Func<bool>? stillCurrent = null, string? wirelessAddress = null)
    {
        bool accepted = false;
        var rollback = new List<Action>();
        void Migrate(AndroidDevice prior)
        {
            if (migrate?.Invoke(prior, device) is { } undo) rollback.Add(undo);
        }
        try { Change(state =>
        {
            if (expectedRevision is { } revision && revision != _managementRevision || stillCurrent?.Invoke() == false) return state;
            var active = state.Profiles.Where(p => p.LastDevice != null).ToArray();
            var current = expectedProfileId == null
                ? active.FirstOrDefault(p => AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, device))
                : active.FirstOrDefault(p => p.Id == expectedProfileId && (AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, device)
                    || verifiedAlias != null && AndroidDeviceIdentity.SamePhysicalDevice(device, verifiedAlias)
                        && AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, verifiedAlias)));
            if (expectedProfileId != null && current == null) return state;
            if (current?.LastDevice?.IdentityFingerprint != null && device.IdentityFingerprint != current.LastDevice.IdentityFingerprint)
                return state;
            // A repeated hardware serial with contradictory evidence must never overwrite a library.
            if (HasConflictingIdentity(device)) return state;
            var preserved = (state.RemovedProfiles ?? []).Where(p => AndroidDeviceIdentity.SamePhysicalDevice(p.LastDevice, device)
                || AndroidDeviceIdentity.StableKey(p.LastDevice) == null && p.LastDevice?.Serial == device.Serial).ToArray();
            var matching = active.Where(p => p.Id == current?.Id || AndroidDeviceIdentity.SamePhysicalDevice(p.LastDevice, device)).ToArray();
            if (matching.Concat(preserved).Any(p => p.LastDevice?.IdentityFingerprint is { } proof && proof != device.IdentityFingerprint))
                return state;
            if (current != null && matching.Length == 1 && preserved.Length == 0 && !IsRemoved(state, device)
                && !current.ManuallyDisconnected && current.WithDevice(device, wirelessAddress) == current && state.SelectedId == current.Id)
            { accepted = true; return state; }
            var primary = current ?? matching.FirstOrDefault(p => p.Id == state.SelectedId) ?? matching.FirstOrDefault()
                ?? preserved.FirstOrDefault() ?? state.Profiles.FirstOrDefault(p => p.LastDevice == null)
                ?? new AndroidPhoneProfile("", null, []);
            if (active.Length >= 32 && matching.Length == 0) throw AndroidError.WithCode(new InvalidOperationException("最多可保存 32 台手机。"), AndroidErrorCode.MaxSavedPhones);
            if (primary.LastDevice is { } prior && AndroidDeviceIdentity.Key(prior) != AndroidDeviceIdentity.Key(device))
                Migrate(prior);
            foreach (var secondary in matching.Concat(preserved).Where(p => p.Id != primary.Id))
                if (secondary.LastDevice is { } alternate && AndroidDeviceIdentity.Key(alternate) != AndroidDeviceIdentity.Key(device)) Migrate(alternate);
            var combined = new[] { primary }.Concat(matching).Concat(preserved).DistinctBy(p => p.Id).ToArray();
            var connected = primary.WithDevice(device, wirelessAddress);
            var merged = WithQuickPanelApps(connected with {
                Name = primary.Name.Length != 0 ? primary.Name : combined.FirstOrDefault(p => p.Name.Length != 0)?.Name ?? "",
                LastWirelessAddress = connected.LastWirelessAddress ?? combined.Select(p => p.LastWirelessAddress).FirstOrDefault(a => a != null),
                ManuallyDisconnected = false,
                RetainedApps = Array.AsReadOnly(combined.SelectMany(p => p.RetainedApps).DistinctBy(a => a.Package).ToArray()),
                DisabledSmartVolumeApps = Array.AsReadOnly(combined.SelectMany(p => p.DisabledSmartVolumeApps).Distinct(StringComparer.Ordinal).ToArray()),
                ReceiveFolder = primary.ReceiveFolder != AndroidFileTransfer.Downloads ? primary.ReceiveFolder
                    : combined.FirstOrDefault(p => p.ReceiveFolder != AndroidFileTransfer.Downloads)?.ReceiveFolder ?? primary.ReceiveFolder
            });
            var profiles = active.Where(p => !matching.Any(m => m.Id == p.Id) || p.Id == merged.Id)
                .Select(p => p.Id == merged.Id ? merged : p).ToList();
            if (!profiles.Any(p => p.Id == merged.Id)) profiles.Add(merged);
            var removed = (state.RemovedDevices ?? []).Where(d => !AndroidDeviceIdentity.MatchesSavedDevice(d, device)).ToArray();
            var archived = (state.RemovedProfiles ?? []).Where(p => !preserved.Any(m => m.Id == p.Id)).ToArray();
            accepted = true;
            return state with { Profiles = Array.AsReadOnly(profiles.ToArray()), SelectedId = merged.Id,
                RemovedDevices = Array.AsReadOnly(removed), RemovedProfiles = Array.AsReadOnly(archived) };
        }, durable: true, management: true); }
        catch
        {
            for (int i = rollback.Count - 1; i >= 0; --i)
                try { rollback[i](); } catch (Exception ex) { Trace.WriteLine("Phone preference rollback (backup retained): " + ex.Message); }
            throw;
        }
        return accepted;
    }

    internal void SetDisconnected(AndroidDevice device, bool disconnected) => Change(state =>
    {
        var profiles = state.Profiles.Select(p => AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, device)
            && p.ManuallyDisconnected != disconnected ? p with { ManuallyDisconnected = disconnected } : p).ToArray();
        return profiles.SequenceEqual(state.Profiles) ? state : state with { Profiles = Array.AsReadOnly(profiles) };
    }, durable: true, management: true);
    public void ObserveDevices(IReadOnlyList<AndroidDevice> devices) => Change(state => Upsert(state,
        devices.Where(d => d.State == "device" && !IsRemoved(state, d)
            && !state.Profiles.Any(p => AndroidDeviceIdentity.Key(p.LastDevice ?? d) == AndroidDeviceIdentity.Key(d)
                && AndroidDeviceIdentity.ConflictingEvidence(p.LastDevice, d))), select: false));
    internal void ObserveManagedDevices(IReadOnlyList<AndroidDevice> devices, long revision, Func<bool> stillCurrent) => Change(state =>
    {
        if (revision != _managementRevision || !stillCurrent()) return state;
        return Upsert(state, devices.Where(d => !IsRemoved(state, d)), select: false);
    });
    public bool RemoveDevice(string profileId, IReadOnlyList<AndroidDevice>? aliases = null)
    {
        bool removed = false;
        Change(state =>
        {
            var profile = state.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile?.LastDevice is not { } device) return state;
            var known = (aliases ?? []).Where(d => AndroidDeviceIdentity.MatchesSavedDevice(device, d)).Append(device)
                .Select(d => d with { HardwareId = device.HardwareId ?? d.HardwareId, Name = "", State = "offline", TransportId = null }).ToArray();
            var ignored = Array.AsReadOnly((state.RemovedDevices ?? []).Concat(known).DistinctBy(d => (AndroidDeviceIdentity.Key(d), d.Serial)).ToArray());
            var archived = state.RemovedProfiles;
            var identity = AndroidDeviceIdentity.StableKey(device) != null ? known[^1]
                : known.FirstOrDefault(d => AndroidDeviceIdentity.StableKey(d) != null);
            if (device != null)
            {
                // Keep preferences without keeping per-device app artwork or live output endpoints.
                var saved = WithQuickPanelApps(profile with {
                    LastDevice = identity ?? device, OutputSinks = [AndroidOutputSink.DefaultSpeaker],
                    RetainedApps = Array.AsReadOnly(profile.RetainedApps.Select(a => a.IconPng == null ? a : a with { IconPng = null }).ToArray())
                });
                archived = Array.AsReadOnly((archived ?? []).Where(p => !AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, identity ?? device)).Append(saved).ToArray());
            }
            var remaining = state.Profiles.Where(p => p.Id != profileId).ToArray();
            if (remaining.Length == 0) remaining = [new("", null, Array.Empty<AndroidApp>())];
            removed = true;
            return state with { Profiles = Array.AsReadOnly(remaining),
                SelectedId = state.SelectedId == profileId ? remaining[0].Id : state.SelectedId, RemovedDevices = ignored, RemovedProfiles = archived };
        }, durable: true, management: true);
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
            var previous = profiles[i].LastDevice;
            var alias = observed.FirstOrDefault(d => d.Serial == previous?.Serial && d.HardwareId != null
                && AndroidDeviceIdentity.MatchesSavedDevice(previous, d));
            if (alias != null && profiles[i].LastDevice != alias) { profiles[i] = profiles[i].WithDevice(alias); changed = true; }
        }
        foreach (var device in AndroidDeviceIdentity.Distinct(observed))
        {
            int index = profiles.FindIndex(p => AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, device));
            if (index < 0) index = profiles.FindIndex(p => p.LastDevice == null);
            AndroidPhoneProfile next;
            if (index < 0)
            {
                if (profiles.Count >= 32) throw AndroidError.WithCode(new InvalidOperationException("最多可保存 32 台手机。"), AndroidErrorCode.MaxSavedPhones);
                next = new AndroidPhoneProfile("", null, Array.Empty<AndroidApp>()).WithDevice(device);
                profiles.Add(next); changed = true;
            }
            else
            {
                next = profiles[index].WithDevice(device);
                if (next != profiles[index]) { profiles[index] = next; changed = true; }
            }
            if (select && selected != next.Id) { selected = next.Id; changed = true; }
        }
        foreach (var group in profiles.Where(p => p.LastDevice != null).GroupBy(p => AndroidDeviceIdentity.Key(p.LastDevice!), StringComparer.Ordinal)
            .Where(g => g.Count() > 1 && !g.Any(a => g.Any(b => AndroidDeviceIdentity.ConflictingEvidence(a.LastDevice, b.LastDevice)))).ToArray())
        {
            var primary = group.FirstOrDefault(p => p.Id == selected) ?? group.First();
            var apps = group.OrderByDescending(p => p.Id == primary.Id).SelectMany(p => p.RetainedApps).DistinctBy(a => a.Package).ToArray();
            var disabledSmart = group.SelectMany(p => p.DisabledSmartVolumeApps).Distinct(StringComparer.Ordinal).ToArray();
            var merged = WithQuickPanelApps(primary with { Name = primary.Name.Length == 0 ? group.FirstOrDefault(p => p.Name.Length != 0)?.Name ?? "" : primary.Name,
                LastWirelessAddress = primary.LastWirelessAddress ?? group.Select(p => p.LastWirelessAddress).FirstOrDefault(a => a != null),
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
    internal void SetReceiveFolder(string profileId, string folder)
    {
        AndroidFileTransfer.ValidateFolder(folder);
        ChangeProfile(profileId, p => p.ReceiveFolder == folder ? p : p with { ReceiveFolder = folder });
    }

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
            int index = _state.Profiles.ToList().FindIndex(p => AndroidDeviceIdentity.MatchesSavedDevice(p.LastDevice, device));
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
            var updated = p.RetainedApps.Select(a =>
            {
                if (!current.TryGetValue(a.Package, out var nextApp)) return a;
                // Preserve existing IconPng array reference when bytes are identical so UI weak caches do not re-decode.
                if (a.IconPng != null && nextApp.IconPng != null && a.IconPng.AsSpan().SequenceEqual(nextApp.IconPng))
                    nextApp = nextApp with { IconPng = a.IconPng };
                return nextApp;
            }).ToArray();
            bool same = updated.Zip(p.RetainedApps).All(pair => pair.First.Package == pair.Second.Package
                && pair.First.Name == pair.Second.Name && pair.First.Uid == pair.Second.Uid
                && pair.First.Category == pair.Second.Category && pair.First.PlatformCategory == pair.Second.PlatformCategory
                && (ReferenceEquals(pair.First.IconPng, pair.Second.IconPng)
                    || (pair.First.IconPng ?? []).AsSpan().SequenceEqual(pair.Second.IconPng ?? [])));
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
    private void Change(Func<State, State> change, bool durable = false, bool management = false)
    {
        bool namesChanged;
        bool outputsChanged;
        lock (_gate)
        {
            if (!_loadedSafely) throw new IOException("Phone library could not be loaded; the original file has been preserved.");
            var next = change(_state); if (next == _state) return;
            if (durable)
            {
                BackupBeforeMigration();
                WriteState(next);
            }
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
            if (management) ++_managementRevision;
            if (!durable && !_writing) { _writing = true; _save = Task.Run(SaveAsync); }
        }
        Notify(Changed);
        if (namesChanged) Notify(DisplayNamesChanged);
        if (outputsChanged) Notify(OutputsChanged);
    }
    private static void Notify(Action? listeners)
    {
        if (listeners == null) return;
        foreach (Action listener in listeners.GetInvocationList())
            try { listener(); } catch (Exception ex) { Trace.WriteLine("Phone library notification: " + ex.Message); }
    }
    private async Task SaveAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(150).ConfigureAwait(false);
                lock (_gate) { WriteState(_state); _writing = false; return; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lock (_gate) _writing = false; Trace.WriteLine("Save phone library: " + ex.Message); }
    }
    public Task FlushAsync() { lock (_gate) return _save; }
    private void BackupBeforeMigration()
    {
        string backup = FilePath + ".pre-connection-management.bak";
        if (File.Exists(FilePath) && !File.Exists(backup))
            AtomicFile.Write(backup, stream => { using var original = File.OpenRead(FilePath); original.CopyTo(stream); });
    }
    private void WriteState(State state)
    {
        if (_legacyFormat) BackupBeforeMigration();
        var saved = new PhoneFile(3, state.SelectedId, state.Profiles.Select(SaveProfile).ToArray(), state.RemovedDevices?.ToArray(),
            state.RemovedProfiles?.Select(SaveProfile).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        AtomicFile.Write(FilePath, stream => JsonSerializer.Serialize(stream, saved, JsonOptions));
        _legacyFormat = false;
    }
    private static SavedPhone SaveProfile(AndroidPhoneProfile profile) => new(profile.Id, profile.Name, profile.LastDevice,
        profile.RetainedApps.ToArray(), profile.DisabledSmartVolumeApps.ToArray(), profile.ReceiveFolder, profile.ManuallyDisconnected, profile.LastWirelessAddress);
    private sealed record LegacyFile(int Version, string Name, AndroidDevice? Device, AndroidApp[] Apps, string[]? DisabledSmartVolumePackages = null);
    private sealed record SavedPhone(string Id, string Name, AndroidDevice? Device, AndroidApp[] Apps, string[]? DisabledSmartVolumePackages = null, string? ReceiveFolder = null, bool ManuallyDisconnected = false, string? LastWirelessAddress = null);
    private sealed record PhoneFile(int Version, string SelectedId, SavedPhone[] Phones, AndroidDevice[]? RemovedDevices = null,
        SavedPhone[]? RemovedProfiles = null);
}
