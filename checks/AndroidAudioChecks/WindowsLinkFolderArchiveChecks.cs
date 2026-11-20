using System.Buffers.Binary;
using System.Text;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task CheckFolderArchiveAsync(string root, CancellationToken token)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "folder-source")).FullName;
        string empty = Path.Combine("嵌套", "空文件夹");
        Directory.CreateDirectory(Path.Combine(source, empty));
        byte[] content = new byte[187213];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(Path.Combine(source, "完全匹配.bin"), content, token);
        File.WriteAllText(Path.Combine(source, "嵌套", "empty.txt"), "");
        string destination = Directory.CreateDirectory(Path.Combine(root, "folder-output")).FullName;
        var archive = LinkFolderArchive.Inspect(source, token);
        await LinkFolderArchive.StreamBetweenAsync(archive.WriteAsync,
            (stream, ct) => LinkFolderArchive.ExtractAsync(stream, destination, archive.Length, ct), token);
        Check(Directory.Exists(Path.Combine(destination, empty)) && File.Exists(Path.Combine(destination, "嵌套", "empty.txt"))
            && (await File.ReadAllBytesAsync(Path.Combine(destination, "完全匹配.bin"), token)).SequenceEqual(content),
            "a bounded folder stream preserves Unicode names, empty subfolders, empty files and multi-window binary data");

        static byte[] Entry(string name, long declaredLength)
        {
            byte[] path = Encoding.UTF8.GetBytes(name);
            byte[] header = new byte[12 + path.Length];
            BinaryPrimitives.WriteInt32LittleEndian(header, path.Length);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(4), declaredLength);
            path.CopyTo(header, 12);
            return header;
        }
        async Task<bool> Rejected(byte[] bytes)
        {
            try
            {
                await LinkFolderArchive.ExtractAsync(new MemoryStream(bytes, writable: false), destination, bytes.Length, token);
                return false;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { return true; }
        }
        Check(await Rejected(Entry("../not-a-child", 0)) && await Rejected(Entry("CON", 0)) &&
            await Rejected(Entry("incomplete.bin", long.MaxValue)) && await Rejected(Entry("嵌套", -1)),
            "invalid names, impossible sizes and duplicate folder records fail before publication");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool cancelled = false;
        try
        {
            await LinkFolderArchive.StreamBetweenAsync(archive.WriteAsync, async (input, ct) =>
            {
                byte[] one = new byte[1];
                await input.ReadExactlyAsync(one, ct);
                cancel.Cancel();
                ct.ThrowIfCancellationRequested();
            }, cancel.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancelling the reader releases a backpressured directory sender");
    }
}
