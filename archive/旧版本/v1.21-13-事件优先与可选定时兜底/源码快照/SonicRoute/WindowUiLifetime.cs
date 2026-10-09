using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SonicRoute
{
    // Only window-owned reads and rendering belong here. Committed writes use their own lifetime.
    internal sealed class WindowUiLifetime : IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationToken _token;
        private readonly object _gate = new();
        private readonly List<DispatcherOperation> _operations = new();
        private bool _closed;

        internal WindowUiLifetime() { _token = _source.Token; }
        internal CancellationToken Token => _token;

        internal Task<T> ReadAsync<T>(Func<T> read) => WaitForReadAsync(Task.Run(read, _token), _token);

        internal Task<TResult> ReadAsync<TState, TResult>(TState state, Func<TState, TResult> read) =>
            ReadAsync(() => read(state));

        private static async Task<T> WaitForReadAsync<T>(Task<T> work, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(work, cancelled.Task).ConfigureAwait(false) != work)
                {
                    // A running COM call cannot be interrupted. Observe failures without retaining its window.
                    _ = work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    token.ThrowIfCancellationRequested();
                }
                token.ThrowIfCancellationRequested();
                return await work.ConfigureAwait(false);
            }
        }

        internal DispatcherOperation Post(Dispatcher dispatcher, Action action,
            DispatcherPriority priority = DispatcherPriority.Background)
        {
            lock (_gate)
            {
                if (_closed) return dispatcher.InvokeAsync(static () => { }, priority, _token);
                _operations.RemoveAll(op => op.Status != DispatcherOperationStatus.Pending);
                var operation = dispatcher.InvokeAsync(action, priority, _token);
                _operations.Add(operation);
                return operation;
            }
        }

        public void Dispose()
        {
            DispatcherOperation[] pending;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                pending = _operations.ToArray();
                _operations.Clear();
            }
            _source.Cancel();
            foreach (var operation in pending) operation.Abort();
            _source.Dispose();
        }
    }
}
