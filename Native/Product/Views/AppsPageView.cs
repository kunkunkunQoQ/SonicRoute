using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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

internal sealed partial class AppsPageView : IViewPage, IViewDockPage
{
    private static readonly StringComparer IdCmp = StringComparer.OrdinalIgnoreCase;

    private enum AppFilterMode { All, Active, Saved, Hidden }

    private readonly IViewHostActions _actions;
    private readonly AliasEditor _aliasEditor;
    private readonly Action _appIconLoaded;
    private UiHitMap? _hit;
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioAppInfo> _appRows = new(StringComparer.Ordinal);

    private List<AudioAppInfo> _apps = new();
    private List<AudioAppInfo> _filteredApps = new();
    private string _searchQuery = "";
    private AppFilterMode _filterMode = AppFilterMode.All;

    private Endpoint[] _outputs = Array.Empty<Endpoint>();
    private Endpoint[] _inputs = Array.Empty<Endpoint>();
    private string? _outputChoicesIdentity;
    private string? _windowsDefaultRenderId;
    private string? _windowsDefaultCaptureId;
    private Dictionary<string, bool> _mutedByIdentity = new(StringComparer.Ordinal);

    // Globally shared device volume/mute cache across all app canvases (keyed by "render:<id>" / "capture:<id>")
    private readonly Dictionary<string, (int Volume, bool Muted, bool Available)> _deviceVolumes = new(IdCmp);

    private readonly HashSet<string> _multiSelected = new(StringComparer.Ordinal);
    private string? _selectedIdentity;
    private string? _selectionAnchorIdentity;
    private bool _autoSelectApp = true;
    private AudioAppInfo? _selectedApp;

    private int _selectedVolume = -1;
    private bool _selectedMuted;
    private string _nativeOutput = AudioService.SystemDefaultDeviceId;
    private string _nativeInput = AudioService.SystemDefaultInputDeviceId;
    private string[] _selectedOutputs = [];
    private string[]? _pendingOutputs;
    private string[] DisplayedOutputs => _pendingOutputs ?? _selectedOutputs;
    private string? _pendingPrimary;
    private readonly Dictionary<string, MirrorGainState> _mirrorGains = new(IdCmp);

    private UiRect _searchBoxRect;
    private UiRect _filterBtnRect;

    private bool _active;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _forceAppsRefreshPending;
    private int _activationGen;
    private int _stateGen;
    private int _mirrorGainRevision;
    private int _outputEditGen;
    private Task _inputWrites = Task.CompletedTask;
    private string? _pendingInput;
    private string DisplayedInput => _pendingInput ?? _nativeInput;
    private int _inputEditGen;
#if SONICROUTE_PHONE
    private Task _externalOutputs = Task.CompletedTask;
    private bool _phoneSubscribed;
#endif

    public bool HasOpenModal => _outputPopup is { IsDisposed: false, IsVisible: true };

    internal AppsPageView(IViewHostActions actions)
    {
        _actions = actions;
        Dock = new UiDockRail(DockItemPrefix, actions.Invalidate);
        _aliasEditor = new(actions);
        _appIconLoaded = () => _actions.Host.TryPost(InvalidateAppIcon);
    }

    private void InvalidateAppIcon()
    {
        if (_active) _actions.Invalidate();
    }

#if SONICROUTE_PHONE
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
        if (_active) return;
        _active = true;
#if SONICROUTE_PHONE
        if (!_phoneSubscribed)
        {
            _phoneSubscribed = true;
            var phone = _actions.Host.Phone;
            phone.Changed += OnPhoneStoreChanged;
            phone.StartTracking();
            if (phone.Discovery.CurrentDevices == null)
                _ = phone.RefreshInBackgroundAsync();
        }
#endif
        TriggerRefresh(forceAppsRefresh: false);
    }

    public void OnDeactivated()
    {
        _active = false;
        _activationGen++;
        _refreshPending = false;
        _forceAppsRefreshPending = false;
        CloseDockTools();
        ResetDockHover();
        CancelCanvasInteraction(commitLayout: true);
        ClosePopups();
        FlushCanvasLayoutSave();
        _wireCache.Clear();
        if (!_actions.Config.ExperimentalMic) ReleaseMicResources();
#if SONICROUTE_PHONE
        if (_phoneSubscribed)
        {
            _phoneSubscribed = false;
            _actions.Host.Phone.Changed -= OnPhoneStoreChanged;
        }
#endif
    }

    public void ClosePopups()
    {
        if (_outputPopup is { } popup)
        {
            _outputPopup = null;
            if (!popup.IsDisposed) popup.CloseWindow();
        }
    }

    public void OnThemeChanged(UiThemePalette theme)
    {
        _outputPopup?.ApplyTheme(theme);
    }

    public void RefreshData()
    {
        if (_active) TriggerRefresh(forceAppsRefresh: false);
    }

    internal void RefreshSettings()
    {
        if (!_active) return;
        SortAppsStably(_apps);
        RebuildFilteredApps();
        if (_selectedApp != null && _outputChoicesIdentity == _selectedIdentity)
        {
            SyncCanvasNodesAndLayout();
            SyncOutputPopupIfOpen();
        }
    }

    public void OnCaptureLost()
    {
        CancelCanvasInteraction(commitLayout: false);
        _dockResultsScroll.EndDrag();
        ResetDockHover();
        _actions.Invalidate();
    }

    public void OnFilesDropped(string[] paths, string? targetId)
    {
        if (targetId != "canvas.app.icon") return;
        if (paths.Length != 1 || _selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName))
        {
            if (_selectedApp == null) _actions.ShowNotice(L10n.T("Apps.SelectHint"));
            return;
        }
        string ext = Path.GetExtension(paths[0]);
        if (IdCmp.Equals(ext, ".png") || IdCmp.Equals(ext, ".jpg") || IdCmp.Equals(ext, ".jpeg")
            || IdCmp.Equals(ext, ".ico") || IdCmp.Equals(ext, ".exe"))
        {
            ImportCustomIconFromPath(_selectedApp.ProcessName!, paths[0]);
        }
        else
        {
            _actions.ShowNotice(L10n.T("AppIcons.Invalid"));
        }
    }

    private void BeginSearchEdit()
    {
        _actions.StartInlineEdit(
            "apps.search",
            _searchBoxRect,
            _searchQuery,
            SetDockSearch,
            (text, viaEnter) =>
            {
                SetDockSearch(text);
                if (viaEnter && _filteredApps.Count != 0 && !string.IsNullOrWhiteSpace(text))
                {
                    var app = _filteredApps[0];
                    CloseDockTools();
                    SelectSingleApp(app);
                }
            },
            selectAll: false);
    }

    private void SetDockSearch(string text)
    {
        if (_searchQuery == text) return;
        _searchQuery = text;
        Dock.ResetScroll();
        _dockResultsScroll.Offset = 0f;
        RebuildFilteredApps();
        _actions.Invalidate();
    }

    private void OpenFilterMenu()
    {
        var items = new List<UiMenuItem>
        {
            new() { Text = GetFilterLabel(AppFilterMode.All), Checked = _filterMode == AppFilterMode.All, Click = () => SetFilterMode(AppFilterMode.All) },
            new() { Text = GetFilterLabel(AppFilterMode.Active), Checked = _filterMode == AppFilterMode.Active, Click = () => SetFilterMode(AppFilterMode.Active) },
            new() { Text = GetFilterLabel(AppFilterMode.Saved), Checked = _filterMode == AppFilterMode.Saved, Click = () => SetFilterMode(AppFilterMode.Saved) },
            new() { Text = GetFilterLabel(AppFilterMode.Hidden), Checked = _filterMode == AppFilterMode.Hidden, Click = () => SetFilterMode(AppFilterMode.Hidden) },
        };
        if (_actions.Config.HiddenPanelApps.Count > 0)
        {
            items.Add(UiMenuItem.Separator());
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Apps.ShowInPanel") + " (" + L10n.T("St.SelectAll") + ")",
                Icon = UiIcon.Refresh,
                Click = UnhideAllPanelApps,
            });
        }
        _actions.ShowMenu(_filterBtnRect, items);
    }

    private void SetFilterMode(AppFilterMode mode)
    {
        if (_filterMode == mode) return;
        _filterMode = mode;
        Dock.ResetScroll();
        _dockResultsScroll.Offset = 0f;
        RebuildFilteredApps();
        _actions.Invalidate();
    }

    private static string GetFilterLabel(AppFilterMode mode) => mode switch
    {
        AppFilterMode.Active => L10n.T("Apps.Filter.Active"),
        AppFilterMode.Saved => L10n.T("Apps.Filter.Saved"),
        AppFilterMode.Hidden => L10n.T("Apps.Filter.Hidden"),
        _ => L10n.T("Apps.Filter.All"),
    };

    private bool HasSavedConfig(AudioAppInfo app)
    {
        var cfg = _actions.Config;
        string stableKey = GetStableAppKey(app);
        if (cfg.AppCanvasLayouts.ContainsKey(stableKey)) return true;
        if (!string.IsNullOrWhiteSpace(app.ProcessName))
        {
            string proc = app.ProcessName!;
            if (cfg.AppNames.ContainsKey(proc) || AppIconFiles.GetFileName(cfg, proc) != null
                || cfg.DisabledAutoSwitchApps.Contains(proc, IdCmp)
                || cfg.HiddenPanelApps.Contains(proc, IdCmp))
                return true;
        }
        if (app.IsExternal && app.ExternalPreferenceKey != null) return true;
        if (!app.IsExternal && app.ProcessId > 0 && AppPlaybackRestore.HasSavedChoice((int)app.ProcessId)) return true;
        return false;
    }

    private void RebuildFilteredApps()
    {
        string q = _searchQuery.Trim();
        _filteredApps.Clear();
        for (int i = 0; i < _apps.Count; i++)
        {
            var app = _apps[i];
            if (_filterMode == AppFilterMode.Active && !app.HasActiveSession) continue;
            if (_filterMode == AppFilterMode.Saved && !HasSavedConfig(app)) continue;
            if (_filterMode == AppFilterMode.Hidden &&
                (string.IsNullOrWhiteSpace(app.ProcessName) || !_actions.Config.HiddenPanelApps.Contains(app.ProcessName!, IdCmp)))
                continue;

            if (q.Length > 0)
            {
                string label = GetAppDisplayLabel(app);
                bool match = label.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (app.ProcessName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                    || (app.SourceLabel?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
                if (!match) continue;
            }
            _filteredApps.Add(app);
        }
    }

    private void SortAppsStably(List<AudioAppInfo> apps)
    {
        // Stable sort independent of transient HasActiveSession changes so rows never jump during playback pauses.
        apps.Sort((a, b) =>
        {
            int extCmp = (a.IsExternal ? 1 : 0).CompareTo(b.IsExternal ? 1 : 0);
            if (extCmp != 0) return extCmp;
            int nameCmp = StringComparer.CurrentCultureIgnoreCase.Compare(GetAppDisplayLabel(a), GetAppDisplayLabel(b));
            if (nameCmp != 0) return nameCmp;
            int procCmp = StringComparer.OrdinalIgnoreCase.Compare(a.ProcessName ?? "", b.ProcessName ?? "");
            if (procCmp != 0) return procCmp;
            return StringComparer.OrdinalIgnoreCase.Compare(a.Identity, b.Identity);
        });
    }

    private void DrawAppIcon(D2dTarget g, UiThemePalette t, AudioAppInfo app, UiRect rect) =>
        ResolveAppIcon(app, GetAppDisplayLabel(app)).Render(g, t, rect);

    private UiItemIcon ResolveAppIcon(AudioAppInfo app, string label)
    {
        if (app.ExternalIconPng is { Length: > 0 } extBytes &&
            NativeAppIconService.GetPngIcon(extBytes, _appIconLoaded) is { } eb)
            return new(eb);
        var resolved = NativeAppIconService.GetAppIcon(
            _actions.Config,
            (int)app.ProcessId,
            app.IconPath,
            app.ProcessName,
            _appIconLoaded);

        if (resolved is { } nb) return new(nb);

#if SONICROUTE_PHONE
        UiIcon? externalIcon = app.ProcessName switch
        {
            "airplay:receiver" => UiIcon.AirPlay,
            "android:sonicroute:device-audio" => UiIcon.Phone,
            _ => null,
        };
        if (externalIcon is { } glyph) return new(null, glyph);
#endif
        string initial = label.Length > 0 ? StringInfo.GetNextTextElement(label).ToUpperInvariant() : "?";
        return new(null, Initial: initial);
    }

    private void TriggerRefresh(bool forceAppsRefresh)
    {
        if (!_active) return;
        if (_refreshing)
        {
            _refreshPending = true;
            _forceAppsRefreshPending |= forceAppsRefresh;
            return;
        }
        _refreshing = true;
        _refreshPending = false;
        forceAppsRefresh |= _forceAppsRefreshPending;
        _forceAppsRefreshPending = false;

        int activation = _activationGen;
        int gen = _stateGen;
        int mirrorRevision = _mirrorGainRevision;
        long appVolAtRead = _actions.Host.AppVolumeRevision;
        long epVolAtRead = _actions.Host.EndpointWriteRevision;
        string? targetIdentity = _selectedIdentity;
        bool autoSelectApp = _autoSelectApp;
        var selectedSnapshot = _selectedApp;
        bool readMic = _actions.Config.ExperimentalMic;

        _ = Task.Run(() =>
        {
            try
            {
                var winRender = ProductAudio.Devices(Flow.Render);
                var outs = _actions.Host.Audio.RenderDevicesWithPhone(selectedSnapshot, windows: winRender, includePhoneCurrent: true);
                var ins = readMic ? ProductAudio.Devices(Flow.Capture) : Array.Empty<Endpoint>();
                string? defRenderId = winRender.Length > 0 ? TryReadDefaultId(Flow.Render) : null;
                string? defCaptureId = readMic && ins.Length > 0 ? TryReadDefaultId(Flow.Capture) : null;

                var apps = _actions.Host.Audio.Apps(forceAppsRefresh, includeOfflineExternal: true);
                var mutedMap = new Dictionary<string, bool>(apps.Count, StringComparer.Ordinal);

                // Read shared device endpoint volumes & mute states
                var devVols = new Dictionary<string, (int Volume, bool Muted, bool Available)>(IdCmp);
                if (winRender.Length > 0)
                {
                    ReadEndpointVolumeInto(devVols, Flow.Render, AudioService.SystemDefaultDeviceId);
                    foreach (var ep in winRender)
                        ReadEndpointVolumeInto(devVols, Flow.Render, ep.Id);
                }
                if (readMic && ins.Length > 0)
                {
                    ReadEndpointVolumeInto(devVols, Flow.Capture, AudioService.SystemDefaultInputDeviceId);
                    foreach (var ep in ins)
                        ReadEndpointVolumeInto(devVols, Flow.Capture, ep.Id);
                }

                int vol = -1;
                bool muted = false;
                string natOut = AudioService.SystemDefaultDeviceId;
                string natIn = AudioService.SystemDefaultInputDeviceId;
                string[] selOuts = [AudioService.SystemDefaultDeviceId];
                var mirrorGains = new Dictionary<string, MirrorGainState>(IdCmp);

                var liveSelected = targetIdentity != null ? apps.FirstOrDefault(a => a.Identity == targetIdentity) : null;
                if (autoSelectApp && targetIdentity == null && apps.Count > 0)
                {
                    // Auto-pick preferred app on first visit (current foreground/last app if present, else first stably sorted)
                    var preferred = _actions.Host.CurrentApp.Current;
                    liveSelected = preferred != null ? apps.FirstOrDefault(a => a.Identity == preferred.Identity) : null;
                    if (liveSelected == null)
                    {
                        var sortedPreview = new List<AudioAppInfo>(apps);
                        SortAppsStably(sortedPreview);
                        liveSelected = sortedPreview[0];
                    }
                    // Re-fetch phone outputs scoped for the auto-selected app if external
                    if (liveSelected.IsExternal)
                        outs = _actions.Host.Audio.RenderDevicesWithPhone(liveSelected, windows: winRender, includePhoneCurrent: true);
                }

                foreach (var app in apps)
                {
                    try
                    {
                        var volume = _actions.Host.Audio.Volume(app);
                        mutedMap[app.Identity] = volume.Muted;
                        if (ReferenceEquals(app, liveSelected))
                        {
                            vol = volume.Count > 0 ? volume.Percent : -1;
                            muted = volume.Muted;
                        }
                    }
                    catch { mutedMap[app.Identity] = false; }
                }

                if (liveSelected != null)
                {
                    try
                    {
                        string? persisted = _actions.Host.Audio.ReadRoute(liveSelected, Flow.Render);
                        natOut = string.IsNullOrWhiteSpace(persisted) || AudioService.IsSystemDefault(persisted)
                            ? AudioService.SystemDefaultDeviceId
                            : AudioPolicyConfig.UnpackDeviceId(persisted);
                        selOuts = _actions.Host.Audio.GetSelectedOutputs(liveSelected);
                        if (selOuts.Length == 0) selOuts = [natOut];
                        foreach (string oid in selOuts)
                            mirrorGains[oid] = _actions.Host.Audio.GetMirrorGain(liveSelected, oid);
                    }
                    catch { }

                    if (readMic && !liveSelected.IsExternal)
                    {
                        try
                        {
                            string? persistedIn = _actions.Host.Audio.ReadRoute((int)liveSelected.ProcessId, Flow.Capture);
                            natIn = string.IsNullOrWhiteSpace(persistedIn) || AudioService.IsSystemDefault(persistedIn)
                                ? AudioService.SystemDefaultInputDeviceId
                                : AudioPolicyConfig.UnpackDeviceId(persistedIn);
                        }
                        catch { }
                    }
                }

                _actions.Host.TryPost(() =>
                {
                    _refreshing = false;
                    if (!_active) return;
                    if (activation != _activationGen || readMic != _actions.Config.ExperimentalMic)
                    {
                        TriggerRefresh(forceAppsRefresh: false);
                        return;
                    }

                    SortAppsStably(apps);
                    _apps = apps;
                    _mutedByIdentity = mutedMap;
                    _windowsDefaultRenderId = defRenderId;
                    _windowsDefaultCaptureId = defCaptureId;

                    if (readMic)
                        _inputs = ins;
                    else
                        ReleaseMicResources();

                    // Merge device volumes while respecting active drags / pending endpoint writes
                    foreach (string key in _deviceVolumes.Keys)
                        if (!devVols.ContainsKey(key)) _deviceVolumes.Remove(key);
                    foreach (var pair in devVols)
                    {
                        bool keepLocal = (_interaction == InteractionMode.DraggingDeviceVolume && IdCmp.Equals(_dragDeviceNodeKey, pair.Key))
                            || IsEndpointWritePendingByNodeKey(pair.Key, epVolAtRead);
                        if (keepLocal && _deviceVolumes.TryGetValue(pair.Key, out var existing))
                            _deviceVolumes[pair.Key] = (existing.Volume, pair.Value.Muted, pair.Value.Available);
                        else
                            _deviceVolumes[pair.Key] = pair.Value;
                    }

                    bool autoSelected = _autoSelectApp && gen == _stateGen && targetIdentity == null && _selectedIdentity == null && liveSelected != null;
                    if (autoSelected)
                    {
                        _autoSelectApp = false;
                        _selectedIdentity = liveSelected!.Identity;
                        _selectionAnchorIdentity = liveSelected.Identity;
                        _multiSelected.Clear();
                        _multiSelected.Add(liveSelected.Identity);
                        targetIdentity = liveSelected.Identity;
                    }

                    if (targetIdentity == _selectedIdentity)
                    {
                        _outputs = outs;
                        _outputChoicesIdentity = liveSelected != null ? targetIdentity : null;
                    }

                    if (targetIdentity != null && gen == _stateGen)
                    {
                        if (liveSelected != null)
                        {
                            _selectedApp = liveSelected;
                            if (_interaction != InteractionMode.DraggingAppVolume
                                && appVolAtRead == _actions.Host.AppVolumeRevision
                                && !_actions.Host.HasPendingVolume(liveSelected))
                            {
                                _selectedVolume = vol;
                                _selectedMuted = muted;
                            }
                            if (_pendingInput == null) _nativeInput = natIn;
                            if (_pendingOutputs == null)
                            {
                                _nativeOutput = natOut;
                                // Readback confirms membership. Keep the display order of surviving connections.
                                var previousOutputs = _selectedOutputs;
                                if (previousOutputs.Length != selOuts.Length || !previousOutputs.All(id => selOuts.Contains(id, IdCmp)))
                                {
                                    _selectedOutputs = [.. previousOutputs.Where(id => selOuts.Contains(id, IdCmp)),
                                        .. selOuts.Where(id => !previousOutputs.Contains(id, IdCmp))];
                                }
                                if (mirrorRevision == _mirrorGainRevision)
                                {
                                    _mirrorGains.Clear();
                                    foreach (var gain in mirrorGains) _mirrorGains.Add(gain.Key, gain.Value);
                                }
                            }
                        }
                        else if (_selectedApp != null)
                        {
                            ClearAppSelection();
                            if (_apps.Count > 0) SelectSingleApp(_apps[0]);
                            else _autoSelectApp = true;
                        }
                    }

                    RebuildFilteredApps();
                    if (_selectedApp == null || _outputChoicesIdentity == _selectedIdentity)
                        SyncCanvasNodesAndLayout(autoSelected);
                    SyncOutputPopupIfOpen();
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

    private string? TryReadDefaultId(Flow flow)
    {
        try { return ProductAudio.DefaultId(flow); }
        catch { return null; }
    }

    private void ReadEndpointVolumeInto(Dictionary<string, (int Volume, bool Muted, bool Available)> map, Flow flow, string endpointId)
    {
        string key = DeviceNodeKey(flow, endpointId);
        try
        {
            var st = _actions.Host.Audio.Volume(flow, endpointId, 0);
            map[key] = (st.Percent, st.Muted, true);
        }
        catch
        {
            map[key] = (0, false, false);
        }
    }

    private bool IsEndpointWritePendingByNodeKey(string nodeKey, long epVolAtRead)
    {
        if (epVolAtRead != _actions.Host.EndpointWriteRevision) return true;
        bool capture = nodeKey.StartsWith("capture:", StringComparison.OrdinalIgnoreCase);
        var flow = capture ? Flow.Capture : Flow.Render;
        string id = nodeKey[(capture ? 8 : 7)..];
        string sysId = capture ? AudioService.SystemDefaultInputDeviceId : AudioService.SystemDefaultDeviceId;
        string? actualId = capture ? _windowsDefaultCaptureId : _windowsDefaultRenderId;
        string? equivalent = IdCmp.Equals(id, sysId) ? actualId : IdCmp.Equals(id, actualId) ? sysId : null;
        return _actions.Host.EndpointVolumePending(flow, id) || _actions.Host.EndpointVolumePending(flow, equivalent);
    }

    private void HandleAppRowClick(AudioAppInfo app, bool ctrl, bool shift)
    {
        if (shift && _selectionAnchorIdentity != null)
        {
            int a = _filteredApps.FindIndex(x => x.Identity == _selectionAnchorIdentity);
            int b = _filteredApps.FindIndex(x => x.Identity == app.Identity);
            if (a >= 0 && b >= 0)
            {
                if (!ctrl) _multiSelected.Clear();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                    _multiSelected.Add(_filteredApps[i].Identity);
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
        FlushCanvasLayoutSave();
        ClosePopups();
        CancelCanvasInteraction(commitLayout: false);
        _actions.CloseInlineEdit(commit: true);
        ResetSelectionRouting();
        _autoSelectApp = false;
        _selectedIdentity = app.Identity;
        _selectedApp = app;
        _selectedVolume = -1;
        _selectedMuted = _mutedByIdentity.GetValueOrDefault(app.Identity);
        // Load the selected app's layout only when its route snapshot is ready.
        _actions.Invalidate();
        TriggerRefresh(forceAppsRefresh: false);
    }

    private void ClearAppSelection()
    {
        FlushCanvasLayoutSave();
        ClosePopups();
        CancelCanvasInteraction(commitLayout: false);
        ResetSelectionRouting();
        _autoSelectApp = false;
        _selectedIdentity = null;
        _selectedApp = null;
        _selectedVolume = -1;
        _selectedMuted = false;
        _actions.Invalidate();
    }

    private void ResetSelectionRouting()
    {
        _stateGen++;
        _outputEditGen++;
        _inputEditGen++;
        _pendingInput = null;
        _nativeInput = AudioService.SystemDefaultInputDeviceId;
        _pendingOutputs = null;
        _pendingPrimary = null;
        _nativeOutput = AudioService.SystemDefaultDeviceId;
        _selectedOutputs = [];
        _mirrorGains.Clear();
        _outputChoicesIdentity = null;
        foreach (var node in _deviceNodes.Values)
            RememberNodePosition(_activeCanvasAppKey, node);
        _activeCanvasAppKey = null;
        _lastLayoutSignature = null;
        _needsInitialFit = false;
        _deviceNodes.Clear();
        _orderedRenderNodes.Clear();
        _orderedCaptureNodes.Clear();
        _canvasCardOrder.Clear();
        _raisedCanvasCards.Clear();
        _wires.Clear();
        _wireCache.Clear();
    }

    private void ShowAppContextMenu(AudioAppInfo primary, UiRect? anchor = null, bool fromAppList = false)
    {
        var selectedApps = _apps.Where(a => _multiSelected.Contains(a.Identity)).ToList();
        if (selectedApps.Count == 0 || !selectedApps.Any(a => a.Identity == primary.Identity))
            selectedApps = new List<AudioAppInfo> { primary };
        var availableApps = selectedApps.Where(a => !a.IsExternalOffline).ToList();
        bool multi = selectedApps.Count > 1;
        bool primaryMuted = _mutedByIdentity.GetValueOrDefault(primary.Identity);
        bool hasProc = !string.IsNullOrWhiteSpace(primary.ProcessName);

        var items = new List<UiMenuItem>();
        if (!multi && hasProc)
        {
            items.Add(new UiMenuItem { Text = L10n.T("Apps.Rename"), Icon = UiIcon.Edit, Click = BeginInlineRename });
            items.Add(new UiMenuItem { Text = L10n.T("AppIcons.Import"), Icon = UiIcon.Apps, Click = () => ImportCustomIcon(primary) });
            bool canResetIcon = AppIconFiles.GetFileName(_actions.Config, primary.ProcessName!) != null;
            items.Add(new UiMenuItem { Text = L10n.T("AppIcons.Reset"), Icon = UiIcon.Reset, Enabled = canResetIcon, Click = () => ResetCustomIcon(primary) });
        }

        if (availableApps.Count != 0)
        {
            if (items.Count > 0) items.Add(UiMenuItem.Separator());
            items.Add(new UiMenuItem
            {
                Text = L10n.T(primaryMuted ? "Auto.MuteOff" : "Auto.MuteOn"),
                Icon = primaryMuted ? UiIcon.Speaker : UiIcon.SpeakerMuted,
                Click = () => BatchToggleMute(availableApps, !primaryMuted),
            });
        }

        if (fromAppList && multi) AddBatchSettingsItems(selectedApps, items);

        if (availableApps.Count != 0)
        {
            items.Add(UiMenuItem.Separator());
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Dev.SystemDefault"),
                Icon = UiIcon.Reset,
                Click = () => BatchResetRoutes(availableApps),
            });
        }

#if SONICROUTE_PHONE
        if (!multi && primary.IsExternal)
        {
            items.Add(UiMenuItem.Separator());
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Phone.StopReceivingApp"),
                Icon = UiIcon.Exit,
                Click = () => _ = StopExternalAppAsync(primary),
            });
        }
#endif

        if (anchor is { } rect) _actions.ShowMenu(rect, items);
        else _actions.ShowMenuAtCursor(items);
    }

    private void AddBatchSettingsItems(List<AudioAppInfo> selectedApps, List<UiMenuItem> items)
    {
        var withProcess = selectedApps.Where(app => !string.IsNullOrWhiteSpace(app.ProcessName)).ToList();
        if (withProcess.Count == 0) return;

        var disabled = _actions.Config.DisabledAutoSwitchApps;
        bool allDisabled = withProcess.All(app => disabled.Contains(app.ProcessName!, IdCmp));
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Apps.DisableAuto"),
            Checked = allDisabled,
            Click = () => SetBatchMembership(withProcess, disabled, !allDisabled),
        });

        var hidden = _actions.Config.HiddenPanelApps;
        bool allShown = withProcess.All(app => !hidden.Contains(app.ProcessName!, IdCmp));
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Apps.ShowInPanel"),
            Checked = allShown,
            Click = () => SetBatchMembership(withProcess, hidden, allShown),
        });
    }

    private void ShowAppIconContextMenu(AudioAppInfo app)
    {
        bool hasProc = !string.IsNullOrWhiteSpace(app.ProcessName);
        if (!hasProc) return;
        bool canResetIcon = AppIconFiles.GetFileName(_actions.Config, app.ProcessName!) != null;
        var items = new List<UiMenuItem>
        {
            new() { Text = L10n.T("AppIcons.Import"), Icon = UiIcon.Apps, Click = () => ImportCustomIcon(app) },
            new() { Text = L10n.T("AppIcons.Reset"), Icon = UiIcon.Reset, Enabled = canResetIcon, Click = () => ResetCustomIcon(app) },
        };
        _actions.ShowMenuAtCursor(items);
    }

#if SONICROUTE_PHONE
    private async Task StopExternalAppAsync(AudioAppInfo app)
    {
        try
        {
            if (_actions.Host.Audio.ExternalAudio is { } bridge)
                await bridge.StopExternalSourceAsync(app).ConfigureAwait(false);
            _actions.Host.TryPost(() =>
            {
                if (_active) TriggerRefresh(forceAppsRefresh: true);
            });
        }
        catch (Exception ex)
        {
            _actions.Host.TryPost(() => _actions.ShowNotice(ex.Message));
        }
    }
#endif

    private void UnhideAllPanelApps()
    {
        if (_actions.Config.HiddenPanelApps.Count == 0) return;
        _actions.Config.HiddenPanelApps.Clear();
        _actions.CommitSettings(notify: true);
    }

    private void BatchToggleMute(List<AudioAppInfo> targets, bool targetMute)
    {
        _actions.Host.SubmitPanelAudio(() =>
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
        _actions.CommitSettings(notify: true);
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
                    output = _actions.Host.CurrentApp.SelectOutputAsync((int)app.ProcessId, AudioService.SystemDefaultDeviceId, FeedbackKind.AppsWarning);
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
        var screenRect = WorldToScreen(_appRenameWorldRect);
        if (screenRect.IsEmpty) return;
        _aliasEditor.Open("apps.rename", screenRect, proc, device: false, displayName: GetAppDisplayLabel(_selectedApp));
    }

    private void BeginDeviceInlineRename(CanvasDeviceNode node)
    {
        var screenRect = WorldToScreen(new UiRect(node.X + 46f, node.Y + 8f, node.Width - 78f, 24f));
        if (screenRect.IsEmpty) return;
        _aliasEditor.Open("canvas.dev.rename:" + node.NodeKey, screenRect, node.DeviceId, device: true, displayName: node.PrimaryName);
    }

    private void ImportCustomIcon(AudioAppInfo app)
    {
        if (string.IsNullOrWhiteSpace(app.ProcessName)) return;
        string? path = NativeDialogs.Open(_actions.OwnerWindow.Handle, "PNG / JPEG / ICO / EXE|*.png;*.jpg;*.jpeg;*.ico;*.exe");
        if (!string.IsNullOrWhiteSpace(path))
            ImportCustomIconFromPath(app.ProcessName, path);
    }

    private void ImportCustomIconFromPath(string proc, string path)
    {
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

    private void ResetCustomIcon(AudioAppInfo app)
    {
        if (string.IsNullOrWhiteSpace(app.ProcessName)) return;
        string proc = app.ProcessName;
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

    // --- Routing Submission (reuses existing optimistic UI + serialized write + rollback) ---

    private void SubmitOutputsChange(AudioAppInfo app, string[] nextOutputs, string preferred, bool singleNativeSwitch)
    {
        if (app.Identity != _selectedIdentity || app.Identity != _outputChoicesIdentity || app.IsExternalOffline) return;

        var normalized = nextOutputs.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(IdCmp).ToArray();
        if (normalized.Length == 0) normalized = [AudioService.SystemDefaultDeviceId];

#if SONICROUTE_PHONE
        if (app.IsExternal && _actions.Host.Audio.ExternalAudio?.FindSession(app.ExternalSourceId) is AndroidAudioSession android && android.State.Device is { } sourceDevice)
        {
            bool OwnForcedOutput(string id) => AndroidOutputIdentity.TryParseDeviceId(id, out var key, out _)
                && string.Equals(key, AndroidDeviceIdentity.Key(sourceDevice), StringComparison.OrdinalIgnoreCase);
            if (normalized.Contains(AndroidAudioSession.SourcePhoneOutputId, IdCmp) && normalized.Any(OwnForcedOutput))
            {
                normalized = IdCmp.Equals(preferred, AndroidAudioSession.SourcePhoneOutputId)
                    ? normalized.Where(id => !OwnForcedOutput(id)).ToArray()
                    : normalized.Where(id => !IdCmp.Equals(id, AndroidAudioSession.SourcePhoneOutputId)).ToArray();
                if (normalized.Length == 0) normalized = [AudioService.SystemDefaultDeviceId];
            }
        }
#endif

        string effectivePrimary = PlaybackSelection.SelectPrimary(
            normalized,
            preferred,
            preferPhysical: !app.IsExternal,
            lowPriorityId: PlaybackSelection.LowPriorityOutputId(_actions.Host.Audio, app));

        _pendingOutputs = normalized;
        _pendingPrimary = effectivePrimary;
        _stateGen++;
        int editGen = ++_outputEditGen;
        ReconcileCanvasIfAppHeightChanged();
        SyncOutputPopupIfOpen();
        _actions.Invalidate();

        Task<ControlReply> write;
#if SONICROUTE_PHONE
        if (app.IsExternal)
            write = QueueExternalOutputs(app, normalized);
        else
#endif
        {
            int pid = (int)app.ProcessId;
            write = singleNativeSwitch && normalized.Length == 1
                ? _actions.Host.CurrentApp.SelectOutputAsync(pid, normalized[0], FeedbackKind.AppsWarning)
                : _actions.Host.CurrentApp.SetOutputsAsync(pid, effectivePrimary, normalized, FeedbackKind.AppsWarning);
        }
        _ = FinishOutputAsync(write, app.Identity, editGen);
    }

    private void ConnectOutputDevice(AudioAppInfo app, string deviceId)
    {
        bool isMultiExternal = PlaybackSelection.IsMultiOutputExternal(_actions.Host.Audio, app);
        if (app.IsExternal && !isMultiExternal)
        {
            SubmitOutputsChange(app, [deviceId], deviceId, singleNativeSwitch: true);
            return;
        }
        var current = DisplayedOutputs;
        if (current.Contains(deviceId, IdCmp)) return;
        string currentPrimary = GetDisplayedPrimaryOutput();
        string preferred = !app.IsExternal && PcRoutingApi.IsPhoneOutputDevice(currentPrimary) && !PcRoutingApi.IsPhoneOutputDevice(deviceId)
            ? deviceId
            : currentPrimary;
        SubmitOutputsChange(app, [.. current, deviceId], preferred, singleNativeSwitch: false);
    }

    private void DisconnectOutputDevice(AudioAppInfo app, string deviceId)
    {
        var current = DisplayedOutputs;
        if (!current.Contains(deviceId, IdCmp)) return;
        var remaining = current.Where(id => !IdCmp.Equals(id, deviceId)).ToArray();
        if (remaining.Length == 0)
        {
            if (AudioService.IsSystemDefault(deviceId)) return;
            SubmitOutputsChange(app, [AudioService.SystemDefaultDeviceId], AudioService.SystemDefaultDeviceId, singleNativeSwitch: true);
            return;
        }
        string currentPrimary = GetDisplayedPrimaryOutput();
        string preferred = IdCmp.Equals(currentPrimary, deviceId)
            ? PlaybackSelection.SelectPrimary(remaining, null, preferPhysical: !app.IsExternal,
                lowPriorityId: PlaybackSelection.LowPriorityOutputId(_actions.Host.Audio, app))
            : currentPrimary;
        SubmitOutputsChange(app, remaining, preferred, singleNativeSwitch: remaining.Length == 1 && !PcRoutingApi.IsPhoneOutputDevice(preferred));
    }

    private void SwitchNativeOutputDevice(AudioAppInfo app, string newNativeId)
    {
        if (!app.IsExternal && PcRoutingApi.IsPhoneOutputDevice(newNativeId))
        {
            // Phone output cannot be a Windows native policy endpoint; route to phone via existing exclusive/mirror flow.
            if (DisplayedOutputs.Length <= 1)
                SubmitOutputsChange(app, [newNativeId], newNativeId, singleNativeSwitch: true);
            else
                ConnectOutputDevice(app, newNativeId);
            return;
        }

        string? oldNative = GetDisplayedNativeOutputId(app);
        if (oldNative != null && IdCmp.Equals(oldNative, newNativeId)) return;
        var next = DisplayedOutputs.Where(id => !IdCmp.Equals(id, oldNative)).Append(newNativeId).Distinct(IdCmp).ToArray();
        SubmitOutputsChange(app, next, newNativeId, singleNativeSwitch: next.Length == 1);
    }

    private void RetargetForwardOutput(AudioAppInfo app, string oldForwardId, string newTargetId)
    {
        if (IdCmp.Equals(oldForwardId, newTargetId)) return;
        string? nativeId = GetDisplayedNativeOutputId(app);
        if (nativeId != null && IdCmp.Equals(nativeId, newTargetId))
        {
            // Dropping forward onto existing native simply disconnects the old forward.
            DisconnectOutputDevice(app, oldForwardId);
            return;
        }
        var next = DisplayedOutputs.Where(id => !IdCmp.Equals(id, oldForwardId)).Append(newTargetId).ToArray();
        string preferred = nativeId ?? GetDisplayedPrimaryOutput();
        SubmitOutputsChange(app, next, preferred, singleNativeSwitch: false);
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

    private const string OutputReadbackMismatch = "error: output_readback_mismatch";

    private async Task FinishOutputAsync(Task<ControlReply> write, string identity, int generation)
    {
        string? error = null;
        try
        {
            var reply = await write.ConfigureAwait(false);
            if (reply.Text.StartsWith("error", StringComparison.OrdinalIgnoreCase)) error = reply.Text;
        }
        catch (Exception ex) { error = ex.Message; }
        bool readbackMismatch = error?.StartsWith(OutputReadbackMismatch, StringComparison.OrdinalIgnoreCase) == true;
        if (readbackMismatch) ExceptionDiagnostics.Record("Native.Apps.OutputReadback", new InvalidOperationException(error));
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
            _stateGen++;
            if (!_active) return;
            if (error != null) _actions.ShowNotice(readbackMismatch ? FormatOutputReadbackMismatch(error) : error);
            ReconcileCanvasIfAppHeightChanged();
            SyncOutputPopupIfOpen();
            _actions.Invalidate();
            TriggerRefresh(forceAppsRefresh: false);
        });
    }

    private string FormatOutputReadbackMismatch(string error)
    {
        const string actualPrefix = OutputReadbackMismatch + ", actual=";
        if (error.StartsWith(actualPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string actual = error[actualPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(actual))
            {
                string label = AudioService.IsSystemDefault(actual)
                    ? L10n.T("Dev.SystemDefaultOut") : DeviceLabel(actual, Flow.Render);
                if (!IdCmp.Equals(actual, label))
                    return string.Format(L10n.T("Ov.OutputReadbackMismatchActual"), label);
            }
        }
        return L10n.T("Ov.OutputReadbackMismatch");
    }

    private string GetDisplayedPrimaryOutput() => _pendingPrimary ?? _nativeOutput;

    private string? GetDisplayedNativeOutputId(AudioAppInfo app)
    {
        string primary = GetDisplayedPrimaryOutput();
        if (!DisplayedOutputs.Contains(primary, IdCmp)) return null;
        if (!app.IsExternal && PcRoutingApi.IsPhoneOutputDevice(primary)) return null;
        return primary;
    }

    private void SelectInputRoute(string deviceId)
    {
        if (!_actions.Config.ExperimentalMic || _selectedApp == null || _selectedApp.IsExternal
            || _outputChoicesIdentity != _selectedIdentity || IdCmp.Equals(deviceId, DisplayedInput)) return;
        int pid = (int)_selectedApp.ProcessId;
        string identity = _selectedApp.Identity;
        int generation = ++_inputEditGen;
        _stateGen++;
        _pendingInput = deviceId;
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
            if (error == null && _pendingInput != null) _nativeInput = _pendingInput;
            _pendingInput = null;
            _stateGen++;
            if (!_active) return;
            if (error != null) _actions.ShowNotice(error);
            _actions.Invalidate();
            TriggerRefresh(forceAppsRefresh: false);
        });
    }

    private void ToggleSelectedAppMute()
    {
        if (_selectedApp is not { IsExternalOffline: false } app || _selectedVolume < 0) return;
        _actions.Host.SubmitPanelAudio(() =>
        {
            int failed = 0;
            try
            {
                if (_actions.Host.Audio.Volume(app, toggle: true).Count == 0) failed++;
            }
            catch (Exception ex)
            {
                ExceptionDiagnostics.Record("Native.Apps.Mute", ex);
                failed++;
            }
            CompleteAudioBatch(1, failed);
        });
    }

    private void ApplyAppSliderVolume(int percent)
    {
        if (_selectedApp is not { IsExternalOffline: false } || _selectedVolume < 0) return;
        _selectedVolume = percent;
        _actions.Host.QueueAppVolume(_selectedApp, percent, FeedbackKind.AppsWarning);
        _actions.Invalidate();
    }

    private void ApplyForwardVolume(string targetDeviceId, int percent) =>
        ChangeForwardGain(targetDeviceId, percent, toggleMute: false);

    private void ToggleForwardMute(string targetDeviceId) =>
        ChangeForwardGain(targetDeviceId, null, toggleMute: true);

    private void ChangeForwardGain(string targetDeviceId, int? percent, bool toggleMute)
    {
        if (_selectedApp is not { IsExternalOffline: false } app || _outputChoicesIdentity != app.Identity
            || !DisplayedOutputs.Contains(targetDeviceId, IdCmp)
            || IdCmp.Equals(GetDisplayedNativeOutputId(app), targetDeviceId)) return;
        _mirrorGainRevision++;
        _mirrorGains[targetDeviceId] = _actions.Host.Audio.SetMirrorGain(app, targetDeviceId, volumePercent: percent, toggleMute: toggleMute);
        _actions.Invalidate();
    }

    private void ApplyDeviceSliderVolume(CanvasDeviceNode node, int percent)
    {
        if (node.IsPhoneOutput || !node.IsAvailable) return;
        SetLocalDeviceVolumeAndMirrorDefault(node, percent, muted: null);
        _actions.Host.QueueEndpointVolume(node.Flow, node.DeviceId, percent);
        _actions.Invalidate();
    }

    private void ToggleDeviceMute(CanvasDeviceNode node)
    {
        if (node.IsPhoneOutput || !node.IsAvailable) return;
        var flow = node.Flow;
        string devId = node.DeviceId;
        _actions.Host.SubmitPanelAudio(() =>
        {
            try
            {
                var st = _actions.Host.Audio.Volume(flow, devId, 0, toggle: true);
                _actions.Host.TryPost(() =>
                {
                    if (!_active) return;
                    if (_deviceNodes.TryGetValue(DeviceNodeKey(flow, devId), out var live))
                        SetLocalDeviceVolumeAndMirrorDefault(live, volume: null, muted: st.Muted);
                    _actions.Host.NotifyAudioVisualChanged();
                    _actions.Invalidate();
                });
            }
            catch
            {
                _actions.Host.PostContinuation(_actions.Host.ReportAudioWriteFailure);
            }
        });
    }

    private void SetLocalDeviceVolumeAndMirrorDefault(CanvasDeviceNode node, int? volume, bool? muted)
    {
        void Apply(CanvasDeviceNode target)
        {
            if (volume.HasValue) target.Volume = Math.Clamp(volume.Value, 0, 100);
            if (muted.HasValue) target.Muted = muted.Value;
            _deviceVolumes[target.NodeKey] = (target.Volume, target.Muted, target.IsAvailable);
        }
        Apply(node);
        // Keep SystemDefault node and the actual Windows default physical device node synchronized!
        string sysId = node.Flow == Flow.Render ? AudioService.SystemDefaultDeviceId : AudioService.SystemDefaultInputDeviceId;
        string? actualDefId = node.Flow == Flow.Render ? _windowsDefaultRenderId : _windowsDefaultCaptureId;
        if (node.IsSystemDefault && actualDefId != null && _deviceNodes.TryGetValue(DeviceNodeKey(node.Flow, actualDefId), out var phys))
            Apply(phys);
        else if (node.IsActualWindowsDefault && _deviceNodes.TryGetValue(DeviceNodeKey(node.Flow, sysId), out var sysNode))
            Apply(sysNode);
    }

    internal void ConfirmAppVolume(string identity, int actual)
    {
        if (!_active || _selectedApp?.Identity != identity || _interaction == InteractionMode.DraggingAppVolume || _selectedVolume == actual) return;
        _selectedVolume = actual;
        _actions.Invalidate();
    }

    internal void ConfirmEndpointVolume(Flow flow, string endpointId, int actual)
    {
        if (!_active) return;
        string key = DeviceNodeKey(flow, endpointId);
        if (_interaction == InteractionMode.DraggingDeviceVolume && IdCmp.Equals(_dragDeviceNodeKey, key)) return;
        if (_deviceNodes.TryGetValue(key, out var node) && node.Volume != actual)
        {
            SetLocalDeviceVolumeAndMirrorDefault(node, actual, muted: null);
            _actions.Invalidate();
        }
    }

    private void ToggleDisableAutoSwitch()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        var list = _actions.Config.DisabledAutoSwitchApps;
        if (list.RemoveAll(x => IdCmp.Equals(x, proc)) == 0) list.Add(proc);
        _actions.CommitSettings(notify: true);
    }

    private void ToggleShowInQuickPanel()
    {
        if (_selectedApp == null || string.IsNullOrWhiteSpace(_selectedApp.ProcessName)) return;
        string proc = _selectedApp.ProcessName!;
        var list = _actions.Config.HiddenPanelApps;
        if (list.RemoveAll(x => IdCmp.Equals(x, proc)) == 0) list.Add(proc);
        _actions.CommitSettings(notify: true);
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

    private static string GetStableAppKey(AudioAppInfo app)
    {
        if (!string.IsNullOrWhiteSpace(app.ExternalPreferenceKey)) return app.ExternalPreferenceKey!;
        if (!string.IsNullOrWhiteSpace(app.ProcessName)) return "proc:" + app.ProcessName!.Trim();
        return "label:" + app.Label;
    }

    private static string DeviceNodeKey(Flow flow, string id) =>
        (flow == Flow.Capture ? "capture:" : "render:") + id;

    private string DeviceLabel(string? id, Flow flow, string? fallback = null)
    {
        if (id != null && (flow == Flow.Render || !AudioService.IsSystemDefault(id))
            && _actions.Config.DeviceNames.TryGetValue(id, out string? custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        if (string.IsNullOrWhiteSpace(id) || AudioService.IsSystemDefault(id))
            return L10n.T(flow == Flow.Render ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn");

        var list = flow == Flow.Render ? _outputs : _inputs;
        return fallback ?? list.FirstOrDefault(e => IdCmp.Equals(e.Id, id))?.Name ?? id;
    }

    private static bool TryExtractExeIconBgra(string path, out byte[] pixels, out int width, out int height)
    {
        if (!NativeAppIconService.TryExtractFileIcon(path, out var bitmap, premultiply: false))
        {
            pixels = [];
            width = height = 0;
            return false;
        }

        width = height = AppIconFiles.IconSize;
        pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int sourceY = y * bitmap.Height / height;
            for (int x = 0; x < width; x++)
            {
                int sourceX = x * bitmap.Width / width;
                bitmap.BgraPixels.AsSpan((sourceY * bitmap.Width + sourceX) * 4, 4)
                    .CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
            }
        }
        return true;
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
        _wireCache.Dispose();
        _aliasEditor.Dispose();
    }
}
