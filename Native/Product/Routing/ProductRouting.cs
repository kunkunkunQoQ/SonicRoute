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
    internal ExternalAudioBridge? ExternalAudio { get; set; }
#endif
    internal void Start() => AppPlaybackRestore.Start();

    internal string? ReadPlaybackRoute(int pid, bool includeSelection, out HashSet<string>? selected)
    {
#if SONICROUTE_PHONE
        bool exclusive = false;
        string? phone = ExternalAudio?.GetPhoneRoute(pid, out exclusive);
        if (exclusive && phone != null)
        {
            selected = includeSelection ? new(StringComparer.OrdinalIgnoreCase) { phone } : null;
            return phone;
        }
#endif
        string? primary = PcRoutingApi.GetPersistedEndpoint(pid, EDataFlow.eRender);
        selected = includeSelection ? AppPlaybackOutputs.GetSelected(pid, primary) : null;
#if SONICROUTE_PHONE
        if (phone != null) selected?.Add(phone);
#endif
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
                    if (PcRoutingApi.IsPhoneOutputDevice(single))
                    {
#if SONICROUTE_PHONE
                        if (ExternalAudio == null) return new("error: phone_unavailable");
                        await LocalAppOutputMirrors.ClearAsync(pid).ConfigureAwait(false);
                        var phoneRes = ExternalAudio.RoutePcAppToPhone(pid, single, silenceLocal: true);
                        PcRoutingApi.NotifyRouteChanged(pid);
                        return new(phoneRes.Success ? "ok" : $"error: 0x{phoneRes.HResult:X8} {phoneRes.Message}");
#else
                        return new("error: phone_output_unsupported_in_lite");
#endif
                    }
#if SONICROUTE_PHONE
                    ExternalAudio?.StopPcAppToPhone(pid, restorePreviousEndpoint: false);
#endif
                    var native = await AppPlaybackOutputs.SetAsync(new AudioAppInfo { ProcessId = (uint)pid }, [single], single, nativeIfUnmanaged: true).ConfigureAwait(false);
                    PcRoutingApi.NotifyRouteChanged(pid);
                    return new(native.Success ? "ok" : $"error: 0x{native.HResult:X8} {native.Message}");
                case "outputs-set" when request.Args is [_, var preferred, .. var outputs]:
                {
                    var phoneOutputs = outputs.Where(PcRoutingApi.IsPhoneOutputDevice).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var windowsOutputs = outputs.Where(id => !PcRoutingApi.IsPhoneOutputDevice(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (phoneOutputs.Length == 0 && !PcRoutingApi.IsPhoneOutputDevice(preferred))
                    {
#if SONICROUTE_PHONE
                        ExternalAudio?.StopPcAppToPhone(pid, restorePreviousEndpoint: false);
#endif
                        var result = await AppPlaybackOutputs.SetAsync(new AudioAppInfo { ProcessId = (uint)pid }, outputs, preferred).ConfigureAwait(false);
                        PcRoutingApi.NotifyRouteChanged(pid);
                        return new(result.Success ? "ok" : $"error: 0x{result.HResult:X8} {result.Message}");
                    }
#if SONICROUTE_PHONE
                    if (ExternalAudio == null) return new("error: phone_unavailable");
                    string targetPhone = phoneOutputs.Length > 0 ? phoneOutputs[0] : preferred;
                    bool phoneOnly = windowsOutputs.Length == 0;
                    if (phoneOnly)
                    {
                        await LocalAppOutputMirrors.ClearAsync(pid).ConfigureAwait(false);
                        var phoneOnlyRes = ExternalAudio.RoutePcAppToPhone(pid, targetPhone, silenceLocal: true);
                        PcRoutingApi.NotifyRouteChanged(pid);
                        return new(phoneOnlyRes.Success ? "ok" : $"error: 0x{phoneOnlyRes.HResult:X8} {phoneOnlyRes.Message}");
                    }
                    string winPreferred = PcRoutingApi.IsPhoneOutputDevice(preferred) ? windowsOutputs[0] : preferred;
                    var winRes = await AppPlaybackOutputs.SetAsync(new AudioAppInfo { ProcessId = (uint)pid }, windowsOutputs, winPreferred).ConfigureAwait(false);
                    if (!winRes.Success) return new($"error: 0x{winRes.HResult:X8} {winRes.Message}");
                    var mixedPhoneRes = ExternalAudio.RoutePcAppToPhone(pid, targetPhone, silenceLocal: false);
                    PcRoutingApi.NotifyRouteChanged(pid);
                    return new(mixedPhoneRes.Success ? "ok" : $"error: 0x{mixedPhoneRes.HResult:X8} {mixedPhoneRes.Message}");
#else
                    return new("error: PC playback selection requires Windows endpoints");
#endif
                }
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
