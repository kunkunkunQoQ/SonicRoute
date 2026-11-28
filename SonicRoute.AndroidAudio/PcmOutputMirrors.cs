namespace SonicRoute.AndroidAudio;

// Output changes are serialized by the owning session. PCM readers consume an immutable snapshot.
internal sealed class PcmOutputMirrors(Func<Predicate<IPcmPlayer>, Task> removeFailed)
{
    private sealed record Output(string Id, AndroidPcmSource Pcm, IPcmPlayer Player);
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private readonly object _gate = new();
    private Output[] _outputs = [], _audible = [];
    private readonly Dictionary<string, (int VolumePercent, bool Muted)> _gains = new(Ids);
    private float _volume = 1;
    private float _headerGain = 1;
    private bool _smart;
    private AudioLatencyMode _latencyMode = AudioLatencyMode.Balanced;
    private string? _primaryDevice;
    internal Func<string, (int VolumePercent, bool Muted)?>? InitialGain { private get; set; }
    internal string[] IdsSnapshot() => Volatile.Read(ref _outputs).Select(output => output.Id).ToArray();
    private float MirrorGainLocked(string id)
    {
        if (!_gains.TryGetValue(id, out var gain)) return 1f;
        return gain.Muted ? 0f : gain.VolumePercent / 100f;
    }
    internal (int VolumePercent, bool Muted) GetGain(string id)
    {
        lock (_gate) return _gains.TryGetValue(id, out var gain) ? gain : (100, false);
    }
    internal (int VolumePercent, bool Muted) SetGain(string id, int? volumePercent = null, bool? muted = null, bool toggleMute = false)
    {
        lock (_gate)
        {
            var prev = _gains.TryGetValue(id, out var existing) ? existing : (VolumePercent: 100, Muted: false);
            var next = (VolumePercent: Math.Clamp(volumePercent ?? prev.VolumePercent, 0, AndroidPcmSource.MaximumMirrorVolumePercent),
                Muted: toggleMute ? !prev.Muted : (muted ?? prev.Muted));
            _gains[id] = next;
            foreach (var output in _outputs)
                if (Ids.Equals(output.Id, id)) output.Pcm.MirrorGain = next.Muted ? 0f : next.VolumePercent / 100f;
            return next;
        }
    }
    internal static (string? Primary, string[] Extras) Select(IReadOnlyList<string?> selection, string? current)
    {
        var distinct = selection.Distinct(Ids).ToArray();
        string? primary = distinct.Length == 0 || distinct.Contains(null) ? null : distinct.Contains(current, Ids) ? current : distinct[0];
        return (primary, distinct.Where(id => id != null && !Ids.Equals(id, primary)).Select(id => id!).ToArray());
    }
    internal void SetLevels(float volume, bool smart)
    {
        lock (_gate)
        {
            _volume = volume; _smart = smart;
            foreach (var output in _outputs) { output.Pcm.Volume = volume; output.Pcm.SmartVolume = smart; }
        }
    }
    internal void SetHeaderGain(float gain)
    {
        lock (_gate)
        {
            _headerGain = gain;
            foreach (var output in _outputs) output.Pcm.HeaderGain = gain;
        }
    }
    internal void SetLatencyMode(AudioLatencyMode mode)
    {
        lock (_gate)
        {
            _latencyMode = mode;
            foreach (var output in _outputs) output.Pcm.LatencyMode = mode;
        }
    }
    internal void SetPrimary(IPcmPlayer? player)
    {
        lock (_gate)
        {
            _primaryDevice = (player as WasapiPcmPlayer)?.EndpointId;
            UpdateAudible();
        }
    }
    private void UpdateAudible()
    {
        var duplicate = _primaryDevice == null ? null : Array.Find(_outputs, o => Ids.Equals(o.Id, _primaryDevice));
        if (duplicate != null) duplicate.Pcm.Clear();
        var audible = duplicate == null ? _outputs : _outputs.Where(o => o != duplicate).ToArray();
        Volatile.Write(ref _audible, audible);
    }
    internal void ClearBuffers() { foreach (var o in Volatile.Read(ref _outputs)) o.Pcm.Clear(); }
    internal void Write(ReadOnlySpan<float> samples, (int NonSilent, float Peak) metrics, AndroidPcmSource primary)
    {
        foreach (var output in Volatile.Read(ref _audible))
        {
            output.Pcm.HeaderGain = primary.HeaderGain;
            output.Pcm.WriteMeasured(samples, metrics);
        }
    }
    internal async Task ApplyAsync(string[] desired, Func<AndroidPcmSource, string, CancellationToken, IPcmPlayer> create,
        Func<Task> commitPrimary, CancellationToken token)
    {
        var previous = _outputs;
        if (desired.Length == previous.Length && previous.All(o => desired.Contains(o.Id, Ids)))
        { await commitPrimary().ConfigureAwait(false); return; }
        var next = new List<Output>(desired.Length);
        var staged = new List<Output>();
        try
        {
            foreach (var id in desired)
            {
                var output = Array.Find(previous, o => Ids.Equals(o.Id, id));
                if (output == null)
                {
                    var initial = InitialGain?.Invoke(id);
                    AudioLatencyMode latencyMode;
                    float volume, mirrorGain, headerGain;
                    bool smart;
                    lock (_gate)
                    {
                        // A live gain edit can arrive while a new renderer is being prepared.
                        if (initial is { } saved) _gains.TryAdd(id, saved);
                        latencyMode = _latencyMode;
                        volume = _volume;
                        mirrorGain = MirrorGainLocked(id);
                        smart = _smart;
                        headerGain = _headerGain;
                    }
                    var pcm = new AndroidPcmSource(latencyMode)
                    {
                        Volume = volume,
                        MirrorGain = mirrorGain,
                        SmartVolume = smart,
                        HeaderGain = headerGain
                    };
                    var player = create(pcm, id, token);
                    staged.Add(output = new Output(id, pcm, player));
                    void Failed(Exception error) => _ = Task.Run(() => removeFailed(p => ReferenceEquals(p, player)));
                    if (player is WasapiPcmPlayer wasapi)
                    {
                        wasapi.Failed += Failed;
                        await wasapi.PlayAndConfirmAsync(token).ConfigureAwait(false);
                    }
                    else
                    {
                        if (player is AndroidPhonePcmPlayer phone)
                        {
                            phone.Failed += Failed;
                            await phone.VerifyBeforePlaybackAsync(token).ConfigureAwait(false);
                            if (phone.Failure is { } error) throw error;
                        }
                        player.Play();
                    }
                }
                next.Add(output);
            }
            token.ThrowIfCancellationRequested();
            await commitPrimary().ConfigureAwait(false);
            Replace(next.ToArray());
            staged.Clear();
        }
        finally { foreach (var output in staged) Dispose(output.Player); }
    }
    internal bool Remove(Predicate<IPcmPlayer> match)
    {
        var next = _outputs.Where(o => !match(o.Player)).ToArray();
        if (next.Length == _outputs.Length) return false;
        Replace(next);
        return true;
    }
    internal void Clear() => Replace([], clearGains: true);

    private void Replace(Output[] next, bool clearGains = false)
    {
        Output[] previous;
        lock (_gate)
        {
            previous = _outputs;
            if (clearGains) _gains.Clear();
            foreach (var output in next)
            {
                output.Pcm.Volume = _volume;
                output.Pcm.MirrorGain = MirrorGainLocked(output.Id);
                output.Pcm.SmartVolume = _smart;
                output.Pcm.LatencyMode = _latencyMode;
                output.Pcm.HeaderGain = _headerGain;
            }
            Volatile.Write(ref _outputs, next);
            UpdateAudible();
        }
        foreach (var output in previous) if (!next.Contains(output)) Dispose(output.Player);
    }
    private static void Dispose(IPcmPlayer player)
    {
        try { player.Dispose(); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine("PCM mirror cleanup: " + ex.Message); }
    }
}
