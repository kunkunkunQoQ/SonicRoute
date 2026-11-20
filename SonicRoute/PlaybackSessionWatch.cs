using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute;

// Only exists while a saved multi-output choice needs to follow an application across process lifetimes.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class PlaybackSessionWatch : IMMNotificationClient, IDisposable
{
    private readonly WasapiDeviceLease.Enumerator _enumerator = new();
    private readonly Dictionary<string, WasapiDeviceLease> _devices = new(StringComparer.OrdinalIgnoreCase);
    internal bool IsAvailable(string id) => AudioService.IsSystemDefault(id) ? _devices.Count != 0 : _devices.ContainsKey(id);

    internal PlaybackSessionWatch()
    {
        try { _enumerator.Register(this); }
        catch { _enumerator.Dispose(); throw; }
    }

    internal void RefreshDevices()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lease in _enumerator.ActiveRender())
        {
            bool retained = false;
            try
            {
                string id = lease.Device.ID;
                active.Add(id);
                if (_devices.ContainsKey(id)) continue;
                var manager = lease.Device.AudioSessionManager;
                manager.OnSessionCreated += OnSession;
                // WASAPI starts creation notifications after GetCount.
                _ = manager.Sessions.Count;
                _devices.Add(id, lease);
                retained = true;
            }
            catch (COMException ex) { ExceptionDiagnostics.Record("PlaybackRestore.Endpoint", ex); }
            finally { if (!retained) lease.Dispose(); }
        }
        foreach (string id in _devices.Keys.Where(id => !active.Contains(id)).ToArray()) Remove(id);
    }

    private static void OnSession(object sender, IAudioSessionControl session)
    {
        try
        {
            if (session is IAudioSessionControl2 control && control.GetProcessId(out uint pid) >= 0 && pid > 0 && pid <= int.MaxValue)
                AppPlaybackRestore.SessionCreated((int)pid);
        }
        catch (COMException) { }
    }

    private void Remove(string id)
    {
        var lease = _devices[id];
        _devices.Remove(id);
        try { lease.Device.AudioSessionManager.OnSessionCreated -= OnSession; }
        catch (COMException) { }
        finally { lease.Dispose(); }
    }

    public void OnDeviceAdded(string id) => AppPlaybackRestore.RefreshDevices();
    public void OnDeviceRemoved(string id) => AppPlaybackRestore.RefreshDevices();
    public void OnDeviceStateChanged(string id, DeviceState state) => AppPlaybackRestore.RefreshDevices();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id)
    {
        if (flow == DataFlow.Render) AppPlaybackRestore.RefreshDevices();
    }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }

    public void Dispose()
    {
        try
        {
            foreach (string id in _devices.Keys.ToArray()) Remove(id);
            _enumerator.Unregister(this);
        }
        finally { _enumerator.Dispose(); }
    }
}
