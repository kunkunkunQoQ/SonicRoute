using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    static bool NewBackground => typeof(App).GetProperty("MicMonitor",PrivateInstance)!=null;
    static void BAssert(bool value,string name) { Add(new {Case="background-check", Name=name, Passed=value}); if(!value) throw new Exception(name); }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread,uint target,bool attach);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam);
    static readonly HashSet<IntPtr> OwnedWindows=new HashSet<IntPtr>();
    [DllImport("user32.dll")] static extern void NotifyWinEvent(uint ev,IntPtr hwnd,int objectId,int childId);
    static bool Focus(IntPtr hwnd)
    {
        var previous=GetForegroundWindow();
        if(OwnedWindows.Contains(previous))
        { SendMessage(previous,0x8001,hwnd,IntPtr.Zero); if(OwnedWindows.Contains(hwnd)) SendMessage(hwnd,0x8002,IntPtr.Zero,IntPtr.Zero); Pump(30); if(GetForegroundWindow()==hwnd) return true; }
        if(OwnedWindows.Contains(hwnd))
        { SendMessage(hwnd,0x8001,hwnd,IntPtr.Zero); SendMessage(hwnd,0x8002,IntPtr.Zero,IntPtr.Zero); Pump(30); if(GetForegroundWindow()==hwnd) return true; }
        uint fg=GetWindowThreadProcessId(GetForegroundWindow(),out _), own=GetCurrentThreadId();
        bool attached=fg!=0 && fg!=own && AttachThreadInput(own,fg,true);
        try { SetForegroundWindow(hwnd); Pump(30); return GetForegroundWindow()==hwnd; }
        finally { if(attached) AttachThreadInput(own,fg,false); }
    }
    static void WaitTask(Task task,int ms=7000)
    { var sw=Stopwatch.StartNew(); while(!task.IsCompleted && sw.ElapsedMilliseconds<ms) Pump(10); task.GetAwaiter().GetResult(); }
    static object? SField(Type type,string name) => type.GetField(name,PrivateStatic)!.GetValue(null);
    static void SSet(Type type,string name,object? value) => type.GetField(name,PrivateStatic)!.SetValue(null,value);

    static void RunBackgroundChecks(App app)
    {
        Config.DefaultAppMode="recent"; Config.DisabledAutoSwitchApps.Clear(); Config.Hotkeys.Clear();
        Config.GetType().GetProperty("BackgroundPollingFallbackEnabled")?.SetValue(Config,Environment.GetEnvironmentVariable("SONICROUTE_PERIODIC_FALLBACK")=="1");
        var initialForeground=GetForegroundWindow();
        var targets=new List<Process>();
        bool fastOnly=Environment.GetEnvironmentVariable("SONICROUTE_FAST_ONLY")=="1";
        try
        {
            string targetDir=Path.Combine(Environment.CurrentDirectory,"dist/v121-background-events/target/bin/Release/net8.0-windows");
            foreach(var name in new[]{"SRTargetA","SRTargetB"})
            {
                string exe=Path.Combine(targetDir,name+".exe"); File.Copy(Path.Combine(targetDir,"SRTestTarget.exe"),exe,true);
                var process=Process.Start(new ProcessStartInfo(exe) {UseShellExecute=false})!; targets.Add(process);
                for(int i=0;i<100;i++) { Pump(40); process.Refresh(); if(process.MainWindowHandle!=IntPtr.Zero) break; }
                BAssert(process.MainWindowHandle!=IntPtr.Zero,"owned-real-target-window-"+name);
                OwnedWindows.Add(process.MainWindowHandle);
            }
            Pump(1400);
            var audio=AudioService.GetApps(true);
            BAssert(targets.All(t=>audio.Any(a=>a.ProcessId==(uint)t.Id)),"both-owned-targets-have-real-silent-audio-sessions");
            ForegroundAppService.BackgroundTestPid=-1; AudioService.BackgroundTestApps=null;
            bool initialFocus=Focus(targets[0].MainWindowHandle);
            Add(new {Case="initial-focus",Succeeded=initialFocus,Foreground=ForegroundAppService.GetForegroundProcessId(),Target=targets[0].Id});
            WaitTask(CurrentAppService.StartForegroundWatcher());
            Add(new {Case="initial-selection",Foreground=ForegroundAppService.GetForegroundProcessId(),Selected=CurrentAppService.Current?.ProcessId});
            BAssert(CurrentAppService.Current?.ProcessId==(uint)targets[0].Id,"startup-captures-actual-foreground-target");
            if(Environment.GetEnvironmentVariable("SONICROUTE_REAL_UI")=="1")
            { RunCurrentUiTargets(targets); return; }
            var delays=new List<double>();
            using(var self=Process.GetCurrentProcess())
            {
                Pump(1800); self.Refresh(); var cpu=self.TotalProcessorTime; var wall=Stopwatch.StartNew();
                int attempts=0, excluded=0; double validCpu=0, validWall=0;
                for(int i=0;i<(fastOnly?0:12);i++)
                {
                    if(++attempts>30) throw new Exception("Foreground repeatedly interrupted by external application");
                    self.Refresh(); var trialCpu=self.TotalProcessorTime;
                    var target=targets[(i+1)%2]; var sw=Stopwatch.StartNew();
                    int stale=0; double latency=-1;
                    Action changed=()=>
                    {
                        var selected=CurrentAppService.Current?.ProcessId;
                        if(selected==(uint)target.Id && latency<0) latency=sw.Elapsed.TotalMilliseconds;
                        else if(selected!=(uint)targets[i%2].Id && selected!=(uint)target.Id) stale++;
                    };
                    CurrentAppService.CurrentChanged+=changed;
                    if(!Focus(target.MainWindowHandle))
                    { CurrentAppService.CurrentChanged-=changed; excluded++; i--; Pump(100); continue; }
                    // 单个长 Dispatcher frame；CPU 不混入每 10ms 查询当前应用的测试轮询。
                    if(CurrentAppService.Current?.ProcessId==(uint)target.Id) latency=sw.Elapsed.TotalMilliseconds;
                    Pump(Math.Max(1,2200-(int)sw.ElapsedMilliseconds));
                    CurrentAppService.CurrentChanged-=changed;
                    if(ForegroundAppService.GetForegroundProcessId()!=target.Id)
                    { Add(new {Case="excluded-external-focus-interference",Index=i}); excluded++; i--; continue; }
                    Add(new {Case="real-switch-observation",Index=i,Target=target.Id,ActualForeground=ForegroundAppService.GetForegroundProcessId(),Selected=CurrentAppService.Current?.ProcessId,Last=CurrentAppService.LastForegroundAudio?.ProcessId,ElapsedMs=latency,UnexpectedSelections=stale});
                    BAssert(CurrentAppService.Current?.ProcessId==(uint)target.Id && stale==0 && latency>=0,"accurate-current-after-real-switch-"+i);
                    delays.Add(latency);
                    self.Refresh(); validCpu+=(self.TotalProcessorTime-trialCpu).TotalMilliseconds; validWall+=sw.Elapsed.TotalMilliseconds;
                }                self.Refresh(); var used=(self.TotalProcessorTime-cpu).TotalMilliseconds;
                if(delays.Count>0) Add(new {Case="real-foreground-switch-metrics", Variant=NewBackground?"candidate":"baseline", Count=delays.Count,
                    LatencyMs=delays, MedianMs=delays.OrderBy(x=>x).ElementAt(delays.Count/2), MaxMs=delays.Max(), CpuMs=used,
                    Seconds=wall.Elapsed.TotalSeconds, OneCorePercent=used/wall.Elapsed.TotalMilliseconds*100,
                    ExcludedFocusInterference=excluded, ValidTrialCpuMs=validCpu, ValidTrialSeconds=validWall/1000, ValidOneCorePercent=validCpu/validWall*100 });
                self.Refresh(); cpu=self.TotalProcessorTime; wall.Restart();
                for(int i=0;i<20;i++) { bool focused=Focus(targets[i%2].MainWindowHandle); Add(new {Case="rapid-foreground-confirmation",Index=i,Expected=targets[i%2].Id,Actual=ForegroundAppService.GetForegroundProcessId()}); BAssert(focused,"rapid-os-switch-"+i); Pump(fastOnly?180:35); }
                Pump(1800);
                self.Refresh(); Add(new {Case="real-rapid-foreground-cpu",Variant=NewBackground?"candidate":"baseline",Switches=20,CpuMs=(self.TotalProcessorTime-cpu).TotalMilliseconds,Seconds=wall.Elapsed.TotalSeconds});
                BAssert(CurrentAppService.Current?.ProcessId==(uint)targets[1].Id,"rapid-switch-final-actual-target-accurate");
                self.Refresh(); cpu=self.TotalProcessorTime; wall.Restart(); Pump(fastOnly?4000:20000); self.Refresh();
                Add(new {Case="foreground-stable-cpu", Variant=NewBackground?"candidate":"baseline", CpuMs=(self.TotalProcessorTime-cpu).TotalMilliseconds, Seconds=wall.Elapsed.TotalSeconds});
            }
            typeof(CurrentAppService).GetMethod("StopForegroundWatcher")?.Invoke(null,null);
            if(!NewBackground) ((DispatcherTimer)SField(typeof(CurrentAppService),"_foregroundTimer")!).Stop();
            if(Environment.GetEnvironmentVariable("SONICROUTE_EVENT_FOREGROUND_ONLY")=="1") return;
            if(fastOnly) return;

            var factory=typeof(ProcessSnapshotService).GetMethod("ForTargets");
            using(var snapshot=factory!=null ? (ProcessSnapshotService)factory.Invoke(null,new object[]{targets.Select(t=>t.ProcessName).ToArray()})! : new ProcessSnapshotService())
            {
                snapshot.Capture(); var sw=Stopwatch.StartNew();
                for(int i=0;i<100;i++) BAssert(snapshot.Capture().IsSupersetOf(targets.Select(t=>t.ProcessName)),"multi-target-presence-"+i);
                Add(new {Case="multi-target-100-captures",Ms=sw.Elapsed.TotalMilliseconds});
            }
            RunSyntheticForegroundChecks(); RunMicLatency(app); RunWindowPauseCheck(); RunRuleChecks(app);
        }
        finally
        {
            typeof(CurrentAppService).GetMethod("StopForegroundWatcher")?.Invoke(null,null);
            AudioService.BackgroundTestApps=null; ForegroundAppService.BackgroundTestPid=-1; GlobalMicMuteService.BackgroundTestRead=null;
            if(initialForeground!=IntPtr.Zero) Focus(initialForeground);
            foreach(var target in targets) { if(!target.HasExited) target.Kill(); target.WaitForExit(3000); target.Dispose(); }
            OwnedWindows.Clear();
        }
    }

    static void RunCurrentUiTargets(List<Process> targets)
    {
        var window=new MainWindow {Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        int writes=SessionVolumeService.CloseTestWrites, routes=AudioService.CloseTestRouteWrites;
        try
        {
            window.Show(); Pump(900);
            var combo=(System.Windows.Controls.ComboBox)window.FindName("OverviewAppCombo");
            for(int i=0;i<6;i++)
            {
                var target=targets[(i+1)%2]; BAssert(Focus(target.MainWindowHandle),"ui-real-foreground-switch-"+i);
                Pump(600);
                BAssert(CurrentAppService.Current?.ProcessId==(uint)target.Id
                    && (UMField(window,"_overviewApp") as AudioAppInfo)?.ProcessId==(uint)target.Id
                    && (combo.SelectedItem as AppItem)?.Info.ProcessId==(uint)target.Id,"main-overview-shows-actual-foreground-target-"+i);
            }
            BAssert(SessionVolumeService.CloseTestWrites==writes && AudioService.CloseTestRouteWrites==routes,"following-and-painting-ui-does-not-write-audio");
        }
        finally {window.Close();}
    }

#if BACKGROUND_CANDIDATE
    static void RunSyntheticForegroundChecks()
    {
        if(!NewBackground) return;
        var query=typeof(CurrentAppService).GetMethod("RequestForegroundAsync",PrivateStatic)!;
        int count=0; var gate=new ManualResetEventSlim(false); var entered=new ManualResetEventSlim(false);
        ForegroundAppService.BackgroundTestPid=1000001;
        AudioService.BackgroundTestApps=()=> { Interlocked.Increment(ref count); entered.Set(); gate.Wait(); return new List<AudioAppInfo>{new AudioAppInfo{ProcessId=1000001,ProcessName="old"},new AudioAppInfo{ProcessId=1000003,ProcessName="latest"}}; };
        var warmup=CurrentAppService.StartForegroundWatcher(); BAssert(entered.Wait(2000),"controlled-query-started");
        for(int pid=1000002;pid<1000100;pid++) { ForegroundAppService.BackgroundTestPid=pid; query.Invoke(null,null); }
        ForegroundAppService.BackgroundTestPid=1000003; query.Invoke(null,null); gate.Set(); WaitTask(warmup);
        BAssert(CurrentAppService.Current?.ProcessId==1000003 && count==2,"latest-only-coalescing-no-stale-publication");
        AudioService.BackgroundTestApps=()=>new List<AudioAppInfo>(); ForegroundAppService.BackgroundTestPid=1000004;
        WaitTask((Task)query.Invoke(null,null)!);
        AudioService.BackgroundTestApps=()=>new List<AudioAppInfo>{new AudioAppInfo {ProcessId=1000004,ProcessName="late-audio"}};
        Pump(1700); BAssert(CurrentAppService.Current?.ProcessId==1000004,"same-foreground-late-audio-retries");
        Action brokenSubscriber=()=>throw new InvalidOperationException("Controlled subscriber failure");
        CurrentAppService.CurrentChanged+=brokenSubscriber;
        ForegroundAppService.BackgroundTestPid=1000005;
        AudioService.BackgroundTestApps=()=>new List<AudioAppInfo>{new AudioAppInfo{ProcessId=1000005,ProcessName="subscriber-failure"}};
        var resilient=(Task)query.Invoke(null,null)!; WaitTask(resilient);
        BAssert(resilient.Status==TaskStatus.RanToCompletion && CurrentAppService.Current?.ProcessId==1000005,"subscriber-failure-does-not-break-foreground-worker");
        CurrentAppService.CurrentChanged-=brokenSubscriber;
        typeof(CurrentAppService).GetMethod("StopForegroundWatcher")!.Invoke(null,null); AudioService.BackgroundTestApps=null; ForegroundAppService.BackgroundTestPid=-1;
        gate.Dispose(); entered.Dispose();
    }

#else
    static void RunSyntheticForegroundChecks() { }
#endif
    static void RunMicLatency(App app)
    {
        bool mute=false; GlobalMicMuteService.BackgroundTestRead=_=>mute;
        typeof(App).GetMethod("StartBackgroundMonitoring",PrivateInstance)!.Invoke(app,null);
        Pump(2200); var delays=new List<double>();
        for(int i=0;i<4;i++)
        {
            Pump(250+i*130); mute=!mute; var sw=Stopwatch.StartNew();
            if(NewBackground)
            {
                var monitor=typeof(App).GetProperty("MicMonitor",PrivateInstance)!.GetValue(app)!;
                var callback=monitor.GetType().GetField("_volumeCallback",PrivateInstance)!.GetValue(monitor)!;
                callback.GetType().GetMethod("OnNotify")!.Invoke(callback,new object[]{IntPtr.Zero});
            }
            while((bool)UMField(app,"_lastMicMutedBaseline")!=mute && sw.ElapsedMilliseconds<2200) Pump(10);
            BAssert((bool)UMField(app,"_lastMicMutedBaseline")==mute,"controlled-mic-state-change-"+i); delays.Add(sw.Elapsed.TotalMilliseconds);
        }
        Add(new {Case="controlled-mic-latency",Variant=NewBackground?"candidate":"baseline",Ms=delays,Note="Controlled read state and callback; no hardware mute changed."});
        if(NewBackground) typeof(App).GetMethod("StopBackgroundMonitoring",PrivateInstance)!.Invoke(app,null);
        else ((DispatcherTimer)UMField(app,"_micMuteWatchTimer")).Stop();
        GlobalMicMuteService.BackgroundTestRead=null;
    }
    static void RunWindowPauseCheck()
    {
        if(!NewBackground) return;
        var window=new MainWindow{Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false}; window.Show(); Pump(400);
        ((FrameworkElement)window.FindName("OverviewPage")).Visibility=Visibility.Collapsed;
        ((FrameworkElement)window.FindName("AutomationPage")).Visibility=Visibility.Visible;
        var draft=new List<AutoRuleStep>{new AutoRuleStep{TargetApp="unsaved-fixture"}};
        UMSet(window,"_autoSteps",draft); UMSet(window,"_autoEditingId","unsaved-id");
        ((System.Windows.Controls.TextBox)window.FindName("AutoNameBox")).Text="unsaved-name";
        UMInvoke(window,"StartAutoRefresh"); BAssert(UMField(window,"_autoRefreshTimer")!=null,"visible-automation-refresh-enabled");
        window.WindowState=WindowState.Minimized; Pump(50); BAssert(UMField(window,"_autoRefreshTimer")==null,"minimized-candidate-refresh-paused");
        window.WindowState=WindowState.Normal; Pump(200); BAssert(UMField(window,"_autoRefreshTimer")!=null,"restore-candidate-refresh-resumed");
        window.Hide(); BAssert(UMField(window,"_autoRefreshTimer")==null,"hidden-candidate-refresh-paused");
        BAssert(ReferenceEquals(UMField(window,"_autoSteps"),draft) && draft[0].TargetApp=="unsaved-fixture"
            && (string)UMField(window,"_autoEditingId")=="unsaved-id"
            && ((System.Windows.Controls.TextBox)window.FindName("AutoNameBox")).Text=="unsaved-name","minimize-restore-hide-preserves-unsaved-draft");
        window.Close();
    }
#if BACKGROUND_CANDIDATE
    static void RunRuleChecks(App app)
    {
        if(!NewBackground) return;
        AutoRuleStore.Invalidate(); AutoRuleStore.LoadAll();
        int changes=0; Action changed=()=>Interlocked.Increment(ref changes); AutoRuleStore.Changed+=changed;
        Directory.CreateDirectory(AutoRuleStore.RulesDir);
        string path=Path.Combine(AutoRuleStore.RulesDir,"external.json");
        File.WriteAllText(path,"{\"Id\":\"external-test\",\"Name\":\"External\",\"Enabled\":true,\"Trigger\":1,\"TriggerApp\":\"owned-test\"}");
        Pump(500); AutoRuleStore.CheckForExternalChanges(); Pump(250);
        BAssert(changes>0 && AutoRuleStore.Find("external-test")!=null,"external-first-rule-notifies-inactive-consumers");
        long revision=AutoRuleStore.ReadSnapshot().Revision;
        BAssert(AutoRuleStore.ReadSnapshot(revision).Rules==null,"unchanged-revision-does-not-clone");
        File.Delete(path); Pump(400); BAssert(AutoRuleStore.Find("external-test")==null,"external-rule-removal-invalidates");
        AutoRuleStore.Changed-=changed;
    }
#else
    static void RunRuleChecks(App app) { }
#endif
}
