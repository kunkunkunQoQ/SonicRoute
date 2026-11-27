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
/// The entrance animation stops on completion. The peak meter runs only while visible.
/// </summary>
internal sealed class QuickPanelWindow : LayeredWindow
{
    private static readonly int[] PresetPercentages = [25, 50, 75, 100];
    private static readonly StringComparer IdCmp = StringComparer.OrdinalIgnoreCase;
    private const int EntranceDurationMs = 210;
    private const float ExpandedDeviceInset = 10f;

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
        public bool CanMeasurePeak => Available && !Muted;
        public float Peak;
        public float PaintedPeak;
        public bool PeakVisible;
#if SONICROUTE_PHONE
        public bool ExternalPeakTarget;
#endif
        public bool Expanded;
        public string NativeOutputId = AudioService.SystemDefaultDeviceId;
        public Endpoint[] OutputDevices = [];
        public HashSet<string> SelectedOutputs = new(IdCmp);
        public string? OutputAnchor;
        public string NativeInputId = AudioService.SystemDefaultInputDeviceId;
        public string? CachedOutputLabel;
        public byte[]? ExternalIconBgra;
        public int ExtIconW, ExtIconH, ExtIconVer;
    }

    private static readonly string VersionLabel = "v" + BuildIdentity.ProductVersion;
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
    protected override bool ShowOwnedWindowTooltips => false;
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
    private long _defaultDeviceRequest;
    private string? _deviceError;

    private bool _pinned;
    private bool _loading;
    private bool _refreshQueued;
    private bool _eventsSubscribed;
    private PanelPeakMeter? _peakMeter;
    private readonly HashSet<int> _peakTargets = [];
    private bool _localPeakDecay;
#if SONICROUTE_PHONE
    private bool _externalPeakSubscribed;
    private bool _externalPeakSampling;
    private int _externalPeakWakePending;
#endif
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
    private float _entranceProgress = 1f;

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
            ApplyConfiguredBounds();
            if (!_config.ShowAppPeakMeter)
            {
                StopPeakSampling();
                UpdateAnimationTimer();
            }
            RefreshData();
            Invalidate();
        }
    }

    internal void ShowQuickPanel()
    {
        _theme = UiThemePalette.Resolve(_config);
        EnsureCreated(L10n.T("Qp.Panel"));
        ApplyConfiguredBounds();

        bool entering = !IsVisible && ClientAreaAnimationsEnabled();
        _animatingEntrance = entering;
        _entranceProgress = entering ? 0f : 1f;

        Show(activate: true);
        RefreshData();
        SyncCompanionsVisibility();
        // Start the shared clock after the visible windows have presented their first frame.
        if (entering) _entranceStartTick = Environment.TickCount64;
    }

    internal bool IsAdjusting => _adjustMode;

    internal void BeginAdjust()
    {
        ShowQuickPanel();
        _adjustMode = true;
        _host.NotifyAdjustmentChanged();
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
                allRenderWithPhone = _host.Audio.RenderDevicesWithPhone(windows: render, includePhoneCurrent: true);
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

                var apps = _host.Audio.Apps(refresh: false, includeOfflineExternal: true);
                foreach (var app in apps)
                {
                    if (!app.IsExternal && app.ProcessName is { } proc && _config.HiddenPanelApps.Contains(proc, IdCmp))
                        continue;
#if SONICROUTE_PHONE
                    if (app.IsExternal && _host.Audio.ExternalAudio?.IsQuickPanelSourceVisible(app, _config) != true)
                        continue;
#endif

                    int pid = checked((int)app.ProcessId);
                    string label = !string.IsNullOrWhiteSpace(app.ProcessName)
                        && _config.AppNames.TryGetValue(app.ProcessName!, out string? custom)
                        && !string.IsNullOrWhiteSpace(custom)
                        ? custom
                        : app.Label;

                    int vol = 0;
                    bool muted = false;
                    bool avail = !app.IsExternalOffline;
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
                    string nativeIn = AudioService.SystemDefaultInputDeviceId;

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
                                ? AudioService.SystemDefaultInputDeviceId
                                : AudioPolicyConfig.UnpackDeviceId(persistedIn);
                        }
                        catch { }
                    }

                    var outputDevices = app.IsExternal
                        ? _host.Audio.RenderDevicesWithPhone(app, render)
                        : allRenderWithPhone;
                    string? outLabel = needOutputName ? ResolveDeviceDisplayName(outputDevices, nativeOut) : null;

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
                        OutputDevices = outputDevices,
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
                    row.Peak = row.CanMeasurePeak ? _smoothedPeaks.GetValueOrDefault(row.Identity) : 0f;
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

    private void UpdatePeakTargets()
    {
        if (!_config.ShowAppPeakMeter)
        {
            if (_peakMeter != null) StopPeakSampling();
            UpdateAnimationTimer();
            return;
        }

        _peakTargets.Clear();
        _localPeakDecay = false;
#if SONICROUTE_PHONE
        bool hasExternal = false;
#endif
        foreach (var row in _rows)
        {
            if (!row.PeakVisible || !row.CanMeasurePeak) row.Peak = 0f;
#if SONICROUTE_PHONE
            if (row.IsExternal)
            {
                bool eligible = row.PeakVisible && row.CanMeasurePeak;
                _externalPeakSampling |= eligible && !row.ExternalPeakTarget;
                row.ExternalPeakTarget = eligible;
                hasExternal |= eligible;
                continue;
            }
#endif
            if (!row.PeakVisible) continue;
            if (!row.IsExternal && row.Available && row.Pid > 0)
            {
                // Keep muted sessions subscribed so an external unmute can restart sampling.
                _peakTargets.Add(row.Pid);
                _localPeakDecay |= row.Peak > 0f;
            }
        }

        (_peakMeter ??= new PanelPeakMeter(OnLocalPeakChanged)).SetTargets(_peakTargets);
#if SONICROUTE_PHONE
        SubscribeExternalPeak(hasExternal);
#endif
        UpdateAnimationTimer();
    }

    private void OnLocalPeakChanged(PanelPeakMeter meter, bool refreshCatalog)
    {
        _host.TryPost(() =>
        {
            if (!ReferenceEquals(_peakMeter, meter) || !IsVisible || IsDisposed || !_config.ShowAppPeakMeter) return;
            if (refreshCatalog) RefreshData();
            UpdateAnimationTimer();
        });
    }

    private void StopPeakSampling()
    {
        _peakMeter?.Dispose();
        _peakMeter = null;
        _localPeakDecay = false;
        foreach (var row in _rows)
        {
            row.Peak = 0f;
            row.PaintedPeak = 0f;
            row.PeakVisible = false;
#if SONICROUTE_PHONE
            row.ExternalPeakTarget = false;
#endif
        }
#if SONICROUTE_PHONE
        SubscribeExternalPeak(false);
#endif
    }

#if SONICROUTE_PHONE
    private void SubscribeExternalPeak(bool enable)
    {
        if (enable == _externalPeakSubscribed) return;
        _externalPeakSubscribed = enable;
        _externalPeakSampling = enable;
        if (enable) ExternalAudioBridge.PeakActivity += OnExternalPeakActivity;
        else ExternalAudioBridge.PeakActivity -= OnExternalPeakActivity;
    }

    private void OnExternalPeakActivity()
    {
        if (Interlocked.Exchange(ref _externalPeakWakePending, 1) != 0) return;
        if (!_host.TryPost(() =>
        {
            Interlocked.Exchange(ref _externalPeakWakePending, 0);
            if (!_externalPeakSubscribed || !IsVisible || IsDisposed || !_config.ShowAppPeakMeter) return;
            _externalPeakSampling = true;
            UpdateAnimationTimer();
        }))
            Interlocked.Exchange(ref _externalPeakWakePending, 0);
    }

    private bool ReadExternalPeaks()
    {
        if (!_externalPeakSampling || _host.Audio.ExternalAudio is not { } bridge) return false;
        bool keepSampling = false;
        bool changed = false;
        foreach (var row in _rows)
        {
            if (!row.IsExternal || !row.PeakVisible || !row.CanMeasurePeak) continue;
            var (active, peak) = bridge.ReadPeak(row.App.ExternalSourceId);
            changed |= UpdateRowPeak(row, peak);
            keepSampling |= active || row.Peak > 0f;
        }
        _externalPeakSampling = keepSampling;
        return changed;
    }
#endif

    private void UpdateAnimationTimer()
    {
        int interval = 0;
        if (IsVisible && !IsDisposed)
        {
            if (_animatingEntrance) interval = 16;
            else if (_config.ShowAppPeakMeter && (_peakMeter?.IsSampling == true || _localPeakDecay
#if SONICROUTE_PHONE
                || _externalPeakSampling
#endif
                )) interval = 50;
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
    }

    protected override void OnHidden()
    {
        _deviceError = null;
        StopAnimationTimer();
        _timerInterval = 0;
        _animatingEntrance = false;
        StopPeakSampling();
        if (_adjustMode)
        {
            _adjustMode = false;
            _host.NotifyAdjustmentChanged();
        }
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
        StopPeakSampling();
        if (_adjustMode)
        {
            _adjustMode = false;
            _host.NotifyAdjustmentChanged();
        }
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
            _entranceProgress = 1f - inv * inv * inv; // CubicEase-Out
            if (t >= 1f)
            {
                _animatingEntrance = false;
                UpdateAnimationTimer();
            }
            UpdateCompanionEntranceOpacity();
            Invalidate();
            return;
        }

        if (!_config.ShowAppPeakMeter) return;
        bool changed = ReadLocalPeaks();
#if SONICROUTE_PHONE
        changed |= ReadExternalPeaks();
#endif
        if (changed) Invalidate();
        UpdateAnimationTimer();
    }

    private bool ReadLocalPeaks()
    {
        if (_peakMeter is not { } meter || (!meter.IsSampling && !_localPeakDecay)) return false;
        bool changed = false;
        _localPeakDecay = false;
        foreach (var row in _rows)
        {
            if (row.IsExternal || !row.PeakVisible || !row.CanMeasurePeak) continue;
            changed |= UpdateRowPeak(row, meter.Read(row.Pid));
            _localPeakDecay |= row.Peak > 0f;
        }
        meter.Sample();
        return changed;
    }

    private static bool UpdateRowPeak(AppRowState row, float peak)
    {
        UiPrimitives.UpdateVolumePeak(ref row.Peak, peak);
        return UiPrimitives.HasVolumePeakChanged(row.PaintedPeak, row.Peak);
    }

    protected override string GetWindowTitle() => L10n.T("Qp.Panel");

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        foreach (var row in _rows) row.PeakVisible = false;
        _drawnSlideY = _animatingEntrance ? (1f - _entranceProgress) * 10f : 0f;
        UiThemePalette theme = _animatingEntrance ? ScaleThemeOpacity(_theme, 0.05f + 0.95f * _entranceProgress) : _theme;
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
            UpdatePeakTargets();
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
        _hit.Add("qp.title", titleRect, UiCursorKind.Hand);
        if (_hit.IsHover("qp.title") || _hit.IsActive("qp.title"))
            g.FillRoundedRect(titleRect, 4f, theme.NavHover);
        g.DrawText(BuildIdentity.DisplayName, titleRect, _pinned ? theme.Accent : theme.TextPrimary,
            14f, 700, UiTextAlign.Leading, UiVertAlign.Top);

#if SONICROUTE_PHONE
        const float phoneBtnW = 30f;
        var phoneRect = new UiRect(rx - phoneBtnW, y - 1f, phoneBtnW, 28f);
        bool phoneOpen = _phoneCompanion is { IsVisible: true };
        UiPrimitives.DrawSecondaryButton(g, theme, _hit, "qp.phone", phoneRect, "",
            radius: 8f, icon: UiIcon.Devices, active: phoneOpen);
        rx = phoneRect.X - 6f;
#endif

        var statusRect = new UiRect(titleRect.Right + 4f, y, MathF.Max(0f, rx - titleRect.Right - 4f), 28f);
        g.DrawText(_deviceError ?? VersionLabel, statusRect, _deviceError == null ? theme.TextSecondary : theme.TextPrimary,
            10.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);

        // 2. Output Device Selector + Expand Output Volumes Toggle
        y = headerRect.Bottom + 10f;
        string activeId = _config.PanelChangeSystemDefault ? (_defaultOutputId ?? "") : (_panelOutputId ?? _defaultOutputId ?? "");
        string devName = ResolveDeviceDisplayName(_outputs, activeId);
        const float expBtnSize = 32f;
        var outputsToggleRect = new UiRect(padX + innerW - expBtnSize, y, expBtnSize, expBtnSize);
        bool outPopOpen = _outputVolumesPopup is { IsVisible: true };
        _hit.Add("qp.outputs", outputsToggleRect, UiCursorKind.Hand);
        g.FillRoundedRect(outputsToggleRect, 8f, outPopOpen ? theme.NavSelected : _hit.IsHover("qp.outputs") ? theme.NavHover : theme.SurfaceBg);
        g.DrawRoundedRect(outputsToggleRect, 8f, outPopOpen || _hit.IsHover("qp.outputs") ? theme.Accent : theme.Border, 1f);
        g.DrawIcon(UiIcon.ChevronUp, new UiRect(outputsToggleRect.CenterX - 5f, outputsToggleRect.CenterY - 5f, 10f, 10f),
            outPopOpen ? theme.Accent : theme.TextSecondary, 1.8f, outPopOpen ? 180f : 0f);

        var masterComboRect = new UiRect(padX, y, MathF.Max(60f, outputsToggleRect.X - 6f - padX), 32f);
        _hit.Add("qp.masterdev", masterComboRect, UiCursorKind.Hand);
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
        // Device pills can use the space up to the scroll track independently of the slider row's padding.
        const float deviceTrackGap = 2f;
        float deviceWidth = MathF.Max(60f, listRect.Width - UiMetrics.ScrollBarWidth - deviceTrackGap - ExpandedDeviceInset);
        RowLayout expanded = expandedRow == null ? collapsed : MeasureExpandedRow(expandedRow, collapsedHeight, showInputsInPanel, deviceWidth);
        float totalContentH = collapsedHeight * _rows.Count + expanded.TotalH - collapsedHeight;

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
                    RenderAppRow(g, theme, row, new UiRect(listRect.X, curY, rowW, layout.TotalH),
                        legacyLayout, isCurrent, deviceWidth, layout.OutPills, layout.InPills);
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

    private RowLayout MeasureExpandedRow(AppRowState row, float collapsedHeight, bool showInputs, float deviceWidth)
    {
        float headerH = collapsedHeight - 2f;
        var outPills = new List<(string, string, UiRect, bool)>();
        var inPills = new List<(string, string, UiRect)>();
        float devX = ExpandedDeviceInset;
        float y = headerH + 4f;

        // Output group label + wrapped pills
        y += 20f;
        var outChoices = BuildOutputChoicesForRow(row);
        float outH = LayoutWrappedPills(outChoices, hasTrailingExit: row.IsExternal, devX, y, deviceWidth, outPills);
        y += outH;

        // Input group when ExperimentalMic && MicInPanel && !IsExternal
        if (showInputs && !row.IsExternal)
        {
            y += 8f;
            y += 20f;
            var inChoices = BuildInputChoices();
            float inH = LayoutWrappedPills(inChoices, devX, y, deviceWidth, inPills);
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
        var choices = new List<(string Id, string Name)>(row.OutputDevices.Length + 1);
        const string defaultId = AudioService.SystemDefaultDeviceId;
        if (_config.IsDeviceShown(EDataFlow.eRender, defaultId))
            choices.Add((defaultId, Label(defaultId, L10n.T("Dev.SystemDefaultOut"))));

        foreach (var d in row.OutputDevices)
        {
            if (!_config.IsDeviceShown(EDataFlow.eRender, d.PreferenceId)) continue;
            choices.Add((d.Id, Label(d.Id, d.Name)));
        }
        return choices;

        string Label(string id, string fallback) =>
            _config.DeviceNames.TryGetValue(id, out var alias) && !string.IsNullOrWhiteSpace(alias) ? alias : fallback;
    }

    private List<(string Id, string Name)> BuildInputChoices()
    {
        var choices = new List<(string Id, string Name)>(_inputs.Length + 1);
        const string defaultId = AudioService.SystemDefaultInputDeviceId;
        if (_config.IsDeviceShown(EDataFlow.eCapture, defaultId))
            choices.Add((defaultId, L10n.T("Dev.SystemDefaultIn")));
        foreach (var d in _inputs)
        {
            if (!_config.IsDeviceShown(EDataFlow.eCapture, d.PreferenceId)) continue;
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

    private void DrawAppSlider(D2dTarget g, UiThemePalette theme, AppRowState row, string id, UiRect rect)
    {
        row.PeakVisible = !_hit.VisiblePart(rect).IsEmpty;
        row.PaintedPeak = row.Peak;
        UiPrimitives.DrawVolumeSlider(g, theme, _hit, id, rect, row.Volume / 100f, row.Peak, row.Muted,
            _config.ShowAppPeakMeter, enabled: row.Available, compact: true, dimUnavailable: row.App.IsExternalOffline);
    }

    private void RenderAppRow(
        D2dTarget g,
        UiThemePalette theme,
        AppRowState row,
        UiRect rowBounds,
        bool legacyLayout,
        bool isCurrent,
        float deviceWidth,
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
        _hit.Add(iconHitId, iconBtnRect, row.Available ? UiCursorKind.Hand : UiCursorKind.Arrow, row.DisplayName);
        if (row.Available && _hit.IsHover(iconHitId))
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
#if SONICROUTE_PHONE
        else if (row.App.ProcessName == "airplay:receiver")
            g.DrawIcon(UiIcon.AirPlay, iconBox, theme.TextSecondary, 1.6f);
        else if (row.App.ProcessName == "android:sonicroute:device-audio")
            g.DrawIcon(UiIcon.Phone, iconBox, theme.TextSecondary, 1.6f);
#endif
        else
        {
            string ch = string.IsNullOrEmpty(row.DisplayName) ? "♪" : row.DisplayName[..1].ToUpperInvariant();
            g.DrawText(ch, iconBox, theme.TextPrimary, 12.5f, 600, UiTextAlign.Center, UiVertAlign.Center);
        }

        if (row.Available && row.Muted)
        {
            g.FillEllipse(iconBtnRect.Right - 5f, iconBtnRect.Y + 6f, 4f, 4f, theme.AccentInverse);
        }

        // Expand button on right
        bool showCompactOutName = legacyLayout && _config.QuickPanelCompactShowOutputName;
        float expandW = showCompactOutName ? 80f : 32f;
        float expandH = legacyLayout ? (showCompactOutName ? 24f : 20f) : 32f;
        var expandRect = new UiRect(headerRect.Right - expandW - 2f, headerRect.CenterY - expandH * 0.5f, expandW, expandH);
        string expandId = $"qp.expand:{row.HitKey}";
        _hit.Add(expandId, expandRect, UiCursorKind.Hand);
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
            DrawAppSlider(g, theme, row, sliderId, sliderRect);
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
            DrawAppSlider(g, theme, row, sliderId, sliderRect);
        }

        // Expanded device wrap-grid
        if (!row.Expanded) return;

        float devX = rowBounds.X + ExpandedDeviceInset;
        float outLabelY = headerRect.Bottom + 4f;
        g.DrawText(L10n.T("Qp.Output"), new UiRect(devX, outLabelY, deviceWidth, 16f), theme.TextSecondary, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);

        foreach (var (devId, label, relRect, isExit) in outPills)
        {
            var btnRect = relRect.Offset(rowBounds.X, rowBounds.Y);
            if (isExit)
            {
                string exitId = $"qp.appexit:{row.HitKey}";
                _hit.Add(exitId, btnRect, UiCursorKind.Hand);
                bool exitHot = _hit.IsHover(exitId);
                g.FillRoundedRect(btnRect, 7f, theme.SurfaceBg);
                g.DrawRoundedRect(btnRect, 7f, exitHot ? theme.Fail : theme.Border, 1f);
                g.DrawText(label, btnRect.Deflate(6f, 0f), exitHot ? theme.Fail : theme.TextPrimary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
                continue;
            }

            string hitId = $"qp.appdev:{row.HitKey}:{devId}";
            if (row.Available) _hit.Add(hitId, btnRect, UiCursorKind.Hand);

            bool isNative = IdCmp.Equals(row.NativeOutputId, devId);
            bool isMirror = !isNative && row.SelectedOutputs.Contains(devId);
            bool hot = row.Available && _hit.IsHover(hitId);

            UiColor bg = isNative ? theme.DeviceNativeBg : isMirror ? theme.Overlay : theme.SurfaceBg;
            UiColor fg = isNative ? theme.DeviceNativeFg : theme.TextPrimary;
            UiColor brd = isNative ? theme.Accent : hot ? theme.Accent : isMirror ? theme.DeviceMirrorBorder : theme.Border;
            if (!row.Available)
            {
                bg = theme.SurfaceBg;
                fg = theme.TextSecondary;
                brd = theme.Border;
            }

            g.FillRoundedRect(btnRect, 7f, bg);
            g.DrawRoundedRect(btnRect, 7f, brd, 1f);
            g.DrawText(label, btnRect.Deflate(6f, 0f), fg, 11.5f, isNative ? 600 : 400, UiTextAlign.Center, UiVertAlign.Center);
        }

        if (inPills.Count > 0)
        {
            float inLabelY = inPills[0].Rect.Y - 20f + rowBounds.Y;
            g.DrawText(L10n.T("Qp.Input"), new UiRect(devX, inLabelY, deviceWidth, 16f), theme.TextSecondary, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);

            foreach (var (devId, label, relRect) in inPills)
            {
                var btnRect = relRect.Offset(rowBounds.X, rowBounds.Y);
                string hitId = $"qp.appin:{row.HitKey}:{devId}";
                _hit.Add(hitId, btnRect, UiCursorKind.Hand);

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
        if (enabled) _hit.Add(id, rect, UiCursorKind.Hand);
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
                int fixedBottom = _resizeStartTopPx + (int)MathF.Round(_resizeStartHeightDip * DpiScale);
                newH = MathF.Min(newH, MathF.Max(UiMetrics.QuickPanelMinHeight, (fixedBottom - mt) / DpiScale));
                newTopPx = fixedBottom - (int)MathF.Round(newH * DpiScale);
            }

            if (newTopPx != TopPx || (int)MathF.Round(newH * DpiScale) != HeightPx)
                SetGroupBounds(LeftPx, newTopPx, newH);
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
            int val = UiPrimitives.VolumeSliderValueFromX(_draggingSliderRect, xDip);
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
            int val = UiPrimitives.VolumeSliderValueFromX(_draggingSliderRect, xDip, compact: true);
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
            int val = UiPrimitives.VolumeSliderValueFromX(_draggingSliderRect, e.X);
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
                int val = UiPrimitives.VolumeSliderValueFromX(_draggingSliderRect, e.X, compact: true);
                _rows[rIdx].Volume = val;
                QueueAppVolume(_rows[rIdx], val);
                Invalidate();
            }
            return;
        }

#if SONICROUTE_PHONE
        if (hit.Value.Id.StartsWith("qp.appmute:", StringComparison.Ordinal) && TryGetAppIndex(hit.Value.Id.AsSpan(11), out int dragIdx))
        {
            if (dragIdx >= 0 && dragIdx < _rows.Count && _rows[dragIdx].Available && (_rows[dragIdx].IsExternal || _phoneCompanion is { IsVisible: true }))
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
            if (HeightPx == (int)MathF.Round(_resizeStartHeightDip * DpiScale) && TopPx == _resizeStartTopPx) return;
            _config.QuickPanelHeight = (int)Math.Clamp(MathF.Round(HeightDip), 350f, 850f);
            if (_resizingTopEdge && string.Equals(_config.QuickPanelPosMode, "custom", StringComparison.OrdinalIgnoreCase))
                _config.QuickPanelCustomY = CurrentCustomPosition().Y;
            if (!CommitPanelSettings())
            {
                SetGroupBounds(LeftPx, _resizeStartTopPx, _resizeStartHeightDip);
                return;
            }
            _host.PanelGeometryChanged();
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

                string inputRoute = AudioService.SystemDefaultInputDeviceId;
                if (includeInputs)
                {
                    string? pin = _host.Audio.ReadRoute(checked((int)app.ProcessId), Flow.Capture);
                    inputRoute = string.IsNullOrWhiteSpace(pin) || AudioService.IsSystemDefault(pin)
                        ? AudioService.SystemDefaultInputDeviceId
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
        if (!row.Available) return;

        var choices = BuildOutputChoicesForRow(row);
        string[] order = choices.Select(c => c.Id).ToArray();
        bool isMultiExternal = PlaybackSelection.IsMultiOutputExternal(_host.Audio, row.App);
        if (row.IsExternal && !isMultiExternal)
        {
            ctrl = false;
            shift = false;
        }
        if (!row.IsExternal && ctrl && shift && PcRoutingApi.IsPhoneOutputDevice(devId))
            return;

        string[] nextOutputs = PlaybackSelection.SelectRange(order, row.SelectedOutputs, devId, row.OutputAnchor, ctrl, shift);
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

        string preferred = PlaybackSelection.SelectPrimary(
            nextOutputs,
            explicitPrimary ?? row.NativeOutputId,
            preferPhysical: !row.IsExternal,
            lowPriorityId: PlaybackSelection.LowPriorityOutputId(_host.Audio, row.App));

        row.SelectedOutputs = new HashSet<string>(nextOutputs, IdCmp);
        row.NativeOutputId = preferred;
        row.CachedOutputLabel = ResolveDeviceDisplayName(row.OutputDevices, preferred);
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
            write = !ctrl && !shift
                ? _host.CurrentApp.SelectOutputAsync(row.Pid, devId, FeedbackKind.PanelWarning)
                : _host.CurrentApp.SetOutputsAsync(row.Pid, preferred, nextOutputs, FeedbackKind.PanelWarning);
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
        _deviceError = null;
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
            if (!applied) ShowDeviceError(L10n.T("Tray.AudioActionFailed"));
            if (IsVisible) RefreshData();
        });
    }

    private void DisconnectExternalRow(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var row = _rows[rowIdx];
        if (!row.IsExternal) return;
#if SONICROUTE_PHONE
        _ = Task.Run(async () =>
        {
            try
            {
                if (_host.Audio.ExternalAudio is { } bridge)
                    await bridge.StopExternalSourceAsync(row.App).ConfigureAwait(false);
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("Panel.Disconnect", ex); _host.TryPost(_host.ReportAudioWriteFailure); }
            _host.TryPost(() => { if (IsVisible && !IsDisposed) RefreshData(); });
        });
#endif
    }

    internal void ShowDeviceError(string message)
    {
        if (IsDisposed || !IsVisible) return;
        _deviceError = message;
        Invalidate();
    }

    private void SelectSystemDefault(string id)
    {
        _deviceError = null;
        long request = ++_defaultDeviceRequest;
        _host.SubmitPanelAudio(() =>
        {
            bool applied = false;
            try
            {
                applied = _host.Audio.SetDefault(Flow.Render, id)
                    && IdCmp.Equals(ProductAudio.DefaultId(Flow.Render, Role.Multimedia), id)
                    && IdCmp.Equals(ProductAudio.DefaultId(Flow.Render, Role.Console), id);
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("Panel.Default", ex); }
            _host.PostContinuation(() =>
            {
                if (IsDisposed || request != _defaultDeviceRequest) return;
                if (!applied) ShowDeviceError(L10n.T("Tray.AudioActionFailed"));
                if (_outputVolumesPopup is { IsVisible: true }) _outputVolumesPopup.RefreshData();
                _host.NotifyAudioVisualChanged();
            });
        });
    }

    private void OpenMasterDeviceCombo(UiRect anchor)
    {
        var visibleOutputs = _outputs.Where(d => _config.IsDeviceShown(EDataFlow.eRender, d.PreferenceId)).ToArray();
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
            _deviceError = null;
            if (_config.PanelChangeSystemDefault)
                SelectSystemDefault(picked.Key);
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
        if (_comboPopup != null) _comboPopup.Closed -= OnComboClosed;
        ComboPopupWindow.Close(ref _comboPopup);
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
            _phoneCompanion ??= new PhoneAudioWindowView(_host, _config, CheckAutoDismiss);
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
            _outputVolumesPopup ??= new OutputVolumesPopupWindow(_host, _config, SelectSystemDefault, CheckAutoDismiss, OnOutputPopupEndpointChanged);
            _outputVolumesPopup.ShowForOwner(this, phoneOccupied);
        }
        else if (_outputVolumesPopup is { IsVisible: true })
        {
            _outputVolumesPopup.Hide();
        }
    }

    internal void ApplyCompanionEntrance(LayeredWindow companion) => companion.SetDockedEntrance(this, _entranceProgress);

    private void UpdateCompanionEntranceOpacity()
    {
#if SONICROUTE_PHONE
        if (_phoneCompanion is { IsVisible: true } phone) ApplyCompanionEntrance(phone);
#endif
        if (_outputVolumesPopup is { IsVisible: true } outputs) ApplyCompanionEntrance(outputs);
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

    private void SetGroupBounds(int left, int top, float height)
    {
        SetPanelBoundsPx(left, top, UiMetrics.QuickPanelWindowWidth, height, deferPresentation: true);
        RepositionCompanions(deferPresentation: true);
        PresentPanelBounds();
#if SONICROUTE_PHONE
        _phoneCompanion?.PresentPanelBounds();
#endif
        _outputVolumesPopup?.PresentPanelBounds();
    }

    private void RepositionCompanions(bool deferPresentation = false)
    {
        UiRect? phoneOccupied = null;
#if SONICROUTE_PHONE
        if (_phoneCompanion is { IsVisible: true })
        {
            _phoneCompanion.RepositionForOwner(this, deferPresentation);
            float s = DpiScale;
            phoneOccupied = new UiRect(_phoneCompanion.LeftPx / s, _phoneCompanion.TopPx / s, _phoneCompanion.WidthDip, _phoneCompanion.HeightDip);
        }
#endif
        if (_outputVolumesPopup is { IsVisible: true })
        {
            _outputVolumesPopup.RepositionForOwner(this, phoneOccupied, deferPresentation);
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

    internal void ApplyConfiguredBounds()
    {
        GetMonitorWorkAreaForWindow(Handle, out _, out int mt, out _, out int mb, out uint monDpi);
        float s = monDpi / 96f;
        float maxH = MathF.Max(240f, (mb - mt) / s - 16f);
        float h = Math.Clamp(_config.QuickPanelHeight, UiMetrics.QuickPanelMinHeight, UiMetrics.QuickPanelMaxHeight);
        h = MathF.Min(h, maxH);
        if (_adjustMode)
        {
            SetGroupBounds(LeftPx, TopPx, h);
            return;
        }
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
            int chPx = (int)MathF.Round(h * cs);
            int cx = Math.Clamp(guessXPx, cml, Math.Max(cml, cmr - cwPx));
            int cy = Math.Clamp(guessYPx, cmt, Math.Max(cmt, cmb - chPx));
            SetGroupBounds(cx, cy, h);
            return;
        }

        GetMonitorWorkAreaForPoint(0, 0, out int ml, out mt, out int mr, out mb, out monDpi);
        s = monDpi / 96f;
        int wPx = (int)MathF.Round(WidthDip * s);
        int hPx = (int)MathF.Round(h * s);
        int xPx = mr - wPx - (int)MathF.Round(12f * s);
        int yPx = mb - hPx - (int)MathF.Round(10f * s);
        if (yPx < mt + (int)MathF.Round(6f * s)) yPx = mt + (int)MathF.Round(6f * s);

        xPx = Math.Clamp(xPx, ml, Math.Max(ml, mr - wPx));
        yPx = Math.Clamp(yPx, mt, Math.Max(mt, mb - hPx));
        SetGroupBounds(xPx, yPx, h);
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
        if (_adjustMode)
        {
            _adjustMode = false;
            _host.NotifyAdjustmentChanged();
        }
        _adjustDragging = false;
        _host.ViewSettingsChanged();
        _host.PublishOsd("📍", L10n.T("Exp.PanelPosSaved"), success: true);
        SyncCompanionsVisibility();
        Invalidate();
    }

    internal void CancelAdjustMode()
    {
        if (_adjustMode)
        {
            _adjustMode = false;
            _host.NotifyAdjustmentChanged();
        }
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

    private bool TryGetExternalIcon(string key, byte[] png, out byte[] bgra, out int w, out int h, out int ver)
    {
        if (_extIconCache.TryGetValue(key, out var cached))
        {
            bgra = cached.Pixels; w = cached.W; h = cached.H; ver = cached.Ver;
            return true;
        }
        if (UiBitmap.TryDecodePng(png, 48, out byte[] pixels, out int pw, out int ph))
        {
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
