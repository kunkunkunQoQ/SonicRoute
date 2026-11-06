using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SonicRoute;
using SonicRoute.AndroidAudio;
using SonicRoute.Core.Models;

internal static partial class Program
{
    private static void CheckPhoneCatalogPage(Catalog catalog, AndroidPhoneStore store, string output)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var host = typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        var discoveryProperty = cache.GetProperty("Discovery", statics)!;
        var previousDiscovery = discoveryProperty.GetValue(null);
        int scans = 0;
        var discovery = new AndroidDeviceDiscovery(_ =>
        { scans++; return Task.FromResult<IReadOnlyList<AndroidDevice>>(new[] { catalog.Device }); });
        discoveryProperty.SetValue(null, discovery); discovery.Publish(new[] { catalog.Device });
        store.RememberDevice(catalog.Device);
        foreach (var retained in store.Profile.RetainedApps.ToArray()) store.Remove(retained.Package);
        var app = catalog.Apps.First(a => a.Package == "cn.wenyu.bodian"); store.Retain(app);
        var main = new MainWindow();
        object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);
        void Set(PhoneDevicePage page, string field, object value) => typeof(PhoneDevicePage).GetField(field, Hidden)!.SetValue(page, value);
        try
        {
            Call(main, "InitializePhoneManagement", false); Call(main, "SelectPhoneManagement");
            var page = (PhoneDevicePage)((Border)main.FindName("PhoneDetailsHost")).Child;
            var deviceScroll = (ScrollViewer)page.FindName("DeviceScroll");
            var list = (ListBox)page.FindName("CatalogApps");
            var pcs = (ListBox)main.FindName("AppsListBox");
            var pc = new AppItem { Info = new AudioAppInfo { ProcessId = 0, ProcessName = "Desktop app", HasActiveSession = true } };
            pcs.ItemsSource = new[] { pc };
            Render((FrameworkElement)main.Content, 1040, 720, Path.Combine(output, "shared-sidebar.png"));
            var pcContainer = (ListBoxItem)pcs.ItemContainerGenerator.ContainerFromItem(pc);
            var dot = Descendants<System.Windows.Shapes.Ellipse>(pcContainer).Single(e => e.Name == "StateDot");
            Check(dot.Visibility == Visibility.Visible && pc.IsActive, "shared row template retains PC active-session status");
            pc.IsMuted = true; DrainUi();
            Check(dot.Fill == main.FindResource("Theme.AccentInverse"), "shared PC status still updates when muted");
            var choices = Enumerable.Range(0, 300).Select(i => new PhoneAppChoice(new PhoneAppItem(i == 0 ? app
                : app with { Package = "cn.test.page" + i, Name = "应用 " + i, Uid = 2000 + i }), i == 0)).ToArray();
            Set(page, "_catalog", choices.Select(c => c.Item).ToArray()); Set(page, "_catalogChoices", choices);
            Set(page, "_catalogSerial", catalog.Device.Serial);
            ((Button)page.FindName("AddAppsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            foreach (string theme in new[] { "light", "dark" })
            {
                ThemeService.Apply(theme, "#00CBE9");
                Render((FrameworkElement)main.Content, 1040, 720, Path.Combine(output, "catalog-page-" + theme + ".png"));
                Check(deviceScroll.Visibility == Visibility.Collapsed
                    && ((FrameworkElement)page.FindName("CatalogPanel")).Visibility == Visibility.Visible
                    && list.ActualHeight > 250 && double.IsNaN(list.Height),
                    theme + " add-app page replaces device content and gives remaining height to the catalog");
                Check(VirtualizingPanel.GetIsVirtualizing(list) && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling
                    && Descendants<ListBoxItem>(list).Count() < 40 && list.Items.Count == 300,
                    theme + " catalog keeps only visible row containers for 300 cached choices");
            }
            Check(scans == 0 && !(bool)host.GetProperty("IsAdbCreated", statics)!.GetValue(null)!,
                "opening cached catalog performs no device scan or ADB initialization");
            ((Button)page.FindName("CloseCatalogButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(deviceScroll.Visibility == Visibility.Visible && list.ItemsSource == null
                && !((IEnumerable)typeof(PhoneDevicePage).GetField("_catalogChoices", Hidden)!.GetValue(page)!).Cast<object>().Any(),
                "Done restores the device page and releases catalog choice models");
            CheckCatalogBatchSelection(page, store, app, output);

            var add = ((StackPanel)main.FindName("PhoneDevicesHost")).Children.OfType<Button>().Single();
            add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            pcs.RaiseEvent(new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
            Check(((FrameworkElement)main.FindName("PhoneDetailsHost")).Visibility == Visibility.Visible
                && ((FrameworkElement)page.FindName("ConnectionPanel")).Visibility == Visibility.Visible,
                "clearing or rebuilding the PC list does not navigate away from Add Device");
            ((Button)main.FindName("AppsRefreshButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Until(() => scans == 1 && !(bool)typeof(PhoneDevicePage).GetField("_busy", Hidden)!.GetValue(page)!);
            Check(((FrameworkElement)page.FindName("ConnectionPanel")).Visibility == Visibility.Visible,
                "bottom Refresh scans devices exactly once and preserves Add Device navigation");
            Render((FrameworkElement)main.Content, 1040, 720, Path.Combine(output, "direct-connect-dark.png"));
            Check(!(bool)host.GetProperty("IsAdbCreated", statics)!.GetValue(null)!,
                "mocked refresh and page navigation start no real ADB or phone session");
        }
        finally
        { main.Close(); discoveryProperty.SetValue(null, previousDiscovery); ThemeService.Apply("light", "#00CBE9"); }
    }
}
