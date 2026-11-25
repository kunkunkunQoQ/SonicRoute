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
        public required string HitKey;
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

    private static readonly string VersionLabel = "v" + MainWindowShell.ProductVersion;
    private readonly ApplicationHost _host;
    private readonly AppConfig _config;
    private readonly Action? _onOpenMain;
    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        Invalidate();
    }

    protected override UiHitMap InputMap => _hit;
    protected override float InputOffsetY => _drawnSlideY;
    private readonly UiHitMap _hit = new();
    private readonly UiScrollState _scroll = new();
    private readonly Dictionary<string, (byte[] Pixels, int W, int H, int Ver)> _extIconCache = new(StringComparer.Ordinal);
    private int _extIconVer;



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
    private readonly Dictionary<(string Identity, Flow Flow), long> _pendingRoutes = new();
    private Task _inputWrites = Task.CompletedTask;
#if SONICROUTE_PHONE
    private Task _externalOutputWrites = Task.CompletedTask;
#endif
    private long _routeRevision;

    private bool _pinned;
    private bool _loading;
    private bool _refreshQueued;
    private bool _eventsSubscribed;
    private bool _peakReadInFlight;
    private int[] _peakPids = [];
    private int _timerInterval;
    private int _draggingRowIdx = -1;
    private bool _draggingMaster;
    private UiRect _draggingSliderRect;
    private float _drawnSlideY;
    private string? _rightPressId;

#if SONICROUTE_PHONE
    private int _pendingDragRowIdx = -1;
    private float _appDragStartDipX;
    private float _appDragStartDipY;
    private bool _draggingCrossDevice;
    private DragIconWindow? _dragIcon;
    private UiRect _phoneDropBounds;
    private bool _showPhoneDrop;
    private bool _airPlayDrop;
    private AndroidAudioSource? _androidDrag;
    private bool? _sharedDrop;
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
        Action? onDismissRequested = null)
        : base(UiMetrics.QuickPanelWindowWidth, Math.Clamp(config.QuickPanelHeight, 350, 850), UiMetrics.QuickPanelWindowWidth, UiMetrics.QuickPanelMinHeight)
    {
        _host = host;
        _config = config;
        _onOpenMain = onOpenMain;
        if (onDismissRequested != null) OnDismissRequested += onDismissRequested;
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
        long readRevision = _host.EndpointWriteRevision;
        long routesAtRead = _routeRevision;
        long volumeAtRead = _host.AppVolumeRevision;

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
                        HitKey = Uri.EscapeDataString(app.Identity),
                        Pid = pid,
                        IsExternal = app.IsExternal,
                        DisplayName = label,
                        Volume = vol,
                        Muted = muted,
                        Available = avail,
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
                    if (_draggingRowIdx < newRows.Count && newRows[_draggingRowIdx].Identity == draggingOld.Identity)
                        newRows[_draggingRowIdx].Volume = draggingOld.Volume;
                    else
                    {
                        _draggingRowIdx = -1;
                        _hit.ActiveId = null;
                        _host.FlushVolumeWrites();
                    }
                }
#if SONICROUTE_PHONE
                if (_pendingDragRowIdx >= 0 && (_pendingDragRowIdx >= newRows.Count ||
                    _rows[_pendingDragRowIdx].Identity != newRows[_pendingDragRowIdx].Identity))
                    _pendingDragRowIdx = -1;
#endif

                _outputs = render;
                _allRenderOutputs = allRenderWithPhone;
                _inputs = capture;
                _defaultOutputId = defId;
                if (_panelOutputId == null || !render.Any(d => IdCmp.Equals(d.Id, _panelOutputId)))
                    _panelOutputId = defId;
                string? masterId = _config.PanelChangeSystemDefault ? defId : _panelOutputId;
                if (!_draggingMaster && readRevision == _host.EndpointWriteRevision && !_host.EndpointVolumePending(masterId))
                    _masterVolume = masterVol;
                _masterMuted = masterMute;
                _globalPlaybackMuted = globalPlaybackMuted;
                _globalMicMuted = micMuted;
                _micKnown = micKnown;
                _smoothedPeaks.Clear();
                foreach (var previous in _rows)
                    if (previous.Peak > 0f) _smoothedPeaks[previous.Identity] = previous.Peak;
                foreach (var next in newRows)
                {
                    var previous = _rows.Find(r => r.Identity == next.Identity);
                    if (previous == null) continue;
                    if (routesAtRead != _routeRevision || _pendingRoutes.ContainsKey((next.Identity, Flow.Render)))
                    {
                        next.NativeOutputId = previous.NativeOutputId;
                        next.SelectedOutputs = previous.SelectedOutputs;
                        next.CachedOutputLabel = previous.CachedOutputLabel;
                    }
                    next.OutputAnchor = previous.OutputAnchor;
                    if (routesAtRead != _routeRevision || _pendingRoutes.ContainsKey((next.Identity, Flow.Capture)))
                        next.NativeInputId = previous.NativeInputId;
                    if (volumeAtRead != _host.AppVolumeRevision || _host.HasPendingVolume(next.App))
                        next.Volume = previous.Volume;
                }
                _rows = newRows;

                foreach (var row in _rows)
                {
                    row.Peak = _smoothedPeaks.GetValueOrDefault(row.Identity);
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

                UpdatePeakTargets();
                if (IsVisible) Invalidate();
                if (_refreshQueued && IsVisible)
                {
                    _refreshQueued = false;
                    RefreshData();
                }
            });
        });
    }

    private void UpdatePeakTargets()
    {
        _peakPids = _rows.Where(static r => !r.IsExternal && r.Pid > 0 && r.Available && !r.Muted)
            .Select(static r => r.Pid).Distinct().ToArray();
        foreach (var row in _rows)
            if (row.Muted || !row.Available) row.Peak = 0f;
        UpdateAnimationTimer();
    }

    private void UpdateAnimationTimer()
    {
        int interval = 0;
        if (IsVisible && !IsDisposed)
        {
            if (_animatingEntrance) interval = 16;
            else if (_config.ShowAppPeakMeter && _peakPids.Length != 0) interval = 50;
        }
        if (_timerInterval == interval) return;
        StopAnimationTimer();
        _timerInterval = interval;
        if (interval != 0) StartAnimationTimer(interval);
    }

    protected override void OnShown()
    {
        base.OnShown();
        SubscribeEvents(true);
        UpdateAnimationTimer();
    }

    protected override void OnHidden()
    {
        StopAnimationTimer();
        _timerInterval = 0;
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
        _timerInterval = 0;
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
            UpdateAnimationTimer();
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
                UpdateAnimationTimer();
            }
            Invalidate();
            return;
        }

        if (_peakReadInFlight || _peakPids.Length == 0 || !_config.ShowAppPeakMeter) return;
        int[] pids = _peakPids;

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
        _drawnSlideY = _animatingEntrance ? _entranceSlideY : 0f;
        UiThemePalette theme = _animatingEntrance ? ScaleThemeOpacity(_theme, _entranceOpacity) : _theme;
        if (_animatingEntrance)
        {
            g.PushTransform(new D2dMatrix3x2(1f, 0f, 0f, 1f, 0f, _drawnSlideY));
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

        const float padX = 17f;
        const float padTop = 15f;
        const float padBottom = 13f;
        float innerW = surfaceW - padX * 2f;

        // 1. Header Bar
        float y = padTop;
        var headerRect = new UiRect(padX, y, innerW, 32f);
        float rx = headerRect.Right;

        float titleWidth = D2dSharedContext.Instance.MeasureText(BuildIdentity.DisplayName, UiMetrics.FontFamily,
            14f, 700, innerW, 28f, false).Width;
        var titleRect = new UiRect(padX, y, titleWidth + 4f, 28f);
        _hit.Add("qp.title", titleRect, UiCursorKind.Hand, PinTitleTooltip());
        if (_hit.IsHover("qp.title") || _hit.IsActive("qp.title"))
            g.FillRoundedRect(titleRect, 4f, theme.NavHover);
        g.DrawText(BuildIdentity.DisplayName, titleRect, _pinned ? theme.Accent : theme.TextPrimary,
            14f, 700, UiTextAlign.Leading, UiVertAlign.Top);

#if SONICROUTE_PHONE
        const float phoneBtnW = 30f;
        var phoneRect = new UiRect(rx - phoneBtnW, y - 1f, phoneBtnW, 28f);
        bool phoneOpen = _phoneCompanion is { IsVisible: true };
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "qp.phone", phoneRect, "",
            radius: 8f, icon: UiIcon.Phone, active: phoneOpen,
            tooltip: phoneOpen ? L10n.T("Phone.ClosePhoneAudio") : L10n.T("Phone.OpenPhoneAudio"));
        rx = phoneRect.X - 6f;
#endif

        string verLabel = VersionLabel;
        float verW = D2dSharedContext.Instance.MeasureText(verLabel, UiMetrics.FontFamily, 10.5f, 400, 80f, 20f, false).Width + 4f;
        g.DrawText(verLabel, new UiRect(rx - verW, y, verW, 28f), theme.TextSecondary, 10.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);

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
        float muteW = MathF.Max(54f, D2dSharedContext.Instance.MeasureText(muteText, UiMetrics.FontFamily, 12f, 400, innerW, 28f, false).Width + 22f);
        var masterMuteRect = new UiRect(padX, y + 2f, muteW, 28f);
        DrawToolButton(g, theme, "qp.mastermute", masterMuteRect, muteText, active: _masterMuted);

        var pctRect = new UiRect(padX + innerW - 42f, y, 42f, 32f);
        g.DrawText($"{_masterVolume}%", pctRect, theme.TextPrimary, 12.5f, 600, UiTextAlign.Trailing, UiVertAlign.Center, wrap: false, ellipsis: false);

        var masterSliderRect = new UiRect(masterMuteRect.Right + 10f, y, MathF.Max(40f, pctRect.X - 6f - (masterMuteRect.Right + 10f)), 32f);
        UiPrimitives.DrawVolumeSlider(g, theme, _hit, "qp.masterslider", masterSliderRect,
            _masterVolume / 100f, 0f, _masterMuted, showPeak: false);

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

        var listRect = new UiRect(padX, y, innerW + 14f, MathF.Max(36f, footerTop - 14f - y));
#if SONICROUTE_PHONE
        _phoneDropBounds = listRect;
        if (_showPhoneDrop)
        {
            DrawPhoneDropArea(g, theme, listRect);
            RenderFooterBar(g, theme, new UiRect(padX, footerTop, innerW, footerH));
            return;
        }
#endif
        bool legacyLayout = _config.QuickPanelUseLegacyAppRows;
        bool showInputsInPanel = _config.ExperimentalMic && _config.MicInPanel;
        var currentApp = _host.CurrentApp.Current;

        // At most one row can be expanded. A peak repaint needs no per-row pill lists.
        float collapsedHeight = legacyLayout ? UiMetrics.QuickPanelCompactRowHeight : UiMetrics.QuickPanelFullRowHeight;
        var collapsed = new RowLayout(collapsedHeight, [], []);
        AppRowState? expandedRow = _expandedAppId == null ? null : _rows.Find(static r => r.Expanded);
        RowLayout expanded = expandedRow == null ? collapsed : MeasureExpandedRow(expandedRow, collapsedHeight, showInputsInPanel, listRect.Width);
        float totalContentH = collapsedHeight * _rows.Count + expanded.TotalH - collapsedHeight;
        if (expandedRow != null && totalContentH > listRect.Height)
        {
            expanded = MeasureExpandedRow(expandedRow, collapsedHeight, showInputsInPanel, listRect.Width - UiMetrics.ScrollBarWidth - 4f);
            totalContentH = collapsedHeight * (_rows.Count - 1) + expanded.TotalH;
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
            _hit.PushClip(listRect);
            float curY = listRect.Y - _scroll.Offset;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var layout = ReferenceEquals(row, expandedRow) ? expanded : collapsed;
                if (curY + layout.TotalH >= listRect.Y && curY <= listRect.Bottom)
                {
                    bool isCurrent = currentApp != null && string.Equals(row.Identity, currentApp.Identity, StringComparison.OrdinalIgnoreCase);
                    RenderAppRow(g, theme, row, new UiRect(listRect.X, curY, rowW, layout.TotalH), legacyLayout, isCurrent, layout.OutPills, layout.InPills);
                }
                curY += layout.TotalH;
            }
            _hit.PopClip();
            g.PopClip();
            _scroll.Draw(g, theme, _hit, "qp.scroll", listRect);
        }

        // 7. Footer Toolbar (Global Mute, Mic Mute, Full UI)
        RenderFooterBar(g, theme, new UiRect(padX, footerTop, innerW, footerH));
    }

#if SONICROUTE_PHONE
    internal bool? UpdatePhoneDrop(int xPx, int yPx, bool airPlay = false)
    {
        if (!IsVisible) return null;
        float x = (xPx - LeftPx) / DpiScale;
        float y = (yPx - TopPx) / DpiScale - _drawnSlideY;
        bool? shared = _phoneDropBounds.Contains(x, y) ? !airPlay && y >= _phoneDropBounds.CenterY : null;
        if (!_showPhoneDrop || shared != _sharedDrop || _airPlayDrop != airPlay)
        {
            _showPhoneDrop = true;
            _airPlayDrop = airPlay;
            _sharedDrop = shared;
            Invalidate();
        }
        return shared;
    }

    internal void ClearPhoneDrop()
    {
        if (!_showPhoneDrop) return;
        _showPhoneDrop = false;
        _sharedDrop = null;
        if (IsVisible) Invalidate();
    }

    private void DrawPhoneDropArea(D2dTarget g, UiThemePalette theme, UiRect bounds)
    {
        g.FillRoundedRect(bounds, 8f, theme.SurfaceBg);
        int count = _airPlayDrop ? 1 : 2;
        for (int i = 0; i < count; i++)
        {
            bool shared = i == 1;
            var zone = new UiRect(bounds.X + 2f, (shared ? bounds.CenterY : bounds.Y) + 2f,
                bounds.Width - 4f, bounds.Height / count - 4f);
            if (_sharedDrop == shared) g.FillRoundedRect(zone, 6f, theme.Overlay);
            g.PushClip(zone);
            g.DrawText(L10n.T(_airPlayDrop ? "Dev.SystemDefaultOut" : shared ? "Phone.DropBothPlayTitle" : "Phone.DropComputerOnlyTitle"),
                new UiRect(zone.X + 8f, zone.CenterY - 20f, zone.Width - 16f, 20f), theme.TextPrimary, 13f, 600, UiTextAlign.Center);
            g.DrawText(L10n.T(_airPlayDrop ? "Phone.AirPlayDropWindowsDesc" : shared ? "Phone.DropBothPlayDesc" : "Phone.DropComputerOnlyDesc"),
                new UiRect(zone.X + 8f, zone.CenterY + 4f, zone.Width - 16f, 30f), theme.TextSecondary, 11f, 400,
                UiTextAlign.Center, UiVertAlign.Top, wrap: true);
            g.PopClip();
        }
        if (!_airPlayDrop) g.DrawLine(bounds.X + 2f, bounds.CenterY, bounds.Right - 2f, bounds.CenterY, theme.Accent, 1f);
        g.DrawRoundedRect(bounds, 8f, theme.Accent, 2f);
    }
#endif

    private readonly record struct RowLayout(float TotalH, IReadOnlyList<(string Id, string Label, UiRect Rect, bool IsExit)> OutPills,
        IReadOnlyList<(string Id, string Label, UiRect Rect)> InPills);

    private RowLayout MeasureExpandedRow(AppRowState row, float collapsedHeight, bool showInputs, float rowWidth)
    {
        float headerH = collapsedHeight - 2f;
        var outPills = new List<(string, string, UiRect, bool)>();
        var inPills = new List<(string, string, UiRect)>();
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
        return new RowLayout(y, outPills, inPills);
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

    private bool TryGetAppIndex(ReadOnlySpan<char> hitKey, out int index)
    {
        for (index = 0; index < _rows.Count; index++)
            if (hitKey.SequenceEqual(_rows[index].HitKey)) return true;
        index = -1;
        return false;
    }

    private void RenderAppRow(
        D2dTarget g,
        UiThemePalette theme,
        AppRowState row,
        UiRect rowBounds,
        bool legacyLayout,
        bool isCurrent,
        IReadOnlyList<(string Id, string Label, UiRect Rect, bool IsExit)> outPills,
        IReadOnlyList<(string Id, string Label, UiRect Rect)> inPills)
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
        string iconHitId = $"qp.appmute:{row.HitKey}";
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
        string expandId = $"qp.expand:{row.HitKey}";
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
        string sliderId = $"qp.appslider:{row.HitKey}";

        if (legacyLayout)
        {
            var sliderRect = new UiRect(midX, headerRect.CenterY - 16f, midW, 32f);
            UiPrimitives.DrawVolumeSlider(g, theme, _hit, sliderId, sliderRect,
                row.Volume / 100f, row.Peak, row.Muted, _config.ShowAppPeakMeter && !row.IsExternal,
                style: UiSliderStyle.PanelRow, enabled: row.Available);
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
                style: UiSliderStyle.PanelRow, enabled: row.Available);
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
                string exitId = $"qp.appexit:{row.HitKey}";
                _hit.Add(exitId, btnRect, UiCursorKind.Hand, L10n.T("Phone.StopReceivingApp"));
                bool exitHot = _hit.IsHover(exitId);
                g.FillRoundedRect(btnRect, 7f, theme.SurfaceBg);
                g.DrawRoundedRect(btnRect, 7f, exitHot ? theme.Fail : theme.Border, 1f);
                g.DrawText(label, btnRect.Deflate(6f, 0f), exitHot ? theme.Fail : theme.TextPrimary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
                continue;
            }

            string hitId = $"qp.appdev:{row.HitKey}:{devId}";
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
                string hitId = $"qp.appin:{row.HitKey}:{devId}";
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
        const float gap = 6f, mainWidth = 76f, micWidth = 120f, muteWidth = 112f;
        float left = rect.Right - mainWidth - micWidth - muteWidth - 3 * gap;
        DrawToolButton(g, theme, "qp.globalmute", new UiRect(left, rect.Y, muteWidth, 28), L10n.T("Qp.GlobalMute"), active: _globalPlaybackMuted);
        left += muteWidth + gap;
        DrawToolButton(g, theme, "qp.mic", new UiRect(left, rect.Y, micWidth, 28), L10n.T("Qp.MicMute"), enabled: _micKnown, active: _globalMicMuted);
        DrawToolButton(g, theme, "qp.settings", new UiRect(left + micWidth + gap, rect.Y, mainWidth, 28), L10n.T("Qp.FullUi"));
    }

    private void DrawToolButton(D2dTarget g, UiThemePalette theme, string id, UiRect rect, string label,
        bool enabled = true, bool active = false)
    {
        if (enabled) _hit.Add(id, rect, UiCursorKind.Hand, label);
        bool hovered = enabled && _hit.IsHover(id);
        if (hovered || _hit.IsActive(id))
            g.FillRoundedRect(rect, 8f, theme.NavHover);
        var ink = active ? theme.Accent : theme.TextSecondary;
        if (!enabled) ink = ink.WithAlphaFraction(0.5f);
        g.DrawRoundedRect(rect, 8f, theme.Border, 1);
        g.DrawText(label, rect.Deflate(3, 0), ink, 12, 400, UiTextAlign.Center, UiVertAlign.Center);
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
            _dragIcon?.Follow(cur.X, cur.Y);
            if (_androidDrag != null) UpdatePhoneDrop(cur.X, cur.Y);
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
                var sourceId = dragRow.App.ExternalSourceId;
                var android = sourceId != null && _host.HasPhone ? _host.Phone.Sources.FindSource(sourceId) : null;
                bool airPlay = sourceId != null && _host.HasAirPlay && _host.AirPlay.Source?.Id == sourceId;
                if (android != null || airPlay || (!dragRow.IsExternal && dragRow.Pid > 0 && _phoneCompanion is { IsVisible: true }))
                {
                    _draggingCrossDevice = true;
                    ViewNative.GetCursorPos(out var cur);
                    _androidDrag = android;
                    if (android != null) UpdatePhoneDrop(cur.X, cur.Y);
                    NativeAppIconService.IconBitmap? icon = dragRow.ExternalIconBgra is { } bgra
                        ? new NativeAppIconService.IconBitmap(bgra, dragRow.ExtIconW, dragRow.ExtIconH)
                        : !dragRow.IsExternal ? NativeAppIconService.GetAppIcon(_config, dragRow.Pid, dragRow.App.IconPath, dragRow.App.ProcessName) : null;
                    _dragIcon = new DragIconWindow(icon, dragRow.DisplayName, _theme.TextPrimary, cur.X, cur.Y,
                        airPlay ? UiIcon.AirPlay : android?.App.IsDeviceAudio == true ? UiIcon.Phone : null);
                    CrossDeviceDragHost.BeginDrag(dragRow.App, cur.X, cur.Y);
                    SetCursorKind(UiCursorKind.Hand);
                    Invalidate();
                    return;
                }
            }
        }
#endif

        if (leftDown && _scroll.DragMove(yDip - _drawnSlideY)) { Invalidate(); return; }

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
            int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, xDip, UiSliderStyle.PanelRow);
            if (row.Volume != val)
            {
                row.Volume = val;
                QueueAppVolume(row, val);
                Invalidate();
            }
            return;
        }

        if (_hit.UpdateHover(xDip, yDip - _drawnSlideY))
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
#if SONICROUTE_PHONE
            if (_draggingCrossDevice)
            {
                ReleasePointerCapture();
                return;
            }
#endif
            if (_adjustMode)
            {
                CancelAdjustMode();
                return;
            }
            var rHit = _hit.HitTest(e.X, e.Y - _drawnSlideY);
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

        var hit = _hit.HitTest(e.X, e.Y - _drawnSlideY);
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

        if (hit?.Id == "qp.scroll" && _scroll.BeginDrag(e.Y - _drawnSlideY))
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

        if (hit.Value.Id.StartsWith("qp.appslider:", StringComparison.Ordinal) && TryGetAppIndex(hit.Value.Id.AsSpan(13), out int rIdx))
        {
            if (rIdx >= 0 && rIdx < _rows.Count && _rows[rIdx].Available)
            {
                _draggingRowIdx = rIdx;
                _draggingSliderRect = hit.Value.Bounds;
                int val = UiPrimitives.SliderValueFromX(_draggingSliderRect, e.X, UiSliderStyle.PanelRow);
                _rows[rIdx].Volume = val;
                QueueAppVolume(_rows[rIdx], val);
                Invalidate();
            }
            return;
        }

#if SONICROUTE_PHONE
        if (hit.Value.Id.StartsWith("qp.appmute:", StringComparison.Ordinal) && TryGetAppIndex(hit.Value.Id.AsSpan(11), out int dragIdx))
        {
            if (dragIdx >= 0 && dragIdx < _rows.Count && (_rows[dragIdx].IsExternal || _phoneCompanion is { IsVisible: true }))
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
            var rHit = _hit.HitTest(e.X, e.Y - _drawnSlideY);
            if (rHit is { } rh && rh.Id == pressed)
            {
                if (rh.Id == "qp.masterslider")
                {
                    ShowMasterPresetsMenu(e.X, e.Y);
                }
                else if (rh.Id.StartsWith("qp.appslider:", StringComparison.Ordinal) && TryGetAppIndex(rh.Id.AsSpan(13), out int pIdx))
                {
                    if (pIdx >= 0 && pIdx < _rows.Count && _rows[pIdx].Available)
                        ShowAppPresetsMenu(_rows[pIdx], e.X, e.Y);
                }
            }
            return;
        }

        if (e.Button != UiMouseButton.Left) return;

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
            if (_resizingTopEdge && string.Equals(_config.QuickPanelPosMode, "custom", StringComparison.OrdinalIgnoreCase))
                _config.QuickPanelCustomY = CurrentCustomPosition().Y;
            if (!CommitPanelSettings())
            {
                SetBoundsPx(LeftPx, _resizeStartTopPx, WidthDip, _resizeStartHeightDip);
                RepositionCompanions();
                return;
            }
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
            _dragIcon?.Dispose();
            _dragIcon = null;
            var android = _androidDrag;
            _androidDrag = null;
            string? sourceId = CrossDeviceDragHost.DraggedApp?.ExternalSourceId;
            bool? shared = android == null ? null : UpdatePhoneDrop(cur.X, cur.Y);
            bool outside = !CrossDeviceDragHost.ContainsScreenPoint(this, cur.X, cur.Y) &&
                !CrossDeviceDragHost.ContainsScreenPoint(_phoneCompanion, cur.X, cur.Y);
            ClearPhoneDrop();
            if (android != null && !ReferenceEquals(_host.Phone.Sources.FindSource(android.Id), android))
                CrossDeviceDragHost.CancelDrag();
            else if (!CrossDeviceDragHost.EndDrag(cur.X, cur.Y))
            {
                if (android != null && (shared != null || outside))
                    _ = CrossDeviceDragHost.FinishAndroidDropAsync(_host, android.Device, android.App, shared, cur.X, cur.Y);
                else if (outside && sourceId != null && _host.HasAirPlay && _host.AirPlay.Source?.Id == sourceId)
                    _host.AirPlay.ShowMirror();
            }
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

        var hit = _hit.HitTest(e.X, e.Y - _drawnSlideY);
        if (hit is null || hit.Value.Id != active) { Invalidate(); return; }

        ActivateFocused(hit.Value, e);
    }

    protected override bool ActivateFocused(UiHitItem item, UiPointerEventArgs e)
    {
        switch (item.Id)
        {
            case "qp.title":
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
                OpenMasterDeviceCombo(item.Bounds.Offset(0f, _drawnSlideY));
                break;
            default:
                if (item.Id.StartsWith("qp.appmute:", StringComparison.Ordinal) && TryGetAppIndex(item.Id.AsSpan(11), out int mIdx))
                {
                    ToggleAppMute(mIdx);
                }
                else if (item.Id.StartsWith("qp.expand:", StringComparison.Ordinal) && TryGetAppIndex(item.Id.AsSpan(10), out int eIdx))
                {
                    ToggleRowExpand(eIdx);
                }
                else if (item.Id.StartsWith("qp.appexit:", StringComparison.Ordinal) && TryGetAppIndex(item.Id.AsSpan(11), out int xIdx))
                {
                    DisconnectExternalRow(xIdx);
                }
                else if (item.Id.StartsWith("qp.appdev:", StringComparison.Ordinal))
                {
                    var rest = item.Id.AsSpan(10);
                    int colon = rest.IndexOf(':');
                    if (colon > 0 && TryGetAppIndex(rest[..colon], out int aIdx))
                    {
                        string devId = rest[(colon + 1)..].ToString();
                        SelectAppOutput(aIdx, devId, e.Ctrl, e.Shift);
                    }
                }
                else if (item.Id.StartsWith("qp.appin:", StringComparison.Ordinal))
                {
                    var rest = item.Id.AsSpan(9);
                    int colon = rest.IndexOf(':');
                    if (colon > 0 && TryGetAppIndex(rest[..colon], out int iIdx))
                    {
                        string devId = rest[(colon + 1)..].ToString();
                        SelectAppInput(iIdx, devId);
                    }
                }
                break;
        }
        return true;
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        var hit = _hit.HitTest(xDip, yDip - _drawnSlideY);
        int step = Math.Clamp(_config.VolumeStep, 1, 20);
        if (hit is { } item && AdjustSlider(item.Id, delta > 0 ? step : -step)) return;
        _scroll.OnWheel(delta);
        Invalidate();
    }

    internal static int PanelSliderKeyDelta(int vk) => vk switch
    {
        0x25 or 0x28 => -1, 0x26 or 0x27 => 1, // arrows
        0x21 => 10, 0x22 => -10, // PageUp / PageDown
        0x24 => -100, 0x23 => 100, // Home / End
        _ => 0,
    };

    private bool AdjustSlider(string id, int delta)
    {
        if (id == "qp.masterslider")
        {
            _masterVolume = Math.Clamp(_masterVolume + delta, 0, 100);
            QueueMasterVolume(_masterVolume);
            SyncMasterToOutputPopup(_masterVolume, null);
        }
        else if (id.StartsWith("qp.appslider:", StringComparison.Ordinal) && TryGetAppIndex(id.AsSpan(13), out int index) && _rows[index].Available)
        {
            var row = _rows[index];
            row.Volume = Math.Clamp(row.Volume + delta, 0, 100);
            QueueAppVolume(row, row.Volume);
        }
        else return false;
        Invalidate();
        return true;
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        int delta = PanelSliderKeyDelta(vk);
        if (!ctrl && !alt && delta != 0 && _hit.TryGetFocus(out var item, out _) && AdjustSlider(item.Id, delta)) return true;
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
                ReleasePointerCapture();
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

    private string PinTitleTooltip() => L10n.CurrentLanguage switch
    {
        "zh-CN" => _pinned ? "取消固定，切换窗口时关闭面板" : "固定面板，切换窗口时保持打开",
        "zh-TW" => _pinned ? "取消固定，切換視窗時關閉面板" : "固定面板，切換視窗時保持開啟",
        "ja-JP" => _pinned ? "固定を解除して、ウィンドウを切り替えるとパネルを閉じる" : "パネルを固定して、ウィンドウを切り替えても開いたままにする",
        "ko-KR" => _pinned ? "고정 해제하고 다른 창으로 전환하면 패널 닫기" : "다른 창으로 전환해도 패널을 열어 두기",
        "de-DE" => _pinned ? "Anheften aufheben und das Panel beim Fensterwechsel schließen" : "Panel beim Wechsel zu einem anderen Fenster geöffnet lassen",
        "fr-FR" => _pinned ? "Désépingler et fermer le panneau lors du changement de fenêtre" : "Garder le panneau ouvert lors du changement de fenêtre",
        "es-ES" => _pinned ? "Desfijar el panel y cerrarlo al cambiar de ventana" : "Mantener el panel abierto al cambiar de ventana",
        "ru-RU" => _pinned ? "Открепить панель и закрывать её при смене окна" : "Оставлять панель открытой при смене окна",
        _ => _pinned ? "Unpin and close the panel when switching windows" : "Keep the panel open when switching windows",
    };

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

    private void QueueAppVolume(AppRowState row, int value) => _host.QueueAppVolume(row.App, value);

    internal void ConfirmAppVolume(string identity, int actual)
    {
        if (IsDisposed || !IsVisible) return;
        var row = _rows.Find(item => item.Identity == identity);
        if (row == null || row.Volume == actual) return;
        row.Volume = actual;
        Invalidate();
    }

    private void QueueMasterVolume(int level)
    {
        string? target = _config.PanelChangeSystemDefault ? _defaultOutputId : (_panelOutputId ?? _defaultOutputId);
        _host.QueueEndpointVolume(target, level);
    }

    internal void ConfirmEndpointVolume(string endpoint, int volume)
    {
        if (IsDisposed) return;
        OnOutputPopupEndpointChanged(endpoint, volume, null);
        _outputVolumesPopup?.SyncEndpointState(endpoint, volume);
    }

    private void ToggleMasterMute()
    {
        _masterMuted = !_masterMuted;
        SyncMasterToOutputPopup(null, _masterMuted);
        Invalidate();

        string? target = _config.PanelChangeSystemDefault ? _defaultOutputId : (_panelOutputId ?? _defaultOutputId);
        string title = ResolveDeviceDisplayName(_outputs, target);
        _host.SubmitPanelAudio(() =>
        {
            try
            {
                var st = _host.Audio.Volume(Flow.Render, target, 0, toggle: true);
                _host.PostContinuation(() =>
                {
                    if (IsDisposed) return;
                    OnOutputPopupEndpointChanged(target, st.Percent, st.Muted);
                    _outputVolumesPopup?.SyncEndpointState(target, st.Percent, st.Muted);
                    _host.PublishOsd(title, L10n.T(st.Muted ? "Qp.Muted" : "Qp.Unmuted"),
                        volume: st.Percent, muted: st.Muted, success: true);
                    if (IsVisible) Invalidate();
                });
            }
            catch
            {
                _host.PostContinuation(_host.ReportAudioWriteFailure);
            }
        });
    }

    private void ToggleGlobalOutputMute()
    {
        _globalPlaybackMuted = !_globalPlaybackMuted;
        _masterMuted = _globalPlaybackMuted;
        _outputVolumesPopup?.SyncEndpointState(null, null, _globalPlaybackMuted);
        Invalidate();

        _host.SubmitPanelAudio(() =>
        {
            try
            {
                bool muted = _host.Audio.GlobalPlaybackMute(null);
                _host.PostContinuation(() =>
                {
                    if (IsDisposed) return;
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
                _host.PostContinuation(() =>
                {
                    if (IsDisposed) return;
                    _host.PublishOsd(L10n.T("Qp.AllApps"), L10n.T("Tray.AudioUnavailable"), success: false);
                    RefreshData();
                });
            }
        });
    }

    private void ToggleGlobalMicMute()
    {
        _host.SubmitPanelAudio(() =>
        {
            try
            {
                bool muted = _host.Audio.GlobalMicMute(null);
                _host.PostContinuation(() =>
                {
                    if (IsDisposed) return;
                    _globalMicMuted = muted;
                    _micKnown = true;
                    _host.PublishOsd(L10n.T("Ov.MuteMic"), L10n.T(muted ? "Qp.MicMuted" : "Qp.MicUnmuted"),
                        muted: muted, success: true);
                    if (IsVisible) Invalidate();
                });
            }
            catch
            {
                _host.PostContinuation(_host.ReportAudioWriteFailure);
            }
        });
    }

    private void ToggleAppMute(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var row = _rows[rowIdx];
        if (!row.Available) return;

        row.Muted = !row.Muted;
        UpdatePeakTargets();
        Invalidate();
        var app = row.App;
        _host.SubmitPanelAudio(() =>
        {
            try
            {
                var st = _host.Audio.Volume(app, toggle: true);
                _host.PostContinuation(() =>
                {
                    if (IsDisposed) return;
                    var current = _rows.Find(r => r.Identity == row.Identity);
                    if (current == null) return;
                    current.Muted = st.Muted;
                    if (st.Percent >= 0) current.Volume = st.Percent;
                    UpdatePeakTargets();
                    if (IsVisible) Invalidate();
                });
            }
            catch { _host.PostContinuation(_host.ReportAudioWriteFailure); }
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
        long generation = _routeRevision;
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
                    if (IsDisposed || generation != _routeRevision ||
                        _rows.Find(r => r.Identity == target.Identity && r.Expanded) is not { } current) return;
                    if (!_pendingRoutes.ContainsKey((current.Identity, Flow.Render)))
                    {
                        current.NativeOutputId = route;
                        current.SelectedOutputs = selected;
                    }
                    if (!_pendingRoutes.ContainsKey((current.Identity, Flow.Capture))) current.NativeInputId = inputRoute;
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

        Task<ControlReply> write;
#if SONICROUTE_PHONE
        if (row.IsExternal)
        {
            var previous = _externalOutputWrites;
            write = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { /* A failed selection does not cancel the next. */ }
                return await _host.Audio.SetExternalOutputsAsync(row.App, nextOutputs).ConfigureAwait(false);
            });
            _externalOutputWrites = write;
        }
        else
#endif
        {
            write = !ctrl && !shift && !PcRoutingApi.IsPhoneOutputDevice(devId)
                ? _host.CurrentApp.SelectOutputAsync(row.Pid, devId)
                : _host.CurrentApp.SetOutputsAsync(row.Pid, preferred, nextOutputs);
        }
        ObserveRoute(row.Identity, Flow.Render, write);
    }

    private void SelectAppInput(int rowIdx, string devId)
    {
        if ((uint)rowIdx >= (uint)_rows.Count) return;
        var row = _rows[rowIdx];
        if (row.IsExternal) return;
        row.NativeInputId = devId;
        Invalidate();

        Task previous = _inputWrites;
        var write = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); } catch { }
            bool applied = _host.Audio.SetRoute(row.Pid, Flow.Capture, AudioService.IsSystemDefault(devId) ? null : devId);
            return new ControlReply(applied ? "ok" : "error: route");
        });
        _inputWrites = write;
        ObserveRoute(row.Identity, Flow.Capture, write);
    }

    private async void ObserveRoute(string identity, Flow flow, Task<ControlReply> write)
    {
        var key = (identity, flow);
        long version = ++_routeRevision;
        _pendingRoutes[key] = version;
        bool applied;
        try { applied = !(await write.ConfigureAwait(false)).Text.StartsWith("error", StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) { ExceptionDiagnostics.Record("Panel.Route", ex); applied = false; }
        _host.PostContinuation(() =>
        {
            if (IsDisposed || _pendingRoutes.GetValueOrDefault(key) != version) return;
            _pendingRoutes.Remove(key);
            ++_routeRevision;
            if (!applied) _host.ReportAudioWriteFailure();
            else if (IsVisible) RefreshData();
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
            catch (Exception ex) { ExceptionDiagnostics.Record("Panel.Disconnect", ex); _host.TryPost(_host.ReportAudioWriteFailure); }
            _host.TryPost(() => { if (IsVisible && !IsDisposed) RefreshData(); });
        });
#endif
    }

    private void OpenMasterDeviceCombo(UiRect anchor)
    {
        var visibleOutputs = _outputs.Where(d => !_config.HiddenOutputDevices.Contains(d.Id)).ToArray();
        var choices = visibleOutputs
            .Select(d => new ComboChoice(d.Id, _config.DeviceNames.GetValueOrDefault(d.Id, d.Name)
                + (IdCmp.Equals(d.Id, _defaultOutputId) ? L10n.T("Ov.Default") : ""), UiIcon.Headphones))
            .ToList();
        if (choices.Count == 0) return;

        string selected = _config.PanelChangeSystemDefault ? (_defaultOutputId ?? choices[0].Key) : (_panelOutputId ?? _defaultOutputId ?? choices[0].Key);
        CloseCombo();
        _comboPopup = ComboPopupWindow.ShowFor(_theme, this, anchor, choices, selected, picked =>
        {
            _comboPopup = null;
            if (_config.PanelChangeSystemDefault)
            {
                _host.SubmitPanelAudio(() =>
                {
                    try
                    {
                        _host.Audio.SetDefault(Flow.Render, picked.Key);
                        _host.TryPost(() =>
                        {
                            if (IsDisposed) return;
                            RefreshData();
                            _outputVolumesPopup?.RefreshData();
                        });
                    }
                    catch (Exception ex) { ExceptionDiagnostics.Record("Panel.Default", ex); _host.TryPost(_host.ReportAudioWriteFailure); }
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
        if (!CommitPanelSettings()) return;
        SyncCompanionsVisibility();
        Invalidate();
    }

#if SONICROUTE_PHONE
    private void TogglePhoneCompanion()
    {
        _config.QuickPanelShowPhoneAudio = !_config.QuickPanelShowPhoneAudio;
        if (!CommitPanelSettings()) return;
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
            float ps = PrimaryCoordinateScale();
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

        GetMonitorWorkAreaForPoint(0, 0, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
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

    private static float PrimaryCoordinateScale()
    {
        GetMonitorWorkAreaForPoint(0, 0, out _, out _, out _, out _, out uint primaryDpi);
        return primaryDpi / 96f;
    }

    private (int X, int Y) CurrentCustomPosition()
    {
        float scale = PrimaryCoordinateScale();
        return (Math.Max(0, (int)MathF.Round(LeftPx / scale)), Math.Max(0, (int)MathF.Round(TopPx / scale)));
    }

    private bool CommitPanelSettings()
    {
        if (ConfigService.TryCommit(_config)) return true;
        _host.PublishOsd(BuildIdentity.DisplayName, L10n.T("Tray.SaveFailed"), success: false);
        Invalidate();
        return false;
    }

    private void SaveAdjustPosition()
    {
        _config.QuickPanelPosMode = "custom";
        (_config.QuickPanelCustomX, _config.QuickPanelCustomY) = CurrentCustomPosition();
        if (!CommitPanelSettings()) { CancelAdjustMode(); return; }
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
            _dragIcon?.Dispose();
            _dragIcon = null;
            _androidDrag = null;
            ClearPhoneDrop();
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
        HighContrastScrollInk = src.HighContrastScrollInk?.WithAlphaFraction(factor),
        HighContrastScrollHighlight = src.HighContrastScrollHighlight.WithAlphaFraction(factor),
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
