#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal static partial class WindowsLinkFileUi
{
    private static readonly Dictionary<string, LinkFileProgress> Latest = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Guid, WindowsLinkReceiveWindow> Prompts = new();
    private static readonly Dictionary<Guid, WindowsLinkReceiveWindow> BrowsePrompts = new();
    private static volatile WindowsLinkService? _service;
    internal static event Action<string>? Changed;

    internal static void Attach(WindowsLinkService service)
    {
        _service = service;
        service.FileOffered += FileOffered;
        service.FileBrowseRequested += BrowseRequested;
        service.FileBrowseRequestEnded += BrowseRequestEnded;
        service.FileTransferChanged += TransferChanged;
    }

    internal static void Detach(WindowsLinkService service)
    {
        service.FileOffered -= FileOffered;
        service.FileBrowseRequested -= BrowseRequested;
        service.FileBrowseRequestEnded -= BrowseRequestEnded;
        service.FileTransferChanged -= TransferChanged;
        if (_service == service) _service = null;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        void Clear()
        {
            foreach (var prompt in Prompts.Values.Where(p => p.Service == service).ToArray()) prompt.Close();
            foreach (var prompt in BrowsePrompts.Values.Where(p => p.Service == service).ToArray()) prompt.Close();
            if (_service == null) Latest.Clear();
        }
        if (dispatcher.CheckAccess()) Clear();
        else dispatcher.BeginInvoke((Action)Clear);
    }

    internal static LinkFileProgress? ForPeer(string peerId) => Latest.GetValueOrDefault(peerId);
    internal static bool Active(LinkFileProgress? transfer) => transfer?.State is LinkFileState.Pending or LinkFileState.Transferring;
    internal static double Fraction(LinkFileProgress transfer) => transfer.Length > 0
        ? Math.Clamp((double)transfer.BytesTransferred / transfer.Length, 0, 1) : 0;

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MiB",
        >= 1L << 10 => $"{bytes / 1024d:0.#} KiB",
        _ => $"{bytes} B"
    };

    internal static string Status(LinkFileProgress transfer)
    {
        string key = transfer.State switch
        {
            LinkFileState.Pending => transfer.Incoming ? "Phone.LinkFileIncomingPending" : "Phone.LinkFileWaiting",
            LinkFileState.Completed => transfer.Incoming ? "Phone.LinkFileSaved" : "Phone.LinkFileDelivered",
            LinkFileState.Cancelled => "Phone.LinkFileCancelled",
            LinkFileState.Rejected => "Phone.LinkFileRejected",
            LinkFileState.Failed => "Phone.LinkFileFailed",
            _ => ""
        };
        return key.Length > 0 ? string.Format(L10n.T(key), transfer.SavedPath ?? transfer.Name)
            : $"{transfer.Name}  ·  {FormatSize(transfer.BytesTransferred)} / {FormatSize(transfer.Length)}";
    }

    private static void TransferChanged(LinkFileProgress transfer)
    {
        var service = _service;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || service == null) return;
        dispatcher.BeginInvoke((Action)(() =>
        {
            if (service != _service) return;
            if (Prompts.TryGetValue(transfer.Id, out var prompt)) prompt.Update(transfer);
            if (transfer.State != LinkFileState.Pending && Latest.TryGetValue(transfer.PeerId, out var current)
                && current.Id != transfer.Id) return;
            Latest[transfer.PeerId] = transfer;
            Changed?.Invoke(transfer.PeerId);
        }));
    }

    private static void BrowseRequested(LinkBrowseApproval approval)
    {
        var service = _service;
        if (service == null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            service.ReplyFileBrowse(approval.Id, false);
            return;
        }
        dispatcher.BeginInvoke((Action)(() =>
        {
            if (service != _service || !service.IsFileBrowseRequestPending(approval.Id) || BrowsePrompts.ContainsKey(approval.Id)) return;
            var peer = service.Peers.FirstOrDefault(p => p.Id == approval.PeerId && p.Connected);
            if (peer == null) { service.ReplyFileBrowse(approval.Id, false); return; }
            var prompt = new WindowsLinkReceiveWindow(service, approval, peer.DisplayName);
            BrowsePrompts.Add(approval.Id, prompt);
            prompt.Closed += (_, _) => BrowsePrompts.Remove(approval.Id);
            prompt.Show();
        }));
    }

    private static void BrowseRequestEnded(Guid id)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke((Action)(() =>
        {
            if (BrowsePrompts.TryGetValue(id, out var prompt)) prompt.DismissApproval();
        }));
    }

    private static void FileOffered(LinkFileOffer offer)
    {
        var service = _service;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (service == null) return;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
        {
            service.RejectFile(offer.Id);
            return;
        }
        dispatcher.BeginInvoke((Action)(() =>
        {
            if (service != _service || Prompts.ContainsKey(offer.Id)) return;
            var last = ForPeer(offer.PeerId);
            if (last?.Id == offer.Id && !Active(last)) return;
            var peer = service.Peers.FirstOrDefault(p => p.Id == offer.PeerId && p.Connected);
            if (peer == null) { service.RejectFile(offer.Id); return; }
            var prompt = new WindowsLinkReceiveWindow(service, offer, peer.DisplayName);
            Prompts.Add(offer.Id, prompt);
            prompt.Closed += (_, _) => Prompts.Remove(offer.Id);
            prompt.Show();
        }));
    }
}
#endif
