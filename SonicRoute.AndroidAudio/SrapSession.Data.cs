namespace SonicRoute.AndroidAudio;

internal sealed partial class SrapSession
{
    private readonly object incomingLock = new();
    private readonly Dictionary<uint, DataLease> incomingData = [];
    // A canceled download may already have one solicited DATA packet in flight.
    // IDs are never reused. Keep only its exact size, for the bounded request lifetime.
    private readonly Dictionary<uint, (int Bytes, long Expires)> canceledFileChunks = [];

    internal void DiscardPendingFileChunk(uint id, int bytes)
    {
        lock (incomingLock)
        {
            long now = Environment.TickCount64;
            foreach (var pair in canceledFileChunks.ToArray())
                if (pair.Value.Expires <= now) canceledFileChunks.Remove(pair.Key);
            if (canceledFileChunks.Count == 16)
                canceledFileChunks.Remove(canceledFileChunks.MinBy(p => p.Value.Expires).Key);
            canceledFileChunks[id] = (bytes, now + 8000);
        }
    }

    internal IDisposable ExpectData(uint id, Action<ReadOnlyMemory<byte>> onData)
    {
        lock (incomingLock)
        {
            token.ThrowIfCancellationRequested();
            var lease = new DataLease(this, id, onData);
            if (id == 0 || incomingData.Count >= 16 || !incomingData.TryAdd(id, lease))
                throw new SrapException("busy");
            return lease;
        }
    }

    private void DeliverData(SrapFrame frame)
    {
        DataLease lease;
        lock (incomingLock)
        {
            if (!incomingData.TryGetValue(frame.StreamId, out var target))
            {
                if (canceledFileChunks.Remove(frame.StreamId, out var pending) && pending.Expires >= Environment.TickCount64
                    && frame.Payload.Length == pending.Bytes) return;
                throw new SrapException("invalid_argument");
            }
            lease = target;
        }
        lease.Receive(frame.Payload);
    }

    private sealed class DataLease(SrapSession session, uint id, Action<ReadOnlyMemory<byte>> callback) : IDisposable
    {
        private Action<ReadOnlyMemory<byte>>? receiver = callback;
        internal void Receive(ReadOnlyMemory<byte> payload) => Volatile.Read(ref receiver)?.Invoke(payload);
        public void Dispose()
        {
            lock (session.incomingLock)
            {
                if (Interlocked.Exchange(ref receiver, null) != null) session.incomingData.Remove(id);
            }
        }
    }
}
