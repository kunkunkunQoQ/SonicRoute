using System.Globalization;
using System.Text.Json;
using SonicRoute.Core;
using SonicRoute.Native.Audio;

namespace SonicRoute.Native.Product;

internal sealed partial class ApplicationHost
{
    private readonly ProductAudio _audio = new();
    internal ProductCurrentApp CurrentApp { get; private set; } = null!;
    private ProductAutomation _automation = null!;
    private CoalescedVolumeWriter _volumeWriter = null!;
    private bool _resident;

    private void StartServices(bool resident)
    {
        _resident = resident;
        CurrentApp = new(_audio, this, _feedback);
        _automation = new(this, _window, _config, _feedback, _audio);
        _volumeWriter = new(TryPost, ScheduleOnce, (pid, percent) =>
        {
            var state = _audio.Volume(Flow.Render, null, pid, level: percent);
            return (state.Count > 0, state.Percent);
        });
        _audio.Start(resident);
        _automation.Start(resident);
        if (resident) { RefreshForegroundInterest(); CurrentApp.Refresh(); }
    }

    internal void RefreshForegroundInterest()
    {
        if (!_resident) return;
        var interest = _window.Interest;
        bool demand = _automation.NeedsForeground || CurrentApp.NeedsForeground;
        _window.SetInterest(demand
            ? interest | ShellInterest.Foreground : interest & ~ShellInterest.Foreground);
        CurrentApp.WatchHealth(demand, _window.Interest.HasFlag(ShellInterest.Foreground), RefreshForegroundInterest);
    }

    private void StopServices()
    {
        _resident = false;
        _automation?.Dispose();
        CurrentApp?.Dispose();
        _volumeWriter?.Dispose();
        _volumeWriter?.Completion.GetAwaiter().GetResult();
        Task.Run(_audio.Dispose).GetAwaiter().GetResult();
    }

    private void BusinessSignal(ShellSignal signal)
    {
        if (_automation == null || !_resident) return;
        if (signal.Kind == ShellSignalKind.Foreground) CurrentApp.OnForegroundChanged(signal.Window);
        if ((signal.Kind == ShellSignalKind.Power && signal.Value is 7 or 18)
            || (signal.Kind == ShellSignalKind.Session && signal.Value is 1 or 3 or 8))
        {
            RefreshForegroundInterest();
            CurrentApp.Refresh();
        }
        _automation.OnSignal(signal);
    }

    private async Task<ControlReply> BusinessCommandAsync(ControlRequest request)
    {
        switch (request.Verb)
        {
            case "current" when request.Args.Length <= 1:
                int? foreground = request.Args.Length == 0 ? null : int.Parse(request.Args[0], CultureInfo.InvariantCulture);
                var app = await Task.Run(() => CurrentApp.Resolve(foreground));
                return new(app == null ? "null" : JsonSerializer.Serialize(
                    new AppSession(app.ProcessId, app.ProcessName, app.Label, app.HasActiveSession), HostJson.Default.AppSession));
            case "route" when request.Args is [var targetPid, "render", var target]:
                return await CurrentApp.SelectOutputAsync(int.Parse(targetPid, CultureInfo.InvariantCulture),
                    target == "default" ? Core.AudioService.SystemDefaultDeviceId : target);
            case "outputs-set" when request.Args is [var outputPid, var preferred, .. var selected]:
                return await CurrentApp.SetOutputsAsync(int.Parse(outputPid, CultureInfo.InvariantCulture), preferred, selected);
            case "cycle-output" when request.Args.Length <= 1:
                int? pid = request.Args.Length == 0 ? null : int.Parse(request.Args[0], CultureInfo.InvariantCulture);
                return await await OnOwner(() => CurrentApp.CycleOutputAsync(pid, requiredPid: pid));
            case "output-state" when request.Args.Length == 0:
                var state = await OnOwner(() => CurrentApp.LastOutput);
                return new(state == null ? "null" : JsonSerializer.Serialize(state, HostJson.Default.OutputIntentState));
            case "volume-queue" when request.Args is [var process, var value]:
                int p = int.Parse(process, CultureInfo.InvariantCulture), level = int.Parse(value, CultureInfo.InvariantCulture);
                if (p < 1 || level is < 0 or > 100) return new("error: invalid_volume");
                int version = await OnOwner(() => _volumeWriter.Enqueue(p, level, (_, _, _, _) => { }));
                return new(version < 0 ? "error: volume_queue_busy" : version.ToString(CultureInfo.InvariantCulture));
            case "volume-flush" when request.Args.Length == 0:
                await await OnOwner(() => { _volumeWriter.Flush(); return _volumeWriter.Completion; });
                return new("ok");
            default:
                return await _automation.TryControlAsync(request) ?? await _audio.ExecuteAsync(request);
        }
    }

    private Task<string> ExecuteRuleAsync(string selector) => _automation.ExecuteRuleAsync(selector);
}
