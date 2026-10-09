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
    /// - 内存缓存 + 失效：Save / Delete 后失效，下次 LoadAll 重建；规则数量少，无性能问题。
    /// - 旧版本 config.json 中的 AutoRules 字段在首次加载时自动迁移到该目录（同样按名称命名）。
    /// </summary>
    public static class AutoRuleStore
    {
        public static string RulesDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonicRoute",
            "Automation");

        private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

        private static List<AutoRule>? _cache;
        private static readonly object _lock = new();

        /// <summary>加载全部规则（带缓存；返回副本，外部修改不影响缓存）。</summary>
        public static List<AutoRule> LoadAll()
        {
            lock (_lock)
            {
                if (_cache == null)
                {
                    var list = new List<AutoRule>();
                    try
                    {
                        if (Directory.Exists(RulesDir))
                        {
                            foreach (var f in Directory.GetFiles(RulesDir, "*.json")
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var r = JsonSerializer.Deserialize<AutoRule>(File.ReadAllText(f));
                                    if (r != null) list.Add(r);
                                }
                                catch
                                {
                                    // 单个文件损坏跳过，不影响其余规则
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 目录不可读时按空列表处理
                    }
                    _cache = list;
                }
                return new List<AutoRule>(_cache);
            }
        }

        /// <summary>按 Id 查规则。</summary>
        public static AutoRule? Find(string id) =>
            LoadAll().FirstOrDefault(r => r.Id == id);

        /// <summary>保存（新增 / 更新）一条规则：以规则名称命名文件，重名追加 (1)(2)…，并失效缓存。</summary>
        public static void Save(AutoRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Id)) return;
            Directory.CreateDirectory(RulesDir);

            // 收集当前占用的文件名（不含扩展名），排除本规则自己的旧文件（旧 Id 命名或旧名称命名）
            var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? selfFile = null;
            try
            {
                if (Directory.Exists(RulesDir))
                {
                    foreach (var f in Directory.GetFiles(RulesDir, "*.json"))
                    {
                        try
                        {
                            var r = JsonSerializer.Deserialize<AutoRule>(File.ReadAllText(f));
                            if (r != null && r.Id == rule.Id)
                            {
                                selfFile = f;
                                continue;
                            }
                        }
                        catch
                        {
                            // 损坏文件按占用处理，避免覆盖
                        }
                        occupied.Add(Path.GetFileNameWithoutExtension(f));
                    }
                }
            }
            catch
            {
                // 目录不可读时忽略占用检测
            }

            // 计算新文件名：优先规则名称，重名则追加 (1)(2)…
            var baseName = SanitizeFileName(rule.Name);
            var fileName = baseName + ".json";
            int n = 1;
            while (occupied.Contains(Path.GetFileNameWithoutExtension(fileName)))
                fileName = $"{baseName}({n++}).json";

            var target = Path.Combine(RulesDir, fileName);
            // 旧文件与新文件名不同（如旧 Id 命名 / 改过名称）时删除旧文件，避免重复加载同一条规则
            if (selfFile != null
                && !string.Equals(Path.GetFullPath(selfFile), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(selfFile); } catch { }
            }
            File.WriteAllText(target, JsonSerializer.Serialize(rule, SaveOptions));
            Invalidate();
        }

        /// <summary>删除一条规则：按 Id 定位文件删除并失效缓存。</summary>
        public static void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            try
            {
                if (Directory.Exists(RulesDir))
                {
                    foreach (var f in Directory.GetFiles(RulesDir, "*.json"))
                    {
                        try
                        {
                            var r = JsonSerializer.Deserialize<AutoRule>(File.ReadAllText(f));
                            if (r != null && r.Id == id)
                            {
                                File.Delete(f);
                                break;
                            }
                        }
                        catch
                        {
                            // 损坏文件无法匹配 Id，跳过
                        }
                    }
                }
            }
            catch
            {
                // 删除失败静默（下次加载仍可读到该规则）
            }
            Invalidate();
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
                    File.WriteAllText(
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
