using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SonicRoute;

internal static partial class Program
{
    private static void CheckWindowsLinkTrustControls(PhoneDevicePage page, object service, object peer, string output)
    {
        var apply = typeof(PhoneDevicePage).GetMethod("ApplyWindowsTrust", Hidden)!;
        var stateType = apply.GetParameters()[1].ParameterType;
        const string exampleCode = "00112233";
        void Present(bool enabled, string? localCode, bool ready, bool submitted = false)
        {
            var state = Activator.CreateInstance(stateType, [enabled, true, localCode, ready, submitted, false, null]);
            apply.Invoke(page, [peer, state]);
            page.UpdateLayout();
        }

        var action = (Button)page.FindName("WindowsTrustAction");
        var status = (TextBlock)page.FindName("WindowsTrustStatus");
        var codes = (Grid)page.FindName("WindowsTrustCodes");
        var local = (TextBox)page.FindName("WindowsTrustLocal");
        var input = (TextBox)page.FindName("WindowsTrustPeerCode");
        var verify = (Button)page.FindName("WindowsTrustVerify");
        var fileCard = (Border)page.FindName("WindowsFileCard");
        // Previous drag checks ended with a rejected directory. Reconnect the same fixture before previewing its idle file area.
        var filePeer = typeof(PhoneDevicePage).GetField("_filePeer", Hidden)!.GetValue(page);
        var refreshPage = typeof(PhoneDevicePage).GetMethod("RefreshWindowsLinkDevicePage", Hidden)!;
        refreshPage.Invoke(page, null);
        typeof(PhoneDevicePage).GetMethod("UpdateWindowsFileCard", Hidden)!.Invoke(page, [filePeer]);
        var folder = (TextBlock)page.FindName("WindowsReceiveFolderPath");
        var folderButton = (Button)page.FindName("ChangeLocalReceiveFolder");
        Check(!((FrameworkElement)page.FindName("AndroidReceiveFolderRow")).IsVisible
            && !((FrameworkElement)page.FindName("AndroidBrowseFiles")).IsVisible,
            "the Windows device shows only its own transfer controls");
        Check(folderButton.IsVisible && folderButton.IsEnabled && folder.IsVisible && Path.IsPathFullyQualified(folder.Text),
            "device details expose the current receive folder and the change-folder button");

        Present(false, null, false);
        Check(action.IsEnabled && Equals(action.Content, L10n.T("Phone.LinkTrustGenerate"))
            && codes.Visibility == Visibility.Collapsed, "ordinary pairing shows the code generator");
        Present(false, exampleCode, false);
        Check(local.IsReadOnly && local.Text == exampleCode && codes.IsVisible && !input.IsEnabled && !verify.IsEnabled,
            "the local code is visible while the other computer has yet to generate its code");
        Present(false, exampleCode, true);
        input.Text = "1234";
        Check(input.IsEnabled && !verify.IsEnabled && status.Text == L10n.T("Phone.LinkTrustExchange"),
            "the peer-code box accepts input and awaits eight digits before verification");
        input.Text = "87654321"; // Synthetic examples only; no real peer exchange runs in this fixture.
        Check(verify.IsEnabled, "entering a complete peer code enables verification");
        Check(local.ActualWidth >= local.DesiredSize.Width - 1 && input.ActualWidth >= 60 && verify.ActualWidth > 0,
            $"the code fields and verification button have room at the device page width ({local.ActualWidth:F0}, {input.ActualWidth:F0}, {verify.ActualWidth:F0})");

        var permissionCard = (Border)page.FindName("WindowsLinkInputCard");
        var inputSwitch = (FrameworkElement)page.FindName("WindowsLinkAllowInputSwitch");
        var details = (StackPanel)permissionCard.Parent;
        var visible = details.Children.OfType<FrameworkElement>().Where(e => e.IsVisible).ToArray();
        double fileTop = fileCard.TranslatePoint(new Point(), details).Y;
        double cardTop = permissionCard.TranslatePoint(new Point(), details).Y;
        Check(fileCard.Parent == details && visible.Last() == permissionCard && cardTop >= fileTop + fileCard.ActualHeight - 1
            && action.TranslatePoint(new Point(), permissionCard).Y >= inputSwitch.TranslatePoint(new Point(0, inputSwitch.ActualHeight), permissionCard).Y - 1,
            "the last visible card follows file transfer and places complete trust below remote keyboard and mouse");

        static Border View(FrameworkElement visual) => new()
        {
            Width = visual.ActualWidth, Height = visual.ActualHeight, Background = new VisualBrush(visual)
        };
        void Snapshot(FrameworkElement visual, string name)
        {
            var surface = new Border { Padding = new Thickness(4), Child = visual };
            surface.SetResourceReference(Border.BackgroundProperty, "Theme.WindowBg");
            Render(surface, (int)Math.Ceiling(visual.Width) + 8, (int)Math.Ceiling(visual.Height) + 8, Path.Combine(output, name));
        }
        var lower = new Canvas { Width = details.ActualWidth, Height = cardTop + permissionCard.ActualHeight - fileTop };
        foreach (var row in visible.SkipWhile(v => v != fileCard))
        {
            var view = View(row);
            var point = row.TranslatePoint(new Point(), details);
            Canvas.SetLeft(view, point.X);
            Canvas.SetTop(view, point.Y - fileTop);
            lower.Children.Add(view);
        }
        Snapshot(lower, "windows-link-trust-and-files.png");

        Present(false, exampleCode, true, submitted: true);
        Check(!input.IsEnabled && !verify.IsEnabled && status.Text == L10n.T("Phone.LinkTrustWaiting"),
            "submitting the peer code shows that the other device still needs to verify");
        Present(true, null, false);
        Check(codes.Visibility == Visibility.Collapsed && input.Text.Length == 0
            && Equals(action.Content, L10n.T("Phone.LinkTrustRevoke")), "active trust clears the form and exposes revoke");
        typeof(PhoneDevicePage).GetMethod("RefreshWindowsTrust", Hidden)!.Invoke(page, [service, peer]);
        refreshPage.Invoke(page, null);
        page.UpdateLayout();
        var content = (FrameworkElement)((ScrollViewer)page.FindName("DeviceScroll")).Content;
        var resolution = (ComboBox)page.FindName("WindowsVideoResolutionCombo");
        var bitrate = (ComboBox)page.FindName("VideoBitRateCombo");
        var fps = (ComboBox)page.FindName("VideoFpsCombo");
        var smartVolume = (TextBlock)page.FindName("SmartVolumeHeader");
        Console.WriteLine($"Device detail layout in DIPs: resolution {resolution.ActualWidth:F0}, bitrate {bitrate.ActualWidth:F0}, FPS {fps.ActualWidth:F0}, smart volume heading {smartVolume.FontSize:F0}.");
        Snapshot(View(content), "windows-link-details-full-light.png");
    }
}
