using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonicRoute;
using SonicRoute.Core;

internal static partial class Program
{
    private static void CheckTrayWheelPause()
    {
        // The launcher has already attached this process to a private desktop and config directory.
        var app = (App)Application.Current;
        var wheelField = typeof(App).GetField("_trayWheel", Instance)!;
        var menuField = typeof(App).GetField("_trayMenu", Instance)!;
        var buildPause = typeof(App).GetMethod("AddTrayWheelPause", Instance)!;
        var wheelType = wheelField.FieldType;
        var enabled = wheelType.GetProperty("IsEnabled", Instance)!;
        var handle = typeof(MouseHotkeyHook).GetField("_hook", Instance)!;
        using var wheel = (IDisposable)Activator.CreateInstance(wheelType, Instance, null, new object?[] { null }, null)!;
        using var shortcut = new MouseHotkeyHook();
        shortcut.Reload(new Dictionary<string, string> { ["Ctrl+Alt+WheelUp"] = "check" });
        var initialHandle = (IntPtr)handle.GetValue(shortcut)!;
        Check(initialHandle != IntPtr.Zero, "private desktop registers the wheel shortcut");
        var config = ConfigService.Load();
        bool original = config.TrayWheelPaused;
        bool TrayActive() => (bool)enabled.GetValue(wheel)!;
        void Click()
        {
            var menu = new ContextMenu();
            menuField.SetValue(app, menu);
            buildPause.Invoke(app, new object[] { menu });
            menu.IsOpen = true;
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(!menu.IsOpen, "product tray menu accepted the click");
        }
        bool Saved()
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ConfigService.ConfigPath));
            return json.RootElement.GetProperty("TrayWheelPaused").GetBoolean();
        }
        var previousWheel = wheelField.GetValue(app);
        var previousMenu = menuField.GetValue(app);
        try
        {
            wheelField.SetValue(app, wheel);
            config.TrayWheelPaused = true;
            Check(ConfigService.TrySave(config), "test profile saved paused state");
            wheelType.GetMethod("Start", Instance)!.Invoke(wheel, new object[] { shortcut });
            Check(!TrayActive(), "persisted pause leaves the tray wheel inactive");
            Click();
            Check(TrayActive() && (IntPtr)handle.GetValue(shortcut)! == initialHandle && !Saved(),
                "resume shares the existing shortcut handle and saves the setting");
            using (File.Open(ConfigService.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Click();
            Check(TrayActive() && !config.TrayWheelPaused && !Saved(), "a failed save restores the prior hook and setting");
            var pending = wheelType.GetField("_pendingDelta", Instance)!;
            pending.SetValue(wheel, 120);
            Click();
            Check(!TrayActive() && Saved() && (int)pending.GetValue(wheel)! == 0, "pause detaches the tray listener, clears queued input and persists");
            Check((IntPtr)handle.GetValue(shortcut)! == initialHandle, "pausing the tray wheel retains its shortcut handle");
            shortcut.Reload(Array.Empty<KeyValuePair<string, string>>());
            Check(!shortcut.IsInstalled, "turning off the last shortcut releases the native hook");
            Click();
            Check(TrayActive() && shortcut.IsInstalled, "resuming tray alone installs the shared hook");
            Click();
            Check(!TrayActive() && !shortcut.IsInstalled, "pausing tray alone uninstalls the shared hook");
        }
        finally
        {
            wheelField.SetValue(app, previousWheel);
            menuField.SetValue(app, previousMenu);
            config.TrayWheelPaused = original;
            ConfigService.TrySave(config);
        }
    }

    private static void CheckTraySubmenuPlacement(string directory)
    {
        var menus = typeof(App).Assembly.GetType("SonicRoute.ConvenienceMenus", true)!;
        var create = menus.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!;
        var item = menus.GetMethod("Item", BindingFlags.Static | BindingFlags.NonPublic)!;
        var named = typeof(App).GetMethod("TrayNamedHeader", BindingFlags.Static | BindingFlags.NonPublic)!;
        var theme = typeof(App).Assembly.GetType("SonicRoute.ThemeService")!.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(string) }, null)!;
        theme.Invoke(null, new object[] { "light", "blue" });
        CheckTrayWheelPause();
        MenuItem Item(string text) => (MenuItem)item.Invoke(null, new object[] { text })!;
        void Name(MenuItem entry, string text) => named.Invoke(null, new object[] { entry, text });
        var names = new[]
        {
            ("short", "Speakers"), ("latin20", new string('W', 20)), ("latin21", new string('W', 21)),
            ("cjk10", new string('音', 10)), ("cjk11", new string('音', 11)), ("ellipsis", new string('长', 80))
        };
        var work = SystemParameters.WorkArea;
        foreach (bool rightEdge in new[] { false, true })
        {
            string mode = rightEdge ? "dark" : "light";
            theme.Invoke(null, new object[] { mode, "blue" });
            foreach (var (tag, name) in names)
            {
                string label = mode + "-" + (rightEdge ? "right" : "left") + "-" + tag;
                var menu = (ContextMenu)create.Invoke(null, null)!;
                menu.MinWidth = 180;
                menu.Placement = PlacementMode.AbsolutePoint;
                menu.HorizontalOffset = rightEdge ? work.Right - 18 : work.Left + 18;
                menu.VerticalOffset = work.Top + 50;
                menu.Items.Add(Item("打开快速面板"));
                var output = Item("系统默认播放");
                output.Items.Add(Item("加载中…"));
                menu.Items.Add(output);
                menu.Items.Add(Item("退出"));
                try
                {
                    menu.IsOpen = true;
                    DrainUi();
                    output.IsSubmenuOpen = true;
                    DrainUi();
                    // Match the actual asynchronous device-read completion: rename the open
                    // parent first, then replace its placeholder without forcing a layout pass.
                    Name(output, "系统默认播放：" + name);
                    output.Items.Clear();
                    var device = Item(""); Name(device, name);
                    device.IsCheckable = true; device.IsChecked = true;
                    output.Items.Add(device);
                    var other = Item(""); Name(other, "另一台播放设备"); output.Items.Add(other);
                    DrainUi(); Finish(Task.Delay(35)); DrainUi();
                    var popup = (Popup)output.Template.FindName("PART_Popup", output);
                    var mainSource = (HwndSource)PresentationSource.FromVisual(menu)!;
                    var subSource = (HwndSource)PresentationSource.FromVisual(popup.Child)!;
                    Check(GetWindowRect(mainSource.Handle, out var main) && GetWindowRect(subSource.Handle, out _), label + " actual menu HWNDs exist");
                    GetWindowRect(subSource.Handle, out var sub);
                    var rowLeft = output.PointToScreen(new Point()).X;
                    var rowRight = output.PointToScreen(new Point(output.ActualWidth, 0)).X;
                    double seam = Math.Ceiling(2 * mainSource.CompositionTarget.TransformToDevice.M11);
                    Console.WriteLine($"PLACEMENT {label} main={main.Left},{main.Top},{main.Right},{main.Bottom} sub={sub.Left},{sub.Top},{sub.Right},{sub.Bottom} row={rowLeft},{rowRight} seam={seam}");
                    SaveTrayMenuImage(menu, (FrameworkElement)popup.Child, main, sub, Path.Combine(directory, label + ".png"));
                    // Popup anchors exclude the menu's decorative outer padding/border.
                    Check(sub.Left >= rowRight - seam || sub.Right <= rowLeft + seam, label + " long-name submenu does not cover its parent rows");
                    Check(rightEdge ? sub.Right <= rowLeft + seam : sub.Left >= rowRight - seam, label + " native placement chooses the available side");
                    Check(((TextBlock)device.Header).Text == name && device.IsChecked && output.IsSubmenuOpen,
                        label + " full device identity, selection and open state survive relayout");
                    // The same open popup must follow a second width change, including shrinkage.
                    Name(output, "系统默认播放");
                    DrainUi(); Finish(Task.Delay(20)); DrainUi();
                    GetWindowRect(mainSource.Handle, out main); GetWindowRect(subSource.Handle, out sub);
                    rowLeft = output.PointToScreen(new Point()).X;
                    rowRight = output.PointToScreen(new Point(output.ActualWidth, 0)).X;
                    Check(sub.Left >= rowRight - seam || sub.Right <= rowLeft + seam, label + " subsequent header resize stays adjacent");
                    output.IsSubmenuOpen = false;
                    Name(output, "系统默认播放：" + name);
                    DrainUi();
                    output.IsSubmenuOpen = true;
                    DrainUi(); Finish(Task.Delay(20)); DrainUi();
                    subSource = (HwndSource)PresentationSource.FromVisual(popup.Child)!;
                    GetWindowRect(subSource.Handle, out sub);
                    rowLeft = output.PointToScreen(new Point()).X;
                    rowRight = output.PointToScreen(new Point(output.ActualWidth, 0)).X;
                    Check(sub.Left >= rowRight - seam || sub.Right <= rowLeft + seam, label + " reopened submenu uses current parent bounds");
                }
                finally
                {
                    output.IsSubmenuOpen = false;
                    menu.IsOpen = false;
                    DrainUi();
                }
            }
        }
    }

    private static void SaveTrayMenuImage(ContextMenu menu, FrameworkElement submenu, OutputNativeRect main, OutputNativeRect sub, string path)
    {
        int left = Math.Min(main.Left, sub.Left), top = Math.Min(main.Top, sub.Top);
        int width = Math.Max(main.Right, sub.Right) - left, height = Math.Max(main.Bottom, sub.Bottom) - top;
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(new VisualBrush(menu), null, new Rect(main.Left - left, main.Top - top, main.Right - main.Left, main.Bottom - main.Top));
            draw.DrawRectangle(new VisualBrush(submenu), null, new Rect(sub.Left - left, sub.Top - top, sub.Right - sub.Left, sub.Bottom - sub.Top));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
