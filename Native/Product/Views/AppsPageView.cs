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
    private const int PeakBurstMaxTicks = 24;
    private static readonly StringComparer IdCmp = StringComparer.OrdinalIgnoreCase;

    private readonly IViewHostActions _actions;
    private readonly AliasEditor _aliasEditor;
    private readonly UiScrollState _appsScroll = new();
    private readonly UiScrollState _detailsScroll = new();
    private readonly Dictionary<string, NativeAppIconService.IconBitmap?> _externalIconCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioAppInfo> _appRows = new(StringComparer.Ordinal);

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
    private bool _refreshPending;
    private int _stateGen;
    private int _outputEditGen;
    private Task _inputWrites = Task.CompletedTask;
    private string? _pendingInput;
    private int _inputEditGen;
#if SONICROUTE_PHONE
    private Task _externalOutputs = Task.CompletedTask;
#endif

    private IDisposable? _peakTimer;
    private bool _peakInFlight;
    private int _peakBurstRemaining;

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
        _aliasEditor = new(actions);
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
        StopPeakBurst();
        _phonePage!.ShowConnectionPage();
        _phonePage.OnActivated();
        _actions.Invalidate();
    }

    internal void OpenPhoneDevice(string? profileId = null)
    {
        EnsurePhonePage();
        _showingPhone = true;
        _selectedIdentity = null;
        _selectedApp = null;
        _multiSelected.Clear();
        StopPeakBurst();
        var store = _actions.Host.Phone.Store;
        if (!string.IsNullOrWhiteSpace(profileId))
            _phonePage!.ShowProfile(profileId);
        else if (store.Profile.LastDevice != null)
            _phonePage!.ShowProfile(store.Profile.Id);
        else
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
            if (_active)
            {
                TriggerRefresh(forceAppsRefresh: false);
                _actions.Invalidate();
            }
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
        TriggerRefresh(forceAppsRefresh: false);
    }

    public void OnDeactivated()
    {
        _active = false;
        _refreshPending = false;
        _draggingVolume = false;
        StopPeakBurst();
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
        if (_active) TriggerRefresh(forceAppsRefresh: false);
    }

    public void OnCaptureLost()
    {
        if (_draggingVolume)
        {
            _draggingVolume = false;
            _actions.Host.FlushVolumeWrites();
        }
        _appsScroll.EndDrag();
        _detailsScroll.EndDrag();
#if SONICROUTE_PHONE
        _phoneScroll.EndDrag();
        if (_showingPhone) _phonePage?.OnCaptureLost();
#endif
        _actions.Invalidate();
    }

    public void OnFilesDropped(string[] paths)
    {
#if SONICROUTE_PHONE
        if (_showingPhone && _phonePage != null)
        {
            _phonePage.OnFilesDropped(paths);
            return;
        }
#endif
        if (paths.Length != 1 || _selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName))
        {
            if (_selectedApp == null) _actions.ShowNotice(L10n.T("Apps.SelectHint"));
            return;
        }
        string ext = Path.GetExtension(paths[0]);
        if (IdCmp.Equals(ext, ".png") || IdCmp.Equals(ext, ".jpg") || IdCmp.Equals(ext, ".jpeg")
            || IdCmp.Equals(ext, ".ico") || IdCmp.Equals(ext, ".exe"))
        {
            ImportCustomIconFromPath(paths[0]);
        }
        else
        {
            _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
        }
    }

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _clicks.Clear();
        _appRows.Clear();

        float leftW = Math.Clamp(contentRect.Width * 0.215f, 140f, 172f) - 8f;
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

    private static void DrawSidebarHeader(D2dTarget g, UiThemePalette theme, UiHitMap hit, UiRect row, string id, string label, int count)
    {
        string number = count.ToString(CultureInfo.InvariantCulture);
        var measure = D2dSharedContext.Instance.MeasureText(number, UiMetrics.FontFamily, 11f, 400, 80f, 28f, false);
        float badgeWidth = MathF.Ceiling(measure.Width) + 10f;
        var badge = new UiRect(row.Right - badgeWidth, row.CenterY - (measure.Height + 6f) / 2f, badgeWidth, measure.Height + 6f);
        var text = new UiRect(row.X, row.Y, MathF.Max(0f, badge.X - 4f - row.X), row.Height);
        g.DrawText(label, text, theme.TextSecondary, 13f, 600);
        hit.Add(id, text, UiCursorKind.Arrow, label);
        g.FillRoundedRect(badge, 6f, theme.SurfaceAlt);
        g.DrawText(number, badge, theme.TextSecondary, 11f, 400, UiTextAlign.Center, UiVertAlign.Center);
    }

    private void RenderLeftSidebar(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect inner)
    {
        float y = inner.Y;

#if SONICROUTE_PHONE
        var phone = _actions.Host.Phone;
        var profiles = phone.Store.Profiles.Where(p => p.LastDevice != null).ToArray();

        var phoneHdr = new UiRect(inner.X + 1f, y, inner.Width - 2f, 22f);
        DrawSidebarHeader(g, t, hit, phoneHdr, "apps.phone.header", L10n.T("Phone.Title"), profiles.Length);
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
            hit.PushClip(_phoneListRect);
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
                _clicks[id] = () => OpenPhoneDevice(profCopy.Id);

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
            hit.PopClip();
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
        _clicks["apps.phone.add"] = OpenPhoneConnection;
        y += 34f;

        g.DrawLine(inner.X + 1f, y, inner.Right - 1f, y, t.Border, 1f, roundCaps: false);
        y += 9f;
#endif

        var appsHdr = new UiRect(inner.X + 1f, y, inner.Width - 2f, 22f);
        DrawSidebarHeader(g, t, hit, appsHdr, "apps.list.header", L10n.T("Apps.Title"), _apps.Count);
        y += 30f;

        float refreshH = 30f;
        var refreshRect = new UiRect(inner.X, inner.Bottom - refreshH, inner.Width, refreshH);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "apps.refresh", refreshRect, L10n.T("Apps.Refresh"), fontSize: 12.5f);
        _clicks["apps.refresh"] = () =>
        {
            TriggerRefresh(forceAppsRefresh: true);
#if SONICROUTE_PHONE
            _ = _actions.Host.Phone.RefreshAsync(restore: true);
#endif
        };

        float listAvailH = MathF.Max(40f, refreshRect.Y - 10f - y);
        _appsListRect = new UiRect(inner.X, y, inner.Width, listAvailH);
        _appsScroll.ViewportHeight = listAvailH;
        _appsScroll.ContentHeight = _apps.Count * LeftRowHeight;
        _appsScroll.Clamp();

        float rowW = _appsScroll.MaxOffset > 0.5f ? inner.Width - UiMetrics.ScrollBarWidth : inner.Width;
        g.PushClip(_appsListRect);
        hit.PushClip(_appsListRect);
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

            string id = "apps.item." + app.Identity;
            hit.Add(id, rowRect, UiCursorKind.Hand, GetAppDisplayLabel(app));
            _appRows[id] = app;

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
        hit.PopClip();
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

        float estW = viewport.Width - 2f;
        float estRouteColW = _actions.Config.ExperimentalMic ? (estW - 28f) * 0.5f : (estW - 20f);
        LayoutPillFlow(outChoices, estRouteColW, out float outPillsH);
        float inPillsH = 0f;
        if (_actions.Config.ExperimentalMic) LayoutPillFlow(inChoices, estRouteColW, out inPillsH);

        float card1H = hasApp ? 82f : 70f;
        float card2H = 20f + 26f + 22f + 36f + 5f + MathF.Max(outPillsH, inPillsH);
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
        card2H = 20f + 26f + 22f + 36f + 5f + MathF.Max(outPillsH, inPillsH);
        _detailsScroll.ContentHeight = card1H + card2H + card3H + card4H + 28f;
        _detailsScroll.Clamp();

        g.PushClip(viewport);
        hit.PushClip(viewport);
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
        if (hasProcName) _clicks["apps.icon.import"] = ImportCustomIcon;
        if (canResetIcon) _clicks["apps.icon.reset"] = ResetCustomIcon;

        float titleLeft = iconCard.Right + 10f;
        float titleMaxW = MathF.Max(80f, importBtn.X - titleLeft - 36f);
        string titleText = hasApp ? GetAppDisplayLabel(app!) : L10n.T("Apps.SelectHint");
        float measuredTitleW = MathF.Min(titleMaxW, D2dSharedContext.Instance.MeasureText(titleText, UiMetrics.FontFamily, 18f, 600, titleMaxW, 28f, false).Width + 6f);
        _renameTitleRect = new UiRect(titleLeft, c1In.Y, MathF.Max(80f, MathF.Min(titleMaxW, 220f)), 26f);

        if (_actions.ActiveInlineEditTag == "apps.rename")
            UiPrimitives.DrawTextBox(g, t, hit, "apps.rename", _renameTitleRect, titleText, focused: true, fontSize: 13f);
        else
            g.DrawText(titleText, new UiRect(titleLeft, c1In.Y, titleMaxW, 26f), t.TextPrimary, 18f, 600);

        var editBtn = new UiRect(titleLeft + measuredTitleW + 4f, c1In.Y, 26f, 26f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, "apps.rename", editBtn, UiIcon.Edit, 14f, enabled: hasProcName, tooltip: L10n.T("Apps.RenameHint"));
        if (hasProcName) _clicks["apps.rename"] = BeginInlineRename;

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
        _outputComboRect = new UiRect(outCol.X, outCol.Y + 22f, outCol.Width, 36f);
        string displayedPrimary = GetDisplayedPrimaryOutput();
        string currentOutLabel = hasApp ? DeviceLabel(displayedPrimary, Flow.Render) : "";
        UiPrimitives.DrawComboBox(g, t, hit, "apps.out.combo", _outputComboRect, currentOutLabel, enabled: hasApp);
        if (hasApp) _clicks["apps.out.combo"] = () => OpenOutputCombo(outChoices);

        var activeSet = _pendingOutputs ?? _selectedOutputs;
        string? nativeCandidate = hasApp && !app!.IsExternal && !PcRoutingApi.IsPhoneOutputDevice(displayedPrimary) && activeSet.Contains(displayedPrimary)
            ? displayedPrimary
            : null;
        float pillsOriginY = _outputComboRect.Bottom + 5f;
        for (int i = 0; i < outChoices.Length; i++)
        {
            var dev = outChoices[i];
            var pr = outPillRects[i].Offset(outCol.X, pillsOriginY);
            bool isMember = hasApp && activeSet.Contains(dev.Id);
            bool isNative = hasApp && isMember && (app!.IsExternal || (nativeCandidate != null && IdCmp.Equals(dev.Id, nativeCandidate)));
            bool isMirror = hasApp && isMember && !isNative;
            string pidKey = "apps.out.dev." + dev.Id;
            DrawQuickDevButton(g, t, hit, pidKey, pr, dev.ShortLabel, isNative, isMirror, hasApp, dev.Label);
            var devCopy = dev;
            if (hasApp) _clicks[pidKey] = () => HandleOutputPillClick(devCopy.Id, outChoices);
        }

        if (_actions.Config.ExperimentalMic)
        {
            var inCol = new UiRect(outCol.Right + 8f, colTop, routeColW, c2In.Bottom - colTop);
            bool inEnabled = hasApp && !app!.IsExternal;
            g.DrawText(L10n.T("Apps.Input"), new UiRect(inCol.X, inCol.Y, inCol.Width, 18f), t.TextSecondary, 13f, 600);
            _inputComboRect = new UiRect(inCol.X, inCol.Y + 22f, inCol.Width, 36f);
            string currentInLabel = hasApp ? DeviceLabel(_nativeInput, Flow.Capture) : "";
            UiPrimitives.DrawComboBox(g, t, hit, "apps.in.combo", _inputComboRect, currentInLabel, enabled: inEnabled);
            if (inEnabled) _clicks["apps.in.combo"] = () => OpenInputCombo(inChoices);

            float inPillsOriginY = _inputComboRect.Bottom + 5f;
            for (int i = 0; i < inChoices.Length; i++)
            {
                var dev = inChoices[i];
                var pr = inPillRects[i].Offset(inCol.X, inPillsOriginY);
                bool isSel = inEnabled && IdCmp.Equals(dev.Id, _nativeInput);
                string iidKey = "apps.in.dev." + dev.Id;
                DrawQuickDevButton(g, t, hit, iidKey, pr, dev.ShortLabel, isSel, false, inEnabled, dev.Label);
                var devCopy = dev;
                if (inEnabled) _clicks[iidKey] = () => SelectInputRoute(devCopy.Id);
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
        float muteW = MathF.Max(78f, D2dSharedContext.Instance.MeasureText(muteText, UiMetrics.FontFamily, 12f, _selectedMuted ? 600 : 400, 120f, 28f, false).Width + 40f);
        var muteBtnRect = new UiRect(c3In.X, volRowY + 2f, muteW, 28f);
        UiPrimitives.DrawSecondaryButton(
            g, t, hit, "apps.mute", muteBtnRect, muteText,
            enabled: volReady, fontSize: 12f,
            icon: _selectedMuted ? UiIcon.VolumeMutedSlash : UiIcon.Speaker,
            active: _selectedMuted, tooltip: L10n.T("Apps.MuteTitle"));
        if (volReady) _clicks["apps.mute"] = ToggleSelectedAppMute;

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
        UiPrimitives.DrawSwitch(g, t, hit, "apps.ctrl.disableAuto", new UiRect(sw1Row.Right - 36f, sw1Row.CenterY - 12f, 34f, 24f), disableAuto, hasProcName);
        if (hasProcName) _clicks["apps.ctrl.disableAuto"] = ToggleDisableAutoSwitch;

        bool showInPanel = !hasProcName || !_actions.Config.HiddenPanelApps.Contains(procName!, IdCmp);
        var sw2Row = new UiRect(sw1Row.Right + 8f, ctrlY, ctrlColW, 32f);
        hit.Add("apps.ctrl.showPanel", sw2Row, UiCursorKind.Hand);
        g.DrawText(L10n.T("Apps.PanelVisible"), new UiRect(sw2Row.X, sw2Row.Y, sw2Row.Width - 40f, sw2Row.Height),
            hasProcName ? t.TextPrimary : t.TextSecondary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center, wrap: true, ellipsis: false);
        UiPrimitives.DrawSwitch(g, t, hit, "apps.ctrl.showPanel", new UiRect(sw2Row.Right - 36f, sw2Row.CenterY - 12f, 34f, 24f), showInPanel, hasProcName);
        if (hasProcName) _clicks["apps.ctrl.showPanel"] = ToggleShowInQuickPanel;

        var resetRouteBtn = new UiRect(sw2Row.Right + 8f, ctrlY + 2f, ctrlColW, 28f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "apps.ctrl.resetRoute", resetRouteBtn, L10n.T("Dev.SystemDefault"), enabled: hasApp, fontSize: 12f, icon: UiIcon.Reset, tooltip: L10n.T("Act.ResetAllApps"));
        if (hasApp) _clicks["apps.ctrl.resetRoute"] = ResetSelectedAppRoutes;

        hit.PopClip();
        g.PopClip();
        _detailsScroll.Draw(g, t, hit, "apps.details.scroll", viewport);
    }

    private static void DrawQuickDevButton(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiRect rect,
        string label, bool isNative, bool isMirror, bool enabled, string? tooltip = null)
    {
        hit.Add(id, rect, enabled ? UiCursorKind.Hand : UiCursorKind.Arrow, tooltip ?? label);
        bool hot = enabled && hit.IsHover(id);

        UiColor bg = isNative ? t.DeviceNativeBg : isMirror ? t.Overlay : t.SurfaceBg;
        UiColor fg = isNative ? t.DeviceNativeFg : t.TextPrimary;
        UiColor border = isNative ? t.Accent : isMirror ? t.DeviceMirrorBorder : hot ? t.Accent : t.Border;

        g.FillRoundedRect(rect, 9f, bg);
        g.DrawRoundedRect(rect, 9f, border, 1f);
        g.DrawText(label, rect.Deflate(10f, 0f), fg, 12.5f, isNative ? 600 : 400, UiTextAlign.Center, UiVertAlign.Center);
    }

    private void DrawAppIcon(D2dTarget g, UiThemePalette t, AudioAppInfo app, UiRect rect)
    {
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

        var resolved = NativeAppIconService.GetAppIcon(
            _actions.Config,
            (int)app.ProcessId,
            app.IconPath,
            app.ProcessName,
            () => _actions.Host.TryPost(() =>
            {
                if (_active) _actions.Invalidate();
            }));

        if (resolved is { } nb)
        {
            string customFile = !string.IsNullOrWhiteSpace(app.ProcessName)
                ? (AppIconFiles.GetFileName(_actions.Config, app.ProcessName!) ?? "")
                : "";
            string key = customFile.Length > 0
                ? "app.custom." + customFile
                : "app.native." + (app.IconPath ?? app.ProcessName ?? app.Identity);
            g.DrawBgraBitmap(key, nb.BgraPixels, nb.Width, nb.Height, rect);
            return;
        }

        g.FillRoundedRect(rect, 6f, t.NavSelected);
        g.DrawRoundedRect(rect, 6f, t.Accent.WithAlphaFraction(0.35f), 1f);
        string lbl = GetAppDisplayLabel(app);
        string initial = lbl.Length > 0 ? StringInfo.GetNextTextElement(lbl).ToUpperInvariant() : "?";
        g.DrawText(initial, rect, t.Accent, rect.Height * 0.48f, 600, UiTextAlign.Center, UiVertAlign.Center);
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

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        if (e.Button == UiMouseButton.Right)
        {
            if (targetId != null && _appRows.TryGetValue(targetId, out var clickedApp))
            {
                if (!_multiSelected.Contains(clickedApp.Identity)) SelectSingleApp(clickedApp);
                ShowAppContextMenu(clickedApp);
                return true;
            }
#if SONICROUTE_PHONE
            if (_showingPhone && _phonePage != null) return _phonePage.OnPointerDown(e, targetId);
#endif
            return false;
        }

        if (e.Button != UiMouseButton.Left) return false;

        if (targetId == "apps.list.scroll" && _appsScroll.BeginDrag(e.Y)) return true;
        if (targetId == "apps.details.scroll" && _detailsScroll.BeginDrag(e.Y)) return true;
#if SONICROUTE_PHONE
        if (targetId == "apps.phone.scroll" && _phoneScroll.BeginDrag(e.Y)) return true;
#endif

        if (_selectedApp != null && _selectedVolume >= 0 && targetId == "apps.vol.slider")
        {
            _draggingVolume = true;
            _dragVolumeVal = UiPrimitives.SliderValueFromX(_volumeSliderRect, e.X);
            ApplySliderVolume(_dragVolumeVal);
            return true;
        }

        if (targetId != null && _appRows.TryGetValue(targetId, out var app))
        {
            HandleAppRowClick(app, e.Ctrl, e.Shift);
            return true;
        }

#if SONICROUTE_PHONE
        if (_showingPhone && _phonePage != null)
            return _phonePage.OnPointerDown(e, targetId);
#endif
        return false;
    }

    public bool ActivateFocused(UiPointerEventArgs e, string id)
    {
        if (!_appRows.TryGetValue(id, out var app)) return OnPointerUp(e, id);
        HandleAppRowClick(app, e.Ctrl, e.Shift);
        return true;
    }

    public bool OnFocusedKey(string id, int vk)
    {
        if (id != "apps.vol.slider" || _selectedApp == null || _selectedVolume < 0 ||
            !ViewSliderKeys.TryChange(vk, _selectedVolume, 0, 100, 1, 10, out double next)) return false;
        ApplySliderVolume((int)next);
        _actions.Host.FlushVolumeWrites();
        return true;
    }

    public bool OnPointerUp(UiPointerEventArgs e, string? clickId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        bool wasDraggingVol = _draggingVolume;
        bool wasDragging = wasDraggingVol || _appsScroll.Dragging || _detailsScroll.Dragging;
        _draggingVolume = false;
        _appsScroll.EndDrag();
        _detailsScroll.EndDrag();
#if SONICROUTE_PHONE
        wasDragging |= _phoneScroll.Dragging;
        _phoneScroll.EndDrag();
#endif
        if (wasDraggingVol)
            _actions.Host.FlushVolumeWrites();

        if (wasDragging) return true;

        if (clickId != null && _clicks.TryGetValue(clickId, out var click))
        {
            click();
            return true;
        }

#if SONICROUTE_PHONE
        if (_showingPhone && _phonePage != null)
            return _phonePage.OnPointerUp(e, clickId);
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

    private void TriggerRefresh(bool forceAppsRefresh)
    {
        if (!_active) return;
        if (_refreshing)
        {
            _refreshPending = true;
            return;
        }
        _refreshing = true;
        _refreshPending = false;

        int gen = _stateGen;
        long volumeAtRead = _actions.Host.AppVolumeRevision;
        string? targetIdentity = _selectedIdentity;
        var selectedSnapshot = _selectedApp;
        bool readMic = _actions.Config.ExperimentalMic;
        bool readPeak = _actions.Config.ShowAppPeakMeter && selectedSnapshot is { IsExternal: false, HasActiveSession: true };

        _ = Task.Run(() =>
        {
            try
            {
                var outs = _actions.Host.Audio.RenderDevicesWithPhone();
                var ins = readMic ? ProductAudio.Devices(Flow.Capture) : Array.Empty<Endpoint>();
                var apps = _actions.Host.Audio.Apps(forceAppsRefresh);
                var mutedMap = new Dictionary<string, bool>(apps.Count, StringComparer.Ordinal);
                foreach (var a in apps)
                {
                    try { mutedMap[a.Identity] = _actions.Host.Audio.Volume(a).Muted; }
                    catch { mutedMap[a.Identity] = false; }
                }

                int vol = -1;
                bool muted = false;
                float peak = 0f;
                string natOut = AudioService.SystemDefaultDeviceId;
                string natIn = AudioService.SystemDefaultDeviceId;
                HashSet<string> selOuts = new(IdCmp) { AudioService.SystemDefaultDeviceId };

                var liveSelected = targetIdentity != null ? apps.FirstOrDefault(a => a.Identity == targetIdentity) : null;
                if (liveSelected != null)
                {
                    bool pendingVol = _actions.Host.HasPendingVolume(liveSelected);
                    if (!pendingVol)
                    {
                        try
                        {
                            var vs = _actions.Host.Audio.Volume(liveSelected);
                            vol = vs.Percent;
                            muted = vs.Muted;
                        }
                        catch { }
                    }
                    try
                    {
                        string? persisted = _actions.Host.Audio.ReadRoute(liveSelected, Flow.Render);
                        natOut = string.IsNullOrWhiteSpace(persisted) || AudioService.IsSystemDefault(persisted)
                            ? AudioService.SystemDefaultDeviceId
                            : AudioPolicyConfig.UnpackDeviceId(persisted);
                        selOuts = new HashSet<string>(_actions.Host.Audio.GetSelectedOutputs(liveSelected), IdCmp);
                        if (selOuts.Count == 0) selOuts.Add(natOut);
                    }
                    catch { }

                    if (readMic && !liveSelected.IsExternal)
                    {
                        try
                        {
                            string? persistedIn = _actions.Host.Audio.ReadRoute((int)liveSelected.ProcessId, Flow.Capture);
                            natIn = string.IsNullOrWhiteSpace(persistedIn) || AudioService.IsSystemDefault(persistedIn)
                                ? AudioService.SystemDefaultDeviceId
                                : AudioPolicyConfig.UnpackDeviceId(persistedIn);
                        }
                        catch { }
                    }

                    if (readPeak && !liveSelected.IsExternal)
                    {
                        try
                        {
                            int pid = (int)liveSelected.ProcessId;
                            var pmap = _actions.Host.Audio.ReadPeaks(new[] { pid });
                            peak = pmap.GetValueOrDefault(pid);
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
                        if (liveSelected != null)
                        {
                            _selectedApp = liveSelected;
                            if (vol >= 0 && !_draggingVolume && volumeAtRead == _actions.Host.AppVolumeRevision && !_actions.Host.HasPendingVolume(liveSelected)) _selectedVolume = vol;
                            if (vol >= 0 && !_draggingVolume) _selectedMuted = muted;
                            _selectedPeak = peak;
                            _nativeOutput = natOut;
                            if (_pendingInput == null) _nativeInput = natIn;
                            _selectedOutputs = selOuts;
                            if (liveSelected.HasActiveSession && !liveSelected.IsExternal && _actions.Config.ShowAppPeakMeter)
                                KickPeakBurst();
                            else
                                StopPeakBurst();
                        }
                        else if (_selectedApp != null)
                        {
                            _selectedApp = null;
                            _selectedIdentity = null;
                            _selectedVolume = -1;
                            _pendingInput = null;
                            _pendingOutputs = null;
                            _pendingPrimary = null;
                            StopPeakBurst();
                        }
                    }

                    _actions.Invalidate();
                    if (_refreshPending) TriggerRefresh(forceAppsRefresh: false);
                });
            }
            catch
            {
                _actions.Host.TryPost(() =>
                {
                    _refreshing = false;
                    if (_active && _refreshPending) TriggerRefresh(forceAppsRefresh: false);
                });
            }
        });
    }

    private void KickPeakBurst()
    {
        if (!_active || !_actions.Config.ShowAppPeakMeter || _selectedApp is not { IsExternal: false, HasActiveSession: true })
        {
            StopPeakBurst();
            return;
        }
        _peakBurstRemaining = PeakBurstMaxTicks;
        ScheduleNextPeakTick();
    }

    private void ScheduleNextPeakTick()
    {
        if (_peakTimer != null || !_active || _peakBurstRemaining <= 0
            || !_actions.Config.ShowAppPeakMeter
            || _selectedApp is not { IsExternal: false, HasActiveSession: true })
        {
            return;
        }
        _peakTimer = _actions.Host.ScheduleOnce(TimeSpan.FromMilliseconds(120), OnPeakTick);
    }

    private void StopPeakBurst()
    {
        _peakTimer?.Dispose();
        _peakTimer = null;
        _peakBurstRemaining = 0;
        _selectedPeak = 0f;
    }

    private void OnPeakTick()
    {
        _peakTimer = null;
        if (!_active || _peakInFlight || _peakBurstRemaining <= 0
            || !_actions.Config.ShowAppPeakMeter
            || _selectedApp is not { IsExternal: false, HasActiveSession: true } app)
        {
            return;
        }

        _peakBurstRemaining--;
        int pid = (int)app.ProcessId;
        _peakInFlight = true;

        _ = Task.Run(() =>
        {
            float read = 0f;
            try { read = _actions.Host.Audio.ReadPeaks(new[] { pid }).GetValueOrDefault(pid); }
            catch { }

            _actions.Host.TryPost(() =>
            {
                _peakInFlight = false;
                if (!_active || _selectedApp?.ProcessId != (uint)pid) return;
                float next = read >= _selectedPeak ? read : _selectedPeak * 0.72f;
                if (next < 0.005f) next = 0f;
                if (MathF.Abs(next - _selectedPeak) > 0.008f)
                {
                    _selectedPeak = next;
                    _actions.Invalidate();
                }
                if (_peakBurstRemaining > 0 && (_selectedPeak > 0.01f || read > 0.01f))
                    ScheduleNextPeakTick();
            });
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
        _outputEditGen++;
        _inputEditGen++;
        _pendingInput = null;
        _selectedIdentity = app.Identity;
        _selectedApp = app;
        _selectedVolume = -1;
        _selectedMuted = _mutedByIdentity.GetValueOrDefault(app.Identity);
        _selectedPeak = 0f;
        _pendingOutputs = null;
        _pendingPrimary = null;
        _outputRangeAnchor = null;
        StopPeakBurst();
        _actions.Invalidate();
        TriggerRefresh(forceAppsRefresh: false);
    }

    private void ClearAppSelection()
    {
        _stateGen++;
        _outputEditGen++;
        _inputEditGen++;
        _pendingInput = null;
        _selectedIdentity = null;
        _selectedApp = null;
        _selectedVolume = -1;
        _selectedMuted = false;
        _pendingOutputs = null;
        _pendingPrimary = null;
        StopPeakBurst();
        _actions.Invalidate();
    }

    private void ShowAppContextMenu(AudioAppInfo primary)
    {
        var selectedApps = _apps.Where(a => _multiSelected.Contains(a.Identity)).ToList();
        if (selectedApps.Count == 0) selectedApps.Add(primary);
        bool multi = selectedApps.Count > 1;
        bool primaryMuted = _mutedByIdentity.GetValueOrDefault(primary.Identity);

        var items = new List<UiMenuItem>();
        if (!multi && !string.IsNullOrWhiteSpace(primary.ProcessName))
        {
            items.Add(new UiMenuItem { Text = L10n.T("Apps.Rename"), Icon = UiIcon.Edit, Click = BeginInlineRename });
        }

        items.Add(new UiMenuItem
        {
            Text = primaryMuted ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute"),
            Icon = UiIcon.Speaker,
            Click = () => BatchToggleMute(selectedApps, !primaryMuted),
        });

        var withProc = selectedApps.Where(a => !string.IsNullOrWhiteSpace(a.ProcessName)).ToList();
        if (withProc.Count > 0)
        {
            bool allAutoDisabled = withProc.All(a => _actions.Config.DisabledAutoSwitchApps.Contains(a.ProcessName!, IdCmp));
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.DisableAuto"),
                Checked = allAutoDisabled,
                Click = () => SetBatchMembership(withProc, _actions.Config.DisabledAutoSwitchApps, !allAutoDisabled),
            });

            bool allShownInPanel = withProc.All(a => !_actions.Config.HiddenPanelApps.Contains(a.ProcessName!, IdCmp));
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.ShowInPanel"),
                Checked = allShownInPanel,
                Click = () => SetBatchMembership(withProc, _actions.Config.HiddenPanelApps, allShownInPanel),
            });
        }

        if (_actions.Config.HiddenPanelApps.Count > 0)
        {
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.ShowInPanel") + " (" + L10n.T("St.SelectAll") + ")",
                Icon = UiIcon.Refresh,
                Click = UnhideAllPanelApps,
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

    private void UnhideAllPanelApps()
    {
        if (_actions.Config.HiddenPanelApps.Count == 0) return;
        _actions.Config.HiddenPanelApps.Clear();
        if (_actions.CommitSettings()) _actions.NotifySettingsChanged();
    }

    private void BatchToggleMute(List<AudioAppInfo> targets, bool targetMute)
    {
        _ = Task.Run(() =>
        {
            int failed = 0;
            foreach (var app in targets)
            {
                try
                {
                    var actual = _actions.Host.Audio.Volume(app, mute: targetMute);
                    if (actual.Count == 0 || actual.Muted != targetMute) failed++;
                }
                catch (Exception ex)
                {
                    ExceptionDiagnostics.Record("Native.Apps.BatchMute", ex);
                    failed++;
                }
            }
            CompleteAudioBatch(targets.Count, failed);
        });
    }

    private void SetBatchMembership(List<AudioAppInfo> targets, List<string> configList, bool include)
    {
        foreach (var app in targets)
        {
            string pn = app.ProcessName!;
            if (!include) configList.RemoveAll(x => IdCmp.Equals(x, pn));
            else if (!configList.Contains(pn, IdCmp)) configList.Add(pn);
        }
        if (_actions.CommitSettings()) _actions.NotifySettingsChanged();
    }

    private async void BatchResetRoutes(List<AudioAppInfo> targets)
    {
        bool resetMic = _actions.Config.ExperimentalMic;
        async Task<bool> ResetAsync(AudioAppInfo app)
        {
            try
            {
                Task<ControlReply> output;
#if SONICROUTE_PHONE
                if (app.IsExternal)
                    output = QueueExternalOutputs(app, [AudioService.SystemDefaultDeviceId]);
                else
#endif
                    output = _actions.Host.CurrentApp.SelectOutputAsync((int)app.ProcessId, AudioService.SystemDefaultDeviceId);
                var input = !app.IsExternal && resetMic ? QueueInputRoute((int)app.ProcessId, null) : Task.FromResult<string?>(null);
                var reply = await output.ConfigureAwait(false);
                return await input.ConfigureAwait(false) == null && !reply.Text.StartsWith("error", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                ExceptionDiagnostics.Record("Native.Apps.BatchReset", ex);
                return false;
            }
        }

        var results = await Task.WhenAll(targets.Select(ResetAsync)).ConfigureAwait(false);
        CompleteAudioBatch(results.Length, results.Count(ok => !ok));
    }

    private void CompleteAudioBatch(int count, int failed)
    {
        _actions.Host.TryPost(() =>
        {
            if (_active && failed > 0) _actions.ShowNotice(string.Format(L10n.T("Auto.RunPartial"), count - failed, failed));
            _actions.Host.NotifyAudioVisualChanged();
        });
    }

    private void BeginInlineRename()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        _aliasEditor.Open("apps.rename", _renameTitleRect, proc, device: false, displayName: GetAppDisplayLabel(_selectedApp));
    }

    private void ImportCustomIcon()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string? path = NativeDialogs.Open(_actions.OwnerWindow.Handle, "PNG / JPEG / ICO / EXE|*.png;*.jpg;*.jpeg;*.ico;*.exe");
        if (!string.IsNullOrWhiteSpace(path))
            ImportCustomIconFromPath(path);
    }

    private void ImportCustomIconFromPath(string path)
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        if (_actions.Config.AppIcons.Count >= AppIconFiles.MaxIcons && !_actions.Config.AppIcons.ContainsKey(proc))
        {
            _actions.ShowNotice(L10n.T("AppIcons.Limit"));
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 32 * 1024 * 1024)
            {
                _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
                return;
            }

            byte[] bgra;
            int w, h;
            if (IdCmp.Equals(Path.GetExtension(path), ".exe"))
            {
                if (!TryExtractExeIconBgra(path, out bgra, out w, out h))
                {
                    _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
                    return;
                }
            }
            else
            {
                if (info.Length > 8 * 1024 * 1024)
                {
                    _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
                    return;
                }
                byte[] raw = File.ReadAllBytes(path);
                if (!SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(raw, AppIconFiles.IconSize, AppIconFiles.IconSize, out bgra, out w, out h)
                    || w != AppIconFiles.IconSize || h != AppIconFiles.IconSize)
                {
                    _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
                    return;
                }
            }

            byte[] png64 = EncodePng64(bgra, w, h);
            string? previous = AppIconFiles.GetFileName(_actions.Config, proc);
            string savedName = AppIconFiles.SavePngIfChanged(png64, out bool changed);
            if (!ConfigService.TrySetAppIcon(proc, savedName))
            {
                _actions.ShowNotice(L10n.T("AppIcons.SaveFailed"));
                return;
            }
            if (!string.IsNullOrEmpty(previous)) NativeAppIconService.InvalidateCustomIcon(previous);
            if (changed) NativeAppIconService.InvalidateCustomIcon(savedName);
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
        string? previous = AppIconFiles.GetFileName(_actions.Config, proc);
        if (!ConfigService.TrySetAppIcon(proc, null))
        {
            _actions.ShowNotice(L10n.T("AppIcons.SaveFailed"));
            return;
        }
        if (!string.IsNullOrEmpty(previous)) NativeAppIconService.InvalidateCustomIcon(previous);
        AppIconFiles.CleanupUnused(_actions.Config);
        _actions.Host.ViewSettingsChanged();
        _actions.ShowNotice(L10n.T("AppIcons.ResetDone"));
    }

    private void OpenOutputCombo(DeviceChoice[] choices)
    {
        if (_selectedApp == null) return;
        var list = choices.Select(c => new ComboChoice(c.Id, c.Label)).ToArray();
        _actions.ShowCombo(_outputComboRect, list, GetDisplayedPrimaryOutput(), picked =>
        {
            ApplyOutputSelection(_selectedApp, choices, picked.Key, ctrl: false, shift: false);
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
        if (_selectedApp == null) return;
        bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
        bool shift = (GetKeyState(0x10) & 0x8000) != 0;
        ApplyOutputSelection(_selectedApp, choices, deviceId, ctrl, shift);
    }

    private void ApplyOutputSelection(AudioAppInfo app, DeviceChoice[] choices, string deviceId, bool ctrl, bool shift)
    {
        bool isMultiExternal = IsMultiOutputExternal(app);
        if (app.IsExternal && !isMultiExternal)
        {
            ctrl = false;
            shift = false;
        }
        if (!app.IsExternal && ctrl && shift && PcRoutingApi.IsPhoneOutputDevice(deviceId))
            return;

        string[] order = choices.Select(c => c.Id).ToArray();
        var currentSelected = _pendingOutputs ?? _selectedOutputs;
        string[] nextOutputs = SelectPlaybackRange(order, currentSelected, deviceId, _outputRangeAnchor, ctrl, shift);
        if (nextOutputs.Length == 0) nextOutputs = new[] { AudioService.SystemDefaultDeviceId };

#if SONICROUTE_PHONE
        if (app.IsExternal && _actions.Host.Audio.ExternalAudio?.FindSession(app.ExternalSourceId) is AndroidAudioSession android && android.State.Device is { } sourceDevice)
        {
            bool OwnForcedOutput(string id) => AndroidOutputIdentity.TryParseDeviceId(id, out var key, out _)
                && string.Equals(key, AndroidDeviceIdentity.Key(sourceDevice), StringComparison.OrdinalIgnoreCase);
            if (nextOutputs.Contains(AndroidAudioSession.SourcePhoneOutputId, IdCmp) && nextOutputs.Any(OwnForcedOutput))
            {
                nextOutputs = IdCmp.Equals(deviceId, AndroidAudioSession.SourcePhoneOutputId)
                    ? nextOutputs.Where(id => !OwnForcedOutput(id)).ToArray()
                    : nextOutputs.Where(id => !IdCmp.Equals(id, AndroidAudioSession.SourcePhoneOutputId)).ToArray();
                if (nextOutputs.Length == 0) nextOutputs = new[] { AudioService.SystemDefaultDeviceId };
            }
        }
#endif

        _outputRangeAnchor = shift && !ctrl ? (_outputRangeAnchor ?? deviceId) : deviceId;
        string? explicitPrimary = ctrl && shift ? deviceId
            : !ctrl && !shift ? (!PcRoutingApi.IsPhoneOutputDevice(deviceId) ? deviceId : null)
            : _pendingPrimary != null && nextOutputs.Contains(_pendingPrimary, IdCmp) ? _pendingPrimary : null;

        string preferred = SelectOptimisticPrimaryOutput(
            nextOutputs,
            explicitPrimary ?? _nativeOutput,
            preferPhysical: !app.IsExternal,
            lowPriorityId: LowPriorityOutputId(app));

        _pendingOutputs = new HashSet<string>(nextOutputs, IdCmp);
        _pendingPrimary = preferred;
        int editGen = ++_outputEditGen;
        _actions.Invalidate();

        Task<ControlReply> write;
#if SONICROUTE_PHONE
        if (app.IsExternal)
            write = QueueExternalOutputs(app, nextOutputs);
        else
#endif
        {
            int pid = (int)app.ProcessId;
            // Enqueue on the input thread so single clicks and Ctrl/Shift requests reach the
            // same per-process owner in the order they were made.
            write = !ctrl && !shift && !PcRoutingApi.IsPhoneOutputDevice(deviceId)
                ? _actions.Host.CurrentApp.SelectOutputAsync(pid, deviceId)
                : _actions.Host.CurrentApp.SetOutputsAsync(pid, preferred, nextOutputs);
        }
        _ = FinishOutputAsync(write, app.Identity, editGen);
    }

#if SONICROUTE_PHONE
    private Task<ControlReply> QueueExternalOutputs(AudioAppInfo app, string[] outputs)
    {
        var previous = _externalOutputs;
        var write = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try { return await _actions.Host.Audio.SetExternalOutputsAsync(app, outputs).ConfigureAwait(false); }
            catch (Exception ex) { return new ControlReply("error: " + ex.Message); }
        });
        _externalOutputs = write;
        return write;
    }
#endif

    private async Task FinishOutputAsync(Task<ControlReply> write, string identity, int generation)
    {
        string? error = null;
        try
        {
            var reply = await write.ConfigureAwait(false);
            if (reply.Text.StartsWith("error", StringComparison.OrdinalIgnoreCase)) error = reply.Text;
        }
        catch (Exception ex) { error = ex.Message; }
        _actions.Host.TryPost(() =>
        {
            if (generation != _outputEditGen || identity != _selectedIdentity) return;
            if (error == null && _pendingOutputs != null)
            {
                _selectedOutputs = _pendingOutputs;
                _nativeOutput = _pendingPrimary ?? AudioService.SystemDefaultDeviceId;
            }
            _pendingOutputs = null;
            _pendingPrimary = null;
            _stateGen++; // discard an older in-flight endpoint read, then read the settled state
            if (!_active) return;
            if (error != null) _actions.ShowNotice(error);
            TriggerRefresh(forceAppsRefresh: false);
        });
    }

    private static string[] SelectPlaybackRange(string[] order, HashSet<string> selected, string clicked, string? anchor, bool ctrl, bool shift)
    {
        if (!shift && !ctrl) return new[] { clicked };
        var result = new HashSet<string>(selected, IdCmp);
        if (shift && ctrl)
        {
            result.Add(clicked);
        }
        else if (shift)
        {
            anchor ??= order.FirstOrDefault(result.Contains);
            int start = Array.FindIndex(order, id => IdCmp.Equals(id, anchor));
            int end = Array.FindIndex(order, id => IdCmp.Equals(id, clicked));
            if (start < 0 || end < 0) result.Add(clicked);
            else for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) result.Add(order[i]);
        }
        else if (!result.Remove(clicked))
        {
            result.Add(clicked);
        }
        var visible = order.Where(result.Contains);
        return visible.Concat(selected.Where(id => result.Contains(id) && !order.Contains(id, IdCmp))).ToArray();
    }

    private static string SelectOptimisticPrimaryOutput(IReadOnlyList<string> outputs, string? currentOutputId, bool preferPhysical, string? lowPriorityId = null)
    {
        if (outputs.Count == 0) return AudioService.SystemDefaultDeviceId;

        string? Pick(bool filtered)
        {
            string? first = null, matchedCurrent = null, systemDefault = null;
            for (int i = 0; i < outputs.Count; i++)
            {
                string id = outputs[i];
                if (filtered)
                {
                    if (preferPhysical && PcRoutingApi.IsPhoneOutputDevice(id)) continue;
                    if (lowPriorityId != null && IdCmp.Equals(id, lowPriorityId)) continue;
                }
                if (!preferPhysical && AudioService.IsSystemDefault(id)) return id;
                first ??= id;
                if (AudioService.IsSystemDefault(id)) systemDefault ??= id;
                if (currentOutputId != null && IdCmp.Equals(id, currentOutputId)) matchedCurrent = id;
            }
            return matchedCurrent ?? systemDefault ?? first;
        }

        return Pick(true) ?? Pick(false) ?? AudioService.SystemDefaultDeviceId;
    }

    private bool IsMultiOutputExternal(AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        return app.IsExternal && _actions.Host.Audio.ExternalAudio?.FindSession(app.ExternalSourceId) is IMultiOutputAudioSession;
#else
        return false;
#endif
    }

    private string? LowPriorityOutputId(AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        return app.IsExternal && _actions.Host.Audio.ExternalAudio?.FindSession(app.ExternalSourceId) is AndroidAudioSession
            ? AndroidAudioSession.SourcePhoneOutputId
            : null;
#else
        return null;
#endif
    }

    private string GetDisplayedPrimaryOutput()
    {
        if (_pendingOutputs is { Count: > 0 } && _selectedApp != null)
        {
            return SelectOptimisticPrimaryOutput(
                _pendingOutputs.ToArray(),
                _pendingPrimary ?? _nativeOutput,
                preferPhysical: !_selectedApp.IsExternal,
                lowPriorityId: LowPriorityOutputId(_selectedApp));
        }
        return _nativeOutput;
    }

    private void SelectInputRoute(string deviceId)
    {
        if (_selectedApp == null || _selectedApp.IsExternal) return;
        int pid = (int)_selectedApp.ProcessId;
        string identity = _selectedApp.Identity;
        int generation = ++_inputEditGen;
        _pendingInput = _nativeInput = deviceId;
        _actions.Invalidate();

        _ = FinishInputAsync(QueueInputRoute(pid, AudioService.IsSystemDefault(deviceId) ? null : deviceId), identity, generation);
    }

    private Task<string?> QueueInputRoute(int pid, string? deviceId)
    {
        Task previous = _inputWrites;
        var write = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try { _actions.Host.Audio.SetRoute(pid, Flow.Capture, deviceId); return (string?)null; }
            catch (Exception ex) { return ex.Message; }
        });
        _inputWrites = write;
        return write;
    }

    private async Task FinishInputAsync(Task<string?> write, string identity, int generation)
    {
        string? error = await write.ConfigureAwait(false);
        _actions.Host.TryPost(() =>
        {
            if (generation != _inputEditGen || _selectedIdentity != identity) return;
            _pendingInput = null;
            _stateGen++;
            if (!_active) return;
            if (error != null) _actions.ShowNotice(error);
            TriggerRefresh(forceAppsRefresh: false);
        });
    }

    private void ToggleSelectedAppMute()
    {
        if (_selectedApp == null) return;
        var app = _selectedApp;
        _ = Task.Run(() =>
        {
            try
            {
                var vs = _actions.Host.Audio.Volume(app, toggle: true);
                _actions.Host.TryPost(() =>
                {
                    if (_selectedApp?.Identity == app.Identity)
                    {
                        _selectedMuted = vs.Muted;
                        if (vs.Percent >= 0) _selectedVolume = vs.Percent;
                        _mutedByIdentity[app.Identity] = vs.Muted;
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
        if (_selectedApp == null) return;
        var app = _selectedApp;
        _selectedVolume = percent;

        _actions.Host.QueueAppVolume(app, percent);
        if (!app.IsExternal) KickPeakBurst();
        _actions.Invalidate();
    }

    internal void ConfirmAppVolume(string identity, int actual)
    {
        if (!_active || _selectedApp?.Identity != identity || _draggingVolume || _selectedVolume == actual) return;
        _selectedVolume = actual;
        _actions.Invalidate();
    }

    private void ToggleDisableAutoSwitch()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        var list = _actions.Config.DisabledAutoSwitchApps;
        if (list.RemoveAll(x => IdCmp.Equals(x, proc)) == 0) list.Add(proc);
        if (_actions.CommitSettings()) _actions.NotifySettingsChanged();
    }

    private void ToggleShowInQuickPanel()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        var list = _actions.Config.HiddenPanelApps;
        if (list.RemoveAll(x => IdCmp.Equals(x, proc)) == 0) list.Add(proc);
        if (_actions.CommitSettings()) _actions.NotifySettingsChanged();
    }

    private void ResetSelectedAppRoutes()
    {
        if (_selectedApp == null) return;
        BatchResetRoutes(new List<AudioAppInfo> { _selectedApp });
    }

    private readonly record struct DeviceChoice(string Id, string Label)
    {
        public string ShortLabel { get; } = UiPrimitives.ShortDeviceName(Label);
    }

    private DeviceChoice[] BuildOutputChoices()
    {
        var cfg = _actions.Config;
        var active = _pendingOutputs ?? _selectedOutputs;
        var list = new List<DeviceChoice>();
        if (!cfg.HiddenOutputDevices.Contains(AudioService.SystemDefaultDeviceId)
            || IdCmp.Equals(_nativeOutput, AudioService.SystemDefaultDeviceId)
            || active.Contains(AudioService.SystemDefaultDeviceId))
        {
            list.Add(new DeviceChoice(AudioService.SystemDefaultDeviceId, L10n.T("Dev.SystemDefaultOut")));
        }

        foreach (var ep in _outputs)
        {
            if (cfg.HiddenOutputDevices.Contains(ep.Id) && !IdCmp.Equals(_nativeOutput, ep.Id) && !active.Contains(ep.Id))
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
            float textW = ctx.MeasureText(choices[i].ShortLabel, UiMetrics.FontFamily, 12.5f, 400, MathF.Max(60f, maxWidth - 24f), rowH, false).Width;
            float pillW = MathF.Min(maxWidth, MathF.Ceiling(textW) + 26f);
            if (x > 0f && x + pillW + gapX > maxWidth)
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

    private static bool TryExtractExeIconBgra(string exePath, out byte[] bgra64, out int outW, out int outH)
    {
        bgra64 = Array.Empty<byte>();
        outW = outH = 0;
        var shfi = new ShFileInfo();
        const uint flags = 0x000000100 /*SHGFI_ICON*/ | 0x000000000 /*SHGFI_LARGEICON*/;
        if (SHGetFileInfoW(exePath, 0, ref shfi, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == 0 || shfi.HIcon == 0)
            return false;
        try
        {
            if (!GetIconInfo(shfi.HIcon, out var ii)) return false;
            try
            {
                if (ii.HbmColor == 0) return false;
                var bmpInfo = new BitmapStruct();
                if (GetObjectW(ii.HbmColor, Marshal.SizeOf<BitmapStruct>(), ref bmpInfo) == 0) return false;
                int sw = bmpInfo.BmWidth;
                int sh = Math.Abs(bmpInfo.BmHeight);
                if (sw <= 0 || sh <= 0 || sw > 256 || sh > 256) return false;

                byte[] src = new byte[sw * sh * 4];
                var bmi = new BitmapInfoHeader
                {
                    BiSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    BiWidth = sw,
                    BiHeight = -sh,
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0,
                };
                nint hdc = GetDC(0);
                try
                {
                    var pin = GCHandle.Alloc(src, GCHandleType.Pinned);
                    try
                    {
                        if (GetDIBits(hdc, ii.HbmColor, 0, (uint)sh, pin.AddrOfPinnedObject(), ref bmi, 0) == 0)
                            return false;
                    }
                    finally { pin.Free(); }
                }
                finally { ReleaseDC(0, hdc); }

                // Bilinear/nearest resize to 64x64 BGRA
                int target = AppIconFiles.IconSize;
                byte[] dst = new byte[target * target * 4];
                for (int y = 0; y < target; y++)
                {
                    int sy = Math.Clamp(y * sh / target, 0, sh - 1);
                    for (int x = 0; x < target; x++)
                    {
                        int sx = Math.Clamp(x * sw / target, 0, sw - 1);
                        int si = (sy * sw + sx) * 4;
                        int di = (y * target + x) * 4;
                        dst[di + 0] = src[si + 0];
                        dst[di + 1] = src[si + 1];
                        dst[di + 2] = src[si + 2];
                        dst[di + 3] = src[si + 3];
                    }
                }
                bgra64 = dst;
                outW = target;
                outH = target;
                return true;
            }
            finally
            {
                if (ii.HbmColor != 0) DeleteObject(ii.HbmColor);
                if (ii.HbmMask != 0) DeleteObject(ii.HbmMask);
            }
        }
        finally
        {
            DestroyIcon(shfi.HIcon);
        }
    }

    private static byte[] EncodePng64(byte[] bgra, int w, int h)
    {
        int stride = w * 4;
        byte[] raw = new byte[(stride + 1) * h];
        for (int row = 0; row < h; row++)
        {
            int dst = row * (stride + 1);
            int src = row * stride;
            raw[dst] = 0;
            for (int col = 0; col < w; col++)
            {
                int s = src + col * 4;
                int d = dst + 1 + col * 4;
                raw[d + 0] = bgra[s + 2];
                raw[d + 1] = bgra[s + 1];
                raw[d + 2] = bgra[s + 0];
                raw[d + 3] = bgra[s + 3];
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
        ihdr[8] = 8;
        ihdr[9] = 6;
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
        _aliasEditor.Dispose();
        _externalIconCache.Clear();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint HIcon; public int IIcon; public uint DwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string SzDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string SzTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public bool FIcon; public uint XHotspot, YHotspot; public nint HbmMask, HbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapStruct { public int BmType, BmWidth, BmHeight, BmWidthBytes; public ushort BmPlanes, BmBitsPixel; public nint BmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize; public int BiWidth, BiHeight; public ushort BiPlanes, BiBitCount;
        public uint BiCompression, BiSizeImage; public int BiXPelsPerMeter, BiYPelsPerMeter; public uint BiClrUsed, BiClrImportant;
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfoW(string path, uint attrs, ref ShFileInfo psfi, uint cbSize, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint hIcon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint hIcon, out IconInfo info);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, ref BitmapStruct bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, nint bits, ref BitmapInfoHeader bmi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
}
