using System.Text.Json;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task DisplayConcurrencyAsync(string output)
    {
        Directory.CreateDirectory(output);
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var device = new AndroidDevice("display-concurrency-fixture", "Fixture", "device", false);
        var app = new AndroidApp("fixture.app", "Fixture", 1001, "other");
        var clients = new List<RetiringDisplay>();
        RetiringDisplay Create() { var client = new RetiringDisplay(); clients.Add(client); return client; }
        await using (var manager = new AndroidVirtualDisplayManager(Create))
        {
            var first = (RetiringDisplay)await manager.OpenAsync(device, app, 0, 0);
            var appChannel = device with { Serial = new string('A', 64), IsApp = true, BoundAdbKey = AndroidDeviceIdentity.Key(device) };
            bool rejected = false;
            try { await manager.OpenAsync(appChannel, app, 0, 0); } catch (SrapException e) when (e.Code == "unsupported") { rejected = true; }
            Check(rejected && clients.Count == 1 && manager.Count == 1, "an App attachment cannot create or reuse the same phone's interactive ADB window");
            var selected = device with { ConnectionTarget = "generation-1" };
            Check(!AndroidConnectionManager.IsRequestedAdbDisplayAttachment(selected, appChannel) &&
                !AndroidConnectionManager.IsRequestedAdbDisplayAttachment(selected, selected with { ConnectionTarget = "generation-2" }) &&
                AndroidConnectionManager.IsRequestedAdbDisplayAttachment(selected, selected),
                "an asynchronous interactive-window resolution accepts only its selected ADB generation");
            first.BlockDisposal = true;
            var registryProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.OnCancel = () =>
            {
                var query = Task.Factory.StartNew(() => manager.Count, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                registryProbe.TrySetResult(query.Wait(TimeSpan.FromSeconds(3)));
            };
            var close = manager.CloseApplicationAsync(device, app.Package);
            var replacement = await manager.OpenAsync(device, app, 0, 0).WaitAsync(safety.Token);
            await first.DisposalEntered.Task.WaitAsync(safety.Token);
            Check(!ReferenceEquals(first, replacement) && clients.Count == 2 && manager.Count == 2,
                "close followed immediately by open creates a fresh client while retaining ownership of old resources");
            bool lockAvailable;
            try { lockAvailable = await registryProbe.Task.WaitAsync(safety.Token); }
            finally { first.ReleaseDisposal.TrySetResult(); }
            await close.WaitAsync(safety.Token);
            Check(lockAvailable, "cancellation callbacks can query the registry from another thread without a lock cycle");
            Check(manager.Count == 1 && ReferenceEquals(await manager.OpenAsync(device, app, 0, 0), replacement),
                "old cleanup cannot remove the newly opened entry sharing the same identity");
        }

        var slowStart = new RetiringDisplay { BlockStart = true };
        var fastStart = new RetiringDisplay(); int started = 0;
        await using (var manager = new AndroidVirtualDisplayManager(() => started++ == 0 ? slowStart : fastStart))
        {
            Task<IVirtualDisplayClient> pending = manager.OpenAsync(device, app, 0, 0);
            var close = manager.CloseDeviceAsync(device);
            Check(pending.IsCanceled, "closing a starting display cancels its pending ready task immediately");
            var replacement = await manager.OpenAsync(device, app, 0, 0).WaitAsync(safety.Token);
            Check(ReferenceEquals(replacement, fastStart), "a canceled startup cannot be reused by a subsequent open");
            slowStart.ReleaseStart.TrySetResult(); await close.WaitAsync(safety.Token);
            Check(manager.Count == 1 && slowStart.Disposed, "late completion of canceled startup disposes only the retired client");
        }

        var slowDispose = new RetiringDisplay { BlockDisposal = true };
        var afterTimeout = new RetiringDisplay(); started = 0;
        await using (var manager = new AndroidVirtualDisplayManager(() => started++ == 0 ? slowDispose : afterTimeout,
            TimeSpan.FromMilliseconds(90)))
        {
            await manager.OpenAsync(device, app, 0, 0);
            bool timedOut = false;
            try { await manager.CloseApplicationAsync(device, app.Package); } catch (TimeoutException) { timedOut = true; }
            Check(timedOut && manager.Count == 1 && !slowDispose.Disposed, "close has a bounded wait and keeps unfinished disposal visible to resource guards");
            Check(ReferenceEquals(await manager.OpenAsync(device, app, 0, 0), afterTimeout), "a cleanup timeout does not make a closing client reusable");
            slowDispose.ReleaseDisposal.TrySetResult();
            await WaitUntilAsync(() => slowDispose.Disposed && manager.Count == 1, TimeSpan.FromSeconds(2));
            Check(ReferenceEquals(await manager.OpenAsync(device, app, 0, 0), afterTimeout), "late timeout cleanup leaves the replacement registered");
        }

        var completed = new RetiringDisplay { BlockDisposal = true };
        var subsequent = new RetiringDisplay(); started = 0;
        await using (var manager = new AndroidVirtualDisplayManager(() => started++ == 0 ? completed : subsequent))
        {
            await manager.OpenAsync(device, app, 0, 0);
            completed.Finished.TrySetResult(); await completed.DisposalEntered.Task.WaitAsync(safety.Token);
            Check(ReferenceEquals(await manager.OpenAsync(device, app, 0, 0), subsequent),
                "natural completion removes the old entry before asynchronous disposal as well");
            completed.ReleaseDisposal.TrySetResult();
            await WaitUntilAsync(() => completed.Disposed && manager.Count == 1, TimeSpan.FromSeconds(2));
        }
        Check(clients.All(c => c.Disposed) && slowStart.Disposed && fastStart.Disposed && slowDispose.Disposed
            && afterTimeout.Disposed && completed.Disposed && subsequent.Disposed,
            "all simulated display resources retire after completion or cancellation");
        await File.WriteAllTextAsync(Path.Combine(output, "display-concurrency.json"), JsonSerializer.Serialize(new
        { Success = true, Assertions = _checks, RealDevice = false, NativeWindows = false, ProductTimeoutSeconds = 8,
            TestTimeoutMilliseconds = 90, NewBackgroundPolling = false }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class RetiringDisplay : IVirtualDisplayClient
    {
        internal bool BlockStart, BlockDisposal;
        internal bool Disposed;
        internal Action? OnCancel;
        internal readonly TaskCompletionSource ReleaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        public Task Completion => Finished.Task;
        public Task StartAsync(AndroidDevice device, AndroidApp app, int x, int y, CancellationToken token)
        {
            _registration = token.Register(() => OnCancel?.Invoke());
            return BlockStart ? ReleaseStart.Task : Task.CompletedTask;
        }
        public void Activate() { }
        public async ValueTask DisposeAsync()
        {
            DisposalEntered.TrySetResult();
            if (BlockDisposal) await ReleaseDisposal.Task;
            _registration.Dispose(); Disposed = true;
        }
    }
}
