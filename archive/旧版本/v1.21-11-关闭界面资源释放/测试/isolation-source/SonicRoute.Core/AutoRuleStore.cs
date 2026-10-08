using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SonicRoute.Core.Models;

namespace SonicRoute.Core
{
    /// <summary>
    /// 自动化规则独立存储（v1.17 起）：不再存于 config.json，改为
    /// <c>%LocalAppData%\SonicRoute\Automation\</c> 目录下每条规则一个 JSON 文件。
    /// - 文件名为规则名称（自动清洗 Windows 非法文件名字符）；同名规则追加 <c>(1)</c>、<c>(2)</c>… 后缀。
    /// - 列表顺序 = 文件名排序（即按规则名称排序，稳定）。
    /// - 旧版本以 <c>{Id}.json</c> 命名的文件仍可正常加载；规则下次保存时自动改为名称命名并清理旧文件。
    /// - 文件索引与修订号缓存，保存只处理目标规则；监听外部修改后按需重新加载。
    /// - 旧版本 config.json 中的 AutoRules 字段在首次加载时自动迁移到该目录（同样按名称命名）。
    /// </summary>
    public static class AutoRuleStore
    {
        public static string RulesDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "Automation"); private static string OriginalRulesDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonicRoute",
            "Automation");

        private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

        private static List<AutoRule>? _cache;
        private static readonly object _lock = new();
        private sealed class Entry
        {
            internal AutoRule? Rule;
            internal long Length, Written;
        }
        private static readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _byId = new(StringComparer.Ordinal);
        private static FileSystemWatcher? _watcher;
        private static long _revision;
        private static long _directoryWritten;

        /// <summary>相同修订号不复制列表，供 UI 跳过未变化的规则数据。</summary>
        public static (long Revision, List<AutoRule>? Rules) ReadSnapshot(long knownRevision = -1)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return (_revision, knownRevision == _revision ? null : _cache!.Select(r => r.Clone()).ToList());
            }
        }

        private static void EnsureLoaded()
        {
            bool exists = Directory.Exists(RulesDir);
            long directoryWritten = exists ? Directory.GetLastWriteTimeUtc(RulesDir).Ticks : 0;
            // 目录删掉再创建会使旧监听句柄失效；目录元数据同时提供事件丢失的兜底。
            if (_watcher != null && directoryWritten != _directoryWritten)
            {
                StopWatcher();
                _cache = null;
            }
            if (_watcher == null && exists)
            {
                try
                {
                    var watcher = new FileSystemWatcher(RulesDir, "*.json")
                    { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    watcher.Changed += FileChanged;
                    watcher.Created += FileChanged;
                    watcher.Deleted += FileChanged;
                    watcher.Renamed += (_, e) => { CheckExternalChange(e.OldFullPath); CheckExternalChange(e.FullPath); };
                    watcher.Error += (_, _) =>
                    {
                        lock (_lock)
                        {
                            if (ReferenceEquals(_watcher, watcher)) { StopWatcher(); _cache = null; }
                        }
                    };
                    watcher.EnableRaisingEvents = true;
                    _watcher = watcher;
                    _cache = null;
                }
                catch { /* 目录不可监听时仍支持显式 Invalidate。 */ }
            }
            if (_cache != null) return;
            _files.Clear();
            if (Directory.Exists(RulesDir))
            {
                // 枚举失败不建立一个可被 Save 使用的空索引，避免覆盖未读到的规则。
                foreach (string file in Directory.GetFiles(RulesDir, "*.json"))
                {
                    var entry = new Entry();
                    try
                    {
                        var info = new FileInfo(file);
                        entry.Length = info.Length; entry.Written = info.LastWriteTimeUtc.Ticks;
                        entry.Rule = JsonSerializer.Deserialize<AutoRule>(File.ReadAllText(file));
                    }
                    catch { /* 损坏文件仍占用名称。 */ }
                    _files[file] = entry;
                }
            }
            RebuildCache();
        }

        private static void StopWatcher()
        {
            var watcher = _watcher;
            _watcher = null;
            watcher?.Dispose();
        }

        private static void FileChanged(object sender, FileSystemEventArgs e) => CheckExternalChange(e.FullPath);

        private static void CheckExternalChange(string path)
        {
            lock (_lock)
            {
                if (_cache == null) return;
                try
                {
                    var info = new FileInfo(path);
                    bool known = _files.TryGetValue(path, out var entry);
                    // 自身原子保存的延后通知不会重复丢弃已更新的索引。
                    if (!info.Exists && !known) return;
                    if (info.Exists && known && entry!.Rule != null && entry.Length == info.Length && entry.Written == info.LastWriteTimeUtc.Ticks) return;
                }
                catch { }
                _cache = null;
            }
        }

        private static void RebuildCache()
        {
            _byId.Clear();
            // 改名后旧文件删除失败时，按最新文件去重；较新时间相同则按路径稳定选择。
            foreach (var pair in _files.OrderBy(p => p.Value.Written).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                if (pair.Value.Rule is AutoRule rule && !string.IsNullOrWhiteSpace(rule.Id)) _byId[rule.Id] = pair.Key;
            _cache = _byId.Values.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => _files[path].Rule!).ToList();
            _directoryWritten = Directory.Exists(RulesDir) ? Directory.GetLastWriteTimeUtc(RulesDir).Ticks : 0;
            ++_revision;
        }

        /// <summary>加载全部规则（带缓存；返回副本，外部修改不影响缓存）。</summary>
        public static List<AutoRule> LoadAll()
        {
            lock (_lock)
            {
                try { EnsureLoaded(); }
                catch { return new List<AutoRule>(); }
                return _cache!.Select(r => r.Clone()).ToList();
            }
        }

        /// <summary>按 Id 查规则。</summary>
        public static AutoRule? Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (_lock)
            {
                try { EnsureLoaded(); } catch { return null; }
                return _byId.TryGetValue(id, out var path) ? _files[path].Rule?.Clone() : null;
            }
        }

        /// <summary>原子保存一条规则：以名称命名，重名追加 (1)(2)…，成功后更新索引与修订号。</summary>
        public static void Save(AutoRule rule, string? defaultNamePrefix = null)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Id)) return;
            lock (_lock)
            {
                Directory.CreateDirectory(RulesDir);
                EnsureLoaded();
                var oldPaths = _files.Where(p => p.Value.Rule?.Id == rule.Id).Select(p => p.Key).ToList();
                if (string.IsNullOrWhiteSpace(rule.Name) && defaultNamePrefix != null)
                {
                    string prefix = SanitizeFileName(defaultNamePrefix.Trim());
                    var names = new HashSet<string>(_cache!.Where(r => r.Id != rule.Id).Select(r => (r.Name ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
                    for (int index = 1; ; index++)
                    {
                        string candidate = prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string path = Path.Combine(RulesDir, candidate + ".json");
                        if (names.Contains(candidate) || (File.Exists(path) && !oldPaths.Contains(path, StringComparer.OrdinalIgnoreCase))) continue;
                        rule.Name = candidate;
                        break;
                    }
                }
                string baseName = SanitizeFileName(rule.Name);
                string target = Path.Combine(RulesDir, baseName + ".json");
                int suffix = 1;
                while ((_files.ContainsKey(target) || File.Exists(target)) && !oldPaths.Contains(target, StringComparer.OrdinalIgnoreCase))
                    target = Path.Combine(RulesDir, $"{baseName}({suffix++}).json");
                AtomicFile.WriteAllText(target, JsonSerializer.Serialize(rule, SaveOptions));
                // 新文件完整提交后才清理旧名称；写入失败不会走到这里。
                foreach (string old in oldPaths)
                {
                    if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(old); _files.Remove(old); } catch { }
                }
                var info = new FileInfo(target);
                _files[target] = new Entry { Rule = rule.Clone(), Length = info.Length, Written = info.LastWriteTimeUtc.Ticks };
                RebuildCache();
            }
        }

        /// <summary>删除一条规则：按 Id 定位文件删除并失效缓存。</summary>
        public static void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            lock (_lock)
            {
                try
                {
                    EnsureLoaded();
                    foreach (string file in _files.Where(p => p.Value.Rule?.Id == id).Select(p => p.Key).ToList())
                    {
                        File.Delete(file);
                        _files.Remove(file);
                    }
                    RebuildCache();
                }
                finally { _cache = null; }
            }
        }

        /// <summary>失效缓存（导入 / 外部修改后调用）。</summary>
        public static void Invalidate()
        {
            lock (_lock) { _cache = null; }
        }

        /// <summary>清洗规则名称中的 Windows 非法文件名字符；空结果回退 "rule"。</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "rule";
            var invalid = Path.GetInvalidFileNameChars();
            var s = new string(name.Where(c => !invalid.Contains(c)).ToArray()).TrimEnd('.', ' ');
            return string.IsNullOrWhiteSpace(s) ? "rule" : s;
        }

        /// <summary>
        /// 旧版迁移（v1.17 起，由 ConfigService.Load 首次调用）：
        /// 将旧 config.json 中的 AutoRules 字段迁移到独立目录（按规则名称命名）；Automation 目录已有文件则跳过。
        /// 迁移后 config.json 中的 AutoRules 键在下次 Save（整体序列化，AppConfig 已无该字段）时自然清除。
        /// </summary>
        internal static void MigrateLegacyIfNeeded()
        {
            try
            {
                if (Directory.Exists(RulesDir)
                    && Directory.GetFiles(RulesDir, "*.json").Length > 0) return;
                var cfgPath = ConfigService.ConfigPath;
                if (!File.Exists(cfgPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                if (!doc.RootElement.TryGetProperty("AutoRules", out var arr)
                    || arr.ValueKind != JsonValueKind.Array) return;
                var legacy = arr.Deserialize<List<AutoRule>>();
                if (legacy == null || legacy.Count == 0) return;
                Directory.CreateDirectory(RulesDir);
                var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(RulesDir, "*.json"))
                    occupied.Add(Path.GetFileNameWithoutExtension(f));
                foreach (var r in legacy)
                {
                    if (r == null || string.IsNullOrWhiteSpace(r.Id)) continue;
                    var baseName = SanitizeFileName(r.Name);
                    var fileName = baseName + ".json";
                    int n = 1;
                    while (occupied.Contains(Path.GetFileNameWithoutExtension(fileName)))
                        fileName = $"{baseName}({n++}).json";
                    occupied.Add(Path.GetFileNameWithoutExtension(fileName));
                    AtomicFile.WriteAllText(
                        Path.Combine(RulesDir, fileName),
                        JsonSerializer.Serialize(r, SaveOptions));
                }
                Invalidate();
            }
            catch
            {
                // 迁移失败静默：不阻断启动，用户下次保存规则时重新写入独立目录
            }
        }
    }
}
