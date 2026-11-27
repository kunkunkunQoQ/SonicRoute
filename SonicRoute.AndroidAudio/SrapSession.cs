using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using static SonicRoute.AndroidAudio.SrapClient;

namespace SonicRoute.AndroidAudio;

internal sealed record SrapCapabilitySnapshot(long Revision, IReadOnlyDictionary<string, SrapCapability> Entries)
{
    internal bool Allows(string id) => Entries.TryGetValue(id, out var value) && value.State == "available";
    internal AppCapturePermission? Capture { get; } = AppCapturePermission.Read(Entries);
}

internal sealed partial class SrapSession : IDisposable
{
    private readonly SslStream stream;
    private readonly string sessionId;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken token;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SemaphoreSlim requestSlots = new(16, 16);
    private readonly ConcurrentDictionary<string, Channel<JsonElement>> requests = new();
    private long nextStream = -1;
    private long seq = -1;
    private int disposed;
    private SrapCapabilitySnapshot capabilities;
    internal SrapCapabilitySnapshot Capabilities => Volatile.Read(ref capabilities);
    internal Task<string?> Completion { get; }
    internal event Action? CapabilitiesChanged;
    internal event Action? MediaChanged;
    internal event Action<JsonElement>? StreamEvent;
    internal event Action<string, long?>? RequestSent;
    internal CancellationToken Token => token;
    internal string PeerFingerprint { get; }
    internal SslProtocols Protocol { get; }
    internal TlsCipherSuite CipherSuite { get; }

    internal SrapSession(SslStream stream, string sessionId, (long Revision, IReadOnlyDictionary<string, SrapCapability> Capabilities) initial, int minor = 0)
    {
        this.stream = stream;
        this.sessionId = sessionId;
        this.minor = minor;
        using var peerKey = (stream.RemoteCertificate as X509Certificate2)?.GetECDsaPublicKey() ?? throw new SrapException("identity_conflict");
        PeerFingerprint = Convert.ToHexString(SHA256.HashData(peerKey.ExportSubjectPublicKeyInfo()));
        token = lifetime.Token;
        (Protocol, CipherSuite) = (stream.SslProtocol, stream.NegotiatedCipherSuite);
        capabilities = new(initial.Revision, initial.Capabilities);
        Completion = ReadLoopAsync();
    }

    internal Task<JsonElement> RequestAsync(string op, string? capability, JsonObject parameters, CancellationToken cancel,
        string? routeTarget = null, Action? beforeWrite = null)
        => routeTarget == null && beforeWrite == null &&
            (op is "audio.devices.list" or "catalog.list" or "volume.get" or "display.next" ||
             op == "media.get" && !parameters.ContainsKey("watchId"))
            ? ReadWithFreshCapabilitiesAsync(op, capability, parameters, cancel)
            : RequestOnceAsync(op, capability, parameters, cancel, routeTarget, beforeWrite);

    private async Task<JsonElement> ReadWithFreshCapabilitiesAsync(string op, string? capability, JsonObject parameters, CancellationToken cancel)
    {
        long before = Capabilities.Revision;
        try { return await RequestOnceAsync(op, capability, parameters, cancel, null, null).ConfigureAwait(false); }
        catch (SrapException ex) when (ex is { Code: "stale_session", WireReason: "capability_revision_mismatch" })
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChange() => changed.TrySetResult();
            CapabilitiesChanged += OnChange;
            try
            {
                if (Capabilities.Revision <= before)
                {
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, cancel);
                    try { await changed.Task.WaitAsync(TimeSpan.FromSeconds(2), stop.Token).ConfigureAwait(false); }
                    catch (TimeoutException) { throw new SrapException("stale_session", "capability_revision_mismatch"); }
                }
            }
            finally { CapabilitiesChanged -= OnChange; }
            return await RequestOnceAsync(op, capability, (JsonObject)parameters.DeepClone(), cancel, null, null).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> RequestOnceAsync(string op, string? capability, JsonObject parameters, CancellationToken cancel,
        string? routeTarget, Action? beforeWrite)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel, token);
        if (op != "display.next")
            deadline.CancelAfter(TimeSpan.FromSeconds(op == "file.offer" ? 65 : op == "file.finish" ? 605 : 6));
        if (!await requestSlots.WaitAsync(0, deadline.Token).ConfigureAwait(false)) throw new SrapException("busy");
        string id = RequestId();
        var channel = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
        requests[id] = channel;
        bool sent = false;
        try
        {
            var current = Capabilities;
            if (capability != null && !current.Allows(capability)) throw new SrapException("unsupported");
            await WriteAsync("request", new()
            {
                ["op"] = op, ["capabilityRevision"] = current.Revision, ["params"] = parameters
            }, id, deadline.Token, beforeWrite).ConfigureAwait(false);
            sent = true;
            var body = await channel.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            string? status = Text(body, "status");
            if (status == "error") throw Error(body);
            if (status == "done" && routeTarget == null && body.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                return result;
            if (status != "pending" || routeTarget == null) throw new SrapException("invalid_argument");
            var routed = await channel.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            if (Text(routed, "name") != "audio.routed" || Text(routed, "targetKey") != routeTarget)
                throw new SrapException("invalid_argument");
            if (Text(routed, "error") != null) throw Error(routed);
            if (!routed.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True
                || Text(routed, "actualKey") is not { Length: > 0 } actual || (routeTarget != "current" && actual != routeTarget))
                throw new SrapException("unavailable");
            var after = Capabilities;
            if (!after.Allows("audio.route.owned") || !after.Allows("audio.play")) throw new SrapException("permission_lost");
            return routed;
        }
        catch (OperationCanceledException) when (sent && !token.IsCancellationRequested)
        {
            // Bound cancel independently of a caller that has already withdrawn its request.
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(token);
            cleanup.CancelAfter(TimeSpan.FromSeconds(1));
            try { await WriteAsync("cancel", new() { ["requestId"] = id }, null, cleanup.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { Dispose(); }
            throw;
        }
        finally
        {
            requests.TryRemove(id, out _);
            requestSlots.Release();
        }
    }

    internal Task<JsonElement> ReadVolumeAsync(CancellationToken token) =>
        RequestAsync("volume.get", "volume.device", new() { ["scope"] = "system_media" }, token);
    internal Task<JsonElement> SetVolumeAsync(int percent, CancellationToken token) =>
        RequestAsync("volume.set", "volume.device", new() { ["scope"] = "system_media", ["percent"] = Math.Clamp(percent, 0, 100) }, token);
    internal bool CanSetVolumeIndex => Capabilities.Entries.TryGetValue("volume.device", out var cap)
        && cap.State == "available" && cap.Limits?.GetValueOrDefault("indexWrite") == "1";
    internal Task<JsonElement> SetVolumeIndexAsync(int index, CancellationToken token)
    {
        if (!CanSetVolumeIndex) throw new SrapException("unsupported");
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        return RequestAsync("volume.set", "volume.device", new() { ["scope"] = "system_media", ["streamIndex"] = index }, token);
    }
    internal Task<JsonElement> SetTrackVolumeAsync(uint id, int percent, bool mute, CancellationToken token) =>
        RequestAsync("volume.set", "audio.play", new() { ["scope"] = "owned_track", ["streamId"] = id, ["trackGainPercent"] = percent, ["trackMuted"] = mute }, token);

    internal async Task<uint> StartTrackAsync(string target, CancellationToken token)
    {
        uint id = AllocateStreamId();
        try
        {
            var result = await RequestAsync("audio.play.start", "audio.play", new() { ["streamId"] = id, ["targetKey"] = target }, token).ConfigureAwait(false);
            if (!result.TryGetProperty("streamId", out var echoed) || !echoed.TryGetInt64(out long value) || value != id)
                throw new SrapException("invalid_argument");
            return (uint)id;
        }
        catch
        {
            if (!Token.IsCancellationRequested)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await StopTrackAsync((uint)id, stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ChannelClosedException or ObjectDisposedException) { Dispose(); }
            }
            throw;
        }
    }

    internal Task<JsonElement> RouteTrackAsync(uint id, string target, CancellationToken token) =>
        RequestAsync("audio.route.set", "audio.route.owned", new() { ["streamId"] = id, ["targetKey"] = target, ["scope"] = "owned_track" }, token, target);

    internal async Task StopTrackAsync(uint id, CancellationToken token)
    {
        // Android releases a session-owned stream on cancel even after capability loss.
        await WriteAsync("cancel", new() { ["streamId"] = id }, null, token).ConfigureAwait(false);
    }

    private async Task WriteAsync(string type, JsonObject body, string? id, CancellationToken cancel, Action? beforeWrite = null)
    {
        await writeGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            beforeWrite?.Invoke();
            if (type == "request" && RequestSent is { } trace && body["op"] is JsonValue op)
                trace(op.GetValue<string>(), body["capabilityRevision"]?.GetValue<long>());
            await SrapClient.WriteAsync(stream, type, body, cancel, id, sessionId).ConfigureAwait(false);
        }
        catch { Dispose(); throw; } // A partially written TLS message cannot be safely resumed.
        finally { writeGate.Release(); }
    }

    private async Task<string?> ReadLoopAsync()
    {
        string? fault = null;
        try
        {
            while (true)
            {
                var frame = await SrapProtocol.ReadAsync(stream, token).ConfigureAwait(false) ?? throw new EndOfStreamException();
                if (frame.Type == SrapFrameType.Data)
                {
                    DeliverData(frame);
                    continue;
                }
                using var document = SrapProtocol.ParseControl(frame);
                var message = document.RootElement.Clone();
                if (Text(message, "session") != sessionId) throw new SrapException("stale_session");
                var body = Body(message);
                switch (Text(message, "type"))
                {
                    case "capabilities":
                        var update = SrapProtocol.ReadCapabilities(body);
                        if (update.Revision > Capabilities.Revision)
                        {
                            Volatile.Write(ref capabilities, new(update.Revision, update.Capabilities));
                            Volatile.Read(ref aliasExchange)?.Fail(Capabilities.Allows(AliasCapability) ? "stale_session" : "permission_lost");
                            CapabilitiesChanged?.Invoke();
                        }
                        break;
                    case "response":
                        var binding = Volatile.Read(ref aliasExchange);
                        if (binding != null && Text(message, "id") == binding.Id) binding.Response(body);
                        else Publish(Text(message, "id"), body);
                        break;
                    case "event":
                        if (!message.TryGetProperty("seq", out var value) || !value.TryGetInt64(out long next) || next <= seq)
                            throw new SrapException("invalid_argument");
                        seq = next;
                        string? name = Text(body, "name");
                        if (name == "session.error") throw Error(body);
                        StreamEvent?.Invoke(body);
                        if (name == "audio.routed") Publish(Text(body, "requestId"), body);
                        if (name == "media.changed") MediaChanged?.Invoke();
                        if (name == "alias.bind.proof") Volatile.Read(ref aliasExchange)?.Proof(body);
                        break;
                    default:
                        throw new SrapException("invalid_argument");
                }
            }
        }
        catch (SrapException ex) { fault = ex.Code == "permission_lost" && !ex.PeerRevoked ? "stale_session" : ex.Code; }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            foreach (var pending in requests.Values) pending.Writer.TryComplete(new SrapException(fault ?? "stale_session"));
            Dispose();
        }
        return fault;
    }

    private void Publish(string? id, JsonElement body)
    {
        if (id == null || !Guid.TryParseExact(id, "D", out var guid) || guid.ToString("D") != id)
            throw new SrapException("invalid_argument");
        if (requests.TryGetValue(id, out var target) && !target.Writer.TryWrite(body))
            throw new SrapException("invalid_argument");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        stream.Dispose();
        lifetime.Dispose();
    }
}
