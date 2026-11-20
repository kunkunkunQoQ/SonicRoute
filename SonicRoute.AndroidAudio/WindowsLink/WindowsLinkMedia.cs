using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    private const int MaxStreamsPerDirection = 5;
    private const int MaxWindowsPerDirection = 2;
    private static readonly TimeSpan CatalogCacheLifetime = TimeSpan.FromSeconds(3);
    private readonly Dictionary<(LinkConnection Link, bool Icons, LinkCatalogScope Scope), (long At, LinkRemoteApp[] Apps)> _catalogCache = new();
    private readonly Dictionary<(LinkConnection Link, bool Icons, LinkCatalogScope Scope), Task<LinkRemoteApp[]>> _inFlightCatalogs = new();
    private readonly Dictionary<LinkConnection, long> _catalogRevisions = new();
    internal Func<LinkCatalogScope, bool, CancellationToken, Task<LinkRemoteApp[]>>? ScopedCatalogProvider { get; init; }
    internal Func<LinkWindow, bool>? CanShareWindow { get; init; }
    private const int MaxCatalogEntries = 48;
    private const int MaxCatalogJsonBytes = 240 * 1024;
    private readonly Dictionary<Guid, Track> _streams = new();
    private readonly Dictionary<Guid, PendingCatalog> _pendingCatalogs = new();
    private readonly Dictionary<Guid, (LinkConnection Connection, TaskCompletionSource<LinkPullResponse> Completion)> _pendingPulls = new();
    private readonly List<PendingWindowPlacement> _pendingPlacements = new();
    private readonly List<PendingAudioHint> _pendingAudioHints = new();

    private sealed record StreamOffer(
        LinkMediaKind Kind,
        string Label,
        bool AllowInput,
        string? AppKey = null,
        string? IconPngBase64 = null,
        bool DestinationOnly = false,
        string? InstanceId = null,
        bool Hevc = false,
        int VideoWidth = 0,
        int VideoHeight = 0,
        Guid? ReplacesAudio = null,
        bool WindowOwned = false,
        bool NativeResolution = false,
        bool Relayed = false);

    private sealed record CatalogRequestPayload(bool IncludeIcons = true, bool AudioOnly = false, bool WindowsOnly = false,
        bool IncludeMonitors = false)
    {
        internal LinkCatalogScope Scope => AudioOnly ? LinkCatalogScope.Audio : WindowsOnly ? LinkCatalogScope.Windows : LinkCatalogScope.Apps;
    }

    private sealed class PendingCatalog(LinkConnection connection)
    {
        internal LinkConnection Connection { get; } = connection;
        internal TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? Buffer;
        internal int ExpectedLength;
        internal int ReceivedOffset;
        internal bool Started;
    }

    private sealed record PendingWindowPlacement(string PeerId, string? AppKey, string? InstanceId, byte[]? IconPng, int X, int Y, long At);
    private sealed record PendingAudioHint(string PeerId, string AppKey, string? InstanceId, byte[]? IconPng, long At);

    // A destination change can briefly have two confirmed receivers for the same process.
    // Both retain the original mute and restoration policy until the last receiver leaves.
    private sealed class ProcessSilence(IPcProcessSilenceLease lease, bool mutedSpeaker) : IDisposable
    {
        private int _owners = 1;
        internal bool MutedSpeaker { get; } = mutedSpeaker;
        internal CancellationToken Invalidated { get; } = lease.Invalidated;
        internal ProcessSilence Retain() { Interlocked.Increment(ref _owners); return this; }
        public void Dispose() { if (Interlocked.Decrement(ref _owners) == 0) lease.Dispose(); }
    }

    private ProcessSilence? FindSilenceLocked(int? pid, string? instance) =>
        pid > 0 && instance != null ? _streams.Values.FirstOrDefault(t => !t.Closing && t.Announced && t.Process == pid
            && t.Info.InstanceId == instance && t.LocalSilence is { Invalidated.IsCancellationRequested: false })?.LocalSilence : null;

    internal bool UsesSpeakerMute(Guid streamId)
    {
        lock (_gate) return _streams.TryGetValue(streamId, out var track) && track.LocalSilence?.MutedSpeaker == true;
    }

    private sealed class Track(LinkConnection connection, LinkMedia info)
    {
        private ILinkInput? _input;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkConnection Connection { get; } = connection;
        internal LinkMedia Info { get; set; } = info;
        internal Task Work = Task.CompletedTask;
        internal TaskCompletionSource? RemotePlaybackReady;
        internal ProcessSilence? LocalSilence;
        internal CancellationTokenRegistration LocalSilenceInvalidation;
        internal Guid SourceChangeId;
        internal TaskCompletionSource<bool>? SourceChangeReply;
        internal bool ChangingSource;
        internal bool PlaybackReplySent;
        internal LinkAudioPlayback? Audio;
        internal WindowsLinkAudioSource? AudioSource;
        internal LinkVideoViewer? Video;
        internal ILinkInput? Input { get => Volatile.Read(ref _input); set => Volatile.Write(ref _input, value); }
        internal ILinkInput? DetachInput() => Interlocked.Exchange(ref _input, null);
        internal bool UsesPeerInputPermission;
        internal LinkMonitor? Monitor;
        internal LinkWindow? SharedWindow;
        internal Track? Companion;
        internal bool WindowOwned;
        internal Track? RelaySource;
        internal ExternalAudioOutput? ExternalOutput;
        internal Track[] RelayTargets = [];
        internal RelayPlayer[] RelayPlayers = [];
        internal volatile bool CoveredBySystem;
        internal volatile bool StagingAudioReplacement;
        internal Guid? ReplacesAudio;
        internal int? Process;
        internal bool Closing;
        internal volatile bool OpenQueued;
        internal volatile bool SendingMedia;
        internal volatile bool Announced;
    }

    public LinkMedia[] Streams
    {
        get { lock (_gate) return _streams.Values.Where(t => !t.Closing && t.Announced).Select(t => t.Info).ToArray(); }
    }

    public WindowsLinkAudioSource[] IncomingAudioSources
    {
        get
        {
            lock (_gate)
            {
                return _streams.Values
                    .Where(t => !t.Closing && t.Announced && t.AudioSource != null)
                    .Select(t => t.AudioSource!)
                    .ToArray();
            }
        }
    }

    public WindowsLinkAudioSource? FindIncomingAudio(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return null;
        lock (_gate)
            return _streams.Values.FirstOrDefault(t => !t.Closing && t.Announced && t.AudioSource?.Id == sourceId)?.AudioSource;
    }

    internal LinkMedia? GetIncomingMedia(Guid streamId)
    {
        lock (_gate) return _streams.TryGetValue(streamId, out var track) && track.Info.Incoming && !track.Closing ? track.Info : null;
    }

    internal LinkMedia? FindOutgoingAppAudio(int processId)
    {
        if (processId <= 0) return null;
        lock (_gate)
        {
            LinkMedia? shared = null;
            foreach (var track in _streams.Values)
            {
                if (!IsOutgoingApp(track, processId)) continue;
                if (track.Info.DestinationOnly) return track.Info;
                shared ??= track.Info;
            }
            return shared;
        }
    }

    internal LinkMedia[] GetOutgoingAppStreams(int pid)
    {
        List<LinkMedia>? found = null;
        lock (_gate)
            foreach (var track in _streams.Values)
                if (IsOutgoingApp(track, pid)) (found ??= []).Add(track.Info);
        return found?.ToArray() ?? [];
    }

    private static bool IsOutgoingApp(Track track, int pid) => track.Process == pid && track.Announced && !track.Closing
        && !track.Info.Incoming && !track.Info.IsRelayed && track.Info.Kind == LinkMediaKind.AppAudio;

    public async Task StopOutgoingAudioForProcessAsync(int processId, bool onlyExclusive = false)
    {
        if (processId <= 0) return;
        Track[] tracks;
        lock (_gate)
        {
            tracks = _streams.Values
                .Where(t => !t.Closing && !t.Info.Incoming && t.Info.Kind == LinkMediaKind.AppAudio
                    && t.Process == processId && (!onlyExclusive || t.Info.DestinationOnly))
                .ToArray();
        }
        if (tracks.Length > 0)
            await Task.WhenAll(tracks.Select(t => RetireAsync(t, announce: true))).ConfigureAwait(false);
    }

    public async Task StopAllOutgoingAudioAsync()
    {
        Track[] tracks;
        lock (_gate)
        {
            tracks = _streams.Values
                .Where(t => !t.Closing && !t.Info.Incoming && !t.Info.IsRelayed && t.Info.Kind == LinkMediaKind.AppAudio)
                .ToArray();
        }
        if (tracks.Length > 0)
            await Task.WhenAll(tracks.Select(t => RetireAsync(t, announce: true))).ConfigureAwait(false);
    }

    public async Task StopPeerAppStreamsAsync(string peerId, string appKey, bool window = false, string? instanceId = null)
    {
        if (string.IsNullOrWhiteSpace(peerId) || string.IsNullOrWhiteSpace(appKey)) return;
        Track[] tracks;
        lock (_gate)
        {
            tracks = _streams.Values
                .Where(t => !t.Closing && t.Info.Incoming && (t.Info.Kind == LinkMediaKind.Window) == window
                    && (!t.Info.IsRelayed || instanceId != null)
                    && string.Equals(t.Info.PeerId, peerId, StringComparison.OrdinalIgnoreCase)
                    && MatchesApp(t.Info, appKey, instanceId))
                .ToArray();
        }
        if (tracks.Length > 0)
            await Task.WhenAll(tracks.Select(t => RetireAsync(t, announce: true))).ConfigureAwait(false);
    }

    public event Action<string>? RemoteCatalogChanged;

    public void NotifyLocalCatalogChanged()
    {
        LinkConnection[] peers;
        lock (_gate)
            peers = _connections.Values.Where(link => (link.Hello.Capabilities & LinkCapabilities.AudioCatalog) != 0).ToArray();
        foreach (var peer in peers) peer.NotifyCatalogChanged();
    }

    public void NotifyDisplayTopologyChanged()
    {
        Track[] sharing;
        lock (_gate) sharing = _streams.Values.Where(t => !t.Closing && t.Monitor != null).ToArray();
        if (sharing.Length != 0)
        {
            var current = LinkMonitors.GetActive();
            foreach (var track in sharing)
                if (!current.Any(m => m.InstanceId == track.Monitor!.InstanceId))
                    _ = RetireAsync(track, announce: true);
        }
        NotifyLocalCatalogChanged();
    }

    public async Task<LinkRemoteApp[]> RequestCatalogAsync(string peerId, bool includeIcons = true,
        CancellationToken token = default, bool audioOnly = false, bool windowsOnly = false, bool refresh = false)
    {
        token.ThrowIfCancellationRequested();
        Task<LinkRemoteApp[]> fetch;
        lock (_gate)
        {
            LinkConnection link = Connected(peerId, LinkCapabilities.RemoteBrowse);
            var request = new CatalogRequestPayload(includeIcons, audioOnly, windowsOnly,
                windowsOnly && link.Hello.Capabilities.HasFlag(LinkCapabilities.MonitorCapture));
            var cacheKey = (link, includeIcons, request.Scope);
            if (!refresh && _catalogCache.TryGetValue(cacheKey, out var cached)
                && Stopwatch.GetElapsedTime(cached.At) < CatalogCacheLifetime)
                return cached.Apps;
            if (!refresh && _inFlightCatalogs.TryGetValue(cacheKey, out var existing))
            {
                fetch = existing;
            }
            else
            {
                long revision = _catalogRevisions.GetValueOrDefault(link);
                fetch = FetchCatalogCoreAsync(peerId, link, request, revision, cacheKey);
                if (!refresh)
                {
                    _inFlightCatalogs[cacheKey] = fetch;
                    _ = fetch.ContinueWith(_ =>
                    {
                        lock (_gate)
                        {
                            if (_inFlightCatalogs.TryGetValue(cacheKey, out var active) && ReferenceEquals(active, fetch))
                                _inFlightCatalogs.Remove(cacheKey);
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        return await fetch.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task<LinkRemoteApp[]> FetchCatalogCoreAsync(string peerId, LinkConnection link,
        CatalogRequestPayload request, long revision, (LinkConnection Link, bool Icons, LinkCatalogScope Scope) cacheKey)
    {
        Guid requestId = Guid.NewGuid();
        var pending = new PendingCatalog(link);
        lock (_gate)
        {
            if (_connections.GetValueOrDefault(peerId) != link || !link.IsOpen)
                throw new InvalidOperationException("The Windows Link peer is disconnected.");
            _pendingCatalogs[requestId] = pending;
        }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            byte[] req = JsonSerializer.SerializeToUtf8Bytes(request);
            await link.SendAsync(LinkMessage.CatalogRequest, requestId, req, deadline.Token).ConfigureAwait(false);
            byte[] payload = await pending.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var apps = JsonSerializer.Deserialize<LinkRemoteApp[]>(payload) ?? [];
            var valid = apps.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Key) && !string.IsNullOrWhiteSpace(a.Name))
                // Older peers return their combined catalog and only identify active audio sessions.
                .Where(a => (!a.IsMonitor || request.IncludeMonitors) && InScope(a, request.Scope))
                .Take(MaxCatalogEntries)
                .Select(a => a with
                {
                    Key = a.Key.Trim(),
                    Name = Label(a.Name, a.Key.Trim()),
                    ProcessName = Label(a.ProcessName, a.Key.Trim()),
                    WindowTitle = string.IsNullOrWhiteSpace(a.WindowTitle) ? null : Label(a.WindowTitle, ""),
                    IconPng = request.IncludeIcons && a.IconPng is { Length: > 0 and <= MaxIconBytes } ? a.IconPng : null,
                    InstanceId = CleanInstanceId(a.InstanceId)
                })
                .ToArray();
            lock (_gate)
            {
                if (_connections.GetValueOrDefault(peerId) == link && _catalogRevisions.GetValueOrDefault(link) == revision)
                    _catalogCache[cacheKey] = (Stopwatch.GetTimestamp(), valid);
            }
            if (request.Scope != LinkCatalogScope.Windows)
                UpdatePinnedMetadata(peerId, valid);
            return valid;
        }
        finally
        {
            lock (_gate) _pendingCatalogs.Remove(requestId);
        }
    }

    public async Task<Guid> RequestPullAudioAsync(string peerId, LinkRemoteApp app, CancellationToken token = default,
        bool destinationOnly = false, bool allowSpeakerMute = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        bool useSpeakerFallback = destinationOnly && allowSpeakerMute && !app.IsSystemAudio && app.CanMuteSourceSpeaker;
        Guid? existingId = null;
        lock (_gate)
        {
            var link = Connected(peerId, app.IsSystemAudio ? LinkCapabilities.SystemAudio : LinkCapabilities.ProcessAudio);
            if (destinationOnly && !useSpeakerFallback && !app.SupportsExclusivePlayback(link.Hello.Capabilities))
                throw new NotSupportedException("The sending computer cannot move this audio exclusively.");
            var existing = _streams.Values.FirstOrDefault(t => !t.Closing && t.Info.Incoming && !t.Info.IsRelayed
                && t.Info.Kind != LinkMediaKind.Window
                && string.Equals(t.Info.PeerId, peerId, StringComparison.OrdinalIgnoreCase)
                && MatchesApp(t.Info, app.Key, app.InstanceId)
                && t.Info.DestinationOnly == destinationOnly && t.Audio?.State.State == CaptureState.Receiving);
            // A manual pull also adopts an existing window-owned track at the sender.
            if (existing is { WindowOwned: false }) existingId = existing.Info.Id;
        }
        if (existingId.HasValue) return existingId.Value;

        if (app.IconPng is { Length: > 0 and <= MaxIconBytes })
        {
            lock (_gate)
            {
                PruneExpiredHints();
                _pendingAudioHints.Add(new PendingAudioHint(peerId, app.Key, app.InstanceId, app.IconPng, Stopwatch.GetTimestamp()));
            }
        }
        var request = new LinkPullRequest(
            app.IsSystemAudio ? LinkMediaKind.SystemAudio : LinkMediaKind.AppAudio,
            app.Key,
            app.ProcessId > 0 ? app.ProcessId : null,
            null, DestinationOnly: destinationOnly, InstanceId: app.InstanceId, AllowSpeakerMute: useSpeakerFallback);
        var response = await SendPullRequestAsync(peerId, request, token).ConfigureAwait(false);
        lock (_gate)
            if (_streams.TryGetValue(response.StreamId, out var adopted) && adopted.Info.Incoming) adopted.WindowOwned = false;
        return response.StreamId;
    }

    private async Task<Guid> PullVideoAsync(LinkConnection link, LinkRemoteApp app, int x, int y, CancellationToken token)
    {
        string peerId = link.Id;
        Track? existing = null;
        LinkCapabilities remoteCapabilities;
        lock (_gate)
        {
            if (Connected(peerId, app.IsMonitor ? LinkCapabilities.MonitorCapture : LinkCapabilities.WindowCapture) != link)
                throw new IOException("The connection changed before opening the viewer.");
            remoteCapabilities = link.Hello.Capabilities;
            existing = _streams.Values.FirstOrDefault(t => !t.Closing && t.Info.Incoming
                && t.Info.Kind == LinkMediaKind.Window
                && string.Equals(t.Info.PeerId, peerId, StringComparison.OrdinalIgnoreCase)
                && MatchesApp(t.Info, app.Key, app.InstanceId));
        }
        if (existing != null)
        {
            existing.Video?.Show();
            return existing.Info.Id;
        }

        var settings = GetPeerSettings(peerId);
        LinkVideoOptions options = settings.VideoOptions();
        options.RequirePeerSupport(link.Hello);
        lock (_gate)
        {
            PruneExpiredHints();
            _pendingPlacements.Add(new PendingWindowPlacement(
                peerId,
                app.Key,
                app.InstanceId,
                app.IconPng is { Length: > 0 and <= MaxIconBytes } ? app.IconPng : null,
                x, y, Stopwatch.GetTimestamp()));
        }
        var request = new LinkPullRequest(
            LinkMediaKind.Window,
            app.Key,
            app.ProcessId > 0 ? app.ProcessId : null,
            app.WindowHandle != 0 ? app.WindowHandle : null,
            options.BitRate,
            options.MaxFps,
            options.MaxHeight, InstanceId: app.InstanceId, VideoCodec: settings.RequestedVideoCodec(LocalCapabilities, remoteCapabilities),
            NativeResolution: options.IsNative);
        var response = await SendPullRequestAsync(peerId, request, token, link).ConfigureAwait(false);
        return response.StreamId;
    }

    private async Task<LinkPullResponse> SendPullRequestAsync(string peerId, LinkPullRequest request, CancellationToken token,
        LinkConnection? expected = null)
    {
        LinkConnection link;
        Guid requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<LinkPullResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_connections.TryGetValue(peerId, out link!) || !link.IsOpen || expected != null && link != expected)
                throw new InvalidOperationException("The Windows Link peer is disconnected.");
            _pendingPulls[requestId] = (link, completion);
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(request);
            await link.SendAsync(LinkMessage.PullRequest, requestId, body, deadline.Token).ConfigureAwait(false);
            LinkPullResponse response = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!response.Accepted)
                throw new IOException(string.IsNullOrWhiteSpace(response.Error) ? "The remote computer could not start this stream." : response.Error);
            return response;
        }
        finally
        {
            lock (_gate) _pendingPulls.Remove(requestId);
        }
    }

    private async Task<Guid> StartAudioAsync(LinkConnection link, int? processId, string label,
        CancellationToken token, string? appKey, byte[]? iconPng, bool destinationOnly,
        string? instanceId, bool windowOwned, bool allowSpeakerMute = false)
    {
        if (!CanSendAudio) throw new NotSupportedException("WASAPI process loopback is unavailable on the sending computer.");
        if (processId.HasValue && (processId <= 0 || processId == Environment.ProcessId))
            throw new ArgumentException("Select another running application.", nameof(processId));
        if (processId is { } pid && instanceId == null)
            instanceId = LinkRemoteApp.AudioInstanceId(pid, LinkWindowCapture.ReadProcess(pid).StartTicks);
        if (destinationOnly)
        {
            RefreshExclusiveCapability(force: true);
            bool alreadyIsolated;
            lock (_gate) alreadyIsolated = FindSilenceLocked(processId, instanceId) != null;
            if (!alreadyIsolated && !CanSendExclusively && !(allowSpeakerMute && CanUseSpeakerFallback))
            {
                if (!allowSpeakerMute && processId.HasValue && link.Hello.Capabilities.HasFlag(LinkCapabilities.AudioReady)
                    && CanOfferSpeakerMute(processId.Value))
                    throw new SourceSpeakerConsentRequiredException("Muting the source speaker requires confirmation.");
                throw new NotSupportedException("The sending computer has no available output for destination-only audio.");
            }
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string effectiveKey = !string.IsNullOrWhiteSpace(appKey) ? appKey.Trim()
            : processId == null ? LinkRemoteApp.SystemAudioKey : ResolveProcessKey(processId.Value, label);
        byte[]? safeIcon = iconPng is { Length: > 0 and <= MaxIconBytes } ? iconPng : null;
        Track track;
        Track? previous;
        // A user may now select a specific renderer that the window's process-tree capture covered.
        Track? related = !windowOwned && processId is { } manualPid
            ? FindRelatedAudio(link, manualPid, instanceId, onlyWindowOwned: true) : null;
        lock (_gate)
        {
            if (Connected(link.Id, LinkCapabilities.AudioReceive) != link) throw new IOException("The audio connection changed.");
            if (_streams.Values.Any(t => t.ChangingSource && OverlapsSource(t, processId)))
                throw new InvalidOperationException("The source playback device is changing.");
            if (_streams.Values.Any(t => !t.Closing && !t.Info.Incoming && t.Info.Kind == LinkMediaKind.SystemAudio
                && t.LocalSilence != null && (processId.HasValue || t.Connection != link)))
                throw new InvalidOperationException("A whole-computer stream is using the source speakers.");
            if (destinationOnly && (processId == null
                || (link.Hello.Capabilities & LinkCapabilities.AudioReady) == 0))
                throw new NotSupportedException("Exclusive playback requires a supported source app and an updated receiver.");
            previous = _streams.Values.FirstOrDefault(t => t.Connection == link && !t.Info.Incoming && !t.Info.IsRelayed
                && t.Info.Kind != LinkMediaKind.Window && t.Process == processId && !t.Closing);
            if (previous == null && related is { Closing: false }) previous = related;
            if (previous is { Announced: false }) throw new InvalidOperationException("This audio stream is starting.");
            if (previous != null && previous.Info.InstanceId == instanceId && (previous.Info.DestinationOnly == destinationOnly || windowOwned))
            {
                if (!windowOwned) { previous.WindowOwned = false; previous.CoveredBySystem = false; }
                return previous.Info.Id;
            }
            track = Reserve(link, processId == null ? LinkMediaKind.SystemAudio : LinkMediaKind.AppAudio,
                label, allowInput: false, effectiveKey, processId, safeIcon, replacesAudio: previous?.Info.Id);
            track.Info = track.Info with { DestinationOnly = destinationOnly, InstanceId = instanceId };
            if ((link.Hello.Capabilities & LinkCapabilities.AudioReady) != 0)
                track.RemotePlaybackReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            track.Process = processId;
            track.WindowOwned = windowOwned;
            track.StagingAudioReplacement = previous != null;
            track.Work = Task.Run(() => CaptureAudio(processId, data =>
            {
                if (track.SendingMedia && !track.CoveredBySystem && !track.StagingAudioReplacement)
                    link.SendAudio(track.Info.Id, data.Span);
            }, () => started.TrySetResult(), track.Stop.Token));
        }
        await OpenTrackAsync(track, started.Task, token, allowSpeakerMute: allowSpeakerMute).ConfigureAwait(false);
        lock (_gate)
        {
            if (previous != null)
            {
                previous.SendingMedia = false;
                foreach (var video in _streams.Values)
                    if (video.Companion == previous) video.Companion = track;
                track.StagingAudioReplacement = false;
            }
            RefreshWindowAudioCoverageLocked(link);
        }
        _ = WatchCaptureAsync(track);
        if (previous != null) await RetireAsync(previous, announce: true).ConfigureAwait(false);
        return track.Info.Id;
    }

    public Task<Guid> ShareWindowAsync(string peerId, nint hwnd, string label, bool allowInput,
        CancellationToken token = default, LinkVideoOptions? videoOptions = null, string? appKey = null, byte[]? iconPng = null)
    {
        var window = LinkWindowCapture.ReadWindow(hwnd) ?? throw new InvalidOperationException("The selected window is no longer available.");
        string key = string.IsNullOrWhiteSpace(appKey) ? ResolveProcessKey(window.ProcessId, label) : appKey.Trim();
        return ShareVideoAsync(peerId, window, label, key, allowInput, token, videoOptions, iconPng);
    }

    private async Task<Guid> ShareVideoAsync(string peerId, LinkCaptureTarget target, string label, string appKey, bool? allowInput,
        CancellationToken token = default, LinkVideoOptions? videoOptions = null, byte[]? iconPng = null)
    {
        var window = target as LinkWindow;
        byte[]? safeIcon = iconPng is { Length: > 0 and <= MaxIconBytes } ? iconPng : null;
        LinkVideoOptions options = (videoOptions ?? GetPeerSettings(peerId).VideoOptions()).Clamp();
        Track track;
        var first = new TaskCompletionSource<(byte[] Data, long Pts)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var link = Connected(peerId, LinkCapabilities.WindowReceive);
            options.RequirePeerSupport(link.Hello);
            var existing = _streams.Values.FirstOrDefault(t => t.Connection == link && !t.Info.Incoming
                && t.Info.Kind == LinkMediaKind.Window && t.Info.InstanceId == target.InstanceId && !t.Closing);
            if (existing != null && existing.Announced) return existing.Info.Id;
            bool inputEnabled = allowInput ?? ((GetPeerSettings(peerId).AllowRemoteInput || IsFullyTrusted(link))
                && (LocalCapabilities & LinkCapabilities.WindowInput) != 0);
            track = Reserve(link, LinkMediaKind.Window, label, inputEnabled, appKey, window?.ProcessId, safeIcon);
            track.UsesPeerInputPermission = !allowInput.HasValue;
            track.Monitor = target as LinkMonitor;
            track.SharedWindow = window;
            bool hevc = options.Encoder == "h265" && LocalCapabilities.HasFlag(LinkCapabilities.HevcEncode)
                && link.Hello.Capabilities.HasFlag(LinkCapabilities.HevcDecode);
            track.Info = track.Info with { InstanceId = target.InstanceId, Hevc = hevc, NativeResolution = options.IsNative };
            if (inputEnabled) track.Input = window != null
                ? new LinkWindowInput(window.Handle, window.ProcessId, window.ThreadId)
                : new LinkMonitorInput((LinkMonitor)target, CanShareWindow);
            track.Work = Task.Run(() => CaptureVideoAsync(track, target, options, first, track.Stop.Token));
        }
        await OpenTrackAsync(track, first.Task, token, () =>
        {
            var initial = first.Task.Result;
            track.Connection.SendVideo(track.Info.Id, initial.Data, initial.Pts, key: true);
        }).ConfigureAwait(false);
        _ = WatchCaptureAsync(track);
        return track.Info.Id;
    }

    private static string ResolveProcessKey(int processId, string fallback)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            if (!string.IsNullOrWhiteSpace(proc.ProcessName)) return proc.ProcessName;
        }
        catch { }
        return Label(fallback, $"pid:{processId}");
    }

    private LinkConnection Connected(string peerId, LinkCapabilities capability)
    {
        if (!_connections.TryGetValue(peerId, out var link) || !link.IsOpen)
            throw new InvalidOperationException("The Windows Link peer is disconnected.");
        if ((link.Hello.Capabilities & capability) == 0)
            throw new NotSupportedException("The peer cannot support this Windows Link capability.");
        return link;
    }

    private Track Reserve(LinkConnection link, LinkMediaKind kind, string label, bool allowInput,
        string? appKey = null, int? processId = null, byte[]? iconPng = null, Guid? replacesAudio = null)
    {
        if (!CanOpen(link, kind, incoming: false, replacesAudio))
            throw new InvalidOperationException("Stop an existing stream before opening another.");
        var info = new LinkMedia(Guid.NewGuid(), link.Id, kind, Label(label, "Windows"),
            false, 1f, false, null, allowInput, appKey, processId, iconPng);
        var track = new Track(link, info) { ReplacesAudio = replacesAudio };
        _streams.Add(info.Id, track);
        return track;
    }

    private bool CanOpen(LinkConnection link, LinkMediaKind kind, bool incoming, Guid? replacesAudio = null)
    {
        var direction = _streams.Values.Where(t => t.Connection == link && t.Info.Incoming == incoming).ToArray();
        if (direction.Length == MaxStreamsPerDirection && kind != LinkMediaKind.Window && replacesAudio.HasValue)
            return direction.Any(t => t.Info.Id == replacesAudio && !t.Closing && t.Info.Kind != LinkMediaKind.Window);
        return direction.Length < MaxStreamsPerDirection
            && (kind != LinkMediaKind.Window || direction.Count(t => t.Info.Kind == LinkMediaKind.Window) < MaxWindowsPerDirection);
    }

    private async Task OpenTrackAsync(Track track, Task ready, CancellationToken token, Action? sendInitial = null, bool allowSpeakerMute = false)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, track.Stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            if (!track.Info.IsRelayed && await Task.WhenAny(ready, track.Work).WaitAsync(deadline.Token).ConfigureAwait(false) == track.Work)
            {
                await track.Work.ConfigureAwait(false);
                throw new IOException("The capture ended before producing a stream.");
            }
            await ready.ConfigureAwait(false);
            var info = track.Info;
            string? iconB64 = EncodeIconBase64(info.IconPng);
            var offer = new StreamOffer(info.Kind, info.Name, info.AllowsInput, info.AppKey, iconB64, info.DestinationOnly,
                info.InstanceId, info.Hevc, info.VideoWidth, info.VideoHeight, track.ReplacesAudio, track.WindowOwned, info.NativeResolution,
                Relayed: info.IsRelayed);
            byte[] open = JsonSerializer.SerializeToUtf8Bytes(offer);
            if (open.Length > 12 * 1024) open = JsonSerializer.SerializeToUtf8Bytes(offer with { IconPngBase64 = null });
            track.OpenQueued = true;
            await track.Connection.SendAsync(LinkMessage.Open, info.Id, open, deadline.Token).ConfigureAwait(false);
            sendInitial?.Invoke();
            // An exclusive receiver can open its player before any PCM arrives.
            track.SendingMedia = !info.DestinationOnly;
            if (track.RemotePlaybackReady is { } playback)
            {
                try { await playback.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token).ConfigureAwait(false); }
                catch (TimeoutException ex) { throw new IOException("The receiving computer did not start audio playback.", ex); }
                catch (OperationCanceledException) when (playback.Task.IsFaulted)
                { await playback.Task.ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                { throw new IOException("The remote stream stopped before audio playback was confirmed.", ex); }
            }
            if (info.DestinationOnly)
            {
                InstallLocalSilence(track, AcquireAudioSilence(track, allowSpeakerMute, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
            }
            lock (_gate)
            {
                if (track.Closing || track.LocalSilence?.Invalidated.IsCancellationRequested == true)
                    throw new IOException("The stream or its local audio route became unavailable.");
                track.Announced = true;
                track.SendingMedia = true;
            }
            Notify();
        }
        catch
        {
            await RetireAsync(track, announce: true).ConfigureAwait(false);
            throw;
        }
    }

    private async Task WatchCaptureAsync(Track track)
    {
        try { await track.Work.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!track.Finished.Task.IsCompleted) Report(ex); }
        await RetireAsync(track, announce: true).ConfigureAwait(false);
    }

    public static LinkWindow[] GetWindows() => LinkWindowCapture.GetWindows();

    public async Task SetPlaybackAsync(Guid id, string? endpointId, float volume, bool muted)
    {
        Track track;
        if (!float.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));
        volume = Math.Clamp(volume, 0f, 1f);
        lock (_gate)
        {
            if (!_streams.TryGetValue(id, out track!) || track.Closing || track.Info.Kind == LinkMediaKind.Window) return;
        }
        if (track.Info.Incoming)
        {
            if (track.Audio != null)
            {
                track.Audio.SetPlaybackLevel(volume, muted);
                if (!string.Equals(track.Audio.State.OutputId, endpointId, StringComparison.OrdinalIgnoreCase))
                    await track.Audio.SetOutputAsync(endpointId).ConfigureAwait(false);
            }
        }
        else
        {
            byte[] body = new byte[5];
            BinaryPrimitives.WriteSingleLittleEndian(body, volume);
            body[4] = muted ? (byte)1 : (byte)0;
            await track.Connection.SendAsync(LinkMessage.Volume, id, body).ConfigureAwait(false);
            lock (_gate)
                if (!track.Closing) track.Info = track.Info with { Volume = volume, Muted = muted };
            Notify();
        }
    }

    private void SyncIncomingAudioState(Guid streamId)
    {
        bool updated = false;
        lock (_gate)
        {
            if (_streams.TryGetValue(streamId, out var track) && !track.Closing && track.Audio != null)
            {
                var state = track.Audio.State;
                track.Info = track.Info with
                {
                    OutputId = state.OutputId,
                    Volume = state.Volume / 100f,
                    Muted = state.Muted
                };
                updated = true;
            }
        }
        if (updated) Notify();
    }

    public void ShowStream(Guid id)
    {
        lock (_gate) if (_streams.TryGetValue(id, out var track)) track.Video?.Show();
    }

    public Task StopStreamAsync(Guid id)
    {
        Track? track;
        lock (_gate) _streams.TryGetValue(id, out track);
        return track == null ? Task.CompletedTask : RetireAsync(track, announce: true);
    }

    private Task CloseConnectionStreamsAsync(LinkConnection connection)
    {
        Track[] tracks;
        lock (_gate) tracks = _streams.Values.Where(t => t.Connection == connection).ToArray();
        return Task.WhenAll(tracks.Select(track => RetireAsync(track, announce: false)));
    }

    private void CancelConnectionRequests(LinkConnection connection)
    {
        List<PendingCatalog> catalogs = [];
        List<TaskCompletionSource<LinkPullResponse>> pulls = [];
        lock (_gate)
        {
            ClearCatalogCacheLocked(connection);
            _catalogRevisions.Remove(connection);
            foreach (var entry in _pendingCatalogs.Where(kv => kv.Value.Connection == connection).ToArray())
            {
                _pendingCatalogs.Remove(entry.Key);
                catalogs.Add(entry.Value);
            }
            foreach (var entry in _pendingPulls.Where(kv => kv.Value.Connection == connection).ToArray())
            {
                _pendingPulls.Remove(entry.Key);
                pulls.Add(entry.Value.Completion);
            }
        }
        foreach (var cat in catalogs)
            cat.Completion.TrySetException(new IOException("The Windows Link peer disconnected."));
        foreach (var pull in pulls)
            pull.TrySetException(new IOException("The Windows Link peer disconnected."));
    }

    private async Task DispatchMediaAsync(LinkConnection link, LinkMessage type, Guid id, ReadOnlyMemory<byte> payload)
    {
        if (type == LinkMessage.CatalogChanged)
        {
            lock (_gate)
            {
                if (_connections.GetValueOrDefault(link.Id) != link) return;
                ClearCatalogCacheLocked(link);
                _catalogRevisions[link] = _catalogRevisions.GetValueOrDefault(link) + 1;
            }
            RemoteCatalogChanged?.Invoke(link.Id);
            return;
        }
        if (type == LinkMessage.CatalogRequest)
        {
            CatalogRequestPayload request = new();
            if (!payload.IsEmpty)
            {
                try { request = JsonSerializer.Deserialize<CatalogRequestPayload>(payload.Span) ?? request; }
                catch (JsonException) { }
            }
            _ = Task.Run(() => HandleCatalogRequestAsync(link, id, request));
            return;
        }
        if (type == LinkMessage.CatalogResponse)
        {
            HandleCatalogResponseChunk(link, id, payload.Span);
            return;
        }
        if (type == LinkMessage.PullRequest)
        {
            LinkPullRequest? pull;
            try { pull = JsonSerializer.Deserialize<LinkPullRequest>(payload.Span); }
            catch (JsonException) { throw new InvalidDataException("Invalid Windows Link pull request."); }
            if (pull == null || pull.Kind is not (LinkMediaKind.SystemAudio or LinkMediaKind.AppAudio or LinkMediaKind.Window))
                throw new InvalidDataException("Invalid Windows Link pull request.");
            _ = Task.Run(() => HandlePullRequestAsync(link, id, pull));
            return;
        }
        if (type == LinkMessage.PullResponse)
        {
            LinkPullResponse? response;
            try { response = JsonSerializer.Deserialize<LinkPullResponse>(payload.Span); }
            catch (JsonException) { throw new InvalidDataException("Invalid Windows Link pull response."); }
            if (response == null) return;
            TaskCompletionSource<LinkPullResponse>? waiter = null;
            lock (_gate)
            {
                if (_pendingPulls.TryGetValue(id, out var entry) && entry.Connection == link)
                {
                    _pendingPulls.Remove(id);
                    waiter = entry.Completion;
                }
            }
            waiter?.TrySetResult(response);
            return;
        }

        if (type == LinkMessage.Open)
        {
            StreamOffer? offer = JsonSerializer.Deserialize<StreamOffer>(payload.Span);
            if (offer == null || offer.Kind is not (LinkMediaKind.Window or LinkMediaKind.AppAudio or LinkMediaKind.SystemAudio)
                || (offer.Relayed && (offer.Kind == LinkMediaKind.Window || !link.Hello.SupportsAudioRelay))
                || string.IsNullOrWhiteSpace(offer.Label) || offer.Label.Length > 64 || offer.Label.Any(char.IsControl)
                || (offer.AllowInput && offer.Kind != LinkMediaKind.Window))
                throw new InvalidDataException("Invalid Windows Link media offer.");
            if (!OpenIncoming(link, id, offer))
            {
                if (offer.Kind != LinkMediaKind.Window && (link.Hello.Capabilities & LinkCapabilities.AudioReady) != 0)
                    await link.SendAsync(LinkMessage.AudioReady, id, "The receiving computer has no free audio stream."u8.ToArray()).ConfigureAwait(false);
                await SendCloseAsync(link, id).ConfigureAwait(false);
            }
            return;
        }

        Track track;
        lock (_gate)
            if (!_streams.TryGetValue(id, out track!) || track.Connection != link || track.Closing) return;

        switch (type)
        {
            case LinkMessage.SourcePlayback when !track.Info.Incoming && link.Hello.SupportsSourcePlayback:
                DispatchSourcePlayback(track, payload.Span);
                break;
            case LinkMessage.SourcePlaybackReply when track.Info.Incoming && link.Hello.SupportsSourcePlayback:
                ReceiveSourcePlaybackReply(track, payload.Span);
                break;
            case LinkMessage.AudioReady when !track.Info.Incoming && track.RemotePlaybackReady is { } playback:
                if (payload.IsEmpty) playback.TrySetResult();
                else playback.TrySetException(new IOException(Encoding.UTF8.GetString(payload.Span)));
                break;
            case LinkMessage.Close:
                await RetireAsync(track, announce: false).ConfigureAwait(false);
                break;
            case LinkMessage.Audio when track.Info.Incoming:
                track.Audio?.Push(payload.Span);
                ForwardRelayAudio(track, payload.Span);
                break;
            case LinkMessage.Video when track.Info.Incoming:
                track.Video?.Push(payload.Span);
                break;
            case LinkMessage.Volume when track.Info.Incoming && track.Audio != null:
                float volume = BinaryPrimitives.ReadSingleLittleEndian(payload.Span);
                if (!float.IsFinite(volume) || volume is < 0 or > 1 || payload.Span[4] > 1)
                    throw new InvalidDataException("Invalid Windows Link volume.");
                track.Audio.SetPlaybackLevel(volume, payload.Span[4] == 1);
                break;
            case LinkMessage.Input when !track.Info.Incoming && CanDeliverWindowInput(track):
                track.Input?.Apply(payload.Span);
                break;
        }
    }

    private void HandleCatalogResponseChunk(LinkConnection link, Guid requestId, ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < 5) throw new InvalidDataException("Invalid Windows Link catalog chunk.");
        byte flags = chunk[0];
        if ((flags & ~3) != 0) throw new InvalidDataException("Invalid Windows Link catalog flags.");
        int totalLength = BinaryPrimitives.ReadInt32LittleEndian(chunk[1..]);
        if (totalLength is < 2 or > LinkConnection.MaxCatalogBytes)
            throw new InvalidDataException("Invalid Windows Link catalog length.");
        ReadOnlySpan<byte> slice = chunk[5..];

        PendingCatalog? pending;
        bool complete = false;
        byte[]? result = null;
        lock (_gate)
        {
            if (!_pendingCatalogs.TryGetValue(requestId, out pending) || pending.Connection != link) return;
            if ((flags & 1) != 0)
            {
                if (pending.Started) throw new InvalidDataException("Duplicate Windows Link catalog start.");
                pending.Started = true;
                pending.ExpectedLength = totalLength;
                pending.Buffer = new byte[totalLength];
                pending.ReceivedOffset = 0;
            }
            if (!pending.Started || pending.Buffer == null || pending.ExpectedLength != totalLength
                || pending.ReceivedOffset + slice.Length > pending.ExpectedLength)
                throw new InvalidDataException("Corrupt Windows Link catalog sequence.");
            slice.CopyTo(pending.Buffer.AsSpan(pending.ReceivedOffset));
            pending.ReceivedOffset += slice.Length;
            if ((flags & 2) != 0)
            {
                if (pending.ReceivedOffset != pending.ExpectedLength)
                    throw new InvalidDataException("Incomplete Windows Link catalog response.");
                _pendingCatalogs.Remove(requestId);
                result = pending.Buffer;
                complete = true;
            }
        }
        if (complete && result != null) pending!.Completion.TrySetResult(result);
    }

    private async Task HandleCatalogRequestAsync(LinkConnection link, Guid requestId, CatalogRequestPayload request)
    {
        try
        {
            if (request.Scope == LinkCatalogScope.Audio) RefreshExclusiveCapability();
            LinkRemoteApp[] raw = await ReadLocalCatalogAsync(request.Scope, request.IncludeIcons).ConfigureAwait(false);

            IEnumerable<LinkRemoteApp> entries = raw;
            if (request.IncludeMonitors && request.Scope == LinkCatalogScope.Windows
                && LocalCapabilities.HasFlag(LinkCapabilities.MonitorCapture))
                entries = LinkMonitors.GetActive().Select(m => m.ToCatalogEntry()).Concat(raw);
            var localRoute = _localAudioRoute;
            var bounded = entries
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Key) && !string.IsNullOrWhiteSpace(a.Name))
                .Where(a => InScope(a, request.Scope))
                .Take(MaxCatalogEntries)
                .Select(a => a with
                {
                    CanPlayExclusively = a.IsSystemAudio || request.Scope == LinkCatalogScope.Windows ? null : localRoute.HasFlag(WindowsLinkExclusiveMode.DedicatedOutput),
                    CanMuteSourceSpeaker = a.ProcessId > 0 && !a.IsSystemAudio && request.Scope != LinkCatalogScope.Windows && localRoute.HasFlag(WindowsLinkExclusiveMode.MuteSourceSpeaker),
                    IconPng = request.IncludeIcons && a.IconPng is { Length: > 0 and <= MaxIconBytes } ? a.IconPng : null
                })
                .ToArray();

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(bounded);
            if (payload.Length > MaxCatalogJsonBytes)
            {
                bounded = bounded.Select(a => a with { IconPng = null }).ToArray();
                payload = JsonSerializer.SerializeToUtf8Bytes(bounded);
            }
            if (payload.Length > LinkConnection.MaxCatalogBytes)
                payload = JsonSerializer.SerializeToUtf8Bytes(Array.Empty<LinkRemoteApp>());

            await link.SendCatalogResponseAsync(requestId, payload).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private async Task HandlePullRequestAsync(LinkConnection link, Guid requestId, LinkPullRequest request)
    {
        try
        {
            Guid streamId;
            if (request.AllowSpeakerMute && (!request.DestinationOnly || request.Kind != LinkMediaKind.AppAudio || request.WindowStream.HasValue))
                throw new InvalidOperationException("Source speaker muting requires an application-only audio request.");
            if (request.WindowStream is { } windowStream)
            {
                streamId = await AttachWindowAudioAsync(link, windowStream, request.Kind).ConfigureAwait(false);
            }
            else if (request.Kind == LinkMediaKind.SystemAudio)
            {
                streamId = await SendAudioAsync(link.Id, null, _name, appKey: LinkRemoteApp.SystemAudioKey).ConfigureAwait(false);
            }
            else if (request.Kind == LinkMediaKind.AppAudio)
            {
                var resolved = await ResolveAudioTargetAsync(request).ConfigureAwait(false);
                streamId = await SendAudioAsync(link.Id, resolved.ProcessId, resolved.Name,
                    appKey: resolved.Key, iconPng: resolved.IconPng, destinationOnly: request.DestinationOnly,
                    instanceId: resolved.InstanceId, allowSpeakerMute: request.AllowSpeakerMute).ConfigureAwait(false);
            }
            else
            {
                var resolved = ResolveVideoTarget(request);
                string? encoder = request.VideoCodec switch
                {
                    "h265" => "h265",
                    "auto" => GetPeerSettings(link.Id).VideoEncoder,
                    _ => "h264"
                };
                var videoOptions = request.VideoOptions(link.Hello, encoder);
                streamId = await ShareVideoAsync(link.Id, resolved.Target, resolved.Target.Name, resolved.AppKey,
                    allowInput: null, videoOptions: videoOptions).ConfigureAwait(false);
            }

            byte[] ok = JsonSerializer.SerializeToUtf8Bytes(new LinkPullResponse(true, streamId, null));
            await link.SendAsync(LinkMessage.PullResponse, requestId, ok).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                byte[] fail = JsonSerializer.SerializeToUtf8Bytes(new LinkPullResponse(false, Guid.Empty, Label(ex.Message, "Failed")));
                await link.SendAsync(LinkMessage.PullResponse, requestId, fail).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private void ClearCatalogCacheLocked(LinkConnection link)
    {
        foreach (var key in _catalogCache.Keys.Where(key => key.Link == link).ToArray())
            _catalogCache.Remove(key);
        foreach (var key in _inFlightCatalogs.Keys.Where(key => key.Link == link).ToArray())
            _inFlightCatalogs.Remove(key);
    }

    private static string? CleanInstanceId(string? id) =>
        id is { Length: > 0 and <= 96 } && !id.Any(char.IsControl) ? id : null;

    private static bool MatchesApp(LinkMedia media, string key, string? instanceId) =>
        string.Equals(media.AppKey, key, StringComparison.OrdinalIgnoreCase)
        && (instanceId == null || media.InstanceId == instanceId);

    private static bool InScope(LinkRemoteApp app, LinkCatalogScope scope) => scope switch
    {
        LinkCatalogScope.Audio => !app.IsMonitor && app.ProcessId > 0 && !app.IsSystemAudio && (app.HasAudioSession ?? app.IsPlaying),
        LinkCatalogScope.Windows => app.IsMonitor
            ? app.Key.StartsWith(LinkRemoteApp.MonitorKeyPrefix, StringComparison.Ordinal) && CleanInstanceId(app.InstanceId) != null
              && app.Display is { Number: > 0, Width: > 0, Height: > 0 } && app.ProcessId == 0 && app.WindowHandle == 0
            : app.HasWindow && app.WindowHandle != 0,
        _ => !app.IsMonitor
    };

    private async Task<LinkRemoteApp[]> ReadLocalCatalogAsync(LinkCatalogScope scope, bool includeIcons)
    {
        if (ScopedCatalogProvider != null)
            return await ScopedCatalogProvider(scope, includeIcons, CancellationToken.None).ConfigureAwait(false);
        if (_localCatalogProvider != null)
            return await _localCatalogProvider(scope == LinkCatalogScope.Audio, CancellationToken.None).ConfigureAwait(false);
        return scope == LinkCatalogScope.Audio ? [] : GetWindows().Select(window =>
        {
            string key = window.ProcessName ?? $"pid:{window.ProcessId}";
            return new LinkRemoteApp(key, window.Name, key, window.ProcessId, false, true,
                window.Handle, window.Name, null) { HasAudioSession = false, InstanceId = window.InstanceId };
        }).ToArray();
    }

    private async Task<LinkRemoteApp> ResolveAudioTargetAsync(LinkPullRequest request)
    {
        LinkRemoteApp[] catalog = await ReadLocalCatalogAsync(LinkCatalogScope.Audio, includeIcons: true).ConfigureAwait(false);
        bool KeyMatches(string key) => string.IsNullOrWhiteSpace(request.AppKey)
            || string.Equals(key, request.AppKey, StringComparison.OrdinalIgnoreCase);

        if (request.ProcessId is int pid && pid > 0 && pid != Environment.ProcessId)
        {
            var process = LinkWindowCapture.ReadProcess(pid);
            if (process.Name != null && KeyMatches(process.Name))
            {
                string instance = LinkRemoteApp.AudioInstanceId(pid, process.StartTicks);
                if (request.InstanceId != null && request.InstanceId != instance)
                    throw new InvalidOperationException("The selected audio process changed. Refresh the list.");
                var match = catalog.FirstOrDefault(app => app.ProcessId == pid && KeyMatches(app.Key));
                if (match != null) return match with { InstanceId = instance };
                if (ScopedCatalogProvider == null && !request.DestinationOnly)
                    return new LinkRemoteApp(process.Name, process.Name, process.Name, pid, false, false, 0, null, null)
                    { InstanceId = instance };
            }
            throw new InvalidOperationException("The selected audio process is no longer available. Refresh the list.");
        }

        if (request.InstanceId == null && !string.IsNullOrWhiteSpace(request.AppKey))
        {
            var group = catalog.FirstOrDefault(app => app.ProcessId > 0 && app.ProcessId != Environment.ProcessId && KeyMatches(app.Key));
            if (group != null)
            {
                var current = LinkWindowCapture.ReadProcess(group.ProcessId);
                if (current.Name != null && KeyMatches(current.Name)
                    && (group.InstanceId == null || group.InstanceId == LinkRemoteApp.AudioInstanceId(group.ProcessId, current.StartTicks)))
                    return group;
            }
        }
        throw new InvalidOperationException("The requested application is no longer running.");
    }

    private (LinkCaptureTarget Target, string AppKey) ResolveVideoTarget(LinkPullRequest request)
    {
        if (request.AppKey?.StartsWith(LinkRemoteApp.MonitorKeyPrefix, StringComparison.Ordinal) == true)
        {
            if (!LocalCapabilities.HasFlag(LinkCapabilities.MonitorCapture) || request.WindowHandle != null || request.ProcessId != null
                || CleanInstanceId(request.InstanceId) == null)
                throw new InvalidOperationException("Select a display from the refreshed list.");
            var monitor = LinkMonitors.GetActive().FirstOrDefault(m => m.Key == request.AppKey && m.InstanceId == request.InstanceId)
                ?? throw new InvalidOperationException("The selected display is no longer available. Refresh the list.");
            return (monitor, monitor.Key);
        }
        string Key(LinkWindow window) => window.ProcessName ?? $"pid:{window.ProcessId}";
        bool KeyMatches(LinkWindow window) => string.IsNullOrWhiteSpace(request.AppKey)
            || string.Equals(Key(window), request.AppKey, StringComparison.OrdinalIgnoreCase);

        LinkWindow? window;
        if (request.WindowHandle is long handle && handle != 0)
        {
            window = LinkWindowCapture.ReadWindow((nint)handle);
            if (window == null || request.ProcessId is > 0 && window.ProcessId != request.ProcessId
                || request.InstanceId != null && request.InstanceId != window.InstanceId || !KeyMatches(window))
                throw new InvalidOperationException("The selected window is no longer available. Refresh the list.");
        }
        else
        {
            // Group-based shortcuts from older peers can still resolve a representative window.
            if (request.InstanceId != null || request.ProcessId is not > 0 && string.IsNullOrWhiteSpace(request.AppKey))
                throw new InvalidOperationException("Select a window from the refreshed list.");
            window = GetWindows().FirstOrDefault(candidate =>
                (request.ProcessId is not > 0 || candidate.ProcessId == request.ProcessId) && KeyMatches(candidate)
                && CanShareWindow?.Invoke(candidate) != false);
        }
        if (window == null || CanShareWindow?.Invoke(window) == false)
            throw new InvalidOperationException("The selected application has no shareable window.");
        return (window, Key(window));
    }

    private bool OpenIncoming(LinkConnection link, Guid id, StreamOffer offer)
    {
        byte[]? iconPng = DecodeIconPng(offer.IconPngBase64);
        string? instanceId = CleanInstanceId(offer.InstanceId);
        string effectiveKey = !string.IsNullOrWhiteSpace(offer.AppKey) && offer.AppKey.Length <= 128 && !offer.AppKey.Any(char.IsControl)
            ? offer.AppKey.Trim()
            : offer.Kind == LinkMediaKind.SystemAudio ? LinkRemoteApp.SystemAudioKey : Label(offer.Label, "Windows");
        lock (_gate)
        {
            if (_connections.GetValueOrDefault(link.Id) != link || _streams.ContainsKey(id)
                || !CanOpen(link, offer.Kind, incoming: true, offer.ReplacesAudio)
                || (offer.NativeResolution && (offer.Kind != LinkMediaKind.Window || !link.Hello.SupportsNativeVideo
                    || !LinkVideoOptions.ValidFrameSize(offer.VideoWidth, offer.VideoHeight)))
                || (offer.Hevc && (offer.Kind != LinkMediaKind.Window || !LocalCapabilities.HasFlag(LinkCapabilities.HevcDecode)
                    || !LinkVideoOptions.ValidHevcFrameSize(offer.VideoWidth, offer.VideoHeight)))) return false;
            PruneExpiredHints();
            PendingWindowPlacement? placement = null;
            if (offer.Kind == LinkMediaKind.Window)
            {
                int idx = _pendingPlacements.FindIndex(p => string.Equals(p.PeerId, link.Id, StringComparison.OrdinalIgnoreCase)
                    && (p.InstanceId == null || p.InstanceId == instanceId)
                    && (string.IsNullOrWhiteSpace(p.AppKey) || string.Equals(p.AppKey, effectiveKey, StringComparison.OrdinalIgnoreCase)));
                if (idx >= 0)
                {
                    placement = _pendingPlacements[idx];
                    _pendingPlacements.RemoveAt(idx);
                    iconPng = placement.IconPng ?? iconPng;
                }
            }
            else
            {
                int idx = _pendingAudioHints.FindIndex(h => string.Equals(h.PeerId, link.Id, StringComparison.OrdinalIgnoreCase)
                    && (h.InstanceId == null || h.InstanceId == instanceId)
                    && string.Equals(h.AppKey, effectiveKey, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    iconPng ??= _pendingAudioHints[idx].IconPng;
                    _pendingAudioHints.RemoveAt(idx);
                }
            }

            var info = new LinkMedia(id, link.Id, offer.Kind, offer.Label, true, 1, false, null,
                offer.AllowInput, effectiveKey, null, iconPng, offer.DestinationOnly)
            { InstanceId = instanceId, IsRelayed = offer.Relayed, Hevc = offer.Hevc, NativeResolution = offer.NativeResolution, VideoWidth = offer.VideoWidth, VideoHeight = offer.VideoHeight };
            var track = new Track(link, info) { Announced = true, OpenQueued = true, WindowOwned = offer.WindowOwned };
            if (info.Kind == LinkMediaKind.Window)
            {
                LinkPeerSettings settings = GetPeerSettings(link.Id);
                track.Video = new LinkVideoViewer(
                    info,
                    offer.AllowInput ? body => _ = SendInputAsync(link, id, body) : null,
                    error => _ = Task.Run(() => OnMediaEndedAsync(track, error)),
                    settings.VideoDecoder,
                    placement?.X ?? 100,
                    placement?.Y ?? 80,
                    hevc: info.Hevc, width: offer.Hevc ? offer.VideoWidth : 0, height: offer.Hevc ? offer.VideoHeight : 0,
                    frameLimit: link.VideoFrameLimit);
            }
            else
            {
                bool smart = IsSmartVolumeEnabled(link.Id, effectiveKey);
                var playback = new LinkAudioPlayback(
                    smart,
                    () =>
                    {
                        SyncIncomingAudioState(id);
                        if (track.Audio?.State.State == CaptureState.Receiving) _ = ReplyAudioReadyAsync(track);
                    },
                    error => _ = Task.Run(() => OnMediaEndedAsync(track, error)),
                    (pcm, endpoint, token) => CreateIncomingPlayer(track, pcm, endpoint, token),
                    lifetime: track.Stop.Token)
                { SourceAlreadyPlaying = () => !track.Info.IsRelayed && GetIncomingMedia(id) is { DestinationOnly: false } };
                track.Audio = playback;
                track.AudioSource = new WindowsLinkAudioSource(id, "winlink:" + id.ToString("N"),
                    link.Id, effectiveKey, info.Name, iconPng, playback) { InstanceId = instanceId, IsRelayed = offer.Relayed };
            }
            _streams.Add(id, track);
            track.Audio?.Start();
        }
        Notify();
        return true;
    }

    private void PruneExpiredHints()
    {
        _pendingPlacements.RemoveAll(p => Stopwatch.GetElapsedTime(p.At) > TimeSpan.FromSeconds(15));
        _pendingAudioHints.RemoveAll(h => Stopwatch.GetElapsedTime(h.At) > TimeSpan.FromSeconds(15));
    }

    private static byte[]? DecodeIconPng(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > MaxCatalogIconBase64Chars) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            return bytes.Length is > 0 and <= MaxIconBytes ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    private async Task OnMediaEndedAsync(Track track, Exception? error)
    {
        if (error != null) Report(error);
        if (track.Audio != null)
            await ReplyAudioReadyAsync(track, error?.Message ?? "Audio playback stopped.").ConfigureAwait(false);
        await RetireAsync(track, announce: true).ConfigureAwait(false);
    }

    private async Task ReplyAudioReadyAsync(Track track, string? failure = null)
    {
        if ((track.Connection.Hello.Capabilities & LinkCapabilities.AudioReady) == 0) return;
        lock (_gate)
        {
            if (track.Closing || track.PlaybackReplySent) return;
            track.PlaybackReplySent = true;
        }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            byte[] body = failure == null ? [] : Encoding.UTF8.GetBytes(Label(failure, "Audio playback could not start."));
            await track.Connection.SendAsync(LinkMessage.AudioReady, track.Info.Id, body, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }

    private static async Task SendInputAsync(LinkConnection link, Guid id, byte[] body)
    {
        try { await link.SendAsync(LinkMessage.Input, id, body).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }

    private async Task RetireAsync(Track track, bool announce)
    {
        bool retire;
        Track? companion;
        lock (_gate)
        {
            retire = !track.Closing;
            track.Closing = true;
            companion = track.Companion;
            track.Companion = null;
            if (retire && !track.Info.Incoming && track.Info.Kind != LinkMediaKind.Window)
                foreach (var video in _streams.Values)
                    if (video.Companion == track) video.Companion = null;
            if (retire && !track.Info.Incoming && track.Info.Kind == LinkMediaKind.SystemAudio)
                RefreshWindowAudioCoverageLocked(track.Connection);
        }
        if (!retire) { await track.Finished.Task.ConfigureAwait(false); return; }
        try
        {
            if (track.RelaySource != null) RelayStopped(track);
            track.ExternalOutput?.PeerClosed();
            track.Stop.Cancel();
            try { ReleaseLocalSilence(track); }
            catch (Exception ex) { Report(ex); }
            track.DetachInput()?.Dispose();
            if (announce && track.OpenQueued)
                await SendCloseAsync(track.Connection, track.Info.Id).ConfigureAwait(false);
            try { await track.Work.ConfigureAwait(false); }
            catch (Exception) { /* The capture task is observed by its owner. */ }
            if (track.Video != null) await track.Video.DisposeAsync().ConfigureAwait(false);
            if (track.Audio != null) await track.Audio.DisposeAsync().ConfigureAwait(false);
            track.Connection.RetireVideo(track.Info.Id);
        }
        catch (Exception ex) { Report(ex); }
        finally
        {
            lock (_gate) _streams.Remove(track.Info.Id);
            track.Stop.Dispose();
            track.Finished.TrySetResult();
            Notify();
            if (companion != null) _ = ReleaseWindowAudioAsync(companion);
        }
    }

    private static async Task SendCloseAsync(LinkConnection connection, Guid id)
    {
        if (!connection.IsOpen) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await connection.SendAsync(LinkMessage.Close, id, [], deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }
}
