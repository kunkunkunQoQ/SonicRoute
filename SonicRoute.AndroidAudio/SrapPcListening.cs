using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

// A paired, authenticated attachment can select PC audio. Only its current request opens the shared PCM slot.
internal sealed class SrapPcListening(AndroidPhoneStore store, SrapPhoneTransport transport,
    Func<Stream, CancellationToken, Task>? captureSystemAudio = null, bool? systemCaptureSupported = null) : IAsyncDisposable
{
    private readonly Func<Stream, CancellationToken, Task> captureAudio = captureSystemAudio ?? CaptureSystemAudio;
    private readonly bool canCapture = systemCaptureSupported ?? Supported;
    private readonly object gate = new();
    private readonly Dictionary<SrapSession, Peer> peers = [];
    private readonly HashSet<Task> retiring = [];
    private bool closed;
    internal static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    internal bool HasActiveWork { get { lock (gate) return peers.Values.Any(p => p.HasActiveWork); } }

    private static bool AcceptsComputerRequest(SrapSession session) => session.SupportsPcm &&
        session.Capabilities.Entries.TryGetValue("audio.play", out var play) && play.Limits?.GetValueOrDefault("pcListenInitiate") == "1";

    internal bool CanStartOnPhone(AndroidDevice device) => canCapture && transport.Session(device) is { } session && AcceptsComputerRequest(session);

    internal string? StoppableRequest(AndroidDevice device)
    {
        lock (gate) return transport.Session(device) is { } session && peers.TryGetValue(session, out var peer) ? peer.StoppableRequest : null;
    }

    internal Task StartOnPhoneAsync(AndroidDevice device, CancellationToken token)
    {
        lock (gate)
        {
            if (!CanStartOnPhone(device) || transport.Session(device) is not { } session || !peers.TryGetValue(session, out var peer))
                throw new SrapException("unsupported");
            return peer.StartFromComputerAsync(token);
        }
    }

    internal Task StopOnPhoneAsync(AndroidDevice device, string request)
    {
        lock (gate)
        {
            if (transport.Session(device) is not { } session || !peers.TryGetValue(session, out var peer)) throw new SrapException("stale_session");
            return peer.StopFromComputerAsync(request);
        }
    }

    internal void Reconcile()
    {
        lock (gate)
        {
            if (closed) return;
            var current = new HashSet<SrapSession>();
            foreach (var profile in store.Profiles)
            {
                if (transport.Resolve(profile) is not { } device || transport.Session(device) is not { } session) continue;
                current.Add(session);
                if (!peers.TryGetValue(session, out var peer)) peers.Add(session, peer = new(session, device, transport, captureAudio));
                peer.Update(canCapture && session.SupportsPcm ? "available" : "unsupported", ReadRequest(session.Capabilities));
            }
            foreach (var session in peers.Keys.Where(s => !current.Contains(s)).ToArray())
            {
                peers.Remove(session, out var old);
                old!.Stop();
                retiring.Add(old.Completion);
            }
            retiring.RemoveWhere(t => t.IsCompleted);
        }
    }

    internal static string? ReadRequest(SrapCapabilitySnapshot caps)
    {
        if (!caps.Entries.TryGetValue("audio.play", out var play) ||
            play.Limits?.GetValueOrDefault("pcListenRequest") is not { Length: 32 } request) return null;
        return request.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? request : null;
    }

    private static Task CaptureSystemAudio(Stream destination, CancellationToken token) =>
        PcProcessLoopbackCapture.RunMtaCaptureLoopAsync(Environment.ProcessId, destination, token,
            onProcessExited: null, excludeProcessTree: true);

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (gate)
        {
            closed = true;
            foreach (var peer in peers.Values) { peer.Stop(); retiring.Add(peer.Completion); }
            peers.Clear();
            tasks = retiring.ToArray();
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private sealed class Peer
    {
        private readonly SrapSession session;
        private readonly AndroidDevice device;
        private readonly SrapPhoneTransport transport;
        private readonly Func<Stream, CancellationToken, Task> captureAudio;
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationToken token;
        private readonly object gate = new();
        private readonly Channel<bool> changes = Channel.CreateBounded<bool>(1);
        private string? offer, request;
        private string? activeRequest;
        private readonly HashSet<Guid> used = [];
        private long offerVersion, sentVersion;
        private TaskCompletionSource<bool> offerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LocalRequest? local;
        private sealed class LocalRequest
        {
            internal string Id { get; } = Guid.NewGuid().ToString("N");
            internal TaskCompletionSource Advertised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Approved { get; set; }
        }
        private CancellationTokenSource? active;
        private bool stopped;
        internal Task Completion { get; }
        internal bool HasActiveWork { get { lock (gate) return active != null; } }
        internal string? StoppableRequest { get { lock (gate) return local?.Id ?? activeRequest; } }
        internal Peer(SrapSession session, AndroidDevice device, SrapPhoneTransport transport, Func<Stream, CancellationToken, Task> captureAudio)
        {
            this.session = session;
            this.device = device;
            this.transport = transport;
            this.captureAudio = captureAudio;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            token = lifetime.Token;
            Completion = Task.Run(RunAsync);
        }

        // Request IDs are single-use for this attachment, including withdrawn or never eligible IDs.
        private void Retire(string? id) { if (id != null) used.Add(Guid.ParseExact(id, "N")); }

        internal void Update(string nextOffer, string? nextRequest)
        {
            lock (gate)
            {
                if (stopped) return;
                bool localUnsupported = local != null && !AcceptsComputerRequest(session);
                if (!localUnsupported && nextOffer == offer && nextRequest == request) return;
                if (nextOffer != offer)
                {
                    offerVersion++;
                    offerReady.TrySetResult(false);
                    offerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                if (request != nextRequest) Retire(request);
                if (nextOffer != "available" || sentVersion != offerVersion) Retire(nextRequest);
                if (local is { } initiating)
                {
                    if (nextRequest != initiating.Id) Retire(nextRequest);
                    if (localUnsupported || request == initiating.Id && nextRequest != initiating.Id)
                    {
                        Retire(initiating.Id);
                        local = null;
                        initiating.Advertised.TrySetCanceled();
                    }
                    else if (nextRequest == initiating.Id) initiating.Advertised.TrySetResult();
                }
                offer = nextOffer;
                request = nextRequest;
                active?.Cancel();
                changes.Writer.TryWrite(true);
            }
        }

        internal async Task StartFromComputerAsync(CancellationToken cancel)
        {
            var attempt = new LocalRequest();
            Task<bool> offerAcknowledged;
            lock (gate)
            {
                if (stopped || transport.Session(device) != session) throw new SrapException("stale_session");
                if (offer != "available" || !AcceptsComputerRequest(session)) throw new SrapException("unsupported");
                if (local != null || active != null || request != null && !used.Contains(Guid.ParseExact(request, "N"))) throw new SrapException("busy");
                local = attempt;
                if (offerReady.Task.IsCompletedSuccessfully && !offerReady.Task.Result)
                {
                    offerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    changes.Writer.TryWrite(true);
                }
                offerAcknowledged = offerReady.Task;
            }
            using var effective = CancellationTokenSource.CreateLinkedTokenSource(cancel, token);
            bool requested = false;
            try
            {
                if (!await offerAcknowledged.WaitAsync(effective.Token).ConfigureAwait(false)) throw new SrapException("unavailable");
                lock (gate)
                {
                    if (stopped || local != attempt || !AcceptsComputerRequest(session) || transport.Session(device) != session)
                        throw new SrapException("stale_session");
                }
                requested = true;
                var reply = await session.RequestAsync("audio.listen.request", "audio.play", new() { ["request"] = attempt.Id }, effective.Token)
                    .ConfigureAwait(false);
                if (SrapClient.Text(reply, "request") != attempt.Id || SrapClient.Text(reply, "state") != "requested")
                    throw new SrapException("invalid_argument");
                await attempt.Advertised.Task.WaitAsync(TimeSpan.FromSeconds(8), effective.Token).ConfigureAwait(false);
                lock (gate)
                {
                    effective.Token.ThrowIfCancellationRequested();
                    if (stopped || local != attempt || request != attempt.Id || ReadRequest(session.Capabilities) != attempt.Id ||
                        transport.Session(device) != session || !AcceptsComputerRequest(session)) throw new SrapException("stale_session");
                    attempt.Approved = true;
                    changes.Writer.TryWrite(true);
                }
            }
            catch
            {
                bool needsStop;
                lock (gate)
                {
                    needsStop = local == attempt;
                    if (needsStop) { local = null; Retire(attempt.Id); }
                }
                if (requested && needsStop && !token.IsCancellationRequested)
                    await NotifyAsync("audio.listen.status", "stopped", attempt.Id, token).ConfigureAwait(false);
                throw;
            }
        }

        internal Task StopFromComputerAsync(string shown)
        {
            lock (gate)
            {
                if (stopped || StoppableRequest != shown) throw new SrapException("stale_session");
                Retire(shown);
                local?.Advertised.TrySetCanceled();
                local = null;
                if (active != null)
                {
                    active.Cancel();
                    return Task.CompletedTask;
                }
                return NotifyAsync("audio.listen.status", "stopped", shown, token);
            }
        }

        internal void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                local?.Advertised.TrySetCanceled();
                lifetime.Cancel();
                changes.Writer.TryComplete();
            }
        }

        private async Task RunAsync()
        {
            long announcedVersion = 0;
            try
            {
                await foreach (var _ in changes.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    string? state;
                    long version;
                    lock (gate) { state = offer; version = offerVersion; }
                    if (state == null) continue;
                    if (announcedVersion != version)
                    {
                        bool accepted = await NotifyAsync("audio.listen.offer", state, null, token, () =>
                        {
                            lock (gate) { if (offerVersion == version) sentVersion = version; }
                        }).ConfigureAwait(false);
                        lock (gate)
                        {
                            if (stopped || offerVersion != version) continue;
                            if (!accepted) { sentVersion = 0; Retire(request); }
                            else if (state == "available" && offerReady.Task.IsCompletedSuccessfully && !offerReady.Task.Result)
                                offerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            offerReady.TrySetResult(accepted && state == "available");
                            if (!accepted) continue;
                            announcedVersion = version;
                        }
                    }
                    string id;
                    CancellationTokenSource capture;
                    lock (gate)
                    {
                        if (stopped || offerVersion != version || offer != "available" || request is not { } current ||
                            local != null && (!local.Approved || local.Id != current) || !CanPlay(current) ||
                            !used.Add(Guid.ParseExact(current, "N"))) continue;
                        id = current;
                        activeRequest = current;
                        capture = active = CancellationTokenSource.CreateLinkedTokenSource(token);
                    }
                    string result;
                    try
                    {
                        await PlayAsync(id, capture.Token).ConfigureAwait(false);
                        result = "stopped";
                    }
                    catch (Exception ex) { result = capture.IsCancellationRequested || ex is SrapException { Code: "canceled" } ? "stopped" : "failed"; }
                    finally
                    {
                        lock (gate)
                        {
                            active = null;
                            activeRequest = null;
                            if (local?.Id == id) local = null;
                            capture.Dispose();
                        }
                    }
                    if (!token.IsCancellationRequested) await NotifyAsync("audio.listen.status", result, id, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                lock (gate) { stopped = true; lifetime.Dispose(); }
            }
        }

        private bool CanPlay(string id) => transport.Session(device) == session && session.SupportsPcm && ReadRequest(session.Capabilities) == id;

        private async Task PlayAsync(string id, CancellationToken token)
        {
            await using var output = await SrapPcmOutput.OpenAsync(session, device, AndroidOutputSink.Current.Key, token,
                listenRequest: id).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!CanPlay(id)) throw new SrapException("stale_session");
            using var capturing = CancellationTokenSource.CreateLinkedTokenSource(token);
            var capture = captureAudio(output.Stream, capturing.Token);
            if (await Task.WhenAny(capture, output.Completion).ConfigureAwait(false) == output.Completion)
            {
                capturing.Cancel();
                try { await capture.ConfigureAwait(false); } catch (OperationCanceledException) when (capturing.IsCancellationRequested) { }
                throw await output.Completion.ConfigureAwait(false);
            }
            await capture.ConfigureAwait(false);
        }

        private async Task<bool> NotifyAsync(string operation, string state, string? id, CancellationToken token, Action? beforeWrite = null)
        {
            var parameters = new System.Text.Json.Nodes.JsonObject { ["state"] = state };
            if (id != null) parameters["request"] = id;
            try { await session.RequestAsync(operation, "audio.play", parameters, token, beforeWrite: beforeWrite).ConfigureAwait(false); return true; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or ChannelClosedException)
            { return false; } // Older phones and retired attachments can decline these optional requests.
        }
    }
}
