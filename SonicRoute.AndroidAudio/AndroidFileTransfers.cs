namespace SonicRoute.AndroidAudio;

internal enum AndroidFilePhase { Running, Saved, Cancelled, InvalidFile, Failed }
internal sealed record AndroidFileProgress(AndroidFilePhase Phase, int Index, int Count, string Text);

// Holds confirmed work independently of its views. Active records are per persistent
// device; only the latest completed result is retained for a newly opened view.
internal sealed class AndroidFileTransfers
{
    private sealed class Job(CancellationTokenSource stop, AndroidFileProgress progress)
    {
        internal readonly CancellationTokenSource Stop = stop;
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AndroidFileProgress Progress = progress;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _active = new(StringComparer.Ordinal);
    private (string Id, AndroidFileProgress State)? _recent;
    private bool _closed;
    internal event Action<string>? Changed;

    internal AndroidFileProgress? Get(string id)
    {
        lock (_gate) return _active.TryGetValue(id, out var job) ? job.Progress
            : _recent is { } previous && previous.Id == id ? previous.State : null;
    }

    internal bool TryStart(string id, AndroidFileTransfer transfer, IReadOnlyList<string> paths, string destination,
        string? pullFrom, Func<bool> currentTransport, CancellationToken device, out Task completion)
    {
        completion = Task.CompletedTask;
        if (paths.Count == 0 || device.IsCancellationRequested) return false;
        string[] batch = paths.ToArray();
        var status = new AndroidFileProgress(AndroidFilePhase.Running, 1, batch.Length, Path.GetFileName(batch[0]));
        Job job;
        lock (_gate)
        {
            if (_closed || _active.ContainsKey(id) || device.IsCancellationRequested) return false;
            job = new(CancellationTokenSource.CreateLinkedTokenSource(device), status);
            _active.Add(id, job);
            completion = job.Done.Task;
        }
        Notify(id);
        _ = Task.Run(() => TransferAsync(job, id, transfer, batch, destination, pullFrom, currentTransport));
        return true;
    }

    private async Task TransferAsync(Job job, string id, AndroidFileTransfer transfer, string[] paths,
        string destination, string? pullFrom, Func<bool> currentTransport)
    {
        AndroidFileProgress state = job.Progress;
        try
        {
            CancellationToken token = job.Stop.Token;
            token.ThrowIfCancellationRequested();
            if (pullFrom == null && paths.Any(p => !Path.IsPathFullyQualified(p) || !File.Exists(p)))
                state = state with { Phase = AndroidFilePhase.InvalidFile };
            else
            {
                string saved = destination;
                for (int i = 0; i < paths.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!currentTransport()) throw new OperationCanceledException("Device transport changed");
                    state = new(AndroidFilePhase.Running, i + 1, paths.Length, Path.GetFileName(paths[i]));
                    lock (_gate) job.Progress = state;
                    Notify(id);
                    saved = pullFrom == null
                        ? await transfer.SendAsync(paths[i], destination, token).ConfigureAwait(false)
                        : await transfer.ReceiveAsync(pullFrom, paths[i], destination, token).ConfigureAwait(false);
                }
                state = state with { Phase = AndroidFilePhase.Saved, Text = paths.Length == 1 ? saved : destination };
            }
        }
        catch (OperationCanceledException) { state = state with { Phase = AndroidFilePhase.Cancelled }; }
        catch (Exception ex) { state = state with { Phase = AndroidFilePhase.Failed, Text = ex.Message }; }
        lock (_gate)
        {
            _active.Remove(id);
            _recent = (id, state);
        }
        job.Stop.Dispose();
        job.Done.TrySetResult();
        Notify(id);
    }

    internal void Cancel(string id)
    {
        Job? job;
        lock (_gate) _active.TryGetValue(id, out job);
        if (job != null) TryCancel(job);
    }

    private static void TryCancel(Job job)
    {
        try { job.Stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal Task CloseAsync()
    {
        Job[] jobs;
        lock (_gate) { _closed = true; jobs = _active.Values.ToArray(); }
        foreach (var job in jobs) TryCancel(job);
        return Task.WhenAll(jobs.Select(job => job.Done.Task));
    }

    private void Notify(string id)
    {
        try { Changed?.Invoke(id); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("File transfer observer: " + ex.Message); }
    }
}

