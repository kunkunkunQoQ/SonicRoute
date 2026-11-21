using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;
#if SONICROUTE_PHONE
using SonicRoute.AndroidAudio;
#endif

namespace SonicRoute;

public partial class App
{
    private sealed record HotkeyOutputIntent(DeviceSwitchToken Notice, AudioDeviceInfo Target);

    // Only outstanding render requests retain a snapshot. The next independent shortcut
    // resolves the app and reads its route again, including changes made outside SonicRoute.
    private sealed class HotkeyOutputScope(AudioAppInfo app)
    {
        internal readonly AudioAppInfo App = app;
        internal List<AudioDeviceInfo> Devices = new();
        internal HotkeyOutputIntent? Pending;
        internal Task Apply = Task.CompletedTask;
    }

    private readonly Dictionary<string, HotkeyOutputScope> _hotkeyOutputs = new(StringComparer.Ordinal);
    private Task _hotkeyOutputPlanning = Task.CompletedTask;
    private Func<int, IntPtr, AudioAppInfo?> _resolveHotkeyOutputApp = (pid, hwnd) =>
        CurrentAppService.ResolveWithWindow(ApplicationAudio.GetApps(), ConfigService.Load(), pid, hwnd);
    private Func<List<AudioDeviceInfo>> _readHotkeyOutputDevices = ApplicationAudio.GetOutputDevices;
    private Func<AudioAppInfo, (string? Id, bool Confirmable)> _readHotkeyOutput = ReadHotkeyOutput;
    private Func<AudioAppInfo, EDataFlow, string, (bool Success, int HResult, string Message)> _applyHotkeyOutput = ApplicationAudio.ApplyEndpoint;
    private Func<AudioAppInfo, bool> _hotkeyOutputAppAvailable = HotkeyOutputAppAvailable;

    private Task QueueHotkeyOutputAsync(int? foregroundPid)
    {
        int pid;
        IntPtr hwnd = IntPtr.Zero;
        if (foregroundPid is int captured) pid = captured;
#if SONICROUTE_PHONE
        else (pid, hwnd) = ApplicationAudio.GetForegroundWindowContext();
#else
        else pid = ForegroundAppService.GetForegroundProcessId();
#endif
        var notice = new DeviceSwitchToken();
        _trayWheel?.BeginDeviceSwitch(notice);
        // A direct foreground PID with outstanding intent has an in-memory cycle reference.
        // App overrides and virtual display windows still go through the normal resolver.
        if (_hotkeyOutputPlanning.IsCompleted && pid > 0
            && _hotkeyOutputs.TryGetValue("windows:" + (uint)pid, out var scope)
            && scope.Pending is { } pending)
        {
            var config = ConfigService.Load();
            if (config.DefaultAppMode is not ("last" or "fixed")
                && !config.DisabledAutoSwitchApps.Contains(scope.App.ProcessName ?? ""))
            {
                var devices = BuildVisibleDevices(EDataFlow.eRender, config, scope.Devices);
                if (devices.Count > 0)
                    return QueueResolvedHotkeyOutput(scope, notice, devices, pending.Target.Id, config, AppDisplayName.Get(scope.App));
            }
        }

        _trayWheel?.ShowDeviceSwitchPending(notice, L10n.T("Ov.VolumeTitle"), L10n.T("Ov.SwitchingOutput"));
        var planning = PlanHotkeyOutputAsync(_hotkeyOutputPlanning, notice, pid, hwnd);
        _hotkeyOutputPlanning = planning;
        return planning.Unwrap();
    }

    private async Task<Task> PlanHotkeyOutputAsync(Task previous, DeviceSwitchToken notice, int pid, IntPtr hwnd)
    {
        try
        {
            // Keep intent calculation in input order even if app discovery is slow. The
            // planning tail finishes after enqueueing, independent of the route write.
            await previous;
            var app = await Task.Run(() => _resolveHotkeyOutputApp(pid, hwnd));
            if (app == null)
            {
                notice.MarkBackendCompleted();
                _trayWheel?.ShowDeviceSwitchResult(notice, L10n.T("Ov.VolumeTitle"), L10n.T("Ov.NoOutputSession"), success: false);
                return Task.CompletedTask;
            }

            var catalog = await Task.Run(() =>
                (Config: ConfigService.Load(), Devices: _readHotkeyOutputDevices(), Title: AppDisplayName.Get(app)));
            var visible = BuildVisibleDevices(EDataFlow.eRender, catalog.Config, catalog.Devices);
            if (visible.Count == 0)
            {
                notice.MarkBackendCompleted();
                _trayWheel?.ShowDeviceSwitchResult(notice, OsdService.FormatDeviceFinalTitle(catalog.Title), L10n.T("Ov.NoDevice"), success: false);
                return Task.CompletedTask;
            }

            string? current;
            if (_hotkeyOutputs.TryGetValue(app.Identity, out var scope) && scope.Pending is { } pending)
                current = pending.Target.Id;
            else
            {
                current = (await Task.Run(() => _readHotkeyOutput(app))).Id;
                scope = new HotkeyOutputScope(app);
                _hotkeyOutputs.Add(app.Identity, scope);
            }
            scope.Devices = catalog.Devices;
            return QueueResolvedHotkeyOutput(scope, notice, visible, current, catalog.Config, catalog.Title);
        }
        catch
        {
            notice.MarkBackendCompleted();
            _trayWheel?.ShowDeviceSwitchResult(notice, OsdService.FormatDeviceFailedTitle(null), L10n.T("Ov.CurrentUnavailable"), success: false);
            return Task.CompletedTask;
        }
    }

    private Task QueueResolvedHotkeyOutput(HotkeyOutputScope scope, DeviceSwitchToken notice, List<AudioDeviceInfo> visible,
        string? current, AppConfig config, string title)
    {
        var target = SelectCycleDevice(visible, current, previous: false);
        var request = new HotkeyOutputIntent(notice, target);
        scope.Pending = request;
        _trayWheel?.ShowDeviceSwitchPending(notice, OsdService.FormatDeviceSwitchingTitle(title), "🔊 " + DeviceDisplayName(config, target));
        return scope.Apply = ApplyHotkeyOutputAsync(scope, scope.Apply, request, title);
    }

    private async Task ApplyHotkeyOutputAsync(HotkeyOutputScope scope, Task previous, HotkeyOutputIntent request, string title)
    {
        var app = scope.App;
        try
        {
            await previous;
            var result = await Task.Run(() =>
            {
                try { return ApplyHotkeyOutput(app, request.Target.Id); }
                finally { request.Notice.MarkBackendCompleted(); }
            });
            if (!ReferenceEquals(scope.Pending, request)) return;

            if (!app.IsExternal && _quickPanel is { IsVisible: true } panel)
                panel.RefreshAppRouteReadback((int)app.ProcessId);
            string resultTitle = result.Success ? OsdService.FormatDeviceFinalTitle(title) : OsdService.FormatDeviceFailedTitle(title);
            string resultText = result.Actual == null ? L10n.T("Ov.CurrentUnavailable") : "🔊 " + result.Actual;
            _trayWheel?.ShowDeviceSwitchResult(request.Notice, resultTitle, resultText, result.Success);
        }
        finally
        {
            if (ReferenceEquals(scope.Pending, request))
            {
                // Already pressed shortcuts may still be resolving their app. Let them
                // consume this intent even when the write fails before discovery ends.
                await _hotkeyOutputPlanning;
                if (ReferenceEquals(scope.Pending, request))
                {
                    scope.Pending = null;
                    _hotkeyOutputs.Remove(app.Identity);
                }
            }
        }
    }

    private (bool Success, string? Actual) ApplyHotkeyOutput(AudioAppInfo app, string target)
    {
        const EDataFlow flow = EDataFlow.eRender;
        bool applied = false;
        try
        {
            if (_hotkeyOutputAppAvailable(app)
                && BuildVisibleDevices(flow, ConfigService.Load(), _readHotkeyOutputDevices()).Any(d => SameOutput(d.Id, target)))
                applied = _applyHotkeyOutput(app, flow, target).Success;
        }
        catch { /* A failed or partial write still needs an authoritative route read. */ }

        try
        {
            if (!_hotkeyOutputAppAvailable(app)) return (false, null);
            var config = ConfigService.Load();
            var readback = _readHotkeyOutput(app);
            if (!readback.Confirmable) return (false, null);
            string actual = OutputId(readback.Id);
            string expected = ApplicationAudio.GetExpectedOutputId(app, target);
            var devices = _readHotkeyOutputDevices();
            var device = devices.FirstOrDefault(d => SameOutput(d.Id, actual));
#if SONICROUTE_PHONE
            // The source phone's "current output" shortcut is stored as its native alias.
            device ??= actual == AndroidAudioSession.SourcePhoneOutputId
                ? devices.FirstOrDefault(d => SameOutput(ApplicationAudio.GetExpectedOutputId(app, d.Id), actual))
                : null;
#endif
            string actualName = device != null ? DeviceDisplayName(config, device)
                : config.DeviceNames.TryGetValue(actual, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom
                : AudioService.IsSystemDefault(actual) ? L10n.T("Dev.SystemDefaultOut")
#if SONICROUTE_PHONE
                : actual == AndroidAudioSession.SourcePhoneOutputId ? L10n.T("Phone.NativeAudioOutput")
#endif
                : L10n.T("Dev.Unknown");
            return (applied && SameOutput(actual, expected), actualName);
        }
        catch { return (false, null); }
    }

    private static string OutputId(string? id) => id == null || AudioService.IsSystemDefault(id)
        ? AudioService.SystemDefaultDeviceId : AudioPolicyConfig.UnpackDeviceId(id);

    private static bool SameOutput(string first, string second) =>
        string.Equals(OutputId(first), OutputId(second), StringComparison.OrdinalIgnoreCase);

    private static (string? Id, bool Confirmable) ReadHotkeyOutput(AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        if (app.IsExternal)
        {
            var state = ApplicationAudio.ExternalSession(app)?.State;
            // A session can record a preferred output before playback is ready.
            return (state?.OutputId, state?.State == CaptureState.Receiving);
        }
#endif
        return (ApplicationAudio.GetPersistedEndpoint(app, EDataFlow.eRender), true);
    }

    private static bool HotkeyOutputAppAvailable(AudioAppInfo app)
    {
#if SONICROUTE_PHONE
        if (app.IsExternal) return ApplicationAudio.ExternalSession(app) != null;
#endif
        try
        {
            using var process = Process.GetProcessById((int)app.ProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
