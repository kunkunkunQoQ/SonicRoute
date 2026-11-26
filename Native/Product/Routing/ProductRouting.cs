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
#if SONICROUTE_PHONE
    internal ExternalAudioBridge? ExternalAudio
    {
        get => PcRoutingApi.Phones;
        set => PcRoutingApi.Phones = value;
    }
#endif
    internal void Start() => AppPlaybackRestore.Start();

    internal string? ReadPlaybackRoute(int pid, bool includeSelection, out HashSet<string>? selected)
    {
#if SONICROUTE_PHONE
        if (LocalAppOutputMirrors.GetExclusivePhone(pid, out _) is { } phone)
        {
            selected = includeSelection ? AppPlaybackOutputs.GetSelected(pid, phone) : null;
            return phone;
        }
        if (ExternalAudio?.GetPhoneRoute(pid, out bool exclusive) is { } legacy && exclusive)
        {
            selected = includeSelection ? new(StringComparer.OrdinalIgnoreCase) { legacy } : null;
            return legacy;
        }
#endif
        string? primary = PcRoutingApi.GetPersistedEndpoint(pid, EDataFlow.eRender);
        selected = includeSelection ? AppPlaybackOutputs.GetSelected(pid, primary) : null;
        return primary;
    }

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
                    var values = await Task.Run(() =>
                    {
                        ReadPlaybackRoute(pid, includeSelection: true, out var selected);
                        return selected!.ToArray();
                    }).ConfigureAwait(false);
                    return new(JsonSerializer.Serialize(values, RoutingJson.Default.StringArray));
                case "output-native" when request.Args is [_, var single]:
                    return await ApplyOutputsAsync(pid, [single], single, nativeIfUnmanaged: true).ConfigureAwait(false);
                case "outputs-set" when request.Args is [_, var preferred, .. var outputs]:
                    return await ApplyOutputsAsync(pid, outputs, preferred, nativeIfUnmanaged: false).ConfigureAwait(false);
                default: return new("error: unknown_command");
            }
        }
        finally
        {
            lock (_gate) { if (--_active == 0 && _closing) _drained.TrySetResult(); }
        }
    }

    private static async Task<ControlReply> ApplyOutputsAsync(int pid, string[] outputs, string preferred, bool nativeIfUnmanaged)
    {
#if !SONICROUTE_PHONE
        if (PcRoutingApi.IsPhoneOutputDevice(preferred) || outputs.Any(PcRoutingApi.IsPhoneOutputDevice))
            return new("error: phone_output_unsupported_in_lite");
#endif
        var result = await AppPlaybackOutputs.SetAsync(new AudioAppInfo { ProcessId = (uint)pid },
            outputs, preferred, nativeIfUnmanaged).ConfigureAwait(false);
        return new(result.Success ? "ok" : $"error: 0x{result.HResult:X8} {result.Message}");
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
#if SONICROUTE_PHONE
        PhoneMirrorIsolation.Shutdown();
#endif
    }
}
