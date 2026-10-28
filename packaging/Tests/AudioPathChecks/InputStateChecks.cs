using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace AudioPathChecks;

internal static partial class Program
{
#if !BASELINE
    private static void RunInputStateChecks()
    {
        Case("input state shares one discovery and reads only the selected first session", () =>
        {
            var first = new Session(710) { Muted = true };
            var second = new Session(710); var other = new Session(711);
            Boundary.Add(EDataFlow.eCapture, first, second, other);
            var state = SessionVolumeService.ReadInputState(710);
            Assert(state.Available && state.Muted && Boundary.Enumerators == 1, "state or discovery");
            Assert(first.MuteReads == 1 && second.MuteReads == 0 && other.MuteReads == 0, "wrong session read");
            Assert(first.Writes + second.Writes + other.Writes == 0, "read wrote audio");
            Assert(SessionVolumeService.IsInputMuted(710) && Boundary.Enumerators == 1 && first.MuteReads == 2,
                "existing read API did not reuse the fresh snapshot");
        });
        Case("missing input PID reuses complete negative discovery", () =>
        {
            var other = new Session(712); Boundary.Add(EDataFlow.eCapture, other);
            Assert(!SessionVolumeService.ReadInputState(713).Available, "missing PID available");
            Assert(!SessionVolumeService.ReadInputState(713).Muted && Boundary.Enumerators == 1
                && other.MuteReads == 0 && other.Writes == 0, "negative snapshot or isolation");
        });
        Case("input state retries one failed mute read", () =>
        {
            var session = new Session(714) { Muted = true, ReadFailures = 1 };
            Boundary.Add(EDataFlow.eCapture, session);
            var state = SessionVolumeService.ReadInputState(714);
            Assert(state.Available && state.Muted && session.MuteReads == 2 && Boundary.Enumerators == 2
                && session.Writes == 0, "retry changed");
        });
        Case("persistent input read failure retains session presence and old muted fallback", () =>
        {
            var session = new Session(715) { Muted = true, ReadFailures = 10 };
            Boundary.Add(EDataFlow.eCapture, session);
            var state = SessionVolumeService.ReadInputState(715);
            Assert(state.Available && !state.Muted && session.MuteReads == 2 && Boundary.Enumerators == 2,
                "failure fallback or retry count");
        });
        Case("input state uses one snapshot when a failed session disappears", () =>
        {
            var session = new Session(716) { ReadFailures = 1 };
            var device = Boundary.Add(EDataFlow.eCapture, session);
            session.OnReadMute = () => device.Sessions = Array.Empty<Session>();
            var state = SessionVolumeService.ReadInputState(716);
            Assert(!state.Available && !state.Muted && Boundary.Enumerators == 2, "stale availability");
        });
        Case("input invalidation replaces cached state immediately", () =>
        {
            var device = Boundary.Add(EDataFlow.eCapture, new Session(717));
            Assert(!SessionVolumeService.ReadInputState(717).Muted, "initial");
            device.Sessions = new[] { new Session(717) { Muted = true } };
            SessionVolumeService.InvalidateCache();
            Assert(SessionVolumeService.ReadInputState(717).Muted && Boundary.Enumerators == 2, "old snapshot reused");
        });
        Case("input state holds references across concurrent refresh", () =>
        {
            var session = new Session(718) { Muted = true }; Boundary.Add(EDataFlow.eCapture, session);
            SessionVolumeService.Refresh(true);
            Task? refresh = null; bool refreshBlocked = false;
            session.OnReadMute = () =>
            {
                session.OnReadMute = null; refresh = Task.Run(() => SessionVolumeService.Refresh(true));
                refreshBlocked = !refresh.Wait(40);
            };
            Assert(SessionVolumeService.ReadInputState(718).Muted && refreshBlocked, "read or reference protection");
            Assert(refresh != null && refresh.Wait(5000), "refresh did not finish");
        });
        Case("confirmed capture write still forces rediscovery and verifies every target session", () =>
        {
            var first = new Session(719); var second = new Session(719); var other = new Session(720);
            Boundary.Add(EDataFlow.eCapture, first, second, other);
            var state = SessionVolumeService.ChangeInputMuteChecked(719, true);
            Assert(state.Applied && state.Muted && Boundary.Enumerators == 2, "write verification changed");
            Assert(first.Writes == 1 && second.Writes == 1 && other.Writes == 0, "write target changed");
        });
        Case("ignored capture write still fails confirmation", () =>
        {
            var session = new Session(721) { IgnoreWrite = true }; Boundary.Add(EDataFlow.eCapture, session);
            Assert(!SessionVolumeService.ChangeInputMuteChecked(721, true).Applied && !session.Muted,
                "ignored write accepted");
            var state = SessionVolumeService.ReadInputState(721);
            Assert(state.Available && !state.Muted && Boundary.Enumerators == 2, "failure recovery rediscovered unnecessarily");
        });
    }
#endif
}
