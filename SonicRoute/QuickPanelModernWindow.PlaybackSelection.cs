using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Core.Models;

namespace SonicRoute;

public partial class QuickPanelModernWindow
{
    private record PlaybackIntent(string[] Outputs, string? Anchor);

    private Func<int, string?, HashSet<string>> _readRowPlaybackOutputs = AppPlaybackOutputs.GetSelected;
    private Func<AudioAppInfo, string[], string?, Task<(bool Success, int HResult, string Message)>> _setRowPlaybackOutputs = AppPlaybackOutputs.SetAsync;

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
        if (!Available(device.Id))
        {
            ShowOsd(AppDisplayName.Get(row.App), L10n.T("Ov.CurrentUnavailable"));
            return Task.CompletedTask;
        }

        var pending = row.PendingPlayback;
        var request = CreatePlaybackIntent(order,
            pending?.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? row.ConfirmedPlayback,
            device.Id, pending?.Anchor ?? row.PlaybackAnchor, modifiers);
        var required = request.Outputs.Where(Available).ToArray();
        row.PendingPlayback = request;
        UpdateLocalPlaybackSelection(row);
        var previous = row.PlaybackChange;
        return row.PlaybackChange = ApplyClickAsync();

        bool Current() => !_isClosed && _rows.TryGetValue(pid, out var live) && ReferenceEquals(live, row);
        bool Available(string id) => _outputDisplay.Any(output => string.Equals(id, output.Id, StringComparison.OrdinalIgnoreCase));

        async Task ApplyClickAsync()
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

                string preferred = request.Outputs.Contains(before.Output, StringComparer.OrdinalIgnoreCase) ? before.Output : request.Outputs[0];
                MarkLastUsed(row.App);
                var result = await _setRowPlaybackOutputs(row.App, request.Outputs, preferred);
                applied = result.Success;
                if (!Current()) return;
                if (applied) row.PlaybackAnchor = request.Anchor;
                else ShowOsd(AppDisplayName.Get(row.App), result.Message);
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
                            if (applied) row.ConfirmedPlayback = request.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
                        }
                        row.PendingPlayback = null;
                        UpdateLocalPlaybackSelection(row);
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
        UpdateRowName(row, output);
    }

    private void UpdateLocalPlaybackSelection(AppRow row)
    {
        var selected = row.PendingPlayback?.Outputs.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? row.ConfirmedPlayback;
        foreach (var item in row.DeviceButtons)
            if (item.Flow == EDataFlow.eRender) SetRowDeviceSelection(item.Button, item.Id, selected.Contains(item.Id) ? item.Id : "");
    }

    private static PlaybackIntent CreatePlaybackIntent(string[] order, HashSet<string> selected, string clicked, string? anchor, ModifierKeys modifiers)
    {
        var outputs = SelectPlaybackRange(order, selected, clicked, anchor, modifiers);
        if (outputs.Length == 0) outputs = [AudioService.SystemDefaultDeviceId];
        if (!modifiers.HasFlag(ModifierKeys.Shift) || anchor == null) anchor = clicked;
        return new PlaybackIntent(outputs, anchor);
    }

    internal static string[] SelectPlaybackRange(string[] order, HashSet<string> selected, string clicked, string? anchor, ModifierKeys modifiers)
    {
        bool extend = modifiers.HasFlag(ModifierKeys.Shift);
        bool toggle = modifiers.HasFlag(ModifierKeys.Control);
        if (!extend && !toggle) return [clicked];
        var result = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        if (extend)
        {
            anchor ??= order.FirstOrDefault(selected.Contains);
            int start = Array.FindIndex(order, id => string.Equals(id, anchor, StringComparison.OrdinalIgnoreCase));
            int end = Array.FindIndex(order, id => string.Equals(id, clicked, StringComparison.OrdinalIgnoreCase));
            if (!toggle) result.Clear();
            if (start < 0 || end < 0) result.Add(clicked);
            else for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) result.Add(order[i]);
        }
        else if (!result.Remove(clicked)) result.Add(clicked);
        var visible = order.Where(result.Contains);
        // Ctrl preserves active outputs even when a device is hidden from this panel.
        return toggle ? visible.Concat(selected.Where(id => result.Contains(id) && !order.Contains(id, StringComparer.OrdinalIgnoreCase))).ToArray()
            : visible.ToArray();
    }
}
