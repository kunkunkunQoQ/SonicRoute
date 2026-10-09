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

        /// <summary>加载单个语言表：外置文件优先，其次嵌入资源；失败返回空表（由索引器回退 zh-CN）。</summary>
        private static Dictionary<string, string> LoadLang(string lang)
        {
            try
            {
                var ext = Path.Combine(ExternalLangDir, lang + ".json");
                if (File.Exists(ext))
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
            return dict;
        }

        private static (string Code, string NativeName)[] BuildSupported()
        {
            var list = new List<(string Code, string NativeName)>(BuiltinLanguages);
            try
            {
                if (Directory.Exists(ExternalLangDir))
                {
                    foreach (var f in Directory.GetFiles(ExternalLangDir, "*.json"))
                    {
                        var code = Path.GetFileNameWithoutExtension(f);
                        if (string.IsNullOrWhiteSpace(code) || list.Any(x => x.Code == code)) continue;
                        var native = code;
                        try
                        {
                            using var fs = File.OpenRead(f);
                            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(fs);
                            if (d != null && d.TryGetValue("Lang.NativeName", out var nn) && !string.IsNullOrWhiteSpace(nn))
                                native = nn;
                        }
                        catch { }
                        list.Add((code, native));
                    }
                }
            }
            catch { }
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

        /// <summary>实验设置-导入语言：从 json 读取语言代码（Lang.Code，缺省用文件名）写入外置目录，重启后生效。</summary>
        public static (bool Ok, string Code) ImportLanguageFile(string file)
        {
            try
            {
                var json = File.ReadAllText(file);
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (d == null) return (false, "");
                var code = d.TryGetValue("Lang.Code", out var c) && !string.IsNullOrWhiteSpace(c)
                    ? c.Trim()
                    : Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(code) || code.Length > 32
                    || code.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (false, "");
                Directory.CreateDirectory(ExternalLangDir);
                File.Copy(file, Path.Combine(ExternalLangDir, code + ".json"), true);
                return (true, code);
            }
            catch { return (false, ""); }
        }

        /// <summary>打开外置语言目录（%LocalAppData%\SonicRoute\Lang，不存在则创建）。</summary>
        public static bool OpenExternalLangDir()
        {
            try
            {
                Directory.CreateDirectory(ExternalLangDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ExternalLangDir,
                    UseShellExecute = true,
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>还原被覆盖的内置语言：删除外置目录中与内置 9 语言同名的文件；自定义语言保留。返回还原数量。</summary>
        public static (bool Ok, int Restored) RestoreBuiltinLanguages()
        {
            try
            {
                if (!Directory.Exists(ExternalLangDir)) return (true, 0);
                var codes = new HashSet<string>(BuiltinLanguages.Select(x => x.Code));
                var n = 0;
                foreach (var f in Directory.GetFiles(ExternalLangDir, "*.json"))
                {
                    var code = Path.GetFileNameWithoutExtension(f);
                    if (codes.Contains(code)) { File.Delete(f); n++; }
                }
                return (true, n);
            }
            catch { return (false, 0); }
        }
    }
}
