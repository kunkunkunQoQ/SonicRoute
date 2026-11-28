using System.Diagnostics;
using System.Runtime.InteropServices;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Product;

namespace SonicRoute.AndroidAudio;

// The capture worker owns all buffer/drain calls. Pool waits only signal it while
// a bounded queue contains frames. Endpoint teardown waits on the control side.
internal sealed class DesktopMirrorPlayer : IDisposable
{
    private readonly PcmRender _output;
    private readonly AutoResetEvent _renderWake;
    private readonly MirrorMeasurement _measurement;
    private readonly ManualResetEventSlim _idle = new(false);
    private DesktopMirrorPcmSource? _pending;
    private Func<nint,int,int>? _copyPending;
    private RegisteredWaitHandle? _writable;
    private int _active, _closed, _failed;
    private const int MuteBit = 1 << 9;
    private int _gain = MirrorGainState.DefaultMaximumVolumePercent;
    private byte[]? _gainBuffer;
    private long _written, _dropped, _emptyAfterStart, _firstWrite, _signalFrames;
    internal string EndpointId { get; }
    internal event Action<Exception>? Failed;

    internal void SetGain(int volumePercent, bool muted) =>
        Volatile.Write(ref _gain, Math.Clamp(volumePercent, 0, MirrorGainState.MaximumSupportedVolumePercent)
            | (muted ? MuteBit : 0));

    internal static void ScaleSamples(ReadOnlySpan<short> source, Span<short> destination, int volumePercent)
    {
        int q15 = (volumePercent * 32768 + 50) / 100;
        for (int i = 0; i < source.Length; i++)
        {
            int sample = (int)(((long)source[i] * q15) >> 15);
            destination[i] = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
        }
    }

    internal DesktopMirrorPlayer(string? endpointId, AutoResetEvent renderWake, MirrorMeasurement measurement)
    {
        _measurement = measurement;
        _renderWake = renderWake;
        using var mta = new MtaScope();
        using var endpoints = new Endpoints();
        using var endpoint = endpointId == null ? endpoints.Default(Flow.Render,Role.Multimedia) : endpoints.Open(endpointId);
        EndpointId = Endpoints.Id(endpoint);
        _output = new(endpoint,Guid.Empty);
        try { _output.Start(); }
        catch { _output.Dispose(); throw; }
    }

    internal MirrorSinkStatus Status() => new(EndpointId, Volatile.Read(ref _written), Volatile.Read(ref _dropped),
        Volatile.Read(ref _emptyAfterStart), _measurement.Milliseconds(Volatile.Read(ref _firstWrite)), Volatile.Read(ref _signalFrames));

    // One worker calls Write or Drain; one control thread may concurrently dispose.
    private bool Enter()
    {
        Interlocked.Increment(ref _active);
        if (Volatile.Read(ref _closed) == 0 && Volatile.Read(ref _failed) == 0) return true;
        Leave();
        return false;
    }

    private void Leave()
    {
        if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _closed) != 0) _idle.Set();
    }

    internal void Write(ReadOnlyMemory<byte> packet)
    {
        if (!Enter()) return;
        try
        {
            int gain = Volatile.Read(ref _gain);
            bool muted = (gain & MuteBit) != 0;
            int vol = gain & (MuteBit - 1);
            if ((muted || vol != 100) && !packet.IsEmpty)
            {
                if (_gainBuffer == null || _gainBuffer.Length < packet.Length)
                    _gainBuffer = new byte[Math.Max(packet.Length, 4096)];
                var dstBytes = _gainBuffer.AsSpan(0, packet.Length);
                if (muted || vol <= 0)
                {
                    dstBytes.Clear();
                }
                else
                {
                    var src = MemoryMarshal.Cast<byte, short>(packet.Span);
                    var dst = MemoryMarshal.Cast<byte, short>(dstBytes);
                    ScaleSamples(src, dst, vol);
                }
                packet = _gainBuffer.AsMemory(0, packet.Length);
            }
            int padding = checked((int)_output.Padding);
            int available = checked((int)_output.Capacity) - padding;
            if (padding == 0 && available > packet.Length / 4)
            {
                if (_written > 0 && _measurement.Enabled) ++_emptyAfterStart;
                int lead = Math.Min(960, available - packet.Length / 4);
                available -= _output.WriteReserved(lead);
            }
            if ((_pending?.BufferedBytes ?? 0) == 0 && available > 0)
            {
                int frames = Math.Min(available, packet.Length / 4);
                var samples = MemoryMarshal.Cast<byte,short>(packet.Span[..(frames*4)]);
                int sent = _output.WriteAvailable(packet.Span[..(frames*4)]);
                _written += sent;
                if (_measurement.Enabled && sent > 0 && samples[..(sent*2)].ContainsAnyExcept((short)0))
                {
                    _signalFrames += sent;
                    if (_firstWrite == 0) _firstWrite = Stopwatch.GetTimestamp();
                }
                packet = packet[(sent*4)..];
                available -= sent;
            }
            if (!packet.IsEmpty)
            {
                if (_pending == null) { _pending = new(); _copyPending = _pending.CopyToNative; }
                int expected = _pending.BufferedBytes + packet.Length;
                _pending.Write(packet.Span);
                _dropped += (expected - _pending.BufferedBytes) / 4;
                DrainCore(available);
            }
        }
        catch (Exception error) { Fail(error); }
        finally { Leave(); }
    }

    internal void Drain()
    {
        if (_writable == null || !Enter()) return;
        try { DrainCore(checked((int)_output.Available)); }
        catch (Exception error) { Fail(error); }
        finally { Leave(); }
    }

    private void DrainCore(int available)
    {
        int frames = Math.Min(available, _pending!.BufferedBytes / 4);
        if (frames > 0) _written += _output.WriteReserved(frames, _copyPending);
        if (_pending.BufferedBytes == 0) { _writable?.Unregister(null); _writable = null; }
        else _writable ??= ThreadPool.RegisterWaitForSingleObject(_output.BufferEvent, static (state,_) =>
        {
            try { ((AutoResetEvent)state!).Set(); } catch (ObjectDisposedException) { }
        }, _renderWake, Timeout.Infinite, false);
    }

    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref _failed,1) == 0) Failed?.Invoke(error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed,1) != 0) return;
        if (Volatile.Read(ref _active) != 0) _idle.Wait();
        _writable?.Unregister(null);
        _output.Dispose();
        // A previously captured Sinks snapshot may still briefly enter, observe _closed
        // and signal _idle. No native wait handle was requested from this slim event.
    }
}
