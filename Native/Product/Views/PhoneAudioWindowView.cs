#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Native.Graphics;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// Companion Phone Audio &amp; App Launcher window anchored beside QuickPanelWindow (235 DIP wide).
/// Exact parity with PhoneAudioWindow.xaml + PhoneCompanionLayout.
/// </summary>
internal sealed class PhoneAudioWindowView : LayeredWindow
{
    private const string AirPlayChoiceKey = "__AIRPLAY__";

    private readonly ApplicationHost _host;
    private readonly AppConfig _config;
    private readonly Action? _onDeactivatedCheck;
    private readonly Action? _onManageApps;
    private readonly UiHitMap _hit = new();
    private readonly UiScrollState _scroll = new();
    private readonly InlineEditHost _inlineEdit = new();
    private readonly AirPlayReceiverViewControl _airPlay;

    private readonly Dictionary<string, (byte[] Pixels, int W, int H, int Ver)> _iconCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _iconOrder = new();
    private int _iconVer;

    private UiThemePalette _theme;
    private ComboPopupWindow? _comboPopup;
    private bool _airPlaySelected;
    private string _searchText = "";
    private AndroidDevice? _activeDevice;
    private AndroidMediaSessionClient? _mediaClient;
    private AndroidMediaState? _mediaState;
    private CancellationTokenSource? _mediaCts;
    private byte[]? _lastArtPng;
    private byte[]? _artBgra;
    private int _artW, _artH, _artVer;
    private bool _wired;

    internal bool HasOpenModal => (_comboPopup is { IsVisible: true }) || _inlineEdit.IsOpen;

    internal PhoneAudioWindowView(
        ApplicationHost host,
        AppConfig config,
        Action? onDeactivatedCheck = null,
        Action? onManageApps = null)
        : base(UiMetrics.PhoneCompanionWidth, Math.Clamp(config.QuickPanelHeight, 350, 850), UiMetrics.PhoneCompanionWidth, 350f)
    {
        _host = host;
        _config = config;
        _onDeactivatedCheck = onDeactivatedCheck;
        _onManageApps = onManageApps;
        _theme = UiThemePalette.Resolve(config);
        TopMost = true;
        ToolWindow = true;
        NoActivate = false;

        _airPlay = new AirPlayReceiverViewControl(
            _host,
            _config,
            Invalidate,
            ShowCombo,
            StartInlineEdit,
            () => _inlineEdit.ActiveTag,
            commit => _inlineEdit.Close(commit, viaEnter: false),
            "companion.airplay");

        _inlineEdit.TextChanged += (tag, text) =>
        {
            if (tag == "companion.search")
            {
                _searchText = text;
                Invalidate();
            }
        };
        _inlineEdit.Committed += (tag, text, _) =>
        {
            if (tag == "companion.search")
            {
                _searchText = text;
                Invalidate();
            }
        };
    }

    internal void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        if (IsVisible) Invalidate();
    }

    internal void ShowForOwner(LayeredWindow owner)
    {
        OwnerHandle = owner.Handle;
        _theme = UiThemePalette.Resolve(_config);
        EnsureCreated(L10n.T("Phone.AudioWindowTitle"));
        RepositionForOwner(owner);
        if (!IsVisible) Show(activate: false);
        AttachPhone();
        RefreshData();
    }

    internal void RepositionForOwner(LayeredWindow owner)
    {
        if (owner.Handle == 0) return;
        GetMonitorWorkAreaForWindow(owner.Handle, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
        float s = monDpi / 96f;
        float workL = ml / s;
        float workT = mt / s;
        float workR = mr / s;
        float workB = mb / s;
        float workW = workR - workL;
        float workH = workB - workT;

        float ownerL = owner.LeftPx / s;
        float ownerT = owner.TopPx / s;
        float ownerW = UiMetrics.QuickPanelSurfaceWidth;
        float ownerH = owner.HeightDip;

        const float gap = UiMetrics.PhoneCompanionGap;
        float width = MathF.Min(UiMetrics.PhoneCompanionWidth, workW);
        float height = MathF.Min(ownerH, workH);
        float left = ownerL - gap - width;
        float right = ownerL + ownerW + gap;
        float x = left >= workL ? left
            : right + width <= workR ? right
            : (ownerL - workL >= workR - (ownerL + ownerW) ? left : right);
        x = Math.Clamp(x, workL, MathF.Max(workL, workR - width));
        float y = Math.Clamp(ownerT, workT, MathF.Max(workT, workB - height));

        SetBoundsPx((int)MathF.Round(x * s), (int)MathF.Round(y * s), width, height);
    }

    internal void RefreshData()
    {
        var phone = _host.Phone;
        var profile = phone.Store.Profile;
        var online = profile.LastDevice is { } last ? phone.Connections.Get(last).Device : null;
        if (!Equals(online, _activeDevice))
        {
            _activeDevice = online;
            RestartMediaWatch(online);
        }
        if (IsVisible) Invalidate();
    }

    protected override void OnShown()
    {
        base.OnShown();
        AttachPhone();
        _airPlay.Attach();
    }

    protected override void OnHidden()
    {
        _inlineEdit.Close(commit: false, viaEnter: false);
        CloseCombo();
        StopMediaWatch();
        _airPlay.Detach();
        DetachPhone();
        base.OnHidden();
    }

    protected override void OnClosed()
    {
        _inlineEdit.Dispose();
        CloseCombo();
        StopMediaWatch();
        _airPlay.Detach();
        DetachPhone();
        base.OnClosed();
    }

    private void AttachPhone()
    {
        if (_wired) return;
        _wired = true;
        _host.Phone.Changed += OnPhoneChanged;
        _host.Phone.StartTracking();
        _ = _host.Phone.RefreshAsync(restore: true);
    }

    private void DetachPhone()
    {
        if (!_wired) return;
        _wired = false;
        _host.Phone.Changed -= OnPhoneChanged;
    }

    private void OnPhoneChanged()
    {
        _host.TryPost(() =>
        {
            if (IsVisible) RefreshData();
        });
    }

    private void RestartMediaWatch(AndroidDevice? device)
    {
        StopMediaWatch();
        if (device == null || !IsVisible) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_host.Phone.Lifetime);
        _mediaCts = cts;
        var client = new AndroidMediaSessionClient(_host.Phone.Adb);
        _mediaClient = client;

        _ = Task.Run(async () =>
        {
            try
            {
                await client.WatchAsync(device, state =>
                {
                    _host.TryPost(() =>
                    {
                        if (!ReferenceEquals(_mediaCts, cts)) return;
                        _mediaState = state;
                        if (IsVisible) Invalidate();
                    });
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch { }
        });
    }

    private void StopMediaWatch()
    {
        var cts = _mediaCts;
        _mediaCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        _mediaClient = null;
        _mediaState = null;
    }

    protected override string GetWindowTitle() => L10n.T("Phone.AudioWindowTitle");

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        var outer = new UiRect(0f, 0f, widthDip, heightDip);
        g.FillRoundedRect(outer, UiMetrics.QuickPanelCornerRadius, _theme.WindowBgAlpha);
        g.DrawRoundedRect(outer, UiMetrics.QuickPanelCornerRadius, _theme.Border, 1f);

        const float padX = 8f;
        const float padY = 12f;
        float contentW = widthDip - padX * 2f;
        float footerH = 36f;
        float footerY = heightDip - padY - footerH;

        var phone = _host.Phone;
        var profile = phone.Store.Profile;
        var online = _activeDevice;
        var wholePhoneSource = online == null ? null : phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, online));

        // 1. Top Media + Receive-to-PC card
        float y = padY;
        var mediaCard = new UiRect(padX, y, contentW, 114f);
        UiPrimitives.DrawCard(g, _theme, mediaCard, 10f);

        var artRect = new UiRect(mediaCard.X + 6f, mediaCard.Y + 6f, 54f, 54f);
        g.FillRoundedRect(artRect, 8f, _theme.SurfaceBg);
        g.DrawRoundedRect(artRect, 8f, _theme.Border, 1f);
        if (TryGetArtwork(_mediaState?.ArtworkPng, out var artBgra, out int aw, out int ah, out int aver))
        {
            g.DrawBgraBitmap("companion.art", artBgra, aw, ah, artRect.Deflate(1f), version: aver);
        }
        else
        {
            g.DrawText("♫", artRect, _theme.TextSecondary, 24f, 400, UiTextAlign.Center, UiVertAlign.Center);
        }

        float infoX = artRect.Right + 6f;
        float infoW = MathF.Max(20f, mediaCard.Right - 6f - infoX);
        string mediaTitle = _mediaState is { HasSession: true } ms && ms.Title.Length > 0 ? ms.Title : L10n.T("Phone.MediaDefaultTitle");
        string mediaSub = _mediaState is { HasSession: true } ms2 && ms2.Artist.Length > 0 ? ms2.Artist
            : online != null ? L10n.T("Phone.MediaNonePlaying") : L10n.T("Phone.MediaSubtitleIdle");
        g.DrawText(mediaTitle, new UiRect(infoX, mediaCard.Y + 5f, infoW, 18f), _theme.TextPrimary, 12.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(mediaSub, new UiRect(infoX, mediaCard.Y + 23f, infoW, 14f), _theme.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        float ctrlY = mediaCard.Y + 40f;
        float btnW = 32f;
        float ctrlStartX = infoX + MathF.Max(0f, (infoW - btnW * 3f - 8f) * 0.5f);
        bool canPrev = _mediaState?.CanPrevious == true;
        bool canToggle = _mediaState?.CanToggle == true;
        bool canNext = _mediaState?.CanNext == true;
        bool isPlaying = _mediaState?.Playing == true;

        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.prev", new UiRect(ctrlStartX, ctrlY, btnW, 24f), "",
            enabled: canPrev, radius: 6f, icon: UiIcon.PreviousTrack, tooltip: L10n.T("Phone.MediaPrevious"));
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.play", new UiRect(ctrlStartX + btnW + 4f, ctrlY, btnW, 24f), "",
            enabled: canToggle, radius: 6f, icon: isPlaying ? UiIcon.Pause : UiIcon.Play, tooltip: L10n.T("Phone.MediaPlayOrPause"));
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.next", new UiRect(ctrlStartX + (btnW + 4f) * 2f, ctrlY, btnW, 24f), "",
            enabled: canNext, radius: 6f, icon: UiIcon.NextTrack, tooltip: L10n.T("Phone.MediaNext"));

        // Prominent Receive-to-PC button
        var receiveRect = new UiRect(mediaCard.X + 6f, mediaCard.Bottom - 36f, mediaCard.Width - 12f, 30f);
        bool receivingPhone = wholePhoneSource != null;
        string receiveText = receivingPhone ? L10n.T("Phone.StopReceiving") : L10n.T("Phone.AndroidReceiveToPc");
        if (receivingPhone)
        {
            UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.receive", receiveRect, receiveText,
                enabled: online != null, fontSize: 11.5f, radius: 7f, icon: UiIcon.Speaker, active: true);
        }
        else
        {
            UiPrimitives.DrawAccentButton(g, _theme, _hit, "companion.receive", receiveRect, receiveText,
                enabled: online != null, fontSize: 11.5f, radius: 7f, icon: UiIcon.Speaker);
        }

        y = mediaCard.Bottom + 6f;

        if (_airPlaySelected)
        {
            var airBox = new UiRect(padX, y, contentW, MathF.Max(40f, footerY - 6f - y));
            UiPrimitives.DrawCard(g, _theme, airBox, 10f);
            var inner = airBox.Deflate(10f);
            float airH = _airPlay.MeasureHeight(inner.Width, showHeaderStatus: false);
            _scroll.ViewportHeight = inner.Height;
            _scroll.ContentHeight = airH;
            _scroll.Clamp();

            g.PushClip(inner);
            _airPlay.Render(g, _theme, _hit, new UiRect(inner.X, inner.Y - _scroll.Offset, inner.Width - (_scroll.MaxOffset > 0.5f ? 8f : 0f), airH), showHeaderStatus: false);
            g.PopClip();
            _scroll.Draw(g, _theme, _hit, "companion.scroll", inner);
        }
        else
        {
            // Search box
            var searchRect = new UiRect(padX, y, contentW, 28f);
            UiPrimitives.DrawTextBox(g, _theme, _hit, "companion.search", searchRect, _searchText, L10n.T("Phone.SearchAppsHint"),
                focused: _inlineEdit.ActiveTag == "companion.search", fontSize: 11.5f);
            y = searchRect.Bottom + 6f;

            var listRect = new UiRect(padX, y, contentW, MathF.Max(30f, footerY - 6f - y));
            var apps = profile.RetainedApps
                .Where(a => string.IsNullOrWhiteSpace(_searchText) || a.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) || a.Package.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            const float itemH = 48f;
            const float itemGap = 4f;
            _scroll.ViewportHeight = listRect.Height;
            _scroll.ContentHeight = apps.Length * (itemH + itemGap);
            _scroll.Clamp();

            if (apps.Length == 0)
            {
                g.DrawText(L10n.T("Phone.NoDisplayedApps"), new UiRect(listRect.X, listRect.CenterY - 26f, listRect.Width, 20f),
                    _theme.TextSecondary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
                if (_onManageApps != null)
                {
                    UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.manage",
                        new UiRect(listRect.CenterX - 70f, listRect.CenterY + 2f, 140f, 28f), L10n.T("Phone.ManagePhoneApps"), fontSize: 11f);
                }
            }
            else
            {
                float itemW = _scroll.MaxOffset > 0.5f ? listRect.Width - UiMetrics.ScrollBarWidth - 2f : listRect.Width;
                g.PushClip(listRect);
                for (int i = 0; i < apps.Length; i++)
                {
                    float iy = listRect.Y + i * (itemH + itemGap) - _scroll.Offset;
                    if (iy + itemH < listRect.Y || iy > listRect.Bottom) continue;

                    var app = apps[i];
                    var rowRect = new UiRect(listRect.X, iy, itemW, itemH);
                    string rowId = $"companion.app:{i}";
                    bool isPlayingApp = _mediaState?.PlayingPackages.Contains(app.Package) == true
                        || phone.Sources.Sources.Any(s => s.App.Package == app.Package && online != null && AndroidDeviceIdentity.Matches(s.Device, online));
                    bool hot = _hit.IsHover(rowId);

                    _hit.Add(rowId, rowRect, UiCursorKind.Hand, app.Name);
                    UiColor bg = isPlayingApp ? _theme.Overlay : hot ? _theme.NavHover : _theme.SurfaceBgAlpha;
                    UiColor border = hot ? _theme.Accent : _theme.Border;
                    g.FillRoundedRect(rowRect, 10f, bg);
                    g.DrawRoundedRect(rowRect, 10f, border, 1f);

                    var iconRect = new UiRect(rowRect.X + 8f, rowRect.CenterY - 14f, 28f, 28f);
                    if (TryGetAppIcon(app, out var ibgra, out int iw, out int ih, out int iver))
                    {
                        g.DrawBgraBitmap($"companion.appicon:{app.Package}", ibgra, iw, ih, iconRect, version: iver);
                    }
                    else
                    {
                        g.FillRoundedRect(iconRect, 6f, _theme.SurfaceAlt);
                        string initial = string.IsNullOrEmpty(app.Name) ? "♪" : app.Name[..1];
                        g.DrawText(initial, iconRect, _theme.TextSecondary, 14f, 600, UiTextAlign.Center, UiVertAlign.Center);
                    }

                    float badgeW = isPlayingApp ? 46f : 0f;
                    if (isPlayingApp)
                    {
                        var bRect = new UiRect(rowRect.Right - 52f, rowRect.CenterY - 9f, 44f, 18f);
                        UiPrimitives.DrawBadge(g, _theme, bRect, L10n.T("Phone.PlayingBadge"), _theme.SurfaceBg, _theme.Accent, fontSize: 9.5f);
                    }

                    float tx = iconRect.Right + 8f;
                    float tw = MathF.Max(10f, rowRect.Right - 8f - badgeW - tx);
                    g.DrawText(app.Name, new UiRect(tx, rowRect.Y + 6f, tw, 18f), _theme.TextPrimary, 12.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                    g.DrawText(app.Package, new UiRect(tx, rowRect.Y + 24f, tw, 15f), _theme.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                }
                g.PopClip();

                _scroll.Draw(g, _theme, _hit, "companion.scroll", listRect);
            }
        }

        // Bottom Footer (Device switcher + status + Refresh)
        var footerRect = new UiRect(padX, footerY, contentW, footerH);
        var refreshRect = new UiRect(footerRect.Right - 36f, footerRect.Y, 36f, 36f);
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.refresh", refreshRect, "",
            radius: 8f, icon: UiIcon.Refresh, tooltip: L10n.T("History.Refresh"));

        var deviceCardRect = new UiRect(footerRect.X, footerRect.Y, MathF.Max(40f, refreshRect.X - 4f - footerRect.X), 36f);
        string comboLabel = _airPlaySelected ? "AirPlay"
            : profile.LastDevice != null ? phone.Store.GetProfileDisplayName(profile)
            : L10n.T("Phone.DeviceSelector");
        UiPrimitives.DrawComboBox(g, _theme, _hit, "companion.device", deviceCardRect, comboLabel, fontSize: 11f);
        UiColor statusDot = online != null ? _theme.Success : _theme.TextSecondary;
        g.FillEllipse(deviceCardRect.Right - 30f, deviceCardRect.CenterY, 3.5f, 3.5f, statusDot);
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (leftDown && _scroll.DragMove(yDip)) { Invalidate(); return; }
        if (_hit.UpdateHover(xDip, yDip))
        {
            SetCursorKind(_hit.Hovered?.Cursor ?? UiCursorKind.Arrow);
            Invalidate();
        }
    }

    protected override void OnPointerLeave()
    {
        _hit.ClearHover();
        Invalidate();
    }

    protected override void OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return;
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }
        var hit = _hit.HitTest(e.X, e.Y);
        _hit.ActiveId = hit?.Id;
        Invalidate();
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (_scroll.Dragging) { _scroll.EndDrag(); Invalidate(); return; }
        string? active = _hit.ActiveId;
        _hit.ActiveId = null;

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit is null) return;

        if (_airPlaySelected && _airPlay.HandleClick(hit.Value.Id, hit.Value.Bounds)) return;

        if (e.Button == UiMouseButton.Right && hit.Value.Id.StartsWith("companion.app:", StringComparison.Ordinal))
        {
            if (int.TryParse(hit.Value.Id.AsSpan(14), out int rIdx))
                ToggleAppAudioCapture(rIdx);
            return;
        }

        if (e.Button != UiMouseButton.Left || hit.Value.Id != active) { Invalidate(); return; }

        switch (hit.Value.Id)
        {
            case "companion.prev":
                if (_mediaClient != null) _ = _mediaClient.SendAsync("previous", _host.Phone.Lifetime);
                break;
            case "companion.play":
                if (_mediaClient != null) _ = _mediaClient.SendAsync("toggle", _host.Phone.Lifetime);
                break;
            case "companion.next":
                if (_mediaClient != null) _ = _mediaClient.SendAsync("next", _host.Phone.Lifetime);
                break;
            case "companion.receive":
                ToggleWholePhoneAudio();
                break;
            case "companion.search":
                StartInlineEdit("companion.search", hit.Value.Bounds, _searchText, t => { _searchText = t; Invalidate(); }, (t, _) => { _searchText = t; Invalidate(); });
                break;
            case "companion.manage":
                _onManageApps?.Invoke();
                break;
            case "companion.refresh":
                _ = _host.Phone.RefreshAsync(restore: true);
                break;
            case "companion.device":
                OpenDeviceSwitcher(hit.Value.Bounds);
                break;
            default:
                if (hit.Value.Id.StartsWith("companion.app:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(14), out int idx))
                    LaunchAppVirtualDisplay(idx);
                break;
        }
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        _scroll.OnWheel(delta);
        Invalidate();
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B)
        {
            if (_inlineEdit.IsOpen) { _inlineEdit.Close(false, false); return true; }
            Hide();
            _onDeactivatedCheck?.Invoke();
            return true;
        }
        return false;
    }

    protected override void OnActivated(bool active)
    {
        if (!active && !HasOpenModal) _onDeactivatedCheck?.Invoke();
    }

    private void ToggleWholePhoneAudio()
    {
        var device = _activeDevice;
        if (device == null) return;
        var existing = _host.Phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, device));
        if (existing != null)
        {
            _ = _host.Phone.Sources.RemoveAsync(existing.Id);
        }
        else
        {
            _ = _host.Phone.Sources.AddAsync(device, AndroidApp.DeviceAudio, keepPhoneAudio: false);
        }
    }

    private void LaunchAppVirtualDisplay(int filteredIndex)
    {
        var device = _activeDevice;
        if (device == null) return;
        var apps = GetFilteredApps();
        if (filteredIndex < 0 || filteredIndex >= apps.Length) return;
        var app = apps[filteredIndex];
        ViewNative.GetCursorPos(out var pt);
        _ = _host.Phone.OpenDisplayAsync(device, app, pt.X, pt.Y);
    }

    private void ToggleAppAudioCapture(int filteredIndex)
    {
        var device = _activeDevice;
        if (device == null) return;
        var apps = GetFilteredApps();
        if (filteredIndex < 0 || filteredIndex >= apps.Length) return;
        var app = apps[filteredIndex];
        var existing = _host.Phone.Sources.Sources.FirstOrDefault(s => s.App.Package == app.Package && AndroidDeviceIdentity.Matches(s.Device, device));
        if (existing != null)
            _ = _host.Phone.Sources.RemoveAsync(existing.Id);
        else
            _ = _host.Phone.Sources.AddAsync(device, app, keepPhoneAudio: false);
    }

    private AndroidApp[] GetFilteredApps()
    {
        return _host.Phone.Store.Profile.RetainedApps
            .Where(a => string.IsNullOrWhiteSpace(_searchText)
                || a.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                || a.Package.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private void OpenDeviceSwitcher(UiRect anchor)
    {
        var phone = _host.Phone;
        var choices = new List<ComboChoice>();
        foreach (var p in phone.Store.Profiles)
        {
            if (p.LastDevice == null) continue;
            choices.Add(new(p.Id, phone.Store.GetProfileDisplayName(p)));
        }
        choices.Add(new(AirPlayChoiceKey, "AirPlay"));

        string selected = _airPlaySelected ? AirPlayChoiceKey : phone.Store.Profile.Id;
        ShowCombo(anchor, choices, selected, picked =>
        {
            if (picked.Key == AirPlayChoiceKey)
            {
                _airPlaySelected = true;
            }
            else
            {
                _airPlaySelected = false;
                phone.Store.SelectProfile(picked.Key);
            }
            _scroll.Offset = 0f;
            RefreshData();
        });
    }

    private void ShowCombo(UiRect anchorDip, IReadOnlyList<ComboChoice> choices, string selectedKey, Action<ComboChoice> onSelected)
    {
        CloseCombo();
        _comboPopup = ComboPopupWindow.ShowFor(_theme, this, anchorDip, choices, selectedKey, picked =>
        {
            _comboPopup = null;
            onSelected(picked);
        });
    }

    private void CloseCombo()
    {
        if (_comboPopup is { } pop)
        {
            _comboPopup = null;
            if (!pop.IsDisposed) { pop.CloseWindow(); pop.Dispose(); }
        }
    }

    private void StartInlineEdit(string tag, UiRect rectDip, string text, Action<string>? onChanged, Action<string, bool> onCommitted)
    {
        var pt = DipToScreenPx(rectDip.X, rectDip.Y);
        int wPx = Math.Max(20, (int)MathF.Round(rectDip.Width * DpiScale));
        int hPx = Math.Max(18, (int)MathF.Round(rectDip.Height * DpiScale));
        _inlineEdit.Open(Handle, tag, text, pt.X, pt.Y, wPx, hPx, 11.5f, Dpi, _theme.SurfaceBg, _theme.TextPrimary, selectAll: true);
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
            Premultiply(pixels);
            int nextVer = ++_iconVer;
            if (_iconCache.Count >= 64 && _iconOrder.Count > 0)
                _iconCache.Remove(_iconOrder.Dequeue());
            _iconCache[app.Package] = (pixels, pw, ph, nextVer);
            _iconOrder.Enqueue(app.Package);
            bgra = pixels; w = pw; h = ph; ver = nextVer;
            return true;
        }
        bgra = Array.Empty<byte>(); w = h = ver = 0;
        return false;
    }

    private bool TryGetArtwork(byte[]? png, out byte[] bgra, out int w, out int h, out int ver)
    {
        if (png is null || png.Length == 0)
        {
            bgra = Array.Empty<byte>(); w = h = ver = 0;
            return false;
        }
        if (ReferenceEquals(png, _lastArtPng) && _artBgra != null)
        {
            bgra = _artBgra; w = _artW; h = _artH; ver = _artVer;
            return true;
        }
        _lastArtPng = png;
        if (SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 96, 96, out byte[] pixels, out int pw, out int ph))
        {
            Premultiply(pixels);
            _artBgra = pixels;
            _artW = pw;
            _artH = ph;
            _artVer++;
            bgra = pixels; w = pw; h = ph; ver = _artVer;
            return true;
        }
        _artBgra = null;
        bgra = Array.Empty<byte>(); w = h = ver = 0;
        return false;
    }

    private static void Premultiply(byte[] pixels)
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
    }
}
#endif
