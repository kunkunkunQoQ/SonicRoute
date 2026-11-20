namespace SonicRoute.WindowsLink;

// Process loopback always supplies the same 48 kHz stereo PCM. Peers and render mirrors
// subscribe to one native reader and keep their own bounded playback/transport queues.
internal static class SharedProcessCapture
{
    private sealed class Subscriber(Action<ReadOnlyMemory<byte>> output)
    {
        internal readonly TaskCompletionSource Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _detached;

        internal void Write(ReadOnlyMemory<byte> pcm)
        {
            lock (this)
            {
                if (_detached) return;
                try { output(pcm); }
                catch (Exception ex) { _detached = true; Failed.TrySetException(ex); }
            }
        }

        internal void Detach()
        {
            lock (this) _detached = true;
            Failed.TrySetResult();
        }
    }

    private sealed class Capture
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Work;
        internal Subscriber[] Outputs = [];

        internal void Write(ReadOnlyMemory<byte> pcm)
        {
            foreach (var output in Volatile.Read(ref Outputs)) output.Write(pcm);
        }
    }

    private static readonly Dictionary<(int?, long), Capture> Active = [];

    internal static async Task SubscribeAsync(int? pid, Action<ReadOnlyMemory<byte>> output, Action started, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = (pid, pid.HasValue ? LinkWindowCapture.ReadProcess(pid.Value).StartTicks : 0L);
        var subscriber = new Subscriber(output);
        Capture capture;
        Task work;
        lock (Active)
        {
            if (!Active.TryGetValue(key, out capture!) || capture.Work?.IsCompleted == true)
                Active[key] = capture = new Capture();
            Volatile.Write(ref capture.Outputs, [.. capture.Outputs, subscriber]);
            work = capture.Work ??= Task.Run(() => LinkAudioCapture.CaptureNativeAsync(pid, capture.Write,
                () => capture.Ready.TrySetResult(), capture.Stop.Token));
        }
        try
        {
            await Task.WhenAny(work, capture.Ready.Task).WaitAsync(token).ConfigureAwait(false);
            if (work.IsCompleted) { await work.ConfigureAwait(false); return; }
            started();
            var finished = await Task.WhenAny(work, subscriber.Failed.Task).WaitAsync(token).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            subscriber.Detach();
            bool last;
            lock (Active)
            {
                Volatile.Write(ref capture.Outputs, capture.Outputs.Where(s => s != subscriber).ToArray());
                last = capture.Outputs.Length == 0;
                if (last && Active.TryGetValue(key, out var current) && current == capture) Active.Remove(key);
            }
            if (last)
            {
                try { capture.Stop.Cancel(); await work.ConfigureAwait(false); }
                catch (OperationCanceledException) when (capture.Stop.IsCancellationRequested) { }
                finally { capture.Stop.Dispose(); }
            }
        }
    }
}
