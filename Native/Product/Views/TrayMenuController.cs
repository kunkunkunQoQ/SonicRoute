using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// High-fidelity Direct2D tray context menu controller using PopupMenuWindow (ConvenienceMenu).
/// </summary>
internal sealed class TrayMenuController
{
    private PopupMenuWindow? _activeMenu;

    internal static TrayMenuController Shared { get; } = new();

    internal void Show(
        ApplicationHost host,
        AppConfig config,
        nint ownerHandle,
        Action? onOpenMain = null,
        Action? onOpenPanel = null,
        Action? onExit = null)
    {
        CloseActive();

        nint foregroundHwnd = ShellNative.GetForegroundWindow();
        ViewNative.GetCursorPos(out var cursor);

        var theme = UiThemePalette.Resolve(config);
        var items = BuildItems(host, config, foregroundHwnd, onOpenMain, onOpenPanel, onExit);
        _activeMenu = PopupMenuWindow.ShowAtCursor(theme, items, ownerHandle, cursor.X, cursor.Y, alignBottomUp: true);
    }

    internal void CloseActive()
    {
        if (_activeMenu is { } existing)
        {
            _activeMenu = null;
            if (!existing.IsDisposed)
            {
                existing.CloseWindow();
                existing.Dispose();
            }
        }
    }

    private static List<UiMenuItem> BuildItems(
        ApplicationHost host,
        AppConfig config,
        nint foregroundHwnd,
        Action? onOpenMain,
        Action? onOpenPanel,
        Action? onExit)
    {
        var items = new List<UiMenuItem>
        {
            new()
            {
                Text = L10n.T("Tray.OpenMain"),
                Icon = UiIcon.Home,
                Click = () => (onOpenMain ?? host.OpenProductMain)(),
            },
            new()
            {
                Text = L10n.T("Tray.OpenPanel"),
                Icon = UiIcon.Options,
                Click = () => onOpenPanel?.Invoke(),
            },
            UiMenuItem.Separator(),
        };

        Endpoint[] renderDevices = [];
        string? defaultRenderId = null;
        try
        {
            renderDevices = ProductAudio.Devices(Flow.Render);
            if (renderDevices.Length > 0) defaultRenderId = ProductAudio.DefaultId(Flow.Render);
        }
        catch { }

        var visibleOutputs = renderDevices
            .Where(d => !config.HiddenOutputDevices.Contains(d.Id))
            .ToArray();

        // Default Output submenu
        items.Add(BuildDefaultOutputItem(host, config, visibleOutputs, defaultRenderId));

        // Current Foreground App submenu
        var currentApp = host.CurrentApp.Resolve(hwnd: foregroundHwnd);
        if (currentApp != null)
        {
            items.Add(BuildCurrentAppItem(host, config, currentApp, visibleOutputs));
        }

        // Global Mic Mute
        if (config.ExperimentalMic)
        {
            bool allMicsMuted = false;
            bool micKnown = false;
            try
            {
                var caps = ProductAudio.Devices(Flow.Capture);
                if (caps.Length > 0)
                {
                    micKnown = true;
                    allMicsMuted = caps.All(d => host.Audio.Volume(Flow.Capture, d.Id).Muted);
                }
            }
            catch { }

            items.Add(new UiMenuItem
            {
                Text = L10n.T("Tray.MuteAllMicrophones"),
                Icon = UiIcon.Microphone,
                Checked = micKnown && allMicsMuted,
                Enabled = micKnown,
                Click = () => RunAudioOp(() => host.Audio.GlobalMicMute(null)),
            });
        }

        // Pause Wheel
        if (host.PreviewGlobalControls)
        {
            items.Add(new UiMenuItem
            {
                Text = L10n.T("Tray.PauseWheel"),
                Checked = config.TrayWheelPaused,
                Click = () =>
                {
                    config.TrayWheelPaused = !config.TrayWheelPaused;
                    ConfigService.TrySave(config);
                    host.ViewSettingsChanged();
                },
            });
        }

        // Favorite Rules (up to 20)
        if (config.FavoriteRuleIds.Count > 0)
        {
            var favRules = AutoRuleStore.LoadAll()
                .Where(r => config.FavoriteRuleIds.Contains(r.Id))
                .Take(20)
                .ToArray();
            if (favRules.Length > 0)
            {
                foreach (var rule in favRules)
                {
                    string ruleId = rule.Id;
                    items.Add(new UiMenuItem
                    {
                        Text = rule.Name,
                        Icon = UiIcon.Automation,
                        Click = () => _ = host.RunRule(ruleId),
                    });
                }
            }
        }

        items.Add(UiMenuItem.Separator());
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Tray.Exit"),
            Icon = UiIcon.Exit,
            Danger = true,
            Click = () => onExit?.Invoke(),
        });

        return items;
    }

    private static UiMenuItem BuildDefaultOutputItem(
        ApplicationHost host,
        AppConfig config,
        Endpoint[] visibleOutputs,
        string? defaultRenderId)
    {
        var sub = new List<UiMenuItem>();
        foreach (var dev in visibleOutputs)
        {
            string devId = dev.Id;
            string name = config.DeviceNames.GetValueOrDefault(devId, dev.Name);
            bool isDef = defaultRenderId != null && string.Equals(devId, defaultRenderId, StringComparison.OrdinalIgnoreCase);
            sub.Add(new UiMenuItem
            {
                Text = name,
                Checked = isDef,
                Click = () => RunAudioOp(() => host.Audio.SetDefault(Flow.Render, devId)),
            });
        }

        if (sub.Count == 0)
        {
            sub.Add(new UiMenuItem { Text = L10n.T("Ov.NoDevice"), Enabled = false });
        }
        else
        {
            sub.Add(UiMenuItem.Separator());
            bool muted = false;
            int currentVol = -1;
            try
            {
                var st = host.Audio.Volume(Flow.Render, null, 0);
                muted = st.Muted;
                currentVol = st.Percent;
            }
            catch { }

            sub.Add(new UiMenuItem
            {
                Text = muted ? L10n.T("Qp.Unmute") : L10n.T("Qp.Mute"),
                Checked = muted,
                Click = () => RunAudioOp(() => host.Audio.Volume(Flow.Render, null, 0, toggle: true)),
            });
            foreach (int level in new[] { 25, 50, 75, 100 })
            {
                int pct = level;
                sub.Add(new UiMenuItem
                {
                    Text = $"{pct}%",
                    Checked = currentVol == pct,
                    Click = () => RunAudioOp(() => host.Audio.Volume(Flow.Render, null, 0, level: pct)),
                });
            }
        }

        string activeName = defaultRenderId != null
            ? visibleOutputs.FirstOrDefault(d => string.Equals(d.Id, defaultRenderId, StringComparison.OrdinalIgnoreCase)) is { } match
                ? config.DeviceNames.GetValueOrDefault(match.Id, match.Name)
                : L10n.T("Dev.SystemDefaultOut")
            : L10n.T("Dev.SystemDefaultOut");

        return new UiMenuItem
        {
            Text = L10n.T("Dev.SystemDefaultOut"),
            Icon = UiIcon.Speaker,
            RightHint = visibleOutputs.Length > 0 ? activeName : null,
            SubItems = sub,
        };
    }

    private static UiMenuItem BuildCurrentAppItem(
        ApplicationHost host,
        AppConfig config,
        Core.Models.AudioAppInfo currentApp,
        Endpoint[] visibleOutputs)
    {
        int pid = checked((int)currentApp.ProcessId);
        string appLabel = currentApp.ProcessName is { } proc && config.AppNames.TryGetValue(proc, out string? custom) && custom.Length > 0
            ? custom
            : currentApp.Label;

        string? persisted = null;
        bool muted = false;
        int vol = -1;
        try
        {
            persisted = host.Audio.ReadRoute(pid, Flow.Render);
            var st = host.Audio.Volume(Flow.Render, null, pid);
            muted = st.Muted;
            vol = st.Percent;
        }
        catch { }

        string currentRoute = persisted ?? AudioService.SystemDefaultDeviceId;
        var sub = new List<UiMenuItem>
        {
            new()
            {
                Text = L10n.T("Dev.SystemDefaultOut"),
                Checked = AudioService.IsSystemDefault(currentRoute),
                Click = () => _ = host.CurrentApp.SelectOutputAsync(pid, AudioService.SystemDefaultDeviceId),
            },
        };

        foreach (var dev in visibleOutputs)
        {
            string devId = dev.Id;
            string name = config.DeviceNames.GetValueOrDefault(devId, dev.Name);
            sub.Add(new UiMenuItem
            {
                Text = name,
                Checked = string.Equals(currentRoute, devId, StringComparison.OrdinalIgnoreCase),
                Click = () => _ = host.CurrentApp.SelectOutputAsync(pid, devId),
            });
        }

        sub.Add(UiMenuItem.Separator());
        sub.Add(new UiMenuItem
        {
            Text = L10n.T("Act.Mute"),
            Checked = muted,
            Click = () => RunAudioOp(() => host.Audio.Volume(Flow.Render, null, pid, toggle: true)),
        });

        foreach (int level in new[] { 25, 50, 75, 100 })
        {
            int pct = level;
            sub.Add(new UiMenuItem
            {
                Text = $"{pct}%",
                Checked = vol == pct,
                Click = () => RunAudioOp(() => host.Audio.Volume(Flow.Render, null, pid, level: pct)),
            });
        }

        return new UiMenuItem
        {
            Text = appLabel,
            Icon = UiIcon.Application,
            SubItems = sub,
        };
    }

    private static void RunAudioOp(Action op)
    {
        _ = Task.Run(() =>
        {
            try { op(); }
            catch (Exception ex) { ExceptionDiagnostics.Record("TrayMenuController", ex); }
        });
    }
}
