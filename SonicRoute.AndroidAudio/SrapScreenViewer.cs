using System.Text.Json;
using System.Threading.Channels;

namespace SonicRoute.AndroidAudio;

internal sealed partial class SrapSession
{
    private readonly object screenLock = new();
    private SrapScreenViewer? screen;
    internal string? ScreenGrant => Capabilities.Entries.TryGetValue("display.mirror", out var cap) &&
        cap is { State: "available", Scope: "one_projection" } && cap.Limits?.GetValueOrDefault("codec") == "h264"
        ? cap.Limits.GetValueOrDefault("grant") : null;

    internal async Task ViewScreenAsync(string title, CancellationToken cancel, AndroidDevice? device = null)
    {
        cancel.ThrowIfCancellationRequested();
        SrapScreenViewer viewer;
        bool created;
        lock (screenLock)
        {
            var grant = ScreenGrant ?? throw new SrapException("permission_required");
            created = screen == null;
            if (created)
            {
                string deviceKey = device == null ? "app:" + PeerFingerprint : AndroidDeviceIdentity.Key(device);
                screen = new SrapScreenViewer(this, title, grant, deviceKey);
                screen.Start(finished => { lock (screenLock) { if (ReferenceEquals(screen, finished)) screen = null; } });
            }
            viewer = screen!;
        }
        if (!created) viewer.Activate();
        try { await viewer.Ready.WaitAsync(cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) when (created) { viewer.Cancel(); throw; }
    }

    internal (long Frames, int Width, int Height) ScreenPresentation
    {
        get { lock (screenLock) return screen?.Presentation ?? default; }
    }
    internal async Task CloseScreenAsync(CancellationToken cancel)
    {
        SrapScreenViewer? viewer;
        lock (screenLock) viewer = screen;
        if (viewer == null) return;
        viewer.Cancel();
        if (await viewer.Closed.WaitAsync(cancel).ConfigureAwait(false) is { } error) throw error;
    }
}

/// <summary>A view-only MediaProjection stream. The native window becomes visible on a decoded and presented frame.</summary>
internal sealed class SrapScreenViewer(SrapSession session, string title, string grant, string deviceKey)
{
    private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
    private readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeVideoWindow? window;
    private long presented;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    internal Task Ready => firstFrame.Task;
    internal Task<Exception?> Closed => closed.Task;
    internal (long Frames, int Width, int Height) Presentation =>
        (Interlocked.Read(ref presented), window?.ContentWidth ?? 0, window?.ContentHeight ?? 0);
    internal void Activate() { if (Ready.IsCompletedSuccessfully) Volatile.Read(ref window)?.Activate(); }
    internal void Cancel() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
    internal void Start(Action<SrapScreenViewer> finished) => _ = Task.Run(() => RunAsync(finished));

    private async Task RunAsync(Action<SrapScreenViewer> finished)
    {
        uint id = 0;
        IDisposable? receiver = null;
        D3D11VideoSurfaceRenderer? renderer = null;
        MediaFoundationH264Decoder? decoder = null;
        Exception? stopError = null;
        int remoteEnded = 0;
        void GrantChanged() { if (session.ScreenGrant != grant) Cancel(); }
        void OnStreamEvent(JsonElement body)
        {
            if (SrapClient.Text(body, "name") != "stream.canceled" || !body.TryGetProperty("streamId", out var value)
                || !value.TryGetUInt32(out uint ended) || ended == 0 || ended != Volatile.Read(ref id)) return;
            Interlocked.Exchange(ref remoteEnded, 1);
            Cancel();
        }
        session.CapabilitiesChanged += GrantChanged;
        session.StreamEvent += OnStreamEvent;
        try
        {
            var token = stop.Token;
            stop.CancelAfter(TimeSpan.FromSeconds(20));
            GrantChanged();
            var opened = await session.RequestAsync("display.start", "display.mirror", new() { ["codec"] = "h264" }, token).ConfigureAwait(false);
            long streamId = Number(opened, "streamId");
            if (streamId is < 2 or > uint.MaxValue || (streamId & 1) != 0) throw Invalid();
            var assembler = new ScreenFrameAssembler();
            // Acquire ownership before stop can target this ID, even on malformed metadata.
            receiver = session.ExpectData((uint)streamId, chunk =>
            {
                if (stop.IsCancellationRequested) return;
                try { assembler.Append(chunk); }
                catch (SrapException error) { firstFrame.TrySetException(error); Cancel(); }
            });
            Volatile.Write(ref id, (uint)streamId);
            long width = Number(opened, "width"), height = Number(opened, "height");
            if (width is < 16 or > 1280 || height is < 16 or > 1280 || Math.Min(width, height) > 720 ||
                (width | height) % 2 != 0 || SrapClient.Text(opened, "codec") != "h264") throw Invalid();
            var (w, h) = MirrorAspectMath.FitAspect((int)width, (int)height, 1120, 800);
            var view = new NativeVideoWindow(title, isMirrorMode: true, preferText: false,
                iconPng: WicIconLoader.LoadDefaultPhoneIconPng(), x: 100, y: 80, w, h,
                appUserModelId: InProcessVirtualDisplayClient.BuildWindowAppUserModelId(deviceKey, AndroidApp.DeviceAudioPackage),
                acceptsGamepad: false, windowModes: true);
            Volatile.Write(ref window, view);
            view.UpdateContentSize((int)width, (int)height);
            view.CloseRequested += Cancel;
            nint handle = await view.CreateAsync(token).ConfigureAwait(false);
            renderer = new D3D11VideoSurfaceRenderer(handle, view.ClientWidth, view.ClientHeight, isMirrorMode: true);
            view.ClientSizeChanged += renderer.Resize;
            decoder = new MediaFoundationH264Decoder(renderer.DeviceHandle, (int)width, (int)height);

            int ready = 0;
            void ConfirmVisibleFrame()
            {
                if (!renderer.VideoPresentSucceeded || Interlocked.Read(ref presented) == 0 || stop.IsCancellationRequested ||
                    !IsWindowVisible(view.WindowHandle) || Interlocked.CompareExchange(ref ready, 1, 0) != 0) return;
                stop.CancelAfter(Timeout.InfiniteTimeSpan);
                firstFrame.TrySetResult();
            }
            view.PaintRequested += () => { renderer.RenderLastFrame(); ConfirmVisibleFrame(); };
            void Presented(int cropW, int cropH)
            {
                view.UpdateContentSize(cropW, cropH);
                if (!renderer.HasPresentedFrame) return;
                Interlocked.Increment(ref presented);
                if (Volatile.Read(ref ready) != 0) return;
                token.ThrowIfCancellationRequested();
                view.EnsureVisible();
                ConfirmVisibleFrame();
            }
            DecodedHardwareFrameHandler hardware = (texture, slice, fw, fh, x, y, cw, ch, matrix) =>
            {
                renderer.PresentHardwareTexture(texture, slice, fw, fh, x, y, cw, ch, matrix);
                Presented(cw, ch);
            };
            DecodedSoftwareFrameHandler software = (pixels, stride, fh, x, y, cw, ch, matrix) =>
            {
                renderer.PresentSoftwareNv12(pixels, stride, fh, x, y, cw, ch, matrix);
                Presented(cw, ch);
            };
            byte[]? config = null;
            bool decoded = false;
            for (long seq = 0; ; seq++)
            {
                token.ThrowIfCancellationRequested();
                assembler.Begin();
                var frame = await session.RequestAsync("display.next", "display.mirror", new() { ["streamId"] = id, ["seq"] = seq }, token).ConfigureAwait(false);
                if (Number(frame, "seq") != seq || Number(frame, "streamId") != id) throw Invalid();
                long size = Number(frame, "size"), pts = Number(frame, "ptsUs");
                if (size is < 1 or > ScreenFrameAssembler.MaxFrame || pts < 0 || pts > long.MaxValue / 10) throw Invalid();
                bool isConfig = Flag(frame, "config"), key = Flag(frame, "key");
                var bytes = assembler.Complete((int)size);
                if (isConfig)
                {
                    if (key || bytes.Length > 65536) throw Invalid();
                    config = bytes.ToArray();
                    continue;
                }
                if (config != null && !key || !decoded && config == null) throw Invalid();
                if (session.ScreenGrant != grant) throw new SrapException("permission_lost");
                decoder.DecodePacket(config, bytes.Span, pts, hardware, software, discontinuity: decoded && config != null);
                config = null;
                decoded = true;
            }
        }
        catch (Exception ex)
        {
            firstFrame.TrySetException(ex);
            if (Ready.IsCompletedSuccessfully && !stop.IsCancellationRequested)
                System.Diagnostics.Trace.WriteLine("SRAP screen stopped: " + ex.GetType().Name);
        }
        finally
        {
            session.CapabilitiesChanged -= GrantChanged;
            if (id != 0 && Volatile.Read(ref remoteEnded) == 0 && !session.Token.IsCancellationRequested)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await session.StopPcmAsync(id, cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or ChannelClosedException)
                {
                    stopError = ex;
                    using var fallback = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    if (!session.Token.IsCancellationRequested && Volatile.Read(ref remoteEnded) == 0)
                        try { await session.StopTrackAsync(id, fallback.Token).ConfigureAwait(false); }
                        catch (Exception) { session.Dispose(); }
                }
            }
            session.StreamEvent -= OnStreamEvent;
            receiver?.Dispose();
            try
            {
                try { if (window != null) await window.DisposeAsync().ConfigureAwait(false); }
                finally { try { decoder?.Dispose(); } finally { renderer?.Dispose(); } }
            }
            finally { stop.Dispose(); finished(this); closed.TrySetResult(Volatile.Read(ref remoteEnded) != 0 ? null : stopError); }
        }
    }

    private static long Number(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : throw Invalid();
    private static bool Flag(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? v.GetBoolean() : throw Invalid();
    private static SrapException Invalid() => new("invalid_argument");
}

internal sealed class ScreenFrameAssembler
{
    internal const int MaxFrame = 1024 * 1024;
    private byte[] buffer = new byte[32768];
    private int length;
    private bool collecting;
    internal void Begin() { lock (this) { collecting = true; length = 0; } }
    internal void Append(ReadOnlyMemory<byte> chunk)
    {
        lock (this)
        {
            if (!collecting || chunk.Length is < 1 or > 32768 || chunk.Length > MaxFrame - length)
                throw new SrapException("invalid_argument");
            int end = length + chunk.Length;
            if (end > buffer.Length) Array.Resize(ref buffer, Math.Min(MaxFrame, Math.Max(end, buffer.Length * 2)));
            chunk.Span.CopyTo(buffer.AsSpan(length));
            length = end;
        }
    }
    internal ReadOnlyMemory<byte> Complete(int expected)
    {
        lock (this)
        {
            if (!collecting || length != expected || length < 4 || buffer[0] != 0 || buffer[1] != 0 ||
                (buffer[2] != 1 && (buffer[2] != 0 || buffer[3] != 1))) throw new SrapException("invalid_argument");
            collecting = false;
            return buffer.AsMemory(0, length);
        }
    }
}
