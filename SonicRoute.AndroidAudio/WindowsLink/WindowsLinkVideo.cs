using System.Buffers;
using System.Diagnostics;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    private static async Task CaptureVideoAsync(Track track, LinkCaptureTarget target, LinkVideoOptions options,
        TaskCompletionSource<(byte[] Data, long Pts)> first, CancellationToken token)
    {
        LinkVideoOptions clamped = options.Clamp();
        int fps = clamped.MaxFps;
        int bitrate = clamped.BitRate;
        LinkVideoEncoder? encoder = null;
        bool hevc = track.Info.Hevc;
        bool switchedToAvc = false;
        byte[]? scaled = null;
        int width = 0, height = 0;
        int slowFrames = 0;
        bool reduce = false;
        long began = Stopwatch.GetTimestamp();
        try
        {
            await LinkWindowCapture.CaptureAsync(target, (bgra, sourceWidth, sourceHeight, stride) =>
            {
                if (sourceWidth < 2 || sourceHeight < 2 || token.IsCancellationRequested) return;
                var (outputWidth, outputHeight) = clamped.FrameSize(sourceWidth, sourceHeight, reduce);
                if (encoder == null || width != outputWidth || height != outputHeight)
                {
                    encoder?.Dispose();
                    encoder = null;
                    width = outputWidth;
                    height = outputHeight;
                }
                // The NV12 encoder reads only the even portion using the capture's original row pitch.
                if (!clamped.IsNative && (width != sourceWidth || height != sourceHeight))
                {
                    int bytes = width * height * 4;
                    if (scaled == null || scaled.Length < bytes)
                    {
                        if (scaled != null) ArrayPool<byte>.Shared.Return(scaled, clearArray: true);
                        scaled = ArrayPool<byte>.Shared.Rent(bytes);
                    }
                    ScaleBgra(bgra, sourceWidth, sourceHeight, stride, scaled.AsSpan(0, bytes), width, height);
                    bgra = scaled.AsSpan(0, bytes);
                    stride = width * 4;
                }
                long pts = (long)Stopwatch.GetElapsedTime(began).TotalMicroseconds;
                long encodeStarted = Stopwatch.GetTimestamp();
                byte[]? frame;
                bool key;
                try
                {
                    encoder ??= new LinkVideoEncoder(width, height, fps, bitrate, hevc);
                    frame = encoder.Encode(bgra, stride, pts, out key);
                }
                catch (Exception ex) when (hevc && (!first.Task.IsCompleted || clamped.IsNative) && LinkVideoEncoder.IsCodecFailure(ex))
                {
                    hevc = false;
                    // Preserve the offer describing the first buffered keyframe. A later resize
                    // is signalled on all subsequent AVC packets until the viewer sees a keyframe.
                    switchedToAvc = first.Task.IsCompleted;
                    if (!switchedToAvc) track.Info = track.Info with { Hevc = false };
                    encoder?.Dispose();
                    encoder = new LinkVideoEncoder(width, height, fps, bitrate);
                    frame = encoder.Encode(bgra, stride, pts, out key);
                }
                if (!clamped.IsNative && !reduce)
                {
                    bool slow = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds > 45 || track.Connection.VideoBacklogged;
                    slowFrames = slow ? slowFrames + 1 : 0;
                    reduce = slowFrames >= 8;
                }
                if (frame == null) return;
                if (track.Announced) track.Connection.SendVideo(track.Info.Id, frame, pts, key, switchedToAvc);
                else if (key && !first.Task.IsCompleted)
                {
                    if (frame.Length > track.Connection.VideoFrameLimit)
                        throw new NotSupportedException("The initial encoded frame exceeds this peer's supported video frame size.");
                    track.Info = track.Info with { VideoWidth = width, VideoHeight = height };
                    first.TrySetResult((frame, pts));
                }
            }, fps, token).ConfigureAwait(false);
        }
        finally
        {
            encoder?.Dispose();
            if (scaled != null) ArrayPool<byte>.Shared.Return(scaled, clearArray: true);
        }
    }

    private static void ScaleBgra(ReadOnlySpan<byte> input, int sourceWidth, int sourceHeight, int sourceStride,
        Span<byte> output, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                input.Slice((y * sourceHeight / height) * sourceStride, sourceWidth * 4));
            var destination = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                output.Slice(y * width * 4, width * 4));
            for (int x = 0; x < width; x++) destination[x] = source[x * sourceWidth / width];
        }
    }
}
