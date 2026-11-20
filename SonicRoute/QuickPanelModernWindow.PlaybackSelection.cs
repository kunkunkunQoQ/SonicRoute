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
                var result = await AppPlaybackOutputs.SetAsync(row.App, next, preferred,
                    allowSpeakerMute: !next.Any(ApplicationAudio.IsWindowsLinkOutputDevice));
                if (_isClosed) return;
                if (result.Success) SetAnchor();
                else
                {
                    ShowOsd(AppDisplayName.Get(row.App), result.Message);
#if SONICROUTE_PHONE
                    if (result.HResult == SourceSpeakerConsentRequiredException.ErrorCode && !selected.SetEquals(next)
                        && row.DeviceButtons.FirstOrDefault(item => item.Id == device.Id && item.Flow == EDataFlow.eRender).Button is { IsVisible: true } button)
                    {
                        var menu = ConvenienceMenus.Create();
                        var names = row.DeviceButtons.Where(b => b.Flow == EDataFlow.eRender && next.Contains(b.Id, StringComparer.OrdinalIgnoreCase))
                            .Select(b => b.Button.Content?.ToString());
                        string target = string.Format(L10n.T("Phone.PcDropOnlyOnTargetTitle"), string.Join(" + ", names));
                        var confirm = ConvenienceMenus.Item(string.Format(L10n.T("Phone.WindowsLinkMuteSpeakerAction"), target));
                        confirm.ToolTip = L10n.T("Phone.WindowsLinkMuteSpeakerDesc");
                        confirm.Click += async (_, _) =>
                        {
                            if (_isClosed || !_rows.TryGetValue(pid, out var live) || live != row) return;
                            var retry = await AppPlaybackOutputs.SetAsync(row.App, next, preferred, allowSpeakerMute: true);
                            if (_isClosed) return;
                            if (retry.Success) SetAnchor();
                            else ShowOsd(AppDisplayName.Get(row.App), retry.Message);
                            if (row.Expanded) await BuildRowDevicesAsync(row);
                        };
                        menu.Items.Add(confirm);
                        menu.PlacementTarget = button;
                        menu.Placement = PlacementMode.Bottom;
                        menu.IsOpen = true;
                    }
#endif
                }
                if (row.Expanded) await BuildRowDevicesAsync(row);

                void SetAnchor()
                {
                    if (!modifiers.HasFlag(ModifierKeys.Shift) || row.PlaybackAnchor == null)
                        row.PlaybackAnchor = device.Id;
                }

            }
            catch (OperationCanceledException) when (_isClosed) { }
            catch (Exception ex) { if (!_isClosed) ShowOsd(AppDisplayName.Get(row.App), ex.Message); }
        }
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
