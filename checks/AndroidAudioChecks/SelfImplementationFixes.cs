using System.Reflection;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SelfImplementationFixesAsync(string outputDirectory)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        CheckClosedStreamReceiveRace();
        await CheckFailedUsbRefreshAsync(outputDirectory);
        await CheckCancelledNativeWindowAsync();
        await CheckMaximizedLayoutAsync(Path.Combine(outputDirectory, "layout"));
        await CheckCaptureRestoreAsync(Path.Combine(outputDirectory, "capture-restore"));
        await CheckTlsPostHandshakeLoopbackAsync();
        await CheckSspiPairingLoopbackAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await CheckAdbMuxWireProtocolAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "regression-result.json"),
            JsonSerializer.Serialize(new { Success = true, Assertions = _checks, RealDeviceAccess = false,
                Tls = "Windows SChannel TLS 1.3 pairing and wrong-PIN loopback", NativeWindowsHiddenAndClosed = true },
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} self-implementation fix assertions; no real phone/USB/shared ADB access.");
    }

    private static void CheckClosedStreamReceiveRace()
    {
        var constructor = typeof(AdbMuxConnection).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var connection = (AdbMuxConnection)constructor.Invoke([null, AdbMuxConnection.VersionSkipChecksum, 1048576, "device::", false]);
        using var stream = new AdbMuxStream(connection, 1);
        using var otherStream = new AdbMuxStream(connection, 2);
        var flowGate = typeof(AdbMuxStream).GetField("_flowLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(stream)!;
        Exception? receiveFailure = null;
        using var receiveStarted = new ManualResetEventSlim();
        var receiver = new Thread(() =>
        {
            receiveStarted.Set();
            try { stream.OnWrte(0, new byte[] { 1, 2, 3 }); }
            catch (Exception error) { receiveFailure = error; }
        }) { IsBackground = true };
        Monitor.Enter(flowGate);
        try
        {
            receiver.Start();
            if (!receiveStarted.Wait(TimeSpan.FromSeconds(2))
                || !SpinWait.SpinUntil(() => (receiver.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 2000))
                throw new Exception("Receive thread did not reach its stream flow lock.");
            stream.Dispose();
        }
        finally { Monitor.Exit(flowGate); }
        Check(receiver.Join(TimeSpan.FromSeconds(2)) && receiveFailure == null,
            "closing one stream during WRTE dispatch does not write a completed pipe or throw");
        otherStream.OnWrte(0, new byte[] { 4, 5, 6 });
        byte[] reply = new byte[3];
        Check(otherStream.Read(reply) == 3 && reply.SequenceEqual(new byte[] { 4, 5, 6 }) && connection.IsConnected,
            "other stream remains usable after the close/receive race");
        stream.OnRemoteClose(0);
        stream.OnTransportFault(new IOException("late fault"));
        stream.OnWrte(0, new byte[] { 7 });
        Check(connection.IsConnected, "late close/fault/data for a closed stream are harmless");
    }

    private static async Task CheckFailedUsbRefreshAsync(string outputDirectory)
    {
        using var keyStore = new AdbKeyStore(Path.Combine(outputDirectory, "unused-usb-test-key"));
        int attempts = 0;
        await using var direct = new DirectAdbTransport(keyStore,
            enumerateUsb: () => [new WinUsbDeviceDescriptor("review-device-path", @"USB\VID_TEST\REVIEW_SERIAL")],
            openUsbLink: _ => new FixTestUsbLink(Interlocked.Increment(ref attempts) == 1),
            watchUsb: _ => null);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task watch = direct.TrackDevicesAsync(devices =>
        {
            if (devices.Any(d => d.State == "offline")) offline.TrySetResult();
            if (devices.Any(d => d.State == "device")) recovered.TrySetResult();
        }, stop.Token);
        try
        {
            await offline.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(200, stop.Token); // Observe only this fake device's event queue.
            Check(Volatile.Read(ref attempts) == 1,
                "failed USB device stays visible without an event-driven retry loop");
            var refreshed = await direct.DevicesAsync(stop.Token);
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(attempts == 2 && refreshed.Any(d => d.State == "device"),
                "explicit refresh retries a failed handshake while USB stays attached");
            Check(!Directory.Exists(Path.Combine(outputDirectory, "unused-usb-test-key")),
                "USB handshake regression does not access or create a private key");
        }
        finally
        {
            stop.Cancel();
            try { await watch; } catch (OperationCanceledException) { }
        }
    }

    private static async Task CheckCancelledNativeWindowAsync()
    {
        await using (var preCancelled = new NativeVideoWindow("Hidden cancelled review", true, true, null, 0, 0, 240, 320))
        {
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            bool cancelled = false;
            try { await preCancelled.CreateAsync(stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && preCancelled.WindowHandle == 0 && preCancelled.Closed.IsCompleted,
                "pre-cancelled window creation starts no native window or message loop");
        }

        await using var window = new NativeVideoWindow("Hidden creation race review", true, true, null, 0, 0, 240, 320);
        var registrationGate = typeof(NativeVideoWindow).GetField("ClassRegistrationLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var thread = (Thread)typeof(NativeVideoWindow).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        using var cancellation = new CancellationTokenSource();
        Task dispose;
        Monitor.Enter(registrationGate);
        try
        {
            Task<nint> create = window.CreateAsync(cancellation.Token);
            if (!SpinWait.SpinUntil(() => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 2000))
                throw new Exception("Window thread did not reach class registration.");
            cancellation.Cancel();
            bool cancelled = false;
            try { create.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancelling an in-flight window completes its create task");
            dispose = window.DisposeAsync().AsTask();
        }
        finally { Monitor.Exit(registrationGate); }
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Check(window.WindowHandle == 0 && window.Closed.IsCompleted && thread.Join(TimeSpan.FromSeconds(2)),
            "late window creation exits after cancellation without leaking HWND or thread");

        await using var healthy = new NativeVideoWindow("Hidden ordinary review", true, true, null, 0, 0, 240, 320);
        Check(await healthy.CreateAsync(CancellationToken.None) != 0, "ordinary hidden window still creates");
        await healthy.DisposeAsync();
        await healthy.DisposeAsync();
        Check(healthy.WindowHandle == 0 && healthy.Closed.IsCompleted, "ordinary window closes idempotently");
    }

    private static async Task CheckTlsPostHandshakeLoopbackAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        byte[] pfx = generated.Export(X509ContentType.Pkcs12);
        using var cert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task server = Task.CompletedTask;
        try
        {
            byte[] expected = "SonicRoute TLS ticket continuation"u8.ToArray();
            server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(stop.Token);
                using var tls = new SslStream(peer.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = true
                }, stop.Token);
                await Task.Delay(100, stop.Token);
                await tls.WriteAsync(expected, stop.Token);
                byte[] acknowledgement = new byte[1];
                await tls.ReadExactlyAsync(acknowledgement, stop.Token);
            });
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
            Type tlsType = typeof(SspiPairingTls).GetNestedType("SspiTlsStream", BindingFlags.NonPublic)!;
            var factory = tlsType.GetMethod("AuthenticateAsClientAsync", BindingFlags.Static | BindingFlags.Public)!;
            var authentication = (Task)factory.Invoke(null, [tcp.GetStream(), null, "localhost", stop.Token])!;
            try { await authentication; }
            catch (Exception clientError)
            {
                stop.Cancel();
                try { await server; }
                catch (Exception serverError) { throw new AggregateException(clientError, serverError); }
                throw;
            }
            using var client = (Stream)authentication.GetType().GetProperty("Result")!.GetValue(authentication)!;
            var contextField = tlsType.GetField("_ctxt", BindingFlags.NonPublic | BindingFlags.Instance)!;
            object originalContext = contextField.GetValue(client)!;
            byte[] actual = new byte[expected.Length];
            await client.ReadExactlyAsync(actual, stop.Token);
            await client.WriteAsync(new byte[] { 1 }, stop.Token);
            await server;
            int messages = (int)tlsType.GetField("_postHandshakeMessages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
            Check(actual.SequenceEqual(expected) && originalContext.Equals(contextField.GetValue(client)),
                "TLS 1.3 resumes application data without replacing its established SChannel context");
            Check(messages > 0, "TLS 1.3 post-handshake ticket is processed by the real SChannel continuation path");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await server; } catch { }
        }
    }

    private sealed class FixTestUsbLink(bool failHandshake) : IAdbPacketLink
    {
        private bool _headerSent;
        public bool SupportsTlsUpgrade => false;
        public string? PeerCertificateSha256 => null;
        public async ValueTask<(AdbPacketHeader Header, int PayloadLength)> ReadPacketAsync(byte[] payloadBuffer, CancellationToken token)
        {
            if (failHandshake) throw new IOException("Simulated transient USB handshake failure.");
            if (_headerSent) { await Task.Delay(Timeout.Infinite, token); throw new OperationCanceledException(token); }
            _headerSent = true;
            byte[] banner = Encoding.UTF8.GetBytes("device::ro.product.model=Recovered;features=shell_v2\0");
            banner.CopyTo(payloadBuffer, 0);
            return (AdbPacketHeader.Create(AdbCommands.Cnxn, AdbMuxConnection.VersionSkipChecksum, 1048576, banner), banner.Length);
        }
        public ValueTask WritePacketAsync(AdbPacketHeader header, ReadOnlyMemory<byte> payload, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public Task UpgradeToTlsAsync(X509Certificate2 clientCertificate, Func<X509Certificate2?, bool>? serverCertificateValidator,
            string? targetHost, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
