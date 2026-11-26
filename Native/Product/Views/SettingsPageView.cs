using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;
using SonicRoute.StoreInterop;
using SonicRoute.Updates;

namespace SonicRoute.Native.Product.Views;

internal sealed class SettingsPageView(IViewHostActions actions) : IViewPage
{
    private readonly UiScrollState _scroll = new();
    private readonly AliasEditor _aliasEditor = new(actions);
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);

    private Endpoint[] _outputs = [];
    private Endpoint[] _inputs = [];
    private AudioAppInfo[] _apps = [];
    private bool _loadingDevices;
    private bool _devicesLoaded;
    private bool _keepDevicesExpanded;
    private bool _deviceNamesExpanded;
    private bool _langMoreExpanded;
    private (string Code, string NativeName)[]? _languageChoices;
    private (string Code, string NativeName)[] _customLanguages = [];
    private readonly Dictionary<string, string> _languageEdits = new(StringComparer.OrdinalIgnoreCase);
    private bool _autoStartMoreExpanded;
    private bool _autoStartBusy;
    private bool _languageFilesBusy;
    private int _autoStartSyncVersion;
    private bool _scrollToUpdates;
    private UiRect _contentRect;
    private int _authorClicks;
    private string _updateNotes = "";
    private bool _subscribedUpdates;
    private bool _disposed;

    internal void ScrollToUpdates()
    {
        _scrollToUpdates = true;
        actions.Invalidate();
    }

    public void OnActivated()
    {
        _keepDevicesExpanded = !actions.Config.CollapseDeviceSections;
        _deviceNamesExpanded = !actions.Config.CollapseDeviceSections;
        if (!_subscribedUpdates)
        {
            actions.Host.Updates.Changed += OnUpdatesChanged;
            _subscribedUpdates = true;
        }
        if (string.IsNullOrEmpty(_updateNotes) && actions.Host.Updates.Release is { Notes.Length: > 0 } rel)
            _updateNotes = rel.Notes;
        if (actions.Host.EnvironmentInfo.IsPackaged)
            _ = SyncStoreAutoStartAsync(++_autoStartSyncVersion);
        RefreshData();
    }

    public void OnDeactivated()
    {
        OnCaptureLost();
        if (_subscribedUpdates)
        {
            actions.Host.Updates.Changed -= OnUpdatesChanged;
            _subscribedUpdates = false;
        }
        actions.CloseInlineEdit(commit: true);
    }

    public void OnCaptureLost()
    {
        _scroll.EndDrag();
    }

    public void OnFilesDropped(string[] paths, string? targetId)
    {
        if (targetId == "st.lang.import") ImportLanguages(paths);
    }

    public void RefreshData()
    {
        if (_disposed || _loadingDevices) return;
        _loadingDevices = true;
        _ = Task.Run(() =>
        {
            Endpoint[] render = [];
            Endpoint[] capture = [];
            AudioAppInfo[] apps = [];
            try
            {
                render = ProductAudio.Devices(Flow.Render);
                capture = ProductAudio.Devices(Flow.Capture);
                apps = actions.Host.Audio.Apps().Where(app => !AudioService.IsInternalApplication(app.ProcessId, app.ProcessName)).ToArray();
            }
            catch { }

            actions.Host.TryPost(() =>
            {
                _loadingDevices = false;
                if (_disposed) return;
                _outputs = render;
                _inputs = capture;
                _apps = apps;
                _devicesLoaded = true;
                actions.Invalidate();
            });
        });
    }

    public void Render(D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect contentRect)
    {
        _contentRect = contentRect;
        _clicks.Clear();

        var cfg = actions.Config;
        var viewport = contentRect;
        _scroll.ViewportHeight = viewport.Height;

        float padR = _scroll.MaxOffset > 0.5f ? UiMetrics.ScrollBarWidth : 0f;
        float x = viewport.X;
        float w = MathF.Max(240f, viewport.Width - padR);
        float y = viewport.Y - _scroll.Offset;
        float startY = y;

        void RegisterClick(string id, UiRect r, Action onClick, UiCursorKind cursor = UiCursorKind.Hand, string? tooltip = null)
        {
            _clicks[id] = onClick;
            if (r.Bottom > viewport.Y && r.Y < viewport.Bottom)
            {
                var clipped = UiHitMap.Intersect(r, viewport);
                if (!clipped.IsEmpty) hit.Add(id, clipped, cursor, tooltip);
            }
        }

        g.PushClip(viewport);
        hit.PushClip(viewport);

        // Page Header
        g.DrawText(L10n.T("St.Settings"), new UiRect(x, y, w, 30f), t.TextPrimary, 22f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        y += 33f;
        string pageHint = L10n.T("Settings.PageHint");
        float pageHintH = MathF.Ceiling(D2dSharedContext.Instance.MeasureText(pageHint, UiMetrics.FontFamily, 11.5f, 400, w, 2048f, true).Height);
        g.DrawText(pageHint, new UiRect(x, y, w, pageHintH), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);
        y += pageHintH + 19f;

        y = RenderDeviceVisibilityCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick) + 14f;
        y = RenderDeviceNamesCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick) + 14f;
        y = RenderDefaultAppCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick) + 14f;
        y = RenderAudioBehaviorCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick) + 14f;
        y = RenderLanguageCard(g, t, hit, viewport, x, y, w, RegisterClick) + 14f;
        float updatesOffset = y - startY;
        y = RenderUpdatesCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick) + 14f;
        y = RenderAboutCard(g, t, hit, x, y, w, RegisterClick);

        hit.PopClip();
        g.PopClip();

        _scroll.ContentHeight = MathF.Max(0f, y - startY);
        if (_scrollToUpdates)
        {
            _scrollToUpdates = false;
            _scroll.Offset = updatesOffset;
            actions.Invalidate();
        }
        _scroll.Clamp();
        _scroll.Draw(g, t, hit, "st.scroll", viewport);
    }

    private float RenderLanguageCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        float pad = 19f;
        float innerW = w - pad * 2f;
        var langs = L10n.SupportedLanguages;
        if (!ReferenceEquals(_languageChoices, langs))
        {
            _languageChoices = langs;
            _customLanguages = L10n.ListCustomLanguages()
                .OrderByDescending(l => string.Equals(l.Code, L10n.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                .ThenBy(l => l.NativeName, StringComparer.OrdinalIgnoreCase).ToArray();
            _languageEdits.Clear();
        }
        int curLangIdx = Array.FindIndex(langs, l => string.Equals(l.Code, L10n.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
        string curLangLabel = langs.Length > 0 ? langs[Math.Max(0, curLangIdx)].NativeName : L10n.CurrentLanguage;


        float langMoreH = _langMoreExpanded ? 46f : 0f;

        float customH = _customLanguages.Length == 0 ? 0f : 30f + 36f * _customLanguages.Length;
        float cardH = pad + 24f + 36f + langMoreH + customH + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, 20f), UiIcon.Message, L10n.T("St.Language"));
        cy += 24f;

        // Language ComboBox + MoreOptions button
        float comboW = MathF.Min(200f, innerW - 100f);
        var comboRect = new UiRect(x + pad, cy, comboW, 36f);
        UiPrimitives.DrawComboBox(g, t, hit, "st.lang.combo", comboRect, curLangLabel);
        register("st.lang.combo", comboRect, () => OpenLanguageCombo(comboRect), UiCursorKind.Hand, L10n.T("St.Language"));

        float moreTextW = D2dSharedContext.Instance.MeasureText(L10n.T("Exp.MoreOptions"), UiMetrics.FontFamily, 11.5f, 400, innerW, 24f, false).Width + 1f;
        var langMoreRect = new UiRect(comboRect.Right + 12f, cy + 5f, moreTextW + 18f, 26f);
        register("st.lang.more", langMoreRect, () => { _langMoreExpanded = !_langMoreExpanded; actions.Invalidate(); }, UiCursorKind.Hand, null);
        bool langMoreHot = hit.IsHover("st.lang.more");
        g.DrawText(L10n.T("Exp.MoreOptions"), new UiRect(langMoreRect.X, langMoreRect.Y, moreTextW, langMoreRect.Height),
            langMoreHot ? t.AccentHover : t.Accent, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(langMoreRect.X + moreTextW + 5f, langMoreRect.CenterY - 5f, 10f, 10f),
            t.Accent, 1.8f, _langMoreExpanded ? 180f : 0f);
        cy += 42f;

        if (_langMoreExpanded)
        {
            float btnX = x + pad;
            float btnW = MathF.Min(138f, (innerW - 24f) / 4f);
            DrawToolbarAction(g, t, hit, "st.lang.export", new UiRect(btnX, cy, btnW, 32f), L10n.T("Exp.ExportLang"), ExportLanguages, register, !_languageFilesBusy);
            btnX += btnW + 8f;
            DrawToolbarAction(g, t, hit, "st.lang.import", new UiRect(btnX, cy, btnW, 32f), L10n.T("Exp.ImportLang"), ImportLanguage, register, !_languageFilesBusy);
            btnX += btnW + 8f;
            DrawToolbarAction(g, t, hit, "st.lang.dir", new UiRect(btnX, cy, btnW, 32f), L10n.T("Exp.OpenLangDir"), () =>
            {
                if (!L10n.OpenExternalLangDir()) actions.ShowNotice(L10n.T("Exp.LangOpenFail"));
            }, register, !_languageFilesBusy);
            btnX += btnW + 8f;
            DrawToolbarAction(g, t, hit, "st.lang.restore", new UiRect(btnX, cy, btnW, 32f), L10n.T("Exp.RestoreLang"), RestoreLanguages, register, !_languageFilesBusy);
            cy += 46f;
        }

        if (_customLanguages.Length > 0)
        {
            g.DrawText(L10n.T("Exp.CustomLang"), new UiRect(x + pad, cy, innerW, 26f), t.TextSecondary, 12.5f, 600);
            cy += 30f;
            foreach (var (code, nativeName) in _customLanguages)
            {
                var box = new UiRect(x + pad, cy, MathF.Min(140f, innerW * .31f), 28f);
                string tag = "st.lang.name." + code;
                string draft = _languageEdits.GetValueOrDefault(code, nativeName);
                UiPrimitives.DrawTextBox(g, t, hit, tag, box, draft, focused: actions.ActiveInlineEditTag == tag);
                register(tag, box, () => actions.StartInlineEdit(tag, box, draft, null, (text, _) =>
                {
                    _languageEdits[code] = text;
                    actions.Invalidate();
                }), UiCursorKind.IBeam, L10n.T("Exp.LangNameHint"));
                var remove = new UiRect(x + w - pad - 72f, cy, 72f, 28f);
                var save = new UiRect(remove.X - 78f, cy, 72f, 28f);
                g.DrawText(code, new UiRect(box.Right + 8f, cy, MathF.Max(1f, save.X - box.Right - 14f), 28f),
                    code == L10n.CurrentLanguage ? t.Accent : t.TextSecondary, 11f);
                DrawToolbarAction(g, t, hit, "st.lang.save." + code, save, L10n.T("Exp.LangSave"), () =>
                {
                    if (!L10n.RenameCustomLanguage(code, _languageEdits.GetValueOrDefault(code, nativeName)))
                        actions.ShowNotice(L10n.T("Exp.LangEditFail"));
                    actions.Invalidate();
                }, register);
                DrawToolbarAction(g, t, hit, "st.lang.delete." + code, remove, L10n.T("Exp.LangDelete"), () =>
                {
                    if (!L10n.DeleteCustomLanguage(code))
                    {
                        actions.ShowNotice(L10n.T("Exp.LangEditFail"));
                        return;
                    }
                    actions.Config.Language = L10n.CurrentLanguage;
                    actions.CommitSettings(notify: true);
                    actions.RefreshAll();
                }, register);
                cy += 36f;
            }
        }
        return y + cardH;
    }

    private float RenderDefaultAppCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        const float pad = 19f; // Card border (1) + padding (18)
        const float gap = 8f;
        float innerW = w - 2f * pad;
        float choiceW = (innerW - 2f * gap) / 3f;
        float textW = MathF.Max(1f, choiceW - 68f);
        var ctx = D2dSharedContext.Instance;
        float Height(string text, float width, float size, int weight) =>
            MathF.Ceiling(ctx.MeasureText(text, UiMetrics.FontFamily, size, weight, width, 2048f, wrap: true).Height);
        var recent = MeasureChoice("St.DefaultAppRecent", "Settings.RecentHint");
        var last = MeasureChoice("St.DefaultAppLast", "Settings.LastHint");
        var fixedChoice = MeasureChoice("St.DefaultAppFixed", "Settings.FixedHint");
        (string Title, string Hint, float TitleH, float HintH) MeasureChoice(string titleKey, string hintKey)
        {
            string title = L10n.T(titleKey), hint = L10n.T(hintKey);
            return (title, hint, Height(title, textW, 12.5f, 600), Height(hint, textW, 10f, 400));
        }
        float choiceH = 26f + 5f + MathF.Max(recent.TitleH + recent.HintH,
            MathF.Max(last.TitleH + last.HintH, fixedChoice.TitleH + fixedChoice.HintH));
        string help = L10n.T("St.DefaultAppHint"), fixedHint = L10n.T("St.FixedAppHint");
        float headingH = MathF.Max(20f, Height(L10n.T("St.DefaultApp"), innerW - 32f, 14.5f, 600));
        float helpH = Height(help, innerW - 32f, 11.5f, 400);
        float fixedHintH = Height(fixedHint, innerW, 11.5f, 400);
        float cy = y + pad;
        float choicesY = cy + headingH + 4f + helpH + 10f;
        var fixedComboRect = new UiRect(x + pad, choicesY + choiceH + gap, innerW, 36f);
        float hintY = fixedComboRect.Bottom + 2f + 5f;
        float bottom = hintY + fixedHintH + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, bottom - y));
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, headingH), UiIcon.Apps, L10n.T("St.DefaultApp"));
        g.DrawText(help, new UiRect(x + pad + 32f, cy + headingH + 4f, innerW - 32f, helpH), t.TextSecondary,
            11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);

        void Choice(int column, string mode, UiIcon icon, (string Title, string Hint, float TitleH, float HintH) labels)
        {
            var rect = new UiRect(x + pad + column * (choiceW + gap), choicesY, choiceW, choiceH);
            bool selected = string.Equals(cfg.DefaultAppMode, mode, StringComparison.OrdinalIgnoreCase);
            DrawAppChoiceCard(g, t, hit, "st.appmode." + mode, rect, icon, labels.Title, labels.Hint, labels.TitleH, labels.HintH,
                selected, () => { cfg.DefaultAppMode = mode; actions.CommitSettings(notify: true); }, register);
        }
        Choice(0, "recent", UiIcon.Clock, recent);
        Choice(1, "last", UiIcon.History, last);
        Choice(2, "fixed", UiIcon.Apps, fixedChoice);

        bool isFixed = string.Equals(cfg.DefaultAppMode, "fixed", StringComparison.OrdinalIgnoreCase);
        var selectedApp = _apps.FirstOrDefault(app => !string.IsNullOrWhiteSpace(cfg.FixedAppName) &&
            string.Equals(app.ProcessName, cfg.FixedAppName, StringComparison.OrdinalIgnoreCase));
        string fixedDisplay = selectedApp == null ? "" : FixedAppLabel(selectedApp);
        UiPrimitives.DrawComboBox(g, t, hit, "st.fixedapp.combo", fixedComboRect, fixedDisplay, fontSize: 13f, enabled: isFixed);
        if (isFixed)
            register("st.fixedapp.combo", fixedComboRect, () => OpenFixedAppCombo(fixedComboRect), UiCursorKind.Hand, L10n.T("St.DefaultAppFixed"));
        g.DrawText(fixedHint, new UiRect(x + pad, hintY, innerW, fixedHintH), t.TextSecondary, 11.5f, 400,
            UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
        return bottom;
    }
    private float RenderAudioBehaviorCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        const float pad = 19f;
        float innerW = w - pad * 2f;
        float Row(string title, string hint) => UiPrimitives.MeasureSettingsSwitchHeight(L10n.T(title), L10n.T(hint), innerW);
        float startMinH = Row("St.StartMinimized", "Settings.StartMinimizedHint");
        float autoStartH = Row("St.AutoStart", "Settings.AutoStartHint");
        float startPanelH = Row("St.StartPanel", "Settings.StartPanelHint");
        float trayH = Row("St.TrayWheelEverywhere", "St.TrayWheelEverywhereHint");
        float micH = Row("St.ExperimentalMic", "St.ExperimentalMicHint");
        float micPanelH = cfg.ExperimentalMic ? UiPrimitives.MeasureSettingsSwitchHeight(L10n.T("Exp.MicPanel"), "", innerW - 32f) + 6f : 0f;
        float systemH = Row("St.PanelChangeSystemDefault", "St.PanelChangeSystemDefaultHint");
        float retainedH = Row("St.PanelOutputVolumesRetainedOnly", "St.PanelOutputVolumesRetainedOnlyHint");
        float collapseH = Row("Exp.Collapse", "Exp.CollapseHint");
        float experimentalH = cfg.ExperimentalUnlocked ? Row("St.ExperimentalMode", "St.ExperimentalModeHint") : 0f;
        float volRowH = MathF.Max(30f, UiPrimitives.MeasureWrappedTextHeight(L10n.T("St.VolumeStep"), 12.5f, innerW - 112f)
            + UiPrimitives.MeasureWrappedTextHeight(L10n.T("St.VolumeStepHint"), 11.5f, innerW - 112f)) + 20f;
        bool packaged = actions.Host.EnvironmentInfo.IsPackaged;
        float startupH = startMinH + autoStartH + startPanelH + (packaged ? 0f : 26f + (_autoStartMoreExpanded ? 40f : 0f));
        float cardH = pad * 2f + 24f + startupH + volRowH + trayH + micH + micPanelH + systemH + retainedH + collapseH + experimentalH;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, 28f), UiIcon.Settings, L10n.T("St.Misc"));
        cy += 24f;

        // Start minimized
        cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.startmin", new UiRect(x + pad, cy, innerW, startMinH),
            UiIcon.Monitor, L10n.T("St.StartMinimized"), L10n.T("Settings.StartMinimizedHint"), cfg.StartMinimized, true, () =>
            {
                cfg.StartMinimized = !cfg.StartMinimized;
                actions.CommitSettings(notify: true);
            }, register);

        // Auto-start switch (MSIX Store StartupTask vs portable HKCU Run)
        bool autoStart = packaged ? cfg.AutoStartStore : actions.Host.AutoStartEnabled;
        cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.autostart", new UiRect(x + pad, cy, innerW, autoStartH),
            UiIcon.Power, L10n.T("St.AutoStart"), L10n.T("Settings.AutoStartHint"), autoStart, true, () =>
            {
                if (_autoStartBusy) return;
                ToggleAutoStart(!autoStart);
            }, register);

        if (!packaged)
        {
            float moreTextW = D2dSharedContext.Instance.MeasureText(L10n.T("Exp.MoreOptions"), UiMetrics.FontFamily, 11.5f, 400, innerW, 24f, false).Width + 1f;
            var autoMoreRect = new UiRect(x + pad + 32f, cy + 2f, moreTextW + 18f, 22f);
            register("st.autostart.more", autoMoreRect, () => { _autoStartMoreExpanded = !_autoStartMoreExpanded; actions.Invalidate(); }, UiCursorKind.Hand, null);
            g.DrawText(L10n.T("Exp.MoreOptions"), new UiRect(autoMoreRect.X, autoMoreRect.Y, moreTextW, autoMoreRect.Height),
                hit.IsHover("st.autostart.more") ? t.AccentHover : t.Accent, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            g.DrawIcon(UiIcon.ChevronDown, new UiRect(autoMoreRect.X + moreTextW + 5f, autoMoreRect.CenterY - 4.5f, 9f, 9f),
                t.Accent, 1.8f, _autoStartMoreExpanded ? 180f : 0f);
            cy += 26f;

            if (_autoStartMoreExpanded)
            {
                var cleanRect = new UiRect(x + pad + 32f, cy + 2f, 200f, 30f);
                UiPrimitives.DrawSecondaryButton(g, t, hit, "st.autostart.clean", cleanRect, L10n.T("St.CleanAutoStart"),
                    enabled: !_autoStartBusy, fontSize: 12f);
                if (!_autoStartBusy)
                    register("st.autostart.clean", cleanRect, CleanAutoStart, UiCursorKind.Hand, L10n.T("St.CleanAutoStart"));
                cy += 40f;
            }
        }

        // Start panel on start
        cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.startpanel", new UiRect(x + pad, cy, innerW, startPanelH),
            UiIcon.Application, L10n.T("St.StartPanel"), L10n.T("Settings.StartPanelHint"), cfg.StartPanelOnStart, true, () =>
            {
                cfg.StartPanelOnStart = !cfg.StartPanelOnStart;
                actions.CommitSettings(notify: true);
            }, register);

        // WPF VolumeStepBox commits on Enter or normal focus loss.
        var volRect = new UiRect(x + pad, cy, innerW, volRowH);
        g.DrawIcon(UiIcon.Speaker, new UiRect(volRect.X + 2f, volRect.CenterY - 8.5f, 17f, 17f), t.TextSecondary, 1.8f);
        float textW = MathF.Max(1f, innerW - 112f);
        g.DrawText(L10n.T("St.VolumeStep"), new UiRect(volRect.X + 32f, volRect.Y + 10f, textW, 20f), t.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(L10n.T("St.VolumeStepHint"), new UiRect(volRect.X + 32f, volRect.Y + 29f, textW, volRowH - 35f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);

        int clampedStep = Math.Clamp(cfg.VolumeStep, 1, 20);
        var stepBoxRect = new UiRect(volRect.Right - 64f, volRect.CenterY - 15f, 64f, 30f);
        UiPrimitives.DrawTextBox(g, t, hit, "st.volstep.box", stepBoxRect, clampedStep.ToString(CultureInfo.InvariantCulture),
            focused: actions.ActiveInlineEditTag == "st.volstep.box", fontSize: 12.5f, centered: true);
        register("st.volstep.box", stepBoxRect, () =>
        {
            actions.StartInlineEdit("st.volstep.box", stepBoxRect, clampedStep.ToString(CultureInfo.InvariantCulture), null, (text, _) =>
            {
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return;
                int step = Math.Clamp(value, 1, 20);
                if (cfg.VolumeStep == step) return;
                cfg.VolumeStep = step;
                actions.CommitSettings(notify: true);
            }, centered: true);
        }, UiCursorKind.IBeam, L10n.T("St.VolumeStep"));
        cy += volRowH;

        cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.traywheel.everywhere", new UiRect(x + pad, cy, innerW, trayH),
            UiIcon.Speaker, L10n.T("St.TrayWheelEverywhere"), L10n.T("St.TrayWheelEverywhereHint"), cfg.TrayWheelEverywhere, true, () =>
            {
                cfg.TrayWheelEverywhere = !cfg.TrayWheelEverywhere;
                actions.CommitSettings(notify: true);
            }, register);

        cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.expmic", new UiRect(x + pad, cy, innerW, micH),
            UiIcon.Microphone, L10n.T("St.ExperimentalMic"), L10n.T("St.ExperimentalMicHint"), cfg.ExperimentalMic, true, () =>
            {
                cfg.ExperimentalMic = !cfg.ExperimentalMic;
                if (!actions.CommitSettings(notify: true)) return;
                actions.ShowNotice(L10n.T("St.ExpMicNeedRestart"));
            }, register);

        if (cfg.ExperimentalMic)
        {
            cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.micinpanel", new UiRect(x + pad + 32f, cy + 6f, innerW - 32f, micPanelH - 6f),
                UiIcon.Microphone, L10n.T("Exp.MicPanel"), "", cfg.MicInPanel, true, () =>
                {
                    cfg.MicInPanel = !cfg.MicInPanel;
                    if (!actions.CommitSettings(notify: true)) return;
                    actions.ShowNotice(L10n.T("St.ExpMicPanelNeedRestart"));
                }, register);
        }

        cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.panelchangesysdef", new UiRect(x + pad, cy, innerW, systemH),
            UiIcon.Switch, L10n.T("St.PanelChangeSystemDefault"), L10n.T("St.PanelChangeSystemDefaultHint"), cfg.PanelChangeSystemDefault, true, () =>
            {
                cfg.PanelChangeSystemDefault = !cfg.PanelChangeSystemDefault;
                actions.CommitSettings(notify: true);
            }, register);

        cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.panelretainedonly", new UiRect(x + pad, cy, innerW, retainedH),
            UiIcon.Speaker, L10n.T("St.PanelOutputVolumesRetainedOnly"), L10n.T("St.PanelOutputVolumesRetainedOnlyHint"), cfg.PanelOutputVolumesRetainedOnly, true, () =>
            {
                cfg.PanelOutputVolumesRetainedOnly = !cfg.PanelOutputVolumesRetainedOnly;
                actions.CommitSettings(notify: true);
            }, register);

        cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.collapsedev", new UiRect(x + pad, cy, innerW, collapseH),
            UiIcon.Options, L10n.T("Exp.Collapse"), L10n.T("Exp.CollapseHint"), cfg.CollapseDeviceSections, true, () =>
            {
                cfg.CollapseDeviceSections = !cfg.CollapseDeviceSections;
                _keepDevicesExpanded = !cfg.CollapseDeviceSections;
                _deviceNamesExpanded = !cfg.CollapseDeviceSections;
                actions.CommitSettings(notify: true);
            }, register);

        if (cfg.ExperimentalUnlocked)
        {
            UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.expmode", new UiRect(x + pad, cy, innerW, experimentalH),
                UiIcon.Experiment, L10n.T("St.ExperimentalMode"), L10n.T("St.ExperimentalModeHint"), cfg.ExperimentalMode, true, () =>
                {
                    cfg.ExperimentalMode = !cfg.ExperimentalMode;
                    if (!actions.CommitSettings(notify: true)) return;
                    if (!cfg.ExperimentalMode) AutoRuleService.StopExecutionHistory();
                    actions.ShowNotice(L10n.T("St.ExpModeNeedRestart"));
                }, register);
        }

        return y + cardH;
    }

    private static float MeasureDeviceHint(string hint, float width) =>
        MathF.Ceiling(D2dSharedContext.Instance.MeasureText(hint, UiMetrics.FontFamily, 11.5f, 400,
            MathF.Max(1f, width), 2048f, true).Height);

    private float RenderDeviceVisibilityCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        const float pad = 19f;
        float innerW = w - pad * 2f;
        const float itemW = 184f;
        const float itemH = 46f;
        const float gap = 8f;
        int cols = Math.Max(1, (int)MathF.Floor((innerW + gap) / (itemW + gap)));

        var outDevices = BuildDeviceList(_outputs, Flow.Render, includeSystemDefault: true);
        var inDevices = cfg.ExperimentalMic ? BuildDeviceList(_inputs, Flow.Capture, includeSystemDefault: true) : [];

        int outRows = outDevices.Count == 0 ? 1 : (int)MathF.Ceiling(outDevices.Count / (float)cols);
        int inRows = inDevices.Count == 0 ? 0 : (int)MathF.Ceiling(inDevices.Count / (float)cols);

        float bodyH = 0f;
        if (_keepDevicesExpanded)
        {
            bodyH += 30f + outRows * (itemH + gap);
            if (cfg.ExperimentalMic)
                bodyH += 10f + 30f + Math.Max(1, inRows) * (itemH + gap);
        }

        string hint = L10n.T("Settings.KeepHint");
        float hintWidth = MathF.Max(1f, innerW - 32f - (cfg.CollapseDeviceSections ? 102f : 0f));
        float hintHeight = MeasureDeviceHint(hint, hintWidth);
        const float headingHeight = 20f;
        float headerH = headingHeight + 4f + hintHeight + 10f;
        float cardH = pad + headerH + bodyH + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, headingHeight), UiIcon.Monitor, L10n.T("Settings.KeepTitle"));
        g.DrawText(hint, new UiRect(x + pad + 32f, cy + headingHeight + 4f, hintWidth, hintHeight), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);

        if (cfg.CollapseDeviceSections)
        {
            var toggleRect = new UiRect(x + w - pad - 102f, cy + 2f, 102f, 26f);
            register("st.keep.toggle", toggleRect, () => { _keepDevicesExpanded = !_keepDevicesExpanded; actions.Invalidate(); }, UiCursorKind.Hand, L10n.T("Exp.CollapseTip"));
            g.DrawText(L10n.T("Exp.MoreOptions"), new UiRect(toggleRect.X, toggleRect.Y, 80f, toggleRect.Height),
                hit.IsHover("st.keep.toggle") ? t.AccentHover : t.Accent, 11.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
            g.DrawIcon(UiIcon.ChevronDown, new UiRect(toggleRect.Right - 14f, toggleRect.CenterY - 4.5f, 9f, 9f),
                t.Accent, 1.8f, _keepDevicesExpanded ? 180f : 0f);
        }

        cy += headerH + 6f;
        if (_keepDevicesExpanded)
        {
            // Output header + Select All
            g.DrawText(L10n.T("St.Output"), new UiRect(x + pad, cy, innerW - 90f, 24f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            bool allOutVisible = outDevices.All(d => !cfg.HiddenOutputDevices.Contains(d.Id, StringComparer.OrdinalIgnoreCase));
            var selOutRect = new UiRect(x + w - pad - 84f, cy - 1f, 84f, 26f);
            UiPrimitives.DrawSecondaryButton(g, t, hit, "st.keep.out.all", selOutRect,
                L10n.T(allOutVisible ? "St.ClearAll" : "St.SelectAll"), fontSize: 11.5f);
            register("st.keep.out.all", selOutRect, () =>
            {
                if (allOutVisible)
                {
                    foreach (var d in outDevices)
                        if (!cfg.HiddenOutputDevices.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
                            cfg.HiddenOutputDevices.Add(d.Id);
                }
                else
                {
                    cfg.HiddenOutputDevices.Clear();
                }
                actions.CommitSettings(notify: true);
            }, UiCursorKind.Hand, null);
            cy += 30f;

            cy = DrawDeviceCheckGrid(g, t, hit, x + pad, cy, cols, itemW, itemH, gap, outDevices, cfg.HiddenOutputDevices, "out", register);

            if (cfg.ExperimentalMic)
            {
                cy += 8f;
                g.DrawText(L10n.T("St.Input"), new UiRect(x + pad, cy, innerW - 90f, 24f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                bool allInVisible = inDevices.All(d => !cfg.HiddenInputDevices.Contains(d.Id, StringComparer.OrdinalIgnoreCase));
                var selInRect = new UiRect(x + w - pad - 84f, cy - 1f, 84f, 26f);
                UiPrimitives.DrawSecondaryButton(g, t, hit, "st.keep.in.all", selInRect,
                    L10n.T(allInVisible ? "St.ClearAll" : "St.SelectAll"), fontSize: 11.5f);
                register("st.keep.in.all", selInRect, () =>
                {
                    if (allInVisible)
                    {
                        foreach (var d in inDevices)
                            if (!cfg.HiddenInputDevices.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
                                cfg.HiddenInputDevices.Add(d.Id);
                    }
                    else
                    {
                        cfg.HiddenInputDevices.Clear();
                    }
                    actions.CommitSettings(notify: true);
                }, UiCursorKind.Hand, null);
                cy += 30f;

                DrawDeviceCheckGrid(g, t, hit, x + pad, cy, cols, itemW, itemH, gap, inDevices, cfg.HiddenInputDevices, "in", register);
            }
        }

        return y + cardH;
    }

    private float DrawDeviceCheckGrid(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        float x, float y, int cols, float itemW, float itemH, float gap,
        List<AudioDeviceInfo> devices, List<string> hiddenList, string prefix,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        if (devices.Count == 0)
        {
            g.DrawText(_devicesLoaded ? L10n.T("Ov.NoDevice") : L10n.T("Tray.Loading"),
                new UiRect(x, y, 220f, itemH), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            return y + itemH + gap;
        }

        for (int i = 0; i < devices.Count; i++)
        {
            var dev = devices[i];
            int col = i % cols;
            int row = i / cols;
            var cardRect = new UiRect(x + col * (itemW + gap), y + row * (itemH + gap), itemW, itemH);
            bool isChecked = !hiddenList.Contains(dev.Id, StringComparer.OrdinalIgnoreCase);

            string cardId = $"st.devchk.{prefix}.{dev.Id}";

            bool hot = hit.IsHover(cardId);
            g.FillRoundedRect(cardRect, 8f, t.SurfaceBg);
            g.DrawRoundedRect(cardRect, 8f, hot ? t.Accent : t.Border, 1f);

            // Checkbox (14x14)
            var box = new UiRect(cardRect.X + 9f, cardRect.CenterY - 7f, 14f, 14f);
            if (isChecked)
            {
                g.FillRoundedRect(box, 3f, t.Accent);
                g.DrawLine(box.X + 3f, box.Y + 7.2f, box.X + 5.8f, box.Y + 10.2f, t.OnAccent, 1.6f);
                g.DrawLine(box.X + 5.8f, box.Y + 10.2f, box.X + 11f, box.Y + 4f, t.OnAccent, 1.6f);
            }
            else
            {
                g.FillRoundedRect(box, 3f, t.SurfaceBg);
                g.DrawRoundedRect(box, 3f, t.Border, 1f);
            }

            // Icon
            var icon = DeviceIcon(dev);
            g.DrawIcon(icon, new UiRect(cardRect.X + 29f, cardRect.CenterY - 8.5f, 17f, 17f), t.TextSecondary, 1.8f);

            // Title + Detail
            (string title, string detail) = SplitDeviceName(dev);
            string shownTitle = GetDeviceTitle(dev, title);
            float txtX = cardRect.X + 52f;
            float txtW = MathF.Max(20f, cardRect.Width - 60f);
            if (detail.Length > 0)
            {
                g.DrawText(shownTitle, new UiRect(txtX, cardRect.Y + 6f, txtW, 18f), t.TextPrimary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                g.DrawText(detail, new UiRect(txtX, cardRect.Y + 24f, txtW, 15f), t.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }
            else
            {
                g.DrawText(shownTitle, new UiRect(txtX, cardRect.Y, txtW, cardRect.Height), t.TextPrimary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }

            // Card click toggles visibility
            register(cardId, cardRect, () =>
            {
                if (hiddenList.Contains(dev.Id, StringComparer.OrdinalIgnoreCase))
                    hiddenList.RemoveAll(id => string.Equals(id, dev.Id, StringComparison.OrdinalIgnoreCase));
                else
                    hiddenList.Add(dev.Id);
                actions.CommitSettings(notify: true);
            }, UiCursorKind.Hand, dev.DisplayName);

        }

        int totalRows = (int)MathF.Ceiling(devices.Count / (float)cols);
        return y + totalRows * (itemH + gap);
    }

    private float RenderDeviceNamesCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        const float pad = 19f;
        float innerW = w - pad * 2f;
        var outDevices = BuildDeviceList(_outputs, Flow.Render, includeSystemDefault: true);
        var inDevices = cfg.ExperimentalMic ? BuildDeviceList(_inputs, Flow.Capture, includeSystemDefault: true) : [];

        const float nameRowH = 44f;
        const float rowGap = 6f;
        float bodyH = 0f;
        if (_deviceNamesExpanded)
        {
            bodyH += 26f + Math.Max(1, outDevices.Count) * (nameRowH + rowGap);
            if (cfg.ExperimentalMic)
                bodyH += 8f + 26f + Math.Max(1, inDevices.Count) * (nameRowH + rowGap);
        }

        string hint = L10n.T("St.DeviceNamesHint");
        float hintWidth = MathF.Max(1f, innerW - 32f - (cfg.CollapseDeviceSections ? 102f : 0f));
        float hintHeight = MeasureDeviceHint(hint, hintWidth);
        const float headingHeight = 20f;
        float headerH = headingHeight + 4f + hintHeight + 10f;
        float cardH = pad + headerH + bodyH + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, headingHeight), UiIcon.Label, L10n.T("St.DeviceNames"));
        g.DrawText(hint, new UiRect(x + pad + 32f, cy + headingHeight + 4f, hintWidth, hintHeight), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);

        if (cfg.CollapseDeviceSections)
        {
            var toggleRect = new UiRect(x + w - pad - 102f, cy + 2f, 102f, 26f);
            register("st.names.toggle", toggleRect, () => { _deviceNamesExpanded = !_deviceNamesExpanded; actions.Invalidate(); }, UiCursorKind.Hand, L10n.T("Exp.CollapseTip"));
            g.DrawText(L10n.T("Exp.MoreOptions"), new UiRect(toggleRect.X, toggleRect.Y, 80f, toggleRect.Height),
                hit.IsHover("st.names.toggle") ? t.AccentHover : t.Accent, 11.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
            g.DrawIcon(UiIcon.ChevronDown, new UiRect(toggleRect.Right - 14f, toggleRect.CenterY - 4.5f, 9f, 9f),
                t.Accent, 1.8f, _deviceNamesExpanded ? 180f : 0f);
        }

        cy += headerH + 6f;
        if (_deviceNamesExpanded)
        {
            g.DrawText(L10n.T("St.Output"), new UiRect(x + pad, cy, innerW, 22f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            cy += 26f;
            cy = DrawDeviceNameRows(g, t, hit, x + pad, cy, innerW, nameRowH, rowGap, outDevices, cfg, "out", register);

            if (cfg.ExperimentalMic)
            {
                cy += 8f;
                g.DrawText(L10n.T("St.Input"), new UiRect(x + pad, cy, innerW, 22f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                cy += 26f;
                DrawDeviceNameRows(g, t, hit, x + pad, cy, innerW, nameRowH, rowGap, inDevices, cfg, "in", register);
            }
        }

        return y + cardH;
    }

    private float DrawDeviceNameRows(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        float x, float y, float w, float rowH, float rowGap,
        List<AudioDeviceInfo> devices, AppConfig cfg, string prefix,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            var dev = devices[i];
            var rowRect = new UiRect(x, y, w, rowH);
            g.FillRoundedRect(rowRect, 8f, t.SurfaceBgAlpha);
            g.DrawRoundedRect(rowRect, 8f, t.Border, 1f);

            g.DrawIcon(DeviceIcon(dev), new UiRect(rowRect.X + 10f, rowRect.CenterY - 9f, 18f, 18f), t.TextSecondary, 1.8f);
            (string title, string detail) = SplitDeviceName(dev);
            float labelW = MathF.Max(120f, w * 0.48f - 38f);
            if (detail.Length > 0)
            {
                g.DrawText(GetDeviceTitle(dev, title), new UiRect(rowRect.X + 36f, rowRect.Y + 5f, labelW, 18f), t.TextPrimary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
                g.DrawText(detail, new UiRect(rowRect.X + 36f, rowRect.Y + 23f, labelW, 15f), t.TextSecondary, 10f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }
            else
            {
                g.DrawText(GetDeviceTitle(dev, title), new UiRect(rowRect.X + 36f, rowRect.Y, labelW, rowH), t.TextPrimary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
            }

            string boxId = $"st.devname.{prefix}.{dev.Id}";
            float boxW = MathF.Max(120f, w * 0.48f);
            var boxRect = new UiRect(rowRect.Right - 10f - boxW, rowRect.CenterY - 15f, boxW, 30f);
            string alias = cfg.DeviceNames.TryGetValue(dev.Id, out var saved) ? saved : "";
            UiPrimitives.DrawTextBox(g, t, hit, boxId, boxRect, alias, focused: actions.ActiveInlineEditTag == boxId);
            register(boxId, boxRect, () => _aliasEditor.Open(boxId, boxRect, dev.Id, device: true), UiCursorKind.IBeam, dev.DisplayName);

            y += rowH + rowGap;
        }
        return y;
    }

    private float RenderUpdatesCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        var updates = actions.Host.Updates;
        bool store = updates.IsPackaged;
        string displayVersion = BuildIdentity.ProductVersion;
        string versionLine = string.Format(L10n.T("Upd.Current"), "v" + displayVersion, AppRuntimeIdentity.Current.CommandEdition, UpdateBootstrap.Architecture)
            + " · " + UpdateProtocol.RuntimeName;
        string? newestLine = updates.Release != null ? string.Format(L10n.T("Upd.Newest"), updates.Release.Tag) : null;

        string statusLine = store ? L10n.T("Upd.Store") : L10n.T("Upd." + updates.State);
        string? outcomeLine = !store && updates.LastOutcome.Length != 0 ? L10n.T(updates.LastOutcome) : null;
        string? dlLine = updates.State == UpdateState.Downloading
            ? string.Format(CultureInfo.InvariantCulture, "{0:F1} / {1:F1} MB · {2:F1} KB/s",
                updates.Bytes / 1048576.0,
                (updates.Release?.Package?.Size ?? 0) / 1048576.0,
                updates.BytesPerSecond / 1024.0)
            : null;
        string? errLine = updates.State == UpdateState.Failed ? L10n.T(updates.ErrorHint) : null;

        bool showProgress = updates.State is UpdateState.Downloading or UpdateState.Verifying;
        string notes = string.IsNullOrWhiteSpace(_updateNotes) ? (updates.Release?.Notes ?? "") : _updateNotes;
        bool showNotes = !string.IsNullOrWhiteSpace(notes);
        bool showDownload = !store && updates.Release?.Package != null && !updates.Busy && updates.State != UpdateState.Ready;
        bool showInstall = !store && updates.State == UpdateState.Ready;
        bool showCancel = showProgress;
        bool showDismiss = !store && updates.ShouldNotify;
        bool showActionRow = showDownload || showInstall || showCancel || showDismiss;

        const float pad = 19f;
        float innerW = w - pad * 2f;
        float verH = newestLine != null ? 42f : 23f;
        float autoH = store ? 0f : UiPrimitives.MeasureSettingsSwitchHeight(L10n.T("Upd.Automatic"), L10n.T("Upd.AutomaticHint"), innerW);
        int statusLines = 1 + (outcomeLine != null ? 1 : 0) + (dlLine != null ? 1 : 0) + (errLine != null ? 1 : 0);
        float statusH = statusLines * 18f + 16f;
        float progH = showProgress ? 16f : 0f;
        float notesH = showNotes ? Math.Clamp(D2dSharedContext.Instance.MeasureText(notes, UiMetrics.FontFamily, 12f, 400, Math.Max(1f, innerW - 22f), 180f, true).Height + 8f, 28f, 180f) + 13f : 0f;
        float actionsH = showActionRow ? 42f : 0f;

        float cardH = pad + 40f + verH + autoH + statusH + progH + notesH + actionsH + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW - 180f, 34f), UiIcon.Clock, L10n.T("Upd.Title"));

        var logRect = new UiRect(x + w - pad - 28f, cy, 28f, 36f);
        UiPrimitives.DrawGhostIconButton(g, t, hit, "st.about.changelog", logRect, UiIcon.History, 16f, tooltip: L10n.T("Upd.Changelog"));
        register("st.about.changelog", logRect, () => OpenUrl(updates.Release?.Url ?? UpdateProtocol.Repository + "/releases"), UiCursorKind.Hand, L10n.T("Upd.Changelog"));

        float checkWidth = D2dSharedContext.Instance.MeasureText(L10n.T("St.CheckUpdate"), UiMetrics.FontFamily, 13f, 400, innerW, 36f, false).Width + 29f;
        var chkRect = new UiRect(logRect.X - 4f - checkWidth, cy, checkWidth, 36f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "st.about.checkupd", chkRect, L10n.T("St.CheckUpdate"), enabled: !updates.Busy, fontSize: 13f);
        if (!updates.Busy)
            register("st.about.checkupd", chkRect, CheckForUpdates, UiCursorKind.Hand, L10n.T("St.CheckUpdate"));
        cy += 40f;

        // Current & newest version
        g.DrawText(versionLine, new UiRect(x + pad, cy, innerW, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        cy += 19f;
        if (newestLine != null)
        {
            g.DrawText(newestLine, new UiRect(x + pad, cy, innerW, 18f), t.Accent, 11.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
            cy += 19f;
        }
        cy += 4f;

        // Auto-check updates switch row (unpackaged only)
        if (!store)
        {
            cy = UiPrimitives.DrawSettingsSwitchWithHint(g, t, hit, "st.about.autochk", new UiRect(x + pad, cy, innerW, autoH),
                UiIcon.Clock, L10n.T("Upd.Automatic"), L10n.T("Upd.AutomaticHint"), cfg.AutoCheckUpdates, true, () =>
                {
                    cfg.AutoCheckUpdates = !cfg.AutoCheckUpdates;
                    if (!actions.CommitSettings(notify: true)) return;
                    updates.AutomaticSettingChanged(cfg.AutoCheckUpdates);
                }, register);
        }

        // Status text
        cy += 6f;
        string combinedStatus = statusLine;
        if (dlLine != null) combinedStatus += "  " + dlLine;
        if (outcomeLine != null) combinedStatus += "\n" + outcomeLine;
        if (errLine != null) combinedStatus += "\n" + errLine;
        g.DrawText(combinedStatus, new UiRect(x + pad, cy, innerW, statusH - 6f),
            updates.State == UpdateState.Failed ? t.Fail : t.TextPrimary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true);
        cy += statusH;

        if (showProgress)
        {
            float frac = updates.Release?.Package is { Size: > 0 } pkg
                ? Math.Clamp((float)(updates.Bytes / (double)pkg.Size), 0f, 1f)
                : 0f;
            var track = new UiRect(x + pad, cy + 2f, innerW, 6f);
            g.FillRoundedRect(track, 3f, t.SliderBg);
            if (frac > 0.005f)
                g.FillRoundedRect(new UiRect(track.X, track.Y, track.Width * frac, track.Height), 3f, t.Accent);
            cy += 16f;
        }

        if (showNotes)
        {
            var notesRect = new UiRect(x + pad, cy + 5f, innerW, notesH - 13f);
            const string id = "st.about.notes";
            UiPrimitives.DrawTextBox(g, t, hit, id, notesRect, notes, focused: actions.ActiveInlineEditTag == id,
                fontSize: 12f, readOnlyMultiline: true);
            register(id, notesRect, () => actions.StartInlineEdit(id, notesRect, notes, null, (_, _) => { },
                selectAll: false, readOnlyMultiline: true), UiCursorKind.IBeam, null);
            cy += notesH;
        }

        if (showActionRow)
        {
            float bx = x + pad;
            if (showDownload)
            {
                var r = new UiRect(bx, cy, 118f, 32f);
                UiPrimitives.DrawAccentButton(g, t, hit, "st.about.dl", r, L10n.T("Upd.Download"), fontSize: 12f);
                register("st.about.dl", r, () => _ = updates.DownloadAsync(), UiCursorKind.Hand, null);
                bx = r.Right + 8f;
            }
            if (showInstall)
            {
                var r = new UiRect(bx, cy, 134f, 32f);
                UiPrimitives.DrawAccentButton(g, t, hit, "st.about.install", r, L10n.T("Upd.Restart"), fontSize: 12f);
                register("st.about.install", r, InstallReadyUpdate, UiCursorKind.Hand, null);
                bx = r.Right + 8f;
            }
            if (showCancel)
            {
                var r = new UiRect(bx, cy, 108f, 32f);
                UiPrimitives.DrawSecondaryButton(g, t, hit, "st.about.cancel", r, L10n.T("Upd.Cancel"), fontSize: 12f);
                register("st.about.cancel", r, () => updates.CancelDownload(), UiCursorKind.Hand, null);
                bx = r.Right + 8f;
            }
            if (showDismiss)
            {
                var laterR = new UiRect(bx, cy, 104f, 32f);
                UiPrimitives.DrawSecondaryButton(g, t, hit, "st.about.later", laterR, L10n.T("Upd.Later"), fontSize: 12f);
                register("st.about.later", laterR, () => updates.Dismiss(false), UiCursorKind.Hand, null);

                var ignR = new UiRect(laterR.Right + 8f, cy, 108f, 32f);
                UiPrimitives.DrawSecondaryButton(g, t, hit, "st.about.ignore", ignR, L10n.T("Upd.Ignore"), fontSize: 12f);
                register("st.about.ignore", ignR, () => updates.Dismiss(true), UiCursorKind.Hand, null);
            }
            cy += 42f;
        }

        return y + cardH;
    }

    private float RenderAboutCard(D2dTarget g, UiThemePalette t, UiHitMap hit, float x, float y, float w,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        const float pad = 18f;
        const float cardHeight = pad + 16f + 18f + 12f + 22f + pad;
        UiPrimitives.DrawCard(g, t, new UiRect(x, y, w, cardHeight));
        var author = new UiRect(x + pad, y + pad + 16f, w - pad * 2, 18f);
        register("st.about.author", author, OnAuthorLabelClicked, UiCursorKind.Hand, L10n.T("St.ExpAuthorTip"));
        g.DrawText(L10n.T("App.About") + " v" + BuildIdentity.ProductVersion, author, t.TextSecondary, 11.5f);

        float cursor = author.X;
        float linkY = author.Bottom + 12f;
        void Append(string text, string? id = null, Action? open = null)
        {
            float width = D2dSharedContext.Instance.MeasureText(text, UiMetrics.FontFamily, 13f, 400, w, 22f, false).Width;
            var rect = new UiRect(cursor, linkY, width, 22f);
            if (id != null && open != null) register(id, rect, open, UiCursorKind.Hand, text);
            g.DrawText(text, rect, id == null ? t.TextSecondary : hit.IsHover(id) ? t.AccentHover : t.Accent, 13f);
            if (id != null) g.DrawLine(rect.X, rect.Bottom - 4f, rect.Right, rect.Bottom - 4f, t.Accent, .8f, roundCaps: false);
            cursor += width + 3f;
        }
        Append(L10n.T("St.AuthorHome"));
        Append(L10n.T("St.Bilibili"), "st.about.bili", () => OpenUrl("https://b23.tv/TDqSAKM"));
        Append(" ‖ ");
        Append(L10n.T("St.Afdian"), "st.about.ifdian", () => OpenUrl("https://www.ifdian.net/a/koukou021"));
        Append(" ‖ ");
        Append("GitHub", "st.about.github", () => OpenUrl(UpdateProtocol.Repository));
        Append(" ‖ ");
        Append("GPL-3.0", "st.about.license", ShowLicense);
        return y + cardHeight;
    }

    private static void DrawAppChoiceCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        string id, UiRect rect, UiIcon icon, string title, string hint, float titleH, float hintH, bool selected,
        Action onClick, Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        register(id, rect, onClick, UiCursorKind.Hand, hint);
        bool hot = hit.IsHover(id);
        g.FillRoundedRect(rect, 8f, selected ? t.NavSelected : t.SurfaceBg);
        g.DrawRoundedRect(rect, 8f, selected || hot ? t.Accent : t.Border, 1f);

        // SettingsAppChoice has 18/28/* columns inside its 1-DIP border and 10×12 padding.
        float left = rect.X + 11f;
        float cx = left + 6.5f;
        float cy = rect.Y + 13f + 2f + 6.5f;
        g.DrawEllipse(cx, cy, 6f, 6f, selected ? t.Accent : t.Border, 1f);
        if (selected) g.FillEllipse(cx, cy, 3.5f, 3.5f, t.Accent);
        g.DrawIcon(icon, new UiRect(left + 18f, rect.CenterY - 9f, 18f, 18f), selected ? t.Accent : t.TextSecondary, 1.8f);
        float textX = left + 46f;
        float textW = MathF.Max(1f, rect.Right - 11f - textX);
        float textY = rect.CenterY - (titleH + 5f + hintH) / 2f;
        g.DrawText(title, new UiRect(textX, textY, textW, titleH), t.TextPrimary, 12.5f, 600,
            UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
        g.DrawText(hint, new UiRect(textX, textY + titleH + 5f, textW, hintH), t.TextSecondary, 10f, 400,
            UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: false);
    }
    private static void DrawToolbarAction(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        string id, UiRect rect, string text, Action onClick,
        Action<string, UiRect, Action, UiCursorKind, string?> register, bool enabled = true)
    {
        UiPrimitives.DrawSecondaryButton(g, t, hit, id, rect, text, fontSize: 11.5f, enabled: enabled);
        if (enabled) register(id, rect, onClick, UiCursorKind.Hand, text);
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (leftDown && _scroll.DragMove(y)) return true;
        return false;
    }

    public bool OnPointerDown(UiPointerEventArgs e, string? targetId)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (targetId == "st.scroll" && _scroll.BeginDrag(e.Y))
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
        if (clickId == null) return false;
        if (!_clicks.TryGetValue(clickId, out var click)) return false;
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aliasEditor.Dispose();
        if (_subscribedUpdates)
        {
            actions.Host.Updates.Changed -= OnUpdatesChanged;
            _subscribedUpdates = false;
        }
    }

    private void OpenLanguageCombo(UiRect anchor)
    {
        var choices = L10n.SupportedLanguages.Select(l => new ComboChoice(l.Code, l.NativeName)).ToList();
        actions.ShowCombo(anchor, choices, L10n.CurrentLanguage, picked =>
        {
            if (string.Equals(picked.Key, L10n.CurrentLanguage, StringComparison.OrdinalIgnoreCase)) return;
            actions.Config.Language = picked.Key;
            if (!actions.CommitSettings(notify: true)) return;
            L10n.Instance.ApplyImportedLanguage(picked.Key);
            actions.RefreshAll();
            actions.ShowNotice(L10n.T("St.LangApplied"));
        });
    }

    private string FixedAppLabel(AudioAppInfo app) =>
        !string.IsNullOrWhiteSpace(app.ProcessName) && actions.Config.AppNames.TryGetValue(app.ProcessName, out var alias) &&
        !string.IsNullOrWhiteSpace(alias) ? alias : app.Label;

    private void OpenFixedAppCombo(UiRect anchor)
    {
        var apps = _apps;
        var choices = apps.Select(app => new ComboChoice(app.Identity, FixedAppLabel(app))).ToArray();
        var selected = Array.Find(apps, app => string.Equals(app.ProcessName, actions.Config.FixedAppName, StringComparison.OrdinalIgnoreCase));
        actions.ShowCombo(anchor, choices, selected?.Identity ?? "", picked =>
        {
            if (!string.Equals(actions.Config.DefaultAppMode, "fixed", StringComparison.OrdinalIgnoreCase)) return;
            var app = Array.Find(apps, candidate => candidate.Identity == picked.Key);
            actions.Config.FixedAppName = app?.ProcessName ?? "";
            actions.CommitSettings(notify: true);
        });
    }
    private void OnAuthorLabelClicked()
    {
        var cfg = actions.Config;
        if (cfg.ExperimentalUnlocked && cfg.ExperimentalMode) return;
        _authorClicks++;
        if (_authorClicks >= 5)
        {
            _authorClicks = 0;
            cfg.ExperimentalUnlocked = true;
            cfg.ExperimentalMode = true;
            if (!actions.CommitSettings(notify: true)) return;
            actions.ShowNotice(L10n.T("St.EggDone"));
        }
        else
        {
            actions.ShowNotice(L10n.T("St.Egg" + _authorClicks.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private void ExportLanguages()
    {
        if (_languageFilesBusy) return;
        string? dir = NativeDialogs.BrowseFolder(actions.OwnerWindow.Handle, L10n.T("Exp.ExportLang"));
        if (dir == null) return;
        RunLanguageOperation(() =>
        {
            bool ok = L10n.ExportBuiltinLanguages(dir);
            return () =>
            {
                if (ok) ShellOpen.Folder(dir);
                actions.ShowNotice(L10n.T(ok ? "Exp.LangExportDone" : "Exp.LangExportFail"));
            };
        }, "Exp.LangExportFail");
    }

    private void ImportLanguage()
    {
        if (!_languageFilesBusy)
            ImportLanguages(NativeDialogs.OpenMultiple(actions.OwnerWindow.Handle, "JSON (*.json)|*.json", L10n.T("Exp.ImportLang")));
    }

    private void ImportLanguages(string[] paths)
    {
        if (_languageFilesBusy || paths.Length == 0) return;
        RunLanguageOperation(() =>
        {
            int ok = 0, lastCoverage = 100;
            string? last = null;
            foreach (string path in paths)
            {
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                var result = L10n.ImportLanguageFile(path);
                if (!result.Ok) continue;
                ok++;
                last = result.Code;
                lastCoverage = result.CoveragePct;
            }
            return () =>
            {
                if (last == null) { actions.ShowNotice(L10n.T("Exp.LangImportFail")); return; }
                actions.Config.Language = last;
                if (!actions.CommitSettings(notify: true)) return;
                L10n.Instance.ApplyImportedLanguage(last);
                actions.RefreshAll();
                string message = paths.Length == 1 && lastCoverage < 80
                    ? string.Format(L10n.T("Exp.LangCoverageLow"), lastCoverage)
                    : ok < paths.Length ? string.Format(L10n.T("Exp.LangImportPartial"), ok, paths.Length - ok)
                    : L10n.T("Exp.LangImportDone");
                actions.ShowNotice(message);
            };
        }, "Exp.LangImportFail");
    }

    private void RestoreLanguages()
    {
        RunLanguageOperation(async () =>
        {
            var res = await L10n.RestoreBuiltinLanguagesAsync().ConfigureAwait(false);
            return () =>
            {
                if (res.Restored > 0)
                {
                    L10n.Instance.ApplyImportedLanguage(actions.Config.Language);
                    actions.RefreshAll();
                    actions.ShowNotice(string.Format(L10n.T(res.Ok ? "Exp.LangRestored" : "Exp.LangRestorePartial"), res.Restored));
                }
                else actions.ShowNotice(L10n.T(res.Ok ? "Exp.LangNoOverride" : "Exp.LangRestoreFail"));
            };
        }, "Exp.LangRestoreFail");
    }

    private void RunLanguageOperation(Func<Action> work, string errorKey) =>
        RunLanguageOperation(() => Task.Run(work), errorKey);

    private async void RunLanguageOperation(Func<Task<Action>> work, string errorKey)
    {
        if (_languageFilesBusy) return;
        _languageFilesBusy = true;
        actions.Invalidate();
        Action done;
        try { done = await work().ConfigureAwait(false); }
        catch (Exception e)
        {
            ExceptionDiagnostics.Record("Native.LanguageFiles", e);
            done = () => actions.ShowNotice(L10n.T(errorKey));
        }
        actions.Host.TryPost(() =>
        {
            _languageFilesBusy = false;
            if (_disposed) return;
            done();
            actions.Invalidate();
        });
    }

    private void OnUpdatesChanged(string? notes)
    {
        if (_disposed) return;
        actions.Host.TryPost(() =>
        {
            if (_disposed) return;
            if (notes != null) _updateNotes = notes;
            actions.Invalidate();
        });
    }

    private void CheckForUpdates()
    {
        var updates = actions.Host.Updates;
        if (updates.IsPackaged)
        {
            OpenUrl(BuildIdentity.Edition == "Lite"
                ? "ms-windows-store://updates"
                : "ms-windows-store://pdp/?ProductId=9NQZGRTPM1NT");
            return;
        }
        _ = updates.CheckAsync(true);
    }

    private async void InstallReadyUpdate()
    {
        if (!NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("Upd.Title"), L10n.T("Upd.InstallConfirm")))
            return;
        try
        {
            if (await actions.Host.Updates.InstallAsync()) actions.Host.ExitForUpdate();
        }
        catch (Exception ex)
        {
            ExceptionDiagnostics.Record("SettingsPageView.InstallUpdate", ex);
            if (!_disposed)
                actions.Host.TryPost(() => actions.ShowNotice(L10n.T("Upd.Failed")));
        }
    }

    private async Task SyncStoreAutoStartAsync(int version)
    {
        try
        {
            var task = await StoreStartup.GetHandleAsync();
            if (_disposed || _autoStartBusy || version != _autoStartSyncVersion) return;
            int state = task.State;
            bool enabled = state is 2 or 4;
            if (actions.Config.AutoStartStore != enabled)
            {
                actions.Config.AutoStartStore = enabled;
                actions.CommitSettings(notify: true);
            }
            actions.Invalidate();
        }
        catch
        {
            if (!_disposed && !_autoStartBusy && version == _autoStartSyncVersion)
                actions.ShowNotice(L10n.T("St.AutoStartFail"));
        }
    }

    private async void ToggleAutoStart(bool enable)
    {
        bool packaged = actions.Host.EnvironmentInfo.IsPackaged;
        _autoStartBusy = true;
        ++_autoStartSyncVersion;
        actions.Invalidate();
        try
        {
            if (packaged)
            {
                var task = await StoreStartup.GetHandleAsync();
                int state = enable ? await task.RequestEnableAsync() : task.State;
                if (!enable)
                {
                    task.Disable();
                    state = task.State;
                }
                bool enabled = state is 2 or 4;
                actions.Config.AutoStartStore = enabled;
                actions.CommitSettings(notify: true);
                if (enable && !enabled)
                {
                    string key = state == 1 ? "St.AutoStartDisabledByUser"
                        : state == 3 ? "St.AutoStartDisabledByPolicy"
                        : "St.AutoStartFail";
                    actions.ShowNotice(L10n.T(key));
                }
            }
            else
            {
                actions.Host.SetAutoStart(enable);
                actions.Config.AutoStart = actions.Host.AutoStartEnabled;
                actions.CommitSettings(notify: true);
            }
        }
        catch
        {
            if (!_disposed) actions.ShowNotice(L10n.T("St.AutoStartFail"));
        }
        finally
        {
            _autoStartBusy = false;
            if (!_disposed) actions.Invalidate();
        }
    }

    private async void CleanAutoStart()
    {
        if (_autoStartBusy) return;
        _autoStartBusy = true;
        ++_autoStartSyncVersion;
        actions.Invalidate();
        try
        {
            if (actions.Host.EnvironmentInfo.IsPackaged)
            {
                var task = await StoreStartup.GetHandleAsync();
                task.Disable();
                actions.Config.AutoStartStore = task.State is 2 or 4;
            }
            else
            {
                actions.Host.SetAutoStart(false);
                actions.Config.AutoStart = false;
            }
            if (!actions.CommitSettings(notify: true)) return;
            if (!_disposed) actions.ShowNotice(L10n.T("St.CleanAutoStartDone"));
        }
        catch
        {
            if (!_disposed) actions.ShowNotice(L10n.T("St.CleanAutoStartFail"));
        }
        finally
        {
            _autoStartBusy = false;
            if (!_disposed) actions.Invalidate();
        }
    }

    private void ShowLicense()
    {
        try
        {
            using var stream = typeof(SettingsPageView).Assembly.GetManifestResourceStream("SonicRoute.License");
            if (stream == null) return;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            actions.ShowProjectLicense("SonicRoute\r\nCopyright (c) 2026 kunkunkunQoQ\r\nGPL-3.0-only\r\n\r\n" + reader.ReadToEnd());
        }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private List<AudioDeviceInfo> BuildDeviceList(Endpoint[] endpoints, Flow flow, bool includeSystemDefault)
    {
        var list = new List<AudioDeviceInfo>(endpoints.Length + 1);
        if (includeSystemDefault)
        {
            list.Add(new AudioDeviceInfo
            {
                Id = flow == Flow.Render ? AudioService.SystemDefaultDeviceId : AudioService.SystemDefaultInputDeviceId,
                DisplayName = L10n.T(flow == Flow.Render ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn"),
                Flow = (SonicRoute.Core.Interop.EDataFlow)flow
            });
        }
        foreach (var e in endpoints)
        {
            list.Add(new AudioDeviceInfo { Id = e.Id, DisplayName = e.Name, Flow = (SonicRoute.Core.Interop.EDataFlow)flow });
        }
        return list;
    }

    private string GetDeviceTitle(AudioDeviceInfo device, string original) =>
        actions.Config.DeviceNames.TryGetValue(device.Id, out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : string.IsNullOrWhiteSpace(original) ? L10n.T("Dev.Unknown") : original;

    private static (string Title, string Detail) SplitDeviceName(AudioDeviceInfo device)
    {
        string full = device.DisplayName ?? "";
        int split = full.IndexOf(" (", StringComparison.Ordinal);
        if (split > 0 && full.EndsWith(")", StringComparison.Ordinal))
            return (full[..split], full.Substring(split + 2, full.Length - split - 3));
        return (full, "");
    }

    private static UiIcon DeviceIcon(AudioDeviceInfo device)
    {
        string full = device.DisplayName ?? "";
        if (device.Flow == EDataFlow.eCapture) return UiIcon.Microphone;
        if (device.Id == AudioService.SystemDefaultDeviceId || full.Contains("HDMI", StringComparison.OrdinalIgnoreCase))
            return UiIcon.Monitor;
        if (full.Contains("head", StringComparison.OrdinalIgnoreCase) || full.Contains("耳机") || full.Contains("耳機") || full.Contains("蓝牙"))
            return UiIcon.Headphones;
        return UiIcon.Speaker;
    }

}
