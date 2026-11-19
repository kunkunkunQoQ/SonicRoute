using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    private const int MaxStreamsPerDirection = 5;
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
        bool DestinationOnly = false);

    private sealed record CatalogRequestPayload(bool IncludeIcons = true, bool AudioOnly = false);

    private sealed class PendingCatalog(LinkConnection connection)
    {
        internal LinkConnection Connection { get; } = connection;
        internal TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? Buffer;
        internal int ExpectedLength;
        internal int ReceivedOffset;
        internal bool Started;
    }

    private sealed record PendingWindowPlacement(string PeerId, string? AppKey, byte[]? IconPng, int X, int Y, long At);
    private sealed record PendingAudioHint(string PeerId, string AppKey, byte[]? IconPng, long At);

    private sealed class Track(LinkConnection connection, LinkMedia info)
    {
        private LinkWindowInput? _input;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkConnection Connection { get; } = connection;
        internal LinkMedia Info { get; set; } = info;
        internal Task Work = Task.CompletedTask;
        internal TaskCompletionSource? RemotePlaybackReady;
        internal IPcProcessSilenceLease? LocalSilence;
        internal bool PlaybackReplySent;
        internal LinkAudioPlayback? Audio;
        internal WindowsLinkAudioSource? AudioSource;
        internal LinkVideoViewer? Video;
        internal LinkWindowInput? Input { get => Volatile.Read(ref _input); set => Volatile.Write(ref _input, value); }
        internal LinkWindowInput? DetachInput() => Interlocked.Exchange(ref _input, null);
        internal int? Process;
        internal nint WindowHandle;
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

    public string? FindOutgoingAudioPeerForProcess(int processId)
    {
        if (processId <= 0) return null;
        lock (_gate)
        {
            return _streams.Values
                .FirstOrDefault(t => !t.Closing && t.Announced && !t.Info.Incoming && t.Info.Kind == LinkMediaKind.AppAudio && t.Process == processId)
                ?.Info.PeerId;
        }
    }

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
                .Where(t => !t.Closing && !t.Info.Incoming && t.Info.Kind == LinkMediaKind.AppAudio)
                .ToArray();
        }
        if (tracks.Length > 0)
            await Task.WhenAll(tracks.Select(t => RetireAsync(t, announce: true))).ConfigureAwait(false);
    }

    public async Task StopPeerAppStreamsAsync(string peerId, string appKey, bool window = false)
    {
        if (string.IsNullOrWhiteSpace(peerId) || string.IsNullOrWhiteSpace(appKey)) return;
        Track[] tracks;
        lock (_gate)
        {
            tracks = _streams.Values
                .Where(t => !t.Closing && t.Info.Incoming && (t.Info.Kind == LinkMediaKind.Window) == window
                    && string.Equals(t.Info.PeerId, peerId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Info.AppKey, appKey, StringComparison.OrdinalIgnoreCase))
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

    public async Task<LinkRemoteApp[]> RequestCatalogAsync(string peerId, bool includeIcons = true,
        CancellationToken token = default, bool audioOnly = false)
    {
        LinkConnection link;
        Guid requestId = Guid.NewGuid();
        var pending = new PendingCatalog(null!);
        lock (_gate)
        {
            link = Connected(peerId, LinkCapabilities.RemoteBrowse);
            pending = new PendingCatalog(link);
            _pendingCatalogs[requestId] = pending;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            byte[] req = JsonSerializer.SerializeToUtf8Bytes(new CatalogRequestPayload(includeIcons, audioOnly));
            await link.SendAsync(LinkMessage.CatalogRequest, requestId, req, deadline.Token).ConfigureAwait(false);
            byte[] payload = await pending.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var apps = JsonSerializer.Deserialize<LinkRemoteApp[]>(payload) ?? [];
            var valid = apps.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Key) && !string.IsNullOrWhiteSpace(a.Name))
                // Older peers return their combined catalog and only identify active audio sessions.
                .Where(a => !audioOnly || (a.ProcessId > 0 && !a.IsSystemAudio && (a.HasAudioSession ?? a.IsPlaying)))
                .Take(MaxCatalogEntries)
                .Select(a => a with
                {
                    Key = a.Key.Trim(),
                    Name = Label(a.Name, a.Key.Trim()),
                    ProcessName = Label(a.ProcessName, a.Key.Trim()),
                    WindowTitle = string.IsNullOrWhiteSpace(a.WindowTitle) ? null : Label(a.WindowTitle, ""),
                    IconPng = a.IconPng is { Length: > 0 and <= MaxIconBytes } ? a.IconPng : null
                })
                .ToArray();
            UpdatePinnedMetadata(peerId, valid);
            return valid;
        }
        finally
        {
            lock (_gate) _pendingCatalogs.Remove(requestId);
        }
    }

    public async Task<Guid> RequestPullAudioAsync(string peerId, LinkRemoteApp app, CancellationToken token = default,
        bool destinationOnly = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        Guid? existingId = null;
        lock (_gate)
        {
            var link = Connected(peerId, app.IsSystemAudio ? LinkCapabilities.SystemAudio : LinkCapabilities.ProcessAudio);
            if (destinationOnly && (app.IsSystemAudio || (link.Hello.Capabilities & LinkCapabilities.DestinationOnly) == 0))
                throw new NotSupportedException("The sending computer cannot move this audio exclusively.");
            var existing = _streams.Values.FirstOrDefault(t => !t.Closing && t.Info.Incoming
                && t.Info.Kind != LinkMediaKind.Window
                && string.Equals(t.Info.PeerId, peerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Info.AppKey, app.Key, StringComparison.OrdinalIgnoreCase)
                && t.Info.DestinationOnly == destinationOnly && t.Audio?.State.State == CaptureState.Receiving);
            if (existing != null) existingId = existing.Info.Id;
        }
        if (existingId.HasValue) return existingId.Value;

        if (app.IconPng is { Length: > 0 and <= MaxIconBytes })
        {
            lock (_gate)
            {
                PruneExpiredHints();
                _pendingAudioHints.Add(new PendingAudioHint(peerId, app.Key, app.IconPng, Stopwatch.GetTimestamp()));
            }
        }
        var request = new LinkPullRequest(
            app.IsSystemAudio ? LinkMediaKind.SystemAudio : LinkMediaKind.AppAudio,
            app.Key,
            app.ProcessId > 0 ? app.ProcessId : null,
            null, DestinationOnly: destinationOnly);
        var response = await SendPullRequestAsync(peerId, request, token).ConfigureAwait(false);
        return response.StreamId;
    }

    public async Task<Guid> RequestPullWindowAsync(string peerId, LinkRemoteApp app, int x = 100, int y = 80, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        Track? existing = null;
        lock (_gate)
        {
            Connected(peerId, LinkCapabilities.WindowCapture);
            existing = _streams.Values.FirstOrDefault(t => !t.Closing && t.Info.Incoming
                && t.Info.Kind == LinkMediaKind.Window
                && string.Equals(t.Info.PeerId, peerId, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(t.Info.AppKey, app.Key, StringComparison.OrdinalIgnoreCase)
                    || (app.ProcessId > 0 && t.Info.ProcessId == app.ProcessId)));
        }
        if (existing != null)
        {
            existing.Video?.Show();
            return existing.Info.Id;
        }

        LinkVideoOptions options = GetPeerSettings(peerId).VideoOptions();
        lock (_gate)
        {
            PruneExpiredHints();
            _pendingPlacements.Add(new PendingWindowPlacement(
                peerId,
                app.Key,
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
            options.MaxHeight);
        var response = await SendPullRequestAsync(peerId, request, token).ConfigureAwait(false);
        return response.StreamId;
    }

    private async Task<LinkPullResponse> SendPullRequestAsync(string peerId, LinkPullRequest request, CancellationToken token)
    {
        LinkConnection link;
        Guid requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<LinkPullResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_connections.TryGetValue(peerId, out link!) || !link.IsOpen)
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

    public async Task<Guid> SendAudioAsync(string peerId, int? processId, string label,
        CancellationToken token = default, string? appKey = null, byte[]? iconPng = null, bool destinationOnly = false)
    {
        if (!LinkAudioCapture.IsSupported) throw new PlatformNotSupportedException("Windows build 20348 or later is required to send audio.");
        if (processId.HasValue && (processId <= 0 || processId == Environment.ProcessId))
            throw new ArgumentException("Select another running application.", nameof(processId));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string effectiveKey = !string.IsNullOrWhiteSpace(appKey) ? appKey.Trim()
            : processId == null ? LinkRemoteApp.SystemAudioKey : ResolveProcessKey(processId.Value, label);
        byte[]? safeIcon = iconPng is { Length: > 0 and <= MaxIconBytes } ? iconPng : null;
        Track track;
        Track? previous;
        LinkConnection link;
        lock (_gate)
        {
            link = Connected(peerId, LinkCapabilities.AudioReceive);
            if (destinationOnly && (processId == null || acquireLocalSilence == null
                || (link.Hello.Capabilities & LinkCapabilities.AudioReady) == 0))
                throw new NotSupportedException("Exclusive playback requires a supported source app and an updated receiver.");
            previous = _streams.Values.FirstOrDefault(t => t.Connection == link && !t.Info.Incoming
                && t.Info.Kind != LinkMediaKind.Window && t.Process == processId && !t.Closing);
            if (previous is { Announced: false }) throw new InvalidOperationException("This audio stream is starting.");
            if (previous != null && previous.Info.DestinationOnly == destinationOnly) return previous.Info.Id;
        }
        if (previous != null) await RetireAsync(previous, announce: true).ConfigureAwait(false);
        lock (_gate)
        {
            if (Connected(peerId, LinkCapabilities.AudioReceive) != link)
                throw new IOException("The Windows Link connection changed while starting audio.");
            if (_streams.Values.Any(t => t.Connection == link && !t.Info.Incoming && !t.Closing
                && t.Info.Kind != LinkMediaKind.Window && t.Process == processId))
                throw new InvalidOperationException("This audio stream is starting.");
            track = Reserve(link, processId == null ? LinkMediaKind.SystemAudio : LinkMediaKind.AppAudio,
                label, allowInput: false, effectiveKey, processId, safeIcon);
            track.Info = track.Info with { DestinationOnly = destinationOnly };
            if ((link.Hello.Capabilities & LinkCapabilities.AudioReady) != 0)
                track.RemotePlaybackReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            track.Process = processId;
            track.Work = Task.Run(() => LinkAudioCapture.CaptureAsync(processId, data =>
            {
                if (track.SendingMedia) link.SendAudio(track.Info.Id, data.Span);
            }, () => started.TrySetResult(), track.Stop.Token));
        }
        await OpenTrackAsync(track, started.Task, token).ConfigureAwait(false);
        _ = WatchCaptureAsync(track);
        return track.Info.Id;
    }

    public async Task<Guid> ShareWindowAsync(string peerId, nint hwnd, string label, bool allowInput,
        CancellationToken token = default, LinkVideoOptions? videoOptions = null, string? appKey = null, byte[]? iconPng = null)
    {
        var window = GetWindows().FirstOrDefault(w => w.Handle == hwnd);
        if (window == null) throw new InvalidOperationException("The selected window is no longer available.");
        string effectiveKey = !string.IsNullOrWhiteSpace(appKey) ? appKey.Trim() : ResolveProcessKey(window.ProcessId, label);
        byte[]? safeIcon = iconPng is { Length: > 0 and <= MaxIconBytes } ? iconPng : null;
        LinkVideoOptions options = (videoOptions ?? LinkVideoOptions.Default).Clamp();
        Track track;
        var first = new TaskCompletionSource<(byte[] Data, long Pts)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var link = Connected(peerId, LinkCapabilities.WindowReceive);
            var existing = _streams.Values.FirstOrDefault(t => t.Connection == link && !t.Info.Incoming
                && t.Info.Kind == LinkMediaKind.Window && t.WindowHandle == hwnd && !t.Closing);
            if (existing != null && existing.Announced) return existing.Info.Id;
            track = Reserve(link, LinkMediaKind.Window, label, allowInput, effectiveKey, window.ProcessId, safeIcon);
            track.WindowHandle = hwnd;
            if (allowInput) track.Input = new LinkWindowInput(hwnd, window.ProcessId);
            track.Work = Task.Run(() => CaptureWindowAsync(track, hwnd, options, first, track.Stop.Token));
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
        string? appKey = null, int? processId = null, byte[]? iconPng = null)
    {
        if (!CanOpen(link, kind, incoming: false))
            throw new InvalidOperationException("Stop an existing stream before opening another.");
        var info = new LinkMedia(Guid.NewGuid(), link.Id, kind, Label(label, "Windows"),
            false, 1f, false, null, allowInput, appKey, processId, iconPng);
        var track = new Track(link, info);
        _streams.Add(info.Id, track);
        return track;
    }

    private bool CanOpen(LinkConnection link, LinkMediaKind kind, bool incoming)
    {
        var direction = _streams.Values.Where(t => t.Connection == link && t.Info.Incoming == incoming).ToArray();
        return direction.Length < MaxStreamsPerDirection
            && (kind != LinkMediaKind.Window || direction.All(t => t.Info.Kind != LinkMediaKind.Window));
    }

    private async Task OpenTrackAsync(Track track, Task ready, CancellationToken token, Action? sendInitial = null)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, track.Stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            if (await Task.WhenAny(ready, track.Work).WaitAsync(deadline.Token).ConfigureAwait(false) == track.Work)
            {
                await track.Work.ConfigureAwait(false);
                throw new IOException("The capture ended before producing a stream.");
            }
            await ready.ConfigureAwait(false);
            var info = track.Info;
            string? iconB64 = EncodeIconBase64(info.IconPng);
            byte[] open = JsonSerializer.SerializeToUtf8Bytes(new StreamOffer(info.Kind, info.Name, info.AllowsInput, info.AppKey, iconB64, info.DestinationOnly));
            if (open.Length > 12 * 1024)
                open = JsonSerializer.SerializeToUtf8Bytes(new StreamOffer(info.Kind, info.Name, info.AllowsInput, info.AppKey, null, info.DestinationOnly));
            track.OpenQueued = true;
            await track.Connection.SendAsync(LinkMessage.Open, info.Id, open, deadline.Token).ConfigureAwait(false);
            sendInitial?.Invoke();
            track.SendingMedia = true;
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
                var silence = acquireLocalSilence!(track.Process!.Value)
                    ?? throw new IOException("The source computer has no available muted output for this app.");
                lock (_gate)
                {
                    if (!track.Closing) track.LocalSilence = silence;
                }
                if (track.LocalSilence != silence) silence.Dispose();
            }
            lock (_gate)
            {
                if (track.Closing) throw new IOException("The remote stream stopped before playback was ready.");
                track.Announced = true;
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
                track.Audio.SetVolume(Math.Clamp((int)Math.Round(volume * 100f), 0, 100));
                track.Audio.SetMute(muted);
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
            case LinkMessage.AudioReady when !track.Info.Incoming && track.RemotePlaybackReady is { } playback:
                if (payload.IsEmpty) playback.TrySetResult();
                else playback.TrySetException(new IOException(Encoding.UTF8.GetString(payload.Span)));
                break;
            case LinkMessage.Close:
                await RetireAsync(track, announce: false).ConfigureAwait(false);
                break;
            case LinkMessage.Audio when track.Info.Incoming:
                track.Audio?.Push(payload.Span);
                break;
            case LinkMessage.Video when track.Info.Incoming:
                track.Video?.Push(payload.Span);
                break;
            case LinkMessage.Volume when track.Info.Incoming && track.Audio != null:
                float volume = BinaryPrimitives.ReadSingleLittleEndian(payload.Span);
                if (!float.IsFinite(volume) || volume is < 0 or > 1 || payload.Span[4] > 1)
                    throw new InvalidDataException("Invalid Windows Link volume.");
                track.Audio.SetPlayback(track.Info.OutputId, volume, payload.Span[4] == 1);
                break;
            case LinkMessage.Input when !track.Info.Incoming:
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
            LinkRemoteApp[] raw = _localCatalogProvider != null
                ? await _localCatalogProvider(request.AudioOnly, CancellationToken.None).ConfigureAwait(false)
                : request.AudioOnly ? [] : GetWindows().Select(w => new LinkRemoteApp(
                    ResolveProcessKey(w.ProcessId, w.Name),
                    w.Name,
                    ResolveProcessKey(w.ProcessId, w.Name),
                    w.ProcessId,
                    false,
                    true,
                    w.Handle,
                    w.Name,
                    null)).ToArray();

            var bounded = raw
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Key) && !string.IsNullOrWhiteSpace(a.Name))
                .Take(MaxCatalogEntries)
                .Select(a => a with
                {
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
            if (request.Kind == LinkMediaKind.SystemAudio)
            {
                streamId = await SendAudioAsync(link.Id, null, _name, appKey: LinkRemoteApp.SystemAudioKey).ConfigureAwait(false);
            }
            else if (request.Kind == LinkMediaKind.AppAudio)
            {
                var resolved = await ResolveAudioTargetAsync(request).ConfigureAwait(false);
                streamId = await SendAudioAsync(link.Id, resolved.ProcessId, resolved.Name,
                    appKey: resolved.Key, iconPng: resolved.IconPng, destinationOnly: request.DestinationOnly).ConfigureAwait(false);
            }
            else
            {
                var resolved = await ResolveWindowTargetAsync(request).ConfigureAwait(false);
                bool allowInput = GetPeerSettings(link.Id).AllowRemoteInput
                    && (LocalCapabilities & LinkCapabilities.WindowInput) != 0;
                var videoOptions = new LinkVideoOptions(request.VideoMaxHeight, request.VideoMaxFps, request.VideoBitRate).Clamp();
                streamId = await ShareWindowAsync(link.Id, resolved.Handle, resolved.Title, allowInput,
                    videoOptions: videoOptions, appKey: resolved.AppKey, iconPng: resolved.IconPng).ConfigureAwait(false);
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

    private async Task<(int ProcessId, string Key, string Name, byte[]? IconPng)> ResolveAudioTargetAsync(LinkPullRequest request)
    {
        LinkRemoteApp[] catalog = _localCatalogProvider != null
            ? await _localCatalogProvider(true, CancellationToken.None).ConfigureAwait(false) : [];
        LinkRemoteApp? match = null;
        if (request.ProcessId is int pid && pid > 0)
            match = catalog.FirstOrDefault(a => a.ProcessId == pid
                && string.Equals(a.Key, request.AppKey, StringComparison.OrdinalIgnoreCase));
        if (match == null && !string.IsNullOrWhiteSpace(request.AppKey))
            match = catalog.FirstOrDefault(a => string.Equals(a.Key, request.AppKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.ProcessName, request.AppKey, StringComparison.OrdinalIgnoreCase));
        if (match is { ProcessId: > 0 } && match.ProcessId != Environment.ProcessId)
            return (match.ProcessId, match.Key, match.Name, match.IconPng);

        if (request.DestinationOnly)
            throw new InvalidOperationException("Refresh the remote app list before moving this app exclusively.");

        if (request.ProcessId is int reqPid && reqPid > 0 && reqPid != Environment.ProcessId)
        {
            try
            {
                using var proc = Process.GetProcessById(reqPid);
                if (!proc.HasExited)
                {
                    string procName = proc.ProcessName;
                    if (string.IsNullOrWhiteSpace(request.AppKey) || string.Equals(procName, request.AppKey, StringComparison.OrdinalIgnoreCase))
                        return (reqPid, procName, procName, null);
                }
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(request.AppKey))
        {
            var procs = Process.GetProcessesByName(request.AppKey);
            try
            {
                var found = procs.FirstOrDefault(p => p.Id > 0 && p.Id != Environment.ProcessId);
                if (found != null) return (found.Id, found.ProcessName, found.ProcessName, null);
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        throw new InvalidOperationException("The requested application is no longer running.");
    }

    private async Task<(nint Handle, string Title, string AppKey, byte[]? IconPng)> ResolveWindowTargetAsync(LinkPullRequest request)
    {
        LinkWindow[] windows = GetWindows();
        LinkRemoteApp[] catalog = _localCatalogProvider != null
            ? await _localCatalogProvider(false, CancellationToken.None).ConfigureAwait(false) : [];
        LinkWindow? window = null;
        if (request.WindowHandle is long hwndVal && hwndVal > 0)
            window = windows.FirstOrDefault(w => w.Handle == (nint)hwndVal);
        if (window == null && request.ProcessId is int pid && pid > 0)
            window = windows.FirstOrDefault(w => w.ProcessId == pid);
        if (window == null && !string.IsNullOrWhiteSpace(request.AppKey))
        {
            var catMatch = catalog.FirstOrDefault(a => string.Equals(a.Key, request.AppKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.ProcessName, request.AppKey, StringComparison.OrdinalIgnoreCase));
            if (catMatch != null)
            {
                if (catMatch.WindowHandle != 0) window = windows.FirstOrDefault(w => w.Handle == (nint)catMatch.WindowHandle);
                window ??= catMatch.ProcessId > 0 ? windows.FirstOrDefault(w => w.ProcessId == catMatch.ProcessId) : null;
            }
            window ??= windows.FirstOrDefault(w => string.Equals(ResolveProcessKey(w.ProcessId, ""), request.AppKey, StringComparison.OrdinalIgnoreCase));
        }
        if (window == null) throw new InvalidOperationException("The requested application has no shareable window.");
        var appInfo = catalog.FirstOrDefault(a => a.ProcessId == window.ProcessId);
        string appKey = appInfo?.Key ?? request.AppKey ?? ResolveProcessKey(window.ProcessId, window.Name);
        return (window.Handle, window.Name, appKey, appInfo?.IconPng);
    }

    private bool OpenIncoming(LinkConnection link, Guid id, StreamOffer offer)
    {
        byte[]? iconPng = DecodeIconPng(offer.IconPngBase64);
        string effectiveKey = !string.IsNullOrWhiteSpace(offer.AppKey) && offer.AppKey.Length <= 128 && !offer.AppKey.Any(char.IsControl)
            ? offer.AppKey.Trim()
            : offer.Kind == LinkMediaKind.SystemAudio ? LinkRemoteApp.SystemAudioKey : Label(offer.Label, "Windows");
        lock (_gate)
        {
            if (_connections.GetValueOrDefault(link.Id) != link || _streams.ContainsKey(id)
                || !CanOpen(link, offer.Kind, incoming: true)) return false;
            PruneExpiredHints();
            PendingWindowPlacement? placement = null;
            if (offer.Kind == LinkMediaKind.Window)
            {
                int idx = _pendingPlacements.FindIndex(p => string.Equals(p.PeerId, link.Id, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(p.AppKey) || string.Equals(p.AppKey, effectiveKey, StringComparison.OrdinalIgnoreCase)));
                if (idx >= 0)
                {
                    placement = _pendingPlacements[idx];
                    _pendingPlacements.RemoveAt(idx);
                    iconPng ??= placement.IconPng;
                }
            }
            else
            {
                int idx = _pendingAudioHints.FindIndex(h => string.Equals(h.PeerId, link.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(h.AppKey, effectiveKey, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    iconPng ??= _pendingAudioHints[idx].IconPng;
                    _pendingAudioHints.RemoveAt(idx);
                }
            }

            var info = new LinkMedia(id, link.Id, offer.Kind, offer.Label, true, 1, false, null,
                offer.AllowInput, effectiveKey, null, iconPng, offer.DestinationOnly);
            var track = new Track(link, info) { Announced = true, OpenQueued = true };
            if (info.Kind == LinkMediaKind.Window)
            {
                LinkPeerSettings settings = GetPeerSettings(link.Id);
                track.Video = new LinkVideoViewer(
                    info.Name,
                    offer.AllowInput ? body => _ = SendInputAsync(link, id, body) : null,
                    error => _ = Task.Run(() => OnMediaEndedAsync(track, error)),
                    settings.VideoDecoder,
                    iconPng,
                    placement?.X ?? 100,
                    placement?.Y ?? 80);
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
                    _audioPlayerFactory,
                    lifetime: track.Stop.Token);
                track.Audio = playback;
                track.AudioSource = new WindowsLinkAudioSource(id, "winlink:" + id.ToString("N"),
                    link.Id, effectiveKey, info.Name, iconPng, playback);
            }
            _streams.Add(id, track);
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
        lock (_gate)
        {
            retire = !track.Closing;
            track.Closing = true;
        }
        if (!retire) { await track.Finished.Task.ConfigureAwait(false); return; }
        try
        {
            track.Stop.Cancel();
            try { track.LocalSilence?.Dispose(); }
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
