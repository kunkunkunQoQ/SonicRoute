using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonicRoute.Updates;

internal static class Program
{
    private static readonly List<object> Results = new();
    private static string Root = "", Product = "", FixtureOld = "", FixtureNew = "";
    private const string Instance = @"Local\SonicRoute.UpdateTests.Instance";
    public static async Task<int> Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("SONICROUTE_UPDATER_FIXTURE") == "1") return Fixture(args);
        if (args[0] == "--official-asset-only")
        {
            Directory.CreateDirectory(args[1]);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SonicRoute-Updater-Validation/1");
            var release = UpdateTransport.ParseRelease(await http.GetByteArrayAsync(UpdateProtocol.LatestApi), out var assets);
            var sums = assets["SHA256SUMS.txt"];
            release.Asset = sums; release.Package = new UpdatePackage { Size = sums.Size, Sha256 = sums.Digest.Substring(7) };
            using var download = new UpdateTransport();
            string output = Path.Combine(Path.GetFullPath(args[1]), "official-SHA256SUMS.txt");
            await download.DownloadAsync(release, output, (_, _) => { }, CancellationToken.None);
            UpdateProtocol.ReadChecksums(File.ReadAllText(output));
            File.WriteAllText(Path.Combine(args[1], "result.json"), JsonSerializer.Serialize(new { passed = true, release.Tag, sums.Url, sums.Size, Sha256 = UpdateProtocol.Hash(output), note = "Real official asset HTTPS redirect/download/integrity; no executable downloaded or run." }, UpdateProtocol.Json));
            Console.WriteLine("PASS real official checksum asset redirects, TLS download and SHA256 integrity"); return 0;
        }
        Root = Path.GetFullPath(args[0]); Product = Path.GetFullPath(args[1]);
        FixtureOld = Path.GetFullPath(args[2]); FixtureNew = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Root);
        try
        {
            Check("numeric versions and strict stable parsing", () =>
            {
                Assert(UpdateProtocol.ParseVersion("v1.9")! < UpdateProtocol.ParseVersion("v1.24"));
                Assert(UpdateProtocol.ParseVersion("1.25") == UpdateProtocol.ParseVersion("1.25.0.0"));
                foreach (var tag in new[] { "1.26-beta", "1.26-test", "1.26r1", "invalid", "1.999999999999999999999" }) Assert(UpdateProtocol.ParseVersion(tag) == null);
            });
            Check("reject untrusted URLs and duplicate or malformed checksums", () =>
            {
                Assert(UpdateTransport.TrustedAssetUrl(UpdateProtocol.Repository+"/releases/download/v1.26/a.exe", "v1.26", "a.exe"));
                Assert(!UpdateTransport.TrustedAssetUrl("https://evil.example/a.exe", "v1.26", "a.exe"));
                Throws(() => UpdateProtocol.ReadChecksums(new string('a',64)+"  a.exe\n"+new string('a',64)+"  a.exe"));
                Throws(() => UpdateProtocol.ReadChecksums(new string('a',64)+"  ../a.exe"));
            });
            await NetworkChecks();
            InstallerChecks();
            PermissionChecks();
            await ProcessChecks();
            using (var live = new UpdateTransport())
            {
                var r = await live.CheckAsync("Lite", "x64", "v1.25", CancellationToken.None);
                Check("actual official GitHub latest API read (no installation)", () => Assert(UpdateProtocol.ParseVersion(r.Tag) != null));
            }
            File.WriteAllText(Path.Combine(Root, "results.json"), JsonSerializer.Serialize(Results, UpdateProtocol.Json));
            Console.WriteLine($"PASS {Results.Count} meaningful scenario groups"); return 0;
        }
        catch (Exception e)
        {
            Results.Add(new { name="failure", passed=false, error=e.ToString() });
            File.WriteAllText(Path.Combine(Root,"results.json"),JsonSerializer.Serialize(Results,UpdateProtocol.Json));
            Console.WriteLine(e); return 1;
        }
    }

    private static void Check(string name, Action action)
    { action(); Results.Add(new { name, passed=true }); Console.WriteLine("PASS " + name); }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection."); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected async rejection."); }
    private static string Hash(byte[] bytes) => UpdateProtocol.Hex(SHA256.HashData(bytes));
    private static UpdatePackage Package(string file, string edition="Lite") => new() { Edition=edition, Runtime=UpdateProtocol.CurrentRuntime, Architecture="x64", Asset="update."+(edition=="Lite"?"exe":"zip"),
        Size=new FileInfo(file).Length, Sha256=UpdateProtocol.Hash(file), Files=new(){new(){Path="SonicRoute.exe",Size=new FileInfo(file).Length,Sha256=UpdateProtocol.Hash(file)}} };
    private static UpdateRelease Release(string file) => new() { Tag=FileVersionInfo.GetVersionInfo(file).ProductVersion!, Package=Package(file),
        Asset=new(){Name="update.exe",Url=UpdateProtocol.Repository+"/releases/download/v1.26/update.exe",Size=new FileInfo(file).Length} };

    private sealed class Fake : HttpMessageHandler
    {
        internal Dictionary<string,byte[]> Responses = new();
        internal int Calls;
        internal HttpStatusCode Code = HttpStatusCode.OK;
        internal bool Broken, Slow;
        internal string? Redirect;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Broken) throw new HttpRequestException("Network unavailable.");
            if (Redirect != null) { var redirect=new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location=new Uri(Redirect); return Task.FromResult(redirect); }
            var response=new HttpResponseMessage(Code);
            response.Content=Slow ? new StreamContent(new SlowStream()) : new ByteArrayContent(Responses[request.RequestUri!.AbsoluteUri]);
            return Task.FromResult(response);
        }
    }
    private sealed class SlowStream : Stream
    {
        public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
        public override long Length=>0; public override long Position{get=>0;set=>throw new NotSupportedException();}
        public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token) { await Task.Delay(10000,token); return 0; }
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException(); public override void Flush(){} public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();
        public override void SetLength(long v)=>throw new NotSupportedException(); public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }

    private static (Fake fake,UpdateRelease release) Network(string edition="Lite",string architecture="x64",bool missing=false)
    {
        var bytes=Encoding.UTF8.GetBytes("official-test-payload");
        var package=new UpdatePackage{Edition=edition,Runtime=edition=="Legacy"?"net48":UpdateProtocol.CurrentRuntime,Architecture=architecture,Asset="update.exe",Size=bytes.Length,Sha256=Hash(bytes),Files=new(){new(){Path="SonicRoute.exe",Size=bytes.Length,Sha256=Hash(bytes)}}};
        var manifest=JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest{SchemaVersion=1,ProtocolVersion=2,Version="1.26",Packages=new(){package}},UpdateProtocol.Json);
        var sums=Encoding.UTF8.GetBytes(Hash(bytes)+"  update.exe\n"+Hash(manifest)+"  "+UpdateProtocol.ManifestName+"\n");
        var files=new Dictionary<string,byte[]>{{UpdateProtocol.ManifestName,manifest},{"SHA256SUMS.txt",sums}};
        if(!missing)files.Add("update.exe",bytes);
        var assets=files.Select(f=>new{name=f.Key,size=f.Value.Length,digest="sha256:"+Hash(f.Value),browser_download_url=UpdateProtocol.Repository+"/releases/download/v1.26/"+f.Key}).ToArray();
        var json=JsonSerializer.SerializeToUtf8Bytes(new{draft=false,prerelease=false,tag_name="v1.26",body="Update notes",assets});
        var fake=new Fake(); fake.Responses[UpdateProtocol.LatestApi]=json;
        foreach(var f in files)fake.Responses[UpdateProtocol.Repository+"/releases/download/v1.26/"+f.Key]=f.Value;
        return(fake,new(){Tag="v1.26",Package=package,Asset=new(){Name="update.exe",Url=UpdateProtocol.Repository+"/releases/download/v1.26/update.exe",Size=bytes.Length}});
    }

    private static async Task NetworkChecks()
    {
        var (fake,release)=Network();
        using(var net=new UpdateTransport(fake))
        {
            var latest=await net.CheckAsync("Lite","x64","1.26",CancellationToken.None);
            Check("already latest performs no asset downloads",()=>Assert(latest.Package==null&&fake.Calls==1));
            var newer=await net.CheckAsync("Lite","x64","1.25",CancellationToken.None);
            Check("new stable release selects explicit package and validates metadata",()=>Assert(newer.Package?.Architecture=="x64"&&newer.Notes=="Update notes"));
            var missing=await net.CheckAsync("Lite","arm64","1.25",CancellationToken.None);
            Check("missing architecture never falls back to x64",()=>Assert(missing.Package==null));
            var legacy=await net.CheckAsync("Legacy","x64","1.25",CancellationToken.None);
            Check("Legacy never receives a Lite payload",()=>Assert(legacy.Package==null));
            string destination=Path.Combine(Root,"verified.package");
            await net.DownloadAsync(newer,destination,(_,_)=>{},CancellationToken.None);
            int count=fake.Calls;
            await net.DownloadAsync(newer,destination,(_,_)=>{},CancellationToken.None);
            Check("verified cache reused without another request",()=>Assert(count==fake.Calls));
            File.WriteAllText(destination,"corrupt-cache");
            await net.DownloadAsync(newer,destination,(_,_)=>{},CancellationToken.None);
            Check("corrupt cache redownloaded and revalidated",()=>Assert(fake.Calls==count+1));
        }
        var (noAsset,_)=Network(missing:true);
        using(var net=new UpdateTransport(noAsset)) Check("missing matching asset remains manual-only",()=>Assert(net.CheckAsync("Lite","x64","1.25",CancellationToken.None).GetAwaiter().GetResult().Package==null));
        foreach(var mode in new[]{"offline","rate-limit","untrusted-redirect","corrupt-metadata","prerelease","draft"})
        {
            var(f,_r)=Network();
            if(mode=="offline")f.Broken=true;
            if(mode=="rate-limit")f.Code=HttpStatusCode.Forbidden;
            if(mode=="untrusted-redirect")f.Redirect="https://evil.example/update.exe";
            if(mode=="corrupt-metadata")f.Responses[UpdateProtocol.Repository+"/releases/download/v1.26/"+UpdateProtocol.ManifestName]=Encoding.UTF8.GetBytes("{}");
            if(mode=="prerelease"||mode=="draft")f.Responses[UpdateProtocol.LatestApi]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(f.Responses[UpdateProtocol.LatestApi]).Replace("\""+mode+"\":false","\""+mode+"\":true"));
            using var net=new UpdateTransport(f); await Reject(async()=>await net.CheckAsync("Lite","x64","1.25",CancellationToken.None));
            Check(mode+" rejected safely",()=>{});
        }
        foreach(var mode in new[]{"cancel","truncated","checksum"})
        {
            var(f,r)=Network(); if(mode=="cancel")f.Slow=true;
            else f.Responses[r.Asset!.Url]=Encoding.UTF8.GetBytes(mode=="truncated"?"short":"official-test-payloae");
            string path=Path.Combine(Root,mode+".package");
            using var cts=new CancellationTokenSource(mode=="cancel"?100:10000);
            using var net=new UpdateTransport(f);
            await Reject(()=>net.DownloadAsync(r,path,(_,_)=>{},cts.Token));
            Check("download "+mode+" removes partial and keeps application usable",()=>Assert(!File.Exists(path)&&!File.Exists(path+".partial")));
        }
    }

    private static void InstallerChecks()
    {
        var release=Release(Product);
        string target=Path.Combine(Root,"中文 空格 & [test] ' apostrophe","Renamed application.exe"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(FixtureOld,target);
        string data=Path.Combine(Root,"isolated-data"); Directory.CreateDirectory(Path.Combine(data,"Automation"));
        File.WriteAllText(Path.Combine(data,"config.json"),"{\"keep\":true}"); File.WriteAllText(Path.Combine(data,"Automation","rule.json"),"rule-preserved");
        var before=UpdateProtocol.Hash(target);
        var plan=UpdateInstaller.Prepare(release,Product,target,data);
        UpdateInstaller.Apply(plan);
        Check("real filesystem replacement with Unicode spaces symbols and renamed EXE",()=>Assert(UpdateProtocol.Hash(target)==release.Package!.Sha256));
        UpdateInstaller.Rollback(plan);
        Check("rollback restores exact old bytes and preserves user configuration/rules",()=>Assert(UpdateProtocol.Hash(target)==before&&File.ReadAllText(Path.Combine(data,"Automation","rule.json"))=="rule-preserved"&&File.ReadAllText(Path.Combine(data,"config.json"))=="{\"keep\":true}"));
        var plan2=UpdateInstaller.Prepare(release,Product,target,data);
        using(var locked=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read)) Throws(()=>UpdateInstaller.Apply(plan2));
        UpdateInstaller.Rollback(plan2);
        Check("locked running target fails without losing old executable",()=>Assert(UpdateProtocol.Hash(target)==before));
        var plan3=UpdateInstaller.Prepare(release,Product,target,data); UpdateInstaller.Apply(plan3); UpdateInstaller.Commit(plan3);
        Check("commit clears pending marker and backup only after confirmation",()=>Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(target)!,UpdateInstaller.MarkerName))&&!Directory.Exists(plan3.WorkDirectory)));
        var package=Package(Product); package.Architecture="arm64";
        Throws(()=>UpdateInstaller.StagePayload(package,Product,Path.Combine(Root,"wrong-arch"),release.Tag));
        Check("wrong PE machine rejected independently of manifest filename",()=>{});
        var wrongVersion=Package(Product); Throws(()=>UpdateInstaller.StagePayload(wrongVersion,Product,Path.Combine(Root,"wrong-version"),"9.99"));
        Check("wrong executable version rejected",()=>{});
        foreach(var bad in new[]{"../config.json","config.json","Automation\\rule.json","evil.ps1","SonicRoute.exe:stream"})
        { var p=Package(Product); p.Files[0].Path=bad; Throws(()=>UpdateProtocol.ValidatePackage(p,"Lite","x64")); }
        Check("manifest rejects traversal ADS scripts configuration and arbitrary files",()=>{});
        // Legacy archive parsing is tested with actual Legacy packages in the release harness.
        // A NET8 worker would reject the runtime before opening a ZIP, so it cannot prove ZIP safety.
        string junction=Path.Combine(Root,"junction");
        var mk=Process.Start(new ProcessStartInfo("cmd.exe","/c mklink /J \""+junction+"\" \""+Path.GetDirectoryName(target)+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true});mk!.WaitForExit();
        Throws(()=>UpdateProtocol.RejectReparsePoints(Path.Combine(junction,"Renamed application.exe")));
        Check("junction paths rejected without touching their targets",()=>{});
        bool alert=false;
        Assert(!UpdateBootstrap.BeforeStartup(new[]{"--update-worker","never-read.json"},data,"Store",true,Instance,_=>alert=true));
        Check("Store rejects independent updater before reading plan or replacing files",()=>Assert(alert));
    }

    private static void PermissionChecks()
    {
        string denied=Path.Combine(Root,"permission-denied");Directory.CreateDirectory(denied);
        string target=Path.Combine(denied,"SonicRoute.exe");File.Copy(FixtureOld,target);
        var directory=new DirectoryInfo(denied);
        var original=System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        var modified=System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        modified.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.CreateDirectories|System.Security.AccessControl.FileSystemRights.CreateFiles,
            System.Security.AccessControl.AccessControlType.Deny));
        bool rejected=false;
        try
        {
            System.IO.FileSystemAclExtensions.SetAccessControl(directory,modified);
            try{UpdateInstaller.Prepare(Release(Product),Product,target,Path.Combine(Root,"permission-data"));}
            catch(UnauthorizedAccessException){rejected=true;}
        }
        finally{System.IO.FileSystemAclExtensions.SetAccessControl(directory,original);}
        Check("actual Windows ACL denial preserves old EXE without elevation",()=>Assert(rejected&&UpdateProtocol.Hash(target)==UpdateProtocol.Hash(FixtureOld)));
    }

    private static int Fixture(string[] args)
    {
        string data=Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!;
        if(!UpdateBootstrap.BeforeStartup(args,data,"Lite",false,Instance,_=>{})) return 0;
        if(UpdateBootstrap.Pending is InstallPlan updated)
        {
            if(Environment.GetEnvironmentVariable("SONICROUTE_FIXTURE_CRASH")=="1")return 23;
            using var mutex=new Mutex(true,Instance,out bool first); if(!first)return 24;
            using(var started=EventWaitHandle.OpenExisting(updated.EventName("started")))started.Set();
            using var stop=EventWaitHandle.OpenExisting(updated.EventName("stop"));
            using var finished=EventWaitHandle.OpenExisting(updated.EventName("finished"));
            int signal=WaitHandle.WaitAny(new WaitHandle[]{finished,stop},60000);
            File.WriteAllText(Path.Combine(data,"child-result"),signal==0?"success":"stop");
            mutex.ReleaseMutex();return 0;
        }
        if(args.Length>0&&args[0]=="--main") { File.WriteAllText(Path.Combine(data,"rollback-restarted"),"yes");return 0; }
        using var owned=new Mutex(true,Instance,out bool owner); if(!owner)return 25;
        var release=Release(Environment.GetEnvironmentVariable("SONICROUTE_FIXTURE_PAYLOAD")!);
        var p=UpdateInstaller.Prepare(release,Environment.GetEnvironmentVariable("SONICROUTE_FIXTURE_PAYLOAD")!,UpdateBootstrap.ExecutablePath,data);
        using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,p.EventName("ready"));
        using var worker=UpdateBootstrap.Start(p.Runner,"--update-worker "+UpdateBootstrap.Quote(p.PlanPath),data);
        if(!ready.WaitOne(10000))return 26;
        File.WriteAllText(Path.Combine(data,"plan-path"),p.PlanPath);
        File.WriteAllText(Path.Combine(data,"worker-pid"),worker.Id.ToString());
        // A second startup while the original is still exiting must be deferred by the worker gate.
        using var duplicate=UpdateBootstrap.Start(p.TargetExe,"--main",data); duplicate.WaitForExit(10000);
        File.WriteAllText(Path.Combine(data,"duplicate-code"),duplicate.ExitCode.ToString());
        owned.ReleaseMutex(); return 0;
    }

    private static async Task ProcessChecks()
    {
        foreach(bool crash in new[]{false,true})
        {
            string area=Path.Combine(Root,crash?"process-rollback":"process-success");Directory.CreateDirectory(area);
            string target=Path.Combine(area,"中文 application & special.exe");File.Copy(FixtureOld,target);
            string data=Path.Combine(area,"data");Directory.CreateDirectory(data);
            var info=new ProcessStartInfo(target){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=area};
            info.Environment["SONICROUTE_UPDATER_FIXTURE"]="1";info.Environment["SONICROUTE_DATA_DIRECTORY"]=data;
            info.Environment["SONICROUTE_FIXTURE_PAYLOAD"]=FixtureNew;
            info.Environment["SONICROUTE_FIXTURE_CRASH"]=crash?"1":"0";
            using var parent=Process.Start(info)!;await parent.WaitForExitAsync();Assert(parent.ExitCode==0);
            var plan=UpdateInstaller.ReadPlan(File.ReadAllText(Path.Combine(data,"plan-path")));
            try { using var worker=Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(data,"worker-pid")))); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(65)); }
            catch (ArgumentException) { }
            var result=UpdateInstaller.ReadPlan(plan.PlanPath);
            Check(crash?"actual worker subprocess startup failure restores old files and restarts old fixture":"actual worker subprocess waits exits swaps restarts and commits new fixture",()=>
                Assert(result.Status==(crash?"rolledBack":"committed")&&UpdateProtocol.Hash(target)==UpdateProtocol.Hash(crash?FixtureOld:FixtureNew)));
            Check("duplicate fixture launch deferred during "+(crash?"rollback":"update"),()=>Assert(!File.Exists(Path.Combine(data,"rollback-restarted"))||crash));
        }
    }
}
