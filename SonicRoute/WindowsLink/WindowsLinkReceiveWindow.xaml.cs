#if SONICROUTE_PHONE
using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using SonicRoute.WindowsLink;

namespace SonicRoute;

internal partial class WindowsLinkReceiveWindow : Window
{
    private readonly WindowsLinkService _service;
    internal WindowsLinkService Service => _service;
    private readonly LinkFileOffer? _offer;
    private readonly LinkBrowseApproval? _approval;
    private bool _answered;
    private LinkFileState _state = LinkFileState.Pending;
    private bool _accepted;
    private bool _closed;

    internal WindowsLinkReceiveWindow(WindowsLinkService service, LinkFileOffer offer, string sender)
    {
        _service = service;
        _offer = offer;
        InitializeComponent();
        SenderLabel.Text = string.Format(L10n.T(offer.IsDirectory ? "Phone.LinkFolderFrom" : "Phone.LinkFileFrom"), sender);
        FileLabel.Text = offer.Name;
        FileLabel.ToolTip = offer.Name;
        FileSizeLabel.Text = WindowsLinkFileUi.FormatSize(offer.Length);
        FolderLabel.ToolTip = FolderLabel.Text = service.GetReceiveDirectory();
    }

    internal WindowsLinkReceiveWindow(WindowsLinkService service, LinkBrowseApproval approval, string sender)
    {
        _service = service;
        _approval = approval;
        InitializeComponent();
        SenderLabel.Text = string.Format(L10n.T(approval.IsDownload ? "Phone.LinkBrowseDownloadPrompt" : "Phone.LinkBrowsePrompt"), sender);
        FileLabel.Text = approval.IsDownload ? Path.GetFileName(approval.Path) : L10n.T("Phone.LinkBrowseDrives");
        FileLabel.ToolTip = approval.IsDownload ? approval.Path : null;
        FileSizeLabel.Text = approval.IsDownload ? approval.Path : L10n.T("Phone.LinkBrowseScope");
        FileSizeLabel.ToolTip = FileSizeLabel.Text;
        FileSizeLabel.TextWrapping = TextWrapping.Wrap;
        FolderLabel.Visibility = SaveAsButton.Visibility = Visibility.Collapsed;
        if (approval.IsDownload && approval.IsDirectory)
        {
            FolderLabel.Text = L10n.T("Phone.LinkBrowseFolderScope");
            FolderLabel.TextWrapping = TextWrapping.Wrap;
            FolderLabel.Visibility = Visibility.Visible;
        }
        SaveButton.Content = L10n.T("Phone.LinkBrowseAllow");
    }

    internal void DismissApproval()
    {
        _answered = true;
        if (!_closed) Close();
    }

    internal void Update(LinkFileProgress progress)
    {
        _state = progress.State;
        if (!_accepted && _state is LinkFileState.Cancelled or LinkFileState.Rejected or LinkFileState.Failed)
        {
            Close();
            return;
        }
        bool pending = _state == LinkFileState.Pending;
        bool active = _state == LinkFileState.Transferring;
        SaveAsButton.Visibility = SaveButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        FolderLabel.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        ReceiveProgress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ReceiveProgress.Value = WindowsLinkFileUi.Fraction(progress);
        DeclineButton.Content = L10n.T(pending ? "Phone.WindowsLinkPairDecline" : active ? "Auto.Cancel" : "Phone.LinkFileClose");
        if (active)
            FileSizeLabel.Text = $"{WindowsLinkFileUi.FormatSize(progress.BytesTransferred)} / {WindowsLinkFileUi.FormatSize(progress.Length)}";
        else if (!pending)
        {
            ResultLabel.Text = WindowsLinkFileUi.Status(progress);
            ResultLabel.Visibility = Visibility.Visible;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_approval != null)
        {
            if (!_answered)
            {
                _answered = true;
                _service.ReplyFileBrowse(_approval.Id, true);
            }
            if (!_closed) Close();
            return;
        }
        if (_state == LinkFileState.Pending && !_accepted) await AcceptAsync(null);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_offer == null || _state != LinkFileState.Pending || _accepted) return;
        if (_offer.IsDirectory)
        {
            var folder = new Microsoft.Win32.OpenFolderDialog { Title = L10n.T("Phone.LinkFileChangeFolder"), InitialDirectory = FolderLabel.Text };
            if (folder.ShowDialog(this) == true && !_closed && _state == LinkFileState.Pending)
                await AcceptAsync(folder.FolderName);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L10n.T("Phone.LinkFileAccept"), FileName = _offer.Name, InitialDirectory = FolderLabel.Text,
            AddExtension = false, CheckPathExists = true, OverwritePrompt = false
        };
        dialog.FileOk += (_, args) =>
        {
            if (!File.Exists(dialog.FileName)) return;
            args.Cancel = true;
            System.Windows.MessageBox.Show(this, L10n.T("Phone.LinkFileExists"), L10n.T("Phone.LinkFiles"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        };
        if (dialog.ShowDialog(this) == true && !_closed && _state == LinkFileState.Pending)
            await AcceptAsync(dialog.FileName);
    }

    private async Task AcceptAsync(string? destination)
    {
        if (_offer == null) return;
        _accepted = true;
        SaveAsButton.IsEnabled = SaveButton.IsEnabled = false;
        try
        {
            if (destination == null) await _service.ReceiveFileInDefaultDirectoryAsync(_offer.Id);
            else if (_offer.IsDirectory) await _service.ReceiveFileInDirectoryAsync(_offer.Id, destination);
            else await _service.ReceiveFileAsync(_offer.Id, destination);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_closed) return;
            ResultLabel.Text = ex.Message;
            ResultLabel.Visibility = Visibility.Visible;
            if (_state == LinkFileState.Pending)
            {
                _accepted = false;
                SaveAsButton.IsEnabled = SaveButton.IsEnabled = true;
            }
        }
    }

    private void Decline_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        _closed = true;
        if (_approval != null)
        {
            if (!_answered) { _answered = true; _service.ReplyFileBrowse(_approval.Id, false); }
            return;
        }
        if (_offer == null) return;
        if (_state == LinkFileState.Pending && !_accepted) _service.RejectFile(_offer.Id);
        else if (_state is LinkFileState.Pending or LinkFileState.Transferring) _service.CancelFile(_offer.Id);
    }
}
#endif
