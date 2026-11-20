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
        var previous = row.PlaybackChange;
        return row.PlaybackChange = ApplyClickAsync();

        async Task ApplyClickAsync()
        {
            await previous;
            if (_isClosed || !_rows.TryGetValue((int)row.App.ProcessId, out var current) || current != row) return;
            try
            {
                int pid = (int)row.App.ProcessId;
                var primary = await _uiLifetime.ReadAsync((Pid: pid, Read: _readRowEndpoints), static p => p.Read(p.Pid, false).Output);
                if (_isClosed) return;
                var selected = AppPlaybackOutputs.GetSelected(pid, primary);
                var order = row.DeviceButtons.Where(x => x.Flow == EDataFlow.eRender).Select(x => x.Id).ToArray();
                var next = SelectPlaybackRange(order, selected, device.Id, row.PlaybackAnchor, modifiers);
                if (next.Length == 0) next = [AudioService.SystemDefaultDeviceId];
                string preferred = next.Contains(primary, StringComparer.OrdinalIgnoreCase) ? primary : next[0];

                MarkLastUsed(row.App);
                var result = await AppPlaybackOutputs.SetAsync(row.App, next, preferred);
                if (_isClosed) return;
                if (result.Success)
                {
                    if (!modifiers.HasFlag(ModifierKeys.Shift) || row.PlaybackAnchor == null)
                        row.PlaybackAnchor = device.Id;
                }
                else ShowOsd(AppDisplayName.Get(row.App), result.Message);
                if (row.Expanded) await BuildRowDevicesAsync(row);
            }
            catch (OperationCanceledException) when (_isClosed) { }
            catch (Exception ex) { if (!_isClosed) ShowOsd(AppDisplayName.Get(row.App), ex.Message); }
        }
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
