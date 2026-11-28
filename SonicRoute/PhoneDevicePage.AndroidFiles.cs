#if SONICROUTE_PHONE
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using Control = System.Windows.Controls.Control;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SonicRoute;

public partial class PhoneDevicePage
{
    private static readonly ReceiveDirectorySettings AndroidReceiveFolder =
        new(Path.Combine(Path.GetDirectoryName(ConfigService.ConfigPath)!, "android-file-receive-folder.txt"));
    private CancellationTokenSource? _androidListing;
    private string? _androidViewSerial, _androidStatusSerial, _androidFileMessage;
    private string _androidFolder = AndroidFileTransfer.Downloads;
    private bool _choosingPhoneFolder, _fileDragOver;

    private string? TransferProfileId => _device == null ? null : AndroidAudioHost.Phone.FindProfile(_device)?.Id;
    private AndroidFileProgress? FileProgressState => TransferProfileId is { } id ? AndroidAudioHost.FileTransfers.Get(id) : null;

    private string? AndroidFileTarget() => !_disposed && !IsConnectionPage
        && FileProgressState?.Phase != AndroidFilePhase.Running && _device is { IsApp: false } && PhoneAudioWindowCache.Manager.IsCurrent(_device) ? _device.CommandTarget : null;

    private void UpdateAndroidFiles(bool connectionPage)
    {
        connectionPage |= (_device ?? AndroidAudioHost.Phone.Profile.LastDevice)?.IsApp == true;
        string? serial = connectionPage ? null : _device?.CommandTarget;
        ResetAndroidFilePicker(serial);
        FileTransferCard.Visibility = connectionPage ? Visibility.Collapsed : Visibility.Visible;
        if (connectionPage) return;
        bool ready = AndroidFileTarget() != null;
        SendFileButton.IsEnabled = AndroidChangeFolder.IsEnabled = AndroidBrowseFiles.IsEnabled = ready;
        if (!ready) SetFileDragOver(false);
        FileDropLabel.Text = L10n.T(serial != null ? "Phone.FileDropHint" : "Phone.FileOfflineHint");
        string folder = AndroidAudioHost.Phone.Profile.ReceiveFolder;
        AndroidReceivePath.Text = AndroidFolderLabel(folder);
        AndroidReceivePath.ToolTip = folder;
        LocalReceiveFolderPath.Text = AndroidReceiveFolder.GetReceiveDirectory();
        var progress = FileProgressState;
        bool working = progress?.Phase == AndroidFilePhase.Running;
        FileProgressRow.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        FileProgress.IsIndeterminate = working;
        FileStatus.Text = serial != null && _androidStatusSerial == serial && _androidFileMessage != null
            ? _androidFileMessage : progress?.Phase switch
        {
            AndroidFilePhase.Running => string.Format(L10n.T("Phone.FileBatchProgress"), progress.Index, progress.Count, progress.Text),
            AndroidFilePhase.Saved => string.Format(L10n.T("Phone.FileSaved"), progress.Text),
            AndroidFilePhase.InvalidFile => L10n.T("Phone.FileOnlyFiles"),
            AndroidFilePhase.Cancelled => L10n.T("Auto.Cancel"),
            AndroidFilePhase.Failed => progress.Text,
            _ => ""
        };
        FileStatus.Visibility = FileStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateAndroidFileAction();
    }

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        if (AndroidFileTarget() is not { } serial) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L10n.T("Phone.FileSend"), CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && AndroidFileTarget() == serial)
            await RunAndroidFilesAsync(serial, dialog.FileNames);
    }

    private void SetFileDragOver(bool over)
    {
        if (_fileDragOver == over) return;
        _fileDragOver = over;
        if (over) SendFileButton.SetResourceReference(Control.BorderBrushProperty, "Theme.Accent");
        else SendFileButton.ClearValue(Control.BorderBrushProperty);
    }

    private static bool HasFileDrop(DragEventArgs e) => e.AllowedEffects.HasFlag(DragDropEffects.Copy)
        && e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false);

    private void File_DragOver(object sender, DragEventArgs e)
    {
        bool accepted = AndroidFileTarget() != null && HasFileDrop(e);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetFileDragOver(accepted);
        e.Handled = true;
    }

    private void File_DragLeave(object sender, DragEventArgs e) => SetFileDragOver(false);

    private async void File_Drop(object sender, DragEventArgs e)
    {
        SetFileDragOver(false);
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (AndroidFileTarget() is not { } serial || !HasFileDrop(e)) return;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop, autoConvert: false) is not string[] { Length: > 0 } paths) return;
            e.Effects = DragDropEffects.Copy;
            await RunAndroidFilesAsync(serial, paths);
        }
        catch (ExternalException ex) { SetAndroidFileStatus(serial, ex.Message); }
    }

    private void CancelFile_Click(object sender, RoutedEventArgs e)
    {
        if (TransferProfileId is { } id) AndroidAudioHost.FileTransfers.Cancel(id);
    }

    private void FileTransferChanged(string id)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_active || _disposed || TransferProfileId != id) return;
            _androidFileMessage = null;
            UpdateAndroidFiles(IsConnectionPage);
        });
    }

    private void LocalReceiveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L10n.T("Phone.FileReceiveFolder"),
            InitialDirectory = AndroidReceiveFolder.GetReceiveDirectory(),
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            AndroidReceiveFolder.SetReceiveDirectory(dialog.FolderName);
            LocalReceiveFolderPath.Text = dialog.FolderName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), ex.Message, L10n.T("Phone.FileReceiveFolder"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetAndroidFileStatus(string serial, string? message)
    {
        _androidStatusSerial = serial;
        _androidFileMessage = message;
        if (!_disposed && _androidViewSerial == serial)
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

    private Task RunAndroidFilesAsync(string serial, string[] paths, string? pullFrom = null)
    {
        if (paths.Length == 0 || AndroidFileTarget() != serial || _device is not { } target || target.CommandTarget != serial)
            return Task.CompletedTask;
        try
        {
            string destination = pullFrom == null ? AndroidAudioHost.Phone.Profile.ReceiveFolder : AndroidReceiveFolder.GetReceiveDirectory();
            if (AndroidAudioHost.StartFileTransfer(AndroidAudioHost.Adb, target, paths, destination, pullFrom))
            {
                _androidFileMessage = null;
                CloseAndroidFilePicker();
                UpdateAndroidFiles(IsConnectionPage);
            }
        }
        catch (Exception ex) { SetAndroidFileStatus(serial, ex.Message); }
        return Task.CompletedTask;
    }
}
#endif
