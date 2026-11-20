using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Threading;
using SonicRoute;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private sealed class IsolatedClipboard : IDesktopClipboardLink
    {
        internal bool Listening, Shareable;
        internal int Reads, Writes;
        internal string Text = "inventory before first opt-in";
        internal TaskCompletionSource? DelayWrite;
        public long Revision { get; private set; }
        public event Action? Changed;
        public void SetListening(bool enabled) => Listening = enabled;
        internal void Copy(string text, bool shareable = true)
        {
            Text = text; Shareable = shareable; Revision++;
            if (Listening) Changed?.Invoke();
        }
        public Task<string?> ReadAsync(CancellationToken token)
        {
            Reads++;
            return Task.FromResult(Shareable ? Text : null);
        }
        public Task<(bool Written, long Revision)> WriteAsync(string text, long expectedRevision, CancellationToken token) =>
            WriteIfCurrentAsync(text, expectedRevision, static () => true, token);
        public async Task<(bool Written, long Revision)> WriteIfCurrentAsync(string text, long expectedRevision, Func<bool> current, CancellationToken token)
        {
            if (DelayWrite != null) await DelayWrite.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (expectedRevision != Revision || !current()) return (false, Revision);
            Writes++; Copy(text, shareable: false); return (true, Revision);
        }
        public void Dispose() => Listening = false;
    }

    private static async Task WindowsLinkClipboardChecksAsync()
    {
        var root = Directory.CreateTempSubdirectory("SonicRoute-clipboard-check-").FullName;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = limit.Token;
        WindowsLinkService Node(string name)
        {
            var node = new WindowsLinkService(Path.Combine(root, name), name, 0, discovery: false) { ProbeAudioCapture = _ => false };
            node.PairingRequested += pair => node.AcceptPairing(pair.Id, true);
            return node;
        }
        IDisposable Bridge(WindowsLinkService node, IsolatedClipboard desktop) => (IDisposable)Activator.CreateInstance(
            typeof(App).Assembly.GetType("SonicRoute.WindowsLinkClipboardSync")!, Hidden, null,
            [node, Dispatcher.CurrentDispatcher, desktop], null)!;
        async Task Wait(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(8, token);
        }
        try
        {
            // Registering/looking up format IDs never opens the system clipboard.
            Type native = typeof(App).Assembly.GetType("SonicRoute.DesktopClipboardLink")!;
            var privacy = native.GetMethod("HasPrivateFormat", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<Func<uint, bool>, Func<uint, bool>, bool>>();
            var passwordManager = native.GetMethod("IsPasswordManager", BindingFlags.Static | BindingFlags.NonPublic)!
                .CreateDelegate<Func<string, bool>>();
            var named = native.GetNestedType("Formats", BindingFlags.NonPublic)!;
            uint Format(string name) => (uint)named.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Check(new[] { "Remote", "IgnoreViewer", "Exclude", "Password" }.All(name => privacy(id => id == Format(name), _ => false))
                && new[] { "Cloud", "History" }.All(name => privacy(_ => false, id => id == Format(name)))
                && privacy(id => id == 15, _ => false) && !privacy(_ => false, _ => false)
                && passwordManager("KeePassXC") && passwordManager("1Password") && !passwordManager("notepad"),
                "native policy filters remote origins, files, exclusion and privacy flags, and known password-manager owners");
            await using var a = Node("A");
            await using var b = Node("B");
            await using var c = Node("C");
            var ca = new IsolatedClipboard(); var cb = new IsolatedClipboard(); var cc = new IsolatedClipboard();
            using var ba = Bridge(a, ca); using var bb = Bridge(b, cb); using var bc = Bridge(c, cc);
            await Task.WhenAll(a.EnableAsync(token), b.EnableAsync(token), c.EnableAsync(token));
            await a.ConnectAsync(b.LocalAddress, token: token);
            await b.ConnectAsync(c.LocalAddress, token: token);
            await Wait(() => a.Peers.Count(p => p.Connected) == 1 && b.Peers.Count(p => p.Connected) == 2 && c.Peers.Count(p => p.Connected) == 1);
            string idA = b.Peers.Single(p => p.Name == "A").Id;
            string idB = a.Peers.Single().Id;
            string idC = b.Peers.Single(p => p.Name == "C").Id;
            Check(a.GetClipboardState(idB) is { Enabled: false, Supported: true } && !ca.Listening && !cb.Listening,
                "paired current endpoints default to clipboard sync off");
            a.SetClipboardEnabled(idB, true);
            await Wait(() => b.GetClipboardState(idA).PeerEnabled);
            Check(!ca.Listening && !cb.Listening, "one-sided opt-in starts no clipboard listeners");
            b.SetClipboardEnabled(idA, true);
            b.SetClipboardEnabled(idC, true);
            c.SetClipboardEnabled(idB, true);
            await Wait(() => ca.Listening && cc.Listening && cb.Listening && b.ClipboardRoutes().Length == 2);
            Check(ca.Reads + cb.Reads + cc.Reads == 0, "mutual activation reads no existing clipboard content");
            ca.Copy("局域网 clipboard A → B");
            await Wait(() => cb.Writes == 1);
            ca.Copy("synthetic excluded item", shareable: false);
            ca.Copy(new string('文', WindowsLinkService.MaxClipboardBytes));
            ca.Copy("marker after skipped entries");
            await Wait(() => cb.Text == "marker after skipped entries");
            Check(cb.Writes == 2 && cc.Writes == 0 && ca.Writes == 0,
                "only new supported text crosses its authorized link and remote clipboard updates do not relay to a third device");
            cb.Copy("local B");
            await Wait(() => ca.Text == "local B" && cc.Text == "local B");
            Check(ca.Writes == 1 && cc.Writes == 1, "locally copied text reaches both explicitly enabled neighbors in both directions");
            var stale = a.ClipboardRoutes().Single();
            cb.DelayWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.ClipboardTextReceived += (_, _) => arrived.TrySetResult();
            ca.Copy("revoked in flight");
            await arrived.Task.WaitAsync(token);
            b.SetClipboardEnabled(idA, false);
            cb.DelayWrite.SetResult();
            await Wait(() => !ca.Listening);
            b.SetClipboardEnabled(idA, true);
            await Wait(() => ca.Listening);
            await a.SendClipboardTextAsync(stale, "old consent replay");
            ca.Copy("fresh epoch");
            await Wait(() => cb.Text == "fresh epoch");
            Check(cb.Writes == 3 && !a.CanSyncClipboard(stale), "re-activation rejects pending writes and snapshots from the revoked epoch");
            await a.DisconnectAsync(idB);
            await Wait(() => !ca.Listening && !b.GetClipboardState(idA).Connected);
            var previousReads = ca.Reads;
            ca.Copy("offline");
            await a.ConnectAsync(b.LocalAddress, idB, token);
            await Wait(() => ca.Listening);
            Check(ca.Reads == previousReads && cb.Text == "fresh epoch", "reconnect renews per-session opt-in without replaying an offline copy");
            await a.DisconnectAsync(idB);
            await Wait(() => !ca.Listening);
            await using (var restarted = Node("A"))
                Check(restarted.GetClipboardState(idB) is { Enabled: true, Connected: false }, "opt-in survives a local restart");
            a.SetClipboardEnabled(idB, false);
            Check(!a.GetClipboardState(idB).Enabled, "the local preference can be revoked offline");

            using var oldIdentity = LinkSecurity.LoadIdentity(Path.Combine(root, "older"));
            using var oldClient = new TcpClient(AddressFamily.InterNetwork);
            await oldClient.ConnectAsync(IPEndPoint.Parse(a.LocalAddress), token);
            var old = await LinkSecurity.AuthenticateAsync(oldClient, false, oldIdentity, null, "Older", ((IPEndPoint)oldClient.Client.LocalEndPoint!).Port,
                LinkCapabilities.AudioReceive, _ => false, (_, _) => true, (_, _, _, _) => Task.FromResult(true), true, token);
            using (old.Stream)
            {
                string older = LinkSecurity.Id(oldIdentity);
                await Wait(() => a.GetClipboardState(older).Connected);
                Check(!a.GetClipboardState(older).Supported && a.ClipboardRoutes().Length == 0,
                    "older peers stay connected with no clipboard frames or automatic grants");
                await a.DisconnectAsync(older);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
