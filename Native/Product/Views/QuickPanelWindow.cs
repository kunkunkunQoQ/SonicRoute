using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

/// <summary>
/// High-fidelity Direct2D Quick Panel window matching WPF QuickPanelModernWindow 1.36.
/// Zero idle timers: 160ms entrance animation stops on completion; 50ms peak meter runs ONLY while visible and stops immediately in OnHidden/OnClosed.
/// </summary>
internal sealed class QuickPanelWindow : LayeredWindow
{
    private static readonly int[] PresetPercentages = [25, 50, 75, 100];
    private static readonly StringComparer IdCmp = StringComparer.OrdinalIgnoreCase;
    private const int EntranceDurationMs = 160;

    private sealed class AppRowState
    {
        public required AudioAppInfo App;
        public required string Identity;
        public int Pid;
        public bool IsExternal;
        public string DisplayName = "";
        public int Volume = 100;
        public bool Muted;
        public bool Available = true;
        public float Peak;
        public bool Expanded;
        public string NativeOutputId = AudioService.SystemDefaultDeviceId;
        public HashSet<string> SelectedOutputs = new(IdCmp);
        public string? OutputAnchor;
        public string NativeInputId = AudioService.SystemDefaultDeviceId;
        public string? CachedOutputLabel;
        public byte[]? ExternalIconBgra;
        public int ExtIconW, ExtIconH, ExtIconVer;
    }

    private readonly ApplicationHost _host;
    private readonly AppConfig _config;
    private readonly Action? _onOpenMain;
    private readonly nint _trayOwnerHwnd;
    private readonly UiHitMap _hit = new();
    private readonly UiScrollState _scroll = new();
    private readonly Dictionary<string, (byte[] Pixels, int W, int H, int Ver)> _extIconCache = new(StringComparer.Ordinal);
    private int _extIconVer;

    private readonly object _masterWriteGate = new();
    private (string? Endpoint, int Level)? _pendingMasterWrite;
    private bool _masterWriteRunning;

    private UiThemePalette _theme;
    private OutputVolumesPopupWindow? _outputVolumesPopup;
#if SONICROUTE_PHONE
    private PhoneAudioWindowView? _phoneCompanion;
#endif
    private ComboPopupWindow? _comboPopup;
    private PopupMenuWindow? _presetMenu;

    private Endpoint[] _outputs = [];
    private Endpoint[] _allRenderOutputs = [];
    private Endpoint[] _inputs = [];
    private string? _defaultOutputId;
    private string? _panelOutputId;
    private int _masterVolume = 100;
    private bool _masterMuted;
    private bool _globalPlaybackMuted;
    private bool _globalMicMuted;
    private bool _micKnown;

    private List<AppRowState> _rows = new();
    private string? _expandedAppId;
    private readonly Dictionary<string, float> _smoothedPeaks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _externalVolumeVersion = new(StringComparer.Ordinal);

    private bool _pinned;
    private bool _loading;
    private bool _refreshQueued;
    private bool _eventsSubscribed;
    private bool _peakReadInFlight;
    private int _draggingRowIdx = -1;
    private bool _draggingMaster;
    private UiRect _draggingSliderRect;
    private string? _rightPressId;

#if SONICROUTE_PHONE
    private int _pendingDragRowIdx = -1;
    private float _appDragStartDipX;
    private float _appDragStartDipY;
    private bool _draggingCrossDevice;
#endif

    private bool _resizingHeight;
    private bool _resizingTopEdge;
    private float _resizeStartMouseScreenY;
    private float _resizeStartHeightDip;
    private int _resizeStartTopPx;

    private bool _adjustMode;
    private bool _adjustDragging;
    private float _adjustDragStartScreenX;
    private float _adjustDragStartScreenY;
    private int _adjustDragWinStartX;
    private int _adjustDragWinStartY;
    private int _adjustOrigLeftPx;
    private int _adjustOrigTopPx;

    private bool _animatingEntrance;
    private long _entranceStartTick;
    private float _entranceOpacity = 1f;
    private float _entranceSlideY;

    internal event Action? OnDismissRequested;
    internal bool IsPinned => _pinned;
    internal bool HasOpenModal =>
        (_comboPopup is { IsDisposed: false, IsVisible: true })
        || (_presetMenu is { IsDisposed: false, IsVisible: true })
        || (_outputVolumesPopup is { IsVisible: true, HasOpenModal: true })
#if SONICROUTE_PHONE
        || (_phoneCompanion is { IsVisible: true, HasOpenModal: true })
#endif
        ;

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
            if (!_adjustMode) PositionWindow();
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
        if (!_adjustMode) PositionWindow();

        if (!IsVisible)
        {
            _animatingEntrance = true;
            _entranceStartTick = Environment.TickCount64;
            _entranceOpacity = 0.05f;
            _entranceSlideY = 10f;
            Show(activate: true);
        }
        else
        {
            _animatingEntrance = false;
            _entranceOpacity = 1f;
            _entranceSlideY = 0f;
            Show(activate: true);
        }

        RefreshData();
        SyncCompanionsVisibility();
    }

    internal void BeginAdjust()
    {
        ShowQuickPanel();
        _adjustMode = true;
        _adjustDragging = false;
        _adjustOrigLeftPx = LeftPx;
        _adjustOrigTopPx = TopPx;
        if (_outputVolumesPopup is { IsVisible: true }) _outputVolumesPopup.Hide();
        _host.PublishOsd(L10n.T("Exp.PanelPosDragTitle"), L10n.T("Exp.PanelPosHint"), success: true);
        Invalidate();
    }

    internal void RefreshData()
    {
        if (IsDisposed) return;
        if (_loading)
        {
            _refreshQueued = true;
            return;
        }
        _loading = true;
        _refreshQueued = false;

        bool needOutputName = _config.QuickPanelUseLegacyAppRows
            ? _config.QuickPanelCompactShowOutputName
            : _config.QuickPanelShowOutputName;
        bool includeInputs = _config.ExperimentalMic && _config.MicInPanel;
        string? expandedId = _expandedAppId;

        _ = Task.Run(() =>
        {
            Endpoint[] render = [];
            Endpoint[] allRenderWithPhone = [];
            Endpoint[] capture = [];
            string? defId = null;
            int masterVol = 100;
            bool masterMute = false;
            bool globalPlaybackMuted = false;
            bool micMuted = false;
            bool micKnown = false;
            var newRows = new List<AppRowState>();

            try
            {
                render = ProductAudio.Devices(Flow.Render);
                allRenderWithPhone = _host.Audio.RenderDevicesWithPhone();
                defId = render.Length > 0 ? ProductAudio.DefaultId(Flow.Render) : null;
                string? targetEndpoint = _config.PanelChangeSystemDefault ? null : (_panelOutputId ?? defId);
                try
                {
                    var mv = _host.Audio.Volume(Flow.Render, targetEndpoint, 0);
                    masterVol = mv.Percent;
                    masterMute = mv.Muted;
                }
                catch { }

                if (render.Length > 0)
                {
                    try
                    {
                        globalPlaybackMuted = render.All(d => _host.Audio.Volume(Flow.Render, d.Id).Muted);
                    }
                    catch { }
                }

                // Live capture mute state always synced to match WPF QuickPanelModernWindow
                try
                {
                    capture = ProductAudio.Devices(Flow.Capture);
                    if (capture.Length > 0)
                    {
                        micKnown = true;
                        micMuted = capture.All(d => _host.Audio.Volume(Flow.Capture, d.Id).Muted);
                    }
                }
                catch { }

                var apps = _host.Audio.Apps();
                foreach (var app in apps)
                {
                    if (!app.IsExternal && app.ProcessName is { } proc && _config.HiddenPanelApps.Contains(proc, IdCmp))
                        continue;

                    int pid = checked((int)app.ProcessId);
                    string label = !string.IsNullOrWhiteSpace(app.ProcessName)
                        && _config.AppNames.TryGetValue(app.ProcessName!, out string? custom)
                        && !string.IsNullOrWhiteSpace(custom)
                        ? custom
                        : app.Label;

                    int vol = 0;
                    bool muted = false;
                    bool avail = true;
                    try
                    {
                        var vs = _host.Audio.Volume(app);
                        if (vs.Percent < 0)
                        {
                            avail = false;
                            vol = 0;
                        }
                        else
                        {
                            vol = vs.Percent;
                            muted = vs.Muted;
                        }
                    }
                    catch
                    {
                        avail = false;
                        vol = 0;
                    }

                    bool expanded = string.Equals(expandedId, app.Identity, StringComparison.Ordinal);
                    string nativeOut = AudioService.SystemDefaultDeviceId;
                    HashSet<string> selectedOuts = new(IdCmp);
                    string nativeIn = AudioService.SystemDefaultDeviceId;

                    if (expanded || needOutputName)
                    {
                        try
                        {
                            string? persisted = _host.Audio.ReadRoute(app, Flow.Render);
                            nativeOut = string.IsNullOrWhiteSpace(persisted) || AudioService.IsSystemDefault(persisted)
                                ? AudioService.SystemDefaultDeviceId
                                : AudioPolicyConfig.UnpackDeviceId(persisted);
                            foreach (string s in _host.Audio.GetSelectedOutputs(app))
                                selectedOuts.Add(s);
                        }
                        catch { }
                    }
                    if (selectedOuts.Count == 0) selectedOuts.Add(nativeOut);

                    if (expanded && includeInputs && !app.IsExternal)
                    {
                        try
                        {
                            string? persistedIn = _host.Audio.ReadRoute(pid, Flow.Capture);
                            nativeIn = string.IsNullOrWhiteSpace(persistedIn) || AudioService.IsSystemDefault(persistedIn)
                                ? AudioService.SystemDefaultDeviceId
                                : AudioPolicyConfig.UnpackDeviceId(persistedIn);
                        }
                        catch { }
                    }

                    string? outLabel = needOutputName ? ResolveDeviceDisplayName(allRenderWithPhone, nativeOut) : null;

                    newRows.Add(new AppRowState
                    {
                        App = app,
                        Identity = app.Identity,
                        Pid = pid,
                        IsExternal = app.IsExternal,
                        DisplayName = label,
                        Volume = vol,
                        Muted = muted,
                        Available = avail,
                        Peak = _smoothedPeaks.GetValueOrDefault(app.Identity),
                        Expanded = expanded,
                        NativeOutputId = nativeOut,
                        SelectedOutputs = selectedOuts,
                        NativeInputId = nativeIn,
                        CachedOutputLabel = outLabel,
                    });
                }
            }
            catch { }

            _host.TryPost(() =>
            {
                _loading = false;
                if (IsDisposed) return;

                if (_draggingRowIdx >= 0 && _draggingRowIdx < _rows.Count)
                {
                    var draggingOld = _rows[_draggingRowIdx];
                    var draggingNew = newRows.FirstOrDefault(r => string.Equals(r.Identity, draggingOld.Identity, StringComparison.Ordinal));
                    if (draggingNew != null) draggingNew.Volume = draggingOld.Volume;
                }

                _outputs = render;
                _allRenderOutputs = allRenderWithPhone;
                _inputs = capture;
                _defaultOutputId = defId;
                if (_panelOutputId == null || !render.Any(d => IdCmp.Equals(d.Id, _panelOutputId)))
                    _panelOutputId = defId;
                if (!_draggingMaster) _masterVolume = masterVol;
                _masterMuted = masterMute;
                _globalPlaybackMuted = globalPlaybackMuted;
                _globalMicMuted = micMuted;
                _micKnown = micKnown;
                _rows = newRows;

                foreach (var row in _rows)
                {
                    if (row.IsExternal && row.App.ExternalIconPng is { Length: > 0 } png)
                    {
                        if (TryGetExternalIcon(row.Identity, png, out var bgra, out int w, out int h, out int ver))
                        {
                            row.ExternalIconBgra = bgra;
                            row.ExtIconW = w;
                            row.ExtIconH = h;
                            row.ExtIconVer = ver;
                        }
                    }
                }

                if (IsVisible) Invalidate();
                if (_refreshQueued && IsVisible)
                {
                    _refreshQueued = false;
                    RefreshData();
                }
            });
        });
    }

    protected override void OnShown()
    {
        base.OnShown();
        SubscribeEvents(true);
        if (_animatingEntrance)
            StartAnimationTimer(16);
        else if (_config.ShowAppPeakMeter)
            StartAnimationTimer(50);
    }

    protected override void OnHidden()
    {
        StopAnimationTimer();
        _animatingEntrance = false;
        _adjustMode = false;
        _adjustDragging = false;
        SubscribeEvents(false);
        CloseCombo();
        ClosePresetMenu();
        CancelActiveInteractions();
        HideCompanions();
        base.OnHidden();
    }

    protected override void OnClosed()
    {
        StopAnimationTimer();
        _animatingEntrance = false;
        _adjustMode = false;
        _adjustDragging = false;
        SubscribeEvents(false);
        CloseCombo();
        ClosePresetMenu();
        CancelActiveInteractions();
        DisposeCompanions();
        base.OnClosed();
    }

    protected override void OnCaptureLost()
    {
        bool hadDrag = _resizingHeight || _adjustDragging || _scroll.Dragging || _draggingMaster || _draggingRowIdx >= 0;
        CancelActiveInteractions();
        _host.FlushVolumeWrites();
        if (hadDrag && IsVisible) Invalidate();
    }

    protected override void OnTickAnimation()
    {
        if (!IsVisible || IsDisposed)
        {
            StopAnimationTimer();
            return;
        }

        if (_animatingEntrance)
        {
            float t = Math.Clamp((Environment.TickCount64 - _entranceStartTick) / (float)EntranceDurationMs, 0f, 1f);
            float inv = 1f - t;
            float ease = 1f - inv * inv * inv; // CubicEase-Out
            _entranceOpacity = 0.05f + 0.95f * ease;
            _entranceSlideY = (1f - ease) * 10f;
            if (t >= 1f)
            {
                _animatingEntrance = false;
                _entranceOpacity = 1f;
                _entranceSlideY = 0f;
                StopAnimationTimer();
                if (_config.ShowAppPeakMeter) StartAnimationTimer(50);
            }
            Invalidate();
            return;
        }

        if (!_config.ShowAppPeakMeter)
        {
            StopAnimationTimer();
            return;
        }
        if (_peakReadInFlight || _rows.Count == 0) return;

        int[] pids = _rows
            .Where(r => !r.IsExternal && r.Pid > 0 && r.Available && !r.Muted)
            .Select(r => r.Pid)
            .Distinct()
            .ToArray();
        if (pids.Length == 0) return;

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
                    if (row.IsExternal) continue;
                    float target = read != null && !row.Muted && read.TryGetValue(row.Pid, out float v) ? v : 0f;
                    float prev = row.Peak;
                    float next = target >= prev ? target : prev * 0.72f;
                    if (next < 0.005f) next = 0f;
                    if (MathF.Abs(next - prev) > 0.008f)
                    {
                        row.Peak = next;
                        _smoothedPeaks[row.Identity] = next;
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
        UiThemePalette theme = _animatingEntrance ? ScaleThemeOpacity(_theme, _entranceOpacity) : _theme;
        if (_animatingEntrance)
        {
            g.PushTransform(new D2dMatrix3x2(1f, 0f, 0f, 1f, 0f, _entranceSlideY));
        }

        try
        {
            RenderPanelContent(g, theme, widthDip, heightDip);
        }
        finally
        {
            if (_animatingEntrance) g.PopTransform();
        }
    }

    private void RenderPanelContent(D2dTarget g, UiThemePalette theme, float widthDip, float heightDip)
    {
        // Surface leaves 6 DIP right margin (Width = 366 inside 372 window)
        float surfaceW = MathF.Min(UiMetrics.QuickPanelSurfaceWidth, widthDip);
        var surface = new UiRect(0f, 0f, surfaceW, heightDip);
        g.FillRoundedRect(surface, UiMetrics.QuickPanelCornerRadius, theme.WindowBgAlpha);
        g.DrawRoundedRect(surface, UiMetrics.QuickPanelCornerRadius, _adjustMode ? theme.Accent : theme.Border, _adjustMode ? 2f : 1f);

        // Top & bottom resize grips (invisible hit zones)
        _hit.Add("qp.resize.top", new UiRect(12f, 0f, surfaceW - 24f, 5f), UiCursorKind.SizeNS);
        _hit.Add("qp.resize.bottom", new UiRect(12f, heightDip - 5f, surfaceW - 24f, 5f), UiCursorKind.SizeNS);

        const float padX = 16f;
        const float padTop = 14f;
        const float padBottom = 12f;
        float innerW = surfaceW - padX * 2f;

        // 1. Header Bar
        float y = padTop;
        var headerRect = new UiRect(padX, y, innerW, 28f);
        float rx = headerRect.Right;

        const float pinBtnW = 28f;
        var pinRect = new UiRect(rx - pinBtnW, y - 1f, pinBtnW, 28f);
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "qp.pin", pinRect, "",
            radius: 8f, icon: UiIcon.Pin, active: _pinned, tooltip: L10n.T("Qp.Panel"));
        rx = pinRect.X - 6f;

#if SONICROUTE_PHONE
        const float phoneBtnW = 30f;
        var phoneRect = new UiRect(rx - phoneBtnW, y - 1f, phoneBtnW, 28f);
        bool phoneOpen = _phoneCompanion is { IsVisible: true };
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "qp.phone", phoneRect, "",
            radius: 8f, icon: UiIcon.Phone, active: phoneOpen,
            tooltip: phoneOpen ? L10n.T("Phone.ClosePhoneAudio") : L10n.T("Phone.OpenPhoneAudio"));
        rx = phoneRect.X - 6f;
#endif

        const string verLabel = "v1.36";
        float verW = D2dSharedContext.Instance.MeasureText(verLabel, UiMetrics.FontFamily, 10.5f, 400, 80f, 20f, false).Width + 4f;
        g.DrawText(verLabel, new UiRect(rx - verW, y, verW, 28f), theme.TextSecondary, 10.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);

        var titleRect = new UiRect(padX, y, MathF.Max(40f, rx - verW - 8f - padX), 28f);
        g.DrawText(BuildIdentity.DisplayName, titleRect, theme.TextPrimary, 14f, 700, UiTextAlign.Leading, UiVertAlign.Center);

        // 2. Output Device Selector + Expand Output Volumes Toggle
        y = headerRect.Bottom + 10f;
        string activeId = _config.PanelChangeSystemDefault ? (_defaultOutputId ?? "") : (_panelOutputId ?? _defaultOutputId ?? "");
        string devName = ResolveDeviceDisplayName(_outputs, activeId);
        const float expBtnSize = 32f;
        var outputsToggleRect = new UiRect(padX + innerW - expBtnSize, y, expBtnSize, expBtnSize);
        bool outPopOpen = _outputVolumesPopup is { IsVisible: true };
        _hit.Add("qp.outputs", outputsToggleRect, UiCursorKind.Hand, L10n.T("Qp.ExpandOutputVolumes"));
        g.FillRoundedRect(outputsToggleRect, 8f, outPopOpen ? theme.NavSelected : _hit.IsHover("qp.outputs") ? theme.NavHover : theme.SurfaceBg);
        g.DrawRoundedRect(outputsToggleRect, 8f, outPopOpen || _hit.IsHover("qp.outputs") ? theme.Accent : theme.Border, 1f);
        g.DrawIcon(UiIcon.ChevronUp, new UiRect(outputsToggleRect.CenterX - 5f, outputsToggleRect.CenterY - 5f, 10f, 10f),
            outPopOpen ? theme.Accent : theme.TextSecondary, 1.8f, outPopOpen ? 180f : 0f);

        var masterComboRect = new UiRect(padX, y, MathF.Max(60f, outputsToggleRect.X - 6f - padX), 32f);
        _hit.Add("qp.masterdev", masterComboRect, UiCursorKind.Hand, devName);
        bool comboHot = _hit.IsHover("qp.masterdev");
        bool comboOpen = _comboPopup is { IsDisposed: false, IsVisible: true };
        g.FillRoundedRect(masterComboRect, 8f, theme.SurfaceBg);
        g.DrawRoundedRect(masterComboRect, 8f, comboOpen || comboHot ? theme.Accent : theme.Border, 1f);
        g.DrawIcon(UiIcon.Headphones, new UiRect(masterComboRect.X + 10f, masterComboRect.CenterY - 7f, 14f, 14f), theme.TextPrimary, 1.7f);
        g.DrawText(devName, new UiRect(masterComboRect.X + 30f, masterComboRect.Y, MathF.Max(20f, masterComboRect.Width - 52f), masterComboRect.Height),
            theme.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(masterComboRect.Right - 20f, masterComboRect.CenterY - 5.5f, 11f, 11f),
            theme.TextSecondary, 1.8f, comboOpen ? 180f : 0f);

        // 3. Master Volume Row (Fixed Mute Label + Slider + Percentage)
        y = masterComboRect.Bottom + 10f;
        string muteText = L10n.T("Qp.Mute");
        float muteW = Math.Clamp(D2dSharedContext.Instance.MeasureText(muteText, UiMetrics.FontFamily, 12f, _masterMuted ? 600 : 400, 120f, 28f, false).Width + 20f, 54f, 96f);
        var masterMuteRect = new UiRect(padX, y + 2f, muteW, 28f);
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "qp.mastermute", masterMuteRect, muteText,
            fontSize: 12f, radius: 7f, active: _masterMuted,
            tooltip: _masterMuted ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute"));

        var pctRect = new UiRect(padX + innerW - 42f, y, 42f, 32f);
        g.DrawText($"{_masterVolume}%", pctRect, theme.TextPrimary, 12.5f, 600, UiTextAlign.Trailing, UiVertAlign.Center, wrap: false, ellipsis: false);

        var masterSliderRect = new UiRect(masterMuteRect.Right + 10f, y, MathF.Max(40f, pctRect.X - 6f - (masterMuteRect.Right + 10f)), 32f);
        UiPrimitives.DrawVolumeSlider(g, theme, _hit, "qp.masterslider", masterSliderRect,
            _masterVolume / 100f, 0f, _masterMuted, showPeak: false, trackHeight: 4f, thumbDiameter: 14f);

        // 4. Divider + App Volumes Header
        y = masterSliderRect.Bottom + 10f;
        g.DrawLine(padX, y, padX + innerW, y, theme.Border, 1f, roundCaps: false);
        y += 8f;

        // 5. Footer bounds
        const float footerH = 28f;
        float footerTop = heightDip - padBottom - footerH;
        g.DrawLine(padX, footerTop - 10f, padX + innerW, footerTop - 10f, theme.Border, 1f, roundCaps: false);

        // 6. Scrollable Apps List Area
        var appsHeaderRect = new UiRect(padX, y, innerW, 18f);
        g.DrawText(L10n.T("Qp.AppVolumes"), appsHeaderRect, theme.TextSecondary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        y = appsHeaderRect.Bottom + 6f;

        var listRect = new UiRect(padX, y, innerW, MathF.Max(36f, footerTop - 14f - y));
        bool legacyLayout = _config.QuickPanelUseLegacyAppRows;
        bool showInputsInPanel = _config.ExperimentalMic && _config.MicInPanel;
        var currentApp = _host.CurrentApp.Current;

        var cachedRowLayouts = new List<(float TotalH, List<(string Id, string Label, UiRect Rect, bool IsExit)> OutPills, List<(string Id, string Label, UiRect Rect)> InPills)>(_rows.Count);
        float totalContentH = 0f;
        float prelimW = _rows.Count > 4 ? listRect.Width - UiMetrics.ScrollBarWidth - 4f : listRect.Width;

        // First pass: measure exact wrapped pill heights using available row width
        for (int pass = 0; pass < 2; pass++)
        {
            cachedRowLayouts.Clear();
            totalContentH = 0f;
            float measureW = pass == 0 ? prelimW : ((totalContentH > listRect.Height) ? listRect.Width - UiMetrics.ScrollBarWidth - 4f : listRect.Width);
            for (int i = 0; i < _rows.Count; i++)
            {
                var layout = MeasureRowLayout(_rows[i], legacyLayout, showInputsInPanel, measureW);
                cachedRowLayouts.Add(layout);
                totalContentH += layout.TotalH;
            }
            bool needsBar = totalContentH > listRect.Height;
            float usedW = needsBar ? listRect.Width - UiMetrics.ScrollBarWidth - 4f : listRect.Width;
            if (MathF.Abs(usedW - measureW) < 0.5f) break;
        }

        _scroll.ViewportHeight = listRect.Height;
        _scroll.ContentHeight = totalContentH;
        _scroll.Clamp();

        if (_rows.Count == 0)
        {
            g.DrawText(L10n.T("Qp.NoAudioMsg"), listRect, theme.TextSecondary, 12f, 400, UiTextAlign.Center, UiVertAlign.Center);
        }
        else
        {
            float rowW = _scroll.MaxOffset > 0.5f ? listRect.Width - UiMetrics.ScrollBarWidth - 4f : listRect.Width;
            g.PushClip(listRect);
            float curY = listRect.Y - _scroll.Offset;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var layout = cachedRowLayouts[i];
                if (curY + layout.TotalH >= listRect.Y && curY <= listRect.Bottom)
                {
                    bool isCurrent = currentApp != null && string.Equals(row.Identity, currentApp.Identity, StringComparison.OrdinalIgnoreCase);
                    RenderAppRow(g, theme, row, i, new UiRect(listRect.X, curY, rowW, layout.TotalH), legacyLayout, isCurrent, layout.OutPills, layout.InPills);
                }
                curY += layout.TotalH;
            }
            g.PopClip();
            _scroll.Draw(g, theme, _hit, "qp.scroll", listRect);
        }

        // 7. Footer Toolbar (Global Mute, Mic Mute, Full UI)
        RenderFooterBar(g, theme, new UiRect(padX, footerTop, innerW, footerH));
    }

    private (float TotalH, List<(string Id, string Label, UiRect Rect, bool IsExit)> OutPills, List<(string Id, string Label, UiRect Rect)> InPills)
        MeasureRowLayout(AppRowState row, bool legacyLayout, bool showInputs, float rowWidth)
    {
        float headerH = (legacyLayout ? UiMetrics.QuickPanelCompactRowHeight : UiMetrics.QuickPanelFullRowHeight) - 2f;
        float baseH = headerH + 2f;
        var outPills = new List<(string, string, UiRect, bool)>();
        var inPills = new List<(string, string, UiRect)>();
        if (!row.Expanded) return (baseH, outPills, inPills);

        float devX = 26f;
        float devW = MathF.Max(60f, rowWidth - devX);
        float y = headerH + 4f;

        // Output group label + wrapped pills
        y += 20f;
        var outChoices = BuildOutputChoicesForRow(row);
        float outH = LayoutWrappedPills(outChoices, hasTrailingExit: row.IsExternal, devX, y, devW, outPills);
        y += outH;

        // Input group when ExperimentalMic && MicInPanel && !IsExternal
        if (showInputs && !row.IsExternal)
        {
            y += 8f;
            y += 20f;
            var inChoices = BuildInputChoices();
            float inH = LayoutWrappedPills(inChoices, devX, y, devW, inPills);
            y += inH;
        }

        y += 6f;
        return (y, outPills, inPills);
    }

    private static float LayoutWrappedPills(
        List<(string Id, string Name)> choices,
        bool hasTrailingExit,
        float startX,
        float startY,
        float maxW,
        List<(string Id, string Label, UiRect Rect, bool IsExit)> result)
    {
        const float btnH = 26f;
        const float gapX = 5f;
        const float gapY = 5f;
        var ctx = D2dSharedContext.Instance;
        float curX = startX;
        float curY = startY;

        foreach (var (id, name) in choices)
        {
            string shortLbl = UiPrimitives.ShortDeviceName(name);
            float textW = ctx.MeasureText(shortLbl, UiMetrics.FontFamily, 11.5f, 600, maxW - 18f, btnH, false).Width;
            float bw = Math.Clamp(MathF.Ceiling(textW + 18f), 48f, maxW);
            if (curX > startX && curX + bw > startX + maxW)
            {
                curX = startX;
                curY += btnH + gapY;
            }
            result.Add((id, shortLbl, new UiRect(curX, curY, bw, btnH), false));
            curX += bw + gapX;
        }

        if (hasTrailingExit)
        {
            string exitLbl = L10n.T("Tray.Exit");
            float textW = ctx.MeasureText(exitLbl, UiMetrics.FontFamily, 11.5f, 400, maxW - 18f, btnH, false).Width;
            float bw = Math.Clamp(MathF.Ceiling(textW + 18f), 48f, maxW);
            if (curX > startX && curX + bw > startX + maxW)
            {
                curX = startX;
                curY += btnH + gapY;
            }
            result.Add(("__EXIT__", exitLbl, new UiRect(curX, curY, bw, btnH), true));
        }

        return result.Count == 0 ? 0f : (curY + btnH - startY);
    }

    private static float LayoutWrappedPills(
        List<(string Id, string Name)> choices,
        float startX,
        float startY,
        float maxW,
        List<(string Id, string Label, UiRect Rect)> result)
    {
        var tmp = new List<(string Id, string Label, UiRect Rect, bool IsExit)>(choices.Count);
        float h = LayoutWrappedPills(choices, hasTrailingExit: false, startX, startY, maxW, tmp);
        foreach (var item in tmp) result.Add((item.Id, item.Label, item.Rect));
        return h;
    }

    private List<(string Id, string Name)> BuildOutputChoicesForRow(AppRowState row)
    {
        var choices = new List<(string Id, string Name)>
        {
            (AudioService.SystemDefaultDeviceId, L10n.T("Dev.SystemDefaultOut"))
        };

#if SONICROUTE_PHONE
        bool isAndroidExternal = row.IsExternal && _host.Audio.ExternalAudio?.FindSession(row.App.ExternalSourceId) is AndroidAudioSession;
        if (isAndroidExternal)
        {
            choices.Add((AndroidAudioSession.SourcePhoneOutputId, L10n.T("Phone.NativeAudioOutput")));
        }
#endif

        var sourceList = _allRenderOutputs.Length > 0 ? _allRenderOutputs : _outputs;
        foreach (var d in sourceList)
        {
            if (_config.HiddenOutputDevices.Contains(d.Id)) continue;
            if (row.IsExternal && PcRoutingApi.IsPhoneOutputDevice(d.Id))
            {
                // Keep external/phone outputs consistent with WPF RowPlaybackDevices
            }
            choices.Add((d.Id, _config.DeviceNames.GetValueOrDefault(d.Id, d.Name)));
        }
        return choices;
    }

    private List<(string Id, string Name)> BuildInputChoices()
    {
        var choices = new List<(string Id, string Name)>(_inputs.Length + 1)
        {
            (AudioService.SystemDefaultDeviceId, L10n.T("Dev.SystemDefaultIn"))
        };
        foreach (var d in _inputs)
        {
            if (_config.HiddenInputDevices.Contains(d.Id)) continue;
            choices.Add((d.Id, _config.DeviceNames.GetValueOrDefault(d.Id, d.Name)));
        }
        return choices;
    }

    private void RenderAppRow(
        D2dTarget g,
        UiThemePalette theme,
        AppRowState row,
        int index,
        UiRect rowBounds,
        bool legacyLayout,
        bool isCurrent,
        List<(string Id, string Label, UiRect Rect, bool IsExit)> outPills,
        List<(string Id, string Label, UiRect Rect)> inPills)
    {
        float headerH = (legacyLayout ? UiMetrics.QuickPanelCompactRowHeight : UiMetrics.QuickPanelFullRowHeight) - 2f;
        var headerRect = new UiRect(rowBounds.X, rowBounds.Y, rowBounds.Width, headerH);

        if (isCurrent)
        {
            g.FillRoundedRect(headerRect, 8f, theme.Overlay);
            g.FillRoundedRect(new UiRect(headerRect.X + 1f, headerRect.Y + 6f, 2.5f, MathF.Max(8f, headerRect.Height - 12f)), 1.25f, theme.Accent);
        }
        else if (row.Expanded)
        {
            g.FillRoundedRect(headerRect, 8f, theme.SurfaceAlt.WithAlphaFraction(0.55f));
        }

        // 30x30 Icon button on left + 18x18 real icon + 8x8 AccentInverse mute dot
        var iconBtnRect = new UiRect(headerRect.X + 4f, headerRect.CenterY - 15f, 30f, 30f);
        string iconHitId = $"qp.appmute:{index}";
        string iconTip = string.IsNullOrEmpty(row.App.SourceLabel) ? row.DisplayName : $"{row.DisplayName} ({row.App.SourceLabel})";
        _hit.Add(iconHitId, iconBtnRect, UiCursorKind.Hand, iconTip);
        if (_hit.IsHover(iconHitId))
        {
            g.FillRoundedRect(iconBtnRect, 6f, theme.NavHover);
        }

        var iconBox = new UiRect(iconBtnRect.CenterX - 9f, iconBtnRect.CenterY - 9f, 18f, 18f);
        if (row.ExternalIconBgra is { } extBgra && row.ExtIconW > 0)
        {
            g.DrawBgraBitmap($"qp.ext:{row.Identity}", extBgra, row.ExtIconW, row.ExtIconH, iconBox, version: row.ExtIconVer);
        }
        else if (!row.IsExternal && NativeAppIconService.GetAppIcon(_config, row.Pid, row.App.IconPath, row.App.ProcessName, () =>
        {
            if (IsVisible && !IsDisposed) Invalidate();
        }) is { } bmp)
        {
            g.DrawBgraBitmap($"qp.icon:{row.App.ProcessName ?? row.Pid.ToString(CultureInfo.InvariantCulture)}", bmp.BgraPixels, bmp.Width, bmp.Height, iconBox);
        }
        else
        {
            string ch = string.IsNullOrEmpty(row.DisplayName) ? "♪" : row.DisplayName[..1].ToUpperInvariant();
            g.DrawText(ch, iconBox, theme.TextPrimary, 12.5f, 600, UiTextAlign.Center, UiVertAlign.Center);
        }

        if (row.Muted)
        {
            g.FillEllipse(iconBtnRect.Right - 5f, iconBtnRect.Y + 6f, 4f, 4f, theme.AccentInverse);
        }

        // Expand button on right
        bool showCompactOutName = legacyLayout && _config.QuickPanelCompactShowOutputName;
        float expandW = showCompactOutName ? 80f : 32f;
        float expandH = legacyLayout ? (showCompactOutName ? 24f : 20f) : 32f;
        var expandRect = new UiRect(headerRect.Right - expandW - 2f, headerRect.CenterY - expandH * 0.5f, expandW, expandH);
        string expandId = $"qp.expand:{index}";
        _hit.Add(expandId, expandRect, UiCursorKind.Hand, L10n.T("Qp.ExpandDevices"));
        if (row.Expanded) g.FillRoundedRect(expandRect, 6f, theme.NavSelected);
        else if (_hit.IsHover(expandId)) g.FillRoundedRect(expandRect, 6f, theme.NavHover);

        if (showCompactOutName)
        {
            string outName = row.CachedOutputLabel ?? L10n.T("Dev.SystemDefaultOut");
            g.DrawText(outName, new UiRect(expandRect.X + 4f, expandRect.Y, expandRect.Width - 18f, expandRect.Height),
                row.Expanded ? theme.Accent : theme.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            g.DrawIcon(UiIcon.ChevronDown, new UiRect(expandRect.Right - 14f, expandRect.CenterY - 5f, 10f, 10f),
                row.Expanded ? theme.Accent : theme.TextSecondary, 1.8f, row.Expanded ? 180f : 0f);
        }
        else
        {
            g.DrawIcon(UiIcon.ChevronDown, new UiRect(expandRect.CenterX - 5.5f, expandRect.CenterY - 5.5f, 11f, 11f),
                row.Expanded ? theme.Accent : theme.TextSecondary, 1.8f, row.Expanded ? 180f : 0f);
        }

        float midX = iconBtnRect.Right + 4f;
        float midW = MathF.Max(40f, expandRect.X - 6f - midX);
        string sliderId = $"qp.appslider:{index}";

        if (legacyLayout)
        {
            var sliderRect = new UiRect(midX, headerRect.CenterY - 16f, midW, 32f);
            UiPrimitives.DrawVolumeSlider(g, theme, _hit, sliderId, sliderRect,
                row.Volume / 100f, row.Peak, row.Muted, _config.ShowAppPeakMeter && !row.IsExternal,
                trackHeight: 4f, thumbDiameter: 14f, enabled: row.Available);
        }
        else
        {
            string title = row.DisplayName;
            if (!string.IsNullOrEmpty(row.App.SourceLabel))
                title += $" · {row.App.SourceLabel}";
            if (_config.QuickPanelShowOutputName && !string.IsNullOrEmpty(row.CachedOutputLabel))
                title += $" · {row.CachedOutputLabel}";
            var nameRect = new UiRect(midX + 6f, headerRect.Y + 2f, MathF.Max(20f, midW - 12f), 16f);
            g.DrawText(title, nameRect, row.Available ? theme.TextPrimary : theme.TextSecondary, 11.5f, isCurrent ? 700 : 600, UiTextAlign.Leading, UiVertAlign.Center);

            var sliderRect = new UiRect(midX, headerRect.Y + 18f, midW, 32f);
            UiPrimitives.DrawVolumeSlider(g, theme, _hit, sliderId, sliderRect,
                row.Volume / 100f, row.Peak, row.Muted, _config.ShowAppPeakMeter && !row.IsExternal,
                trackHeight: 4f, thumbDiameter: 14f, enabled: row.Available);
        }

        // Expanded device wrap-grid
        if (!row.Expanded) return;

        float devX = rowBounds.X + 26f;
        float devW = MathF.Max(60f, rowBounds.Right - devX);
        float outLabelY = headerRect.Bottom + 4f;
        g.DrawText(L10n.T("Qp.Output"), new UiRect(devX, outLabelY, devW, 16f), theme.TextSecondary, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);

        foreach (var (devId, label, relRect, isExit) in outPills)
        {
            var btnRect = relRect.Offset(rowBounds.X, rowBounds.Y);
            if (isExit)
            {
                string exitId = $"qp.appexit:{index}";
                _hit.Add(exitId, btnRect, UiCursorKind.Hand, L10n.T("Phone.StopReceivingApp"));
                bool exitHot = _hit.IsHover(exitId);
                g.FillRoundedRect(btnRect, 7f, theme.SurfaceBg);
                g.DrawRoundedRect(btnRect, 7f, exitHot ? theme.Fail : theme.Border, 1f);
                g.DrawText(label, btnRect.Deflate(6f, 0f), exitHot ? theme.Fail : theme.TextPrimary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
                continue;
            }

            string hitId = $"qp.appdev:{index}:{devId}";
            _hit.Add(hitId, btnRect, UiCursorKind.Hand, label);

            bool isNative = IdCmp.Equals(row.NativeOutputId, devId);
            bool isMirror = !isNative && row.SelectedOutputs.Contains(devId);
            bool hot = _hit.IsHover(hitId);

            UiColor bg = isNative ? theme.DeviceNativeBg : isMirror ? theme.Overlay : theme.SurfaceBg;
            UiColor fg = isNative ? theme.DeviceNativeFg : theme.TextPrimary;
            UiColor brd = isNative ? theme.Accent : hot ? theme.Accent : isMirror ? theme.DeviceMirrorBorder : theme.Border;

            g.FillRoundedRect(btnRect, 7f, bg);
            g.DrawRoundedRect(btnRect, 7f, brd, 1f);
            g.DrawText(label, btnRect.Deflate(6f, 0f), fg, 11.5f, isNative ? 600 : 400, UiTextAlign.Center, UiVertAlign.Center);
        }

        if (inPills.Count > 0)
        {
            float inLabelY = inPills[0].Rect.Y - 20f + rowBounds.Y;
            g.DrawText(L10n.T("Qp.Input"), new UiRect(devX, inLabelY, devW, 16f), theme.TextSecondary, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);

            foreach (var (devId, label, relRect) in inPills)
            {
                var btnRect = relRect.Offset(rowBounds.X, rowBounds.Y);
                string hitId = $"qp.appin:{index}:{devId}";
                _hit.Add(hitId, btnRect, UiCursorKind.Hand, label);

                bool isNativeIn = IdCmp.Equals(row.NativeInputId, devId);
                bool hot = _hit.IsHover(hitId);

                UiColor bg = isNativeIn ? theme.DeviceNativeBg : theme.SurfaceBg;
                UiColor fg = isNativeIn ? theme.DeviceNativeFg : theme.TextPrimary;
                UiColor brd = isNativeIn ? theme.Accent : hot ? theme.Accent : theme.Border;

                g.FillRoundedRect(btnRect, 7f, bg);
                g.DrawRoundedRect(btnRect, 7f, brd, 1f);
                g.DrawText(label, btnRect.Deflate(6f, 0f), fg, 11.5f, isNativeIn ? 600 : 400, UiTextAlign.Center, UiVertAlign.Center);
            }
        }
    }

    private void RenderFooterBar(D2dTarget g, UiThemePalette theme, UiRect rect)
    {
        // Bottom 3-button toolbar: [GlobalMute] [MicMute] [FullUi] — MicMute always visible and synced to live capture state
        float rx = rect.Right;
        const float btnH = 28f;
        const float gap = 6f;
        var ctx = D2dSharedContext.Instance;

        string fullUiLbl = L10n.T("Qp.FullUi");
        float fullUiW = Math.Clamp(MathF.Ceiling(ctx.MeasureText(fullUiLbl, UiMetrics.FontFamily, 12f, 400, 140f, btnH, false).Width + 20f), 76f, 108f);
        var fullUiRect = new UiRect(rx - fullUiW, rect.Y, fullUiW, btnH);
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "qp.settings", fullUiRect, fullUiLbl, fontSize: 12f, radius: 7f);
        rx = fullUiRect.X - gap;

        string micLbl = L10n.T("Qp.MicMute");
        float micW = Math.Clamp(MathF.Ceiling(ctx.MeasureText(micLbl, UiMetrics.FontFamily, 12f, _globalMicMuted ? 600 : 400, 150f, btnH, false).Width + 20f), 96f, 132f);
        var micRect = new UiRect(rx - micW, rect.Y, micW, btnH);
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "qp.mic", micRect, micLbl,
            enabled: _micKnown, fontSize: 12f, radius: 7f, active: _globalMicMuted);
        rx = micRect.X - gap;

        string gmLbl = L10n.T("Qp.GlobalMute");
        float availGmW = MathF.Max(76f, rx - rect.X);
        float gmW = MathF.Min(availGmW, Math.Clamp(MathF.Ceiling(ctx.MeasureText(gmLbl, UiMetrics.FontFamily, 12f, _globalPlaybackMuted ? 600 : 400, 150f, btnH, false).Width + 20f), 96f, 124f));
        var gmRect = new UiRect(rx - gmW, rect.Y, gmW, btnH);
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "qp.globalmute", gmRect, gmLbl,
            fontSize: 12f, radius: 7f, active: _globalPlaybackMuted);
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (leftDown && _adjustMode && _adjustDragging)
        {
            ViewNative.GetCursorPos(out var cur);
            int dx = cur.X - (int)MathF.Round(_adjustDragStartScreenX);
            int dy = cur.Y - (int)MathF.Round(_adjustDragStartScreenY);

            int vsL = ShellNative.GetSystemMetrics(76); // SM_XVIRTUALSCREEN
            int vsT = ShellNative.GetSystemMetrics(77); // SM_YVIRTUALSCREEN
            int vsW = ShellNative.GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
            int vsH = ShellNative.GetSystemMetrics(79); // SM_CYVIRTUALSCREEN

            int minX = vsL;
            int maxX = Math.Max(minX, vsL + vsW - WidthPx);
            int minY = vsT;
            int maxY = Math.Max(minY, vsT + vsH - HeightPx);

            int nextX = Math.Clamp(_adjustDragWinStartX + dx, minX, maxX);
            int nextY = Math.Clamp(_adjustDragWinStartY + dy, minY, maxY);
            MovePx(nextX, nextY);
            RepositionCompanions();
            return;
        }

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

#if SONICROUTE_PHONE
        if (leftDown && _draggingCrossDevice)
        {
            ViewNative.GetCursorPos(out var cur);
            CrossDeviceDragHost.UpdateDrag(cur.X, cur.Y);
            return;
        }
        if (leftDown && _pendingDragRowIdx >= 0 && _pendingDragRowIdx < _rows.Count)
        {
            if (MathF.Abs(xDip - _appDragStartDipX) > 6f || MathF.Abs(yDip - _appDragStartDipY) > 6f)
            {
                var dragRow = _rows[_pendingDragRowIdx];
                _pendingDragRowIdx = -1;
                _hit.ActiveId = null;
                if (!dragRow.IsExternal && dragRow.Pid > 0 && _phoneCompanion is { IsVisible: true })
                {
                    _draggingCrossDevice = true;
                    ViewNative.GetCursorPos(out var cur);
                    CrossDeviceDragHost.BeginDrag(dragRow.App, cur.X, cur.Y);
                    SetCursorKind(UiCursorKind.Hand);
                    Invalidate();
                    return;
                }
            }
        }
#endif

        if (leftDown && _scroll.DragMove(yDip)) { Invalidate(); return; }

        if (leftDown && _draggingMaster)
        {
            int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, xDip);
            if (_masterVolume != val)
            {
                _masterVolume = val;
                QueueMasterVolume(val);
                SyncMasterToOutputPopup(val, null);
                Invalidate();
            }
            return;
        }

        if (leftDown && _draggingRowIdx >= 0 && _draggingRowIdx < _rows.Count)
        {
            var row = _rows[_draggingRowIdx];
            int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, xDip);
            if (row.Volume != val)
            {
                row.Volume = val;
                QueueAppVolume(row, val);
                Invalidate();
            }
            return;
        }

        if (_hit.UpdateHover(xDip, yDip))
        {
            SetCursorKind(_adjustMode ? UiCursorKind.Hand : (_hit.Hovered?.Cursor ?? UiCursorKind.Arrow));
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
        if (e.Button == UiMouseButton.Right)
        {
            if (_adjustMode)
            {
                CancelAdjustMode();
                return;
            }
            var rHit = _hit.HitTest(e.X, e.Y);
            _rightPressId = rHit?.Id;
            return;
        }

        if (e.Button != UiMouseButton.Left) return;

        ClosePresetMenu();

        if (_adjustMode)
        {
            _adjustDragging = true;
            ViewNative.GetCursorPos(out var cur);
            _adjustDragStartScreenX = cur.X;
            _adjustDragStartScreenY = cur.Y;
            _adjustDragWinStartX = LeftPx;
            _adjustDragWinStartY = TopPx;
            return;
        }

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
            QueueMasterVolume(val);
            SyncMasterToOutputPopup(val, null);
            Invalidate();
            return;
        }

        if (hit.Value.Id.StartsWith("qp.appslider:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(13), out int rIdx))
        {
            if (rIdx >= 0 && rIdx < _rows.Count && _rows[rIdx].Available)
            {
                _draggingRowIdx = rIdx;
                _draggingSliderRect = hit.Value.Bounds;
                int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, e.X);
                _rows[rIdx].Volume = val;
                QueueAppVolume(_rows[rIdx], val);
                Invalidate();
            }
            return;
        }

#if SONICROUTE_PHONE
        if (hit.Value.Id.StartsWith("qp.appmute:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(11), out int dragIdx))
        {
            if (dragIdx >= 0 && dragIdx < _rows.Count && !_rows[dragIdx].IsExternal && _phoneCompanion is { IsVisible: true })
            {
                _pendingDragRowIdx = dragIdx;
                _appDragStartDipX = e.X;
                _appDragStartDipY = e.Y;
            }
        }
#endif

        Invalidate();
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (e.Button == UiMouseButton.Right)
        {
            string? pressed = _rightPressId;
            _rightPressId = null;
            var rHit = _hit.HitTest(e.X, e.Y);
            if (rHit is { } rh && rh.Id == pressed)
            {
                if (rh.Id == "qp.masterslider")
                {
                    ShowMasterPresetsMenu(e.X, e.Y);
                }
                else if (rh.Id.StartsWith("qp.appslider:", StringComparison.Ordinal) && int.TryParse(rh.Id.AsSpan(13), out int pIdx))
                {
                    if (pIdx >= 0 && pIdx < _rows.Count && _rows[pIdx].Available)
                        ShowAppPresetsMenu(_rows[pIdx], e.X, e.Y);
                }
            }
            return;
        }

        if (_adjustMode && _adjustDragging)
        {
            _adjustDragging = false;
            SaveAdjustPosition();
            return;
        }

        if (_resizingHeight)
        {
            _resizingHeight = false;
            _config.QuickPanelHeight = (int)Math.Clamp(MathF.Round(HeightDip), 350f, 850f);
            ConfigService.TrySave(_config);
            _host.ViewSettingsChanged();
            RepositionCompanions();
            return;
        }

#if SONICROUTE_PHONE
        _pendingDragRowIdx = -1;
        if (_draggingCrossDevice)
        {
            _draggingCrossDevice = false;
            _hit.ActiveId = null;
            ViewNative.GetCursorPos(out var cur);
            CrossDeviceDragHost.EndDrag(cur.X, cur.Y);
            SetCursorKind(UiCursorKind.Arrow);
            Invalidate();
            return;
        }
#endif

        if (_scroll.Dragging) { _scroll.EndDrag(); Invalidate(); return; }
        if (_draggingMaster) { _draggingMaster = false; _hit.ActiveId = null; _host.FlushVolumeWrites(); Invalidate(); return; }
        if (_draggingRowIdx >= 0) { _draggingRowIdx = -1; _hit.ActiveId = null; _host.FlushVolumeWrites(); Invalidate(); return; }

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
            case "qp.globalmute":
                ToggleGlobalOutputMute();
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
                if (hit.Value.Id.StartsWith("qp.appmute:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(11), out int mIdx))
                {
                    ToggleAppMute(mIdx);
                }
                else if (hit.Value.Id.StartsWith("qp.expand:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(10), out int eIdx))
                {
                    ToggleRowExpand(eIdx);
                }
                else if (hit.Value.Id.StartsWith("qp.appexit:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(11), out int xIdx))
                {
                    DisconnectExternalRow(xIdx);
                }
                else if (hit.Value.Id.StartsWith("qp.appdev:", StringComparison.Ordinal))
                {
                    string rest = hit.Value.Id[10..];
                    int colon = rest.IndexOf(':');
                    if (colon > 0 && int.TryParse(rest[..colon], out int aIdx))
                    {
                        string devId = rest[(colon + 1)..];
                        SelectAppOutput(aIdx, devId, e.Ctrl, e.Shift);
                    }
                }
                else if (hit.Value.Id.StartsWith("qp.appin:", StringComparison.Ordinal))
                {
                    string rest = hit.Value.Id[9..];
                    int colon = rest.IndexOf(':');
                    if (colon > 0 && int.TryParse(rest[..colon], out int iIdx))
                    {
                        string devId = rest[(colon + 1)..];
                        SelectAppInput(iIdx, devId);
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
                QueueMasterVolume(_masterVolume);
                SyncMasterToOutputPopup(_masterVolume, null);
                Invalidate();
                return;
            }
            if (h.Id.StartsWith("qp.appslider:", StringComparison.Ordinal) && int.TryParse(h.Id.AsSpan(13), out int rIdx))
            {
                if (rIdx >= 0 && rIdx < _rows.Count && _rows[rIdx].Available)
                {
                    var row = _rows[rIdx];
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
            if (_adjustMode)
            {
                CancelAdjustMode();
                return true;
            }
#if SONICROUTE_PHONE
            if (_draggingCrossDevice)
            {
                _draggingCrossDevice = false;
                CrossDeviceDragHost.CancelDrag();
                Invalidate();
                return true;
            }
#endif
            if (_presetMenu is { IsDisposed: false, IsVisible: true }) { ClosePresetMenu(); return true; }
            if (_comboPopup is { IsDisposed: false, IsVisible: true }) { CloseCombo(); return true; }
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
        if (_pinned || _adjustMode || HasOpenModal || !IsVisible) return;
        _host.TryPost(() =>
        {
            if (_pinned || _adjustMode || HasOpenModal || !IsVisible) return;
            nint fg = ShellNative.GetForegroundWindow();
            if (fg == Handle) return;
            if (_outputVolumesPopup is { IsVisible: true } && fg == _outputVolumesPopup.Handle) return;
#if SONICROUTE_PHONE
            if (_phoneCompanion is { IsVisible: true } && (fg == _phoneCompanion.Handle || _phoneCompanion.HasOpenModal)) return;
#endif
            if (_comboPopup is { IsDisposed: false, IsVisible: true } && fg == _comboPopup.Handle) return;
            Hide();
            OnDismissRequested?.Invoke();
        });
    }

    private void ShowMasterPresetsMenu(float xDip, float yDip)
    {
        ClosePresetMenu();
        var items = new List<UiMenuItem>(PresetPercentages.Length);
        foreach (int pct in PresetPercentages)
        {
            int captured = pct;
            items.Add(new UiMenuItem
            {
                Text = $"{captured}%",
                Checked = _masterVolume == captured,
                Click = () =>
                {
                    _presetMenu = null;
                    _masterVolume = captured;
                    QueueMasterVolume(captured);
                    SyncMasterToOutputPopup(captured, null);
                    Invalidate();
                },
            });
        }
        var pt = DipToScreenPx(xDip, yDip);
        _presetMenu = PopupMenuWindow.ShowAtCursor(_theme, items, Handle, pt.X, pt.Y);
    }

    private void ShowAppPresetsMenu(AppRowState row, float xDip, float yDip)
    {
        ClosePresetMenu();
        var items = new List<UiMenuItem>(PresetPercentages.Length);
        foreach (int pct in PresetPercentages)
        {
            int captured = pct;
            items.Add(new UiMenuItem
            {
                Text = $"{captured}%",
                Checked = row.Volume == captured,
                Click = () =>
                {
                    _presetMenu = null;
                    row.Volume = captured;
                    QueueAppVolume(row, captured);
                    Invalidate();
                },
            });
        }
        var pt = DipToScreenPx(xDip, yDip);
        _presetMenu = PopupMenuWindow.ShowAtCursor(_theme, items, Handle, pt.X, pt.Y);
    }

    private void ClosePresetMenu()
    {
        if (_presetMenu is { } menu)
        {
            _presetMenu = null;
            if (!menu.IsDisposed) menu.CloseWindow();
        }
    }

    private void QueueAppVolume(AppRowState row, int value)
    {
        if (row.IsExternal)
        {
            int ver = _externalVolumeVersion.TryGetValue(row.Identity, out int prev) ? prev + 1 : 1;
            _externalVolumeVersion[row.Identity] = ver;
            var app = row.App;
            _ = Task.Run(() =>
            {
                try
                {
                    var st = _host.Audio.Volume(app, level: value);
                    _host.TryPost(() =>
                    {
                        if (IsDisposed) return;
                        if (_externalVolumeVersion.TryGetValue(row.Identity, out int cur) && cur == ver && st.Percent >= 0)
                            row.Volume = st.Percent;
                        if (IsVisible) Invalidate();
                    });
                }
                catch { }
            });
            return;
        }

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

    private void QueueMasterVolume(int level)
    {
        string? target = _config.PanelChangeSystemDefault ? null : (_panelOutputId ?? _defaultOutputId);
        bool startWorker = false;
        lock (_masterWriteGate)
        {
            _pendingMasterWrite = (target, level);
            if (!_masterWriteRunning)
            {
                _masterWriteRunning = true;
                startWorker = true;
            }
        }
        if (!startWorker) return;

        _ = Task.Run(() =>
        {
            while (true)
            {
                (string? Endpoint, int Level) job;
                lock (_masterWriteGate)
                {
                    if (!_pendingMasterWrite.HasValue)
                    {
                        _masterWriteRunning = false;
                        break;
                    }
                    job = _pendingMasterWrite.Value;
                    _pendingMasterWrite = null;
                }
                try { _host.Audio.Volume(Flow.Render, job.Endpoint, 0, level: job.Level); }
                catch { }
            }
        });
    }

    private void ToggleMasterMute()
    {
        _masterMuted = !_masterMuted;
        SyncMasterToOutputPopup(null, _masterMuted);
        Invalidate();

        string? target = _config.PanelChangeSystemDefault ? null : (_panelOutputId ?? _defaultOutputId);
        string title = ResolveDeviceDisplayName(_outputs, target);
        _ = Task.Run(() =>
        {
            try
            {
                var st = _host.Audio.Volume(Flow.Render, target, 0, toggle: true);
                _host.TryPost(() =>
                {
                    _masterMuted = st.Muted;
                    _masterVolume = st.Percent;
                    SyncMasterToOutputPopup(st.Percent, st.Muted);
                    _host.PublishOsd(title, L10n.T(st.Muted ? "Qp.Muted" : "Qp.Unmuted"),
                        volume: st.Percent, muted: st.Muted, success: true);
                    if (IsVisible) Invalidate();
                });
            }
            catch
            {
                _host.TryPost(() => _host.PublishOsd(title, L10n.T("Tray.AudioUnavailable"), success: false));
            }
        });
    }

    private void ToggleGlobalOutputMute()
    {
        _globalPlaybackMuted = !_globalPlaybackMuted;
        _masterMuted = _globalPlaybackMuted;
        _outputVolumesPopup?.SyncEndpointState(null, null, _globalPlaybackMuted);
        Invalidate();

        _ = Task.Run(() =>
        {
            try
            {
                bool muted = _host.Audio.GlobalPlaybackMute(null);
                _host.TryPost(() =>
                {
                    _globalPlaybackMuted = muted;
                    _masterMuted = muted;
                    _outputVolumesPopup?.SyncEndpointState(null, null, muted);
                    _host.PublishOsd(L10n.T("Qp.AllApps"), L10n.T(muted ? "Qp.GlobalMuted" : "Qp.GlobalUnmuted"),
                        muted: muted, success: true);
                    if (IsVisible) Invalidate();
                });
            }
            catch
            {
                _host.TryPost(() =>
                {
                    _host.PublishOsd(L10n.T("Qp.AllApps"), L10n.T("Tray.AudioUnavailable"), success: false);
                    RefreshData();
                });
            }
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
                    _host.PublishOsd(L10n.T("Ov.MuteMic"), L10n.T(muted ? "Qp.MicMuted" : "Qp.MicUnmuted"),
                        muted: muted, success: true);
                    if (IsVisible) Invalidate();
                });
            }
            catch
            {
                _host.TryPost(() => _host.PublishOsd(L10n.T("Ov.MuteMic"), L10n.T("Tray.AudioUnavailable"), success: false));
            }
        });
    }

    private void ToggleAppMute(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var row = _rows[rowIdx];
        if (!row.Available) return;

        row.Muted = !row.Muted;
        Invalidate();
        var app = row.App;
        _ = Task.Run(() =>
        {
            try
            {
                var st = _host.Audio.Volume(app, toggle: true);
                _host.TryPost(() =>
                {
                    row.Muted = st.Muted;
                    if (st.Percent >= 0) row.Volume = st.Percent;
                    if (IsVisible) Invalidate();
                });
            }
            catch { }
        });
    }

    private void ToggleRowExpand(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var target = _rows[rowIdx];
        bool next = !target.Expanded;

        _expandedAppId = next ? target.Identity : null;
        foreach (var row in _rows)
        {
            row.Expanded = next && ReferenceEquals(row, target);
        }
        Invalidate();

        if (!next) return;
        var app = target.App;
        bool includeInputs = _config.ExperimentalMic && _config.MicInPanel && !target.IsExternal;
        _ = Task.Run(() =>
        {
            try
            {
                string? persisted = _host.Audio.ReadRoute(app, Flow.Render);
                string route = string.IsNullOrWhiteSpace(persisted) || AudioService.IsSystemDefault(persisted)
                    ? AudioService.SystemDefaultDeviceId
                    : AudioPolicyConfig.UnpackDeviceId(persisted);
                var selected = _host.Audio.GetSelectedOutputs(app).ToHashSet(IdCmp);
                if (selected.Count == 0) selected.Add(route);

                string inputRoute = AudioService.SystemDefaultDeviceId;
                if (includeInputs)
                {
                    string? pin = _host.Audio.ReadRoute(checked((int)app.ProcessId), Flow.Capture);
                    inputRoute = string.IsNullOrWhiteSpace(pin) || AudioService.IsSystemDefault(pin)
                        ? AudioService.SystemDefaultDeviceId
                        : AudioPolicyConfig.UnpackDeviceId(pin);
                }

                _host.TryPost(() =>
                {
                    target.NativeOutputId = route;
                    target.SelectedOutputs = selected;
                    target.NativeInputId = inputRoute;
                    if (IsVisible) Invalidate();
                });
            }
            catch { }
        });
    }

    private void SelectAppOutput(int rowIdx, string devId, bool ctrl, bool shift)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var row = _rows[rowIdx];

        var choices = BuildOutputChoicesForRow(row);
        string[] order = choices.Select(c => c.Id).ToArray();
        bool isMultiExternal = IsMultiOutputExternal(row.App);
        if (row.IsExternal && !isMultiExternal)
        {
            ctrl = false;
            shift = false;
        }
        if (!row.IsExternal && ctrl && shift && PcRoutingApi.IsPhoneOutputDevice(devId))
            return;

        string[] nextOutputs = SelectPlaybackRange(order, row.SelectedOutputs, devId, row.OutputAnchor, ctrl, shift);
        if (nextOutputs.Length == 0) nextOutputs = [AudioService.SystemDefaultDeviceId];

#if SONICROUTE_PHONE
        if (row.IsExternal && _host.Audio.ExternalAudio?.FindSession(row.App.ExternalSourceId) is AndroidAudioSession android && android.State.Device is { } sourceDevice)
        {
            bool OwnForcedOutput(string id) => AndroidOutputIdentity.TryParseDeviceId(id, out var key, out _)
                && IdCmp.Equals(key, AndroidDeviceIdentity.Key(sourceDevice));
            if (nextOutputs.Contains(AndroidAudioSession.SourcePhoneOutputId, IdCmp) && nextOutputs.Any(OwnForcedOutput))
            {
                nextOutputs = IdCmp.Equals(devId, AndroidAudioSession.SourcePhoneOutputId)
                    ? nextOutputs.Where(id => !OwnForcedOutput(id)).ToArray()
                    : nextOutputs.Where(id => !IdCmp.Equals(id, AndroidAudioSession.SourcePhoneOutputId)).ToArray();
                if (nextOutputs.Length == 0) nextOutputs = [AudioService.SystemDefaultDeviceId];
            }
        }
#endif

        row.OutputAnchor = shift && !ctrl ? (row.OutputAnchor ?? devId) : devId;
        string? explicitPrimary = ctrl && shift ? devId
            : !ctrl && !shift ? (!PcRoutingApi.IsPhoneOutputDevice(devId) ? devId : null)
            : row.SelectedOutputs.Contains(row.NativeOutputId) && nextOutputs.Contains(row.NativeOutputId, IdCmp) ? row.NativeOutputId : null;

        string preferred = SelectOptimisticPrimaryOutput(
            nextOutputs,
            explicitPrimary ?? row.NativeOutputId,
            preferPhysical: !row.IsExternal,
            lowPriorityId: LowPriorityOutputId(row.App));

        row.SelectedOutputs = new HashSet<string>(nextOutputs, IdCmp);
        row.NativeOutputId = preferred;
        row.CachedOutputLabel = ResolveDeviceDisplayName(_allRenderOutputs.Length > 0 ? _allRenderOutputs : _outputs, preferred);
        Invalidate();

        var app = row.App;
        _ = Task.Run(async () =>
        {
            try
            {
#if SONICROUTE_PHONE
                if (app.IsExternal)
                {
                    await _host.Audio.SetExternalOutputsAsync(app, nextOutputs).ConfigureAwait(false);
                }
                else
#endif
                if (!ctrl && !shift && !PcRoutingApi.IsPhoneOutputDevice(devId))
                {
                    await _host.CurrentApp.SelectOutputAsync(row.Pid, devId).ConfigureAwait(false);
                }
                else
                {
                    await _host.CurrentApp.SetOutputsAsync(row.Pid, preferred, nextOutputs).ConfigureAwait(false);
                }
            }
            catch { }

            _host.TryPost(RefreshData);
        });
    }

    private void SelectAppInput(int rowIdx, string devId)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var row = _rows[rowIdx];
        if (row.IsExternal) return;

        row.NativeInputId = devId;
        Invalidate();

        int pid = row.Pid;
        string? target = AudioService.IsSystemDefault(devId) ? null : devId;
        _ = Task.Run(() =>
        {
            try { _host.Audio.SetRoute(pid, Flow.Capture, target); }
            catch { }
            _host.TryPost(RefreshData);
        });
    }

    private void DisconnectExternalRow(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var row = _rows[rowIdx];
        if (!row.IsExternal) return;
#if SONICROUTE_PHONE
        string? sourceId = row.App.ExternalSourceId;
        if (string.IsNullOrWhiteSpace(sourceId)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_host.HasPhone && _host.Phone.Sources.FindSource(sourceId) is { } androidSrc)
                {
                    await _host.Phone.Sources.RemoveAsync(androidSrc.Id).ConfigureAwait(false);
                }
                else if (_host.HasAirPlay && _host.AirPlay.Source is { } ap && string.Equals(ap.Id, sourceId, StringComparison.Ordinal))
                {
                    _host.AirPlay.Disconnect();
                }
            }
            catch { }
            _host.TryPost(RefreshData);
        });
#endif
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
        _comboPopup.Closed += OnComboClosed;
    }

    private void OnComboClosed()
    {
        if (_comboPopup != null) _comboPopup.Closed -= OnComboClosed;
        _comboPopup = null;
        CheckAutoDismiss();
    }

    private void CloseCombo()
    {
        if (_comboPopup is { } pop)
        {
            _comboPopup = null;
            pop.Closed -= OnComboClosed;
            if (!pop.IsDisposed) { pop.CloseWindow(); pop.Dispose(); }
        }
    }

    private void ToggleOutputVolumesPopup()
    {
        if (_adjustMode) return;
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

        if (_config.QuickPanelShowAllOutputVolumes && !_adjustMode)
        {
            _outputVolumesPopup ??= new OutputVolumesPopupWindow(_host, _config, RefreshData, CheckAutoDismiss, OnOutputPopupEndpointChanged);
            _outputVolumesPopup.ShowForOwner(this, phoneOccupied);
        }
        else if (_outputVolumesPopup is { IsVisible: true })
        {
            _outputVolumesPopup.Hide();
        }
    }

    private void OnOutputPopupEndpointChanged(string? deviceId, int? volume, bool? muted)
    {
        string? masterTarget = _config.PanelChangeSystemDefault ? _defaultOutputId : (_panelOutputId ?? _defaultOutputId);
        if (deviceId != null && masterTarget != null && !IdCmp.Equals(deviceId, masterTarget))
            return;

        if (volume.HasValue && !_draggingMaster) _masterVolume = volume.Value;
        if (muted.HasValue) _masterMuted = muted.Value;
        if (IsVisible) Invalidate();
    }

    private void SyncMasterToOutputPopup(int? volume, bool? muted)
    {
        string? masterTarget = _config.PanelChangeSystemDefault ? _defaultOutputId : (_panelOutputId ?? _defaultOutputId);
        if (masterTarget != null) _outputVolumesPopup?.SyncEndpointState(masterTarget, volume, muted);
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
            // Restore using primary monitor DPI -> physical pixel probe so multi-DPI coordinates never collide across monitors
            GetMonitorWorkAreaForPoint(0, 0, out _, out _, out _, out _, out uint primaryDpi);
            float ps = primaryDpi / 96f;
            int guessXPx = (int)MathF.Round(_config.QuickPanelCustomX * ps);
            int guessYPx = (int)MathF.Round(_config.QuickPanelCustomY * ps);

            GetMonitorWorkAreaForPoint(guessXPx, guessYPx, out int cml, out int cmt, out int cmr, out int cmb, out uint cDpi);
            float cs = cDpi / 96f;
            int cwPx = (int)MathF.Round(WidthDip * cs);
            int chPx = (int)MathF.Round(HeightDip * cs);
            int cx = Math.Clamp(guessXPx, cml, Math.Max(cml, cmr - cwPx));
            int cy = Math.Clamp(guessYPx, cmt, Math.Max(cmt, cmb - chPx));
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

    private void SaveAdjustPosition()
    {
        GetMonitorWorkAreaForPoint(0, 0, out _, out _, out _, out _, out uint primaryDpi);
        float ps = primaryDpi / 96f;
        _config.QuickPanelPosMode = "custom";
        _config.QuickPanelCustomX = Math.Max(0, (int)MathF.Round(LeftPx / ps));
        _config.QuickPanelCustomY = Math.Max(0, (int)MathF.Round(TopPx / ps));
        ConfigService.TrySave(_config);
        _adjustMode = false;
        _adjustDragging = false;
        _host.ViewSettingsChanged();
        _host.PublishOsd("📍", L10n.T("Exp.PanelPosSaved"), success: true);
        SyncCompanionsVisibility();
        Invalidate();
    }

    private void CancelAdjustMode()
    {
        _adjustMode = false;
        _adjustDragging = false;
        MovePx(_adjustOrigLeftPx, _adjustOrigTopPx);
        SyncCompanionsVisibility();
        Invalidate();
    }

    private void CancelActiveInteractions()
    {
        _resizingHeight = false;
        if (_adjustDragging)
        {
            _adjustDragging = false;
            MovePx(_adjustOrigLeftPx, _adjustOrigTopPx);
        }
        _scroll.EndDrag();
        _draggingMaster = false;
        _draggingRowIdx = -1;
        _rightPressId = null;
        _hit.ActiveId = null;
#if SONICROUTE_PHONE
        _pendingDragRowIdx = -1;
        if (_draggingCrossDevice)
        {
            _draggingCrossDevice = false;
            CrossDeviceDragHost.CancelDrag();
        }
#endif
    }

    private void SubscribeEvents(bool enable)
    {
        if (enable == _eventsSubscribed) return;
        _eventsSubscribed = enable;
        if (enable)
        {
            _host.CurrentApp.CurrentChanged += OnCurrentAppChanged;
            _host.Audio.AudioChanged += OnAudioChanged;
        }
        else
        {
            _host.CurrentApp.CurrentChanged -= OnCurrentAppChanged;
            _host.Audio.AudioChanged -= OnAudioChanged;
        }
    }

    private void OnCurrentAppChanged()
    {
        if (!IsVisible || IsDisposed) return;
        _host.TryPost(() =>
        {
            if (IsVisible && !IsDisposed) Invalidate();
        });
    }

    private void OnAudioChanged()
    {
        if (!IsVisible || IsDisposed) return;
        _host.TryPost(() =>
        {
            if (IsVisible && !IsDisposed) RefreshData();
        });
    }

    private bool IsMultiOutputExternal(AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        return app.IsExternal && _host.Audio.ExternalAudio?.FindSession(app.ExternalSourceId) is IMultiOutputAudioSession;
#else
        return false;
#endif
    }

    private string? LowPriorityOutputId(AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        return app.IsExternal && _host.Audio.ExternalAudio?.FindSession(app.ExternalSourceId) is AndroidAudioSession
            ? AndroidAudioSession.SourcePhoneOutputId
            : null;
#else
        return null;
#endif
    }

    private static string[] SelectPlaybackRange(string[] order, HashSet<string> selected, string clicked, string? anchor, bool ctrl, bool shift)
    {
        if (!shift && !ctrl) return [clicked];
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

    private static string SelectOptimisticPrimaryOutput(IReadOnlyList<string> outputs, string? currentOutputId, bool preferPhysical, string? lowPriorityId)
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

    private bool TryGetExternalIcon(string key, byte[] png, out byte[] bgra, out int w, out int h, out int ver)
    {
        if (_extIconCache.TryGetValue(key, out var cached))
        {
            bgra = cached.Pixels; w = cached.W; h = cached.H; ver = cached.Ver;
            return true;
        }
        if (SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 48, 48, out byte[] pixels, out int pw, out int ph))
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
            int nextVer = ++_extIconVer;
            if (_extIconCache.Count >= 64) _extIconCache.Clear();
            _extIconCache[key] = (pixels, pw, ph, nextVer);
            bgra = pixels; w = pw; h = ph; ver = nextVer;
            return true;
        }
        bgra = []; w = h = ver = 0;
        return false;
    }

    private string ResolveDeviceDisplayName(Endpoint[] devices, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || AudioService.IsSystemDefault(deviceId))
            return L10n.T("Dev.SystemDefaultOut");
#if SONICROUTE_PHONE
        if (IdCmp.Equals(deviceId, AndroidAudioSession.SourcePhoneOutputId))
            return L10n.T("Phone.NativeAudioOutput");
#endif
        if (_config.DeviceNames.TryGetValue(deviceId, out string? custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        var found = devices.FirstOrDefault(d => IdCmp.Equals(d.Id, deviceId));
        return found?.Name ?? L10n.T("Dev.Unknown");
    }

    private static UiThemePalette ScaleThemeOpacity(UiThemePalette src, float factor) => new()
    {
        IsDark = src.IsDark,
        OpacityPercent = src.OpacityPercent,
        WindowBg = src.WindowBg.WithAlphaFraction(factor),
        WindowBgAlpha = src.WindowBgAlpha.WithAlphaFraction(factor),
        SurfaceBg = src.SurfaceBg.WithAlphaFraction(factor),
        SurfaceBgAlpha = src.SurfaceBgAlpha.WithAlphaFraction(factor),
        SurfaceAlt = src.SurfaceAlt.WithAlphaFraction(factor),
        SurfaceCard = src.SurfaceCard.WithAlphaFraction(factor),
        TextPrimary = src.TextPrimary.WithAlphaFraction(factor),
        TextSecondary = src.TextSecondary.WithAlphaFraction(factor),
        Border = src.Border.WithAlphaFraction(factor),
        NavHover = src.NavHover.WithAlphaFraction(factor),
        NavSelected = src.NavSelected.WithAlphaFraction(factor),
        SliderBg = src.SliderBg.WithAlphaFraction(factor),
        Overlay = src.Overlay.WithAlphaFraction(factor),
        Accent = src.Accent.WithAlphaFraction(factor),
        AccentHover = src.AccentHover.WithAlphaFraction(factor),
        AccentPressed = src.AccentPressed.WithAlphaFraction(factor),
        AccentDisabled = src.AccentDisabled.WithAlphaFraction(factor),
        AccentPeak = src.AccentPeak.WithAlphaFraction(factor),
        AccentInverse = src.AccentInverse.WithAlphaFraction(factor),
        OnAccent = src.OnAccent.WithAlphaFraction(factor),
        AccentIconInk = src.AccentIconInk.WithAlphaFraction(factor),
        Success = src.Success.WithAlphaFraction(factor),
        Fail = src.Fail.WithAlphaFraction(factor),
        DeviceNativeBg = src.DeviceNativeBg.WithAlphaFraction(factor),
        DeviceNativeFg = src.DeviceNativeFg.WithAlphaFraction(factor),
        DeviceMirrorBorder = src.DeviceMirrorBorder.WithAlphaFraction(factor),
        OsdTitleText = src.OsdTitleText.WithAlphaFraction(factor),
        OsdTitle = src.OsdTitle.WithAlphaFraction(factor),
        OsdAccent = src.OsdAccent.WithAlphaFraction(factor),
    };
}
