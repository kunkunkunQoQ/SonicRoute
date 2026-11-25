using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SonicRoute.Native.Audio;

[GeneratedComInterface, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IDeviceNotifications
{
    [PreserveSig] int OnDeviceStateChanged(nint id, uint state);
    [PreserveSig] int OnDeviceAdded(nint id);
    [PreserveSig] int OnDeviceRemoved(nint id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, nint id);
    [PreserveSig] int OnPropertyValueChanged(nint id, PropertyKey key);
}
[GeneratedComInterface, Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal partial interface IEndpointNotifications
{
    [PreserveSig] int OnNotify(nint notification);
}
[GeneratedComInterface, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
internal partial interface ISessionNotifications
{
    [PreserveSig] int OnDisplayNameChanged(nint name,nint context);
    [PreserveSig] int OnIconPathChanged(nint path,nint context);
    [PreserveSig] int OnSimpleVolumeChanged(float level,int mute,nint context);
    [PreserveSig] int OnChannelVolumeChanged(uint count,nint levels,uint channel,nint context);
    [PreserveSig] int OnGroupingParamChanged(nint grouping,nint context);
    [PreserveSig] int OnStateChanged(int state);
    [PreserveSig] int OnSessionDisconnected(int reason);
}
[GeneratedComClass]
internal sealed partial class Notice : IDeviceNotifications, IEndpointNotifications, ISessionNotifications
{
    private readonly object _gate = new();
    private bool _stopped;
    private readonly Action? _changed;
    private int _queued;
    internal Notice(Action? changed = null) => _changed = changed;
    private void Deliver()
    {
        Interlocked.Exchange(ref _queued, 0);
        lock (_gate) { if (_stopped) return; }
        try { _changed?.Invoke(); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.Message); }
    }
    private int _count, _late;
    internal int Added, Removed, StateChanges;
    internal int Count => Volatile.Read(ref _count);
    internal int Late => Volatile.Read(ref _late);
    internal void Stop() { lock (_gate) _stopped = true; }
    private int Signal()
    {
        lock (_gate)
        {
            if (_stopped) { _late++; return 0; }
            _count++;
            if (_changed != null && Interlocked.Exchange(ref _queued, 1) == 0)
                ThreadPool.UnsafeQueueUserWorkItem(static n => n.Deliver(), this, preferLocal: false);
            Monitor.PulseAll(_gate);
        }
        return 0;
    }
    internal bool WaitForNext(int previous, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate)
        {
            while (!_stopped && _count <= previous && clock.Elapsed < timeout)
            {
                var remaining = timeout - clock.Elapsed;
                if (remaining > TimeSpan.Zero) Monitor.Wait(_gate,remaining);
            }
            return _count > previous;
        }
    }
    public int OnDeviceStateChanged(nint id,uint state) { Interlocked.Increment(ref StateChanges); return Signal(); }
    public int OnDeviceAdded(nint id) { Interlocked.Increment(ref Added); return Signal(); }
    public int OnDeviceRemoved(nint id) { Interlocked.Increment(ref Removed); return Signal(); }
    public int OnDefaultDeviceChanged(int flow,int role,nint id) => Signal();
    public int OnPropertyValueChanged(nint id,PropertyKey key) => Signal();
    public int OnNotify(nint notification) => Signal();
    public int OnDisplayNameChanged(nint name,nint context) => 0;
    public int OnIconPathChanged(nint path,nint context) => 0;
    public int OnSimpleVolumeChanged(float level,int mute,nint context) => Signal();
    public int OnChannelVolumeChanged(uint count,nint levels,uint channel,nint context) => 0;
    public int OnGroupingParamChanged(nint grouping,nint context) => 0;
    public int OnStateChanged(int state) => Signal();
    public int OnSessionDisconnected(int reason) => Signal();
}

internal sealed unsafe class Subscription : IDisposable
{
    private static int _active;
    internal static int Active => Volatile.Read(ref _active);
    private readonly Com _source;
    private readonly int _registerSlot;
    private nint _pointer;
    internal readonly Notice Sink;
    internal static Subscription ForDevices(Com source, Notice sink) => new(source,6,(nint)ComInterfaceMarshaller<IDeviceNotifications>.ConvertToUnmanaged(sink),sink);
    internal static Subscription ForEndpoint(Com source, Notice sink) => new(source,3,(nint)ComInterfaceMarshaller<IEndpointNotifications>.ConvertToUnmanaged(sink),sink);
    internal static Subscription ForSession(Com source, Notice sink) => new(source,10,(nint)ComInterfaceMarshaller<ISessionNotifications>.ConvertToUnmanaged(sink),sink);
    private Subscription(Com source,int registerSlot,nint pointer,Notice sink)
    {
        _source = source;
        _registerSlot = registerSlot;
        Sink = sink;
        _pointer = pointer;
        try { Com.Check(Change(registerSlot,pointer),"Register notification"); }
        catch { Com.Release(Interlocked.Exchange(ref _pointer,0)); throw; }
        Interlocked.Increment(ref _active);
    }
    private int Change(int slot,nint ptr) => ((delegate* unmanaged[Stdcall]<nint,nint,int>)_source.Slot(slot))(_source.Handle,ptr);
    public void Dispose()
    {
        nint ptr = Interlocked.Exchange(ref _pointer,0);
        if (ptr == 0) return;
        Sink.Stop();
        try { Com.Check(Change(_registerSlot + 1,ptr),"Unregister notification"); }
        finally { Com.Release(ptr); Interlocked.Decrement(ref _active); }
    }
}
