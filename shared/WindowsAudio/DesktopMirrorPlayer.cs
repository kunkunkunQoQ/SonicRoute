using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonicRoute.AndroidAudio;

// Capture packets normally go straight into the endpoint. Only a full endpoint buffer needs a queue and a render callback.
internal sealed class DesktopMirrorPlayer : IDisposable
{
    private static readonly WaveFormat Format = new(48000, 16, 2);
    private readonly object _gate = new();
    private readonly WasapiDeviceLease _device;
    private readonly AudioClient _client;
    private readonly AudioRenderClient _render;
    private readonly AutoResetEvent _writable = new(false);
    private readonly int _frames;
    private DesktopMirrorPcmSource? _pending;
    private RegisteredWaitHandle? _drainWait;
    private bool _closed, _failed;
    internal event Action<Exception>? Failed;

    internal DesktopMirrorPlayer(string endpointId)
    {
        _device = WasapiDeviceLease.Open(endpointId);
        try
        {
            _client = _device.Device.AudioClient;
            var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            _client.Initialize(AudioClientShareMode.Shared, flags, 1_000_000, 0, Format, Guid.Empty);
            _client.SetEventHandle(_writable.SafeWaitHandle.DangerousGetHandle());
            _frames = _client.BufferSize;
            _render = _client.AudioRenderClient;
            _client.Start();
        }
        catch
        {
            _client?.Dispose();
            _writable.Dispose();
            _device.Dispose();
            throw;
        }
    }

    internal void Write(ReadOnlyMemory<byte> packet)
    {
        Exception? failure = null;
        Action<Exception>? handler = null;
        lock (_gate)
        {
            if (_closed || _failed) return;
            try
            {
                int padding = _client.CurrentPadding;
                int available = _frames - padding;
                // Leave two engine periods of headroom when resuming an empty endpoint.
                if (padding == 0 && available > packet.Length / Format.BlockAlign)
                {
                    int lead = Math.Min(Format.SampleRate / 50, available - packet.Length / Format.BlockAlign);
                    _render.GetBuffer(lead);
                    _render.ReleaseBuffer(lead, AudioClientBufferFlags.Silent);
                    available -= lead;
                }
                if ((_pending?.BufferedBytes ?? 0) == 0 && MemoryMarshal.TryGetArray(packet, out var segment))
                {
                    int frames = Math.Min(available, packet.Length / Format.BlockAlign);
                    if (frames != 0)
                    {
                        int bytes = frames * Format.BlockAlign;
                        var destination = _render.GetBuffer(frames);
                        Marshal.Copy(segment.Array!, segment.Offset, destination, bytes);
                        _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
                        packet = packet.Slice(bytes);
                        available -= frames;
                    }
                    if (packet.IsEmpty) return;
                }
                (_pending ??= new DesktopMirrorPcmSource()).Write(packet.Span);
                Drain(available);
            }
            catch (Exception ex) { failure = ex; handler = FailLocked(); }
        }
        if (failure != null) handler?.Invoke(failure);
    }

    private void Drain(int available)
    {
        var queue = _pending!;
        int frames = Math.Min(available, queue.BufferedBytes / Format.BlockAlign);
        if (frames > 0)
        {
            var destination = _render.GetBuffer(frames);
            queue.CopyToNative(destination, frames * Format.BlockAlign);
            _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
        }
        if (queue.BufferedBytes == 0)
        {
            _drainWait?.Unregister(null);
            _drainWait = null;
        }
        else if (_drainWait == null)
        {
            _drainWait = ThreadPool.RegisterWaitForSingleObject(_writable, DrainOnSignal, null, Timeout.Infinite, false);
        }
    }

    private void DrainOnSignal(object? state, bool timedOut)
    {
        Exception? failure = null;
        Action<Exception>? handler = null;
        lock (_gate)
        {
            if (_closed || _failed || _drainWait == null) return;
            try { Drain(_frames - _client.CurrentPadding); }
            catch (Exception ex) { failure = ex; handler = FailLocked(); }
        }
        if (failure != null) handler?.Invoke(failure);
    }

    private Action<Exception>? FailLocked()
    {
        _failed = true;
        _drainWait?.Unregister(null);
        _drainWait = null;
        return Failed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _drainWait?.Unregister(null);
            _drainWait = null;
            try { _client.Stop(); }
            catch (COMException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                _client.Dispose();
                _writable.Dispose();
                _device.Dispose();
            }
        }
    }
}
