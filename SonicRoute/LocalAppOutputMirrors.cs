#if SONICROUTE_PHONE
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using AudioPolicyConfig = SonicRoute.Core.Interop.AudioPolicyConfig;
using SonicRoute.WindowsLink;

namespace SonicRoute;

// The app's original output belongs to Windows. One process loopback capture feeds all additional outputs.
internal static class LocalAppOutputMirrors
{
    private static readonly SemaphoreSlim Changes = new(1, 1);
    private static readonly ConcurrentDictionary<int, Route> Routes = new();
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private static IDisposable? _deviceWatch;
    private static WindowsLinkCatalogNotifications? _phoneIsolationWatch;
    private static int _refreshQueued;
    private static readonly CancellationTokenSource Shutdown = new();

    internal static string[] GetExtraOutputs(int processId) =>
        Routes.TryGetValue(processId, out var route) ? route.Sinks.Select(s => s.Id).ToArray() : [];

    internal static string? GetExclusivePhone(int pid, out bool mutedSpeaker)
    {
        mutedSpeaker = false;
        if (!Routes.TryGetValue(pid, out var route) || !route.PhoneOnly) return null;
        mutedSpeaker = route.MutedSpeaker;
        return route.Sinks.FirstOrDefault()?.Id;
    }

    internal static Task SetExclusivePhoneOutputsAsync(int pid, string[] phones, bool allowSpeakerMute)
    {
        if (phones.Length == 0 || phones.Any(id => !AndroidOutputIdentity.IsPhoneOutput(id)))
            throw new ArgumentException(L10n.T("Phone.Err.InvalidPhoneOutputDevice"));
        return SetOutputsAsync(pid, null, phones, phoneOnly: true, allowSpeakerMute, default);
    }

    internal static Task SetExtraOutputsAsync(int processId, string? primaryOutputId, IReadOnlyCollection<string> extraOutputIds,
        CancellationToken token = default) => SetOutputsAsync(processId, primaryOutputId, extraOutputIds, false, false, token);

    private static Task SetOutputsAsync(int processId, string? primaryOutputId, IReadOnlyCollection<string> extraOutputIds,
        bool phoneOnly, bool allowSpeakerMute, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var requested = extraOutputIds.Distinct(Ids).ToArray();
        if (requested.Length == 0) return ClearAsync(processId);
        return Task.Run(async () =>
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, Shutdown.Token);
            await Changes.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                if (processId <= 0 || processId == Environment.ProcessId || LinkWindowAudioOwner.DescendsFrom(Environment.ProcessId, processId))
                    throw new InvalidOperationException(L10n.T("Tray.AppUnavailable"));
                _deviceWatch ??= WasapiDeviceLease.ObserveDefaultOutput(new DeviceEvents());
                var endpoints = ResolveOutputs(processId, primaryOutputId, requested, strict: true);
                Routes.TryGetValue(processId, out var route);
                if (endpoints.Length == 0) { if (route != null) await RemoveAsync(route); return; }
                if (route != null && route.HasExited) { await RemoveAsync(route); route = null; }
                bool starting = route == null;
                route ??= new Route(processId);
                try
                {
                    if (starting) await route.StartAsync(lifetime.Token).ConfigureAwait(false);
                    await route.UpdateAsync(primaryOutputId, endpoints, phoneOnly, allowSpeakerMute, lifetime.Token).ConfigureAwait(false);
                    if (starting)
                    {
                        Routes[processId] = route;
                        _ = RetireAfterCaptureAsync(route);
                    }
                    ApplicationAudio.NotifyChanged();
                }
                catch
                {
                    if (starting) await route.StopAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally { ReleaseChanges(); }
        }, token);
    }

    internal static Task ClearAsync(int processId) => Task.Run(async () =>
    {
        await Changes.WaitAsync().ConfigureAwait(false);
        try { if (Routes.TryGetValue(processId, out var route)) await RemoveAsync(route); }
        finally { ReleaseChanges(); }
    });

    internal static async Task ShutdownAsync()
    {
        AppPlaybackOutputs.ForgetAll();
        Shutdown.Cancel();
        await ClearAllAsync().ConfigureAwait(false);
    }

    internal static async Task ClearAllAsync()
    {
        await Changes.WaitAsync().ConfigureAwait(false);
        try { foreach (var route in Routes.Values) await RemoveAsync(route).ConfigureAwait(false); }
        finally { ReleaseChanges(); }
    }

    internal static Task PhoneRemovedAsync(AndroidDevice device) =>
        PruneAsync((_, sink) => sink.Player is AndroidPhonePcmPlayer phone && AndroidDeviceIdentity.MatchesSavedDevice(device, phone.Device), recover: false);

    internal static void RefreshPhoneOutputs()
    {
        if (!Routes.Values.Any(route => route.Sinks.Any(s => s.Player is AndroidPhonePcmPlayer))) return;
        _ = PruneAsync((_, sink) => sink.Player is AndroidPhonePcmPlayer && !AndroidAudioHost.GetOutputDevices().Any(d => Ids.Equals(d.Id, sink.Id)), recover: true);
    }

    private static void ReleaseChanges()
    {
        try
        {
            if (_phoneIsolationWatch != null && !Routes.Values.Any(r => r.PhoneOnly))
            {
                _ = _phoneIsolationWatch.ReleaseAsync();
                _phoneIsolationWatch = null;
            }
            if (Routes.IsEmpty) Interlocked.Exchange(ref _deviceWatch, null)?.Dispose();
        }
        catch (COMException ex) { ExceptionDiagnostics.Record("LocalAppOutputMirrors.DisposeDeviceWatch", ex); }
        finally { Changes.Release(); }
    }

    private static async Task RemoveAsync(Route route)
    {
        Routes.TryRemove(route.ProcessId, out _);
        await route.StopAsync().ConfigureAwait(false);
        ApplicationAudio.NotifyChanged();
    }

    private static async Task RetireAfterCaptureAsync(Route route)
    {
        try { await route.Capture.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ExceptionDiagnostics.Record("LocalAppOutputMirrors.Capture", ex); }
        await Changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Routes.TryGetValue(route.ProcessId, out var active) && active == route) await RemoveAsync(route);
        }
        finally { ReleaseChanges(); }
    }

    private static Task PruneAsync(Func<Route, Sink, bool> remove, bool recover = false) => Task.Run(async () =>
    {
        await Changes.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var route in Routes.Values)
            {
                var previous = route.Sinks;
                var retained = previous.Where(sink => !remove(route, sink)).ToArray();
                if (retained.Length == previous.Length) continue;
                if (retained.Length == 0) await RemoveAsync(route);
                else { route.Replace(retained); ApplicationAudio.NotifyChanged(); }
                foreach (var sink in previous)
                    if (sink.Player is AndroidPhonePcmPlayer phone && !retained.Contains(sink))
                        AppPlaybackOutputs.OutputEnded(route.ProcessId, sink.Id, recover && !AndroidAudioHost.Phone.IsRemoved(phone.Device));
            }
        }
        finally { ReleaseChanges(); }
    });

    private static void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Changes.WaitAsync().ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                foreach (var route in Routes.Values)
                {
                    var current = route.Sinks;
                    var allowed = ResolveOutputs(route.ProcessId, route.PrimaryId, current.Select(s => s.Id).ToArray(), strict: false).ToHashSet(Ids);
                    foreach (var sink in current) if (allowed.Contains(sink.Id)) sink.ObserveSourceSessions(route.ProcessId);
                    if (allowed.Count == current.Length) continue;
                    if (allowed.Count == 0) await RemoveAsync(route);
                    else { route.Replace(current.Where(s => allowed.Contains(s.Id)).ToArray()); ApplicationAudio.NotifyChanged(); }
                }
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("LocalAppOutputMirrors.OutputChange", ex); }
            finally { ReleaseChanges(); }
        });
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceEvents : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { if (flow == DataFlow.Render) QueueRefresh(); }
        public void OnDeviceStateChanged(string id, DeviceState state) => QueueRefresh();
        public void OnDeviceRemoved(string id) => QueueRefresh();
        public void OnDeviceAdded(string id) { }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }

    private static string[] ResolveOutputs(int pid, string? primary, string[] requested, bool strict)
    {
        using var devices = new WasapiDeviceLease.Enumerator();
        using var console = devices.TryDefault(Role.Console);
        using var multimedia = devices.TryDefault(Role.Multimedia);
        var excluded = new HashSet<string?>(Ids);
        if (primary == null || AudioService.IsSystemDefault(primary))
        {
            excluded.Add(console?.Device.ID);
            excluded.Add(multimedia?.Device.ID);
        }
        else excluded.Add(AudioPolicyConfig.UnpackDeviceId(primary));
        var selected = new HashSet<string>(Ids);
        foreach (string id in requested)
        {
            string? physical = AudioService.IsSystemDefault(id) ? console?.Device.ID : AudioPolicyConfig.UnpackDeviceId(id);
            if (physical == null || excluded.Contains(physical)) continue;
            if (AndroidOutputIdentity.IsPhoneOutput(physical)) { selected.Add(physical); continue; }
            try
            {
                using var lease = devices.GetDevice(physical);
                var device = lease.Device;
                if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render)
                    throw new IOException(L10n.T("Ov.CurrentUnavailable"));
                if (!HasActiveSourceSession(device, pid)) selected.Add(device.ID);
            }
            catch (Exception ex) when (!strict && (ex is COMException or IOException)) { }
        }
        return selected.ToArray();
    }

    private static bool HasActiveSourceSession(MMDevice device, int pid)
    {
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.State == AudioSessionState.AudioSessionStateActive && WindowsLinkPlaybackWatch.ContainsProcess(pid, (int)session.GetProcessID)) return true;
        }
        return false;
    }

    private sealed class Sink(string id, AndroidPcmSource pcm, IPcmPlayer player) : IAudioSessionEventsHandler
    {
        internal readonly string Id = id;
        internal readonly AndroidPcmSource Pcm = pcm;
        internal readonly IPcmPlayer Player = player;
        private WasapiDeviceLease? _observed;
        private readonly Dictionary<string, AudioSessionControl> _sourceSessions = new(StringComparer.Ordinal);

        // A process can open another session directly on a selected mirror without changing a default device.
        // Subscribe only on active mirrors; existing dormant sessions can resume without a creation event.
        internal void ObserveSourceSessions(int pid)
        {
            if (Player is not WasapiPcmPlayer) return;
            if (_observed == null)
            {
                _observed = WasapiDeviceLease.Open(Id);
                _observed.Device.AudioSessionManager.OnSessionCreated += SessionCreated;
            }
            var sessions = _observed.Device.AudioSessionManager.Sessions;
            var present = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                bool keep = false;
                try
                {
                    if (session.State == AudioSessionState.AudioSessionStateExpired || !WindowsLinkPlaybackWatch.ContainsProcess(pid, (int)session.GetProcessID)) continue;
                    var key = session.GetSessionInstanceIdentifier;
                    present.Add(key);
                    if (_sourceSessions.ContainsKey(key)) continue;
                    session.RegisterEventClient(this);
                    _sourceSessions.Add(key, session);
                    keep = true;
                    if (session.State == AudioSessionState.AudioSessionStateActive) QueueRefresh();
                }
                finally { if (!keep) session.Dispose(); }
            }
            foreach (var key in _sourceSessions.Keys.Where(key => !present.Contains(key)).ToArray()) ForgetSession(key);
        }
        private void SessionCreated(object sender, IAudioSessionControl session) => QueueRefresh();
        public void OnStateChanged(AudioSessionState state) { if (state == AudioSessionState.AudioSessionStateActive) QueueRefresh(); }
        public void OnVolumeChanged(float volume, bool isMuted) { }
        public void OnDisplayNameChanged(string name) { }
        public void OnIconPathChanged(string path) { }
        public void OnChannelVolumeChanged(uint count, nint volumes, uint channel) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
        public void OnSessionDisconnected(AudioSessionDisconnectReason reason) { }

        private void ForgetSession(string key)
        {
            if (!_sourceSessions.Remove(key, out var session)) return;
            try { session.UnRegisterEventClient(this); } catch (COMException) { } finally { session.Dispose(); }
        }
        internal void Dispose()
        {
            try
            {
                if (_observed != null)
                {
                    try { _observed.Device.AudioSessionManager.OnSessionCreated -= SessionCreated; } catch (COMException) { }
                    foreach (var key in _sourceSessions.Keys.ToArray()) ForgetSession(key);
                    _observed.Dispose();
                }
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("LocalAppOutputMirrors.DisposeObserver", ex); }
            try { Player.Dispose(); }
            catch (Exception ex) { ExceptionDiagnostics.Record("LocalAppOutputMirrors.DisposeOutput", ex); }
        }
    }

    private sealed class Route(int processId)
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Process _process = Process.GetProcessById(processId);
        private Sink[] _sinks = [];
        private IPcProcessSilenceLease? _silence;
        private CancellationTokenRegistration _invalidated;
        internal bool PhoneOnly => Volatile.Read(ref _silence) != null;
        internal bool MutedSpeaker { get; private set; }
        internal Sink[] Sinks => Volatile.Read(ref _sinks);
        internal bool HasExited => _process.HasExited;
        internal int ProcessId => processId;
        internal string? PrimaryId { get; private set; }
        internal Task Capture { get; private set; } = Task.CompletedTask;

        internal async Task StartAsync(CancellationToken token)
        {
            _process.Exited += Exited;
            _process.EnableRaisingEvents = true;
            if (HasExited) throw new InvalidOperationException(L10n.T("Tray.AppUnavailable"));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Capture = LinkAudioCapture.CaptureAsync(processId, Push, () => ready.TrySetResult(), _stop.Token);
            var first = await Task.WhenAny(ready.Task, Capture).WaitAsync(token).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (Capture.IsCompleted) throw new IOException(L10n.T("Tray.AppUnavailable"));
        }

        private void Exited(object? sender, EventArgs args)
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        }

        internal async Task UpdateAsync(string? primary, string[] desired, bool phoneOnly, bool allowSpeakerMute, CancellationToken token)
        {
            using var change = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            var previous = Sinks;
            var staged = new List<Sink>();
            var next = new List<Sink>();
            try
            {
                foreach (string id in desired)
                {
                    var existing = previous.FirstOrDefault(s => Ids.Equals(s.Id, id));
                    if (existing != null) { next.Add(existing); continue; }
                    var pcm = new AndroidPcmSource();
                    IPcmPlayer player = AndroidOutputIdentity.IsPhoneOutput(id)
                        ? AndroidAudioHost.CreatePhonePcmPlayer(pcm, id, change.Token) : new WasapiPcmPlayer(pcm, id);
                    var sink = new Sink(id, pcm, player);
                    staged.Add(sink);
                    void Failed(Exception error)
                    {
                        ExceptionDiagnostics.Record("LocalAppOutputMirrors.Playback", error);
                        _ = PruneFailed(sink);
                    }
                    if (player is AndroidPhonePcmPlayer phone)
                    {
                        phone.Failed += Failed;
                        await phone.VerifyBeforePlaybackAsync(change.Token).ConfigureAwait(false);
                        phone.Play();
                        if (phone.Failure is { } error) throw error;
                    }
                    else if (player is WasapiPcmPlayer windows)
                    {
                        windows.Failed += Failed;
                        sink.ObserveSourceSessions(processId);
                        await windows.PlayAndConfirmAsync(change.Token).ConfigureAwait(false);
                    }
                    next.Add(sink);
                }
                change.Token.ThrowIfCancellationRequested();
                if (Capture.IsCompleted) { await Capture.ConfigureAwait(false); throw new IOException(L10n.T("Tray.AppUnavailable")); }
                if (phoneOnly && !PhoneOnly) Isolate(allowSpeakerMute);
                else if (!phoneOnly) ReleaseIsolation();
                PrimaryId = primary;
                Replace(next.ToArray());
            }
            catch { foreach (var sink in staged) sink.Dispose(); throw; }
        }

        private void Isolate(bool allowSpeakerMute)
        {
            _phoneIsolationWatch ??= WindowsLinkCatalogNotifications.Acquire();
            IPcProcessSilenceLease? lease = null;
            try { lease = AndroidAudioHost.AcquireWindowsLinkSilenceLease(processId); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                if (!allowSpeakerMute && AndroidAudioHost.CanMuteAppSpeaker(processId))
                    throw new SourceSpeakerConsentRequiredException(ex.Message);
                if (!allowSpeakerMute) throw;
                ExceptionDiagnostics.Record("LocalAppOutputMirrors.Isolate", ex);
            }
            if (lease == null && !allowSpeakerMute && AndroidAudioHost.CanMuteAppSpeaker(processId))
                throw new SourceSpeakerConsentRequiredException(L10n.T("Phone.WindowsLinkMuteSpeakerDesc"));
            bool muteSpeaker = lease == null && allowSpeakerMute;
            if (muteSpeaker) lease = AndroidAudioHost.AcquireWindowsLinkSpeakerFallback(processId);
            if (lease == null) throw new IOException(L10n.T("Phone.WindowsLinkAppKeptOutput"));
            try
            {
                _invalidated = lease.Invalidated.Register(() => _ = PruneAsync((route, _) => route == this && _silence == lease));
                lease.Invalidated.ThrowIfCancellationRequested();
                MutedSpeaker = muteSpeaker;
                Volatile.Write(ref _silence, lease);
            }
            catch { _invalidated.Dispose(); lease.Dispose(); throw; }
        }

        private void ReleaseIsolation()
        {
            if (!PhoneOnly) return;
            _invalidated.Dispose();
            Interlocked.Exchange(ref _silence, null)?.Dispose();
            MutedSpeaker = false;
        }

        private Task PruneFailed(Sink sink) => PruneAsync((route, value) => route == this && ReferenceEquals(value, sink), recover: true);

        internal void Replace(Sink[] next)
        {
            var previous = Interlocked.Exchange(ref _sinks, next);
            foreach (var sink in previous) if (!next.Contains(sink)) sink.Dispose();
        }

        private void Push(ReadOnlyMemory<byte> packet)
        {
            var outputs = Sinks;
            if (outputs.Length == 0) return;
            var data = MemoryMarshal.Cast<byte, short>(packet.Span);
            float[] scratch = ArrayPool<float>.Shared.Rent(data.Length);
            try
            {
                var floats = scratch.AsSpan(0, data.Length);
                for (int i = 0; i < data.Length; i++) floats[i] = data[i] * (1f / 32768);
                var metrics = AndroidPcmSource.Measure(floats);
                foreach (var sink in outputs) sink.Pcm.WriteMeasured(floats, metrics);
            }
            finally { ArrayPool<float>.Shared.Return(scratch); }
        }

        internal async Task StopAsync()
        {
            ReleaseIsolation();
            _process.Exited -= Exited;
            _stop.Cancel();
            try { await Capture.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex) { Debug.WriteLine(ex); }
            Replace([]);
            _process.Dispose();
            _stop.Dispose();
        }
    }
}
#endif
