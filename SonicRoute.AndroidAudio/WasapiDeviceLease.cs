using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SonicRoute.AndroidAudio;

/// <summary>
/// Compatibility boundary for pinned NAudio.Wasapi 2.2.1. Its enumerator constructor
/// casts a COM coclass which conflicts with Core's import of the same CLSID.
/// Activate/query interfaces instead, retaining a unique endpoint RCW we own.
/// </summary>
internal sealed class WasapiDeviceLease : IDisposable
{
    private object? _endpoint;
    internal MMDevice Device { get; }
    private static readonly ConstructorInfo DeviceConstructor = typeof(MMDevice)
        .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(c => c.GetParameters() is [var p]
            && p.ParameterType.GUID == new Guid("D666063F-1587-4E43-81F1-B948E807363F"));

    private WasapiDeviceLease(object endpoint)
    {
        _endpoint = endpoint;
        // The only reflection call: NAudio's MMDevice(IMMDevice) is internal.
        // Fail closed if the pinned dependency changes; do not patch Core's COM types.
        Device = (MMDevice)DeviceConstructor.Invoke(new[] { endpoint });
    }

    private static object Unique(int hr, nint pointer)
    {
        try
        {
            Marshal.ThrowExceptionForHR(hr);
            return Marshal.GetUniqueObjectForIUnknown(pointer);
        }
        finally { if (pointer != 0) Marshal.Release(pointer); }
    }

    private static WasapiDeviceLease Wrap(int hr, nint pointer)
    {
        object endpoint = Unique(hr, pointer);
        try { return new WasapiDeviceLease(endpoint); }
        catch { Marshal.ReleaseComObject(endpoint); throw; }
    }

    internal sealed class Enumerator : IDisposable
    {
        private static readonly Guid ClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid InterfaceId = typeof(IEndpointEnumerator).GUID;
        private IEndpointEnumerator? _api;
        private nint _mta;

        internal Enumerator()
        {
            Marshal.ThrowExceptionForHR(Win32Native.CoIncrementMTAUsage(out _mta));
            try
            {
                // Avoid introducing either library's imported coclass into the shared RCW cache.
                int hr = Win32Native.CoCreateInstance(in ClassId, 0, Win32Native.CLSCTX_INPROC_SERVER, in InterfaceId, out nint pointer);
                _api = (IEndpointEnumerator)Unique(hr, pointer);
            }
            catch { Dispose(); throw; }
        }

        private IEndpointEnumerator Api => _api ?? throw new ObjectDisposedException(nameof(Enumerator));

        internal WasapiDeviceLease GetDevice(string id)
        {
            int hr = Api.GetDevice(id, out nint device);
            return Wrap(hr, device);
        }

        internal WasapiDeviceLease? TryDefault(Role role)
        {
            int hr = Api.GetDefaultAudioEndpoint(DataFlow.Render, role, out nint device);
            if (hr == unchecked((int)0x80070490)) return null; // E_NOTFOUND: no current default for this role.
            return Wrap(hr, device);
        }

        internal IEnumerable<WasapiDeviceLease> ActiveRender()
        {
            int hr = Api.EnumAudioEndpoints(DataFlow.Render, (int)DeviceState.Active, out nint pointer);
            var collection = (IEndpointCollection)Unique(hr, pointer);
            try
            {
                Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
                for (uint i = 0; i < count; i++)
                {
                    hr = collection.Item(i, out nint device);
                    yield return Wrap(hr, device);
                }
            }
            finally { Marshal.ReleaseComObject(collection); }
        }

        internal void Register(IMMNotificationClient callback) =>
            Marshal.ThrowExceptionForHR(Api.RegisterEndpointNotificationCallback(callback));

        internal void Unregister(IMMNotificationClient callback) =>
            Marshal.ThrowExceptionForHR(Api.UnregisterEndpointNotificationCallback(callback));

        public void Dispose()
        {
            var api = Interlocked.Exchange(ref _api, null);
            if (api != null) Marshal.ReleaseComObject(api);
            nint mta = Interlocked.Exchange(ref _mta, 0);
            if (mta != 0) Win32Native.CoDecrementMTAUsage(mta);
        }
    }

    internal static WasapiDeviceLease Open(string? endpointId)
    {
        using var enumerator = new Enumerator();
        return !string.IsNullOrEmpty(endpointId) ? enumerator.GetDevice(endpointId)
            : enumerator.TryDefault(Role.Multimedia) ?? throw new COMException("No default playback device.", unchecked((int)0x80070490));
    }

    public void Dispose()
    {
        object? endpoint = Interlocked.Exchange(ref _endpoint, null);
        if (endpoint == null) return;
        try { Device.Dispose(); }
        finally { Marshal.ReleaseComObject(endpoint); }
    }

    internal static IDisposable ObserveDefaultOutput(IMMNotificationClient callback)
    {
        var enumerator = new Enumerator();
        try { enumerator.Register(callback); return new NotificationLease(enumerator, callback); }
        catch { enumerator.Dispose(); throw; }
    }

    private sealed class NotificationLease(Enumerator enumerator, IMMNotificationClient callback) : IDisposable
    {
        private Enumerator? _enumerator = enumerator;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _enumerator, null);
            if (current == null) return;
            try { current.Unregister(callback); }
            finally { current.Dispose(); }
        }
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out nint device);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow flow, int states, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, Role role, out IntPtr device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient callback);
    }
}
