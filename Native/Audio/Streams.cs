using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SonicRoute.Native.Audio;

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct PcmFormat
{
    internal ushort Tag, Channels;
    internal uint Rate, AverageBytes;
    internal ushort Alignment, Bits, Extra;
    internal static PcmFormat Stereo48() => new() {Tag=1,Channels=2,Rate=48_000,AverageBytes=192_000,Alignment=4,Bits=16};
}

internal abstract unsafe class AudioStream : IDisposable
{
    protected Com Client;
    protected Com? Service;
    internal readonly AutoResetEvent BufferEvent = new(false);
    private bool _running;
    private bool _disposed;
    protected AudioStream(Com client) => Client = client;
    protected int Initialize(uint flags, Guid session, long duration, bool useEvent)
    {
        return SharedAudioBuffers.Initialize(Client, PcmFormat.Stereo48(), flags, duration, session, useEvent ? BufferEvent : null);
    }
    internal void Start()
    {
        Com.Check(Client.Call(10),"IAudioClient.Start");
        _running = true;
    }
    protected virtual void ReleaseStorage() { }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_running) Client.Call(11); }
        finally { Service?.Dispose(); Client.Dispose(); BufferEvent.Dispose(); ReleaseStorage(); }
    }
}
internal sealed unsafe class PcmRender : AudioStream
{
    private readonly uint _capacity;
    internal PcmRender(Com endpoint, Guid session) : base(Endpoints.Activate(endpoint,Endpoints.AudioClient))
    {
        try
        {
            Com.Check(Initialize(0,session,1_000_000,true),"initialize shared event render client");
            Service = SharedAudioBuffers.OpenRenderService(Client);
            _capacity = SharedAudioBuffers.Capacity(Client);
        }
        catch { Dispose(); throw; }
    }
    internal int WriteReserved(int frames, Func<nint,int,int>? copy = null)
    {
        if (frames <= 0) return 0;
        var target = SharedAudioBuffers.Acquire(Service!, checked((uint)frames));
        uint committed = 0;
        try { committed = copy == null ? (uint)frames : checked((uint)(copy(target, checked(frames * 4)) / 4)); }
        finally { SharedAudioBuffers.Release(Service!, committed, copy == null ? 2u : 0u); }
        return (int)committed;
    }
    internal uint Capacity => _capacity;
    internal int WriteAvailable(ReadOnlySpan<byte> frames) => SharedAudioBuffers.Write(Service!, frames, 4);
    internal uint Available => _capacity - Padding;
    internal uint Padding => SharedAudioBuffers.Padding(Client);
    internal Com GetControl() => Client.Service(14,new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"));
    internal Com GetSimpleVolume() => Client.Service(14,Endpoints.SimpleVolume);
    internal int Write(ReadOnlySpan<short> stereo)
    {
        if ((stereo.Length & 1) != 0) throw new ArgumentException("Partial stereo PCM frame.",nameof(stereo));
        uint frames = Math.Min(Available,checked((uint)stereo.Length / 2));
        if (frames == 0) return 0;
        int samples = checked((int)frames * 2);
        return SharedAudioBuffers.Write(Service!, MemoryMarshal.AsBytes(stereo[..samples]), 2 * sizeof(short));
    }
}

[GeneratedComInterface, Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
internal partial interface ICompletion
{
    [PreserveSig] int ActivateCompleted(nint operation);
}
[GeneratedComInterface, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
internal partial interface IAgile { }
[GeneratedComClass]
internal sealed unsafe partial class LoopbackCompletion : ICompletion, IAgile
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private bool _abandoned;
    private nint _result;
    private int _hr;
    private bool _lateResultReleased;
    internal Task Completed => _ready.Task;
    internal bool LateResultReleased { get { lock (_gate) return _lateResultReleased; } }
    public int ActivateCompleted(nint operation)
    {
        int activation = -1;
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint,int*,nint*,int>)Com.Slot(operation,3))(operation,&activation,&result);
        lock (_gate)
        {
            _hr = hr < 0 ? hr : activation;
            if (_abandoned || _hr < 0)
            {
                _lateResultReleased = _abandoned && result != 0;
                Com.Release(result);
            }
            else _result = result;
        }
        _ready.TrySetResult();
        return 0;
    }
    internal Com Take(TimeSpan timeout, CancellationToken token)
    {
        _ready.Task.WaitAsync(timeout,token).GetAwaiter().GetResult();
        lock (_gate)
        {
            Com.Check(_hr,"GetActivateResult");
            return new(Interlocked.Exchange(ref _result,0));
        }
    }
    internal void Abandon()
    {
        lock (_gate)
        {
            _abandoned = true;
            Com.Release(Interlocked.Exchange(ref _result,0));
        }
    }
}
internal delegate void PcmPacket(ReadOnlySpan<short> samples);

// Single capture worker owns WaitForPacket, Drain and Dispose. Its caller cancels
// the token, joins that worker and then releases the token source.
internal sealed unsafe class ProcessCapture : AudioStream
{
    private readonly CancellationToken _token;
    private readonly WaitHandle[] _waits;
    private byte[]? _buffer;
    internal bool EventDriven { get; }
    internal long Discontinuities { get; private set; }
    internal static Com Begin(uint processId,ICompletion handler)
    {
        nint callback = (nint)ComInterfaceMarshaller<ICompletion>.ConvertToUnmanaged(handler);
        nint op = 0;
        try
        {
            uint* data = stackalloc uint[3] {1,processId,0};
            PropVariant parameters = new() { Type = 65,BlobSize = 12,Blob = (nint)data };
            Guid iid = Endpoints.AudioClient;
            fixed (char* path = @"VAD\Process_Loopback")
                Com.Check(NativeMethods.ActivateAudioInterfaceAsync(path,&iid,&parameters,callback,&op),"ActivateAudioInterfaceAsync");
            var owner = new Com(op);
            op = 0;
            return owner;
        }
        finally { Com.Release(op); Com.Release(callback); }
    }
    internal static Com Activate(uint processId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var handler = new LoopbackCompletion();
        using var operation = Begin(processId,handler);
        try { return handler.Take(TimeSpan.FromSeconds(5),token); }
        finally { handler.Abandon(); }
    }
    internal ProcessCapture(uint pid,Guid session,CancellationToken token,bool preferEvents = true, WaitHandle? renderWake = null) : base(Activate(pid,token))
    {
        _token = token;
        try
        {
            _waits = renderWake == null ? [token.WaitHandle, BufferEvent] : [token.WaitHandle, BufferEvent, renderWake];
            const uint loopback = 0x00020000;
            const long captureDuration = 200_000;
            if (preferEvents)
            {
                EventDriven = Initialize(loopback,session,captureDuration,true) >= 0;
                if (!EventDriven)
                {
                    Client.Dispose();
                    Client = Activate(pid,token);
                }
            }
            if (!EventDriven) Com.Check(Initialize(loopback,session,captureDuration,false),"initialize non-event capture");
            Service = Client.Service(14,new("C8ADBD64-E71E-48a0-A4DE-185C395CD317"));
            _buffer = ArrayPool<byte>.Shared.Rent(16384);
        }
        catch { Dispose(); throw; }
    }
    internal bool WaitForPacket(int timeoutMs)
    {
        _token.ThrowIfCancellationRequested();
        if (EventDriven)
        {
            int signaled = WaitHandle.WaitAny(_waits,timeoutMs);
            _token.ThrowIfCancellationRequested();
            return signaled != WaitHandle.WaitTimeout;
        }
        _token.WaitHandle.WaitOne(timeoutMs < 0 ? 10 : Math.Min(10,timeoutMs));
        _token.ThrowIfCancellationRequested();
        return true;
    }
    protected override void ReleaseStorage()
    {
        if (_buffer is { } buffer) { _buffer = null; ArrayPool<byte>.Shared.Return(buffer); }
    }
    internal void Drain(PcmPacket packet) => DrainCore(packet, null);
    internal void DrainBytes(Action<ReadOnlyMemory<byte>> packet) => DrainCore(null, packet);

    private void DrainCore(PcmPacket? samplesPacket, Action<ReadOnlyMemory<byte>>? bytesPacket)
    {
        Com reader = Service!;
        // Bound dispatch work when a slow consumer lets the engine refill continuously.
        for (int drained = 0; drained < 64 && reader.UInt32(5) > 0; drained++)
        {
            _token.ThrowIfCancellationRequested();
            nint data = 0;
            uint frames = 0, flags = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint,nint*,uint*,uint*,ulong*,ulong*,int>)reader.Slot(3))(reader.Handle,&data,&frames,&flags,null,null);
            Com.Check(hr,"IAudioCaptureClient.GetBuffer");
            if (hr == 0x08890001) return;
            if ((flags & 1) != 0) ++Discontinuities;
            byte[]? overflow = null;
            try
            {
                int length;
                byte[] buffer;
                try
                {
                    length = checked((int)frames * 4);
                    if (length > 192_000) throw new InvalidDataException("Unexpected process capture packet larger than one second.");
                    if (length == 0) return;
                    buffer = _buffer!;
                    if (length > buffer.Length) buffer = overflow = ArrayPool<byte>.Shared.Rent(length);
                    var samples = buffer.AsSpan(0,length);
                    if ((flags & 2) != 0 || data == 0) samples.Clear();
                    else new ReadOnlySpan<byte>((void*)data,length).CopyTo(samples);
                }
                finally { Com.Check(((delegate* unmanaged[Stdcall]<nint,uint,int>)reader.Slot(4))(reader.Handle,frames),"IAudioCaptureClient.ReleaseBuffer"); }
                if (bytesPacket != null) bytesPacket(buffer.AsMemory(0,length));
                else samplesPacket!(MemoryMarshal.Cast<byte,short>(buffer.AsSpan(0,length)));
            }
            finally { if (overflow != null) ArrayPool<byte>.Shared.Return(overflow); }
        }
    }
}
