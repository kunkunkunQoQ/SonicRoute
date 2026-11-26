using System.Diagnostics;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;


namespace SonicRoute.Native.Product.Views;

internal sealed class TrayMenuController
{
    private static readonly int[] Presets = [25, 50, 75, 100];
    private readonly SemaphoreSlim _audioCommands = new(1);
    private PopupMenuWindow? _activeMenu;
    private IDisposable? _openDeadline;
    private long _openGeneration;

    internal static TrayMenuController Shared { get; } = new();

    internal void Show(ApplicationHost host, AppConfig config, nint ownerHandle, ShellNative.Point anchor,
        Action onOpenMain, Action onOpenPanel, Action onOpenUpdates, Action onExit)
    {
        nint foreground = ShellNative.GetForegroundWindow();
        CloseActive();

        long generation = _openGeneration;
        Action<Endpoint?, string>? applyLate = null;

        // Typically the first frame shows the actual endpoint. A stalled audio service must
        // not prevent using Open Main or Exit. This deadline exists only for this invocation.
        _openDeadline = host.ScheduleOnce(TimeSpan.FromMilliseconds(80), () =>
        {
            _openDeadline = null;
            if (_openGeneration == generation) applyLate = Open(null, "Tray.Loading");
        });
        _ = Task.Run(() =>
        {
            Endpoint? endpoint = null;
            string fallback = "Ov.NoDevice";
            try { endpoint = ProductAudio.DefaultEndpoint(Flow.Render); }
            catch (Exception error) { ExceptionDiagnostics.Record("TrayMenu.Default", error); fallback = "Tray.AudioUnavailable"; }
            host.TryPost(() =>
            {
                if (_openGeneration != generation) return;
                _openDeadline?.Dispose();
                _openDeadline = null;
                if (applyLate == null) applyLate = Open(endpoint, fallback);
                else applyLate(endpoint, fallback);
            });
        });

        Action<Endpoint?, string> Open(Endpoint? endpoint, string fallback) => ShowReady(host, config, ownerHandle,
            anchor, foreground, onOpenMain, onOpenPanel, onOpenUpdates, onExit, endpoint, fallback);
    }

    private Action<Endpoint?, string> ShowReady(ApplicationHost host, AppConfig config, nint ownerHandle, ShellNative.Point anchor,
        nint foreground, Action onOpenMain, Action onOpenPanel, Action onOpenUpdates, Action onExit, Endpoint? initialOutput, string fallback)
    {
        var lifetime = new CancellationTokenSource();
        var token = lifetime.Token;
        var current = new UiMenuItem { Text = L10n.T("Ov.CurrentApp"), SubItems = Placeholder("Tray.Loading") };
        var mic = new UiMenuItem { Text = L10n.T("Tray.MuteAllMicrophones"), Enabled = false };
        var favorites = new UiMenuItem { Text = L10n.T("Auto.Favorites"), SubItems = Placeholder("Auto.FavoritesEmpty") };
        var items = new List<UiMenuItem>
        {
            new() { Text = L10n.T("Tray.OpenPanel"), Click = onOpenPanel },
            new() { Text = L10n.T("Tray.OpenMain"), Click = onOpenMain }
        };
        if (host.Updates.ShouldNotify && host.Updates.Release is { } release)
            items.Add(new() { Text = string.Format(L10n.T("Upd.Notice"), release.Tag), Click = onOpenUpdates });
        items.Add(UiMenuItem.Separator());
        items.Add(current);
        bool outputReadback = false;
        var output = OutputMenu(host, config, token, null, () => outputReadback = true);
        (output.Text, output.Tooltip) = OutputSummary(config, initialOutput, fallback);
        items.Add(output);
        items.Add(mic);
        items.Add(favorites);
        items.Add(UiMenuItem.Separator());
        items.Add(new UiMenuItem
        {
            Text = L10n.T("Tray.PauseWheel"), Tooltip = L10n.T("Tray.PauseWheelHint"), Checked = config.TrayWheelPaused,
            Click = () => host.SetTrayWheelPaused(!config.TrayWheelPaused)
        });
        items.Add(new() { Text = L10n.T("Tray.Exit"), Click = onExit });

        var menu = PopupMenuWindow.ShowAtCursor(UiThemePalette.Resolve(config), items, ownerHandle,
            anchor.X, anchor.Y, schedule: host.ScheduleOnce);
        if (menu.Handle == 0) { lifetime.Dispose(); return static (_, _) => { }; }
        _activeMenu = menu;
        menu.Closed += () =>
        {
            if (ReferenceEquals(_activeMenu, menu))
            {
                ++_openGeneration;
                _activeMenu = null;
            }
            lifetime.Cancel();
            lifetime.Dispose();
        };

        Read(host, menu, token, () => ReadTarget(host, foreground), target =>
            menu.UpdateItem(current, target == null
                ? new UiMenuItem { Text = current.Text, Enabled = false, Tooltip = L10n.T("Ov.NoOutputSession") }
                : CurrentMenu(host, config, target, token)),
            () => menu.UpdateItem(current, new UiMenuItem { Text = current.Text, SubItems = Placeholder("Tray.AudioUnavailable") }));

        Read(host, menu, token, () =>
        {
            var devices = ProductAudio.Devices(Flow.Capture);
            int muted = devices.Count(d => host.Audio.Volume(Flow.Capture, d.Id).Muted);
            return (Count: devices.Length, Muted: muted);
        }, state =>
        {
            bool allMuted = state.Count > 0 && state.Count == state.Muted;
            menu.UpdateItem(mic, new UiMenuItem
            {
                Text = mic.Text, Checked = allMuted, Enabled = state.Count > 0,
                Tooltip = state.Count == 0 ? L10n.T("Ov.NoMicDevice") :
                    state.Muted != 0 && !allMuted ? L10n.T("Tray.MicrophonesMixed") : null,
                Click = () => RunAudio(host, L10n.T("Ov.MuteMic"), null, () =>
                {
                    bool actual = host.Audio.GlobalMicMute(!allMuted);
                    return Task.FromResult<Feedback?>(new(L10n.T(actual ? "Ov.MicMuted" : "Ov.MicUnmuted"), Muted: actual));
                })
            });
        }, () => menu.UpdateItem(mic, new UiMenuItem { Text = mic.Text, Enabled = false, Tooltip = L10n.T("Tray.AudioUnavailable") }));

        var ids = config.FavoriteRuleIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length != 0)
            Read(host, menu, token, () => ids.Select(AutoRuleStore.Find).OfType<AutoRule>().ToArray(), rules =>
            {
                var children = rules.Select(rule => new UiMenuItem
                {
                    Text = rule.Name, Enabled = !AutoRuleService.IsRunning(rule.Id),
                    Click = () => _ = RunFavorite(host, rule.Id)
                }).ToList();
                if (children.Count != 0) menu.UpdateItem(favorites, new() { Text = favorites.Text, SubItems = children });
            }, () => { });

        return (endpoint, unavailableKey) =>
        {
            if (!outputReadback && menu.Handle != 0)
            {
                var (label, tooltip) = OutputSummary(config, endpoint, unavailableKey);
                menu.UpdateLabel(output, label, tooltip);
            }
        };
    }

    internal void CloseActive()
    {
        ++_openGeneration;
        _openDeadline?.Dispose();
        _openDeadline = null;
        _activeMenu?.CloseWindow();
    }

    private static List<UiMenuItem> Placeholder(string key) => [new() { Text = L10n.T(key), Enabled = false }];

    // UI reads die with their HWND. A command accepted by a leaf survives closing that menu.
    private static async void Read<T>(ApplicationHost host, PopupMenuWindow menu, CancellationToken token,
        Func<T> read, Action<T> apply, Action unavailable)
    {
        Action completed;
        try
        {
            var result = await Task.Run(read, token).ConfigureAwait(false);
            completed = () => apply(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            ExceptionDiagnostics.Record("TrayMenu.Read", error);
            completed = unavailable;
        }
        if (!token.IsCancellationRequested)
            host.TryPost(() => { if (!token.IsCancellationRequested && menu.Handle != 0) completed(); });
    }

    private sealed record Target(AudioAppInfo App, string Name, VolumeState State, long? Start);
    private sealed record Feedback(string Text, int? Volume = null, bool? Muted = null);

    private static Target? ReadTarget(ApplicationHost host, nint foreground)
    {
        var app = host.CurrentApp.Resolve(hwnd: foreground);
        if (app == null) return null;
        try
        {
            var state = host.Audio.Volume(app);
            return state.Percent < 0 ? null : new(app, AppDisplayName.Get(app), state, ProcessStart(app));
        }
        catch (InvalidOperationException) { return null; }
    }

    private static long? ProcessStart(AudioAppInfo app)
    {
        if (app.IsExternal) return null;
        try { using var process = Process.GetProcessById((int)app.ProcessId); return process.StartTime.Ticks; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { return null; }
    }

    private UiMenuItem CurrentMenu(ApplicationHost host, AppConfig config, Target target, CancellationToken token)
    {
        string title = string.Format(L10n.T("Qp.CurrentApp"), target.Name);
        var levels = Presets.Select(p => new UiMenuItem
        {
            Text = p + "%", Checked = p == target.State.Percent,
            Click = () => RunAudio(host, target.Name, target, () =>
            {
                var actual = host.Audio.Volume(target.App, level: p);
                if (actual.Percent < 0) throw new InvalidOperationException();
                return Task.FromResult<Feedback?>(new(string.Format(L10n.T("Qp.VolOk"), actual.Percent), actual.Percent, actual.Muted));
            })
        }).ToList();
        return new UiMenuItem
        {
            Text = title, Tooltip = title, MaxTextWidth = 300,
            SubItems =
            [
                new() { Text = L10n.T("Act.Mute"), Checked = target.State.Muted, Click = () =>
                    RunAudio(host, target.Name, target, () =>
                    {
                        bool requested = !target.State.Muted;
                        var actual = host.Audio.Volume(target.App, mute: requested);
                        if (actual.Muted != requested) throw new InvalidOperationException();
                        return Task.FromResult<Feedback?>(new(L10n.T(actual.Muted ? "Ov.AppMuted" : "Ov.AppUnmuted"), Muted: actual.Muted));
                    })
                },
                new() { Text = L10n.T("Tray.VolumePresets"), SubItems = levels },
                OutputMenu(host, config, token, target)
            ]
        };
    }

    private UiMenuItem OutputMenu(ApplicationHost host, AppConfig config, CancellationToken token, Target? target,
        Action? onOutputRead = null)
    {
        string title = L10n.T(target == null ? "Dev.SystemDefaultOut" : "Qp.Output");
        return new UiMenuItem
        {
            Text = title, MaxTextWidth = 300, SubItems = Placeholder("Tray.Loading"),
            SubmenuOpened = child =>
            {

                Read(host, child, token, () =>
                {
                    if (target == null)
                    {
                        var devices = ProductAudio.Devices(Flow.Render);
                        return (Devices: devices, Selected: devices.Length == 0 ? null : ProductAudio.DefaultId(Flow.Render));
                    }
                    var outputs = host.Audio.RenderDevicesWithPhone(target.App, includePhoneCurrent: true).ToList();
                    outputs.Insert(0, new(AudioService.SystemDefaultDeviceId, L10n.T("Dev.SystemDefaultOut")));
                    return (Devices: outputs.ToArray(), Selected: host.Audio.ReadRoute(target.App, Flow.Render) ?? AudioService.SystemDefaultDeviceId);
                }, state =>
                {
                    var (devices, selected) = state;
                    if (target == null)
                    {
                        onOutputRead?.Invoke();
                        var (label, tooltip) = OutputSummary(config, devices.FirstOrDefault(d => SameDevice(d.Id, selected)), "Ov.NoDevice");
                        child.UpdateParentLabel(label, tooltip);
                    }
                    var rows = new List<UiMenuItem>(devices.Length);
                    foreach (var device in devices)
                    {
                        bool chosen = SameDevice(device.Id, selected);
                        if (!config.IsDeviceShown(EDataFlow.eRender, device.PreferenceId)) continue;
                        string id = device.Id, name = Name(config, device);
                        rows.Add(new()
                        {
                            Text = target == null ? SystemOutputLabel(name, config.DeviceNames.ContainsKey(id)) : name,
                            Tooltip = name, MaxTextWidth = target == null ? 196 : 300, Checked = chosen,
                            Click = () => RunAudio(host, target?.Name ?? title, target, () => SetOutput(host, target, id, name))
                        });
                    }
                    child.UpdateItems(rows.Count == 0 ? Placeholder("Ov.NoDevice") : rows);
                }, () =>
                {
                    child.UpdateItems(Placeholder("Tray.AudioUnavailable"));
                    if (target == null)
                    {
                        onOutputRead?.Invoke();
                        var (label, tooltip) = OutputSummary(config, null, "Tray.AudioUnavailable");
                        child.UpdateParentLabel(label, tooltip);
                    }
                });
            }
        };
    }

    private static (string Label, string Tooltip) OutputSummary(AppConfig config, Endpoint? endpoint, string fallback)
    {
        string format = L10n.T("Tray.SystemOutput");
        string name = endpoint == null ? L10n.T(fallback) : Name(config, endpoint);
        string shortName = SystemOutputLabel(name, endpoint != null && config.DeviceNames.ContainsKey(endpoint.Id), 16);
        return (string.Format(format, shortName), string.Format(format, name));
    }

    private static string SystemOutputLabel(string name, bool custom, int limit = 24)
    {
        // Keep the Sonar channel or headset model visible. Custom aliases are literal user text.
        string label = name.Trim();
        int opening = label.IndexOfAny(['(', '（']);
        if (!custom && opening > 0 && (label.EndsWith(')') || label.EndsWith('）')))
        {
            string primary = label[..opening].TrimEnd();
            string driver = label[(opening + 1)..^1].Trim();
            if (primary.Contains(" - ", StringComparison.Ordinal) || driver.StartsWith(primary, StringComparison.OrdinalIgnoreCase))
                label = primary;
        }
        int suffix = label.LastIndexOf(" - ", StringComparison.Ordinal);
        if (!custom && limit == 16 && suffix >= 0 && label.Length > limit)
        {
            string brand = label[..suffix].Split(' ', 2)[0];
            string channel = label[(suffix + 3)..].Trim();
            var brandChars = System.Globalization.StringInfo.ParseCombiningCharacters(brand);
            int remaining = limit - System.Globalization.StringInfo.ParseCombiningCharacters(channel).Length - 1;
            label = remaining >= brandChars.Length ? brand + " " + channel :
                remaining >= 3 ? brand[..brandChars[remaining - 1]] + "… " + channel : channel;
        }
        if (label.Length <= limit) return label;
        if (!custom)
        {
            int family = label.IndexOf(' '), channel = label.LastIndexOf(" - ", StringComparison.Ordinal);
            if (family > 0 && channel > family)
                label = label[..family] + " · " + label[(channel + 3)..];
        }
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(label);
        if (boundaries.Length <= limit) return label;
        int prefixLength = limit == 16 ? 6 : 10;
        int suffixLength = limit - prefixLength - 1;
        return label[..boundaries[prefixLength]].TrimEnd() + "…" + label[boundaries[^suffixLength]..].TrimStart();
    }

    private static string Name(AppConfig config, Endpoint device) =>
        config.DeviceNames.TryGetValue(device.Id, out string? name) && !string.IsNullOrWhiteSpace(name)
            ? name : string.IsNullOrWhiteSpace(device.Name) ? L10n.T("Dev.Unknown") : device.Name;

    private static bool SameDevice(string id, string? selected) => selected != null &&
        string.Equals(AudioPolicyConfig.UnpackDeviceId(id), AudioPolicyConfig.UnpackDeviceId(selected), StringComparison.OrdinalIgnoreCase);

    private static async Task<Feedback?> SetOutput(ApplicationHost host, Target? target, string id, string name)
    {
        if (target == null)
        {
            if (!host.Audio.SetDefault(Flow.Render, id) || !SameDevice(id, ProductAudio.DefaultId(Flow.Render)) ||
                !SameDevice(id, ProductAudio.DefaultId(Flow.Render, Role.Multimedia))) throw new InvalidOperationException();
            return new(name);
        }
#if SONICROUTE_PHONE
        if (target.App.IsExternal)
        {
            if (host.Audio.ExternalAudio is not { } bridge) throw new InvalidOperationException(L10n.T("Tray.AppUnavailable"));
            var external = await bridge.SetExternalOutputAsync(target.App, id).ConfigureAwait(false);
            if (external.Text.StartsWith("error:", StringComparison.Ordinal))
                throw new InvalidOperationException(external.Text);
            return new(name);
        }
#endif
        // This path publishes the existing optimistic request, readback and failure feedback.
        var reply = await host.CurrentApp.SelectOutputAsync((int)target.App.ProcessId, id).ConfigureAwait(false);
        if (reply.Text == "error: output_queue_busy") throw new InvalidOperationException(reply.Text);
        return null;
    }

    private async void RunAudio(ApplicationHost host, string title, Target? target, Func<Task<Feedback?>> operation)
    {
        // Queue in input order on the STA, then do device I/O off the message thread.
        await _audioCommands.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                if (target != null)
                {
                    bool exists = host.Audio.Apps(refresh: true).Any(app => app.Identity == target.App.Identity &&
                        string.Equals(app.ProcessName, target.App.ProcessName, StringComparison.OrdinalIgnoreCase));
                    if (!exists || (target.Start.HasValue && ProcessStart(target.App) != target.Start) ||
                        host.Audio.Volume(target.App).Percent < 0)
                    {
                        host.TryPost(() => host.PublishOsd(title, L10n.T("Tray.AppUnavailable"), success: false));
                        return;
                    }
                }
                var feedback = await operation().ConfigureAwait(false);
                if (feedback != null)
                    host.TryPost(() => host.PublishOsd(title, feedback.Text, feedback.Volume, feedback.Muted));
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ExceptionDiagnostics.Record("TrayMenu.Audio", error);
            host.TryPost(() => host.PublishOsd(title, L10n.T("Tray.AudioActionFailed"), success: false));
        }
        finally
        {
            host.NotifyAudioVisualChanged();
            _audioCommands.Release();
        }
    }

    private static async Task RunFavorite(ApplicationHost host, string id)
    {
        string title = L10n.T("Auto.Favorites"), message;
        try
        {
            var rule = await Task.Run(() => AutoRuleStore.Find(id)).ConfigureAwait(false);
            if (rule == null) message = L10n.T("Auto.MissingRule");
            else
            {
                title = rule.Name;
                var result = await AutoRuleService.ExecuteWithResultAsync(rule, "favorite").ConfigureAwait(false);
                message = AutoRuleService.DescribeResult(result);
            }
        }
        catch (Exception error)
        {
            ExceptionDiagnostics.Record("TrayMenu.Favorite", error);
            message = L10n.T("Auto.RunFailed");
        }
        host.TryPost(() => host.PublishOsd(title, message));
    }
}
