using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SonicRoute.Core
{
    /// <summary>按进程名存在性生成快照；少量指定目标复用存活句柄，缺失时统一扫描。</summary>
    public sealed class ProcessSnapshotService : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string? _targetName;
        private Process? _knownProcess;
        private string[]? _targets;
        private readonly Dictionary<string, Process> _knownTargets = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ProcessSnapshotService(string? targetName = null) { _targetName = targetName; }

        public static ProcessSnapshotService ForTargets(IEnumerable<string> targets)
        {
            var names = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length == 1) return new ProcessSnapshotService(names[0]);
            // 大规则集使用原有单次全量枚举，限制常驻句柄数量。
            return new ProcessSnapshotService { _targets = names.Length <= 32 ? names : null };
        }

        public HashSet<string> Capture()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ProcessSnapshotService));
                if (_targets != null) return CaptureTargets();
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

        private HashSet<string> CaptureTargets()
        {
            bool allLive = _knownTargets.Count == _targets!.Length;
            foreach (var process in _knownTargets.Values)
                try { if (process.HasExited) allLive = false; } catch { allLive = false; }
            if (allLive) return new HashSet<string>(_targets, StringComparer.OrdinalIgnoreCase);
            foreach (var process in _knownTargets.Values) process.Dispose();
            _knownTargets.Clear();
            var targets = new HashSet<string>(_targets, StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 任一目标缺失或旧实例退出时统一扫描一次；同名其它实例仍在则不误报退出。
            foreach (var process in Process.GetProcesses())
            {
                bool retained = false;
                try
                {
                    string name = process.ProcessName;
                    if (targets.Contains(name))
                    {
                        names.Add(name);
                        if (!_knownTargets.ContainsKey(name) && !process.HasExited)
                        { _knownTargets.Add(name, process); retained = true; }
                    }
                }
                catch { }
                finally { if (!retained) process.Dispose(); }
            }
            return names;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _knownProcess?.Dispose();
                _knownProcess = null;
                foreach (var process in _knownTargets.Values) process.Dispose();
                _knownTargets.Clear();
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
