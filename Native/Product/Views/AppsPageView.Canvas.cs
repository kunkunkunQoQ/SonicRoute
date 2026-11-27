using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;
#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
#endif

namespace SonicRoute.Native.Product.Views;

internal sealed partial class AppsPageView
{
    private enum InteractionMode
    {
        None,
        DraggingAppVolume,
        DraggingForwardVolume,
        DraggingDeviceVolume,
        DraggingNode,
        PanningCanvas,
        DraggingWire,
    }

    private enum WireDragOriginKind { NativePort, ForwardPort, AddOutputPort, MicPort, RenderDevicePort, CaptureDevicePort }
    private enum RenderDeviceDropKind { None, Native, ForwardRow, AddOutput }

    // Reusable single canvas state (incrementally updated when switching apps)
    private readonly Dictionary<string, CanvasDeviceNode> _deviceNodes = new(IdCmp);
    private readonly HashSet<string> _seenNodeKeys = new(IdCmp);
    private readonly List<CanvasDeviceNode> _orderedRenderNodes = new();
    private readonly List<CanvasDeviceNode> _orderedCaptureNodes = new();
    private readonly List<string> _raisedCanvasCards = new();
    private readonly List<CanvasDeviceNode?> _canvasCardOrder = new(); // null is the app card
    private readonly List<CanvasWire> _wires = new();
    private readonly List<string> _forwardTargetIds = new();
    private readonly CanvasWireGeometryCache _wireCache = new();
    private readonly Dictionary<string, UiRect> _sliderWorldRects = new(IdCmp);
    // Per-app remembered node positions (for clean reconnect / mic-toggle restore without disk churn)
    private readonly Dictionary<string, Dictionary<string, RememberedNodePosition>> _rememberedNodePositions = new(IdCmp);

    private UiRect _canvasRect;
    private UiRect _canvasToolbarRect;
    private float _zoom = 1f;
    private float _panX;
    private float _panY;
    private float _appNodeX = AppsCanvasMetrics.DefaultStartX;
    private float _appNodeY = AppsCanvasMetrics.DefaultStartY;
    private float _appNodeHeight = 250f;
    private bool _appNodeUserPositioned;
    private UiRect _appRenameWorldRect;
    private UiRect _appAddOutputWorldRect;
    private UiPoint _appAddOutputPortWorld;
    private UiRect _appMicComboWorldRect;
    private UiRect _appMasterZoneWorldRect;
    private UiRect _appForwardZoneWorldRect;
    private UiRect _appMicZoneWorldRect;
    private UiRect _appMoreWorldRect;
    private UiPoint _appNativePortWorld;
    private UiPoint _appMicPortWorld;
    private readonly Dictionary<string, UiPoint> _appForwardPortsWorld = new(IdCmp);

    private string? _activeCanvasAppKey;
    private string? _lastLayoutSignature;
    private bool _needsInitialFit;
    private bool _layoutDirty;
    private string? _lastMovedDeviceLayoutKey;
    private IDisposable? _layoutSaveTimer;
    private OutputConnectionPopupWindow? _outputPopup;

    // Strict mutually exclusive input interaction state machine
    private InteractionMode _interaction;
    private float _dragStartScreenX;
    private float _dragStartScreenY;
    private float _dragOrigX;
    private float _dragOrigY;
    private bool _dragMoved;
    private string? _dragNodeKey; // null = app node, non-null = device node key
    private string? _dragForwardDeviceId;
    private string? _dragDeviceNodeKey;
    private int _dragVolumeVal;

    // Wire drag state
    private WireDragOriginKind _wireOriginKind;
    private string? _wireOriginDeviceId;
    private UiPoint _wireOriginWorld;
    private UiPoint _wireCursorWorld;
    private bool _wireDropInCanvas;
    private UiColor _wireDragColor;
    private bool _wireDragDashed;
    private string? _hoveredWireKey;

    private float CanvasOrigin(float origin) => MathF.Abs(_zoom - 1f) < 0.001f ? MathF.Round(origin) : origin;
    private float CanvasOriginX => CanvasOrigin(_canvasRect.X + _panX);
    private float CanvasOriginY => CanvasOrigin(_canvasRect.Y + _panY);
    private float ScreenToWorldX(float sx) => (sx - CanvasOriginX) / _zoom;
    private float ScreenToWorldY(float sy) => (sy - CanvasOriginY) / _zoom;
    private float WorldToScreenX(float wx) => CanvasOriginX + wx * _zoom;
    private float WorldToScreenY(float wy) => CanvasOriginY + wy * _zoom;
    private UiRect WorldToScreen(UiRect r) => new(WorldToScreenX(r.X), WorldToScreenY(r.Y), r.Width * _zoom, r.Height * _zoom);

    private Dictionary<string, RememberedNodePosition> GetRememberedMap(string appKey)
    {
        if (!_rememberedNodePositions.TryGetValue(appKey, out var map))
        {
            if (_rememberedNodePositions.Count >= AppConfig.MaxCanvasLayouts)
                _rememberedNodePositions.Remove(_rememberedNodePositions.Keys.First());
            map = new Dictionary<string, RememberedNodePosition>(IdCmp);
            _rememberedNodePositions[appKey] = map;
        }
        return map;
    }

    private void RememberNodePosition(string? appKey, CanvasDeviceNode node)
    {
        if (string.IsNullOrWhiteSpace(appKey) || !node.HasAssignedPosition) return;
        var map = GetRememberedMap(appKey);
        if (map.Count >= AppCanvasLayout.MaxDevices && !map.ContainsKey(node.LayoutKey))
        {
            foreach (var pair in map)
            {
                if (pair.Value.UserPositioned) continue;
                map.Remove(pair.Key);
                break;
            }
            if (map.Count >= AppCanvasLayout.MaxDevices) map.Remove(map.Keys.First());
        }
        map[node.LayoutKey] = new RememberedNodePosition(node.X, node.Y, node.IsUserPositioned);
    }

    private void ReleaseMicResources()
    {
        _inputs = Array.Empty<Endpoint>();
        foreach (var node in _orderedCaptureNodes)
        {
            RememberNodePosition(_activeCanvasAppKey, node);
            _deviceNodes.Remove(node.NodeKey);
        }
        _orderedCaptureNodes.Clear();
        _canvasCardOrder.RemoveAll(static node => node?.Flow == Flow.Capture);
        _wires.RemoveAll(w => w.Kind == CanvasWireKind.MicrophoneInput);
        _lastLayoutSignature = null;
    }

    private string BuildLayoutSignature(string appKey, bool showMic, float appHeight)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(appKey).Append('|').Append(showMic ? '1' : '0').Append('|').Append((int)MathF.Round(appHeight));
        sb.Append("|R:");
        for (int i = 0; i < _orderedRenderNodes.Count; i++)
        {
            var n = _orderedRenderNodes[i];
            sb.Append(n.NodeKey).Append('@').Append((int)n.Height).Append(n.HasAssignedPosition ? '1' : '0').Append(';');
        }
        sb.Append("|C:");
        for (int i = 0; i < _orderedCaptureNodes.Count; i++)
        {
            var n = _orderedCaptureNodes[i];
            sb.Append(n.NodeKey).Append('@').Append((int)n.Height).Append(n.HasAssignedPosition ? '1' : '0').Append(';');
        }
        return sb.ToString();
    }

    private Dictionary<string, RememberedNodePosition> BuildMergedRememberedPositions(string appKey, AppCanvasLayout? saved)
    {
        var merged = new Dictionary<string, RememberedNodePosition>(IdCmp);
        if (saved?.Devices != null)
        {
            foreach (var pair in saved.Devices)
                merged[pair.Key] = new RememberedNodePosition(pair.Value.X, pair.Value.Y, UserPositioned: !pair.Value.Auto);
        }
        if (_rememberedNodePositions.TryGetValue(appKey, out var liveMap))
        {
            foreach (var pair in liveMap)
                merged[pair.Key] = pair.Value;
        }
        return merged;
    }

    private Func<CanvasDeviceNode, int> MakeRenderRank(AudioAppInfo? app)
    {
        string? nativeId = app != null ? GetDisplayedNativeOutputId(app) : null;
        var outputs = DisplayedOutputs;
        return n =>
        {
            if (nativeId != null && IdCmp.Equals(n.DeviceId, nativeId)) return 0;
            if (outputs.Contains(n.DeviceId, IdCmp)) return 1;
            if (n.IsSystemDefault) return 2;
            if (n.IsActualWindowsDefault) return 3;
            return n.IsPhoneOutput ? 5 : 4;
        };
    }

    private Func<CanvasDeviceNode, int> MakeCaptureRank(AudioAppInfo? app)
    {
        return n =>
        {
            if (app != null && !app.IsExternal && IdCmp.Equals(DisplayedInput, n.DeviceId)) return 0;
            if (n.IsSystemDefault) return 1;
            return n.IsActualWindowsDefault ? 2 : 3;
        };
    }

    private void SyncCanvasNodesAndLayout(bool forceInitialFit = false)
    {
        var app = _selectedApp;
        if (app == null)
        {
            _orderedRenderNodes.Clear();
            _orderedCaptureNodes.Clear();
            _canvasCardOrder.Clear();
            _deviceNodes.Clear();
            _wires.Clear();
            _wireCache.Clear();
            _lastLayoutSignature = null;
            return;
        }

        var cfg = _actions.Config;
        string appKey = GetStableAppKey(app);
        bool switchedApp = !IdCmp.Equals(_activeCanvasAppKey, appKey);
        if (switchedApp && !string.IsNullOrWhiteSpace(_activeCanvasAppKey))
        {
            foreach (var oldNode in _deviceNodes.Values)
                RememberNodePosition(_activeCanvasAppKey, oldNode);
        }
        _activeCanvasAppKey = appKey;
        AppCanvasLayout? saved = cfg.AppCanvasLayouts.TryGetValue(appKey, out var foundLayout) ? foundLayout : null;

        if (switchedApp)
        {
            _lastLayoutSignature = null;
            _raisedCanvasCards.Clear();
            if (saved?.RaisedCards is { } raisedCards) _raisedCanvasCards.AddRange(raisedCards);
            if (saved != null)
            {
                _zoom = Math.Clamp(saved.Zoom, AppsCanvasMetrics.MinZoom, AppsCanvasMetrics.MaxZoom);
                _panX = saved.PanX;
                _panY = saved.PanY;
                if (saved.AppNode != null)
                {
                    _appNodeX = saved.AppNode.X;
                    _appNodeY = saved.AppNode.Y;
                    _appNodeUserPositioned = !saved.AppNode.Auto;
                }
                else
                {
                    _appNodeX = AppsCanvasMetrics.DefaultStartX;
                    _appNodeY = AppsCanvasMetrics.DefaultStartY;
                    _appNodeUserPositioned = false;
                }
                _needsInitialFit = false;
            }
            else
            {
                _zoom = 1f;
                _panX = 0f;
                _panY = 0f;
                _appNodeX = AppsCanvasMetrics.DefaultStartX;
                _appNodeY = AppsCanvasMetrics.DefaultStartY;
                _appNodeUserPositioned = false;
                _needsInitialFit = true;
            }

            // Reset existing node assignment flags so switched app loads its own layout cleanly
            foreach (var existing in _deviceNodes.Values)
            {
                existing.HasAssignedPosition = false;
                existing.IsUserPositioned = false;
            }
        }
        else if (forceInitialFit && saved == null)
        {
            _needsInitialFit = true;
        }

        var activeOutputs = DisplayedOutputs;
        _seenNodeKeys.Clear();
        bool topologyChanged = false;
        _orderedRenderNodes.Clear();
        _orderedCaptureNodes.Clear();

        void UpsertDevice(Flow flow, string id, string prefId, string rawName, bool isSysDefault, bool isActualDefault, bool isHiddenFallback)
        {
            string nodeKey = DeviceNodeKey(flow, id);
            if (!_seenNodeKeys.Add(nodeKey)) return;
            string layoutKey = GetStableDeviceLayoutKey(flow, id, prefId);
            if (!_deviceNodes.TryGetValue(nodeKey, out var node) || node == null)
            {
                node = new CanvasDeviceNode
                {
                    NodeKey = nodeKey,
                    DeviceId = id,
                    PreferenceId = prefId,
                    LayoutKey = layoutKey,
                    Flow = flow,
                };
                _deviceNodes[nodeKey] = node;
                topologyChanged = true;
            }

            bool isPhone = flow == Flow.Render && (PcRoutingApi.IsPhoneOutputDevice(id)
#if SONICROUTE_PHONE
                || IdCmp.Equals(id, AndroidAudioSession.SourcePhoneOutputId)
#endif
                );
            node.PreferenceId = prefId;
            node.LayoutKey = layoutKey;
            node.IsSystemDefault = isSysDefault;
            node.IsActualWindowsDefault = isActualDefault;
            node.IsPhoneOutput = isPhone;
            node.IsHiddenFallback = isHiddenFallback;
            node.Icon = AppsCanvasPalette.ClassifyDeviceIcon(flow, id, rawName);
            node.Height = isPhone ? AppsCanvasMetrics.PhoneDeviceNodeHeight : AppsCanvasMetrics.DeviceNodeHeight;

            bool hasCustomName = (flow == Flow.Render || !isSysDefault)
                && cfg.DeviceNames.TryGetValue(id, out string? custom) && !string.IsNullOrWhiteSpace(custom);
            if (isSysDefault)
            {
                node.PrimaryName = hasCustomName ? cfg.DeviceNames[id] : L10n.T(flow == Flow.Render ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn");
                string? actualDefId = flow == Flow.Render ? _windowsDefaultRenderId : _windowsDefaultCaptureId;
                var list = flow == Flow.Render ? _outputs : _inputs;
                string? actualDefName = actualDefId == null ? null
                    : cfg.DeviceNames.TryGetValue(actualDefId, out var alias) && !string.IsNullOrWhiteSpace(alias)
                        ? alias
                        : list.FirstOrDefault(e => IdCmp.Equals(e.Id, actualDefId))?.Name;
                node.SecondaryName = !string.IsNullOrWhiteSpace(actualDefName)
                    ? L10n.T("Ov.Current") + AppsCanvasPalette.SplitFriendlyName(actualDefName).Primary
                    : "";
                node.FullTooltip = !string.IsNullOrWhiteSpace(actualDefName)
                    ? $"{node.PrimaryName}\n{L10n.T("Ov.Current")}{actualDefName}"
                    : node.PrimaryName;
            }
            else
            {
                var (primary, secondary) = AppsCanvasPalette.SplitFriendlyName(rawName);
                node.PrimaryName = hasCustomName ? cfg.DeviceNames[id] : (primary.Length > 0 ? primary : rawName);
                node.SecondaryName = hasCustomName ? rawName : secondary;
                node.FullTooltip = string.IsNullOrEmpty(node.SecondaryName) ? node.PrimaryName : $"{node.PrimaryName}\n{node.SecondaryName}";
            }

            if (isPhone)
            {
                node.IsAvailable = true;
                node.Volume = 100;
                node.Muted = false;
            }
            else if (_deviceVolumes.TryGetValue(nodeKey, out var volState))
            {
                node.Volume = volState.Volume;
                node.Muted = volState.Muted;
                node.IsAvailable = volState.Available;
            }
            else
            {
                node.IsAvailable = false;
            }

            if (flow == Flow.Render) _orderedRenderNodes.Add(node);
            else _orderedCaptureNodes.Add(node);
        }

        // 1. Build/update Render (Playback) Device Nodes (Windows + connected Phone outputs)
        if (_outputChoicesIdentity == _selectedIdentity)
        {
            bool showSysOut = cfg.IsDeviceShown(EDataFlow.eRender, AudioService.SystemDefaultDeviceId);
            if (showSysOut || activeOutputs.Contains(AudioService.SystemDefaultDeviceId, IdCmp))
            {
                UpsertDevice(Flow.Render, AudioService.SystemDefaultDeviceId, AudioService.SystemDefaultDeviceId,
                    L10n.T("Dev.SystemDefaultOut"), isSysDefault: true, isActualDefault: false, isHiddenFallback: !showSysOut);
            }

            foreach (var ep in _outputs)
            {
                bool shown = cfg.IsDeviceShown(EDataFlow.eRender, ep.PreferenceId);
                bool connected = activeOutputs.Contains(ep.Id, IdCmp);
                if (!shown && !connected) continue;
                bool isWinDef = _windowsDefaultRenderId != null && IdCmp.Equals(ep.Id, _windowsDefaultRenderId);
                UpsertDevice(Flow.Render, ep.Id, ep.PreferenceId, ep.Name, isSysDefault: false, isActualDefault: isWinDef, isHiddenFallback: !shown);
            }
        }

        // 2. Build/update Capture (Microphone) Device Nodes ONLY when ExperimentalMic is enabled
        if (cfg.ExperimentalMic)
        {
            bool showSysIn = cfg.IsDeviceShown(EDataFlow.eCapture, AudioService.SystemDefaultInputDeviceId);
            if (showSysIn || (!app.IsExternal && IdCmp.Equals(DisplayedInput, AudioService.SystemDefaultInputDeviceId)))
            {
                UpsertDevice(Flow.Capture, AudioService.SystemDefaultInputDeviceId, AudioService.SystemDefaultInputDeviceId,
                    L10n.T("Dev.SystemDefaultIn"), isSysDefault: true, isActualDefault: false, isHiddenFallback: !showSysIn);
            }
            foreach (var ep in _inputs)
            {
                bool shown = cfg.IsDeviceShown(EDataFlow.eCapture, ep.PreferenceId);
                bool connected = !app.IsExternal && IdCmp.Equals(DisplayedInput, ep.Id);
                if (!shown && !connected) continue;
                bool isWinDef = _windowsDefaultCaptureId != null && IdCmp.Equals(ep.Id, _windowsDefaultCaptureId);
                UpsertDevice(Flow.Capture, ep.Id, ep.PreferenceId, ep.Name, isSysDefault: false, isActualDefault: isWinDef, isHiddenFallback: !shown);
            }
        }

        // Remember positions of departing/hidden/disconnected devices before removing them so reconnect can restore cleanly
        if ((topologyChanged || _seenNodeKeys.Count != _deviceNodes.Count)
            && _interaction is InteractionMode.DraggingWire or InteractionMode.DraggingDeviceVolume or InteractionMode.DraggingNode)
            CancelCanvasInteraction(commitLayout: false);
        foreach (string key in _deviceNodes.Keys)
            if (!_seenNodeKeys.Contains(key) && _deviceNodes.Remove(key, out var removed))
                RememberNodePosition(appKey, removed);

        // Compute true App Node height with current forwarding targets & mic setting
        var forwardIds = GetForwardTargetIds(app);
        bool showMic = cfg.ExperimentalMic;
        _appNodeHeight = ComputeAppNodeHeight(app, forwardIds.Count, showMic);

        // Skip layout math entirely if topology, heights, mic visibility, and app identity did not change!
        string sig = BuildLayoutSignature(appKey, showMic, _appNodeHeight);
        if (!switchedApp && !_needsInitialFit && sig == _lastLayoutSignature)
            return;

        bool doFullArrange = saved == null && (switchedApp || _needsInitialFit);
        if (doFullArrange)
        {
            ArrangeNodesDefault(fitViewport: !_canvasRect.IsEmpty);
        }
        else
        {
            var remembered = BuildMergedRememberedPositions(appKey, saved);
            AppsCanvasLayoutEngine.Reconcile(
                ref _appNodeX,
                ref _appNodeY,
                _appNodeHeight,
                ref _appNodeUserPositioned,
                _orderedRenderNodes,
                _orderedCaptureNodes,
                MakeRenderRank(app),
                MakeCaptureRank(app),
                fullArrange: false,
                remembered: remembered,
                restoreSavedStack: switchedApp && _raisedCanvasCards.Count > 0);
            RebuildCanvasCardOrder();
        }

        _lastLayoutSignature = BuildLayoutSignature(appKey, showMic, _appNodeHeight);
    }

    private void ReconcileCanvasIfAppHeightChanged()
    {
        var app = _selectedApp;
        if (app == null || string.IsNullOrWhiteSpace(_activeCanvasAppKey)) return;
        bool showMic = _actions.Config.ExperimentalMic;
        float nextH = ComputeAppNodeHeight(app, GetForwardTargetIds(app).Count, showMic);
        if (MathF.Abs(nextH - _appNodeHeight) < 0.5f) return;

        _appNodeHeight = nextH;
        AppCanvasLayout? saved = _actions.Config.AppCanvasLayouts.TryGetValue(_activeCanvasAppKey, out var found) ? found : null;
        var remembered = BuildMergedRememberedPositions(_activeCanvasAppKey, saved);
        AppsCanvasLayoutEngine.Reconcile(
            ref _appNodeX,
            ref _appNodeY,
            _appNodeHeight,
            ref _appNodeUserPositioned,
            _orderedRenderNodes,
            _orderedCaptureNodes,
            MakeRenderRank(app),
            MakeCaptureRank(app),
            fullArrange: false,
            remembered: remembered);
        _lastLayoutSignature = BuildLayoutSignature(_activeCanvasAppKey, showMic, _appNodeHeight);
    }

    private float ComputeAppNodeHeight(AudioAppInfo app, int forwardCount, bool showMic)
    {
        // Header(64) + PlaybackHeader(22) + MasterVol(34) + Forwards(n * 54) + AddOutputPortRow(32)
        // + OptionalMic(68) + Divider(10) + 2 QuickSwitches(58) + Pad(12)
        float h = 64f + 22f + 34f + forwardCount * 54f + 32f + 10f + 58f + 12f;
        if (showMic) h += 68f;
        return h;
    }

    private const float CanvasFitPaddingX = 24f;
    private const float CanvasDefaultMinZoom = 0.78f;

    private void ArrangeNodesDefault(bool fitViewport)
    {
        var app = _selectedApp;
        int forwardCount = app != null ? GetForwardTargetIds(app).Count : 0;
        bool showMic = _actions.Config.ExperimentalMic;
        _appNodeHeight = app != null ? ComputeAppNodeHeight(app, forwardCount, showMic) : 250f;
        float twoColumnWidth = AppsCanvasMetrics.AppNodeWidth + AppsCanvasMetrics.DefaultColumnGap
            + 2f * AppsCanvasMetrics.DeviceNodeWidth + AppsCanvasMetrics.DefaultDeviceGapX;
        bool allowTwoColumns = _canvasRect.Width - 2f * CanvasFitPaddingX >= twoColumnWidth * CanvasDefaultMinZoom;

        AppsCanvasLayoutEngine.Reconcile(
            ref _appNodeX,
            ref _appNodeY,
            _appNodeHeight,
            ref _appNodeUserPositioned,
            _orderedRenderNodes,
            _orderedCaptureNodes,
            MakeRenderRank(app),
            MakeCaptureRank(app),
            fullArrange: true,
            remembered: null,
            allowTwoColumns: allowTwoColumns);
        RebuildCanvasCardOrder();

        if (!string.IsNullOrWhiteSpace(_activeCanvasAppKey))
            _lastLayoutSignature = BuildLayoutSignature(_activeCanvasAppKey, showMic, _appNodeHeight);

        if (fitViewport && !_canvasRect.IsEmpty)
        {
            FrameDefaultViewport();
            _needsInitialFit = false;
        }
    }

    private void FrameDefaultViewport()
    {
        if (_canvasRect.IsEmpty || _selectedApp == null) return;
        const float padX = CanvasFitPaddingX;
        const float padTop = 52f; // clear space below floating top-right toolbar
        const float padBottom = 24f;
        float availW = MathF.Max(160f, _canvasRect.Width - padX * 2f);
        float availH = MathF.Max(160f, _canvasRect.Height - padTop - padBottom);

        float minX = _appNodeX;
        float minY = _appNodeY;
        float maxX = _appNodeX + AppsCanvasMetrics.AppNodeWidth;
        float maxY = _appNodeY + _appNodeHeight;
        foreach (var n in _deviceNodes.Values)
        {
            if (n.X < minX) minX = n.X;
            if (n.Y < minY) minY = n.Y;
            if (n.X + n.Width > maxX) maxX = n.X + n.Width;
            if (n.Y + n.Height > maxY) maxY = n.Y + n.Height;
        }
        // Reserve space above topmost device node so the low-contrast partition header never touches the top-right toolbar
        if (_orderedRenderNodes.Count > 0 || _orderedCaptureNodes.Count > 0)
            minY -= AppsCanvasMetrics.SectionHeaderHeight;

        // Compute readable primary focus width (App Node + Column 1) so many devices never crush initial zoom below 78%
        float primaryMaxX = _appNodeX + AppsCanvasMetrics.AppNodeWidth + AppsCanvasMetrics.DefaultColumnGap + AppsCanvasMetrics.DeviceNodeWidth;
        float totalW = MathF.Max(100f, maxX - minX);
        float totalH = MathF.Max(100f, maxY - minY);
        float focusW = totalW <= primaryMaxX - minX + 10f ? totalW : MathF.Max(100f, primaryMaxX - minX);

        float scaleForWidth = availW / totalW;
        bool fitAllColumns = scaleForWidth >= CanvasDefaultMinZoom;
        float scaleAll = MathF.Min(scaleForWidth, availH / totalH);
        float scale;
        if (scaleAll >= CanvasDefaultMinZoom)
        {
            scale = Math.Clamp(scaleAll, CanvasDefaultMinZoom, 1.0f);
        }
        else
        {
            float focusScale = MathF.Min(availW / focusW, availH / MathF.Max(_appNodeHeight + 24f, 360f));
            scale = Math.Clamp(focusScale, CanvasDefaultMinZoom, 1.0f);
            if (fitAllColumns) scale = MathF.Min(scale, scaleForWidth);
        }

        _zoom = scale;
        float fittedW = (fitAllColumns ? totalW : focusW) * scale;
        _panX = (availW >= fittedW ? padX + (availW - fittedW) * 0.5f : padX) - minX * scale;
        float fittedH = totalH * scale;
        _panY = (availH >= fittedH ? padTop + (availH - fittedH) * 0.5f : padTop) - minY * scale;
    }

    private void FitCanvasToViewport(float clampMaxZoom = 1.10f)
    {
        if (_canvasRect.IsEmpty || _selectedApp == null) return;
        float minX = _appNodeX;
        float minY = _appNodeY;
        float maxX = _appNodeX + AppsCanvasMetrics.AppNodeWidth;
        float maxY = _appNodeY + _appNodeHeight;

        foreach (var n in _deviceNodes.Values)
        {
            if (n.X < minX) minX = n.X;
            if (n.Y < minY) minY = n.Y;
            if (n.X + n.Width > maxX) maxX = n.X + n.Width;
            if (n.Y + n.Height > maxY) maxY = n.Y + n.Height;
        }
        // Reserve space above topmost device node so the low-contrast partition header never touches the top-right toolbar
        if (_orderedRenderNodes.Count > 0 || _orderedCaptureNodes.Count > 0)
            minY -= AppsCanvasMetrics.SectionHeaderHeight;

        float contentW = MathF.Max(100f, maxX - minX);
        float contentH = MathF.Max(100f, maxY - minY);
        const float padX = 28f;
        const float padTop = 52f; // leave breathing room for floating top-right toolbar
        const float padBottom = 24f;
        float availW = MathF.Max(120f, _canvasRect.Width - padX * 2f);
        float availH = MathF.Max(120f, _canvasRect.Height - padTop - padBottom);

        float scale = Math.Clamp(MathF.Min(availW / contentW, availH / contentH), AppsCanvasMetrics.MinZoom, clampMaxZoom);
        _zoom = scale;
        float scaledW = contentW * scale;
        float scaledH = contentH * scale;
        _panX = (availW >= scaledW ? padX + (availW - scaledW) * 0.5f : padX) - minX * scale;
        _panY = (availH >= scaledH ? padTop + (availH - scaledH) * 0.5f : padTop) - minY * scale;
    }

    private void ScheduleCanvasLayoutSave()
    {
        if (_selectedApp == null) return;
        _layoutDirty = true;
        _layoutSaveTimer?.Dispose();
        _layoutSaveTimer = _actions.Host.ScheduleOnce(TimeSpan.FromMilliseconds(450), FlushCanvasLayoutSave);
    }

    private void FlushCanvasLayoutSave()
    {
        _layoutSaveTimer?.Dispose();
        _layoutSaveTimer = null;
        if (!_layoutDirty || _selectedApp == null || string.IsNullOrWhiteSpace(_activeCanvasAppKey)) return;
        _layoutDirty = false;

        var layout = new AppCanvasLayout
        {
            Zoom = Math.Clamp(_zoom, AppsCanvasMetrics.MinZoom, AppsCanvasMetrics.MaxZoom),
            PanX = MathF.Round(_panX * 10f) * 0.1f,
            PanY = MathF.Round(_panY * 10f) * 0.1f,
            AppNode = new CanvasPoint
            {
                X = MathF.Round(_appNodeX * 10f) * 0.1f,
                Y = MathF.Round(_appNodeY * 10f) * 0.1f,
                Auto = !_appNodeUserPositioned,
            },
            Devices = new Dictionary<string, CanvasPoint>(IdCmp),
            RaisedCards = _raisedCanvasCards,
        };

        void AddPosition(string key, float x, float y, bool auto)
        {
            if (layout.Devices.Count >= AppCanvasLayout.MaxDevices || layout.Devices.ContainsKey(key)) return;
            layout.Devices.Add(key, new CanvasPoint
            {
                X = MathF.Round(x * 10f) * 0.1f,
                Y = MathF.Round(y * 10f) * 0.1f,
                Auto = auto,
            });
        }

        var activeKeys = new HashSet<string>(IdCmp);
        foreach (var node in _deviceNodes.Values)
            if (node.HasAssignedPosition) activeKeys.Add(node.LayoutKey);

        // Keep the newest move and active manual positions ahead of offline and automatic positions.
        if (_lastMovedDeviceLayoutKey is { } movedKey)
            foreach (var node in _deviceNodes.Values)
                if (node.HasAssignedPosition && IdCmp.Equals(node.LayoutKey, movedKey))
                    AddPosition(node.LayoutKey, node.X, node.Y, auto: !node.IsUserPositioned);
        _lastMovedDeviceLayoutKey = null;
        foreach (var node in _deviceNodes.Values)
            if (node.HasAssignedPosition && node.IsUserPositioned)
                AddPosition(node.LayoutKey, node.X, node.Y, auto: false);
        if (_rememberedNodePositions.TryGetValue(_activeCanvasAppKey, out var remembered))
            foreach (var pair in remembered)
                if (pair.Value.UserPositioned && !activeKeys.Contains(pair.Key))
                    AddPosition(pair.Key, pair.Value.X, pair.Value.Y, auto: false);
        if (_actions.Config.AppCanvasLayouts.TryGetValue(_activeCanvasAppKey, out var prevSaved))
            foreach (var pair in prevSaved.Devices)
                if (!pair.Value.Auto && !activeKeys.Contains(pair.Key))
                    AddPosition(pair.Key, pair.Value.X, pair.Value.Y, auto: false);
        foreach (var node in _deviceNodes.Values)
            if (node.HasAssignedPosition)
                AddPosition(node.LayoutKey, node.X, node.Y, auto: !node.IsUserPositioned);

        _actions.Config.SaveCanvasLayout(_activeCanvasAppKey, layout);
        ConfigService.QueueSave(_actions.Config);
    }

    private void ResetCurrentAppCanvasLayout()
    {
        if (_selectedApp == null) return;
        _layoutSaveTimer?.Dispose();
        _layoutSaveTimer = null;
        _layoutDirty = false;
        _lastMovedDeviceLayoutKey = null;
        _raisedCanvasCards.Clear();
        string key = GetStableAppKey(_selectedApp);
        _rememberedNodePositions.Remove(key);
        if (_actions.Config.AppCanvasLayouts.Remove(key))
            ConfigService.QueueSave(_actions.Config);
        ArrangeNodesDefault(fitViewport: true);
        _actions.Invalidate();
    }

    private static string GetStableDeviceLayoutKey(Flow flow, string id, string prefId)
    {
        string prefix = flow == Flow.Capture ? "capture:" : "render:";
        if (AudioService.IsSystemDefault(id)) return prefix + id;
#if SONICROUTE_PHONE
        if (flow == Flow.Render && PcRoutingApi.IsPhoneOutputDevice(id))
            return prefix + (PcRoutingApi.GetPersistentOutputId(id) ?? prefId);
#endif
        return prefix + prefId;
    }

    private List<string> GetForwardTargetIds(AudioAppInfo app)
    {
        string? nativeId = GetDisplayedNativeOutputId(app);
        var list = _forwardTargetIds;
        list.Clear();
        foreach (string id in DisplayedOutputs)
            if (!IdCmp.Equals(id, nativeId)) list.Add(id);
        return list;
    }

    private void RenderCanvasWorkspace(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport)
    {
        _canvasRect = viewport;
        _sliderWorldRects.Clear();
        _appForwardPortsWorld.Clear();

        // Canvas outer card
        g.FillRoundedRect(viewport, UiMetrics.CardCornerRadius, t.SurfaceCard.WithAlphaFraction(t.OpacityPercent / 100f));
        g.DrawRoundedRect(viewport, UiMetrics.CardCornerRadius, t.Border, 1f);

        var app = _selectedApp;
        if (app == null || _activeCanvasAppKey == null)
        {
            string hint = app != null ? L10n.T("Tray.Loading") : L10n.T(_apps.Count == 0 ? "Apps.Canvas.NoApps" : "Apps.SelectHint");
            g.DrawText(hint, viewport.Deflate(24f), t.TextSecondary, 14f, 400, UiTextAlign.Center, UiVertAlign.Center, wrap: true);
            return;
        }

        if (_needsInitialFit)
        {
            ArrangeNodesDefault(fitViewport: true);
            _needsInitialFit = false;
        }

        // Update theme-dependent wire colors
        foreach (var node in _deviceNodes.Values)
            node.WireColor = AppsCanvasPalette.DeviceWireColor(t, node.Flow, node.LayoutKey, node.IsSystemDefault);

        var innerClip = viewport.Deflate(1f);
        g.PushClip(innerClip);
        hit.PushClip(innerClip);

        // Blank canvas background hit target (panning)
        hit.Add("canvas.bg", innerClip, _interaction == InteractionMode.PanningCanvas ? UiCursorKind.Hand : UiCursorKind.Arrow);

        // Low-contrast subtle dot grid in screen space
        DrawDotGrid(g, t, innerClip);

        // Compute App Node layout & ports BEFORE drawing wires so endpoints are 100% exact on frame 0
        var forwardIds = GetForwardTargetIds(app);
        bool showMic = _actions.Config.ExperimentalMic;
        _appNodeHeight = ComputeAppNodeHeight(app, forwardIds.Count, showMic);
        ComputeAppNodePorts(app, forwardIds, showMic);
        BuildCanvasWires(t, app, forwardIds, showMic);

        // Unified World Transform (Pan + Zoom)
        float tx = CanvasOriginX;
        float ty = CanvasOriginY;
        g.PushTransform(D2dMatrix3x2.ScaleTranslate(_zoom, tx, ty));
        hit.PushTransform(_zoom, tx, ty);

        // 1. Unified Wire Layer (strictly underneath all nodes!)
        RenderWireLayer(g, t);

        // 2. Low-contrast partition headers (Playback above, Microphone strictly below)
        var appBounds = new UiRect(_appNodeX, _appNodeY, AppsCanvasMetrics.AppNodeWidth, _appNodeHeight);
        if (_orderedRenderNodes.Count > 0 && AppsCanvasLayoutEngine.TryGetSectionHeaderRect(_orderedRenderNodes, _orderedCaptureNodes, appBounds, out var outLblRect))
        {
            g.DrawIcon(UiIcon.Speaker, new UiRect(outLblRect.X, outLblRect.CenterY - 6f, 12f, 12f), t.TextSecondary.WithAlphaFraction(0.8f), 1.6f);
            g.DrawText(L10n.T("Apps.Output"), new UiRect(outLblRect.X + 16f, outLblRect.Y, outLblRect.Width - 16f, 18f),
                t.TextSecondary.WithAlphaFraction(0.85f), 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        }
        if (showMic && _orderedCaptureNodes.Count > 0 && AppsCanvasLayoutEngine.TryGetSectionHeaderRect(_orderedCaptureNodes, _orderedRenderNodes, appBounds, out var micLblRect))
        {
            g.DrawIcon(UiIcon.Microphone, new UiRect(micLblRect.X, micLblRect.CenterY - 6f, 12f, 12f), AppsCanvasPalette.MicWireColor(t), 1.7f);
            g.DrawText(L10n.T("Apps.Input"), new UiRect(micLblRect.X + 16f, micLblRect.Y, micLblRect.Width - 16f, 18f),
                t.TextSecondary, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        }

        // 3. Draw cards and register their hit regions in the same order.
        var activeOutputs = DisplayedOutputs;
        string? nativeId = GetDisplayedNativeOutputId(app);
        bool draggingCard = _interaction == InteractionMode.DraggingNode && _dragMoved;
        CanvasDeviceNode? draggedDevice = null;
        if (draggingCard && _dragNodeKey is { } key) _deviceNodes.TryGetValue(key, out draggedDevice);

        void DrawCard(CanvasDeviceNode? node)
        {
            if (node == null)
            {
                RenderAppNode(g, t, hit, app, forwardIds, showMic);
                return;
            }
            bool playback = node.Flow == Flow.Render;
            if (!playback && !showMic) return;
            bool connected = playback ? activeOutputs.Contains(node.DeviceId, IdCmp)
                : !app.IsExternal && IdCmp.Equals(DisplayedInput, node.DeviceId);
            bool isNative = connected && (!playback || IdCmp.Equals(node.DeviceId, nativeId));
            RenderDeviceNode(g, t, hit, node, connected, isNative);
        }

        bool liftCard = draggingCard && (_dragNodeKey == null || draggedDevice != null);
        foreach (var node in _canvasCardOrder)
            if (!liftCard || node != draggedDevice) DrawCard(node);
        if (liftCard) DrawCard(draggedDevice);

        // 4. Active Wire-Drag Preview Curve (only once pointer passes drag threshold)
        if (_interaction == InteractionMode.DraggingWire && _dragMoved)
            RenderDraggingWirePreview(g, t);

        hit.PopTransform();
        g.PopTransform();

        // 5. Floating Canvas Toolbar (Screen-space overlay, crisp at any zoom)
        RenderCanvasToolbar(g, t, hit, innerClip);

        hit.PopClip();
        g.PopClip();
    }

    private void DrawDotGrid(D2dTarget g, UiThemePalette t, UiRect clip)
    {
        float worldStep = _zoom < 0.72f ? 48f : 24f;
        float stepPx = worldStep * _zoom;
        if (stepPx < 10f) return;

        UiColor dotColor = t.Border.WithAlphaFraction(t.IsDark ? 0.55f : 0.75f);
        float startX = clip.X + ((_panX % stepPx) + stepPx) % stepPx;
        float startY = clip.Y + ((_panY % stepPx) + stepPx) % stepPx;
        float dotSize = _zoom >= 0.85f ? 1.5f : 1.25f;

        for (float y = startY; y < clip.Bottom; y += stepPx)
        {
            for (float x = startX; x < clip.Right; x += stepPx)
                g.FillRect(new UiRect(x, y, dotSize, dotSize), dotColor);
        }
    }

    private float GetOuterPortHitReach() =>
        Math.Clamp(16f / MathF.Max(0.25f, _zoom), AppsCanvasMetrics.PortHitRadius, 26f);

    private float GetAppPortHitHalfY() =>
        // Keep vertical hit radius <= 16.5 world units so adjacent Native / Forward / '+' ports (34u apart) never overlap
        Math.Clamp(13f / MathF.Max(0.25f, _zoom), AppsCanvasMetrics.PortHitRadius, 16.5f);

    private UiRect GetDeviceDropRect(CanvasDeviceNode node)
    {
        float leftPad = GetOuterPortHitReach() + 2f;
        return new UiRect(node.X - leftPad, node.Y - 6f, node.Width + leftPad + 6f, node.Height + 12f);
    }

    private UiRect GetAppDropRect()
    {
        float rightPad = GetOuterPortHitReach() + 4f;
        return new UiRect(_appNodeX - 10f, _appNodeY - 10f, AppsCanvasMetrics.AppNodeWidth + 10f + rightPad, _appNodeHeight + 20f);
    }

    private RenderDeviceDropKind ResolveRenderDeviceDropTarget(float wx, float wy, out string? forwardDeviceId)
    {
        forwardDeviceId = null;
        if (_selectedApp == null) return RenderDeviceDropKind.None;

        float outerX = GetOuterPortHitReach() + 4f;
        float snapY = GetAppPortHitHalfY() + 1f;
        bool HitsAppPort(UiPoint p) =>
            wx >= p.X - 16f && wx <= p.X + outerX && MathF.Abs(wy - p.Y) <= snapY;

        // 1. Direct hits on specific App Node right-edge ports take highest priority
        if (HitsAppPort(_appNativePortWorld))
            return RenderDeviceDropKind.Native;

        foreach (var pair in _appForwardPortsWorld)
        {
            if (HitsAppPort(pair.Value))
            {
                forwardDeviceId = pair.Key;
                return RenderDeviceDropKind.ForwardRow;
            }
        }

        if (HitsAppPort(_appAddOutputPortWorld))
            return RenderDeviceDropKind.AddOutput;

        // 2. Fallback to broad drop zones across the App Node card + right-edge port band
        if (_appMasterZoneWorldRect.Contains(wx, wy))
            return RenderDeviceDropKind.Native;

        if (_appForwardZoneWorldRect.Contains(wx, wy) || GetAppDropRect().Contains(wx, wy))
            return RenderDeviceDropKind.AddOutput;

        return RenderDeviceDropKind.None;
    }

    private bool IsCaptureDeviceDropOnApp(float wx, float wy)
    {
        if (_selectedApp == null || _selectedApp.IsExternal || !_actions.Config.ExperimentalMic) return false;
        float outerX = GetOuterPortHitReach() + 4f;
        float snapY = GetAppPortHitHalfY() + 4f;
        if (wx >= _appMicPortWorld.X - 16f && wx <= _appMicPortWorld.X + outerX && MathF.Abs(wy - _appMicPortWorld.Y) <= snapY)
            return true;
        return _appMicZoneWorldRect.Contains(wx, wy) || GetAppDropRect().Contains(wx, wy);
    }

    private void ComputeAppNodePorts(AudioAppInfo app, List<string> forwardIds, bool showMic)
    {
        float rightX = _appNodeX + AppsCanvasMetrics.AppNodeWidth;
        float rightPortPad = GetOuterPortHitReach() + 4f;
        float y = _appNodeY + 64f; // After header
        y += 22f; // Playback label
        float masterVolCenterY = y + 16f;
        _appNativePortWorld = new UiPoint(rightX, masterVolCenterY);
        _appMasterZoneWorldRect = new UiRect(_appNodeX, _appNodeY, AppsCanvasMetrics.AppNodeWidth + rightPortPad, (y + 34f) - _appNodeY);
        y += 34f;

        float fwdTopY = y;
        for (int i = 0; i < forwardIds.Count; i++)
        {
            _appForwardPortsWorld[forwardIds[i]] = new UiPoint(rightX, y + 25f);
            y += 54f;
        }
        _appAddOutputPortWorld = new UiPoint(rightX, y + 16f);
        _appAddOutputWorldRect = new UiRect(
            rightX - AppsCanvasMetrics.AddPortRadius,
            _appAddOutputPortWorld.Y - AppsCanvasMetrics.AddPortRadius,
            AppsCanvasMetrics.AddPortRadius * 2f,
            AppsCanvasMetrics.AddPortRadius * 2f);
        _appForwardZoneWorldRect = new UiRect(_appNodeX, fwdTopY, AppsCanvasMetrics.AppNodeWidth + rightPortPad, MathF.Max(32f, (y + 32f) - fwdTopY));
        y += 32f; // '+' output endpoint row

        if (showMic)
        {
            float micTop = y;
            y += 32f; // divider + label
            _appMicPortWorld = new UiPoint(rightX, y + 15f);
            _appMicZoneWorldRect = new UiRect(_appNodeX, micTop, AppsCanvasMetrics.AppNodeWidth + rightPortPad, 68f);
        }
        else
        {
            _appMicZoneWorldRect = default;
        }
    }

    private void BuildCanvasWires(UiThemePalette t, AudioAppInfo app, List<string> forwardIds, bool showMic)
    {
        _wires.Clear();
        bool outputPending = _pendingOutputs != null;
        bool appOffline = app.IsExternalOffline;

        // Native output wire (solid)
        string? nativeId = GetDisplayedNativeOutputId(app);
        if (nativeId != null && _deviceNodes.TryGetValue(DeviceNodeKey(Flow.Render, nativeId), out var nativeNode))
        {
            _wires.Add(new CanvasWire
            {
                Key = nativeNode.NodeKey,
                Kind = CanvasWireKind.NativeOutput,
                DeviceId = nativeNode.DeviceId,
                X0 = _appNativePortWorld.X,
                Y0 = _appNativePortWorld.Y,
                X1 = nativeNode.PortCenter.X,
                Y1 = nativeNode.PortCenter.Y,
                Color = nativeNode.WireColor,
                Dashed = false,
                Pending = outputPending,
                Unavailable = appOffline || !nativeNode.IsAvailable,
            });
        }

        // Capture forwarding wires (dashed)
        for (int i = 0; i < forwardIds.Count; i++)
        {
            string fid = forwardIds[i];
            if (!_deviceNodes.TryGetValue(DeviceNodeKey(Flow.Render, fid), out var devNode)) continue;
            if (!_appForwardPortsWorld.TryGetValue(fid, out var port)) continue;
            var gain = _mirrorGains.GetValueOrDefault(fid, new MirrorGainState(100, false));
            _wires.Add(new CanvasWire
            {
                Key = devNode.NodeKey,
                Kind = CanvasWireKind.CaptureForward,
                DeviceId = fid,
                X0 = port.X,
                Y0 = port.Y,
                X1 = devNode.PortCenter.X,
                Y1 = devNode.PortCenter.Y,
                Color = devNode.WireColor,
                Dashed = true,
                Pending = outputPending,
                Unavailable = appOffline || !devNode.IsAvailable || gain.Muted,
            });
        }

        // Microphone input wire (solid emerald, direction Mic -> App)
        if (showMic && !app.IsExternal && _deviceNodes.TryGetValue(DeviceNodeKey(Flow.Capture, DisplayedInput), out var micNode))
        {
            _wires.Add(new CanvasWire
            {
                Key = micNode.NodeKey,
                Kind = CanvasWireKind.MicrophoneInput,
                DeviceId = micNode.DeviceId,
                X0 = _appMicPortWorld.X,
                Y0 = _appMicPortWorld.Y,
                X1 = micNode.PortCenter.X,
                Y1 = micNode.PortCenter.Y,
                Color = AppsCanvasPalette.MicWireColor(t),
                Dashed = false,
                Pending = _pendingInput != null,
                Unavailable = !micNode.IsAvailable || micNode.Muted,
            });
        }
    }

    private void RenderWireLayer(D2dTarget g, UiThemePalette t)
    {
        _wireCache.BeginFrame();
        for (int i = 0; i < _wires.Count; i++)
        {
            var wire = _wires[i];
            nint geom = _wireCache.GetOrUpdate(wire);
            bool hot = _hoveredWireKey == wire.Key;
            float alpha = wire.Pending ? 0.48f : wire.Unavailable ? 0.36f : hot ? 1f : 0.88f;
            float width = (wire.Kind == CanvasWireKind.NativeOutput ? 2.4f : 2.15f) + (hot ? 0.8f : 0f);
            UiColor col = wire.Color.WithAlphaFraction(alpha);

            g.DrawGeometry(geom, col, width, wire.Dashed);
            CanvasWireGeometryCache.DrawDirectionChevron(
                g, wire.X0, wire.Y0, wire.X1, wire.Y1,
                reverseToLeft: wire.Kind == CanvasWireKind.MicrophoneInput,
                col, strokeWidth: hot ? 2.2f : 1.85f);
        }
        _wireCache.EndFramePrune();
    }

    private void RenderDraggingWirePreview(D2dTarget g, UiThemePalette t)
    {
        float x0, y0, x1, y1;
        bool dashed = _wireDragDashed;
        if (_wireOriginKind is WireDragOriginKind.RenderDevicePort or WireDragOriginKind.CaptureDevicePort)
        {
            if (_wireCursorWorld.X <= _wireOriginWorld.X)
            {
                x0 = _wireCursorWorld.X; y0 = _wireCursorWorld.Y;
                x1 = _wireOriginWorld.X; y1 = _wireOriginWorld.Y;
            }
            else
            {
                x0 = _wireOriginWorld.X; y0 = _wireOriginWorld.Y;
                x1 = _wireCursorWorld.X; y1 = _wireCursorWorld.Y;
            }

            if (_wireDropInCanvas && _wireOriginKind == WireDragOriginKind.RenderDevicePort)
            {
                var dropKind = ResolveRenderDeviceDropTarget(_wireCursorWorld.X, _wireCursorWorld.Y, out _);
                if (dropKind is RenderDeviceDropKind.ForwardRow or RenderDeviceDropKind.AddOutput)
                    dashed = true;
                else if (dropKind == RenderDeviceDropKind.Native)
                    dashed = false;
            }
        }
        else
        {
            x0 = _wireOriginWorld.X; y0 = _wireOriginWorld.Y;
            x1 = _wireCursorWorld.X; y1 = _wireCursorWorld.Y;
        }
        CanvasWireGeometryCache.ComputeControlPoints(x0, y0, x1, y1, out float cx1, out float cy1, out float cx2, out float cy2);
        g.DrawCubicBezier(x0, y0, cx1, cy1, cx2, cy2, x1, y1, _wireDragColor.WithAlphaFraction(0.85f), 2.3f, dashed);
        g.FillEllipse(_wireCursorWorld.X, _wireCursorWorld.Y, 4.5f, 4.5f, _wireDragColor);
    }

    private void RenderAppNode(D2dTarget g, UiThemePalette t, UiHitMap hit, AudioAppInfo app, List<string> forwardIds, bool showMic)
    {
        var card = new UiRect(_appNodeX, _appNodeY, AppsCanvasMetrics.AppNodeWidth, _appNodeHeight);
        string? procName = app.ProcessName;
        bool hasProc = !string.IsNullOrWhiteSpace(procName);
        bool outputReady = _outputChoicesIdentity == app.Identity && !app.IsExternalOffline;
        float appPortOuter = GetOuterPortHitReach();
        float appPortRy = GetAppPortHitHalfY();
        const float appPortInner = 12f; // Never overlaps forward-row '×' button (card.Right - 16) or volume percentage text

        // Determine exact live drop target when dragging a wire from a device port onto the App Node
        RenderDeviceDropKind renderDropTarget = RenderDeviceDropKind.None;
        string? renderDropFwdId = null;
        bool micDropHot = false;
        if (_interaction == InteractionMode.DraggingWire && _dragMoved)
        {
            if (_wireDropInCanvas && _wireOriginKind == WireDragOriginKind.RenderDevicePort)
                renderDropTarget = ResolveRenderDeviceDropTarget(_wireCursorWorld.X, _wireCursorWorld.Y, out renderDropFwdId);
            else if (_wireDropInCanvas && _wireOriginKind == WireDragOriginKind.CaptureDevicePort)
                micDropHot = IsCaptureDeviceDropOnApp(_wireCursorWorld.X, _wireCursorWorld.Y);
        }

        // Subtle clean elevation shadow
        g.FillRoundedRect(card.Offset(0f, 3f), AppsCanvasMetrics.NodeCornerRadius, new UiColor(0, 0, 0, t.IsDark ? (byte)55 : (byte)18));
        g.FillRoundedRect(card, AppsCanvasMetrics.NodeCornerRadius, t.SurfaceBg);

        bool wireHoverApp = renderDropTarget != RenderDeviceDropKind.None || micDropHot;
        UiColor borderCol = wireHoverApp || (_interaction == InteractionMode.DraggingNode && _dragNodeKey == null)
            ? t.Accent : t.Border;
        g.DrawRoundedRect(card, AppsCanvasMetrics.NodeCornerRadius, borderCol, wireHoverApp ? 1.6f : 1.1f);

        // Register node drag body FIRST so inner interactive controls take priority in UiHitMap
        hit.Add("canvas.node.app", card, UiCursorKind.Arrow);

        float pad = 12f;
        float x = card.X + pad;
        float w = card.Width - pad * 2f;
        float y = card.Y + 12f;

        // --- 1. App Header ---
        var iconTile = new UiRect(x, y, 40f, 40f);
        g.FillRoundedRect(iconTile, 9f, t.SurfaceAlt);
        g.DrawRoundedRect(iconTile, 9f, t.Border, 1f);
        DrawAppIcon(g, t, app, iconTile.Deflate(6f));
        if (hasProc) hit.Add("canvas.app.icon", iconTile, UiCursorKind.Hand, L10n.T("AppIcons.Hint"));

        var moreBtn = new UiRect(card.Right - pad - 26f, y, 26f, 26f);
        _appMoreWorldRect = moreBtn;
        UiPrimitives.DrawGhostIconButton(g, t, hit, "canvas.app.more", moreBtn, UiIcon.More, 15f, tooltip: L10n.T("Auto.More"));
        _clicks["canvas.app.more"] = () => ShowAppContextMenu(app, WorldToScreen(_appMoreWorldRect));

        float titleX = iconTile.Right + 10f;
        float titleMaxW = MathF.Max(40f, moreBtn.X - titleX - 28f);
        string titleText = GetAppDisplayLabel(app);
        float measuredW = MathF.Min(titleMaxW, D2dSharedContext.Instance.MeasureText(titleText, UiMetrics.FontFamily, 14.5f, 600, titleMaxW, 24f, false).Width + 4f);
        _appRenameWorldRect = new UiRect(titleX, y, MathF.Max(80f, MathF.Min(titleMaxW + 20f, moreBtn.X - titleX - 6f)), 24f);

        if (_actions.ActiveInlineEditTag == "apps.rename")
        {
            UiPrimitives.DrawTextBox(g, t, hit, "apps.rename", _appRenameWorldRect, titleText, focused: true, fontSize: 13f);
        }
        else
        {
            var titleBox = new UiRect(titleX, y, titleMaxW, 22f);
            g.DrawText(titleText, titleBox, t.TextPrimary, 14.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            var editBtn = new UiRect(titleX + measuredW + 2f, y - 1f, 22f, 22f);
            UiPrimitives.DrawGhostIconButton(g, t, hit, "apps.rename", editBtn, UiIcon.Edit, 12.5f, enabled: hasProc, tooltip: L10n.T("Apps.RenameHint"));
            if (hasProc) _clicks["apps.rename"] = BeginInlineRename;
        }

        // PID / process / session status line (keep compact without awkward truncation; full status in tooltip)
        string pidPart = app.IsExternal
            ? (app.SourceLabel ?? "")
            : $"PID {(int)app.ProcessId} · {app.ProcessName ?? "?"}";
        string statusPart = app.IsExternalOffline
            ? L10n.T("Phone.Disconnected")
            : app.HasActiveSession ? L10n.T("Apps.AudioActive") : L10n.T("Apps.AudioInactive");
        UiColor statusColor = app.IsExternalOffline ? t.TextSecondary : app.HasActiveSession ? t.Success : t.TextSecondary;
        float metaY = y + 24f;
        var metaRect = new UiRect(titleX, metaY, MathF.Max(40f, card.Right - pad - titleX), 15f);
        g.FillEllipse(titleX + 3.5f, metaY + 7.5f, 3.2f, 3.2f, statusColor);
        g.DrawText(pidPart, new UiRect(titleX + 11f, metaY, MathF.Max(40f, metaRect.Width - 11f), 15f),
            t.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        hit.Add("canvas.app.meta", metaRect, UiCursorKind.Arrow, $"{pidPart}\n{statusPart}");

        y = card.Y + 60f;
        g.DrawLine(card.X + 10f, y, card.Right - 10f, y, t.Border, 1f, roundCaps: false);
        y += 8f;

        // --- 2. Playback Output & App Master Volume (Native Output Port right-aligned to Master Volume!) ---
        string? nativeId = GetDisplayedNativeOutputId(app);
        string nativeName = nativeId != null ? DeviceLabel(nativeId, Flow.Render) : L10n.T("Ov.Unset");
        g.DrawText(L10n.T("Apps.Output"), new UiRect(x, y, w * 0.45f, 16f), t.TextSecondary, 11f, 600);
        g.DrawText(_pendingOutputs != null ? L10n.T("Apps.Canvas.SwitchingShort") : UiPrimitives.ShortDeviceName(nativeName),
            new UiRect(x + w * 0.45f, y, w * 0.55f, 16f),
            _pendingOutputs != null ? t.Accent : t.TextSecondary, 10.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
        y += 18f;

        bool volumeKnown = _selectedVolume >= 0;
        bool volReady = volumeKnown && !app.IsExternalOffline;
        int shownVol = _interaction == InteractionMode.DraggingAppVolume ? _dragVolumeVal : Math.Max(0, _selectedVolume);

        // Highlight drop zone when dragging a render device port over master volume or native port
        bool nativeDropHot = renderDropTarget == RenderDeviceDropKind.Native;
        if (nativeDropHot)
        {
            g.FillRoundedRect(new UiRect(x - 4f, y - 2f, w + 8f, 34f), 8f, t.NavSelected);
        }

        var muteBtn = new UiRect(x, y + 3f, 28f, 26f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "canvas.app.mute", muteBtn, "",
            enabled: volReady, radius: 6f,
            icon: _selectedMuted ? UiIcon.SpeakerMuted : UiIcon.Speaker,
            active: _selectedMuted,
            tooltip: L10n.T(_selectedMuted ? "Auto.MuteOff" : "Auto.MuteOn"));
        if (volReady) _clicks["canvas.app.mute"] = ToggleSelectedAppMute;

        var pctRect = new UiRect(x + w - 38f, y, 36f, 32f);
        g.DrawText(volumeKnown ? $"{shownVol}%" : "—", pctRect, volReady ? t.TextPrimary : t.TextSecondary,
            12f, 600, UiTextAlign.Trailing, UiVertAlign.Center, wrap: false, ellipsis: false);

        var masterSliderRect = new UiRect(muteBtn.Right + 6f, y, MathF.Max(40f, pctRect.X - muteBtn.Right - 10f), 32f);
        _sliderWorldRects["canvas.app.vol"] = masterSliderRect;
        // Section 3.4: NO peak meter bars!
        UiPrimitives.DrawVolumeSlider(g, t, hit, "canvas.app.vol", masterSliderRect,
            volumeKnown ? shownVol / 100f : 0f, 0f, _selectedMuted, showPeak: false,
            enabled: volReady, compact: true, dimUnavailable: app.IsExternalOffline);
        if (hit.AccessibilityEnabled)
            hit.DescribeVolume("canvas.app.vol", masterSliderRect, titleText, volReady, volumeKnown ? shownVol : null,
                app.Identity, (_, value) => { ApplyAppSliderVolume((int)Math.Round(value)); _actions.Host.FlushVolumeWrites(); });

        // Native output port directly to the right of App Master Volume row
        UiColor nativePortColor = nativeDropHot
            ? _wireDragColor
            : nativeId != null && _deviceNodes.TryGetValue(DeviceNodeKey(Flow.Render, nativeId), out var natDev)
                ? natDev.WireColor
                : t.Border;
        DrawPort(g, t, hit, "canvas.port.native", _appNativePortWorld, nativePortColor,
            connected: nativeId != null, dashed: false, emphasized: nativeDropHot,
            hitLeft: appPortInner, hitRight: appPortOuter, hitY: appPortRy,
            tooltip: $"{L10n.T("Apps.Canvas.NativeBadge")}: {nativeName}\n{L10n.T("Apps.Canvas.PortDragHint")}");

        y += 34f;

        // --- 3. Capture Forwarding Rows (Independent volume, mute, port per forwarded target) ---
        if (renderDropTarget == RenderDeviceDropKind.AddOutput)
        {
            g.FillRoundedRect(new UiRect(x - 4f, y - 2f, w + 8f, MathF.Max(34f, forwardIds.Count * 54f + 34f)), 8f, t.Overlay);
        }

        for (int i = 0; i < forwardIds.Count; i++)
        {
            string fid = forwardIds[i];
            var devNode = _deviceNodes.GetValueOrDefault(DeviceNodeKey(Flow.Render, fid));
            UiColor fwdColor = devNode?.WireColor ?? t.Accent;
            string fwdName = devNode?.PrimaryName ?? DeviceLabel(fid, Flow.Render);
            var gain = _mirrorGains.GetValueOrDefault(fid, new MirrorGainState(100, false));
            int fwdVol = (_interaction == InteractionMode.DraggingForwardVolume && IdCmp.Equals(_dragForwardDeviceId, fid))
                ? _dragVolumeVal : gain.VolumePercent;
            bool fwdReady = outputReady && (devNode?.IsAvailable ?? true);
            bool fwdDropHot = renderDropTarget == RenderDeviceDropKind.ForwardRow && renderDropFwdId != null && IdCmp.Equals(renderDropFwdId, fid);

            var subRow = new UiRect(x, y + 2f, w, 48f);
            g.FillRoundedRect(subRow, 8f, fwdDropHot ? t.NavSelected : t.SurfaceAlt);
            g.DrawRoundedRect(subRow, 8f, fwdDropHot ? _wireDragColor : t.Border, fwdDropHot ? 1.3f : 0.8f);

            // Top line: color dot + name + remove button
            g.FillEllipse(subRow.X + 9f, subRow.Y + 11f, 3.2f, 3.2f, fwdColor);
            var removeBtn = new UiRect(subRow.Right - 22f, subRow.Y + 3f, 18f, 18f);
            string remId = "canvas.fwd.remove:" + fid;
            UiPrimitives.DrawGhostIconButton(g, t, hit, remId, removeBtn, UiIcon.Close, 9.5f, enabled: outputReady, tooltip: L10n.T("Apps.Canvas.DisconnectOne"));
            string capturedFid = fid;
            if (outputReady) _clicks[remId] = () => DisconnectOutputDevice(app, capturedFid);

            string badgeStr = L10n.T("Apps.Canvas.ForwardBadge");
            float badgeW = MathF.Ceiling(D2dSharedContext.Instance.MeasureText(badgeStr, UiMetrics.FontFamily, 9.5f, 600, 96f, 16f, false).Width) + 16f;
            var tagR = new UiRect(removeBtn.X - 4f - badgeW, subRow.Y + 4f, badgeW, 15f);
            UiPrimitives.DrawBadge(g, t, tagR, badgeStr, t.SurfaceBg, t.TextSecondary, fontSize: 9.5f);

            g.DrawText(fwdName, new UiRect(subRow.X + 17f, subRow.Y + 3f, MathF.Max(20f, tagR.X - subRow.X - 21f), 17f),
                t.TextPrimary, 11.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);

            // Bottom line: independent forwarding mute + slider + pct (NO peak bar)
            float ctrlY = subRow.Y + 21f;
            var fMuteR = new UiRect(subRow.X + 6f, ctrlY + 2f, 24f, 22f);
            string fMuteId = "canvas.fwd.mute:" + fid;
            UiPrimitives.DrawGhostIconButton(g, t, hit, fMuteId, fMuteR,
                gain.Muted ? UiIcon.SpeakerMuted : UiIcon.Speaker, 12.5f,
                iconColor: gain.Muted ? t.Fail : t.TextSecondary, active: gain.Muted, enabled: fwdReady,
                tooltip: L10n.T(gain.Muted ? "Auto.MuteOff" : "Auto.MuteOn"));
            if (fwdReady) _clicks[fMuteId] = () => ToggleForwardMute(capturedFid);

            var fPctR = new UiRect(subRow.Right - 38f, ctrlY, 33f, 26f);
            g.DrawText($"{fwdVol}%", fPctR, fwdReady ? t.TextPrimary : t.TextSecondary, 11f, 600, UiTextAlign.Trailing, UiVertAlign.Center, wrap: false, ellipsis: false);

            var fSliderR = new UiRect(fMuteR.Right + 4f, ctrlY, MathF.Max(30f, fPctR.X - fMuteR.Right - 6f), 26f);
            string fSliderId = "canvas.fwd.vol:" + fid;
            _sliderWorldRects[fSliderId] = fSliderR;
            UiPrimitives.DrawVolumeSlider(g, t, hit, fSliderId, fSliderR,
                fwdVol / 100f, 0f, gain.Muted, showPeak: false, enabled: fwdReady, compact: true);
            if (hit.AccessibilityEnabled)
                hit.DescribeVolume(fSliderId, fSliderR, fwdName, fwdReady, fwdVol, app.Identity,
                    (_, value) => ApplyForwardVolume(capturedFid, (int)Math.Round(value)));

            // Forwarding port on right edge of App Node
            if (_appForwardPortsWorld.TryGetValue(fid, out var fwdPort))
            {
                DrawPort(g, t, hit, "canvas.port.fwd:" + fid, fwdPort, fwdDropHot ? _wireDragColor : fwdColor,
                    connected: true, dashed: true, emphasized: fwdDropHot,
                    hitLeft: appPortInner, hitRight: appPortOuter, hitY: appPortRy,
                    tooltip: $"{L10n.T("Apps.Canvas.ForwardBadge")}: {fwdName}\n{L10n.T("Apps.Canvas.PortDragHint")}");
            }

            y += 54f;
        }

        // --- 4. Right-Edge Draggable '+' Output Endpoint (vertically aligned with native & forwarding ports) ---
        bool plusOpen = _outputPopup is { IsDisposed: false, IsVisible: true };
        bool plusDragging = _interaction == InteractionMode.DraggingWire && _wireOriginKind == WireDragOriginKind.AddOutputPort;
        bool plusDropHot = renderDropTarget == RenderDeviceDropKind.AddOutput;
        float plusCenterY = _appAddOutputPortWorld.Y;
        if (outputReady)
        {
            // Cover both the right-edge circular '+' port and its subtle adjacent right-aligned trigger label
            var labelHit = new UiRect(card.Right - 96f, y + 3f, 82f, 26f);
            hit.Add("canvas.port.addOutput", labelHit, UiCursorKind.Hand, L10n.T("Apps.Canvas.AddOutputHint"));
            hit.Add("canvas.port.addOutput",
                new UiRect(_appAddOutputPortWorld.X - 14f, plusCenterY - appPortRy, 14f + appPortOuter, appPortRy * 2f),
                UiCursorKind.Hand, L10n.T("Apps.Canvas.AddOutputHint"));
        }
        bool plusHot = outputReady && (plusOpen || plusDragging || plusDropHot || hit.IsHover("canvas.port.addOutput"));
        UiColor addLabelCol = !outputReady ? t.TextSecondary.WithAlphaFraction(0.35f)
            : plusHot ? t.Accent : t.TextSecondary.WithAlphaFraction(0.75f);
        g.DrawText(L10n.T("Apps.Canvas.AddOutputShort"),
            new UiRect(x, y + 5f, w - 10f, 22f),
            addLabelCol, 10.5f, plusHot ? 600 : 400, UiTextAlign.Trailing, UiVertAlign.Center);

        float pr = plusHot ? AppsCanvasMetrics.AddPortRadius + 1f : AppsCanvasMetrics.AddPortRadius;
        UiColor plusBg = !outputReady ? t.SurfaceAlt.WithAlphaFraction(0.55f)
            : plusOpen || plusDragging || plusDropHot ? t.NavSelected : plusHot ? t.NavHover : t.SurfaceAlt;
        UiColor plusBorder = !outputReady ? t.Border.WithAlphaFraction(0.5f)
            : plusHot ? t.Accent : t.Border;
        UiColor plusFg = !outputReady ? t.TextSecondary.WithAlphaFraction(0.45f)
            : plusHot ? t.Accent : t.TextPrimary;
        // Clean halo separating the circular '+' endpoint from the card right border
        g.FillEllipse(_appAddOutputPortWorld.X, plusCenterY, pr + 2.2f, pr + 2.2f, t.SurfaceBg);
        g.FillEllipse(_appAddOutputPortWorld.X, plusCenterY, pr, pr, plusBg);
        g.DrawEllipse(_appAddOutputPortWorld.X, plusCenterY, pr - 0.5f, pr - 0.5f, plusBorder, plusHot ? 1.4f : 1.15f);
        var plusGlyph = new UiRect(_appAddOutputPortWorld.X - 4.5f, plusCenterY - 4.5f, 9f, 9f);
        g.DrawIcon(UiIcon.Plus, plusGlyph, plusFg, 1.85f);

        y += 32f;

        // --- 5. Optional Microphone Input Section (only when ExperimentalMic is enabled) ---
        if (showMic)
        {
            g.DrawLine(card.X + 10f, y + 3f, card.Right - 10f, y + 3f, t.Border, 1f, roundCaps: false);
            y += 11f;

            bool micEnabled = !app.IsExternal;
            UiColor micColor = AppsCanvasPalette.MicWireColor(t);
            g.DrawIcon(UiIcon.Microphone, new UiRect(x, y + 2f, 12f, 12f), micEnabled ? micColor : t.TextSecondary, 1.7f);
            g.DrawText(L10n.T("Apps.Input"), new UiRect(x + 16f, y, w - 16f, 16f), t.TextSecondary, 11f, 600);
            y += 20f;

            _appMicComboWorldRect = new UiRect(x, y, w, 30f);
            string micLabel = micEnabled ? DeviceLabel(DisplayedInput, Flow.Capture) : L10n.T("Ov.NoMicDevice");
            UiPrimitives.DrawComboBox(g, t, hit, "canvas.app.micCombo", _appMicComboWorldRect, micLabel,
                open: micDropHot, fontSize: 12f, enabled: micEnabled);
            if (micEnabled) _clicks["canvas.app.micCombo"] = OpenMicCombo;

            if (micEnabled)
            {
                DrawPort(g, t, hit, "canvas.port.mic", _appMicPortWorld, micColor,
                    connected: true, dashed: false, emphasized: micDropHot,
                    hitLeft: appPortInner, hitRight: appPortOuter, hitY: appPortRy,
                    tooltip: $"{L10n.T("Apps.Input")}: {micLabel}\n{L10n.T("Apps.Canvas.MicPortDragHint")}");
            }
            y += 37f;
        }

        // --- 6. Quick Controls (directly visible inside App Node) ---
        g.DrawLine(card.X + 10f, y + 2f, card.Right - 10f, y + 2f, t.Border, 1f, roundCaps: false);
        y += 10f;

        bool disableAuto = hasProc && _actions.Config.DisabledAutoSwitchApps.Contains(procName!, IdCmp);
        var sw1Row = new UiRect(x, y, w, 26f);
        if (hasProc) hit.Add("apps.ctrl.disableAuto", sw1Row, UiCursorKind.Hand);
        g.DrawText(L10n.T("Apps.DisableAuto"), new UiRect(sw1Row.X, sw1Row.Y, sw1Row.Width - 42f, sw1Row.Height),
            hasProc ? t.TextPrimary : t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSwitch(g, t, hit, "apps.ctrl.disableAuto", new UiRect(sw1Row.Right - 36f, sw1Row.CenterY - 12f, 34f, 24f), disableAuto, hasProc);
        if (hasProc) _clicks["apps.ctrl.disableAuto"] = ToggleDisableAutoSwitch;

        y += 28f;

        bool showInPanel = !hasProc || !_actions.Config.HiddenPanelApps.Contains(procName!, IdCmp);
        var sw2Row = new UiRect(x, y, w, 26f);
        if (hasProc) hit.Add("apps.ctrl.showPanel", sw2Row, UiCursorKind.Hand);
        g.DrawText(L10n.T("Apps.PanelVisible"), new UiRect(sw2Row.X, sw2Row.Y, sw2Row.Width - 42f, sw2Row.Height),
            hasProc ? t.TextPrimary : t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        UiPrimitives.DrawSwitch(g, t, hit, "apps.ctrl.showPanel", new UiRect(sw2Row.Right - 36f, sw2Row.CenterY - 12f, 34f, 24f), showInPanel, hasProc);
        if (hasProc) _clicks["apps.ctrl.showPanel"] = ToggleShowInQuickPanel;
    }

    private void RenderDeviceNode(D2dTarget g, UiThemePalette t, UiHitMap hit, CanvasDeviceNode node, bool connected, bool isNative)
    {
        var card = node.Bounds;
        bool draggingThis = _interaction == InteractionMode.DraggingNode && IdCmp.Equals(_dragNodeKey, node.NodeKey);
        bool wireCompatible = _interaction == InteractionMode.DraggingWire && _dragMoved && IsWireCompatibleWithDevice(node);
        bool wireTargetHot = _wireDropInCanvas && wireCompatible && GetDeviceDropRect(node).Contains(_wireCursorWorld.X, _wireCursorWorld.Y);

        g.FillRoundedRect(card.Offset(0f, 2.5f), AppsCanvasMetrics.NodeCornerRadius, new UiColor(0, 0, 0, t.IsDark ? (byte)45 : (byte)15));
        g.FillRoundedRect(card, AppsCanvasMetrics.NodeCornerRadius, t.SurfaceBg);

        UiColor borderCol = wireTargetHot || draggingThis
            ? node.WireColor
            : connected
                ? node.WireColor.WithAlphaFraction(t.IsDark ? 0.65f : 0.72f)
                : t.Border;
        g.DrawRoundedRect(card, AppsCanvasMetrics.NodeCornerRadius, borderCol, wireTargetHot || draggingThis ? 1.6f : (connected ? 1.25f : 1f));

        // Node body hit rect FIRST
        hit.Add("canvas.node.dev:" + node.NodeKey, card, UiCursorKind.Arrow);

        float pad = 10f;
        float x = card.X + pad + 4f; // slight left clearance for connection port
        float y = card.Y + 10f;

        // Device Icon Tile
        var iconTile = new UiRect(x, y + 2f, 30f, 30f);
        g.FillRoundedRect(iconTile, 8f, connected ? node.WireColor.WithAlphaFraction(t.IsDark ? 0.20f : 0.14f) : t.SurfaceAlt);
        g.DrawIcon(node.Icon, iconTile.Deflate(6.5f), connected ? node.WireColor : t.TextSecondary, 1.7f);

        // More button
        var moreBtn = new UiRect(card.Right - pad - 24f, y, 24f, 24f);
        string moreId = "canvas.dev.more:" + node.NodeKey;
        UiPrimitives.DrawGhostIconButton(g, t, hit, moreId, moreBtn, UiIcon.More, 14f, tooltip: L10n.T("Auto.More"));
        _clicks[moreId] = () => ShowDeviceMoreMenu(node, WorldToScreen(moreBtn));

        // Optional default badge
        float rightLimit = moreBtn.X - 4f;
        if (node.IsActualWindowsDefault)
        {
            string defTxt = L10n.T("Qp.DefaultOutputBadge");
            float defW = MathF.Ceiling(D2dSharedContext.Instance.MeasureText(defTxt, UiMetrics.FontFamily, 9.5f, 600, 96f, 16f, false).Width) + 16f;
            var defR = new UiRect(rightLimit - defW, y + 4f, defW, 16f);
            UiPrimitives.DrawBadge(g, t, defR, defTxt, t.NavSelected, t.Accent, fontSize: 9.5f);
            rightLimit = defR.X - 4f;
        }

        // Primary & Secondary Name
        float textX = iconTile.Right + 8f;
        float nameW = MathF.Max(20f, rightLimit - textX);
        float subW = MathF.Max(20f, moreBtn.X - 4f - textX);
        string renameTag = "canvas.dev.rename:" + node.NodeKey;
        if (_actions.ActiveInlineEditTag == renameTag)
        {
            UiPrimitives.DrawTextBox(g, t, hit, renameTag, new UiRect(textX, y, MathF.Max(40f, moreBtn.X - 4f - textX), 24f),
                node.PrimaryName, focused: true, fontSize: 12f);
        }
        else
        {
            var titleRect = new UiRect(textX, y, nameW, 18f);
            g.DrawText(node.PrimaryName, titleRect, node.IsAvailable ? t.TextPrimary : t.TextSecondary,
                12.5f, connected ? 600 : 400, UiTextAlign.Leading, UiVertAlign.Center);
            hit.Add("canvas.dev.tip:" + node.NodeKey, new UiRect(textX, y, subW, 34f), UiCursorKind.Arrow, node.FullTooltip);
        }

        if (!string.IsNullOrEmpty(node.SecondaryName))
        {
            g.DrawText(node.SecondaryName, new UiRect(textX, y + 18f, subW, 15f),
                t.TextSecondary, 10.25f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }

        // System Volume Row (real Windows endpoints only; remote phone outputs show concise remote status)
        float volY = card.Y + 48f;
        if (node.IsPhoneOutput)
        {
            g.DrawText(L10n.T("Apps.Canvas.RemoteDeviceHint"), new UiRect(x, volY - 4f, card.Width - pad * 2f - 4f, 16f),
                t.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
        else
        {
            var muteR = new UiRect(x, volY + 4f, 26f, 24f);
            string muteId = "canvas.dev.mute:" + node.NodeKey;
            UiIcon spkIcon = node.Flow == Flow.Capture
                ? (node.Muted ? UiIcon.MicMuted : UiIcon.Microphone)
                : (node.Muted ? UiIcon.SpeakerMuted : UiIcon.Speaker);
            UiPrimitives.DrawGhostIconButton(g, t, hit, muteId, muteR, spkIcon, 13.5f,
                iconColor: node.Muted ? t.Fail : t.TextSecondary, active: node.Muted, enabled: node.IsAvailable,
                tooltip: L10n.T(node.Muted ? "Auto.MuteOff" : "Auto.MuteOn"));
            if (node.IsAvailable) _clicks[muteId] = () => ToggleDeviceMute(node);

            var pctR = new UiRect(card.Right - pad - 36f, volY, 36f, 32f);
            int shownVol = (_interaction == InteractionMode.DraggingDeviceVolume && IdCmp.Equals(_dragDeviceNodeKey, node.NodeKey))
                ? _dragVolumeVal : node.Volume;
            g.DrawText(node.IsAvailable ? $"{shownVol}%" : "—", pctR,
                node.IsAvailable ? t.TextPrimary : t.TextSecondary, 11.5f, 600, UiTextAlign.Trailing, UiVertAlign.Center, wrap: false, ellipsis: false);

            var sliderR = new UiRect(muteR.Right + 4f, volY, MathF.Max(30f, pctR.X - muteR.Right - 6f), 32f);
            string sliderId = "canvas.dev.vol:" + node.NodeKey;
            _sliderWorldRects[sliderId] = sliderR;
            // Section 3.4: NO peak meter bars!
            UiPrimitives.DrawVolumeSlider(g, t, hit, sliderId, sliderR,
                node.IsAvailable ? shownVol / 100f : 0f, 0f, node.Muted, showPeak: false,
                enabled: node.IsAvailable, compact: true, dimUnavailable: !node.IsAvailable);
            if (hit.AccessibilityEnabled)
                hit.DescribeVolume(sliderId, sliderR, node.PrimaryName, node.IsAvailable, node.IsAvailable ? shownVol : null,
                    node.DeviceId, (_, value) => { ApplyDeviceSliderVolume(node, (int)Math.Round(value)); _actions.Host.FlushVolumeWrites(); });
        }

        // Left-edge connection port: extend generously to the left at low zoom, cap right inside card at 12u so mute button (card.X + 14) is never overlapped
        float devPortOuter = GetOuterPortHitReach();
        float devPortY = MathF.Min(20f, devPortOuter);
        DrawPort(g, t, hit, "canvas.port.dev:" + node.NodeKey, node.PortCenter, node.WireColor,
            connected: connected, dashed: connected && !isNative && node.Flow == Flow.Render,
            emphasized: wireTargetHot,
            hitLeft: devPortOuter, hitRight: 12f, hitY: devPortY,
            tooltip: connected
                ? $"{node.PrimaryName}\n{L10n.T("Apps.Canvas.DevicePortConnectedHint")}"
                : $"{node.PrimaryName}\n{L10n.T("Apps.Canvas.DevicePortConnectHint")}");
    }

    private static void DrawPort(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string id, UiPoint center, UiColor color,
        bool connected, bool dashed, bool emphasized = false,
        float hitLeft = AppsCanvasMetrics.PortHitRadius,
        float hitRight = AppsCanvasMetrics.PortHitRadius,
        float hitY = AppsCanvasMetrics.PortHitRadius,
        string? tooltip = null)
    {
        hit.Add(id, new UiRect(center.X - hitLeft, center.Y - hitY, hitLeft + hitRight, hitY * 2f), UiCursorKind.Hand, tooltip);
        bool hot = emphasized || hit.IsHover(id);
        float r = hot ? AppsCanvasMetrics.PortRadius + 1.5f : AppsCanvasMetrics.PortRadius;

        // Outer halo for clean separation from node border
        g.FillEllipse(center.X, center.Y, r + 2f, r + 2f, t.SurfaceBg);

        if (connected || hot)
        {
            g.FillEllipse(center.X, center.Y, r, r, color);
            if (dashed)
                g.FillEllipse(center.X, center.Y, r * 0.45f, r * 0.45f, t.SurfaceBg);
            else
                g.FillEllipse(center.X, center.Y, r * 0.36f, r * 0.36f, t.OnAccent);
        }
        else
        {
            g.FillEllipse(center.X, center.Y, r - 0.5f, r - 0.5f, t.SurfaceAlt);
            g.DrawEllipse(center.X, center.Y, r - 0.5f, r - 0.5f, t.Border, 1.4f);
        }
    }

    private void RenderCanvasToolbar(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect clip)
    {
        string zoomText = $"{(int)MathF.Round(_zoom * 100f)}%";
        const float btnS = 26f;
        const float zoomW = 42f;
        float barW = 6f + btnS * 3f + 4f + 9f + btnS + zoomW + btnS + 6f;
        float barH = 32f;
        var bar = _canvasToolbarRect = new UiRect(clip.Right - 12f - barW, clip.Y + 10f, barW, barH);

        g.FillRoundedRect(bar.Offset(0f, 1.5f), 9f, new UiColor(0, 0, 0, t.IsDark ? (byte)40 : (byte)14));
        g.FillRoundedRect(bar, 9f, t.SurfaceBg.WithAlphaFraction(0.95f));
        g.DrawRoundedRect(bar, 9f, t.Border, 1f);

        float bx = bar.X + 4f;
        float by = bar.Y + 3f;

        UiPrimitives.DrawGhostIconButton(g, t, hit, "canvas.tool.arrange", new UiRect(bx, by, btnS, btnS),
            UiIcon.AutoArrange, 14f, tooltip: L10n.T("Apps.Canvas.AutoArrange"));
        _clicks["canvas.tool.arrange"] = () =>
        {
            ArrangeNodesDefault(fitViewport: true);
            ScheduleCanvasLayoutSave();
            _actions.Invalidate();
        };
        bx += btnS + 2f;

        UiPrimitives.DrawGhostIconButton(g, t, hit, "canvas.tool.fit", new UiRect(bx, by, btnS, btnS),
            UiIcon.FitCanvas, 13.5f, tooltip: L10n.T("Apps.Canvas.FitCanvas"));
        _clicks["canvas.tool.fit"] = () =>
        {
            FitCanvasToViewport();
            ScheduleCanvasLayoutSave();
            _actions.Invalidate();
        };
        bx += btnS + 2f;

        UiPrimitives.DrawGhostIconButton(g, t, hit, "canvas.tool.reset", new UiRect(bx, by, btnS, btnS),
            UiIcon.Reset, 13.5f, tooltip: L10n.T("Apps.Canvas.ResetLayout"));
        _clicks["canvas.tool.reset"] = ResetCurrentAppCanvasLayout;
        bx += btnS + 5f;

        g.DrawLine(bx, bar.Y + 7f, bx, bar.Bottom - 7f, t.Border, 1f, roundCaps: false);
        bx += 5f;

        UiPrimitives.DrawGhostIconButton(g, t, hit, "canvas.tool.zoomOut", new UiRect(bx, by, btnS, btnS),
            UiIcon.Minus, 12f, enabled: _zoom > AppsCanvasMetrics.MinZoom + 0.01f, tooltip: L10n.T("Apps.Canvas.ZoomOut"));
        _clicks["canvas.tool.zoomOut"] = () => ZoomAtPoint(_canvasRect.CenterX, _canvasRect.CenterY, _zoom / 1.15f);
        bx += btnS;

        var zoomRect = new UiRect(bx, by, zoomW, btnS);
        hit.Add("canvas.tool.zoomReset", zoomRect, UiCursorKind.Hand, L10n.T("Apps.Canvas.ZoomReset"));
        if (hit.IsHover("canvas.tool.zoomReset")) g.FillRoundedRect(zoomRect, 6f, t.NavHover);
        g.DrawText(zoomText, zoomRect, t.TextPrimary, 11f, 600, UiTextAlign.Center, UiVertAlign.Center);
        _clicks["canvas.tool.zoomReset"] = () => ZoomAtPoint(_canvasRect.CenterX, _canvasRect.CenterY, 1f);
        bx += zoomW;

        UiPrimitives.DrawGhostIconButton(g, t, hit, "canvas.tool.zoomIn", new UiRect(bx, by, btnS, btnS),
            UiIcon.Plus, 12f, enabled: _zoom < AppsCanvasMetrics.MaxZoom - 0.01f, tooltip: L10n.T("Apps.Canvas.ZoomIn"));
        _clicks["canvas.tool.zoomIn"] = () => ZoomAtPoint(_canvasRect.CenterX, _canvasRect.CenterY, _zoom * 1.15f);
    }

    private void ZoomAtPoint(float screenX, float screenY, float targetZoom)
    {
        float next = Math.Clamp(targetZoom, AppsCanvasMetrics.MinZoom, AppsCanvasMetrics.MaxZoom);
        if (MathF.Abs(next - _zoom) < 0.001f) return;
        float worldX = ScreenToWorldX(screenX);
        float worldY = ScreenToWorldY(screenY);
        _zoom = next;
        _panX = screenX - _canvasRect.X - worldX * _zoom;
        _panY = screenY - _canvasRect.Y - worldY * _zoom;
        _actions.CloseInlineEdit(commit: true);
        ScheduleCanvasLayoutSave();
        _actions.Invalidate();
    }

    // --- Output Connection Continuous-Click Popup (Section 3.5) ---

    private void ToggleOutputConnectionPopup()
    {
        if (_outputPopup is { IsDisposed: false, IsVisible: true })
        {
            ClosePopups();
            _actions.Invalidate();
            return;
        }
        var app = _selectedApp;
        if (app == null || _outputChoicesIdentity != app.Identity || app.IsExternalOffline) return;

        var (connectable, disconnectable) = BuildOutputPopupGroups(app);
        var anchor = WorldToScreen(_appAddOutputWorldRect);
        _outputPopup = OutputConnectionPopupWindow.ShowFor(
            _actions.Theme,
            _actions.OwnerWindow,
            anchor,
            connectable,
            disconnectable,
            onConnect: deviceId =>
            {
                if (_selectedApp is { } current && current.Identity == app.Identity)
                    ConnectOutputDevice(current, deviceId);
            },
            onDisconnect: deviceId =>
            {
                if (_selectedApp is { } current && current.Identity == app.Identity)
                    DisconnectOutputDevice(current, deviceId);
            });
        _outputPopup.Closed += () =>
        {
            _outputPopup = null;
            if (_active) _actions.Invalidate();
        };
        _actions.Invalidate();
    }

    private void SyncOutputPopupIfOpen()
    {
        if (_outputPopup is not { IsDisposed: false, IsVisible: true } popup) return;
        var app = _selectedApp;
        if (app == null || _outputChoicesIdentity != app.Identity || app.IsExternalOffline)
        {
            ClosePopups();
            return;
        }
        var (connectable, disconnectable) = BuildOutputPopupGroups(app);
        popup.RefreshItems(connectable, disconnectable);
    }

    private (List<OutputConnectionItem> Connectable, List<OutputConnectionItem> Disconnectable) BuildOutputPopupGroups(AudioAppInfo app)
    {
        var connectable = new List<OutputConnectionItem>();
        var disconnectable = new List<OutputConnectionItem>();
        var activeOutputs = DisplayedOutputs;
        string? nativeId = GetDisplayedNativeOutputId(app);
        bool pending = _pendingOutputs != null;

        foreach (var node in _orderedRenderNodes)
        {
            bool connected = activeOutputs.Contains(node.DeviceId, IdCmp);
            bool isNative = connected && nativeId != null && IdCmp.Equals(node.DeviceId, nativeId);
            bool canDisconnect = connected && (activeOutputs.Length > 1 || !AudioService.IsSystemDefault(node.DeviceId));
            var item = new OutputConnectionItem(
                DeviceId: node.DeviceId,
                PrimaryName: node.PrimaryName,
                SecondaryName: node.SecondaryName,
                FullTooltip: node.FullTooltip,
                Icon: node.Icon,
                AccentColor: node.WireColor,
                Connected: connected,
                IsNative: isNative,
                CanDisconnect: canDisconnect,
                IsPending: pending);

            if (!connected) connectable.Add(item);
            else if (canDisconnect) disconnectable.Add(item);
        }
        return (connectable, disconnectable);
    }

    private void OpenMicCombo()
    {
        if (_selectedApp == null || _selectedApp.IsExternal || !_actions.Config.ExperimentalMic || _outputChoicesIdentity != _selectedIdentity) return;
        var choices = _orderedCaptureNodes.Select(n => new ComboChoice(n.DeviceId, n.PrimaryName, n.Icon)).ToList();
        if (choices.Count == 0) return;
        _actions.ShowCombo(WorldToScreen(_appMicComboWorldRect), choices, DisplayedInput, picked => SelectInputRoute(picked.Key));
    }

    private void ShowDeviceMoreMenu(CanvasDeviceNode node, UiRect anchorScreen)
    {
        var app = _selectedApp;
        var items = new List<UiMenuItem>();
        if (app != null && !app.IsExternalOffline)
        {
            if (node.Flow == Flow.Render)
            {
                var activeOutputs = DisplayedOutputs;
                bool connected = activeOutputs.Contains(node.DeviceId, IdCmp);
                string? nativeId = GetDisplayedNativeOutputId(app);
                bool isNative = connected && nativeId != null && IdCmp.Equals(node.DeviceId, nativeId);
                bool canDisconnect = connected && (activeOutputs.Length > 1 || !AudioService.IsSystemDefault(node.DeviceId));

                if (!isNative && (!node.IsPhoneOutput || app.IsExternal))
                {
                    items.Add(new UiMenuItem
                    {
                        Text = L10n.T("Apps.Canvas.SetAsNativeOutput"),
                        Icon = UiIcon.Link,
                        Click = () => SwitchNativeOutputDevice(app, node.DeviceId),
                    });
                }
                if (!connected && (!app.IsExternal || PlaybackSelection.IsMultiOutputExternal(_actions.Host.Audio, app)))
                {
                    items.Add(new UiMenuItem
                    {
                        Text = L10n.T("Apps.Canvas.AddCaptureForward"),
                        Icon = UiIcon.Plus,
                        Click = () => ConnectOutputDevice(app, node.DeviceId),
                    });
                }
                if (canDisconnect)
                {
                    items.Add(new UiMenuItem
                    {
                        Text = L10n.T("Apps.Canvas.DisconnectOne"),
                        Icon = UiIcon.Close,
                        Click = () => DisconnectOutputDevice(app, node.DeviceId),
                    });
                }
            }
            else if (!app.IsExternal)
            {
                bool connected = IdCmp.Equals(DisplayedInput, node.DeviceId);
                if (!connected)
                {
                    items.Add(new UiMenuItem
                    {
                        Text = L10n.T("Apps.Canvas.UseMicForApp"),
                        Icon = UiIcon.Microphone,
                        Click = () => SelectInputRoute(node.DeviceId),
                    });
                }
                else if (!AudioService.IsSystemDefault(node.DeviceId))
                {
                    items.Add(new UiMenuItem
                    {
                        Text = L10n.T("Apps.Canvas.DisconnectOne"),
                        Icon = UiIcon.Close,
                        Click = () => SelectInputRoute(AudioService.SystemDefaultInputDeviceId),
                    });
                }
            }
            if (items.Count > 0) items.Add(UiMenuItem.Separator());
        }

        if (!node.IsSystemDefault && !node.IsPhoneOutput && node.IsAvailable)
        {
            items.Add(new UiMenuItem
            {
                Text = L10n.T(node.Flow == Flow.Render ? "Auto.ActionSystemOutput" : "Auto.ActionSystemInput"),
                Icon = UiIcon.Check,
                Checked = node.IsActualWindowsDefault,
                Click = () => SetWindowsSystemDefault(node),
            });
        }

        if (!node.IsPhoneOutput)
        {
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.Rename"),
                Icon = UiIcon.Edit,
                Click = () => BeginDeviceInlineRename(node),
            });
        }

        items.Add(new UiMenuItem
        {
            Text = L10n.T("Apps.Canvas.HideDevice"),
            Icon = UiIcon.Close,
            Click = () =>
            {
                _actions.Config.SetDeviceShown(node.Flow == Flow.Capture ? EDataFlow.eCapture : EDataFlow.eRender, node.PreferenceId, false);
                _actions.CommitSettings(notify: true);
            },
        });

        items.Add(UiMenuItem.Separator());

#if SONICROUTE_PHONE
        if (node.IsPhoneOutput)
        {
            string? profileId = FindPhoneProfileIdForOutput(node.DeviceId);
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Phone.ConfigureTab"),
                Icon = UiIcon.Settings,
                Click = () => _actions.Host.OpenPhoneManagement(profileId ?? _actions.Host.Phone.Store.Profile.Id, airPlay: false),
            });
        }
        else
#endif
        {
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.Canvas.OpenDeviceSettings"),
                Icon = UiIcon.Settings,
                Click = () => _actions.NavigateTo(MainNavTab.Settings),
            });
        }

        _actions.ShowMenu(anchorScreen, items);
    }

    private void SetWindowsSystemDefault(CanvasDeviceNode node)
    {
        var flow = node.Flow;
        string id = node.DeviceId;
        _actions.Host.SubmitPanelAudio(() =>
        {
            try
            {
                _actions.Host.Audio.SetDefault(flow, id);
                _actions.Host.NotifyAudioVisualChanged();
            }
            catch
            {
                _actions.Host.TryPost(() => _actions.ShowNotice(L10n.T("St.SysDefaultSetFail")));
            }
        });
    }

#if SONICROUTE_PHONE
    private string? FindPhoneProfileIdForOutput(string deviceId)
    {
        if (!AndroidOutputIdentity.TryParseDeviceId(deviceId, out string key, out _)) return null;
        var store = _actions.Host.Phone.Store;
        foreach (var p in store.Profiles)
        {
            if (p.LastDevice != null && IdCmp.Equals(AndroidDeviceIdentity.Key(p.LastDevice), key))
                return p.Id;
        }
        return null;
    }
#endif

    // --- Input Handling & Strict Gesture Isolation ---

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (leftDown && _dockResultsScroll.DragMove(y)) return true;

        if (_interaction != InteractionMode.None && !leftDown)
        {
            CancelCanvasInteraction(commitLayout: _interaction is InteractionMode.DraggingNode or InteractionMode.PanningCanvas);
            return true;
        }

        switch (_interaction)
        {
            case InteractionMode.DraggingAppVolume:
            {
                if (_sliderWorldRects.TryGetValue("canvas.app.vol", out var sr))
                {
                    int next = UiPrimitives.VolumeSliderValueFromX(sr, ScreenToWorldX(x), compact: true);
                    if (next != _dragVolumeVal)
                    {
                        _dragVolumeVal = next;
                        ApplyAppSliderVolume(next);
                    }
                }
                return true;
            }
            case InteractionMode.DraggingForwardVolume:
            {
                if (_dragForwardDeviceId != null && _sliderWorldRects.TryGetValue("canvas.fwd.vol:" + _dragForwardDeviceId, out var sr))
                {
                    int next = UiPrimitives.VolumeSliderValueFromX(sr, ScreenToWorldX(x), compact: true);
                    if (next != _dragVolumeVal)
                    {
                        _dragVolumeVal = next;
                        ApplyForwardVolume(_dragForwardDeviceId, next);
                    }
                }
                return true;
            }
            case InteractionMode.DraggingDeviceVolume:
            {
                if (_dragDeviceNodeKey != null && _sliderWorldRects.TryGetValue("canvas.dev.vol:" + _dragDeviceNodeKey, out var sr)
                    && _deviceNodes.TryGetValue(_dragDeviceNodeKey, out var node))
                {
                    int next = UiPrimitives.VolumeSliderValueFromX(sr, ScreenToWorldX(x), compact: true);
                    if (next != _dragVolumeVal)
                    {
                        _dragVolumeVal = next;
                        ApplyDeviceSliderVolume(node, next);
                    }
                }
                return true;
            }
            case InteractionMode.DraggingNode:
            {
                float dxScreen = x - _dragStartScreenX;
                float dyScreen = y - _dragStartScreenY;
                if (!_dragMoved && (MathF.Abs(dxScreen) >= 3f || MathF.Abs(dyScreen) >= 3f))
                {
                    _dragMoved = true;
                    _actions.CloseInlineEdit(commit: true);
                }
                if (_dragMoved)
                {
                    float wx = _dragOrigX + dxScreen / _zoom;
                    float wy = _dragOrigY + dyScreen / _zoom;
                    if (_dragNodeKey == null)
                    {
                        _appNodeX = wx;
                        _appNodeY = wy;
                    }
                    else if (_deviceNodes.TryGetValue(_dragNodeKey, out var node))
                    {
                        node.X = wx;
                        node.Y = wy;
                    }
                }
                return true;
            }
            case InteractionMode.PanningCanvas:
            {
                float dx = x - _dragStartScreenX;
                float dy = y - _dragStartScreenY;
                if (!_dragMoved && (MathF.Abs(dx) >= 2f || MathF.Abs(dy) >= 2f))
                {
                    _dragMoved = true;
                    _actions.CloseInlineEdit(commit: true);
                }
                if (_dragMoved)
                {
                    _panX = _dragOrigX + dx;
                    _panY = _dragOrigY + dy;
                }
                return true;
            }
            case InteractionMode.DraggingWire:
            {
                if (!_dragMoved && HasWireDragDistance(x, y))
                {
                    _dragMoved = true;
                    if (_wireOriginKind == WireDragOriginKind.AddOutputPort)
                        ClosePopups();
                }
                _wireDropInCanvas = IsWireDropInCanvas(x, y);
                _wireCursorWorld = new UiPoint(ScreenToWorldX(x), ScreenToWorldY(y));
                return true;
            }
        }

        // Hover hit-testing for wires when cursor is over blank canvas
        string? prevWire = _hoveredWireKey;
        if (_canvasRect.Contains(x, y) && !IsDockOverlayAt(x, y) && _wires.Count > 0)
        {
            _hoveredWireKey = HitTestWire(ScreenToWorldX(x), ScreenToWorldY(y))?.Key;
        }
        else
        {
            _hoveredWireKey = null;
        }
        return prevWire != _hoveredWireKey;
    }

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        bool overTools = _dockToolsOpen && _dockToolsRect.Contains(e.X, e.Y);
        if (_dockToolsOpen && !overTools && targetId != DockToolsId) CloseDockTools();
        if (e.Button == UiMouseButton.Right)
        {
            if (_interaction != InteractionMode.None)
            {
                CancelCanvasInteraction(commitLayout: false);
                return true;
            }
            if (targetId != null && _appRows.TryGetValue(targetId, out var clickedApp))
            {
                ResetDockHover();
                if (!_multiSelected.Contains(clickedApp.Identity)) SelectSingleApp(clickedApp);
                ShowAppContextMenu(clickedApp, new UiRect(e.X, e.Y, 1f, 1f), fromAppList: true);
                return true;
            }
            if (overTools || targetId == DockToolsId) return true;
            if (targetId == "canvas.app.icon" && _selectedApp != null)
            {
                ShowAppIconContextMenu(_selectedApp);
                return true;
            }
            if (targetId is "canvas.node.app" or "canvas.app.meta" && _selectedApp != null)
            {
                ShowAppContextMenu(_selectedApp);
                return true;
            }
            if (targetId != null && (targetId.StartsWith("canvas.node.dev:", StringComparison.Ordinal) || targetId.StartsWith("canvas.dev.tip:", StringComparison.Ordinal)))
            {
                string devKey = targetId.StartsWith("canvas.node.dev:", StringComparison.Ordinal)
                    ? targetId["canvas.node.dev:".Length..]
                    : targetId["canvas.dev.tip:".Length..];
                if (_deviceNodes.TryGetValue(devKey, out var rDev))
                {
                    ShowDeviceMoreMenu(rDev, new UiRect(e.X, e.Y, 1f, 1f));
                    return true;
                }
            }
            if (_canvasRect.Contains(e.X, e.Y) && HitTestWire(ScreenToWorldX(e.X), ScreenToWorldY(e.Y)) is { } wire)
            {
                ShowWireContextMenu(wire);
                return true;
            }
            return false;
        }

        if (e.Button != UiMouseButton.Left) return false;

        if (targetId == "apps.results.scroll")
        {
            ResetDockHover();
            return _dockResultsScroll.BeginDrag(e.Y);
        }

        if (targetId != null && _appRows.TryGetValue(targetId, out var app))
        {
            if (!e.Ctrl && !e.Shift && targetId.StartsWith(DockResultPrefix, StringComparison.Ordinal))
                CloseDockTools();
            HandleAppRowClick(app, e.Ctrl, e.Shift);
            return true;
        }

        if (ResetDockHover()) _actions.Invalidate();
        if (targetId == null) return false;

        // 1. Sliders (never drag node!)
        if (targetId == "canvas.app.vol" && _selectedApp is { IsExternalOffline: false } && _selectedVolume >= 0
            && _sliderWorldRects.TryGetValue("canvas.app.vol", out var appSr))
        {
            _interaction = InteractionMode.DraggingAppVolume;
            _dragVolumeVal = UiPrimitives.VolumeSliderValueFromX(appSr, ScreenToWorldX(e.X), compact: true);
            ApplyAppSliderVolume(_dragVolumeVal);
            return true;
        }

        if (targetId.StartsWith("canvas.fwd.vol:", StringComparison.Ordinal) && _selectedApp is { IsExternalOffline: false }
            && _sliderWorldRects.TryGetValue(targetId, out var fwdSr))
        {
            string fid = targetId["canvas.fwd.vol:".Length..];
            _interaction = InteractionMode.DraggingForwardVolume;
            _dragForwardDeviceId = fid;
            _dragVolumeVal = UiPrimitives.VolumeSliderValueFromX(fwdSr, ScreenToWorldX(e.X), compact: true);
            ApplyForwardVolume(fid, _dragVolumeVal);
            return true;
        }

        if (targetId.StartsWith("canvas.dev.vol:", StringComparison.Ordinal) && _sliderWorldRects.TryGetValue(targetId, out var devSr))
        {
            string nodeKey = targetId["canvas.dev.vol:".Length..];
            if (_deviceNodes.TryGetValue(nodeKey, out var node) && !node.IsPhoneOutput && node.IsAvailable)
            {
                _interaction = InteractionMode.DraggingDeviceVolume;
                _dragDeviceNodeKey = nodeKey;
                _dragVolumeVal = UiPrimitives.VolumeSliderValueFromX(devSr, ScreenToWorldX(e.X), compact: true);
                ApplyDeviceSliderVolume(node, _dragVolumeVal);
                return true;
            }
        }

        // 2. Connection Ports (wire dragging!)
        if (targetId == "canvas.port.native" && _selectedApp is { IsExternalOffline: false } && _outputChoicesIdentity == _selectedIdentity)
        {
            BeginWireDrag(WireDragOriginKind.NativePort, null, _appNativePortWorld, _actions.Theme.Accent, dashed: false, e.X, e.Y);
            return true;
        }

        if (targetId.StartsWith("canvas.port.fwd:", StringComparison.Ordinal) && _selectedApp is { IsExternalOffline: false } && _outputChoicesIdentity == _selectedIdentity)
        {
            string fid = targetId["canvas.port.fwd:".Length..];
            if (_appForwardPortsWorld.TryGetValue(fid, out var port))
            {
                UiColor col = _deviceNodes.GetValueOrDefault(DeviceNodeKey(Flow.Render, fid))?.WireColor ?? _actions.Theme.Accent;
                BeginWireDrag(WireDragOriginKind.ForwardPort, fid, port, col, dashed: true, e.X, e.Y);
                return true;
            }
        }

        if (targetId == "canvas.port.addOutput" && _selectedApp is { IsExternalOffline: false } && _outputChoicesIdentity == _selectedIdentity)
        {
            BeginWireDrag(WireDragOriginKind.AddOutputPort, null, _appAddOutputPortWorld, _actions.Theme.Accent, dashed: true, e.X, e.Y);
            return true;
        }

        if (targetId == "canvas.port.mic" && _selectedApp is { IsExternal: false } && _actions.Config.ExperimentalMic)
        {
            BeginWireDrag(WireDragOriginKind.MicPort, null, _appMicPortWorld, AppsCanvasPalette.MicWireColor(_actions.Theme), dashed: false, e.X, e.Y);
            return true;
        }

        if (targetId.StartsWith("canvas.port.dev:", StringComparison.Ordinal) && _selectedApp is { IsExternalOffline: false })
        {
            string nodeKey = targetId["canvas.port.dev:".Length..];
            if (_deviceNodes.TryGetValue(nodeKey, out var node))
            {
                if (node.Flow == Flow.Render && _outputChoicesIdentity != _selectedIdentity) return false;
                if (node.Flow == Flow.Capture && _selectedApp.IsExternal) return false;
                bool isConnectedFwd = node.Flow == Flow.Render
                    && DisplayedOutputs.Contains(node.DeviceId, IdCmp)
                    && !IdCmp.Equals(GetDisplayedNativeOutputId(_selectedApp), node.DeviceId);
                BeginWireDrag(
                    node.Flow == Flow.Capture ? WireDragOriginKind.CaptureDevicePort : WireDragOriginKind.RenderDevicePort,
                    node.DeviceId, node.PortCenter, node.WireColor, dashed: isConnectedFwd, e.X, e.Y);
                return true;
            }
        }

        // 3. Node Body Dragging (ignore clicks on buttons/switches/combos/inputs!)
        if (targetId is "canvas.node.app" or "canvas.app.icon" or "canvas.app.meta")
        {
            _interaction = InteractionMode.DraggingNode;
            _dragNodeKey = null;
            _dragStartScreenX = e.X;
            _dragStartScreenY = e.Y;
            _dragOrigX = _appNodeX;
            _dragOrigY = _appNodeY;
            _dragMoved = false;
            return true;
        }

        if (targetId.StartsWith("canvas.node.dev:", StringComparison.Ordinal) || targetId.StartsWith("canvas.dev.tip:", StringComparison.Ordinal))
        {
            string nodeKey = targetId.StartsWith("canvas.node.dev:", StringComparison.Ordinal)
                ? targetId["canvas.node.dev:".Length..]
                : targetId["canvas.dev.tip:".Length..];
            if (_deviceNodes.TryGetValue(nodeKey, out var node))
            {
                _interaction = InteractionMode.DraggingNode;
                _dragNodeKey = nodeKey;
                _dragStartScreenX = e.X;
                _dragStartScreenY = e.Y;
                _dragOrigX = node.X;
                _dragOrigY = node.Y;
                _dragMoved = false;
                return true;
            }
        }

        // 4. Blank Canvas Panning
        if (targetId == "canvas.bg")
        {
            _interaction = InteractionMode.PanningCanvas;
            _dragStartScreenX = e.X;
            _dragStartScreenY = e.Y;
            _dragOrigX = _panX;
            _dragOrigY = _panY;
            _dragMoved = false;
            return true;
        }

        return false;
    }

    public bool OnPointerUp(UiPointerEventArgs e, string? clickId)
    {
        if (e.Button != UiMouseButton.Left) return false;

        if (_dockResultsScroll.Dragging)
        {
            _dockResultsScroll.EndDrag();
            return true;
        }

        var mode = _interaction;
        if (mode != InteractionMode.None)
        {
            switch (mode)
            {
                case InteractionMode.DraggingAppVolume:
                case InteractionMode.DraggingDeviceVolume:
                    _interaction = InteractionMode.None;
                    _dragDeviceNodeKey = null;
                    _actions.Host.FlushVolumeWrites();
                    return true;

                case InteractionMode.DraggingForwardVolume:
                    _interaction = InteractionMode.None;
                    _dragForwardDeviceId = null;
                    return true;

                case InteractionMode.DraggingNode:
                case InteractionMode.PanningCanvas:
                    bool moved = _dragMoved;
                    string? draggedKey = _dragNodeKey;
                    _interaction = InteractionMode.None;
                    _dragNodeKey = null;
                    _dragMoved = false;
                    if (moved)
                    {
                        if (mode == InteractionMode.DraggingNode)
                            CommitCanvasCardDrag(draggedKey);
                        ScheduleCanvasLayoutSave();
                        return true;
                    }
                    break;

                case InteractionMode.DraggingWire:
                    bool wireDragged = _dragMoved || HasWireDragDistance(e.X, e.Y);
                    if (IsWireDropInCanvas(e.X, e.Y))
                        CompleteWireDrag(ScreenToWorldX(e.X), ScreenToWorldY(e.Y), wireDragged);
                    _interaction = InteractionMode.None;
                    _wireOriginDeviceId = null;
                    _dragMoved = false;
                    return true;
            }
        }

        if (clickId != null && _clicks.TryGetValue(clickId, out var click))
        {
            click();
            return true;
        }

        return false;
    }

    public bool ActivateFocused(UiPointerEventArgs e, string id)
    {
        if (id == "canvas.port.addOutput")
        {
            ToggleOutputConnectionPopup();
            return true;
        }
        if (!_appRows.TryGetValue(id, out var app)) return OnPointerUp(e, id);
        if (!e.Ctrl && !e.Shift && id.StartsWith(DockResultPrefix, StringComparison.Ordinal))
            CloseDockTools();
        HandleAppRowClick(app, e.Ctrl, e.Shift);
        return true;
    }

    public bool OnFocusedKey(string id, int vk)
    {
        if (id == "canvas.app.vol" && _selectedApp is { IsExternalOffline: false } && _selectedVolume >= 0
            && ViewSliderKeys.TryChange(vk, _selectedVolume, 0, 100, 1, 10, out double nextApp))
        {
            ApplyAppSliderVolume((int)nextApp);
            _actions.Host.FlushVolumeWrites();
            return true;
        }
        if (id.StartsWith("canvas.fwd.vol:", StringComparison.Ordinal) && _selectedApp is { IsExternalOffline: false })
        {
            string fid = id["canvas.fwd.vol:".Length..];
            var cur = _mirrorGains.GetValueOrDefault(fid, new MirrorGainState(100, false));
            if (ViewSliderKeys.TryChange(vk, cur.VolumePercent, 0, 100, 1, 10, out double nextFwd))
            {
                ApplyForwardVolume(fid, (int)nextFwd);
                return true;
            }
        }
        if (id.StartsWith("canvas.dev.vol:", StringComparison.Ordinal))
        {
            string nodeKey = id["canvas.dev.vol:".Length..];
            if (_deviceNodes.TryGetValue(nodeKey, out var node) && !node.IsPhoneOutput && node.IsAvailable
                && ViewSliderKeys.TryChange(vk, node.Volume, 0, 100, 1, 10, out double nextDev))
            {
                ApplyDeviceSliderVolume(node, (int)nextDev);
                _actions.Host.FlushVolumeWrites();
                return true;
            }
        }
        return false;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (horizontal || delta == 0f) return false;
        if (_interaction != InteractionMode.None) return true;
        if (_dockToolsOpen && _dockToolsRect.Contains(x, y))
        {
            if (!_dockResultsRect.Contains(x, y)) return false;
            float before = _dockResultsScroll.Offset;
            _dockResultsScroll.OnWheel(delta);
            return _dockResultsScroll.Offset != before;
        }
        if (Dock.HandlesWheel(_hit, x, y)) return Dock.Scroll(delta);

        if (!_canvasRect.Contains(x, y) || _activeCanvasAppKey == null) return false;
        // Use the same clipped, frontmost target as pointer and keyboard input.
        int step = Math.Clamp(_actions.Config.VolumeStep, 1, 20);
        int change = delta > 0 ? step : -step;
        string? target = _hit?.HitTest(x, y)?.Id;
        if (target == "canvas.app.vol")
            ApplyAppSliderVolume(Math.Clamp(_selectedVolume + change, 0, 100));
        else if (target?.StartsWith("canvas.fwd.vol:", StringComparison.Ordinal) == true)
        {
            string id = target["canvas.fwd.vol:".Length..];
            var gain = _mirrorGains.GetValueOrDefault(id, new MirrorGainState(100, false));
            ApplyForwardVolume(id, Math.Clamp(gain.VolumePercent + change, 0, 100));
        }
        else if (target?.StartsWith("canvas.dev.vol:", StringComparison.Ordinal) == true
            && _deviceNodes.TryGetValue(target["canvas.dev.vol:".Length..], out var node))
            ApplyDeviceSliderVolume(node, Math.Clamp(node.Volume + change, 0, 100));
        else
            ZoomAtPoint(x, y, _zoom * (delta > 0 ? 1.12f : 1f / 1.12f));
        return true;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            if (_dockToolsOpen)
            {
                CloseDockTools();
                return true;
            }
            if (_outputPopup is { IsDisposed: false, IsVisible: true })
            {
                ClosePopups();
                return true;
            }
            if (_interaction != InteractionMode.None)
            {
                CancelCanvasInteraction(commitLayout: false);
                return true;
            }
        }

        if (ctrl && vk == 'A' && _filteredApps.Count > 0)
        {
            _multiSelected.Clear();
            foreach (var app in _filteredApps) _multiSelected.Add(app.Identity);
            if (_selectedApp == null) SetPrimarySelectedApp(_filteredApps[0]);
            return true;
        }

        if (ctrl && vk == '0' && _selectedApp != null)
        {
            ZoomAtPoint(_canvasRect.CenterX, _canvasRect.CenterY, 1f);
            return true;
        }
        return false;
    }

    private void RebuildCanvasCardOrder()
    {
        _canvasCardOrder.Clear();
        _canvasCardOrder.AddRange(_orderedRenderNodes);
        _canvasCardOrder.AddRange(_orderedCaptureNodes);
        _canvasCardOrder.Add(null);
        foreach (string key in _raisedCanvasCards)
        {
            for (int i = 0; i < _canvasCardOrder.Count; i++)
            {
                var card = _canvasCardOrder[i];
                if (!IdCmp.Equals(key, card?.LayoutKey ?? AppCanvasLayout.AppCardKey)) continue;
                _canvasCardOrder.RemoveAt(i);
                _canvasCardOrder.Add(card);
                break;
            }
        }
    }

    private void CommitCanvasCardDrag(string? draggedNodeKey)
    {
        string cardKey;
        if (draggedNodeKey == null)
        {
            _appNodeUserPositioned = true;
            cardKey = AppCanvasLayout.AppCardKey;
        }
        else if (_deviceNodes.TryGetValue(draggedNodeKey, out var node))
        {
            node.IsUserPositioned = true;
            node.HasAssignedPosition = true;
            _lastMovedDeviceLayoutKey = node.LayoutKey;
            RememberNodePosition(_activeCanvasAppKey, node);
            cardKey = node.LayoutKey;
        }
        else return;

        int index = _raisedCanvasCards.FindIndex(key => IdCmp.Equals(key, cardKey));
        if (index >= 0) _raisedCanvasCards.RemoveAt(index);
        _raisedCanvasCards.Add(cardKey);
        if (_raisedCanvasCards.Count > AppCanvasLayout.MaxDevices + 1) _raisedCanvasCards.RemoveAt(0);
        RebuildCanvasCardOrder();
    }

    private void BeginWireDrag(WireDragOriginKind kind, string? originDevId, UiPoint originWorld, UiColor color, bool dashed, float sx, float sy)
    {
        _actions.CloseInlineEdit(commit: true);
        _interaction = InteractionMode.DraggingWire;
        _wireOriginKind = kind;
        _wireOriginDeviceId = originDevId;
        _wireOriginWorld = originWorld;
        _wireDropInCanvas = true;
        _wireCursorWorld = new UiPoint(ScreenToWorldX(sx), ScreenToWorldY(sy));
        _wireDragColor = color;
        _wireDragDashed = dashed;
        _dragStartScreenX = sx;
        _dragStartScreenY = sy;
        _dragMoved = false;
    }

    private bool IsWireCompatibleWithDevice(CanvasDeviceNode node) => _wireOriginKind switch
    {
        WireDragOriginKind.NativePort => node.Flow == Flow.Render,
        WireDragOriginKind.ForwardPort => node.Flow == Flow.Render && !IdCmp.Equals(node.DeviceId, _wireOriginDeviceId),
        WireDragOriginKind.AddOutputPort => node.Flow == Flow.Render && !DisplayedOutputs.Contains(node.DeviceId, IdCmp),
        WireDragOriginKind.MicPort => node.Flow == Flow.Capture,
        WireDragOriginKind.RenderDevicePort => node.Flow == Flow.Render
            && _wireOriginDeviceId != null
            && !IdCmp.Equals(node.DeviceId, _wireOriginDeviceId)
            && DisplayedOutputs.Contains(_wireOriginDeviceId, IdCmp),
        WireDragOriginKind.CaptureDevicePort => node.Flow == Flow.Capture
            && _wireOriginDeviceId != null
            && !IdCmp.Equals(node.DeviceId, _wireOriginDeviceId)
            && IdCmp.Equals(DisplayedInput, _wireOriginDeviceId),
        _ => false,
    };

    private bool HasWireDragDistance(float x, float y)
    {
        float dx = x - _dragStartScreenX, dy = y - _dragStartScreenY;
        const float threshold = AppsCanvasMetrics.DragThresholdDip;
        return dx * dx + dy * dy >= threshold * threshold;
    }

    private bool IsWireDropInCanvas(float x, float y) =>
        _canvasRect.Deflate(1f).Contains(x, y) && !_canvasToolbarRect.Contains(x, y) && !IsDockOverlayAt(x, y);

    private bool IsDockOverlayAt(float x, float y) =>
        (_dockToolsOpen && _dockToolsRect.Contains(x, y)) ||
        _hit?.IsOverlayAt(x, y) == true ||
        Dock.OwnsItem(_hit?.HitTest(x, y)?.Id);

    private bool IsBlankWireDrop(float x, float y) =>
        !GetAppDropRect().Contains(x, y)
        && HitTestDeviceNode(Flow.Render, x, y) == null
        && HitTestDeviceNode(Flow.Capture, x, y) == null;

    private void CompleteWireDrag(float dropWorldX, float dropWorldY, bool wireDragged)
    {
        var app = _selectedApp;
        if (app == null || app.IsExternalOffline) return;

        // Screen-space click vs drag threshold (DPI- and zoom-independent)
        if (!wireDragged)
        {
            if (_wireOriginKind == WireDragOriginKind.AddOutputPort)
            {
                ToggleOutputConnectionPopup();
            }
            else if (_wireOriginKind == WireDragOriginKind.RenderDevicePort && _wireOriginDeviceId != null)
            {
                var activeOutputs = DisplayedOutputs;
                if (activeOutputs.Contains(_wireOriginDeviceId, IdCmp)) DisconnectOutputDevice(app, _wireOriginDeviceId);
                else ConnectOutputDevice(app, _wireOriginDeviceId);
            }
            else if (_wireOriginKind == WireDragOriginKind.CaptureDevicePort && _wireOriginDeviceId != null && !app.IsExternal)
            {
                SelectInputRoute(IdCmp.Equals(DisplayedInput, _wireOriginDeviceId)
                    ? AudioService.SystemDefaultInputDeviceId : _wireOriginDeviceId);
            }
            return;
        }

        switch (_wireOriginKind)
        {
            case WireDragOriginKind.AddOutputPort:
            {
                var target = HitTestDeviceNode(Flow.Render, dropWorldX, dropWorldY);
                if (target != null)
                {
                    var activeOutputs = DisplayedOutputs;
                    if (!activeOutputs.Contains(target.DeviceId, IdCmp))
                        ConnectOutputDevice(app, target.DeviceId);
                }
                break;
            }
            case WireDragOriginKind.NativePort:
            {
                var target = HitTestDeviceNode(Flow.Render, dropWorldX, dropWorldY);
                if (target != null)
                    SwitchNativeOutputDevice(app, target.DeviceId);
                else if (IsBlankWireDrop(dropWorldX, dropWorldY) && GetDisplayedNativeOutputId(app) is { } curNat)
                    DisconnectOutputDevice(app, curNat);
                break;
            }
            case WireDragOriginKind.ForwardPort when _wireOriginDeviceId != null:
            {
                var target = HitTestDeviceNode(Flow.Render, dropWorldX, dropWorldY);
                if (target != null)
                    RetargetForwardOutput(app, _wireOriginDeviceId, target.DeviceId);
                else if (IsBlankWireDrop(dropWorldX, dropWorldY))
                    DisconnectOutputDevice(app, _wireOriginDeviceId);
                break;
            }
            case WireDragOriginKind.MicPort:
            {
                var target = HitTestDeviceNode(Flow.Capture, dropWorldX, dropWorldY);
                if (target != null)
                    SelectInputRoute(target.DeviceId);
                else if (IsBlankWireDrop(dropWorldX, dropWorldY) && !AudioService.IsSystemDefault(DisplayedInput))
                    SelectInputRoute(AudioService.SystemDefaultInputDeviceId);
                break;
            }
            case WireDragOriginKind.RenderDevicePort when _wireOriginDeviceId != null:
            {
                var dropKind = ResolveRenderDeviceDropTarget(dropWorldX, dropWorldY, out string? targetFwdId);
                if (dropKind == RenderDeviceDropKind.Native)
                {
                    SwitchNativeOutputDevice(app, _wireOriginDeviceId);
                    break;
                }
                if (dropKind == RenderDeviceDropKind.ForwardRow && targetFwdId != null)
                {
                    RetargetForwardOutput(app, targetFwdId, _wireOriginDeviceId);
                    break;
                }
                if (dropKind == RenderDeviceDropKind.AddOutput)
                {
                    ConnectOutputDevice(app, _wireOriginDeviceId);
                    break;
                }

                // Dragging a connected output device port onto another output device reroutes; onto blank canvas disconnects.
                var activeOutputs = DisplayedOutputs;
                if (!activeOutputs.Contains(_wireOriginDeviceId, IdCmp)) break;

                var otherDevice = HitTestDeviceNode(Flow.Render, dropWorldX, dropWorldY);
                if (otherDevice != null)
                {
                    if (IdCmp.Equals(otherDevice.DeviceId, _wireOriginDeviceId)) break; // Released back on own device -> cancel
                    string? curNative = GetDisplayedNativeOutputId(app);
                    if (curNative != null && IdCmp.Equals(curNative, _wireOriginDeviceId))
                        SwitchNativeOutputDevice(app, otherDevice.DeviceId);
                    else
                        RetargetForwardOutput(app, _wireOriginDeviceId, otherDevice.DeviceId);
                }
                else if (IsBlankWireDrop(dropWorldX, dropWorldY))
                {
                    DisconnectOutputDevice(app, _wireOriginDeviceId);
                }
                break;
            }
            case WireDragOriginKind.CaptureDevicePort when _wireOriginDeviceId != null && !app.IsExternal:
            {
                if (IsCaptureDeviceDropOnApp(dropWorldX, dropWorldY))
                {
                    SelectInputRoute(_wireOriginDeviceId);
                    break;
                }

                if (!IdCmp.Equals(DisplayedInput, _wireOriginDeviceId)) break;
                var otherMic = HitTestDeviceNode(Flow.Capture, dropWorldX, dropWorldY);
                if (otherMic != null)
                {
                    if (!IdCmp.Equals(otherMic.DeviceId, _wireOriginDeviceId))
                        SelectInputRoute(otherMic.DeviceId);
                }
                else if (IsBlankWireDrop(dropWorldX, dropWorldY) && !AudioService.IsSystemDefault(_wireOriginDeviceId))
                {
                    SelectInputRoute(AudioService.SystemDefaultInputDeviceId);
                }
                break;
            }
        }
    }

    private CanvasDeviceNode? HitTestDeviceNode(Flow flow, float wx, float wy)
    {
        for (int i = _canvasCardOrder.Count - 1; i >= 0; i--)
        {
            var n = _canvasCardOrder[i];
            if (n != null && n.Flow == flow && GetDeviceDropRect(n).Contains(wx, wy)) return n;
        }
        return null;
    }

    private CanvasWire? HitTestWire(float wx, float wy)
    {
        CanvasWire? best = null;
        float bestDistSq = 64f; // 8 DIP world tolerance
        for (int i = 0; i < _wires.Count; i++)
        {
            float d2 = CanvasWireGeometryCache.DistanceSquaredToWire(_wires[i], wx, wy);
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                best = _wires[i];
            }
        }
        return best;
    }

    private void ShowWireContextMenu(CanvasWire wire)
    {
        var app = _selectedApp;
        if (app == null || app.IsExternalOffline) return;
        var items = new List<UiMenuItem>();
        if (wire.Kind == CanvasWireKind.CaptureForward)
        {
            if (!PcRoutingApi.IsPhoneOutputDevice(wire.DeviceId) || app.IsExternal)
            {
                items.Add(new UiMenuItem
                {
                    Text = L10n.T("Apps.Canvas.SetAsNativeOutput"),
                    Icon = UiIcon.Link,
                    Click = () => SwitchNativeOutputDevice(app, wire.DeviceId),
                });
            }
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.Canvas.DisconnectOne"),
                Icon = UiIcon.Close,
                Click = () => DisconnectOutputDevice(app, wire.DeviceId),
            });
        }
        else if (wire.Kind == CanvasWireKind.NativeOutput)
        {
            var activeOutputs = DisplayedOutputs;
            bool canDisc = activeOutputs.Length > 1 || !AudioService.IsSystemDefault(wire.DeviceId);
            if (canDisc)
            {
                items.Add(new UiMenuItem
                {
                    Text = L10n.T("Apps.Canvas.DisconnectOne"),
                    Icon = UiIcon.Close,
                    Click = () => DisconnectOutputDevice(app, wire.DeviceId),
                });
            }
        }
        else if (wire.Kind == CanvasWireKind.MicrophoneInput && !AudioService.IsSystemDefault(wire.DeviceId))
        {
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.Canvas.DisconnectOne"),
                Icon = UiIcon.Close,
                Click = () => SelectInputRoute(AudioService.SystemDefaultInputDeviceId),
            });
        }

        if (items.Count > 0) _actions.ShowMenuAtCursor(items);
    }

    private void CancelCanvasInteraction(bool commitLayout)
    {
        if (_interaction == InteractionMode.None) return;
        if (_interaction is InteractionMode.DraggingAppVolume or InteractionMode.DraggingDeviceVolume)
            _actions.Host.FlushVolumeWrites();
        if (!commitLayout)
        {
            if (_interaction == InteractionMode.DraggingNode && _dragMoved)
            {
                if (_dragNodeKey == null)
                {
                    _appNodeX = _dragOrigX;
                    _appNodeY = _dragOrigY;
                }
                else if (_deviceNodes.TryGetValue(_dragNodeKey, out var node))
                {
                    node.X = _dragOrigX;
                    node.Y = _dragOrigY;
                }
            }
            else if (_interaction == InteractionMode.PanningCanvas && _dragMoved)
            {
                _panX = _dragOrigX;
                _panY = _dragOrigY;
            }
        }
        else if (_dragMoved && _interaction is InteractionMode.DraggingNode or InteractionMode.PanningCanvas)
        {
            if (_interaction == InteractionMode.DraggingNode)
                CommitCanvasCardDrag(_dragNodeKey);
            ScheduleCanvasLayoutSave();
        }
        _interaction = InteractionMode.None;
        _dragNodeKey = null;
        _dragForwardDeviceId = null;
        _dragDeviceNodeKey = null;
        _wireOriginDeviceId = null;
        _dragMoved = false;
    }
}
