using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

// Resident temporary mic OSD observes all-muted plus the configured default input, like 1.36.
// A persistent source selection narrows that scope. Temporary CLI instances never construct this owner.
internal sealed class ProductMicMonitor(ProductAudio audio, NativeApplicationWindow window, AppConfig config, FeedbackState feedback) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, IDisposable> endpoints = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? topology, retry;
    private bool closed, running, rebuild, dirty, incomplete;
    private bool? baseline;
    private string? defaultInput;
    private Task completion = Task.CompletedTask;
    private int count;
    internal int Count => Volatile.Read(ref count);

    internal void Start() => Request(true);
    internal void InvalidateTopology() => Request(true);
    private void Changed() => Request(false);

    private void Request(bool changeTopology)
    {
        lock (gate)
        {
            if (closed) return;
            dirty = true;
            rebuild |= changeTopology;
            if (running) return;
            running = true;
            completion = Task.Run(Drain);
        }
    }

    private (bool All, bool Default) Sources()
    {
        bool all = config.MicMuteOsdPersistent, input = config.MicMuteOsdDefaultInputPersistent;
        return all || input ? (all, input) : (true, config.MicMuteOsdTrackInputMuted);
    }

    private void Drain()
    {
        while (true)
        {
            bool refresh;
            lock (gate)
            {
                if (closed || !dirty) { running = false; return; }
                dirty = false; refresh = rebuild; rebuild = false;
            }
            try
            {
                var scope = Sources();
                if (refresh || incomplete)
                {
                    incomplete = false;
                    topology ??= ProductAudio.WatchDevices(InvalidateTopology);
                    var all = ProductAudio.Devices(Flow.Capture);
                    defaultInput = scope.Default && all.Length > 0 ? ProductAudio.DefaultId(Flow.Capture, Role.Multimedia) : null;
                    var wanted = all.Where(item => scope.All || item.Id.Equals(defaultInput,StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var id in endpoints.Keys.Where(id => !wanted.Contains(id)).ToArray())
                    { endpoints[id].Dispose(); endpoints.Remove(id); }
                    foreach (var id in wanted)
                        if (!endpoints.ContainsKey(id)) endpoints.Add(id,audio.WatchEndpoint(Flow.Capture,id,Changed));
                    Volatile.Write(ref count,endpoints.Count);
                }
                if (endpoints.Count != 0)
                {
                    bool allMuted = true, defaultMuted = false;
                    foreach (string id in endpoints.Keys)
                    {
                        bool muted = audio.Volume(Flow.Capture,id).Muted;
                        allMuted &= muted;
                        if (id.Equals(defaultInput,StringComparison.OrdinalIgnoreCase)) defaultMuted = muted;
                    }
                    bool current = scope.All && allMuted || scope.Default && defaultMuted;
                    if (baseline.HasValue && baseline.Value != current)
                        window.TryPost(() =>
                        {
                            if (!closed) feedback.Publish(FeedbackKind.Osd,L10n.T("Ov.MuteMic"),L10n.T(current ? "Ov.MicMuted" : "Ov.MicUnmuted"));
                        });
                    baseline = current;
                }
            }
            catch (Exception e)
            {
                if (!incomplete) AutoRuleScheduler.Log("Microphone monitor recovery: " + e.Message);
                incomplete = true; // Preserve the last confirmed state across a driver failure.
            }
            bool needed = incomplete || config.BackgroundPollingFallbackEnabled;
            window.TryPost(() => ScheduleRecovery(needed));
        }
    }

    private void ScheduleRecovery(bool needed)
    {
        if (closed) return;
        if (!needed) { retry?.Dispose(); retry = null; return; }
        retry ??= window.ScheduleOnce(TimeSpan.FromSeconds(2), () => { retry = null; Request(true); });
    }

    public void Dispose()
    {
        Task pending;
        lock (gate) { closed = true; pending = completion; }
        retry?.Dispose();
        // All background work posts without waiting on the HWND. Release on an MTA before the audio owner goes away.
        Task.Run(async () =>
        {
            await pending.ConfigureAwait(false);
            topology?.Dispose();
            foreach (var owner in endpoints.Values) owner.Dispose();
            endpoints.Clear();
        }).GetAwaiter().GetResult();
    }
}
