namespace SonicRoute.AndroidAudio;

internal enum AndroidManagedConnectionState { Unknown, Offline, Unauthorized, Connected, IdentityConflict, Removed, ManuallyDisconnected }

internal sealed record AndroidManagedConnection(AndroidPhoneProfile? Profile, AndroidManagedConnectionState State, AndroidDevice? Device);

/// <summary>
/// The phone connection coordinator and live-target resolver. Raw ADB discovery
/// remains available to pairing; a saved device is a library reference, not a lease.
/// No periodic work is owned by this module.
/// </summary>
internal sealed class AndroidConnectionManager(AndroidPhoneStore store, Func<AndroidDeviceDiscovery> discovery,
    Func<AndroidDevice, AndroidDevice, Action?>? migrate = null, SrapPhoneTransport? app = null,
    AdbClient? adb = null, Func<AndroidDevice, AndroidDevice, bool, Action?>? migrateApp = null)
{
    private readonly Dictionary<string, CancellationTokenSource> _operationLifetimes = new();
    private readonly Dictionary<string, int> _retiring = new(StringComparer.Ordinal);
    internal long Revision => store.ManagementRevision;
    internal IReadOnlyList<AndroidDevice>? CurrentRaw => discovery().CurrentDevices;

    // Duplicate serials are unsafe even if the hardware property claims they match.
    // Distinct USB/Wi-Fi addresses may be aliases only with corroborating evidence.
    internal static bool IsAmbiguous(AndroidDevice device, IReadOnlyList<AndroidDevice> raw)
    {
        foreach (var other in raw)
        {
            if (other.State != "device" || other == device) continue;
            if (other.Serial == device.Serial && !AndroidDeviceIdentity.SameTransport(other, device)) return true;
            if (AndroidDeviceIdentity.StableKey(device) is not { } key || AndroidDeviceIdentity.StableKey(other) != key) continue;
            if (AndroidDeviceIdentity.ConflictingEvidence(device, other)) return true;
            if (!AndroidDeviceIdentity.SameTransport(device, other)
                && (device.IdentityFingerprint == null || other.IdentityFingerprint == null)) return true;
            if (!AndroidDeviceIdentity.SameTransport(device, other) && device.IsWifi == other.IsWifi
                && (!device.IsWifi || device.Serial.Contains("_adb-tls-connect", StringComparison.OrdinalIgnoreCase)
                    == other.Serial.Contains("_adb-tls-connect", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static bool VerifiedMatch(AndroidDevice saved, AndroidDevice live)
    {
        if (AndroidDeviceIdentity.ConflictingEvidence(saved, live)) return false;
        if (live.ConnectionTarget != null && live.HardwareId == null) return false;
        // Endpoint-only admissions are process-local (ConnectionTarget is not serialized).
        if (AndroidDeviceIdentity.StableKey(saved) == null)
            return saved.ConnectionTarget != null && saved.ConnectionTarget == live.ConnectionTarget
                && AndroidDeviceIdentity.SameTransport(saved, live);
        if (!AndroidDeviceIdentity.SamePhysicalDevice(saved, live)) return false;
        return saved.IdentityFingerprint == null || saved.IdentityFingerprint == live.IdentityFingerprint;
    }

    internal AndroidManagedConnection Get(AndroidDevice? requested, AndroidDevice? avoidTransport = null)
    {
        if (requested == null) return new(null, AndroidManagedConnectionState.Unknown, null);
        if (IsRetiring(requested)) return new(null, AndroidManagedConnectionState.Removed, null);
        var profiles = store.Profiles.Where(p => AndroidPhoneStore.OwnsDevice(p, requested)).ToArray();
        if (profiles.Length != 1)
            return new(null, store.IsRemoved(requested) ? AndroidManagedConnectionState.Removed
                : profiles.Length > 1 ? AndroidManagedConnectionState.IdentityConflict : AndroidManagedConnectionState.Unknown, null);
        var profile = profiles[0]; var saved = profile.LastDevice!;
        if (store.IsRemoved(requested) || store.IsRemoved(saved)) return new(profile, AndroidManagedConnectionState.IdentityConflict, null);
        if (saved.IsApp)
        {
            if (profile.AppTrustRevoked) return new(profile, AndroidManagedConnectionState.Unauthorized, null);
            if (profile.ManuallyDisconnected) return new(profile, AndroidManagedConnectionState.ManuallyDisconnected, null);
            return app?.Resolve(profile) is { } attached ? new(profile, AndroidManagedConnectionState.Connected, attached)
                : new(profile, AndroidManagedConnectionState.Offline, null);
        }
        if (AndroidDeviceIdentity.StableKey(requested) == null && AndroidDeviceIdentity.StableKey(saved) != null
            && (requested.ConnectionTarget == null || requested.ConnectionTarget != saved.ConnectionTarget
                || !AndroidDeviceIdentity.SameTransport(requested, saved)))
            return new(profile, AndroidManagedConnectionState.IdentityConflict, null);
        if (profile.ManuallyDisconnected) return new(profile, AndroidManagedConnectionState.ManuallyDisconnected, null);
        var boundApp = app?.Resolve(profile);
        var raw = CurrentRaw;
        if (raw == null) return boundApp == null ? new(profile, AndroidManagedConnectionState.Unknown, null)
            : new(profile, AndroidManagedConnectionState.Connected, boundApp);
        var matches = raw.Where(d => AndroidDeviceIdentity.MatchesSavedDevice(saved, d)).ToArray();
        var live = matches.Where(d => d.State == "device" && VerifiedMatch(saved, d)
            && !IsAmbiguous(d, raw) && !store.IsRemoved(d) && !store.HasConflictingIdentity(d))
            .OrderBy(d => AndroidDeviceIdentity.SameTransport(d, avoidTransport))
            .ThenByDescending(d => d.Serial == saved.Serial).ThenBy(d => d.IsWifi).ThenBy(d => d.Serial, StringComparer.Ordinal).FirstOrDefault();
        if (live != null) return new(profile, AndroidManagedConnectionState.Connected,
            profile.PreferAppTransport && boundApp != null ? boundApp : live);
        if (matches.Any(d => d.State == "device" && store.IsRemoved(d))) return new(profile, AndroidManagedConnectionState.IdentityConflict, null);
        if (matches.Any(d => d.State == "device") || raw.Any(d => d.State == "device" && d.Serial == saved.Serial))
            return new(profile, AndroidManagedConnectionState.IdentityConflict, null);
        if (boundApp != null) return new(profile, AndroidManagedConnectionState.Connected, boundApp);
        return new(profile, matches.Any(d => d.State == "unauthorized") ? AndroidManagedConnectionState.Unauthorized : AndroidManagedConnectionState.Offline, null);
    }

    internal AndroidDevice? Resolve(AndroidDevice? requested) => Get(requested).Device;
    internal AndroidDevice? ResolveKey(string key) => store.Profiles
        .Where(p => p.LastDevice != null && AndroidDeviceIdentity.Key(p.LastDevice) == key)
        .Select(p => Resolve(p.LastDevice)).OfType<AndroidDevice>().FirstOrDefault();
    internal IReadOnlyList<AndroidDevice> OnlineDevices => store.Profiles.Select(p => Resolve(p.LastDevice)).OfType<AndroidDevice>().ToArray();

    internal bool IsCurrent(AndroidDevice device)
    {
        if (device.IsApp) return ReferenceEquals(Get(device).Device, device);
        // Existing operations stay on their admitted transport, even if another alias
        // is now preferred for new operations. Never use an unverified replacement.
        var admission = Get(device);
        return admission.Device is { IsApp: false } && admission.Profile?.LastDevice is { } saved
            && CurrentRaw is { } raw && raw.Any(d => d.State == "device" && AndroidDeviceIdentity.SameTransport(device, d)
                && device.ConnectionTarget == d.ConnectionTarget
                && VerifiedMatch(saved, d) && !IsAmbiguous(d, raw) && !store.IsRemoved(d) && !store.HasConflictingIdentity(d));
    }

    internal bool AcceptRecovery(AndroidConnectionResult result, string profileId, long expectedRevision)
    {
        bool StillCurrent() => Revision == expectedRevision && ReferenceEquals(result.Devices, CurrentRaw)
            && IsCurrent(result.Device) && store.FindProfile(result.Device)?.Id == profileId;
        string? endpoint = result.Device.IsWifi ? AndroidPhoneProfile.WirelessAddress(result.Device.Serial)
            ?? AndroidPhoneProfile.WirelessAddress(result.WirelessAddress) : null;
        return endpoint == null ? StillCurrent() : store.RememberRecoveredAddress(profileId, endpoint, StillCurrent);
    }

    internal bool CanBindApp(AndroidDevice device) => app?.Session(device)?.Capabilities.Allows(SrapSession.AliasCapability) == true;

    internal async Task BindAppAsync(string adbId, AndroidDevice adbDevice, string appId, AndroidDevice appDevice,
        bool keepAdbSettings, CancellationToken cancel)
    {
        if (adb == null || app == null || !AdbClient.SupportsAliasForward(adbDevice)) throw new SrapException("unsupported");
        var session = app.Session(appDevice) ?? throw new SrapException("stale_session");
        long revision = store.LibraryRevision;
        var attachment = await adb.CaptureAliasAttachmentAsync(adbDevice, cancel).ConfigureAwait(false);
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        void Validate()
        {
            if (Get(adbDevice).State == AndroidManagedConnectionState.IdentityConflict) throw new SrapException("identity_conflict");
            if (revision != store.LibraryRevision || !attachment.IsConnected || !IsCurrent(adbDevice)
                || store.FindProfile(adbDevice)?.Id != adbId || store.FindProfile(appDevice)?.Id != appId
                || !ReferenceEquals(app.Session(appDevice), session)) throw new SrapException("stale_session");
        }
        void Lost() { try { scope.Cancel(); } catch (ObjectDisposedException) { } }
        void OnChange()
        {
            try { Validate(); }
            catch (SrapException) { Lost(); }
        }
        var source = discovery();
        source.Changed += OnChange;
        store.Changed += OnChange;
        attachment.Disconnected += Lost;
        try
        {
            Validate();
            await app.BindAliasAsync(session, appDevice.Serial, adbId, attachment, Validate,
                check => store.MergeVerifiedAppAlias(adbId, appId, appDevice.Serial, revision, keepAdbSettings, check, migrateApp),
                scope.Token).ConfigureAwait(false);
        }
        catch (SrapException ex) when (ex.Code == "canceled" && !cancel.IsCancellationRequested) { Validate(); throw; }
        finally
        {
            attachment.Disconnected -= Lost;
            store.Changed -= OnChange;
            source.Changed -= OnChange;
        }
        Revoke(appId);
    }

    internal void SelectAppTransport(string id, string fingerprint, bool? preferApp)
    {
        store.SetAppTransportPreference(id, fingerprint, preferApp);
        Revoke(id);
        if (preferApp == null) app?.Close(id);
    }

    internal CancellationToken OperationToken(AndroidDevice device)
    {
        if (device.IsApp) return IsCurrent(device) && app?.Session(device) is { } session ? session.Token : new CancellationToken(true);
        long revision = Revision;
        var current = Get(device);
        if (current.Profile == null) return new CancellationToken(true);
        lock (_operationLifetimes)
        {
            if (!IsCurrent(device) || revision != Revision) return new CancellationToken(true);
            if (!_operationLifetimes.TryGetValue(current.Profile.Id, out var lifetime))
                _operationLifetimes[current.Profile.Id] = lifetime = new();
            return lifetime.Token;
        }
    }

    private void Revoke(string profileId)
    {
        CancellationTokenSource? lifetime;
        lock (_operationLifetimes) _operationLifetimes.Remove(profileId, out lifetime);
        lifetime?.Cancel();
    }

    internal bool AwaitingDiscovery(AndroidDevice device) =>
        CurrentRaw == null && Get(device) is { Profile: not null, State: AndroidManagedConnectionState.Unknown };

    private static IEnumerable<string> RetirementKeys(AndroidDevice device) =>
        [AndroidDeviceIdentity.Key(device), (device.IsApp ? "app-serial:" : "serial:") + device.Serial];
    private bool IsRetiring(AndroidDevice device)
    { lock (_retiring) return RetirementKeys(device).Any(_retiring.ContainsKey); }
    internal void BeginRetirement(IEnumerable<AndroidDevice> aliases)
    {
        lock (_retiring)
            foreach (string key in aliases.SelectMany(RetirementKeys).Distinct(StringComparer.Ordinal))
                _retiring[key] = _retiring.GetValueOrDefault(key) + 1;
    }
    internal void CompleteRetirement(IEnumerable<AndroidDevice> aliases)
    {
        lock (_retiring)
            foreach (string key in aliases.SelectMany(RetirementKeys).Distinct(StringComparer.Ordinal))
                if (_retiring.TryGetValue(key, out int count))
                {
                    if (count == 1) _retiring.Remove(key);
                    else _retiring[key] = count - 1;
                }
    }

    internal void ObserveCurrent()
    {
        var raw = CurrentRaw;
        if (raw == null) return;
        long revision = Revision;
        var profiles = store.Profiles;
        var safe = raw.Where(d => d.State == "device" && !IsRetiring(d) && !IsAmbiguous(d, raw) && !store.IsRemoved(d) && !store.HasConflictingIdentity(d)
            && AndroidDeviceIdentity.StableKey(d) != null
            && (d.ConnectionTarget == null || d.HardwareId != null)
            && ((!d.IsWifi && !store.HasUnresolvedRemovals && !profiles.Any(p => p.LastDevice != null && AndroidDeviceIdentity.StableKey(p.LastDevice) == null))
                || profiles.Any(p => AndroidDeviceIdentity.SamePhysicalDevice(p.LastDevice, d)))
            && !profiles.Any(p => p.LastDevice is { } saved && (
                // Endpoint-only legacy settings require an explicit rebind; passive
                // enrichment cannot silently resurrect archived preferences.
                saved.Serial == d.Serial && AndroidDeviceIdentity.StableKey(saved) == null
                || AndroidDeviceIdentity.Key(saved) == AndroidDeviceIdentity.Key(d) && !VerifiedMatch(saved, d))))
            .GroupBy(AndroidDeviceIdentity.Key, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(d => profiles.Any(p => AndroidDeviceIdentity.SamePhysicalDevice(p.LastDevice, d)
                && p.LastDevice!.Serial == d.Serial)).ThenBy(d => d.IsWifi).ThenBy(d => d.Serial, StringComparer.Ordinal).First()).ToArray();
        store.ObserveManagedDevices(safe, revision, () => ReferenceEquals(raw, CurrentRaw));
    }

    internal bool Admit(AndroidConnectionResult result, long expectedRevision, string? expectedProfileId = null, bool requireCurrent = false)
    {
        var live = result.Device;
        if (live.State != "device" || IsRetiring(live) || IsAmbiguous(live, result.Devices)
            || live.ConnectionTarget != null && live.HardwareId == null
            || !result.Devices.Any(d => d.State == "device" && AndroidDeviceIdentity.SameTransport(d, live))) return false;
        AndroidDevice? alias = null;
        string? selected = expectedProfileId;
        if (expectedProfileId == null && AndroidDeviceIdentity.StableKey(live) != null)
        {
            var unresolved = store.Profiles.Where(p => p.LastDevice != null && AndroidDeviceIdentity.StableKey(p.LastDevice) == null)
                .Select(p => (Profile: p, Alias: result.Devices.FirstOrDefault(d => d.State == "device"
                    && d.Serial == p.LastDevice!.Serial && AndroidDeviceIdentity.SamePhysicalDevice(d, live)
                    && !IsAmbiguous(d, result.Devices)))).Where(x => x.Alias != null).ToArray();
            if (unresolved.Length > 1) return false;
            if (unresolved.Length == 1) { expectedProfileId = unresolved[0].Profile.Id; alias = unresolved[0].Alias; }
        }
        return store.BindExplicit(live, expectedProfileId, expectedRevision, migrate, alias,
            requireCurrent ? () => ReferenceEquals(result.Devices, CurrentRaw) && !IsRetiring(live)
                && (selected == null || store.Profile.Id == selected) : null, result.WirelessAddress);
    }

    internal bool Remove(string profileId, out IReadOnlyList<AndroidDevice> aliases)
    {
        var saved = store.Profiles.FirstOrDefault(p => p.Id == profileId)?.LastDevice;
        if (saved == null) { aliases = []; return false; }
        // Capture all verified aliases for both tombstones and owned-session cleanup.
        aliases = saved.IsApp ? [saved] : (CurrentRaw ?? discovery().Devices ?? []).Where(d => VerifiedMatch(saved, d)
            || saved.ConnectionTarget != null && AndroidDeviceIdentity.SameTransport(saved, d)).Append(saved).Distinct().ToArray();
        BeginRetirement(aliases);
        if (!saved.IsApp) discovery().RetirePendingScan();
        try
        {
            if (!store.RemoveDevice(profileId, aliases)) { CompleteRetirement(aliases); return false; }
        }
        catch { CompleteRetirement(aliases); throw; }
        Revoke(profileId);
        return true;
    }

    internal void SetDisconnected(AndroidDevice device, bool value)
    {
        if (value && !device.IsApp) discovery().RetirePendingScan();
        store.SetDisconnected(device, value);
        if (value && store.FindProfile(device) is { } profile) Revoke(profile.Id);
    }
}
