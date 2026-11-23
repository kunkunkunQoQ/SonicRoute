#if SONICROUTE_PHONE
using System.IO;
using System.Threading.Channels;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal static partial class AndroidAudioHost
{
    private static readonly Lazy<Task> SavedSourceStartup = new(() => Task.Run(RestoreSavedSourcesAsync));

    internal static void StartSavedSourcePlayback() => _ = SavedSourceStartup.Value;

    internal static string? GetPersistentOutputId(string id)
    {
        if (!AndroidOutputIdentity.IsPhoneOutput(id)) return id;
        if (!AndroidOutputIdentity.TryParseDeviceId(id, out var key, out var sink)) return null;
        if (key.StartsWith("device:", StringComparison.Ordinal) && key.Length > 7) return id;
        // A wireless IP/port is not a device identity. Only upgrade it with a verified live serial.
        if (!key.StartsWith("endpoint:", StringComparison.Ordinal)) return null;
        var device = PhoneAudioWindowCache.Manager.ResolveKey(key);
        return AndroidDeviceIdentity.StableKey(device) is { } stable ? AndroidOutputIdentity.FormatDeviceId(stable, sink) : null;
    }

    internal static string? ResolvePersistentOutputId(string id)
    {
        if (!AndroidOutputIdentity.IsPhoneOutput(id)) return id;
        if (!AndroidOutputIdentity.TryParseDeviceId(id, out var key, out var sink) || !LazyPhone.IsValueCreated) return null;
        var device = PhoneAudioWindowCache.Manager.ResolveKey(key);
        if (device == null || Phone.FindProfile(device) is not { } profile) return null;
        if (AndroidOutputIdentity.IsPhoneCurrentOutput(id))
            return AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), AndroidOutputSink.Current.Key);
        var available = profile.OutputSinks.FirstOrDefault(s => string.Equals(s.Key, sink, StringComparison.OrdinalIgnoreCase));
        return available == null ? null : AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(device), available.Key);
    }

    private static HashSet<string> PhoneKeys(IEnumerable<string?> ids)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (AndroidOutputIdentity.TryParseDeviceId(id, out var key, out _) && key.StartsWith("device:", StringComparison.Ordinal) && key.Length > 7)
                keys.Add(key);
        return keys;
    }

    internal static async Task PrepareSavedPhoneOutputsAsync(IEnumerable<string> ids, CancellationToken token)
    {
        var outputs = ids.Where(AndroidOutputIdentity.IsPhoneOutput).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var phones = PhoneKeys(outputs);
        if (phones.Count == 0) return;
        await PrepareSavedPhonesAsync(phones, token).ConfigureAwait(false);
        var changed = Channel.CreateBounded<bool>(1);
        void Wake() => changed.Writer.TryWrite(true);
        ApplicationAudio.OutputsChanged += Wake;
        Phone.Changed += Wake;
        try
        {
            bool Missing() => outputs.Any(id => AndroidOutputIdentity.TryParseDeviceId(id, out var key, out _)
                && Phone.Profiles.Any(p => AndroidDeviceIdentity.StableKey(p.LastDevice) == key) && ResolvePersistentOutputId(id) == null);
            if (!Missing()) return;
            await using var watch = SavedTransportWatch.Subscribe(phones);
            await using var bluetooth = new SavedBluetoothWatches();
            while (Missing())
            {
                await bluetooth.UpdateAsync(outputs).ConfigureAwait(false);
                await changed.Reader.ReadAsync(token).ConfigureAwait(false);
            }
        }
        finally { Phone.Changed -= Wake; ApplicationAudio.OutputsChanged -= Wake; }
    }

    // Only monitor an online phone if a saved, specifically selected Bluetooth output is missing.
    // Both native and Android sources can wait for the same accessory with one phone-side listener.
    private sealed class SavedBluetoothWatches : IAsyncDisposable
    {
        private sealed class Listener(AndroidDevice device)
        {
            internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(MediaShutdown.Token,
                PhoneAudioWindowCache.Manager.OperationToken(device));
            internal int Users;
            internal Task Watch = Task.CompletedTask;
            internal async Task RunAsync()
            {
                try
                {
                    var client = new AndroidMediaSessionClient(Adb);
                    await client.WatchAsync(device, state => Phone.UpdateOutputs(device, state.Outputs),
                        Stop.Token, measureLatency: false).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Saved Bluetooth output: " + ex.Message); }
            }
        }

        private static readonly Dictionary<string, Listener> Shared = new(StringComparer.Ordinal);
        private readonly HashSet<string> _owned = new(StringComparer.Ordinal);

        internal async Task UpdateAsync(IEnumerable<string?> outputs)
        {
            var keys = PhoneKeys(outputs.Where(id => AndroidOutputIdentity.IsPhoneBluetoothOutput(id)
                && ResolvePersistentOutputId(id!) == null));
            var online = PhoneAudioWindowCache.OnlineDevices.Where(d =>
                AndroidDeviceIdentity.StableKey(d) is { } key && keys.Contains(key)).ToArray();
            var wanted = online.Select(d => d.CommandTarget).ToHashSet(StringComparer.Ordinal);
            foreach (string serial in _owned.Where(s => !wanted.Contains(s)).ToArray())
                await ReleaseAsync(serial).ConfigureAwait(false);
            foreach (var device in online)
            {
                if (!_owned.Add(device.CommandTarget)) continue;
                lock (Shared)
                {
                    if (!Shared.TryGetValue(device.CommandTarget, out var entry))
                    {
                        Shared.Add(device.CommandTarget, entry = new Listener(device));
                        entry.Watch = Task.Run(entry.RunAsync);
                    }
                    entry.Users++;
                }
            }
        }

        private async Task ReleaseAsync(string serial)
        {
            _owned.Remove(serial);
            Listener entry;
            lock (Shared)
            {
                entry = Shared[serial];
                if (--entry.Users > 0) return;
                Shared.Remove(serial);
            }
            entry.Stop.Cancel();
            try { await entry.Watch.ConfigureAwait(false); }
            finally { entry.Stop.Dispose(); }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (string serial in _owned.ToArray()) await ReleaseAsync(serial).ConfigureAwait(false);
        }
    }

    private static async Task PrepareSavedPhonesAsync(HashSet<string> keys, CancellationToken token)
    {
        await PreparePhoneAsync(token).ConfigureAwait(false);
        var saved = Phone.Profiles.Select(p => p.LastDevice)
            .OfType<AndroidDevice>().Where(d => AndroidDeviceIdentity.StableKey(d) is { } key && keys.Contains(key)).ToArray();
        if (saved.Length == 0) return;
        var devices = await PhoneAudioWindowCache.ReadDevicesAsync(token).ConfigureAwait(false);
        foreach (var phone in saved)
            if (await PhoneAudioWindowCache.RecoverMissingWirelessAsync(phone, devices, token).ConfigureAwait(false))
                devices = PhoneAudioWindowCache.Devices ?? devices;
        await ProbeOutputsIfConnectedAsync(devices.Where(d => d.State == "device"
            && AndroidDeviceIdentity.StableKey(d) is { } key && keys.Contains(key)), token: token).ConfigureAwait(false);
    }

    // Source and Windows-app restoration share one transport notification subscription while either is waiting.
    private sealed class SavedTransportWatch : IAsyncDisposable
    {
        private static readonly HashSet<SavedTransportWatch> Users = [];
        private static CancellationTokenSource? _lifetime;
        private static Task? _tracking;
        private readonly HashSet<string> _keys;
        private SavedTransportWatch(HashSet<string> keys) => _keys = keys;
        internal static SavedTransportWatch Subscribe(HashSet<string> keys)
        {
            var item = new SavedTransportWatch(keys);
            lock (Users)
            {
                Users.Add(item);
                if (_lifetime == null)
                {
                    var lifetime = _lifetime = CancellationTokenSource.CreateLinkedTokenSource(MediaShutdown.Token);
                    _tracking = Task.Run(() => TrackAsync(lifetime.Token));
                }
            }
            return item;
        }

        private static async Task TrackAsync(CancellationToken token)
        {
            using var listenLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var changes = Channel.CreateBounded<bool>(1);
            var listen = Adb.TrackDevicesAsync(_ => changes.Writer.TryWrite(true), listenLifetime.Token);
            try
            {
                while (true)
                {
                    var changed = changes.Reader.ReadAsync(token).AsTask();
                    if (await Task.WhenAny(changed, listen).ConfigureAwait(false) == listen) break;
                    await changed.ConfigureAwait(false);
                    var devices = await PhoneAudioWindowCache.ReadDevicesAsync(token).ConfigureAwait(false);
                    HashSet<string> phones;
                    lock (Users) phones = Users.SelectMany(u => u._keys).ToHashSet(StringComparer.Ordinal);
                    await ProbeOutputsIfConnectedAsync(devices.Where(d => AndroidDeviceIdentity.StableKey(d) is { } key
                        && phones.Contains(key)), token: token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Saved playback device watch: " + ex.Message); }
            finally
            {
                listenLifetime.Cancel();
                try { await listen.ConfigureAwait(false); }
                catch (OperationCanceledException) when (listenLifetime.IsCancellationRequested) { }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Saved playback ADB notifications: " + ex.Message); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            CancellationTokenSource? lifetime;
            Task? tracking;
            lock (Users)
            {
                if (!Users.Remove(this) || Users.Count != 0) return;
                lifetime = _lifetime; tracking = _tracking;
                _lifetime = null; _tracking = null;
            }
            try { lifetime?.Cancel(); if (tracking != null) await tracking.ConfigureAwait(false); }
            finally { lifetime?.Dispose(); }
        }
    }

    private sealed class PendingPhoneSource(AndroidSavedSourceRoute route)
    {
        internal readonly AndroidSavedSourceRoute Route = route;
        internal AndroidAudioSource? Source;
        internal string?[] LastOutputs = [];
    }

    private static async Task RestoreSavedSourcesAsync()
    {
        try
        {
            var token = MediaShutdown.Token;
            if (token.IsCancellationRequested || !File.Exists(Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "android-audio.json"))) return;
            var settings = await PrepareStreamingAsync(token).ConfigureAwait(false);
            var pending = settings.GetSavedSources().Select(r => new PendingPhoneSource(r)).ToList();
            if (pending.Count == 0) return;
            var phones = PhoneKeys(pending.SelectMany(p => p.Route.Outputs));
            phones.UnionWith(pending.Select(p => p.Route.DeviceKey));
            await PrepareSavedPhonesAsync(phones, token).ConfigureAwait(false);

            var changes = Channel.CreateBounded<bool>(1);
            void Wake() => changes.Writer.TryWrite(true);
            ApplicationAudio.OutputsChanged += Wake;
            settings.SavedSourcesChanged += Wake;
            Phone.Changed += Wake;
            await using var bluetooth = new SavedBluetoothWatches();
            SavedTransportWatch? watch = null;
            IDisposable? localWatch = null;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var devices = PhoneAudioWindowCache.Devices ?? [];
                    var local = AudioService.GetDevices(EDataFlow.eRender).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in pending.ToArray())
                    {
                        var route = entry.Route;
                        var profile = Phone.Profiles.FirstOrDefault(p => AndroidDeviceIdentity.StableKey(p.LastDevice) == route.DeviceKey);
                        if (profile?.LastDevice == null || profile.ManuallyDisconnected) { pending.Remove(entry); continue; }
                        bool StillSelected() => settings.Get(profile.LastDevice, route.Package).ResumeOutputs is { } saved
                            && saved.SequenceEqual(route.Outputs, StringComparer.OrdinalIgnoreCase);
                        if (!StillSelected() || (entry.Source != null && !ReferenceEquals(Sources.FindSource(entry.Source.Id), entry.Source)))
                        { pending.Remove(entry); continue; }
                        var device = PhoneAudioWindowCache.Manager.ResolveKey(route.DeviceKey);
                        if (device == null) continue;
                        var outputs = new List<string?>();
                        foreach (var id in route.Outputs)
                        {
                            if ((id == null && local.Count != 0) || id == AndroidAudioSession.SourcePhoneOutputId || (id != null && local.Contains(id)))
                                outputs.Add(id);
                            else if (id != null && AndroidOutputIdentity.IsPhoneOutput(id) && ResolvePersistentOutputId(id) is { } phoneId)
                                outputs.Add(phoneId);
                            else if (id != null && WindowsLinkHub.IsOutputAvailable(id, fromPhone: true))
                                outputs.Add(id);
                        }
                        if (!outputs.Any(id => id != AndroidAudioSession.SourcePhoneOutputId)
                            || entry.LastOutputs.SequenceEqual(outputs, StringComparer.OrdinalIgnoreCase)) continue;
                        entry.LastOutputs = outputs.ToArray();
                        try
                        {
                            if (entry.Source == null)
                            {
                                var app = route.Package == AndroidApp.DeviceAudioPackage ? AndroidApp.DeviceAudio
                                    : (await Adb.AppsAsync(device, token, [route.Package]).ConfigureAwait(false)).FirstOrDefault(a => a.Package == route.Package);
                                if (app == null) { pending.Remove(entry); continue; }
                                Task<AndroidAudioSource> start;
                                lock (PhoneOperations)
                                {
                                    token.ThrowIfCancellationRequested();
                                    if (!StillSelected() || !PhoneAudioWindowCache.Manager.IsCurrent(device)) { pending.Remove(entry); continue; }
                                    start = Sources.ResumeAsync(device, app, entry.LastOutputs);
                                }
                                entry.Source = await start.ConfigureAwait(false);
                            }
                            else await entry.Source.Session.RestoreOutputsAsync(entry.LastOutputs, StillSelected).ConfigureAwait(false);
                            if (outputs.Count == route.Outputs.Length) pending.Remove(entry);
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        { System.Diagnostics.Trace.WriteLine("Restore phone playback: " + ex.Message); pending.Remove(entry); }
                    }
                    if (pending.Count == 0) break;
                    watch ??= SavedTransportWatch.Subscribe(phones);
                    await bluetooth.UpdateAsync(pending.SelectMany(p => p.Route.Outputs)).ConfigureAwait(false);
                    if (localWatch == null && pending.Any(p => p.Route.Outputs.Any(id =>
                        id != AndroidAudioSession.SourcePhoneOutputId && !AndroidOutputIdentity.IsPhoneOutput(id)
                        && !WindowsLinkOutputIdentity.IsWindowsLinkOutput(id) && (id == null ? local.Count == 0 : !local.Contains(id)))))
                    {
                        localWatch = DefaultOutputNotifications.SubscribeTopology(() =>
                        {
                            AudioService.InvalidateDeviceCache(EDataFlow.eRender);
                            Wake();
                        });
                        Wake(); // Check device arrivals between the scan and COM registration.
                    }
                    await changes.Reader.ReadAsync(token).ConfigureAwait(false);
                }
            }
            finally
            {
                Phone.Changed -= Wake; settings.SavedSourcesChanged -= Wake; ApplicationAudio.OutputsChanged -= Wake;
                localWatch?.Dispose();
                if (watch != null) await watch.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (MediaShutdown.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Restore saved phone sources: " + ex.Message); }
    }
}
#endif
