using System.Collections;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using Application = System.Windows.Application;

internal static partial class Program
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAIL " + message); ++_checks; Console.WriteLine("PASS " + message); }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY", Path.Combine(output, "data"));
            Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID", "EditionChecks");
            if (args.Length == 3 && args[1] == "--payload") { CheckPayload(output,args[2]); return 0; }
#if SONICROUTE_PHONE
            bool phone = true; const string edition = "Full", name = "SonicRoute";
#else
            bool phone = false; const string edition = "Lite", name = "SonicRoute Lite";
#endif
            var assembly = typeof(App).Assembly;
            Check(assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title == name, "assembly title identifies " + name);
            var identityType = assembly.GetType("SonicRoute.BuildIdentity")!;
            Check((string)identityType.GetField("Edition", Hidden)!.GetRawConstantValue()! == edition,
                "compiled edition identity is " + edition);
            string[] phoneTypes = {"PhoneAudioWindow","PhoneAppItem","PhoneDevicePage","AndroidAudioHost","AndroidAppDragController","AndroidAppDrag","AndroidLabRuntime"};
            Check(phoneTypes.All(t => (assembly.GetType("SonicRoute." + t) != null) == phone),
                "phone windows, provider, virtual-display drag and Lab startup code exist only in the full assembly");
            string[] phoneReferences = {"SonicRoute.AndroidAudio","QRCoder","Concentus"};
            Check(assembly.GetReferencedAssemblies().Any(a => a.Name == "SonicRoute.AndroidAudio") == phone
                && (phone || !assembly.GetReferencedAssemblies().Any(a => phoneReferences.Contains(a.Name))),
                "Lite has no Android provider, QR or audio decoder assembly dependency");
            using (var resources = new ResourceReader(assembly.GetManifestResourceStream("SonicRoute.g.resources")!))
            {
                var keys = resources.Cast<DictionaryEntry>().Select(e => (string)e.Key).ToArray();
                Check(keys.Any(k => k.EndsWith("phoneaudiowindow.baml")) == phone && keys.Any(k => k.EndsWith("phonedevicepage.baml")) == phone,
                    "compiled phone XAML is excluded from Lite resources");
            }
            Check(assembly.GetManifestResourceNames().Contains("SonicRoute.FullRelease.files.json") == phone,
                "phone runtime package layout is embedded only in the full edition");
            var runtimeIdentity = assembly.GetType("SonicRoute.AppRuntimeIdentity")!;
            var id = Activator.CreateInstance(runtimeIdentity, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[]{false,false}, null)!;
            Check((string)runtimeIdentity.GetProperty("CommandEdition", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(id)! == "EditionChecks_" + edition,
                "command endpoints distinguish full and Lite instances");
            var application = new App(); application.InitializeComponent();
            var panel = new QuickPanelModernWindow();
            try
            {
                Check(panel.Title == name && (panel.FindName("PhoneAudioButton") != null) == phone
                    && new[]{"AndroidDropHighlight","AndroidComputerDropZone","AndroidSharedDropZone"}.All(n => (panel.FindName(n) != null) == phone)
                    && ((ScrollViewer)panel.FindName("AppListScroll")).AllowDrop == phone,
                    "quick panel identity, phone button, drop overlays and drag handlers match the compiled edition");
                Check(panel.FindName("VolumeSlider") != null && panel.FindName("AppListPanel") != null,
                    "desktop volume controls and application list remain available");
            }
            finally { panel.Close(); }
            var main = new MainWindow();
            try
            {
                if (phone) typeof(MainWindow).GetMethod("InitializePhoneManagement", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main,new object[]{false});
                Check((main.FindName("PhoneDevicesHost") != null) == phone && (main.FindName("PhoneDetailsHost") != null) == phone,
                    "Lite constructs no device sidebar or phone details host");
                Check(main.Title.StartsWith(name + " ") && main.FindName("AppsListBox") != null,
                    "full desktop app management remains available with the edition title");
            }
            finally { main.Close(); }
            var audio = assembly.GetType("SonicRoute.ApplicationAudio")!;
            if (!phone) Check(!(bool)audio.GetProperty("HasExternal", Hidden)!.GetValue(null)!, "Lite has no external phone audio provider");
            CheckUpdatePackages(assembly,phone);
            if (phone) CheckCommandRouting(output);
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Edition=edition,Assertions=_checks,PhoneCompiled=phone,
                ProductAssembly=assembly.Location,DeviceAccess=false,ProductLaunched=false,Scope="Offline build/resource/UI/protocol checks; no App.Run, ADB, real audio controls or global input"},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS {_checks} {edition} edition assertions; offline only");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void CheckUpdatePackages(Assembly assembly,bool phone)
    {
        var protocol=assembly.GetType("SonicRoute.Updates.UpdateProtocol")!;
        var packageType=assembly.GetType("SonicRoute.Updates.UpdatePackage")!;
        var validate=protocol.GetMethod("ValidatePackage",Hidden)!;
        var data=new Dictionary<string,object?> { ["Edition"]="Lite",["Runtime"]="net10",["Architecture"]="x64",["Asset"]="SonicRoute-v1.30-Lite-x64.exe",
            ["Sha256"]=new string('a',64),["Size"]=100,["Files"]=new[]{new{Path="SonicRoute.exe",Sha256=new string('b',64),Size=100}} };
        object Parse()=>JsonSerializer.Deserialize(JsonSerializer.Serialize(data),packageType)!;
        validate.Invoke(null,new[]{Parse(),"Lite","x64","net10"});
        bool rejected=false; try {validate.Invoke(null,new[]{Parse(),"Full","x64","net10"});}catch(TargetInvocationException e) when(e.InnerException is InvalidDataException){rejected=true;}
        Check(rejected,"a Lite update is never accepted as the full edition");
        if (!phone) return;
        var names=((IEnumerable<string>)protocol.GetField("FullFiles",Hidden)!.GetValue(null)!).ToArray();
        data["Edition"]="Full"; data["Asset"]="SonicRoute-v1.30-x64.exe";
        data["Files"]=names.Select(n=>new{Path=n,Sha256=new string('c',64),Size=10}).ToArray();
        validate.Invoke(null,new[]{Parse(),"Full","x64","net10"});
        Check(names.SequenceEqual(new[]{"SonicRoute.exe"}),"full updates contain exactly one executable with embedded phone components");
        data["Files"]=names.Skip(1).Select(n=>new{Path=n,Sha256=new string('c',64),Size=10}).ToArray();
        rejected=false;try{validate.Invoke(null,new[]{Parse(),"Full","x64","net10"});}catch(TargetInvocationException e) when(e.InnerException is InvalidDataException){rejected=true;}
        Check(rejected,"incomplete full update is rejected before installation");
    }
}
