namespace SonicRoute.Native.Audio;

internal sealed unsafe class Policy : IDisposable
{
    private readonly Com _factory;
    internal Policy()
    {
        using var name = new HString("Windows.Media.Internal.AudioPolicyConfig");
        Guid iid = OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)
            ? new("ab3d4648-e242-459f-b02f-541c70306324") : new("2a59116d-6c4f-45e0-a74f-707e3fef9258");
        nint result = 0;
        Com.Check(NativeMethods.RoGetActivationFactory(name.Handle,&iid,&result),"RoGetActivationFactory(AudioPolicyConfig)");
        _factory = new(result);
    }
    internal static string FullId(string id, Flow flow)
        => @"\\?\SWD#MMDEVAPI#" + id + (flow == Flow.Render ? "#{e6327cad-dcec-4949-ae8a-991e976a79d2}" : "#{2eef81be-33fa-4800-9670-1cd474972c3f}");
    internal string? Read(uint pid, Flow flow, Role role)
    {
        nint value = 0;
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint,uint,Flow,Role,nint*,int>)_factory.Slot(26))(_factory.Handle,pid,flow,role,&value);
            if (hr == unchecked((int)0x80070490)) return null;
            Com.Check(hr,"GetPersistedDefaultAudioEndpoint");
            if (value == 0) return null;
            uint length = 0;
            char* text = NativeMethods.WindowsGetStringRawBuffer(value,&length);
            return length == 0 ? null : new string(text,0,checked((int)length));
        }
        finally { NativeMethods.WindowsDeleteString(value); }
    }
    internal void Write(uint pid, Flow flow, Role role, string? fullId)
    {
        using var value = new HString(fullId);
        Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,Flow,Role,nint,int>)_factory.Slot(25))(_factory.Handle,pid,flow,role,value.Handle),"SetPersistedDefaultAudioEndpoint");
    }
    internal void ClearAll() => Com.Check(_factory.Call(27), "ClearAllPersistedApplicationDefaultEndpoints");
    public void Dispose() => _factory.Dispose();
}
