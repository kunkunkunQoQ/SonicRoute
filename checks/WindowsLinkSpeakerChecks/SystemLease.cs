using System.Reflection;
using System.Runtime.InteropServices;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static void CheckSystemLease()
    {
        // The production lease classes receive only these in-memory endpoint operations.
        // This suite does not enumerate, mute or restore any Windows audio device.
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        { ["speaker"] = false, ["headset"] = false, ["pre-muted"] = true, ["new"] = false };
        var writes = new List<(string Id, bool Mute)>();
        string? disconnected = null, refuse = null;
        string[] topology = ["speaker", "headset", "HEADSET", "pre-muted"];
        Func<string[]> list = () => topology;
        var assembly = typeof(SonicRoute.App).Assembly;
        var sourceType = assembly.GetType("SonicRoute.SourceSpeakerLeases", true)!;
        var groupType = assembly.GetType("SonicRoute.SystemSpeakerLeases", true)!;
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var source = Activator.CreateInstance(sourceType, hidden, null, [
            (Func<int, (string?, bool)>)(pid => pid == 18 ? ("headset", true) : (null, false)),
            (Func<string, (bool, bool)>)(id => (state[id], !string.Equals(id, disconnected, StringComparison.OrdinalIgnoreCase))),
            (Func<string, bool, bool>)((id, mute) =>
            {
                if (string.Equals(id, refuse, StringComparison.OrdinalIgnoreCase)) return false;
                writes.Add((id, mute)); state[id] = mute; return true;
            })], null)!;
        var endpoint = sourceType.GetMethod("AcquireEndpoint", hidden)!.CreateDelegate<Func<string, int, IPcProcessSilenceLease?>>(source);
        var sourceChanged = sourceType.GetMethod("Revalidate", hidden)!.CreateDelegate<Action>(source);
        var unmuted = sourceType.GetMethod("NoteUnmuted", hidden)!.CreateDelegate<Action<string>>(source);
        var pool = Activator.CreateInstance(groupType, hidden, null, [(Func<string[]>)(() => list()),
            (Func<string, IPcProcessSilenceLease?>)(id => endpoint(id, 0))], null)!;
        var tryAcquire = groupType.GetMethod("Acquire", hidden)!.CreateDelegate<Func<IPcProcessSilenceLease?>>(pool);
        var topologyChanged = groupType.GetMethod("Revalidate", hidden)!.CreateDelegate<Action>(pool);
        IPcProcessSilenceLease AcquireGroup() => tryAcquire() ?? throw new InvalidOperationException("Expected a group lease");

        using (var app = endpoint("headset", 18)!)
        {
            using var first = AcquireGroup();
            using var second = AcquireGroup();
            Require(writes.SequenceEqual([("headset", true), ("speaker", true)]) && state["pre-muted"],
                "two whole-PC groups and an overlapping app share one mute per audible endpoint, preserving pre-muted devices");
            topology = ["PRE-MUTED", "headset", "speaker"]; topologyChanged();
            Require(!first.Invalidated.IsCancellationRequested && !second.Invalidated.IsCancellationRequested, "ordering, casing and duplicate IDs cannot revoke a valid topology");
            first.Dispose(); first.Dispose(); second.Release(false);
            Require(!state["speaker"] && state["headset"] && state["pre-muted"], "last whole-PC release restores its own speaker while the app still owns its headset");
        }
        Require(!state["headset"] && writes.Count == 4 && !writes.Any(w => w.Id.Equals("pre-muted", StringComparison.OrdinalIgnoreCase)),
            "the last app restores the headset once and the original user mute is never written");

        writes.Clear();
        using (var first = AcquireGroup())
        using (var second = AcquireGroup())
        {
            state["speaker"] = false; unmuted("speaker"); sourceChanged();
            Require(first.Invalidated.IsCancellationRequested && second.Invalidated.IsCancellationRequested, "a manual unmute invalidates both groups through the shared endpoint");
            int beforeRetry = writes.Count;
            Require(tryAcquire() == null && writes.Count == beforeRetry && !state["speaker"],
                "a new group cannot silently remute the endpoint while the revoked groups are being released");
            state["speaker"] = true; // The user subsequently chose to mute the speaker again.
            first.Dispose(); second.Dispose();
            Require(state["speaker"] && !state["headset"] && !writes.Contains(("speaker", false)), "releasing revoked groups preserves a later manual mute");
        }
        state["speaker"] = false;
        writes.Clear();
        using (var group = AcquireGroup())
        {
            topology = ["speaker", "new", "pre-muted"]; disconnected = "headset"; topologyChanged();
            Require(group.Invalidated.IsCancellationRequested && !writes.Any(w => w.Id == "new"), "adding or unplugging an output invalidates the group without writing to the new endpoint");
        }
        Require(!state["speaker"] && state["headset"], "group release restores available devices and holds the disconnected restore until its next notification");
        disconnected = null; sourceChanged();
        Require(!state["headset"] && !state["new"], "reconnection restores only the mute that this service still owns");

        topology = ["speaker", "pre-muted", "headset"];
        refuse = "headset"; writes.Clear();
        Require(tryAcquire() == null && writes.SequenceEqual([("speaker", true), ("speaker", false)]) && !state["headset"] && state["pre-muted"],
            "failure to acquire the last endpoint rolls back earlier changes and keeps original mute settings");
        refuse = null; disconnected = "headset"; writes.Clear();
        Require(tryAcquire() == null && writes.SequenceEqual([("speaker", true), ("speaker", false)]), "unknown mute state rolls back prior endpoints without guessing its value");
        disconnected = null;
        int snapshots = 0;
        list = () => ++snapshots == 1 ? ["speaker", "headset"] : ["speaker", "headset", "new"];
        Require(tryAcquire() == null && !state["speaker"] && !state["headset"] && !state["new"], "a new endpoint appearing during acquisition cancels the transaction before publishing success");
        list = () => topology;
        using (var group = AcquireGroup())
        {
            list = () => throw new COMException("Disconnected enumerator");
            topologyChanged();
            Require(group.Invalidated.IsCancellationRequested, "an unreadable topology invalidates a live group");
        }
        list = () => [];
        writes.Clear();
        Require(tryAcquire() == null && writes.Count == 0, "zero active outputs cannot produce a successful system lease");
    }
}
