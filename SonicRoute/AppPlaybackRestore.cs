#if SONICROUTE_NATIVE_PC
using AudioService = SonicRoute.Native.Product.PcRoutingApi;
using PhoneRoutingHost = SonicRoute.Native.Product.PcRoutingApi;
using ApplicationAudio = SonicRoute.Native.Product.PcRoutingApi;
#endif
using SonicRoute.Core;
using SonicRoute.Core.Compat;

#if SONICROUTE_PHONE && !SONICROUTE_NATIVE_PC
using PhoneRoutingHost = SonicRoute.AndroidAudioHost;
#endif

namespace SonicRoute;

internal static class AppPlaybackRestore
{
    private sealed record Applied(PlaybackAppIdentity App, SavedPlaybackSelection Choice, string[] Available, bool Complete);
    private static readonly object Gate = new();
    private static readonly Dictionary<int, Applied> AppliedRoutes = new();
    private static readonly HashSet<int> Pending = new();
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
#if SONICROUTE_PHONE
    private static HashSet<string> _pendingPhones = new(Ids);
    private static CancellationTokenSource? _phoneStop;
    private static Task _phoneWork = Task.CompletedTask;
#endif
    private static PlaybackSessionWatch? _watch;
    private static Task _worker = Task.CompletedTask;
    private static bool _active, _running, _refresh, _scan;

    internal static void Start()
    {
        lock (Gate)
        {
            if (_active) return;
            _active = true;
            RefreshDevices();
        }
    }

    internal static async Task StopAsync()
    {
        Task worker;
        lock (Gate)
        {
            _active = false;
            worker = _worker;
        }
        await worker.ConfigureAwait(false);
#if SONICROUTE_PHONE
        UpdatePhonePreparation(new HashSet<string>(Ids), replace: true);
        await _phoneWork.ConfigureAwait(false);
#endif
        // COM notification removal may block briefly and should run off the dispatcher.
        await Task.Run(() =>
        {
            ReleaseWatch();
            lock (Gate) { AppliedRoutes.Clear(); Pending.Clear(); }
        }).ConfigureAwait(false);
    }

    internal static bool HasSavedChoice(int pid) =>
        SavedPlaybackStore.ReadAll() is { Count: > 0 } saved && PlaybackAppIdentity.Read(pid) is { } app && saved.ContainsKey(app.Key);

    internal static bool Save(int pid, PlaybackAppIdentity? app, string[] outputs, string preferred)
    {
        if (app == null) return outputs.Length < 2;
        if (outputs.Length < 2 && !SavedPlaybackStore.ReadAll().ContainsKey(app.Value.Key)) return true;
#if SONICROUTE_PHONE
        string? Persistent(string id) => PhoneRoutingHost.GetPersistentOutputId(id);
        var stable = outputs.Select(Persistent).ToArray();
        if (stable.Any(id => id == null)) return false;
        var ids = stable.Select(id => id!).ToArray();
        string first = Persistent(preferred) ?? ids.FirstOrDefault() ?? preferred;
#else
        var ids = outputs;
        string first = preferred;
#endif
        if (!SavedPlaybackStore.TrySave(app.Value.Key, ids, first)) return false;
        lock (Gate)
        {
            if (SavedPlaybackStore.ReadAll().TryGetValue(app.Value.Key, out var choice))
                AppliedRoutes[pid] = new Applied(app.Value, choice, outputs, true);
            else AppliedRoutes.Remove(pid);
            ScheduleScan();
        }
        return true;
    }

    internal static async Task<bool> ClearAsync()
    {
        if (!SavedPlaybackStore.TryClear()) return false;
        Task worker;
        lock (Gate)
        {
            AppliedRoutes.Clear();
            ScheduleScan();
            worker = _worker;
        }
        await worker.ConfigureAwait(false);
        return true;
    }

    internal static void SessionCreated(int pid)
    {
        if (pid == AppInfo.CurrentProcessId) return;
        lock (Gate) { if (!_active) return; if (Pending.Count < 256) Pending.Add(pid); else _scan = true; Queue(); }
    }

    internal static void RefreshDevices()
    {
        lock (Gate) { if (!_active) return; _refresh = true; ScheduleScan(); }
    }

    private static void OutputsChanged()
    {
        lock (Gate) ScheduleScan();
    }

    private static void ScheduleScan()
    {
        if (!_active) return;
        _scan = true;
        Queue();
    }

    private static void Queue()
    {
        if (_running) return;
        _running = true;
        _worker = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        while (true)
        {
            bool refresh, scan;
            int[] ids;
            lock (Gate)
            {
                if (!_active || (!_scan && Pending.Count == 0))
                { _running = false; return; }
                refresh = _refresh;
                scan = _scan;
                _refresh = _scan = false;
                ids = Pending.ToArray();
                Pending.Clear();
            }
            try
            {
#if SONICROUTE_PHONE
                var missingPhones = new HashSet<string>(Ids);
#endif
                if (SavedPlaybackStore.ReadAll().Count == 0)
                {
                    ReleaseWatch();
#if SONICROUTE_PHONE
                    UpdatePhonePreparation(missingPhones, replace: true);
#endif
                    continue;
                }
                if (_watch == null)
                {
                    _watch = new PlaybackSessionWatch();
                    ApplicationAudio.OutputsChanged += OutputsChanged;
                    refresh = true;
                }
                if (refresh) _watch.RefreshDevices();
                if (scan)
                {
                    var apps = await AudioService.GetAppsAsync(refresh: true).ConfigureAwait(false);
                    ids = apps.Where(app => !app.IsExternal && app.ProcessId <= int.MaxValue)
                        .Select(app => (int)app.ProcessId).Concat(ids).Distinct().ToArray();
                    var present = ids.ToHashSet();
                    lock (Gate)
                        foreach (int stale in AppliedRoutes.Keys.Where(id => !present.Contains(id)).ToArray()) AppliedRoutes.Remove(stale);
                }
                foreach (int pid in ids)
                {
                    if (PlaybackAppIdentity.Read(pid) is not { } app ||
                        !SavedPlaybackStore.ReadAll().TryGetValue(app.Key, out var choice)) continue;
                    lock (Gate)
                    {
                        if (!_active) break;
                        if (AppliedRoutes.TryGetValue(pid, out var done) && done.App == app && done.Choice == choice && done.Complete && !refresh) continue;
                    }
#if SONICROUTE_PHONE
                    foreach (string id in choice.Outputs)
                        if (ApplicationAudio.IsPhoneOutputDevice(id) && PhoneRoutingHost.ResolvePersistentOutputId(id) == null)
                            missingPhones.Add(id);
#endif
                    try { await RestoreAsync(pid, app, choice).ConfigureAwait(false); }
                    catch (Exception ex) { ExceptionDiagnostics.Record("PlaybackRestore.Apply", ex); }
                }
#if SONICROUTE_PHONE
                UpdatePhonePreparation(missingPhones, replace: scan);
#endif
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("PlaybackRestore.Refresh", ex); }
        }
    }

#if SONICROUTE_PHONE
    private static void UpdatePhonePreparation(HashSet<string> missing, bool replace)
    {
        if (!replace) missing.UnionWith(_pendingPhones);
        if (_pendingPhones.SetEquals(missing)) return;
        _pendingPhones = missing;
        var previousStop = _phoneStop;
        previousStop?.Cancel();
        var prior = _phoneWork;
        var stop = _phoneStop = missing.Count == 0 ? null : new CancellationTokenSource();
        _phoneWork = PrepareAsync();

        async Task PrepareAsync()
        {
            await prior.ConfigureAwait(false);
            previousStop?.Dispose();
            if (stop == null) return;
            try
            {
                await PhoneRoutingHost.PrepareSavedPhoneOutputsAsync(missing, stop.Token).ConfigureAwait(false);
                if (!stop.IsCancellationRequested) OutputsChanged();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { ExceptionDiagnostics.Record("PlaybackRestore.Phone", ex); }
        }
    }
#endif

    private static void ReleaseWatch()
    {
        var watch = _watch;
        _watch = null;
        if (watch == null) return;
        ApplicationAudio.OutputsChanged -= OutputsChanged;
        try { watch.Dispose(); }
        catch (Exception ex) { ExceptionDiagnostics.Record("PlaybackRestore.Stop", ex); }
    }

    private static async Task RestoreAsync(int pid, PlaybackAppIdentity app, SavedPlaybackSelection choice)
    {
        string? Available(string id)
        {
            if (_watch!.IsAvailable(id)) return id;
#if SONICROUTE_PHONE
            if (PhoneRoutingHost.ResolvePersistentOutputId(id) is { } runtime && ApplicationAudio.IsPhoneOutputDevice(runtime)) return runtime;
#endif
            return null;
        }
        var available = choice.Outputs.Select(Available).Where(id => id != null).Cast<string>().Distinct(Ids).ToArray();
        if (available.Length == 0) return;
        // Do not turn a mixed PC/phone choice into exclusive phone playback while its PC endpoint is missing.
        if (available.All(ApplicationAudio.IsPhoneOutputDevice) && available.Length < choice.Outputs.Count) return;
        lock (Gate)
            if (AppliedRoutes.TryGetValue(pid, out var prior) && prior.App == app && prior.Choice == choice
                && prior.Available.SequenceEqual(available, Ids)) return;
        string preferred = Available(choice.Preferred) ?? available[0];
        bool StillWanted()
        {
            lock (Gate) return _active && SavedPlaybackStore.ReadAll().TryGetValue(app.Key, out var current) && current == choice;
        }
        if (!await AppPlaybackOutputs.RestoreAsync(pid, app, available, preferred, StillWanted).ConfigureAwait(false)) return;
        lock (Gate)
            if (StillWanted()) AppliedRoutes[pid] = new Applied(app, choice, available, available.Length == choice.Outputs.Count);
    }
}
