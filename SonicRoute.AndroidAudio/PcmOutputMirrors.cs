using SonicRoute.WindowsLink;

namespace SonicRoute.AndroidAudio;

// Output changes are serialized by the owning session. PCM readers consume an immutable snapshot.
internal sealed class PcmOutputMirrors(bool preserveQuietLevel, Func<Predicate<IPcmPlayer>, Task> removeFailed,
    Func<string, bool>? forwardsPackets = null)
{
    private sealed record Output(string Id, AndroidPcmSource Pcm, IPcmPlayer Player);
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;
    private readonly object _gate = new();
    private Output[] _outputs = [], _audible = [];
    private float _volume = 1;
    private bool _smart;
    private string? _primaryDevice;
    private PhoneLinkAudioPlayer? _primaryPeer;
    private PhoneLinkAudioPlayer[] _peerPlayers = [];

    internal string[] IdsSnapshot() => Volatile.Read(ref _outputs).Select(output => output.Id).ToArray();
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
    internal void SetPrimary(IPcmPlayer? player)
    {
        lock (_gate)
        {
            _primaryDevice = (player as WasapiPcmPlayer)?.EndpointId;
            _primaryPeer = player as PhoneLinkAudioPlayer;
            UpdateAudible();
        }
    }
    private void UpdateAudible()
    {
        var duplicate = _primaryDevice == null ? null : Array.Find(_outputs, o => Ids.Equals(o.Id, _primaryDevice));
        if (duplicate != null) duplicate.Pcm.Clear();
        var audible = duplicate == null ? _outputs : _outputs.Where(o => o != duplicate).ToArray();
        Volatile.Write(ref _audible, audible);
        var peers = audible.Select(o => o.Player).OfType<PhoneLinkAudioPlayer>();
        Volatile.Write(ref _peerPlayers, _primaryPeer == null ? peers.ToArray() : peers.Prepend(_primaryPeer).ToArray());
    }
    internal void ClearBuffers() { foreach (var o in Volatile.Read(ref _outputs)) o.Pcm.Clear(); }
    internal void Write(ReadOnlySpan<float> samples, (int NonSilent, float Peak) metrics, AndroidPcmSource primary)
    {
        foreach (var output in Volatile.Read(ref _audible))
        {
            output.Pcm.HeaderGain = primary.HeaderGain;
            output.Pcm.WriteMeasured(samples, metrics);
        }
        var peers = Volatile.Read(ref _peerPlayers);
        if (peers.Length != 0) PhoneLinkAudioPlayer.Send(peers, samples, primary.EffectiveGain);
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
                    var pcm = new AndroidPcmSource(forwardsPackets?.Invoke(id) == true) { PreserveQuietLevel = preserveQuietLevel };
                    var player = create(pcm, id, token);
                    staged.Add(output = new Output(id, pcm, player));
                    void Failed(Exception error) => _ = Task.Run(() => removeFailed(p => ReferenceEquals(p, player)));
                    if (player is IFailedPcmPlayer reported) reported.Failed += Failed;
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
    internal void Clear() => Replace([]);
    private void Replace(Output[] next)
    {
        var previous = _outputs;
        lock (_gate)
        {
            foreach (var output in next) { output.Pcm.Volume = _volume; output.Pcm.SmartVolume = _smart; }
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
