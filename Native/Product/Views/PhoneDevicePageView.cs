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
    private readonly IViewHostActions _actions;
    private readonly UiScrollState _scroll = new();
    private readonly UiScrollState _fileScroll = new();
    private readonly ReceiveDirectorySettings _pcReceiveFolder;
    private UiHitMap _hit = new();
    private readonly AirPlayReceiverViewControl _airPlay;

    private readonly Dictionary<string, (byte[] Pixels, int W, int H, int Ver)> _iconCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _iconOrder = new();
    private readonly HashSet<string> _iconDecoding = new(StringComparer.Ordinal);
    private int _iconVer;

    private bool _active;
    private bool _showConnection;
    private bool _airPlayConnectionChoice;
    private bool _busy;
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
    private AndroidMediaSessionClient? _mediaClient;
    private AndroidMediaState? _mediaState;
    private CancellationTokenSource? _mediaCts;
    private CancellationTokenSource? _catalogCts;
    private IReadOnlyList<AndroidApp> _catalogApps = Array.Empty<AndroidApp>();
    private string _appSearch = "";

    // Inline edit & slider drag state
    private UiRect _renameRect;
    private bool _draggingVolume;
    private UiRect _volumeSliderRect;

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
    private UiRect _fileDropRect;

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
    public bool NeedsAnimationTick => false;

    internal PhoneDevicePageView(IViewHostActions actions)
    {
        _actions = actions;
        _pcReceiveFolder = new ReceiveDirectorySettings(Path.Combine(actions.Host.DataDirectory, "android-file-receive-folder.txt"));
        _airPlay = new AirPlayReceiverViewControl(actions, "phonepage.airplay");
    }

    internal void ShowConnectionPage()
    {
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
        _airPlay.Attach();

        if (!_hevcLoadStarted)
        {
            _hevcLoadStarted = true;
            _ = LoadWindowsHevcAsync();
        }

        UpdateQrPairing();
        _ = phone.RefreshAsync(restore: true);
        RefreshData();
    }

    public void OnDeactivated()
    {
        if (!_active) return;
        _active = false;
        _actions.CloseInlineEdit(commit: false);
        OnCaptureLost();
        var phone = _actions.Host.Phone;
        phone.Changed -= OnPhoneChanged;

        _airPlay.Detach();
        StopQrPairing();
        ResetSavedSearch();
        StopMediaWatch();
        CancelEncoderRead();
        CancelRemoteList();
        CloseCatalog();
    }

    public void OnCaptureLost()
    {
        bool changed = _draggingVolume || _fileScroll.Dragging || _scroll.Dragging;
        _draggingVolume = false;
        _fileScroll.EndDrag();
        _scroll.EndDrag();
        if (changed) _actions.Invalidate();
    }

    public void OnFilesDropped(string[] paths)
    {
        if (!_active || IsConnectionPage || _activeDevice is not { } online || paths.Length == 0) return;
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        EnqueueOrStartFiles(online, profile.Id, paths, profile.ReceiveFolder, pullFrom: null);
    }

    public void RefreshData()
    {
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        var online = profile.LastDevice is { } last ? phone.Connections.Get(last).Device : null;
        if (!Equals(online, _activeDevice))
        {
            _activeDevice = online;
            _showFileBrowser = false;
            _remoteFiles = Array.Empty<AndroidStorageFile>();
            _selectedRemoteFiles.Clear();
            _remoteError = null;
            _catalogApps = Array.Empty<AndroidApp>();
            CancelRemoteList();
            RestartMediaWatch(online);
            CloseCatalog();
        }
        var cached = phone.Discovery.CurrentDevices ?? [];
        _restorableUsb = IsConnectionPage ? cached.Where(device => !device.IsWifi && device.State == "device" &&
            phone.Connections.Resolve(device) == null && !AndroidConnectionManager.IsAmbiguous(device, cached)).ToArray() : [];
        RefreshStreamingSettings(online);
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
        _visibleDeviceRect = contentRect;
        if (_showCatalog)
        {
            RenderCatalog(g, t, contentRect);
            return;
        }
        _scroll.ViewportHeight = contentRect.Height;

        float viewW = MathF.Max(200f, contentRect.Width - UiMetrics.ScrollBarWidth - 6f);
        float startY = contentRect.Y - _scroll.Offset;

        g.PushClip(contentRect);
        _hit.PushClip(contentRect);
        float endY = IsConnectionPage
            ? RenderConnectionView(g, t, contentRect.X, startY, viewW)
            : RenderConnectedView(g, t, contentRect.X, startY, viewW);
        _hit.PopClip();
        g.PopClip();

        _scroll.ContentHeight = MathF.Max(0f, endY - startY);
        _scroll.Clamp();
        _scroll.Draw(g, t, _hit, "phone.scroll", contentRect);
    }

    private float RenderConnectedView(D2dTarget g, UiThemePalette t, float x, float y, float w)
    {
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        var profilesWithDevices = phone.Store.Profiles.Where(p => p.LastDevice != null).ToArray();
        var online = _activeDevice;

        // Top profile switcher bar + Add Device button
        float barX = x;
        foreach (var p in profilesWithDevices)
        {
            string label = phone.Store.GetProfileDisplayName(p);
            float bw = Math.Clamp(D2dSharedContext.Instance.MeasureText(label, UiMetrics.FontFamily, 12f, 600, 180f, 30f, false).Width + 24f, 70f, 180f);
            if (barX + bw > x + w - 110f) break;
            UiPrimitives.DrawSecondaryButton(g, t, _hit, $"phone.prof:{p.Id}", new UiRect(barX, y, bw, 30f), label,
                fontSize: 12f, active: p.Id == profile.Id);
            barX += bw + 6f;
        }
        UiPrimitives.DrawAccentButton(g, t, _hit, "phone.add", new UiRect(x + w - 108f, y, 108f, 30f), L10n.T("Phone.AddDeviceButton"), fontSize: 11.5f);
        y += 40f;

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            var statusSize = D2dSharedContext.Instance.MeasureText(_statusMessage, UiMetrics.FontFamily, 12f, 400, w - 8f, 96f, wrap: true);
            float msgH = MathF.Max(20f, statusSize.Height + 2f);
            g.DrawText(_statusMessage, new UiRect(x + 4f, y, w - 8f, msgH), _statusError ? t.Fail : t.TextSecondary,
                12f, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
            y += msgH + 4f;
        }

        // Card 1: Device Header Card
        var devCard = new UiRect(x, y, w, 76f);
        UiPrimitives.DrawCard(g, t, devCard, 12f);

        var phoneIconBox = new UiRect(devCard.X + 12f, devCard.CenterY - 24f, 38f, 48f);
        g.FillRoundedRect(phoneIconBox, 10f, t.Overlay);
        g.DrawIcon(UiIcon.Phone, new UiRect(phoneIconBox.CenterX - 11f, phoneIconBox.CenterY - 15f, 22f, 30f),
            online != null ? t.Accent : t.TextSecondary, 1.6f);

        // Action buttons on right
        float btnRight = devCard.Right - 12f;
        float btnY = devCard.CenterY - 15f;
        var removeRect = new UiRect(btnRight - 72f, btnY, 72f, 30f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.remove", removeRect, L10n.T("Phone.DeleteDevice"), enabled: !_busy && profile.LastDevice != null, fontSize: 11f, danger: true);
        btnRight = removeRect.X - 6f;

        if (online != null)
        {
            if (online.IsWifi)
            {
                var unpairRect = new UiRect(btnRight - 72f, btnY, 72f, 30f);
                UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.unpair", unpairRect, L10n.T("Phone.Unpair"), enabled: !_busy, fontSize: 11f);
                btnRight = unpairRect.X - 6f;
            }
            var discRect = new UiRect(btnRight - 76f, btnY, 76f, 30f);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.disconnect", discRect,
                online.IsWifi ? L10n.T("Phone.Disconnect") : L10n.T("Phone.StopReceiving"), enabled: !_busy, fontSize: 11f);
            btnRight = discRect.X - 6f;
        }
        else
        {
            var recRect = new UiRect(btnRight - 76f, btnY, 76f, 30f);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.reconnect", recRect, L10n.T("Phone.Reconnect"), enabled: !_busy && profile.LastDevice != null, fontSize: 11f);
            btnRight = recRect.X - 6f;
        }

        float nameX = phoneIconBox.Right + 12f;
        float nameMaxW = MathF.Max(60f, btnRight - nameX - 36f);
        string devName = phone.Store.GetProfileDisplayName(profile);
        _renameRect = new UiRect(nameX, devCard.Y + 14f, nameMaxW, 24f);
        if (_actions.ActiveInlineEditTag != "phone.rename")
        {
            g.DrawText(devName, _renameRect, t.TextPrimary, 16f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        }
        else
        {
            UiPrimitives.DrawTextBox(g, t, _hit, "phone.rename.box", _renameRect, devName, focused: true, fontSize: 15f);
        }
        UiPrimitives.DrawGhostIconButton(g, t, _hit, "phone.rename", new UiRect(_renameRect.Right + 4f, devCard.Y + 13f, 26f, 26f), UiIcon.Edit, 14f,
            tooltip: L10n.T("Phone.RenamePhoneAutomation"));

        string statusLine = online != null
            ? $"{(online.IsWifi ? L10n.T("Phone.WifiConnected") : L10n.T("Phone.UsbConnected"))} · {online.Serial}"
            : $"{L10n.T("Phone.Disconnected")} · {profile.LastDevice?.Serial ?? ""}";
        g.DrawText(statusLine, new UiRect(nameX, devCard.Y + 42f, MathF.Max(60f, btnRight - nameX - 8f), 18f),
            online != null ? t.Success : t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        y = devCard.Bottom + 10f;

        // Card 2: Audio & Media Card
        var wholeSource = online == null ? null : phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, online));
        float audioCardH = wholeSource != null ? 136f : 96f;
        var audioCard = new UiRect(x, y, w, audioCardH);
        UiPrimitives.DrawCard(g, t, audioCard, 12f);

        float ax = audioCard.X + 12f;
        float aw = audioCard.Width - 24f;
        float ay = audioCard.Y + 12f;

        bool recActive = wholeSource != null;
        var recBtnRect = new UiRect(ax, ay, 148f, 32f);
        if (recActive)
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.receive", recBtnRect, L10n.T("Phone.StopReceiving"), enabled: online != null, fontSize: 12f, radius: 7f, icon: UiIcon.Speaker, active: true);
        else
            UiPrimitives.DrawAccentButton(g, t, _hit, "phone.receive", recBtnRect, L10n.T("Phone.AndroidReceiveToPc"), enabled: online != null, fontSize: 12f, radius: 7f, icon: UiIcon.Speaker);

        var bothBtnRect = new UiRect(recBtnRect.Right + 8f, ay, 150f, 32f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.receiveboth", bothBtnRect, L10n.T("Phone.DropBothPlayTitle"),
            enabled: online != null, fontSize: 11.5f, active: wholeSource?.KeepPhoneAudio == true);

        float mBtnW = 32f;
        float mx = audioCard.Right - 12f - mBtnW * 3f - 8f;
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.mediaprev", new UiRect(mx, ay, mBtnW, 32f), "", enabled: _mediaState?.CanPrevious == true, icon: UiIcon.PreviousTrack);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.mediaplay", new UiRect(mx + mBtnW + 4f, ay, mBtnW, 32f), "", enabled: _mediaState?.CanToggle == true, icon: _mediaState?.Playing == true ? UiIcon.Pause : UiIcon.Play);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.medianext", new UiRect(mx + (mBtnW + 4f) * 2f, ay, mBtnW, 32f), "", enabled: _mediaState?.CanNext == true, icon: UiIcon.NextTrack);

        string nowPlaying = _mediaState is { HasSession: true } ms
            ? $"{(ms.Title.Length > 0 ? ms.Title : L10n.T("Phone.MediaDefaultTitle"))}{(ms.Artist.Length > 0 ? " · " + ms.Artist : "")}"
            : L10n.T("Phone.MediaSubtitleIdle");
        g.DrawText(nowPlaying, new UiRect(ax, ay + 40f, aw, 20f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        if (wholeSource != null)
        {
            var st = wholeSource.Session.State;
            float vy = ay + 68f;
            var vmute = new UiRect(ax, vy, 54f, 28f);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.audiomute", vmute, "", icon: st.Muted ? UiIcon.VolumeMutedSlash : UiIcon.Speaker, active: st.Muted);
            var vpct = new UiRect(ax + aw - 44f, vy, 44f, 28f);
            g.DrawText($"{st.Volume}%", vpct, t.TextPrimary, 12.5f, 600, UiTextAlign.Trailing, UiVertAlign.Center);
            var vslider = new UiRect(vmute.Right + 8f, vy, MathF.Max(40f, vpct.X - 8f - (vmute.Right + 8f)), 28f);
            UiPrimitives.DrawVolumeSlider(g, t, _hit, "phone.audiovol", vslider, st.Volume / 100f, 0f, st.Muted, false);
        }

        y = audioCard.Bottom + 10f;

        // Card 3: Streaming Codec / FPS / Bitrate Card (matching WPF PhoneDevicePage.Streaming.cs)
        y = RenderStreamingCard(g, t, x, y, w, online != null);

        // Card 4: App Library Card
        var apps = profile.RetainedApps;
        float libHeaderH = 70f;
        float libRowH = 48f;
        float libBodyH = apps.Count == 0 ? 40f : apps.Count * libRowH;
        var libCard = new UiRect(x, y, w, libHeaderH + libBodyH + 12f);
        UiPrimitives.DrawCard(g, t, libCard, 12f);

        float lx = libCard.X + 12f;
        float lw = libCard.Width - 24f;
        float ly = libCard.Y + 12f;

        g.DrawText(L10n.T("Phone.QuickPanelAppsTitle"), new UiRect(lx, ly, lw - 100f, 24f), t.TextPrimary, 15f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(string.Format(L10n.T("Phone.RetainedCountFormat"), apps.Count), new UiRect(libCard.Right - 124f, ly, 112f, 24f), t.TextSecondary, 11f, 400, UiTextAlign.Trailing);
        g.DrawText(L10n.T("Phone.PinDesc"), new UiRect(lx, ly + 28f, lw, 32f), t.TextSecondary, 11.5f, wrap: true);
        ly += 58f;

        if (apps.Count == 0)
        {
            g.DrawText(L10n.T("Phone.RetainedEmptyHint"), new UiRect(lx, ly, lw, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Center, UiVertAlign.Center);
        }
        else
        {
            for (int i = 0; i < apps.Count; i++)
            {
                var app = apps[i];
                var rowRect = new UiRect(lx, ly + i * libRowH, lw, libRowH);
                if (rowRect.Bottom < _visibleDeviceRect.Y || rowRect.Y >= _visibleDeviceRect.Bottom) continue;
                const bool retained = true;
                bool smart = phone.Store.IsSmartVolumeEnabled(profile.LastDevice, app.Package);
                bool hasDisplayAudio = online != null && phone.Sources.Sources.Any(s => s.App.Package == app.Package && AndroidDeviceIdentity.Matches(s.Device, online));

                var iconRect = new UiRect(rowRect.X, rowRect.CenterY - 16f, 32f, 32f);
                if (TryGetAppIcon(app, out var ibgra, out int iw, out int ih, out int iver))
                {
                    g.DrawBgraBitmap($"phonepage.icon:{app.Package}", ibgra, iw, ih, iconRect, version: iver);
                }
                else
                {
                    g.FillRoundedRect(iconRect, 8f, t.Overlay);
                    g.DrawText(string.IsNullOrEmpty(app.Name) ? "♪" : app.Name[..1], iconRect, t.Accent, 14f, 600, UiTextAlign.Center, UiVertAlign.Center);
                }

                var swRect = new UiRect(rowRect.Right - 36f, rowRect.CenterY - 12f, 34f, 24f);
                UiPrimitives.DrawSwitch(g, t, _hit, $"phone.smart:{app.Package}", swRect, smart);

                var pinRect = new UiRect(swRect.X - 34f, rowRect.CenterY - 14f, 28f, 28f);
                UiPrimitives.DrawGhostIconButton(g, t, _hit, $"phone.pin:{app.Package}", pinRect, UiIcon.Pin, 14f,
                    iconColor: retained ? t.Accent : t.TextSecondary, active: retained,
                    tooltip: retained ? L10n.T("Phone.RemoveFromQuickPanel") : L10n.T("Phone.AddToQuickPanel"));

                var dispRect = new UiRect(pinRect.X - 34f, rowRect.CenterY - 14f, 28f, 28f);
                UiPrimitives.DrawGhostIconButton(g, t, _hit, $"phone.launch:{app.Package}", dispRect, UiIcon.Monitor, 14f,
                    enabled: online != null, active: hasDisplayAudio, tooltip: L10n.T("Phone.DisplayRestoreScreen"));

                float tx = iconRect.Right + 10f;
                float tw = MathF.Max(20f, dispRect.X - 8f - tx);
                g.DrawText(app.Name, new UiRect(tx, rowRect.Y + 5f, tw, 18f), t.TextPrimary, 12.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                g.DrawText(app.Package, new UiRect(tx, rowRect.Y + 24f, tw, 15f), t.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }
        }

        var addRect = new UiRect(x, libCard.Bottom, w, 38f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.addapps", addRect, L10n.T("Phone.AddQuickPanelAppButton"), enabled: online != null);
        y = addRect.Bottom + 12f;

        // Card 5: File Transfer Card
        y = RenderFileTransferCard(g, t, x, y, w, phone, profile, online);

        // Card 6: Clipboard Sync Card
        var clipCard = new UiRect(x, y, w, 64f);
        UiPrimitives.DrawCard(g, t, clipCard, 12f);
        bool clipEnabled = online != null ? phone.Clipboard.IsEnabled(online) : phone.Clipboard.Enabled;
        string clipDesc = online != null
            ? (clipEnabled ? phone.Clipboard.StatusFor(online) : L10n.T("Phone.ClipboardDefaultHint"))
            : L10n.T("Phone.ClipboardWaitingPhone");
        g.DrawText(L10n.T("Phone.ClipboardSyncTitle"), new UiRect(clipCard.X + 12f, clipCard.Y + 10f, clipCard.Width - 70f, 20f),
            t.TextPrimary, 13.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(clipDesc, new UiRect(clipCard.X + 12f, clipCard.Y + 32f, clipCard.Width - 70f, 20f),
            t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSwitch(g, t, _hit, "phone.clipboard", new UiRect(clipCard.Right - 48f, clipCard.CenterY - 12f, 36f, 24f),
            clipEnabled, enabled: online != null);

        return clipCard.Bottom + 12f;
    }

    private float RenderStreamingCard(D2dTarget g, UiThemePalette t, float x, float y, float w, bool enabled)
    {
        float cardH = 104f;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card, 12f);

        float cx = card.X + 12f;
        float cw = card.Width - 24f;
        float cy = card.Y + 10f;

        g.DrawText(L10n.T("Phone.StreamApplyHint"), new UiRect(cx, cy, cw, 16f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        cy += 22f;

        float labelW = 46f;
        float gap = 6f;
        float gridW = MathF.Max(120f, cw - labelW - gap);
        float colW = (gridW - gap * 3f) / 4f;
        float col0 = cx + labelW + gap;
        float rowH = 28f;

        bool opusMode = _streamSettings.AudioDecoder != AndroidStreamSettings.RawPcmDecoder;
        string audioEncLabel = GetEncoderSelectedLabel(_streamSettings.AudioEncoder, _streamEncoders?.Audio);
        string audioDecLabel = GetAudioDecoderLabel(_streamSettings.AudioDecoder);
        string audioBrLabel = (_streamSettings.AudioBitRate / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Kbps";
        string audioLatLabel = GetLatencyLabel(_streamSettings.AudioLatencyMode ?? AndroidStreamSettings.LatencyModeBalanced);

        g.DrawText(L10n.T("Phone.StreamAudioLabel"), new UiRect(cx, cy, labelW, rowH), t.TextPrimary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.audioenc", new UiRect(col0, cy, colW, rowH), audioEncLabel, fontSize: 11f, enabled: enabled && opusMode);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.audiodec", new UiRect(col0 + (colW + gap), cy, colW, rowH), audioDecLabel, fontSize: 11f, enabled: enabled);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.audiobr", new UiRect(col0 + (colW + gap) * 2f, cy, colW, rowH), audioBrLabel, fontSize: 11f, enabled: enabled && opusMode);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.audiolat", new UiRect(col0 + (colW + gap) * 3f, cy, colW, rowH), audioLatLabel, fontSize: 11f, enabled: enabled);
        cy += rowH + 8f;

        string? selectedVideoEnc = UseHevc ? _streamSettings.HevcEncoder : _streamSettings.VideoEncoder;
        var videoEncList = UseHevc ? _streamEncoders?.Hevc : _streamEncoders?.Video;
        string videoEncLabel = GetEncoderSelectedLabel(selectedVideoEnc, videoEncList);
        string videoDecLabel = GetVideoDecoderLabel(_streamSettings.VideoDecoder);
        string videoBrLabel = (_streamSettings.VideoBitRate / 1000000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps";
        string videoFpsLabel = _streamSettings.MaxFps.ToString(CultureInfo.InvariantCulture) + " fps";

        g.DrawText(L10n.T("Phone.StreamVideoLabel"), new UiRect(cx, cy, labelW, rowH), t.TextPrimary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.videoenc", new UiRect(col0, cy, colW, rowH), videoEncLabel, fontSize: 11f, enabled: enabled);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.videodec", new UiRect(col0 + (colW + gap), cy, colW, rowH), videoDecLabel, fontSize: 11f, enabled: enabled);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.videobr", new UiRect(col0 + (colW + gap) * 2f, cy, colW, rowH), videoBrLabel, fontSize: 11f, enabled: enabled);
        UiPrimitives.DrawComboBox(g, t, _hit, "phone.stream.videofps", new UiRect(col0 + (colW + gap) * 3f, cy, colW, rowH), videoFpsLabel, fontSize: 11f, enabled: enabled);

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
        var entries = _choosingPhoneFolder
            ? _remoteFiles.Where(f => f.Directory).ToArray()
            : _remoteFiles.ToArray();

        if (_showFileBrowser)
        {
            float listViewH = 196f;
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
            const float rowH = 28f;
            _fileScroll.ViewportHeight = inner.Height;
            _fileScroll.ContentHeight = entries.Length * rowH;
            _fileScroll.Clamp();

            if (_remoteLoading)
            {
                g.DrawText(L10n.T("Phone.ProcessingStatus"), inner, t.TextSecondary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
            }
            else if (!string.IsNullOrEmpty(_remoteError))
            {
                g.DrawText(_remoteError, inner, t.Fail, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center, wrap: true);
            }
            else if (entries.Length == 0)
            {
                g.DrawText(L10n.T(_choosingPhoneFolder ? "Phone.AndroidNoSubfolders" : "Phone.AndroidEmptyFolder"),
                    inner, t.TextSecondary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
            }
            else
            {
                float rowW = _fileScroll.MaxOffset > 0.5f ? inner.Width - UiMetrics.ScrollBarWidth - 2f : inner.Width;
                g.PushClip(inner);
                _hit.PushClip(inner);
                for (int i = 0; i < entries.Length; i++)
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

                    float textLeft = rowRect.X + 8f;
                    if (!f.Directory)
                    {
                        var cbBox = new UiRect(rowRect.X + 8f, rowRect.CenterY - 7.5f, 15f, 15f);
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
                        textLeft = cbBox.Right + 8f;
                    }

                    float rightReserved = f.Directory ? 8f : 76f;
                    g.DrawText((f.Directory ? "▸ " : "") + f.Label,
                        new UiRect(textLeft, rowRect.Y, MathF.Max(20f, rowRect.Right - rightReserved - textLeft), rowRect.Height),
                        t.TextPrimary, 12f, f.Directory ? 600 : 400, UiTextAlign.Leading, UiVertAlign.Center);
                    if (!f.Directory)
                        g.DrawText(f.SizeText, new UiRect(rowRect.Right - 72f, rowRect.Y, 64f, rowRect.Height),
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
            bool actionEnabled = !_remoteLoading && online != null && (_choosingPhoneFolder || selCount > 0);
            UiPrimitives.DrawAccentButton(g, t, _hit, "phone.rfileact", new UiRect(fileCard.Right - 180f, fy, 168f, 30f),
                actionText, enabled: actionEnabled, fontSize: 11.5f);

            return fileCard.Bottom + 10f;
        }

        _fileBrowserListRect = default;
        bool hasStatus = transfer != null || queuedCount > 0;
        float fileCardH = 196f + (hasStatus ? 30f : 0f);
        var card = new UiRect(x, y, w, fileCardH);
        UiPrimitives.DrawCard(g, t, card, 12f);

        float x0 = card.X + 12f;
        float cw = card.Width - 24f;
        float y0 = card.Y + 12f;

        g.DrawText(L10n.T("Phone.Files"), new UiRect(x0, y0, cw - 160f, 28f), t.TextPrimary, 14f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.browsefiles", new UiRect(card.Right - 164f, y0, 152f, 28f),
            L10n.T("Phone.AndroidBrowseFiles"), enabled: online != null, fontSize: 11.5f);
        y0 += 36f;

        _fileDropRect = new UiRect(x0, y0, cw, 64f);
        bool dropEnabled = online != null;
        if (dropEnabled) _hit.Add("phone.sendfile", _fileDropRect, UiCursorKind.Hand, L10n.T("Phone.FileSend"));
        bool dropHot = dropEnabled && _hit.IsHover("phone.sendfile");
        g.FillRoundedRect(_fileDropRect, 9f, dropEnabled ? (dropHot ? t.NavHover : t.Overlay) : t.WindowBg);
        g.DrawRoundedRect(_fileDropRect, 9f, dropHot ? t.Accent : t.Border, 1f);
        string dropText = online != null ? L10n.T("Phone.FileDropHint") : L10n.T("Phone.FileOfflineHint");
        g.DrawText(dropText, _fileDropRect.Deflate(10f, 6f), dropEnabled ? t.TextPrimary : t.TextSecondary,
            12f, 500, UiTextAlign.Center, UiVertAlign.Center, wrap: true);
        y0 = _fileDropRect.Bottom + 10f;

        // Android receive folder (pushed files destination on phone)
        string androidDirLabel = AndroidFolderLabel(profile.ReceiveFolder);
        g.DrawText($"{L10n.T("Phone.AndroidReceiveFolder")}: {androidDirLabel}", new UiRect(x0, y0, cw - 92f, 22f),
            t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.remotefolder", new UiRect(card.Right - 98f, y0 - 1f, 86f, 24f),
            L10n.T("Phone.FileChangeFolder"), enabled: online != null, fontSize: 10.5f);
        y0 += 26f;

        // PC receive folder (pulled files destination on PC)
        string pcDir = _pcReceiveFolder.GetReceiveDirectory();
        g.DrawText($"{L10n.T("Phone.FileReceiveFolder")}: {pcDir}", new UiRect(x0, y0, cw - 92f, 22f),
            t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.localfolder", new UiRect(card.Right - 98f, y0 - 1f, 86f, 24f),
            L10n.T("Phone.FileChangeFolder"), fontSize: 10.5f);
        y0 += 28f;

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

        return card.Bottom + 10f;
    }

    private float RenderConnectionView(D2dTarget g, UiThemePalette t, float x, float y, float w)
    {
        var phone = _actions.Host.Phone;
        if (phone.Store.Profile.LastDevice != null)
        {
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.back", new UiRect(x, y, 110f, 30f), L10n.T("Phone.DoneButton"), fontSize: 12f);
            y += 38f;
        }

        // Connection kind selector: Android vs AirPlay
        UiPrimitives.DrawRadioButton(g, t, _hit, "phone.connchoice.android", new UiRect(x, y, 110f, 26f), !_airPlayConnectionChoice, "Android", fontSize: 12.5f);
        UiPrimitives.DrawRadioButton(g, t, _hit, "phone.connchoice.airplay", new UiRect(x + 116f, y, 110f, 26f), _airPlayConnectionChoice, "AirPlay", fontSize: 12.5f);
        y += 32f;

        if (_airPlayConnectionChoice)
        {
            float airInnerW = w - 24f;
            float airH = _airPlay.MeasureHeight(airInnerW, showHeaderStatus: true);
            var airCard = new UiRect(x, y, w, airH + 24f);
            UiPrimitives.DrawCard(g, t, airCard, 12f);
            _airPlay.Render(g, t, _hit, airCard.Deflate(12f), showHeaderStatus: true);
            return airCard.Bottom + 12f;
        }

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            var statusSize = D2dSharedContext.Instance.MeasureText(_statusMessage, UiMetrics.FontFamily, 12f, 400, w - 8f, 96f, wrap: true);
            float msgH = MathF.Max(20f, statusSize.Height + 2f);
            g.DrawText(_statusMessage, new UiRect(x + 4f, y, w - 8f, msgH), _statusError ? t.Fail : t.TextSecondary,
                12f, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
            y += msgH + 4f;
        }

        // Card 1: QR Pairing + Pairing Code side-by-side
        var pairCard = new UiRect(x, y, w, 260f);
        UiPrimitives.DrawCard(g, t, pairCard, 12f);

        float colW = (pairCard.Width - 36f) * 0.5f;
        float qx = pairCard.X + 12f;
        float qy = pairCard.Y + 12f;
        g.DrawText(L10n.T("Phone.QrPairTitle"), new UiRect(qx, qy, colW, 20f), t.TextPrimary, 14f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(L10n.T("Phone.QrPairSteps"), new UiRect(qx, qy + 22f, colW, 18f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        var qrBox = new UiRect(qx + (colW - 154f) * 0.5f, qy + 48f, 154f, 154f);
        _hit.Add("phone.qrrefresh", qrBox, UiCursorKind.Hand, L10n.T("Phone.QrRefreshHint"));
        g.FillRoundedRect(qrBox, 8f, UiColor.White);
        if (_qrBgra != null && _qrSizePx > 0)
        {
            g.DrawBgraBitmap("phone.qr", _qrBgra, _qrSizePx, _qrSizePx, qrBox.Deflate(6f), version: _qrVersion);
        }
        else
        {
            g.DrawText(L10n.T("Phone.ProcessingStatus"), qrBox, UiColor.FromRgb(0x68, 0x70, 0x80), 12f, 400, UiTextAlign.Center, UiVertAlign.Center);
        }
        string qrHint = L10n.T(_qrRetry ? "Phone.QrRetryHint" : "Phone.QrRefreshHint");
        g.DrawText(qrHint, new UiRect(qx, qrBox.Bottom + 6f, colW, 24f), t.TextSecondary, 10.5f, 400, UiTextAlign.Center, UiVertAlign.Center, wrap: true);

        // Right column: Pair with code
        float cx = qx + colW + 16f;
        g.DrawText(L10n.T("Phone.CodePairTitle"), new UiRect(cx, qy, colW, 20f), t.TextPrimary, 14f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(L10n.T("Phone.CodePairSteps"), new UiRect(cx, qy + 22f, colW, 18f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        g.DrawText(L10n.T("Phone.PairingAddressLabel"), new UiRect(cx, qy + 48f, colW, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawTextBox(g, t, _hit, "phone.pairaddr", new UiRect(cx, qy + 68f, colW, 30f), _pairAddress, "192.168.1.100:37099",
            focused: _actions.ActiveInlineEditTag == "phone.pairaddr", mono: true, fontSize: 12f, enabled: !_busy);

        g.DrawText(L10n.T("Phone.PairingCodeLabel"), new UiRect(cx, qy + 106f, colW, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawTextBox(g, t, _hit, "phone.paircode", new UiRect(cx, qy + 126f, colW, 32f), _pairCode, "123456",
            focused: _actions.ActiveInlineEditTag == "phone.paircode", mono: true, fontSize: 15f, enabled: !_busy);

        UiPrimitives.DrawAccentButton(g, t, _hit, "phone.pairbtn", new UiRect(cx, qy + 170f, colW, 32f), L10n.T("Phone.PairAndConnect"),
            enabled: !_busy && _pairAddress.Length > 0 && _pairCode.Length > 0, fontSize: 12f);

        y = pairCard.Bottom + 10f;

        // Card 2: Direct Connect + USB Refresh
        var directCard = new UiRect(x, y, w, 140f + 32f * _restorableUsb.Length + (_savedSearchStatus == null ? 0f : 34f));
        UiPrimitives.DrawCard(g, t, directCard, 12f);
        float dx = directCard.X + 12f;
        float dw = directCard.Width - 24f;
        float dy = directCard.Y + 12f;

        g.DrawText(L10n.T("Phone.DirectConnectTitle"), new UiRect(dx, dy, dw, 20f), t.TextPrimary, 13.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(L10n.T("Phone.DirectConnectDesc"), new UiRect(dx, dy + 22f, dw, 20f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        var connBox = new UiRect(dx, dy + 48f, MathF.Max(100f, dw - 196f), 32f);
        if (_savedChoice == null)
            UiPrimitives.DrawTextBox(g, t, _hit, "phone.connaddr", connBox, _connectAddress, "192.168.1.100:5555",
                focused: _actions.ActiveInlineEditTag == "phone.connaddr", mono: true, fontSize: 12f, enabled: !_busy);
        else
            UiPrimitives.DrawComboBox(g, t, _hit, "phone.saved", connBox, SavedLabel(_savedChoice), enabled: !_busy);
        UiPrimitives.DrawAccentButton(g, t, _hit, "phone.connbtn", new UiRect(connBox.Right + 8f, dy + 48f, 88f, 32f), L10n.T("Phone.ConnectShort"),
            enabled: !_busy && (_savedChoice != null || _connectAddress.Length > 0), fontSize: 12f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.searchsaved", new UiRect(connBox.Right + 104f, dy + 48f, 92f, 32f),
            L10n.T("Phone.SearchSavedDevices"), enabled: !_busy && !_manualSearch, fontSize: 12f, icon: UiIcon.Search);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.usbrefresh", new UiRect(dx, dy + 88f, 120f, 28f),
            L10n.T("History.Refresh"), enabled: !_busy, fontSize: 12f, icon: UiIcon.Refresh);
        float usbY = dy + 120f;
        foreach (var device in _restorableUsb)
        {
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.restoreusb:" + AndroidDeviceIdentity.Key(device),
                new UiRect(dx, usbY, dw, 28f), string.Format(L10n.T("Phone.RestoreUsbDevice"), device.Name), enabled: !_busy);
            usbY += 32f;
        }
        if (_savedSearchStatus != null)
            g.DrawText(L10n.T(_savedSearchStatus), new UiRect(dx, usbY, dw, 34f), t.TextSecondary, 11f, wrap: true);

        return directCard.Bottom + 12f;
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (leftDown && _draggingVolume && _activeDevice != null)
        {
            var whole = _actions.Host.Phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, _activeDevice));
            if (whole != null)
            {
                whole.Session.SetVolume(UiPrimitives.SliderValueFromX(_volumeSliderRect, x));
                _actions.Invalidate();
                return true;
            }
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
        if (targetId == "phone.audiovol" && hit is { } h && _activeDevice != null)
        {
            var whole = _actions.Host.Phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, _activeDevice));
            if (whole != null)
            {
                _draggingVolume = true;
                _volumeSliderRect = h.Bounds;
                whole.Session.SetVolume(UiPrimitives.SliderValueFromX(h.Bounds, e.X));
                _actions.Invalidate();
                return true;
            }
        }
        _actions.Invalidate();
        return hit != null;
    }

    public bool OnPointerUp(UiPointerEventArgs e, string? clickId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (_draggingVolume)
        {
            _draggingVolume = false;
            _actions.Invalidate();
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

        if (id == "phone.add") { ShowConnectionPage(); return true; }
        if (id == "phone.back") { _actions.CloseInlineEdit(commit: false); _showConnection = false; StopQrPairing(); RefreshData(); return true; }
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
        if (id.StartsWith("phone.prof:", StringComparison.Ordinal)) { ShowProfile(id[11..]); return true; }

        if (id == "phone.rename" || id == "phone.rename.box")
        {
            var boxRect = _renameRect.IsEmpty ? hit.Value.Bounds : _renameRect;
            void Rename(string text)
            {
                if (phone.Store.Profile.Id == profile.Id) phone.Store.Rename(text);
            }
            _actions.StartInlineEdit("phone.rename", boxRect, phone.Store.GetProfileDisplayName(profile), Rename, (text, _) =>
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
            }, mono: true);
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
            _actions.StartInlineEdit("phone.connaddr", hit.Value.Bounds, _connectAddress, t => _connectAddress = t, (t, _) =>
            {
                _connectAddress = t.Trim();
                _actions.Invalidate();
            }, mono: true);
            return true;
        }

        if (id == "phone.pairbtn")
        {
            _actions.CloseInlineEdit(commit: true);
            string address = _pairAddress.Trim(), code = _pairCode.Trim();
            _pairCode = "";
            RunBusy(ct => phone.PairAsync(address, code, ct), "Phone.ConnectedStatus");
            return true;
        }
        if (id == "phone.searchsaved") { SearchSaved(manual: true); return true; }
        if (id == "phone.saved" && !_busy)
        {
            var choices = _savedConnections.Select(saved => new ComboChoice(saved.Address, SavedLabel(saved))).ToList();
            choices.Add(new("", L10n.T("Phone.ManualConnectAddress")));
            _actions.ShowCombo(hit.Value.Bounds, choices, _savedChoice?.Address ?? "", pick =>
            {
                _savedChoice = _savedConnections.FirstOrDefault(saved => saved.Address == pick.Key);
                _actions.Invalidate();
            });
            return true;
        }
        if (id == "phone.connbtn")
        {
            _actions.CloseInlineEdit(commit: true);
            var saved = _savedChoice;
            string address = _connectAddress.Trim();
            RunBusy(ct => phone.DiscoverAndConnectAsync(address, ct, saved), "Phone.ConnectedStatus");
            return true;
        }
        const string restoreUsb = "phone.restoreusb:";
        if (id.StartsWith(restoreUsb, StringComparison.Ordinal))
        {
            var chosen = _restorableUsb.FirstOrDefault(d => AndroidDeviceIdentity.Key(d) == id[restoreUsb.Length..]);
            if (chosen != null) RunBusy(ct => phone.RestoreUsbAsync(chosen, ct), "Phone.ConnectedStatus");
            return true;
        }
        if (id == "phone.usbrefresh")
        {
            RunBusy(_ => phone.RefreshAsync(restore: true));
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
        if (id == "phone.unpair" && online != null)
        {
            if (NativeDialogs.Confirm(_actions.OwnerWindow.Handle, L10n.T("Phone.DefaultName"), L10n.T("Phone.Unpair")))
            {
                RunBusy(_ => phone.DisconnectAsync(online, unpair: true), "Phone.StatusUnpairRequested");
            }
            return true;
        }
        if (id == "phone.remove" && profile.LastDevice != null)
        {
            if (NativeDialogs.Confirm(_actions.OwnerWindow.Handle, L10n.T("Phone.DefaultName"), L10n.T("Phone.DeleteDevice")))
            {
                phone.ClearQueuedTransfers(profile.Id);
                RunBusy(_ => phone.RemovePhoneAsync(profile.Id), "Phone.StatusRemovedKeepAuth");
            }
            return true;
        }
        if (id == "phone.receive" && online != null)
        {
            var whole = phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, online));
            if (whole != null) _ = phone.Sources.RemoveAsync(whole.Id);
            else _ = phone.Sources.AddAsync(online, AndroidApp.DeviceAudio, keepPhoneAudio: false);
            return true;
        }
        if (id == "phone.receiveboth" && online != null)
        {
            _ = phone.Sources.AddAsync(online, AndroidApp.DeviceAudio, keepPhoneAudio: true);
            return true;
        }
        if (id == "phone.audiomute" && online != null)
        {
            var whole = phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, online));
            if (whole != null) { whole.Session.SetMute(!whole.Session.State.Muted); _actions.Invalidate(); }
            return true;
        }
        if (id == "phone.mediaprev" && _mediaClient != null) { _ = _mediaClient.SendAsync("previous", phone.Lifetime); return true; }
        if (id == "phone.mediaplay" && _mediaClient != null) { _ = _mediaClient.SendAsync("toggle", phone.Lifetime); return true; }
        if (id == "phone.medianext" && _mediaClient != null) { _ = _mediaClient.SendAsync("next", phone.Lifetime); return true; }
        if (id == "phone.addapps" && online != null)
        {
            _showCatalog = true;
            _detailOffset = _scroll.Offset;
            _scroll.Offset = 0f;
            _appSearch = "";
            FilterCatalog();
            LoadCatalog(online);
            _actions.Invalidate();
            return true;
        }
        if (id == "phone.catalog.done") { CloseCatalog(); _actions.Invalidate(); return true; }
        if (id.StartsWith(CatalogRow, StringComparison.Ordinal) && _showCatalog)
        {
            SelectCatalogRow(id[CatalogRow.Length..], e.Ctrl, e.Shift);
            return true;
        }

        if (id.StartsWith("phone.stream.", StringComparison.Ordinal) && online != null)
        {
            return HandleStreamComboClick(id, hit.Value.Bounds, online);
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
        if (id.StartsWith("phone.launch:", StringComparison.Ordinal) && online != null)
        {
            string pkg = id[13..];
            var app = profile.RetainedApps.Concat(_catalogApps).FirstOrDefault(a => a.Package == pkg);
            if (app != null)
            {
                ViewNative.GetCursorPos(out var pt);
                _ = phone.OpenDisplayAsync(online, app, pt.X, pt.Y);
            }
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
        if ((id == "phone.clipboard" || id == "phone.clipboard:sw") && online != null)
        {
            bool next = !phone.Clipboard.IsEnabled(online);
            phone.Clipboard.SetEnabled(online, next);
            _actions.Invalidate();
            return true;
        }

        return false;
    }

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
        _actions.Invalidate();
        return true;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
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
        if (_remoteLoading) return;
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
        if (key == _streamDeviceKey && (device == null || _encoderCts != null || _streamEncoders != null || _encoderFailed))
        {
            return;
        }

        CancelEncoderRead();
        _streamDeviceKey = key;
        _streamEncoders = null;
        _encoderFailed = false;
        var phone = _actions.Host.Phone;
        _streamSettings = device == null ? new() : phone.Settings.GetStreaming(device);

        if (_active && !IsConnectionPage && device != null)
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
        _streamSettings = next.Normalize();
        _actions.Host.Phone.Settings.RememberStreaming(online, _streamSettings);
        _actions.Invalidate();
    }

    private void RetryEncodersIfNeeded(AndroidDevice online, bool forceRetry = false)
    {
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

    private void RunBusy(Func<CancellationToken, Task> op, string? successKey = null)
    {
        if (_busy) return;
        _busy = true;
        UpdateQrPairing();
        SetStatus(L10n.T("Phone.ProcessingStatus"), false);
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_actions.Host.Phone.Lifetime);
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                await op(cts.Token).ConfigureAwait(false);
                _actions.Host.TryPost(() =>
                {
                    _busy = false;
                    if (!_active) return;
                    _showConnection = _actions.Host.Phone.Store.Profile.LastDevice == null;
                    if (successKey != null) SetStatus(L10n.T(successKey), false);
                    else SetStatus("", false);
                    RefreshData();
                });
            }
            catch (Exception ex)
            {
                _actions.Host.TryPost(() =>
                {
                    _busy = false;
                    if (!_active) return;
                    SetStatus(PhonePreview.TranslateError(ex), true);
                    UpdateQrPairing();
                    _actions.Invalidate();
                });
            }
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
        _ = WatchQrPairingAsync(cts);
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

    // --- Media, Catalog, Remote Files, App Icons ---

    private void RestartMediaWatch(AndroidDevice? device)
    {
        StopMediaWatch();
        if (device == null || !_active) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_actions.Host.Phone.Lifetime);
        _mediaCts = cts;
        var client = new AndroidMediaSessionClient(_actions.Host.Phone.Adb);
        _mediaClient = client;

        _ = Task.Run(async () =>
        {
            try
            {
                await client.WatchAsync(device, state =>
                {
                    _actions.Host.TryPost(() =>
                    {
                        if (!ReferenceEquals(_mediaCts, cts)) return;
                        _mediaState = state;
                        if (_active) _actions.Invalidate();
                    });
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch { }
        });
    }

    private void StopMediaWatch()
    {
        CancelCts(ref _mediaCts);
        _mediaClient = null;
        _mediaState = null;
    }

    private void LoadCatalog(AndroidDevice device)
    {
        CancelCts(ref _catalogCts);
        var phone = _actions.Host.Phone;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
        cts.CancelAfter(TimeSpan.FromSeconds(18));
        _catalogCts = cts;
        _catalogLoading = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var apps = await phone.Adb.AppsAsync(device, cts.Token).ConfigureAwait(false);
                _actions.Host.TryPost(() =>
                {
                    if (!ReferenceEquals(_catalogCts, cts)) return;
                    _catalogApps = apps;
                    _catalogLoading = false;
                    FilterCatalog();
                    if (_active) _actions.Invalidate();
                });
            }
            catch
            {
                _actions.Host.TryPost(() =>
                {
                    if (!ReferenceEquals(_catalogCts, cts)) return;
                    _catalogLoading = false;
                    _actions.Invalidate();
                });
            }
        });
    }

    private void LoadRemoteFolder(AndroidDevice device, string folder)
    {
        CancelRemoteList();
        var phone = _actions.Host.Phone;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime, phone.Connections.OperationToken(device));
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        _remoteListCts = cts;
        _remoteFolder = folder;
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
                _actions.Host.TryPost(() =>
                {
                    if (!ReferenceEquals(_remoteListCts, cts) || !Equals(_activeDevice, device)) return;
                    _remoteFiles = list;
                    _remoteLoading = false;
                    _remoteError = null;
                    if (_active) _actions.Invalidate();
                });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _actions.Host.TryPost(() =>
                {
                    if (!ReferenceEquals(_remoteListCts, cts)) return;
                    _remoteLoading = false;
                    _remoteError = PhonePreview.TranslateError(ex);
                    _actions.Invalidate();
                });
            }
            finally
            {
                _actions.Host.TryPost(() =>
                {
                    if (ReferenceEquals(_remoteListCts, cts)) _remoteListCts = null;
                    cts.Dispose();
                });
            }
        });
    }

    private bool TryGetAppIcon(AndroidApp app, out byte[] bgra, out int w, out int h, out int ver)
    {
        if (app.IconPng is null || app.IconPng.Length == 0)
        {
            bgra = Array.Empty<byte>(); w = h = ver = 0;
            return false;
        }
        if (_iconCache.TryGetValue(app.Package, out var cached))
        {
            bgra = cached.Pixels; w = cached.W; h = cached.H; ver = cached.Ver;
            return true;
        }
        if (_iconDecoding.Add(app.Package))
        {
            byte[] png = app.IconPng;
            string pkg = app.Package;
            _ = Task.Run(() =>
            {
                if (!SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 48, 48, out byte[] pixels, out int pw, out int ph))
                    return;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte a = pixels[i + 3];
                    if (a == 255) continue;
                    if (a == 0) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; continue; }
                    pixels[i] = (byte)(pixels[i] * a / 255);
                    pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                    pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
                }
                _actions.Host.TryPost(() =>
                {
                    int nextVer = ++_iconVer;
                    if (_iconCache.Count >= 96 && _iconOrder.Count > 0)
                        _iconCache.Remove(_iconOrder.Dequeue());
                    _iconCache[pkg] = (pixels, pw, ph, nextVer);
                    _iconOrder.Enqueue(pkg);
                    if (_active) _actions.Invalidate();
                });
            });
        }
        bgra = Array.Empty<byte>(); w = h = ver = 0;
        return false;
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
