using SonicRoute;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace MonitoringChecks;
internal static class Program
{
    private static int _passed, _failed;
    private static bool _endpointVolumesOnly;
    private static bool _meterOnly;
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Wait(Func<bool> condition, string message, int timeout = 4000)
    { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!condition() && watch.ElapsedMilliseconds < timeout) Thread.Sleep(10); Assert(condition(), message); }
    private static void Case(string name, Action test)
    {
        if (_endpointVolumesOnly && !name.StartsWith("output ") && !name.Contains("microphone") && !name.StartsWith("missing device notification")) return;
        if (_meterOnly && (name.StartsWith("output ") || name.Contains("microphone") || name.StartsWith("missing device notification") || name.StartsWith("WMI "))) return;
        try { Boundary.Reset(); test(); Assert(Boundary.CallbackReleases == 0, "COM released inside a callback"); _passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
    private sealed class Probe : IDisposable
    {
        internal readonly AudioMeterService Meter = new();
        internal IReadOnlyDictionary<int, float> Latest = new Dictionary<int, float>();
        internal int Frames;
        internal Probe(params int[] pids) { Meter.PeaksUpdated += peaks => { Volatile.Write(ref Latest, peaks); Interlocked.Increment(ref Frames); }; Meter.Start(pids); }
        internal float Peak(int pid) { var latest = Volatile.Read(ref Latest); return latest.TryGetValue(pid, out var peak) ? peak : 0; }
        public void Dispose() => Meter.Dispose();
    }
    public static int Main(string[] args)
    {
        if (args.Contains("--panel-performance"))
        {
            PanelPerformanceCases.Run(Case, Assert);
            Console.WriteLine($"RESULT passed={_passed} failed={_failed} PANEL_PERFORMANCE_ONLY=true OS_BOUNDARIES=FAKES_ONLY");
            return _failed == 0 ? 0 : 1;
        }
        _endpointVolumesOnly = args.Contains("--endpoint-volumes-only");
        _meterOnly = args.Contains("--meter-only");
        Case("visible demand samples only visible PIDs without rebuilding subscriptions", () =>
        {
            var a = new Session(901); var b = new Session(902); Boundary.Add(EDataFlow.eRender, a, b);
            using var probe = new Probe(901, 902);
            probe.Meter.UpdateVisibleTargets(new[] { 901 });
            Wait(() => probe.Peak(901) > .5f && !Volatile.Read(ref probe.Latest).ContainsKey(902), "visible snapshot missing");
            int hiddenReads = b.PeakReads, enumerators = Boundary.Enumerators;
            Thread.Sleep(150);
            Assert(b.PeakReads == hiddenReads, "offscreen native sampling continued");
            var saved = Volatile.Read(ref probe.Latest);
            probe.Meter.UpdateVisibleTargets(new[] { 902 });
            Wait(() => probe.Peak(902) > .5f && !Volatile.Read(ref probe.Latest).ContainsKey(901), "new visible target not resumed");
            Assert(Boundary.Enumerators == enumerators && saved.ContainsKey(901), "scroll rebuilt graph or mutated a published snapshot");
            probe.Meter.UpdateVisibleTargets(Array.Empty<int>());
            Wait(() => Volatile.Read(ref probe.Latest).Count == 0, "empty visible demand not published");
            int before = a.PeakReads + b.PeakReads; Thread.Sleep(150);
            Assert(a.PeakReads + b.PeakReads == before, "empty visible demand kept sampling");
            Console.WriteLine("VISIBLE_SAMPLE_METRIC targets=2 visible=1 hiddenPeakReads=0 scrollingRebuilds=0 emptyPeakReads=0");
        });
        OutputVolumeCases.Run(Case, Assert);
        Case("healthy active meter retains managers beyond old two-second scan", () =>
        {
            var session = new Session(101); var device = Boundary.Add(EDataFlow.eRender, session);
            using var probe = new Probe(101); Wait(() => probe.Peak(101) > .5f, "no active peak");
            Thread.Sleep(2200);
            Assert(Boundary.Enumerators == 1 && Boundary.Activations == 1 && device.Manager.Registrations == 1, "healthy graph rebuilt");
            Assert(session.PeakReads > 40, "active sampling stopped");
        });
        Case("inactive session waits for state event and fades fully back to zero", () =>
        {
            var session = new Session(102, AudioSessionState.Inactive); Boundary.Add(EDataFlow.eRender, session);
            using var probe = new Probe(102); Wait(() => probe.Frames > 0, "initial snapshot");
            Thread.Sleep(100); int reads = session.StateReads;
            Thread.Sleep(220); Assert(session.PeakReads == 0 && session.StateReads == reads, "inactive polling");
            session.SetState(AudioSessionState.Active); Wait(() => probe.Peak(102) > .5f, "activation did not wake");
            session.SetState(AudioSessionState.Inactive); Wait(() => probe.Peak(102) == 0, "fade did not finish");
            int peaks = session.PeakReads, frames = probe.Frames; Thread.Sleep(220);
            Assert(session.PeakReads == peaks && probe.Frames == frames, "inactive did not settle");
        });
        Case("new-session callback is consumed even when absent from enumerator", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); using var probe = new Probe(103);
            Wait(() => device.Manager.Callbacks.Count == 1 && probe.Frames > 0, "manager not ready");
            var session = new Session(103); device.Manager.Created(session);
            Wait(() => probe.Peak(103) > .5f, "new callback ignored");
            Assert(Boundary.Enumerators == 1 && device.Manager.Snapshot.Length == 0, "notification replaced by scan");
        });
        Case("initial enumeration and creation notification deduplicate by instance", () =>
        {
            var session = new Session(104); var device = Boundary.Add(EDataFlow.eRender, session);
            device.Manager.OnCount = () => device.Manager.Created(session);
            using var probe = new Probe(104); Wait(() => probe.Peak(104) > .5f, "no peak");
            Assert(session.Registrations == 1, "duplicate session subscribed");
        });
        Case("same PID sessions aggregate max and expired instance is removed", () =>
        {
            var a = new Session(105) { Peak = .2f }; var b = new Session(105) { Peak = .8f };
            Boundary.Add(EDataFlow.eRender, a, b); using var probe = new Probe(105);
            Wait(() => probe.Peak(105) > .7f, "max lost"); b.SetState(AudioSessionState.Expired);
            Wait(() => b.Unregistrations == 1 && probe.Peak(105) < .3f, "expired session retained");
            Assert(a.Registrations == 1 && Boundary.Enumerators == 1, "remaining session rebuilt");
        });
        Case("active silent stream continues sampling", () =>
        {
            var session = new Session(106) { Peak = 0 }; Boundary.Add(EDataFlow.eRender, session);
            using var probe = new Probe(106); Wait(() => session.PeakReads > 1, "no samples");
            int reads = session.PeakReads; Thread.Sleep(220); Assert(session.PeakReads > reads + 3, "zero peak incorrectly slept");
        });
        Case("target removal releases graph and creation cannot restart retired work", () =>
        {
            var session = new Session(107); var device = Boundary.Add(EDataFlow.eRender, session);
            using var probe = new Probe(107); Wait(() => probe.Peak(107) > .5f, "no peak");
            var callback = device.Manager.Callbacks.Keys.Single(); probe.Meter.UpdateTargets(Array.Empty<int>());
            Wait(() => device.Manager.Unregistrations == 1 && session.Unregistrations == 1, "not released");
            int reads = session.PeakReads, scans = Boundary.Enumerators; callback.OnSessionCreated(new Session(107)); Thread.Sleep(150);
            Assert(session.PeakReads == reads && Boundary.Enumerators == scans && Boundary.Notifications.IsEmpty, "retired graph woke");
        });
        Case("notification failure preserves peak sampling and original recovery scan", () =>
        {
            var session = new Session(108) { ThrowRegistration = true };
            var device = Boundary.Add(EDataFlow.eRender, session); device.Manager.ThrowRegistration = true;
            using var probe = new Probe(108); Wait(() => probe.Peak(108) > .5f, "registration failure stopped reads");
            Wait(() => Boundary.Enumerators >= 2, "fallback scan missing", 3000);
            device.Manager.ThrowRegistration = false; session.ThrowRegistration = false;
            Wait(() => device.Manager.Callbacks.Count == 1 && session.Callbacks.Count == 1, "notification recovery", 3000);
            int scans = Boundary.Enumerators; Thread.Sleep(2200); Assert(Boundary.Enumerators == scans, "recovered graph kept scanning");
        });
        Case("device event rebuilds on worker and stop releases all callbacks", () =>
        {
            var session = new Session(109); var device = Boundary.Add(EDataFlow.eRender, session);
            var probe = new Probe(109); try
            {
                Wait(() => probe.Peak(109) > .5f, "not started"); Boundary.Topology();
                Wait(() => Boundary.Enumerators == 2 && session.Registrations == 2, "topology not rebuilt");
            }
            finally { probe.Dispose(); }
            Assert(Boundary.Notifications.IsEmpty && device.Manager.Callbacks.IsEmpty && session.Callbacks.IsEmpty, "callbacks retained");
            int reads = session.PeakReads; Thread.Sleep(100); Assert(session.PeakReads == reads, "work after stop");
        });
        Case("unreadable identity remains sampled through compatibility path", () =>
        {
            var session = new Session(110) { IdentityFails = true }; Boundary.Add(EDataFlow.eRender, session);
            using var probe = new Probe(110); Wait(() => probe.Peak(110) > .5f, "identity failure hid meter");
        });
        Case("microphone callback recovery reuses live endpoint and stops retry after success", () =>
        {
            var device = Boundary.Add(EDataFlow.eCapture); device.Volume.RegistrationFailures = 2; device.Volume.Muted = true;
            var monitor = new MicrophoneStateMonitor(); try
            {
                Assert(monitor.ReadAsync(true).GetAwaiter().GetResult() && monitor.NeedsRecovery, "first read");
                Assert(monitor.ReadAsync(true).GetAwaiter().GetResult() && monitor.NeedsRecovery, "second read");
                Assert(monitor.ReadAsync(true).GetAwaiter().GetResult() && !monitor.NeedsRecovery, "registration not restored");
                monitor.ReadAsync(true).GetAwaiter().GetResult();
                Assert(Boundary.Enumerators == 1 && Boundary.Activations == 1 && device.Volume.Registrations == 3, "valid COM rebuilt or retried after recovery");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
            Assert(device.Volume.Unregistrations == 1 && Boundary.Notifications.IsEmpty, "mic teardown");
        });
        Case("partial microphone registration retries only missing callback", () =>
        {
            var a = Boundary.Add(EDataFlow.eCapture); var b = Boundary.Add(EDataFlow.eCapture); b.Volume.RegistrationFailures = 1;
            var monitor = new MicrophoneStateMonitor(); try
            {
                monitor.ReadAsync(false).GetAwaiter().GetResult(); monitor.ReadAsync(false).GetAwaiter().GetResult();
                Assert(a.Volume.Registrations == 1 && b.Volume.Registrations == 2 && Boundary.Activations == 2 && !monitor.NeedsRecovery, "partial registration rebuilt healthy device");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        Case("missing device notification preserves full topology recovery", () =>
        {
            Boundary.DeviceRegistrationFailures = 1; Boundary.Add(EDataFlow.eCapture);
            var monitor = new MicrophoneStateMonitor(); try
            {
                monitor.ReadAsync(false).GetAwaiter().GetResult(); Assert(monitor.NeedsRecovery, "registration failure hidden");
                monitor.ReadAsync(false).GetAwaiter().GetResult(); Assert(!monitor.NeedsRecovery && Boundary.Enumerators == 2, "topology recovery removed");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        Case("microphone COM read failure rebuilds next time", () =>
        {
            var device = Boundary.Add(EDataFlow.eCapture); device.Volume.ReadFailures = 1;
            var monitor = new MicrophoneStateMonitor(); try
            {
                monitor.ReadAsync(false).GetAwaiter().GetResult(); Assert(monitor.NeedsRecovery, "read error hidden");
                monitor.ReadAsync(false).GetAwaiter().GetResult(); Assert(!monitor.NeedsRecovery && Boundary.Activations == 2, "failed COM reused");
                monitor.RequestRebuild(); monitor.ReadAsync(false).GetAwaiter().GetResult(); Assert(Boundary.Activations == 3, "explicit rebuild ignored");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        Case("WMI startup failure and stopped event retain availability and disposal semantics", () =>
        {
            System.Management.ManagementEventWatcher.Failures = 1; int signals = 0;
            var source = new ProcessChangeSource(_ => Interlocked.Increment(ref signals));
            source.StartAsync().GetAwaiter().GetResult(); Assert(!source.IsAvailable && source.Startup.IsCompleted && signals == 1, "failed source availability");
            source.Dispose(); source.Completion.GetAwaiter().GetResult();
            var replacement = new ProcessChangeSource(_ => Interlocked.Increment(ref signals));
            replacement.StartAsync().GetAwaiter().GetResult(); Assert(replacement.IsAvailable, "replacement not available");
            foreach (var watcher in System.Management.ManagementEventWatcher.Instances) watcher.StopUnexpectedly();
            Assert(!replacement.IsAvailable, "stopped watcher stays healthy");
            replacement.Dispose(); replacement.Completion.GetAwaiter().GetResult();
            int before = signals; foreach (var watcher in System.Management.ManagementEventWatcher.Instances) watcher.Emit();
            Assert(signals == before, "retired callback delivered");
        });
        Case("meter ignores invalid peaks and clamps positive infinity", () =>
        {
            var a = new Session(201) { Peak = float.NaN }; var b = new Session(202) { Peak = -.5f };
            var c = new Session(203) { Peak = float.PositiveInfinity };
            Boundary.Add(EDataFlow.eRender, a, b, c);
            using var probe = new Probe(201, 202, 203);
            Wait(() => probe.Frames > 2 && probe.Peak(203) > .9f, "clamp frame missing");
            Assert(probe.Peak(201) == 0 && probe.Peak(202) == 0 && probe.Peak(203) <= 1, "invalid peak published");
        });
        Case("target replacement removes old keys and published snapshots stay immutable", () =>
        {
            var a = new Session(204) { Peak = .2f }; var b = new Session(205) { Peak = .8f };
            Boundary.Add(EDataFlow.eRender, a, b);
            using var probe = new Probe(204);
            Wait(() => probe.Peak(204) > .1f, "initial target missing");
            var first = Volatile.Read(ref probe.Latest);
            float retained = first[204];
            probe.Meter.UpdateTargets(new[] { 205 });
            Wait(() => probe.Peak(205) > .5f, "new target missing");
            var current = Volatile.Read(ref probe.Latest);
            Assert(current.Count == 1 && !current.ContainsKey(204), "stale snapshot key");
            Assert(first.Count == 1 && first[204] == retained && !first.ContainsKey(205), "old snapshot mutated");
        });
        Case("empty meter waits without enumerating and starts on target event", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(206));
            using var probe = new Probe(); Thread.Sleep(120);
            Assert(Boundary.Enumerators == 0 && probe.Frames == 0, "empty worker polled");
            probe.Meter.UpdateTargets(new[] { 206 });
            Wait(() => probe.Peak(206) > .5f, "target update did not wake empty worker");
        });
        Console.WriteLine($"RESULT passed={_passed} failed={_failed} runtime={Environment.Version} OS_BOUNDARIES=FAKES_ONLY");
        return _failed == 0 ? 0 : 1;
    }
}
