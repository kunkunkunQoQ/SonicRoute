param([string]$Framework = 'net10.0-windows', [string]$DotNetHost = '', [string]$OutputDirectory = '', [switch]$Followup, [switch]$Optimization)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('bin/output-popup/' + $Framework) }
$resultDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resultDirectory) | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class OutputPopupLauncher {
 [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Start {
  public int Size; public string Reserved, Desktop, Title;
  public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags; public ushort Show,ReservedSize;
  public IntPtr ReservedData,Input,Output,Error;
 }
 [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public uint Pid,Tid; }
 [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr attributes);
 [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr environment,string cwd,ref Start start,out ProcessInfo process);
 [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
 [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
 [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr handle,uint code);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 public static uint Run(string app,string arguments,string cwd) {
  string name="SonicRoute-OutputChecks-"+Guid.NewGuid().ToString("N");
  IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x1ff,IntPtr.Zero);
  if(desktop==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
  ProcessInfo process=default(ProcessInfo);
  try {
   var start=new Start {Size=Marshal.SizeOf(typeof(Start)),Desktop="WinSta0\\"+name,Flags=1,Show=0};
   if(!CreateProcess(app,new StringBuilder("\""+app+"\" "+arguments),IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,cwd,ref start,out process)) throw new Win32Exception(Marshal.GetLastWin32Error());
   if(WaitForSingleObject(process.Process,45000)!=0) {
    TerminateProcess(process.Process,124); WaitForSingleObject(process.Process,5000);
    throw new TimeoutException("Private popup check timed out; test process "+process.Pid);
   }
   uint code; GetExitCodeProcess(process.Process,out code); return code;
  } finally {
   if(process.Thread!=IntPtr.Zero) CloseHandle(process.Thread);
   if(process.Process!=IntPtr.Zero) CloseHandle(process.Process);
   CloseDesktop(desktop);
  }
 }
}
'@
$modeArguments = if ($Followup) { '--output-popup-live --output-followup-live' } else { '--output-popup-live' }
if ($Optimization) { $modeArguments += ' --output-optimization-live' }
if ($Framework -eq 'net48') {
    $hostPath = Join-Path $PSScriptRoot 'bin/Release/net48/PanelRemovalChecks.exe'
    $arguments = $modeArguments + ' "' + $resultDirectory + '"'
} else {
    if (!$DotNetHost) { $DotNetHost = (Get-Command dotnet -ErrorAction Stop).Source }
    $hostPath = $DotNetHost
    $assembly = Join-Path $PSScriptRoot "bin/Release/$Framework/PanelRemovalChecks.dll"
    $arguments = '"' + $assembly + '" ' + $modeArguments + ' "' + $resultDirectory + '"'
}
$code = [OutputPopupLauncher]::Run($hostPath, $arguments, $sourceRoot)
Get-Content -LiteralPath (Join-Path $resultDirectory 'console.log') -Tail 18
exit $code
