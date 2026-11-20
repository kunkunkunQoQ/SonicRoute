using System.Security.Cryptography;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task PairingFixesAsync(string output)
    {
        Directory.CreateDirectory(output);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var clock = new PairingTestClock();
        var wireless = new AndroidWirelessConnection(async (token, _) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { throw new ObjectDisposedException("cancelled pairing socket"); }
            return "";
        }, timeProvider: clock);
        Task pairing = wireless.PairCodeAsync("192.0.2.10:37000", "123456", default);
        clock.Advance(TimeSpan.FromSeconds(29));
        Check(!pairing.IsCompleted, "manual pairing stays available before its 30-second deadline");
        clock.Advance(TimeSpan.FromSeconds(1));
        bool timedOut = false;
        try { await pairing.WaitAsync(safety.Token); }
        catch (TimeoutException ex) { timedOut = AndroidError.TryGetCode(ex, out var code) && code == AndroidErrorCode.PairTimedOut; }
        Check(timedOut, "manual pairing deadline normalizes an aborted socket into a localized timeout");
        using (var cancel = new CancellationTokenSource())
        {
            pairing = wireless.PairCodeAsync("192.0.2.10:37000", "123456", cancel.Token);
            cancel.Cancel(); bool cancelled = false;
            try { await pairing.WaitAsync(safety.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "manual caller cancellation remains cancellation rather than timeout");
        }

        clock = new PairingTestClock();
        var discovered = new TaskCompletionSource<AndroidMdnsService?>();
        bool connected = false;
        int connects = 0;
        wireless = new AndroidWirelessConnection((token, args) => Task.FromResult(args[0] switch
        {
            "devices" => connected ? "List of devices attached\n192.0.2.10:38000 device model:Expected\n" : "List of devices attached\n",
            "mdns" => "",
            "connect" => Connected(),
            _ => ""
        }), async (delay, token) =>
        {
            clock.Advance(delay);
            if (clock.GetUtcNow() >= DateTimeOffset.UnixEpoch.AddSeconds(9))
                discovered.TrySetResult(new("adb-expected-random", "_adb-tls-connect._tcp", "192.0.2.10:38000"));
            await Task.Yield(); token.ThrowIfCancellationRequested();
        }, (_, _, _, _, _, token) => discovered.Task.WaitAsync(token), clock);
        string Connected() { connected = true; connects++; return "connected to 192.0.2.10:38000"; }
        var result = await wireless.FinishPairingAsync(new("192.0.2.10:37000", "expected"), default).WaitAsync(safety.Token);
        Check(result.Device.Serial == "192.0.2.10:38000" && connects == 1
            && clock.GetUtcNow() >= DateTimeOffset.UnixEpoch.AddSeconds(9),
            "a service appearing after the former 8.1-second cutoff still completes within the overall deadline");

        clock = new PairingTestClock();
        connected = false; var endpoints = new List<string>();
        wireless = new AndroidWirelessConnection((token, args) =>
        {
            if (args[0] == "connect") { endpoints.Add(args[1]); connected = true; }
            return Task.FromResult(args[0] switch
            {
                "devices" => "List of devices attached\n192.0.2.10:39999 device model:WrongOldPhone\n"
                    + (connected ? "192.0.2.10:38000 device model:Expected\n" : ""),
                "mdns" => "adb-other-random _adb-tls-connect._tcp 192.0.2.10:39999\nadb-expected-random _adb-tls-connect._tcp 192.0.2.10:38000\n",
                "connect" => "connected to " + args[1],
                _ => ""
            });
        }, (delay, token) => { clock.Advance(delay); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, timeProvider: clock);
        result = await wireless.FinishPairingAsync(new("192.0.2.10:37000", "expected"), default).WaitAsync(safety.Token);
        Check(result.Device.Serial == "192.0.2.10:38000" && endpoints.SequenceEqual(["192.0.2.10:38000"]),
            "a known GUID rejects both the old same-IP device and its mismatched mDNS endpoint");

        var runtime = Path.Combine(output, "runtime"); Directory.CreateDirectory(runtime);
        await File.WriteAllBytesAsync(Path.Combine(runtime, "sonicroute-catalog.jar"), [1]);
        using var liveExpected = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        liveExpected.Start();
        string expectedAddress = liveExpected.LocalEndpoint.ToString()!;
        var transport = new PairingFixtureTransport
        {
            Devices = [new("192.0.2.10:38000", "Wrong", "device", true), new(expectedAddress, "Expected", "device", true)]
        };
        transport.HardwareIds["192.0.2.10:38000"] = "hardware-wrong";
        transport.HardwareIds[expectedAddress] = "hardware-expected";
        wireless = new AndroidWirelessConnection(new AdbClient(runtime, transport));
        result = await wireless.ReconnectAsync(new("192.0.2.10:38000", "Saved", "offline", true, HardwareId: "hardware-expected"), default);
        Check(result.Device.Serial == expectedAddress,
            "reconnect gives saved hardware identity precedence over a reused old address");
        transport.FirstIdentityReadFailures.Add(expectedAddress);
        transport.IdentityReads.Clear();
        wireless = new AndroidWirelessConnection(new AdbClient(runtime, transport));
        result = await wireless.ReconnectAsync(new("192.0.2.10:38000", "Saved", "offline", true, HardwareId: "hardware-expected"), default);
        Check(result.Device.Serial == expectedAddress && transport.IdentityReads[expectedAddress] == 2,
            "strong reconnect retries a cached missing identity once instead of accepting a different phone or polling");

        await CheckAliasProofRecoveryAsync(runtime, expectedAddress, selectUsb: false);
        await CheckAliasProofRecoveryAsync(runtime, expectedAddress, selectUsb: true);
        await CheckAliasProofRecoveryAsync(runtime, expectedAddress, selectUsb: false, conflictingProof: true);

        var endpointOnly = new PairingFixtureTransport { Devices = [new(expectedAddress, "Expected", "device", true, TransportId: "current")] };
        endpointOnly.HardwareIds[expectedAddress] = "hardware-expected";
        endpointOnly.FirstIdentityReadFailures.Add(expectedAddress);
        var endpointAdb = new AdbClient(runtime, endpointOnly);
        var initial = await endpointAdb.DevicesAsync(default);
        var endpointSaved = initial.Single();
        wireless = new AndroidWirelessConnection(endpointAdb);
        result = await wireless.ReconnectAsync(endpointSaved, null, default, initial, retryMissingIdentity: false);
        Check(result.Device.HardwareId == null && endpointOnly.IdentityReads[expectedAddress] == 1,
            "manual refresh can preserve an endpoint-only key while a live stream owns it");
        result = await wireless.ReconnectAsync(endpointSaved, null, default, initial);
        Check(result.Device.HardwareId == "hardware-expected" && result.Devices.Single() == result.Device
            && endpointOnly.IdentityReads[expectedAddress] == 2,
            "an explicit reconnect upgrades the missing identity in both the returned device and published list");
        var prefs = new AndroidAudioSettingsStore(Path.Combine(output, "endpoint-promotion.json"));
        const string package = "cn.test.identity";
        string oldOutput = AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(endpointSaved), AndroidOutputSink.DefaultSpeaker.Key);
        prefs.Remember(endpointSaved, package, 37, oldOutput);
        prefs.RememberWindow(endpointSaved, package, new(900, 600));
        prefs.RememberStreaming(endpointSaved, new(AudioBitRate: 192000));
        prefs.PromoteEndpointPreferences(endpointSaved, result.Device with { TransportId = "different" });
        Check(prefs.Get(result.Device, package).Volume == 100, "identity promotion refuses an unrelated transport generation");
        prefs.PromoteEndpointPreferences(endpointSaved, result.Device);
        Check(prefs.Get(result.Device, package).Volume == 37 && prefs.Get(result.Device, package).WindowSize == new AndroidWindowSize(900, 600)
            && prefs.Get(result.Device, package).OutputId == AndroidOutputIdentity.FormatDeviceId(AndroidDeviceIdentity.Key(result.Device), AndroidOutputSink.DefaultSpeaker.Key)
            && prefs.GetStreaming(result.Device).AudioBitRate == 192000,
            "the verified same-transport identity retains volume, display layout, and stream preferences");
        prefs.Remember(result.Device, package, 19, null);
        prefs.PromoteEndpointPreferences(endpointSaved, result.Device);
        Check(prefs.Get(result.Device, package).Volume == 19, "identity promotion never overwrites an existing verified preference");
        await prefs.FlushAsync();

        var handshake = new PairingFixtureTransport { Devices = [new(expectedAddress, "Expected", "device", true, TransportId: "banner")] };
        handshake.HandshakeSerials[expectedAddress] = "hardware-banner";
        Check((await new AdbClient(runtime, handshake).DevicesAsync(default)).Single().HardwareId == "hardware-banner",
            "authorized transport metadata provides the missing hardware serial without another connection");
        handshake.HandshakeSerials[expectedAddress] = expectedAddress;
        Check((await new AdbClient(runtime, handshake).DevicesAsync(default)).Single().HardwareId == null,
            "the fallback cannot misclassify the wireless endpoint as a hardware identity");

        string keyDirectory = Path.Combine(output, "synthetic-keys"); Directory.CreateDirectory(keyDirectory);
        string privatePath = Path.Combine(keyDirectory, "adbkey"), publicPath = Path.Combine(keyDirectory, "adbkey.pub");
        using var privateKey = RSA.Create(2048);
        using var staleKey = RSA.Create(2048);
        await File.WriteAllTextAsync(privatePath, privateKey.ExportPkcs8PrivateKeyPem());
        string staleLine = Convert.ToBase64String(AdbKeyStore.EncodeAospPublicKey(staleKey)) + " synthetic-stale\n";
        await File.WriteAllTextAsync(publicPath, staleLine);
        try
        {
            using var keys = new AdbKeyStore(keyDirectory);
            Check(keys.LoadedFingerprint == null, "unpair identity inspection does not load or generate a key");
            string actualFingerprint = keys.GetFingerprint();
            await using var direct = new DirectAdbTransport(keys);
            var (_, fingerprint) = await new AdbClient(runtime, direct).BindPairingIdentityAsync(default);
            Check(fingerprint == actualFingerprint && fingerprint != AndroidWirelessConnection.PairingFingerprint(staleLine),
                "unpair binds the key already used by native authentication rather than stale adbkey.pub");
            Check(await File.ReadAllTextAsync(publicPath) == staleLine,
                "identity binding preserves existing key files instead of overwriting another tool's public key");
            var known = new KnownPairingFixtureTransport(actualFingerprint);
            wireless = new AndroidWirelessConnection(new AdbClient(runtime, known));
            int cleanups = 0;
            await wireless.UnpairAsync(new("phone-fixture", "Phone", "device", true), default, () => { cleanups++; return Task.CompletedTask; });
            Check(cleanups == 1 && known.Pushed == 1 && known.Commands.Any(c => c.Contains("PairingControl " + actualFingerprint + " ")),
                "verified identity is carried through cleanup and the bound unpair helper request");
        }
        finally { File.Delete(privatePath); File.Delete(publicPath); }
        var unknown = new PairingFixtureTransport();
        wireless = new AndroidWirelessConnection(new AdbClient(runtime, unknown));
        bool rejected = false, cleanupCalled = false;
        try { await wireless.UnpairAsync(new("phone-fixture", "Phone", "device", true), default,
            () => { cleanupCalled = true; return Task.CompletedTask; }); }
        catch (IOException ex) { rejected = AndroidError.TryGetCode(ex, out var code) && code == AndroidErrorCode.UnpairIdentityUnavailable; }
        Check(rejected && !cleanupCalled && unknown.Pushed == 0 && unknown.Commands.Count == 0,
            "an unverified external identity is rejected before stopping audio, pushing helpers or changing pairing");

        await CheckConnectionRegressionsAsync().WaitAsync(safety.Token);
        await CheckDeviceDiscoveryAsync().WaitAsync(safety.Token);
        await CheckSspiPairingLoopbackAsync().WaitAsync(safety.Token);
        await File.WriteAllTextAsync(Path.Combine(output, "pairing-fixes-result.json"), JsonSerializer.Serialize(new
        {
            Success = true, Assertions = _checks, RealPhoneAccess = false, RealUserKeysAccessed = false,
            SharedAdbAccess = false, VirtualDeadlineClock = true, TlsLoopback = true, SyntheticKeyFilesRemoved = true
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {_checks} pairing fix assertions; no real phone, user keys or shared ADB access.");
    }

    private sealed class PairingTestClock : TimeProvider
    {
        private long _ticks;
        private readonly List<ClockTimer> _timers = new();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ClockTimer(this, callback, state); timer.Change(dueTime, period); return timer; }
        internal void Advance(TimeSpan amount)
        {
            ClockTimer[] ready;
            lock (_timers) { _ticks += amount.Ticks; ready = _timers.Where(t => t.Due <= _ticks).ToArray(); }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class ClockTimer(PairingTestClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            private long _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers)
                {
                    if (_disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                    _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                    if (!clock._timers.Contains(this)) clock._timers.Add(this);
                    return true;
                }
            }
            internal void Fire()
            {
                lock (clock._timers)
                {
                    if (_disposed || Due > clock._ticks) return;
                    Due = _period == 0 ? long.MaxValue : clock._ticks + _period;
                }
                callback(state);
            }
            public void Dispose() { lock (clock._timers) { _disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static async Task CheckAliasProofRecoveryAsync(string runtime, string address, bool selectUsb, bool conflictingProof = false)
    {
        const string usb = "serial-alias", unrelated = "other-phone", androidId = "0123456789abcdef";
        var transport = new PairingFixtureTransport
        {
            Devices = [new(usb, "Phone", "device", false, TransportId: "usb-1"),
                new(address, "Phone", "device", true, TransportId: "wifi-2"),
                new(unrelated, "Phone", "device", false, TransportId: "usb-3")]
        };
        transport.HardwareIds[usb] = transport.HardwareIds[address] = usb;
        transport.HardwareIds[unrelated] = unrelated;
        string selected = selectUsb ? usb : address, missing = selectUsb ? address : usb;
        transport.AndroidIds[selected] = androidId;
        var adb = new AdbClient(runtime, transport);
        var wireless = new AndroidWirelessConnection(adb);
        var devices = await adb.DevicesAsync(default, deduplicate: false);
        var ready = devices.Single(d => d.Serial == selected);
        transport.AndroidIds[missing] = conflictingProof ? "abcdef0123456789" : androidId;
        devices = await adb.DevicesAsync(default, deduplicate: false);
        Check(AndroidConnectionManager.IsAmbiguous(ready, devices) && transport.FingerprintReads[missing] == 1,
            "passive scans preserve the cached incomplete alias and refuse ambiguous identity");

        var result = selectUsb ? await wireless.EnrichReadyResultAsync(new(ready, devices), false, default)
            : await wireless.ReconnectAsync(ready, null, default, devices, retryMissingIdentity: false);
        var alias = result.Devices.Single(d => d.Serial == missing);
        Check(result.Device == ready && alias.IdentityFingerprint != null && transport.FingerprintReads[missing] == 2
            && transport.IdentityReads[missing] == 1 && transport.FingerprintReads[selected] == 1 && transport.FingerprintReads[unrelated] == 1,
            "with active playback reconnect only retries the missing fingerprint of the selected phone's alias");
        Check(AndroidConnectionManager.IsAmbiguous(ready, result.Devices) == conflictingProof
            && (alias.IdentityFingerprint == ready.IdentityFingerprint) == !conflictingProof,
            "matching proof resolves ambiguity while conflicting proof still prevents admission");
    }

    private class PairingFixtureTransport : IAdbTransport
    {
        internal IReadOnlyList<AndroidDevice> Devices = [];
        internal readonly Dictionary<string, string> HardwareIds = new();
        internal readonly Dictionary<string, string> HandshakeSerials = new();
        internal readonly Dictionary<string, string> AndroidIds = new();
        internal readonly Dictionary<string, int> FingerprintReads = new();
        internal readonly HashSet<string> FirstIdentityReadFailures = new();
        internal readonly Dictionary<string, int> IdentityReads = new();
        internal readonly List<string> Commands = new();
        internal int Pushed;
        public Task<IReadOnlyList<AndroidDevice>> DevicesAsync(CancellationToken token) => Task.FromResult(Devices);
        public Task<AdbShellResult> RunShellAsync(string serial, string command, CancellationToken token)
        {
            Commands.Add(command);
            if (command == "getprop ro.serialno")
            {
                int count = IdentityReads.GetValueOrDefault(serial) + 1; IdentityReads[serial] = count;
                return Task.FromResult(new AdbShellResult(count == 1 && FirstIdentityReadFailures.Contains(serial)
                    ? "unknown" : HardwareIds.GetValueOrDefault(serial, "unknown"), 0));
            }
            if (command == "settings get secure android_id")
            {
                FingerprintReads[serial] = FingerprintReads.GetValueOrDefault(serial) + 1;
                return Task.FromResult(new AdbShellResult(AndroidIds.GetValueOrDefault(serial, ""), 0));
            }
            return Task.FromResult(new AdbShellResult(command.Contains("PairingControl ") ? "SR_UNPAIR_REQUESTED" : "", 0));
        }
        public Task PushAsync(string serial, string path, string remote, CancellationToken token) { Pushed++; return Task.CompletedTask; }
        public Task<string> VersionAsync(CancellationToken token) => Task.FromResult("41");
        public Task TrackDevicesAsync(Action<IReadOnlyList<AndroidDevice>> changed, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> FeaturesAsync(string serial, CancellationToken token) => throw new NotSupportedException();
        public Task<string> GetSerialNoAsync(string serial, CancellationToken token) => HandshakeSerials.TryGetValue(serial, out var value)
            ? Task.FromResult(value) : throw new NotSupportedException();
        public Task<IAdbShellSession> OpenShellAsync(string serial, string command, bool input, CancellationToken token) => throw new NotSupportedException();
        public Task<int> ForwardTcpToLocalAbstractAsync(string serial, string name, CancellationToken token) => throw new NotSupportedException();
        public Task RemoveForwardAsync(string serial, string local, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdbForwardRule>> ListForwardsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<Stream> OpenLocalAbstractAsync(string serial, string name, CancellationToken token) => throw new NotSupportedException();
        public Task<string> PairAsync(string address, string password, CancellationToken token) => throw new NotSupportedException();
        public Task<string> ConnectAsync(string address, CancellationToken token) => Task.FromResult("already connected to " + address);
        public Task<string> DisconnectAsync(string address, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<AndroidMdnsService>> MdnsServicesAsync(CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class KnownPairingFixtureTransport(string fingerprint) : PairingFixtureTransport, IAdbPairingIdentityProvider
    { public Task<string?> GetPairingFingerprintAsync(CancellationToken token) => Task.FromResult<string?>(fingerprint); }
}
