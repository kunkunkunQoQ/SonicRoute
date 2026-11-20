using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using SonicRoute.AndroidAudio;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task CheckNativeResolutionAsync(string output)
    {
        var native = new LinkPeerSettings(VideoMaxHeight: 0, VideoMaxFps: 60, VideoBitRate: 24_000_000);
        var options = native.Normalize().VideoOptions().Clamp();
        Check(options.IsNative && options.MaxFps == 60 && options.BitRate == 24_000_000
            && JsonSerializer.Deserialize<LinkPeerSettings>("{}")!.Normalize().VideoMaxHeight == 1080
            && (native with { VideoMaxHeight = 720 }).Normalize().VideoMaxHeight == 720,
            "native preserves frame rate and bitrate; absent and saved legacy presets retain their meaning");
        var legacy = JsonSerializer.Deserialize<LinkHello>("""{"Name":"Earlier","Port":1,"Capabilities":8,"Trusted":true,"Commitment":""}""")!;
        var modern = legacy with { SupportsNativeVideo = true };
        var request = new LinkPullRequest(LinkMediaKind.Window, "desktop", null, null, options.BitRate, options.MaxFps, 0);
        bool oldPeerRejected = false;
        try { options.RequirePeerSupport(legacy); }
        catch (NotSupportedException) { oldPeerRejected = true; }
        var modernRequest = JsonSerializer.Deserialize<LinkPullRequest>(JsonSerializer.Serialize(request with { NativeResolution = true }))!;
        bool invalidRequestRejected = false;
        try { modernRequest.VideoOptions(legacy, null); }
        catch (InvalidDataException) { invalidRequestRejected = true; }
        Check(!legacy.SupportsNativeVideo && oldPeerRejected && invalidRequestRejected && request.VideoOptions(legacy, null).MaxHeight == 1080
            && modernRequest.VideoOptions(modern, null) == options, "legacy zero and explicitly negotiated native resolution are distinct on the wire");
        Check(options.FrameSize(3841, 2161, true) == (3840, 2160) && options.FrameSize(2160, 3840, true) == (2160, 3840)
            && LinkVideoOptions.Default.FrameSize(3840, 2160, false) == (1920, 1080)
            && LinkVideoOptions.Default.FrameSize(3840, 2160, true) == (640, 360)
            && !LinkVideoOptions.ValidFrameSize(8194, 32) && !LinkVideoOptions.ValidFrameSize(8192, 8192)
            && !LinkVideoOptions.ValidFrameSize(3841, 2160),
            "native retains source pixels under load, crops odd edges and bounds untrusted dimensions");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string run = Path.Combine(output, Guid.NewGuid().ToString("N"));
        string aDir = Path.Combine(run, "a");
        await using (var a = new WindowsLinkService(aDir, "Native A", 0, discovery: false))
        await using (var b = new WindowsLinkService(Path.Combine(run, "b"), "Native B", 0, discovery: false))
        {
            a.PairingRequested += p => a.AcceptPairing(p.Id, true);
            b.PairingRequested += p => b.AcceptPairing(p.Id, true);
            await a.EnableAsync(timeout.Token);
            await b.EnableAsync(timeout.Token);
            await b.ConnectAsync(a.LocalAddress, token: timeout.Token);
            await WaitUntilAsync(() => a.Peers.Any(p => p.Connected) && b.Peers.Any(p => p.Connected), timeout.Token);
            var peer = a.Peers.Single(p => p.Connected);
            Check(peer.SupportsNativeVideo && b.Peers.Single(p => p.Connected).SupportsNativeVideo, "both authenticated loopback peers advertise native video");
            a.UpdatePeerSettings(peer.Id, native);
            await using var restored = new WindowsLinkService(aDir, "Native A", 0, discovery: false);
            restored.RestoreSavedPeers();
            Check(restored.GetPeerSettings(peer.Id) == native, "per-peer native resolution survives reloading saved settings");
        }
        CheckNativeWindowCodec(false);
        if (LinkVideoEncoder.SupportsHevc && MediaFoundationH264Decoder.SupportsHevc)
        {
            CheckNativeWindowCodec(true);
            await CheckNativeCodecSwitchAsync();
        }
        else Console.WriteLine($"Native HEVC availability: encode={LinkVideoEncoder.SupportsHevc}, decode={MediaFoundationH264Decoder.SupportsHevc}");
    }

    private static void CheckNativeWindowCodec(bool hevc)
    {
        (int Width, int Height)[] sizes = [(2560, 1440), (3840, 2160), (2160, 3840)];
        if (hevc) sizes = sizes.Where(size => LinkVideoOptions.ValidHevcFrameSize(size.Width, size.Height)).ToArray();
        var received = new HashSet<(int, int)>();
        const int fps = 60;
        using var decoder = new MediaFoundationH264Decoder(0, hevc ? sizes[0].Width : 0, hevc ? sizes[0].Height : 0,
            codec: hevc ? ScrcpyVideoStreamHeader.CodecH265 : ScrcpyVideoStreamHeader.CodecH264);
        DecodedSoftwareFrameHandler frame = (pixels, stride, _, cx, cy, width, height, _) =>
        {
            int row = (cy + height / 2) * stride + cx;
            if (pixels[row + width * 3 / 4] - pixels[row + width / 4] > 80) received.Add((width, height));
        };
        long pts = 0;
        foreach (var (width, height) in sizes)
        {
            using var encoder = new LinkVideoEncoder(width, height, fps, 24_000_000, hevc);
            // Simulate an odd padded source pitch; encode the even rectangle without scaling.
            int stride = (width + 1) * 4;
            byte[] pixels = new byte[stride * height];
            for (int y = 0; y < height; y++)
                pixels.AsSpan(y * stride + width * 2, width * 2).Fill(230);
            for (int i = 0; i < 6; i++, pts += 1_000_000 / fps)
            {
                var packet = encoder.Encode(pixels, stride, pts, out _);
                if (packet != null) decoder.DecodePacket(packet, pts, null, frame);
            }
        }
        decoder.Drain(null, frame);
        Console.WriteLine($"Decoded {(hevc ? "H.265" : "H.264")} native sizes: {string.Join(", ", received)}");
        Check(received.SetEquals(sizes), "the native decoder follows resolution changes and reproduces the source image content");
    }

    private static async Task CheckNativeCodecSwitchAsync()
    {
        bool guarded = false;
        try { using var unsupported = new LinkVideoEncoder(2160, 3840, 30, 24_000_000, hevc: true); }
        catch (NotSupportedException) { guarded = true; }
        Check(guarded, "portrait 4K is rejected before entering the out-of-range Windows HEVC transform");
        var info = new LinkMedia(Guid.NewGuid(), "fixture", LinkMediaKind.Window, "Native resize test", true, 1, false, null, false)
            { Hevc = true, NativeResolution = true };
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var viewer = new LinkVideoViewer(info, null, error => finished.TrySetResult(error), hevc: true,
            width: 640, height: 360, frameLimit: LinkConnection.MaxVideoFrame);
        var field = typeof(LinkVideoViewer).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long pts = 0;
        async Task Present(int width, int height, bool switched)
        {
            using var encoder = new LinkVideoEncoder(width, height, 30, 24_000_000, !switched);
            byte[] pixels = new byte[width * height * 4];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            do
            {
                if (finished.Task.IsCompleted) throw await finished.Task ?? new IOException("Viewer closed unexpectedly.");
                var data = encoder.Encode(pixels, width * 4, pts, out bool key);
                if (data != null)
                {
                    for (int at = 0; at < data.Length; at += LinkConnection.VideoChunk)
                    {
                        int count = Math.Min(data.Length - at, LinkConnection.VideoChunk);
                        byte[] chunk = new byte[count + 9];
                        chunk[0] = (byte)((at == 0 ? 1 : 0) | (at + count == data.Length ? 2 : 0) | (key ? 4 : 0) | (switched ? 8 : 0));
                        BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(1), pts);
                        data.AsSpan(at, count).CopyTo(chunk.AsSpan(9));
                        viewer.Push(chunk);
                    }
                }
                pts += 33333;
                await Task.Delay(60, timeout.Token);
            } while (field.GetValue(viewer) is not NativeVideoWindow window || window.ContentWidth != width || window.ContentHeight != height);
        }
        await Present(640, 360, false);
        // A resize's first AVC keyframe was lost. Its dependent packets must not go to the HEVC decoder.
        byte[] delta = new byte[10];
        delta[0] = 1 | 2 | 8;
        BinaryPrimitives.WriteInt64LittleEndian(delta.AsSpan(1), pts);
        viewer.Push(delta);
        pts += 33333;
        await Present(2160, 3840, true);
        Check(!finished.Task.IsCompleted, "the open HEVC viewer waits for a marked AVC keyframe and renders 2160 × 3840 in the same window");
    }
}
