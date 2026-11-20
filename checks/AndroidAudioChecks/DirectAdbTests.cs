using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CheckDirectAdbAsync()
    {
        CheckAdbKeyStore();
        CheckSpake25519Vectors();
        await CheckSspiPairingLoopbackAsync().ConfigureAwait(false);
        await CheckAdbMuxWireProtocolAsync().ConfigureAwait(false);
        await CheckDirectAndArbitratedTransportAsync().ConfigureAwait(false);
    }

    private static void CheckAdbKeyStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sonicroute-adbkey-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] aospBlob1;
            string line1;
            string fp1;
            byte[] token = RandomNumberGenerator.GetBytes(20);
            byte[] sig;
            using (var ks1 = new AdbKeyStore(tempDir))
            {
                Check(!Directory.Exists(tempDir), "AdbKeyStore defers key creation until first access");
                aospBlob1 = ks1.GetAospPublicKeyBlob();
                Check(File.Exists(ks1.PrivateKeyPath) && File.Exists(ks1.PublicKeyPath),
                    "AdbKeyStore generates PKCS#8 private key and adbkey.pub on first access");
                line1 = ks1.GetPublicKeyLine();
                fp1 = ks1.GetFingerprint();
                sig = ks1.SignAuthToken(token);
                var cert = ks1.GetTlsClientCertificate();
                using var certRsa = cert.GetRSAPublicKey();
                Check(cert.HasPrivateKey && certRsa != null && aospBlob1.Length == 524,
                    "AdbKeyStore produces 524-byte AOSP key blob and TLS client certificate with private key");
            }

            using (var ks2 = new AdbKeyStore(tempDir))
            {
                Check(ks2.GetAospPublicKeyBlob().SequenceEqual(aospBlob1)
                    && ks2.GetPublicKeyLine() == line1
                    && ks2.GetFingerprint() == fp1
                    && ks2.GetFingerprint() == AndroidWirelessConnection.PairingFingerprint(line1),
                    "reloading AdbKeyStore preserves exact AOSP key blob, public key line, and MD5 fingerprint");

                using var rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(ks2.PrivateKeyPath, Encoding.UTF8));
                Check(rsa.VerifyHash(token, sig, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1),
                    "AdbKeyStore AUTH signature verifies against loaded RSA-2048 private key");

                // Validate AOSP Montgomery modulus/n0inv/rr invariants mathematically.
                uint words = BinaryPrimitives.ReadUInt32LittleEndian(aospBlob1.AsSpan(0, 4));
                uint n0inv = BinaryPrimitives.ReadUInt32LittleEndian(aospBlob1.AsSpan(4, 4));
                var modulus = new BigInteger(aospBlob1.AsSpan(8, 256), isUnsigned: true, isBigEndian: false);
                var rr = new BigInteger(aospBlob1.AsSpan(264, 256), isUnsigned: true, isBigEndian: false);
                uint exponent = BinaryPrimitives.ReadUInt32LittleEndian(aospBlob1.AsSpan(520, 4));
                var p = rsa.ExportParameters(false);
                var expectedModulus = new BigInteger(p.Modulus!, isUnsigned: true, isBigEndian: true);
                uint n0 = (uint)(modulus & 0xFFFFFFFFu);
                Check(words == 64
                    && modulus == expectedModulus
                    && unchecked(n0 * n0inv) == uint.MaxValue
                    && rr == BigInteger.ModPow(2, 4096, expectedModulus)
                    && exponent == 65537,
                    "AOSP RSA Montgomery blob n0inv, modulus, R^2 mod N, and exponent match spec");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void CheckSpake25519Vectors()
    {
        byte[] pw = "123456"u8.ToArray();
        byte[] entropyA = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
        byte[] entropyB = Enumerable.Range(0, 64).Select(i => (byte)(200 - i)).ToArray();

        var alice = new Spake25519.HandshakeSession(isAlice: true, pw, entropyA);
        var bob = new Spake25519.HandshakeSession(isAlice: false, pw, entropyB);
        var wrongBob = new Spake25519.HandshakeSession(isAlice: false, "654321"u8, entropyB);

        byte[] keyAlice = alice.ProcessPeerMessage(bob.MyMessage);
        byte[] keyBob = bob.ProcessPeerMessage(alice.MyMessage);
        byte[] keyWrong = wrongBob.ProcessPeerMessage(alice.MyMessage);

        Check(keyAlice.Length == 64 && keyAlice.SequenceEqual(keyBob) && !keyAlice.SequenceEqual(keyWrong),
            "Spake25519 Alice/Bob key exchange converges on identical 64-byte key and rejects wrong PIN");

        using var aesAlice = new Spake25519.PairingAesCipher(keyAlice);
        using var aesBob = new Spake25519.PairingAesCipher(keyBob);
        using var aesWrong = new Spake25519.PairingAesCipher(keyWrong);

        byte[] infoBytes = Spake25519.EncodePeerInfo(Spake25519.PeerInfoDeviceGuid, "adb-test-guid-99");
        Check(infoBytes.Length == Spake25519.PeerInfoSize, "PeerInfo wire record is fixed 8192 bytes");
        byte[] cipher0 = aesAlice.Encrypt(infoBytes);
        byte[] plain0 = aesBob.Decrypt(cipher0);
        var decoded = Spake25519.DecodePeerInfo(plain0);
        Check(decoded.Type == Spake25519.PeerInfoDeviceGuid && decoded.Value == "adb-test-guid-99",
            "PairingAesCipher HKDF-SHA256 AES-128-GCM decrypts 8192-byte PeerInfo cleanly");

        bool wrongPinFailed = false;
        try { _ = aesWrong.Decrypt(cipher0); } catch (CryptographicException) { wrongPinFailed = true; }
        byte[] cipher1 = aesAlice.Encrypt("second-frame"u8);
        cipher1[0] ^= 0xFF;
        bool tamperFailed = false;
        try { _ = aesBob.Decrypt(cipher1); } catch (CryptographicException) { tamperFailed = true; }
        Check(wrongPinFailed && tamperFailed,
            "PairingAesCipher rejects wrong-PIN decryption and ciphertext tampering");
    }

    private static async Task CheckSspiPairingLoopbackAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sonicroute-sspi-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var clientKeyStore = new AdbKeyStore(tempDir);
            using var serverCert = CreateLoopbackServerCert("CN=SonicRouteTestPairing");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;

                // Matching PIN loopback TLS 1.3 + SECPKG_ATTR_KEYING_MATERIAL + SPAKE2
                var acceptTask = Task.Run(async () =>
                {
                    using TcpClient accepted = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    accepted.NoDelay = true;
                    return await SspiPairingTls.AcceptPairingServerAsync(
                        accepted.GetStream(), serverCert, "482913", "adb-loopback-guid", default).ConfigureAwait(false);
                });

                string pairResult = await SspiPairingTls.PairAsync("127.0.0.1", port, "482913", clientKeyStore, default).ConfigureAwait(false);
                string receivedClientKey = await acceptTask.ConfigureAwait(false);

                Check(pairResult == $"Successfully paired to 127.0.0.1:{port} [guid=adb-loopback-guid]"
                    && receivedClientKey == clientKeyStore.GetPublicKeyLine(),
                    "SspiPairingTls completes TLS 1.3 KeyingMaterial + SPAKE2 + PeerInfo exchange over loopback");

                // Wrong PIN must be rejected by AEAD verification
                var rejectAccept = Task.Run(async () =>
                {
                    using TcpClient accepted = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    accepted.NoDelay = true;
                    await SspiPairingTls.AcceptPairingServerAsync(
                        accepted.GetStream(), serverCert, "482913", "adb-loopback-guid", default).ConfigureAwait(false);
                });

                bool clientFailed = false;
                try
                {
                    await SspiPairingTls.PairAsync("127.0.0.1", port, "999999", clientKeyStore, default).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CryptographicException or IOException)
                {
                    clientFailed = true;
                }
                bool serverFailed = false;
                try { await rejectAccept.ConfigureAwait(false); }
                catch (Exception ex) when (ex is CryptographicException or IOException) { serverFailed = true; }
                Check(clientFailed && serverFailed, "SspiPairingTls rejects mismatched 6-digit pairing code");
            }
            finally
            {
                listener.Stop();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task CheckAdbMuxWireProtocolAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sonicroute-mux-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            using var keyStore = new AdbKeyStore(tempDir);
            byte[] trustedKeyBytes = Encoding.UTF8.GetBytes(keyStore.GetPublicKeyLine());

            // 1) RSA AUTH (unknown key -> prompt RSAPUBLICKEY -> CNXN) + concurrent mux + chunking + sync push + v1 fallback + flow control
            await using (var usbSim = new FakeAdbdServer(useTls: false, requirePublicKeyFallback: true, trustedKeyBytes, peerMaxPayload: 4096))
            {
                var link = await OpenClientLinkAsync(usbSim.Port, supportsTlsUpgrade: false).ConfigureAwait(false);
                await using var mux = await AdbMuxConnection.ConnectAsync(link, keyStore).ConfigureAwait(false);
                Check(usbSim.ReceivedSignatureAuth && usbSim.ReceivedPublicKeyAuth
                    && mux.DeviceProperties.GetValueOrDefault("ro.product.model") == "Pixel_Mux"
                    && mux.Features.Contains("shell_v2"),
                    "AdbMuxConnection handles RSA AUTH signature, public-key fallback, and banner parsing");

                // shell_v2 + shell v1 fallback
                var v2 = await mux.RunShellAsync("echo hello_v2", default).ConfigureAwait(false);
                Check(v2.Output == "hello_v2\n" && v2.ExitCode == 0 && v2.Error == "warn\n",
                    "AdbMuxConnection decodes shell,v2,raw stdout/stderr/exit over mux stream");

                var v1 = await mux.RunShellAsync("legacy_v1", default).ConfigureAwait(false);
                Check(v1.Output == "v1_ok\n" && v1.ExitCode == 0,
                    "AdbMuxConnection falls back to shell: v1 when peer rejects shell,v2,raw");

                // sync: push > 64 KiB across PeerMaxPayload = 4096 chunks
                byte[] srcBytes = new byte[140 * 1024];
                RandomNumberGenerator.Fill(srcBytes);
                string pushFile = Path.Combine(tempDir, "payload.bin");
                await File.WriteAllBytesAsync(pushFile, srcBytes).ConfigureAwait(false);
                await mux.PushAsync(pushFile, "/data/local/tmp/payload.bin", default).ConfigureAwait(false);
                Check(usbSim.LastPushedBytes != null && usbSim.LastPushedBytes.SequenceEqual(srcBytes)
                    && usbSim.LastPushRemotePath == "/data/local/tmp/payload.bin",
                    "AdbMuxConnection pushes >140 KiB files over sync: respecting PeerMaxPayload chunking and write credits");

                // Concurrent multiplexed localabstract streams + backpressure deferred OKAY (> 4 MiB burst)
                await using Stream echo = await mux.OpenLocalAbstractAsync("sonicroute_echo", default).ConfigureAwait(false);
                await using Stream burst = await mux.OpenLocalAbstractAsync("sonicroute_burst", default).ConfigureAwait(false);

                byte[] ping = "concurrent-mux-frame"u8.ToArray();
                await echo.WriteAsync(ping, default).ConfigureAwait(false);
                byte[] pong = new byte[ping.Length];
                await echo.ReadExactlyAsync(pong, default).ConfigureAwait(false);

                // While burst is buffered past HighWaterMarkBytes without reading, verify OKAY pauses and echo stays unblocked!
                await usbSim.BurstHighWaterReached.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await echo.WriteAsync("still-alive"u8.ToArray(), default).ConfigureAwait(false);
                byte[] aliveReply = new byte[11];
                await echo.ReadExactlyAsync(aliveReply, default).ConfigureAwait(false);

                byte[] burstTotal = new byte[FakeAdbdServer.BurstTotalBytes];
                int readOff = 0;
                while (readOff < burstTotal.Length)
                {
                    int n = await burst.ReadAsync(burstTotal.AsMemory(readOff)).ConfigureAwait(false);
                    if (n == 0) break;
                    readOff += n;
                }
                Check(pong.SequenceEqual(ping)
                    && Encoding.UTF8.GetString(aliveReply) == "still-alive"
                    && readOff == FakeAdbdServer.BurstTotalBytes
                    && burstTotal[^1] == FakeAdbdServer.BurstSentinelByte,
                    "AdbMuxConnection deferred-OKAY backpressure prevents OOM without blocking sibling mux streams");
            }

            // 2) STLS -> TLS 1.3 mTLS upgrade + post-TLS CNXN + shell
            using var serverCert = CreateLoopbackServerCert("CN=FakeAdbdTlsServer");
            await using (var tlsSim = new FakeAdbdServer(useTls: true, requirePublicKeyFallback: false, trustedKeyBytes, peerMaxPayload: 256 * 1024, serverCert: serverCert))
            {
                var link = await OpenClientLinkAsync(tlsSim.Port, supportsTlsUpgrade: true).ConfigureAwait(false);
                await using var mux = await AdbMuxConnection.ConnectAsync(link, keyStore).ConfigureAwait(false);
                var tlsShell = await mux.RunShellAsync("echo tls_ok", default).ConfigureAwait(false);
                Check(mux.IsTls
                    && tlsSim.VerifiedClientTlsCert
                    && !string.IsNullOrEmpty(mux.PeerCertificateSha256)
                    && tlsShell.Output == "tls_ok\n",
                    "AdbMuxConnection upgrades STLS to TLS 1.3 mTLS and runs encrypted shell commands");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task CheckDirectAndArbitratedTransportAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sonicroute-arb-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            using var keyStore = new AdbKeyStore(tempDir);
            byte[] keyBytes = Encoding.UTF8.GetBytes(keyStore.GetPublicKeyLine());

            // Verify fast <200ms loopback closed-port detection (no 2s Windows SYN retry penalty)
            int closedPort;
            var probeTmp = new TcpListener(IPAddress.Loopback, 0);
            probeTmp.Start();
            closedPort = ((IPEndPoint)probeTmp.LocalEndpoint).Port;
            probeTmp.Stop();

            var clock = Stopwatch.StartNew();
            bool closedReachable = await AdbSocketClient.IsServerReachableAsync(new IPEndPoint(IPAddress.Loopback, closedPort)).ConfigureAwait(false);
            clock.Stop();
            Check(!closedReachable && clock.ElapsedMilliseconds < 400,
                $"IsServerReachableAsync rejects unbound loopback port without 2s SYN retry ({clock.ElapsedMilliseconds}ms)");

            // DirectAdbTransport fake USB device + event-driven TrackDevices + forwarder + PairAsync + ConnectAsync
            await using var usbPhone = new FakeAdbdServer(useTls: false, requirePublicKeyFallback: false, keyBytes, peerMaxPayload: 64 * 1024);
            using var tlsCert = CreateLoopbackServerCert("CN=FakeWifiPhone");
            await using var wifiPhone = new FakeAdbdServer(useTls: true, requirePublicKeyFallback: false, keyBytes, peerMaxPayload: 64 * 1024, serverCert: tlsCert);

            var usbPresent = false;
            const string usbInterfacePath = @"\\?\usb#vid_18d1&pid_4ee7#FAKEUSB01#{f72fe0d4-cbcb-407d-8814-9ed673d0dd6b}";
            string? blockedUsbPath = Path.Combine(tempDir, "exclusive-usb-fixture");
            Action? usbChangedCallback = null;
            using var watcherToken = new CancellationTokenSource();

            var direct = new DirectAdbTransport(
                keyStore,
                ownsKeyStore: false,
                enumerateUsb: () => usbPresent
                    ? [new WinUsbDeviceDescriptor(usbInterfacePath, @"USB\VID_18D1&PID_4EE7\FAKEUSB01")]
                    : [],
                openUsbLink: _ =>
                {
                    if (blockedUsbPath != null) return WinUsbAdbLink.TryOpen(blockedUsbPath);
                    var tcp = new TcpClient { NoDelay = true };
                    tcp.Connect(IPAddress.Loopback, usbPhone.Port);
                    return new StreamAdbPacketLink(tcp.GetStream(), tcp, supportsTlsUpgrade: false);
                },
                watchUsb: cb =>
                {
                    usbChangedCallback = cb;
                    return new CallbackDisposable(() => usbChangedCallback = null);
                },
                pairImpl: (host, port, pw, _) => Task.FromResult($"Successfully paired to {host}:{port} [guid=adb-direct-guid]"));

            var trackedSnapshots = new ConcurrentQueue<IReadOnlyList<AndroidDevice>>();
            using var trackCts = new CancellationTokenSource();
            var trackTask = direct.TrackDevicesAsync(list => trackedSnapshots.Enqueue(list), trackCts.Token);

            // Initial snapshot empty, then USB arrives via event
            await WaitUntilAsync(() => trackedSnapshots.Count >= 1, TimeSpan.FromSeconds(3));
            using (var held = new FileStream(blockedUsbPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.DeleteOnClose))
            {
                usbPresent = true;
                var blocked = await direct.DevicesAsync(default);
                var savedWifi = new AndroidDevice("192.0.2.1:40123", "Fixture", "device", true, "fakeusb01");
                Check(DirectAdbTransport.UsbAccessError(blocked, savedWifi) == AndroidErrorCode.UsbInterfaceInUse
                    && DirectAdbTransport.UsbAccessError(blocked, savedWifi with { HardwareId = "another-phone" }) == null
                    && blocked.All(d => d.State != "device" && d.UsbInterfacePath == usbInterfacePath),
                    "Win32 sharing violation is attributed only to the matching USB interface and cannot admit a phone");
            }
            Check((await direct.DevicesAsync(default)).Count == 0,
                "a removed USB path clears the access diagnostic on the next scan");
            blockedUsbPath = null;
            usbChangedCallback?.Invoke();
            await WaitUntilAsync(() => trackedSnapshots.Any(s => s.Any(d => d.Serial == "FAKEUSB01" && d.State == "device")), TimeSpan.FromSeconds(3));

            int fwdPort = await direct.ForwardTcpToLocalAbstractAsync("FAKEUSB01", "sonicroute_echo", default).ConfigureAwait(false);
            var forwards = await direct.ListForwardsAsync(default).ConfigureAwait(false);
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, fwdPort).ConfigureAwait(false);
                byte[] hello = "forward-ok"u8.ToArray();
                await client.GetStream().WriteAsync(hello).ConfigureAwait(false);
                byte[] reply = new byte[hello.Length];
                await client.GetStream().ReadExactlyAsync(reply).ConfigureAwait(false);
                Check(reply.SequenceEqual(hello) && forwards.Any(f => f.Serial == "FAKEUSB01" && f.Local == "tcp:" + fwdPort),
                    "DirectAdbTransport local loopback forward bridges to localabstract cleanly");
            }
            await direct.RemoveForwardAsync("FAKEUSB01", "tcp:" + fwdPort, default).ConfigureAwait(false);
            Check((await direct.ListForwardsAsync(default).ConfigureAwait(false)).Count == 0,
                "DirectAdbTransport removes TCP forward rule cleanly");

            string connectMsg = await direct.ConnectAsync("127.0.0.1:" + wifiPhone.Port, default).ConfigureAwait(false);
            var bothDevices = await direct.DevicesAsync(default).ConfigureAwait(false);
            string pairMsg = await direct.PairAsync("127.0.0.1:37123", "123456", default).ConfigureAwait(false);
            Check(connectMsg == "connected to 127.0.0.1:" + wifiPhone.Port
                && bothDevices.Any(d => d.Serial == "FAKEUSB01" && !d.IsWifi)
                && bothDevices.Any(d => d.Serial == "127.0.0.1:" + wifiPhone.Port && d.IsWifi)
                && pairMsg == "Successfully paired to 127.0.0.1:37123 [guid=adb-direct-guid]",
                "DirectAdbTransport exposes USB + Wi-Fi TLS devices and wireless pairing");
            string admittedWifi = AdbTransportTarget.Create(bothDevices.Single(d => d.IsWifi), direct)!;
            using var pinnedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Check((await direct.RunShellAsync(admittedWifi, "echo tls_ok", pinnedDeadline.Token)).Output == "tls_ok\n",
                "transport ID selector reaches the already admitted Wi-Fi connection");

            var oldUsb = bothDevices.Single(d => !d.IsWifi);
            Check(oldUsb.UsbInterfacePath == usbInterfacePath && bothDevices.Single(d => d.IsWifi).UsbInterfacePath == null,
                "only the USB attachment carries its exact Windows interface path for device-specific removal");
            string oldUsbTarget = AdbTransportTarget.Create(oldUsb, direct)!;
            var wifi = bothDevices.Single(d => d.IsWifi);
            using (var keepWifi = await direct.OpenLocalAbstractAsync(admittedWifi, "sonicroute_echo", pinnedDeadline.Token))
            {
                usbPresent = false;
                usbChangedCallback!.Invoke();
                await WaitUntilAsync(() => trackedSnapshots.Any(s => s.Count == 1 && s[0] == wifi), TimeSpan.FromSeconds(3));
                byte[] marker = "usb-unplugged"u8.ToArray();
                await keepWifi.WriteAsync(marker, pinnedDeadline.Token);
                var echoed = new byte[marker.Length];
                await keepWifi.ReadExactlyAsync(echoed, pinnedDeadline.Token);
                Check(echoed.SequenceEqual(marker),
                    "USB removal updates the tracked list and keeps the existing Wi-Fi stream alive");
            }

            usbPresent = true;
            usbChangedCallback!();
            await WaitUntilAsync(() => trackedSnapshots.Any(s => s.Contains(wifi)
                && s.Any(d => d.State == "device" && d.Serial == oldUsb.Serial && d.TransportId != oldUsb.TransportId)), TimeSpan.FromSeconds(3));
            bool staleUsbRejected = false;
            try { await direct.GetSerialNoAsync(oldUsbTarget, pinnedDeadline.Token); }
            catch (IOException) { staleUsbRejected = true; }
            Check(staleUsbRejected, "replugging the same USB path and serial rejects the previous USB transport ID");

            await using (var adbViaDirect = new AdbClient(tempDir, direct))
            {
                string usbShell = await adbViaDirect.RunAsync(default, "-s", "FAKEUSB01", "shell", "echo", "hello_v2").ConfigureAwait(false);
                string wifiShell = await adbViaDirect.RunAsync(default, "-s", "127.0.0.1:" + wifiPhone.Port, "shell", "echo", "tls_ok").ConfigureAwait(false);
                Check(usbShell == "hello_v2\n" && wifiShell == "tls_ok\n",
                    "AdbClient.RunAsync routes -s <serial> shell ... cleanly over DirectAdbTransport");
            }

            trackCts.Cancel();
            try { await trackTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

            // Arbitration test: when fake 5037 server runs, ArbitratedAdbTransport selects socket and releases direct USB!
            var fake5037 = new FakeSmartSocket5037();
            int directEnumCalls = 0;
            var gatedDirect = new DirectAdbTransport(
                keyStore,
                ownsKeyStore: false,
                enumerateUsb: () => { Interlocked.Increment(ref directEnumCalls); return []; });
            await using var arbiter = new ArbitratedAdbTransport(
                token => AdbSocketClient.IsServerReachableAsync(new IPEndPoint(IPAddress.Loopback, fake5037.Port), token),
                new AdbSocketClient(new IPEndPoint(IPAddress.Loopback, fake5037.Port)),
                gatedDirect,
                ownsDirect: true);

            fake5037.DevicesResponse = "5037-SERIAL\tdevice model:Socket5037\n";
            var via5037 = await arbiter.DevicesAsync(default).ConfigureAwait(false);
            Check(via5037.Count == 1 && via5037[0].Serial == "5037-SERIAL" && directEnumCalls == 0,
                "ArbitratedAdbTransport defers to external 5037 daemon without opening Direct USB handles");

            await using (var mixed = new ArbitratedAdbTransport(_ => Task.FromResult(true),
                new AdbSocketClient(new IPEndPoint(IPAddress.Loopback, fake5037.Port)), direct))
            {
                string address = "127.0.0.1:" + wifiPhone.Port;
                fake5037.DevicesResponse += address + "\toffline\n";
                var listed = await mixed.DevicesAsync(default);
                Check(listed.Count(d => d.Serial == address) == 1 && listed.Any(d => d.Serial == address && d.State == "device")
                    && listed.Any(d => d.Serial == "5037-SERIAL"), "starting external ADB keeps the owned Wi-Fi phone online alongside daemon devices");
                var reply = await mixed.RunShellAsync(address, "echo tls_ok", default);
                Check(reply.Output == "tls_ok\n", "commands to the live Wi-Fi phone keep using its original TLS connection");
                await mixed.DisconnectAsync(address, default);
                Check(!(await mixed.DevicesAsync(default)).Any(d => d.Serial == address && d.State == "device"),
                    "explicit disconnect releases the original Wi-Fi transport while the external daemon is present");
            }

            await direct.ConnectAsync("127.0.0.1:" + wifiPhone.Port, default);
            var replacement = (await direct.DevicesAsync(default)).Single(d => d.IsWifi);
            string replacementTarget = AdbTransportTarget.Create(replacement, direct)!;
            bool oldRejected = false;
            using var replacementDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await direct.RunShellAsync(admittedWifi, "echo tls_ok", replacementDeadline.Token); }
            catch (IOException) { oldRejected = true; }
            Check(oldRejected && replacementTarget != admittedWifi
                && (await direct.RunShellAsync(replacementTarget, "echo tls_ok", replacementDeadline.Token)).Output == "tls_ok\n",
                "reused Wi-Fi address cannot receive an old connection's commands or cleanup");
            int replacementPort = await direct.ForwardTcpToLocalAbstractAsync(replacementTarget, "sonicroute_echo", default);
            string replacementSpec = "tcp:" + replacementPort;
            await direct.RemoveForwardAsync(admittedWifi, replacementSpec, default);
            Check((await direct.ListForwardsAsync(default)).Any(rule => rule.Serial == replacementTarget && rule.Local == replacementSpec),
                "retired Wi-Fi transport cannot remove a replacement transport's port forward");
            await direct.RemoveForwardAsync(replacementTarget, replacementSpec, default);

            using (var panel = new CancellationTokenSource())
            await using (var adb = new AdbClient(tempDir, direct))
            {
                var lost = new ConcurrentQueue<string>();
                var listening = adb.WatchPanelWirelessAsync(lost.Enqueue, () => { }, panel.Token);
                var shared = new PairingFixtureTransport();
                await using var both = new ArbitratedAdbTransport(_ => Task.FromResult(true), shared, direct);
                int aliasReads = 0;
                var aliasWatch = both.WatchPanelWirelessAsync(_ => { }, () => Interlocked.Increment(ref aliasReads), panel.Token);
                var remote = new FakeAdbdServer(true, false, keyBytes, 64 * 1024, tlsCert);
                string dropped;
                try
                {
                    string address = "127.0.0.1:" + remote.Port;
                    await direct.ConnectAsync(address, default);
                    dropped = AdbTransportTarget.Create((await direct.DevicesAsync(default)).Single(d => d.Serial == address), direct)!;
                    shared.Devices = [new AndroidDevice(address, "external phone", "device", true, TransportId: "42")
                        { ConnectionTarget = AdbTransportTarget.CreateSocket("42", "fixture-daemon") }];
                    var before = await both.DevicesAsync(default);
                    Check(before.Count(d => d.Serial == address) == 1 && !before.Contains(shared.Devices[0]),
                        "one direct Wi-Fi session shadows the external transport at the same phone address");
                }
                finally { await remote.DisposeAsync(); }
                await WaitUntilAsync(() => lost.Contains(dropped), TimeSpan.FromSeconds(3));
                Check(lost.SequenceEqual([dropped]) && direct.OwnsWifiConnection(replacement.Serial),
                    "remote Wi-Fi socket loss emits only the dropped generation and keeps the other phone connected");
                await WaitUntilAsync(() => Volatile.Read(ref aliasReads) == 1, TimeSpan.FromSeconds(3));
                Check((await both.DevicesAsync(default)).Contains(shared.Devices[0]),
                    "loss of a shadowing direct session requests one fresh read to reveal the already connected external phone");
                panel.Cancel();
                try { await Task.WhenAll(listening, aliasWatch); } catch (OperationCanceledException) { }
                await direct.DisconnectAsync(replacementTarget, default);
                Check(lost.SequenceEqual([dropped]), "closing the final panel detaches the direct disconnect callback");
            }

            var initial = wifi with { ConnectionTarget = admittedWifi };
            var fresh = replacement with { ConnectionTarget = replacementTarget };
            var healthy = oldUsb with { ConnectionTarget = oldUsbTarget };
            var pending = new TaskCompletionSource<IReadOnlyList<AndroidDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var discovery = new AndroidDeviceDiscovery(_ => pending.Task);
            discovery.Publish([initial, healthy]);
            var scanning = discovery.ReadAsync(default);
            discovery.RemoveTransport(admittedWifi);
            var third = healthy with { Serial = "third-phone", ConnectionTarget = "third-session" };
            pending.SetResult([initial, fresh, healthy, third]);
            Check((await scanning).SequenceEqual([fresh, healthy, third]), "an in-flight scan drops only the departed generation and publishes other phones");
            discovery.RemoveTransport(admittedWifi);
            Check(discovery.CurrentDevices!.SequenceEqual([fresh, healthy, third]), "a late loss cannot remove a replacement on the same Wi-Fi address");

            using (var panel = new CancellationTokenSource())
            {
                var external = new AdbSocketClient(new IPEndPoint(IPAddress.Loopback, fake5037.Port));
                var lost = new ConcurrentQueue<string>();
                int changed = 0;
                var listening = external.WatchPanelWirelessAsync(lost.Enqueue, () => Interlocked.Increment(ref changed), panel.Token);
                const string usbRow = "USB-SERIAL\tdevice transport_id:9\n";
                const string first = "10.0.0.1:1111\tdevice transport_id:7\n";
                const string second = "10.0.0.2:2222\tdevice transport_id:8\n";
                fake5037.DevicesResponse = first + second + usbRow;
                var startup = await external.DevicesAsync(default);
                await fake5037.TrackingStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                fake5037.SendTrackFrame(second + usbRow);
                await WaitUntilAsync(() => lost.Count == 1, TimeSpan.FromSeconds(3));
                Check(lost.Single() == startup[0].ConnectionTarget && changed == 0, "external ADB loss reports the daemon-qualified Wi-Fi target without refreshing healthy phones");
                fake5037.SendTrackFrame(second);
                await WaitUntilAsync(() => lost.Count == 2, TimeSpan.FromSeconds(3));
                Check(lost.Last() == startup[2].ConnectionTarget, "an external USB loss is qualified by daemon and transport ID, independently of Windows USB paths");
                fake5037.SendTrackFrame(second + "10.0.0.1:1111\tdevice transport_id:10\n");
                await WaitUntilAsync(() => Volatile.Read(ref changed) == 1, TimeSpan.FromSeconds(3));
                Check(lost.Count == 2, "external ADB arrival at a reused address requests one fresh identity read");
                panel.Cancel();
                try { await listening; } catch (OperationCanceledException) { }
            }

            await using (var broken = new FakeSmartSocket5037 { RejectTracking = true, DevicesResponse = "10.0.0.1:1111\tdevice transport_id:7\n" })
            using (var panel = new CancellationTokenSource())
            {
                var external = new AdbSocketClient(new IPEndPoint(IPAddress.Loopback, broken.Port));
                int rereads = 0;
                var listening = external.WatchPanelWirelessAsync(_ => { }, () => Interlocked.Increment(ref rereads), panel.Token);
                await external.DevicesAsync(default);
                await WaitUntilAsync(() => Volatile.Read(ref rereads) == 1, TimeSpan.FromSeconds(3));
                await external.DevicesAsync(default);
                await Task.Delay(350);
                Check(broken.TrackAttempts == 2 && rereads == 1,
                    "persistent external tracking failure is retried once and rereading the same daemon list does not start another attempt");
                broken.DevicesResponse = "10.0.0.1:1111\tdevice transport_id:9\n";
                await external.DevicesAsync(default);
                await WaitUntilAsync(() => broken.TrackAttempts == 3, TimeSpan.FromSeconds(3));
                panel.Cancel();
                try { await listening; } catch (OperationCanceledException) { }
                Check(broken.TrackAttempts == 3 && rereads == 1, "a new external transport re-arms tracking and closing the panel cancels the pending retry");
            }

            await fake5037.DisposeAsync().ConfigureAwait(false);
            string verAfterStop = await arbiter.VersionAsync(default).ConfigureAwait(false);
            var afterStop = await arbiter.DevicesAsync(default).ConfigureAwait(false);
            Check(verAfterStop.Contains("Android Debug Bridge") && verAfterStop.Contains("SonicRoute C#") && directEnumCalls >= 1 && afterStop.Count == 0,
                "ArbitratedAdbTransport fails over to DirectAdbTransport cleanly when external 5037 daemon stops");

            await direct.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task<StreamAdbPacketLink> OpenClientLinkAsync(int port, bool supportsTlsUpgrade)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        return new StreamAdbPacketLink(tcp.GetStream(), tcp, supportsTlsUpgrade);
    }

    private static X509Certificate2 CreateLoopbackServerCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephem = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        byte[] pfx = ephem.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("Timed out waiting for async test condition.");
            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class FakeAdbdServer : IAsyncDisposable
    {
        public const int BurstTotalBytes = 4 * 1024 * 1024 + 512 * 1024;
        public const byte BurstSentinelByte = 0x5A;

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _useTls;
        private readonly bool _requirePublicKeyFallback;
        private readonly byte[] _expectedPubKey;
        private readonly int _peerMaxPayload;
        private readonly X509Certificate2? _serverCert;
        private readonly Task _loop;

        public FakeAdbdServer(bool useTls, bool requirePublicKeyFallback, byte[] expectedPubKey, int peerMaxPayload, X509Certificate2? serverCert = null)
        {
            _useTls = useTls;
            _requirePublicKeyFallback = requirePublicKeyFallback;
            _expectedPubKey = expectedPubKey;
            _peerMaxPayload = peerMaxPayload;
            _serverCert = serverCert;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }
        public bool ReceivedSignatureAuth { get; private set; }
        public bool ReceivedPublicKeyAuth { get; private set; }
        public bool VerifiedClientTlsCert { get; private set; }
        public byte[]? LastPushedBytes { get; private set; }
        public string? LastPushRemotePath { get; private set; }
        public TaskCompletionSource BurstHighWaterReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    _ = HandleClientAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    Stream stream = client.GetStream();
                    byte[] payloadBuf = new byte[2 * 1024 * 1024];

                    // Initial CNXN
                    var (cnxn, _) = await ReadPacketAsync(stream, payloadBuf, _cts.Token).ConfigureAwait(false);
                    if (cnxn.Command != AdbCommands.Cnxn) return;

                    if (_useTls)
                    {
                        await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Stls, AdbMuxConnection.StlsVersion, 0, []), ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                        var (stlsReply, _) = await ReadPacketAsync(stream, payloadBuf, _cts.Token).ConfigureAwait(false);
                        if (stlsReply.Command != AdbCommands.Stls) return;

                        var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, _, _) =>
                        {
                            VerifiedClientTlsCert = cert != null;
                            return cert != null;
                        });
                        stream = ssl;
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _serverCert!,
                            ClientCertificateRequired = true,
                            EnabledSslProtocols = SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                        }, _cts.Token).ConfigureAwait(false);

                    }
                    else
                    {
                        byte[] token1 = RandomNumberGenerator.GetBytes(20);
                        await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Auth, AdbAuthType.Token, 0, token1), token1, _cts.Token).ConfigureAwait(false);
                        var (auth1, auth1Len) = await ReadPacketAsync(stream, payloadBuf, _cts.Token).ConfigureAwait(false);
                        if (auth1.Command == AdbCommands.Auth && auth1.Arg0 == AdbAuthType.Signature && auth1Len > 0)
                            ReceivedSignatureAuth = true;

                        if (_requirePublicKeyFallback)
                        {
                            byte[] token2 = RandomNumberGenerator.GetBytes(20);
                            await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Auth, AdbAuthType.Token, 0, token2), token2, _cts.Token).ConfigureAwait(false);
                            var (auth2, auth2Len) = await ReadPacketAsync(stream, payloadBuf, _cts.Token).ConfigureAwait(false);
                            if (auth2.Command == AdbCommands.Auth && auth2.Arg0 == AdbAuthType.RsaPublicKey)
                            {
                                string received = Encoding.UTF8.GetString(payloadBuf, 0, auth2Len).TrimEnd('\0');
                                if (Encoding.UTF8.GetBytes(received).SequenceEqual(_expectedPubKey))
                                    ReceivedPublicKeyAuth = true;
                            }
                        }
                    }

                    byte[] deviceBanner = Encoding.UTF8.GetBytes("device::ro.product.model=Pixel_Mux;ro.serialno=EMUUSB01;features=shell_v2,cmd\0");
                    await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Cnxn, AdbMuxConnection.VersionSkipChecksum, (uint)_peerMaxPayload, deviceBanner), deviceBanner, _cts.Token).ConfigureAwait(false);

                    uint nextRemoteId = 100;
                    var syncChunks = new ConcurrentDictionary<uint, MemoryStream>();
                    var okayWaiters = new ConcurrentDictionary<uint, TaskCompletionSource<bool>>();

                    while (!_cts.IsCancellationRequested)
                    {
                        var (hdr, len) = await ReadPacketAsync(stream, payloadBuf, _cts.Token).ConfigureAwait(false);
                        if (hdr.Command == AdbCommands.Cnxn) return; // a repeated host handshake retires the online transport
                        if (hdr.Command == AdbCommands.Open)
                        {
                            string dest = Encoding.UTF8.GetString(payloadBuf, 0, len).TrimEnd('\0');
                            uint localClient = hdr.Arg0;
                            if (dest == "shell,v2,raw:legacy_v1")
                            {
                                await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Clse, 0, localClient, []), ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                                continue;
                            }

                            uint remoteId = nextRemoteId++;
                            await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Okay, remoteId, localClient, []), ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);

                            if (dest == "shell,v2,raw:echo hello_v2")
                            {
                                await SendV2Async(stream, localClient, remoteId, 1, "hello_v2\n"u8.ToArray()).ConfigureAwait(false);
                                await SendV2Async(stream, localClient, remoteId, 2, "warn\n"u8.ToArray()).ConfigureAwait(false);
                                await SendV2Async(stream, localClient, remoteId, 3, [0]).ConfigureAwait(false);
                                await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Clse, remoteId, localClient, []), ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                            }
                            else if (dest == "shell,v2,raw:echo tls_ok")
                            {
                                await SendV2Async(stream, localClient, remoteId, 1, "tls_ok\n"u8.ToArray()).ConfigureAwait(false);
                                await SendV2Async(stream, localClient, remoteId, 3, [0]).ConfigureAwait(false);
                                await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Clse, remoteId, localClient, []), ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                            }
                            else if (dest == "shell:legacy_v1")
                            {
                                byte[] outBytes = "v1_ok\n"u8.ToArray();
                                await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Wrte, remoteId, localClient, outBytes), outBytes, _cts.Token).ConfigureAwait(false);
                                await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Clse, remoteId, localClient, []), ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                            }
                            else if (dest == "sync:")
                            {
                                syncChunks[localClient] = new MemoryStream();
                            }
                            else if (dest == "localabstract:sonicroute_burst")
                            {
                                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                okayWaiters[remoteId] = waiter;
                                _ = Task.Run(async () =>
                                {
                                    byte[] chunk = new byte[512 * 1024];
                                    chunk.AsSpan().Fill(BurstSentinelByte);
                                    int sent = 0;
                                    while (sent < BurstTotalBytes && !_cts.IsCancellationRequested)
                                    {
                                        int take = Math.Min(chunk.Length, BurstTotalBytes - sent);
                                        var nextWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                        okayWaiters[remoteId] = nextWaiter;
                                        await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Wrte, remoteId, localClient, chunk.AsSpan(0, take)), chunk.AsMemory(0, take), _cts.Token).ConfigureAwait(false);
                                        sent += take;
                                        if (sent >= 4 * 1024 * 1024)
                                            BurstHighWaterReached.TrySetResult();
                                        await nextWaiter.Task.WaitAsync(_cts.Token).ConfigureAwait(false);
                                    }
                                    await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Clse, remoteId, localClient, []), ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                                });
                            }
                        }
                        else if (hdr.Command == AdbCommands.Okay)
                        {
                            if (okayWaiters.TryGetValue(hdr.Arg1, out var waiter))
                                waiter.TrySetResult(true);
                        }
                        else if (hdr.Command == AdbCommands.Wrte)
                        {
                            uint localClient = hdr.Arg0;
                            uint remoteId = hdr.Arg1;
                            await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Okay, remoteId, localClient, []), ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                            if (syncChunks.TryGetValue(localClient, out var ms))
                            {
                                ms.Write(payloadBuf, 0, len);
                                if (TryCompleteSyncStream(ms, out string? remotePath, out byte[]? body))
                                {
                                    syncChunks.TryRemove(localClient, out _);
                                    LastPushRemotePath = remotePath;
                                    LastPushedBytes = body;
                                    byte[] ok = [(byte)'O', (byte)'K', (byte)'A', (byte)'Y', 0, 0, 0, 0];
                                    await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Wrte, remoteId, localClient, ok), ok, _cts.Token).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                // echo stream
                                byte[] copy = payloadBuf[..len];
                                await WritePacketAsync(stream, AdbPacketHeader.Create(AdbCommands.Wrte, remoteId, localClient, copy), copy, _cts.Token).ConfigureAwait(false);
                            }
                        }
                        else if (hdr.Command == AdbCommands.Clse)
                        {
                            syncChunks.TryRemove(hdr.Arg0, out _);
                            if (okayWaiters.TryRemove(hdr.Arg1, out var w)) w.TrySetCanceled();
                        }
                    }
                }
            }
            catch { }
        }

        private static bool TryCompleteSyncStream(MemoryStream ms, out string? remotePath, out byte[]? fileBytes)
        {
            remotePath = null;
            fileBytes = null;
            byte[] buf = ms.GetBuffer();
            int len = (int)ms.Length;
            int pos = 0;
            if (len < 8) return false;
            using var content = new MemoryStream();
            while (pos + 8 <= len)
            {
                string tag = Encoding.ASCII.GetString(buf, pos, 4);
                int arg = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos + 4, 4));
                pos += 8;
                if (tag == "SEND")
                {
                    if (pos + arg > len) return false;
                    string spec = Encoding.UTF8.GetString(buf, pos, arg);
                    int comma = spec.LastIndexOf(',');
                    remotePath = comma >= 0 ? spec[..comma] : spec;
                    pos += arg;
                }
                else if (tag == "DATA")
                {
                    if (pos + arg > len) return false;
                    content.Write(buf, pos, arg);
                    pos += arg;
                }
                else if (tag == "DONE")
                {
                    fileBytes = content.ToArray();
                    return true;
                }
                else
                {
                    return false;
                }
            }
            return false;
        }

        private async Task SendV2Async(Stream s, uint localClient, uint remoteId, byte id, byte[] payload)
        {
            byte[] pkt = new byte[5 + payload.Length];
            pkt[0] = id;
            BinaryPrimitives.WriteInt32LittleEndian(pkt.AsSpan(1, 4), payload.Length);
            payload.CopyTo(pkt, 5);
            await WritePacketAsync(s, AdbPacketHeader.Create(AdbCommands.Wrte, remoteId, localClient, pkt), pkt, _cts.Token).ConfigureAwait(false);
        }

        private readonly SemaphoreSlim _writeGate = new(1, 1);

        private async Task<(AdbPacketHeader Header, int Len)> ReadPacketAsync(Stream stream, byte[] buf, CancellationToken token)
        {
            byte[] hdr = new byte[AdbPacketHeader.WireSize];
            await stream.ReadExactlyAsync(hdr, token).ConfigureAwait(false);
            if (!AdbPacketHeader.TryParse(hdr, out var parsed)) throw new InvalidDataException();
            int len = (int)parsed.DataLength;
            if (len > 0) await stream.ReadExactlyAsync(buf.AsMemory(0, len), token).ConfigureAwait(false);
            return (parsed, len);
        }

        private async Task WritePacketAsync(Stream stream, AdbPacketHeader header, ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            await _writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte[] hdr = new byte[AdbPacketHeader.WireSize];
                header.WriteTo(hdr);
                await stream.WriteAsync(hdr, token).ConfigureAwait(false);
                if (!payload.IsEmpty) await stream.WriteAsync(payload, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }

    private sealed class FakeSmartSocket5037 : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeSmartSocket5037()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(RunAsync);
        }

        public int Port { get; }
        public string DevicesResponse { get; set; } = "";
        public TaskCompletionSource TrackingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RejectTracking { get; init; }
        private int _trackAttempts;
        public int TrackAttempts => Volatile.Read(ref _trackAttempts);
        private readonly System.Threading.Channels.Channel<string> _trackFrames = System.Threading.Channels.Channel.CreateUnbounded<string>();
        public void SendTrackFrame(string text) => _trackFrames.Writer.TryWrite(text);

        private async Task RunAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    var stream = client.GetStream();
                    byte[] lenBuf = new byte[4];
                    await stream.ReadExactlyAsync(lenBuf, _cts.Token).ConfigureAwait(false);
                    int len = int.Parse(Encoding.ASCII.GetString(lenBuf), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte[] reqBytes = new byte[len];
                    await stream.ReadExactlyAsync(reqBytes, _cts.Token).ConfigureAwait(false);
                    string req = Encoding.UTF8.GetString(reqBytes);
                    if (req == "host:track-devices-l")
                    {
                        Interlocked.Increment(ref _trackAttempts);
                        if (RejectTracking) continue; // EOF before the initial tracking frame.
                        await stream.WriteAsync("OKAY"u8.ToArray(), _cts.Token);
                        SendTrackFrame(DevicesResponse);
                        TrackingStarted.TrySetResult();
                        await foreach (string frame in _trackFrames.Reader.ReadAllAsync(_cts.Token))
                        {
                            byte[] body = Encoding.UTF8.GetBytes(frame);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("x4", CultureInfo.InvariantCulture)), _cts.Token);
                            await stream.WriteAsync(body, _cts.Token);
                        }
                        continue;
                    }
                    string reply = req switch
                    {
                        "host:version" => "0029",
                        "host:devices-l" => DevicesResponse,
                        _ => ""
                    };
                    byte[] payload = Encoding.UTF8.GetBytes(reply);
                    byte[] resp = new byte[4 + 4 + payload.Length];
                    "OKAY"u8.CopyTo(resp);
                    Encoding.ASCII.GetBytes(payload.Length.ToString("x4", CultureInfo.InvariantCulture), resp.AsSpan(4, 4));
                    payload.CopyTo(resp, 8);
                    await stream.WriteAsync(resp, _cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}
