using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace MonitoringChecks;
internal static class PanelPerformanceCases
{
    internal static void Run(Action<string, Action> check, Action<bool, string> assert)
    {
        check("output stable targets capture and read only the changed endpoint", () =>
        {
            var devices = Enumerable.Range(0, 64).Select(_ => Boundary.Add(EDataFlow.eRender)).ToArray();
            var targets = new OutputVolumeMonitor.TargetSet(devices.Select(d => d.Id));
            var monitor = new OutputVolumeMonitor();
            try
            {
                var first = monitor.GetReadDevices(targets, false);
                monitor.ReadAsync(targets, first).GetAwaiter().GetResult();
                assert(first.Count == 64 && monitor.GetReadDevices(targets, false).Count == 0, "initial/idle targets");
                devices[42].Volume.Scalar = .79f; devices[42].Volume.Notify();
                var dirty = monitor.GetReadDevices(targets, false);
                var result = monitor.ReadAsync(targets, dirty).GetAwaiter().GetResult();
                assert(dirty.SetEquals(new[] { devices[42].Id }) && result.Count == 1 && result[devices[42].Id].Volume == 79, "dirty selection");
                assert(devices.Sum(d => d.Volume.VolumeReads) == 65 && Boundary.Activations == 64, "full list reread/rebound");
                Console.WriteLine("OUTPUT_PREP_METRIC total=64 captured=1 nativeReads=1 targetSetReused=true");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output filter replacement releases subscriptions and explicit reads are targeted", () =>
        {
            var a = Boundary.Add(EDataFlow.eRender); var b = Boundary.Add(EDataFlow.eRender);
            var monitor = new OutputVolumeMonitor();
            try
            {
                var targets = new OutputVolumeMonitor.TargetSet(new[] { a.Id, b.Id });
                monitor.ReadAsync(targets, monitor.GetReadDevices(targets, false)).GetAwaiter().GetResult();
                assert(monitor.GetReadDevices(targets, false, new[] { b.Id, "missing" }).SetEquals(new[] { b.Id }), "explicit target");
                var filtered = new OutputVolumeMonitor.TargetSet(new[] { b.Id });
                monitor.ReadAsync(filtered, monitor.GetReadDevices(filtered, false)).GetAwaiter().GetResult();
                assert(a.Volume.Callbacks.IsEmpty && b.Volume.Registrations == 1, "filter subscription lifecycle");
                monitor.ReadAsync(targets, monitor.GetReadDevices(targets, false)).GetAwaiter().GetResult();
                assert(a.Volume.Registrations == 2 && b.Volume.Registrations == 1, "filter reversal");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output notification arriving after version capture remains pending", () =>
        {
            var a = Boundary.Add(EDataFlow.eRender); var b = Boundary.Add(EDataFlow.eRender);
            var targets = new OutputVolumeMonitor.TargetSet(new[] { a.Id, b.Id });
            var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(targets, monitor.GetReadDevices(targets, false)).GetAwaiter().GetResult();
                a.Volume.Notify(); var captured = monitor.GetReadDevices(targets, false);
                a.Volume.OnVolumeRead = () => { a.Volume.OnVolumeRead = null; b.Volume.Notify(); };
                monitor.ReadAsync(targets, captured).GetAwaiter().GetResult();
                var next = monitor.GetReadDevices(targets, false);
                assert(next.SetEquals(new[] { b.Id }), "late endpoint notification lost");
                monitor.ReadAsync(targets, next).GetAwaiter().GetResult();
                assert(monitor.GetReadDevices(targets, false).Count == 0, "consumed notification repeated");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output stable target path retains registration recovery", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); device.Volume.RegistrationFailures = 1;
            var targets = new OutputVolumeMonitor.TargetSet(new[] { device.Id });
            var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(targets, monitor.GetReadDevices(targets, false)).GetAwaiter().GetResult();
                assert(monitor.NeedsRecovery && !monitor.NeedsTopologyRecovery, "registration failure lost");
                monitor.ReadAsync(targets, monitor.GetReadDevices(targets, false)).GetAwaiter().GetResult();
                assert(!monitor.NeedsRecovery && device.Volume.Registrations == 2, "recovery lost");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("meter async dispose returns while native read is blocked and releases on worker", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var session = new Session(720); var later = new Session(722);
            var device = Boundary.Add(EDataFlow.eRender, session, later);
            session.OnPeakRead = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); };
            var meter = new AudioMeterService(); int frames = 0;
            meter.PeaksUpdated += _ => Interlocked.Increment(ref frames); meter.Start(new[] { 720, 722 });
            try
            {
                assert(entered.Wait(4000), "native read not entered");
                var done = meter.DisposeAsync();
                assert(!done.IsCompleted && ReferenceEquals(done, meter.DisposeAsync()), "dispose waited or duplicated completion");
                release.Set(); assert(done.Wait(4000), "dispose never completed");
                assert(frames == 0 && later.PeakReads == 0 && session.Callbacks.IsEmpty && device.Manager.Callbacks.IsEmpty && Boundary.Notifications.IsEmpty,
                    "late callback or native resources retained");
                Console.WriteLine("METER_STOP_METRIC returnedBeforeBlockedReadFinished=true workerCleanupCompleted=true");
            }
            finally { release.Set(); meter.Dispose(); }
        });
        check("meter rapid new instance survives retired instance disposal", () =>
        {
            var session = new Session(721); Boundary.Add(EDataFlow.eRender, session);
            var previous = new AudioMeterService(); previous.Start(new[] { 721 });
            var retired = previous.DisposeAsync();
            using var current = new AudioMeterService(); using var frame = new ManualResetEventSlim();
            current.PeaksUpdated += _ => frame.Set(); current.Start(new[] { 721 });
            assert(retired.Wait(4000) && frame.Wait(4000), "new instance suppressed by retired owner");
            previous.Dispose();
        });
    }
}
