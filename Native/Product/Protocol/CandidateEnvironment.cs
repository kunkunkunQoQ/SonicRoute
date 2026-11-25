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
    private CandidateEnvironment(string root, string id)
    {
        Root = root;
        DataDirectory = Path.Combine(root, "data");
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

    internal static CandidateEnvironment Open()
    {
        bool packaged = NativePackageIdentity.IsPackaged();
        if (packaged && !NativePackageIdentity.GetApplicationUserModelId().StartsWith(
            "SonicRoute.PhaseOne.Validation_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A candidate requires its isolated validation package identity.");

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string requested = Environment.GetEnvironmentVariable("SONICROUTE_CANDIDATE_ROOT") ?? local;
        string profile = Environment.GetEnvironmentVariable("SONICROUTE_CANDIDATE_ID") ?? "v1.36-preview";
        if (profile.Length is < 1 or > 48 || !profile.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            throw new ArgumentException("Invalid candidate profile name.");
        if (!Path.IsPathFullyQualified(requested) || requested.StartsWith(@"\\", StringComparison.Ordinal) ||
            requested.Length < 3 || requested[1] != ':' || requested.AsSpan(2).Contains(':') ||
            requested.Split('\\','/').Any(p => p.EndsWith('.') || p.EndsWith(' ')))
            throw new ArgumentException("Candidate root must be an absolute local directory.");
        string root = Path.Combine(Path.GetFullPath(requested), "SonicRoute.NativeCandidate", profile,
            (packaged ? "Store-" : "") + BuildIdentity.Edition);
        string[] protectedDirectories = [
            Path.Combine(local, "SonicRoute"), Path.Combine(local, "Packages"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        ];
        if (protectedDirectories.Any(p => p.Length > 0 && ContainsPath(p, root)))
            throw new InvalidOperationException("Candidate data overlaps a protected installation or user profile.");
        string data = Path.Combine(root, "data");
        UpdateProtocol.RejectReparsePoints(data);
        UpdateProtocol.RejectReparsePoints(Path.Combine(data, "Updates"));
        Directory.CreateDirectory(data);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        string instance = "NativeCandidate_" + key;
        Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", data);
        Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID", instance);
        var result = new CandidateEnvironment(root, instance);
        if (!string.Equals(AppDataRoot.DirectoryPath, data, StringComparison.OrdinalIgnoreCase)
            || !result.MutexName.Contains(instance, StringComparison.Ordinal))
            throw new InvalidOperationException("Candidate identity was initialized out of order.");
        return result;
    }

    private static bool ContainsPath(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal void EnsureInstallTarget(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), InstallDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Updates require a candidate executable in its profile's install directory.");
        UpdateProtocol.RejectReparsePoints(path);
    }
}
