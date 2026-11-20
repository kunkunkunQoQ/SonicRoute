#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute.WindowsLink;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private sealed record BrowserRow(LinkBrowseEntry Entry, bool Drive)
    {
        public string Name => Entry.Name;
        public string Path => Entry.Path;
        public bool IsDirectory => Entry.IsDirectory;
        public string Detail => IsDirectory ? "›" : Entry.Length is long bytes ? WindowsLinkFileUi.FormatSize(bytes) : "";
        public Visibility SelectVisibility => Drive ? Visibility.Collapsed : Visibility.Visible;
    }

    private sealed class FileBrowser(WindowsLinkService service, string peer)
    {
        internal readonly WindowsLinkService Service = service;
        internal readonly string Peer = peer;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<Guid?> Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ObservableCollection<BrowserRow> Rows = new();
        internal readonly Stack<string> Parents = new();
        internal TaskCompletionSource? Work;
        internal Guid? Id;
        internal string Path = "";
        internal int? More;
        internal string? Message;
        internal bool Downloading;
    }

    private FileBrowser? _fileBrowser;
    private Task _closingFileBrowser = Task.CompletedTask;

    private async void WindowsBrowseFiles_Click(object sender, RoutedEventArgs e) => await OpenWindowsBrowserAsync();

    private async Task OpenWindowsBrowserAsync()
    {
        if (_disposed || _fileBrowser != null || !WindowsBrowseFiles.IsEnabled || SelectedWindowsLinkPeer is not { } peer
            || _filePeer is not { Connected: true, SupportsFileBrowse: true } target || target.Id != peer
            || WindowsLinkHub.Service is not { } service) return;
        var previousClose = _closingFileBrowser;
        var browser = _fileBrowser = new FileBrowser(service, peer);
        service.FileBrowserClosed += WindowsBrowserEnded;
        _fileError = _fileErrorPeer = null;
        WindowsBrowserEntries.ItemsSource = browser.Rows;
        WindowsFileBrowser.Visibility = Visibility.Visible;
        WindowsBrowserClose.Focus();
        await RunBrowserWorkAsync(browser, "Phone.LinkBrowseWaiting", async () =>
        {
            try
            {
                await previousClose;
                browser.Stop.Token.ThrowIfCancellationRequested();
                browser.Id = await service.OpenFileBrowserAsync(peer, browser.Stop.Token);
            }
            finally { browser.Opened.TrySetResult(browser.Id); }
            if (_fileBrowser == browser) await ReadWindowsFolderAsync(browser, "", 0);
        }, opening: true);
    }

    private void WindowsBrowserEnded(string peer, Guid id)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_fileBrowser is { } browser && browser.Peer == peer && browser.Id == id)
                CloseWindowsBrowser(L10n.T("Phone.LinkBrowseEnded"));
        }));
    }

    private void WindowsFileCard_Unloaded(object sender, RoutedEventArgs e) => CloseWindowsBrowser();

    private void WindowsFileCard_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) CloseWindowsBrowser();
    }

    private void WindowsBrowserClose_Click(object sender, RoutedEventArgs e)
    {
        CloseWindowsBrowser();
        WindowsBrowseFiles.Focus();
    }

    private void CloseWindowsBrowser(string? message = null)
    {
        if (_fileBrowser is not { } browser) return;
        _fileBrowser = null;
        browser.Service.FileBrowserClosed -= WindowsBrowserEnded;
        var work = browser.Work?.Task ?? Task.CompletedTask;
        browser.Stop.Cancel();
        _closingFileBrowser = EndWindowsBrowserAsync(browser, work);
        WindowsBrowserEntries.ItemsSource = null;
        WindowsFileBrowser.Visibility = Visibility.Collapsed;
        if (SelectedWindowsLinkPeer == browser.Peer && message != null)
        {
            _fileErrorPeer = browser.Peer;
            _fileError = message;
            WindowsFileStatus.Text = message;
            WindowsFileStatus.Visibility = Visibility.Visible;
        }
    }

    private static async Task EndWindowsBrowserAsync(FileBrowser browser, Task work)
    {
        try
        {
            if (await browser.Opened.Task is Guid id) await browser.Service.CloseFileBrowserAsync(browser.Peer, id);
        }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Close file browser: " + ex.Message); }
        finally { await work; browser.Stop.Dispose(); }
    }

    private async Task RunBrowserWorkAsync(FileBrowser browser, string statusKey, Func<Task> action, bool opening = false)
    {
        if (_fileBrowser != browser || browser.Work != null) return;
        var work = browser.Work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Message = L10n.T(statusKey);
        UpdateBrowserControls();
        try { await action(); }
        catch (Exception ex)
        {
            if (_fileBrowser != browser) return;
            string message = ex is UnauthorizedAccessException or OperationCanceledException
                ? L10n.T("Phone.LinkBrowseDeclined") : ex.Message;
            if (opening) CloseWindowsBrowser(message);
            else browser.Message = message;
        }
        finally
        {
            browser.Work = null;
            browser.Downloading = false;
            work.TrySetResult();
            if (_fileBrowser == browser) UpdateBrowserControls();
        }
    }

    private async Task ReadWindowsFolderAsync(FileBrowser browser, string path, int offset)
    {
        var page = await browser.Service.ListRemoteFilesAsync(browser.Peer, browser.Id!.Value, path, offset, browser.Stop.Token);
        if (_fileBrowser != browser) return;
        if (offset == 0)
        {
            browser.Rows.Clear();
            browser.Path = path;
        }
        foreach (var entry in page.Entries) browser.Rows.Add(new BrowserRow(entry, path.Length == 0));
        browser.More = page.NextOffset;
        browser.Message = browser.Rows.Count == 0 ? L10n.T("Phone.AndroidEmptyFolder") : null;
    }

    private Task NavigateWindowsFolderAsync(FileBrowser browser, string path, bool up)
    {
        string previous = browser.Path;
        return RunBrowserWorkAsync(browser, "Phone.ProcessingStatus", async () =>
        {
            await ReadWindowsFolderAsync(browser, path, 0);
            if (_fileBrowser != browser) return;
            if (up) browser.Parents.Pop(); else browser.Parents.Push(previous);
        });
    }

    private async void WindowsBrowserMore_Click(object sender, RoutedEventArgs e)
    {
        if (_fileBrowser is { More: int offset } browser)
            await RunBrowserWorkAsync(browser, "Phone.ProcessingStatus", () => ReadWindowsFolderAsync(browser, browser.Path, offset));
    }

    private async void WindowsBrowserUp_Click(object sender, RoutedEventArgs e)
    {
        if (_fileBrowser is { } browser && browser.Parents.TryPeek(out var parent))
            await NavigateWindowsFolderAsync(browser, parent, true);
    }

    private async void WindowsBrowserEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserRow row } || _fileBrowser is not { Work: null } browser) return;
        if (row.IsDirectory) await NavigateWindowsFolderAsync(browser, row.Path, false);
        else if (WindowsBrowserEntries.SelectedItems.Contains(row)) WindowsBrowserEntries.SelectedItems.Remove(row);
        else WindowsBrowserEntries.SelectedItems.Add(row);
    }

    private async void WindowsBrowserGet_Click(object sender, RoutedEventArgs e)
    {
        if (!WindowsBrowserGet.IsEnabled || _fileBrowser is not { Id: Guid id, Work: null } browser) return;
        var rows = WindowsBrowserEntries.SelectedItems.OfType<BrowserRow>().Where(row => !row.Drive).ToArray();
        if (rows.Length == 0) return;
        browser.Downloading = true;
        await RunBrowserWorkAsync(browser, "Phone.LinkBrowseDownloadWaiting", async () =>
        {
            foreach (var row in rows)
            {
                browser.Stop.Token.ThrowIfCancellationRequested();
                browser.Message = L10n.T("Phone.LinkBrowseDownloadWaiting") + " " + row.Name;
                UpdateBrowserControls();
                await browser.Service.DownloadRemoteFileAsync(browser.Peer, id, row.Entry, browser.Stop.Token);
                if (_fileBrowser != browser) return;
                WindowsBrowserEntries.SelectedItems.Remove(row);
                browser.Message = WindowsLinkFileUi.ForPeer(browser.Peer) is { State: LinkFileState.Completed, Incoming: true } file && file.Name == row.Name
                    ? WindowsLinkFileUi.Status(file) : string.Format(L10n.T("Phone.LinkBrowseReceived"), row.Name);
            }
        });
    }

    private void WindowsBrowserSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateBrowserControls();

    private async void WindowsBrowser_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; WindowsBrowserClose_Click(sender, e); }
        else if (e.Key == Key.Back && WindowsBrowserUp.IsEnabled) { e.Handled = true; WindowsBrowserUp_Click(sender, e); }
        else if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem { DataContext: BrowserRow { IsDirectory: true } row }
            && _fileBrowser is { Work: null } browser)
        {
            e.Handled = true;
            await NavigateWindowsFolderAsync(browser, row.Path, false);
        }
    }

    private void UpdateBrowserControls()
    {
        if (_fileBrowser is not { } browser || WindowsBrowserGet == null) return;
        bool idle = browser.Work == null;
        WindowsBrowserPath.Text = browser.Path.Length == 0 ? L10n.T("Phone.LinkBrowseDrives") : browser.Path;
        WindowsBrowserUp.IsEnabled = idle && browser.Parents.Count > 0;
        WindowsBrowserEntries.IsEnabled = idle;
        int count = WindowsBrowserEntries.SelectedItems.OfType<BrowserRow>().Count(row => !row.Drive);
        string action = L10n.T("Phone.AndroidReceiveToPc");
        WindowsBrowserGet.Content = count == 0 ? action : $"{action} ({count})";
        WindowsBrowserGet.IsEnabled = idle && count > 0
            && !WindowsLinkFileUi.IsSending(browser.Peer) && !WindowsLinkFileUi.Active(WindowsLinkFileUi.ForPeer(browser.Peer));
        WindowsBrowserMore.Visibility = browser.More.HasValue ? Visibility.Visible : Visibility.Collapsed;
        WindowsBrowserMore.IsEnabled = idle;
        string? status = browser.Downloading && WindowsLinkFileUi.ForPeer(browser.Peer) is { State: LinkFileState.Transferring, Incoming: true } file
            ? WindowsLinkFileUi.Status(file) : browser.Message;
        WindowsBrowserStatus.Text = status ?? "";
        WindowsBrowserStatus.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }
}
#endif
