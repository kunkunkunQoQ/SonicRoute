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

    internal static WasapiDeviceLease Open(string? endpointId)
    {
        object enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
        IntPtr pointer = IntPtr.Zero;
        object? endpoint = null;
        try
        {
            var api = (IEndpointEnumerator)enumerator;
            Marshal.ThrowExceptionForHR(string.IsNullOrEmpty(endpointId)
                ? api.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out pointer)
                : api.GetDevice(endpointId, out pointer));
            endpoint = Marshal.GetUniqueObjectForIUnknown(pointer);
            return new WasapiDeviceLease(endpoint);
        }
        catch { if (endpoint != null) Marshal.ReleaseComObject(endpoint); throw; }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            Marshal.ReleaseComObject(enumerator);
        }
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
        object enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
        try
        {
            Marshal.ThrowExceptionForHR(((IEndpointEnumerator)enumerator).RegisterEndpointNotificationCallback(callback));
            return new NotificationLease(enumerator, callback);
        }
        catch { Marshal.ReleaseComObject(enumerator); throw; }
    }

    private sealed class NotificationLease(object enumerator, IMMNotificationClient callback) : IDisposable
    {
        private object? _enumerator = enumerator;
        public void Dispose()
        {
            object? current = Interlocked.Exchange(ref _enumerator, null);
            if (current == null) return;
            try { ((IEndpointEnumerator)current).UnregisterEndpointNotificationCallback(callback); }
            finally { Marshal.ReleaseComObject(current); }
        }
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
