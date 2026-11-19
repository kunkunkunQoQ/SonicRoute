using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal sealed class LinkVideoViewer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly Queue<Frame> _frames = new();
    private readonly Task _show;
    private NativeVideoWindow? _window;
    private byte[]? _assembly;
    private int _length;
    private long _pts;
    private bool _key;
    private bool _needKey = true;
    private bool _disposed;
    private long _lastMove;

    private sealed record Frame(byte[] Data, int Length, long Pts, bool Key);

    internal LinkVideoViewer(
        string title,
        Action<byte[]>? input,
        Action<Exception?> ended,
        string? decoderMode = null,
        byte[]? iconPng = null,
        int x = 100,
        int y = 80)
        => _show = Task.Run(async () =>
        {
            Exception? error = null;
            try { await ShowAsync(title, input, decoderMode, iconPng, x, y, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { error = ex; }
            finally { ended(error); }
        });

    internal void Push(ReadOnlySpan<byte> chunk)
    {
        lock (_gate)
        {
            if (_disposed) return;
            byte flags = chunk[0];
            long pts = BinaryPrimitives.ReadInt64LittleEndian(chunk[1..]);
            if ((flags & ~7) != 0 || pts < 0 || pts > TimeSpan.FromDays(365).TotalMicroseconds)
                throw new InvalidDataException("Invalid Windows Link video timestamp or flags.");
            if ((flags & 1) != 0)
            {
                _assembly ??= ArrayPool<byte>.Shared.Rent(LinkConnection.MaxVideoFrame);
                _length = 0;
                _pts = pts;
                _key = (flags & 4) != 0;
            }
            if (_assembly == null || pts != _pts || _length + chunk.Length - 9 > LinkConnection.MaxVideoFrame)
                throw new InvalidDataException("Invalid Windows Link video fragment sequence.");
            chunk[9..].CopyTo(_assembly.AsSpan(_length));
            _length += chunk.Length - 9;
            if ((flags & 2) == 0) return;

            if (_frames.Count >= 2)
            {
                while (_frames.TryDequeue(out var discarded)) Return(discarded.Data);
                _needKey = true;
            }
            if (!_needKey || _key)
            {
                _frames.Enqueue(new Frame(_assembly, _length, pts, _key));
                _assembly = null;
                _needKey = false;
                if (_ready.CurrentCount == 0) _ready.Release();
            }
            _length = 0;
        }
    }

    private async Task ShowAsync(string title, Action<byte[]>? input, string? decoderMode, byte[]? iconPng, int x, int y, CancellationToken token)
    {
        NativeVideoWindow? window = null;
        D3D11VideoSurfaceRenderer? renderer = null;
        MediaFoundationH264Decoder? decoder = null;
        string? effectiveDecoder = decoderMode is AndroidStreamSettings.WindowsVideoDecoder or AndroidStreamSettings.SoftwareVideoDecoder
            ? decoderMode : null;
        try
        {
            bool shown = false;
            void OnPresented(int width, int height)
            {
                window!.UpdateContentSize(width, height);
                if (shown) return;
                shown = true;
                window.EnsureVisible();
            }
            DecodedHardwareFrameHandler hardware = (texture, slice, w, h, cx, cy, cw, ch, matrix) =>
            {
                renderer!.PresentHardwareTexture(texture, slice, w, h, cx, cy, cw, ch, matrix);
                OnPresented(cw, ch);
            };
            DecodedSoftwareFrameHandler software = (pixels, stride, h, cx, cy, cw, ch, matrix) =>
            {
                renderer!.PresentSoftwareNv12(pixels, stride, h, cx, cy, cw, ch, matrix);
                OnPresented(cw, ch);
            };
            while (!token.IsCancellationRequested)
            {
                Frame? next;
                lock (_gate) _frames.TryDequeue(out next);
                if (next == null)
                {
                    await _ready.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    if (window == null)
                    {
                        window = new NativeVideoWindow(title, isMirrorMode: true, preferText: input != null, iconPng: iconPng,
                            x: x, y: y, initialClientWidth: 800, initialClientHeight: 450, acceptsGamepad: false, windowModes: true);
                        window.CloseRequested += _stop.Cancel;
                        if (input != null) AttachInput(window, input);
                        nint hwnd = await window.CreateAsync(token).ConfigureAwait(false);
                        renderer = new D3D11VideoSurfaceRenderer(hwnd, window.ClientWidth, window.ClientHeight, isMirrorMode: true);
                        window.ClientSizeChanged += renderer.Resize;
                        window.PaintRequested += renderer.RenderLastFrame;
                        decoder = new MediaFoundationH264Decoder(renderer.DeviceHandle, decoder: effectiveDecoder) { SkipCorruptFrames = true };
                        Volatile.Write(ref _window, window);
                    }
                    decoder!.DecodePacket(next.Data.AsSpan(0, next.Length), next.Pts, hardware, software);
                }
                finally { Return(next.Data); }
            }
        }
        finally
        {
            Volatile.Write(ref _window, null);
            if (window != null) await window.DisposeAsync().ConfigureAwait(false);
            try { decoder?.Dispose(); } finally { renderer?.Dispose(); }
        }
    }

    private void AttachInput(NativeVideoWindow window, Action<byte[]> input)
    {
        window.WindowsInputFocusLost += () => input([4]);
        window.WindowsKeyEvent += (down, key) => input([2, (byte)key, down ? (byte)1 : (byte)0]);
        window.TextInput += text =>
        {
            byte[] data = new byte[1 + Encoding.UTF8.GetByteCount(text)];
            if (data.Length > 9) return;
            data[0] = 3;
            Encoding.UTF8.GetBytes(text, data.AsSpan(1));
            input(data);
        };
        window.TouchEvent += (action, _, x, y, w, h, _, button, _) =>
        {
            bool moving = action is not (0 or 1);
            long now = Stopwatch.GetTimestamp();
            if (moving && Stopwatch.GetElapsedTime(_lastMove, now).TotalMilliseconds < 17) return;
            _lastMove = now;
            input(Pointer(x, y, w, h, moving ? (byte)0 : action == 0 ? (byte)1 : (byte)2, (byte)button, 0));
        };
        window.ScrollEvent += (x, y, w, h, horizontal, vertical, _) =>
        {
            if (horizontal == 0 && vertical != 0)
                input(Pointer(x, y, w, h, 3, 0, (short)Math.Clamp(vertical * 120, -1200, 1200)));
        };
    }

    private static byte[] Pointer(int x, int y, int w, int h, byte action, byte button, short wheel)
    {
        byte[] data = new byte[9];
        data[0] = 1;
        data[1] = action;
        data[2] = button;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3), Normalize(x, w));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(5), Normalize(y, h));
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(7), wheel);
        return data;
    }

    private static ushort Normalize(int value, int length) => (ushort)(Math.Clamp(value, 0, Math.Max(0, length - 1))
        * 65535L / Math.Max(1, length - 1));

    internal void Show() => Volatile.Read(ref _window)?.Activate();

    private static void Return(byte[] data) => ArrayPool<byte>.Shared.Return(data, clearArray: true);

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            if (_assembly != null) { Return(_assembly); _assembly = null; }
            while (_frames.TryDequeue(out var frame)) Return(frame.Data);
        }
        await _show.ConfigureAwait(false);
        _ready.Dispose();
        _stop.Dispose();
    }
}
