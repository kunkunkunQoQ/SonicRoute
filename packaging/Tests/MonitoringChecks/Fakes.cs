using System.Collections.Concurrent;
using SonicRoute.Core.Interop;

namespace MonitoringChecks
{
    internal static class Boundary
    {
        internal static readonly List<Device> Devices = new();
        internal static readonly ConcurrentDictionary<IMMNotificationClient, byte> Notifications = new();
        internal static int Enumerators, Scans, Activations, Releases, DeviceRegistrationFailures, CallbackReleases;
        [ThreadStatic] internal static int CallbackDepth;
        internal static void Reset()
        {
            Devices.Clear(); Notifications.Clear();
            Enumerators = Scans = Activations = Releases = DeviceRegistrationFailures = CallbackReleases = 0;
            System.Management.ManagementEventWatcher.Reset();
        }
        internal static IMMDeviceEnumerator CreateEnumerator() { Interlocked.Increment(ref Enumerators); return new Enumerator(); }
        internal static int Release(object value)
        { Interlocked.Increment(ref Releases); if (CallbackDepth > 0) Interlocked.Increment(ref CallbackReleases); return 0; }
        internal static Device Add(EDataFlow flow, params Session[] sessions)
        { var device = new Device(flow, Devices.Count.ToString(), sessions); Devices.Add(device); return device; }
        internal static void Callback(Action action) { CallbackDepth++; try { action(); } finally { CallbackDepth--; } }
        internal static void Topology() => Callback(() => { foreach (var cb in Notifications.Keys) cb.OnDeviceAdded("changed"); });
    }
    internal sealed class Enumerator : IMMDeviceEnumerator
    {
        public int EnumAudioEndpoints(EDataFlow flow, DeviceState state, out IMMDeviceCollection devices)
        { Interlocked.Increment(ref Boundary.Scans); devices = new Devices(Boundary.Devices.Where(d => d.Flow == flow).ToArray()); return 0; }
        public int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device)
        { device = Boundary.Devices.FirstOrDefault(d => d.Flow == flow)!; return device == null ? unchecked((int)0x80070490) : 0; }
        public int GetDevice(string id, out IMMDevice device) { device = Boundary.Devices.Single(d => d.Id == id); return 0; }
        public int RegisterEndpointNotificationCallback(IMMNotificationClient client)
        { if (Boundary.DeviceRegistrationFailures-- > 0) return -1; Boundary.Notifications[client] = 0; return 0; }
        public int UnregisterEndpointNotificationCallback(IMMNotificationClient client) { Boundary.Notifications.TryRemove(client, out _); return 0; }
    }
    internal sealed class Devices : IMMDeviceCollection
    {
        private readonly Device[] _devices;
        internal Devices(Device[] devices) => _devices = devices;
        public int GetCount(out uint count) { count = (uint)_devices.Length; return 0; }
        public int Item(uint i, out IMMDevice device) { device = _devices[i]; return 0; }
    }
    internal sealed class Device : IMMDevice
    {
        internal readonly EDataFlow Flow;
        internal readonly string Id;
        internal readonly Manager Manager;
        internal readonly Volume Volume = new();
        internal Device(EDataFlow flow, string id, Session[] sessions) { Flow = flow; Id = id; Manager = new Manager(sessions); }
        public int Activate(ref Guid iid, int context, IntPtr data, out object value)
        { Interlocked.Increment(ref Boundary.Activations); value = iid == typeof(IAudioEndpointVolume).GUID ? Volume : Manager; return 0; }
        public int GetId(out string id) { id = Id; return 0; }
        public int GetState(out DeviceState state) { state = DeviceState.ACTIVE; return 0; }
        public int OpenPropertyStore(int access, out IPropertyStore store) => throw new InvalidOperationException("No metadata access expected");
    }
    internal sealed class Manager : IAudioSessionManager2
    {
        internal readonly Session[] Snapshot;
        internal readonly ConcurrentDictionary<IAudioSessionNotification, byte> Callbacks = new();
        internal int RegistrationFailures, Registrations, Unregistrations;
        internal bool ThrowRegistration;
        internal Action? OnCount;
        internal Manager(Session[] sessions) => Snapshot = sessions;
        internal void Created(Session session) => Boundary.Callback(() => { foreach (var cb in Callbacks.Keys) cb.OnSessionCreated(session); });
        public int RegisterSessionNotification(IAudioSessionNotification n)
        {
            Interlocked.Increment(ref Registrations);
            if (ThrowRegistration) throw new InvalidOperationException("Simulated registration exception");
            if (RegistrationFailures-- > 0) return -1; Callbacks[(IAudioSessionNotification)n] = 0; return 0;
        }
        public int UnregisterSessionNotification(IAudioSessionNotification n) { Interlocked.Increment(ref Unregistrations); Callbacks.TryRemove(n, out _); return 0; }
        public int GetSessionEnumerator(out IAudioSessionEnumerator sessions) { sessions = new Sessions(this); return 0; }
        public int GetAudioSessionControl(ref Guid id, uint flags, out object control) => throw new NotSupportedException();
        public int GetSimpleAudioVolume(ref Guid id, uint flags, out object volume) => throw new NotSupportedException();
        public int RegisterDuckNotification(string id, object callback) => throw new NotSupportedException();
        public int UnregisterDuckNotification(object callback) => 0;
    }
    internal sealed class Sessions : IAudioSessionEnumerator
    {
        private readonly Manager _manager;
        internal Sessions(Manager manager) => _manager = manager;
        public int GetCount(out int count) { count = _manager.Snapshot.Length; _manager.OnCount?.Invoke(); return 0; }
        public int GetSession(int index, out IAudioSessionControl2 session) { session = _manager.Snapshot[index]; return 0; }
    }
    internal sealed class Session : IAudioSessionControl2, IAudioMeterInformation
    {
        internal readonly uint Pid;
        internal readonly string Instance = Guid.NewGuid().ToString();
        internal readonly ConcurrentDictionary<IAudioSessionEvents, byte> Callbacks = new();
        internal int PeakReads, StateReads, Registrations, Unregistrations, RegistrationFailures, StateFailures, PeakFailures;
        internal bool ThrowRegistration, IdentityFails;
        internal float Peak = .7f;
        internal AudioSessionState State = AudioSessionState.Active;
        internal Session(uint pid, AudioSessionState state = AudioSessionState.Active) { Pid = pid; State = state; }
        internal void SetState(AudioSessionState state) { State = state; Boundary.Callback(() => { foreach (var cb in Callbacks.Keys) cb.OnStateChanged(state); }); }
        internal void Disconnect() => Boundary.Callback(() => { foreach (var cb in Callbacks.Keys) cb.OnSessionDisconnected(0); });
        public int GetPeakValue(out float peak) { Interlocked.Increment(ref PeakReads); peak = Peak; return PeakFailures-- > 0 ? -1 : 0; }
        public int GetState(out AudioSessionState state) { Interlocked.Increment(ref StateReads); state = State; return StateFailures-- > 0 ? -1 : 0; }
        public int GetProcessId(out uint pid) { pid = Pid; return 0; }
        public int GetSessionInstanceIdentifier(out string id) { id = Instance; return IdentityFails ? -1 : 0; }
        public int RegisterAudioSessionNotification(IAudioSessionEvents cb)
        {
            Interlocked.Increment(ref Registrations); if (ThrowRegistration) throw new InvalidOperationException("Simulated state registration exception");
            if (RegistrationFailures-- > 0) return -1; Callbacks[(IAudioSessionEvents)cb] = 0; return 0;
        }
        public int UnregisterAudioSessionNotification(IAudioSessionEvents cb) { Interlocked.Increment(ref Unregistrations); Callbacks.TryRemove(cb, out _); return 0; }
        public int GetMeteringChannelCount(out uint count) { count = 2; return 0; }
        public int GetChannelsPeakValues(uint count, IntPtr peaks) => throw new NotSupportedException();
        public int QueryHardwareSupport(out uint support) { support = 0; return 0; }
        public int GetDisplayName(out string name) { name = "test"; return 0; }
        public int GetIconPath(out string path) { path = ""; return 0; }
        public int SetDisplayName(string name, ref Guid context) => throw new NotSupportedException();
        public int SetIconPath(string path, ref Guid context) => throw new NotSupportedException();
        public int GetGroupingParam(out Guid grouping) { grouping = Guid.Empty; return 0; }
        public int SetGroupingParam(ref Guid grouping, ref Guid context) => throw new NotSupportedException();
        public int GetSessionIdentifier(out string id) { id = Instance; return 0; }
        public int IsSystemSoundsSession() => 1;
        public int SetDuckingPreference(int value) => throw new NotSupportedException();
    }
    internal sealed class Volume : IAudioEndpointVolume
    {
        internal bool Muted;
        internal int RegistrationFailures, Registrations, Unregistrations, ReadFailures;
        internal readonly ConcurrentDictionary<IAudioEndpointVolumeCallback, byte> Callbacks = new();
        public int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb)
        { Registrations++; if (RegistrationFailures-- > 0) return -1; Callbacks[cb] = 0; return 0; }
        public int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb) { Unregistrations++; Callbacks.TryRemove(cb, out _); return 0; }
        public int GetMute(out int muted) { muted = Muted ? 1 : 0; return ReadFailures-- > 0 ? -1 : 0; }
        public int SetMute(int muted, ref Guid context) => throw new InvalidOperationException("No writes permitted");
        public int GetMasterVolumeLevelScalar(out float value) { value = .5f; return 0; }
        public int GetMasterVolumeLevel(out float value) { value = -6; return 0; }
        public int GetChannelCount(out uint count) { count = 2; return 0; }
        public int GetChannelVolumeLevel(uint channel, out float value) { value = -6; return 0; }
        public int GetChannelVolumeLevelScalar(uint channel, out float value) { value = .5f; return 0; }
        public int SetMasterVolumeLevel(float value, ref Guid context) => throw new InvalidOperationException("No writes permitted");
        public int SetMasterVolumeLevelScalar(float value, ref Guid context) => throw new InvalidOperationException("No writes permitted");
        public int SetChannelVolumeLevel(uint channel, float value, ref Guid context) => throw new InvalidOperationException("No writes permitted");
        public int SetChannelVolumeLevelScalar(uint channel, float value, ref Guid context) => throw new InvalidOperationException("No writes permitted");
    }
}
namespace SonicRoute.Core
{
    internal static class AudioService { internal static void InvalidateAppsCache() { } internal static void InvalidateDeviceCache(EDataFlow? flow = null) { } }
    internal static class SessionVolumeService { internal static void InvalidateCache() { } }
    internal static class GlobalMicMuteService { internal static bool IsAnyMuted(bool track) => false; }
}
namespace SonicRoute { internal static class AutoRuleScheduler { internal static void Log(string message) { } } }
namespace System.Management
{
    public sealed class ManagementBaseObject : IDisposable
    { public object? this[string key] => "test.exe"; public void Dispose() { } }
    public sealed class EventArrivedEventArgs : EventArgs { public ManagementBaseObject NewEvent { get; } = new(); }
    public sealed class StoppedEventArgs : EventArgs { }
    public sealed class ManagementEventWatcher : IDisposable
    {
        internal static readonly ConcurrentBag<ManagementEventWatcher> Instances = new();
        internal static int Failures, Starts, Disposals;
        public event EventHandler<EventArrivedEventArgs>? EventArrived;
        public event EventHandler<StoppedEventArgs>? Stopped;
        internal static void Reset() { while (Instances.TryTake(out _)) { } Failures = Starts = Disposals = 0; }
        public ManagementEventWatcher(string query) { if (query.Contains("WITHIN")) throw new Exception("Hidden polling"); Instances.Add(this); }
        public void Start() { Interlocked.Increment(ref Starts); if (Interlocked.Decrement(ref Failures) >= 0) throw new Exception("WMI unavailable"); }
        internal void Emit() => EventArrived?.Invoke(this, new EventArrivedEventArgs());
        internal void StopUnexpectedly() => Stopped?.Invoke(this, new StoppedEventArgs());
        public void Dispose() { Interlocked.Increment(ref Disposals); }
    }
}
