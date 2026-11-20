using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int CheckAndroidFileCard()
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var assembly = typeof(App).Assembly;
        var cache = assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var host = assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        var phone = new AndroidDevice("file-card-fixture", "Phone", "device", false, "file-card-fixture");
        var discovery = new AndroidDeviceDiscovery(_ => throw new InvalidOperationException("Unexpected discovery scan."));
        discovery.Publish([phone]);
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        store.RememberDevice(phone);
        using var page = new PhoneDevicePage();
        void Show(string method) => typeof(PhoneDevicePage).GetMethod(method, Hidden)!.Invoke(page, null);
        T Control<T>(string name) where T : FrameworkElement => (T)page.FindName(name);
        var card = Control<Border>("FileTransferCard");
        var drop = Control<Button>("SendFileButton");
        var browse = Control<Button>("AndroidBrowseFiles");
        var picker = Control<FrameworkElement>("AndroidFilePicker");
        var path = Control<TextBlock>("AndroidReceivePath");
        var pc = Control<TextBlock>("LocalReceiveFolderPath");
        var settings = (ReceiveDirectorySettings)typeof(PhoneDevicePage).GetField("AndroidReceiveFolder", statics)!.GetValue(null)!;
        DragDropEffects Hover(DataObject content)
        {
            var e = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), Hidden, null,
                [content, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, drop, new Point(12, 12)], null)!;
            e.RoutedEvent = DragDrop.PreviewDragOverEvent;
            drop.RaiseEvent(e);
            return e.Effects;
        }

        Show("ShowDevice");
        Check(card.Visibility == Visibility.Visible && drop.IsEnabled && drop.AllowDrop && browse.IsEnabled
            && picker.Visibility == Visibility.Collapsed && Equals(path.ToolTip, AndroidFileTransfer.Downloads)
            && pc.Text == settings.GetReceiveDirectory(), "the saved phone opens with its file actions and both default destinations");
        var file = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(Path.GetTempPath(), "file-card.txt") });
        Check(Hover(file) == DragDropEffects.Copy && Hover(new DataObject(DataFormats.Text, "example")) == DragDropEffects.None,
            "the Android file drop target accepts file paths and declines text without starting a transfer");

        string target = Path.Combine(Environment.GetEnvironmentVariable("SONICROUTE_DATA_DIRECTORY")!, "received");
        settings.SetReceiveDirectory(Directory.CreateDirectory(target).FullName);
        store.SetReceiveFolder(store.Profile.Id, "/sdcard/Documents");
        Show("ShowDevice");
        Check(Equals(path.ToolTip, "/sdcard/Documents") && pc.Text == target, "both custom receive folders appear on the phone card");
        Show("ShowConnection");
        Check(card.Visibility == Visibility.Collapsed && Control<FrameworkElement>("ConnectionPanel").Visibility == Visibility.Visible,
            "opening the Android connection page hides file transfer");
        Control<RadioButton>("AirPlayConnectionChoice").IsChecked = true;
        Check(card.Visibility == Visibility.Collapsed && Control<FrameworkElement>("AirPlayCard").Visibility == Visibility.Visible,
            "switching to AirPlay keeps the phone file actions hidden");
        discovery.Publish([]);
        Show("ShowDevice");
        Check(card.Visibility == Visibility.Visible && !drop.IsEnabled && !browse.IsEnabled
            && picker.Visibility == Visibility.Collapsed && Hover(file) == DragDropEffects.None
            && !(bool)host.GetProperty("IsAdbCreated", statics)!.GetValue(null)!,
            "a disconnected phone keeps its saved folders and disables browsing and drops without initializing ADB");
        return 0;
    }

    private static int AndroidFilesUi(string output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var transport = new AdbSocketClient();
        var devices = transport.DevicesAsync(timeout.Token).GetAwaiter().GetResult();
        var phone = devices.Where(d => d.State == "device").OrderBy(d => d.IsWifi).First();
        var app = typeof(App).Assembly;
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var cache = app.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult(devices));
        discovery.Publish(devices);
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var store = (AndroidPhoneStore)app.GetType("SonicRoute.AndroidAudioHost")!.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(phone);

        const string downloads = "/sdcard/Download";
        const string child = "工作资料";
        const string decoded = "项目说明.txt";
        string encoded = Uri.EscapeDataString(decoded);
        const string ordinary = "测试记录.txt";
        const string longName = "项目会议记录与交付文件清单（包含试运行期间的现场反馈以及后续安排）.txt";
        string tag = "传输示例", folder = downloads + "/" + tag;
        var work = Directory.CreateTempSubdirectory("sonicroute-picker-").FullName;
        string pc = Directory.CreateDirectory(Path.Combine(work, "接收")).FullName;
        var fixtureFiles = new[] { encoded, ordinary };
        foreach (string name in fixtureFiles) File.WriteAllText(Path.Combine(work, name), "文件往返测试 " + name);
        var settings = typeof(PhoneDevicePage).GetField("AndroidReceiveFolder", statics)!.GetValue(null)!;
        settings.GetType().GetMethod("SetReceiveDirectory", Hidden)!.Invoke(settings, [pc]);

        using var page = new PhoneDevicePage();
        var window = new Window { Content = page, Width = 630, Height = 660, ShowInTaskbar = false };
        T Control<T>(string id) where T : FrameworkElement => (T)page.FindName(id);
        void Click(string id) => Control<Button>(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var entries = Control<ListBox>("AndroidFileEntries");
        var send = Control<Button>("SendFileButton");
        var picker = Control<FrameworkElement>("AndroidFilePicker");
        var path = Control<TextBlock>("AndroidFilePath");
        var get = Control<Button>("AndroidFileGet");
        object? Entry(string name) => entries.Items.Cast<object>().FirstOrDefault(item =>
            (string?)item.GetType().GetProperty("Name")?.GetValue(item) == name);
        ListBoxItem Row(string name)
        {
            var item = Entry(name)!;
            entries.ScrollIntoView(item);
            entries.UpdateLayout();
            return (ListBoxItem)entries.ItemContainerGenerator.ContainerFromItem(item);
        }
        void Open(string name)
        {
            var button = Descendants<Button>(Row(name)).Single(b => b.IsVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        bool In(string dir) => Equals(path.ToolTip, dir) && entries.ItemsSource != null;
        string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
        bool Shell(string command, CancellationToken token) =>
            transport.RunShellAsync(phone.Serial, command, token).GetAwaiter().GetResult().ExitCode == 0;
        bool madeFolder = false;
        try
        {
            if (!Shell("mkdir -p " + Quote(downloads), timeout.Token))
                throw new IOException("The test phone Downloads folder is unavailable.");
            // Never reuse a directory that was already present on the phone.
            if (!Shell("mkdir " + Quote(folder), timeout.Token))
            {
                tag += "-" + Guid.NewGuid().ToString("N")[..8];
                folder = downloads + "/" + tag;
                if (!Shell("mkdir " + Quote(folder), timeout.Token)) throw new IOException("Cannot create the fixture directory.");
            }
            madeFolder = true;
            if (!Shell("mkdir " + Quote(folder + "/" + child) + " && printf test > " + Quote(folder + "/" + longName), timeout.Token))
                throw new IOException("Cannot populate the fixture directory.");
            window.Show();
            typeof(PhoneDevicePage).GetMethod("ShowDevice", Hidden)!.Invoke(page, null);
            send.BringIntoView();
            DrainUi();
            Check(send.IsVisible && send.IsEnabled && send.AllowDrop && store.Profile.ReceiveFolder == downloads,
                "connected Android device exposes a file drop area and defaults to phone Downloads");
            Click("AndroidChangeFolder");
            Until(() => In(downloads) && Entry(tag) != null);
            Open(tag);
            Until(() => In(folder) && Entry(child) != null);
            Open(child);
            Until(() => In(folder + "/" + child) && get.IsEnabled);
            Click("AndroidFileUp");
            Until(() => In(folder) && Entry(child) != null && get.IsEnabled);
            Check(path.Text.Contains(tag) && !path.Text.Contains("/sdcard") && get.IsEnabled,
                "a folder button opens in one click, Up returns to its parent, and the breadcrumb retains its raw-path tooltip");
            Click("AndroidFileGet");
            Check(store.Profile.ReceiveFolder == folder && picker.Visibility == Visibility.Collapsed && send.IsVisible,
                "saving the current phone folder returns to the file drop area");

            send.BringIntoView();
            DrainUi();
            var paths = fixtureFiles.Select(name => Path.Combine(work, name)).ToArray();
            Check(OleDragOver(window, send, new DataObject(DataFormats.FileDrop, paths), .5, drop: true) == DragDropEffects.Copy,
                "the restored drop area accepts both Windows files");
            Until(() => send.IsEnabled && Control<TextBlock>("FileStatus").Text.Contains(folder));
            Click("AndroidBrowseFiles");
            Until(() => In(folder) && Entry(encoded) != null && Entry(ordinary) != null);
            Check(Descendants<TextBlock>(Row(encoded)).Any(text => text.IsVisible && text.Text == decoded)
                && Entry(decoded) == null, "the phone's percent-encoded filename is shown in Chinese while the raw name remains available for transfer");
            Check(entries.SelectionMode == SelectionMode.Multiple, "ordinary file rows allow independent multi-selection without modifier keys");
            // Drive the list's actual primary-click path on the test UI thread.
            ClickAndroidFileRow(Row(encoded));
            ClickAndroidFileRow(Row(ordinary));
            Check(entries.SelectedItems.Count == 2 && get.IsEnabled && get.Content?.ToString()?.Contains('2') == true,
                "two normal single clicks select both files and show the count on the enabled receive action");

            window.Width = 380;
            picker.BringIntoView();
            DrainUi();
            var viewport = Descendants<ScrollViewer>(entries).First();
            Check(viewport.ScrollableWidth <= .5 && entries.ActualWidth <= 380 && Entry(longName) != null,
                "long names fit a narrow phone picker without horizontal scrolling");
            SaveWindowRegion(Control<FrameworkElement>("FileTransferCard"), Path.Combine(output, "android-file-picker.png"));
            Click("AndroidFileGet");
            Until(() => send.IsEnabled && fixtureFiles.All(name => File.Exists(Path.Combine(pc, name))));
            Check(fixtureFiles.All(name => File.ReadAllBytes(Path.Combine(work, name)).SequenceEqual(File.ReadAllBytes(Path.Combine(pc, name))))
                && picker.Visibility == Visibility.Collapsed && send.IsVisible,
                "one receive action downloads both original filenames with identical bytes and restores the drop area");
            Click("AndroidChangeFolder");
            Until(() => In(folder) && Entry(child) != null);
            Open(child);
            Until(() => In(folder + "/" + child));
            Click("AndroidFileCancel");
            Check(store.Profile.ReceiveFolder == folder && picker.Visibility == Visibility.Collapsed && send.IsVisible && send.IsEnabled,
                "canceling the folder chooser leaves the saved destination and the drop area ready");
            return 0;
        }
        finally
        {
            window.Close();
            if (madeFolder)
            {
                using var clean = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var files = fixtureFiles.Append(longName).Select(name => Quote(folder + "/" + name));
                if (!Shell("rm -f " + string.Join(" ", files) + " && rmdir " + Quote(folder + "/" + child) + " && rmdir " + Quote(folder), clean.Token))
                    Console.Error.WriteLine("Phone fixture cleanup was incomplete.");
            }
            foreach (string name in fixtureFiles)
            {
                File.Delete(Path.Combine(work, name));
                File.Delete(Path.Combine(pc, name));
            }
            Directory.Delete(pc);
            Directory.Delete(work);
        }
    }

    private static void ClickAndroidFileRow(ListBoxItem row)
    {
        row.BringIntoView();
        DrainUi();
        var point = new Point(row.ActualWidth * .45, row.ActualHeight / 2);
        var saved = new byte[256];
        if (!GetKeyboardState(saved)) throw new IOException("Unable to read the UI fixture key state.");
        try
        {
            var keys = (byte[])saved.Clone();
            keys[1] = 0;
            SetKeyboardState(keys);
            ReportWpfMouse(row, point, "Activate, AbsoluteMove");
            ReportWpfMouse(row, point, "AbsoluteMove");
            keys[1] = 0x80;
            SetKeyboardState(keys);
            ReportWpfMouse(row, point, "Button1Press");
            keys[1] = 0;
            SetKeyboardState(keys);
            ReportWpfMouse(row, point, "Button1Release");
        }
        finally { SetKeyboardState(saved); }
    }

    private static void SaveWindowRegion(FrameworkElement card, string path)
    {
        card.BringIntoView();
        DrainUi();
        var page = (FrameworkElement)Window.GetWindow(card)!.Content;
        var bounds = ReferenceEquals(card, page) ? new Rect(page.RenderSize)
            : card.TransformToAncestor(page).TransformBounds(new Rect(card.RenderSize));
        const int scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth * scale), (int)Math.Ceiling(page.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var crop = new Int32Rect((int)Math.Ceiling(bounds.X * scale), (int)Math.Ceiling(bounds.Y * scale),
            (int)Math.Floor(bounds.Width * scale), (int)Math.Floor(bounds.Height * scale));
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, crop)));
        using var output = File.Create(path);
        png.Save(output);
    }
}
