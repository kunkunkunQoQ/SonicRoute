#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using SonicRoute.AndroidAudio;

namespace SonicRoute;

// WASAPI captures the process tree. Watch its dormant sessions on other endpoints:
// resuming an old session doesn't generate a new session-created notification.
internal sealed class WindowsLinkPlaybackWatch(int processId, string routedEndpoint, Action revoke)
    : ILocalPlaybackWatch, IAudioSessionEventsHandler
{
    internal Action? CheckReservedOutput { get; init; }
    private readonly Dictionary<string, AudioSessionControl> _sessions = new(StringComparer.Ordinal);

    internal static bool ContainsProcess(int root, int candidate) =>
        root == candidate || candidate > 0 && AudioProcessTree.DescendsFrom(candidate, root);

    // Windows can expose a process-loopback tap as an active session on the
    // source's render endpoint. An unrecognized session remains a potential renderer.
    internal static bool IsOwnLoopback(AudioSessionControl session, int pid) =>
        pid == Environment.ProcessId && session.GetSessionIdentifier.EndsWith(
            "%b{" + PcProcessLoopbackCapture.CaptureSessionId + "}", StringComparison.OrdinalIgnoreCase);

    public void Refresh()
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        using var devices = new WasapiDeviceLease.Enumerator();
        foreach (var lease in devices.ActiveRender())
        using (lease)
        {
            var device = lease.Device;
            bool onTarget = string.Equals(device.ID, routedEndpoint, StringComparison.OrdinalIgnoreCase);
            if (onTarget && CheckReservedOutput == null) continue;
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                bool retained = false;
                try
                {
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                    int pid = (int)session.GetProcessID;
                    if (IsOwnLoopback(session, pid)) continue;
                    bool ownProcess = ContainsProcess(processId, pid);
                    if (onTarget == ownProcess) continue;
                    string key = session.GetSessionInstanceIdentifier;
                    present.Add(key);
                    if (!_sessions.ContainsKey(key))
                    {
                        session.RegisterEventClient(this);
                        _sessions.Add(key, session);
                        retained = true;
                    }
                    if (!onTarget && session.State == AudioSessionState.AudioSessionStateActive) revoke();
                }
                finally { if (!retained) session.Dispose(); }
            }
        }
        foreach (var key in _sessions.Keys.Where(key => !present.Contains(key)).ToArray()) Remove(key);
    }

    public void OnStateChanged(AudioSessionState state)
    {
        // Unregister and COM enumeration run under the lease gate. Keep WASAPI's callback nonblocking.
        if (state == AudioSessionState.AudioSessionStateActive)
            ThreadPool.QueueUserWorkItem(static action => action(), CheckReservedOutput ?? revoke, preferLocal: false);
    }
    public void OnVolumeChanged(float volume, bool isMuted) { }
    public void OnDisplayNameChanged(string name) { }
    public void OnIconPathChanged(string path) { }
    public void OnChannelVolumeChanged(uint count, nint volumes, uint channel) { }
    public void OnGroupingParamChanged(ref Guid groupingId) { }
    public void OnSessionDisconnected(AudioSessionDisconnectReason reason) { }

    private void Remove(string key)
    {
        if (!_sessions.Remove(key, out var session)) return;
        try { session.UnRegisterEventClient(this); }
        catch (COMException) { }
        finally { session.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var key in _sessions.Keys.ToArray()) Remove(key);
    }
}
#endif
