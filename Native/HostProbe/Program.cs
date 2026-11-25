using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Native.Monitoring;
using SonicRoute.StoreInterop;
using SonicRoute.Updates;

internal static class Program
{
    private static readonly SortedDictionary<string,string> Evidence = new();

    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--ipc-child", var pipe]) return IpcChild(pipe).GetAwaiter().GetResult();
            if (args is ["--read-playback"])
                return SavedPlaybackStore.ReadAll().TryGetValue("fixture",out var playback) && playback.Preferred=="B" && playback.Outputs.SequenceEqual(new[]{"A","B"}) ? 0 : 3;
            if (args is ["--wait", var ev]) { using var quit = EventWaitHandle.OpenExisting(ev); return quit.WaitOne(15000) ? 0 : 2; }
            if (NativePackageIdentity.IsPackaged())
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonicRoute.Phase1.TestIdentity");
                Directory.CreateDirectory(path);
                try {
                    Evidence["packaged"] = NativePackageIdentity.GetApplicationUserModelId();
                    Evidence["commandEdition"] = AppRuntimeIdentity.Current.CommandEdition;
                    Evidence["mutex"] = AppRuntimeIdentity.Current.MutexName;
                    StoreActivation.SetTaskbarIdentity(Evidence["packaged"]);
                    int state=StoreStartup.ReadStateAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    Evidence["StartupTaskState"]=state.ToString();
                    var handle=StoreStartup.GetHandleAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    Check("StartupTask handle state",handle.State==state);
                    if(Evidence["packaged"].StartsWith("SonicRoute.PhaseOne.Validation_",StringComparison.Ordinal) && state==0)
                    {
                        handle.Disable();
                        Check("already disabled test task",handle.State==0);
                    }
                } catch(Exception e){Evidence["failure"] = e.ToString();}
                Write(Path.Combine(path,"package-result.json"));
                return Evidence.ContainsKey("failure") ? 1 : 0;
            }
            if(args is ["--activate-test", var family] && family.StartsWith("SonicRoute.PhaseOne.Validation_", StringComparison.Ordinal) && family.EndsWith("!App",StringComparison.Ordinal))
            {
                int hr = CoInitializeEx(0, 0);
                if(hr != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(hr);
                try { Console.WriteLine(StoreActivation.Activate(family,"")); }
                finally { if(hr >= 0) CoUninitialize(); }
                return 0;
            }
            if(args is not ["--check", var output, var manifestPath]) {Console.Error.WriteLine("--check <new output directory> <1.36 manifest>");return 2;}
            string root=Path.GetFullPath(output);
            if(Directory.Exists(root)) throw new IOException("Use a new isolated output directory.");
            Directory.CreateDirectory(root);
            try
            {
                Environment.SetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY",root);
                Environment.SetEnvironmentVariable("SONICROUTE_INSTANCE_ID","AotProbe_"+Guid.NewGuid().ToString("N"));
                Evidence["runtime"]=RuntimeInformation.FrameworkDescription+" "+RuntimeInformation.ProcessArchitecture;
                Check("native image",!RuntimeFeature.IsDynamicCodeSupported);
                Check("JSON reflection disabled",!JsonSerializer.IsReflectionEnabledByDefault);
                Check("portable identity",!AppRuntimeIdentity.Current.IsPackaged);
                Settings(root);
                Ipc();
                var manifest=JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), UpdateManifestContext.Default.UpdateManifest)!;
                string architecture=RuntimeInformation.ProcessArchitecture switch { Architecture.X64=>"x64", Architecture.Arm64=>"arm64", _=>throw new PlatformNotSupportedException() };
                Evidence["manifest architecture"]=architecture;
                var p=manifest.Packages.Single(p=>p.Edition==BuildIdentity.Edition && p.Architecture==architecture);
                UpdateProtocol.ValidatePackage(p,BuildIdentity.Edition,architecture);
                UpdateInstaller.StagePayload(p,Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!,p.Asset),Path.Combine(root,"incoming"),manifest.Version);
                Check("protocol 2 validates and stages real 1.36 asset",manifest.ProtocolVersion==2 && File.Exists(Path.Combine(root,"incoming","SonicRoute.exe")));
                AutomationChecks.RoundTripPlan(root,p,manifest.Version);
                Evidence["source-generated update plan and outcome"]="pass";
                AutomationChecks.DirectoryNotification().GetAwaiter().GetResult();
                Evidence["real external automation rule change"]="pass";
                MonitorProcesses(root).GetAwaiter().GetResult();
                if(Evidence.ContainsKey("WMI process trace"))
                {
                    AutomationChecks.Fallback(root);
                    Evidence["existing demand-only process snapshot fallback"]="observed own child absent, present and absent";
                }
            }
            catch(Exception e){Evidence["failure"]=e.ToString();}
            finally{AutoRuleStore.Shutdown(); Write(Path.Combine(root,"host-result.json"));}
            return Evidence.ContainsKey("failure") ? 1 : 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved,uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    private static void Check(string label,bool ok)
    {
        if(!ok)throw new InvalidOperationException(label);
        Evidence[label]="pass";
    }

    private static void Write(string path)
    {
        using var f=File.Create(path);
        using var writer=new Utf8JsonWriter(f,new JsonWriterOptions{Indented=true});
        writer.WriteStartObject();
        foreach(var (key,value) in Evidence)writer.WriteString(key,value);
        writer.WriteEndObject();
    }

    private static void Settings(string root)
    {
        File.WriteAllText(ConfigService.ConfigPath,"""{"Language":"ja-JP","QuickPanelCustomX":-250,"MicMuteOsdPersistent":true,"Hotkeys":null,"AutoRules":[{"Id":"old-rule","Name":"旧规则","Action":10}]}""");
        var cfg=ConfigService.Load();
        Check("legacy settings read and normalized",cfg.QuickPanelCustomX==-250&&cfg.MicMuteOsdDefaultInputPersistent&&cfg.Hotkeys!=null);
        Check("legacy inline rule extracted",AutoRuleStore.LoadAll().Any(r=>r.Id=="old-rule"&&r.Name=="旧规则"));
        File.WriteAllText(Path.Combine(root,"playback-outputs.json"),"""{"fixture":{"Outputs":["A","B"],"Preferred":"A"}}""");
        Check("old saved route read",SavedPlaybackStore.ReadAll()["fixture"].Preferred=="A");
        Check("saved route write",SavedPlaybackStore.TrySave("fixture",new[]{"A","B"},"B"));
        using(var reader=Start("--read-playback"))Check("saved route fresh-process readback",reader.WaitForExit(5000)&&reader.ExitCode==0);
        cfg.VolumeStep=7;
        Check("atomic config save",ConfigService.TrySave(cfg));
        string path=Path.Combine(root,"exported.json");
        Check("export imports as prior wire shape",ConfigService.ExportTo(path)&&ConfigService.ImportFrom(path)&&ConfigService.Load().VolumeStep==7);
        var rule=AutoRuleStore.Find("old-rule")!;
        rule.Name="规则二";
        AutoRuleStore.Save(rule);
        var snapshot=AutoRuleStore.ReadDisplaySnapshot();
        Check("rule fingerprint serializes",snapshot.Fingerprints?.ContainsKey("old-rule")==true);
    }

    private static Process Start(params string[] args)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false, CreateNoWindow=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("Cannot start isolated probe child.");
    }

    private static void Ipc()
    {
        string name=Guid.NewGuid().ToString("N");
        using var mutex=new Mutex(true,AppRuntimeIdentity.Current.MutexName,out bool created);
        if(!created)throw new InvalidOperationException("isolated instance name collision");
        try{
            using var bound=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            using var child=Start("--ipc-child",name);
            server.WaitForConnectionAsync(bound.Token).GetAwaiter().GetResult();
            var value=RuleCommandFrame.ReadFrameAsync(server,bound.Token).GetAwaiter().GetResult();
            RuleCommandFrame.WriteFrameAsync(server,RuleCommandLine.EncodeResult(new RuleCommandLine.Result{Code="ok",RuleName=value,Succeeded=1}),bound.Token).GetAwaiter().GetResult();
            Check("per-user framed Unicode IPC & instance mutex",value=="音跃/旧规则"&&child.WaitForExit(5000)&&child.ExitCode==0);
        }finally{mutex.ReleaseMutex();}
    }

    private static async Task<int> IpcChild(string name)
    {
        using var mutex=Mutex.OpenExisting(AppRuntimeIdentity.Current.MutexName);
        if(mutex.WaitOne(0)){mutex.ReleaseMutex();return 3;}
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token);
        await RuleCommandFrame.WriteFrameAsync(client,"音跃/旧规则",timeout.Token);
        var reply=RuleCommandLine.ReadResult(await RuleCommandFrame.ReadFrameAsync(client,timeout.Token));
        return reply is { Code:"ok", RuleName:"音跃/旧规则", Succeeded:1, ExitCode:0 } ? 0 : 4;
    }

    private static async Task MonitorProcesses(string root)
    {
        string name="Local\\SonicRoute.PhaseOne."+Guid.NewGuid().ToString("N");
        using var quit=new EventWaitHandle(false,EventResetMode.ManualReset,name);
        var started=new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped=new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint wanted=0;
        string childExe=Path.Combine(root,"SRW"+Guid.NewGuid().ToString("N")[..10]+".exe");
        File.Copy(Environment.ProcessPath!,childExe);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        ProcessTraceSubscription sub;
        try
        {
            string processName=Path.GetFileName(childExe);
            sub=await ProcessTraceSubscription.StartAsync(e=>{
                if(!e.ProcessName.Equals(processName,StringComparison.OrdinalIgnoreCase))return;
                if(e.Started)started.TrySetResult(e.ProcessId);
                else if(e.ProcessId==Volatile.Read(ref wanted))stopped.TrySetResult(e.ProcessId);
            }, hr=>{started.TrySetException(new COMException("subscription stopped",hr));stopped.TrySetException(new COMException("subscription stopped",hr));},deadline.Token);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        catch(COMException e) when(e.HResult==unchecked((int)0x80041003)||e.HResult==unchecked((int)0x80070005))
        { Evidence["WMI process trace"]="denied by OS 0x"+e.HResult.ToString("X8");return; }
        catch(UnauthorizedAccessException e){Evidence["WMI process trace"]="denied by OS 0x"+e.HResult.ToString("X8");return;}
        await using(sub)
        {
            var info=new ProcessStartInfo(childExe){UseShellExecute=false,CreateNoWindow=true};
            info.ArgumentList.Add("--wait"); info.ArgumentList.Add(name);
            using var child=Process.Start(info)!;
            Volatile.Write(ref wanted,(uint)child.Id);
            try {
                Check("WMI actual child start",await started.Task.WaitAsync(TimeSpan.FromSeconds(8))==wanted);
            }finally{quit.Set();}
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            Check("WMI actual child stop",await stopped.Task.WaitAsync(TimeSpan.FromSeconds(8))==wanted);
        }
        Evidence["WMI cancellation"]="completed";
    }
}
