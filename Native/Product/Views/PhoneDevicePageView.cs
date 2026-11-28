#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Native.Graphics;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// Device configuration and Android / AirPlay connection page.
/// </summary>
internal sealed partial class PhoneDevicePageView : IViewPage, IViewDockPage
{
    private const string PinnedAppGrip = "phone.order:";
    private readonly IViewHostActions _actions;
    private readonly UiScrollState _scroll = new();
    private readonly UiScrollState _fileScroll = new();
    private readonly UiVerticalReorder<string> _pinnedOrder = new();
    private string? _pinnedOrderProfile;
    private readonly ReceiveDirectorySettings _pcReceiveFolder;
    private UiHitMap _hit = new();
    private readonly AirPlayReceiverViewControl _airPlay;

    private readonly PhoneAppIconCache _appIcons;

    private bool _active;
    private bool _showConnection;
    private bool _airPlayConnectionChoice;
    private bool _busy;
    private CancellationTokenSource? _operation;
    private Task _qrTask = Task.CompletedTask;
    private string? _statusMessage;
    private bool _statusError;

    // Connection view state
    private string _pairAddress = "";
    private string _pairCode = "";
    private string _connectAddress = "";
    private IReadOnlyList<AndroidSavedConnection> _savedConnections = [];
    private AndroidSavedConnection? _savedChoice;
    private AndroidDevice[] _restorableUsb = [];
    private CancellationTokenSource? _searchSaved;
    private bool _searchedSaved, _manualSearch;
    private string? _savedSearchStatus;
    private byte[]? _qrBgra;
    private int _qrSizePx;
    private int _qrVersion;
    private bool _qrRetry;
    private CancellationTokenSource? _qrCts;

    // Connected view state
    private AndroidDevice? _activeDevice;
    private CancellationTokenSource? _catalogCts;
    private IReadOnlyList<AndroidApp> _catalogApps = Array.Empty<AndroidApp>();
    private string _appSearch = "";

    // Inline edit & slider drag state
    private UiRect _renameRect;

    // File transfer & remote browser state
    private string _remoteFolder = AndroidFileTransfer.Downloads;
    private string? _appReceiveFolder;
    private IReadOnlyList<AndroidStorageFile> _remoteFiles = Array.Empty<AndroidStorageFile>();
    private readonly HashSet<string> _selectedRemoteFiles = new(StringComparer.Ordinal);

    private bool _showFileBrowser;
    private bool _choosingPhoneFolder;
    private bool _remoteLoading;
    private string? _remoteError;
    private CancellationTokenSource? _remoteListCts;
    private UiRect _fileBrowserListRect;

    // Streaming codec/fps/bitrate settings state
    private AndroidStreamSettings _streamSettings = new();
    private string? _streamDeviceKey;
    private AndroidEncoderCatalog? _streamEncoders;
    private CancellationTokenSource? _encoderCts;
    private bool _encoderFailed;
    private bool? _windowsHevcAvailable;
    private bool _hevcLoadStarted;

    private bool IsConnectionPage => _showConnection;
    private bool AndroidConnectionVisible => _active && IsConnectionPage && !_airPlayConnectionChoice;
    private bool _pauseWirelessPairing;
    private bool CanShowQr => AndroidConnectionVisible && !_pauseWirelessPairing;
    private bool HasPairingInput => !string.IsNullOrWhiteSpace(_pairAddress) && _pairCode.Length == 6 && _pairCode.All(char.IsAsciiDigit);
    private bool UseHevc => _streamSettings.VideoDecoder == AndroidStreamSettings.WindowsHevcDecoder;
    public bool NeedsContentAnimationTick => _pinnedOrder.NeedsScrollTick(_scroll, _visibleDeviceRect);
    public bool NeedsAnimationTick => NeedsContentAnimationTick || Dock.NeedsAnimationTick;

    public void TickAnimation()
    {
        _pinnedOrder.TickScroll(_scroll, _visibleDeviceRect);
        Dock.TickAnimation();
    }

    public void OnFocusLost()
    {
        if (Dock.ResetHover()) _actions.Invalidate();
    }
    internal PhoneDevicePageView(IViewHostActions actions)
    {
        _actions = actions;
        Dock = new UiDockRail(DeviceProfilePrefix, actions.Invalidate);
        _appIcons = new PhoneAppIconCache(actions.Host, 96, actions.Invalidate);
        _pcReceiveFolder = new ReceiveDirectorySettings(Path.Combine(actions.Host.DataDirectory, "android-file-receive-folder.txt"));
        _airPlay = new AirPlayReceiverViewControl(actions, "phonepage.airplay");
    }

    internal void ShowConnectionPage(bool airPlay = false) => SwitchSection(connection: true, airPlay: airPlay);

    internal void ShowConfigurationPage(string? profileId = null)
    {
        SwitchSection(connection: false, profileId: profileId);
        if (profileId != null && _actions.Host.Phone.PcListening.Pending(profileId) != null)
        {
            CloseCatalog();
            CloseAndroidPicker();
            _scroll.Offset = 0f;
            _actions.Invalidate();
        }
    }

    private void SwitchSection(bool connection, bool airPlay = false, string? profileId = null)
    {
        var store = _actions.Host.Phone.Store;
        bool switchProfile = profileId != null && store.Profile.Id != profileId;
        if (_showConnection == connection && _airPlayConnectionChoice == airPlay && !switchProfile) return;

        OnCaptureLost();
        CancelViewOperation();
        if (_active) _actions.CloseInlineEdit(commit: false);
        CloseCatalog();
        CloseAndroidPicker();
        _showConnection = connection;
        _airPlayConnectionChoice = airPlay;
        _appCandidates = [];
        _appSearchComplete = false;
        if (switchProfile) store.SelectProfile(profileId!);
        _scroll.Offset = 0f;
        CancelEncoderRead();
        if (_active)
        {
            RefreshData();
            if (AndroidConnectionVisible) SearchAppCandidates();
        }
    }

    public void OnActivated()
    {
        if (_active) return;
        _active = true;
        var phone = _actions.Host.Phone;
        phone.Changed += OnPhoneChanged;

        phone.StartTracking();

        if (phone.Discovery.CurrentDevices == null && (IsConnectionPage ? !_airPlayConnectionChoice : phone.Store.Profile.LastDevice != null))
            _ = phone.RefreshAsync();
        RefreshData();
        if (AndroidConnectionVisible) SearchAppCandidates();
    }

    public void OnDeactivated()
    {
        if (!_active) return;
        _active = false;
        CancelViewOperation();
        _actions.CloseInlineEdit(commit: false);
        OnCaptureLost();
        var phone = _actions.Host.Phone;
        phone.Changed -= OnPhoneChanged;

        _airPlay.Detach();
        _appIcons.Clear();
        _deviceProfiles = [];
        StopQrPairing();
        CancelAppDiscovery();
        ResetSavedSearch();
        _pairCode = "";
        CancelEncoderRead();
        CloseAndroidPicker();
        CloseCatalog();
    }

    public void OnCaptureLost()
    {
        bool changed = _fileScroll.Dragging || _scroll.Dragging || _pinnedOrder.Pressed;
        FinishPinnedOrder(commit: false);
        _fileScroll.EndDrag();
        Dock.ResetHover();
        _scroll.EndDrag();
        if (changed) _actions.Invalidate();
    }

    public void OnFilesDropped(string[] paths, string? targetId)
    {
        if (targetId != "phone.sendfile" || !_active || _showCatalog || _showFileBrowser || IsConnectionPage || _activeDevice is not { } online || paths.Length == 0) return;
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        if (phone.Operations.CanWriteFiles(online))
            EnqueueOrStartFiles(online, profile.Id, paths, ReceiveFolder(online, profile), pullFrom: null);
    }

    public void RefreshData()
    {
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        _deviceProfiles = IsConnectionPage ? [] : phone.Store.Profiles.Where(p => p.LastDevice != null).ToArray();
        if (_pinnedOrder.Pressed && _pinnedOrderProfile != profile.Id) FinishPinnedOrder(commit: false);
        _appIcons.SetProfile(profile.Id);
        var online = profile.LastDevice is { } last ? phone.Connections.Get(last).Device : null;
        RefreshSharingStatus(online);
        if (!Equals(online, _activeDevice))
        {
            CancelEncoderRead();
            _streamDeviceKey = null;
            _activeDevice = online;
            _appReceiveFolder = null;
            _showFileBrowser = false;
            _remoteFiles = Array.Empty<AndroidStorageFile>();
            _selectedRemoteFiles.Clear();
            _remoteError = null;
            _catalogApps = Array.Empty<AndroidApp>();
            CancelRemoteList();
            CloseCatalog();
        }
        var cached = phone.Discovery.CurrentDevices ?? [];
        _restorableUsb = IsConnectionPage ? cached.Where(device => !device.IsWifi && device.State == "device" &&
            phone.Connections.Resolve(device) == null && !AndroidConnectionManager.IsAmbiguous(device, cached)).ToArray() : [];
        RefreshStreamingSettings(IsConnectionPage || (online ?? profile.LastDevice)?.IsApp == true ? null : online ?? profile.LastDevice);
        if (!_hevcLoadStarted && _active && !IsConnectionPage && profile.LastDevice != null)
        {
            _hevcLoadStarted = true;
            _ = LoadWindowsHevcAsync();
        }
        UpdateQrPairing();
        _actions.Invalidate();
    }

    public void Dispose()
    {
        OnDeactivated();
    }

    private void OnPhoneChanged()
    {
        _actions.Host.TryPost(() =>
        {
            if (_active) RefreshData();
        });
    }



    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _hit = hit;
        _pinnedOrder.ClearRows();
        contentRect = RenderDeviceTabs(g, t, contentRect);
        _deviceDockArea = !IsConnectionPage && _deviceProfiles.Length > 0 ? contentRect : default;
        if (!_deviceDockArea.IsEmpty)
            contentRect = contentRect.Deflate(UiDockRail.Width - 3f, 0f, 0f, 0f);
        string? status = IsConnectionPage && (_airPlayConnectionChoice || _nearbyPairing != null) ? null : _statusMessage;
        if (!string.IsNullOrEmpty(status))
        {
            float usable = MathF.Max(1f, contentRect.Width - 8f);
            float height = PhoneTextHeight(status, usable);
            var footer = new UiRect(contentRect.X + 4f, contentRect.Bottom - 8f - height, usable, height);
            g.DrawText(status, footer, _statusError ? t.Fail : t.TextSecondary, 12f, wrap: true);
            _hit.DescribeText("phone.status", footer, status);
            contentRect = new UiRect(contentRect.X, contentRect.Y, contentRect.Width, MathF.Max(1f, contentRect.Height - height - 8f));
        }
        _visibleDeviceRect = contentRect;
        if (!IsConnectionPage && _actions.Host.Phone.Store.Profile.LastDevice == null)
        {
            UiPrimitives.DrawCard(g, t, new UiRect(contentRect.X, contentRect.Y, contentRect.Width, 68f));
            g.DrawText(L10n.T("Phone.DetailConnectPhoneFirst"), new UiRect(contentRect.X + 14f, contentRect.Y, contentRect.Width - 28f, 68f),
                t.TextSecondary, 13f, wrap: true);
            return;
        }
        if (_showCatalog)
        {
            RenderCatalog(g, t, contentRect);
            return;
        }
        _scroll.ViewportHeight = contentRect.Height;

        float viewW = MathF.Max(1f, contentRect.Width - 6f - (_scroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth : 0f));
        float startY = contentRect.Y - _scroll.Offset;

        g.PushClip(contentRect);
        _hit.PushClip(contentRect);
        float endY = IsConnectionPage
            ? RenderConnectionView(g, t, contentRect.X + 2f, startY, viewW)
            : RenderConnectedView(g, t, contentRect.X + 2f, startY, viewW);
        _hit.PopClip();
        g.PopClip();

        bool hadScroll = _scroll.MaxOffset > 0.5f;
        float oldOffset = _scroll.Offset;
        _scroll.ContentHeight = MathF.Max(0f, endY - startY);
        _scroll.Clamp();
        bool targetChanged = _pinnedOrder.Retarget(_scroll, _visibleDeviceRect);
        if (targetChanged || hadScroll != (_scroll.MaxOffset > 0.5f) || oldOffset != _scroll.Offset) _actions.Invalidate();
        _scroll.Draw(g, t, _hit, "phone.scroll", contentRect);
    }

    private const float DeviceCardGap = 10f;
    private const float DeviceCardPadding = 12f;
    private const float DeviceCardRadius = 12f;
    private const float DeviceControlHeight = 28f;
    private const float DeviceHeadingSize = 14f;

    private float RenderDeviceHeader(D2dTarget g, UiThemePalette t, float x, float y, float w, string name, AndroidDevice? online)
    {
        var profile = _actions.Host.Phone.Store.Profile;
        var prompt = _actions.Host.Phone.PcListening.Pending(profile.Id);
        var card = new UiRect(x, y, w, 64f + (prompt == null ? 0 : PcListenPromptHeight(w)));
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);
        var iconBox = new UiRect(x + DeviceCardPadding, y + 13f, 38f, 38f);
        g.FillRoundedRect(iconBox, 8f, t.Overlay);
        g.DrawIcon(UiIcon.Phone, iconBox.Deflate(7f), t.TextSecondary, 1.6f);

        string action = L10n.T(online != null ? online.IsApp || online.IsWifi ? "Phone.Disconnect" : "Phone.StopReceiving"
            : profile is { AppTrustRevoked: true, LastDevice.IsApp: true } ? "Phone.SrapPairAgain" : "Phone.Reconnect");
        float actionW = MathF.Max(74f, PhoneTextWidth(action, 12f) + 22f);
        var more = new UiRect(card.Right - DeviceCardPadding - 28f, y + 32f - DeviceControlHeight / 2f, 28f, DeviceControlHeight);
        var primary = new UiRect(more.X - 6f - actionW, more.Y, actionW, DeviceControlHeight);
        float nameX = iconBox.Right + 10f;
        float? appActions = online is { IsApp: true } app ? RenderAppHeaderActions(g, t, more, nameX, app) : null;
        float nameAreaW = MathF.Max(1f, (appActions ?? primary.X) - nameX - 12f);
        bool editing = _actions.ActiveInlineEditTag == "phone.rename.box";
        _renameRect = new UiRect(nameX, y + 9f, MathF.Max(1f, nameAreaW - 30f), 26f);
        if (editing)
            UiPrimitives.DrawTextBox(g, t, _hit, "phone.rename.box", _renameRect, name, focused: true, fontSize: 16f);
        else
            g.DrawText(name, _renameRect, t.TextPrimary, 16f, 600);
        float renameX = editing ? _renameRect.Right : nameX + MathF.Min(_renameRect.Width, PhoneTextWidth(name, 16f, 600));
        UiPrimitives.DrawGhostIconButton(g, t, _hit, "phone.rename", new UiRect(renameX + 4f, _renameRect.Y, 26f, 26f),
            UiIcon.Edit, 13f, tooltip: L10n.T("Phone.RenamePhoneAutomation"));

        string connection = PhoneAppPresentation.ConnectionLabel(profile, online);
        g.FillEllipse(nameX + 4f, y + 46f, 3.5f, 3.5f, online == null ? t.TextSecondary : t.Success);
        g.DrawText(connection, new UiRect(nameX + 14f, y + 37f, MathF.Max(1f, nameAreaW - 14f), 18f), t.TextSecondary, 11.5f);
        _hit.DescribeText("phone.connection.status", new UiRect(nameX, y + 35f, nameAreaW, 22f), name + " · " + connection);

        if (appActions == null) UiPrimitives.DrawSecondaryButton(g, t, _hit, online == null ? "phone.connect" : "phone.disconnect",
            primary, action, enabled: !_busy, fontSize: 12f, tooltip: action);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.more", more, "", enabled: !_busy, icon: UiIcon.More,
            tooltip: L10n.T("Phone.DeviceTools"));
        if (prompt != null) DrawPcListeningPrompt(g, t, card, prompt);
        return card.Bottom + DeviceCardGap;
    }

    private float RenderConnectedView(D2dTarget g, UiThemePalette t, float x, float y, float w)
    {
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        var online = _activeDevice;
        var detail = profile.LastDevice;
        y = RenderDeviceHeader(g, t, x, y, w, phone.Store.GetProfileDisplayName(profile), online);
        if ((online ?? detail)?.IsApp == true)
        {
            y = RenderQuickPanelApps(g, t, x, y, w, phone, profile, online != null && phone.Operations.CanListApps(online));
            y = RenderFileTransferCard(g, t, x, y, w, phone, profile, online);
            return RenderClipboardCard(g, t, x, y, w, phone, detail);
        }
        y = RenderStreamingCard(g, t, x, y, w, detail != null);
        y = RenderQuickPanelApps(g, t, x, y, w, phone, profile, online != null);
        y = RenderFileTransferCard(g, t, x, y, w, phone, profile, online);
        return RenderClipboardCard(g, t, x, y, w, phone, detail);
    }

    private float RenderClipboardCard(D2dTarget g, UiThemePalette t, float x, float y, float w,
        PhonePreview phone, AndroidDevice? detail)
    {
        var clipCard = new UiRect(x, y, w, 60f);
        UiPrimitives.DrawCard(g, t, clipCard, DeviceCardRadius);
        bool clipEnabled = phone.Clipboard.IsEnabled(detail);
        string clipDesc = phone.Clipboard.StatusFor(detail);
        g.DrawText(L10n.T("Phone.ClipboardSyncTitle"), new UiRect(x + DeviceCardPadding, y + 9f, w - 72f, 20f),
            t.TextPrimary, DeviceHeadingSize, 600);
        g.DrawText(clipDesc, new UiRect(x + DeviceCardPadding, y + 30f, w - 72f, 20f), t.TextSecondary, 11.5f);
        UiPrimitives.DrawSwitch(g, t, _hit, "phone.clipboard",
            new UiRect(clipCard.Right - DeviceCardPadding - 36f, clipCard.CenterY - 12f, 36f, 24f), clipEnabled, enabled: detail != null);

        return clipCard.Bottom + DeviceCardGap;
    }

    private float RenderQuickPanelApps(D2dTarget g, UiThemePalette t, float x, float y, float w,
        PhonePreview phone, AndroidPhoneProfile profile, bool online)
    {
        var apps = profile.RetainedApps;
        float lx = x + DeviceCardPadding;
        float lw = w - 2f * DeviceCardPadding;
        float right = lx + lw;
        string pinHint = L10n.T("Phone.PinDesc");
        float pinHintH = PhoneTextHeight(pinHint, lw, 11f);
        float ly = y + DeviceCardPadding;
        float hintY = ly + DeviceControlHeight + 3f;
        float headerY = hintY + pinHintH + 7f;
        const float libRowH = 36f, columnHeaderH = 22f;
        float bodyY = headerY + (apps.Count == 0 ? 0f : columnHeaderH);
        var libCard = new UiRect(x, y, w, bodyY - y + (apps.Count == 0 ? 24f : apps.Count * libRowH) + 8f);
        UiPrimitives.DrawCard(g, t, libCard, DeviceCardRadius);

        string addLabel = L10n.T("Phone.AddAppsShort");
        float addWidth = PhoneTextWidth(addLabel, 12f) + 46f;
        var addRect = new UiRect(right - addWidth, ly, addWidth, DeviceControlHeight);
        string title = L10n.T("Phone.QuickPanelAppsTitle");
        string count = string.Format(L10n.T("Phone.RetainedCountFormat"), apps.Count);
        float titleW = PhoneTextWidth(title, DeviceHeadingSize, 600);
        float titleArea = addRect.X - lx - 10f;
        if (titleW + 10f + PhoneTextWidth(count, 11f) <= titleArea)
        {
            g.DrawText(count, new UiRect(lx + titleW + 10f, ly, titleArea - titleW - 10f, DeviceControlHeight), t.TextSecondary, 11f);
            titleArea = titleW + 2f;
        }
        g.DrawText(title, new UiRect(lx, ly, titleArea, DeviceControlHeight), t.TextPrimary, DeviceHeadingSize, 600);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.addapps", addRect, addLabel, enabled: online && !_busy,
            fontSize: 12f, icon: UiIcon.Plus, tooltip: addLabel);
        g.DrawText(pinHint, new UiRect(lx, hintY, lw, pinHintH), t.TextSecondary, 11f, wrap: true);

        if (apps.Count == 0)
        {
            g.DrawText(L10n.T("Phone.RetainedEmptyHint"), new UiRect(lx, bodyY, lw, 24f), t.TextSecondary, 11.5f);
            return libCard.Bottom + DeviceCardGap;
        }

        string smartLabel = L10n.T("Phone.SmartVolumeTitle");
        string pinLabel = L10n.T("Phone.AppPinColumn");
        float smartW = MathF.Max(68f, PhoneTextWidth(smartLabel, 11f, 600) + 16f);
        float pinW = MathF.Max(48f, PhoneTextWidth(pinLabel, 11f, 600) + 16f);
        float smartX = right - smartW;
        float pinX = smartX - pinW;
        float pinCenter = pinX + pinW / 2f;
        float smartCenter = smartX + smartW / 2f;
        float textX = lx + 38f;
        float textW = MathF.Max(1f, pinX - textX - 8f);

        g.DrawText(L10n.T("Nav.Apps"), new UiRect(textX, headerY, textW, columnHeaderH), t.TextSecondary, 11f, 600);
        g.DrawText(pinLabel, new UiRect(pinX, headerY, pinW, columnHeaderH), t.TextSecondary, 11f, 600, UiTextAlign.Center);
        g.DrawText(smartLabel, new UiRect(smartX, headerY, smartW, columnHeaderH), t.TextSecondary, 11f, 600, UiTextAlign.Center);

        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            var row = new UiRect(lx, bodyY + i * libRowH, lw, libRowH);
            if (apps.Count > 1) _pinnedOrder.AddRow(app.Package, row, _scroll.Offset);
            if (row.Bottom < _visibleDeviceRect.Y || row.Y >= _visibleDeviceRect.Bottom) continue;
            g.DrawLine(row.X, row.Y, row.Right, row.Y, t.Border, 1f);
            bool dragged = _pinnedOrder.IsSource(app.Package);
            if (dragged)
            {
                var selected = row.Deflate(0f, 2f);
                g.FillRoundedRect(selected, 6f, t.NavHover);
                g.DrawRoundedRect(selected, 6f, t.Accent, 1f);
            }
            _pinnedOrder.DrawInsertionLine(g, t, app.Package, row, gap: 0f);

            var icon = new UiRect(row.X + 4f, row.CenterY - 12f, 24f, 24f);
            if (_appIcons.TryGet(_appIcons.Resolve(_activeDevice, app), out var pixels, out int iw, out int ih, out int version))
                g.DrawBgraBitmap($"phonepage.icon:{app.Package}", pixels, iw, ih, icon, version: version);
            else
            {
                g.FillRoundedRect(icon, 6f, t.Overlay);
                string initial = string.IsNullOrEmpty(app.Name) ? "♪" : StringInfo.GetNextTextElement(app.Name);
                g.DrawText(initial, icon, t.TextSecondary, 13f, 600, UiTextAlign.Center);
            }

            if (apps.Count > 1)
            {
                string grip = PinnedAppGrip + app.Package;
                var dragArea = new UiRect(row.X, row.Y, pinX - row.X, row.Height);
                _hit.Add(grip, dragArea, UiCursorKind.Hand, L10n.T("Phone.DragAppOrder"));
                if (dragged || _hit.IsHover(grip))
                {
                    var badge = new UiRect(icon.Right - 10f, icon.Bottom - 10f, 13f, 13f);
                    g.FillRoundedRect(badge, 4f, t.SurfaceBg);
                    g.DrawIcon(UiIcon.Grip, badge.Deflate(2f), t.TextPrimary);
                }
            }

            g.DrawText(app.Name, new UiRect(textX, row.Y + 3f, textW, 16f), t.TextPrimary, 12f, 600);
            g.DrawText(PhoneAppPresentation.Description(app), new UiRect(textX, row.Y + 19f, textW, 14f), t.TextSecondary, 10.5f);
            DrawAppPin(g, t, new UiRect(pinCenter - 14f, row.CenterY - 14f, 28f, 28f), app, retained: true, compact: true);
            bool smart = phone.Store.IsSmartVolumeEnabled(profile.LastDevice, app.Package);
            UiPrimitives.DrawSwitch(g, t, _hit, $"phone.smart:{app.Package}",
                new UiRect(smartCenter - 20f, row.CenterY - 12f, 40f, 24f), smart);
        }

        return libCard.Bottom + DeviceCardGap;
    }

    private float RenderStreamingCard(D2dTarget g, UiThemePalette t, float x, float y, float w, bool enabled)
    {
        const float gap = 10f, labelSize = 11.5f, valueSize = 12f;
        float cx = x + DeviceCardPadding;
        float cw = w - 2f * DeviceCardPadding;
        int columns = cw >= 700f ? 4 : 2;
        float available = cw - (columns - 1) * gap;
        ReadOnlySpan<float> shares = columns == 4 ? [0.27f, 0.39f, 0.15f, 0.19f] : [0.5f, 0.5f];

        bool opus = enabled && _streamSettings.AudioDecoder != AndroidStreamSettings.RawPcmDecoder;
        string? videoEncoder = UseHevc ? _streamSettings.HevcEncoder : _streamSettings.VideoEncoder;
        ReadOnlySpan<(string Id, string Label, (string Text, string? Tooltip) Value, bool Enabled)> fields =
        [
            ("phone.stream.audioenc", L10n.T("Phone.StreamEncoder"), GetEncoderSelectedLabel(_streamSettings.AudioEncoder), opus),
            ("phone.stream.audiodec", L10n.T("Phone.StreamDecoder"), (GetAudioDecoderLabel(_streamSettings.AudioDecoder), null), enabled),
            ("phone.stream.audiobr", L10n.T("Phone.StreamBitRate"), ((_streamSettings.AudioBitRate / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Kbps", null), opus),
            ("phone.stream.audiolat", L10n.T("Phone.StreamAudioLatency"), (GetLatencyLabel(_streamSettings.AudioLatencyMode ?? AndroidStreamSettings.LatencyModeBalanced), null), enabled),
            ("phone.stream.videoenc", L10n.T("Phone.StreamEncoder"), GetEncoderSelectedLabel(videoEncoder), enabled),
            ("phone.stream.videodec", L10n.T("Phone.StreamDecoder"), (GetVideoDecoderLabel(_streamSettings.VideoDecoder), null), enabled),
            ("phone.stream.videobr", L10n.T("Phone.StreamBitRate"), ((_streamSettings.VideoBitRate / 1000000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps", null), enabled),
            ("phone.stream.videofps", L10n.T("Phone.StreamFps"), (_streamSettings.MaxFps.ToString(CultureInfo.InvariantCulture) + " fps", null), enabled),
        ];

        Span<UiRect> labels = stackalloc UiRect[8];
        Span<UiRect> controls = stackalloc UiRect[8];
        Span<float> rowLabelHeights = stackalloc float[2];
        float cy = y + DeviceCardPadding;
        float audioY = cy, videoY = 0f;
        for (int i = 0; i < fields.Length; i += columns)
        {
            if (i == 4)
            {
                cy += 4f;
                videoY = cy;
            }
            if (i is 0 or 4) cy += 24f;
            int row = i % 4 / columns;
            if (i < 4)
            {
                float height = 16f;
                for (int col = 0; col < columns; col++)
                {
                    float width = available * shares[col];
                    height = MathF.Max(height, PhoneTextHeight(fields[i + col].Label, width, labelSize));
                    height = MathF.Max(height, PhoneTextHeight(fields[i + col + 4].Label, width, labelSize));
                }
                rowLabelHeights[row] = height;
            }

            float labelH = rowLabelHeights[row];
            float left = cx;
            for (int col = 0; col < columns; col++)
            {
                float width = available * shares[col];
                labels[i + col] = new UiRect(left, cy, width, labelH);
                controls[i + col] = new UiRect(left, cy + labelH + 3f, width, DeviceControlHeight);
                left += width + gap;
            }
            cy += labelH + 3f + DeviceControlHeight + 7f;
        }

        var card = new UiRect(x, y, w, cy - y - 7f + DeviceCardPadding);
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);
        g.DrawText(L10n.T("Phone.StreamAudioLabel"), new UiRect(cx, audioY, cw, 20f), t.TextPrimary, DeviceHeadingSize, 600);
        g.DrawLine(cx, videoY - 5f, cx + cw, videoY - 5f, t.Border, 1f);
        g.DrawText(L10n.T("Phone.StreamVideoLabel"), new UiRect(cx, videoY, cw, 20f), t.TextPrimary, DeviceHeadingSize, 600);
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            g.DrawText(field.Label, labels[i], t.TextSecondary, labelSize, wrap: true);
            UiPrimitives.DrawComboBox(g, t, _hit, field.Id, controls[i], field.Value.Text, fontSize: valueSize,
                enabled: field.Enabled, tooltip: field.Value.Tooltip);
        }
        return card.Bottom + DeviceCardGap;
    }

    private float RenderFileTransferCard(
        D2dTarget g,
        UiThemePalette t,
        float x,
        float y,
        float w,
        PhonePreview phone,
        AndroidPhoneProfile profile,
        AndroidDevice? online)
    {
        var transfer = phone.Transfers.Get(profile.Id);
        int queuedCount = phone.QueuedTransferCount(profile.Id);
        var entries = _remoteFiles;

        if (_showFileBrowser)
        {
            const float rowH = 40f;
            float listViewH = Math.Clamp(entries.Count * rowH, 84f, 260f) + 6f;
            float browserCardH = 104f + listViewH;
            var fileCard = new UiRect(x, y, w, browserCardH);
            UiPrimitives.DrawCard(g, t, fileCard, DeviceCardRadius);

            float fx = fileCard.X + DeviceCardPadding;
            float fw = fileCard.Width - 2f * DeviceCardPadding;
            float fy = fileCard.Y + DeviceCardPadding;

            bool canUp = online != null && _remoteFolder != phone.Operations.FileRoot(online) && !_remoteLoading;
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.folderup", new UiRect(fx, fy, 34f, 28f), "..",
                enabled: online != null && canUp, fontSize: 12f, tooltip: L10n.T("Phone.AndroidParentFolder"));
            g.DrawText(AndroidFolderLabel(_remoteFolder), new UiRect(fx + 42f, fy, MathF.Max(40f, fw - 82f), 28f),
                t.TextPrimary, 13f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            UiPrimitives.DrawGhostIconButton(g, t, _hit, "phone.fileclose", new UiRect(fileCard.Right - 40f, fy, 28f, 28f),
                UiIcon.Close, 13f, tooltip: L10n.T("Phone.FileClose"));
            fy += 36f;

            var listBox = new UiRect(fx, fy, fw, listViewH);
            _fileBrowserListRect = listBox;
            g.FillRoundedRect(listBox, 8f, t.SurfaceBg);
            g.DrawRoundedRect(listBox, 8f, t.Border, 1f);

            var inner = listBox.Deflate(4f);
            _fileScroll.ViewportHeight = inner.Height;
            _fileScroll.ContentHeight = entries.Count * rowH;
            _fileScroll.Clamp();

            if (_remoteLoading)
            {
                g.DrawText(L10n.T("Phone.ProcessingStatus"), inner, t.TextSecondary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
            }
            else if (!string.IsNullOrEmpty(_remoteError))
            {
                g.DrawText(_remoteError, inner, t.Fail, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center, wrap: true);
            }
            else if (entries.Count == 0)
            {
                g.DrawText(L10n.T(_choosingPhoneFolder ? "Phone.AndroidNoSubfolders" : "Phone.AndroidEmptyFolder"),
                    inner, t.TextSecondary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
            }
            else
            {
                float rowW = _fileScroll.MaxOffset > 0.5f ? inner.Width - UiMetrics.ScrollBarWidth - 2f : inner.Width;
                g.PushClip(inner);
                _hit.PushClip(inner);
                for (int i = 0; i < entries.Count; i++)
                {
                    float ry = inner.Y + i * rowH - _fileScroll.Offset;
                    if (ry + rowH < inner.Y || ry > inner.Bottom) continue;

                    var f = entries[i];
                    var rowRect = new UiRect(inner.X, ry, rowW, rowH);
                    string fid = $"phone.rfile:{_remoteFolder}/{f.Name}";
                    bool selected = !f.Directory && _selectedRemoteFiles.Contains(f.Name);
                    _hit.Add(fid, rowRect, UiCursorKind.Hand, f.Label);
                    if (selected) g.FillRoundedRect(rowRect, 6f, t.NavSelected);
                    else if (_hit.IsHover(fid)) g.FillRoundedRect(rowRect, 6f, t.NavHover);

                    var icon = new UiRect(rowRect.X + 11f, rowRect.CenterY - 8f, 16f, 16f);
                    if (f.Directory) g.DrawIcon(UiIcon.Folder, icon, t.Overlay, filled: true);
                    g.DrawIcon(f.Directory ? UiIcon.Folder : UiIcon.File, icon, f.Directory ? t.TextPrimary : t.TextSecondary, 1.5f);
                    float textLeft = icon.Right + 10f;
                    if (!f.Directory)
                    {
                        var cbBox = new UiRect(rowRect.Right - 26f, rowRect.CenterY - 7.5f, 15f, 15f);
                        if (selected)
                        {
                            g.FillRoundedRect(cbBox, 4f, t.Accent);
                            g.DrawLine(cbBox.X + 3f, cbBox.Y + 7.5f, cbBox.X + 6.2f, cbBox.Y + 10.6f, t.OnAccent, 1.7f);
                            g.DrawLine(cbBox.X + 6.2f, cbBox.Y + 10.6f, cbBox.X + 11.6f, cbBox.Y + 4.4f, t.OnAccent, 1.7f);
                        }
                        else
                        {
                            g.FillRoundedRect(cbBox, 4f, t.SurfaceBg);
                            g.DrawRoundedRect(cbBox, 4f, t.Border, 1f);
                        }
                    }
                    else g.DrawIcon(UiIcon.ChevronRight, new UiRect(rowRect.Right - 24f, rowRect.CenterY - 7f, 14f, 14f), t.TextSecondary);

                    float rightReserved = f.Directory ? 35f : 102f;
                    g.DrawText(f.Label,
                        new UiRect(textLeft, rowRect.Y, MathF.Max(20f, rowRect.Right - rightReserved - textLeft), rowRect.Height),
                        t.TextPrimary, 13f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                    if (!f.Directory)
                        g.DrawText(f.SizeText, new UiRect(rowRect.Right - 99f, rowRect.Y, 64f, rowRect.Height),
                            t.TextSecondary, 11f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
                }
                _hit.PopClip();
                g.PopClip();
                _fileScroll.Draw(g, t, _hit, "phone.filescroll", inner);
            }

            fy = listBox.Bottom + 8f;
            int selCount = _selectedRemoteFiles.Count;
            string actionBase = L10n.T(_choosingPhoneFolder ? "Phone.AndroidUseFolder" : "Phone.AndroidReceiveToPc");
            string actionText = !_choosingPhoneFolder && selCount > 0 ? $"{actionBase} ({selCount})" : actionBase;
            bool actionEnabled = !_remoteLoading && _remoteError == null && online != null && (_choosingPhoneFolder || selCount > 0);
            UiPrimitives.DrawAccentButton(g, t, _hit, "phone.rfileact", new UiRect(fileCard.Right - 180f, fy, 168f, 30f),
                actionText, enabled: actionEnabled, fontSize: 11.5f);

            return fileCard.Bottom + DeviceCardGap;
        }

        _fileBrowserListRect = default;
        bool hasStatus = transfer != null || queuedCount > 0;
        float cw = w - 2f * DeviceCardPadding;
        var fileRoot = online == null ? null : phone.Operations.FileRoot(online);
        bool dropEnabled = online != null && phone.Operations.CanWriteFiles(online);
        string dropText = L10n.T(dropEnabled ? "Phone.FileDropHint" : online?.IsApp == true
            ? (fileRoot == null ? "Phone.SafChoose" : "Phone.SafReadOnly") : "Phone.FileOfflineHint");
        float hintHeight = PhoneTextHeight(dropText, cw - 24f);
        float dropH = MathF.Max(70f, hintHeight + 42f);
        float fileCardH = 124f + dropH + (hasStatus ? 30f : 0f);
        var card = new UiRect(x, y, w, fileCardH);
        UiPrimitives.DrawCard(g, t, card, DeviceCardRadius);

        float x0 = card.X + DeviceCardPadding;
        float y0 = card.Y + DeviceCardPadding;

        string browse = L10n.T("Phone.AndroidBrowseFiles");
        float browseW = MathF.Min(cw * 0.48f, PhoneTextWidth(browse, 12f) + 42f);
        g.DrawText(L10n.T("Phone.Files"), new UiRect(x0, y0, cw - browseW - 6f, DeviceControlHeight), t.TextPrimary, DeviceHeadingSize, 600);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.browsefiles", new UiRect(x0 + cw - browseW, y0, browseW, DeviceControlHeight),
            browse, enabled: fileRoot != null, fontSize: 12f, icon: UiIcon.Download);
        y0 += DeviceControlHeight + 7f;

        var dropRect = new UiRect(x0, y0, cw, dropH);
        if (dropEnabled) _hit.Add("phone.sendfile", dropRect, UiCursorKind.Hand, L10n.T("Phone.FileSend"));
        bool dropHot = dropEnabled && _hit.IsHover("phone.sendfile");
        if (dropHot) g.FillRoundedRect(dropRect, 8f, t.NavHover);
        g.DrawRoundedRect(dropRect, 8f, dropHot ? t.Accent : t.Border, 1f);
        float iconTop = dropRect.CenterY - (24f + hintHeight) / 2f;
        g.DrawIcon(UiIcon.File, new UiRect(dropRect.CenterX - 9f, iconTop, 18f, 18f), t.TextSecondary, 1.5f);
        g.DrawText(dropText, new UiRect(x0 + 12f, iconTop + 24f, cw - 24f, hintHeight), t.TextSecondary,
            12f, 400, UiTextAlign.Center, UiVertAlign.Center, wrap: true);
        y0 = dropRect.Bottom + 10f;

        if (hasStatus)
        {
            string prog = FormatTransferStatus(transfer, queuedCount);
            bool running = transfer?.Phase == AndroidFilePhase.Running;
            g.DrawText(prog, new UiRect(x0, y0, cw - (running ? 76f : 0f), 24f),
                transfer?.Phase == AndroidFilePhase.Failed ? t.Fail : t.Accent, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            if (running)
            {
                UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.cancelfile", new UiRect(card.Right - 82f, y0, 70f, 24f),
                    L10n.T("Auto.Cancel"), fontSize: 11f, danger: true);
            }
        }

        if (hasStatus) y0 += 30f;
        void DrawFolder(string label, string location, string id, bool enabled)
        {
            string change = L10n.T("Phone.FileChangeFolder");
            float actionW = MathF.Min(cw * 0.34f, PhoneTextWidth(change, 11f) + 18f);
            float labelW = MathF.Min(cw * 0.38f, PhoneTextWidth(label, 11f));
            g.DrawText(label, new UiRect(x0, y0, labelW, 24f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            float pathX = x0 + labelW + 9f;
            g.DrawText(location, new UiRect(pathX, y0, MathF.Max(1f, x0 + cw - actionW - pathX - 6f), 24f), t.TextPrimary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var action = new UiRect(x0 + cw - actionW, y0, actionW, 24f);
            if (enabled)
            {
                _hit.Add(id, action, UiCursorKind.Hand);
                if (_hit.IsHover(id)) g.FillRoundedRect(action, 7f, t.NavHover);
            }
            g.DrawText(change, action, enabled ? t.TextPrimary : t.TextSecondary.WithAlphaFraction(0.55f), 11f, 400, UiTextAlign.Center, UiVertAlign.Center);
        }
        DrawFolder(L10n.T("Phone.AndroidReceiveFolder"), AndroidFolderLabel(ReceiveFolder(online ?? profile.LastDevice, profile)), "phone.remotefolder", dropEnabled);
        y0 += 31f;
        DrawFolder(L10n.T("Phone.FileReceiveFolder"), _pcReceiveFolder.GetReceiveDirectory(), "phone.localfolder", true);
        return card.Bottom + DeviceCardGap;
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (_pinnedOrder.Pressed)
        {
            if (leftDown) return _pinnedOrder.Move(y, _scroll, _visibleDeviceRect);
            FinishPinnedOrder(commit: false);
            return true;
        }
        if (leftDown && _fileScroll.DragMove(y)) { _actions.Invalidate(); return true; }
        if (leftDown && _scroll.DragMove(y)) { _actions.Invalidate(); return true; }
        bool changed = _hit.UpdateHover(x, y);
        if (changed)
        {
            _actions.OwnerWindow.SetCursorKind(_hit.Hovered?.Cursor ?? UiCursorKind.Arrow);
            _actions.Invalidate();
        }
        return changed;
    }

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        if (_showCatalog && e.Button == UiMouseButton.Right && targetId?.StartsWith(CatalogRow, StringComparison.Ordinal) == true)
        {
            ShowCatalogMenu(targetId[CatalogRow.Length..]);
            return true;
        }
        if (e.Button != UiMouseButton.Left) return false;
        if (targetId?.StartsWith(PinnedAppGrip, StringComparison.Ordinal) == true)
        {
            _pinnedOrderProfile = _actions.Host.Phone.Store.Profile.Id;
            _pinnedOrder.Begin(targetId[PinnedAppGrip.Length..], e.Y);
            return true;
        }
        if (targetId == "phone.filescroll" && _fileScroll.BeginDrag(e.Y))
        {
            _actions.Invalidate();
            return true;
        }
        if (targetId == "phone.scroll" && _scroll.BeginDrag(e.Y))
        {
            _actions.CloseInlineEdit(commit: true);
            _actions.Invalidate();
            return true;
        }
        var hit = _hit.HitTest(e.X, e.Y);
        _actions.Invalidate();
        return hit != null;
    }

    public bool OnPointerUp(UiPointerEventArgs e, string? clickId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (_pinnedOrder.Pressed)
        {
            if (_pinnedOrder.Dragging) _pinnedOrder.Move(e.Y, _scroll, _visibleDeviceRect);
            FinishPinnedOrder(commit: true);
            return true;
        }
        if (_fileScroll.Dragging)
        {
            _fileScroll.EndDrag();
            _actions.Invalidate();
            return true;
        }
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            _actions.Invalidate();
            return true;
        }

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit is null || hit.Value.Id != clickId) { _actions.Invalidate(); return false; }
        string id = hit.Value.Id;

        if (HandleDeviceNavigation(id)) return true;
        if (_airPlay.HandleClick(id, hit.Value.Bounds)) return true;
        if (HandleAppDeviceAction(id, hit.Value.Bounds)) return true;

        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        var online = _activeDevice;

        if (id == "phone.connchoice.android")
        {
            if (_airPlayConnectionChoice)
            {
                _actions.CloseInlineEdit(commit: false);
                CancelViewOperation();
                _airPlayConnectionChoice = false;
                _scroll.Offset = 0f;
                UpdateQrPairing();
                SearchAppCandidates();
                _actions.Invalidate();
            }
            return true;
        }
        if (id == "phone.connchoice.airplay")
        {
            if (!_airPlayConnectionChoice)
            {
                _actions.CloseInlineEdit(commit: false);
                CancelViewOperation();
                _airPlayConnectionChoice = true;
                _scroll.Offset = 0f;
                UpdateQrPairing();
                _actions.Invalidate();
            }
            return true;
        }

        if (id == "phone.qrrefresh")
        {
            if (CanShowQr)
            {
                StopQrPairing();
                UpdateQrPairing();
                _actions.Invalidate();
            }
            return true;
        }

        if (id == "phone.rename" || id == "phone.rename.box")
        {
            var boxRect = _renameRect.IsEmpty ? hit.Value.Bounds : _renameRect;
            void Rename(string text)
            {
                if (phone.Store.Profile.Id == profile.Id) phone.Store.Rename(text);
            }
            _actions.StartInlineEdit("phone.rename.box", boxRect, phone.Store.GetProfileDisplayName(profile), Rename, (text, _) =>
            {
                Rename(text);
                _actions.Invalidate();
            }, selectAll: true, maxLength: 64);
            return true;
        }
        if (id == "phone.appsearch")
        {
            _actions.StartInlineEdit(id, hit.Value.Bounds, _appSearch, SetCatalogSearch, (text, _) => SetCatalogSearch(text));
            return true;
        }
        if (id == "phone.pairaddr")
        {
            _actions.StartInlineEdit("phone.pairaddr", hit.Value.Bounds, _pairAddress, t => { _pairAddress = t; _actions.Invalidate(); }, (t, _) =>
            {
                _pairAddress = t.Trim();
                _actions.Invalidate();
            });
            return true;
        }
        if (id == "phone.paircode")
        {
            _actions.StartInlineEdit("phone.paircode", hit.Value.Bounds, _pairCode, t => { _pairCode = t; _actions.Invalidate(); }, (t, _) =>
            {
                _pairCode = t.Trim();
                _actions.Invalidate();
            }, mono: true, maxLength: 6);
            return true;
        }
        if (id == "phone.connaddr")
        {
            OpenConnectAddress(hit.Value.Bounds);
            return true;
        }

        if (id == "phone.pairbtn")
        {
            _actions.CloseInlineEdit(commit: true);
            string address = _pairAddress.Trim(), code = _pairCode.Trim();
            _pairCode = "";
            RunBusy(ct => phone.PairAsync(address, code, ct), "Phone.ConnectedStatus", openDevice: true);
            return true;
        }
        if (id == "phone.searchsaved") { SearchSaved(manual: true); return true; }
        if (id == "phone.saved" && !_busy)
        {
            var choices = _savedConnections.Select(saved => new ComboChoice(saved.Address, SavedLabel(saved))).ToList();
            choices.Add(new("", L10n.T("Phone.ManualConnectAddress")));
            _actions.ShowCombo(hit.Value.Bounds, choices, _savedChoice?.Address ?? "", pick =>
            {
                if (!_active || !IsConnectionPage || _airPlayConnectionChoice || _busy) return;
                _savedChoice = _savedConnections.FirstOrDefault(saved => saved.Address == pick.Key);
                _actions.Invalidate();
                if (pick.Key.Length == 0)
                {
                    var box = hit.Value.Bounds;
                    _actions.OwnerWindow.RenderNow();
                    _hit.FocusPointer(box.CenterX, box.CenterY);
                    OpenConnectAddress(box);
                }
            });
            return true;
        }
        if (id == "phone.connbtn")
        {
            _actions.CloseInlineEdit(commit: true);
            var saved = _savedChoice;
            string address = _connectAddress.Trim();
            RunBusy(ct => phone.DiscoverAndConnectAsync(address, ct, saved), "Phone.ConnectedStatus", openDevice: true,
                failed: error =>
                {
                    if (saved != null && error is IOException or System.Net.Sockets.SocketException)
                        _savedChoice = null;
                });
            return true;
        }
        const string restoreUsb = "phone.restoreusb:";
        if (id.StartsWith(restoreUsb, StringComparison.Ordinal))
        {
            var chosen = _restorableUsb.FirstOrDefault(d => AndroidDeviceIdentity.Key(d) == id[restoreUsb.Length..]);
            if (chosen != null) RunBusy(ct => phone.RestoreUsbAsync(chosen, ct), "Phone.ConnectedStatus", openDevice: true);
            return true;
        }
        if (id == "phone.more" && profile.LastDevice != null)
        {
            ShowDeviceMenu(hit.Value.Bounds, profile.Id);
            return true;
        }
        if (id is "phone.connect" or "phone.disconnect")
        {
            RunDeviceCommand(id);
            return true;
        }
        if (id == "phone.addapps" && online != null)
        {
            _showCatalog = true;
            _focusCatalogSearch = true;
            _detailOffset = _scroll.Offset;
            _scroll.Offset = 0f;
            _appSearch = "";
            FilterCatalog();
            LoadCatalog(online, refresh: false);
            _actions.Invalidate();
            return true;
        }
        if (id == "phone.catalog.done") { CloseCatalog(); _actions.Invalidate(); return true; }
        if (id.StartsWith(CatalogRow, StringComparison.Ordinal) && _showCatalog)
        {
            SelectCatalogRow(id[CatalogRow.Length..], e.Ctrl, e.Shift);
            return true;
        }

        if (id.StartsWith("phone.stream.", StringComparison.Ordinal) && (online ?? profile.LastDevice) is { } settingsDevice)
        {
            return HandleStreamComboClick(id, hit.Value.Bounds, settingsDevice);
        }

        if (id.StartsWith("phone.pin:", StringComparison.Ordinal))
        {
            string pkg = id[10..];
            var app = profile.RetainedApps.Concat(_catalogApps).FirstOrDefault(a => a.Package == pkg);
            if (app != null)
            {
                if (profile.RetainedApps.Any(a => a.Package == pkg)) phone.Store.Remove(pkg);
                else phone.Store.Retain(app);
                _actions.Invalidate();
            }
            return true;
        }
        if (id.StartsWith("phone.smart:", StringComparison.Ordinal))
        {
            string pkg = id[12..];
            bool smart = phone.Store.IsSmartVolumeEnabled(profile.LastDevice, pkg);
            phone.Store.SetSmartVolume(pkg, !smart);
            phone.Sources.SyncSmartVolume((d, p) => phone.Store.IsSmartVolumeEnabled(d, p));
            _actions.Invalidate();
            return true;
        }
        if (id == "phone.sendfile" && online != null)
        {
            var paths = NativeDialogs.OpenMultiple(_actions.OwnerWindow.Handle, "All Files|*.*", L10n.T("Phone.FileSend"));
            if (paths.Length > 0 && _active && Equals(_activeDevice, online))
            {
                EnqueueOrStartFiles(online, profile.Id, paths, ReceiveFolder(online, profile), pullFrom: null);
            }
            return true;
        }
        if (id == "phone.browsefiles" && online != null)
        {
            OpenAndroidPicker(online, ReceiveFolder(online, profile), chooseFolder: false);
            return true;
        }
        if (id == "phone.remotefolder" && online != null)
        {
            OpenAndroidPicker(online, ReceiveFolder(online, profile), chooseFolder: true);
            return true;
        }
        if (id == "phone.localfolder")
        {
            string currentDir = _pcReceiveFolder.GetReceiveDirectory();
            if (NativeDialogs.BrowseFolder(_actions.OwnerWindow.Handle, L10n.T("Phone.FileReceiveFolder"), currentDir) is { Length: > 0 } picked)
            {
                try
                {
                    _pcReceiveFolder.SetReceiveDirectory(picked);
                    _actions.Invalidate();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    SetStatus(ex.Message, true);
                }
            }
            return true;
        }
        if (id == "phone.fileclose")
        {
            CloseAndroidPicker();
            return true;
        }
        if (id == "phone.folderup" && online != null && _remoteFolder != phone.Operations.FileRoot(online))
        {
            int slash = _remoteFolder.TrimEnd('/').LastIndexOf('/');
            string parent = slash <= 0 ? AndroidFileTransfer.Storage : _remoteFolder[..slash];
            LoadRemoteFolder(online, parent);
            return true;
        }
        if (id.StartsWith("phone.rfile:", StringComparison.Ordinal) && online != null)
        {
            string prefix = $"phone.rfile:{_remoteFolder}/";
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string name = id[prefix.Length..];
            var file = _remoteFiles.FirstOrDefault(f => f.Name == name);
            if (file != null)
            {
                if (file.Directory)
                {
                    LoadRemoteFolder(online, _remoteFolder.TrimEnd('/') + "/" + file.Name);
                }
                else if (!_choosingPhoneFolder)
                {
                    if (!_selectedRemoteFiles.Add(file.Name))
                        _selectedRemoteFiles.Remove(file.Name);
                    _actions.Invalidate();
                }
            }
            return true;
        }
        if (id == "phone.rfileact" && online != null)
        {
            ActOnAndroidPicker(online, profile.Id);
            return true;
        }
        if (id == "phone.cancelfile")
        {
            phone.ClearQueuedTransfers(profile.Id);
            phone.Transfers.Cancel(profile.Id);
            return true;
        }
        if ((id == "phone.clipboard" || id == "phone.clipboard:sw") && profile.LastDevice is { } clipDevice)
        {
            bool next = !phone.Clipboard.IsEnabled(clipDevice);
            phone.Clipboard.SetEnabled(clipDevice, next);
            _actions.Invalidate();
            return true;
        }

        return false;
    }

    private void OpenConnectAddress(UiRect box) =>
        _actions.StartInlineEdit("phone.connaddr", box, _connectAddress, value => { _connectAddress = value; _actions.Invalidate(); },
            (value, _) => { _connectAddress = value.Trim(); _actions.Invalidate(); });

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (!horizontal && !_pinnedOrder.Pressed && Dock.HandlesWheel(_hit, x, y)) return Dock.Scroll(delta);
        if (!_visibleDeviceRect.Contains(x, y) && !_pinnedOrder.Pressed) return false;
        _actions.CloseInlineEdit(commit: true);
        if (_showFileBrowser && _fileBrowserListRect.Contains(x, y))
        {
            _fileScroll.OnWheel(delta);
            _actions.Invalidate();
            return true;
        }
        _scroll.OnWheel(delta);
        _pinnedOrder.Retarget(_scroll, _visibleDeviceRect);
        _actions.Invalidate();
        return true;
    }

    public bool OnFocusedKey(string id, int vk)
    {
        if (!_showFileBrowser || vk != 0x0D || _remoteLoading || _activeDevice is not { } device) return false;
        string prefix = "phone.rfile:" + _remoteFolder + "/";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var file = _remoteFiles.FirstOrDefault(entry => entry.Name == id[prefix.Length..]);
        if (file == null) return false;
        if (file.Directory) LoadRemoteFolder(device, _remoteFolder.TrimEnd('/') + "/" + file.Name);
        else if (_selectedRemoteFiles.Count > 0) ActOnAndroidPicker(device, _actions.Host.Phone.Store.Profile.Id);
        return true;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B && _pinnedOrder.Pressed)
        {
            FinishPinnedOrder(commit: false);
            return true;
        }
        if (_actions.ActiveInlineEditTag != null) return false;
        if (_showCatalog)
        {
            if (vk == 0x1B) { CloseCatalog(); _actions.Invalidate(); return true; }
            if (ctrl && vk == 0x41)
            {
                _selectedCatalog.Clear();
                foreach (var app in _filteredCatalog) _selectedCatalog.Add(app.Package);
                _actions.Invalidate();
                return true;
            }
            return false;
        }
        if (_showFileBrowser)
        {
            if (vk == 0x1B) // Escape
            {
                CloseAndroidPicker();
                return true;
            }
            if (vk == 0x08 && _activeDevice is { } online && _remoteFolder != _actions.Host.Phone.Operations.FileRoot(online) && !_remoteLoading) // Backspace
            {
                int slash = _remoteFolder.TrimEnd('/').LastIndexOf('/');
                string parent = slash <= 0 ? AndroidFileTransfer.Storage : _remoteFolder[..slash];
                LoadRemoteFolder(online, parent);
                return true;
            }
        }
        return false;
    }

    private void FinishPinnedOrder(bool commit)
    {
        string? profileId = _pinnedOrderProfile;
        _pinnedOrderProfile = null;
        if (_pinnedOrder.End(commit) is not { } drop || profileId == null) return;
        var store = _actions.Host.Phone.Store;
        if (store.Profile.Id != profileId) return;
        try { store.MoveRetainedApp(profileId, drop.Source, drop.Anchor, drop.After); }
        catch (IOException ex)
        {
            ExceptionDiagnostics.Record("Phone.MoveRetainedApp", ex);
            _actions.ShowNotice(L10n.T("Tray.SaveFailed"));
        }
    }

    private void EnqueueOrStartFiles(AndroidDevice online, string profileId, string[] paths, string destination, string? pullFrom)
    {
        bool accepted = _actions.Host.Phone.EnqueueTransfer(online, profileId, paths, destination, pullFrom);
        SetStatus(accepted ? "" : L10n.T("Tray.AudioActionFailed"), !accepted);
        _actions.Invalidate();
    }

    private void OpenAndroidPicker(AndroidDevice online, string initialFolder, bool chooseFolder)
    {
        if (_showFileBrowser && _choosingPhoneFolder == chooseFolder)
        {
            CloseAndroidPicker();
            return;
        }
        _choosingPhoneFolder = chooseFolder;
        _showFileBrowser = true;
        _fileScroll.Offset = 0f;
        LoadRemoteFolder(online, string.IsNullOrWhiteSpace(initialFolder) ? AndroidFileTransfer.Downloads : initialFolder);
    }

    private void CloseAndroidPicker()
    {
        CancelRemoteList();
        _showFileBrowser = false;
        _remoteFiles = [];
        _remoteLoading = false;
        _remoteError = null;
        _selectedRemoteFiles.Clear();
        _actions.Invalidate();
    }

    private void CancelRemoteList()
    {
        var old = _remoteListCts;
        _remoteListCts = null;
        if (old != null)
        {
            try { old.Cancel(); } catch { }
        }
    }

    private void ActOnAndroidPicker(AndroidDevice online, string profileId)
    {
        if (_remoteLoading || _remoteError != null || !_actions.Host.Phone.Connections.IsCurrent(online)) return;
        if (_choosingPhoneFolder)
        {
            if (online.IsApp) _appReceiveFolder = _remoteFolder;
            else _actions.Host.Phone.Store.SetReceiveFolder(profileId, _remoteFolder);
            CloseAndroidPicker();
            return;
        }
        string[] names = _remoteFiles.Where(f => !f.Directory && _selectedRemoteFiles.Contains(f.Name)).Select(f => f.Name).ToArray();
        if (names.Length == 0) return;
        EnqueueOrStartFiles(online, profileId, names, _pcReceiveFolder.GetReceiveDirectory(), pullFrom: _remoteFolder);
        CloseAndroidPicker();
    }

    private static string FormatTransferStatus(AndroidFileProgress? progress, int queuedCount)
    {
        string text = progress?.Phase switch
        {
            AndroidFilePhase.Running => string.Format(L10n.T("Phone.FileBatchProgress"), progress.Index, progress.Count, progress.Text),
            AndroidFilePhase.Saved => string.Format(L10n.T("Phone.FileSaved"), progress.Text),
            AndroidFilePhase.InvalidFile => L10n.T("Phone.FileOnlyFiles"),
            AndroidFilePhase.Cancelled => L10n.T("Auto.Cancel"),
            AndroidFilePhase.Failed => progress.Text,
            _ => ""
        };
        if (queuedCount > 0)
            text = string.IsNullOrEmpty(text) ? $"+{queuedCount}" : $"{text} (+{queuedCount})";
        return text;
    }

    private string ReceiveFolder(AndroidDevice? device, AndroidPhoneProfile profile)
    {
        if (device?.IsApp != true) return profile.ReceiveFolder;
        string root = _actions.Host.Phone.Operations.FileRoot(device) ?? SrapFilesClient.Prefix;
        return _appReceiveFolder?.StartsWith(root + "/", StringComparison.Ordinal) == true ? _appReceiveFolder : root;
    }

    private static string AndroidFolderLabel(string path)
    {
        if (path.StartsWith(SrapFilesClient.Prefix, StringComparison.Ordinal))
        {
            int slash = path.IndexOf('/', 1);
            return L10n.T("Phone.SafFolder") + (slash < 0 ? "" : " / " + AndroidFileTransfer.FormatDisplayName(path[(path.LastIndexOf('/') + 1)..]));
        }
        string phone = L10n.T("Phone.AndroidStorage");
        if (path == AndroidFileTransfer.Storage) return phone;
        int split = path.LastIndexOf('/');
        if (split <= 0) return path;
        string parent = path[..split];
        string downloads = L10n.T("Phone.AndroidDownloads");
        string current = path == AndroidFileTransfer.Downloads ? downloads : AndroidFileTransfer.FormatDisplayName(path[(split + 1)..]);
        string prefix = parent == AndroidFileTransfer.Storage ? phone : parent == AndroidFileTransfer.Downloads ? downloads : "…";
        return $"{prefix}  ›  {current}";
    }

    // --- Streaming Settings Helpers (matching WPF PhoneDevicePage.Streaming.cs) ---

    private void RefreshStreamingSettings(AndroidDevice? device)
    {
        string? key = device == null ? null : AndroidDeviceIdentity.Key(device);
        if (key == _streamDeviceKey && (device == null || _activeDevice == null || _encoderCts != null || _streamEncoders != null || _encoderFailed))
        {
            return;
        }

        CancelEncoderRead();
        _streamDeviceKey = key;
        _streamEncoders = null;
        _encoderFailed = false;
        var phone = _actions.Host.Phone;
        _streamSettings = device == null ? new() : phone.Settings.GetStreaming(device);

        if (_active && !IsConnectionPage && _activeDevice != null && device != null)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime, phone.Connections.OperationToken(device));
            _encoderCts = cts;
            _ = LoadStreamEncodersAsync(device, key!, cts);
        }
    }

    private async Task LoadWindowsHevcAsync()
    {
        bool available = await Task.Run(() => MediaFoundationH264Decoder.SupportsHevc).ConfigureAwait(false);
        _actions.Host.TryPost(() =>
        {
            _windowsHevcAvailable = available;
            if (_active) _actions.Invalidate();
        });
    }

    private async Task LoadStreamEncodersAsync(AndroidDevice device, string key, CancellationTokenSource cts)
    {
        CancellationToken token = cts.Token;
        try
        {
            var encoders = await Task.Run(() => _actions.Host.Phone.Operations.EncodersAsync(device, token), token).ConfigureAwait(false);
            _actions.Host.TryPost(() =>
            {
                if (!_active || token.IsCancellationRequested || !ReferenceEquals(_encoderCts, cts) || _streamDeviceKey != key)
                    return;
                _streamEncoders = encoders;
                _encoderFailed = false;
                _actions.Invalidate();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _actions.Host.TryPost(() =>
            {
                if (_active && !token.IsCancellationRequested && ReferenceEquals(_encoderCts, cts))
                {
                    _encoderFailed = true;
                    SetStatus(PhonePreview.TranslateError(ex), true);
                }
            });
        }
        finally
        {
            _actions.Host.TryPost(() =>
            {
                if (ReferenceEquals(_encoderCts, cts))
                {
                    _encoderCts = null;
                    if (_active) _actions.Invalidate();
                }
                cts.Dispose();
            });
        }
    }

    private bool HandleStreamComboClick(string id, UiRect anchor, AndroidDevice online)
    {
        var phone = _actions.Host.Phone;
        if (id == "phone.stream.audioenc")
        {
            if (_streamSettings.AudioDecoder == AndroidStreamSettings.RawPcmDecoder) return true;
            RetryEncodersIfNeeded(online);
            var choices = BuildEncoderChoices(_streamEncoders?.Audio, _streamEncoders?.Kinds, _streamSettings.AudioEncoder);
            _actions.ShowCombo(anchor, choices, _streamSettings.AudioEncoder ?? "", picked =>
            {
                if (picked.Key == "#status")
                {
                    RetryEncodersIfNeeded(online, forceRetry: true);
                    return;
                }
                SaveStreaming(online, _streamSettings with { AudioEncoder = NullIfEmpty(picked.Key) });
            }, fitChoices: true);
            return true;
        }
        if (id == "phone.stream.videoenc")
        {
            RetryEncodersIfNeeded(online);
            string? selected = UseHevc ? _streamSettings.HevcEncoder : _streamSettings.VideoEncoder;
            var list = UseHevc ? _streamEncoders?.Hevc : _streamEncoders?.Video;
            var choices = BuildEncoderChoices(list, _streamEncoders?.Kinds, selected);
            _actions.ShowCombo(anchor, choices, selected ?? "", picked =>
            {
                if (picked.Key == "#status")
                {
                    RetryEncodersIfNeeded(online, forceRetry: true);
                    return;
                }
                string? val = NullIfEmpty(picked.Key);
                SaveStreaming(online, UseHevc
                    ? _streamSettings with { HevcEncoder = val }
                    : _streamSettings with { VideoEncoder = val });
            }, fitChoices: true);
            return true;
        }
        if (id == "phone.stream.audiodec")
        {
            var choices = new List<ComboChoice>
            {
                new("", L10n.T("Phone.StreamDecoderAutomatic")),
                new(AndroidStreamSettings.ConcentusDecoder, string.Format(L10n.T("Phone.StreamDecoderManaged"), "Concentus Opus")),
                new(AndroidStreamSettings.RawPcmDecoder, string.Format(L10n.T("Phone.StreamDecoderRawPcm"), "Raw PCM")),
            };
            _actions.ShowCombo(anchor, choices, _streamSettings.AudioDecoder ?? "", picked =>
                SaveStreaming(online, _streamSettings with { AudioDecoder = NullIfEmpty(picked.Key) }), fitChoices: true);
            return true;
        }
        if (id == "phone.stream.videodec")
        {
            const string windowsName = "Microsoft H.264 Video Decoder MFT";
            var choices = new List<ComboChoice>
            {
                new("", L10n.T("Phone.StreamDecoderAutomatic")),
                new(AndroidStreamSettings.WindowsVideoDecoder, string.Format(L10n.T("Phone.StreamDecoderAccelerated"), windowsName + " / D3D11")),
                new(AndroidStreamSettings.SoftwareVideoDecoder, string.Format(L10n.T("Phone.StreamDecoderSoftware"), windowsName + " / CPU")),
            };
            bool deviceLacksHevc = _streamEncoders?.Hevc is { Count: 0 };
            if (_windowsHevcAvailable == true && !deviceLacksHevc)
            {
                choices.Add(new(AndroidStreamSettings.WindowsHevcDecoder,
                    string.Format(L10n.T("Phone.StreamDecoderHevc"), "Microsoft HEVC Video Decoder MFT")));
            }
            else if (_streamSettings.VideoDecoder == AndroidStreamSettings.WindowsHevcDecoder)
            {
                string label = _windowsHevcAvailable == false ? L10n.T("Phone.StreamDecoderHevcWindowsUnavailable")
                    : L10n.T("Phone.StreamDecoderHevcAndroidUnavailable");
                choices.Add(new(AndroidStreamSettings.WindowsHevcDecoder, label));
            }
            _actions.ShowCombo(anchor, choices, _streamSettings.VideoDecoder ?? "", picked =>
                SaveStreaming(online, _streamSettings with { VideoDecoder = NullIfEmpty(picked.Key) }), fitChoices: true);
            return true;
        }
        if (id == "phone.stream.audiobr")
        {
            if (_streamSettings.AudioDecoder == AndroidStreamSettings.RawPcmDecoder) return true;
            var choices = BuildNumericChoices([64000, 96000, 128000, 192000, 256000, 320000], _streamSettings.AudioBitRate, 1000, " Kbps");
            _actions.ShowCombo(anchor, choices, _streamSettings.AudioBitRate.ToString(CultureInfo.InvariantCulture), picked =>
            {
                if (int.TryParse(picked.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                    SaveStreaming(online, _streamSettings with { AudioBitRate = val });
            });
            return true;
        }
        if (id == "phone.stream.audiolat")
        {
            var choices = new List<ComboChoice>
            {
                new(AndroidStreamSettings.LatencyModeUltraLow, L10n.T("Phone.StreamLatencyUltraLow")),
                new(AndroidStreamSettings.LatencyModeBalanced, L10n.T("Phone.StreamLatencyBalanced")),
                new(AndroidStreamSettings.LatencyModeStable, L10n.T("Phone.StreamLatencyStable")),
            };
            _actions.ShowCombo(anchor, choices, _streamSettings.AudioLatencyMode ?? AndroidStreamSettings.LatencyModeBalanced, picked =>
                SaveStreaming(online, _streamSettings with { AudioLatencyMode = picked.Key }), fitChoices: true);
            return true;
        }
        if (id == "phone.stream.videobr")
        {
            var choices = BuildNumericChoices([1000000, 4000000, 6000000, 8000000, 12000000, 16000000, 24000000, 32000000, 50000000],
                _streamSettings.VideoBitRate, 1000000, " Mbps");
            _actions.ShowCombo(anchor, choices, _streamSettings.VideoBitRate.ToString(CultureInfo.InvariantCulture), picked =>
            {
                if (int.TryParse(picked.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                    SaveStreaming(online, _streamSettings with { VideoBitRate = val });
            }, fitChoices: true);
            return true;
        }
        if (id == "phone.stream.videofps")
        {
            var choices = BuildNumericChoices([15, 24, 30, 60, 120], _streamSettings.MaxFps, 1, " fps");
            _actions.ShowCombo(anchor, choices, _streamSettings.MaxFps.ToString(CultureInfo.InvariantCulture), picked =>
            {
                if (int.TryParse(picked.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                    SaveStreaming(online, _streamSettings with { MaxFps = val });
            });
            return true;
        }
        return false;
    }

    private void SaveStreaming(AndroidDevice online, AndroidStreamSettings next)
    {
        if (!_active || IsConnectionPage || _actions.Host.Phone.Store.Profile.LastDevice is not { } current ||
            !AndroidDeviceIdentity.MatchesSavedDevice(current, online)) return;
        try
        {
            _actions.Host.Phone.Settings.RememberStreaming(online, next.Normalize());
            _streamSettings = _actions.Host.Phone.Settings.GetStreaming(online);
        }
        catch (Exception error) { SetStatus(PhonePreview.TranslateError(error), true); }
        _actions.Invalidate();
    }

    private void RetryEncodersIfNeeded(AndroidDevice online, bool forceRetry = false)
    {
        if (_activeDevice == null || !_actions.Host.Phone.Connections.IsCurrent(online)) return;
        if (forceRetry) _encoderFailed = false;
        if (_streamEncoders == null && _encoderCts == null && !_encoderFailed && _streamDeviceKey is { } key)
        {
            var phone = _actions.Host.Phone;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime, phone.Connections.OperationToken(online));
            _encoderCts = cts;
            _ = LoadStreamEncodersAsync(online, key, cts);
        }
    }

    private List<ComboChoice> BuildEncoderChoices(IReadOnlyList<string>? names, IReadOnlyDictionary<string, AndroidEncoderKind>? kinds, string? selected)
    {
        var choices = new List<ComboChoice> { new("", L10n.T("Phone.StreamAutomatic")) };
        var entries = (names ?? Array.Empty<string>())
            .Concat(selected == null ? Array.Empty<string>() : new[] { selected })
            .Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                var reported = kinds != null && kinds.TryGetValue(name, out var kind) ? kind : AndroidEncoderKind.Unknown;
                var (_, fallbackKind) = ClassifyEncoder(name);
                var effective = reported != AndroidEncoderKind.Unknown ? reported : fallbackKind;
                int rank = effective switch { AndroidEncoderKind.Hardware => 0, AndroidEncoderKind.Software => 1, _ => 2 };
                return (Name: name, Label: FormatEncoderLabels(name, reported).Full, Rank: rank);
            })
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Name, StringComparer.Ordinal);
        foreach (var item in entries) choices.Add(new(item.Name, item.Label));
        if (_streamEncoders == null && (_encoderCts != null || _encoderFailed))
            choices.Add(new("#status", L10n.T(_encoderFailed ? "Phone.StreamEncoderRetry" : "Phone.StreamEncoderLoading")));
        return choices;
    }

    private static List<ComboChoice> BuildNumericChoices(int[] presets, int selected, int divisor, string unit)
    {
        return presets.Append(selected).Distinct().Order().Select(value => new ComboChoice(
            value.ToString(CultureInfo.InvariantCulture),
            (value / (double)divisor).ToString("0.##", CultureInfo.InvariantCulture) + unit)).ToList();
    }

    private (string Text, string? Tooltip) GetEncoderSelectedLabel(string? selected)
    {
        if (string.IsNullOrEmpty(selected)) return (L10n.T("Phone.StreamAutomatic"), null);
        var kind = _streamEncoders?.Kinds != null && _streamEncoders.Kinds.TryGetValue(selected, out var k)
            ? k : AndroidEncoderKind.Unknown;
        var labels = FormatEncoderLabels(selected, kind);
        return labels.Compact != null ? (labels.Compact, labels.Full) : (labels.Full, null);
    }

    private static string GetAudioDecoderLabel(string? decoder) => decoder switch
    {
        AndroidStreamSettings.ConcentusDecoder => string.Format(L10n.T("Phone.StreamDecoderManaged"), "Concentus Opus"),
        AndroidStreamSettings.RawPcmDecoder => string.Format(L10n.T("Phone.StreamDecoderRawPcm"), "Raw PCM"),
        _ => L10n.T("Phone.StreamDecoderAutomatic")
    };

    private static string GetVideoDecoderLabel(string? decoder) => decoder switch
    {
        AndroidStreamSettings.WindowsVideoDecoder => string.Format(L10n.T("Phone.StreamDecoderAccelerated"), "Microsoft H.264 / D3D11"),
        AndroidStreamSettings.SoftwareVideoDecoder => string.Format(L10n.T("Phone.StreamDecoderSoftware"), "Microsoft H.264 / CPU"),
        AndroidStreamSettings.WindowsHevcDecoder => string.Format(L10n.T("Phone.StreamDecoderHevc"), "Microsoft HEVC"),
        _ => L10n.T("Phone.StreamDecoderAutomatic")
    };

    private static string GetLatencyLabel(string mode) => mode switch
    {
        AndroidStreamSettings.LatencyModeUltraLow => L10n.T("Phone.StreamLatencyUltraLow"),
        AndroidStreamSettings.LatencyModeStable => L10n.T("Phone.StreamLatencyStable"),
        _ => L10n.T("Phone.StreamLatencyBalanced")
    };

    private static (string? VendorKey, AndroidEncoderKind FallbackKind) ClassifyEncoder(string name)
    {
        if (Starts(name, "c2.qti.", "c2.qcom.", "omx.qcom.")) return ("Phone.StreamVendorQualcomm", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.mtk.", "omx.mtk.")) return ("Phone.StreamVendorMediaTek", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.exynos.", "omx.exynos.", "c2.samsung.", "omx.sec.")) return ("Phone.StreamVendorSamsung", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.hisi.", "omx.hisi.", "c2.kirin.")) return ("Phone.StreamVendorHiSilicon", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.unisoc.", "c2.sprd.", "omx.sprd.")) return ("Phone.StreamVendorUnisoc", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.rk.", "omx.rk.")) return ("Phone.StreamVendorRockchip", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.amlogic.", "omx.amlogic.")) return ("Phone.StreamVendorAmlogic", AndroidEncoderKind.Hardware);
        if (Starts(name, "c2.google.", "omx.google.")) return ("Phone.StreamVendorGoogle", AndroidEncoderKind.Software);
        if (Starts(name, "c2.android.", "omx.android.")) return ("Phone.StreamVendorAndroid", AndroidEncoderKind.Software);
        return (null, AndroidEncoderKind.Unknown);

        static bool Starts(string value, params string[] prefixes) =>
            prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static (string Full, string? Compact) FormatEncoderLabels(string name, AndroidEncoderKind reportedKind)
    {
        var (vendorKey, fallbackKind) = ClassifyEncoder(name);
        var effectiveKind = reportedKind != AndroidEncoderKind.Unknown ? reportedKind : fallbackKind;
        string kindText = effectiveKind switch
        {
            AndroidEncoderKind.Hardware => L10n.T("Phone.StreamEncoderHardware"),
            AndroidEncoderKind.Software => L10n.T("Phone.StreamEncoderSoftware"),
            _ => L10n.T("Phone.StreamEncoderCustom")
        };
        string? compact = null;
        if (name.Contains(".hevc", StringComparison.OrdinalIgnoreCase) || name.Contains(".h265", StringComparison.OrdinalIgnoreCase))
        {
            compact = name.EndsWith(".cq", StringComparison.OrdinalIgnoreCase) ? "H.265 CQ"
                : name.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase) ? "H.265 HDR" : null;
            string format = compact switch
            {
                "H.265 CQ" => L10n.T("Phone.StreamEncoderHevcCq"),
                "H.265 HDR" => L10n.T("Phone.StreamEncoderHevcHdr"),
                _ => "H.265"
            };
            kindText = format + " · " + kindText;
        }
        string? vendor = vendorKey != null && effectiveKind != AndroidEncoderKind.Unknown ? L10n.T(vendorKey) : null;
        string full = vendor != null
            ? string.Format(L10n.T("Phone.StreamEncoderVendorFormat"), vendor, kindText, name)
            : string.Format(L10n.T("Phone.StreamEncoderFormat"), kindText, name);
        return (full, compact != null && vendor != null ? $"{compact} · {vendor}" : compact);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private void CancelEncoderRead()
    {
        var old = _encoderCts;
        _encoderCts = null;
        if (old != null)
        {
            try { old.Cancel(); } catch { }
        }
    }

    // --- Busy & QR Pairing ---

    internal void RefreshFromUser()
    {
        if (!_active || _busy || (IsConnectionPage && _airPlayConnectionChoice)) return;
        if (_showCatalog && _activeDevice is { } catalogDevice)
        {
            LoadCatalog(catalogDevice, refresh: true);
            return;
        }
        var phone = _actions.Host.Phone;
        if (IsConnectionPage)
        {
            SearchAppCandidates();
            RunBusy(ct => phone.RefreshAsync().WaitAsync(ct), pauseWirelessPairing: false);
        }
        else
        {
            if (_activeDevice is { } online)
            {
                CancelEncoderRead();
                phone.Operations.InvalidateEncoders(online);
                _streamDeviceKey = null;
            }
            RunBusy(ct => phone.RefreshSelectedDeviceAsync(ct));
        }
    }

    private void ShowDeviceMenu(UiRect anchor, string profileId)
    {
        void Execute(string action)
        {
            if (!_active || IsConnectionPage || _actions.Host.Phone.Store.Profile.Id != profileId) return;
            RunDeviceCommand(action);
        }

        List<UiMenuItem> items =
        [
            new UiMenuItem { Text = L10n.T("Phone.Reconnect"), Icon = UiIcon.Refresh, Click = () => Execute("phone.connect") },
        ];
        AddAppDeviceTools(items, _actions.Host.Phone.Store.Profile, anchor);
        AddAppBindingMenu(items, _actions.Host.Phone.Store.Profile);
        if (_activeDevice?.IsApp != true || _actions.Host.Phone.Store.Profile.LastDevice?.IsApp == true)
            items.Add(new UiMenuItem { Text = L10n.T("Phone.Unpair"), Icon = UiIcon.Link, Click = () => Execute("phone.unpair") });
        items.Add(UiMenuItem.Separator());
        items.Add(new UiMenuItem { Text = L10n.T("Phone.DeleteDevice"), Icon = UiIcon.Delete, Click = () => Execute("phone.remove") });
        _actions.ShowMenu(anchor, items);
    }

    private void RunDeviceCommand(string action)
    {
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        if (_busy || profile.LastDevice == null) return;
        var online = _activeDevice;

        switch (action)
        {
            case "phone.connect" when profile.LastDevice.IsApp && profile.AppTrustRevoked:
                ShowConnectionPage();
                SetStatus(L10n.T("Phone.Srap.not_paired"), false);
                break;
            case "phone.connect":
                RunBusy(ct => phone.ReconnectSelectedDeviceAsync(ct));
                break;
            case "phone.disconnect" when online != null:
                RunBusy(_ => phone.DisconnectAsync(online, unpair: false), online.IsWifi ? "Phone.StatusWifiDisconnectedSaved" : "Phone.StatusUsbStoppedUnplug");
                break;
            case "phone.unpair":
                if (profile.LastDevice.IsApp)
                    RunBusy(_ => phone.DisconnectAsync(profile.LastDevice, unpair: true), "Phone.SrapUnpairedLocally");
                else if (online == null)
                    SetStatus(PhonePreview.TranslateError(AndroidError.WithCode(new IOException(), AndroidErrorCode.UnpairPhoneOffline)), true);
                else
                    RunBusy(_ => phone.DisconnectAsync(online, unpair: true), "Phone.StatusUnpairRequested");
                break;
            case "phone.remove":
                RunBusy(_ => phone.RemovePhoneAsync(profile.Id), "Phone.StatusRemovedKeepAuth");
                break;
        }
    }

    private void CancelViewOperation()
    {
        var previous = _operation;
        _operation = null;
        _busy = false;
        _pauseWirelessPairing = false;
        previous?.Cancel();
        ClearNearbyPairing();
        _statusMessage = null;
    }

    private void RunBusy(Func<CancellationToken, Task> op, string? successKey = null, bool openDevice = false, Action<Exception>? failed = null,
        int timeoutSeconds = 25, bool pauseWirelessPairing = true, Func<string>? completedText = null)
    {
        if (_busy || !_active) return;
        _busy = true;
        _pauseWirelessPairing = pauseWirelessPairing;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_actions.Host.Phone.Lifetime);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        _operation = cts;
        if (pauseWirelessPairing)
        {
            CancelCts(ref _searchSaved);
            CancelAppDiscovery();
            _manualSearch = false;
            _savedSearchStatus = null;
        }
        var pendingQr = pauseWirelessPairing ? _qrTask : Task.CompletedTask;
        UpdateQrPairing();
        SetStatus(L10n.T("Phone.ProcessingStatus"), false);
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await pendingQr.WaitAsync(cts.Token).ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();
                await op(cts.Token).ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; }
            if (!_actions.Host.TryPost(() =>
            {
                try
                {
                    if (!ReferenceEquals(_operation, cts)) return;
                    _operation = null;
                    _busy = false;
                    _pauseWirelessPairing = false;
                    if (!_active) return;
                    if (failure != null)
                    {
                        failed?.Invoke(failure);
                        SetStatus(failure is OperationCanceledException ? L10n.T("Phone.StatusCancelled") : PhonePreview.TranslateError(failure), true);
                    }
                    else
                    {
                        if (openDevice) CompleteDeviceConnection();
                        SetStatus(completedText?.Invoke() ?? (successKey == null ? "" : L10n.T(successKey)), false);
                    }
                    RefreshData();
                }
                finally { cts.Dispose(); }
            })) cts.Dispose();
        });
    }

    private void SetStatus(string msg, bool error)
    {
        _statusMessage = msg;
        _statusError = error;
        _actions.Invalidate();
    }

    private static string SavedLabel(AndroidSavedConnection saved) =>
        saved.Name == saved.Address ? saved.Address : saved.Name + " · " + saved.Address;

    private void ResetSavedSearch()
    {
        CancelCts(ref _searchSaved);
        _savedConnections = [];
        _savedChoice = null;
        _searchedSaved = _manualSearch = false;
        _savedSearchStatus = null;
    }

    private void SearchSaved(bool manual)
    {
        if (!CanShowQr || (!manual && _searchedSaved)) return;
        _searchedSaved = true;
        if (manual)
        {
            _manualSearch = true;
            _savedSearchStatus = "Phone.ProcessingStatus";
            _actions.Invalidate();
        }
        if (_searchSaved != null) return;
        var phone = _actions.Host.Phone;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
        var token = cts.Token;
        _searchSaved = cts;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<AndroidSavedConnection> found = [];
            try
            {
                var services = await phone.DiscoverSavedConnectionsAsync(token).ConfigureAwait(false);
                var candidates = phone.Store.FindWirelessConnectionCandidates(services, phone.Discovery.CurrentDevices ?? []);
                found = await AndroidWirelessConnection.VerifySavedConnectionsAsync(candidates, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { ExceptionDiagnostics.Record("NativePhone.SavedDeviceSearch", ex); }
            _actions.Host.TryPost(() =>
            {
                if (!ReferenceEquals(_searchSaved, cts)) return;
                _searchSaved = null;
                cts.Dispose();
                bool requested = _manualSearch;
                _manualSearch = false;
                _savedSearchStatus = requested && found.Count == 0 ? "Phone.SavedDevicesNotFound" : null;
                if (CanShowQr && (requested || (_connectAddress.Length == 0 && _actions.ActiveInlineEditTag != "phone.connaddr")))
                {
                    _savedConnections = found;
                    _savedChoice = found.FirstOrDefault(item => _savedChoice is { } old &&
                        AndroidDeviceIdentity.MatchesSavedDevice(item.Device, old.Device)) ?? found.FirstOrDefault();
                }
                if (_active) _actions.Invalidate();
            });
        });
    }

    private void UpdateQrPairing()
    {
        if (_active && IsConnectionPage && _airPlayConnectionChoice) _airPlay.Attach();
        else _airPlay.Detach();
        if (!AndroidConnectionVisible)
        {
            CancelAppDiscovery();
            _appCandidates = [];
            ClearNearbyPairing();
            _appSearchComplete = false;
            ResetSavedSearch();
        }
        else SearchSaved(manual: false);
        if (!CanShowQr)
        {
            StopQrPairing();
            return;
        }
        if (_qrCts != null) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_actions.Host.Phone.Lifetime);
        _qrCts = cts;
        _qrTask = WatchQrPairingAsync(cts);
    }

    private void StopQrPairing()
    {
        var old = _qrCts;
        _qrCts = null;
        if (old != null)
        {
            try { old.Cancel(); } catch { }
        }
        _qrBgra = null;
        _qrSizePx = 0;
        _qrRetry = false;
    }

    private async Task WatchQrPairingAsync(CancellationTokenSource cts)
    {
        await Task.Yield();
        CancellationToken token = cts.Token;
        var phone = _actions.Host.Phone;
        int failures = 0;

        try
        {
            while (CanShowQr && !token.IsCancellationRequested)
            {
                try
                {
                    _actions.Host.TryPost(() =>
                    {
                        if (ReferenceEquals(_qrCts, cts))
                        {
                            _qrRetry = false;
                            _actions.Invalidate();
                        }
                    });

                    var (qr, pixels, size) = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        var pair = AndroidQrPairing.Create();
                        using var data = QRCodeGenerator.GenerateQrCode(pair.Payload, QRCodeGenerator.ECCLevel.M);
                        var matrix = data.ModuleMatrix;
                        int n = matrix.Count;
                        int cell = Math.Max(2, 144 / Math.Max(1, n));
                        int pxSize = n * cell;
                        byte[] buf = new byte[pxSize * pxSize * 4];
                        for (int r = 0; r < n; r++)
                        {
                            for (int c = 0; c < n; c++)
                            {
                                byte v = matrix[r][c] ? (byte)0 : (byte)255;
                                for (int dy = 0; dy < cell; dy++)
                                {
                                    int py = r * cell + dy;
                                    for (int dx = 0; dx < cell; dx++)
                                    {
                                        int px = c * cell + dx;
                                        int idx = (py * pxSize + px) * 4;
                                        buf[idx] = v;
                                        buf[idx + 1] = v;
                                        buf[idx + 2] = v;
                                        buf[idx + 3] = 255;
                                    }
                                }
                            }
                        }
                        return (pair, buf, pxSize);
                    }, token).ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();
                    if (!CanShowQr) return;

                    _actions.Host.TryPost(() =>
                    {
                        if (ReferenceEquals(_qrCts, cts) && CanShowQr)
                        {
                            _qrBgra = pixels;
                            _qrSizePx = size;
                            _qrVersion++;
                            _actions.Invalidate();
                        }
                    });

                    await phone.PairAsync(qr, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    _actions.Host.TryPost(() =>
                    {
                        if (!ReferenceEquals(_qrCts, cts)) return;
                        _qrCts = null;
                        _qrBgra = null;
                        CloseCatalog();
                        CompleteDeviceConnection();
                        SetStatus(L10n.T("Phone.ConnectedStatus"), false);
                        RefreshData();
                    });
                    return;
                }
                catch (TimeoutException ex) when (!token.IsCancellationRequested
                    && AndroidError.TryGetCode(ex, out var code) && code == AndroidErrorCode.QrExpired)
                {
                    failures = 0;
                    _actions.Host.TryPost(() =>
                    {
                        if (ReferenceEquals(_qrCts, cts))
                        {
                            _qrBgra = null;
                            _actions.Invalidate();
                        }
                    });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Phone QR pairing retry: " + ex.GetType().Name);
                    failures = Math.Min(failures + 1, 4);
                    _actions.Host.TryPost(() =>
                    {
                        if (ReferenceEquals(_qrCts, cts))
                        {
                            _qrBgra = null;
                            _qrRetry = true;
                            _actions.Invalidate();
                        }
                    });
                    int delaySec = failures switch { 1 => 5, 2 => 15, 3 => 30, _ => 60 };
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _actions.Host.TryPost(() =>
            {
                if (ReferenceEquals(_qrCts, cts))
                {
                    _qrCts = null;
                    _qrBgra = null;
                    _qrSizePx = 0;
                    _actions.Invalidate();
                }
            });
            cts.Dispose();
        }
    }

    private void LoadCatalog(AndroidDevice device, bool refresh)
    {
        if (refresh) _appIcons.Clear();
        CancelCts(ref _catalogCts);
        var phone = _actions.Host.Phone;
        var cache = _catalogCache ??= new AndroidAppCatalogCache(Path.Combine(_actions.Host.DataDirectory, "phone-app-catalog.json"),
            (target, token) => phone.Operations.AppsAsync(target, token));
        var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime, phone.Connections.OperationToken(device));
        cts.CancelAfter(TimeSpan.FromSeconds(18));
        var token = cts.Token;
        _catalogCts = cts;
        _catalogLoading = true;
        SetStatus(L10n.T("Phone.ProcessingStatus"), false);

        _ = Task.Run(async () =>
        {
            IReadOnlyList<AndroidApp> apps = [];
            Exception? failure = null;
            try
            {
                apps = await cache.GetAsync(device, refresh, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (phone.Connections.IsCurrent(device)) phone.Store.UpdateMetadata(device, apps);
            }
            catch (Exception error) { failure = error; }
            if (!_actions.Host.TryPost(() =>
            {
                if (!ReferenceEquals(_catalogCts, cts)) return;
                _catalogCts = null;
                cts.Dispose();
                _catalogLoading = false;
                if (!_active || !_showCatalog) return;
                if (failure != null)
                {
                    SetStatus(failure is OperationCanceledException ? L10n.T("Phone.StatusCancelled") : PhonePreview.TranslateError(failure), true);
                    return;
                }
                if (!AndroidDeviceIdentity.SameTransport(_activeDevice, device) || !phone.Connections.IsCurrent(device)) { RefreshData(); return; }
                _catalogFetched = true;
                _catalogApps = apps;
                FilterCatalog();
                SetStatus("", false);
            })) cts.Dispose();
        });
    }

    private void LoadRemoteFolder(AndroidDevice device, string folder)
    {
        CancelRemoteList();
        var phone = _actions.Host.Phone;
        var deviceToken = phone.Connections.OperationToken(device);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime, deviceToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        _remoteListCts = cts;
        _remoteFolder = folder;
        _remoteFiles = [];
        _remoteLoading = true;
        _remoteError = null;
        _selectedRemoteFiles.Clear();
        _fileScroll.Offset = 0f;
        _actions.Invalidate();

        _ = Task.Run(async () =>
        {
            try
            {
                var list = await phone.Operations.OpenFiles(device).ListAsync(folder, cts.Token).ConfigureAwait(false);
                cts.Token.ThrowIfCancellationRequested();
                _actions.Host.TryPost(() =>
                {
                    if (!ReferenceEquals(_remoteListCts, cts) || !Equals(_activeDevice, device)) return;
                    _remoteFiles = _choosingPhoneFolder ? list.Where(entry => entry.Directory).ToArray() : list;
                    _remoteError = null;
                });
            }
            catch (Exception error)
            {
                _actions.Host.TryPost(() =>
                {
                    if (!ReferenceEquals(_remoteListCts, cts)) return;
                    _remoteError = error is OperationCanceledException
                        ? L10n.T(deviceToken.IsCancellationRequested || phone.Lifetime.IsCancellationRequested ? "Phone.StatusCancelled" : "Phone.LatencyTimeout")
                        : PhonePreview.TranslateError(error);
                });
            }
            finally
            {
                if (!_actions.Host.TryPost(() =>
                {
                    if (ReferenceEquals(_remoteListCts, cts))
                    {
                        _remoteListCts = null;
                        _remoteLoading = false;
                        if (_active) _actions.Invalidate();
                    }
                    cts.Dispose();
                })) cts.Dispose();
            }
        });
    }

    private static void CancelCts(ref CancellationTokenSource? cts)
    {
        var old = cts;
        cts = null;
        if (old != null)
        {
            try { old.Cancel(); } catch { }
            old.Dispose();
        }
    }
}
#endif
