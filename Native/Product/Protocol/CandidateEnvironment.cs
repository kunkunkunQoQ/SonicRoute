using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using SonicRoute.Core;
using SonicRoute.StoreInterop;
using SonicRoute.Updates;

namespace SonicRoute.Native.Product;

/// <summary>Resolve every persistent/OS identity before the first Core singleton is touched.</summary>
internal sealed class CandidateEnvironment
{
    private CandidateEnvironment(string root, string data, string id)
    {
        Root = root;
        DataDirectory = data;
        InstanceId = id;
        var runtime = AppRuntimeIdentity.Current;
        MutexName = runtime.MutexName;
        ActivationTitle = runtime.ActivateSinkTitle;
        ActivationMessage = runtime.ActivateMessageName;
        IsPackaged = runtime.IsPackaged;
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        string suffix = runtime.CommandEdition + "." + current.SessionId + "." + identity.User!.Value;
        RulePipeName = "SonicRoute.RuleCommand." + suffix;
        ControlPipeName = "SonicRoute.NativeControl." + suffix;
    }

    internal string Root { get; }
    internal string DataDirectory { get; }
    internal string UpdateDirectory => Path.Combine(DataDirectory, "Updates");
    internal string InstallDirectory => Path.Combine(Root, "install");
    internal string InstanceId { get; }
    internal string MutexName { get; }
    internal string RulePipeName { get; }
    internal string ControlPipeName { get; }
    internal string ActivationTitle { get; }
    internal string ActivationMessage { get; }
    internal string Edition => BuildIdentity.Edition;
    internal bool IsPackaged { get; }
    internal bool IsIsolated => InstanceId.Length != 0;

#if SONICROUTE_PHONE
    internal int? IsolatedAdbFixturePort()
    {
        if (!IsIsolated || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SONICROUTE_CANDIDATE_ROOT")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SONICROUTE_CANDIDATE_ID"))) return null;
        string? value = Environment.GetEnvironmentVariable("SONICROUTE_TEST_ADB_LOOPBACK_PORT");
        if (value == null) return null;
        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out int port) && port is >= 49152 and <= 65535 ? port : throw new ArgumentException("Expected a dynamic loopback test port.");
    }
#endif

    internal static CandidateEnvironment Open()
    {
        bool packaged = NativePackageIdentity.IsPackaged();
        bool validationPackage = packaged && NativePackageIdentity.GetApplicationUserModelId().StartsWith(
            "SonicRoute.PhaseOne.Validation_", StringComparison.OrdinalIgnoreCase);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string productionData = Path.Combine(local, "SonicRoute");
        string? requested = Environment.GetEnvironmentVariable("SONICROUTE_CANDIDATE_ROOT");
        string? profile = Environment.GetEnvironmentVariable("SONICROUTE_CANDIDATE_ID");
        string? overrideData = Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY");
        string? id = Environment.GetEnvironmentVariable("SONICROUTE_INSTANCE_ID")?.Trim();
        string root, data;
        if (validationPackage || requested != null || profile != null)
        {
            profile ??= "v136-preview"; // Existing explicitly isolated launchers and validation packages.
            ValidateName(profile);
            root = Path.Combine(LocalDirectory(requested ?? local), "SonicRoute.NativeCandidate", profile,
                (packaged ? "Store-" : "") + BuildIdentity.Edition);
            data = Path.Combine(root, "data");
            id = null; // Derive the original candidate identity from its root.
        }
        else
        {
            data = overrideData == null ? productionData : LocalDirectory(overrideData);
            if (string.Equals(data, productionData, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(id)) throw new ArgumentException("An isolated instance requires a separate data directory.");
                // The updater may forward the normal data path explicitly to its worker.
                return new CandidateEnvironment(data, AppDataRoot.DirectoryPath, "");
            }
            root = data;
            if (!string.IsNullOrEmpty(id)) ValidateName(id);
        }

        string[] protectedDirectories = [
            productionData, Path.Combine(local, "Packages"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        ];
        if (protectedDirectories.Any(p => p.Length > 0 && (ContainsPath(p, root) || ContainsPath(root, p))))
            throw new InvalidOperationException("Isolated data overlaps a protected installation or user profile.");
        UpdateProtocol.RejectReparsePoints(data);
        UpdateProtocol.RejectReparsePoints(Path.Combine(data, "Updates"));
        Directory.CreateDirectory(data);
        if (string.IsNullOrEmpty(id))
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
            id = "NativeCandidate_" + key;
        }
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", data);
        Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID", id);
        Environment.SetEnvironmentVariable("SONICROUTE_ISOLATED_ADB_KEYS", Path.Combine(data, "adb-keys"));
        var result = new CandidateEnvironment(root, data, id);
        if (!string.Equals(AppDataRoot.DirectoryPath, data, StringComparison.OrdinalIgnoreCase)
            || !result.MutexName.Contains(id, StringComparison.Ordinal))
            throw new InvalidOperationException("Isolated identity was initialized out of order.");
        return result;
    }

    private static void ValidateName(string value)
    {
        if (value.Length is < 1 or > 80 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            throw new ArgumentException("Invalid isolated profile name.");
    }

    private static string LocalDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length < 3 || path[1] != ':' || path.AsSpan(2).Contains(':') ||
            path.Split('\\', '/').Any(p => p.EndsWith('.') || p.EndsWith(' ')))
            throw new ArgumentException("An isolated data path must be an absolute local directory.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool ContainsPath(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal void EnsureInstallTarget(string path)
    {
        if (IsIsolated && !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), InstallDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Updates require a candidate executable in its profile's install directory.");
        UpdateProtocol.RejectReparsePoints(path);
    }
}
