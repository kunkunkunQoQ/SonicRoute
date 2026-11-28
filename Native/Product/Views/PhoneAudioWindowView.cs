#if SONICROUTE_PHONE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.AirPlay;
using SonicRoute.AndroidAudio;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.Native.Graphics;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// Companion Phone Audio &amp; App Launcher window anchored beside QuickPanelWindow (235 DIP wide).
/// Exact parity with PhoneAudioWindow.xaml + PhoneCompanionLayout.
/// </summary>
internal sealed class PhoneAudioWindowView : LayeredWindow
{
    private const float MediaCardHeight = 64f + 20f + 2f * (5f + 1f);
    private const string ReturnToPhone = "companion.drop.return";
    private const string DisconnectAirPlay = "companion.drop.airplay";
    private const string AppPrefix = "companion.app:";
    private const string AirPlayChoiceKey = "__AIRPLAY__";

    private sealed record DropZoneTarget(string DeviceId, UiRect Bounds, string Title, string Description, AndroidOutputSinkKind Kind);

    private readonly ApplicationHost _host;
    private readonly AppConfig _config;
    private readonly Action? _onDeactivatedCheck;
    private readonly UiHitMap _hit = new();
    private readonly UiScrollState _scroll = new();
    private readonly InlineEditHost _inlineEdit = new();
    private readonly AirPlayReceiverViewControl _airPlay;

    private readonly PhoneAppIconCache _appIcons;

    private readonly List<DropZoneTarget> _dropZones = new();
    private UiRect _dropAreaRect;
    private string? _externalDragHoverZone;
    private string? _dropStatusMessage;
    private long _commandVersion;
    private IDisposable? _dragRegistration;
    private bool _dragSubscribed;

    private Action<string>? _inlineChanged;
    private Action<string, bool>? _inlineCommitted;

    private UiThemePalette _theme;
    private ComboPopupWindow? _comboPopup;
    private bool _airPlaySelected;
    private string _searchText = "";
    private IReadOnlyList<AndroidApp>? _filteredAppsSource;
    private string? _filteredAppsQuery;
    private string? _filteredGrant;
    private AndroidApp? _grantApp;
    private bool _filteredAppChannel;
    private AndroidApp[] _filteredApps = [];
    private string? _activeProfileId;
    private AndroidDevice? _activeDevice;
    private QuickPanelWindow? _panel;
    private AndroidDevice? _pressDevice;
    private AndroidApp? _pressApp;
    private float _pressX, _pressY;
    private DragIconWindow? _dragIcon;
    private bool _returnHovered;
    private string? _selectedPackage;
    private string? _pressAirPlayId;
    private int _seekKey;
    private IPhoneMediaSession? _mediaClient;
    private AndroidMediaState? _mediaState;
    private AndroidConnectionQuality? _connectionQuality;
    private CancellationTokenSource? _mediaCts;
    private Task? _mediaWatch;

    // Media seek bar & background artwork decoding
    private bool _seeking;
    private double _seekPositionMs;
    private string? _seekPackage;
    private UiRect _mediaTitleRect;
    private CancellationTokenSource? _catalogRefresh;
    private CancellationTokenSource? _openingReconnect;
    private string? _refreshError;
    private string? _seekTitle;
    private UiRect _seekSliderRect;
    private bool _seekTimerRunning;

    private byte[]? _lastArtPng;
    private byte[]? _artBgra;
    private int _artW, _artH, _artVer, _artGen;
    private bool _wired;
    private int _phoneChangePending;

    internal bool HasOpenModal => (_comboPopup is { IsVisible: true }) || _inlineEdit.IsOpen;

    internal PhoneAudioWindowView(
        ApplicationHost host,
        AppConfig config,
        Action? onDeactivatedCheck = null)
        : base(UiMetrics.PhoneCompanionWidth, Math.Clamp(config.QuickPanelHeight, 350, 850), UiMetrics.PhoneCompanionWidth, 350f)
    {
        _host = host;
        _config = config;
        _appIcons = new PhoneAppIconCache(host, 64, Invalidate);
        _onDeactivatedCheck = onDeactivatedCheck;
        _theme = UiThemePalette.Resolve(config);
        TopMost = true;
        ToolWindow = true;
        NoActivate = false;

        _airPlay = new AirPlayReceiverViewControl(
            _host,
            _config,
            Invalidate,
            ShowCombo,
            (tag, rect, value, changed, committed) => StartInlineEdit(tag, rect, value, changed, committed, 50),
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
            else
            {
                _inlineChanged?.Invoke(text);
            }
        };
        _inlineEdit.Committed += (tag, text, viaEnter) =>
        {
            var onCommit = _inlineCommitted;
            _inlineChanged = null;
            _inlineCommitted = null;
            if (tag == "companion.search")
            {
                _searchText = text;
                Invalidate();
            }
            else
            {
                onCommit?.Invoke(text, viaEnter);
            }
        };
    }

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        if (_inlineEdit.IsOpen) _inlineEdit.SetTheme(theme.SurfaceBg, theme.TextPrimary);
        if (IsVisible) Invalidate();
    }

    internal void ShowForOwner(QuickPanelWindow owner)
    {
        _panel = owner;
        OwnerHandle = owner.Handle;
        _theme = UiThemePalette.Resolve(_config);
        EnsureCreated(L10n.T("Phone.AudioWindowTitle"));
        RepositionForOwner(owner);
        owner.ApplyCompanionEntrance(this);
        if (!IsVisible) Show(activate: false);
        AttachPhone();
        AttachCrossDeviceDrag();
        RefreshData();
        UpdateSeekAnimationTimer();
    }

    internal void RepositionForOwner(LayeredWindow owner, bool deferPresentation = false)
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

        SetPanelBoundsPx((int)MathF.Round(x * s), (int)MathF.Round(y * s), width, height, deferPresentation);
    }

    internal void RefreshData()
    {
        var phone = _host.Phone;
        var profile = phone.Store.Profile;
        if (_activeProfileId != profile.Id)
        {
            _activeProfileId = profile.Id;
            CancelCatalogRefresh();
            ClearFilteredApps();
            _dropStatusMessage = null;
        }
        _appIcons.SetProfile(profile.Id);
        var online = profile.LastDevice is { } last ? phone.Connections.Get(last).Device : null;
        if (IsVisible && _airPlaySelected) _airPlay.Attach();
        else _airPlay.Detach();
        if (_airPlaySelected)
        {
            _activeDevice = online;
            StopMediaWatch();
        }
        else if (!Equals(online, _activeDevice))
        {
            _refreshError = null;
            if (!AndroidDeviceIdentity.Matches(online, _activeDevice))
            {
                ++_commandVersion;
                _selectedPackage = null;
                _dropStatusMessage = null;
                CancelAppDrag();
                CancelKeyboardSeek();
            }
            _activeDevice = online;
            _connectionQuality = null;
            RestartMediaWatch(online);
        }
        else if (online != null && phone.Operations.CanWatchMedia(online) && _mediaCts == null && IsVisible)
        {
            RestartMediaWatch(online);
        }
        else if (online is { IsApp: true } && _mediaCts != null && !phone.Operations.CanWatchMedia(online))
        {
            StopMediaWatch();
        }

        RebuildDropZones(WidthDip, HeightDip);
        UpdateSeekAnimationTimer();
        if (IsVisible) Invalidate();
    }

    internal string? HitTestScreenDropZone(int screenXPx, int screenYPx)
    {
        if (!IsVisible) return null;
        var drag = CrossDeviceDragHost.DraggedApp;
        float x = (screenXPx - LeftPx) / DpiScale;
        float y = (screenYPx - TopPx) / DpiScale;
        if (_airPlaySelected)
            return drag?.ProcessName == "airplay:receiver" && AirPlayDropBounds(WidthDip, HeightDip).Contains(x, y) ? DisconnectAirPlay : null;
        if (drag?.ProcessName?.StartsWith("android:", StringComparison.Ordinal) == true)
            return OverReturnTarget(screenXPx, screenYPx) ? ReturnToPhone : null;
        EnsureDropGeometry();
        if (_dropZones.Count == 0 || _dropAreaRect.IsEmpty) return null;

        float scale = DpiScale > 0f ? DpiScale : 1f;
        float xDip = (screenXPx - LeftPx) / scale;
        float yDip = (screenYPx - TopPx) / scale;
        if (xDip < _dropAreaRect.X || xDip > _dropAreaRect.Right || yDip < _dropAreaRect.Y || yDip > _dropAreaRect.Bottom)
            return null;

        for (int i = 0; i < _dropZones.Count; i++)
        {
            if (_dropZones[i].Bounds.Contains(xDip, yDip))
                return _dropZones[i].DeviceId;
        }

        // Clamp to nearest zone vertically when inside the drop overlay padding
        int best = 0;
        float bestDist = float.MaxValue;
        for (int i = 0; i < _dropZones.Count; i++)
        {
            float dist = MathF.Abs(yDip - _dropZones[i].Bounds.CenterY);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        return _dropZones[best].DeviceId;
    }

    internal void HandleExternalAppDrop(AudioAppInfo app, string targetDeviceId)
    {
        _externalDragHoverZone = null;
        if (string.IsNullOrWhiteSpace(targetDeviceId))
        {
            if (IsVisible) Invalidate();
            return;
        }
        if (targetDeviceId == ReturnToPhone)
        {
            if (app.ExternalSourceId is { } id && _host.Phone.Sources.FindSource(id) is { } source)
                _ = CrossDeviceDragHost.FinishAndroidDropAsync(_host, source.Device, source.App, null, 0, 0, returnToPhone: true);
        }
        else if (targetDeviceId == DisconnectAirPlay)
        {
            if (_host.HasAirPlay && _host.AirPlay.Source is { } air && air.Id == app.ExternalSourceId)
                _host.AirPlay.Disconnect();
        }
        else _ = RouteExternalAppAsync(app, targetDeviceId);
        if (IsVisible) Invalidate();
    }

    private Task RouteExternalAppAsync(AudioAppInfo app, string targetDeviceId) => RunCompanionCommand(async () =>
    {
        ControlReply reply = app.IsExternal
            ? await _host.Audio.SetExternalOutputsAsync(app, [targetDeviceId]).ConfigureAwait(false)
            : await _host.CurrentApp.SelectOutputAsync((int)app.ProcessId, targetDeviceId, FeedbackKind.PanelWarning).ConfigureAwait(false);
        if (reply.Text.StartsWith("error:", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(reply.Text);
    });

    private async Task RunCompanionCommand(Func<Task> operation, Action? completed = null)
    {
        long request = ++_commandVersion;
        string? error = null;
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            ExceptionDiagnostics.Record("PhoneCompanion.Command", ex);
            error = PhonePreview.TranslateError(ex);
        }
        _host.TryPost(() =>
        {
            if (request != _commandVersion || !IsVisible) return;
            _dropStatusMessage = error;
            if (error == null) completed?.Invoke();
            Invalidate();
        });
    }

    protected override void OnShown()
    {
        base.OnShown();
        AttachPhone();
        AttachCrossDeviceDrag();
        if (_airPlaySelected) _airPlay.Attach();
        if (!_airPlaySelected && _activeDevice != null && _mediaCts == null)
            RestartMediaWatch(_activeDevice);
        UpdateSeekAnimationTimer();
    }

    protected override void OnHidden()
    {
        CancelCatalogRefresh();
        ++_commandVersion;
        CancelAppDrag();
        _seekKey = 0;
        StopSeekTimer();
        _seeking = false;
        _inlineEdit.Close(commit: true, viaEnter: false);
        _inlineChanged = null;
        _inlineCommitted = null;
        ComboPopupWindow.Close(ref _comboPopup);
        StopMediaWatch();
        ClearArtworkState();
        ClearFilteredApps();
        _appIcons.Clear();
        _airPlay.Detach();
        DetachCrossDeviceDrag();
        DetachPhone();
        base.OnHidden();
    }

    protected override void OnClosed()
    {
        CancelCatalogRefresh();
        ++_commandVersion;
        CancelAppDrag();
        _seekKey = 0;
        StopSeekTimer();
        _seeking = false;
        _inlineEdit.Dispose();
        _inlineChanged = null;
        _inlineCommitted = null;
        ComboPopupWindow.Close(ref _comboPopup);
        StopMediaWatch();
        ClearArtworkState();
        ClearFilteredApps();
        _appIcons.Clear();
        _airPlay.Detach();
        DetachCrossDeviceDrag();
        DetachPhone();
        base.OnClosed();
    }

    protected override void OnTickAnimation()
    {
        if (!IsVisible || _airPlaySelected || _seeking || _mediaState is not { Playing: true, DurationMs: > 0, Speed: > 0 })
        {
            StopSeekTimer();
            return;
        }
        Invalidate();
    }

    protected override void OnCaptureLost()
    {
        CancelAppDrag();
        bool changed = false;
        if (_seeking)
        {
            changed = true;
            _seeking = false;
            UpdateSeekAnimationTimer();
        }
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            changed = true;
        }
        if (_hit.ActiveId != null)
        {
            _hit.ActiveId = null;
            changed = true;
        }
        if (changed && IsVisible) Invalidate();
    }

    private void AttachPhone()
    {
        if (_wired) return;
        _wired = true;
        var phone = _host.Phone;
        phone.Changed += OnPhoneChanged;
        phone.StartTracking();
        var profile = phone.Store.Profile;
        if (!_airPlaySelected && profile is { LastDevice: not null, ManuallyDisconnected: false })
        {
            _openingReconnect = new CancellationTokenSource();
            _ = phone.RefreshInBackgroundAsync(profile.Id, _openingReconnect.Token);
        }
        else _ = phone.RefreshInBackgroundAsync();
    }

    private void DetachPhone()
    {
        CancelOpeningReconnect();
        if (!_wired) return;
        _wired = false;
        _host.Phone.Changed -= OnPhoneChanged;
    }

    private void CancelOpeningReconnect()
    {
        if (_openingReconnect is not { } reconnect) return;
        _openingReconnect = null;
        reconnect.Cancel();
        reconnect.Dispose();
    }

    private void AttachCrossDeviceDrag()
    {
        _dragRegistration ??= CrossDeviceDragHost.RegisterTarget(HitTestScreenDropZone, HandleExternalAppDrop);
        if (!_dragSubscribed)
        {
            _dragSubscribed = true;
            CrossDeviceDragHost.StateChanged += OnCrossDeviceDragStateChanged;
        }
    }

    private void DetachCrossDeviceDrag()
    {
        _dragRegistration?.Dispose();
        _dragRegistration = null;
        if (_dragSubscribed)
        {
            _dragSubscribed = false;
            CrossDeviceDragHost.StateChanged -= OnCrossDeviceDragStateChanged;
        }
        _externalDragHoverZone = null;
    }

    private void OnCrossDeviceDragStateChanged()
    {
        _host.TryPost(() =>
        {
            if (!IsVisible) return;
            _externalDragHoverZone = CrossDeviceDragHost.IsDragging ? CrossDeviceDragHost.ActiveZone : null;
            EnsureDropGeometry();
            Invalidate();
        });
    }

    private void OnPhoneChanged()
    {
        if (Interlocked.Exchange(ref _phoneChangePending, 1) != 0) return;
        if (!_host.TryPost(() =>
        {
            Volatile.Write(ref _phoneChangePending, 0);
            if (_wired && IsVisible) RefreshData();
        })) Volatile.Write(ref _phoneChangePending, 0);
    }

    private void RestartMediaWatch(AndroidDevice? device)
    {
        StopMediaWatch();
        if (device == null || !_host.Phone.Operations.CanWatchMedia(device) || !IsVisible || _airPlaySelected) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_host.Phone.Lifetime, _host.Phone.Connections.OperationToken(device));
        _mediaCts = cts;
        var client = _host.Phone.Operations.OpenMediaSession(device);
        _mediaClient = client;

        client.ConnectionQualityChanged += quality =>
        {
            _host.TryPost(() =>
            {
                if (!ReferenceEquals(_mediaClient, client) || !IsVisible) return;
                _connectionQuality = quality;
                Invalidate();
            });
        };

        var token = cts.Token;
        _mediaWatch = Task.Run(async () =>
        {
            try
            {
                await client.WatchAsync(device, state =>
                {
                    _host.TryPost(() =>
                    {
                        if (!ReferenceEquals(_mediaCts, cts)) return;
                        _mediaState = state;
                        UpdateSeekAnimationTimer();
                        if (IsVisible && !_airPlaySelected) Invalidate();
                    });
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { ExceptionDiagnostics.Record("PhoneCompanion.MediaWatch", ex); }
            finally
            {
                if (!token.IsCancellationRequested) _host.TryPost(() =>
                {
                    if (!ReferenceEquals(_mediaCts, cts)) return;
                    _mediaState = null;
                    _connectionQuality = null;
                    UpdateSeekAnimationTimer();
                    if (IsVisible) Invalidate();
                });
            }
        });
    }

    private void StopMediaWatch()
    {
        _seekKey = 0;
        StopSeekTimer();
        _seeking = false;
        var cts = _mediaCts;
        _mediaCts = null;
        _mediaWatch = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        _mediaClient = null;
        _mediaState = null;
        _connectionQuality = null;
    }

    private void ClearArtworkState()
    {
        Interlocked.Increment(ref _artGen);
        _lastArtPng = null;
        _artBgra = null;
        _artW = 0;
        _artH = 0;
    }

    private void UpdateSeekAnimationTimer()
    {
        bool shouldRun = IsVisible && !_airPlaySelected && !_seeking
            && _mediaState is { Playing: true, DurationMs: > 0, Speed: > 0 };
        if (shouldRun && !_seekTimerRunning)
        {
            _seekTimerRunning = true;
            StartAnimationTimer(1000);
        }
        else if (!shouldRun && _seekTimerRunning)
        {
            StopSeekTimer();
        }
    }

    private void StopSeekTimer()
    {
        if (!_seekTimerRunning) return;
        _seekTimerRunning = false;
        StopAnimationTimer();
    }

    protected override string GetWindowTitle() => L10n.T("Phone.AudioWindowTitle");

    private const float PanelPaddingX = 9f;
    private const float PanelPaddingY = 13f;
    private UiRect AirPlayDropBounds(float w, float h)
    {
        float width = w - PanelPaddingX * 2f;
        float height = _airPlay.MeasureHeight(width - 22f, showHeaderStatus: false) + 22f;
        return new UiRect(PanelPaddingX, PanelPaddingY, width, MathF.Min(height, MathF.Max(1f, h - PanelPaddingY * 2f - 42f)));
    }

    protected override int HitTestNonClient(float xDip, float yDip) =>
        !_airPlaySelected && _mediaTitleRect.Contains(xDip, yDip) ? 2 : 1;

    private static UiRect ComputeDropAreaRect(float widthDip, float heightDip, bool airPlaySelected)
    {
        if (airPlaySelected) return default;
        const float padX = PanelPaddingX;
        const float padY = PanelPaddingY;
        float contentW = widthDip - padX * 2f;
        float footerH = 36f;
        float footerY = heightDip - padY - footerH;
        float listY = padY + MediaCardHeight + 6f + 28f + 6f;
        return new UiRect(padX, listY, contentW, MathF.Max(30f, footerY - 6f - listY));
    }

    private void EnsureDropGeometry()
    {
        if (_dropAreaRect != ComputeDropAreaRect(WidthDip, HeightDip, _airPlaySelected))
            RebuildDropZones(WidthDip, HeightDip);
    }

    private void RebuildDropZones(float widthDip, float heightDip)
    {
        _dropZones.Clear();
        _dropAreaRect = ComputeDropAreaRect(widthDip, heightDip, _airPlaySelected);
        if (_airPlaySelected || _dropAreaRect.IsEmpty) return;

        var phone = _host.Phone;
        var profile = phone.Store.Profile;
        var online = profile.LastDevice is { } last ? phone.Connections.Get(last).Device : null;
        if (online == null) return;

        string phoneKey = AndroidDeviceIdentity.Key(online);
        IReadOnlyList<AndroidOutputSink> sinks = profile.OutputSinks is { Count: > 0 } configured
            ? AndroidOutputSink.NormalizeList(configured)
            : [AndroidOutputSink.DefaultSpeaker];

        var entries = new List<(AndroidOutputSink Sink, string DeviceId)>(sinks.Count + 1)
        {
            (AndroidOutputSink.Current, AndroidOutputIdentity.FormatDeviceId(phoneKey, AndroidOutputSink.Current.Key))
        };
        foreach (var sink in sinks)
            entries.Add((sink, AndroidOutputIdentity.FormatDeviceId(phoneKey, sink.Key)));

        float innerPad = 4f;
        float gap = 4f;
        float zoneX = _dropAreaRect.X + innerPad;
        float zoneW = MathF.Max(40f, _dropAreaRect.Width - innerPad * 2f);
        float availH = MathF.Max(40f, _dropAreaRect.Height - innerPad * 2f - gap * Math.Max(0, entries.Count - 1));
        float zoneH = availH / entries.Count;
        string phoneName = phone.Store.GetProfileDisplayName(profile);

        for (int i = 0; i < entries.Count; i++)
        {
            var (sink, deviceId) = entries[i];
            string targetName = PhoneAppPresentation.OutputLabel(sink, phoneName);
            string title = sink.Kind == AndroidOutputSinkKind.Speaker
                ? string.Format(L10n.T("Phone.PcDropOnlyOnSpeakerTitle"), targetName)
                : string.Format(L10n.T("Phone.PcDropOnlyOnTargetTitle"), targetName);
            string desc = sink.Kind != AndroidOutputSinkKind.Bluetooth
                ? string.Format(L10n.T("Phone.PcDropOnlyOnSpeakerDesc"), targetName)
                : L10n.T("Phone.PcDropOnlyOnBtDesc");

            var rect = new UiRect(zoneX, _dropAreaRect.Y + innerPad + i * (zoneH + gap), zoneW, zoneH);
            _dropZones.Add(new DropZoneTarget(deviceId, rect, title, desc, sink.Kind));
        }
    }

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        _scroll.TrackRect = default;
        var outer = new UiRect(0f, 0f, widthDip, heightDip);
        g.FillRoundedRect(outer, UiMetrics.QuickPanelCornerRadius, _theme.WindowBgAlpha);
        g.DrawRoundedRect(outer, UiMetrics.QuickPanelCornerRadius, _theme.Border, 1f);

        const float padX = PanelPaddingX;
        const float padY = PanelPaddingY;
        float contentW = widthDip - padX * 2f;
        float footerH = 36f;
        float footerY = heightDip - padY - footerH;

        var phone = _host.Phone;
        var profile = phone.Store.Profile;
        var online = _activeDevice;


        if (_airPlaySelected)
        {
            // AirPlay mode isolation: hide Android media card & search box, fill upper area with AirPlayPane
            var airBox = AirPlayDropBounds(widthDip, heightDip);
            if (CrossDeviceDragHost.DraggedApp?.ProcessName == "airplay:receiver")
            {
                DrawReturnOverlay(g, airBox, _externalDragHoverZone == DisconnectAirPlay, airPlay: true);
                RenderFooter(g, padX, footerY, contentW, footerH, phone, profile, online);
                return;
            }
            UiPrimitives.DrawCard(g, _theme, airBox, 10f);
            var inner = airBox.Deflate(11f);
            float airH = _airPlay.MeasureHeight(inner.Width, showHeaderStatus: false);
            _scroll.ViewportHeight = inner.Height;
            _scroll.ContentHeight = airH;
            _scroll.Clamp();

            g.PushClip(inner);
            _hit.PushClip(inner);
            _airPlay.Render(g, _theme, _hit, new UiRect(inner.X, inner.Y - _scroll.Offset, inner.Width - (_scroll.MaxOffset > 0.5f ? 8f : 0f), airH), showHeaderStatus: false);
            _hit.PopClip();
            g.PopClip();
            _scroll.Draw(g, _theme, _hit, "companion.scroll", inner);

            RenderFooter(g, padX, footerY, contentW, footerH, phone, profile, online);
            return;
        }

        // Android media card and seek bar
        float y = padY;
        var mediaCard = new UiRect(padX, y, contentW, MediaCardHeight);
        UiPrimitives.DrawCard(g, _theme, mediaCard, 10f);

        var artRect = new UiRect(mediaCard.X + 7f, mediaCard.Y + 10f, 56f, 56f);
        g.FillRoundedRect(artRect, 8f, _theme.SurfaceBg);
        g.DrawRoundedRect(artRect, 8f, _theme.Border, 1f);

        var mediaApp = _mediaState == null ? null
            : profile.RetainedApps.FirstOrDefault(a => a.Package == _mediaState.Package);
        if (mediaApp == null && _mediaState?.Package is { Length: > 0 } activePackage && online is { IsApp: true })
            mediaApp = new AndroidApp(activePackage, activePackage, 0, "");
        if (mediaApp != null) mediaApp = _appIcons.Resolve(online, mediaApp);

        bool hasArtwork = TryGetArtwork(_mediaState?.ArtworkPng, out var artBgra, out int aw, out int ah, out int aver);
        if (hasArtwork)
        {
            g.DrawBgraBitmap("companion.art", artBgra, aw, ah, artRect.Deflate(1f), version: aver);
            if (mediaApp != null && _appIcons.TryGet(mediaApp, out var bBgra, out int bw, out int bh, out int bver))
            {
                var badgeBox = new UiRect(artRect.Right - 17f, artRect.Bottom - 17f, 16f, 16f);
                g.FillRoundedRect(badgeBox, 4f, _theme.WindowBg);
                g.DrawRoundedRect(badgeBox, 4f, _theme.Border, 0.8f);
                g.DrawBgraBitmap($"companion.artbadge:{mediaApp.Package}", bBgra, bw, bh, badgeBox.Deflate(1.5f), version: bver);
            }
        }
        else if (mediaApp != null && _appIcons.TryGet(mediaApp, out var fallbackBgra, out int fw, out int fh, out int fver))
        {
            g.DrawBgraBitmap($"companion.articon:{mediaApp.Package}", fallbackBgra, fw, fh, artRect.Deflate(4f), version: fver);
        }
        else
        {
            g.DrawText("♫", artRect, _theme.TextSecondary, 22f, 400, UiTextAlign.Center, UiVertAlign.Center);
        }

        float infoX = artRect.Right + 6f;
        float infoW = MathF.Max(20f, mediaCard.Right - 6f - infoX);
        string mediaTitle = _mediaState is { HasSession: true } ms && ms.Title.Length > 0
            ? ms.Title
            : (_mediaState is { HasSession: true } ? mediaApp?.Name ?? _mediaState.Package : L10n.T("Phone.MediaDefaultTitle"));
        string mediaSub = L10n.T("Phone.MediaNonePlaying");
        if (_mediaState is { HasSession: true } session)
        {
            mediaSub = session.Artist.Length > 0 ? session.Artist : L10n.T(session.Playing ? "Phone.MediaPlaying" : "Phone.MediaPaused");
            if (mediaApp != null) mediaSub += " · " + mediaApp.Name;
        }
        _mediaTitleRect = new UiRect(infoX, mediaCard.Y + 5f, infoW, 18f);
        g.DrawText(mediaTitle, _mediaTitleRect, _theme.TextPrimary, 13f, 600);
        g.DrawText(mediaSub, new UiRect(infoX, mediaCard.Y + 23f, infoW, 14f), _theme.TextSecondary, 10f);

        float ctrlY = mediaCard.Y + 40f;
        const float gap = 4f;
        float btnW = MathF.Min(32f, (infoW - 3 * gap) / 3f);
        float ctrlStartX = infoX + (infoW - 3 * btnW - 2 * gap) / 2f;
        bool canPrev = _mediaState?.CanPrevious == true;
        bool canToggle = _mediaState?.CanToggle == true;
        bool canNext = _mediaState?.CanNext == true;
        bool isPlaying = _mediaState?.Playing == true;

        void TransportButton(string id, int index, UiIcon icon, bool enabled, string tipKey)
        {
            var rect = new UiRect(ctrlStartX + index * (btnW + gap), ctrlY, btnW, 28f);
            if (enabled) _hit.Add(id, rect, UiCursorKind.Hand, L10n.T(tipKey));
            if (enabled && _hit.IsHoverOrActive(id)) g.FillRoundedRect(rect, 6f, _theme.NavHover);
            g.DrawIcon(icon, new UiRect(rect.CenterX - 9f, rect.CenterY - 9f, 18f, 18f),
                enabled ? _theme.TextPrimary : _theme.TextPrimary.WithAlphaFraction(0.5f), filled: true);
        }
        TransportButton("companion.prev", 0, UiIcon.PreviousTrack, canPrev, "Phone.MediaPrevious");
        TransportButton("companion.play", 1, isPlaying ? UiIcon.Pause : UiIcon.Play, canToggle, isPlaying ? "Phone.MediaPause" : "Phone.MediaPlay");
        TransportButton("companion.next", 2, UiIcon.NextTrack, canNext, "Phone.MediaNext");

        // Seek bar row (time · slider · duration)
        float seekY = mediaCard.Y + 70f;
        long durationMs = Math.Max(0L, _mediaState?.DurationMs ?? 0L);
        double posMs = _seeking
            ? _seekPositionMs
            : _mediaState is { } st ? Math.Clamp(st.CurrentPositionMs, 0, Math.Max(1L, durationMs)) : 0;
        float progress01 = durationMs > 0 ? Math.Clamp((float)(posMs / durationMs), 0f, 1f) : 0f;
        bool canSeek = _mediaState?.CanSeek == true && durationMs > 0;

        var posRect = new UiRect(mediaCard.X + 7f, seekY, 32f, 20f);
        var durRect = new UiRect(mediaCard.Right - 39f, seekY, 32f, 20f);
        g.DrawText(FormatMediaTime(posMs), posRect, _theme.TextSecondary, 10f);
        g.DrawText(durationMs > 0 ? FormatMediaTime(durationMs) : "--:--", durRect, _theme.TextSecondary, 10f, 400, UiTextAlign.Trailing);
        var seekSlider = new UiRect(posRect.Right, seekY, durRect.X - posRect.Right, 20f);
        UiPrimitives.DrawValueSlider(g, _theme, _hit, "companion.seek", seekSlider, progress01,
            enabled: canSeek);
        if (_hit.AccessibilityEnabled)
            _hit.DescribeRange("companion.seek", seekSlider, L10n.T("Phone.MediaPlaybackPosition"), canSeek,
                durationMs > 0 ? Math.Round(posMs / 1000, 1) : null, durationMs / 1000d, 0.1, 1,
                string.Concat(_activeDevice?.Serial, "\0", _mediaState?.Package, "\0", _mediaState?.Title), SetAccessibleSeek);

        y = mediaCard.Bottom + 6f;

        // Search box
        var searchRect = new UiRect(padX, y, contentW, 28f);
        UiPrimitives.DrawTextBox(g, _theme, _hit, "companion.search", searchRect, _searchText, L10n.T("Phone.SearchAppsHint"),
            focused: _inlineEdit.ActiveTag == "companion.search", fontSize: 11.5f, search: true);

        var listRect = ComputeDropAreaRect(widthDip, heightDip, _airPlaySelected);

        if (_dragIcon != null || CrossDeviceDragHost.DraggedApp?.ProcessName?.StartsWith("android:", StringComparison.Ordinal) == true)
        {
            DrawReturnOverlay(g, listRect, _returnHovered || _externalDragHoverZone == ReturnToPhone, airPlay: false);
            RenderFooter(g, padX, footerY, contentW, footerH, phone, profile, online);
            return;
        }
        // Show cross-device drop zones when user drags a PC app from QuickPanelWindow
        if (CrossDeviceDragHost.IsDragging)
        {
            EnsureDropGeometry();
            RenderDropZoneOverlay(g, listRect, online != null);
            RenderFooter(g, padX, footerY, contentW, footerH, phone, profile, online);
            return;
        }

        // Keep whole-phone audio first and the retained apps in their saved order.
        var apps = GetFilteredApps(profile);
        var grant = online?.IsApp == true ? phone.Operations.AppCapture(online) : null;
        const float itemH = 50f;
        const float itemGap = 4f;
        _scroll.ViewportHeight = listRect.Height;
        _scroll.ContentHeight = apps.Length * (itemH + itemGap);
        _scroll.Clamp();

        if (apps.Length == 0)
        {
            g.DrawText(L10n.T("Phone.NoDisplayedApps"), new UiRect(listRect.X, listRect.CenterY - 26f, listRect.Width, 20f),
                _theme.TextSecondary, 11.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
            UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.manage",
                new UiRect(listRect.CenterX - 70f, listRect.CenterY + 2f, 140f, 28f), L10n.T("Phone.ManagePhoneApps"), fontSize: 11f);
        }
        else
        {
            const float scrollRightInset = 2f;
            var scrollViewport = new UiRect(listRect.X, listRect.Y, widthDip - listRect.X - scrollRightInset, listRect.Height);
            float itemW = _scroll.MaxOffset > 0.5f ? scrollViewport.Width - UiMetrics.ScrollBarWidth - 2f : listRect.Width;
            g.PushClip(listRect);
            _hit.PushClip(listRect);
            for (int i = 0; i < apps.Length; i++)
            {
                float iy = listRect.Y + 2f + i * (itemH + itemGap) - _scroll.Offset;
                if (iy + itemH < listRect.Y || iy > listRect.Bottom) continue;

                var app = _appIcons.Resolve(online, apps[i]);
                var rowRect = new UiRect(listRect.X + 4f, iy + 2f, MathF.Max(1f, itemW - 6f), itemH);
                string rowId = $"companion.app:{app.Package}";
                bool isPlayingApp = IsAppCurrentlyPlaying(app);
                bool hot = _hit.IsHover(rowId);

                string rowName = app.IsDeviceAudio ? phone.Store.GetProfileDisplayName(profile) : app.Name;
                string rowSub = grant?.IsDeviceMedia == true && app.IsDeviceAudio
                    ? L10n.T("Phone.DeviceMediaDescription") : PhoneAppPresentation.Description(app);
                bool grantNeeded = online?.IsApp == true && grant?.LocalSourceKey != app.Package;
                _hit.Add(rowId, rowRect, UiCursorKind.Hand, grantNeeded ? L10n.T("Phone.SrapCaptureGrant") : rowName);
                bool selected = _selectedPackage == app.Package;
                UiColor bg = selected ? _theme.NavSelected : hot ? _theme.NavHover : isPlayingApp ? _theme.Overlay : _theme.SurfaceBgAlpha;
                UiColor border = hot || selected ? _theme.Accent : UiColor.Transparent;
                g.FillRoundedRect(rowRect, 10f, bg);
                g.DrawRoundedRect(rowRect, 10f, border, 1f);

                var iconRect = new UiRect(rowRect.X + 8f, rowRect.CenterY - 14f, 28f, 28f);
                if (app.IsDeviceAudio)
                {
                    g.DrawIcon(UiIcon.Phone, iconRect, _theme.TextSecondary, 1.7f);
                }
                else if (_appIcons.TryGet(app, out var ibgra, out int iw, out int ih, out int iver))
                {
                    g.DrawBgraBitmap($"companion.appicon:{app.Package}", ibgra, iw, ih, iconRect, version: iver);
                }
                else
                {
                    string initial = string.IsNullOrEmpty(rowName) ? "♪" : rowName[..1];
                    g.DrawText(initial, iconRect, _theme.TextSecondary, 16f, 400, UiTextAlign.Center, UiVertAlign.Center);
                }

                float badgeW = isPlayingApp ? 46f : 0f;
                if (isPlayingApp)
                {
                    var bRect = new UiRect(rowRect.Right - 52f, rowRect.CenterY - 9f, 44f, 18f);
                    UiPrimitives.DrawBadge(g, _theme, bRect, L10n.T("Phone.PlayingBadge"), _theme.SurfaceBg, _theme.Accent, fontSize: 9.5f);
                }

                float tx = iconRect.Right + 8f;
                float tw = MathF.Max(10f, rowRect.Right - 8f - badgeW - tx);
                g.DrawText(rowName, new UiRect(tx, rowRect.Y + 8f, tw, 18f), _theme.TextPrimary, 13f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                g.DrawText(rowSub, new UiRect(tx, rowRect.Y + 26f, tw, 15f), _theme.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }
            _hit.PopClip();
            g.PopClip();

            _scroll.Draw(g, _theme, _hit, "companion.scroll", scrollViewport);
        }

        RenderFooter(g, padX, footerY, contentW, footerH, phone, profile, online);
    }

    private void RenderDropZoneOverlay(D2dTarget g, UiRect area, bool isOnline)
    {
        g.FillRoundedRect(area, 8f, _theme.SurfaceBg);
        g.DrawRoundedRect(area, 8f, _theme.Accent, 2f);

        if (!isOnline || _dropZones.Count == 0)
        {
            g.DrawText(L10n.T("Phone.DetailConnectPhoneFirst"), area.Deflate(12f), _theme.TextSecondary, 12f, 500,
                UiTextAlign.Center, UiVertAlign.Center, wrap: true);
            return;
        }

        for (int i = 0; i < _dropZones.Count; i++)
        {
            var zone = _dropZones[i];
            bool hovered = string.Equals(_externalDragHoverZone, zone.DeviceId, StringComparison.Ordinal);
            UiColor cellBg = hovered ? _theme.NavHover : _theme.Overlay;
            UiColor cellBorder = hovered ? _theme.Accent : _theme.Border;
            g.FillRoundedRect(zone.Bounds, 6f, cellBg);
            g.DrawRoundedRect(zone.Bounds, 6f, cellBorder, hovered ? 1.8f : 1f);

            var iconBox = new UiRect(zone.Bounds.X + 10f, zone.Bounds.CenterY - 9f, 18f, 18f);
            g.DrawIcon(zone.Kind is AndroidOutputSinkKind.Bluetooth or AndroidOutputSinkKind.Wired ? UiIcon.Headphones : UiIcon.Speaker,
                iconBox, hovered ? _theme.Accent : _theme.TextPrimary, 1.7f);

            float tx = iconBox.Right + 8f;
            float tw = MathF.Max(20f, zone.Bounds.Right - 8f - tx);
            float midY = zone.Bounds.CenterY;
            g.DrawText(zone.Title, new UiRect(tx, midY - 16f, tw, 16f), _theme.TextPrimary, 11.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            g.DrawText(zone.Description, new UiRect(tx, midY, tw, 16f), _theme.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
    }

    private void RenderFooter(
        D2dTarget g,
        float padX,
        float footerY,
        float contentW,
        float footerH,
        PhonePreview phone,
        AndroidPhoneProfile profile,
        AndroidDevice? online)
    {
        var footer = new UiRect(padX, footerY, contentW, footerH);
        float right = footer.Right;
        if (!_airPlaySelected)
        {
            var refresh = new UiRect(right - footerH, footerY, footerH, footerH);
            UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.refresh", refresh, "",
                enabled: _catalogRefresh == null, radius: 8f, icon: UiIcon.Refresh,
                tooltip: _refreshError ?? L10n.T(online == null && profile.LastDevice != null ? "Phone.Reconnect" : "History.Refresh"));
            right = refresh.X - 4f;
        }
        var card = new UiRect(padX, footerY, right - padX, footerH);
        string name = _airPlaySelected ? "AirPlay" : profile.LastDevice == null
            ? L10n.T("Phone.DeviceSelector") : phone.Store.GetProfileDisplayName(profile);
        string status = _airPlaySelected ? AirPlayReceiverViewControl.GetStageLabel(_host.AirPlay.Status)
            : L10n.T(_catalogRefresh != null ? "Phone.StatusLoading" : online == null ? "Phone.Disconnected" : "Phone.ConnectedStatus");
        string connection = _airPlaySelected ? status : PhoneAppPresentation.ConnectionLabel(profile, online);
        string accessibleName = name + " · " + connection;
        if (!_airPlaySelected && _catalogRefresh == null && online?.IsApp == true) status = "App · " + status;
        _hit.Add("companion.device", card, UiCursorKind.Hand, _refreshError ?? _dropStatusMessage ?? accessibleName);
        _hit.Describe("companion.device", accessibleName, UiAccessibleRole.ComboBox);
        g.FillRoundedRect(card, 8f, _theme.SurfaceBgAlpha);
        g.DrawRoundedRect(card, 8f, _hit.IsHover("companion.device") ? _theme.Accent : _theme.Border);
        var icon = new UiRect(card.X + 7f, card.CenterY - 8f, 16f, 16f);
        if (_airPlaySelected && _host.AirPlay.Source != null)
            _hit.Add("companion.airplaygrip", icon, UiCursorKind.Hand, L10n.T("Phone.AirPlayDragToWindows"));
        g.DrawIcon(_airPlaySelected ? UiIcon.AirPlay : UiIcon.Phone, icon, _theme.TextPrimary, 1.5f);
        float statusRight = card.Right - 29f;
        float measuredStatus = D2dSharedContext.Instance.MeasureText(status, UiMetrics.FontFamily, 10f, 400, 400f, footerH, false).Width;
        float statusWidth = Math.Clamp(measuredStatus + 3f, 32f, Math.Max(32f, card.Width * .45f));
        float statusX = statusRight - statusWidth - (!_airPlaySelected && online is { IsApp: false } ? 16f : 0f);
        g.DrawText(name, new UiRect(card.X + 28f, card.Y, MathF.Max(1f, statusX - card.X - 30f), footerH),
            _theme.TextPrimary, 11f, 700);
        float labelLeft = statusX;
        if (!_airPlaySelected && online is { IsApp: false })
        {
            var signal = new UiRect(statusX, card.CenterY - 6f, 14f, 12f);
            if (online.IsWifi)
            {
                string tip = _connectionQuality == null ? L10n.T("Phone.MeasuringLatency")
                    : _connectionQuality.RoundTripMs is { } ms ? string.Format(L10n.T("Phone.LatencyRoundTripFormat"), ms) : L10n.T("Phone.LatencyTimeout");
                _hit.Add("companion.signal", signal, UiCursorKind.Hand, tip);
                DrawSignal(g, signal, _connectionQuality?.SignalLevel ?? 0);
            }
            else
            {
                void Line(float x1, float y1, float x2, float y2) =>
                    g.DrawLine(signal.X + x1, signal.Y + y1, signal.X + x2, signal.Y + y2, _theme.Accent, 1.4f);
                Line(7, 1, 7, 9); Line(3, 5, 11, 5); Line(3, 5, 3, 8);
                Line(11, 5, 11, 3); Line(7, 9, 10, 9); Line(10, 9, 10, 12);
            }
            labelLeft += 16f;
        }
        g.DrawText(status, new UiRect(labelLeft, card.Y, statusRight - labelLeft, footerH), _theme.TextSecondary, 10f, 400, UiTextAlign.Trailing);
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(right - 21f, card.CenterY - 5f, 10f, 10f), _theme.TextSecondary, 1.5f);
    }

    private void DrawSignal(D2dTarget g, UiRect rect, int level)
    {
        var box = new UiRect(rect.X, rect.Y, 14f, 14f);
        g.DrawIcon(UiIcon.WifiSignalOuter, box, _theme.Accent.WithAlphaFraction(level >= 4 ? 1f : level == 3 ? 0.6f : 0.2f), filled: true);
        g.DrawIcon(UiIcon.WifiSignalNear, box, _theme.Accent.WithAlphaFraction(level >= 2 ? 1f : 0.2f), filled: true);
        g.DrawIcon(UiIcon.WifiSignalDot, box, _theme.Accent.WithAlphaFraction(level >= 1 ? 1f : 0.2f), filled: true);
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        var app = _pressApp;
        string? airId = _pressAirPlayId;
        if (leftDown && (app != null || airId != null))
        {
            if (_dragIcon == null && MathF.Abs(xDip - _pressX) <= 6f && MathF.Abs(yDip - _pressY) <= 6f) return;
            if (_panel is not { IsVisible: true } || (app != null && !AndroidDeviceIdentity.SameTransport(_pressDevice, _activeDevice)) ||
                (airId != null && (!_host.HasAirPlay || _host.AirPlay.Source?.Id != airId)))
            {
                ReleasePointerCapture();
                return;
            }
            ViewNative.GetCursorPos(out var pt);
            if (_dragIcon == null)
            {
                NativeAppIconService.IconBitmap? icon = app != null && _appIcons.TryGet(app, out var pixels, out int w, out int h, out _)
                    ? new NativeAppIconService.IconBitmap(pixels, w, h) : null;
                _hit.ActiveId = null;
                _dragIcon = new DragIconWindow(icon, app?.Name ?? "AirPlay", _theme.TextPrimary, pt.X, pt.Y,
                    airId != null ? UiIcon.AirPlay : app?.IsDeviceAudio == true ? UiIcon.Phone : null);
                Invalidate();
            }
            else _dragIcon.Follow(pt.X, pt.Y);
            bool overReturn = OverReturnTarget(pt.X, pt.Y);
            if (overReturn != _returnHovered) { _returnHovered = overReturn; Invalidate(); }
            _panel.UpdatePhoneDrop(pt.X, pt.Y, airPlay: airId != null, sharedOnly: _pressDevice?.IsApp == true);
            return;
        }
        if (leftDown && _seeking)
        {
            long durationMs = _mediaState?.DurationMs ?? 0L;
            if (durationMs > 0)
            {
                int pct = UiPrimitives.ValueSliderValueFromX(_seekSliderRect, xDip);
                _seekPositionMs = (long)Math.Round(durationMs * (pct / 100.0));
                Invalidate();
            }
            return;
        }
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
        CancelKeyboardSeek();
        if (e.Button == UiMouseButton.Right)
        {
            if (_dragIcon != null) { ReleasePointerCapture(); return; }
            return;
        }
        if (e.Button != UiMouseButton.Left) return;
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }
        var hit = _hit.HitTest(e.X, e.Y);
        _hit.ActiveId = hit?.Id;
        if (hit?.Id == "companion.airplaygrip" && _host.HasAirPlay && _host.AirPlay.Source is { } air)
        {
            _pressAirPlayId = air.Id;
            _pressX = e.X;
            _pressY = e.Y;
        }
        if (hit is { } row && row.Id.StartsWith(AppPrefix, StringComparison.Ordinal) && _activeDevice is { } active)
        {
            string package = row.Id[AppPrefix.Length..];
            _pressApp = !active.IsApp || _host.Phone.Operations.AppCapture(active)?.LocalSourceKey == package
                ? GetFilteredApps().FirstOrDefault(a => a.Package == package) : null;
            _pressDevice = active;
            _pressX = e.X;
            _pressY = e.Y;
        }
        if (hit is { } h && h.Id == "companion.seek" && _mediaState is { CanSeek: true, DurationMs: > 0 } st)
        {
            _seeking = true;
            _seekSliderRect = h.Bounds;
            _seekPackage = st.Package;
            _seekTitle = st.Title;
            StopSeekTimer();
            int pct = UiPrimitives.ValueSliderValueFromX(h.Bounds, e.X);
            _seekPositionMs = (long)Math.Round(st.DurationMs * (pct / 100.0));
        }
        Invalidate();
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        var hit = _hit.HitTest(e.X, e.Y);
        if (e.Button != UiMouseButton.Left) return;
        var app = _pressApp;
        var device = _pressDevice;
        string? airId = _pressAirPlayId;
        if (_dragIcon != null)
        {
            ViewNative.GetCursorPos(out var pt);
            bool? shared = _panel?.UpdatePhoneDrop(pt.X, pt.Y, airPlay: airId != null, sharedOnly: device?.IsApp == true);
            bool returnToPhone = OverReturnTarget(pt.X, pt.Y);
            bool outside = !CrossDeviceDragHost.ContainsScreenPoint(this, pt.X, pt.Y) && !CrossDeviceDragHost.ContainsScreenPoint(_panel, pt.X, pt.Y);
            CancelAppDrag();
            if (airId != null && _host.HasAirPlay && _host.AirPlay.Source is { } air && air.Id == airId)
            {
                if (shared.HasValue)
                    _ = RouteExternalAppAsync(new AudioAppInfo { ProcessId = 0, ExternalSourceId = air.Id }, AudioService.SystemDefaultDeviceId);
                else if (outside) _host.AirPlay.ShowMirror();
            }
            else if (device != null && app != null && AndroidDeviceIdentity.SameTransport(device, _activeDevice) && (shared.HasValue || outside || returnToPhone))
                _ = CrossDeviceDragHost.FinishAndroidDropAsync(_host, device, app, shared, pt.X, pt.Y, returnToPhone);
            return;
        }
        CancelAppDrag();
        if (device != null && !AndroidDeviceIdentity.SameTransport(device, _activeDevice)) { _hit.ActiveId = null; return; }
        if (_seeking)
        {
            _hit.ActiveId = null;
            _ = CommitMediaSeekAsync();
            Invalidate();
            return;
        }
        if (_scroll.Dragging) { _scroll.EndDrag(); Invalidate(); return; }
        string? active = _hit.ActiveId;
        _hit.ActiveId = null;

        if (hit is null || hit.Value.Id != active) { Invalidate(); return; }

        if (hit.Value.Id.StartsWith(AppPrefix, StringComparison.Ordinal))
        {
            _selectedPackage = hit.Value.Id[AppPrefix.Length..];
            Invalidate();
            return;
        }
        ActivateFocused(hit.Value, e);
    }

    protected override UiHitMap InputMap => _hit;

    protected override bool ActivateFocused(UiHitItem item, UiPointerEventArgs e)
    {
        if (_airPlaySelected && _airPlay.HandleClick(item.Id, item.Bounds)) return true;

        switch (item.Id)
        {
            case "companion.prev":
            case "companion.play":
            case "companion.next":
                if (_mediaClient is { } client && _mediaCts is { } media)
                {
                    string command = item.Id == "companion.prev" ? "previous" : item.Id == "companion.play" ? "toggle" : "next";
                    _ = RunCompanionCommand(() => client.SendAsync(command, media.Token));
                }
                break;
            case "companion.search":
                StartInlineEdit("companion.search", item.Bounds, _searchText, t => { _searchText = t; Invalidate(); }, (t, _) => { _searchText = t; Invalidate(); });
                break;
            case "companion.manage":
                OpenPhoneManagementDeepLink();
                break;
            case "companion.refresh":
                _ = RefreshCatalogAsync();
                break;
            case "companion.airplaygrip":
            case "companion.device":
            case "companion.signal":
                OpenDeviceSwitcher(item.Bounds);
                break;
            default:
                if (item.Id.StartsWith(AppPrefix, StringComparison.Ordinal) && _activeDevice is { } device)
                {
                    _selectedPackage = item.Id[AppPrefix.Length..];
                    var selected = GetFilteredApps().FirstOrDefault(a => a.Package == _selectedPackage);
                    if (selected != null) _ = CrossDeviceDragHost.FinishAndroidDropAsync(_host, device, selected, true, 0, 0);
                    Invalidate();
                }
                break;
        }
        return true;
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        _scroll.OnWheel(delta);
        Invalidate();
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 9) CancelKeyboardSeek();
        if (!ctrl && !alt && vk is 0x25 or 0x27 or 0x24 or 0x23 or 0x21 or 0x22 &&
            _hit.TryGetFocus(out var item, out _) && item.Id == "companion.seek" &&
            _mediaState is { CanSeek: true, DurationMs: > 0 } state && (!_seeking || _seekKey != 0))
        {
            if (!_seeking)
            {
                _seeking = true;
                _seekPackage = state.Package;
                _seekTitle = state.Title;
                _seekPositionMs = state.CurrentPositionMs;
                StopSeekTimer();
            }
            _seekKey = vk;
            _seekPositionMs = Math.Clamp(vk switch
            {
                0x24 => 0d, 0x23 => state.DurationMs,
                0x25 => _seekPositionMs - 100d, 0x27 => _seekPositionMs + 100d,
                0x21 => _seekPositionMs + 1000d, _ => _seekPositionMs - 1000d
            }, 0d, state.DurationMs);
            Invalidate();
            return true;
        }
        if (vk == 0x1B)
        {
            if (_dragIcon != null) { ReleasePointerCapture(); return true; }
            if (_seeking)
            {
                _seeking = false;
                _seekKey = 0;
                UpdateSeekAnimationTimer();
                Invalidate();
                return true;
            }
            if (_inlineEdit.IsOpen) { _inlineEdit.Close(false, false); return true; }
            Hide();
            _onDeactivatedCheck?.Invoke();
            return true;
        }
        return false;
    }

    protected override bool OnKeyUp(int vk)
    {
        if (vk != _seekKey || _seekKey == 0) return false;
        _seekKey = 0;
        _ = CommitMediaSeekAsync();
        return true;
    }

    private void CancelKeyboardSeek()
    {
        if (_seekKey == 0) return;
        _seekKey = 0;
        _seeking = false;
        UpdateSeekAnimationTimer();
        if (IsVisible) Invalidate();
    }

    protected override void OnFocusLost() => CancelKeyboardSeek();

    protected override void OnActivated(bool active)
    {
        if (!active) CancelKeyboardSeek();
        if (!active && !HasOpenModal) _onDeactivatedCheck?.Invoke();
    }

    private async Task CommitMediaSeekAsync()
    {
        if (!_seeking) return;
        _seeking = false;
        long target = (long)Math.Round(_seekPositionMs);
        var state = _mediaState;
        var client = _mediaClient;
        var cts = _mediaCts;
        UpdateSeekAnimationTimer();
        if (state?.CanSeek != true || client == null || cts == null || cts.IsCancellationRequested
            || state.Package != _seekPackage || state.Title != _seekTitle) return;
        await RunCompanionCommand(() => client.SeekAsync(state.Package, target, cts.Token), () =>
        {
            if (!ReferenceEquals(_mediaCts, cts) || !ReferenceEquals(_mediaState, state)) return;
            _mediaState = state with { PositionMs = target, ReceivedTimestamp = Stopwatch.GetTimestamp() };
            UpdateSeekAnimationTimer();
        }).ConfigureAwait(false);
    }

    private void SetAccessibleSeek(string id, double seconds)
    {
        if (_seeking || _mediaState is not { CanSeek: true, DurationMs: > 0 } state) return;
        _seeking = true;
        _seekPackage = state.Package;
        _seekTitle = state.Title;
        _seekPositionMs = Math.Clamp(seconds * 1000, 0, state.DurationMs);
        _ = CommitMediaSeekAsync();
    }

    private async Task RefreshCatalogAsync()
    {
        if (_catalogRefresh != null) return;
        ClearFilteredApps();
        _appIcons.Clear();
        CancelOpeningReconnect();
        var phone = _host.Phone;
        var selected = phone.Store.Profile;
        var mediaAtClick = _mediaWatch;
        var refresh = CancellationTokenSource.CreateLinkedTokenSource(phone.Lifetime);
        _catalogRefresh = refresh;
        _refreshError = null;
        Invalidate();
        string? error = null;
        try
        {
            await phone.ReconnectSelectedDeviceAsync(refresh.Token).ConfigureAwait(false);
            refresh.Token.ThrowIfCancellationRequested();
            var current = phone.Store.Profile;
            if (current.Id != selected.Id || current.QuickPanelApps.Count == 0 ||
                phone.Connections.Resolve(current.LastDevice) is not { } device || !phone.Operations.CanListApps(device)) return;
            using var read = CancellationTokenSource.CreateLinkedTokenSource(refresh.Token, phone.Connections.OperationToken(device));
            var apps = await phone.Operations.AppsAsync(device, read.Token, current.QuickPanelApps.Select(a => a.Package).ToArray()).ConfigureAwait(false);
            read.Token.ThrowIfCancellationRequested();
            if (phone.Store.Profile.Id == selected.Id && phone.Connections.IsCurrent(device))
                phone.Store.UpdateMetadata(device, apps);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ExceptionDiagnostics.Record("PhoneCompanion.Refresh", ex);
            error = PhonePreview.TranslateError(ex);
        }
        finally
        {
            if (!_host.TryPost(() =>
            {
                refresh.Dispose();
                if (!ReferenceEquals(_catalogRefresh, refresh)) return;
                _catalogRefresh = null;
                if (!IsVisible || _airPlaySelected || phone.Store.Profile.Id != selected.Id) return;
                RefreshData();
                _refreshError = error;
                // A failed media session can leave a healthy ADB device online. An
                // explicit refresh retries that ended session, preserving any live one.
                if (ReferenceEquals(_mediaWatch, mediaAtClick) && mediaAtClick is { IsCompleted: true } && _activeDevice != null)
                    RestartMediaWatch(_activeDevice);
            })) refresh.Dispose();
        }
    }

    private void CancelCatalogRefresh()
    {
        _catalogRefresh?.Cancel();
        _catalogRefresh = null;
        _refreshError = null;
    }

    private void OpenPhoneManagementDeepLink()
    {
        var profile = _host.Phone.Store.Profile;
        string? profileId = _airPlaySelected || profile.LastDevice == null ? null : profile.Id;
        Hide();
        _onDeactivatedCheck?.Invoke();
        _host.OpenPhoneManagement(profileId, _airPlaySelected);
    }

    private void DrawReturnOverlay(D2dTarget g, UiRect bounds, bool hovered, bool airPlay)
    {
        g.FillRoundedRect(bounds, 8f, hovered ? _theme.Overlay : _theme.SurfaceBg);
        g.DrawRoundedRect(bounds, 8f, _theme.Accent, 2f);
        g.DrawText(L10n.T(airPlay ? "Phone.AirPlayDropExit" : "Phone.ReturnToPhoneTitle"),
            new UiRect(bounds.X + 8f, bounds.CenterY - 21f, bounds.Width - 16f, 20f), _theme.TextPrimary, 13f, 600, UiTextAlign.Center);
        g.DrawText(L10n.T(airPlay ? "Phone.AirPlayDropExitDesc" : "Phone.ReturnToPhoneDesc"),
            new UiRect(bounds.X + 8f, bounds.CenterY + 5f, bounds.Width - 16f, 34f), _theme.TextSecondary, 11f, 400,
            UiTextAlign.Center, UiVertAlign.Top, wrap: true);
    }

    private bool OverReturnTarget(int x, int y) => IsVisible && !_airPlaySelected &&
        ComputeDropAreaRect(WidthDip, HeightDip, false).Contains((x - LeftPx) / DpiScale, (y - TopPx) / DpiScale);

    private void CancelAppDrag()
    {
        _pressApp = null;
        _pressDevice = null;
        _pressAirPlayId = null;
        if (_dragIcon == null) return;
        _dragIcon.Dispose();
        _dragIcon = null;
        _panel?.ClearPhoneDrop();
        _returnHovered = false;
        if (IsVisible) Invalidate();
    }

    private AndroidApp[] GetFilteredApps(AndroidPhoneProfile? profile = null)
    {
        profile ??= _host.Phone.Store.Profile;
        if (profile.LastDevice == null) return Array.Empty<AndroidApp>();
        var source = profile.QuickPanelApps;
        bool appChannel = (_activeDevice ?? profile.LastDevice).IsApp;
        var grant = _activeDevice is { IsApp: true } peer ? _host.Phone.Operations.AppCapture(peer) : null;
        string? granted = grant?.LocalSourceKey;
        AndroidApp? grantApp = null;
        if (grant?.IsDeviceMedia == true) grantApp = AndroidApp.DeviceAudio;
        else if (granted != null)
        {
            var known = source.FirstOrDefault(a => a.Package == granted)
                ?? _host.Phone.Sources.Sources.FirstOrDefault(s => AndroidDeviceIdentity.SameTransport(s.Device, _activeDevice) && s.App.Package == granted)?.App
                ?? (_filteredGrant == granted ? _grantApp : null) ?? new AndroidApp(granted, granted, 0, "");
            grantApp = _appIcons.Resolve(_activeDevice, known);
        }
        if (ReferenceEquals(_filteredAppsSource, source) && _filteredAppsQuery == _searchText
            && _filteredGrant == granted && ReferenceEquals(_grantApp, grantApp) && _filteredAppChannel == appChannel) return _filteredApps;

        string query = _searchText.Trim();
        IEnumerable<AndroidApp> items = source;
        if (grantApp != null)
            items = source.Any(a => a.Package == granted) ? items.Select(a => a.Package == granted ? grantApp : a) : items.Prepend(grantApp);
        if (query.Length != 0) items = items.Where(a =>
            a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            a.Package.Contains(query, StringComparison.OrdinalIgnoreCase));
        _filteredApps = (appChannel ? items : items.Prepend(AndroidApp.DeviceAudio)).ToArray();
        _filteredAppsSource = source;
        _filteredAppsQuery = _searchText;
        _filteredGrant = granted;
        _grantApp = grantApp;
        _filteredAppChannel = appChannel;
        return _filteredApps;
    }

    private void ClearFilteredApps()
    {
        _grantApp = null;
        _filteredAppsSource = null;
        _filteredApps = [];
    }

    private bool IsAppCurrentlyPlaying(AndroidApp app) => _mediaState is { } state &&
        (app.IsDeviceAudio ? state.Playing || state.PlayingPackages.Count > 0
            : (state.Playing && state.Package == app.Package) || state.PlayingPackages.Contains(app.Package));

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
            CancelCatalogRefresh();
            CancelOpeningReconnect();
            if (picked.Key == AirPlayChoiceKey)
            {
                _airPlaySelected = true;
            }
            else
            {
                _airPlaySelected = false;
                phone.Store.SelectProfile(picked.Key);
            }
            ++_commandVersion;
            _dropStatusMessage = null;
            _scroll.Offset = 0f;
            RefreshData();
        });
    }

    private void ShowCombo(UiRect anchorDip, IReadOnlyList<ComboChoice> choices, string selectedKey, Action<ComboChoice> onSelected)
    {
        ComboPopupWindow.Close(ref _comboPopup);
        _comboPopup = ComboPopupWindow.ShowFor(_theme, this, anchorDip, choices, selectedKey, picked =>
        {
            _comboPopup = null;
            onSelected(picked);
        });
    }

    protected override InlineEditHost ActiveEditor => _inlineEdit;

    private void StartInlineEdit(string tag, UiRect rectDip, string text, Action<string>? onChanged, Action<string, bool> onCommitted, int maxLength = 0)
    {
        _inlineEdit.Close(commit: true, viaEnter: false);
        _inlineChanged = onChanged;
        _inlineCommitted = onCommitted;
        _inlineEdit.Open(this, tag, text, rectDip, 11.5f, _theme.SurfaceBg, _theme.TextPrimary,
            maxLength: maxLength, minWidthPx: 20, minHeightPx: 18);
        Invalidate();
    }

    private bool TryGetArtwork(byte[]? png, out byte[] bgra, out int w, out int h, out int ver)
    {
        if (png is null || png.Length == 0)
        {
            _lastArtPng = null;
            _artBgra = null;
            bgra = Array.Empty<byte>(); w = h = ver = 0;
            return false;
        }
        if (ReferenceEquals(png, _lastArtPng))
        {
            if (_artBgra != null)
            {
                bgra = _artBgra; w = _artW; h = _artH; ver = _artVer;
                return true;
            }
            bgra = Array.Empty<byte>(); w = h = ver = 0;
            return false;
        }
        _lastArtPng = png;
        _artBgra = null;
        int gen = Interlocked.Increment(ref _artGen);
        _ = Task.Run(() =>
        {
            if (!UiBitmap.TryDecodePng(png, 96, out byte[] pixels, out int pw, out int ph))
                return;
            _host.TryPost(() =>
            {
                if (IsVisible && Volatile.Read(ref _artGen) == gen && ReferenceEquals(_lastArtPng, png))
                {
                    _artBgra = pixels;
                    _artW = pw;
                    _artH = ph;
                    _artVer++;
                    Invalidate();
                }
            });
        });

        bgra = Array.Empty<byte>(); w = h = ver = 0;
        return false;
    }

    private static string FormatMediaTime(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

}
#endif
