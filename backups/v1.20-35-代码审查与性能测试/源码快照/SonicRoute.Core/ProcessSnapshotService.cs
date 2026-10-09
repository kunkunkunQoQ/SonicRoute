using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SonicRoute.Core
{
    /// <summary>按进程名存在性生成快照；单个指定目标只创建匹配的 Process 对象。</summary>
    public sealed class ProcessSnapshotService : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string? _targetName;
        private Process? _knownProcess;
        private bool _disposed;

        public ProcessSnapshotService(string? targetName = null) { _targetName = targetName; }

        public HashSet<string> Capture()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ProcessSnapshotService));
                if (_targetName == null) return CaptureNames();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_knownProcess != null)
                {
                    try
                    {
                        // 规则按同名进程存在性触发：一个已知实例仍存活时，无需重扫所有进程。
                        if (!_knownProcess.HasExited) { names.Add(_targetName); return names; }
                    }
                    catch { /* 无权限或句柄失效时重新查询，不误判退出 */ }
                    _knownProcess.Dispose();
                    _knownProcess = null;
                }
                var matches = Process.GetProcessesByName(_targetName);
                try
                {
                    if (matches.Length > 0) names.Add(_targetName);
                    foreach (var process in matches)
                    {
                        try
                        {
                            if (!process.HasExited) { _knownProcess = process; break; }
                        }
                        catch { }
                    }
                }
                finally
                {
                    foreach (var process in matches)
                        if (!ReferenceEquals(process, _knownProcess)) process.Dispose();
                }
                // 最后一个已知实例退出后重新枚举同名实例，才判断应用是否真正退出。
                return names;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _knownProcess?.Dispose();
                _knownProcess = null;
            }
        }

        public static HashSet<string> CaptureNames(string? targetName = null)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (targetName != null)
            {
                var matches = Process.GetProcessesByName(targetName);
                try { if (matches.Length > 0) names.Add(targetName); }
                finally { foreach (var process in matches) process.Dispose(); }
                return names;
            }

            // 多目标只做一次全量查询，避免每个名字各自扫描全部进程。
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    string name = process.ProcessName;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                catch { /* 已退出或无权限的进程跳过 */ }
                finally { process.Dispose(); }
            }
            return names;
        }
    }
}
