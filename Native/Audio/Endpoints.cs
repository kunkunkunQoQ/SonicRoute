using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SonicRoute.Native.Audio;

internal enum Flow { Render, Capture }
internal enum Role { Console, Multimedia, Communications }
internal sealed record Endpoint(string Id, string Name);
internal sealed record SessionInfo(uint ProcessId, uint State);

internal sealed unsafe class Endpoints : IDisposable
{
    internal readonly Com Enumerator = Com.Create(new("BCDE0395-E52F-467C-8E3D-C4579291692E"), new("A95664D2-9614-4F35-A746-DE8DB63617E6"));
    internal static readonly Guid AudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    internal static readonly Guid SessionManager = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    internal static readonly Guid VolumeInterface = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    internal static readonly Guid SessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    internal static readonly Guid SimpleVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    internal static Com Activate(Com endpoint, Guid iid)
    {
        nint result = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)endpoint.Slot(3))(endpoint.Handle,&iid,23,0,&result), "IMMDevice.Activate");
        return new(result);
    }
    internal static string Id(Com device)
    {
        nint value = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)device.Slot(5))(device.Handle,&value), "IMMDevice.GetId");
        try { return Marshal.PtrToStringUni(value)!; }
        finally { Marshal.FreeCoTaskMem(value); }
    }
    private static string ReadName(Com device)
    {
        nint value = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)device.Slot(4))(device.Handle,0,&value), "OpenPropertyStore");
        using var store = new Com(value);
        PropertyKey key = new() { Format = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
        PropVariant name = default;
        try
        {
            Com.Check(((delegate* unmanaged[Stdcall]<nint,PropertyKey*,PropVariant*,int>)store.Slot(5))(store.Handle,&key,&name),"GetValue(friendly name)");
            return name.Type == 31 ? Marshal.PtrToStringUni(name.Value) ?? "" : "";
        }
        finally { NativeMethods.PropVariantClear(&name); }
    }
    internal List<Endpoint> Active(Flow flow)
    {
        nint ptr = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,Flow,uint,nint*,int>)Enumerator.Slot(3))(Enumerator.Handle,flow,1,&ptr), "EnumAudioEndpoints");
        using var list = new Com(ptr);
        uint count = list.UInt32(3);
        var result = new List<Endpoint>();
        for (uint i = 0; i < count; i++)
        {
            Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)list.Slot(4))(list.Handle,i,&ptr), "endpoint collection item");
            using var device = new Com(ptr);
            result.Add(new(Id(device),ReadName(device)));
        }
        return result;
    }
    internal Com Default(Flow flow, Role role)
    {
        nint ptr = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,Flow,Role,nint*,int>)Enumerator.Slot(4))(Enumerator.Handle,flow,role,&ptr), "GetDefaultAudioEndpoint");
        return new(ptr);
    }
    internal Com Open(string id)
    {
        nint ptr = 0;
        fixed (char* name = id)
            Com.Check(((delegate* unmanaged[Stdcall]<nint,char*,nint*,int>)Enumerator.Slot(5))(Enumerator.Handle,name,&ptr),"GetDevice");
        return new(ptr);
    }
    internal static List<SessionInfo> Sessions(Com device)
    {
        using var manager = Activate(device, SessionManager);
        nint ptr = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)manager.Slot(5))(manager.Handle,&ptr), "GetSessionEnumerator");
        using var sessions = new Com(ptr);
        uint count = sessions.UInt32(3);
        var result = new List<SessionInfo>();
        for (uint i = 0; i < count; i++)
        {
            Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)sessions.Slot(4))(sessions.Handle,i,&ptr), "GetSession");
            using var control = new Com(ptr);
            using var control2 = control.Query(SessionControl2);
            result.Add(new(control2.UInt32(14), control.UInt32(3)));
        }
        return result;
    }

    internal static void SetDefault(string endpointId, Role role)
    {
        using var policy = Com.Create(new("870af99c-171d-4f9e-af0d-e63df40c2bc9"),new("f8679f50-850a-41cf-9c72-430f290290c8"));
        fixed (char* id = endpointId)
            Com.Check(((delegate* unmanaged[Stdcall]<nint,char*,Role,int>)policy.Slot(13))(policy.Handle,id,role),"SetDefaultEndpoint");
    }
    internal static (float Level, bool Muted) ReadVolume(Com volume, bool endpoint)
        => (volume.Scalar(endpoint ? 9 : 4),volume.UInt32(endpoint ? 15 : 6) != 0);
    internal static void SetVolume(Com volume, bool endpoint, float level, Guid context)
        => Com.Check(((delegate* unmanaged[Stdcall]<nint,float,Guid*,int>)volume.Slot(endpoint ? 7 : 3))(volume.Handle,level,&context),"set volume");
    internal static void SetMute(Com volume, bool endpoint, bool mute, Guid context)
        => Com.Check(((delegate* unmanaged[Stdcall]<nint,int,Guid*,int>)volume.Slot(endpoint ? 14 : 5))(volume.Handle,mute ? 1 : 0,&context),"set mute");
    public void Dispose() => Enumerator.Dispose();
}
