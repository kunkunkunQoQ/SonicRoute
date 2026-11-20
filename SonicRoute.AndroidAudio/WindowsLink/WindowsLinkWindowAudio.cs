namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    public async Task<Guid> RequestPullWindowAsync(string peerId, LinkRemoteApp app, int x = 100, int y = 80, CancellationToken token = default)
        => (await OpenWindowWithAudioAsync(peerId, app, x, y, token).ConfigureAwait(false)).StreamId;

    internal async Task<(Guid StreamId, bool AudioAvailable)> OpenWindowWithAudioAsync(
        string peerId, LinkRemoteApp app, int x, int y, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(app);
        LinkConnection link;
        lock (_gate) link = Connected(peerId, app.IsMonitor ? LinkCapabilities.MonitorCapture : LinkCapabilities.WindowCapture);
        await link.ViewerStarts.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var video = await PullVideoAsync(link, app, x, y, token).ConfigureAwait(false);
            if (!link.Hello.Capabilities.HasFlag(LinkCapabilities.WindowAudio))
                return (video, AlreadyReceivingViewerAudio(link, app));
            try
            {
                var kind = app.IsMonitor ? LinkMediaKind.SystemAudio : LinkMediaKind.AppAudio;
                await SendPullRequestAsync(peerId, new LinkPullRequest(kind, null, null, null, WindowStream: video), token, link)
                    .ConfigureAwait(false);
                return (video, true);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
                || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                Report(ex);
                return (video, false);
            }
        }
        finally { link.ViewerStarts.Release(); }
    }

    private bool AlreadyReceivingViewerAudio(LinkConnection link, LinkRemoteApp app)
    {
        // Older senders cannot bind a new audio lifetime to a viewer. Recognize a manual
        // route only when its system key or process identity matches the requested source.
        string? audioInstance = null;
        if (!app.IsMonitor && app.InstanceId?.Split(':') is ["w", _, var pid, _, var born]
            && int.TryParse(pid, System.Globalization.NumberStyles.HexNumber, null, out int process) && process == app.ProcessId
            && long.TryParse(born, System.Globalization.NumberStyles.HexNumber, null, out long ticks))
            audioInstance = LinkRemoteApp.AudioInstanceId(process, ticks);
        lock (_gate) return _streams.Values.Any(t => t.Connection == link && !t.Closing && t.Announced && t.Info.Incoming && !t.Info.IsRelayed
            && (app.IsMonitor ? t.Info.Kind == LinkMediaKind.SystemAudio
                : audioInstance != null && t.Info.Kind == LinkMediaKind.AppAudio && t.Info.InstanceId == audioInstance));
    }

    public async Task<Guid> SendAudioAsync(string peerId, int? processId, string label,
        CancellationToken token = default, string? appKey = null, byte[]? iconPng = null, bool destinationOnly = false,
        string? instanceId = null, bool allowSpeakerMute = false)
    {
        LinkConnection link;
        lock (_gate) link = Connected(peerId, LinkCapabilities.AudioReceive);
        await link.AudioStarts.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await StartAudioAsync(link, processId, label, token, appKey, iconPng,
                destinationOnly, instanceId, windowOwned: false, allowSpeakerMute: allowSpeakerMute).ConfigureAwait(false);
        }
        finally { link.AudioStarts.Release(); }
    }

    private async Task<Guid> AttachWindowAudioAsync(LinkConnection link, Guid windowId, LinkMediaKind kind)
    {
        await link.AudioStarts.WaitAsync().ConfigureAwait(false);
        try
        {
            Track video;
            lock (_gate)
            {
                if (!_streams.TryGetValue(windowId, out video!) || video.Connection != link || video.Info.Incoming
                    || video.Closing || !video.Announced || video.Info.Kind != LinkMediaKind.Window
                    || (video.Monitor != null ? LinkMediaKind.SystemAudio : LinkMediaKind.AppAudio) != kind)
                    throw new IOException("The selected viewer is no longer sharing this source.");
                if (video.Companion is { Closing: false } active) return active.Info.Id;
            }

            LinkWindowAudioOwner? owner = null;
            if (video.Monitor == null)
            {
                var window = video.SharedWindow ?? throw new IOException("The shared window has no application owner.");
                var process = LinkWindowAudioOwner.Resolve(window);
                var source = process.Pid == window.ProcessId ? window : window with { ProcessId = process.Pid, ProcessName = process.Name };
                if (CanShareWindow?.Invoke(source) == false) throw new IOException("The shared application's audio is unavailable.");
                owner = process;
            }
            Track? audio = null;
            if (owner.HasValue)
                audio = FindRelatedAudio(link, owner.Value.Pid, owner.Value.InstanceId, onlyWindowOwned: false);
            if (audio == null)
            {
                var id = await StartAudioAsync(link, owner?.Pid, owner?.Name ?? _name, CancellationToken.None,
                    owner?.Name ?? LinkRemoteApp.SystemAudioKey, video.Info.IconPng, false, owner?.InstanceId, windowOwned: true)
                    .ConfigureAwait(false);
                lock (_gate) _streams.TryGetValue(id, out audio);
            }
            lock (_gate)
            {
                if (audio == null || audio.Closing) throw new IOException("The associated audio stream has stopped.");
                if (!video.Closing)
                {
                    video.Companion = audio;
                    return audio.Info.Id;
                }
            }
            await RetireOrphanAudioAsync(audio).ConfigureAwait(false);
            throw new IOException("The viewer was closed while its audio was starting.");
        }
        finally { link.AudioStarts.Release(); }
    }

    private Track? FindRelatedAudio(LinkConnection link, int ownerPid, string? instance, bool onlyWindowOwned)
    {
        Track[] candidates;
        lock (_gate) candidates = _streams.Values.Where(t => t.Connection == link && !t.Closing && t.Announced
            && !t.Info.Incoming && t.Process.HasValue && t.Info.Kind == LinkMediaKind.AppAudio
            && (!onlyWindowOwned || t.WindowOwned)).ToArray();
        return candidates.FirstOrDefault(t => t.Process == ownerPid && t.Info.InstanceId == instance)
            ?? candidates.FirstOrDefault(t => t.Process != ownerPid && t.Process is { } pid
                && t.Info.InstanceId == LinkRemoteApp.AudioInstanceId(pid, LinkWindowCapture.ReadProcess(pid).StartTicks)
                && LinkWindowAudioOwner.SameTree(ownerPid, pid));
    }

    private async Task ReleaseWindowAudioAsync(Track companion)
    {
        await companion.Connection.AudioStarts.WaitAsync().ConfigureAwait(false);
        try { await RetireOrphanAudioAsync(companion).ConfigureAwait(false); }
        finally { companion.Connection.AudioStarts.Release(); }
    }

    private Task RetireOrphanAudioAsync(Track audio)
    {
        lock (_gate)
        {
            if (!audio.WindowOwned || audio.Closing || _streams.Values.Any(t => !t.Closing && t.Companion == audio))
                return Task.CompletedTask;
        }
        return RetireAsync(audio, announce: true);
    }

    private void RefreshWindowAudioCoverageLocked(LinkConnection link)
    {
        bool systemPlaying = _streams.Values.Any(t => t.Connection == link && t.Announced && !t.Closing
            && !t.Info.Incoming && !t.Info.IsRelayed && t.Info.Kind == LinkMediaKind.SystemAudio);
        foreach (var app in _streams.Values)
            if (app.Connection == link && !app.Info.Incoming && app.Info.Kind == LinkMediaKind.AppAudio)
                app.CoveredBySystem = app.WindowOwned && systemPlaying;
    }
}
