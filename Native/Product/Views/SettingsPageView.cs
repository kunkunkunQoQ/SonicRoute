using System;
using System.Collections.Generic;
using System.Diagnostics;
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

internal sealed class SettingsPageView(IViewHostActions actions) : IViewPage
{
    private readonly UiScrollState _scroll = new();
    private readonly Dictionary<string, UiRect> _rects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _clicks = new(StringComparer.Ordinal);

    private Endpoint[] _outputs = [];
    private Endpoint[] _inputs = [];
    private AudioAppInfo[] _apps = [];
    private bool _loadingDevices;
    private bool _devicesLoaded;
    private bool _keepDevicesExpanded;
    private bool _deviceNamesExpanded;
    private bool _langMoreExpanded;
    private bool _draggingVolumeStep;
    private UiRect _volumeStepSliderRect;
    private UiRect _contentRect;
    private int _authorClicks;
    private string _updateStatus = "";
    private bool _disposed;

    public void OnActivated()
    {
        _keepDevicesExpanded = !actions.Config.CollapseDeviceSections;
        _deviceNamesExpanded = !actions.Config.CollapseDeviceSections;
        RefreshData();
    }

    public void OnDeactivated()
    {
        _draggingVolumeStep = false;
        actions.CloseInlineEdit(commit: true);
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
                apps = actions.Host.Audio.Apps().ToArray();
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

        void RegisterClick(string id, UiRect r, Action onClick, UiCursorKind cursor = UiCursorKind.Hand, string? tooltip = null)
        {
            _rects[id] = r;
            _clicks[id] = onClick;
            if (r.Bottom > viewport.Y && r.Y < viewport.Bottom)
            {
                var clipped = ClipToViewport(r, viewport);
                if (!clipped.IsEmpty) hit.Add(id, clipped, cursor, tooltip);
            }
        }

        g.PushClip(viewport);

        // Page Header
        g.DrawText(L10n.T("St.Settings"), new UiRect(x, y, w, 30f), t.TextPrimary, 22f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        y += 33f;
        g.DrawText(L10n.T("Settings.PageHint"), new UiRect(x, y, w, 20f), t.TextSecondary, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        y += 34f;

        // 1. General / Language & Startup Card
        y = RenderGeneralStartupCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick);
        y += 14f;

        // 2. Default App Selection Card
        y = RenderDefaultAppCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick);
        y += 14f;

        // 3. Audio & Volume + Behavior Switches Card
        y = RenderAudioBehaviorCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick);
        y += 14f;

        // 4. Device Visibility Card
        y = RenderDeviceVisibilityCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick);
        y += 14f;

        // 5. Device Names Card
        y = RenderDeviceNamesCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick);
        y += 14f;

        // 6. Config & Maintenance Card
        y = RenderMaintenanceCard(g, t, hit, viewport, x, y, w, RegisterClick);
        y += 14f;

        // 7. About & Updates Card
        y = RenderAboutCard(g, t, hit, viewport, x, y, w, cfg, RegisterClick);
        y += 16f;

        g.PopClip();

        _scroll.ContentHeight = MathF.Max(0f, y - startY);
        _scroll.Clamp();
        _scroll.Draw(g, t, hit, "st.scroll", viewport);
    }

    private float RenderGeneralStartupCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        float pad = 16f;
        float innerW = w - pad * 2f;
        var langs = L10n.SupportedLanguages;
        int curLangIdx = Array.FindIndex(langs, l => string.Equals(l.Code, L10n.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
        string curLangLabel = langs.Length > 0 ? langs[Math.Max(0, curLangIdx)].NativeName : L10n.CurrentLanguage;

        float langMoreH = _langMoreExpanded ? 46f : 0f;
        float cardH = pad + 28f + 12f + 36f + langMoreH + 8f + 54f * 3f + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, 28f), UiIcon.Message, L10n.T("St.Language") + " · " + L10n.T("St.StartupSection"));
        cy += 38f;

        // Language ComboBox + MoreOptions button
        float comboW = MathF.Min(240f, innerW - 130f);
        var comboRect = new UiRect(x + pad, cy, comboW, 34f);
        UiPrimitives.DrawComboBox(g, t, hit, "st.lang.combo", comboRect, curLangLabel);
        register("st.lang.combo", comboRect, () => OpenLanguageCombo(comboRect), UiCursorKind.Hand, L10n.T("St.Language"));

        var langMoreRect = new UiRect(comboRect.Right + 12f, cy + 3f, 110f, 28f);
        register("st.lang.more", langMoreRect, () => { _langMoreExpanded = !_langMoreExpanded; actions.Invalidate(); }, UiCursorKind.Hand, null);
        bool langMoreHot = hit.IsHover("st.lang.more");
        g.DrawText(L10n.T("Exp.MoreOptions"), new UiRect(langMoreRect.X, langMoreRect.Y, 84f, langMoreRect.Height),
            langMoreHot ? t.AccentHover : t.Accent, 12f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(langMoreRect.X + 86f, langMoreRect.CenterY - 5f, 10f, 10f),
            t.Accent, 1.8f, _langMoreExpanded ? 180f : 0f);
        cy += 42f;

        if (_langMoreExpanded)
        {
            float btnX = x + pad;
            float btnW = MathF.Min(138f, (innerW - 24f) / 4f);
            DrawToolbarAction(g, t, hit, "st.lang.export", new UiRect(btnX, cy, btnW, 32f), L10n.T("Exp.ExportLang"), ExportLanguages, register);
            btnX += btnW + 8f;
            DrawToolbarAction(g, t, hit, "st.lang.import", new UiRect(btnX, cy, btnW, 32f), L10n.T("Exp.ImportLang"), ImportLanguage, register);
            btnX += btnW + 8f;
            DrawToolbarAction(g, t, hit, "st.lang.dir", new UiRect(btnX, cy, btnW, 32f), L10n.T("Exp.OpenLangDir"), () =>
            {
                if (!L10n.OpenExternalLangDir()) actions.ShowNotice(L10n.T("Exp.LangOpenFail"));
            }, register);
            btnX += btnW + 8f;
            DrawToolbarAction(g, t, hit, "st.lang.restore", new UiRect(btnX, cy, btnW, 32f), L10n.T("Exp.RestoreLang"), RestoreLanguages, register);
            cy += 46f;
        }

        // Auto-start switch
        bool autoStart = actions.Host.PreviewAutoStart;
        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.autostart", new UiRect(x + pad, cy, innerW, 54f),
            UiIcon.Power, L10n.T("St.AutoStart"), L10n.T("Settings.AutoStartHint"), autoStart, true, () =>
            {
                bool next = !actions.Host.PreviewAutoStart;
                try
                {
                    actions.Host.SetPreviewAutoStart(next);
                    actions.Invalidate();
                }
                catch
                {
                    actions.ShowNotice(L10n.T("St.AutoStartFail"));
                }
            }, register);

        // Start minimized
        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.startmin", new UiRect(x + pad, cy, innerW, 54f),
            UiIcon.Monitor, L10n.T("St.StartMinimized"), L10n.T("Settings.StartMinimizedHint"), cfg.StartMinimized, true, () =>
            {
                cfg.StartMinimized = !cfg.StartMinimized;
                SaveConfig();
            }, register);

        // Start panel on start
        DrawSettingsSwitchWithHint(g, t, hit, "st.startpanel", new UiRect(x + pad, cy, innerW, 54f),
            UiIcon.Application, L10n.T("St.StartPanel"), L10n.T("Settings.StartPanelHint"), cfg.StartPanelOnStart, false, () =>
            {
                cfg.StartPanelOnStart = !cfg.StartPanelOnStart;
                SaveConfig();
            }, register);

        return y + cardH;
    }

    private float RenderDefaultAppCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        float pad = 16f;
        float innerW = w - pad * 2f;
        bool isFixed = string.Equals(cfg.DefaultAppMode, "fixed", StringComparison.OrdinalIgnoreCase);
        float cardH = pad + 28f + 22f + 86f + 10f + 34f + 22f + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, 28f), UiIcon.Apps, L10n.T("St.DefaultApp"));
        cy += 28f;
        g.DrawText(L10n.T("St.DefaultAppHint"), new UiRect(x + pad + 32f, cy, innerW - 32f, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        cy += 24f;

        float gap = 8f;
        float colW = (innerW - gap * 2f) / 3f;
        DrawAppChoiceCard(g, t, hit, "st.appmode.recent", new UiRect(x + pad, cy, colW, 82f),
            UiIcon.Clock, L10n.T("St.DefaultAppRecent"), L10n.T("Settings.RecentHint"),
            string.Equals(cfg.DefaultAppMode, "recent", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(cfg.DefaultAppMode),
            () => { cfg.DefaultAppMode = "recent"; SaveConfig(); }, register);

        DrawAppChoiceCard(g, t, hit, "st.appmode.last", new UiRect(x + pad + colW + gap, cy, colW, 82f),
            UiIcon.History, L10n.T("St.DefaultAppLast"), L10n.T("Settings.LastHint"),
            string.Equals(cfg.DefaultAppMode, "last", StringComparison.OrdinalIgnoreCase),
            () => { cfg.DefaultAppMode = "last"; SaveConfig(); }, register);

        DrawAppChoiceCard(g, t, hit, "st.appmode.fixed", new UiRect(x + pad + (colW + gap) * 2f, cy, colW, 82f),
            UiIcon.Apps, L10n.T("St.DefaultAppFixed"), L10n.T("Settings.FixedHint"),
            isFixed,
            () => { cfg.DefaultAppMode = "fixed"; SaveConfig(); }, register);
        cy += 92f;

        // Fixed app picker + inline edit fallback
        float pickW = MathF.Min(260f, innerW - 140f);
        var fixedComboRect = new UiRect(x + pad, cy, pickW, 34f);
        string fixedDisplay = string.IsNullOrWhiteSpace(cfg.FixedAppName) ? L10n.T("Ov.Unset") : cfg.FixedAppName;
        UiPrimitives.DrawComboBox(g, t, hit, "st.fixedapp.combo", fixedComboRect, fixedDisplay, enabled: isFixed);
        if (isFixed)
        {
            register("st.fixedapp.combo", fixedComboRect, () => OpenFixedAppCombo(fixedComboRect), UiCursorKind.Hand, L10n.T("St.DefaultAppFixed"));
        }

        var fixedBoxRect = new UiRect(fixedComboRect.Right + 8f, cy, MathF.Max(90f, innerW - pickW - 8f), 34f);
        if (isFixed)
        {
            UiPrimitives.DrawTextBox(g, t, hit, "st.fixedapp.edit", fixedBoxRect, cfg.FixedAppName,
                L10n.T("Auto.TargetApp"), focused: actions.ActiveInlineEditTag == "st.fixedapp.edit");
            register("st.fixedapp.edit", fixedBoxRect, () =>
            {
                actions.StartInlineEdit("st.fixedapp.edit", fixedBoxRect, cfg.FixedAppName, null, (text, commit) =>
                {
                    if (!commit) return;
                    cfg.FixedAppName = text.Trim();
                    SaveConfig();
                });
            }, UiCursorKind.IBeam, L10n.T("St.DefaultAppFixed"));
        }
        else
        {
            g.FillRoundedRect(fixedBoxRect, 6f, t.SurfaceAlt);
            g.DrawRoundedRect(fixedBoxRect, 6f, t.Border, 1f);
            g.DrawText(cfg.FixedAppName, fixedBoxRect.Deflate(9f, 2f), t.TextSecondary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        }

        cy += 38f;
        g.DrawText(L10n.T("St.FixedAppHint"), new UiRect(x + pad, cy, innerW, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        return y + cardH;
    }

    private float RenderAudioBehaviorCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        float pad = 16f;
        float innerW = w - pad * 2f;
        int switchCount = 8 + (cfg.ExperimentalMic ? 1 : 0) + (cfg.ExperimentalUnlocked ? 1 : 0);
        float volRowH = 58f;
        float rowH = 54f;
        float cardH = pad + 28f + 8f + volRowH + switchCount * rowH + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, 28f), UiIcon.Settings, L10n.T("St.Misc"));
        cy += 36f;

        // Volume Step row (slider + numeric box)
        var volRect = new UiRect(x + pad, cy, innerW, volRowH);
        g.DrawIcon(UiIcon.Speaker, new UiRect(volRect.X + 2f, volRect.CenterY - 8.5f, 17f, 17f), t.TextSecondary, 1.8f);
        float textW = MathF.Max(120f, innerW - 32f - 230f);
        g.DrawText(L10n.T("St.VolumeStep"), new UiRect(volRect.X + 32f, volRect.Y + 6f, textW, 20f), t.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(L10n.T("St.VolumeStepHint"), new UiRect(volRect.X + 32f, volRect.Y + 26f, textW, 26f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: true);

        int clampedStep = Math.Clamp(cfg.VolumeStep, 1, 20);
        var sliderRect = new UiRect(volRect.Right - 220f, volRect.CenterY - 12f, 146f, 24f);
        _volumeStepSliderRect = sliderRect;
        _rects["st.volstep.slider"] = sliderRect;
        float val01 = (clampedStep - 1) / 19f;
        UiPrimitives.DrawVolumeSlider(g, t, hit, "st.volstep.slider", sliderRect, val01, 0f, false, false);
        if (sliderRect.Bottom > viewport.Y && sliderRect.Y < viewport.Bottom)
        {
            var clipped = ClipToViewport(sliderRect, viewport);
            if (!clipped.IsEmpty) hit.Add("st.volstep.slider", clipped, UiCursorKind.Hand);
        }

        var stepBoxRect = new UiRect(volRect.Right - 64f, volRect.CenterY - 15f, 64f, 30f);
        UiPrimitives.DrawTextBox(g, t, hit, "st.volstep.box", stepBoxRect, clampedStep.ToString(CultureInfo.InvariantCulture) + "%",
            focused: actions.ActiveInlineEditTag == "st.volstep.box");
        register("st.volstep.box", stepBoxRect, () =>
        {
            actions.StartInlineEdit("st.volstep.box", stepBoxRect, clampedStep.ToString(CultureInfo.InvariantCulture), null, (text, commit) =>
            {
                if (!commit) return;
                if (int.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                {
                    cfg.VolumeStep = Math.Clamp(v, 1, 20);
                    SaveConfig();
                }
            });
        }, UiCursorKind.IBeam, L10n.T("St.VolumeStep"));
        g.DrawLine(volRect.X + 2f, volRect.Bottom, volRect.Right - 2f, volRect.Bottom, t.Border, 1f, roundCaps: false);
        cy += volRowH;

        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.traywheel.everywhere", new UiRect(x + pad, cy, innerW, rowH),
            UiIcon.Speaker, L10n.T("St.TrayWheelEverywhere"), L10n.T("St.TrayWheelEverywhereHint"), cfg.TrayWheelEverywhere, true, () =>
            {
                cfg.TrayWheelEverywhere = !cfg.TrayWheelEverywhere;
                SaveConfig();
            }, register);

        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.traywheel.paused", new UiRect(x + pad, cy, innerW, rowH),
            UiIcon.Pause, L10n.T("Tray.PauseWheel"), L10n.T("Tray.PauseWheelHint"), cfg.TrayWheelPaused, true, () =>
            {
                cfg.TrayWheelPaused = !cfg.TrayWheelPaused;
                SaveConfig();
            }, register);

        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.expmic", new UiRect(x + pad, cy, innerW, rowH),
            UiIcon.Microphone, L10n.T("St.ExperimentalMic"), L10n.T("St.ExperimentalMicHint"), cfg.ExperimentalMic, true, () =>
            {
                cfg.ExperimentalMic = !cfg.ExperimentalMic;
                SaveConfig();
                actions.ShowNotice(L10n.T("St.ExpMicNeedRestart"));
            }, register);

        if (cfg.ExperimentalMic)
        {
            cy = DrawSettingsSwitchWithHint(g, t, hit, "st.micinpanel", new UiRect(x + pad + 20f, cy, innerW - 20f, rowH),
                UiIcon.Microphone, L10n.T("Exp.MicPanel"), "", cfg.MicInPanel, true, () =>
                {
                    cfg.MicInPanel = !cfg.MicInPanel;
                    SaveConfig();
                    actions.ShowNotice(L10n.T("St.ExpMicPanelNeedRestart"));
                }, register);
        }

        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.panelchangesysdef", new UiRect(x + pad, cy, innerW, rowH),
            UiIcon.Switch, L10n.T("St.PanelChangeSystemDefault"), L10n.T("St.PanelChangeSystemDefaultHint"), cfg.PanelChangeSystemDefault, true, () =>
            {
                cfg.PanelChangeSystemDefault = !cfg.PanelChangeSystemDefault;
                SaveConfig();
            }, register);

        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.panelretainedonly", new UiRect(x + pad, cy, innerW, rowH),
            UiIcon.Speaker, L10n.T("St.PanelOutputVolumesRetainedOnly"), L10n.T("St.PanelOutputVolumesRetainedOnlyHint"), cfg.PanelOutputVolumesRetainedOnly, true, () =>
            {
                cfg.PanelOutputVolumesRetainedOnly = !cfg.PanelOutputVolumesRetainedOnly;
                SaveConfig();
            }, register);

        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.micmute.persist", new UiRect(x + pad, cy, innerW, rowH),
            UiIcon.Microphone, L10n.T("Theme.MicMuteOsdPersist"), "", cfg.MicMuteOsdPersistent, true, () =>
            {
                cfg.MicMuteOsdPersistent = !cfg.MicMuteOsdPersistent;
                SaveConfig();
            }, register);

        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.micmute.trackdef", new UiRect(x + pad, cy, innerW, rowH),
            UiIcon.Microphone, L10n.T("Theme.MicMuteTrackInput"), "", cfg.MicMuteOsdDefaultInputPersistent, true, () =>
            {
                cfg.MicMuteOsdDefaultInputPersistent = !cfg.MicMuteOsdDefaultInputPersistent;
                SaveConfig();
            }, register);

        bool drawCollapseBottom = cfg.ExperimentalUnlocked;
        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.collapsedev", new UiRect(x + pad, cy, innerW, rowH),
            UiIcon.Options, L10n.T("Exp.Collapse"), L10n.T("Exp.CollapseHint"), cfg.CollapseDeviceSections, drawCollapseBottom, () =>
            {
                cfg.CollapseDeviceSections = !cfg.CollapseDeviceSections;
                _keepDevicesExpanded = !cfg.CollapseDeviceSections;
                _deviceNamesExpanded = !cfg.CollapseDeviceSections;
                SaveConfig();
            }, register);

        if (cfg.ExperimentalUnlocked)
        {
            DrawSettingsSwitchWithHint(g, t, hit, "st.expmode", new UiRect(x + pad, cy, innerW, rowH),
                UiIcon.Experiment, L10n.T("St.ExperimentalMode"), L10n.T("St.ExperimentalModeHint"), cfg.ExperimentalMode, false, () =>
                {
                    cfg.ExperimentalMode = !cfg.ExperimentalMode;
                    SaveConfig();
                    actions.ShowNotice(L10n.T("St.ExpModeNeedRestart"));
                }, register);
        }

        return y + cardH;
    }

    private float RenderDeviceVisibilityCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        float pad = 16f;
        float innerW = w - pad * 2f;
        const float itemW = 184f;
        const float itemH = 46f;
        const float gap = 8f;
        int cols = Math.Max(1, (int)MathF.Floor((innerW + gap) / (itemW + gap)));

        var outDevices = BuildDeviceList(_outputs, Flow.Render, includeSystemDefault: false);
        var inDevices = cfg.ExperimentalMic ? BuildDeviceList(_inputs, Flow.Capture, includeSystemDefault: false) : [];

        int outRows = outDevices.Count == 0 ? 1 : (int)MathF.Ceiling(outDevices.Count / (float)cols);
        int inRows = inDevices.Count == 0 ? 0 : (int)MathF.Ceiling(inDevices.Count / (float)cols);

        float bodyH = 0f;
        if (_keepDevicesExpanded)
        {
            bodyH += 30f + outRows * (itemH + gap);
            if (cfg.ExperimentalMic)
                bodyH += 10f + 30f + Math.Max(1, inRows) * (itemH + gap);
        }

        float headerH = 48f;
        float cardH = pad + headerH + bodyH + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW - 110f, 26f), UiIcon.Monitor, L10n.T("Settings.KeepTitle"));
        g.DrawText(L10n.T("Settings.KeepHint"), new UiRect(x + pad + 32f, cy + 26f, innerW - 110f, 20f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        var toggleRect = new UiRect(x + w - pad - 102f, cy + 2f, 102f, 26f);
        register("st.keep.toggle", toggleRect, () => { _keepDevicesExpanded = !_keepDevicesExpanded; actions.Invalidate(); }, UiCursorKind.Hand, L10n.T("Exp.CollapseTip"));
        g.DrawText(L10n.T("Exp.MoreOptions"), new UiRect(toggleRect.X, toggleRect.Y, 80f, toggleRect.Height),
            hit.IsHover("st.keep.toggle") ? t.AccentHover : t.Accent, 11.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(toggleRect.Right - 14f, toggleRect.CenterY - 4.5f, 9f, 9f),
            t.Accent, 1.8f, _keepDevicesExpanded ? 180f : 0f);

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
                SaveConfig();
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
                    SaveConfig();
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

            string cardId = $"st.devchk.{prefix}.{i}";
            string editId = $"st.devchk.edit.{prefix}.{i}";

            bool hot = hit.IsHover(cardId) || hit.IsHover(editId);
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
            float txtW = MathF.Max(20f, cardRect.Width - 76f);
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
            var toggleArea = new UiRect(cardRect.X, cardRect.Y, cardRect.Width - 24f, cardRect.Height);
            register(cardId, toggleArea, () =>
            {
                if (hiddenList.Contains(dev.Id, StringComparer.OrdinalIgnoreCase))
                    hiddenList.RemoveAll(id => string.Equals(id, dev.Id, StringComparison.OrdinalIgnoreCase));
                else
                    hiddenList.Add(dev.Id);
                SaveConfig();
            }, UiCursorKind.Hand, dev.DisplayName);

            // Small inline rename icon button on right
            var editRect = new UiRect(cardRect.Right - 24f, cardRect.CenterY - 10f, 20f, 20f);
            UiPrimitives.DrawGhostIconButton(g, t, hit, editId, editRect, UiIcon.Edit, 12f, tooltip: L10n.T("St.DeviceNames"));
            register(editId, editRect, () => StartRenameDevice(dev, cardRect), UiCursorKind.Hand, L10n.T("St.DeviceNames"));
        }

        int totalRows = (int)MathF.Ceiling(devices.Count / (float)cols);
        return y + totalRows * (itemH + gap);
    }

    private float RenderDeviceNamesCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        float pad = 16f;
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

        float headerH = 48f;
        float cardH = pad + headerH + bodyH + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW - 110f, 26f), UiIcon.Label, L10n.T("St.DeviceNames"));
        g.DrawText(L10n.T("St.DeviceNamesHint"), new UiRect(x + pad + 32f, cy + 26f, innerW - 110f, 20f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        var toggleRect = new UiRect(x + w - pad - 102f, cy + 2f, 102f, 26f);
        register("st.names.toggle", toggleRect, () => { _deviceNamesExpanded = !_deviceNamesExpanded; actions.Invalidate(); }, UiCursorKind.Hand, L10n.T("Exp.CollapseTip"));
        g.DrawText(L10n.T("Exp.MoreOptions"), new UiRect(toggleRect.X, toggleRect.Y, 80f, toggleRect.Height),
            hit.IsHover("st.names.toggle") ? t.AccentHover : t.Accent, 11.5f, 400, UiTextAlign.Trailing, UiVertAlign.Center);
        g.DrawIcon(UiIcon.ChevronDown, new UiRect(toggleRect.Right - 14f, toggleRect.CenterY - 4.5f, 9f, 9f),
            t.Accent, 1.8f, _deviceNamesExpanded ? 180f : 0f);

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

            string boxId = $"st.devname.{prefix}.{i}";
            float boxW = MathF.Max(120f, w * 0.48f);
            var boxRect = new UiRect(rowRect.Right - 10f - boxW, rowRect.CenterY - 15f, boxW, 30f);
            string alias = cfg.DeviceNames.TryGetValue(dev.Id, out var saved) ? saved : "";
            UiPrimitives.DrawTextBox(g, t, hit, boxId, boxRect, alias, title, focused: actions.ActiveInlineEditTag == boxId);
            register(boxId, boxRect, () => StartRenameDevice(dev, boxRect), UiCursorKind.IBeam, dev.DisplayName);

            y += rowH + rowGap;
        }
        return y;
    }

    private float RenderMaintenanceCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        float pad = 16f;
        float innerW = w - pad * 2f;
        float cardH = pad + 28f + 22f + 36f + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW, 28f), UiIcon.File, L10n.T("Exp.ConfigFiles"));
        cy += 28f;
        g.DrawText(L10n.T("Exp.ConfigTransferHint"), new UiRect(x + pad + 32f, cy, innerW - 32f, 18f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        cy += 22f;

        float gap = 8f;
        float btnW = MathF.Max(90f, (innerW - gap * 3f) / 4f);
        float bx = x + pad;

        var expRect = new UiRect(bx, cy, btnW, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "st.cfg.export", expRect, L10n.T("Exp.ExportConfig"), icon: UiIcon.File);
        register("st.cfg.export", expRect, ExportConfig, UiCursorKind.Hand, L10n.T("Exp.ExportConfig"));
        bx += btnW + gap;

        var impRect = new UiRect(bx, cy, btnW, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "st.cfg.import", impRect, L10n.T("Exp.ImportConfig"), icon: UiIcon.Refresh);
        register("st.cfg.import", impRect, ImportConfig, UiCursorKind.Hand, L10n.T("Exp.ImportConfig"));
        bx += btnW + gap;

        var openLabel = IsZh() ? "打开数据目录" : "Open Data Folder";
        var dirRect = new UiRect(bx, cy, btnW, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "st.cfg.opendir", dirRect, openLabel, icon: UiIcon.Apps);
        register("st.cfg.opendir", dirRect, () =>
        {
            try
            {
                Directory.CreateDirectory(actions.Host.DataDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", actions.Host.DataDirectory) { UseShellExecute = true });
            }
            catch
            {
                ShellOpen.Folder(actions.Host.DataDirectory);
            }
        }, UiCursorKind.Hand, actions.Host.DataDirectory);
        bx += btnW + gap;

        var rstRect = new UiRect(bx, cy, btnW, 34f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "st.cfg.reset", rstRect, L10n.T("Exp.ClearConfig"), danger: true, icon: UiIcon.Reset);
        register("st.cfg.reset", rstRect, ResetSettings, UiCursorKind.Hand, L10n.T("Exp.ClearConfigHint"));

        return y + cardH;
    }

    private float RenderAboutCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit, UiRect viewport,
        float x, float y, float w, AppConfig cfg,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        float pad = 16f;
        float innerW = w - pad * 2f;
        float cardH = pad + 34f + 38f + 44f + 6f + 24f + pad;
        var card = new UiRect(x, y, w, cardH);
        UiPrimitives.DrawCard(g, t, card);

        float cy = y + pad;
        UiPrimitives.DrawSettingsSectionHeader(g, t, new UiRect(x + pad, cy, innerW - 140f, 28f), UiIcon.Info, L10n.T("Upd.Title"));
        UiPrimitives.DrawBadge(g, t, new UiRect(x + w - pad - 156f, cy + 3f, 156f, 22f),
            "SonicRoute 1.36 Native AOT", t.NavSelected, t.Accent, t.Accent.WithAlphaFraction(0.45f), 10.5f);
        cy += 34f;

        // Check Update + Auto Check toggle
        var chkRect = new UiRect(x + pad, cy, 128f, 32f);
        UiPrimitives.DrawSecondaryButton(g, t, hit, "st.about.checkupd", chkRect, L10n.T("St.CheckUpdate"), icon: UiIcon.Refresh);
        register("st.about.checkupd", chkRect, CheckForUpdates, UiCursorKind.Hand, L10n.T("St.CheckUpdate"));

        string statusText = string.IsNullOrEmpty(_updateStatus)
            ? string.Format(L10n.T("Upd.Current"), "1.36", BuildIdentity.Edition, "Native AOT")
            : _updateStatus;
        g.DrawText(statusText, new UiRect(chkRect.Right + 12f, cy, innerW - 140f, 32f), t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        cy += 38f;

        // Auto-check updates switch row
        cy = DrawSettingsSwitchWithHint(g, t, hit, "st.about.autochk", new UiRect(x + pad, cy, innerW, 44f),
            UiIcon.Clock, L10n.T("Upd.Automatic"), "", cfg.AutoCheckUpdates, true, () =>
            {
                cfg.AutoCheckUpdates = !cfg.AutoCheckUpdates;
                SaveConfig();
            }, register);

        cy += 6f;
        // 5-click version / author label to unlock experimental mode
        string verText = $"{L10n.T("App.About")} · SonicRoute 1.36 Native AOT";
        var verRect = new UiRect(x + pad, cy, innerW * 0.62f, 24f);
        register("st.about.author", verRect, OnAuthorLabelClicked, UiCursorKind.Hand, L10n.T("St.ExpAuthorTip"));
        g.DrawText(verText, verRect, hit.IsHover("st.about.author") ? t.Accent : t.TextSecondary, 11.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);

        // GitHub & GPL-3.0 links on the right
        var ghRect = new UiRect(x + w - pad - 130f, cy, 58f, 24f);
        register("st.about.github", ghRect, () => OpenUrl("https://github.com/kunkunkunkunkun/SonicRoute"), UiCursorKind.Hand, null);
        g.DrawText("GitHub", ghRect, hit.IsHover("st.about.github") ? t.AccentHover : t.Accent, 12f, 400, UiTextAlign.Center, UiVertAlign.Center);

        var licRect = new UiRect(x + w - pad - 66f, cy, 66f, 24f);
        register("st.about.license", licRect, ShowLicense, UiCursorKind.Hand, null);
        g.DrawText("GPL-3.0", licRect, hit.IsHover("st.about.license") ? t.AccentHover : t.Accent, 12f, 400, UiTextAlign.Center, UiVertAlign.Center);

        return y + cardH;
    }

    private static float DrawSettingsSwitchWithHint(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        string id, UiRect rect, UiIcon icon, string title, string hint, bool isChecked, bool drawBottomBorder,
        Action onClick, Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        register(id, rect, onClick, UiCursorKind.Hand, string.IsNullOrEmpty(hint) ? null : hint);
        if (string.IsNullOrEmpty(hint))
        {
            UiPrimitives.DrawSettingsSwitchRow(g, t, hit, id, rect, icon, title, isChecked, drawBottomBorder);
            return rect.Bottom;
        }
        if (hit.IsHover(id))
            g.FillRoundedRect(rect.Deflate(0f, 1f), 6f, t.NavHover.WithAlphaFraction(0.55f));

        g.DrawIcon(icon, new UiRect(rect.X + 2f, rect.CenterY - 8.5f, 17f, 17f), t.TextSecondary, 1.8f);
        float textW = MathF.Max(60f, rect.Width - 82f);
        g.DrawText(title, new UiRect(rect.X + 32f, rect.Y + 6f, textW, 18f), t.TextPrimary, 12.5f, 400, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(hint, new UiRect(rect.X + 32f, rect.Y + 26f, textW, 22f), t.TextSecondary, 11f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: false, ellipsis: true);

        UiPrimitives.DrawSwitch(g, t, hit, id + ":sw", new UiRect(rect.Right - 36f, rect.CenterY - 12f, 34f, 24f), isChecked);
        if (drawBottomBorder)
            g.DrawLine(rect.X + 2f, rect.Bottom, rect.Right - 2f, rect.Bottom, t.Border, 1f, roundCaps: false);
        return rect.Bottom;
    }

    private static void DrawAppChoiceCard(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        string id, UiRect rect, UiIcon icon, string title, string hint, bool selected,
        Action onClick, Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        register(id, rect, onClick, UiCursorKind.Hand, hint);
        bool hot = hit.IsHover(id);
        g.FillRoundedRect(rect, 8f, selected ? t.NavSelected : t.SurfaceBg);
        g.DrawRoundedRect(rect, 8f, selected || hot ? t.Accent : t.Border, 1f);

        float cx = rect.X + 16f;
        float cy = rect.Y + 19f;
        g.FillEllipse(cx, cy, 6.5f, 6.5f, t.SurfaceBg);
        g.DrawEllipse(cx, cy, 6.5f, 6.5f, selected ? t.Accent : t.Border, 1.2f);
        if (selected) g.FillEllipse(cx, cy, 3.5f, 3.5f, t.Accent);

        g.DrawIcon(icon, new UiRect(rect.X + 29f, rect.Y + 11f, 18f, 18f), selected ? t.Accent : t.TextSecondary, 1.8f);
        float textX = rect.X + 54f;
        float textW = MathF.Max(20f, rect.Right - textX - 8f);
        g.DrawText(title, new UiRect(textX, rect.Y + 8f, textW, 22f), t.TextPrimary, 12.5f, 600, UiTextAlign.Leading, UiVertAlign.Center);
        g.DrawText(hint, new UiRect(textX, rect.Y + 32f, textW, rect.Height - 38f), t.TextSecondary, 10.5f, 400, UiTextAlign.Leading, UiVertAlign.Top, wrap: true, ellipsis: true);
    }

    private static void DrawToolbarAction(
        D2dTarget g, UiThemePalette t, UiHitMap hit,
        string id, UiRect rect, string text, Action onClick,
        Action<string, UiRect, Action, UiCursorKind, string?> register)
    {
        UiPrimitives.DrawSecondaryButton(g, t, hit, id, rect, text, fontSize: 11.5f);
        register(id, rect, onClick, UiCursorKind.Hand, text);
    }

    public bool OnPointerMove(float x, float y, bool leftDown)
    {
        if (_scroll.DragMove(y)) return true;
        if (_draggingVolumeStep && leftDown)
        {
            UpdateVolumeStepFromX(x);
            return true;
        }
        return false;
    }

    public bool OnPointerDown(UiPointerEventArgs e)
    {
        if (e.Button != UiMouseButton.Left) return false;
        if (_scroll.TrackRect.Contains(e.X, e.Y) && _scroll.BeginDrag(e.Y))
            return true;

        if (_volumeStepSliderRect.Contains(e.X, e.Y) && _contentRect.Contains(e.X, e.Y))
        {
            _draggingVolumeStep = true;
            UpdateVolumeStepFromX(e.X);
            return true;
        }

        return false;
    }

    public bool OnPointerUp(UiPointerEventArgs e)
    {
        if (_scroll.Dragging)
        {
            _scroll.EndDrag();
            return true;
        }
        if (_draggingVolumeStep)
        {
            _draggingVolumeStep = false;
            SaveConfig();
            return true;
        }
        if (e.Button != UiMouseButton.Left || !_contentRect.Contains(e.X, e.Y)) return false;

        foreach (var kv in _rects)
        {
            if (kv.Value.Contains(e.X, e.Y) && _clicks.TryGetValue(kv.Key, out var action))
            {
                if (e.ClickCount >= 2 && kv.Key.StartsWith("st.devchk.", StringComparison.Ordinal) && !kv.Key.Contains(".edit.", StringComparison.Ordinal))
                {
                    // Double-click device card triggers rename
                    var parts = kv.Key.Split('.');
                    if (parts.Length == 4 && int.TryParse(parts[3], out int idx))
                    {
                        var list = parts[2] == "out"
                            ? BuildDeviceList(_outputs, Flow.Render, false)
                            : BuildDeviceList(_inputs, Flow.Capture, false);
                        if ((uint)idx < (uint)list.Count)
                        {
                            StartRenameDevice(list[idx], kv.Value);
                            return true;
                        }
                    }
                }
                action();
                return true;
            }
        }
        return false;
    }

    public bool OnMouseWheel(float x, float y, float delta, bool horizontal)
    {
        if (horizontal || !_contentRect.Contains(x, y)) return false;
        if (_volumeStepSliderRect.Contains(x, y))
        {
            actions.Config.VolumeStep = Math.Clamp(actions.Config.VolumeStep + (delta > 0 ? 1 : -1), 1, 20);
            SaveConfig();
            return true;
        }
        _scroll.OnWheel(delta);
        return true;
    }

    public bool OnKeyDown(int vk, bool ctrl, bool shift, bool alt) => false;

    public void Dispose() => _disposed = true;

    private void UpdateVolumeStepFromX(float x)
    {
        int pct = UiPrimitives.SliderValueFromX(_volumeStepSliderRect, x);
        int step = Math.Clamp(1 + (int)MathF.Round(pct / 100f * 19f), 1, 20);
        if (actions.Config.VolumeStep != step)
        {
            actions.Config.VolumeStep = step;
            actions.Invalidate();
        }
    }

    private void OpenLanguageCombo(UiRect anchor)
    {
        var choices = L10n.SupportedLanguages.Select(l => new ComboChoice(l.Code, l.NativeName)).ToList();
        actions.ShowCombo(anchor, choices, L10n.CurrentLanguage, picked =>
        {
            if (string.Equals(picked.Key, L10n.CurrentLanguage, StringComparison.OrdinalIgnoreCase)) return;
            L10n.Instance.ApplyImportedLanguage(picked.Key);
            actions.Config.Language = L10n.CurrentLanguage;
            SaveConfig();
            actions.RefreshAll();
            actions.ShowNotice(L10n.T("St.LangApplied"));
        });
    }

    private void OpenFixedAppCombo(UiRect anchor)
    {
        var choices = new List<ComboChoice> { new("", L10n.T("Ov.Unset")) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _apps)
        {
            string proc = app.ProcessName ?? "";
            if (string.IsNullOrWhiteSpace(proc) || !seen.Add(proc)) continue;
            string display = actions.Config.AppNames.TryGetValue(proc, out var alias) && !string.IsNullOrWhiteSpace(alias)
                ? alias : app.Label;
            choices.Add(new(proc, $"{display} ({proc})"));
        }
        if (!string.IsNullOrWhiteSpace(actions.Config.FixedAppName) && seen.Add(actions.Config.FixedAppName))
            choices.Add(new(actions.Config.FixedAppName, actions.Config.FixedAppName));

        actions.ShowCombo(anchor, choices, actions.Config.FixedAppName, picked =>
        {
            actions.Config.FixedAppName = picked.Key;
            SaveConfig();
        });
    }

    private void StartRenameDevice(AudioDeviceInfo device, UiRect rect)
    {
        string current = actions.Config.DeviceNames.TryGetValue(device.Id, out var name) ? name : "";
        actions.StartInlineEdit("st.rename." + device.Id, rect, current, null, (text, commit) =>
        {
            if (!commit) return;
            string trimmed = text.Trim();
            if (string.IsNullOrEmpty(trimmed)) actions.Config.DeviceNames.Remove(device.Id);
            else actions.Config.DeviceNames[device.Id] = trimmed;
            SaveConfig();
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
            SaveConfig();
            actions.ShowNotice(L10n.T("St.EggDone"));
        }
        else
        {
            actions.ShowNotice(L10n.T("St.Egg" + _authorClicks.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private void ExportLanguages()
    {
        string dir = Path.Combine(actions.Host.DataDirectory, "LangExport");
        bool ok = L10n.ExportBuiltinLanguages(dir);
        if (ok) ShellOpen.Folder(dir);
        actions.ShowNotice(L10n.T(ok ? "Exp.LangExportDone" : "Exp.LangExportFail"));
    }

    private void ImportLanguage()
    {
        string? path = NativeDialogs.Open(actions.OwnerWindow.Handle, "JSON language file|*.json|All files|*.*");
        if (path == null) return;
        var res = L10n.ImportLanguageFile(path);
        if (!res.Ok)
        {
            actions.ShowNotice(L10n.T("Exp.LangImportFail"));
            return;
        }
        actions.Config.Language = res.Code;
        L10n.Instance.ApplyImportedLanguage(res.Code);
        SaveConfig();
        actions.RefreshAll();
        actions.ShowNotice(res.CoveragePct < 90
            ? string.Format(L10n.T("Exp.LangCoverageLow"), res.CoveragePct)
            : L10n.T("Exp.LangImportDone"));
    }

    private void RestoreLanguages()
    {
        _ = Task.Run(async () =>
        {
            var res = await L10n.RestoreBuiltinLanguagesAsync().ConfigureAwait(false);
            actions.Host.TryPost(() =>
            {
                if (_disposed) return;
                if (res.Restored > 0)
                {
                    L10n.Instance.ApplyImportedLanguage(actions.Config.Language);
                    actions.RefreshAll();
                    actions.ShowNotice(string.Format(L10n.T(res.Ok ? "Exp.LangRestored" : "Exp.LangRestorePartial"), res.Restored));
                }
                else
                {
                    actions.ShowNotice(L10n.T(res.Ok ? "Exp.LangNoOverride" : "Exp.LangRestoreFail"));
                }
            });
        });
    }

    private void ExportConfig()
    {
        string? path = NativeDialogs.Save(actions.OwnerWindow.Handle, "SonicRoute configuration|*.json|All files|*.*", "json");
        if (path == null) return;
        string snapshot = ConfigService.CaptureExportSnapshot();
        _ = Task.Run(() =>
        {
            bool ok = ConfigService.ExportTo(path, snapshot);
            actions.Host.TryPost(() =>
            {
                if (!_disposed)
                    actions.ShowNotice(L10n.T(ok ? "Exp.ExportDone" : "Exp.TransferFail"));
            });
        });
    }

    private void ImportConfig()
    {
        string? path = NativeDialogs.Open(actions.OwnerWindow.Handle, "SonicRoute configuration|*.json|All files|*.*");
        if (path == null) return;
        byte[]? prepared = ConfigService.PrepareImport(path);
        if (prepared == null)
        {
            actions.ShowNotice(L10n.T("Exp.TransferFail"));
            return;
        }
        var preview = ConfigService.ReadPreparedConfig(prepared);
        string msg = preview == null ? path : string.Format(
            IsZh() ? "语言：{0}\n设备别名：{1}\n快捷键：{2}\n应用图标：{3}\n自动化收藏：{4}\n\n确认导入并重启？"
                   : "Language: {0}\nDevice aliases: {1}\nShortcuts: {2}\nApp icons: {3}\nFavorite rules: {4}\n\nImport and restart?",
            preview.Language, preview.DeviceNames.Count, preview.Hotkeys.Count, preview.AppIcons.Count, preview.FavoriteRuleIds.Count);
        if (!NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("Exp.ImportConfig"), msg)) return;
        if (!ConfigService.ApplyImport(prepared))
        {
            actions.ShowNotice(L10n.T("Exp.TransferFail"));
            return;
        }
        actions.ShowNotice(L10n.T("Exp.ImportDone"));
        actions.Host.RestartPreview();
    }

    private void ResetSettings()
    {
        if (!NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("Exp.ClearConfig"), L10n.T("Exp.ClearConfigHint")))
            return;
        ConfigService.ResetToDefault();
        actions.Host.RestartPreview();
    }

    private void CheckForUpdates()
    {
        _updateStatus = IsZh() ? "当前已是测试预览基线 1.36 Native AOT；可打开 GitHub Releases 查看发布。"
                               : "Running 1.36 Native AOT preview; open GitHub Releases for official packages.";
        actions.Invalidate();
        if (NativeDialogs.Confirm(actions.OwnerWindow.Handle, L10n.T("Upd.Title"),
            IsZh() ? "是否打开官方 GitHub Releases 页面检查最新版本？" : "Open official GitHub Releases page to check for updates?"))
        {
            OpenUrl("https://github.com/kunkunkunkunkun/SonicRoute/releases");
        }
    }

    private void ShowLicense()
    {
        try
        {
            using var stream = typeof(SettingsPageView).Assembly.GetManifestResourceStream("SonicRoute.License");
            if (stream == null) return;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            NativeDialogs.Input(actions.OwnerWindow.Handle, "GNU General Public License", reader.ReadToEnd(), readOnly: true, multiline: true);
        }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void SaveConfig()
    {
        if (!ConfigService.TrySave(actions.Config))
            actions.ShowNotice(L10n.T("Tray.SaveFailed"));
        actions.NotifySettingsChanged();
        actions.Host.ViewSettingsChanged();
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
