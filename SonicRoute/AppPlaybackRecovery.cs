#if SONICROUTE_PHONE
using System.Diagnostics;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

namespace SonicRoute;

internal static partial class AppPlaybackOutputs
{
    private sealed record Choice(string[] Outputs, string Preferred, bool AllowSpeakerMute);
    private sealed class History(Process process)
    {
        internal readonly Process Process = process;
        internal readonly List<Choice> Previous = [];
        internal Choice? Current;
    }
    private static readonly object HistoryGate = new();
    private static readonly Dictionary<int, History> Histories = [];
    private static long _historyEpoch;
    internal static bool IsManaged(int pid) { lock (HistoryGate) return Histories.ContainsKey(pid); }
    private static History? HistoryFor(int pid)
    {
        lock (HistoryGate)
        {
            var history = Histories.GetValueOrDefault(pid);
            if (history == null || !history.Process.HasExited) return history;
            Forget(pid, history);
            return null;
        }
    }
    private static void Forget(int pid, History? expected = null)
    {
        lock (HistoryGate)
        {
            if (!Histories.TryGetValue(pid, out var current) || expected != null && current != expected) return;
            Histories.Remove(pid);
            current.Process.Dispose();
        }
    }
    internal static void ForgetAll()
    {
        lock (HistoryGate)
        {
            Interlocked.Increment(ref _historyEpoch);
            foreach (var history in Histories.Values) history.Process.Dispose();
            Histories.Clear();
        }
    }
    private static void Remember(int pid, string[] before, string[] outputs, string preferred, bool allowSpeakerMute)
    {
        lock (HistoryGate)
        {
            if (!Histories.TryGetValue(pid, out var history))
            {
                if (!outputs.Any(AndroidOutputIdentity.IsPhoneOutput)) return;
                try
                {
                    var process = Process.GetProcessById(pid);
                    history = new History(process);
                    Histories.Add(pid, history);
                    process.Exited += (_, _) => Forget(pid, history);
                    process.EnableRaisingEvents = true;
                    if (process.HasExited) { Forget(pid, history); return; }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { Forget(pid, history); return; }
            }
            if (history.Current == null && before.Length > 0) history.Current = new(before, before[0], false);
            if (history.Current is { } previous && !previous.Outputs.ToHashSet(Ids).SetEquals(outputs))
            {
                history.Previous.RemoveAll(c => c.Outputs.ToHashSet(Ids).SetEquals(previous.Outputs));
                history.Previous.Insert(0, previous);
                if (history.Previous.Count > 12) history.Previous.RemoveAt(12);
            }
            history.Current = new(outputs, preferred, allowSpeakerMute);
        }
    }

    // Mark work with the exact successful choice that observed the disconnect. A later manual command wins.
    internal static void OutputEnded(int pid, string output, bool recover)
    {
        if (!AndroidOutputIdentity.IsPhoneOutput(output)) return;
        History? history;
        Choice? expected;
        lock (HistoryGate)
        {
            if (!Histories.TryGetValue(pid, out history) || history.Current is not { } choice || !choice.Outputs.Contains(output, Ids)) return;
            expected = choice;
        }
        _ = Task.Run(async () =>
        {
            try { await SerializeAsync(pid, () => RecoverLockedAsync(pid, output, recover, history, expected)).ConfigureAwait(false); }
            catch (Exception ex) { ExceptionDiagnostics.Record("AppPlaybackOutputs.Disconnect", ex); }
        });
    }

    private static async Task<bool> RecoverLockedAsync(int pid, string lost, bool recover, History history, Choice expected)
    {
        lock (HistoryGate)
        {
            if (Histories.GetValueOrDefault(pid) != history || !ReferenceEquals(history.Current, expected) || history.Process.HasExited) return false;
        }
        if (LocalAppOutputMirrors.GetExtraOutputs(pid).Contains(lost, Ids)) return false;
        // An explicit stop removes intent but must not open another output.
        if (!recover)
        {
            string[] left = expected.Outputs.Where(id => !Ids.Equals(id, lost)).ToArray();
            if (left.Length == 0) Forget(pid, history);
            else lock (HistoryGate) history.Current = expected with { Outputs = left };
            return true;
        }
        var historyBefore = history.Previous.ToArray();
        var externalIds = AndroidAudioHost.GetOutputDevices().Select(phone => phone.Id).ToHashSet(Ids);
        var localIds = new HashSet<string>(Ids) { AudioService.SystemDefaultDeviceId };
        if (historyBefore.Any(c => c.Outputs.Any(id => !AndroidOutputIdentity.IsPhoneOutput(id) && !AudioService.IsSystemDefault(id))))
            using (var devices = new WasapiDeviceLease.Enumerator())
                foreach (var lease in devices.ActiveRender()) using (lease) localIds.Add(lease.Device.ID);
        bool Available(string id) => !Ids.Equals(id, lost) && (AndroidOutputIdentity.IsPhoneOutput(id) ? externalIds.Contains(id) : localIds.Contains(id));
        // A Windows endpoint in the current selection remains governed by native routing even while unplugged.
        var remaining = expected.Outputs.Where(id => !AndroidOutputIdentity.IsPhoneOutput(id) || Available(id)).ToArray();
        var candidates = remaining.Length != 0
            ? new[] { expected with { Outputs = remaining } }
            : historyBefore.Select(c => c with { Outputs = c.Outputs.Where(Available).ToArray() }).Where(c => c.Outputs.Length != 0);
        foreach (var candidate in candidates)
        {
            lock (HistoryGate)
                if (Histories.GetValueOrDefault(pid) != history || history.Current != expected || history.Process.HasExited) return false;
            string preferred = candidate.Outputs.Contains(candidate.Preferred, Ids) ? candidate.Preferred : candidate.Outputs[0];
            var result = await ApplyAsync(pid, candidate.Outputs, preferred, candidate.AllowSpeakerMute).ConfigureAwait(false);
            if (!result.Success) continue;
            lock (HistoryGate)
                if (Histories.GetValueOrDefault(pid) == history) history.Current = candidate with { Preferred = preferred };
            return true;
        }
        Forget(pid, history);
        return false;
    }
}
#endif
