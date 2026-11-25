using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class HotkeysPageView : IViewPage
{
    private const float RowH = 40f;

    private readonly IViewHostActions _actions;
    private readonly UiScrollState _scroll = new();
    private readonly List<(UiRect Rect, Action Click)> _buttons = new();
    private List<AutoRule> _hotkeyRules = new();
    private UiRect _viewportRect;

    private string? _recordingAction;
    private string? _recordingRuleId;

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

    public void RefreshData()
    {
        _hotkeyRules = AutoRuleStore.LoadAll()
            .Where(r => r.Trigger == AutoRuleTrigger.Hotkey)
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _actions.Invalidate();
    }

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _buttons.Clear();
        _viewportRect = contentRect;

        string title = L10n.T("Nav.Hotkeys");
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
        float rulesCardH = _hotkeyRules.Count > 0 ? 36f + _hotkeyRules.Count * RowH + 24f : 0f;
        float mainCardH = cardInnerH + 28f;
        float headerH = 32f + hintH + 16f;
        float totalH = headerH + mainCardH + (rulesCardH > 0 ? 14f + rulesCardH : 0f) + 8f;

        _scroll.ViewportHeight = contentRect.Height;
        _scroll.ContentHeight = totalH;
        _scroll.Clamp();

        float w = _scroll.MaxOffset > 0.5f ? contentRect.Width - UiMetrics.ScrollBarWidth - 4f : contentRect.Width;
        var conflictMap = BuildConflictSet();

        g.PushClip(contentRect);
        float y = contentRect.Y - _scroll.Offset;

        g.DrawText(title, new UiRect(contentRect.X, y, w, 28f), t.TextPrimary, 22f, 600);
        y += 32f;
        g.DrawText(hint, new UiRect(contentRect.X, y, w, hintH + 4f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
        y += hintH + 16f;

        var mainCard = new UiRect(contentRect.X, y, w, mainCardH);
        UiPrimitives.DrawCard(g, t, mainCard, UiMetrics.CardCornerRadius);
        float cy = mainCard.Y + 14f;
        float cx = mainCard.X + 18f;
        float cw = mainCard.Width - 36f;

        foreach (var (l10nKey, groupActions) in HotkeyActions.Groups)
        {
            var visible = groupActions
                .Where(a => (a != HotkeyActions.ActSwitchInput && a != HotkeyActions.ActSwitchAllInput) || expMic)
                .ToArray();
            if (visible.Length == 0) continue;

            g.DrawText(L10n.T(l10nKey), new UiRect(cx, cy + 8f, cw, 22f), t.Accent, 13f, 600);
            cy += 34f;

            foreach (string action in visible)
            {
                string combo = GetConfiguredCombo(action);
                bool isRecording = _recordingAction == action && _recordingRuleId == null;
                bool hasConflict = !string.IsNullOrWhiteSpace(combo) && conflictMap.Contains(combo);
                string actCopy = action;

                DrawHotkeyRow(
                    g, t, hit,
                    "hk.act." + action,
                    new UiRect(cx, cy, cw, RowH),
                    HotkeyActions.DisplayName(action),
                    combo,
                    isRecording,
                    hasConflict,
                    () => StartRecordingAction(actCopy),
                    () => ClearActionHotkey(actCopy));

                cy += RowH;
            }
        }

        y += mainCardH;

        if (_hotkeyRules.Count > 0)
        {
            y += 14f;
            var ruleCard = new UiRect(contentRect.X, y, w, rulesCardH);
            UiPrimitives.DrawCard(g, t, ruleCard, UiMetrics.CardCornerRadius);
            float ry = ruleCard.Y + 14f;
            g.DrawText(L10n.T("Auto.Favorites"), new UiRect(cx, ry + 6f, cw, 22f), t.Accent, 13f, 600);
            ry += 34f;

            foreach (var rule in _hotkeyRules)
            {
                string combo = rule.Hotkey ?? "";
                bool isRecording = _recordingRuleId == rule.Id;
                bool hasConflict = !string.IsNullOrWhiteSpace(combo) && conflictMap.Contains(combo);
                var ruleCopy = rule;

                DrawHotkeyRow(
                    g, t, hit,
                    "hk.rule." + rule.Id,
                    new UiRect(cx, ry, cw, RowH),
                    rule.Name,
                    combo,
                    isRecording,
                    hasConflict,
                    () => StartRecordingRule(ruleCopy.Id),
                    () => ClearRuleHotkey(ruleCopy));

                ry += RowH;
            }
        }

        g.PopClip();
        _scroll.Draw(g, t, hit, "hk.scroll", contentRect);
    }

    private void DrawHotkeyRow(
        D2dTarget g, UiThemePalette t, UiHitMap hit, string idPrefix, UiRect row,
        string label, string combo, bool isRecording, bool hasConflict,
        Action onRecord, Action onClear)
    {
        float clearW = 58f;
        float pillW = 190f;
        float recBtnW = 58f;
        float rightCluster = pillW + 8f + recBtnW + 6f + clearW;

        var labelRect = new UiRect(row.X, row.Y, MathF.Max(60f, row.Width - rightCluster - 12f), row.Height);
        g.DrawText(label, labelRect, t.TextPrimary, 13f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        var clearRect = new UiRect(row.Right - clearW, row.CenterY - 16f, clearW, 32f);
        var recRect = new UiRect(clearRect.X - 6f - recBtnW, row.CenterY - 16f, recBtnW, 32f);
        var pillRect = new UiRect(recRect.X - 8f - pillW, row.CenterY - 16f, pillW, 32f);

        string shown = isRecording
            ? L10n.T("Hk.RecordingPrompt")
            : string.IsNullOrWhiteSpace(combo)
                ? L10n.T("Ov.Unset")
                : hasConflict ? combo + " ⚠" : combo;

        string pillId = idPrefix + ".pill";
        UiPrimitives.DrawSecondaryButton(
            g, t, hit, pillId, pillRect, shown,
            fontSize: 12.5f, active: isRecording,
            tooltip: hasConflict ? L10n.T("Hk.Conflict") : null);
        _buttons.Add((pillRect, onRecord));

        string recId = idPrefix + ".rec";
        UiPrimitives.DrawSecondaryButton(
            g, t, hit, recId, recRect,
            isRecording ? L10n.T("Auto.Cancel") : L10n.T("Hk.Change"),
            fontSize: 12f, active: isRecording);
        _buttons.Add((recRect, isRecording ? CancelRecording : onRecord));

        string clrId = idPrefix + ".clr";
        bool canClear = !string.IsNullOrWhiteSpace(combo) || isRecording;
        UiPrimitives.DrawSecondaryButton(
            g, t, hit, clrId, clearRect,
            L10n.T("Auto.HotkeyClear"),
            enabled: canClear,
            fontSize: 12f);
        if (canClear) _buttons.Add((clearRect, onClear));
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        return leftDown && _scroll.DragMove(y);
    }

    public bool OnPointerDown(UiPointerEventArgs e)
    {
        if ((_recordingAction != null || _recordingRuleId != null) &&
            e.Button == UiMouseButton.Middle)
        {
            CommitRecordedCombo(FormatMouseButton("MButton"));
            return true;
        }

        if (e.Button == UiMouseButton.Left && _scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
            return true;

        return false;
    }

    public bool OnPointerUp(UiPointerEventArgs e)
    {
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            return true;
        }
        if (e.Button != UiMouseButton.Left) return false;

        for (int i = _buttons.Count - 1; i >= 0; i--)
        {
            if (_buttons[i].Rect.Contains(e.X, e.Y))
            {
                _buttons[i].Click();
                return true;
            }
        }
        return false;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (_recordingAction != null || _recordingRuleId != null)
        {
            CommitRecordedCombo(FormatMouseButton(delta > 0 ? "WheelUp" : "WheelDown"));
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
        if (_recordingAction == null && _recordingRuleId == null) return false;

        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            CommitRecordedCombo("");
            return true;
        }

        string? keyName = VirtualKeyToName(vk);
        if (keyName == null) return true;

        var parts = new List<string>(5);
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) parts.Add("Win");
        parts.Add(keyName);
        CommitRecordedCombo(string.Join("+", parts));
        return true;
    }

    private void StartRecordingAction(string action)
    {
        _recordingRuleId = null;
        _recordingAction = action;
        _actions.Host.HoldHotkeys(true);
        _actions.Invalidate();
    }

    private void StartRecordingRule(string ruleId)
    {
        _recordingAction = null;
        _recordingRuleId = ruleId;
        _actions.Host.HoldHotkeys(true);
        _actions.Invalidate();
    }

    private void CancelRecording()
    {
        if (_recordingAction == null && _recordingRuleId == null) return;
        _recordingAction = null;
        _recordingRuleId = null;
        _actions.Host.HoldHotkeys(false);
        _actions.Invalidate();
    }

    private void CommitRecordedCombo(string combo)
    {
        string? action = _recordingAction;
        string? ruleId = _recordingRuleId;
        _recordingAction = null;
        _recordingRuleId = null;
        _actions.Host.HoldHotkeys(false);

        if (action != null)
        {
            _actions.Config.Hotkeys[action] = combo;
            if (!ConfigService.TrySave(_actions.Config))
                _actions.ShowNotice(L10n.T("St.SaveFailed"));
            else
                _actions.ShowNotice(L10n.T("Hk.Updated"));
            _actions.Host.ViewSettingsChanged();
        }
        else if (ruleId != null)
        {
            var rule = _hotkeyRules.FirstOrDefault(r => r.Id == ruleId);
            if (rule != null)
            {
                rule.Hotkey = combo;
                AutoRuleStore.Save(rule);
                _actions.ShowNotice(L10n.T("Hk.Updated"));
                _actions.Host.ViewSettingsChanged();
            }
        }
        _actions.Invalidate();
    }

    private void ClearActionHotkey(string action)
    {
        CancelRecording();
        _actions.Config.Hotkeys[action] = "";
        ConfigService.TrySave(_actions.Config);
        _actions.Host.ViewSettingsChanged();
        _actions.ShowNotice(L10n.T("Hk.Updated"));
        _actions.Invalidate();
    }

    private void ClearRuleHotkey(AutoRule rule)
    {
        CancelRecording();
        rule.Hotkey = "";
        AutoRuleStore.Save(rule);
        _actions.Host.ViewSettingsChanged();
        _actions.ShowNotice(L10n.T("Hk.Updated"));
        _actions.Invalidate();
    }

    private string GetConfiguredCombo(string action) =>
        _actions.Config.Hotkeys.TryGetValue(action, out var c) ? c
        : HotkeyActions.Defaults.TryGetValue(action, out var d) ? d : "";

    private HashSet<string> BuildConflictSet()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool expMic = _actions.Config.ExperimentalMic;

        foreach (string action in HotkeyActions.All)
        {
            if (!expMic && action is HotkeyActions.ActSwitchInput or HotkeyActions.ActSwitchAllInput) continue;
            string combo = GetConfiguredCombo(action);
            if (!string.IsNullOrWhiteSpace(combo) && !seen.Add(combo))
                conflicts.Add(combo);
        }
        foreach (var rule in _hotkeyRules)
        {
            if (rule.Enabled && !string.IsNullOrWhiteSpace(rule.Hotkey) && !seen.Add(rule.Hotkey))
                conflicts.Add(rule.Hotkey);
        }
        return conflicts;
    }

    private static string FormatMouseButton(string button)
    {
        var parts = new List<string>(5);
        if (GetKeyState(0x11) < 0) parts.Add("Ctrl");
        if (GetKeyState(0x12) < 0) parts.Add("Alt");
        if (GetKeyState(0x10) < 0) parts.Add("Shift");
        if (GetKeyState(0x5B) < 0 || GetKeyState(0x5C) < 0) parts.Add("Win");
        parts.Add(button);
        return string.Join("+", parts);
    }

    private static string? VirtualKeyToName(int vk)
    {
        if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x1B)
            return null;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        if (vk is >= 0x60 and <= 0x69) return "NumPad" + (vk - 0x60);
        return vk switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x08 => "Back",
            0x90 => "NumLock",
            0xBB => "OemPlus",
            0xBD => "OemMinus",
            0xBC => "OemComma",
            0xBE => "OemPeriod",
            0xBF => "OemQuestion",
            0xDB => "OemOpenBrackets",
            0xDD => "OemCloseBrackets",
            0xDC => "OemPipes",
            0xBA => "OemSemicolon",
            0xDE => "OemQuotes",
            0xC0 => "OemTilde",
            _ => null,
        };
    }

    public void Dispose()
    {
        CancelRecording();
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
}
