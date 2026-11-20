using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace SonicRoute.WindowsLink;

internal enum LinkFileState { Pending, Transferring, Completed, Rejected, Cancelled, Failed }
internal sealed record LinkFileOffer(Guid Id, string PeerId, string Name, long Length, bool IsDirectory = false);
internal sealed record LinkFileProgress(Guid Id, string PeerId, string Name, long Length, long BytesTransferred,
    bool Incoming, LinkFileState State)
{
    internal string? SavedPath { get; init; }
}

internal sealed partial class WindowsLinkService
{
    internal const long MaxFileBytes = 16L * 1024 * 1024 * 1024;
    private const int FileWindow = 8;
    private const int FileChunkSize = LinkConnection.VideoChunk;
    private static readonly TimeSpan FileOfferTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FileIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly UTF8Encoding FileNameEncoding = new(false, true);
    private readonly Dictionary<Guid, FileTransfer> _fileTransfers = [];

    public event Action<LinkFileOffer>? FileOffered;
    public event Action<LinkFileProgress>? FileTransferChanged;

    private sealed record FileDestination(string Path, bool IsDirectory);

    private sealed class FileTransfer(LinkConnection link, LinkFileOffer offer, bool incoming)
    {
        internal readonly LinkConnection Link = link;
        internal readonly LinkFileOffer Offer = offer;
        internal readonly bool Incoming = incoming;
        internal readonly object Gate = new();
        internal readonly CancellationTokenSource Stop = new(FileOfferTimeout);
        internal readonly TaskCompletionSource<FileDestination> Destination = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<LinkFileState> Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Channel<byte[]> Chunks = Channel.CreateBounded<byte[]>(FileWindow);
        internal readonly Channel<long> Acks = Channel.CreateBounded<long>(1);
        internal LinkFileState? StopReason;
        internal Exception? Error;
        internal bool NotifyStop = true;
        internal volatile bool Receiving, EndSent, Sealed;
        internal bool AcceptedByTrust;
        internal string? SavedPath;
        internal long Received, Bytes, LastProgress;

        internal void Abort(LinkFileState reason, bool notify, Exception? error = null)
        {
            lock (Gate)
            {
                if (Sealed || StopReason.HasValue) return;
                StopReason = reason;
                NotifyStop = notify;
                Error = error;
                Stop.Cancel();
            }
        }

        internal async Task CheckResultAsync()
        {
            switch (await Ended.Task.ConfigureAwait(false))
            {
                case LinkFileState.Completed: return;
                case LinkFileState.Cancelled: throw new OperationCanceledException("The file transfer was cancelled.");
                case LinkFileState.Rejected: throw new IOException("The receiving computer declined the file.");
                default: throw new IOException(Error?.Message ?? "The file transfer failed.", Error);
            }
        }
    }

    public Task SendFileAsync(string peerId, string sourcePath, CancellationToken token = default) =>
        SendPathCoreAsync(peerId, sourcePath, token, null, null, allowFolder: false);

    internal Task SendPathAsync(string peerId, string sourcePath, CancellationToken token = default, Guid? requestedId = null) =>
        SendPathCoreAsync(peerId, sourcePath, token, requestedId, null, allowFolder: true);

    private Task SendPathOnLinkAsync(LinkConnection expectedLink, string sourcePath, CancellationToken token, Guid requestedId) =>
        SendPathCoreAsync(expectedLink.Id, sourcePath, token, requestedId, expectedLink, allowFolder: true);

    private Task SendPathCoreAsync(string peerId, string sourcePath, CancellationToken token, Guid? requestedId,
        LinkConnection? expectedLink, bool allowFolder) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourcePath)) throw new ArgumentException("Select a full path.", nameof(sourcePath));
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        string name = Path.GetFileName(path);
        if (!IsSafeTransferName(name)) throw new IOException("Select a file or a named folder.");
        bool directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        LinkConnection link;
        lock (_gate)
        {
            link = Connected(peerId, LinkCapabilities.FileTransfer);
            if (expectedLink != null && link != expectedLink) throw new IOException("The connection changed.");
            if (directory && (!allowFolder || !link.Hello.SupportsFileBrowse))
                throw new IOException("Folder transfers require an updated Windows Link on both computers.");
        }
        if (expectedLink != null) LinkFolderArchive.RejectLinks(path);
        var archive = directory ? LinkFolderArchive.Inspect(path, token) : null;
        await using var source = directory ? null : LinkFolderArchive.OpenSource(path, expectedLink != null);
        long length = archive?.Length ?? source!.Length;
        if (length > MaxFileBytes) throw new IOException("The Windows Link transfer size limit is 16 GiB.");
        FileTransfer file;
        lock (_gate)
        {
            var offer = new LinkFileOffer(requestedId ?? Guid.NewGuid(), link.Id, name, length, directory);
            file = ReserveFileLocked(link, offer, false)
                ?? throw new InvalidOperationException("This connection is unavailable or already transferring a file.");
        }
        using var registration = token.Register(() => file.Abort(LinkFileState.Cancelled, true));
        await RunFileAsync(file, () => SendFileCoreAsync(file, source, archive)).ConfigureAwait(false);
        await file.CheckResultAsync().ConfigureAwait(false);
    }, token);

    public Task ReceiveFileAsync(Guid id, string destinationPath, CancellationToken token = default) =>
        AcceptFileAsync(id, new(destinationPath, false), token);

    internal Task ReceiveFileInDefaultDirectoryAsync(Guid id, CancellationToken token = default) =>
        AcceptFileAsync(id, new(GetReceiveDirectory(), true), token);

    internal Task ReceiveFileInDirectoryAsync(Guid id, string parent, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(parent)) throw new IOException("Choose a destination folder.");
        return AcceptFileAsync(id, new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), true), token);
    }

    private async Task AcceptFileAsync(Guid id, FileDestination destination, CancellationToken token)
    {
        FileTransfer file;
        lock (_gate)
        {
            if (!_fileTransfers.TryGetValue(id, out file!) || !file.Incoming)
                throw new InvalidOperationException("The file offer has expired.");
        }
        using var registration = token.Register(() => file.Abort(LinkFileState.Cancelled, true));
        if (!file.Destination.TrySetResult(destination))
            throw new InvalidOperationException("This file offer already has a destination.");
        await file.CheckResultAsync().ConfigureAwait(false);
    }

    public void RejectFile(Guid id)
    {
        FileTransfer? file;
        lock (_gate) file = _fileTransfers.GetValueOrDefault(id);
        if (file is { Incoming: true } && !file.Destination.Task.IsCompleted) file.Abort(LinkFileState.Rejected, true);
    }

    public void CancelFile(Guid id)
    {
        FileTransfer? file;
        lock (_gate) file = _fileTransfers.GetValueOrDefault(id);
        file?.Abort(LinkFileState.Cancelled, true);
    }

    private FileTransfer? ReserveFileLocked(LinkConnection link, LinkFileOffer offer, bool incoming)
    {
        if (!link.IsOpen || _connections.GetValueOrDefault(link.Id) != link || !_saved.ContainsKey(link.Id)
            || _fileTransfers.Count >= MaximumPeers || _fileTransfers.ContainsKey(offer.Id)
            || _fileTransfers.Values.Any(file => !file.Sealed && file.Offer.PeerId == link.Id)) return null;
        var file = new FileTransfer(link, offer, incoming);
        _fileTransfers.Add(offer.Id, file);
        return file;
    }

    private async Task RunFileAsync(FileTransfer file, Func<Task> work)
    {
        LinkFileState final = LinkFileState.Completed;
        PublishFileProgress(file, LinkFileState.Pending);
        try { await work().ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (!file.Incoming) file.Link.RetireFile(file.Offer.Id);
            lock (file.Gate)
            {
                final = file.StopReason ?? (ex is OperationCanceledException ? LinkFileState.Cancelled : LinkFileState.Failed);
                file.Error ??= ex;
                file.Sealed = true;
            }
            if (file.NotifyStop)
            {
                var signal = final == LinkFileState.Rejected ? LinkMessage.FileReject
                    : final == LinkFileState.Cancelled ? LinkMessage.FileCancel : LinkMessage.FileError;
                await SendFileSignalAsync(file.Link, file.Offer.Id, signal).ConfigureAwait(false);
            }
        }
        finally
        {
            file.Chunks.Writer.TryComplete();
            while (file.Chunks.Reader.TryRead(out byte[]? unused)) ArrayPool<byte>.Shared.Return(unused);
            lock (file.Gate) { file.Sealed = true; file.Stop.Dispose(); }
            lock (_gate) _fileTransfers.Remove(file.Offer.Id);
            PublishFileProgress(file, final);
            if (file.Incoming) RequestedFileEnded(file.Link, file.Offer.Id, final);
            file.Ended.TrySetResult(final);
        }
    }

    private async Task SendFileCoreAsync(FileTransfer file, Stream? source, LinkFolderArchive? folder)
    {
        var token = file.Stop.Token;
        token.ThrowIfCancellationRequested();
        byte[] offer = new byte[8 + FileNameEncoding.GetByteCount(file.Offer.Name)];
        BinaryPrimitives.WriteInt64LittleEndian(offer, file.Offer.Length);
        FileNameEncoding.GetBytes(file.Offer.Name, offer.AsSpan(8));
        await file.Link.SendAsync(file.Offer.IsDirectory ? LinkMessage.FolderOffer : LinkMessage.FileOffer, file.Offer.Id, offer, token).ConfigureAwait(false);
        await file.Accepted.Task.WaitAsync(token).ConfigureAwait(false);
        file.Stop.CancelAfter(FileIdleTimeout);
        PublishFileProgress(file, LinkFileState.Transferring);
        if (folder == null) await SendFileBytesAsync(file, source!, token).ConfigureAwait(false);
        else await LinkFolderArchive.StreamBetweenAsync(folder.WriteAsync,
            (input, ct) => SendFileBytesAsync(file, input, ct), token).ConfigureAwait(false);
        file.EndSent = true;
        await file.Link.SendAsync(LinkMessage.FileEnd, file.Offer.Id, [], token).ConfigureAwait(false);
        await file.Committed.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task SendFileBytesAsync(FileTransfer file, Stream source, CancellationToken token)
    {
        long sent = 0;
        const int batchSize = 4 * FileChunkSize;
        byte[] batch = new byte[(int)Math.Min(batchSize, file.Offer.Length)];
        while (sent < file.Offer.Length)
        {
            long windowEnd = Math.Min(sent + FileWindow * FileChunkSize, file.Offer.Length);
            while (sent < windowEnd)
            {
                int count = (int)Math.Min(batch.Length, windowEnd - sent);
                await source.ReadExactlyAsync(batch.AsMemory(0, count), token).ConfigureAwait(false);
                await file.Link.SendFileBatchAsync(file.Offer.Id, batch, count, token).ConfigureAwait(false);
                sent += count;
            }
            if (await file.Acks.Reader.ReadAsync(token).ConfigureAwait(false) != sent)
                throw new InvalidDataException("Invalid file acknowledgement.");
            file.Stop.CancelAfter(FileIdleTimeout);
            ReportFileBytes(file, sent);
        }
    }

    private async Task ReceiveFileCoreAsync(FileTransfer file)
    {
        var token = file.Stop.Token;
        token.ThrowIfCancellationRequested();
        bool requested = TryAcceptRequestedFile(file.Link, file.Offer, file.Offer.IsDirectory);
        lock (_gate) file.AcceptedByTrust = !requested && IsFullyTrusted(file.Link);
        if (requested || file.AcceptedByTrust)
            file.Destination.TrySetResult(new(GetReceiveDirectory(), true));
        else if (FileOffered is not { } handler)
        {
            file.Abort(LinkFileState.Rejected, true);
            token.ThrowIfCancellationRequested();
        }
        else handler(file.Offer);
        var destination = await file.Destination.Task.WaitAsync(token).ConfigureAwait(false);
        string path = destination.IsDirectory ? Path.Combine(destination.Path, file.Offer.Name) : destination.Path;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal) || !IsSafeTransferName(Path.GetFileName(path)))
            throw new IOException("Choose a new local file name.");
        path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(path)!;
        if (destination.IsDirectory)
        {
            if (!string.Equals(Path.TrimEndingDirectorySeparator(directory), destination.Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The offered name is outside the receive folder.");
            Directory.CreateDirectory(directory);
        }
        else if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The destination already exists.");
        string temp = Path.Combine(directory, $".sonicroute-{Guid.NewGuid():N}.partial");
        bool ownsTemp = false;
        try
        {
            async Task ReceiveBytes(Stream output, CancellationToken ct)
            {
                file.Stop.CancelAfter(FileIdleTimeout);
                file.Receiving = true;
                await file.Link.SendAsync(LinkMessage.FileAccept, file.Offer.Id, [], ct).ConfigureAwait(false);
                await ReceiveFileBytesAsync(file, output, ct).ConfigureAwait(false);
            }
            if (file.Offer.IsDirectory)
            {
                Directory.CreateDirectory(temp);
                ownsTemp = true;
                await LinkFolderArchive.StreamBetweenAsync(ReceiveBytes,
                    (input, ct) => LinkFolderArchive.ExtractAsync(input, temp, file.Offer.Length, ct), token).ConfigureAwait(false);
            }
            else
            {
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                ownsTemp = true;
                await ReceiveBytes(output, token).ConfigureAwait(false);
            }
            if (file.AcceptedByTrust && !IsFullyTrusted(file.Link)) file.Abort(LinkFileState.Cancelled, true);
            lock (file.Gate)
            {
                token.ThrowIfCancellationRequested();
                if (destination.IsDirectory)
                    path = CommitToReceiveDirectory(temp, path, token, file.Offer.IsDirectory);
                else if (file.Offer.IsDirectory) Directory.Move(temp, path);
                else File.Move(temp, path, overwrite: false);
                file.SavedPath = path;
                ownsTemp = false;
                file.Sealed = true;
            }
            await SendFileSignalAsync(file.Link, file.Offer.Id, LinkMessage.FileComplete).ConfigureAwait(false);
        }
        finally
        {
            if (ownsTemp)
            {
                if (file.Offer.IsDirectory) Directory.Delete(temp, recursive: true);
                else File.Delete(temp);
            }
        }
    }

    private async Task ReceiveFileBytesAsync(FileTransfer file, Stream output, CancellationToken token)
    {
        long written = 0;
        int window = 0;
        await foreach (var chunk in file.Chunks.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            int count = (int)Math.Min(FileChunkSize, file.Offer.Length - written);
            try { await output.WriteAsync(chunk.AsMemory(0, count), token).ConfigureAwait(false); }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
            written += count;
            window++;
            if (window == FileWindow || written == file.Offer.Length)
            {
                file.Stop.CancelAfter(FileIdleTimeout);
                byte[] ack = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(ack, written);
                await file.Link.SendAsync(LinkMessage.FileAck, file.Offer.Id, ack, token).ConfigureAwait(false);
                ReportFileBytes(file, written);
                window = 0;
            }
        }
        if (written != file.Offer.Length) throw new EndOfStreamException("The file ended before its advertised size.");
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    internal static string CommitToReceiveDirectory(string temp, string path, CancellationToken token, bool folder = false)
    {
        string directory = Path.GetDirectoryName(path)!;
        string stem = folder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        string extension = folder ? "" : Path.GetExtension(path);
        string candidate = path;
        for (int index = 1; ; index++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (folder) Directory.Move(temp, candidate);
                else File.Move(temp, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 80 or 183 || Directory.Exists(candidate) || File.Exists(candidate)) { }
            string suffix = $" ({index})";
            // File components are limited to 255 UTF-16 code units, including the suffix.
            string ext = TrimFilePart(extension, 255 - suffix.Length);
            string prefix = TrimFilePart(stem, 255 - suffix.Length - ext.Length);
            candidate = Path.Combine(directory, prefix + suffix + ext);
        }
    }

    private static string TrimFilePart(string value, int limit)
    {
        if (value.Length <= limit) return value;
        if (limit > 0 && char.IsHighSurrogate(value[limit - 1])) limit--;
        return value[..limit];
    }

    private void RevokeTrustedFileReceives(string peerId)
    {
        FileTransfer[] incoming;
        lock (_gate) incoming = _fileTransfers.Values.Where(f => f.AcceptedByTrust
            && string.Equals(f.Link.Id, peerId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var file in incoming) file.Abort(LinkFileState.Cancelled, true);
    }

    internal static bool IsSafeTransferName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(c => char.IsControl(c)
                || c is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069')) return false;
        string device = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT"))
                && "0123456789¹²³".Contains(device[3]));
    }

    private void DispatchFile(LinkConnection link, LinkMessage type, Guid id, ReadOnlyMemory<byte> body)
    {
        if ((link.Hello.Capabilities & LinkCapabilities.FileTransfer) == 0) return;
        if (type is LinkMessage.FileOffer or LinkMessage.FolderOffer)
        {
            bool directory = type == LinkMessage.FolderOffer;
            if (directory && !link.Hello.SupportsFileBrowse) { _ = SendFileSignalAsync(link, id, LinkMessage.FileReject); return; }
            long length = BinaryPrimitives.ReadInt64LittleEndian(body.Span);
            string name;
            try { name = FileNameEncoding.GetString(body.Span[8..]); }
            catch (DecoderFallbackException) { name = ""; }
            FileTransfer? incoming = null;
            lock (_gate)
            {
                if (_fileTransfers.ContainsKey(id)) return;
                if (length is >= 0 and <= MaxFileBytes && IsSafeTransferName(name))
                    incoming = ReserveFileLocked(link, new LinkFileOffer(id, link.Id, name, length, directory), true);
            }
            if (incoming == null) _ = SendFileSignalAsync(link, id, LinkMessage.FileReject);
            else _ = Task.Run(() => RunFileAsync(incoming, () => ReceiveFileCoreAsync(incoming)));
            return;
        }

        FileTransfer? file;
        lock (_gate) file = _fileTransfers.GetValueOrDefault(id);
        if (file == null || file.Link != link) return;
        switch (type)
        {
            case LinkMessage.FileAccept when !file.Incoming:
                file.Accepted.TrySetResult();
                return;
            case LinkMessage.FileReject when !file.Incoming:
                file.Abort(LinkFileState.Rejected, false);
                return;
            case LinkMessage.FileCancel:
                file.Abort(LinkFileState.Cancelled, false);
                return;
            case LinkMessage.FileError:
                file.Abort(LinkFileState.Failed, false, new IOException("The other computer could not complete the file transfer."));
                return;
            case LinkMessage.FileAck when !file.Incoming:
                if (file.Acks.Writer.TryWrite(BinaryPrimitives.ReadInt64LittleEndian(body.Span))) return;
                break;
            case LinkMessage.FileComplete when !file.Incoming && file.EndSent:
                file.Committed.TrySetResult();
                return;
            case LinkMessage.FileChunk when file.Incoming && file.Receiving:
                if (body.Length == Math.Min(FileChunkSize, file.Offer.Length - file.Received))
                {
                    byte[] chunk = ArrayPool<byte>.Shared.Rent(body.Length);
                    body.CopyTo(chunk);
                    if (file.Chunks.Writer.TryWrite(chunk))
                    {
                        file.Received += body.Length;
                        return;
                    }
                    ArrayPool<byte>.Shared.Return(chunk);
                }
                break;
            case LinkMessage.FileEnd when file.Incoming && file.Receiving && file.Received == file.Offer.Length:
                file.Chunks.Writer.TryComplete();
                return;
        }
        file.Abort(LinkFileState.Failed, true, new InvalidDataException("Invalid Windows Link file sequence."));
    }

    private static async Task SendFileSignalAsync(LinkConnection link, Guid id, LinkMessage type)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await link.SendAsync(type, id, [], deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void ReportFileBytes(FileTransfer file, long bytes)
    {
        file.Bytes = bytes;
        if (file.LastProgress != 0 && Stopwatch.GetElapsedTime(file.LastProgress) < TimeSpan.FromMilliseconds(200)) return;
        file.LastProgress = Stopwatch.GetTimestamp();
        PublishFileProgress(file, LinkFileState.Transferring);
    }

    private void PublishFileProgress(FileTransfer file, LinkFileState state)
    {
        var offer = file.Offer;
        try { FileTransferChanged?.Invoke(new(offer.Id, offer.PeerId, offer.Name, offer.Length, file.Bytes, file.Incoming, state) { SavedPath = file.SavedPath }); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    private async Task CloseConnectionFilesAsync(LinkConnection link)
    {
        FileTransfer[] files;
        lock (_gate) files = _fileTransfers.Values.Where(file => file.Link == link).ToArray();
        foreach (var file in files) file.Abort(LinkFileState.Failed, false, new IOException("The sending or receiving computer disconnected."));
        await Task.WhenAll(files.Select(file => file.Ended.Task)).ConfigureAwait(false);
    }
}
