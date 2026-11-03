using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SonicRoute;

internal static partial class Program
{
    private static void CheckPayload(string output,string payload)
    {
        var assembly=typeof(App).Assembly;
        var installer=assembly.GetType("SonicRoute.Updates.UpdateInstaller")!;
        var packageType=assembly.GetType("SonicRoute.Updates.UpdatePackage")!;
        var releaseType=assembly.GetType("SonicRoute.Updates.UpdateRelease")!;
        string version=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        string Hash(string path) { using var file=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        var files=new[]{new{Path="SonicRoute.exe",Size=new FileInfo(payload).Length,Sha256=Hash(payload)}};
        var package=JsonSerializer.Deserialize(JsonSerializer.Serialize(new{Edition="Full",Runtime="net10",Architecture="x64",Asset=Path.GetFileName(payload),
            Size=new FileInfo(payload).Length,Sha256=Hash(payload),Files=files}),packageType)!;
        string incoming=Path.Combine(output,"incoming");
        installer.GetMethod("StagePayload",Hidden)!.Invoke(null,new[]{package,payload,incoming,version});
        Check(File.Exists(Path.Combine(incoming,"SonicRoute.exe")) && Directory.GetFiles(incoming,"*",SearchOption.AllDirectories).Length==1,
            "the real full executable passes production checksum/identity checks and stages no loose phone files");
        string target=Path.Combine(output,"installed"), data=Path.Combine(output,"isolated-data");
        Directory.CreateDirectory(Path.Combine(target,"AndroidRuntime"));Directory.CreateDirectory(data);
        string executable=Path.Combine(target,"SonicRoute.exe");File.Copy(Path.Combine(incoming,"SonicRoute.exe"),executable);
        using(var old=new FileStream(executable,FileMode.Append))old.WriteByte(0);
        string original=Hash(executable);
        string server=Path.Combine(target,"AndroidRuntime/scrcpy-server"); File.WriteAllText(server,"fixture old server");
        File.WriteAllText(Path.Combine(target,"user-note.txt"),"preserve");File.WriteAllText(Path.Combine(data,"config.json"),"{\"keep\":true}");
        var release=Activator.CreateInstance(releaseType)!;releaseType.GetField("Tag",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(release,version);
        releaseType.GetField("Package",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(release,package);
        var plan=installer.GetMethod("Prepare",Hidden)!.Invoke(null,new[]{release,payload,executable,data})!;
        installer.GetMethod("Apply",Hidden)!.Invoke(null,new[]{plan});
        Check(Hash(executable)==Hash(payload) && File.ReadAllText(server)=="fixture old server",
            "full update installs only its single executable and preserves unrelated legacy files");
        installer.GetMethod("Rollback",Hidden)!.Invoke(null,new[]{plan});
        Check(Hash(executable)==original && File.ReadAllText(server)=="fixture old server"
            && File.ReadAllText(Path.Combine(target,"user-note.txt"))=="preserve" && File.ReadAllText(Path.Combine(data,"config.json"))=="{\"keep\":true}",
            "full update rollback restores prior files and preserves configuration and unknown files");
        File.WriteAllText(Path.Combine(output,"payload-result.json"),JsonSerializer.Serialize(new{Assertions=_checks,Payload=payload,
            PayloadSHA256=Hash(payload),ProductLaunched=false,DeviceAccess=false,FixtureOnly=true},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {_checks} full payload assertions; isolated fixture only");
    }
}
