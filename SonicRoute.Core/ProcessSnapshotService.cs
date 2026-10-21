using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SonicRoute.Core
{
    /// <summary>按进程名存在性生成快照；少量指定目标复用存活句柄，缺失时统一扫描。</summary>
    public sealed class ProcessSnapshotService : IDisposable
    {
        /// <summary>Complete=false 表示整轮不可作为缺失证据；Uncertain 只影响指定目标。</summary>
        public sealed class Snapshot
        {
            public HashSet<string> Present { get; }
            public HashSet<string> Uncertain { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Absent { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool Complete { get; internal set; } = true;
            public Snapshot(IEnumerable<string>? present = null, bool complete = true)
            { Present = new HashSet<string>(present ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase); Complete = complete; }
        }
        private readonly object _gate = new object();
        private readonly string? _targetName;
        private Process? _knownProcess;
        private string[]? _targets;
        private string[]? _queryTargets;
        private bool _includeAll;
        private readonly Dictionary<string, Process> _knownTargets = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public ProcessSnapshotService(string? targetName = null) { _targetName = targetName; }

        public static ProcessSnapshotService ForTargets(IEnumerable<string> targets, bool includeAll = false)
        {
            var names = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (includeAll) return new ProcessSnapshotService { _queryTargets = names, _includeAll = true };
            if (names.Length == 1) return new ProcessSnapshotService(names[0]);
            // 大规则集使用原有单次全量枚举，限制常驻句柄数量。
            return new ProcessSnapshotService { _targets = names.Length <= 32 ? names : null, _queryTargets = names };
        }

        public HashSet<string> Capture()
        {
            var snapshot = CaptureState();
            if (!snapshot.Complete || snapshot.Uncertain.Count != 0) throw new InvalidOperationException("Process snapshot is incomplete.");
            return snapshot.Present;
        }

        public Snapshot CaptureState()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ProcessSnapshotService));
                if (_targets != null) return CaptureTargets();
                if (_targetName == null) return CaptureAll(_queryTargets, _includeAll);
                var result = new Snapshot();
                var names = result.Present;
                if (_knownProcess != null)
                {
                    try
                    {
                        // 规则按同名进程存在性触发：一个已知实例仍存活时，无需重扫所有进程。
                        if (!_knownProcess.HasExited) { names.Add(_targetName); return result; }
                    }
                    catch { /* 无权限或句柄失效时重新查询，不误判退出 */ }
                    _knownProcess.Dispose();
                    _knownProcess = null;
                }
                Process[] matches;
                try { matches = Process.GetProcessesByName(_targetName); }
                catch { result.Uncertain.Add(_targetName); return result; }
                try
                {
                    if (matches.Length > 0) names.Add(_targetName);
                    foreach (var process in matches)
                    {
                        try
                        {
                            if (TryRetain(process, _targetName)) { _knownProcess = process; break; }
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
                return result;
            }
        }

        private Snapshot CaptureTargets()
        {
            bool allLive = _knownTargets.Count == _targets!.Length;
            foreach (var process in _knownTargets.Values)
                try { if (process.HasExited) allLive = false; } catch { allLive = false; }
            if (allLive) return new Snapshot(_targets);
            foreach (var process in _knownTargets.Values) process.Dispose();
            _knownTargets.Clear();
            var targets = new HashSet<string>(_targets, StringComparer.OrdinalIgnoreCase);
            var result = new Snapshot();
            var names = result.Present;
            bool unreadable = false;
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { result.Complete = false; return result; }
            // 任一目标缺失或旧实例退出时统一扫描一次；同名其它实例仍在则不误报退出。
            foreach (var process in processes)
            {
                bool retained = false;
                try
                {
                    string name = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(name)) { unreadable = true; continue; }
                    if (targets.Contains(name))
                    {
                        names.Add(name);
                        if (!_knownTargets.ContainsKey(name) && TryRetain(process, name))
                        { _knownTargets.Add(name, process); retained = true; }
                    }
                }
                catch { unreadable = true; }
                finally { if (!retained) process.Dispose(); }
            }
            // 个别名称读不到时，仅对尚未确认的目标补一次按名查询；故障不能推导为退出。
            // 正常路径仍只有原有一次共用扫描，不增加每目标周期扫描。
            if (unreadable) ResolveUnreadableTargets(result, targets);
            return result;
        }

        private static void ResolveUnreadableTargets(Snapshot result, IEnumerable<string> targets)
        {
            foreach (string target in targets.Where(name => !result.Present.Contains(name)))
            {
                Process[] matches;
                try { matches = Process.GetProcessesByName(target); }
                catch { result.Uncertain.Add(target); continue; }
                try { if (matches.Length > 0) result.Present.Add(target); else result.Absent.Add(target); }
                finally { foreach (var process in matches) process.Dispose(); }
            }
        }

        private static bool TryRetain(Process process, string expectedName)
        {
            try
            {
                // 仅缓存Process对象不足以固定进程身份；先固定句柄，再刷新名称并前后确认存活，防止PID复用。
                _ = process.Handle;
                process.Refresh();
                return !process.HasExited && string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase) && !process.HasExited;
            }
            catch { return false; } // 无句柄权限仍使用本次按名枚举的可靠存在结果，不提权或错误缓存身份。
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
            using var service = new ProcessSnapshotService(targetName);
            return service.Capture();
        }

        private static Snapshot CaptureAll(string[]? targets, bool includeAll)
        {
            var result = new Snapshot();
            // 多目标只做一次全量查询，避免每个名字各自扫描全部进程。
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { result.Complete = false; return result; }
            foreach (var process in processes)
            {
                try
                {
                    string name = process.ProcessName;
                    if (!string.IsNullOrWhiteSpace(name)) result.Present.Add(name);
                    else result.Complete = false;
                }
                catch
                {
                    // 已确认退出的条目无需保留；仍存活但名称不可读使缺失判断不可靠。
                    try { if (!process.HasExited) result.Complete = false; }
                    catch { result.Complete = false; }
                }
                finally { process.Dispose(); }
            }
            if (!result.Complete && targets != null)
            {
                ResolveUnreadableTargets(result, targets);
                if (!includeAll) result.Complete = true; // 指定目标已分别确认，任意应用规则仍保留全局不确定性。
            }
            return result;
        }
    }
}
