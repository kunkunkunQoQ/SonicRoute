using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

internal sealed record PcListeningPrompt(string ProfileId, string Fingerprint, string Attachment, string Request);

// One event-driven coordinator per authenticated attachment. Only a current, authorized phone
// request opens WASAPI and an owned AudioTrack. Ordinary PC-initiated routes share the PCM slot.
internal sealed class SrapPcListening(AndroidPhoneStore store, SrapPhoneTransport transport,
    Func<Stream, CancellationToken, Task>? captureSystemAudio = null) : IAsyncDisposable
{
    private readonly Func<Stream, CancellationToken, Task> captureAudio = captureSystemAudio ?? CaptureSystemAudio;
    private readonly object gate = new();
    private readonly Dictionary<SrapSession, Peer> peers = [];
    private readonly HashSet<Task> retiring = [];
    private readonly HashSet<(string Id, string Pin)> failedRevocations = [];
    private bool closed;
    internal event Action<PcListeningPrompt>? PermissionRequested;
    internal static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    internal bool HasActiveWork { get { lock (gate) return peers.Values.Any(p => p.HasActiveWork); } }

    internal bool CanGrant(AndroidPhoneProfile profile) => Supported && transport.Resolve(profile) is { } device &&
        transport.Session(device)?.SupportsPcm == true;

    private static bool AcceptsComputerRequest(SrapSession session) => session.SupportsPcm &&
        session.Capabilities.Entries.TryGetValue("audio.play", out var play) && play.Limits?.GetValueOrDefault("pcListenInitiate") == "1";

    internal bool CanStartOnPhone(AndroidDevice device) => Supported && transport.Session(device) is { } session && AcceptsComputerRequest(session);

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

    internal PcListeningPrompt? Pending(string profileId)
    {
        lock (gate) return peers.Values.FirstOrDefault(p => p.ProfileId == profileId)?.Prompt;
    }

    internal bool Answer(PcListeningPrompt shown, bool allow)
    {
        lock (gate)
        {
            var peer = peers.Values.FirstOrDefault(p => p.Prompt == shown);
            if (peer == null) return false;
            if (allow) SetPermission(shown.ProfileId, shown.Fingerprint, true);
            else peer.Decline(shown.Request);
            return true;
        }
    }

    internal bool IsAllowed(AndroidPhoneProfile profile)
    {
        lock (gate) return profile.AllowsPcListening && !failedRevocations.Contains((profile.Id, profile.AppPublicKeySha256!));
    }

    internal void SetPermission(string id, string fingerprint, bool allow)
    {
        var profile = store.Profiles.FirstOrDefault(p => p.Id == id && p.AppPublicKeySha256 == fingerprint);
        if (profile == null || allow && !CanGrant(profile)) throw new SrapException("stale_session");
        try { store.SetPcListening(id, fingerprint, allow); }
        catch (Exception) when (!allow)
        {
            // A disk error must not keep a live capture running after the user turns sharing off.
            lock (gate) failedRevocations.Add((id, fingerprint));
            Reconcile();
            throw;
        }
        bool changed;
        lock (gate) changed = failedRevocations.Remove((id, fingerprint));
        if (changed) Reconcile();
    }

    internal void Reconcile()
    {
        List<PcListeningPrompt>? requests = null;
        lock (gate)
        {
            if (closed) return;
            var current = new HashSet<SrapSession>();
            var profiles = store.Profiles;
            if (failedRevocations.Count != 0) failedRevocations.RemoveWhere(f => !profiles.Any(p =>
                p.Id == f.Id && p.PcListenFingerprint == f.Pin && p.AllowsPcListening));
            foreach (var profile in profiles)
            {
                if (transport.Resolve(profile) is not { } device || transport.Session(device) is not { } session) continue;
                current.Add(session);
                if (!peers.TryGetValue(session, out var peer)) peers.Add(session, peer = new(profile.Id, session, device, captureAudio));
                string state = !Supported || !session.SupportsPcm ? "unsupported" :
                    IsAllowed(profile) ? "available" : "needs_authorization";
                if (peer.Update(state, ReadRequest(session.Capabilities)) is { } prompt) (requests ??= []).Add(prompt);
            }
            foreach (var session in peers.Keys.Where(s => !current.Contains(s)).ToArray())
            {
                peers.Remove(session, out var old);
                old!.Stop();
                retiring.Add(old.Completion);
            }
            retiring.RemoveWhere(t => t.IsCompleted);
        }
        if (requests != null) foreach (var prompt in requests) PermissionRequested?.Invoke(prompt);
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
        private readonly Func<Stream, CancellationToken, Task> captureAudio;
        private readonly CancellationTokenSource lifetime;
        private readonly object gate = new();
        private readonly Channel<bool> changes = Channel.CreateBounded<bool>(1);
        private string? offer, request;
        private string? declined;
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
        internal string? StoppableRequest { get { lock (gate) return local?.Id ?? (active != null ? request : null); } }
        internal string ProfileId { get; }
        internal PcListeningPrompt? Prompt
        {
            get
            {
                lock (gate) return !stopped && !session.Token.IsCancellationRequested && offer == "needs_authorization" &&
                    request != null && local == null && declined != request && ReadRequest(session.Capabilities) == request
                    ? new(ProfileId, device.Serial, device.ConnectionTarget!, request) : null;
            }
        }

        internal Peer(string profileId, SrapSession session, AndroidDevice device, Func<Stream, CancellationToken, Task> captureAudio)
        {
            ProfileId = profileId;
            this.session = session;
            this.device = device;
            this.captureAudio = captureAudio;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            Completion = Task.Run(RunAsync);
        }

        internal PcListeningPrompt? Update(string nextOffer, string? nextRequest)
        {
            lock (gate)
            {
                if (stopped) return null;
                bool localUnsupported = local != null && !AcceptsComputerRequest(session);
                if (!localUnsupported && nextOffer == offer && nextRequest == request) return null;
                if (offer == "available" && request == nextRequest && nextOffer != "available") declined = nextRequest;
                if (local is { } initiating)
                {
                    if (localUnsupported)
                    {
                        declined = initiating.Id;
                        local = null;
                        initiating.Advertised.TrySetCanceled();
                    }
                    else if (nextRequest == initiating.Id) initiating.Advertised.TrySetResult();
                    else if (request == initiating.Id)
                    {
                        local = null;
                        initiating.Advertised.TrySetCanceled();
                    }
                }
                offer = nextOffer;
                request = nextRequest;
                active?.Cancel();
                changes.Writer.TryWrite(true);
                return Prompt;
            }
        }

        internal void Decline(string id)
        {
            lock (gate)
            {
                declined = id;
                changes.Writer.TryWrite(true);
            }
        }

        internal async Task StartFromComputerAsync(CancellationToken cancel)
        {
            var attempt = new LocalRequest();
            lock (gate)
            {
                if (stopped || session.Token.IsCancellationRequested) throw new SrapException("stale_session");
                if (!AcceptsComputerRequest(session)) throw new SrapException("unsupported");
                if (local != null || request != null || active != null) throw new SrapException("busy");
                local = attempt;
            }
            using var effective = CancellationTokenSource.CreateLinkedTokenSource(cancel, session.Token);
            try
            {
                var reply = await session.RequestAsync("audio.listen.request", "audio.play", new() { ["request"] = attempt.Id }, effective.Token)
                    .ConfigureAwait(false);
                if (SrapClient.Text(reply, "request") != attempt.Id || SrapClient.Text(reply, "state") != "requested")
                    throw new SrapException("invalid_argument");
                await attempt.Advertised.Task.WaitAsync(TimeSpan.FromSeconds(8), effective.Token).ConfigureAwait(false);
                lock (gate)
                {
                    effective.Token.ThrowIfCancellationRequested();
                    if (stopped || local != attempt || request != attempt.Id || ReadRequest(session.Capabilities) != attempt.Id ||
                        !AcceptsComputerRequest(session)) throw new SrapException("stale_session");
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
                    if (needsStop) { local = null; declined = attempt.Id; }
                }
                if (needsStop && !session.Token.IsCancellationRequested)
                    await NotifyAsync("audio.listen.status", "stopped", attempt.Id, session.Token).ConfigureAwait(false);
                throw;
            }
        }

        internal Task StopFromComputerAsync(string shown)
        {
            lock (gate)
            {
                if (stopped || StoppableRequest != shown) throw new SrapException("stale_session");
                declined = shown;
                local?.Advertised.TrySetCanceled();
                local = null;
                if (active != null)
                {
                    active.Cancel();
                    return Task.CompletedTask;
                }
                return NotifyAsync("audio.listen.status", "stopped", shown, session.Token);
            }
        }

        internal void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                lifetime.Cancel();
                changes.Writer.TryComplete();
            }
        }

        private async Task RunAsync()
        {
            var token = lifetime.Token;
            string? announced = null;
            // Handles a withdrawn or replayed request once while bounding per-peer state.
            var recent = new Queue<string>(32);
            try
            {
                await foreach (var _ in changes.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    string? state, id;
                    lock (gate) { state = offer; id = request; }
                    if (state == null) continue;
                    if (announced != state)
                    {
                        await NotifyAsync("audio.listen.offer", state, null, token).ConfigureAwait(false);
                        announced = state;
                    }
                    if (id == null || recent.Contains(id)) continue;
                    CancellationTokenSource? capture;
                    lock (gate)
                    {
                        if (stopped || state != offer || id != request) continue;
                        bool oneShot = local is { Approved: true } agreed && agreed.Id == id;
                        if (local != null && !oneShot ||
                            state == "needs_authorization" && declined != id && !oneShot) continue;
                        if (recent.Count == 32) recent.Dequeue();
                        recent.Enqueue(id);
                        capture = active = (oneShot && state != "unsupported" || state == "available") && declined != id
                            ? CancellationTokenSource.CreateLinkedTokenSource(token) : null;
                    }
                    string result = "declined";
                    if (capture != null)
                    {
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
                                if (local?.Id == id) local = null;
                                capture.Dispose();
                            }
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

        private async Task PlayAsync(string id, CancellationToken token)
        {
            await using var output = await SrapPcmOutput.OpenAsync(session, device, AndroidOutputSink.Current.Key, token,
                listenRequest: id).ConfigureAwait(false);
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

        private async Task NotifyAsync(string operation, string state, string? id, CancellationToken token)
        {
            var parameters = new System.Text.Json.Nodes.JsonObject { ["state"] = state };
            if (id != null) parameters["request"] = id;
            try { await session.RequestAsync(operation, "audio.play", parameters, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or ChannelClosedException)
            { /* Older phones and retired attachments may decline these optional requests. */ }
        }
    }
}
