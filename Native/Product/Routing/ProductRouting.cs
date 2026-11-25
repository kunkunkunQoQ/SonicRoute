using System.Globalization;
using System.Text.Json;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute.Native.Product;

internal sealed class ProductRouting : IDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closing;
    internal void Start() => AppPlaybackRestore.Start();

    internal Task<ControlReply> SelectOutputAsync(int pid, string target)
        => ExecuteAsync(new ControlRequest("output-native", [pid.ToString(CultureInfo.InvariantCulture), target]));

    internal async Task<ControlReply> ExecuteAsync(ControlRequest request)
    {
        lock (_gate)
        {
            if (_closing) return new("error: routing_closed");
            ++_active;
        }
        try
        {
            if (request.Verb == "outputs-clear-saved" && request.Args.Length == 0)
                return new(await AppPlaybackRestore.ClearAsync().ConfigureAwait(false) ? "ok" : "error: save_failed");
            if (request.Args.Length == 0 || !int.TryParse(request.Args[0], NumberStyles.None, CultureInfo.InvariantCulture,
                out int pid) || pid <= 0) return new("error: invalid_pid");
            switch (request.Verb)
            {
                case "mirror-status" when request.Args.Length == 1:
                    return new(JsonSerializer.Serialize(LocalAppOutputMirrors.Status(pid), RoutingJson.Default.MirrorStatus));
                case "outputs-get" when request.Args.Length == 1:
                    var values = await Task.Run(() => AppPlaybackOutputs.GetSelected(pid,
                        PcRoutingApi.GetPersistedEndpoint(pid,EDataFlow.eRender)).ToArray()).ConfigureAwait(false);
                    return new(JsonSerializer.Serialize(values, RoutingJson.Default.StringArray));
                case "output-native" when request.Args is [_, var single] && !PcRoutingApi.IsPhoneOutputDevice(single):
                    var native = await AppPlaybackOutputs.SetAsync(new AudioAppInfo { ProcessId = (uint)pid }, [single], single, nativeIfUnmanaged: true).ConfigureAwait(false);
                    return new(native.Success ? "ok" : $"error: 0x{native.HResult:X8} {native.Message}");
                case "outputs-set" when request.Args is [_, var preferred, .. var outputs]:
                    if (outputs.Any(PcRoutingApi.IsPhoneOutputDevice) || PcRoutingApi.IsPhoneOutputDevice(preferred))
                        return new("error: PC playback selection requires Windows endpoints");
                    var result = await AppPlaybackOutputs.SetAsync(new AudioAppInfo { ProcessId = (uint)pid }, outputs, preferred).ConfigureAwait(false);
                    return new(result.Success ? "ok" : $"error: 0x{result.HResult:X8} {result.Message}");
                default: return new("error: unknown_command");
            }
        }
        finally
        {
            lock (_gate) { if (--_active == 0 && _closing) _drained.TrySetResult(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            if (_active == 0) _drained.TrySetResult();
        }
        _drained.Task.GetAwaiter().GetResult();
        AppPlaybackRestore.StopAsync().GetAwaiter().GetResult();
        LocalAppOutputMirrors.ShutdownAsync().GetAwaiter().GetResult();
    }
}
