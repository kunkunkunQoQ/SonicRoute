param([Parameter(Mandatory)][int]$TargetPid, [Parameter(Mandatory)][string]$OutFile, [int]$Seconds=5)
$ErrorActionPreference='Stop'
if(!('PhaseOneMetrics' -as [type])){
Add-Type @"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class PhaseOneMetrics {
    [StructLayout(LayoutKind.Sequential)] public struct Memory {
        public uint Size,Faults;
        public UIntPtr PeakWorkingSet,WorkingSet,PeakPagePool,PagePool,PeakNonPagePool,NonPagePool,Pagefile,PeakPagefile,PrivateBytes,PrivateWorkingSet,SharedCommit;
    }
    [DllImport("psapi.dll",SetLastError=true)] static extern bool GetProcessMemoryInfo(IntPtr h,out Memory data,uint cb);
    public static Memory Read(Process p) { Memory m; if (!GetProcessMemoryInfo(p.Handle,out m,(uint)Marshal.SizeOf<Memory>())) throw new System.ComponentModel.Win32Exception(); return m; }
    delegate bool Visitor(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(Visitor f,IntPtr arg);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h,uint message,UIntPtr w,IntPtr l,uint flags,uint ms,out UIntPtr result);
    public static double[] MessageLatency(int pid) {
        IntPtr target=IntPtr.Zero; EnumWindows((h,a)=>{int p;GetWindowThreadProcessId(h,out p); if(p==pid&&(target==IntPtr.Zero||IsWindowVisible(h)))target=h;return true;},IntPtr.Zero);
        var times=new List<double>(); if(target!=IntPtr.Zero) for(int i=0;i<12;i++){var watch=Stopwatch.StartNew();UIntPtr value; if(SendMessageTimeout(target,0,UIntPtr.Zero,IntPtr.Zero,2,500,out value)==IntPtr.Zero)break;times.Add(watch.Elapsed.TotalMilliseconds);}return times.ToArray();
    }
}
"@
}
$target=Get-Process -Id $TargetPid
$exe=$target.Path
$hash=(Get-FileHash -LiteralPath $exe).Hash
$gpu=@()
$gpuError=$null
try{$gpu=@(Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUProcessMemory | Where-Object {$_.Name -like ('pid_'+$TargetPid+'_*')})}catch{$gpuError=$_.Exception.Message}
$target.Refresh()
$cpu=$target.TotalProcessorTime
$clock=[Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $Seconds
$target.Refresh()
$wall=$clock.Elapsed.TotalSeconds
$cpuMs=($target.TotalProcessorTime-$cpu).TotalMilliseconds
$memory=[PhaseOneMetrics]::Read($target)
$latency=@([PhaseOneMetrics]::MessageLatency($TargetPid))
$sample=[ordered]@{Utc=[DateTime]::UtcNow.ToString('o');Pid=$TargetPid;File=$exe;SHA256=$hash;FileVersion=$target.MainModule.FileVersionInfo.FileVersion;DurationSeconds=$wall;CpuMs=$cpuMs;CpuPercentOfOneCore=($cpuMs/($wall*10));PrivateBytes=$memory.PrivateBytes.ToUInt64();WorkingSet=$memory.WorkingSet.ToUInt64();PrivateWorkingSet=$memory.PrivateWorkingSet.ToUInt64();Threads=$target.Threads.Count;Handles=$target.HandleCount;GpuInstances=@($gpu|ForEach-Object {[ordered]@{Name=$_.Name;DedicatedBytes=$_.DedicatedUsage;SharedBytes=$_.SharedUsage}});GpuReadError=$gpuError;MessageRoundTripsMs=$latency}
$sample|ConvertTo-Json -Depth 5|Set-Content -Encoding utf8 -LiteralPath $OutFile
