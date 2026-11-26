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
    private readonly UiScrollState _scroll = new();
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);
    private UiRect _contentRect;
    private bool _clearingHistory;
    private bool _configBusy;
    private bool _disposed;

    public void OnActivated() { }

    public void OnDeactivated()
    {
        _scroll.EndDrag();
        actions.CloseInlineEdit(commit: true);
    }

    private async Task ApplyHistoryLimitAsync(ExecutionHistoryStore store, int limit)
    {
        try
        {
            await store.SetLimitAsync(limit).ConfigureAwait(false);
            if (store.LastError == null) return;
        }
        catch (Exception error) { ExceptionDiagnostics.Record("Native.HistoryLimit", error); }

        actions.Host.TryPost(() =>
        {
            if (!_disposed && actions.Config.ExecutionHistoryLimitMb == limit && actions.OwnerWindow.IsVisible)
                actions.ShowNotice(L10n.T("History.ReadError"));
        });
    }

    public void OnCaptureLost() => _scroll.EndDrag();

    public void RefreshData() => actions.Invalidate();

    public void OnFilesDropped(string[] paths, string? targetId)
    {
        if (targetId != "exp.cfg.import" || paths.Length == 0 || _configBusy) return;
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

        const float pad = 19f, gap = 14f;
        bool histActive = cfg.ExperimentalMode && cfg.ExecutionHistoryEnabled;
        string limitHint = L10n.T("History.LimitHint");
        string transferHint = L10n.T("Exp.ConfigTransferHint");
        string clearHint = L10n.T("Exp.ClearConfigHint");
        (float PollRow, float HistoryRow, float LimitH, float Left, float Right, float TransferH, float ClearH, float Total) Measure(float width)
        {
            float inner = width - 2f * pad;
            float poll = UiPrimitives.MeasureSettingsSwitchHeight(L10n.T("Exp.BackgroundPolling"), L10n.T("Exp.BackgroundPollingHint"), inner);
            float history = UiPrimitives.MeasureSettingsSwitchHeight(L10n.T("History.Title"), L10n.T("History.Hint"), inner);
            float limit = histActive ? UiPrimitives.MeasureWrappedTextHeight(limitHint, 11.5f, inner - 32f) : 0f;
            float left = (inner - 1f) * 2f / 3f - gap, right = inner - left - 2f * gap - 1f;
            float transfer = UiPrimitives.MeasureWrappedTextHeight(transferHint, 11.5f, left);
            float clear = UiPrimitives.MeasureWrappedTextHeight(clearHint, 11.5f, right);
            float options = histActive ? 96f + limit : 0f;
            float total = 36f + 3f * (2f * pad + 14f) + poll + history + options + 26f + MathF.Max(transfer, clear) + 10f + 34f;
            return (poll, history, limit, left, right, transfer, clear, total);
        }
        float w = MathF.Max(240f, viewport.Width - UiMetrics.ScrollBarWidth);
        var layout = Measure(w);
        if (layout.Total <= viewport.Height)
        {
            w = MathF.Max(240f, viewport.Width);
            layout = Measure(w);
        }
        _scroll.ContentHeight = layout.Total;
        _scroll.Clamp();
        float x = viewport.X;
        float y = viewport.Y - _scroll.Offset;

        void Register(string id, UiRect r, Action onClick, UiCursorKind cursor = UiCursorKind.Hand, string? tip = null)
        {
            _clicks[id] = onClick;
            if (r.Bottom > viewport.Y && r.Y < viewport.Bottom)
            {
                var clipped = UiHitMap.Intersect(r, viewport);
                if (!clipped.IsEmpty) hit.Add(id, clipped, cursor, tip);
            }
        }

        g.PushClip(viewport);
        hit.PushClip(viewport);

        g.DrawText(L10n.T("Nav.Experimental"), new UiRect(x, y, w, 22f), t.TextPrimary, 16f, 600);
        y += 36f;

        float innerW = w - pad * 2f;

        float pollRow = layout.PollRow;
        float pollH = pad + pollRow + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, pollH));
        UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "exp.polling", new UiRect(x + pad, y + pad, innerW, pollRow),
            UiIcon.Options, L10n.T("Exp.BackgroundPolling"), L10n.T("Exp.BackgroundPollingHint"),
            cfg.BackgroundPollingFallbackEnabled, true, () =>
            {
                cfg.BackgroundPollingFallbackEnabled = !cfg.BackgroundPollingFallbackEnabled;
                actions.CommitSettings(notify: true);
            }, Register);
        y += pollH + 14f;

        float limitHintH = layout.LimitH;
        float historyRow = layout.HistoryRow;
        float histH = pad + historyRow + (histActive ? 96f + limitHintH : 0f) + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, histH));
        float hy = y + pad;
        UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "exp.history", new UiRect(x + pad, hy, innerW, historyRow),
            UiIcon.Options, L10n.T("History.Title"), L10n.T("History.Hint"),
            cfg.ExecutionHistoryEnabled, true, () =>
            {
                bool previous = cfg.ExecutionHistoryEnabled;
                cfg.ExecutionHistoryEnabled = !previous;
                if (!ConfigService.TryCommit(cfg))
                {
                    cfg.ExecutionHistoryEnabled = previous;
                    actions.ShowNotice(L10n.T("Tray.SaveFailed"));
                    return;
                }
                if (!cfg.ExecutionHistoryEnabled) AutoRuleService.StopExecutionHistory();
                actions.NotifySettingsChanged();
            }, Register);
        if (histActive)
        {
            hy += historyRow + 8f;
            float labelW = D2dSharedContext.Instance.MeasureText(L10n.T("History.Limit"), UiMetrics.FontFamily, 12.5f, 400, 240f, 32f, false).Width;
            g.DrawText(L10n.T("History.Limit"), new UiRect(x + pad + 32f, hy, labelW, 32f), t.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            var limRect = new UiRect(x + pad + 32f + labelW + 10f, hy, 70f, 32f);
            UiPrimitives.DrawTextBox(g, t, hit, "exp.hist.limit", limRect,
                Math.Clamp(cfg.ExecutionHistoryLimitMb, 1, 64).ToString(CultureInfo.InvariantCulture),
                focused: actions.ActiveInlineEditTag == "exp.hist.limit");
            Register("exp.hist.limit", limRect, () =>
            {
                actions.StartInlineEdit("exp.hist.limit", limRect,
                    Math.Clamp(cfg.ExecutionHistoryLimitMb, 1, 64).ToString(CultureInfo.InvariantCulture), null, (txt, viaEnter) =>
                    {
                        if (int.TryParse(txt.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 1 && v <= 64)
                        {
                            int previous = cfg.ExecutionHistoryLimitMb;
                            if (previous == v) return;
                            cfg.ExecutionHistoryLimitMb = v;
                            if (!ConfigService.TryCommit(cfg))
                            {
                                cfg.ExecutionHistoryLimitMb = previous;
                                actions.ShowNotice(L10n.T("Tray.SaveFailed"));
                                return;
                            }
                            actions.NotifySettingsChanged();
                            if (AutoRuleService.GetHistoryStore() is { } store) _ = ApplyHistoryLimitAsync(store, v);
                        }
                        else actions.ShowNotice(L10n.T("History.InvalidLimit"));
                    }, maxLength: 2);
            }, UiCursorKind.IBeam);

            g.DrawText(limitHint, new UiRect(x + pad + 32f, limRect.Bottom + 6f, innerW - 32f, limitHintH), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);
            float ButtonW(string key) => MathF.Max(80f, D2dSharedContext.Instance.MeasureText(L10n.T(key), UiMetrics.FontFamily, 13f, 400, innerW, 34f, false).Width + 2f);
            var viewRect = new UiRect(x + pad + 32f, limRect.Bottom + 16f + limitHintH, ButtonW("History.View"), 34f);
            UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.hist.view", viewRect, "", fontSize: 13f);
            g.DrawText(L10n.T("History.View"), viewRect, t.TextPrimary, 13f, 400, UiTextAlign.Center, UiVertAlign.Center);
            Register("exp.hist.view", viewRect, actions.OpenHistory);
            var clrRect = new UiRect(viewRect.Right + 8f, viewRect.Y, ButtonW("History.Clear"), 34f);
            UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.hist.clear", clrRect, "", enabled: !_clearingHistory);
            g.DrawText(L10n.T("History.Clear"), clrRect, _clearingHistory ? t.TextSecondary : t.TextPrimary, 13f, 400, UiTextAlign.Center, UiVertAlign.Center);
            if (!_clearingHistory) Register("exp.hist.clear", clrRect, () =>
            {
                var store = AutoRuleService.GetHistoryStore();
                if (store == null || _clearingHistory) return;
                _clearingHistory = true;
                actions.Invalidate();
                _ = Task.Run(async () =>
                {
                    bool ok = await store.ClearAsync().ConfigureAwait(false);
                    actions.Host.TryPost(() =>
                    {
                        _clearingHistory = false;
                        if (_disposed) return;
                        actions.Invalidate();
                        actions.ShowNotice(L10n.T(ok ? "History.Cleared" : "History.ReadError"));
                        if (ok) actions.RefreshHistoryIfOpen();
                    });
                });
            });
        }
        y += histH + 14f;

        float leftWidth = layout.Left, rightWidth = layout.Right;
        float rightX = x + pad + leftWidth + 2f * gap + 1f;
        float transferH = layout.TransferH, clearH = layout.ClearH;
        float hintH = MathF.Max(transferH, clearH);
        float cfgH = pad * 2 + 18f + 8f + hintH + 10f + 34f;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, cfgH));
        float cy = y + pad;
        g.DrawText(L10n.T("Exp.ConfigFiles"), new UiRect(x + pad, cy, innerW, 18f), t.TextSecondary, 13f, 600);
        cy += 26f;
        g.DrawLine(rightX - gap - 1f, cy, rightX - gap - 1f, y + cfgH - pad, t.Border, 1f, roundCaps: false);
        g.DrawText(transferHint, new UiRect(x + pad, cy, leftWidth, transferH), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);
        g.DrawText(clearHint, new UiRect(rightX, cy, rightWidth, clearH), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);
        float ButtonWidth(string key) => D2dSharedContext.Instance.MeasureText(L10n.T(key), UiMetrics.FontFamily, 13f, 400, innerW, 34f, false).Width + 20f;
        float btnW = MathF.Min(MathF.Max(ButtonWidth("Exp.ExportConfig"), ButtonWidth("Exp.ImportConfig")), (leftWidth - 8f) / 2f);
        var expBtn = new UiRect(x + pad, cy + hintH + 10f, btnW, 34f);
        var impBtn = new UiRect(expBtn.Right + 8f, expBtn.Y, btnW, 34f);
        var rstBtn = new UiRect(rightX, cy + hintH + 10f, MathF.Min(ButtonWidth("Exp.ClearConfig"), rightWidth), 34f);

        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.cfg.export", expBtn, L10n.T("Exp.ExportConfig"), enabled: !_configBusy, fontSize: 13f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.cfg.import", impBtn, L10n.T("Exp.ImportConfig"), enabled: !_configBusy, fontSize: 13f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "exp.cfg.clear", rstBtn, L10n.T("Exp.ClearConfig"), enabled: !_configBusy, fontSize: 13f);
        if (!_configBusy)
        {
            Register("exp.cfg.export", expBtn, ExportConfig, UiCursorKind.Hand, L10n.T("Exp.ExportConfig"));
            Register("exp.cfg.import", impBtn, ImportConfig, UiCursorKind.Hand, L10n.T("Exp.ImportConfig"));
            Register("exp.cfg.clear", rstBtn, ResetConfig, UiCursorKind.Hand, L10n.T("Exp.ClearConfigHint"));
        }


        hit.PopClip();
        g.PopClip();

        _scroll.Draw(g, t, hit, "exp.scroll", viewport);
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
                if (!NativeDialogs.ConfirmImport(actions.OwnerWindow.Handle, L10n.T("Exp.ImportConfig"), msg))
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
        if (ConfigService.TryResetToDefault(out _)) actions.Host.RestartPreview();
        else actions.ShowNotice(L10n.T("Tray.SaveFailed"));
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

}
