using System;
using System.Runtime.InteropServices;
using SonicRoute.Core.Interop;

namespace SonicRoute.Core
{
    // Owns one activated endpoint and its callback. All registration/release belongs
    // to the caller's serialized worker, never to a Core Audio callback.
    internal sealed class EndpointVolumeSubscription : IDisposable
    {
        internal IAudioEndpointVolume Volume { get; }
        private readonly IAudioEndpointVolumeCallback _callback;
        private bool _disposed;
        internal bool Registered { get; private set; }

        internal EndpointVolumeSubscription(IMMDevice device, IAudioEndpointVolumeCallback callback)
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out var obj));
            Volume = obj as IAudioEndpointVolume ?? throw new COMException("Endpoint volume unavailable.");
            _callback = callback;
            TryRegister();
        }

        internal void TryRegister()
        {
            if (Registered) return;
            try { Registered = Volume.RegisterControlChangeNotify(_callback) >= 0; }
            catch { } // A readable endpoint remains usable while notification recovery is pending.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Registered)
            {
                try { Volume.UnregisterControlChangeNotify(_callback); } catch { }
                Registered = false;
            }
            try { Marshal.ReleaseComObject(Volume); } catch { }
        }
    }
}
