using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using SonicRoute.AndroidAudio;

internal static class QueueProbe
{
    private static async Task Until(Func<bool> ready, CancellationToken token)
    {
        while (!ready()) await Task.Delay(5, token);
    }

    internal static async Task AdbAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        var link = new HeldAdbLink();
        using var unusedKeys = new AdbKeyStore(Path.Combine(root, "unused-queue-fixture"));
        var pool = new CountingPool();
        await using var mux = await AdbMuxConnection.ConnectAsync(link, unusedKeys, token: token, outboundPool: pool);
        var streams = new List<AdbMuxStream>();
        try
        {
            for (int i = 0; i < AdbMuxConnection.MaxOpenStreams; i++)
                streams.Add(await mux.OpenStreamAsync("localabstract:queue" + i, token));
            try { await mux.OpenStreamAsync("localabstract:extra", token); throw new InvalidDataException("Open limit bypassed"); }
            catch (IOException ex) when (ex.Message.Contains("limit", StringComparison.Ordinal)) { }

            link.Hold();
            var writes = streams.Take(AdbMuxConnection.MaxPooledPackets).Select(s => s.WriteAsync(new byte[] { 10 }, token).AsTask()).ToArray();
            await Until(() => pool.Count == AdbMuxConnection.MaxPooledPackets, token);
            using (var pending = new CancellationTokenSource())
            {
                var waiting = Enumerable.Range(1, AdbMuxStream.MaxPendingWrites - 1)
                    .Select(i => streams[0].WriteAsync(new byte[] { (byte)i }, pending.Token).AsTask()).ToArray();
                try { await streams[0].WriteAsync(new byte[] { 99 },token); throw new InvalidDataException("Exceeded pending writer limit"); }
                catch (IOException ex) when (ex.Message.Contains("concurrent writes", StringComparison.Ordinal)) { }
                pending.Cancel();
                foreach (var write in waiting) await ExpectCancelled(write);
            }

            // This stream already took its credit but cannot rent a ninth packet.
            var victim = streams[AdbMuxConnection.MaxPooledPackets];
            using (var cancel = new CancellationTokenSource())
            {
                var blocked = victim.WriteAsync(new byte[] { 11 }, cancel.Token).AsTask();
                Program.Require(!blocked.IsCompleted, "Data producers must backpressure before renting");
                cancel.Cancel();
                await ExpectCancelled(blocked);
            }
            var incoming = streams.Where(s => s != victim).Select(s => s.LocalId).ToArray();
            foreach (uint local in incoming) link.SendPeer(AdbCommands.Wrte, local + 100, local, [31]);
            await Until(() => mux.QueuedPacketCount == AdbMuxConnection.OutboundCapacity, token);
            var sibling = streams[^1];
            var siblingWrite = sibling.WriteAsync(new byte[] { 42 }, token).AsTask();
            Program.Require(!siblingWrite.IsCompleted && pool.Count == AdbMuxConnection.MaxPooledPackets, "Bounded slow physical link");
            link.Resume();
            await Task.WhenAll(writes.Append(siblingWrite)).WaitAsync(token);
            await Until(() => link.Headers.Count(h => h.Command == AdbCommands.Okay) == incoming.Length, token);
            Program.Require(link.Headers.Where(h => h.Command == AdbCommands.Okay).Select(h => h.Arg0).SequenceEqual(incoming), "ACKs retain peer WRTE order");
            Program.Require(link.Headers.Count(h => h.Command == AdbCommands.Clse && h.Arg0 == victim.LocalId && h.Arg1 == victim.LocalId + 100) == 1,
                "Cancelled stream gets exactly one CLSE");
            byte[] read = new byte[1];
            Program.Require(await sibling.ReadAsync(read, token) == 1 && read[0] == 31 && mux.IsConnected,
                "Sibling data survives another stream's cancellation");
            await sibling.WriteAsync(new byte[] { 43 }, token);
            Program.Require(link.BytesFor(sibling.LocalId).SequenceEqual(new byte[] { 42,43 }), "Sibling writes remain ordered");
            await Until(() => pool.Count == 0, token);

            // Span writes retain their logical ordering but rent only one negotiated chunk at a time.
            byte[] large = new byte[mux.SendLimit * 2 + 13];
            large[0] = 51; large[mux.SendLimit] = 52; large[mux.SendLimit * 2] = 53;
            link.Hold();
            var sync = Task.Run(() => sibling.Write(large.AsSpan()), token);
            await Until(() => pool.Count > 0, token);
            var following = sibling.WriteAsync(new byte[]{ 54 },token).AsTask();
            link.Resume();
            await Task.WhenAll(sync, following).WaitAsync(token);
            Program.Require(link.BytesFor(sibling.LocalId).SequenceEqual(new byte[]{42,43,51,52,53,54})
                && pool.LargestRequest == mux.SendLimit, "Synchronous span writes bound staging without interleaving");

            await using (var flow = await mux.OpenStreamAsync("localabstract:flow-credit", token))
                await DeferredAckAsync(mux, flow, link, token);

            // Include in-flight, FIFO and waiting-rental ownership in connection teardown.
            link.Hold();
            var aborted = streams.Where(s => s != victim).Take(AdbMuxConnection.MaxPooledPackets + 1)
                .Select(s => s.WriteAsync(new byte[] { 44 }, token).AsTask()).ToArray();
            await Until(() => pool.Count == AdbMuxConnection.MaxPooledPackets, token);
            await mux.DisposeAsync();
            foreach (var task in aborted)
            {
                try { await task.WaitAsync(token); throw new InvalidDataException("Write completed on stalled, closed link"); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ChannelClosedException or ObjectDisposedException) { }
            }
            Program.Require(pool.Count == 0 && pool.Rents == pool.Returns && pool.Peak == AdbMuxConnection.MaxPooledPackets,
                "Each accepted payload returned exactly once");
            Console.WriteLine($"ADB FIFO={AdbMuxConnection.OutboundCapacity} retainedPayloadsPeak={pool.Peak} rents={pool.Rents} returns={pool.Returns} inOrderACK={incoming.Length} maxFixtureRentBytes={pool.LargestRequest}");
        }
        finally { foreach (var s in streams) await s.DisposeAsync(); }
    }

    private static async Task DeferredAckAsync(AdbMuxConnection mux, AdbMuxStream stream, HeldAdbLink link, CancellationToken token)
    {
        int CountAcks() => link.Headers.Count(h => h.Command == AdbCommands.Okay && h.Arg0 == stream.LocalId);
        const int block = 64 * 1024;
        for (int i = 1; i <= 64; i++)
        {
            var packet = new byte[block]; Array.Fill(packet, (byte)i);
            link.SendPeer(AdbCommands.Wrte, stream.RemoteId, stream.LocalId, packet);
            if (i < 64) await Until(() => CountAcks() == i, token);
        }
        link.Hold();
        for (uint n = 1; n <= AdbMuxConnection.OutboundCapacity + 2; n++)
            link.SendPeer(AdbCommands.Open, 10000 + n, 0, []);
        await Until(() => mux.QueuedPacketCount == AdbMuxConnection.OutboundCapacity, token);
        byte[] data = new byte[block];
        using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
        read.CancelAfter(TimeSpan.FromSeconds(1));
        for (int i = 1; i <= 32; i++)
        {
            await stream.ReadExactlyAsync(data, read.Token);
            Program.Require(data[0] == i && data[^1] == i, "Preserve the bytes which released a deferred ACK");
        }
        read.Cancel();
        await stream.ReadExactlyAsync(data, token);
        Program.Require(data[0] == 33 && CountAcks() == 63, "The next read works while the ACK remains queued");
        link.Resume();
        await Until(() => CountAcks() == 64, token);
    }

    private static async Task ExpectCancelled(Task task)
    {
        try { await task; throw new InvalidDataException("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }

    private sealed class CountingPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> _owned = [];
        internal int Peak, Rents, Returns, LargestRequest;
        internal int Count { get { lock (_owned) return _owned.Count; } }
        public override byte[] Rent(int min)
        {
            byte[] bytes = Shared.Rent(min);
            lock (_owned)
            {
                if (!_owned.Add(bytes)) throw new InvalidDataException("Duplicate pool lease");
                Peak = Math.Max(Peak, _owned.Count);
                LargestRequest = Math.Max(LargestRequest, min);
                Rents++;
            }
            return bytes;
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_owned)
            {
                if (!_owned.Remove(array)) throw new InvalidDataException("Returning an unowned packet");
                Returns++;
            }
            Shared.Return(array, clearArray);
        }
    }

    private sealed class HeldAdbLink : IAdbPacketLink
    {
        private readonly Channel<(AdbPacketHeader Header,byte[] Payload)> _in = Channel.CreateBounded<(AdbPacketHeader,byte[])>(128);
        private readonly List<(AdbPacketHeader Header,byte First)> _writes = [];
        private volatile TaskCompletionSource? _held;
        public bool SupportsTlsUpgrade => false;
        public string? PeerCertificateSha256 => null;
        internal AdbPacketHeader[] Headers { get { lock (_writes) return _writes.Select(f=>f.Header).ToArray(); } }
        internal byte[] BytesFor(uint id)
        {
            lock (_writes) return _writes.Where(f=>f.Header.Command==AdbCommands.Wrte && f.Header.Arg0==id).Select(f=>f.First).ToArray();
        }
        internal void Hold() => _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Resume() => Interlocked.Exchange(ref _held, null)?.TrySetResult();
        internal void SendPeer(uint cmd, uint a0, uint a1, byte[] data)
        {
            if (!_in.Writer.TryWrite((AdbPacketHeader.Create(cmd,a0,a1,data),data))) throw new InvalidDataException("Fixture input full");
        }
        public async ValueTask<(AdbPacketHeader Header,int PayloadLength)> ReadPacketAsync(byte[] data, CancellationToken token)
        {
            var p = await _in.Reader.ReadAsync(token);
            p.Payload.CopyTo(data, 0);
            return (p.Header,p.Payload.Length);
        }
        public async ValueTask<(AdbPacketHeader Header,int PayloadLength,byte[]? RentedBuffer)> ReadPacketPooledAsync(byte[] data, int max, CancellationToken token)
        {
            var p = await ReadPacketAsync(data, token);
            return (p.Header,p.PayloadLength,null);
        }
        public async ValueTask WritePacketAsync(AdbPacketHeader h, ReadOnlyMemory<byte> data, CancellationToken token)
        {
            if (_held is { } held) await held.Task.WaitAsync(token);
            lock (_writes)
            {
                if (_writes.Count >= 256) throw new InvalidDataException("Fixture write record limit");
                _writes.Add((h,data.IsEmpty ? (byte)0 : data.Span[0]));
            }
            if (h.Command == AdbCommands.Cnxn) SendPeer(AdbCommands.Cnxn, AdbMuxConnection.VersionSkipChecksum, 65536, "device::"u8.ToArray());
            else if (h.Command == AdbCommands.Open || h.Command == AdbCommands.Wrte) SendPeer(AdbCommands.Okay,h.Arg0+100,h.Arg0,[]);
        }
        public Task UpgradeToTlsAsync(X509Certificate2 certificate, Func<X509Certificate2?,bool>? validator, string? host, CancellationToken token) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() { _in.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    internal static async Task VideoAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        var queue = new InProcessVirtualDisplayClient.ControlQueue();
        var blocked = new HeldStream();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = queue.WaitForSessionEndAsync(live.Task, live.Task, session);
        var sending = queue.SendAsync(blocked, _ => true, session.Token);
        Program.Require(queue.Enqueue([10]), "First control packet admitted");
        await blocked.Started.Task.WaitAsync(token);
        for (int i=0; i<InProcessVirtualDisplayClient.ControlQueue.Capacity; i++)
            Program.Require(queue.Enqueue([(byte)i]), "Pre-boundary control accepted");
        Program.Require(!queue.Enqueue([255]) && (await queue.Failure.WaitAsync(token)).Message.Contains("queue is full", StringComparison.Ordinal), "Saturation explicitly faults only the affected display");
        await ending.WaitAsync(token);
        Program.Require(session.IsCancellationRequested, "The actual session supervisor cancels a saturated controller");
        await sending.WaitAsync(token);
        Program.Require(!queue.Enqueue([0]) && blocked.Length == 0, "Cancelled control writer drains no stale commands");

        var failed = new InProcessVirtualDisplayClient.ControlQueue();
        using var failedSession = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ended = failed.WaitForSessionEndAsync(live.Task, live.Task, failedSession);
        Program.Require(failed.Enqueue([1]), "Initial write on independently failing stream");
        using var broken = new FailedControlStream();
        await failed.SendAsync(broken, _ => true, failedSession.Token);
        await ended.WaitAsync(token);
        Program.Require(failedSession.IsCancellationRequested && failed.Failure.Result.Message == "fixture control disconnected",
            "Writer disconnect terminates its own video session");

        var other = new InProcessVirtualDisplayClient.ControlQueue();
        var destination = new HeldStream();
        var send = other.SendAsync(destination, _ => true, token);
        var down = new byte[14]; var up = new byte[14];
        ScrcpyControlMessageWriter.WriteInjectKeycode(down,0,29,0,0);
        ScrcpyControlMessageWriter.WriteInjectKeycode(up,1,29,0,0);
        Program.Require(other.Enqueue(down), "Sibling key down");
        await destination.Started.Task.WaitAsync(token);
        byte[] motion = new byte[32];
        for (int x=0;x<4000;x++)
        {
            ScrcpyControlMessageWriter.WriteInjectTouch(motion,ScrcpyControlMessageWriter.MotionActionHoverMove,
                ScrcpyControlMessageWriter.PointerIdMouse,x,0,4096,256,0,0,0);
            Program.Require(other.EnqueueTouch(motion), "Motions should coalesce");
        }
        Program.Require(other.Enqueue(up), "Sibling key up");
        destination.Release.SetResult();
        await Until(()=>destination.Length == down.Length + motion.Length + up.Length,token);
        other.Close();
        await send.WaitAsync(token);
        Program.Require(destination.ToArray().SequenceEqual(down.Concat(motion).Concat(up)), "Preserve down, last hover, up order");
        Console.WriteLine($"display FIFO={InProcessVirtualDisplayClient.ControlQueue.Capacity} coalesced=4000 sibling=down/last-hover/up");
    }

    private sealed class FailedControlStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token = default) =>
            ValueTask.FromException(new IOException("fixture control disconnected"));
    }

    private sealed class HeldStream : MemoryStream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _recorded;
        public override long Length => Volatile.Read(ref _recorded);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            base.Write(data.Span);
            Interlocked.Add(ref _recorded,data.Length);
        }
    }
}

