using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

internal sealed record AppCapturePermission(string? Package, string? Reason, string? Grant)
{
    internal static AppCapturePermission? Read(IReadOnlyDictionary<string, SrapCapability> entries)
    {
        if (!entries.TryGetValue("audio.capture.app", out var cap)) return null;
        if (cap.State == "needs_authorization") return new(null, cap.Reason, null);
        const string prefix = "other_app:";
        if (cap.State != "available" || cap.Scope?.StartsWith(prefix, StringComparison.Ordinal) != true
            || cap.Limits?.GetValueOrDefault("pcm") != SrapSession.PcmEncoding
            || cap.Limits.GetValueOrDefault("mode") != "playback_capture") return null;
        string package = cap.Scope[prefix.Length..];
        if (package.Length is < 1 or > 255) return null;
        try { AdbClient.ValidatePackage(package); }
        catch (ArgumentException) { return null; }
        return new(package, null, cap.Limits.GetValueOrDefault("grant"));
    }

    internal static void Require(AppCapturePermission? grant, AndroidApp app, bool keepPhoneAudio)
    {
        if (!keepPhoneAudio || app.IsDeviceAudio || grant == null) throw new SrapException("unsupported");
        if (!string.Equals(grant.Package, app.Package, StringComparison.Ordinal))
            throw new SrapException("permission_required", grant.Reason);
    }
}

// The TLS reader only validates and enqueues complete packets. The receiver grants
// new credit after writing PCM into the existing playback source.
internal sealed class SrapPcmCapture(SrapSession session, AndroidApp app)
{
    private const int Window = SrapPcmOutput.Window;
    private const int Header = 12;
    private readonly Channel<ReadOnlyMemory<byte>> packets = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(Window) { SingleReader = true, SingleWriter = true });
    private readonly object gate = new();
    private readonly AppCapturePermission? expected = session.Capabilities.Capture;
    private CancellationTokenSource? run;
    private HashSet<uint>? canceledBeforeStart = [];
    private Exception? failure;
    private uint streamId;
    private ulong received, position, permitted;
    private long consumed;
    private bool stopping;
    internal long PacketsDecoded => Interlocked.Read(ref consumed);
    internal bool StopConfirmed { get; private set; }

    internal async Task ReceiveAsync(AndroidPcmSource pcm, Action firstFrame, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        run = lifetime;
        var ct = lifetime.Token;
        IDisposable? data = null;
        session.CapabilitiesChanged += Recheck;
        session.StreamEvent += OnEvent;
        try
        {
            AppCapturePermission.Require(expected, app, true);
            Recheck();
            ct.ThrowIfCancellationRequested();
            var result = await session.RequestAsync("audio.capture.start", "audio.capture.app", new()
            {
                ["appKey"] = app.Package, ["encoding"] = SrapSession.PcmEncoding, ["keepPhoneAudio"] = true
            }, ct).ConfigureAwait(false);
            if (!result.TryGetProperty("streamId", out var field) || !field.TryGetUInt32(out uint id) || id == 0 || (id & 1) != 0)
                throw new SrapException("invalid_argument");
            // A new receiver owns the ID only after registration. On collision do
            // not stop the previous owner's stream. No credit has been sent.
            data = session.ExpectData(id, OnData);
            lock (gate)
            {
                streamId = id;
                if (canceledBeforeStart!.Contains(id)) FailLocked("canceled");
                canceledBeforeStart = null;
            }
            if (SrapClient.Text(result, "encoding") != SrapSession.PcmEncoding
                || !result.TryGetProperty("credit", out var count) || !count.TryGetInt32(out int credit) || credit != Window)
                throw new SrapException("invalid_argument");
            Recheck();
            ct.ThrowIfCancellationRequested();
            await GrantAsync(0, ct).ConfigureAwait(false);
            var samples = new float[SrapPcmOutput.MaxStereoFrames * AndroidPcmSource.Channels];
            pcm.HeaderGain = 1f;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                if (!packets.Reader.TryRead(out var packet))
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(8));
                    try { packet = await packets.Reader.ReadAsync(idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new SrapException("timeout"); }
                    finally { idle.CancelAfter(Timeout.InfiniteTimeSpan); }
                }
                ct.ThrowIfCancellationRequested();
                int sampleCount = (packet.Length - Header) / sizeof(short);
                ScrcpyRawPcmReceiver.ConvertPcm16LeToFloat(packet.Span[Header..], samples.AsSpan(0, sampleCount));
                pcm.Write(samples.AsSpan(0, sampleCount));
                long next = Interlocked.Increment(ref consumed);
                if (next == 1) firstFrame();
                if (next % (Window / 2) == 0) await GrantAsync((ulong)next, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref failure) is { } reason) { throw reason; }
        finally
        {
            lock (gate) stopping = true;
            session.CapabilitiesChanged -= Recheck;
            session.StreamEvent -= OnEvent;
            try
            {
                if (streamId != 0 && !session.Token.IsCancellationRequested)
                {
                    using var confirm = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await session.StopPcmAsync(streamId, confirm.Token).ConfigureAwait(false); StopConfirmed = true; }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or ChannelClosedException)
                    {
                        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        if (!session.Token.IsCancellationRequested)
                            try { await session.StopTrackAsync(streamId, cancel.Token).ConfigureAwait(false); }
                            catch (Exception) { session.Dispose(); }
                    }
                }
            }
            finally { data?.Dispose(); }
        }
    }

    private async Task GrantAsync(ulong acknowledged, CancellationToken token)
    {
        var result = await session.RequestAsync("audio.capture.credit", "audio.capture.app", new()
        {
            ["streamId"] = streamId, ["consumedPackets"] = acknowledged
        }, token, beforeWrite: () => { lock (gate) permitted = acknowledged + Window; }).ConfigureAwait(false);
        if (!result.TryGetProperty("streamId", out var id) || !id.TryGetUInt32(out uint stream) || stream != streamId
            || !result.TryGetProperty("consumedPackets", out var count) || !count.TryGetUInt64(out ulong echoed) || echoed != acknowledged)
            throw new SrapException("invalid_argument");
    }

    private void OnData(ReadOnlyMemory<byte> payload)
    {
        lock (gate)
        {
            if (stopping || failure != null || run!.IsCancellationRequested) return;
            int bytes = payload.Length - Header;
            if (bytes is < SrapPcmOutput.FrameBytes or > SrapPcmOutput.MaxStereoFrames * SrapPcmOutput.FrameBytes
                || bytes % SrapPcmOutput.FrameBytes != 0 || received >= permitted
                || BinaryPrimitives.ReadUInt32BigEndian(payload.Span) != received
                || BinaryPrimitives.ReadUInt64BigEndian(payload.Span[4..]) != position
                || !packets.Writer.TryWrite(payload))
            {
                FailLocked("invalid_argument");
                return;
            }
            received++;
            position += (uint)(bytes / SrapPcmOutput.FrameBytes);
        }
    }

    private void OnEvent(JsonElement body)
    {
        if (SrapClient.Text(body, "name") != "stream.canceled" || !body.TryGetProperty("streamId", out var val)
            || !val.TryGetUInt32(out uint id) || id == 0 || (id & 1) != 0) return;
        lock (gate)
        {
            if (id == streamId) FailLocked("canceled");
            else if (canceledBeforeStart != null && canceledBeforeStart.Add(id) && canceledBeforeStart.Count > 16)
                FailLocked("invalid_argument");
        }
    }

    private void Recheck()
    {
        if (session.Capabilities.Capture != expected)
            lock (gate) FailLocked("permission_lost");
    }

    private void FailLocked(string code)
    {
        if (stopping || failure != null) return;
        Volatile.Write(ref failure, new SrapException(code));
        _ = run!.CancelAsync();
    }
}
