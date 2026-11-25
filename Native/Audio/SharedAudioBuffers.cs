namespace SonicRoute.Native.Audio;

// Shared-mode PCM buffer ownership is identical for the pull-based phone player and
// the push-based desktop stream. Scheduling, formats and latency policy stay with callers.
internal static unsafe class SharedAudioBuffers
{
    private const uint PcmConversion = 0x88000000u;
    private const uint EventCallback = 0x00040000u;
    private static readonly Guid RenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    internal static int Initialize(Com client, PcmFormat format, uint flags, long duration,
        Guid? session, EventWaitHandle? ready)
    {
        Guid id = session.GetValueOrDefault();
        int hr = ((delegate* unmanaged[Stdcall]<nint,int,uint,long,long,PcmFormat*,Guid*,int>)client.Slot(3))
            (client.Handle, 0, flags | PcmConversion | (ready == null ? 0u : EventCallback),
                duration, 0, &format, session.HasValue ? &id : null);
        if (hr < 0 || ready == null) return hr;
        return ((delegate* unmanaged[Stdcall]<nint,nint,int>)client.Slot(13))
            (client.Handle, ready.SafeWaitHandle.DangerousGetHandle());
    }

    internal static Com OpenRenderService(Com client) => client.Service(14, RenderClient);
    internal static uint Capacity(Com client) => client.UInt32(4);
    internal static uint Padding(Com client) => client.UInt32(6);

    internal static nint Acquire(Com render, uint frames)
    {
        nint buffer = 0;
        Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)render.Slot(3))
            (render.Handle, frames, &buffer), "IAudioRenderClient.GetBuffer");
        return buffer;
    }

    internal static void Release(Com render, uint frames, uint flags) =>
        Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,uint,int>)render.Slot(4))
            (render.Handle, frames, flags), "IAudioRenderClient.ReleaseBuffer");

    internal static int Write(Com render, ReadOnlySpan<byte> bytes, int frameSize)
    {
        if (frameSize <= 0 || bytes.Length % frameSize != 0) throw new ArgumentException("Partial PCM frame.");
        uint requested = (uint)(bytes.Length / frameSize);
        if (requested == 0) return 0;

        nint buffer = Acquire(render, requested);
        uint committed = 0;
        try
        {
            bytes.CopyTo(new Span<byte>((void*)buffer, bytes.Length));
            committed = requested;
        }
        finally
        {
            Release(render, committed, 0);
        }
        return (int)committed;
    }
}

