using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using SonicRoute.AndroidAudio;

namespace SonicRoute.AirPlay;

// The screen stream has one producer. Keep decryption, decoding, and presentation in order
// so flow control comes from TCP and no queue of decoded frames builds up.
internal sealed class AirPlayMirrorStream : IAsyncDisposable
{
    private const int MaxPacket = 8 * 1024 * 1024;
    private const int MaxConfig = 64 * 1024;
    private readonly TcpListener _listener;
    private readonly AirPlayAesCtr _cipher;
    private readonly CancellationTokenSource _stop;
    private readonly Task _run;
    private readonly ScrcpyPacketMerger _merger = new();
    private readonly bool _allowHevc;
    private NativeVideoWindow? _window;
    private D3D11VideoSurfaceRenderer? _renderer;
    private MediaFoundationH264Decoder? _decoder;
    private int _frames;
    private int _nalLength = 4;
    private uint _codec;

    internal AirPlayMirrorStream(IPAddress local, IPAddress peer, ulong id, byte[] key, string name, bool showWindow, bool allowHevc,
        Action started, Action<bool, bool> modeChanged, Action<bool> ended, CancellationToken token)
    {
        string label = id.ToString(CultureInfo.InvariantCulture);
        _allowHevc = allowHevc;
        _listener = new TcpListener(local, 0);
        _cipher = AirPlayAesCtr.FromLabels(Encoding.ASCII.GetBytes("AirPlayStreamKey" + label),
            Encoding.ASCII.GetBytes("AirPlayStreamIV" + label), key);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            _listener.Start(2);
            DataPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _run = RunAsync(peer, name, showWindow, started, modeChanged, ended, token, _stop.Token);
        }
        catch
        {
            _listener.Stop();
            _cipher.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    internal int DataPort { get; }
    internal int PresentedFrames => Volatile.Read(ref _frames);
    internal void Activate() { if (PresentedFrames > 0) Volatile.Read(ref _window)?.Activate(); }
    internal void SetTopmost(bool value) => Volatile.Read(ref _window)?.SetTopmost(value);
    internal void SetFullscreen(bool value)
    {
        if (Volatile.Read(ref _window) is not { } window) return;
        window.Activate();
        window.SetFullscreen(value);
    }

    private async Task RunAsync(IPAddress peer, string name, bool showWindow, Action started, Action<bool, bool> modeChanged, Action<bool> ended,
        CancellationToken sessionToken, CancellationToken token)
    {
        bool failed = false;
        try
        {
            _stop.CancelAfter(TimeSpan.FromSeconds(25));
            using var client = await AirPlayMediaStream.AcceptPeerAsync(_listener, peer, token).ConfigureAwait(false);
            _listener.Stop();
            using var stream = client.GetStream();
            var header = new byte[128];
            var smallPacket = new byte[64 * 1024];
            int width = 0, height = 0;
            long began = Stopwatch.GetTimestamp();
            ulong? firstNtp = null;

            void Presented(int cropWidth, int cropHeight)
            {
                _window!.UpdateContentSize(cropWidth, cropHeight);
                if (_renderer!.HasPresentedFrame && Interlocked.Increment(ref _frames) == 1)
                {
                    _stop.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (showWindow) _window.EnsureVisible();
                    started();
                }
            }

            DecodedHardwareFrameHandler hardware = (texture, slice, w, h, x, y, cropW, cropH, matrix) =>
            {
                _renderer!.PresentHardwareTexture(texture, slice, w, h, x, y, cropW, cropH, matrix);
                Presented(cropW, cropH);
            };
            DecodedSoftwareFrameHandler software = (pixels, stride, h, x, y, cropW, cropH, matrix) =>
            {
                _renderer!.PresentSoftwareNv12(pixels, stride, h, x, y, cropW, cropH, matrix);
                Presented(cropW, cropH);
            };

            while (!token.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                int type = header[4];
                if (length < 0 || length > (type == 1 ? MaxConfig : MaxPacket))
                    throw new InvalidDataException("Invalid AirPlay screen packet length.");
                if (length == 0) continue;
                var packet = length <= smallPacket.Length ? smallPacket : ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    await stream.ReadExactlyAsync(packet.AsMemory(0, length), token).ConfigureAwait(false);
                    if (type == 1)
                    {
                        ApplyConfig(packet.AsSpan(0, length));
                        (width, height) = ReadSize(header);
                    }
                    else if (type == 0)
                    {
                        _cipher.Apply(packet.AsSpan(0, length));
                        if (_decoder == null)
                        {
                            if (!_merger.HasPendingConfig) continue;
                            await CreateWindowAsync(name, width, height, modeChanged, token).ConfigureAwait(false);
                        }
                        ulong ntp = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
                        long ptsUs = ntp == 0 ? (long)Stopwatch.GetElapsedTime(began).TotalMicroseconds
                            : (long)(unchecked((long)(ntp - (firstNtp ??= ntp))) / 4294.967296);
                        DecodeFrame(packet.AsSpan(0, length), ptsUs, hardware, software);
                    }
                }
                finally { if (packet != smallPacket) ArrayPool<byte>.Shared.Return(packet); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or SocketException or COMException
            or ObjectDisposedException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // A clean EOF or a window closed by the user is a normal disconnect.
            failed = !sessionToken.IsCancellationRequested && (PresentedFrames == 0 || !token.IsCancellationRequested && ex is not EndOfStreamException);
            if (failed) Trace.WriteLine("AirPlay mirror: " + ex.GetType().Name);
        }
        finally
        {
            if (_window != null) await _window.DisposeAsync().ConfigureAwait(false);
            try { _decoder?.Dispose(); }
            finally
            {
                _renderer?.Dispose();
                ended(failed);
            }
        }
    }

    private async Task CreateWindowAsync(string name, int width, int height, Action<bool, bool> modeChanged, CancellationToken token)
    {
        var (w, h) = MirrorAspectMath.FitAspect(width > 0 ? width : 640, height > 0 ? height : 960, 1120, 800);
        var window = new NativeVideoWindow(name, isMirrorMode: true, preferText: false, iconPng: null,
            x: 100, y: 80, w, h, acceptsGamepad: false, windowModes: true);
        Volatile.Write(ref _window, window);
        window.UpdateContentSize(width, height);
        window.CloseRequested += _stop.Cancel;
        window.WindowModeChanged += modeChanged;
        nint handle = await window.CreateAsync(token).ConfigureAwait(false);
        _renderer = new D3D11VideoSurfaceRenderer(handle, window.ClientWidth, window.ClientHeight, isMirrorMode: true);
        window.ClientSizeChanged += _renderer.Resize;
        window.PaintRequested += _renderer.RenderLastFrame;
        _decoder = new MediaFoundationH264Decoder(_renderer.DeviceHandle, width, height, codec: _codec);
    }

    private void DecodeFrame(Span<byte> data, long ptsUs, DecodedHardwareFrameHandler hardware, DecodedSoftwareFrameHandler software)
    {
        // AirPlay usually sends a four byte AVCC length. With that width the conversion is in place.
        byte[]? expanded = _nalLength == 4 ? null : ArrayPool<byte>.Shared.Rent(checked(data.Length * 3));
        try
        {
            Span<byte> annexB = expanded == null ? data : expanded;
            int dest = 0;
            while (!data.IsEmpty)
            {
                if (data.Length <= _nalLength) throw new InvalidDataException("Truncated AirPlay NAL unit.");
                uint count = 0;
                foreach (byte b in data[.._nalLength]) count = (count << 8) | b;
                data = data[_nalLength..];
                if (count == 0 || count > data.Length || (data[0] & 0x80) != 0)
                    throw new InvalidDataException("Invalid AirPlay NAL unit.");
                BinaryPrimitives.WriteUInt32BigEndian(annexB[dest..], 1);
                if (expanded != null) data[..(int)count].CopyTo(annexB[(dest + 4)..]);
                dest += (int)count + 4;
                data = data[(int)count..];
            }
            var merged = _merger.Process(annexB[..dest], isConfig: false);
            _decoder!.DecodePacket(merged, Math.Max(0, ptsUs), hardware, software);
        }
        finally { if (expanded != null) ArrayPool<byte>.Shared.Return(expanded); }
    }

    private void ApplyConfig(ReadOnlySpan<byte> data)
    {
        bool hevc = data.Length >= 8 && data.Slice(4, 4).SequenceEqual("hvc1"u8);
        uint codec = hevc ? ScrcpyVideoStreamHeader.CodecH265 : ScrcpyVideoStreamHeader.CodecH264;
        if (hevc && !_allowHevc) throw new NotSupportedException("AirPlay HEVC was not offered.");
        if (_codec != 0 && _codec != codec) throw new InvalidDataException("AirPlay changed the screen codec within a stream.");
        _codec = codec;
        using var config = new MemoryStream(data.Length + 4);
        if (hevc) ReadHevcConfig(data, config, out _nalLength);
        else ReadAvcConfig(data, config, out _nalLength);
        _merger.Process(config.GetBuffer().AsSpan(0, (int)config.Length), isConfig: true);
    }

    private static void ReadAvcConfig(ReadOnlySpan<byte> data, Stream result, out int nalLength)
    {
        if (data.Length < 7 || data[0] != 1) throw new InvalidDataException("Invalid AirPlay H.264 configuration.");
        nalLength = (data[4] & 3) + 1;
        int spsCount = data[5] & 31;
        data = data[6..];
        WriteParameterSets(ref data, spsCount, 7, false, result);
        if (data.IsEmpty) throw new InvalidDataException("Missing AirPlay PPS.");
        int ppsCount = data[0];
        data = data[1..];
        WriteParameterSets(ref data, ppsCount, 8, false, result);
    }

    private static void ReadHevcConfig(ReadOnlySpan<byte> sampleEntry, Stream result, out int nalLength)
    {
        const int visualSampleEntrySize = 86;
        if (sampleEntry.Length < visualSampleEntrySize) throw new InvalidDataException("Invalid AirPlay HEVC sample entry.");
        var box = sampleEntry[visualSampleEntrySize..];
        while (box.Length >= 8)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(box);
            if (size < 8 || size > box.Length) break;
            if (box.Slice(4, 4).SequenceEqual("hvcC"u8))
            {
                var data = box.Slice(8, (int)size - 8);
                if (data.Length < 23 || data[0] != 1) break;
                nalLength = (data[21] & 3) + 1;
                int arrays = data[22], parameterSets = 0;
                data = data[23..];
                for (int i = 0; i < arrays; ++i)
                {
                    if (data.Length < 3) throw new InvalidDataException("Truncated AirPlay HEVC parameter array.");
                    int type = data[0] & 63;
                    int count = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
                    data = data[3..];
                    WriteParameterSets(ref data, count, type, true, result);
                    if (type is >= 32 and <= 34) parameterSets |= 1 << (type - 32);
                }
                if (parameterSets == 7) return;
                break;
            }
            box = box[(int)size..];
        }
        throw new InvalidDataException("Missing AirPlay HEVC parameter sets.");
    }

    private static void WriteParameterSets(ref ReadOnlySpan<byte> data, int count, int expectedType, bool hevc, Stream output)
    {
        if (count == 0) throw new InvalidDataException("Missing AirPlay parameter set.");
        for (int i = 0; i < count; ++i)
        {
            if (data.Length < 3) throw new InvalidDataException("Truncated AirPlay parameter set.");
            int size = BinaryPrimitives.ReadUInt16BigEndian(data);
            data = data[2..];
            if (size < (hevc ? 2 : 1) || size > data.Length || (hevc ? (data[0] >> 1) & 63 : data[0] & 31) != expectedType)
                throw new InvalidDataException("Invalid AirPlay parameter set.");
            output.Write("\0\0\0\x01"u8);
            output.Write(data[..size]);
            data = data[size..];
        }
    }

    private static (int Width, int Height) ReadSize(byte[] header)
    {
        float width = BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(40));
        float height = BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(44));
        return width is >= 64 and <= 4096 && height is >= 64 and <= 4096 ? ((int)width, (int)height) : (0, 0);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _run.ConfigureAwait(false); }
        finally { _cipher.Dispose(); _stop.Dispose(); }
    }
}
