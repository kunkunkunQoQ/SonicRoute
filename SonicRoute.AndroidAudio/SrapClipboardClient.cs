using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

internal sealed class SrapClipboardClient(SrapPhoneTransport transport) : IAndroidClipboardClient
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private sealed record Watch(AndroidDevice Device, SrapSession Session, string Id, CancellationToken Token)
    {
        private long revision = -1;
        internal long Revision => Interlocked.Read(ref revision);
        internal void Advance(long next)
        {
            long before;
            do { before = Revision; if (before >= next) return; }
            while (Interlocked.CompareExchange(ref revision, next, before) != before);
        }
    }
    private Watch? active;
    public long Revision => Volatile.Read(ref active)?.Revision ?? -1;

    public async Task WatchAsync(AndroidDevice device, Action ready, Action<string> changed, CancellationToken token)
    {
        var session = transport.Session(device) ?? throw new SrapException("stale_session");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, session.Token);
        var watch = new Watch(device, session, Guid.NewGuid().ToString("D"), lifetime.Token);
        if (Interlocked.CompareExchange(ref active, watch, null) != null) throw new SrapException("busy");
        var gate = new object();
        IDisposable? expectation = null;
        Timer? dataDeadline = null;
        long lastReceived = -1;
        uint lastStream = 0;
        void LostCapability()
        {
            if (!session.AllowsFocusedClipboard) lifetime.Cancel();
        }
        void Deliver(long version, string? value)
        {
            if (lifetime.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref active), watch)
                || !session.AllowsFocusedClipboard || !ReferenceEquals(session, transport.Session(device))) return;
            if (version <= lastReceived || version < watch.Revision) return;
            lastReceived = version;
            watch.Advance(version);
            if (value != null && value.IndexOf('\0') < 0 && AndroidClipboardClient.IsSupported(value)) changed(value);
        }
        void Event(JsonElement body)
        {
            string? name = SrapClient.Text(body, "name");
            if (name is not ("clipboard.changed" or "clipboard.revision") || SrapClient.Text(body, "watchId") != watch.Id) return;
            if (!body.TryGetProperty("revision", out var stamp) || !stamp.TryGetInt64(out long version) || version < 0)
                throw new SrapException("invalid_argument");
            if (name == "clipboard.revision") { Deliver(version, null); return; }
            if (!body.TryGetProperty("byteLength", out var size) || !size.TryGetInt32(out int count) || count is < 0 or > SrapProtocol.MaxPayload
                || !body.TryGetProperty("streamId", out var stream) || !stream.TryGetUInt32(out uint dataId))
                throw new SrapException("invalid_argument");
            if (count == 0)
            {
                if (dataId != 0) throw new SrapException("invalid_argument");
                Deliver(version, "");
                return;
            }
            lock (gate)
            {
                if (expectation != null || dataId == 0 || (dataId & 1) != 0 || dataId <= lastStream)
                    throw new SrapException("invalid_argument");
                lastStream = dataId;
                expectation = session.ExpectData(dataId, payload =>
                {
                    lock (gate)
                    {
                        dataDeadline?.Dispose(); dataDeadline = null;
                        expectation?.Dispose(); expectation = null;
                    }
                    try
                    {
                        if (payload.Length != count) throw new SrapException("invalid_argument");
                        if (!lifetime.IsCancellationRequested) Deliver(version, StrictUtf8.GetString(payload.Span));
                    }
                    catch (DecoderFallbackException) { throw new SrapException("invalid_argument"); }
                    finally
                    {
                        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment)) CryptographicOperations.ZeroMemory(segment.AsSpan());
                    }
                });
                dataDeadline = new Timer(_ =>
                {
                    lock (gate)
                    {
                        if (expectation == null || lastStream != dataId) return;
                        expectation.Dispose(); expectation = null;
                        dataDeadline?.Dispose(); dataDeadline = null;
                    }
                    try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
                }, null, TimeSpan.FromSeconds(6), Timeout.InfiniteTimeSpan);
            }
        }
        session.StreamEvent += Event;
        session.CapabilitiesChanged += LostCapability;
        try
        {
            if (!session.AllowsFocusedClipboard) throw new SrapException("permission_lost");
            var result = await session.RequestAsync("clipboard.watch", "clipboard.sync", new() { ["watchId"] = watch.Id }, lifetime.Token).ConfigureAwait(false);
            if (!result.TryGetProperty("revision", out var row) || !row.TryGetInt64(out long start) || start < 0)
                throw new SrapException("invalid_argument");
            watch.Advance(start);
            ready();
            await Task.Delay(Timeout.Infinite, lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            Interlocked.CompareExchange(ref active, null, watch);
            session.CapabilitiesChanged -= LostCapability;
            if (!session.Token.IsCancellationRequested)
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                stop.CancelAfter(TimeSpan.FromSeconds(2));
                try { await session.RequestAsync("clipboard.unwatch", null, new() { ["watchId"] = watch.Id }, stop.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or System.Threading.Channels.ChannelClosedException) { }
            }
            session.StreamEvent -= Event;
            lock (gate)
            {
                dataDeadline?.Dispose(); dataDeadline = null;
                expectation?.Dispose(); expectation = null;
            }
        }
    }

    public async Task SendAsync(string text, long expectedRevision, CancellationToken token)
    {
        var watch = Volatile.Read(ref active) ?? throw new SrapException("stale_session");
        if (expectedRevision < 0 || expectedRevision != watch.Revision || !ReferenceEquals(watch.Session, transport.Session(watch.Device)))
            throw new SrapException("stale_session");
        if (text.Contains('\0') || !AndroidClipboardClient.IsSupported(text)) throw new SrapException("invalid_argument");
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(token, watch.Token);
        var bytes = StrictUtf8.GetBytes(text);
        try
        {
            long next = await watch.Session.WriteClipboardAsync(watch.Id, expectedRevision, bytes, sending.Token).ConfigureAwait(false);
            watch.Advance(next);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

}
