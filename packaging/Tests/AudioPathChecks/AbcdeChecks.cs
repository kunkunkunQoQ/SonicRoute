using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace AudioPathChecks;
internal static partial class Program
{
#if !BASELINE
    private static void RunAbcdeChecks()
    {
        Case("playback demand scans no recording devices and first recording demand remains fresh", () =>
        {
            var render = new Session(801); var capture = new Session(802) { Muted = true };
            Boundary.Add(EDataFlow.eRender, render); Boundary.Add(EDataFlow.eCapture, capture);
            Assert(SessionVolumeService.ReadStateChecked(801).MuteKnown && Boundary.Scans == 1, "playback scanned capture");
            Assert(SessionVolumeService.ReadInputState(802).Muted && Boundary.Scans == 2, "first recording request not scanned");
            for (int i = 0; i < 10; i++) SessionVolumeService.ReadStateChecked(801);
            Assert(Boundary.Scans == 2, "fresh playback repeated discovery");
            Console.WriteLine("SCOPED_SCAN_METRIC playbackFlows=1 firstCaptureFlows=1 repeatedPlaybackFlows=0");
        });
        Case("volume remains known when mute read fails and toggle performs no blind write", () =>
        {
            var session = new Session(803) { ReadFailures = 20, Volume = .42f }; Boundary.Add(EDataFlow.eRender, session);
            var state = SessionVolumeService.ReadStateChecked(803);
            Assert(state.Volume == 42 && !state.MuteKnown, "unknown mute hidden by known volume");
            Assert(!SessionVolumeService.ToggleMuteChecked(803).Applied && session.Writes == 0, "unknown toggle wrote audio");
            session.ReadFailures = 0; session.Muted = true;
            Assert(SessionVolumeService.ReadStateChecked(803).MuteKnown && SessionVolumeService.ReadStateChecked(803).Muted, "mute recovery lost");
        });
        Case("checked playback toggle rejects ignored writes and reports confirmed actual value", () =>
        {
            var session = new Session(804) { IgnoreWrite = true }; Boundary.Add(EDataFlow.eRender, session);
            Assert(!SessionVolumeService.ToggleMuteChecked(804).Applied && !session.Muted, "ignored mute accepted");
            Assert(!SessionVolumeService.SetVolumePercentChecked(804, 17).Applied && session.Volume == .6f, "ignored volume accepted");
        });
        Case("panel snapshot distinguishes unknown row and global mute from unmuted", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(806) { Volume = .42f, ReadFailures = 20 });
            var panel = SessionVolumeService.ReadPanelStateChecked(new[] { 806 });
            Assert(panel.Apps[806].Volume == 42 && !panel.Apps[806].MuteKnown && !panel.AllMuted.HasValue,
                "panel converted failed mute reads to unmuted");
        });
        Case("refresh replaces discovery without releasing an interface borrowed by a slow read", () =>
        {
            var session = new Session(805); var device = Boundary.Add(EDataFlow.eRender, session);
            SessionVolumeService.RefreshPlayback(true);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int oldReleases = 0;
            Boundary.OnRelease = value => { if (ReferenceEquals(value, session)) Interlocked.Increment(ref oldReleases); };
            session.OnReadVolume = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); };
            var read = Task.Run(() => SessionVolumeService.GetVolumePercent(805));
            try
            {
                Assert(entered.Wait(3000), "read did not enter driver"); device.Sessions = Array.Empty<Session>();
                var refresh = Task.Run(() => SessionVolumeService.RefreshPlayback(true));
                Assert(refresh.Wait(3000), "refresh blocked behind native read");
                Assert(oldReleases == 0, "borrowed interface released early");
            }
            finally { release.Set(); }
            Assert(read.Result == 60 && oldReleases == 1, "final lease did not release exactly once");
        });
        Case("endpoint partial read preserves independent mute validity", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); device.Volume.ReadFailures = 1;
            var state = SystemVolumeService.ReadStateChecked(device.Id);
            Assert(state.Volume == 50 && !state.MuteKnown, "endpoint mute failure became unmuted");
        });
    }
#endif
}
