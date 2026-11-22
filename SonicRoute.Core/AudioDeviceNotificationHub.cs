using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    internal enum AudioDeviceChangeKind
    {
        Topology,
        DefaultChanged,
        PropertyChanged
    }

    internal readonly struct AudioDeviceChange
    {
        internal AudioDeviceChange(AudioDeviceChangeKind kind, EDataFlow flow, ERole role)
        {
            Kind = kind;
            Flow = flow;
            Role = role;
        }

        internal AudioDeviceChangeKind Kind { get; }
        internal EDataFlow Flow { get; }
        internal ERole Role { get; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class AudioEndpointVolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly Action _changed;

        internal AudioEndpointVolumeCallback(Action changed) => _changed = changed;

        public int OnNotify(IntPtr notification)
        {
            _changed();
            return 0;
        }
    }

    /// <summary>
    /// Shared on-demand Core Audio endpoint notification hub. Registers at most one
    /// IMMNotificationClient while subscribers exist and invalidates Core caches once per event.
    /// </summary>
    internal static class AudioDeviceNotificationHub
    {
        private static readonly object Gate = new();
        private static readonly List<Subscription> Subscribers = new();
        private static readonly NotificationClient Client = new();
        private static Action<AudioDeviceChange>[] _listeners = Array.Empty<Action<AudioDeviceChange>>();
        private static IMMDeviceEnumerator? _enumerator;
        private static int _registered;

        internal static bool IsRegistered => Volatile.Read(ref _registered) != 0;

        internal static IDisposable Subscribe(Action<AudioDeviceChange> listener)
        {
            if (listener == null) throw new ArgumentNullException(nameof(listener));
            lock (Gate)
            {
                var subscription = new Subscription(listener);
                Subscribers.Add(subscription);
                PublishListenersLocked();
                EnsureRegisteredLocked();
                return subscription;
            }
        }

        internal static bool EnsureRegistered()
        {
            if (IsRegistered) return true;
            lock (Gate)
            {
                if (Subscribers.Count == 0) return false;
                return EnsureRegisteredLocked();
            }
        }

        internal static void InvalidateRegistration()
        {
            IMMDeviceEnumerator? enumeratorToRelease = null;
            bool unregister = false;
            lock (Gate)
            {
                enumeratorToRelease = _enumerator;
                _enumerator = null;
                unregister = Interlocked.Exchange(ref _registered, 0) != 0;
            }
            if (enumeratorToRelease != null)
            {
                if (unregister)
                    try { enumeratorToRelease.UnregisterEndpointNotificationCallback(Client); } catch { }
                try { Marshal.ReleaseComObject(enumeratorToRelease); } catch { }
            }
        }

        private static bool EnsureRegisteredLocked()
        {
            if (Volatile.Read(ref _registered) != 0 && _enumerator != null) return true;
            try
            {
                _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                bool ok = _enumerator.RegisterEndpointNotificationCallback(Client) >= 0;
                if (!ok)
                {
                    FreeEnumeratorLocked();
                }
                Volatile.Write(ref _registered, ok ? 1 : 0);
                return ok;
            }
            catch
            {
                FreeEnumeratorLocked();
                Volatile.Write(ref _registered, 0);
                return false;
            }
        }

        private static void FreeEnumeratorLocked()
        {
            if (_enumerator == null) return;
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
        }

        private static void PublishListenersLocked()
        {
            var copy = new Action<AudioDeviceChange>[Subscribers.Count];
            for (int i = 0; i < Subscribers.Count; i++) copy[i] = Subscribers[i].Listener;
            Volatile.Write(ref _listeners, copy);
        }

        private static void Publish(AudioDeviceChange change)
        {
            // Never take Gate or release COM inside Core Audio's callback thread.
            var listeners = Volatile.Read(ref _listeners);
            for (int i = 0; i < listeners.Length; i++)
            {
                try { listeners[i](change); } catch { }
            }
        }

        private sealed class Subscription : IDisposable
        {
            private int _disposed;
            internal readonly Action<AudioDeviceChange> Listener;

            internal Subscription(Action<AudioDeviceChange> listener) => Listener = listener;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                IMMDeviceEnumerator? enumeratorToRelease = null;
                bool unregister = false;
                lock (Gate)
                {
                    if (!Subscribers.Remove(this)) return;
                    PublishListenersLocked();
                    if (Subscribers.Count == 0)
                    {
                        enumeratorToRelease = _enumerator;
                        _enumerator = null;
                        unregister = Interlocked.Exchange(ref _registered, 0) != 0;
                    }
                }
                if (enumeratorToRelease != null)
                {
                    if (unregister)
                        try { enumeratorToRelease.UnregisterEndpointNotificationCallback(Client); } catch { }
                    try { Marshal.ReleaseComObject(enumeratorToRelease); } catch { }
                }
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class NotificationClient : IMMNotificationClient
        {
            private static void InvalidateTopology()
            {
                AudioService.InvalidateDeviceCache();
                AudioService.InvalidateAppsCache();
                SessionVolumeService.InvalidateCache();
                Publish(new AudioDeviceChange(AudioDeviceChangeKind.Topology, EDataFlow.eAll, ERole.eConsole));
            }

            public int OnDeviceStateChanged(string id, DeviceState state) { InvalidateTopology(); return 0; }
            public int OnDeviceAdded(string id) { InvalidateTopology(); return 0; }
            public int OnDeviceRemoved(string id) { InvalidateTopology(); return 0; }

            public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
            {
                AudioService.InvalidateDeviceCache(flow);
                AudioService.InvalidateAppsCache();
                SessionVolumeService.InvalidateCache();
                Publish(new AudioDeviceChange(AudioDeviceChangeKind.DefaultChanged, flow, role));
                return 0;
            }

            public int OnPropertyValueChanged(string id, PROPERTYKEY key)
            {
                AudioService.InvalidateDeviceCache();
                Publish(new AudioDeviceChange(AudioDeviceChangeKind.PropertyChanged, EDataFlow.eAll, ERole.eConsole));
                return 0;
            }
        }
    }
}
