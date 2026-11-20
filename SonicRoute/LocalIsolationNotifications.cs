#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonicRoute.Core;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

// Revalidate exclusive phone playback when local audio sessions or endpoint mute states change.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class LocalIsolationNotifications : IMMNotificationClient
{
    private readonly object _gate = new();
    private readonly List<(WasapiDeviceLease Lease, AudioEndpointVolumeNotificationDelegate? MuteChanged)> _devices = [];
    private WasapiDeviceLease.Enumerator? _enumerator;
    private int _rebuildQueued, _noticeQueued, _stopped;

    internal LocalIsolationNotifications()
    {
        QueueRebuild();
    }

    private void QueueRebuild()
    {
        if (Volatile.Read(ref _stopped) != 0 || Interlocked.Exchange(ref _rebuildQueued, 1) != 0) return;
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                Interlocked.Exchange(ref _rebuildQueued, 0);
                if (Volatile.Read(ref _stopped) != 0) return;
                try
                {
                    if (_enumerator == null)
                    {
                        var enumerator = new WasapiDeviceLease.Enumerator();
                        try { enumerator.Register(this); _enumerator = enumerator; }
                        catch { enumerator.Dispose(); throw; }
                    }
                    ClearSessions();
                    foreach (var lease in _enumerator.ActiveRender())
                    {
                        var endpoint = lease.Device;
                        bool subscribed = false, retained = false;
                        try
                        {
                            var sessions = endpoint.AudioSessionManager;
                            sessions.OnSessionCreated += SessionCreated;
                            subscribed = true;
                            // Core Audio enables the registered notification after GetCount.
                            _ = sessions.Sessions.Count;
                            AudioEndpointVolumeNotificationDelegate? muteChanged = null;
                            try
                            {
                                int muted = endpoint.AudioEndpointVolume.Mute ? 1 : 0;
                                string endpointId = endpoint.ID;
                                muteChanged = data =>
                                {
                                    int next = data.Muted ? 1 : 0;
                                    if (Interlocked.Exchange(ref muted, next) == next) return;
                                    if (next == 0) AndroidAudioHost.OutputUnmuted(endpointId, data.EventContext);
                                    QueueNotice();
                                };
                                endpoint.AudioEndpointVolume.OnVolumeNotification += muteChanged;
                            }
                            catch (COMException) { muteChanged = null; }
                            _devices.Add((lease, muteChanged));
                            retained = true;
                        }
                        catch (COMException) { }
                        finally
                        {
                            if (!retained)
                            {
                                try { if (subscribed) endpoint.AudioSessionManager.OnSessionCreated -= SessionCreated; }
                                catch (COMException) { }
                                finally { lease.Dispose(); }
                            }
                        }
                    }
                }
                catch (Exception ex) { ExceptionDiagnostics.Record("LocalIsolationNotifications.Rebuild", ex); }
                finally { QueueNotice(); }
            }
        });
    }

    private void SessionCreated(object sender, IAudioSessionControl session) => QueueNotice();

    private void QueueNotice()
    {
        if (Volatile.Read(ref _stopped) != 0 || Interlocked.Exchange(ref _noticeQueued, 1) != 0) return;
        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _noticeQueued, 0);
            if (Volatile.Read(ref _stopped) != 0) return;
            AndroidAudioHost.RevalidateLocalSilence();
        });
    }

    private void ClearSessions()
    {
        foreach (var (lease, muteChanged) in _devices)
        {
            var endpoint = lease.Device;
            try { if (muteChanged != null) endpoint.AudioEndpointVolume.OnVolumeNotification -= muteChanged; }
            catch (COMException) { }
            try { endpoint.AudioSessionManager.OnSessionCreated -= SessionCreated; }
            catch (COMException) { }
            finally { try { lease.Dispose(); } catch (COMException) { } }
        }
        _devices.Clear();
    }

    internal Task StopAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        return Task.Run(() =>
        {
            lock (_gate)
            {
                ClearSessions();
                if (_enumerator == null) return;
                try { _enumerator.Unregister(this); }
                catch (COMException) { }
                finally { _enumerator.Dispose(); _enumerator = null; }
            }
        });
    }

    public void OnDeviceStateChanged(string id, DeviceState state) => QueueRebuild();
    public void OnDeviceAdded(string id) => QueueRebuild();
    public void OnDeviceRemoved(string id) => QueueRebuild();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { if (flow == DataFlow.Render) QueueNotice(); }
    public void OnPropertyValueChanged(string id, PropertyKey key) { }
}
#endif
