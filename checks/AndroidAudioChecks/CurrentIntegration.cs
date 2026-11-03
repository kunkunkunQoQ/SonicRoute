using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task CurrentIntegrationAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var samples = new Dictionary<string, double>(); var clock = Stopwatch.StartNew();
        var bundle = new AndroidRuntimeBundle(Path.Combine(output, "cache"));
        var adb = new AdbClient(bundle);
        samples["ConstructMs"] = clock.Elapsed.TotalMilliseconds;
        Check(!Directory.Exists(bundle.DirectoryPath), "constructing bundled phone services performs no extraction");
        clock.Restart(); string version = await adb.RunAsync(default, "version"); samples["ColdAdbVersionMs"] = clock.Elapsed.TotalMilliseconds;
        Check(version.Contains("Android Debug Bridge") && Directory.GetFiles(bundle.DirectoryPath, "*", SearchOption.AllDirectories).Length == 3,
            "first ADB request prepares only its three native files and runs without a phone");
        clock.Restart(); new VirtualDisplayRuntime(adb).Validate(); samples["ColdVideoPrepareMs"] = clock.Elapsed.TotalMilliseconds;
        Check(Directory.GetFiles(bundle.DirectoryPath, "*", SearchOption.AllDirectories).Length == 10 && !File.Exists(adb.ServerFile),
            "first video request materializes its own components without loading audio tools");
        var written = Directory.GetFiles(bundle.DirectoryPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.GetLastWriteTimeUtc);
        clock.Restart(); var second = new AdbClient(new AndroidRuntimeBundle(Path.Combine(output, "cache")));
        await second.RunAsync(default, "version"); new VirtualDisplayRuntime(second).Validate(); samples["WarmAdbAndVideoMs"] = clock.Elapsed.TotalMilliseconds;
        Check(written.All(p => File.GetLastWriteTimeUtc(p.Key) == p.Value), "a new process provider reuses validated tools without rewriting files");
        string helper = Path.Combine(bundle.DirectoryPath, "sonicroute-catalog.jar");
        adb.ValidateServer();
        byte[] correct = File.ReadAllBytes(helper), damaged = (byte[])correct.Clone(); damaged[0] ^= 1; File.WriteAllBytes(helper, damaged);
        new AdbClient(new AndroidRuntimeBundle(Path.Combine(output, "cache"))).ValidateServer();
        Check(File.ReadAllBytes(helper).SequenceEqual(correct), "a damaged cached helper is repaired from the embedded component");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); var unused = new AndroidRuntimeBundle(Path.Combine(output, "canceled")); bool stopped = false;
            try { await new AdbClient(unused).RunAsync(canceled.Token, "version"); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && !Directory.Exists(unused.DirectoryPath), "cancellation before first use creates no tools or process");
        }
        string notices = AndroidRuntimeBundle.ReadNotices();
        Check(new[] { "ADB-NOTICE", "FFmpeg-LICENSE", "SDL2-LICENSE", "zlib-LICENSE", "QRCoder-LICENSE", "Concentus", "NAudio" }.All(notices.Contains),
            "single executable preserves readable notices for every bundled dependency");
        Check(AndroidWirelessConnection.PairingFingerprint(Convert.ToBase64String(Enumerable.Range(0,64).Select(i=>(byte)i).ToArray()) + " test")
            == "B2:D3:F5:6B:C1:97:FD:98:5D:59:65:07:9B:5E:71:48", "unpairing uses Android's exact public-key fingerprint format");
        Check(AndroidWirelessConnection.ParseServices("studio-name (2)._adb-tls-pairing._tcp.local. _adb-tls-pairing._tcp.local. 192.0.2.5:37000")[0].Name == "studio-name (2)"
            && AndroidWirelessConnection.MatchesPairingName("studio-name (2)","studio-name")
            && !AndroidWirelessConnection.MatchesPairingName("studio-name-other", "studio-name"), "QR discovery accepts DNS-SD collision suffixes and rejects unrelated identities");
        byte[] packet = DnsServicePacket();
        var services = AndroidMdnsDiscovery.ParseResponse(packet, IPAddress.Parse("192.0.2.5"));
        Check(services.Count == 1 && services[0] == new AndroidMdnsService("studio-name", "_adb-tls-pairing._tcp", "192.0.2.5:37000"),
            "direct DNS-SD discovery reads compressed SRV responses with the advertised pairing port");
        packet[12] = 0xc0; packet[13] = 12;
        Check(AndroidMdnsDiscovery.ParseResponse(packet, IPAddress.Loopback).Count == 0,
            "cyclic DNS pointers terminate without accepting a malformed discovery result");
        var qr = AndroidQrPairing.Create();
        var wireless = new AndroidWirelessConnection((_, args) => Task.FromResult(args[0] switch
        {
            "mdns" => $"{qr.ServiceName} _adb-tls-pairing._tcp 192.0.2.5:37000",
            "pair" => "Successfully paired to 192.0.2.5:37000 [guid=adb-phone]",
            "devices" => "List of devices attached\nadb-phone-abc._adb-tls-connect._tcp device model:Phone\n",
            _ => throw new IOException("unexpected")
        }), NoConnectionDelay, (_,_,_,_,_,_) => Task.FromException<AndroidMdnsService?>(new SocketException()));
        var result = await wireless.FinishPairingAsync(await wireless.PairQrAsync(qr, default), default);
        Check(result.Device.State == "device", "optional discovery faults cannot override successful QR pairing and readiness");
        await CheckConnectionRegressionsAsync();
        Check(VirtualDisplayClient.TryReadState("[server] INFO: SonicRoute app-state: display=12 state=phone") == VirtualDisplayState.OnPhone
            && VirtualDisplayClient.TryReadState("unrelated state=phone") == null
            && VirtualDisplayClient.TryReadState("[server] INFO: SonicRoute app-state: display=0 state=active") == null,
            "virtual-display state accepts only structured logs for owned non-default displays");
        var device = new AndroidDevice("fixture", "Phone", "device", true); var app = new AndroidApp("cn.test.app", "Test",123,"media");
        await VirtualDisplaysAsync(device, app);
        var info = VirtualDisplayClient.CreateStartInfo(adb, device, app, 1, 2);
        Check(info.ArgumentList.Contains("--prefer-text") && !VirtualDisplayClient.CreateStartInfo(adb, device, app with {Category="games"}, 1, 2).ArgumentList.Contains("--prefer-text"),
            "desktop IME text routing keeps raw game-key behavior isolated");
        byte[] wire = BuildWire();
        await using var manager = new AndroidAudioManager(() => new AndroidAudioSession(() => new FakeTransport(wire,()=>{}),(_,_)=>new FakePlayer()));
        var owned = await manager.AddAsync(device,app,false,displayOwned:true); await manager.ReleaseDisplayAudioAsync(owned);
        Check(manager.Sources.Count==0, "phone priority releases temporary display-owned computer audio");
        var explicitSource=await manager.AddAsync(device,app,true); var displayed=await manager.AddAsync(device,app,false,displayOwned:true);
        await manager.ReleaseDisplayAudioAsync(displayed);
        Check(ReferenceEquals(explicitSource,displayed) && manager.Sources.Count==1 && explicitSource.KeepPhoneAudio,
            "playback-bar ownership survives a display's phone transition and closure");
        await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Assertions=_checks,Samples=samples,DeviceAccess=false},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {_checks} current integration assertions; no phone connection or capture");
    }
    private static byte[] DnsServicePacket()
    {
        using var data = new MemoryStream();
        data.Write(new byte[]{0,0,0x84,0,0,1,0,1,0,0,0,0});
        foreach(string label in "studio-name._adb-tls-pairing._tcp.local".Split('.')) { byte[] bytes=Encoding.UTF8.GetBytes(label);data.WriteByte((byte)bytes.Length);data.Write(bytes); }
        data.WriteByte(0); data.Write(new byte[]{0,33,0,1});
        data.Write(new byte[]{0xc0,12,0,33,0,1,0,0,0,120,0,18,0,0,0,0,0x90,0x88,4,(byte)'h',(byte)'o',(byte)'s',(byte)'t',5,(byte)'l',(byte)'o',(byte)'c',(byte)'a',(byte)'l',0});
        return data.ToArray();
    }
}
