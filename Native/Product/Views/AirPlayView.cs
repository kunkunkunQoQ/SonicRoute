#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.AirPlay;
using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

/// <summary>On-demand owner for the AirPlay receiver used by the native Full view.</summary>
internal sealed class AirPlayPreview : IDisposable
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private AirPlayReceiver? _receiver;
    private int _disposed;

    internal event Action? Changed;
    internal AirPlayStatus Status => Volatile.Read(ref _receiver)?.Status ?? new(AirPlayStage.Off);
    internal AirPlayAudioOutput? Source => Volatile.Read(ref _receiver)?.Source;
    internal int Port => Volatile.Read(ref _receiver)?.Port ?? 0;

    internal async Task SetReceivingAsync(AppConfig config, bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enabled)
            {
                if (_receiver is { Status.Stage: not AirPlayStage.Off }) return;
                if (_receiver is { } ended)
                {
                    _receiver = null;
                    await CloseAsync(ended).ConfigureAwait(false);
                }
                string name = AirPlayIdentity.NormalizeName(config.AirPlayReceiverName);
                if (name.Length == 0) name = DefaultName;
                var receiver = new AirPlayReceiver(name, player: static (pcm, endpoint, _) => new WasapiPcmPlayer(pcm, endpoint),
                    preferHevc: config.AirPlayVideoDecoder == 2, requireCode: config.AirPlayRequireCode);
                receiver.Changed += ReceiverChanged;
                receiver.DisplayChanged += ReceiverChanged;
                Volatile.Write(ref _receiver, receiver);
                _ = ObserveAsync(receiver);
            }
            else if (Interlocked.Exchange(ref _receiver, null) is { } receiver)
                await CloseAsync(receiver).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
            NotifyChanged();
        }
    }

    internal void ShowMirror() => Volatile.Read(ref _receiver)?.ShowMirror();
    internal void SetMirrorTopmost(bool value) => Volatile.Read(ref _receiver)?.SetMirrorTopmost(value);
    internal void SetMirrorFullscreen(bool value) => Volatile.Read(ref _receiver)?.SetMirrorFullscreen(value);
    internal void Disconnect() { if (Source is { } source) Volatile.Read(ref _receiver)?.Disconnect(source.Id); }

    private static string DefaultName
    {
        get
        {
            string host = string.Concat(Environment.MachineName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32));
            return host.Length == 0 ? "SonicRoute Preview" : "SonicRoute Preview - " + host;
        }
    }

    private void ReceiverChanged() => NotifyChanged();
    private void NotifyChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* A UI observer must not interrupt receiver teardown. */ }
    }

    private async Task ObserveAsync(AirPlayReceiver receiver)
    {
        try { await receiver.Completion.ConfigureAwait(false); }
        catch { /* The receiver reports connection failures through Status. */ }
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(receiver, Volatile.Read(ref _receiver))) return;
            Volatile.Write(ref _receiver, null);
            await CloseAsync(receiver).ConfigureAwait(false);
        }
        finally { _operations.Release(); NotifyChanged(); }
    }

    private async Task CloseAsync(AirPlayReceiver receiver)
    {
        receiver.Changed -= ReceiverChanged;
        receiver.DisplayChanged -= ReceiverChanged;
        await receiver.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _operations.Wait();
        try
        {
            if (Interlocked.Exchange(ref _receiver, null) is { } receiver)
                CloseAsync(receiver).GetAwaiter().GetResult();
        }
        finally { _operations.Release(); }
    }
}

internal sealed partial class ProductViews
{
    private bool observingAirPlay;
    private void ReleaseAirPlayView()
    {
        if (!observingAirPlay) return;
        host.AirPlay.Changed -= RefreshAirPlay;
        observingAirPlay = false;
    }

    private void BuildAirPlay()
    {
        if (!observingAirPlay)
        {
            observingAirPlay = true;
            host.AirPlay.Changed += RefreshAirPlay;
        }
        var status = host.AirPlay.Status;
        var source = host.AirPlay.Source;
        string statusText = status.Stage switch
        {
            AirPlayStage.Waiting => $"{T("Phone.AirPlayWaitingShort")} · TCP {host.AirPlay.Port}",
            AirPlayStage.Connected => string.Format(T("Phone.AirPlayConnected"), status.Sender ?? status.Name ?? "AirPlay"),
            AirPlayStage.Failed => T(status.ErrorKey ?? "Phone.AirPlayFailed"),
            AirPlayStage.Starting => T("Phone.AirPlayStarting"),
            AirPlayStage.Stopping => T("Phone.AirPlayStopping"),
            _ => T("Phone.AirPlayOff")
        };
        Header("AirPlay", statusText);
        bool active = status.Stage is AirPlayStage.Waiting or AirPlayStage.Connected;
        Add(active ? Label("停止接收", "Stop receiving") : T("Phone.AirPlayStart"),
            active ? "" : T("Phone.AirPlaySteps"),
            () => Run(() => host.AirPlay.SetReceivingAsync(config, !active)));
        if (status.Code is { Length: > 0 } code)
            Add("Pairing code", code, () => NativeDialogs.Input(Owner, "AirPlay pairing code", code, readOnly: true));
        if (status.MirrorAudioUnavailable)
            Add("Mirror audio", "Audio could not be opened for this mirror session.", selected: true);

        Add(T("Phone.AirPlayName"), config.AirPlayReceiverName.Length == 0 ? "SonicRoute Preview" : config.AirPlayReceiverName,
            () =>
            {
                string? name = NativeDialogs.Input(Owner, "AirPlay receiver name", config.AirPlayReceiverName);
                if (name == null) return;
                config.AirPlayReceiverName = AirPlayIdentity.NormalizeName(name);
                Persist(); Refresh();
            });
        Add(T("Phone.AirPlayRequireCode"), config.AirPlayRequireCode ? Label("开启", "On") : Label("关闭", "Off"), () =>
        {
            config.AirPlayRequireCode = !config.AirPlayRequireCode;
            Persist(); Refresh();
        }, selected: config.AirPlayRequireCode);
        string decoder = config.AirPlayVideoDecoder switch { 1 => "H.264", 2 => "Prefer HEVC", _ => "Automatic" };
        Add(Label("视频解码器", "Video decoder"), decoder, () =>
        {
            string current = config.AirPlayVideoDecoder.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string? choice = NativeDialogs.Input(Owner, "Video decoder: 0 automatic, 1 H.264, 2 prefer HEVC", current);
            if (choice == null || !int.TryParse(choice, out int value) || value is < 0 or > 2) return;
            config.AirPlayVideoDecoder = value;
            Persist(); Refresh();
        });

        if (source != null)
        {
            var audio = source.State;
            Add("AirPlay volume", $"{audio.Volume}%{(audio.Muted ? " · muted" : "")}", null,
                value => { source.SetVolume(value); Refresh(); }, audio.Volume);
            Add(audio.Muted ? "Unmute" : "Mute", "Adjusts local playback only.", () =>
            { source.SetMute(!audio.Muted); Refresh(); }, selected: audio.Muted);
            Add("Disconnect sender", source.Name, () =>
            {
                if (NativeDialogs.Confirm(Owner, "Disconnect AirPlay", "Disconnect the current sender?"))
                    host.AirPlay.Disconnect();
            });
            if (status.Mirroring)
            {
                Add("Show mirror", "Activate the video window", host.AirPlay.ShowMirror);
                Add(status.Topmost ? "Disable always on top" : "Keep on top", "", () => host.AirPlay.SetMirrorTopmost(!status.Topmost), selected: status.Topmost);
                Add(status.Fullscreen ? "Leave fullscreen" : "Fullscreen", "", () => host.AirPlay.SetMirrorFullscreen(!status.Fullscreen), selected: status.Fullscreen);
            }
            Header("Playback output", audio.OutputId ?? "System default");
            var route = (IMultiOutputAudioSession)source;
            var selected = route.GetOutputs();
            Add(T("Dev.SystemDefaultOut"), "", () => SelectPhoneAudioOutput(route, null), selected: selected.Contains(null));
            foreach (var endpoint in outputs)
            {
                string endpointId = endpoint.Id;
                Add(endpoint.Name, endpoint.Id, () => Run(() => source.SetOutputAsync(endpointId)), selected: string.Equals(audio.OutputId, endpoint.Id, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private void RefreshAirPlay()
    {
        host.TryPost(() => { if (observingAirPlay) Refresh(); });
    }

}
#endif
