using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SonicRoute
{
    /// <summary>
    /// Multi-language localization.
    /// 语言数据按语言独立存放：嵌入资源 Resources/Lang/*.json（9 语言，内置）；
    /// 支持实验设置「导入语言」加载外置语言文件（%LocalAppData%\SonicRoute\Lang\{code}.json，优先于内置），
    /// 外置文件可含特殊键 Lang.Code / Lang.NativeName（自定义语言显示名）。
    /// XAML 用 {Binding [key], Source={x:Static local:L10n.Instance}}，代码用 L10n.T("key")。
    /// 只加载当前语言 + zh-CN 回退表；语言更改/导入在下次启动生效。
    /// </summary>
    public sealed class L10n : INotifyPropertyChanged
    {
        public static L10n Instance { get; } = new();
        public static string CurrentLanguage { get; private set; } = "zh-CN";

        private static readonly (string Code, string NativeName)[] BuiltinLanguages =
        {
            ("zh-CN", "中文"), ("zh-TW", "繁體中文"), ("en-US", "English"), ("ja-JP", "日本語"),
            ("ko-KR", "한국어"), ("fr-FR", "Français"), ("de-DE", "Deutsch"), ("es-ES", "Español"),
            ("ru-RU", "Русский"),
        };

        /// <summary>支持的 语言代码 → 原生名称：内置 9 语言 + 外置语言目录中的自定义语言。</summary>
        private static (string Code, string NativeName)[]? _supported;
        public static (string Code, string NativeName)[] SupportedLanguages => _supported ??= BuildSupported();

        public string this[string key] =>
            Tables.TryGetValue(CurrentLanguage, out var t) && t.TryGetValue(key, out var v)
                ? v
                : (Tables["zh-CN"].TryGetValue(key, out var zh) ? zh : key);

        public void SetLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) lang = "zh-CN";
            // 未知 code（如附加语言文件被删 / 配置残留 / 换机器）回退 zh-CN
            if (!SupportedLanguages.Any(x => x.Code == lang)) lang = "zh-CN";
            if (CurrentLanguage == lang) return;
            CurrentLanguage = lang;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static string T(string key) => Instance[key];

        private static Dictionary<string, Dictionary<string, string>>? _tables;
        private static Dictionary<string, Dictionary<string, string>> Tables =>
            _tables ??= BuildTables();

        private static Dictionary<string, Dictionary<string, string>> BuildTables()
        {
            var t = new Dictionary<string, Dictionary<string, string>>();
            var cur = CurrentLanguage;
            t[cur] = LoadLang(cur);
            if (cur != "zh-CN") t["zh-CN"] = LoadLang("zh-CN");
            return t;
        }

        /// <summary>外置语言目录：%LocalAppData%\SonicRoute\Lang（导入语言文件存放处，优先于内置）。</summary>
        public static string ExternalLangDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonicRoute", "Lang");

        // 外置语言统一索引（v1.15 起）：一次扫描构建，BuildSupported / LoadLang / ListCustomLanguages /
        // RestoreBuiltinLanguages 共用，避免对同一目录重复 GetFiles + 反序列化。
        // 附加语言（带 Lang.Custom 非空）语义：Lang.Custom 存储自定义语言名称，有值 → 直接应用该名称
        // （优先级高于 Lang.NativeName）、不显示在自定义语言 UI、且不管文件名是什么都不替换原本的 9 个
        // 内置语言（作为独立附加语言加入下拉框，文件名撞内置 code 时用内部码 code# 区分）。
        private sealed class LangFileEntry
        {
            public required string FileCode { get; init; }   // 文件名 code（实际文件）
            public required string Native { get; init; }     // 显示名：NativeName（附加语言 = Lang.Custom 值）
            public string? CustomName { get; init; }         // 非 null = 附加语言（Lang.Custom 非空）
        }

        private static bool _extIndexBuilt;
        private static readonly List<LangFileEntry> _extEntries = new();              // 外置目录全部语言
        private static readonly Dictionary<string, string> _customLangFileMap = new(); // 附加语言：内部码 → 文件 code
        private static readonly HashSet<string> _customLangCodes = new();              // 附加语言：文件 code（含内置名）
        private static readonly HashSet<string> _builtinCodes = new(
            BuiltinLanguages.Select(x => x.Code), StringComparer.OrdinalIgnoreCase);  // 内置 code（忽略大小写）

        /// <summary>惰性扫描外置目录一次，构建统一索引（附加语言 + 普通自定义/覆盖语言）。</summary>
        private static void EnsureExtIndex()
        {
            if (_extIndexBuilt) return;
            _extIndexBuilt = true;
            _extEntries.Clear();
            _customLangFileMap.Clear();
            _customLangCodes.Clear();
            try
            {
                if (!Directory.Exists(ExternalLangDir)) return;
                foreach (var f in Directory.GetFiles(ExternalLangDir, "*.json"))
                {
                    var code = Path.GetFileNameWithoutExtension(f);
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    try
                    {
                        using var fs = File.OpenRead(f);
                        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(fs);
                        if (d == null) continue;
                        string native = code;
                        if (d.TryGetValue("Lang.NativeName", out var nn) && !string.IsNullOrWhiteSpace(nn))
                            native = nn.Trim();
                        string? custom = null;
                        if (d.TryGetValue("Lang.Custom", out var ct) && !string.IsNullOrWhiteSpace(ct?.Trim()))
                            custom = ct.Trim();
                        _extEntries.Add(new LangFileEntry { FileCode = code, Native = native, CustomName = custom });
                        if (custom != null)
                        {
                            var addCode = _builtinCodes.Contains(code) ? code + "#" : code;
                            _customLangFileMap[addCode] = code;
                            _customLangCodes.Add(code);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>加载单个语言表：外置文件优先（带 Lang.Custom 的外置文件不覆盖内置语言，仅作附加语言），其次嵌入资源；失败返回空表（由索引器回退 zh-CN）。</summary>
        private static Dictionary<string, string> LoadLang(string lang)
        {
            try
            {
                EnsureExtIndex();
                // 附加语言（带 Lang.Custom 非空）：走内部码映射，加载其自身文件内容
                if (_customLangFileMap.TryGetValue(lang, out var fc))
                {
                    var p = Path.Combine(ExternalLangDir, fc + ".json");
                    if (File.Exists(p))
                        using (var fs = File.OpenRead(p))
                            return ParseLangJson(fs);
                    return new Dictionary<string, string>();
                }
                var ext = Path.Combine(ExternalLangDir, lang + ".json");
                // 带 Lang.Custom 的同名外置文件不覆盖内置语言内容（仅作为附加语言存在于下拉框）
                if (File.Exists(ext) && !_customLangCodes.Contains(lang))
                    using (var fs = File.OpenRead(ext))
                        return ParseLangJson(fs);
                var resName = "SonicRoute.Resources.Lang." + lang + ".json";
                using (var rs = typeof(L10n).Assembly.GetManifestResourceStream(resName))
                {
                    if (rs != null) return ParseLangJson(rs);
                }
            }
            catch { }
            return new Dictionary<string, string>();
        }

        private static Dictionary<string, string> ParseLangJson(Stream s)
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s)
                       ?? new Dictionary<string, string>();
            dict.Remove("Lang.Code");
            dict.Remove("Lang.NativeName");
            dict.Remove("Lang.Custom");
            return dict;
        }

        private static (string Code, string NativeName)[] BuildSupported()
        {
            EnsureExtIndex();
            var list = new List<(string Code, string NativeName)>(BuiltinLanguages);
            foreach (var e in _extEntries)
            {
                if (e.CustomName != null)
                {
                    // 附加语言：不替换内置 9 语言，显示名 = Lang.Custom（优先级高），文件名撞内置 code 用 code# 区分
                    var addCode = _builtinCodes.Contains(e.FileCode) ? e.FileCode + "#" : e.FileCode;
                    list.Add((addCode, e.CustomName));
                    continue;
                }
                if (_builtinCodes.Contains(e.FileCode)) continue; // 覆盖内置：列表保持内置项（内容加载时外置优先）
                list.Add((e.FileCode, e.Native));
            }
            return list.ToArray();
        }

        /// <summary>实验设置-导出语言：把内置 9 语言 json 写入目标文件夹（含 Lang.Code/Lang.NativeName 特殊键，可编辑后导入）。</summary>
        public static bool ExportBuiltinLanguages(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var asm = typeof(L10n).Assembly;
                foreach (var (code, native) in BuiltinLanguages)
                {
                    var resName = "SonicRoute.Resources.Lang." + code + ".json";
                    using var rs = asm.GetManifestResourceStream(resName);
                    if (rs == null) return false;
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(rs)
                            ?? new Dictionary<string, string>();
                    d["Lang.Code"] = code;
                    d["Lang.NativeName"] = native;
                    File.WriteAllText(Path.Combine(dir, code + ".json"),
                        JsonSerializer.Serialize(d, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        }),
                        new System.Text.UTF8Encoding(false));
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>内置 zh-CN 键集（用于导入语言时的键覆盖率校验，缓存一次）。</summary>
        private static HashSet<string>? _zhKeys;
        private static HashSet<string> ZhKeys => _zhKeys ??= LoadZhKeys();

        private static HashSet<string> LoadZhKeys()
        {
            var set = new HashSet<string>();
            try
            {
                const string resName = "SonicRoute.Resources.Lang.zh-CN.json";
                using var rs = typeof(L10n).Assembly.GetManifestResourceStream(resName);
                if (rs != null)
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(rs);
                    if (d != null)
                    {
                        // 与 ParseLangJson 一致剔除特殊键，避免覆盖率被 Lang.Code/NativeName/Custom 虚增
                        d.Remove("Lang.Code");
                        d.Remove("Lang.NativeName");
                        d.Remove("Lang.Custom");
                        foreach (var k in d.Keys) set.Add(k);
                    }
                }
            }
            catch { }
            return set;
        }

        /// <summary>实验设置-导入语言：从 json 读取语言代码（Lang.Code，缺省用文件名）写入外置目录。
        /// 原子写入：先写临时文件（.json.tmp，不匹配 *.json 扫描）再 Move 覆盖，目标要么旧要么新，不会出现半成品。
        /// 返回键覆盖率 CoveragePct（导入文件键数 / 内置 zh-CN 键数，用于缺失键警告）。</summary>
        public static (bool Ok, string Code, int CoveragePct) ImportLanguageFile(string file)
        {
            try
            {
                var json = File.ReadAllText(file);
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (d == null) return (false, "", 0);
                var code = d.TryGetValue("Lang.Code", out var c) && !string.IsNullOrWhiteSpace(c)
                    ? c.Trim()
                    : Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(code) || code.Length > 32
                    || code.IndexOf('#') >= 0 // '#' 保留给附加语言内部码专用，禁止作为语言 code
                    || code.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (false, "", 0);
                int cov = 100;
                if (ZhKeys.Count > 0)
                {
                    var have = 0;
                    foreach (var k in d.Keys) if (ZhKeys.Contains(k)) have++;
                    cov = (int)Math.Round(have * 100.0 / ZhKeys.Count);
                }
                Directory.CreateDirectory(ExternalLangDir);
                var dest = Path.Combine(ExternalLangDir, code + ".json");
                var tmp = Path.Combine(ExternalLangDir, code + ".json.tmp");
                try
                {
                    File.Copy(file, tmp, true);
                    // net48 无 File.Move(src, dst, overwrite) 三参重载（.NET Core 3.0+）。
                    // 目标已存在 → File.Replace（原子覆盖替换，语义与原实现一致）；不存在 → File.Move。
                    if (File.Exists(dest))
                        File.Replace(tmp, dest, null);
                    else
                        File.Move(tmp, dest);
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return (false, "", 0);
                }
                return (true, code, cov);
            }
            catch { return (false, "", 0); }
        }

        /// <summary>导入语言后即时生效（无需重启）：重建外置语言索引、语言列表与语言表缓存并切到目标语言，触发全量绑定刷新。</summary>
        public void ApplyImportedLanguage(string code)
        {
            _supported = null;
            _tables = null;
            _extIndexBuilt = false; // 统一索引随缓存一起重建
            if (!string.IsNullOrWhiteSpace(code)) CurrentLanguage = code;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }

        /// <summary>外置目录中的自定义语言（非内置 9 语言，且不带 Lang.Custom）列表：(code, nativeName)。
        /// 带非空 Lang.Custom 的语言视为"附加语言"：直接出现在语言下拉框（显示名 = Lang.Custom），不显示在自定义语言 UI。</summary>
        public static (string Code, string NativeName)[] ListCustomLanguages()
        {
            EnsureExtIndex();
            var list = new List<(string, string)>();
            foreach (var e in _extEntries)
            {
                if (_builtinCodes.Contains(e.FileCode)) continue;
                if (e.CustomName != null) continue; // 附加语言：直接入下拉框，不进自定义 UI
                list.Add((e.FileCode, e.Native));
            }
            return list.ToArray();
        }

        /// <summary>重命名自定义语言显示名：写回 Lang.NativeName 并重建语言列表缓存。</summary>
        public static bool RenameCustomLanguage(string code, string newName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newName)) return false;
                var path = Path.Combine(ExternalLangDir, code + ".json");
                if (!File.Exists(path)) return false;
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                        ?? new Dictionary<string, string>();
                d["Lang.NativeName"] = newName.Trim();
                File.WriteAllText(path,
                    JsonSerializer.Serialize(d, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }),
                    new System.Text.UTF8Encoding(false));
                _supported = null;
                _extIndexBuilt = false; // 索引缓存了旧显示名，需重建
                return true;
            }
            catch { return false; }
        }

        /// <summary>删除自定义语言文件（内置语言不允许删除）；若删除的是当前语言则切回 zh-CN 并刷新界面。</summary>
        public static bool DeleteCustomLanguage(string code)
        {
            try
            {
                if (_builtinCodes.Contains(code)) return false;
                var path = Path.Combine(ExternalLangDir, code + ".json");
                if (!File.Exists(path)) return false;
                File.Delete(path);
                _supported = null;
                _tables = null;
                _extIndexBuilt = false;
                if (string.Equals(CurrentLanguage, code, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentLanguage = "zh-CN";
                    Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>打开外置语言目录（%LocalAppData%\SonicRoute\Lang，不存在则创建）。与自动化脚本文件夹共用 ShellOpen.Folder。</summary>
        public static bool OpenExternalLangDir() => Core.ShellOpen.Folder(ExternalLangDir);

        /// <summary>还原被覆盖的内置语言：删除外置目录中与内置 9 语言同名的文件（附加语言带 Lang.Custom 的除外）；自定义语言保留。返回还原数量。</summary>
        public static (bool Ok, int Restored) RestoreBuiltinLanguages()
        {
            try
            {
                if (!Directory.Exists(ExternalLangDir)) return (true, 0);
                EnsureExtIndex();
                var n = 0;
                foreach (var e in _extEntries)
                {
                    if (_builtinCodes.Contains(e.FileCode) && e.CustomName == null)
                    {
                        File.Delete(Path.Combine(ExternalLangDir, e.FileCode + ".json"));
                        n++;
                    }
                }
                if (n > 0)
                {
                    _supported = null;
                    _tables = null;
                    _extIndexBuilt = false;
                }
                return (true, n);
            }
            catch { return (false, 0); }
        }
    }
}
