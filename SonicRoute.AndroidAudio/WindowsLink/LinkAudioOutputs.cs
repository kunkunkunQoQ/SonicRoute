using SonicRoute.AndroidAudio;

namespace SonicRoute.WindowsLink;

internal sealed partial class LinkAudioPlayback
{
    private readonly PcmOutputMirrors _mirrors;
    private readonly List<string?[]> _previousOutputs = [];

    internal string?[] GetOutputs()
    {
        lock (_gate) return [_endpoint, .. _mirrors.IdsSnapshot()];
    }

    internal Task SetOutputsAsync(IReadOnlyList<string?> outputs) => SetOutputsAsync(outputs, remember: true);

    internal Task SetOutputsAsync(IReadOnlyList<string?> outputs, bool remember)
    {
        var desired = outputs.ToArray();
        return Task.Run(async () =>
        {
            await _routes.WaitAsync(_lifetime).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var previous = GetOutputs();
                var (primary, extras) = PcmOutputMirrors.Select(desired, _endpoint);
                if (string.Equals(primary, _endpoint, StringComparison.OrdinalIgnoreCase) &&
                    extras.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_mirrors.IdsSnapshot())) return;
                await _mirrors.ApplyAsync(extras, _createPlayer, () => ChangePrimaryLockedAsync(primary), _lifetime).ConfigureAwait(false);
                if (remember) RememberOutputs(previous);
            }
            finally { _routes.Release(); }
            _changed();
        });
    }

    internal void RememberOutputs(string?[] previous)
    {
        lock (_gate)
        {
            var ids = previous.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _previousOutputs.RemoveAll(ids.SetEquals);
            _previousOutputs.Insert(0, previous);
            if (_previousOutputs.Count > 8) _previousOutputs.RemoveAt(8);
        }
    }

    private async Task<bool> RestoreOutputsAsync(string?[] candidates, HashSet<string?> unavailable)
    {
        var valid = candidates.Where(id => !unavailable.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var next in valid)
        {
            try { await ChangePrimaryLockedAsync(next, restorePrevious: false).ConfigureAwait(false); }
            catch (Exception) when (!_lifetime.IsCancellationRequested) { unavailable.Add(next); continue; }
            var extras = new List<string>();
            foreach (var other in valid)
            {
                if (other == null || string.Equals(other, next, StringComparison.OrdinalIgnoreCase) || unavailable.Contains(other)) continue;
                extras.Add(other);
                try { await _mirrors.ApplyAsync(extras.ToArray(), _createPlayer, () => Task.CompletedTask, _lifetime).ConfigureAwait(false); }
                catch (Exception) when (!_lifetime.IsCancellationRequested) { extras.RemoveAt(extras.Count - 1); unavailable.Add(other); }
            }
            await _mirrors.ApplyAsync(extras.ToArray(), _createPlayer, () => Task.CompletedTask, _lifetime).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private async Task RemoveMirrorsAsync(Predicate<IPcmPlayer> match)
    {
        await _routes.WaitAsync().ConfigureAwait(false);
        bool changed;
        try { changed = _mirrors.Remove(match); }
        finally { _routes.Release(); }
        if (changed) _changed();
    }
}
