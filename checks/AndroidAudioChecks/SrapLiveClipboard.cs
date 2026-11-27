using System.Security.Cryptography;
using System.Text;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static async Task SrapLiveClipboardAsync(string directory)
    {
        var store = new AndroidPhoneStore(Path.Combine(directory, "android-phone.json"));
        var saved = store.Profiles.Single(p => p.LastAppAddress != null && !p.AppTrustRevoked);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await using var transport = new SrapPhoneTransport(store, directory);
        var device = await transport.ReconnectAsync(saved, ct);
        var session = transport.Session(device) ?? throw new SrapException("stale_session");
        var allowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Capability() { if (session.AllowsFocusedClipboard) allowed.TrySetResult(); }
        session.CapabilitiesChanged += Capability;
        try
        {
            Console.WriteLine("CLIPBOARD_WAITING_FOR_LOCAL_OPT_IN_AND_FOCUS");
            Capability();
            await allowed.Task.WaitAsync(ct);
        }
        finally { session.CapabilitiesChanged -= Capability; }
        string token = Guid.NewGuid().ToString("N");
        string Mark(string part) => "SonicRoute-interop/" + part + "/" + token;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var client = new SrapClipboardClient(transport);
        var watching = client.WatchAsync(device, () => Console.WriteLine("CLIPBOARD_WATCH_READY " + token), value =>
        {
            if (value == Mark("ready")) start.TrySetResult();
            if (value == Mark("phone")) phone.TrySetResult();
        }, watchStop.Token);
        try
        {
            if (await Task.WhenAny(start.Task, watching, Task.Delay(TimeSpan.FromSeconds(22), ct)) != start.Task)
            {
                if (watching.IsCompleted) await watching;
                Console.WriteLine("SKIP no test-owned marker from an initially empty, focused phone clipboard.");
                return;
            }
            await client.SendAsync(Mark("pc"), client.Revision, ct);
            await phone.Task.WaitAsync(TimeSpan.FromSeconds(12), ct);
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Mark("phone"))));
            await client.SendAsync(Mark("done"), client.Revision, ct);
            Console.WriteLine("PASS native phone write/readback and phone-to-Windows memory sink SHA256=" + hash);
        }
        finally
        {
            watchStop.Cancel();
            try { await watching; } catch (OperationCanceledException) when (watchStop.IsCancellationRequested) { }
        }
    }
}
