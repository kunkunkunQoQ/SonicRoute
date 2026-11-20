#if SONICROUTE_PHONE
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using SonicRoute.WindowsLink;
using Control = System.Windows.Controls.Control;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private LinkPeer? _filePeer;
    private string? _fileErrorPeer, _fileError;
    private bool _fileDragOver;

    private void WindowsFileChanged(string peerId)
    {
        if (_disposed || !_active || SelectedWindowsLinkPeer != peerId || _filePeer?.Id != peerId) return;
        if (WindowsLinkFileUi.Active(WindowsLinkFileUi.ForPeer(peerId))) _fileError = _fileErrorPeer = null;
        UpdateWindowsFileCard(_filePeer);
    }

    private void UpdateWindowsFileCard(LinkPeer peer)
    {
        if (_fileBrowser != null && (_fileBrowser.Peer != peer.Id || !peer.Connected || !peer.SupportsFileBrowse)) CloseWindowsBrowser();
        if (_filePeer?.Id != peer.Id) SetWindowsFileDragOver(false);
        _filePeer = peer;
        if (!peer.Connected && _fileErrorPeer == peer.Id) _fileError = _fileErrorPeer = null;
        var transfer = WindowsLinkFileUi.ForPeer(peer.Id);
        bool active = WindowsLinkFileUi.Active(transfer);
        bool sending = WindowsLinkFileUi.IsSending(peer.Id);
        bool supported = (peer.Capabilities & LinkCapabilities.FileTransfer) != 0;
        WindowsBrowseActions.Visibility = Visibility.Visible;
        WindowsBrowseFiles.IsEnabled = peer.Connected && peer.SupportsFileBrowse && !active && !sending;
        WindowsSendFolder.IsEnabled = WindowsBrowseFiles.IsEnabled;
        string? upgrade = peer.Connected && !peer.SupportsFileBrowse ? L10n.T("Phone.LinkBrowseUpdate") : null;
        WindowsSendFolder.ToolTip = WindowsBrowseFiles.ToolTip = upgrade;
        WindowsFileDropLabel.Text = L10n.T(!peer.Connected ? "Phone.LinkFileOfflineHint" :
            peer.SupportsFileBrowse ? "Phone.LinkFolderDropHint" : "Phone.LinkFileDropHint");
        SendWindowsFileButton.IsEnabled = peer.Connected && supported && !active && !sending;
        if (!SendWindowsFileButton.IsEnabled) SetWindowsFileDragOver(false);
        WindowsFileProgressRow.Visibility = active || sending ? Visibility.Visible : Visibility.Collapsed;
        WindowsFileProgress.IsIndeterminate = active ? transfer!.State == LinkFileState.Pending : sending;
        WindowsFileProgress.Value = active ? WindowsLinkFileUi.Fraction(transfer!) : 0;
        string? status = _fileErrorPeer == peer.Id && _fileError != null ? _fileError
            : peer.Connected && !supported ? L10n.T("Phone.LinkFileUpdatePeer")
            : WindowsLinkFileUi.BatchStatus(peer.Id, transfer);
        WindowsFileStatus.Text = status ?? "";
        WindowsFileStatus.Visibility = status == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateBrowserControls();
    }

    private async void SendWindowsFile_Click(object sender, RoutedEventArgs e)
    {
        if (!HasFileTarget()) return;
        var peerId = FileTarget();
        var phone = AndroidFileTarget();
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L10n.T("Phone.LinkFileSend"), CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (phone != null && AndroidFileTarget() == phone) await RunAndroidFilesAsync(phone, dialog.FileNames);
        else if (peerId != null && FileTarget() == peerId) await SendWindowsFilesAsync(peerId, dialog.FileNames);
    }

    private async void WindowsSendFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!WindowsSendFolder.IsEnabled || FileTarget() is not { } peer || _filePeer?.SupportsFileBrowse != true) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = L10n.T("Phone.LinkSendFolder"), Multiselect = false };
        if (picker.ShowDialog(Window.GetWindow(this)) == true && FileTarget() == peer && _filePeer?.SupportsFileBrowse == true)
            await SendWindowsFilesAsync(peer, [picker.FolderName]);
    }

    private string? FileTarget() => !_disposed && SendWindowsFileButton.IsEnabled && _filePeer?.Id == SelectedWindowsLinkPeer
        ? SelectedWindowsLinkPeer : null;

    private void SetWindowsFileDragOver(bool over)
    {
        if (_fileDragOver == over) return;
        _fileDragOver = over;
        if (over) SendWindowsFileButton.SetResourceReference(Control.BorderBrushProperty, "Theme.Accent");
        else SendWindowsFileButton.ClearValue(Control.BorderBrushProperty);
    }

    private bool HasFileTarget() => FileTarget() != null || AndroidFileTarget() != null;

    private bool CanDropWindowsFiles(DragEventArgs e) => HasFileTarget() && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
        && e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false);

    private void WindowsFile_DragOver(object sender, DragEventArgs e)
    {
        bool accepted = CanDropWindowsFiles(e);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetWindowsFileDragOver(accepted);
        e.Handled = true;
    }

    private void WindowsFile_DragLeave(object sender, DragEventArgs e) => SetWindowsFileDragOver(false);

    private async void WindowsFile_Drop(object sender, DragEventArgs e)
    {
        SetWindowsFileDragOver(false);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!CanDropWindowsFiles(e)) return;
        var peerId = FileTarget();
        var phone = AndroidFileTarget();
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] { Length: > 0 } paths) return;
            e.Effects = DragDropEffects.Copy;
            if (phone != null) await RunAndroidFilesAsync(phone, paths);
            else if (peerId != null) await SendWindowsFilesAsync(peerId, paths);
        }
        catch (ExternalException ex)
        {
            if (phone != null) SetAndroidFileStatus(phone, ex.Message);
            else if (peerId != null) { _fileError = ex.Message; _fileErrorPeer = peerId; WindowsFileChanged(peerId); }
        }
    }

    private async Task SendWindowsFilesAsync(string peerId, string[] paths)
    {
        if (WindowsLinkHub.Service is not { } service) return;
        _fileError = _fileErrorPeer = null;
        var previous = WindowsLinkFileUi.ForPeer(peerId);
        try { await WindowsLinkFileUi.SendFilesAsync(service, peerId, paths); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var last = WindowsLinkFileUi.ForPeer(peerId);
            if (last?.Id == previous?.Id || last?.State is not (LinkFileState.Rejected or LinkFileState.Cancelled))
            {
                _fileError = ex.Message;
                _fileErrorPeer = peerId;
            }
        }
        WindowsFileChanged(peerId);
    }

    private void CancelWindowsFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWindowsLinkPeer == null) { _androidFileWork?.Cancel(); return; }
        if (SelectedWindowsLinkPeer is not { } peerId || WindowsLinkFileUi.CancelBatch(peerId)
            || WindowsLinkFileUi.ForPeer(peerId) is not { } transfer) return;
        if (transfer.Incoming && transfer.State == LinkFileState.Pending) WindowsLinkHub.Service?.RejectFile(transfer.Id);
        else if (WindowsLinkFileUi.Active(transfer)) WindowsLinkHub.Service?.CancelFile(transfer.Id);
    }
}
#endif
