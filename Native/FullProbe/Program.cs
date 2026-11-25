using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using QRCoder;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int failures;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Supply a new, private output directory.");
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        Console.WriteLine($"architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} dynamic-code={RuntimeFeature.IsDynamicCodeSupported} os={Environment.OSVersion}");
        await Run("android.resources-settings", () => AndroidState(root));
        await Run("adb.socket-file-owner", () => FileTransferProbe.RunAsync(root));
        await Run("adb.loopback-tls-pairing", ProbeAdbPairingAsync);
        await Run("adb.bounded-outbound", () => QueueProbe.AdbAsync(root));
        await Run("display.bounded-controls", QueueProbe.VideoAsync);
        await Run("qrcoder.wic", () => { CheckQr(); return Task.CompletedTask; });
        await Run("android.opus", ProbeOpusAsync);
        await Run("airplay.eldaudio", () => { ProbeEld(); return Task.CompletedTask; });
        await Run("airplay.aac-alac-mft", () => { ProbeAudioTransforms(); return Task.CompletedTask; });
        await Run("airplay.rtsp-localhost", ProbeAirPlayAsync);
        await Run("native-window.d3d11.mf-h264", ProbeVideoAsync);
        await Run("wasapi.phone-shared-render", ProbeWasapiAsync);
        return failures == 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task> action)
    {
        try { await action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name} {ex}"); }
    }

    internal static void Require(bool ok, string detail)
    {
        if (!ok) throw new InvalidDataException(detail);
    }

    private static async Task AndroidState(string root)
    {
        var bundle = new AndroidRuntimeBundle(Path.Combine(root, "runtime"));
        await bundle.PrepareAsync("audio", CancellationToken.None);
        byte[] jar = await File.ReadAllBytesAsync(Path.Combine(bundle.DirectoryPath, "sonicroute-catalog.jar"));
        Require(Convert.ToHexString(SHA256.HashData(jar)) == "7FB06D89DB79D051DE5B2911BA1BD028FD294C96EE9AB1A7823783250DDD3B4C", "JAR checksum");
        var phone = new AndroidDevice("probe-usb", "Probe", "device", false, "probe-hardware");
        var app = new AndroidApp("org.sonicroute.probe", "音符", 10001, "media");
        string path = Path.Combine(root, "phone.json");
        var store = new AndroidPhoneStore(path);
        store.RememberDevice(phone);
        store.Retain(app);
        await store.FlushAsync();
        Require(new AndroidPhoneStore(path).Profile.RetainedApps.Any(a => a.Name == app.Name), "profile serialization");
        string audioPath = Path.Combine(root, "audio.json");
        var settings = new AndroidAudioSettingsStore(audioPath);
        settings.Remember(phone, app.Package, 37, "probe-output");
        await settings.FlushAsync();
        Require(new AndroidAudioSettingsStore(audioPath).Get(phone, app.Package).Volume == 37, "stream settings serialization");
    }

    private static async Task ProbeAdbPairingAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var temporary = Directory.CreateTempSubdirectory("sonicroute-aot-pair-");
        try
        {
            using var keys = new AdbKeyStore(temporary.FullName);
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Local FullProbe", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var self = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            using var certificate = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), ReadOnlySpan<char>.Empty,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            async Task<string> AcceptAsync()
            {
                using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
                return await SspiPairingTls.AcceptPairingServerAsync(peer.GetStream(), certificate, "482913", "probe-guid", deadline.Token);
            }
            Task<string> accept = AcceptAsync();
            string status = await SspiPairingTls.PairAsync("127.0.0.1", port, "482913", keys, deadline.Token);
            Require(status.EndsWith("[guid=probe-guid]", StringComparison.Ordinal) && await accept == keys.GetPublicKeyLine(),
                "Schannel TLS key export and pairing confirmation");
        }
        finally
        {
            File.Delete(Path.Combine(temporary.FullName, "adbkey"));
            File.Delete(Path.Combine(temporary.FullName, "adbkey.pub"));
            temporary.Delete();
        }
    }

    private static void CheckQr()
    {
        using var code = QRCodeGenerator.GenerateQrCode("SonicRoute-fixture", QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(code);
        byte[] png = renderer.GetGraphic(6);
        Require(WicIconLoader.TryDecodePngToBgra32(png, 0, 0, out var pixels, out int w, out int h)
            && w == h && w == code.ModuleMatrix.Count * 6 && pixels.Contains((byte)0) && pixels.Contains((byte)255), "QR to WIC pixels");
        byte[] icon = WicIconLoader.LoadDefaultPhoneIconPng() ?? throw new InvalidDataException("Phone icon missing");
        nint handle = WicIconLoader.TryCreateIcon(icon, 32);
        Require(handle != 0, "WIC to HICON");
        WicIconLoader.DestroyIconSafe(ref handle);
    }

    private static async Task ProbeOpusAsync()
    {
        var stream = new MemoryStream();
        stream.Write("opus"u8);
        byte[] opusHead = new byte[19];
        "OpusHead"u8.CopyTo(opusHead);
        opusHead[8] = 1;
        opusHead[9] = 2;
        WritePacket(stream, 1UL << 63, opusHead);
        WritePacket(stream, 0, [0xf8, 0xff, 0xfe]);
        stream.Position = 0;
        var pcm = new AndroidPcmSource();
        var receiver = new ScrcpyOpusReceiver();
        bool first = false;
        try { await receiver.ReceiveAsync(stream, pcm, () => first = true, CancellationToken.None); }
        catch (EndOfStreamException) { }
        Require(first && receiver.PacketsDecoded == 1 && pcm.SamplesWritten == 1920, "20 ms Opus silence packet decoded to stereo PCM");
    }

    private static void WritePacket(Stream stream, ulong flags, byte[] data)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(header, flags);
        BinaryPrimitives.WriteInt32BigEndian(header[8..], data.Length);
        stream.Write(header);
        stream.Write(data);
    }

    private static byte[] Resource(string name)
    {
        using var data = typeof(Program).Assembly.GetManifestResourceStream("FullProbe." + name) ?? throw new IOException(name);
        using var bytes = new MemoryStream();
        data.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void ProbeEld()
    {
        foreach ((int frames, int rate) in new[] { (480,44100), (512,48000) })
        {
            byte[] data = Resource($"eld-{frames}.au");
            var decoder = new AacEldDecoder(new AirPlayFormat(AirPlayFormat.Eld, rate, 16, frames));
            var pcm = new float[frames * 2];
            int offset = 1 + data[0], count = 0;
            while (offset < data.Length)
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
                Require(decoder.Decode(data.AsSpan(offset + 2, length), pcm) == pcm.Length, "ELD frame length");
                count++;
                offset += 2 + length;
            }
            Require(count == 16 && pcm.Any(v => Math.Abs(v) > .02f), "16 non-silent ELD frames");
        }
    }

    private static void ProbeAudioTransforms()
    {
        foreach ((string name, int codec, int frames) in new[]
        {
            ("aac", AirPlayFormat.Aac, 1024), ("alac", AirPlayFormat.Alac, 4096)
        })
        {
            using var decoder = new AirPlayDecoder(new AirPlayFormat(codec, 44100, 16, frames));
            byte[] encoded = Resource(name + ".packets");
            var pcm = new float[frames * 4];
            int samples = 0;
            float peak = 0;
            double largestAlacError = 0;
            for (int offset = 0; offset < encoded.Length;)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset));
                int count = decoder.Decode(encoded.AsSpan(offset + 4, length), pcm);
                if (name == "alac")
                {
                    Require(count == frames * 2, "One ALAC access unit must produce one stereo block");
                    for (int i = 0; i < count; i++)
                    {
                        int frame = (samples + i) / 2;
                        double value = 0.42 * Math.Sin(2 * Math.PI * 437 * frame / 44100);
                        double expected = (int)(value * 32767) / 32768d;
                        largestAlacError = Math.Max(largestAlacError, Math.Abs(expected - pcm[i]));
                    }
                }
                for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(pcm[i]));
                samples += count;
                offset += length + 4;
            }
            Require(samples >= frames * 2 && peak is > 0.1f and < 1f, $"{name} samples={samples}, peak={peak}");
            if (name == "alac") Require(samples == frames * 2 * 3 && largestAlacError < 0.00004, "ALAC lossless tone mismatch " + largestAlacError);
            Console.WriteLine($"{name} decodedSamples={samples} peak={peak:F4}");
            decoder.Flush();
        }
    }

    private static async Task ProbeAirPlayAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var receiver = new AirPlayReceiver("Phase1", loopback: true);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, receiver.Port, deadline.Token);
        var socket = client.GetStream();
        await socket.WriteAsync("OPTIONS * RTSP/1.0\r\nCSeq: 1\r\n\r\n"u8.ToArray(), deadline.Token);
        using var reader = new StreamReader(socket, Encoding.ASCII);
        string? status = await reader.ReadLineAsync(deadline.Token);
        Require(status == "RTSP/1.0 200 OK", "Actual RTSP receiver reply: " + status);
    }

    private static async Task ProbeVideoAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var window = new NativeVideoWindow("Phase 1 native video probe", true, false, null, 0, 0, 128, 128, acceptsGamepad:false);
        nint hwnd = await window.CreateAsync(timeout.Token);
        using (var renderer = new D3D11VideoSurfaceRenderer(hwnd, 128, 128))
        using (var decoder = new MediaFoundationH264Decoder(0, 64, 64))
        {
            int frames = 0;
            void Present(ReadOnlySpan<byte> nv12, int stride, int height, int x, int y, int width, int cropHeight, VideoColorMatrix matrix)
            {
                Require(width == 64 && cropHeight == 64 && nv12.Length >= 6144, "Decoded fixture geometry");
                renderer.PresentSoftwareNv12(nv12, stride, height, x, y, width, cropHeight, matrix);
                frames++;
            }
            decoder.DecodePacket(Resource("frame64.h264"), 0, null, Present);
            if (frames == 0) decoder.Drain(null, Present);
            Require(frames > 0 && renderer.HasPresentedFrame, "H264 MFT and DXGI presented frame");
        }
        Require(MediaFoundationH264Decoder.ActiveInstanceCount == 0 && D3D11VideoSurfaceRenderer.ActiveInstanceCount == 0, "Media resources released");
    }

    private static async Task ProbeWasapiAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var subscription = DefaultOutputNotifications.Subscribe(static () => { });
        using var player = new WasapiPcmPlayer(new AndroidPcmSource(), null);
        await player.PlayAndConfirmAsync(timeout.Token);
        player.Stop();
        await player.PlayAndConfirmAsync(timeout.Token);
        player.Stop();
        Console.WriteLine($"WASAPI requestedLatencyMs={player.ActualRequestedLatencyMs} endpointPeriodMs={player.DeviceDefaultPeriodMs:F2}");
    }
}

