using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class AutomationPageView(IViewHostActions actions) : IViewPage
{
    private readonly UiScrollState _listScroll = new();
    private readonly UiScrollState _editorScroll = new();
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AutoRuleStep Step, AutoRuleAction Action)> _fileDropTargets = new(StringComparer.Ordinal);

    private List<AutoRule> _rules = new();
    private readonly Dictionary<string, string> _ruleSummaries = new(StringComparer.Ordinal);
    private Endpoint[] _outputs = [];
    private Endpoint[] _inputs = [];
    private AudioAppInfo[] _apps = [];
    private bool _loadingDevices;

    private string _searchText = "";
    private string? _selectedRuleId;
    private int _stateFilter;
    private int _triggerFilter = -1;
    private AutoRule? _editingRule;
    private List<AutoRuleStep> _editingSteps = new();
    private bool _isNewDraft;
    private string? _templateHint;
    private bool _capturingHotkey;
    private int _openWithRequest;


    // Rule drag-to-reorder state
    private readonly List<(string RuleId, UiRect RowRect)> _visibleRuleRows = new();
    private string? _dragRuleId;
    private bool _ruleDragging;
    private float _ruleDragStartY;
    private float _ruleDragPointerY;
    private string? _dropRuleId;
    private bool _dropRuleAfter;
    private UiRect _listViewport;

    private readonly UiVerticalReorder<int> _stepOrder = new();
    private UiRect _editorViewport;

    // Volume slider drag state
    private int _draggingVolStepIndex = -1;
    private UiRect _draggingVolSliderRect;
    private readonly Dictionary<int, UiRect> _stepVolSliders = new();

    private bool _observed;
    private bool _disposed;

    public bool NeedsAnimationTick => _ruleDragging || _stepOrder.NeedsScrollTick(_editorScroll, _editorViewport);

    public void TickAnimation()
    {
        if (_ruleDragging)
        {
            const float edge = 24f;
            if (_ruleDragPointerY < _listViewport.Y + edge)
            {
                _listScroll.Offset -= 8f;
                _listScroll.Clamp();
                UpdateRuleDropTarget();
            }
            else if (_ruleDragPointerY > _listViewport.Bottom - edge)
            {
                _listScroll.Offset += 8f;
                _listScroll.Clamp();
                UpdateRuleDropTarget();
            }
        }
        else
        {
            _stepOrder.TickScroll(_editorScroll, _editorViewport);
        }
    }

    public void OnActivated()
    {
        if (!_observed)
        {
            _observed = true;
            AutoRuleService.ExecutionStateChanged += OnExecutionStateChanged;
        }
        RefreshData();
    }

    public void OnDeactivated()
    {
        if (_observed)
        {
            _observed = false;
            AutoRuleService.ExecutionStateChanged -= OnExecutionStateChanged;
        }
        CancelHotkeyCapture();
        OnCaptureLost();
        actions.CloseInlineEdit(commit: true);
    }

    public void OnFocusLost()
    {
        _openWithRequest++;
        CancelHotkeyCapture();
    }

    public void OnCaptureLost()
    {
        _listScroll.EndDrag();
        _editorScroll.EndDrag();
        _draggingVolStepIndex = -1;
        if (_ruleDragging || _dragRuleId != null)
            CommitOrCancelRuleDrag(commit: false);
        if (_stepOrder.Pressed)
            CommitOrCancelStepDrag(commit: false);
    }

    private void OnExecutionStateChanged()
    {
        actions.Host.TryPost(() =>
        {
            if (!_disposed && _observed) actions.Invalidate();
        });
    }

    public void RefreshData()
    {
        LoadRules();
        if (!_loadingDevices)
        {
            _loadingDevices = true;
            _ = Task.Run(() =>
            {
                Endpoint[] outs = [];
                Endpoint[] ins = [];
                AudioAppInfo[] apps = [];
                try
                {
                    outs = actions.Host.Audio.RenderDevicesWithPhone(includePhoneCurrent: true);
                    ins = ProductAudio.Devices(Flow.Capture);
                    apps = actions.Host.Audio.Apps().Where(app => !AudioService.IsInternalApplication(app.ProcessId, app.ProcessName)).ToArray();
                }
                catch { }
                actions.Host.TryPost(() =>
                {
                    _loadingDevices = false;
                    if (_disposed) return;
                    _outputs = outs;
                    _inputs = ins;
                    _apps = apps;
                    actions.Invalidate();
                });
            });
        }
    }

    private void LoadRules()
    {
        var orderMap = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string id in actions.Config.AutomationRuleOrder ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(id) && !orderMap.ContainsKey(id))
                orderMap[id] = orderMap.Count;

        _rules = AutoRuleStore.LoadAll()
            .OrderBy(r => orderMap.TryGetValue(r.Id, out int idx) ? idx : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        _ruleSummaries.Clear();
        foreach (var rule in _rules) _ruleSummaries[rule.Id] = RuleActionSummary(rule);

        if (_editingRule != null && !_isNewDraft)
        {
            var match = _rules.FirstOrDefault(r => r.Id == _editingRule.Id);
            if (match == null)
            {
                _editingRule = null;
                _editingSteps.Clear();

            }
        }
    }

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _clicks.Clear();
        _fileDropTargets.Clear();
        _visibleRuleRows.Clear();
        _stepOrder.ClearRows();
        _stepVolSliders.Clear();

        _listViewport = _editorViewport = default;
        if (_editingRule != null)
            RenderEditorPane(g, t, hit, contentRect);
        else
            RenderLeftPane(g, t, hit, contentRect);
    }

    private void RegisterBtn(string id, UiRect r, UiRect vp, UiHitMap hit, Action onClick, UiCursorKind cursor = UiCursorKind.Hand, string? tooltip = null)
    {
        _clicks[id] = onClick;
        if (r.Bottom > vp.Y && r.Y < vp.Bottom)
        {
            var clipped = UiHitMap.Intersect(r, vp);
            if (!clipped.IsEmpty) hit.Add(id, clipped, cursor, tooltip);
        }
    }

    private void RenderLeftPane(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect card)
    {
        const float pad = 0f;
        float innerW = card.Width;
        float y = card.Y;
        const float newW = 136f, iconBtnW = 40f, btnGap = 8f;
        float toolbarW = newW + iconBtnW + btnGap;
        float titleW = MathF.Max(40f, innerW - toolbarW - 12f);
        g.DrawText(L10n.T("Nav.Automation"), new UiRect(card.X, y, titleW, 28f), t.TextPrimary, 22f, 600);
        string hint = L10n.T("Auto.Hint");
        float hintH = MathF.Max(18f, D2dSharedContext.Instance.MeasureText(hint, UiMetrics.FontFamily, 12f, 400, titleW, 100f, true).Height);
        g.DrawText(hint, new UiRect(card.X, y + 32f, titleW, hintH), t.TextSecondary, 12f, 400, wrap: true);
        var newRect = new UiRect(card.Right - toolbarW, y, newW, 40f);
        UiPrimitives.DrawAccentButton(g, t, hit, "auto.new", newRect, L10n.T("Auto.New"));
        RegisterBtn("auto.new", newRect, card, hit, NewRule);

        float bx = newRect.Right + btnGap;
        var tplRect = new UiRect(bx, y, iconBtnW, 40f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "auto.templates", tplRect, "", icon: UiIcon.Templates, tooltip: L10n.T("Template.Open"));
        RegisterBtn("auto.templates", tplRect, card, hit, () => OpenTemplatesMenu(tplRect), UiCursorKind.Hand, L10n.T("Template.Open"));
        y = card.Y + 32f + hintH + 18f;
        float stateW = MathF.Min(130f, innerW * 0.23f);
        float triggerW = MathF.Min(180f, innerW * 0.32f);
        var searchRect = new UiRect(card.X, y, innerW - stateW - triggerW - 60f, 36f);
        UiPrimitives.DrawTextBox(g, t, hit, "auto.search", searchRect, _searchText, L10n.T("Auto.Search"),
            focused: actions.ActiveInlineEditTag == "auto.search", fontSize: 12f, search: true);
        RegisterBtn("auto.search", searchRect, card, hit, () =>
        {
            actions.StartInlineEdit("auto.search", searchRect, _searchText,
                txt => { _searchText = txt; actions.Invalidate(); },
                (txt, _) => { _searchText = txt; actions.Invalidate(); });
        }, UiCursorKind.IBeam, L10n.T("Auto.Search"));

        var stateRect = new UiRect(searchRect.Right + 8f, y, stateW, 36f);
        string StateLabel(int state) => L10n.T(state switch { 1 => "Auto.Enabled", 2 => "Auto.Disabled", _ => "Auto.FilterAllStates" });
        UiPrimitives.DrawComboBox(g, t, hit, "auto.stateFilter", stateRect, StateLabel(_stateFilter));
        RegisterBtn("auto.stateFilter", stateRect, card, hit, () =>
            actions.ShowCombo(stateRect, Enumerable.Range(0, 3).Select(n => new ComboChoice(n.ToString(), StateLabel(n))).ToList(), _stateFilter.ToString(), p =>
            {
                _stateFilter = int.Parse(p.Key, CultureInfo.InvariantCulture);
                _listScroll.Offset = 0f;
                actions.Invalidate();
            }));
        var triggerRect = new UiRect(stateRect.Right + 8f, y, triggerW, 36f);
        string TriggerLabel(int n) => n < 0 ? L10n.T("Auto.FilterAllTriggers") : AutomationTriggerTitle((AutoRuleTrigger)n);
        UiPrimitives.DrawComboBox(g, t, hit, "auto.triggerFilter", triggerRect, TriggerLabel(_triggerFilter));
        RegisterBtn("auto.triggerFilter", triggerRect, card, hit, () =>
            actions.ShowCombo(triggerRect, Enumerable.Range(-1, 7).Select(n => new ComboChoice(n.ToString(), TriggerLabel(n))).ToList(), _triggerFilter.ToString(), p =>
            {
                _triggerFilter = int.Parse(p.Key, CultureInfo.InvariantCulture);
                _listScroll.Offset = 0f;
                actions.Invalidate();
            }));
        var optRect = new UiRect(triggerRect.Right + 8f, y, 36f, 36f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, "auto.listopt", optRect, UiIcon.Options, 15f, tooltip: L10n.T("Auto.ListOptions"));
        RegisterBtn("auto.listopt", optRect, card, hit, () => OpenListOptionsMenu(optRect), UiCursorKind.Hand, L10n.T("Auto.ListOptions"));
        y += 38f;

        // Rule list viewport
        _listViewport = new UiRect(card.X + pad, y, innerW, MathF.Max(40f, card.Bottom - pad - y));
        _listScroll.ViewportHeight = _listViewport.Height;

        string query = _searchText.Trim();
        var filtered = query.Length == 0 && _stateFilter == 0 && _triggerFilter < 0 ? _rules :
            _rules.Where(r => (query.Length == 0 || (r.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                && (_stateFilter == 0 || r.Enabled == (_stateFilter == 1))
                && (_triggerFilter < 0 || (int)r.Trigger == _triggerFilter)).ToList();

        float listPadR = _listScroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth + 4f : 0f;
        float rowW = MathF.Max(120f, _listViewport.Width - listPadR);
        const float rowH = 66f;
        const float rowGap = 6f;

        g.PushClip(_listViewport);
        hit.PushClip(_listViewport);
        if (filtered.Count == 0)
        {
            g.DrawText(L10n.T(_rules.Count == 0 ? "Auto.Empty" : "Auto.NoMatches"),
                new UiRect(_listViewport.X + 4f, _listViewport.Y + 12f, rowW - 8f, 44f),
                t.TextSecondary, 12f, 400, UiTextAlign.Center, UiVertAlign.Center, wrap: true);
        }
        else
        {
            float ry = _listViewport.Y - _listScroll.Offset;
            for (int i = 0; i < filtered.Count; i++)
            {
                var rule = filtered[i];
                var rowRect = new UiRect(_listViewport.X, ry, rowW, rowH);
                _visibleRuleRows.Add((rule.Id, rowRect));

                if (rowRect.Bottom >= _listViewport.Y - 2f && rowRect.Y <= _listViewport.Bottom + 2f)
                {
                    bool selected = _selectedRuleId == rule.Id;
                    bool running = AutoRuleService.IsRunning(rule.Id);
                    bool isDragged = _ruleDragging && _dragRuleId == rule.Id;

                    string rowId = "auto.rule." + rule.Id;
                    bool hot = hit.IsHover(rowId) || selected;
                    UiColor bg = selected ? t.NavSelected : hot ? t.SurfaceAlt : t.SurfaceCard;
                    UiColor border = selected || running ? t.Accent : t.Border;
                    if (isDragged) { bg = bg.WithAlphaFraction(0.45f); border = t.Accent; }

                    g.FillRoundedRect(rowRect, 8f, bg);
                    g.DrawRoundedRect(rowRect, 8f, border, selected ? 1.3f : 1f);

                    // Left Grip handle
                    var gripRect = new UiRect(rowRect.X + 4f, rowRect.Y + 8f, 18f, rowH - 16f);
                    UiPrimitives.DrawGhostIconButton(g, t, hit, "auto.grip." + rule.Id, gripRect, UiIcon.Grip, 13f, tooltip: L10n.T("Auto.DragRules"));

                    float buttonsX = rowRect.Right - 6f - 150f;
                    var swRect = new UiRect(buttonsX - 40f, rowRect.CenterY - 12f, 34f, 24f);
                    float triggerWidth = Math.Clamp(rowRect.Width * .23f, 94f, 180f);
                    var trigger = new UiRect(swRect.X - triggerWidth - 8f, rowRect.CenterY - 24f, triggerWidth, 48f);
                    var icon = new UiRect(gripRect.Right + 5f, rowRect.CenterY - 14f, 28f, 28f);
                    g.FillRoundedRect(icon, 8f, t.NavSelected);
                    var firstStep = rule.Actions.Count > 0 ? rule.Actions[0].Action : rule.Action;
                    UiIcon actionIcon = firstStep switch
                    {
                        AutoRuleAction.RunPowerShell => UiIcon.Terminal,
                        AutoRuleAction.LaunchProgram => UiIcon.Switch,
                        AutoRuleAction.ExecuteRule => UiIcon.Link,
                        AutoRuleAction.ShowOsd or AutoRuleAction.ShowQuickPanel or AutoRuleAction.HideQuickPanel => UiIcon.Monitor,
                        AutoRuleAction.ResetAllAppRouting => UiIcon.Reset,
                        AutoRuleAction.SetMicrophoneMute or AutoRuleAction.ToggleMicrophoneMute or AutoRuleAction.SetGlobalMicMute
                            or AutoRuleAction.ToggleGlobalMicMute or AutoRuleAction.SetAppInput or AutoRuleAction.SetSystemInput
                            or AutoRuleAction.SetDefaultInputVolume or AutoRuleAction.AdjustDefaultInputVolume or AutoRuleAction.SetDefaultInputMute
                            or AutoRuleAction.ToggleDefaultInputMute or AutoRuleAction.SetAppInputMute or AutoRuleAction.ToggleAppInputMute => UiIcon.Microphone,
                        _ => UiIcon.Speaker
                    };
                    g.DrawIcon(actionIcon, icon.Deflate(5f, 5f), t.Accent);
                    float infoX = icon.Right + 8f;
                    float infoW = MathF.Max(1f, trigger.X - infoX - 8f);
                    string title = string.IsNullOrWhiteSpace(rule.Name) ? L10n.T("Auto.DefaultNamePrefix") : rule.Name;
                    bool onceRan = rule.Trigger == AutoRuleTrigger.Schedule && rule.ScheduleMode == 0 && !string.IsNullOrWhiteSpace(rule.LastRunKey);
                    bool conflict = HasHotkeyConflict(rule);
                    float labelY = rowRect.CenterY - (conflict ? 26f : 18f);
                    g.DrawText(title, new UiRect(infoX, labelY, infoW - (onceRan ? 10f : 0f), 20f), t.TextPrimary, 12.5f, 600);
                    if (onceRan) g.FillEllipse(infoX + infoW - 4f, labelY + 7f, 3f, 3f, t.Accent);
                    string summary = _ruleSummaries[rule.Id];
                    g.DrawText(summary, new UiRect(infoX, labelY + 21f, infoW, 16f), t.TextSecondary, 11.5f);
                    if (conflict) g.DrawText(L10n.T("Auto.HotkeyConflict"), new UiRect(infoX, labelY + 38f, infoW, 15f), t.Fail, 10.5f);
                    g.FillRoundedRect(trigger, 10f, t.SurfaceCard);
                    g.DrawRoundedRect(trigger, 10f, t.Border);
                    string detail = AutomationTriggerDetail(rule);
                    g.DrawText(AutomationTriggerTitle(rule.Trigger), new UiRect(trigger.X + 8f, trigger.Y + (detail.Length == 0 ? 15f : 6f), trigger.Width - 16f, 18f),
                        t.TextPrimary, 11.5f);
                    if (detail.Length != 0) g.DrawText(detail, new UiRect(trigger.X + 8f, trigger.Y + 25f, trigger.Width - 16f, 17f), t.TextSecondary, 10.5f);
                    UiPrimitives.DrawSwitch(g, t, hit, "auto.sw." + rule.Id, swRect, rule.Enabled);
                    RegisterBtn("auto.sw." + rule.Id, swRect, _listViewport, hit, () => ToggleRuleEnabled(rule.Id), tooltip: L10n.T(rule.Enabled ? "Auto.Disable" : "Auto.Enable"));
                    void Button(string suffix, UiIcon glyph, string key, Action<UiRect> click, bool enabled = true)
                    {
                        string id = suffix + rule.Id;
                        var button = new UiRect(buttonsX, rowRect.CenterY - 14f, 28f, 28f);
                        string tip = L10n.T(key);
                        UiPrimitives.DrawGhostIconButton(g, t, hit, id, button, glyph, 16f, enabled: enabled, tooltip: tip);
                        if (enabled) RegisterBtn(id, button, _listViewport, hit, () => click(button), tooltip: tip);
                        buttonsX += 30f;
                    }
                    Button("auto.run.", running ? UiIcon.Waiting : UiIcon.Play, running ? "Auto.Running" : "Auto.Run", _ => RunRuleById(rule.Id), !running);
                    Button("auto.edit.", UiIcon.Edit, "Auto.Edit", _ => OpenRuleInEditor(rule, false, null));
                    Button("auto.copy.", UiIcon.Copy, "Auto.Copy", _ => DuplicateRule(rule.Id));
                    Button("auto.delete.", UiIcon.Delete, "Auto.Delete", _ => DeleteRule(rule.Id));
                    Button("auto.more.", UiIcon.More, "Auto.More", anchor => ShowRuleExtras(rule.Id, anchor));

                    // Drop indicator line
                    if (_ruleDragging && _dropRuleId == rule.Id && _dragRuleId != rule.Id)
                    {
                        float lineY = _dropRuleAfter ? rowRect.Bottom + 2f : rowRect.Y - 2f;
                        g.DrawLine(rowRect.X, lineY, rowRect.Right, lineY, t.Accent, 2.5f);
                    }
                }
                ry += rowH + rowGap;
            }
        }
        hit.PopClip();
        g.PopClip();

        _listScroll.ContentHeight = filtered.Count == 0 ? 60f : filtered.Count * (rowH + rowGap);
        _listScroll.Clamp();
        _listScroll.Draw(g, t, hit, "auto.list.scroll", _listViewport);
    }

    private void RenderEditorPane(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect card)
    {
        UiPrimitives.DrawCard(g, t, card);
        float pad = 18f;
        float innerW = card.Width - pad * 2f;

        if (_editingRule == null)
        {
            g.DrawText(L10n.T("Auto.Empty"), card.Deflate(24f, 24f), t.TextSecondary, 13.5f, 400, UiTextAlign.Center, UiVertAlign.Center, wrap: true);
            return;
        }

        // Fixed bottom Save bar (54px)
        float footerH = 54f;
        _editorViewport = new UiRect(card.X + pad, card.Y + pad, innerW, MathF.Max(60f, card.Height - pad - footerH - 8f));
        _editorScroll.ViewportHeight = _editorViewport.Height;

        float bodyW = MathF.Max(200f, _editorViewport.Width - (_editorScroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth + 8f : 0f));
        float ex = _editorViewport.X;
        float y = _editorViewport.Y - _editorScroll.Offset;
        float startY = y;

        g.PushClip(_editorViewport);
        hit.PushClip(_editorViewport);

        // Editor title
        g.DrawIcon(UiIcon.Edit, new UiRect(ex, y + 3f, 22f, 22f), t.Accent, 1.8f);
        g.DrawText(L10n.T(_isNewDraft ? "Auto.EditorNew" : "Auto.EditorEdit"),
            new UiRect(ex + 30f, y, bodyW - 30f, 28f), t.TextPrimary, 17f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        y += 30f;
        g.DrawText(L10n.T("Auto.EditorHint"), new UiRect(ex + 30f, y, bodyW - 30f, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        y += 24f;

        if (!string.IsNullOrEmpty(_templateHint))
        {
            g.DrawText(_templateHint, new UiRect(ex, y, bodyW, 34f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);
            y += 38f;
        }

        g.DrawLine(ex, y, ex + bodyW, y, t.Border, 1f, roundCaps: false);
        y += 14f;

        // Basic Info Header
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(ex, y, bodyW, 24f), UiIcon.Info, L10n.T("Auto.BasicInfo"));
        y += 32f;

        const float labelW = 84f;
        float inputX = ex + labelW;
        float inputW = MathF.Max(120f, bodyW - labelW);

        // Name row
        g.DrawText(L10n.T("Auto.Name"), new UiRect(ex, y, labelW - 8f, 34f), t.TextSecondary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        var nameRect = new UiRect(inputX, y, inputW, 34f);
        UiPrimitives.DrawTextBox(g, t, hit, "edit.name", nameRect, _editingRule.Name, L10n.T("Auto.DefaultNameHint"),
            focused: actions.ActiveInlineEditTag == "edit.name");
        RegisterBtn("edit.name", nameRect, _editorViewport, hit, () =>
        {
            actions.StartInlineEdit("edit.name", nameRect, _editingRule.Name,
                null,
                (txt, _) => { _editingRule.Name = txt.Trim(); actions.Invalidate(); });
        }, UiCursorKind.IBeam);
        y += 42f;

        // Trigger row
        g.DrawText(L10n.T("Auto.Trigger"), new UiRect(ex, y, labelW - 8f, 34f), t.TextSecondary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        var trigRect = new UiRect(inputX, y, inputW, 34f);
        UiPrimitives.DrawComboBox(g, t, hit, "edit.trigger", trigRect, AutomationTriggerTitle(_editingRule.Trigger));
        RegisterBtn("edit.trigger", trigRect, _editorViewport, hit, () => OpenTriggerCombo(trigRect));
        y += 42f;

        // Dynamic Trigger fields
        if (_editingRule.Trigger == AutoRuleTrigger.Hotkey)
        {
            g.DrawText(L10n.T("Auto.Hotkey"), new UiRect(ex, y, labelW - 8f, 34f), t.TextSecondary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var hkRect = new UiRect(inputX, y, inputW, 34f);
            string hkLabel = _capturingHotkey
                ? L10n.T("Auto.HotkeyCapturing")
                : string.IsNullOrWhiteSpace(_editingRule.Hotkey) ? L10n.T("Auto.HotkeyUnbound") : _editingRule.Hotkey;
            HotkeyBinding.Draw(g, t, hit, "edit.hk.rec", hkRect, hkLabel);
            RegisterBtn("edit.hk.rec", hkRect, _editorViewport, hit, ToggleHotkeyCapture);

            y += 38f;
            g.DrawText(L10n.T(_capturingHotkey ? "Auto.HotkeyCapturing" : "Auto.HotkeyHint"), new UiRect(inputX, y, inputW, 18f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            y += 22f;
            if (HasHotkeyConflict(_editingRule))
            {
                g.DrawText(L10n.T("Auto.HotkeyConflict"), new UiRect(inputX, y, inputW, 18f), t.Fail, 11f, 600, UiTextAlign.Leading, UiVertAlign.Center);
                y += 20f;
            }
            else
            {
                y += 2f;
            }
        }
        else if (_editingRule.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit)
        {
            g.DrawText(L10n.T("Auto.TriggerApp"), new UiRect(ex, y, labelW - 8f, 34f), t.TextSecondary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var pickRect = new UiRect(inputX, y, inputW, 34f);
            DrawAppPicker(g, t, hit, "edit.trigapp.pick", pickRect, _editingRule.TriggerApp);
            RegisterBtn("edit.trigapp.pick", pickRect, _editorViewport, hit, () =>
                OpenAppPickerCombo(pickRect, _editingRule.TriggerApp, proc => { _editingRule.TriggerApp = proc; actions.Invalidate(); },
                    includeWindowApps: _editingRule.Trigger == AutoRuleTrigger.AppSwitch));
            y += 42f;
        }
        else if (_editingRule.Trigger == AutoRuleTrigger.Schedule)
        {
            g.DrawText(L10n.T("Auto.ScheduleMode"), new UiRect(ex, y, labelW - 8f, 34f), t.TextSecondary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var modeRect = new UiRect(inputX, y, 130f, 34f);
            string modeLabel = L10n.T(_editingRule.ScheduleMode switch { 1 => "Auto.ScheduleDaily", 2 => "Auto.ScheduleWeekly", _ => "Auto.ScheduleOnce" });
            UiPrimitives.DrawComboBox(g, t, hit, "edit.sched.mode", modeRect, modeLabel);
            RegisterBtn("edit.sched.mode", modeRect, _editorViewport, hit, () => OpenScheduleModeCombo(modeRect));

            ParseScheduleTime(_editingRule.ScheduleTime, out int hh, out int mm);
            var hourRect = new UiRect(modeRect.Right + 10f, y, 64f, 34f);
            UiPrimitives.DrawComboBox(g, t, hit, "edit.sched.hr", hourRect, hh.ToString("00", CultureInfo.InvariantCulture));
            RegisterBtn("edit.sched.hr", hourRect, _editorViewport, hit, () => OpenHourCombo(hourRect, hh, mm));

            g.DrawText(":", new UiRect(hourRect.Right + 2f, y, 10f, 34f), t.TextPrimary, 13f, 600, UiTextAlign.Center, UiVertAlign.Center);

            var minRect = new UiRect(hourRect.Right + 14f, y, 64f, 34f);
            UiPrimitives.DrawComboBox(g, t, hit, "edit.sched.min", minRect, mm.ToString("00", CultureInfo.InvariantCulture));
            RegisterBtn("edit.sched.min", minRect, _editorViewport, hit, () => OpenMinuteCombo(minRect, hh, mm));
            y += 42f;

            if (_editingRule.ScheduleMode == 2)
            {
                g.DrawText(L10n.T("Auto.ScheduleWeekday"), new UiRect(ex, y, labelW - 8f, 28f), t.TextSecondary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                float pillW = MathF.Min(48f, (inputW - 6f * 6f) / 7f);
                for (int wd = 0; wd < 7; wd++)
                {
                    int day = wd;
                    bool active = _editingRule.ScheduleWeekdays.Contains(day);
                    var pillRect = new UiRect(inputX + wd * (pillW + 5f), y, pillW, 28f);
                    UiPrimitives.DrawSecondaryButton(g, t, hit, "edit.sched.wd." + day, pillRect, L10n.T("Auto.Wd" + day), active: active, fontSize: 11f);
                    RegisterBtn("edit.sched.wd." + day, pillRect, _editorViewport, hit, () =>
                    {
                        if (_editingRule.ScheduleWeekdays.Contains(day)) _editingRule.ScheduleWeekdays.Remove(day);
                        else _editingRule.ScheduleWeekdays.Add(day);
                        actions.Invalidate();
                    });
                }
                y += 36f;
            }
        }

        y += 8f;
        g.DrawLine(ex, y, ex + bodyW, y, t.Border, 1f, roundCaps: false);
        y += 14f;

        // Steps Section Header
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(ex, y, 160f, 24f), UiIcon.Play, L10n.T("Auto.Action"));
        g.DrawText(L10n.T("Auto.ActionsHint"), new UiRect(ex + 130f, y, bodyW - 130f, 24f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        y += 32f;

        for (int i = 0; i < _editingSteps.Count; i++)
        {
            y = RenderStepCard(g, t, hit, ex, y, bodyW, i, _editingSteps[i]);
            y += 10f;
        }

        // Add step button
        var addStepRect = new UiRect(ex, y, 150f, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "edit.step.add", addStepRect, L10n.T("Auto.AddAction"), fontSize: 12f);
        RegisterBtn("edit.step.add", addStepRect, _editorViewport, hit, () =>
        {
            _editingSteps.Add(new AutoRuleStep());
            actions.Invalidate();
        });
        y += 46f;

        hit.PopClip();
        g.PopClip();

        _editorScroll.ContentHeight = MathF.Max(0f, y - startY);
        _editorScroll.Clamp();
        if (_stepOrder.Retarget(_editorScroll, _editorViewport)) actions.Invalidate();
        _editorScroll.Draw(g, t, hit, "edit.scroll", _editorViewport);

        // Footer Save bar
        float fy = card.Bottom - footerH;
        g.DrawLine(card.X + pad, fy, card.Right - pad, fy, t.Border, 1f, roundCaps: false);
        float btnY = fy + 10f;
        float rightX = card.Right - pad;

        var saveRect = new UiRect(rightX - 108f, btnY, 108f, 34f);
        UiPrimitives.DrawAccentButton(g, t, hit, "edit.save", saveRect, L10n.T("Auto.Save"));
        RegisterBtn("edit.save", saveRect, card, hit, SaveEditorRule);

        var cancelRect = new UiRect(saveRect.X - 90f, btnY, 80f, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "edit.cancel", cancelRect, L10n.T("Auto.Cancel"));
        RegisterBtn("edit.cancel", cancelRect, card, hit, CancelEditor);
    }

    private float RenderStepCard(D2dTarget g, UiThemePalette t, UiHitMap hit, float x, float y, float w, int index, AutoRuleStep step)
    {
        float pad = 12f;
        float innerW = w - pad * 2f;
        float paramsH = MeasureStepParamsHeight(step);
        float cardH = pad + 34f + 10f + paramsH + pad;
        var cardRect = new UiRect(x, y, w, cardH);
        _stepOrder.AddRow(index, cardRect, _editorScroll.Offset);

        bool isDragged = _stepOrder.IsSource(index);
        (UiColor bg, UiColor border, UiColor fg) = StepPalette(t.IsDark, step.Action);
        if (isDragged) bg = bg.WithAlphaFraction(0.55f);

        g.FillRoundedRect(cardRect, 10f, bg);
        g.DrawRoundedRect(cardRect, 10f, isDragged ? t.Accent : border, isDragged ? 1.8f : 1f);

        _stepOrder.DrawInsertionLine(g, t, index, cardRect, gap: 4f);

        // Top bar: Grip + Step index badge + Action Combo + Copy + Delete
        float ty = y + pad;
        var gripRect = new UiRect(x + pad, ty + 2f, 22f, 30f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.grip.{index}", gripRect, UiIcon.Grip, 14f, tooltip: L10n.T("Auto.DragOrder"));

        var idxBadgeRect = new UiRect(gripRect.Right + 4f, ty + 6f, 44f, 22f);
        UiPrimitives.DrawBadge(g, t, idxBadgeRect, $"#{index + 1}", t.SurfaceBg, fg, border, 11f);

        float rightBtnsW = 108f;
        float comboW = Math.Clamp(innerW - 76f - rightBtnsW, 130f, 300f);
        var actionComboRect = new UiRect(idxBadgeRect.Right + 8f, ty, comboW, 34f);
        UiPrimitives.DrawComboBox(g, t, hit, $"step.act.{index}", actionComboRect, L10n.T(AutoActionKey(step.Action)), fontSize: 12f);
        RegisterBtn($"step.act.{index}", actionComboRect, _editorViewport, hit, () => OpenActionCombo(actionComboRect, step));

        // Right step buttons: Delete | Copy
        float rx = x + w - pad;
        var delRect = new UiRect(rx - 68f, ty, 68f, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, $"step.del.{index}", delRect, L10n.T("Auto.Delete"),
            icon: UiIcon.Delete, danger: true, fontSize: 12f);
        RegisterBtn($"step.del.{index}", delRect, _editorViewport, hit, () => { _editingSteps.RemoveAt(index); actions.Invalidate(); });

        rx = delRect.X - 2f;
        var copyRect = new UiRect(rx - 28f, ty + 2f, 28f, 30f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.cp.{index}", copyRect, UiIcon.Copy, 14f, tooltip: L10n.T("Auto.Copy"));
        RegisterBtn($"step.cp.{index}", copyRect, _editorViewport, hit, () => { _editingSteps.Insert(index + 1, step.Clone()); actions.Invalidate(); });

        // Inner params box
        var paramBox = new UiRect(x + pad, ty + 42f, innerW, paramsH);
        g.FillRoundedRect(paramBox, 8f, t.SurfaceBgAlpha);
        RenderStepParams(g, t, hit, paramBox.Deflate(10f, 8f), index, step);

        return y + cardH;
    }

    private float MeasureStepParamsHeight(AutoRuleStep step)
    {
        float h = 16f;
        if (AutoRuleActions.NeedsApp(step.Action)) h += 40f;
        if (AutoRuleActions.NeedsDevice(step.Action)) h += 40f;
        if (AutoRuleActions.SetsVolume(step.Action) || AutoRuleActions.AdjustsVolume(step.Action)) h += 36f;
        if (AutoRuleActions.SetsMute(step.Action)) h += 40f;
        if (step.Action == AutoRuleAction.ShowOsd) h += 40f;
        if (step.Action == AutoRuleAction.ExecuteRule) h += 40f;
        if (step.Action == AutoRuleAction.LaunchProgram)
        {
            var items = step.EffectiveLaunchItems();
            h += 40f + items.Count * 36f + 36f;
        }
        else if (step.Action == AutoRuleAction.RunPowerShell)
        {
            h += step.ProgramPaths.Count * 36f + 36f;
        }
        h += 38f; // Delay row
        h += 24f; // StopOnFailure checkbox
        return h;
    }

    private void RenderStepParams(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect area, int index, AutoRuleStep step)
    {
        float x = area.X;
        float y = area.Y;
        float w = area.Width;
        const float lw = 84f;
        float ix = x + lw;
        float iw = MathF.Max(100f, w - lw);

        if (AutoRuleActions.NeedsApp(step.Action))
        {
            g.DrawText(L10n.T("Auto.TargetApp"), new UiRect(x, y, lw - 6f, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var pickRect = new UiRect(ix, y, iw, 32f);
            DrawAppPicker(g, t, hit, $"step.{index}.apppick", pickRect, step.TargetApp, fontSize: 12f);
            RegisterBtn($"step.{index}.apppick", pickRect, _editorViewport, hit, () =>
                OpenAppPickerCombo(pickRect, step.TargetApp, proc => { step.TargetApp = proc; actions.Invalidate(); }));
            y += 40f;
        }

        if (AutoRuleActions.NeedsDevice(step.Action))
        {
            g.DrawText(L10n.T("Auto.TargetDevice"), new UiRect(x, y, lw - 6f, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var devRect = new UiRect(ix, y, iw, 32f);
            string devLabel = ResolveDeviceLabel(step);
            UiPrimitives.DrawComboBox(g, t, hit, $"step.{index}.dev", devRect, devLabel, fontSize: 12f);
            RegisterBtn($"step.{index}.dev", devRect, _editorViewport, hit, () => OpenStepDeviceCombo(devRect, step));
            y += 40f;
        }

        bool isRel = AutoRuleActions.AdjustsVolume(step.Action);
        if (isRel || AutoRuleActions.SetsVolume(step.Action))
        {
            g.DrawText(L10n.T("Auto.Volume"), new UiRect(x, y, lw - 6f, 28f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            int val = isRel ? Math.Clamp(step.VolumeDelta, -100, 100) : Math.Clamp(step.Volume, 0, 100);
            float val01 = isRel ? (val + 100) / 200f : val / 100f;
            var slRect = new UiRect(ix, y + 2f, MathF.Max(80f, iw - 60f), 24f);
            _stepVolSliders[index] = slRect;
            UiPrimitives.DrawVolumeSlider(g, t, hit, $"step.{index}.vol", slRect, val01, 0f, false, false);
            if (slRect.Bottom > _editorViewport.Y && slRect.Y < _editorViewport.Bottom)
            {
                var clipped = UiHitMap.Intersect(slRect, _editorViewport);
                if (!clipped.IsEmpty) hit.Add($"step.{index}.vol", clipped, UiCursorKind.Hand);
            }

            string valStr = isRel ? $"{(val > 0 ? "+" : "")}{val}%" : $"{val}%";
            g.DrawText(valStr, new UiRect(slRect.Right + 8f, y, 52f, 28f), t.TextPrimary, 12.5f, 600, UiTextAlign.Trailing, UiVertAlign.Center);
            y += 36f;
        }

        if (AutoRuleActions.SetsMute(step.Action))
        {
            g.DrawText(L10n.T("Auto.MuteState"), new UiRect(x, y, lw - 6f, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var muteRect = new UiRect(ix, y, MathF.Min(180f, iw), 32f);
            UiPrimitives.DrawComboBox(g, t, hit, $"step.{index}.mute", muteRect, L10n.T(step.Muted ? "Auto.MuteOn" : "Auto.MuteOff"), fontSize: 12f);
            RegisterBtn($"step.{index}.mute", muteRect, _editorViewport, hit, () =>
            {
                var choices = new[] { new ComboChoice("1", L10n.T("Auto.MuteOn")), new ComboChoice("0", L10n.T("Auto.MuteOff")) };
                actions.ShowCombo(muteRect, choices, step.Muted ? "1" : "0", p => { step.Muted = p.Key == "1"; actions.Invalidate(); });
            });
            y += 40f;
        }

        if (step.Action == AutoRuleAction.ShowOsd)
        {
            float halfW = (w - 140f) * 0.5f;
            g.DrawText(L10n.T("Auto.OsdTitle"), new UiRect(x, y, 58f, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var tRect = new UiRect(x + 62f, y, MathF.Max(60f, halfW), 32f);
            string tTag = $"step.{index}.osdt";
            UiPrimitives.DrawTextBox(g, t, hit, tTag, tRect, step.OsdTitle, L10n.T("Auto.OsdDefaultTitle"), focused: actions.ActiveInlineEditTag == tTag, fontSize: 12f);
            RegisterBtn(tTag, tRect, _editorViewport, hit, () =>
                actions.StartInlineEdit(tTag, tRect, step.OsdTitle, null, (s, _) => { step.OsdTitle = s; actions.Invalidate(); }), UiCursorKind.IBeam);

            g.DrawText(L10n.T("Auto.OsdText"), new UiRect(tRect.Right + 10f, y, 58f, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var xRect = new UiRect(tRect.Right + 72f, y, MathF.Max(60f, w - (tRect.Right - x + 72f)), 32f);
            string xTag = $"step.{index}.osdx";
            UiPrimitives.DrawTextBox(g, t, hit, xTag, xRect, step.OsdText, "", focused: actions.ActiveInlineEditTag == xTag, fontSize: 12f);
            RegisterBtn(xTag, xRect, _editorViewport, hit, () =>
                actions.StartInlineEdit(xTag, xRect, step.OsdText, null, (s, _) => { step.OsdText = s; actions.Invalidate(); }), UiCursorKind.IBeam);
            y += 40f;
        }

        if (step.Action == AutoRuleAction.ExecuteRule)
        {
            g.DrawText(L10n.T("Auto.TargetRule"), new UiRect(x, y, lw - 6f, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var trRect = new UiRect(ix, y, iw, 32f);
            var target = _rules.FirstOrDefault(r => r.Id == step.TargetRuleId);
            UiPrimitives.DrawComboBox(g, t, hit, $"step.{index}.trule", trRect, target?.Name ?? L10n.T("Auto.MissingRule"), fontSize: 12f);
            RegisterBtn($"step.{index}.trule", trRect, _editorViewport, hit, () =>
            {
                var choices = _rules.Where(r => r.Id != _editingRule?.Id).Select(r => new ComboChoice(r.Id, r.Name)).ToList();
                actions.ShowCombo(trRect, choices, step.TargetRuleId, p => { step.TargetRuleId = p.Key; actions.Invalidate(); });
            });
            y += 40f;
        }

        if (step.Action == AutoRuleAction.LaunchProgram)
        {
            if (step.LaunchItems.Count == 0 && step.ProgramPaths.Count > 0)
                step.LaunchItems = step.EffectiveLaunchItems();

            g.DrawText(L10n.T("Auto.LaunchMode"), new UiRect(x, y, lw - 6f, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var lmRect = new UiRect(ix, y, MathF.Min(200f, iw), 32f);
            UiPrimitives.DrawComboBox(g, t, hit, $"step.{index}.lmode", lmRect, L10n.T(step.LaunchMode == 1 ? "Auto.LaunchModeRandom" : "Auto.LaunchModeAll"), fontSize: 12f);
            RegisterBtn($"step.{index}.lmode", lmRect, _editorViewport, hit, () =>
            {
                var choices = new[] { new ComboChoice("0", L10n.T("Auto.LaunchModeAll")), new ComboChoice("1", L10n.T("Auto.LaunchModeRandom")) };
                actions.ShowCombo(lmRect, choices, step.LaunchMode == 1 ? "1" : "0", p => { step.LaunchMode = p.Key == "1" ? 1 : 0; actions.Invalidate(); });
            });
            y += 40f;

            for (int li = 0; li < step.LaunchItems.Count; li++)
            {
                int itemIdx = li;
                var item = step.LaunchItems[li];
                float choiceW = MathF.Min(132f, iw * 0.35f);
                var pathRect = new UiRect(ix, y, MathF.Max(36f, iw - choiceW - 70f), 30f);
                string pTag = $"step.{index}.li.{li}";
                _fileDropTargets[pTag] = (step, step.Action);
                UiPrimitives.DrawTextBox(g, t, hit, pTag, pathRect, item.Path, "C:\\...", focused: actions.ActiveInlineEditTag == pTag, fontSize: 11.5f);
                RegisterBtn(pTag, pathRect, _editorViewport, hit, () =>
                    actions.StartInlineEdit(pTag, pathRect, item.Path, null, (s, _) => { item.Path = s.Trim(); actions.Invalidate(); }, acceptFileDrop: true), UiCursorKind.IBeam);

                var brRect = new UiRect(pathRect.Right + 4f, y, 28f, 30f);
                UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.{index}.libr.{li}", brRect, UiIcon.File, 13f);
                RegisterBtn($"step.{index}.libr.{li}", brRect, _editorViewport, hit, () =>
                {
                    if (NativeDialogs.Open(actions.OwnerWindow.Handle, L10n.T("Auto.FileFilterAll") + " (*.*)|*.*|" + L10n.T("Auto.FileFilterExe") + " (*.exe)|*.exe") is { } f)
                    {
                        item.Path = f;
                        actions.Invalidate();
                    }
                });

                string openId = $"step.{index}.openwith.{li}";
                var openRect = new UiRect(brRect.Right + 4f, y, choiceW, 30f);
                string label = item.OpenWith.Length == 0 ? L10n.T("Auto.OpenWithDefault")
                    : string.IsNullOrWhiteSpace(item.OpenWithName) ? Path.GetFileNameWithoutExtension(item.OpenWith) : item.OpenWithName;
                UiPrimitives.DrawComboBox(g, t, hit, openId, openRect, label, fontSize: 12f,
                    appIcon: item.OpenWith.Length == 0 ? null : ComboAppIcon.ForExecutable(item.OpenWith),
                    onAppIconLoaded: OnAppPickerIconLoaded);
                RegisterBtn(openId, openRect, _editorViewport, hit, () => OpenWithMenu(item, openRect),
                    tooltip: string.IsNullOrEmpty(item.OpenWith) ? L10n.T("Auto.OpenWith") : item.OpenWith);
                var rmRect = new UiRect(openRect.Right + 2f, y, 28f, 30f);
                UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.{index}.lirm.{li}", rmRect, UiIcon.Close, 13f, iconColor: t.Fail);
                RegisterBtn($"step.{index}.lirm.{li}", rmRect, _editorViewport, hit, () => { step.LaunchItems.RemoveAt(itemIdx); actions.Invalidate(); });
                y += 36f;
            }

            var addFileRect = new UiRect(ix, y, 148f, 28f);
            string addTag = $"step.{index}.liadd";
            _fileDropTargets[addTag] = (step, step.Action);
            UiPrimitives.DrawSecondaryButton(g, t, hit, addTag, addFileRect, L10n.T("Auto.AddFile"), fontSize: 11.5f);
            RegisterBtn(addTag, addFileRect, _editorViewport, hit, () =>
            {
                var files = NativeDialogs.OpenMultiple(actions.OwnerWindow.Handle,
                    L10n.T("Auto.FileFilterAll") + " (*.*)|*.*|" + L10n.T("Auto.FileFilterExe") + " (*.exe)|*.exe", L10n.T("Auto.AddFileTitle"));
                AppendStepFiles(step, files);
                actions.Invalidate();
            });
            y += 36f;
        }
        else if (step.Action == AutoRuleAction.RunPowerShell)
        {
            g.DrawText(L10n.T("Auto.Script"), new UiRect(x, y, lw - 6f, 30f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            for (int pi = 0; pi < step.ProgramPaths.Count; pi++)
            {
                int pathIdx = pi;
                var pRect = new UiRect(ix, y, MathF.Max(80f, iw - 34f), 30f);
                string psTag = $"step.{index}.ps.{pi}";
                _fileDropTargets[psTag] = (step, step.Action);
                UiPrimitives.DrawTextBox(g, t, hit, psTag, pRect, step.ProgramPaths[pi], "script.ps1 / command", focused: actions.ActiveInlineEditTag == psTag, mono: true, fontSize: 11.5f);
                RegisterBtn(psTag, pRect, _editorViewport, hit, () =>
                    actions.StartInlineEdit(psTag, pRect, step.ProgramPaths[pathIdx], null, (s, _) => { step.ProgramPaths[pathIdx] = s; actions.Invalidate(); }, mono: true, acceptFileDrop: true), UiCursorKind.IBeam);

                var rmRect = new UiRect(pRect.Right + 4f, y, 28f, 30f);
                UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.{index}.psrm.{pi}", rmRect, UiIcon.Close, 13f, iconColor: t.Fail);
                RegisterBtn($"step.{index}.psrm.{pi}", rmRect, _editorViewport, hit, () => { step.ProgramPaths.RemoveAt(pathIdx); actions.Invalidate(); });
                y += 36f;
            }
            var addPathRect = new UiRect(ix, y, 130f, 28f);
            string addTag = $"step.{index}.psadd";
            _fileDropTargets[addTag] = (step, step.Action);
            UiPrimitives.DrawSecondaryButton(g, t, hit, addTag, addPathRect, L10n.T("Auto.AddPath"), fontSize: 11.5f);
            RegisterBtn(addTag, addPathRect, _editorViewport, hit, () => { step.ProgramPaths.Add(""); actions.Invalidate(); });
            y += 36f;
        }

        // Delay row
        g.DrawText(L10n.T("Auto.Delay"), new UiRect(x, y, lw - 6f, 30f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        var delayRect = new UiRect(ix, y, 80f, 30f);
        string delayTag = $"step.{index}.delay";
        UiPrimitives.DrawTextBox(g, t, hit, delayTag, delayRect, step.DelayMs.ToString(CultureInfo.InvariantCulture), "0", focused: actions.ActiveInlineEditTag == delayTag, fontSize: 12f);
        RegisterBtn(delayTag, delayRect, _editorViewport, hit, () =>
        {
            void UpdateDelay(string text)
            {
                text = text.Trim();
                if (text.Length == 0) return;
                bool valid = int.TryParse(text, out int value);
                step.DelayMs = Math.Clamp(valid ? value : step.DelayMs, 0, 60000);
                if (!valid || value != step.DelayMs || text.Length > 5)
                    actions.SetInlineEditText(delayTag, step.DelayMs.ToString(CultureInfo.InvariantCulture));
            }
            actions.StartInlineEdit(delayTag, delayRect, step.DelayMs.ToString(CultureInfo.InvariantCulture),
                UpdateDelay, (text, _) => UpdateDelay(text));
        }, UiCursorKind.IBeam);
        g.DrawText(L10n.T("Auto.DelayUnit") + " (" + L10n.T("Auto.DelayRange") + ")",
            new UiRect(delayRect.Right + 8f, y, MathF.Max(40f, iw - 88f), 30f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        y += 38f;

        // Stop on failure checkbox
        var stopRect = new UiRect(x, y, w, 22f);
        UiPrimitives.DrawCheckBox(g, t, hit, $"step.{index}.stop", stopRect, step.StopOnFailure, L10n.T("Auto.StopOnFailure"), 11.5f);
        RegisterBtn($"step.{index}.stop", stopRect, _editorViewport, hit, () => { step.StopOnFailure = !step.StopOnFailure; actions.Invalidate(); });
    }

    public void OnFilesDropped(string[] paths, string? targetId)
    {
        if (_editingRule == null || targetId == null || !_fileDropTargets.TryGetValue(targetId, out var target)
            || target.Step.Action != target.Action || !_editingSteps.Contains(target.Step)) return;
        actions.CloseInlineEdit(commit: true);
        AppendStepFiles(target.Step, paths);
    }

    private static void AppendStepFiles(AutoRuleStep step, string[] paths)
    {
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (step.Action == AutoRuleAction.LaunchProgram)
            {
                if (!step.LaunchItems.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
                    step.LaunchItems.Add(new AutoLaunchItem { Path = path });
            }
            else if (step.Action == AutoRuleAction.RunPowerShell && !step.ProgramPaths.Contains(path))
                step.ProgramPaths.Add(path);
        }
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (_listScroll.DragMove(y) || _editorScroll.DragMove(y))
            return true;

        if (_draggingVolStepIndex >= 0)
        {
            if (!leftDown) { _draggingVolStepIndex = -1; return true; }
            if ((uint)_draggingVolStepIndex < (uint)_editingSteps.Count)
            {
                UpdateStepVolumeFromX(_editingSteps[_draggingVolStepIndex], _draggingVolSliderRect, x);
                return true;
            }
        }

        if (_dragRuleId != null)
        {
            if (!leftDown) { CommitOrCancelRuleDrag(commit: _ruleDragging); return true; }
            _ruleDragPointerY = y;
            if (!_ruleDragging && MathF.Abs(y - _ruleDragStartY) >= 6f)
                _ruleDragging = true;
            if (_ruleDragging) UpdateRuleDropTarget();
            return true;
        }

        if (_stepOrder.Pressed)
        {
            if (leftDown) return _stepOrder.Move(y, _editorScroll, _editorViewport);
            CommitOrCancelStepDrag(commit: false);
            return true;
        }

        return false;
    }

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        _openWithRequest++;
        if (_capturingHotkey && _editingRule != null)
        {
            if (HotkeyBinding.MouseButton(e.Button) is string button)
            {
                _editingRule.Hotkey = HotkeyBinding.Format(button, e.Ctrl, e.Alt, e.Shift);
                CancelHotkeyCapture();
                return true;
            }
            if (targetId != "edit.hk.rec") CancelHotkeyCapture();
        }
        if (e.Button != UiMouseButton.Left) return false;

        if (targetId == "auto.list.scroll" && _listScroll.BeginDrag(e.Y)) return true;
        if (targetId == "edit.scroll" && _editorScroll.BeginDrag(e.Y)) return true;

        // Rule grip drag start
        if (_listViewport.Contains(e.X, e.Y))
        {
            foreach (var row in _visibleRuleRows)
            {
                if (targetId == "auto.grip." + row.RuleId)
                {
                    _dragRuleId = row.RuleId;
                    _ruleDragStartY = _ruleDragPointerY = e.Y;
                    _ruleDragging = false;
                    return true;
                }
            }
        }

        // Step grip or volume slider drag start
        if (_editorViewport.Contains(e.X, e.Y))
        {
            const string gripPrefix = "step.grip.";
            if (targetId?.StartsWith(gripPrefix, StringComparison.Ordinal) == true &&
                int.TryParse(targetId.AsSpan(gripPrefix.Length), out int index) && (uint)index < (uint)_editingSteps.Count)
            {
                _stepOrder.Begin(index, e.Y);
                return true;
            }
            foreach (var kv in _stepVolSliders)
            {
                if (targetId == $"step.{kv.Key}.vol" && (uint)kv.Key < (uint)_editingSteps.Count)
                {
                    _draggingVolStepIndex = kv.Key;
                    _draggingVolSliderRect = kv.Value;
                    UpdateStepVolumeFromX(_editingSteps[kv.Key], kv.Value, e.X);
                    return true;
                }
            }
        }

        return false;
    }

    public bool OnPointerUp(UiPointerEventArgs e, string? clickId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (_listScroll.Dragging) { _listScroll.EndDrag(); return true; }
        if (_editorScroll.Dragging) { _editorScroll.EndDrag(); return true; }

        if (_draggingVolStepIndex >= 0)
        {
            _draggingVolStepIndex = -1;
            return true;
        }

        if (_dragRuleId != null)
        {
            CommitOrCancelRuleDrag(commit: _ruleDragging);
            return true;
        }

        if (_stepOrder.Pressed)
        {
            if (_stepOrder.Dragging) _stepOrder.Move(e.Y, _editorScroll, _editorViewport);
            CommitOrCancelStepDrag(commit: true);
            return true;
        }

        if (clickId == null || !_clicks.TryGetValue(clickId, out var click)) return false;
        click();
        return true;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        _openWithRequest++;
        if (horizontal) return false;
        if (_capturingHotkey && _editingRule != null && delta != 0)
        {
            _editingRule.Hotkey = HotkeyBinding.Wheel(delta);
            CancelHotkeyCapture();
            return true;
        }

        if (_listViewport.Contains(x, y)) { _listScroll.OnWheel(delta); return true; }
        if (_editorViewport.Contains(x, y))
        {
            foreach (var kv in _stepVolSliders)
            {
                if (kv.Value.Contains(x, y) && (uint)kv.Key < (uint)_editingSteps.Count)
                {
                    var st = _editingSteps[kv.Key];
                    int d = delta > 0 ? 5 : -5;
                    if (AutoRuleActions.AdjustsVolume(st.Action))
                        st.VolumeDelta = Math.Clamp(st.VolumeDelta + d, -100, 100);
                    else
                        st.Volume = Math.Clamp(st.Volume + d, 0, 100);
                    return true;
                }
            }
            _editorScroll.OnWheel(delta);
            return true;
        }
        return false;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        _openWithRequest++;
        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            if (_ruleDragging || _dragRuleId != null)
            {
                CommitOrCancelRuleDrag(commit: false);
                return true;
            }
            if (_stepOrder.Pressed)
            {
                CommitOrCancelStepDrag(commit: false);
                return true;
            }
            if (_capturingHotkey && _editingRule != null)
            {
                _editingRule.Hotkey = "";
                CancelHotkeyCapture();
                actions.Invalidate();
                return true;
            }
        }

        if (_capturingHotkey && _editingRule != null)
        {
            if (HotkeyBinding.Keyboard(vk, ctrl, alt, shift) is string combo)
            {
                _editingRule.Hotkey = combo;
                CancelHotkeyCapture();
            }
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        OnDeactivated();
    }

    private void UpdateRuleDropTarget()
    {
        _dropRuleId = null;
        _dropRuleAfter = false;
        foreach (var row in _visibleRuleRows)
        {
            _dropRuleId = row.RuleId;
            _dropRuleAfter = _ruleDragPointerY >= row.RowRect.CenterY;
            if (_ruleDragPointerY < row.RowRect.Bottom) break;
        }
    }

    private void CommitOrCancelRuleDrag(bool commit)
    {
        if (commit && _dropRuleId != null && _dragRuleId != null && _dropRuleId != _dragRuleId)
        {
            var ids = _rules.Select(rule => rule.Id).ToList();
            bool removed = ids.Remove(_dragRuleId);
            int index = ids.IndexOf(_dropRuleId);
            if (removed && index >= 0)
            {
                ids.Insert(index + (_dropRuleAfter ? 1 : 0), _dragRuleId);
                actions.Config.AutomationRuleOrder = ids;
                if (actions.CommitSettings()) actions.Host.ViewSettingsChanged();
                LoadRules();
            }
        }
        _dragRuleId = _dropRuleId = null;
        _ruleDragging = false;
        actions.Invalidate();
    }

    private void CommitOrCancelStepDrag(bool commit)
    {
        if (_stepOrder.End(commit) is { } drop &&
            (uint)drop.Source < (uint)_editingSteps.Count && (uint)drop.Anchor < (uint)_editingSteps.Count)
        {
            var step = _editingSteps[drop.Source];
            _editingSteps.RemoveAt(drop.Source);
            int target = drop.Anchor + (drop.After ? 1 : 0) - (drop.Source < drop.Anchor ? 1 : 0);
            _editingSteps.Insert(target, step);
        }
        actions.Invalidate();
    }

    private void ToggleHotkeyCapture()
    {
        if (_capturingHotkey) CancelHotkeyCapture();
        else
        {
            _capturingHotkey = true;
            actions.Host.HoldHotkeys(true);
        }
        actions.Invalidate();
    }

    private void CancelHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        actions.Host.HoldHotkeys(false);
        actions.Invalidate();
    }

    private void OpenRuleInEditor(AutoRule rule, bool isNewDraft, string? templateHint)
    {
        CancelHotkeyCapture();
        _selectedRuleId = rule.Id;
        _editingRule = rule.Clone();
        _editingRule.TriggerApp ??= string.Empty;
        _editingSteps = _editingRule.CloneSteps();
        if (_editingSteps.Count == 0) _editingSteps.Add(new AutoRuleStep());
        _isNewDraft = isNewDraft;
        _templateHint = templateHint;
        _editorScroll.Offset = 0f;
        actions.Invalidate();
    }

    private void NewRule()
    {
        var draft = new AutoRule { Name = "", Enabled = true, Trigger = AutoRuleTrigger.Hotkey };
        draft.SetSteps([new AutoRuleStep()]);
        OpenRuleInEditor(draft, isNewDraft: true, templateHint: null);
    }

    private void OpenTemplatesMenu(UiRect anchor)
    {
        var items = AutoRuleTemplates.All.Select(tpl => new UiMenuItem
        {
            Text = L10n.T(tpl.NameKey),
            Icon = UiIcon.Templates,
            Click = () =>
            {
                string baseName = L10n.T(tpl.NameKey), name = baseName;
                for (int suffix = 2; _rules.Any(rule => string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
                    name = $"{baseName} ({suffix})";
                var draft = tpl.CreateDraft(name);
                OpenRuleInEditor(draft, isNewDraft: true, templateHint: L10n.T("Template.DraftHint") + "\n" + L10n.T(tpl.DescriptionKey));
            }
        }).ToList();
        actions.ShowMenu(anchor, items);
    }

    private void ShowRuleExtras(string id, UiRect anchor)
    {
        actions.ShowMenu(anchor,
        [
            new() { Text = L10n.T("Auto.CopyCommand"), Click = () =>
                actions.ShowNotice(L10n.T(NativeTextClipboard.TryWrite(actions.OwnerWindow.Handle, RuleCommandLine.BuildShortCommand(id))
                    ? "Auto.CommandCopied" : "Auto.CommandCopyFailed")) },
            new() { Text = L10n.T("Auto.Favorite"), Checked = actions.Config.FavoriteRuleIds.Contains(id), Click = () => ToggleFavorite(id) }
        ]);
    }

    private void DuplicateRule(string id)
    {
        var rule = AutoRuleStore.Find(id);
        if (rule == null) return;
        var occupied = new HashSet<string>(_rules.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        string baseName = string.Format(L10n.T("Auto.CopyName"), rule.Name);
        string name = baseName;
        for (int suffix = 2; occupied.Contains(name); suffix++) name = $"{baseName} ({suffix})";
        var copy = rule.CopyAsNew(name);
        if (!actions.WriteRule(() => AutoRuleStore.Save(copy))) return;
        actions.Host.ViewSettingsChanged();
        LoadRules();
        actions.Invalidate();
        actions.ShowNotice(L10n.T("Auto.Copied"));
    }

    private void DeleteRule(string id)
    {
        if (!actions.WriteRule(() => AutoRuleStore.Delete(id))) return;
        actions.Config.FavoriteRuleIds.Remove(id);
        actions.Config.AutomationRuleOrder.Remove(id);
        actions.CommitSettings(); // deletion already committed to the rule store; refresh even if metadata failed
        actions.Host.ViewSettingsChanged();
        if (_editingRule?.Id == id) { _editingRule = null; _editingSteps.Clear(); }
        LoadRules();
        actions.Invalidate();
    }

    private void ToggleRuleEnabled(string id)
    {
        var rule = AutoRuleStore.Find(id);
        if (rule == null) return;
        rule.Enabled = !rule.Enabled;
        if (!actions.WriteRule(() => AutoRuleStore.Save(rule))) return;
        if (_editingRule?.Id == id) _editingRule.Enabled = rule.Enabled;
        actions.Host.ViewSettingsChanged();
        LoadRules();
        actions.Invalidate();
    }

    private void ToggleFavorite(string id)
    {
        if (actions.Config.FavoriteRuleIds.Contains(id)) actions.Config.FavoriteRuleIds.Remove(id);
        else actions.Config.FavoriteRuleIds.Add(id);
        if (!actions.CommitSettings()) return;
        actions.Host.ViewSettingsChanged();
        actions.Invalidate();
    }

    private void RunRuleById(string id)
    {
        _ = Task.Run(async () =>
        {
            string reply = await actions.Host.RunRule(id).ConfigureAwait(false);
            var parsed = RuleCommandLine.ReadResult(reply);
            string msg = parsed == null ? L10n.T("Auto.RunFailed") : parsed.Code switch
            {
                "rule_busy" => L10n.T("Auto.Running"),
                "canceled" => L10n.T("Auto.RunCanceled"),
                "ok" => string.Format(L10n.T("Auto.RunDone"), parsed.Succeeded),
                _ => parsed.Succeeded > 0
                    ? string.Format(L10n.T("Auto.RunPartial"), parsed.Succeeded, parsed.Failed)
                    : L10n.T("Auto.RunFailed")
            };
            actions.Host.TryPost(() =>
            {
                if (!_disposed)
                {
                    actions.ShowNotice(msg);
                    actions.RefreshHistoryIfOpen();
                    actions.Invalidate();
                }
            });
        });
    }

    private void SaveEditorRule()
    {
        if (_editingRule == null) return;
        actions.CloseInlineEdit(commit: true);
        if (!ValidateEditor(out string errMsg))
        {
            actions.ShowNotice(errMsg);
            return;
        }
        _editingRule.Name = _editingRule.Name.Trim();
        _editingRule.Hotkey = _editingRule.Hotkey.Trim();
        _editingRule.TriggerApp = _editingRule.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit
            ? _editingRule.TriggerApp.Trim() : "";
        if (_editingRule.Trigger != AutoRuleTrigger.Schedule)
        {
            _editingRule.ScheduleMode = 0;
            _editingRule.ScheduleTime = "";
            _editingRule.ScheduleWeekdays.Clear();
        }
        else if (string.IsNullOrWhiteSpace(_editingRule.ScheduleTime))
        {
            _editingRule.ScheduleTime = "08:00";
        }
        _editingRule.SetSteps(_editingSteps);
        if (!actions.WriteRule(() => AutoRuleStore.Save(_editingRule, L10n.T("Auto.DefaultNamePrefix")))) return;
        CancelHotkeyCapture();
        _isNewDraft = false;
        _templateHint = null;
        _selectedRuleId = _editingRule.Id;
        _editingRule = null;
        _editingSteps.Clear();
        actions.Host.ViewSettingsChanged();
        LoadRules();
        actions.ShowNotice(L10n.T("Auto.Saved"));
        actions.Invalidate();
    }

    private void CancelEditor()
    {
        CancelHotkeyCapture();
        actions.CloseInlineEdit(commit: false);
        _templateHint = null;
        _editingRule = null;
        _editingSteps.Clear();
        _isNewDraft = false;
        actions.Invalidate();
    }

    private bool ValidateEditor(out string message)
    {
        message = "";
        if (_editingRule == null) return false;
        if (_editingRule.Trigger == AutoRuleTrigger.Hotkey && string.IsNullOrWhiteSpace(_editingRule.Hotkey))
        { message = L10n.T("Auto.HotkeyRequired"); return false; }
        if (_editingRule.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit
            && !HasTriggerApp(_editingRule))
        { message = L10n.T("Auto.TriggerAppRequired"); return false; }
        if (_editingRule.Trigger == AutoRuleTrigger.Schedule && _editingRule.ScheduleMode == 2 && _editingRule.ScheduleWeekdays.Count == 0)
        { message = L10n.T("Auto.ScheduleWeekdayRequired"); return false; }

        for (int i = 0; i < _editingSteps.Count; i++)
        {
            var s = _editingSteps[i];
            string? key = null;
            if (s.Action == AutoRuleAction.ExecuteRule && (string.IsNullOrWhiteSpace(s.TargetRuleId) || s.TargetRuleId == _editingRule.Id || AutoRuleStore.Find(s.TargetRuleId) == null))
                key = "Auto.RuleRequired";
            else if (AutoRuleActions.NeedsApp(s.Action) && (string.IsNullOrWhiteSpace(s.TargetApp) || (_templateHint != null
                && !_apps.Any(app => string.Equals(app.ProcessName, s.TargetApp, StringComparison.OrdinalIgnoreCase)))))
                key = "Auto.ActionAppRequired";
            else if (AutoRuleActions.NeedsDevice(s.Action) && (string.IsNullOrWhiteSpace(s.TargetDeviceId)
                || (_templateHint != null && !AudioService.IsSystemDefault(s.TargetDeviceId) && !(AutoRuleActions.UsesCaptureDevice(s.Action) ? _inputs : _outputs)
                    .Any(endpoint => string.Equals(endpoint.Id, s.TargetDeviceId, StringComparison.OrdinalIgnoreCase)))
                || (s.Action is not (AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput)
                    && (AudioService.IsSystemDefault(s.TargetDeviceId) || PcRoutingApi.IsPhoneOutputDevice(s.TargetDeviceId)))))
                key = "Auto.DeviceRequired";
            else if ((s.Action == AutoRuleAction.LaunchProgram && s.CurrentLaunchItems().Count == 0)
                || (s.Action == AutoRuleAction.RunPowerShell && s.ProgramPaths.All(string.IsNullOrWhiteSpace)))
                key = "Auto.ProgramRequired";

            if (key != null)
            {
                message = string.Format(L10n.T("Template.StepRequired"), i + 1, L10n.T(key));
                return false;
            }
        }
        return true;
    }

    private void OpenListOptionsMenu(UiRect anchor)
    {
        var items = new List<UiMenuItem>
        {
            new() { Text = L10n.T("Auto.ResetFilters"), Click = () => { _searchText = ""; _stateFilter = 0; _triggerFilter = -1; _listScroll.Offset = 0f; actions.Invalidate(); } },
            new() { Text = L10n.T("Auto.ResetRuleOrder"), Enabled = actions.Config.AutomationRuleOrder.Count > 0, Click = () =>
            {
                actions.Config.AutomationRuleOrder.Clear();
                if (!actions.CommitSettings()) return;
                actions.Host.ViewSettingsChanged();
                LoadRules();
                actions.Invalidate();
            }},
            new() { Text = L10n.T("Auto.OpenFolder"), Click = () =>
            {
                if (!ShellOpen.Folder(AutoRuleStore.RulesDir)) actions.ShowNotice(L10n.T("Auto.OpenFolderFail"));
            }}
        };
        actions.ShowMenu(anchor, items);
    }

    private void OpenTriggerCombo(UiRect anchor)
    {
        if (_editingRule == null) return;
        var choices = new[]
        {
            new ComboChoice(((int)AutoRuleTrigger.Hotkey).ToString(CultureInfo.InvariantCulture), L10n.T("Auto.TriggerHotkey")),
            new ComboChoice(((int)AutoRuleTrigger.AppStart).ToString(CultureInfo.InvariantCulture), L10n.T("Auto.TriggerAppStart")),
            new ComboChoice(((int)AutoRuleTrigger.AppSwitch).ToString(CultureInfo.InvariantCulture), L10n.T("Auto.TriggerAppSwitch")),
            new ComboChoice(((int)AutoRuleTrigger.AppExit).ToString(CultureInfo.InvariantCulture), L10n.T("Auto.TriggerAppExit")),
            new ComboChoice(((int)AutoRuleTrigger.Schedule).ToString(CultureInfo.InvariantCulture), L10n.T("Auto.TriggerSchedule")),
            new ComboChoice(((int)AutoRuleTrigger.Startup).ToString(CultureInfo.InvariantCulture), L10n.T("Auto.TriggerStartup")),
        };
        actions.ShowCombo(anchor, choices, ((int)_editingRule.Trigger).ToString(CultureInfo.InvariantCulture), p =>
        {
            if (int.TryParse(p.Key, out int val))
            {
                CancelHotkeyCapture();
                _editingRule.Trigger = (AutoRuleTrigger)val;
                actions.Invalidate();
            }
        });
    }

    private void OpenScheduleModeCombo(UiRect anchor)
    {
        if (_editingRule == null) return;
        var choices = new[]
        {
            new ComboChoice("0", L10n.T("Auto.ScheduleOnce")),
            new ComboChoice("1", L10n.T("Auto.ScheduleDaily")),
            new ComboChoice("2", L10n.T("Auto.ScheduleWeekly")),
        };
        actions.ShowCombo(anchor, choices, Math.Clamp(_editingRule.ScheduleMode, 0, 2).ToString(CultureInfo.InvariantCulture), p =>
        {
            _editingRule.ScheduleMode = int.Parse(p.Key, CultureInfo.InvariantCulture);
            actions.Invalidate();
        });
    }

    private void OpenHourCombo(UiRect anchor, int curH, int curM)
    {
        if (_editingRule == null) return;
        var choices = Enumerable.Range(0, 24).Select(h => new ComboChoice(h.ToString("00", CultureInfo.InvariantCulture), h.ToString("00", CultureInfo.InvariantCulture))).ToList();
        actions.ShowCombo(anchor, choices, curH.ToString("00", CultureInfo.InvariantCulture), p =>
        {
            _editingRule.ScheduleTime = $"{p.Key}:{curM:00}";
            actions.Invalidate();
        });
    }

    private void OpenMinuteCombo(UiRect anchor, int curH, int curM)
    {
        if (_editingRule == null) return;
        var choices = Enumerable.Range(0, 60).Select(m => new ComboChoice(m.ToString("00", CultureInfo.InvariantCulture), m.ToString("00", CultureInfo.InvariantCulture))).ToList();
        actions.ShowCombo(anchor, choices, curM.ToString("00", CultureInfo.InvariantCulture), p =>
        {
            _editingRule.ScheduleTime = $"{curH:00}:{p.Key}";
            actions.Invalidate();
        });
    }

    private void DrawAppPicker(D2dTarget g, UiThemePalette theme, UiHitMap hit, string id, UiRect rect, string process, float fontSize = 12.5f)
    {
        var app = Array.Find(_apps, a => string.Equals(a.ProcessName, process, StringComparison.OrdinalIgnoreCase));
        string label = actions.Config.AppNames.TryGetValue(process, out string? alias) && !string.IsNullOrWhiteSpace(alias)
            ? alias : app?.Label ?? process;
        ComboAppIcon? icon = app != null ? ComboAppIcon.ForApp(app, actions.Config) :
            string.IsNullOrWhiteSpace(process) ? null : new ComboAppIcon(UiIcon.Application);
        UiPrimitives.DrawComboBox(g, theme, hit, id, rect, label, fontSize: fontSize,
            appIcon: icon, onAppIconLoaded: OnAppPickerIconLoaded);
    }

    private void OnAppPickerIconLoaded() => actions.Host.TryPost(actions.Invalidate);

    private IEnumerable<AudioAppInfo> AppPickerSources(bool includeWindowApps)
    {
#if SONICROUTE_PHONE
        if (includeWindowApps && actions.Host.Audio.ExternalAudio is { } bridge)
            return _apps.Concat(bridge.GetWindowTriggerApps());
#endif
        return _apps;
    }

    private bool HasTriggerApp(AutoRule rule)
    {
        string? app = rule.TriggerApp?.Trim();
        if (string.IsNullOrEmpty(app)) return false;
        bool switchesApp = rule.Trigger == AutoRuleTrigger.AppSwitch;
        if (AppPickerSources(switchesApp).Any(entry => string.Equals(entry.ProcessName, app, StringComparison.OrdinalIgnoreCase)))
            return true;
        return switchesApp && !_isNewDraft && AutoRuleStore.Find(rule.Id) is { Trigger: AutoRuleTrigger.AppSwitch } saved
            && string.Equals(saved.TriggerApp?.Trim(), app, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenAppPickerCombo(UiRect anchor, string current, Action<string> onPick, bool includeWindowApps = false)
    {
        var choices = new List<ComboChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in AppPickerSources(includeWindowApps))
        {
            string proc = app.ProcessName ?? "";
            if (string.IsNullOrWhiteSpace(proc) || !seen.Add(proc)) continue;
            string alias = actions.Config.AppNames.TryGetValue(proc, out var a) && !string.IsNullOrWhiteSpace(a) ? a : app.Label;
            choices.Add(new(proc, $"{alias} ({proc})", AppIcon: ComboAppIcon.ForApp(app, actions.Config)));
        }
        actions.ShowCombo(anchor, choices, current, p => { if (!string.IsNullOrEmpty(p.Key)) onPick(p.Key); });
    }

    private void OpenActionCombo(UiRect anchor, AutoRuleStep step)
    {
        var choices = new List<ComboChoice>();
        foreach (var group in AutoActionDisplay.Groups)
        {
            string groupLabel = L10n.T(group.Key);
            foreach (var act in group.Actions)
            {
                choices.Add(new ComboChoice(((int)act).ToString(CultureInfo.InvariantCulture), $"{groupLabel} · {L10n.T(AutoActionKey(act))}"));
            }
        }
        actions.ShowCombo(anchor, choices, ((int)step.Action).ToString(CultureInfo.InvariantCulture), p =>
        {
            if (int.TryParse(p.Key, out int val))
            {
                step.Action = (AutoRuleAction)val;
                if (step.Action == AutoRuleAction.LaunchProgram && step.LaunchItems.Count == 0 && step.ProgramPaths.Count > 0)
                    step.LaunchItems = step.EffectiveLaunchItems();
                actions.Invalidate();
            }
        });
    }

    private async void OpenWithMenu(AutoLaunchItem item, UiRect anchor)
    {
        int request = ++_openWithRequest;
        string path = item.Path;
        List<ComboChoice> choices;
        try { choices = await Task.Run(() => OpenWithChoices.ForFile(path)).ConfigureAwait(false); }
        catch (Exception ex)
        {
            ExceptionDiagnostics.Record("Native.OpenWith", ex);
            choices = [];
        }
        actions.Host.TryPost(() =>
        {
            if (request != _openWithRequest || _disposed || !_observed || !_editingSteps.Any(step => step.LaunchItems.Contains(item))) return;
            choices.Insert(0, new("", L10n.T("Auto.OpenWithDefault")));
            if (item.OpenWith.Length > 0 && !choices.Any(c => string.Equals(item.OpenWith, c.Key, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new(item.OpenWith, string.IsNullOrEmpty(item.OpenWithName) ? Path.GetFileNameWithoutExtension(item.OpenWith) : item.OpenWithName,
                    AppIcon: ComboAppIcon.ForExecutable(item.OpenWith)));
            const string chooseProgram = "<pick>";
            choices.Add(new(chooseProgram, L10n.T("Auto.OpenWithPick")));
            actions.ShowCombo(anchor, choices, item.OpenWith, picked =>
            {
                if (picked.Key == chooseProgram)
                {
                    string? exe = NativeDialogs.Open(actions.OwnerWindow.Handle, L10n.T("Auto.FileFilterExe") + "|*.exe");
                    if (exe == null) return;
                    item.OpenWith = exe;
                    item.OpenWithName = Path.GetFileNameWithoutExtension(exe);
                }
                else
                {
                    item.OpenWith = picked.Key;
                    item.OpenWithName = picked.Key.Length == 0 ? "" : picked.Label;
                }
                actions.Invalidate();
            });
        });
    }

    private void OpenStepDeviceCombo(UiRect anchor, AutoRuleStep step)
    {
        bool capture = AutoRuleActions.UsesCaptureDevice(step.Action);
        var flow = capture ? Flow.Capture : Flow.Render;
        var list = new List<ComboChoice>();
        if (step.Action is AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput)
        {
            string sysId = capture ? AudioService.SystemDefaultInputDeviceId : AudioService.SystemDefaultDeviceId;
            if (actions.Config.IsDeviceShown((EDataFlow)flow, sysId))
                list.Add(new ComboChoice(sysId, L10n.T(capture ? "Dev.SystemDefaultIn" : "Dev.SystemDefaultOut")));
        }
        var endpoints = capture ? _inputs : _outputs;
        foreach (var ep in endpoints)
        {
            if (!actions.Config.IsDeviceShown((EDataFlow)flow, ep.PreferenceId)) continue;
            if (step.Action is not (AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput) && PcRoutingApi.IsPhoneOutputDevice(ep.Id)) continue;
            string name = actions.Config.DeviceNames.TryGetValue(ep.Id, out var alias) && !string.IsNullOrWhiteSpace(alias) ? alias : ep.Name;
            list.Add(new ComboChoice(ep.Id, name));
        }
        actions.ShowCombo(anchor, list, step.TargetDeviceId, p =>
        {
            if (!string.IsNullOrEmpty(p.Key)) { step.TargetDeviceId = p.Key; actions.Invalidate(); }
        });
    }

    private string ResolveDeviceLabel(AutoRuleStep step)
    {
        if (string.IsNullOrWhiteSpace(step.TargetDeviceId)) return L10n.T("Auto.DeviceRequired");
        if (AudioService.IsSystemDefault(step.TargetDeviceId))
            return L10n.T(AutoRuleActions.UsesCaptureDevice(step.Action) ? "Dev.SystemDefaultIn" : "Dev.SystemDefaultOut");
        if (actions.Config.DeviceNames.TryGetValue(step.TargetDeviceId, out var alias) && !string.IsNullOrWhiteSpace(alias))
            return alias;
        var ep = (AutoRuleActions.UsesCaptureDevice(step.Action) ? _inputs : _outputs)
            .FirstOrDefault(d => string.Equals(d.Id, step.TargetDeviceId, StringComparison.OrdinalIgnoreCase));
        return ep?.Name ?? step.TargetDeviceId;
    }

    private static void UpdateStepVolumeFromX(AutoRuleStep step, UiRect sliderRect, float mouseX)
    {
        int pct = UiPrimitives.VolumeSliderValueFromX(sliderRect, mouseX);
        if (AutoRuleActions.AdjustsVolume(step.Action))
            step.VolumeDelta = Math.Clamp((int)MathF.Round((pct / 100f) * 200f - 100f), -100, 100);
        else
            step.Volume = Math.Clamp(pct, 0, 100);
    }

    private static void ParseScheduleTime(string? time, out int hour, out int minute)
    {
        hour = 8;
        minute = 0;
        if (!string.IsNullOrWhiteSpace(time) && TimeSpan.TryParse(time.Trim(), out var ts))
        {
            hour = Math.Clamp(ts.Hours, 0, 23);
            minute = Math.Clamp(ts.Minutes, 0, 59);
        }
    }

    private bool HasHotkeyConflict(AutoRule rule)
    {
        if (!rule.Enabled || rule.Trigger != AutoRuleTrigger.Hotkey || string.IsNullOrWhiteSpace(rule.Hotkey))
            return false;

        string hk = rule.Hotkey.Trim();
        var persisted = _rules.Find(other => other.Id == rule.Id);
        if (!_capturingHotkey && persisted is { Enabled: true, Trigger: AutoRuleTrigger.Hotkey }
            && string.Equals(persisted.Hotkey, hk, StringComparison.OrdinalIgnoreCase)
            && (!actions.Host.HotkeyStatuses.TryGetValue(AutoRuleService.HotkeyPrefix + rule.Id, out var status)
                || string.IsNullOrEmpty(status))) return true;

        foreach (string action in HotkeyActions.All)
        {
            if (!actions.Config.ExperimentalMic && action is HotkeyActions.ActSwitchInput or HotkeyActions.ActSwitchAllInput) continue;
            string configured = actions.Config.Hotkeys.GetValueOrDefault(action, HotkeyActions.Defaults[action]);
            if (string.Equals(configured, hk, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var other in _rules)
        {
            if (other.Id != rule.Id && other.Enabled && other.Trigger == AutoRuleTrigger.Hotkey
                && !string.IsNullOrWhiteSpace(other.Hotkey)
                && string.Equals(other.Hotkey.Trim(), hk, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string RuleActionSummary(AutoRule rule)
    {
        var steps = rule.Actions.Count > 0 ? rule.Actions : rule.CloneSteps();
        return string.Join(L10n.T("Auto.ActionListSeparator"), steps.Select(step =>
        {
            string label = L10n.T(AutoActionKey(step.Action));
            if (AutoRuleActions.SetsMute(step.Action)) label += "·" + L10n.T(step.Muted ? "Auto.MuteOn" : "Auto.MuteOff");
            if (AutoRuleActions.AdjustsVolume(step.Action))
                label += " " + string.Format(L10n.T("Auto.VolumeDeltaValue"), (step.VolumeDelta > 0 ? "+" : "") + step.VolumeDelta);
            if (AutoRuleActions.SetsVolume(step.Action)) label += " " + step.Volume + "%";
            if (step.Action == AutoRuleAction.LaunchProgram && step.LaunchMode == 1) label += " · " + L10n.T("Auto.LaunchModeRandom");
            return label;
        }));
    }

    private static string AutomationTriggerTitle(AutoRuleTrigger trigger) => L10n.T(trigger switch
    {
        AutoRuleTrigger.AppStart => "Auto.TriggerAppStart",
        AutoRuleTrigger.AppSwitch => "Auto.TriggerAppSwitch",
        AutoRuleTrigger.AppExit => "Auto.TriggerAppExit",
        AutoRuleTrigger.Schedule => "Auto.TriggerSchedule",
        AutoRuleTrigger.Startup => "Auto.TriggerStartup",
        _ => "Auto.TriggerHotkey",
    });

    private static string AutomationTriggerDetail(AutoRule rule)
    {
        if (rule.Trigger == AutoRuleTrigger.Hotkey)
            return string.IsNullOrWhiteSpace(rule.Hotkey) ? L10n.T("Auto.HotkeyUnbound") : rule.Hotkey;
        if (rule.Trigger == AutoRuleTrigger.Startup)
            return "SonicRoute.exe";
        if (rule.Trigger == AutoRuleTrigger.Schedule)
            return string.IsNullOrWhiteSpace(rule.ScheduleTime) ? "08:00" : rule.ScheduleTime;
        return string.IsNullOrWhiteSpace(rule.TriggerApp) ? "*" : rule.TriggerApp;
    }

    internal static string AutoActionKey(AutoRuleAction action) => action switch
    {
        AutoRuleAction.SetSystemOutput => "Auto.ActionSystemOutput",
        AutoRuleAction.SetSystemInput => "Auto.ActionSystemInput",
        AutoRuleAction.SetSystemVolume => "Auto.ActionSystemVolume",
        AutoRuleAction.ToggleSystemMute => "Auto.ActionSystemMute",
        AutoRuleAction.SetAppVolume => "Auto.ActionAppVolume",
        AutoRuleAction.ToggleAppMute => "Auto.ActionAppMute",
        AutoRuleAction.SetAppOutput => "Auto.ActionAppOutput",
        AutoRuleAction.SetAppInput => "Auto.ActionAppInput",
        AutoRuleAction.LaunchProgram => "Auto.ActionLaunch",
        AutoRuleAction.RunPowerShell => "Auto.ActionPowerShell",
        AutoRuleAction.ExecuteRule => "Auto.ActionExecuteRule",
        AutoRuleAction.ShowOsd => "Auto.ActionShowOsd",
        AutoRuleAction.SetSystemMute => "Auto.ActionSetSystemMute",
        AutoRuleAction.SetAppMute => "Auto.ActionSetAppMute",
        AutoRuleAction.AdjustSystemVolume => "Auto.ActionAdjustSystemVolume",
        AutoRuleAction.AdjustAppVolume => "Auto.ActionAdjustAppVolume",
        AutoRuleAction.SetGlobalMicMute => "Auto.ActionSetMicMute",
        AutoRuleAction.ToggleGlobalMicMute => "Auto.ActionToggleMicMute",
        AutoRuleAction.SetMicrophoneMute => "Auto.ActionSetMicrophoneMute",
        AutoRuleAction.ToggleMicrophoneMute => "Auto.ActionToggleMicrophoneMute",
        AutoRuleAction.SetPlaybackDeviceVolume => "Auto.ActionSetPlaybackDeviceVolume",
        AutoRuleAction.AdjustPlaybackDeviceVolume => "Auto.ActionAdjustPlaybackDeviceVolume",
        AutoRuleAction.SetPlaybackDeviceMute => "Auto.ActionSetPlaybackDeviceMute",
        AutoRuleAction.TogglePlaybackDeviceMute => "Auto.ActionTogglePlaybackDeviceMute",
        AutoRuleAction.SetDefaultInputVolume => "Auto.ActionSetDefaultInputVolume",
        AutoRuleAction.AdjustDefaultInputVolume => "Auto.ActionAdjustDefaultInputVolume",
        AutoRuleAction.SetDefaultInputMute => "Auto.ActionSetDefaultInputMute",
        AutoRuleAction.ToggleDefaultInputMute => "Auto.ActionToggleDefaultInputMute",
        AutoRuleAction.SetAppInputMute => "Auto.ActionSetAppInputMute",
        AutoRuleAction.ToggleAppInputMute => "Auto.ActionToggleAppInputMute",
        AutoRuleAction.SetCurrentAppInputMute => "Auto.ActionSetCurrentAppInputMute",
        AutoRuleAction.ToggleCurrentAppInputMute => "Auto.ActionToggleCurrentAppInputMute",
        AutoRuleAction.SetAllAppsVolume => "Auto.ActionSetAllAppsVolume",
        AutoRuleAction.AdjustAllAppsVolume => "Auto.ActionAdjustAllAppsVolume",
        AutoRuleAction.SetAllAppsMute => "Auto.ActionSetAllAppsMute",
        AutoRuleAction.ToggleAllAppsMute => "Auto.ActionToggleAllAppsMute",
        AutoRuleAction.ResetAllAppRouting => "Auto.ActionResetAllAppRouting",
        AutoRuleAction.ShowQuickPanel => "Auto.ActionShowQuickPanel",
        AutoRuleAction.HideQuickPanel => "Auto.ActionHideQuickPanel",
        _ => "Auto.Action"
    };

    private static (UiColor Bg, UiColor Border, UiColor Fg) StepPalette(bool dark, AutoRuleAction a)
    {
        int group = a switch
        {
            AutoRuleAction.SetSystemOutput or AutoRuleAction.SetSystemInput or AutoRuleAction.SetSystemVolume
                or AutoRuleAction.ToggleSystemMute or AutoRuleAction.SetSystemMute or AutoRuleAction.AdjustSystemVolume
                or AutoRuleAction.SetGlobalMicMute or AutoRuleAction.ToggleGlobalMicMute
                or AutoRuleAction.SetMicrophoneMute or AutoRuleAction.ToggleMicrophoneMute
                or AutoRuleAction.SetPlaybackDeviceVolume or AutoRuleAction.AdjustPlaybackDeviceVolume
                or AutoRuleAction.SetPlaybackDeviceMute or AutoRuleAction.TogglePlaybackDeviceMute
                or AutoRuleAction.SetDefaultInputVolume or AutoRuleAction.AdjustDefaultInputVolume
                or AutoRuleAction.SetDefaultInputMute or AutoRuleAction.ToggleDefaultInputMute => 0,
            AutoRuleAction.SetAppVolume or AutoRuleAction.ToggleAppMute or AutoRuleAction.SetAppOutput
                or AutoRuleAction.SetAppInput or AutoRuleAction.SetAppMute or AutoRuleAction.AdjustAppVolume
                or AutoRuleAction.SetAppInputMute or AutoRuleAction.ToggleAppInputMute
                or AutoRuleAction.SetCurrentAppInputMute or AutoRuleAction.ToggleCurrentAppInputMute
                or AutoRuleAction.SetAllAppsVolume or AutoRuleAction.AdjustAllAppsVolume
                or AutoRuleAction.SetAllAppsMute or AutoRuleAction.ToggleAllAppsMute or AutoRuleAction.ResetAllAppRouting => 1,
            AutoRuleAction.LaunchProgram or AutoRuleAction.RunPowerShell => 2,
            _ => 3
        };
        return group switch
        {
            0 => dark
                ? (UiColor.FromRgb(0x2A, 0x3A, 0x5C), UiColor.FromRgb(0x4A, 0x6F, 0xA8), UiColor.FromRgb(0xBF, 0xD4, 0xFF))
                : (UiColor.FromRgb(0xE9, 0xF1, 0xFE), UiColor.FromRgb(0xC7, 0xD9, 0xF8), UiColor.FromRgb(0x1E, 0x41, 0x91)),
            1 => dark
                ? (UiColor.FromRgb(0x4A, 0x37, 0x22), UiColor.FromRgb(0x7A, 0x5A, 0x2E), UiColor.FromRgb(0xFF, 0xD9, 0xA8))
                : (UiColor.FromRgb(0xFD, 0xF1, 0xE4), UiColor.FromRgb(0xF6, 0xDC, 0xBB), UiColor.FromRgb(0x8C, 0x4E, 0x11)),
            2 => dark
                ? (UiColor.FromRgb(0x3A, 0x33, 0x54), UiColor.FromRgb(0x5E, 0x4F, 0x8F), UiColor.FromRgb(0xD3, 0xC4, 0xFF))
                : (UiColor.FromRgb(0xF1, 0xED, 0xFE), UiColor.FromRgb(0xDE, 0xD4, 0xF9), UiColor.FromRgb(0x5C, 0x3F, 0xAA)),
            _ => dark
                ? (UiColor.FromRgb(0x25, 0x46, 0x4A), UiColor.FromRgb(0x3E, 0x6E, 0x70), UiColor.FromRgb(0xB8, 0xEC, 0xE9))
                : (UiColor.FromRgb(0xE8, 0xF6, 0xF5), UiColor.FromRgb(0xC3, 0xE7, 0xE5), UiColor.FromRgb(0x17, 0x66, 0x64)),
        };
    }

}
