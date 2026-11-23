using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SonicRoute.AndroidAudio;

/// <summary>One native subscription shared only by active default-output and topology watchers.</summary>
internal static class DefaultOutputNotifications
{
    internal enum Interest
    {
        DefaultMultimedia,
        TopologyOnly,
        TopologyAndRenderDefault
    }

    private static readonly object Gate = new();
    private static readonly List<Subscription> Subscribers = new();
    private static Subscription[] _subscribers = [];
    private static IDisposable? _native;

    internal static IDisposable Subscribe(Action changed) =>
        Subscribe(Interest.DefaultMultimedia, changed);

    internal static IDisposable SubscribeTopology(Action changed, bool includeDefaultChanged = true) =>
        Subscribe(includeDefaultChanged ? Interest.TopologyAndRenderDefault : Interest.TopologyOnly, changed);

    private static IDisposable Subscribe(Interest interest, Action changed)
    {
        lock (Gate)
        {
            _native ??= WasapiDeviceLease.ObserveDefaultOutput(new DeviceCallback(PublishDefault, PublishTopology));
            var subscription = new Subscription(interest, changed);
            Subscribers.Add(subscription);
            PublishSubscribers();
            return subscription;
        }
    }

    private static void PublishSubscribers() =>
        Volatile.Write(ref _subscribers, Subscribers.ToArray());

    private static void PublishDefault(bool renderDefaultAnyRole, bool multimediaChanged)
    {
        // Native callbacks only queue work; never wait for a renderer or unregister here.
        foreach (var sub in Volatile.Read(ref _subscribers))
        {
            if (sub.Kind == Interest.DefaultMultimedia ? multimediaChanged
                : sub.Kind == Interest.TopologyAndRenderDefault && renderDefaultAnyRole)
                try { sub.Changed(); } catch { }
        }
    }

    private static void PublishTopology()
    {
        foreach (var sub in Volatile.Read(ref _subscribers))
        {
            if (sub.Kind != Interest.DefaultMultimedia)
                try { sub.Changed(); } catch { }
        }
    }

    private sealed class Subscription(Interest kind, Action changed) : IDisposable
    {
        internal readonly Interest Kind = kind;
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
    internal sealed class DeviceCallback(Action<bool, bool> defaultChanged, Action topologyChanged) : IMMNotificationClient
    {
        internal DeviceCallback(Action changed)
            : this((_, multimediaChanged) => { if (multimediaChanged) changed(); }, static () => { }) { }

        private string? _lastDefault;
        private int _seen;
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render) return;
            bool multimediaChanged = false;
            if (role == Role.Multimedia)
            {
                string? previous = Interlocked.Exchange(ref _lastDefault, defaultDeviceId);
                multimediaChanged = Interlocked.Exchange(ref _seen, 1) == 0
                    || !string.Equals(previous, defaultDeviceId, StringComparison.OrdinalIgnoreCase);
            }
            defaultChanged(true, multimediaChanged);
        }
        public void OnDeviceStateChanged(string id, DeviceState state) => topologyChanged();
        public void OnDeviceAdded(string id) => topologyChanged();
        public void OnDeviceRemoved(string id) => topologyChanged();
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }
}
