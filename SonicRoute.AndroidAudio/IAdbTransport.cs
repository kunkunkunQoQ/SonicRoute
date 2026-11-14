using System.Net;

namespace SonicRoute.AndroidAudio;

/// <summary>Result of a completed non-interactive ADB shell invocation.</summary>
public sealed record AdbShellResult(string Output, int ExitCode, string Error = "");

/// <summary>Active ADB TCP-to-device forwarding rule reported by the daemon or local bridge.</summary>
public sealed record AdbForwardRule(string Serial, string Local, string Remote);

/// <summary>Bidirectional shell session over ADB smart-socket or native direct transport.</summary>
public interface IAdbShellSession : IAsyncDisposable
{
    Stream OutputStream { get; }
    Stream ErrorStream { get; }
    Stream InputStream { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Task<int> WaitForExitAsync(CancellationToken token = default);
    Task CloseInputAsync(CancellationToken token = default);
    void Kill();
}

/// <summary>Abstraction for communicating with Android devices via ADB server socket (5037) or in-process transport.</summary>
public interface IAdbTransport
{
    Task<string> VersionAsync(CancellationToken token);
    Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token);
    /// <summary>Blocks until canceled while pushing device-list snapshots whenever ADB reports a change.</summary>
    Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> onChanged, CancellationToken token);
    Task<IReadOnlyList<string>> FeaturesAsync(string serial, CancellationToken token);
    Task<string> GetSerialNoAsync(string serial, CancellationToken token);
    Task<AdbShellResult> RunShellAsync(string serial, string command, CancellationToken token);
    Task<IAdbShellSession> OpenShellAsync(string serial, string command, bool redirectInput, CancellationToken token);
    Task PushAsync(string serial, string localPath, string remotePath, CancellationToken token);
    Task<int> ForwardTcpToLocalAbstractAsync(string serial, string localAbstractName, CancellationToken token);
    Task RemoveForwardAsync(string serial, string localSpec, CancellationToken token);
    Task<IReadOnlyList<AdbForwardRule>> ListForwardsAsync(CancellationToken token);
    Task<Stream> OpenLocalAbstractAsync(string serial, string name, CancellationToken token);
    Task<string> PairAsync(string address, string password, CancellationToken token);
    Task<string> ConnectAsync(string address, CancellationToken token);
    Task<string> DisconnectAsync(string address, CancellationToken token);
    Task<IReadOnlyList<AndroidMdnsService>> MdnsServicesAsync(CancellationToken token);
}
