using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

// External fixture boundaries only; production engine source is linked without edits.
namespace SonicRoute
{
    public sealed partial class App : System.Windows.Application
    {
        internal string? LastOsdText;
        public void ShowOsd(string title,string text) { LastOsdText=text; }
        internal readonly List<(string Target,int Volume,bool Muted)> VolumeNotices=new();
        internal void ShowVolumeOsd(string title,int volume,string text,bool muted=false,string? target=null) { VolumeNotices.Add((target??"",volume,muted)); }
        internal int MicNotices;
        public void ShowMicMuteOsd(string title,bool muted) { MicNotices++; }
        internal bool SetQuickPanelVisibility(bool visible)=>true;
    }
    internal static class L10n { internal static string T(string key)=>key; }
    internal sealed class AppRuntimeIdentity
    {
        internal static AppRuntimeIdentity Current { get; }=new();
        internal string CommandEdition=>"IsolatedAutomationCheck";
    }
    internal static class AutoRuleScheduler { internal static void Log(string text) { } }
    internal static class AudioService
    {
        internal const string SystemDefaultDeviceId="@@SYSTEM_DEFAULT@@";
        internal const string SystemDefaultInputDeviceId="@@SYSTEM_DEFAULT_IN@@";
        internal static readonly List<AudioDeviceInfo> Inputs=new();
        internal static readonly List<AudioDeviceInfo> Outputs=new();
        internal static readonly List<AudioAppInfo> Apps=new();
        internal static bool ThrowApps;
        internal static int AppReads;
        internal static int DeviceReads;
        internal static string? DefaultInput="mic-a";
        internal static string? DefaultOutput="speaker-a";
        internal static bool EndpointApplied;
        internal static readonly Dictionary<(int Pid,EDataFlow Flow),string?> Endpoints=new();
        internal static readonly List<(int Pid,EDataFlow Flow,string Id)> EndpointWrites=new();
        internal static bool ResetApplied=true;
        internal static EDataFlow? LastFlow;
        internal static bool IsSystemDefault(string? id)=>Core.AudioService.IsSystemDefault(id);
        internal static List<AudioDeviceInfo> GetDevices(EDataFlow flow){LastFlow=flow;DeviceReads++;return (flow==EDataFlow.eCapture?Inputs:Outputs).ToList();}
        internal static List<AudioAppInfo> GetApps(){AppReads++;if(ThrowApps)throw new InvalidOperationException("Simulated unavailable audio discovery");return Apps.ToList();}
        internal static string? GetDefaultDeviceId(EDataFlow flow){LastFlow=flow;return flow==EDataFlow.eCapture?DefaultInput:DefaultOutput;}
        internal static string? GetPersistedEndpoint(int pid,EDataFlow flow)=>Endpoints.TryGetValue((pid,flow),out var id)?id:null;
        internal static (int OutOk,int InOk,int Total,bool Applied) ResetAllPersistedEndpointsChecked()=>(1,1,1,ResetApplied);
        internal static (bool Success,int HResult,string Message) ApplyEndpoint(int pid,EDataFlow flow,string id)
        {
            EndpointWrites.Add((pid,flow,id));
            if(EndpointApplied)Endpoints[(pid,flow)]=IsSystemDefault(id)?null:AudioPolicyConfig.EnsureFullDeviceId(id,flow);
            return(EndpointApplied,EndpointApplied?0:-1,"fixture");
        }
    }
    internal static class SystemDefaultDeviceService
    {
        internal static readonly List<(EDataFlow Flow,string Id)> Writes=new();
        internal static bool Succeeds=true;
        internal static (bool Success,int HResult,string Message) SetDefault(EDataFlow flow,string id)
        {
            Writes.Add((flow,id));
            if(Succeeds){if(flow==EDataFlow.eCapture)AudioService.DefaultInput=id;else AudioService.DefaultOutput=id;}
            return(Succeeds,Succeeds?0:-1,"fixture");
        }
    }
    internal static class SystemVolumeService
    {
        internal static readonly List<(string? Id,bool Muted)> Writes=new();
        internal static bool Succeeds=true;
        internal static bool ToggleApplied=true;
        internal static bool VolumeReadable=true,VolumeMuted;
        internal static readonly Dictionary<string,bool> States=new();
        internal static readonly List<(string? Id,int Value,bool Relative)> Volumes=new();
        internal static (bool Muted,bool Applied) ToggleMuteChecked(string? id = null)
        {
            if(!ToggleApplied)return(false,false);
            bool target=!States.TryGetValue(id!,out bool current)||!current;
            Writes.Add((id,target));States[id!]=target;return(target,true);
        }
        internal static bool SetMute(string? id,bool muted){Writes.Add((id,muted));return Succeeds;}
        internal static bool IsMuted()=>false;
        internal static bool SetVolumePercent(string? id,int value){Volumes.Add((id,value,false));return Succeeds;}
        internal static (int Current,int Requested,int Actual,bool Applied) AdjustVolumePercent(string? id,int delta){Volumes.Add((id,delta,true));return(50,50+delta,50+delta,Succeeds);}
        internal static (int Current,int Requested,int Actual,bool Applied,bool Muted) AdjustVolumeWithState(string? id,int delta)
        {Volumes.Add((id,delta,true));return(VolumeReadable?50:-1,50+delta,VolumeReadable?Math.Max(0,Math.Min(100,50+delta)):-1,Succeeds,VolumeMuted);}
    }
    internal static class SessionVolumeService
    {
        internal static bool InputApplied=true;
        internal static (int Pid,bool? Muted) LastInput;
        internal static (int Succeeded,int Total) Batch=(2,2);
        internal static (int Value,bool Relative) LastVolume;
        internal static bool? LastMute;
        internal static (bool Muted,bool Applied) ChangeInputMuteChecked(int pid,bool? muted,CancellationToken token=default){token.ThrowIfCancellationRequested();LastInput=(pid,muted);return(muted??true,InputApplied);}
        internal static (bool Muted,bool Applied) ToggleInputMuteChecked(int pid)=>ChangeInputMuteChecked(pid,null);
        internal static (int Succeeded,int Total) ChangeAllPlaybackVolumeChecked(int value,bool relative,CancellationToken token=default){token.ThrowIfCancellationRequested();LastVolume=(value,relative);return Batch;}
        internal static (int Succeeded,int Total) ChangeAllPlaybackMuteChecked(bool? muted,CancellationToken token=default){token.ThrowIfCancellationRequested();LastMute=muted;return Batch;}
        internal static bool SetVolumePercent(int pid,int value)=>true;
        internal static (int Current,int Requested,int Actual,bool Applied) AdjustVolumePercent(int pid,int delta){AdjustTargets.Add((pid,delta));return(50,50+delta,50+delta,true);}
        internal static bool SetMute(int pid,bool mute)=>true;
        internal static readonly List<int> MuteTargets=new();
        internal static bool MuteApplied=true;
        internal static readonly List<(int Pid,int Delta)> AdjustTargets=new();
        internal static bool IsMuted(int pid)=>false;
        internal static (bool Muted,bool Applied) ToggleMuteChecked(int pid){MuteTargets.Add(pid);return(true,MuteApplied);}
    }
    internal static class GlobalMicMuteService
    {
        internal static bool Known=true,AllMuted,Applied=true;
        internal static readonly List<bool> Writes=new();
        internal readonly struct State
        {
            internal bool Known=>GlobalMicMuteService.Known;
            internal bool AllMuted=>GlobalMicMuteService.AllMuted;
        }
        internal static State ReadState()=>new();
        internal static bool Toggle()=>SetMuteChecked(!AllMuted).State.AllMuted;
        internal static (State State,bool Applied) SetMuteChecked(bool muted){Writes.Add(muted);if(Applied)AllMuted=muted;return(new(),Applied);}
    }
}
