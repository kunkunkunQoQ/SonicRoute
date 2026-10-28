using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace AudioPathChecks;
internal static partial class Program
{
#if !BASELINE
    private static void RunPanelSnapshotChecks()
    {
        Case("panel shares one enumeration and includes hidden playback sessions", () =>
        {
            var shown = new Session(710) { Volume = .31f, Muted = true };
            var hidden = new Session(711) { Muted = false };
            Boundary.Add(EDataFlow.eRender, shown, hidden);
            SessionVolumeService.Refresh(); SessionVolumeService.ReadState(710); SessionVolumeService.AllMuted();
            int previous = Boundary.Enumerators;
            Boundary.Reset(); Boundary.Add(EDataFlow.eRender, shown, hidden);
            var state = SessionVolumeService.ReadPanelState(new[] { 710 });
            Assert(state.Apps.Count == 1 && state.Apps[710] == (31, true) && !state.AllMuted, "hidden session omitted");
            Assert(previous == 2 && Boundary.Enumerators == 1 && Boundary.Scans == 2 && shown.Writes + hidden.Writes == 0, "extra enumeration/write");
            Console.WriteLine("PANEL_SCAN_METRIC coldPrevious=2 coldCurrent=1 flows=2 shown=1 hidden=1 writes=0");
        });
        Case("panel keeps first-session row semantics and all-session global semantics", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(712) { Volume = .22f, Muted = true }, new Session(712) { Muted = false });
            Boundary.Add(EDataFlow.eCapture, new Session(712) { Muted = true });
            var state = SessionVolumeService.ReadPanelState(new[] { 712, 712, 999999 });
            Assert(state.Apps.Count == 2 && state.Apps[712] == (22, true) && state.Apps[999999] == (-1, false)
                && !state.AllMuted && Boundary.Enumerators == 1, "mixed/missing session semantics");
        });
        Case("panel recording state cannot change global playback mute", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(713) { Muted = true });
            Boundary.Add(EDataFlow.eCapture, new Session(714) { Muted = false });
            Assert(SessionVolumeService.ReadPanelState(new[] { 713 }).AllMuted, "recording session mixed into playback");
        });
        Case("panel retries failed state once and retains unknown values on persistent failure", () =>
        {
            var session = new Session(715) { VolumeReadFailures = 1 };
            Boundary.Add(EDataFlow.eRender, session);
            Assert(SessionVolumeService.ReadPanelState(new[] { 715 }).Apps[715].Volume == 60 && Boundary.Enumerators == 2, "retry missing");
            session.VolumeReadFailures = 10; Boundary.ClearCounts();
            Assert(SessionVolumeService.ReadPanelState(new[] { 715 }).Apps[715].Volume == -1 && Boundary.Enumerators == 2, "persistent failure loop/value");
        });
        Case("panel retries invalidated snapshot generation", () =>
        {
            var session = new Session(716);
            session.OnReadVolume = () => { session.OnReadVolume = null; SessionVolumeService.InvalidateCache(); };
            Boundary.Add(EDataFlow.eRender, session);
            SessionVolumeService.ReadPanelState(new[] { 716 });
            Assert(Boundary.Enumerators == 2, "invalidated snapshot accepted");
        });
        Case("actual panel reader skips routes when names off and returns shared global result", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(717) { Muted = true }, new Session(718) { Muted = false });
            var state = PanelRows.ReadSnapshot(new List<AudioAppInfo> { new() { ProcessId = 717, ProcessName = "panel" } }, false);
            Assert(state.States[717].muted && state.AllMuted == false && Boundary.Enumerators == 1 && Boundary.Policies == 0, "reader boundary");
        });
        Case("empty panel defers global read and creates no route policy", () =>
        {
            var state = PanelRows.ReadSnapshot(new List<AudioAppInfo>(), true);
            Assert(state.States.Count == 0 && state.AllMuted == null && Boundary.Enumerators == 0 && Boundary.Policies == 0, "empty reader became eager");
        });
    }
#endif
}
