using System.Buffers;
using System.Diagnostics;

namespace SonicRoute.WindowsLink;

internal sealed partial class WindowsLinkService
{
    private static async Task CaptureWindowAsync(Track track, nint handle, LinkVideoOptions options,
        TaskCompletionSource<(byte[] Data, long Pts)> first, CancellationToken token)
    {
        LinkVideoOptions clamped = options.Clamp();
        int fps = clamped.MaxFps;
        int maxHeight = clamped.MaxHeight;
        int maxWidth = maxHeight >= 1080 ? 1920 : 1280;
        int bitrate = clamped.BitRate;
        LinkH264Encoder? encoder = null;
        byte[]? scaled = null;
        int width = 0, height = 0;
        int slowFrames = 0;
        bool reduce = false;
        long began = Stopwatch.GetTimestamp();
        try
        {
            await LinkWindowCapture.CaptureAsync(handle, (bgra, sourceWidth, sourceHeight, stride) =>
            {
                if (sourceWidth < 2 || sourceHeight < 2 || token.IsCancellationRequested) return;
                double limitW = reduce ? Math.Min(640d, maxWidth) : maxWidth;
                double limitH = reduce ? Math.Min(360d, maxHeight) : maxHeight;
                double scale = Math.Min(1d, Math.Min(limitW / sourceWidth, limitH / sourceHeight));
                int outputWidth = Math.Max(16, (int)(sourceWidth * scale) & ~1);
                int outputHeight = Math.Max(16, (int)(sourceHeight * scale) & ~1);
                if (encoder == null || width != outputWidth || height != outputHeight)
                {
                    encoder?.Dispose();
                    encoder = new LinkH264Encoder(outputWidth, outputHeight, fps, bitrate);
                    width = outputWidth;
                    height = outputHeight;
                }
                if (width != sourceWidth || height != sourceHeight)
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
                byte[]? frame = encoder.Encode(bgra, stride, pts, out bool key);
                if (!reduce)
                {
                    bool slow = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds > 45 || track.Connection.VideoBacklogged;
                    slowFrames = slow ? slowFrames + 1 : 0;
                    reduce = slowFrames >= 8;
                }
                if (frame == null) return;
                if (track.Announced) track.Connection.SendVideo(track.Info.Id, frame, pts, key);
                else if (key) first.TrySetResult((frame, pts));
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
