using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductViews
{
    private Endpoint[] outputs = [], inputs = [];
    private AudioAppInfo[] apps = [];
    private string? defaultOut, defaultIn, panelOutput, nativeOutput, nativeInput, outputAnchor;
    private string[] actualOutputs = [];
    private HashSet<string>? pendingOutputs;
    private long outputEdit;
    private int selectedPid;
    private bool outputPopup, refreshingAudio, loadedAudio, expandedPanelRoutes;
    private readonly Dictionary<(Flow Flow, string? Endpoint, int Pid), VolumeState> volumes = [];
    private Dictionary<int, float> peaks = [];
    private static string SystemDefault => AudioService.SystemDefaultDeviceId;
    private static readonly StringComparer DeviceComparison = StringComparer.OrdinalIgnoreCase;

    private void ClearAudio()
    {
        outputs = []; inputs = []; apps = [];
        volumes.Clear();
        peaks.Clear();
        loadedAudio = false;
        selectedPid = 0;
        pendingOutputs = null;
        outputAnchor = null;
        outputPopup = false;
    }

    private void RequestAudio()
    {
        if (!visible || Owner == 0 || refreshingAudio || (page != 0 && page != 3 && !panel)) return;
        var readGeneration = generation;
        int selected = selectedPid;
        bool readInputs = config.ExperimentalMic || page == 3;
        bool readApps = page == 0 || panel;
#if SONICROUTE_PHONE
        if (phonePage || airPlayPage) readApps = false;
#endif
        bool peaksEnabled = panel && config.ShowAppPeakMeter && readApps;
        var appRows = hits.Select(h => h.Row).ToArray();
        var shownApps = panel
            ? apps.Where(a => appRows.Any(r => r.Title == AppLabel(a) || r.Title == "◌  " + AppLabel(a)))
                .Select(a => (int)a.ProcessId).Distinct().ToArray()
            : selected > 0 ? [selected] : [];
        bool allOutputs = outputPopup || config.QuickPanelShowAllOutputVolumes && panel;
        string? mainEndpoint = config.PanelChangeSystemDefault || !panel ? null : panelOutput;
        refreshingAudio = true;
        _ = Task.Run(() =>
        {
            try
            {
                var render = ProductAudio.Devices(Flow.Render);
                var capture = readInputs ? ProductAudio.Devices(Flow.Capture) : [];
                string? activeRender = render.Length == 0 ? null : ProductAudio.DefaultId(Flow.Render);
                string? activeCapture = capture.Length == 0 ? null : ProductAudio.DefaultId(Flow.Capture);
                var applications = readApps ? host.Audio.Apps().ToArray() : [];
                var state = new Dictionary<(Flow, string?, int), VolumeState>();
                void Read(Flow flow, string? endpoint, int pid)
                {
                    try { state[(flow, endpoint, pid)] = host.Audio.Volume(flow, endpoint, pid); }
                    catch (Exception error) when (error is COMException or InvalidOperationException) { }
                }
                if (readApps)
                {
                    if (render.Length != 0) Read(Flow.Render, mainEndpoint, 0);
                    if (capture.Length != 0) Read(Flow.Capture, null, 0);
                    foreach (int pid in shownApps)
                        if (!host.HasPendingVolume(pid)) Read(Flow.Render, null, pid);
                    if (allOutputs)
                        foreach (var endpoint in render) Read(Flow.Render, endpoint.Id, 0);
                }
                string? output = null, input = null;
                string[] selectedOutputs = [];
                if (selected > 0 && applications.Any(a => a.ProcessId == selected))
                {
                    output = host.Audio.ReadRoute(selected, Flow.Render);
                    selectedOutputs = AppPlaybackOutputs.GetSelected(selected, output).ToArray();
                    if (readInputs) input = host.Audio.ReadRoute(selected, Flow.Capture);
                }
                var peakValues = peaksEnabled && shownApps.Length != 0 ? host.Audio.ReadPeaks(shownApps) : new Dictionary<int, float>();
                host.TryPost(() =>
                {
                    refreshingAudio = false;
                    if (readGeneration == generation && Owner != 0 && visible)
                    {
                        bool changed = !loadedAudio || !outputs.SequenceEqual(render) || !inputs.SequenceEqual(capture)
                            || !SameDevice(defaultOut, activeRender) || !SameDevice(defaultIn, activeCapture)
                            || !apps.Select(AppStamp).SequenceEqual(applications.Select(AppStamp))
                            || !SameDevice(nativeOutput, output) || !SameDevice(nativeInput, input)
                            || !actualOutputs.SequenceEqual(selectedOutputs, DeviceComparison)
                            || state.Any(entry => volumes.GetValueOrDefault(entry.Key) != entry.Value)
                            || peaks.Count != peakValues.Count
                            || peaks.Any(entry => Math.Abs(entry.Value - peakValues.GetValueOrDefault(entry.Key)) > .012f);
                        loadedAudio = true;
                        (outputs, inputs, defaultOut, defaultIn, apps, nativeOutput, nativeInput, actualOutputs, peaks) =
                            (render, capture, activeRender, activeCapture, applications, output, input, selectedOutputs, peakValues);
                        if (selectedPid != 0 && !apps.Any(a => a.ProcessId == selectedPid) && readApps) ChooseApp(0);
                        foreach (var entry in state) volumes[entry.Key] = entry.Value;
                        var currentPids = apps.Select(app => (int)app.ProcessId).ToHashSet();
                        foreach (var key in volumes.Keys.Where(key => key.Pid != 0 && !currentPids.Contains(key.Pid)).ToArray())
                            volumes.Remove(key);
                        if (changed) Refresh();
                    }
                    ScheduleRefresh(TimeSpan.FromMilliseconds(peaksEnabled ? 350 : 1150));
                });
            }
            catch (Exception error)
            {
                host.TryPost(() =>
                {
                    refreshingAudio = false;
                    if (readGeneration == generation && !loadedAudio) Notice(error.Message);
                    ScheduleRefresh(TimeSpan.FromSeconds(2));
                });
            }
        });
    }

    private static (uint Pid, string Label, bool Active) AppStamp(AudioAppInfo info)
        => (info.ProcessId, info.Label, info.HasActiveSession);

    private void BuildAudio()
    {
#if SONICROUTE_PHONE
        Add(T("Phone.DefaultName"), click: () => { phonePage = !phonePage; airPlayPage = false; ReleaseAirPlayView(); scroll = 0; Refresh(); if (phonePage) OpenPhoneView(); else ReleasePhoneView(); }, selected: phonePage);
        Add("AirPlay", click: () => { airPlayPage = !airPlayPage; phonePage = false; scroll = 0; ReleasePhoneView(); if (!airPlayPage) ReleaseAirPlayView(); Refresh(); }, selected: airPlayPage);
        if (phonePage) { BuildPhone(); return; }
        if (airPlayPage) { BuildAirPlay(); return; }
#endif
        var visibleOutputs = panel ? outputs.Where(d => !config.HiddenOutputDevices.Contains(d.Id)).ToArray() : outputs;
        var visibleInputs = panel ? inputs.Where(d => !config.HiddenInputDevices.Contains(d.Id)).ToArray() : inputs;
        if (!loadedAudio)
        {
            Header(T("Tray.Loading"));
            return;
        }
        if (apps.Length == 0 && selectedPid != 0) selectedPid = 0;
        string? mainEndpoint = config.PanelChangeSystemDefault || !panel ? null : panelOutput;
        Header(T("Dev.SystemDefaultOut"), DeviceLabel(defaultOut, Flow.Render));
        VolumeRow(Label("设备音量", "Device volume"), Flow.Render, mainEndpoint, 0);
        Add(Label("播放设备", "Playback devices"), DeviceLabel(mainEndpoint ?? defaultOut, Flow.Render), () =>
        {
            outputPopup = !outputPopup;
            Refresh();
            ScheduleRefresh(TimeSpan.Zero);
        }, selected: outputPopup);
        if (outputPopup || panel && config.QuickPanelShowAllOutputVolumes)
        {
            foreach (var endpoint in visibleOutputs)
            {
                bool chosen = SameDevice(mainEndpoint ?? defaultOut, endpoint.Id);
                VolumeRow(DeviceLabel(endpoint.Id, Flow.Render), Flow.Render, endpoint.Id, 0,
                    () => PickSystemEndpoint(Flow.Render, endpoint.Id), selected: chosen);
            }
        }

        if (config.ExperimentalMic && (config.MicInPanel || !panel) && visibleInputs.Length != 0)
        {
            Header(T("Dev.SystemDefaultIn"), DeviceLabel(defaultIn, Flow.Capture));
            VolumeRow(Label("麦克风", "Microphone"), Flow.Capture, null, 0);
            Add(T("Tray.MuteAllMicrophones"), click: () => Run(() => Task.Run(() => host.Audio.GlobalMicMute(null))));
            if (outputPopup) foreach (var endpoint in visibleInputs)
                Add(DeviceLabel(endpoint.Id, Flow.Capture), click: () => PickSystemEndpoint(Flow.Capture, endpoint.Id),
                    selected: SameDevice(defaultIn, endpoint.Id));
        }

        Header(T("Nav.Apps"));
        if (apps.Length == 0) Add(T("Tray.AppUnavailable"));
        if (apps.Length > 0)
            Add(Label("全部应用静音 / 取消静音", "Toggle mute for all apps"), click: () => Run(() =>
                Task.Run(() => host.Audio.BatchVolume(Flow.Render, null, null, null, null, default))));
        var selectedApp = apps.FirstOrDefault(a => a.ProcessId == selectedPid);
        if (!panel && selectedApp != null) AppCommands(selectedApp, visibleOutputs, visibleInputs);
        foreach (var app in apps)
        {
            if (panel && app.ProcessName is { } process && config.HiddenPanelApps.Contains(process, DeviceComparison)) continue;
            int pid = checked((int)app.ProcessId);
            string title = AppLabel(app);
            if (panel)
            {
                string extra = config.QuickPanelShowOutputName || config.QuickPanelCompactShowOutputName
                    ? pid == selectedPid ? DeviceLabel(nativeOutput, Flow.Render) : "" : "";
                VolumeRow(title, Flow.Render, null, pid, () =>
                {
                    if (selectedPid == pid) expandedPanelRoutes = !expandedPanelRoutes;
                    else { ChooseApp(pid); expandedPanelRoutes = true; }
                    Refresh();
                }, pid == selectedPid, extra);
                AddPcIcon(app);
                if (pid == selectedPid && expandedPanelRoutes) AppCommands(app, visibleOutputs, visibleInputs);
            }
            else
            {
                Add(title, app.ProcessName ?? "", () => ChooseApp(pid), selected: pid == selectedPid);
                AddPcIcon(app);
            }
        }
    }

    private void AddPcIcon(AudioAppInfo app)
    {
        if (app.ProcessName is { } process && AppIconFiles.GetFileName(config, process) is { } file)
            RowIcon(file, () => AppIconFiles.ReadPng(file));
    }

    private void ChooseApp(int pid)
    {
        if (pid == selectedPid) return;
        selectedPid = pid;
        ++generation;
        outputAnchor = nativeOutput = nativeInput = null;
        actualOutputs = [];
        pendingOutputs = null;
        ++outputEdit;
        Refresh();
        ScheduleRefresh(TimeSpan.Zero);
    }

    private string AppLabel(AudioAppInfo app) =>
        app.ProcessName is { } process && config.AppNames.TryGetValue(process, out string? custom) && custom.Length > 0 ? custom : app.Label;

    private void AppCommands(AudioAppInfo app, Endpoint[] visibleOutputs, Endpoint[] visibleInputs)
    {
        int pid = checked((int)app.ProcessId);
        VolumeRow(AppLabel(app), Flow.Render, null, pid);
        if (!panel && app.ProcessName is { } process)
        {
            Add(Label("重命名应用", "Rename app"), AppLabel(app), () =>
            {
                if (NativeDialogs.Input(Owner, Label("应用名称", "App name"), AppLabel(app)) is not { } name) return;
                if (string.IsNullOrWhiteSpace(name)) config.AppNames.Remove(process);
                else config.AppNames[process] = name.Trim();
                Persist();
            });
            Add(Label("导入应用图标", "Import app icon"), "64 × 64 PNG", () =>
            {
                if (NativeDialogs.Open(Owner, "PNG|*.png") is not { } path) return;
                if (new FileInfo(path).Length > AppIconFiles.MaxIconBytes)
                { Notice(Label("图标文件过大", "Icon file is too large")); return; }
                config.AppIcons[process] = AppIconFiles.SavePng(File.ReadAllBytes(path));
                Persist();
            });
            if (AppIconFiles.GetFileName(config, process) != null)
                Add(Label("恢复默认图标", "Restore default icon"), click: () => { config.AppIcons.Remove(process); Persist(); });
            Add(Label("快捷面板显示", "Show in quick panel"), click: () => ToggleAppList(config.HiddenPanelApps, process),
                selected: !config.HiddenPanelApps.Contains(process, DeviceComparison));
            Add(Label("参与自动切换", "Follow app automatically"), click: () => ToggleAppList(config.DisabledAutoSwitchApps, process),
                selected: !config.DisabledAutoSwitchApps.Contains(process, DeviceComparison));
        }

        string current = nativeOutput ?? SystemDefault;
        Header(Label("原生输出 / 捕获转发", "Native output / capture forwarding"), Label("Ctrl 切换 · Shift 选范围", "Ctrl toggles · Shift selects a range"));
        var choices = new[] { new Endpoint(SystemDefault, T("Dev.SystemDefaultOut")) }.Concat(visibleOutputs).ToArray();
        string[] selected = pendingOutputs?.ToArray() ?? (actualOutputs.Length == 0 ? [current] : actualOutputs);
        foreach (var device in choices)
        {
            bool isSelected = selected.Contains(device.Id, DeviceComparison);
            string detail = SameDevice(current, device.Id) ? Label("原生输出", "Native output") :
                isSelected ? Label("捕获转发", "Capture forwarding") : "";
            Add(DeviceLabel(device.Id, Flow.Render, device.Name), detail, () => SelectAppOutput(pid, device.Id, choices, current), selected: isSelected);
        }
        if (config.ExperimentalMic)
        {
            Header(T("Dev.SystemDefaultIn"));
            foreach (var device in new[] { new Endpoint(SystemDefault, T("Dev.SystemDefaultIn")) }.Concat(visibleInputs))
                Add(DeviceLabel(device.Id, Flow.Capture, device.Name), click: () => Run(() => Task.Run(() =>
                    host.Audio.SetRoute(pid, Flow.Capture, SameDevice(device.Id, SystemDefault) ? null : device.Id))),
                    selected: SameDevice(nativeInput ?? SystemDefault, device.Id));
        }
#if SONICROUTE_PHONE
        AppendPcToPhone(pid);
#endif
    }

    private void PickSystemEndpoint(Flow flow, string endpoint)
    {
        if (flow == Flow.Render && panel && !config.PanelChangeSystemDefault)
        {
            panelOutput = endpoint;
            Refresh();
            ScheduleRefresh(TimeSpan.Zero);
            return;
        }
        Run(() => Task.Run(() => host.Audio.SetDefault(flow, endpoint)));
    }

    private void ToggleAppList(List<string> list, string process)
    {
        if (list.RemoveAll(x => DeviceComparison.Equals(x, process)) == 0) list.Add(process);
        Persist();
    }

    private void SelectAppOutput(int pid, string id, Endpoint[] choices, string current)
    {
        bool control = (modifiers & 8) != 0, shift = (modifiers & 4) != 0;
        var selection = new HashSet<string>(pendingOutputs ?? (IEnumerable<string>)(actualOutputs.Length > 0 ? actualOutputs : [current]), DeviceComparison);
        if (shift)
        {
            int a = Array.FindIndex(choices, item => SameDevice(item.Id, outputAnchor));
            int b = Array.FindIndex(choices, item => SameDevice(item.Id, id));
            if (a < 0) a = b;
            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) selection.Add(choices[i].Id);
        }
        else if (control)
        {
            if (!selection.Remove(id)) selection.Add(id);
            outputAnchor = id;
        }
        else
        {
            selection.Clear();
            selection.Add(id);
            outputAnchor = id;
        }
        string preferred = selection.Contains(id) ? id : selection.Contains(current) ? current : selection.FirstOrDefault() ?? SystemDefault;
        pendingOutputs = selection;
        long edit = ++outputEdit;
        Refresh();
        Run(async () =>
        {
            ControlReply result = !control && !shift
                ? await host.CurrentApp.SelectOutputAsync(pid, id).ConfigureAwait(false)
                : await host.CurrentApp.SetOutputsAsync(pid, preferred, selection.ToArray()).ConfigureAwait(false);
            host.TryPost(() =>
            {
                if (edit != outputEdit || pid != selectedPid) return;
                var observed = host.CurrentApp.LastOutput;
                if (observed?.Pid == pid)
                {
                    nativeOutput = observed.Observed;
                    actualOutputs = observed.ObservedOutputs ?? (observed.Observed == null ? [] : [observed.Observed]);
                }
                pendingOutputs = null;
                if (result.Text.StartsWith("error", StringComparison.Ordinal)) Notice(result.Text);
                Refresh();
            });
        });
    }

    private string DeviceLabel(string? id, Flow flow, string? fallback = null)
    {
        if (id == null || SameDevice(id, SystemDefault)) return T(flow == Flow.Render ? "Dev.SystemDefaultOut" : "Dev.SystemDefaultIn");
        return config.DeviceNames.GetValueOrDefault(id,
            fallback ?? (flow == Flow.Render ? outputs : inputs).FirstOrDefault(e => SameDevice(e.Id, id))?.Name ?? id);
    }

    private static bool SameDevice(string? a, string? b) => DeviceComparison.Equals(a, b);

    private void VolumeRow(string name, Flow flow, string? endpoint, int pid, Action? click = null, bool selected = false, string detail = "")
    {
        var key = (flow, endpoint, pid);
        if (!volumes.TryGetValue(key, out var state))
        {
            Add(name, detail, click, selected: selected);
            return;
        }

        void Mute() => Run(() => Task.Run(() => host.Audio.Volume(flow, endpoint, pid, toggle: true)));
        rows.Add(new((state.Muted ? "◌  " : "") + name, detail.Length > 0 ? detail : state.Muted ? Label("已静音", "Muted") : "",
            click ?? Mute, value =>
            {
                if (pid > 0 && flow == Flow.Render)
                {
                    if (host.QueueVolume(pid, value, (actual, applied) =>
                    {
                        if (Owner == 0) return;
                        if (applied) volumes[key] = state with { Percent = actual };
                        else Notice(T("Tray.AudioActionFailed"));
                        Refresh();
                        ScheduleRefresh(TimeSpan.Zero);
                    }) < 0) Notice(T("Tray.AudioActionFailed"));
                }
                else Run(() => Task.Run(() => host.Audio.Volume(flow, endpoint, pid, level: value)));
            }, state.Percent, selected, false, peaks.GetValueOrDefault(pid)));
        if (selected && click != null && pid != 0)
            Add(state.Muted ? Label("取消静音", "Unmute") : Label("静音", "Mute"), click: Mute);
    }
}

internal sealed partial class ProductAudio
{
    private static readonly Guid PeakMeter = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    internal Dictionary<int, float> ReadPeaks(int[] processes)
    {
        using var apt = new MtaScope();
        using var devices = new Endpoints();
        var result = new Dictionary<int, float>(processes.Length);
        VisitAll(devices, Flow.Render, (processId, state, control) =>
        {
            int pid = (int)processId;
            if (state != 1 || !processes.Contains(pid)) return;
            try
            {
                using var meter = control.Query(PeakMeter);
                result[pid] = Math.Max(result.GetValueOrDefault(pid), Math.Clamp(meter.Scalar(3), 0f, 1f));
            }
            catch (COMException) { }
        });
        return result;
    }
}
