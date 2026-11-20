using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SonicRoute.WindowsLink;

// A directory offer carries a sequence of relative UTF-8 names and uncompressed file contents.
// Each 12-byte header contains the name length and content length (-1 for an empty or nonempty directory).
internal sealed class LinkFolderArchive
{
    private const int HeaderSize = 12, MaxNameBytes = 16 * 1024, MaxEntries = 65536, MaxMetadata = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Entry(string Relative, long Size);
    private readonly List<Entry> _entries = [];
    private readonly string _root;
    internal long Length { get; private set; }

    private LinkFolderArchive(string root) => _root = root;

    internal static LinkFolderArchive Inspect(string path, CancellationToken token)
    {
        var result = new LinkFolderArchive(Path.GetFullPath(path));
        int metadata = 0;
        var folders = new Stack<string>();
        folders.Push(result._root);
        while (folders.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            RejectLinks(current);
            foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A folder transfer cannot include a symbolic link or junction: " + item.Name);
                var relative = Path.GetRelativePath(result._root, item.FullName).Replace('\\', '/');
                int bytes = Utf8.GetByteCount(relative);
                if (!WindowsLinkService.IsSafeTransferName(item.Name) || bytes > MaxNameBytes)
                    throw new IOException("The folder contains a name that cannot be transferred: " + item.Name);
                long size = item is FileInfo file ? file.Length : -1;
                metadata += HeaderSize + bytes;
                long entryLength = checked(HeaderSize + bytes + Math.Max(0, size));
                if (result._entries.Count == MaxEntries || metadata > MaxMetadata || entryLength > WindowsLinkService.MaxFileBytes - result.Length)
                    throw new IOException("This folder exceeds the Windows Link transfer limit.");
                result.Length += entryLength;
                result._entries.Add(new(relative, size));
                if (size < 0) folders.Push(item.FullName);
            }
        }
        return result;
    }

    internal static void RejectLinks(string path, string? stopAt = null)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked files and folders cannot be transferred: " + Path.GetFileName(current));
            if (string.Equals(current, stopAt, StringComparison.OrdinalIgnoreCase)) return;
        }
    }

    internal static FileStream OpenSource(string path, bool checkLocation)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!checkLocation) return stream;
        try
        {
            // Check the opened handle so replacing a parent with a junction cannot redirect a read.
            var buffer = new StringBuilder(512);
            uint count = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            if (count >= buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)count + 1));
                count = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
            }
            string final = buffer.ToString();
            string expected = Path.GetFullPath(path);
            if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final[8..];
            else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
            if (count == 0 || !string.Equals(final, expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected file location changed.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);

    internal async Task WriteAsync(Stream output, CancellationToken token)
    {
        byte[] header = new byte[HeaderSize];
        foreach (var entry in _entries)
        {
            token.ThrowIfCancellationRequested();
            byte[] name = Utf8.GetBytes(entry.Relative);
            BinaryPrimitives.WriteInt32LittleEndian(header, name.Length);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(4), entry.Size);
            await output.WriteAsync(header, token).ConfigureAwait(false);
            await output.WriteAsync(name, token).ConfigureAwait(false);
            if (entry.Size < 0) continue;
            string path = Path.Combine(_root, entry.Relative.Replace('/', Path.DirectorySeparatorChar));
            await using var file = OpenSource(path, true);
            if (file.Length != entry.Size) throw new IOException("The selected folder changed while sending.");
            await file.CopyToAsync(output, 64 * 1024, token).ConfigureAwait(false);
        }
    }

    internal static async Task ExtractAsync(Stream input, string root, long remaining, CancellationToken token)
    {
        byte[] header = new byte[HeaderSize];
        byte[] data = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int metadata = 0, entries = 0;
        try
        {
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaxEntries || remaining < HeaderSize) throw new InvalidDataException("Invalid folder entry.");
                await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
                int bytes = BinaryPrimitives.ReadInt32LittleEndian(header);
                long size = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(4));
                remaining -= HeaderSize;
                if (bytes is <= 0 or > MaxNameBytes || bytes > remaining || size < -1 || size > remaining - bytes ||
                    (metadata += HeaderSize + bytes) > MaxMetadata) throw new InvalidDataException("Invalid folder entry size.");
                await input.ReadExactlyAsync(data.AsMemory(0, bytes), token).ConfigureAwait(false);
                string relative = Utf8.GetString(data, 0, bytes);
                if (relative.Split('/').Any(part => !WindowsLinkService.IsSafeTransferName(part)))
                    throw new InvalidDataException("Invalid entry in the incoming folder.");
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                string parent = Path.GetDirectoryName(path)!;
                // Only accept children of existing directories in our fresh staging tree.
                RejectLinks(parent, root);
                if (size == -1)
                {
                    if (Directory.Exists(path) || File.Exists(path)) throw new InvalidDataException("Duplicate folder entry.");
                    Directory.CreateDirectory(path);
                }
                else
                {
                    await using var dest = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    for (long left = size; left > 0;)
                    {
                        int n = (int)Math.Min(left, data.Length);
                        await input.ReadExactlyAsync(data.AsMemory(0, n), token).ConfigureAwait(false);
                        await dest.WriteAsync(data.AsMemory(0, n), token).ConfigureAwait(false);
                        left -= n;
                    }
                }
                remaining -= bytes + Math.Max(size, 0);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(data); }
    }

    internal static async Task StreamBetweenAsync(Func<Stream, CancellationToken, Task> produce,
        Func<Stream, CancellationToken, Task> consume, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var pipe = new Pipe();
        async Task Produce()
        {
            Exception? failure = null;
            try { await produce(pipe.Writer.AsStream(leaveOpen: true), stop.Token).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; stop.Cancel(); throw; }
            finally { await pipe.Writer.CompleteAsync(failure).ConfigureAwait(false); }
        }
        async Task Consume()
        {
            Exception? failure = null;
            try { await consume(pipe.Reader.AsStream(leaveOpen: true), stop.Token).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; stop.Cancel(); throw; }
            finally { await pipe.Reader.CompleteAsync(failure).ConfigureAwait(false); }
        }
        await Task.WhenAll(Produce(), Consume()).ConfigureAwait(false);
    }
}
