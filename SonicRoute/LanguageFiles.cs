using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SonicRoute
{
    // 文件操作不接触本地化缓存；显式目录也便于在独立临时目录验证。
    internal static class LanguageFiles
    {
        internal static Dictionary<string, string>? Read(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(json
#if NET10_0_OR_GREATER
                , LanguageJsonContext.Default.DictionaryStringString
#endif
                );

        internal static Dictionary<string, string>? Read(Stream stream) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(stream
#if NET10_0_OR_GREATER
                , LanguageJsonContext.Default.DictionaryStringString
#endif
                );

        internal static string Serialize(Dictionary<string, string> data) => JsonSerializer.Serialize(data, ExportJson.Options);

        // Export/rename alone create the writer options; normal reads need no writer cache.
        private static class ExportJson
        {
            internal static readonly JsonSerializerOptions Options = new()
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
#if NET10_0_OR_GREATER
                TypeInfoResolver = LanguageJsonContext.Default,
#endif
            };
        }

        internal static bool IsValidCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 32 || code == "." || code == ".."
                || code.EndsWith(".", StringComparison.Ordinal) || code.EndsWith(" ", StringComparison.Ordinal)
                || code.IndexOf('#') >= 0 || code.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            var stem = code.Split('.')[0].ToUpperInvariant();
            return stem != "CON" && stem != "PRN" && stem != "AUX" && stem != "NUL"
                && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                     && stem[3] >= '1' && stem[3] <= '9');
        }

        internal static void WriteAtomic(string destination, string json)
        {
            var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static (bool Ok, string Code, int CoveragePct) Import(string file, string directory,
            HashSet<string> keys, IEnumerable<string> builtinCodes)
        {
            try
            {
                var json = File.ReadAllText(file);
                var data = Read(json);
                if (data == null || data.Values.Any(value => value == null)) return (false, "", 0);
                var code = data.TryGetValue("Lang.Code", out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value.Trim() : Path.GetFileNameWithoutExtension(file);
                if (!IsValidCode(code)) return (false, "", 0);
                code = builtinCodes.FirstOrDefault(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) ?? code;
                var coverage = keys.Count == 0 ? 100 : (int)Math.Round(data.Keys.Count(keys.Contains) * 100.0 / keys.Count);
                Directory.CreateDirectory(directory);
                // 写入已校验的快照，源文件随后被修改也不会绕过校验。
                WriteAtomic(Path.Combine(directory, code + ".json"), json);
                var additional = data.TryGetValue("Lang.Custom", out var custom) && !string.IsNullOrWhiteSpace(custom);
                var selected = additional && builtinCodes.Contains(code, StringComparer.OrdinalIgnoreCase) ? code + "#" : code;
                return (true, selected, coverage);
            }
            catch { return (false, "", 0); }
        }

        internal static (bool Ok, int Restored) Restore(string directory, IEnumerable<string> builtinCodes)
        {
            int restored = 0;
            bool ok = true;
            foreach (var code in builtinCodes)
            {
                var path = Path.Combine(directory, code + ".json");
                if (!File.Exists(path)) continue;
                try
                {
                    // 格式损坏的覆盖文件也可还原；有效附加语言仍保留。
                    Dictionary<string, string>? data = null;
                    try { data = Read(File.ReadAllText(path)); }
                    catch (JsonException) { }
                    if (data != null && data.TryGetValue("Lang.Custom", out var custom) && !string.IsNullOrWhiteSpace(custom)) continue;
                    File.Delete(path);
                    ++restored;
                }
                catch { ok = false; } // 继续处理其他语言，准确返回已还原数量。
            }
            return (ok, restored);
        }
    }
}
