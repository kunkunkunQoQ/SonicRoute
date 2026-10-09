using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Models;

internal static partial class Program
{
    static void RunBackgroundUnitChecks(App app)
    {
        using(var mouse=new MouseHotkeyHook())
        {
            BAssert(!mouse.IsInstalled,"mouse-hook-absent-with-no-bindings");
            mouse.Reload(new[]{new KeyValuePair<string,string>("Ctrl+XButton1","first")});
            BAssert(mouse.IsInstalled,"mouse-hook-installs-on-demand");
            var callback=typeof(MouseHotkeyHook).GetMethod("MouseProc",PrivateInstance)!;
            // 未识别消息在读取 lParam 前过滤：空地址有效验证早期返回。
            callback.Invoke(mouse,new object[]{0,new IntPtr(0x200),IntPtr.Zero});
            BAssert(true,"mouse-move-rejected-before-structure-read");
            var matches=typeof(MouseHotkeyHook).GetMethod("ComboMatches",PrivateStatic)!;
            BAssert((bool)matches.Invoke(null,new object[]{" Ctrl + XButton1 ",(uint)2,"XButton1"})!
                && !(bool)matches.Invoke(null,new object[]{"Ctrl+XButton1",(uint)6,"XButton1"})!,"mouse-modifiers-remain-exact");
            mouse.Reload(Array.Empty<KeyValuePair<string,string>>());
            BAssert(!mouse.IsInstalled,"mouse-hook-uninstalls-after-last-binding");
            mouse.Dispose(); mouse.Reload(new[]{new KeyValuePair<string,string>("WheelUp","test")});
            BAssert(!mouse.IsInstalled,"disposed-mouse-hook-cannot-reinstall");
        }
        RunSyntheticForegroundChecks();
        RunEventOnlyForegroundChecks();
        // 注册失败时临时检查，成功重新注册后停止。
        var sourceType=typeof(CurrentAppService).Assembly.GetType("SonicRoute.ForegroundChangeSource")!;
        SSet(sourceType,"EventTestFailRegistration",true);
        ForegroundAppService.BackgroundTestPid=1100001;
        AudioService.BackgroundTestApps=()=>new List<AudioAppInfo>{new AudioAppInfo{ProcessId=1100001,ProcessName="fallback-a"},new AudioAppInfo{ProcessId=1100002,ProcessName="fallback-b"}};
        Config.DefaultAppMode="recent";
        WaitTask(CurrentAppService.StartForegroundWatcher());
        var source=SField(typeof(CurrentAppService),"_foregroundSource")!;
        BAssert(!(bool)source.GetType().GetProperty("IsInstalled",PrivateInstance)!.GetValue(source)! && ForegroundTimerActive(),"foreground-registration-failure-enables-temporary-checks");
        ForegroundAppService.BackgroundTestPid=1100002;
        Pump(1750); BAssert(CurrentAppService.Current?.ProcessId==1100002,"temporary-checks-follow-target-while-registration-fails");
        SSet(sourceType,"EventTestFailRegistration",false); Pump(1750);
        BAssert((bool)source.GetType().GetProperty("IsInstalled",PrivateInstance)!.GetValue(source)! && !ForegroundTimerActive(),"foreground-reregistration-succeeds-and-stops-checks");
        CurrentAppService.StopForegroundWatcher();
        int reads=0; ForegroundAppService.BackgroundTestPid=1100003;
        AudioService.BackgroundTestApps=()=> { reads++; return new List<AudioAppInfo>(); };
        WaitTask(CurrentAppService.StartForegroundWatcher()); Pump(6500);
        BAssert(reads==3 && !ForegroundTimerActive(),"no-audio-foreground-retries-are-bounded-and-timer-stops"); CurrentAppService.StopForegroundWatcher();
        AudioService.BackgroundTestApps=null; ForegroundAppService.BackgroundTestPid=-1;
        RunWindowPauseCheck(); RunRuleChecks(app);
        string moved=AutoRuleStore.RulesDir+"-retired-"+Guid.NewGuid().ToString("N");
        Directory.Move(AutoRuleStore.RulesDir,moved); Directory.CreateDirectory(AutoRuleStore.RulesDir);
        File.WriteAllText(Path.Combine(AutoRuleStore.RulesDir,"recreated.json"),"{\"Id\":\"recreated\",\"Name\":\"recreated\"}");
        AutoRuleStore.CheckForExternalChanges();
        BAssert(AutoRuleStore.Find("recreated")!=null,"deleted-and-recreated-rule-directory-recovers");
        BAssert(AutoRuleStore.Find("external-test")==null,"recreated-directory-does-not-retain-old-rules");
        var rulesHandler=(Action)Delegate.CreateDelegate(typeof(Action),app,typeof(App).GetMethod("RulesChanged",PrivateInstance)!);
        AutoRuleStore.Changed+=rulesHandler;
        AutoRuleService.RefreshWatcher();
        BAssert(SField(typeof(AutoRuleService),"_watchTimer")==null,"no-app-rules-keeps-watcher-inactive");
        string wakeFile=Path.Combine(AutoRuleStore.RulesDir,"wake.json");
        File.WriteAllText(wakeFile,"{\"Id\":\"wake\",\"Name\":\"wake\",\"Trigger\":1,\"TriggerApp\":\"nonexistent-owned-fixture\",\"Actions\":[]}");
        Pump(650);
        BAssert(SField(typeof(AutoRuleService),"_watchTimer")!=null,"external-first-app-rule-wakes-real-inactive-watcher");
        File.Delete(wakeFile); Pump(650);
        BAssert(SField(typeof(AutoRuleService),"_watchTimer")==null,"external-last-app-rule-removal-stops-watcher");
        string future=DateTime.Now.AddHours(2).ToString("HH:mm");
        var schedule=new AutoRule {Id="future",Name="future",Trigger=AutoRuleTrigger.Schedule,ScheduleMode=1,ScheduleTime=future,LastRunKey="preserve-record"};
        AutoRuleStore.Save(schedule); Pump(650);
        BAssert(SField(typeof(AutoRuleScheduler),"_timer")!=null && AutoRuleStore.Find("future")!.LastRunKey=="preserve-record","rule-notification-wakes-scheduler-and-preserves-last-run-key");
        AutoRuleStore.Delete("future"); Pump(650);
        BAssert(SField(typeof(AutoRuleScheduler),"_timer")==null,"removing-last-schedule-stops-scheduler");
        AutoRuleStore.Changed-=rulesHandler; AutoRuleScheduler.Shutdown();
        RunOwnedProcessChecks();
        RunEventOnlyMicChecks(app);
        AutoRuleStore.Shutdown();
    }

    static bool ForegroundTimerActive() => (SField(typeof(CurrentAppService),"_foregroundTimer") as System.Windows.Threading.DispatcherTimer)?.IsEnabled==true;

    static void RunEventOnlyForegroundChecks()
    {
        int reads=0;
        ForegroundAppService.BackgroundTestPid=1200001;
        AudioService.BackgroundTestApps=()=> { reads++; return new List<AudioAppInfo>{new AudioAppInfo{ProcessId=1200001,ProcessName="healthy"}}; };
        WaitTask(CurrentAppService.StartForegroundWatcher());
        BAssert(!ForegroundTimerActive(),"healthy-foreground-has-no-periodic-timer");
        int before=reads; Pump(3600);
        BAssert(reads==before,"healthy-foreground-idle-does-not-query-audio");
        CurrentAppService.StopForegroundWatcher();
        ForegroundAppService.BackgroundTestPid=1200002;
        AudioService.BackgroundTestApps=()=>throw new InvalidOperationException("Controlled query failure");
        WaitTask(CurrentAppService.StartForegroundWatcher());
        BAssert(ForegroundTimerActive(),"foreground-query-failure-enables-recovery");
        AudioService.BackgroundTestApps=()=>new List<AudioAppInfo>{new AudioAppInfo{ProcessId=1200002,ProcessName="recovered"}};
        Pump(1750);
        BAssert(CurrentAppService.Current?.ProcessId==1200002 && !ForegroundTimerActive(),"same-pid-query-recovers-and-stops-checking");
        CurrentAppService.StopForegroundWatcher();
        ForegroundAppService.BackgroundTestPid=1200003; reads=0;
        AudioService.BackgroundTestApps=()=> {reads++; return new List<AudioAppInfo>();};
        WaitTask(CurrentAppService.StartForegroundWatcher()); BAssert(ForegroundTimerActive(),"no-audio-foreground-arms-short-retry");
        ForegroundAppService.BackgroundTestPid=Process.GetCurrentProcess().Id;
        WaitTask((System.Threading.Tasks.Task)typeof(CurrentAppService).GetMethod("RequestForegroundAsync",PrivateStatic)!.Invoke(null,null)!);
        before=reads; Pump(1750);
        BAssert(reads==before && !ForegroundTimerActive(),"own-foreground-cancels-audio-retries");
        CurrentAppService.StopForegroundWatcher();
        AudioService.BackgroundTestApps=null; ForegroundAppService.BackgroundTestPid=-1;
    }

    static void RunEventOnlyMicChecks(App app)
    {
        BAssert(!new AppConfig().BackgroundPollingFallbackEnabled
            && !System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{}")!.BackgroundPollingFallbackEnabled,"periodic-fallback-default-and-old-config-are-off");
        UMInvoke(app,"StartBackgroundMonitoring"); Pump(700);
        var monitor=(MicrophoneStateMonitor)typeof(App).GetProperty("MicMonitor",PrivateInstance)!.GetValue(app)!;
        bool TimerActive() => ((System.Windows.Threading.DispatcherTimer)UMField(app,"_micMuteWatchTimer")).IsEnabled;
        BAssert(!monitor.NeedsRecovery && !TimerActive(),"native-mic-registration-disables-recovery-timer");
        BAssert(((System.Windows.Threading.DispatcherTimer)UMField(app,"_ruleProbeTimer")).IsEnabled,"rule-directory-recovery-retains-separate-timer");
        UMInvoke(app,"StartBackgroundMonitoring");
        BAssert(ReferenceEquals(monitor,typeof(App).GetProperty("MicMonitor",PrivateInstance)!.GetValue(app)),"monitor-start-is-idempotent");
        int before=MicrophoneStateMonitor.EventTestReads; Pump(4600);
        BAssert(MicrophoneStateMonitor.EventTestReads==before && !TimerActive(),"healthy-mic-idle-has-no-periodic-reads");
        bool muted=true; GlobalMicMuteService.BackgroundTestRead=_=>muted;
        UMField(monitor,"_volumeCallback").GetType().GetMethod("OnNotify")!.Invoke(UMField(monitor,"_volumeCallback"),new object[]{IntPtr.Zero}); Pump(150);
        BAssert((bool)UMField(app,"_lastMicMutedBaseline") && !TimerActive(),"mic-callback-updates-without-periodic-checks");
        GlobalMicMuteService.BackgroundTestRead=track=>track;
        Config.MicMuteOsdTrackInputMuted=false; UMInvoke(app,"RefreshMicMonitoringState"); Pump(150);
        Config.MicMuteOsdTrackInputMuted=true; app.GetType().GetMethod("NotifyMicMuteOsdSettingChanged",PrivateInstance)!.Invoke(app,new object[]{false}); Pump(150);
        BAssert((bool)UMField(app,"_lastMicMutedBaseline"),"mic-policy-change-refreshes-without-volume-event");
        GlobalMicMuteService.BackgroundTestRead=null; Config.MicMuteOsdTrackInputMuted=false;
        MicrophoneStateMonitor.EventTestFailDeviceRegistration=true; monitor.RequestRebuild(); Pump(500);
        BAssert(monitor.NeedsRecovery && TimerActive(),"failed-device-notification-registration-enables-mic-recovery");
        MicrophoneStateMonitor.EventTestFailDeviceRegistration=false; Pump(2300);
        BAssert(!monitor.NeedsRecovery && !TimerActive() && (bool)UMField(monitor,"_deviceRegistered"),"device-reregistration-recovers-and-stops-mic-timer");
        MicrophoneStateMonitor.EventTestFailVolumeRegistration=true; monitor.RequestRebuild(); Pump(500);
        BAssert(monitor.NeedsRecovery && TimerActive(),"partial-volume-registration-keeps-recovery-active");
        MicrophoneStateMonitor.EventTestFailVolumeRegistration=false; Pump(2300);
        BAssert(!monitor.NeedsRecovery && !TimerActive(),"volume-reregistration-recovers-and-stops-checks");
        MicrophoneStateMonitor.EventTestFailReads=true; monitor.RequestRebuild(); Pump(700);
        BAssert(monitor.NeedsRecovery && TimerActive(),"com-read-failure-enables-temporary-mic-checks");
        before=MicrophoneStateMonitor.EventTestReads; Pump(2300);
        BAssert(MicrophoneStateMonitor.EventTestReads-before<=2,"persistent-com-failure-does-not-create-immediate-read-loop");
        MicrophoneStateMonitor.EventTestFailReads=false; Pump(2300);
        BAssert(!monitor.NeedsRecovery && !TimerActive(),"com-read-recovery-stops-checks");
        before=MicrophoneStateMonitor.EventTestReads; Pump(4100);
        BAssert(MicrophoneStateMonitor.EventTestReads==before,"recovered-mic-returns-to-zero-idle-reads");
        ForegroundAppService.BackgroundTestPid=1300001;
        AudioService.BackgroundTestApps=()=>new List<AudioAppInfo>{new AudioAppInfo{ProcessId=1300001,ProcessName="resume"}};
        WaitTask(CurrentAppService.StartForegroundWatcher());
        RunFallbackSettingChecks(app,monitor);
        var previous=SField(typeof(CurrentAppService),"_foregroundSource")!;
        UMInvoke(app,"RecoverBackgroundMonitoring"); Pump(400);
        BAssert(!ReferenceEquals(previous,SField(typeof(CurrentAppService),"_foregroundSource")) && !(bool)previous.GetType().GetProperty("IsInstalled",PrivateInstance)!.GetValue(previous)!,"resume-replaces-and-unhooks-foreground-registration");
        BAssert(!monitor.NeedsRecovery && !TimerActive() && !ForegroundTimerActive(),"resume-rebuild-finishes-without-polling");
        UMInvoke(app,"StopBackgroundMonitoring"); WaitTask(monitor.Completion);
        before=MicrophoneStateMonitor.EventTestReads;
        UMField(monitor,"_volumeCallback").GetType().GetMethod("OnNotify")!.Invoke(UMField(monitor,"_volumeCallback"),new object[]{IntPtr.Zero}); Pump(250);
        BAssert(MicrophoneStateMonitor.EventTestReads==before && !TimerActive() && !ForegroundTimerActive(),"shutdown-cancels-timers-and-ignores-late-callback");
        AudioService.BackgroundTestApps=null; ForegroundAppService.BackgroundTestPid=-1;
    }

    static void RunFallbackSettingChecks(App app, MicrophoneStateMonitor monitor)
    {
        var window=new MainWindow{Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        window.Show(); Pump(300);
        UMInvoke(window,"LoadExperimentalSettings");
        var check=(System.Windows.Controls.CheckBox)window.FindName("ExpBackgroundPollingCheck");
        BAssert(check.IsChecked==false,"experimental-fallback-switch-initially-off");
        check.IsChecked=true; Pump(450);
        BAssert(Config.BackgroundPollingFallbackEnabled && ForegroundTimerActive()
            && ((System.Windows.Threading.DispatcherTimer)UMField(app,"_micMuteWatchTimer")).IsEnabled,"switch-on-immediately-restores-both-periodic-checks");
        BAssert(((System.Windows.Threading.DispatcherTimer)SField(typeof(CurrentAppService),"_foregroundTimer")!).Interval.TotalMilliseconds==1500
            && ((System.Windows.Threading.DispatcherTimer)UMField(app,"_micMuteWatchTimer")).Interval.TotalMilliseconds==2000,"original-periodic-check-intervals-retained");
        var stored=System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigService.ConfigPath))!;
        BAssert(stored.BackgroundPollingFallbackEnabled,"fallback-switch-is-persisted");
        int before=MicrophoneStateMonitor.EventTestReads; Pump(4200);
        BAssert(MicrophoneStateMonitor.EventTestReads-before>=2,"opt-in-fallback-continues-periodic-microphone-reads");
        window.Close();
        window=new MainWindow{Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false}; window.Show(); Pump(300);
        int saves=ConfigService.CloseTestSaves; UMInvoke(window,"LoadExperimentalSettings");
        check=(System.Windows.Controls.CheckBox)window.FindName("ExpBackgroundPollingCheck");
        BAssert(check.IsChecked==true && ConfigService.CloseTestSaves==saves,"reopened-experimental-page-restores-switch-without-saving-on-load");
        ((System.Windows.FrameworkElement)window.FindName("OverviewPage")).Visibility=System.Windows.Visibility.Collapsed;
        ((System.Windows.FrameworkElement)window.FindName("ExperimentalPage")).Visibility=System.Windows.Visibility.Visible;
        window.UpdateLayout();
        var content=(System.Windows.Controls.StackPanel)check.Content;
        var hint=(System.Windows.Controls.TextBlock)content.Children[1];
        BAssert(check.ActualWidth>200 && hint.ActualHeight>0 && !hint.Text.StartsWith("Exp."),"fallback-setting-row-and-localized-hint-layout");
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(window); var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using(var output=File.Create(Path.Combine(Output,"experimental-setting.png"))) encoder.Save(output);
        check.IsChecked=false; Pump(450);
        BAssert(!Config.BackgroundPollingFallbackEnabled && !ForegroundTimerActive()
            && !((System.Windows.Threading.DispatcherTimer)UMField(app,"_micMuteWatchTimer")).IsEnabled,"switch-off-immediately-restores-event-only-healthy-mode");
        before=MicrophoneStateMonitor.EventTestReads; Pump(2300);
        BAssert(MicrophoneStateMonitor.EventTestReads==before,"switch-off-removes-periodic-microphone-reads");
        window.Close();
        stored=System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigService.ConfigPath))!;
        BAssert(!stored.BackgroundPollingFallbackEnabled,"disabled-fallback-persists-after-window-close");
    }

    static void RunOwnedProcessChecks()
    {
        string folder=Path.Combine(Environment.CurrentDirectory,"dist/v121-background-events/process-target/bin/Release/net8.0");
        var owned=new List<Process>();
        string nameA="SRUnitA"+Process.GetCurrentProcess().Id, nameB="SRUnitB"+Process.GetCurrentProcess().Id;
        Process Start(string name)
        {
            string exe=Path.Combine(folder,name+".exe"); if(!File.Exists(exe)) File.Copy(Path.Combine(folder,"ProcessTarget.exe"),exe);
            var child=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden})!;
            owned.Add(child); Pump(150); return child;
        }
        try
        {
            var a=Start(nameA); var a2=Start(nameA); var b=Start(nameB);
            using(var snapshot=ProcessSnapshotService.ForTargets(new[]{nameA,nameB}))
            {
                BAssert(snapshot.Capture().Count==2,"multi-specific-targets-initial-presence");
                a.Kill(); a.WaitForExit();
                BAssert(snapshot.Capture().Contains(nameA),"exited-known-instance-with-other-instance-does-not-report-exit");
                a2.Kill(); a2.WaitForExit();
                BAssert(!snapshot.Capture().Contains(nameA),"last-instance-exit-detected");
                Start(nameA); BAssert(snapshot.Capture().Count==2,"missing-target-start-detected-on-next-snapshot");
                snapshot.Dispose(); bool rejected=false;
                try { snapshot.Capture(); } catch(ObjectDisposedException) { rejected=true; }
                BAssert(rejected,"disposed-process-snapshot-rejects-reads");
            }
            using(var full=ProcessSnapshotService.ForTargets(Enumerable.Range(0,33).Select(i=>"missing"+i)))
                BAssert(full.Capture().Contains(Process.GetCurrentProcess().ProcessName),"large-target-set-falls-back-to-full-snapshot");
        }
        finally { foreach(var child in owned) { if(!child.HasExited) child.Kill(); child.WaitForExit(); child.Dispose(); } }
    }
}
