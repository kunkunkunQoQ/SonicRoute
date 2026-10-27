using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace MonitoringChecks;
internal static partial class OutputVolumeCases
{
    internal static void Run(Action<string, Action> check, Action<bool, string> assert)
    {
        check("output endpoint-only recovery preserves healthy reads and subscriptions", () =>
        {
            var a = Boundary.Add(EDataFlow.eRender); var b = Boundary.Add(EDataFlow.eRender); b.Volume.RegistrationFailures = 1;
            var monitor = new OutputVolumeMonitor(); var ids = new[] { a.Id, b.Id };
            try
            {
                monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                assert(monitor.NeedsRecovery && !monitor.NeedsTopologyRecovery, "endpoint fault escalated to topology recovery");
                b.Volume.Scalar = .64f;
                var recovered = monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                assert(recovered.Count == 1 && recovered[b.Id].Volume == 64 && a.Volume.VolumeReads == 1 && a.Volume.Registrations == 1
                    && !monitor.NeedsRecovery && !monitor.NeedsTopologyRecovery, "healthy endpoint reread during recovery");
                b.Volume.VolumeReadFailures = 1; b.Volume.Notify(); monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                assert(monitor.NeedsRecovery && !monitor.NeedsTopologyRecovery, "read failure escalated topology");
                monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                assert(a.Volume.VolumeReads == 1 && a.Volume.Unregistrations == 0 && !monitor.NeedsRecovery, "healthy endpoint recreated after peer read failure");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output missing device notifications retain full topology recovery", () =>
        {
            Boundary.DeviceRegistrationFailures = 1; var device = Boundary.Add(EDataFlow.eRender); var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(new[] { device.Id }, false).GetAwaiter().GetResult();
                assert(monitor.NeedsRecovery && monitor.NeedsTopologyRecovery, "missing device callbacks lost full recovery");
                monitor.ReadAsync(new[] { device.Id }, true).GetAwaiter().GetResult();
                assert(!monitor.NeedsRecovery && !monitor.NeedsTopologyRecovery, "topology recovery did not settle");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output targeted reconciliation rereads one clean endpoint without removing peers", () =>
        {
            var devices = Enumerable.Range(0, 8).Select(_ => Boundary.Add(EDataFlow.eRender)).ToArray();
            var ids = devices.Select(d => d.Id).ToArray(); var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(ids, false).GetAwaiter().GetResult(); devices[3].Volume.Muted = true;
                var states = monitor.ReadAsync(ids, false, new[] { ids[3].ToUpperInvariant(), "not-a-target" }).GetAwaiter().GetResult();
                assert(states.Count == 1 && states[ids[3]].Muted && devices.Sum(d => d.Volume.VolumeReads) == 9
                    && devices.All(d => d.Volume.Unregistrations == 0) && Boundary.Activations == 8, "targeted reconciliation scanned or removed peers");
                Console.WriteLine("READ-COUNTS targeted-reconciliation=1 healthy-peer-rereads=0 subscriptions-retained=8");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output unresolved endpoint keeps topology fallback without recreating healthy peers", () =>
        {
            var a = Boundary.Add(EDataFlow.eRender); var b = Boundary.Add(EDataFlow.eRender);
            var ids = new[] { a.Id, b.Id }; var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                b.Volume.VolumeReadFailures = 1; b.Volume.Notify(); monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                Boundary.Devices.Remove(b);
                var states = monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                assert(monitor.NeedsRecovery && monitor.NeedsTopologyRecovery && states[b.Id].Volume == -1
                    && a.Volume.VolumeReads == 1 && a.Volume.Unregistrations == 0, "unresolved endpoint lost topology fallback or churned healthy peer");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output incremental bursts read one changed endpoint out of eight", () =>
        {
            var devices = Enumerable.Range(0, 8).Select(_ => Boundary.Add(EDataFlow.eRender)).ToArray();
            var ids = devices.Select(d => d.Id).ToArray();
            var monitor = new OutputVolumeMonitor();
            try
            {
                assert(monitor.ReadAsync(ids, false).GetAwaiter().GetResult().Count == 8, "initial states missing");
                assert(monitor.ReadAsync(ids, false).GetAwaiter().GetResult().Count == 0
                    && devices.Sum(d => d.Volume.VolumeReads) == 8, "unchanged endpoints reread");
                devices[3].Volume.Scalar = .73f;
                for (int i = 0; i < 100; i++) devices[3].Volume.Notify();
                var changed = monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                assert(changed.Count == 1 && changed[ids[3]] == (73, false)
                    && devices.Sum(d => d.Volume.VolumeReads) == 9
                    && devices.Sum(d => d.Volume.MuteReads) == 9 && Boundary.Activations == 8,
                    "burst read unrelated endpoints or recreated subscriptions");
                assert(monitor.ReadAsync(ids, true).GetAwaiter().GetResult().Count == 8
                    && devices.Sum(d => d.Volume.VolumeReads) == 17, "explicit full refresh skipped");
                Console.WriteLine("READ-COUNTS eight-device initial=8 quiet=0 burst-of-100=1 explicit-refresh=8");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output incremental reads retain changes raised during an in-flight read", () =>
        {
            var a = Boundary.Add(EDataFlow.eRender); var b = Boundary.Add(EDataFlow.eRender);
            var monitor = new OutputVolumeMonitor(); var ids = new[] { a.Id, b.Id };
            try
            {
                monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                a.Volume.Scalar = .2f; a.Volume.Notify();
                a.Volume.OnVolumeRead = () => { a.Volume.OnVolumeRead = null; a.Volume.Scalar = .8f; a.Volume.Notify(); };
                var before = monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                var after = monitor.ReadAsync(ids, false).GetAwaiter().GetResult();
                assert(before[a.Id].Volume == 20 && after[a.Id].Volume == 80 && after.Count == 1
                    && b.Volume.VolumeReads == 1, "in-flight notification lost or unrelated endpoint read");
                var c = Boundary.Add(EDataFlow.eRender);
                var added = monitor.ReadAsync(new[] { b.Id, c.Id }, false).GetAwaiter().GetResult();
                assert(added.Count == 1 && added[c.Id].Volume == 50 && a.Volume.Unregistrations == 1
                    && b.Volume.VolumeReads == 1, "target changes forced unrelated reads");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output incremental notification failures still recover and read fresh state", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); device.Volume.RegistrationFailures = 1;
            var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(new[] { device.Id }, false).GetAwaiter().GetResult();
                assert(monitor.NeedsRecovery, "missing callback recovery suppressed");
                device.Volume.Scalar = .64f;
                var recovered = monitor.ReadAsync(new[] { device.Id }, false).GetAwaiter().GetResult();
                assert(!monitor.NeedsRecovery && recovered[device.Id].Volume == 64
                    && device.Volume.Registrations == 2 && Boundary.Activations == 1, "callback recovery returned stale state");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output stop during a read skips remaining endpoint work", () =>
        {
            var devices = Enumerable.Range(0, 8).Select(_ => Boundary.Add(EDataFlow.eRender)).ToArray();
            var monitor = new OutputVolumeMonitor();
            foreach (var device in devices) device.Volume.OnVolumeRead = monitor.Dispose;
            try
            {
                monitor.ReadAsync(devices.Select(d => d.Id), false).GetAwaiter().GetResult();
                monitor.Completion.GetAwaiter().GetResult();
                assert(devices.Sum(d => d.Volume.VolumeReads) == 1 && Boundary.Activations == 1
                    && devices.All(d => d.Volume.Callbacks.IsEmpty) && Boundary.Notifications.IsEmpty,
                    "collapse continued reading remaining endpoints");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output snapshots isolate device volume/mute and notifications do no COM work", () =>
        {
            var a = Boundary.Add(EDataFlow.eRender); a.Volume.Scalar = .23f;
            var b = Boundary.Add(EDataFlow.eRender); b.Volume.Scalar = .81f; b.Volume.Muted = true;
            var monitor = new OutputVolumeMonitor();
            try
            {
                var before = monitor.ReadAsync(new[] { a.Id, b.Id, "@@SYSTEM_DEFAULT@@", "" }).GetAwaiter().GetResult();
                assert(before.Count == 2 && before[a.Id] == (23, false) && before[b.Id] == (81, true), "device states mixed");
                int signals = 0; monitor.Invalidated += topology => { assert(!topology, "volume treated as topology"); signals++; };
                int reads = a.Volume.VolumeReads + b.Volume.VolumeReads;
                a.Volume.Scalar = .42f; a.Volume.Notify();
                assert(signals == 1 && a.Volume.VolumeReads + b.Volume.VolumeReads == reads && Boundary.Activations == 2,
                    "callback read or rebuilt COM");
                var after = monitor.ReadAsync(new[] { a.Id, b.Id }).GetAwaiter().GetResult();
                assert(after[a.Id].Volume == 42 && before[a.Id].Volume == 23 && after[b.Id] == before[b.Id], "mutable or cross-device snapshot");
                Thread.Sleep(120);
                assert(Boundary.Enumerators == 1 && a.Volume.Registrations == 1 && b.Volume.Registrations == 1 && !monitor.NeedsRecovery,
                    "healthy subscriptions rebuilt");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
            assert(a.Volume.Callbacks.IsEmpty && b.Volume.Callbacks.IsEmpty && Boundary.Notifications.IsEmpty, "callbacks retained");
        });
        check("output target replacement releases only removed endpoint and ignores virtual default", () =>
        {
            var a = Boundary.Add(EDataFlow.eRender); var b = Boundary.Add(EDataFlow.eRender);
            var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(new[] { a.Id, b.Id }).GetAwaiter().GetResult();
                var states = monitor.ReadAsync(new[] { b.Id, "@@SYSTEM_DEFAULT@@", "@@SYSTEM_DEFAULT_IN@@" }).GetAwaiter().GetResult();
                assert(states.Count == 1 && states.ContainsKey(b.Id) && a.Volume.Unregistrations == 1
                    && b.Volume.Unregistrations == 0 && Boundary.Activations == 2, "wrong endpoint retired");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output partial registration recovery retains healthy endpoints", () =>
        {
            var a = Boundary.Add(EDataFlow.eRender); var b = Boundary.Add(EDataFlow.eRender); b.Volume.RegistrationFailures = 1;
            var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(new[] { a.Id, b.Id }).GetAwaiter().GetResult(); assert(monitor.NeedsRecovery, "failure hidden");
                monitor.ReadAsync(new[] { a.Id, b.Id }).GetAwaiter().GetResult();
                assert(!monitor.NeedsRecovery && a.Volume.Registrations == 1 && b.Volume.Registrations == 2 && Boundary.Activations == 2, "healthy COM recreated");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output device callback failure can recover without a worker loop", () =>
        {
            Boundary.DeviceRegistrationFailures = 1; var device = Boundary.Add(EDataFlow.eRender);
            var monitor = new OutputVolumeMonitor();
            try
            {
                monitor.ReadAsync(new[] { device.Id }).GetAwaiter().GetResult(); assert(monitor.NeedsRecovery, "registration failure hidden");
                monitor.ReadAsync(new[] { device.Id }).GetAwaiter().GetResult();
                assert(!monitor.NeedsRecovery && Boundary.Enumerators == 1 && Boundary.Notifications.Count == 1, "notification recovery failed");
                int signals = 0; monitor.Invalidated += topology => { if (topology) signals++; };
                Boundary.Topology(); assert(signals == 1 && Boundary.Enumerators == 1, "callback did not defer topology work");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output unreadable device is disabled and reactivated only on a later request", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); device.Volume.VolumeReadFailures = 1;
            var monitor = new OutputVolumeMonitor();
            try
            {
                var first = monitor.ReadAsync(new[] { device.Id }).GetAwaiter().GetResult();
                assert(first[device.Id].Volume == -1 && monitor.NeedsRecovery && device.Volume.Unregistrations == 1, "unknown state presented as volume");
                var next = monitor.ReadAsync(new[] { device.Id }).GetAwaiter().GetResult();
                assert(next[device.Id].Volume == 50 && !monitor.NeedsRecovery && Boundary.Activations == 2, "failed endpoint not recovered");
            }
            finally { monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult(); }
        });
        check("output stop rejects late callbacks and further read requests", () =>
        {
            var device = Boundary.Add(EDataFlow.eRender); var monitor = new OutputVolumeMonitor(); int signals = 0;
            monitor.Invalidated += _ => signals++;
            monitor.ReadAsync(new[] { device.Id }).GetAwaiter().GetResult();
            var callback = device.Volume.Callbacks.Keys.Single(); var topology = Boundary.Notifications.Keys.Single();
            monitor.Dispose(); monitor.Completion.GetAwaiter().GetResult();
            Boundary.Callback(() => { callback.OnNotify(IntPtr.Zero); topology.OnDeviceAdded("late"); });
            var after = monitor.ReadAsync(new[] { device.Id }).GetAwaiter().GetResult();
            assert(signals == 0 && after.Count == 0 && Boundary.Enumerators == 1 && device.Volume.Unregistrations == 1
                && Boundary.Notifications.IsEmpty, "retired monitor resumed work");
        });
    }
}
