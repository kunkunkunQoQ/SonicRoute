using System;
using System.IO;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Product.Views;
using SonicRoute.Updates;

namespace SonicRoute.Native.Product;

internal sealed partial class ApplicationHost
{
    private MainWindowShell? mainView;
    private QuickPanelWindow? panelView;
    private OsdWindow? osdView;
    private IDisposable? osdDeadline, osdAnimation, osdIdleRelease, switchDeadline;
    private long switchingSince;
    private bool panelDismissQueued;
    private bool micOsd;
    private string micOsdTitle = "", micOsdText = "";
    private bool? micOsdMuted;
    private long micRestoreVersion;

    internal string DataDirectory => _environment.DataDirectory;
    internal CandidateEnvironment EnvironmentInfo => _environment;
    internal UpdateService Updates => _updates;
    internal IReadOnlyDictionary<string, string> HotkeyStatuses => _automation.HotkeyStatuses;
    internal ProductAudio Audio => _audio;
    internal long AppVolumeRevision { get; private set; }
    internal bool HasPendingVolume(AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        if (app.ExternalSourceId is { } source) return _externalVolumeWriter.HasPending(source);
#endif
        return !app.IsExternal && _volumeWriter.HasPending((int)app.ProcessId);
    }

    internal void QueueAppVolume(AudioAppInfo app, int level)
    {
        ++AppVolumeRevision;
        string identity = app.Identity;
        void Confirm(bool applied, int actual)
        {
            ++AppVolumeRevision;
            if (_disposed) return;
            if (!applied || actual < 0) { ReportAudioWriteFailure(); return; }
            mainView?.ConfirmAppVolume(identity, actual);
            panelView?.ConfirmAppVolume(identity, actual);
        }

        int request = -1;
#if SONICROUTE_PHONE
        if (app.ExternalSourceId is { } source)
            request = _externalVolumeWriter.Enqueue(source, level, (_, _, applied, actual) => Confirm(applied, actual));
        else
#endif
        if (!app.IsExternal && app.ProcessId > 0)
            request = _volumeWriter.Enqueue((int)app.ProcessId, level, (_, _, applied, actual) => Confirm(applied, actual));
        if (request < 0) Confirm(false, -1);
    }

    private Task _panelAudioClicks = Task.CompletedTask;

    internal void SubmitPanelAudio(Action command)
    {
        Task previous = _panelAudioClicks;
        _panelAudioClicks = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try { command(); }
            catch (Exception error)
            {
                ExceptionDiagnostics.Record("Native.PanelAudio", error);
                PostContinuation(ReportAudioWriteFailure);
            }
        });
    }

    internal long EndpointWriteRevision { get; private set; }
    internal bool EndpointVolumePending(string? id) => id != null && _endpointVolumeWriter.HasPending(id);
    internal void QueueEndpointVolume(string? id, int level)
    {
        if (id == null) { ReportAudioWriteFailure(); return; }
        ++EndpointWriteRevision;
        if (_endpointVolumeWriter.Enqueue(id, level, (endpoint, _, applied, actual) =>
        {
            ++EndpointWriteRevision;
            if (!applied) { ReportAudioWriteFailure(); return; }
            panelView?.ConfirmEndpointVolume(endpoint, actual);
        }) < 0) ReportAudioWriteFailure();
    }

    internal void ReportAudioWriteFailure()
    {
        if (_disposed) return;
        PublishOsd(BuildIdentity.DisplayName, L10n.T("Tray.AudioActionFailed"), success: false);
        NotifyAudioVisualChanged();
    }

    internal void FlushVolumeWrites()
    {
        _volumeWriter.Flush();
        _endpointVolumeWriter.Flush();
#if SONICROUTE_PHONE
        _externalVolumeWriter.Flush();
#endif
    }
    internal void NotifyAudioVisualChanged()
    {
        if (_disposed) return;
        _window.TryPost(() =>
        {
            if (_disposed) return;
            mainView?.ExternalRefresh();
            if (panelView is { IsVisible: true })
            {
                panelView.RefreshData();
                panelView.Invalidate();
            }
        });
    }

    private void StartViews()
    {
        _feedback.Changed += ShowFeedback;
        L10n.Instance.PropertyChanged += RefreshLanguage;
    }

    private void StopViews()
    {
        _feedback.Changed -= ShowFeedback;
        L10n.Instance.PropertyChanged -= RefreshLanguage;
        osdDeadline?.Dispose();
        osdAnimation?.Dispose();
        osdIdleRelease?.Dispose();
        switchDeadline?.Dispose();
        TrayMenuController.Shared.CloseActive();
        mainView?.Dispose();
        panelView?.Dispose();
        osdView?.Dispose();
    }

    private void RefreshLanguage(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        mainView?.ExternalRefresh();
        if (panelView is { IsVisible: true })
        {
            panelView.RefreshData();
            panelView.Invalidate();
        }
    }

    internal Task<string> RunRule(string selector) => ExecuteRuleAsync(selector);
    internal bool RecordingHotkey => _window.CapturingHotkey;
    internal void HoldHotkeys(bool hold)
    {
        if (_window.CapturingHotkey == hold) return;
        _window.CapturingHotkey = hold;
        _automation.ReloadBindings();
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

    internal void RefreshViewThemes() => Ui.LayeredWindow.RefreshVisibleThemes(() => Ui.UiThemePalette.Resolve(_config));

    internal void ViewSettingsChanged()
    {
        _automation?.ReloadSettings();
        mainView?.InvalidateTheme();
        panelView?.ApplyThemeAndSettings();
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
    internal void OpenPhoneManagement(string? profileId = null) => EnsureMainShell().OpenPhoneDevice(profileId);
    internal void PreviewOsd() => _feedback.Publish(FeedbackKind.Osd, "📍", L10n.T("Exp.OsdPreview"));
    internal void PublishOsd(string title, string text, int? volume = null, bool? muted = null, bool? success = null) =>
        _feedback.Publish(FeedbackKind.Osd, title, text, volume, muted, success);
    internal bool IsOsdAdjusting => osdView?.IsAdjusting == true;
    internal bool IsPanelAdjusting => panelView?.IsAdjusting == true;
    internal void NotifyAdjustmentChanged() => mainView?.Invalidate();
    internal void CancelOsdAdjust() => osdView?.CancelAdjust();
    internal void CancelPanelAdjust()
    {
        if (panelView?.IsAdjusting == true) panelView.CancelAdjustMode();
    }

    internal void BeginOsdAdjust()
    {
        osdView ??= new OsdWindow(_config, NotifyAdjustmentChanged);
        osdDeadline?.Dispose();
        osdAnimation?.Dispose();
        osdIdleRelease?.Dispose();
        osdDeadline = osdAnimation = osdIdleRelease = null;
        osdView.BeginAdjust(_window.Handle);
    }
    internal void BeginQuickPanelAdjust() => EnsureQuickPanel().BeginAdjust();

    private static IViewPage CreateMainPage(MainNavTab tab, IViewHostActions actions) => tab switch
    {
        MainNavTab.Apps => new AppsPageView(actions),
        MainNavTab.Hotkeys => new HotkeysPageView(actions),
        MainNavTab.Theme => new ThemePageView(actions),
        MainNavTab.Settings => new SettingsPageView(actions),
        MainNavTab.Automation => new AutomationPageView(actions),
        MainNavTab.Experimental => new ExperimentalPageView(actions),
        _ => new AppsPageView(actions),
    };

    private MainWindowShell EnsureMainShell()
    {
        if (mainView == null || mainView.IsDisposed)
            mainView = new MainWindowShell(this, _config, CreateMainPage);
        return mainView;
    }

    private QuickPanelWindow EnsureQuickPanel()
    {
        if (panelView == null || panelView.IsDisposed)
            panelView = new QuickPanelWindow(this, _config, OpenProductMain, DismissPanel);
        return panelView;
    }

    private void ShowFeedback(HostFeedback feedback)
    {
        try
        {
            switch (feedback.Kind)
            {
                case FeedbackKind.OpenMain:
                    panelView?.Hide();
                    TrayMenuController.Shared.CloseActive();
                    EnsureMainShell().ShowMain(_window.Handle);
                    break;

                case FeedbackKind.TogglePanel:
                    if (panelView?.IsVisible == true) { panelView.Hide(); break; }
                    goto case FeedbackKind.ShowPanel;

                case FeedbackKind.ShowPanel:
                    TrayMenuController.Shared.CloseActive();
                    EnsureQuickPanel().ShowQuickPanel();
                    break;

                case FeedbackKind.HidePanel:
                    panelView?.Hide();
                    break;

                case FeedbackKind.TrayMenu:
                    ShowTrayMenu();
                    break;

                case FeedbackKind.Osd:
                    switchDeadline?.Dispose();
                    switchDeadline = null;
                    switchingSince = 0;
                    ShowOsd(feedback.Title, feedback.Text, volume: feedback.Volume, muted: feedback.Muted);
                    break;

                case FeedbackKind.OutputPending:
                    switchDeadline?.Dispose();
                    switchDeadline = null;
                    if (switchingSince != 0) ShowOsd(feedback.Title, feedback.Text, persistent: true);
                    else switchDeadline = _window.ScheduleOnce(TimeSpan.FromMilliseconds(45), () =>
                    {
                        switchDeadline = null;
                        switchingSince = Environment.TickCount64;
                        ShowOsd(feedback.Title, feedback.Text, persistent: true);
                    });
                    break;

                case FeedbackKind.OutputSettled:
                    switchDeadline?.Dispose();
                    switchDeadline = null;
                    long remaining = (switchingSince == 0 || feedback.Success == false)
                        ? 0
                        : 500 - (Environment.TickCount64 - switchingSince);
                    void Settle()
                    {
                        switchingSince = 0;
                        switchDeadline = null;
                        ShowOsd(feedback.Title, feedback.Text, muted: feedback.Muted, volume: feedback.Volume);
                    }
                    if (remaining > 0) switchDeadline = _window.ScheduleOnce(TimeSpan.FromMilliseconds(remaining), Settle);
                    else Settle();
                    break;

                case FeedbackKind.MicStatus:
                    ++micRestoreVersion;
                    micOsd = feedback.Text.Length != 0;
                    (micOsdTitle, micOsdText, micOsdMuted) = (feedback.Title, feedback.Text, feedback.Muted);
                    if (osdDeadline == null && switchingSince == 0)
                    {
                        if (micOsd) ShowOsd(micOsdTitle, micOsdText, persistent: true, muted: micOsdMuted);
                        else if (osdView is { IsVisible: true })
                            AnimateOsd(osdView.CurrentAlpha, 0, _config.OsdFadeOutMs, HideOsdWithIdleRelease);
                        else HideOsdWithIdleRelease();
                    }
                    break;
            }
        }
        catch (Exception error) { HandleError(error); }
    }

    private void DismissPanel()
    {
        if (panelView == null || !panelView.IsVisible || panelView.IsPinned || panelView.HasOpenModal || panelDismissQueued)
            return;
        panelDismissQueued = true;
        _window.TryPost(() =>
        {
            panelDismissQueued = false;
            if (panelView is { IsVisible: true, IsPinned: false, HasOpenModal: false }
                && ShellNative.GetForegroundWindow() != panelView.Handle)
            {
                panelView.Hide();
            }
        });
    }

    private void ShowOsd(string title, string text, bool persistent = false, bool? muted = null, int? volume = null)
    {
        if (osdView == null || osdView.IsDisposed) osdView = new OsdWindow(_config, NotifyAdjustmentChanged);
        osdDeadline?.Dispose();
        osdAnimation?.Dispose();
        osdIdleRelease?.Dispose();
        osdDeadline = osdAnimation = osdIdleRelease = null;
        bool wasVisible = osdView.IsVisible;
        bool animate = !wasVisible && _config.OsdFadeInMs > 0;
        byte alpha = animate ? (byte)24 : (wasVisible ? osdView.CurrentAlpha : (byte)255);
        if (!osdView.ShowOsd(_window.Handle, title, text, alpha, muted, volume) &&
            !osdView.ShowOsd(_window.Handle, title, text, alpha, muted, volume)) return;
        if (animate || alpha < 255) AnimateOsd(alpha, 255, _config.OsdFadeInMs, static () => { });
        if (!persistent) osdDeadline = _window.ScheduleOnce(TimeSpan.FromMilliseconds(1100), () =>
        {
            osdDeadline = null;
            if (micOsd)
            {
                long ver = micRestoreVersion;
                string restoreTitle = micOsdTitle;
                string restoreText = micOsdText;
                bool? restoreMuted = micOsdMuted;
                _ = Task.Run(() =>
                {
                    bool stillMuted = false;
                    try
                    {
                        var devs = ProductAudio.Devices(Flow.Capture);
                        stillMuted = devs.Length > 0 && devs.All(d => _audio.Volume(Flow.Capture, d.Id).Muted);
                    }
                    catch { }
                    _window.TryPost(() =>
                    {
                        if (_disposed || ver != micRestoreVersion || osdDeadline != null || switchingSince != 0) return;
                        if (stillMuted) ShowOsd(restoreTitle, restoreText, persistent: true, muted: restoreMuted);
                        else
                        {
                            micOsd = false;
                            byte cur = osdView?.CurrentAlpha ?? 255;
                            AnimateOsd(cur, 0, _config.OsdFadeOutMs, HideOsdWithIdleRelease);
                        }
                    });
                });
            }
            else
            {
                byte cur = osdView?.CurrentAlpha ?? 255;
                AnimateOsd(cur, 0, _config.OsdFadeOutMs, HideOsdWithIdleRelease);
            }
        });
    }

    private void HideOsdWithIdleRelease()
    {
        if (osdView == null) return;
        osdView.Hide();
        osdIdleRelease?.Dispose();
        osdIdleRelease = _window.ScheduleOnce(TimeSpan.FromSeconds(8), () =>
        {
            osdIdleRelease = null;
            if (osdView is { IsVisible: false }) osdView.ReleaseIdleResources();
        });
    }

    private void AnimateOsd(byte from, byte to, int duration, Action completed)
    {
        osdAnimation?.Dispose();
        osdAnimation = null;
        if (duration <= 0) { osdView?.SetOsdAlpha(to); completed(); return; }
        long start = Environment.TickCount64;
        void Frame()
        {
            osdAnimation = null;
            if (osdView == null || !osdView.IsVisible) return;
            float t = Math.Clamp((Environment.TickCount64 - start) / (float)duration, 0f, 1f);
            float eased = to > from ? 1f - MathF.Pow(1f - t, 3f) : t * t * t;
            osdView.SetOsdAlpha((byte)MathF.Round(from + (to - from) * eased));
            if (t >= 1f) completed();
            else osdAnimation = _window.ScheduleOnce(TimeSpan.FromMilliseconds(16), Frame);
        }
        osdAnimation = _window.ScheduleOnce(TimeSpan.FromMilliseconds(16), Frame);
    }

    internal void SetTrayWheelPaused(bool paused)
    {
        bool previous = _config.TrayWheelPaused;
        if (paused == previous) return;
        if (!_automation.TryConfigureTrayWheel(paused))
        {
            _automation.TryConfigureTrayWheel(previous);
            PublishOsd(L10n.T("Tray.PauseWheel"), L10n.T("Tray.AudioActionFailed"), success: false);
            return;
        }
        _config.TrayWheelPaused = paused;
        if (!ConfigService.TryCommit(_config))
        {
            _automation.TryConfigureTrayWheel(_config.TrayWheelPaused);
            PublishOsd(L10n.T("Tray.PauseWheel"), L10n.T("Tray.SaveFailed"), success: false);
            return;
        }
        RefreshForegroundInterest();
        mainView?.ExternalRefresh();
    }

    private void ShowTrayMenu()
    {
        TrayMenuController.Shared.Show(
            this,
            _config,
            _window.Handle,
            _window.TrayMenuPoint,
            onOpenMain: OpenProductMain,
            onOpenPanel: () =>
            {
                if (panelView is { IsVisible: true }) panelView.RequestFocus();
                else _feedback.Publish(FeedbackKind.ShowPanel);
            },
            onOpenUpdates: () => { OpenProductMain(); EnsureMainShell().OpenUpdateSettings(); },
            onExit: () => _window.RequestExit());
    }
}
