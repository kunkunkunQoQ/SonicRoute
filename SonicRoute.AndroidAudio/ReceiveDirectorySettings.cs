using System.Runtime.InteropServices;
using SonicRoute.Core;

namespace SonicRoute.AndroidAudio;

internal sealed class ReceiveDirectorySettings(string settingsFile)
{
    private readonly object _receiveDirectoryGate = new();
    private string? _receiveDirectory;

    internal string GetReceiveDirectory()
    {
        lock (_receiveDirectoryGate)
        {
            if (_receiveDirectory != null) return _receiveDirectory;
            try
            {
                if (File.Exists(settingsFile) && File.ReadAllText(settingsFile) is { Length: > 0 } configured)
                    return _receiveDirectory = NormalizeReceiveDirectory(configured);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { System.Diagnostics.Trace.WriteLine(ex); }
            return _receiveDirectory = GetDownloadsFolder();
        }
    }

    internal void SetReceiveDirectory(string? folder)
    {
        lock (_receiveDirectoryGate)
        {
            string path = folder == null ? GetDownloadsFolder() : NormalizeReceiveDirectory(folder);
            if (folder != null && !Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
            AtomicFile.WriteAllText(settingsFile, folder == null ? string.Empty : path);
            _receiveDirectory = path;
        }
    }

    private static string NormalizeReceiveDirectory(string folder)
    {
        string path = Path.GetFullPath(folder);
        if (!Path.IsPathFullyQualified(folder) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new IOException("Choose a file-system folder.");
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string GetDownloadsFolder()
    {
        Guid downloads = new("374de290-123f-4565-9164-39c4925e467b");
        // KF_FLAG_DONT_VERIFY also resolves redirected folders without creating or listing them.
        int result = SHGetKnownFolderPath(in downloads, 0x4000, 0, out nint value);
        try
        {
            if (result >= 0 && Marshal.PtrToStringUni(value) is { Length: > 0 } path)
                return NormalizeReceiveDirectory(path);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) return NormalizeReceiveDirectory(Path.Combine(profile, "Downloads"));
            Marshal.ThrowExceptionForHR(result);
            throw new DirectoryNotFoundException("Windows could not locate the Downloads folder.");
        }
        finally { Marshal.FreeCoTaskMem(value); }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
