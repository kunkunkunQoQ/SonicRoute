using System.Diagnostics;
using SonicRoute.Core;
using SonicRoute.Core.Interop;

namespace AudioPathChecks;

internal static partial class Program
{
    private static void RunTuningChecks()
    {
#if !BASELINE
        Case("concurrent volume adjustments retain read-modify-write exclusion", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(998) { Volume = .1f });
            SessionVolumeService.Refresh(true);
            Parallel.For(0, 4, _ =>
            {
                for (int i = 0; i < 20; i++)
                    Assert(SessionVolumeService.AdjustVolumePercent(998, 1).Applied, "adjust failed");
            });
            Assert(SessionVolumeService.ReadState(998).Volume == 90, "adjustment lost");
        });
        Case("concurrent forced refresh waiters wake and share completed generation", () =>
        {
            Boundary.Add(EDataFlow.eRender, new Session(999) { Volume = .32f });
            using var scanning = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int first = 0;
            Boundary.BeforeScan = () =>
            {
                if (Interlocked.Increment(ref first) != 1) return;
                scanning.Set();
                if (!release.Wait(5000)) throw new TimeoutException("scan not released");
            };
            Exception? failure = null;
            void Refresh() { try { SessionVolumeService.Refresh(true); } catch (Exception e) { failure = e; } }
            var owner = new Thread(Refresh) { IsBackground = true };
            var waiter = new Thread(Refresh) { IsBackground = true };
            try
            {
                owner.Start();
                Assert(scanning.Wait(3000), "scan did not begin");
                waiter.Start();
                Assert(SpinWait.SpinUntil(() => (waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 3000), "refresh did not wait");
            }
            finally { release.Set(); owner.Join(5000); if ((waiter.ThreadState & System.Threading.ThreadState.Unstarted) == 0) waiter.Join(5000); Boundary.BeforeScan = null; }
            Assert(!owner.IsAlive && !waiter.IsAlive && failure == null, "refresh deadlock or failure");
            Assert(Boundary.Enumerators == 1 && SessionVolumeService.ReadState(999).Volume == 32, "refresh repeated or stale");
        });
#endif
    }

    // Controlled product-method benchmark with substituted OS boundaries, never user audio.
    private static int SessionPerformance(bool concurrent)
    {
#if NET10_0_OR_GREATER
        Boundary.Reset();
        Boundary.Add(EDataFlow.eRender, new Session(998) { Volume = .5f });
        SessionVolumeService.Refresh(true);
        for (int i = 0; i < 200_000; i++) SessionVolumeService.ReadState(998);
        ThreadPool.SetMinThreads(4, 4);
        if (concurrent) Parallel.For(0, 4, _ => { for (int i = 0; i < 50_000; i++) SessionVolumeService.ReadState(998); });
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        int sum = 0;
        const int count = 2_000_000;
        if (concurrent)
            Parallel.For(0, 4, _ => { int part = 0; for (int i = 0; i < count / 4; i++) part += SessionVolumeService.ReadState(998).Volume; Interlocked.Add(ref sum, part); });
        else
            for (int i = 0; i < count; i++) sum += SessionVolumeService.ReadState(998).Volume;
        watch.Stop();
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert(sum == count * 50, "invalid benchmark result");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Method = "SessionVolumeService.ReadState", Concurrent = concurrent, Iterations = count, ElapsedMs = watch.Elapsed.TotalMilliseconds, MainThreadAllocatedBytes = bytes, Runtime = Environment.Version.ToString(), OsBoundaries = "FAKES_ONLY" }));
        return 0;
#else
        throw new NotSupportedException("NET10 performance probe only");
#endif
    }
}
