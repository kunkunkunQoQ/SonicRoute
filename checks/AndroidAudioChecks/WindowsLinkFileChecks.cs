using System.Security.Cryptography;
using System.Threading.Channels;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WindowsLinkFileChecksAsync(string? sourceExecutable)
    {
        string root = Directory.CreateTempSubdirectory("SonicRoute-file-check-").FullName;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var token = timeout.Token;
            await CheckFolderArchiveAsync(root, token);
            await using var a = new WindowsLinkService(Path.Combine(root, "a"), "Sender", 0, discovery: false)
                { ProbeAudioCapture = _ => false };
            await using var b = new WindowsLinkService(Path.Combine(root, "b"), "Receiver", 0, discovery: false)
                { ProbeAudioCapture = _ => false };
            a.PairingRequested += p => a.AcceptPairing(p.Id, true);
            b.PairingRequested += p => b.AcceptPairing(p.Id, true);
            await Task.WhenAll(a.EnableAsync(token), b.EnableAsync(token));
            await a.ConnectAsync(b.LocalAddress, token: token);
            await WaitUntilAsync(() => a.Peers.Any(p => p.Connected) && b.Peers.Any(p => p.Connected), token);
            var peer = a.Peers.Single(p => p.Connected);
            var offers = Channel.CreateUnbounded<LinkFileOffer>();
            b.FileOffered += offer => offers.Writer.TryWrite(offer);
            string saveDir = Directory.CreateDirectory(Path.Combine(root, "received")).FullName;
            string downloads = b.GetReceiveDirectory();
            b.SetReceiveDirectory(saveDir);
            await using (var restored = new WindowsLinkService(Path.Combine(root, "b"), "Settings", 0, discovery: false))
                Check(Path.IsPathFullyQualified(downloads) && restored.GetReceiveDirectory() == saveDir,
                    "the local receive folder defaults to a fully qualified Windows Downloads path and persists a custom choice");
            string source = Path.Combine(root, "测试包.exe");
            File.Copy(sourceExecutable ?? Environment.ProcessPath!, source);
            string saved = Path.Combine(saveDir, "received.exe");

            async Task<(Task Send, LinkFileOffer Offer)> OfferAsync()
            {
                Task send = a.SendFileAsync(peer.Id, source, token);
                return (send, await offers.Reader.ReadAsync(token));
            }
            static async Task<bool> FailsAsync(Task task)
            {
                try { await task; return false; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { return true; }
            }
            bool Clean() => !Directory.EnumerateFiles(saveDir, "*.partial").Any();

            var first = await OfferAsync();
            Check(peer.Trusted && peer.Capabilities.HasFlag(LinkCapabilities.FileTransfer)
                && first.Offer.Name == Path.GetFileName(source) && first.Offer.Length == new FileInfo(source).Length
                && !first.Send.IsCompleted && Directory.GetFileSystemEntries(saveDir).Length == 0,
                "paired TLS file transfer waits for explicit acceptance before creating a destination");
            Check(await FailsAsync(a.SendFileAsync(peer.Id, source, token)),
                "a second file for the same peer is refused while an offer is pending");
            Task<LinkRemoteApp[]>? catalogDuringTransfer = null;
            void RequestCatalog(LinkFileProgress progress)
            {
                if (progress.Id == first.Offer.Id && progress.State == LinkFileState.Transferring)
                    catalogDuringTransfer ??= b.RequestCatalogAsync(progress.PeerId, token: token, audioOnly: true);
            }
            b.FileTransferChanged += RequestCatalog;
            using var process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            try { await Task.WhenAll(b.ReceiveFileAsync(first.Offer.Id, saved, token), first.Send); }
            finally { b.FileTransferChanged -= RequestCatalog; }
            double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            double allocationMiB = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / (1024.0 * 1024);
            Console.WriteLine(FormattableString.Invariant(
                $"Loopback package {first.Offer.Length / 1048576.0 / elapsed:F1} MiB/s, {(process.TotalProcessorTime - cpu).TotalMilliseconds:F1} ms process CPU, {allocationMiB:F2} MiB allocated across both nodes."));
            await catalogDuringTransfer!;
            Check(a.Peers.Single().Connected && b.Peers.Single().Connected,
                "catalog traffic completes on the same TLS connection during a package transfer");
            byte[] hash;
            await using (var stream = File.OpenRead(source)) hash = await SHA256.HashDataAsync(stream, token);
            await using (var stream = File.OpenRead(saved))
                Check((await SHA256.HashDataAsync(stream, token)).SequenceEqual(hash) && Clean(),
                    "an executable with a Unicode offer name arrives byte-for-byte at the chosen path");
            Console.WriteLine($"File bytes {first.Offer.Length}, SHA-256 {Convert.ToHexString(hash)}");

            var exists = await OfferAsync();
            Check(await FailsAsync(b.ReceiveFileAsync(exists.Offer.Id, saved, token)) && await FailsAsync(exists.Send),
                "the receiver refuses an existing destination and reports the failure to the sender");
            await using (var stream = File.OpenRead(saved))
                Check((await SHA256.HashDataAsync(stream, token)).SequenceEqual(hash) && Clean(),
                    "an existing file keeps its original contents");
            var reject = await OfferAsync();
            b.RejectFile(reject.Offer.Id);
            Check(await FailsAsync(reject.Send) && Clean(), "declining a file leaves no partial file");

            File.WriteAllBytes(source, []);
            string initialName = Path.Combine(saveDir, Path.GetFileName(source));
            File.WriteAllText(initialName, "keep this file");
            Directory.CreateDirectory(Path.Combine(saveDir, "测试包 (1).exe"));
            var defaultOffer = await OfferAsync();
            await Task.WhenAll(defaultOffer.Send, b.ReceiveFileInDefaultDirectoryAsync(defaultOffer.Offer.Id, token));
            Check(File.ReadAllText(initialName) == "keep this file" && File.Exists(Path.Combine(saveDir, "测试包 (2).exe")) && Clean(),
                "one-click acceptance uses the configured folder and numbers collisions with either files or directories");

            // A local folder may disappear after the user selected it. No partial files survive an I/O failure.
            string removedFolder = Directory.CreateDirectory(Path.Combine(root, "disappearing")).FullName;
            b.SetReceiveDirectory(removedFolder);
            Directory.Delete(removedFolder);
            File.WriteAllText(removedFolder, "preserve");
            var unwritable = await OfferAsync();
            Check(await FailsAsync(b.ReceiveFileInDefaultDirectoryAsync(unwritable.Offer.Id, token)) && await FailsAsync(unwritable.Send)
                && File.ReadAllText(removedFolder) == "preserve" && Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories).Length == 0,
                "a receive folder replaced by a file reports failure on both peers without falling back to another folder");
            b.SetReceiveDirectory(null);
            Check(b.GetReceiveDirectory() == downloads, "resetting the receive folder restores the Windows Downloads location");
            b.SetReceiveDirectory(saveDir);

            string empty = Path.Combine(saveDir, "empty.dat");
            var zero = await OfferAsync();
            await Task.WhenAll(b.ReceiveFileAsync(zero.Offer.Id, empty, token), zero.Send);
            Check(File.Exists(empty) && new FileInfo(empty).Length == 0 && Clean(),
                "zero-length files complete after both sides acknowledge the transfer");

            // A multi-window payload gives each interruption an actual partially written file.
            await using (var output = File.OpenWrite(source)) output.SetLength(4 * 1024 * 1024);
            string interrupted = Path.Combine(saveDir, "partial.exe");
            var cancel = await OfferAsync();
            void OnBytes(LinkFileProgress progress)
            {
                if (progress.Id == cancel.Offer.Id && progress.State == LinkFileState.Transferring && progress.BytesTransferred > 0)
                    b.CancelFile(progress.Id);
            }
            b.FileTransferChanged += OnBytes;
            try
            {
                Check(await FailsAsync(b.ReceiveFileAsync(cancel.Offer.Id, interrupted, token)) && await FailsAsync(cancel.Send)
                    && !File.Exists(interrupted) && Clean() && a.Peers.Single().Connected,
                    "cancelling after actual file data arrived cleans the partial file and preserves the paired connection");
            }
            finally { b.FileTransferChanged -= OnBytes; }

            var disconnect = await OfferAsync();
            Task? disconnecting = null;
            void Disconnect(LinkFileProgress progress)
            {
                if (progress.Id == disconnect.Offer.Id && progress.State == LinkFileState.Transferring && progress.BytesTransferred > 0)
                    disconnecting ??= b.DisconnectAsync(progress.PeerId);
            }
            b.FileTransferChanged += Disconnect;
            try
            {
                Check(await FailsAsync(b.ReceiveFileAsync(disconnect.Offer.Id, interrupted, token))
                    && await FailsAsync(disconnect.Send) && !File.Exists(interrupted) && Clean(),
                    "disconnecting during a transfer fails both sides and removes the receiver's partial file");
                await disconnecting!;
            }
            finally { b.FileTransferChanged -= Disconnect; }
            Check(new[] { @"..\escape.exe", @"C:\escape.exe", "payload.exe:side", "con.exe", "fake\u202e.exe" }
                    .All(name => !WindowsLinkService.IsSafeTransferName(name)),
                "wire names exclude paths, alternate streams, device names and direction overrides");

            // Negotiate an actual control session whose hello predates the file capability.
            using var oldIdentity = LinkSecurity.LoadIdentity(Path.Combine(root, "legacy-peer"));
            using var oldClient = new TcpClient(AddressFamily.InterNetwork);
            await oldClient.ConnectAsync(IPEndPoint.Parse(a.LocalAddress), token);
            int fixturePort = ((IPEndPoint)oldClient.Client.LocalEndPoint!).Port;
            var oldPeer = await LinkSecurity.AuthenticateAsync(oldClient, false, oldIdentity, null, "Legacy fixture", fixturePort,
                LinkCapabilities.AudioReceive, _ => false, (_, _) => true, (_, _, _, _) => Task.FromResult(true), true, token);
            using (oldPeer.Stream)
            {
                string oldId = LinkSecurity.Id(oldIdentity);
                await WaitUntilAsync(() => a.Peers.Any(p => p.Id == oldId && p.Connected), token);
                bool unavailable = false;
                try { await a.SendFileAsync(oldId, source, token); }
                catch (NotSupportedException) { unavailable = true; }
                Check(unavailable && a.ReadTraffic(oldId).Sent == 0 && a.Peers.Any(p => p.Id == oldId && p.Connected),
                    "a paired legacy client stays connected without receiving unsupported file frames");
                await a.DisconnectAsync(oldId);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
