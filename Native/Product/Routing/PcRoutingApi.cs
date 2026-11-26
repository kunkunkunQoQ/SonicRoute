using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed class PcRoutingPolicy : IDisposable
{
    private readonly MtaScope _mta = new();
    private readonly Policy _policy;
    private readonly Flow _flow;
    internal PcRoutingPolicy(EDataFlow flow)
    {
        _flow = (Flow)flow;
        try { _policy = new Policy(); }
        catch { _mta.Dispose(); throw; }
    }
    internal string? GetDefaultEndPoint(int pid, ERole role) => _policy.Read(checked((uint)pid), _flow, (Role)role);
    internal int SetDefaultEndPoint(string? fullId, int pid, ERole role)
    {
        try { _policy.Write(checked((uint)pid), _flow, (Role)role, fullId); return 0; }
        catch (Exception ex) { return ex.HResult; }
    }
    internal static string UnpackDeviceId(string id) => AudioPolicyConfig.UnpackDeviceId(id);
    public void Dispose() { _policy.Dispose(); _mta.Dispose(); }
}

internal static partial class PcRoutingApi
{
    internal const string SystemDefaultDeviceId = Core.AudioService.SystemDefaultDeviceId;
    internal static bool IsSystemDefault(string? id) => Core.AudioService.IsSystemDefault(id);
    internal static string? GetPersistedEndpoint(int pid, EDataFlow flow)
    {
        using var policy = new PcRoutingPolicy(flow);
        string? raw = policy.GetDefaultEndPoint(pid, ERole.eMultimedia);
        return raw == null ? null : PcRoutingPolicy.UnpackDeviceId(raw);
    }

    internal static (bool Success, int HResult, string Message) ApplyEndpoint(int pid, EDataFlow flow, string? endpoint)
    {
        try
        {
            using var policy = new PcRoutingPolicy(flow);
            string? next = string.IsNullOrEmpty(endpoint) || IsSystemDefault(endpoint) ? null : AudioPolicyConfig.EnsureFullDeviceId(endpoint,flow);
            var old = policy.GetDefaultEndPoint(pid, ERole.eMultimedia);
            int hr = policy.SetDefaultEndPoint(next, pid, ERole.eMultimedia);
            if (hr >= 0)
            {
                hr = policy.SetDefaultEndPoint(next, pid, ERole.eConsole);
                if (hr < 0)
                {
                    int restored = policy.SetDefaultEndPoint(old, pid, ERole.eMultimedia);
                    if (restored < 0) ExceptionDiagnostics.Record("Routing.Rollback", new System.Runtime.InteropServices.COMException("Multimedia role restore failed.", restored));
                }
            }
            return hr >= 0 ? (true,hr,L10n.T("History.Status.success")) : (false,hr,$"HRESULT 0x{hr:X8}");
        }
        catch (Exception ex) { return (false, ex.HResult, ex.Message); }
    }

    internal static Task<(bool Success,int HResult,string Message)> ApplyEndpointAsync(AudioAppInfo app, EDataFlow flow, string device)
        => Task.Run(() => ApplyEndpoint(checked((int)app.ProcessId), flow, device));
    internal static event Action? OutputsChanged;
    internal static void NotifyRouteChanged(int pid) => OutputsChanged?.Invoke();

    internal static Task<AudioAppInfo[]> GetAppsAsync(bool refresh) => Task.Run(() =>
    {
        using var mta = new MtaScope();
        using var devices = new Endpoints();
        var ids = new HashSet<uint>();
        foreach (var endpoint in devices.Active(Flow.Render))
        {
            try
            {
                using var device = devices.Open(endpoint.Id);
                foreach (var session in Endpoints.Sessions(device)) if (session.ProcessId != 0) ids.Add(session.ProcessId);
            }
            catch (System.Runtime.InteropServices.COMException e) { ExceptionDiagnostics.Record("PlaybackRestore.Enumerate", e); }
        }
        return ids.Select(pid => new AudioAppInfo { ProcessId = pid }).ToArray();
    });

    internal static bool IsPhoneOutputDevice(string? id) => id?.StartsWith("android-out:",StringComparison.OrdinalIgnoreCase) == true;
    internal static string[] ResolveOutputs(string[] requested, bool strict)
    {
        HashSet<string>? active = null;
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in requested)
        {
            string id = PcRoutingPolicy.UnpackDeviceId(value);
#if SONICROUTE_PHONE
            if (IsPhoneOutputDevice(id)) { valid.Add(id); continue; }
#endif
            active ??= ProductAudio.Devices(Flow.Render).Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (IsSystemDefault(id) ? active.Count > 0 : active.Contains(id)) valid.Add(id);
            else if (strict) throw new IOException(L10n.T("Ov.CurrentUnavailable"));
        }
        return valid.ToArray();
    }

    private static readonly string MirrorOwnerPrefix = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SONICROUTE_INSTANCE_ID"))
        ? @"Local\SonicRoute.AudioMirror." : @"Local\SonicRoute.AudioMirror.NativeCandidate.";
    internal static string MirrorOwnerName(int pid) => MirrorOwnerPrefix + pid;
}
