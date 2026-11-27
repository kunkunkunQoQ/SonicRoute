#if SONICROUTE_PHONE
using System.Globalization;
using SonicRoute.AndroidAudio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed partial class PhoneDevicePageView
{
    private const string ConfigureTab = "phone.tabs.configure";
    private const string ConnectTab = "phone.tabs.connect";
    private const string RefreshDevice = "phone.refresh";
    private const string DeviceListScroll = "phone.profiles.scroll";
    private const string DeviceProfilePrefix = "phone.profile:";
    private static readonly UiSwitchSegment[] DeviceSegments =
    [
        new(ConfigureTab, "Phone.ConfigureTab", UiIcon.Settings),
        new(ConnectTab, "Phone.ConnectTab", UiIcon.Link)
    ];
    private readonly UiScrollState _deviceListScroll = new();
    private UiRect _deviceListRect;
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

    private UiRect RenderDeviceList(D2dTarget g, UiThemePalette theme, UiRect area)
    {
        var phone = _actions.Host.Phone;
        string selectedId = phone.Store.Profile.Id;
        float width = Math.Clamp(area.Width * 0.215f, 132f, 164f);
        var card = new UiRect(area.X, area.Y, width, area.Height);
        UiPrimitives.DrawCard(g, theme, card);
        var inner = card.Deflate(8f, 10f);
        var badge = new UiRect(inner.Right - 26f, inner.Y + 2f, 26f, 20f);
        g.DrawText(L10n.T("Phone.Title"), new UiRect(inner.X, inner.Y, inner.Width - 30f, 24f),
            theme.TextSecondary, 13f, 600);
        UiPrimitives.DrawBadge(g, theme, badge, _deviceProfiles.Length.ToString(CultureInfo.InvariantCulture),
            theme.SurfaceAlt, theme.TextSecondary, fontSize: 11f);

        const float rowHeight = 56.5f;
        _deviceListRect = inner.Deflate(0f, 30f, 0f, 0f);
        _deviceListScroll.ViewportHeight = _deviceListRect.Height;
        _deviceListScroll.ContentHeight = _deviceProfiles.Length * rowHeight;
        _deviceListScroll.Clamp();
        float rowWidth = _deviceListRect.Width - (_deviceListScroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth : 0f);
        g.PushClip(_deviceListRect);
        _hit.PushClip(_deviceListRect);
        for (int index = 0; index < _deviceProfiles.Length; index++)
        {
            float y = _deviceListRect.Y + index * rowHeight - _deviceListScroll.Offset;
            if (y + rowHeight <= _deviceListRect.Y) continue;
            if (y >= _deviceListRect.Bottom) break;

            var profile = _deviceProfiles[index];
            var row = new UiRect(inner.X, y + 1f, rowWidth, rowHeight - 2f);
            string id = DeviceProfilePrefix + profile.Id;
            string name = phone.Store.GetProfileDisplayName(profile);
            _hit.Add(id, row, UiCursorKind.Hand, name);
            bool selected = profile.Id == selectedId;
            if (selected) g.FillRoundedRect(row, 7f, theme.NavSelected);
            else if (_hit.IsHover(id)) g.FillRoundedRect(row, 7f, theme.NavHover);

            var icon = new UiRect(row.X + 5f, row.CenterY - 13f, 26f, 26f);
            g.DrawIcon(UiIcon.Phone, icon, theme.TextSecondary, 1.6f);
            var device = phone.Connections.Get(profile.LastDevice!).Device;
            string status = L10n.T(device == null ? "Phone.Disconnected" : device.IsWifi ? "Phone.WifiConnected" : "Phone.UsbConnected");
            float textX = icon.Right + 8f, textWidth = MathF.Max(1f, row.Right - textX - 16f);
            g.DrawText(name, new UiRect(textX, row.CenterY - 17f, textWidth, 18f), theme.TextPrimary, 12.5f, selected ? 600 : 400);
            g.DrawText(status, new UiRect(textX, row.CenterY + 3f, textWidth, 15f), theme.TextSecondary, 10.5f);
            if (device != null) g.FillEllipse(row.Right - 8f, row.CenterY, 3f, 3f, theme.Success);
        }
        _hit.PopClip();
        g.PopClip();
        _deviceListScroll.Draw(g, theme, _hit, DeviceListScroll, _deviceListRect);
        return area.Deflate(width + 8f, 0f, 0f, 0f);
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
        _scroll.Offset = 0f;
    }
}
#endif
