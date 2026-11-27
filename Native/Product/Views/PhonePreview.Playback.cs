#if SONICROUTE_PHONE
using System.Threading.Channels;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute.Native.Product;

internal sealed partial class PhonePreview
{
    private void OnPlaybackDevicesChanged()
    {
        QueueSourceReconciliation(refreshRoutes: true);
        tryGetAirPlay?.Invoke()?.QueueOutputRecovery();
        if (!token.IsCancellationRequested && SavedPlaybackStore.ReadAll().Values.Any(
            choice => choice.Outputs.Any(AndroidOutputIdentity.IsPhoneOutput)))
            AppPlaybackRestore.RefreshDevices();
        Notify();
    }

    private void UpdatePlaybackConnections()
    {
        IReadOnlyList<AndroidDevice> previous;
        lock (operationsGate)
        {
            var online = Connections.OnlineDevices;
            if (online.SequenceEqual(lastOnline)) return;
            previous = lastOnline;
            lastOnline = online;
        }
        if (LocalAppOutputMirrors.HasPhoneOutputs)
        {
            foreach (var device in previous)
            {
                var state = Connections.Get(device).State;
                if (state is AndroidManagedConnectionState.Offline or AndroidManagedConnectionState.Unauthorized
                    or AndroidManagedConnectionState.IdentityConflict)
                    _ = RetireOfflineOutputsAsync(device);
            }
        }
        OnPlaybackDevicesChanged();
    }

    private static async Task RetireOfflineOutputsAsync(AndroidDevice device)
    {
        try { await LocalAppOutputMirrors.PhoneRemovedAsync(device, recover: true).ConfigureAwait(false); }
        catch (Exception error) { ExceptionDiagnostics.Record("PlaybackRestore.Disconnect", error); }
    }

    private async Task RefreshSavedTransportsAsync(CancellationToken cancel)
    {
        try { await Discovery.ReadAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { ExceptionDiagnostics.Record("PlaybackRestore.Discovery", error); }
    }

    // Only requested for a running Windows app with a saved, currently unavailable phone output.
    internal async Task PrepareSavedPhoneOutputsAsync(IEnumerable<string> ids, CancellationToken cancel)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, cancel);
        var requested = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (!AndroidOutputIdentity.TryParseDeviceId(id, out string key, out _)
                || !key.StartsWith("device:", StringComparison.Ordinal)) continue;
            if (!requested.TryGetValue(key, out var sinks)) requested[key] = sinks = new(StringComparer.OrdinalIgnoreCase);
            sinks.Add(id);
        }
        var saved = Store.Profiles.Where(p => p.LastDevice != null && !p.ManuallyDisconnected
            && requested.ContainsKey(AndroidDeviceIdentity.Key(p.LastDevice))).ToArray();
        if (saved.Length == 0) return;

        var devices = await Discovery.ReadAsync(stop.Token).ConfigureAwait(false);
        foreach (var profile in saved)
        {
            if (await RecoverMissingWirelessAsync(profile.LastDevice, devices, stop.Token).ConfigureAwait(false))
                devices = Discovery.Devices ?? devices;
        }
        await ProbeOutputsIfConnectedAsync(saved.Select(p => p.LastDevice!), token: stop.Token).ConfigureAwait(false);

        // All requests share the existing track-devices connection. Release it once the saved
        // targets are ready unless the panel, clipboard or another product feature took ownership.
        var waits = saved.Select(p => WaitForSavedSinksAsync(p.Id,
            requested[AndroidDeviceIdentity.Key(p.LastDevice!)], stop.Token)).ToArray();
        if (waits.All(task => task.IsCompleted)) { await Task.WhenAll(waits).ConfigureAwait(false); return; }
        lock (operationsGate) { ++temporaryTrackers; EnsureTracking(); }
        try { await Task.WhenAll(waits).ConfigureAwait(false); }
        finally
        {
            Task? retiring = null;
            lock (operationsGate)
            {
                if (--temporaryTrackers == 0 && !keepTracking && trackingLifetime != null)
                {
                    var current = trackingLifetime;
                    trackingLifetime = null;
                    retiring = tracking;
                    current.Cancel();
                }
            }
            if (retiring != null) await retiring.ConfigureAwait(false);
        }
    }

    private async Task WaitForSavedSinksAsync(string profileId, HashSet<string> sinks, CancellationToken cancel)
    {
        var changes = Channel.CreateBounded<bool>(1);
        void Wake() => changes.Writer.TryWrite(true);
        Discovery.Changed += Wake;
        Store.Changed += Wake;
        AndroidDevice? listening = null;
        CancellationTokenSource? listener = null;
        Task watch = Task.CompletedTask;
        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var profile = Store.Profiles.FirstOrDefault(p => p.Id == profileId);
                if (profile?.LastDevice == null || profile.ManuallyDisconnected) return;
                var online = Connections.Resolve(profile.LastDevice);
                if (online != null && sinks.All(id => PcRoutingApi.ResolvePersistentOutputId(id) != null)) return;
                // Current and speaker wake on connection changes. A missing accessory
                // additionally needs Android's audio-device notification while its phone is online.
                if (!Equals(online, listening))
                {
                    listener?.Cancel();
                    await watch.ConfigureAwait(false);
                    listener?.Dispose();
                    listener = null;
                    listening = online;
                    if (online != null && sinks.Any(AndroidOutputIdentity.IsPhoneAccessoryOutput))
                    {
                        listener = CancellationTokenSource.CreateLinkedTokenSource(cancel, Connections.OperationToken(online));
                        watch = WatchSavedSinksAsync(online, listener.Token);
                    }
                }
                await changes.Reader.ReadAsync(cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            Discovery.Changed -= Wake;
            Store.Changed -= Wake;
            listener?.Cancel();
            try { await watch.ConfigureAwait(false); }
            finally { listener?.Dispose(); }
        }
    }

    private async Task WatchSavedSinksAsync(AndroidDevice device, CancellationToken cancel)
    {
        try
        {
            if (device.IsApp) return;
            await Operations.OpenMediaSession(device).WatchAsync(device, state =>
            {
                if (Connections.IsCurrent(device)) Store.UpdateOutputs(device, state.Outputs);
            }, cancel, measureLatency: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) { ExceptionDiagnostics.Record("PlaybackRestore.PhoneAccessory", error); }
    }
}

internal sealed partial class ApplicationHost
{
    private async Task PrepareSavedPhoneOutputsAsync(IEnumerable<string> ids, CancellationToken cancel)
    {
        var manager = await OnOwner(() =>
        {
            cancel.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Phone;
        }).WaitAsync(cancel).ConfigureAwait(false);
        await manager.PrepareSavedPhoneOutputsAsync(ids, cancel).ConfigureAwait(false);
    }
}
#endif
