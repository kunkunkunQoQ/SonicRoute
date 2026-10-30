using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace SonicRoute.Core
{
    /// <summary>受管的小图文件。只有用户导入、恢复、导出配置时才写盘或清理。</summary>
    public static class AppIconFiles
    {
        public const int IconSize = 64;
        public const int MaxIconBytes = 64 * 1024;
        public const int MaxIcons = 256;
        public const int MaxTransferBytes = 16 * 1024 * 1024;
        private static string DirectoryPath => Path.Combine(AppDataRoot.DirectoryPath, "Icons");

        public static bool IsFileName(string? name)
        {
            if (name == null || name.Length != 68 || !name.EndsWith(".png", StringComparison.Ordinal)) return false;
            for (int i = 0; i < 64; i++)
                if (!(name[i] >= '0' && name[i] <= '9') && !(name[i] >= 'a' && name[i] <= 'f')) return false;
            return true;
        }

        public static Dictionary<string, string> NormalizeMappings(IDictionary<string, string>? values)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (values != null)
                foreach (var pair in values)
                    if (!string.IsNullOrWhiteSpace(pair.Key) && IsFileName(pair.Value))
                    {
                        if (result.Count >= MaxIcons && !result.ContainsKey(pair.Key)) break;
                        result[pair.Key] = pair.Value;
                    }
            return result;
        }

        public static string? GetFileName(AppConfig config, string processName) =>
            !string.IsNullOrWhiteSpace(processName) && config.AppIcons != null
                && config.AppIcons.TryGetValue(processName, out var name) && IsFileName(name) ? name : null;

        public static byte[] ReadPng(string name)
        {
            if (!IsFileName(name)) throw new InvalidDataException("Invalid icon name.");
            var bytes = ReadBytes(name);
            ValidatePng(bytes);
            if (NameFor(bytes) != name) throw new InvalidDataException("Icon content does not match its name.");
            return bytes;
        }

        private static byte[] ReadBytes(string name)
        {
            var path = Path.Combine(DirectoryPath, name);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxIconBytes || stream.Length < 33) throw new InvalidDataException("Invalid icon size.");
            var bytes = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
            return bytes;
        }

        public static string SavePng(byte[] bytes) => SavePngIfChanged(bytes, out _);

        public static string SavePngIfChanged(byte[] bytes, out bool changed)
        {
            ValidatePng(bytes);
            string name = NameFor(bytes);
            SaveValidatedPng(name, bytes, out changed);
            return name;
        }

        // Private callers have already validated both the PNG and its content-addressed name.
        private static void SaveValidatedPng(string name, byte[] bytes, out bool changed)
        {
            changed = false;
            // Equality with the validated input also proves the existing file is intact.
            try { if (ReadBytes(name).SequenceEqual(bytes)) return; }
            catch { /* Missing or damaged files are repaired by the normal atomic write. */ }
            Directory.CreateDirectory(DirectoryPath);
            // Also repairs an existing damaged file with the same original content hash.
            AtomicFile.WriteAllBytes(Path.Combine(DirectoryPath, name), bytes);
            changed = true;
        }

        public static void CleanupUnused(AppConfig config)
        {
            try
            {
                if (!Directory.Exists(DirectoryPath)) return;
                var used = new HashSet<string>((config.AppIcons ?? new Dictionary<string, string>()).Values, StringComparer.Ordinal);
                foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.png", SearchOption.TopDirectoryOnly))
                    if (IsFileName(Path.GetFileName(path)) && !used.Contains(Path.GetFileName(path)))
                        try { File.Delete(path); } catch { }
            }
            catch { /* A cleanup failure must not undo a saved setting. */ }
        }

        internal static Dictionary<string, string> ExportAssets(AppConfig config)
        {
            var assets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in NormalizeMappings(config.AppIcons).Values.Distinct(StringComparer.Ordinal))
                assets[name] = Convert.ToBase64String(ReadPng(name));
            return assets;
        }

        internal static void ImportAssets(JsonElement root, AppConfig config)
        {
            foreach (var asset in ReadImportAssets(root, config)) SaveValidatedPng(asset.Key, asset.Value, out _);
        }

        internal static List<KeyValuePair<string, byte[]>> ReadImportAssets(JsonElement root, AppConfig config)
        {
            if (!root.TryGetProperty("AppIconAssets", out var assets)) return new(); // Old JSON backups remain valid.
            if (assets.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid icon assets.");
            var used = new HashSet<string>(config.AppIcons.Values, StringComparer.Ordinal);
            var pending = new List<KeyValuePair<string, byte[]>>();
            int count = 0;
            foreach (var asset in assets.EnumerateObject())
            {
                if (++count > MaxIcons || !IsFileName(asset.Name) || asset.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Invalid icon assets.");
                string value = asset.Value.GetString()!;
                if (value.Length > ((MaxIconBytes + 2) / 3) * 4) throw new InvalidDataException("Icon asset too large.");
                byte[] bytes = Convert.FromBase64String(value);
                ValidatePng(bytes);
                if (NameFor(bytes) != asset.Name) throw new InvalidDataException("Icon content does not match its name.");
                if (used.Contains(asset.Name)) pending.Add(new KeyValuePair<string, byte[]>(asset.Name, bytes));
            }
            return pending; // Validation/preview is pure; files are created only after import confirmation.
        }

        private static string NameFor(byte[] bytes)
        {
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() + ".png";
        }

        private static void ValidatePng(byte[] bytes)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (bytes.Length < 33 || bytes.Length > MaxIconBytes) throw new InvalidDataException("Invalid icon size.");
            for (int i = 0; i < signature.Length; i++)
                if (bytes[i] != signature[i]) throw new InvalidDataException("Invalid PNG icon.");
            if (bytes[8] != 0 || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 13
                || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R'
                || bytes[16] != 0 || bytes[17] != 0 || bytes[18] != 0 || bytes[19] != IconSize
                || bytes[20] != 0 || bytes[21] != 0 || bytes[22] != 0 || bytes[23] != IconSize)
                throw new InvalidDataException("Expected a 64 pixel PNG icon.");
        }
    }
}
