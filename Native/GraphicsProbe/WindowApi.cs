using System.Runtime.InteropServices;
using System.ComponentModel;
using SonicRoute.Native.Graphics;

namespace SonicRoute.GraphicsProbe;

internal static unsafe class Win
{
    [StructLayout(LayoutKind.Sequential)] internal struct WindowClass
    {
        internal uint Size,Style; internal nint Proc; internal int ClassBytes,WindowBytes;
        internal nint Instance,Icon,Cursor,Background,Menu,Name,SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Controls { internal uint Size,Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Msg { internal nint Window; internal uint Message; internal nuint W; internal nint L; internal uint Time; internal Pt Point; internal uint Private; }
    [StructLayout(LayoutKind.Sequential)] internal struct Pt(int x,int y) { internal int X=x,Y=y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Paint { internal nint Dc; internal int Erase; internal WinRect Rect; internal int Restore,Inc; internal fixed byte Reserved[32]; }
    [StructLayout(LayoutKind.Sequential)] internal struct Blend { internal byte Op,Flags,Alpha,Format; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
    {
        internal uint Size; internal int Width,Height; internal ushort Planes,Bits; internal uint Compression,ImageSize;
        internal int X,Y; internal uint Colors,Important,UnusedColor;
    }
    [DllImport("kernel32",CharSet=CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? module);
    [DllImport("user32",SetLastError=true,CharSet=CharSet.Unicode)] internal static extern nint CreateDesktopW(string name,nint device,nint mode,uint flags,uint access,nint security);
    [DllImport("user32",SetLastError=true)] internal static extern int SetThreadDesktop(nint desktop);
    [DllImport("user32")] internal static extern int CloseDesktop(nint desktop);
    [DllImport("user32")] internal static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32",SetLastError=true)] internal static extern ushort RegisterClassExW(in WindowClass wc);
    [DllImport("user32",CharSet=CharSet.Unicode)] internal static extern int UnregisterClassW(string name,nint module);
    [DllImport("user32",SetLastError=true,CharSet=CharSet.Unicode)] internal static extern nint CreateWindowExW(uint ex,string cls,string name,uint style,int x,int y,int w,int h,nint parent,nint id,nint module,nint p);
    [DllImport("user32")] internal static extern nint DefWindowProcW(nint hwnd,uint msg,nuint w,nint l);
    [DllImport("user32")] internal static extern nint LoadCursorW(nint module,nint id);
    [DllImport("user32")] internal static extern int ShowWindow(nint hwnd,int cmd);
    [DllImport("user32")] internal static extern int DestroyWindow(nint hwnd);
    [DllImport("user32")] internal static extern nint BeginPaint(nint hwnd,out Paint paint);
    [DllImport("user32")] internal static extern int EndPaint(nint hwnd,in Paint paint);
    [DllImport("user32")] internal static extern int InvalidateRect(nint hwnd,nint rect,int erase);
    [DllImport("user32")] internal static extern int UpdateWindow(nint hwnd);
    [DllImport("user32")] internal static extern int GetClientRect(nint hwnd,out WinRect rect);
    [DllImport("user32")] internal static extern int GetWindowRect(nint hwnd,out WinRect rect);
    [DllImport("user32")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] internal static extern int SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32")] internal static extern nint SetFocus(nint hwnd);
    [DllImport("user32")] internal static extern nint GetFocus();
    [DllImport("user32")] internal static extern nint GetActiveWindow();
    [DllImport("user32")] internal static extern nint GetForegroundWindow();
    [DllImport("user32")] internal static extern nint SendMessageW(nint hwnd,uint message,nuint w,nint l);
    [DllImport("user32",CharSet=CharSet.Unicode)] internal static extern int GetWindowTextW(nint hwnd,char* text,int chars);
    [DllImport("user32",CharSet=CharSet.Unicode)] internal static extern int SetWindowTextW(nint hwnd,string text);
    [DllImport("user32")] internal static extern int PeekMessageW(out Msg msg,nint hwnd,uint min,uint max,uint remove);
    [DllImport("user32")] internal static extern int GetMessageW(out Msg msg,nint hwnd,uint min,uint max);
    [DllImport("user32")] internal static extern int TranslateMessage(in Msg msg);
    [DllImport("user32")] internal static extern nint DispatchMessageW(in Msg msg);
    [DllImport("user32")] internal static extern int IsDialogMessageW(nint hwnd,ref Msg msg);
    [DllImport("user32")] internal static extern uint MsgWaitForMultipleObjectsEx(uint count,nint handles,uint timeout,uint mask,uint flags);
    [DllImport("user32")] internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32")] internal static extern uint GetGuiResources(nint process,uint flags);
    [DllImport("user32")] internal static extern int IsWindow(nint hwnd);
    [DllImport("user32")] internal static extern int EnumDesktopWindows(nint desktop,delegate* unmanaged[Stdcall]<nint,nint,int> callback,nint state);
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out uint process);
    [DllImport("user32")] internal static extern int GetClassNameW(nint hwnd,char* cls,int size);
    [DllImport("user32")] internal static extern int GetSystemMetrics(int i);
    [DllImport("user32")] internal static extern int EnumDisplayMonitors(nint dc,nint clip,delegate* unmanaged[Stdcall]<nint,nint,WinRect*,nint,int> callback,nint state);
    [DllImport("shcore")] internal static extern int GetDpiForMonitor(nint monitor,int kind,out uint x,out uint y);
    [DllImport("user32")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32")] internal static extern int GetKeyboardLayoutList(int count,nint* list);
    [DllImport("imm32")] internal static extern nint ImmGetContext(nint hwnd);
    [DllImport("imm32")] internal static extern int ImmReleaseContext(nint hwnd,nint context);
    [DllImport("imm32")] internal static extern int ImmIsIME(nint layout);
    [DllImport("imm32")] internal static extern int ImmSetCompositionStringW(nint context,uint index,char* text,uint bytes,nint reading,uint readingBytes);
    [DllImport("imm32")] internal static extern int ImmGetCompositionStringW(nint context,uint index,char* text,uint bytes);
    [DllImport("imm32")] internal static extern int ImmNotifyIME(nint context,uint action,uint index,uint value);
    [DllImport("ole32")] internal static extern int OleInitialize(nint reserved);
    [DllImport("ole32")] internal static extern void OleUninitialize();
    [DllImport("comctl32")] internal static extern int InitCommonControlsEx(in Controls init);
    [DllImport("comctl32")] internal static extern int SetWindowSubclass(nint hwnd,nint proc,nuint id,nuint data);
    [DllImport("comctl32")] internal static extern int RemoveWindowSubclass(nint hwnd,nint proc,nuint id);
    [DllImport("comctl32")] internal static extern nint DefSubclassProc(nint hwnd,uint message,nuint w,nint l);
    [DllImport("gdi32")] internal static extern nint GetStockObject(int i);
    [DllImport("gdi32",SetLastError=true)] internal static extern nint CreateCompatibleDC(nint src);
    [DllImport("gdi32",SetLastError=true)] internal static extern nint CreateDIBSection(nint dc,in BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32")] internal static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32")] internal static extern int DeleteDC(nint dc);
    [DllImport("gdi32")] internal static extern int DeleteObject(nint h);
    [DllImport("user32",SetLastError=true)] internal static extern int UpdateLayeredWindow(nint hwnd,nint destDc,in Pt dest,in Pt size,nint dc,in Pt src,uint key,in Blend blend,uint flags);
    internal static nint Handle(nint h,string name) => h != 0 ? h : throw new Win32Exception(Marshal.GetLastPInvokeError(),name);
    internal static void Ok(int ok,string name) { if (ok==0) throw new Win32Exception(Marshal.GetLastPInvokeError(),name); }
}
