using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

/// <summary>
/// High-fidelity Direct2D tray context menu controller using PopupMenuWindow (ConvenienceMenu).
/// Opens immediately with lightweight controls and hydrates heavy audio/app/rule data asynchronously.
/// </summary>
internal sealed class TrayMenuController
{
    private static readonly int[] VolumePresetLevels = [25, 50, 75, 100];

    private PopupMenuWindow? _activeMenu;
    private CancellationTokenSource? _loadCts;
    private long _requestId;

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

        long requestId = ++_requestId;
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        nint foregroundHwnd = ShellNative.GetForegroundWindow();
        ViewNative.GetCursorPos(out var cursor);

        var theme = UiThemePalette.Resolve(config);
        var initial = BuildInitialItems(host, config, onOpenMain, onOpenPanel, onExit);
        var menu = PopupMenuWindow.ShowAtCursor(theme, initial, ownerHandle, cursor.X, cursor.Y, alignBottomUp: true);
        _activeMenu = menu;

        CancellationToken ct = cts.Token;
        _ = Task.Run(() =>
        {
            List<UiMenuItem> full;
            try
            {
                full = BuildFullItems(host, config, foregroundHwnd, onOpenMain, onOpenPanel, onExit, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ExceptionDiagnostics.Record("TrayMenuController.Build", ex);
                full = BuildUnavailableItems(host, config, onOpenMain, onOpenPanel, onExit);
            }

            if (ct.IsCancellationRequested) return;
            host.TryPost(() =>
            {
                if (ct.IsCancellationRequested || _requestId != requestId) return;
                if (!ReferenceEquals(_activeMenu, menu) || menu.IsDisposed) return;
                menu.UpdateItems(full);
            });
        }, ct);
    }

    internal void CloseActive()
    {
        ++_requestId;
        if (_loadCts is { } cts)
        {
            _loadCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
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

    private static List<UiMenuItem> BuildInitialItems(
        ApplicationHost host,
        AppConfig config,
        Action? onOpenMain,
        Action? onOpenPanel,
        Action? onExit)
    {
        var items = new List<UiMenuItem>();
        AddTopItems(items, host, onOpenMain, onOpenPanel);
        items.Add(UiMenuItem.Separator());
        items.Add(new UiMenuItem { Text = L10n.T("Tray.Loading"), Enabled = false });
        AddBottomItems(items, host, config, onExit);
        return items;
    }

    private static List<UiMenuItem> BuildUnavailableItems(
        ApplicationHost host,
        AppConfig config,
        Action? onOpenMain,
        Action? onOpenPanel,
        Action? onExit)
    {
        var items = new List<UiMenuItem>();
        AddTopItems(items, host, onOpenMain, onOpenPanel);
        items.Add(UiMenuItem.Separator());
        items.Add(new UiMenuItem { Text = L10n.T("Tray.AudioUnavailable"), Enabled = false });
        AddBottomItems(items, host, config, onExit);
        return items;
    }

    private static List<UiMenuItem> BuildFullItems(
        ApplicationHost host,
        AppConfig config,
        nint foregroundHwnd,
        Action? onOpenMain,
        Action? onOpenPanel,
        Action? onExit,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var items = new List<UiMenuItem>();
        AddTopItems(items, host, onOpenMain, onOpenPanel);
        items.Add(UiMenuItem.Separator());

        Endpoint[] renderDevices = [];
        string? defaultRenderId = null;
        bool defRenderMuted = false;
        int defRenderVol = -1;
        try
        {
            renderDevices = host.Audio.RenderDevicesWithPhone();
            if (renderDevices.Length > 0)
            {
                defaultRenderId = ProductAudio.DefaultId(Flow.Render);
                var st = host.Audio.Volume(Flow.Render, null, 0);
                defRenderMuted = st.Muted;
                defRenderVol = st.Percent;
            }
        }
        catch { }

        ct.ThrowIfCancellationRequested();

        var visibleOutputs = renderDevices
            .Where(d => !config.HiddenOutputDevices.Contains(d.Id))
            .ToArray();

        Endpoint[] captureDevices = [];
        Endpoint[] visibleInputs = [];
        string? defaultCaptureId = null;
        bool defCaptureMuted = false;
        int defCaptureVol = -1;
        bool micKnown = false;
        bool allMicsMuted = false;
        int mutedMicCount = 0;

        if (config.ExperimentalMic)
        {
            try
            {
                captureDevices = ProductAudio.Devices(Flow.Capture);
                visibleInputs = captureDevices
                    .Where(d => !config.HiddenInputDevices.Contains(d.Id))
                    .ToArray();
                if (captureDevices.Length > 0)
                {
                    defaultCaptureId = ProductAudio.DefaultId(Flow.Capture);
                    var defCapSt = host.Audio.Volume(Flow.Capture, null, 0);
                    defCaptureMuted = defCapSt.Muted;
                    defCaptureVol = defCapSt.Percent;

                    micKnown = true;
                    foreach (var cap in captureDevices)
                    {
                        if (host.Audio.Volume(Flow.Capture, cap.Id).Muted) mutedMicCount++;
                    }
                    allMicsMuted = mutedMicCount == captureDevices.Length;
                }
            }
            catch { }
        }

        ct.ThrowIfCancellationRequested();

        // Current Foreground App & Running Apps
        AudioAppInfo? currentApp = null;
        try { currentApp = host.CurrentApp.Resolve(hwnd: foregroundHwnd); } catch { }
        if (currentApp != null)
        {
            items.Add(BuildAppItem(host, config, currentApp, visibleOutputs, visibleInputs, isCurrentHeader: true));
        }

        try
        {
            var otherApps = host.Audio.Apps()
                .Where(a => currentApp == null || a.ProcessId != currentApp.ProcessId || !string.Equals(a.Identity, currentApp.Identity, StringComparison.Ordinal))
                .Where(a => string.IsNullOrWhiteSpace(a.ProcessName) || !config.HiddenPanelApps.Contains(a.ProcessName))
                .Take(20)
                .ToList();
            if (otherApps.Count > 0)
            {
                var appSub = new List<UiMenuItem>(otherApps.Count);
                foreach (var app in otherApps)
                {
                    ct.ThrowIfCancellationRequested();
                    appSub.Add(BuildAppItem(host, config, app, visibleOutputs, visibleInputs, isCurrentHeader: false));
                }
                items.Add(new UiMenuItem
                {
                    Text = L10n.T("Nav.Apps"),
                    Icon = UiIcon.Apps,
                    SubItems = appSub,
                });
            }
        }
        catch { }

        // Default Output Devices
        items.Add(BuildDefaultOutputItem(host, config, visibleOutputs, defaultRenderId, defRenderMuted, defRenderVol));

        // Default Input Devices + Mute All Microphones
        if (config.ExperimentalMic)
        {
            items.Add(BuildDefaultInputItem(host, config, visibleInputs, defaultCaptureId, defCaptureMuted, defCaptureVol));

            string? micHint = !micKnown ? null
                : mutedMicCount > 0 && !allMicsMuted ? L10n.T("Tray.MicrophonesMixed") : null;
            string micTip = !micKnown
                ? L10n.T(captureDevices.Length == 0 ? "Ov.NoMicDevice" : "Tray.AudioUnavailable")
                : micHint ?? "";

            items.Add(new UiMenuItem
            {
                Text = L10n.T("Tray.MuteAllMicrophones"),
                Icon = UiIcon.Microphone,
                Checked = micKnown && allMicsMuted,
                Enabled = micKnown,
                RightHint = micHint,
                Tooltip = string.IsNullOrEmpty(micTip) ? null : micTip,
                Click = () => RunAudioOp(host, () =>
                {
                    bool next = host.Audio.GlobalMicMute(!allMicsMuted);
                    host.TryPost(() => host.PublishOsd(L10n.T("Ov.MuteMic"), L10n.T(next ? "Ov.MicMuted" : "Ov.MicUnmuted"), muted: next));
                }),
            });
        }

        // Reset All App Routes
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Act.ResetAllApps"),
            Icon = UiIcon.Reset,
            Click = () => RunAudioOp(host, () =>
            {
                bool ok = host.Audio.ResetRoutes();
                host.TryPost(() => host.PublishOsd(L10n.T("Act.ResetAllApps"), L10n.T(ok ? "Act.ResetAllAppsDone" : "Act.ResetAllAppsFailed"), success: ok));
            }),
        });

        ct.ThrowIfCancellationRequested();

        // Favorite Rules (up to 20)
        items.Add(BuildFavoritesMenu(host, config));

#if SONICROUTE_PHONE
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Phone.OpenPhoneAudio"),
            Icon = UiIcon.Phone,
            Click = () => host.OpenPhoneManagement(),
        });
#endif

        AddBottomItems(items, host, config, onExit);
        return items;
    }

    private static void AddTopItems(
        List<UiMenuItem> items,
        ApplicationHost host,
        Action? onOpenMain,
        Action? onOpenPanel)
    {
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Tray.OpenPanel"),
            Icon = UiIcon.Options,
            Click = () => onOpenPanel?.Invoke(),
        });
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Tray.OpenMain"),
            Icon = UiIcon.Home,
            Click = () => (onOpenMain ?? host.OpenProductMain)(),
        });
        if (host.Updates.ShouldNotify && host.Updates.Release is { } release)
        {
            items.Add(new UiMenuItem
            {
                Text = string.Format(L10n.T("Upd.Notice"), release.Tag),
                Dot = UiMenuDotKind.Accent,
                Icon = UiIcon.Refresh,
                Click = () => (onOpenMain ?? host.OpenProductMain)(),
            });
        }
    }

    private static void AddBottomItems(
        List<UiMenuItem> items,
        ApplicationHost host,
        AppConfig config,
        Action? onExit)
    {
        items.Add(UiMenuItem.Separator());
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Tray.PauseWheel"),
            Checked = config.TrayWheelPaused,
            Tooltip = L10n.T("Tray.PauseWheelHint"),
            Click = () => ToggleWheelSetting(host, config, everywhere: false),
        });
        items.Add(new UiMenuItem
        {
            Text = L10n.T("St.TrayWheelEverywhere"),
            Checked = config.TrayWheelEverywhere,
            Tooltip = L10n.T("St.TrayWheelEverywhereHint"),
            Click = () => ToggleWheelSetting(host, config, everywhere: true),
        });
        items.Add(UiMenuItem.Separator());
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Tray.Exit"),
            Icon = UiIcon.Exit,
            Danger = true,
            Click = () => onExit?.Invoke(),
        });
    }

    private static void ToggleWheelSetting(ApplicationHost host, AppConfig config, bool everywhere)
    {
        bool prevPaused = config.TrayWheelPaused;
        bool prevEverywhere = config.TrayWheelEverywhere;
        if (everywhere) config.TrayWheelEverywhere = !prevEverywhere;
        else config.TrayWheelPaused = !prevPaused;

        if (!ConfigService.TrySave(config))
        {
            config.TrayWheelPaused = prevPaused;
            config.TrayWheelEverywhere = prevEverywhere;
            return;
        }
        host.ViewSettingsChanged();
    }

    private static UiMenuItem BuildDefaultOutputItem(
        ApplicationHost host,
        AppConfig config,
        Endpoint[] visibleOutputs,
        string? defaultRenderId,
        bool muted,
        int currentVol)
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
                Click = () => RunAudioOp(host, () =>
                {
                    host.Audio.SetDefault(Flow.Render, devId);
                    host.TryPost(() => host.PublishOsd(L10n.T("Dev.SystemDefaultOut"), "🔊 " + name));
                }),
            });
        }

        if (sub.Count == 0)
        {
            sub.Add(new UiMenuItem { Text = L10n.T("Ov.NoDevice"), Enabled = false });
        }
        else
        {
            sub.Add(UiMenuItem.Separator());
            sub.Add(new UiMenuItem
            {
                Text = L10n.T(muted ? "Qp.Unmute" : "Qp.Mute"),
                Checked = muted,
                Click = () => RunAudioOp(host, () =>
                {
                    var st = host.Audio.Volume(Flow.Render, null, 0, toggle: true);
                    host.TryPost(() => host.PublishOsd(L10n.T("Dev.SystemDefaultOut"), st.Muted ? L10n.T("Ov.AppMuted") : $"{st.Percent}%", volume: st.Percent, muted: st.Muted));
                }),
            });
            sub.Add(BuildPresetsSubmenu(currentVol, pct => RunAudioOp(host, () =>
            {
                var st = host.Audio.Volume(Flow.Render, null, 0, level: pct);
                host.TryPost(() => host.PublishOsd(L10n.T("Dev.SystemDefaultOut"), st.Muted ? L10n.T("Ov.AppMuted") : $"{st.Percent}%", volume: st.Percent, muted: st.Muted));
            })));
        }

        string activeName = defaultRenderId != null &&
            visibleOutputs.FirstOrDefault(d => string.Equals(d.Id, defaultRenderId, StringComparison.OrdinalIgnoreCase)) is { } match
                ? config.DeviceNames.GetValueOrDefault(match.Id, match.Name)
                : "";

        return new UiMenuItem
        {
            Text = L10n.T("Dev.SystemDefaultOut"),
            Icon = UiIcon.Speaker,
            RightHint = string.IsNullOrEmpty(activeName) ? null : activeName,
            SubItems = sub,
        };
    }

    private static UiMenuItem BuildDefaultInputItem(
        ApplicationHost host,
        AppConfig config,
        Endpoint[] visibleInputs,
        string? defaultCaptureId,
        bool muted,
        int currentVol)
    {
        var sub = new List<UiMenuItem>();
        foreach (var dev in visibleInputs)
        {
            string devId = dev.Id;
            string name = config.DeviceNames.GetValueOrDefault(devId, dev.Name);
            bool isDef = defaultCaptureId != null && string.Equals(devId, defaultCaptureId, StringComparison.OrdinalIgnoreCase);
            sub.Add(new UiMenuItem
            {
                Text = name,
                Checked = isDef,
                Click = () => RunAudioOp(host, () =>
                {
                    host.Audio.SetDefault(Flow.Capture, devId);
                    host.TryPost(() => host.PublishOsd(L10n.T("Dev.SystemDefaultIn"), "🎙️ " + name));
                }),
            });
        }

        if (sub.Count == 0)
        {
            sub.Add(new UiMenuItem { Text = L10n.T("Ov.NoMicDevice"), Enabled = false });
        }
        else
        {
            sub.Add(UiMenuItem.Separator());
            sub.Add(new UiMenuItem
            {
                Text = L10n.T(muted ? "Qp.Unmute" : "Qp.Mute"),
                Checked = muted,
                Click = () => RunAudioOp(host, () =>
                {
                    var st = host.Audio.Volume(Flow.Capture, null, 0, toggle: true);
                    host.TryPost(() => host.PublishOsd(L10n.T("Dev.SystemDefaultIn"), st.Muted ? L10n.T("Ov.MicMuted") : $"{st.Percent}%", volume: st.Percent, muted: st.Muted));
                }),
            });
            sub.Add(BuildPresetsSubmenu(currentVol, pct => RunAudioOp(host, () =>
            {
                var st = host.Audio.Volume(Flow.Capture, null, 0, level: pct);
                host.TryPost(() => host.PublishOsd(L10n.T("Dev.SystemDefaultIn"), st.Muted ? L10n.T("Ov.MicMuted") : $"{st.Percent}%", volume: st.Percent, muted: st.Muted));
            })));
        }

        string activeName = defaultCaptureId != null &&
            visibleInputs.FirstOrDefault(d => string.Equals(d.Id, defaultCaptureId, StringComparison.OrdinalIgnoreCase)) is { } match
                ? config.DeviceNames.GetValueOrDefault(match.Id, match.Name)
                : "";

        return new UiMenuItem
        {
            Text = L10n.T("Dev.SystemDefaultIn"),
            Icon = UiIcon.Microphone,
            RightHint = string.IsNullOrEmpty(activeName) ? null : activeName,
            SubItems = sub,
        };
    }

    private static UiMenuItem BuildAppItem(
        ApplicationHost host,
        AppConfig config,
        AudioAppInfo app,
        Endpoint[] visibleOutputs,
        Endpoint[] visibleInputs,
        bool isCurrentHeader)
    {
        int pid = checked((int)app.ProcessId);
        string appName = app.ProcessName is { } proc && config.AppNames.TryGetValue(proc, out string? custom) && custom.Length > 0
            ? custom
            : app.Label;

        string? persistedOut = null;
        string? persistedIn = null;
        bool muted = false;
        int vol = -1;
        try
        {
            persistedOut = host.Audio.ReadRoute(app, Flow.Render);
            if (config.ExperimentalMic && !app.IsExternal)
                persistedIn = host.Audio.ReadRoute(pid, Flow.Capture);
            var st = host.Audio.Volume(app);
            muted = st.Muted;
            vol = st.Percent;
        }
        catch { }

        string currentOutRoute = persistedOut ?? AudioService.SystemDefaultDeviceId;
        var outSub = new List<UiMenuItem>
        {
            new()
            {
                Text = L10n.T("Dev.SystemDefaultOut"),
                Checked = AudioService.IsSystemDefault(currentOutRoute),
                Click = () => ApplyAppOutput(host, app, pid, AudioService.SystemDefaultDeviceId),
            },
        };
        foreach (var dev in visibleOutputs)
        {
            string devId = dev.Id;
            string name = config.DeviceNames.GetValueOrDefault(devId, dev.Name);
            outSub.Add(new UiMenuItem
            {
                Text = name,
                Checked = string.Equals(currentOutRoute, devId, StringComparison.OrdinalIgnoreCase),
                Click = () => ApplyAppOutput(host, app, pid, devId),
            });
        }

        var sub = new List<UiMenuItem>
        {
            new()
            {
                Text = L10n.T("Act.Mute"),
                Checked = muted,
                Click = () => RunAudioOp(host, () =>
                {
                    var st = host.Audio.Volume(app, toggle: true);
                    host.CurrentApp.RecordUsedApp(app);
                    host.TryPost(() => host.PublishOsd(appName, st.Muted ? L10n.T("Ov.AppMuted") : $"{st.Percent}%", volume: st.Percent, muted: st.Muted));
                }),
            },
            BuildPresetsSubmenu(vol, pct => RunAudioOp(host, () =>
            {
                var st = host.Audio.Volume(app, level: pct);
                host.CurrentApp.RecordUsedApp(app);
                host.TryPost(() => host.PublishOsd(appName, st.Muted ? L10n.T("Ov.AppMuted") : $"{st.Percent}%", volume: st.Percent, muted: st.Muted));
            })),
            new()
            {
                Text = L10n.T("Qp.Output"),
                Icon = UiIcon.Speaker,
                SubItems = outSub,
            },
        };

        if (config.ExperimentalMic && !app.IsExternal && visibleInputs.Length > 0)
        {
            string currentInRoute = persistedIn ?? AudioService.SystemDefaultDeviceId;
            var inSub = new List<UiMenuItem>
            {
                new()
                {
                    Text = L10n.T("Dev.SystemDefaultIn"),
                    Checked = AudioService.IsSystemDefault(currentInRoute),
                    Click = () => RunAudioOp(host, () =>
                    {
                        host.Audio.SetRoute(pid, Flow.Capture, null);
                        host.TryPost(() => host.PublishOsd(appName, "🎙️ " + L10n.T("Dev.SystemDefaultIn")));
                    }),
                },
            };
            foreach (var dev in visibleInputs)
            {
                string devId = dev.Id;
                string name = config.DeviceNames.GetValueOrDefault(devId, dev.Name);
                inSub.Add(new UiMenuItem
                {
                    Text = name,
                    Checked = string.Equals(currentInRoute, devId, StringComparison.OrdinalIgnoreCase),
                    Click = () => RunAudioOp(host, () =>
                    {
                        host.Audio.SetRoute(pid, Flow.Capture, devId);
                        host.TryPost(() => host.PublishOsd(appName, "🎙️ " + name));
                    }),
                });
            }
            sub.Add(new UiMenuItem
            {
                Text = L10n.T("Qp.Input"),
                Icon = UiIcon.Microphone,
                SubItems = inSub,
            });
        }

        return new UiMenuItem
        {
            Text = isCurrentHeader ? string.Format(L10n.T("Qp.CurrentApp"), appName) : appName,
            Icon = UiIcon.Application,
            RightHint = vol >= 0 ? (muted ? L10n.T("Ov.Muted") : $"{vol}%") : null,
            SubItems = sub,
        };
    }

    private static UiMenuItem BuildPresetsSubmenu(int currentVol, Action<int> applyPercent)
    {
        var items = new List<UiMenuItem>(VolumePresetLevels.Length);
        foreach (int level in VolumePresetLevels)
        {
            int pct = level;
            items.Add(new UiMenuItem
            {
                Text = $"{pct}%",
                Checked = currentVol == pct,
                Click = () => applyPercent(pct),
            });
        }
        return new UiMenuItem
        {
            Text = L10n.T("Tray.VolumePresets"),
            SubItems = items,
        };
    }

    private static UiMenuItem BuildFavoritesMenu(ApplicationHost host, AppConfig config)
    {
        var favIds = (config.FavoriteRuleIds ?? new List<string>())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();

        var sub = new List<UiMenuItem>();
        if (favIds.Count > 0)
        {
            var byId = AutoRuleStore.LoadAll().ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (string id in favIds)
            {
                if (!byId.TryGetValue(id, out var rule)) continue;
                string ruleId = rule.Id;
                sub.Add(new UiMenuItem
                {
                    Text = rule.Name,
                    Icon = UiIcon.Play,
                    Enabled = !AutoRuleService.IsRunning(ruleId),
                    Click = () => _ = host.RunRule(ruleId),
                });
            }
        }

        if (sub.Count == 0)
        {
            sub.Add(new UiMenuItem { Text = L10n.T("Auto.FavoritesEmpty"), Enabled = false });
        }

        return new UiMenuItem
        {
            Text = L10n.T("Auto.Favorites"),
            Icon = UiIcon.Automation,
            SubItems = sub,
        };
    }

    private static void ApplyAppOutput(ApplicationHost host, AudioAppInfo app, int pid, string targetId)
    {
#if SONICROUTE_PHONE
        if (app.IsExternal)
        {
            _ = host.Audio.SetExternalOutputsAsync(app, [targetId]);
            return;
        }
#endif
        _ = host.CurrentApp.SelectOutputAsync(pid, targetId);
    }

    private static void RunAudioOp(ApplicationHost host, Action op)
    {
        _ = Task.Run(() =>
        {
            try
            {
                op();
                host.NotifyAudioVisualChanged();
            }
            catch (Exception ex) { ExceptionDiagnostics.Record("TrayMenuController", ex); }
        });
    }
}
