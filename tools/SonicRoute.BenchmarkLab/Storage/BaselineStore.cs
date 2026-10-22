using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SonicRoute.BenchmarkLab.Core;
using SonicRoute.BenchmarkLab.Scenarios;

namespace SonicRoute.BenchmarkLab.Storage
{
    public sealed class StoredBaseline
    {
        public string Name { get; set; } = string.Empty;
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
        public string TargetPath { get; set; } = string.Empty;
        public string ExeSha256 { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public SystemEnvironmentInfo Environment { get; set; } = new();
        public Dictionary<string, ProcessMetricSummary> Scenarios { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public static class BaselineStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string DefaultStoreDirectory
        {
            get
            {
                string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "baselines");
                Directory.CreateDirectory(baseDir);
                return baseDir;
            }
        }

        public static void Save(string name, StoredBaseline baseline, string? customDir = null)
        {
            string dir = customDir ?? DefaultStoreDirectory;
            Directory.CreateDirectory(dir);
            string safeName = Path.GetInvalidFileNameChars().Aggregate(name, (curr, c) => curr.Replace(c, '_'));
            string path = Path.Combine(dir, $"{safeName}.json");

            string json = JsonSerializer.Serialize(baseline, JsonOptions);
            File.WriteAllText(path, json);
        }

        public static StoredBaseline? Load(string name, string? customDir = null)
        {
            // If the name is an executable file path, it's not a stored JSON baseline
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string dir = customDir ?? DefaultStoreDirectory;
            string safeName = Path.GetInvalidFileNameChars().Aggregate(name, (curr, c) => curr.Replace(c, '_'));
            string path = Path.Combine(dir, $"{safeName}.json");

            if (!File.Exists(path))
            {
                // Try direct path if user supplied a json file path
                if (File.Exists(name) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    path = name;
                }
                else
                {
                    return null;
                }
            }

            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<StoredBaseline>(json);
            }
            catch
            {
                return null;
            }
        }

        public static IReadOnlyList<StoredBaseline> ListAll(string? customDir = null)
        {
            string dir = customDir ?? DefaultStoreDirectory;
            var list = new List<StoredBaseline>();
            if (!Directory.Exists(dir)) return list;

            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<StoredBaseline>(json);
                    if (item != null) list.Add(item);
                }
                catch { }
            }

            return list;
        }

        public static bool Delete(string name, string? customDir = null)
        {
            string dir = customDir ?? DefaultStoreDirectory;
            string safeName = Path.GetInvalidFileNameChars().Aggregate(name, (curr, c) => curr.Replace(c, '_'));
            string path = Path.Combine(dir, $"{safeName}.json");

            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
            return false;
        }
    }
}
