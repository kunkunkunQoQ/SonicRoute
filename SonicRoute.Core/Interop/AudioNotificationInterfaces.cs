using System;
using System.Runtime.InteropServices;

namespace SonicRoute.Core.Interop
{
    [ComVisible(true), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated([MarshalAs(UnmanagedType.Interface)] object session);
    }

    [ComVisible(true), Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, ref Guid context);
        [PreserveSig] int OnGroupingParamChanged(ref Guid grouping, ref Guid context);
        [PreserveSig] int OnStateChanged(AudioSessionState state);
        [PreserveSig] int OnSessionDisconnected(int reason);
    }

    [ComVisible(true), Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notification);
    }

    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, DeviceState state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
    }
}
