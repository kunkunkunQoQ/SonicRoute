using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SonicRoute.Core
{
    public sealed class ExecutionHistoryStep
    {
        public int Number { get; set; }
        public int Action { get; set; }
        public string TargetApp { get; set; } = "";
        public string Status { get; set; } = "";
        public string Reason { get; set; } = "";
        public long ElapsedMs { get; set; }
    }

    public sealed class ExecutionHistoryEntry
    {
        public string RunId { get; set; } = "";
        public string ParentRunId { get; set; } = "";
        public string RuleId { get; set; } = "";
        public string RuleName { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public long ElapsedMs { get; set; }
        public string Status { get; set; } = "";
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int TotalSteps { get; set; }
        public List<ExecutionHistoryStep> Steps { get; set; } = new();
    }

    /// <summary>Opt-in storage. No timer, watcher, permanent worker or startup history read.</summary>
    public sealed class ExecutionHistoryStore : IDisposable
    {
        private const int MaxRecordBytes = 128 * 1024;
        private readonly string _directory;
        private readonly SemaphoreSlim _io;
        private volatile bool _disposed;
        private long _limitBytes;
        private int _pending;
        private readonly object _pendingGate = new();
        private TaskCompletionSource<bool>? _idle;
        public string? LastError { get; private set; }

        public ExecutionHistoryStore(string directory, int limitMb, SemaphoreSlim? io = null)
        {
            _directory = Path.GetFullPath(directory);
            _io = io ?? new SemaphoreSlim(1, 1);
            _limitBytes = (long)Math.Max(1, Math.Min(64, limitMb)) * 1024 * 1024;
        }

        private string Current => Path.Combine(_directory, "recent.jsonl");
        private string Previous => Path.Combine(_directory, "previous.jsonl");

        public async Task AppendAsync(ExecutionHistoryEntry entry)
        {
            lock (_pendingGate)
            {
                if (_disposed) return;
                if (_pending >= 32) { LastError = "queue_full"; return; }
                if (_pending++ == 0) _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try
            {
                await _io.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed) return;
                    await Task.Run(() =>
                    {
                        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
                        if (bytes.Length > MaxRecordBytes) { LastError = "record_too_large"; return; }
                        Directory.CreateDirectory(_directory);
                        Trim();
                        long segment = Interlocked.Read(ref _limitBytes) / 2;
                        long existing = File.Exists(Current) ? new FileInfo(Current).Length : 0;
                        if (existing + bytes.Length + 2 > segment)
                        {
                            if (File.Exists(Previous)) File.Delete(Previous);
                            if (File.Exists(Current)) File.Move(Current, Previous);
                        }
                        using (var stream = new FileStream(Current, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                        {
                            // A crash may leave a partial JSON line. Keep it separate from the next record.
                            if (stream.Length > 0)
                            {
                                stream.Seek(-1, SeekOrigin.End);
                                int tail = stream.ReadByte();
                                if (tail != '\n') stream.WriteByte((byte)'\n');
                            }
                            stream.Seek(0, SeekOrigin.End);
                            stream.Write(bytes, 0, bytes.Length);
                            stream.WriteByte((byte)'\n');
                        }
                        LastError = null;
                    }).ConfigureAwait(false);
                }
                finally { _io.Release(); }
            }
            catch (Exception error) { LastError = error.GetType().Name; }
            finally
            {
                lock (_pendingGate) if (--_pending == 0) _idle?.TrySetResult(true);
            }
        }

        public Task WhenIdleAsync() { lock (_pendingGate) return _idle?.Task ?? Task.CompletedTask; }

        public async Task SetLimitAsync(int limitMb)
        {
            Interlocked.Exchange(ref _limitBytes, (long)Math.Max(1, Math.Min(64, limitMb)) * 1024 * 1024);
            await _io.WaitAsync().ConfigureAwait(false);
            try { if (!_disposed) await Task.Run(Trim).ConfigureAwait(false); }
            catch (Exception error) { LastError = error.GetType().Name; }
            finally { _io.Release(); }
        }

        private void Trim()
        {
            long segment = Interlocked.Read(ref _limitBytes) / 2;
            // On a capacity reduction discard the older segment before compacting the newer one.
            if (File.Exists(Current) && new FileInfo(Current).Length > segment)
            {
                if (File.Exists(Previous)) File.Delete(Previous);
                Compact(Current, segment);
            }
            Compact(Previous, segment);
        }

        private static void Compact(string file, long maximum)
        {
            if (!File.Exists(file) || new FileInfo(file).Length <= maximum) return;
            string temporary = file + ".trim";
            try
            {
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.Position = input.Length - maximum;
                    // Start at a complete record, retaining the newest lines without parsing history.
                    while (input.Position < input.Length && input.ReadByte() != '\n') { }
                    input.CopyTo(output, 8192);
                }
                File.Replace(temporary, file, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public async Task<List<ExecutionHistoryEntry>> ReadRecentAsync(CancellationToken cancellationToken = default)
        {
            await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) return new List<ExecutionHistoryEntry>();
                return await Task.Run(() =>
                {
                    var entries = new List<ExecutionHistoryEntry>(200);
                    foreach (string file in new[] { Current, Previous })
                    {
                        foreach (string line in ReadLinesBackwards(file, cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (_disposed) return entries;
                            if (line.Length > MaxRecordBytes) { LastError = "record_too_large"; continue; }
                            try
                            {
                                var entry = JsonSerializer.Deserialize<ExecutionHistoryEntry>(line);
                                if (entry == null || entry.Steps == null || entry.Steps.Count > 100
                                    || entry.Steps.Exists(step => step == null || step.TargetApp == null || step.Status == null || step.Reason == null)) continue;
                                entries.Add(entry);
                                if (entries.Count == 200) return entries;
                            }
                            catch (JsonException) { LastError = "invalid_record"; }
                        }
                    }
                    return entries;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { LastError = error.GetType().Name; return new List<ExecutionHistoryEntry>(); }
            finally { _io.Release(); }
        }

        // Read only the tail needed by the viewer. Byte boundaries preserve UTF8 across blocks.
        private IEnumerable<string> ReadLinesBackwards(string file, CancellationToken token)
        {
            if (!File.Exists(file)) yield break;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64L * 1024 * 1024) { LastError = "file_too_large"; yield break; }
            var block = new byte[8192];
            var line = new byte[1024];
            int count = 0;
            bool tooLong = false, atEnd = true;
            long position = stream.Length;
            while (position > 0)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed) yield break;
                int size = (int)Math.Min(block.Length, position);
                position -= size;
                stream.Position = position;
                int read = 0;
                while (read < size)
                {
                    int n = stream.Read(block, read, size - read);
                    if (n == 0) throw new EndOfStreamException();
                    read += n;
                }
                for (int i = size - 1; i >= 0; i--)
                {
                    if (block[i] == '\n')
                    {
                        if (tooLong) LastError = "record_too_large";
                        else if (count != 0 || !atEnd)
                        {
                            Array.Reverse(line, 0, count);
                            yield return Encoding.UTF8.GetString(line, 0, count);
                        }
                        count = 0; tooLong = false; atEnd = false;
                    }
                    else if (!tooLong)
                    {
                        // The original reader limited UTF16 characters, so allow multibyte UTF8.
                        if (count == MaxRecordBytes * 4) { tooLong = true; count = 0; continue; }
                        if (count == line.Length) Array.Resize(ref line, Math.Min(line.Length * 2, MaxRecordBytes * 4));
                        line[count++] = block[i];
                    }
                }
            }
            if (tooLong) LastError = "record_too_large";
            else if (count != 0 || !atEnd)
            {
                Array.Reverse(line, 0, count);
                yield return Encoding.UTF8.GetString(line, 0, count).TrimStart('\uFEFF');
            }
        }

        public async Task<bool> ClearAsync()
        {
            await _io.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return false;
                await Task.Run(() =>
                {
                    if (File.Exists(Current)) File.Delete(Current);
                    if (File.Exists(Previous)) File.Delete(Previous);
                }).ConfigureAwait(false);
                LastError = null;
                return true;
            }
            catch (Exception error) { LastError = error.GetType().Name; return false; }
            finally { _io.Release(); }
        }

        // Do not block a UI toggle waiting for storage. A write already underway may finish.
        public void Dispose() => _disposed = true;
    }
}
