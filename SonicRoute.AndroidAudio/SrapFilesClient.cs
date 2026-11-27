using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SonicRoute.AndroidAudio;

internal sealed class SrapFilesClient(SrapSession session, Action? uploadAccepted = null) : IPhoneFileOperations
{
    private const int Chunk = 32768;
    private const long MaxFile = 512L * 1024 * 1024;
    internal const string Prefix = "/shared-";
    internal static string Root(SrapSession session) => Prefix + Grant(session);

    private static string Grant(SrapSession session)
    {
        if (!session.Capabilities.Entries.TryGetValue("files.transfer", out var cap) || cap.State != "available"
            || cap.Scope != "saf_tree" || cap.Limits?.TryGetValue("grant", out var version) != true
            || !long.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out long generation) || generation < 0) throw new SrapException("permission_lost");
        return version;
    }

    private string[] Parent(string folder)
    {
        string root = Root(session);
        if (folder == root) return [];
        if (!folder.StartsWith(root + "/", StringComparison.Ordinal)) throw new SrapException("stale_session");
        string[] parts = folder[(root.Length + 1)..].Split('/');
        if (parts.Length > 16 || parts.Any(n => !AndroidFileTransfer.IsSafeTransferName(n))) throw new SrapException("invalid_argument");
        return parts;
    }

    private void RequireWrite()
    {
        if (!session.Capabilities.Entries.TryGetValue("files.transfer", out var entry) ||
            entry is not { State: "available", Scope: "saf_tree" } || entry.Limits?.GetValueOrDefault("write") != "true")
            throw new SrapException("permission_lost");
    }

    private static JsonArray Names(string[] parts) => new(parts.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    private static long Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var prop) && prop.TryGetInt64(out long result) ? result : throw new SrapException("invalid_argument");

    public async Task<List<AndroidStorageFile>> ListAsync(string folder, CancellationToken token)
    {
        var parent = Parent(folder);
        var list = new List<AndroidStorageFile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pages = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            Parent(folder);
            var p = new JsonObject { ["parent"] = Names(parent) };
            if (cursor != null) p["cursor"] = cursor;
            var reply = await session.RequestAsync("file.list", "files.transfer", p, token).ConfigureAwait(false);
            if (!reply.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() > 24 || list.Count + entries.GetArrayLength() > 4096)
                throw new SrapException("invalid_argument");
            foreach (var entry in entries.EnumerateArray())
            {
                var name = SrapClient.Text(entry, "name");
                if (name == null || !AndroidFileTransfer.IsSafeTransferName(name) || !names.Add(name) ||
                    !entry.TryGetProperty("directory", out var dir) || dir.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new SrapException("invalid_argument");
                list.Add(new(name, dir.GetBoolean()) {
                    Size = entry.TryGetProperty("size", out var size) && size.TryGetUInt64(out ulong bytes) ? bytes : null,
                    Modified = Number(entry, "modified") / 1000
                });
            }
            cursor = SrapClient.Text(reply, "cursor");
            if (cursor != null && (entries.GetArrayLength() == 0 || !Guid.TryParseExact(cursor, "D", out _) || !pages.Add(cursor)))
                throw new SrapException("invalid_argument");
        } while (cursor != null);
        Parent(folder);
        return list;
    }

    public async Task<string> SendAsync(string path, string destination, CancellationToken token)
    {
        var parent = Parent(destination);
        RequireWrite();
        var name = Path.GetFileName(path);
        if (!Path.IsPathFullyQualified(path) || !AndroidFileTransfer.IsSafeTransferName(name)) throw new SrapException("invalid_argument");
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, true);
        long size = source.Length;
        if (size > MaxFile) throw new SrapException("unsupported");
        string digest = Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
        source.Position = 0;
        Parent(destination);
        RequireWrite();
        uint id = session.AllocateStreamId();
        bool complete = false;
        try
        {
            var opened = await session.RequestAsync("file.offer", "files.transfer", new() { ["parent"] = Names(parent),
                ["name"] = name, ["size"] = size, ["streamId"] = id, ["sha256"] = digest }, token).ConfigureAwait(false);
            CheckOpen(opened, id, size);
            uploadAccepted?.Invoke();
            var buffer = new byte[Chunk];
            for (long offset = 0; offset < size;)
            {
                Parent(destination);
                int count = (int)Math.Min(size - offset, Chunk);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                RequireWrite();
                Parent(destination);
                await session.WriteDataFrameAsync(id, buffer.AsMemory(0, count), token).ConfigureAwait(false);
                await Acknowledge(id, offset, count, token).ConfigureAwait(false);
                offset += count;
            }
            var receipt = await session.RequestAsync("file.finish", "files.transfer", new() { ["streamId"] = id }, token).ConfigureAwait(false);
            CheckReceipt(receipt, name, size, digest);
            complete = true;
            return name;
        }
        finally { if (!complete) await Cancel(id).ConfigureAwait(false); }
    }

    public async Task<string> ReceiveAsync(string folder, string name, string destination, CancellationToken token)
    {
        var parent = Parent(folder);
        if (!AndroidFileTransfer.IsSafeTransferName(name) || !Path.IsPathFullyQualified(destination))
            throw new SrapException("invalid_argument");
        Directory.CreateDirectory(destination);
        uint id = session.AllocateStreamId();
        var temp = Path.Combine(destination, ".sonicroute-" + Guid.NewGuid().ToString("N") + ".part");
        bool created = false, complete = false;
        try
        {
            var opened = await session.RequestAsync("file.open", "files.transfer", new() { ["parent"] = Names(parent),
                ["name"] = name, ["streamId"] = id }, token).ConfigureAwait(false);
            long size = Number(opened, "size");
            CheckOpen(opened, id, size);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, Chunk, true))
            {
                created = true;
                for (long offset = 0; offset < size;)
                {
                    Parent(folder);
                    int count = (int)Math.Min(size - offset, Chunk);
                    byte[]? packet = null;
                    using (session.ExpectData(id, frame =>
                    {
                        if (frame.Length != count || Interlocked.CompareExchange(ref packet, frame.ToArray(), null) != null)
                            throw new SrapException("invalid_argument");
                    }))
                    {
                        try { await Acknowledge(id, offset, count, token).ConfigureAwait(false); }
                        catch (OperationCanceledException)
                        {
                            if (Volatile.Read(ref packet) == null && !session.Token.IsCancellationRequested)
                                session.DiscardPendingFileChunk(id, count);
                            throw;
                        }
                    }
                    var data = Volatile.Read(ref packet) ?? throw new SrapException("invalid_argument");
                    digest.AppendData(data);
                    await output.WriteAsync(data, token).ConfigureAwait(false);
                    offset += count;
                }
            }
            var receipt = await session.RequestAsync("file.finish", "files.transfer", new() { ["streamId"] = id }, token).ConfigureAwait(false);
            CheckReceipt(receipt, name, size, Convert.ToHexString(digest.GetHashAndReset()));
            Parent(folder);
            var saved = AndroidFileTransfer.CommitToReceiveDirectory(temp, Path.Combine(destination, name), token);
            complete = true;
            return saved;
        }
        finally
        {
            if (!complete) await Cancel(id).ConfigureAwait(false);
            if (created && !complete) File.Delete(temp);
        }
    }

    private async Task Acknowledge(uint id, long offset, int count, CancellationToken token)
    {
        var r = await session.RequestAsync("file.chunk", "files.transfer", new() {
            ["streamId"] = id, ["offset"] = offset, ["count"] = count }, token).ConfigureAwait(false);
        if (Number(r, "offset") != offset || Number(r, "count") != count) throw new SrapException("invalid_argument");
    }

    private static void CheckOpen(JsonElement r, uint id, long size)
    {
        if (size is < 0 or > MaxFile || Number(r, "size") != size || Number(r, "streamId") != id) throw new SrapException("invalid_argument");
    }

    private static void CheckReceipt(JsonElement r, string name, long size, string hash)
    {
        if (Number(r, "size") != size || SrapClient.Text(r, "name") != name ||
            !string.Equals(SrapClient.Text(r, "sha256"), hash, StringComparison.OrdinalIgnoreCase)) throw new SrapException("io_failure");
    }

    private async Task Cancel(uint id)
    {
        if (session.Token.IsCancellationRequested) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await session.StopTrackAsync(id, timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { session.Dispose(); }
    }
}
