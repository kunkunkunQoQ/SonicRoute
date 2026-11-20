using System.Threading.Channels;
using SonicRoute.WindowsLink;

internal static partial class Program
{
    private static async Task WindowsLinkFileBrowserAsync()
    {
        var root = Directory.CreateTempSubdirectory("SonicRoute-browse-");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(80));
            var token = timeout.Token;
            WindowsLinkService Node(string name) => new(Path.Combine(root.FullName, name), name, 0, discovery: false)
                { ProbeAudioCapture = _ => false };
            await using var a = Node("A");
            await using var b = Node("B");
            var aPrompts = Channel.CreateUnbounded<LinkBrowseApproval>();
            var bPrompts = Channel.CreateUnbounded<LinkBrowseApproval>();
            var aOffers = Channel.CreateUnbounded<LinkFileOffer>();
            var bOffers = Channel.CreateUnbounded<LinkFileOffer>();
            a.FileBrowseRequested += p => aPrompts.Writer.TryWrite(p);
            b.FileBrowseRequested += p => bPrompts.Writer.TryWrite(p);
            a.FileOffered += p => aOffers.Writer.TryWrite(p);
            b.FileOffered += p => bOffers.Writer.TryWrite(p);
            a.PairingRequested += p => a.AcceptPairing(p.Id, true);
            b.PairingRequested += p => b.AcceptPairing(p.Id, true);
            string aIn = root.CreateSubdirectory("A-received").FullName;
            string bIn = root.CreateSubdirectory("B-received").FullName;
            a.SetReceiveDirectory(aIn);
            b.SetReceiveDirectory(bIn);
            var aSource = root.CreateSubdirectory("A-files");
            var bSource = root.CreateSubdirectory("B-files");
            const string folder = "项目包", data = "资源.bin", empty = "空文件夹", nested = "子目录", zero = "空.txt";
            byte[] MakePackage(DirectoryInfo source, int seed)
            {
                var binary = new byte[320003];
                new Random(seed).NextBytes(binary);
                var package = source.CreateSubdirectory(folder);
                package.CreateSubdirectory(empty);
                File.WriteAllBytes(Path.Combine(package.CreateSubdirectory(nested).FullName, data), binary);
                File.WriteAllBytes(Path.Combine(package.FullName, zero), []);
                return binary;
            }
            byte[] aPayload = MakePackage(aSource, 123), bPayload = MakePackage(bSource, 321);
            bool SamePackage(string path, byte[] expected) => Directory.Exists(Path.Combine(path, empty))
                && Directory.GetFileSystemEntries(Path.Combine(path, empty)).Length == 0
                && File.ReadAllBytes(Path.Combine(path, nested, data)).SequenceEqual(expected)
                && File.Exists(Path.Combine(path, zero)) && new FileInfo(Path.Combine(path, zero)).Length == 0;
            static async Task<bool> Denied(Task action)
            {
                try { await action; return false; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return true; }
            }

            await Task.WhenAll(a.EnableAsync(token), b.EnableAsync(token));
            await a.ConnectAsync(b.LocalAddress, token: token);
            await WaitUntilAsync(() => a.Peers.Any(p => p.Connected) && b.Peers.Any(p => p.Connected), token);
            string idA = b.Peers.Single().Id, idB = a.Peers.Single().Id;
            var rejected = a.OpenFileBrowserAsync(idB, token);
            var pending = await bPrompts.Reader.ReadAsync(token);
            Check(!rejected.IsCompleted && !pending.IsDownload && pending.PeerId == idA
                && await Denied(a.ListRemoteFilesAsync(idB, pending.Id, "", token: token)),
                "paired browsing waits for the remote approval and even a known pending session cannot list disks");
            b.ReplyFileBrowse(pending.Id, false);
            Check(await Denied(rejected) && !Directory.EnumerateFileSystemEntries(aIn).Any(),
                "declining a browse request releases no disk listing or received items");
            async Task<Guid> OpenNormally()
            {
                var opening = a.OpenFileBrowserAsync(idB, token);
                var request = await bPrompts.Reader.ReadAsync(token);
                Check(!request.IsDownload && !opening.IsCompleted, "each new ordinary browse session requires a new decision");
                b.ReplyFileBrowse(request.Id, true);
                return await opening;
            }

            Guid first = await OpenNormally();
            var disks = await a.ListRemoteFilesAsync(idB, first, "", token: token);
            Check(disks.Entries.Any(d => string.Equals(d.Path, bSource.Root.FullName, StringComparison.OrdinalIgnoreCase))
                && disks.Entries.All(d => d.IsDirectory && d.Path == Path.GetPathRoot(d.Path)),
                "an approved session enumerates local disk roots without recursively opening their contents");
            var selected = (await a.ListRemoteFilesAsync(idB, first, bSource.FullName, token: token)).Entries.Single();
            var askDownload = a.DownloadRemoteFileAsync(idB, first, selected, token);
            pending = await bPrompts.Reader.ReadAsync(token);
            Check(pending.IsDownload && pending.IsDirectory && pending.Path == selected.Path && !askDownload.IsCompleted,
                "a normal folder download identifies the selected directory and waits for the source computer");
            b.ReplyFileBrowse(pending.Id, false);
            Check(await Denied(askDownload) && Directory.GetFileSystemEntries(aIn).Length == 0,
                "declining a directory download leaves the receiver empty and keeps the browser usable");
            var download = a.DownloadRemoteFileAsync(idB, first, selected, token);
            b.ReplyFileBrowse((await bPrompts.Reader.ReadAsync(token)).Id, true);
            await download;
            Check(SamePackage(Path.Combine(aIn, folder), bPayload) && !aOffers.Reader.TryPeek(out _),
                "the authorized browser download preserves binary data, an empty file, and an empty folder without another receiver prompt");
            await a.CloseFileBrowserAsync(idB, first);
            Check(await Denied(a.ListRemoteFilesAsync(idB, first, bSource.FullName, token: token)),
                "a closed browser cannot reuse its previous permission");
            Guid second = await OpenNormally();
            await a.CloseFileBrowserAsync(idB, second);

            await Task.WhenAll(a.BeginFullTrustAsync(idB, token), b.BeginFullTrustAsync(idA, token));
            await WaitUntilAsync(() => a.GetFullTrustState(idB).PeerCodeReady && b.GetFullTrustState(idA).PeerCodeReady, token);
            var codes = (A: a.GetFullTrustState(idB).LocalCode!, B: b.GetFullTrustState(idA).LocalCode!);
            await a.SubmitFullTrustCodeAsync(idB, codes.B, token);
            Check(!a.IsFullyTrusted(idB) && !b.IsFullyTrusted(idA), "one verified device does not grant unattended browsing");
            second = await OpenNormally();
            await a.CloseFileBrowserAsync(idB, second);
            await b.SubmitFullTrustCodeAsync(idA, codes.A, token);
            await WaitUntilAsync(() => a.IsFullyTrusted(idB) && b.IsFullyTrusted(idA), token);

            Guid fromB = await a.OpenFileBrowserAsync(idB, token), fromA = await b.OpenFileBrowserAsync(idA, token);
            var selectA = (await b.ListRemoteFilesAsync(idA, fromA, aSource.FullName, token: token)).Entries.Single();
            await b.DownloadRemoteFileAsync(idA, fromA, selectA, token);
            await a.DownloadRemoteFileAsync(idB, fromB, selected, token);
            Check(SamePackage(Path.Combine(bIn, folder), aPayload) && SamePackage(Path.Combine(aIn, folder), bPayload)
                && SamePackage(Path.Combine(aIn, folder + " (1)"), bPayload)
                && !aPrompts.Reader.TryPeek(out _) && !bPrompts.Reader.TryPeek(out _) && !bOffers.Reader.TryPeek(out _),
                "mutually trusted computers browse and download folders both ways without prompts and keep both colliding directory names");
            var files = await b.ListRemoteFilesAsync(idA, fromA, Path.Combine(selectA.Path, nested), token: token);
            var one = files.Entries.Single();
            await b.DownloadRemoteFileAsync(idA, fromA, one, token);
            Check(!one.IsDirectory && one.Length == aPayload.Length && File.ReadAllBytes(Path.Combine(bIn, data)).SequenceEqual(aPayload),
                "selecting an individual file preserves its name, length and bytes");
            await b.RevokeFullTrustAsync(idA, token);
            await WaitUntilAsync(() => !a.IsFullyTrusted(idB) && !b.IsFullyTrusted(idA), token);
            Check(await Denied(a.ListRemoteFilesAsync(idB, fromB, "", token: token)) && await Denied(b.ListRemoteFilesAsync(idA, fromA, "", token: token)),
                "revoking trust terminates both already-open browser sessions");
            Guid ordinary = await OpenNormally();
            await a.DisconnectAsync(idB);
            await WaitUntilAsync(() => !a.Peers.Single().Connected && !b.Peers.Single().Connected, token);
            await a.ConnectAsync(b.LocalAddress, idB, token);
            await WaitUntilAsync(() => a.Peers.Single().Connected && b.Peers.Single().Connected, token);
            Check(await Denied(a.ListRemoteFilesAsync(idB, ordinary, "", token: token)),
                "a new connection cannot resume the previous connection's approved browser");

            var large = bSource.CreateSubdirectory("取消示例");
            using (var file = File.Create(Path.Combine(large.FullName, "large.bin"))) file.SetLength(12 * 1024 * 1024);
            var send = b.SendPathAsync(idA, large.FullName, token);
            var offer = await aOffers.Reader.ReadAsync(token);
            int sawBytes = 0;
            void Cancel(LinkFileProgress progress)
            {
                if (progress.Id != offer.Id || progress.State != LinkFileState.Transferring || progress.BytesTransferred <= 0) return;
                Interlocked.Exchange(ref sawBytes, 1);
                a.CancelFile(offer.Id);
            }
            a.FileTransferChanged += Cancel;
            try
            {
                var receive = a.ReceiveFileInDefaultDirectoryAsync(offer.Id, token);
                Check(offer.IsDirectory && await Denied(send) && await Denied(receive) && sawBytes == 1
                    && !Directory.Exists(Path.Combine(aIn, large.Name)) && !Directory.EnumerateFileSystemEntries(aIn, "*.partial").Any(),
                    "cancelling after directory bytes reach the receiver removes all temporary contents and publishes no partial folder");
            }
            finally { a.FileTransferChanged -= Cancel; }
        }
        finally { root.Delete(true); }
    }
}
