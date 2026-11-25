using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// Companion popup window showing individual volume/mute/default controls for visible output devices.
/// Parity with WPF OutputVolumePopupTemplate + OutputVolumePopupLayout, bidirectional sync, and zero idle timers.
/// </summary>
internal sealed class OutputVolumesPopupWindow : LayeredWindow
{
    private static readonly int[] PresetPercentages = [25, 50, 75, 100];

    private sealed class OutputDeviceRow
    {
        public required string Id;
        public required string Name;
        public bool IsDefault;
        public int Volume;
        public bool Muted;
        public bool Available = true;
    }

    private readonly ApplicationHost _host;
    private readonly AppConfig _config;
    private readonly Action? _onDefaultChanged;
    private readonly Action? _onDeactivatedCheck;
    private readonly Action<string?, int?, bool?>? _onEndpointStateChanged;
    private readonly UiHitMap _hit = new();
    private readonly UiScrollState _scroll = new();

    private readonly object _writeGate = new();
    private readonly Dictionary<string, int> _pendingWrites = new(StringComparer.OrdinalIgnoreCase);
    private bool _writeLoopRunning;

    private UiThemePalette _theme;
    private List<OutputDeviceRow> _rows = new();
    private PopupMenuWindow? _presetMenu;
    private string? _draggingId;
    private UiRect _draggingRect;
    private string? _rightPressId;
    private bool _loading;
    private bool _refreshQueued;
    private bool _audioSubscribed;

    internal bool HasOpenModal => _presetMenu is { IsDisposed: false, IsVisible: true };

    internal OutputVolumesPopupWindow(
        ApplicationHost host,
        AppConfig config,
        Action? onDefaultChanged = null,
        Action? onDeactivatedCheck = null,
        Action<string?, int?, bool?>? onEndpointStateChanged = null)
        : base(UiMetrics.OutputPopupWidth, 160f, 220f, 90f)
    {
        _host = host;
        _config = config;
        _onDefaultChanged = onDefaultChanged;
        _onDeactivatedCheck = onDeactivatedCheck;
        _onEndpointStateChanged = onEndpointStateChanged;
        _theme = UiThemePalette.Resolve(config);
        TopMost = true;
        ToolWindow = true;
        NoActivate = false;
    }

    internal void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        if (IsVisible) Invalidate();
    }

    internal void ShowForOwner(LayeredWindow owner, UiRect? companionOccupiedDip = null)
    {
        OwnerHandle = owner.Handle;
        _theme = UiThemePalette.Resolve(_config);
        EnsureCreated(L10n.T("Qp.Output"));
        RepositionForOwner(owner, companionOccupiedDip);
        if (!IsVisible) Show(activate: false);
        RefreshData(owner, companionOccupiedDip);
    }

    internal void RepositionForOwner(LayeredWindow owner, UiRect? companionOccupiedDip = null)
    {
        if (owner.Handle == 0) return;
        GetMonitorWorkAreaForWindow(owner.Handle, out int ml, out int mt, out int mr, out int mb, out uint monDpi);
        float s = monDpi / 96f;
        float workL = ml / s;
        float workT = mt / s;
        float workW = (mr - ml) / s;
        float workH = (mb - mt) / s;

        float ownerL = owner.LeftPx / s;
        float ownerT = owner.TopPx / s;
        float ownerW = UiMetrics.QuickPanelSurfaceWidth; // Surface width (366)
        float ownerH = owner.HeightDip;

        float desiredW = MathF.Min(UiMetrics.OutputPopupWidth, workW * 0.75f);
        float contentH = UiMetrics.OutputPopupHeaderHeight + (_rows.Count == 0 ? 32f : _rows.Count * UiMetrics.OutputPopupRowHeight);
        float desiredH = Math.Clamp(contentH, 90f, MathF.Min(ownerH, workH * 0.75f));

        var rect = ArrangeLayout(
            new UiRect(ownerL, ownerT, ownerW, ownerH),
            new UiRect(workL, workT, workW, workH),
            desiredW,
            desiredH,
            companionOccupiedDip);

        SetBoundsPx((int)MathF.Round(rect.X * s), (int)MathF.Round(rect.Y * s), rect.Width, rect.Height);
    }

    internal void SyncEndpointState(string? deviceId, int? volume = null, bool? muted = null)
    {
        if (_rows.Count == 0) return;
        bool changed = false;
        foreach (var row in _rows)
        {
            if (deviceId != null && !string.Equals(row.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (volume.HasValue && _draggingId != row.Id && row.Volume != volume.Value)
            {
                row.Volume = Math.Clamp(volume.Value, 0, 100);
                changed = true;
            }
            if (muted.HasValue && row.Muted != muted.Value)
            {
                row.Muted = muted.Value;
                changed = true;
            }
        }
        if (changed && IsVisible) Invalidate();
    }

    internal void RefreshData(LayeredWindow? ownerToReposition = null, UiRect? companionOccupiedDip = null)
    {
        if (_loading || IsDisposed)
        {
            _refreshQueued = true;
            return;
        }
        _loading = true;
        _refreshQueued = false;

        _ = Task.Run(() =>
        {
            var nextRows = new List<OutputDeviceRow>();
            try
            {
                var devices = ProductAudio.Devices(Flow.Render);
                string? defId = devices.Length > 0 ? ProductAudio.DefaultId(Flow.Render) : null;
                var hidden = _config.PanelOutputVolumesRetainedOnly ? _config.HiddenOutputDevices : null;
                foreach (var dev in devices)
                {
                    if (string.IsNullOrWhiteSpace(dev.Id) || AudioService.IsSystemDefault(dev.Id)) continue;
                    if (hidden != null && hidden.Contains(dev.Id)) continue;
                    string name = _config.DeviceNames.GetValueOrDefault(dev.Id, dev.Name);
                    int vol = 0;
                    bool muted = false;
                    bool avail = true;
                    try
                    {
                        var st = _host.Audio.Volume(Flow.Render, dev.Id, 0);
                        vol = st.Percent;
                        muted = st.Muted;
                    }
                    catch { avail = false; }

                    nextRows.Add(new OutputDeviceRow
                    {
                        Id = dev.Id,
                        Name = name,
                        IsDefault = defId != null && string.Equals(dev.Id, defId, StringComparison.OrdinalIgnoreCase),
                        Volume = vol,
                        Muted = muted,
                        Available = avail,
                    });
                }
            }
            catch { }

            _host.TryPost(() =>
            {
                _loading = false;
                if (IsDisposed) return;
                if (_draggingId != null)
                {
                    var dragged = _rows.FirstOrDefault(r => string.Equals(r.Id, _draggingId, StringComparison.OrdinalIgnoreCase));
                    var target = nextRows.FirstOrDefault(r => string.Equals(r.Id, _draggingId, StringComparison.OrdinalIgnoreCase));
                    if (dragged != null && target != null) target.Volume = dragged.Volume;
                }

                int prevCount = _rows.Count;
                _rows = nextRows;
                if (ownerToReposition != null && IsVisible && prevCount != _rows.Count)
                {
                    RepositionForOwner(ownerToReposition, companionOccupiedDip);
                }
                if (IsVisible) Invalidate();
                if (_refreshQueued && IsVisible)
                {
                    _refreshQueued = false;
                    RefreshData(ownerToReposition, companionOccupiedDip);
                }
            });
        });
    }

    protected override void OnShown()
    {
        base.OnShown();
        SubscribeAudio(true);
    }

    protected override void OnHidden()
    {
        SubscribeAudio(false);
        ClosePresetMenu();
        CancelActiveDrags();
        base.OnHidden();
    }

    protected override void OnClosed()
    {
        SubscribeAudio(false);
        ClosePresetMenu();
        CancelActiveDrags();
        base.OnClosed();
    }

    protected override void OnCaptureLost()
    {
        bool hadDrag = _draggingId != null || _scroll.Dragging;
        CancelActiveDrags();
        _host.FlushVolumeWrites();
        if (hadDrag && IsVisible) Invalidate();
    }

    protected override string GetWindowTitle() => L10n.T("Qp.Output");

    protected override void OnRender(D2dTarget g, float widthDip, float heightDip)
    {
        _hit.Clear();
        var outer = new UiRect(0f, 0f, widthDip, heightDip);
        g.FillRoundedRect(outer, UiMetrics.QuickPanelCornerRadius, _theme.WindowBgAlpha);
        g.DrawRoundedRect(outer, UiMetrics.QuickPanelCornerRadius, _theme.Border, 1f);

        const float padX = 14f;
        const float padY = 12f;

        var headerRect = new UiRect(padX, padY, widthDip - padX * 2f, 24f);
        g.DrawText(L10n.T("Qp.Output"), headerRect, _theme.TextSecondary, 12.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);

        var listRect = new UiRect(padX, headerRect.Bottom + 8f, widthDip - padX * 2f, MathF.Max(20f, heightDip - headerRect.Bottom - 8f - padY));
        _scroll.ViewportHeight = listRect.Height;
        _scroll.ContentHeight = _rows.Count * UiMetrics.OutputPopupRowHeight;
        _scroll.Clamp();

        if (_rows.Count == 0)
        {
            g.DrawText(L10n.T("Ov.NoDevice"), listRect, _theme.TextSecondary, 12.5f, 400, UiTextAlign.Center, UiVertAlign.Center);
            return;
        }

        float rowW = _scroll.MaxOffset > 0.5f ? listRect.Width - UiMetrics.ScrollBarWidth - 4f : listRect.Width;
        g.PushClip(listRect);
        for (int i = 0; i < _rows.Count; i++)
        {
            float ry = listRect.Y + i * UiMetrics.OutputPopupRowHeight - _scroll.Offset;
            if (ry + UiMetrics.OutputPopupRowHeight < listRect.Y || ry > listRect.Bottom) continue;

            var row = _rows[i];
            var nameRect = new UiRect(listRect.X, ry, rowW, 22f);
            string setDefId = $"ov.def:{i}";
            _hit.Add(setDefId, nameRect, UiCursorKind.Hand, row.Name);

            bool nameHot = _hit.IsHover(setDefId);
            float textMaxW = rowW - (row.IsDefault ? 48f : 0f);
            if (row.IsDefault)
            {
                g.FillEllipse(nameRect.X + 4f, nameRect.CenterY, 3.5f, 3.5f, _theme.Accent);
                g.DrawText(row.Name, new UiRect(nameRect.X + 12f, nameRect.Y, MathF.Max(10f, textMaxW - 12f), nameRect.Height),
                    nameHot ? _theme.Accent : _theme.TextPrimary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                var badgeRect = new UiRect(nameRect.Right - 42f, nameRect.CenterY - 8.5f, 42f, 17f);
                UiPrimitives.DrawBadge(g, _theme, badgeRect, L10n.T("Qp.DefaultOutputBadge"), _theme.NavSelected, _theme.Accent, fontSize: 10f);
            }
            else
            {
                g.DrawText(row.Name, nameRect, nameHot ? _theme.Accent : _theme.TextPrimary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }

            float ctrlY = ry + 26f;
            var muteRect = new UiRect(listRect.X, ctrlY + 2f, 54f, 28f);
            string muteId = $"ov.mute:{i}";
            UiPrimitives.DrawSecondaryButton(g, _theme, _hit, muteId, muteRect, "",
                enabled: row.Available, radius: 6f,
                icon: row.Muted ? UiIcon.VolumeMutedSlash : UiIcon.Speaker,
                active: row.Muted,
                tooltip: row.Muted ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute"));

            var pctRect = new UiRect(listRect.X + rowW - 42f, ctrlY, 42f, 32f);
            g.DrawText(row.Available ? $"{row.Volume}%" : "—", pctRect, _theme.TextPrimary,
                12.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center, wrap: false, ellipsis: false);

            var sliderRect = new UiRect(muteRect.Right + 6f, ctrlY, MathF.Max(40f, pctRect.X - 6f - (muteRect.Right + 6f)), 32f);
            string sliderId = $"ov.vol:{i}";
            UiPrimitives.DrawVolumeSlider(g, _theme, _hit, sliderId, sliderRect,
                row.Volume / 100f, 0f, row.Muted, showPeak: false, trackHeight: 4f, thumbDiameter: 14f, enabled: row.Available);
        }
        g.PopClip();

        _scroll.Draw(g, _theme, _hit, "ov.scroll", listRect);
    }

    protected override void OnPointerMove(float xDip, float yDip, bool leftDown)
    {
        if (leftDown && _scroll.DragMove(yDip)) { Invalidate(); return; }
        if (leftDown && _draggingId != null)
        {
            int idx = _rows.FindIndex(r => r.Id == _draggingId);
            if (idx >= 0)
            {
                int val = UiPrimitives.SliderValueFromX(_draggingRect, xDip);
                if (_rows[idx].Volume != val)
                {
                    _rows[idx].Volume = val;
                    QueueDeviceVolume(_rows[idx].Id, val);
                    _onEndpointStateChanged?.Invoke(_rows[idx].Id, val, null);
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
        if (e.Button == UiMouseButton.Right)
        {
            var rHit = _hit.HitTest(e.X, e.Y);
            _rightPressId = rHit?.Id;
            return;
        }
        if (e.Button != UiMouseButton.Left) return;

        ClosePresetMenu();
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit is null) return;
        _hit.ActiveId = hit.Value.Id;

        if (hit.Value.Id.StartsWith("ov.vol:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(7), out int vIdx))
        {
            if (vIdx >= 0 && vIdx < _rows.Count && _rows[vIdx].Available)
            {
                _draggingId = _rows[vIdx].Id;
                _draggingRect = hit.Value.Bounds;
                int val = UiPrimitives.SliderValueFromX(_draggingRect, e.X);
                _rows[vIdx].Volume = val;
                QueueDeviceVolume(_rows[vIdx].Id, val);
                _onEndpointStateChanged?.Invoke(_rows[vIdx].Id, val, null);
                Invalidate();
            }
            return;
        }
        Invalidate();
    }

    protected override void OnPointerUp(UiPointerEventArgs e)
    {
        if (e.Button == UiMouseButton.Right)
        {
            string? pressed = _rightPressId;
            _rightPressId = null;
            var rHit = _hit.HitTest(e.X, e.Y);
            if (rHit is { } rh && rh.Id == pressed && rh.Id.StartsWith("ov.vol:", StringComparison.Ordinal)
                && int.TryParse(rh.Id.AsSpan(7), out int pIdx) && pIdx >= 0 && pIdx < _rows.Count && _rows[pIdx].Available)
            {
                ShowVolumePresetsMenu(_rows[pIdx], e.X, e.Y);
            }
            return;
        }

        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            Invalidate();
            return;
        }
        if (_draggingId != null)
        {
            _draggingId = null;
            _hit.ActiveId = null;
            _host.FlushVolumeWrites();
            Invalidate();
            return;
        }

        string? active = _hit.ActiveId;
        _hit.ActiveId = null;
        if (e.Button != UiMouseButton.Left) return;

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit is null || hit.Value.Id != active) { Invalidate(); return; }

        if (hit.Value.Id.StartsWith("ov.def:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(7), out int dIdx))
        {
            if (dIdx >= 0 && dIdx < _rows.Count)
            {
                string id = _rows[dIdx].Id;
                _ = Task.Run(() =>
                {
                    try
                    {
                        _host.Audio.SetDefault(Flow.Render, id);
                        _host.TryPost(() => { _onDefaultChanged?.Invoke(); RefreshData(); });
                    }
                    catch { }
                });
            }
        }
        else if (hit.Value.Id.StartsWith("ov.mute:", StringComparison.Ordinal) && int.TryParse(hit.Value.Id.AsSpan(8), out int mIdx))
        {
            if (mIdx >= 0 && mIdx < _rows.Count && _rows[mIdx].Available)
            {
                var row = _rows[mIdx];
                row.Muted = !row.Muted;
                _onEndpointStateChanged?.Invoke(row.Id, null, row.Muted);
                Invalidate();
                string id = row.Id;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var st = _host.Audio.Volume(Flow.Render, id, 0, toggle: true);
                        _host.TryPost(() =>
                        {
                            row.Muted = st.Muted;
                            row.Volume = st.Percent;
                            _onEndpointStateChanged?.Invoke(id, st.Percent, st.Muted);
                            if (IsVisible) Invalidate();
                        });
                    }
                    catch { }
                });
            }
        }
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        var hit = _hit.HitTest(xDip, yDip);
        if (hit is { } h && h.Id.StartsWith("ov.vol:", StringComparison.Ordinal) && int.TryParse(h.Id.AsSpan(7), out int idx))
        {
            if (idx >= 0 && idx < _rows.Count && _rows[idx].Available)
            {
                int step = Math.Clamp(_config.VolumeStep, 1, 20);
                int next = Math.Clamp(_rows[idx].Volume + (delta > 0 ? step : -step), 0, 100);
                _rows[idx].Volume = next;
                QueueDeviceVolume(_rows[idx].Id, next);
                _onEndpointStateChanged?.Invoke(_rows[idx].Id, next, null);
                Invalidate();
                return;
            }
        }
        _scroll.OnWheel(delta);
        Invalidate();
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            if (HasOpenModal) { ClosePresetMenu(); return true; }
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

    private void ShowVolumePresetsMenu(OutputDeviceRow row, float xDip, float yDip)
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
                    QueueDeviceVolume(row.Id, captured);
                    _onEndpointStateChanged?.Invoke(row.Id, captured, null);
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

    private void CancelActiveDrags()
    {
        _draggingId = null;
        _rightPressId = null;
        _scroll.EndDrag();
        _hit.ActiveId = null;
    }

    private void QueueDeviceVolume(string deviceId, int level)
    {
        bool startWorker = false;
        lock (_writeGate)
        {
            _pendingWrites[deviceId] = level;
            if (!_writeLoopRunning)
            {
                _writeLoopRunning = true;
                startWorker = true;
            }
        }
        if (!startWorker) return;

        _ = Task.Run(() =>
        {
            while (true)
            {
                KeyValuePair<string, int> next;
                lock (_writeGate)
                {
                    if (_pendingWrites.Count == 0)
                    {
                        _writeLoopRunning = false;
                        break;
                    }
                    next = _pendingWrites.First();
                    _pendingWrites.Remove(next.Key);
                }
                try { _host.Audio.Volume(Flow.Render, next.Key, 0, level: next.Value); }
                catch { }
            }
        });
    }

    private void SubscribeAudio(bool enable)
    {
        if (enable == _audioSubscribed) return;
        _audioSubscribed = enable;
        if (enable) _host.Audio.AudioChanged += OnAudioChanged;
        else _host.Audio.AudioChanged -= OnAudioChanged;
    }

    private void OnAudioChanged()
    {
        if (!IsVisible || IsDisposed) return;
        _host.TryPost(() =>
        {
            if (IsVisible && !IsDisposed) RefreshData();
        });
    }

    private static UiRect ArrangeLayout(UiRect owner, UiRect work, float desiredW, float desiredH, UiRect? occupied)
    {
        const float gap = 6f;
        float width = MathF.Min(desiredW, work.Width);
        float height = MathF.Min(desiredH, work.Height);

        var spaces = new UiRect[]
        {
            new(work.X, work.Y, work.Width, MathF.Max(0f, owner.Y - gap - work.Y)), // 0: above
            new(MathF.Min(work.Right, owner.Right + gap), work.Y, MathF.Max(0f, work.Right - owner.Right - gap), work.Height), // 1: right
            new(work.X, work.Y, MathF.Max(0f, owner.X - gap - work.X), work.Height), // 2: left
            new(work.X, MathF.Min(work.Bottom, owner.Bottom + gap), work.Width, MathF.Max(0f, work.Bottom - owner.Bottom - gap)), // 3: below
        };

        // Exact WPF OutputVolumePopupLayout priority: [3, 0, 1, 2] when companion occupied, [0, 1, 2, 3] otherwise
        int[] order = occupied.HasValue ? [3, 0, 1, 2] : [0, 1, 2, 3];
        int chosen = -1;
        float bestArea = -1f;

        UiRect Candidate(int side, UiRect avail, float candH)
        {
            float cx = side == 1 ? avail.X : side == 2 ? avail.Right - width : owner.X;
            float cy = side == 0 ? avail.Bottom - candH : side == 3 ? avail.Y : owner.Y;
            return new UiRect(
                Math.Clamp(cx, work.X, MathF.Max(work.X, work.Right - width)),
                Math.Clamp(cy, work.Y, MathF.Max(work.Y, work.Bottom - candH)),
                width, candH);
        }

        static bool Intersects(UiRect a, UiRect b) =>
            a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

        foreach (int i in order)
        {
            var space = spaces[i];
            float candH = MathF.Min(height, space.Height);
            var cand = Candidate(i, space, candH);
            if (occupied.HasValue && Intersects(cand, occupied.Value)) continue;
            if (space.Width >= width && space.Height >= height)
            {
                chosen = i;
                break;
            }
            float area = space.Width >= width ? width * MathF.Min(height, space.Height) : 0f;
            if (area > bestArea)
            {
                bestArea = area;
                chosen = i;
            }
        }

        if (chosen < 0 || (bestArea == 0f && (spaces[chosen].Width < width || spaces[chosen].Height <= 0f)))
        {
            return new UiRect(work.X, work.Y, width, height);
        }

        var chosenSpace = spaces[chosen];
        return Candidate(chosen, chosenSpace, MathF.Min(height, chosenSpace.Height));
    }
}
