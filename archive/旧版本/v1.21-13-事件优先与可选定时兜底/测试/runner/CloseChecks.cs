using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.Core;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        internal uint Size, PageFaults;
        internal UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
        internal UIntPtr Pagefile, PeakPagefile, PrivateBytes, PrivateWorkingSet;
        internal ulong SharedCommit;
    }
    [DllImport("psapi.dll", SetLastError=true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);

    private static void CloseAssert(bool success, string name) => Add(new { Case="close-check", Name=name, Passed=success });
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, PrivateInstance)?.GetValue(owner);
    private static void SetField(object owner, string name, object? value) => owner.GetType().GetField(name, PrivateInstance)!.SetValue(owner, value);
    private static object? Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, PrivateInstance)!.Invoke(owner,args);
    private static int EventCount(object owner, string name) => (Field(owner,name) as Delegate)?.GetInvocationList().Length ?? 0;
    private static Window MakeWindow(string kind) => kind == "main" ? new MainWindow() : kind == "modern" ? new QuickPanelModernWindow() : new QuickPanelWindow();
    private static void ShowOffscreen(Window window)
    {
        window.ShowActivated=false; window.ShowInTaskbar=false;
        window.WindowStartupLocation=WindowStartupLocation.Manual; window.Left=-10000; window.Top=-10000;
        window.Show();
    }
    private static void Memory(string label)
    {
        using var p=Process.GetCurrentProcess(); p.Refresh();
        var native=new MemoryCounters { Size=(uint)Marshal.SizeOf(typeof(MemoryCounters)) };
        bool ok=GetProcessMemoryInfo(p.Handle,ref native,native.Size);
        Add(new { Case="memory", Label=label, Seconds=Clock.Elapsed.TotalSeconds,
            PrivateMiB=p.PrivateMemorySize64/1048576.0, WorkingSetMiB=p.WorkingSet64/1048576.0,
            PrivateWorkingSetMiB=ok ? native.PrivateWorkingSet.ToUInt64()/1048576.0 : -1,
            ManagedMiB=GC.GetTotalMemory(false)/1048576.0,
            Gen0=GC.CollectionCount(0), Gen1=GC.CollectionCount(1), Gen2=GC.CollectionCount(2),
            Handles=p.HandleCount, Threads=p.Threads.Count, CpuMs=p.TotalProcessorTime.TotalMilliseconds });
        Save();
    }
    // This diagnostic collection is confined to the test driver, never to the resident scenario or product.
    private static void DiagnoseCollection()
    {
        Pump(250); GC.Collect(); GC.WaitForPendingFinalizers(); Pump(250);
        GC.Collect(); GC.WaitForPendingFinalizers(); Pump(100);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CloseOne(string kind, bool adjust, bool immediate)
    {
        var w=MakeWindow(kind); ShowOffscreen(w);
        if(kind!="main") Call(w,"LoadAsync");
        if(!immediate) Pump(600);
        if(Environment.GetEnvironmentVariable("SONICROUTE_CLOSE_DELAY")=="1")
        {
            Pump(50);
            CloseAssert(((ManualResetEventSlim)typeof(AudioService).GetField("CloseTestEntered")!.GetValue(null)!).Wait(2000),"blocked-read-really-started");
        }
        if(adjust)
        {
            Call(w,"SubscribeOsdAdjust"); Call(w,"SubscribePanelPosAdjust");
        }
        var weak=new WeakReference(w); w.Close(); return weak;
    }

    private static void RunCloseChecks()
    {
        Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data"));
        Config=new AppConfig { Language="zh-CN", Hotkeys=new Dictionary<string,string>(), ShowAppPeakMeter=false };
        typeof(ConfigService).GetField("_cache",PrivateStatic)!.SetValue(null,Config);
        var app=(App)Application.Current;
        var tray=Activator.CreateInstance(typeof(App).Assembly.GetType("SonicRoute.TrayWheelService")!,new object?[]{null})!;
        SetField(app,"_trayWheel",tray);
        var osd=Field(tray,"_osdService")!;
        CloseAssert(osd!=null,"real-osd-event-publisher-created");
        int osdInitial=EventCount(osd,"AdjustFinished"), panelInitial=EventCount(app,"QuickPanelAdjustFinished");
        var main=new MainWindow(); ShowOffscreen(main); Pump(500);
        Call(main,"SubscribeOsdAdjust"); Call(main,"SubscribePanelPosAdjust");
        Call(main,"SubscribeOsdAdjust"); Call(main,"SubscribePanelPosAdjust");
        CloseAssert(EventCount(osd,"AdjustFinished")==osdInitial+1,"osd-real-subscription-once");
        CloseAssert(EventCount(app,"QuickPanelAdjustFinished")==panelInitial+1,"panel-real-subscription-once");
        SetField(main,"_osdAdjusting",true); SetField(main,"_panelPosAdjusting",true);
        (Field(osd,"AdjustFinished") as Action)?.Invoke(); Call(app,"NotifyQuickPanelAdjustFinished");
        CloseAssert(!(bool)Field(main,"_osdAdjusting")! && !(bool)Field(main,"_panelPosAdjusting")!,"completion-resets-adjust-buttons");
        SetField(main,"_osdAdjusting",true); SetField(main,"_panelPosAdjusting",true);
        int savesBeforeClose=(int)typeof(ConfigService).GetField("CloseTestSaves")!.GetValue(null)!;
        main.Close();
        CloseAssert((int)typeof(ConfigService).GetField("CloseTestSaves")!.GetValue(null)! == savesBeforeClose,"closing-without-edits-does-not-save-config");
        CloseAssert(EventCount(osd,"AdjustFinished")==osdInitial,"closed-unsubscribes-real-osd-event");
        CloseAssert(EventCount(app,"QuickPanelAdjustFinished")==panelInitial,"closed-unsubscribes-panel-event");
        CloseAssert(!(bool)Field(main,"_osdAdjusting")! && !(bool)Field(main,"_panelPosAdjusting")!,"closing-active-adjust-mode-clears-state");
        foreach(string name in new[]{"OverviewAppCombo","AppsListBox","FixedAppCombo","AutoRuleList"})
            CloseAssert(((ItemsControl)main.FindName(name)).Items.Count==0,"main-detaches-"+name);
        var queued=new MainWindow();
        var scope=Field(queued,"_uiLifetime");
        if(scope!=null)
        {
            bool ran=false;
            scope.GetType().GetMethod("Post",PrivateInstance)!.Invoke(scope,new object[]{queued.Dispatcher,(Action)(()=>ran=true),DispatcherPriority.Background});
            queued.Close(); Pump(60); CloseAssert(!ran,"close-aborts-queued-ui-callback");
        }
        foreach(string kind in new[]{"main","modern","classic"})
        {
            var w=MakeWindow(kind); ShowOffscreen(w); if(kind!="main") Call(w,"LoadAsync"); Pump(600);
            w.Close();
            string name=kind=="modern"?"AppListPanel":kind=="classic"?"AppCombo":"OverviewAppCombo";
            CloseAssert(((ItemsControl)w.FindName(name)).Items.Count==0,kind+"-clears-dynamic-items");
            if(w.GetType().GetMethod("CleanupClosedWindow",PrivateInstance)!=null)
            {
                Call(w,"CleanupClosedWindow"); CloseAssert(true,kind+"-cleanup-idempotent");
            }
        }
        // Keep the long-lived publisher alive while checking the old window.
        foreach(string kind in new[]{"main","modern","classic"})
        foreach(bool immediate in new[]{false,true})
        {
            var weak=CloseOne(kind,kind=="main",immediate); DiagnoseCollection();
            CloseAssert(!weak.IsAlive,kind+(immediate?"-immediate":"-loaded")+"-window-collectible");
        }
        // A blocked read must no longer retain the closed main window while COM is still running.
        var release=(ManualResetEventSlim)typeof(AudioService).GetField("CloseTestRelease")!.GetValue(null)!;
        var entered=(ManualResetEventSlim)typeof(AudioService).GetField("CloseTestEntered")!.GetValue(null)!;
        release.Reset(); entered.Reset(); Environment.SetEnvironmentVariable("SONICROUTE_CLOSE_DELAY","1");
        var blocked=CloseOne("main",false,true); Pump(100); entered.Wait(2000);
        DiagnoseCollection(); CloseAssert(!blocked.IsAlive,"blocked-read-does-not-retain-closed-window");
        Environment.SetEnvironmentVariable("SONICROUTE_CLOSE_DELAY",null); release.Set(); Pump(300);
        CloseAssert((int)typeof(SessionVolumeService).GetField("CloseTestWrites")!.GetValue(null)! == 0,"closing-does-not-write-audio");
        CloseAssert((int)typeof(AudioService).GetField("CloseTestRouteWrites")!.GetValue(null)! == 0,"closing-does-not-write-audio-routing");
        var pendingPanel=new QuickPanelModernWindow();
        ((Dictionary<int,int>)Field(pendingPanel,"_pendingVolume")!)[10000001]=37;
        pendingPanel.Close(); Pump(100);
        CloseAssert((int)typeof(SessionVolumeService).GetField("CloseTestWrites")!.GetValue(null)! == 1,"pending-volume-committed-after-close");
        ((IDisposable)tray).Dispose(); SetField(app,"_trayWheel",null);
        Memory("diagnostic-end");
    }

    private static void RunResident(App app)
    {
        Config=new AppConfig { Language="zh-CN", StartMinimized=true, ShowAppPeakMeter=true,
            Hotkeys=new Dictionary<string,string>(), MicMuteOsdPersistent=false };
        typeof(ConfigService).GetField("_cache",PrivateStatic)!.SetValue(null,Config);
        Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data"));
        Environment.SetEnvironmentVariable("SONICROUTE_CLOSE_RESIDENT","1");
        // OnStartup queued by InitializeComponent runs during the first dispatcher pump.
        Pump(8000); Memory("startup-idle");
        if(Field(app,"_trayWheel")==null) throw new InvalidOperationException("Resident services not started");
        var timings=new List<double>();
        for(int round=0;round<20;round++)
        {
            var sw=Stopwatch.StartNew();
            Call(app,"CancelUiReclaim");
            var main=new MainWindow(); SetField(app,"_mainWindow",main); ShowOffscreen(main); Pump(450); timings.Add(sw.Elapsed.TotalMilliseconds);
            Call(main,"SubscribeOsdAdjust"); Call(main,"SubscribePanelPosAdjust");
            main.Close(); SetField(app,"_mainWindow",null); main=null!;
            foreach(var kind in new[]{"modern","classic"})
            {
                var panel=MakeWindow(kind); SetField(app,"_quickPanel",panel); ShowOffscreen(panel); Call(panel,"LoadAsync"); Pump(450);
                panel.Close(); SetField(app,"_quickPanel",null); panel=null!;
            }
            Pump(200);
        }
        Memory("after-60-window-cycles");
        Call(app,"ScheduleUiReclaim");
        for(int second=0;second<60;second++) { Pump(1000); if(second%5==4) Memory("idle-"+(second+1)); }
        // Reopen speed excludes the fixed warmup wait; both variants use the same path.
        var openTimes=new List<double>();
        for(int i=0;i<5;i++)
        {
            Call(app,"CancelUiReclaim");
            var sw=Stopwatch.StartNew(); var w=new MainWindow(); SetField(app,"_mainWindow",w); ShowOffscreen(w); w.UpdateLayout();
            openTimes.Add(sw.Elapsed.TotalMilliseconds); Pump(250); w.Close(); SetField(app,"_mainWindow",null); Pump(150);
        }
        Add(new {Case="reopen", Milliseconds=openTimes, MedianMilliseconds=openTimes.OrderBy(x=>x).ElementAt(2)});
        Memory("end");
        // No diagnostic GC in this mode.
    }
}
