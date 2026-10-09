using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread,uint target,bool attach);
    [DllImport("user32.dll")] static extern bool LockSetForegroundWindow(uint code);
    sealed class TargetForm : Form
    {
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x8001)
            {
                LockSetForegroundWindow(2);
                uint fg=GetWindowThreadProcessId(GetForegroundWindow(),out _), own=GetCurrentThreadId();
                bool attached=fg!=0 && fg!=own && AttachThreadInput(own,fg,true);
                try { SetForegroundWindow(m.WParam); } finally { if(attached) AttachThreadInput(own,fg,false); }
                return;
            }
            if(m.Msg==0x8002) { LockSetForegroundWindow(1); return; }
            base.WndProc(ref m);
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct Format { public ushort tag, channels; public uint rate, bytes; public ushort align, bits, extra; }
    [StructLayout(LayoutKind.Sequential)] struct Header { public IntPtr data; public uint length, recorded; public IntPtr user; public uint flags, loops; public IntPtr next, reserved; }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr handle, uint device, ref Format format, IntPtr cb, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr handle);
    [STAThread] static void Main()
    {
        var format = new Format { tag=1, channels=1, rate=48000, bytes=96000, align=2, bits=16 };
        if(waveOutOpen(out var audio, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0)!=0) return;
        var data=Marshal.AllocHGlobal(96000); Marshal.Copy(new byte[96000],0,data,96000);
        var hdr=Marshal.AllocHGlobal(Marshal.SizeOf<Header>()); var size=(uint)Marshal.SizeOf<Header>();
        Marshal.StructureToPtr(new Header { data=data, length=96000 },hdr,false);
        waveOutPrepareHeader(audio,hdr,size); waveOutWrite(audio,hdr,size);
        using var timer=new Timer { Interval=40 };
        timer.Tick+=(_,_)=> { if((Marshal.PtrToStructure<Header>(hdr).flags&1)!=0) waveOutWrite(audio,hdr,size); };
        timer.Start();
        using var form=new TargetForm { Text="SonicRoute foreground test "+Environment.ProcessId, Left=-10000, Top=-10000, Width=320, Height=120, StartPosition=FormStartPosition.Manual, ShowInTaskbar=true };
        try { Application.Run(form); }
        finally { timer.Stop(); waveOutReset(audio); waveOutUnprepareHeader(audio,hdr,size); waveOutClose(audio); Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data); }
    }
}
