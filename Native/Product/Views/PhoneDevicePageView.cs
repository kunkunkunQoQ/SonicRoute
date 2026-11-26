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
/// Full Phone &amp; AirPlay device management page implementing IViewPage.
/// Supports both Connected Phone Device view and Add Device / Connection view.
/// </summary>
internal sealed partial class PhoneDevicePageView : IViewPage
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

    internal bool IsConnectionPage => _showConnection || _actions.Host.Phone.Store.Profile.LastDevice == null;
    private bool CanShowQr => _active && !_busy && IsConnectionPage && !_airPlayConnectionChoice;
    private bool UseHevc => _streamSettings.VideoDecoder == AndroidStreamSettings.WindowsHevcDecoder;
    internal bool IsReorderingPinnedApp => _pinnedOrder.Pressed;
    public bool NeedsAnimationTick => _pinnedOrder.NeedsScrollTick(_scroll, _visibleDeviceRect);

    public void TickAnimation() => _pinnedOrder.TickScroll(_scroll, _visibleDeviceRect);

    internal PhoneDevicePageView(IViewHostActions actions)
    {
        _actions = actions;
        _appIcons = new PhoneAppIconCache(actions.Host, 96, actions.Invalidate);
        _pcReceiveFolder = new ReceiveDirectorySettings(Path.Combine(actions.Host.DataDirectory, "android-file-receive-folder.txt"));
        _airPlay = new AirPlayReceiverViewControl(actions, "phonepage.airplay");
    }

    internal void ShowConnectionPage()
    {
        FinishPinnedOrder(commit: false);
        CancelViewOperation();
        _actions.CloseInlineEdit(commit: false);
        CloseCatalog();
        _showConnection = true;
        _scroll.Offset = 0f;
        CancelEncoderRead();
        UpdateQrPairing();
        _actions.Invalidate();
    }

    internal void ShowProfile(string profileId)
    {
        FinishPinnedOrder(commit: false);
        CancelViewOperation();
        _actions.CloseInlineEdit(commit: false);
        CloseCatalog();
        _showConnection = false;
        _airPlayConnectionChoice = false;
        _actions.Host.Phone.Store.SelectProfile(profileId);
        _scroll.Offset = 0f;
        StopQrPairing();
        RefreshData();
    }

    public void OnActivated()
    {
        if (_active) return;
        _active = true;
        var phone = _actions.Host.Phone;
        phone.Changed += OnPhoneChanged;

        phone.StartTracking();

        UpdateQrPairing();
        if (phone.Discovery.CurrentDevices == null && (!IsConnectionPage || !_airPlayConnectionChoice))
            _ = phone.RefreshAsync();
        RefreshData();
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
        StopQrPairing();
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
        _scroll.EndDrag();
        if (changed) _actions.Invalidate();
    }

    public void OnFilesDropped(string[] paths, string? targetId)
    {
        if (targetId != "phone.sendfile" || !_active || _showCatalog || _showFileBrowser || IsConnectionPage || _activeDevice is not { } online || paths.Length == 0) return;
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        EnqueueOrStartFiles(online, profile.Id, paths, profile.ReceiveFolder, pullFrom: null);
    }

    public void RefreshData()
    {
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        if (_pinnedOrder.Pressed && _pinnedOrderProfile != profile.Id) FinishPinnedOrder(commit: false);
        _appIcons.SetProfile(profile.Id);
        var online = profile.LastDevice is { } last ? phone.Connections.Get(last).Device : null;
        if (!Equals(online, _activeDevice))
        {
            CancelEncoderRead();
            _streamDeviceKey = null;
            _activeDevice = online;
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
        RefreshStreamingSettings(IsConnectionPage ? null : online ?? profile.LastDevice);
        if (!_hevcLoadStarted && _active && !IsConnectionPage)
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
        string? status = IsConnectionPage && _airPlayConnectionChoice ? null : _statusMessage;
        if (!string.IsNullOrEmpty(status))
        {
            float usable = MathF.Max(1f, contentRect.Width - 8f);
            float height = PhoneTextHeight(status, usable);
            var footer = new UiRect(contentRect.X + 4f, contentRect.Bottom - 8f - height, usable, height);
            g.DrawText(status, footer, _statusError ? t.Fail : t.TextSecondary, 12f, wrap: true);
            contentRect = new UiRect(contentRect.X, contentRect.Y, contentRect.Width, MathF.Max(1f, contentRect.Height - height - 8f));
        }
        _visibleDeviceRect = contentRect;
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

    private float RenderConnectedView(D2dTarget g, UiThemePalette t, float x, float y, float w)
    {
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        var online = _activeDevice;

        var detail = profile.LastDevice;
        var devCard = new UiRect(x, y, w, 74f);
        UiPrimitives.DrawCard(g, t, devCard, 12f);
        var phoneIconBox = new UiRect(x + 13f, y + 13f, 38f, 48f);
        g.FillRoundedRect(phoneIconBox, 10f, t.Overlay);
        g.DrawIcon(UiIcon.Phone, new UiRect(phoneIconBox.CenterX - 11f, phoneIconBox.CenterY - 16f, 22f, 32f), t.TextSecondary, 1.5f);

        float right = devCard.Right - 13f;
        float maxCommandWidth = MathF.Max(29f, (w - 150f) / 4f);
        void DrawCommand(string id, string labelKey, bool enabled)
        {
            string label = L10n.T(labelKey);
            float width = MathF.Min(maxCommandWidth, PhoneTextWidth(label, 11f) + 18f);
            right -= width;
            UiPrimitives.DrawSecondaryButton(g, t, _hit, id, new UiRect(right, devCard.CenterY - 15f, width, 30f),
                label, enabled: enabled && !_busy, fontSize: 11f, tooltip: label);
            right -= 4f;
        }
        DrawCommand("phone.remove", "Phone.DeleteDevice", detail != null);
        DrawCommand("phone.unpair", "Phone.Unpair", detail != null);
        DrawCommand("phone.disconnect", online is { IsWifi: false } ? "Phone.StopReceiving" : "Phone.Disconnect", online != null);
        DrawCommand("phone.reconnect", "Phone.Reconnect", detail != null);

        string name = phone.Store.GetProfileDisplayName(profile);
        bool editing = _actions.ActiveInlineEditTag == "phone.rename.box";
        float nameX = x + 61f, available = MathF.Max(1f, right - nameX - 4f);
        float nameWidth = MathF.Min(MathF.Max(1f, available - 32f), editing ? available : PhoneTextWidth(name, 16f, 600));
        _renameRect = new UiRect(nameX, y + 15f, nameWidth, 26f);
        if (editing)
            UiPrimitives.DrawTextBox(g, t, _hit, "phone.rename.box", _renameRect, name, focused: true, fontSize: 16f);
        else
            g.DrawText(name, _renameRect, t.TextPrimary, 16f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.rename", new UiRect(_renameRect.Right + 6f, _renameRect.Y, 26f, 26f),
            "", icon: UiIcon.Edit, tooltip: L10n.T("Phone.RenamePhoneAutomation"));
        string connection = online == null ? L10n.T("Phone.Disconnected")
            : $"{name} · {(online.IsWifi ? "Wi-Fi" : "USB")} · {L10n.T("Phone.ConnectedStatus")}";
        g.DrawText(connection, new UiRect(nameX, _renameRect.Bottom + 2f, available, 18f), t.TextSecondary, 11f);
        y = devCard.Bottom + 10f;

        // Card 3: Streaming Codec / FPS / Bitrate Card (matching WPF PhoneDevicePage.Streaming.cs)
        y = RenderStreamingCard(g, t, x, y, w, detail != null);

        // Card 4: App Library Card
        var apps = profile.RetainedApps;
        string pinHint = L10n.T("Phone.PinDesc");
        float pinHintH = PhoneTextHeight(pinHint, w - 34f);
        float libHeaderH = 10f + 24f + 6f + pinHintH + 10f;
        const float libRowH = 44f;
        float libBodyH = apps.Count == 0 ? 42f : apps.Count * libRowH;
        var libCard = new UiRect(x, y, w, libHeaderH + libBodyH + 10f);
        UiPrimitives.DrawCard(g, t, libCard, 12f);
        float lx = libCard.X + 12f;
        float lw = libCard.Width - 24f;
        float ly = libCard.Y + 12f;

        g.DrawText(L10n.T("Phone.QuickPanelAppsTitle"), new UiRect(lx + 4f, ly, lw - 100f, 24f), t.TextPrimary, 17f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(string.Format(L10n.T("Phone.RetainedCountFormat"), apps.Count), new UiRect(libCard.Right - 128f, ly, 112f, 24f), t.TextSecondary, 12f, 400, UiTextAlign.Trailing);
        g.DrawText(pinHint, new UiRect(lx + 4f, ly + 30f, lw - 8f, pinHintH), t.TextSecondary, 12f, wrap: true);
        ly = libCard.Y + libHeaderH;

        if (apps.Count == 0)
        {
            g.DrawText(L10n.T("Phone.RetainedEmptyHint"), new UiRect(lx, ly, lw, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
        else
        {
            for (int i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                var rowRect = new UiRect(lx, ly + i * libRowH, lw, libRowH);
                if (apps.Count > 1) _pinnedOrder.AddRow(app.Package, rowRect, _scroll.Offset);
                if (rowRect.Bottom < _visibleDeviceRect.Y || rowRect.Y >= _visibleDeviceRect.Bottom) continue;
                bool dragged = _pinnedOrder.IsSource(app.Package);
                if (dragged)
                {
                    var selected = rowRect.Deflate(0f, 2f);
                    g.FillRoundedRect(selected, 8f, t.NavSelected.WithAlphaFraction(0.55f));
                    g.DrawRoundedRect(selected, 8f, t.Accent, 1.5f);
                }
                _pinnedOrder.DrawInsertionLine(g, t, app.Package, rowRect, gap: 0f);
                bool smart = phone.Store.IsSmartVolumeEnabled(profile.LastDevice, app.Package);

                var iconRect = new UiRect(rowRect.X, rowRect.CenterY - 16f, 32f, 32f);
                if (_appIcons.TryGet(app, out var ibgra, out int iw, out int ih, out int iver))
                {
                    g.DrawBgraBitmap($"phonepage.icon:{app.Package}", ibgra, iw, ih, iconRect, version: iver);
                }
                else
                {
                    g.DrawText(string.IsNullOrEmpty(app.Name) ? "♪" : StringInfo.GetNextTextElement(app.Name), iconRect, t.TextPrimary, 18f, 400, UiTextAlign.Center, UiVertAlign.Center);
                }

                if (apps.Count > 1)
                {
                    string grip = PinnedAppGrip + app.Package;
                    _hit.Add(grip, iconRect, UiCursorKind.Hand, L10n.T("Phone.DragAppOrder"));
                    if (dragged || _hit.IsHover(grip))
                    {
                        var gripBadge = new UiRect(iconRect.Right - 11f, iconRect.Bottom - 13f, 14f, 14f);
                        g.FillRoundedRect(gripBadge, 4f, t.SurfaceBg);
                        g.DrawIcon(UiIcon.Grip, gripBadge.Deflate(2f), t.TextPrimary);
                    }
                }

                var swRect = new UiRect(rowRect.Right - 36f, rowRect.CenterY - 12f, 34f, 24f);
                UiPrimitives.DrawSwitch(g, t, _hit, $"phone.smart:{app.Package}", swRect, smart);

                var pinRect = new UiRect(swRect.X - 36f, rowRect.CenterY - 14f, 28f, 28f);
                DrawAppPin(g, t, pinRect, app, retained: true);

                float tx = iconRect.Right + 10f;
                float tw = MathF.Max(20f, pinRect.X - 8f - tx);
                g.DrawText(app.Name, new UiRect(tx, rowRect.Y + 5f, tw, 18f), t.TextPrimary, 12.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                g.DrawText(PhoneAppPresentation.Description(app), new UiRect(tx, rowRect.Y + 24f, tw, 15f), t.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }
        }

        var addRect = new UiRect(x, libCard.Bottom + 10f, w, 38f);
        DrawOverlayAction(g, t, "phone.addapps", addRect, L10n.T("Phone.AddQuickPanelAppButton"), enabled: online != null && !_busy);
        y = addRect.Bottom + 12f;

        // Card 5: File Transfer Card
        y = RenderFileTransferCard(g, t, x, y, w, phone, profile, online);

        // Card 6: Clipboard Sync Card
        var clipCard = new UiRect(x, y, w, 64f);
        UiPrimitives.DrawCard(g, t, clipCard, 12f);
        bool clipEnabled = phone.Clipboard.IsEnabled(detail);
        string clipDesc = phone.Clipboard.StatusFor(detail);
        g.DrawText(L10n.T("Phone.ClipboardSyncTitle"), new UiRect(clipCard.X + 12f, clipCard.Y + 10f, clipCard.Width - 70f, 20f),
            t.TextPrimary, 13.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(clipDesc, new UiRect(clipCard.X + 12f, clipCard.Y + 32f, clipCard.Width - 70f, 20f),
            t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSwitch(g, t, _hit, "phone.clipboard", new UiRect(clipCard.Right - 48f, clipCard.CenterY - 12f, 36f, 24f),
            clipEnabled, enabled: detail != null);

        return clipCard.Bottom + 12f;
    }

    private float RenderStreamingCard(D2dTarget g, UiThemePalette t, float x, float y, float w, bool enabled)
    {
        const float cardH = 88f;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card, 12f);

        float cx = card.X + 12f;
        float cw = card.Width - 24f;
        float cy = card.Y + 13f;
        float labelW = MathF.Max(PhoneTextWidth(L10n.T("Phone.StreamAudioLabel"), 11f), PhoneTextWidth(L10n.T("Phone.StreamVideoLabel"), 11f)) + 8f;
        const float gap = 8f;
        float lastW = MathF.Min(70f, cw * 0.18f), bitrateW = MathF.Min(78f, cw * 0.2f);
        float colW = MathF.Max(1f, (cw - labelW - bitrateW - lastW - 3 * gap) / 2f);
        float col0 = cx + labelW, bitrateX = col0 + (colW + gap) * 2f, lastX = bitrateX + bitrateW + gap;
        const float rowH = 28f;

        bool opusMode = _streamSettings.AudioDecoder != AndroidStreamSettings.RawPcmDecoder;
        string audioEncLabel = GetEncoderSelectedLabel(_streamSettings.AudioEncoder, _streamEncoders?.Audio);
        string audioDecLabel = GetAudioDecoderLabel(_streamSettings.AudioDecoder);
        string audioBrLabel = (_streamSettings.AudioBitRate / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Kbps";
        string audioLatLabel = GetLatencyLabel(_streamSettings.AudioLatencyMode ?? AndroidStreamSettings.LatencyModeBalanced);

        g.DrawText(L10n.T("Phone.StreamAudioLabel"), new UiRect(cx, cy, labelW, rowH), t.TextPrimary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.audioenc", new UiRect(col0, cy, colW, rowH), audioEncLabel, fontSize: 11f, enabled: enabled && opusMode);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.audiodec", new UiRect(col0 + (colW + gap), cy, colW, rowH), audioDecLabel, fontSize: 11f, enabled: enabled);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.audiobr", new UiRect(bitrateX, cy, bitrateW, rowH), audioBrLabel, fontSize: 11f, enabled: enabled && opusMode);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.audiolat", new UiRect(lastX, cy, lastW, rowH), audioLatLabel, fontSize: 11f, enabled: enabled);
        cy += rowH + 6f;

        string? selectedVideoEnc = UseHevc ? _streamSettings.HevcEncoder : _streamSettings.VideoEncoder;
        var videoEncList = UseHevc ? _streamEncoders?.Hevc : _streamEncoders?.Video;
        string videoEncLabel = GetEncoderSelectedLabel(selectedVideoEnc, videoEncList);
        string videoDecLabel = GetVideoDecoderLabel(_streamSettings.VideoDecoder);
        string videoBrLabel = (_streamSettings.VideoBitRate / 1000000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps";
        string videoFpsLabel = _streamSettings.MaxFps.ToString(CultureInfo.InvariantCulture) + " fps";

        g.DrawText(L10n.T("Phone.StreamVideoLabel"), new UiRect(cx, cy, labelW, rowH), t.TextPrimary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.videoenc", new UiRect(col0, cy, colW, rowH), videoEncLabel, fontSize: 11f, enabled: enabled);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.videodec", new UiRect(col0 + (colW + gap), cy, colW, rowH), videoDecLabel, fontSize: 11f, enabled: enabled);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.videobr", new UiRect(bitrateX, cy, bitrateW, rowH), videoBrLabel, fontSize: 11f, enabled: enabled);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.videofps", new UiRect(lastX, cy, lastW, rowH), videoFpsLabel, fontSize: 11f, enabled: enabled);

        return card.Bottom + 10f;
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
            UiPrimitives.DrawCard(g, t, fileCard, 12f);

            float fx = fileCard.X + 12f;
            float fw = fileCard.Width - 24f;
            float fy = fileCard.Y + 12f;

            bool canUp = _remoteFolder != AndroidFileTransfer.Storage && !_remoteLoading;
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

            return fileCard.Bottom + 10f;
        }

        _fileBrowserListRect = default;
        bool hasStatus = transfer != null || queuedCount > 0;
        float fileCardH = 232f + (hasStatus ? 30f : 0f);
        var card = new UiRect(x, y, w, fileCardH);
        UiPrimitives.DrawCard(g, t, card, 12f);

        float x0 = card.X + 12f;
        float cw = card.Width - 24f;
        float y0 = card.Y + 12f;

        string browse = L10n.T("Phone.AndroidBrowseFiles");
        float browseW = MathF.Min(cw * 0.48f, PhoneTextWidth(browse, 12f) + 42f);
        g.DrawText(L10n.T("Phone.Files"), new UiRect(x0, y0, cw - browseW - 6f, 30f), t.TextPrimary, 14f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.browsefiles", new UiRect(x0 + cw - browseW, y0, browseW, 30f),
            browse, enabled: online != null, fontSize: 12f, icon: UiIcon.Download);
        y0 += 36f;

        var dropRect = new UiRect(x0, y0 + 4f, cw, 104f);
        bool dropEnabled = online != null;
        if (dropEnabled) _hit.Add("phone.sendfile", dropRect, UiCursorKind.Hand, L10n.T("Phone.FileSend"));
        bool dropHot = dropEnabled && _hit.IsHover("phone.sendfile");
        g.FillRoundedRect(dropRect, 8f, dropEnabled ? (dropHot ? t.NavHover : t.Overlay) : t.WindowBg);
        if (dropHot) g.DrawRoundedRect(dropRect, 8f, t.Accent, 1f);
        string dropText = online != null ? L10n.T("Phone.FileDropHint") : L10n.T("Phone.FileOfflineHint");
        var dropInk = dropEnabled ? t.TextPrimary : t.TextSecondary;
        float hintHeight = PhoneTextHeight(dropText, cw - 24f);
        float iconTop = dropRect.CenterY - (24f + 8f + hintHeight) / 2f;
        g.DrawText("\uE898", new UiRect(x0, iconTop, cw, 24f), dropInk, 22f, 400, UiTextAlign.Center, UiVertAlign.Center,
            fontFamily: "Segoe MDL2 Assets");
        g.DrawText(dropText, new UiRect(x0 + 12f, iconTop + 32f, cw - 24f, hintHeight), dropInk,
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
        DrawFolder(L10n.T("Phone.AndroidReceiveFolder"), AndroidFolderLabel(profile.ReceiveFolder), "phone.remotefolder", online != null);
        y0 += 31f;
        DrawFolder(L10n.T("Phone.FileReceiveFolder"), _pcReceiveFolder.GetReceiveDirectory(), "phone.localfolder", true);
        return card.Bottom + 10f;
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

        if (_airPlay.HandleClick(id, hit.Value.Bounds)) return true;

        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        var online = _activeDevice;

        if (id == "phone.connchoice.android")
        {
            if (_airPlayConnectionChoice)
            {
                _actions.CloseInlineEdit(commit: false);
                _airPlayConnectionChoice = false;
                UpdateQrPairing();
                _actions.Invalidate();
            }
            return true;
        }
        if (id == "phone.connchoice.airplay")
        {
            if (!_airPlayConnectionChoice)
            {
                _actions.CloseInlineEdit(commit: false);
                _airPlayConnectionChoice = true;
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
            _actions.StartInlineEdit("phone.pairaddr", hit.Value.Bounds, _pairAddress, t => _pairAddress = t, (t, _) =>
            {
                _pairAddress = t.Trim();
                _actions.Invalidate();
            });
            return true;
        }
        if (id == "phone.paircode")
        {
            _actions.StartInlineEdit("phone.paircode", hit.Value.Bounds, _pairCode, t => _pairCode = t, (t, _) =>
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
        if (id == "phone.reconnect" && profile.LastDevice != null)
        {
            RunBusy(ct => phone.ReconnectSelectedDeviceAsync(ct));
            return true;
        }
        if (id == "phone.disconnect" && online != null)
        {
            RunBusy(_ => phone.DisconnectAsync(online, unpair: false), online.IsWifi ? "Phone.StatusWifiDisconnectedSaved" : "Phone.StatusUsbStoppedUnplug");
            return true;
        }
        if (id == "phone.unpair" && profile.LastDevice != null)
        {
            if (online == null)
                SetStatus(PhonePreview.TranslateError(AndroidError.WithCode(new IOException(), AndroidErrorCode.UnpairPhoneOffline)), true);
            else
                RunBusy(_ => phone.DisconnectAsync(online, unpair: true), "Phone.StatusUnpairRequested");
            return true;
        }
        if (id == "phone.remove" && profile.LastDevice != null)
        {
            RunBusy(_ => phone.RemovePhoneAsync(profile.Id), "Phone.StatusRemovedKeepAuth");
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
                EnqueueOrStartFiles(online, profile.Id, paths, profile.ReceiveFolder, pullFrom: null);
            }
            return true;
        }
        if (id == "phone.browsefiles" && online != null)
        {
            OpenAndroidPicker(online, profile.ReceiveFolder, chooseFolder: false);
            return true;
        }
        if (id == "phone.remotefolder" && online != null)
        {
            OpenAndroidPicker(online, profile.ReceiveFolder, chooseFolder: true);
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
        if (id == "phone.folderup" && online != null && _remoteFolder != AndroidFileTransfer.Storage)
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
        _actions.StartInlineEdit("phone.connaddr", box, _connectAddress, value => _connectAddress = value,
            (value, _) => { _connectAddress = value.Trim(); _actions.Invalidate(); });

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
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
            if (vk == 0x08 && _activeDevice is { } online && _remoteFolder != AndroidFileTransfer.Storage && !_remoteLoading) // Backspace
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
            _actions.Host.Phone.Store.SetReceiveFolder(profileId, _remoteFolder);
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

    private static string AndroidFolderLabel(string path)
    {
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
            var encoders = await Task.Run(() => _actions.Host.Phone.Adb.EncodersAsync(device, token), token).ConfigureAwait(false);
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
            });
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
            });
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
                SaveStreaming(online, _streamSettings with { AudioDecoder = NullIfEmpty(picked.Key) }));
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
                SaveStreaming(online, _streamSettings with { VideoDecoder = NullIfEmpty(picked.Key) }));
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
                SaveStreaming(online, _streamSettings with { AudioLatencyMode = picked.Key }));
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
            });
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
                return (Name: name, Label: FormatEncoderLabel(name, reported), Rank: rank);
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

    private string GetEncoderSelectedLabel(string? selected, IReadOnlyList<string>? names)
    {
        if (string.IsNullOrEmpty(selected)) return L10n.T("Phone.StreamAutomatic");
        var kind = _streamEncoders?.Kinds != null && _streamEncoders.Kinds.TryGetValue(selected, out var k)
            ? k : AndroidEncoderKind.Unknown;
        return FormatEncoderLabel(selected, kind);
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

    private static string FormatEncoderLabel(string name, AndroidEncoderKind reportedKind)
    {
        var (vendorKey, fallbackKind) = ClassifyEncoder(name);
        var effectiveKind = reportedKind != AndroidEncoderKind.Unknown ? reportedKind : fallbackKind;
        string kindText = effectiveKind switch
        {
            AndroidEncoderKind.Hardware => L10n.T("Phone.StreamEncoderHardware"),
            AndroidEncoderKind.Software => L10n.T("Phone.StreamEncoderSoftware"),
            _ => L10n.T("Phone.StreamEncoderCustom")
        };
        if (name.Contains(".hevc", StringComparison.OrdinalIgnoreCase) || name.Contains(".h265", StringComparison.OrdinalIgnoreCase))
        {
            string format = name.EndsWith(".cq", StringComparison.OrdinalIgnoreCase) ? L10n.T("Phone.StreamEncoderHevcCq")
                : name.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase) ? L10n.T("Phone.StreamEncoderHevcHdr") : "H.265";
            kindText = format + " · " + kindText;
        }
        return vendorKey != null && effectiveKind != AndroidEncoderKind.Unknown
            ? string.Format(L10n.T("Phone.StreamEncoderVendorFormat"), L10n.T(vendorKey), kindText, name)
            : string.Format(L10n.T("Phone.StreamEncoderFormat"), kindText, name);
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
            RunBusy(ct => phone.RefreshAsync().WaitAsync(ct));
        else
        {
            if (_activeDevice is { } online)
            {
                CancelEncoderRead();
                phone.Adb.InvalidateEncoders(online);
                _streamDeviceKey = null;
            }
            RunBusy(ct => phone.RefreshSelectedDeviceAsync(ct));
        }
    }

    private void CancelViewOperation()
    {
        var previous = _operation;
        _operation = null;
        _busy = false;
        previous?.Cancel();
        _statusMessage = null;
    }

    private void RunBusy(Func<CancellationToken, Task> op, string? successKey = null, bool openDevice = false, Action<Exception>? failed = null)
    {
        if (_busy || !_active) return;
        _busy = true;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_actions.Host.Phone.Lifetime);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        _operation = cts;
        CancelCts(ref _searchSaved);
        _manualSearch = false;
        _savedSearchStatus = null;
        var pendingQr = _qrTask;
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
                    if (!_active) return;
                    if (failure != null)
                    {
                        failed?.Invoke(failure);
                        SetStatus(failure is OperationCanceledException ? L10n.T("Phone.StatusCancelled") : PhonePreview.TranslateError(failure), true);
                    }
                    else
                    {
                        if (openDevice) _showConnection = false;
                        SetStatus(successKey == null ? "" : L10n.T(successKey), false);
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
                var services = await AndroidMdnsDiscovery.DiscoverConnectionsAsync(token).ConfigureAwait(false);
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
        if (!_active || !IsConnectionPage || _airPlayConnectionChoice) ResetSavedSearch();
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
                        _showConnection = false;
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
        CancelCts(ref _catalogCts);
        var phone = _actions.Host.Phone;
        var cache = _catalogCache ??= new AndroidAppCatalogCache(Path.Combine(_actions.Host.DataDirectory, "phone-app-catalog.json"),
            (target, token) => phone.Adb.AppsAsync(target, token));
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
                var list = await new AndroidFileTransfer(phone.Adb, device.CommandTarget).ListAsync(folder, cts.Token).ConfigureAwait(false);
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
