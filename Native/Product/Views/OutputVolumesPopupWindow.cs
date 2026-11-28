using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
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
    private readonly Action<string> _selectDefault;
    private readonly Action? _onDeactivatedCheck;
    private readonly Action<string?, int?, bool?>? _onEndpointStateChanged;
    private readonly UiHitMap _hit = new();
    private readonly UiScrollState _scroll = new();



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
        Action<string> selectDefault,
        Action? onDeactivatedCheck = null,
        Action<string?, int?, bool?>? onEndpointStateChanged = null)
        : base(UiMetrics.OutputPopupWidth, 160f, 220f, 90f)
    {
        _host = host;
        _config = config;
        _selectDefault = selectDefault;
        _onDeactivatedCheck = onDeactivatedCheck;
        _onEndpointStateChanged = onEndpointStateChanged;
        _theme = UiThemePalette.Resolve(config);
        TopMost = true;
        ToolWindow = true;
        NoActivate = false;
    }

    internal override void ApplyTheme(UiThemePalette theme)
    {
        _theme = theme;
        if (IsVisible) Invalidate();
    }

    internal void ShowForOwner(QuickPanelWindow owner, UiRect? companionOccupiedDip = null)
    {
        OwnerHandle = owner.Handle;
        _theme = UiThemePalette.Resolve(_config);
        EnsureCreated(L10n.T("Qp.Output"));
        RepositionForOwner(owner, companionOccupiedDip);
        owner.ApplyCompanionEntrance(this);
        if (!IsVisible) Show(activate: false);
        RefreshData(owner, companionOccupiedDip);
    }

    internal void RepositionForOwner(LayeredWindow owner, UiRect? companionOccupiedDip = null, bool deferPresentation = false)
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

        SetPanelBoundsPx((int)MathF.Round(rect.X * s), (int)MathF.Round(rect.Y * s), rect.Width, rect.Height, deferPresentation);
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
        long readRevision = _host.EndpointWriteRevision;

        _ = Task.Run(() =>
        {
            var nextRows = new List<OutputDeviceRow>();
            try
            {
                var devices = ProductAudio.Devices(Flow.Render);
                string? defId = devices.Length > 0 ? ProductAudio.DefaultId(Flow.Render) : null;
                foreach (var dev in devices)
                {
                    if (string.IsNullOrWhiteSpace(dev.Id) || AudioService.IsSystemDefault(dev.Id)) continue;
                    if (_config.PanelOutputVolumesRetainedOnly && !_config.IsDeviceShown(EDataFlow.eRender, dev.PreferenceId)) continue;
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
                    else
                    {
                        _draggingId = null;
                        _hit.ActiveId = null;
                        _host.FlushVolumeWrites();
                    }
                }

                foreach (var next in nextRows)
                    if ((readRevision != _host.EndpointWriteRevision || _host.EndpointVolumePending(next.Id)) &&
                        _rows.Find(row => string.Equals(row.Id, next.Id, StringComparison.OrdinalIgnoreCase)) is { } previous)
                        next.Volume = previous.Volume;

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
        string defaultBadge = L10n.T("Qp.DefaultOutputBadge");
        float badgeWidth = Math.Clamp(MathF.Ceiling(D2dSharedContext.Instance.MeasureText(defaultBadge, UiMetrics.FontFamily,
            10f, 600, rowW, 17f, false).Width) + 12f, 42f, MathF.Max(42f, rowW * .45f));
        g.PushClip(listRect);
        _hit.PushClip(listRect);
        for (int i = 0; i < _rows.Count; i++)
        {
            float ry = listRect.Y + i * UiMetrics.OutputPopupRowHeight - _scroll.Offset;
            if (ry + UiMetrics.OutputPopupRowHeight < listRect.Y || ry > listRect.Bottom) continue;

            var row = _rows[i];
            var nameRect = new UiRect(listRect.X, ry, rowW, 22f);
            string setDefId = $"ov.def:{row.Id}";
            _hit.Add(setDefId, nameRect, UiCursorKind.Hand, row.Name);

            bool nameHot = _hit.IsHover(setDefId);
            if (row.IsDefault)
            {
                g.FillEllipse(nameRect.X + 4f, nameRect.CenterY, 3.5f, 3.5f, _theme.Accent);
                g.DrawText(row.Name, new UiRect(nameRect.X + 12f, nameRect.Y, MathF.Max(10f, rowW - badgeWidth - 18f), nameRect.Height),
                    nameHot ? _theme.Accent : _theme.TextPrimary, 12f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                var badgeRect = new UiRect(nameRect.Right - badgeWidth, nameRect.CenterY - 8.5f, badgeWidth, 17f);
                UiPrimitives.DrawBadge(g, _theme, badgeRect, defaultBadge, _theme.NavSelected, _theme.Accent, fontSize: 10f);
            }
            else
            {
                g.DrawText(row.Name, nameRect, nameHot ? _theme.Accent : _theme.TextPrimary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }

            float ctrlY = ry + 26f;
            var muteRect = new UiRect(listRect.X, ctrlY + 2f, 54f, 28f);
            string muteId = $"ov.mute:{row.Id}";
            UiPrimitives.DrawSecondaryButton(g, _theme, _hit, muteId, muteRect, "",
                enabled: row.Available, radius: 6f,
                icon: row.Muted ? UiIcon.SpeakerMuted : UiIcon.Speaker,
                active: row.Muted,
                tooltip: row.Muted ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute"));

            var pctRect = new UiRect(listRect.X + rowW - 42f, ctrlY, 42f, 32f);
            g.DrawText(row.Available ? $"{row.Volume}%" : "—", pctRect, _theme.TextPrimary,
                12.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center, wrap: false, ellipsis: false);

            var sliderRect = new UiRect(muteRect.Right + 6f, ctrlY, MathF.Max(40f, pctRect.X - 6f - (muteRect.Right + 6f)), 32f);
            string sliderId = $"ov.vol:{row.Id}";
            UiPrimitives.DrawVolumeSlider(g, _theme, _hit, sliderId, sliderRect,
                row.Volume / 100f, 0f, row.Muted, showPeak: false, enabled: row.Available, compact: true);
            if (_hit.AccessibilityEnabled)
                _hit.DescribeVolume(sliderId, sliderRect, row.Name, row.Available, row.Available ? row.Volume : null,
                    row.Id, SetAccessibleVolume);
        }
        _hit.PopClip();
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
                int val = UiPrimitives.VolumeSliderValueFromX(_draggingRect, xDip, compact: true);
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

    private bool TryGetDeviceIndex(ReadOnlySpan<char> deviceId, out int index)
    {
        for (index = 0; index < _rows.Count; index++)
            if (deviceId.Equals(_rows[index].Id, StringComparison.OrdinalIgnoreCase)) return true;
        index = -1;
        return false;
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
        var hit = _hit.HitTest(e.X, e.Y);
        if (hit?.Id == "ov.scroll" && _scroll.BeginDrag(e.Y))
        {
            Invalidate();
            return;
        }

        if (hit is null) return;
        _hit.ActiveId = hit.Value.Id;

        if (hit.Value.Id.StartsWith("ov.vol:", StringComparison.Ordinal) && TryGetDeviceIndex(hit.Value.Id.AsSpan(7), out int vIdx))
        {
            if (vIdx >= 0 && vIdx < _rows.Count && _rows[vIdx].Available)
            {
                _draggingId = _rows[vIdx].Id;
                _draggingRect = hit.Value.Bounds;
                int val = UiPrimitives.VolumeSliderValueFromX(_draggingRect, e.X, compact: true);
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
                && TryGetDeviceIndex(rh.Id.AsSpan(7), out int pIdx) && pIdx >= 0 && pIdx < _rows.Count && _rows[pIdx].Available)
            {
                ShowVolumePresetsMenu(_rows[pIdx], e.X, e.Y);
            }
            return;
        }

        if (e.Button != UiMouseButton.Left) return;
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

        var hit = _hit.HitTest(e.X, e.Y);
        if (hit is null || hit.Value.Id != active) { Invalidate(); return; }

        ActivateFocused(hit.Value, e);
    }

    protected override UiHitMap InputMap => _hit;

    protected override bool ActivateFocused(UiHitItem item, UiPointerEventArgs e)
    {
        if (item.Id.StartsWith("ov.def:", StringComparison.Ordinal) && TryGetDeviceIndex(item.Id.AsSpan(7), out int dIdx))
        {
            if (dIdx >= 0 && dIdx < _rows.Count)
            {
                _selectDefault(_rows[dIdx].Id);
            }
        }
        else if (item.Id.StartsWith("ov.mute:", StringComparison.Ordinal) && TryGetDeviceIndex(item.Id.AsSpan(8), out int mIdx))
        {
            if (mIdx >= 0 && mIdx < _rows.Count && _rows[mIdx].Available)
            {
                var row = _rows[mIdx];
                row.Muted = !row.Muted;
                _onEndpointStateChanged?.Invoke(row.Id, null, row.Muted);
                Invalidate();
                string id = row.Id;
                _host.SubmitPanelAudio(() =>
                {
                    try
                    {
                        var st = _host.Audio.Volume(Flow.Render, id, 0, toggle: true);
                        _host.TryPost(() =>
                        {
                            if (IsDisposed) return;
                            SyncEndpointState(id, st.Percent, st.Muted);
                            _onEndpointStateChanged?.Invoke(id, st.Percent, st.Muted);
                            if (IsVisible) Invalidate();
                        });
                    }
                    catch { _host.PostContinuation(_host.ReportAudioWriteFailure); }
                });
            }
        }
        return true;
    }

    protected override void OnMouseWheel(float xDip, float yDip, float delta, bool horizontal)
    {
        var hit = _hit.HitTest(xDip, yDip);
        int step = Math.Clamp(_config.VolumeStep, 1, 20);
        if (hit is { } item && AdjustSlider(item.Id, delta > 0 ? step : -step)) return;
        _scroll.OnWheel(delta);
        Invalidate();
    }

    private void SetAccessibleVolume(string id, double value) => AdjustSlider(id, (int)Math.Round(value), absolute: true);

    private bool AdjustSlider(string id, int value, bool absolute = false)
    {
        if (!id.StartsWith("ov.vol:", StringComparison.Ordinal) || !TryGetDeviceIndex(id.AsSpan(7), out int index)) return false;
        var row = _rows[index];
        if (!row.Available) return false;
        row.Volume = Math.Clamp(absolute ? value : row.Volume + value, 0, 100);
        QueueDeviceVolume(row.Id, row.Volume);
        _onEndpointStateChanged?.Invoke(row.Id, row.Volume, null);
        Invalidate();
        return true;
    }

    protected override bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        int delta = QuickPanelWindow.PanelSliderKeyDelta(vk);
        if (!ctrl && !alt && delta != 0 && _hit.TryGetFocus(out var item, out _) && AdjustSlider(item.Id, delta)) return true;
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

    private void QueueDeviceVolume(string deviceId, int level) => _host.QueueEndpointVolume(deviceId, level);

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
