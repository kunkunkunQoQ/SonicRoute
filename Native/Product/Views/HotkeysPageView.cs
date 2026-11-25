using System;
using System.Collections.Generic;
using System.Linq;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class HotkeysPageView : IViewPage
{
    private const float RowH = 42f;
    private Dictionary<string, string>? _registrationsBeforeCapture;

    private readonly IViewHostActions _actions;
    private readonly UiScrollState _scroll = new();
    private readonly Dictionary<string, Action> _buttons = new(StringComparer.Ordinal);
    private UiRect _viewportRect;

    private string? _recordingAction;

    internal HotkeysPageView(IViewHostActions actions)
    {
        _actions = actions;
    }

    public void OnActivated()
    {
        RefreshData();
    }

    public void OnDeactivated()
    {
        CancelRecording();
    }

    public void OnFocusLost() => CancelRecording();

    public void OnCaptureLost()
    {
        _scroll.EndDrag();
    }

    public void RefreshData() => _actions.Invalidate();

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _buttons.Clear();
        _viewportRect = contentRect;

        string hint = L10n.T("Hk.Hint");
        float hintH = D2dSharedContext.Instance.MeasureText(hint, UiMetrics.FontFamily, 12f, 400, contentRect.Width, 80f, wrap: true).Height;

        bool expMic = _actions.Config.ExperimentalMic;
        float cardInnerH = 0f;
        foreach (var (_, actions) in HotkeyActions.Groups)
        {
            int visibleCount = actions.Count(a => (a != HotkeyActions.ActSwitchInput && a != HotkeyActions.ActSwitchAllInput) || expMic);
            if (visibleCount == 0) continue;
            cardInnerH += 34f + visibleCount * RowH;
        }
        float mainCardH = cardInnerH + 38f;
        float headerH = hintH + 16f;
        float totalH = headerH + mainCardH;

        _scroll.ViewportHeight = contentRect.Height;
        _scroll.ContentHeight = totalH;
        _scroll.Clamp();

        float w = _scroll.MaxOffset > 0.5f ? contentRect.Width - UiMetrics.ScrollBarWidth - 4f : contentRect.Width;
        var registered = _registrationsBeforeCapture ?? _actions.Host.HotkeyStatuses;

        g.PushClip(contentRect);
        hit.PushClip(contentRect);
        float y = contentRect.Y - _scroll.Offset;

        g.DrawText(hint, new UiRect(contentRect.X, y, w, hintH + 4f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
        y += hintH + 16f;

        var mainCard = new UiRect(contentRect.X, y, w, mainCardH);
        UiPrimitives.DrawCard(g, t, mainCard, UiMetrics.CardCornerRadius);
        float cy = mainCard.Y + 19f;
        float cx = mainCard.X + 19f;
        float cw = mainCard.Width - 38f;

        foreach (var (l10nKey, groupActions) in HotkeyActions.Groups)
        {
            var visible = groupActions
                .Where(a => (a != HotkeyActions.ActSwitchInput && a != HotkeyActions.ActSwitchAllInput) || expMic)
                .ToArray();
            if (visible.Length == 0) continue;

            g.DrawText(L10n.T(l10nKey), new UiRect(cx, cy + 12f, cw, 18f), t.Accent, 13f, 600);
            cy += 34f;

            foreach (string action in visible)
            {
                string combo = GetConfiguredCombo(action);
                bool isRecording = _recordingAction == action;
                ResolveDisplayAndConflict(action, combo, registered, out string display, out string? tip);
                string actCopy = action;

                DrawHotkeyRow(
                    g, t, hit,
                    "hk.act." + action,
                    new UiRect(cx, cy, cw, RowH),
                    HotkeyActions.DisplayName(action),
                    display,
                    isRecording,
                    tip,
                    () => StartRecordingAction(actCopy));

                cy += RowH;
            }
        }

        hit.PopClip();
        g.PopClip();
        _scroll.Draw(g, t, hit, "hk.scroll", contentRect);
    }

    private static void ResolveDisplayAndConflict(string key, string combo,
        IReadOnlyDictionary<string, string> registered, out string display, out string? tooltip)
    {
        display = combo;
        tooltip = null;
        if (combo.Length == 0) return;
        if (!registered.TryGetValue(key, out string? actual) || string.IsNullOrEmpty(actual))
        {
            display = combo + " ⚠";
            tooltip = L10n.T("Hk.Unregistered");
        }
        else if (!string.Equals(combo, actual, StringComparison.OrdinalIgnoreCase))
        {
            display = actual + " ⚠";
            tooltip = string.Format(L10n.T("Hk.ConflictTip"), combo, actual);
        }
    }

    private void DrawHotkeyRow(D2dTarget g, UiThemePalette theme, UiHitMap hit, string idPrefix, UiRect row,
        string label, string display, bool recording, string? tooltip, Action record)
    {
        float width = MathF.Min(180f, row.Width * 0.70f);
        var box = new UiRect(row.Right - width, row.CenterY - 16f, width, 32f);
        g.DrawText(label, new UiRect(row.X, row.Y, box.X - row.X - 10f, row.Height),
            theme.TextPrimary, 13f, 400, UiTextAlign.Leading, UiVertAlign.Center, wrap: true);
        string id = idPrefix + ".pill";
        HotkeyBinding.Draw(g, theme, hit, id, box,
            recording ? L10n.T("Hk.RecordingPrompt") : display, tooltip);
        _buttons[id] = record;
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        return leftDown && _scroll.DragMove(y);
    }

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        if (_recordingAction != null)
        {
            string? mouseName = HotkeyBinding.MouseButton(e.Button);
            if (mouseName != null)
            {
                CommitRecordedCombo(HotkeyBinding.Format(mouseName, e.Ctrl, e.Alt, e.Shift));
                return true;
            }
        }

        if (e.Button == UiMouseButton.Left && (targetId == null || !targetId.EndsWith(".pill", StringComparison.Ordinal)))
            CancelRecording();

        if (e.Button == UiMouseButton.Left && _scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
            return true;

        return false;
    }

    public bool OnPointerUp(UiPointerEventArgs e, string? clickId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            return true;
        }
        if (clickId == null || !_buttons.TryGetValue(clickId, out var click)) return false;
        click();
        return true;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (_recordingAction != null)
        {
            if (horizontal || delta == 0) return false;
            CommitRecordedCombo(HotkeyBinding.Wheel(delta));
            return true;
        }
        if (_viewportRect.Contains(x, y))
        {
            _scroll.OnWheel(delta);
            return true;
        }
        return false;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (_recordingAction == null) return false;

        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            CommitRecordedCombo("");
            return true;
        }

        if (HotkeyBinding.Keyboard(vk, ctrl, alt, shift) is string combo)
            CommitRecordedCombo(combo);
        return true;
    }

    private void StartRecordingAction(string action)
    {
        _recordingAction = action;
        _registrationsBeforeCapture ??= new(_actions.Host.HotkeyStatuses, StringComparer.Ordinal);
        _actions.Host.HoldHotkeys(true);
        _actions.Invalidate();
    }

    private void CancelRecording()
    {
        if (_recordingAction == null) return;
        _recordingAction = null;
        _registrationsBeforeCapture = null;
        _actions.Host.HoldHotkeys(false);
        _actions.Invalidate();
    }

    private void CommitRecordedCombo(string combo)
    {
        if (_recordingAction is not string action) return;
        _actions.Config.Hotkeys[action] = combo;
        bool saved = _actions.CommitSettings();
        CancelRecording(); // reloads the confirmed bindings before displaying registration status
        if (saved)
        {
            ResolveDisplayAndConflict(action, combo, _actions.Host.HotkeyStatuses, out _, out string? warning);
            _actions.ShowNotice(warning ?? L10n.T("Hk.Updated"));
        }
    }

    private string GetConfiguredCombo(string action) =>
        _actions.Config.Hotkeys.TryGetValue(action, out var c) ? c
        : HotkeyActions.Defaults.TryGetValue(action, out var d) ? d : "";

    public void Dispose()
    {
        CancelRecording();
    }

}
