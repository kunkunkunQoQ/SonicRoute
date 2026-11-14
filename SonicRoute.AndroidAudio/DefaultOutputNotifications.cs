using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SonicRoute.AndroidAudio;

/// <summary>One native subscription shared only by active default-output sessions.</summary>
internal static class DefaultOutputNotifications
{
    private static readonly object Gate = new();
    private static readonly List<Subscription> Subscribers = new();
    private static Action[] _callbacks = [];
    private static IDisposable? _native;

    internal static IDisposable Subscribe(Action changed)
    {
        lock (Gate)
        {
            _native ??= WasapiDeviceLease.ObserveDefaultOutput(new DeviceCallback(Publish));
            var subscription = new Subscription(changed);
            Subscribers.Add(subscription);
            PublishSubscribers();
            return subscription;
        }
    }

    private static void PublishSubscribers() =>
        Volatile.Write(ref _callbacks, Subscribers.Select(s => s.Changed).ToArray());

    private static void Publish()
    {
        // Native callbacks only queue work; never wait for a renderer or unregister here.
        foreach (var callback in Volatile.Read(ref _callbacks))
            try { callback(); } catch { }
    }

    private sealed class Subscription(Action changed) : IDisposable
    {
        internal readonly Action Changed = changed;
        public void Dispose()
        {
            lock (Gate)
            {
                if (!Subscribers.Remove(this)) return;
                PublishSubscribers();
                if (Subscribers.Count != 0) return;
                var native = _native;
                _native = null;
                native?.Dispose();
            }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class DeviceCallback(Action changed) : IMMNotificationClient
    {
        private string? _lastDefault;
        private int _seen;
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // WasapiDeviceLease.Open(null) resolves this same role.
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            string? previous = Interlocked.Exchange(ref _lastDefault, defaultDeviceId);
            if (Interlocked.Exchange(ref _seen, 1) != 0
                && string.Equals(previous, defaultDeviceId, StringComparison.OrdinalIgnoreCase)) return;
            changed();
        }
        public void OnDeviceStateChanged(string id, DeviceState state) { }
        public void OnDeviceAdded(string id) { }
        public void OnDeviceRemoved(string id) { }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }
}
