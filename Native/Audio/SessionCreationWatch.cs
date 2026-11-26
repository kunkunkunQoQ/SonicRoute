using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SonicRoute.Native.Audio;

[GeneratedComInterface, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
internal partial interface ISessionCreated
{
    [PreserveSig] int OnSessionCreated(nint borrowedSession);
}

[GeneratedComClass]
internal sealed unsafe partial class SessionCreatedSink(Action<int> notify, bool includeSystem) : ISessionCreated
{
    private Action<int>? _notify = notify;
    internal void Stop() => Interlocked.Exchange(ref _notify, null);
    public int OnSessionCreated(nint borrowedSession)
    {
        var emit = Volatile.Read(ref _notify);
        if (emit == null || borrowedSession == 0) return 0;
        Guid iid = Endpoints.SessionControl2;
        nint owned = 0;
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Com.Slot(borrowedSession,0))(borrowedSession,&iid,&owned);
            if (hr < 0 || owned == 0) return 0;
            uint pid = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint,uint*,int>)Com.Slot(owned,14))(owned,&pid);
            if (hr >= 0 && pid <= int.MaxValue && (pid > 0 || includeSystem)) emit((int)pid);
        }
        catch { /* COM callbacks never unwind managed exceptions into the audio engine. */ }
        finally { Com.Release(owned); }
        return 0;
    }
}

internal sealed unsafe class SessionCreationWatch : IDisposable
{
    private readonly Com _manager;
    private readonly SessionCreatedSink _sink;
    private nint _callback;
    internal SessionCreationWatch(Com device, Action<int> notify, bool includeSystem = false)
    {
        _manager = Endpoints.Activate(device, Endpoints.SessionManager);
        _sink = new(notify, includeSystem);
        bool registered = false;
        try
        {
            _callback = (nint)ComInterfaceMarshaller<ISessionCreated>.ConvertToUnmanaged(_sink);
            Com.Check(Change(6), "RegisterSessionNotification");
            registered = true;
            nint enumerator = 0;
            Com.Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)_manager.Slot(5))(_manager.Handle,&enumerator), "GetSessionEnumerator");
            using var sessions = new Com(enumerator);
            _ = sessions.UInt32(3); // GetCount enables new-session notifications after registration.
        }
        catch
        {
            _sink.Stop();
            if (registered) Change(7);
            Com.Release(_callback);
            _manager.Dispose();
            throw;
        }
    }
    private int Change(int slot) =>
        ((delegate* unmanaged[Stdcall]<nint,nint,int>)_manager.Slot(slot))(_manager.Handle,_callback);
    public void Dispose()
    {
        if (_callback == 0) return;
        _sink.Stop();
        try { Com.Check(Change(7), "UnregisterSessionNotification"); }
        finally { Com.Release(_callback); _callback = 0; _manager.Dispose(); }
    }
}
