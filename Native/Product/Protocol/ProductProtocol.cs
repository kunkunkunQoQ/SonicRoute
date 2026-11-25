using System.Diagnostics;
using System.Text.Json;
using SonicRoute.Core;
using SonicRoute.StoreInterop;
using SonicRoute.Updates;

namespace SonicRoute.Native.Product;

internal enum RuleMode { None, Run, Help, Invalid }

internal static class ProductProtocol
{
    internal static RuleMode ParseRule(string[] args, out string selector) => RuleCommandLine.Parse(args, out selector) switch
    {
        RuleCommandLine.Mode.Help => RuleMode.Help,
        RuleCommandLine.Mode.Run => RuleMode.Run,
        RuleCommandLine.Mode.Invalid => RuleMode.Invalid,
        _ => RuleMode.None
    };

    internal static bool BeforeStartup(CandidateEnvironment profile, ref string[] args)
    {
        if (profile.IsPackaged) StoreActivation.SetTaskbarIdentity(NativePackageIdentity.GetApplicationUserModelId());
        // A control client never starts a resident host. It must still receive a framed status
        // or failure during an update instead of exiting successfully with no response.
        if (args.Length > 0 && args[0] == "--control") return true;
        if (!UpdateBootstrap.BeforeStartup(args, profile.DataDirectory, profile.Edition, profile.IsPackaged,
            profile.MutexName, message => Console.Error.WriteLine(message), AppRuntimeIdentity.Current.CommandEdition,
            profile.EnsureInstallTarget)) return false;
        if (UpdateBootstrap.Pending != null) args = [];
        return RestartCoordinator.CompleteHandoff(ref args);
    }

    internal static bool TransactionActive()
    {
        using var gate = new Mutex(false, UpdateInstaller.GateName(AppRuntimeIdentity.Current.CommandEdition));
        if (!UpdateBootstrap.TryAcquire(gate, 0)) return true;
        gate.ReleaseMutex();
        return false;
    }

    internal static UpdateService CreateUpdates(CandidateEnvironment profile, Func<Action, CancellationToken, Task> onOwner)
        => new(FileVersionInfo.GetVersionInfo(UpdateBootstrap.ExecutablePath).ProductVersion ?? "1.36",
            profile.Edition, onOwner, profile.EnsureInstallTarget);

    internal static Task<bool> RestartAsync() => RestartCoordinator.TryStartAsync(UpdateBootstrap.ExecutablePath, start: info =>
    {
        info.UseShellExecute = false; // Child inherits the same candidate profile even outside its install directory.
        info.CreateNoWindow = true;
        return Process.Start(info);
    });

    // Called on a live host dispatch after service initialization (never while constructing the host).
    internal static async Task ConfirmUpdatedStartupAsync(Action requestExit)
    {
        if (UpdateBootstrap.Pending is not { } plan) return;
        try
        {
            using var started = EventWaitHandle.OpenExisting(plan.EventName("started"));
            using var stop = EventWaitHandle.OpenExisting(plan.EventName("stop"));
            using var finished = EventWaitHandle.OpenExisting(plan.EventName("finished"));
            started.Set();
            int signal = await Task.Run(() => WaitHandle.WaitAny([finished, stop], 90000)).ConfigureAwait(false);
            if (signal == 1) requestExit();
            else if (signal == WaitHandle.WaitTimeout)
                throw new TimeoutException("Pending update worker did not finish.");
        }
        catch (Exception e) { ExceptionDiagnostics.Record("Update.ConfirmStartup", e); }
    }

    // The offline candidate command consumes the same hash-addressed download and Protocol 2 manifest.
    // Normal release downloads use UpdateTransport and UpdateService, including their upstream integrity checks.
    internal static async Task<bool> InstallCachedAsync(CandidateEnvironment profile, string manifestPath)
    {
        if (profile.IsPackaged) return false;
        profile.EnsureInstallTarget(UpdateBootstrap.ExecutablePath);
        string expected = Path.Combine(profile.UpdateDirectory, UpdateProtocol.ManifestName);
        if (!Path.GetFullPath(manifestPath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected candidate's cached SonicRoute-update.json.");
        UpdateProtocol.RejectReparsePoints(expected);
        if (new FileInfo(expected).Length > 1024 * 1024) throw new InvalidDataException("Update manifest too large.");
        UpdateManifest manifest;
        using (var stream = File.OpenRead(expected))
            manifest = JsonSerializer.Deserialize(stream, UpdateManifestContext.Default.UpdateManifest) ??
                throw new InvalidDataException("Empty update manifest.");
        if (manifest.SchemaVersion != 1 || manifest.ProtocolVersion != 2 || UpdateProtocol.ParseVersion(manifest.Version) == null ||
            manifest.Packages == null || manifest.Packages.Any(p => p == null || string.IsNullOrWhiteSpace(p.Runtime)))
            throw new InvalidDataException("Invalid Protocol 2 manifest.");
        var package = manifest.Packages.Single(p => p.Edition == profile.Edition && p.Architecture == UpdateBootstrap.Architecture
            && p.Runtime == UpdateProtocol.CurrentRuntime);
        UpdateProtocol.ValidatePackage(package, profile.Edition, UpdateBootstrap.Architecture);
        string payload = Path.Combine(profile.UpdateDirectory, "downloads", package.Sha256.ToLowerInvariant() + ".package");
        UpdateProtocol.RejectReparsePoints(payload);
        return await UpdateService.PrepareAndStartAsync(new UpdateRelease { Tag = manifest.Version, Package = package },
            payload, profile.DataDirectory, CancellationToken.None).ConfigureAwait(false);
    }
}
