using System.Diagnostics;
using System.Text.Json;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static void WindowsLinkCodecChecks()
    {
        using var process = Process.GetCurrentProcess();
        const int fps = 15;
        const int framesPerSize = 30;
        int received = 0, keys = 0, encoded = 0;
        int expectedWidth = 640, expectedHeight = 360;
        bool dimensionsMatch = true;
        long bytes = 0;
        var encodeMs = new List<double>();
        var decodeMs = new List<double>();
        DecodedSoftwareFrameHandler frame = (pixels, stride, height, x, y, width, cropHeight, _) =>
        {
            received++;
            dimensionsMatch &= width == expectedWidth && cropHeight == expectedHeight
                && pixels.Length >= stride * height && width > 0;
        };
        var began = Stopwatch.GetTimestamp();
        TimeSpan cpu = process.TotalProcessorTime;
        foreach ((int width, int height) in new[] { (640, 360), (800, 450) })
        {
            expectedWidth = width;
            expectedHeight = height;
            // Independent window sizes use a fresh decoder, matching a session startup.
            using var native = new MediaFoundationH264Decoder(0, width, height);
            using var encoder = new LinkH264Encoder(width, height, fps, 1_600_000);
            byte[] pixels = new byte[width * height * 4];
            for (int i = 0; i < framesPerSize; i++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int position = (y * width + x) * 4;
                        pixels[position] = (byte)(y + i * 5);
                        pixels[position + 1] = (byte)(x + y);
                        pixels[position + 2] = (byte)(x + i * 3);
                        pixels[position + 3] = byte.MaxValue;
                    }
                }
                long pts = i * 1_000_000L / fps;
                long start = Stopwatch.GetTimestamp();
                byte[]? packet = encoder.Encode(pixels, width * 4, pts, out bool key);
                encodeMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (packet == null) continue;
                encoded++;
                bytes += packet.Length;
                if (key) keys++;
                start = Stopwatch.GetTimestamp();
                native.DecodePacket(packet, pts, null, frame);
                decodeMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            native.Drain(null, frame);
        }
        process.Refresh();
        var timing = new
        {
            Resolution = new[] { "640x360", "800x450" }, Fps = fps, Encoded = encoded, Decoded = received,
            Keyframes = keys, H264Bytes = bytes, ElapsedMs = Stopwatch.GetElapsedTime(began).TotalMilliseconds,
            CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            EncodeP95Ms = Percentile95(encodeMs), DecodeP95Ms = Percentile95(decodeMs),
            EstimatedPayloadMbps = bytes * 8.0 * fps / (2 * framesPerSize * 1_000_000),
            HardwareDecoding = false
        };
        Console.WriteLine(JsonSerializer.Serialize(timing));
        Check(encoded >= 58 && received >= 58, "native Media Foundation H.264 encodes and decodes both test sizes");
        Check(keys >= 4 && dimensionsMatch, "native frames have the expected dimensions and periodic keyframes");
    }

    private static double Percentile95(List<double> readings)
    {
        if (readings.Count == 0) return 0;
        readings.Sort();
        return readings[(int)Math.Ceiling(readings.Count * .95) - 1];
    }
}
