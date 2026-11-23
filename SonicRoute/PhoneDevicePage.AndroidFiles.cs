#if SONICROUTE_PHONE
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.WindowsLink;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private static readonly ReceiveDirectorySettings AndroidReceiveFolder =
        new(Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "android-file-receive-folder.txt"));
    private CancellationTokenSource? _androidListing, _androidFileWork;
    private string? _androidViewSerial, _androidTransferSerial, _androidStatusSerial, _androidFileMessage;
    private string _androidFolder = AndroidFileTransfer.Downloads;
    private bool _choosingPhoneFolder;

    private string? AndroidFileTarget() => !_disposed && SelectedWindowsLinkPeer == null && !IsConnectionPage
        && _androidFileWork == null && _device != null && PhoneAudioWindowCache.Manager.IsCurrent(_device) ? _device.CommandTarget : null;

    private void UpdateAndroidFiles(bool connectionPage)
    {
        string? serial = connectionPage ? null : _device?.CommandTarget;
        ResetAndroidFilePicker(serial);
        WindowsFileCard.Visibility = connectionPage ? Visibility.Collapsed : Visibility.Visible;
        if (connectionPage) return;
        AndroidBrowseFiles.Visibility = AndroidReceiveFolderRow.Visibility = Visibility.Visible;
        bool ready = AndroidFileTarget() != null;
        SendWindowsFileButton.IsEnabled = AndroidChangeFolder.IsEnabled = AndroidBrowseFiles.IsEnabled = ready;
        if (!ready) SetWindowsFileDragOver(false);
        WindowsFileDropLabel.Text = L10n.T(serial != null ? "Phone.LinkFileDropHint" : "Phone.LinkFileOfflineHint");
        string folder = AndroidAudioHost.Phone.Profile.ReceiveFolder;
        AndroidReceivePath.Text = AndroidFolderLabel(folder);
        AndroidReceivePath.ToolTip = folder;
        WindowsReceiveFolderPath.Text = AndroidReceiveFolder.GetReceiveDirectory();
        bool working = serial != null && _androidTransferSerial == serial && _androidFileWork != null;
        WindowsFileProgressRow.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        WindowsFileProgress.IsIndeterminate = working;
        WindowsFileStatus.Text = serial != null && _androidStatusSerial == serial ? _androidFileMessage ?? "" : "";
        WindowsFileStatus.Visibility = WindowsFileStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateAndroidFileAction();
    }

    private void SetAndroidFileStatus(string serial, string? message)
    {
        _androidStatusSerial = serial;
        _androidFileMessage = message;
        if (!_disposed && SelectedWindowsLinkPeer == null && _androidViewSerial == serial)
            UpdateAndroidFiles(IsConnectionPage);
    }

    private void CloseAndroidFilePicker()
    {
        var list = _androidListing;
        _androidListing = null;
        list?.Cancel();
        AndroidFilePicker.Visibility = Visibility.Collapsed;
        AndroidFileEntries.ItemsSource = null;
    }

    private void ResetAndroidFilePicker(string? serial)
    {
        if (serial == _androidViewSerial) return;
        _androidViewSerial = serial;
        CloseAndroidFilePicker();
    }

    private async void AndroidBrowseFiles_Click(object sender, RoutedEventArgs e) =>
        await OpenAndroidPickerAsync(false);

    private async void AndroidChangeFolder_Click(object sender, RoutedEventArgs e) =>
        await OpenAndroidPickerAsync(true);

    private async Task OpenAndroidPickerAsync(bool chooseFolder)
    {
        if (AndroidFileTarget() is not { } serial) return;
        if (_choosingPhoneFolder == chooseFolder && AndroidFilePicker.Visibility == Visibility.Visible)
        { CloseAndroidFilePicker(); return; }
        _choosingPhoneFolder = chooseFolder;
        await ListAndroidFolderAsync(serial, AndroidAudioHost.Phone.Profile.ReceiveFolder);
    }

    private async Task ListAndroidFolderAsync(string serial, string folder)
    {
        _androidListing?.Cancel();
        var target = _device;
        if (target == null || target.CommandTarget != serial || !PhoneAudioWindowCache.Manager.IsCurrent(target)) return;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, PhoneAudioWindowCache.Manager.OperationToken(target));
        _androidListing = read;
        _androidFolder = folder;
        bool focusList = AndroidFilePicker.Visibility != Visibility.Visible || AndroidFileEntries.IsKeyboardFocusWithin;
        AndroidFilePicker.Visibility = Visibility.Visible;
        AndroidFilePath.Text = AndroidFolderLabel(folder);
        AndroidFilePath.ToolTip = folder;
        AndroidFileEntries.ItemsSource = null;
        if (focusList) AndroidFileEntries.Focus();
        AndroidFileUp.IsEnabled = folder != AndroidFileTransfer.Storage;
        AndroidFileEmpty.Text = L10n.T("Phone.ProcessingStatus");
        AndroidFileEmpty.Visibility = Visibility.Visible;
        UpdateAndroidFileAction();
        try
        {
            var files = await new AndroidFileTransfer(AndroidAudioHost.Adb, serial).ListAsync(folder, read.Token);
            if (_androidListing != read) return;
            AndroidFileEntries.ItemsSource = _choosingPhoneFolder ? files.Where(f => f.Directory).ToArray() : files;
            if (AndroidFileEntries.Items.Count > 0)
            {
                AndroidFileEmpty.Visibility = Visibility.Collapsed;
                AndroidFileEntries.ScrollIntoView(AndroidFileEntries.Items[0]);
            }
            else
                AndroidFileEmpty.Text = L10n.T(_choosingPhoneFolder ? "Phone.AndroidNoSubfolders" : "Phone.AndroidEmptyFolder");
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception ex) { if (_androidListing == read) AndroidFileEmpty.Text = ex.Message; }
        finally
        {
            if (_androidListing == read) { _androidListing = null; UpdateAndroidFileAction(); }
        }
    }

    private static string AndroidFolderLabel(string path)
    {
        string phone = L10n.T("Phone.AndroidStorage");
        if (path == AndroidFileTransfer.Storage) return phone;
        int split = path.LastIndexOf('/');
        string parent = path[..split];
        string downloads = L10n.T("Phone.AndroidDownloads");
        string current = path == AndroidFileTransfer.Downloads ? downloads : AndroidFileTransfer.FormatDisplayName(path[(split + 1)..]);
        string prefix = parent == AndroidFileTransfer.Storage ? phone : parent == AndroidFileTransfer.Downloads ? downloads : "…";
        return $"{prefix}  ›  {current}";
    }

    private async void AndroidFileUp_Click(object sender, RoutedEventArgs e)
    {
        if (AndroidFileTarget() is { } serial && _androidFolder != AndroidFileTransfer.Storage)
            await ListAndroidFolderAsync(serial, _androidFolder[.._androidFolder.LastIndexOf('/')]);
    }

    private async void AndroidFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AndroidStorageFile { Directory: true } folder }
            && AndroidFileTarget() is { } serial && _androidListing == null)
            await ListAndroidFolderAsync(serial, _androidFolder + "/" + folder.Name);
    }

    private void AndroidFileCancel_Click(object sender, RoutedEventArgs e) => DismissAndroidPicker();

    private void DismissAndroidPicker()
    {
        CloseAndroidFilePicker();
        (_choosingPhoneFolder ? AndroidChangeFolder : AndroidBrowseFiles).Focus();
    }

    private void AndroidFilePicker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; DismissAndroidPicker(); }
        else if (e.Key == Key.Back && AndroidFileUp.IsEnabled) { e.Handled = true; AndroidFileUp_Click(sender, e); }
    }

    private void AndroidFileSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateAndroidFileAction();

    private void UpdateAndroidFileAction()
    {
        if (AndroidFileGet == null || AndroidFilePicker.Visibility != Visibility.Visible) return;
        int count = AndroidFileEntries.SelectedItems.Cast<AndroidStorageFile>().Count(f => !f.Directory);
        string action = L10n.T(_choosingPhoneFolder ? "Phone.AndroidUseFolder" : "Phone.AndroidReceiveToPc");
        AndroidFileGet.Content = !_choosingPhoneFolder && count > 0 ? $"{action} ({count})" : action;
        AndroidFileGet.IsEnabled = _androidListing == null && AndroidFileTarget() != null
            && AndroidFileEntries.ItemsSource != null && (_choosingPhoneFolder || count > 0);
    }

    private async void AndroidFileGet_Click(object sender, RoutedEventArgs e) => await ActOnAndroidFilesAsync();

    private async void AndroidFileEntries_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (ItemsControl.ContainerFromElement(AndroidFileEntries, e.OriginalSource as DependencyObject)
                is ListBoxItem { DataContext: AndroidStorageFile { Directory: true } folder }
            && AndroidFileTarget() is { } serial)
        {
            e.Handled = true;
            await ListAndroidFolderAsync(serial, _androidFolder + "/" + folder.Name);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ActOnAndroidFilesAsync();
        }
    }

    private async Task ActOnAndroidFilesAsync()
    {
        if (!AndroidFileGet.IsEnabled || AndroidFileTarget() is not { } serial) return;
        if (_choosingPhoneFolder)
        {
            AndroidAudioHost.Phone.SetReceiveFolder(AndroidAudioHost.Phone.Profile.Id, _androidFolder);
            DismissAndroidPicker();
            UpdateAndroidFiles(IsConnectionPage);
        }
        else
        {
            string[] names = AndroidFileEntries.SelectedItems.Cast<AndroidStorageFile>().Where(f => !f.Directory)
                .Select(f => f.Name).ToArray();
            if (names.Length > 0) await RunAndroidFilesAsync(serial, names, _androidFolder);
        }
    }

    private async Task RunAndroidFilesAsync(string serial, string[] paths, string? pullFrom = null)
    {
        if (paths.Length == 0 || AndroidFileTarget() != serial) return;
        var target = _device;
        if (target == null || target.CommandTarget != serial) return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, PhoneAudioWindowCache.Manager.OperationToken(target));
        _androidFileWork = work;
        _androidTransferSerial = serial;
        try
        {
            CloseAndroidFilePicker();
            string phoneFolder = AndroidAudioHost.Phone.Profile.ReceiveFolder;
            string pcFolder = AndroidReceiveFolder.GetReceiveDirectory();
            var files = new AndroidFileTransfer(AndroidAudioHost.Adb, serial);
            string saved = "";
            if (pullFrom == null && !await Task.Run(() => paths.All(p => Path.IsPathFullyQualified(p) && File.Exists(p)), work.Token))
                throw new IOException(L10n.T("Phone.LinkFileOnlyFiles"));
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                work.Token.ThrowIfCancellationRequested();
                SetAndroidFileStatus(serial, string.Format(L10n.T("Phone.LinkFileBatchProgress"), i + 1, paths.Length, Path.GetFileName(path)));
                // Opening a source or creating a receive folder can block on a Windows network share.
                saved = await Task.Run(() => pullFrom == null
                    ? files.SendAsync(path, phoneFolder, work.Token)
                    : files.ReceiveAsync(pullFrom, path, pcFolder, work.Token), work.Token);
            }
            SetAndroidFileStatus(serial, string.Format(L10n.T("Phone.LinkFileSaved"), paths.Length == 1 ? saved : pullFrom == null ? phoneFolder : pcFolder));
        }
        catch (OperationCanceledException) { SetAndroidFileStatus(serial, L10n.T("Auto.Cancel")); }
        catch (Exception ex) { SetAndroidFileStatus(serial, ex.Message); }
        finally
        {
            _androidFileWork = null;
            _androidTransferSerial = null;
            if (!_disposed && SelectedWindowsLinkPeer == null) UpdateAndroidFiles(IsConnectionPage);
        }
    }
}
#endif
