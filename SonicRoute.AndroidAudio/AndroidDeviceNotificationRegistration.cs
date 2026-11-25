using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NAudio.CoreAudioApi;
using SonicRoute.Native.Audio;
using NativePropertyKey = SonicRoute.Native.Audio.PropertyKey;

namespace SonicRoute.AndroidAudio;

[GeneratedComClass]
internal sealed partial class AndroidDeviceNotificationSink(DefaultOutputNotifications.DeviceCallback callback) : IDeviceNotifications
{
    public int OnDeviceStateChanged(nint id, uint state) => Dispatch(() => callback.OnDeviceStateChanged("", (DeviceState)state));
    public int OnDeviceAdded(nint id) => Dispatch(() => callback.OnDeviceAdded(""));
    public int OnDeviceRemoved(nint id) => Dispatch(() => callback.OnDeviceRemoved(""));
    public int OnPropertyValueChanged(nint id, NativePropertyKey key) => 0;
    public int OnDefaultDeviceChanged(int flow, int role, nint id) =>
        Dispatch(() => callback.OnDefaultDeviceChanged((DataFlow)flow, (NAudio.CoreAudioApi.Role)role, Marshal.PtrToStringUni(id) ?? ""));

    private static int Dispatch(Action handler)
    {
        try { handler(); return 0; }
        catch (Exception ex) { return ex.HResult; }
    }
}

internal sealed unsafe class AndroidDeviceNotificationRegistration : IDisposable
{
    private readonly Endpoints _devices;
    private nint _sink;
    private nint _mta;
    private bool _closed;

    internal AndroidDeviceNotificationRegistration(DefaultOutputNotifications.DeviceCallback callback)
    {
        Com.Check(Win32Native.CoIncrementMTAUsage(out _mta), "CoIncrementMTAUsage");
        try
        {
            _devices = new Endpoints();
            _sink = (nint)ComInterfaceMarshaller<IDeviceNotifications>.ConvertToUnmanaged(new AndroidDeviceNotificationSink(callback));
            Com.Check(Register(6), "IMMDeviceEnumerator.RegisterEndpointNotificationCallback");
        }
        catch
        {
            if (_sink != 0) ComInterfaceMarshaller<IDeviceNotifications>.Free((void*)_sink);
            _devices?.Dispose();
            Win32Native.CoDecrementMTAUsage(_mta);
            throw;
        }
    }

    private int Register(int slot) => ((delegate* unmanaged[Stdcall]<nint,nint,int>)_devices.Enumerator.Slot(slot))
        (_devices.Enumerator.Handle, _sink);

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { Com.Check(Register(7), "IMMDeviceEnumerator.UnregisterEndpointNotificationCallback"); }
        finally
        {
            ComInterfaceMarshaller<IDeviceNotifications>.Free((void*)_sink);
            _sink = 0;
            _devices.Dispose();
            Win32Native.CoDecrementMTAUsage(_mta);
        }
    }
}

