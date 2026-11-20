using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;

internal static partial class Program
{
    private static int DeviceActionsUi(string output)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);
        bool LazyCreated(string name)
        {
            var field = host.GetField(name, statics)!.GetValue(null)!;
            return (bool)field.GetType().GetProperty("IsValueCreated")!.GetValue(field)!;
        }
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ => { scans++; return Task.FromResult<IReadOnlyList<AndroidDevice>>([]); });
        cache.GetProperty("Discovery", statics)!.SetValue(null, discovery);
        var one = new AndroidDevice("fixture-one-wifi", "Fixture", "offline", true, "fixture-one");
        var two = new AndroidDevice("fixture-two-wifi", "Fixture", "device", true, "fixture-two");
        var app = new AndroidApp("cn.test.music", "Music", 1001, "media");
        var store = (AndroidPhoneStore)host.GetProperty("Phone", statics)!.GetValue(null)!;
        store.RememberDevice(one); store.Rename("First"); store.Retain(app); string first = store.Profile.Id;
        store.RememberDevice(two); store.Rename("Second"); store.Retain(app); store.SelectProfile(first);
        discovery.Publish([one, two]);
        var main = new MainWindow();
        var phone = new PhoneAudioWindow(false) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            Pump((Task)Call(main, "InitializePhoneManagementAsync")!); Call(main, "SelectPhoneManagement");
            var page = (PhoneDevicePage)((Border)main.FindName("PhoneDetailsHost")).Child;
            var remove = (Button)page.FindName("RemoveDeviceButton");
            Check(remove.IsEnabled && ((Button)page.FindName("UnpairButton")).Content?.ToString() == L10n.T("Phone.Unpair")
                && remove.Content?.ToString() == L10n.T("Phone.DeleteDevice") && !((Button)page.FindName("DisconnectButton")).IsEnabled,
                "offline phone exposes distinct local removal and remote unpair actions");
            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render(page, 400, 650, Path.Combine(output, "device-actions-" + theme + ".png")); DrainUi(); page.UpdateLayout();
                var card = (Border)page.FindName("DeviceCard");
                var commands = (StackPanel)page.FindName("DeviceCommands");
                var bounds = remove.TransformToAncestor(card).TransformBounds(new Rect(remove.RenderSize));
                Check(bounds.Right <= card.ActualWidth + .5 && commands.ActualHeight <= remove.ActualHeight + .5
                    && ((TextBlock)page.FindName("DeviceName")).ActualWidth > 30,
                    theme + " narrow device card keeps commands in one compact row without losing the name");
            }
            discovery.Publish([one with { State = "device" }, two]);
            CheckCatalogBatchSelection(page, store, app, output);
            discovery.Publish([one, two]);
            phone.Show(); Pump((Task)Call(phone, "InitializeDevicesAsync")!);
            CheckSharedDragPreview(phone);
            string[] pinnedPackages = store.Profile.RetainedApps.Select(a => a.Package).ToArray();
            remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(WaitUntilAsync(() => !(bool)typeof(PhoneDevicePage).GetField("_busy", Hidden)!.GetValue(page)!, TimeSpan.FromSeconds(5)));
            DrainUi();
            var combo = (ComboBox)phone.FindName("DeviceCombo");
            var sidebar = (StackPanel)main.FindName("PhoneDevicesHost");
            Check(store.Profiles.Count == 1 && store.Profile.Name == "Second" && combo.Items.OfType<AndroidDevice>().Count() == 1
                && combo.SelectedItem is AndroidDevice selected && selected.HardwareId == two.HardwareId
                && ((ListBox)main.FindName("PhoneDevicesList")).Items.Count == 1,
                "offline removal immediately updates the sidebar and quick phone selection");
            Check(!LazyCreated("LazyAdb") && !LazyCreated("LazySources") && !LazyCreated("LazyDisplays") && scans == 0,
                "unused phone removal starts no tools, audio, video or discovery");
            discovery.Publish([one with { State = "device" }, two]); DrainUi();
            Check(combo.Items.OfType<AndroidDevice>().Count() == 1 && store.Profiles.Count == 1 && store.IsRemoved(one),
                "a late device snapshot cannot restore a locally removed phone in either UI");

            store.RememberDevice(one with { State = "device" }); DrainUi();
            Check(store.Profile.Id == first && store.Profile.Name == "First" && store.Profile.RetainedApps.Select(a => a.Package).SequenceEqual(pinnedPackages),
                "explicitly re-adding the removed device restores its original settings and application list");
            var sources = (AndroidAudioManager)host.GetProperty("Sources", statics)!.GetValue(null)!;
            var owned = AddFixture(sources, one, app); var other = AddFixture(sources, two, app);
            var extra = AddFixture(sources, one, app with { Package = "cn.test.keepplaying" });
            var payload = new AndroidAppDrag(one, app);
            var data = new DataObject(AndroidAppDrag.Format, payload);
            var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, [data, DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move, phone, new Point(20, 200)], null)!;
            var dragging = host.GetProperty("IsDragging", statics)!;
            var returnOverlay = (Border)phone.FindName("PcDropHighlight");
            var returnCell = (Border)phone.FindName("AndroidReturnDropZone");
            try
            {
                dragging.SetValue(null, true);
                foreach (string theme in new[] { "light", "dark" })
                {
                    ThemeService.Apply(theme, "#00CBE9");
                    Render((FrameworkElement)phone.Content, 235, 450, Path.Combine(output, "phone-return-" + theme + ".png"));
                    DrainUi();
                    Check(returnOverlay.Visibility == Visibility.Visible && returnCell.Visibility == Visibility.Visible
                        && ((ListBox)phone.FindName("PhoneApps")).Visibility == Visibility.Hidden
                        && Descendants<TextBlock>(returnCell).Any(t => t.Text == L10n.T("Phone.ReturnToPhoneTitle")
                            && ReferenceEquals(t.Foreground, phone.FindResource("Theme.TextPrimary"))),
                        theme + " phone drag immediately shows the themed return card before entering the panel");
                }
                drop.RoutedEvent = System.Windows.DragDrop.DragLeaveEvent;
                Call(phone, "PhoneApp_DragLeave", phone, drop);
                Check(returnOverlay.Visibility == Visibility.Visible && returnCell.Visibility == Visibility.Visible,
                    "leaving the phone panel retains the return card for the active gesture without list flashing");
            }
            finally { dragging.SetValue(null, false); }
            DrainUi();
            Check(returnOverlay.Visibility == Visibility.Collapsed && ((ListBox)phone.FindName("PhoneApps")).Visibility == Visibility.Visible,
                "ending or cancelling the phone drag restores the existing app list");
            drop.RoutedEvent = System.Windows.DragDrop.PreviewDragOverEvent;
            Call(phone, "PhoneApp_DragOver", phone, drop);
            Check(drop.Effects == DragDropEffects.Move && drop.Handled, "phone panel accepts a phone app as a return-to-phone move");
            drop.RoutedEvent = System.Windows.DragDrop.PreviewDropEvent;
            Call(phone, "PhoneApp_Drop", phone, drop);
            Pump(WaitUntilAsync(() => !sources.Sources.Contains(owned), TimeSpan.FromSeconds(5)));
            Check(sources.Sources.Count == 2 && sources.Sources.Contains(extra) && sources.Sources.Contains(other),
                "dropping back removes only the matching phone app and leaves both unrelated streams");
            CheckPcToPhoneOutputsAndDropGrid(main, phone, store, one with { State = "device" }, output);
            Pump(sources.RemoveAsync(extra.Id)); owned = AddFixture(sources, one, app);
            remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(WaitUntilAsync(() => !(bool)typeof(PhoneDevicePage).GetField("_busy", Hidden)!.GetValue(page)!, TimeSpan.FromSeconds(5)));
            Check(sources.Sources.Count == 1 && ReferenceEquals(sources.Sources[0], other)
                && owned.Session.State.State == CaptureState.Idle && other.Session.State.State == CaptureState.Receiving,
                "local removal stops only the target phone's audio while another phone keeps receiving");
            bool rejected = false;
            try { Pump((Task)host.GetMethod("AddAudioAsync", statics)!.Invoke(null, [one, app, false, true])!); }
            catch (IOException) { rejected = true; }
            Check(rejected && sources.Sources.Count == 1, "pending display audio cannot resurrect a removed phone");
            rejected = false;
            try { host.GetMethod("OpenDisplayAsync", statics)!.Invoke(null, [one, app, 0, 0]); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { rejected = true; }
            Check(rejected && !LazyCreated("LazyDisplays"), "stale drag payloads cannot reopen removed phone displays");
            remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(WaitUntilAsync(() => !(bool)typeof(PhoneDevicePage).GetField("_busy", Hidden)!.GetValue(page)!, TimeSpan.FromSeconds(5)));
            DrainUi();
            Check(store.Profile.LastDevice == null && combo.Items.Count == 1 && !combo.Items.OfType<AndroidDevice>().Any() && sources.Sources.Count == 0
                && sidebar.Children.OfType<Button>().Count() == 1
                && ((FrameworkElement)page.FindName("ConnectionPanel")).Visibility == Visibility.Visible,
                "last removal leaves Add Device and clears quick phone apps and playback");
            var overUsb = two with { Serial = "fixture-two", IsWifi = false };
            discovery.Publish([overUsb]); Call(page, "ApplyDevices", (object)new[] { overUsb }); DrainUi();
            var usbChoices = (ItemsControl)page.FindName("RestoreUsbDevices");
            Check(usbChoices.Visibility == Visibility.Visible && usbChoices.Items.Count == 1 && store.Profile.LastDevice == null,
                "a connected removed USB device offers explicit restoration without re-adding on discovery");
            Pump(store.FlushAsync());
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            { Passed = true, Assertions = _checks, RealDeviceAccess = false, Scans = scans,
                Scope = "Offline UI removal, stale discovery, isolated source cleanup, lazy startup, themes, phone output device pool and PC-to-phone dynamic drop grid" }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {_checks} device action UI assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { phone.Close(); main.Close(); }
    }

    private static Point PhoneDropCellCenter(PhoneAudioWindow phone, string endpoint)
    {
        phone.UpdateLayout();
        var grid = (Grid)phone.FindName("PcDropZonesGrid");
        var cell = grid.Children.OfType<Border>().Single(b => Equals(b.Tag, endpoint));
        if (cell.ActualWidth <= 0 || cell.ActualHeight <= 0) throw new InvalidOperationException("Drop cell has no layout for " + endpoint);
        return cell.TranslatePoint(new Point(cell.ActualWidth / 2, cell.ActualHeight / 2), (Border)phone.FindName("PcDropHighlight"));
    }

    private static void CheckPcToPhoneOutputsAndDropGrid(MainWindow main, PhoneAudioWindow phone, AndroidPhoneStore store, AndroidDevice device, string output)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var controller = typeof(App).Assembly.GetType("SonicRoute.AppIconDragController")!;
        var pcToPhone = (PcToPhoneAudioManager)host.GetProperty("PcToPhone", statics)!.GetValue(null)!;
        pcToPhone.EnableLiveStreaming = false;

        var combo = (ComboBox)phone.FindName("DeviceCombo");
        combo.SelectedItem = combo.Items.OfType<AndroidDevice>().First(d => AndroidDeviceIdentity.Matches(d, device));
        var overlay = (Border)phone.FindName("PcDropHighlight");
        var grid = (Grid)phone.FindName("PcDropZonesGrid");
        string phoneKey = AndroidDeviceIdentity.Key(device);
        string speakerTitle = string.Format(L10n.T("Phone.PcDropOnlyOnSpeakerTitle"),
            string.Format(L10n.T("Phone.PcDropSpeakerTitle"), store.GetDisplayName(device)));
        string spkId = AndroidOutputIdentity.FormatDeviceId(phoneKey, "speaker");
        string currentId = AndroidOutputIdentity.FormatDeviceId(phoneKey, "current");
        string btKey = "bt:8:AA:BB:CC:DD:EE:FF";
        string btId = AndroidOutputIdentity.FormatDeviceId(phoneKey, btKey);

        try
        {
            store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]);
            controller.GetMethod("SetDesktopDraggingForTest", statics)!.Invoke(null, [true]);
            Render((FrameworkElement)phone.Content, 235, 450, Path.Combine(output, "phone-pc-drop-current-speaker.png"));
            DrainUi();
            Check(overlay.Visibility == Visibility.Visible && grid.RowDefinitions.Count == 2
                && phone.FindName("PcCurrentOutputDropZone") is Border && phone.FindName("PcSpeakerDropZone") is Border
                && phone.FindName("PcBluetoothDropZone_1") == null
                && Descendants<TextBlock>(grid).Any(t => t.Text == speakerTitle),
                "online phone shows current and speaker drop targets without Bluetooth");

            var btScoDup = new AndroidOutputSink("bt:Xiaomi_Buds_4_Pro", AndroidOutputSinkKind.Bluetooth, 16, 7, "Xiaomi Buds 4 Pro", "00:00:00:00:00:00");
            var btSink = new AndroidOutputSink(btKey, AndroidOutputSinkKind.Bluetooth, 15, 8, "Xiaomi Buds 4 Pro", "AA:BB:CC:DD:EE:FF");
            store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker, btScoDup, btSink]);
            Render((FrameworkElement)phone.Content, 235, 450, Path.Combine(output, "phone-pc-drop-with-bluetooth.png"));
            DrainUi();
            Check(overlay.Visibility == Visibility.Visible && grid.RowDefinitions.Count == 3
                && phone.FindName("PcSpeakerDropZone") is Border && phone.FindName("PcBluetoothDropZone_1") is Border
                && phone.FindName("PcBluetoothDropZone_2") == null
                && Descendants<TextBlock>(grid).Any(t => t.Text == speakerTitle)
                && Descendants<TextBlock>(grid).Any(t => t.Text == "仅在 Xiaomi Buds 4 Pro 播放"),
                "connecting a Bluetooth device deduplicates SCO + A2DP and adds one target below current and speaker");

            typeof(MainWindow).GetMethod("EnsureSettingsPage", Hidden)!.Invoke(main, null);
            typeof(MainWindow).GetMethod("SyncExternalOutputDevices", Hidden)!.Invoke(main, null);
            typeof(MainWindow).GetMethod("BuildDeviceFilter", Hidden)!.Invoke(main, null);
            var outputs = (IList<SonicRoute.Core.Models.AudioDeviceInfo>)typeof(MainWindow).GetField("_outputs", Hidden)!.GetValue(main)!;
            var filterList = (ItemsControl)main.FindName("OutputFilterList");
            var spkCheck = filterList.Items.OfType<CheckBox>().FirstOrDefault(c => c.Tag is SonicRoute.Core.Models.AudioDeviceInfo d && d.Id == spkId);
            var btCheck = filterList.Items.OfType<CheckBox>().FirstOrDefault(c => c.Tag is SonicRoute.Core.Models.AudioDeviceInfo d && d.Id == btId);
            Check(outputs.Any(d => d.Id == spkId) && outputs.Any(d => d.Id == btId)
                && spkCheck?.IsChecked == true && btCheck?.IsChecked == true
                && !ConfigService.Load().HiddenOutputDevices.Contains(spkId)
                && !ConfigService.Load().HiddenOutputDevices.Contains(btId),
                "detected phone speaker and Bluetooth outputs join the device pool and are automatically checked in retained devices");

            btCheck!.IsChecked = false;
            Check(ConfigService.Load().HiddenOutputDevices.Contains(btId),
                "unchecking a phone output in retained devices hides it using the same filter list as PC devices");
            btCheck.IsChecked = true;
            Check(!ConfigService.Load().HiddenOutputDevices.Contains(btId),
                "re-checking a phone output restores it to retained devices");

            var pcApp = new SonicRoute.Core.Models.AudioAppInfo { ProcessId = 42424, ProcessName = "cloudmusic", DisplayName = "网易云音乐" };
            var pcData = new DataObject("SonicRoute.PcApp.v1", pcApp);
            var targets = grid.Children.OfType<Border>().Where(cell => cell.Tag is string).OrderBy(Grid.GetRow).ToArray();
            Check(targets.Select(cell => (string)cell.Tag).SequenceEqual([currentId, spkId, btId]),
                "online phone orders the system media route, fixed speaker and Bluetooth as distinct IDs");
            var dragOver = typeof(PhoneAudioWindow).GetMethod("PhoneApp_DragOver", Hidden)!;
            foreach (var cell in targets)
            {
                var point = PhoneDropCellCenter(phone, (string)cell.Tag);
                var over = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), Hidden, null,
                    [pcData, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Move, overlay, point], null)!;
                over.RoutedEvent = System.Windows.DragDrop.PreviewDragOverEvent;
                dragOver.Invoke(phone, [phone, over]);
                Check(over.Handled && over.Effects == DragDropEffects.Copy
                    && targets.Where(t => ReferenceEquals(t.Background, phone.FindResource("Theme.Overlay"))).SequenceEqual([cell]),
                    "a PC drag over " + cell.Name + " highlights only its actual endpoint");
            }
            // The routed PC input is exercised with a real process in the native audio check.
            // A stopped transport suffices here to verify that device loss retires an existing route.
            Check(pcToPhone.RouteProcess(42424, spkId, device).Success && pcToPhone.GetPersistedEndpoint(42424) == spkId,
                "an isolated route is present before the phone disconnects");

            store.UpdateOutputs(device, [AndroidOutputSink.DefaultSpeaker]);
            DrainUi();
            typeof(MainWindow).GetMethod("SyncExternalOutputDevices", Hidden)!.Invoke(main, null);
            typeof(MainWindow).GetMethod("BuildDeviceFilter", Hidden)!.Invoke(main, null);
            outputs = (IList<SonicRoute.Core.Models.AudioDeviceInfo>)typeof(MainWindow).GetField("_outputs", Hidden)!.GetValue(main)!;
            Check(outputs.Any(d => d.Id == spkId) && !outputs.Any(d => d.Id == btId)
                && !filterList.Items.OfType<CheckBox>().Any(c => c.Tag is SonicRoute.Core.Models.AudioDeviceInfo d && d.Id == btId)
                && grid.RowDefinitions.Count == 2 && phone.FindName("PcBluetoothDropZone_1") == null,
                "disconnecting Bluetooth leaves current and speaker drop targets");

            var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
            var currentDevices = ((IReadOnlyList<AndroidDevice>)cache.GetProperty("Devices", statics)!.GetValue(null)!).ToArray();
            var discovery = (AndroidDeviceDiscovery)cache.GetProperty("Discovery", statics)!.GetValue(null)!;
            discovery.Publish(currentDevices.Where(d => !AndroidDeviceIdentity.Matches(d, device)).ToArray());
            DrainUi();
            typeof(MainWindow).GetMethod("SyncExternalOutputDevices", Hidden)!.Invoke(main, null);
            typeof(MainWindow).GetMethod("BuildDeviceFilter", Hidden)!.Invoke(main, null);
            outputs = (IList<SonicRoute.Core.Models.AudioDeviceInfo>)typeof(MainWindow).GetField("_outputs", Hidden)!.GetValue(main)!;
            Check(!outputs.Any(d => d.Id == spkId || d.Id == currentId || d.Id == btId)
                && !filterList.Items.OfType<CheckBox>().Any(c => c.Tag is SonicRoute.Core.Models.AudioDeviceInfo d && d.Id == spkId)
                && pcToPhone.GetPersistedEndpoint(42424) == null,
                "disconnecting the phone automatically removes its output devices from the device pool and stops active PC-to-phone routes");
            discovery.Publish(currentDevices);
            DrainUi();
        }
        finally
        {
            pcToPhone.StopAll(restorePreviousEndpoint: false);
            controller.GetMethod("SetDesktopDraggingForTest", statics)!.Invoke(null, [false]);
            DrainUi();
            Check(overlay.Visibility == Visibility.Collapsed, "ending a PC app drag hides the phone drop overlay");
        }
    }

    private static void CheckSharedDragPreview(PhoneAudioWindow phone)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var controller = typeof(App).Assembly.GetType("SonicRoute.AppIconDragController")!;
        var type = controller.GetNestedType("IconPreview", BindingFlags.NonPublic)!;
        var icon = new System.Windows.Media.DrawingImage(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.Blue,
            null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 28, 28))));
        var preview = (Window)Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance, null, [icon, "Fixture"], null)!;
        try
        {
            preview.Show(); type.GetMethod("Move", Hidden)!.Invoke(preview, [-10000, -10000]);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(preview).Handle;
            long style = (long)((IntPtr)controller.GetMethod("GetWindowLongPtr", statics)!.Invoke(null, [hwnd, -20])!).ToInt64();
            Check(!preview.ShowActivated && !preview.IsHitTestVisible && (style & 0x08000020) == 0x08000020
                && ((Image)((Border)preview.Content).Child).Source == icon,
                "shared icon preview reuses the icon and cannot intercept focus or drop targets");
        }
        finally { preview.Close(); }
    }

    private static void CheckCatalogBatchSelection(PhoneDevicePage page, AndroidPhoneStore store, AndroidApp app, string output)
    {
        object? Call(string method, params object?[] args) => typeof(PhoneDevicePage).GetMethod(method, Hidden)!.Invoke(page, args);
        var choices = Enumerable.Range(0, 5).Select(i => new PhoneAppChoice(new PhoneAppItem(i == 0 ? app
            : app with { Package = "cn.test.choice" + i, Name = "Choice " + i, Uid = 1001 + i }), i == 0)).ToArray();
        typeof(PhoneDevicePage).GetField("_catalog", Hidden)!.SetValue(page, choices.Select(c => c.Item).ToArray());
        typeof(PhoneDevicePage).GetField("_catalogChoices", Hidden)!.SetValue(page, choices);
        Call("SetCatalogVisible", true);
        Call("ApplyCatalogFilter");
        var list = (ListBox)page.FindName("CatalogApps"); var rows = list.ItemsSource;
        var menu = (MenuItem)page.FindName("AddSelectedAppsMenu");
        list.SelectedItems.Add(choices[0]); list.SelectedItems.Add(choices[1]); list.SelectedItems.Add(choices[3]);
        Render(page, 400, 650, Path.Combine(output, "catalog-multi-select.png")); DrainUi(); page.UpdateLayout();
        var selectedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(choices[1]);
        var right = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
            System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = selectedRow };
        Call("CatalogApps_RightButtonDown", list, right);
        Check(list.SelectionMode == SelectionMode.Extended && list.SelectedItems.Count == 3,
            "extended selection and right-click preserve the existing selected group");
        Call("CatalogMenu_Opening", list, null);
        Check(menu.IsEnabled && menu.Header.ToString() == "添加所选应用（2）" && list.ContextMenu.Style == page.FindResource("ConvenienceMenu"),
            "shared themed context menu counts only selected apps that need adding");
        int changes = 0; void Changed() => changes++; store.Changed += Changed;
        try { menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        finally { store.Changed -= Changed; }
        Check(changes == 1 && store.Profile.RetainedApps.Select(a => a.Package).ToHashSet()
            .SetEquals(new[] { app.Package, choices[1].Item.Package, choices[3].Item.Package })
            && ReferenceEquals(rows, list.ItemsSource) && list.SelectedItems.Count == 3,
            "right-click batch addition updates once and preserves rows, scroll and selection");
        Call("CatalogMenu_Opening", list, null);
        Check(!menu.IsEnabled, "already-added selections cannot accidentally remove apps through the add menu");
        var unselectedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(choices[2]);
        right = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
            System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = unselectedRow };
        Call("CatalogApps_RightButtonDown", list, right);
        Check(list.SelectedItems.Count == 1 && ReferenceEquals(list.SelectedItem, choices[2]),
            "right-click on an unselected app targets that app instead of the earlier group");
        list.SelectAll();
        Check(list.SelectedItems.Count == choices.Length, "native list Select All includes every visible catalogue row");
        ((TextBox)page.FindName("CatalogSearch")).Text = "choice4";
        Check(list.Items.Count == 1 && list.SelectedItems.Cast<PhoneAppChoice>().All(c => c.Item.Package == choices[4].Item.Package),
            "filter changes leave no hidden rows selected for a later batch");
        Call("CloseCatalog_Click", page, new RoutedEventArgs());
        Check(list.Items.Count == 0 && !list.ContextMenu.IsOpen
            && ((FrameworkElement)page.FindName("DeviceScroll")).Visibility == Visibility.Visible,
            "closing the catalogue restores the device page and releases selections and its context menu");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stop = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= stop) throw new TimeoutException("Focused check condition timed out.");
            await Task.Delay(100); // Test-only deadline; no product polling.
        }
    }
}
