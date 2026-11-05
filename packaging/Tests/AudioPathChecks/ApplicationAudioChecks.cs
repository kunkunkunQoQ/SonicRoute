#if !BASELINE
using System.Threading;
using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace AudioPathChecks;

internal static partial class Program
{
    private static void RunApplicationAudioChecks()
    {
        Case("shared toggle reads each target state once and writes every session", () =>
        {
            var first = new Session(41); var second = new Session(42);
            var sibling = new Session(41);
            Boundary.Add(EDataFlow.eRender, first, second, sibling);
            SessionVolumeService.Refresh(true);
            int reads = 0; first.OnReadVolume = second.OnReadVolume = () => reads++;
            var result = ApplicationAudio.ToggleAllMuteChecked();
            Assert(result == (true, true) && first.Muted && second.Muted && sibling.Muted, "all sessions muted");
            Assert(reads == 2 && first.MuteReads == 1 && second.MuteReads == 1 && sibling.MuteReads == 0,
                $"one volume/mute read per app: volume={reads}, mute={first.MuteReads + second.MuteReads}");
            Assert(first.Writes == 1 && second.Writes == 1 && sibling.Writes == 1, "one write per session");
            Console.WriteLine($"METRIC batch-toggle apps=2 volumeReads={reads} muteReads={first.MuteReads + second.MuteReads} writes=3");
        });
        Case("shared toggle unmutes only after confirmed all-muted state", () =>
        {
            var first = new Session(41) { Muted = true }; var second = new Session(42) { Muted = true };
            Boundary.Add(EDataFlow.eRender, first, second);
            Assert(ApplicationAudio.ToggleAllMuteChecked() == (false, true) && !first.Muted && !second.Muted, "unmute all");
        });
        Case("unknown mute state never fabricates all-muted", () =>
        {
            var session = new Session(41) { Muted = true, ReadFailures = 10 };
            Boundary.Add(EDataFlow.eRender, session);
            var result = ApplicationAudio.ToggleAllMuteChecked();
            Assert(result == (true, true) && session.Muted, "unknown state must choose mute, not unmute");
        });
        Case("explicit mute preserves partial failure counts", () =>
        {
            var first = new Session(41); var second = new Session(42) { WriteFails = true };
            Boundary.Add(EDataFlow.eRender, first, second);
            var result = ApplicationAudio.ChangeAllPlaybackMuteChecked(true);
            Assert(result == (1, 2) && first.Muted && !second.Muted, "one failed target remains visible");
        });
        Case("empty batch does not report an applied toggle", () =>
        {
            Assert(ApplicationAudio.ToggleAllMuteChecked() == (true, false), "empty target set");
        });
        Case("pre-cancelled batch never enumerates or writes", () =>
        {
            var session = new Session(41); Boundary.Add(EDataFlow.eRender, session);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            bool cancelled = false;
            try { ApplicationAudio.ChangeAllPlaybackMuteChecked(true, stop.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && session.Writes == 0 && Boundary.Scans == 0, "cancel before target enumeration");
        });
    }
}
#endif
