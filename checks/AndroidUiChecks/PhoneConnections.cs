using System.IO;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;
using SonicRoute.AndroidAudio;

internal static partial class Program
{
    private static int QrPairingLive(string output)
    {
        const BindingFlags statics = BindingFlags.NonPublic | BindingFlags.Static;
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        cache.GetMethod("SetDevices", statics)!.Invoke(null, new object[]{Array.Empty<AndroidDevice>()});
        var page = new PhoneDevicePage();
        var window = new Window { Title="SonicRoute · 手机配对验证", Content=page, Width=850, Height=700,
            WindowStartupLocation=WindowStartupLocation.CenterScreen };
        window.SetResourceReference(Window.BackgroundProperty,"Theme.AppBg");
        var frame = new System.Windows.Threading.DispatcherFrame();
        var image = (Image)page.FindName("PairQrImage");
        var status = (TextBlock)page.FindName("OperationStatus");
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        bool started=false, ended=false;
        EventHandler changed = (_,_) =>
        {
            if (image.Source!=null) { started=true; return; }
            if (!started || ended) return; ended=true;
            var host=typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!;
            var store=(AndroidPhoneStore)host.GetProperty("Phone",statics)!.GetValue(null)!;
            File.WriteAllText(Path.Combine(output,"qr-live-result.json"), JsonSerializer.Serialize(new{
                Status=status.Text, Device=store.Profile.LastDevice,
                DeviceCardVisible=((FrameworkElement)page.FindName("DeviceCard")).Visibility==Visibility.Visible,
                RealAdb=true, AudioOrVirtualDisplayStarted=false},new JsonSerializerOptions{WriteIndented=true}));
            window.Close();
        };
        descriptor.AddValueChanged(image,changed);
        var limit=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(130) };
        limit.Tick+=(_,_)=>{limit.Stop();window.Close();};
        window.Closed+=(_,_)=>{limit.Stop();descriptor.RemoveValueChanged(image,changed);page.Dispose();frame.Continue=false;};
        window.Show(); typeof(PhoneDevicePage).GetMethod("Activate",Hidden)!.Invoke(page,null);
        typeof(PhoneDevicePage).GetMethod("ShowConnection",Hidden)!.Invoke(page,null);
        ((Button)page.FindName("QrButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        limit.Start(); Console.WriteLine("QR_WINDOW_READY; scan from Android wireless debugging. No credentials logged.");
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        return ended && status.Text=="已连接" ? 0 : 1;
    }
    private static void CheckPhoneConnectionEvents(Catalog catalog)
    {
        var cache = typeof(App).Assembly.GetType("SonicRoute.PhoneAudioWindowCache")!;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var discoveryProperty = cache.GetProperty("Discovery", statics)!;
        var previousDiscovery = discoveryProperty.GetValue(null);
        var previousSerial = cache.GetField("SelectedSerial", statics)!.GetValue(null);
        var initial = new[] { catalog.Device };
        IReadOnlyList<AndroidDevice> scanned = initial;
        var discovery = new AndroidDeviceDiscovery(_ => Task.FromResult(scanned));
        discovery.Publish(initial); discoveryProperty.SetValue(null, discovery);
        var qr = new AndroidQrPairing("studio-fixture", "ephemeral-fixture");
        var target = new AndroidDevice("adb-testphone-qr123._adb-tls-connect._tcp", "Test phone", "device", true);
        bool ready = false;
        var commands = new List<string[]>();
        var wireless = new AndroidWirelessConnection((token, args) =>
        {
            token.ThrowIfCancellationRequested(); commands.Add(args);
            return Task.FromResult(args[0] switch
            {
                "mdns" => $"{qr.ServiceName} _adb-tls-pairing._tcp 192.168.1.10:37000\n",
                "pair" => Paired(),
                "connect" => Connected(),
                "devices" => "List of devices attached\n" + (ready ? target.Serial + " device model:Test_phone\n" : ""),
                "-s" => target.Serial,
                _ => throw new Exception("unexpected fixture command")
            });
            string Paired() { ready = true; return "Successfully paired to 192.168.1.10:37000 [guid=adb-testphone]"; }
            string Connected() { ready = true; return "connected to 192.168.1.10:38000"; }
        }, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var page = (PhoneDevicePage)Activator.CreateInstance(typeof(PhoneDevicePage), Hidden, null,
            new object[] { wireless, (Func<AndroidQrPairing>)(() => qr) }, null)!;
        var main = new MainWindow(); var phone = new PhoneAudioWindow(false);
        try
        {
            typeof(MainWindow).GetMethod("InitializePhoneManagement", Hidden)!.Invoke(main, new object[] { false });
            typeof(MainWindow).GetField("_phoneDevicePage", Hidden)!.SetValue(main, page);
            typeof(MainWindow).GetMethod("SelectPhoneManagement", Hidden)!.Invoke(main, null);
            var sidebar = (StackPanel)main.FindName("PhoneDevicesHost");
            var deviceButton = sidebar.Children.OfType<Button>().First();
            var addButton = sidebar.Children.OfType<Button>().Last();
            void Click(string name) => ((Button)page.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void ShowConnection() => addButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Empty() { ready = false; discovery.Publish(Array.Empty<AndroidDevice>()); }
            void AssertConnected(string label)
            {
                Until(() => !(bool)typeof(PhoneDevicePage).GetField("_busy", Hidden)!.GetValue(page)!);
                Pump((Task)typeof(PhoneAudioWindow).GetMethod("InitializeDevicesAsync", Hidden)!.Invoke(phone, null)!);
                Check(deviceButton.Visibility == Visibility.Visible
                    && ((ComboBox)phone.FindName("DeviceCombo")).SelectedItem is AndroidDevice selected && selected.Serial == target.Serial
                    && ((FrameworkElement)page.FindName("DeviceCard")).Visibility == Visibility.Visible,
                    label + " publishes the authorized phone to the sidebar, device page and reopened quick panel");
            }
            ShowConnection(); Empty(); Click("QrButton"); AssertConnected("QR pairing");
            Check(((FrameworkElement)page.FindName("PairQrImage")).Visibility == Visibility.Collapsed,
                "completed QR pairing clears the transient code image");
            ShowConnection(); Empty();
            ((TextBox)page.FindName("PairAddressBox")).Text = "192.168.1.10:37000";
            ((TextBox)page.FindName("PairCodeBox")).Text = "123456";
            target = target with { Serial = "adb-testphone-code123._adb-tls-connect._tcp" };
            Click("PairButton"); AssertConnected("Six-digit pairing");
            ShowConnection(); Empty();
            ((TextBox)page.FindName("ConnectAddressBox")).Text = "192.168.1.10:38000";
            target = target with { Serial = "adb-testphone-direct123._adb-tls-connect._tcp" };
            Click("ConnectButton"); AssertConnected("Manual address connection");
            target = target with { Serial = "adb-testphone-new123._adb-tls-connect._tcp" };
            ready = true; discovery.Publish(Array.Empty<AndroidDevice>());
            Click("ReconnectButton"); AssertConnected("Reconnect with a changed service suffix");

            ShowConnection();
            Check(page.FindName("BackToDeviceButton") == null && ((FrameworkElement)page.FindName("ConnectionPanel")).Visibility == Visibility.Visible,
                "Add Device is an independent page without a parent-device back button");
            discovery.Publish(new[] { target });
            Check(((FrameworkElement)page.FindName("ConnectionPanel")).Visibility == Visibility.Visible,
                "device notifications do not navigate away from the Add Device page");
            deviceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((FrameworkElement)page.FindName("DeviceCard")).Visibility == Visibility.Visible,
                "the sibling phone sidebar entry explicitly returns to its own management page");

            ShowConnection(); Empty();
            var usb = new AndroidDevice("usb-fixture", "USB phone", "device", false);
            var phoneStore = (AndroidPhoneStore)typeof(App).Assembly.GetType("SonicRoute.AndroidAudioHost")!
                .GetProperty("Phone", statics)!.GetValue(null)!;
            string selectedProfile = phoneStore.Profile.Id;
            scanned = new[] { usb }; Click("RefreshDevicesButton");
            Until(() => !(bool)typeof(PhoneDevicePage).GetField("_busy", Hidden)!.GetValue(page)!);
            Pump((Task)typeof(PhoneAudioWindow).GetMethod("InitializeDevicesAsync", Hidden)!.Invoke(phone, null)!);
            var combo = (ComboBox)phone.FindName("DeviceCombo");
            Check(phoneStore.Profile.Id == selectedProfile && combo.Items.Cast<AndroidDevice>().Any(d => d.Serial == usb.Serial)
                && phoneStore.FindProfile(usb) != null,
                "USB refresh adds a separate phone without switching or overwriting the selected phone");
            combo.SelectedItem = combo.Items.Cast<AndroidDevice>().Single(d => d.Serial == usb.Serial);
            Check(combo.SelectedItem is AndroidDevice selectedUsb && !selectedUsb.IsWifi
                && phoneStore.Profile.LastDevice?.Serial == usb.Serial,
                "explicit USB selection activates its own profile without loading an application catalog");
            Check(commands.All(c => c[0] is "pair" or "connect" or "devices" or "mdns" or "-s"),
                "connection-page fixtures perform no application catalog, audio, video or media-control work");
        }
        finally
        {
            main.Close(); phone.Close(); page.Dispose();
            discoveryProperty.SetValue(null, previousDiscovery);
            cache.GetField("SelectedSerial", statics)!.SetValue(null, previousSerial);
        }
    }
}
