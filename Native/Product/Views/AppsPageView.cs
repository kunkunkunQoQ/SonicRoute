using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Graphics;
using SonicRoute.Native.Ui;
#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
#endif

namespace SonicRoute.Native.Product.Views;

internal sealed class AppsPageView : IViewPage
{
    private const float LeftRowHeight = 44f;
    private const float PhoneListMaxHeight = 196f;
    private static readonly StringComparer IdCmp = StringComparer.OrdinalIgnoreCase;

    private readonly IViewHostActions _actions;
    private readonly UiScrollState _appsScroll = new();
    private readonly UiScrollState _detailsScroll = new();
    private readonly Dictionary<string, NativeAppIconService.IconBitmap?> _customIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NativeAppIconService.IconBitmap?> _externalIconCache = new(StringComparer.Ordinal);
    private readonly List<(UiRect Rect, Action Click)> _clicks = new();
    private readonly List<(UiRect Rect, AudioAppInfo App)> _appRowRects = new();

    private List<AudioAppInfo> _apps = new();
    private Endpoint[] _outputs = Array.Empty<Endpoint>();
    private Endpoint[] _inputs = Array.Empty<Endpoint>();
    private Dictionary<string, bool> _mutedByIdentity = new(StringComparer.Ordinal);

    private readonly HashSet<string> _multiSelected = new(StringComparer.Ordinal);
    private string? _selectedIdentity;
    private string? _selectionAnchorIdentity;
    private AudioAppInfo? _selectedApp;

    private int _selectedVolume = -1;
    private bool _selectedMuted;
    private float _selectedPeak;
    private string _nativeOutput = AudioService.SystemDefaultDeviceId;
    private string _nativeInput = AudioService.SystemDefaultDeviceId;
    private HashSet<string> _selectedOutputs = new(IdCmp);
    private HashSet<string>? _pendingOutputs;
    private string? _pendingPrimary;
    private string? _outputRangeAnchor;

    private UiRect _appsListRect;
    private UiRect _detailsViewportRect;
    private UiRect _volumeSliderRect;
    private UiRect _volumeCardRect;
    private UiRect _outputComboRect;
    private UiRect _inputComboRect;
    private UiRect _renameTitleRect;
    private bool _draggingVolume;
    private int _dragVolumeVal;

    private bool _active;
    private bool _refreshing;
    private int _stateGen;
    private int _outputEditGen;
    private IDisposable? _pollTimer;

#if SONICROUTE_PHONE
    private PhoneDevicePageView? _phonePage;
    private bool _showingPhone;
    private bool _phoneSubscribed;
    private readonly UiScrollState _phoneScroll = new();
    private UiRect _phoneListRect;
#endif

    internal AppsPageView(IViewHostActions actions)
    {
        _actions = actions;
    }

    public bool NeedsAnimationTick
    {
        get
        {
#if SONICROUTE_PHONE
            if (_showingPhone && _phonePage is IViewPage vp) return vp.NeedsAnimationTick;
#endif
            return false;
        }
    }

    public void TickAnimation()
    {
#if SONICROUTE_PHONE
        if (_showingPhone && _phonePage is IViewPage vp) vp.TickAnimation();
#endif
    }

#if SONICROUTE_PHONE
    internal void OpenPhoneConnection()
    {
        EnsurePhonePage();
        _showingPhone = true;
        _selectedIdentity = null;
        _selectedApp = null;
        _multiSelected.Clear();
        _phonePage!.ShowConnectionPage();
        _phonePage.OnActivated();
        _actions.Invalidate();
    }

    private void EnsurePhonePage()
    {
        _phonePage ??= new PhoneDevicePageView(_actions);
    }

    private void OnPhoneStoreChanged()
    {
        _actions.Host.TryPost(() =>
        {
            if (_active) _actions.Invalidate();
        });
    }
#endif

    public void OnActivated()
    {
        _active = true;
#if SONICROUTE_PHONE
        if (!_phoneSubscribed)
        {
            _phoneSubscribed = true;
            _actions.Host.Phone.Changed += OnPhoneStoreChanged;
            _actions.Host.Phone.StartTracking();
        }
        if (_showingPhone) _phonePage?.OnActivated();
#endif
        TriggerRefresh(immediate: true);
    }

    public void OnDeactivated()
    {
        _active = false;
        _pollTimer?.Dispose();
        _pollTimer = null;
        _draggingVolume = false;
#if SONICROUTE_PHONE
        if (_phoneSubscribed)
        {
            _phoneSubscribed = false;
            _actions.Host.Phone.Changed -= OnPhoneStoreChanged;
        }
        _phonePage?.OnDeactivated();
#endif
    }

    public void RefreshData()
    {
        if (_active) TriggerRefresh(immediate: true);
    }

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _clicks.Clear();
        _appRowRects.Clear();

        float leftW = Math.Clamp(contentRect.Width * 0.215f, 140f, 172f);
        var leftCardRect = new UiRect(contentRect.X, contentRect.Y, leftW, contentRect.Height);
        var rightRect = new UiRect(leftCardRect.Right + 8f, contentRect.Y, MathF.Max(120f, contentRect.Width - leftW - 8f), contentRect.Height);

        UiPrimitives.DrawCard(g, t, leftCardRect, UiMetrics.CardCornerRadius);
        RenderLeftSidebar(g, t, hit, leftCardRect.Deflate(6f, 10f));

#if SONICROUTE_PHONE
        if (_showingPhone && _phonePage != null)
        {
            _detailsViewportRect = default;
            _phonePage.Render(g, t, hit, rightRect);
            return;
        }
#endif
        _detailsViewportRect = rightRect;
        RenderDetailsPane(g, t, hit, rightRect);
    }

    private void RenderLeftSidebar(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect inner)
    {
        float y = inner.Y;

#if SONICROUTE_PHONE
        var phone = _actions.Host.Phone;
        var profiles = phone.Store.Profiles.Where(p => p.LastDevice != null).ToArray();

        var phoneHdr = new UiRect(inner.X + 1f, y, inner.Width - 2f, 22f);
        g.DrawText(L10n.T("Phone.Title"), new UiRect(phoneHdr.X, phoneHdr.Y, phoneHdr.Width - 32f, phoneHdr.Height),
            t.TextSecondary, 13f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        string pCountStr = profiles.Length.ToString(CultureInfo.InvariantCulture);
        float pBadgeW = MathF.Max(22f, pCountStr.Length * 7f + 10f);
        UiPrimitives.DrawBadge(g, t, new UiRect(phoneHdr.Right - pBadgeW, phoneHdr.Y + 1f, pBadgeW, 20f), pCountStr, t.SurfaceAlt, t.TextSecondary, fontSize: 11f);
        y += 28f;

        if (profiles.Length > 0)
        {
            float listH = MathF.Min(PhoneListMaxHeight, profiles.Length * LeftRowHeight);
            _phoneListRect = new UiRect(inner.X, y, inner.Width, listH);
            _phoneScroll.ViewportHeight = listH;
            _phoneScroll.ContentHeight = profiles.Length * LeftRowHeight;
            _phoneScroll.Clamp();

            float itemW = _phoneScroll.MaxOffset > 0.5f ? inner.Width - UiMetrics.ScrollBarWidth : inner.Width;
            g.PushClip(_phoneListRect);
            for (int i = 0; i < profiles.Length; i++)
            {
                var prof = profiles[i];
                float ry = y + i * LeftRowHeight - _phoneScroll.Offset;
                if (ry + LeftRowHeight < _phoneListRect.Y || ry > _phoneListRect.Bottom) continue;

                var rowRect = new UiRect(inner.X, ry + 1f, itemW, LeftRowHeight - 2f);
                bool selected = _showingPhone && _phonePage?.IsConnectionPage == false && prof.Id == phone.Store.Profile.Id;
                string id = "apps.phone." + prof.Id;
                hit.Add(id, rowRect, UiCursorKind.Hand);
                var profCopy = prof;
                _clicks.Add((rowRect, () =>
                {
                    EnsurePhonePage();
                    phone.Store.SelectProfile(profCopy.Id);
                    _showingPhone = true;
                    _selectedIdentity = null;
                    _selectedApp = null;
                    _multiSelected.Clear();
                    _phonePage!.ShowProfile(profCopy.Id);
                    _phonePage.OnActivated();
                    _actions.Invalidate();
                }));

                if (selected) g.FillRoundedRect(rowRect, 7f, t.NavSelected);
                else if (hit.IsHover(id)) g.FillRoundedRect(rowRect, 7f, t.NavHover);

                var iconBox = new UiRect(rowRect.X + 6f, rowRect.CenterY - 11f, 22f, 22f);
                g.DrawIcon(UiIcon.Phone, iconBox, selected ? t.Accent : t.TextSecondary, 1.6f);

                var conn = phone.Connections.Get(prof.LastDevice!);
                bool online = conn.Device != null;
                string sub = !online ? L10n.T("Phone.Disconnected") : conn.Device!.IsWifi ? L10n.T("Phone.WifiConnected") : L10n.T("Phone.UsbConnected");
                string dispName = phone.Store.GetProfileDisplayName(prof);

                var textArea = new UiRect(iconBox.Right + 6f, rowRect.Y + 4f, MathF.Max(10f, rowRect.Right - iconBox.Right - 20f), rowRect.Height - 8f);
                g.DrawText(dispName, new UiRect(textArea.X, textArea.Y, textArea.Width, 18f), t.TextPrimary, 12.5f, selected ? 600 : 400);
                g.DrawText(sub, new UiRect(textArea.X, textArea.Y + 18f, textArea.Width, 15f), t.TextSecondary, 10.5f);

                if (online)
                    g.FillEllipse(rowRect.Right - 8f, rowRect.CenterY, 3f, 3f, t.Success);
            }
            g.PopClip();
            _phoneScroll.Draw(g, t, hit, "apps.phone.scroll", _phoneListRect);
            y += listH + 4f;
        }
        else
        {
            _phoneListRect = default;
        }

        var addBtnRect = new UiRect(inner.X, y, inner.Width, 26f);
        bool addActive = _showingPhone && _phonePage?.IsConnectionPage == true;
        UiPrimitives.DrawSecondaryButton(g, t, hit, "apps.phone.add", addBtnRect, L10n.T("Phone.AddDeviceButton"), fontSize: 12f, active: addActive);
        _clicks.Add((addBtnRect, OpenPhoneConnection));
        y += 34f;

        g.DrawLine(inner.X + 1f, y, inner.Right - 1f, y, t.Border, 1f, roundCaps: false);
        y += 9f;
#endif

        var appsHdr = new UiRect(inner.X + 1f, y, inner.Width - 2f, 22f);
        g.DrawText(L10n.T("Apps.Title"), new UiRect(appsHdr.X, appsHdr.Y, appsHdr.Width - 34f, appsHdr.Height),
            t.TextSecondary, 13f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        string aCountStr = _apps.Count.ToString(CultureInfo.InvariantCulture);
        float aBadgeW = MathF.Max(22f, aCountStr.Length * 7f + 10f);
        UiPrimitives.DrawBadge(g, t, new UiRect(appsHdr.Right - aBadgeW, appsHdr.Y + 1f, aBadgeW, 20f), aCountStr, t.SurfaceAlt, t.TextSecondary, fontSize: 11f);
        y += 30f;

        float refreshH = 30f;
        var refreshRect = new UiRect(inner.X, inner.Bottom - refreshH, inner.Width, refreshH);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "apps.refresh", refreshRect, L10n.T("Apps.Refresh"), fontSize: 12.5f);
        _clicks.Add((refreshRect, () => TriggerRefresh(immediate: true)));

        float listAvailH = MathF.Max(40f, refreshRect.Y - 10f - y);
        _appsListRect = new UiRect(inner.X, y, inner.Width, listAvailH);
        _appsScroll.ViewportHeight = listAvailH;
        _appsScroll.ContentHeight = _apps.Count * LeftRowHeight;
        _appsScroll.Clamp();

        float rowW = _appsScroll.MaxOffset > 0.5f ? inner.Width - UiMetrics.ScrollBarWidth : inner.Width;
        g.PushClip(_appsListRect);
        for (int i = 0; i < _apps.Count; i++)
        {
            var app = _apps[i];
            float ry = y + i * LeftRowHeight - _appsScroll.Offset;
            if (ry + LeftRowHeight < _appsListRect.Y || ry > _appsListRect.Bottom) continue;

            var rowRect = new UiRect(inner.X, ry + 1f, rowW, LeftRowHeight - 2f);
            bool selected =
#if SONICROUTE_PHONE
                !_showingPhone &&
#endif
                (_selectedIdentity == app.Identity || _multiSelected.Contains(app.Identity));

            string id = "apps.item." + i;
            hit.Add(id, rowRect, UiCursorKind.Hand, GetAppDisplayLabel(app));
            _appRowRects.Add((rowRect, app));

            if (selected) g.FillRoundedRect(rowRect, 7f, t.NavSelected);
            else if (hit.IsHover(id)) g.FillRoundedRect(rowRect, 7f, t.NavHover);

            var iconRect = new UiRect(rowRect.X + 5f, rowRect.CenterY - 13f, 26f, 26f);
            DrawAppIcon(g, t, app, iconRect);

            if (!string.IsNullOrWhiteSpace(app.ProcessName) &&
                _actions.Config.HiddenPanelApps.Contains(app.ProcessName, IdCmp))
            {
                g.FillEllipse(iconRect.X + 2f, iconRect.Y + 2f, 3f, 3f, t.AccentInverse);
            }

            string label = GetAppDisplayLabel(app);
            string sub = app.IsExternal
                ? (app.SourceLabel ?? "")
                : (!string.IsNullOrWhiteSpace(app.ProcessName) ? app.ProcessName! : $"PID {app.ProcessId}");
            var textRect = new UiRect(iconRect.Right + 6f, rowRect.Y + 4f, MathF.Max(10f, rowRect.Right - iconRect.Right - 20f), rowRect.Height - 8f);
            g.DrawText(label, new UiRect(textRect.X, textRect.Y, textRect.Width, 17f), t.TextPrimary, 12.5f, selected ? 600 : 400);
            g.DrawText(sub, new UiRect(textRect.X, textRect.Y + 18f, textRect.Width, 14f), t.TextSecondary, 10.5f);

            bool isMuted = _mutedByIdentity.GetValueOrDefault(app.Identity);
            if (isMuted)
                g.FillEllipse(rowRect.Right - 8f, rowRect.CenterY, 3f, 3f, t.Fail);
            else if (app.HasActiveSession)
                g.FillEllipse(rowRect.Right - 8f, rowRect.CenterY, 3f, 3f, t.Success);
        }
        g.PopClip();

        _appsScroll.Draw(g, t, hit, "apps.list.scroll", _appsListRect);
    }

    private void RenderDetailsPane(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport)
    {
        var app = _selectedApp;
        bool hasApp = app != null;
        string? procName = app?.ProcessName;
        bool hasProcName = !string.IsNullOrWhiteSpace(procName);

        var outChoices = BuildOutputChoices();
        var inChoices = BuildInputChoices();

        // First pass estimate for scrollbar
        float estW = viewport.Width - 2f;
        float estRouteColW = _actions.Config.ExperimentalMic ? (estW - 28f) * 0.5f : (estW - 20f);
        LayoutPillFlow(outChoices, estRouteColW, out float outPillsH);
        float inPillsH = 0f;
        if (_actions.Config.ExperimentalMic) LayoutPillFlow(inChoices, estRouteColW, out inPillsH);

        float card1H = 82f;
        float card2H = 20f + 26f + 22f + 34f + 6f + MathF.Max(outPillsH, inPillsH);
        float card3H = 20f + 22f + 34f;
        float card4H = 20f + 24f + 36f;
        float totalH = card1H + 8f + card2H + 8f + card3H + 8f + card4H + 4f;

        _detailsScroll.ViewportHeight = viewport.Height;
        _detailsScroll.ContentHeight = totalH;
        _detailsScroll.Clamp();

        float contentW = _detailsScroll.MaxOffset > 0.5f ? viewport.Width - UiMetrics.ScrollBarWidth - 4f : viewport.Width - 2f;
        float routeColW = _actions.Config.ExperimentalMic ? (contentW - 28f) * 0.5f : (contentW - 20f);
        var outPillRects = LayoutPillFlow(outChoices, routeColW, out outPillsH);
        var inPillRects = _actions.Config.ExperimentalMic ? LayoutPillFlow(inChoices, routeColW, out inPillsH) : Array.Empty<UiRect>();
        card2H = 20f + 26f + 22f + 34f + 6f + MathF.Max(outPillsH, inPillsH);

        g.PushClip(viewport);
        float y = viewport.Y - _detailsScroll.Offset;

        // --- Card 1: App Header ---
        var card1 = new UiRect(viewport.X, y, contentW, card1H);
        UiPrimitives.DrawCard(g, t, card1, UiMetrics.CardCornerRadius);
        var c1In = card1.Deflate(10f);

        var iconCard = new UiRect(c1In.X, c1In.Y, 48f, 48f);
        UiPrimitives.DrawCard(g, t, iconCard, 10f);
        if (hasApp) DrawAppIcon(g, t, app!, iconCard.Deflate(8f));

        string importLabel = L10n.T("AppIcons.Import");
        string resetLabel = L10n.T("AppIcons.Reset");
        float importW = MathF.Max(78f, D2dSharedContext.Instance.MeasureText(importLabel, UiMetrics.FontFamily, 12f, 400, 140f, 28f, false).Width + 20f);
        float resetW = MathF.Max(72f, D2dSharedContext.Instance.MeasureText(resetLabel, UiMetrics.FontFamily, 12f, 400, 140f, 28f, false).Width + 20f);
        var resetBtn = new UiRect(c1In.Right - resetW, c1In.Y, resetW, 28f);
        var importBtn = new UiRect(resetBtn.X - 6f - importW, c1In.Y, importW, 28f);

        bool canResetIcon = hasProcName && AppIconFiles.GetFileName(_actions.Config, procName!) != null;
        UiPrimitives.DrawSecondaryButton(g, t, hit, "apps.icon.import", importBtn, importLabel, enabled: hasProcName, fontSize: 12f, tooltip: L10n.T("AppIcons.Hint"));
        UiPrimitives.DrawSecondaryButton(g, t, hit, "apps.icon.reset", resetBtn, resetLabel, enabled: canResetIcon, fontSize: 12f, tooltip: L10n.T("AppIcons.Reset"));
        if (hasProcName) _clicks.Add((importBtn, ImportCustomIcon));
        if (canResetIcon) _clicks.Add((resetBtn, ResetCustomIcon));

        float titleLeft = iconCard.Right + 10f;
        float titleMaxW = MathF.Max(80f, importBtn.X - titleLeft - 36f);
        string titleText = hasApp ? GetAppDisplayLabel(app!) : L10n.T("Apps.SelectHint");
        float measuredTitleW = MathF.Min(titleMaxW, D2dSharedContext.Instance.MeasureText(titleText, UiMetrics.FontFamily, 18f, 600, titleMaxW, 28f, false).Width + 6f);
        _renameTitleRect = new UiRect(titleLeft, c1In.Y, MathF.Max(80f, MathF.Min(titleMaxW, 220f)), 26f);

        if (_actions.ActiveInlineEditTag == "apps.rename")
            g.DrawRoundedRect(_renameTitleRect, 5f, t.Accent, 1.5f);
        else
            g.DrawText(titleText, new UiRect(titleLeft, c1In.Y, titleMaxW, 26f), t.TextPrimary, 18f, 600);

        var editBtn = new UiRect(titleLeft + measuredTitleW + 4f, c1In.Y, 26f, 26f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, "apps.rename", editBtn, UiIcon.Edit, 14f, enabled: hasProcName, tooltip: L10n.T("Apps.RenameHint"));
        if (hasProcName) _clicks.Add((editBtn, BeginInlineRename));

        if (hasApp)
        {
            string pidText = app!.IsExternal
                ? (app.SourceLabel ?? "")
                : (app.SourceLabel ?? $"PID {(int)app.ProcessId} · {app.ProcessName ?? "?"}");
            g.DrawText(pidText, new UiRect(titleLeft, c1In.Y + 28f, MathF.Max(40f, c1In.Right - titleLeft), 16f), t.TextSecondary, 11f);

            string statusText = app.HasActiveSession ? L10n.T("Apps.AudioActive") : L10n.T("Apps.AudioInactive");
            UiColor statusCol = app.HasActiveSession ? t.Success : t.TextSecondary;
            g.DrawText(statusText, new UiRect(titleLeft, c1In.Y + 46f, MathF.Max(40f, c1In.Right - titleLeft), 15f), statusCol, 10.5f);
        }

        y += card1H + 8f;

        // --- Card 2: Audio Routing ---
        var card2 = new UiRect(viewport.X, y, contentW, card2H);
        UiPrimitives.DrawCard(g, t, card2, UiMetrics.CardCornerRadius);
        var c2In = card2.Deflate(10f);

        g.DrawText(L10n.T("Apps.RouteTitle"), new UiRect(c2In.X, c2In.Y, c2In.Width, 20f), t.TextPrimary, 14f, 600);
        float colTop = c2In.Y + 26f;

        var outCol = new UiRect(c2In.X, colTop, routeColW, c2In.Bottom - colTop);
        g.DrawText(L10n.T("Apps.Output"), new UiRect(outCol.X, outCol.Y, outCol.Width, 18f), t.TextSecondary, 13f, 600);
        _outputComboRect = new UiRect(outCol.X, outCol.Y + 22f, outCol.Width, 34f);
        string currentOutLabel = hasApp ? DeviceLabel(_nativeOutput, Flow.Render) : "";
        UiPrimitives.DrawComboBox(g, t, hit, "apps.out.combo", _outputComboRect, currentOutLabel, enabled: hasApp);
        if (hasApp) _clicks.Add((_outputComboRect, () => OpenOutputCombo(outChoices)));

        var activeSet = _pendingOutputs ?? _selectedOutputs;
        string? displayedNative = _pendingPrimary ?? _nativeOutput;
        float pillsOriginY = _outputComboRect.Bottom + 6f;
        for (int i = 0; i < outChoices.Length; i++)
        {
            var dev = outChoices[i];
            var pr = outPillRects[i].Offset(outCol.X, pillsOriginY);
            bool isMember = hasApp && activeSet.Contains(dev.Id);
            bool isNative = hasApp && isMember && IdCmp.Equals(dev.Id, displayedNative);
            bool isMirror = hasApp && isMember && !isNative;
            string pidKey = "apps.out.dev." + i;
            DrawQuickDevButton(g, t, hit, pidKey, pr, UiPrimitives.ShortDeviceName(dev.Label), isNative, isMirror, enabled: hasApp, tooltip: dev.Label);
            var devCopy = dev;
            if (hasApp) _clicks.Add((pr, () => HandleOutputPillClick(devCopy.Id, outChoices)));
        }

        if (_actions.Config.ExperimentalMic)
        {
            var inCol = new UiRect(outCol.Right + 8f, colTop, routeColW, c2In.Bottom - colTop);
            bool inEnabled = hasApp && !app!.IsExternal;
            g.DrawText(L10n.T("Apps.Input"), new UiRect(inCol.X, inCol.Y, inCol.Width, 18f), t.TextSecondary, 13f, 600);
            _inputComboRect = new UiRect(inCol.X, inCol.Y + 22f, inCol.Width, 34f);
            string currentInLabel = hasApp ? DeviceLabel(_nativeInput, Flow.Capture) : "";
            UiPrimitives.DrawComboBox(g, t, hit, "apps.in.combo", _inputComboRect, currentInLabel, enabled: inEnabled);
            if (inEnabled) _clicks.Add((_inputComboRect, () => OpenInputCombo(inChoices)));

            float inPillsOriginY = _inputComboRect.Bottom + 6f;
            for (int i = 0; i < inChoices.Length; i++)
            {
                var dev = inChoices[i];
                var pr = inPillRects[i].Offset(inCol.X, inPillsOriginY);
                bool isSel = inEnabled && IdCmp.Equals(dev.Id, _nativeInput);
                string iidKey = "apps.in.dev." + i;
                DrawQuickDevButton(g, t, hit, iidKey, pr, UiPrimitives.ShortDeviceName(dev.Label), isSel, false, enabled: inEnabled, tooltip: dev.Label);
                var devCopy = dev;
                if (inEnabled) _clicks.Add((pr, () => SelectInputRoute(devCopy.Id)));
            }
        }

        y += card2H + 8f;

        // --- Card 3: Volume ---
        var card3 = new UiRect(viewport.X, y, contentW, card3H);
        _volumeCardRect = card3;
        UiPrimitives.DrawCard(g, t, card3, UiMetrics.CardCornerRadius);
        var c3In = card3.Deflate(10f);

        g.DrawText(L10n.T("Apps.Volume"), new UiRect(c3In.X, c3In.Y, c3In.Width, 18f), t.TextSecondary, 13f, 600);
        float volRowY = c3In.Y + 22f;
        bool volReady = hasApp && _selectedVolume >= 0;
        string muteText = _selectedMuted ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute");
        float muteW = MathF.Max(54f, D2dSharedContext.Instance.MeasureText(muteText, UiMetrics.FontFamily, 12f, _selectedMuted ? 600 : 400, 110f, 28f, false).Width + 18f);
        var muteBtnRect = new UiRect(c3In.X, volRowY + 2f, muteW, 28f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "apps.mute", muteBtnRect, muteText, enabled: volReady, fontSize: 12f, active: _selectedMuted, tooltip: L10n.T("Apps.MuteTitle"));
        if (volReady) _clicks.Add((muteBtnRect, ToggleSelectedAppMute));

        var pctRect = new UiRect(c3In.Right - 48f, volRowY, 46f, 32f);
        int shownVol = _draggingVolume ? _dragVolumeVal : Math.Max(0, _selectedVolume);
        string pctText = volReady ? $"{shownVol}%" : "—";
        g.DrawText(pctText, pctRect, t.TextPrimary, 13f, 600, UiTextAlign.Trailing, UiVertAlign.Center);

        _volumeSliderRect = new UiRect(muteBtnRect.Right + 10f, volRowY, MathF.Max(40f, pctRect.X - muteBtnRect.Right - 14f), 32f);
        UiPrimitives.DrawVolumeSlider(
            g, t, hit, "apps.vol.slider", _volumeSliderRect,
            volReady ? shownVol / 100f : 0f,
            _selectedPeak,
            _selectedMuted,
            _actions.Config.ShowAppPeakMeter,
            trackHeight: 6f,
            thumbDiameter: 14f,
            enabled: volReady);

        y += card3H + 8f;

        // --- Card 4: Quick Controls ---
        var card4 = new UiRect(viewport.X, y, contentW, card4H);
        UiPrimitives.DrawCard(g, t, card4, UiMetrics.CardCornerRadius);
        var c4In = card4.Deflate(10f);

        g.DrawText(L10n.T("Apps.ControlsTitle"), new UiRect(c4In.X + 2f, c4In.Y, c4In.Width, 20f), t.TextPrimary, 14f, 600);

        float ctrlY = c4In.Y + 24f;
        float ctrlColW = (c4In.Width - 16f) / 3f;

        bool disableAuto = hasProcName && _actions.Config.DisabledAutoSwitchApps.Contains(procName!, IdCmp);
        var sw1Row = new UiRect(c4In.X, ctrlY, ctrlColW, 32f);
        hit.Add("apps.ctrl.disableAuto", sw1Row, UiCursorKind.Hand);
        g.DrawText(L10n.T("Apps.DisableAuto"), new UiRect(sw1Row.X, sw1Row.Y, sw1Row.Width - 40f, sw1Row.Height),
            hasProcName ? t.TextPrimary : t.TextSecondary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
        UiPrimitives.DrawSwitch(g, t, hit, "apps.ctrl.disableAuto.sw", new UiRect(sw1Row.Right - 36f, sw1Row.CenterY - 12f, 34f, 24f), disableAuto, hasProcName);
        if (hasProcName) _clicks.Add((sw1Row, ToggleDisableAutoSwitch));

        bool showInPanel = !hasProcName || !_actions.Config.HiddenPanelApps.Contains(procName!, IdCmp);
        var sw2Row = new UiRect(sw1Row.Right + 8f, ctrlY, ctrlColW, 32f);
        hit.Add("apps.ctrl.showPanel", sw2Row, UiCursorKind.Hand);
        g.DrawText(L10n.T("Apps.PanelVisible"), new UiRect(sw2Row.X, sw2Row.Y, sw2Row.Width - 40f, sw2Row.Height),
            hasProcName ? t.TextPrimary : t.TextSecondary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
        UiPrimitives.DrawSwitch(g, t, hit, "apps.ctrl.showPanel.sw", new UiRect(sw2Row.Right - 36f, sw2Row.CenterY - 12f, 34f, 24f), showInPanel, hasProcName);
        if (hasProcName) _clicks.Add((sw2Row, ToggleShowInQuickPanel));

        var resetRouteBtn = new UiRect(sw2Row.Right + 8f, ctrlY + 2f, ctrlColW, 28f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "apps.ctrl.resetRoute", resetRouteBtn, L10n.T("Dev.SystemDefault"), enabled: hasApp, fontSize: 12f, icon: UiIcon.Reset, tooltip: L10n.T("Act.ResetAllApps"));
        if (hasApp) _clicks.Add((resetRouteBtn, ResetSelectedAppRoutes));

        g.PopClip();
        _detailsScroll.Draw(g, t, hit, "apps.details.scroll", viewport);
    }

    private static void DrawQuickDevButton(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect,
        string label, bool isNative, bool isMirror, bool enabled, string? tooltip = null)
    {
        if (enabled) hit.Add(id, rect, UiCursorKind.Hand, tooltip ?? label);
        bool hot = enabled && hit.IsHover(id);

        UiColor bg = isNative ? t.DeviceNativeBg : isMirror ? t.Overlay : t.SurfaceBg;
        UiColor fg = !enabled ? t.TextSecondary.WithAlphaFraction(0.5f) : isNative ? t.DeviceNativeFg : t.TextPrimary;
        UiColor border = isNative ? t.Accent : isMirror ? t.DeviceMirrorBorder : hot ? t.Accent : t.Border;

        g.FillRoundedRect(rect, 9f, bg);
        g.DrawRoundedRect(rect, 9f, border, 1f);
        g.DrawText(label, rect.Deflate(10f, 0f), fg, 12.5f, isNative ? 600 : 400, UiTextAlign.Center, UiVertAlign.Center);
    }

    private void DrawAppIcon(D2dTarget g, UiThemePalette t, AudioAppInfo app, UiRect rect)
    {
        if (!string.IsNullOrWhiteSpace(app.ProcessName) &&
            AppIconFiles.GetFileName(_actions.Config, app.ProcessName!) is { } customFile)
        {
            if (!_customIconCache.TryGetValue(customFile, out var customBmp))
            {
                customBmp = LoadCustomIconBitmap(customFile);
                _customIconCache[customFile] = customBmp;
            }
            if (customBmp is { } cb)
            {
                g.DrawBgraBitmap("app.custom." + customFile, cb.BgraPixels, cb.Width, cb.Height, rect);
                return;
            }
        }

        if (app.ExternalIconPng is { Length: > 0 } extBytes)
        {
            if (!_externalIconCache.TryGetValue(app.Identity, out var extBmp))
            {
                extBmp = DecodePngBitmap(extBytes);
                _externalIconCache[app.Identity] = extBmp;
            }
            if (extBmp is { } eb)
            {
                g.DrawBgraBitmap("app.ext." + app.Identity, eb.BgraPixels, eb.Width, eb.Height, rect);
                return;
            }
        }

        var nativeIcon = NativeAppIconService.GetAppIcon((int)app.ProcessId, app.IconPath, app.ProcessName);
        if (nativeIcon is { } nb)
        {
            g.DrawBgraBitmap("app.native." + (app.IconPath ?? app.ProcessName ?? app.Identity), nb.BgraPixels, nb.Width, nb.Height, rect);
            return;
        }

        string lbl = GetAppDisplayLabel(app);
        string initial = lbl.Length > 0 ? lbl[..1].ToUpperInvariant() : "?";
        g.DrawText(initial, rect, t.TextPrimary, rect.Height * 0.52f, 600, UiTextAlign.Center, UiVertAlign.Center);
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (_draggingVolume && leftDown)
        {
            int next = UiPrimitives.SliderValueFromX(_volumeSliderRect, x);
            if (next != _dragVolumeVal)
            {
                _dragVolumeVal = next;
                ApplySliderVolume(next);
            }
            return true;
        }

        if (leftDown && _appsScroll.DragMove(y)) return true;
        if (leftDown && _detailsScroll.DragMove(y)) return true;

#if SONICROUTE_PHONE
        if (leftDown && _phoneScroll.DragMove(y)) return true;
        if (_showingPhone && _phonePage != null && !_appsListRect.Contains(x, y) && !_phoneListRect.Contains(x, y))
            return _phonePage.OnPointerMove(x, y, leftDown);
#endif
        return false;
    }

    public bool OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button == UiMouseButton.Right)
        {
            for (int i = 0; i < _appRowRects.Count; i++)
            {
                if (_appRowRects[i].Rect.Contains(e.X, e.Y))
                {
                    var clickedApp = _appRowRects[i].App;
                    if (!_multiSelected.Contains(clickedApp.Identity))
                        SelectSingleApp(clickedApp);
                    ShowAppContextMenu(clickedApp);
                    return true;
                }
            }
#if SONICROUTE_PHONE
            if (_showingPhone && _phonePage != null) return _phonePage.OnPointerDown(e);
#endif
            return false;
        }

        if (e.Button != UiMouseButton.Left) return false;

        if (_appsScroll.TrackRect.Contains(e.X, e.Y) && _appsScroll.BeginDrag(e.Y)) return true;
        if (_detailsScroll.TrackRect.Contains(e.X, e.Y) && _detailsScroll.BeginDrag(e.Y)) return true;
#if SONICROUTE_PHONE
        if (_phoneScroll.TrackRect.Contains(e.X, e.Y) && _phoneScroll.BeginDrag(e.Y)) return true;
#endif

        if (_selectedApp != null && _selectedVolume >= 0 && _volumeSliderRect.Contains(e.X, e.Y))
        {
            _draggingVolume = true;
            _dragVolumeVal = UiPrimitives.SliderValueFromX(_volumeSliderRect, e.X);
            ApplySliderVolume(_dragVolumeVal);
            return true;
        }

        for (int i = 0; i < _appRowRects.Count; i++)
        {
            if (_appRowRects[i].Rect.Contains(e.X, e.Y))
            {
                HandleAppRowClick(_appRowRects[i].App, e.Ctrl, e.Shift);
                return true;
            }
        }

#if SONICROUTE_PHONE
        if (_showingPhone && _phonePage != null)
            return _phonePage.OnPointerDown(e);
#endif
        return false;
    }

    public bool OnPointerUp(UiPointerEventArgs e)
    {
        bool wasDragging = _draggingVolume || _appsScroll.Dragging || _detailsScroll.Dragging;
        _draggingVolume = false;
        _appsScroll.EndDrag();
        _detailsScroll.EndDrag();
#if SONICROUTE_PHONE
        wasDragging |= _phoneScroll.Dragging;
        _phoneScroll.EndDrag();
#endif
        if (wasDragging) return true;

        if (e.Button != UiMouseButton.Left) return false;

        for (int i = _clicks.Count - 1; i >= 0; i--)
        {
            if (_clicks[i].Rect.Contains(e.X, e.Y))
            {
                _clicks[i].Click();
                return true;
            }
        }

#if SONICROUTE_PHONE
        if (_showingPhone && _phonePage != null)
            return _phonePage.OnPointerUp(e);
#endif
        return false;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (_volumeCardRect.Contains(x, y) && _selectedApp != null && _selectedVolume >= 0)
        {
            int step = Math.Clamp(_actions.Config.VolumeStep, 1, 20);
            int next = Math.Clamp(_selectedVolume + (delta > 0 ? step : -step), 0, 100);
            _selectedVolume = next;
            ApplySliderVolume(next);
            return true;
        }

        if (_appsListRect.Contains(x, y))
        {
            _appsScroll.OnWheel(delta);
            return true;
        }

#if SONICROUTE_PHONE
        if (_phoneListRect.Contains(x, y))
        {
            _phoneScroll.OnWheel(delta);
            return true;
        }
        if (_showingPhone && _phonePage != null)
            return _phonePage.OnMouseWheel(x, y, delta, horizontal);
#endif
        if (_detailsViewportRect.Contains(x, y))
        {
            _detailsScroll.OnWheel(delta);
            _actions.CloseInlineEdit(commit: true);
            return true;
        }
        return false;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
#if SONICROUTE_PHONE
        if (_showingPhone && _phonePage != null)
            return _phonePage.OnKeyDown(vk, ctrl, shift, alt);
#endif
        if (ctrl && vk == 'A' && _apps.Count > 0)
        {
            _multiSelected.Clear();
            foreach (var app in _apps) _multiSelected.Add(app.Identity);
            _selectedApp ??= _apps[0];
            _selectedIdentity ??= _apps[0].Identity;
            return true;
        }
        return false;
    }

    private void TriggerRefresh(bool immediate)
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (!_active) return;

        if (immediate)
        {
            RunAudioPoll();
        }
        else
        {
            int delayMs = _actions.Config.ShowAppPeakMeter ? 350 : 1000;
            _pollTimer = _actions.Host.ScheduleOnce(TimeSpan.FromMilliseconds(delayMs), RunAudioPoll);
        }
    }

    private void RunAudioPoll()
    {
        if (!_active || _refreshing) return;
        _refreshing = true;

        int gen = _stateGen;
        string? targetIdentity = _selectedIdentity;
        int selectedPid = _selectedApp != null && !_selectedApp.IsExternal ? (int)_selectedApp.ProcessId : 0;
        bool readMic = _actions.Config.ExperimentalMic;
        bool readPeak = _actions.Config.ShowAppPeakMeter && selectedPid > 0;

        _ = Task.Run(() =>
        {
            try
            {
                var outs = ProductAudio.Devices(Flow.Render);
                var ins = readMic ? ProductAudio.Devices(Flow.Capture) : Array.Empty<Endpoint>();
                var apps = _actions.Host.Audio.Apps();
                var mutedMap = new Dictionary<string, bool>(apps.Count, StringComparer.Ordinal);
                foreach (var a in apps)
                {
                    if (a.IsExternal) continue;
                    try { mutedMap[a.Identity] = _actions.Host.Audio.Volume(Flow.Render, null, (int)a.ProcessId).Muted; }
                    catch { mutedMap[a.Identity] = false; }
                }

                int vol = -1;
                bool muted = false;
                float peak = 0f;
                string natOut = AudioService.SystemDefaultDeviceId;
                string natIn = AudioService.SystemDefaultDeviceId;
                HashSet<string> selOuts = new(IdCmp) { AudioService.SystemDefaultDeviceId };

                if (selectedPid > 0 && apps.Any(a => (int)a.ProcessId == selectedPid))
                {
                    if (!_actions.Host.HasPendingVolume(selectedPid))
                    {
                        try
                        {
                            var vs = _actions.Host.Audio.Volume(Flow.Render, null, selectedPid);
                            vol = vs.Percent;
                            muted = vs.Muted;
                        }
                        catch { }
                    }
                    try
                    {
                        string? persisted = _actions.Host.Audio.ReadRoute(selectedPid, Flow.Render);
                        natOut = string.IsNullOrWhiteSpace(persisted) || AudioService.IsSystemDefault(persisted)
                            ? AudioService.SystemDefaultDeviceId
                            : AudioPolicyConfig.UnpackDeviceId(persisted);
                        selOuts = AppPlaybackOutputs.GetSelected(selectedPid, natOut);
                    }
                    catch { }

                    if (readMic)
                    {
                        try
                        {
                            string? persistedIn = _actions.Host.Audio.ReadRoute(selectedPid, Flow.Capture);
                            natIn = string.IsNullOrWhiteSpace(persistedIn) || AudioService.IsSystemDefault(persistedIn)
                                ? AudioService.SystemDefaultDeviceId
                                : AudioPolicyConfig.UnpackDeviceId(persistedIn);
                        }
                        catch { }
                    }

                    if (readPeak)
                    {
                        try
                        {
                            var pmap = _actions.Host.Audio.ReadPeaks(new[] { selectedPid });
                            peak = pmap.GetValueOrDefault(selectedPid);
                        }
                        catch { }
                    }
                }

                _actions.Host.TryPost(() =>
                {
                    _refreshing = false;
                    if (!_active) return;
                    _outputs = outs;
                    _inputs = ins;
                    _apps = apps;
                    _mutedByIdentity = mutedMap;

                    if (targetIdentity != null && gen == _stateGen)
                    {
                        var found = apps.FirstOrDefault(a => a.Identity == targetIdentity);
                        if (found != null)
                        {
                            _selectedApp = found;
                            if (vol >= 0 && !_draggingVolume) _selectedVolume = vol;
                            _selectedMuted = muted;
                            _selectedPeak = peak;
                            _nativeOutput = natOut;
                            _nativeInput = natIn;
                            _selectedOutputs = selOuts;
                        }
                        else if (_selectedApp != null)
                        {
                            _selectedApp = null;
                            _selectedIdentity = null;
                            _selectedVolume = -1;
                        }
                    }

                    _actions.Invalidate();
                    TriggerRefresh(immediate: false);
                });
            }
            catch
            {
                _actions.Host.TryPost(() =>
                {
                    _refreshing = false;
                    if (_active) TriggerRefresh(immediate: false);
                });
            }
        });
    }

    private void HandleAppRowClick(AudioAppInfo app, bool ctrl, bool shift)
    {
#if SONICROUTE_PHONE
        if (_showingPhone)
        {
            _showingPhone = false;
            _phonePage?.OnDeactivated();
        }
#endif
        if (shift && _selectionAnchorIdentity != null)
        {
            int a = _apps.FindIndex(x => x.Identity == _selectionAnchorIdentity);
            int b = _apps.FindIndex(x => x.Identity == app.Identity);
            if (a >= 0 && b >= 0)
            {
                if (!ctrl) _multiSelected.Clear();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                    _multiSelected.Add(_apps[i].Identity);
                SetPrimarySelectedApp(app);
                return;
            }
        }

        if (ctrl)
        {
            if (!_multiSelected.Remove(app.Identity))
            {
                _multiSelected.Add(app.Identity);
                _selectionAnchorIdentity = app.Identity;
                SetPrimarySelectedApp(app);
            }
            else if (_selectedIdentity == app.Identity)
            {
                var nextId = _multiSelected.FirstOrDefault();
                var nextApp = nextId != null ? _apps.FirstOrDefault(x => x.Identity == nextId) : null;
                if (nextApp != null) SetPrimarySelectedApp(nextApp);
                else ClearAppSelection();
            }
            _actions.Invalidate();
            return;
        }

        SelectSingleApp(app);
    }

    private void SelectSingleApp(AudioAppInfo app)
    {
        _multiSelected.Clear();
        _multiSelected.Add(app.Identity);
        _selectionAnchorIdentity = app.Identity;
        SetPrimarySelectedApp(app);
    }

    private void SetPrimarySelectedApp(AudioAppInfo app)
    {
        if (_selectedIdentity == app.Identity) return;
        _actions.CloseInlineEdit(commit: true);
        _stateGen++;
        _selectedIdentity = app.Identity;
        _selectedApp = app;
        _selectedVolume = -1;
        _selectedMuted = _mutedByIdentity.GetValueOrDefault(app.Identity);
        _selectedPeak = 0f;
        _pendingOutputs = null;
        _pendingPrimary = null;
        _outputRangeAnchor = null;
        _actions.Invalidate();
        TriggerRefresh(immediate: true);
    }

    private void ClearAppSelection()
    {
        _stateGen++;
        _selectedIdentity = null;
        _selectedApp = null;
        _selectedVolume = -1;
        _selectedMuted = false;
        _selectedPeak = 0f;
        _pendingOutputs = null;
        _pendingPrimary = null;
        _actions.Invalidate();
    }

    private void ShowAppContextMenu(AudioAppInfo primary)
    {
        var selectedApps = _apps.Where(a => _multiSelected.Contains(a.Identity)).ToList();
        if (selectedApps.Count == 0) selectedApps.Add(primary);
        bool multi = selectedApps.Count > 1;

        var items = new List<UiMenuItem>();
        if (!multi && !string.IsNullOrWhiteSpace(primary.ProcessName))
        {
            items.Add(new UiMenuItem { Text = L10n.T("Apps.Rename"), Icon = UiIcon.Edit, Click = BeginInlineRename });
        }

        items.Add(new UiMenuItem
        {
            Text = _mutedByIdentity.GetValueOrDefault(primary.Identity) ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute"),
            Icon = UiIcon.Speaker,
            Click = () => BatchToggleMute(selectedApps),
        });

        var withProc = selectedApps.Where(a => !string.IsNullOrWhiteSpace(a.ProcessName)).ToList();
        if (withProc.Count > 0)
        {
            bool allAutoDisabled = withProc.All(a => _actions.Config.DisabledAutoSwitchApps.Contains(a.ProcessName!, IdCmp));
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.DisableAuto"),
                Checked = allAutoDisabled,
                Click = () => BatchToggleList(withProc, _actions.Config.DisabledAutoSwitchApps),
            });

            bool allShownInPanel = withProc.All(a => !_actions.Config.HiddenPanelApps.Contains(a.ProcessName!, IdCmp));
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.ShowInPanel"),
                Checked = allShownInPanel,
                Click = () => BatchToggleList(withProc, _actions.Config.HiddenPanelApps),
            });
        }

        items.Add(UiMenuItem.Separator());
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Dev.SystemDefault"),
            Icon = UiIcon.Reset,
            Click = () => BatchResetRoutes(selectedApps),
        });

        _actions.ShowMenuAtCursor(items);
    }

    private void BatchToggleMute(List<AudioAppInfo> targets)
    {
        bool targetMute = !_mutedByIdentity.GetValueOrDefault(targets[0].Identity);
        _ = Task.Run(() =>
        {
            foreach (var app in targets)
            {
                if (app.IsExternal) continue;
                try { _actions.Host.Audio.Volume(Flow.Render, null, (int)app.ProcessId, mute: targetMute); }
                catch { }
            }
            _actions.Host.TryPost(() => TriggerRefresh(immediate: true));
        });
    }

    private void BatchToggleList(List<AudioAppInfo> targets, List<string> configList)
    {
        bool allInList = targets.All(a => configList.Contains(a.ProcessName!, IdCmp));
        foreach (var app in targets)
        {
            string pn = app.ProcessName!;
            if (allInList) configList.RemoveAll(x => IdCmp.Equals(x, pn));
            else if (!configList.Contains(pn, IdCmp)) configList.Add(pn);
        }
        ConfigService.TrySave(_actions.Config);
        _actions.NotifySettingsChanged();
    }

    private void BatchResetRoutes(List<AudioAppInfo> targets)
    {
        _ = Task.Run(async () =>
        {
            foreach (var app in targets)
            {
                if (app.IsExternal) continue;
                int pid = (int)app.ProcessId;
                try { await _actions.Host.CurrentApp.SelectOutputAsync(pid, AudioService.SystemDefaultDeviceId).ConfigureAwait(false); }
                catch { }
                if (_actions.Config.ExperimentalMic)
                {
                    try { _actions.Host.Audio.SetRoute(pid, Flow.Capture, null); }
                    catch { }
                }
            }
            _actions.Host.TryPost(() => TriggerRefresh(immediate: true));
        });
    }

    private void BeginInlineRename()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        string initial = _actions.Config.AppNames.TryGetValue(proc, out var custom) ? custom : "";
        _actions.StartInlineEdit(
            "apps.rename",
            _renameTitleRect,
            initial,
            text =>
            {
                string trimmed = (text ?? "").Trim();
                if (string.IsNullOrEmpty(trimmed)) _actions.Config.AppNames.Remove(proc);
                else _actions.Config.AppNames[proc] = trimmed;
                _actions.Invalidate();
            },
            (text, _) =>
            {
                string trimmed = (text ?? "").Trim();
                if (string.IsNullOrEmpty(trimmed)) _actions.Config.AppNames.Remove(proc);
                else _actions.Config.AppNames[proc] = trimmed;
                ConfigService.TrySave(_actions.Config);
                _actions.Host.ViewSettingsChanged();
                _actions.ShowNotice(L10n.T("Apps.RenameSaved"));
            });
    }

    private void ImportCustomIcon()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        if (_actions.Config.AppIcons.Count >= AppIconFiles.MaxIcons && !_actions.Config.AppIcons.ContainsKey(proc))
        {
            _actions.ShowNotice(L10n.T("AppIcons.Limit"));
            return;
        }
        string? path = NativeDialogs.Open(_actions.OwnerWindow.Handle, "PNG / JPEG / ICO|*.png;*.jpg;*.jpeg;*.ico");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 8 * 1024 * 1024)
            {
                _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
                return;
            }
            byte[] raw = File.ReadAllBytes(path);
            if (!SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(raw, AppIconFiles.IconSize, AppIconFiles.IconSize, out byte[] bgra, out int w, out int h)
                || w != AppIconFiles.IconSize || h != AppIconFiles.IconSize)
            {
                _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
                return;
            }
            byte[] png64 = EncodePng64(bgra, w, h);
            string savedName = AppIconFiles.SavePngIfChanged(png64, out bool changed);
            if (!ConfigService.TrySetAppIcon(proc, savedName))
            {
                _actions.ShowNotice(L10n.T("AppIcons.SaveFailed"));
                return;
            }
            if (changed) _customIconCache.Remove(savedName);
            AppIconFiles.CleanupUnused(_actions.Config);
            _actions.Host.ViewSettingsChanged();
            _actions.ShowNotice(L10n.T("AppIcons.Imported"));
        }
        catch
        {
            _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
        }
    }

    private void ResetCustomIcon()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        if (!ConfigService.TrySetAppIcon(proc, null))
        {
            _actions.ShowNotice(L10n.T("AppIcons.SaveFailed"));
            return;
        }
        AppIconFiles.CleanupUnused(_actions.Config);
        _actions.Host.ViewSettingsChanged();
        _actions.ShowNotice(L10n.T("AppIcons.ResetDone"));
    }

    private void OpenOutputCombo(DeviceChoice[] choices)
    {
        if (_selectedApp == null) return;
        var list = choices.Select(c => new ComboChoice(c.Id, c.Label)).ToArray();
        _actions.ShowCombo(_outputComboRect, list, _nativeOutput, picked =>
        {
            SelectSingleOutputRoute(picked.Key);
        });
    }

    private void OpenInputCombo(DeviceChoice[] choices)
    {
        if (_selectedApp == null || _selectedApp.IsExternal) return;
        var list = choices.Select(c => new ComboChoice(c.Id, c.Label)).ToArray();
        _actions.ShowCombo(_inputComboRect, list, _nativeInput, picked =>
        {
            SelectInputRoute(picked.Key);
        });
    }

    private void HandleOutputPillClick(string deviceId, DeviceChoice[] choices)
    {
        if (_selectedApp == null || _selectedApp.IsExternal) return;
        bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
        bool shift = (GetKeyState(0x10) & 0x8000) != 0;

        if (!ctrl && !shift)
        {
            _outputRangeAnchor = deviceId;
            SelectSingleOutputRoute(deviceId);
            return;
        }

        int pid = (int)_selectedApp.ProcessId;
        var selection = new HashSet<string>(_pendingOutputs ?? _selectedOutputs, IdCmp);
        if (shift)
        {
            int a = Array.FindIndex(choices, c => IdCmp.Equals(c.Id, _outputRangeAnchor));
            int b = Array.FindIndex(choices, c => IdCmp.Equals(c.Id, deviceId));
            if (a < 0) a = b;
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                selection.Add(choices[i].Id);
        }
        else
        {
            if (!selection.Remove(deviceId)) selection.Add(deviceId);
            _outputRangeAnchor = deviceId;
        }
        if (selection.Count == 0) selection.Add(AudioService.SystemDefaultDeviceId);

        string preferred = selection.Contains(deviceId) ? deviceId
            : selection.Contains(_nativeOutput) ? _nativeOutput
            : selection.First();

        _pendingOutputs = selection;
        _pendingPrimary = preferred;
        int editGen = ++_outputEditGen;
        _actions.Invalidate();

        _ = Task.Run(async () =>
        {
            var reply = await _actions.Host.CurrentApp.SetOutputsAsync(pid, preferred, selection.ToArray()).ConfigureAwait(false);
            _actions.Host.TryPost(() =>
            {
                if (editGen != _outputEditGen) return;
                _pendingOutputs = null;
                _pendingPrimary = null;
                if (reply.Text.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                    _actions.ShowNotice(reply.Text);
                TriggerRefresh(immediate: true);
            });
        });
    }

    private void SelectSingleOutputRoute(string deviceId)
    {
        if (_selectedApp == null || _selectedApp.IsExternal) return;
        int pid = (int)_selectedApp.ProcessId;
        _pendingOutputs = new HashSet<string>(IdCmp) { deviceId };
        _pendingPrimary = deviceId;
        int editGen = ++_outputEditGen;
        _actions.Invalidate();

        _ = Task.Run(async () =>
        {
            var reply = await _actions.Host.CurrentApp.SelectOutputAsync(pid, deviceId).ConfigureAwait(false);
            _actions.Host.TryPost(() =>
            {
                if (editGen != _outputEditGen) return;
                _pendingOutputs = null;
                _pendingPrimary = null;
                if (reply.Text.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                    _actions.ShowNotice(reply.Text);
                TriggerRefresh(immediate: true);
            });
        });
    }

    private void SelectInputRoute(string deviceId)
    {
        if (_selectedApp == null || _selectedApp.IsExternal) return;
        int pid = (int)_selectedApp.ProcessId;
        _nativeInput = deviceId;
        _actions.Invalidate();

        _ = Task.Run(() =>
        {
            try
            {
                _actions.Host.Audio.SetRoute(pid, Flow.Capture, AudioService.IsSystemDefault(deviceId) ? null : deviceId);
            }
            catch (Exception ex)
            {
                _actions.Host.TryPost(() => _actions.ShowNotice(ex.Message));
            }
            _actions.Host.TryPost(() => TriggerRefresh(immediate: true));
        });
    }

    private void ToggleSelectedAppMute()
    {
        if (_selectedApp == null || _selectedApp.IsExternal) return;
        int pid = (int)_selectedApp.ProcessId;
        _ = Task.Run(() =>
        {
            try
            {
                var vs = _actions.Host.Audio.Volume(Flow.Render, null, pid, toggle: true);
                _actions.Host.TryPost(() =>
                {
                    if (_selectedApp?.ProcessId == (uint)pid)
                    {
                        _selectedMuted = vs.Muted;
                        _selectedVolume = vs.Percent;
                        _mutedByIdentity[_selectedApp.Identity] = vs.Muted;
                        _actions.Invalidate();
                    }
                });
            }
            catch (Exception ex)
            {
                _actions.Host.TryPost(() => _actions.ShowNotice(ex.Message));
            }
        });
    }

    private void ApplySliderVolume(int percent)
    {
        if (_selectedApp == null || _selectedApp.IsExternal) return;
        int pid = (int)_selectedApp.ProcessId;
        _selectedVolume = percent;
        _actions.Host.QueueVolume(pid, percent, (actual, applied) =>
        {
            if (!_active || _selectedApp?.ProcessId != (uint)pid) return;
            if (applied && actual >= 0 && !_draggingVolume) _selectedVolume = actual;
            else if (!applied) _actions.ShowNotice(L10n.T("Ov.VolFail"));
            _actions.Invalidate();
        });
        _actions.Invalidate();
    }

    private void ToggleDisableAutoSwitch()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        var list = _actions.Config.DisabledAutoSwitchApps;
        if (list.RemoveAll(x => IdCmp.Equals(x, proc)) == 0) list.Add(proc);
        ConfigService.TrySave(_actions.Config);
        _actions.NotifySettingsChanged();
    }

    private void ToggleShowInQuickPanel()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        var list = _actions.Config.HiddenPanelApps;
        if (list.RemoveAll(x => IdCmp.Equals(x, proc)) == 0) list.Add(proc);
        ConfigService.TrySave(_actions.Config);
        _actions.NotifySettingsChanged();
    }

    private void ResetSelectedAppRoutes()
    {
        if (_selectedApp == null) return;
        BatchResetRoutes(new List<AudioAppInfo> { _selectedApp });
    }

    private readonly record struct DeviceChoice(string Id, string Label);

    private DeviceChoice[] BuildOutputChoices()
    {
        var cfg = _actions.Config;
        var list = new List<DeviceChoice>();
        if (!cfg.HiddenOutputDevices.Contains(AudioService.SystemDefaultDeviceId) || IdCmp.Equals(_nativeOutput, AudioService.SystemDefaultDeviceId))
            list.Add(new DeviceChoice(AudioService.SystemDefaultDeviceId, L10n.T("Dev.SystemDefaultOut")));

        foreach (var ep in _outputs)
        {
            if (cfg.HiddenOutputDevices.Contains(ep.Id) && !IdCmp.Equals(_nativeOutput, ep.Id) && !_selectedOutputs.Contains(ep.Id))
                continue;
            list.Add(new DeviceChoice(ep.Id, DeviceLabel(ep.Id, Flow.Render, ep.Name)));
        }
        return list.ToArray();
    }

    private DeviceChoice[] BuildInputChoices()
    {
        var cfg = _actions.Config;
        var list = new List<DeviceChoice>();
        if (!cfg.HiddenInputDevices.Contains(AudioService.SystemDefaultDeviceId) || IdCmp.Equals(_nativeInput, AudioService.SystemDefaultDeviceId))
            list.Add(new DeviceChoice(AudioService.SystemDefaultDeviceId, L10n.T("Dev.SystemDefaultIn")));

        foreach (var ep in _inputs)
        {
            if (cfg.HiddenInputDevices.Contains(ep.Id) && !IdCmp.Equals(_nativeInput, ep.Id))
                continue;
            list.Add(new DeviceChoice(ep.Id, DeviceLabel(ep.Id, Flow.Capture, ep.Name)));
        }
        return list.ToArray();
    }

    private static UiRect[] LayoutPillFlow(DeviceChoice[] choices, float maxWidth, out float totalHeight)
    {
        var rects = new UiRect[choices.Length];
        float x = 0f, y = 0f, rowH = 30f, gapX = 8f, gapY = 8f;
        var ctx = D2dSharedContext.Instance;

        for (int i = 0; i < choices.Length; i++)
        {
            string shortLbl = UiPrimitives.ShortDeviceName(choices[i].Label);
            float textW = ctx.MeasureText(shortLbl, UiMetrics.FontFamily, 12.5f, 600, MathF.Max(60f, maxWidth - 24f), rowH, false).Width;
            float pillW = MathF.Min(maxWidth, MathF.Ceiling(textW) + 24f);
            if (x > 0f && x + pillW > maxWidth)
            {
                x = 0f;
                y += rowH + gapY;
            }
            rects[i] = new UiRect(x, y, pillW, rowH);
            x += pillW + gapX;
        }
        totalHeight = choices.Length > 0 ? y + rowH : 0f;
        return rects;
    }

    private string GetAppDisplayLabel(AudioAppInfo app)
    {
        if (!string.IsNullOrWhiteSpace(app.ProcessName) &&
            _actions.Config.AppNames.TryGetValue(app.ProcessName!, out string? custom) &&
            !string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }
        return app.Label;
    }

    private string DeviceLabel(string? id, Flow flow, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id))
            return L10n.T(flow == Flow.Render ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn");

        if (_actions.Config.DeviceNames.TryGetValue(id, out string? custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;

        var list = flow == Flow.Render ? _outputs : _inputs;
        return fallback ?? list.FirstOrDefault(e => IdCmp.Equals(e.Id, id))?.Name ?? id;
    }

    private static NativeAppIconService.IconBitmap? LoadCustomIconBitmap(string fileName)
    {
        try { return DecodePngBitmap(AppIconFiles.ReadPng(fileName)); }
        catch { return null; }
    }

    private static NativeAppIconService.IconBitmap? DecodePngBitmap(byte[] png)
    {
        if (!SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 64, 64, out byte[] pixels, out int w, out int h))
            return null;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte a = pixels[i + 3];
            if (a == 255) continue;
            if (a == 0) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; continue; }
            pixels[i] = (byte)(pixels[i] * a / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
        }
        return new NativeAppIconService.IconBitmap(pixels, w, h);
    }

    private static byte[] EncodePng64(byte[] bgra, int w, int h)
    {
        // Convert BGRA to filtered RGBA scanlines (filter byte 0 per row)
        int stride = w * 4;
        byte[] raw = new byte[(stride + 1) * h];
        for (int row = 0; row < h; row++)
        {
            int dst = row * (stride + 1);
            int src = row * stride;
            raw[dst] = 0; // filter None
            for (int col = 0; col < w; col++)
            {
                int s = src + col * 4;
                int d = dst + 1 + col * 4;
                raw[d + 0] = bgra[s + 2]; // R
                raw[d + 1] = bgra[s + 1]; // G
                raw[d + 2] = bgra[s + 0]; // B
                raw[d + 3] = bgra[s + 3]; // A
            }
        }

        byte[] idatPayload;
        using (var zms = new MemoryStream())
        {
            using (var zs = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(raw, 0, raw.Length);
            idatPayload = zms.ToArray();
        }

        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        WriteBigEndianUInt32(ihdr, 0, (uint)w);
        WriteBigEndianUInt32(ihdr, 4, (uint)h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", idatPayload);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        WriteBigEndianUInt32(lenBuf, 0, (uint)data.Length);
        s.Write(lenBuf);

        byte[] typeAndData = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typeAndData[i] = (byte)type[i];
        data.CopyTo(typeAndData, 4);
        s.Write(typeAndData);

        uint crc = Crc32(typeAndData);
        Span<byte> crcBuf = stackalloc byte[4];
        WriteBigEndianUInt32(crcBuf, 0, crc);
        s.Write(crcBuf);
    }

    private static void WriteBigEndianUInt32(Span<byte> buf, int offset, uint val)
    {
        buf[offset + 0] = (byte)(val >> 24);
        buf[offset + 1] = (byte)(val >> 16);
        buf[offset + 2] = (byte)(val >> 8);
        buf[offset + 3] = (byte)val;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < bytes.Length; i++)
        {
            crc ^= bytes[i];
            for (int b = 0; b < 8; b++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    public void Dispose()
    {
        OnDeactivated();
#if SONICROUTE_PHONE
        _phonePage?.Dispose();
        _phonePage = null;
#endif
        _customIconCache.Clear();
        _externalIconCache.Clear();
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
}
