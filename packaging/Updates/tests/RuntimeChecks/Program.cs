using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SonicRoute.Updates;
internal static class Program
{
#if SONICROUTE_PHONE
    const string ClientEdition = "Full";
#elif NET48
    const string ClientEdition = "Legacy";
#else
    const string ClientEdition = "Lite";
#endif

    static List<string> checks=new();
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks.Add(label);}
    static void Reject(Action action,string label){try{action();}catch(InvalidDataException){checks.Add(label);return;}throw new Exception("Expected rejection: "+label);}
    static async Task RejectAsync(Func<Task> action,string label){try{await action();}catch(InvalidDataException){checks.Add(label);return;}throw new Exception("Expected rejection: "+label);}
    static string Hash(byte[] bytes){using var sha=System.Security.Cryptography.SHA256.Create();return UpdateProtocol.Hex(sha.ComputeHash(bytes));}
    sealed class Fake:HttpMessageHandler
    {
        internal Dictionary<string,byte[]> Files=new(); internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Files[request.RequestUri!.AbsoluteUri])});}
    }
    static UpdatePackage Package(string runtime,string architecture="x64")=>new UpdatePackage{Runtime=runtime,Edition=runtime=="net48"?"Legacy":"Lite",Architecture=architecture,Asset="deliberately-uninformative-"+runtime+"-"+architecture+(runtime=="net48"?".zip":".exe"),Sha256=new string('a',64),Size=256,Files=runtime=="net48"?new List<UpdateFile>{new(){Path="SonicRoute.exe",Size=100,Sha256=new string('a',64)},new(){Path="SonicRoute.Core.dll",Size=100,Sha256=new string('a',64)}}:new List<UpdateFile>{new(){Path="SonicRoute.exe",Size=256,Sha256=new string('a',64)}}};
    static Fake Wire(List<UpdatePackage> packages,int protocol=2,string tag="v1.26")
    {
        var manifest=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new UpdateManifest{SchemaVersion=1,ProtocolVersion=protocol,Version=tag,Packages=packages},UpdateProtocol.Json));
        var sums=Encoding.UTF8.GetBytes(Hash(manifest)+"  "+UpdateProtocol.ManifestName+"\n"+string.Join("\n",packages.GroupBy(p=>p.Asset).Select(g=>g.First().Sha256+"  "+g.Key))+"\n");
        var assets=new List<object>{new{name=UpdateProtocol.ManifestName,size=(long)manifest.Length,digest="sha256:"+Hash(manifest),browser_download_url=Url(tag,UpdateProtocol.ManifestName)},new{name="SHA256SUMS.txt",size=(long)sums.Length,digest="sha256:"+Hash(sums),browser_download_url=Url(tag,"SHA256SUMS.txt")}};
        assets.AddRange(packages.GroupBy(p=>p.Asset).Select(g=>g.First()).Select(p=>(object)new{name=p.Asset,size=p.Size,digest="sha256:"+p.Sha256,browser_download_url=Url(tag,p.Asset)}));
        var fake=new Fake();fake.Files[UpdateProtocol.LatestApi]=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{draft=false,prerelease=false,tag_name=tag,body="notes",assets}));fake.Files[Url(tag,UpdateProtocol.ManifestName)]=manifest;fake.Files[Url(tag,"SHA256SUMS.txt")]=sums;return fake;
    }
    static string Url(string tag,string file)=>UpdateProtocol.Repository+"/releases/download/"+tag+"/"+file;
    static async Task<UpdateRelease> Fetch(Fake fake,string architecture="x64",string current="v1.25",string? edition=null){using var transport=new UpdateTransport(fake);return await transport.CheckAsync(edition??(UpdateProtocol.CurrentRuntime=="net48"?"Legacy":"Lite"),architecture,current,CancellationToken.None);}
    static List<UpdatePackage> Matrix()=>new(){Package("net10"),Package("net10","arm64"),Package("net8"),Package("net8","arm64"),Package("net48")};
    static async Task<int> Run(string assets,string output,bool subset)
    {
        var runtime=UpdateProtocol.CurrentRuntime;var all=Matrix();
        var release=await Fetch(Wire(all));Check(release.Package?.Runtime==runtime,"explicit runtime selects only current channel from all five variants");
        release=await Fetch(Wire(all),"arm64");Check(runtime=="net48"?release.Package==null:release.Package?.Runtime==runtime&&release.Package.Architecture=="arm64","architecture match remains strict; Legacy has no ARM64 package");
        release=await Fetch(Wire(all.Where(p=>p.Runtime!=runtime).ToList()));Check(release.Package==null,"missing own-runtime package cannot silently migrate to another runtime");
        var same=Wire(all,tag:"v1.25");release=await Fetch(same);Check(release.Package==null&&same.Calls==1,"current latest version avoids manifest/download requests");
        var duplicate=Matrix();duplicate.Add(Package(runtime));await RejectAsync(()=>Fetch(Wire(duplicate)),"duplicate current runtime/architecture rejected");
        var absent=Matrix();absent[0].Runtime="";await RejectAsync(()=>Fetch(Wire(absent)),"protocol 2 requires runtime metadata even for other-channel entries");
        var legacy8=Package("net8");legacy8.Runtime="";release=await Fetch(Wire(new(){legacy8},1));Check(runtime=="net8"?release.Package!=null:release.Package==null,"original protocol-1 Lite means NET8, never NET10");
        var legacy48=Package("net48");legacy48.Runtime="";release=await Fetch(Wire(new(){legacy48},1));Check(runtime=="net48"?release.Package!=null:release.Package==null,"original Legacy protocol-1 remains NET48 only");
        var ambiguous=Package("net8");ambiguous.Edition="Lite-NET8";ambiguous.Runtime="";release=await Fetch(Wire(new(){legacy8,ambiguous},1));Check(release.Package==null,"ambiguous unpublished dual-runtime protocol-1 manifest remains manual-download only");
        foreach(var p in Matrix())UpdateProtocol.ValidatePackage(p,p.Edition,p.Architecture,p.Runtime);Check(true,"all five explicit edition/runtime/architecture combinations validate");
        var wrong=Package(runtime=="net10"?"net8":"net10");Reject(()=>UpdateProtocol.ValidatePackage(wrong,wrong.Edition,wrong.Architecture),"direct staging validation rejects another runtime");
        var wrongLegacy=Package("net8");wrongLegacy.Edition="Legacy";Reject(()=>UpdateProtocol.ValidatePackage(wrongLegacy,"Legacy","x64","net8"),"Legacy cannot use a modern runtime");
        var manifest=JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(Path.Combine(assets,UpdateProtocol.ManifestName)),UpdateProtocol.Json)!;
        var actualVersion = UpdateProtocol.ParseVersion(manifest.Version);
        Check(actualVersion != null && manifest.SchemaVersion == 1 && manifest.ProtocolVersion == 2, "candidate uses supported release metadata");
        var modern = new[] { "Full:net10:x64", "Full:net10:arm64", "Lite:net10:x64", "Lite:net10:arm64" };
        var expected = actualVersion! >= new Version(2, 0) ? modern : modern.Append("Legacy:net48:x64").ToArray();
        var variants = manifest.Packages.Select(p => $"{p.Edition}:{p.Runtime}:{p.Architecture}").ToArray();
        Check(variants.Length > 0 && (subset || variants.Length == expected.Length)
            && variants.Distinct(StringComparer.Ordinal).Count() == variants.Length
            && variants.All(p => expected.Contains(p, StringComparer.Ordinal)),
            subset ? "candidate subset contains only distinct supported packages" : "candidate contains every supported package exactly once");

        var candidate = Wire(manifest.Packages, tag: "v" + manifest.Version.TrimStart('v'));
        foreach (var architecture in new[] { "x64", "arm64" })
        {
            var matching = manifest.Packages.SingleOrDefault(p => p.Edition == ClientEdition && p.Runtime == runtime && p.Architecture == architecture);
            var selected = await Fetch(candidate, architecture, current: "v0.1", edition: ClientEdition);
            Check(selected.Package?.Asset == matching?.Asset && (matching != null || selected.Asset == null),
                matching == null ? $"{ClientEdition} {architecture} receives no installable candidate" : $"{ClientEdition} {architecture} selects its exact candidate");
        }

        int staged = 0;
        foreach (var p in manifest.Packages)
        {
            var variant = $"{p.Edition} {p.Runtime} {p.Architecture}";
            var incoming = Path.Combine(output, $"stage-{p.Edition}-{p.Runtime}-{p.Architecture}");
            var payload = Path.Combine(assets, p.Asset);
            if (p.Runtime == runtime && p.Edition == ClientEdition)
            {
                UpdateInstaller.StagePayload(p, payload, incoming, manifest.Version);
                Check(File.Exists(Path.Combine(incoming, "SonicRoute.exe")), $"hash-verified package stages and PE identity validates {variant}");
                staged++;
            }
            else
            {
                Reject(() => UpdateProtocol.ValidatePackage(p, ClientEdition, p.Architecture), $"client rejects other edition/runtime {variant}");
                if (p.Runtime != runtime || (ClientEdition == "Lite" && p.Edition == "Full"))
                    Reject(() => UpdateInstaller.StagePayload(p, payload, incoming, manifest.Version), $"direct staging rejects unsupported edition/runtime {variant}");
                Check(!Directory.Exists(incoming), $"wrong edition/runtime leaves no installation directory {variant}");
            }
        }
        if (runtime == "net10" && manifest.Packages.FirstOrDefault(p => p.Edition != ClientEdition && p.Runtime == runtime) is { } other)
        {
            var forged = JsonSerializer.Deserialize<UpdatePackage>(JsonSerializer.Serialize(other, UpdateProtocol.Json), UpdateProtocol.Json)!;
            forged.Edition = ClientEdition;
            forged.Asset = $"forged-{ClientEdition}.exe";
            Reject(() => UpdateInstaller.StagePayload(forged, Path.Combine(assets, other.Asset), Path.Combine(output, "forged-edition"), manifest.Version),
                "correct hash but executable file description does not match its advertised edition");
        }
        var oldPlanPackage=Package("net8");oldPlanPackage.Runtime="";Check(UpdateProtocol.PackageRuntime(oldPlanPackage)=="net8"&&UpdateProtocol.ExecutableDescription(oldPlanPackage)=="SonicRoute Lite","old NET8 plan identity remains readable for recovery");
        return staged;
    }
    static void LegacyTransaction(string assets,string output,string historicalZip)
    {
#if NET48
        var manifest=JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(Path.Combine(assets,UpdateProtocol.ManifestName)),UpdateProtocol.Json)!;
        if (!(UpdateProtocol.ParseVersion(manifest.Version) < new Version(2, 0)))
            throw new InvalidDataException("Legacy transaction requires a historical release asset directory.");
        var package=manifest.Packages.Single(p=>p.Edition=="Legacy");
        string payload=Path.Combine(assets,package.Asset);
        string unsafeZip=Path.Combine(output,"legacy-unsafe.zip");
        File.Copy(payload,unsafeZip);
        using (var archive=ZipFile.Open(unsafeZip,ZipArchiveMode.Update))
        {
            archive.GetEntry("LICENSE")!.Delete();
            using var entry=archive.CreateEntry("../config.json").Open();
            entry.WriteByte(0);
        }
        var unsafePackage=JsonSerializer.Deserialize<UpdatePackage>(JsonSerializer.Serialize(package,UpdateProtocol.Json),UpdateProtocol.Json)!;
        unsafePackage.Sha256=UpdateProtocol.Hash(unsafeZip);
        unsafePackage.Size=new FileInfo(unsafeZip).Length;
        Reject(()=>UpdateInstaller.StagePayload(unsafePackage,unsafeZip,Path.Combine(output,"legacy-unsafe-incoming"),manifest.Version),
            "historical Legacy archive rejects traversal with matching outer hash and entry count");
        Check(!File.Exists(Path.Combine(output,"config.json")),"historical archive cannot escape the staging directory");

        string target=Path.Combine(output,"legacy 中文 & [path]");Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(historicalZip,target);
        File.WriteAllText(Path.Combine(target,"config.json"),"user-owned-settings");
        Directory.CreateDirectory(Path.Combine(target,"Automation"));File.WriteAllText(Path.Combine(target,"Automation","keep.json"),"user-rule");
        var before=Directory.GetFiles(target).ToDictionary(Path.GetFileName,UpdateProtocol.Hash);
        var release=new UpdateRelease{Tag=manifest.Version,Package=package};
        var plan=UpdateInstaller.Prepare(release,payload,Path.Combine(target,"SonicRoute.exe"),Path.Combine(output,"legacy-data"));
        bool failed=false;
        using(var locked=new FileStream(Path.Combine(target,"SonicRoute.exe"),FileMode.Open,FileAccess.Read,FileShare.Read))
            try{UpdateInstaller.Apply(plan);}catch(IOException){failed=true;}
        Check(failed&&UpdateProtocol.Hash(Path.Combine(target,"SonicRoute.Core.dll"))==package.Files.Single(f=>f.Path=="SonicRoute.Core.dll").Sha256,"Legacy locked EXE causes actual partial replacement failure");
        UpdateInstaller.Rollback(plan);
        Check(before.All(pair=>UpdateProtocol.Hash(Path.Combine(target,pair.Key))==pair.Value),"actual historical Legacy file transaction rolls back every original byte");
        var next=UpdateInstaller.Prepare(release,payload,Path.Combine(target,"SonicRoute.exe"),Path.Combine(output,"legacy-data"));
        UpdateInstaller.Apply(next);UpdateInstaller.Commit(next);
        Check(package.Files.All(f=>UpdateProtocol.Hash(Path.Combine(target,f.Path))==f.Sha256)&&File.ReadAllText(Path.Combine(target,"config.json"))=="user-owned-settings"&&File.ReadAllText(Path.Combine(target,"Automation","keep.json"))=="user-rule","actual Legacy payload installs with user settings and rules preserved (direct transaction, not old client auto-update)");
#else
        throw new InvalidOperationException("Run the Legacy transaction on net48.");
#endif
    }
    static async Task<int> Main(string[] args)
    {
        bool subset = args.Length == 3 && args[2] == "--subset";
        bool explicitLegacyTransaction = args.Length == 5 && args[2] == "--legacy-transaction";
        bool legacyTransaction = explicitLegacyTransaction || (args.Length == 3 && !subset && !args[2].StartsWith("--", StringComparison.Ordinal));
        if (args.Length != 2 && !subset && !legacyTransaction)
        {
            Console.Error.WriteLine("Usage: Checks <assets> <output> [--subset | --legacy-transaction <historical-assets> <baseline-zip>]");
            return 2;
        }
        string assets = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        try
        {
            int staged = await Run(assets, output, subset);
            if (legacyTransaction)
                LegacyTransaction(explicitLegacyTransaction ? Path.GetFullPath(args[3]) : assets, output,
                    Path.GetFullPath(args[explicitLegacyTransaction ? 4 : 2]));
            var result = new { Passed = true, Runtime = UpdateProtocol.CurrentRuntime, Edition = ClientEdition, Checks = checks,
                NetworkIsFake = true, ActualReleasePackagesStaged = staged > 0, StagedPackages = staged, ScopedPackageSubset = subset,
                HistoricalLegacyFileTransaction = legacyTransaction, InstalledOrExecutedCandidate = false };
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, UpdateProtocol.Json));
            Console.WriteLine($"PASS {ClientEdition} {UpdateProtocol.CurrentRuntime} {checks.Count}");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
            Console.WriteLine(error);
            return 1;
        }
    }
}
