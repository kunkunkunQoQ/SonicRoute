using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SonicRoute
{
    public partial class App
    {
        private int _trayAudioRequest;

        private bool TrayMenuAlive(ContextMenu owner, int request) => request == _trayAudioRequest
            && ReferenceEquals(owner, _trayMenu) && owner.IsOpen && !Dispatcher.HasShutdownStarted;

        private static void TrayPlaceholder(MenuItem parent, string key)
        {
            parent.Items.Clear();
            var item = ConvenienceMenus.Item(L10n.T(key));
            item.IsEnabled = false;
            parent.Items.Add(item);
        }

        private static void TrayNamedHeader(MenuItem item, string text)
        {
            if (item.Header is TextBlock header) header.Text = text;
            else item.Header = new TextBlock { Text = text, MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis };
            item.ToolTip = text;
        }

        private static void BeginTrayDeviceRead(MenuItem parent)
        {
            if (!parent.Items.OfType<MenuItem>().Any(item => item.Tag is string))
                TrayPlaceholder(parent, "Tray.Loading");
            else
                foreach (MenuItem item in parent.Items) item.IsEnabled = false;
        }

        private static void UpdateTrayDeviceItems(MenuItem parent, IEnumerable<AudioDeviceInfo> devices,
            AppConfig config, string? selected, Action<string, string> apply)
        {
            // Reuse controls only within this menu lifetime; every expansion still reads current devices/state.
            var existing = parent.Items.OfType<MenuItem>().Where(item => item.Tag is string)
                .ToDictionary(item => (string)item.Tag, StringComparer.OrdinalIgnoreCase);
            var items = new List<MenuItem>();
            foreach (var device in devices)
            {
                string id = device.Id;
                if (!existing.TryGetValue(id, out var item))
                {
                    item = ConvenienceMenus.Item("");
                    item.Tag = id;
                    item.IsCheckable = true;
                    item.StaysOpenOnClick = true;
                    var entry = item;
                    item.Click += (_, _) => apply(id, ((TextBlock)entry.Header).Text);
                }
                TrayNamedHeader(item, DeviceDisplayName(config, device));
                item.IsEnabled = true;
                item.IsChecked = SameTrayDevice(id, selected);
                items.Add(item);
            }
            if (items.Count == 0) { TrayPlaceholder(parent, "Ov.NoDevice"); return; }
            if (parent.Items.Count == items.Count && !items.Where((item, index) => !ReferenceEquals(parent.Items[index], item)).Any())
                return;
            parent.Items.Clear();
            foreach (var item in items) parent.Items.Add(item);
        }

        private void AddTrayAudioItems(ContextMenu owner, int foregroundPid)
        {
            var lifetime = _trayMenuLifetime!;
            int request = _trayAudioRequest;
            var current = ConvenienceMenus.Item(L10n.T("Ov.CurrentApp"));
            current.Tag = "tray.current";
            TrayPlaceholder(current, "Tray.Loading");
            owner.Items.Add(current);
            owner.Opened += async (_, _) => await LoadTrayAppAsync(owner, current, foregroundPid, request, lifetime);

            var output = ConvenienceMenus.Item(L10n.T("Dev.SystemDefaultOut"));
            output.Tag = "tray.output";
            TrayPlaceholder(output, "Tray.Loading");
            owner.Items.Add(output);
            int outputRead = 0;
            output.SubmenuOpened += async (_, e) =>
            {
                if (e.OriginalSource != output) return;
                int read = ++outputRead;
                BeginTrayDeviceRead(output);
                try
                {
                    var state = await lifetime.ReadAsync(() => (Devices: AudioService.GetDevices(EDataFlow.eRender),
                        Default: AudioService.GetDefaultDeviceId(EDataFlow.eRender)));
                    if (!TrayMenuAlive(owner, request) || read != outputRead || !output.IsSubmenuOpen) return;
                    var config = ConfigService.Load();
                    var active = state.Devices.FirstOrDefault(d => SameTrayDevice(d.Id, state.Default));
                    TrayNamedHeader(output, active == null ? L10n.T("Dev.SystemDefaultOut")
                        : string.Format(L10n.T("Tray.SystemOutput"), DeviceDisplayName(config, active)));
                    UpdateTrayDeviceItems(output, state.Devices.Where(d => !config.HiddenOutputDevices.Contains(d.Id)), config, state.Default,
                        (id, name) => _ = RunTrayAudioActionAsync(owner, L10n.T("Dev.SystemDefaultOut"), async () =>
                        {
                            var result = await Task.Run(() => SystemDefaultDeviceService.SetDefault(EDataFlow.eRender, id));
                            if (!result.Success) throw new InvalidOperationException();
                            AudioService.InvalidateDeviceCache(EDataFlow.eRender);
                            ShowOsd(L10n.T("Dev.SystemDefaultOut"), name);
                        }));
                }
                catch { if (TrayMenuAlive(owner, request) && read == outputRead) TrayPlaceholder(output, "Tray.AudioUnavailable"); }
            };

            var mic = ConvenienceMenus.Item(L10n.T("Tray.MuteAllMicrophones"));
            mic.Tag = "tray.mic";
            mic.IsEnabled = false;
            mic.IsCheckable = true;
            mic.StaysOpenOnClick = true;
            owner.Items.Add(mic);
            GlobalMicMuteService.MuteState micState = default;
            owner.Opened += async (_, _) =>
            {
                try
                {
                    var state = await lifetime.ReadAsync(GlobalMicMuteService.ReadState);
                    if (!TrayMenuAlive(owner, request)) return;
                    micState = state;
                    mic.IsEnabled = state.Known;
                    mic.IsChecked = state.AllMuted;
                    mic.ToolTip = !state.Known ? L10n.T(state.Count == 0 && state.UnavailableCount == 0 ? "Ov.NoMicDevice" : "Tray.AudioUnavailable")
                        : state.MutedCount > 0 && !state.AllMuted ? L10n.T("Tray.MicrophonesMixed") : null;
                }
                catch (OperationCanceledException) { }
            };
            mic.Click += async (_, _) => await RunTrayAudioActionAsync(owner, L10n.T("Ov.MuteMic"), async () =>
            {
                var result = await Task.Run(() => GlobalMicMuteService.SetMuteChecked(!micState.AllMuted));
                if (!result.Applied) throw new InvalidOperationException();
                _quickPanel?.ApplyMicMuteReadback(result.State.AllMuted);
                ShowMicMuteOsd(L10n.T("Ov.MuteMic"), result.State.AllMuted);
            });
        }

        private sealed class TrayAppTarget
        {
            internal AudioAppInfo App = null!;
            internal string Name = "";
            internal long? StartTicks;
            internal int Pid => (int)App.ProcessId;
        }

        private static long? TrayProcessStart(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return process.StartTime.Ticks; }
            catch { return null; }
        }

        private static bool TrayTargetAvailable(TrayAppTarget target)
        {
            var app = AudioService.GetApps(refresh: true).FirstOrDefault(a => a.ProcessId == target.App.ProcessId
                && string.Equals(a.ProcessName, target.App.ProcessName, StringComparison.OrdinalIgnoreCase));
            return app != null && (!target.StartTicks.HasValue || TrayProcessStart(target.Pid) == target.StartTicks)
                && SessionVolumeService.HasSession(target.Pid);
        }

        private async Task LoadTrayAppAsync(ContextMenu owner, MenuItem parent, int foregroundPid, int request, WindowUiLifetime lifetime)
        {
            try
            {
                if (!TrayMenuAlive(owner, request)) return;
                var apps = await lifetime.ReadAsync(AudioService.GetAppsAsync());
                if (!TrayMenuAlive(owner, request)) return;
                var data = await lifetime.ReadAsync(() =>
                {
                    var app = CurrentAppService.Resolve(apps, ConfigService.Load(), foregroundPid);
                    if (app == null) return (Target: (TrayAppTarget?)null, Volume: -1, Muted: false);
                    var target = new TrayAppTarget { App = app, Name = AppDisplayName.Get(app), StartTicks = TrayProcessStart((int)app.ProcessId) };
                    return (Target: (TrayAppTarget?)target, Volume: SessionVolumeService.GetVolumePercent(target.Pid), Muted: SessionVolumeService.IsMuted(target.Pid));
                });
                if (!TrayMenuAlive(owner, request)) return;
                if (data.Target == null || data.Volume < 0)
                {
                    parent.Items.Clear();
                    parent.IsEnabled = false;
                    parent.ToolTip = L10n.T("Ov.NoOutputSession");
                    return;
                }
                var target = data.Target;
                TrayNamedHeader(parent, string.Format(L10n.T("Qp.CurrentApp"), target.Name));
                parent.Items.Clear();
                var mute = ConvenienceMenus.Item(L10n.T("Act.Mute"));
                mute.IsCheckable = true;
                mute.StaysOpenOnClick = true;
                mute.IsChecked = data.Muted;
                mute.Click += async (_, _) => await RunTrayAppActionAsync(owner, target, async () =>
                {
                    var result = await Task.Run(() =>
                    {
                        bool ok = SessionVolumeService.SetMute(target.Pid, !data.Muted);
                        return (Applied: ok && SessionVolumeService.IsMuted(target.Pid) == !data.Muted,
                            Volume: SessionVolumeService.GetVolumePercent(target.Pid));
                    });
                    if (!result.Applied) throw new InvalidOperationException();
                    if (result.Volume >= 0)
                        _quickPanel?.ApplyAppVolumeReadback(target.Pid, result.Volume, !data.Muted);
                    ShowOsd(target.Name, L10n.T(!data.Muted ? "Ov.AppMuted" : "Ov.AppUnmuted"));
                });
                parent.Items.Add(mute);
                var presets = ConvenienceMenus.Item(L10n.T("Tray.VolumePresets"));
                TrayPlaceholder(presets, "Tray.Loading");
                bool presetsCreated = false;
                presets.SubmenuOpened += (_, e) =>
                {
                    if (e.OriginalSource != presets || presetsCreated || !TrayMenuAlive(owner, request)) return;
                    presetsCreated = true;
                    presets.Items.Clear();
                    VolumePresets.Populate(presets.Items, percent => _ = RunTrayAppActionAsync(owner, target, async () =>
                    {
                        var result = await Task.Run(() =>
                        {
                            bool ok = SessionVolumeService.SetVolumePercent(target.Pid, percent);
                            return (Applied: ok, Actual: SessionVolumeService.GetVolumePercent(target.Pid), Muted: SessionVolumeService.IsMuted(target.Pid));
                        });
                        if (!result.Applied || result.Actual < 0) throw new InvalidOperationException();
                        _quickPanel?.ApplyAppVolumeReadback(target.Pid, result.Actual, result.Muted);
                        ShowVolumeOsd(target.Name, result.Actual, string.Format(L10n.T("Qp.VolOk"), result.Actual), result.Muted, "app:" + target.Pid);
                    }), staysOpenOnClick: true);
                    VolumePresets.Update(presets.Items, data.Volume, true);
                };
                parent.Items.Add(presets);
                AddTrayAppDevices(owner, parent, target, request, lifetime);
            }
            catch { if (TrayMenuAlive(owner, request)) TrayPlaceholder(parent, "Tray.AudioUnavailable"); }
        }

        private void AddTrayAppDevices(ContextMenu owner, MenuItem parent, TrayAppTarget target, int request, WindowUiLifetime lifetime)
        {
            var output = ConvenienceMenus.Item(L10n.T("Qp.Output"));
            TrayPlaceholder(output, "Tray.Loading");
            parent.Items.Add(output);
            int version = 0;
            output.SubmenuOpened += async (_, e) =>
            {
                if (e.OriginalSource != output) return;
                int read = ++version;
                BeginTrayDeviceRead(output);
                try
                {
                    var state = await lifetime.ReadAsync(() => (Devices: AudioService.GetDevices(EDataFlow.eRender),
                        Persisted: AudioService.GetPersistedEndpoint(target.Pid, EDataFlow.eRender)));
                    if (!TrayMenuAlive(owner, request) || read != version || !output.IsSubmenuOpen) return;
                    var config = ConfigService.Load();
                    string selected = state.Persisted == null ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(state.Persisted);
                    var devices = PanelDevices.WithSystemDefault(state.Devices.Where(d => !config.HiddenOutputDevices.Contains(d.Id)), EDataFlow.eRender, config);
                    UpdateTrayDeviceItems(output, devices, config, selected,
                        (id, name) => _ = RunTrayAppActionAsync(owner, target, async () =>
                        {
                            var result = await Task.Run(() => AudioService.ApplyEndpoint(target.Pid, EDataFlow.eRender, id));
                            if (!result.Success) throw new InvalidOperationException();
                            _quickPanel?.RefreshAppRouteReadback(target.Pid);
                            ShowOsd(target.Name, name);
                        }));
                }
                catch { if (TrayMenuAlive(owner, request) && read == version) TrayPlaceholder(output, "Tray.AudioUnavailable"); }
            };
        }

        private Task RunTrayAppActionAsync(ContextMenu owner, TrayAppTarget target, Func<Task> action)
            => RunTrayAudioActionAsync(owner, target.Name, async () =>
            {
                if (!await Task.Run(() => TrayTargetAvailable(target)))
                { ShowOsd(target.Name, L10n.T("Tray.AppUnavailable")); return; }
                await action();
            });

        private async Task RunTrayAudioActionAsync(ContextMenu owner, string title, Func<Task> action)
        {
            if (!TrayMenuAlive(owner, _trayAudioRequest)) return;
            // The clicked action owns its work; closing the menu only invalidates pending UI reads.
            owner.IsOpen = false;
            await _volumeHotkeyGate.WaitAsync();
            try { await action(); }
            catch { if (!Dispatcher.HasShutdownStarted) ShowOsd(title, L10n.T("Tray.AudioActionFailed")); }
            finally { _volumeHotkeyGate.Release(); }
        }

        private static bool SameTrayDevice(string? left, string? right) => left != null && right != null
            && string.Equals(AudioPolicyConfig.UnpackDeviceId(left), AudioPolicyConfig.UnpackDeviceId(right), StringComparison.OrdinalIgnoreCase);

        private void AddTrayWheelPause(ContextMenu owner)
        {
            var pause = ConvenienceMenus.Item(L10n.T("Tray.PauseWheel"));
            pause.Tag = "tray.pause";
            pause.IsCheckable = true;
            pause.StaysOpenOnClick = true;
            pause.IsChecked = ConfigService.Load().TrayWheelPaused;
            pause.ToolTip = L10n.T("Tray.PauseWheelHint");
            pause.Click += (_, _) =>
            {
                if (!TrayMenuAlive(owner, _trayAudioRequest)) return;
                owner.IsOpen = false;
                var config = ConfigService.Load();
                bool previous = config.TrayWheelPaused;
                config.TrayWheelPaused = !previous;
                if (!ConfigService.TrySave(config))
                {
                    config.TrayWheelPaused = previous;
                    ShowOsd(L10n.T("Tray.PauseWheel"), L10n.T("Tray.SaveFailed"));
                    return;
                }
                _trayWheel?.DiscardPendingWheel();
            };
            owner.Items.Add(pause);
        }
    }
}
