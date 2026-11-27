using System.Diagnostics;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapVerifyScreenAsync(SrapSession session, CancellationToken token)
    {
        static async Task WaitGrantAsync(SrapSession session, bool active, CancellationToken token)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CheckGrant() { if ((session.ScreenGrant != null) == active) ready.TrySetResult(); }
            session.CapabilitiesChanged += CheckGrant;
            try { CheckGrant(); await ready.Task.WaitAsync(token); }
            finally { session.CapabilitiesChanged -= CheckGrant; }
        }
        Console.WriteLine("SCREEN_WAITING Phone PC page / Share phone screen / choose this computer / approve the system picker.");
        await WaitGrantAsync(session, true, token);
        try
        {
            var opening = Stopwatch.StartNew();
            await session.ViewScreenAsync("SonicRoute screen check", token);
            opening.Stop();
            var actual = session.ScreenPresentation;
            Check(actual.Frames > 0 && actual.Width > 0 && actual.Height > 0,
                $"MediaProjection H.264 produced a decoded and presented Native video frame ({actual.Width}x{actual.Height}, frames={actual.Frames})");
            Console.WriteLine(FormattableString.Invariant($"SCREEN_TIMING start=windows_view_open_after_grant firstPresentedFrameMs={opening.Elapsed.TotalMilliseconds:F1} width={actual.Width} height={actual.Height} presentedFrames={actual.Frames} opticalLatency=unmeasured"));
        }
        finally { await session.CloseScreenAsync(token); }
        await WaitGrantAsync(session, false, token);
        Check(!session.Token.IsCancellationRequested && session.ScreenGrant == null,
            "stopping the viewer revokes the single projection and preserves the authenticated connection");
    }

    private static void SrapScreenFramesCheck()
    {
        var a = new ScreenFrameAssembler();
        void Denied(Action action, string label)
        {
            try { action(); } catch (SrapException e) when (e.Code == "invalid_argument") { Check(true, label); return; }
            throw new InvalidDataException(label);
        }
        byte[] first = new byte[32768]; first[3] = 1; first[4] = 0x65;
        Denied(() => a.Append(first), "DATA requires an outstanding display.next");
        a.Begin(); a.Append(first); a.Append(new byte[] { 7, 8 });
        var merged = a.Complete(first.Length + 2);
        Check(merged.Span[..first.Length].SequenceEqual(first) && merged.Span[^1] == 8, "Annex-B DATA chunks reconstruct one access unit");
        a.Begin(); a.Append(first);
        Denied(() => a.Complete(first.Length - 1), "declared access unit size matches the bytes received");
        a.Begin(); a.Append(new byte[] { 1, 2, 3, 4, 5 });
        Denied(() => a.Complete(5), "only Annex-B is accepted");
        a.Begin();
        Denied(() => a.Append(new byte[32769]), "a single DATA frame is bounded to 32 KiB");
        for (int n = 0; n < ScreenFrameAssembler.MaxFrame / first.Length; n++) a.Append(first);
        Denied(() => a.Append(new byte[] { 1 }), "aggregate access unit remains bounded to 1 MiB");
    }
}
