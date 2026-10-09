using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

internal static partial class Program
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NativeNotify(IntPtr self,IntPtr data);
    static void RunMicChecks()
    {
        using(var monitor=new MicrophoneStateMonitor())
        {
            bool old=GlobalMicMuteService.IsAnyMuted(false); var task=monitor.ReadAsync(false); WaitTask(task);
            BAssert(task.Result==old,"real-device-state-matches-old-aggregation");
            BAssert((bool)UMField(monitor,"_deviceRegistered"),"native-IMMNotificationClient-registration-succeeded");
            int count=((ICollection)UMField(monitor,"_volumes")).Count;
            int registered=(int)UMField(monitor,"_registered").GetType().GetProperty("Count")!.GetValue(UMField(monitor,"_registered"))!;
            BAssert(count==registered,"native-volume-callback-registrations-succeeded");
            int signals=0; monitor.Invalidated+=()=>signals++;
            var callback=UMField(monitor,"_volumeCallback");
            IntPtr ptr=Marshal.GetComInterfaceForObject(callback,typeof(IAudioEndpointVolumeCallback));
            try
            {
                var vtable=Marshal.ReadIntPtr(ptr); var method=Marshal.ReadIntPtr(vtable,3*IntPtr.Size);
                var notify=Marshal.GetDelegateForFunctionPointer<NativeNotify>(method);
                BAssert(notify(ptr,IntPtr.Zero)==0 && signals==1,"unmanaged-volume-callback-vtable-round-trip");
            }
            finally { Marshal.Release(ptr); }
            var oldMs=new List<double>(); var newMs=new List<double>();
            for(int i=0;i<3;i++)
            {
                var sw=Stopwatch.StartNew(); for(int k=0;k<60;k++) GlobalMicMuteService.IsAnyMuted(k%2==0); oldMs.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); for(int k=0;k<60;k++) monitor.ReadAsync(k%2==0).GetAwaiter().GetResult(); newMs.Add(sw.Elapsed.TotalMilliseconds);
            }
            Add(new {Case="mic-read-reuse-benchmark",Endpoints=count,Registered=registered,Old60ReadsMs=oldMs,New60ReadsMs=newMs});
            var captures=(List<IAudioEndpointVolume>)UMField(monitor,"_capture");
            var one=new FakeVolume(); var two=new FakeVolume();
            captures.Clear(); captures.Add(one); captures.Add(two); UMSet(monitor,"_default",one);
            UMSet(monitor,"_dirty",0);
            BAssert(!monitor.ReadAsync(false).Result,"mixed-unmuted-captures-not-globally-muted");
            one.Muted=1;
            BAssert(!monitor.ReadAsync(false).Result && monitor.ReadAsync(true).Result,"track-default-input-preserves-aggregation-semantics");
            two.Muted=1; BAssert(monitor.ReadAsync(false).Result,"all-capture-endpoints-muted-is-global-mute");
            captures.Clear(); UMSet(monitor,"_default",null!);
            BAssert(!monitor.ReadAsync(true).Result,"no-capture-endpoints-is-unmuted");
            // 设备通知只标记 dirty；实际 COM 重建由下一次后台读取执行。
            var deviceCallback=UMField(monitor,"_deviceCallback");
            deviceCallback.GetType().GetMethod("OnDeviceAdded")!.Invoke(deviceCallback,new object[]{"controlled-device"});
            BAssert((int)UMField(monitor,"_dirty")==1,"device-notification-invalidates-without-blocking-com-work");
            WaitTask(monitor.ReadAsync(false));
            BAssert(((ICollection)UMField(monitor,"_volumes")).Count==count,"topology-invalidated-endpoints-rebuilt");
            monitor.Dispose(); WaitTask(monitor.Completion);
            BAssert(((ICollection)UMField(monitor,"_volumes")).Count==0,"dispose-unregisters-and-releases-endpoints");
        }
    }
    sealed class FakeVolume : IAudioEndpointVolume
    {
        public int Muted;
        public int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback)=>0;
        public int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback)=>0;
        public int GetChannelCount(out uint count) {count=1;return 0;}
        public int SetMasterVolumeLevel(float value,ref Guid context)=>throw new Exception("No writes");
        public int SetMasterVolumeLevelScalar(float value,ref Guid context)=>throw new Exception("No writes");
        public int GetMasterVolumeLevel(out float value) {value=0;return 0;}
        public int GetMasterVolumeLevelScalar(out float value) {value=0;return 0;}
        public int SetChannelVolumeLevel(uint channel,float value,ref Guid context)=>throw new Exception("No writes");
        public int SetChannelVolumeLevelScalar(uint channel,float value,ref Guid context)=>throw new Exception("No writes");
        public int GetChannelVolumeLevel(uint channel,out float value) {value=0;return 0;}
        public int GetChannelVolumeLevelScalar(uint channel,out float value) {value=0;return 0;}
        public int SetMute(int value,ref Guid context)=>throw new Exception("No writes");
        public int GetMute(out int value) {value=Muted;return 0;}
    }
}
