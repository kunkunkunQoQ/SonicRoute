using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;

internal static partial class Program
{
    static void RunEventIdleChecks(App app)
    {
        var option=Config.GetType().GetProperty("BackgroundPollingFallbackEnabled");
        bool enabled=Environment.GetEnvironmentVariable("SONICROUTE_PERIODIC_FALLBACK")=="1";
        option?.SetValue(Config,enabled);
        ForegroundAppService.BackgroundTestPid=-1; AudioService.BackgroundTestApps=null; GlobalMicMuteService.BackgroundTestRead=null;
        UMInvoke(app,"StartBackgroundMonitoring"); WaitTask(CurrentAppService.StartForegroundWatcher()); Pump(6500);
        var monitor=(MicrophoneStateMonitor)typeof(App).GetProperty("MicMonitor",PrivateInstance)!.GetValue(app)!;
        var foregroundTimer=(DispatcherTimer)SField(typeof(CurrentAppService),"_foregroundTimer")!;
        var micTimer=(DispatcherTimer)UMField(app,"_micMuteWatchTimer");
        var micCount=typeof(MicrophoneStateMonitor).GetField("EventTestReads",BindingFlags.Static|BindingFlags.Public)!;
        var pidCount=typeof(ForegroundAppService).GetField("EventTestPidReads",BindingFlags.Static|BindingFlags.Public)!;
        var health=typeof(MicrophoneStateMonitor).GetProperty("NeedsRecovery");
        Add(new{Case="native-idle-setup",PeriodicFallback=option==null||enabled,MicRecovery=health?.GetValue(monitor),ForegroundTimer=foregroundTimer.IsEnabled,MicTimer=micTimer.IsEnabled});
        using(var self=Process.GetCurrentProcess())
        for(int sample=0;sample<2;sample++)
        {
            int initial=ForegroundAppService.GetForegroundProcessId();
            int reads=(int)micCount.GetValue(null)!,pids=(int)pidCount.GetValue(null)!;
            self.Refresh(); var cpu=self.TotalProcessorTime; var sw=Stopwatch.StartNew(); Pump(12000); self.Refresh();
            var used=(self.TotalProcessorTime-cpu).TotalMilliseconds;
            int final=ForegroundAppService.GetForegroundProcessId();
            Add(new{Case="native-idle-metrics",Sample=sample,Seconds=sw.Elapsed.TotalSeconds,CpuMs=used,LogicalProcessors=Environment.ProcessorCount,
                WholeMachinePercent=used/sw.Elapsed.TotalMilliseconds/Environment.ProcessorCount*100,MicReads=(int)micCount.GetValue(null)!-reads,
                ForegroundPidReads=(int)pidCount.GetValue(null)!-pids-1,InitialForeground=initial,FinalForeground=final,
                ForegroundTimer=foregroundTimer.IsEnabled,MicTimer=micTimer.IsEnabled});
        }
        UMInvoke(app,"StopBackgroundMonitoring"); WaitTask(monitor.Completion);
    }
}
