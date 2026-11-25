using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

internal sealed class ExperimentalPageView(IViewHostActions actions) : IViewPage
{
    private static readonly (string Code, string Key)[] OsdPositionChoices =
    [
        ("TL", "Theme.PosTL"),
        ("TC", "Theme.PosTC"),
        ("TR", "Theme.PosTR"),
        ("ML", "Theme.PosML"),
        ("MC", "Theme.PosMC"),
        ("MR", "Theme.PosMR"),
        ("BL", "Theme.PosBL"),
        ("BC", "Theme.PosBC"),
        ("BR", "Theme.PosBR"),
        ("Custom", "Exp.Osd.Custom"),
    ];

    private readonly UiScrollState _scroll = new();
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);
    private UiRect _contentRect;
    private bool _resettingRoutes;
    private bool _configBusy;
    private bool _disposed;

    public void OnActivated() { }

    public void OnDeactivated()
    {
        _scroll.EndDrag();
        actions.CloseInlineEdit(commit: true);
    }

    public void OnCaptureLost() => _scroll.EndDrag();

    public void RefreshData() => actions.Invalidate();

    public void OnFilesDropped(string[] paths)
    {
        if (paths == null || paths.Length == 0 || _configBusy) return;
        string? jsonPath = paths.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p) &&
            p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(p));
        if (jsonPath != null)
        {
            ImportConfigFromPath(jsonPath);
        }
    }

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _contentRect = contentRect;
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
            _clicks[id] = onClick;
            if (r.Bottom > viewport.Y && r.Y < viewport.Bottom)
            {
                var clipped = ClipToViewport(r, viewport);
                if (!clipped.IsEmpty) hit.Add(id, clipped, cursor, tip);
            }
        }

        g.PushClip(viewport);
        hit.PushClip(viewport);

        // Header + Beta badge
        g.DrawText(L10n.T("Nav.Experimental"), new UiRect(x, y, w - 120f, 30f), t.TextPrimary, 22f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        string badgeText = IsZh() ? "BETA · 实验功能" : "BETA";
        UiPrimitives.DrawBadge(g, t, new UiRect(x + w - 116f, y + 4f, 116f, 22f), badgeText,
            t.Fail.WithAlphaFraction(0.16f), t.Fail, t.Fail.WithAlphaFraction(0.4f), 10.5f);
        y += 34f;
        g.DrawText(IsZh() ? "实验功能可能需要重启，仅在你明确开启时生效。" : "Experimental features may require restart and stay off until enabled.",
            new UiRect(x, y, w, 20f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        y += 32f;

        float pad = 16f;
        float innerW = w - pad * 2f;

        // 1. Experimental Master Toggle Card
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

        // 2. Background Polling Fallback Card (WPF 1.36 ExpBackgroundPollingCheck)
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

        // 3. Execution History Card (WPF 1.36 ExpHistoryCheck)
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

        // 4. Mic & Panel Quick Behavior Card
        float micH = pad + 56f + (cfg.ExperimentalMic ? 52f : 0f) + 56f + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, micH));
        float my = y + pad;
        DrawSwitchRow(g, t, hit, "exp.mic", new UiRect(x + pad, my, innerW, 56f),
            UiIcon.Microphone, L10n.T("St.ExperimentalMic"), L10n.T("St.ExperimentalMicHint"),
            cfg.ExperimentalMic, true, () =>
            {
                cfg.ExperimentalMic = !cfg.ExperimentalMic;
                SaveConfig();
                actions.ShowNotice(L10n.T("St.ExpMicNeedRestart"));
            }, Register);
        my += 56f;
        if (cfg.ExperimentalMic)
        {
            DrawSwitchRow(g, t, hit, "exp.micpanel", new UiRect(x + pad + 20f, my, innerW - 20f, 52f),
                UiIcon.Microphone, L10n.T("Exp.MicPanel"), "",
                cfg.MicInPanel, true, () =>
                {
                    cfg.MicInPanel = !cfg.MicInPanel;
                    SaveConfig();
                    actions.ShowNotice(L10n.T("St.ExpMicPanelNeedRestart"));
                }, Register);
            my += 52f;
        }
        DrawSwitchRow(g, t, hit, "exp.panelretained", new UiRect(x + pad, my, innerW, 56f),
            UiIcon.Speaker, L10n.T("St.PanelOutputVolumesRetainedOnly"), L10n.T("St.PanelOutputVolumesRetainedOnlyHint"),
            cfg.PanelOutputVolumesRetainedOnly, false, () =>
            {
                cfg.PanelOutputVolumesRetainedOnly = !cfg.PanelOutputVolumesRetainedOnly;
                SaveConfig();
            }, Register);
        y += micH + 14f;

        // 5. OSD Preview & Quick Parameters Card
        float osdH = pad + 32f + 56f + 44f + 44f + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, osdH));
        float oy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, oy, innerW - 230f, 28f), UiIcon.Monitor, L10n.T("Exp.Osd"));
        var prevBtn = new UiRect(x + w - pad - 224f, oy, 108f, 30f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.osd.preview", prevBtn, L10n.T("Exp.OsdPreview"), icon: UiIcon.Play, fontSize: 12f);
        Register("exp.osd.preview", prevBtn, () => actions.Host.PreviewOsd(), UiCursorKind.Hand, L10n.T("Exp.OsdPreview"));

        var themeBtn = new UiRect(prevBtn.Right + 8f, oy, 108f, 30f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.osd.theme", themeBtn, L10n.T("Nav.Theme"), icon: UiIcon.Theme, fontSize: 12f);
        Register("exp.osd.theme", themeBtn, () => actions.NavigateTo(MainNavTab.Theme), UiCursorKind.Hand, L10n.T("Nav.Theme"));
        oy += 36f;

        DrawSwitchRow(g, t, hit, "exp.osd.volvisual", new UiRect(x + pad, oy, innerW, 52f),
            UiIcon.Speaker, L10n.T("Theme.OsdVolumeVisual"), L10n.T("Theme.OsdVolumeVisualHint"),
            cfg.OsdVolumeVisualEnabled, true, () =>
            {
                cfg.OsdVolumeVisualEnabled = !cfg.OsdVolumeVisualEnabled;
                SaveConfig();
            }, Register);
        oy += 58f;

        // Row 1: Position + Width + Font Scale
        float thirdW = (innerW - 20f) / 3f;
        g.DrawText(L10n.T("Exp.Osd"), new UiRect(x + pad, oy, thirdW, 16f), t.TextSecondary, 11f, 400);
        var posCombo = new UiRect(x + pad, oy + 18f, thirdW, 28f);
        string posLabel = GetOsdPositionLabel(cfg.OsdPosition);
        UiPrimitives.DrawComboBox(g, t, hit, "exp.osd.pos", posCombo, posLabel);
        Register("exp.osd.pos", posCombo, () => OpenOsdPositionCombo(posCombo), UiCursorKind.Hand, L10n.T("Exp.OsdHint"));

        float col2X = posCombo.Right + 10f;
        g.DrawText(L10n.T("Exp.OsdWidthLabel"), new UiRect(col2X, oy, thirdW, 16f), t.TextSecondary, 11f, 400);
        var wRect = new UiRect(col2X, oy + 18f, thirdW, 28f);
        UiPrimitives.DrawTextBox(g, t, hit, "exp.osd.w", wRect, $"{Math.Clamp(cfg.OsdWidth, 180, 600)} px",
            focused: actions.ActiveInlineEditTag == "exp.osd.w");
        Register("exp.osd.w", wRect, () =>
        {
            actions.StartInlineEdit("exp.osd.w", wRect, cfg.OsdWidth.ToString(CultureInfo.InvariantCulture), null, (txt, commit) =>
            {
                if (!commit) return;
                if (int.TryParse(txt.Trim().Replace("px", "", StringComparison.OrdinalIgnoreCase).Trim(), out int val))
                {
                    cfg.OsdWidth = Math.Clamp(val, 180, 600);
                    SaveConfig();
                    actions.Host.PreviewOsd();
                }
            }, mono: true);
        }, UiCursorKind.IBeam, null);

        float col3X = wRect.Right + 10f;
        double fontScale = Math.Clamp(cfg.OsdFontScale, 0.7, 1.6);
        g.DrawText(L10n.T("Exp.OsdFontLabel"), new UiRect(col3X, oy, thirdW, 16f), t.TextSecondary, 11f, 400);
        var fsRect = new UiRect(col3X, oy + 18f, thirdW, 28f);
        UiPrimitives.DrawTextBox(g, t, hit, "exp.osd.font", fsRect, $"{(int)Math.Round(fontScale * 100)}%",
            focused: actions.ActiveInlineEditTag == "exp.osd.font");
        Register("exp.osd.font", fsRect, () =>
        {
            actions.StartInlineEdit("exp.osd.font", fsRect, fontScale.ToString("0.00", CultureInfo.InvariantCulture), null, (txt, commit) =>
            {
                if (!commit) return;
                string cleaned = txt.Trim();
                if (cleaned.EndsWith('%') && double.TryParse(cleaned.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    cfg.OsdFontScale = Math.Clamp(Math.Round(pct / 100.0, 2), 0.7, 1.6);
                    SaveConfig();
                    actions.Host.PreviewOsd();
                }
                else if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                {
                    cfg.OsdFontScale = Math.Clamp(Math.Round(val, 2), 0.7, 1.6);
                    SaveConfig();
                    actions.Host.PreviewOsd();
                }
            }, mono: true);
        }, UiCursorKind.IBeam, null);
        oy += 52f;

        // Row 2: Fade-in + Fade-out
        float halfW = (innerW - 12f) / 2f;
        g.DrawText(L10n.T("Exp.OsdFadeInLabel"), new UiRect(x + pad, oy, halfW, 16f), t.TextSecondary, 11f, 400);
        var finRect = new UiRect(x + pad, oy + 18f, halfW, 28f);
        UiPrimitives.DrawTextBox(g, t, hit, "exp.osd.fin", finRect, $"{Math.Clamp(cfg.OsdFadeInMs, 0, 500)} ms",
            focused: actions.ActiveInlineEditTag == "exp.osd.fin");
        Register("exp.osd.fin", finRect, () =>
        {
            actions.StartInlineEdit("exp.osd.fin", finRect, cfg.OsdFadeInMs.ToString(CultureInfo.InvariantCulture), null, (txt, commit) =>
            {
                if (!commit) return;
                if (int.TryParse(txt.Trim().Replace("ms", "", StringComparison.OrdinalIgnoreCase).Trim(), out int val))
                {
                    cfg.OsdFadeInMs = Math.Clamp(val, 0, 500);
                    SaveConfig();
                }
            }, mono: true);
        }, UiCursorKind.IBeam, null);

        float foutX = finRect.Right + 12f;
        g.DrawText(L10n.T("Exp.OsdFadeOutLabel"), new UiRect(foutX, oy, halfW, 16f), t.TextSecondary, 11f, 400);
        var foutRect = new UiRect(foutX, oy + 18f, halfW, 28f);
        UiPrimitives.DrawTextBox(g, t, hit, "exp.osd.fout", foutRect, $"{Math.Clamp(cfg.OsdFadeOutMs, 0, 1000)} ms",
            focused: actions.ActiveInlineEditTag == "exp.osd.fout");
        Register("exp.osd.fout", foutRect, () =>
        {
            actions.StartInlineEdit("exp.osd.fout", foutRect, cfg.OsdFadeOutMs.ToString(CultureInfo.InvariantCulture), null, (txt, commit) =>
            {
                if (!commit) return;
                if (int.TryParse(txt.Trim().Replace("ms", "", StringComparison.OrdinalIgnoreCase).Trim(), out int val))
                {
                    cfg.OsdFadeOutMs = Math.Clamp(val, 0, 1000);
                    SaveConfig();
                }
            }, mono: true);
        }, UiCursorKind.IBeam, null);

        y += osdH + 14f;

        // 6. Multi-output & Routing Helpers Card
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

        // 7. Config Files Management Card (WPF 1.36 ExpConfigFiles)
        float cfgH = pad + 28f + 38f + 40f + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, cfgH));
        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, 28f), UiIcon.File, L10n.T("Exp.ConfigFiles"));
        cy += 34f;
        g.DrawText(L10n.T("Exp.ConfigTransferHint"), new UiRect(x + pad + 32f, cy, innerW - 32f, 22f),
            t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        cy += 30f;

        float btnW = MathF.Min(164f, (innerW - 48f) / 3f);
        var expBtn = new UiRect(x + pad + 32f, cy, btnW, 34f);
        var impBtn = new UiRect(expBtn.Right + 8f, cy, btnW, 34f);
        var rstBtn = new UiRect(impBtn.Right + 16f, cy, btnW, 34f);

        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.cfg.export", expBtn, L10n.T("Exp.ExportConfig"), enabled: !_configBusy, fontSize: 12f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.cfg.import", impBtn, L10n.T("Exp.ImportConfig"), enabled: !_configBusy, fontSize: 12f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.cfg.clear", rstBtn, L10n.T("Exp.ClearConfig"), enabled: !_configBusy, danger: true, fontSize: 12f);
        if (!_configBusy)
        {
            Register("exp.cfg.export", expBtn, ExportConfig, UiCursorKind.Hand, L10n.T("Exp.ExportConfig"));
            Register("exp.cfg.import", impBtn, ImportConfig, UiCursorKind.Hand, L10n.T("Exp.ImportConfig"));
            Register("exp.cfg.clear", rstBtn, ResetConfig, UiCursorKind.Hand, L10n.T("Exp.ClearConfigHint"));
        }

        y += cfgH + 16f;

        hit.PopClip();
        g.PopClip();

        _scroll.ContentHeight = MathF.Max(0f, y - startY);
        _scroll.Clamp();
        _scroll.Draw(g, t, hit, "exp.scroll", viewport);
    }

    private void OpenOsdPositionCombo(UiRect anchor)
    {
        var choices = new List<ComboChoice>(OsdPositionChoices.Length);
        foreach (var (code, key) in OsdPositionChoices)
        {
            choices.Add(new ComboChoice(code, L10n.T(key)));
        }
        string cur = string.IsNullOrWhiteSpace(actions.Config.OsdPosition) ? "TR" : actions.Config.OsdPosition;
        actions.ShowCombo(anchor, choices, cur, picked =>
        {
            actions.Config.OsdPosition = picked.Key;
            SaveConfig();
            actions.Host.PreviewOsd();
        });
    }

    private static string GetOsdPositionLabel(string? code)
    {
        string normalized = string.IsNullOrWhiteSpace(code) ? "TR" : code.Trim();
        foreach (var (c, key) in OsdPositionChoices)
        {
            if (string.Equals(c, normalized, StringComparison.OrdinalIgnoreCase))
                return L10n.T(key);
        }
        return normalized;
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
        UiPrimitives.DrawSwitch(g, t, hit, id, new UiRect(rect.Right - 36f, rect.CenterY - 12f, 34f, 24f), isChecked);
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

    private void ExportConfig()
    {
        string? path = NativeDialogs.Save(actions.OwnerWindow.Handle, "JSON (*.json)|*.json|All files|*.*", "json", L10n.T("Exp.ExportConfig"));
        if (path == null) return;
        _configBusy = true;
        actions.Invalidate();
        string snapshot = ConfigService.CaptureExportSnapshot();
        _ = Task.Run(() =>
        {
            bool ok = ConfigService.ExportTo(path, snapshot);
            actions.Host.TryPost(() =>
            {
                _configBusy = false;
                if (_disposed) return;
                actions.ShowNotice(L10n.T(ok ? "Exp.ExportDone" : "Exp.TransferFail"));
                actions.Invalidate();
            });
        });
    }

    private void ImportConfig()
    {
        string? path = NativeDialogs.Open(actions.OwnerWindow.Handle, "JSON (*.json)|*.json|All files|*.*", L10n.T("Exp.ImportConfig"));
        if (path != null) ImportConfigFromPath(path);
    }

    private void ImportConfigFromPath(string path)
    {
        if (_configBusy) return;
        _configBusy = true;
        actions.Invalidate();
        _ = Task.Run(() =>
        {
            byte[]? prepared = null;
            AppConfig? preview = null;
            int missingDevices = 0;
            int missingApps = 0;
            try
            {
                prepared = ConfigService.PrepareImport(path);
                if (prepared != null)
                {
                    preview = ConfigService.ReadPreparedConfig(prepared);
                    if (preview != null)
                    {
                        var devIds = new HashSet<string>(
                            ProductAudio.Devices(Flow.Render).Concat(ProductAudio.Devices(Flow.Capture)).Select(d => d.Id),
                            StringComparer.OrdinalIgnoreCase);
                        missingDevices = (preview.DeviceNames?.Keys ?? Enumerable.Empty<string>())
                            .Concat(preview.HiddenOutputDevices ?? [])
                            .Concat(preview.HiddenInputDevices ?? [])
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(id => !AudioService.IsSystemDefault(id) && !devIds.Contains(id));

                        var appNames = new HashSet<string>(
                            actions.Host.Audio.Apps().Select(a => a.ProcessName ?? ""),
                            StringComparer.OrdinalIgnoreCase);
                        missingApps = (preview.AppNames?.Keys ?? Enumerable.Empty<string>())
                            .Concat(preview.AppIcons?.Keys ?? Enumerable.Empty<string>())
                            .Concat(preview.HiddenPanelApps ?? [])
                            .Concat(preview.DisabledAutoSwitchApps ?? [])
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(n => !appNames.Contains(n));
                    }
                }
            }
            catch { }

            actions.Host.TryPost(() =>
            {
                _configBusy = false;
                if (_disposed) return;
                if (prepared == null || preview == null)
                {
                    actions.ShowNotice(L10n.T("Exp.TransferFail"));
                    actions.Invalidate();
                    return;
                }

                string msg = string.Format(L10n.T("Exp.ImportPreview"),
                    preview.Hotkeys?.Count ?? 0,
                    preview.DeviceNames?.Count ?? 0,
                    preview.AppNames?.Count ?? 0,
                    missingDevices,
                    missingApps);
                if (!NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("Exp.ImportConfig"), msg))
                {
                    actions.Invalidate();
                    return;
                }

                ConfigService.FlushPendingSaves();
                if (!ConfigService.ApplyImport(prepared))
                {
                    actions.ShowNotice(L10n.T("Exp.TransferFail"));
                    actions.Invalidate();
                    return;
                }
                actions.ShowNotice(L10n.T("Exp.ImportDone"));
                actions.Host.RestartPreview();
            });
        });
    }

    private void ResetConfig()
    {
        if (!NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("Exp.ClearConfig"), L10n.T("Exp.ClearConfigHint")))
            return;
        ConfigService.ResetToDefault();
        actions.Host.RestartPreview();
    }

    public bool OnPointerMove(float x, float y, bool leftDown) => leftDown && _scroll.DragMove(y);

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        return targetId == "exp.scroll" && _scroll.BeginDrag(e.Y);
    }

    public bool OnPointerUp(UiPointerEventArgs e, string? clickId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            return true;
        }
        if (clickId == null || !_clicks.TryGetValue(clickId, out var click)) return false;
        click();
        return true;
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
