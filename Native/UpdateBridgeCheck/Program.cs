using System.Diagnostics;
using System.Text.Json;
using SonicRoute.Updates;

if(args.Length!=3) return 2;
string hosts=Path.GetFullPath(args[0]), oldPackages=Path.GetFullPath(args[1]), work=Path.GetFullPath(args[2]);
if(Directory.Exists(work))throw new IOException("Use a new test directory.");
Directory.CreateDirectory(work);
#if SONICROUTE_PHONE
const string edition="Full";
#else
const string edition="Lite";
#endif
foreach(string architecture in new[]{"x64","arm64"})
{
    string payload=Path.Combine(hosts,edition+"-"+architecture,"HostProbe.exe");
    string version=FileVersionInfo.GetVersionInfo(payload).ProductVersion!;
    string digest=UpdateProtocol.Hash(payload);
    long size=new FileInfo(payload).Length;
    string name="SonicRoute-v"+version+(edition=="Lite"?"-Lite":"")+"-"+architecture+".exe";
    var p=new UpdatePackage{Edition=edition,Architecture=architecture,Runtime="net10",Asset=name,Size=size,Sha256=digest,Files=[new UpdateFile{Path="SonicRoute.exe",Size=size,Sha256=digest}]};
    string data=Path.Combine(work,architecture,"data");
    string target=Path.Combine(work,architecture,"target","SonicRoute.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(Path.Combine(oldPackages,name),target);
    var old=UpdateProtocol.Hash(target);
    var plan=UpdateInstaller.Prepare(new UpdateRelease{Package=p,Tag=version},payload,target,data);
    var reread=UpdateInstaller.ReadPlan(plan.PlanPath);
    UpdateInstaller.Apply(reread);
    if(UpdateProtocol.Hash(target)!=digest)throw new IOException("candidate not installed");
    UpdateInstaller.Rollback(reread);
    if(UpdateProtocol.Hash(target)!=old)throw new IOException("original bytes not restored");
    bool refused=false;
    try{UpdateProtocol.ValidatePackage(p,edition,architecture=="x64"?"arm64":"x64");}
    catch(InvalidDataException){refused=true;}
    if(!refused)throw new InvalidDataException("cross-architecture acceptance");
    Console.WriteLine(JsonSerializer.Serialize(new{edition,architecture,selectedRuntime=p.Runtime,payloadHash=digest,oldHash=old,copyApplied=true,oldBytesRestored=true,wrongArchitectureRefused=true,launched=false}));
}
return 0;
