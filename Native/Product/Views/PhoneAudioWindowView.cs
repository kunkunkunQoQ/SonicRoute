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
    private const string AirPlayChoiceKey = "__AIRPLAY__";

    private sealed record DropZoneTarget(string DeviceId, UiRect Bounds, string Title, string Description, AndroidOutputSinkKind Kind);

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
    private readonly HashSet<string> _iconDecoding = new(StringComparer.Ordinal);
    private int _iconVer;

    private readonly List<DropZoneTarget> _dropZones = new();
    private UiRect _dropAreaRect;
    private string? _externalDragHoverZone;
    private string? _dropStatusMessage;
    private IDisposable? _dragRegistration;
    private bool _dragSubscribed;

    private Action<string>? _inlineChanged;
    private Action<string, bool>? _inlineCommitted;

    private UiThemePalette _theme;
    private ComboPopupWindow? _comboPopup;
    private bool _airPlaySelected;
    private string _searchText = "";
    private AndroidDevice? _activeDevice;
    private AndroidMediaSessionClient? _mediaClient;
    private AndroidMediaState? _mediaState;
    private AndroidConnectionQuality? _connectionQuality;
    private CancellationTokenSource? _mediaCts;

    // Media seek bar & background artwork decoding
    private bool _seeking;
    private long _seekPositionMs;
    private string? _seekPackage;
    private string? _seekTitle;
    private UiRect _seekSliderRect;
    private bool _seekTimerRunning;

    private byte[]? _lastArtPng;
    private byte[]? _artBgra;
    private int _artW, _artH, _artVer, _artGen;
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
                onCommit?.Invoke(text, true);
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
        AttachCrossDeviceDrag();
        _airPlay.Attach();
        RefreshData();
        UpdateSeekAnimationTimer();
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
        if (_airPlaySelected)
        {
            _activeDevice = online;
            StopMediaWatch();
        }
        else if (!Equals(online, _activeDevice))
        {
            _activeDevice = online;
            _connectionQuality = null;
            RestartMediaWatch(online);
        }
        else if (online != null && _mediaCts == null && IsVisible)
        {
            RestartMediaWatch(online);
        }

        RebuildDropZones(WidthDip, HeightDip);
        UpdateSeekAnimationTimer();
        if (IsVisible) Invalidate();
    }

    internal (string DeviceId, UiRect Bounds)[] GetDropZoneTargets()
    {
        RebuildDropZones(WidthDip, HeightDip);
        var result = new (string DeviceId, UiRect Bounds)[_dropZones.Count];
        for (int i = 0; i < _dropZones.Count; i++)
            result[i] = (_dropZones[i].DeviceId, _dropZones[i].Bounds);
        return result;
    }

    internal string? HitTestScreenDropZone(int screenXPx, int screenYPx)
    {
        if (!IsVisible || _airPlaySelected) return null;
        RebuildDropZones(WidthDip, HeightDip);
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
        _ = RouteExternalAppAsync(app, targetDeviceId);
        if (IsVisible) Invalidate();
    }

    internal void SetExternalDragHover(string? zoneId)
    {
        if (string.Equals(_externalDragHoverZone, zoneId, StringComparison.Ordinal)) return;
        _externalDragHoverZone = zoneId;
        if (IsVisible) Invalidate();
    }

    private async Task RouteExternalAppAsync(AudioAppInfo app, string targetDeviceId)
    {
        try
        {
            ControlReply reply = app.IsExternal
                ? await _host.Audio.SetExternalOutputsAsync(app, [targetDeviceId]).ConfigureAwait(false)
                : await _host.Audio.SelectOutputAsync((int)app.ProcessId, targetDeviceId).ConfigureAwait(false);
            string? err = reply.Text.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ? reply.Text : null;
            _host.TryPost(() =>
            {
                _dropStatusMessage = err;
                if (IsVisible) Invalidate();
            });
        }
        catch (Exception ex)
        {
            _host.TryPost(() =>
            {
                _dropStatusMessage = PhonePreview.TranslateError(ex);
                if (IsVisible) Invalidate();
            });
        }
    }

    protected override void OnShown()
    {
        base.OnShown();
        AttachPhone();
        AttachCrossDeviceDrag();
        _airPlay.Attach();
        if (!_airPlaySelected && _activeDevice != null && _mediaCts == null)
            RestartMediaWatch(_activeDevice);
        UpdateSeekAnimationTimer();
    }

    protected override void OnHidden()
    {
        StopSeekTimer();
        _seeking = false;
        _inlineEdit.Close(commit: false, viaEnter: false);
        _inlineChanged = null;
        _inlineCommitted = null;
        CloseCombo();
        StopMediaWatch();
        ClearArtworkState();
        _airPlay.Detach();
        DetachCrossDeviceDrag();
        DetachPhone();
        base.OnHidden();
    }

    protected override void OnClosed()
    {
        StopSeekTimer();
        _seeking = false;
        _inlineEdit.Dispose();
        _inlineChanged = null;
        _inlineCommitted = null;
        CloseCombo();
        StopMediaWatch();
        ClearArtworkState();
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
        bool changed = false;
        if (_seeking)
        {
            changed = true;
            _ = CommitMediaSeekAsync();
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
            RebuildDropZones(WidthDip, HeightDip);
            Invalidate();
        });
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
        if (device == null || !IsVisible || _airPlaySelected) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_host.Phone.Lifetime, _host.Phone.Connections.OperationToken(device));
        _mediaCts = cts;
        var client = new AndroidMediaSessionClient(_host.Phone.Adb);
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
                        UpdateSeekAnimationTimer();
                        if (IsVisible && !_airPlaySelected) Invalidate();
                    });
                }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch { }
        });
    }

    private void StopMediaWatch()
    {
        StopSeekTimer();
        _seeking = false;
        var cts = _mediaCts;
        _mediaCts = null;
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

    private static UiRect ComputeDropAreaRect(float widthDip, float heightDip, bool airPlaySelected)
    {
        if (airPlaySelected) return default;
        const float padX = 8f;
        const float padY = 12f;
        float contentW = widthDip - padX * 2f;
        float footerH = 36f;
        float footerY = heightDip - padY - footerH;
        float listY = padY + 136f + 6f + 28f + 6f;
        return new UiRect(padX, listY, contentW, MathF.Max(30f, footerY - 6f - listY));
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
            string sinkName = sink.Name == "蓝牙音频设备" ? L10n.T("Phone.BluetoothAudioDevice") : sink.Name;
            string targetName = sink.Kind == AndroidOutputSinkKind.Current
                ? L10n.T("Phone.NativeAudioOutput")
                : sink.Kind == AndroidOutputSinkKind.Speaker
                    ? string.Format(L10n.T("Phone.PcDropSpeakerTitle"), phoneName)
                    : string.Format(L10n.T("Phone.BluetoothOutputFormat"), sinkName, phoneName);
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

        if (_airPlaySelected)
        {
            // AirPlay mode isolation: hide Android media card & search box, fill upper area with AirPlayPane
            var airBox = new UiRect(padX, padY, contentW, MathF.Max(40f, footerY - 6f - padY));
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

            RenderFooter(g, padX, footerY, contentW, footerH, phone, profile, online);
            return;
        }

        // 1. Top Media + Seek Bar + Receive-to-PC card
        float y = padY;
        var mediaCard = new UiRect(padX, y, contentW, 136f);
        UiPrimitives.DrawCard(g, _theme, mediaCard, 10f);

        var artRect = new UiRect(mediaCard.X + 6f, mediaCard.Y + 6f, 52f, 52f);
        g.FillRoundedRect(artRect, 8f, _theme.SurfaceBg);
        g.DrawRoundedRect(artRect, 8f, _theme.Border, 1f);

        var mediaApp = _mediaState == null ? null
            : profile.RetainedApps.FirstOrDefault(a => a.Package == _mediaState.Package);

        bool hasArtwork = TryGetArtwork(_mediaState?.ArtworkPng, out var artBgra, out int aw, out int ah, out int aver);
        if (hasArtwork)
        {
            g.DrawBgraBitmap("companion.art", artBgra, aw, ah, artRect.Deflate(1f), version: aver);
            if (mediaApp != null && TryGetAppIcon(mediaApp, out var bBgra, out int bw, out int bh, out int bver))
            {
                var badgeBox = new UiRect(artRect.Right - 17f, artRect.Bottom - 17f, 16f, 16f);
                g.FillRoundedRect(badgeBox, 4f, _theme.WindowBg);
                g.DrawRoundedRect(badgeBox, 4f, _theme.Border, 0.8f);
                g.DrawBgraBitmap($"companion.artbadge:{mediaApp.Package}", bBgra, bw, bh, badgeBox.Deflate(1.5f), version: bver);
            }
        }
        else if (mediaApp != null && TryGetAppIcon(mediaApp, out var fallbackBgra, out int fw, out int fh, out int fver))
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
        string mediaSub = _mediaState is { HasSession: true } ms2
            ? (ms2.Artist.Length > 0 ? ms2.Artist : (ms2.Playing ? L10n.T("Phone.MediaPlaying") : L10n.T("Phone.MediaPaused")))
            : online != null ? L10n.T("Phone.MediaNonePlaying") : L10n.T("Phone.MediaSubtitleIdle");
        g.DrawText(mediaTitle, new UiRect(infoX, mediaCard.Y + 4f, infoW, 17f), _theme.TextPrimary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(mediaSub, new UiRect(infoX, mediaCard.Y + 21f, infoW, 14f), _theme.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        float ctrlY = mediaCard.Y + 37f;
        float btnW = 30f;
        float ctrlStartX = infoX + MathF.Max(0f, (infoW - btnW * 3f - 8f) * 0.5f);
        bool canPrev = _mediaState?.CanPrevious == true;
        bool canToggle = _mediaState?.CanToggle == true;
        bool canNext = _mediaState?.CanNext == true;
        bool isPlaying = _mediaState?.Playing == true;

        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.prev", new UiRect(ctrlStartX, ctrlY, btnW, 22f), "",
            enabled: canPrev, radius: 6f, icon: UiIcon.PreviousTrack, tooltip: L10n.T("Phone.MediaPrevious"));
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.play", new UiRect(ctrlStartX + btnW + 4f, ctrlY, btnW, 22f), "",
            enabled: canToggle, radius: 6f, icon: isPlaying ? UiIcon.Pause : UiIcon.Play, tooltip: L10n.T(isPlaying ? "Phone.MediaPause" : "Phone.MediaPlay"));
        UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.next", new UiRect(ctrlStartX + (btnW + 4f) * 2f, ctrlY, btnW, 22f), "",
            enabled: canNext, radius: 6f, icon: UiIcon.NextTrack, tooltip: L10n.T("Phone.MediaNext"));

        // Seek bar row (time · slider · duration)
        float seekY = mediaCard.Y + 67f;
        long durationMs = Math.Max(0L, _mediaState?.DurationMs ?? 0L);
        double posMs = _seeking
            ? _seekPositionMs
            : _mediaState is { } st ? Math.Clamp(st.CurrentPositionMs, 0, Math.Max(1L, durationMs)) : 0;
        float progress01 = durationMs > 0 ? Math.Clamp((float)(posMs / durationMs), 0f, 1f) : 0f;
        bool canSeek = _mediaState?.CanSeek == true && durationMs > 0;

        var posRect = new UiRect(mediaCard.X + 6f, seekY, 34f, 18f);
        var durRect = new UiRect(mediaCard.Right - 40f, seekY, 34f, 18f);
        g.DrawText(FormatMediaTime(posMs), posRect, _theme.TextSecondary, 9.5f, 400, UiTextAlign.Leading, UiVertAlign.Center, fontFamily: UiMetrics.MonoFontFamily);
        g.DrawText(durationMs > 0 ? FormatMediaTime(durationMs) : "--:--", durRect, _theme.TextSecondary, 9.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center, fontFamily: UiMetrics.MonoFontFamily);

        var seekSlider = new UiRect(posRect.Right + 2f, seekY, MathF.Max(24f, durRect.X - 2f - (posRect.Right + 2f)), 18f);
        UiPrimitives.DrawVolumeSlider(g, _theme, _hit, "companion.seek", seekSlider, progress01, 0f, muted: false, showPeak: false,
            trackHeight: 4f, thumbDiameter: 10f, enabled: canSeek);

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

        // Search box
        var searchRect = new UiRect(padX, y, contentW, 28f);
        UiPrimitives.DrawTextBox(g, _theme, _hit, "companion.search", searchRect, _searchText, L10n.T("Phone.SearchAppsHint"),
            focused: _inlineEdit.ActiveTag == "companion.search", fontSize: 11.5f);
        y = searchRect.Bottom + 6f;

        var listRect = new UiRect(padX, y, contentW, MathF.Max(30f, footerY - 6f - y));
        RebuildDropZones(widthDip, heightDip);

        // Show cross-device drop zones when user drags a PC app from QuickPanelWindow
        if (CrossDeviceDragHost.IsDragging)
        {
            RenderDropZoneOverlay(g, listRect, online != null);
            RenderFooter(g, padX, footerY, contentW, footerH, phone, profile, online);
            return;
        }

        // App list: Whole-Phone Audio Row + Retained Apps (sorted by DeviceAudio then Playing)
        var apps = GetFilteredApps();
        const float itemH = 48f;
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
            float itemW = _scroll.MaxOffset > 0.5f ? listRect.Width - UiMetrics.ScrollBarWidth - 2f : listRect.Width;
            g.PushClip(listRect);
            for (int i = 0; i < apps.Length; i++)
            {
                float iy = listRect.Y + i * (itemH + itemGap) - _scroll.Offset;
                if (iy + itemH < listRect.Y || iy > listRect.Bottom) continue;

                var app = apps[i];
                var rowRect = new UiRect(listRect.X, iy, itemW, itemH);
                string rowId = $"companion.app:{i}";
                bool isPlayingApp = app.IsDeviceAudio
                    ? (_mediaState?.Playing == true || (_mediaState?.PlayingPackages.Count ?? 0) > 0 || wholePhoneSource != null)
                    : (_mediaState?.PlayingPackages.Contains(app.Package) == true
                        || (online != null && phone.Sources.Sources.Any(s => s.App.Package == app.Package && AndroidDeviceIdentity.Matches(s.Device, online))));
                bool hot = _hit.IsHover(rowId);

                string rowName = app.IsDeviceAudio ? phone.Store.GetProfileDisplayName(profile) : app.Name;
                string rowSub = app.IsDeviceAudio ? L10n.T("Phone.DeviceAudioDescription") : app.Package;
                _hit.Add(rowId, rowRect, UiCursorKind.Hand, rowName);
                UiColor bg = isPlayingApp ? _theme.Overlay : hot ? _theme.NavHover : _theme.SurfaceBgAlpha;
                UiColor border = hot ? _theme.Accent : _theme.Border;
                g.FillRoundedRect(rowRect, 10f, bg);
                g.DrawRoundedRect(rowRect, 10f, border, 1f);

                var iconRect = new UiRect(rowRect.X + 8f, rowRect.CenterY - 14f, 28f, 28f);
                if (app.IsDeviceAudio)
                {
                    g.FillRoundedRect(iconRect, 6f, _theme.Overlay);
                    g.DrawIcon(UiIcon.Phone, iconRect.Deflate(5f), _theme.Accent, 1.7f);
                }
                else if (TryGetAppIcon(app, out var ibgra, out int iw, out int ih, out int iver))
                {
                    g.DrawBgraBitmap($"companion.appicon:{app.Package}", ibgra, iw, ih, iconRect, version: iver);
                }
                else
                {
                    g.FillRoundedRect(iconRect, 6f, _theme.SurfaceAlt);
                    string initial = string.IsNullOrEmpty(rowName) ? "♪" : rowName[..1];
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
                g.DrawText(rowName, new UiRect(tx, rowRect.Y + 6f, tw, 18f), _theme.TextPrimary, 12.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                g.DrawText(rowSub, new UiRect(tx, rowRect.Y + 24f, tw, 15f), _theme.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }
            g.PopClip();

            _scroll.Draw(g, _theme, _hit, "companion.scroll", listRect);
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
            g.DrawIcon(zone.Kind == AndroidOutputSinkKind.Bluetooth ? UiIcon.Headphones : UiIcon.Speaker,
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
        var footerRect = new UiRect(padX, footerY, contentW, footerH);
        float rightEdge = footerRect.Right;

        if (!_airPlaySelected)
        {
            var refreshRect = new UiRect(rightEdge - 32f, footerRect.Y, 32f, footerH);
            UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.refresh", refreshRect, "",
                radius: 8f, icon: UiIcon.Refresh, tooltip: L10n.T("History.Refresh"));
            rightEdge = refreshRect.X - 4f;

            var manageRect = new UiRect(rightEdge - 32f, footerRect.Y, 32f, footerH);
            UiPrimitives.DrawSecondaryButton(g, _theme, _hit, "companion.manage", manageRect, "",
                radius: 8f, icon: UiIcon.Apps, tooltip: L10n.T("Phone.ManagePhoneApps"));
            rightEdge = manageRect.X - 4f;
        }

        var deviceCardRect = new UiRect(footerRect.X, footerRect.Y, MathF.Max(40f, rightEdge - footerRect.X), footerH);
        string comboLabel = _airPlaySelected ? "AirPlay"
            : profile.LastDevice != null ? phone.Store.GetProfileDisplayName(profile)
            : L10n.T("Phone.DeviceSelector");

        _hit.Add("companion.device", deviceCardRect, UiCursorKind.Hand, _dropStatusMessage);
        bool devHot = _hit.IsHover("companion.device");
        g.FillRoundedRect(deviceCardRect, 8f, _theme.SurfaceBg);
        g.DrawRoundedRect(deviceCardRect, 8f, devHot ? _theme.Accent : _theme.Border, 1f);

        // Device kind icon (AirPlay or Phone)
        var kindRect = new UiRect(deviceCardRect.X + 7f, deviceCardRect.CenterY - 8f, 16f, 16f);
        g.DrawIcon(_airPlaySelected ? UiIcon.AirPlay : UiIcon.Phone, kindRect, _theme.TextPrimary, 1.7f);

        // Right-side indicator: chevron + status dot + signal bars (for Android)
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(deviceCardRect.Right - 17f, deviceCardRect.CenterY - 5f, 10f, 10f), _theme.TextSecondary, 1.7f);

        float textLeft = kindRect.Right + 6f;
        float rightReserved = 22f;

        if (!_airPlaySelected && online != null)
        {
            if (online.IsWifi)
            {
                var sigRect = new UiRect(deviceCardRect.Right - 36f, deviceCardRect.CenterY - 6f, 14f, 12f);
                int level = _connectionQuality?.SignalLevel ?? 0;
                string latencyTip = _connectionQuality == null
                    ? L10n.T("Phone.MeasuringLatency")
                    : _connectionQuality.RoundTripMs is { } ms
                        ? string.Format(L10n.T("Phone.LatencyRoundTripFormat"), ms)
                        : L10n.T("Phone.LatencyTimeout");
                _hit.Add("companion.signal", sigRect, UiCursorKind.Hand, latencyTip);
                DrawSignalBars(g, sigRect, level);
                rightReserved = 42f;
            }
            else
            {
                g.FillEllipse(deviceCardRect.Right - 27f, deviceCardRect.CenterY, 3.5f, 3.5f, _theme.Success);
                rightReserved = 36f;
            }
        }
        else if (_airPlaySelected)
        {
            var airStatus = _host.AirPlay.Status;
            UiColor airDot = airStatus.ErrorKey != null ? _theme.Fail : airStatus.Stage switch
            {
                AirPlayStage.Connected => _theme.Success,
                AirPlayStage.Waiting or AirPlayStage.Starting => _theme.Accent,
                _ => _theme.TextSecondary,
            };
            g.FillEllipse(deviceCardRect.Right - 27f, deviceCardRect.CenterY, 3.5f, 3.5f, airDot);
            rightReserved = 36f;
        }
        else
        {
            g.FillEllipse(deviceCardRect.Right - 27f, deviceCardRect.CenterY, 3.5f, 3.5f, _theme.TextSecondary);
            rightReserved = 36f;
        }

        float labelW = MathF.Max(20f, deviceCardRect.Right - rightReserved - textLeft);
        if (_airPlaySelected)
        {
            var airStatus = _host.AirPlay.Status;
            string airStage = AirPlayReceiverViewControl.GetStageLabel(airStatus);
            if (_host.AirPlay.Port > 0 && airStatus.Stage is AirPlayStage.Waiting or AirPlayStage.Connected)
                airStage += $" · {_host.AirPlay.Port}";
            g.DrawText("AirPlay", new UiRect(textLeft, deviceCardRect.Y + 3f, labelW, 15f), _theme.TextPrimary, 11.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            g.DrawText(airStage, new UiRect(textLeft, deviceCardRect.Y + 18f, labelW, 14f), _theme.TextSecondary, 9.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
        else
        {
            string subText = _dropStatusMessage
                ?? (online != null
                    ? (_connectionQuality?.RoundTripMs is { } ms ? string.Format(L10n.T("Phone.LatencyRoundTripFormat"), ms) : L10n.T("Phone.ConnectedStatus"))
                    : L10n.T("Phone.Disconnected"));
            g.DrawText(comboLabel, new UiRect(textLeft, deviceCardRect.Y + 3f, labelW, 15f), _theme.TextPrimary, 11.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            g.DrawText(subText, new UiRect(textLeft, deviceCardRect.Y + 18f, labelW, 14f),
                _dropStatusMessage != null ? _theme.Fail : (online != null ? _theme.Success : _theme.TextSecondary),
                9.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
    }

    private void DrawSignalBars(D2dTarget g, UiRect rect, int signalLevel)
    {
        float barW = 2.2f;
        float gap = 1.5f;
        float baseY = rect.Bottom - 1f;
        for (int i = 0; i < 4; i++)
        {
            float barH = 3f + i * 2.3f;
            bool active = signalLevel >= i + 1;
            UiColor col = active
                ? (i == 3 && signalLevel == 3 ? _theme.Success.WithAlphaFraction(0.6f) : _theme.Success)
                : _theme.TextSecondary.WithAlphaFraction(0.3f);
            g.FillRoundedRect(new UiRect(rect.X + i * (barW + gap), baseY - barH, barW, barH), 1f, col);
        }
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (leftDown && _seeking)
        {
            long durationMs = _mediaState?.DurationMs ?? 0L;
            if (durationMs > 0)
            {
                int pct = UiPrimitives.SliderValueFromX(_seekSliderRect, xDip, thumbDiameter: 10f);
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
        if (e.Button != UiMouseButton.Left) return;
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }
        var hit = _hit.HitTest(e.X, e.Y);
        _hit.ActiveId = hit?.Id;
        if (hit is { } h && h.Id == "companion.seek" && _mediaState is { CanSeek: true, DurationMs: > 0 } st)
        {
            _seeking = true;
            _seekSliderRect = h.Bounds;
            _seekPackage = st.Package;
            _seekTitle = st.Title;
            StopSeekTimer();
            int pct = UiPrimitives.SliderValueFromX(h.Bounds, e.X, thumbDiameter: 10f);
            _seekPositionMs = (long)Math.Round(st.DurationMs * (pct / 100.0));
        }
        Invalidate();
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
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
                OpenPhoneManagementDeepLink();
                break;
            case "companion.refresh":
                _ = _host.Phone.RefreshAsync(restore: true);
                break;
            case "companion.device":
            case "companion.signal":
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
            if (_seeking)
            {
                _seeking = false;
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

    protected override void OnActivated(bool active)
    {
        if (!active && !HasOpenModal) _onDeactivatedCheck?.Invoke();
    }

    private async Task CommitMediaSeekAsync()
    {
        if (!_seeking) return;
        _seeking = false;
        long target = _seekPositionMs;
        var state = _mediaState;
        var client = _mediaClient;
        var cts = _mediaCts;
        if (state?.CanSeek == true && client != null && cts != null && !cts.IsCancellationRequested
            && state.Package == _seekPackage && state.Title == _seekTitle)
        {
            try
            {
                await client.SeekAsync(state.Package, target, cts.Token).ConfigureAwait(false);
                _host.TryPost(() =>
                {
                    if (ReferenceEquals(_mediaState, state))
                    {
                        _mediaState = state with { PositionMs = target, ReceivedTimestamp = Stopwatch.GetTimestamp() };
                    }
                    UpdateSeekAnimationTimer();
                    if (IsVisible) Invalidate();
                });
                return;
            }
            catch (OperationCanceledException) { }
            catch { }
        }
        UpdateSeekAnimationTimer();
        if (IsVisible) Invalidate();
    }

    private void OpenPhoneManagementDeepLink()
    {
        string? profileId = _airPlaySelected ? null : _host.Phone.Store.Profile.Id;
        Hide();
        if (_onManageApps != null)
        {
            // Preserve owner QuickPanel dismissal behavior while deep-linking directly to the selected phone profile.
            _onDeactivatedCheck?.Invoke();
            _host.OpenPhoneManagement(profileId);
        }
        else
        {
            _onDeactivatedCheck?.Invoke();
            _host.OpenPhoneManagement(profileId);
        }
    }

    private void ToggleWholePhoneAudio()
    {
        var device = _activeDevice;
        if (device == null) return;
        var existing = _host.Phone.Sources.Sources.FirstOrDefault(s => s.App.IsDeviceAudio && AndroidDeviceIdentity.Matches(s.Device, device));
        if (existing != null)
            _ = _host.Phone.Sources.RemoveAsync(existing.Id);
        else
            _ = _host.Phone.Sources.AddAsync(device, AndroidApp.DeviceAudio, keepPhoneAudio: false);
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
        var phone = _host.Phone;
        var retained = phone.Store.Profile.RetainedApps;
        if (_activeDevice == null && retained.Count == 0)
            return Array.Empty<AndroidApp>();

        string query = _searchText.Trim();
        var online = _activeDevice;
        var all = new List<AndroidApp>(retained.Count + 1) { AndroidApp.DeviceAudio };
        all.AddRange(retained);

        return all
            .Where(a => a.IsDeviceAudio || query.Length == 0
                || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || a.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.IsDeviceAudio)
            .ThenByDescending(a => IsAppCurrentlyPlaying(a, online, phone))
            .ToArray();
    }

    private bool IsAppCurrentlyPlaying(AndroidApp app, AndroidDevice? online, PhonePreview phone)
    {
        if (app.IsDeviceAudio)
            return _mediaState?.Playing == true || (_mediaState?.PlayingPackages.Count ?? 0) > 0;
        if (_mediaState?.PlayingPackages.Contains(app.Package) == true)
            return true;
        return online != null && phone.Sources.Sources.Any(s => s.App.Package == app.Package && AndroidDeviceIdentity.Matches(s.Device, online));
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
            _dropStatusMessage = null;
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
        _inlineChanged = onChanged;
        _inlineCommitted = onCommitted;
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
        if (_iconDecoding.Add(app.Package))
        {
            byte[] png = app.IconPng;
            string pkg = app.Package;
            _ = Task.Run(() =>
            {
                if (!SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 48, 48, out byte[] pixels, out int pw, out int ph))
                    return;
                Premultiply(pixels);
                _host.TryPost(() =>
                {
                    int nextVer = ++_iconVer;
                    if (_iconCache.Count >= 64 && _iconOrder.Count > 0)
                        _iconCache.Remove(_iconOrder.Dequeue());
                    _iconCache[pkg] = (pixels, pw, ph, nextVer);
                    _iconOrder.Enqueue(pkg);
                    if (IsVisible) Invalidate();
                });
            });
        }
        bgra = Array.Empty<byte>(); w = h = ver = 0;
        return false;
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
            if (!SonicRoute.Native.Graphics.WicIconLoader.TryDecodePngToBgra32(png, 96, 96, out byte[] pixels, out int pw, out int ph))
                return;
            Premultiply(pixels);
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
