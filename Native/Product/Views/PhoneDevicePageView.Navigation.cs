#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class PhoneDevicePageView
{
    private const string ConfigureTab = "phone.tabs.configure";
    private const string ConnectTab = "phone.tabs.connect";
    private const string RefreshDevice = "phone.refresh";
    private const string DeviceProfilePrefix = "phone.profile:";
    private static readonly UiSwitchSegment[] DeviceSegments =
    [
        new(ConfigureTab, "Phone.ConfigureTab", UiIcon.Settings),
        new(ConnectTab, "Phone.ConnectTab", UiIcon.Link)
    ];
    private UiRect _deviceDockArea;
    private AndroidPhoneProfile[] _deviceProfiles = [];

    private UiRect RenderDeviceTabs(D2dTarget g, UiThemePalette theme, UiRect area)
    {
        const float gap = 6f, height = 32f;
        string refresh = L10n.T("Apps.Refresh");
        float refreshWidth = PhoneTextWidth(refresh, 12.5f) + 24f;
        float configureWidth = PhoneTextWidth(L10n.T(DeviceSegments[0].LabelKey), 11f, 600) + 32f;
        float connectWidth = PhoneTextWidth(L10n.T(DeviceSegments[1].LabelKey), 11f, 600) + 32f;
        float total = configureWidth + connectWidth;
        float width = MathF.Min(total + UiSegmentedSwitch.Padding * 2f, area.Width - refreshWidth - gap);
        int selected = IsConnectionPage ? 1 : 0;
        UiSegmentedSwitch.Draw(g, theme, _hit, new UiRect(area.X, area.Y, width, height), DeviceSegments,
            [configureWidth / total, connectWidth / total], selected, selected);
        bool canRefresh = IsConnectionPage ? !_airPlayConnectionChoice : _actions.Host.Phone.Store.Profile.LastDevice != null;
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, RefreshDevice,
            new UiRect(area.Right - refreshWidth, area.Y, refreshWidth, height), refresh, enabled: canRefresh && !_busy);

        return area.Deflate(0f, height + 10f, 0f, 0f);
    }

    public UiDockRail Dock { get; }

    public void RenderDock(D2dTarget g, UiThemePalette theme, UiHitMap hit, float navEdge)
    {
        if (_deviceDockArea.IsEmpty)
        {
            Dock.Hide();
            return;
        }

        var area = new UiRect(navEdge, _deviceDockArea.Y, _deviceDockArea.Right - navEdge, _deviceDockArea.Height);
        Dock.Render(g, theme, hit, area, _deviceProfiles.Length, GetDeviceDockItem);
    }

    private UiDockRail.Item GetDeviceDockItem(int index)
    {
        var phone = _actions.Host.Phone;
        var profile = _deviceProfiles[index];
        var device = phone.Connections.Get(profile.LastDevice!).Device;
        string status = L10n.T(device == null ? "Phone.Disconnected" : device.IsApp ? "Phone.SrapConnected"
            : device.IsWifi ? "Phone.WifiConnected" : "Phone.UsbConnected");
        bool selected = phone.Store.Profile.Id == profile.Id;
        return new UiDockRail.Item(DeviceProfilePrefix + profile.Id, phone.Store.GetProfileDisplayName(profile),
            new UiItemIcon(null, UiIcon.Phone, GlyphInset: 0.05f), selected, selected, device == null, status);
    }
    private bool HandleDeviceNavigation(string id)
    {
        switch (id)
        {
            case ConfigureTab: ShowConfigurationPage(); return true;
            case ConnectTab: if (!IsConnectionPage) ShowConnectionPage(); return true;
            case RefreshDevice: RefreshFromUser(); return true;
        }
        if (!id.StartsWith(DeviceProfilePrefix, StringComparison.Ordinal)) return false;
        ShowConfigurationPage(id[DeviceProfilePrefix.Length..]);
        return true;
    }

    private void CompleteDeviceConnection()
    {
        _showConnection = false;
        _airPlayConnectionChoice = false;
        _appConnectionChoice = false;
        _scroll.Offset = 0f;
    }
}
#endif
