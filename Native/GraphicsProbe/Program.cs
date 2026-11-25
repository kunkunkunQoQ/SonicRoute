using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonicRoute.Native.Graphics;

namespace SonicRoute.GraphicsProbe;

internal static unsafe class Program
{
    private static readonly List<(WinRect Area,uint Dpi)> Screens=[];
    private static readonly Dictionary<string,int> LiveClasses=new();
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int InspectWindow(nint hwnd,nint state)
    {
        Win.GetWindowThreadProcessId(hwnd,out uint pid);
        if(pid!=Environment.ProcessId)return 1;
        char* name=stackalloc char[128];int n=Win.GetClassNameW(hwnd,name,128);
        if(n>0){var cls=new string(name,0,n);LiveClasses[cls]=LiveClasses.GetValueOrDefault(cls)+1;}
        return 1;
    }
    private static string Windows(nint desktop)
    {
        LiveClasses.Clear();Win.EnumDesktopWindows(desktop,&InspectWindow,0);
        return string.Join(",",LiveClasses.Select(entry=>$"{entry.Key}:{entry.Value}"));
    }
    private static Exception? _failure;
    private static bool _interactive;
    [DllImport("kernel32",SetLastError=true)] private static extern nint CreateThread(nint attributes,nuint stack,delegate* unmanaged[Stdcall]<nint,uint> start,nint arg,uint flags,nint threadId);
    [DllImport("kernel32")] private static extern uint WaitForSingleObject(nint handle,uint timeout);
    [DllImport("kernel32")] private static extern int CloseHandle(nint handle);
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static uint RunDesktopThread(nint desktop)
    {
        try { Run(desktop,_interactive);return 0; }
        catch(Exception e){_failure=e;return 1;}
    }
    private static int Main(string[] args)
    {
        bool interactive=args.Length==1 && args[0]=="--interactive";
        if(args.Length!=0 && !interactive && !(args.Length==1 && args[0]=="--self-test")) return 2;
        nint desktop=0;
        try
        {
            if(!interactive)
                desktop=Win.Handle(Win.CreateDesktopW($"SonicRoute.GraphicsAudit.{Environment.ProcessId}",0,0,0,0x1ff,0),"CreateDesktopW");
            // Create and destroy all desktop-owned UI on the same thread. The default test
            // never switches the input desktop and never sends global input.
            // A managed STA thread already owns COM's HWND when its delegate starts.
            // Bind the fresh native thread to the private desktop before OleInitialize.
            _interactive=interactive;
            nint ui=Win.Handle(CreateThread(0,0,&RunDesktopThread,desktop,0,0),"CreateThread");
            try { if(WaitForSingleObject(ui,uint.MaxValue)!=0)throw new InvalidOperationException("UI thread wait failed."); }
            finally { CloseHandle(ui); }
            if(_failure!=null){Console.Error.WriteLine(_failure);return 1;}
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally { if(desktop!=0) Win.CloseDesktop(desktop); }
    }
    private static void Run(nint desktop,bool interactive)
    {
        if(desktop!=0) Win.Ok(Win.SetThreadDesktop(desktop),"SetThreadDesktop");
        NativeCom.Check(Win.OleInitialize(0));
        try
        {
            ProbeWindow.Register();
            try
            {
                if(interactive)
                {
                    using var factory=new DrawingFactory();
                    using var window=new ProbeWindow(factory,true);
                    window.DrawPopup();Win.SetFocus(window.Edit);
                    while(Win.GetMessageW(out var m,0,0,0)>0) window.Dispatch(ref m);
                    ThrowOnPaint(window);
                }
                else
                {
#if GRAPHICS_OPEN_PROFILE
                    Console.WriteLine("Focused open profile: isolated desktop, no IME/UIA test or global input.");
                    bool[] activate=[true,true,true,false,false,true,false];
                    for(int i=0;i<activate.Length;i++)
                    {
                        Console.WriteLine($"sample={i+1}; activate={activate[i]}");
                        using(var f=new DrawingFactory()) using(var w=new ProbeWindow(f,false,activate[i]))
                        {
                            ThrowOnPaint(w);Need(w.PaintCount>0,"first render delivered");
                        }
                        Pump(null,TimeSpan.FromMilliseconds(8));
                    }
#else
                    Test(desktop);
#endif
                }
            }
            finally { ProbeWindow.Unregister(); }
        }
        finally {Win.OleUninitialize();}
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int MonitorCallback(nint handle,nint dc,WinRect* area,nint state)
    {
        Win.GetDpiForMonitor(handle,0,out uint dpi,out _);Screens.Add((*area,dpi));return 1;
    }
    private static void Test(nint desktop)
    {
        Console.WriteLine($"runtime={RuntimeInformation.FrameworkDescription}; os={Environment.OSVersion.Version}; arch={RuntimeInformation.ProcessArchitecture}; desktop=private");
        Win.Ok(Win.EnumDisplayMonitors(0,0,&MonitorCallback,0),"EnumDisplayMonitors");
        foreach(var s in Screens) Console.WriteLine($"monitor={s.Area.Left},{s.Area.Top},{s.Area.Width}x{s.Area.Height}; effectiveDpi={s.Dpi}");
        var create=Stopwatch.StartNew();
        using(var factory=new DrawingFactory())
        using(var window=new ProbeWindow(factory,false))
        {
            Console.WriteLine($"initialCreateAndShowMs={create.Elapsed.TotalMilliseconds:F3}; paintsAtReturn={window.PaintCount}");
            Pump(window,TimeSpan.FromMilliseconds(150));
            Need(window.MainSurface.Generation>0,"D2D HWND target painted");
            Console.WriteLine($"initialDpi={window.Dpi}; firstShowThroughSettleMs={create.Elapsed.TotalMilliseconds:F2}");
            Win.SetFocus(window.Edit);Need(Win.GetFocus()==window.Edit,"EDIT keyboard focus");
            // Exercise standard EDIT's actual Unicode input procedure without global SendInput.
            Win.SendMessageW(window.Edit,0xb1,0,(nint)(-1)); // EM_SETSEL
            const string expected="音跃中文 37";
            foreach(char c in expected)Win.SendMessageW(window.Edit,0x102,c,0);
            Need(window.ReadText()==expected,"Unicode WM_CHAR committed in EDIT");
            ProbeIme(window);
            Win.SetFocus(window.Slider);
            Pump(window,TimeSpan.FromMilliseconds(100));
            Console.WriteLine($"IME after focus transition text={window.ReadText()}; start={window.ImeStarts}, updates={window.ImeUpdates}, results={window.ImeResults}, end={window.ImeEnds}");
            Win.Ok(Win.SetWindowTextW(window.Edit,expected),"restore UIA fixture text");
            Win.SendMessageW(window.Slider,0x100,0x27,0);Win.SendMessageW(window.Slider,0x101,0x27,0);
            Pump(window,TimeSpan.FromMilliseconds(75));
            Need(Win.GetFocus()==window.Slider && window.SliderValue==36,"focus moved, trackbar right key reflected via WM_HSCROLL");
            nint active=Win.GetActiveWindow(), focus=Win.GetFocus();
            window.DrawPopup();
            Need(active==Win.GetActiveWindow() && focus==Win.GetFocus(),"layered OSD kept focus and activation");
            Need(window.CornerAlpha==0 && window.CenterAlpha>0,"D2D DC target has transparent corner and painted interior");
            Console.WriteLine($"OSD BGRA alpha corner={window.CornerAlpha} middle={window.CenterAlpha}");
            var uia=new TaskCompletionSource<(string Text,double Value,int EditType,int SliderType)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var client=new Thread(()=>{try{uia.SetResult(AutomationCheck.Read(desktop,window.Edit,window.Slider));}catch(Exception e){uia.SetException(e);}}){IsBackground=true};
            client.Start();var limit=Stopwatch.StartNew();
            while(!uia.Task.IsCompleted && limit.Elapsed<TimeSpan.FromSeconds(12)) Pump(window,TimeSpan.FromMilliseconds(30));
            Need(uia.Task.IsCompleted,"UIA client completed");
            var result=uia.Task.GetAwaiter().GetResult();
            Console.WriteLine($"UIA controlTypes={result.EditType},{result.SliderType}; value={result.Text}; range={result.Value}");
            Need(result.EditType==50004 && result.SliderType==50015 && result.Value==36 && result.Text==expected,"UIA Value and RangeValue from real EDIT and TRACKBAR");
            uint originalDpi=window.Dpi;
            foreach(var monitor in Screens)
            {
                Win.SetWindowPos(window.Main,0,monitor.Area.Left+8,monitor.Area.Top+8,470,240,0x14);
                Pump(window,TimeSpan.FromMilliseconds(40));
                Console.WriteLine($"realMove=({monitor.Area.Left},{monitor.Area.Top}); GetDpiForWindow={Win.GetDpiForWindow(window.Main)}; logicalDpi={window.Dpi}");
            }
            WinRect injected=new(-720,-370,-80,-30);
            Win.SendMessageW(window.Main,0x02E0,192u|(192u<<16),(nint)(&injected));
            Win.GetWindowRect(window.Main,out var actual);
            Need(window.Dpi==192 && actual.Left==injected.Left && actual.Top==injected.Top,"injected WM_DPICHANGED uses suggested negative rectangle");
            Win.GetWindowRect(window.Edit,out var editRect);
            Need(editRect.Height==56,"EDIT relayout at injected 200% DPI");
            WinRect restore=new(80,80,550,320);
            Win.SendMessageW(window.Main,0x02E0,originalDpi|(originalDpi<<16),(nint)(&restore));
            Pump(window,TimeSpan.FromMilliseconds(50));
            int gen=window.MainSurface.Generation;
            window.InjectLoss=true;Win.InvalidateRect(window.Main,0,0);Win.UpdateWindow(window.Main);
            Pump(window,TimeSpan.FromMilliseconds(80));
            Need(window.MainSurface.Generation==gen+1 && window.MainSurface.Recreated==1,"injected D2DERR_RECREATE_TARGET disposed/repainted a new HWND target");
            int paints=window.PaintCount,popupPaints=window.PopupDrawCount;
            using var process=Process.GetCurrentProcess();
            var idleCpu=process.TotalProcessorTime;var idle=Stopwatch.StartNew();
            Pump(window,TimeSpan.FromSeconds(2));
            Console.WriteLine($"idleMs={idle.Elapsed.TotalMilliseconds:F2}; processCpuMs={(process.TotalProcessorTime-idleCpu).TotalMilliseconds:F2}; paintDelta={window.PaintCount-paints}; popupDrawDelta={window.PopupDrawCount-popupPaints}");
            Need(window.PaintCount==paints && window.PopupDrawCount==popupPaints,"idle surface has zero paints");
            var redraw=new List<double>();
            for(int i=0;i<45;i++)
            {
                var sw=Stopwatch.GetTimestamp();Win.SendMessageW(window.Slider,0x100,(nuint)(i%2==0?0x27:0x25),0);
                Win.UpdateWindow(window.Main);ThrowOnPaint(window);redraw.Add(Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
            }
            Timings("keyThroughEndDrawMs",redraw);
        }
        Pump(null,TimeSpan.FromMilliseconds(500));
        uint gdi=Win.GetGuiResources((nint)(-1),0),user=Win.GetGuiResources((nint)(-1),1);
        Console.WriteLine($"cycleBaseline: GDI={gdi}; USER={user}; ownDesktopWindows={Windows(desktop)}");
        var cycle=new List<double>();
        var factoryTimes=new List<double>();var windowTimes=new List<double>();
        var overlayTimes=new List<double>();var closeTimes=new List<double>();
        uint warmGdi=0,warmUser=0;
        for(int i=0;i<30;i++)
        {
            long start=Stopwatch.GetTimestamp();
            nint main,edit,slider,popup;
            ProbeWindow closed;
            long closeStart;
            using(var f=new DrawingFactory())
            {
                long windowStart=Stopwatch.GetTimestamp();
                factoryTimes.Add(Stopwatch.GetElapsedTime(start,windowStart).TotalMilliseconds);
                using(var w=new ProbeWindow(f,false))
                {
                    long overlayStart=Stopwatch.GetTimestamp();
                    windowTimes.Add(Stopwatch.GetElapsedTime(windowStart,overlayStart).TotalMilliseconds);
                    closed=w;
                    main=w.Main;edit=w.Edit;slider=w.Slider;popup=w.Popup;
                    Win.UpdateWindow(w.Main);w.DrawPopup();ThrowOnPaint(w);
                    closeStart=Stopwatch.GetTimestamp();
                    overlayTimes.Add(Stopwatch.GetElapsedTime(overlayStart,closeStart).TotalMilliseconds);
                }
            }
            closeTimes.Add(Stopwatch.GetElapsedTime(closeStart).TotalMilliseconds);
            if(Win.IsWindow(main)+Win.IsWindow(edit)+Win.IsWindow(slider)+Win.IsWindow(popup)!=0)
                throw new InvalidOperationException("Owned HWND still live after close.");
            if(closed.MainSurface.HasDeviceResources || closed.OverlaySurface.HasDeviceResources)
                throw new InvalidOperationException("Retained render target or brush after close.");
            cycle.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            // Controls/COM post teardown work to the owner. A real host returns to its
            // message loop after closing; blocking the STA across all 30 closes distorts this.
            Pump(null,TimeSpan.FromMilliseconds(8));
            if ((i+1)%10==0) Console.WriteLine($"cycleBatch={i+1}; GDI={Win.GetGuiResources((nint)(-1),0)}; USER={Win.GetGuiResources((nint)(-1),1)}; ownDesktopWindows={Windows(desktop)}");
            if(i==4)
            {
                Timings("first5.factoryMs",factoryTimes);Timings("first5.windowCreateShowMs",windowTimes);
                Timings("first5.osdDrawMs",overlayTimes);Timings("first5.closeReleaseMs",closeTimes);
            }
            if(i==9){warmGdi=Win.GetGuiResources((nint)(-1),0);warmUser=Win.GetGuiResources((nint)(-1),1);}
        }
        Pump(null,TimeSpan.FromMilliseconds(500));
        uint finalGdi=Win.GetGuiResources((nint)(-1),0),finalUser=Win.GetGuiResources((nint)(-1),1);
        Console.WriteLine($"cycles={cycle.Count}; GDI={gdi}->{finalGdi}; USER={user}->{finalUser}");
        Console.WriteLine("PASS all 120 owned HWNDs destroyed and per-window D2D pointers released across 30 closes");
        if(finalGdi>gdi || finalUser>user)
            Console.WriteLine("OBSERVATION cold-to-final GUI counters increased; warm plateau check is separate.");
        Need(finalGdi<=warmGdi && finalUser<=warmUser,"GDI/USER handles after 30 closes at or below post-warm-up (10 closes)");
        Timings("cycleMs",cycle);
        Timings("factoryMs",factoryTimes);Timings("windowCreateShowMs",windowTimes);
        Timings("osdDrawMs",overlayTimes);Timings("closeReleaseMs",closeTimes);
    }
    private static void ProbeIme(ProbeWindow window)
    {
        int count=Win.GetKeyboardLayoutList(0,null);var layouts=new nint[Math.Max(1,count)];fixed(nint* pl=layouts) Win.GetKeyboardLayoutList(layouts.Length,pl);
        nint current=Win.GetKeyboardLayout(0),ctx=Win.ImmGetContext(window.Edit);
        Console.WriteLine($"IME layouts={string.Join(',',layouts.Select(x=>x.ToString("X",CultureInfo.InvariantCulture)))}; active={current:X}; immIsIme={Win.ImmIsIME(current)}; HIMC={ctx!=0}");
        if(ctx==0){Console.WriteLine("IME real candidate selection requires interactive verification.");return;}
        try
        {
            const string preedit="中文";
            fixed(char* s=preedit)
            {
                int set=Win.ImmSetCompositionStringW(ctx,9,s,(uint)(preedit.Length*2),0,0);
                Pump(window,TimeSpan.FromMilliseconds(100));
                char* got=stackalloc char[32];int size=Win.ImmGetCompositionStringW(ctx,8,got,64);
                Console.WriteLine($"IME IMM composition injection set={set}; returnedBytes={size}; matched={(size==4 && new string(got,0,2)==preedit)}. Real Pinyin candidate selection requires interactive verification.");
                int completed=Win.ImmNotifyIME(ctx,0x15,1,0);
                Console.WriteLine($"IME injected completion acknowledged={completed}");
            }
        }
        finally { Win.ImmReleaseContext(window.Edit,ctx); }
    }
    private static void Pump(ProbeWindow? window,TimeSpan duration)
    {
        var clock=Stopwatch.StartNew();
        while(true)
        {
            while(Win.PeekMessageW(out var m,0,0,0,1)!=0)
            {
                if(window!=null)window.Dispatch(ref m);
                else {Win.TranslateMessage(in m);Win.DispatchMessageW(in m);}
            }
            if(window!=null)ThrowOnPaint(window);
            double left=(duration-clock.Elapsed).TotalMilliseconds;
            if(left<=0) return;
            Win.MsgWaitForMultipleObjectsEx(0,0,(uint)Math.Ceiling(left),0x04ff,4);
        }
    }
    private static void Need(bool result,string label)
    {
        if(!result)throw new InvalidOperationException("FAIL "+label);
        Console.WriteLine("PASS "+label);
    }
    private static void ThrowOnPaint(ProbeWindow w)
    {
        if(w.PaintError!=null)throw new InvalidOperationException("Window message failed",w.PaintError);
    }
    private static void Timings(string name,List<double> values)
    {
        values.Sort();Console.WriteLine($"{name}: n={values.Count}, p50={values[values.Count/2]:F3}, p95={values[(int)Math.Ceiling(values.Count*.95)-1]:F3}");
    }
}
