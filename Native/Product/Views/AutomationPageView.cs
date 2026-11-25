using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AutoRule))]
internal partial class AutomationRuleJsonContext : JsonSerializerContext { }

internal sealed class AutomationPageView(IViewHostActions actions) : IViewPage
{
    private readonly UiScrollState _listScroll = new();
    private readonly UiScrollState _editorScroll = new();
    private readonly UiScrollState _historyScroll = new();
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _rightClicks = new(StringComparer.Ordinal);

    private List<AutoRule> _rules = new();
    private Endpoint[] _outputs = [];
    private Endpoint[] _inputs = [];
    private AudioAppInfo[] _apps = [];
    private bool _loadingDevices;

    private string _searchText = "";
    private string? _selectedRuleId;
    private AutoRule? _editingRule;
    private List<AutoRuleStep> _editingSteps = new();
    private bool _isNewDraft;
    private string? _templateHint;
    private bool _capturingHotkey;

    private bool _historyMode;
    private bool _historyLoading;
    private List<ExecutionHistoryEntry> _historyEntries = new();
    private string? _expandedRunId;

    // Rule drag-to-reorder state
    private readonly List<(string RuleId, UiRect RowRect)> _visibleRuleRows = new();
    private string? _dragRuleId;
    private bool _ruleDragging;
    private float _ruleDragStartY;
    private float _ruleDragPointerY;
    private string? _dropRuleId;
    private bool _dropRuleAfter;
    private UiRect _listViewport;

    // Step drag-to-reorder state
    private readonly List<(int Index, UiRect CardRect)> _stepCardRects = new();
    private int _dragStepIndex = -1;
    private bool _stepDragging;
    private float _stepDragStartY;
    private float _stepDragPointerY;
    private int _dropStepIndex = -1;
    private bool _dropStepAfter;
    private UiRect _editorViewport;

    // Volume slider drag state
    private int _draggingVolStepIndex = -1;
    private UiRect _draggingVolSliderRect;
    private readonly Dictionary<int, UiRect> _stepVolSliders = new();

    private bool _observed;
    private bool _disposed;

    public bool NeedsAnimationTick => _ruleDragging || _stepDragging;

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
        else if (_stepDragging)
        {
            const float edge = 28f;
            if (_stepDragPointerY < _editorViewport.Y + edge)
            {
                _editorScroll.Offset -= 10f;
                _editorScroll.Clamp();
                UpdateStepDropTarget();
            }
            else if (_stepDragPointerY > _editorViewport.Bottom - edge)
            {
                _editorScroll.Offset += 10f;
                _editorScroll.Clamp();
                UpdateStepDropTarget();
            }
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

    public void OnCaptureLost()
    {
        _listScroll.EndDrag();
        _editorScroll.EndDrag();
        _historyScroll.EndDrag();
        _draggingVolStepIndex = -1;
        if (_ruleDragging || _dragRuleId != null)
            CommitOrCancelRuleDrag(commit: false);
        if (_stepDragging || _dragStepIndex >= 0)
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
                    outs = actions.Host.Audio.RenderDevicesWithPhone();
                    ins = ProductAudio.Devices(Flow.Capture);
                    apps = actions.Host.Audio.Apps().ToArray();
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
        if (_historyMode) LoadHistoryAsync();
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

        if (_editingRule == null && _rules.Count > 0)
        {
            var pick = _rules.FirstOrDefault(r => r.Id == _selectedRuleId) ?? _rules[0];
            OpenRuleInEditor(pick, isNewDraft: false, templateHint: null);
        }
        else if (_editingRule != null && !_isNewDraft)
        {
            var match = _rules.FirstOrDefault(r => r.Id == _editingRule.Id);
            if (match == null)
            {
                _editingRule = null;
                _editingSteps.Clear();
                if (_rules.Count > 0) OpenRuleInEditor(_rules[0], false, null);
            }
        }
    }

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _clicks.Clear();
        _rightClicks.Clear();
        _visibleRuleRows.Clear();
        _stepCardRects.Clear();
        _stepVolSliders.Clear();

        float leftW = Math.Clamp(contentRect.Width * 0.34f, 260f, 300f);
        float gap = 14f;
        var leftCard = new UiRect(contentRect.X, contentRect.Y, leftW, contentRect.Height);
        var rightCard = new UiRect(leftCard.Right + gap, contentRect.Y, MathF.Max(260f, contentRect.Width - leftW - gap), contentRect.Height);

        RenderLeftPane(g, t, hit, leftCard);
        if (_historyMode)
            RenderHistoryPane(g, t, hit, rightCard);
        else
            RenderEditorPane(g, t, hit, rightCard);
    }

    private void RegisterBtn(string id, UiRect r, UiRect vp, UiHitMap hit, Action onClick, UiCursorKind cursor = UiCursorKind.Hand, string? tooltip = null, Action? onRightClick = null)
    {
        _clicks[id] = onClick;
        if (onRightClick != null) _rightClicks[id] = onRightClick;
        if (r.Bottom > vp.Y && r.Y < vp.Bottom)
        {
            var clipped = ClipToViewport(r, vp);
            if (!clipped.IsEmpty) hit.Add(id, clipped, cursor, tooltip);
        }
    }

    private void RenderLeftPane(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect card)
    {
        UiPrimitives.DrawCard(g, t, card);
        float pad = 12f;
        float innerW = card.Width - pad * 2f;
        float y = card.Y + pad;

        // Title + Toolbar row
        g.DrawText(L10n.T("Nav.Automation"), new UiRect(card.X + pad, y, innerW, 24f), t.TextPrimary, 16f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        y += 30f;

        // Toolbar: New | Templates | Import | History
        float btnGap = 6f;
        float iconBtnW = 32f;
        float newW = MathF.Max(80f, innerW - iconBtnW * 3f - btnGap * 3f);
        var newRect = new UiRect(card.X + pad, y, newW, 32f);
        UiPrimitives.DrawAccentButton(g, t, hit, "auto.new", newRect, L10n.T("Auto.New").Replace("＋ ", ""), icon: UiIcon.Plus, fontSize: 12f);
        RegisterBtn("auto.new", newRect, card, hit, NewRule, UiCursorKind.Hand, L10n.T("Auto.New"));

        float bx = newRect.Right + btnGap;
        var tplRect = new UiRect(bx, y, iconBtnW, 32f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "auto.templates", tplRect, "", icon: UiIcon.Templates, tooltip: L10n.T("Template.Open"));
        RegisterBtn("auto.templates", tplRect, card, hit, () => OpenTemplatesMenu(tplRect), UiCursorKind.Hand, L10n.T("Template.Open"));
        bx += iconBtnW + btnGap;

        var impRect = new UiRect(bx, y, iconBtnW, 32f);
        string impTip = IsZh() ? "导入规则 JSON" : "Import rule JSON";
        UiPrimitives.DrawSecondaryButton(g, t, hit, "auto.import", impRect, "", icon: UiIcon.File, tooltip: impTip);
        RegisterBtn("auto.import", impRect, card, hit, ImportRuleJson, UiCursorKind.Hand, impTip);
        bx += iconBtnW + btnGap;

        var histRect = new UiRect(bx, y, iconBtnW, 32f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "auto.history", histRect, "", icon: UiIcon.History, active: _historyMode, tooltip: L10n.T("History.Title"));
        RegisterBtn("auto.history", histRect, card, hit, ToggleHistoryMode, UiCursorKind.Hand, L10n.T("History.Title"));
        y += 40f;

        // Search box + options button
        var searchRect = new UiRect(card.X + pad, y, innerW - 36f, 30f);
        UiPrimitives.DrawTextBox(g, t, hit, "auto.search", searchRect, _searchText, L10n.T("Auto.Search"),
            focused: actions.ActiveInlineEditTag == "auto.search", fontSize: 12f);
        RegisterBtn("auto.search", searchRect, card, hit, () =>
        {
            actions.StartInlineEdit("auto.search", searchRect, _searchText,
                txt => { _searchText = txt; actions.Invalidate(); },
                (txt, _) => { _searchText = txt; actions.Invalidate(); });
        }, UiCursorKind.IBeam, L10n.T("Auto.Search"));

        var optRect = new UiRect(searchRect.Right + 6f, y, 30f, 30f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, "auto.listopt", optRect, UiIcon.Options, 15f, tooltip: L10n.T("Auto.ListOptions"));
        RegisterBtn("auto.listopt", optRect, card, hit, () => OpenListOptionsMenu(optRect), UiCursorKind.Hand, L10n.T("Auto.ListOptions"));
        y += 38f;

        // Rule list viewport
        _listViewport = new UiRect(card.X + pad, y, innerW, MathF.Max(40f, card.Bottom - pad - y));
        _listScroll.ViewportHeight = _listViewport.Height;

        var filtered = string.IsNullOrWhiteSpace(_searchText)
            ? _rules
            : _rules.Where(r => (r.Name ?? "").Contains(_searchText.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

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
                    bool selected = !_historyMode && _editingRule != null && _editingRule.Id == rule.Id;
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

                    // Name + trigger summary
                    float infoX = gripRect.Right + 4f;
                    float rightCtrlW = 94f;
                    float infoW = MathF.Max(40f, rowRect.Right - rightCtrlW - infoX - 4f);
                    string title = string.IsNullOrWhiteSpace(rule.Name) ? L10n.T("Auto.DefaultNamePrefix") : rule.Name;
                    g.DrawText(title, new UiRect(infoX, rowRect.Y + 9f, infoW, 20f),
                        rule.Enabled ? t.TextPrimary : t.TextSecondary, 12.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);

                    string sub = AutomationTriggerTitle(rule.Trigger);
                    string det = AutomationTriggerDetail(rule);
                    if (!string.IsNullOrEmpty(det)) sub += " · " + det;
                    int stepCnt = rule.CloneSteps().Count;
                    sub += $" ({stepCnt})";
                    bool hkConflict = HasHotkeyConflict(rule);
                    if (hkConflict) sub += " · " + L10n.T("Auto.HotkeyConflict");
                    g.DrawText(sub, new UiRect(infoX, rowRect.Y + 32f, infoW, 18f),
                        hkConflict ? t.Fail : t.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

                    // Right controls: Run | Favorite | Enable Switch
                    float cx = rowRect.Right - 6f;
                    var swRect = new UiRect(cx - 34f, rowRect.CenterY - 12f, 34f, 24f);
                    UiPrimitives.DrawSwitch(g, t, hit, "auto.sw." + rule.Id, swRect, rule.Enabled);
                    RegisterBtn("auto.sw." + rule.Id, swRect, _listViewport, hit, () => ToggleRuleEnabled(rule.Id), UiCursorKind.Hand,
                        L10n.T(rule.Enabled ? "Auto.Disable" : "Auto.Enable"));

                    cx = swRect.X - 4f;
                    bool fav = actions.Config.FavoriteRuleIds.Contains(rule.Id);
                    var favRect = new UiRect(cx - 24f, rowRect.CenterY - 12f, 24f, 24f);
                    UiPrimitives.DrawGhostIconButton(g, t, hit, "auto.fav." + rule.Id, favRect, UiIcon.Pin, 13f,
                        iconColor: fav ? t.Accent : t.TextSecondary, active: fav, tooltip: L10n.T("Auto.Favorite"));
                    RegisterBtn("auto.fav." + rule.Id, favRect, _listViewport, hit, () => ToggleFavorite(rule.Id), UiCursorKind.Hand, L10n.T("Auto.Favorite"));

                    cx = favRect.X - 2f;
                    var runRect = new UiRect(cx - 24f, rowRect.CenterY - 12f, 24f, 24f);
                    UiPrimitives.DrawGhostIconButton(g, t, hit, "auto.run." + rule.Id, runRect,
                        running ? UiIcon.Waiting : UiIcon.Play, 13f,
                        iconColor: running ? t.Accent : t.TextPrimary, enabled: !running, tooltip: L10n.T(running ? "Auto.Running" : "Auto.Run"));
                    RegisterBtn("auto.run." + rule.Id, runRect, _listViewport, hit, () => RunRuleById(rule.Id), UiCursorKind.Hand, L10n.T("Auto.Run"));

                    // Row click body (select in editor) + right-click context menu
                    var bodyRect = new UiRect(gripRect.Right, rowRect.Y, runRect.X - gripRect.Right, rowH);
                    RegisterBtn(rowId, bodyRect, _listViewport, hit, () =>
                    {
                        _historyMode = false;
                        var found = AutoRuleStore.Find(rule.Id) ?? rule;
                        OpenRuleInEditor(found, false, null);
                    }, UiCursorKind.Hand, rule.Name, () => ShowRuleContextMenu(rule));

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

    private void RenderHistoryPane(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect card)
    {
        UiPrimitives.DrawCard(g, t, card);
        float pad = 18f;
        float innerW = card.Width - pad * 2f;
        float y = card.Y + pad;

        // Header
        g.DrawIcon(UiIcon.History, new UiRect(card.X + pad, y + 4f, 20f, 20f), t.Accent, 1.8f);
        g.DrawText(L10n.T("History.Title"), new UiRect(card.X + pad + 28f, y, innerW - 220f, 28f), t.TextPrimary, 17f, 600, UiTextAlign.Leading, UiVertAlign.Center);

        var backRect = new UiRect(card.Right - pad - 76f, y, 76f, 30f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "hist.close", backRect, L10n.T("Auto.Cancel"), fontSize: 12f);
        RegisterBtn("hist.close", backRect, card, hit, () => { _historyMode = false; actions.Invalidate(); });

        var clearRect = new UiRect(backRect.X - 88f, y, 80f, 30f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "hist.clear", clearRect, L10n.T("History.Clear"), danger: true, fontSize: 11.5f);
        RegisterBtn("hist.clear", clearRect, card, hit, ClearHistoryAsync);

        var refRect = new UiRect(clearRect.X - 76f, y, 68f, 30f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "hist.refresh", refRect, L10n.T("History.Refresh"), fontSize: 11.5f);
        RegisterBtn("hist.refresh", refRect, card, hit, LoadHistoryAsync);
        y += 40f;

        var vp = new UiRect(card.X + pad, y, innerW, MathF.Max(40f, card.Bottom - pad - y));
        _historyScroll.ViewportHeight = vp.Height;

        bool enabled = actions.Config.ExperimentalMode && actions.Config.ExecutionHistoryEnabled;
        if (!enabled)
        {
            g.DrawText(L10n.T("History.Hint"), new UiRect(vp.X, vp.Y + 12f, vp.Width, 44f), t.TextSecondary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);
            if (!actions.Config.ExecutionHistoryEnabled || !actions.Config.ExperimentalMode)
            {
                var enRect = new UiRect(vp.X, vp.Y + 66f, 160f, 34f);
                UiPrimitives.DrawAccentButton(g, t, hit, "hist.enable", enRect, IsZh() ? "开启执行记录" : "Enable History");
                RegisterBtn("hist.enable", enRect, card, hit, () =>
                {
                    actions.Config.ExperimentalUnlocked = true;
                    actions.Config.ExperimentalMode = true;
                    actions.Config.ExecutionHistoryEnabled = true;
                    ConfigService.TrySave(actions.Config);
                    actions.NotifySettingsChanged();
                    LoadHistoryAsync();
                });
            }
            return;
        }

        if (_historyLoading)
        {
            g.DrawText(L10n.T("Tray.Loading"), vp, t.TextSecondary, 13f, 400, UiTextAlign.Center, UiVertAlign.Center);
            return;
        }

        if (_historyEntries.Count == 0)
        {
            g.DrawText(L10n.T("History.Empty"), vp.Deflate(20f, 20f), t.TextSecondary, 12.5f, 400, UiTextAlign.Center, UiVertAlign.Center, wrap: true);
            return;
        }

        float listW = _historyScroll.MaxOffset > 0.5f ? vp.Width - UiMetrics.ScrollBarWidth - 6f : vp.Width;
        float hy = vp.Y - _historyScroll.Offset;
        float startY = hy;

        g.PushClip(vp);
        hit.PushClip(vp);
        for (int i = 0; i < _historyEntries.Count; i++)
        {
            var entry = _historyEntries[i];
            bool expanded = _expandedRunId == entry.RunId;
            float stepsH = expanded ? (entry.Steps.Count * 22f + 10f) : 0f;
            float ch = 64f + stepsH;
            var er = new UiRect(vp.X, hy, listW, ch);
            if (er.Bottom >= vp.Y && er.Y <= vp.Bottom)
            {
                g.FillRoundedRect(er, 8f, t.SurfaceCard);
                g.DrawRoundedRect(er, 8f, expanded ? t.Accent : t.Border, 1f);

                string timeStr = entry.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                g.DrawText(entry.RuleName, new UiRect(er.X + 12f, er.Y + 8f, er.Width - 130f, 20f), t.TextPrimary, 13f, 600, UiTextAlign.Leading, UiVertAlign.Center);

                bool ok = string.Equals(entry.Status, "success", StringComparison.OrdinalIgnoreCase);
                bool fail = string.Equals(entry.Status, "failed", StringComparison.OrdinalIgnoreCase);
                UiColor badgeBg = ok ? t.Success.WithAlphaFraction(0.18f) : fail ? t.Fail.WithAlphaFraction(0.18f) : t.NavSelected;
                UiColor badgeFg = ok ? t.Success : fail ? t.Fail : t.Accent;
                UiPrimitives.DrawBadge(g, t, new UiRect(er.Right - 92f, er.Y + 8f, 80f, 20f), L10n.T("History.Status." + entry.Status), badgeBg, badgeFg);

                string meta = $"{timeStr} · {L10n.T("History.Source." + entry.Source)} · {string.Format(L10n.T("History.Summary"), entry.Succeeded, entry.Failed, entry.Skipped)} · {entry.ElapsedMs} ms";
                g.DrawText(meta, new UiRect(er.X + 12f, er.Y + 32f, er.Width - 24f, 20f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

                if (expanded)
                {
                    float sy = er.Y + 58f;
                    foreach (var step in entry.Steps)
                    {
                        string actName = Enum.IsDefined(typeof(AutoRuleAction), step.Action)
                            ? L10n.T(AutoActionKey((AutoRuleAction)step.Action))
                            : step.Action.ToString(CultureInfo.InvariantCulture);
                        string stepLine = $"{step.Number}. {actName} — {L10n.T("History.Status." + step.Status)} ({step.ElapsedMs} ms)";
                        if (!string.IsNullOrEmpty(step.Reason))
                            stepLine += " · " + L10n.T("History.Reason." + step.Reason);
                        g.DrawText(stepLine, new UiRect(er.X + 16f, sy, er.Width - 28f, 20f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                        sy += 22f;
                    }
                }

                string hid = "hist.entry." + entry.RunId;
                RegisterBtn(hid, new UiRect(er.X, er.Y, er.Width, 58f), vp, hit, () =>
                {
                    _expandedRunId = _expandedRunId == entry.RunId ? null : entry.RunId;
                    actions.Invalidate();
                });
            }
            hy += ch + 8f;
        }
        hit.PopClip();
        g.PopClip();

        _historyScroll.ContentHeight = MathF.Max(0f, hy - startY);
        _historyScroll.Clamp();
        _historyScroll.Draw(g, t, hit, "hist.scroll", vp);
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
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(ex, y, bodyW - 80f, 24f), UiIcon.Info, L10n.T("Auto.BasicInfo"));
        // Enabled switch on right
        var enSwRect = new UiRect(ex + bodyW - 40f, y, 36f, 24f);
        UiPrimitives.DrawSwitch(g, t, hit, "edit.enabled", enSwRect, _editingRule.Enabled);
        RegisterBtn("edit.enabled", enSwRect, _editorViewport, hit, () => { _editingRule.Enabled = !_editingRule.Enabled; actions.Invalidate(); },
            tooltip: L10n.T(_editingRule.Enabled ? "Auto.Disable" : "Auto.Enable"));
        g.DrawText(L10n.T(_editingRule.Enabled ? "Auto.Enabled" : "Auto.Disabled"),
            new UiRect(enSwRect.X - 72f, y, 66f, 24f), _editingRule.Enabled ? t.Accent : t.TextSecondary, 11.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
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
                txt => { _editingRule.Name = txt; },
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
            var hkRect = new UiRect(inputX, y, MathF.Max(100f, inputW - 72f), 34f);
            string hkLabel = _capturingHotkey
                ? L10n.T("Auto.HotkeyCapturing")
                : string.IsNullOrWhiteSpace(_editingRule.Hotkey) ? L10n.T("Auto.HotkeyUnbound") : _editingRule.Hotkey;
            UiPrimitives.DrawSecondaryButton(g, t, hit, "edit.hk.rec", hkRect, hkLabel, active: _capturingHotkey, icon: UiIcon.Keyboard);
            RegisterBtn("edit.hk.rec", hkRect, _editorViewport, hit, ToggleHotkeyCapture);

            var hkClrRect = new UiRect(hkRect.Right + 6f, y, 66f, 34f);
            UiPrimitives.DrawSecondaryButton(g, t, hit, "edit.hk.clr", hkClrRect, L10n.T("Auto.HotkeyClear"));
            RegisterBtn("edit.hk.clr", hkClrRect, _editorViewport, hit, () =>
            {
                CancelHotkeyCapture();
                _editingRule.Hotkey = "";
                actions.Invalidate();
            });
            y += 38f;
            g.DrawText(L10n.T("Auto.HotkeyHint"), new UiRect(inputX, y, inputW, 18f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
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
            float pickW = MathF.Min(190f, inputW * 0.45f);
            var boxRect = new UiRect(inputX, y, inputW - pickW - 6f, 34f);
            UiPrimitives.DrawTextBox(g, t, hit, "edit.trigapp.box", boxRect, _editingRule.TriggerApp,
                "chrome / game.exe", focused: actions.ActiveInlineEditTag == "edit.trigapp.box");
            RegisterBtn("edit.trigapp.box", boxRect, _editorViewport, hit, () =>
            {
                actions.StartInlineEdit("edit.trigapp.box", boxRect, _editingRule.TriggerApp,
                    txt => { _editingRule.TriggerApp = txt; },
                    (txt, _) => { _editingRule.TriggerApp = txt.Trim(); actions.Invalidate(); });
            }, UiCursorKind.IBeam);

            var pickRect = new UiRect(boxRect.Right + 6f, y, pickW, 34f);
            UiPrimitives.DrawComboBox(g, t, hit, "edit.trigapp.pick", pickRect, IsZh() ? "选择运行中应用" : "Running app");
            RegisterBtn("edit.trigapp.pick", pickRect, _editorViewport, hit, () =>
                OpenAppPickerCombo(pickRect, _editingRule.TriggerApp, proc => { _editingRule.TriggerApp = proc; actions.Invalidate(); }));
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
        _editorScroll.Draw(g, t, hit, "edit.scroll", _editorViewport);

        // Footer Save bar
        float fy = card.Bottom - footerH;
        g.DrawLine(card.X + pad, fy, card.Right - pad, fy, t.Border, 1f, roundCaps: false);
        float btnY = fy + 10f;
        float rightX = card.Right - pad;

        var saveRect = new UiRect(rightX - 108f, btnY, 108f, 34f);
        UiPrimitives.DrawAccentButton(g, t, hit, "edit.save", saveRect, IsZh() ? "保存规则" : L10n.T("Auto.Save"));
        RegisterBtn("edit.save", saveRect, card, hit, SaveEditorRule);

        var testRect = new UiRect(saveRect.X - 106f, btnY, 96f, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "edit.test", testRect, IsZh() ? "立即测试" : L10n.T("Auto.Run"), icon: UiIcon.Play);
        RegisterBtn("edit.test", testRect, card, hit, TestEditorRule);

        var cancelRect = new UiRect(testRect.X - 90f, btnY, 80f, 34f);
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
        _stepCardRects.Add((index, cardRect));

        bool isDragged = _stepDragging && _dragStepIndex == index;
        (UiColor bg, UiColor border, UiColor fg) = StepPalette(t.IsDark, step.Action);
        if (isDragged) bg = bg.WithAlphaFraction(0.55f);

        g.FillRoundedRect(cardRect, 10f, bg);
        g.DrawRoundedRect(cardRect, 10f, isDragged ? t.Accent : border, isDragged ? 1.8f : 1f);

        if (_stepDragging && _dropStepIndex == index && _dragStepIndex != index)
        {
            float lineY = _dropStepAfter ? cardRect.Bottom + 4f : cardRect.Y - 4f;
            g.DrawLine(cardRect.X, lineY, cardRect.Right, lineY, t.Accent, 2.5f);
        }

        // Top bar: Grip + Step index badge + Action Combo + Up/Down + Copy + Delete
        float ty = y + pad;
        var gripRect = new UiRect(x + pad, ty + 2f, 22f, 30f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.grip.{index}", gripRect, UiIcon.Grip, 14f, tooltip: L10n.T("Auto.DragOrder"));

        var idxBadgeRect = new UiRect(gripRect.Right + 4f, ty + 6f, 44f, 22f);
        UiPrimitives.DrawBadge(g, t, idxBadgeRect, $"#{index + 1}", t.SurfaceBg, fg, border, 11f);

        float rightBtnsW = 128f;
        float comboW = Math.Clamp(innerW - 76f - rightBtnsW, 130f, 300f);
        var actionComboRect = new UiRect(idxBadgeRect.Right + 8f, ty, comboW, 34f);
        UiPrimitives.DrawComboBox(g, t, hit, $"step.act.{index}", actionComboRect, L10n.T(AutoActionKey(step.Action)), fontSize: 12f);
        RegisterBtn($"step.act.{index}", actionComboRect, _editorViewport, hit, () => OpenActionCombo(actionComboRect, step));

        // Right step buttons: Delete | Copy | Down | Up
        float rx = x + w - pad;
        bool canDelete = _editingSteps.Count > 1;
        var delRect = new UiRect(rx - 30f, ty + 2f, 30f, 30f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.del.{index}", delRect, UiIcon.Delete, 14f,
            iconColor: canDelete ? t.Fail : t.TextSecondary, enabled: canDelete, tooltip: L10n.T("Auto.Delete"));
        if (canDelete) RegisterBtn($"step.del.{index}", delRect, _editorViewport, hit, () => { _editingSteps.RemoveAt(index); actions.Invalidate(); });

        rx = delRect.X - 2f;
        var copyRect = new UiRect(rx - 28f, ty + 2f, 28f, 30f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.cp.{index}", copyRect, UiIcon.Copy, 14f, tooltip: L10n.T("Auto.Copy"));
        RegisterBtn($"step.cp.{index}", copyRect, _editorViewport, hit, () => { _editingSteps.Insert(index + 1, step.Clone()); actions.Invalidate(); });

        rx = copyRect.X - 2f;
        bool canDown = index + 1 < _editingSteps.Count;
        var downRect = new UiRect(rx - 26f, ty + 2f, 26f, 30f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.dn.{index}", downRect, UiIcon.ChevronDown, 13f, enabled: canDown);
        if (canDown) RegisterBtn($"step.dn.{index}", downRect, _editorViewport, hit, () =>
        {
            (_editingSteps[index], _editingSteps[index + 1]) = (_editingSteps[index + 1], _editingSteps[index]);
            actions.Invalidate();
        });

        rx = downRect.X - 2f;
        bool canUp = index > 0;
        var upRect = new UiRect(rx - 26f, ty + 2f, 26f, 30f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.up.{index}", upRect, UiIcon.ChevronUp, 13f, enabled: canUp);
        if (canUp) RegisterBtn($"step.up.{index}", upRect, _editorViewport, hit, () =>
        {
            (_editingSteps[index - 1], _editingSteps[index]) = (_editingSteps[index], _editingSteps[index - 1]);
            actions.Invalidate();
        });

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
            h += Math.Max(1, step.ProgramPaths.Count) * 36f + 36f;
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
            float pickW = MathF.Min(150f, iw * 0.42f);
            var appBox = new UiRect(ix, y, iw - pickW - 6f, 32f);
            string tag = $"step.{index}.app";
            UiPrimitives.DrawTextBox(g, t, hit, tag, appBox, step.TargetApp, "app.exe", focused: actions.ActiveInlineEditTag == tag, fontSize: 12f);
            RegisterBtn(tag, appBox, _editorViewport, hit, () =>
            {
                actions.StartInlineEdit(tag, appBox, step.TargetApp, txt => step.TargetApp = txt, (txt, _) => { step.TargetApp = txt.Trim(); actions.Invalidate(); });
            }, UiCursorKind.IBeam);

            var pickRect = new UiRect(appBox.Right + 6f, y, pickW, 32f);
            UiPrimitives.DrawComboBox(g, t, hit, $"step.{index}.apppick", pickRect, IsZh() ? "选择应用" : "Pick App", fontSize: 11.5f);
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
                var clipped = ClipToViewport(slRect, _editorViewport);
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
                actions.StartInlineEdit(tTag, tRect, step.OsdTitle, s => step.OsdTitle = s, (s, _) => { step.OsdTitle = s; actions.Invalidate(); }), UiCursorKind.IBeam);

            g.DrawText(L10n.T("Auto.OsdText"), new UiRect(tRect.Right + 10f, y, 58f, 32f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var xRect = new UiRect(tRect.Right + 72f, y, MathF.Max(60f, w - (tRect.Right - x + 72f)), 32f);
            string xTag = $"step.{index}.osdx";
            UiPrimitives.DrawTextBox(g, t, hit, xTag, xRect, step.OsdText, "", focused: actions.ActiveInlineEditTag == xTag, fontSize: 12f);
            RegisterBtn(xTag, xRect, _editorViewport, hit, () =>
                actions.StartInlineEdit(xTag, xRect, step.OsdText, s => step.OsdText = s, (s, _) => { step.OsdText = s; actions.Invalidate(); }), UiCursorKind.IBeam);
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
                var pathRect = new UiRect(ix, y, MathF.Max(80f, iw - 64f), 30f);
                string pTag = $"step.{index}.li.{li}";
                UiPrimitives.DrawTextBox(g, t, hit, pTag, pathRect, item.Path, "C:\\...", focused: actions.ActiveInlineEditTag == pTag, fontSize: 11.5f);
                RegisterBtn(pTag, pathRect, _editorViewport, hit, () =>
                    actions.StartInlineEdit(pTag, pathRect, item.Path, s => item.Path = s, (s, _) => { item.Path = s.Trim(); actions.Invalidate(); }), UiCursorKind.IBeam);

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

                var rmRect = new UiRect(brRect.Right + 2f, y, 28f, 30f);
                UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.{index}.lirm.{li}", rmRect, UiIcon.Close, 13f, iconColor: t.Fail);
                RegisterBtn($"step.{index}.lirm.{li}", rmRect, _editorViewport, hit, () => { step.LaunchItems.RemoveAt(itemIdx); actions.Invalidate(); });
                y += 36f;
            }

            var addFileRect = new UiRect(ix, y, 148f, 28f);
            UiPrimitives.DrawSecondaryButton(g, t, hit, $"step.{index}.liadd", addFileRect, L10n.T("Auto.AddFile"), fontSize: 11.5f);
            RegisterBtn($"step.{index}.liadd", addFileRect, _editorViewport, hit, () =>
            {
                if (NativeDialogs.Open(actions.OwnerWindow.Handle, L10n.T("Auto.FileFilterAll") + " (*.*)|*.*|" + L10n.T("Auto.FileFilterExe") + " (*.exe)|*.exe") is { } f)
                    step.LaunchItems.Add(new AutoLaunchItem { Path = f });
                else
                    step.LaunchItems.Add(new AutoLaunchItem());
                actions.Invalidate();
            });
            y += 36f;
        }
        else if (step.Action == AutoRuleAction.RunPowerShell)
        {
            if (step.ProgramPaths.Count == 0) step.ProgramPaths.Add("");
            for (int pi = 0; pi < step.ProgramPaths.Count; pi++)
            {
                int pathIdx = pi;
                if (pi == 0)
                    g.DrawText(L10n.T("Auto.Script"), new UiRect(x, y, lw - 6f, 30f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                var pRect = new UiRect(ix, y, MathF.Max(80f, iw - 34f), 30f);
                string psTag = $"step.{index}.ps.{pi}";
                UiPrimitives.DrawTextBox(g, t, hit, psTag, pRect, step.ProgramPaths[pi], "script.ps1 / command", focused: actions.ActiveInlineEditTag == psTag, mono: true, fontSize: 11.5f);
                RegisterBtn(psTag, pRect, _editorViewport, hit, () =>
                    actions.StartInlineEdit(psTag, pRect, step.ProgramPaths[pathIdx], s => step.ProgramPaths[pathIdx] = s, (s, _) => { step.ProgramPaths[pathIdx] = s; actions.Invalidate(); }, mono: true), UiCursorKind.IBeam);

                var rmRect = new UiRect(pRect.Right + 4f, y, 28f, 30f);
                UiPrimitives.DrawGhostIconButton(g, t, hit, $"step.{index}.psrm.{pi}", rmRect, UiIcon.Close, 13f, iconColor: t.Fail);
                RegisterBtn($"step.{index}.psrm.{pi}", rmRect, _editorViewport, hit, () => { step.ProgramPaths.RemoveAt(pathIdx); actions.Invalidate(); });
                y += 36f;
            }
            var addPathRect = new UiRect(ix, y, 130f, 28f);
            UiPrimitives.DrawSecondaryButton(g, t, hit, $"step.{index}.psadd", addPathRect, L10n.T("Auto.AddPath"), fontSize: 11.5f);
            RegisterBtn($"step.{index}.psadd", addPathRect, _editorViewport, hit, () => { step.ProgramPaths.Add(""); actions.Invalidate(); });
            y += 36f;
        }

        // Delay row
        g.DrawText(L10n.T("Auto.Delay"), new UiRect(x, y, lw - 6f, 30f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        var delayRect = new UiRect(ix, y, 80f, 30f);
        string delayTag = $"step.{index}.delay";
        UiPrimitives.DrawTextBox(g, t, hit, delayTag, delayRect, step.DelayMs.ToString(CultureInfo.InvariantCulture), "0", focused: actions.ActiveInlineEditTag == delayTag, fontSize: 12f);
        RegisterBtn(delayTag, delayRect, _editorViewport, hit, () =>
        {
            actions.StartInlineEdit(delayTag, delayRect, step.DelayMs.ToString(CultureInfo.InvariantCulture), null, (s, commit) =>
            {
                if (!commit) return;
                if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                {
                    step.DelayMs = Math.Clamp(ms, 0, 600000);
                    actions.Invalidate();
                }
            });
        }, UiCursorKind.IBeam);
        g.DrawText(L10n.T("Auto.DelayUnit") + " (" + L10n.T("Auto.DelayRange") + ")",
            new UiRect(delayRect.Right + 8f, y, MathF.Max(40f, iw - 88f), 30f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        y += 38f;

        // Stop on failure checkbox
        var stopRect = new UiRect(x, y, w, 22f);
        UiPrimitives.DrawCheckBox(g, t, hit, $"step.{index}.stop", stopRect, step.StopOnFailure, L10n.T("Auto.StopOnFailure"), 11.5f);
        RegisterBtn($"step.{index}.stop", stopRect, _editorViewport, hit, () => { step.StopOnFailure = !step.StopOnFailure; actions.Invalidate(); });
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (_listScroll.DragMove(y) || _editorScroll.DragMove(y) || _historyScroll.DragMove(y))
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

        if (_dragStepIndex >= 0)
        {
            if (!leftDown) { CommitOrCancelStepDrag(commit: _stepDragging); return true; }
            _stepDragPointerY = y;
            if (!_stepDragging && MathF.Abs(y - _stepDragStartY) >= 6f)
                _stepDragging = true;
            if (_stepDragging) UpdateStepDropTarget();
            return true;
        }

        return false;
    }

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        if (e.Button == UiMouseButton.Right)
        {
            if (targetId == null || !_rightClicks.TryGetValue(targetId, out var contextMenu)) return false;
            contextMenu();
            return true;
        }
        if (e.Button != UiMouseButton.Left) return false;

        if (targetId == "auto.list.scroll" && _listScroll.BeginDrag(e.Y)) return true;
        if (targetId == "edit.scroll" && _editorScroll.BeginDrag(e.Y)) return true;
        if (targetId == "hist.scroll" && _historyScroll.BeginDrag(e.Y)) return true;

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
        if (!_historyMode && _editorViewport.Contains(e.X, e.Y))
        {
            for (int i = 0; i < _stepCardRects.Count; i++)
            {
                int idx = _stepCardRects[i].Index;
                if (targetId == $"step.grip.{idx}")
                {
                    _dragStepIndex = idx;
                    _stepDragStartY = _stepDragPointerY = e.Y;
                    _stepDragging = false;
                    return true;
                }
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
        if (_historyScroll.Dragging) { _historyScroll.EndDrag(); return true; }

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

        if (_dragStepIndex >= 0)
        {
            CommitOrCancelStepDrag(commit: _stepDragging);
            return true;
        }

        if (clickId == null || !_clicks.TryGetValue(clickId, out var click)) return false;
        click();
        return true;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (horizontal) return false;
        if (_listViewport.Contains(x, y)) { _listScroll.OnWheel(delta); return true; }
        if (_historyMode) { _historyScroll.OnWheel(delta); return true; }
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
        if (vk == 0x1B /*VK_ESCAPE*/)
        {
            if (_ruleDragging || _dragRuleId != null)
            {
                CommitOrCancelRuleDrag(commit: false);
                return true;
            }
            if (_stepDragging || _dragStepIndex >= 0)
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
            if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
                return true;
            string? keyName = KeyName(vk);
            if (keyName != null)
            {
                var parts = new List<string>(5);
                if (ctrl) parts.Add("Ctrl");
                if (alt) parts.Add("Alt");
                if (shift) parts.Add("Shift");
                parts.Add(keyName);
                _editingRule.Hotkey = string.Join("+", parts);
                CancelHotkeyCapture();
                actions.Invalidate();
                return true;
            }
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
        if (_dropRuleId != null && _dragRuleId != null && _dropRuleId != _dragRuleId)
        {
            var ids = _rules.Select(r => r.Id).ToList();
            if (ids.Remove(_dragRuleId))
            {
                int idx = ids.IndexOf(_dropRuleId);
                if (idx >= 0)
                {
                    ids.Insert(idx + (_dropRuleAfter ? 1 : 0), _dragRuleId);
                    actions.Config.AutomationRuleOrder = ids;
                }
            }
        }
    }

    private void CommitOrCancelRuleDrag(bool commit)
    {
        if (commit && _dropRuleId != null && _dragRuleId != null && _dropRuleId != _dragRuleId)
        {
            ConfigService.TrySave(actions.Config);
            actions.Host.ViewSettingsChanged();
            LoadRules();
        }
        else if (!commit && _ruleDragging)
        {
            var reloaded = ConfigService.Load();
            actions.Config.AutomationRuleOrder = new List<string>(reloaded.AutomationRuleOrder ?? new List<string>());
            LoadRules();
        }
        _dragRuleId = _dropRuleId = null;
        _ruleDragging = false;
        actions.Invalidate();
    }

    private void UpdateStepDropTarget()
    {
        _dropStepIndex = -1;
        _dropStepAfter = false;
        foreach (var sc in _stepCardRects)
        {
            _dropStepIndex = sc.Index;
            _dropStepAfter = _stepDragPointerY >= sc.CardRect.CenterY;
            if (_stepDragPointerY < sc.CardRect.Bottom) break;
        }
    }

    private void CommitOrCancelStepDrag(bool commit)
    {
        if (commit && _dragStepIndex >= 0 && _dropStepIndex >= 0 && _dragStepIndex != _dropStepIndex
            && (uint)_dragStepIndex < (uint)_editingSteps.Count)
        {
            var step = _editingSteps[_dragStepIndex];
            _editingSteps.RemoveAt(_dragStepIndex);
            int target = _dropStepIndex;
            if (_dragStepIndex < target) target--;
            if (_dropStepAfter) target++;
            target = Math.Clamp(target, 0, _editingSteps.Count);
            _editingSteps.Insert(target, step);
        }
        _dragStepIndex = _dropStepIndex = -1;
        _stepDragging = false;
        actions.Invalidate();
    }

    private void CancelDrags()
    {
        _dragRuleId = _dropRuleId = null;
        _ruleDragging = false;
        _dragStepIndex = _dropStepIndex = -1;
        _stepDragging = false;
        _draggingVolStepIndex = -1;
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
    }

    private void OpenRuleInEditor(AutoRule rule, bool isNewDraft, string? templateHint)
    {
        CancelHotkeyCapture();
        _selectedRuleId = rule.Id;
        _editingRule = rule.Clone();
        _editingSteps = _editingRule.CloneSteps();
        if (_editingSteps.Count == 0) _editingSteps.Add(new AutoRuleStep());
        _isNewDraft = isNewDraft;
        _templateHint = templateHint;
        _editorScroll.Offset = 0f;
        actions.Invalidate();
    }

    private void NewRule()
    {
        _historyMode = false;
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
                _historyMode = false;
                var draft = tpl.CreateDraft(L10n.T(tpl.NameKey));
                OpenRuleInEditor(draft, isNewDraft: true, templateHint: L10n.T(tpl.DescriptionKey) + " " + L10n.T("Template.DraftHint"));
            }
        }).ToList();
        actions.ShowMenu(anchor, items);
    }

    private void ImportRuleJson()
    {
        string? path = NativeDialogs.Open(actions.OwnerWindow.Handle, "Automation rule|*.json|All files|*.*");
        if (path == null) return;
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var rule = JsonSerializer.Deserialize(json, AutomationRuleJsonContext.Default.AutoRule);
            if (rule == null) { actions.ShowNotice(L10n.T("Exp.TransferFail")); return; }
            if (string.IsNullOrWhiteSpace(rule.Id) || AutoRuleStore.Find(rule.Id) != null)
                rule.Id = Guid.NewGuid().ToString("N");
            AutoRuleStore.Save(rule, L10n.T("Auto.DefaultNamePrefix"));
            actions.Host.ViewSettingsChanged();
            LoadRules();
            OpenRuleInEditor(rule, false, null);
            actions.ShowNotice(L10n.T("Auto.Saved"));
        }
        catch
        {
            actions.ShowNotice(L10n.T("Exp.TransferFail"));
        }
    }

    private void ShowRuleContextMenu(AutoRule rule)
    {
        bool fav = actions.Config.FavoriteRuleIds.Contains(rule.Id);
        var items = new List<UiMenuItem>
        {
            new() { Text = L10n.T("Auto.Copy"), Icon = UiIcon.Copy, Click = () => DuplicateRule(rule.Id) },
            new() { Text = IsZh() ? "导出 JSON" : "Export JSON", Icon = UiIcon.File, Click = () => ExportRuleJson(rule.Id) },
            new() { Text = L10n.T("Auto.Favorite"), Icon = UiIcon.Pin, Checked = fav, Click = () => ToggleFavorite(rule.Id) },
            UiMenuItem.Separator(),
            new() { Text = L10n.T("Auto.Delete"), Icon = UiIcon.Delete, Danger = true, Click = () => DeleteRule(rule.Id) }
        };
        actions.ShowMenuAtCursor(items);
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
        AutoRuleStore.Save(copy);
        actions.Host.ViewSettingsChanged();
        LoadRules();
        OpenRuleInEditor(copy, false, null);
        actions.ShowNotice(L10n.T("Auto.Copied"));
    }

    private void ExportRuleJson(string id)
    {
        var rule = AutoRuleStore.Find(id);
        if (rule == null) return;
        string? path = NativeDialogs.Save(actions.OwnerWindow.Handle, "Automation rule|*.json|All files|*.*", "json");
        if (path == null) return;
        try
        {
            string json = JsonSerializer.Serialize(rule, AutomationRuleJsonContext.Default.AutoRule);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            actions.ShowNotice(L10n.T("Exp.ExportDone"));
        }
        catch
        {
            actions.ShowNotice(L10n.T("Exp.TransferFail"));
        }
    }

    private void DeleteRule(string id)
    {
        var rule = AutoRuleStore.Find(id);
        string name = rule?.Name ?? id;
        if (!NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("Auto.Delete"),
            IsZh() ? $"确定删除“{name}”？" : $"Delete rule \"{name}\"?"))
            return;
        AutoRuleStore.Delete(id);
        actions.Config.FavoriteRuleIds.Remove(id);
        actions.Config.AutomationRuleOrder.Remove(id);
        ConfigService.TrySave(actions.Config);
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
        AutoRuleStore.Save(rule);
        if (_editingRule?.Id == id) _editingRule.Enabled = rule.Enabled;
        actions.Host.ViewSettingsChanged();
        LoadRules();
        actions.Invalidate();
    }

    private void ToggleFavorite(string id)
    {
        if (actions.Config.FavoriteRuleIds.Contains(id)) actions.Config.FavoriteRuleIds.Remove(id);
        else actions.Config.FavoriteRuleIds.Add(id);
        ConfigService.TrySave(actions.Config);
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
                    if (_historyMode) LoadHistoryAsync();
                    actions.Invalidate();
                }
            });
        });
    }

    private void TestEditorRule()
    {
        if (_editingRule == null) return;
        actions.CloseInlineEdit(commit: true);
        if (!ValidateEditor(out string errMsg)) { actions.ShowNotice(errMsg); return; }
        var draft = _editingRule.Clone();
        draft.SetSteps(_editingSteps);
        _ = Task.Run(async () =>
        {
            var result = await AutoRuleService.ExecuteWithResultAsync(draft, "manual").ConfigureAwait(false);
            string msg = AutoRuleService.DescribeResult(result);
            actions.Host.TryPost(() =>
            {
                if (!_disposed) actions.ShowNotice(msg);
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
        AutoRuleStore.Save(_editingRule, L10n.T("Auto.DefaultNamePrefix"));
        CancelHotkeyCapture();
        _isNewDraft = false;
        _templateHint = null;
        _selectedRuleId = _editingRule.Id;
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
        if (_isNewDraft && _rules.Count > 0)
            OpenRuleInEditor(_rules[0], false, null);
        else if (_editingRule != null && AutoRuleStore.Find(_editingRule.Id) is { } saved)
            OpenRuleInEditor(saved, false, null);
        else
            actions.Invalidate();
    }

    private bool ValidateEditor(out string message)
    {
        message = "";
        if (_editingRule == null) return false;
        if (_editingRule.Trigger == AutoRuleTrigger.Hotkey && string.IsNullOrWhiteSpace(_editingRule.Hotkey))
        { message = L10n.T("Auto.HotkeyRequired"); return false; }
        if (_editingRule.Trigger is AutoRuleTrigger.AppStart or AutoRuleTrigger.AppSwitch or AutoRuleTrigger.AppExit
            && string.IsNullOrWhiteSpace(_editingRule.TriggerApp))
        { message = L10n.T("Auto.TriggerAppRequired"); return false; }
        if (_editingRule.Trigger == AutoRuleTrigger.Schedule && _editingRule.ScheduleMode == 2 && _editingRule.ScheduleWeekdays.Count == 0)
        { message = L10n.T("Auto.ScheduleWeekdayRequired"); return false; }

        for (int i = 0; i < _editingSteps.Count; i++)
        {
            var s = _editingSteps[i];
            string? key = null;
            if (s.Action == AutoRuleAction.ExecuteRule && (string.IsNullOrWhiteSpace(s.TargetRuleId) || s.TargetRuleId == _editingRule.Id))
                key = "Auto.RuleRequired";
            else if (AutoRuleActions.NeedsApp(s.Action) && string.IsNullOrWhiteSpace(s.TargetApp))
                key = "Auto.ActionAppRequired";
            else if (AutoRuleActions.NeedsDevice(s.Action) && (string.IsNullOrWhiteSpace(s.TargetDeviceId)
                || (s.Action is not (AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput) && AudioService.IsSystemDefault(s.TargetDeviceId))))
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

    private void ToggleHistoryMode()
    {
        _historyMode = !_historyMode;
        if (_historyMode) LoadHistoryAsync();
        actions.Invalidate();
    }

    private void LoadHistoryAsync()
    {
        var store = AutoRuleService.GetHistoryStore();
        if (store == null)
        {
            _historyEntries.Clear();
            _historyLoading = false;
            actions.Invalidate();
            return;
        }
        _historyLoading = true;
        _ = Task.Run(async () =>
        {
            List<ExecutionHistoryEntry> entries;
            try
            {
                await store.WhenIdleAsync().ConfigureAwait(false);
                entries = await store.ReadRecentAsync().ConfigureAwait(false);
            }
            catch { entries = new(); }
            actions.Host.TryPost(() =>
            {
                _historyLoading = false;
                if (_disposed) return;
                _historyEntries = entries;
                actions.Invalidate();
            });
        });
    }

    private void ClearHistoryAsync()
    {
        var store = AutoRuleService.GetHistoryStore();
        if (store == null) return;
        _ = Task.Run(async () =>
        {
            bool ok = await store.ClearAsync().ConfigureAwait(false);
            actions.Host.TryPost(() =>
            {
                if (_disposed) return;
                _historyEntries.Clear();
                actions.ShowNotice(L10n.T(ok ? "History.Cleared" : "History.ReadError"));
                actions.Invalidate();
            });
        });
    }

    private void OpenListOptionsMenu(UiRect anchor)
    {
        var items = new List<UiMenuItem>
        {
            new() { Text = L10n.T("Auto.ResetFilters"), Click = () => { _searchText = ""; actions.Invalidate(); } },
            new() { Text = L10n.T("Auto.ResetRuleOrder"), Enabled = actions.Config.AutomationRuleOrder.Count > 0, Click = () =>
            {
                actions.Config.AutomationRuleOrder.Clear();
                ConfigService.TrySave(actions.Config);
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

    private void OpenAppPickerCombo(UiRect anchor, string current, Action<string> onPick)
    {
        var choices = new List<ComboChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _apps)
        {
            string proc = app.ProcessName ?? "";
            if (string.IsNullOrWhiteSpace(proc) || !seen.Add(proc)) continue;
            string alias = actions.Config.AppNames.TryGetValue(proc, out var a) && !string.IsNullOrWhiteSpace(a) ? a : app.Label;
            choices.Add(new(proc, $"{alias} ({proc})"));
        }
        if (choices.Count == 0) choices.Add(new("", IsZh() ? "暂无运行中音频应用" : "No active audio apps"));
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

    private void OpenStepDeviceCombo(UiRect anchor, AutoRuleStep step)
    {
        bool capture = AutoRuleActions.UsesCaptureDevice(step.Action);
        var list = new List<ComboChoice>();
        if (step.Action is AutoRuleAction.SetAppOutput or AutoRuleAction.SetAppInput)
        {
            string sysId = capture ? AudioService.SystemDefaultInputDeviceId : AudioService.SystemDefaultDeviceId;
            list.Add(new ComboChoice(sysId, L10n.T(capture ? "Dev.SystemDefaultIn" : "Dev.SystemDefaultOut")));
        }
        var endpoints = capture ? _inputs : _outputs;
        foreach (var ep in endpoints)
        {
            string name = actions.Config.DeviceNames.TryGetValue(ep.Id, out var alias) && !string.IsNullOrWhiteSpace(alias) ? alias : ep.Name;
            list.Add(new ComboChoice(ep.Id, name));
        }
        if (list.Count == 0) list.Add(new ComboChoice("", L10n.T("Ov.NoDevice")));
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
        int pct = UiPrimitives.SliderValueFromX(sliderRect, mouseX);
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
        if (actions.Host.HotkeyStatuses.TryGetValue(AutoRuleService.HotkeyPrefix + rule.Id, out string? status)
            && string.Equals(status, "", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var kv in actions.Config.Hotkeys)
        {
            if (!actions.Config.ExperimentalMic && kv.Key is HotkeyActions.ActSwitchInput or HotkeyActions.ActSwitchAllInput)
                continue;
            if (!string.IsNullOrWhiteSpace(kv.Value) && string.Equals(kv.Value.Trim(), hk, StringComparison.OrdinalIgnoreCase))
                return true;
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

    private static string AutoActionKey(AutoRuleAction action) => action switch
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

    private static string? KeyName(int vk)
    {
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
            _ => null
        };
    }

    private static UiRect ClipToViewport(UiRect r, UiRect vp)
    {
        float l = MathF.Max(r.X, vp.X);
        float t = MathF.Max(r.Y, vp.Y);
        float right = MathF.Min(r.Right, vp.Right);
        float b = MathF.Min(r.Bottom, vp.Bottom);
        return new UiRect(l, t, MathF.Max(0f, right - l), MathF.Max(0f, b - t));
    }

    private static bool IsZh() => L10n.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
}
