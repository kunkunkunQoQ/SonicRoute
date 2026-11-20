using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace SonicRoute.WindowsLink;

internal sealed class LinkConnection(TcpClient socket, LinkSecure secure, bool preferred, IPAddress remoteAddress, CancellationToken lifetime)
{
    private const int HeaderBytes = 21;
    internal const int VideoChunk = 12 * 1024;
    internal const int MaxCatalogBytes = 256 * 1024;
    internal const int LegacyMaxVideoFrame = 1024 * 1024;
    internal const int MaxVideoFrame = 8 * 1024 * 1024;
    internal int VideoFrameLimit => Hello.SupportsNativeVideo ? MaxVideoFrame : LegacyMaxVideoFrame;
    internal const int MaxAudioPacket = 16 * 1024;
    private const int MaxPayload = MaxAudioPacket;
    private readonly object _gate = new();
    internal readonly SemaphoreSlim AudioStarts = new(1, 1);
    internal Guid RemoteTrustGrant;
    internal Guid LocalClipboardConsent, RemoteClipboardConsent;
    internal LinkTrustExchange? TrustExchange;
    internal readonly SemaphoreSlim ViewerStarts = new(1, 1);
    private readonly Queue<(LinkFrame Frame, TaskCompletionSource? Written)> _control = new();
    private FileBatch? _fileBatch;
    private readonly Queue<AudioPacket> _audio = new();
    private readonly Queue<Video> _video = new();
    private readonly HashSet<Guid> _waitForKey = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly CancellationTokenSource _closed = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly long _opened = Stopwatch.GetTimestamp();
    private long _lastRead = Stopwatch.GetTimestamp();
    private long _pingStarted;
    private TaskCompletionSource<double?>? _ping;
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    private long _sent;
    private long _received;
    private int _aborted;
    internal string Id => secure.Id;
    internal LinkHello Hello => secure.Hello;
    internal bool Preferred => preferred;
    internal IPAddress RemoteAddress => remoteAddress;
    internal bool IsOpen => Volatile.Read(ref _aborted) == 0 && !lifetime.IsCancellationRequested;
    internal bool RecentlyOpened => Stopwatch.GetElapsedTime(_opened) < TimeSpan.FromSeconds(10);
    internal long SentBytes => Interlocked.Read(ref _sent);
    internal long ReceivedBytes => Interlocked.Read(ref _received);
    internal bool VideoBacklogged { get { lock (_gate) return _video.Count >= 2; } }

    private readonly record struct AudioPacket(Guid Id, byte[] Data, int Length, long At)
    {
        internal void Release() => ArrayPool<byte>.Shared.Return(Data, clearArray: true);
    }

    private sealed record Video(Guid Id, byte[] Data, long Pts, bool Key, bool SwitchedToAvc);
    private sealed class FileBatch(Guid id, byte[] data, int length)
    {
        internal readonly Guid Id = id;
        internal readonly byte[] Data = data;
        internal readonly int Length = length;
        internal int Offset;
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The owner keeps the buffer unchanged until completion. Revisit media priorities between wire frames.
    internal Task SendFileBatchAsync(Guid id, byte[] data, int length, CancellationToken token)
    {
        var batch = new FileBatch(id, data, length);
        lock (_gate)
        {
            if (!IsOpen) throw new IOException("The Windows Link connection is closed.");
            if (_fileBatch != null) throw new IOException("A Windows Link file batch is already queued.");
            _fileBatch = batch;
            SignalWriter();
        }
        return batch.Written.Task.WaitAsync(token);
    }

    internal void RetireFile(Guid id)
    {
        lock (_gate)
        {
            if (_fileBatch?.Id != id) return;
            _fileBatch.Written.TrySetCanceled();
            _fileBatch = null;
        }
    }

    internal Task SendAsync(LinkMessage type, Guid id, byte[] body, CancellationToken token = default)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!IsOpen) throw new IOException("The Windows Link connection is closed.");
            if (_control.Count >= 32) throw new IOException("The Windows Link control queue is full.");
            _control.Enqueue((new LinkFrame(type, id, body), written));
            SignalWriter();
        }
        return written.Task.WaitAsync(token);
    }

    internal async Task SendCatalogResponseAsync(Guid requestId, byte[] payload, CancellationToken token = default)
    {
        if (payload.Length is < 1 or > MaxCatalogBytes)
            throw new InvalidDataException("Windows Link catalog response exceeds size bounds.");
        int offset = 0;
        while (offset < payload.Length)
        {
            int count = Math.Min(VideoChunk, payload.Length - offset);
            byte[] chunk = new byte[5 + count];
            chunk[0] = (byte)((offset == 0 ? 1 : 0) | (offset + count == payload.Length ? 2 : 0));
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(1), payload.Length);
            payload.AsSpan(offset, count).CopyTo(chunk.AsSpan(5));
            await SendAsync(LinkMessage.CatalogResponse, requestId, chunk, token).ConfigureAwait(false);
            offset += count;
        }
    }

    internal void NotifyCatalogChanged() => SendSignal(LinkMessage.CatalogChanged);
    // The original wire format has no probe ID. Keep a single outstanding ping for
    // the watchdog, discovery and the visible panel so a late reply cannot time a newer probe.
    internal void ProbeHealth() => StartPing();

    private Task<double?>? StartPing()
    {
        lock (_gate)
        {
            if (!IsOpen) return null;
            if (_ping != null) return _ping.Task;
            if (_control.Count >= 32) return null;
            _ping = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pingStarted = Stopwatch.GetTimestamp();
            _control.Enqueue((new LinkFrame(LinkMessage.Ping, Guid.Empty, []), null));
            SignalWriter();
            return _ping.Task;
        }
    }

    internal async Task<double?> MeasureRoundTripAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (StartPing() is not { } reply) return null;
        try { return await reply.WaitAsync(PingTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { return null; }
    }

    private void PongReceived()
    {
        lock (_gate)
        {
            if (_ping == null) return;
            var elapsed = Stopwatch.GetElapsedTime(_pingStarted);
            _ping.TrySetResult(elapsed <= PingTimeout ? elapsed.TotalMilliseconds : null);
            _ping = null;
        }
    }

    private void SendSignal(LinkMessage type)
    {
        lock (_gate)
        {
            if (!IsOpen || _control.Count >= 32) return;
            _control.Enqueue((new LinkFrame(type, Guid.Empty, []), null));
            SignalWriter();
        }
    }

    internal void SendAudio(Guid id, ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data.Length % 4 != 0) return;
        lock (_gate)
        {
            if (!IsOpen) return;
            long at = Stopwatch.GetTimestamp();
            while (!data.IsEmpty)
            {
                int count = Math.Min(data.Length, MaxAudioPacket);
                if (_audio.Count >= 24) _audio.Dequeue().Release();
                byte[] samples = ArrayPool<byte>.Shared.Rent(count);
                data[..count].CopyTo(samples);
                _audio.Enqueue(new AudioPacket(id, samples, count, at));
                data = data[count..];
            }
            SignalWriter();
        }
    }

    internal void SendVideo(Guid id, byte[] data, long pts, bool key, bool switchedToAvc = false)
    {
        if (data.Length == 0) return;
        lock (_gate)
        {
            if (!IsOpen) return;
            if (data.Length > VideoFrameLimit)
            {
                _waitForKey.Add(id);
                return;
            }
            if (_video.Count >= 2)
            {
                while (_video.TryDequeue(out var dropped)) _waitForKey.Add(dropped.Id);
            }
            if (_waitForKey.Contains(id) && !key) return;
            _waitForKey.Remove(id);
            _video.Enqueue(new Video(id, data, pts, key, switchedToAvc && Hello.SupportsNativeVideo));
            SignalWriter();
        }
    }

    internal void RetireVideo(Guid id)
    {
        lock (_gate) _waitForKey.Remove(id);
    }

    private void SignalWriter()
    {
        if (_ready.CurrentCount == 0) _ready.Release();
    }

    internal async Task RunAsync(Func<LinkMessage, Guid, ReadOnlyMemory<byte>, Task> dispatch)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxPayload);
        byte[] header = new byte[HeaderBytes];
        CancellationToken token = _closed.Token;
        Task writer = WriteLoopAsync(token);
        Task watchdog = WatchAsync(token);
        try
        {
            while (true)
            {
                await secure.Stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                var type = (LinkMessage)header[0];
                var id = new Guid(header.AsSpan(1, 16));
                int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(17));
                if (!ValidFrame(type, id, length)) throw new InvalidDataException("Invalid Windows Link frame.");
                await secure.Stream.ReadExactlyAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
                Interlocked.Add(ref _received, length + HeaderBytes);
                Volatile.Write(ref _lastRead, Stopwatch.GetTimestamp());
                if (type == LinkMessage.Ping) SendSignal(LinkMessage.Pong);
                else if (type == LinkMessage.Pong) PongReceived();
                else
                    await dispatch(type, id, buffer.AsMemory(0, length)).ConfigureAwait(false);
            }
        }
        finally
        {
            Abort();
            try { await Task.WhenAll(writer, watchdog).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            secure.Stream.Dispose();
            _closed.Dispose();
            _ready.Dispose();
        }
    }

    private async Task WatchAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastRead));
            if (elapsed >= TimeSpan.FromSeconds(24)) { Abort(); return; }
            if (elapsed >= TimeSpan.FromSeconds(8)) ProbeHealth();
        }
    }

    private static bool ValidFrame(LinkMessage type, Guid id, int length)
    {
        if (type == LinkMessage.ClipboardState) return length == 0;
        if (type == LinkMessage.ClipboardText) return id != Guid.Empty && length is > 16 and <= 16 + WindowsLinkService.MaxClipboardBytes;
        if (type is LinkMessage.Ping or LinkMessage.Pong or LinkMessage.Bye or LinkMessage.CatalogChanged)
            return id == Guid.Empty && length == 0;
        if (type == LinkMessage.TrustState) return id == Guid.Empty && length == 16;
        if (type == LinkMessage.TrustRevoke) return id == Guid.Empty && length == 0;
        if (id == Guid.Empty) return false;
        return type switch
        {
            LinkMessage.TrustBegin or LinkMessage.TrustCancel => length == 0,
            LinkMessage.TrustTry => length == 40,
            LinkMessage.TrustAnswer => length == 1,
            LinkMessage.Open => length is > 0 and <= 12 * 1024,
            LinkMessage.Close => length == 0,
            LinkMessage.Audio => length is >= 4 and <= MaxAudioPacket && length % 4 == 0,
            LinkMessage.Video => length > 9 && length <= VideoChunk + 9,
            LinkMessage.Volume => length == 5,
            LinkMessage.Input => length is >= 1 and <= 128,
            LinkMessage.CatalogRequest => length is >= 0 and <= 256,
            LinkMessage.CatalogResponse => length >= 5 && length <= VideoChunk + 5,
            LinkMessage.PullRequest or LinkMessage.PullResponse => length is > 0 and <= 1024,
            LinkMessage.AudioReady => length is >= 0 and <= 512,
            LinkMessage.SourcePlayback => length == 17,
            LinkMessage.SourcePlaybackReply => length is >= 18 and <= 530,
            LinkMessage.FileOffer or LinkMessage.FolderOffer => length is > 8 and <= 1032,
            LinkMessage.BrowseOpen or LinkMessage.BrowseClose or LinkMessage.BrowseCancel or LinkMessage.BrowsePreparing => length == 0,
            LinkMessage.BrowseList or LinkMessage.BrowseDownload => length is > 0 and <= VideoChunk,
            LinkMessage.BrowseReply => length is > 0 and <= MaxPayload,
            LinkMessage.FileChunk => length is > 0 and <= VideoChunk,
            LinkMessage.FileAck => length == 8,
            LinkMessage.FileAccept or LinkMessage.FileReject or LinkMessage.FileEnd or LinkMessage.FileComplete
                or LinkMessage.FileCancel or LinkMessage.FileError => length == 0,
            _ => false
        };
    }

    private async Task WriteLoopAsync(CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HeaderBytes + MaxPayload);
        Video? video = null;
        int offset = 0;
        int videoChunksSinceFile = 0;
        TaskCompletionSource? inFlight = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                LinkFrame? frame = null;
                int audioLength = 0;
                FileBatch? file = null;
                int fileOffset = 0, fileCount = 0;
                lock (_gate)
                {
                    if (_control.TryDequeue(out var message)) (frame, inFlight) = message;
                    else
                    {
                        while (_audio.TryDequeue(out var audio))
                        {
                            if (Stopwatch.GetElapsedTime(audio.At) <= TimeSpan.FromMilliseconds(180))
                            {
                                audioLength = audio.Length;
                                Header(buffer, LinkMessage.Audio, audio.Id, audioLength);
                                audio.Data.AsSpan(0, audioLength).CopyTo(buffer.AsSpan(HeaderBytes));
                                audio.Release();
                                break;
                            }
                            audio.Release();
                        }
                        if (audioLength == 0 && _fileBatch != null && (videoChunksSinceFile >= 4 || video == null && _video.Count == 0))
                        {
                            file = _fileBatch;
                            fileOffset = file.Offset;
                            fileCount = Math.Min(VideoChunk, file.Length - fileOffset);
                            file.Offset += fileCount;
                            if (file.Offset == file.Length)
                            {
                                inFlight = file.Written;
                                _fileBatch = null;
                            }
                            videoChunksSinceFile = 0;
                        }
                        if (audioLength == 0 && file == null && video == null && _video.TryDequeue(out video)) offset = 0;
                    }
                }
                int length;
                if (frame != null)
                {
                    length = frame.Payload.Length;
                    Header(buffer, frame.Type, frame.StreamId, length);
                    frame.Payload.CopyTo(buffer, HeaderBytes);
                }
                else if (audioLength > 0) length = audioLength;
                else if (file != null)
                {
                    length = fileCount;
                    Header(buffer, LinkMessage.FileChunk, file.Id, length);
                    file.Data.AsSpan(fileOffset, fileCount).CopyTo(buffer.AsSpan(HeaderBytes));
                }
                else if (video != null)
                {
                    videoChunksSinceFile = Math.Min(4, videoChunksSinceFile + 1);
                    int count = Math.Min(VideoChunk, video.Data.Length - offset);
                    length = count + 9;
                    Header(buffer, LinkMessage.Video, video.Id, length);
                    buffer[HeaderBytes] = (byte)((offset == 0 ? 1 : 0) | (offset + count == video.Data.Length ? 2 : 0)
                        | (video.Key ? 4 : 0) | (video.SwitchedToAvc ? 8 : 0));
                    BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(HeaderBytes + 1), video.Pts);
                    video.Data.AsSpan(offset, count).CopyTo(buffer.AsSpan(HeaderBytes + 9));
                    offset += count;
                    if (offset == video.Data.Length) video = null;
                }
                else
                {
                    await _ready.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }
                await secure.Stream.WriteAsync(buffer.AsMemory(0, HeaderBytes + length), token).ConfigureAwait(false);
                Interlocked.Add(ref _sent, HeaderBytes + length);
                inFlight?.TrySetResult();
                inFlight = null;
            }
        }
        finally
        {
            Abort();
            inFlight?.TrySetCanceled(token);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void Header(byte[] buffer, LinkMessage type, Guid id, int length)
    {
        buffer[0] = (byte)type;
        id.TryWriteBytes(buffer.AsSpan(1, 16));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(17), length);
    }

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
        _closed.Cancel();
        socket.Dispose();
        lock (_gate)
        {
            _ping?.TrySetResult(null);
            _ping = null;
            while (_control.TryDequeue(out var item)) item.Written?.TrySetCanceled();
            _fileBatch?.Written.TrySetCanceled();
            _fileBatch = null;
            while (_audio.TryDequeue(out var audio)) audio.Release();
            _video.Clear();
            _waitForKey.Clear();
        }
    }
}
