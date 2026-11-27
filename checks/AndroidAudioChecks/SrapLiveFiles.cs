using System.Diagnostics;
using System.Security.Cryptography;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapVerifyFilesAsync(SrapSession session, string directory, CancellationToken token)
    {
        string? WritableRoot()
        {
            try
            {
                if (session.Capabilities.Entries.GetValueOrDefault("files.transfer")?.Limits?.GetValueOrDefault("write") != "true") return null;
                return SrapFilesClient.Root(session);
            }
            catch (SrapException) { return null; }
        }
        long accepted = 0;
        var files = new SrapFilesClient(session, () => accepted = Stopwatch.GetTimestamp());
        string root;
        Console.WriteLine("FILES_WAITING Select a new empty test folder in the phone Settings / Shared folder.");
        while (true)
        {
            string? previous = WritableRoot();
            if (previous != null)
            {
                try
                {
                    var initial = await files.ListAsync(previous, token);
                    if (initial.Count == 0 && WritableRoot() == previous) { root = previous; break; }
                    Console.WriteLine("FILES_WAITING The selected folder already contains entries. Select an empty test folder.");
                }
                catch (SrapException e) when (e.Code is "permission_lost" or "stale_session") { }
            }
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Update() { if (WritableRoot() != previous) changed.TrySetResult(); }
            session.CapabilitiesChanged += Update;
            try { Update(); await changed.Task.WaitAsync(token); }
            finally { session.CapabilitiesChanged -= Update; }
        }
        var label = Guid.NewGuid().ToString("N")[..12];
        string name = $"SonicRoute-transfer-{label}.bin";
        string workspace = Path.Combine(Path.GetFullPath(directory), "file-checks", label);
        string downloads = Path.Combine(workspace, "received");
        Directory.CreateDirectory(workspace);
        string source = Path.Combine(workspace, name);
        const int length = 1024 * 1024 + 17;
        byte[] payload = RandomNumberGenerator.GetBytes(length);
        await File.WriteAllBytesAsync(source, payload, token);
        string expected = Convert.ToHexString(SHA256.HashData(payload));
        Console.WriteLine($"FILES_OFFER name={name} bytes={length} sha256={expected}. Confirm this file on the phone.");
        long began = Stopwatch.GetTimestamp();
        string saved = await files.SendAsync(source, root, token);
        var uploaded = Stopwatch.GetElapsedTime(accepted);
        var total = Stopwatch.GetElapsedTime(began);
        Check(saved == name && accepted >= began && WritableRoot() == root, "phone accepted the named file and returned its verified storage receipt");
        Console.WriteLine(FormattableString.Invariant($"FILES_UPLOAD bytes={length} totalSeconds={total.TotalSeconds:F3} transferSeconds={uploaded.TotalSeconds:F3} MiBPerSecond={length / 1048576d / uploaded.TotalSeconds:F3}"));
        var listed = await files.ListAsync(root, token);
        Check(listed.Count(e => e.Name == name && !e.Directory && e.Size == (ulong)length) == 1,
            "the selected SAF folder enumerates the committed file with its exact size");
        began = Stopwatch.GetTimestamp();
        string downloaded = await files.ReceiveAsync(root, name, downloads, token);
        var received = Stopwatch.GetElapsedTime(began);
        await using var readback = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(readback, token));
        Check(readback.Length == length && actual == expected && !session.Token.IsCancellationRequested,
            "the independently reopened Windows download matches the generated SHA-256 and size on the same trusted session");
        Console.WriteLine(FormattableString.Invariant($"FILES_DOWNLOAD bytes={readback.Length} transferSeconds={received.TotalSeconds:F3} MiBPerSecond={length / 1048576d / received.TotalSeconds:F3} sha256={actual}"));
        Console.WriteLine($"FILES_RETAINED phoneFile={name} localFiles={workspace}");
    }
}
