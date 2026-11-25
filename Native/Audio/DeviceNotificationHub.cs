using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SonicRoute.Native.Audio;

// Share the IMMDeviceEnumerator registration between PC controls, restore and the phone renderer.
internal static unsafe partial class DeviceNotificationHub
{
    private static readonly object Gate = new();
    private static Sources? _active;
    internal static IDisposable Subscribe(IDeviceNotifications observer)
    {
        lock (Gate)
        {
            _active ??= new Sources();
            _active.Sink.Add(observer);
            return new Lease(_active, observer);
        }
    }

    private sealed class Lease(Sources source, IDeviceNotifications observer) : IDisposable
    {
        private bool _released;
        public void Dispose()
        {
            Sources? last = null;
            lock (Gate)
            {
                if (_released) return;
                _released = true;
                if (source.Sink.Remove(observer)) { _active = null; last = source; }
            }
            last?.Dispose();
        }
    }

    private sealed class Sources : IDisposable
    {
        internal readonly DeviceBroadcast Sink = new();
        private readonly Endpoints _endpoints;
        private readonly MtaLifetime _mta = new();
        private readonly nint _callback;
        internal Sources()
        {
            try
            {
                _endpoints = new Endpoints();
                _callback = (nint)ComInterfaceMarshaller<IDeviceNotifications>.ConvertToUnmanaged(Sink);
                Com.Check(Change(6), "RegisterEndpointNotificationCallback");
            }
            catch
            {
                Com.Release(_callback);
                _endpoints?.Dispose();
                _mta.Dispose();
                throw;
            }
        }
        private int Change(int slot) => ((delegate* unmanaged[Stdcall]<nint,nint,int>)_endpoints.Enumerator.Slot(slot))
            (_endpoints.Enumerator.Handle, _callback);
        public void Dispose()
        {
            try { Com.Check(Change(7), "UnregisterEndpointNotificationCallback"); }
            finally { Com.Release(_callback); _endpoints.Dispose(); _mta.Dispose(); }
        }
    }
}

[GeneratedComClass]
internal sealed partial class DeviceBroadcast : IDeviceNotifications
{
    private IDeviceNotifications[] _listeners = [];
    internal void Add(IDeviceNotifications listener) => Volatile.Write(ref _listeners, [.. _listeners, listener]);
    internal bool Remove(IDeviceNotifications listener)
    {
        var next = _listeners.Where(sub => !ReferenceEquals(sub, listener)).ToArray();
        Volatile.Write(ref _listeners, next);
        return next.Length == 0;
    }
    public int OnDeviceStateChanged(nint id, uint state)
    { foreach (var listener in Volatile.Read(ref _listeners)) listener.OnDeviceStateChanged(id, state); return 0; }
    public int OnDeviceAdded(nint id)
    { foreach (var listener in Volatile.Read(ref _listeners)) listener.OnDeviceAdded(id); return 0; }
    public int OnDeviceRemoved(nint id)
    { foreach (var listener in Volatile.Read(ref _listeners)) listener.OnDeviceRemoved(id); return 0; }
    public int OnDefaultDeviceChanged(int flow, int role, nint id)
    { foreach (var listener in Volatile.Read(ref _listeners)) listener.OnDefaultDeviceChanged(flow, role, id); return 0; }
    public int OnPropertyValueChanged(nint id, PropertyKey key)
    { foreach (var listener in Volatile.Read(ref _listeners)) listener.OnPropertyValueChanged(id, key); return 0; }
}
