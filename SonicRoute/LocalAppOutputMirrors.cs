using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Compat;
#if SONICROUTE_PHONE
using System.Buffers;
using CaptureSource = SonicRoute.WindowsLink.LinkAudioCapture;
#else
using CaptureSource = SonicRoute.AndroidAudio.LocalProcessAudioCapture;
#endif
using AudioPolicyConfig = SonicRoute.Core.Interop.AudioPolicyConfig;

namespace SonicRoute;

// The app's original output belongs to Windows. One process loopback capture feeds all additional outputs.
internal static partial class LocalAppOutputMirrors
{
    private static readonly SemaphoreSlim Changes = new(1, 1);
    private static readonly ConcurrentDictionary<int, Route> Routes = new();
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private static IDisposable? _deviceWatch;
    private static int _refreshQueued;
    private static readonly CancellationTokenSource Shutdown = new();

    internal static bool HasActiveRoutes => !Routes.IsEmpty;

    private static string OwnerName(int pid) => @"Local\SonicRoute.AudioMirror." + pid;

    // Full and Lite can share settings and run side by side. Only one may replay the same process.
    internal static IDisposable Reserve(int pid)
    {
        var owner = new EventWaitHandle(false, EventResetMode.ManualReset, OwnerName(pid), out bool created);
        if (created || Routes.ContainsKey(pid)) return owner;
        owner.Dispose();
        throw new System.ComponentModel.Win32Exception(170); // ERROR_BUSY
    }

    internal static string[] GetExtraOutputs(int processId) =>
        Routes.TryGetValue(processId, out var route) ? route.Sinks.Select(s => s.Id).ToArray() : [];

    internal static Task SetExtraOutputsAsync(int processId, IReadOnlyCollection<string> extraOutputIds,
        CancellationToken token = default) => SetOutputsAsync(processId, extraOutputIds, false, false, token);

    private static Task SetOutputsAsync(int processId, IReadOnlyCollection<string> extraOutputIds,
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
                if (processId <= 0 || AudioProcessTree.ContainsProcess(processId, AppInfo.CurrentProcessId))
                    throw new InvalidOperationException(L10n.T("Tray.AppUnavailable"));
#if SONICROUTE_PHONE
                _deviceWatch ??= DefaultOutputNotifications.SubscribeTopology(QueueRefresh, includeDefaultChanged: false);
#else
                _deviceWatch ??= WasapiDeviceLease.ObserveDefaultOutput(new DeviceEvents());
#endif
                var endpoints = ResolveOutputs(requested, strict: true);
                Routes.TryGetValue(processId, out var route);
                if (endpoints.Length == 0) { if (route != null) await RemoveAsync(route); return; }
                if (route != null && route.HasExited) { await RemoveAsync(route); route = null; }
                bool starting = route == null;
                route ??= new Route(processId);
                try
                {
                    if (starting) await route.StartAsync(lifetime.Token).ConfigureAwait(false);
                    await route.UpdateAsync(endpoints, phoneOnly, allowSpeakerMute, lifetime.Token).ConfigureAwait(false);
                    if (starting)
                    {
                        Routes[processId] = route;
                        _ = RetireAfterCaptureAsync(route);
                    }
                    ApplicationAudio.NotifyRouteChanged(route.ProcessId);
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
#if SONICROUTE_PHONE
        AppPlaybackOutputs.ForgetAll();
#endif
        Shutdown.Cancel();
        await ClearAllAsync().ConfigureAwait(false);
    }

    internal static async Task ClearAllAsync()
    {
        await Changes.WaitAsync().ConfigureAwait(false);
        try { foreach (var route in Routes.Values) await RemoveAsync(route).ConfigureAwait(false); }
        finally { ReleaseChanges(); }
    }

    private static void ReleaseChanges()
    {
        try
        {
            ReleaseUnusedPhoneWatch();
            if (Routes.IsEmpty) Interlocked.Exchange(ref _deviceWatch, null)?.Dispose();
        }
        catch (COMException ex) { ExceptionDiagnostics.Record("LocalAppOutputMirrors.DisposeDeviceWatch", ex); }
        finally { Changes.Release(); }
    }

    static partial void ReleaseUnusedPhoneWatch();

    private static async Task RemoveAsync(Route route)
    {
        Routes.TryRemove(route.ProcessId, out _);
        await route.StopAsync().ConfigureAwait(false);
        ApplicationAudio.NotifyRouteChanged(route.ProcessId);
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
                else { route.Replace(retained); ApplicationAudio.NotifyRouteChanged(route.ProcessId); }
                NotifyPrunedPhoneOutputs(route, previous, retained, recover);
            }
        }
        finally { ReleaseChanges(); }
    });

    static partial void NotifyPrunedPhoneOutputs(Route route, Sink[] previous, Sink[] retained, bool recover);

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
                    var allowed = ResolveOutputs(current.Select(s => s.Id).ToArray(), strict: false).ToHashSet(Ids);
                    if (allowed.Count == current.Length) continue;
                    if (allowed.Count == 0) await RemoveAsync(route);
                    else { route.Replace(current.Where(s => allowed.Contains(s.Id)).ToArray()); ApplicationAudio.NotifyRouteChanged(route.ProcessId); }
                }
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("LocalAppOutputMirrors.OutputChange", ex); }
            finally { ReleaseChanges(); }
        });
    }

#if !SONICROUTE_PHONE
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceEvents : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { }
        public void OnDeviceStateChanged(string id, DeviceState state) => QueueRefresh();
        public void OnDeviceRemoved(string id) => QueueRefresh();
        public void OnDeviceAdded(string id) { }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }
#endif

    private static string[] ResolveOutputs(string[] requested, bool strict)
    {
        using var devices = new WasapiDeviceLease.Enumerator();
        // Mirror destinations are explicit selections. Changes to the primary route do not remove them.
        var selected = new HashSet<string>(Ids);
        foreach (string id in requested)
        {
            string physical = AudioPolicyConfig.UnpackDeviceId(id);
            if (ApplicationAudio.IsPhoneOutputDevice(physical)) { selected.Add(physical); continue; }
            try
            {
                using var lease = devices.GetDevice(physical);
                var device = lease.Device;
                if (device.State != DeviceState.Active || device.DataFlow != DataFlow.Render)
                    throw new IOException(L10n.T("Ov.CurrentUnavailable"));
                selected.Add(device.ID);
            }
            catch (Exception ex) when (!strict && (ex is COMException or IOException)) { }
        }
        return selected.ToArray();
    }

    private sealed class Sink(string id, IDisposable player)
    {
        internal readonly string Id = id;
#if SONICROUTE_PHONE
        internal AndroidPcmSource? Pcm { get; init; }
        internal DesktopMirrorPlayer? DesktopPlayer => Player as DesktopMirrorPlayer;
#else
        internal DesktopMirrorPlayer DesktopPlayer => (DesktopMirrorPlayer)Player;
#endif
        internal readonly IDisposable Player = player;

        internal void Dispose()
        {
            try { Player.Dispose(); }
            catch (Exception ex) { ExceptionDiagnostics.Record("LocalAppOutputMirrors.DisposeOutput", ex); }
        }
    }

    private sealed partial class Route(int processId)
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Process _process = Process.GetProcessById(processId);
        private EventWaitHandle? _owner;
        private Sink[] _sinks = [];
        internal Sink[] Sinks => Volatile.Read(ref _sinks);
        internal bool HasExited => _process.HasExited;
        internal int ProcessId => processId;
        internal Task Capture { get; private set; } = Task.CompletedTask;

        internal async Task StartAsync(CancellationToken token)
        {
            // Keep the transaction's reservation alive until the capture and its renderers are stopped.
            _owner = new EventWaitHandle(false, EventResetMode.ManualReset, OwnerName(processId));
            _process.Exited += Exited;
            _process.EnableRaisingEvents = true;
            if (HasExited) throw new InvalidOperationException(L10n.T("Tray.AppUnavailable"));
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancelWait = token.Register(() => ready.TrySetCanceled());
            Capture = CaptureSource.CaptureAsync(processId, Push, () => ready.TrySetResult(true), _stop.Token);
            var first = await Task.WhenAny(ready.Task, Capture).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (Capture.IsCompleted) throw new IOException(L10n.T("Tray.AppUnavailable"));
        }

        private void Exited(object? sender, EventArgs args)
        {
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        }

        internal async Task UpdateAsync(string[] desired, bool phoneOnly, bool allowSpeakerMute, CancellationToken token)
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
                    Sink sink = null!;
                    void Failed(Exception error)
                    {
                        ExceptionDiagnostics.Record("LocalAppOutputMirrors.Playback", error);
                        _ = PruneFailed(sink);
                    }
#if SONICROUTE_PHONE
                    if (AndroidOutputIdentity.IsPhoneOutput(id))
                    {
                        var pcm = new AndroidPcmSource();
                        var phone = AndroidAudioHost.CreatePhonePcmPlayer(pcm, id, change.Token);
                        sink = new Sink(id, phone) { Pcm = pcm };
                        staged.Add(sink);
                        phone.Failed += error =>
                        {
                            Failed(error);
                            AndroidAudioHost.PhonePlaybackFailed(phone.Device);
                        };
                        await phone.VerifyBeforePlaybackAsync(change.Token).ConfigureAwait(false);
                        phone.Play();
                        if (phone.Failure is { } error) throw error;
                    }
                    else
                    {
                        var desktop = new DesktopMirrorPlayer(id);
                        sink = new Sink(id, desktop);
                        staged.Add(sink);
                        desktop.Failed += Failed;
                    }
#else
                    var player = new DesktopMirrorPlayer(id);
                    sink = new Sink(id, player);
                    staged.Add(sink);
                    player.Failed += Failed;
#endif
                    next.Add(sink);
                }
                change.Token.ThrowIfCancellationRequested();
                if (Capture.IsCompleted) { await Capture.ConfigureAwait(false); throw new IOException(L10n.T("Tray.AppUnavailable")); }
                UpdateIsolation(phoneOnly, allowSpeakerMute);
                Replace(next.ToArray());
            }
            catch { foreach (var sink in staged) sink.Dispose(); throw; }
        }

        partial void UpdateIsolation(bool phoneOnly, bool allowSpeakerMute);
        partial void ReleaseIsolation();

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
#if SONICROUTE_PHONE
            bool hasPhone = false;
            foreach (var sink in outputs)
            {
                if (sink.DesktopPlayer is { } desktop) desktop.Write(packet);
                else if (sink.Pcm != null) hasPhone = true;
            }
            if (!hasPhone) return;
            var data = MemoryMarshal.Cast<byte, short>(packet.Span);
            float[] scratch = ArrayPool<float>.Shared.Rent(data.Length);
            try
            {
                var floats = scratch.AsSpan(0, data.Length);
                for (int i = 0; i < data.Length; i++) floats[i] = data[i] * (1f / 32768);
                var metrics = AndroidPcmSource.Measure(floats);
                foreach (var sink in outputs) sink.Pcm?.WriteMeasured(floats, metrics);
            }
            finally { ArrayPool<float>.Shared.Return(scratch); }
#else
            foreach (var sink in outputs) sink.DesktopPlayer.Write(packet);
#endif
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
            _owner?.Dispose();
        }
    }
}
