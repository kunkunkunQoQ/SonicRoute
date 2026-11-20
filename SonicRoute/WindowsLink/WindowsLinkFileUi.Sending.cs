#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal static partial class WindowsLinkFileUi
{
    // Shared by device pages on the UI dispatcher, so a batch survives navigating away.
    private sealed class SendBatch(int count)
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly int Count = count;
        internal int Number;
        internal string Name = "";
    }

    private static readonly Dictionary<string, SendBatch> Sending = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsSending(string peerId) => Sending.ContainsKey(peerId);

    internal static string? BatchStatus(string peerId, LinkFileProgress? transfer)
    {
        if (!Sending.TryGetValue(peerId, out var batch)) return transfer == null ? null : Status(transfer);
        string detail = transfer != null && !transfer.Incoming && Active(transfer) && transfer.Name == batch.Name
            ? Status(transfer) : batch.Name;
        return batch.Count > 1 ? string.Format(L10n.T("Phone.LinkFileBatchProgress"), batch.Number, batch.Count, detail) : detail;
    }

    internal static bool CancelBatch(string peerId)
    {
        if (!Sending.TryGetValue(peerId, out var batch)) return false;
        batch.Stop.Cancel();
        return true;
    }

    internal static async Task SendFilesAsync(WindowsLinkService service, string peerId, string[] paths)
    {
        if (paths.Length == 0 || IsSending(peerId) || Active(ForPeer(peerId))) return;
        var batch = new SendBatch(paths.Length) { Number = 1, Name = Path.GetFileName(paths[0]) };
        Sending.Add(peerId, batch);
        var token = batch.Stop.Token;
        try
        {
            Changed?.Invoke(peerId);
            // Check the entire drop once, before sending any item. Avoid filesystem I/O during DragOver.
            bool supportsFolders = service.Peers.Any(peer => peer.Id == peerId && peer.Connected && peer.SupportsFileBrowse);
            await Task.Run(() =>
            {
                foreach (string path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path)))
                        throw new IOException(string.Format(L10n.T("Phone.LinkFileFailed"), Path.GetFileName(path)));
                    if (!supportsFolders && Directory.Exists(path)) throw new IOException(L10n.T("Phone.LinkBrowseUpdate"));
                }
            }, token);
            for (int i = 0; i < paths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i > 0)
                {
                    batch.Number = i + 1;
                    batch.Name = Path.GetFileName(paths[i]);
                    Changed?.Invoke(peerId);
                }
                await service.SendPathAsync(peerId, paths[i], token);
            }
        }
        finally
        {
            Sending.Remove(peerId);
            batch.Stop.Dispose();
            Changed?.Invoke(peerId);
        }
    }
}
#endif
