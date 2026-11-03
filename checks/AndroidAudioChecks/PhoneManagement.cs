using SonicRoute.AndroidAudio;
using System.Text.Json;

internal static partial class Program
{
    private static async Task ConnectionStateAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var adb = new AdbClient(args[1]);
        var device = (await adb.DevicesAsync(timeout.Token)).First(d => d.State == "device" && d.IsWifi);
        var commands = new List<string[]>();
        var wireless = new AndroidWirelessConnection((token, command) =>
        {
            if (command[0] is not ("devices" or "mdns") && !(command.Length == 3 && command[0] == "-s" && command[2] == "get-serialno"))
                throw new InvalidOperationException("This live check allows only device/discovery/identity reads.");
            commands.Add(command); return adb.RunAsync(token, command);
        });
        var result = device.Serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
            ? await wireless.ReconnectAsync(device, timeout.Token)
            : await wireless.FinishPairingAsync(new(device.Serial, null), timeout.Token);
        Check(result.Device.Serial == device.Serial && result.Device.State == "device", "existing live Wi-Fi transport is recognized as ready with its actual ADB identity");
        Check(commands.All(c => c[0] != "connect" && c[0] != "pair"), "live readiness verification sends no pairing, connect/disconnect, audio, video or media controls");
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new {Device=result.Device, Commands=commands,
            Scope="Existing connection readiness only; no new QR scan or pairing was performed.", Assertions=_checks}, new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"PASS {_checks} read-only connection assertions");
    }
    private static async Task RetainedCatalogAsync(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var adb = new AdbClient(args[1]);
        var device = (await adb.DevicesAsync(timeout.Token)).First(d=>d.State=="device");
        string[] packages = { "cn.wenyu.bodian", "tv.danmaku.bili" };
        var apps = await adb.AppsAsync(device,timeout.Token,packages);
        Check(apps.Count==2 && apps.Select(a=>a.Package).ToHashSet().SetEquals(packages), "live retained-catalog query returns only the two requested installed packages");
        Check(apps.All(a=>a.Uid>0 && a.IconPng is {Length:>0}), "retained-only query resolves actual app UIDs, names and PNG icons");
        await File.WriteAllTextAsync(args[2],JsonSerializer.Serialize(new {Device=device,Requested=packages,
            Apps=apps.Select(a=>new {a.Package,a.Name,a.Uid,IconBytes=a.IconPng?.Length}),ControlsSent=0,
            FullCatalogueRequested=false,Assertions=_checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {_checks} read-only allowlist assertions; no audio, display or media controls.");
    }
    private static async Task CheckPhoneManagementAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "phone-fixture-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "android-phone.json");
        var store = new AndroidPhoneStore(file);
        Check(store.Profile.RetainedApps.Count == 0, "fresh phone library has an explicit empty allowlist");
        var device = new AndroidDevice("10.0.0.1:5000", "Phone", "device", true);
        var one = new AndroidApp("cn.test.one", "One", 123, "media", new byte[] { 1,2,3 });
        var two = new AndroidApp("cn.test.two", "Two", 124, "other");
        int changes = 0; store.Changed += () => ++changes;
        store.RememberDevice(device); store.Rename("我的测试手机"); store.Retain(one); store.Retain(two);
        int before = changes; store.Retain(one); store.RememberDevice(device); store.UpdateMetadata(new[] { one, two });
        Check(changes == before, "unchanged retained apps, device and metadata cause no write notifications");
        store.Remove(two.Package); store.UpdateMetadata(new[] { one with { Name = "Updated", Uid = 200 } });
        await store.FlushAsync(); var restored = new AndroidPhoneStore(file);
        Check(restored.Profile.Name == "我的测试手机" && restored.Profile.RetainedApps.Count == 1
            && restored.Profile.RetainedApps[0].Uid == 200 && restored.Profile.RetainedApps[0].IconPng!.SequenceEqual(one.IconPng!)
            && restored.Profile.LastDevice == device, "retained metadata, icons and phone name survive restart without a full catalogue");
        bool rejected = false;
        try { store.Retain(one with { Package = "bad;command" }); } catch (ArgumentException) { rejected = true; }
        Check(rejected && store.Profile.RetainedApps.Count == 1, "invalid retained package cannot enter the saved allowlist");
        var missing = new AdbClient(Path.Combine(directory,"missing-runtime"));
        Check((await missing.AppsAsync(device,default,Array.Empty<string>())).Count == 0, "empty allowlist starts no ADB process and needs no runtime files");
        Check(AndroidWirelessConnection.ValidateAddress(" 192.168.1.10:37000 ") == "192.168.1.10:37000"
            && AndroidWirelessConnection.ValidateAddress("[fe80::1]:37000") == "[fe80::1]:37000", "wireless endpoints normalize IPv4 and bracketed IPv6");
        foreach (string bad in new[] { "127.0.0.1;id:123", "127.0.0.1:0", "127.0.0.1:65536", "example.com:123" })
        {
            rejected = false; try { AndroidWirelessConnection.ValidateAddress(bad); } catch (ArgumentException) { rejected = true; }
            Check(rejected,"wireless input rejects invalid address: " + bad);
        }
        var qr = AndroidQrPairing.Create();
        Check(qr.Payload.StartsWith("WIFI:T:ADB;S:studio-") && qr.Payload.EndsWith(";;") && qr != AndroidQrPairing.Create(),
            "QR pairing uses fresh AOSP service identity and ephemeral secret");
        var commands = new List<string[]>();
        var wireless = new AndroidWirelessConnection((token,args) =>
        {
            token.ThrowIfCancellationRequested(); commands.Add(args);
            return Task.FromResult(args[0] switch
            {
                "pair" => "Successfully paired to 192.168.1.10:37000",
                "connect" => "connected to 192.168.1.10:38000",
                "mdns" => $"{qr.ServiceName} _adb-tls-pairing._tcp 192.168.1.10:37000\nphone _adb-tls-connect._tcp 192.168.1.10:38000\nother _adb-tls-connect._tcp 192.168.1.11:39000\n",
                "devices" => "List of devices attached\n192.168.1.10:38000 device model:Test\n",
                _ => ""
            });
        });
        await wireless.PairCodeAsync("192.168.1.10:37000","123456",default);
        Check(commands[0].SequenceEqual(new[]{"pair","192.168.1.10:37000","123456"}), "pairing command uses the explicit pairing endpoint and code as separate arguments");
        var paired = await wireless.PairQrAsync(qr, default);
        Check(paired.Address == "192.168.1.10:37000" && commands.Last().Last() == qr.Password, "QR pairs only the service matching this generated identity");
        int count = commands.Count;
        try { await wireless.PairCodeAsync("192.168.1.10:37000","bad",default); } catch (ArgumentException) { }
        Check(commands.Count == count, "invalid pairing code performs no process work");
        var found = await wireless.FinishPairingAsync(paired,default);
        Check(found.Device.State == "device", "pairing completion returns an authorized connected phone");
        await CheckConnectionRegressionsAsync();
        await CheckDeviceDiscoveryAsync();
        await CheckCatalogCacheAsync(directory, device, one);
        byte[] wire = BuildWire();
        await using var manager = new AndroidAudioManager(() => new AndroidAudioSession(() => new FakeTransport(wire,()=>{}),(_,_)=>new FakePlayer()));
        var owned = await manager.AddAsync(device,one, false, displayOwned:true);
        await manager.ReleaseDisplayAudioAsync(owned);
        Check(manager.Sources.Count == 0, "closing a standalone display releases only its temporary computer audio");
        var explicitSource = await manager.AddAsync(device,one,true);
        var displaySource = await manager.AddAsync(device,one,false,displayOwned:true);
        await manager.ReleaseDisplayAudioAsync(displaySource);
        Check(ReferenceEquals(explicitSource,displaySource) && manager.Sources.Count == 1 && explicitSource.KeepPhoneAudio,
            "existing playback-bar choice takes priority over opening and closing a display");
        await manager.RemoveAsync(explicitSource.Id);
        owned = await manager.AddAsync(device,one,false,displayOwned:true);
        var promoted = await manager.AddAsync(device,one,false);
        await manager.ReleaseDisplayAudioAsync(owned);
        Check(ReferenceEquals(owned,promoted) && manager.Sources.Count == 1, "manual playback choice during a display keeps audio after the display closes");
    }
}
