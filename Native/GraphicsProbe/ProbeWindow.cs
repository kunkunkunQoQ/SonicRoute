using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonicRoute.Native.Graphics;

namespace SonicRoute.GraphicsProbe;

internal unsafe sealed class ProbeWindow : IDisposable
{
    private static ProbeWindow? _active;
    private static readonly nint Module = Win.GetModuleHandleW(null);
    private const string ClassName="SonicRoute.Native.GraphicsProbe.Window";
    internal readonly DrawingSurface MainSurface, OverlaySurface;
    internal nint Main, Edit, Slider, Popup;
    internal uint Dpi;
    internal int PaintCount, PopupDrawCount, SliderValue=35;
    internal int ImeStarts,ImeUpdates,ImeResults,ImeEnds;
    internal bool InjectLoss, Interactive;
    internal byte CornerAlpha, CenterAlpha;
    internal Exception? PaintError;

    internal static void Register()
    {
        fixed(char* cls=ClassName)
        {
            Win.WindowClass wc=new() { Size=(uint)sizeof(Win.WindowClass), Proc=(nint)(delegate* unmanaged[Stdcall]<nint,uint,nuint,nint,nint>)&Procedure,
                Instance=Module, Cursor=Win.LoadCursorW(0,32512), Name=(nint)cls };
            Win.Ok(Win.RegisterClassExW(in wc),"RegisterClassExW");
        }
        Win.Controls init=new(){Size=8,Flags=4};
        Win.Ok(Win.InitCommonControlsEx(in init),"InitCommonControlsEx");
    }
    internal static void Unregister() => Win.Ok(Win.UnregisterClassW(ClassName,Module),"UnregisterClassW");
    internal ProbeWindow(DrawingFactory factory,bool interactive,bool? activateOnShow=null)
    {
        if (_active!=null) throw new InvalidOperationException("Another probe window is open.");
        MainSurface=new(factory,false); OverlaySurface=new(factory,true);
        Interactive=interactive; _active=this;
        try
        {
            long sample=0;OpenTimings.Begin(ref sample);
            Main=Win.Handle(Win.CreateWindowExW(0,ClassName,"SonicRoute · 原生图形验证",0x02CF0000,80,80,470,240,0,0,Module,0),"main HWND");
            OpenTimings.Mark(ref sample,"CreateWindowEx.Main");
            Dpi=Win.GetDpiForWindow(Main);
            Edit=Win.Handle(Win.CreateWindowExW(0x200,"EDIT","中文输入",0x50010080,0,0,1,1,Main,101,Module,0),"EDIT");
            OpenTimings.Mark(ref sample,"CreateWindowEx.Edit");
            Win.Ok(Win.SetWindowSubclass(Edit,EditProc,1,0),"SetWindowSubclass");
            Slider=Win.Handle(Win.CreateWindowExW(0,"msctls_trackbar32","Volume",0x50010001,0,0,1,1,Main,102,Module,0),"TRACKBAR");
            OpenTimings.Mark(ref sample,"CreateWindowEx.Trackbar");
            Win.SendMessageW(Edit,0x30,(nuint)Win.GetStockObject(17),1);
            Win.SendMessageW(Slider,0x406,1,100<<16); // TBM_SETRANGE, 0..100
            Win.SendMessageW(Slider,0x405,1,SliderValue);
            OpenTimings.Mark(ref sample,"SetFontAndRange");
            Popup=Win.Handle(Win.CreateWindowExW(0x080800A0,"STATIC","SonicRoute.Overscreen",0x80000000,0,0,1,1,Main,0,Module,0),"layered popup");
            OpenTimings.Mark(ref sample,"CreateWindowEx.OSD");
            Layout();
            OpenTimings.Mark(ref sample,"Layout");
            // A private test desktop is never the input desktop. Asking the shell to
            // activate its windows can block ShowWindow even though no user can see them.
            Win.ShowWindow(Main,activateOnShow.GetValueOrDefault(interactive)?5:4);
            OpenTimings.Mark(ref sample,"ShowWindow");
            Win.UpdateWindow(Main);
            OpenTimings.Mark(ref sample,"UpdateWindow");
        }
        catch { Dispose(); throw; }
    }
    private static nint EditProc => (nint)(delegate* unmanaged[Stdcall]<nint,uint,nuint,nint,nuint,nuint,nint>)&EditProcedure;
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static nint EditProcedure(nint hwnd,uint message,nuint w,nint l,nuint id,nuint data)
    {
        if(_active is { } p)
        {
            if(message==0x10d)p.ImeStarts++;
            if(message==0x10e)p.ImeEnds++;
            if(message==0x10f && (l&8)!=0)p.ImeUpdates++;
            if(message==0x10f && (l&0x800)!=0)p.ImeResults++;
        }
        if(message==0x82)Win.RemoveWindowSubclass(hwnd,EditProc,1);
        return Win.DefSubclassProc(hwnd,message,w,l);
    }
    internal void Layout()
    {
        if(Edit==0 || Slider==0 || Dpi==0) return;
        Win.GetClientRect(Main,out var area);
        int Sc(int dip)=>(int)Math.Round(dip*Dpi/96.0);
        Win.SetWindowPos(Edit,0,Sc(16),Sc(64),Math.Max(1,area.Width-Sc(32)),Sc(28),0x14);
        Win.SetWindowPos(Slider,0,Sc(16),Sc(106),Math.Max(1,area.Width-Sc(32)),Sc(38),0x14);
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static nint Procedure(nint hwnd,uint msg,nuint w,nint l)
    {
        try
        {
            var p=_active;
            if(p!=null && hwnd==p.Main)
            {
                switch(msg)
                {
                    case 0x000F:
                        long sample=0;OpenTimings.Begin(ref sample);
                        Win.BeginPaint(hwnd,out var paint);
                        OpenTimings.Mark(ref sample,"BeginPaint");
                        try { p.Paint(); } finally { Win.EndPaint(hwnd,in paint); }
                        OpenTimings.Mark(ref sample,"D2DAndEndPaint");
                        return 0;
                    case 0x0014: return 1;
                    case 0x0005: p.Layout(); Win.InvalidateRect(hwnd,0,0); break;
                    case 0x02E0: // WM_DPICHANGED
                        p.Dpi=(uint)w&0xffff;
                        if(l!=0){var r=*(WinRect*)l;Win.SetWindowPos(hwnd,0,r.Left,r.Top,r.Width,r.Height,0x14);}
                        p.Layout(); Win.InvalidateRect(hwnd,0,0); return 0;
                    case 0x0114 when l==p.Slider: // WM_HSCROLL
                        p.SliderValue=(int)Win.SendMessageW(p.Slider,0x400,0,0);
                        Win.InvalidateRect(hwnd,0,0);
                        if (p.Interactive) p.DrawPopup();
                        return 0;
                    case 0x0002: if(p.Interactive) Win.PostQuitMessage(0); return 0;
                }
            }
        }
        catch(Exception ex) { if(_active!=null) _active.PaintError=ex; return 0; }
        return Win.DefWindowProcW(hwnd,msg,w,l);
    }
    private void Paint()
    {
        PaintCount++;
        Win.GetClientRect(Main,out var area);
        bool presented;
        if (InjectLoss) { InjectLoss=false; presented=MainSurface.Finish(DrawingSurface.RecreateTarget); }
        else presented=MainSurface.Draw(Main,0,area.Width,area.Height,Dpi,$"音跃 · 主音量 {SliderValue}%");
        if (!presented) Win.InvalidateRect(Main,0,0);
    }
    internal void DrawPopup()
    {
        uint dpi=Dpi;
        int width=(int)Math.Ceiling(190*dpi/96d),height=(int)Math.Ceiling(54*dpi/96d);
        Win.BitmapInfo bi=new(){Size=40,Width=width,Height=-height,Planes=1,Bits=32};
        nint dc=Win.Handle(Win.CreateCompatibleDC(0),"CreateCompatibleDC"), bitmap=0, old=0;
        try
        {
            bitmap=Win.Handle(Win.CreateDIBSection(dc,in bi,0,out var bits,0,0),"CreateDIBSection");
            old=Win.Handle(Win.SelectObject(dc,bitmap),"SelectObject");
            if (!OverlaySurface.Draw(Popup,dc,width,height,dpi,$"音量 {SliderValue}%"))
            {
                if(!OverlaySurface.Draw(Popup,dc,width,height,dpi,$"音量 {SliderValue}%")) throw new InvalidOperationException("Repeated OSD target loss.");
            }
            var data=(byte*)bits;
            CornerAlpha=data[3]; CenterAlpha=data[(width*(height/2)+width/2)*4+3];
            Win.GetWindowRect(Main,out var owner);
            Win.Pt dest=new(owner.Right-width,owner.Bottom+5),size=new(width,height),source=new(0,0);
            Win.Blend blend=new(){Alpha=255,Format=1};
            Win.Ok(Win.UpdateLayeredWindow(Popup,0,in dest,in size,dc,in source,0,in blend,2),"UpdateLayeredWindow");
            Win.ShowWindow(Popup,4);
            PopupDrawCount++;
        }
        finally { if(old!=0) Win.SelectObject(dc,old); if(bitmap!=0) Win.DeleteObject(bitmap); Win.DeleteDC(dc); }
    }
    internal string ReadText()
    {
        char* text=stackalloc char[256];
        int n=Win.GetWindowTextW(Edit,text,256);return new string(text,0,n);
    }
    internal void Dispatch(ref Win.Msg m)
    {
        if(Win.IsDialogMessageW(Main,ref m)==0){Win.TranslateMessage(in m);Win.DispatchMessageW(in m);}
    }
    public void Dispose()
    {
        if(Popup!=0){Win.DestroyWindow(Popup);Popup=0;}
        if(Main!=0){Win.DestroyWindow(Main);Main=0;}
        _active=null; Edit=Slider=0;
        OverlaySurface.Dispose(); MainSurface.Dispose();
    }
}
