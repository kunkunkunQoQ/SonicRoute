using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonicRoute.AndroidAudio;

/// <summary>Uncompressed embedded tools. Prepare each component once, only when requested.</summary>
public sealed class AndroidRuntimeBundle
{
    private sealed record RuntimeFile(string Path, string Group, long Bytes, string SHA256);
    private static readonly Assembly Assembly = typeof(AndroidRuntimeBundle).Assembly;
    private static readonly Lazy<RuntimeFile[]> Files = new(() =>
    {
        using var stream = Assembly.GetManifestResourceStream("SonicRoute.AndroidAudio.AndroidRuntime.files.json")!;
        var files = JsonSerializer.Deserialize<RuntimeFile[]>(stream)!;
        if (files.Length is < 1 or > 32 || files.Any(f => f.Bytes is < 1 or > 32 * 1024 * 1024
            || f.Path.Split('/').Any(p => p is "" or "." or "..") || f.Path.Contains('\\') || f.Path.Contains(':')
            || f.SHA256.Length != 64)) throw new InvalidDataException("Invalid embedded phone runtime manifest.");
        return files;
    });
    private readonly object _gate = new();
    private int _prepared;
    private readonly Lazy<string> _directory;
    public string DirectoryPath => _directory.Value;

    public AndroidRuntimeBundle(string cacheRoot)
    {
        // C# changes reuse the same cache; only changed tool content receives a different identity.
        string root = Path.GetFullPath(cacheRoot);
        _directory = new(() =>
        {
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\n', Files.Value.Select(f => f.Path + ":" + f.SHA256)))))[..24];
            return Path.Combine(root, identity);
        });
    }
    private static int ComponentBit(string group) => group switch { "audio" => 2,
        _ => throw new ArgumentException("Unknown phone runtime component.", nameof(group)) };
    internal Task PrepareAsync(string group, CancellationToken token) => (Volatile.Read(ref _prepared) & ComponentBit(group)) != 0
        ? Task.CompletedTask : Task.Run(() => Prepare(group, token), token);
    internal void Prepare(string group, CancellationToken token = default)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            int bit = ComponentBit(group);
            if ((_prepared & bit) != 0) return;
            var files = Files.Value.Where(f => f.Group == group).ToArray();
            if (files.Length == 0) throw new ArgumentException("Unknown phone runtime component.", nameof(group));
            RejectLinks(DirectoryPath);
            Directory.CreateDirectory(DirectoryPath);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(DirectoryPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
                RejectLinks(path);
                if (Matches(path, file)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var source = Assembly.GetManifestResourceStream("SonicRoute.AndroidRuntime." + file.Path.Replace('/', '.'))
                        ?? throw new InvalidDataException("Missing embedded phone component: " + file.Path))
                    using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        byte[] buffer = new byte[65536]; int count; long total = 0;
                        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            total += count;
                            if (total > file.Bytes) throw new InvalidDataException("Oversized embedded phone component.");
                            hash.AppendData(buffer, 0, count); target.Write(buffer, 0, count);
                        }
                        if (total != file.Bytes || Convert.ToHexString(hash.GetHashAndReset()) != file.SHA256)
                            throw new InvalidDataException("Embedded phone component checksum mismatch.");
                        target.Flush(true);
                    }
                    token.ThrowIfCancellationRequested();
                    try { File.Move(temporary, path, true); }
                    catch (IOException) when (Matches(path, file)) { } // Another process prepared the identical component.
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            Interlocked.Or(ref _prepared, bit);
        }
    }
    private static bool Matches(string path, RuntimeFile file)
    {
        if (!File.Exists(path)) return false;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return input.Length == file.Bytes && Convert.ToHexString(SHA256.HashData(input)) == file.SHA256;
    }
    private static void RejectLinks(string path)
    {
        for (string? item = path; item != null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Phone runtime cache cannot contain links or junctions.");
    }
}
