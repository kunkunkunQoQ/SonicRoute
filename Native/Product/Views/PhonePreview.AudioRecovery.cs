#if SONICROUTE_PHONE
using System.Net.NetworkInformation;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed partial class PhonePreview
{
    private readonly object sourceRecoveryGate = new();
    private readonly Dictionary<string, (AndroidDevice Device, string? Grant)> captureAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AndroidDevice Device, string Grant)> phonePlaybackRequests = new(StringComparer.Ordinal);
    private Task sourceRecovery = Task.CompletedTask;
    private bool sourceRecoveryRunning, sourceRecoveryPending, sourceRoutesChanged;
    private readonly object wirelessWatchGate = new();
    private readonly Dictionary<string, (string Address, long Time)> wirelessAttempts = new(StringComparer.Ordinal);
    private AndroidDevice[] offlineWirelessPhones = [];
    private CancellationTokenSource? wirelessWatch;
    private Task wirelessWatchTask = Task.CompletedTask;
    private bool watchingWirelessNetwork;

    private void QueueSourceReconciliation() => QueueSourceReconciliation(refreshRoutes: false);

    internal void QueueSourceReconciliation(bool refreshRoutes)
    {
        lock (sourceRecoveryGate)
        {
            if (token.IsCancellationRequested) return;
            sourceRecoveryPending = true;
            sourceRoutesChanged |= refreshRoutes;
            if (sourceRecoveryRunning) return;
            sourceRecoveryRunning = true;
            sourceRecovery = Task.Run(RestoreCaptureSourcesAsync);
        }
    }

    private Task WaitForSourceReconciliationAsync()
    {
        lock (sourceRecoveryGate) return sourceRecovery;
    }

    private async Task RestoreCaptureSourcesAsync()
    {
        while (true)
        {
            bool refreshRoutes;
            lock (sourceRecoveryGate)
            {
                if (token.IsCancellationRequested || !sourceRecoveryPending)
                {
                    sourceRecoveryRunning = false;
                    return;
                }
                sourceRecoveryPending = false;
                refreshRoutes = sourceRoutesChanged;
                sourceRoutesChanged = false;
            }
            try { await ReconcileCaptureSourcesAsync(refreshRoutes).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.Resume", error); }
        }
    }

    private async Task ReconcileCaptureSourcesAsync(bool refreshRoutes)
    {
        StartRequestedScreenViewers();
        await StartPhoneRequestedPlaybackAsync().ConfigureAwait(false);
        if (Discovery.CurrentDevices == null) return;
        await Sources.SuspendUnavailableAsync(Connections.IsCurrent).ConfigureAwait(false);
        foreach (var (key, attempt) in captureAttempts.ToArray())
            if (!Connections.IsCurrent(attempt.Device)) captureAttempts.Remove(key);

        var saved = Settings.ExternalAudio;
        var legacy = Settings.GetSavedSources();
        var keys = saved.Where(p => p.App != null && p.Resume).Select(p => p.DeviceKey!).Concat(legacy.Select(p => p.DeviceKey))
            .ToHashSet(StringComparer.Ordinal);
        UpdateWirelessWatch(Store.Profiles.Where(p => !p.ManuallyDisconnected
            && p.WirelessReconnectTarget(discoverFromUsb: false) != null
            && AndroidDeviceIdentity.StableKey(p.LastDevice) is { } key && keys.Contains(key)
            && Connections.Get(p.LastDevice).State == AndroidManagedConnectionState.Offline)
            .Select(p => p.LastDevice!).ToArray());
        foreach (var choice in saved)
        {
            if (!choice.Resume || choice.App == null || Connections.ResolveKey(choice.DeviceKey!) is not { } online) continue;
            var live = Sources.Sources.FirstOrDefault(s => s.App.Package == choice.App.Package && AndroidDeviceIdentity.Matches(s.Device, online));
            if (live == null)
                await ResumeCaptureAsync(choice.Key, online, choice.App, choice.Outputs).ConfigureAwait(false);
            else if (refreshRoutes && live.Session.State.State == CaptureState.Receiving && live.Session.CanChangeActiveOutputs)
            {
                var available = ExternalAudioBridge.ResolveAvailableOutputs(choice.Outputs);
                if (!live.Session.CurrentSelection().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(available))
                {
                    try
                    {
                        await live.Session.RestoreOutputsAsync(available, () =>
                            ReferenceEquals(live, Sources.FindSource(live.Id)) &&
                            Settings.FindExternalAudio(choice.Key) is { Resume: true } current &&
                            ReferenceEquals(current.Outputs, choice.Outputs)).ConfigureAwait(false);
                    }
                    catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.RestoreOutputs", error); }
                }
            }
        }

        // Older builds only wrote resume intent for explicit multi-output routes.
        foreach (var old in legacy)
        {
            string key = old.DeviceKey + "\n" + old.Package;
            if (Settings.FindExternalAudio(key) != null || Connections.ResolveKey(old.DeviceKey) is not { } online) continue;
            var app = old.Package == AndroidApp.DeviceAudioPackage ? AndroidApp.DeviceAudio
                : (!online.IsApp ? Store.FindProfile(online)?.RetainedApps.FirstOrDefault(item => item.Package == old.Package) : null)
                    ?? new AndroidApp(old.Package, old.Package, -1, "");
            await ResumeCaptureAsync(key, online, app, old.Outputs).ConfigureAwait(false);
        }
    }

    private async Task StartPhoneRequestedPlaybackAsync()
    {
        var profiles = Store.Profiles;
        foreach (var id in phonePlaybackRequests.Keys.ToArray())
            if (!profiles.Any(p => p.Id == id && ReferenceEquals(appTransport.Resolve(p), phonePlaybackRequests[id].Device)))
                phonePlaybackRequests.Remove(id);
        foreach (var profile in profiles)
        {
            if (appTransport.Resolve(profile) is not { } device || !Connections.IsCurrent(device)) continue;
            if (Operations.AppCapture(device) is not { PlayOnComputer: true, Grant: { } grant, LocalSourceKey: { } key } permission) continue;
            if (phonePlaybackRequests.TryGetValue(profile.Id, out var previous) && ReferenceEquals(previous.Device, device)
                && previous.Grant == grant) continue;
            phonePlaybackRequests[profile.Id] = (device, grant);
            if (Sources.Sources.Any(source => AndroidDeviceIdentity.Matches(source.Device, device)
                && source.App.Package == key && source.Session.State.State != CaptureState.Failed)) continue;
            try
            {
                var app = permission.IsDeviceMedia ? AndroidApp.DeviceAudio : new AndroidApp(key, key, 0, "");
                if (Operations.AppCapture(device) != permission || !Connections.IsCurrent(device)) continue;
                var preferences = Settings.FindExternalAudio(ExternalAudioPreference.AndroidKey(device, app.Package));
                var selected = preferences?.Outputs ?? Settings.Get(device, app.Package).ResumeOutputs ?? [null];
                var local = ExternalAudioBridge.ResolveAvailableOutputs(selected)
                    .Where(id => id != AndroidAudioSession.SourcePhoneOutputId && !AndroidOutputIdentity.IsPhoneOutput(id)).ToArray();
                await Sources.PlayPhoneRequestAsync(device, app, local.Length == 0 ? [null] : local).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.StartSharedAudio", error); }
        }
    }

    private async Task ResumeCaptureAsync(string key, AndroidDevice online, AndroidApp app, string?[] outputs)
    {
        token.ThrowIfCancellationRequested();
        var grant = online.IsApp ? Operations.AppCapture(online) : null;
        if (online.IsApp && (grant?.LocalSourceKey != app.Package
            || !outputs.Contains(AndroidAudioSession.SourcePhoneOutputId))) return;
        if (Sources.Sources.Any(s => AndroidDeviceIdentity.Matches(s.Device, online)
            && (s.App.Package == app.Package || s.App.IsDeviceAudio || app.IsDeviceAudio))) return;
        if (captureAttempts.TryGetValue(key, out var attempted) && AndroidDeviceIdentity.SameTransport(attempted.Device, online)
            && attempted.Grant == grant?.Grant) return;
        captureAttempts[key] = (online, grant?.Grant);
        try { await Sources.ResumeAsync(online, app, ExternalAudioBridge.ResolveAvailableOutputs(outputs)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.Resume", error); }
    }

    private void UpdateWirelessWatch(AndroidDevice[] offline, bool restart = false)
    {
        lock (wirelessWatchGate)
        {
            if (token.IsCancellationRequested || Volatile.Read(ref closed) != 0) return;
            offlineWirelessPhones = offline;
            bool needWatch = offline.Length != 0;
            if (watchingWirelessNetwork != needWatch)
            {
                watchingWirelessNetwork = needWatch;
                if (needWatch) NetworkChange.NetworkAddressChanged += OnRecoveryNetworkChanged;
                else NetworkChange.NetworkAddressChanged -= OnRecoveryNetworkChanged;
            }
            if (!needWatch || restart)
            {
                wirelessAttempts.Clear();
                wirelessWatch?.Cancel();
                wirelessWatch = null;
            }
            if (!needWatch || wirelessWatch != null) return;
            var lifetime = wirelessWatch = CancellationTokenSource.CreateLinkedTokenSource(token);
            var previous = wirelessWatchTask;
            wirelessWatchTask = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    if (!lifetime.IsCancellationRequested)
                        await adbDiscovery.WatchSavedAsync(s => OnWirelessAdvertisement(s, lifetime.Token), lifetime.Token)
                            .ConfigureAwait(false);
                }
                catch (Exception error) { ExceptionDiagnostics.Record("NativePhone.RecoveryDiscovery", error); }
                finally
                {
                    lock (wirelessWatchGate)
                    {
                        if (ReferenceEquals(wirelessWatch, lifetime)) wirelessWatch = null;
                        lifetime.Dispose();
                    }
                }
            });
        }
    }

    private void OnRecoveryNetworkChanged(object? sender, EventArgs args)
    {
        lock (wirelessWatchGate)
            if (watchingWirelessNetwork) UpdateWirelessWatch(offlineWirelessPhones, restart: true);
    }

    private async Task OnWirelessAdvertisement(AndroidMdnsService service, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested || AndroidDeviceIdentity.WirelessServiceIdentity(service) is not { } advertised) return;
        AndroidDevice? device;
        lock (wirelessWatchGate)
        {
            device = offlineWirelessPhones.FirstOrDefault(d => AndroidDeviceIdentity.SamePhysicalDevice(d, advertised));
            if (device == null) return;
            long now = Environment.TickCount64;
            string key = AndroidDeviceIdentity.Key(device);
            if (wirelessAttempts.TryGetValue(key, out var previous) && service.Address == previous.Address && now - previous.Time < 20000) return;
            wirelessAttempts[key] = (service.Address, now);
        }
        if (Discovery.CurrentDevices is { } devices)
            await RecoverMissingWirelessAsync(device, devices, cancel, advertisedAddress: service.Address).ConfigureAwait(false);
    }

    private void StopWirelessWatch()
    {
        Task watch;
        lock (wirelessWatchGate)
        {
            if (watchingWirelessNetwork) NetworkChange.NetworkAddressChanged -= OnRecoveryNetworkChanged;
            watchingWirelessNetwork = false;
            wirelessWatch?.Cancel();
            offlineWirelessPhones = [];
            watch = wirelessWatchTask;
        }
        watch.GetAwaiter().GetResult();
    }
}
#endif
