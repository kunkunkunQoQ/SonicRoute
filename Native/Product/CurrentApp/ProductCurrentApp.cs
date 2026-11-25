using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Models;

namespace SonicRoute.Native.Product;

internal sealed partial class ProductCurrentApp(ProductAudio audio, IMainThread owner, FeedbackState feedback) : IDisposable
{
    private readonly CurrentAppSelection _selection = new();
    private IDisposable? _retry, _hookRecovery;
    private Task _foregroundRead = Task.CompletedTask;
    private int _pid = -1, _budget;
    private nint _hwnd;
    private long _foregroundGeneration;
    private bool _running, _refreshPending;
    private volatile bool _closed;

    internal AudioAppInfo? Current { get => _selection.Current; set => _selection.Current = value; }
    internal AudioAppInfo? LastForeground => _selection.LastForeground;
    internal event Action? CurrentChanged { add => _selection.Changed += value; remove => _selection.Changed -= value; }

    internal void RecordUsedApp(AudioAppInfo? app)
    {
        if (app == null || string.IsNullOrWhiteSpace(app.ProcessName)) return;
        owner.PostContinuation(() =>
        {
            if (_closed) return;
            _selection.Current = app;
            var cfg = ConfigService.Load();
            if (string.Equals(cfg.LastUsedAppName, app.ProcessName, StringComparison.OrdinalIgnoreCase)) return;
            cfg.LastUsedAppName = app.ProcessName;
            if (!ConfigService.TryCommit(cfg))
                feedback.Publish(FeedbackKind.Osd, BuildIdentity.DisplayName, L10n.T("Tray.SaveFailed"), success: false);
        });
    }

    internal bool NeedsForeground
    {
        get
        {
            var c = ConfigService.Load();
            if (c.DefaultAppMode == "recent" || !c.TrayWheelPaused) return true;
            // Missing entries use the shipped defaults. Only an explicit empty binding
            // disables an action. Match ReloadRules, including the optional capture action.
            foreach (var pair in HotkeyActions.Defaults)
            {
                if (pair.Key is not (HotkeyActions.ActSwitchOutput or HotkeyActions.ActSwitchInput or
                    HotkeyActions.ActMute or HotkeyActions.ActMuteInput or HotkeyActions.ActVolUp or HotkeyActions.ActVolDown)) continue;
                if (pair.Key == HotkeyActions.ActSwitchInput && !c.ExperimentalMic) continue;
                string combo = c.Hotkeys.TryGetValue(pair.Key, out var configured) ? configured : pair.Value;
                if (!string.IsNullOrWhiteSpace(combo)) return true;
            }
            return false;
        }
    }

    [LibraryImport("user32")] private static partial uint GetWindowThreadProcessId(nint window, out uint pid);

    private static int WindowProcess(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        return (int)pid;
    }

    private AudioAppInfo? Foreground(List<AudioAppInfo> apps, AppConfig config, int pid, nint hwnd = 0)
    {
        if (pid <= 0) return null;
#if SONICROUTE_PHONE
        if (pid == Environment.ProcessId)
        {
            if (hwnd == 0) hwnd = ShellNative.GetForegroundWindow();
            if (audio.ExternalAudio?.TryResolveVirtualDisplayPackage(hwnd, pid) is { } pkg)
                return CurrentAppSelection.MatchForeground(apps, config, pid, allowExactPid: false, () => pkg);
            return null;
        }
#else
        if (pid == Environment.ProcessId) return null;
#endif
        return CurrentAppSelection.MatchForeground(apps, config, pid, true, () => ForegroundAppService.GetProcessNameSafe(pid));
    }

    // MTA caller. Capture HWND or PID at command acceptance so a transient menu does not retarget the write.
    internal AudioAppInfo? Resolve(int? foregroundPid = null, nint hwnd = 0)
    {
        nint effectiveHwnd = hwnd == 0 ? ShellNative.GetForegroundWindow() : hwnd;
        int pid = foregroundPid ?? WindowProcess(effectiveHwnd);
#if SONICROUTE_PHONE
        if (pid == Environment.ProcessId && foregroundPid.HasValue && hwnd == 0)
        {
            nint live = ShellNative.GetForegroundWindow();
            if (WindowProcess(live) == pid) effectiveHwnd = live;
        }
#endif
        var config = ConfigService.Load();
        var apps = audio.Apps();
        return _selection.Resolve(apps, config, () => Foreground(apps, config, pid, effectiveHwnd));
    }

    internal void Refresh()
    {
        _pid = -1;
        _hwnd = 0;
        OnForegroundChanged(ShellNative.GetForegroundWindow());
    }

    internal void WatchHealth(bool required, bool installed, Action restore)
    {
        if (_closed || !required || installed)
        {
            _hookRecovery?.Dispose();
            _hookRecovery = null;
            return;
        }
        _hookRecovery ??= owner.ScheduleOnce(TimeSpan.FromMilliseconds(1500), () =>
        {
            _hookRecovery = null;
            restore();
            Refresh();
        });
    }

    // The shell owns the single WinEvent subscription shared with foreground automation.
    internal void OnForegroundChanged(nint hwnd)
    {
        if (_closed) return;
        int pid = NeedsForeground ? WindowProcess(hwnd) : 0;
        bool ownVirtual = false;
#if SONICROUTE_PHONE
        ownVirtual = pid == Environment.ProcessId && (audio.ExternalAudio?.IsVirtualDisplayWindow(hwnd) == true);
#endif
        if (pid == _pid && (!ownVirtual || hwnd == _hwnd)) return;
        _retry?.Dispose(); _retry = null;
        _pid = pid;
        _hwnd = hwnd;
        ++_foregroundGeneration;
        _budget = 2;
        _refreshPending = pid > 0 && (pid != Environment.ProcessId || ownVirtual);
        QueryForeground();
    }

    private void QueryForeground()
    {
        if (_closed || _running || !_refreshPending) return;
        _running = true;
        _refreshPending = false;
        int pid = _pid;
        nint hwnd = _hwnd;
        long generation = _foregroundGeneration;
        _foregroundRead = Task.Run(() =>
        {
            AudioAppInfo? match = null;
            bool failed = false;
            try { match = Foreground(audio.Apps(), ConfigService.Load(), pid, hwnd); }
            catch { failed = true; }
            if (!_closed) owner.PostContinuation(() => CompleteQuery(generation, pid, hwnd, match, failed));
        });
    }

    private void CompleteQuery(long generation, int pid, nint hwnd, AudioAppInfo? match, bool failed)
    {
        _running = false;
        if (_closed) return;
        if (generation == _foregroundGeneration)
        {
            nint live = ShellNative.GetForegroundWindow();
            if (WindowProcess(live) != pid || (pid == Environment.ProcessId && live != hwnd))
            {
                OnForegroundChanged(live);
                return;
            }
            if (match != null) _selection.Observe(match, ConfigService.Load());
            bool repeat = failed || ConfigService.Load().BackgroundPollingFallbackEnabled || (match == null && _budget-- > 0);
            if (repeat && NeedsForeground)
                _retry = owner.ScheduleOnce(TimeSpan.FromMilliseconds(1500), () =>
                {
                    _retry = null;
                    _refreshPending = true;
                    QueryForeground();
                });
        }
        QueryForeground();
    }

    public void Dispose()
    {
        _closed = true;
        ++_foregroundGeneration;
        _retry?.Dispose();
        _retry = null;
        _hookRecovery?.Dispose();
        _foregroundRead.GetAwaiter().GetResult();
        DrainAsync().GetAwaiter().GetResult();
    }
}
