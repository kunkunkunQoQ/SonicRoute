using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SonicRoute;

internal static partial class Program
{
    private static void CheckWindowsDesktopPage(PhoneDevicePage page, object service, object peer, string output, CancellationToken token)
    {
        var button = (Button)page.FindName("WindowsDesktopOpen");
        var status = (TextBlock)page.FindName("WindowsDesktopStatus");
        var serviceType = service.GetType();
        var refresh = typeof(PhoneDevicePage).GetMethod("RefreshWindowsLinkPeerState", Hidden)!;
        string id = DeviceId(peer) ?? throw new InvalidOperationException("The paired device has no ID.");
        IEnumerable<object> Videos() => ((IEnumerable)serviceType.GetProperty("Streams")!.GetValue(service)!).Cast<object>()
            .Where(s => s.GetType().GetProperty("Kind")!.GetValue(s)?.ToString() == "Window");
        void Click() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void WaitForViewer()
        {
            Until(() => button.IsEnabled);
            if (!Videos().Any()) throw new Exception("Desktop viewer failed: " + status.Text);
        }

        Check(button.IsEnabled && button.IsVisible, "the paired device page offers Open desktop on a connected monitor-capable PC");
        var card = (Border)page.FindName("DeviceCard");
        Render(card, (int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight),
            Path.Combine(output, "open-desktop-connected.png"));
        var legacy = peer.GetType().GetMethod("<Clone>$")!.Invoke(peer, null)!;
        var capabilities = peer.GetType().GetProperty("Capabilities")!;
        capabilities.SetValue(legacy, Enum.ToObject(capabilities.PropertyType, 64));
        typeof(PhoneDevicePage).GetMethod("UpdateWindowsDesktop", Hidden)!.Invoke(page, [legacy]);
        Check(!button.IsEnabled && status.Text == L10n.T("Phone.WindowsDesktopUpgrade"), "older peers explain why desktop sharing needs an update");
        refresh.Invoke(page, null);
        var catalog = (Task)serviceType.GetMethod("RequestCatalogAsync")!.Invoke(service, [id, false, token, false, true, false])!;
        Pump(catalog);
        var entries = ((IEnumerable)catalog.GetType().GetProperty("Result")!.GetValue(catalog)!).Cast<object>();
        var expected = entries.Single(s => s.GetType().GetProperty("Display")!.GetValue(s) is { } display
            && (bool)display.GetType().GetProperty("IsPrimary")!.GetValue(display)!);
        Click();
        Check(!button.IsEnabled && status.Text == L10n.T("Phone.WindowsDesktopOpening"), "a pending desktop request disables the same button immediately");
        Click();
        WaitForViewer();
        var video = Videos().Single();
        Check(Equals(video.GetType().GetProperty("InstanceId", Hidden)!.GetValue(video),
            expected.GetType().GetProperty("InstanceId")!.GetValue(expected)), "two immediate clicks open one stream for the remote primary monitor");
        var streamId = video.GetType().GetProperty("Id")!.GetValue(video);
        Click();
        WaitForViewer();
        Check(Equals(Videos().Single().GetType().GetProperty("Id")!.GetValue(Videos().Single()), streamId),
            "opening the same desktop again reuses the active viewer");
        Pump((Task)serviceType.GetMethod("DisconnectAsync")!.Invoke(service, [id])!);
        refresh.Invoke(page, null);
        Click();
        Check(!button.IsEnabled && !Videos().Any() && status.Text == L10n.T("Phone.WindowsDesktopOffline"),
            "disconnect closes the desktop and the page explains how to open it again");
    }
}
