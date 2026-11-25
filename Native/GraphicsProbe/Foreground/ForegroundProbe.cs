using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonicRoute;
using SonicRoute.GraphicsProbe;

internal static unsafe class ForegroundProbe
{
    private const uint EventForeground=3,TimeChange=0x001e,PowerBroadcast=0x0218,ResumeAutomatic=0x12;
    private static nint _window;
    private static uint _thread,_witnessThread;
    private static int _witnessCount,_timeMessages,_resumeMessages;
    [DllImport("user32")] private static extern void NotifyWinEvent(uint type,nint hwnd,int obj,int child);
    [DllImport("user32")] private static extern nint SetWinEventHook(uint min,uint max,nint module,
        delegate* unmanaged[Stdcall]<nint,uint,nint,int,int,uint,uint,void> call,uint process,uint thread,uint flags);
    [DllImport("user32")] private static extern int UnhookWinEvent(nint hook);
    [DllImport("user32",CharSet=CharSet.Unicode)] private static extern int GetUserObjectInformationW(nint obj,uint index,char* info,uint bytes,out uint needed);

    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static void Witness(nint hook,uint ev,nint hwnd,int obj,int child,uint thread,uint time)
    {
        if(ev==EventForeground && hwnd==_window && obj==0 && child==0 && thread==_thread)
        {
            _witnessCount++;
            _witnessThread=Win.GetCurrentThreadId();
        }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static nint WindowMessages(nint hwnd,uint msg,nuint w,nint l,nuint id,nuint state)
    {
        if(msg==TimeChange) _timeMessages++;
        if(msg==PowerBroadcast && w==ResumeAutomatic) _resumeMessages++;
        return Win.DefSubclassProc(hwnd,msg,w,l);
    }
    private static void Pump(TimeSpan duration,Func<bool>? ready=null)
    {
        var clock=Stopwatch.StartNew();
        while(true)
        {
            while(Win.PeekMessageW(out var msg,0,0,0,1)!=0){Win.TranslateMessage(in msg);Win.DispatchMessageW(in msg);}
            if(ready?.Invoke()==true)return;
            var remaining=(duration-clock.Elapsed).TotalMilliseconds;
            if(remaining<=0)return;
            Win.MsgWaitForMultipleObjectsEx(0,0,(uint)Math.Ceiling(remaining),0x04ff,4);
        }
    }
    private static void Require(bool predicate,string message)
    {
        if(!predicate)throw new InvalidOperationException(message);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length!=1 || !Path.IsPathFullyQualified(args[0]))return 2;
        using var log=new StreamWriter(args[0]){AutoFlush=true};
        _thread=Win.GetCurrentThreadId();
        char* buffer=stackalloc char[256];
        nint desktop=Win.GetThreadDesktop(_thread);
        nint subclass=(nint)(delegate* unmanaged[Stdcall]<nint,uint,nuint,nint,nuint,nuint,nint>)&WindowMessages;
        nint witnessHook=0;
        try
        {
            Win.Ok(GetUserObjectInformationW(desktop,2,buffer,512,out _),"read desktop name");
            string name=new(buffer);
            Require(name.StartsWith("SonicRoute-OutputChecks-",StringComparison.Ordinal),"Private launcher required.");
            log.WriteLine($"runtime={RuntimeInformation.FrameworkDescription}; arch={RuntimeInformation.ProcessArchitecture}; desktop={name}; STA={Thread.CurrentThread.GetApartmentState()}; thread={_thread}");
            Require(Thread.CurrentThread.GetApartmentState()==ApartmentState.STA,"STA required.");
            // A hidden, nonactivating window. No call to SetForegroundWindow, ShowWindow or SendInput.
            _window=Win.Handle(Win.CreateWindowExW(0x08000080,"STATIC","SonicRoute.ForegroundProbe",0x80000000,
                0,0,16,16,0,0,Win.GetModuleHandleW(null),0),"CreateWindowEx");
            Win.Ok(Win.SetWindowSubclass(_window,subclass,1,0),"SetWindowSubclass");
            Pump(TimeSpan.FromMilliseconds(50));
            witnessHook=Win.Handle(SetWinEventHook(EventForeground,EventForeground,0,&Witness,(uint)Environment.ProcessId,_thread,0),"witness SetWinEventHook");
            int productCount=0;uint callbackThread=0;
            using var product=new ForegroundChangeSource(()=>{productCount++;callbackThread=Win.GetCurrentThreadId();});
            Require(product.IsInstalled,"Production SetWinEventHook failed.");
            Pump(TimeSpan.FromMilliseconds(50));
            Require(productCount==0&&_witnessCount==0,"Unexpected foreground event before injection.");
            NotifyWinEvent(EventForeground,_window,0,0);
            Pump(TimeSpan.FromSeconds(2),()=>productCount>0&&_witnessCount>0);
            Require(productCount==1&&_witnessCount==1&&callbackThread==_thread&&_witnessThread==_thread,"Production foreground callback/witness mismatch.");
            log.WriteLine($"first injection: installed={product.IsInstalled}, productCallbacks={productCount}, scopedWitness={_witnessCount}, productCallbackThread={callbackThread}");
            product.Dispose();Require(!product.IsInstalled,"Hook state still installed.");
            NotifyWinEvent(EventForeground,_window,0,0);
            Pump(TimeSpan.FromSeconds(2),()=>_witnessCount>=2);
            Pump(TimeSpan.FromMilliseconds(200));
            Require(_witnessCount==2&&productCount==1,"Post-dispose injection not observed exclusively by witness.");
            log.WriteLine($"second injection after Dispose: installed={product.IsInstalled}, productCallbacks={productCount}, scopedWitness={_witnessCount}; drainedWindowMs=200");
            Require(product.TryRegister()&&product.IsInstalled,"Production re-registration failed.");
            NotifyWinEvent(EventForeground,_window,0,0);
            Pump(TimeSpan.FromSeconds(2),()=>_witnessCount>=3&&productCount>=2);
            Require(_witnessCount==3&&productCount==2,"Recovery registration callback mismatch.");
            log.WriteLine($"third injection after TryRegister: installed={product.IsInstalled}, productCallbacks={productCount}, scopedWitness={_witnessCount}");
            Win.SendMessageW(_window,TimeChange,0,0);Win.SendMessageW(_window,PowerBroadcast,ResumeAutomatic,0);
            Require(_timeMessages==1&&_resumeMessages==1,"Directed system-message sample failed.");
            log.WriteLine($"directed private HWND messages: WM_TIMECHANGE={_timeMessages}, WM_POWERBROADCAST/PBT_APMRESUMEAUTOMATIC={_resumeMessages}; no OS clock/power changes");
            log.WriteLine("PASS Native AOT production delegate thunk, OS WinEvent dispatch on STA, unhook and re-registration; events injected, actual foreground never switched.");
            return 0;
        }
        catch(Exception ex){log.WriteLine(ex);return 1;}
        finally
        {
            if(witnessHook!=0)UnhookWinEvent(witnessHook);
            if(_window!=0){Win.RemoveWindowSubclass(_window,subclass,1);Win.DestroyWindow(_window);}
        }
    }
}
