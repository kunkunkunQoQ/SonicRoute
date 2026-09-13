using SonicRoute.Core;

string mode = args.Length > 0 ? args[0] : "";
int pid = args.Length > 1 ? int.Parse(args[1]) : 0;

if (mode == "mutecheck" && pid > 0)
{
    SessionVolumeService.Refresh(true);
    Console.WriteLine($"pid={pid} muted={SessionVolumeService.IsMuted(pid)} vol={SessionVolumeService.GetVolumePercent(pid)} has={SessionVolumeService.HasSession(pid)}");
}
else if (mode == "apps")
{
    foreach (var a in AudioService.GetApps(true))
        Console.WriteLine($"pid={a.ProcessId} proc={a.ProcessName} label={a.DisplayName}");
}
else if (mode == "fg")
{
    var fg = ForegroundAppService.GetForegroundProcessId();
    Console.WriteLine($"fgpid={fg}");
}
else if (mode == "fgmatch")
{
    int f = ForegroundAppService.GetForegroundProcessId();
    var apps = AudioService.GetApps(true);
    var hit = apps.FirstOrDefault(a => a.ProcessId == (uint)f);
    Console.WriteLine($"fgpid={f} inapps={(hit != null)} {(hit != null ? $"-> pid={hit.ProcessId} proc={hit.ProcessName}" : "")} totalapps={apps.Count}");
}
else if (mode == "toggle")
{
    SessionVolumeService.Refresh(true);
    bool before = SessionVolumeService.IsMuted(pid);
    bool after = SessionVolumeService.ToggleMute(pid);
    bool afterState = SessionVolumeService.IsMuted(pid);
    Console.WriteLine($"pid={pid} before={before} toggleret={after} afterState={afterState}");
}
else if (mode == "inmutecheck" && pid > 0)
{
    SessionVolumeService.Refresh(true);
    Console.WriteLine($"pid={pid} hasInput={SessionVolumeService.HasInputSession(pid)} inputMuted={SessionVolumeService.IsInputMuted(pid)}");
}
else if (mode == "inmute" && pid > 0)
{
    SessionVolumeService.Refresh(true);
    bool before = SessionVolumeService.IsInputMuted(pid);
    var r = SessionVolumeService.ToggleInputMuteChecked(pid);
    bool after = SessionVolumeService.IsInputMuted(pid);
    Console.WriteLine($"pid={pid} before={before} retMuted={r.Muted} applied={r.Applied} after={after}");
}
else if (mode == "gmiccheck")
{
    Console.WriteLine($"allMicsMuted={GlobalMicMuteService.IsMuted()}");
}
else if (mode == "gmic")
{
    bool before = GlobalMicMuteService.IsMuted();
    bool after = GlobalMicMuteService.Toggle();
    bool real = GlobalMicMuteService.IsMuted();
    Console.WriteLine($"before={before} ret={after} real={real}");
}
