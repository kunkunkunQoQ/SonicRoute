using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

// The render client serializes access to these 48 kHz stereo PCM16 frames.
internal sealed class DesktopMirrorPcmSource
{
    private const int BytesPerFrame = 4;
    private const int BytesPerMillisecond = 48 * BytesPerFrame;
    private readonly byte[] _buffer = new byte[250 * BytesPerMillisecond];
    private int _start, _count;

    internal int BufferedBytes => _count;

    internal void Write(ReadOnlySpan<byte> packet)
    {
        if (packet.Length % BytesPerFrame != 0) throw new ArgumentException("Unaligned PCM packet.", nameof(packet));
        if (packet.Length >= _buffer.Length)
        {
            packet = packet.Slice(packet.Length - _buffer.Length);
            _start = _count = 0;
        }
        else if (_count + packet.Length > _buffer.Length)
        {
            int keep = Math.Max(0, 150 * BytesPerMillisecond - packet.Length);
            _start = (_start + _count - keep) % _buffer.Length;
            _count = keep;
        }
        int end = (_start + _count) % _buffer.Length;
        int first = Math.Min(packet.Length, _buffer.Length - end);
        packet.Slice(0, first).CopyTo(_buffer.AsSpan(end));
        packet.Slice(first).CopyTo(_buffer);
        _count += packet.Length;
    }

    internal int CopyToNative(IntPtr destination, int capacityBytes)
    {
        if (capacityBytes < 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        int take = Math.Min(_count, capacityBytes - capacityBytes % BytesPerFrame);
        if (take == 0) return 0;
        int first = Math.Min(take, _buffer.Length - _start);
        Marshal.Copy(_buffer, _start, destination, first);
        if (take > first) Marshal.Copy(_buffer, 0, IntPtr.Add(destination, first), take - first);
        _start = (_start + take) % _buffer.Length;
        _count -= take;
        return take;
    }
}
