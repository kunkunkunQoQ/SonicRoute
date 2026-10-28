using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace AudioPathChecks
{
    internal static class Boundary
    {
        internal static readonly List<Device> Devices = new();
        internal static int Enumerators, Scans, Activations, Lookups, Properties, Releases, Policies, PolicyDisposals;
        internal static int ScanFailure, CountFailure;
        internal static Action? BeforeScan;
        internal static readonly HashSet<int> RouteFailures = new();
        internal static readonly List<int> PolicyRoles = new();
        internal static int PolicyFailureRole = -1;
        internal static IMMDeviceEnumerator CreateEnumerator() { Enumerators++; return new Enumerator(); }
        internal static IPolicyConfig CreatePolicyClient() { Policies++; return new Policy(); }
        internal static int Release(object value) { Releases++; return 0; }
        internal static void Reset()
        {
            Devices.Clear(); RouteFailures.Clear(); PolicyRoles.Clear();
            ScanFailure = CountFailure = 0; PolicyFailureRole = -1; BeforeScan = null;
            AudioService.InvalidateAppsCache(); AudioService.InvalidateDeviceCache();
            SessionVolumeService.Refresh(true);
#if !BASELINE
            SessionVolumeService.InvalidateCache();
#endif
            ClearCounts();
        }
        internal static void ClearCounts()
        { Enumerators = Scans = Activations = Lookups = Properties = Releases = Policies = PolicyDisposals = 0; }
        internal static Device Add(EDataFlow flow, params Session[] sessions)
        {
            var device = new Device(flow, Devices.Count.ToString(), sessions);
            Devices.Add(device); return device;
        }
    }

    internal sealed class Enumerator : IMMDeviceEnumerator
    {
        public int EnumAudioEndpoints(EDataFlow flow, DeviceState state, out IMMDeviceCollection devices)
        { Boundary.BeforeScan?.Invoke(); Boundary.Scans++; devices = new Collection(Boundary.Devices.Where(d => d.Flow == flow).ToArray()); return Boundary.ScanFailure; }
        public int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device)
        { device = Boundary.Devices.FirstOrDefault(d => d.Flow == flow)!; return device == null ? -1 : 0; }
        public int GetDevice(string id, out IMMDevice device)
        { Boundary.Lookups++; device = Boundary.Devices.FirstOrDefault(d => d.Id == id)!; return device == null ? -1 : 0; }
        public int RegisterEndpointNotificationCallback(IMMNotificationClient client) => 0;
        public int UnregisterEndpointNotificationCallback(IMMNotificationClient client) => 0;
    }
    internal sealed class Collection : IMMDeviceCollection
    {
        private readonly Device[] _devices;
        internal Collection(Device[] devices) => _devices = devices;
        public int GetCount(out uint count) { count = (uint)_devices.Length; return Boundary.CountFailure; }
        public int Item(uint i, out IMMDevice device) { device = _devices[i]; return 0; }
    }
    internal sealed class Device : IMMDevice
    {
        internal readonly EDataFlow Flow;
        internal readonly string Id;
        internal readonly Endpoint Volume = new();
        internal Session[] Sessions;
        internal bool ActivationFails;
        internal Device(EDataFlow flow, string id, Session[] sessions) { Flow = flow; Id = id; Sessions = sessions; }
        public int Activate(ref Guid iid, int context, IntPtr parameters, out object value)
        {
            Boundary.Activations++;
            value = iid == typeof(IAudioEndpointVolume).GUID ? Volume : new Manager(Sessions);
            return ActivationFails ? -1 : 0;
        }
        public int OpenPropertyStore(int access, out IPropertyStore store)
        { Boundary.Properties++; store = new Properties(); return 0; }
        public int GetId(out string id) { id = Id; return 0; }
        public int GetState(out DeviceState state) { state = DeviceState.ACTIVE; return 0; }
    }
    internal sealed class Properties : IPropertyStore
    {
        public int GetCount(out uint count) { count = 1; return 0; }
        public int GetAt(uint i, out PROPERTYKEY key) { key = PropertyKeys.PKEY_Device_FriendlyName; return 0; }
        public int GetValue(ref PROPERTYKEY key, out PROPVARIANT value)
        { value = new PROPVARIANT { vt = 31, pwszVal = Marshal.StringToCoTaskMemUni("设备 名称") }; return 0; }
        public int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value) => throw new InvalidOperationException("No property writes");
        public int Commit() => throw new InvalidOperationException("No property writes");
    }
    internal sealed class Endpoint : IAudioEndpointVolume
    {
        internal float Volume = .5f;
        internal bool Muted, WriteFails, IgnoreWrite, ThrowWrite;
        internal int ReadFailures, VolumeReadFailures, Writes;
        internal Action? AfterMuteWrite;
        public int GetMute(out int state) { state = Muted ? 1 : 0; return ReadFailures-- > 0 ? -1 : 0; }
        public int SetMute(int state, ref Guid context)
        { Writes++; if (ThrowWrite) throw new COMException(); if (WriteFails) return -1; if (!IgnoreWrite) Muted = state != 0; AfterMuteWrite?.Invoke(); return 0; }
        public int GetMasterVolumeLevelScalar(out float volume) { volume = Volume; return VolumeReadFailures-- > 0 ? -1 : 0; }
        public int SetMasterVolumeLevelScalar(float volume, ref Guid context)
        { Writes++; if (ThrowWrite) throw new COMException(); if (WriteFails) return -1; if (!IgnoreWrite) Volume = volume; return 0; }
        public int RegisterControlChangeNotify(IAudioEndpointVolumeCallback c) => 0;
        public int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback c) => 0;
        public int GetChannelCount(out uint count) { count = 1; return 0; }
        public int SetMasterVolumeLevel(float v, ref Guid c) => 0;
        public int GetMasterVolumeLevel(out float v) { v = 0; return 0; }
        public int SetChannelVolumeLevel(uint n, float v, ref Guid c) => 0;
        public int SetChannelVolumeLevelScalar(uint n, float v, ref Guid c) => 0;
        public int GetChannelVolumeLevel(uint n, out float v) { v = 0; return 0; }
        public int GetChannelVolumeLevelScalar(uint n, out float v) { v = 0; return 0; }
    }
    internal sealed class Manager : IAudioSessionManager2
    {
        private readonly Session[] _sessions;
        internal Manager(Session[] sessions) => _sessions = sessions;
        public int GetSessionEnumerator(out IAudioSessionEnumerator sessions) { sessions = new Sessions(_sessions); return 0; }
        public int GetAudioSessionControl(ref Guid g, uint f, out object s) { s = null!; return -1; }
        public int GetSimpleAudioVolume(ref Guid g, uint f, out object s) { s = null!; return -1; }
        public int RegisterSessionNotification(object n) => throw new InvalidOperationException("No new subscriptions");
        public int UnregisterSessionNotification(object n) => 0;
#if !BASELINE
        public int RegisterSessionNotification(IAudioSessionNotification n) => RegisterSessionNotification((object)n);
        public int UnregisterSessionNotification(IAudioSessionNotification n) => UnregisterSessionNotification((object)n);
#endif
        public int RegisterDuckNotification(string id, object n) => throw new InvalidOperationException("No new subscriptions");
        public int UnregisterDuckNotification(object n) => 0;
    }
    internal sealed class Sessions : IAudioSessionEnumerator
    {
        private readonly Session[] _sessions;
        internal Sessions(Session[] sessions) => _sessions = sessions;
        public int GetCount(out int count) { count = _sessions.Length; return 0; }
        public int GetSession(int i, out IAudioSessionControl2 session) { session = _sessions[i]; return 0; }
    }
    internal sealed class Session : IAudioSessionControl2, ISimpleAudioVolume
    {
        internal readonly uint Pid;
        internal float Volume = .6f;
        internal bool Muted, WriteFails, IgnoreWrite;
        internal int ReadFailures, VolumeReadFailures, Writes, IconReads, MuteReads;
        internal AudioSessionState State = AudioSessionState.Active;
        internal Action? OnReadVolume;
        internal Action? OnReadMute;
        internal Action? AfterMuteWrite;
        internal Session(uint pid) => Pid = pid;
        public int GetMasterVolume(out float v) { OnReadVolume?.Invoke(); v = Volume; return VolumeReadFailures-- > 0 ? -1 : 0; }
        public int SetMasterVolume(float v, ref Guid c) { Writes++; if (WriteFails) return -1; if (!IgnoreWrite) Volume = v; return 0; }
        public int GetMute(out int v) { MuteReads++; OnReadMute?.Invoke(); v = Muted ? 1 : 0; return ReadFailures-- > 0 ? -1 : 0; }
        public int SetMute(int v, ref Guid c) { Writes++; if (WriteFails) return -1; if (!IgnoreWrite) Muted = v != 0; AfterMuteWrite?.Invoke(); return 0; }
        public int GetState(out AudioSessionState state) { state = State; return 0; }
        public int GetProcessId(out uint pid) { pid = Pid; return 0; }
        public int GetDisplayName(out string name) { name = "应用 " + Pid; return 0; }
        public int GetIconPath(out string path) { IconReads++; path = "C:\\图标 空格\\app.exe"; return 0; }
        public int SetDisplayName(string s, ref Guid c) => 0;
        public int SetIconPath(string s, ref Guid c) => 0;
        public int GetGroupingParam(out Guid g) { g = Guid.Empty; return 0; }
        public int SetGroupingParam(ref Guid g, ref Guid c) => 0;
        public int RegisterAudioSessionNotification(object n) => throw new InvalidOperationException("No new subscriptions");
        public int UnregisterAudioSessionNotification(object n) => 0;
#if !BASELINE
        public int RegisterAudioSessionNotification(IAudioSessionEvents n) => RegisterAudioSessionNotification((object)n);
        public int UnregisterAudioSessionNotification(IAudioSessionEvents n) => UnregisterAudioSessionNotification((object)n);
#endif
        public int GetSessionIdentifier(out string id) { id = "session"; return 0; }
        public int GetSessionInstanceIdentifier(out string id) { id = "instance"; return 0; }
        public int IsSystemSoundsSession() => 1;
        public int SetDuckingPreference(int value) => throw new InvalidOperationException("No ducking changes");
    }
    internal sealed class Policy : IPolicyConfig
    {
#if BASELINE
        public int SetDefaultEndpoint(string id, EDataFlow role)
#else
        public int SetDefaultEndpoint(string id, ERole role)
#endif
        { Boundary.PolicyRoles.Add((int)role); return (int)role == Boundary.PolicyFailureRole ? -1 : 0; }
        public int GetMixFormat(IntPtr a, IntPtr b) => 0;
        public int GetDeviceFormat(IntPtr a, IntPtr b, IntPtr c) => 0;
        public int ResetDeviceFormat(IntPtr a, IntPtr b) => 0;
        public int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c, IntPtr d) => 0;
        public int GetProcessingPeriod(IntPtr a, IntPtr b, IntPtr c, IntPtr d) => 0;
        public int SetProcessingPeriod(IntPtr a, IntPtr b, IntPtr c) => 0;
        public int GetShareMode(IntPtr a, IntPtr b, IntPtr c) => 0;
        public int SetShareMode(IntPtr a, IntPtr b, IntPtr c) => 0;
        public int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c, IntPtr d) => 0;
        public int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c, IntPtr d) => 0;
        public int SetEndpointVisibility(IntPtr a, IntPtr b, int c) => 0;
    }
}

namespace SonicRoute.Core
{
    internal static class ForegroundAppService
    { internal static string GetProcessNameSafe(int pid) => "app" + pid; }
}
namespace SonicRoute.Core.Interop
{
    public sealed class AudioPolicyConfig : IDisposable
    {
        public AudioPolicyConfig(EDataFlow flow) { AudioPathChecks.Boundary.Policies++; }
        public string? GetDefaultEndPoint(int pid)
        { if (AudioPathChecks.Boundary.RouteFailures.Contains(pid)) throw new COMException(); return "endpoint" + pid; }
        public int SetDefaultEndPoint(string? id, int pid) => throw new InvalidOperationException("No routing writes");
        public int ClearAllPersistedApplicationDefaultEndpoints() => throw new InvalidOperationException("No reset writes");
        public static string EnsureFullDeviceId(string id, EDataFlow flow) => id;
        public static string UnpackDeviceId(string id) => id;
        public void Dispose() { AudioPathChecks.Boundary.PolicyDisposals++; }
    }
}
