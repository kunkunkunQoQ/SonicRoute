namespace SonicRoute.BenchmarkLab.Core;

// Only temporary test material belongs here. Reports/baselines are deliberately
// outside this lifetime and are never automatically deleted.
public sealed class OwnedTestDirectory : IDisposable
{
    public const string RootVariable = "SONICROUTE_BENCHMARK_TEMP_ROOT";
    private const string MarkerName = ".benchmarklab-owner";
    private readonly string _root, _nonce = Guid.NewGuid().ToString("N");
    private bool _disposed;
    public string DirectoryPath { get; }

    public OwnedTestDirectory(string prefix, string? root = null, string? name = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? Environment.GetEnvironmentVariable(RootVariable)
            ?? Path.Combine(Path.GetTempPath(), "SonicRouteBenchmark")));
        Directory.CreateDirectory(_root);
        RejectLinks(_root);
        DirectoryPath = Path.Combine(_root, name ?? prefix + "_" + Guid.NewGuid().ToString("N"));
        ValidateBoundary();
        if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("Test directory already exists: " + DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        using var marker = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, MarkerName), FileMode.CreateNew));
        marker.Write(_nonce);
    }

    private void ValidateBoundary()
    {
        string relative = Path.GetRelativePath(_root, Path.GetFullPath(DirectoryPath));
        if (relative == "." || Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Length != 1 || relative is "..")
            throw new InvalidOperationException("Test cleanup must address one owned child, never the work root or another directory.");
    }

    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked test directory is not accepted: " + current.FullName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!Directory.Exists(DirectoryPath)) return;
        try
        {
            ValidateBoundary(); RejectLinks(DirectoryPath);
            string marker = Path.Combine(DirectoryPath, MarkerName);
            if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker) != _nonce)
                throw new IOException("Test ownership marker changed.");
            var pending = new Stack<string>(); pending.Push(DirectoryPath);
            while (pending.Count > 0)
                foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked child prevents automatic cleanup: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Preserve the directory for a precise retry instead of hiding cleanup failure.
            Console.Error.WriteLine("CLEANUP_INCOMPLETE " + DirectoryPath + ": " + error.Message);
        }
    }
}
