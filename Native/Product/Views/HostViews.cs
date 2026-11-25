using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed partial class ApplicationHost
{
    private ProductViewWindow? mainView, panelView, osdView;
    private IDisposable? osdDeadline, osdAnimation, switchDeadline;
    private long switchingSince;
    private bool panelDismissQueued;
    private bool micOsd;
    private string micOsdTitle = "", micOsdText = "";
    private string PreviewControlsFile => Path.Combine(_environment.DataDirectory, "global-controls.enabled");

    internal string DataDirectory => _environment.DataDirectory;
    internal ProductAudio Audio => _audio;
    internal bool PreviewGlobalControls { get; private set; }
    internal bool HasPendingVolume(int pid) => _volumeWriter.HasPending(pid);
    internal int QueueVolume(int pid, int volume, Action<int, bool> readback) =>
        _volumeWriter.Enqueue(pid, volume, (_, _, applied, actual) => readback(actual, applied));

    private void StartViews()
    {
        PreviewGlobalControls = File.Exists(PreviewControlsFile);
        _feedback.Changed += ShowFeedback;
        L10n.Instance.PropertyChanged += RefreshLanguage;
    }

    private void StopViews()
    {
        _feedback.Changed -= ShowFeedback;
        L10n.Instance.PropertyChanged -= RefreshLanguage;
        osdDeadline?.Dispose();
        osdAnimation?.Dispose();
        switchDeadline?.Dispose();
        mainView?.Dispose();
        panelView?.Dispose();
        osdView?.Dispose();
    }

    private void RefreshLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        mainView?.View?.ExternalRefresh();
        panelView?.View?.ExternalRefresh();
    }

    internal Task<string> RunRule(string selector) => ExecuteRuleAsync(selector);
    internal bool RecordingHotkey => _window.CapturingHotkey;
    internal void HoldHotkeys(bool hold)
    {
        _window.CapturingHotkey = hold;
        _automation.ReloadSettings();
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private string RunEntry => "SonicRouteNativeAotPreview_" + _environment.InstanceId;
    internal bool PreviewAutoStart
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunEntry) is string;
        }
    }

    internal void SetPreviewAutoStart(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key?.SetValue(RunEntry, '"' + Environment.ProcessPath + '"');
        else key?.DeleteValue(RunEntry, false);
    }

    internal void SetPreviewGlobalControls(bool enabled)
    {
        if (enabled) File.WriteAllText(PreviewControlsFile, "Native AOT Preview");
        else File.Delete(PreviewControlsFile);
        PreviewGlobalControls = enabled;
        ViewSettingsChanged();
    }

    internal void ViewSettingsChanged()
    {
        _automation?.ReloadSettings();
        mainView?.Invalidate();
        panelView?.Invalidate();
    }

    internal void RestartPreview() => _ = RestartViewAsync();
    private async Task RestartViewAsync()
    {
        try
        {
            if (await ProductProtocol.RestartAsync().ConfigureAwait(false)) _window.RequestExit();
        }
        catch (Exception error) { HandleError(error); }
    }

    internal void OpenProductMain() => _feedback.Publish(FeedbackKind.OpenMain);
    internal void PreviewOsd() => _feedback.Publish(FeedbackKind.Osd, "SonicRoute · Native AOT Preview", L10n.T("Theme.OsdStyle"));

    private void ShowFeedback(HostFeedback feedback)
    {
        try
        {
            switch (feedback.Kind)
            {
                case FeedbackKind.OpenMain:
                    panelView?.Close();
                    if (mainView == null)
                    {
                        mainView = new(ProductSurface.Main, _config);
                        mainView.View = new(this, _config, mainView, false);
                    }
                    mainView.Show(_window.Handle);
                    break;
                case FeedbackKind.TogglePanel:
                    if (panelView?.Visible == true) { panelView.Close(); break; }
                    goto case FeedbackKind.ShowPanel;
                case FeedbackKind.ShowPanel:
                    if (panelView == null)
                    {
                        panelView = new(ProductSurface.Panel, _config, DismissPanel);
                        panelView.View = new(this, _config, panelView, true);
                    }
                    panelView.Show(_window.Handle);
                    break;
                case FeedbackKind.HidePanel: panelView?.Close(); break;
                case FeedbackKind.TrayMenu: ShowTrayMenu(); break;
                case FeedbackKind.Osd:
                    if (switchDeadline == null && switchingSince == 0) ShowOsd(feedback.Title, feedback.Text);
                    break;
                case FeedbackKind.OutputPending:
                    switchDeadline?.Dispose();
                    switchDeadline = null;
                    if (switchingSince != 0) ShowOsd(feedback.Title, feedback.Text, true);
                    else switchDeadline = _window.ScheduleOnce(TimeSpan.FromMilliseconds(45), () =>
                    {
                        switchDeadline = null;
                        switchingSince = Environment.TickCount64;
                        ShowOsd(feedback.Title, feedback.Text, true);
                    });
                    break;
                case FeedbackKind.OutputSettled:
                    switchDeadline?.Dispose();
                    switchDeadline = null;
                    long remaining = switchingSince == 0 ? 0 : 500 - (Environment.TickCount64 - switchingSince);
                    void Settle()
                    {
                        switchingSince = 0;
                        switchDeadline = null;
                        ShowOsd(feedback.Title, feedback.Text);
                    }
                    if (remaining > 0) switchDeadline = _window.ScheduleOnce(TimeSpan.FromMilliseconds(remaining), Settle);
                    else Settle();
                    break;
                case FeedbackKind.MicStatus:
                    micOsd = feedback.Text.Length != 0;
                    (micOsdTitle, micOsdText) = (feedback.Title, feedback.Text);
                    if (osdDeadline == null && switchingSince == 0)
                    {
                        if (micOsd) ShowOsd(micOsdTitle, micOsdText, true);
                        else osdView?.Close();
                    }
                    break;
            }
        }
        catch (Exception error) { HandleError(error); }
    }

    private void DismissPanel()
    {
        if (panelView?.View?.HasModal == true || panelDismissQueued) return;
        panelDismissQueued = true;
        _window.TryPost(() =>
        {
            panelDismissQueued = false;
            if (panelView?.View?.HasModal == false && ShellNative.GetForegroundWindow() != panelView.Handle)
                panelView.Close();
        });
    }

    private void ShowOsd(string title, string text, bool persistent = false)
    {
        osdView ??= new(ProductSurface.Osd, _config);
        osdDeadline?.Dispose();
        osdAnimation?.Dispose();
        osdDeadline = osdAnimation = null;
        bool animate = osdView.Handle == 0 && _config.OsdFadeInMs > 0;
        byte alpha = animate ? (byte)24 : (byte)255;
        if (!osdView.ShowOsd(_window.Handle, title, text, alpha) &&
            !osdView.ShowOsd(_window.Handle, title, text, alpha)) return;
        if (animate) AnimateOsd(alpha, 255, _config.OsdFadeInMs, static () => { });
        if (!persistent) osdDeadline = _window.ScheduleOnce(TimeSpan.FromMilliseconds(1250), () =>
        {
            osdDeadline = null;
            if (micOsd) ShowOsd(micOsdTitle, micOsdText, true);
            else AnimateOsd(255, 0, _config.OsdFadeOutMs, osdView.Close);
        });
    }

    private void AnimateOsd(byte from, byte to, int duration, Action completed)
    {
        osdAnimation?.Dispose();
        osdAnimation = null;
        if (duration <= 0) { completed(); return; }
        long start = Environment.TickCount64;
        void Frame()
        {
            osdAnimation = null;
            if (osdView?.Handle is null or 0) return;
            float amount = Math.Clamp((Environment.TickCount64 - start) / (float)duration, 0f, 1f);
            osdView.SetOsdAlpha((byte)MathF.Round(from + (to - from) * amount));
            if (amount >= 1) completed();
            else osdAnimation = _window.ScheduleOnce(TimeSpan.FromMilliseconds(16), Frame);
        }
        osdAnimation = _window.ScheduleOnce(TimeSpan.FromMilliseconds(16), Frame);
    }

    private void ShowTrayMenu()
    {
        nint foreground = ShellNative.GetForegroundWindow();
        ViewNative.GetCursorPos(out var point);
        var top = new List<(string Label, Action? Action)>
        {
            (L10n.T("Tray.OpenMain"), OpenProductMain),
            (L10n.T("Tray.OpenPanel"), () => _feedback.Publish(FeedbackKind.ShowPanel)),
            ("", null)
        };
        top.Add((L10n.T("Dev.SystemDefaultOut"), () => TrayAudioMenu(point, 0)));
        var current = CurrentApp.Resolve(hwnd: foreground);
        if (current != null) top.Add((current.Label, () => TrayAudioMenu(point, checked((int)current.ProcessId))));
        if (_config.ExperimentalMic) top.Add((L10n.T("Tray.MuteAllMicrophones"), () => TrayOperation(() =>
            _audio.GlobalMicMute(null) ? L10n.T("Ov.MicMuted") : L10n.T("Ov.MicUnmuted"))));
        if (PreviewGlobalControls) top.Add((L10n.T("Tray.PauseWheel"), () => { _config.TrayWheelPaused = !_config.TrayWheelPaused; ConfigService.TrySave(_config); ViewSettingsChanged(); }));
        foreach (var rule in AutoRuleStore.LoadAll().Where(rule => _config.FavoriteRuleIds.Contains(rule.Id)).Take(20))
            top.Add((rule.Name, () => _ = RunRule(rule.Id)));
        top.Add(("", null));
        top.Add((L10n.T("Tray.Exit"), () => _window.RequestExit()));
        TrayMenu(point, top);
    }

    private void TrayAudioMenu(ViewNative.Point point, int pid)
    {
        var entries = new List<(string Label, Action? Action)>();
        if (pid != 0) entries.Add((L10n.T("Dev.SystemDefaultOut"), () => _ = CurrentApp.SelectOutputAsync(pid, AudioService.SystemDefaultDeviceId)));
        foreach (var device in ProductAudio.Devices(Flow.Render).Where(device => !_config.HiddenOutputDevices.Contains(device.Id)))
            entries.Add((_config.DeviceNames.GetValueOrDefault(device.Id, device.Name), pid == 0
                ? () => TrayOperation(() => { _audio.SetDefault(Flow.Render, device.Id); return device.Name; })
                : () => _ = CurrentApp.SelectOutputAsync(pid, device.Id)));
        entries.Add(("", null));
        foreach (int level in new[] { 25, 50, 75, 100 })
            entries.Add(($"{level}%", () => TrayOperation(() => $"{_audio.Volume(Flow.Render, null, pid, level: level).Percent}%")));
        TrayMenu(point, entries);
    }

    private void TrayMenu(ViewNative.Point point, IReadOnlyList<(string Label, Action? Action)> entries)
    {
        uint selected = ViewNative.Menu(_window.Handle, point, entries);
        if (selected > 0 && selected <= entries.Count) entries[(int)selected - 1].Action?.Invoke();
    }

    private void TrayOperation(Func<string> action)
    {
        _ = Task.Run(() =>
        {
            string result;
            try { result = action(); } catch (Exception error) { result = error.Message; }
            TryPost(() => _feedback.Publish(FeedbackKind.Osd, L10n.T("Ov.VolumeTitle"), result));
        });
    }
}
