#if SONICROUTE_PHONE
using System;
using System.Collections;
using System.Collections.Generic;
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
internal sealed class PhoneDevicePageView : IViewPage
{
    private readonly IViewHostActions _actions;
    private readonly UiScrollState _scroll = new();
    private UiHitMap _hit = new();
    private readonly AirPlayReceiverViewControl _airPlay;

    private readonly Dictionary<string, (byte[] Pixels, int W, int H, int Ver)> _iconCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _iconOrder = new();
    private int _iconVer;

    private bool _active;
    private bool _showConnection;
    private bool _busy;
    private string? _statusMessage;
    private bool _statusError;

    // Connection view state
    private string _pairAddress = "";
    private string _pairCode = "";
    private string _connectAddress = "";
    private AndroidQrPairing? _qrPairing;
    private byte[]? _qrBgra;
    private int _qrSizePx;
    private int _qrVersion;
    private CancellationTokenSource? _qrCts;

    // Connected view state
    private AndroidDevice? _activeDevice;
    private AndroidMediaSessionClient? _mediaClient;
    private AndroidMediaState? _mediaState;
    private CancellationTokenSource? _mediaCts;
    private CancellationTokenSource? _catalogCts;
    private IReadOnlyList<AndroidApp> _catalogApps = Array.Empty<AndroidApp>();
    private string _appSearch = "";
    private string _remoteFolder = "/sdcard/Download";
    private IReadOnlyList<AndroidStorageFile> _remoteFiles = Array.Empty<AndroidStorageFile>();
    private bool _showFileBrowser;

    internal bool IsConnectionPage => _showConnection || _actions.Host.Phone.Store.Profile.LastDevice == null;
    public bool NeedsAnimationTick => false;

    internal PhoneDevicePageView(IViewHostActions actions)
    {
        _actions = actions;
        _airPlay = new AirPlayReceiverViewControl(actions, "phonepage.airplay");
    }

    internal void ShowConnectionPage()
    {
        _showConnection = true;
        _scroll.Offset = 0f;
        StartQrPairing();
        _actions.Invalidate();
    }

    internal void ShowProfile(string profileId)
    {
        _showConnection = false;
        _actions.Host.Phone.Store.SelectProfile(profileId);
        _scroll.Offset = 0f;
        RefreshData();
    }

    public void OnActivated()
    {
        if (_active) return;
        _active = true;
        var phone = _actions.Host.Phone;
        phone.Changed += OnPhoneChanged;
        phone.Transfers.Changed += OnTransfersChanged;
        phone.StartTracking();
        _airPlay.Attach();

        if (IsConnectionPage) StartQrPairing();
        _ = phone.RefreshAsync(restore: true);
        RefreshData();
    }

    public void OnDeactivated()
    {
        if (!_active) return;
        _active = false;
        var phone = _actions.Host.Phone;
        phone.Changed -= OnPhoneChanged;
        phone.Transfers.Changed -= OnTransfersChanged;
        _airPlay.Detach();
        StopQrPairing();
        StopMediaWatch();
        CancelCts(ref _catalogCts);
    }

    public void RefreshData()
    {
        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        var online = profile.LastDevice is { } last ? phone.Connections.Get(last).Device : null;
        if (!Equals(online, _activeDevice))
        {
            _activeDevice = online;
            _remoteFiles = Array.Empty<AndroidStorageFile>();
            _catalogApps = Array.Empty<AndroidApp>();
            RestartMediaWatch(online);
            if (online != null) LoadCatalog(online);
        }
        if (IsConnectionPage && _qrPairing == null && _active)
        {
            StartQrPairing();
        }
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

    private void OnTransfersChanged(string _)
    {
        _actions.Host.TryPost(() =>
        {
            if (_active) _actions.Invalidate();
        });
    }

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _hit = hit;
        _scroll.ViewportHeight = contentRect.Height;

        float viewW = MathF.Max(200f, contentRect.Width - UiMetrics.ScrollBarWidth - 6f);
        float startY = contentRect.Y - _scroll.Offset;

        g.PushClip(contentRect);
        float endY = IsConnectionPage
            ? RenderConnectionView(g, t, contentRect.X, startY, viewW)
            : RenderConnectedView(g, t, contentRect.X, startY, viewW);
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
            g.DrawText(_statusMessage, new UiRect(x + 4f, y, w - 8f, 20f), _statusError ? t.Fail : t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            y += 24f;
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
        var nameRect = new UiRect(nameX, devCard.Y + 14f, nameMaxW, 24f);
        if (_actions.ActiveInlineEditTag != "phone.rename")
        {
            g.DrawText(devName, nameRect, t.TextPrimary, 16f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        }
        else
        {
            UiPrimitives.DrawTextBox(g, t, _hit, "phone.rename.box", nameRect, devName, focused: true, fontSize: 15f);
        }
        UiPrimitives.DrawGhostIconButton(g, t, _hit, "phone.rename", new UiRect(nameRect.Right + 4f, devCard.Y + 13f, 26f, 26f), UiIcon.Edit, 14f);

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

        // Receive to PC button + Keep Phone Audio button + Media Controls
        bool recActive = wholeSource != null;
        var recBtnRect = new UiRect(ax, ay, 148f, 32f);
        if (recActive)
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.receive", recBtnRect, L10n.T("Phone.StopReceiving"), enabled: online != null, fontSize: 12f, radius: 7f, icon: UiIcon.Speaker, active: true);
        else
            UiPrimitives.DrawAccentButton(g, t, _hit, "phone.receive", recBtnRect, L10n.T("Phone.AndroidReceiveToPc"), enabled: online != null, fontSize: 12f, radius: 7f, icon: UiIcon.Speaker);

        var bothBtnRect = new UiRect(recBtnRect.Right + 8f, ay, 150f, 32f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.receiveboth", bothBtnRect, L10n.T("Phone.DropBothPlayTitle"),
            enabled: online != null, fontSize: 11.5f, active: wholeSource?.KeepPhoneAudio == true);

        // Media transport buttons on right
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

        // Card 3: App Library Card
        var apps = profile.RetainedApps.Concat(_catalogApps).DistinctBy(a => a.Package)
            .Where(a => string.IsNullOrWhiteSpace(_appSearch) || a.Name.Contains(_appSearch, StringComparison.OrdinalIgnoreCase) || a.Package.Contains(_appSearch, StringComparison.OrdinalIgnoreCase))
            .Take(60)
            .ToArray();

        float libHeaderH = 74f;
        float libRowH = 48f;
        float libBodyH = apps.Length == 0 ? 40f : apps.Length * libRowH;
        var libCard = new UiRect(x, y, w, libHeaderH + libBodyH + 12f);
        UiPrimitives.DrawCard(g, t, libCard, 12f);

        float lx = libCard.X + 12f;
        float lw = libCard.Width - 24f;
        float ly = libCard.Y + 12f;

        g.DrawText(L10n.T("Phone.QuickPanelAppsTitle"), new UiRect(lx, ly, lw - 100f, 24f), t.TextPrimary, 15f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.refreshapps", new UiRect(libCard.Right - 102f, ly - 2f, 90f, 28f),
            L10n.T("History.Refresh"), enabled: online != null, fontSize: 11.5f, icon: UiIcon.Refresh);
        ly += 30f;

        var searchRect = new UiRect(lx, ly, lw, 30f);
        UiPrimitives.DrawTextBox(g, t, _hit, "phone.appsearch", searchRect, _appSearch, L10n.T("Phone.SearchAppsHint"),
            focused: _actions.ActiveInlineEditTag == "phone.appsearch", fontSize: 12f);
        ly += 38f;

        if (apps.Length == 0)
        {
            g.DrawText(L10n.T("Phone.RetainedEmptyHint"), new UiRect(lx, ly, lw, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Center, UiVertAlign.Center);
        }
        else
        {
            for (int i = 0; i < apps.Length; i++)
            {
                var app = apps[i];
                var rowRect = new UiRect(lx, ly + i * libRowH, lw, libRowH);
                bool retained = profile.RetainedApps.Any(a => a.Package == app.Package);
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

                // Right controls: Launch Virtual Display + Pin + SmartVolume switch
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

        y = libCard.Bottom + 10f;

        // Card 4: File Transfer Card
        var transfer = phone.Transfers.Get(profile.Id);
        float fileListH = _showFileBrowser ? Math.Clamp(_remoteFiles.Count * 32f + 36f, 96f, 240f) : 0f;
        float fileCardH = 112f + (transfer != null ? 32f : 0f) + fileListH;
        var fileCard = new UiRect(x, y, w, fileCardH);
        UiPrimitives.DrawCard(g, t, fileCard, 12f);

        float fx = fileCard.X + 12f;
        float fw = fileCard.Width - 24f;
        float fy = fileCard.Y + 12f;

        g.DrawText(L10n.T("Phone.Files"), new UiRect(fx, fy, fw - 220f, 28f), t.TextPrimary, 14f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.sendfile", new UiRect(fileCard.Right - 220f, fy, 102f, 28f),
            L10n.T("Phone.FileSend"), enabled: online != null, fontSize: 11.5f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.browsefiles", new UiRect(fileCard.Right - 112f, fy, 100f, 28f),
            L10n.T("Phone.AndroidBrowseFiles"), enabled: online != null, fontSize: 11.5f, active: _showFileBrowser);
        fy += 36f;

        g.DrawText($"{L10n.T("Phone.AndroidReceiveFolder")}: {_remoteFolder}", new UiRect(fx, fy, fw - 90f, 22f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.remotefolder", new UiRect(fileCard.Right - 96f, fy - 1f, 84f, 24f), L10n.T("Phone.FileChangeFolder"), fontSize: 10.5f);
        fy += 26f;

        g.DrawText($"{L10n.T("Phone.FileReceiveFolder")}: {profile.ReceiveFolder}", new UiRect(fx, fy, fw - 90f, 22f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.localfolder", new UiRect(fileCard.Right - 96f, fy - 1f, 84f, 24f), L10n.T("Phone.FileChangeFolder"), fontSize: 10.5f);
        fy += 28f;

        if (transfer != null)
        {
            string prog = $"{transfer.Phase} · {transfer.Index}/{transfer.Count} · {transfer.Text}";
            g.DrawText(prog, new UiRect(fx, fy, fw - (transfer.Phase == AndroidFilePhase.Running ? 74f : 0f), 24f),
                transfer.Phase == AndroidFilePhase.Failed ? t.Fail : t.Accent, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            if (transfer.Phase == AndroidFilePhase.Running)
            {
                UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.cancelfile", new UiRect(fileCard.Right - 80f, fy, 68f, 24f), L10n.T("Auto.Cancel"), fontSize: 11f, danger: true);
            }
            fy += 30f;
        }

        if (_showFileBrowser)
        {
            var fbRect = new UiRect(fx, fy, fw, fileListH - 8f);
            g.FillRoundedRect(fbRect, 8f, t.SurfaceBg);
            g.DrawRoundedRect(fbRect, 8f, t.Border, 1f);
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.folderup", new UiRect(fbRect.X + 6f, fbRect.Y + 4f, 64f, 24f), "..", fontSize: 11f);
            g.DrawText(_remoteFolder, new UiRect(fbRect.X + 78f, fbRect.Y + 4f, fbRect.Width - 86f, 24f), t.TextSecondary, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);

            int maxRows = Math.Min(_remoteFiles.Count, (int)MathF.Floor((fbRect.Height - 32f) / 30f));
            for (int i = 0; i < maxRows; i++)
            {
                var f = _remoteFiles[i];
                var r = new UiRect(fbRect.X + 6f, fbRect.Y + 32f + i * 30f, fbRect.Width - 12f, 28f);
                string fid = $"phone.rfile:{i}";
                _hit.Add(fid, r, UiCursorKind.Hand, f.Label);
                if (_hit.IsHover(fid)) g.FillRoundedRect(r, 6f, t.NavHover);
                g.DrawText((f.Directory ? "▸ " : "") + f.Label, new UiRect(r.X + 8f, r.Y, r.Width - 80f, r.Height),
                    t.TextPrimary, 12f, f.Directory ? 600 : 400, UiTextAlign.Leading, UiVertAlign.Center);
                if (!f.Directory)
                    g.DrawText(f.SizeText, new UiRect(r.Right - 72f, r.Y, 64f, r.Height), t.TextSecondary, 11f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
            }
            if (_remoteFiles.Count == 0)
            {
                g.DrawText(L10n.T("Phone.AndroidEmptyFolder"), new UiRect(fbRect.X, fbRect.Y + 36f, fbRect.Width, 32f), t.TextSecondary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
            }
        }

        y = fileCard.Bottom + 10f;

        // Card 5: Clipboard Sync Card
        var clipCard = new UiRect(x, y, w, 64f);
        UiPrimitives.DrawCard(g, t, clipCard, 12f);
        g.DrawText(L10n.T("Phone.ClipboardSyncTitle"), new UiRect(clipCard.X + 12f, clipCard.Y + 10f, clipCard.Width - 70f, 20f),
            t.TextPrimary, 13.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        string clipDesc = phone.Clipboard.Enabled ? phone.Clipboard.Status : L10n.T("Phone.ClipboardDefaultHint");
        g.DrawText(clipDesc, new UiRect(clipCard.X + 12f, clipCard.Y + 32f, clipCard.Width - 70f, 20f),
            t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSwitch(g, t, _hit, "phone.clipboard", new UiRect(clipCard.Right - 48f, clipCard.CenterY - 12f, 36f, 24f),
            phone.Clipboard.Enabled, enabled: online != null);

        return clipCard.Bottom + 12f;
    }

    private float RenderConnectionView(D2dTarget g, UiThemePalette t, float x, float y, float w)
    {
        var phone = _actions.Host.Phone;
        if (phone.Store.Profile.LastDevice != null)
        {
            UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.back", new UiRect(x, y, 110f, 30f), L10n.T("Phone.DoneButton"), fontSize: 12f);
            y += 38f;
        }

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            g.DrawText(_statusMessage, new UiRect(x + 4f, y, w - 8f, 20f), _statusError ? t.Fail : t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            y += 24f;
        }

        // Card 1: QR Pairing + Pairing Code side-by-side
        var pairCard = new UiRect(x, y, w, 256f);
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
        g.DrawText(L10n.T("Phone.QrRefreshHint"), new UiRect(qx, qrBox.Bottom + 6f, colW, 18f), t.TextSecondary, 11f, 400, UiTextAlign.Center, UiVertAlign.Center);

        // Right column: Pair with code
        float cx = qx + colW + 16f;
        g.DrawText(L10n.T("Phone.CodePairTitle"), new UiRect(cx, qy, colW, 20f), t.TextPrimary, 14f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(L10n.T("Phone.CodePairSteps"), new UiRect(cx, qy + 22f, colW, 18f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        g.DrawText(L10n.T("Phone.PairingAddressLabel"), new UiRect(cx, qy + 48f, colW, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawTextBox(g, t, _hit, "phone.pairaddr", new UiRect(cx, qy + 68f, colW, 30f), _pairAddress, "192.168.1.100:37099",
            focused: _actions.ActiveInlineEditTag == "phone.pairaddr", mono: true, fontSize: 12f);

        g.DrawText(L10n.T("Phone.PairingCodeLabel"), new UiRect(cx, qy + 106f, colW, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawTextBox(g, t, _hit, "phone.paircode", new UiRect(cx, qy + 126f, colW, 32f), _pairCode, "123456",
            focused: _actions.ActiveInlineEditTag == "phone.paircode", mono: true, fontSize: 15f);

        UiPrimitives.DrawAccentButton(g, t, _hit, "phone.pairbtn", new UiRect(cx, qy + 170f, colW, 32f), L10n.T("Phone.PairAndConnect"),
            enabled: !_busy && _pairAddress.Length > 0 && _pairCode.Length > 0, fontSize: 12f);

        y = pairCard.Bottom + 10f;

        // Card 2: Direct Connect + USB Refresh
        var directCard = new UiRect(x, y, w, 108f);
        UiPrimitives.DrawCard(g, t, directCard, 12f);
        float dx = directCard.X + 12f;
        float dw = directCard.Width - 24f;
        float dy = directCard.Y + 12f;

        g.DrawText(L10n.T("Phone.DirectConnectTitle"), new UiRect(dx, dy, dw, 20f), t.TextPrimary, 13.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(L10n.T("Phone.DirectConnectDesc"), new UiRect(dx, dy + 22f, dw, 20f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        var connBox = new UiRect(dx, dy + 48f, MathF.Max(100f, dw - 196f), 32f);
        UiPrimitives.DrawTextBox(g, t, _hit, "phone.connaddr", connBox, _connectAddress, "192.168.1.100:5555",
            focused: _actions.ActiveInlineEditTag == "phone.connaddr", mono: true, fontSize: 12f);
        UiPrimitives.DrawAccentButton(g, t, _hit, "phone.connbtn", new UiRect(connBox.Right + 8f, dy + 48f, 88f, 32f), L10n.T("Phone.ConnectShort"),
            enabled: !_busy && _connectAddress.Length > 0, fontSize: 12f);
        UiPrimitives.DrawSecondaryButton(g, t, _hit, "phone.usbrefresh", new UiRect(connBox.Right + 104f, dy + 48f, 92f, 32f),
            L10n.T("History.Refresh"), enabled: !_busy, fontSize: 12f, icon: UiIcon.Refresh);

        y = directCard.Bottom + 10f;

        // Card 3: Embedded AirPlay Receiver
        float airInnerW = w - 24f;
        float airH = _airPlay.MeasureHeight(airInnerW, showHeaderStatus: true);
        var airCard = new UiRect(x, y, w, airH + 24f);
        UiPrimitives.DrawCard(g, t, airCard, 12f);
        _airPlay.Render(g, t, _hit, airCard.Deflate(12f), showHeaderStatus: true);

        return airCard.Bottom + 12f;
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (leftDown && _scroll.DragMove(y)) { _actions.Invalidate(); return true; }
        bool changed = _hit.UpdateHover(x, y);
        if (changed)
        {
            _actions.OwnerWindow.SetCursorKind(_hit.Hovered?.Cursor ?? UiCursorKind.Arrow);
            _actions.Invalidate();
        }
        return changed;
    }

    public bool OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            _actions.Invalidate();
            return true;
        }
        var hit = _hit.HitTest(e.X, e.Y);
        _hit.ActiveId = hit?.Id;
        if (hit is { } h && h.Id == "phone.audiovol" && _activeDevice != null)
        {
            var whole = _actions.Host.Phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, _activeDevice));
            if (whole != null)
            {
                int val = UiPrimitives.SliderValueFromX(h.Bounds, e.X);
                whole.Session.SetVolume(val);
                _actions.Invalidate();
                return true;
            }
        }
        _actions.Invalidate();
        return hit != null;
    }

    public bool OnPointerUp(UiPointerEventArgs e)
    {
        if (_scroll.Dragging) { _scroll.EndDrag(); _actions.Invalidate(); return true; }
        string? active = _hit.ActiveId;
        _hit.ActiveId = null;
        if (e.Button != UiMouseButton.Left) return false;

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit is null || hit.Value.Id != active) { _actions.Invalidate(); return false; }
        string id = hit.Value.Id;

        if (_airPlay.HandleClick(id, hit.Value.Bounds)) return true;

        var phone = _actions.Host.Phone;
        var profile = phone.Store.Profile;
        var online = _activeDevice;

        if (id == "phone.add") { ShowConnectionPage(); return true; }
        if (id == "phone.back") { _showConnection = false; StopQrPairing(); RefreshData(); return true; }
        if (id == "phone.qrrefresh") { StartQrPairing(); return true; }
        if (id.StartsWith("phone.prof:", StringComparison.Ordinal)) { ShowProfile(id[11..]); return true; }

        if (id == "phone.rename")
        {
            _actions.StartInlineEdit("phone.rename", hit.Value.Bounds, phone.Store.GetProfileDisplayName(profile), null, (text, commit) =>
            {
                if (commit) { phone.Store.Rename(text); _actions.Invalidate(); }
            });
            return true;
        }
        if (id == "phone.appsearch")
        {
            _actions.StartInlineEdit("phone.appsearch", hit.Value.Bounds, _appSearch, t => { _appSearch = t; _actions.Invalidate(); }, (t, _) => { _appSearch = t; _actions.Invalidate(); });
            return true;
        }
        if (id == "phone.pairaddr")
        {
            _actions.StartInlineEdit("phone.pairaddr", hit.Value.Bounds, _pairAddress, t => _pairAddress = t, (t, _) => { _pairAddress = t.Trim(); _actions.Invalidate(); }, mono: true);
            return true;
        }
        if (id == "phone.paircode")
        {
            _actions.StartInlineEdit("phone.paircode", hit.Value.Bounds, _pairCode, t => _pairCode = t, (t, _) => { _pairCode = t.Trim(); _actions.Invalidate(); }, mono: true);
            return true;
        }
        if (id == "phone.connaddr")
        {
            _actions.StartInlineEdit("phone.connaddr", hit.Value.Bounds, _connectAddress, t => _connectAddress = t, (t, _) => { _connectAddress = t.Trim(); _actions.Invalidate(); }, mono: true);
            return true;
        }

        if (id == "phone.pairbtn")
        {
            RunBusy(ct => phone.PairAsync(_pairAddress.Trim(), _pairCode.Trim(), ct), "Phone.ConnectedStatus");
            return true;
        }
        if (id == "phone.connbtn")
        {
            RunBusy(ct => phone.DiscoverAndConnectAsync(_connectAddress.Trim(), ct), "Phone.ConnectedStatus");
            return true;
        }
        if (id == "phone.usbrefresh")
        {
            RunBusy(_ => phone.RefreshAsync(restore: true));
            return true;
        }
        if (id == "phone.reconnect" && profile.LastDevice != null)
        {
            RunBusy(_ => phone.ReconnectAsync(profile.LastDevice));
            return true;
        }
        if (id == "phone.disconnect" && online != null)
        {
            phone.Clipboard.Disable();
            RunBusy(_ => phone.DisconnectAsync(online, unpair: false), online.IsWifi ? "Phone.StatusWifiDisconnectedSaved" : "Phone.StatusUsbStoppedUnplug");
            return true;
        }
        if (id == "phone.unpair" && online != null)
        {
            if (NativeDialogs.Confirm(_actions.OwnerWindow.Handle, L10n.T("Phone.DefaultName"), L10n.T("Phone.Unpair")))
            {
                phone.Clipboard.Disable();
                RunBusy(_ => phone.DisconnectAsync(online, unpair: true), "Phone.StatusUnpairRequested");
            }
            return true;
        }
        if (id == "phone.remove" && profile.LastDevice != null)
        {
            if (NativeDialogs.Confirm(_actions.OwnerWindow.Handle, L10n.T("Phone.DefaultName"), L10n.T("Phone.DeleteDevice")))
            {
                phone.Connections.Remove(profile.Id, out _);
                _showConnection = phone.Store.Profile.LastDevice == null;
                RefreshData();
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
        if (id == "phone.refreshapps" && online != null) { LoadCatalog(online); return true; }

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
            if (NativeDialogs.Open(_actions.OwnerWindow.Handle, "All Files|*.*") is { Length: > 0 } path)
            {
                if (!phone.StartTransfer(online, [path], _remoteFolder, null))
                    SetStatus(L10n.T("Tray.AudioActionFailed"), true);
            }
            return true;
        }
        if (id == "phone.browsefiles" && online != null)
        {
            _showFileBrowser = !_showFileBrowser;
            if (_showFileBrowser) LoadRemoteFolder(online, _remoteFolder);
            _actions.Invalidate();
            return true;
        }
        if (id == "phone.folderup" && online != null)
        {
            int slash = _remoteFolder.TrimEnd('/').LastIndexOf('/');
            string parent = slash <= 0 ? "/sdcard" : _remoteFolder[..slash];
            LoadRemoteFolder(online, parent);
            return true;
        }
        if (id.StartsWith("phone.rfile:", StringComparison.Ordinal) && online != null && int.TryParse(id[12..], out int fIdx))
        {
            if (fIdx >= 0 && fIdx < _remoteFiles.Count)
            {
                var file = _remoteFiles[fIdx];
                if (file.Directory)
                    LoadRemoteFolder(online, _remoteFolder.TrimEnd('/') + "/" + file.Name);
                else if (NativeDialogs.Confirm(_actions.OwnerWindow.Handle, L10n.T("Phone.AndroidReceiveToPc"), file.Label))
                    phone.StartTransfer(online, [file.Name], profile.ReceiveFolder, _remoteFolder);
            }
            return true;
        }
        if (id == "phone.remotefolder")
        {
            if (NativeDialogs.Input(_actions.OwnerWindow.Handle, L10n.T("Phone.AndroidReceiveFolder"), _remoteFolder) is { Length: > 0 } next && online != null)
            {
                _remoteFolder = next.Trim();
                if (_showFileBrowser) LoadRemoteFolder(online, _remoteFolder);
                _actions.Invalidate();
            }
            return true;
        }
        if (id == "phone.localfolder")
        {
            if (NativeDialogs.Input(_actions.OwnerWindow.Handle, L10n.T("Phone.FileReceiveFolder"), profile.ReceiveFolder) is { Length: > 0 } next && Path.IsPathFullyQualified(next))
            {
                phone.Store.SetReceiveFolder(profile.Id, next.Trim());
                _actions.Invalidate();
            }
            return true;
        }
        if (id == "phone.cancelfile")
        {
            phone.Transfers.Cancel(profile.Id);
            return true;
        }
        if (id == "phone.clipboard" || id == "phone.clipboard:sw")
        {
            if (phone.Clipboard.Enabled) phone.Clipboard.Disable();
            else if (online != null) phone.Clipboard.Enable(online);
            _actions.Invalidate();
            return true;
        }

        return false;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        _scroll.OnWheel(delta);
        _actions.Invalidate();
        return true;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt) => false;

    private void RunBusy(Func<CancellationToken, Task> op, string? successKey = null)
    {
        if (_busy) return;
        _busy = true;
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
                    SetStatus(ex.Message, true);
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

    private void StartQrPairing()
    {
        StopQrPairing();
        var phone = _actions.Host.Phone;
        var pair = AndroidQrPairing.Create();
        _qrPairing = pair;

        using var data = QRCodeGenerator.GenerateQrCode(pair.Payload, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        int n = matrix.Count;
        int cell = Math.Max(2, 144 / Math.Max(1, n));
        int size = n * cell;
        byte[] pixels = new byte[size * size * 4];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                bool black = matrix[r][c];
                byte v = black ? (byte)0 : (byte)255;
                for (int dy = 0; dy < cell; dy++)
                {
                    int py = r * cell + dy;
                    for (int dx = 0; dx < cell; dx++)
                    {
                        int px = c * cell + dx;
                        int idx = (py * size + px) * 4;
                        pixels[idx] = v;
                        pixels[idx + 1] = v;
                        pixels[idx + 2] = v;
                        pixels[idx + 3] = 255;
                    }
                }
            }
        }
        _qrBgra = pixels;
        _qrSizePx = size;
        _qrVersion++;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
        _qrCts = cts;
        _ = Task.Run(async () =>
        {
            string? err = null;
            try { await phone.PairAsync(pair, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex) { err = ex.Message; }

            _actions.Host.TryPost(() =>
            {
                if (!ReferenceEquals(_qrCts, cts)) return;
                _qrCts = null;
                _qrPairing = null;
                if (err != null) SetStatus(err, true);
                else { _showConnection = false; SetStatus(L10n.T("Phone.ConnectedStatus"), false); }
                RefreshData();
            });
        });
    }

    private void StopQrPairing()
    {
        CancelCts(ref _qrCts);
        _qrPairing = null;
    }

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

        _ = Task.Run(async () =>
        {
            try
            {
                var apps = await phone.Adb.AppsAsync(device, cts.Token).ConfigureAwait(false);
                _actions.Host.TryPost(() =>
                {
                    if (!ReferenceEquals(_catalogCts, cts)) return;
                    _catalogApps = apps;
                    if (_active) _actions.Invalidate();
                });
            }
            catch { }
        });
    }

    private void LoadRemoteFolder(AndroidDevice device, string folder)
    {
        var phone = _actions.Host.Phone;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                var list = await new AndroidFileTransfer(phone.Adb, device.CommandTarget).ListAsync(folder, cts.Token).ConfigureAwait(false);
                _actions.Host.TryPost(() =>
                {
                    if (!Equals(_activeDevice, device)) return;
                    _remoteFolder = folder;
                    _remoteFiles = list;
                    if (_active) _actions.Invalidate();
                });
            }
            catch (Exception ex)
            {
                _actions.Host.TryPost(() => SetStatus(ex.Message, true));
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
        if (SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(app.IconPng, 48, 48, out byte[] pixels, out int pw, out int ph))
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte a = pixels[i + 3];
                if (a == 255) continue;
                if (a == 0) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; continue; }
                pixels[i] = (byte)(pixels[i] * a / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
            }
            int nextVer = ++_iconVer;
            if (_iconCache.Count >= 96 && _iconOrder.Count > 0)
                _iconCache.Remove(_iconOrder.Dequeue());
            _iconCache[app.Package] = (pixels, pw, ph, nextVer);
            _iconOrder.Enqueue(app.Package);
            bgra = pixels; w = pw; h = ph; ver = nextVer;
            return true;
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
