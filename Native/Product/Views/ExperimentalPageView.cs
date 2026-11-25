using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class ExperimentalPageView(IViewHostActions actions) : IViewPage
{
    private readonly UiScrollState _scroll = new();
    private readonly Dictionary<string, UiRect> _rects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);
    private UiRect _contentRect;
    private bool _resettingRoutes;
    private bool _disposed;

    public void OnActivated() { }

    public void OnDeactivated()
    {
        actions.CloseInlineEdit(commit: true);
    }

    public void RefreshData() => actions.Invalidate();

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _contentRect = contentRect;
        _rects.Clear();
        _clicks.Clear();

        var cfg = actions.Config;
        var viewport = contentRect;
        _scroll.ViewportHeight = viewport.Height;

        float padR = _scroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth + 6f : 0f;
        float x = viewport.X;
        float w = MathF.Max(240f, viewport.Width - padR);
        float y = viewport.Y - _scroll.Offset;
        float startY = y;

        void Register(string id, UiRect r, Action onClick, UiCursorKind cursor = UiCursorKind.Hand, string? tip = null)
        {
            _rects[id] = r;
            _clicks[id] = onClick;
            if (r.Bottom > viewport.Y && r.Y < viewport.Bottom)
            {
                var clipped = ClipToViewport(r, viewport);
                if (!clipped.IsEmpty) hit.Add(id, clipped, cursor, tip);
            }
        }

        g.PushClip(viewport);

        // Header + Beta badge
        g.DrawText(L10n.T("Nav.Experimental"), new UiRect(x, y, w - 120f, 30f), t.TextPrimary, 22f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        string badgeText = IsZh() ? "BETA · 实验功能" : "BETA";
        UiPrimitives.DrawBadge(g, t, new UiRect(x + w - 116f, y + 4f, 116f, 22f), badgeText,
            t.Fail.WithAlphaFraction(0.16f), t.Fail, t.Fail.WithAlphaFraction(0.4f), 10.5f);
        y += 34f;
        g.DrawText(IsZh() ? "实验功能可能需要重启，仅在你明确开启时生效。" : "Experimental features may require restart and stay off until enabled.",
            new UiRect(x, y, w, 20f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        y += 32f;

        // 1. Experimental Master Toggle Card
        float pad = 16f;
        float innerW = w - pad * 2f;
        float masterH = pad + 56f + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, masterH));
        DrawSwitchRow(g, t, hit, "exp.mode", new UiRect(x + pad, y + pad, innerW, 56f),
            UiIcon.Experiment, L10n.T("St.ExperimentalMode"), L10n.T("St.ExperimentalModeHint"),
            cfg.ExperimentalMode, false, () =>
            {
                if (!cfg.ExperimentalMode && !NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("St.ExperimentalMode"),
                    IsZh() ? "实验功能可能不稳定，确定启用？" : "Experimental features may be unstable. Enable them?"))
                    return;
                cfg.ExperimentalUnlocked = true;
                cfg.ExperimentalMode = !cfg.ExperimentalMode;
                if (!cfg.ExperimentalMode) AutoRuleService.StopExecutionHistory();
                SaveConfig();
                actions.ShowNotice(L10n.T("St.ExpModeNeedRestart"));
            }, Register);
        y += masterH + 14f;

        // 2. Experimental Mic Routing Card
        float micH = pad + 56f + (cfg.ExperimentalMic ? 52f : 0f) + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, micH));
        float my = y + pad;
        DrawSwitchRow(g, t, hit, "exp.mic", new UiRect(x + pad, my, innerW, 56f),
            UiIcon.Microphone, L10n.T("St.ExperimentalMic"), L10n.T("St.ExperimentalMicHint"),
            cfg.ExperimentalMic, cfg.ExperimentalMic, () =>
            {
                cfg.ExperimentalMic = !cfg.ExperimentalMic;
                SaveConfig();
                actions.ShowNotice(L10n.T("St.ExpMicNeedRestart"));
            }, Register);
        if (cfg.ExperimentalMic)
        {
            my += 56f;
            DrawSwitchRow(g, t, hit, "exp.micpanel", new UiRect(x + pad + 20f, my, innerW - 20f, 52f),
                UiIcon.Microphone, L10n.T("Exp.MicPanel"), "",
                cfg.MicInPanel, false, () =>
                {
                    cfg.MicInPanel = !cfg.MicInPanel;
                    SaveConfig();
                    actions.ShowNotice(L10n.T("St.ExpMicPanelNeedRestart"));
                }, Register);
        }
        y += micH + 14f;

        // 3. Background Polling Fallback Card
        float pollH = pad + 64f + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, pollH));
        DrawSwitchRow(g, t, hit, "exp.polling", new UiRect(x + pad, y + pad, innerW, 64f),
            UiIcon.Options, L10n.T("Exp.BackgroundPolling"), L10n.T("Exp.BackgroundPollingHint"),
            cfg.BackgroundPollingFallbackEnabled, false, () =>
            {
                cfg.BackgroundPollingFallbackEnabled = !cfg.BackgroundPollingFallbackEnabled;
                SaveConfig();
            }, Register);
        y += pollH + 14f;

        // 4. Execution History Card
        bool histActive = cfg.ExperimentalMode && cfg.ExecutionHistoryEnabled;
        float histH = pad + 60f + (histActive ? 50f : 0f) + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, histH));
        float hy = y + pad;
        DrawSwitchRow(g, t, hit, "exp.history", new UiRect(x + pad, hy, innerW, 60f),
            UiIcon.History, L10n.T("History.Title"), L10n.T("History.Hint"),
            cfg.ExecutionHistoryEnabled, histActive, () =>
            {
                if (!cfg.ExecutionHistoryEnabled && !NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("History.Title"),
                    IsZh() ? "启用后会记录自动化规则的执行明细。确定启用？" : "Execution details for automation rules will be recorded. Enable?"))
                    return;
                cfg.ExecutionHistoryEnabled = !cfg.ExecutionHistoryEnabled;
                if (!cfg.ExecutionHistoryEnabled) AutoRuleService.StopExecutionHistory();
                SaveConfig();
            }, Register);
        if (histActive)
        {
            hy += 66f;
            g.DrawText(L10n.T("History.Limit"), new UiRect(x + pad + 32f, hy, 140f, 32f), t.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var limRect = new UiRect(x + pad + 178f, hy, 70f, 32f);
            UiPrimitives.DrawTextBox(g, t, hit, "exp.hist.limit", limRect,
                Math.Clamp(cfg.ExecutionHistoryLimitMb, 1, 64).ToString(CultureInfo.InvariantCulture) + " MB",
                focused: actions.ActiveInlineEditTag == "exp.hist.limit");
            Register("exp.hist.limit", limRect, () =>
            {
                actions.StartInlineEdit("exp.hist.limit", limRect,
                    Math.Clamp(cfg.ExecutionHistoryLimitMb, 1, 64).ToString(CultureInfo.InvariantCulture), null, (txt, commit) =>
                    {
                        if (!commit) return;
                        if (int.TryParse(txt.Trim().Replace("MB", "", StringComparison.OrdinalIgnoreCase).Trim(), out int v) && v >= 1 && v <= 64)
                        {
                            cfg.ExecutionHistoryLimitMb = v;
                            SaveConfig();
                            _ = AutoRuleService.GetHistoryStore()?.SetLimitAsync(v);
                        }
                        else actions.ShowNotice(L10n.T("History.InvalidLimit"));
                    });
            }, UiCursorKind.IBeam);

            var clrRect = new UiRect(x + w - pad - 120f, hy, 120f, 32f);
            UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.hist.clear", clrRect, L10n.T("History.Clear"), danger: true, fontSize: 12f);
            Register("exp.hist.clear", clrRect, () =>
            {
                var store = AutoRuleService.GetHistoryStore();
                if (store == null) return;
                _ = Task.Run(async () =>
                {
                    bool ok = await store.ClearAsync().ConfigureAwait(false);
                    actions.Host.TryPost(() =>
                    {
                        if (!_disposed) actions.ShowNotice(L10n.T(ok ? "History.Cleared" : "History.ReadError"));
                    });
                });
            });
        }
        y += histH + 14f;

        // 5. Multi-output & Routing Helpers Card
        float routeH = pad + 28f + 36f + 40f + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, routeH));
        float ry = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, ry, innerW, 28f), UiIcon.Reset,
            IsZh() ? "多输出与路由重置" : "Multi-Output & Route Helpers");
        ry += 32f;
        g.DrawText(L10n.T("Auto.ResetAllRoutingHint"), new UiRect(x + pad + 32f, ry, innerW - 32f, 32f),
            t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);
        ry += 38f;
        var rstRoutesRect = new UiRect(x + pad + 32f, ry, 240f, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.resetroutes", rstRoutesRect, L10n.T("Act.ResetAllApps"),
            enabled: !_resettingRoutes, icon: UiIcon.Reset, fontSize: 12f);
        if (!_resettingRoutes)
            Register("exp.resetroutes", rstRoutesRect, ResetAllAppRoutes);
        y += routeH + 14f;

        // 6. Preview Host Controls & Restart Card
        float prevH = pad + 28f + 56f + 46f + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, prevH));
        float py = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, py, innerW, 28f), UiIcon.Application,
            IsZh() ? "Native AOT 预览宿主控制" : "Native AOT Preview Controls");
        py += 32f;

        bool globalCtrl = actions.Host.PreviewGlobalControls;
        DrawSwitchRow(g, t, hit, "exp.globalctrl", new UiRect(x + pad, py, innerW, 52f),
            UiIcon.Keyboard,
            IsZh() ? "预览版全局快捷键与托盘滚轮" : "Preview global shortcuts and tray wheel",
            IsZh() ? "启用后将与正式版共享系统快捷键和托盘滚轮。" : "Competes with any running installed edition for global hotkeys and tray wheel.",
            globalCtrl, false, () =>
            {
                if (!globalCtrl && !NativeDialogs.Confirm(actions.OwnerWindow.Handle, "Native AOT Preview",
                    IsZh() ? "启用后，测试版可能与正式版争用相同的全局快捷键和托盘滚轮。继续？"
                           : "The preview and installed version may compete for the same global shortcuts and tray wheel. Enable?"))
                    return;
                actions.Host.SetPreviewGlobalControls(!globalCtrl);
                actions.Invalidate();
            }, Register);
        py += 58f;

        var restartRect = new UiRect(x + pad + 32f, py, 168f, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.restart", restartRect,
            IsZh() ? "重启预览版" : "Restart Preview", icon: UiIcon.Refresh, fontSize: 12.5f);
        Register("exp.restart", restartRect, () =>
        {
            if (NativeDialogs.Confirm(actions.OwnerWindow.Handle, "SonicRoute",
                IsZh() ? "立即重启 Native AOT 预览版？" : "Restart Native AOT Preview now?"))
            {
                actions.Host.RestartPreview();
            }
        });
        y += prevH + 16f;

        g.PopClip();

        _scroll.ContentHeight = MathF.Max(0f, y - startY);
        _scroll.Clamp();
        _scroll.Draw(g, t, hit, "exp.scroll", viewport);
    }

    private static void DrawSwitchRow(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        string id, UiRect rect, UiIcon icon, string title, string hint, bool isChecked, bool drawBottomBorder,
        Action onClick, Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        register(id, rect, onClick, UiCursorKind.Hand, string.IsNullOrEmpty(hint) ? null : hint);
        if (hit.IsHover(id))
            g.FillRoundedRect(rect.Deflate(0f, 1f), 6f, t.NavHover.WithAlphaFraction(0.55f));

        g.DrawIcon(icon, new UiRect(rect.X + 2f, rect.CenterY - 8.5f, 17f, 17f), t.TextSecondary, 1.8f);
        float textW = MathF.Max(60f, rect.Width - 82f);
        if (string.IsNullOrEmpty(hint))
        {
            g.DrawText(title, new UiRect(rect.X + 32f, rect.Y, textW, rect.Height), t.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }
        else
        {
            g.DrawText(title, new UiRect(rect.X + 32f, rect.Y + 4f, textW, 18f), t.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            g.DrawText(hint, new UiRect(rect.X + 32f, rect.Y + 24f, textW, rect.Height - 26f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: true);
        }
        UiPrimitives.DrawSwitch(g, t, hit, id + ":sw", new UiRect(rect.Right - 36f, rect.CenterY - 12f, 34f, 24f), isChecked);
        if (drawBottomBorder)
            g.DrawLine(rect.X + 2f, rect.Bottom, rect.Right - 2f, rect.Bottom, t.Border, 1f, roundCaps: false);
    }

    private void ResetAllAppRoutes()
    {
        if (_resettingRoutes) return;
        _resettingRoutes = true;
        actions.Invalidate();
        _ = Task.Run(() =>
        {
            bool ok = false;
            try { ok = actions.Host.Audio.ResetRoutes(); }
            catch { }
            actions.Host.TryPost(() =>
            {
                _resettingRoutes = false;
                if (_disposed) return;
                actions.ShowNotice(L10n.T(ok ? "Act.ResetAllAppsDone" : "Act.ResetAllAppsFailed"));
                actions.Invalidate();
            });
        });
    }

    public bool OnPointerMove(float x, float y, bool leftDown) => _scroll.DragMove(y);

    public bool OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return false;
        return _scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y);
    }

    public bool OnPointerUp(UiPointerEventArgs e)
    {
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            return true;
        }
        if (e.Button != UiMouseButton.Left || !_contentRect.Contains(e.X, e.Y)) return false;
        foreach (var kv in _rects)
        {
            if (kv.Value.Contains(e.X, e.Y) && _clicks.TryGetValue(kv.Key, out var click))
            {
                click();
                return true;
            }
        }
        return false;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (horizontal || !_contentRect.Contains(x, y)) return false;
        _scroll.OnWheel(delta);
        return true;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt) => false;

    public void Dispose() => _disposed = true;

    private void SaveConfig()
    {
        if (!ConfigService.TrySave(actions.Config))
            actions.ShowNotice(L10n.T("Tray.SaveFailed"));
        actions.NotifySettingsChanged();
        actions.Host.ViewSettingsChanged();
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
