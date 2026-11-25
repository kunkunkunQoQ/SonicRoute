using System;
using System.IO;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Product.Views;

namespace SonicRoute.Native.Product;

internal sealed partial class ApplicationHost
{
    private MainWindowShell? mainView;
    private QuickPanelWindow? panelView;
    private OsdWindow? osdView;
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
    internal void PreviewOsd() => _feedback.Publish(FeedbackKind.Osd, "SonicRoute · Native AOT Preview", L10n.T("Theme.OsdStyle"));

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
            panelView = new QuickPanelWindow(this, _config, OpenProductMain, DismissPanel, _window.Handle);
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
                    if (switchDeadline == null && switchingSince == 0) ShowOsd(feedback.Title, feedback.Text);
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
                        if (micOsd) ShowOsd(micOsdTitle, micOsdText, persistent: true);
                        else osdView?.HideAndRelease();
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

    private void ShowOsd(string title, string text, bool persistent = false)
    {
        if (osdView == null || osdView.IsDisposed) osdView = new OsdWindow(_config);
        osdDeadline?.Dispose();
        osdAnimation?.Dispose();
        osdDeadline = osdAnimation = null;
        bool animate = !osdView.IsVisible && _config.OsdFadeInMs > 0;
        byte alpha = animate ? (byte)24 : (byte)255;
        if (!osdView.ShowOsd(_window.Handle, title, text, alpha) &&
            !osdView.ShowOsd(_window.Handle, title, text, alpha)) return;
        if (animate) AnimateOsd(alpha, 255, _config.OsdFadeInMs, static () => { });
        if (!persistent) osdDeadline = _window.ScheduleOnce(TimeSpan.FromMilliseconds(1250), () =>
        {
            osdDeadline = null;
            if (micOsd) ShowOsd(micOsdTitle, micOsdText, persistent: true);
            else AnimateOsd(255, 0, _config.OsdFadeOutMs, () => osdView?.HideAndRelease());
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
            if (osdView == null || !osdView.IsVisible) return;
            float amount = Math.Clamp((Environment.TickCount64 - start) / (float)duration, 0f, 1f);
            osdView.SetOsdAlpha((byte)MathF.Round(from + (to - from) * amount));
            if (amount >= 1) completed();
            else osdAnimation = _window.ScheduleOnce(TimeSpan.FromMilliseconds(16), Frame);
        }
        osdAnimation = _window.ScheduleOnce(TimeSpan.FromMilliseconds(16), Frame);
    }

    private void ShowTrayMenu()
    {
        TrayMenuController.Shared.Show(
            this,
            _config,
            _window.Handle,
            onOpenMain: OpenProductMain,
            onOpenPanel: () => _feedback.Publish(FeedbackKind.ShowPanel),
            onExit: () => _window.RequestExit());
    }
}
