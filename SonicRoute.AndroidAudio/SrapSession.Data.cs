namespace SonicRoute.AndroidAudio;

internal sealed partial class SrapSession
{
    private readonly object incomingLock = new();
    private readonly Dictionary<uint, DataLease> incomingData = [];

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
                throw new SrapException("invalid_argument");
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
