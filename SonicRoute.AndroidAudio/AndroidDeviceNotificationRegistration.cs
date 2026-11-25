using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SonicRoute.Native.Audio;
using NativePropertyKey = SonicRoute.Native.Audio.PropertyKey;

namespace SonicRoute.AndroidAudio;

[GeneratedComClass]
internal sealed partial class AndroidDeviceNotificationSink(DefaultOutputNotifications.DeviceCallback callback) : IDeviceNotifications
{
    public int OnDeviceStateChanged(nint id, uint state) => Dispatch(() => callback.OnDeviceStateChanged("", state));
    public int OnDeviceAdded(nint id) => Dispatch(() => callback.OnDeviceAdded(""));
    public int OnDeviceRemoved(nint id) => Dispatch(() => callback.OnDeviceRemoved(""));
    public int OnPropertyValueChanged(nint id, NativePropertyKey key) => 0;
    public int OnDefaultDeviceChanged(int flow, int role, nint id) =>
        Dispatch(() => callback.OnDefaultDeviceChanged(flow, role, Marshal.PtrToStringUni(id) ?? ""));

    private static int Dispatch(Action handler)
    {
        try { handler(); return 0; }
        catch (Exception ex) { return ex.HResult; }
    }
}

internal sealed class AndroidDeviceNotificationRegistration(DefaultOutputNotifications.DeviceCallback callback) : IDisposable
{
    private readonly IDisposable _registration = DeviceNotificationHub.Subscribe(new AndroidDeviceNotificationSink(callback));
    public void Dispose() => _registration.Dispose();
}
