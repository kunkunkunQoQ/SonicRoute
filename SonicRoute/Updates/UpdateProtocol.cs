using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SonicRoute.Updates
{
    // The manifest is data, never executable instructions. Transport authentication is GitHub HTTPS;
    // hashes detect corruption/substitution against that metadata, not a compromised publisher account.
    internal sealed class UpdateFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
    }

    internal sealed class UpdatePackage
    {
        public string Edition { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Asset { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public List<UpdateFile> Files { get; set; } = new();
    }

    internal sealed class UpdateManifest
    {
        public int SchemaVersion { get; set; }
        public int ProtocolVersion { get; set; }
        public string Version { get; set; } = "";
        public List<UpdatePackage> Packages { get; set; } = new();
    }

    internal sealed class ReleaseAsset
    {
        internal string Name = "", Url = "", Digest = "";
        internal long Size;
    }

    internal sealed class UpdateRelease
    {
        internal string Tag = "", Notes = "", Url = "";
        internal UpdatePackage? Package;
        internal ReleaseAsset? Asset;
    }

    internal static class UpdateProtocol
    {
        internal const string Repository = "https://github.com/kunkunkunQoQ/SonicRoute";
        internal const string LatestApi = "https://api.github.com/repos/kunkunkunQoQ/SonicRoute/releases/latest";
        internal const string ManifestName = "SonicRoute-update.json";
        internal const long MaxPackageSize = 128 * 1024 * 1024;
        internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

        // Only application-owned flat files can be installed. In particular config.json, Automation,
        // Lang, Themes, Icons, update markers, scripts, shortcuts and arbitrary subdirectories cannot.
        internal static readonly HashSet<string> LegacyFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "SonicRoute.exe", "SonicRoute.exe.config", "SonicRoute.Core.dll", "LICENSE",
            "Microsoft.Bcl.AsyncInterfaces.dll", "System.Buffers.dll", "System.Memory.dll",
            "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Text.Encodings.Web.dll", "System.Text.Json.dll",
            "System.Threading.Tasks.Extensions.dll", "System.ValueTuple.dll"
        };

        internal static Version? ParseVersion(string text)
        {
            var m = Regex.Match(text ?? "", @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?$");
            if (!m.Success) return null;
            var parts = new int[4];
            for (int i = 0; i < parts.Length; i++)
                if (m.Groups[i + 1].Success && !int.TryParse(m.Groups[i + 1].Value, out parts[i])) return null;
            return new Version(parts[0], parts[1], parts[2], parts[3]);
        }

        internal static bool IsHash(string? hash) => hash != null && Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$");
        internal static string Hash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }
        internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        internal static void Verify(string path, string hash, long size)
        {
            if (!IsHash(hash) || new FileInfo(path).Length != size || !Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update checksum mismatch.");
        }

        internal static Dictionary<string, string> ReadChecksums(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                if (line.Length == 0) continue;
                var m = Regex.Match(line, @"^([a-fA-F0-9]{64})  ([A-Za-z0-9._-]+)$");
                if (!m.Success || result.ContainsKey(m.Groups[2].Value)) throw new InvalidDataException("Invalid SHA256SUMS.");
                result.Add(m.Groups[2].Value, m.Groups[1].Value);
            }
            return result;
        }

        internal static void ValidatePackage(UpdatePackage p, string edition, string architecture)
        {
            if (p.Edition != edition || p.Architecture != architecture || (edition != "Lite" && edition != "Legacy")
                || (architecture != "x64" && architecture != "arm64") || (edition == "Legacy" && architecture != "x64")
                || !Regex.IsMatch(p.Asset, @"^[A-Za-z0-9._-]+$") || !IsHash(p.Sha256)
                || p.Size <= 0 || p.Size > MaxPackageSize || p.Files == null || p.Files.Count == 0 || p.Files.Count > 32)
                throw new InvalidDataException("Invalid update package.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in p.Files)
            {
                if (!LegacyFiles.Contains(file.Path) || !names.Add(file.Path) || !IsHash(file.Sha256)
                    || file.Size <= 0 || file.Size > MaxPackageSize) throw new InvalidDataException("Invalid update file.");
                total += file.Size;
            }
            if (total > MaxPackageSize || !names.Contains("SonicRoute.exe")
                || (edition == "Lite" && (p.Files.Count != 1 || !p.Asset.EndsWith(".exe", StringComparison.Ordinal)))
                || (edition == "Legacy" && (!names.Contains("SonicRoute.Core.dll") || !p.Asset.EndsWith(".zip", StringComparison.Ordinal))))
                throw new InvalidDataException("Incomplete update package.");
        }

        internal static void VerifyExecutable(string path, string architecture, string version)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadUInt16() != 0x5a4d || reader.BaseStream.Length < 256) throw new InvalidDataException("Not a PE image.");
            reader.BaseStream.Position = 0x3c;
            int offset = reader.ReadInt32();
            if (offset < 64 || offset > reader.BaseStream.Length - 6) throw new InvalidDataException("Invalid PE header.");
            reader.BaseStream.Position = offset;
            if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != (architecture == "arm64" ? 0xaa64 : 0x8664))
                throw new InvalidDataException("Wrong executable architecture.");
            var info = FileVersionInfo.GetVersionInfo(path);
            if (ParseVersion(info.ProductVersion ?? "") != ParseVersion(version) || ParseVersion(version) == null)
                throw new InvalidDataException("Wrong executable version.");
        }

        internal static void RejectReparsePoints(string path)
        {
            for (string? item = System.IO.Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = System.IO.Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Update paths cannot contain symbolic links or junctions.");
        }

    }
}
