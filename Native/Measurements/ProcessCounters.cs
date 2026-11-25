using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

public static class SonicRouteProcessMetrics
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public uint Size, Faults;
        public UIntPtr PeakWorkingSet, WorkingSet, PeakPagePool, PagePool, PeakNonPagePool, NonPagePool;
        public UIntPtr PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }

    [DllImport("psapi.dll", SetLastError=true)]
    private static extern bool GetProcessMemoryInfo(IntPtr handle, out Memory memory, uint size);

    public static Memory ReadMemory(Process process)
    {
        Memory memory;
        if (!GetProcessMemoryInfo(process.Handle, out memory, (uint)Marshal.SizeOf<Memory>()))
            throw new Win32Exception();
        return memory;
    }

    [DllImport("user32.dll", SetLastError=true)] public static extern uint GetGuiResources(IntPtr processHandle, uint type);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern IntPtr FindWindowW(string className, string windowTitle);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", SetLastError=true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)] private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd,uint message,UIntPtr w,IntPtr l,uint flags,uint ms,out UIntPtr result);

    public static long SampleWpf(int expectedPid, string instance, string edition, uint command)
    {
        Guid token;
        if (!instance.StartsWith("PerfWpf_", StringComparison.Ordinal) || !Guid.TryParseExact(instance.Substring(8), "N", out token))
            throw new ArgumentException("Only an isolated WPF measurement fixture is valid.");
        string title = "SonicRoute_" + instance + (edition == "Full" ? "_Full" : "") + "_ActivateSink";
        IntPtr window = FindWindowW(null, title);
        int pid;
        GetWindowThreadProcessId(window, out pid);
        if (window == IntPtr.Zero || pid != expectedPid) throw new InvalidOperationException("Fixture window is unavailable.");
        uint message = RegisterWindowMessageW(instance + ".sample");
        UIntPtr result;
        if (message == 0 || SendMessageTimeoutW(window, message, (UIntPtr)command, IntPtr.Zero, 2, 3000, out result)==IntPtr.Zero)
            throw new Win32Exception();
        return (long)result.ToUInt64();
    }
}

// Raw Thread counters accumulate switches. A pair of PDH samples avoids a continuous
// trace and works under a standard user account. Report thread births/deaths separately.
public sealed class SonicRouteThreadCounters : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RawCounter
    {
        public uint Status, TimeStampLo, TimeStampHi;
        public long First, Second;
        public uint MultiCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Item {public IntPtr Name; public RawCounter Raw;}
    [DllImport("pdh.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern uint PdhOpenQueryW(string path, UIntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern uint PdhAddEnglishCounterW(IntPtr query,string path,UIntPtr user,out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling=true)] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet=CharSet.Unicode, ExactSpelling=true)] private static extern uint PdhGetRawCounterArrayW(IntPtr counter,ref uint size, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll", ExactSpelling=true)] private static extern uint PdhCloseQuery(IntPtr query);
    private IntPtr query;
    private readonly IntPtr switches, processes, threads;
    public SonicRouteThreadCounters()
    {
        Check(PdhOpenQueryW(null, UIntPtr.Zero, out query));
        try
        {
            processes=Add("ID Process");
            threads=Add("ID Thread");
            switches=Add("Context Switches/sec");
        }
        catch { Dispose(); throw; }
    }
    private IntPtr Add(string name)
    {
        IntPtr counter;
        Check(PdhAddEnglishCounterW(query, @"\Thread(*)\" + name, UIntPtr.Zero, out counter));
        return counter;
    }
    private static void Check(uint code)
    {
        if(code!=0) throw new InvalidOperationException("PDH returned 0x" + code.ToString("X8"));
    }
    private static Dictionary<string,long> Read(IntPtr counter)
    {
        uint size=0, count=0;
        uint status=PdhGetRawCounterArrayW(counter,ref size,ref count,IntPtr.Zero);
        if(status!=0x800007D2) Check(status);
        IntPtr buffer=Marshal.AllocHGlobal(checked((int)size));
        try
        {
            Check(PdhGetRawCounterArrayW(counter,ref size,ref count,buffer));
            var result=new Dictionary<string,long>(StringComparer.Ordinal);
            int step=Marshal.SizeOf<Item>();
            for(int i=0;i<count;i++)
            {
                var entry=Marshal.PtrToStructure<Item>(IntPtr.Add(buffer,checked(i*step)));
                if(entry.Raw.Status<=1) result[Marshal.PtrToStringUni(entry.Name)]=entry.Raw.First;
            }
            return result;
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    public Dictionary<int,long> Sample(int pid)
    {
        Check(PdhCollectQueryData(query));
        var byProcess=Read(processes);
        var byThread=Read(threads);
        var bySwitches=Read(switches);
        var result=new Dictionary<int,long>();
        foreach(var pair in byProcess)
        {
            long thread, value;
            if(pair.Value==pid && byThread.TryGetValue(pair.Key,out thread) && bySwitches.TryGetValue(pair.Key,out value))
                result[checked((int)thread)]=value;
        }
        if(result.Count==0) throw new InvalidOperationException("No per-thread context switch counters for the target.");
        return result;
    }
    public void Dispose(){if(query!=IntPtr.Zero){PdhCloseQuery(query); query=IntPtr.Zero;}}
}
