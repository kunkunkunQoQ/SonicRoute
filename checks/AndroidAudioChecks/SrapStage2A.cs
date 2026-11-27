using System.Net;
using System.Security.Authentication;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapStage2ACheckAsync(string directory)
    {
        const string prefix = "sonicroute://pair?v=1&id=AAAAAAAAAAAAAAAAAAAAAA&s=_____________________w&fp=";
        string pin = new('A', 64);
        foreach (string address in new[] { "", "&h=192.168.1.2&p=65535", "&h=fd00::23&p=1", "&h=[fd00::23]&p=234", "&h=[fe80::1%2512]&p=2" })
        {
            using var invite = SrapInvite.Parse(prefix + pin.ToLowerInvariant() + address);
            Check(invite.Fingerprint == pin && invite.Secret.All(b => b == 255), "invite parse " + address);
        }
        foreach (string illegal in new[] { "&x=1", "&v=1", "&h=192.168.0.1", "&p=2", "&h=001.0.0.1&p=2",
            "&h=1.2.3.4&p=0", "&h=1.2.3.4&p=65536", "&h=1.2.3.4&p=002", "&h=1.2.3.4&p=+2", "&h=1.2.3.4&p=2#frag",
            "&h=myphone.local&p=2", "&h=127.0.0.1&p=2", "&h=::1&p=2", "&h=ff02::1&p=2", "&h=fe80::1&p=2",
            "&h=[fe80::1%wlan0]&p=2", "&h=fd00%3A%3A23&p=2", "&h=1.2.3.4%26p%3D2&p=2",
            "&h=[fd00::1]:24&p=2", "&h=[[fd00::1]]&p=2", "&h=fd00::1 &p=2" })
        {
            bool rejected = false;
            try { using var _ = SrapInvite.Parse(prefix + pin + illegal); }
            catch (FormatException ex) { rejected = !ex.Message.Contains(prefix, StringComparison.Ordinal); }
            Check(rejected, "reject invite suffix " + illegal);
        }
        foreach (string malformed in new[] { prefix.Replace("____w", "____x") + pin, prefix + pin[..63], prefix + pin + "&",
            prefix.Replace("v=1", "v=2") + pin, prefix.Replace("AAAAAA&", "AAAAA=&") + pin })
        {
            bool rejected = false;
            try { using var _ = SrapInvite.Parse(malformed); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "reject malformed invite");
        }
        Check(!AndroidClipboardClient.IsSupported(prefix + pin), "SRAP invitations excluded from phone clipboard sync");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "profiles-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new AndroidPhoneStore(path);
        var phone = new AndroidDevice("ADB-TEST", "ADB", "device", false, "ADB-TEST");
        store.RememberDevice(phone);
        string adbId = store.Profile.Id;
        var app = store.RegisterApp(pin, IPEndPoint.Parse("192.168.1.2:5678"), store.ManagementRevision);
        Check(store.Profiles.Count == 2 && app.Id != adbId && app.LastDevice?.IsApp == true, "pair creates isolated app profile pending 2B");
        store.Rename("Test app");
        store.Retain(new("org.sonicroute.app", "SonicRoute", 0, ""));
        store.RevokeAppTrust(app.Id, pin);
        await store.FlushAsync();
        var reloaded = new AndroidPhoneStore(path);
        Check(reloaded.Profile.Id == app.Id && reloaded.Profile.AppTrustRevoked && reloaded.Profile.LastAppAddress == null
            && reloaded.Profile.RetainedApps.Single().Uid == 0 && reloaded.FindProfile(phone)?.Id == adbId, "reopen profile and revoked trust with same ADB ID");
        var manager = new AndroidConnectionManager(reloaded, () => throw new InvalidOperationException("An App action must not modify ADB discovery."));
        manager.SetDisconnected(reloaded.Profile.LastDevice!, true);
        Check(manager.Remove(app.Id, out var aliases) && aliases.Single().IsApp, "remove app through coordinator without touching ADB discovery");
        var restored = reloaded.RegisterApp(pin, IPEndPoint.Parse("192.168.1.3:3210"), reloaded.ManagementRevision);
        Check(restored.Id == app.Id && restored.Name == "Test app" && !restored.AppTrustRevoked, "explicit repair restores same archived profile");
    }

    private static async Task SrapLiveAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var store = new AndroidPhoneStore(Path.Combine(directory, "phone.json"));
        using var identity = new SrapIdentity(directory);
        await using var transport = new SrapPhoneTransport(store, directory);
        var manager = new AndroidConnectionManager(store, () => throw new InvalidOperationException("LAN must not call ADB"), app: transport);
        transport.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var stop = deadline.Token;
        Console.WriteLine("INVITE");
        string text = await Console.In.ReadLineAsync(stop) ?? throw new EndOfStreamException();
        using var invite = SrapInvite.Parse(text);
        var endpoint = invite.Endpoint ?? throw new InvalidOperationException("Live test expects address in invitation.");
        var found = await SrapPhoneTransport.DiscoverAsync(stop);
        Console.WriteLine($"MDNS_INITIAL count={found.Count} matchingEndpoint={found.Any(s => s.Address == endpoint.ToString() && s.App?.Pairing == true)}");
        var client = new SrapClient(identity);
        bool wrongPinRejected = false;
        try { using var _ = await client.ConnectAsync(endpoint, new string('1', 64), stop); }
        catch (AuthenticationException) { wrongPinRejected = true; }
        Check(wrongPinRejected, "live incorrect SPKI refused");
        var original = await transport.PairAsync(text, null, () => Console.WriteLine("PAIR_PENDING"), stop, "SonicRoute Windows 2A Lab");
        text = "";
        var session = transport.Session(original)!;
        Check(manager.IsCurrent(original) && session.Protocol is SslProtocols.Tls12 or SslProtocols.Tls13, "live paired and admitted by shared manager");
        Console.WriteLine($"TLS {session.Protocol} {session.CipherSuite}");
        bool badVersion = false;
        try { using var _ = await client.ConnectAsync(endpoint, invite.Fingerprint, stop, major: 2); }
        catch (SrapException ex) when (ex.Code == "version_mismatch") { badVersion = true; }
        Check(badVersion, "live major-version mismatch refused");
        using (var tls12 = await new SrapClient(identity, SslProtocols.Tls12).ConnectAsync(endpoint, invite.Fingerprint, stop))
            Check(tls12.Protocol == SslProtocols.Tls12, "live TLS 1.2 mutual authentication");
        var outputs = await session.RequestAsync("audio.devices.list", "audio.devices", new(), stop);
        var apps = await session.RequestAsync("catalog.list", "catalog.apps", new(), stop);
        Check(outputs.GetProperty("outputs").GetArrayLength() > 0 && apps.GetProperty("apps").GetArrayLength() > 0, "live audio endpoints and app catalogue");
        int percent = (await session.ReadVolumeAsync(stop)).GetProperty("percent").GetInt32();
        Check(percent is >= 0 and <= 100, "live system media volume readback");
        try
        {
            var volume = await session.SetVolumeAsync(percent, stop);
            Check(volume.GetProperty("percent").GetInt32() == percent, "live system media volume write and readback at original level");
        }
        catch (SrapException ex) { ReportAndroidError("system media volume write", ex); }
        uint track = await session.StartTrackAsync("current", stop);
        try
        {
            try
            {
                var gain = await session.SetTrackVolumeAsync(track, 37, false, stop);
                Check(gain.GetProperty("trackGainPercent").GetInt32() == 37 && !gain.GetProperty("trackMuted").GetBoolean(), "live owned-track gain readback");
            }
            catch (SrapException ex) { ReportAndroidError("owned-track gain", ex); }
            try
            {
                Check(SrapClient.Text(await session.RouteTrackAsync(track, "speaker", stop), "actualKey") == "speaker", "live owned-track speaker routing with verified readback");
            }
            catch (SrapException ex) { ReportAndroidError("owned-track speaker routing", ex); }
        }
        finally { await session.StopTrackAsync(track, stop); }

        long revision = session.Capabilities.Revision;
        Console.WriteLine($"CAPABILITY_SWITCH {revision}");
        await AwaitAsync(() => session.Capabilities.Revision > revision, 55, stop);
        Check(session.Capabilities.Revision > revision, $"live capability revision {revision} -> {session.Capabilities.Revision}");
        Check(!session.Capabilities.Allows("audio.route.other_app"), "live third-party route remains unavailable");

        _ = await session.StartTrackAsync("current", stop);
        Console.WriteLine("RESTART_LISTENER");
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(45), stop);
        Check(!manager.IsCurrent(original) && manager.OperationToken(original).IsCancellationRequested, "live disconnect invalidates old attachment and operation token");
        await AwaitAsync(() => manager.Resolve(store.Profile.LastDevice) is { } live && !ReferenceEquals(live, original), 50, stop);
        var fresh = manager.Resolve(store.Profile.LastDevice);
        Check(fresh != null && !ReferenceEquals(fresh, original) && store.Profiles.Count == 1, "live automatic mTLS reconnect keeps one profile and replaces generation");
        Check((await transport.Session(fresh!)!.ReadVolumeAsync(stop)).GetProperty("percent").GetInt32() == percent, "live request after reconnect");
        string storeAddress = store.Profile.LastAppAddress!;
        Console.WriteLine("REVOKE_IN_ANDROID");
        var revoked = await transport.Session(fresh!)!.Completion.WaitAsync(TimeSpan.FromSeconds(60), stop);
        await AwaitAsync(() => store.Profile.AppTrustRevoked, 5, stop);
        Check(revoked == "permission_lost" && store.Profile.AppTrustRevoked && manager.Resolve(store.Profile.LastDevice) == null, "live revocation retires session and disables trust");
        bool denied = false;
        try { using var _ = await client.ConnectAsync(IPEndPoint.Parse(storeAddress), invite.Fingerprint, stop); }
        catch (SrapException ex) when (ex.Code == "not_paired") { denied = true; }
        Check(denied, "live Android rejects previously trusted certificate after revoke");

        static void ReportAndroidError(string operation, SrapException error)
        {
            string? reason = error.WireReason;
            string hint = reason switch
            {
                { } s when s.Contains("Looper", StringComparison.OrdinalIgnoreCase) => "looper",
                { } s when s.Contains("thread", StringComparison.OrdinalIgnoreCase) => "thread",
                { } s when s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') => s,
                _ => "(unclassified)"
            };
            Console.WriteLine($"ANDROID_ERROR {operation}: {error.Code}, reason={hint}");
        }

        static async Task AwaitAsync(Func<bool> condition, int seconds, CancellationToken token)
        {
            long deadline = Environment.TickCount64 + seconds * 1000L;
            while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(150, token);
        }
    }

    private static async Task SrapLiveRevokeAsync(string directory)
    {
        var store = new AndroidPhoneStore(Path.Combine(directory, "phone.json"));
        var profile = store.Profile;
        using var identity = new SrapIdentity(directory);
        await using var transport = new SrapPhoneTransport(store, directory);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(115));
        var manager = new AndroidConnectionManager(store, () => throw new InvalidOperationException("LAN must not call ADB"), app: transport);
        transport.Start();
        for (int i = 0; i < 80 && manager.Resolve(profile.LastDevice) == null; i++) await Task.Delay(250, stop.Token);
        var device = manager.Resolve(profile.LastDevice);
        Check(device != null, "live persisted CNG identity and profile reconnect after C# client restart");
        var session = transport.Session(device!)!;
        string address = store.Profile.LastAppAddress!;
        Console.WriteLine("REVOKE " + Convert.ToHexString(identity.Fingerprint)[..16]);
        string? code = await session.Completion.WaitAsync(TimeSpan.FromSeconds(80), stop.Token);
        for (int i = 0; i < 20 && !store.Profile.AppTrustRevoked; i++) await Task.Delay(250, stop.Token);
        Check(code == "permission_lost" && store.Profile.AppTrustRevoked && manager.Resolve(profile.LastDevice) == null
            && manager.OperationToken(device!).IsCancellationRequested, "live Android revoke drops the mTLS session and disables automatic restore");
        bool refused = false;
        try { using var _ = await new SrapClient(identity).ConnectAsync(IPEndPoint.Parse(address), profile.AppPublicKeySha256!, stop.Token); }
        catch (SrapException error) when (error.Code == "not_paired") { refused = true; }
        Check(refused, "live Android denies revoked identity even on a new connection");
        await store.FlushAsync();
        Check(new AndroidPhoneStore(store.FilePath).Profile.AppTrustRevoked, "live revocation durable across application restart");
    }
}
