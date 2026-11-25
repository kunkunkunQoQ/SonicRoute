using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// High-fidelity Direct2D Quick Panel window matching WPF QuickPanelModernWindow 1.36.
/// Zero idle timers: 50ms peak meter timer runs ONLY while visible and stops immediately in OnHidden/OnClosed.
/// </summary>
internal sealed class QuickPanelWindow : LayeredWindow
{
    private sealed class AppRowState
    {
        public required AudioAppInfo App;
        public int Pid;
        public string DisplayName = "";
        public int Volume = 100;
        public bool Muted;
        public float Peak;
        public bool Expanded;
        public string NativeOutputId = AudioService.SystemDefaultDeviceId;
        public HashSet<string> SelectedOutputs = new(StringComparer.OrdinalIgnoreCase);
        public string? OutputAnchor;
        public string? CachedOutputLabel;
    }

    private readonly ApplicationHost _host;
    private readonly AppConfig _config;
    private readonly Action? _onOpenMain;
    private readonly nint _trayOwnerHwnd;
    private readonly UiHitMap _hit = new();
    private readonly UiScrollState _scroll = new();

    private UiThemePalette _theme;
    private OutputVolumesPopupWindow? _outputVolumesPopup;
#if SONICROUTE_PHONE
    private PhoneAudioWindowView? _phoneCompanion;
#endif
    private ComboPopupWindow? _comboPopup;

    private Endpoint[] _outputs = [];
    private string? _defaultOutputId;
    private string? _panelOutputId;
    private int _masterVolume = 100;
    private bool _masterMuted;
    private bool _globalMicMuted;
    private bool _micKnown;

    private List<AppRowState> _rows = new();
    private readonly Dictionary<int, bool> _expandedPids = new();
    private readonly Dictionary<int, float> _smoothedPeaks = new();

    private bool _pinned;
    private bool _loading;
    private bool _peakReadInFlight;
    private int _draggingPid = -1;
    private bool _draggingMaster;
    private UiRect _draggingSliderRect;

    private bool _resizingHeight;
    private bool _resizingTopEdge;
    private float _resizeStartMouseScreenY;
    private float _resizeStartHeightDip;
    private int _resizeStartTopPx;

    internal event Action? OnDismissRequested;
    internal bool IsPinned => _pinned;
    internal bool HasOpenModal => _comboPopup is { IsVisible: true };

    internal QuickPanelWindow(
        ApplicationHost host,
        AppConfig config,
        Action? onOpenMain = null,
        Action? onDismissRequested = null,
        nint trayOwnerHwnd = 0)
        : base(UiMetrics.QuickPanelWindowWidth, Math.Clamp(config.QuickPanelHeight, 350, 850), UiMetrics.QuickPanelWindowWidth, UiMetrics.QuickPanelMinHeight)
    {
        _host = host;
        _config = config;
        _onOpenMain = onOpenMain;
        if (onDismissRequested != null) OnDismissRequested += onDismissRequested;
        _trayOwnerHwnd = trayOwnerHwnd;
        _theme = UiThemePalette.Resolve(config);
        TopMost = true;
        ToolWindow = true;
        NoActivate = false;
    }

    internal void ApplyThemeAndSettings()
    {
        _theme = UiThemePalette.Resolve(_config);
        _outputVolumesPopup?.ApplyTheme(_theme);
#if SONICROUTE_PHONE
        _phoneCompanion?.ApplyTheme(_theme);
#endif
        if (IsVisible)
        {
            ApplyConfiguredHeight();
            PositionWindow();
            RepositionCompanions();
            RefreshData();
            Invalidate();
        }
    }

    internal void ShowQuickPanel()
    {
        _theme = UiThemePalette.Resolve(_config);
        EnsureCreated(L10n.T("Qp.Panel"));
        ApplyConfiguredHeight();
        PositionWindow();
        Show(activate: true);
        RefreshData();
        SyncCompanionsVisibility();
    }

    internal void RefreshData()
    {
        if (_loading || IsDisposed) return;
        _loading = true;
        bool needOutputName = _config.QuickPanelUseLegacyAppRows
            ? _config.QuickPanelCompactShowOutputName
            : _config.QuickPanelShowOutputName;

        _ = Task.Run(() =>
        {
            Endpoint[] render = [];
            string? defId = null;
            int masterVol = 100;
            bool masterMute = false;
            bool micMuted = false;
            bool micKnown = false;
            var newRows = new List<AppRowState>();

            try
            {
                render = ProductAudio.Devices(Flow.Render);
                defId = render.Length > 0 ? ProductAudio.DefaultId(Flow.Render) : null;
                string? targetEndpoint = _config.PanelChangeSystemDefault ? null : (_panelOutputId ?? defId);
                try
                {
                    var mv = _host.Audio.Volume(Flow.Render, targetEndpoint, 0);
                    masterVol = mv.Percent;
                    masterMute = mv.Muted;
                }
                catch { }

                if (_config.ExperimentalMic)
                {
                    try
                    {
                        var caps = ProductAudio.Devices(Flow.Capture);
                        if (caps.Length > 0)
                        {
                            micKnown = true;
                            micMuted = caps.All(d => _host.Audio.Volume(Flow.Capture, d.Id).Muted);
                        }
                    }
                    catch { }
                }

                var apps = _host.Audio.Apps();
                foreach (var app in apps)
                {
                    if (app.ProcessName is { } proc && _config.HiddenPanelApps.Contains(proc, StringComparer.OrdinalIgnoreCase))
                        continue;

                    int pid = checked((int)app.ProcessId);
                    string label = app.ProcessName is { } pn && _config.AppNames.TryGetValue(pn, out string? custom) && custom.Length > 0
                        ? custom
                        : app.Label;

                    int vol = 100;
                    bool muted = false;
                    try
                    {
                        var vs = _host.Audio.Volume(Flow.Render, null, pid);
                        vol = vs.Percent;
                        muted = vs.Muted;
                    }
                    catch { }

                    bool expanded = _expandedPids.TryGetValue(pid, out bool ex) && ex;
                    string nativeOut = AudioService.SystemDefaultDeviceId;
                    HashSet<string> selectedOuts = new(StringComparer.OrdinalIgnoreCase);

                    if (expanded || needOutputName)
                    {
                        try
                        {
                            nativeOut = _host.Audio.ReadRoute(pid, Flow.Render) ?? AudioService.SystemDefaultDeviceId;
                            foreach (var s in AppPlaybackOutputs.GetSelected(pid, nativeOut))
                                selectedOuts.Add(s);
                        }
                        catch { }
                    }
                    if (selectedOuts.Count == 0) selectedOuts.Add(nativeOut);

                    string? outLabel = null;
                    if (needOutputName)
                    {
                        outLabel = ResolveDeviceDisplayName(render, nativeOut);
                    }

                    newRows.Add(new AppRowState
                    {
                        App = app,
                        Pid = pid,
                        DisplayName = label,
                        Volume = vol,
                        Muted = muted,
                        Peak = _smoothedPeaks.GetValueOrDefault(pid),
                        Expanded = expanded,
                        NativeOutputId = nativeOut,
                        SelectedOutputs = selectedOuts,
                        CachedOutputLabel = outLabel,
                    });
                }
            }
            catch { }

            _host.TryPost(() =>
            {
                _loading = false;
                if (IsDisposed) return;
                _outputs = render;
                _defaultOutputId = defId;
                if (_panelOutputId == null || !render.Any(d => string.Equals(d.Id, _panelOutputId, StringComparison.OrdinalIgnoreCase)))
                    _panelOutputId = defId;
                _masterVolume = masterVol;
                _masterMuted = masterMute;
                _globalMicMuted = micMuted;
                _micKnown = micKnown;
                _rows = newRows;
                if (IsVisible) Invalidate();
            });
        });
    }

    protected override void OnShown()
    {
        base.OnShown();
        if (_config.ShowAppPeakMeter)
        {
            StartAnimationTimer(50);
        }
    }

    protected override void OnHidden()
    {
        StopAnimationTimer();
        CloseCombo();
        HideCompanions();
        base.OnHidden();
    }

    protected override void OnClosed()
    {
        StopAnimationTimer();
        CloseCombo();
        DisposeCompanions();
        base.OnClosed();
    }

    protected override void OnTickAnimation()
    {
        if (!IsVisible || !_config.ShowAppPeakMeter)
        {
            StopAnimationTimer();
            return;
        }
        if (_peakReadInFlight || _rows.Count == 0) return;

        int[] pids = _rows.Select(r => r.Pid).Distinct().ToArray();
        _peakReadInFlight = true;
        _ = Task.Run(() =>
        {
            Dictionary<int, float>? read = null;
            try { read = _host.Audio.ReadPeaks(pids); }
            catch { }

            _host.TryPost(() =>
            {
                _peakReadInFlight = false;
                if (!IsVisible || IsDisposed) return;
                bool changed = false;
                foreach (var row in _rows)
                {
                    float target = read != null && read.TryGetValue(row.Pid, out float v) ? v : 0f;
                    float prev = row.Peak;
                    float next = target >= prev ? target : prev * 0.72f;
                    if (next < 0.005f) next = 0f;
                    if (MathF.Abs(next - prev) > 0.008f)
                    {
                        row.Peak = next;
                        _smoothedPeaks[row.Pid] = next;
                        changed = true;
                    }
                }
                if (changed) Invalidate();
            });
        });
    }

    protected override string GetWindowTitle() => L10n.T("Qp.Panel");

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        // Surface leaves 6 DIP right margin (Width = 366 inside 372 window)
        float surfaceW = MathF.Min(UiMetrics.QuickPanelSurfaceWidth, widthDip);
        var surface = new UiRect(0f, 0f, surfaceW, heightDip);
        g.FillRoundedRect(surface, UiMetrics.QuickPanelCornerRadius, _theme.WindowBgAlpha);
        g.DrawRoundedRect(surface, UiMetrics.QuickPanelCornerRadius, _theme.Border, 1f);

        // Top & bottom resize grips (invisible hit zones)
        _hit.Add("qp.resize.top", new UiRect(12f, 0f, surfaceW - 24f, 5f), UiCursorKind.SizeNS);
        _hit.Add("qp.resize.bottom", new UiRect(12f, heightDip - 5f, surfaceW - 24f, 5f), UiCursorKind.SizeNS);

        const float padX = 16f;
        const float padTop = 14f;
        const float padBottom = 12f;
        float innerW = surfaceW - padX * 2f;

        // 1. Header Bar (exact match to QuickPanelModernWindow.xaml: Title + Version + Phone companion toggle)
        float y = padTop;
        var headerRect = new UiRect(padX, y, innerW, 28f);
        float rx = headerRect.Right;

#if SONICROUTE_PHONE
        const float phoneBtnW = 30f;
        var phoneRect = new UiRect(rx - phoneBtnW, y - 1f, phoneBtnW, 30f);
        bool phoneOpen = _phoneCompanion is { IsVisible: true };
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "qp.phone", phoneRect, "",
            radius: 8f, icon: UiIcon.Phone, active: phoneOpen,
            tooltip: L10n.T("Phone.OpenPhoneAudio"));
        rx = phoneRect.X - 6f;
#endif

        const string verLabel = "v1.36";
        float verW = D2dSharedContext.Instance.MeasureText(verLabel, UiMetrics.FontFamily, 10.5f, 400, 80f, 20f, false).Width + 4f;
        g.DrawText(verLabel, new UiRect(rx - verW, y, verW, 28f), _theme.TextSecondary, 10.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);

        var titleRect = new UiRect(padX, y, MathF.Max(40f, rx - verW - 8f - padX), 28f);
        g.DrawText(BuildIdentity.DisplayName, titleRect, _theme.TextPrimary, 14f, 700, UiTextAlign.Leading, UiVertAlign.Center);

        // 2. Output Device Selector + Expand Output Volumes Toggle
        y = headerRect.Bottom + 10f;
        string activeId = _config.PanelChangeSystemDefault ? (_defaultOutputId ?? "") : (_panelOutputId ?? _defaultOutputId ?? "");
        string devName = ResolveDeviceDisplayName(_outputs, activeId);
        const float expBtnSize = 32f;
        var outputsToggleRect = new UiRect(padX + innerW - expBtnSize, y, expBtnSize, expBtnSize);
        bool outPopOpen = _outputVolumesPopup is { IsVisible: true };
        _hit.Add("qp.outputs", outputsToggleRect, UiCursorKind.Hand, L10n.T("Qp.ExpandOutputVolumes"));
        g.FillRoundedRect(outputsToggleRect, 8f, outPopOpen ? _theme.NavSelected : _hit.IsHover("qp.outputs") ? _theme.NavHover : _theme.SurfaceBg);
        g.DrawRoundedRect(outputsToggleRect, 8f, outPopOpen || _hit.IsHover("qp.outputs") ? _theme.Accent : _theme.Border, 1f);
        g.DrawIcon(UiIcon.ChevronUp, new UiRect(outputsToggleRect.CenterX - 5f, outputsToggleRect.CenterY - 5f, 10f, 10f),
            outPopOpen ? _theme.Accent : _theme.TextSecondary, 1.8f, outPopOpen ? 180f : 0f);

        var masterComboRect = new UiRect(padX, y, MathF.Max(60f, outputsToggleRect.X - 6f - padX), 32f);
        _hit.Add("qp.masterdev", masterComboRect, UiCursorKind.Hand, devName);
        bool comboHot = _hit.IsHover("qp.masterdev");
        bool comboOpen = _comboPopup is { IsVisible: true };
        g.FillRoundedRect(masterComboRect, 8f, _theme.SurfaceBg);
        g.DrawRoundedRect(masterComboRect, 8f, comboOpen || comboHot ? _theme.Accent : _theme.Border, 1f);
        g.DrawIcon(UiIcon.Headphones, new UiRect(masterComboRect.X + 10f, masterComboRect.CenterY - 7f, 14f, 14f), _theme.TextPrimary, 1.7f);
        g.DrawText(devName, new UiRect(masterComboRect.X + 30f, masterComboRect.Y, MathF.Max(20f, masterComboRect.Width - 52f), masterComboRect.Height),
            _theme.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(masterComboRect.Right - 20f, masterComboRect.CenterY - 5.5f, 11f, 11f),
            _theme.TextSecondary, 1.8f, comboOpen ? 180f : 0f);

        // 3. Master Volume Row (Mute Pill + Slider + Percentage)
        y = masterComboRect.Bottom + 10f;
        string muteText = _masterMuted ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute");
        float muteW = Math.Clamp(D2dSharedContext.Instance.MeasureText(muteText, UiMetrics.FontFamily, 12f, _masterMuted ? 600 : 400, 120f, 28f, false).Width + 20f, 64f, 108f);
        var masterMuteRect = new UiRect(padX, y + 2f, muteW, 28f);
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "qp.mastermute", masterMuteRect, muteText,
            fontSize: 12f, radius: 7f, active: _masterMuted,
            tooltip: _masterMuted ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute"));

        var pctRect = new UiRect(padX + innerW - 42f, y, 42f, 32f);
        g.DrawText($"{_masterVolume}%", pctRect, _theme.TextPrimary, 12.5f, 600, UiTextAlign.Trailing, UiVertAlign.Center, wrap: false, ellipsis: false);

        var masterSliderRect = new UiRect(masterMuteRect.Right + 10f, y, MathF.Max(40f, pctRect.X - 6f - (masterMuteRect.Right + 10f)), 32f);
        UiPrimitives.DrawVolumeSlider(g, _theme, _hit, "qp.masterslider", masterSliderRect,
            _masterVolume / 100f, 0f, _masterMuted, showPeak: false, trackHeight: 4f, thumbDiameter: 14f);

        // 4. Divider + App Volumes Header
        y = masterSliderRect.Bottom + 10f;
        g.DrawLine(padX, y, padX + innerW, y, _theme.Border, 1f, roundCaps: false);
        y += 8f;

        // 5. Footer Bar (bottom 3-button toolbar matching QuickPanelModernWindow.xaml Row 6)
        const float footerH = 28f;
        float footerTop = heightDip - padBottom - footerH;
        g.DrawLine(padX, footerTop - 10f, padX + innerW, footerTop - 10f, _theme.Border, 1f, roundCaps: false);

        // 6. Scrollable Apps List Area
        var appsHeaderRect = new UiRect(padX, y, innerW, 18f);
        g.DrawText(L10n.T("Qp.AppVolumes"), appsHeaderRect, _theme.TextSecondary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        y = appsHeaderRect.Bottom + 6f;

        var listRect = new UiRect(padX, y, innerW, MathF.Max(36f, footerTop - 14f - y));
        bool legacyLayout = _config.QuickPanelUseLegacyAppRows;
        var visibleOutputs = _outputs.Where(d => !_config.HiddenOutputDevices.Contains(d.Id)).ToArray();

        float totalContentH = 0f;
        foreach (var row in _rows)
        {
            totalContentH += GetRowTotalHeight(row, legacyLayout, visibleOutputs.Length + 1, innerW);
        }

        _scroll.ViewportHeight = listRect.Height;
        _scroll.ContentHeight = totalContentH;
        _scroll.Clamp();

        if (_rows.Count == 0)
        {
            g.DrawText(L10n.T("Qp.NoAudioMsg"), listRect, _theme.TextSecondary, 12f, 400, UiTextAlign.Center, UiVertAlign.Center);
        }
        else
        {
            float rowW = _scroll.MaxOffset > 0.5f ? listRect.Width - UiMetrics.ScrollBarWidth - 4f : listRect.Width;
            g.PushClip(listRect);
            float curY = listRect.Y - _scroll.Offset;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                float rowTotalH = GetRowTotalHeight(row, legacyLayout, visibleOutputs.Length + 1, rowW);
                if (curY + rowTotalH >= listRect.Y && curY <= listRect.Bottom)
                {
                    RenderAppRow(g, row, i, new UiRect(listRect.X, curY, rowW, rowTotalH), legacyLayout, visibleOutputs);
                }
                curY += rowTotalH;
            }
            g.PopClip();
            _scroll.Draw(g, _theme, _hit, "qp.scroll", listRect);
        }

        // 4. Render Footer Bar (Master Default Output device combo + Mute + Volume slider)
        RenderFooterBar(g, new UiRect(padX, footerTop, innerW, footerH));
    }

    private void RenderAppRow(D2dTarget g, AppRowState row, int index, UiRect rowBounds, bool legacyLayout, Endpoint[] visibleOutputs)
    {
        float headerH = legacyLayout ? UiMetrics.QuickPanelCompactRowHeight - 2f : UiMetrics.QuickPanelFullRowHeight - 2f;
        var headerRect = new UiRect(rowBounds.X, rowBounds.Y, rowBounds.Width, headerH);

        if (row.Expanded)
        {
            g.FillRoundedRect(headerRect, 8f, _theme.SurfaceAlt.WithAlphaFraction(0.55f));
        }

        // 30x30 Icon button on left + 18x18 real icon + 8x8 AccentInverse mute dot
        var iconBtnRect = new UiRect(headerRect.X + 2f, headerRect.CenterY - 15f, 30f, 30f);
        string iconHitId = $"qp.appmute:{row.Pid}";
        _hit.Add(iconHitId, iconBtnRect, UiCursorKind.Hand, row.DisplayName);
        if (_hit.IsHover(iconHitId))
        {
            g.FillRoundedRect(iconBtnRect, 6f, _theme.NavHover);
        }

        var iconBox = new UiRect(iconBtnRect.CenterX - 9f, iconBtnRect.CenterY - 9f, 18f, 18f);
        var appBmp = NativeAppIconService.GetAppIcon(row.Pid, row.App.IconPath, row.App.ProcessName);
        if (appBmp is { } bmp)
        {
            g.DrawBgraBitmap($"qp.icon:{row.App.ProcessName ?? row.Pid.ToString()}", bmp.BgraPixels, bmp.Width, bmp.Height, iconBox);
        }
        else
        {
            string ch = string.IsNullOrEmpty(row.DisplayName) ? "♪" : row.DisplayName[..1].ToUpperInvariant();
            g.DrawText(ch, iconBox, _theme.TextPrimary, 12.5f, 600, UiTextAlign.Center, UiVertAlign.Center);
        }

        if (row.Muted)
        {
            g.FillEllipse(iconBtnRect.Right - 5f, iconBtnRect.Y + 6f, 4f, 4f, _theme.AccentInverse);
        }

        // Expand button on right
        bool showCompactOutName = legacyLayout && _config.QuickPanelCompactShowOutputName;
        float expandW = showCompactOutName ? 80f : 32f;
        float expandH = legacyLayout ? (showCompactOutName ? 24f : 20f) : 32f;
        var expandRect = new UiRect(headerRect.Right - expandW - 2f, headerRect.CenterY - expandH * 0.5f, expandW, expandH);
        string expandId = $"qp.expand:{row.Pid}";
        _hit.Add(expandId, expandRect, UiCursorKind.Hand, L10n.T("Qp.ExpandDevices"));
        if (row.Expanded) g.FillRoundedRect(expandRect, 6f, _theme.NavSelected);
        else if (_hit.IsHover(expandId)) g.FillRoundedRect(expandRect, 6f, _theme.NavHover);

        if (showCompactOutName)
        {
            string outName = row.CachedOutputLabel ?? L10n.T("Dev.SystemDefaultOut");
            g.DrawText(outName, new UiRect(expandRect.X + 4f, expandRect.Y, expandRect.Width - 18f, expandRect.Height),
                row.Expanded ? _theme.Accent : _theme.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            g.DrawIcon(UiIcon.ChevronDown, new UiRect(expandRect.Right - 14f, expandRect.CenterY - 5f, 10f, 10f),
                row.Expanded ? _theme.Accent : _theme.TextSecondary, 1.8f, row.Expanded ? 180f : 0f);
        }
        else
        {
            g.DrawIcon(UiIcon.ChevronDown, new UiRect(expandRect.CenterX - 5.5f, expandRect.CenterY - 5.5f, 11f, 11f),
                row.Expanded ? _theme.Accent : _theme.TextSecondary, 1.8f, row.Expanded ? 180f : 0f);
        }

        float midX = iconBtnRect.Right + 4f;
        float midW = MathF.Max(40f, expandRect.X - 6f - midX);
        string sliderId = $"qp.appslider:{row.Pid}";

        if (legacyLayout)
        {
            var sliderRect = new UiRect(midX, headerRect.CenterY - 16f, midW, 32f);
            UiPrimitives.DrawVolumeSlider(g, _theme, _hit, sliderId, sliderRect,
                row.Volume / 100f, row.Peak, row.Muted, _config.ShowAppPeakMeter, trackHeight: 4f, thumbDiameter: 14f);
        }
        else
        {
            string title = row.DisplayName;
            if (_config.QuickPanelShowOutputName && !string.IsNullOrEmpty(row.CachedOutputLabel))
                title += $" · {row.CachedOutputLabel}";
            var nameRect = new UiRect(midX + 6f, headerRect.Y + 2f, MathF.Max(20f, midW - 12f), 16f);
            g.DrawText(title, nameRect, _theme.TextPrimary, 11.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);

            var sliderRect = new UiRect(midX, headerRect.Y + 18f, midW, 32f);
            UiPrimitives.DrawVolumeSlider(g, _theme, _hit, sliderId, sliderRect,
                row.Volume / 100f, row.Peak, row.Muted, _config.ShowAppPeakMeter, trackHeight: 4f, thumbDiameter: 14f);
        }

        // Expanded device wrap-grid
        if (row.Expanded)
        {
            float devX = rowBounds.X + 26f;
            float devW = MathF.Max(60f, rowBounds.Right - devX);
            float devY = headerRect.Bottom + 4f;

            g.DrawText(L10n.T("Qp.Output"), new UiRect(devX, devY, devW, 16f), _theme.TextSecondary, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            devY += 20f;

            var choices = new List<(string Id, string Name)>(visibleOutputs.Length + 1)
            {
                (AudioService.SystemDefaultDeviceId, L10n.T("Dev.SystemDefaultOut"))
            };
            foreach (var d in visibleOutputs)
            {
                choices.Add((d.Id, _config.DeviceNames.GetValueOrDefault(d.Id, d.Name)));
            }

            float curX = devX;
            float curY = devY;
            const float btnH = 26f;
            const float gapX = 5f;
            const float gapY = 5f;
            var ctx = D2dSharedContext.Instance;

            foreach (var (devId, devName) in choices)
            {
                string shortLbl = UiPrimitives.ShortDeviceName(devName);
                float textW = ctx.MeasureText(shortLbl, UiMetrics.FontFamily, 11.5f, 600, devW - 20f, btnH, false).Width;
                float bw = Math.Clamp(MathF.Ceiling(textW + 18f), 48f, devW);
                if (curX > devX && curX + bw > devX + devW)
                {
                    curX = devX;
                    curY += btnH + gapY;
                }

                var btnRect = new UiRect(curX, curY, bw, btnH);
                string hitId = $"qp.appdev:{row.Pid}:{devId}";
                _hit.Add(hitId, btnRect, UiCursorKind.Hand, devName);

                bool isNative = string.Equals(row.NativeOutputId, devId, StringComparison.OrdinalIgnoreCase);
                bool isMirror = !isNative && row.SelectedOutputs.Contains(devId);
                bool hot = _hit.IsHover(hitId);

                UiColor bg = isNative ? _theme.DeviceNativeBg : isMirror ? _theme.Overlay : _theme.SurfaceBg;
                UiColor fg = isNative ? _theme.DeviceNativeFg : _theme.TextPrimary;
                UiColor brd = isNative ? _theme.Accent : hot ? _theme.Accent : isMirror ? _theme.DeviceMirrorBorder : _theme.Border;

                g.FillRoundedRect(btnRect, 7f, bg);
                g.DrawRoundedRect(btnRect, 7f, brd, 1f);
                g.DrawText(shortLbl, btnRect.Deflate(8f, 0f), fg, 11.5f, isNative ? 600 : 400, UiTextAlign.Center, UiVertAlign.Center);

                curX += bw + gapX;
            }
        }
    }

    private void RenderFooterBar(D2dTarget g, UiRect rect)
    {
        float rx = rect.Right;
        const float btnH = 28f;
        const float fullUiW = 84f;
        const float micW = 108f;
        const float globalMuteW = 110f;

        var fullUiRect = new UiRect(rx - fullUiW, rect.Y, fullUiW, btnH);
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "qp.settings", fullUiRect, L10n.T("Qp.FullUi"),
            fontSize: 12f, radius: 7f);
        rx = fullUiRect.X - 6f;

        if (_config.ExperimentalMic)
        {
            var micRect = new UiRect(rx - micW, rect.Y, micW, btnH);
            UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "qp.mic", micRect, L10n.T("Qp.MicMute"),
                enabled: _micKnown, fontSize: 12f, radius: 7f, active: _globalMicMuted);
            rx = micRect.X - 6f;
        }

        float gmX = MathF.Max(rect.X, rx - globalMuteW);
        var gmRect = new UiRect(gmX, rect.Y, rx - gmX, btnH);
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "qp.mastermute", gmRect, L10n.T("Qp.GlobalMute"),
            fontSize: 12f, radius: 7f, active: _masterMuted);
    }

    private float GetRowTotalHeight(AppRowState row, bool legacyLayout, int choiceCount, float availWidth)
    {
        float baseH = (legacyLayout ? UiMetrics.QuickPanelCompactRowHeight : UiMetrics.QuickPanelFullRowHeight) + 2f;
        if (!row.Expanded) return baseH;

        float devW = MathF.Max(60f, availWidth - 26f);
        int perLine = Math.Max(1, (int)MathF.Floor(devW / 110f));
        int lines = Math.Max(1, (int)Math.Ceiling(choiceCount / (double)perLine));
        return baseH + 24f + lines * 31f + 6f;
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (leftDown && _resizingHeight)
        {
            ViewNative.GetCursorPos(out var cur);
            float dyDip = (cur.Y - _resizeStartMouseScreenY) / DpiScale;
            float newH = _resizingTopEdge ? _resizeStartHeightDip - dyDip : _resizeStartHeightDip + dyDip;

            GetMonitorWorkAreaForWindow(Handle, out _, out int mt, out _, out int mb, out uint monDpi);
            float s = monDpi / 96f;
            float maxH = MathF.Min(UiMetrics.QuickPanelMaxHeight, MathF.Max(350f, (mb - mt) / s - 16f));
            newH = Math.Clamp(newH, UiMetrics.QuickPanelMinHeight, maxH);

            int newTopPx = _resizeStartTopPx;
            if (_resizingTopEdge)
            {
                int deltaPx = (int)MathF.Round((newH - _resizeStartHeightDip) * DpiScale);
                newTopPx = Math.Max(mt, _resizeStartTopPx - deltaPx);
            }

            SetBoundsPx(LeftPx, newTopPx, WidthDip, newH);
            RepositionCompanions();
            return;
        }

        if (leftDown && _scroll.DragMove(yDip)) { Invalidate(); return; }

        if (leftDown && _draggingMaster)
        {
            int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, xDip);
            if (_masterVolume != val)
            {
                _masterVolume = val;
                SetMasterVolume(val);
                Invalidate();
            }
            return;
        }

        if (leftDown && _draggingPid > 0)
        {
            var row = _rows.FirstOrDefault(r => r.Pid == _draggingPid);
            if (row != null)
            {
                int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, xDip);
                if (row.Volume != val)
                {
                    row.Volume = val;
                    QueueAppVolume(row, val);
                    Invalidate();
                }
            }
            return;
        }

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

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit is { } rh && (rh.Id == "qp.resize.top" || rh.Id == "qp.resize.bottom"))
        {
            _resizingHeight = true;
            _resizingTopEdge = rh.Id == "qp.resize.top";
            ViewNative.GetCursorPos(out var cur);
            _resizeStartMouseScreenY = cur.Y;
            _resizeStartHeightDip = HeightDip;
            _resizeStartTopPx = TopPx;
            return;
        }

        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }

        if (hit is null) return;
        _hit.ActiveId = hit.Value.Id;

        if (hit.Value.Id == "qp.masterslider")
        {
            _draggingMaster = true;
            _draggingSliderRect = hit.Value.Bounds;
            int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, e.X);
            _masterVolume = val;
            SetMasterVolume(val);
            Invalidate();
            return;
        }

        if (hit.Value.Id.StartsWith("qp.appslider:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(13), out int pid))
        {
            var row = _rows.FirstOrDefault(r => r.Pid == pid);
            if (row != null)
            {
                _draggingPid = pid;
                _draggingSliderRect = hit.Value.Bounds;
                int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, e.X);
                row.Volume = val;
                QueueAppVolume(row, val);
                Invalidate();
            }
            return;
        }

        Invalidate();
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (_resizingHeight)
        {
            _resizingHeight = false;
            _config.QuickPanelHeight = (int)Math.Clamp(MathF.Round(HeightDip), 350f, 850f);
            ConfigService.TrySave(_config);
            _host.ViewSettingsChanged();
            RepositionCompanions();
            return;
        }

        if (_scroll.Dragging) { _scroll.EndDrag(); Invalidate(); return; }
        if (_draggingMaster) { _draggingMaster = false; _hit.ActiveId = null; Invalidate(); return; }
        if (_draggingPid > 0) { _draggingPid = -1; _hit.ActiveId = null; Invalidate(); return; }

        string? active = _hit.ActiveId;
        _hit.ActiveId = null;
        if (e.Button != UiMouseButton.Left) return;

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit is null || hit.Value.Id != active) { Invalidate(); return; }

        switch (hit.Value.Id)
        {
            case "qp.pin":
                _pinned = !_pinned;
                Invalidate();
                break;
            case "qp.settings":
                Hide();
                (_onOpenMain ?? _host.OpenProductMain)();
                break;
            case "qp.mic":
                ToggleGlobalMicMute();
                break;
            case "qp.outputs":
                ToggleOutputVolumesPopup();
                break;
#if SONICROUTE_PHONE
            case "qp.phone":
                TogglePhoneCompanion();
                break;
#endif
            case "qp.mastermute":
                ToggleMasterMute();
                break;
            case "qp.masterdev":
                OpenMasterDeviceCombo(hit.Value.Bounds);
                break;
            default:
                if (hit.Value.Id.StartsWith("qp.appmute:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(11), out int mPid))
                {
                    ToggleAppMute(mPid);
                }
                else if (hit.Value.Id.StartsWith("qp.expand:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(10), out int ePid))
                {
                    ToggleRowExpand(ePid);
                }
                else if (hit.Value.Id.StartsWith("qp.appdev:", StringComparison.Ordinal))
                {
                    string rest = hit.Value.Id[10..];
                    int colon = rest.IndexOf(':');
                    if (colon > 0 && int.TryParse(rest[..colon], out int aPid))
                    {
                        string devId = rest[(colon + 1)..];
                        SelectAppOutput(aPid, devId, e.Ctrl, e.Shift);
                    }
                }
                break;
        }
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        var hit = _hit.HitTest(xDip, yDip);
        int step = Math.Clamp(_config.VolumeStep, 1, 20);
        if (hit is { } h)
        {
            if (h.Id == "qp.masterslider")
            {
                _masterVolume = Math.Clamp(_masterVolume + (delta > 0 ? step : -step), 0, 100);
                SetMasterVolume(_masterVolume);
                Invalidate();
                return;
            }
            if (h.Id.StartsWith("qp.appslider:", StringComparison.Ordinal) && int.TryParse(h.Id.AsSpan(13), out int pid))
            {
                var row = _rows.FirstOrDefault(r => r.Pid == pid);
                if (row != null)
                {
                    row.Volume = Math.Clamp(row.Volume + (delta > 0 ? step : -step), 0, 100);
                    QueueAppVolume(row, row.Volume);
                    Invalidate();
                    return;
                }
            }
        }
        _scroll.OnWheel(delta);
        Invalidate();
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            if (HasOpenModal) { CloseCombo(); return true; }
            Hide();
            OnDismissRequested?.Invoke();
            return true;
        }
        return false;
    }

    protected override void OnActivated(bool active)
    {
        if (!active) CheckAutoDismiss();
    }

    private void CheckAutoDismiss()
    {
        if (_pinned || HasOpenModal || !IsVisible) return;
        _host.TryPost(() =>
        {
            if (_pinned || HasOpenModal || !IsVisible) return;
            nint fg = ShellNative.GetForegroundWindow();
            if (fg == Handle) return;
            if (_outputVolumesPopup is { IsVisible: true } && fg == _outputVolumesPopup.Handle) return;
#if SONICROUTE_PHONE
            if (_phoneCompanion is { IsVisible: true } && (fg == _phoneCompanion.Handle || _phoneCompanion.HasOpenModal)) return;
#endif
            if (_comboPopup is { IsVisible: true } && fg == _comboPopup.Handle) return;
            Hide();
            OnDismissRequested?.Invoke();
        });
    }

    private void QueueAppVolume(AppRowState row, int value)
    {
        _host.QueueVolume(row.Pid, value, (actual, applied) =>
        {
            _host.TryPost(() =>
            {
                if (IsDisposed) return;
                if (applied) row.Volume = actual;
                if (IsVisible) Invalidate();
            });
        });
    }

    private void SetMasterVolume(int level)
    {
        string? target = _config.PanelChangeSystemDefault ? null : _panelOutputId;
        _ = Task.Run(() =>
        {
            try { _host.Audio.Volume(Flow.Render, target, 0, level: level); }
            catch { }
        });
    }

    private void ToggleMasterMute()
    {
        _masterMuted = !_masterMuted;
        Invalidate();
        string? target = _config.PanelChangeSystemDefault ? null : _panelOutputId;
        _ = Task.Run(() =>
        {
            try
            {
                var st = _host.Audio.Volume(Flow.Render, target, 0, toggle: true);
                _host.TryPost(() =>
                {
                    _masterMuted = st.Muted;
                    _masterVolume = st.Percent;
                    if (IsVisible) Invalidate();
                });
            }
            catch { }
        });
    }

    private void ToggleGlobalMicMute()
    {
        _ = Task.Run(() =>
        {
            try
            {
                bool muted = _host.Audio.GlobalMicMute(null);
                _host.TryPost(() =>
                {
                    _globalMicMuted = muted;
                    _micKnown = true;
                    if (IsVisible) Invalidate();
                });
            }
            catch { }
        });
    }

    private void ToggleAppMute(int pid)
    {
        var row = _rows.FirstOrDefault(r => r.Pid == pid);
        if (row == null) return;
        row.Muted = !row.Muted;
        Invalidate();
        _ = Task.Run(() =>
        {
            try
            {
                var st = _host.Audio.Volume(Flow.Render, null, pid, toggle: true);
                _host.TryPost(() =>
                {
                    row.Muted = st.Muted;
                    row.Volume = st.Percent;
                    if (IsVisible) Invalidate();
                });
            }
            catch { }
        });
    }

    private void ToggleRowExpand(int pid)
    {
        bool next = !(_expandedPids.TryGetValue(pid, out bool cur) && cur);
        _expandedPids[pid] = next;
        var row = _rows.FirstOrDefault(r => r.Pid == pid);
        if (row != null)
        {
            row.Expanded = next;
            Invalidate();
            if (next)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        string route = _host.Audio.ReadRoute(pid, Flow.Render) ?? AudioService.SystemDefaultDeviceId;
                        var selected = AppPlaybackOutputs.GetSelected(pid, route).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (selected.Count == 0) selected.Add(route);
                        _host.TryPost(() =>
                        {
                            row.NativeOutputId = route;
                            row.SelectedOutputs = selected;
                            if (IsVisible) Invalidate();
                        });
                    }
                    catch { }
                });
            }
        }
    }

    private void SelectAppOutput(int pid, string devId, bool ctrl, bool shift)
    {
        var row = _rows.FirstOrDefault(r => r.Pid == pid);
        if (row == null) return;

        if (!ctrl && !shift)
        {
            row.NativeOutputId = devId;
            row.SelectedOutputs.Clear();
            row.SelectedOutputs.Add(devId);
            row.OutputAnchor = devId;
            row.CachedOutputLabel = ResolveDeviceDisplayName(_outputs, devId);
            Invalidate();
            _ = _host.Audio.SelectOutputAsync(pid, devId).ContinueWith(_ =>
            {
                _host.TryPost(RefreshData);
            }, TaskScheduler.Default);
            return;
        }

        // Ctrl/Shift toggles mirror/multi-output
        var visibleChoices = new List<string> { AudioService.SystemDefaultDeviceId };
        visibleChoices.AddRange(_outputs.Where(d => !_config.HiddenOutputDevices.Contains(d.Id)).Select(d => d.Id));

        var selection = new HashSet<string>(row.SelectedOutputs, StringComparer.OrdinalIgnoreCase);
        if (selection.Count == 0) selection.Add(row.NativeOutputId);

        if (shift)
        {
            int a = visibleChoices.FindIndex(id => string.Equals(id, row.OutputAnchor ?? row.NativeOutputId, StringComparison.OrdinalIgnoreCase));
            int b = visibleChoices.FindIndex(id => string.Equals(id, devId, StringComparison.OrdinalIgnoreCase));
            if (a < 0) a = b;
            if (b >= 0)
            {
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) selection.Add(visibleChoices[i]);
            }
        }
        else if (ctrl)
        {
            if (!selection.Remove(devId)) selection.Add(devId);
            row.OutputAnchor = devId;
        }

        if (selection.Count == 0) selection.Add(AudioService.SystemDefaultDeviceId);
        string preferred = selection.Contains(devId) ? devId
            : selection.Contains(row.NativeOutputId) ? row.NativeOutputId
            : selection.First();

        row.SelectedOutputs = selection;
        row.NativeOutputId = preferred;
        row.CachedOutputLabel = ResolveDeviceDisplayName(_outputs, preferred);
        Invalidate();

        _ = _host.CurrentApp.SetOutputsAsync(pid, preferred, selection.ToArray()).ContinueWith(_ =>
        {
            _host.TryPost(RefreshData);
        }, TaskScheduler.Default);
    }

    private void OpenMasterDeviceCombo(UiRect anchor)
    {
        var visibleOutputs = _outputs.Where(d => !_config.HiddenOutputDevices.Contains(d.Id)).ToArray();
        var choices = visibleOutputs
            .Select(d => new ComboChoice(d.Id, _config.DeviceNames.GetValueOrDefault(d.Id, d.Name)))
            .ToList();
        if (choices.Count == 0) return;

        string selected = _config.PanelChangeSystemDefault ? (_defaultOutputId ?? choices[0].Key) : (_panelOutputId ?? _defaultOutputId ?? choices[0].Key);
        CloseCombo();
        _comboPopup = ComboPopupWindow.ShowFor(_theme, this, anchor, choices, selected, picked =>
        {
            _comboPopup = null;
            if (_config.PanelChangeSystemDefault)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        _host.Audio.SetDefault(Flow.Render, picked.Key);
                        _host.TryPost(() =>
                        {
                            RefreshData();
                            _outputVolumesPopup?.RefreshData();
                        });
                    }
                    catch { }
                });
            }
            else
            {
                _panelOutputId = picked.Key;
                RefreshData();
            }
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

    private void ToggleOutputVolumesPopup()
    {
        _config.QuickPanelShowAllOutputVolumes = !_config.QuickPanelShowAllOutputVolumes;
        ConfigService.TrySave(_config);
        SyncCompanionsVisibility();
        Invalidate();
    }

#if SONICROUTE_PHONE
    private void TogglePhoneCompanion()
    {
        _config.QuickPanelShowPhoneAudio = !_config.QuickPanelShowPhoneAudio;
        ConfigService.TrySave(_config);
        SyncCompanionsVisibility();
        Invalidate();
    }
#endif

    private void SyncCompanionsVisibility()
    {
        if (!IsVisible) return;

        UiRect? phoneOccupied = null;
#if SONICROUTE_PHONE
        if (_config.QuickPanelShowPhoneAudio)
        {
            _phoneCompanion ??= new PhoneAudioWindowView(_host, _config, CheckAutoDismiss, () => { Hide(); (_onOpenMain ?? _host.OpenProductMain)(); });
            _phoneCompanion.ShowForOwner(this);
            float s = DpiScale;
            phoneOccupied = new UiRect(_phoneCompanion.LeftPx / s, _phoneCompanion.TopPx / s, _phoneCompanion.WidthDip, _phoneCompanion.HeightDip);
        }
        else if (_phoneCompanion is { IsVisible: true })
        {
            _phoneCompanion.Hide();
        }
#endif

        if (_config.QuickPanelShowAllOutputVolumes)
        {
            _outputVolumesPopup ??= new OutputVolumesPopupWindow(_host, _config, RefreshData, CheckAutoDismiss);
            _outputVolumesPopup.ShowForOwner(this, phoneOccupied);
        }
        else if (_outputVolumesPopup is { IsVisible: true })
        {
            _outputVolumesPopup.Hide();
        }
    }

    private void RepositionCompanions()
    {
        UiRect? phoneOccupied = null;
#if SONICROUTE_PHONE
        if (_phoneCompanion is { IsVisible: true })
        {
            _phoneCompanion.RepositionForOwner(this);
            float s = DpiScale;
            phoneOccupied = new UiRect(_phoneCompanion.LeftPx / s, _phoneCompanion.TopPx / s, _phoneCompanion.WidthDip, _phoneCompanion.HeightDip);
        }
#endif
        if (_outputVolumesPopup is { IsVisible: true })
        {
            _outputVolumesPopup.RepositionForOwner(this, phoneOccupied);
        }
    }

    private void HideCompanions()
    {
        if (_outputVolumesPopup is { IsVisible: true }) _outputVolumesPopup.Hide();
#if SONICROUTE_PHONE
        if (_phoneCompanion is { IsVisible: true }) _phoneCompanion.Hide();
#endif
    }

    private void DisposeCompanions()
    {
        _outputVolumesPopup?.Dispose();
        _outputVolumesPopup = null;
#if SONICROUTE_PHONE
        _phoneCompanion?.Dispose();
        _phoneCompanion = null;
#endif
    }

    private void ApplyConfiguredHeight()
    {
        GetMonitorWorkAreaForWindow(Handle, out _, out int mt, out _, out int mb, out uint monDpi);
        float s = monDpi / 96f;
        float maxH = MathF.Max(240f, (mb - mt) / s - 16f);
        float h = Math.Clamp(_config.QuickPanelHeight, UiMetrics.QuickPanelMinHeight, UiMetrics.QuickPanelMaxHeight);
        ResizeDip(UiMetrics.QuickPanelWindowWidth, MathF.Min(h, maxH));
    }

    private void PositionWindow()
    {
        if (string.Equals(_config.QuickPanelPosMode, "custom", StringComparison.OrdinalIgnoreCase)
            && _config.QuickPanelCustomX >= 0 && _config.QuickPanelCustomY >= 0)
        {
            GetMonitorWorkAreaForPoint(_config.QuickPanelCustomX, _config.QuickPanelCustomY,
                out int cml, out int cmt, out int cmr, out int cmb, out uint cDpi);
            float cs = cDpi / 96f;
            int cwPx = (int)MathF.Round(WidthDip * cs);
            int chPx = (int)MathF.Round(HeightDip * cs);
            int cx = Math.Clamp((int)MathF.Round(_config.QuickPanelCustomX * cs), cml, Math.Max(cml, cmr - cwPx));
            int cy = Math.Clamp((int)MathF.Round(_config.QuickPanelCustomY * cs), cmt, Math.Max(cmt, cmb - chPx));
            SetBoundsPx(cx, cy, WidthDip, HeightDip);
            return;
        }

        int anchorX = 0, anchorY = 0;
        if (_trayOwnerHwnd != 0)
        {
            var id = new ShellNative.IconId
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.IconId>(),
                Window = _trayOwnerHwnd,
                Id = 1,
            };
            if (ShellNative.Shell_NotifyIconGetRect(in id, out var trayRect) >= 0 && trayRect.Right > trayRect.Left)
            {
                anchorX = (trayRect.Left + trayRect.Right) / 2;
                anchorY = (trayRect.Top + trayRect.Bottom) / 2;
            }
        }

        GetMonitorWorkAreaForPoint(anchorX, anchorY, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
        float s = monDpi / 96f;
        int wPx = (int)MathF.Round(WidthDip * s);
        int hPx = (int)MathF.Round(HeightDip * s);
        int xPx = mr - wPx - (int)MathF.Round(12f * s);
        int yPx = mb - hPx - (int)MathF.Round(10f * s);
        if (yPx < mt + (int)MathF.Round(6f * s)) yPx = mt + (int)MathF.Round(6f * s);

        xPx = Math.Clamp(xPx, ml, Math.Max(ml, mr - wPx));
        yPx = Math.Clamp(yPx, mt, Math.Max(mt, mb - hPx));
        SetBoundsPx(xPx, yPx, WidthDip, HeightDip);
    }

    private string ResolveDeviceDisplayName(Endpoint[] devices, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || AudioService.IsSystemDefault(deviceId))
            return L10n.T("Dev.SystemDefaultOut");
        if (_config.DeviceNames.TryGetValue(deviceId, out string? custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        var found = devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        return found?.Name ?? L10n.T("Dev.Unknown");
    }
}
