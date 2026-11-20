using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static Task NoConnectionDelay(TimeSpan _, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    private static async Task CheckConnectionRegressionsAsync()
    {
        const string header = "List of devices attached\n";
        const string transport = "adb-phone-abc123._adb-tls-connect._tcp";
        var qr = AndroidQrPairing.Create();
        int reads = 0; bool paired = false;
        var commands = new List<string[]>();
        var wireless = new AndroidWirelessConnection((token, args) =>
        {
            token.ThrowIfCancellationRequested(); commands.Add(args);
            return Task.FromResult(args[0] switch
            {
                "mdns" when !paired => $"{qr.ServiceName.ToUpperInvariant()}._adb-tls-pairing._tcp. _adb-tls-pairing._tcp. 192.168.1.10:37000\n",
                "pair" => Pair(),
                "devices" when ++reads <= 2 => throw new IOException("temporary device scan failure"),
                "devices" => reads < 8 ? header + transport + " offline\n" : header + transport + " device model:Phone\n",
                "mdns" => throw new IOException("mDNS temporarily unavailable"),
                _ => throw new IOException("not ready")
            });
            string Pair() { paired = true; return "Successfully paired to 192.168.1.10:37000 [guid=adb-phone]"; }
        }, NoConnectionDelay);
        var pairing = await wireless.PairQrAsync(qr, default);
        var result = await wireless.FinishPairingAsync(pairing, default);
        Check(result.Device.Serial == transport && reads == 8 && pairing.Guid == "adb-phone",
            "QR waits through transient scan errors and delayed/offline mDNS transports after pairing");
        Check(!commands.Any(c => c[0] == "connect" && c[1].EndsWith(":37000")), "pairing never reuses the pairing port as the connection port");
        Check(result.WirelessAddress == null, "a paired GUID alone cannot identify a numeric connection endpoint");

        reads = 0; bool connected = false;
        var attempts = new List<string>();
        wireless = new AndroidWirelessConnection((_, args) => Task.FromResult(args[0] switch
        {
            "pair" => "Successfully paired to 192.168.1.10:37000",
            "devices" => header + (connected ? transport + " device model:Phone\n" : "192.168.1.11:39000 device model:Other\n"),
            "mdns" => "other _adb-tls-connect._tcp 192.168.1.11:39000\nadb-phone-abc123 _adb-tls-connect._tcp 192.168.1.10:38000\n",
            "connect" => Connect(),
            _ => throw new IOException("not ready")
        }), NoConnectionDelay);
        string Connect() { attempts.Add("192.168.1.10:38000"); connected = ++reads >= 2; return connected ? "connected to 192.168.1.10:38000" : throw new IOException("temporary endpoint failure"); }
        result = await wireless.FinishPairingAsync(await wireless.PairCodeAsync("192.168.1.10:37000", "123456", default), default);
        Check(result.Device.Serial == transport && attempts.Count == 2, "code pairing retries a transient endpoint failure and recognizes the service-name transport");
        Check(result.WirelessAddress == "192.168.1.10:38000", "the exact paired connect-service alias carries its current numeric endpoint");
        Check(attempts.All(a => a == "192.168.1.10:38000"), "pair completion connects only services on the paired host");

        reads = 0;
        wireless = new AndroidWirelessConnection((_, args) => Task.FromResult(args[0] switch
        {
            "connect" => "connected to 192.168.1.10:38000",
            "devices" => ++reads < 3 ? header + "192.168.1.10:38000 unauthorized\n" : header + "192.168.1.10:38000 device model:Phone\n",
            "mdns" => "",
            _ => throw new IOException("not authorized yet")
        }), NoConnectionDelay);
        result = await wireless.ConnectAsync("192.168.1.10:38000", default);
        Check(reads == 3 && result.Device.State == "device", "manual address connection waits for authorization instead of accepting the first empty/offline snapshot");

        wireless = new AndroidWirelessConnection((_, args) => Task.FromResult(args[0] switch
        {
            "connect" => "already connected to 192.168.1.10:38000",
            "devices" => header + transport + " device model:Phone\n",
            "mdns" => "",
            "-s" => transport,
            _ => ""
        }), NoConnectionDelay);
        result = await wireless.ConnectAsync("192.168.1.10:38000", default);
        Check(result.Device.Serial == transport, "direct connection resolves an existing service-name alias without relying on mDNS discovery");
        Check(result.WirelessAddress == "192.168.1.10:38000", "a manually connected address is retained when ADB identifies its canonical service name");
        result = await wireless.ReconnectAsync(new("adb-phone-old123._adb-tls-connect._tcp", "Phone", "offline", true), default);
        Check(result.Device.Serial == transport, "reconnect accepts the same phone after its random service suffix changes");

        wireless = new AndroidWirelessConnection((_, args) => Task.FromResult(args[0] switch
        {
            "devices" => header + transport + " device model:Phone\n",
            "mdns" => "adb-phone-abc123 _adb-tls-connect._tcp 192.168.1.10:38001\n"
                + "adb-phone-abc123 _adb-tls-connect._tcp 192.168.1.11:38002\n",
            _ => throw new IOException("unavailable")
        }), NoConnectionDelay);
        result = await wireless.FinishPairingAsync(new("192.168.1.10:37000", "adb-phone"), default);
        Check(result.Device.Serial == transport && result.WirelessAddress == null,
            "two different endpoints advertising one service cannot replace a remembered address");

        connected = false;
        wireless = new AndroidWirelessConnection((_, args) => Task.FromResult(args[0] switch
        {
            "connect" when args[1].EndsWith(":37001") => throw new IOException("old port"),
            "connect" => Reconnected(),
            "mdns" => "phone-new123 _adb-tls-connect._tcp 192.168.1.10:38000\n",
            "devices" => connected ? header + "192.168.1.10:38000 device model:Phone\n" : header,
            _ => throw new IOException("not ready")
        }), NoConnectionDelay);
        string Reconnected() { connected = true; return "connected to 192.168.1.10:38000"; }
        result = await wireless.ReconnectAsync(new("192.168.1.10:37001", "Phone", "offline", true), default);
        Check(result.Device.Serial == "192.168.1.10:38000", "reconnect discovers the same host's new connection port when the saved port expires");

        reads = 0;
        var failedClock = new PairingTestClock();
        wireless = new AndroidWirelessConnection((_, args) => Task.FromResult(args[0] switch
        {
            "devices" => ++reads > 0 ? header + "192.168.1.11:39000 device model:Other\n" : header,
            "mdns" => "other _adb-tls-connect._tcp 192.168.1.11:39000\n",
            _ => throw new IOException("not ready")
        }), (delay, token) => { failedClock.Advance(delay); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, timeProvider: failedClock);
        bool failed = false;
        try { await wireless.FinishPairingAsync(new("192.168.1.10:37000", "adb-phone"), default); }
        catch (IOException) { failed = true; }
        Check(failed && reads > 15 && failedClock.GetUtcNow() >= DateTimeOffset.UnixEpoch.AddSeconds(20)
            && failedClock.GetUtcNow() < DateTimeOffset.UnixEpoch.AddSeconds(21),
            "a different connected phone cannot satisfy pairing and retries stop at the overall deadline");

        using var canceled = new CancellationTokenSource();
        wireless = new AndroidWirelessConnection((token, args) => { token.ThrowIfCancellationRequested(); return Task.FromResult(header); },
            (_, token) => { canceled.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        failed = false;
        try { await wireless.FinishPairingAsync(new("192.168.1.10:37000", null), canceled.Token); }
        catch (OperationCanceledException) { failed = true; }
        Check(failed, "leaving the connection page cancels readiness retries immediately");
    }
    private static async Task CheckDeviceDiscoveryAsync()
    {
        var wifi = new AndroidDevice("adb-phone-abc123._adb-tls-connect._tcp", "Phone", "device", true);
        IReadOnlyList<AndroidDevice> connected = new[] { wifi };
        var response = new TaskCompletionSource<IReadOnlyList<AndroidDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0, changes = 0; CancellationToken providerToken = default;
        var discovery = new AndroidDeviceDiscovery(token => { ++reads; providerToken = token; return response.Task; });
        discovery.Changed += () => ++changes;
        using var firstToken = new CancellationTokenSource();
        var first = discovery.ReadAsync(firstToken.Token);
        var second = discovery.ReadAsync(default);
        firstToken.Cancel();
        try { await first; } catch (OperationCanceledException) { }
        Check(reads == 1 && !providerToken.IsCancellationRequested, "canceling the first UI caller does not cancel another window's shared scan");
        response.SetResult(connected);
        Check(ReferenceEquals(await second, connected) && changes == 1, "remaining scan caller receives the connected snapshot and one change notification");

        response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery = new AndroidDeviceDiscovery(_ => response.Task); changes = 0;
        discovery.Changed += () => ++changes;
        var old = discovery.ReadAsync(default);
        discovery.Publish(connected);
        response.SetResult(Array.Empty<AndroidDevice>());
        Check(ReferenceEquals(await old, connected) && ReferenceEquals(discovery.Devices, connected) && changes == 1,
            "an empty scan started before pairing cannot overwrite the newly published connected phone");
        discovery.Publish(connected);
        Check(changes == 1, "republishing the same device snapshot does not wake subscribers again");
        discovery.Invalidate();
        Check(changes == 2 && discovery.CurrentDevices == null && ReferenceEquals(discovery.Devices, connected),
            "invalidated discovery retains presentation data but rejects it for new stream starts");
        discovery.Publish(connected);
        Check(changes == 3 && ReferenceEquals(discovery.CurrentDevices, connected),
            "publishing the same list after invalidation makes it current again and notifies consumers");

        long beforeHandshake = discovery.Revision;
        var replacement = connected[0] with { HardwareId = "replacement", ConnectionTarget = "new-instance" };
        discovery.Publish([replacement]);
        bool admitted = false;
        Check(!discovery.PublishConnection(new(connected[0], connected), beforeHandshake, _ => admitted = true)
            && !admitted && discovery.CurrentDevices!.Single() == replacement,
            "a late recovery cannot overwrite a new identity at a reused address");
        discovery.Publish([connected[0], replacement with { Serial = "another-device" }]);
        Check(discovery.PublishConnection(new(connected[0], connected), beforeHandshake, r => r.Devices.Count == 2)
            && discovery.CurrentDevices!.Count == 2, "an unchanged transport can be admitted without dropping a newer phone");

        var usb = new AndroidDevice("USB-A", "Phone", "device", false, TransportId: "usb-1");
        var emulator = new AndroidDevice("emulator-5554", "Emulator", "device", false, TransportId: "2");
        discovery.Publish([usb, wifi, emulator]);
        var beforeReplug = discovery.CurrentDevices;
        discovery.Publish([usb with { TransportId = "usb-2" }, wifi, emulator]);
        discovery.RemoveAbsentUsbDevices(beforeReplug, new HashSet<string>(), []);
        Check(discovery.CurrentDevices?.First().TransportId == "usb-2", "a late empty USB probe cannot retire a newer attachment");
        discovery.RemoveAbsentUsbDevices(discovery.CurrentDevices, new HashSet<string>(), []);
        Check(discovery.CurrentDevices!.SequenceEqual([wifi, emulator]), "confirmed USB absence preserves Wi-Fi and local emulators");
        discovery.Invalidate();
        discovery.RemoveAbsentUsbDevices(discovery.CurrentDevices, new HashSet<string>(), []);
        Check(discovery.CurrentDevices == null, "a USB presence fallback cannot make an invalidated snapshot current");

        var originalUsb = usb with { UsbInterfacePath = "port", TransportId = "usb-5" };
        var newUsb = originalUsb with { TransportId = "usb-6" };
        var unmapped = usb with { TransportId = "3" }; // Some shared ADB servers expose only a serial.
        discovery.Publish([newUsb, unmapped, wifi]);
        discovery.RemoveAbsentUsbDevices(discovery.CurrentDevices, null, [originalUsb]);
        Check(discovery.CurrentDevices!.SequenceEqual([newUsb, unmapped, wifi]),
            "a delayed path removal cannot retire a replacement attachment or a shared ADB transport with an unverified path");
        var beforeOtherPhoneLost = discovery.CurrentDevices;
        discovery.Publish([newUsb, unmapped]);
        discovery.RemoveAbsentUsbDevices(beforeOtherPhoneLost, null, [newUsb]);
        Check(discovery.CurrentDevices!.SequenceEqual([unmapped]),
            "a concurrent change on another phone does not hide a verified USB removal");

        var oldResponse = new TaskCompletionSource<IReadOnlyList<AndroidDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResponse = new TaskCompletionSource<IReadOnlyList<AndroidDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        reads = 0;
        discovery = new AndroidDeviceDiscovery(_ => ++reads == 1 ? oldResponse.Task : newResponse.Task);
        old = discovery.ReadAsync(default); discovery.Invalidate();
        var current = discovery.ReadAsync(default);
        newResponse.SetResult(connected); await current;
        oldResponse.SetResult(Array.Empty<AndroidDevice>()); await old;
        Check(reads == 2 && ReferenceEquals(discovery.Devices, connected), "a refresh after a connection mutation starts a new scan and supersedes the old one");

        response = new(TaskCreationOptions.RunContinuationsAsynchronously); changes = 0;
        discovery = new AndroidDeviceDiscovery(token => { providerToken = token; return response.Task; });
        discovery.Changed += () => ++changes;
        using var lastToken = new CancellationTokenSource();
        var abandoned = discovery.ReadAsync(lastToken.Token); lastToken.Cancel();
        try { await abandoned; } catch (OperationCanceledException) { }
        Check(providerToken.IsCancellationRequested, "the underlying scan is canceled when its last visible caller leaves");
        response.SetResult(connected);
        await Task.Yield();
        Check(discovery.Devices == null && changes == 0, "an abandoned scan cannot publish a late connection result");
    }
}
