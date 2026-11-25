using System.Diagnostics;
using System.Text.Json;
using SonicRoute.Core;
using SonicRoute.Updates;

internal static class AutomationChecks
{
    internal static async Task DirectoryNotification()
    {
        var added=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            if(AutoRuleStore.Find("external-phase-one")?.Name=="external")added.TrySetResult();
        }
        AutoRuleStore.Changed+=Observe;
        try
        {
            string f=Path.Combine(AutoRuleStore.RulesDir,"external-phase-one.json");
            await File.WriteAllTextAsync(f,"""{"Id":"external-phase-one","Name":"external","Trigger":0,"Action":10}""");
            await added.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally {AutoRuleStore.Changed-=Observe;}
    }

    // Run the existing compatibility reader on known process boundaries. This tests
    // the fallback's observations without starting a replacement monitor or timer.
    internal static void Fallback(string root)
    {
        string name="SRP"+Guid.NewGuid().ToString("N")[..10];
        using var view=new ProcessSnapshotService(name);
        if(view.CaptureState().Present.Contains(name))throw new InvalidOperationException("unexpected existing target");
        string target=Path.Combine(root,name+".exe");
        File.Copy(Environment.ProcessPath!,target);
        using var quit=new EventWaitHandle(false,EventResetMode.ManualReset,@"Local\"+name);
        var info=new ProcessStartInfo(target){UseShellExecute=false,CreateNoWindow=true};
        info.ArgumentList.Add("--wait");
        info.ArgumentList.Add(@"Local\"+name);
        using var child=Process.Start(info)!;
        try
        {
            if(!view.CaptureState().Present.Contains(name))throw new InvalidOperationException("fallback missed start");
        }
        finally { quit.Set(); }
        if(!child.WaitForExit(5000)||view.CaptureState().Present.Contains(name))throw new InvalidOperationException("fallback missed exit");
    }

    internal static void RoundTripPlan(string root,UpdatePackage p,string version)
    {
        var plan=new InstallPlan{Id=Guid.NewGuid().ToString("N"),DataDirectory=root,TargetExe=Path.Combine(root,"target","SonicRoute.exe"),Package=p,Version=version};
        plan.CacheDirectory=Path.Combine(root,"Updates","transactions",plan.Id);
        plan.Originals.Add(new InstallOriginal{Name="SonicRoute.exe",Exists=false});
        Directory.CreateDirectory(plan.CacheDirectory);
        UpdateInstaller.WritePlan(plan);
        var saved=UpdateInstaller.ReadPlan(plan.PlanPath);
        UpdateInstaller.ValidatePlan(saved,p.Edition,p.Architecture);
        UpdateInstaller.WriteOutcome(saved);
        using var result=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"Updates","last-result.json")));
        if(saved.Id!=plan.Id||result.RootElement.GetProperty("Id").GetString()!=plan.Id)throw new InvalidDataException("transaction JSON changed");
    }
}
