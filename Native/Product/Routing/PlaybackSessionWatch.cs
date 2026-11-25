using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Product;

namespace SonicRoute;

internal sealed class PlaybackSessionWatch : IDisposable
{
    private readonly IDisposable _topology = ProductAudio.WatchDevices(AppPlaybackRestore.RefreshDevices);
    private readonly Dictionary<string,SessionCreationWatch> _devices = new(StringComparer.OrdinalIgnoreCase);
    internal bool IsAvailable(string id) => PcRoutingApi.IsSystemDefault(id) ? _devices.Count != 0 : _devices.ContainsKey(id);
    internal void RefreshDevices()
    {
        using var mta = new MtaScope();
        using var endpoints = new Endpoints();
        var active = endpoints.Active(Flow.Render).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in active)
        {
            if (_devices.ContainsKey(id)) continue;
            try
            {
                using var device = endpoints.Open(id);
                _devices.Add(id, new SessionCreationWatch(device, AppPlaybackRestore.SessionCreated));
            }
            catch (COMException ex) { ExceptionDiagnostics.Record("PlaybackRestore.Endpoint", ex); }
        }
        foreach (string id in _devices.Keys.Where(id => !active.Contains(id)).ToArray()) Remove(id);
    }
    private void Remove(string id)
    {
        if (!_devices.Remove(id, out var registration)) return;
        try { registration.Dispose(); }
        catch (COMException ex) { ExceptionDiagnostics.Record("PlaybackRestore.Unregister", ex); }
    }
    public void Dispose()
    {
        using var mta = new MtaScope();
        try { foreach (string id in _devices.Keys.ToArray()) Remove(id); }
        finally { _topology.Dispose(); }
    }
}
