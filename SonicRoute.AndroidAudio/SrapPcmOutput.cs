using System.Buffers.Binary;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

// An SRAP sender owns one approved AudioTrack and only writes PCM after its actual
// output is confirmed. A blocked writer wakes on credit, cancellation or link loss.
internal sealed class SrapPcmOutput : Stream, IPhonePcmConnection
{
    internal const int Window = 4;
    internal const int MaxStereoFrames = 960;
    internal const int FrameBytes = 4;
    private readonly SrapSession session;
    private readonly CancellationTokenSource stop;
    private readonly CancellationTokenRegistration onStop;
    private readonly Action<string, int, string>? onRouted;
    private readonly object gate = new();
    private readonly SemaphoreSlim writing = new(1, 1), switching = new(1, 1);
    private readonly byte[] payload = new byte[12 + MaxStereoFrames * FrameBytes];
    private readonly TaskCompletionSource<Exception> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? credit;
    private ulong sent, acknowledged, framePosition;
    private string? target;
    private bool paused, requested;
    private int disposed;
    internal uint Id { get; }
    internal bool StopConfirmed { get; private set; }
    public AndroidDevice Device { get; }
    public Stream Stream => this;
    public int RoutedType { get; private set; }
    public Task<Exception> Completion => ended.Task;
    public event Action<Exception>? Failed;

    private SrapPcmOutput(SrapSession session, AndroidDevice device, CancellationToken token, Action<string, int, string>? onRouted)
    {
        this.session = session;
        this.onRouted = onRouted;
        Device = device;
        Id = session.AllocateStreamId();
        stop = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        onStop = stop.Token.Register(() => Abort(new SrapException("stale_session")));
    }

    internal static async Task<IPhonePcmConnection> OpenAsync(SrapSession session, AndroidDevice device, string target,
        CancellationToken token, Action<string, int, string>? routed = null)
    {
        var output = new SrapPcmOutput(session, device, token, routed);
        try
        {
            session.RegisterPcm(output);
            session.CapabilitiesChanged += output.Recheck;
            session.StreamEvent += output.OnEvent;
            output.requested = true;
            await session.StartPcmAsync(output.Id, target, output.stop.Token).ConfigureAwait(false);
            await output.SwitchTargetAsync(target, output.stop.Token).ConfigureAwait(false);
            return output;
        }
        catch { await output.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private void Recheck()
    {
        if (!session.SupportsPcm) Abort(new SrapException("permission_lost"));
    }

    private void OnEvent(JsonElement body)
    {
        string? name = SrapClient.Text(body, "name");
        if (name is not ("audio.credit" or "stream.canceled") || !body.TryGetProperty("streamId", out var val)
            || !val.TryGetUInt32(out uint id) || id != Id) return;
        if (name == "stream.canceled") { Abort(new SrapException("canceled")); return; }
        if (body.TryGetProperty("nextSeq", out var count) && count.TryGetUInt64(out ulong next)) Acknowledge(next);
        else Abort(new SrapException("invalid_argument"));
    }

    public async Task SwitchTargetAsync(string key, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        var ct = operation.Token;
        await switching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (target == key) return;
            await writing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await session.RouteTrackAsync(Id, key, ct).ConfigureAwait(false);
                if (result.TryGetProperty("streamId", out var field) && (!field.TryGetUInt32(out uint id) || id != Id))
                    throw new SrapException("stale_session");
                var actual = SrapClient.Text(result, "actualKey")!;
                RoutedType = result.TryGetProperty("type", out var type) && type.TryGetInt32(out int value) ? value : 0;
                target = key;
                onRouted?.Invoke(actual, RoutedType, actual);
            }
            finally { writing.Release(); }
        }
        catch (Exception ex) { Abort(ex); throw; }
        finally { switching.Release(); }
    }

    public Task WaitForRoutingAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ended.Task.IsCompletedSuccessfully) throw ended.Task.Result;
        return target == null ? Task.FromException(new SrapException("target_unavailable")) : Task.CompletedTask;
    }

    public void SetPaused(bool value)
    {
        if (paused == value) return;
        var result = session.RequestAsync("volume.set", "audio.play", new()
        {
            ["scope"] = "owned_track", ["streamId"] = Id, ["trackMuted"] = value
        }, stop.Token).GetAwaiter().GetResult();
        if (!result.TryGetProperty("trackMuted", out var muted) || muted.ValueKind != (value ? JsonValueKind.True : JsonValueKind.False))
            throw new SrapException("io_failure");
        paused = value;
    }

    internal void Acknowledge(ulong next)
    {
        lock (gate)
        {
            if (next < acknowledged || next > sent) { Abort(new SrapException("invalid_argument")); return; }
            if (next == acknowledged) return;
            acknowledged = next;
            credit?.TrySetResult();
            credit = null;
        }
    }

    internal async Task<ulong> WaitForConsumptionAsync(CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        while (true)
        {
            Task wait;
            lock (gate)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (sent == acknowledged) return acknowledged;
                wait = (credit ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            await wait.WaitAsync(TimeSpan.FromSeconds(5), operation.Token).ConfigureAwait(false);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if ((buffer.Length % FrameBytes) != 0) throw new InvalidDataException("Incomplete stereo PCM frame.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
        var ct = operation.Token;
        await writing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (!buffer.IsEmpty)
            {
                int size = Math.Min(MaxStereoFrames * FrameBytes, buffer.Length);
                Task? wait;
                lock (gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ended.Task.IsCompletedSuccessfully) throw ended.Task.Result;
                    wait = sent - acknowledged >= Window ? (credit ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : null;
                    if (wait == null)
                    {
                        if (sent > uint.MaxValue) throw new SrapException("busy");
                        EncodePacket(payload, (uint)sent++, framePosition, buffer.Span[..size]);
                        framePosition += (uint)(size / FrameBytes);
                    }
                }
                if (wait != null) { await wait.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); continue; }
                await session.WriteDataFrameAsync(Id, payload.AsMemory(0, 12 + size), ct).ConfigureAwait(false);
                buffer = buffer[size..];
            }
        }
        catch (Exception ex) { Abort(ex); throw; }
        finally { writing.Release(); }
    }

    internal static void EncodePacket(Span<byte> destination, uint seq, ulong position, ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length is < FrameBytes or > MaxStereoFrames * FrameBytes || pcm.Length % FrameBytes != 0)
            throw new SrapException("invalid_argument");
        BinaryPrimitives.WriteUInt32BigEndian(destination, seq);
        BinaryPrimitives.WriteUInt64BigEndian(destination[4..], position);
        pcm.CopyTo(destination[12..]);
    }

    public void Abort(Exception error)
    {
        if (Volatile.Read(ref disposed) != 0 || !ended.TrySetResult(error)) return;
        stop.Cancel();
        if (Failed is { } failed) _ = Task.Run(() => { try { failed(error); } catch { } });
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        session.CapabilitiesChanged -= Recheck;
        session.StreamEvent -= OnEvent;
        stop.Cancel();
        onStop.Dispose();
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            if (requested && !session.Token.IsCancellationRequested)
            {
                using var confirm = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await session.StopPcmAsync(Id, confirm.Token).ConfigureAwait(false); StopConfirmed = true; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Threading.Channels.ChannelClosedException or ObjectDisposedException)
                {
                    if (!session.Token.IsCancellationRequested)
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        try { await session.StopTrackAsync(Id, cleanup.Token).ConfigureAwait(false); }
                        catch (Exception) { session.Dispose(); }
                    }
                }
            }
        }
        finally
        {
            session.RetirePcm(this);
            Array.Clear(payload);
            writing.Release();
            stop.Dispose();
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => Volatile.Read(ref disposed) == 0;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    protected override void Dispose(bool disposing) { if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
