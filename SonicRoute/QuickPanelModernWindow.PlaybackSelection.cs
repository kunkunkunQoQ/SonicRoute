using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

#if SONICROUTE_PHONE
using System.Windows.Controls.Primitives;
using SonicRoute.WindowsLink;
#endif

namespace SonicRoute;

public partial class QuickPanelModernWindow
{
    internal record PlaybackIntent(string[] Outputs, string? Anchor, string? PreferredPrimary = null);
    private sealed record PendingInputIntent(string DeviceId);

    private Func<int, string?, HashSet<string>> _readRowPlaybackOutputs = AppPlaybackOutputs.GetSelected;
    private Func<AudioAppInfo, string[], string?, Task<(bool Success, int HResult, string Message)>> _setRowPlaybackOutputs = AppPlaybackOutputs.SetSelectedAsync;
    private Func<AudioAppInfo, EDataFlow, string, (bool Success, int HResult, string Message)> _applyRowEndpoint = ApplicationAudio.ApplyEndpoint;

#if !SONICROUTE_PHONE
    // Full already refreshes expanded rows from its combined local/external playback subscription.
    private int _localPlaybackRefreshQueued;
    private void OnLocalPlaybackChanged()
    {
        if (_isClosed || Interlocked.Exchange(ref _localPlaybackRefreshQueued, 1) != 0) return;
        _uiLifetime.Post(Dispatcher, () =>
        {
            Interlocked.Exchange(ref _localPlaybackRefreshQueued, 0);
            if (_isClosed || !IsVisible) return;
            foreach (var row in _rows.Values)
                if (row.Expanded && row.PlaybackChange.IsCompleted) _ = BuildRowDevicesAsync(row);
        });
    }
#endif
    private Task QueuePlaybackClickAsync(AppRow row, AudioDeviceInfo device, ModifierKeys modifiers)
    {
        int pid = (int)row.App.ProcessId;
        if (!Current()) return Task.CompletedTask;
        var order = row.DeviceButtons.Where(entry => entry.Flow == EDataFlow.eRender).Select(entry => entry.Id).ToArray();
        if (!order.Contains(device.Id, StringComparer.OrdinalIgnoreCase)) return Task.CompletedTask;
        if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Shift)
            && ApplicationAudio.IsExternalOutputDevice(device.Id))
            return Task.CompletedTask;
        if (!Available(device.Id))
        {
            ShowOsd(AppDisplayName.Get(row.App), L10n.T("Ov.CurrentUnavailable"));
            return Task.CompletedTask;
        }

        var pending = row.PendingPlayback;
        var request = CreatePlaybackIntent(order,
            pending?.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? row.ConfirmedPlayback,
            device.Id, pending?.Anchor ?? row.PlaybackAnchor, modifiers, pending?.PreferredPrimary);
        var required = request.Outputs.Where(Available).ToArray();
        return QueueApply(false);

        Task QueueApply(bool speakerConsent)
        {
            row.PendingPlayback = request;
            UpdateLocalPlaybackSelection(row);
            UpdateRowName(row, row.OutputId);
            return row.PlaybackChange = ApplyClickAsync(row.PlaybackChange, speakerConsent);
        }

        bool Current() => !_isClosed && _rows.TryGetValue(pid, out var live) && ReferenceEquals(live, row);
        bool Available(string id) => _outputDisplay.Any(output => string.Equals(id, output.Id, StringComparison.OrdinalIgnoreCase));

        async Task ApplyClickAsync(Task previous, bool speakerConsent)
        {
            bool applied = false;
            try
            {
                await previous;
                if (!Current()) return;
                int beforeRead = ++row.PlaybackReadVersion;
                var before = await ReadLocalPlaybackAsync(pid, micOn: false);
                if (!Current()) return;
                ConfirmLocalPlayback(row, beforeRead, before.Output, before.Playback);
                if (required.Any(id => !Available(id)))
                {
                    ShowOsd(AppDisplayName.Get(row.App), L10n.T("Ov.CurrentUnavailable"));
                    return;
                }

                string preferred = SelectOptimisticPrimaryOutput(request.Outputs, request.PreferredPrimary ?? before.Output, preferPhysical: true);
                MarkLastUsed(row.App);
                var result = await _uiLifetime.ReadAsync(speakerConsent
                    ? AppPlaybackOutputs.SetAsync(row.App, request.Outputs, preferred, allowSpeakerMute: true)
                    : _setRowPlaybackOutputs(row.App, request.Outputs, preferred));
                applied = result.Success;
                if (!Current()) return;
                if (applied)
                {
                    row.PlaybackAnchor = request.Anchor;
                    if (!row.App.IsExternal) (System.Windows.Application.Current as App)?.NotifyAppRouteChanged(pid, caller: this);
                }
                else
                {
                    ShowOsd(AppDisplayName.Get(row.App), result.Message);
#if SONICROUTE_PHONE
                    if (!speakerConsent && result.HResult == SourceSpeakerConsentRequiredException.ErrorCode
                        && ReferenceEquals(row.PendingPlayback, request) && !before.Playback.SetEquals(request.Outputs)
                        && row.DeviceButtons.FirstOrDefault(item => item.Id == device.Id && item.Flow == EDataFlow.eRender).Button is { IsVisible: true } button)
                    {
                        var menu = ConvenienceMenus.Create();
                        var names = row.DeviceButtons.Where(b => b.Flow == EDataFlow.eRender && request.Outputs.Contains(b.Id, StringComparer.OrdinalIgnoreCase))
                            .Select(b => b.Button.Content?.ToString());
                        string target = string.Format(L10n.T("Phone.PcDropOnlyOnTargetTitle"), string.Join(" + ", names));
                        var confirm = ConvenienceMenus.Item(string.Format(L10n.T("Phone.WindowsLinkMuteSpeakerAction"), target));
                        confirm.ToolTip = L10n.T("Phone.WindowsLinkMuteSpeakerDesc");
                        var promptedChange = row.PlaybackChange;
                        confirm.Click += async (_, _) =>
                        {
                            if (Current() && ReferenceEquals(row.PlaybackChange, promptedChange)) await QueueApply(true);
                        };
                        menu.Items.Add(confirm);
                        menu.PlacementTarget = button;
                        menu.Placement = PlacementMode.Bottom;
                        menu.IsOpen = true;
                    }
#endif
                }
            }
            catch (OperationCanceledException) when (!Current()) { }
            catch (Exception ex) { if (Current()) ShowOsd(AppDisplayName.Get(row.App), ex.Message); }
            finally
            {
                if (Current() && ReferenceEquals(row.PendingPlayback, request))
                {
                    int playbackRead = ++row.PlaybackReadVersion;
                    (string Output, string Input, HashSet<string> Playback)? confirmed = null;
                    try { confirmed = await ReadLocalPlaybackAsync(pid, micOn: false); }
                    catch (OperationCanceledException) when (!Current()) { }
                    catch (Exception ex) { if (Current()) ShowOsd(AppDisplayName.Get(row.App), ex.Message); }

                    if (Current() && ReferenceEquals(row.PendingPlayback, request))
                    {
                        if (confirmed is { } actual)
                            ConfirmLocalPlayback(row, playbackRead, actual.Output, actual.Playback);
                        else if (playbackRead > row.ConfirmedPlaybackRead)
                        {
                            row.ConfirmedPlaybackRead = playbackRead;
                            if (applied)
                            {
                                row.ConfirmedPlayback = request.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
                                row.OutputId = SelectOptimisticPrimaryOutput(request.Outputs, request.PreferredPrimary ?? row.OutputId, preferPhysical: true);
                            }
                        }
                        row.PendingPlayback = null;
                        UpdateLocalPlaybackSelection(row);
                        UpdateRowName(row, row.OutputId);
                    }
                }
            }
        }
    }

    private Task<(string Output, string Input, HashSet<string> Playback)> ReadLocalPlaybackAsync(int pid, bool micOn) =>
        _uiLifetime.ReadAsync((Pid: pid, MicOn: micOn, Endpoints: _readRowEndpoints, Outputs: _readRowPlaybackOutputs), static target =>
        {
            var (output, input) = target.Endpoints(target.Pid, target.MicOn);
            return (output, input, target.Outputs(target.Pid, output));
        });

    private void ConfirmLocalPlayback(AppRow row, int read, string output, HashSet<string> playback)
    {
        // Device-list reads can finish after a newer confirmation. Only the latest snapshot wins.
        if (read <= row.ConfirmedPlaybackRead) return;
        row.ConfirmedPlaybackRead = read;
        row.ConfirmedPlayback = playback;
#if SONICROUTE_PHONE
        WindowsLinkHub.GetPcAppOutput((int)row.App.ProcessId, out row.WindowsLinkExclusive);
#endif
        UpdateRowName(row, output);
    }

    private static string? GetLocalNativeOutput(AppRow row, HashSet<string> selected)
    {
        if (row.PendingPlayback is { } pending)
        {
            string optimistic = SelectOptimisticPrimaryOutput(pending.Outputs, pending.PreferredPrimary ?? row.OutputId, preferPhysical: true);
            return !ApplicationAudio.IsExternalOutputDevice(optimistic) && selected.Contains(optimistic) ? optimistic : null;
        }
        return row.OutputId is { } output && !ApplicationAudio.IsExternalOutputDevice(output) && selected.Contains(output)
            ? output : null;
    }

    private void UpdateLocalPlaybackSelection(AppRow row)
    {
        var selected = row.PendingPlayback?.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? row.ConfirmedPlayback;
        string? native = GetLocalNativeOutput(row, selected);
        foreach (var item in row.DeviceButtons)
        {
            if (item.Flow != EDataFlow.eRender) continue;
            string style = !selected.Contains(item.Id) ? "DevButton"
                : native != null && string.Equals(item.Id, native, StringComparison.OrdinalIgnoreCase) ? "DevButtonActive"
                : "DevButtonMirror";
            SetRowDeviceStyle(item.Button, style);
        }
    }

    private void ConfirmLocalInput(AppRow row, int read, string? input)
    {
        if (read <= row.ConfirmedInputRead) return;
        row.ConfirmedInputRead = read;
        row.ConfirmedInput = string.IsNullOrWhiteSpace(input) ? AudioService.SystemDefaultInputDeviceId : input;
    }

    private void UpdateLocalInputSelection(AppRow row)
    {
        string? pendingId = row.PendingInput?.DeviceId;
        bool pendingPresent = pendingId != null && row.DeviceButtons.Any(item =>
            item.Flow == EDataFlow.eCapture && string.Equals(item.Id, pendingId, StringComparison.OrdinalIgnoreCase));
        string selected = pendingPresent ? pendingId! : row.ConfirmedInput ?? AudioService.SystemDefaultInputDeviceId;
        foreach (var item in row.DeviceButtons)
            if (item.Flow == EDataFlow.eCapture) SetRowDeviceSelection(item.Button, item.Id, selected);
    }

    private Task QueueInputClickAsync(AppRow row, AudioDeviceInfo dev)
    {
        int pid = (int)row.App.ProcessId;
        if (!Current()) return Task.CompletedTask;
        if (!row.DeviceButtons.Any(entry => entry.Flow == EDataFlow.eCapture && string.Equals(entry.Id, dev.Id, StringComparison.OrdinalIgnoreCase)))
            return Task.CompletedTask;
        if (!Available(dev.Id))
        {
            ShowOsd(AppDisplayName.Get(row.App), L10n.T("Ov.CurrentUnavailable"));
            return Task.CompletedTask;
        }

        var request = new PendingInputIntent(dev.Id);
        row.PendingInput = request;
        UpdateLocalInputSelection(row);
        var previous = row.InputChange;
        return row.InputChange = ApplyInputClickAsync();

        bool Current() => !_isClosed && _rows.TryGetValue(pid, out var live) && ReferenceEquals(live, row);
        bool Available(string id) => _inputDisplay.Any(input => string.Equals(id, input.Id, StringComparison.OrdinalIgnoreCase));

        async Task ApplyInputClickAsync()
        {
            bool applied = false;
            try
            {
                await previous;
                if (!Current()) return;
                if (!Available(request.DeviceId))
                {
                    ShowOsd(AppDisplayName.Get(row.App), L10n.T("Ov.CurrentUnavailable"));
                    return;
                }
                MarkLastUsed(row.App);
                var (ok, _, msg) = await ReadbackAfterWriteAsync((Write: _applyRowEndpoint, row.App, request.DeviceId),
                    static state => state.Write(state.App, EDataFlow.eCapture, state.DeviceId));
                applied = ok;
                if (!Current()) return;
                if (ok)
                {
                    ShowOsd(AppDisplayName.Get(row.App), "🎤 " + PanelDevices.Name(dev));
                    if (!row.App.IsExternal) (System.Windows.Application.Current as App)?.NotifyAppRouteChanged(pid, caller: this);
                }
                else
                    ShowOsd(AppDisplayName.Get(row.App), $"✗ {msg}");
            }
            catch (OperationCanceledException) when (!Current()) { }
            catch (Exception ex) { if (Current()) ShowOsd(AppDisplayName.Get(row.App), ex.Message); }
            finally
            {
                if (Current() && ReferenceEquals(row.PendingInput, request))
                {
                    int inputRead = ++row.InputReadVersion;
                    string? confirmedInput = null;
                    try
                    {
                        var actual = await ReadLocalPlaybackAsync(pid, micOn: true);
                        confirmedInput = actual.Input;
                    }
                    catch (OperationCanceledException) when (!Current()) { }
                    catch (Exception ex) { if (Current()) ShowOsd(AppDisplayName.Get(row.App), ex.Message); }

                    if (Current() && ReferenceEquals(row.PendingInput, request))
                    {
                        if (confirmedInput != null)
                            ConfirmLocalInput(row, inputRead, confirmedInput);
                        else if (inputRead > row.ConfirmedInputRead)
                        {
                            row.ConfirmedInputRead = inputRead;
                            if (applied) row.ConfirmedInput = request.DeviceId;
                        }
                        row.PendingInput = null;
                        UpdateLocalInputSelection(row);
                    }
                }
            }
        }
    }

    internal static string SelectOptimisticPrimaryOutput(IReadOnlyList<string> outputs, string? currentOutputId,
        bool preferPhysical, string? lowPriorityId = null)
    {
        if (outputs.Count == 0)
            return AudioService.SystemDefaultDeviceId;

        string? Pick(bool filtered)
        {
            string? first = null;
            string? matchedCurrent = null;
            string? systemDefault = null;
            for (int i = 0; i < outputs.Count; i++)
            {
                string id = outputs[i];
                if (filtered)
                {
                    if (preferPhysical && ApplicationAudio.IsExternalOutputDevice(id)) continue;
                    if (lowPriorityId != null && string.Equals(id, lowPriorityId, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (!preferPhysical && AudioService.IsSystemDefault(id)) return id;
                first ??= id;
                if (AudioService.IsSystemDefault(id)) systemDefault ??= id;
                if (currentOutputId != null && string.Equals(id, currentOutputId, StringComparison.OrdinalIgnoreCase))
                    matchedCurrent = id;
            }
            return matchedCurrent ?? systemDefault ?? first;
        }

        return Pick(filtered: true) ?? Pick(filtered: false) ?? AudioService.SystemDefaultDeviceId;
    }

    internal static PlaybackIntent CreatePlaybackIntent(string[] order, HashSet<string> selected, string clicked,
        string? anchor, ModifierKeys modifiers, string? priorPreferred = null)
    {
        var outputs = SelectPlaybackRange(order, selected, clicked, anchor, modifiers);
        if (outputs.Length == 0) outputs = [AudioService.SystemDefaultDeviceId];
        bool extend = modifiers.HasFlag(ModifierKeys.Shift);
        bool toggle = modifiers.HasFlag(ModifierKeys.Control);
        string nextAnchor = extend && !toggle ? anchor ?? clicked : clicked;
        string? preferredPrimary = extend && toggle ? clicked
            : !extend && !toggle ? (!ApplicationAudio.IsExternalOutputDevice(clicked) ? clicked : null)
            : priorPreferred != null && outputs.Contains(priorPreferred, StringComparer.OrdinalIgnoreCase) ? priorPreferred : null;
        return new PlaybackIntent(outputs, nextAnchor, preferredPrimary);
    }

#if SONICROUTE_PHONE
    private static async Task<(bool Success, int HResult, string Message)> ApplyPeerMenuAsync(
        AudioAppInfo app, string peer, bool destinationOnly, bool allowSpeakerMute)
    {
        if (destinationOnly) return await AppPlaybackOutputs.SetAsync(app, [peer], peer, allowSpeakerMute);
        // Retiring an exclusive route restores the previous Windows output; the dedicated, muted endpoint
        // currently visible in per-app policy must never be chosen as the local speaker.
        int pid = (int)app.ProcessId;
        WindowsLinkHub.GetPcAppOutput(pid, out bool exclusive);
        if (exclusive)
        {
            var result = await WindowsLinkHub.RoutePcAppToPeerAsync(pid, peer);
            if (!result.Success) return result;
        }
        string local = await Task.Run(() => ApplicationAudio.GetPersistedEndpoint(pid, EDataFlow.eRender))
            ?? AudioService.SystemDefaultDeviceId;
        return await AppPlaybackOutputs.SetAsync(app, [local, peer], local, allowSpeakerMute: false);
    }

#endif
    internal static string[] SelectPlaybackRange(string[] order, HashSet<string> selected, string clicked, string? anchor, ModifierKeys modifiers)
    {
        bool extend = modifiers.HasFlag(ModifierKeys.Shift);
        bool toggle = modifiers.HasFlag(ModifierKeys.Control);
        if (!extend && !toggle) return [clicked];
        var result = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        if (extend && toggle)
        {
            result.Add(clicked);
        }
        else if (extend)
        {
            anchor ??= order.FirstOrDefault(result.Contains);
            int start = Array.FindIndex(order, id => string.Equals(id, anchor, StringComparison.OrdinalIgnoreCase));
            int end = Array.FindIndex(order, id => string.Equals(id, clicked, StringComparison.OrdinalIgnoreCase));
            if (start < 0 || end < 0) result.Add(clicked);
            else for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) result.Add(order[i]);
        }
        else if (!result.Remove(clicked))
        {
            result.Add(clicked);
        }
        var visible = order.Where(result.Contains);
        // Modifier selections preserve active outputs even when a device is hidden from this panel.
        return visible.Concat(selected.Where(id => result.Contains(id) && !order.Contains(id, StringComparer.OrdinalIgnoreCase))).ToArray();
    }
}
