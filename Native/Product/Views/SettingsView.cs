using System.Text;
using SonicRoute.Core;
using SonicRoute.Native.Audio;
using SonicRoute;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductViews
{
    private void BuildHotkeys()
    {
        Header(T("Nav.Hotkeys"), Label("录制快捷键后会立即应用。", "Record a shortcut to apply it immediately."));
        AddPreviewGlobalControls();
        foreach (string action in HotkeyActions.All)
        {
            string key = config.Hotkeys.TryGetValue(action, out var saved)
                ? saved : HotkeyActions.Defaults.GetValueOrDefault(action, "");
            Add(HotkeyActions.DisplayName(action), key.Length == 0 ? Label("未设置", "Not set") : key,
                () =>
                {
                    string? recorded = null;
                    host.HoldHotkeys(true);
                    try { recorded = NativeDialogs.RecordHotkey(Owner, HotkeyActions.DisplayName(action), key); }
                    finally { host.HoldHotkeys(false); }
                    if (recorded == null) return;
                    config.Hotkeys[action] = recorded;
                    Persist();
                });
        }
        Add(Label("恢复默认快捷键", "Restore defaults"), Label("仅恢复此程序的快捷键配置。", "Restore the built-in shortcut assignments."), () =>
        {
            if (!NativeDialogs.Confirm(Owner, Label("恢复默认快捷键", "Restore defaults"),
                Label("确定恢复全部默认快捷键？", "Restore all default shortcuts?"))) return;
            config.Hotkeys = new(HotkeyActions.Defaults);
            Persist();
        });
    }

    private void BuildTheme()
    {
        Header(T("Nav.Theme"), Label("主题更改即时预览并保存。", "Theme changes preview immediately and are saved."));
        string[] modes = ["system", "light", "dark"];
        Add(Label("主题模式", "Theme mode"), config.ThemeMode switch
        {
            "light" => Label("浅色", "Light"), "dark" => Label("深色", "Dark"), _ => Label("跟随系统", "System")
        }, () =>
        {
            int index = Array.IndexOf(modes, config.ThemeMode);
            config.ThemeMode = modes[(index + 1 + modes.Length) % modes.Length];
            Persist();
        });
        string[] accents = ["blue", "green", "purple", "red", "orange", "pink", "teal"];
        Add(Label("强调色", "Accent color"), config.Accent, () =>
        {
            int index = Array.IndexOf(accents, config.Accent);
            config.Accent = accents[(index + 1 + accents.Length) % accents.Length];
            Persist();
        });
        Add(Label("自定义强调色", "Custom accent"), config.Accent, () =>
        {
            string? color = NativeDialogs.Input(Owner, Label("输入十六进制 RGB 颜色", "Enter a hex RGB color"), "#3D91EF");
            if (color == null) return;
            color = color.Trim().TrimStart('#');
            if (color.Length != 6 || !uint.TryParse(color, System.Globalization.NumberStyles.HexNumber, null, out _))
            { Notice(Label("请输入 6 位 RGB 颜色", "Enter six RGB hex digits")); return; }
            config.Accent = "#" + color;
            Persist();
        });
        Add(Label("显示麦克风", "Show microphone in panel"), click: () =>
        { config.MicInPanel = !config.MicInPanel; Persist(); }, selected: config.MicInPanel);
        Add(Label("应用行显示播放设备", "Show playback device in app rows"), click: () =>
        { config.QuickPanelShowOutputName = !config.QuickPanelShowOutputName; Persist(); }, selected: config.QuickPanelShowOutputName);
        Add(Label("显示所有设备音量", "Show every output volume"), click: () =>
        { config.QuickPanelShowAllOutputVolumes = !config.QuickPanelShowAllOutputVolumes; Persist(); }, selected: config.QuickPanelShowAllOutputVolumes);
        Add(Label("面板修改系统默认输出", "Panel changes system default"), click: () =>
        { config.PanelChangeSystemDefault = !config.PanelChangeSystemDefault; Persist(); }, selected: config.PanelChangeSystemDefault);
        Add(T("Theme.MicMuteOsdPersist"), click: () =>
        { config.MicMuteOsdPersistent = !config.MicMuteOsdPersistent; Persist(); }, selected: config.MicMuteOsdPersistent);
        Add(Label("OSD 位置", "OSD position"), config.OsdPosition, () =>
        {
            string[] positions = ["TL", "TC", "TR", "BL", "BC", "BR", "C"];
            Menu(positions.Select<string, (string, Action?)>(position => (position, () =>
            { config.OsdPosition = position; Persist(); host.PreviewOsd(); })).ToArray());
        });
        Add(Label("背景透明度", "Background opacity"), $"{config.BackgroundOpacity}%", slider: value =>
        {
            config.BackgroundOpacity = Math.Clamp(60 + value * 40 / 100, 60, 100);
            window.SetOpacity(); Persist();
        }, value: Math.Clamp((config.BackgroundOpacity - 60) * 100 / 40, 0, 100));
        Add(Label("快速面板高度", "Quick panel height"), $"{config.QuickPanelHeight} px", slider: value =>
        {
            config.QuickPanelHeight = 350 + value * 500 / 100;
            Persist();
        }, value: Math.Clamp((config.QuickPanelHeight - 350) * 100 / 500, 0, 100));
        Add(Label("应用行布局", "App row layout"), config.QuickPanelUseLegacyAppRows
            ? Label("紧凑单行", "Compact single row") : Label("完整布局", "Full layout"), () =>
        {
            config.QuickPanelUseLegacyAppRows = !config.QuickPanelUseLegacyAppRows;
            Persist();
        }, selected: config.QuickPanelUseLegacyAppRows);
        Add(Label("显示应用音量峰值", "Show app volume peaks"), config.ShowAppPeakMeter ? Label("开启", "On") : Label("关闭", "Off"), () =>
        {
            config.ShowAppPeakMeter = !config.ShowAppPeakMeter;
            Persist();
        }, selected: config.ShowAppPeakMeter);
        Add(Label("OSD 宽度", "OSD width"), $"{config.OsdWidth} px", slider: value =>
        {
            config.OsdWidth = 200 + value * 560 / 100;
            Persist();
        }, value: Math.Clamp((config.OsdWidth - 200) * 100 / 560, 0, 100));
        Add(Label("预览 OSD", "Preview OSD"), Label("在屏幕上显示一次当前 OSD 样式。", "Show a sample using the current OSD style."), host.PreviewOsd);
    }

    private void AddPreviewGlobalControls()
    {
        Add(Label("预览版全局按键与滚轮", "Preview shortcuts and tray wheel"),
            host.PreviewGlobalControls ? Label("已启用", "Enabled") : Label("已关闭", "Disabled"), () =>
            {
                if (!host.PreviewGlobalControls && !NativeDialogs.Confirm(Owner, "Native AOT Preview",
                    Label("启用后，测试版可能与正式版争用相同的全局快捷键和托盘滚轮。继续？",
                        "The preview and installed version may compete for the same global shortcuts and tray wheel. Enable?"))) return;
                host.SetPreviewGlobalControls(!host.PreviewGlobalControls);
                Refresh();
            }, selected: host.PreviewGlobalControls);
    }

    private int authorClicks;
    private void BuildSettings()
    {
        Header(T("Nav.Settings"), Label("语言、设备列表和一般偏好。", "Language, device lists, and general preferences."));
        Add("SonicRoute 1.36 · Native AOT Preview", host.DataDirectory, () => ShellOpen.Folder(host.DataDirectory));
        Add(T("App.About"), "", () =>
        {
            if (++authorClicks < 5 || config.ExperimentalUnlocked) return;
            config.ExperimentalUnlocked = true;
            Persist();
            Notice(T("St.ExpUnlocked"));
        });
        Add(Label("开源许可证", "Open source license"), click: ShowLicense);
        Add(Label("开机启动测试版", "Start preview at sign-in"), click: () =>
        {
            bool next = !host.PreviewAutoStart;
            if (next && !NativeDialogs.Confirm(Owner, "Native AOT Preview", Label("登录后启动本测试版？", "Start this preview after Windows sign-in?"))) return;
            host.SetPreviewAutoStart(next);
            Refresh();
        }, selected: host.PreviewAutoStart);
        Add(Label("导入配置", "Import configuration"), click: () =>
        {
            if (NativeDialogs.Open(Owner, "SonicRoute configuration|*.json|All files|*.*") is { } path) ImportConfiguration(path);
        });
        var languages = L10n.SupportedLanguages;
        int currentLanguage = Array.FindIndex(languages, item => string.Equals(item.Code, L10n.CurrentLanguage, StringComparison.OrdinalIgnoreCase));
        Add(Label("界面语言", "Interface language"), languages.Length == 0 ? L10n.CurrentLanguage : languages[Math.Max(currentLanguage, 0)].NativeName,
            () =>
            {
                if (languages.Length == 0) return;
                string next = languages[(Math.Max(currentLanguage, 0) + 1) % languages.Length].Code;
                config.Language = next;
                L10n.Instance.SetLanguage(next);
                Persist();
            });
        Add(Label("导入语言文件", "Import language file"), Label("导入 JSON 语言表并应用。", "Import and apply a JSON language table."), () =>
        {
            string? path = NativeDialogs.Open(Owner, "JSON language file|*.json|All files|*.*");
            if (path == null) return;
            var result = L10n.ImportLanguageFile(path);
            if (!result.Ok) { Notice(Label("语言文件无效或无法读取。", "The language file is invalid or could not be read.")); return; }
            config.Language = result.Code;
            L10n.Instance.ApplyImportedLanguage(result.Code);
            Persist();
        });
        Add(Label("打开语言目录", "Open language folder"), L10n.ExternalLangDir, () => ShellOpen.Folder(L10n.ExternalLangDir));
        Add(Label("启动时最小化", "Start minimized"), config.StartMinimized ? Label("开启", "On") : Label("关闭", "Off"), () =>
        { config.StartMinimized = !config.StartMinimized; Persist(); }, selected: config.StartMinimized);
        Add(Label("启动时打开快速面板", "Open quick panel at startup"), config.StartPanelOnStart ? Label("开启", "On") : Label("关闭", "Off"), () =>
        { config.StartPanelOnStart = !config.StartPanelOnStart; Persist(); }, selected: config.StartPanelOnStart);
        Add(Label("音量步进", "Volume step"), $"{config.VolumeStep}%", () =>
        {
            config.VolumeStep = config.VolumeStep >= 20 ? 1 : config.VolumeStep + 1;
            Persist();
        });
        Add(Label("托盘滚轮区域", "Tray wheel area"), config.TrayWheelEverywhere
            ? Label("整个通知区域", "Entire notification area") : Label("仅本程序图标", "SonicRoute icon only"), () =>
        { config.TrayWheelEverywhere = !config.TrayWheelEverywhere; Persist(); }, selected: config.TrayWheelEverywhere);
        Add(Label("暂停托盘滚轮", "Pause tray wheel"), config.TrayWheelPaused ? Label("已暂停", "Paused") : Label("运行中", "Active"), () =>
        { config.TrayWheelPaused = !config.TrayWheelPaused; Persist(); }, selected: config.TrayWheelPaused);
        AddPreviewGlobalControls();
        Add(Label("设备名称与快捷面板显示", "Device names and panel visibility"), Label("编辑当前可用的播放与录音设备。", "Edit available playback and capture devices."));
        AddEndpointSettings(outputs, Flow.Render);
        AddEndpointSettings(inputs, Flow.Capture);
        Add(Label("导出配置", "Export configuration"), Label("生成包含受管图标资源的配置备份。", "Save a configuration backup with managed icon assets."), () =>
        {
            string? path = NativeDialogs.Save(Owner, "SonicRoute configuration|*.json|All files|*.*", "json");
            if (path == null) return;
            string snapshot = ConfigService.CaptureExportSnapshot();
            Run(async () =>
            {
                bool ok = await Task.Run(() => ConfigService.ExportTo(path, snapshot));
                host.TryPost(() => Notice(ok ? Label("配置已导出。", "Configuration exported.") : Label("导出配置失败。", "Configuration export failed.")));
            });
        });
    }

    private void AddEndpointSettings(Endpoint[] devices, Flow flow)
    {
        string[] hidden = flow == Flow.Render ? config.HiddenOutputDevices.ToArray() : config.HiddenInputDevices.ToArray();
        foreach (var endpoint in devices)
        {
            string id = endpoint.Id;
            string alias = config.DeviceNames.TryGetValue(id, out var name) ? name : "";
            bool isHidden = hidden.Contains(id, StringComparer.OrdinalIgnoreCase);
            string flowLabel = flow == Flow.Render ? Label("播放", "Playback") : Label("录音", "Capture");
            Add($"{flowLabel}: {(alias.Length == 0 ? endpoint.Name : alias)}", isHidden
                ? Label("已从快速面板隐藏", "Hidden from quick panel") : Label("显示在快速面板", "Visible in quick panel"), () =>
            {
                var list = flow == Flow.Render ? config.HiddenOutputDevices : config.HiddenInputDevices;
                if (list.Contains(id, StringComparer.OrdinalIgnoreCase)) list.RemoveAll(value => string.Equals(value, id, StringComparison.OrdinalIgnoreCase));
                else list.Add(id);
                Persist();
            }, selected: !isHidden);
            Add(Label("设备别名", "Device alias"), alias.Length == 0 ? endpoint.Name : alias, () =>
            {
                string? value = NativeDialogs.Input(Owner, Label("设备别名（留空恢复默认）", "Device alias (blank restores default)"), alias);
                if (value == null) return;
                if (string.IsNullOrWhiteSpace(value)) config.DeviceNames.Remove(id);
                else config.DeviceNames[id] = value.Trim();
                Persist();
            });
        }
    }

    private void BuildExperimental()
    {
        Header(T("Nav.Experimental"), Label("实验功能可能需要重启。仅在你明确开启时生效。", "Experimental features may require restart and stay off until enabled."));
        if (!config.ExperimentalUnlocked)
        {
            Add(Label("实验功能未解锁", "Experimental features are locked"), Label("请在设置页按产品规定的方式解锁。", "Unlock experimental mode through the supported product flow."));
            return;
        }
        Add(Label("实验模式", "Experimental mode"), config.ExperimentalMode ? Label("已启用", "Enabled") : Label("已关闭", "Off"), () =>
        {
            if (!config.ExperimentalMode && !NativeDialogs.Confirm(Owner, Label("启用实验模式", "Enable experimental mode"),
                Label("实验功能可能不稳定。确定启用？", "Experimental features may be unstable. Enable them?"))) return;
            config.ExperimentalMode = !config.ExperimentalMode;
            Persist();
        }, selected: config.ExperimentalMode);
        if (!config.ExperimentalMode) return;
        Add(Label("导入配置", "Import configuration"), Label("选择并预览配置备份；提交前会再次确认。", "Select a backup and confirm before replacing settings."), () =>
        {
            string? path = NativeDialogs.Open(Owner, "SonicRoute configuration|*.json|All files|*.*");
            if (path != null) ImportConfiguration(path);
        });
        Add(Label("后台轮询兼容模式", "Background polling fallback"), config.BackgroundPollingFallbackEnabled
            ? Label("开启", "On") : Label("关闭", "Off"), () =>
        { config.BackgroundPollingFallbackEnabled = !config.BackgroundPollingFallbackEnabled; Persist(); }, selected: config.BackgroundPollingFallbackEnabled);
        Add(Label("执行历史", "Execution history"), config.ExecutionHistoryEnabled
            ? Label("记录自动化执行", "Record automation executions") : Label("关闭且不读取历史", "Off; history is not read"), () =>
        {
            if (!config.ExecutionHistoryEnabled && !NativeDialogs.Confirm(Owner, Label("启用执行历史", "Enable execution history"),
                Label("启用后会记录自动化规则的执行明细。", "Rule execution details will be written to the profile."))) return;
            config.ExecutionHistoryEnabled = !config.ExecutionHistoryEnabled;
            Persist();
        }, selected: config.ExecutionHistoryEnabled);
        if (config.ExecutionHistoryEnabled)
            Add(Label("历史容量上限", "History size limit"), $"{config.ExecutionHistoryLimitMb} MiB", () =>
            {
                config.ExecutionHistoryLimitMb = config.ExecutionHistoryLimitMb >= 64 ? 1 : config.ExecutionHistoryLimitMb + 1;
                Persist();
            });
        Add(Label("麦克风选项", "Microphone controls"), config.ExperimentalMic ? Label("开启", "On") : Label("关闭", "Off"), () =>
        { config.ExperimentalMic = !config.ExperimentalMic; Persist(); }, selected: config.ExperimentalMic);
    }

    private void ShowLicense()
    {
        using var stream = typeof(ProductViews).Assembly.GetManifestResourceStream("SonicRoute.License");
        if (stream == null) return;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        NativeDialogs.Input(Owner, Label("GNU 通用公共许可证", "GNU General Public License"), reader.ReadToEnd(), readOnly: true, multiline: true);
    }

    private void ImportConfiguration(string path)
    {
        byte[]? prepared;
        try { prepared = ConfigService.PrepareImport(path); }
        catch (Exception error) { Notice(error.Message); return; }
        if (prepared == null)
        {
            Notice(Label("配置备份无效或包含无法导入的资源。", "The configuration backup is invalid or contains unsupported assets."));
            return;
        }
        var preview = ConfigService.ReadPreparedConfig(prepared);
        string summary = preview == null ? path : string.Format(Label("语言：{0}\n设备别名：{1}\n快捷键：{2}\n应用图标：{3}\n自动化收藏：{4}",
            "Language: {0}\nDevice aliases: {1}\nShortcuts: {2}\nApp icons: {3}\nFavorite rules: {4}"),
            preview.Language, preview.DeviceNames.Count, preview.Hotkeys.Count, preview.AppIcons.Count, preview.FavoriteRuleIds.Count);
        if (!NativeDialogs.Confirm(Owner, Label("确认导入配置", "Confirm configuration import"),
            Label("导入会替换当前配置。请先确认文件来源。\n\n", "Importing replaces the current configuration. Confirm the source first.\n\n") + summary)) return;
        if (!ConfigService.ApplyImport(prepared))
        {
            Notice(Label("导入失败；当前配置未更改。", "Import failed; the current configuration was not changed."));
            return;
        }
        host.RestartPreview();
    }
}
